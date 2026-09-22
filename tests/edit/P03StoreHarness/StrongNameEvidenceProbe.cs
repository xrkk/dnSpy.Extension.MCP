using System;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
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

		// ---- PLAN-CHANGE 2026.09.22: necessary classifier gates + observation lifecycle + one-shot consumption ----
		// This generated metadata fixture exercises the production five-argument classifier. Passing
		// all of these necessary gates is not proof that a real CLR loader rejection occurred.
		var fixtureDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnspy-mcp-strong-name-" + Guid.NewGuid().ToString("N"));
		System.IO.Directory.CreateDirectory(fixtureDirectory);
		var realHost = System.IO.Path.Combine(fixtureDirectory, "Host.exe");
		try {
			CreateClassifierHost(realHost);
			Check(System.IO.File.Exists(realHost), "classifier host fixture generated");
			var loaderMsg = "Could not load file or assembly 'SignedTarget, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0011223344556677' or one of its dependencies. Strong Name signature was invalid";
			string[] loadedOther = { "Host", "mscorlib" };
			var facts = DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg, realHost, loadedOther);
			Check(facts is not null && facts.AssemblyName == "SignedTarget" && facts.AssemblyVersion == "1.0.0.0"
				&& facts.PublicKeyToken == "0011223344556677", "necessary classifier gates accept synthesized framework-shaped candidate");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("SyStEm.PrIvAtE.CoReLiB", CorEStrongName, loaderMsg, realHost, loadedOther) is not null,
				"corelib module comparison is case-insensitive");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg, null, null) is null, "module alone rejected without differential");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("Host", CorEStrongName, loaderMsg, realHost, loadedOther) is null, "non-framework module rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection(null, CorEStrongName, loaderMsg, realHost, loadedOther) is null, "null module rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection(string.Empty, CorEStrongName, loaderMsg, realHost, loadedOther) is null, "empty module rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", unchecked((int)0x8013DE1A), loaderMsg, realHost, loadedOther) is null, "unapproved strong-name HRESULT rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", unchecked((int)0x80131509), loaderMsg, realHost, loadedOther) is null, "other HRESULT rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg) is not null, "legacy overload accepts approved HRESULT and framework module");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", unchecked((int)0x8013DE1A), loaderMsg) is null, "legacy overload rejects unapproved strong-name HRESULT");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, "sample supplied text", realHost, loadedOther) is null, "unparseable message rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName,
				"Could not load file or assembly 'Unsigned, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null' or one of its dependencies.", realHost, loadedOther) is null, "null token rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg, realHost, new[] { "mscorlib", "SignedTarget" }) is null, "already-loaded target rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg, realHost, new[] { "mscorlib", "Unrelated" }) is not null, "unloaded matching reference passes necessary differential");
			var driftMsg = loaderMsg.Replace("Version=1.0.0.0", "Version=9.9.9.9");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, driftMsg, realHost, loadedOther) is null, "version drift rejected");
			var wrongTokenMsg = loaderMsg.Replace("0011223344556677", "8899aabbccddeeff");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, wrongTokenMsg, realHost, loadedOther) is null, "token drift rejected");
			Check(DebugSessionService.ClassifyLoaderStrongNameRejection("mscorlib", CorEStrongName, loaderMsg, Environment.GetCommandLineArgs()[0], loadedOther) is null, "host not referencing identity rejected");
		}
		finally {
			try { System.IO.Directory.Delete(fixtureDirectory, recursive: true); } catch { }
		}
		var lifecycleMsg = "Could not load file or assembly 'SignedTarget, Version=1.2.3.4, Culture=neutral, PublicKeyToken=0011223344556677' or one of its dependencies. Strong Name signature was invalid";

		var evidence = new DebugSessionCoordinator(() => "session-e", () => now.ToString("O"), () => now);
		Check(evidence.BeginLaunch("launch", "start", "net-framework", "x64") && evidence.MarkLaunchClaimSucceeded(false, null), "evidence fixture running");
		evidence.ObservePaused("session-e", 1, true, new[] {
			new BreakInfoObservation(PauseCauseArbiter.Exception, 0, null, null, true, null,
				"thread-1", "module-1", "System.IO.FileLoadException", lifecycleMsg, CorEStrongName, false, true) {
				StrongNameRejection = new StrongNameRejectionFacts {
					LoaderModule = "mscorlib", HResult = CorEStrongName,
					AssemblyName = "SignedTarget", AssemblyVersion = "1.2.3.4", PublicKeyToken = "0011223344556677",
				},
			},
		});
		var evidenceEvents = evidence.ReadEvents("session-e", 0, 32, null)!;
		var evidenceCursor = EventCursor(evidenceEvents.Events.Single(x => EventKind(x) == EventKinds.Exception));
		var observed = evidence.ReadStrongNameFailure("session-e", evidenceCursor);
		Check(observed is not null && observed.AssemblyName == "SignedTarget" && observed.PublicKeyToken == "0011223344556677", "loader observation recorded at cursor");
		Check(!evidence.TryConsumeStrongNameFailure("session-e", evidenceCursor, "SignedTarget", "9.9.9.9", "0011223344556677"), "version drift rejected");
		Check(!evidence.TryConsumeStrongNameFailure("session-e", evidenceCursor, "SignedTarget", "1.2.3.4", "ffffffffffffffff"), "token drift rejected");
		Check(!evidence.TryConsumeStrongNameFailure("other-session", evidenceCursor, "SignedTarget", "1.2.3.4", "0011223344556677"), "cross-session consume rejected");
		Check(evidence.TryConsumeStrongNameFailure("session-e", evidenceCursor, "SignedTarget", "1.2.3.4", "0011223344556677"), "exact identity consumed");
		Check(!evidence.TryConsumeStrongNameFailure("session-e", evidenceCursor, "SignedTarget", "1.2.3.4", "0011223344556677"), "second consume rejected");
		Check(evidence.ReadStrongNameFailure("session-e", evidenceCursor) is null, "consumed observation unreadable");

		var seam = new DebugSessionCoordinator(() => "session-t");
		Check(seam.BeginLaunch("launch", "start", "net-framework", "x64") && seam.MarkLaunchClaimSucceeded(false, null), "seam fixture running");
		var seamCursor = seam.WriteStrongNameRejectionForTest("SeamTarget", "2.0.0.0", "aabbccddeeff0011");
		Check(seamCursor > 0 && seam.ReadStrongNameFailure("session-t", seamCursor) is not null, "seam records observation through event path");
		Check(seam.TryConsumeStrongNameFailure("session-t", seamCursor, "SeamTarget", "2.0.0.0", "aabbccddeeff0011"), "seam observation consumable");

		// CHK-20260922-04-02: every buffer eviction route invalidates the observation,
		// even when no later strong-name observation is recorded.
		CheckEviction("evict-count", c => {
			for (var i = 0; i < 4200; i++)
				c.WriteObservedException(true, false, "System.Exception", "ordinary exception");
		}, "ordinary event-count eviction");
		CheckEviction("evict-byte-multi", c => {
			for (var i = 0; i < 100; i++)
				c.WriteObservedException(true, false, "System.Exception", new string('m', 100000));
		}, "multi-event byte eviction");
		CheckEviction("evict-byte-single", c =>
			c.WriteObservedException(true, false, "System.Exception", new string('s', 8388000)),
			"single-event byte eviction");

		var restart = new DebugSessionCoordinator(() => "session-r");
		Check(restart.BeginLaunch("launch", "start", "net-framework", "x64") && restart.MarkLaunchClaimSucceeded(false, null), "restart fixture running");
		var oldCursor = RecordEvidence(restart, "session-r");
		var admission = restart.TryBeginControl(ControlOperation.Restart, "restart");
		Check(admission.Admitted && restart.MarkControlIssued(admission.Record!), "restart issued");
		Check(restart.ObserveProcessRemoved("session-r", 1, true, 0).Outcome == "pending-restart", "restart removal");
		Check(restart.BeginRestartRelaunch() && restart.Generation == 2, "restart generation");
		Check(restart.MarkLaunchClaimSucceeded(false, null) && restart.State == DebugStates.Running, "restart running");
		Check(restart.ReadStrongNameFailure("session-r", oldCursor) is null, "old generation rejected before new observation");
		var newCursor = RecordEvidence(restart, "session-r");
		var retained = restart.ReadEvents("session-r", 0, 32, null)!;
		Check(oldCursor >= retained.EarliestCursor, "old generation cursor remains buffered");
		Check(restart.ReadStrongNameFailure("session-r", oldCursor) is null, "new observation does not revive old generation");
		Check(!restart.TryConsumeStrongNameFailure("session-r", oldCursor, "SignedTarget", "1.0.0.0", "0011223344556677"), "old generation consume rejected");
		Check(restart.ReadStrongNameFailure("session-r", newCursor) is not null, "new generation observation readable");
		Check(restart.TryConsumeStrongNameFailure("session-r", newCursor, "SignedTarget", "1.0.0.0", "0011223344556677"), "new generation observation consumed");
		Check(!restart.TryConsumeStrongNameFailure("session-r", newCursor, "SignedTarget", "1.0.0.0", "0011223344556677"), "new generation observation one-shot");

		Console.WriteLine("PASS strong-name-evidence self_hresult_rejected=true sequence=true retention=true restart=true classifier_fixture_generated=true necessary_module_gate=true approved_hresult_only=true version_binding=true differential=true observation_lifecycle=true eviction_routes=true generation_binding=true one_shot_consume=true");
	}

	static void CreateClassifierHost(string path) {
		const string token = "0011223344556677";
		using var host = new ModuleDefUser("Host.exe") { Kind = ModuleKind.Console };
		new AssemblyDefUser("Host", new Version(1, 0, 0, 0)).Modules.Add(host);
		var reference = new AssemblyRefUser("SignedTarget", new Version(1, 0, 0, 0), new PublicKeyToken(token));
		host.Types.Add(new TypeDefUser("Fixture", "UnusedReference", new TypeRefUser(host, "Fixture", "Base", reference)));
		host.Write(path);
	}

	static long RecordEvidence(DebugSessionCoordinator coordinator, string sessionId) {
		var observed = coordinator.ObservePaused(sessionId, coordinator.Generation, true, new[] {
			new BreakInfoObservation(PauseCauseArbiter.Exception, 0, null, null, true, null,
				"thread-1", "module-1", "System.IO.FileLoadException",
				"Could not load file or assembly 'SignedTarget, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0011223344556677'",
				CorEStrongName, false, true) {
				StrongNameRejection = new StrongNameRejectionFacts {
					LoaderModule = "mscorlib", HResult = CorEStrongName, AssemblyName = "SignedTarget",
					AssemblyVersion = "1.0.0.0", PublicKeyToken = "0011223344556677",
				},
			},
		});
		Check(observed.Accepted, "evidence observation accepted");
		return coordinator.ReadEvents(sessionId, 0, 4096, null)!.Events
			.Where(x => EventKind(x) == EventKinds.Exception).Select(EventCursor).Last();
	}

	static void CheckEviction(string sessionId, Action<DebugSessionCoordinator> fill, string name) {
		var coordinator = new DebugSessionCoordinator(() => sessionId);
		Check(coordinator.BeginLaunch("launch", "start", "net-framework", "x64") && coordinator.MarkLaunchClaimSucceeded(false, null), name + " fixture running");
		var cursor = RecordEvidence(coordinator, sessionId);
		Check(coordinator.ReadStrongNameFailure(sessionId, cursor) is not null, name + " precondition observation readable");
		fill(coordinator);
		var retained = coordinator.ReadEvents(sessionId, 0, 32, null)!;
		Check(retained.EventsLost > 0 && cursor < retained.EarliestCursor, name + " precondition cursor evicted");
		Check(coordinator.ReadStrongNameFailure(sessionId, cursor) is null, name + " read rejected");
		Check(!coordinator.TryConsumeStrongNameFailure(sessionId, cursor, "SignedTarget", "1.0.0.0", "0011223344556677"), name + " consume rejected");
	}

	static string EventKind(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("kind").GetString()!; }
	static long EventCursor(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("cursor").GetInt64(); }
	static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); }
}
