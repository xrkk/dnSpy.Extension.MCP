using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Server-generated opaque handles bound to session / generation / pause-epoch (REQ-DYN-006).
/// A handle resolves only while its whole binding chain matches; resume (pause-epoch), restart
/// (generation) and session terminal invalidate whole ranges. Invalidation closes each still
/// live handle exactly once through the registered close action, and a closed handle is dead
/// forever — revalidated lookups return stale.
/// </summary>
public sealed class DebugHandleStore {
	public sealed class Entry {
		public string Handle { get; }
		public string SessionId { get; }
		public int Generation { get; }
		/// <summary>Null for generation/session-bound handles (process, runtime, module).</summary>
		public int? PauseEpoch { get; }
		/// <summary>Optional payload (e.g. the dispatcher-side resolution context).</summary>
		public object? Payload { get; }
		public Entry(string handle, string sessionId, int generation, int? pauseEpoch, object? payload) {
			Handle = handle; SessionId = sessionId; Generation = generation; PauseEpoch = pauseEpoch; Payload = payload;
		}
	}

	readonly ConcurrentDictionary<string, Entry> live = new();
	readonly Func<string> newHandle;
	readonly Action<Entry>? onClose;

	public DebugHandleStore(Func<string>? newHandle = null, Action<Entry>? onClose = null) {
		this.newHandle = newHandle ?? DefaultNewHandle;
		this.onClose = onClose;
	}

	static string DefaultNewHandle() {
		var bytes = System.Security.Cryptography.RandomNumberGeneratorShim.GetBytes(15);
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	public int LiveCount => live.Count;

	/// <summary>Allocates a new handle bound to the given chain; handles are unique per store.</summary>
	public string Allocate(string sessionId, int generation, int? pauseEpoch = null, object? payload = null) {
		while (true) {
			var handle = newHandle();
			var entry = new Entry(handle, sessionId, generation, pauseEpoch, payload);
			if (live.TryAdd(handle, entry))
				return handle;
		}
	}

	/// <summary>
	/// Resolves a handle under the caller's current binding. A mismatch anywhere in the chain
	/// (including a handle invalidated by epoch/generation/session end) is stale — it never
	/// resolves again, even if the numbers coincidentally realign later.
	/// </summary>
	public bool TryResolve(string handle, string sessionId, int generation, int? pauseEpoch, out Entry? entry) {
		entry = null;
		if (!live.TryGetValue(handle, out var stored))
			return false;
		if (stored.SessionId != sessionId || stored.Generation != generation)
			return false;
		if (stored.PauseEpoch.HasValue != pauseEpoch.HasValue)
			return false;
		if (stored.PauseEpoch.HasValue && stored.PauseEpoch.Value != pauseEpoch!.Value)
			return false;
		entry = stored;
		return true;
	}

	/// <summary>Session terminal: closes every live handle of the session exactly once.</summary>
	public int InvalidateSession(string sessionId) {
		var victims = live.Values.Where(e => e.SessionId == sessionId).ToList();
		foreach (var v in victims)
			Close(v);
		return victims.Count;
	}

	/// <summary>Restart moved to a new generation: closes all handles of the old generation.</summary>
	public int InvalidateGeneration(string sessionId, int generation) {
		var victims = live.Values.Where(e => e.SessionId == sessionId && e.Generation == generation).ToList();
		foreach (var v in victims)
			Close(v);
		return victims.Count;
	}

	/// <summary>Continue/step left the pause epoch: closes that epoch's pause-bound handles.</summary>
	public int InvalidatePauseEpoch(string sessionId, int generation, int pauseEpoch) {
		var victims = live.Values.Where(e => e.SessionId == sessionId && e.Generation == generation
			&& e.PauseEpoch == pauseEpoch).ToList();
		foreach (var v in victims)
			Close(v);
		return victims.Count;
	}

	void Close(Entry entry) {
		if (!live.TryRemove(entry.Handle, out _))
			return; // already closed exactly once by a racing invalidation
		onClose?.Invoke(entry);
	}
}
