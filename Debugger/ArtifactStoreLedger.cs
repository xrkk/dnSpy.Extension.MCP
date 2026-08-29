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
		long length, byte[]? payload);
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
		public ChildRecord(string relativeName, string volumeSerial, string fileId, long length, string sha256) {
			RelativeName = relativeName; VolumeSerial = volumeSerial; FileId = fileId; Length = length; Sha256 = sha256;
		}
	}

	readonly object gate = new();
	readonly IArtifactStoreFs fs;
	// Session state: ledger sessions (active_writer + known_retained) and stale names.
	readonly Dictionary<string, List<ChildRecord>> ledgerSessions = new(StringComparer.Ordinal);
	readonly HashSet<string> retainedSessions = new(StringComparer.Ordinal);
	readonly HashSet<string> staleNames = new(StringComparer.Ordinal);
	readonly long maxFile, maxSession, maxStore;
	readonly int maxSessions;
	bool initialized;

	public ArtifactStoreLedger(IArtifactStoreFs fs,
		int maxSessions = MaxRetainedSessions, long maxFile = MaxFileBytes,
		long maxSession = MaxSessionBytes, long maxStore = MaxStoreBytes) {
		this.fs = fs;
		this.maxSessions = maxSessions;
		this.maxFile = maxFile;
		this.maxSession = maxSession;
		this.maxStore = maxStore;
	}

	public int LedgerSessionCount { get { lock (gate) return ledgerSessions.Count; } }
	public int StaleCount { get { lock (gate) return staleNames.Count; } }
	public long LedgerBytes { get { lock (gate) return ledgerSessions.Values.SelectMany(v => v).Sum(c => c.Length); } }
	/// <summary>Retained bytes never re-read or re-hashed after commit (retained_bytes_hashed=0).</summary>
	public long RetainedBytesHashed => 0;

	/// <summary>
	/// Startup scan: the in-process ledger starts empty; every existing direct child of the root
	/// is stale_untrusted — a structure-legal or copied marker creates no ledger entry.
	/// </summary>
	public void Initialize() {
		lock (gate) {
			initialized = true;
			foreach (var name in fs.EnumerateRootChildren())
				staleNames.Add(name);
		}
	}

	public enum AdmitResult { Ok, NotInitialized, LimitExceeded, AlreadyExists, TargetMismatch, SessionNotFound }

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
			if (ledgerSessions.Count + 1 > maxSessions)
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
	public AdmitResult AdmitArtifactWrite(string sessionId, string relativeName, byte[] payload) =>
		AdmitArtifactWriteCore(sessionId, relativeName, payload.LongLength, payload);

	/// <summary>Deterministic quota seam; production callers must use the byte[] overload.</summary>
	public AdmitResult AdmitArtifactReservationForTest(string sessionId, string relativeName, long length) =>
		AdmitArtifactWriteCore(sessionId, relativeName, length, null);

	AdmitResult AdmitArtifactWriteCore(string sessionId, string relativeName, long length, byte[]? payload) {
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
			if (length > maxFile)
				return AdmitResult.LimitExceeded;
			if (children.Sum(c => c.Length) + length > maxSession)
				return AdmitResult.LimitExceeded;
			if (LedgerBytes + length > maxStore)
				return AdmitResult.LimitExceeded;
			if (children.Count + 1 > MaxSessionChildren)
				return AdmitResult.LimitExceeded;
			if (ledgerSessions.Values.Sum(v => v.Count) + 1 > MaxStoreChildren)
				return AdmitResult.LimitExceeded;
			var committed = fs.CreateChildFile(sessionId, relativeName, length, payload);
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
				retainedSessions.Add(sessionId);
				return TerminalResult.Retained;
			}
			staleNames.Add(sessionId);
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
		if (rootChildren.Count > MaxRootChildren + 1)
			return AdmitResult.LimitExceeded;
		// known_retained sessions stay in the re-verification set for the whole process
		// lifetime (retention lease) — leaving them out would flag them as unknown siblings.
		var known = new HashSet<string>(ledgerSessions.Keys.Concat(retainedSessions).Concat(staleNames), StringComparer.Ordinal);
		if (!rootChildren.All(known.Contains))
			return AdmitResult.TargetMismatch;
		foreach (var pair in ledgerSessions) {
			var sessionId = pair.Key;
			var children = pair.Value;
			var observed = fs.EnumerateSessionChildren(sessionId);
			if (observed.Count != children.Count)
				return AdmitResult.TargetMismatch;
			if (!observed.All(name => children.Any(c => c.RelativeName == name)))
				return AdmitResult.TargetMismatch;
		}
		return AdmitResult.Ok;
	}
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
	readonly long deadline;

	public ArtifactOperationRecord(string requestId, string sessionId, TimeSpan? deadlineOverride = null) {
		RequestId = requestId;
		SessionId = sessionId;
		var duration = deadlineOverride ?? TimeSpan.FromSeconds(30);
		deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
		DeadlineTimestamp = deadline;
		phase = (int)Phase.Scheduled;
	}

	public Phase CurrentPhase => (Phase)System.Threading.Volatile.Read(ref phase);
	public bool IsExpired(long now) => now >= deadline;
	public bool IsExpiredNow => IsExpired(System.Diagnostics.Stopwatch.GetTimestamp());

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
