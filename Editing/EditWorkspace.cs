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
	readonly byte[] baselineCheckpointBytes;
	readonly Dictionary<string, BodyHeader> baselineBodyHeaders;
	public string AssemblyName { get; }
	public string ModuleName => LiveModule.Name;
	public string FilePath => LiveModule.Location ?? string.Empty;
	public string FileSha256 { get; }
	public string BaselineLiveFingerprint { get; }
	/// <summary>CHK-003: baseline of the full external-drift guard (semantic
	/// channels plus entry point/AssemblyRef/Win32/CDI rows), captured with the
	/// same function as <see cref="CurrentExternalGuard"/>.</summary>
	public string BaselineExternalGuard { get; }
	public string BaselineSemanticFingerprint { get; }
	public string ModuleMvid => (LiveModule.Mvid?.ToString("D") ?? string.Empty).ToLowerInvariant();
	public byte[] BaselineBytes => (byte[])baselineCheckpointBytes.Clone();
	public string BaselineImageSha256 { get; }
	public Dictionary<string, IMDTokenProvider> ObjectIds { get; } = new(StringComparer.Ordinal);
	public List<string> NormalizedOperations { get; } = new();
	public List<Dictionary<string, object?>> Diffs { get; } = new();
	public List<Dictionary<string, object?>> Risks { get; } = new();

	EditWorkspace(ModuleDef live, ModuleDefMD privateModule, byte[] baselinePrivateBytes, string assemblyName, string fileSha256, string baseline) {
		LiveModule = live;
		PrivateModule = privateModule;
		this.baselinePrivateBytes = baselinePrivateBytes;
		baselineCheckpointBytes = WriteCheckpointImage(privateModule);
		baselineBodyHeaders = CaptureBodyHeaders(privateModule);
		AssemblyName = assemblyName;
		FileSha256 = fileSha256;
		BaselineLiveFingerprint = baseline;
		BaselineExternalGuard = EditFingerprint.ComputeExternalGuard(live);
		BaselineSemanticFingerprint = EditFingerprint.ComputeRoundtrip(privateModule);
		BaselineImageSha256 = EditWire.Sha256(baselineCheckpointBytes);
	}

	public static EditWorkspace Create(IDocumentTreeView tree, string assemblyName, string? requestedMvid) {
		// dnSpy hydrates a newly opened document's ModuleDef asynchronously, so
		// an edit_begin racing open_files may briefly see zero candidates.  Each
		// attempt enumerates on the dispatcher; the retry sleep runs on the
		// caller thread so hydration can proceed on the UI thread.
		ModuleDef? live = null;
		for (var attempt = 0; live == null && attempt < 9; attempt++) {
			if (attempt != 0) System.Threading.Thread.Sleep(250);
			live = OnDispatcher(() => {
				var modules = tree.GetAllModuleNodes().Select(n => n.Document?.ModuleDef).Where(m => m != null)
					.Cast<ModuleDef>().Where(m => string.Equals(m.Assembly?.Name, assemblyName, StringComparison.OrdinalIgnoreCase)).ToList();
				if (requestedMvid != null) modules = modules.Where(m => string.Equals(m.Mvid?.ToString("D"), requestedMvid, StringComparison.OrdinalIgnoreCase)).ToList();
				return modules.Count == 1 ? modules[0] : null;
			});
		}
		if (live == null) throw Capability("target_ambiguous_or_not_found", "Exactly one loaded module must match assembly_name and module_mvid");
		return OnDispatcher(() => CreateFromLive(live, assemblyName));
	}

	static EditWorkspace CreateFromLive(ModuleDef live, string assemblyName) {
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
		RestoreBodyHeaders(privateModule, CaptureBodyHeaders(live));
		var baseline = EditFingerprint.Compute(live);
		if (!string.Equals(baseline, EditFingerprint.Compute(privateModule), StringComparison.Ordinal)) {
			var difference=EditFingerprint.Difference(live,privateModule);
			privateModule.Dispose();
			throw Capability("roundtrip_fingerprint", "Private copy does not preserve the canonical module image: "+difference);
		}
		var path = live.Location;
		var fileSha = File.Exists(path) ? HashFile(path) : EditWire.Sha256(bytes);
		return new EditWorkspace(live, privateModule, bytes, assemblyName, fileSha, baseline);
	}

	internal static EditWorkspace CreateForTesting(ModuleDef live) {
		if (!string.Equals(Environment.GetEnvironmentVariable("DNMCP_TEST"), "1", StringComparison.Ordinal))
			throw new InvalidOperationException("DNMCP_TEST=1 is required");
		if (live.Assembly == null) throw Capability("netmodule", "NetModule targets are not supported");
		var bytes = Write(live);
		var privateModule = ModuleDefMD.Load(bytes);
		RestoreBodyHeaders(privateModule, CaptureBodyHeaders(live));
		var baseline = EditFingerprint.Compute(live);
		if (baseline != EditFingerprint.Compute(privateModule)) {
			privateModule.Dispose();
			throw Capability("roundtrip_fingerprint", "Test private copy does not preserve the canonical module image");
		}
		var fileSha = File.Exists(live.Location) ? HashFile(live.Location) : EditWire.Sha256(bytes);
		return new EditWorkspace(live, privateModule, bytes, live.Assembly.Name.String, fileSha, baseline);
	}

	public static byte[] Write(ModuleDef module) => WriteCore(module, MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack);
	public static byte[] WriteCanonical(ModuleDef module) => WriteCore(module, MetadataFlags.KeepOldMaxStack);

	internal static byte[] WriteCheckpointImage(ModuleDef module) {
		// Preserve row/token identity and opaque signature suffixes, but rebuild
		// heap offsets: PreserveAll copies the original heaps, making the output
		// depend on whether a ModuleDefMD came from source or an emitted baseline.
		// This is the actual checkpoint/export image; no hash regions are ignored.
		var semantic = EditFingerprint.ComputeRoundtrip(module);
		var originalNames = new HashSet<string>(module.GetTypes().Select(t => t.FullName), StringComparer.Ordinal);
		const MetadataFlags flags = MetadataFlags.PreserveRids | MetadataFlags.PreserveExtraSignatureData | MetadataFlags.KeepOldMaxStack;
		var bytes = WriteCore(module, flags);
		using var materialized = ModuleDefMD.Load(bytes);
		if (EditFingerprint.ComputeRoundtrip(materialized) != semantic)
			throw new EditDomainException("EDIT_VALIDATION_FAILED");
		// dnlib creates random GUID names for deleted-row placeholders. Only
		// placeholders newly emitted by this write are ours to name; even a sample
		// type matching the dummy naming pattern remains untouched. Rewriting the
		// detached image rebuilds its heaps without retaining the random strings.
		foreach (var type in materialized.GetTypes().Where(t => EditFingerprint.IsWriterTombstoneType(t) && !originalNames.Contains(t.FullName))) {
			var attempt = 0;
			string name;
			do {
				name = new Guid((int)type.Rid, (short)(attempt >> 16), (short)attempt, new byte[8]).ToString("B");
				attempt++;
			} while (originalNames.Contains(type.Namespace + "." + name));
			type.Name = name;
			originalNames.Add(type.FullName);
		}
		return WriteCore(materialized, flags);
	}

	// Tiny method headers cannot encode InitLocals/MaxStack. Preserve those live
	// object properties in the private copy, rather than weakening the complete
	// live/private conflict fingerprint when dnlib normalizes their disk encoding.
	readonly struct BodyHeader {
		public readonly bool InitLocals;
		public readonly ushort MaxStack;
		public BodyHeader(bool initLocals, ushort maxStack) { InitLocals = initLocals; MaxStack = maxStack; }
	}
	// These are private-copy positions, not persisted identities. Newly added
	// methods all have RID zero until emission; using MDToken as a dictionary key
	// both collides and cannot address their emitted copies. Hierarchical slots
	// survive this unedited roundtrip, and the complete fingerprint below remains
	// the final guard against any unsupported writer reordering or data loss.
	static Dictionary<string, MethodDef> MethodSlots(ModuleDef module) {
		var slots = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
		void AddTypes(IList<TypeDef> types, string prefix) {
			for (var i = 0; i < types.Count; i++) {
				var type = types[i];
				var path = prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
				for (var j = 0; j < type.Methods.Count; j++)
					slots.Add(path + "/m/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture), type.Methods[j]);
				AddTypes(type.NestedTypes, path + "/t/");
			}
		}
		AddTypes(module.Types, "t/");
		return slots;
	}
	static Dictionary<string, BodyHeader> CaptureBodyHeaders(ModuleDef module) => MethodSlots(module)
		.Where(pair => pair.Value.HasBody)
		.ToDictionary(pair => pair.Key, pair => new BodyHeader(pair.Value.Body.InitLocals, pair.Value.Body.MaxStack), StringComparer.Ordinal);
	static void RestoreBodyHeaders(ModuleDefMD module, Dictionary<string, BodyHeader> headers) {
		var slots = MethodSlots(module);
		foreach (var pair in headers) {
			if (!slots.TryGetValue(pair.Key, out var method) || !method.HasBody)
				throw Capability("roundtrip_fingerprint", "Private copy did not preserve the method body identity");
			method.Body.InitLocals = pair.Value.InitLocals;
			method.Body.MaxStack = pair.Value.MaxStack;
		}
	}

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
	/// <summary>CHK-003 / CON-004: full-coverage live-drift guard — detects UI
	/// edits to entry point, AssemblyRef, native Win32 resources and custom
	/// debug information that the frozen semantic fingerprint does not carry.
	/// Compare only against <see cref="BaselineExternalGuard"/>.</summary>
	public string CurrentExternalGuard() => OnDispatcher(() => EditFingerprint.ComputeExternalGuard(LiveModule));
	public string CurrentLiveSemanticFingerprint() => OnDispatcher(() => EditFingerprint.ComputeRoundtrip(LiveModule));
	/// <summary>T003: version-matched live semantic digest.  v2 lineages compare
	/// against the owner-bound strong projection; v1 lineages keep the historical
	/// algorithm and never mix the two.</summary>
	public string CurrentLiveSemanticFingerprintFor(string format) =>
		OnDispatcher(() => EditHistoryModule.SemanticDigest(format, LiveModule));
	string? baselineSemanticV2;
	public string BaselineSemanticFingerprintFor(string format) {
		if (EditHistoryModule.IsV2(format))
			return baselineSemanticV2 ??= EditHistoryModule.BaselineSemanticDigest(format, BaselineBytes);
		if (EditHistoryModule.IsKnownFormat(format)) return BaselineSemanticFingerprint;
		throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
	}
	public string CurrentLiveImageSha256() => OnDispatcher(() => EditWire.Sha256(WriteCheckpointImage(LiveModule)));
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
			RestoreBodyHeaders(rebuilt, baselineBodyHeaders);
			for (int index = 0; index < NormalizedOperations.Count; index++) {
				using var document = System.Text.Json.JsonDocument.Parse(NormalizedOperations[index]);
				EditOperationRegistry.ApplyPersisted(rebuilt, document.RootElement, rebuiltIds, index);
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
	// The WPF touch lives in a NoInlining helper: JIT of this wrapper never
	// loads WindowsBase, so a headless host without WPF (the Linux store-level
	// verification probe) falls back to inline execution instead of failing
	// assembly resolution.  Real dnSpy hosts dispatch unchanged.
	public static T OnDispatcher<T>(Func<T> action) {
		try { return Dispatched(action); }
		catch (System.IO.FileNotFoundException ex) when (ex.FileName?.StartsWith("WindowsBase", StringComparison.Ordinal) == true) { return action(); }
		catch (TypeLoadException) { return action(); }
	}
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	static T Dispatched<T>(Func<T> action) {
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
