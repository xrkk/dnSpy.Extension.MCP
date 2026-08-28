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
	};

	public bool Handles(string toolName) => HandledTools.Contains(toolName);

	readonly DbgCodeBreakpointsService? breakpointsService;
	readonly DbgDotNetCodeLocationFactory? locationFactory;
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
		public ModuleId UpstreamId;
	}

	[ImportingConstructor]
	public DebugSessionService([Import(AllowDefault = true)] DbgManager? dbgManager,
		[Import(AllowDefault = true)] DbgCodeBreakpointsService? breakpointsService,
		[Import(AllowDefault = true)] DbgDotNetCodeLocationFactory? locationFactory,
		DebugGateService gateService) {
		this.dbgManager = dbgManager;
		this.breakpointsService = breakpointsService;
		this.locationFactory = locationFactory;
		this.gateService = gateService;
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

	public CallToolResult Execute(string toolName, Dictionary<string, object>? arguments) {
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
				_ => null,
			};
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

	static string Ok(DebugSessionCoordinator c, object result, List<string>? warnings = null) {
		var envelope = new DebugSuccessEnvelope {
			DebugContext = c.ContextSnapshot(),
			Result = result,
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

		var claimed = launchClaimTcs!.Task.Wait(ControlOperationRecord.DefaultDeadline);
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

	string? StartViaWpf(LaunchPlan plan) {
		if (dbgManager is null)
			return "DbgManager is not available";
		DebugProgramOptions options = BuildOptions(plan);
		var uiDispatcher = Application.Current?.Dispatcher;
		Func<string?> start = () => dbgManager.Start(options);
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

	async Task<string> Control(Dictionary<string, object>? args, ControlOperation operation) {
		if (!gateService.Current.EffectiveDebugLaunch)
			return Fail(coordinator, DomainErrorCodes.DebugDisabled);
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
			var localAdapter = adapter;
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

		var done = await Task.WhenAny(tcs.Task, Task.Delay(ControlOperationRecord.DefaultDeadline)).ConfigureAwait(false);
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
				Reason = PauseCauseArbiter.Manual,
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
		var claimed = launchClaimTcs!.Task.Wait(ControlOperationRecord.DefaultDeadline);
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
						Filename = module.Filename ?? module.Name,
						UpstreamId = (ModuleId)(module.Filename ?? module.Name),
					};
				}
			}
		}
	}

	bool PausedEpochMatches(Dictionary<string, object>? args) =>
		coordinator.State == DebugStates.Paused && coordinator.PauseEpoch == ArgInt(args, "pause_epoch", required: true);

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
		var enabled = args is not null && args.TryGetValue("enabled", out var e) && e is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };

		RegisteredModuleRecord? module;
		lock (sessionLock) {
			if (!modulesByHandle.TryGetValue(moduleHandle, out module)) {
				// Increment-2 registration: an unknown handle names the launch target; its disk
				// filename is the upstream identity, the request's mvid is recorded, and the
				// launch-verified target sha256 fills the disk-strong identity requirement.
				var targetFilename = activePlan?.Filename ?? string.Empty;
				var targetSha = launchIdentities.FirstOrDefault(i => i.Role == "target")?.Sha256;
				module = new RegisteredModuleRecord {
					ModuleHandle = moduleHandle,
					RuntimeHandle = "rt-0",
					Mvid = mvid,
					Sha256 = string.IsNullOrEmpty(moduleSha) ? targetSha : moduleSha,
					Filename = targetFilename,
					UpstreamId = (ModuleId)targetFilename,
				};
				modulesByHandle[moduleHandle] = module;
			}
		}
		bpStore.RegisterModule(new RegisteredModule {
			ModuleHandle = module.ModuleHandle,
			RuntimeHandle = module.RuntimeHandle,
			Mvid = mvid,
			IdentityStrength = "disk_strong",
			Sha256 = module.Sha256,
		});
		var tokenValue = ParseToken(methodToken);
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
		DebugBreakpointStore.CreateError.ModuleNotFound => DomainErrorCodes.NotFound,
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
	readonly Dictionary<string, ulong> threadIdsByHandle = new();

	static string ThreadHandleOf(DbgThread thread) => $"th-{thread.Id}";

	DbgThread? FindThreadByHandle(string threadHandle) {
		DbgProcess? process;
		lock (sessionLock) process = ownedProcess;
		if (process is null)
			return null;
		foreach (var thread in process.Threads)
			if (ThreadHandleOf(thread) == threadHandle)
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
		lock (sessionLock) {
			threadIdsByHandle.Clear();
			foreach (var t in threads)
				threadIdsByHandle[ThreadHandleOf(t)] = t.Id;
		}
		var page = threads.Skip(start).Take(pageSize).ToList();
		var dto = new PagedItemsDto {
			Items = page.Select((t, i) => (object)new ThreadInfoDto {
				ThreadHandle = ThreadHandleOf(t),
				ManagedId = t.ManagedId?.ToString(),
				OsId = t.Id.ToString(),
				Name = t.HasName() ? t.Name : null,
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
		var frames = new List<(string module, uint token, uint offset)>();
		PostVoidToDispatcherSync(() => {
			var thread = FindThreadByHandle(threadHandle);
			if (thread is null)
				return;
			var walker = thread.CreateStackWalker();
			try {
				foreach (var frame in walker.GetNextStackFrames(start + pageSize)) {
					var module = frame.Module?.Name ?? frame.Module?.Filename ?? string.Empty;
					frames.Add((module, frame.FunctionToken, frame.FunctionOffset));
				}
			}
			finally {
				walker.Close();
			}
		});
		var page = frames.Skip(start).Take(pageSize).ToList();
		var dto = new PagedItemsDto {
			Items = page.Select((f, i) => (object)new FrameInfoDto {
				FrameHandle = $"fr-{start + i}",
				Index = start + i,
				Location = new LocationDto {
					ModuleHandle = $"mod:{f.module}",
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
		return Ok(coordinator, dto);
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
		string stepId = $"step-{Interlocked.Increment(ref stepSeq)}";
		lock (sessionLock)
			currentStep = new StepRegistration { Id = stepId, Kind = kind, ThreadHandle = threadHandle };
		bool stepped = false;
		PostVoidToDispatcherSync(() => {
			var thread = FindThreadByHandle(threadHandle);
			if (thread is null)
				return;
			var stepper = thread.CreateStepper();
			stepper.Step(upstreamKind.Value, autoClose: true);
			stepped = true;
		});
		if (!stepped) {
			lock (sessionLock) currentStep = null;
			return Fail(coordinator, DomainErrorCodes.NotFound, message: "unknown thread_handle");
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
		var done = new ManualResetEventSlim();
		dbgManager.Dispatcher.BeginInvoke(new Action(() => {
			try { action(); }
			finally { done.Set(); }
		}));
		done.Wait(ControlOperationRecord.DefaultDeadline);
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
				if (coordinator.State == DebugStates.Starting && claimTcs is not null) {
					lock (sessionLock) {
						ownedProcess = process;
						adapter = new DbgProcessControlAdapter(process);
						adapter.Observation += OnAdapterObservation;
					}
				process.IsRunningChanged += OnOwnedIsRunningChanged;
				RegisterModules(process);
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
		var result = coordinator.ObservePaused(coordinator.ActiveSessionId, coordinator.Generation,
			ownedIdentityMatch: true, infos);
		if (result.Accepted && result.SettledPauseRecord) {
			TaskCompletionSource<string>? controlTcs;
			lock (sessionLock) controlTcs = controlOutcomeTcs;
			controlTcs?.TrySetResult("paused");
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
				list.Add(new BreakInfoObservation(kind, ordinal++, ownedId, stepId, policyPause));
			}
		}
		return list;
	}

	void OnAdapterObservation(ProcessObservation observation) { }

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
			fs.Position = peOffset + 4 + 20 + 208; // COM descriptor directory entry
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
