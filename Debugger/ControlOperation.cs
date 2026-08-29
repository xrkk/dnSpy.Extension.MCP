using System;
using System.Diagnostics;
using System.Threading;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>Control operations that run under a ControlOperationRecord (§3.2).</summary>
public enum ControlOperation {
	Pause,
	Terminate,
	Restart,
}

/// <summary>
/// One pause/terminate/restart attempt (§3.2). The 30-second monotonic deadline starts at
/// control-lane admission (lane slot reserved, upstream Break/Terminate not yet delivered) and
/// covers lane wait, artifact-cancellation handshake, dispatcher queueing and the wait for the
/// authoritative state observation. Phases move scheduled → issued → settled with lock-free
/// compare-and-swap transitions so the deadline timer, an explicit failure and a compatible
/// authoritative observation can each race the others but only one of them settles — the
/// response, the EVT-DYN-021 event, the cache entry and the lane slot are released exactly once.
/// The record holds no dnSpy objects; the deadline path only mutates this thread-safe metadata.
/// </summary>
public sealed class ControlOperationRecord {
	/// <summary>Fixed control deadline (CON-DYN-009/API-DYN-001.control_operation_seconds).</summary>
	public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

	public enum Phase { Scheduled, Issued, Settled }

	public string SessionId { get; }
	public int Generation { get; }
	/// <summary>Monotonic per-coordinator sequence; orders local records only, never claims to exist upstream.</summary>
	public long ControlEpoch { get; }
	public string RequestId { get; }
	public ControlOperation Operation { get; }
	/// <summary>Public coordinator state before the operation was admitted.</summary>
	public string PriorState { get; }
	/// <summary>Fixed EVT-DYN-021 late-completion policy for this operation (§3.5).</summary>
	public string LateCompletionPolicy { get; }

	int phase; // Phase value, guarded by Interlocked
	readonly long deadlineTimestamp;

	ControlOperationRecord(string sessionId, int generation, long controlEpoch, string requestId,
		ControlOperation operation, string priorState, long deadlineTimestamp) {
		SessionId = sessionId;
		Generation = generation;
		ControlEpoch = controlEpoch;
		RequestId = requestId;
		Operation = operation;
		PriorState = priorState;
		this.deadlineTimestamp = deadlineTimestamp;
		phase = (int)Phase.Scheduled;
		LateCompletionPolicy = operation switch {
			ControlOperation.Pause => "reconcile_owned_pause",
			ControlOperation.Terminate => "finish_owned_termination_only",
			ControlOperation.Restart => "finish_restart_as_failed",
			_ => throw new ArgumentOutOfRangeException(nameof(operation)),
		};
	}

	public static ControlOperationRecord Begin(string sessionId, int generation, long controlEpoch,
		string requestId, ControlOperation operation, string priorState, TimeSpan? deadline = null,
		long? admissionTimestamp = null) {
		// Stopwatch timestamps are monotonic per machine; the deadline is a raw timestamp
		// comparison so the timer never needs to touch any dnSpy object.
		var duration = deadline ?? DefaultDeadline;
		var deadlineTimestamp = (admissionTimestamp ?? Stopwatch.GetTimestamp())
			+ (long)(duration.TotalSeconds * Stopwatch.Frequency);
		return new ControlOperationRecord(sessionId, generation, controlEpoch, requestId, operation, priorState, deadlineTimestamp);
	}

	public Phase CurrentPhase => (Phase)Volatile.Read(ref phase);

	/// <summary>True once the monotonic deadline has passed at the given reading.</summary>
	public bool IsExpired(long nowTimestamp) => nowTimestamp >= deadlineTimestamp;

	/// <summary>True when the fixed deadline has already passed.</summary>
	public bool IsExpiredNow => IsExpired(Stopwatch.GetTimestamp());

	/// <summary>
	/// scheduled → issued, called by the dispatcher callback right before invoking upstream. A
	/// record that was already settled (deadline or explicit failure won the race) refuses — the
	/// late callback must not call upstream.
	/// </summary>
	public bool TryMarkIssued() {
		while (true) {
			int observed = Volatile.Read(ref phase);
			if (observed == (int)Phase.Settled)
				return false;
			if (observed == (int)Phase.Issued)
				return false;
			if (Interlocked.CompareExchange(ref phase, (int)Phase.Issued, (int)Phase.Scheduled) == (int)Phase.Scheduled)
				return true;
		}
	}

	/// <summary>
	/// Settles the record (from scheduled or issued). Returns true exactly once — every later
	/// call returns false so the response, EVT-DYN-021, cache settlement and lane release each
	/// happen a single time.
	/// </summary>
	public bool TrySettle() {
		while (true) {
			int observed = Volatile.Read(ref phase);
			if (observed == (int)Phase.Settled)
				return false;
			if (Interlocked.CompareExchange(ref phase, (int)Phase.Settled, observed) == observed)
				return true;
		}
	}
}
