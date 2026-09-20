using System;
using System.Linq;
using System.Text.Json;
using dnSpy.Extension.MCP.Debugger;

static class StrongNameEvidenceProbe {
	const int CorEStrongName = unchecked((int)0x8013141A);

	public static void Run() {
		var now = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
		var sequence = new DebugSessionCoordinator(() => "session-a", () => now.ToString("O"), () => now);
		Check(sequence.BeginLaunch("launch", "start", "net-framework", "x64"), "launch admitted");
		Check(sequence.MarkLaunchClaimSucceeded(false, null), "launch running");
		var stopped = sequence.ObservePaused("session-a", 1, true, new[] {
			new BreakInfoObservation(PauseCauseArbiter.Exception, 0, null, null, true, null,
				"thread-1", "throwing-module", "System.IO.FileLoadException", "sample supplied text",
				CorEStrongName, false, true),
		});
		Check(stopped.Accepted && stopped.PrimaryCause == PauseCauseArbiter.Exception, "exception pause cause");
		var events = sequence.ReadEvents("session-a", 0, 32, null)!;
		var kinds = events.Events.Select(EventKind).ToArray();
		Check(Array.IndexOf(kinds, EventKinds.Paused) < Array.IndexOf(kinds, EventKinds.Exception), "paused precedes exception detail");
		var exceptionCursor = EventCursor(events.Events.Single(x => EventKind(x) == EventKinds.Exception));
		Check(sequence.ReadStrongNameFailure("session-a", exceptionCursor) is null,
			"self-thrown HRESULT is not trusted loader evidence");
		Check(sequence.ReadStrongNameFailure("other-session", exceptionCursor) is null, "session mismatch rejected");
		Check(sequence.ReadStrongNameFailure("session-a", exceptionCursor + 1) is null, "unknown cursor rejected");
		var ordinary = new DebugSessionCoordinator(() => "session-o");
		Check(ordinary.BeginLaunch("launch", "start", "net-framework", "x64") && ordinary.MarkLaunchClaimSucceeded(false, null), "ordinary fixture running");
		ordinary.ObservePaused("session-o", 1, true, new[] {
			new BreakInfoObservation(PauseCauseArbiter.Exception, 0, null, null, true, null,
				null, "dependency-module", "System.InvalidOperationException", "ordinary", unchecked((int)0x80131509), false, true),
		});
		var ordinaryEvents = ordinary.ReadEvents("session-o", 0, 32, null)!;
		var ordinaryCursor = EventCursor(ordinaryEvents.Events.Single(x => EventKind(x) == EventKinds.Exception));
		Check(ordinary.ReadStrongNameFailure("session-o", ordinaryCursor) is null, "ordinary exception rejected");
		var normal = new DebugSessionCoordinator(() => "session-n");
		Check(normal.BeginLaunch("launch", "start", "net-framework", "x64") && normal.MarkLaunchClaimSucceeded(false, null), "normal fixture running");
		Check(normal.ObserveProcessRemoved("session-n", 1, true, 0).Accepted, "normal exit observed");
		Check(normal.ReadStrongNameFailure("session-n", 1) is null, "normal exit rejected");

		Check(sequence.ObserveProcessRemoved("session-a", 1, true, 1).Accepted, "terminal observation");
		Check(sequence.ReadEvents("session-a", 0, 32, null) is not null, "terminal events retained");
		now = now.Add(DebugSessionCoordinator.TerminalRetention).Add(TimeSpan.FromSeconds(1));
		Check(sequence.ReadEvents("session-a", 0, 32, null) is null, "terminal events expire");

		var restart = new DebugSessionCoordinator(() => "session-r");
		Check(restart.BeginLaunch("launch", "start", "net-framework", "x64") && restart.MarkLaunchClaimSucceeded(false, null), "restart fixture running");
		var admission = restart.TryBeginControl(ControlOperation.Restart, "restart");
		Check(admission.Admitted && restart.MarkControlIssued(admission.Record!), "restart issued");
		Check(restart.ObserveProcessRemoved("session-r", 1, true, 0).Outcome == "pending-restart", "restart removal");
		Check(restart.BeginRestartRelaunch() && restart.Generation == 2, "restart generation");
		Check(restart.MarkLaunchClaimSucceeded(false, null) && restart.State == DebugStates.Running, "restart running");
		Console.WriteLine("PASS strong-name-evidence self_hresult_rejected=true sequence=true retention=true restart=true");
	}

	static string EventKind(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("kind").GetString()!; }
	static long EventCursor(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("cursor").GetInt64(); }
	static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); }
}
