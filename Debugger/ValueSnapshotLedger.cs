using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>One materialization input node, supplied in the final provider/BFS order.</summary>
public sealed class NodeInput {
	public string Name { get; init; } = string.Empty;
	public string Kind { get; init; } = string.Empty;
	public string Type { get; init; } = string.Empty;
	public string Display { get; init; } = string.Empty;
	public int Depth { get; init; }
	/// <summary>Index of the parent node in the same BFS order (null for roots).</summary>
	public int? ParentIndex { get; init; }
	public bool HasChildren { get; init; }
	public bool IsNull { get; init; }
	/// <summary>True when the node could be expanded (children exist and are representable).</summary>
	public bool Expandable { get; init; }
}

/// <summary>One materialized immutable node with its preallocated handle (TYPE-DYN-007 shape).</summary>
public sealed class MaterializedNode {
	public string Name { get; internal set; } = string.Empty;
	public string Kind { get; internal set; } = string.Empty;
	public string Type { get; internal set; } = string.Empty;
	public string Display { get; internal set; } = string.Empty;
	public int Depth { get; internal set; }
	public string? ParentValueHandle { get; internal set; }
	public string? ValueHandle { get; internal set; }
	public bool HasChildren { get; internal set; }
	public bool IsNull { get; internal set; }
	public bool Truncated { get; internal set; }
	public string? UnavailableReason { get; internal set; }
}

/// <summary>
/// Pure pause-epoch value-snapshot accounting (CON-DYN-009 / API-DYN-020/021). Each pause epoch
/// holds at most 2 live snapshots (a 3rd creation is LIMIT_EXCEEDED with no evaluation). Handles
/// are preallocated for every expandable node at materialization time, counted against the
/// epoch's 4096 total immediately; once the budget is spent, further expandable nodes are marked
/// <c>unavailable_reason=value_handle_limit,truncated=true</c> and get no handle. Page issuance
/// transfers that page's handles atomically from snapshot ownership to the epoch's returned
/// store; the final page destroys the snapshot/cursor and frees the slot while the issued
/// handles survive until epoch end. Abandoned snapshots keep their un-issued handles (and their
/// slot) until the epoch closes. Closing the epoch closes every still-live handle exactly once
/// and makes them all stale. Contract constants are ctor-injectable for tests.
/// </summary>
public sealed class ValueSnapshotLedger {
	public const int DefaultSnapshotSlots = 2;
	public const int DefaultNodesPerSnapshot = 1024;
	public const int DefaultHandlesPerEpoch = 4096;

	readonly object gate = new();
	readonly int snapshotSlots;
	readonly int nodesPerSnapshot;
	readonly int handlesPerEpoch;
	readonly Func<string> newHandle;
	readonly Action<string> onCloseHandle;

	sealed class Snapshot {
		public string Id = string.Empty;
		public string Kind = string.Empty;
		public int PageSize;
		public List<MaterializedNode> Nodes = new();
		public int IssuedUpTo;
		public bool Finalized;
		public HashSet<string> OwnedHandles = new(StringComparer.Ordinal);
	}

	readonly List<Snapshot> live = new();
	readonly HashSet<string> returnedHandles = new(StringComparer.Ordinal);
	readonly HashSet<string> closedHandles = new(StringComparer.Ordinal);
	bool epochOpen;
	long snapshotSeq;

	public ValueSnapshotLedger(Func<string>? newHandle = null, Action<string>? onCloseHandle = null,
		int snapshotSlots = DefaultSnapshotSlots, int nodesPerSnapshot = DefaultNodesPerSnapshot,
		int handlesPerEpoch = DefaultHandlesPerEpoch) {
		this.newHandle = newHandle ?? DefaultNewHandle;
		this.onCloseHandle = onCloseHandle ?? (_ => { });
		this.snapshotSlots = snapshotSlots;
		this.nodesPerSnapshot = nodesPerSnapshot;
		this.handlesPerEpoch = handlesPerEpoch;
		static string DefaultNewHandle() {
			var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
			return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		}
	}

	public int LiveSnapshotCount { get { lock (gate) return live.Count; } }
	public int HandlesInUse { get { lock (gate) return live.Sum(s => s.OwnedHandles.Count) + returnedHandles.Count; } }
	public int ReturnedHandleCount { get { lock (gate) return returnedHandles.Count; } }

	/// <summary>Opens a new pause epoch (previous handles must already be closed).</summary>
	public void BeginEpoch() {
		lock (gate) {
			epochOpen = true;
		}
	}

	public enum CreateError { None, NoFreeSlot, EpochClosed }

	/// <summary>
	/// Materializes one immutable snapshot in the given order: at most nodesPerSnapshot nodes,
	/// handles preallocated for expandable nodes while the epoch budget lasts; beyond the budget
	/// expandable nodes carry the fixed truncation marker and no handle.
	/// </summary>
	public (List<MaterializedNode>? Nodes, string SnapshotId, CreateError Error) TryCreateSnapshot(
		string kind, int pageSize, IReadOnlyList<NodeInput> nodes) {
		lock (gate) {
			if (!epochOpen)
				return (null, string.Empty, CreateError.EpochClosed);
			if (live.Count >= snapshotSlots)
				return (null, string.Empty, CreateError.NoFreeSlot);
			var snapshot = new Snapshot {
				Id = "snap-" + (++snapshotSeq),
				Kind = kind,
				PageSize = pageSize,
			};
			int budgetLeft = handlesPerEpoch - HandlesInUseLocked;
			foreach (var input in nodes.Take(nodesPerSnapshot)) {
				var node = new MaterializedNode {
					Name = input.Name, Kind = input.Kind, Type = input.Type, Display = input.Display,
					Depth = input.Depth, HasChildren = input.HasChildren, IsNull = input.IsNull,
				};
				if (input.ParentIndex is int pi && pi >= 0 && pi < snapshot.Nodes.Count)
					node.ParentValueHandle = snapshot.Nodes[pi].ValueHandle;
				if (input.Expandable && !input.IsNull && input.HasChildren) {
					if (budgetLeft > 0) {
						node.ValueHandle = newHandle();
						snapshot.OwnedHandles.Add(node.ValueHandle);
						budgetLeft--;
					}
					else {
						// Handle budget spent: the node stays but can never be expanded; its
						// descendants are not materialized.
						node.UnavailableReason = "value_handle_limit";
						node.Truncated = true;
					}
				}
				snapshot.Nodes.Add(node);
			}
			live.Add(snapshot);
			return (snapshot.Nodes.ToList(), snapshot.Id, CreateError.None);
		}
	}

	int HandlesInUseLocked => live.Sum(s => s.OwnedHandles.Count) + returnedHandles.Count;

	public sealed class PageResult {
		public List<MaterializedNode> Items { get; } = new();
		public string SnapshotId { get; internal set; } = string.Empty;
		public bool Final { get; internal set; }
		/// <summary>Handles transferred to the returned store by this page.</summary>
		public List<string> TransferredHandles { get; } = new();
	}

	/// <summary>
	/// Issues the next page of a snapshot. Page-size must equal the snapshot's original size.
	/// The page's handles transfer atomically to the returned store; the final page frees the
	/// slot and destroys the cursor while issued handles survive to epoch end.
	/// </summary>
	public PageResult? IssuePage(string snapshotId, int pageSize) {
		lock (gate) {
			var snapshot = live.FirstOrDefault(s => s.Id == snapshotId);
			if (snapshot is null || snapshot.Finalized)
				return null;
			if (pageSize != snapshot.PageSize)
				return null;
			var page = new PageResult { SnapshotId = snapshotId };
			int count = Math.Min(pageSize, snapshot.Nodes.Count - snapshot.IssuedUpTo);
			for (int i = 0; i < count; i++) {
				var node = snapshot.Nodes[snapshot.IssuedUpTo + i];
				page.Items.Add(node);
				if (node.ValueHandle != null && snapshot.OwnedHandles.Remove(node.ValueHandle)) {
					returnedHandles.Add(node.ValueHandle);
					page.TransferredHandles.Add(node.ValueHandle);
				}
			}
			snapshot.IssuedUpTo += count;
			if (snapshot.IssuedUpTo >= snapshot.Nodes.Count) {
				snapshot.Finalized = true;
				page.Final = true;
				live.Remove(snapshot);
			}
			return page;
		}
	}

	/// <summary>True when the handle is a returned (issued) handle of the open epoch.</summary>
	public bool IsReturnedHandle(string handle) {
		lock (gate)
			return epochOpen && returnedHandles.Contains(handle);
	}

	/// <summary>
	/// Creation/serialization failure: closes every un-transferred handle of the snapshot and
	/// releases its slot; already-issued handles stay alive to epoch end.
	/// </summary>
	public bool FailSnapshot(string snapshotId) {
		lock (gate) {
			var snapshot = live.FirstOrDefault(s => s.Id == snapshotId);
			if (snapshot is null)
				return false;
			foreach (var handle in snapshot.OwnedHandles) {
				closedHandles.Add(handle);
				onCloseHandle(handle);
			}
			snapshot.OwnedHandles.Clear();
			live.Remove(snapshot);
			return true;
		}
	}

	/// <summary>
	/// Closes the epoch: every still-live handle (snapshot-owned and returned) closes exactly
	/// once, counts drop to zero, and all handles become stale afterwards.
	/// </summary>
	public List<string> CloseEpoch() {
		lock (gate) {
			var all = new List<string>();
			foreach (var snapshot in live)
				all.AddRange(snapshot.OwnedHandles);
			all.AddRange(returnedHandles);
			foreach (var handle in all.Distinct(StringComparer.Ordinal)) {
				if (closedHandles.Add(handle))
					onCloseHandle(handle);
			}
			live.Clear();
			returnedHandles.Clear();
			epochOpen = false;
			return all;
		}
	}
}
