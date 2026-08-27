using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace dnSpy.Extension.MCP {
/// <summary>
/// Immutable 11-field settings snapshot (CON-DYN-014). The required field set, safe defaults
/// and every structural combination rule are fixed by the plan; no other settings fields may
/// exist. Runtime lease checks (AllowedSampleRoot/ArtifactRoot) happen at Apply/startup, not here.
/// </summary>
public sealed class McpSettingsSnapshot {
	public const string SchemaVersionValue = "dnspy.mcp.settings.v1";
	public const int MaxCidrs = 16;

	public string SchemaVersion { get; }
	public bool EnableServer { get; }
	public string Host { get; }
	public int Port { get; }
	public bool DebugToolsEnabled { get; }
	public bool DedicatedDebugInstanceAcknowledged { get; }
	public string AllowedSampleRoot { get; }
	public string ArtifactRoot { get; }
	public IReadOnlyList<string> RemoteAllowedCidrs { get; }
	/// <summary>64 lowercase hex chars (SHA-256 of the 32-byte token) or null. The raw token is never stored.</summary>
	public string? RemoteTokenVerifier { get; }
	public bool RemoteHostOnlyAcknowledged { get; }

	McpSettingsSnapshot(string schemaVersion, bool enableServer, string host, int port,
		bool debugToolsEnabled, bool dedicatedDebugInstanceAcknowledged, string allowedSampleRoot,
		string artifactRoot, IReadOnlyList<string> remoteAllowedCidrs, string? remoteTokenVerifier,
		bool remoteHostOnlyAcknowledged) {
		SchemaVersion = schemaVersion;
		EnableServer = enableServer;
		Host = host;
		Port = port;
		DebugToolsEnabled = debugToolsEnabled;
		DedicatedDebugInstanceAcknowledged = dedicatedDebugInstanceAcknowledged;
		AllowedSampleRoot = allowedSampleRoot;
		ArtifactRoot = artifactRoot;
		RemoteAllowedCidrs = remoteAllowedCidrs;
		RemoteTokenVerifier = remoteTokenVerifier;
		RemoteHostOnlyAcknowledged = remoteHostOnlyAcknowledged;
	}

	/// <summary>Safe defaults (CON-DYN-014): server off, loopback, no debug, empty sample root, no remote.</summary>
	public static McpSettingsSnapshot SafeDefaults() => new(
		SchemaVersionValue, false, "localhost", 3000, false, false, "",
		System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnspy-mcp"),
		Array.Empty<string>(), null, false);

	/// <summary>
	/// Structural validation per CON-DYN-014: port range, canonical deduplicated ordinal-sorted
	/// CIDR set (IPv4-mapped IPv6 rejected, host bits must be zero, canonical textual round-trip),
	/// verifier shape, and the loopback/non-loopback combination matrix. Returns null on success.
	/// </summary>
	public static string? TryValidate(int port, string host, IReadOnlyList<string> cidrs,
		string? verifierHex, bool remoteAck, out List<string> canonicalCidrs) {
		canonicalCidrs = new List<string>();
		if (port < 1 || port > 65535)
			return "Port must be within 1..65535.";
		if (!CanonicalizeCidrs(cidrs, out canonicalCidrs, out var cidrError))
			return cidrError;
		if (verifierHex != null && !IsHex64(verifierHex))
			return "RemoteTokenVerifier must be 64 lowercase hexadecimal characters.";
		bool loopback = host == "localhost";
		if (!loopback) {
			if (!IPAddress.TryParse(host, out var hostIp) || host.IndexOfAny(new[] { '%', '/' }) >= 0)
				return "Host must be 'localhost' or a unicast IP literal.";
			if (hostIp.IsIPv4MappedToIPv6)
				return "Host must not be an IPv4-mapped IPv6 literal.";
			if (IPAddress.IsLoopback(hostIp))
				return "Loopback IP literals are not a supported Host; use 'localhost'.";
			var hb = hostIp.GetAddressBytes();
			bool v4 = hb.Length == 4;
			if (v4 ? hb[0] >= 224 : hostIp.IsIPv6Multicast)
				return "Host must be a unicast address.";
		}
		if (loopback) {
			if (canonicalCidrs.Count != 0) return "RemoteAllowedCidrs must be empty for loopback Host.";
			if (verifierHex != null) return "RemoteTokenVerifier must be null for loopback Host.";
			if (remoteAck) return "RemoteHostOnlyAcknowledged must be false for loopback Host.";
		}
		else {
			if (canonicalCidrs.Count == 0) return "RemoteAllowedCidrs must not be empty for non-loopback Host.";
			if (verifierHex == null) return "RemoteTokenVerifier must not be null for non-loopback Host.";
			if (!remoteAck) return "RemoteHostOnlyAcknowledged must be true for non-loopback Host.";
		}
		return null;
	}

	/// <summary>Builds a fully validated snapshot; returns null with an error otherwise.</summary>
	public static McpSettingsSnapshot? TryCreate(bool enableServer, string host, int port,
		bool debugToolsEnabled, bool dedicatedDebugInstanceAcknowledged, string allowedSampleRoot,
		string artifactRoot, IReadOnlyList<string> remoteAllowedCidrs, string? remoteTokenVerifier,
		bool remoteHostOnlyAcknowledged, out string? error) {
		error = TryValidate(port, host, remoteAllowedCidrs, remoteTokenVerifier,
			remoteHostOnlyAcknowledged, out var canonical);
		if (error != null)
			return null;
		return new McpSettingsSnapshot(SchemaVersionValue, enableServer, host, port,
			debugToolsEnabled, dedicatedDebugInstanceAcknowledged, allowedSampleRoot ?? "",
			artifactRoot ?? "", canonical, remoteTokenVerifier, remoteHostOnlyAcknowledged);
	}

	static bool IsHex64(string s) {
		if (s.Length != 64) return false;
		foreach (var c in s)
			if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
				return false;
		return true;
	}

	/// <summary>
	/// Canonicalizes a CIDR list: each entry "addr/prefix" must parse, IPv4-mapped IPv6 is
	/// rejected, host bits must be zero, and the text must already be the canonical rendering
	/// (dotted-decimal IPv4 / RFC 5952 lowercase IPv6). The input must also be deduplicated and
	/// ordinal-sorted; the output preserves that canonical order.
	/// </summary>
	public static bool CanonicalizeCidrs(IReadOnlyList<string> input, out List<string> canonical, out string? error) {
		canonical = new List<string>();
		error = null;
		if (input.Count > MaxCidrs) { error = $"At most {MaxCidrs} CIDRs are allowed."; return false; }
		foreach (var raw in input) {
			var slash = raw.LastIndexOf('/');
			if (slash <= 0 || slash == raw.Length - 1) { error = $"Invalid CIDR: {raw}"; return false; }
			var addrText = raw.Substring(0, slash);
			if (!int.TryParse(raw.Substring(slash + 1), out var prefix)) { error = $"Invalid CIDR prefix: {raw}"; return false; }
			if (!IPAddress.TryParse(addrText, out var addr)) { error = $"Invalid CIDR address: {raw}"; return false; }
			if (addr.IsIPv4MappedToIPv6) { error = $"IPv4-mapped IPv6 CIDRs are rejected: {raw}"; return false; }
			var bytes = addr.GetAddressBytes();
			bool isV4 = bytes.Length == 4;
			if (isV4 != (addr.AddressFamily == AddressFamily.InterNetwork)) { error = $"Invalid CIDR: {raw}"; return false; }
			int bitLen = bytes.Length * 8;
			if (prefix < 0 || prefix > bitLen) { error = $"CIDR prefix out of range: {raw}"; return false; }
			for (int i = prefix; i < bitLen; i++) {
				if ((bytes[i / 8] & (0x80 >> (i % 8))) != 0) { error = $"CIDR host bits must be zero: {raw}"; return false; }
			}
			var canonicalText = addr.ToString() + "/" + prefix.ToString();
			if (!string.Equals(raw, canonicalText, StringComparison.Ordinal)) { error = $"CIDR must use canonical text: {raw}"; return false; }
			canonical.Add(canonicalText);
		}
		if (canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Count) { error = "CIDRs must not contain duplicates."; return false; }
		if (!canonical.SequenceEqual(canonical.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal)) { error = "CIDRs must be ordinal-sorted."; return false; }
		canonical = canonical.ToList();
		return true;
	}

	/// <summary>The 11 fields as a JCS-serializable object graph (keys are sorted by the writer).</summary>
	public Dictionary<string, object?> ToCanonicalObject() => new() {
		["SchemaVersion"] = SchemaVersion,
		["EnableServer"] = EnableServer,
		["Host"] = Host,
		["Port"] = Port,
		["DebugToolsEnabled"] = DebugToolsEnabled,
		["DedicatedDebugInstanceAcknowledged"] = DedicatedDebugInstanceAcknowledged,
		["AllowedSampleRoot"] = AllowedSampleRoot,
		["ArtifactRoot"] = ArtifactRoot,
		["RemoteAllowedCidrs"] = RemoteAllowedCidrs.ToList(),
		["RemoteTokenVerifier"] = RemoteTokenVerifier,
		["RemoteHostOnlyAcknowledged"] = RemoteHostOnlyAcknowledged,
	};

	public string ToCanonicalJson() => Jcs.Serialize(ToCanonicalObject());
}

/// <summary>
/// Minimal RFC 8785 (JSON Canonicalization Scheme) writer for the extension's canonical domains
/// (objects with string keys, strings, integers, booleans, null, arrays). Floating-point numbers
/// are rejected because ECMAScript shortest-round-trip formatting is out of scope here; no
/// canonical payload in this extension contains floats.
/// </summary>
public static class Jcs {
	public static string Serialize(object? value) {
		var sb = new StringBuilder();
		Write(value, sb);
		return sb.ToString();
	}

	static void Write(object? value, StringBuilder sb) {
		switch (value) {
			case null: sb.Append("null"); break;
			case bool b: sb.Append(b ? "true" : "false"); break;
			case int: case long: sb.Append(value.ToString()); break;
			case string s: WriteString(s, sb); break;
			case IEnumerable<object?> seq:
				sb.Append('[');
				bool first = true;
				foreach (var item in seq) {
					if (!first) sb.Append(',');
					first = false;
					Write(item, sb);
				}
				sb.Append(']');
				break;
			case IDictionary<string, object?> obj:
				sb.Append('{');
				bool firstKv = true;
				foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal)) {
					if (!firstKv) sb.Append(',');
					firstKv = false;
					WriteString(kv.Key, sb);
					sb.Append(':');
					Write(kv.Value, sb);
				}
				sb.Append('}');
				break;
			default:
				throw new NotSupportedException($"JCS: unsupported value type {value.GetType().Name}");
		}
	}

	static void WriteString(string s, StringBuilder sb) {
		sb.Append('"');
		foreach (var c in s) {
			switch (c) {
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\b': sb.Append("\\b"); break;
				case '\f': sb.Append("\\f"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				default:
					if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
					else sb.Append(c);
					break;
			}
		}
		sb.Append('"');
	}
}

/// <summary>
/// Two-key staged/committed persistence and the CON-DYN-014 startup recovery matrix. The Apply
/// write sequence (pending -> server transition -> committed -> memory swap -> clear pending) is
/// enforced by the ApplySnapshot path; this type only interprets stored state.
/// </summary>
public static class McpSettingsPersistence {
	public const string CommittedKey = "SettingsSnapshotJson";
	public const string PendingKey = "SettingsPendingJson";

	public const string InvalidStoredWarning = "Stored MCP settings were invalid. Safe defaults are active.";
	public const string CommittedPendingClearWarning = "The pending recovery marker could not be cleared. The committed settings remain authoritative.";
	public const string SafeDefaultsPendingClearWarning = "The pending recovery marker could not be cleared. The safe defaults are active.";
	public const string ApplyErrorTitle = "MCP Debug Settings";
	public const string ApplyErrorBody = "Settings could not be applied. The previously committed settings remain authoritative.";
	public const string ApplyActiveBody = "Cannot change debug enablement or path roots while an MCP debug session is active. Stop it and try again.";
	public const string ApplyPendingClearFailedBody = "Settings were applied, but the pending recovery marker could not be cleared. The committed settings remain authoritative and will be used on restart.";

	/// <summary>Outcome of startup recovery: the authoritative snapshot, fixed warning and clear request.</summary>
	public sealed class RecoveryResult {
		public McpSettingsSnapshot Snapshot { get; }
		/// <summary>null, or one of the fixed warning strings above.</summary>
		public string? Warning { get; }
		/// <summary>Best-effort removal of the pending key is requested when true.</summary>
		public bool TryClearPending { get; }
		public RecoveryResult(McpSettingsSnapshot snapshot, string? warning, bool tryClearPending) {
			Snapshot = snapshot; Warning = warning; TryClearPending = tryClearPending;
		}
	}

	/// <summary>
	/// Parses a stored snapshot JSON string: the decoded object must have exactly the 11 fields
	/// with correct JSON types, every CON-DYN-014 combination rule must pass, and the raw string
	/// must already be the RFC 8785 canonical form of that object.
	/// </summary>
	public static McpSettingsSnapshot? TryParseEffective(string? storedJson, out string? parseError) {
		parseError = null;
		if (string.IsNullOrEmpty(storedJson))
			return null;
		JsonDocument doc;
		try {
			doc = JsonDocument.Parse(storedJson, new JsonDocumentOptions {
				AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow,
			});
		}
		catch (JsonException) { parseError = "not parseable JSON"; return null; }
		using (doc) {
			if (doc.RootElement.ValueKind != JsonValueKind.Object) { parseError = "root is not an object"; return null; }
			var expected = new HashSet<string> {
				"SchemaVersion", "EnableServer", "Host", "Port", "DebugToolsEnabled",
				"DedicatedDebugInstanceAcknowledged", "AllowedSampleRoot", "ArtifactRoot",
				"RemoteAllowedCidrs", "RemoteTokenVerifier", "RemoteHostOnlyAcknowledged",
			};
			var props = doc.RootElement.EnumerateObject().ToList();
			if (props.Count != expected.Count || props.Any(p => !expected.Contains(p.Name))) {
				parseError = "field set is not exactly the 11 required fields"; return null;
			}
			bool ReadBool(string name) => props.First(p => p.Name == name).Value.GetBoolean();
			string ReadString(string name) => props.First(p => p.Name == name).Value.GetString()!;
			var schema = props.First(p => p.Name == "SchemaVersion").Value;
			if (schema.ValueKind != JsonValueKind.String || schema.GetString() != McpSettingsSnapshot.SchemaVersionValue) {
				parseError = "wrong schema_version"; return null;
			}
			foreach (var name in new[] { "EnableServer", "DebugToolsEnabled", "DedicatedDebugInstanceAcknowledged", "RemoteHostOnlyAcknowledged" })
				if (props.First(p => p.Name == name).Value.ValueKind != JsonValueKind.True && props.First(p => p.Name == name).Value.ValueKind != JsonValueKind.False)
					{ parseError = $"{name} must be boolean"; return null; }
			foreach (var name in new[] { "Host", "AllowedSampleRoot", "ArtifactRoot" })
				if (props.First(p => p.Name == name).Value.ValueKind != JsonValueKind.String)
					{ parseError = $"{name} must be a string"; return null; }
			var portEl = props.First(p => p.Name == "Port").Value;
			if (portEl.ValueKind != JsonValueKind.Number || !portEl.TryGetInt32(out var port) || port < 1 || port > 65535)
				{ parseError = "Port must be an integer within 1..65535"; return null; }
			var verifierEl = props.First(p => p.Name == "RemoteTokenVerifier").Value;
			string? verifier = verifierEl.ValueKind == JsonValueKind.Null ? null
				: verifierEl.ValueKind == JsonValueKind.String ? verifierEl.GetString()
				: null;
			if (verifierEl.ValueKind != JsonValueKind.Null && verifierEl.ValueKind != JsonValueKind.String)
				{ parseError = "RemoteTokenVerifier must be a string or null"; return null; }
			var cidrEl = props.First(p => p.Name == "RemoteAllowedCidrs").Value;
			if (cidrEl.ValueKind != JsonValueKind.Array || cidrEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
				{ parseError = "RemoteAllowedCidrs must be an array of strings"; return null; }
			var cidrs = cidrEl.EnumerateArray().Select(e => e.GetString()!).ToList();

			var snap = McpSettingsSnapshot.TryCreate(ReadBool("EnableServer"), ReadString("Host"), port,
				ReadBool("DebugToolsEnabled"), ReadBool("DedicatedDebugInstanceAcknowledged"),
				ReadString("AllowedSampleRoot"), ReadString("ArtifactRoot"), cidrs, verifier,
				ReadBool("RemoteHostOnlyAcknowledged"), out var semError);
			if (snap == null) { parseError = semError; return null; }
			if (!string.Equals(storedJson, snap.ToCanonicalJson(), StringComparison.Ordinal)) {
				parseError = "stored JSON is not canonical RFC 8785"; return null;
			}
			return snap;
		}
	}

	/// <summary>
	/// The CON-DYN-014 startup matrix over the two stored keys plus (only when both are absent)
	/// the legacy EnableServer/Host/Port attributes. Only a fully valid committed snapshot is
	/// authoritative; every other branch loads committed (if valid) or safe defaults.
	/// </summary>
	public static RecoveryResult Recover(string? committedJson, string? pendingJson,
		(bool? enableServer, string? host, int? port)? legacy) {
		bool hasCommitted = !string.IsNullOrEmpty(committedJson);
		bool hasPending = !string.IsNullOrEmpty(pendingJson);
		var committedValid = TryParseEffective(committedJson, out _);

		if (!hasCommitted && !hasPending) {
			if (legacy is (bool enable, string host, int port)) {
				var legacySnap = McpSettingsSnapshot.TryCreate(enable, host, port, false, false, "",
					McpSettingsSnapshot.SafeDefaults().ArtifactRoot, Array.Empty<string>(), null, false, out _);
				if (legacySnap != null)
					return new RecoveryResult(legacySnap, null, false);
				return new RecoveryResult(McpSettingsSnapshot.SafeDefaults(), InvalidStoredWarning, false);
			}
			return new RecoveryResult(McpSettingsSnapshot.SafeDefaults(), null, false);
		}
		if (committedValid != null && hasPending && string.Equals(committedJson, pendingJson, StringComparison.Ordinal))
			return new RecoveryResult(committedValid, null, true);
		if (committedValid != null)
			// Pending differs / invalid / absent: committed stays authoritative, clear pending best-effort.
			return new RecoveryResult(committedValid, null, hasPending);
		// Committed missing or invalid: pending is never activated; safe defaults win.
		return new RecoveryResult(McpSettingsSnapshot.SafeDefaults(),
			hasCommitted ? InvalidStoredWarning : null, hasPending);
	}
}
}
