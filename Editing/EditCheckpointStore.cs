using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using dnSpy.Extension.MCP.Debugger;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// The single disk seam used by <see cref="EditHistoryModule"/>.  It deliberately exposes
/// package-sized operations rather than ZIP or manifest primitives so path validation, owned
/// temporary files and atomic replacement cannot leak into the coordinator.
/// </summary>
internal interface IEditCheckpointStore : IDisposable {
	string ArtifactRoot { get; }
	IReadOnlyList<EditStoreObject> EnumerateCheckpointObjects();
	bool FinalExists(string lineageId);
	byte[] ReadFinal(string lineageId);
	byte[] ReadTemp(EditOwnedTemp temp);
	EditOwnedTemp CreateTemp(string lineageId, byte[] bytes);
	void FinalizeTemp(EditOwnedTemp temp, bool replaceExisting);
	void DeleteTemp(EditOwnedTemp temp);
	bool Matches(EditOwnedTemp temp);
	EditOutputResult WriteOutputAtomic(string relativeOrAbsolutePath, byte[] bytes, bool replaceExisting);
}

internal sealed class EditStoreObject {
	public string Name { get; init; } = string.Empty;
	public long Length { get; init; }
	public bool IsMeasurable { get; init; } = true;
	public bool IsTrustedFinal { get; init; }
	public bool IsResidual { get; init; }
}

internal sealed class EditOwnedTemp {
	public string LineageId { get; init; } = string.Empty;
	public string Path { get; init; } = string.Empty;
	public string FinalPath { get; init; } = string.Empty;
	public string FileId { get; init; } = string.Empty;
	public long Length { get; init; }
	public string Sha256 { get; init; } = string.Empty;
}

internal sealed class EditOutputResult {
	public string Path { get; init; } = string.Empty;
	public long Length { get; init; }
	public string Sha256 { get; init; } = string.Empty;
	public string FileId { get; init; } = string.Empty;
}

/// <summary>
/// Windows production adapter.  ArtifactRoot is held by the already-audited root lease, every
/// child is rejected when it is a reparse point, temporary names are server-random, and the
/// final switch is Move(CreateNew) or Replace.  There is intentionally no copy/delete fallback.
/// </summary>
internal sealed class WindowsEditCheckpointStore : IEditCheckpointStore {
	readonly SettingsRootLease rootLease;
	readonly string checkpointRoot;
	readonly string outputRoot;
	bool disposed;

	public WindowsEditCheckpointStore(McpSettingsSnapshot snapshot) {
		if (!SettingsRootLease.TryAcquire(snapshot, out var lease, out var error, force: true) || lease == null)
			throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
				["kind"] = "capability", ["capability"] = "artifact_root", ["reason"] = error ?? "ArtifactRoot lease failed",
			});
		rootLease = lease;
		ArtifactRoot = Path.GetFullPath(snapshot.ArtifactRoot).TrimEnd(Path.DirectorySeparatorChar);
		checkpointRoot = EnsureDirectDirectory("edit-checkpoints");
		outputRoot = EnsureDirectDirectory("edit-output");
	}

	public string ArtifactRoot { get; }

	public IReadOnlyList<EditStoreObject> EnumerateCheckpointObjects() {
		ThrowIfDisposed();
		var rows = new List<EditStoreObject>();
		var entries = Directory.EnumerateFileSystemEntries(checkpointRoot).Take(ArtifactStoreLedger.MaxStoreChildren + 1).ToArray();
		if (entries.Length > ArtifactStoreLedger.MaxStoreChildren)
			throw Capacity("store_children", entries.Length, ArtifactStoreLedger.MaxStoreChildren);
		foreach (var path in entries) {
			var name = Path.GetFileName(path);
			long length = 0;
			var trusted = false;
			try {
				var attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0) {
					rows.Add(new EditStoreObject { Name = name, IsResidual = true, IsMeasurable = false });
					continue;
				}
				length = new FileInfo(path).Length;
				trusted = IsLineageFile(name);
			}
			catch {
				rows.Add(new EditStoreObject { Name = name, IsResidual = true, IsMeasurable = false });
				continue;
			}
			rows.Add(new EditStoreObject {
				Name = name, Length = length, IsTrustedFinal = trusted,
				IsResidual = !trusted,
			});
		}
		return rows;
	}

	public bool FinalExists(string lineageId) {
		ThrowIfDisposed();
		var path = FinalPath(lineageId);
		if (!File.Exists(path)) return false;
		RejectReparse(path, allowMissing: false);
		return true;
	}

	public byte[] ReadFinal(string lineageId) {
		ThrowIfDisposed();
		var path = FinalPath(lineageId);
		RejectReparse(path, allowMissing: false);
		var info = new FileInfo(path);
		if (info.Length < 0 || info.Length > ArtifactStoreLedger.MaxFileBytes)
			throw Capacity("package_file_bytes", info.Length, ArtifactStoreLedger.MaxFileBytes);
		return File.ReadAllBytes(path);
	}

	public byte[] ReadTemp(EditOwnedTemp temp) {
		ThrowIfDisposed();
		RequireOwned(temp);
		return File.ReadAllBytes(temp.Path);
	}

	public EditOwnedTemp CreateTemp(string lineageId, byte[] bytes) {
		ThrowIfDisposed();
		if (bytes.LongLength > ArtifactStoreLedger.MaxFileBytes)
			throw Capacity("package_file_bytes", bytes.LongLength, ArtifactStoreLedger.MaxFileBytes);
		var finalPath = FinalPath(lineageId);
		EnsureCheckpointWriteCapacity(finalPath, bytes.LongLength);
		var tempPath = finalPath + ".tmp-" + EditWire.NewId("tmp").Substring(4);
		using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
			64 * 1024, FileOptions.WriteThrough)) {
			stream.Write(bytes, 0, bytes.Length);
			stream.Flush(true);
		}
		var observed = Observe(tempPath);
		return new EditOwnedTemp {
			LineageId = lineageId, Path = tempPath, FinalPath = finalPath,
			FileId = observed.FileId, Length = observed.Length, Sha256 = observed.Sha256,
		};
	}

	public void FinalizeTemp(EditOwnedTemp temp, bool replaceExisting) {
		ThrowIfDisposed();
		RequireOwned(temp);
		RejectReparse(temp.FinalPath, allowMissing: !replaceExisting);
		if (replaceExisting) File.Replace(temp.Path, temp.FinalPath, null, true);
		else File.Move(temp.Path, temp.FinalPath);
	}

	public void DeleteTemp(EditOwnedTemp temp) {
		ThrowIfDisposed();
		RequireOwned(temp);
		File.Delete(temp.Path);
	}

	public bool Matches(EditOwnedTemp temp) {
		try {
			var observed = Observe(temp.Path);
			return observed.FileId == temp.FileId && observed.Length == temp.Length && observed.Sha256 == temp.Sha256;
		}
		catch { return false; }
	}

	public EditOutputResult WriteOutputAtomic(string relativeOrAbsolutePath, byte[] bytes, bool replaceExisting) {
		ThrowIfDisposed();
		if (bytes.LongLength > ArtifactStoreLedger.MaxFileBytes)
			throw Capacity("export_file_bytes", bytes.LongLength, ArtifactStoreLedger.MaxFileBytes);
		var final = Path.IsPathRooted(relativeOrAbsolutePath)
			? Path.GetFullPath(relativeOrAbsolutePath)
			: Path.GetFullPath(Path.Combine(ArtifactRoot, relativeOrAbsolutePath));
		if (!IsBelowRoot(final, ArtifactRoot)) throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		if (IsBelowRoot(final, checkpointRoot) || final.EndsWith(".dnspy-mcp-checkpoints", StringComparison.OrdinalIgnoreCase)
			|| final.IndexOf(".dnspy-mcp-checkpoints.tmp-", StringComparison.OrdinalIgnoreCase) >= 0)
			throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		var directory = Path.GetDirectoryName(final) ?? throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		EnsurePathBelowArtifactRoot(directory);
		Directory.CreateDirectory(directory);
		EnsurePathBelowArtifactRoot(directory);
		RejectReparse(final, allowMissing: !replaceExisting);
		var temp = final + ".tmp-" + EditWire.NewId("tmp").Substring(4);
		try {
			using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
				64 * 1024, FileOptions.WriteThrough)) {
				stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
			}
			var staged = Observe(temp);
			if (replaceExisting) File.Replace(temp, final, null, true);
			else File.Move(temp, final);
			var committed = Observe(final);
			if (committed.Length != staged.Length || committed.Sha256 != staged.Sha256)
				throw new IOException("export identity changed during atomic switch");
			return new EditOutputResult { Path = final, Length = committed.Length, Sha256 = committed.Sha256, FileId = committed.FileId };
		}
		catch {
			try { if (File.Exists(temp)) File.Delete(temp); } catch { }
			throw;
		}
	}

	string EnsureDirectDirectory(string name) {
		var path = Path.Combine(ArtifactRoot, name);
		if (File.Exists(path)) throw new IOException(name + " is not a directory");
		if (!Directory.Exists(path)) Directory.CreateDirectory(path);
		RejectReparse(path, allowMissing: false);
		return path;
	}

	void EnsureCheckpointWriteCapacity(string finalPath, long incomingLength) {
		var objects = EnumerateCheckpointObjects();
		if (objects.Any(x => !x.IsMeasurable))
			throw Capacity("unmeasurable_store_object", objects.Count(x => !x.IsMeasurable), 0);
		if (objects.Count >= ArtifactStoreLedger.MaxStoreChildren)
			throw Capacity("store_children", objects.Count + 1L, ArtifactStoreLedger.MaxStoreChildren);
		long total = 0;
		foreach (var row in objects) total = CheckedAdd(total, row.Length, ArtifactStoreLedger.MaxStoreBytes, "store_bytes");
		CheckedAdd(total, incomingLength, ArtifactStoreLedger.MaxStoreBytes, "store_bytes");
		if (File.Exists(finalPath)) {
			RejectReparse(finalPath, allowMissing: false);
			var existing = new FileInfo(finalPath).Length;
			CheckedAdd(existing, incomingLength, ArtifactStoreLedger.MaxSessionBytes, "lineage_transient_bytes");
		}
	}

	void EnsurePathBelowArtifactRoot(string directory) {
		if (!IsBelowRoot(directory, ArtifactRoot)) throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		var relative = directory.Substring(ArtifactRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var current = ArtifactRoot;
		foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)) {
			current = Path.Combine(current, part);
			if (Directory.Exists(current)) RejectReparse(current, allowMissing: false);
		}
	}

	string FinalPath(string lineageId) {
		if (!EditHistoryIds.Is(lineageId, "lineage")) throw new ArgumentException("invalid lineage_id", "lineage_id");
		return Path.Combine(checkpointRoot, lineageId + ".dnspy-mcp-checkpoints");
	}

	static bool IsLineageFile(string name) => name.StartsWith("lineage-", StringComparison.Ordinal)
		&& name.EndsWith(".dnspy-mcp-checkpoints", StringComparison.Ordinal)
		&& EditHistoryIds.Is(name.Substring(0, name.Length - ".dnspy-mcp-checkpoints".Length), "lineage");

	void RequireOwned(EditOwnedTemp temp) {
		if (!Matches(temp)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var expected = FinalPath(temp.LineageId);
		if (!string.Equals(Path.GetFullPath(temp.FinalPath), expected, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(Path.GetDirectoryName(Path.GetFullPath(temp.Path)), checkpointRoot, StringComparison.OrdinalIgnoreCase))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}

	static void RejectReparse(string path, bool allowMissing) {
		if (!File.Exists(path) && !Directory.Exists(path)) {
			if (allowMissing) return;
			throw new FileNotFoundException("path is absent", path);
		}
		if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			throw new IOException("path is a reparse point: " + path);
	}

	static bool IsBelowRoot(string path, string root) {
		var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
			&& (p[r.Length] == Path.DirectorySeparatorChar || p[r.Length] == Path.AltDirectorySeparatorChar);
	}

	static (string FileId, long Length, string Sha256) Observe(string path) {
		RejectReparse(path, allowMissing: false);
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var hash = SHA256.Create();
		var sha = string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
		var fileId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsFileId(stream.SafeFileHandle) : sha.Substring(0, 32);
		return (fileId, stream.Length, sha);
	}

	static string WindowsFileId(SafeFileHandle handle) {
		if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var info))
			throw new IOException("file identity query failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		return (info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8")).PadLeft(32, '0');
	}

	static EditDomainException Capacity(string limit, long current, long maximum) => new("EDIT_CAPACITY_EXCEEDED",
		new Dictionary<string, object?> { ["kind"] = "capacity", ["limit"] = limit, ["current"] = current, ["maximum"] = maximum });
	static long CheckedAdd(long value, long add, long maximum, string limit) {
		try { var result = checked(value + add); if (result > maximum) throw Capacity(limit, result, maximum); return result; }
		catch (OverflowException) { throw Capacity(limit, long.MaxValue, maximum); }
	}

	void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(WindowsEditCheckpointStore)); }
	public void Dispose() { if (disposed) return; disposed = true; rootLease.Dispose(); }

	[StructLayout(LayoutKind.Sequential)]
	struct BY_HANDLE_FILE_INFORMATION {
		public uint FileAttributes;
		public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION info);
}

/// <summary>Deterministic fault-capable adapter used by the store harness; never MEF-exported.</summary>
internal sealed class InMemoryEditCheckpointStore : IEditCheckpointStore {
	readonly Dictionary<string, (byte[] Bytes, string FileId)> finals = new(StringComparer.Ordinal);
	readonly Dictionary<string, (byte[] Bytes, string FileId)> temps = new(StringComparer.Ordinal);
	readonly Dictionary<string, (byte[] Bytes, string FileId)> outputs = new(StringComparer.OrdinalIgnoreCase);
	long identity;
	public string? ArmedStage { get; set; }
	internal int TempCount => temps.Count;
	internal int FinalCount => finals.Count;
	internal IReadOnlyList<string> ListFinalIds() => finals.Keys.ToArray();
	internal byte[] FinalBytes(string lineageId) => ReadFinal(lineageId);
	public string ArtifactRoot { get; }
	public InMemoryEditCheckpointStore(string artifactRoot = @"C:\artifacts") => ArtifactRoot = artifactRoot;

	public IReadOnlyList<EditStoreObject> EnumerateCheckpointObjects() => finals.Select(x => new EditStoreObject {
		Name = x.Key + ".dnspy-mcp-checkpoints", Length = x.Value.Bytes.LongLength, IsTrustedFinal = true,
	}).Concat(temps.Select(x => new EditStoreObject { Name = x.Key, Length = x.Value.Bytes.LongLength, IsResidual = true })).ToArray();
	public bool FinalExists(string lineageId) => finals.ContainsKey(lineageId);
	public byte[] ReadFinal(string lineageId) => finals.TryGetValue(lineageId, out var value) ? (byte[])value.Bytes.Clone() : throw new FileNotFoundException();
	public byte[] ReadTemp(EditOwnedTemp temp) { Require(temp, out var value); Fault("readback"); return (byte[])value.Bytes.Clone(); }
	public EditOwnedTemp CreateTemp(string lineageId, byte[] bytes) {
		Fault("prewrite");
		if (bytes.LongLength > ArtifactStoreLedger.MaxFileBytes) throw StoreCapacity("package_file_bytes", bytes.LongLength, ArtifactStoreLedger.MaxFileBytes);
		var children = finals.Count + temps.Count; if (children >= ArtifactStoreLedger.MaxStoreChildren) throw StoreCapacity("store_children", children + 1L, ArtifactStoreLedger.MaxStoreChildren);
		var storeBytes = finals.Values.Sum(x => x.Bytes.LongLength) + temps.Values.Sum(x => x.Bytes.LongLength);
		if (storeBytes + bytes.LongLength > ArtifactStoreLedger.MaxStoreBytes) throw StoreCapacity("store_bytes", storeBytes + bytes.LongLength, ArtifactStoreLedger.MaxStoreBytes);
		if (finals.TryGetValue(lineageId, out var final) && final.Bytes.LongLength + bytes.LongLength > ArtifactStoreLedger.MaxSessionBytes)
			throw StoreCapacity("lineage_transient_bytes", final.Bytes.LongLength + bytes.LongLength, ArtifactStoreLedger.MaxSessionBytes);
		var name = lineageId + ".tmp-" + (++identity).ToString("x32"); var fileId = identity.ToString("x32");
		temps.Add(name, ((byte[])bytes.Clone(), fileId));
		return new EditOwnedTemp { LineageId = lineageId, Path = name, FinalPath = lineageId, FileId = fileId,
			Length = bytes.LongLength, Sha256 = EditWire.Sha256(bytes) };
	}
	public void FinalizeTemp(EditOwnedTemp temp, bool replaceExisting) {
		Fault("finalize"); Require(temp, out var value);
		if (!replaceExisting && finals.ContainsKey(temp.LineageId)) throw new IOException("final exists");
		finals[temp.LineageId] = value; temps.Remove(temp.Path);
	}
	public void DeleteTemp(EditOwnedTemp temp) { Fault("cleanup"); Require(temp, out _); temps.Remove(temp.Path); }
	public bool Matches(EditOwnedTemp temp) => temps.TryGetValue(temp.Path, out var value) && value.FileId == temp.FileId
		&& value.Bytes.LongLength == temp.Length && EditWire.Sha256(value.Bytes) == temp.Sha256;
	public bool OutputBytes(string path, out byte[] bytes) {
		if (outputs.TryGetValue(path, out var entry)) { bytes = (byte[])entry.Bytes.Clone(); return true; }
		bytes = Array.Empty<byte>(); return false;
	}

	public EditOutputResult WriteOutputAtomic(string path, byte[] bytes, bool replaceExisting) {
		Fault("finalize"); if (!replaceExisting && outputs.ContainsKey(path)) throw new IOException("output exists");
		var fileId = (++identity).ToString("x32"); outputs[path] = ((byte[])bytes.Clone(), fileId);
		return new EditOutputResult { Path = path, Length = bytes.LongLength, Sha256 = EditWire.Sha256(bytes), FileId = fileId };
	}
	void Require(EditOwnedTemp temp, out (byte[] Bytes, string FileId) value) {
		if (!temps.TryGetValue(temp.Path, out value) || value.FileId != temp.FileId || value.Bytes.LongLength != temp.Length
			|| EditWire.Sha256(value.Bytes) != temp.Sha256) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}
	void Fault(string stage) { if (ArmedStage == stage) { ArmedStage = null; throw new IOException("injected " + stage); } }
	static EditDomainException StoreCapacity(string limit, long current, long maximum) => new("EDIT_CAPACITY_EXCEEDED",
		new Dictionary<string, object?> { ["kind"] = "capacity", ["limit"] = limit, ["current"] = current, ["maximum"] = maximum });
	public void Dispose() { }
}

internal static class EditHistoryIds {
	public static bool Is(string value, string prefix) {
		if (value == null || value.Length != prefix.Length + 33 || !value.StartsWith(prefix + "-", StringComparison.Ordinal)) return false;
		for (var i = prefix.Length + 1; i < value.Length; i++) {
			var c = value[i]; if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
		}
		return true;
	}
}
