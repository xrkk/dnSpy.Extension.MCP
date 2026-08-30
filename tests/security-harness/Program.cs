using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using dnSpy.Extension.MCP;
using dnSpy.Extension.MCP.Debugger;
using dnSpy.Extension.MCP.Transport;

static class Program {
	static int failures;

	static void Assert(bool condition, string name) {
		if (condition) {
			Console.WriteLine($"PASS {name}");
			return;
		}
		failures++;
		Console.Error.WriteLine($"FAIL {name}");
	}

	static void Main() {
		var defaults = McpSettingsSnapshot.SafeDefaults();
		Assert(defaults.IsRemote, "defaults are remote host-only mode");
		Assert(!defaults.RequiresRemoteToken, "defaults do not require a token");
		Assert(defaults.RemoteAllowedCidrs.SequenceEqual(new[] { McpSettingsSnapshot.TrustedHostOnlyPeerCidr }),
			"defaults trust only the VMware host peer /32");

		var tokenless = McpSettingsSnapshot.TryCreate(true, "192.168.204.149", 15378,
			true, true, "", McpSettingsSnapshot.DefaultArtifactRoot(),
			new[] { "192.168.204.1/32" }, null, true, out var tokenlessError);
		Assert(tokenless != null && tokenlessError == null, "enabled trusted-peer tokenless snapshot is valid");

		var wildcard = McpSettingsSnapshot.TryCreate(true, "192.168.204.149", 15378,
			true, true, "", McpSettingsSnapshot.DefaultArtifactRoot(),
			new[] { "*" }, null, true, out var wildcardError);
		Assert(wildcard == null && wildcardError?.Contains("192.168.204.1/32", StringComparison.Ordinal) == true,
			"tokenless wildcard is rejected");

		var subnet = McpSettingsSnapshot.TryCreate(true, "192.168.204.149", 15378,
			true, true, "", McpSettingsSnapshot.DefaultArtifactRoot(),
			new[] { "192.168.204.0/24" }, null, true, out var subnetError);
		Assert(subnet == null && subnetError?.Contains("192.168.204.1/32", StringComparison.Ordinal) == true,
			"tokenless subnet is rejected");

		var tokenMode = McpSettingsSnapshot.TryCreate(true, "192.168.204.149", 15378,
			true, true, "", McpSettingsSnapshot.DefaultArtifactRoot(),
			new[] { "*" }, new string('0', 64), true, out var tokenModeError);
		Assert(tokenMode != null && tokenMode!.RequiresRemoteToken && tokenModeError == null,
			"explicit token mode remains available");

		Assert(CidrFilter.IsAllowed(IPAddress.Parse("192.168.204.1"), new[] { "192.168.204.1/32" }),
			"trusted VMware host peer is admitted");
		Assert(!CidrFilter.IsAllowed(IPAddress.Parse("192.168.204.2"), new[] { "192.168.204.1/32" }),
			"another host-only peer is denied");
		Assert(!CidrFilter.IsAllowed(null, new[] { "192.168.204.1/32" }), "missing peer is denied");

		VerifyRestartedArtifactStore();

		if (failures != 0)
			Environment.Exit(1);
	}

	static void VerifyRestartedArtifactStore() {
		var fs = new ArtifactFs();
		fs.Tree["old-session"] = new Dictionary<string, (string Volume, string Id, long Length)>(StringComparer.Ordinal) {
			["old.bin"] = ("vol", "old-id", 4),
		};
		var ledger = new ArtifactStoreLedger(fs, maxSessions: 2, maxFile: 10, maxSession: 10, maxStore: 10);
		ledger.Initialize();

		Assert(ledger.AdmitNewSession("new-session") == ArtifactStoreLedger.AdmitResult.Ok,
			"restart keeps old artifacts read-only and admits a fresh session");
		Assert(ledger.AdmitArtifactReservationForTest("new-session", "new.bin", 6)
			== ArtifactStoreLedger.AdmitResult.Ok,
			"restart-counted stale bytes allow an exact store-limit write");
		Assert(ledger.AdmitArtifactReservationForTest("new-session", "over.bin", 1)
			== ArtifactStoreLedger.AdmitResult.LimitExceeded,
			"restart-counted stale bytes enforce the whole-store byte limit");

		fs.Tree["old-session"]["old.bin"] = ("vol", "replaced-id", 4);
		Assert(ledger.AdmitArtifactReservationForTest("new-session", "after-replace.bin", 0)
			== ArtifactStoreLedger.AdmitResult.TargetMismatch,
			"replaced stale artifacts fail closed before another write");
	}

	sealed class ArtifactFs : IArtifactStoreFs {
		public readonly Dictionary<string, Dictionary<string, (string Volume, string Id, long Length)>> Tree =
			new(StringComparer.Ordinal);
		int nextId;

		public IReadOnlyList<string> EnumerateRootChildren() => Tree.Keys.ToList();
		public IReadOnlyList<string> EnumerateSessionChildren(string sessionId) =>
			Tree.TryGetValue(sessionId, out var children) ? children.Keys.ToList() : Array.Empty<string>();
		public bool SessionDirectoryExists(string sessionId) => Tree.ContainsKey(sessionId);
		public bool TryLeaseExistingSessionDirectory(string sessionId) => Tree.ContainsKey(sessionId);
		public (string VolumeSerial, string FileId, long Length)? ObserveChild(string sessionId, string relativeName) =>
			Tree.TryGetValue(sessionId, out var children) && children.TryGetValue(relativeName, out var child)
				? (child.Volume, child.Id, child.Length) : null;
		public void CreateSessionDirectory(string sessionId) =>
			Tree.Add(sessionId, new Dictionary<string, (string Volume, string Id, long Length)>(StringComparer.Ordinal));
		public ArtifactStoreLedger.ChildRecord CreateChildFile(string sessionId, string relativeName,
			long length, byte[]? payload, ArtifactOperationRecord? operation) {
			var child = (Volume: "vol", Id: "new-id-" + ++nextId, Length: length);
			Tree[sessionId].Add(relativeName, child);
			return new ArtifactStoreLedger.ChildRecord(relativeName, child.Volume, child.Id, child.Length,
				new string('0', 64));
		}
	}
}
