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
using dnSpy.Contracts.Debugger.DotNet.CorDebug;

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
	};

	public bool Handles(string toolName) => HandledTools.Contains(toolName);

	[ImportingConstructor]
	public DebugSessionService([Import(AllowDefault = true)] DbgManager? dbgManager, DebugGateService gateService) {
		this.dbgManager = dbgManager;
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
				_ => null,
			};
		}
		catch (Exception ex) {
			envelope = Fail(coordinator, DomainErrorCodes.InternalError, message: ex.ToString());
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
		// A plain "break" info maps to the manual cause exactly when an issued pause record is
		// unsettled; real stop details (exception/breakpoint/step) arrive with the IMP-006/007
		// event wiring and will replace the synthesized singleton.
		var result = coordinator.ObservePaused(coordinator.ActiveSessionId, coordinator.Generation,
			ownedIdentityMatch: true, new[] { new BreakInfoObservation("break", 0) });
		if (result.Accepted && result.SettledPauseRecord) {
			TaskCompletionSource<string>? controlTcs;
			lock (sessionLock) controlTcs = controlOutcomeTcs;
			controlTcs?.TrySetResult("paused");
		}
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
}
