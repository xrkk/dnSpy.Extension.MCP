using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Per-session bounded monotonic debug-event log (REQ-DYN-005 / CON-DYN-009). Cursors start at 1
/// and are monotonic within the session; eviction is triggered by EITHER the 4096-entry cap or
/// the 8 MiB content cap, with cumulative events_lost accounting. A single event whose envelope
/// alone exceeds the byte cap is rewritten as an EVT-DYN-019 payload_omitted entry. Writing
/// EVT-DYN-020 (session_end) freezes the log — it is the session's only terminal event.
/// </summary>
public sealed class DebugEventBuffer {
	public const int DefaultMaxEntries = 4096;
	public const int DefaultMaxBytes = 8388608;

	sealed class Entry {
		public long Cursor;
		public string Kind = string.Empty;
		public string Json = string.Empty;
		public int Bytes;
	}

	readonly object gate = new object();
	readonly Queue<Entry> entries = new();
	long totalBytes;
	long eventsLost;
	long nextCursor;
	bool frozen;

	public string SessionId { get; }
	public int MaxEntries { get; }
	public int MaxBytes { get; }
	/// <summary>Clock for EVT-DYN-019 rewrites (UTC RFC3339 with fixed milliseconds); injectable for tests.</summary>
	readonly Func<string> utcNow;

	public DebugEventBuffer(string sessionId, int maxEntries = DefaultMaxEntries, int maxBytes = DefaultMaxBytes, Func<string>? utcNow = null) {
		SessionId = sessionId;
		MaxEntries = maxEntries;
		MaxBytes = maxBytes;
		this.utcNow = utcNow ?? DefaultUtcNow;
	}

	static string DefaultUtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

	/// <summary>Total evicted events since session start (monotonic; reported as events_lost).</summary>
	public long EventsLost { get { lock (gate) return eventsLost; } }

	/// <summary>Cursor of the newest entry, or 0 when empty.</summary>
	public long LastCursor { get { lock (gate) return nextCursor; } }

	/// <summary>True once session_end has been written; appends are refused afterwards.</summary>
	public bool Frozen { get { lock (gate) return frozen; } }

	/// <summary>
	/// Appends one canonical event-envelope JSON. Returns false when the log is frozen. An event
	/// whose envelope alone exceeds the byte cap is replaced by an EVT-DYN-019 payload_omitted
	/// entry carrying the original kind, byte count and SHA-256.
	/// </summary>
	public bool Append(string kind, string canonicalJson) => Append(kind, canonicalJson, 0);

	/// <summary>
	/// Appends with an explicit cursor. The coordinator owns ONE monotonic cursor line for the
	/// whole process (envelope cursor == entry cursor); a zero cursor falls back to the local
	/// line. Splitting the two made after_cursor reads silently empty in any second session.
	/// </summary>
	public bool Append(string kind, string canonicalJson, long explicitCursor) {
		lock (gate) {
			if (frozen)
				return false;
			var bytes = Encoding.UTF8.GetByteCount(canonicalJson);
			var cursor = explicitCursor > 0 ? explicitCursor : ++nextCursor;
			nextCursor = Math.Max(nextCursor, cursor);
			if (bytes > MaxBytes) {
				canonicalJson = BuildPayloadOmitted(kind, canonicalJson, bytes, cursor);
				kind = EventKinds.PayloadOmitted;
				bytes = Encoding.UTF8.GetByteCount(canonicalJson);
			}
			// An entry must always fit the log; the omitted-entry fallback is tiny by design.
			if (bytes > MaxBytes)
				return false;
			var entry = new Entry { Cursor = cursor, Kind = kind, Json = canonicalJson, Bytes = bytes };
			entries.Enqueue(entry);
			totalBytes += bytes;
			if (kind == EventKinds.SessionEnd)
				frozen = true;
			EvictWhileOverBudget();
			return true;
		}
	}

	void EvictWhileOverBudget() {
		while (entries.Count > 0 && (entries.Count > MaxEntries || totalBytes > MaxBytes)) {
			var evicted = entries.Dequeue();
			totalBytes -= evicted.Bytes;
			eventsLost++;
		}
	}

	string BuildPayloadOmitted(string originalKind, string originalJson, int originalBytes, long cursor) {
		string? contextJson = null;
		try {
			using var doc = JsonDocument.Parse(originalJson);
			if (doc.RootElement.TryGetProperty("debug_context", out var ctx))
				contextJson = ctx.GetRawText();
		}
		catch (JsonException) { /* keep null context */ }
		string sha;
		using (var sha256 = SHA256.Create())
			sha = System.ConvertHexShim.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(originalJson))).ToLowerInvariant();
		var envelope = new {
			schema_version = DebugWire.SchemaVersion,
			cursor,
			timestamp_utc = utcNow(),
			kind = EventKinds.PayloadOmitted,
			// event_envelope requires debug_context; an unparseable original still gets the
			// no-session idle context rather than a schema-invalid rewrite.
			debug_context = contextJson is null ? new DebugContextDto() : JsonSerializer.Deserialize<DebugContextDto>(contextJson),
			payload = new {
				original_kind = originalKind,
				original_utf8_bytes = originalBytes,
				sha256 = sha,
				payload_omitted = true,
			},
			untrusted_sample_data = false,
		};
		return JsonSerializer.Serialize(envelope, new JsonSerializerOptions {
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		});
	}

	public sealed class ReadResult {
		public List<string> Events { get; } = new();
		public long NextCursor { get; set; }
		public long EarliestCursor { get; set; }
		public long EventsLost { get; set; }
	}

	/// <summary>
	/// Reads at most <paramref name="limit"/> events with cursor greater than
	/// <paramref name="afterCursor"/>, optionally filtered by kind. earliest_cursor is the
	/// oldest retained cursor (0 when empty); next_cursor is the newest returned cursor position,
	/// or the caller's still-valid position when no event matched.
	/// </summary>
	public ReadResult Read(long afterCursor, int limit, IReadOnlyCollection<string>? kinds) {
		lock (gate) {
			var result = new ReadResult {
				// Never make an empty page send a caller backwards to zero.  Clamp a cursor
				// beyond the current tail so an untrusted future value cannot skip later events.
				NextCursor = Math.Min(Math.Max(0, afterCursor), nextCursor),
				EarliestCursor = entries.Count == 0 ? 0 : entries.Peek().Cursor,
				EventsLost = eventsLost,
			};
			foreach (var e in entries) {
				if (result.Events.Count >= limit)
					break;
				if (e.Cursor <= afterCursor)
					continue;
				if (kinds != null && kinds.Count > 0 && !kinds.Contains(e.Kind))
					continue;
				result.Events.Add(e.Json);
				result.NextCursor = e.Cursor;
			}
			return result;
		}
	}
}
