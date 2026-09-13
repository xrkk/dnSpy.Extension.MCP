using System;
using System.IO;
using System.Collections.Generic;
using dnSpy.Extension.MCP.Debugger;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditSourceFileObservation {
	public string FinalPath { get; init; } = string.Empty;
	public string PathHash { get; init; } = string.Empty;
	public string Sha256 { get; init; } = string.Empty;
	public string VolumeSerial { get; init; } = string.Empty;
	public string FileId { get; init; } = string.Empty;
}

/// <summary>Handle-derived identity for an on-disk source sample; paths are never identity by themselves.</summary>
internal static class EditSourceFileIdentity {
	public static EditSourceFileObservation? Observe(string? path) {
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
		using var hash = SHA256.Create();
		var sha = string.Concat(hash.ComputeHash(stream).Select(x => x.ToString("x2")));
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
			var full = Path.GetFullPath(path);
			return new EditSourceFileObservation {
				FinalPath = full, PathHash = HashNormalizedPath(full), Sha256 = sha,
				VolumeSerial = "non-windows", FileId = sha.Substring(0, 32),
			};
		}
		if (!GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var info))
			throw new IOException("source file identity query failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		if ((((FileAttributes)info.FileAttributes) & FileAttributes.ReparsePoint) != 0)
			throw new IOException("source file is a reparse point");
		var final = FinalPath(stream.SafeFileHandle);
		return new EditSourceFileObservation {
			FinalPath = final, PathHash = HashNormalizedPath(final), Sha256 = sha,
			VolumeSerial = "0x" + info.VolumeSerialNumber.ToString("x16"),
			FileId = (info.FileIndexHigh.ToString("x8") + info.FileIndexLow.ToString("x8")).PadLeft(32, '0'),
		};
	}

	/// <summary>Read a bounded resource from one leased, non-reparse sample file.
	/// Identity, length and content all come from the same handle; no path reopen.</summary>
	public static (byte[] Bytes, EditSourceFileObservation Identity) ReadAllowedResource(
		string path, string? allowedRoot, int maximumBytes) {
		if (string.IsNullOrWhiteSpace(allowedRoot) || !Path.IsPathRooted(allowedRoot)
			|| !Path.IsPathRooted(path)) throw new IOException("resource path requires an absolute AllowedSampleRoot and VM path");
		var full = Path.GetFullPath(path);
		var root = Path.GetFullPath(allowedRoot);
		if (!WindowsPathRelation.Contains(root, full)) throw new IOException("resource path is outside AllowedSampleRoot");
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			throw new PlatformNotSupportedException("resource path identity requires Windows handles");
		// Reject drive-relative/device/UNC paths and alternate data streams.
		if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':'
			|| (path[2] != '\\' && path[2] != '/') || full.IndexOf(':', 2) >= 0)
			throw new IOException("resource path must identify a local drive file without an alternate stream");
		var leases = new List<SafeFileHandle>();
		try {
			var parents = new Stack<string>();
			for (var parent = Path.GetDirectoryName(full); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
				parents.Push(parent);
			while (parents.Count != 0) {
				var parent = parents.Pop();
				var lease = OpenResourceHandle(parent);
				leases.Add(lease);
				var info = ResourceHandleInfo(lease);
				if (((FileAttributes)info.FileAttributes & FileAttributes.Directory) == 0
					|| !WindowsPathRelation.EqualPath(parent, FinalPath(lease)))
					throw new IOException("resource parent is not the expected directory");
			}
			using var file = OpenResourceHandle(full);
			var fileInfo = ResourceHandleInfo(file);
			if (((FileAttributes)fileInfo.FileAttributes & FileAttributes.Directory) != 0
				|| GetFileType(file.DangerousGetHandle()) != 1)
				throw new IOException("resource path is not a regular disk file");
			var final = FinalPath(file);
			if (!WindowsPathRelation.EqualPath(full, final) || !WindowsPathRelation.Contains(root, final))
				throw new IOException("resource handle resolves outside the expected sample path");
			using var stream = new FileStream(file, FileAccess.Read);
			var length = stream.Length;
			if (length > maximumBytes) throw new EditDomainException("EDIT_CAPACITY_EXCEEDED",
				new Dictionary<string, object?> { ["kind"] = "capacity", ["limit"] = "resource_bytes", ["current"] = length, ["maximum"] = maximumBytes });
			var bytes = new byte[checked((int)length)];
			var offset = 0;
			while (offset < bytes.Length) {
				var count = stream.Read(bytes, offset, bytes.Length - offset);
				if (count == 0) throw new IOException("resource file shortened while reading");
				offset += count;
			}
			if (stream.ReadByte() != -1) throw new IOException("resource file length changed while reading");
			return (bytes, new EditSourceFileObservation {
				FinalPath = final, PathHash = HashNormalizedPath(final), Sha256 = EditWire.Sha256(bytes),
				VolumeSerial = "0x" + fileInfo.VolumeSerialNumber.ToString("x16"),
				FileId = (fileInfo.FileIndexHigh.ToString("x8") + fileInfo.FileIndexLow.ToString("x8")).PadLeft(32, '0'),
			});
		}
		finally { foreach (var lease in leases) lease.Dispose(); }
	}

	static SafeFileHandle OpenResourceHandle(string path) {
		// Read sharing only: no concurrent writer/rename while bytes are observed.
		var raw = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
		if (raw == IntPtr.Zero || raw == new IntPtr(-1))
			throw new IOException("resource handle open failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		return new SafeFileHandle(raw, ownsHandle: true);
	}

	static BY_HANDLE_FILE_INFORMATION ResourceHandleInfo(SafeFileHandle handle) {
		if (!GetFileInformationByHandle(handle.DangerousGetHandle(), out var info))
			throw new IOException("resource identity query failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		if (((FileAttributes)info.FileAttributes & FileAttributes.ReparsePoint) != 0)
			throw new IOException("resource path traverses a reparse point");
		return info;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
	[DllImport("kernel32.dll", SetLastError = true)]
	static extern uint GetFileType(IntPtr file);

	public static string HashNormalizedPath(string path) {
		using var hash = SHA256.Create();
		return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(path)).Select(x => x.ToString("x2")));
	}

	static string FinalPath(SafeFileHandle handle) {
		var buffer = new StringBuilder(32768);
		var count = GetFinalPathNameByHandleW(handle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
		if (count == 0 || count >= buffer.Capacity)
			throw new IOException("source final path query failed", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
		var value = buffer.ToString();
		return value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + value.Substring(8)
			: value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? value.Substring(4) : value;
	}

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

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern uint GetFinalPathNameByHandleW(IntPtr hFile, StringBuilder path, uint pathLength, uint flags);
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION info);
}
