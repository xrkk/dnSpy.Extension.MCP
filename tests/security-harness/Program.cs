using System;
using System.Linq;
using System.Net;
using dnSpy.Extension.MCP;
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

		if (failures != 0)
			Environment.Exit(1);
	}
}
