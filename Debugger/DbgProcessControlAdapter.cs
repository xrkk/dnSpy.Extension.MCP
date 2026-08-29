using System;
using System.Collections.Generic;
using dnSpy.Contracts.Debugger;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// One upstream process observation, stripped of any request/control correlation (EVD-API-011/012:
/// stock dnSpy v6.6.0 events carry no request_id, no control_epoch and no caller token — the
/// adapter must never fabricate them). Identity fields let the coordinator validate the
/// session/generation/owned chain; BreakInfos arrive already classified with original ordinals.
/// </summary>
public sealed class ProcessObservation {
	public enum ObservationKind { Paused, Removed }

	public ObservationKind Kind { get; init; }
	public int Pid { get; init; }
	/// <summary>Process start time in UTC; part of the owned-identity chain.</summary>
	public DateTime StartedUtc { get; init; }
	public int? ExitCode { get; init; }
	/// <summary>Classified BreakInfos snapshot for Paused observations (original ordinals preserved).</summary>
	public IReadOnlyList<BreakInfoObservation> BreakInfos { get; init; } = Array.Empty<BreakInfoObservation>();
}

/// <summary>
/// Seam between the pure coordinator and the owned dnSpy process (IMP-005). Break/Terminate are
/// void async commands upstream: a successful Post means DELIVERED, never completed — completion
/// is only ever an authoritative observation. Synchronous exceptions that can be attributed to
/// exactly this record surface as <see cref="PostResult.ExplicitFailure"/>. Observations are
/// raised for whatever the upstream actually did, without control tokens.
/// </summary>
public interface IDbgProcessControlAdapter : IDisposable {
	public enum PostResult { Delivered, ExplicitFailure }

	/// <summary>Posts Break() for the owned process. Void upstream — delivered, not completed.</summary>
	PostResult PostBreak(ControlOperationRecord forRecord);

	/// <summary>Posts Terminate() for the owned process. Void upstream — delivered, not completed.</summary>
	PostResult PostTerminate(ControlOperationRecord forRecord);

	/// <summary>Raised on the dispatcher thread for every upstream paused/removal observation.</summary>
	event Action<ProcessObservation>? Observation;
}

/// <summary>
/// Production adapter over the owned <see cref="DbgProcess"/>. Posts the void async commands
/// (the caller is responsible for invoking this on the DbgManager dispatcher) and forwards
/// upstream paused/removal observations. It never treats a void return as completion and never
/// attaches request/control tokens to observations.
/// </summary>
public sealed class DbgProcessControlAdapter : IDbgProcessControlAdapter {
	readonly DbgProcess ownedProcess;

	public DbgProcessControlAdapter(DbgProcess ownedProcess) {
		this.ownedProcess = ownedProcess;
	}

	public event Action<ProcessObservation>? Observation;

	public IDbgProcessControlAdapter.PostResult PostBreak(ControlOperationRecord forRecord) {
		try {
			DebugSessionService.SpyInc("adapter_break_posts");
			ownedProcess.Break();
			return IDbgProcessControlAdapter.PostResult.Delivered;
		}
		catch (Exception) {
			// Synchronous failure attributable to this exact record: the caller maps it to the
			// record's explicit-failure settlement; it is not an observation.
			return IDbgProcessControlAdapter.PostResult.ExplicitFailure;
		}
	}

	public IDbgProcessControlAdapter.PostResult PostTerminate(ControlOperationRecord forRecord) {
		try {
			DebugSessionService.SpyInc("adapter_terminate_posts");
			ownedProcess.Terminate();
			return IDbgProcessControlAdapter.PostResult.Delivered;
		}
		catch (Exception) {
			return IDbgProcessControlAdapter.PostResult.ExplicitFailure;
		}
	}

	/// <summary>Called by the dispatcher-side event wiring (IMP-005 launch flow) to forward observations.</summary>
	public void RaiseObservation(ProcessObservation observation) => Observation?.Invoke(observation);

	public void Dispose() { }
}

/// <summary>
/// Scriptable test adapter: posts either deliver or a synchronous explicit failure, and lets the
/// test emit observations independently — accepted / explicit_failure / never_complete /
/// late_complete scenarios are all expressible without any real process.
/// </summary>
public sealed class FakeDbgProcessControlAdapter : IDbgProcessControlAdapter {
	public bool FailOnPost { get; set; }
	public int BreakPosts;
	public int TerminatePosts;
	public IReadOnlyList<BreakInfoObservation> NextPauseBreakInfos { get; set; } = Array.Empty<BreakInfoObservation>();

	public event Action<ProcessObservation>? Observation;

	public IDbgProcessControlAdapter.PostResult PostBreak(ControlOperationRecord forRecord) {
		BreakPosts++;
		return FailOnPost ? IDbgProcessControlAdapter.PostResult.ExplicitFailure : IDbgProcessControlAdapter.PostResult.Delivered;
	}

	public IDbgProcessControlAdapter.PostResult PostTerminate(ControlOperationRecord forRecord) {
		TerminatePosts++;
		return FailOnPost ? IDbgProcessControlAdapter.PostResult.ExplicitFailure : IDbgProcessControlAdapter.PostResult.Delivered;
	}

	public void EmitPaused(int pid, DateTime startedUtc, IReadOnlyList<BreakInfoObservation>? breakInfos = null) =>
		Observation?.Invoke(new ProcessObservation {
			Kind = ProcessObservation.ObservationKind.Paused, Pid = pid, StartedUtc = startedUtc,
			BreakInfos = breakInfos ?? NextPauseBreakInfos,
		});

	public void EmitRemoved(int pid, DateTime startedUtc, int exitCode = 0) =>
		Observation?.Invoke(new ProcessObservation {
			Kind = ProcessObservation.ObservationKind.Removed, Pid = pid, StartedUtc = startedUtc, ExitCode = exitCode,
		});

	public void Dispose() { }
}
