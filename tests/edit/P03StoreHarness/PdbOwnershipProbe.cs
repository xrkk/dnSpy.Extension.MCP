using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class PdbOwnershipProbe {
	public static void Run(string fixture) {
		NoParentPdb(fixture);
		ParentAndSharedDocument(fixture);
		Exception? checksumFailure = null;
		try { SameNameDifferentChecksum(fixture); }
		catch (Exception ex) { checksumFailure = ex; Console.WriteLine("PDB_CASE checksum RED " + ex.Message); }
		NavigationCompensation(fixture);
		if (checksumFailure != null) throw checksumFailure;
		Console.WriteLine("PASS pdb-ownership no-parent+parent-shared+three-users+same-name-different-checksum+late-navigation-compensation");
	}

	public static void RunCompatibility(string fixture) {
		var packagePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixture))!, "a5-checkpoints.zip");
		var package = File.ReadAllBytes(packagePath);
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetDirectoryName(packagePath)!, "compat-store"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		var validated = history.ValidatePackageForTesting(package);
		var temp = store.CreateTemp(validated.Manifest.LineageId, package);
		store.FinalizeTemp(temp, replaceExisting: false);
		var lineage = history.Load(validated.Manifest.LineageId);
		var root = lineage.Manifest.Checkpoints.Single(node => node.ParentCheckpointId == null).CheckpointId;
		var resource = lineage.Manifest.Checkpoints.Single(node => node.CheckpointId == "checkpoint-f42fcc7a093ae964b1c7bce9f0df2d9b").CheckpointId;
		var method = lineage.Manifest.Checkpoints.Single(node => node.CheckpointId == "checkpoint-66fbfb78816b8ebc971a5f3b029c14fe").CheckpointId;
		var originalSha = EditWire.Sha256(package);
		var source = history.Assess(lineage.Manifest.LineageId, resource, string.Empty);
		using (var live = ModuleDefMD.Load(source.Bytes)) {
			var before = EditFingerprint.Compute(live);
			history.PlanNavigation(lineage, resource, root).Apply(live);
			Check(EditHistoryModule.SemanticDigest(lineage.Manifest.Format, live) == lineage.Checkpoint(root).ResultSemanticFingerprint,
				"old package resource-only inverse restores baseline");
			Console.WriteLine("PDB_COMPAT resource-only old package before=" + before + " after=" + EditFingerprint.Compute(live));
		}
		var oldMethod = history.Assess(lineage.Manifest.LineageId, method, string.Empty);
		Console.WriteLine("PDB_COMPAT method classification=" + oldMethod.Classification + " image=" + oldMethod.ImageSha256
			+ " expected=" + lineage.Checkpoint(method).ResultImageSha256 + " semantic=" + oldMethod.SemanticFingerprint
			+ " expected_semantic=" + lineage.Checkpoint(method).ResultSemanticFingerprint);
		try { history.PlanNavigation(lineage, method, root); throw new InvalidOperationException("old method package unexpectedly undid"); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
		Check(EditWire.Sha256(store.FinalBytes(lineage.Manifest.LineageId)) == originalSha,
			"old package remained byte-for-byte unchanged");
		Console.WriteLine("PASS pdb-compat old-package-sha=" + originalSha + " resource-only=undo-ok method-with-PDB=EDIT_HISTORY_CONFLICT unchanged");
	}

	static ModuleDefMD Open(string fixture) => ModuleDefMD.Load(Path.GetFullPath(fixture));
	static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAILED: " + name); }

	static string Operation(ModuleDef module, string name, string documentName, string checksum) {
		var owner = module.GetTypes().Single(type => type.FullName == "TestIL.Members");
		return JsonSerializer.Serialize(new {
			kind = "method_add", owner_type = new { token = "0x" + owner.MDToken.Raw.ToString("x8") },
			name, attributes = 150,
			signature = new { return_type = "System.Void", has_this = false, parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() },
			body = new {
				init_locals = false, max_stack = 1, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
				instructions = new[] { new { opcode = "ret" } },
				sequence_points = new[] { new {
					document = new { name = documentName, language = "3f5162f8-07c6-11d3-9053-00c04fa302a1",
						vendor = "994b45c4-e6e9-11d2-903f-00c04fa302a1", hash = checksum,
						type = "5a869d0b-6611-11d3-bd2a-0000f80849bd", hashAlgorithm = "ff1816ec-aa5e-4d10-87f7-6f4963833460" },
					start = new { il = 0, line = 1, column = 1 }, end = new { line = 1, column = 2 },
				} },
			},
		});
	}

	static (string Inverse, string CreatedId) Add(ModuleDef module, string name, string documentName, string checksum) {
		using var forward = JsonDocument.Parse(Operation(module, name, documentName, checksum));
		var map = new Dictionary<string, IMDTokenProvider>();
		var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
		var applied = EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
		return (JsonSerializer.Serialize(inverse), applied.CreatedObjectIds.Single());
	}

	static Action Undo(ModuleDef module, string inverse) {
		using var state = JsonDocument.Parse(inverse);
		return EditOperationRegistry.ApplyCompiledInverse(module, state.RootElement,
			new Dictionary<string, IMDTokenProvider>(), 0).Undo;
	}

	static string[] Documents(ModuleDef module) => module.PdbState?.Documents
		.Select(document => document.Url + ":" + Convert.ToBase64String(document.CheckSum ?? Array.Empty<byte>()) + ":" +
			module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody)
				.SelectMany(method => method.Body.Instructions)
				.Count(instruction => ReferenceEquals(instruction.SequencePoint?.Document, document)))
		.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();

	static void NoParentPdb(string fixture) {
		using var module = Open(fixture);
		Check(module.PdbState == null, "no-parent initial PDB absent");
		var baseline = EditFingerprint.Compute(module);
		var (inverse, _) = Add(module, "R02NoParent", "R02NoParent.cs", "AQID");
		var after = EditFingerprint.Compute(module);
		Check(Documents(module).SequenceEqual(new[] { "R02NoParent.cs:AQID:1" }), "no-parent forward document and reference");
		var compensate = Undo(module, inverse);
		Check(Documents(module).Length == 0, "no-parent undo releases document");
		EditOperationRegistry.RemoveEmptyPdbState(module);
		Check(module.PdbState == null && EditFingerprint.Compute(module) == baseline, "no-parent undo exact baseline");
		compensate();
		Check(EditFingerprint.Compute(module) == after && Documents(module).SequenceEqual(new[] { "R02NoParent.cs:AQID:1" }),
			"no-parent compensation exact forward");
		Console.WriteLine("PDB_CASE no-parent before=" + baseline + " after=" + after + " docs=" + string.Join(",", Documents(module)));
	}

	static void ParentAndSharedDocument(string fixture) {
		using var module = Open(fixture);
		Add(module, "R02First", "R02Shared.cs", "AQID");
		var parent = EditFingerprint.Compute(module);
		var (secondInverse, _) = Add(module, "R02Second", "R02Shared.cs", "AQID");
		Check(Documents(module).SequenceEqual(new[] { "R02Shared.cs:AQID:2" }), "shared second method reuses parent document");
		using (var state = JsonDocument.Parse(secondInverse))
			Check(!state.RootElement.GetProperty("definition_tail_remove").TryGetProperty("release_documents", out _),
				"shared parent document is not claimed by second inverse");
		var (thirdInverse, _) = Add(module, "R02Third", "R02Shared.cs", "AQID");
		Check(Documents(module).SequenceEqual(new[] { "R02Shared.cs:AQID:3" }), "three methods share one document");
		Undo(module, thirdInverse);
		Check(Documents(module).SequenceEqual(new[] { "R02Shared.cs:AQID:2" }), "undo one of three retains shared document");
		Undo(module, secondInverse);
		Check(EditFingerprint.Compute(module) == parent && Documents(module).SequenceEqual(new[] { "R02Shared.cs:AQID:1" }),
			"undo second restores parent and its document");
		Console.WriteLine("PDB_CASE shared parent=" + parent + " docs=" + string.Join(",", Documents(module)));
	}

	static void SameNameDifferentChecksum(string fixture) {
		using var module = Open(fixture);
		var empty = EditFingerprint.Compute(module);
		var doublePoint = JsonNode.Parse(Operation(module, "R02ChecksumPair", "R02Pair.cs", "AQID"))!.AsObject();
		var points = doublePoint["body"]!["sequence_points"]!.AsArray();
		var secondPoint = points[0]!.DeepClone();
		secondPoint["document"]!["hash"] = "BAUG";
		points.Add(secondPoint);
		using (var paired = JsonDocument.Parse(doublePoint.ToJsonString())) {
			try {
				EditOperationRegistry.CompileInverse(module, paired.RootElement, new Dictionary<string, IMDTokenProvider>());
				throw new InvalidOperationException("same-operation checksum collision was silently accepted");
			}
			catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
		}
		Check(module.PdbState == null && EditFingerprint.Compute(module) == empty,
			"same-operation checksum collision rejects before mutation");
		Add(module, "R02ChecksumA", "R02Same.cs", "AQID");
		var parent = EditFingerprint.Compute(module);
		using var forward = JsonDocument.Parse(Operation(module, "R02ChecksumB", "R02Same.cs", "BAUG"));
		try {
			EditOperationRegistry.CompileInverse(module, forward.RootElement, new Dictionary<string, IMDTokenProvider>());
			throw new InvalidOperationException("same-name different-checksum document was silently accepted");
		}
		catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
		Check(EditFingerprint.Compute(module) == parent && Documents(module).SequenceEqual(new[] { "R02Same.cs:AQID:1" }),
			"same-name checksum collision rejects before mutation and retains parent document");
		Console.WriteLine("PDB_CASE checksum-isolated-by-rejection parent=" + parent + " docs=" + string.Join(",", Documents(module)));
	}

	static void NavigationCompensation(string fixture) {
		using var module = Open(fixture);
		var (inverse, _) = Add(module, "R02Navigate", "R02Navigate.cs", "AQID");
		var before = EditFingerprint.Compute(module);
		var semantic = EditHistoryModule.SemanticDigest(EditHistoryModule.PackageFormatV2, module);
		var plan = new EditHistoryNavigationPlan(EditHistoryModule.PackageFormatV2,
			new[] { new EditHistoryNavigationPlan.Step { CheckpointId = "checkpoint-r02", Index = 0, IsInverse = true, Operation = inverse } },
			semantic, "deliberate-late-semantic-failure", targetHasPdb: false);
		try { plan.Apply(module); throw new InvalidOperationException("late navigation failure was not observed"); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
		Check(EditFingerprint.Compute(module) == before && Documents(module).SequenceEqual(new[] { "R02Navigate.cs:AQID:1" })
			&& module.GetTypes().SelectMany(type => type.Methods).Any(method => method.Name == "R02Navigate"),
			"late navigation failure compensates method, document, and fingerprint");
		Console.WriteLine("PDB_CASE late-navigation-compensation before=" + before + " docs=" + string.Join(",", Documents(module)));
	}
}
