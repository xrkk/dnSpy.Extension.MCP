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
using dnSpy.Contracts.Debugger.DotNet.Metadata;
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
	internal static void SpyInc(string name) => SpyCounters.AddOrUpdate(name, 1, (_, v) => v + 1);
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
	readonly List<Microsoft.Win32.SafeHandles.SafeFileHandle> identityDirectoryLeases = new();
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
	readonly StaticWriteGate staticWriteGate;
	readonly DbgMetadataService? metadataService;
	readonly DbgCodeBreakpointsService? breakpointsService;
	readonly DbgDotNetCodeLocationFactory? locationFactory;
	readonly dnSpy.Contracts.Debugger.Evaluation.DbgLanguageService? languageService;
	DebugBreakpointStore bpStore = new();
	readonly Dictionary<int, string> mcpIdByDnSpyBreakpoint = new();
	readonly Dictionary<string, int> dnSpyIdByMcpBreakpoint = new();
	readonly Dictionary<string, List<DbgCodeBreakpoint>> dnSpyBreakpointsByMcp = new();
	/// <summary>ACC-035: owned breakpoint id -> the live module its module_handle addressed.
	/// dnSpy binds one breakpoint against every ModuleId-equal module (same MVID/name), so hit
	/// attribution is scoped here by object identity — a sibling module's hit never settles as
	/// this breakpoint's EVT-DYN-013.</summary>
	readonly Dictionary<string, DbgModule?> moduleByOwnedBp = new();

	/// <summary>The upstream location identity for a live module. In-memory modules (no
	/// filename) need the engine's own ModuleId — built from the same ModuleDef the metadata
	/// service loads, exactly like dndbg does — or the engine never binds (ACC-035); disk-backed
	/// modules keep the filename identity.</summary>
	static bool HasDiskPath(DbgModule module) =>
		!string.IsNullOrEmpty(module.Filename) && Path.IsPathRooted(module.Filename);

	/// <summary>Reads the runtime's authoritative module identity.  The reflection model is
	/// preferred because it is the identity used by the active debug engine; ForceMemory is
	/// the fallback for engines which have not published a reflection module yet.</summary>
	string AuthoritativeMvidOf(DbgModule module) {
		try {
			var runtimeMvid = module.GetReflectionModule()?.ModuleVersionId ?? Guid.Empty;
			if (runtimeMvid != Guid.Empty)
				return runtimeMvid.ToString("D").ToLowerInvariant();
		}
		catch {
			// Some engines create the reflection facade lazily.  Metadata below is authoritative
			// as well and also supports dynamic/in-memory modules.
		}
		try {
			var definition = metadataService?.TryGetMetadata(module,
				DbgLoadModuleOptions.ForceMemory | DbgLoadModuleOptions.AutoLoaded);
			var metadataMvid = definition?.Mvid ?? Guid.Empty;
			return metadataMvid.ToString("D").ToLowerInvariant();
		}
		catch {
			return Guid.Empty.ToString("D");
		}
	}

	static string? TryHashDiskModule(DbgModule module) {
		if (!HasDiskPath(module))
			return null;
		try {
			using var stream = new FileStream(module.Filename, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 1024 * 64, FileOptions.SequentialScan);
			using var sha = SHA256.Create();
			return ConvertHexShim.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
		}
		catch {
			return null;
		}
	}

	/// <summary>Accept only an exact IL instruction start in a concrete MethodDef body.
	/// Token-table shape alone is insufficient: body-out and mid-instruction offsets are
	/// protocol parameter errors (ACC-011).</summary>
	bool IsInstructionBoundary(RegisteredModuleRecord module, uint token, int ilOffset) {
		if (module.LiveModule is null || ilOffset < 0 || metadataService is null)
			return false;
		try {
			var definition = metadataService.TryGetMetadata(module.LiveModule,
				DbgLoadModuleOptions.ForceMemory | DbgLoadModuleOptions.AutoLoaded);
			var method = definition?.ResolveToken(token) as dnlib.DotNet.MethodDef;
			return method?.Body is not null
				&& method.Body.Instructions.Any(instruction => instruction.Offset == (uint)ilOffset);
		}
		catch {
			return false;
		}
	}

	ModuleId UpstreamIdOf(DbgModule module) {
		if (HasDiskPath(module))
			return (ModuleId)module.Filename;
		// In-memory/dynamic modules: the ENGINE's own id is the only one that binds — dndbg
		// serializes them as "<name> (id=N)" so same-bytes siblings get distinct ids, which is
		// exactly the module_handle disambiguation ACC-035 needs.
		try {
			var id = module.Runtime.GetDotNetRuntime().GetModuleId(module);
			SpyCounters["upstream_id:" + id.ModuleName + "|mem" + (id.IsInMemory ? 1 : 0) + "dyn" + (id.IsDynamic ? 1 : 0) + "nameOnly" + (id.ModuleNameOnly ? 1 : 0)] = 1;
			return id;
		}
		catch (Exception ex) {
			SpyCounters["upstream_id_exc:" + ex.GetType().Name] = 1;
			// non-dotnet runtime or engine refusal: name-only in-memory guess (won't bind)
			return ModuleId.Create(null, module.Name, module.IsDynamic, isInMemory: true, moduleNameOnly: true);
		}
	}
	readonly Dictionary<string, RegisteredModuleRecord> modulesByHandle = new();
	string exceptionPolicy = "unhandled";

	sealed class RegisteredModuleRecord {
		public string ModuleHandle = string.Empty;
		/// <summary>Live upstream module (dispatcher-only access): same-MVID sibling modules
		/// are disambiguated by object identity for module_handle-scoped breakpoints (ACC-035).</summary>
		public DbgModule? LiveModule;
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
		McpSettings settings,
		[Import(AllowDefault = true)] StaticWriteGate? staticWriteGate,
		[Import(AllowDefault = true)] DbgMetadataService? metadataService) {
		this.dbgManager = dbgManager;
		this.breakpointsService = breakpointsService;
		this.metadataService = metadataService;
		this.locationFactory = locationFactory;
		this.languageService = languageService;
		this.gateService = gateService;
		this.settings = settings;
		settings.SetActiveSessionProbe(() => coordinator.ActiveSessionId is not null);
		this.staticWriteGate = staticWriteGate ?? new StaticWriteGate(() => false);
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
		artifactFs?.Dispose();
		foreach (var retired in retiredArtifactStores)
			retired.Dispose();
		retiredArtifactStores.Clear();
		foreach (var chain in rootChainLeases.Values)
			foreach (var lease in chain)
				lease.Dispose();
		rootChainLeases.Clear();
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
			case "ui_debugging":
				testUiDebugging = true;
				staticWriteGate.TestUiDebuggingHook = () => testUiDebugging;
				break;
			case "ui_debugging_off":
				testUiDebugging = false;
				break;
			case "foreign_process":
				// ACC-025-B injection: a process/runtime event the session never registered
				// (same classification path OnProcessesChanged uses for real foreign arrivals).
				MarkForeignProcessObserved(-1, sessionStartedUtc, "test://foreign-runtime", "test_family", "x64");
				break;
			case "manager_idle":
				// ACC-025-B recovery injection: the manager stopped debugging with no new
				// objects (UI ended all debugging) — same path OnIsDebuggingChanged uses.
				HandleManagerDebuggingChanged(false);
				break;
			default:
				throw new ArgumentException("mode must be fail_start, exit_before_claim, ui_debugging, ui_debugging_off, foreign_process or manager_idle", "mode");
		}
		return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["armed"] = mode });
	}

	string TestDump(Dictionary<string, object>? args) {
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var mode = args is not null && args.TryGetValue("mode", out var m) && m is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } mj ? mj.GetString() : null;
		switch (mode) {
			case "raw":
			case null:
				testDumpMode = null;
				break;
			case "force_memory":
			case "both_unavailable":
				testDumpMode = mode;
				break;
			default:
				throw new ArgumentException("mode must be raw, force_memory or both_unavailable", "mode");
		}
		return Ok(coordinator, new Dictionary<string, object?> { ["test_mode"] = true, ["armed"] = mode ?? "raw" });
	}

	sealed class TestSettingsIo : ISettingsSnapshotIO {
		public readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal);
		public string? FailKey;
		public int FailOnWriteNumber;
		int writes;
		public string? Read(string key) => Values.TryGetValue(key, out var value) ? value : null;
		public void Write(string key, string? value) {
			writes++;
			if (key == FailKey && (FailOnWriteNumber == 0 || writes == FailOnWriteNumber))
				throw new IOException("injected settings write failure");
			if (value is null) Values.Remove(key); else Values[key] = value;
		}
	}

	/// <summary>ACC-036 deterministic execution of the real two-key transaction/recovery
	/// implementation. It is deliberately unadvertised and available only in DNMCP_TEST.</summary>
	string TestSettings(Dictionary<string, object>? args) {
		_ = args;
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var old = McpSettingsSnapshot.SafeDefaults();
		var candidate = McpSettingsSnapshot.TryCreate(true, "localhost", 3000, true, true,
			@"C:\Tools\samples", @"C:\Tools\artifacts", Array.Empty<string>(), null, false, out _)!;
		Dictionary<string, object?> Run(string? failKey, bool failTransition, bool failClear) {
			var io = new TestSettingsIo();
			io.Values[McpSettingsPersistence.CommittedKey] = old.ToCanonicalJson();
			var store = new McpSettingsStore(io, null);
			var events = 0; var transitions = 0;
			store.SnapshotChanged += _ => events++;
			if (failKey != null) {
				io.FailKey = failKey;
				// Pending clear is the third write: pending, committed, clear.
				io.FailOnWriteNumber = failClear ? 3 : 0;
			}
			var result = store.Apply(candidate, snap => { transitions++; return !failTransition || ReferenceEquals(snap, old); }, () => { });
			return new Dictionary<string, object?> {
				["success"] = result.Success, ["failed_step"] = result.FailedStep.ToString(),
				["current_is_candidate"] = ReferenceEquals(store.Current, candidate),
				["events"] = events, ["transitions"] = transitions,
				["pending_present"] = io.Values.ContainsKey(McpSettingsPersistence.PendingKey),
				["warning"] = result.FixedMessage,
			};
		}
		var badUnknown = old.ToCanonicalJson().TrimEnd('}') + ",\"Unknown\":1}";
		var strictRejected = McpSettingsPersistence.TryParseEffective(badUnknown, out _) is null;
		var recovered = McpSettingsPersistence.Recover(badUnknown, candidate.ToCanonicalJson(), null);
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true,
			["pending_write_failure"] = Run(McpSettingsPersistence.PendingKey, false, false),
			["server_transition_failure"] = Run(null, true, false),
			["committed_write_failure"] = Run(McpSettingsPersistence.CommittedKey, false, false),
			["pending_clear_failure"] = Run(McpSettingsPersistence.PendingKey, false, true),
			["unknown_field_rejected"] = strictRejected,
			["invalid_committed_pending_not_activated"] = ReferenceEquals(recovered.Snapshot, McpSettingsSnapshot.SafeDefaults())
				|| !recovered.Snapshot.EnableServer,
			["null_peer_rejected"] = !dnSpy.Extension.MCP.Transport.CidrFilter.IsAllowed(null, new[] { "127.0.0.1/32" }),
		});
	}

	sealed class TestArtifactFs : IArtifactStoreFs {
		public readonly Dictionary<string, Dictionary<string, (string Volume, string Id, long Length)>> Tree = new(StringComparer.Ordinal);
		int nextId;
		public ArtifactStoreLedger.AdmitResult? InterruptAfterCreate;
		public IReadOnlyList<string> EnumerateRootChildren() => Tree.Keys.ToList();
		public IReadOnlyList<string> EnumerateSessionChildren(string sessionId) => Tree.TryGetValue(sessionId, out var c) ? c.Keys.ToList() : new List<string>();
		public bool SessionDirectoryExists(string sessionId) => Tree.ContainsKey(sessionId);
		public (string VolumeSerial, string FileId, long Length)? ObserveChild(string sessionId, string relativeName) =>
			Tree.TryGetValue(sessionId, out var c) && c.TryGetValue(relativeName, out var v) ? (v.Volume, v.Id, v.Length) : null;
		public void CreateSessionDirectory(string sessionId) => Tree.Add(sessionId, new Dictionary<string, (string, string, long)>(StringComparer.Ordinal));
		public ArtifactStoreLedger.ChildRecord CreateChildFile(string sessionId, string relativeName,
			long length, byte[]? payload, ArtifactOperationRecord? operation) {
			var value = (Volume: "vol", Id: "id-" + ++nextId, Length: length);
			Tree[sessionId].Add(relativeName, value);
			var record = new ArtifactStoreLedger.ChildRecord(relativeName, value.Volume, value.Id,
				length, new string('0', 64), InterruptAfterCreate is null ? "committed" : "aborted_owned");
			if (InterruptAfterCreate is { } interrupted) {
				InterruptAfterCreate = null;
				throw new ArtifactStoreLedger.ArtifactWriteInterruptedException(record, interrupted);
			}
			return record;
		}
	}

	/// <summary>ACC-019 deterministic execution of the production artifact ledger limits and
	/// cancellation phase machine through IArtifactStoreFs.</summary>
	string TestArtifact(Dictionary<string, object>? args) {
		_ = args;
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var fs = new TestArtifactFs();
		var ledger = new ArtifactStoreLedger(fs, maxSessions: 2, maxFile: 10, maxSession: 12, maxStore: 15);
		ledger.Initialize();
		var s1 = ledger.AdmitNewSession("s1");
		var atFile = ledger.AdmitArtifactReservationForTest("s1", "at-limit", 10);
		var beforeOver = fs.Tree["s1"].Count;
		var overFile = ledger.AdmitArtifactReservationForTest("s1", "over-file", 11);
		var overSession = ledger.AdmitArtifactReservationForTest("s1", "over-session", 3);
		var s2 = ledger.AdmitNewSession("s2");
		var storeAt = ledger.AdmitArtifactReservationForTest("s2", "store-at", 5);
		var storeOver = ledger.AdmitArtifactReservationForTest("s2", "store-over", 1);
		fs.Tree["external"] = new Dictionary<string, (string, string, long)>();
		var beforeMismatch = fs.Tree.Count;
		var mismatch = ledger.AdmitNewSession("s3");
		var operation = new ArtifactOperationRecord("r", "s");
		var active = operation.TryMarkActive();
		var canceling = operation.TryMarkCanceling();
		var settled = operation.TrySettle();
		var settledTwice = operation.TrySettle();

		var staleFs = new TestArtifactFs();
		staleFs.Tree["preexisting"] = new Dictionary<string, (string, string, long)>();
		var staleLedger = new ArtifactStoreLedger(staleFs);
		staleLedger.Initialize();
		var startupStale = staleLedger.AdmitNewSession("new");

		var retainedFs = new TestArtifactFs();
		var retainedLedger = new ArtifactStoreLedger(retainedFs, maxSessions: 2, maxFile: 10, maxSession: 10, maxStore: 10);
		retainedLedger.Initialize();
		retainedLedger.AdmitNewSession("r1");
		retainedLedger.AdmitArtifactReservationForTest("r1", "child", 10);
		var retained = retainedLedger.TerminalSession("r1");
		var retainedBytes = retainedLedger.LedgerBytes;
		var r2 = retainedLedger.AdmitNewSession("r2");
		var retainedOver = retainedLedger.AdmitArtifactReservationForTest("r2", "over", 1);
		retainedFs.Tree["r1"]["child"] = ("vol", "tampered", 10);
		var retainedTamper = retainedLedger.AdmitArtifactReservationForTest("r2", "after-tamper", 0);

		var interruptedFs = new TestArtifactFs();
		var interruptedLedger = new ArtifactStoreLedger(interruptedFs, maxSessions: 2, maxFile: 10, maxSession: 10, maxStore: 10);
		interruptedLedger.Initialize();
		interruptedLedger.AdmitNewSession("i1");
		interruptedFs.InterruptAfterCreate = ArtifactStoreLedger.AdmitResult.OperationCanceled;
		var interruptedResult = interruptedLedger.AdmitArtifactWrite("i1", "partial", new byte[4],
			new ArtifactOperationRecord("ir", "i1"));

		var expiredFs = new TestArtifactFs();
		var expiredLedger = new ArtifactStoreLedger(expiredFs);
		expiredLedger.Initialize(); expiredLedger.AdmitNewSession("e1");
		var expiredOp = new ArtifactOperationRecord("er", "e1", TimeSpan.Zero); expiredOp.TryMarkActive();
		var expiredResult = expiredLedger.AdmitArtifactWrite("e1", "never-created", new byte[1], expiredOp);
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true, ["session_admitted"] = s1 == ArtifactStoreLedger.AdmitResult.Ok,
			["file_at_limit"] = atFile == ArtifactStoreLedger.AdmitResult.Ok,
			["file_over_rejected_zero_delta"] = overFile == ArtifactStoreLedger.AdmitResult.LimitExceeded && fs.Tree["s1"].Count == beforeOver,
			["session_over_rejected_zero_delta"] = overSession == ArtifactStoreLedger.AdmitResult.LimitExceeded && fs.Tree["s1"].Count == beforeOver,
			["second_session_admitted"] = s2 == ArtifactStoreLedger.AdmitResult.Ok,
			["store_at_limit"] = storeAt == ArtifactStoreLedger.AdmitResult.Ok,
			["store_over_rejected"] = storeOver == ArtifactStoreLedger.AdmitResult.LimitExceeded,
			["external_child_fail_closed_zero_delta"] = mismatch == ArtifactStoreLedger.AdmitResult.TargetMismatch && fs.Tree.Count == beforeMismatch,
			["cancel_timeline_exactly_once"] = active && canceling && settled && !settledTwice && operation.CurrentPhase == ArtifactOperationRecord.Phase.Settled,
			["startup_stale_blocks_new"] = startupStale == ArtifactStoreLedger.AdmitResult.TargetMismatch && !staleFs.Tree.ContainsKey("new"),
			["retained_counts_toward_limits"] = retained == ArtifactStoreLedger.TerminalResult.Retained && retainedBytes == 10
				&& r2 == ArtifactStoreLedger.AdmitResult.Ok && retainedOver == ArtifactStoreLedger.AdmitResult.LimitExceeded,
			["retained_identity_reverified"] = retainedTamper == ArtifactStoreLedger.AdmitResult.TargetMismatch,
			["post_create_cancel_aborted_owned"] = interruptedResult == ArtifactStoreLedger.AdmitResult.OperationCanceled
				&& interruptedLedger.AbortedOwnedCount == 1 && interruptedFs.Tree["i1"].ContainsKey("partial"),
			["pre_create_deadline_zero_delta"] = expiredResult == ArtifactStoreLedger.AdmitResult.OperationTimedOut
				&& expiredFs.Tree["e1"].Count == 0,
		});
	}

	/// <summary>ACC-004 deterministic execution of transport limits which cannot be forged
	/// through HttpListener after HTTP.sys has normalized framing.</summary>
	string TestTransport(Dictionary<string, object>? args) {
		_ = args;
		if (!TestModeEnabled)
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "test diagnostics require DNMCP_TEST=1");
		var oversized = new byte[dnSpy.Extension.MCP.Transport.BoundedBodyReader.MaxBodyBytes + 1];
		var fakeSmall = dnSpy.Extension.MCP.Transport.BoundedBodyReader.Read(new MemoryStream(oversized), 1);
		var chunked = dnSpy.Extension.MCP.Transport.BoundedBodyReader.Read(new MemoryStream(oversized), null);
		using var shortGate = new dnSpy.Extension.MCP.Transport.AdmissionGate(16);
		var first16 = true;
		for (var i = 0; i < 16; i++) first16 &= shortGate.TryEnter();
		var seventeenth = shortGate.TryEnter();
		using var longGate = new dnSpy.Extension.MCP.Transport.AdmissionGate(8);
		var first8 = true;
		for (var i = 0; i < 8; i++) first8 &= longGate.TryEnter();
		var ninth = longGate.TryEnter();
		return Ok(coordinator, new Dictionary<string, object?> {
			["test_mode"] = true,
			["fake_small_content_length_rejected_at_1048577"] = fakeSmall.Decision == dnSpy.Extension.MCP.Transport.BoundedBodyReader.BodyDecision.StreamTooLarge && fakeSmall.Data.Length == oversized.Length,
			["chunked_unknown_length_rejected_at_1048577"] = chunked.Decision == dnSpy.Extension.MCP.Transport.BoundedBodyReader.BodyDecision.StreamTooLarge && chunked.Data.Length == oversized.Length,
			["short_17th_rejected"] = first16 && !seventeenth,
			["long_9th_rejected"] = first8 && !ninth,
		});
	}


	/// <summary>
	/// ACC-016: value-tool arguments are closed (additionalProperties=false) with a depth
	/// maximum of 4 — unknown budget fields or out-of-range depth are -32602 (ArgumentException),
	/// not silently ignored.
	/// </summary>
	static void ValidateValueToolArgs(Dictionary<string, object>? args, string tool, IEnumerable<string> allowedKeys, int? depthKey = null) {
		if (args is null)
			return;
		var allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
		foreach (var key in args.Keys)
			if (!allowed.Contains(key))
				throw new ArgumentException($"{tool} rejects unknown field: {key}", key);
		if (depthKey is not null && args.TryGetValue("depth", out var d) && d is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } dj) {
			if (!dj.TryGetInt32(out var depth) || depth < 1 || depth > 4)
				throw new ArgumentException("depth must be within 1..4", "depth");
		}
	}

	/// <summary>
	/// dnspy.debug.utf8-limits.v1 input side (ACC-028): deterministic UTF-8 byte ceilings on
	/// the input pointers — session_id/opaque handles/page_cursor 1024, relative_name 128,
	/// name_filter 256. Over-limit strings are -32602 regardless of character count (the
	/// standard schema maxLength counts characters and never substitutes for this).
	/// </summary>
	static void ValidateInputUtf8Limits(string tool, Dictionary<string, object>? args) {
		if (args is null)
			return;
		foreach (var key in args.Keys) {
			if (!(args.TryGetValue(key, out var raw) && raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je))
				continue;
			var value = je.GetString();
			if (value is null)
				continue;
			int limit = key switch {
				"session_id" or "page_cursor" or "thread_handle" or "frame_handle" or "value_handle"
					or "module_handle" or "runtime_handle" or "process_handle" => 1024,
				"relative_name" => 128,
				"name_filter" => 256,
				_ => 0,
			};
			if (limit > 0 && System.Text.Encoding.UTF8.GetByteCount(value) > limit)
				throw new ArgumentException($"{tool}.{key} exceeds {limit} UTF-8 bytes", key);
		}
	}

	// Tools whose request_id is structurally required: the -32602 shape rejection precedes
	// every gate/state semantic (ACC-002: invalid-gate continue is DEBUG_DISABLED only for
	// schema-valid requests).
	static readonly System.Collections.Generic.HashSet<string> RequestIdRequired = new() {
		"debug_launch", "debug_pause", "debug_continue", "debug_terminate", "debug_restart",
		"debug_set_breakpoint", "debug_set_breakpoint_enabled", "debug_remove_breakpoint",
		"debug_set_exception_policy", "debug_step", "debug_dump_module",
	};
	static readonly System.Collections.Generic.HashSet<string> ControlLaneTools = new() {
		"debug_pause", "debug_continue", "debug_restart", "debug_terminate", "debug_step",
	};
	readonly SideEffectRequestCache sideEffectCache = new();
	static readonly string SideEffectEnvelopeTemplate =
		new string('0', SideEffectRequestCache.MaxEnvelopeBytes);

	static TimeSpan RemainingControlDeadline(DualLaneQueue.Ticket? ticket) {
		if (ticket is null)
			return ControlOperationRecord.DefaultDeadline;
		var elapsedTicks = Stopwatch.GetTimestamp() - ticket.AdmissionTimestamp;
		var remainingTicks = (long)(ControlOperationRecord.DefaultDeadline.TotalSeconds * Stopwatch.Frequency)
			- elapsedTicks;
		return remainingTicks <= 0 ? TimeSpan.Zero
			: TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
	}

	public CallToolResult Execute(string toolName, Dictionary<string, object>? arguments) {
		ValidateInputUtf8Limits(toolName, arguments);
		string? admittedRequestId = null;
		DualLaneQueue.Ticket? laneTicket = null;
		if (RequestIdRequired.Contains(toolName)) {
			var requestId = ArgString(arguments, "request_id", required: true);
			// Gate failure precedes cache/queue admission. Schema validation has already run in
			// McpServer; the explicit request_id read above preserves -32602 for direct calls.
			if (gateService.Current.EffectiveDebugLaunch) {
				var canonicalArgs = SideEffectRequestCache.CanonicalizeArguments(arguments is null ? null
					: new Dictionary<string, object?>(arguments.ToDictionary(kv => kv.Key,
						kv => (object?)NormalizeJsonValue(kv.Value)), StringComparer.Ordinal));
				var admit = sideEffectCache.TryAdmit(requestId, toolName, canonicalArgs, SideEffectEnvelopeTemplate, () =>
					Fail(coordinator, DomainErrorCodes.LimitExceeded));
				switch (admit.Status) {
					case SideEffectRequestCache.AdmitStatus.HitSettled:
						return ResultOf(admit.SettledEnvelope!);
					case SideEffectRequestCache.AdmitStatus.RequestIdReuse:
						return ResultOf(Fail(coordinator, DomainErrorCodes.RequestIdReuse));
					case SideEffectRequestCache.AdmitStatus.LimitExceeded:
						return ResultOf(Fail(coordinator, DomainErrorCodes.LimitExceeded));
					case SideEffectRequestCache.AdmitStatus.JoinedInFlight:
						for (var spin = 0; spin < 1500; spin++) {
							Thread.Sleep(20);
							var settled = sideEffectCache.LookupSettled(requestId, toolName, canonicalArgs);
							if (settled is not null)
								return ResultOf(settled);
						}
						return ResultOf(Fail(coordinator, DomainErrorCodes.Timeout));
				}
				admittedRequestId = requestId;
				var entered = ControlLaneTools.Contains(toolName)
					? laneQueue.TryEnterControl(out laneTicket)
					: laneQueue.TryEnterGeneral(out laneTicket);
				if (!entered) {
					var limited = Fail(coordinator, DomainErrorCodes.LimitExceeded);
					sideEffectCache.Settle(requestId, limited);
					return ResultOf(limited);
				}
				// Capacity admission and execution scheduling are distinct. Only the granted
				// ticket may cross a side-effect mutation boundary; release promotes the oldest
				// control ticket first, otherwise the oldest general ticket.
				laneTicket!.WaitForTurn();
			}
		}
		string? envelope;
		try {
			envelope = toolName switch {
				"debug_status" => Status(arguments),
				"debug_launch" => Launch(arguments),
				"debug_pause" => Control(arguments, ControlOperation.Pause, laneTicket).GetAwaiter().GetResult(),
				"debug_continue" => Continue(arguments, laneTicket).GetAwaiter().GetResult(),
				"debug_terminate" => Control(arguments, ControlOperation.Terminate, laneTicket).GetAwaiter().GetResult(),
				"debug_restart" => Restart(arguments, laneTicket).GetAwaiter().GetResult(),
				"debug_read_events" => ReadEvents(arguments, wait: false).GetAwaiter().GetResult(),
				"debug_wait_event" => ReadEvents(arguments, wait: true).GetAwaiter().GetResult(),
				"debug_set_breakpoint" => SetBreakpoint(arguments),
				"debug_list_breakpoints" => ListBreakpoints(arguments),
				"debug_set_breakpoint_enabled" => SetBreakpointEnabled(arguments),
				"debug_remove_breakpoint" => RemoveBreakpoint(arguments),
				"debug_set_exception_policy" => SetExceptionPolicy(arguments),
				"debug_list_threads" => ListThreads(arguments),
				"debug_get_stack" => GetStack(arguments),
				"debug_step" => Step(arguments, laneTicket),
				"debug_get_locals" => GetLocals(arguments),
				"debug_expand_value" => ExpandValue(arguments),
				"debug_list_modules" => ListModules(arguments),
				"debug_read_memory" => ReadMemory(arguments),
				"debug_dump_module" => DumpModule(arguments, laneTicket),
				"debug_test_spy" => TestSpy(arguments),
				"debug_test_clock" => TestClock(arguments),
				"debug_test_adapter" => TestAdapter(arguments),
				"debug_test_flood" => TestFlood(arguments),
				"debug_test_start" => TestStart(arguments),
				"debug_test_dump" => TestDump(arguments),
				"debug_test_settings" => TestSettings(arguments),
				"debug_test_artifact" => TestArtifact(arguments),
				"debug_test_transport" => TestTransport(arguments),
				// The three fixed-disabled APIs (API-DYN-004/005/010) answer direct calls with
				// the domain CAPABILITY_UNAVAILABLE envelope — never an "unknown tool" text —
				// and without the unsupported-target details object.
				"debug_attach" or "debug_detach" or "debug_list_attachable_processes"
					=> Fail(coordinator, DomainErrorCodes.CapabilityUnavailable),
				_ => null,
			};
		}
		catch (ArgumentException) {
			// Semantic parameter/metadata rejections (token table, identity-shape, boundary)
			// surface as JSON-RPC -32602 via the server's ArgumentException mapping.
			if (admittedRequestId is not null)
				sideEffectCache.RemoveInFlight(admittedRequestId);
			laneTicket?.TryRelease();
			throw;
		}
		catch (Exception ex) {
			envelope = Fail(coordinator, DomainErrorCodes.InternalError, message: ex.GetType().Name + ": " + ex.Message);
		}
		if (envelope is null) {
			if (admittedRequestId is not null)
				sideEffectCache.RemoveInFlight(admittedRequestId);
			laneTicket?.TryRelease();
			return new CallToolResult {
				Content = new List<ToolContent> { new() { Text = $"Unknown tool: {toolName}" } },
				IsError = true,
			};
		}
		if (admittedRequestId is not null)
			sideEffectCache.Settle(admittedRequestId, envelope);
		laneTicket?.TryRelease();
		return ResultOf(envelope);
	}

	static CallToolResult ResultOf(string envelope) => new() {
		Content = new List<ToolContent> { new() { Text = envelope } },
		IsError = envelope.Contains("\"ok\":false"),
	};

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

	static string Fail(DebugSessionCoordinator c, string code, List<string>? requiredStates = null, string? message = null) =>
		Fail(c, code, requiredStates, message, details: null, untrustedSampleData: false);

	static string Fail(DebugSessionCoordinator c, string code, List<string>? requiredStates, string? message,
		UnsupportedTargetDetailsDto? details, bool untrustedSampleData) {
		var error = DomainErrorDto.Create(code, c.State, requiredStates);
		// Domain messages/recovery are frozen by DomainErrorDto.Create. Runtime exception,
		// path and debugger text never replace them or enter the cacheable wire envelope.
		error.Details = details;
		return JsonSerializer.Serialize(new DebugFailureEnvelope {
			DebugContext = c.ContextSnapshot(),
			Error = error,
			UntrustedSampleData = untrustedSampleData,
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
			ObservedProcessState = coordinator.ActiveSessionId is not null && coordinator.State != DebugStates.Idle
				? coordinator.ObservedProcessState
				: null,
			RuntimeFamily = activePlan?.RuntimeFamily,
			Architecture = activePlan is null ? null : launchArchitecture,
			StartKind = activePlan is null ? null : "launch",
			LaunchMode = activePlan?.LaunchMode,
			Fault = coordinator.State == DebugStates.Faulted ? (coordinator.Fault == DebugSessionCoordinator.FaultKind.OwnershipLost ? "ownership_lost" : "control_fault") : null,
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

	/// <summary>JsonElement → primitive tree for the cache's JCS canonicalizer.</summary>
	static object? NormalizeJsonValue(object? v) {
		// System.Text.Json normally leaves object-valued numbers as JsonElement, but the
		// net48 request path can materialize schema "integer" values as CLR Double during
		// the Params → CallToolRequest round trip. Preserve the cache's integer-only JCS
		// domain by losslessly folding those representations back to Int64. Rejecting a
		// fractional or non-finite value here is deliberate: side-effect schemas expose
		// integers only, and admitting an imprecise cache identity would be unsafe.
		if (v is double d) {
			if (double.IsNaN(d) || double.IsInfinity(d) || d != Math.Truncate(d)
				|| d < -9007199254740991d || d > 9007199254740991d)
				throw new ArgumentException("side-effect numeric argument is not a lossless JSON integer");
			return (long)d;
		}
		if (v is float f) {
			if (float.IsNaN(f) || float.IsInfinity(f) || f != Math.Truncate(f))
				throw new ArgumentException("side-effect numeric argument is not a JSON integer");
			return (long)f;
		}
		if (v is decimal m) {
			if (m != decimal.Truncate(m) || m < long.MinValue || m > long.MaxValue)
				throw new ArgumentException("side-effect numeric argument is not a JSON integer");
			return (long)m;
		}
		if (v is not System.Text.Json.JsonElement je)
			return v;
		switch (je.ValueKind) {
			case System.Text.Json.JsonValueKind.String: return je.GetString();
			case System.Text.Json.JsonValueKind.True: return true;
			case System.Text.Json.JsonValueKind.False: return false;
			case System.Text.Json.JsonValueKind.Number:
				return je.TryGetInt64(out var l) ? l : NormalizeJsonValue(je.GetDouble());
			case System.Text.Json.JsonValueKind.Null: return null;
			case System.Text.Json.JsonValueKind.Array: {
				var list = new List<object?>();
				foreach (var item in je.EnumerateArray()) list.Add(NormalizeJsonValue(item));
				return list;
			}
			case System.Text.Json.JsonValueKind.Object: {
				var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
				foreach (var prop in je.EnumerateObject()) dict[prop.Name] = NormalizeJsonValue(prop.Value);
				return dict;
			}
			default: return null;
		}
	}

	string Launch(Dictionary<string, object>? args) => LaunchCore(args);

	string LaunchCore(Dictionary<string, object>? args) {
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

		// ACC-033: only volumes with stable FILE_ID_INFO/share semantics (NTFS) may host
		// launch inputs — fail closed before any lease/claim/Start, zero side effects.
		{
			var hostPathEarly = ArgString(args, "host_path");
			var harnessPathEarly = ArgString(args, "harness_path");
			var workingDirectoryEarly = ArgString(args, "working_directory");
			var unsupportedFs = FindUnsupportedVolume(targetPath)
				?? FindUnsupportedVolume(harnessPathEarly) ?? FindUnsupportedVolume(hostPathEarly)
				?? FindUnsupportedVolume(workingDirectoryEarly);
			if (unsupportedFs is not null)
				return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable,
					message: $"launch inputs must live on an NTFS volume (found {unsupportedFs})");
		}

		// Validate the entire namespace before opening any target lease.  Missing tail
		// components continue walking their existing parents; inspection errors fail closed.
		var workingDirectory = ArgString(args, "working_directory");
		foreach (var rpPath in new[] { targetPath, ArgString(args, "host_path"),
			ArgString(args, "harness_path"), workingDirectory }) {
			if (string.IsNullOrEmpty(rpPath))
				continue;
			string? reparseComponent = FindReparseComponent(rpPath);
			if (reparseComponent is not null)
				return Fail(coordinator, DomainErrorCodes.TargetMismatch,
					message: $"path is not a verifiable reparse-free path: {reparseComponent}");
		}
		var sampleRoot = settings.CurrentSnapshot?.AllowedSampleRoot;
		if (!string.IsNullOrEmpty(sampleRoot)) {
			var rootFull = System.IO.Path.GetFullPath(sampleRoot);
			if (!TryAcquireRootLease(rootFull, out var rootLeaseError))
				return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: rootLeaseError);
			var rootPrefix = rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
				? rootFull : rootFull + System.IO.Path.DirectorySeparatorChar;
			foreach (var candidate in new[] { targetPath, ArgString(args, "host_path"),
				ArgString(args, "harness_path"), workingDirectory }) {
				if (string.IsNullOrEmpty(candidate))
					continue;
				string candidateFull;
				try { candidateFull = System.IO.Path.GetFullPath(candidate); }
				catch (Exception ex) { return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: ex.Message); }
				if (!candidateFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(candidateFull.TrimEnd(System.IO.Path.DirectorySeparatorChar),
						rootFull.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
					return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: $"path is outside AllowedSampleRoot: {candidate}");
			}
		}

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
		FileIdentityDto? workingDirectoryIdentity = null;
		if (!string.IsNullOrEmpty(workingDirectory)
			&& !TryLeaseIdentity(workingDirectory, "working_directory", "directory", null,
				out workingDirectoryIdentity, out identityError))
			return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: identityError);

		var breakKind = ArgString(args, "break_kind");
		if (string.IsNullOrEmpty(breakKind))
			breakKind = BreakKinds.None;
		// ACC-030: harness launches accept only none/omitted break_kind — the other three
		// values are -32602 (ArgumentException) before any lease or Start.
		if (launchMode == LaunchModes.Harness && breakKind != BreakKinds.None)
			throw new ArgumentException("harness launch_mode allows only break_kind=none", "break_kind");
		// FB-002 / ACC-024: deterministic unsupported-target detection over the target's own
		// bytes — CAPABILITY_UNAVAILABLE with the TYPE-DYN-019 evidence chain, zero session.
		// An over-limit evidence value answers a small INTERNAL_ERROR instead of shipping an
		// over-limit untrusted domain envelope.
		// coreclr-apphost is EXEMPT: a .NET apphost is a native loader stub by design (no CLR
		// header in the stub PE) — the debugged managed image is the sibling DLL, so PE-level
		// unsupported-target detection must not run against the stub (CHK-002/ACC-008).
		var unsupported = launchMode == LaunchModes.CoreClrAppHost
			? null
			: UnsupportedTargetDetector.Detect(targetPath);
		if (unsupported is not null && unsupported.EvidenceOverLimit) {
			SpyInc("unsupported_target_evidence_overflow");
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.InternalError, null,
				"unsupported-target evidence exceeds the 1024 UTF-8 byte limit", null, false);
		}
		if (unsupported is not null) {
			SpyInc("unsupported_target_rejections:" + unsupported.DetectedTargetKind);
			// A rejected target keeps no lease (same rule as the identity rejections).
			ReleaseLeases();
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, null,
				$"unsupported target kind: {unsupported.DetectedTargetKind} (use {unsupported.RecommendedWorkflow})",
				new UnsupportedTargetDetailsDto {
					DetectedTargetKind = unsupported.DetectedTargetKind,
					RecommendedWorkflow = unsupported.RecommendedWorkflow,
					Evidence = unsupported.Evidence
						.Select(e => new UnsupportedTargetDetailsDto.EvidenceItem { Kind = e.Kind, Value = e.Value })
						.ToList(),
				}, true);
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
			if (workingDirectoryIdentity is not null) launchIdentities.Add(workingDirectoryIdentity);
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
			bool uiBusy = startError == UiDebuggingBusy;
			coordinator.MarkLaunchFailed(uiBusy ? "INVALID_STATE" : "INTERNAL_ERROR");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return uiBusy
				? Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Idle })
				: Fail(coordinator, DomainErrorCodes.InternalError, message: startError);
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
	// DNMCP_TEST-only: simulates a human UI debug session being active (DbgManager.IsDebugging
	// reads true for the launch precheck and the static write gate) without a real UI session.
	volatile bool testUiDebugging;

	// Marker for the CON-DYN-003 pre-Start check: a UI (or other-extension) debug session is
	// already active — the launch answers INVALID_STATE, not the Start-error INTERNAL_ERROR.
	const string UiDebuggingBusy = "\u0001UI_DEBUGGING_ACTIVE";

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
			// Same WPF callback as Start (CON-DYN-003): claim established, precheck IsDebugging,
			// then Start — a busy manager means INVALID_STATE with zero Start side effects.
			// Bounded teardown wait: right after OUR terminal transition the manager can still
			// report IsDebugging while tearing the previous session down; when the coordinator
			// is idle with no owned process that residue is not a competing debug session, so
			// wait for quiescence instead of failing the immediately following launch/restart
			// (CHK-005 / ACC-034). The test seam never waits.
			if (dbgManager.IsDebugging && !testUiDebugging) {
				DbgProcess? ownedPeek;
				lock (sessionLock) ownedPeek = ownedProcess;
				// owned==null covers both post-terminal launches and the restart-internal
				// relaunch right after a REAL removal (state=restarting there, not idle).
				if (ownedPeek is null) {
					SpyInc("manager_teardown_waits");
					var swWait = System.Diagnostics.Stopwatch.StartNew();
					while (dbgManager.IsDebugging && swWait.ElapsedMilliseconds < 3000)
						System.Threading.Thread.Sleep(50);
				}
			}
			if (dbgManager.IsDebugging || testUiDebugging) {
				SpyInc("ui_debugging_blocks");
				return UiDebuggingBusy;
			}
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

	async Task<string> Control(Dictionary<string, object>? args, ControlOperation operation,
		DualLaneQueue.Ticket? laneTicket) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, RequiredFor(operation));
		var requestId = ArgString(args, "request_id", required: true);
		CancelArtifactForControl();

		var admission = coordinator.TryBeginControl(operation, requestId, laneTicket?.AdmissionTimestamp);
		if (!admission.Admitted || admission.Record is null) {
			// ACC-025-B: while faulted(ownership_lost) every enabled control answers
			// OWNERSHIP_LOST (manual resolve then wait idle), never a bare INVALID_STATE.
			if (coordinator.OwnershipLostFaulted)
				return Fail(coordinator, DomainErrorCodes.OwnershipLost);
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

		var remaining = RemainingControlDeadline(laneTicket);
		var deadlineTask = TestModeEnabled ? VirtualDeadline(remaining) : Task.Delay(remaining);
		var done = await Task.WhenAny(tcs.Task, deadlineTask).ConfigureAwait(false);
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

	async Task<string> Continue(Dictionary<string, object>? args, DualLaneQueue.Ticket? laneTicket) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		if (coordinator.OwnershipLostFaulted)
			return Fail(coordinator, DomainErrorCodes.OwnershipLost);
		var pauseEpoch = ArgInt(args, "pause_epoch", required: true);
		if (coordinator.State != DebugStates.Paused || coordinator.PauseEpoch != pauseEpoch)
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		CancelArtifactForControl();

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
			var done = await Task.WhenAny(completed.Task, Task.Delay(RemainingControlDeadline(laneTicket))).ConfigureAwait(false);
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

	async Task<string> Restart(Dictionary<string, object>? args, DualLaneQueue.Ticket? laneTicket) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		var identityErr = SessionIdentityError(args);
		if (identityErr is not null)
			return Fail(coordinator, identityErr, message: "request names a different session than the active one");
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Running, DebugStates.Paused });
		var requestId = ArgString(args, "request_id", required: true);

		// Phase 1: terminate the owned process under a restart reservation.
		var terminateEnvelope = await Control(args, ControlOperation.Restart, laneTicket).ConfigureAwait(false);
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
			bool uiBusy = startError == UiDebuggingBusy;
			coordinator.MarkLaunchFailed(uiBusy ? "INVALID_STATE" : "INTERNAL_ERROR");
			lock (sessionLock) { launchClaimTcs = null; activePlan = null; }
			ReleaseLeases();
			return uiBusy
				? Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Idle })
				: Fail(coordinator, DomainErrorCodes.InternalError, message: startError);
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
			// CON-DYN-009 global wait cap: admission is instantaneous — the 9th concurrent
			// wait is LIMIT_EXCEEDED (queuing for a slot would make the cap unobservable).
			if (!await waitSlots.WaitAsync(0).ConfigureAwait(false))
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

	/// <summary>Assigns module handles for the owned process's loaded modules.  MVID is read
	/// from the live engine metadata and disk modules carry their actual SHA-256.</summary>
	void RegisterModules(DbgProcess process) {
		lock (sessionLock) {
			modulesByHandle.Clear();
			int index = 0;
			foreach (var runtime in process.Runtimes) {
				string runtimeHandle = $"rt-{index++}";
				foreach (var module in runtime.Modules) {
					string handle = $"mod-g{coordinator.Generation}-{modulesByHandle.Count}";
					modulesByHandle[handle] = new RegisteredModuleRecord {
						ModuleHandle = handle,
						RuntimeHandle = runtimeHandle,
						Filename = module.Filename ?? string.Empty,
						Name = module.Name,
						Address = module.Address,
						Size = module.Size,
						Layout = module.IsDynamic || !HasDiskPath(module) ? "memory" : "file",
						Mvid = AuthoritativeMvidOf(module),
						Sha256 = TryHashDiskModule(module),
						UpstreamId = UpstreamIdOf(module),
						LiveModule = module,
					};
				}
			}
		}
	}

	/// <summary>Paused-tool gate (ACC-006/016): pause_epoch is part of every handle/page-cursor
	/// identity, so a request naming an epoch that no longer matches answers STALE_HANDLE
	/// whatever the current state — INVALID_STATE stays reserved for a current epoch while
	/// the coordinator is not paused. Returns the serialized failure, or null when admitted.</summary>
	string? PausedGateFailure(Dictionary<string, object>? args) {
		if (ArgInt(args, "pause_epoch", required: true) != coordinator.PauseEpoch)
			return Fail(coordinator, DomainErrorCodes.StaleHandle);
		if (coordinator.State != DebugStates.Paused)
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		return null;
	}

	/// <summary>
	/// Enumerates the owned process's live module table on the DbgManager dispatcher and
	/// refreshes <see cref="modulesByHandle"/> with minted mod-N handles and identities read
	/// from the current live modules (seeding the launch-verified target sha where available).
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
						string handle = $"mod-g{coordinator.Generation}-{modules.Count}";
						var record = new RegisteredModuleRecord {
							ModuleHandle = handle,
							RuntimeHandle = runtimeHandle,
							Filename = module.Filename ?? string.Empty,
							Name = module.Name,
							Address = module.Address,
							Size = module.Size,
							Layout = module.IsDynamic || !HasDiskPath(module) ? "memory" : "file",
							Mvid = AuthoritativeMvidOf(module),
							Sha256 = TryHashDiskModule(module),
							UpstreamId = UpstreamIdOf(module),
							LiveModule = module,
						};
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
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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
		// API-DYN-013: enabled? defaults to TRUE — an omitted field must never create a
		// disabled breakpoint (the engine never binds disabled bps, so it would silently
		// never hit).
		var enabled = true;
		if (args is not null && args.TryGetValue("enabled", out var e) && e is System.Text.Json.JsonElement je)
			enabled = je.ValueKind == System.Text.Json.JsonValueKind.True;

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
		if (ilOffset < 0)
			throw new ArgumentException("il_offset must be a non-negative IL instruction boundary", nameof(ilOffset));
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
		var instructionBoundary = false;
		PostVoidToDispatcherSync(() => instructionBoundary = IsInstructionBoundary(module, tokenValue, ilOffset));
		if (!instructionBoundary)
			throw new ArgumentException("il_offset is not an instruction boundary in the MethodDef body", nameof(ilOffset));
		bpStore.RegisterModule(new RegisteredModule {
			ModuleHandle = module.ModuleHandle,
			RuntimeHandle = module.RuntimeHandle,
			Mvid = module.Mvid,
			IdentityStrength = identityStrength,
			Sha256 = module.Sha256,
		});
		// ACC-035: with identical MVID/token/offset on same-bytes sibling modules there is
		// exactly one engine location — a SECOND module_handle retrying the same identity is a
		// cross-module mismatch, never a second binding (and never an INTERNAL_ERROR).
		foreach (var existingBp in bpStore.List()) {
			if (existingBp.Module.ModuleHandle != moduleHandle
				&& string.Equals(existingBp.Module.Mvid, mvid, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(existingBp.MethodToken, methodToken, StringComparison.OrdinalIgnoreCase)
				&& existingBp.IlOffset == ilOffset)
				return Fail(coordinator, DomainErrorCodes.TargetMismatch, message: "identity already owned by another module_handle of this session");
		}
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
			// dnSpy's Add answers null when a breakpoint ALREADY exists at this location —
			// reuse that live breakpoint instead of failing the create (multiple owned ids may
			// share one engine breakpoint; removal stays per-owned-id).
			bp ??= breakpointsService.TryGetBreakpoint(location);
			return bp is null ? null : new[] { bp };
		});
		if (created is null or { Length: 0 }) {
			bpStore.Remove(entry.BreakpointId);
			return Fail(coordinator, DomainErrorCodes.InternalError, message: "the debugger rejected the breakpoint location");
		}
		foreach (var bp in created) {
			bpStore.SetEnabled(entry.BreakpointId, enabled);
			lock (sessionLock) {
				moduleByOwnedBp[entry.BreakpointId] = module.LiveModule;
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
		var breakpointId = ArgString(args, "breakpoint_id", required: true);
		if (!bpStore.Remove(breakpointId))
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown breakpoint");
		PostToDispatcher(() => {
			if (breakpointsService is null)
				return null;
			lock (sessionLock) {
				moduleByOwnedBp.Remove(breakpointId);
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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

	string Step(Dictionary<string, object>? args, DualLaneQueue.Ticket? laneTicket) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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
		CancelArtifactForControl();
		string stepId = $"step-{Interlocked.Increment(ref stepSeq)}";
		lock (sessionLock)
			currentStep = new StepRegistration { Id = stepId, Kind = kind, ThreadHandle = threadHandle };
		bool stepped = false;
		var delivered = PostVoidToDispatcherSync(() => {
			var thread = FindThreadByTid(stepTid);
			if (thread is null)
				return;
			var stepper = thread.CreateStepper();
			stepper.Step(upstreamKind.Value, autoClose: true);
			stepped = true;
		}, RemainingControlDeadline(laneTicket));
		if (!delivered) {
			lock (sessionLock) currentStep = null;
			return Fail(coordinator, DomainErrorCodes.Timeout);
		}
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

	bool PostVoidToDispatcherSync(Action action, TimeSpan? timeout = null) {
		if (dbgManager is null)
			return false;
		SpyInc("dispatcher_sync_posts");
		var done = new ManualResetEventSlim();
		dbgManager.Dispatcher.BeginInvoke(new Action(() => {
			try { action(); }
			finally { done.Set(); }
		}));
		return done.Wait(timeout ?? ControlOperationRecord.DefaultDeadline);
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
		ValidateValueToolArgs(args, "debug_get_locals", new[] { "session_id", "generation", "pause_epoch", "frame_handle", "page_size", "page_cursor" });
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
		ValidateValueToolArgs(args, "debug_expand_value", new[] { "session_id", "generation", "pause_epoch", "value_handle", "depth", "page_size", "page_cursor" }, depthKey: 1);
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
	ProductionArtifactFs? artifactFs;
	readonly List<ProductionArtifactFs> retiredArtifactStores = new();
	string? artifactLedgerRoot;
	sealed class ActiveArtifactOperation {
		public ArtifactOperationRecord Record { get; }
		public TaskCompletionSource<bool> Finished { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> VisibleCancellation { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool HandedToFinalizer;
		public ActiveArtifactOperation(ArtifactOperationRecord record) => Record = record;
	}
	readonly object artifactOperationLock = new();
	ActiveArtifactOperation? activeArtifactOperation;
	string? terminalPendingArtifactSession;
	string? ArtifactRootPath => settings.CurrentSnapshot?.ArtifactRoot is { Length: > 0 } root ? root : null;

	bool TryBeginArtifactOperation(string requestId, string sessionId, long? admissionTimestamp,
		out ActiveArtifactOperation? active) {
		lock (artifactOperationLock) {
			active = null;
			if (activeArtifactOperation is not null)
				return false;
			var record = new ArtifactOperationRecord(requestId, sessionId,
				admissionTimestamp: admissionTimestamp);
			if (!record.TryMarkActive())
				return false;
			activeArtifactOperation = active = new ActiveArtifactOperation(record);
			return true;
		}
	}

	void CompleteArtifactOperation(ActiveArtifactOperation active) {
		string? pending = null;
		active.Record.TrySettle();
		lock (artifactOperationLock) {
			if (ReferenceEquals(activeArtifactOperation, active)) {
				activeArtifactOperation = null;
				pending = terminalPendingArtifactSession;
				terminalPendingArtifactSession = null;
			}
			active.Finished.TrySetResult(true);
		}
		if (pending is not null)
			TerminalArtifactSession(pending);
	}

	void CancelArtifactForControl() {
		ActiveArtifactOperation? active;
		lock (artifactOperationLock) active = activeArtifactOperation;
		if (active is null)
			return;
		active.Record.RequestCancellation();
		if (!active.Finished.Task.Wait(TimeSpan.FromSeconds(2))) {
			active.Record.TryMarkCanceling();
			active.VisibleCancellation.TrySetResult(true);
			SpyInc("artifact_canceling_after_grace");
		}
	}

	sealed class ProductionArtifactFs : IArtifactStoreFs, IDisposable {
		readonly string root;
		readonly Microsoft.Win32.SafeHandles.SafeFileHandle rootLease;
		readonly List<Microsoft.Win32.SafeHandles.SafeFileHandle> rootChainLeases = new();
		readonly Dictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> sessionLeases = new(StringComparer.Ordinal);
		readonly Dictionary<string, FileStream> childLeases = new(StringComparer.Ordinal);
		public ProductionArtifactFs(string root) {
			this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
			var unsupported = FindUnsupportedVolume(this.root);
			if (unsupported is not null)
				throw new IOException($"ArtifactRoot must be on NTFS (found {unsupported})");
			var components = new Stack<string>();
			for (var current = this.root; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
				components.Push(current);
				var parent = Path.GetDirectoryName(current);
				if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
					break;
			}
			while (components.Count != 0) {
				var component = components.Pop();
				if (!Directory.Exists(component)) {
					if (!string.Equals(component, this.root, StringComparison.OrdinalIgnoreCase)
						|| !CreateDirectoryW(component, IntPtr.Zero))
						throw new IOException($"ArtifactRoot parent is absent or root creation failed: {component}",
							new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				}
				var attrs = File.GetAttributes(component);
				if ((attrs & FileAttributes.ReparsePoint) != 0)
					throw new IOException($"ArtifactRoot component is a reparse point: {component}");
				var raw = CreateFileW(component, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
					IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
				if (raw == IntPtr.Zero || raw == new IntPtr(-1))
					throw new IOException($"ArtifactRoot component lease failed: {component}",
						new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				var lease = new Microsoft.Win32.SafeHandles.SafeFileHandle(raw, ownsHandle: true);
				var finalPath = FinalPathOf(raw).TrimEnd(Path.DirectorySeparatorChar);
				if (!string.Equals(finalPath, Path.GetFullPath(component).TrimEnd(Path.DirectorySeparatorChar),
					StringComparison.OrdinalIgnoreCase)) {
					lease.Dispose();
					throw new IOException($"ArtifactRoot final path mismatch: {component} -> {finalPath}");
				}
				rootChainLeases.Add(lease);
			}
			rootLease = rootChainLeases[rootChainLeases.Count - 1];
		}
		public (string VolumeSerial, string FileId) RootIdentity {
			get {
				if (!GetFileInformationByHandle(rootLease.DangerousGetHandle(), out var info))
					throw new IOException("ArtifactRoot identity query failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				return ($"0x{info.VolumeSerial:x16}", $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}".PadLeft(32, '0'));
			}
		}
		public string RootFinalPath => FinalPathOf(rootLease.DangerousGetHandle());
		string SessionDir(string sessionId) => Path.Combine(root, sessionId);
		public IReadOnlyList<string> EnumerateRootChildren() =>
			Directory.Exists(root) ? Directory.GetFileSystemEntries(root).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList() : new List<string>();
		public IReadOnlyList<string> EnumerateSessionChildren(string sessionId) {
			var dir = SessionDir(sessionId);
			return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList() : new List<string>();
		}
		public bool SessionDirectoryExists(string sessionId) => Directory.Exists(SessionDir(sessionId));
		public (string VolumeSerial, string FileId, long Length)? ObserveChild(string sessionId, string relativeName) {
			try {
				var path = Path.Combine(SessionDir(sessionId), relativeName);
				using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				var info = GetFileIdentity(lease);
				return ($"0x{info.VolumeSerial:x16}", $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}".PadLeft(32, '0'), lease.Length);
			}
			catch { return null; }
		}
		public void CreateSessionDirectory(string sessionId) {
			var path = SessionDir(sessionId);
			if (!CreateDirectoryW(path, IntPtr.Zero))
				throw new IOException("artifact session CreateDirectory failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ,
				IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
			if (handle == IntPtr.Zero || handle == new IntPtr(-1))
				throw new IOException("artifact session lease failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			sessionLeases.Add(sessionId, new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: true));
		}
		public ArtifactStoreLedger.ChildRecord CreateChildFile(string sessionId, string relativeName,
			long length, byte[]? payload, ArtifactOperationRecord? operation) {
			if (payload is null || payload.LongLength != length)
				throw new InvalidOperationException("production artifact writes require the exact payload");
			var path = Path.Combine(SessionDir(sessionId), relativeName);
			var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
				1024 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough | FileOptions.Asynchronous);
			try {
				using var sha = SHA256.Create();
				const int chunkSize = 1024 * 1024;
				for (var offset = 0; offset < payload.Length; offset += chunkSize) {
					if (operation?.IsExpiredNow == true || operation?.CancellationRequested == true)
						Interrupt(stream, sessionId, relativeName, operation);
					var count = Math.Min(chunkSize, payload.Length - offset);
					stream.WriteAsync(payload, offset, count, operation?.CancellationToken
						?? CancellationToken.None).GetAwaiter().GetResult();
					sha.TransformBlock(payload, offset, count, null, 0);
				}
				if (operation?.IsExpiredNow == true || operation?.CancellationRequested == true)
					Interrupt(stream, sessionId, relativeName, operation);
				sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
				stream.Flush(true);
				var info = GetFileIdentity(stream);
				var record = new ArtifactStoreLedger.ChildRecord(relativeName,
					$"0x{info.VolumeSerial:x16}", $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}".PadLeft(32, '0'),
					stream.Length, ConvertHexShim.ToHexString(sha.Hash!).ToLowerInvariant());
				childLeases.Add(sessionId + "\0" + relativeName, stream);
				return record;
			}
			catch (OperationCanceledException) when (operation?.CancellationRequested == true) {
				Interrupt(stream, sessionId, relativeName, operation);
				throw;
			}
			catch (ArtifactStoreLedger.ArtifactWriteInterruptedException) {
				throw;
			}
			catch {
				stream.Dispose();
				throw;
			}
		}
		void Interrupt(FileStream stream, string sessionId, string relativeName,
			ArtifactOperationRecord? operation) {
			stream.Flush(true);
			// CancelIoEx/async cancellation may report after a prefix of the current chunk was
			// written. Re-hash the final bytes through the same still-leased handle so the
			// aborted_owned record reflects the actual final length/content, not an assumed
			// chunk boundary.
			stream.Position = 0;
			byte[] finalHash;
			using (var finalSha = SHA256.Create())
				finalHash = finalSha.ComputeHash(stream);
			var info = GetFileIdentity(stream);
			var record = new ArtifactStoreLedger.ChildRecord(relativeName,
				$"0x{info.VolumeSerial:x16}", $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}".PadLeft(32, '0'),
				stream.Length, ConvertHexShim.ToHexString(finalHash).ToLowerInvariant(), "aborted_owned");
			childLeases.Add(sessionId + "\0" + relativeName, stream);
			throw new ArtifactStoreLedger.ArtifactWriteInterruptedException(record,
				operation?.IsExpiredNow == true ? ArtifactStoreLedger.AdmitResult.OperationTimedOut
					: ArtifactStoreLedger.AdmitResult.OperationCanceled);
		}
		public void Dispose() {
			foreach (var stream in childLeases.Values)
				stream.Dispose();
			childLeases.Clear();
			foreach (var lease in sessionLeases.Values)
				lease.Dispose();
			sessionLeases.Clear();
			foreach (var lease in rootChainLeases)
				lease.Dispose();
			rootChainLeases.Clear();
		}
	}

	ArtifactStoreLedger ArtifactLedger() {
		var configuredRoot = ArtifactRootPath ?? throw new InvalidOperationException("artifact root not configured");
		var normalizedRoot = Path.GetFullPath(configuredRoot);
		if (artifactLedger is not null && !WindowsPathRelation.EqualPath(artifactLedgerRoot!, normalizedRoot)) {
			// Lease-relevant settings can change only while idle. Old retention handles remain
			// alive for the process lifetime, while the new root starts with an empty ledger.
			if (artifactFs is not null) retiredArtifactStores.Add(artifactFs);
			artifactFs = null; artifactLedger = null; artifactLedgerRoot = null;
		}
		if (artifactLedger is null) {
			artifactFs = new ProductionArtifactFs(normalizedRoot);
			var roots = new List<string> { artifactFs.RootFinalPath };
			var sampleRoot = settings.CurrentSnapshot?.AllowedSampleRoot;
			if (!string.IsNullOrEmpty(sampleRoot)) roots.Add(Path.GetFullPath(sampleRoot));
			var extensionDirectory = Path.GetDirectoryName(typeof(DebugSessionService).Assembly.Location);
			if (!string.IsNullOrEmpty(extensionDirectory)) roots.Add(extensionDirectory);
			if (!WindowsPathRelation.RootsAreDisjoint(roots)) {
				artifactFs.Dispose(); artifactFs = null;
				throw new InvalidOperationException("ArtifactRoot, AllowedSampleRoot and extension directory must be disjoint");
			}
			artifactLedger = new ArtifactStoreLedger(artifactFs);
			artifactLedger.Initialize();
			artifactLedgerRoot = normalizedRoot;
		}
		return artifactLedger;
	}

	void TerminalArtifactSession(string? sessionId) {
		if (sessionId is null || artifactLedger is null)
			return;
		lock (artifactOperationLock) {
			if (activeArtifactOperation is { } active && active.Record.SessionId == sessionId
				&& active.Record.CurrentPhase != ArtifactOperationRecord.Phase.Settled) {
				terminalPendingArtifactSession = sessionId;
				SpyInc("artifact_terminal_pending");
				return;
			}
		}
		var result = artifactLedger.TerminalSession(sessionId);
		SpyInc(result == ArtifactStoreLedger.TerminalResult.Retained
			? "artifact_terminal_retained" : "artifact_terminal_stale");
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
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
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
				try {
					var attrs = System.IO.File.GetAttributes(current);
					if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0)
						return current;
				}
				catch (System.IO.FileNotFoundException) { }
				catch (System.IO.DirectoryNotFoundException) { }
				catch (Exception ex) { return current + " (inspection failed: " + ex.GetType().Name + ")"; }
				var parent = System.IO.Path.GetDirectoryName(current);
				if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
					return null;
				current = parent;
			}
		}
		catch (Exception ex) {
			return path + " (inspection failed: " + ex.GetType().Name + ")";
		}
		return null;
	}

	static ulong ParseUlong(string text) {
		var t = text.Trim();
		if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			return Convert.ToUInt64(t.Substring(2), 16);
		return Convert.ToUInt64(t);
	}

	string DumpModule(Dictionary<string, object>? args, DualLaneQueue.Ticket? laneTicket) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
		if (!SessionAndGenerationMatch(args))
			return Fail(coordinator, DomainErrorCodes.InvalidState, new List<string> { DebugStates.Paused });
		var pausedGateFailure = PausedGateFailure(args);
		if (pausedGateFailure is not null)
			return pausedGateFailure;
		var requestId = ArgString(args, "request_id", required: true);
		var moduleHandle = ArgString(args, "module_handle", required: true);
		var relativeName = ArgString(args, "relative_name");
		if (!string.IsNullOrEmpty(relativeName) && !IsValidArtifactStem(relativeName))
			throw new ArgumentException("relative_name is not a safe Windows child-name stem");
		var root = ArtifactRootPath;
		if (string.IsNullOrEmpty(root))
			return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "ArtifactRoot is not configured");
		var sessionId = coordinator.ActiveSessionId!;
		if (!TryBeginArtifactOperation(requestId, sessionId, laneTicket?.AdmissionTimestamp, out var active) || active is null)
			return Fail(coordinator, DomainErrorCodes.InvalidState, message: "an artifact operation is already active or canceling");
		try {
			return DumpModuleOperation(args, requestId, moduleHandle, relativeName, root!, sessionId, active, laneTicket);
		}
		finally {
			if (!active.HandedToFinalizer)
				CompleteArtifactOperation(active);
		}
	}

	string DumpModuleOperation(Dictionary<string, object>? args, string requestId,
		string moduleHandle, string relativeName, string root, string sessionId,
		ActiveArtifactOperation active, DualLaneQueue.Ticket? laneTicket) {

		RegisteredModuleRecord module;
		lock (sessionLock) {
			if (!modulesByHandle.TryGetValue(moduleHandle, out module!))
				return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown module_handle");
		}
		// FB-001 three-branch flow over the injectable IRawModuleBytesSource seam: raw bytes
		// win when available (source_exact); otherwise a ForceMemory metadata reconstruction
		// produces a NOT source-equivalent PE; when both are unavailable the closed
		// CAPABILITY_UNAVAILABLE answer never leaves an artifact and never shells out.
		byte[] artifactBytes;
		string kind, equivalence, artifactLayout, reconstructionMethod;
		var rawBytes = ReadRawModuleBytes(module);
		if (rawBytes is not null) {
			SpyInc("dump_branch_raw");
			artifactBytes = rawBytes;
			kind = "raw";
			equivalence = "source_exact";
			artifactLayout = "file";
			reconstructionMethod = null!;
		}
		else {
			if (testDumpMode == "both_unavailable") {
				SpyInc("dump_branch_unavailable");
				return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "raw bytes unavailable and ForceMemory reconstruction disabled (test injection)");
			}
			var reconstructed = ReconstructViaForceMemory(module);
			if (reconstructed is null) {
				SpyInc("dump_branch_unavailable");
				return Fail(coordinator, DomainErrorCodes.CapabilityUnavailable, message: "module has no raw image and ForceMemory reconstruction failed");
			}
			SpyInc("dump_branch_reconstructed");
			artifactBytes = reconstructed;
			kind = "reconstructed";
			equivalence = "reconstructed_not_source_equivalent";
			artifactLayout = "memory";
			reconstructionMethod = "dnspy-force-memory";
		}
		if (artifactBytes.Length > ArtifactStoreLedger.MaxFileBytes)
			return Fail(coordinator, DomainErrorCodes.LimitExceeded, message: "module exceeds the 512 MiB artifact file cap");

		if (string.IsNullOrEmpty(relativeName)) {
			if (kind == "raw")
				relativeName = Path.GetFileName(module.Filename);
			else {
				using (var sha12 = SHA256.Create())
					relativeName = $"{module.Mvid}-{ConvertHexShim.ToHexString(sha12.ComputeHash(System.Text.Encoding.UTF8.GetBytes(moduleHandle))).ToLowerInvariant().Substring(0, 12)}";
			}
		}
		// User input was rejected above rather than rewritten. The generated default is not
		// request input, so normalize it deterministically and fall back to the module handle
		// digest when the source filename cannot be represented as a safe child-name stem.
		if (!IsValidArtifactStem(relativeName)) {
			using var fallbackSha = SHA256.Create();
			relativeName = ConvertHexShim.ToHexString(fallbackSha.ComputeHash(
				System.Text.Encoding.UTF8.GetBytes(moduleHandle))).ToLowerInvariant().Substring(0, 24);
		}
		var childName = relativeName + ".bin";

		// All dnSpy-owned state and module bytes are immutable from this point. Yield only
		// the coordinator mutation turn; the request keeps its general-lane capacity slot
		// until its response/cache settlement, including while artifact I/O is in flight.
		laneTicket?.TryCompleteMutation();

		Func<string> writeAndCommit = () => {
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

		try {
			// Every file in a ledgered session directory must be an admitted child — the marker
			// included — or the next admission's fail-closed verification rejects the store.
			var rootIdentity = artifactFs!.RootIdentity;
			var markerName = ".dnspy-mcp-session.json";
			var markerBytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {
				schema_version = "dnspy.debug.artifact.v1",
				session_id = sessionId,
				created_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				artifact_root_volume_serial = rootIdentity.VolumeSerial,
				artifact_root_file_id = rootIdentity.FileId,
			}, CanonicalOptions));
			var markerAdmit = ledger.AdmitArtifactWrite(sessionId, markerName, markerBytes, active.Record);
			// The marker is constant per session: a second dump in the same session re-admits
			// it and the ledger answers AlreadyExists — that is the idempotent success.
			if (markerAdmit != ArtifactStoreLedger.AdmitResult.Ok && markerAdmit != ArtifactStoreLedger.AdmitResult.AlreadyExists)
				return Fail(coordinator, MapAdmit(markerAdmit));
			string sha256;
			using (var sha = SHA256.Create())
				sha256 = ConvertHexShim.ToHexString(sha.ComputeHash(artifactBytes)).ToLowerInvariant();
			// The admission reserves the quota and creates the empty child; this call is its
			// active writer, so the bytes go straight into the ledgered file.
			var admit = ledger.AdmitArtifactWrite(sessionId, childName, artifactBytes, active.Record);
			if (admit != ArtifactStoreLedger.AdmitResult.Ok)
				return Fail(coordinator, MapAdmit(admit));
			var finalPath = Path.Combine(root, sessionId, childName);

			var manifestName = childName + ".manifest.json";
			// FB-001: the manifest declares the exact byte-equivalence class and, for the
			// reconstructed branch, the producing API — a reconstruction is never presented as
			// a source-equivalent image.
			var artifactId = sessionId + "/" + childName;
			var manifest = System.Text.Json.JsonSerializer.Serialize(new {
				schema_version = "dnspy.debug.artifact.v1",
				artifact_id = artifactId,
				session_id = sessionId,
				kind,
				layout = artifactLayout,
				size = artifactBytes.Length,
				sha256,
				source_module = ModuleDtoOf(module),
				byte_equivalence = equivalence,
				reconstruction_method = reconstructionMethod,
				created_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				untrusted_sample_data = true,
			}, CanonicalOptions);
			var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest);
			var manifestAdmit = ledger.AdmitArtifactWrite(sessionId, manifestName, manifestBytes, active.Record);
			if (manifestAdmit != ArtifactStoreLedger.AdmitResult.Ok)
				return Fail(coordinator, MapAdmit(manifestAdmit));

			return Ok(coordinator, untrustedSampleData: true, result: new DumpModuleResultDto {
				Artifact = new ArtifactDto {
					ArtifactId = artifactId,
					Path = finalPath,
					Kind = kind,
					Layout = artifactLayout,
					Size = artifactBytes.Length,
					Sha256 = sha256,
					SourceModule = ModuleDtoOf(module),
					ManifestPath = Path.Combine(root, sessionId, manifestName),
				},
			});
		}
		catch (Exception ex) {
			return Fail(coordinator, DomainErrorCodes.InternalError, message: ex.GetType().Name + ": " + ex.Message);
		}
		};

		active.HandedToFinalizer = true;
		Task<string> worker;
		try {
			worker = Task.Factory.StartNew(() => {
				try { return writeAndCommit(); }
				finally { CompleteArtifactOperation(active); }
			}, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
				TaskScheduler.Default);
		}
		catch {
			active.HandedToFinalizer = false;
			throw;
		}
		var visible = Task.WhenAny(worker, active.VisibleCancellation.Task).GetAwaiter().GetResult();
		if (visible == active.VisibleCancellation.Task)
			return Fail(coordinator, DomainErrorCodes.InvalidState,
				message: "artifact cancellation is pending final I/O completion");
		return worker.GetAwaiter().GetResult();
	}

	static string MapAdmit(ArtifactStoreLedger.AdmitResult result) => result switch {
		ArtifactStoreLedger.AdmitResult.AlreadyExists => DomainErrorCodes.AlreadyExists,
		ArtifactStoreLedger.AdmitResult.LimitExceeded => DomainErrorCodes.LimitExceeded,
		ArtifactStoreLedger.AdmitResult.TargetMismatch => DomainErrorCodes.TargetMismatch,
		ArtifactStoreLedger.AdmitResult.OperationTimedOut => DomainErrorCodes.Timeout,
		ArtifactStoreLedger.AdmitResult.OperationCanceled => DomainErrorCodes.InvalidState,
		_ => DomainErrorCodes.InternalError,
	};

	static bool IsValidArtifactStem(string? value) {
		if (value is null || value.Length == 0 || value == "." || value == ".." || value.Length > 128)
			return false;
		if (value.Any(char.IsWhiteSpace) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal))
			return false;
		var deviceStem = value.Split('.')[0].ToUpperInvariant();
		if (deviceStem is "CON" or "PRN" or "AUX" or "NUL")
			return false;
		return !(deviceStem.Length == 4
			&& (deviceStem.StartsWith("COM", StringComparison.Ordinal) || deviceStem.StartsWith("LPT", StringComparison.Ordinal))
			&& deviceStem[3] >= '1' && deviceStem[3] <= '9');
	}

	// DNMCP_TEST-only: fixed by debug_test_dump — null/"raw" is the production behavior.
	volatile string? testDumpMode;

	/// <summary>
	/// FB-001 seam (ACC-032): the raw-bytes side of debug_dump_module. Production reads the
	/// on-disk image of file-layout modules; the DNMCP_TEST injection can force raw
	/// unavailable so the ForceMemory reconstruction / both-unavailable branches are live.
	/// </summary>
	public interface IRawModuleBytesSource {
		/// <summary>Raw image bytes, or null when unavailable (dynamic/in-memory or injected).</summary>
		byte[]? TryReadRawBytes(string filename, string layout);
	}

	sealed class ProductionRawModuleBytesSource : IRawModuleBytesSource {
		public byte[]? TryReadRawBytes(string filename, string layout) {
			try {
				if (layout != "file" || !File.Exists(filename))
					return null;
				return File.ReadAllBytes(filename);
			}
			catch {
				return null;
			}
		}
	}

	readonly IRawModuleBytesSource rawBytesSource = new ProductionRawModuleBytesSource();

	byte[]? ReadRawModuleBytes(RegisteredModuleRecord module) {
		if (testDumpMode is "force_memory" or "both_unavailable")
			return null;
		return rawBytesSource.TryReadRawBytes(module.Filename, module.Layout);
	}

	/// <summary>
	/// FB-001 branch 2 (ACC-032): reconstruct a managed PE from the module's ForceMemory
	/// metadata (DbgMetadataService on the DbgManager dispatcher) via dnlib's writer. The
	/// output is a valid PE but by construction NOT source-equivalent.
	/// </summary>
	byte[]? ReconstructViaForceMemory(RegisteredModuleRecord module) {
		if (metadataService is null)
			return null;
		byte[]? result = null;
		PostVoidToDispatcherSync(() => {
			DbgProcess? process;
			lock (sessionLock) process = ownedProcess;
			if (process is null)
				return;
			foreach (var runtime in process.Runtimes) {
				foreach (var live in runtime.Modules) {
					bool match = string.Equals(live.Filename ?? string.Empty, module.Filename, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(live.Name, module.Name, StringComparison.OrdinalIgnoreCase);
					if (!match)
						continue;
					var moduleDef = metadataService.TryGetMetadata(live, DbgLoadModuleOptions.ForceMemory | DbgLoadModuleOptions.AutoLoaded);
					if (moduleDef is null)
						return;
					using var ms = new MemoryStream();
					moduleDef.Write(ms);
					result = ms.ToArray();
					return;
				}
			}
		});
		return result;
	}

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
				else if (coordinator.State != DebugStates.Idle) {
					// A process event outside any claim window while a session is active is a
					// detectable ownership ambiguity (CON-DYN-012): EVT-DYN-017 + faulted. The
					// unowned object itself is never touched.
					MarkForeignProcessObserved(process.Id, sessionStartedUtc,
						process.Runtimes.Length == 0 ? "" : process.Runtimes[0].Name,
						activePlan?.RuntimeFamily ?? "", launchArchitecture ?? "");
				}
			}
			else {
				DbgProcess? owned;
				lock (sessionLock) owned = ownedProcess;
				if (process != owned)
					continue;
				process.IsRunningChanged -= OnOwnedIsRunningChanged;
				var terminalSessionId = coordinator.ActiveSessionId;
				var result = coordinator.ObserveProcessRemoved(terminalSessionId, coordinator.Generation, ownedIdentityMatch: true, exitCode: null);
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
				TerminalArtifactSession(terminalSessionId);
				sideEffectCache.MarkSessionTerminal(DateTime.UtcNow);
				controlTcs?.TrySetResult("removed");
				ReleaseLeases();
				// Session teardown must remove the OWNED dnSpy breakpoints too — clearing only
				// the maps would leak engine breakpoints that then collide with the next
				// session's Add at the same location (dnSpy returns null for duplicates).
				DbgCodeBreakpoint[]? leaked = null;
				lock (sessionLock) {
					var ownedDnSpy = dnSpyBreakpointsByMcp.Values.SelectMany(v => v).Where(b => b is not null).ToArray();
					if (ownedDnSpy.Length > 0)
						leaked = ownedDnSpy;
					bpStore = new DebugBreakpointStore();
					moduleByOwnedBp.Clear();
					mcpIdByDnSpyBreakpoint.Clear();
					dnSpyIdByMcpBreakpoint.Clear();
					dnSpyBreakpointsByMcp.Clear();
					modulesByHandle.Clear();
				}
				if (leaked is not null)
					breakpointsService?.Remove(leaked);
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
			List<RegisteredModuleRecord> previous;
			lock (sessionLock) previous = modulesByHandle.Values.ToList();
			// We are already on the DbgManager dispatcher: enumerate inline (a sync post from
			// this thread would self-wait on the dispatcher and stall the debug pump).
			var table = new List<RegisteredModuleRecord>();
			RegisterLiveModulesInto(table);
			foreach (var module in e.Objects) {
				var added = e.Added;
				var candidates = added ? table : previous;
				var record = candidates.FirstOrDefault(m => ReferenceEquals(m.LiveModule, module))
					?? candidates.FirstOrDefault(m => string.Equals(m.Filename, module.Filename ?? string.Empty, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(m.Name, module.Name, StringComparison.OrdinalIgnoreCase));
				if (added) {
					if (record is not null)
						coordinator.WriteModuleLoaded(new { module = ModuleDtoOf(record) });
				}
				else {
					coordinator.WriteModuleUnloaded(new {
						module_handle = record?.ModuleHandle ?? "",
						mvid = record?.Mvid ?? Guid.Empty.ToString("D"),
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
			// An accepted breakpoint hit settles bound=true for its owned breakpoint on the
			// consumption side too (single producer both flips it in CollectBreakInfos).
			foreach (var bi in observation.BreakInfos)
				if (bi.OwnedBreakpointId is not null)
					bpStore.MarkBound(bi.OwnedBreakpointId, true);
			var result = coordinator.ObservePaused(coordinator.ActiveSessionId, coordinator.Generation,
				ownedIdentityMatch: true, observation.BreakInfos);
			if (result.Accepted && result.SettledPauseRecord) {
				TaskCompletionSource<string>? controlTcs;
				lock (sessionLock) controlTcs = controlOutcomeTcs;
				controlTcs?.TrySetResult("paused");
			}
		}
		else if (observation.Kind == ProcessObservation.ObservationKind.Removed) {
			var terminalSessionId = coordinator.ActiveSessionId;
			var result = coordinator.ObserveProcessRemoved(terminalSessionId, coordinator.Generation,
				ownedIdentityMatch: true, observation.ExitCode);
			TaskCompletionSource<string>? controlTcs;
			lock (sessionLock) controlTcs = controlOutcomeTcs;
			if (result.Outcome == "pending-restart")
				controlTcs?.TrySetResult("removed-pending-restart");
			else {
				TerminalArtifactSession(terminalSessionId);
				sideEffectCache.MarkSessionTerminal(DateTime.UtcNow);
				controlTcs?.TrySetResult("removed");
			}
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
								lock (sessionLock) {
									mcpIdByDnSpyBreakpoint.TryGetValue(boundArgs.BoundBreakpoint.Breakpoint.Id, out ownedId);
									// ACC-035 module_handle scoping: the engine binds this
									// breakpoint against every ModuleId-equal sibling (same
									// MVID/name); only the module the creating handle addressed
									// may settle as this breakpoint's hit. The engine leaves
									// Module unset on some bind paths (disk modules) — then the
									// location's engine-unique id already scopes the hit.
									var hitModule = boundArgs.BoundBreakpoint.Module;
									if (ownedId is not null
										&& moduleByOwnedBp.TryGetValue(ownedId, out var ownerModule)
										&& ownerModule is not null
										&& hitModule is not null
										&& !ReferenceEquals(ownerModule, hitModule))
										ownedId = null;
								}
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

	void OnIsDebuggingChanged(object? sender, EventArgs e) =>
		HandleManagerDebuggingChanged(dbgManager is not null && dbgManager.IsDebugging);

	/// <summary>
	/// DbgManager debugging-state transitions (dispatcher thread). The only coordinator-visible
	/// effect is ownership recovery: when the manager stops debugging without any new object
	/// having appeared (the human UI ended all debugging), a faulted(ownership_lost) session
	/// recovers per §3.2 — EVT-DYN-018 then EVT-DYN-020(ownership_recovered), back to idle.
	/// Normal MCP terminal transitions are already idle here, so Recover no-ops for them.
	/// </summary>
	void HandleManagerDebuggingChanged(bool debugging) {
		if (!debugging && coordinator.OwnershipLostFaulted) {
			SpyInc("ownership_recovered_manager_idle");
			coordinator.Recover("manager_became_idle_without_new_objects");
		}
	}

	/// <summary>
	/// DbgManager dispatcher: an unregistered process/runtime observation while a session is
	/// active (outside any claim window). EVT-DYN-017 + faulted(ownership_lost); the ambiguous
	/// object is never operated on (ACC-025-B). Also the DNMCP_TEST injection target.
	/// </summary>
	void MarkForeignProcessObserved(int pid, DateTime startedUtc, string runtimeIdentity, string family, string arch) {
		SpyInc("foreign_process_observations");
		coordinator.MarkOwnershipLost(null, new[] { (pid, runtimeIdentity, family, arch) });
	}

	// ---- file identity leases ----

	bool TryLeaseIdentity(string path, string role, string objectKind, string? expectedSha256, out FileIdentityDto? identity, out string? error) {
		identity = null;
		error = null;
		FileStream? stream = null;
		Microsoft.Win32.SafeHandles.SafeFileHandle? directoryLease = null;
		try {
			BY_HANDLE_FILE_INFORMATION info;
			string? sha256 = null;
			IntPtr rawHandle;
			if (objectKind == "directory") {
				var raw = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
					IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
				if (raw == IntPtr.Zero || raw == new IntPtr(-1))
					throw new IOException("directory identity lease failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				directoryLease = new Microsoft.Win32.SafeHandles.SafeFileHandle(raw, ownsHandle: true);
				rawHandle = raw;
			}
			else {
				stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				identityLeases.Add(stream);
				rawHandle = stream.SafeFileHandle.DangerousGetHandle();
				using var sha = SHA256.Create();
				stream.Position = 0;
				var hash = sha.ComputeHash(stream);
				stream.Position = 0;
				sha256 = ConvertHexShim.ToHexString(hash).ToLowerInvariant();
			}
			if (!GetFileInformationByHandle(rawHandle, out info))
				throw new IOException("GetFileInformationByHandle failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			var finalPath = FinalPathOf(rawHandle);
			if (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.OrdinalIgnoreCase)) {
				error = $"{role} sha256 mismatch: expected {expectedSha256.ToLowerInvariant()}, file is {sha256}";
				// A rejected target keeps no lease: the handle must not outlive the rejection
				// (ACC-033 — a leaked read handle would block the file's legitimate rewrite).
				if (stream is not null) {
					stream.Dispose();
					identityLeases.Remove(stream);
				}
				return false;
			}
			if (directoryLease is not null)
				identityDirectoryLeases.Add(directoryLease);
			identity = new FileIdentityDto {
				Role = role,
				ObjectKind = objectKind,
				FinalPath = finalPath,
				VolumeSerial = $"0x{info.VolumeSerial:x16}",
				FileId = $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}".PadLeft(32, '0'),
				Sha256 = sha256,
			};
			return true;
		}
		catch (Exception ex) {
			// Same rule for open/read failures (e.g. file-not-found, device-not-ready).
			if (stream is not null) {
				try { stream.Dispose(); } catch { }
				identityLeases.Remove(stream);
			}
			if (directoryLease is not null) {
				try { directoryLease.Dispose(); } catch { }
				identityDirectoryLeases.Remove(directoryLease);
			}
			error = $"{role} identity lease failed: {ex.Message}";
			return false;
		}
	}

	void ReleaseLeases() {
		foreach (var lease in identityLeases)
			try { lease.Dispose(); } catch { }
		identityLeases.Clear();
		foreach (var lease in identityDirectoryLeases)
			try { lease.Dispose(); } catch { }
		identityDirectoryLeases.Clear();
		// The root lease intentionally outlives per-launch identity leases: it is held from
		// first gate use to process exit (ACC-033 root-stability contract).
	}

	// CON-DYN-011: every AllowedSampleRoot component is opened with OPEN_REPARSE_POINT,
	// without delete sharing, and retained.  Multiple committed roots can occur over a process
	// lifetime; retaining old leases is conservative and avoids ever silently dropping the
	// protection while an old debug object is winding down.
	readonly Dictionary<string, List<Microsoft.Win32.SafeHandles.SafeFileHandle>> rootChainLeases =
		new(StringComparer.OrdinalIgnoreCase);

	bool TryAcquireRootLease(string rootFull, out string? error) {
		error = null;
		rootFull = Path.GetFullPath(rootFull).TrimEnd(Path.DirectorySeparatorChar);
		if (rootChainLeases.ContainsKey(rootFull))
			return true;
		if (!Directory.Exists(rootFull)) {
			error = "AllowedSampleRoot does not exist";
			return false;
		}
		var unsupported = FindUnsupportedVolume(rootFull);
		if (unsupported is not null) {
			error = $"AllowedSampleRoot must be on NTFS (found {unsupported})";
			return false;
		}
		var components = new Stack<string>();
		for (var current = rootFull; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
			components.Push(current);
			var parent = Path.GetDirectoryName(current);
			if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
				break;
		}
		var acquired = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
		try {
			while (components.Count != 0) {
				var component = components.Pop();
				var attrs = File.GetAttributes(component);
				if ((attrs & FileAttributes.ReparsePoint) != 0)
					throw new IOException($"AllowedSampleRoot component is a reparse point: {component}");
				var raw = CreateFileW(component, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
					IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
				if (raw == IntPtr.Zero || raw == new IntPtr(-1))
					throw new IOException($"AllowedSampleRoot component lease failed: {component}",
						new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				var lease = new Microsoft.Win32.SafeHandles.SafeFileHandle(raw, ownsHandle: true);
				if (!GetFileInformationByHandle(lease.DangerousGetHandle(), out _)) {
					lease.Dispose();
					throw new IOException($"AllowedSampleRoot identity query failed: {component}",
						new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
				}
				acquired.Add(lease);
			}
			rootChainLeases.Add(rootFull, acquired);
			return true;
		}
		catch (Exception ex) {
			foreach (var lease in acquired)
				lease.Dispose();
			error = ex.Message;
			return false;
		}
	}

	/// <summary>ACC-033: non-NTFS volumes lack stable FILE_ID_INFO/share semantics — every
	/// launch input must sit on NTFS. Returns the offending filesystem name (or a not-ready
	/// marker), or null when the volume is NTFS. DriveInfo throws for a no-media device, which
	/// is itself an unsupported volume.</summary>
	static string? FindUnsupportedVolume(string? path) {
		if (string.IsNullOrEmpty(path))
			return null;
		try {
			var full = System.IO.Path.GetFullPath(path);
			var root = System.IO.Path.GetPathRoot(full);
			if (string.IsNullOrEmpty(root))
				return "PathRootUnavailable";
			var drive = new System.IO.DriveInfo(root);
			var format = drive.DriveFormat;
			return string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase) ? null : format;
		}
		catch (Exception ex) {
			return ex.GetType().Name;
		}
	}

	const uint GENERIC_READ = 0x80000000;
	const uint FILE_SHARE_READ = 0x00000001;
	const uint FILE_SHARE_WRITE = 0x00000002;
	const uint OPEN_EXISTING = 3;
	const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
	const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
		IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern uint GetFinalPathNameByHandleW(IntPtr hFile,
		System.Text.StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

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

	static string FinalPathOf(IntPtr handle) {
		var buffer = new System.Text.StringBuilder(32768);
		var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
		if (count == 0 || count >= buffer.Capacity)
			throw new IOException("GetFinalPathNameByHandleW failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		var path = buffer.ToString();
		return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
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
			// "PE\0\0" sits AT peOffset (Machine follows at +4); the optional header magic
			// decides the data-directory base: PE32 (0x10b) at 96, PE32+ (0x20b) at 112;
			// the CLR header is directory index 14.
			fs.Position = peOffset;
			if (br.ReadInt32() != 0x4550)
				return null;
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

	static List<string> ArgStrings(Dictionary<string, object>? args, string key) {
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
		// API-DYN-002: present only while a process is actively owned; null (omitted by the
		// canonical null-ignoring options) otherwise.
		[System.Text.Json.Serialization.JsonPropertyName("observed_process_state")]
		public string? ObservedProcessState { get; set; }
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
