using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditRequestCache {
	sealed class Entry { public string PayloadHash = string.Empty; public string ResponseJson = string.Empty; public int Bytes; }
	readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	readonly int maxEntries;
	readonly int maxBytes;
	int bytes;

	public EditRequestCache(int maxEntries, int maxBytes) { this.maxEntries = maxEntries; this.maxBytes = maxBytes; }
	public int Count => entries.Count;
	public int Bytes => bytes;
	public void EnsureCanAdd(string responseJson) {
		var size = System.Text.Encoding.UTF8.GetByteCount(responseJson);
		if (entries.Count >= maxEntries || bytes + size > maxBytes) Capacity(entries.Count >= maxEntries ? "request_cache_entries" : "request_cache_bytes",
			entries.Count >= maxEntries ? entries.Count : bytes + size, entries.Count >= maxEntries ? maxEntries : maxBytes);
	}

	public bool TryReplay(string requestId, string payloadHash, out string responseJson) {
		if (!entries.TryGetValue(requestId, out var entry)) { responseJson = string.Empty; return false; }
		if (!string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal)) throw new EditDomainException("REQUEST_ID_REUSE", new Dictionary<string, object?> {
			["kind"] = "request_reuse", ["request_id"] = requestId,
		});
		responseJson = entry.ResponseJson; return true;
	}

	public void Add(string requestId, string payloadHash, string responseJson) {
		var size = System.Text.Encoding.UTF8.GetByteCount(responseJson);
		EnsureCanAdd(responseJson);
		entries[requestId] = new Entry { PayloadHash = payloadHash, ResponseJson = responseJson, Bytes = size }; bytes += size;
	}

	public void Clear() { entries.Clear(); bytes = 0; }
	public void RemovePrefix(string requestIdPrefix) {
		foreach (var key in entries.Keys.Where(key => key.StartsWith(requestIdPrefix, StringComparison.Ordinal)).ToArray()) {
			bytes -= entries[key].Bytes;
			entries.Remove(key);
		}
	}
	static void Capacity(string resource, long current, long maximum) => throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> {
		["kind"] = "capacity", ["limit"] = resource, ["current"] = current, ["maximum"] = maximum,
	});
}

/// <summary>One bounded idempotent terminal response per authoritative transport session.</summary>
internal sealed class EditTerminalCache {
	sealed class Entry { public string RequestId = string.Empty; public string PayloadHash = string.Empty; public string ResponseJson = string.Empty; }
	readonly Dictionary<string, Entry> sessions = new(StringComparer.Ordinal);
	public bool TryReplay(string sessionId, string requestId, string payloadHash, out string responseJson) {
		if (!sessions.TryGetValue(sessionId, out var entry) || !string.Equals(entry.RequestId, requestId, StringComparison.Ordinal)) {
			responseJson = string.Empty; return false;
		}
		if (!string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal))
			throw new EditDomainException("REQUEST_ID_REUSE", new Dictionary<string, object?> {
				["kind"] = "request_reuse", ["request_id"] = requestId,
			});
		responseJson = entry.ResponseJson; return true;
	}
	public void Store(string sessionId, string requestId, string payloadHash, string responseJson) {
		var bytes = System.Text.Encoding.UTF8.GetByteCount(responseJson);
		if (bytes > EditWire.RollbackSlotBytes) throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> {
			["kind"] = "capacity", ["limit"] = "rollback_terminal_slot", ["current"] = bytes, ["maximum"] = EditWire.RollbackSlotBytes,
		});
		sessions[sessionId] = new Entry { RequestId = requestId, PayloadHash = payloadHash, ResponseJson = responseJson };
	}
	public void RemoveSession(string sessionId) => sessions.Remove(sessionId);
}

internal sealed class EditReviewCache {
	sealed class Entry {
		public string RequestId = string.Empty;
		public string PayloadHash = string.Empty;
		public string ReviewId = string.Empty;
		public uint Revision;
		public string? ResponseJson;
	}
	readonly List<Entry> tombstones = new();
	Entry? current;
	int tombstoneBytes;

	public int TombstoneCount => tombstones.Count;
	public int TombstoneBytes => tombstoneBytes;
	public bool HasCurrent => current != null;
	public string? CurrentReviewId => current?.ReviewId;
	public uint? CurrentRevision => current?.Revision;

	public bool TryReplay(string requestId, string payloadHash, out string responseJson, out string? staleReviewId) {
		if (current != null && string.Equals(current.RequestId, requestId, StringComparison.Ordinal)) {
			EnsurePayload(current, requestId, payloadHash);
			responseJson = current.ResponseJson!;
			staleReviewId = null;
			return true;
		}
		var old = tombstones.FirstOrDefault(x => string.Equals(x.RequestId, requestId, StringComparison.Ordinal));
		if (old != null) {
			EnsurePayload(old, requestId, payloadHash);
			if (current == null || current.ResponseJson == null) {
				responseJson = string.Empty;
				staleReviewId = old.ReviewId;
				return true;
			}
			// Review responses for a fixed revision are deterministic except for the
			// fixed-width review id. Reconstruct the old response without retaining a
			// second multi-megabyte response in the compact tombstone.
			responseJson = current.ResponseJson.Replace(current.ReviewId, old.ReviewId);
			staleReviewId = null;
			return true;
		}
		responseJson = string.Empty;
		staleReviewId = null;
		return false;
	}

	public void EnsureCanReplace() {
		if (current == null) return;
		var size = TombstoneSize(current);
		if (tombstones.Count >= EditWire.ReviewTombstoneEntries || tombstoneBytes + size > EditWire.ReviewTombstoneBytes)
			throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> {
				["kind"] = "capacity", ["limit"] = "review_tombstones",
				["current"] = tombstones.Count, ["maximum"] = EditWire.ReviewTombstoneEntries,
			});
	}

	public void Store(string requestId, string payloadHash, string reviewId, uint revision, string responseJson) {
		var responseBytes = System.Text.Encoding.UTF8.GetByteCount(responseJson);
		if (responseBytes > EditWire.ReviewSlotBytes)
			throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> {
				["kind"] = "capacity", ["limit"] = "review_slot", ["current"] = responseBytes,
				["maximum"] = EditWire.ReviewSlotBytes,
			});
		EnsureCanReplace();
		if (current != null) {
			tombstones.Add(new Entry {
				RequestId = current.RequestId,
				PayloadHash = current.PayloadHash,
				ReviewId = current.ReviewId,
				Revision = current.Revision,
			});
			tombstoneBytes += TombstoneSize(current);
		}
		current = new Entry { RequestId = requestId, PayloadHash = payloadHash, ReviewId = reviewId, Revision = revision, ResponseJson = responseJson };
	}
	public void EnsureResponseFits(string responseJson) {
		var bytes = System.Text.Encoding.UTF8.GetByteCount(responseJson);
		if (bytes > EditWire.ReviewSlotBytes)
			throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> {
				["kind"] = "capacity", ["limit"] = "review_slot", ["current"] = bytes,
				["maximum"] = EditWire.ReviewSlotBytes,
			});
	}

	public void Clear() { current = null; tombstones.Clear(); tombstoneBytes = 0; }

	static int TombstoneSize(Entry entry) =>
		System.Text.Encoding.UTF8.GetByteCount(entry.RequestId) + 64 + 64 +
		System.Text.Encoding.UTF8.GetByteCount(entry.ReviewId) + 32;

	static void EnsurePayload(Entry entry, string requestId, string payloadHash) {
		if (string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal)) return;
		throw new EditDomainException("REQUEST_ID_REUSE", new Dictionary<string, object?> {
			["kind"] = "request_reuse", ["request_id"] = requestId,
		});
	}
}
