using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class GenV1 {
	static int Main(string[] args) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		var outDir = args.Length > 0 ? args[0] : "/tmp/dnspy-t003-r01-build/packages";
		Directory.CreateDirectory(outDir);
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-t003-r02-gen"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = BuildModule();
		using var workspace = EditWorkspace.CreateForTesting(live);
		var family = "family-" + Guid.NewGuid().ToString("N");
		var binding = new EditHistoryBinding { FamilyId = family };
		var head1 = Commit(history, workspace, live, ref binding, "review-gen-1", Rename("MethodOwnerProbeG1"));
		var head2 = Commit(history, workspace, live, ref binding, "review-gen-2", Rename("MethodOwnerProbeG2"));
		var lineageId = binding.LineageId!;
		var lineage = history.Load(lineageId);
		Console.WriteLine($"V1 package lineage={lineageId} head={head2} format={lineage.Manifest.Format} checkpoints={lineage.Manifest.Checkpoints.Count}");
		var package = store.FinalBytes(lineageId);
		File.WriteAllBytes(Path.Combine(outDir, "v1-fixed-package.zip"), package);
		Console.WriteLine("V1 fixed sha=" + EditWire.Sha256(package));
		// v1 swapped-baseline variant (body swap between T.One and T.Two)
		var swapped = BuildSwappedBaseline(package);
		var swappedId = "lineage-" + Guid.NewGuid().ToString("N");
		var swappedPkg = RewriteBaseline(package, swapped, swappedId);
		File.WriteAllBytes(Path.Combine(outDir, "v1-swapped-baseline.zip"), swappedPkg);
		Console.WriteLine("V1 swapped sha=" + EditWire.Sha256(swappedPkg));
		// v1 writer-image variant of the head node
		byte[] rawWrite;
		using (var buffer = new MemoryStream()) { live.Write(buffer); rawWrite = buffer.ToArray(); }
		var writerId = "lineage-" + Guid.NewGuid().ToString("N");
		var writerPkg = RewriteHeadImage(package, EditWire.Sha256(rawWrite), writerId);
		File.WriteAllBytes(Path.Combine(outDir, "v1-writer-image.zip"), writerPkg);
		Console.WriteLine("V1 writer sha=" + EditWire.Sha256(writerPkg));
		Console.WriteLine("V1 baseline_semantic=" + lineage.Manifest.SourceIdentity.BaselineSemanticFingerprint);
		return 0;
	}

	static string Commit(EditHistoryModule history, EditWorkspace workspace, ModuleDef live, ref EditHistoryBinding binding, string review, params string[] operations) {
		for (var index = 0; index < operations.Length; index++) {
			using var json = JsonDocument.Parse(operations[index]);
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operations[index]);
		}
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, review, 1, Array.Empty<string>());
		var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		foreach (var operation in operations) {
			using var json = JsonDocument.Parse(operation);
			EditOperationRegistry.ApplyPersisted(live, json.RootElement, map, 0);
		}
		history.Finalize(prepared, live);
		workspace.NormalizedOperations.Clear();
		binding = new EditHistoryBinding { FamilyId = binding.FamilyId, LineageId = prepared.Lineage.Manifest.LineageId, BaseCheckpointId = prepared.PostHeadCheckpointId };
		return prepared.PostHeadCheckpointId;
	}

	static string Rename(string name) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["kind"] = "module_update", ["name"] = name }, EditWire.JsonOptions);

	static ModuleDef BuildModule() {
		var module = new ModuleDefUser("MethodOwnerProbeG.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("11111111-2222-3333-4444-555555555555") };
		new AssemblyDefUser("MethodOwnerProbeG", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		Add(t, "One", 1);
		Add(t, "Two", 2);
		return module;
	}

	static void Add(TypeDef type, string name, int value) {
		var module = type.Module;
		var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		method.Body.Instructions.Add(Instruction.Create(value switch { 1 => OpCodes.Ldc_I4_1, 2 => OpCodes.Ldc_I4_2, _ => OpCodes.Ldc_I4_0 }));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
	}

	static MethodDef Find(ModuleDef module, string name) => module.GetTypes().Single(t => t.Name == "T").Methods.Single(m => m.Name == name);

	static byte[] BuildSwappedBaseline(byte[] package) {
		var entries = ReadEntries(package);
		using var module = ModuleDefMD.Load(entries["baseline/module.bin"]);
		var one = Find(module, "One"); var two = Find(module, "Two");
		(one.Body, two.Body) = (two.Body, one.Body);
		return EditWorkspace.WriteCheckpointImage(module);
	}

	static Dictionary<string, byte[]> ReadEntries(byte[] package) {
		using var input = new MemoryStream(package, writable: false);
		using var archive = new ZipArchive(input, ZipArchiveMode.Read);
		return archive.Entries.ToDictionary(x => x.FullName, x => { using var s = x.Open(); using var c = new MemoryStream(); s.CopyTo(c); return c.ToArray(); }, StringComparer.Ordinal);
	}

	static byte[] BuildZip(Dictionary<string, byte[]> entries) {
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
			foreach (var row in entries) { var e = archive.CreateEntry(row.Key, CompressionLevel.Optimal); using var s = e.Open(); s.Write(row.Value, 0, row.Value.Length); }
		return output.ToArray();
	}

	static byte[] RewriteBaseline(byte[] package, byte[] baseline, string lineageId) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		entries["baseline/module.bin"] = baseline;
		manifest.Baseline.Length = baseline.LongLength;
		manifest.Baseline.Sha256 = EditWire.Sha256(baseline);
		manifest.SourceIdentity.BaselineImageSha256 = manifest.Baseline.Sha256;
		manifest.LineageId = lineageId; manifest.FamilyId = "family-" + Guid.NewGuid().ToString("N");
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static byte[] RewriteHeadImage(byte[] package, string imageSha, string lineageId) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		manifest.Checkpoints.Single(x => x.CheckpointId == manifest.HeadCheckpointId).ResultImageSha256 = imageSha;
		manifest.LineageId = lineageId; manifest.FamilyId = "family-" + Guid.NewGuid().ToString("N");
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}
}
