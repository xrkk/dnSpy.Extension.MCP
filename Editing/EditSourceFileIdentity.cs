using System;
using System.IO;
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
