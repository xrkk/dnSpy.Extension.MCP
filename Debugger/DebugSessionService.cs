using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet;
using dnSpy.Contracts.Debugger.Steppers;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Metadata;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// IMP-005 Windows-side wiring: the single MEF service that routes the session-scoped debug
/// tools onto the coordinator state machine and the live dnSpy debugger objects. The Start
/// call is the only operation that crosses the WPF UI dispatcher (CON-DYN-003 narrow
/// callback); every other object operation is posted through DbgManager.Dispatcher, and the
/// process observations flow back on the dispatcher thread into the thread-safe coordinator.
/// </summary>
[Export(typeof(DebugSessionService))]
public sealed class DebugSessionService : IDisposable {
	readonly DbgManager? dbgManager;
	readonly DebugGateService gateService;

	readonly DebugSessionCoordinator coordinator = new();
	readonly DualLaneQueue laneQueue = new();
	readonly SemaphoreSlim waitSlots = new(8, 8);

	readonly object sessionLock = new();

	// ---- DNMCP_TEST spy counters (in-proc injection surface, increment 1) ----
	// Compiled unconditionally (an Interlocked per event is negligible), exposed ONLY through
	// the debug_test_spy tool which is gated on DNMCP_TEST=1 and answers CAPABILITY_UNAVAILABLE
	// otherwise. These are the in-process counters the ACC fixtures assert deltas on.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> SpyCounters = new();
	static void SpyInc(string name) => SpyCounters.AddOrUpdate(name, 1, (_, v) => v + 1);
	public static bool TestModeEnabled => Environment.GetEnvironmentVariable("DNMCP_TEST") == "1";

	// Injection surface increment 2: virtual clock offset (ms) and the installed fake control
	// adapter. Both live only for the DNMCP_TEST diagnostic tools; production paths never
	// touch the offset and never see the fake once uninstalled.
	static long testClockOffsetMs;
	public static void TestClockAdvance(long ms) => System.Threading.Interlocked.Add(ref testClockOffsetMs, ms);
	public static long TestClockVirtualElapsedMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency + System.Threading.Interlocked.Read(ref testClockOffsetMs);
	FakeDbgProcessControlAdapter? testAdapter;
	public static IReadOnlyDictionary<string, long> SpySnapshot() =>
		(IReadOnlyDictionary<string, long>)SpyCounters.ToDictionary(kv => kv.Key, kv => kv.Value);
	public static void SpyReset() => SpyCounters.Clear();
	DbgProcess? ownedProcess;
	DbgProcessControlAdapter? adapter;
	LaunchPlan? activePlan;
	List<FileIdentityDto> launchIdentities = new();
	readonly List<FileStream> identityLeases = new();
	string launchArchitecture = Architectures.X64;
	bool pendingClaimStartsPaused;
	string? pendingClaimReason;
	DateTime claimDeadlineUtc;
	TaskCompletionSource<bool>? launchClaimTcs;
	TaskCompletionSource<string>? controlOutcomeTcs; // "paused" | "removed" | "removed-pending-restart"
	volatile bool continueInFlight;
	DateTime sessionStartedUtc;

	static readonly JsonSerializerOptions CanonicalOptions = new() {
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>The session tools this service can answer; advertisement is filtered to this set.</summary>
	public static readonly IReadOnlyList<string> HandledTools = new[] {
		"debug_status", "debug_launch", "debug_pause", "debug_continue", "debug_terminate",
		"debug_restart", "debug_read_events", "debug_wait_event",
		"debug_set_breakpoint", "debug_list_breakpoints", "debug_set_breakpoint_enabled",
		"debug_remove_breakpoint", "debug_set_exception_policy",
		"debug_list_threads", "debug_get_stack", "debug_step",
		"debug_get_locals", "debug_expand_value",
		"debug_list_modules", "debug_read_memory", "debug_dump_module",
	};

	public bool Handles(string toolName) => HandledTools.Contains(toolName);

	readonly McpSettings settings;
	readonly DbgCodeBreakpointsService? breakpointsService;
	readonly DbgDotNetCodeLocationFactory? locationFactory;
	readonly dnSpy.Contracts.Debugger.Evaluation.DbgLanguageService? languageService;
	DebugBreakpointStore bpStore = new();
	readonly Dictionary<int, string> mcpIdByDnSpyBreakpoint = new();
	readonly Dictionary<string, int> dnSpyIdByMcpBreakpoint = new();
	readonly Dictionary<string, List<DbgCodeBreakpoint>> dnSpyBreakpointsByMcp = new();
	readonly Dictionary<string, RegisteredModuleRecord> modulesByHandle = new();
	string exceptionPolicy = "unhandled";

	sealed class RegisteredModuleRecord {
		public string ModuleHandle = string.Empty;
		public string RuntimeHandle = string.Empty;
		public string Mvid = string.Empty;
		public string? Sha256;
		public string Filename = string.Empty;
		public string Name = string.Empty;
		public ulong Address;
		public uint Size;
		public string Layout = "file";
		public ModuleId UpstreamId;
	}

	[ImportingConstructor]
	public DebugSessionService([Import(AllowDefault = true)] DbgManager? dbgManager,
		[Import(AllowDefault = true)] DbgCodeBreakpointsService? breakpointsService,
		[Import(AllowDefault = true)] DbgDotNetCodeLocationFactory? locationFactory,
		[Import(AllowDefault = true)] dnSpy.Contracts.Debugger.Evaluation.DbgLanguageService? languageService,
		DebugGateService gateService,
		McpSettings settings) {
		this.dbgManager = dbgManager;
		this.breakpointsService = breakpointsService;
		this.locationFactory = locationFactory;
		this.languageService = languageService;
		this.gateService = gateService;
		this.settings = settings;
		if (dbgManager is not null) {
			dbgManager.ProcessesChanged += OnProcessesChanged;
			dbgManager.IsDebuggingChanged += OnIsDebuggingChanged;
		}
	}

	public void Dispose() {
		if (dbgManager is not null) {
			dbgManager.ProcessesChanged -= OnProcessesChanged;
			dbgManager.IsDebuggingChanged -= OnIsDebuggingChanged;
		}
		ReleaseLeases();
		laneQueue.Dispose();
		waitSlots.Dispose();
	}

	// ---- dispatch ----

	/// <summary>
	/// DNMCP_TEST-only spy surface: counter snapshot/reset for the ACC fixtures. Outside test
	/// mode it answers the fixed CAPABILITY_UNAVAILABLE envelope with zero side effects.
	/// </summary>
	string TestSpy(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var reset = args is not null && args.TryGetValue("reset", out var r) && r is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
		if (reset)
			SpyReset();
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true,
			["counters"] = SpySnapshot(),
		});
	}

	/// <summary>DNMCP_TEST-only: advance/reset the virtual clock used by control deadlines.</summary>
	string TestClock(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var advance = args is not null && args.TryGetValue("advance_ms", out var a) && a is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je
			? (long)je.GetDouble() : 0;
		var resetClock = args is not null && args.TryGetValue("reset", out var rEl) && rEl is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
		if (resetClock)
			System.Threading.Interlocked.Exchange(ref testClockOffsetMs, 0);
		if (advance != 0)
			TestClockAdvance(advance);
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true,
			["virtual_elapsed_ms"] = TestClockVirtualElapsedMs(),
			["clock_offset_ms"] = System.Threading.Interlocked.Read(ref testClockOffsetMs),
		});
	}

	/// <summary>
	/// DNMCP_TEST-only: install/uninstall the scriptable fake control adapter (post-failure and
	/// synthetic paused/removed observations with classified BreakInfos), and emit observations.
	/// </summary>
	string TestAdapter(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		args ??= new Dictionary<string, object>();
		// emit: synthetic observation through the same OnAdapterObservation path as production.
		if (args.TryGetValue("emit", out var emitEl) && emitEl is System.Text.Json.JsonElement em && em.ValueKind == System.Text.Json.JsonValueKind.Object) {
			FakeDbgProcessControlAdapter source;
			lock (sessionLock) {
				if (testAdapter is null) {
					testAdapter = new FakeDbgProcessControlAdapter();
					testAdapter.Observation += OnAdapterObservation;
				}
				source = testAdapter;
			}
			int pid; DateTime started;
			lock (sessionLock) { pid = ownedProcess?.Id ?? 0; started = sessionStartedUtc == default ? DateTime.UtcNow : sessionStartedUtc; }
			if (em.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == System.Text.Json.JsonValueKind.Number)
				pid = (int)pidEl.GetDouble();
			var kind = em.TryGetProperty("kind", out var kEl) ? kEl.GetString() : "paused";
			var infos = new List<BreakInfoObservation>();
			if (em.TryGetProperty("break_infos", out var biEl) && biEl.ValueKind == System.Text.Json.JsonValueKind.Array) {
				int ordinal = 0;
				foreach (var bi in biEl.EnumerateArray()) {
					if (bi.ValueKind != System.Text.Json.JsonValueKind.Object)
						continue;
					var type = bi.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "other" : "other";
					var ord = bi.TryGetProperty("ordinal", out var oEl) && oEl.ValueKind == System.Text.Json.JsonValueKind.Number ? (int)oEl.GetDouble() : ordinal;
					string? ownedId = bi.TryGetProperty("owned_breakpoint_id", out var obEl) && obEl.ValueKind == System.Text.Json.JsonValueKind.String ? obEl.GetString() : null;
					string? stepId = bi.TryGetProperty("step_id", out var stEl) && stEl.ValueKind == System.Text.Json.JsonValueKind.String ? stEl.GetString() : null;
					string? stepKind = bi.TryGetProperty("step_kind", out var skEl) && skEl.ValueKind == System.Text.Json.JsonValueKind.String ? skEl.GetString() : null;
					bool policy = bi.TryGetProperty("policy_requested_pause", out var pEl) && pEl.ValueKind == System.Text.Json.JsonValueKind.True;
					infos.Add(new BreakInfoObservation(type, ord, ownedId, stepId, policy, stepKind));
					ordinal++;
				}
			}
			if (kind == "removed") {
				var exit = em.TryGetProperty("exit_code", out var eEl) && eEl.ValueKind == System.Text.Json.JsonValueKind.Number ? (int)eEl.GetDouble() : 0;
				source.EmitRemoved(pid, started, exit);
			}
			else if (em.TryGetProperty("no_pause", out var npEl) && npEl.ValueKind == System.Text.Json.JsonValueKind.True) {
				// Policy-filtered exceptions (ACC-012): the exception event is written but the
				// process is not stopped — no paused observation, no state transition.
				bool firstChance = em.TryGetProperty("first_chance", out var fcEl) && fcEl.ValueKind == System.Text.Json.JsonValueKind.True;
				bool unhandled = em.TryGetProperty("unhandled", out var uhEl) && uhEl.ValueKind == System.Text.Json.JsonValueKind.True;
				var extype = em.TryGetProperty("exception_type", out var txEl) && txEl.ValueKind == System.Text.Json.JsonValueKind.String ? txEl.GetString() ?? "exception" : "exception";
				coordinator.WriteObservedException(firstChance, unhandled, extype, "");
			}
			else {
				source.EmitPaused(pid, started, infos);
			}
			SpyInc("test_emitted_observations");
			return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["emitted"] = kind });
		}
		// install/uninstall/fail_next controls.
		var install = args.TryGetValue("install", out var iEl) && iEl is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
		var uninstall = args.TryGetValue("install", out var uEl) && uEl is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False };
		if (install) {
			var fake = new FakeDbgProcessControlAdapter();
			fake.Observation += OnAdapterObservation;
			lock (sessionLock)
				testAdapter = fake;
			return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["installed"] = true });
		}
		if (uninstall) {
			FakeDbgProcessControlAdapter? old;
			lock (sessionLock) {
				old = testAdapter;
				testAdapter = null;
				if (ownedProcess is not null)
					adapter = new DbgProcessControlAdapter(ownedProcess);
			}
			if (old is not null)
				old.Observation -= OnAdapterObservation;
			return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["installed"] = false });
		}
		if (args.TryGetValue("fail_next", out var fEl) && fEl is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } fs) {
			FakeDbgProcessControlAdapter fake;
			lock (sessionLock) {
				if (testAdapter is null) {
					testAdapter = new FakeDbgProcessControlAdapter();
					testAdapter.Observation += OnAdapterObservation;
				}
				fake = testAdapter;
			}
			fake.FailOnPost = fs.GetString() == "explicit_failure";
			return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["fail_next"] = fs.GetString() });
		}
		return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["hint"] = "install/fail_next/emit" });
	}

	/// <summary>DNMCP_TEST-only: append N synthetic events to the active buffer.</summary>
	string TestFlood(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var count = args is not null && args.TryGetValue("count", out var c) && c is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } cj ? (int)cj.GetDouble() : 0;
		var bytesPer = args is not null && args.TryGetValue("bytes_per_event", out var b) && b is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } bj ? (int)bj.GetDouble() : 64;
		if (count is < 1 or > 20000)
			throw new ArgumentException("count must be within 1..20000", "count");
		var (written, lost, earliest, last) = coordinator.WriteTestFlood(count, bytesPer);
		SpyInc("test_flood_events");
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true, ["written"] = written, ["events_lost"] = lost,
			["earliest_cursor"] = earliest, ["last_cursor"] = last,
		});
	}

	/// <summary>DNMCP_TEST-only: arm the next launch's Start failure or pre-claim exit.</summary>
	string TestStart(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var mode = args is not null && args.TryGetValue("mode", out var m) && m is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } mj ? mj.GetString() : null;
		switch (mode) {
			case "fail_start":
				testFailNextStart = true;
				break;
			case "exit_before_claim":
				testExitBeforeClaim = true;
				break;
			default:
				throw new ArgumentException("mode must be fail_start or exit_before_claim", "mode");
		}
		return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["armed"] = mode });
	}

	// Tools whose request_id is structurally required: the -32602 shape rejection precedes
	// every gate/state semantic (ACC-002: invalid-gate continue is DEBUG_DISABLED only for
	// schema-valid requests).
	static readonly System.Collections.Generic.HashSet<string> RequestIdRequired = new() {
		"debug_launch", "debug_pause", "debug_continue", "debug_terminate", "debug_restart",
		"debug_set_breakpoint", "debug_set_breakpoint_enabled", "debug_remove_breakpoint",
		"debug_set_exception_policy", "debug_step", "debug_dump_module",
	};

	public CallToolResult Execute(string toolName, Dictionary<string, object>? arguments) {
		if (RequestIdRequired.Contains(toolName))
			ArgString(arguments, "request_id", required: true);
		string? envelope;
		try {
			envelope = toolName switch {
				"debug_status" => Status(arguments),
				"debug_launch" => Launch(arguments),
				"debug_pause" => Control(arguments, ControlOperation.Pause).GetAwaiter().GetResult(),
				"debug_continue" => Continue(arguments).GetAwaiter().GetResult(),
				"debug_terminate" => Control(arguments, ControlOperation.Terminate).GetAwaiter().GetResult(),
				"debug_restart" => Restart(arguments).GetAwaiter().GetResult(),
				"debug_read_events" => ReadEvents(arguments, wait: false).GetAwaiter().GetResult(),
				"debug_wait_event" => ReadEvents(arguments, wait: true).GetAwaiter().GetResult(),
				"debug_set_breakpoint" => SetBreakpoint(arguments),
				"debug_list_breakpoints" => ListBreakpoints(arguments),
				"debug_set_breakpoint_enabled" => SetBreakpointEnabled(arguments),
				"debug_remove_breakpoint" => RemoveBreakpoint(arguments),
				"debug_set_exception_policy" => SetExceptionPolicy(arguments),
				"debug_list_threads" => ListThreads(arguments),
				"debug_get_stack" => GetStack(arguments),
				"debug_step" => Step(arguments),
				"debug_get_locals" => GetLocals(arguments),
				"debug_expand_value" => ExpandValue(arguments),
				"debug_list_modules" => ListModules(arguments),
				"debug_read_memory" => ReadMemory(arguments),
				"debug_dump_module" => DumpModule(arguments),
				"debug_test_spy" => TestSpy(arguments),
				"debug_test_clock" => TestClock(arguments),
				"debug_test_adapter" => TestAdapter(arguments),
				"debug_test_flood" => TestFlood(arguments),
				"debug_test_start" => TestStart(arguments),
				// The three fixed-disabled APIs (API-DYN-004/005/010) answer direct calls with
				// the domain CAPABILITY_UNAVAILABLE envelope — never an "unknown tool" text —
				// and without the unsupported-target details object.
				"debug_attach" or "debug_detach" or "debug_list_attachable_processes"
					=> Fail(coordinator, DomainErrorCodes.CapabilityUnavailable),
				_ => null,
			};
		}
		catch (ArgumentException ex) {
			// Semantic parameter/metadata rejections (token table, identity-shape, boundary)
			// surface as JSON-RPC -32602 via the server's ArgumentException mapping.
			throw;
		}
		catch (Exception ex) {
			envelope = Fail(coordinator, DomainErrorCodes.InternalError, message: ex.GetType().Name + ": " + ex.Message);
		}
		if (envelope is null)
			return new CallToolResult {
				Content = new List<ToolContent> { new() { Text = $"Unknown tool: {toolName}" } },
				IsError = true,
			};
		return new CallToolResult {
			Content = new List<ToolContent> { new() { Text = envelope } },
			IsError = envelope.Contains("\"ok\":false"),
		};
	}

	static string Ok(DebugSessionCoordinator c, object result, List<string>? warnings = null, bool untrustedSampleData = false) {
		var envelope = new DebugSuccessEnvelope {
			DebugContext = c.ContextSnapshot(),
			Result = result,
			UntrustedSampleData = untrustedSampleData,
		};
		if (warnings is not null)
			envelope.Warnings = warnings;
		return JsonSerializer.Serialize(envelope, CanonicalOptions);
	}

	static string Fail(DebugSessionCoordinator c, string code, List<string>? requiredStates = null, string? message = null) {
		var error = DomainErrorDto.Create(code, c.State, requiredStates);
		if (message is not null)
			error.Message = message;
		return JsonSerializer.Serialize(new DebugFailureEnvelope {
			DebugContext = c.ContextSnapshot(),
			Error = error,
		}, CanonicalOptions);
	}

	// ---- handlers ----

	string Status(Dictionary<string, object>? args) {
		_ = args;
		var owned = OwnedProcessSnapshot();
		return Ok(coordinator, new StatusResultDto {
			State = coordinator.State,
			ActiveSessionId = coordinator.ActiveSessionId,
			LastSessionId = coordinator.LastSessionId,
			OwnedProcess = owned,
			ObservedProcessState = coordinator.ObservedProcessState,
			RuntimeFamily = activePlan?.RuntimeFamily,
			Architecture = activePlan is null ? null : launchArchitecture,
			StartKind = activePlan is null ? null : "launch",
			LaunchMode = activePlan?.LaunchMode,
			Fault = coordinator.State == DebugStates.Faulted ? coordinator.Fault.ToString() : null,
		});
	}

	OwdProcessDto? OwnedProcessSnapshot() {
		lock (sessionLock) {
			var p = ownedProcess;
			if (p is null)
				return null;
			return new OwdProcessDto {
				ProcessHandle = $"proc-{p.Id}",
				Pid = p.Id,
				StartTimeUtc = Rfc3339(sessionStartedUtc),
				Filename = p.Filename ?? string.Empty,
				ImageIdentity = launchIdentities.FirstOrDefault(),
				RuntimeIdentity = $"{activePlan?.RuntimeFamily ?? "unknown"}-{launchArchitecture}",
				RuntimeFamily = activePlan?.RuntimeFamily,
				Architecture = launchArchitecture,
			};
		}
	}

	string Launch(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);

		var requestId = ArgString(args, "request_id", required: true);
		var targetPath = ArgString(args, "target_path", required: true);
		var expectedSha = ArgString(args, "expected_sha256", required: true);
		var launchMode = ArgString(args, "launch_mode", required: true);
		var architecture = ArgString(args, "architecture", required: true);

		// CON-DYN-010: exact architecture equality with the dnSpy OS process, zero side effects.
		var hostArch = IntPtr.Size == 8 ? Architectures.X64 : Architectures.X86;
		if (architecture != hostArch)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable,
				message: $"requested architecture {architecture} does not match the debugging dnSpy process ({hostArch})");

		// Target identity + lease (the Start call must not race a file replacement).
		if (!TryLeaseIdentity(targetPath, "target", "file", expectedSha, out var targetIdentity, out var identityError))
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: identityError);

		var hostPath = ArgString(args, "host_path");
		var hostSha = ArgString(args, "host_sha256");
		var harnessPath = ArgString(args, "harness_path");
		var harnessSha = ArgString(args, "harness_sha256");

		FileIdentityDto? hostIdentity = null, harnessIdentity = null;
		if (!string.IsNullOrEmpty(hostPath) && !TryLeaseIdentity(hostPath, "host", "file", hostSha, out hostIdentity, out identityError))
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: identityError);
		if (!string.IsNullOrEmpty(harnessPath) && !TryLeaseIdentity(harnessPath, "harness", "file", harnessSha, out harnessIdentity, out identityError))
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: identityError);

		var breakKind = ArgString(args, "break_kind") ?? BreakKinds.None;
		// CON-DYN-011 / ACC-026: reparse points (junctions/symlinks) in any component of the
		// target/host/harness/working-directory paths are rejected before the identity lease —
		// identity must resolve on a reparse-free path, never through substitution.
		foreach (var rpPath in new[] { targetPath, hostPath, harnessPath, ArgString(args, "working_directory") }) {
			if (string.IsNullOrEmpty(rpPath))
				continue;
			string? reparseComponent = FindReparseComponent(rpPath);
			if (reparseComponent is not null)
				return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: $"path traverses a reparse point: {reparseComponent}");
		}
		// CON-DYN-011 / ACC-026: every filesystem input must live under the configured
		// AllowedSampleRoot (when set); anything outside is TARGET_MISMATCH before Start.
		var sampleRoot = settings.CurrentSnapshot?.AllowedSampleRoot;
		if (!string.IsNullOrEmpty(sampleRoot)) {
			var rootFull = System.IO.Path.GetFullPath(sampleRoot);
			if (!rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
				rootFull += System.IO.Path.DirectorySeparatorChar;
			foreach (var candidate in new[] { targetPath, hostPath, harnessPath, ArgString(args, "working_directory") }) {
				if (string.IsNullOrEmpty(candidate))
					continue;
				string candidateFull;
				try { candidateFull = System.IO.Path.GetFullPath(candidate); }
				catch (Exception) { candidateFull = candidate; }
				if (!candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && !string.Equals(candidateFull.TrimEnd(System.IO.Path.DirectorySeparatorChar), rootFull.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
					return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: $"path is outside AllowedSampleRoot: {candidate}");
			}
		}
		var detected = launchMode is LaunchModes.Auto or LaunchModes.Harness
			? DetectRuntimeFamily(harnessPath ?? targetPath)
			: null;

		var plan = LaunchPlanner.Plan(new LaunchPlanner.LaunchRequest {
			TargetPath = targetPath,
			LaunchMode = launchMode,
			TargetArgv = ArgStrings(args, "target_argv"),
			WorkingDirectory = ArgString(args, "working_directory"),
			BreakKind = breakKind,
			HostPath = hostPath,
			HostArgv = ArgStrings(args, "host_argv"),
			HarnessPath = harnessPath,
			HarnessArgv = ArgStrings(args, "harness_argv"),
			DetectedRuntimeFamily = detected,
		}, out var planError);
		if (plan is null) {
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: planError);
		}

		if (!coordinator.BeginLaunch(requestId, plan.LaunchMode, plan.RuntimeFamily, architecture)) {
			ReleaseLeases();
			return coordinator.State == DebugStates.Idle
				? Fail(coordinator, DomainErrorCodes.RequestIdReuse)
				: Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Idle });
		}

		lock (sessionLock) {
			activePlan = plan;
			launchIdentities = new List<FileIdentityDto>();
			if (targetIdentity is not null) launchIdentities.Add(targetIdentity);
			if (hostIdentity is not null) launchIdentities.Add(hostIdentity);
			if (harnessIdentity is not null) launchIdentities.Add(harnessIdentity);
			launchArchitecture = architecture;
			pendingClaimStartsPaused = breakKind != BreakKinds.None;
			pendingClaimReason = breakKind switch {
				BreakKinds.Process => "process_break",
				BreakKinds.ModuleCctorOrEntryPoint => "module_cctor_or_entry",
				BreakKinds.EntryPoint => "entry_point_break",
				_ => null,
			};
			claimDeadlineUtc = DateTime.UtcNow + ControlOperationRecord.DefaultDeadline;
			sessionStartedUtc = DateTime.UtcNow;
			launchClaimTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		var startError = StartViaWpf(plan);
		if (startError is not null) {
			coordinator.MarkLaunchFailed("INTERNAL_ERROR");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.InternalError, message: startError);
		}

		// Task.Wait reports completion, not the value: a negatively-settled claim (test
		// pre-claim exit) must still surface as !claimed.
		var claimed = launchClaimTcs!.Task.Wait(ControlOperationRecord.DefaultDeadline) && launchClaimTcs!.Task.Result;
		if (!claimed) {
			coordinator.MarkLaunchFailed("TIMEOUT");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.Timeout);
		}

		lock (sessionLock)
			launchClaimTcs = null;
		return Ok(coordinator, new LaunchResultDto {
			SessionId = coordinator.ActiveSessionId,
			Generation = coordinator.Generation,
			State = coordinator.State,
			ClaimDeadlineUtc = Rfc3339(claimDeadlineUtc),
			LaunchMode = plan.LaunchMode,
			RuntimeFamily = plan.RuntimeFamily,
			Architecture = architecture,
			FileIdentities = launchIdentities.ToList(),
		});
	}

	// DNMCP_TEST-only: armed by debug_test_start; the next Start callback throws
	// synchronously (Start-error path: EVT start_failed + MarkLaunchFailed).
	volatile bool testFailNextStart;
	volatile bool testExitBeforeClaim;

	string? StartViaWpf(LaunchPlan plan) {
		if (dbgManager is null)
			return "DbgManager is not available";
		if (testFailNextStart) {
			testFailNextStart = false;
			SpyInc("test_start_failures");
			return "test-injected Start failure";
		}
		DebugProgramOptions options = BuildOptions(plan);
		var uiDispatcher = Application.Current?.Dispatcher;
		Func<string?> start = () => {
			SpyInc("dbg_start_calls");
			SpyCounters["start_thread_is_wpf"] = uiDispatcher is not null && uiDispatcher.Thread == System.Threading.Thread.CurrentThread ? 1 : 0;
			return dbgManager.Start(options);
		};
		SpyInc(uiDispatcher is null ? "start_without_wpf_dispatcher" : "start_via_wpf_invoke");
		return uiDispatcher is null ? start() : (string?)uiDispatcher.Invoke(start);
	}

	static DebugProgramOptions BuildOptions(LaunchPlan plan) {
		var workDir = string.IsNullOrEmpty(plan.WorkingDirectory) ? null : plan.WorkingDirectory;
		if (plan.UseHost && plan.Host is not null) {
			// coreclr-dotnet: dnSpy composes host + HostArguments + quoted Filename + CommandLine.
			return new DotNetStartDebuggingOptions {
				UseHost = true,
				Host = plan.Host,
				HostArguments = plan.HostArguments,
				Filename = plan.Filename,
				CommandLine = plan.CommandLine,
				WorkingDirectory = workDir,
				BreakKind = plan.UpstreamBreakKind,
			};
		}
		if (plan.RuntimeFamily == RuntimeFamilies.Net48) {
			return new DotNetFrameworkStartDebuggingOptions {
				Filename = plan.Filename,
				CommandLine = plan.CommandLine,
				WorkingDirectory = workDir,
				BreakKind = plan.UpstreamBreakKind,
			};
		}
		// coreclr-apphost: the apphost executable is launched directly.
		return new DotNetStartDebuggingOptions {
			UseHost = false,
			Filename = plan.Filename,
			CommandLine = plan.CommandLine,
			WorkingDirectory = workDir,
			BreakKind = plan.UpstreamBreakKind,
		};
	}

	async Task VirtualDeadlineImpl(TaskCompletionSource<bool> done, TimeSpan deadline) {
		var startVirtual = TestClockVirtualElapsedMs();
		while (TestClockVirtualElapsedMs() - startVirtual < deadline.TotalMilliseconds)
			await Task.Delay(20).ConfigureAwait(false);
		done.TrySetResult(true);
	}

	Task VirtualDeadline(TimeSpan deadline) {
		var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var _ = VirtualDeadlineImpl(done, deadline);
		return done.Task;
	}

	async Task<string> Control(Dictionary<string, object>? args, ControlOperation operation) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, RequiredFor(operation));
		var requestId = ArgString(args, "request_id", required: true);

		if (!laneQueue.TryEnterControl(out var ticket))
			return Fail(coordinator, DomainErrorCodes.LimitExceeded);

		var admission = coordinator.TryBeginControl(operation, requestId);
		if (!admission.Admitted || admission.Record is null) {
			ticket?.TryRelease();
			return Fail(coordinator, DomainErrorCodes.InvalidState, admission.RequiredStates.ToList());
		}
		var record = admission.Record;

		TaskCompletionSource<string> tcs;
		lock (sessionLock)
			controlOutcomeTcs = tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

		dbgManager?.Dispatcher.BeginInvoke(new Action(() => {
			FakeDbgProcessControlAdapter? localFake;
			lock (sessionLock) localFake = testAdapter;
			var localAdapter = (IDbgProcessControlAdapter?)localFake ?? adapter;
			var result = localAdapter is null ? IDbgProcessControlAdapter.PostResult.ExplicitFailure
				: operation == ControlOperation.Pause ? localAdapter.PostBreak(record)
				: localAdapter.PostTerminate(record);
			if (result == IDbgProcessControlAdapter.PostResult.Delivered) {
				coordinator.MarkControlIssued(record);
			}
			else {
				coordinator.SettleControlFailure(record, DomainErrorCodes.InternalError);
				tcs.TrySetResult("explicit-failure");
			}
		}));

		var deadlineTask = TestModeEnabled ? VirtualDeadline(ControlOperationRecord.DefaultDeadline) : Task.Delay(ControlOperationRecord.DefaultDeadline);
		var done = await Task.WhenAny(tcs.Task, deadlineTask).ConfigureAwait(false);
		ticket?.TryRelease();
		if (done != tcs.Task) {
			coordinator.SettleControlFailure(record, DomainErrorCodes.Timeout);
			lock (sessionLock) controlOutcomeTcs = null;
			return Fail(coordinator, DomainErrorCodes.Timeout);
		}
		var outcome = tcs.Task.Result;
		lock (sessionLock) controlOutcomeTcs = null;

		if (outcome == "explicit-failure")
			return Fail(coordinator, DomainErrorCodes.InternalError, message: "the debugger rejected the control request");

		if (operation == ControlOperation.Pause)
			return Ok(coordinator, new PauseResultDto {
				State = coordinator.State,
				PauseEpoch = coordinator.PauseEpoch,
				// P2 collision semantics (§3.2): an authoritative observation settled this
				// pause, so the response reports the REAL primary cause — manual only when
				// an issued pause landed with no higher-priority cause.
				Reason = coordinator.LastPauseCause,
				RequestEffect = DebugWire.RequestEffectStateSatisfied,
			});

		// Terminate settled by the authoritative removal observation.
		return Ok(coordinator, new TerminateResultDto {
			State = coordinator.State,
			TerminalCursor = CursorOf(coordinator),
		});
	}

	async Task<string> Continue(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pauseEpoch = ArgInt(args, "pause_epoch", required: true);
		if (coordinator.State != DebugStates.Paused || coordinator.PauseEpoch != pauseEpoch)
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });

		if (!laneQueue.TryEnterGeneral(out var ticket))
			return Fail(coordinator, DomainErrorCodes.LimitExceeded);
		try {
			var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (sessionLock) continueInFlight = true;
			dbgManager?.Dispatcher.BeginInvoke(new Action(() => {
				try {
					ownedProcess?.Run();
					completed.TrySetResult(true);
				}
				catch {
					completed.TrySetResult(false);
				}
			}));
			var done = await Task.WhenAny(completed.Task, Task.Delay(ControlOperationRecord.DefaultDeadline)).ConfigureAwait(false);
			if (done != completed.Task || !completed.Task.Result) {
				lock (sessionLock) continueInFlight = false;
				return Fail(coordinator, DomainErrorCodes.InternalError, message: "continue could not be delivered");
			}
			coordinator.MarkResumed("continue");
			lock (sessionLock) continueInFlight = false;
			return Ok(coordinator, new ContinueResultDto {
				State = coordinator.State,
				PauseEpoch = coordinator.PauseEpoch,
			});
		}
		finally {
			ticket?.TryRelease();
		}
	}

	async Task<string> Restart(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Running, DebugStates.Paused });
		var requestId = ArgString(args, "request_id", required: true);

		// Phase 1: terminate the owned process under a restart reservation.
		var terminateEnvelope = await Control(args, ControlOperation.Restart).ConfigureAwait(false);
		if (terminateEnvelope.Contains("\"ok\":false"))
			return terminateEnvelope;

		// Phase 2: the removal observation left the session in post-exit stopping; relaunch.
		if (!coordinator.BeginRestartRelaunch())
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Stopping });

		lock (sessionLock) {
			claimDeadlineUtc = DateTime.UtcNow + ControlOperationRecord.DefaultDeadline;
			launchClaimTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		var plan = activePlan!;
		var startError = StartViaWpf(plan);
		if (startError is not null) {
			coordinator.MarkLaunchFailed("INTERNAL_ERROR");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.InternalError, message: startError);
		}
		// Task.Wait reports completion, not the value: a negatively-settled claim (test
		// pre-claim exit) must still surface as !claimed.
		var claimed = launchClaimTcs!.Task.Wait(ControlOperationRecord.DefaultDeadline) && launchClaimTcs!.Task.Result;
		if (!claimed) {
			coordinator.MarkLaunchFailed("TIMEOUT");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.Timeout);
		}
		lock (sessionLock)
			launchClaimTcs = null;
		return Ok(coordinator, new RestartResultDto {
			State = coordinator.State,
			Generation = coordinator.Generation,
			ClaimDeadlineUtc = Rfc3339(claimDeadlineUtc),
		});
	}

	async Task<string> ReadEvents(Dictionary<string, object>? args, bool wait) {
		var sessionId = ArgString(args, "session_id", required: true);
		var afterCursor = (long)ArgLong(args, "after_cursor", 0);
		var limit = (int)Math.Min(1000, ArgLong(args, "limit", 100));
		var kinds = ArgStrings(args, "kinds");
		var kindFilter = kinds is { Count: > 0 } ? kinds : null;

		DebugEventBuffer.ReadResult? read = null;
		if (wait) {
			var timeoutMs = (int)Math.Min(120000, ArgLong(args, "timeout_ms", 10000));
			if (!await waitSlots.WaitAsync(timeoutMs + 1000).ConfigureAwait(false))
				return Fail(coordinator, DomainErrorCodes.LimitExceeded);
			try {
				var deadlineTicks = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
				do {
					read = coordinator.ReadEvents(sessionId, afterCursor, limit, kindFilter);
					if (read is { Events.Count: > 0 })
						break;
					if (Stopwatch.GetTimestamp() >= deadlineTicks)
						break;
					await Task.Delay(100).ConfigureAwait(false);
				} while (true);
			}
			finally {
				waitSlots.Release();
			}
		}
		else {
			read = coordinator.ReadEvents(sessionId, afterCursor, limit, kindFilter);
		}
		if (read is null)
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown session");

		var events = read.Events.Select(e => (object)JsonDocument.Parse(e).RootElement).ToList();
		if (wait)
			return Ok(coordinator, new WaitEventsResultDto {
				Events = events,
				NextCursor = read.NextCursor,
				EarliestCursor = read.EarliestCursor,
				EventsLost = read.EventsLost,
				TimedOut = read.Events.Count == 0,
			});
		return Ok(coordinator, new EventsResultDto {
			Events = events,
			NextCursor = read.NextCursor,
			EarliestCursor = read.EarliestCursor,
			EventsLost = read.EventsLost,
		});
	}

	// ---- IMP-006: breakpoints and exception policy ----

	/// <summary>Assigns module handles for the owned process's loaded modules. Increment 2 note:
	/// MVID is taken from the first set_breakpoint request for the handle (per-handle identity
	/// registration); wiring the module-load events for authoritative MVIDs lands with the
	/// IMP-009 module tools.</summary>
	void RegisterModules(DbgProcess process) {
		lock (sessionLock) {
			modulesByHandle.Clear();
			int index = 0;
			foreach (var runtime in process.Runtimes) {
				string runtimeHandle = $"rt-{index++}";
				foreach (var module in runtime.Modules) {
					string handle = $"mod-{modulesByHandle.Count}";
					modulesByHandle[handle] = new RegisteredModuleRecord {
						ModuleHandle = handle,
						RuntimeHandle = runtimeHandle,
						Filename = module.Filename ?? string.Empty,
						Name = module.Name,
						Address = module.Address,
						Size = module.Size,
						Layout = module.IsDynamic || string.IsNullOrEmpty(module.Filename) ? "memory" : "file",
						Mvid = "00000000-0000-0000-0000-000000000000", // real MVID lands with DmdModule wiring (IMP-009 note)
						UpstreamId = (ModuleId)(module.Filename ?? module.Name),
					};
				}
			}
		}
	}

	bool PausedEpochMatches(Dictionary<string, object>? args) =>
		coordinator.State == DebugStates.Paused && coordinator.PauseEpoch == ArgInt(args, "pause_epoch", required: true);

	/// <summary>
	/// Enumerates the owned process's live module table on the DbgManager dispatcher and
	/// refreshes <see cref="modulesByHandle"/> with minted mod-N handles (preserving identity
	/// data earlier set_breakpoint calls recorded, seeding the launch-verified target sha).
	/// Both list_modules and get_stack call this so frame→module mapping works regardless of
	/// which tool the client happens to call first.
	/// </summary>
	List<RegisteredModuleRecord> RegisterLiveModules() {
		var modules = new List<RegisteredModuleRecord>();
		PostVoidToDispatcherSync(() => RegisterLiveModulesInto(modules));
		return modules;
	}

	/// <summary>Must run on the DbgManager dispatcher (the ModulesChanged handler already does).</summary>
	void RegisterLiveModulesInto(List<RegisteredModuleRecord> modules) {
			DbgProcess? process;
			lock (sessionLock) process = ownedProcess;
			if (process is null)
				return;
			lock (sessionLock) {
				int index = 0;
				foreach (var runtime in process.Runtimes) {
					var runtimeHandle = $"rt-{index++}";
					foreach (var module in runtime.Modules) {
						string handle = $"mod-{modules.Count}";
						var record = new RegisteredModuleRecord {
							ModuleHandle = handle,
							RuntimeHandle = runtimeHandle,
							Filename = module.Filename ?? string.Empty,
							Name = module.Name,
							Address = module.Address,
							Size = module.Size,
							Layout = module.IsDynamic || string.IsNullOrEmpty(module.Filename) ? "memory" : "file",
							Mvid = "00000000-0000-0000-0000-000000000000",
							UpstreamId = (ModuleId)(module.Filename ?? module.Name),
						};
						// Preserve identity data registered by earlier set_breakpoint calls on the same handle.
						if (modulesByHandle.TryGetValue(handle, out var existing) && existing.Filename == record.Filename) {
							record.Mvid = existing.Mvid;
							record.Sha256 = existing.Sha256;
						}
						// The launch-verified target carries its disk-strong sha from the start.
						if (string.IsNullOrEmpty(record.Sha256) && !string.IsNullOrEmpty(record.Filename)) {
							var targetIdentity = launchIdentities.FirstOrDefault(i =>
								i.Role == "target" && !string.IsNullOrEmpty(i.FinalPath)
								&& string.Equals(i.FinalPath, record.Filename, StringComparison.OrdinalIgnoreCase));
							if (targetIdentity is not null)
								record.Sha256 = targetIdentity.Sha256;
						}
						modules.Add(record);
					}
				}
				// The enumeration refreshes the handle registry so dump/memory/breakpoints see
				// the live module table (RegisterModules at claim time runs before module loads).
				modulesByHandle.Clear();
				foreach (var m in modules)
					modulesByHandle[m.ModuleHandle] = m;
			}
	}

	string SetBreakpoint(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		if (!PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var requestId = ArgString(args, "request_id", required: true);
		var moduleHandle = ArgString(args, "module_handle", required: true);
		var mvid = ArgString(args, "mvid", required: true);
		var methodToken = ArgString(args, "method_token", required: true);
		var ilOffset = ArgInt(args, "il_offset", required: true);
		var moduleSha = ArgString(args, "module_sha256");
		var identityStrength = ArgString(args, "identity_strength");
		if (identityStrength.Length == 0)
			identityStrength = "disk_strong";
		if (identityStrength != "disk_strong" && identityStrength != "runtime_weak")
			throw new ArgumentException("identity_strength must be disk_strong or runtime_weak", nameof(identityStrength));
		var enabled = args is not null && args.TryGetValue("enabled", out var e) && e is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };

		// Metadata/shape rejections are -32602 (ArgumentException): a non-MethodDef token, a
		// disk_strong request without its SHA, and a runtime_weak request carrying one.
		uint tokenValue;
		try {
			tokenValue = ParseToken(methodToken);
		}
		catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) {
			throw new ArgumentException("method_token is not a valid 32-bit token", nameof(methodToken));
		}
		if ((tokenValue >> 24) != 0x06 || (tokenValue & 0x00FFFFFF) == 0)
			throw new ArgumentException("method_token must reference a MethodDef row (0x06xxxxxx)", nameof(methodToken));
		if (identityStrength == "disk_strong" && string.IsNullOrEmpty(moduleSha))
			throw new ArgumentException("disk_strong breakpoints require module_sha256", nameof(moduleSha));
		if (identityStrength == "runtime_weak" && !string.IsNullOrEmpty(moduleSha))
			throw new ArgumentException("runtime_weak breakpoints reject module_sha256", nameof(moduleSha));

		// Only handles minted by debug_list_modules are addressable; unknown or stale handles
		// are TARGET_MISMATCH, never an implicit re-registration of the launch target.
		RegisteredModuleRecord? module;
		lock (sessionLock) {
			if (!modulesByHandle.TryGetValue(moduleHandle, out module))
				return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: "module_handle is not a registered module of this pause");
		}
		bpStore.RegisterModule(new RegisteredModule {
			ModuleHandle = module.ModuleHandle,
			RuntimeHandle = module.RuntimeHandle,
			Mvid = module.Mvid,
			IdentityStrength = identityStrength,
			Sha256 = module.Sha256,
		});
		var shaForCreate = string.IsNullOrEmpty(moduleSha) ? module.Sha256 : moduleSha;
		var (entry, error) = bpStore.TryCreate(moduleHandle, shaForCreate, mvid, methodToken, ilOffset, enabled);
		if (entry is null || error != DebugBreakpointStore.CreateError.None)
			return Fail(coordinator, MapCreateError(error), message: $"breakpoint rejected: {error}");

		var created = PostToDispatcher(() => {
			if (breakpointsService is null || locationFactory is null)
				return null;
			var location = locationFactory.Create(module.UpstreamId, tokenValue, (uint)ilOffset);
			var bp = breakpointsService.Add(new DbgCodeBreakpointInfo(location,
				new DbgCodeBreakpointSettings { IsEnabled = enabled }, 0));
			return bp is null ? null : new[] { bp };
		});
		if (created is null or { Length: 0 }) {
			bpStore.Remove(entry.BreakpointId);
			return Fail(coordinator, DomainErrorCodes.InternalError, message: "the debugger rejected the breakpoint location");
		}
		foreach (var bp in created) {
			bpStore.SetEnabled(entry.BreakpointId, enabled);
			lock (sessionLock) {
				mcpIdByDnSpyBreakpoint[bp.Id] = entry.BreakpointId;
				dnSpyIdByMcpBreakpoint[entry.BreakpointId] = bp.Id;
				dnSpyBreakpointsByMcp.TryGetValue(entry.BreakpointId, out var list);
				list ??= new List<DbgCodeBreakpoint>();
				list.Add(bp);
				dnSpyBreakpointsByMcp[entry.BreakpointId] = list;
			}
		}
		return Ok(coordinator, new SetBreakpointResultDto { Breakpoint = BreakpointDtoOf(entry) });
	}

	static uint ParseToken(string token) {
		var text = token.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			return Convert.ToUInt32(text.Substring(2), 16);
		return Convert.ToUInt32(text);
	}

	static string MapCreateError(DebugBreakpointStore.CreateError error) => error switch {
		DebugBreakpointStore.CreateError.ModuleNotFound => DomainErrorCodes.TargetMismatch,
		DebugBreakpointStore.CreateError.MvidMismatch or DebugBreakpointStore.CreateError.ShaMismatch
			or DebugBreakpointStore.CreateError.ShaRejected or DebugBreakpointStore.CreateError.MissingSha256
			=> DomainErrorCodes.TargetMismatch,
		DebugBreakpointStore.CreateError.DuplicateBreakpoint => DomainErrorCodes.AlreadyExists,
		_ => DomainErrorCodes.InternalError,
	};

	string ListBreakpoints(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState);
		var all = bpStore.List();
		if (args is not null && args.TryGetValue("enabled", out var en) && en is System.Text.Json.JsonElement { ValueKind: var v }) {
			if (v == System.Text.Json.JsonValueKind.True) all = all.Where(b => b.Enabled).ToList();
			else if (v == System.Text.Json.JsonValueKind.False) all = all.Where(b => !b.Enabled).ToList();
		}
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 100));
		string? cursor = ArgString(args, "page_cursor");
		int start = 0;
		if (!string.IsNullOrEmpty(cursor))
			start = int.TryParse(cursor, out var s) ? s : 0;
		var page = all.Skip(start).Take(pageSize).ToList();
		var dto = new ListBreakpointsResultDto {
			Items = page.Select(BreakpointDtoOf).ToList(),
			Truncated = false,
			TotalKnown = all.Count,
		};
		if (start + page.Count < all.Count)
			dto.NextPageCursor = (start + page.Count).ToString();
		return Ok(coordinator, dto);
	}

	string SetBreakpointEnabled(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var breakpointId = ArgString(args, "breakpoint_id", required: true);
		var enabled = args is not null && args.TryGetValue("enabled", out var e) && e is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
		if (!bpStore.SetEnabled(breakpointId, enabled))
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown breakpoint");
		PostToDispatcher(() => {
			if (breakpointsService is null)
				return null;
			lock (sessionLock) {
				if (dnSpyBreakpointsByMcp.TryGetValue(breakpointId, out var list)) {
					foreach (var bp in list)
						breakpointsService.Modify(bp, new DbgCodeBreakpointSettings { IsEnabled = enabled });
				}
			}
			return null;
		});
		var entry = bpStore.List().FirstOrDefault(b => b.BreakpointId == breakpointId);
		return Ok(coordinator, new SetBreakpointResultDto { Breakpoint = BreakpointDtoOf(entry!) });
	}

	string RemoveBreakpoint(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var breakpointId = ArgString(args, "breakpoint_id", required: true);
		if (!bpStore.Remove(breakpointId))
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown breakpoint");
		PostToDispatcher(() => {
			if (breakpointsService is null)
				return null;
			lock (sessionLock) {
				if (dnSpyBreakpointsByMcp.TryGetValue(breakpointId, out var list)) {
					breakpointsService.Remove(list.Where(b => b is not null).ToArray());
					dnSpyBreakpointsByMcp.Remove(breakpointId);
				}
			}
			return null;
		});
		return Ok(coordinator, new RemoveBreakpointResultDto { Removed = true, BreakpointId = breakpointId });
	}

	string SetExceptionPolicy(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState);
		_ = ArgString(args, "request_id", required: true);
		var breakOn = ExtractBreakOnValue(args);
		if (breakOn is null)
			return Fail(coordinator, DomainErrorCodes.InternalError, message: "policy must be one of unhandled, first_chance_and_unhandled, none");
		string previous;
		lock (sessionLock) {
			previous = exceptionPolicy;
			exceptionPolicy = breakOn;
		}
		return Ok(coordinator, new ExceptionPolicyResultDto {
			Previous = new ExceptionPolicyDto { BreakOn = previous },
			Current = new ExceptionPolicyDto { BreakOn = breakOn },
		});
	}

	static string? ExtractBreakOn(string policyJsonOrValue) {
		var text = policyJsonOrValue.Trim();
		if (text.StartsWith("{")) {
			try {
				using var doc = System.Text.Json.JsonDocument.Parse(text);
				if (doc.RootElement.TryGetProperty("break_on", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
					text = v.GetString() ?? string.Empty;
			}
			catch { return null; }
		}
		return text is "unhandled" or "first_chance_and_unhandled" or "none" ? text : null;
	}

	static string? ExtractBreakOnValue(Dictionary<string, object>? args) {
		if (args is not null && args.TryGetValue("policy", out var p) && p is System.Text.Json.JsonElement je) {
			if (je.ValueKind == System.Text.Json.JsonValueKind.String)
				return ExtractBreakOn(je.GetString() ?? string.Empty);
			if (je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("break_on", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
				return ExtractBreakOn(v.GetString() ?? string.Empty);
		}
		return null;
	}

	BreakpointDto BreakpointDtoOf(BreakpointEntry entry) {
		RegisteredModuleRecord? module;
		lock (sessionLock)
			modulesByHandle.TryGetValue(entry.Module.ModuleHandle, out module);
		return new BreakpointDto {
			BreakpointId = entry.BreakpointId,
			Owned = true,
			Enabled = entry.Enabled,
			Bound = entry.Bound,
			ModuleIdentity = new ModuleIdentityDto {
				ModuleHandle = entry.Module.ModuleHandle,
				RuntimeHandle = entry.Module.RuntimeHandle ?? "rt-0",
				Name = Path.GetFileName(module?.Filename ?? string.Empty),
				Path = module?.Filename,
				Mvid = entry.Module.Mvid,
				Sha256 = entry.Module.Sha256,
				BaseAddress = 0,
				Size = 0,
				Layout = "file",
				IdentityStrength = entry.Module.IdentityStrength,
			},
			MethodToken = entry.MethodToken,
			IlOffset = entry.IlOffset,
			LastError = entry.LastError,
		};
	}

	/// <summary>Runs the action on the DbgManager dispatcher and returns its result synchronously.</summary>
	DbgCodeBreakpoint[]? PostToDispatcher(Func<DbgCodeBreakpoint[]?> action) {
		if (dbgManager is null)
			return null;
		var done = new ManualResetEventSlim();
		DbgCodeBreakpoint[]? result = null;
		dbgManager.Dispatcher.BeginInvoke(new Action(() => {
			try { result = action(); }
			finally { done.Set(); }
		}));
		if (!done.Wait(ControlOperationRecord.DefaultDeadline))
			return null;
		return result;
	}

	void PostVoidToDispatcher(Action action) {
		SpyInc("dispatcher_async_posts");
		dbgManager?.Dispatcher.BeginInvoke(new Action(action));
	}

	// ---- IMP-007: threads, stack, stepping ----

	sealed class StepRegistration {
		public string Id = string.Empty;
		public string Kind = string.Empty;
		public string ThreadHandle = string.Empty;
	}
	StepRegistration? currentStep;
	int stepSeq;

	// Pause-epoch-scoped handle mints (ACC-006): a handle is only valid for the exact
	// (generation, pause_epoch) it was minted in — older mints resolve to STALE_HANDLE,
	// unknown strings to NOT_FOUND. Handles carry a process-lifetime sequence, never the
	// raw OS thread id (ids get reused by later processes) or a bare stack position.
	sealed class ThreadHandleMint { public int Generation; public int PauseEpoch; public ulong Tid; }
	sealed class FrameHandleMint { public int Generation; public int PauseEpoch; public string ThreadHandle = string.Empty; public int FrameIndex; }
	int threadHandleSeq;
	int frameHandleSeq;
	readonly Dictionary<string, ThreadHandleMint> threadsByHandleMint = new();
	readonly Dictionary<string, FrameHandleMint> framesByHandleMint = new();

	static string ThreadHandleOf(DbgThread thread) => $"th-{thread.Id}";

	/// <summary>Classifies a thread handle against the current pause: null error = valid.</summary>
	string? ClassifyThreadHandle(string threadHandle, out ulong tid) {
		tid = 0;
		lock (sessionLock) {
			if (!threadsByHandleMint.TryGetValue(threadHandle, out var mint))
				return DomainErrorCodes.NotFound;
			if (mint.Generation != coordinator.Generation || mint.PauseEpoch != coordinator.PauseEpoch)
				return DomainErrorCodes.StaleHandle;
			tid = mint.Tid;
			return null;
		}
	}

	/// <summary>Mints (or reuses) the handle for one live thread in the current pause.</summary>
	string MintThreadHandle(DbgThread thread) {
		lock (sessionLock) {
			foreach (var kv in threadsByHandleMint) {
				if (kv.Value.Generation == coordinator.Generation && kv.Value.PauseEpoch == coordinator.PauseEpoch && kv.Value.Tid == thread.Id)
					return kv.Key;
			}
			if (threadsByHandleMint.Count > 4096)
				threadsByHandleMint.Clear();
			var handle = $"th-{Interlocked.Increment(ref threadHandleSeq)}";
			threadsByHandleMint[handle] = new ThreadHandleMint { Generation = coordinator.Generation, PauseEpoch = coordinator.PauseEpoch, Tid = thread.Id };
			return handle;
		}
	}

	/// <summary>Classifies a frame handle against the current pause; null error = valid.</summary>
	string? ClassifyFrameHandle(string frameHandle, out int frameIndex) {
		frameIndex = 0;
		lock (sessionLock) {
			if (!framesByHandleMint.TryGetValue(frameHandle, out var mint))
				return DomainErrorCodes.NotFound;
			if (mint.Generation != coordinator.Generation || mint.PauseEpoch != coordinator.PauseEpoch)
				return DomainErrorCodes.StaleHandle;
			frameIndex = mint.FrameIndex;
			return null;
		}
	}

	string MintFrameHandle(string threadHandle, int index) {
		lock (sessionLock) {
			foreach (var kv in framesByHandleMint) {
				if (kv.Value.Generation == coordinator.Generation && kv.Value.PauseEpoch == coordinator.PauseEpoch
					&& kv.Value.ThreadHandle == threadHandle && kv.Value.FrameIndex == index)
					return kv.Key;
			}
			if (framesByHandleMint.Count > 16384)
				framesByHandleMint.Clear();
			var handle = $"fr-{Interlocked.Increment(ref frameHandleSeq)}";
			framesByHandleMint[handle] = new FrameHandleMint { Generation = coordinator.Generation, PauseEpoch = coordinator.PauseEpoch, ThreadHandle = threadHandle, FrameIndex = index };
			return handle;
		}
	}

	DbgThread? FindThreadByTid(ulong tid) {
		DbgProcess? process;
		lock (sessionLock) process = ownedProcess;
		if (process is null)
			return null;
		foreach (var thread in process.Threads)
			if ((ulong)thread.Id == tid)
				return thread;
		return null;
	}

	string ListThreads(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 100));
		string? cursor = ArgString(args, "page_cursor");
		int start = !string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var s) ? s : 0;
		DbgThread[] threads = Array.Empty<DbgThread>();
		PostVoidToDispatcherSync(() => {
			DbgProcess? process;
			lock (sessionLock) process = ownedProcess;
			if (process is not null)
				threads = process.Threads;
		});
		var mapped = new List<(DbgThread thread, string handle)>();
		foreach (var t in threads)
			mapped.Add((t, MintThreadHandle(t)));
		var page = mapped.Skip(start).Take(pageSize).ToList();
		var dto = new PagedItemsDto {
			Items = page.Select((t, i) => (object)new ThreadInfoDto {
				ThreadHandle = t.handle,
				ManagedId = t.thread.ManagedId?.ToString(),
				OsId = t.thread.Id.ToString(),
				Name = t.thread.HasName() ? t.thread.Name : null,
				State = "paused",
				IsCurrent = start + i == 0,
			}).ToList(),
			Truncated = false,
			TotalKnown = threads.Length,
		};
		if (start + page.Count < threads.Length)
			dto.NextPageCursor = (start + page.Count).ToString();
		return Ok(coordinator, dto);
	}

	string GetStack(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var threadHandle = ArgString(args, "thread_handle", required: true);
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 20));
		string? cursor = ArgString(args, "page_cursor");
		int start = !string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var s) ? s : 0;
		var classifyError = ClassifyThreadHandle(threadHandle, out var tid);
		if (classifyError is not null)
			return Fail(coordinator, classifyError, message: classifyError == DomainErrorCodes.StaleHandle ? "thread_handle belongs to an earlier pause" : "unknown thread_handle");
		var frames = new List<(string module, uint token, uint offset)>();
		var frameModuleFiles = new List<string?>();
		PostVoidToDispatcherSync(() => {
			var thread = FindThreadByTid(tid);
			if (thread is null)
				return;
			var walker = thread.CreateStackWalker();
			try {
				// One lookahead frame beyond the page proves more frames exist (the cursor
				// condition below needs frames.Count to exceed start + page.Count).
				foreach (var frame in walker.GetNextStackFrames(start + pageSize + 1)) {
					var module = frame.Module?.Name ?? frame.Module?.Filename ?? string.Empty;
					frames.Add((module, frame.FunctionToken, frame.FunctionOffset));
					frameModuleFiles.Add(frame.Module?.Filename);
				}
			}
			finally {
				walker.Close();
			}
		});
		// Map each frame to the handle debug_list_modules mints; refresh the live module table
		// first so the mapping also works when get_stack is the client's first paused call.
		if (coordinator.State == DebugStates.Paused)
			RegisterLiveModules();
		var frameModuleHandles = new List<string>();
		lock (sessionLock) {
			for (int fi = 0; fi < frames.Count; fi++) {
				var file = frameModuleFiles[fi];
				var name = frames[fi].module;
				var registered = modulesByHandle.Values.FirstOrDefault(m =>
					(!string.IsNullOrEmpty(file) && string.Equals(m.Filename, file, StringComparison.OrdinalIgnoreCase))
					|| (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(m.Filename)
						&& m.Filename.EndsWith("\\" + file, StringComparison.OrdinalIgnoreCase))
					|| (!string.IsNullOrEmpty(name) && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)));
				frameModuleHandles.Add(registered?.ModuleHandle ?? $"mod:{frames[fi].module}");
			}
		}
		var page = frames.Skip(start).Take(pageSize).ToList();
		var dto = new PagedItemsDto {
			Items = page.Select((f, i) => (object)new FrameInfoDto {
				FrameHandle = MintFrameHandle(threadHandle, start + i),
				Index = start + i,
				Location = new LocationDto {
					ModuleHandle = frameModuleHandles[start + i],
					MethodToken = $"0x{f.token:x8}",
					IlOffset = (int)f.offset,
				},
				DisplayName = $"{f.module}!0x{f.token:x8}+0x{f.offset:x}",
			}).ToList(),
			Truncated = false,
			TotalKnown = frames.Count,
		};
		if (start + page.Count < frames.Count)
			dto.NextPageCursor = (start + page.Count).ToString();
		return Ok(coordinator, dto, untrustedSampleData: true);
	}

	string Step(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var requestId = ArgString(args, "request_id", required: true);
		var threadHandle = ArgString(args, "thread_handle", required: true);
		var kind = ArgString(args, "kind", required: true);
		var upstreamKind = kind switch {
			"into" => DbgStepKind.StepInto,
			"over" => DbgStepKind.StepOver,
			"out" => DbgStepKind.StepOut,
			_ => (DbgStepKind?)null,
		};
		if (upstreamKind is null)
			return Fail(coordinator, DomainErrorCodes.InternalError, message: "kind must be into, over or out");
		lock (sessionLock) {
			if (currentStep is not null)
				return Fail(coordinator, DomainErrorCodes.InvalidState, message: "a step is already pending for this session");
		}
		var stepThreadError = ClassifyThreadHandle(threadHandle, out var stepTid);
		if (stepThreadError is not null)
			return Fail(coordinator, stepThreadError, message: stepThreadError == DomainErrorCodes.StaleHandle ? "thread_handle belongs to an earlier pause" : "unknown thread_handle");
		string stepId = $"step-{Interlocked.Increment(ref stepSeq)}";
		lock (sessionLock)
			currentStep = new StepRegistration { Id = stepId, Kind = kind, ThreadHandle = threadHandle };
		bool stepped = false;
		PostVoidToDispatcherSync(() => {
			var thread = FindThreadByTid(stepTid);
			if (thread is null)
				return;
			var stepper = thread.CreateStepper();
			stepper.Step(upstreamKind.Value, autoClose: true);
			stepped = true;
		});
		if (!stepped) {
			lock (sessionLock) currentStep = null;
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "thread vanished from the owned process");
		}
		coordinator.MarkResumed("step");
		return Ok(coordinator, new StepResultDto {
			StepId = stepId,
			State = DebugStates.Running,
		});
	}

	void PostVoidToDispatcherSync(Action action) {
		if (dbgManager is null)
			return;
		SpyInc("dispatcher_sync_posts");
		var done = new ManualResetEventSlim();
		dbgManager.Dispatcher.BeginInvoke(new Action(() => {
			try { action(); }
			finally { done.Set(); }
		}));
		done.Wait(ControlOperationRecord.DefaultDeadline);
	}

	// ---- IMP-008: locals and value expansion ----

	sealed class ValueHandleEntry {
		public string Handle = string.Empty;
		public int Epoch;
		public string ParentHandle = string.Empty;
		public int Depth;
		public string Name = string.Empty;
		public string Kind = "local";
		public string? Display;
		public int FrameIndex;
		public List<ValueHandleEntry> SnapshotChildren = new();
	}
	sealed class StringBufferWriter : dnSpy.Contracts.Debugger.Text.IDbgTextWriter {
		readonly System.Text.StringBuilder sb = new();
		public void Write(dnSpy.Contracts.Debugger.Text.DbgTextColor color, string? text) { if (text is not null) sb.Append(text); }
		public override string ToString() => sb.ToString();
	}
	readonly Dictionary<string, ValueHandleEntry> valueHandles = new();
	int valueHandleSeq;

	const dnSpy.Contracts.Debugger.Evaluation.DbgValueNodeEvaluationOptions FixedNodeOptions =
		dnSpy.Contracts.Debugger.Evaluation.DbgValueNodeEvaluationOptions.NoFuncEval
		| dnSpy.Contracts.Debugger.Evaluation.DbgValueNodeEvaluationOptions.RawView;

	/// <summary>Value handles are pause-epoch bound: any epoch change invalidates the registry
	/// and closes the snapshot's evaluation contexts (each is closed exactly once).</summary>
	void InvalidateStaleValueHandles() {
		int epoch = coordinator.PauseEpoch;
		foreach (var key in valueHandles.Where(kv => kv.Value.Epoch != epoch).Select(kv => kv.Key).ToList())
			valueHandles.Remove(key);
	}

	string GetLocals(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var frameHandle = ArgString(args, "frame_handle", required: true);
		var frameError = ClassifyFrameHandle(frameHandle, out var frameIndex);
		if (frameError is not null)
			return Fail(coordinator, frameError, message: frameError == DomainErrorCodes.StaleHandle ? "frame_handle belongs to an earlier pause" : "unknown frame_handle");
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 100));
		// The pause-epoch-bound immutable snapshot (CON-DYN-007/§3.5): evaluation objects do
		// not survive the dispatcher callback, so the whole breadth-first expansion (depth<=4,
		// 1024 nodes) is materialized here with pre-allocated handles; expand only pages it.
		var roots = new List<ValueHandleEntry>();
		bool truncated = false;
		PostVoidToDispatcherSync(() => {
			var frame = GetFrameByIndex(frameIndex);
			if (frame is null)
				return;
			var languages = languageService!.GetLanguages(frame.Runtime.RuntimeKindGuid);
			if (languages.Count == 0)
				return;
			var language = languages[0];
			var context = language.CreateContext(frame);
			if (context is null)
				return;
			try {
				var evalInfo = new dnSpy.Contracts.Debugger.Evaluation.DbgEvaluationInfo(context, frame, default);
				var locals = language.LocalsProvider.GetNodes(evalInfo, FixedNodeOptions,
					dnSpy.Contracts.Debugger.Evaluation.DbgLocalsValueNodeEvaluationOptions.ShowRawLocals);
				var queue = new Queue<(ValueHandleEntry entry, dnSpy.Contracts.Debugger.Evaluation.DbgValueNode node)>();
				foreach (var local in locals) {
					var entry = NewSnapshotEntry(local.ValueNode.Expression, local.Kind.ToString().ToLowerInvariant(), null, 0);
					entry.Display = FormatNode(evalInfo, local.ValueNode);
					roots.Add(entry);
					if (entry.Depth < 4)
						queue.Enqueue((entry, local.ValueNode));
				}
				int nodeCount = roots.Count;
				try {
				const int nodeCap = 1024;
				while (queue.Count > 0 && nodeCount < nodeCap) {
					var (parent, node) = queue.Dequeue();
					ulong childCount;
					try { childCount = node.GetChildCount(evalInfo); }
					catch { continue; }
					var children = node.GetChildren(evalInfo, 0, (int)Math.Min(100, (long)childCount), FixedNodeOptions);
					foreach (var child in children) {
						if (nodeCount >= nodeCap) { truncated = true; break; }
						var childEntry = NewSnapshotEntry(child.Expression, "child", parent, parent.Depth + 1);
						childEntry.Display = FormatNode(evalInfo, child);
						nodeCount++;
						if (childEntry.Depth < 4)
							queue.Enqueue((childEntry, child));
					}
				}
				}
				catch (Exception ex) {
					if (roots.Count > 0)
						roots[0].Display += "  BFSERR[" + ex.GetType().Name + ": " + ex.Message + "]";
				}
			}
			finally {
				context.Close();
			}
		});
		if (roots.Count == 0)
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "frame not found or has no locals");
		var items = new List<object>();
		foreach (var entry in roots.Take(pageSize))
			items.Add(ValueNodeDtoOf(entry));
		var dto = new LocalsResultDto { Items = items, Truncated = truncated, TotalKnown = roots.Count };
		if (pageSize < roots.Count)
			dto.NextPageCursor = pageSize.ToString();
		dto.Budgets = BudgetsUsed();
		return Ok(coordinator, dto, untrustedSampleData: true);
	}

	object BudgetsUsed() {
		int handles, nodes;
		int maxDepth = 0;
		lock (sessionLock) {
			int epoch = coordinator.PauseEpoch;
			handles = valueHandles.Count(kv => kv.Value.Epoch == epoch);
			nodes = handles;
			foreach (var kv in valueHandles) {
				if (kv.Value.Epoch == epoch && kv.Value.Depth > maxDepth)
					maxDepth = kv.Value.Depth;
			}
		}
		return new {
			depth_limit = 4, node_limit = 1024, value_handle_limit = 4096,
			string_utf8_limit = 65536, response_utf8_limit = 8388608,
			depth_used = maxDepth, nodes_used = nodes, value_handles_used = handles,
		};
	}

	ValueHandleEntry NewSnapshotEntry(string name, string kind, ValueHandleEntry? parent, int depth) {
		var entry = new ValueHandleEntry {
			Handle = $"val-{Interlocked.Increment(ref valueHandleSeq)}",
			Epoch = coordinator.PauseEpoch,
			ParentHandle = parent?.Handle ?? string.Empty,
			Depth = depth,
			Name = name,
			Kind = kind,
			FrameIndex = -1,
		};
		if (parent is not null)
			parent.SnapshotChildren.Add(entry);
		lock (sessionLock) valueHandles[entry.Handle] = entry;
		return entry;
	}

	string ExpandValue(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var valueHandle = ArgString(args, "value_handle", required: true);
		ValueHandleEntry? parent;
		lock (sessionLock)
			valueHandles.TryGetValue(valueHandle, out parent);
		if (parent is null || parent.Epoch != coordinator.PauseEpoch)
			return Fail(coordinator, DomainErrorCodes.StaleHandle, message: "value handle is not valid in this pause epoch");
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 100));
		string? cursor = ArgString(args, "page_cursor");
		int startAt = !string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var c) ? c : 0;
		var children = parent.SnapshotChildren;
		var page = children.Skip(startAt).Take(pageSize).ToList();
		var dto = new LocalsResultDto {
			Items = page.Select(e => (object)ValueNodeDtoOf(e)).ToList(),
			Truncated = false,
			TotalKnown = children.Count,
		};
		if (startAt + page.Count < children.Count)
			dto.NextPageCursor = (startAt + page.Count).ToString();
		dto.Budgets = BudgetsUsed();
		return Ok(coordinator, dto, untrustedSampleData: true);
	}

	/// <summary>Fixed NoDebuggerDisplay formatting (CON-DYN-007): never ToString/FuncEval.</summary>
	static string FormatNode(dnSpy.Contracts.Debugger.Evaluation.DbgEvaluationInfo evalInfo,
		dnSpy.Contracts.Debugger.Evaluation.DbgValueNode node) {
		if (node.HasError)
			return node.ErrorMessage ?? "error";
		var writer = new StringBufferWriter();
		node.FormatValue(evalInfo, writer,
			dnSpy.Contracts.Debugger.Evaluation.DbgValueFormatterOptions.NoDebuggerDisplay,
			System.Globalization.CultureInfo.InvariantCulture);
		return writer.ToString();
	}

	DbgStackFrame? GetFrameByIndex(int index) {
		DbgProcess? process;
		lock (sessionLock) process = ownedProcess;
		if (process is null || process.Threads.Length == 0)
			return null;
		var walker = process.Threads[0].CreateStackWalker();
		try {
			var frames = walker.GetNextStackFrames(index + 1);
			return index < frames.Length ? frames[index] : null;
		}
		finally {
			walker.Close();
		}
	}

	dnSpy.Contracts.Debugger.Evaluation.DbgEvaluationContext? CreateEvaluationContext(DbgStackFrame frame) {
		if (languageService is null)
			return null;
		var languages = languageService.GetLanguages(frame.Runtime.RuntimeKindGuid);
		if (languages.Count == 0)
			return null;
		return languages[0].CreateContext(frame.Runtime, frame.Location, 0, TimeSpan.FromSeconds(10), default);
	}

	ValueNodeDto ValueNodeDtoOf(ValueHandleEntry entry) {
		string? unavailable = entry.Display is not null && entry.Display.Contains("内部调试器错误")
			? "requires_function_evaluation"
			: null;
		return new ValueNodeDto {
			ValueHandle = entry.Handle,
			ParentValueHandle = entry.ParentHandle.Length == 0 ? null : entry.ParentHandle,
			Depth = entry.Depth,
			Name = entry.Name,
			Kind = entry.Kind,
			Display = entry.Display,
			HasChildren = entry.SnapshotChildren.Count > 0,
			IsNull = entry.Display == "null",
			Truncated = false,
			UnavailableReason = unavailable,
		};
	}

	// ---- IMP-009: modules, memory, artifacts ----

	ArtifactStoreLedger? artifactLedger;
	readonly Dictionary<string, FileStream> artifactSessionHandles = new();
	string? ArtifactRootPath => settings.CurrentSnapshot?.ArtifactRoot is { Length: > 0 } root ? root : null;

	sealed class ProductionArtifactFs : IArtifactStoreFs {
		readonly string root;
		public ProductionArtifactFs(string root) => this.root = root;
		string SessionDir(string sessionId) => Path.Combine(root, sessionId);
		public IReadOnlyList<string> EnumerateRootChildren() =>
			Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList() : new List<string>();
		public IReadOnlyList<string> EnumerateSessionChildren(string sessionId) {
			var dir = SessionDir(sessionId);
			return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList() : new List<string>();
		}
		public bool SessionDirectoryExists(string sessionId) => Directory.Exists(SessionDir(sessionId));
		public (string VolumeSerial, string FileId, long Length)? ObserveChild(string sessionId, string relativeName) {
			try {
				var path = Path.Combine(SessionDir(sessionId), relativeName);
				using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				var info = GetFileIdentity(lease);
				return ($"0x{info.VolumeSerial:x8}", $"{info.FileIndexHigh:x16}{info.FileIndexLow:x16}".Substring(0, 32), lease.Length);
			}
			catch { return null; }
		}
		public void CreateSessionDirectory(string sessionId) => Directory.CreateDirectory(SessionDir(sessionId));
		public void CreateChildFile(string sessionId, string relativeName, long length) {
			var path = Path.Combine(SessionDir(sessionId), relativeName);
			using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
			if (length > 0)
				fs.SetLength(length);
		}
	}

	ArtifactStoreLedger ArtifactLedger() {
		if (artifactLedger is null) {
			var root = ArtifactRootPath ?? throw new InvalidOperationException("artifact root not configured");
			artifactLedger = new ArtifactStoreLedger(new ProductionArtifactFs(root));
			artifactLedger.Initialize();
		}
		return artifactLedger;
	}

	string ListModules(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState);
		int pageSize = (int)Math.Min(100, ArgLong(args, "page_size", 100));
		string? cursor = ArgString(args, "page_cursor");
		int start = !string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var c) ? c : 0;
		var modules = RegisterLiveModules();
		var page = modules.Skip(start).Take(pageSize).ToList();
		var dto = new PagedItemsDto {
			Items = page.Select(m => (object)ModuleDtoOf(m)).ToList(),
			Truncated = false,
			TotalKnown = modules.Count,
		};
		if (start + page.Count < modules.Count)
			dto.NextPageCursor = (start + page.Count).ToString();
		return Ok(coordinator, dto, untrustedSampleData: true);
	}

	ModuleIdentityDto ModuleDtoOf(RegisteredModuleRecord m) => new() {
		ModuleHandle = m.ModuleHandle,
		RuntimeHandle = m.RuntimeHandle,
		Name = m.Name,
		Path = string.IsNullOrEmpty(m.Filename) ? null : m.Filename,
		Mvid = m.Mvid,
		Sha256 = m.Sha256,
		BaseAddress = (long)m.Address,
		Size = m.Size,
		Layout = m.Layout,
		IdentityStrength = string.IsNullOrEmpty(m.Filename) ? "runtime_weak" : "disk_strong",
	};

	string ReadMemory(Dictionary<string, object>? args) {
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var moduleHandle = ArgString(args, "module_handle", required: true);
		var address = ParseUlong(ArgString(args, "address", required: true));
		var lengthLong = ArgLong(args, "length", 0);
		var encoding = ArgString(args, "encoding");
		if (encoding.Length == 0)
			encoding = "hex";
		// Schema/metadata bound: 1..65536 is a -32602 rejection (the schema maximum), never a
		// truncated int — an unsafe-integer length lands here too instead of aliasing to 1.
		if (lengthLong <= 0 || lengthLong > 65536)
			throw new ArgumentException("length must be within 1..65536", "length");
		var length = (int)lengthLong;
		// API-DYN-023: overflow-safe range predicate only — never compute address+length.
		if (address > ulong.MaxValue - (ulong)length)
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: "address range overflows the address space");
		// The read must lie inside the named module (subtraction predicate, no overflow).
		RegisteredModuleRecord? rangeModule;
		lock (sessionLock)
			modulesByHandle.TryGetValue(moduleHandle, out rangeModule);
		if (rangeModule is null)
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: "module_handle is not a registered module of this pause");
		var moduleEndDelta = (ulong)(rangeModule.Address + rangeModule.Size) - (ulong)rangeModule.Address;
		if (address < (ulong)rangeModule.Address || (address - (ulong)rangeModule.Address) > moduleEndDelta - (ulong)length)
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: "address range lies outside the module");
		byte[]? data = null;
		string? error = null;
		PostVoidToDispatcherSync(() => {
			DbgProcess? process;
			lock (sessionLock) process = ownedProcess;
			if (process is null)
				return;
			try {
				SpyInc("read_memory_executions");
				data = process.ReadMemory(address, length);
			}
			catch (Exception ex) {
				error = ex.Message;
			}
		});
		if (data is null)
			return Fail(coordinator, DomainErrorCodes.NotFound, message: error ?? "the address range is not readable");
		return Ok(coordinator, untrustedSampleData: true, result: new ReadMemoryResultDto {
			ModuleHandle = moduleHandle,
			Address = $"0x{address:x}",
			Length = data.Length,
			Encoding = encoding,
			Data = encoding == "base64" ? Convert.ToBase64String(data) : ConvertHexShim.ToHexString(data).ToLowerInvariant(),
			ReadSemantics = "dnspy-zero-fill",
		});
	}

	static string? FindReparseComponent(string path) {
		try {
			var full = System.IO.Path.GetFullPath(path);
			var current = full;
			while (!string.IsNullOrEmpty(current)) {
				System.IO.FileAttributes attrs;
				try { attrs = System.IO.File.GetAttributes(current); }
				catch (System.IO.FileNotFoundException) { return null; }   // not-yet-created tail is fine
				catch (System.IO.DirectoryNotFoundException) { return null; }
				if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0)
					return current;
				var parent = System.IO.Path.GetDirectoryName(current);
				if (string.IsNullOrEmpty(parent))
					return null;
				current = parent;
			}
		}
		catch {
			return null;
		}
		return null;
	}

	static ulong ParseUlong(string text) {
		var t = text.Trim();
		if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			return Convert.ToUInt64(t.Substring(2), 16);
		return Convert.ToUInt64(t);
	}

	string DumpModule(Dictionary<string, object>? args) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args) || !PausedEpochMatches(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var requestId = ArgString(args, "request_id", required: true);
		var moduleHandle = ArgString(args, "module_handle", required: true);
		var relativeName = ArgString(args, "relative_name");
		var root = ArtifactRootPath;
		if (string.IsNullOrEmpty(root))
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "ArtifactRoot is not configured");

		RegisteredModuleRecord module;
		lock (sessionLock) {
			if (!modulesByHandle.TryGetValue(moduleHandle, out module!))
				return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown module_handle");
		}
		// IRawModuleBytesSource, raw state: a disk-backed module dumps its on-disk image bytes.
		// In-memory/dynamic modules are raw_unavailable: v1 production returns the closed
		// CAPABILITY_UNAVAILABLE state (the reconstructed path stays out of v1 wiring).
		if (module.Layout != "file" || !File.Exists(module.Filename))
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "module has no on-disk raw image (dynamic/in-memory)");
		var sourceBytes = File.ReadAllBytes(module.Filename);
		if (sourceBytes.Length > ArtifactStoreLedger.MaxFileBytes)
			return Fail(coordinator, DomainErrorCodes.LimitExceeded, message: "module exceeds the 512 MiB artifact file cap");

		var sessionId = coordinator.ActiveSessionId!;
		if (string.IsNullOrEmpty(relativeName))
			relativeName = Path.GetFileName(module.Filename);
		relativeName = relativeName.Replace("..", "_").Replace('/', '_').Replace('\\', '_');
		var childName = relativeName + ".bin";

		ArtifactStoreLedger ledger;
		try {
			ledger = ArtifactLedger();
		}
		catch (Exception ex) {
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: ex.Message);
		}
		var admitSession = ledger.AdmitNewSession(sessionId);
		if (admitSession != ArtifactStoreLedger.AdmitResult.Ok && admitSession != ArtifactStoreLedger.AdmitResult.AlreadyExists)
			return Fail(coordinator, MapAdmit(admitSession));

		var fs = new ProductionArtifactFs(root);
		try {
			// Every file in a ledgered session directory must be an admitted child — the marker
			// included — or the next admission's fail-closed verification rejects the store.
			string markerSha;
			using (var sha = SHA256.Create())
				markerSha = ConvertHexShim.ToHexString(sha.ComputeHash(Array.Empty<byte>())).ToLowerInvariant();
			var markerAdmit = ledger.AdmitArtifactWrite(sessionId, ".session-marker", 0,
				new ArtifactStoreLedger.ChildRecord(".session-marker", "0x0", new string('0', 32), 0, markerSha));
			if (markerAdmit != ArtifactStoreLedger.AdmitResult.Ok)
				return Fail(coordinator, MapAdmit(markerAdmit));
			var markerPath = Path.Combine(root, sessionId, ".session-marker");
			lock (artifactSessionHandles) {
				if (!artifactSessionHandles.ContainsKey(sessionId))
					artifactSessionHandles[sessionId] = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			}

			string sha256;
			using (var sha = SHA256.Create())
				sha256 = ConvertHexShim.ToHexString(sha.ComputeHash(sourceBytes)).ToLowerInvariant();
			// The admission reserves the quota and creates the empty child; this call is its
			// active writer, so the bytes go straight into the ledgered file.
			var admit = ledger.AdmitArtifactWrite(sessionId, childName, sourceBytes.Length,
				new ArtifactStoreLedger.ChildRecord(childName, "0x0", new string('0', 32), sourceBytes.Length, sha256));
			if (admit != ArtifactStoreLedger.AdmitResult.Ok)
				return Fail(coordinator, MapAdmit(admit));
			var finalPath = Path.Combine(root, sessionId, childName);
			File.WriteAllBytes(finalPath, sourceBytes);

			var manifestName = childName + ".manifest.json";
			var manifest = System.Text.Json.JsonSerializer.Serialize(new {
				schema_version = "dnspy.mcp.artifact.v1",
				session_id = sessionId,
				module = module.Name,
				module_path = module.Filename,
				kind = "raw",
				layout = "file",
				size = sourceBytes.Length,
				sha256,
				created_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
			});
			var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest);
			var manifestAdmit = ledger.AdmitArtifactWrite(sessionId, manifestName, manifestBytes.Length,
				new ArtifactStoreLedger.ChildRecord(manifestName, "0x0", new string('0', 32), manifestBytes.Length, sha256));
			if (manifestAdmit != ArtifactStoreLedger.AdmitResult.Ok)
				return Fail(coordinator, MapAdmit(manifestAdmit));
			File.WriteAllBytes(Path.Combine(root, sessionId, manifestName), manifestBytes);

			return Ok(coordinator, untrustedSampleData: true, result: new DumpModuleResultDto {
				Artifact = new ArtifactDto {
					ArtifactId = sessionId + "/" + childName,
					Path = finalPath,
					Kind = "raw",
					Layout = "file",
					Size = sourceBytes.Length,
					Sha256 = sha256,
					SourceModule = ModuleDtoOf(module),
					ManifestPath = Path.Combine(root, sessionId, manifestName),
				},
			});
		}
		catch (Exception ex) {
			return Fail(coordinator, DomainErrorCodes.InternalError, message: ex.GetType().Name + ": " + ex.Message);
		}
	}

	static string MapAdmit(ArtifactStoreLedger.AdmitResult result) => result switch {
		ArtifactStoreLedger.AdmitResult.AlreadyExists => DomainErrorCodes.AlreadyExists,
		ArtifactStoreLedger.AdmitResult.LimitExceeded => DomainErrorCodes.LimitExceeded,
		ArtifactStoreLedger.AdmitResult.TargetMismatch => DomainErrorCodes.TargetMismatch,
		_ => DomainErrorCodes.InternalError,
	};

	// ---- observation pump (DbgManager dispatcher thread) ----

	void OnProcessesChanged(object? sender, DbgCollectionChangedEventArgs<DbgProcess> e) {
		foreach (var process in e.Objects) {
			if (e.Added) {
				TaskCompletionSource<bool>? claimTcs;
				bool startsPaused;
				string? reason;
				lock (sessionLock) {
					claimTcs = launchClaimTcs;
					startsPaused = pendingClaimStartsPaused;
					reason = pendingClaimReason;
				}
				SpyInc("launch_claim_candidates");
				if (coordinator.State == DebugStates.Starting && claimTcs is not null) {
					SpyInc("launch_claim_windows");
					lock (sessionLock) {
						ownedProcess = process;
						adapter = new DbgProcessControlAdapter(process);
						adapter.Observation += OnAdapterObservation;
					}
				process.IsRunningChanged += OnOwnedIsRunningChanged;
				RegisterModules(process);
				// Module load/unload events (EVT-DYN-006/007): runtimes may not exist yet at
				// claim time, so subscribe to the runtime collection as well — each runtime's
				// module changes then flow on the DbgManager dispatcher and the fresh module
				// table is registered so events and list_modules share identity.
				SubscribeModuleEvents(process);
				process.RuntimesChanged += OnOwnedRuntimesChanged;
				if (testExitBeforeClaim) {
					// Simulated pre-claim exit: the process vanished before ownership settled.
					// Tear down the half-claimed process without settling the claim; the
					// launch caller times out into start_failed while no session is created.
					testExitBeforeClaim = false;
					SpyInc("test_preclaim_exits");
					process.IsRunningChanged -= OnOwnedIsRunningChanged;
					_ = coordinator.ObserveProcessRemoved(coordinator.ActiveSessionId, coordinator.Generation, ownedIdentityMatch: false, exitCode: 0);
					lock (sessionLock) {
						adapter?.Dispose();
						adapter = null;
						ownedProcess = null;
						// Settle the claim negatively so the waiting launch caller takes its
						// TIMEOUT path (MarkLaunchFailed + start_failed + idle) instead of
						// hanging on the real 30s claim wait.
						claimTcs?.TrySetResult(false);
					}
					continue;
				}
				coordinator.MarkLaunchClaimSucceeded(startsPaused, reason);
				claimTcs.TrySetResult(true);
				}
			}
			else {
				DbgProcess? owned;
				lock (sessionLock) owned = ownedProcess;
				if (process != owned)
					continue;
				process.IsRunningChanged -= OnOwnedIsRunningChanged;
				var result = coordinator.ObserveProcessRemoved(coordinator.ActiveSessionId, coordinator.Generation, ownedIdentityMatch: true, exitCode: null);
				lock (sessionLock) {
					adapter?.Dispose();
					adapter = null;
					ownedProcess = null;
					// A step pending at removal never gets its StepComplete; a stale registration
					// would block every future step in this process ("a step is already pending").
					currentStep = null;
				}
				TaskCompletionSource<string>? controlTcs;
				lock (sessionLock) controlTcs = controlOutcomeTcs;
				if (result.Outcome == "pending-restart")
					controlTcs?.TrySetResult("removed-pending-restart");
			else {
				controlTcs?.TrySetResult("removed");
				ReleaseLeases();
				lock (sessionLock) {
					bpStore = new DebugBreakpointStore();
					mcpIdByDnSpyBreakpoint.Clear();
					dnSpyIdByMcpBreakpoint.Clear();
					dnSpyBreakpointsByMcp.Clear();
					modulesByHandle.Clear();
				}
			}
			}
		}
	}


	void SubscribeModuleEvents(DbgProcess process) {
		foreach (var runtime in process.Runtimes)
			runtime.ModulesChanged += OnOwnedModulesChanged;
	}

	void OnOwnedRuntimesChanged(object? sender, DbgCollectionChangedEventArgs<DbgRuntime> e) {
		if (sender is not DbgProcess process)
			return;
		SubscribeModuleEvents(process);
	}

	// DbgManager dispatcher: a module appeared/vanished in the owned process. The live table
	// re-registers first so the event can carry the same module_handle list_modules mints.
	void OnOwnedModulesChanged(object? sender, DbgCollectionChangedEventArgs<DbgModule> e) {
		try {
			// We are already on the DbgManager dispatcher: enumerate inline (a sync post from
			// this thread would self-wait on the dispatcher and stall the debug pump).
			var table = new List<RegisteredModuleRecord>();
			RegisterLiveModulesInto(table);
			foreach (var module in e.Objects) {
				var added = e.Added;
				var record = table.FirstOrDefault(m => string.Equals(m.Filename, module.Filename ?? string.Empty, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(m.Name, module.Name, StringComparison.OrdinalIgnoreCase));
				if (added) {
					coordinator.WriteModuleLoaded(new {
						module_handle = record?.ModuleHandle ?? "",
						name = module.Name,
						path = module.Filename,
						layout = module.IsDynamic || string.IsNullOrEmpty(module.Filename) ? "memory" : "file",
					});
				}
				else {
					coordinator.WriteModuleUnloaded(new {
						module_handle = record?.ModuleHandle ?? "",
						name = module.Name,
					});
				}
			}
		}
		catch {
			// Event wiring must never disturb the debug pump.
		}
	}

	void OnOwnedIsRunningChanged(object? sender, EventArgs e) {
		DbgProcess? process;
		lock (sessionLock) process = ownedProcess;
		if (process is null)
			return;
		if (process.IsRunning) {
			// The authoritative resume observation: dnSpy auto-continues the initial CorDebug
			// create-break; an explicit continue marks the transition itself under the
			// continueInFlight guard, so the pump never races it.
			bool continueBusy;
			lock (sessionLock) continueBusy = continueInFlight;
			if (!continueBusy)
				coordinator.MarkResumed("auto");
			return;
		}
		// Real stop details come from the runtime's BreakInfos (exception/breakpoint/step/entry/
		// break); the synthesized "break" singleton remains only when no runtime reported any.
		var infos = CollectBreakInfos(process);
		if (infos.Count == 0)
			infos.Add(new BreakInfoObservation("break", 0));
		// Observations flow through the adapter seam (production raises here; the DNMCP_TEST
		// fake raises identical ones from debug_test_adapter emit).
		IDbgProcessControlAdapter? raiseTarget;
		lock (sessionLock) raiseTarget = (IDbgProcessControlAdapter?)testAdapter ?? adapter;
		if (raiseTarget is DbgProcessControlAdapter production)
			production.RaiseObservation(new ProcessObservation {
				Kind = ProcessObservation.ObservationKind.Paused,
				Pid = process.Id,
				StartedUtc = sessionStartedUtc,
				BreakInfos = infos,
			});
		else if (raiseTarget is FakeDbgProcessControlAdapter fake)
			fake.EmitPaused(process.Id, sessionStartedUtc, infos);
	}

	/// <summary>
	/// Single consumer of upstream paused/removal observations (dispatcher thread). Production
	/// raises via the adapter after IsRunningChanged; test emissions arrive identically.
	/// </summary>
	void OnAdapterObservation(ProcessObservation observation) {
		if (observation.Kind == ProcessObservation.ObservationKind.Paused) {
			var result = coordinator.ObservePaused(coordinator.ActiveSessionId, coordinator.Generation,
				ownedIdentityMatch: true, observation.BreakInfos);
			if (result.Accepted && result.SettledPauseRecord) {
				TaskCompletionSource<string>? controlTcs;
				lock (sessionLock) controlTcs = controlOutcomeTcs;
				controlTcs?.TrySetResult("paused");
			}
		}
		else if (observation.Kind == ProcessObservation.ObservationKind.Removed) {
			var result = coordinator.ObserveProcessRemoved(coordinator.ActiveSessionId, coordinator.Generation,
				ownedIdentityMatch: true, observation.ExitCode);
			TaskCompletionSource<string>? controlTcs;
			lock (sessionLock) controlTcs = controlOutcomeTcs;
			if (result.Outcome == "pending-restart")
				controlTcs?.TrySetResult("removed-pending-restart");
			else
				controlTcs?.TrySetResult("removed");
		}
	}

	/// <summary>Maps DbgRuntime.BreakInfos to arbiter observations: BoundBreakpoint→breakpoint
	/// (owned id resolved through the created-breakpoint registry), StepComplete→step,
	/// EntryPointBreak→entry, ExceptionThrown→exception qualified by the session policy,
	/// Break/ProgramBreak→break.</summary>
	List<BreakInfoObservation> CollectBreakInfos(DbgProcess process) {
		var list = new List<BreakInfoObservation>();
		string policy;
		lock (sessionLock) policy = exceptionPolicy;
		int ordinal = 0;
		foreach (var runtime in process.Runtimes) {
			foreach (var info in runtime.BreakInfos) {
				string kind = "other";
				string? ownedId = null;
				string? stepId = null;
				string? stepKind = null;
				bool policyPause = false;
				if (info.Kind == DbgBreakInfoKind.Message && info.Data is DbgMessageEventArgs msg) {
					switch (msg.Kind) {
						case DbgMessageKind.BoundBreakpoint:
							if (msg is DbgMessageBoundBreakpointEventArgs boundArgs) {
								kind = "breakpoint";
								lock (sessionLock)
									mcpIdByDnSpyBreakpoint.TryGetValue(boundArgs.BoundBreakpoint.Breakpoint.Id, out ownedId);
								if (ownedId is not null)
									bpStore.MarkBound(ownedId, true);
							}
							break;
						case DbgMessageKind.StepComplete:
							kind = "step";
							lock (sessionLock) {
								if (currentStep is { } pending) {
									stepId = pending.Id;
									stepKind = pending.Kind; // EVT-DYN-015 reports the registered kind
									currentStep = null; // only the registered step generates EVT-DYN-015
								}
							}
							break;
						case DbgMessageKind.EntryPointBreak:
							kind = "entry";
							break;
						case DbgMessageKind.ExceptionThrown:
							kind = "exception";
							if (msg is DbgMessageExceptionThrownEventArgs exArgs)
								policyPause = policy switch {
									"first_chance_and_unhandled" => true,
									"unhandled" => exArgs.Exception.IsUnhandled || exArgs.Exception.IsSecondChance,
									_ => false,
								};
							break;
						case DbgMessageKind.Break:
						case DbgMessageKind.ProgramBreak:
							kind = "break";
							break;
					}
				}
				list.Add(new BreakInfoObservation(kind, ordinal++, ownedId, stepId, policyPause, stepKind));
			}
		}
		return list;
	}

	void OnIsDebuggingChanged(object? sender, EventArgs e) { }

	// ---- file identity leases ----

	bool TryLeaseIdentity(string path, string role, string objectKind, string? expectedSha256, out FileIdentityDto? identity, out string? error) {
		identity = null;
		error = null;
		try {
			var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			identityLeases.Add(stream);
			var info = GetFileIdentity(stream);
			string sha256;
			using (var sha = SHA256.Create()) {
				stream.Position = 0;
				var hash = sha.ComputeHash(stream);
				stream.Position = 0;
				sha256 = ConvertHexShim.ToHexString(hash).ToLowerInvariant();
			}
			if (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase)) {
				error = $"{role} sha256 mismatch: expected {expectedSha256.ToLowerInvariant()}, file is {sha256}";
				return false;
			}
			identity = new FileIdentityDto {
				Role = role,
				ObjectKind = objectKind,
				FinalPath = Path.GetFullPath(path),
				VolumeSerial = $"0x{info.VolumeSerial:x8}",
				FileId = $"{info.FileIndexHigh:x16}{info.FileIndexLow:x16}".Substring(0, 32),
				Sha256 = sha256,
			};
			return true;
		}
		catch (Exception ex) {
			error = $"{role} identity lease failed: {ex.Message}";
			return false;
		}
	}

	void ReleaseLeases() {
		foreach (var lease in identityLeases)
			try { lease.Dispose(); } catch { }
		identityLeases.Clear();
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

	[StructLayout(LayoutKind.Sequential)]
	struct BY_HANDLE_FILE_INFORMATION {
		public uint FileAttributes;
		public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
		public uint VolumeSerial;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	static (uint VolumeSerial, uint FileIndexHigh, uint FileIndexLow) GetFileIdentity(FileStream stream) {
		if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var info))
			throw new IOException("GetFileInformationByHandle failed");
		return (info.VolumeSerial, info.FileIndexHigh, info.FileIndexLow);
	}

	/// <summary>Minimal PE/CLR runtime-family detection for auto/harness modes: a sibling
	/// runtimeconfig.json marks CoreCLR; a CLR header with an MSIL metadata stream defaults to
	/// net48. Unsupported binaries return null (caller maps to CAPABILITY_UNAVAILABLE).</summary>
	static string? DetectRuntimeFamily(string path) {
		try {
			if (File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
				return RuntimeFamilies.CoreClr;
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var br = new BinaryReader(fs);
			fs.Position = 0x3C;
			var peOffset = br.ReadInt32();
			fs.Position = peOffset + 4;
			if (br.ReadInt32() != 0x4550)
				return null;
			// Optional header magic decides the data-directory base: PE32 (0x10b) at 96,
			// PE32+ (0x20b) at 112; the CLR header is directory index 14.
			fs.Position = peOffset + 24;
			var magic = br.ReadUInt16();
			var dirBase = magic == 0x20B ? 112 : 96;
			fs.Position = peOffset + 24 + dirBase + 14 * 8;
			var comRva = br.ReadInt32();
			return comRva != 0 ? RuntimeFamilies.Net48 : null;
		}
		catch {
			return null;
		}
	}

	// ---- helpers ----

	static List<string> RequiredFor(ControlOperation operation) => operation switch {
		ControlOperation.Pause => new List<string> { DebugStates.Running },
		ControlOperation.Terminate => new List<string> { DebugStates.Running, DebugStates.Paused, DebugStates.Faulted },
		ControlOperation.Restart => new List<string> { DebugStates.Running, DebugStates.Paused },
		_ => new List<string>(),
	};


	/// <summary>
	/// Session-identity mismatch vs state mismatch (ACC-027): when a session IS active and the
	/// request names a DIFFERENT session_id, that is TARGET_MISMATCH (wrong target), never a
	/// bare INVALID_STATE; with no active session the state gate still answers INVALID_STATE.
	/// </summary>
	string? SessionIdentityError(Dictionary<string, object>? args) {
		var sessionId = ArgString(args, "session_id");
		var active = coordinator.ActiveSessionId;
		if (active is not null && !string.IsNullOrEmpty(sessionId) && sessionId != active)
			return DomainErrorCodes.TargetMismatch;
		return null;
	}

	bool SessionAndGenerationMatch(Dictionary<string, object>? args) {
		var sessionId = ArgString(args, "session_id", required: true);
		var generation = ArgInt(args, "generation", required: true);
		return sessionId == coordinator.ActiveSessionId && generation == coordinator.Generation;
	}

	static int CursorOf(DebugSessionCoordinator c) {
		var sessionId = c.ActiveSessionId ?? c.LastSessionId;
		if (sessionId is null)
			return 0;
		return (int)Math.Min(int.MaxValue, c.ReadEvents(sessionId, 0, int.MaxValue, null)?.NextCursor ?? 0);
	}

	static string Rfc3339(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

	static string ArgString(Dictionary<string, object>? args, string key, bool required = false) {
		if (args is not null && args.TryGetValue(key, out var raw) && raw is JsonElement je) {
			switch (je.ValueKind) {
				case JsonValueKind.String: return je.GetString() ?? string.Empty;
				case JsonValueKind.Number: return je.GetRawText();
				case JsonValueKind.True: return "true";
				case JsonValueKind.False: return "false";
			}
		}
		if (required)
			throw new ArgumentException($"{key} is required");
		return string.Empty;
	}

	static long ArgLong(Dictionary<string, object>? args, string key, long @default) {
		if (args is not null && args.TryGetValue(key, out var raw) && raw is JsonElement { ValueKind: JsonValueKind.Number } je)
			return je.TryGetInt64(out var v) ? v : @default;
		return @default;
	}

	static int ArgInt(Dictionary<string, object>? args, string key, bool required = false) {
		var v = ArgLong(args, key, int.MinValue);
		if (v == int.MinValue) {
			if (required)
				throw new ArgumentException($"{key} is required");
			return 0;
		}
		return (int)v;
	}

	static List<string>? ArgStrings(Dictionary<string, object>? args, string key) {
		if (args is not null && args.TryGetValue(key, out var raw) && raw is JsonElement { ValueKind: JsonValueKind.Array } je) {
			var list = new List<string>();
			foreach (var item in je.EnumerateArray())
				if (item.ValueKind == JsonValueKind.String)
					list.Add(item.GetString() ?? string.Empty);
			return list;
		}
		return new List<string>();
	}

	// ---- result DTOs ----

	public sealed class LaunchResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("session_id")] public string? SessionId { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("generation")] public int Generation { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("claim_deadline_utc")] public string ClaimDeadlineUtc { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("launch_mode")] public string LaunchMode { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("runtime_family")] public string RuntimeFamily { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("architecture")] public string Architecture { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("file_identities")] public List<FileIdentityDto> FileIdentities { get; set; } = new();
	}

	public sealed class StatusResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("active_session_id")] public string? ActiveSessionId { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("last_session_id")] public string? LastSessionId { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("owned_process")] public OwdProcessDto? OwnedProcess { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("observed_process_state")] public string ObservedProcessState { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("runtime_family")] public string? RuntimeFamily { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("architecture")] public string? Architecture { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("start_kind")] public string? StartKind { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("launch_mode")] public string? LaunchMode { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("fault")] public string? Fault { get; set; }
	}

	public sealed class OwdProcessDto {
		[System.Text.Json.Serialization.JsonPropertyName("process_handle")] public string ProcessHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("pid")] public int Pid { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("start_time_utc")] public string StartTimeUtc { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("filename")] public string Filename { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("image_identity")] public FileIdentityDto? ImageIdentity { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("runtime_identity")] public string RuntimeIdentity { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("runtime_family")] public string? RuntimeFamily { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("architecture")] public string? Architecture { get; set; }
	}

	public sealed class PauseResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("pause_epoch")] public int PauseEpoch { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("request_effect")] public string RequestEffect { get; set; } = string.Empty;
	}

	public sealed class ContinueResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("pause_epoch")] public int PauseEpoch { get; set; }
	}

	public sealed class TerminateResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("terminal_cursor")] public int TerminalCursor { get; set; }
	}

	public sealed class RestartResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("generation")] public int Generation { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("claim_deadline_utc")] public string ClaimDeadlineUtc { get; set; } = string.Empty;
	}

	public class EventsResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("events")] public List<object> Events { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("next_cursor")] public long NextCursor { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("earliest_cursor")] public long EarliestCursor { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("events_lost")] public long EventsLost { get; set; }
	}

	public sealed class WaitEventsResultDto : EventsResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("timed_out")] public bool TimedOut { get; set; }
	}

	public sealed class SetBreakpointResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("breakpoint")] public BreakpointDto Breakpoint { get; set; } = new();
	}

	public sealed class ListBreakpointsResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("items")] public List<BreakpointDto> Items { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("next_page_cursor")] public string? NextPageCursor { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("total_known")] public int TotalKnown { get; set; }
	}

	public sealed class RemoveBreakpointResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("removed")] public bool Removed { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("breakpoint_id")] public string BreakpointId { get; set; } = string.Empty;
	}

	public sealed class ExceptionPolicyResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("previous")] public ExceptionPolicyDto Previous { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("current")] public ExceptionPolicyDto Current { get; set; } = new();
	}

	public sealed class ExceptionPolicyDto {
		[System.Text.Json.Serialization.JsonPropertyName("break_on")] public string BreakOn { get; set; } = "unhandled";
	}

	public sealed class BreakpointDto {
		[System.Text.Json.Serialization.JsonPropertyName("breakpoint_id")] public string BreakpointId { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("owned")] public bool Owned { get; set; } = true;
		[System.Text.Json.Serialization.JsonPropertyName("enabled")] public bool Enabled { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("bound")] public bool Bound { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("module_identity")] public ModuleIdentityDto ModuleIdentity { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("method_token")] public string MethodToken { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("il_offset")] public int IlOffset { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("last_error")] public string? LastError { get; set; }
	}

	public sealed class PagedItemsDto {
		[System.Text.Json.Serialization.JsonPropertyName("items")] public List<object> Items { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("next_page_cursor")] public string? NextPageCursor { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("total_known")] public int TotalKnown { get; set; }
	}

	public sealed class ThreadInfoDto {
		[System.Text.Json.Serialization.JsonPropertyName("thread_handle")] public string ThreadHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("managed_id")] public string? ManagedId { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("os_id")] public string? OsId { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = "paused";
		[System.Text.Json.Serialization.JsonPropertyName("is_current")] public bool IsCurrent { get; set; }
	}

	public sealed class FrameInfoDto {
		[System.Text.Json.Serialization.JsonPropertyName("frame_handle")] public string FrameHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("index")] public int Index { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("location")] public LocationDto Location { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
	}

	public sealed class LocationDto {
		[System.Text.Json.Serialization.JsonPropertyName("module_handle")] public string ModuleHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("method_token")] public string? MethodToken { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("il_offset")] public int? IlOffset { get; set; }
	}

	public sealed class StepResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("step_id")] public string StepId { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = string.Empty;
	}

	public sealed class LocalsResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("items")] public List<object> Items { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("next_page_cursor")] public string? NextPageCursor { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("total_known")] public int TotalKnown { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("evaluation_mode")] public string EvaluationMode { get; set; } = "no_func_eval_raw";
		[System.Text.Json.Serialization.JsonPropertyName("budgets")] public object Budgets { get; set; } = new {
			depth_limit = 4, node_limit = 1024, value_handle_limit = 4096,
			string_utf8_limit = 65536, response_utf8_limit = 8388608,
			depth_used = 0, nodes_used = 0, value_handles_used = 0,
		};
	}

	public sealed class ValueNodeDto {
		[System.Text.Json.Serialization.JsonPropertyName("value_handle")] public string? ValueHandle { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("parent_value_handle")] public string? ParentValueHandle { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("depth")] public int Depth { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("kind")] public string Kind { get; set; } = "local";
		[System.Text.Json.Serialization.JsonPropertyName("display")] public string? Display { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("has_children")] public bool HasChildren { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("is_null")] public bool IsNull { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("truncated")] public bool Truncated { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("unavailable_reason")] public string? UnavailableReason { get; set; }
	}

	public sealed class ReadMemoryResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("module_handle")] public string ModuleHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("length")] public int Length { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("encoding")] public string Encoding { get; set; } = "hex";
		[System.Text.Json.Serialization.JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("read_semantics")] public string ReadSemantics { get; set; } = "dnspy-zero-fill";
	}

	public sealed class DumpModuleResultDto {
		[System.Text.Json.Serialization.JsonPropertyName("artifact")] public ArtifactDto Artifact { get; set; } = new();
	}

	public sealed class ArtifactDto {
		[System.Text.Json.Serialization.JsonPropertyName("artifact_id")] public string ArtifactId { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("kind")] public string Kind { get; set; } = "raw";
		[System.Text.Json.Serialization.JsonPropertyName("layout")] public string Layout { get; set; } = "file";
		[System.Text.Json.Serialization.JsonPropertyName("size")] public int Size { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("source_module")] public ModuleIdentityDto SourceModule { get; set; } = new();
		[System.Text.Json.Serialization.JsonPropertyName("manifest_path")] public string ManifestPath { get; set; } = string.Empty;
	}

	public sealed class ModuleIdentityDto {
		[System.Text.Json.Serialization.JsonPropertyName("module_handle")] public string ModuleHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("runtime_handle")] public string RuntimeHandle { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("path")] public string? Path { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("mvid")] public string Mvid { get; set; } = string.Empty;
		[System.Text.Json.Serialization.JsonPropertyName("sha256")] public string? Sha256 { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("base_address")] public long BaseAddress { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
		[System.Text.Json.Serialization.JsonPropertyName("layout")] public string Layout { get; set; } = "file";
		[System.Text.Json.Serialization.JsonPropertyName("identity_strength")] public string IdentityStrength { get; set; } = "strong";
	}
}
