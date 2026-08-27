using System;
using System.Collections.Generic;
using System.Threading;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Fixed dual-lane admission queue for active-session side-effect requests (CON-DYN-013):
/// API-DYN-006..009 and API-DYN-019 occupy the 8-slot control lane; every other side-effect API
/// occupies the 56-slot general lane; the total in-flight count never exceeds 64. The 9th
/// control or 57th general request is rejected before enqueueing (LIMIT_EXCEEDED), and the
/// scheduler drains the control lane first at every atomic coordinator mutation boundary while
/// each lane keeps FIFO order. Slots are released exactly once per ticket; capacity counts
/// in-flight tickets, so a released-but-still-queued entry frees its slot immediately.
/// </summary>
public sealed class DualLaneQueue : IDisposable {
	public const int ControlCapacity = 8;
	public const int GeneralCapacity = 56;
	public const int TotalCapacity = 64;

	public enum Lane { Control, General }

	public sealed class Ticket {
		public long Id { get; }
		public Lane Lane { get; }
		readonly DualLaneQueue owner;
		int released;
		internal Ticket(DualLaneQueue owner, Lane lane, long id) {
			this.owner = owner; Lane = lane; Id = id;
		}
		/// <summary>Releases the slot exactly once; a second release is rejected.</summary>
		public bool TryRelease() {
			if (Interlocked.Exchange(ref released, 1) == 1)
				return false;
			owner.Release(this);
			return true;
		}
	}

	readonly object gate = new();
	readonly Queue<long> controlOrder = new();
	readonly Queue<long> generalOrder = new();
	readonly Dictionary<long, Ticket> inFlight = new();
	int controlCount, generalCount;
	long ticketSeq;

	/// <summary>In-flight counts per lane plus the shared total.</summary>
	public (int Control, int General, int Total) Counts { get { lock (gate) return (controlCount, generalCount, inFlight.Count); } }

	public bool TryEnterControl(out Ticket? ticket) => TryEnter(Lane.Control, out ticket);
	public bool TryEnterGeneral(out Ticket? ticket) => TryEnter(Lane.General, out ticket);

	bool TryEnter(Lane lane, out Ticket? ticket) {
		lock (gate) {
			ticket = null;
			if (lane == Lane.Control ? controlCount >= ControlCapacity : generalCount >= GeneralCapacity)
				return false;
			if (inFlight.Count >= TotalCapacity)
				return false;
			var t = new Ticket(this, lane, ++ticketSeq);
			(lane == Lane.Control ? controlOrder : generalOrder).Enqueue(t.Id);
			inFlight[t.Id] = t;
			if (lane == Lane.Control) controlCount++; else generalCount++;
			ticket = t;
			return true;
		}
	}

	internal void Release(Ticket ticket) {
		lock (gate) {
			if (!inFlight.Remove(ticket.Id))
				return;
			if (ticket.Lane == Lane.Control) controlCount--; else generalCount--;
		}
	}

	/// <summary>
	/// Next ticket to run: control lane first (FIFO within the lane), otherwise general FIFO.
	/// Released-but-still-queued ids are skipped. Null when both lanes are drained.
	/// </summary>
	public Ticket? DequeueNext() {
		lock (gate) {
			while (controlOrder.Count > 0) {
				var id = controlOrder.Dequeue();
				if (inFlight.TryGetValue(id, out var t))
					return t;
			}
			while (generalOrder.Count > 0) {
				var id = generalOrder.Dequeue();
				if (inFlight.TryGetValue(id, out var t))
					return t;
			}
			return null;
		}
	}

	public void Dispose() { }
}
