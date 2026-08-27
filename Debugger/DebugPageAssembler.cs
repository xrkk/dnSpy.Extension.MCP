using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// TYPE-DYN-002/003 pagination assembly (IMP-007): one immutable page state per
/// (tool, session, generation, pause-epoch?, original page size, filter key) binding, with a
/// server-generated opaque continuation cursor. Continuation rules: an explicit page_size must
/// equal the original (mismatch is STALE_HANDLE), omission reuses the original; a cursor from
/// another binding or from superseded data is STALE_HANDLE — never a silent restart at page 1.
/// truncated is reserved for budget truncation: having further pages alone never sets it.
/// </summary>
public sealed class DebugPageAssembler {
	readonly object gate = new();
	readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
	long stateSeq;

	sealed class State {
		public long Id;
		public string Tool = string.Empty;
		public string SessionId = string.Empty;
		public int Generation;
		public int? PauseEpoch;
		public int PageSize;
		public string FilterKey = string.Empty;
		public IReadOnlyList<object> Items = Array.Empty<object>();
		public int TotalKnown;
		public bool Truncated;
	}

	/// <summary>Cursor codec seam; default is unpadded base64url of "stateIndex-offset".</summary>
	readonly Func<long, int, string> encodeCursor;
	readonly Func<string, (long stateId, int offset)?> decodeCursor;

	public DebugPageAssembler(Func<long, int, string>? encodeCursor = null, Func<string, (long, int)?>? decodeCursor = null) {
		this.encodeCursor = encodeCursor ?? DefaultEncode;
		this.decodeCursor = decodeCursor ?? DefaultDecode;
		static string DefaultEncode(long stateId, int offset) =>
			Convert.ToBase64String(Encoding.UTF8.GetBytes($"{stateId}:{offset}"))
				.TrimEnd('=').Replace('+', '-').Replace('/', '_');
		static (long, int)? DefaultDecode(string cursor) {
			try {
				var padded = cursor.Replace('-', '+').Replace('_', '/');
				switch (padded.Length % 4) {
					case 2: padded += "=="; break;
					case 3: padded += "="; break;
				}
				var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
				var parts = text.Split(':');
				if (parts.Length != 2 || !long.TryParse(parts[0], out var id) || !int.TryParse(parts[1], out var off))
					return null;
				return (id, off);
			}
			catch {
				return null;
			}
		}
	}

	public sealed class RequestContext {
		public string Tool { get; init; } = string.Empty;
		public string SessionId { get; init; } = string.Empty;
		public int Generation { get; init; }
		public int? PauseEpoch { get; init; }
		/// <summary>Canonical representation of the filter/construction parameters.</summary>
		public string FilterKey { get; init; } = string.Empty;
	}

	public sealed class PageResult {
		public IReadOnlyList<object> Items { get; internal set; } = Array.Empty<object>();
		public string? NextPageCursor { get; internal set; }
		public bool Truncated { get; internal set; }
		public int? TotalKnown { get; internal set; }
	}

	public enum ReadStatus { Ok, StaleHandle }

	/// <summary>
	/// First page: materializes the immutable page state (caller supplies the full ordered
	/// source list) and returns the first slice plus the continuation cursor when more remains.
	/// pageSize comes in already validated to 1..100 by the schema layer.
	/// </summary>
	public PageResult FirstPage(RequestContext context, int pageSize, IReadOnlyList<object> items,
		int? totalKnown = null, bool truncated = false) {
		lock (gate) {
			var state = new State {
				Id = ++stateSeq,
				Tool = context.Tool, SessionId = context.SessionId, Generation = context.Generation,
				PauseEpoch = context.PauseEpoch, PageSize = pageSize, FilterKey = context.FilterKey,
				Items = items, TotalKnown = totalKnown ?? items.Count, Truncated = truncated,
			};
			states[state.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = state;
			return Slice(state, 0);
		}
	}

	/// <summary>
	/// Continuation: the cursor must belong to the exact same binding (tool/session/generation/
	/// pause-epoch/filter) — any mismatch, an unknown cursor, a superseded state or an explicit
	/// page_size different from the original is STALE_HANDLE.
	/// </summary>
	public (PageResult? Page, ReadStatus Status) Continue(RequestContext context, string cursor, int? explicitPageSize) {
		lock (gate) {
			var decoded = decodeCursor(cursor);
			if (decoded is null)
				return (null, ReadStatus.StaleHandle);
			var (stateId, offset) = decoded.Value;
			if (!states.TryGetValue(stateId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var state))
				return (null, ReadStatus.StaleHandle);
			if (!SameBinding(state, context))
				return (null, ReadStatus.StaleHandle);
			if (explicitPageSize.HasValue && explicitPageSize.Value != state.PageSize)
				return (null, ReadStatus.StaleHandle);
			if (offset < 0 || offset > state.Items.Count)
				return (null, ReadStatus.StaleHandle);
			return (Slice(state, offset), ReadStatus.Ok);
		}
	}

	bool SameBinding(State state, RequestContext context) =>
		state.Tool == context.Tool && state.SessionId == context.SessionId
		&& state.Generation == context.Generation && state.PauseEpoch == context.PauseEpoch
		&& state.FilterKey == context.FilterKey;

	PageResult Slice(State state, int offset) {
		var page = new PageResult {
			Items = state.Items.Skip(offset).Take(state.PageSize).ToList(),
			Truncated = state.Truncated,
			TotalKnown = state.TotalKnown,
		};
		int nextOffset = offset + page.Items.Count;
		if (nextOffset < state.Items.Count)
			page.NextPageCursor = encodeCursor(state.Id, nextOffset);
		return page;
	}
}
