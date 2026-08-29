using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// FB-002 / ACC-024: deterministic unsupported-target detection over the launch input's own
/// bytes (dnlib). Produces the TYPE-DYN-019 evidence chain in fixed order; evidence values
/// over the 1024 UTF-8-byte limit are flagged so the launch answers a small INTERNAL_ERROR
/// instead of an over-limit domain envelope (the values are untrusted sample data).
/// </summary>
public static class UnsupportedTargetDetector {
	public sealed class Result {
		public string DetectedTargetKind = string.Empty;
		public List<(string Kind, string Value)> Evidence = new();
		public string RecommendedWorkflow = string.Empty;
		public bool EvidenceOverLimit;
	}

	const int EvidenceValueMaxBytes = 1024;

	/// <summary>Raw PE read of the COR20 header flags (dnlib does not expose them):
	/// PE offset -> optional-header magic -> data-directory base -> CLR directory RVA ->
	/// section-mapped file offset -> flags at COR20+16.</summary>
	static uint Cor20Flags(string path) {
		try {
			using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
			using var br = new System.IO.BinaryReader(fs);
			fs.Position = 0x3C;
			int peOffset = br.ReadInt32();
			fs.Position = peOffset + 24;
			ushort magic = br.ReadUInt16();
			int dirBase = magic == 0x20B ? 112 : 96;
			fs.Position = peOffset + 24 + dirBase + 14 * 8;
			uint clrRva = br.ReadUInt32();
			uint clrSize = br.ReadUInt32();
			if (clrRva == 0 || clrSize == 0)
				return 0;
			fs.Position = peOffset + 20;
			ushort optSize = br.ReadUInt16();
			fs.Position = peOffset + 6;
			ushort numSections = br.ReadUInt16();
			long sectionsAt = peOffset + 24 + optSize;
			for (int i = 0; i < numSections; i++) {
				fs.Position = sectionsAt + i * 40 + 8;
				uint virtSize = br.ReadUInt32();
				uint virtAddr = br.ReadUInt32();
				uint rawSize = br.ReadUInt32();
				uint rawPtr = br.ReadUInt32();
				uint span = System.Math.Max(virtSize, rawSize);
				if (clrRva >= virtAddr && clrRva < virtAddr + span) {
					fs.Position = rawPtr + (clrRva - virtAddr) + 16;
					return br.ReadUInt32();
				}
			}
			return 0;
		}
		catch {
			return 0;
		}
	}

	static readonly string MonoMarkerPrefix = "UnityEngine";
	static readonly string[] MonoMarkerNames = { "Mono.Posix", "Mono.Security", "mono" };
	static readonly string[] SupportedRuntimeVersions = { "v2.0.50727", "v4.0.30319" };

	public static string WorkflowFor(string kind) => kind switch {
		"unity_mono" => "mono_dynamic_analysis",
		"mixed_mode" or "pure_native" => "pe_x64dbg_ida_dynamic_analysis",
		"unsupported_managed_runtime" => "managed_static_analysis",
		_ => string.Empty,
	};

	public static Result? Detect(string path) {
		ModuleDefMD module;
		try {
			module = ModuleDefMD.Load(path);
		}
		catch {
			// Not loadable as a managed module: a native (or corrupt) PE.
			return new Result {
				DetectedTargetKind = "pure_native",
				RecommendedWorkflow = "pe_x64dbg_ida_dynamic_analysis",
				Evidence = { ("pe_headers", $"PE without a loadable CLR header: {System.IO.Path.GetFileName(path)}") },
			};
		}
		using (module) {
			// ILOnly alone does NOT mean mixed: csc /platform:x64 emits ILOnly=0 for pure IL.
			// 32BITREQUIRED without ILOnly is the classic 32-bit mixed-mode (C++/CLI) shape.
			uint corFlags = Cor20Flags(path);
			if ((corFlags & 0x00000002) != 0 && (corFlags & 0x00000001) == 0) {
				return new Result {
					DetectedTargetKind = "mixed_mode",
					RecommendedWorkflow = "pe_x64dbg_ida_dynamic_analysis",
					Evidence = {
						("pe_headers", $"native entry point with CLR directory present: {System.IO.Path.GetFileName(path)}"),
						("clr_metadata", $"CorFlags=0x{corFlags:x8} (32BITREQUIRED without ILOnly: mixed-mode C++/CLI image)"),
					},
				};
			}
			var monoRefs = module.GetAssemblyRefs()
				.Where(r => r.Name is not null &&
					(r.Name.StartsWith(MonoMarkerPrefix, StringComparison.OrdinalIgnoreCase)
						|| MonoMarkerNames.Contains(r.Name.String, StringComparer.OrdinalIgnoreCase)))
				.Select(r => r.FullName)
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();
			if (monoRefs.Count > 0) {
				var value = "unity/mono references: " + string.Join("; ", monoRefs);
				return new Result {
					DetectedTargetKind = "unity_mono",
					RecommendedWorkflow = "mono_dynamic_analysis",
					Evidence = { ("module_identity", value) },
					EvidenceOverLimit = System.Text.Encoding.UTF8.GetByteCount(value) > EvidenceValueMaxBytes,
				};
			}
			var runtime = module.RuntimeVersion ?? string.Empty;
			if (!SupportedRuntimeVersions.Contains(runtime, StringComparer.Ordinal)) {
				return new Result {
					DetectedTargetKind = "unsupported_managed_runtime",
					RecommendedWorkflow = "managed_static_analysis",
					Evidence = { ("runtime_contract", $"CLR runtime version '{runtime}' is outside the supported netfx/CoreCLR set") },
				};
			}
			return null;
		}
	}
}
