using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// CON-DYN-014 runtime validation for a debug-enabled settings snapshot. It validates the
/// AllowedSampleRoot, ArtifactRoot and extension directory through non-reparse Windows handles,
/// requires NTFS, compares final paths, and retains the two configured root chains until the
/// authoritative snapshot changes. The extension-directory probe is released after validation.
/// </summary>
public sealed class SettingsRootLease : IDisposable {
	readonly List<SafeFileHandle> retained;

	SettingsRootLease(List<SafeFileHandle> retained) => this.retained = retained;

	public static bool TryAcquire(McpSettingsSnapshot snapshot, out SettingsRootLease? lease, out string? error,
		bool force = false) {
		lease = null;
		error = null;
		if (!snapshot.DebugToolsEnabled && !force)
			return true;
		if (string.IsNullOrWhiteSpace(snapshot.ArtifactRoot)) {
			error = "ArtifactRoot must be non-empty when debug tools are enabled.";
			return false;
		}

		var keep = new List<SafeFileHandle>();
		var probe = new List<SafeFileHandle>();
		try {
			string? sampleFinal = null;
			if (!string.IsNullOrWhiteSpace(snapshot.AllowedSampleRoot))
				sampleFinal = AcquireChain(snapshot.AllowedSampleRoot, createFinal: false, keep);
			var artifactFinal = AcquireChain(snapshot.ArtifactRoot, createFinal: true, keep);
			var extensionDirectory = Path.GetDirectoryName(typeof(SettingsRootLease).Assembly.Location);
			if (string.IsNullOrEmpty(extensionDirectory))
				throw new IOException("extension directory is unavailable");
			var extensionFinal = AcquireChain(extensionDirectory, createFinal: false, probe);
			var roots = new List<string> { artifactFinal, extensionFinal };
			if (sampleFinal != null)
				roots.Add(sampleFinal);
			if (!WindowsPathRelation.RootsAreDisjoint(roots))
				throw new IOException("AllowedSampleRoot, ArtifactRoot and extension directory must be disjoint");
			lease = new SettingsRootLease(keep);
			keep = new List<SafeFileHandle>();
			return true;
		}
		catch (Exception ex) {
			error = ex.Message;
			return false;
		}
		finally {
			foreach (var handle in keep) handle.Dispose();
			foreach (var handle in probe) handle.Dispose();
		}
	}

	static string AcquireChain(string path, bool createFinal, List<SafeFileHandle> leases) {
		var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
		var volumeRoot = Path.GetPathRoot(full);
		if (string.IsNullOrEmpty(volumeRoot))
			throw new IOException("path volume root is unavailable");
		var format = new DriveInfo(volumeRoot).DriveFormat;
		if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
			throw new IOException($"path must be on NTFS (found {format})");

		var components = new Stack<string>();
		for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) {
			components.Push(current);
			var parent = Path.GetDirectoryName(current);
			if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
				break;
		}
		string? finalRoot = null;
		while (components.Count != 0) {
			var component = components.Pop();
			if (!Directory.Exists(component)) {
				if (!createFinal || !string.Equals(component, full, StringComparison.OrdinalIgnoreCase)
					|| !CreateDirectoryW(component, IntPtr.Zero))
					throw new IOException($"root component is absent or creation failed: {component}",
						new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			}
			if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
				throw new IOException($"root component is a reparse point: {component}");
			var raw = CreateFileW(component, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
				IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
			if (raw == IntPtr.Zero || raw == new IntPtr(-1))
				throw new IOException($"root component lease failed: {component}",
					new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			var lease = new SafeFileHandle(raw, ownsHandle: true);
			var finalPath = FinalPathOf(raw).TrimEnd(Path.DirectorySeparatorChar);
			if (!string.Equals(finalPath, Path.GetFullPath(component).TrimEnd(Path.DirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase)) {
				lease.Dispose();
				throw new IOException($"root final path mismatch: {component} -> {finalPath}");
			}
			if (!GetFileInformationByHandle(raw, out _)) {
				lease.Dispose();
				throw new IOException($"root identity query failed: {component}",
					new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
			}
			leases.Add(lease);
			finalRoot = finalPath;
		}
		return finalRoot ?? throw new IOException("root chain is empty");
	}

	static string FinalPathOf(IntPtr handle) {
		var buffer = new System.Text.StringBuilder(32768);
		var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
		if (count == 0 || count >= buffer.Capacity)
			throw new IOException("GetFinalPathNameByHandleW failed",
				new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		var result = buffer.ToString();
		return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
	}

	public void Dispose() {
		foreach (var handle in retained) handle.Dispose();
		retained.Clear();
	}

	const uint GENERIC_READ = 0x80000000;
	const uint FILE_SHARE_READ = 0x00000001;
	const uint FILE_SHARE_WRITE = 0x00000002;
	const uint OPEN_EXISTING = 3;
	const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
	const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
		IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);
	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION info);
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern uint GetFinalPathNameByHandleW(IntPtr hFile, System.Text.StringBuilder path,
		uint pathLength, uint flags);

	[StructLayout(LayoutKind.Sequential)]
	struct BY_HANDLE_FILE_INFORMATION {
		public uint FileAttributes;
		public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
		public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
		public uint VolumeSerial;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}
}
