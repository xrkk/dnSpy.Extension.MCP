using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Unified side-effect request cache (CON-DYN-013). Key is the UUID request_id; the stored
/// identity is the method plus the RFC 8785 canonical form of the arguments with request_id
/// excluded. Dual capacity: at most 4096 entries and 268435456 content bytes (method +
/// canonical arguments + final canonical envelope, strict UTF-8). A new id reserves
/// method + arguments + 65536 bytes and is settled to the exact byte sum on completion; nothing
/// unexpired is ever evicted, and either shortage rejects before any queue/reservation/side
/// effect. The cacheable envelope itself is capped at 65536 bytes: admission runs the handler's
/// size-safe envelope template first, and a predicted overflow returns and caches a small
/// LIMIT_EXCEEDED envelope instead of executing. Retention is 10 minutes from whichever is
/// later: request completion or session termination.
/// </summary>
public sealed class SideEffectRequestCache {
	public const int MaxEntries = 4096;
	public const int MaxContentBytes = 268435456;
	public const int MaxEnvelopeBytes = 65536;
	public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

	public enum AdmitStatus { Admitted, JoinedInFlight, HitSettled, RequestIdReuse, LimitExceeded }

	public readonly struct AdmitResult {
		public AdmitStatus Status { get; init; }
		/// <summary>Settled envelope for HitSettled (and for the cached LIMIT_EXCEEDED path).</summary>
		public string? SettledEnvelope { get; init; }
	}

	sealed class Entry {
		public string RequestId = string.Empty;
		public string Method = string.Empty;
		public string CanonicalArgs = string.Empty;
		public string? Envelope;           // settled envelope (null while in flight)
		public int ReservedBytes;          // method + args + 65536 until settled
		public DateTime? CompletedAtUtc;
		public DateTime SessionTerminalUtc; // max(completed, terminal) drives expiry
	}

	readonly object gate = new();
	readonly Dictionary<string, Entry> entries = new();
	readonly Func<DateTime> wallClock;

	public SideEffectRequestCache(Func<DateTime>? wallClock = null) {
		this.wallClock = wallClock ?? (() => DateTime.UtcNow);
	}

	public int EntryCount { get { lock (gate) return entries.Count; } }
	public long ReservedBytes { get { lock (gate) return entries.Values.Sum(e => e.ReservedBytes); } }

	static int Utf8Len(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

	/// <summary>
	/// Canonicalizes arguments with the request_id key removed (RFC 8785 over the supported
	/// domain: strings, integers, booleans, null, arrays, objects).
	/// </summary>
	public static string CanonicalizeArguments(Dictionary<string, object?>? arguments) {
		if (arguments is null)
			return "{}";
		var filtered = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
		filtered.Remove("request_id");
		return Jcs.Serialize(filtered);
	}

	/// <summary>
	/// Admission: template is the handler's size-safe envelope template. A template over
	/// MaxEnvelopeBytes returns (and caches) a small LIMIT_EXCEEDED envelope. A repeated id with
	/// identical identity joins the in-flight request or returns the settled envelope; a
	/// mismatched identity is REQUEST_ID_REUSE. Capacity shortage is LIMIT_EXCEEDED.
	/// </summary>
	public AdmitResult TryAdmit(string requestId, string method, string canonicalArgs, string envelopeTemplate,
		Func<string> smallLimitExceededEnvelope) {
		lock (gate) {
			ExpireLocked();
			if (entries.TryGetValue(requestId, out var existing)) {
				if (existing.Method != method || existing.CanonicalArgs != canonicalArgs)
					return new AdmitResult { Status = AdmitStatus.RequestIdReuse };
				return existing.Envelope is { } env
					? new AdmitResult { Status = AdmitStatus.HitSettled, SettledEnvelope = env }
					: new AdmitResult { Status = AdmitStatus.JoinedInFlight };
			}
			int templateBytes = Utf8Len(envelopeTemplate);
			if (templateBytes > MaxEnvelopeBytes) {
				var small = smallLimitExceededEnvelope();
				var smallEntry = new Entry {
					RequestId = requestId, Method = method, CanonicalArgs = canonicalArgs,
					Envelope = small, ReservedBytes = Utf8Len(method) + Utf8Len(canonicalArgs) + Utf8Len(small),
					CompletedAtUtc = wallClock(),
				};
				smallEntry.SessionTerminalUtc = smallEntry.CompletedAtUtc.Value;
				if (!TryReserveCapacity(smallEntry.ReservedBytes))
					return new AdmitResult { Status = AdmitStatus.LimitExceeded };
				entries[requestId] = smallEntry;
				return new AdmitResult { Status = AdmitStatus.HitSettled, SettledEnvelope = small };
			}
			var reservation = Utf8Len(method) + Utf8Len(canonicalArgs) + MaxEnvelopeBytes;
			if (entries.Count >= MaxEntries || !TryReserveCapacity(reservation))
				return new AdmitResult { Status = AdmitStatus.LimitExceeded };
			entries[requestId] = new Entry {
				RequestId = requestId, Method = method, CanonicalArgs = canonicalArgs,
				ReservedBytes = reservation,
			};
			return new AdmitResult { Status = AdmitStatus.Admitted };
		}
	}

	bool TryReserveCapacity(int bytes) => ReservedBytesLocked() + bytes <= MaxContentBytes;

	long ReservedBytesLocked() => entries.Values.Sum(e => e.ReservedBytes);

	/// <summary>Settles an admitted in-flight request with its final canonical envelope (exact byte accounting).</summary>
	public bool Settle(string requestId, string finalEnvelope) {
		lock (gate) {
			if (!entries.TryGetValue(requestId, out var entry) || entry.Envelope != null)
				return false;
			entry.Envelope = finalEnvelope;
			entry.ReservedBytes = Utf8Len(entry.Method) + Utf8Len(entry.CanonicalArgs) + Utf8Len(finalEnvelope);
			entry.CompletedAtUtc = wallClock();
			entry.SessionTerminalUtc = entry.CompletedAtUtc.Value;
			return true;
		}
	}

	/// <summary>Cached envelope lookup for an identical (request_id, method, canonicalArgs) identity.</summary>
	public string? LookupSettled(string requestId, string method, string canonicalArgs) {
		lock (gate) {
			ExpireLocked();
			return entries.TryGetValue(requestId, out var e) && e.Method == method
				&& e.CanonicalArgs == canonicalArgs ? e.Envelope : null;
		}
	}

	/// <summary>Session terminal: keeps entries but never lets session-terminal extend a completed entry's clock.</summary>
	public void MarkSessionTerminal(DateTime terminalUtc) {
		lock (gate)
			foreach (var e in entries.Values)
				if (e.CompletedAtUtc is { } done && terminalUtc > e.SessionTerminalUtc)
					e.SessionTerminalUtc = terminalUtc;
	}

	void ExpireLocked() {
		var now = wallClock();
		// Only settled entries age out; an in-flight request's retention clock starts at
		// completion (or the later session terminal), never at admission.
		var expired = entries
			.Where(kv => kv.Value.CompletedAtUtc is not null && now - kv.Value.SessionTerminalUtc >= Retention)
			.Select(kv => kv.Key).ToList();
		foreach (var key in expired)
			entries.Remove(key);
	}
}
