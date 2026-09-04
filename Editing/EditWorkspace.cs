using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using dnSpy.Contracts.Documents.TreeView;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditWorkspace : IDisposable {
	public ModuleDef LiveModule { get; }
	public ModuleDefMD PrivateModule { get; private set; }
	readonly byte[] baselinePrivateBytes;
	public string AssemblyName { get; }
	public string ModuleName => LiveModule.Name;
	public string FilePath => LiveModule.Location ?? string.Empty;
	public string FileSha256 { get; }
	public string BaselineLiveFingerprint { get; }
	public Dictionary<string, IMDTokenProvider> ObjectIds { get; } = new(StringComparer.Ordinal);
	public List<string> NormalizedOperations { get; } = new();
	public List<Dictionary<string, object?>> Diffs { get; } = new();
	public List<Dictionary<string, object?>> Risks { get; } = new();

	EditWorkspace(ModuleDef live, ModuleDefMD privateModule, byte[] baselinePrivateBytes, string assemblyName, string fileSha256, string baseline) {
		LiveModule = live;
		PrivateModule = privateModule;
		this.baselinePrivateBytes = baselinePrivateBytes;
		AssemblyName = assemblyName;
		FileSha256 = fileSha256;
		BaselineLiveFingerprint = baseline;
	}

	public static EditWorkspace Create(IDocumentTreeView tree, string assemblyName, string? requestedMvid) => OnDispatcher(() => {
		var modules = tree.GetAllModuleNodes().Select(n => n.Document?.ModuleDef).Where(m => m != null)
			.Cast<ModuleDef>().Where(m => string.Equals(m.Assembly?.Name, assemblyName, StringComparison.OrdinalIgnoreCase)).ToList();
			if (requestedMvid != null) modules = modules.Where(m => string.Equals(m.Mvid?.ToString("D"), requestedMvid, StringComparison.OrdinalIgnoreCase)).ToList();
		if (modules.Count != 1) throw Capability("target_ambiguous_or_not_found", "Exactly one loaded module must match assembly_name and module_mvid");
		var live = modules[0];
		if (live.Assembly == null) throw Capability("netmodule", "NetModule targets are not supported");
		if (live.Assembly.Modules.Count != 1) throw Capability("multi_file", "Multi-file assemblies are not supported");
		if (live is ModuleDefMD md && !md.IsILOnly) throw Capability("mixed_mode", "Mixed-mode modules are not supported");
		var bytes = Write(live);
		if (bytes.Length > EditWire.MaxModuleBytes) throw Capability("module_size", "Module exceeds max_module_bytes");
		var rows = live.GetTypes().Sum(t => 1L + t.Fields.Count + t.Methods.Count + t.Properties.Count + t.Events.Count + t.ParamDefs().Count());
		if (rows > EditWire.MaxMetadataRows) throw Capability("metadata_rows", "Module exceeds max_metadata_rows");
		var il = live.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).Sum(m => (long)m.Body.Instructions.Count);
		if (il > EditWire.MaxIlInstructions) throw Capability("il_instructions", "Module exceeds max_il_instructions");
		var resourceBytes = live.Resources.OfType<EmbeddedResource>().Sum(r => (long)r.CreateReader().Length);
		if (resourceBytes > EditWire.MaxResourceBytes) throw Capability("resource_bytes", "Module exceeds max_resource_bytes");
		var privateModule = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		var baseline = EditFingerprint.Compute(live);
		if (!string.Equals(baseline, EditFingerprint.Compute(privateModule), StringComparison.Ordinal)) {
			var difference=EditFingerprint.Difference(live,privateModule);
			privateModule.Dispose();
			throw Capability("roundtrip_fingerprint", "Private copy does not preserve the canonical module image: "+difference);
		}
		var path = live.Location;
		var fileSha = File.Exists(path) ? HashFile(path) : EditWire.Sha256(bytes);
		return new EditWorkspace(live, privateModule, bytes, assemblyName, fileSha, baseline);
	});

	public static byte[] Write(ModuleDef module) => WriteCore(module, MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack);
	public static byte[] WriteCanonical(ModuleDef module) => WriteCore(module, MetadataFlags.KeepOldMaxStack);

	static byte[] WriteCore(ModuleDef module, MetadataFlags metadataFlags) {
		using var stream = new MemoryStream();
		var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
		options.MetadataOptions.Flags = metadataFlags;
		var originalTopLevelTypes = new HashSet<TypeDef>(module.Types);
		var pdbState = module.PdbState;
		var oldPdbKind = pdbState?.PdbFileKind;
		try {
			if (pdbState != null) {
				pdbState.PdbFileKind = PdbFileKind.EmbeddedPortablePDB;
				options.WritePdb = true;
				options.PdbFileName = "embedded.pdb";
			}
			module.Write(stream, options);
		}
		finally {
			if (pdbState != null && oldPdbKind.HasValue) pdbState.PdbFileKind = oldPdbKind.Value;
			// dnlib's PreserveTokensMetadata materializes dummy.{GUID} and
			// dummy_ptr.{GUID} rows in the supplied ModuleDef. They belong to the
			// emitted image only; retaining them in dnSpy's live graph makes a
			// read-only private-copy operation mutate later transaction baselines.
			for(var i=module.Types.Count-1;i>=0;i--){var type=module.Types[i];if(!originalTopLevelTypes.Contains(type)&&EditFingerprint.IsWriterTombstoneType(type))module.Types.RemoveAt(i);}
		}
		return stream.ToArray();
	}

	public string CurrentLiveFingerprint() => OnDispatcher(() => EditFingerprint.Compute(LiveModule));
	public string PrivateFingerprint() => EditFingerprint.Compute(PrivateModule);
	public byte[] ValidateRoundtrip() {
		var bytes = Write(PrivateModule);
		using var reloaded = ModuleDefMD.Load(bytes);
		var expected = EditFingerprint.ComputeRoundtrip(PrivateModule);
		var actual = EditFingerprint.ComputeRoundtrip(reloaded);
		if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new EditDomainException("EDIT_VALIDATION_FAILED", ValidationDetails("module_roundtrip", "roundtrip", EditFingerprint.Difference(PrivateModule, reloaded)));
		return bytes;
	}
	public byte[] SnapshotPrivate() => Write(PrivateModule);
	public void RestorePrivate(byte[] bytes) {
		var previous = PrivateModule;
		PrivateModule = ModuleDefMD.Load(bytes);
		previous.Dispose();
	}

	/// <summary>Rebuild the private graph and its transaction object map from committed operations.</summary>
	public void RestoreCommittedState() {
		var previous = PrivateModule;
		var rebuilt = ModuleDefMD.Load(baselinePrivateBytes);
		var rebuiltIds = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		try {
			for (int index = 0; index < NormalizedOperations.Count; index++) {
				using var document = System.Text.Json.JsonDocument.Parse(NormalizedOperations[index]);
				EditOperationRegistry.Apply(rebuilt, document.RootElement, rebuiltIds, index);
			}
			EditStructuralValidator.Validate(rebuilt);
		}
		catch {
			rebuilt.Dispose();
			throw;
		}
		PrivateModule = rebuilt;
		ObjectIds.Clear();
		foreach (var pair in rebuiltIds) ObjectIds.Add(pair.Key, pair.Value);
		previous.Dispose();
	}

	public T OnLive<T>(Func<T> action) => OnDispatcher(action);
	public static T OnDispatcher<T>(Func<T> action) {
		var dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess()) return action();
		return dispatcher.Invoke(action);
	}

	public static object ValidationDetails(string ruleId, string location, string message, string? target = null) => new Dictionary<string, object?> {
		["kind"] = "validation",
		["errors"] = new[] { new Dictionary<string, object?> {
			["rule_id"] = Clip(ruleId, 48), ["object"] = Clip(target ?? location, 48),
			["location"] = Clip(location, 48), ["message"] = Clip(message, 192),
		} },
	};
	public static object ValidationDetails(string location, string message) =>
		ValidationDetails("typesig", location, message);
	static string Clip(string value, int maximum) {
		if (string.IsNullOrEmpty(value)) return "unknown";
		return value.Length <= maximum ? value : value.Substring(0, maximum);
	}

	static EditDomainException Capability(string capability, string reason) => new("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
		["kind"] = "capability", ["capability"] = capability, ["reason"] = reason,
	});
	static string HashFile(string path) { using var s = File.OpenRead(path); using var h = SHA256.Create(); return string.Concat(h.ComputeHash(s).Select(b => b.ToString("x2"))); }
	public void Dispose() => PrivateModule.Dispose();
}

internal static class DnlibEditExtensions {
	public static IEnumerable<ParamDef> ParamDefs(this TypeDef type) => type.Methods.SelectMany(m => m.ParamDefs);
}
