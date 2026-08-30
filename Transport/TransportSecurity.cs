using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace dnSpy.Extension.MCP.Transport {
/// <summary>
/// CON-DYN-006 bearer-token verification. The token must strictly base64url-decode (no padding,
/// canonical trailing bits) to exactly 32 bytes; its SHA-256 is compared in fixed time against
/// the 32 bytes parsed from the stored 64-lowercase-hex verifier. Missing/malformed/mismatched
/// credentials all fail closed.
/// </summary>
public static class RemoteTokenAuth {
	public const int TokenByteLength = 32;

	/// <summary>Verifies an Authorization header value against the stored verifier hex.</summary>
	public static bool Verify(string? authorizationHeader, string verifierHex) {
		if (authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal))
			return false;
		var token = authorizationHeader.Substring("Bearer ".Length);
		if (!TryDecodeBase64UrlStrict(token, out var tokenBytes) || tokenBytes.Length != TokenByteLength)
			return false;
		if (!TryParseHex64(verifierHex, out var verifierBytes) || verifierBytes.Length != TokenByteLength)
			return false;
		using var sha = SHA256.Create();
		var digest = sha.ComputeHash(tokenBytes);
		return FixedTimeEquals(digest, verifierBytes);
	}

	/// <summary>Strict unpadded base64url decode (RFC 4648 §5): rejects '=', non-alphabet chars,
	/// impossible lengths and non-zero trailing bits (canonical form only).</summary>
	public static bool TryDecodeBase64UrlStrict(string s, out byte[] bytes) {
		bytes = Array.Empty<byte>();
		if (s.Length == 0 || s.Length % 4 == 1)
			return false;
		int fullGroups = s.Length / 4;
		int remChars = s.Length % 4;
		int outLen = fullGroups * 3 + (remChars == 2 ? 1 : remChars == 3 ? 2 : 0);
		var result = new byte[outLen];
		int outPos = 0;
		int buffer = 0, bits = 0;
		foreach (var c in s) {
			int v = c switch {
				>= 'A' and <= 'Z' => c - 'A',
				>= 'a' and <= 'z' => c - 'a' + 26,
				>= '0' and <= '9' => c - '0' + 52,
				'-' => 62,
				'_' => 63,
				_ => -1,
			};
			if (v < 0)
				return false;
			buffer = (buffer << 6) | v;
			bits += 6;
			if (bits >= 8) {
				bits -= 8;
				if (outPos >= outLen)
					return false;
				result[outPos++] = (byte)((buffer >> bits) & 0xFF);
			}
		}
		// Canonical form: leftover bits must be zero.
		if (bits > 0 && (buffer & ((1 << bits) - 1)) != 0)
			return false;
		if (outPos != outLen)
			return false;
		bytes = result;
		return true;
	}

	/// <summary>Parses exactly 64 lowercase hex characters into 32 bytes.</summary>
	public static bool TryParseHex64(string hex, out byte[] bytes) {
		bytes = Array.Empty<byte>();
		if (hex.Length != 64)
			return false;
		var result = new byte[32];
		for (int i = 0; i < 32; i++) {
			int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
			if (hi < 0 || lo < 0)
				return false;
			result[i] = (byte)((hi << 4) | lo);
		}
		bytes = result;
		return true;
	}

	static int HexVal(char c) => c switch {
		>= '0' and <= '9' => c - '0',
		>= 'a' and <= 'f' => c - 'a' + 10,
		_ => -1,
	};

	/// <summary>Length-safe constant-time equality (net48 has no CryptographicOperations).</summary>
	public static bool FixedTimeEquals(byte[] a, byte[] b) {
		if (a.Length != b.Length)
			return false;
		int diff = 0;
		for (int i = 0; i < a.Length; i++)
			diff |= a[i] ^ b[i];
		return diff == 0;
	}
}

/// <summary>
/// CON-DYN-006 CIDR admission. Only the request's direct RemoteEndPoint is consulted; any
/// forwarded header is ignored by construction (never read). IPv4-mapped IPv6 peers are
/// normalized to IPv4 first; null or unnormalizable peers fail closed.
/// </summary>
public static class CidrFilter {
	/// <summary>Normalizes a peer address for matching: IPv4-mapped IPv6 becomes IPv4.</summary>
	public static IPAddress? NormalizePeer(IPAddress? peer) {
		if (peer is null)
			return null;
		if (peer.IsIPv4MappedToIPv6) {
			var all = peer.GetAddressBytes();
			var v4 = new byte[4];
			System.Array.Copy(all, all.Length - 4, v4, 0, 4);
			return new IPAddress(v4);
		}
		return peer;
	}

	/// <summary>True when the normalized peer falls inside any canonical network.</summary>
	public static bool IsAllowed(IPAddress? peer, IReadOnlyList<string> canonicalCidrs) {
		var p = NormalizePeer(peer);
		if (p is null)
			return false;
		if (canonicalCidrs.Count == 1 && canonicalCidrs[0] == "*")
			return true;
		var peerBytes = p.GetAddressBytes();
		foreach (var cidr in canonicalCidrs) {
			var slash = cidr.LastIndexOf('/');
			if (!IPAddress.TryParse(cidr.Substring(0, slash), out var net))
				continue;
			if (!int.TryParse(cidr.Substring(slash + 1), out var prefix))
				continue;
			var netBytes = net.GetAddressBytes();
			if (netBytes.Length != peerBytes.Length)
				continue;
			bool match = true;
			for (int i = 0; i < prefix; i++) {
				if ((netBytes[i / 8] & (0x80 >> (i % 8))) != (peerBytes[i / 8] & (0x80 >> (i % 8)))) {
					match = false;
					break;
				}
			}
			if (match)
				return true;
		}
		return false;
	}
}

/// <summary>Fixed wire shapes of the pre-parse HTTP rejections (CON-DYN-006/009).</summary>
public static class HttpRejectShapes {
	public const string WwwAuthenticate = "Bearer realm=\"dnspy-mcp\"";
	public const string RetryAfterSeconds = "1";
	public const int StatusUnauthorized = 401;
	public const int StatusForbidden = 403;
	public const int StatusTooManyRequests = 429;
	public const int StatusPayloadTooLarge = 413;
}
}
