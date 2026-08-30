using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// IO seam of the artifact store: the production side enumerates and observes through held
/// Windows handles under the store lock; tests use an in-memory model. No member may mutate
/// anything except the two Create operations, which must fail when the target already exists.
/// </summary>
public interface IArtifactStoreFs {
	/// <summary>Direct child names of the leased ArtifactRoot (at most 129 are read).</summary>
	IReadOnlyList<string> EnumerateRootChildren();
	/// <summary>Direct child names of a session directory (at most 4097 are read).</summary>
	IReadOnlyList<string> EnumerateSessionChildren(string sessionId);
	bool SessionDirectoryExists(string sessionId);
	/// <summary>
	/// Validate and retain a read-only, no-delete lease for an existing direct-child directory.
	/// Returns false for files, reparse points, path substitutions or inaccessible directories.
	/// </summary>
	bool TryLeaseExistingSessionDirectory(string sessionId);
	/// <summary>Handle-derived identity + length of a child file, or null when absent.</summary>
	(string VolumeSerial, string FileId, long Length)? ObserveChild(string sessionId, string relativeName);
	/// <summary>CreateDirectory of a direct child; must throw when the name exists.</summary>
	void CreateSessionDirectory(string sessionId);
	/// <summary>
	/// CreateNew and commit a child through one held handle (no overwrite).  The returned
	/// record must be derived from that handle after the final write/flush; production keeps
	/// the handle leased for the process lifetime.  A null payload is a quota-test-only length
	/// seam and must never be used by the production implementation.
	/// </summary>
	ArtifactStoreLedger.ChildRecord CreateChildFile(string sessionId, string relativeName,
		long length, byte[]? payload, ArtifactOperationRecord? operation);
}

/// <summary>
/// Pure artifact-store ledger and admission state machine (CON-DYN-008): session directories are
/// active_writer or known_retained (both ledger-verified, capacity-counting) — every other
/// direct child is stale_untrusted (read-only quota counting, never deleted). Seven fixed quotas
/// are checked inside the store lock BEFORE any creation or write with checked adds; violations
/// are LIMIT_EXCEEDED with zero tree delta. Next-admission re-verification fails closed with
/// TARGET_MISMATCH on any unknown/changed child. Terminal transfer to known_retained requires
/// the exact closed child-name set and per-child identity/length; a mismatch marks the session
/// stale_untrusted. Nothing is ever auto-deleted, truncated, moved or overwritten.
/// </summary>
public sealed class ArtifactStoreLedger {
	public const int MaxRetainedSessions = 128;
	public const int MaxRootChildren = 128;
	public const int MaxSessionChildren = 4096;
	public const int MaxStoreChildren = 4096;
	public const long MaxFileBytes = 536870912;
	public const long MaxSessionBytes = 1073741824;
	public const long MaxStoreBytes = 8589934592;

	public sealed class ChildRecord {
		public string RelativeName { get; }
		public string VolumeSerial { get; }
		public string FileId { get; }
		public long Length { get; }
		public string Sha256 { get; }
		public string Status { get; }
		public ChildRecord(string relativeName, string volumeSerial, string fileId, long length,
			string sha256, string status = "committed") {
			RelativeName = relativeName; VolumeSerial = volumeSerial; FileId = fileId;
			Length = length; Sha256 = sha256; Status = status;
		}
	}

	public sealed class ArtifactWriteInterruptedException : Exception {
		public ChildRecord Record { get; }
		public AdmitResult Result { get; }
		public ArtifactWriteInterruptedException(ChildRecord record, AdmitResult result) {
			Record = record; Result = result;
		}
	}

	readonly object gate = new();
	readonly IArtifactStoreFs fs;
	// Session state: active writers, process-lifetime known-retained ledgers, and stale names.
	readonly Dictionary<string, List<ChildRecord>> ledgerSessions = new(StringComparer.Ordinal);
	readonly Dictionary<string, List<ChildRecord>> retainedSessions = new(StringComparer.Ordinal);
	readonly Dictionary<string, List<ChildRecord>> staleSessions = new(StringComparer.Ordinal);
	readonly HashSet<string> invalidStaleNames = new(StringComparer.Ordinal);
	readonly long maxFile, maxSession, maxStore;
	readonly int maxSessions;
	bool initialized;
	bool startupLimitExceeded;

	public ArtifactStoreLedger(IArtifactStoreFs fs,
		int maxSessions = MaxRetainedSessions, long maxFile = MaxFileBytes,
		long maxSession = MaxSessionBytes, long maxStore = MaxStoreBytes) {
		this.fs = fs;
		this.maxSessions = maxSessions;
		this.maxFile = maxFile;
		this.maxSession = maxSession;
		this.maxStore = maxStore;
	}

	public int LedgerSessionCount { get { lock (gate) return ledgerSessions.Count + retainedSessions.Count; } }
	public int StaleCount { get { lock (gate) return staleSessions.Count + invalidStaleNames.Count; } }
	public long LedgerBytes { get { lock (gate) return AllLedgerChildren().Sum(c => c.Length); } }
	public int AbortedOwnedCount { get { lock (gate) return AllLedgerChildren().Count(c => c.Status == "aborted_owned"); } }
	/// <summary>Retained bytes never re-read or re-hashed after commit (retained_bytes_hashed=0).</summary>
	public long RetainedBytesHashed => 0;

	/// <summary>
	/// Startup scan: the in-process writer ledger starts empty. Existing session directories are
	/// leased and snapshotted as stale_untrusted: their marker is never trusted, their identity /
	/// length is re-verified before every admission, and their capacity counts against all store
	/// quotas. Invalid objects remain fail-closed without being modified.
	/// </summary>
	public void Initialize() {
		lock (gate) {
			initialized = true;
			var roots = fs.EnumerateRootChildren();
			if (roots.Count > MaxRootChildren)
				startupLimitExceeded = true;
			foreach (var name in roots) {
				if (TrySnapshotStaleSession(name, out var children, out var limitExceeded))
					staleSessions[name] = children;
				else
					invalidStaleNames.Add(name);
				startupLimitExceeded |= limitExceeded;
			}
			if (staleSessions.Count + invalidStaleNames.Count > maxSessions)
				startupLimitExceeded = true;
			if (StaleChildren().Count() > MaxStoreChildren
				|| SumWouldExceed(StaleChildren(), maxStore))
				startupLimitExceeded = true;
		}
	}

	public enum AdmitResult { Ok, NotInitialized, LimitExceeded, AlreadyExists, TargetMismatch,
		SessionNotFound, OperationTimedOut, OperationCanceled }

	/// <summary>
	/// Creates a session directory (CreateDirectory as a direct child, name == session_id).
	/// Admission first re-verifies the whole store (unknown or changed children fail closed),
	/// then checks the session quota, then creates — any failure leaves zero tree delta.
	/// </summary>
	public AdmitResult AdmitNewSession(string sessionId) {
		lock (gate) {
			if (!initialized)
				return AdmitResult.NotInitialized;
			var verify = VerifyForAdmission();
			if (verify != AdmitResult.Ok)
				return verify;
			if (fs.SessionDirectoryExists(sessionId) || ledgerSessions.ContainsKey(sessionId))
				return AdmitResult.AlreadyExists;
			if (ledgerSessions.Count + retainedSessions.Count + staleSessions.Count
				+ invalidStaleNames.Count + 1 > maxSessions)
				return AdmitResult.LimitExceeded;
			fs.CreateSessionDirectory(sessionId);
			ledgerSessions[sessionId] = new List<ChildRecord>();
			return AdmitResult.Ok;
		}
	}

	/// <summary>
	/// Admits one artifact write (a CreateNew child plus its manifest-sized reservation):
	/// re-verification, then the checked-add quotas (per file / per session / whole store), then
	/// the creation. Existing names are ALREADY_EXISTS; quota failures have zero side effects.
	/// </summary>
	public AdmitResult AdmitArtifactWrite(string sessionId, string relativeName, byte[] payload,
		ArtifactOperationRecord? operation = null) =>
		AdmitArtifactWriteCore(sessionId, relativeName, payload.LongLength, payload, operation);

	/// <summary>Deterministic quota seam; production callers must use the byte[] overload.</summary>
	public AdmitResult AdmitArtifactReservationForTest(string sessionId, string relativeName, long length) =>
		AdmitArtifactWriteCore(sessionId, relativeName, length, null, null);

	AdmitResult AdmitArtifactWriteCore(string sessionId, string relativeName, long length,
		byte[]? payload, ArtifactOperationRecord? operation) {
		lock (gate) {
			if (!initialized)
				return AdmitResult.NotInitialized;
			var verify = VerifyForAdmission();
			if (verify != AdmitResult.Ok)
				return verify;
			if (!ledgerSessions.TryGetValue(sessionId, out var children))
				return AdmitResult.SessionNotFound;
			if (children.Any(c => c.RelativeName == relativeName))
				return AdmitResult.AlreadyExists;
			if (fs.EnumerateSessionChildren(sessionId).Contains(relativeName))
				return AdmitResult.AlreadyExists;
			if (length < 0 || length > maxFile)
				return AdmitResult.LimitExceeded;
			if (WouldExceed(children.Sum(c => c.Length), length, maxSession))
				return AdmitResult.LimitExceeded;
			if (WouldExceed(AllTrackedChildren().Sum(c => c.Length), length, maxStore))
				return AdmitResult.LimitExceeded;
			if (children.Count + 1 > MaxSessionChildren)
				return AdmitResult.LimitExceeded;
			if (AllTrackedChildren().Count() + 1 > MaxStoreChildren)
				return AdmitResult.LimitExceeded;
			if (operation?.IsExpiredNow == true)
				return AdmitResult.OperationTimedOut;
			if (operation?.CancellationRequested == true)
				return AdmitResult.OperationCanceled;
			ChildRecord committed;
			try {
				committed = fs.CreateChildFile(sessionId, relativeName, length, payload, operation);
			}
			catch (ArtifactWriteInterruptedException interrupted) {
				children.Add(interrupted.Record);
				return interrupted.Result;
			}
			if (committed.RelativeName != relativeName || committed.Length != length)
				throw new InvalidOperationException("artifact filesystem returned a mismatched committed record");
			children.Add(committed);
			return AdmitResult.Ok;
		}
	}

	/// <summary>
	/// Settles a partial child after cancellation as an explicit ledger entry (aborted_owned):
	/// the finalizer observed the final identity/length/hash on the same handle; no manifest.
	/// </summary>
	public AdmitResult SettleAbortedOwned(string sessionId, ChildRecord record) {
		lock (gate) {
			if (!ledgerSessions.TryGetValue(sessionId, out var children))
				return AdmitResult.SessionNotFound;
			children.Add(record);
			return AdmitResult.Ok;
		}
	}

	public enum TerminalResult { Retained, StaleUntrusted, SessionNotFound }

	/// <summary>
	/// Terminal transfer: the closed child-name set and every child's identity and length must
	/// exactly equal the active-writer ledger — then the session atomically becomes
	/// known_retained (process lifetime). Any mismatch marks it stale_untrusted instead; both
	/// outcomes never delete anything.
	/// </summary>
	public TerminalResult TerminalSession(string sessionId) {
		lock (gate) {
			if (!ledgerSessions.TryGetValue(sessionId, out var children))
				return TerminalResult.SessionNotFound;
			bool exact = true;
			var observed = fs.EnumerateSessionChildren(sessionId);
			var ledgerNames = children.Select(c => c.RelativeName).ToList();
			if (observed.Count != ledgerNames.Count || !observed.All(ledgerNames.Contains))
				exact = false;
			if (exact) {
				foreach (var child in children) {
					var identity = fs.ObserveChild(sessionId, child.RelativeName);
					if (identity is null || identity.Value.VolumeSerial != child.VolumeSerial
						|| identity.Value.FileId != child.FileId || identity.Value.Length != child.Length) {
						exact = false;
						break;
					}
				}
			}
			ledgerSessions.Remove(sessionId);
			if (exact) {
				retainedSessions.Add(sessionId, children);
				return TerminalResult.Retained;
			}
			if (TrySnapshotStaleSession(sessionId, out var staleChildren, out var limitExceeded))
				staleSessions[sessionId] = staleChildren;
			else
				invalidStaleNames.Add(sessionId);
			startupLimitExceeded |= limitExceeded;
			return TerminalResult.StaleUntrusted;
		}
	}

	/// <summary>
	/// Fail-closed re-verification before any new tree delta: the root's child set must exactly
	/// equal ledger sessions + recorded stale names (a new external sibling is
	/// TARGET_MISMATCH), and every ledger session's children must match the ledger exactly.
	/// </summary>
	AdmitResult VerifyForAdmission() {
		var rootChildren = fs.EnumerateRootChildren();
		if (rootChildren.Count > MaxRootChildren)
			return AdmitResult.LimitExceeded;
		if (startupLimitExceeded)
			return AdmitResult.LimitExceeded;
		// Files, reparse points and objects that could not be leased/snapshotted never become
		// writable provenance roots and keep the store fail-closed.
		if (invalidStaleNames.Count != 0)
			return AdmitResult.TargetMismatch;
		// active, retained, and startup-stale sessions all stay in the exact re-verification
		// set for this process. Stale marker contents confer no ownership or write authority.
		var known = new HashSet<string>(ledgerSessions.Keys.Concat(retainedSessions.Keys)
			.Concat(staleSessions.Keys), StringComparer.Ordinal);
		if (rootChildren.Count != known.Count || !known.SetEquals(rootChildren))
			return AdmitResult.TargetMismatch;
		foreach (var pair in ledgerSessions.Concat(retainedSessions).Concat(staleSessions)) {
			var sessionId = pair.Key;
			var children = pair.Value;
			var observed = fs.EnumerateSessionChildren(sessionId);
			if (observed.Count != children.Count)
				return AdmitResult.TargetMismatch;
			if (!observed.All(name => children.Any(c => c.RelativeName == name)))
				return AdmitResult.TargetMismatch;
			foreach (var child in children) {
				var identity = fs.ObserveChild(sessionId, child.RelativeName);
				if (identity is null || identity.Value.VolumeSerial != child.VolumeSerial
					|| identity.Value.FileId != child.FileId || identity.Value.Length != child.Length)
					return AdmitResult.TargetMismatch;
			}
		}
		if (AllTrackedChildren().Count() > MaxStoreChildren
			|| SumWouldExceed(AllTrackedChildren(), maxStore))
			return AdmitResult.LimitExceeded;
		return AdmitResult.Ok;
	}

	bool TrySnapshotStaleSession(string sessionId, out List<ChildRecord> children,
		out bool limitExceeded) {
		children = new List<ChildRecord>();
		limitExceeded = false;
		if (!fs.SessionDirectoryExists(sessionId) || !fs.TryLeaseExistingSessionDirectory(sessionId))
			return false;
		var names = fs.EnumerateSessionChildren(sessionId);
		if (names.Count > MaxSessionChildren)
			limitExceeded = true;
		long sessionBytes = 0;
		foreach (var name in names) {
			var identity = fs.ObserveChild(sessionId, name);
			if (identity is null || identity.Value.Length < 0)
				return false;
			if (identity.Value.Length > maxFile
				|| WouldExceed(sessionBytes, identity.Value.Length, maxSession))
				limitExceeded = true;
			if (sessionBytes <= maxSession && identity.Value.Length <= maxSession - sessionBytes)
				sessionBytes += identity.Value.Length;
			else
				sessionBytes = maxSession;
			children.Add(new ChildRecord(name, identity.Value.VolumeSerial,
				identity.Value.FileId, identity.Value.Length, string.Empty, "stale_untrusted"));
		}
		return true;
	}

	IEnumerable<ChildRecord> AllLedgerChildren() =>
		ledgerSessions.Values.Concat(retainedSessions.Values).SelectMany(v => v);
	IEnumerable<ChildRecord> StaleChildren() => staleSessions.Values.SelectMany(v => v);
	IEnumerable<ChildRecord> AllTrackedChildren() => AllLedgerChildren().Concat(StaleChildren());
	static bool SumWouldExceed(IEnumerable<ChildRecord> children, long limit) {
		long total = 0;
		foreach (var child in children) {
			if (WouldExceed(total, child.Length, limit))
				return true;
			total += child.Length;
		}
		return false;
	}

	static bool WouldExceed(long current, long add, long limit) =>
		current < 0 || add < 0 || current > limit || add > limit - current;
}

/// <summary>
/// ArtifactOperationRecord phase machine (CON-DYN-008 global rules): scheduled → active →
/// canceling → settled, settled exactly once. The 30-second monotonic deadline starts at cache
/// admission and lane-slot reservation; cancellation grace is handled by the caller — this
/// record only fixes the phase transitions and single settlement.
/// </summary>
public sealed class ArtifactOperationRecord {
	public enum Phase { Scheduled, Active, Canceling, Settled }

	public string RequestId { get; }
	public string SessionId { get; }
	public long DeadlineTimestamp { get; }
	int phase;
	int cancellationRequested;
	readonly System.Threading.CancellationTokenSource cancellation = new();
	readonly long deadline;

	public ArtifactOperationRecord(string requestId, string sessionId, TimeSpan? deadlineOverride = null,
		long? admissionTimestamp = null) {
		RequestId = requestId;
		SessionId = sessionId;
		var duration = deadlineOverride ?? TimeSpan.FromSeconds(30);
		deadline = (admissionTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp())
			+ (long)(duration.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
		DeadlineTimestamp = deadline;
		phase = (int)Phase.Scheduled;
	}

	public Phase CurrentPhase => (Phase)System.Threading.Volatile.Read(ref phase);
	public bool IsExpired(long now) => now >= deadline;
	public bool IsExpiredNow => IsExpired(System.Diagnostics.Stopwatch.GetTimestamp());
	public bool CancellationRequested => System.Threading.Volatile.Read(ref cancellationRequested) != 0;
	public System.Threading.CancellationToken CancellationToken => cancellation.Token;
	public bool RequestCancellation() {
		if (System.Threading.Interlocked.Exchange(ref cancellationRequested, 1) != 0)
			return false;
		cancellation.Cancel();
		return true;
	}

	public bool TryMarkActive() => Cas(Phase.Scheduled, Phase.Active);
	/// <summary>Post-CreateNew timeout/cancellation: no further writes, manifest or commit.</summary>
	public bool TryMarkCanceling() => CurrentPhase == Phase.Active && Cas(Phase.Active, Phase.Canceling);
	/// <summary>Exactly-once settlement (success or aborted_owned).</summary>
	public bool TrySettle() {
		while (true) {
			var observed = (Phase)System.Threading.Volatile.Read(ref phase);
			if (observed == Phase.Settled)
				return false;
			if (System.Threading.Interlocked.CompareExchange(ref phase, (int)Phase.Settled, (int)observed) == (int)observed)
				return true;
		}
	}

	bool Cas(Phase from, Phase to) =>
		System.Threading.Interlocked.CompareExchange(ref phase, (int)to, (int)from) == (int)from;
}
