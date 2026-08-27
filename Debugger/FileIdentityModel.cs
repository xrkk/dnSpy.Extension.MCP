using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// TYPE-DYN-012 FileIdentity: role, object kind, final path, volume serial and file id, with
/// sha256 required for files and forbidden for directories. The wire field order is the
/// declaration order and must not change.
/// </summary>
public sealed class FileIdentityDto {
	[JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
	[JsonPropertyName("object_kind")] public string ObjectKind { get; init; } = string.Empty;
	[JsonPropertyName("final_path")] public string FinalPath { get; init; } = string.Empty;
	[JsonPropertyName("volume_serial")] public string VolumeSerial { get; init; } = string.Empty;
	[JsonPropertyName("file_id")] public string FileId { get; init; } = string.Empty;
	[JsonPropertyName("sha256")] public string? Sha256 { get; init; }

	public static readonly IReadOnlyList<string> Roles = new[] { "target", "host", "harness", "working_directory" };
	public const string KindFile = "file";
	public const string KindDirectory = "directory";

	/// <summary>Validates shape and the file/directory sha256 rule; returns null error when valid.</summary>
	public static string? Validate(FileIdentityDto identity) {
		if (!Roles.Contains(identity.Role))
			return $"unknown role: {identity.Role}";
		if (identity.ObjectKind != KindFile && identity.ObjectKind != KindDirectory)
			return $"unknown object_kind: {identity.ObjectKind}";
		if (string.IsNullOrEmpty(identity.FinalPath))
			return "final_path must not be empty";
		if (!IsVolumeSerial(identity.VolumeSerial))
			return "volume_serial must be 0x + 16 lowercase hex characters";
		if (!IsFileId(identity.FileId))
			return "file_id must be 32 lowercase hex characters";
		if (identity.ObjectKind == KindFile && !IsSha256(identity.Sha256))
			return "file identities must carry a 64 lowercase hex sha256";
		if (identity.ObjectKind == KindDirectory && identity.Sha256 != null)
			return "directory identities must omit sha256";
		return null;
	}

	public static bool IsVolumeSerial(string s) => s.Length == 2 + 16 && s.StartsWith("0x", StringComparison.Ordinal) && IsHex(s, 2);
	public static bool IsFileId(string s) => s.Length == 32 && IsHex(s, 0);
	public static bool IsSha256(string? s) => s != null && s.Length == 64 && IsHex(s, 0);

	static bool IsHex(string s, int offset) {
		for (int i = offset; i < s.Length; i++) {
			var c = s[i];
			if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
				return false;
		}
		return true;
	}

	/// <summary>Identity equality: volume serial + file id (+ sha256 when both present).</summary>
	public bool SameObjectAs(FileIdentityDto other) =>
		VolumeSerial == other.VolumeSerial && FileId == other.FileId
		&& (Sha256 ?? string.Empty) == (other.Sha256 ?? string.Empty);
}

/// <summary>
/// Windows-path relation helpers used by the lease rules. Comparison is case-insensitive with
/// both separators accepted; containment requires a real component boundary (C:\\a does not
/// contain C:\\ab).
/// </summary>
public static class WindowsPathRelation {
	public static string Normalize(string path) {
		var p = path.Replace('/', '\\');
		return p.TrimEnd('\\');
	}

	public static bool EqualPath(string a, string b) =>
		string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

	/// <summary>True when parent contains child as a subpath (strict component boundary).</summary>
	public static bool Contains(string parent, string child) {
		var p = Normalize(parent);
		var c = Normalize(child);
		if (p.Length == 0 || c.Length <= p.Length)
			return false;
		if (!c.StartsWith(p, StringComparison.OrdinalIgnoreCase))
			return false;
		return c[p.Length] == '\\' || (p.EndsWith(":", StringComparison.OrdinalIgnoreCase) && c[p.Length - 1] == '\\');
	}

	/// <summary>
	/// CON-DYN-008/011: the roots must be pairwise unequal and mutually non-containing.
	/// </summary>
	public static bool RootsAreDisjoint(IReadOnlyList<string> roots) {
		for (int i = 0; i < roots.Count; i++) {
			for (int j = i + 1; j < roots.Count; j++) {
				if (EqualPath(roots[i], roots[j]) || Contains(roots[i], roots[j]) || Contains(roots[j], roots[i]))
					return false;
			}
		}
		return true;
	}
}

/// <summary>
/// Pure bookkeeping for CON-DYN-011 leases: which role is bound to which validated identity,
/// and whether a re-observed identity still matches. The Windows handle acquisition itself
/// (CreateFileW with FILE_FLAG_OPEN_REPARSE_POINT, final path and FILE_ID_INFO) is the
/// production-side part; this model defines the comparison semantics the lease enforces.
/// </summary>
public sealed class FileIdentityLeaseBook {
	readonly Dictionary<string, FileIdentityDto> leased = new(StringComparer.Ordinal);

	/// <summary>Records the leased identity for a role (first lease wins; returns false on a duplicate role).</summary>
	public bool Lease(FileIdentityDto identity) {
		var error = FileIdentityDto.Validate(identity);
		if (error != null)
			throw new ArgumentException(error);
		lock (leased)
			return leased.TryAdd(identity.Role, identity);
	}

	public enum ObserveResult { NoLease, Match, Mismatch }

	/// <summary>
	/// Compares an observed identity against the lease for its role: no lease, exact match, or
	/// TARGET_MISMATCH (final path, volume serial, file id or sha256 changed).
	/// </summary>
	public ObserveResult Observe(FileIdentityDto observed) {
		lock (leased) {
			if (!leased.TryGetValue(observed.Role, out var bound))
				return ObserveResult.NoLease;
			if (!bound.SameObjectAs(observed))
				return ObserveResult.Mismatch;
			if (!WindowsPathRelation.EqualPath(bound.FinalPath, observed.FinalPath))
				return ObserveResult.Mismatch;
			return ObserveResult.Match;
		}
	}

	public IReadOnlyList<FileIdentityDto> LeasedIdentities {
		get { lock (leased) return leased.Values.ToList(); }
	}
}
