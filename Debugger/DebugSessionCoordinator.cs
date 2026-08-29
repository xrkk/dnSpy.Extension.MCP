using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Core coordinator state machine (§3.2/3.6): the seven public states, session/generation/
/// pause_epoch accounting, the 3.6 control-admission matrix, authoritative paused/removal
/// observation reconciliation and the EVT sequencing rules. This core is pure logic — it never
/// touches dnSpy objects; launch/control/observation inputs arrive as plain data and IMP-005
/// wires them through the DbgManager dispatcher and control adapters.
/// </summary>
public sealed class DebugSessionCoordinator {
	/// <summary>Session event-log retention after terminal: 10 minutes or the next launch reservation, whichever first (§3.2).</summary>
	public static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

	public enum FaultKind { None, ControlFault, OwnershipLost }

	public readonly struct ControlAdmission {
		public bool Admitted { get; init; }
		public ControlOperationRecord? Record { get; init; }
		/// <summary>INVALID_STATE allowed states (fixed §3.6 order) when not admitted.</summary>
		public IReadOnlyList<string> RequiredStates { get; init; }
	}

	public readonly struct PauseObservationResult {
		public bool Accepted { get; init; }
		/// <summary>Unique primary cause chosen for EVT-DYN-010 (arbitrated per §3.2).</summary>
		public string PrimaryCause { get; init; }
		/// <summary>True when a compatible issued/scheduled pause record settled here (state_satisfied).</summary>
		public bool SettledPauseRecord { get; init; }
	}

	public readonly struct RemovalObservationResult {
		public bool Accepted { get; init; }
		/// <summary>pending-restart (post-exit path) | settled-terminate | unexpected-exit | rejected.</summary>
		public string Outcome { get; init; }
	}

	readonly object gate = new object();
	readonly Func<string> newSessionId;
	readonly Func<string> utcNow;
	readonly Func<DateTime> wallClock;

	string? activeSessionId;
	string? lastSessionId;
	int generation;
	int pauseEpoch;
	string state = DebugStates.Idle;
	FaultKind fault = FaultKind.None;
	string observedProcessState = "unknown";
	DateTime? terminalAtUtc;
	DebugEventBuffer? activeBuffer;
	DebugEventBuffer? retainedBuffer;   // terminal session's frozen log, until retention expiry
	ControlOperationRecord? unsettledControl;
	long controlEpoch;
	bool restartReservation;
	bool abandonedRestart;
	long eventCursorCounter; // debug_context.event_cursor source

	public DebugSessionCoordinator(Func<string>? newSessionId = null, Func<string>? utcNow = null, Func<DateTime>? wallClock = null) {
		this.newSessionId = newSessionId ?? DefaultSessionId;
		this.utcNow = utcNow ?? DefaultUtcNow;
		this.wallClock = wallClock ?? (() => DateTime.UtcNow);
		static string DefaultSessionId() => System.ConvertHexShim.ToHexString(System.Security.Cryptography.RandomNumberGeneratorShim.GetBytes(12)).ToLowerInvariant();
		static string DefaultUtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
	}

	// ---- read side ----
	public string State { get { lock (gate) return state; } }
	public string? ActiveSessionId { get { lock (gate) return activeSessionId; } }
	public string? LastSessionId { get { lock (gate) return lastSessionId; } }
	public int Generation { get { lock (gate) return generation; } }

	/// <summary>
	/// Primary cause of the most recent accepted paused observation ("manual" when an issued
	/// pause settled with no higher-priority cause) — the control response reports it.
	/// </summary>
	public string LastPauseCause { get { lock (gate) return lastPauseCause; } }
	string lastPauseCause = PauseCauseArbiter.Manual;
	public int PauseEpoch { get { lock (gate) return pauseEpoch; } }
	public FaultKind Fault { get { lock (gate) return fault; } }
	public string ObservedProcessState { get { lock (gate) return observedProcessState; } }
	public bool RestartReservationHeld { get { lock (gate) return restartReservation; } }
	public bool AbandonedRestart { get { lock (gate) return abandonedRestart; } }
	public bool HasUnsettledControl { get { lock (gate) return unsettledControl != null && unsettledControl.CurrentPhase != ControlOperationRecord.Phase.Settled; } }

	public DebugContextDto ContextSnapshot() {
		lock (gate) return new DebugContextDto {
			SessionId = activeSessionId, Generation = generation, PauseEpoch = pauseEpoch,
			EventCursor = (int)eventCursorCounter, State = state,
		};
	}

	// ---- lifecycle ----

	/// <summary>
	/// idle + launch → starting (§3.6). Creates the session, increments the generation (never
	/// reused afterwards, even when the claim fails) and writes EVT-DYN-001 session_start.
	/// </summary>

	/// <summary>Module lifecycle events (EVT-DYN-006/007) with sample-derived identity.</summary>
	public void WriteModuleLoaded(object payload) {
		lock (gate)
			WriteEvent(EventKinds.ModuleLoaded, payload, untrusted: true);
	}

	public void WriteModuleUnloaded(object payload) {
		lock (gate)
			WriteEvent(EventKinds.ModuleUnloaded, payload, untrusted: true);
	}

	/// <summary>
	/// Exception observation the session policy did NOT convert into a pause (e.g. a
	/// first-chance exception under break_on=unhandled): the EVT exception is written, the
	/// process is not stopped and no paused event exists (ACC-012 captured-exception path).
	/// </summary>
	/// <summary>DNMCP_TEST-only: appends N synthetic events to the ACTIVE buffer (eviction
	/// and payload_omitted semantics become observable without thousands of HTTP calls).</summary>
	public (long written, long lost, long earliest, long lastCursor) WriteTestFlood(int count, int bytesPerEvent) {
		long written = 0;
		lock (gate) {
			if (activeBuffer is null)
				return (0, 0, 0, 0);
			for (int i = 0; i < count; i++) {
				WriteEvent("test_flood", new { seq = i, pad = new string('x', Math.Max(0, bytesPerEvent)) }, untrusted: false);
				written++;
			}
			var read = activeBuffer.Read(0, 1, null);
			return (written, activeBuffer.EventsLost, read.EarliestCursor, activeBuffer.LastCursor);
		}
	}

	public void WriteObservedException(bool firstChance, bool unhandled, string type, string message) {
		lock (gate)
			WriteEvent(EventKinds.Exception, new {
				first_chance = firstChance, unhandled, type, message, thread_handle = (string?)null,
			}, untrusted: true);
	}

	public bool BeginLaunch(string requestId, string launchMode, string runtimeFamily, string architecture) {
		lock (gate) {
			if (state != DebugStates.Idle)
				return false;
			ExpireRetainedLog();
			// ACC-005: the next launch's start reservation releases the retained terminal log —
			// reads of the old session_id answer NOT_FOUND from this point on.
			retainedBuffer = null;
			lastSessionId = null;
			terminalAtUtc = null;
			activeSessionId = newSessionId();
			// Cursors are session-scoped (start at 1, strictly increasing within the session).
			eventCursorCounter = 0;
			generation = 1;
			pauseEpoch = 0;
			fault = FaultKind.None;
			abandonedRestart = false;
			observedProcessState = "unknown";
			activeBuffer = new DebugEventBuffer(activeSessionId, utcNow: utcNow);
			SetState(DebugStates.Starting);
			WriteEvent(EventKinds.SessionStart, new {
				operation = "launch", runtime_family = runtimeFamily, architecture = architecture, launch_mode = launchMode,
			}, untrusted: false);
			return true;
		}
	}

	/// <summary>Claim success → running, or paused with the fixed initial break_kind reason (no BreakInfos arbitration for the initial stop).</summary>
	public bool MarkLaunchClaimSucceeded(bool startsPaused, string? initialPauseReason) {
		lock (gate) {
			if (state != DebugStates.Starting || activeSessionId is null)
				return false;
			if (!startsPaused) {
				observedProcessState = "running";
				SetState(DebugStates.Running);
				// Restart reached running: its reservation is released only now (§3.2).
				restartReservation = false;
				return true;
			}
			// Entering paused: pause_epoch increments (entering and leaving both count).
			observedProcessState = "paused";
			pauseEpoch++;
			SetState(DebugStates.Paused);
			restartReservation = false;
			WriteEvent(EventKinds.Paused, new {
				reason = initialPauseReason ?? "unknown",
			}, untrusted: false);
			return true;
		}
	}

	/// <summary>Start/claim failure before ownership: EVT-DYN-002 then EVT-DYN-020(start_failed), release everything, back to idle.</summary>
	public bool MarkLaunchFailed(string errorCode) {
		lock (gate) {
			if (state != DebugStates.Starting && state != DebugStates.Faulted)
				return false;
			WriteEvent(EventKinds.StartFailed, new { operation = "launch", error = ErrorBody(errorCode, state) }, untrusted: false);
			TerminalSession("start_failed", null);
			return true;
		}
	}

	/// <summary>True only in faulted(ownership_lost) — ACC-025-B: every enabled control answers
	/// OWNERSHIP_LOST (never a bare INVALID_STATE) while the ambiguity is unresolved.</summary>
	public bool OwnershipLostFaulted => state == DebugStates.Faulted && fault == FaultKind.OwnershipLost;

	/// <summary>Detectable ownership ambiguity: EVT-DYN-017 and faulted(ownership_lost). No force reset.</summary>
	public bool MarkOwnershipLost(string? claimRequestId, IReadOnlyList<(int pid, string runtimeIdentity, string family, string arch)> observed) {
		lock (gate) {
			if (state == DebugStates.Idle)
				return false;
			fault = FaultKind.OwnershipLost;
			SetState(DebugStates.Faulted);
			WriteEvent(EventKinds.OwnershipLost, new {
				claim_request_id = claimRequestId,
				observed_processes = observed.Select(o => new { pid = o.pid, runtime_identity = o.runtimeIdentity, runtime_family = o.family, architecture = o.arch }).ToList(),
				observed_processes_truncated = false,
				recovery = "manual_resolve_then_wait_idle",
			}, untrusted: true);
			return true;
		}
	}

	/// <summary>Recovery from faulted per §3.2: only the two observation-based reasons; writes EVT-DYN-018 then EVT-DYN-020(ownership_recovered) and returns to idle.</summary>
	public bool Recover(string reason) {
		lock (gate) {
			if (state != DebugStates.Faulted || fault != FaultKind.OwnershipLost)
				return false;
			if (reason != "owned_process_exited" && reason != "manager_became_idle_without_new_objects")
				return false;
			WriteEvent(EventKinds.Recovery, new { reason = reason, terminal_state = DebugStates.Idle }, untrusted: false);
			TerminalSession("ownership_recovered", null);
			return true;
		}
	}

	// ---- control admission (§3.6 matrix) ----

	/// <summary>
	/// Admits at most one unsettled control operation per active session per the fixed matrix:
	/// pause@running; terminate@running,paused,faulted(ControlFault only); restart@running,paused.
	/// The deadline starts here (control-lane admission).
	/// </summary>
	public ControlAdmission TryBeginControl(ControlOperation operation, string requestId) {
		lock (gate) {
			var required = operation switch {
				ControlOperation.Pause => new[] { DebugStates.Running },
				ControlOperation.Terminate => new[] { DebugStates.Running, DebugStates.Paused, DebugStates.Faulted },
				ControlOperation.Restart => new[] { DebugStates.Running, DebugStates.Paused },
				_ => Array.Empty<string>(),
			};
			bool stateOk = required.Contains(state);
			if (operation == ControlOperation.Terminate && state == DebugStates.Faulted && fault != FaultKind.ControlFault)
				stateOk = false;
			if (!stateOk || activeSessionId is null || HasUnsettledControlLocked)
				return new ControlAdmission { Admitted = false, RequiredStates = required };
			var record = ControlOperationRecord.Begin(activeSessionId, generation, ++controlEpoch, requestId, operation, state);
			unsettledControl = record;
			if (operation == ControlOperation.Restart) {
				restartReservation = true;
				SetState(DebugStates.Restarting);
			}
			else if (operation == ControlOperation.Terminate) {
				SetState(DebugStates.Stopping);
			}
			return new ControlAdmission { Admitted = true, Record = record, RequiredStates = required };
		}
	}

	bool HasUnsettledControlLocked => unsettledControl != null && unsettledControl.CurrentPhase != ControlOperationRecord.Phase.Settled;

	/// <summary>
	/// Deadline or explicit-failure settlement (§3.2): scheduled-phase failure restores
	/// prior_state (restart also releases its reservation); issued-phase failure enters
	/// confirmed-owned control-faulted (session kept, restart additionally sets abandoned and
	/// releases the reservation). Writes exactly one EVT-DYN-021 when this call settles first.
	/// </summary>
	public bool SettleControlFailure(ControlOperationRecord record, string errorCode) {
		lock (gate) {
			if (!record.TrySettle())
				return false;
			bool wasIssued = record.CurrentPhase == ControlOperationRecord.Phase.Settled && recordWasIssued(record);
			string evtState = state;
			if (!wasIssued) {
				// Upstream was never called: restore prior public state.
				SetState(record.PriorState);
				if (record.Operation == ControlOperation.Restart)
					restartReservation = false;
			}
			else {
				fault = FaultKind.ControlFault;
				SetState(DebugStates.Faulted);
				if (record.Operation == ControlOperation.Restart) {
					restartReservation = false;
					abandonedRestart = true;
				}
			}
			WriteEvent(EventKinds.ControlFailed, new {
				operation = record.Operation switch {
					ControlOperation.Pause => "pause",
					ControlOperation.Terminate => "terminate",
					_ => "restart",
				},
				request_id = record.RequestId,
				control_epoch = record.ControlEpoch,
				phase = wasIssued ? "issued" : "scheduled",
				error = ErrorBody(errorCode, evtState),
				late_completion_policy = record.LateCompletionPolicy,
			}, untrusted: false);
			return true;
		}
	}

	bool recordWasIssued(ControlOperationRecord record) => issuedRecords.Contains(record);
	readonly HashSet<ControlOperationRecord> issuedRecords = new();

	/// <summary>Dispatcher callback marks the record issued right before the upstream call.</summary>
	public bool MarkControlIssued(ControlOperationRecord record) {
		lock (gate) {
			if (!record.TryMarkIssued())
				return false;
			lock (issuedRecordsSync) issuedRecords.Add(record);
			return true;
		}
	}
	readonly object issuedRecordsSync = new();

	// ---- authoritative observations (§3.2) ----

	/// <summary>
	/// running→paused observation. Validated by session/generation/owned identity; the unique
	/// primary cause comes from the closed-priority arbiter; EVT-DYN-010 is written exactly once
	/// per public transition and a compatible unsettled pause record settles with
	/// request_effect=state_satisfied.
	/// </summary>
	public PauseObservationResult ObservePaused(string? sessionId, int obsGeneration, bool ownedIdentityMatch,
		IReadOnlyList<BreakInfoObservation> breakInfos) {
		lock (gate) {
			var valid = sessionId != null && sessionId == activeSessionId && obsGeneration == generation && ownedIdentityMatch;
			if (!valid)
				return default; // old session/generation/non-owned: no state change, no events
			observedProcessState = "paused";
			bool settledPause = false;
			string cause = "unknown";
			switch (state) {
				case DebugStates.Running: {
					bool issuedPause = unsettledControl is { Operation: ControlOperation.Pause }
						&& unsettledControl.CurrentPhase == ControlOperationRecord.Phase.Issued;
					cause = PauseCauseArbiter.SelectPrimaryCause(breakInfos, issuedPause);
					lastPauseCause = cause;
					pauseEpoch++;
					SetState(DebugStates.Paused);
					WriteEvent(EventKinds.Paused, new { reason = cause }, untrusted: false);
					WritePauseDetails(breakInfos);
					if (unsettledControl is { Operation: ControlOperation.Pause } record && record.TrySettle())
						settledPause = true;
					return new PauseObservationResult { Accepted = true, PrimaryCause = cause, SettledPauseRecord = settledPause };
				}
				case DebugStates.Paused:
					// Duplicate observation: dedupe only, never re-emit or re-count.
					return new PauseObservationResult { Accepted = true, PrimaryCause = cause };
				case DebugStates.Restarting:
				case DebugStates.Stopping:
				case DebugStates.Faulted:
					// Keep lifecycle state; only the observed state changed.
					return new PauseObservationResult { Accepted = true, PrimaryCause = cause };
				default:
					return default;
			}
		}
	}

	void WritePauseDetails(IReadOnlyList<BreakInfoObservation> breakInfos) {
		foreach (var info in PauseCauseArbiter.DetailOrder(breakInfos)) {
			switch (info.Kind) {
				case PauseCauseArbiter.Exception:
					WriteEvent(EventKinds.Exception, new { first_chance = false, unhandled = true, type = "exception", message = "", thread_handle = (string?)null }, untrusted: true);
					break;
				case PauseCauseArbiter.Breakpoint:
					if (info.OwnedBreakpointId != null)
						WriteEvent(EventKinds.BreakpointHit, new { breakpoint_id = info.OwnedBreakpointId, thread_handle = (string?)"", location = new { module_handle = (string?)"" } }, untrusted: false);
					break;
				case PauseCauseArbiter.Step:
					if (info.StepId != null)
						WriteEvent(EventKinds.StepCompleted, new { step_id = info.StepId, kind = info.StepKind ?? "into", thread_handle = (string?)"", location = new { module_handle = (string?)"" } }, untrusted: false);
					break;
			}
		}
	}

	/// <summary>
	/// Owned-process removal: the authoritative terminal observation (§3.2). Settles a pending
	/// terminate (either phase) as success, advances a pending non-abandoned restart into the
	/// post-exit stopping state, and otherwise terminates the session (target_exited).
	/// </summary>
	public RemovalObservationResult ObserveProcessRemoved(string? sessionId, int obsGeneration, bool ownedIdentityMatch, int? exitCode) {
		lock (gate) {
			var valid = sessionId != null && sessionId == activeSessionId && obsGeneration == generation && ownedIdentityMatch;
			if (!valid)
				return new RemovalObservationResult { Accepted = false, Outcome = "rejected" };
			observedProcessState = "exited";
			WriteEvent(EventKinds.ProcessExited, new { process_handle = (string?)"", exit_code = exitCode ?? 0 }, untrusted: false);
			var control = unsettledControl;
			if (control is { Operation: ControlOperation.Restart } && !abandonedRestart
				&& control.CurrentPhase != ControlOperationRecord.Phase.Settled) {
				// Restart keeps the session and reservation: post-exit stopping, no session_end here.
				SetState(DebugStates.Stopping);
				return new RemovalObservationResult { Accepted = true, Outcome = "pending-restart" };
			}
			if (control is { Operation: ControlOperation.Terminate } && control.TrySettle()) {
				TerminalSession("terminated", exitCode);
				return new RemovalObservationResult { Accepted = true, Outcome = "settled-terminate" };
			}
			// Unexpected exit: settle any incompatible pause record first (INTERNAL_ERROR, current idle).
			if (control is { Operation: ControlOperation.Pause } unsettledPause && unsettledPause.TrySettle()) {
				WriteEvent(EventKinds.ControlFailed, new {
					operation = "pause", request_id = unsettledPause.RequestId, control_epoch = unsettledPause.ControlEpoch,
					phase = unsettledPause.CurrentPhase == ControlOperationRecord.Phase.Settled && recordWasIssued(unsettledPause) ? "issued" : "scheduled",
					error = ErrorBody("INTERNAL_ERROR", DebugStates.Idle),
					late_completion_policy = unsettledPause.LateCompletionPolicy,
				}, untrusted: false);
			}
			TerminalSession("target_exited", exitCode);
			return new RemovalObservationResult { Accepted = true, Outcome = "unexpected-exit" };
		}
	}

	/// <summary>Restart relaunch after post-exit stopping: keeps the session, increments the
	/// generation, re-enters starting and settles the restart control record — its response
	/// (new generation, claim deadline) is satisfied once the new Start is issued. The restart
	/// reservation itself persists until running/paused is reached.</summary>
	public bool BeginRestartRelaunch() {
		lock (gate) {
			if (state != DebugStates.Stopping || !restartReservation)
				return false;
			if (unsettledControl is { Operation: ControlOperation.Restart } pendingRestart)
				pendingRestart.TrySettle();
			generation++;
			observedProcessState = "unknown";
			SetState(DebugStates.Starting);
			return true;
		}
	}

	/// <summary>Leaving paused (continue/step/restart resume): pause_epoch increments and EVT-DYN-011 is written.</summary>
	public bool MarkResumed(string reason) {
		lock (gate) {
			if (state != DebugStates.Paused)
				return false;
			pauseEpoch++;
			observedProcessState = "running";
			SetState(DebugStates.Running);
			WriteEvent(EventKinds.Continued, new { reason = reason }, untrusted: false);
			return true;
		}
	}

	// ---- session reads with terminal retention (§3.2) ----

	public DebugEventBuffer.ReadResult? ReadEvents(string? sessionId, long afterCursor, int limit, IReadOnlyCollection<string>? kinds) {
		lock (gate) {
			if (sessionId == null)
				return null;
			if (sessionId == activeSessionId && activeBuffer != null)
				return activeBuffer.Read(afterCursor, limit, kinds);
			if (sessionId == lastSessionId && retainedBuffer != null && !RetentionExpired)
				return retainedBuffer.Read(afterCursor, limit, kinds);
			return null; // NOT_FOUND
		}
	}

	bool RetentionExpired => terminalAtUtc is { } t && (wallClock() - t) >= TerminalRetention;

	void ExpireRetainedLog() {
		if (retainedBuffer != null && RetentionExpired) {
			retainedBuffer = null;
			lastSessionId = null;
			terminalAtUtc = null;
		}
	}

	// ---- internals ----

	void SetState(string next) => state = next;

	void TerminalSession(string reason, int? exitCode) {
		WriteEvent(EventKinds.SessionEnd, new {
			reason = reason,
			exit_code = reason is "terminated" or "target_exited" ? (int?)exitCode ?? 0 : (int?)null,
			terminal_state = DebugStates.Idle,
		}, untrusted: false);
		lastSessionId = activeSessionId;
		terminalAtUtc = wallClock();
		retainedBuffer = activeBuffer;
		activeSessionId = null;
		activeBuffer = null;
		unsettledControl = null;
		restartReservation = false;
		fault = FaultKind.None;
		abandonedRestart = false;
		observedProcessState = "unknown";
		SetState(DebugStates.Idle);
	}

	static object ErrorBody(string code, string currentState) {
		var (message, recovery) = DomainErrorDto.Lookup(code);
		return new { code = code, message = message, recovery = recovery, current_state = currentState, required_states = Array.Empty<string>() };
	}

	void WriteEvent(string kind, object payload, bool untrusted) {
		var buffer = activeBuffer;
		if (buffer is null)
			return;
		var context = new {
			session_id = activeSessionId,
			generation = generation,
			pause_epoch = pauseEpoch,
			event_cursor = (int)(eventCursorCounter + 1),
			state = state,
		};
		var envelope = new {
			schema_version = DebugWire.SchemaVersion,
			cursor = eventCursorCounter + 1,
			timestamp_utc = utcNow(),
			kind,
			debug_context = context,
			payload,
			untrusted_sample_data = untrusted,
		};
		var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions {
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		});
		eventCursorCounter++;
		buffer.Append(kind, json, eventCursorCounter);
	}
}
