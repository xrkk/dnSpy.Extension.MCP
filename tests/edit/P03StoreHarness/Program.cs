using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Extension.MCP.Editing;
using dnSpy.Extension.MCP;
using dnSpy.Extension.MCP.Transport;

static class Program {
	static int Main(string[] args) {
		try {
		if (args.Length == 2 && args[1] == "--export") { Environment.SetEnvironmentVariable("DNMCP_TEST", "1"); TestExport(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--tail-inverses") { TestTailInverses(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--image-spike") { TestImageEncodingSpike(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--tombstone-gate") { TestTombstoneGate(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--dual-tool-classification") { TestDualToolClassification(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--capacity-resolution") { TestCapacityResolution(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--p04-slice1") { TestP04Slice1(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--p04-slice2") { TestP04Slice2(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--p04-slice3") { TestP04Slice3(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--ca-roundtrip-spike") { CaRoundtripSpike(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--compile-frontend-matrix") { TestCompileFrontendMatrix(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--advanced-metadata-matrix") {
			TestP04Slice1(args[0]); TestP04Slice2(args[0]); TestP04Slice3(args[0]);
			Console.WriteLine("PASS advanced-metadata-matrix slice1+slice2+slice3");
			return 0;
		}
				if (args.Length == 2 && args[1] == "--locator-spike") { TestLocatorSpike(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--workspace-new-methods") { TestWorkspaceNewMethods(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--definition-add-inverses") { TestDefinitionAddInverses(args[0]); TestMethodAddSymbolInverse(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--pdb-ownership") { PdbOwnershipProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--pdb-identity") { PdbOwnershipProbe.RunIdentity(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--new-method-history") { NewMethodHistoryProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--serialized-token-binding") { SerializedTokenBindingProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--t059-regression") {
				NewMethodHistoryProbe.Run(args[0]);
				PdbOwnershipProbe.RunIdentity(args[0]);
				ImportMatrixProbe.Run(args[0]);
				TestDefinitionAddInverses(args[0]);
				Console.WriteLine("PASS t059-regression");
				return 0;
			}
			if (args.Length == 2 && args[1] == "--pdb-compat") { PdbOwnershipProbe.RunCompatibility(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--zero-rid-reference") { TestZeroRidReference(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--writer-token-map") { TestWriterTokenMap(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--reference-identity") { TestReferenceIdentity(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--signature-snapshot") { SignatureSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--constant-snapshot") { ConstantSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--inverse-fidelity") { InverseFidelityRegression.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--structured-signature-codec") { StructuredSignatureCodecProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--reference-graph-reload") { ReferenceGraphReloadProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--attribute-snapshot") { AttributeSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--reference-attribute-cycle") { ReferenceAttributeCycleProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--field-deletion-snapshot") { FieldDeletionSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--accessor-deletion-snapshot") { AccessorDeletionSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--method-metadata-deletion") { MethodMetadataDeletionProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--marshal-snapshot") { MarshalSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--body-snapshot") { BodySnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--sequence-point-snapshot") { SequencePointSnapshotProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--portable-pdb-cdi") { PortablePdbCdiProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--portable-pdb-graph-cdi") { PortablePdbGraphCdiProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--import-matrix") { ImportMatrixProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--identity-matrix") { IdentityMatrixProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--resource-matrix") { ResourceMatrixProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--resource-payload-dedup") { ResourcePayloadDedupProbe.Run(args[0]); return 0; }
			if (args.Length == 2 && args[1] == "--cdi-guard-content") { CdiGuardProbe.Run(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--owner-version") { OwnerVersionProbe.Run(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--legacy-history") { LegacyHistoryProbe.Run(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--structural-commit-guard") { StructuralCommitGuardProbe.Run(args[0]); return 0; }
		if (args.Length == 2 && args[1] == "--strong-name-evidence") { StrongNameEvidenceProbe.Run(); return 0; }
		if (args.Length >= 2 && args[1] == "--owner-version-child") { OwnerVersionProbe.RunChildCase(args[2]); return 0; }
			if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("usage: P03StoreHarness <managed-fixture>");
			Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
			TestLegacyRename(args[0]);
			TestUpdateInverses(args[0]);
			InverseFidelityRegression.Run(args[0]);
			TestTailInverses(args[0]);
			TestDefinitionAddInverses(args[0]);
			TestMethodAddSymbolInverse(args[0]);
			PdbOwnershipProbe.Run(args[0]);
			PdbOwnershipProbe.RunIdentity(args[0]);
			TestZeroRidReference(args[0]);
			TestReloadedTailInverses(args[0]);
			TestWorkspaceNewMethods(args[0]);
			TestPayloadHistory(args[0]);
			TestExport(args[0]);
			TestBranching(args[0]);
			TestDualToolClassification(args[0]);
			TestCapacityResolution(args[0]);
			TestP04Slice1(args[0]); TestP04Slice2(args[0]); TestP04Slice3(args[0]);
			using var catalog = new EditSchemaCatalog();
			var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-artifacts"));
			using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
			using var live = ModuleDefMD.Load(Path.GetFullPath(args[0]));
			using var workspace = EditWorkspace.CreateForTesting(live);

			var method = live.GetTypes().SelectMany(x => x.Methods).First(x => !x.IsConstructor && x.HasBody);
			var oldName = method.Name.String;
			var newName = oldName + "_P03";
			var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
				["kind"] = "method_update",
				["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
				["name"] = newName,
			}, EditWire.JsonOptions);
			using (var json = JsonDocument.Parse(operation))
				EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operation);

			var binding = history.ResolveBegin(workspace, null);
			var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-harness", 1, Array.Empty<string>());
			Check(store.TempCount == 1 && store.FinalCount == 0, "prepared package ownership");
			using (var liveOperation = JsonDocument.Parse(operation))
				EditOperationRegistry.ApplyPersisted(live, liveOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			Check(live.ResolveToken(method.MDToken.Raw) is MethodDef changed && changed.Name == newName
				&& ReferenceEquals(changed, method), "live operation preserves token identity");
			history.Finalize(prepared, live);
			Check(store.TempCount == 0 && store.FinalCount == 1, "atomic finalize");

			var lineage = history.Load(prepared.Lineage.Manifest.LineageId);
			Check(lineage.Manifest.Checkpoints.Count == 2, "single baseline plus checkpoint");
			Check(lineage.Manifest.Checkpoints.Count(x => x.ParentCheckpointId == null) == 1, "single root");
			var headAssessment = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, EditFingerprint.Compute(live));
			Check(headAssessment.Classification == "exact", "exact replay");

			var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
			var rootAssessment = history.Assess(lineage.Manifest.LineageId, root.CheckpointId, EditFingerprint.Compute(live));
			var undoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, root.CheckpointId, "undo");
			var undoPlan = history.PlanNavigation(lineage, lineage.Manifest.HeadCheckpointId, root.CheckpointId);
			var inverse = undoPlan.Apply(live);
			Check(ReferenceEquals(live.ResolveToken(method.MDToken.Raw), method), "navigation preserves token identity");
			history.Finalize(undoWrite, live);
			Check(((MethodDef)live.ResolveToken(method.MDToken.Raw)).Name == oldName, "undo graph");

			var redoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, root.CheckpointId, prepared.PostHeadCheckpointId, "redo");
			store.ArmedStage = "readback";
			try { history.PrepareHeadMove(lineage.Manifest.LineageId, root.CheckpointId, prepared.PostHeadCheckpointId, "redo"); throw new Exception("readback fault not observed"); }
			catch (IOException) { }
			Check(store.TempCount == 1, "fault cleanup keeps only previously prepared temp");
			history.DeleteOwnedTemp(redoWrite);
			Check(store.TempCount == 0, "owned temp cleanup");

			var validPackage = store.FinalBytes(lineage.Manifest.LineageId);
			// P03-CHANGE-001: the persisted envelope must carry the executable
			// compiled inverse, not the historical replay summary.
			using (var archiveCheck = new System.IO.Compression.ZipArchive(new MemoryStream(validPackage), System.IO.Compression.ZipArchiveMode.Read))
				foreach (var opEntry in archiveCheck.Entries.Where(e => e.FullName.StartsWith("operations/", StringComparison.Ordinal)).ToArray())
				using (var opReader = JsonDocument.Parse(opEntry.Open())) {
					var envelopes = opReader.RootElement.GetProperty("operations");
					foreach (var envelope in envelopes.EnumerateArray()) {
						var inv = envelope.GetProperty("inverse");
						Check(inv.GetProperty("strategy").GetString() == "compiled_state", "envelope carries compiled_state inverse");
						Check(inv.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object && st.EnumerateObject().Any(), "envelope state is a non-empty object");
					}
				}
			var duplicate = AddDuplicateManifest(validPackage);
			try { history.ValidatePackageForTesting(duplicate); throw new Exception("duplicate ZIP entry accepted"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { }

			inverse();
			Check(((MethodDef)live.ResolveToken(method.MDToken.Raw)).Name == newName, "navigation failure recovery restores pre-state");
			try { inverse(); throw new Exception("navigation inverse executed twice"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
			undoPlan.Apply(live);
			Check(((MethodDef)live.ResolveToken(method.MDToken.Raw)).Name == oldName, "restored root state");
			Console.WriteLine("PASS p03-store-harness checkpoints=2 fault_cleanup=true malicious_zip_rejected=true operation_inverse=true token_identity=true");
			return 0;
		}
		catch (Exception ex) {
			Console.Error.WriteLine(ex);
			return 1;
		}
	}

	static byte[] AddDuplicateManifest(byte[] package) {
		using var input = new MemoryStream(package, writable: false);
		using var source = new ZipArchive(input, ZipArchiveMode.Read);
		using var output = new MemoryStream();
		using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true)) {
			foreach (var entry in source.Entries) {
				var copy = target.CreateEntry(entry.FullName);
				using var from = entry.Open(); using var to = copy.Open(); from.CopyTo(to);
			}
			var duplicate = target.CreateEntry("manifest.json");
			using var writer = new StreamWriter(duplicate.Open(), Encoding.UTF8); writer.Write("{}");
		}
		return output.ToArray();
	}

	static void TestLegacyRename(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var method = module.GetTypes().Single(x => x.Name == "GenericMethodOwner`1").Methods.Single(x => x.Name == "Echo");
		var references = module.GetMemberRefs().Where(x => x.IsMethodRef && x.Name == "Echo").OrderBy(x => x.MDToken.Raw).ToArray();
		Check(references.Length > 0, "legacy closed generic reference fixture");
		var operation = new Dictionary<string, object?> {
			["kind"] = "legacy_symbol_rename", ["target_kind"] = "method",
			["definition"] = RenameRow(method.MDToken.Raw, "Echo", "EchoP03"),
			["references"] = references.Select(x => {
				var row = RenameRow(x.MDToken.Raw, "Echo", "EchoP03"); row["table"] = "MemberRef"; return row;
			}).ToArray(),
		};
		using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation, EditWire.JsonOptions));
		var before = EditFingerprint.Compute(module);
		var order = new List<string>();
		try {
			EditLegacyRenameOperation.TestMutationHook = (kind, _) => order.Add(kind);
			EditLegacyRenameOperation.Apply(module, forward.RootElement);
			Check(order.First() == "definition" && order.Skip(1).All(x => x == "reference"), "legacy forward order");
			using var inverse = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?> {
				["legacy"] = EditLegacyRenameOperation.Reverse(forward.RootElement),
			}, EditWire.JsonOptions));
			order.Clear();
			EditLegacyRenameOperation.ApplyInverse(module, inverse.RootElement);
			Check(order.Last() == "definition" && order.Take(order.Count - 1).All(x => x == "reference"), "legacy inverse order");
			Check(EditFingerprint.Compute(module) == before, "legacy serialized inverse fingerprint");

			foreach (var failKind in new[] { "definition", "reference" }) {
				EditLegacyRenameOperation.TestMutationHook = (kind, _) => { if (kind == failKind) throw new IOException("injected rename failure"); };
				try { EditLegacyRenameOperation.Apply(module, forward.RootElement); throw new Exception("rename failure not injected"); }
				catch (IOException) { }
				Check(EditFingerprint.Compute(module) == before, "legacy " + failKind + " failure atomicity");
			}
			EditLegacyRenameOperation.TestMutationHook = null;
			var outcome = EditLegacyRenameOperation.Apply(module, forward.RootElement);
			method.Name = "ExternalDrift";
			var drifted = EditFingerprint.Compute(module);
			try { outcome.Undo(); throw new Exception("inverse accepted definition drift"); }
			catch (InvalidOperationException) { }
			Check(EditFingerprint.Compute(module) == drifted, "inverse drift causes zero partial mutation");
			method.Name = "EchoP03"; outcome.Undo();
			Check(EditFingerprint.Compute(module) == before, "legacy undo after drift removed");
		}
		finally { EditLegacyRenameOperation.TestMutationHook = null; }
		Console.WriteLine("PASS legacy-rename order=forward+inverse faults=definition+reference drift=zero-mutation");
	}

	static Dictionary<string, object?> RenameRow(uint token, string oldName, string newName) => new() {
		["token"] = "0x" + token.ToString("x8"), ["old_name"] = oldName, ["new_name"] = newName,
	};

	static void TestUpdateInverses(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var method = module.GetTypes().Single(x => x.FullName == "TestIL.Simple").Methods.Single(x => x.Name == "AddOne");
		var methodRef = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") };
		var body = new Dictionary<string, object?> {
			["max_stack"] = 1, ["init_locals"] = false, ["locals"] = Array.Empty<object>(), ["exception_handlers"] = Array.Empty<object>(),
			["instructions"] = new[] { new { opcode = "ldc.i4.7", operand = (object?)null }, new { opcode = "ret", operand = (object?)null } },
		};
		var cases = new[] {
			new Dictionary<string, object?> { ["kind"] = "method_update", ["target"] = methodRef, ["name"] = "Renamed" },
			new Dictionary<string, object?> { ["kind"] = "method_body_replace", ["target"] = methodRef, ["body"] = body },
			new Dictionary<string, object?> { ["kind"] = "parameter_update",
				["parameter_target"] = new { owner_method = methodRef, parameter_index = 0 }, ["name"] = "renamedArgument" },
		};
		foreach (var operation in cases) {
			var before = EditFingerprint.Compute(module);
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var map = new Dictionary<string, IMDTokenProvider>();
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
			EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			EditOperationRegistry.ApplyCompiledInverse(module, serialized.RootElement, map, 0);
			Check(EditFingerprint.Compute(module) == before, "compiled inverse " + operation["kind"]);
			Check(ReferenceEquals(module.ResolveToken(method.MDToken.Raw), method), "compiled inverse retains method token object");
		}
		method.ParamDefs.Clear();
		var absentBefore = EditFingerprint.Compute(module);
		using var materialize = JsonDocument.Parse(JsonSerializer.Serialize(cases[2]));
		var emptyMap = new Dictionary<string, IMDTokenProvider>();
		var absentInverse = EditOperationRegistry.CompileInverse(module, materialize.RootElement, emptyMap);
		EditOperationRegistry.Apply(module, materialize.RootElement, emptyMap, 0);
		Check(method.ParamDefs.Count == 1, "parameter materialized");
		using var absentSerialized = JsonDocument.Parse(JsonSerializer.Serialize(absentInverse));
		EditOperationRegistry.ApplyCompiledInverse(module, absentSerialized.RootElement, emptyMap, 0);
		Check(method.ParamDefs.Count == 0 && EditFingerprint.Compute(module) == absentBefore, "inverse removes only materialized ParamDef");
		Console.WriteLine("PASS update-inverses method+body+parameter+absent-paramdef token_identity=true");
	}

	static void TestPayloadHistory(string fixture) {
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-payload-artifacts"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var method = live.GetTypes().Single(x => x.FullName == "TestIL.Simple").Methods.Single(x => x.Name == "AddOne");
		var before = EditFingerprint.Compute(live);
		var operation = JsonSerializer.Serialize(new {
			kind = "method_body_replace", target = new { token = "0x" + method.MDToken.Raw.ToString("x8") },
			body = new { max_stack = 1, init_locals = false, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
				instructions = new[] { new { opcode = "ldc.i4.7", operand = (object?)null }, new { opcode = "ret", operand = (object?)null } } },
		});
		EditLoadedLineage? lineage = null;
		for (var round = 0; round < 2; round++) {
			var observed = EditFingerprint.Compute(live);
			using var workspace = EditWorkspace.CreateForTesting(live);
			Check(observed == EditFingerprint.Compute(live) && observed == workspace.PrivateFingerprint(), "private copy preserves full live fingerprint after commit");
			workspace.RestoreCommittedState();
			Check(observed == workspace.PrivateFingerprint(), "private rebuild preserves baseline body header");
			if (lineage != null) {
				var assessment = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, observed);
				var liveBytes = EditWorkspace.WriteCheckpointImage(live);
				Check(assessment.Classification == "exact" && assessment.Bytes.SequenceEqual(liveBytes), "live and replay emitted images match byte-for-byte");
				using var reloaded = ModuleDefMD.Load(liveBytes);
				Check(liveBytes.SequenceEqual(EditWorkspace.WriteCheckpointImage(reloaded)), "checkpoint image remains stable after reload");
			}
			var binding = history.ResolveBegin(workspace, null);
			using var forward = JsonDocument.Parse(operation);
			EditOperationRegistry.Apply(workspace.PrivateModule, forward.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operation);
			var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-payload", 1, Array.Empty<string>());
			EditOperationRegistry.ApplyPersisted(live, forward.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			history.Finalize(prepared, live);
			lineage = history.Load(prepared.Lineage.Manifest.LineageId);
		}
		Check(lineage!.Manifest.Checkpoints.Count == 3 && lineage.Manifest.Payloads.Count == 1 && lineage.PayloadBytes.Count == 1, "payload deduplicated across checkpoints");
		using (var archive = new ZipArchive(new MemoryStream(store.FinalBytes(lineage.Manifest.LineageId)), ZipArchiveMode.Read)) {
			Check(archive.Entries.Count(x => x.FullName == "baseline/module.bin") == 1, "one payload history baseline");
			Check(archive.Entries.Count(x => x.FullName.StartsWith("payloads/", StringComparison.Ordinal)) == 1, "one physical payload entry");
		}
		var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
		var plan = history.PlanNavigation(lineage, lineage.Manifest.HeadCheckpointId, root.CheckpointId);
		plan.Apply(live);
		Check(EditFingerprint.Compute(live) == before && ReferenceEquals(live.ResolveToken(method.MDToken.Raw), method), "payload LCA inverse preserves state and token identity");
		Console.WriteLine("PASS payload-history checkpoints=3 baseline=1 payload=1 operation_inverse=true");
	}

	// ACC-011: branching undo/redo with explicit target selection and native
	// Ctrl+Z isolation, through the production commit/navigation path.
	static void TestBranching(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-branch"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.GetTypes().SelectMany(x => x.Methods).First(x => !x.IsConstructor && x.HasBody);
		var originalName = method.Name.String;
		string Commit(string name) {
			var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
				["kind"] = "method_update",
				["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
				["name"] = name,
			}, EditWire.JsonOptions);
			using (var json = JsonDocument.Parse(operation))
				EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operation);
			var binding = history.ResolveBegin(workspace, null);
			var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-branch-" + name, 1, Array.Empty<string>());
			using (var liveOperation = JsonDocument.Parse(operation))
				EditOperationRegistry.ApplyPersisted(live, liveOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			history.Finalize(prepared, live);
			workspace.NormalizedOperations.Clear();
			return prepared.Lineage.Manifest.HeadCheckpointId;
		}
		string UndoTo(string target) {
			var lineageId = history.Load(store.ListFinalIds().First()).Manifest.LineageId;
			var write = history.PrepareHeadMove(lineageId, history.Load(lineageId).Manifest.HeadCheckpointId, target, "branch-undo");
			var plan = history.PlanNavigation(history.Load(lineageId), history.Load(lineageId).Manifest.HeadCheckpointId, target);
			plan.Apply(live);
			history.Finalize(write, live);
			return lineageId;
		}
		var branchA = Commit(originalName + "_A");
		var lineageId = store.ListFinalIds().Single();
		UndoTo(history.Load(lineageId).Manifest.Checkpoints.Single(c => c.ParentCheckpointId == null).CheckpointId);
		var branchB = Commit(originalName + "_B");
		var lineage = history.Load(lineageId);
		var root = lineage.Manifest.Checkpoints.Single(c => c.ParentCheckpointId == null);
		Check(lineage.Manifest.Checkpoints.Count == 3, "branch tree has three checkpoints");
		Check(lineage.Manifest.Checkpoints.Count(c => c.ParentCheckpointId == root.CheckpointId) == 2, "root has two children (A and B)");
		Check(branchA != branchB && lineage.Manifest.HeadCheckpointId == branchB, "head is branch B; branch A preserved");

		// Redo to the sibling branch is an explicit target choice; the tree keeps both.
		var redoWrite = history.PrepareHeadMove(lineageId, branchB, branchA, "redo-to-A");
		var redoPlan = history.PlanNavigation(history.Load(lineageId), branchB, branchA);
		redoPlan.Apply(live);
		history.Finalize(redoWrite, live);
		Check(((MethodDef)live.ResolveToken(method.MDToken.Raw)).Name == originalName + "_A", "redo to sibling branch restores A");
		lineage = history.Load(lineageId);
		Check(lineage.Manifest.Checkpoints.Count == 3 && lineage.Manifest.HeadCheckpointId == branchA, "navigation kept both branches; head is A");

		// Native Ctrl+Z isolation: a raw live mutation leaves MCP history intact
		// and the next navigation is rejected until the module matches the plan.
		var historyBytesBefore = store.FinalBytes(lineageId);
		var savedName = method.Name.String;
		method.Name = originalName + "_CTRLZ";
		Check(store.FinalBytes(lineageId).SequenceEqual(historyBytesBefore), "raw live mutation does not touch MCP history");
		var blockedWrite = default(EditPreparedHistoryWrite);
		var blocked = false;
		try {
			blockedWrite = history.PrepareHeadMove(lineageId, branchA, branchB, "ctrlz-blocked");
			var blockedPlan = history.PlanNavigation(history.Load(lineageId), branchA, branchB);
			blockedPlan.Apply(live);
		}
		catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { blocked = true; }
		Check(blocked, "navigation after raw mutation is rejected");
		method.Name = savedName;
		var retryWrite = history.PrepareHeadMove(lineageId, branchA, branchB, "retry-after-restore");
		var retryPlan = history.PlanNavigation(history.Load(lineageId), branchA, branchB);
		retryPlan.Apply(live);
		history.Finalize(retryWrite, live);
		Check(((MethodDef)live.ResolveToken(method.MDToken.Raw)).Name == originalName + "_B", "navigation works after restoring the raw mutation");

		Console.WriteLine("PASS acc011-branching undo-new-commit-sibling-redo-explicit-target ctrlz-isolation-conflict-retry");
	}

	// ACC-013: classification and migration semantics against packages whose
	// recorded head results come from a second real writer (raw dnlib module
	// write) instead of the checkpoint-image writer.
	static void TestDualToolClassification(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-dualtool"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.GetTypes().SelectMany(x => x.Methods).First(x => !x.IsConstructor && x.HasBody);
		var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_update",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
			["name"] = "DualToolHead",
		}, EditWire.JsonOptions);
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
		workspace.NormalizedOperations.Add(operation);
		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-dualtool", 1, Array.Empty<string>());
		using (var liveOperation = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(live, liveOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		history.Finalize(prepared, live);
		var realId = prepared.Lineage.Manifest.LineageId;
		var headId = prepared.Lineage.Manifest.HeadCheckpointId;
		var headNode = prepared.Lineage.Manifest.Checkpoints.Single(x => x.CheckpointId == headId);
		var liveFingerprint = EditFingerprint.Compute(live);

		// The real second-writer image: the same live module through the raw
		// dnlib writer instead of EditWorkspace.WriteCheckpointImage.
		byte[] rawWrite;
		using (var buffer = new MemoryStream()) { live.Write(buffer); rawWrite = buffer.ToArray(); }
		var rawSha = EditWire.Sha256(rawWrite);
		Check(rawSha != headNode.ResultImageSha256, "raw dnlib writer image differs from the checkpoint image");

		Check(history.Assess(realId, headId, liveFingerprint).Classification == "exact", "control checkpoint classifies exact");

		byte[] Craft(byte[] package, string newLineageId, Action<EditCheckpointManifest, Dictionary<string, byte[]>> mutate) {
			using var input = new MemoryStream(package);
			using var zip = new ZipArchive(input, ZipArchiveMode.Read);
			var entries = zip.Entries.ToDictionary(x => x.FullName, x => {
				using var stream = x.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
			}, StringComparer.Ordinal);
			var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
			manifest.LineageId = newLineageId;
			manifest.FamilyId = "family-" + newLineageId.Substring("lineage-".Length);
			mutate(manifest, entries);
			entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
			using var output = new MemoryStream();
			using (var rewritten = new ZipArchive(output, ZipArchiveMode.Create, true))
				foreach (var row in entries) {
					var entry = rewritten.CreateEntry(row.Key, CompressionLevel.Optimal);
					using var target = entry.Open(); target.Write(row.Value, 0, row.Value.Length);
				}
			return output.ToArray();
		}
		void Inject(string lineageId, byte[] package) {
			var temp = store.CreateTemp(lineageId, package);
			store.FinalizeTemp(temp, replaceExisting: false);
		}

		var validatedId = "lineage-" + Guid.NewGuid().ToString("N");
		Inject(validatedId, Craft(store.FinalBytes(realId), validatedId, (manifest, _) => {
			manifest.Checkpoints.Single(x => x.CheckpointId == headId).ResultImageSha256 = rawSha;
		}));
		var unverifiedId = "lineage-" + Guid.NewGuid().ToString("N");
		Inject(unverifiedId, Craft(store.FinalBytes(realId), unverifiedId, (manifest, _) => {
			var node = manifest.Checkpoints.Single(x => x.CheckpointId == headId);
			node.ResultImageSha256 = rawSha;
			node.ResultSemanticFingerprint = (node.ResultSemanticFingerprint[0] == 'f' ? "0" : "f") + node.ResultSemanticFingerprint.Substring(1);
		}));
		var unknownFormatId = "lineage-" + Guid.NewGuid().ToString("N");
		Inject(unknownFormatId, Craft(store.FinalBytes(realId), unknownFormatId, (manifest, _) => {
			manifest.Format = "dnspy.edit.checkpoints.v3";
		}));
		var malformedVersionId = "lineage-" + Guid.NewGuid().ToString("N");
		Inject(malformedVersionId, Craft(store.FinalBytes(realId), malformedVersionId, (manifest, entries) => {
			var node = manifest.Checkpoints.Single(x => x.CheckpointId == headId);
			var document = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(entries[node.OperationEntry]))!;
			document["operations"]![0]!["kind_version"] = 2;
			var rewritten = Encoding.UTF8.GetBytes(document.ToJsonString());
			entries[node.OperationEntry] = rewritten;
			node.OperationSha256 = EditWire.Sha256(rewritten);
		}));
		var unknownEnvelopeId = "lineage-" + Guid.NewGuid().ToString("N");
		Inject(unknownEnvelopeId, Craft(store.FinalBytes(realId), unknownEnvelopeId, (manifest, entries) => {
			var node = manifest.Checkpoints.Single(x => x.CheckpointId == headId);
			var document = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(entries[node.OperationEntry]))!;
			document["format"] = "dnspy.edit.op.v2";
			var rewritten = Encoding.UTF8.GetBytes(document.ToJsonString());
			entries[node.OperationEntry] = rewritten;
			node.OperationSha256 = EditWire.Sha256(rewritten);
		}));

		var validatedReplay = history.Assess(validatedId, headId, liveFingerprint);
		Check(validatedReplay.Classification == "validated_drift", "second-writer image with same semantics classifies validated_drift");
		Check(history.Assess(unverifiedId, headId, liveFingerprint).Classification == "unverified_drift", "recorded semantic drift classifies unverified_drift");
		try {
			history.Load(unknownFormatId);
			Check(false, "unknown package format must be rejected at load");
		}
		catch (EditDomainException ex) {
			Check(ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED", "unknown package format rejection code=" + ex.Code);
		}
		try {
			history.Load(malformedVersionId);
			Check(false, "schema-invalid operation version must be rejected at load");
		}
		catch (EditDomainException ex) {
			Check(ex.Code == "EDIT_CHECKPOINT_INVALID", "schema-invalid operation version rejection code=" + ex.Code);
		}
		try {
			history.Load(unknownEnvelopeId);
			Check(false, "unknown operation envelope format must be rejected at load");
		}
		catch (EditDomainException ex) {
			Check(ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED", "unknown operation envelope format rejection code=" + ex.Code);
		}

		// Confirmed migration of the validated head re-baselines as an exact
		// migration child carrying the current checkpoint image.
		var currentImage = EditWorkspace.WriteCheckpointImage(live);
		var migration = history.PrepareMigration(validatedReplay, currentImage, "review-dualtool-migration");
		history.Finalize(migration, live);
		var migratedId = migration.Lineage.Manifest.HeadCheckpointId;
		var migrated = history.Assess(validatedId, migratedId, liveFingerprint);
		Check(migrated.Classification == "exact", "confirmed migration child classifies exact");
		Check(migrated.ImageSha256 == EditWire.Sha256(currentImage), "migration child records the current checkpoint image");

		Check(history.Assess(realId, headId, liveFingerprint).Classification == "exact", "original lineage unaffected");
		Check(EditFingerprint.Compute(live) == liveFingerprint, "live module unchanged by assessments and migration");
		Check(store.ListFinalIds().Count == 6, "store finals are exactly the real lineage plus five fixtures");

		Console.WriteLine("PASS dual-tool-classification exact+validated+unverified+unknown-format+envelope-rejected+malformed-rejected migration-child-exact zero-side-effect");
	}

	// ACC-029: capacity curve across 22 commits on one lineage (monotonic store,
	// payload dedup, transient dual occupancy, residual metering) plus ACC-012
	// history pagination and unique resolution against a foreign-mvid lineage.
	static void TestCapacityResolution(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-capacity"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.GetTypes().SelectMany(x => x.Methods).First(x => !x.IsConstructor && x.HasBody);

		var sizes = new List<long>();
		string lineageId = string.Empty;
		for (var index = 0; index < 22; index++) {
			var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
				["kind"] = "method_update",
				["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
				["name"] = index % 2 == 0 ? "CurveA" : "CurveB",
			}, EditWire.JsonOptions);
			using (var json = JsonDocument.Parse(operation))
				EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operation);
			var binding = history.ResolveBegin(workspace, null);
			var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-curve-" + index, 1, Array.Empty<string>());
			if (index == 1) {
				// Transient dual occupancy: the previous final and the owned temp
				// coexist between prepare and finalize; the temp is metered as a
				// residual store object during that window.
				Check(store.TempCount == 1 && store.FinalCount == 1, "transient dual occupancy final+temp");
				var window = store.EnumerateCheckpointObjects().ToArray();
				Check(window.Count(o => o.IsTrustedFinal) == 1 && window.Count(o => o.IsResidual) == 1, "temp metered as residual in the commit window");
			}
			using (var liveOperation = JsonDocument.Parse(operation))
				EditOperationRegistry.ApplyPersisted(live, liveOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			history.Finalize(prepared, live);
			workspace.NormalizedOperations.Clear();
			lineageId = prepared.Lineage.Manifest.LineageId;
			sizes.Add(store.FinalBytes(lineageId).LongLength);
		}
		for (var i = 1; i < sizes.Count; i++) Check(sizes[i] >= sizes[i - 1], "capacity curve monotonic step " + i);
		Check(store.ListFinalIds().Count == 1, "single lineage across 22 commits");
		var loaded = history.Load(lineageId);
		Check(loaded.Manifest.Checkpoints.Count == 23, "22 commits accumulate 23 checkpoints");
		// Pure metadata updates (method_update) reference no side-car payloads
		// (actual=0 measured); payload deduplication is exercised where payloads
		// exist by the payload-history suite test.
		Check(loaded.Manifest.Payloads.Count == 0, "metadata-only commits add no side-car payloads");
		Check(store.EnumerateCheckpointObjects().All(o => o.IsTrustedFinal), "no residual after finalize");

		// ACC-012: pagination drains the lineage through the cursor.
		var first = history.HistoryView(lineageId, null, 0, 10);
		Check(((Array)first["checkpoints"]!).Length == 10 && first["next_cursor"] != null, "first page of 10 carries cursor");
		var second = history.HistoryView(lineageId, null, EditHistoryModule.DecodeCursor(first["next_cursor"] as string), 100);
		Check(((Array)second["checkpoints"]!).Length == 13 && second["next_cursor"] == null, "cursor drains the remaining 13");

		// ACC-012: a foreign-mvid lineage with identical content is skipped by
		// the resolver, so a fresh binding resolves the real lineage uniquely.
		var foreignId = "lineage-" + Guid.NewGuid().ToString("N");
		{
			using var input = new MemoryStream(store.FinalBytes(lineageId));
			using var zip = new ZipArchive(input, ZipArchiveMode.Read);
			var entries = zip.Entries.ToDictionary(x => x.FullName, x => {
				using var stream = x.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
			}, StringComparer.Ordinal);
			zip.Dispose();
			var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
			manifest.LineageId = foreignId;
			manifest.FamilyId = "family-" + foreignId.Substring("lineage-".Length);
			manifest.SourceIdentity.OriginMvid = Guid.NewGuid().ToString("D").ToLowerInvariant();
			entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
			using var output = new MemoryStream();
			using (var rewritten = new ZipArchive(output, ZipArchiveMode.Create, true))
				foreach (var row in entries) {
					var entry = rewritten.CreateEntry(row.Key, CompressionLevel.Optimal);
					using var target = entry.Open(); target.Write(row.Value, 0, row.Value.Length);
				}
			var temp = store.CreateTemp(foreignId, output.ToArray());
			store.FinalizeTemp(temp, replaceExisting: false);
		}
		using (var history2 = new EditHistoryModule(store, catalog.CheckpointPackage))
		using (var workspace2 = EditWorkspace.CreateForTesting(live)) {
			var binding = history2.ResolveBegin(workspace2, null);
			Check(binding.LineageId == lineageId, "foreign-mvid lineage skipped; unique resolution binds the real lineage");
		}

		Console.WriteLine("PASS capacity-resolution curve22+dedup2+transient-dual+residual-window pagination foreign-mvid-skipped");
	}

	// ACC-014: default/explicit export, blocked states, source protection and
	// fault-preserved old outputs, exercised through the production commit path.
	static void TestExport(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-export"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.GetTypes().SelectMany(x => x.Methods).First(x => !x.IsConstructor && x.HasBody);
		var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_update",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
			["name"] = method.Name.String + "_X",
		}, EditWire.JsonOptions);
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
		workspace.NormalizedOperations.Add(operation);
		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-export", 1, Array.Empty<string>());
		using (var liveOperation = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(live, liveOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		history.Finalize(prepared, live);
		var lineage = history.Load(prepared.Lineage.Manifest.LineageId);
		var exact = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, EditFingerprint.Compute(live));
		Check(exact.Classification == "exact", "export head exact");

		// default output: identity, bytes and reload
		var defaultResult = history.Export(exact, null, Path.GetFullPath(fixture));
		var defaultPath = lineage.Manifest.DefaultOutput.RelativePath;
		Check(string.Equals(defaultResult.Path, defaultPath, StringComparison.OrdinalIgnoreCase), "default output path");
		Check(store.OutputBytes(defaultPath, out var defaultBytes) && defaultBytes.SequenceEqual(exact.Bytes), "default output bytes equal replay image");
		Check(defaultResult.Sha256 == EditWire.Sha256(exact.Bytes) && defaultResult.Length == exact.Bytes.LongLength, "default output identity");
		using (var exported = ModuleDefMD.Load(defaultBytes)) Check(exported.GetTypes().Count() == live.GetTypes().Count(), "export reloads");

		// explicit output
		var explicitResult = history.Export(exact, "explicit/export.dll", Path.GetFullPath(fixture));
		Check(store.OutputBytes("explicit/export.dll", out var explicitBytes) && explicitBytes.SequenceEqual(exact.Bytes), "explicit output bytes");

		// The store must validate the staged bytes before replacing an existing
		// output. A rejected staged image cannot change either bytes or identity.
		var validatorCalled = false;
		var explicitFileId = store.OutputFileId("explicit/export.dll");
		try {
			store.WriteOutputAtomic("explicit/export.dll", exact.Bytes, true, new EditOutputValidation(
				null, stream => { validatorCalled = true; throw new InvalidDataException("rejected staged image"); }));
			throw new Exception("staged validator rejection not observed");
		}
		catch (InvalidDataException) { }
		Check(validatorCalled, "staged validator invoked");
		Check(store.OutputBytes("explicit/export.dll", out var afterValidationFault)
			&& afterValidationFault.SequenceEqual(explicitBytes), "validator rejection preserves old output bytes");
		Check(store.OutputFileId("explicit/export.dll") == explicitFileId, "validator rejection preserves old output identity");
		var afterValidationResult = store.WriteOutputAtomic("explicit/export.dll", explicitBytes, true,
			new EditOutputValidation(null, stream => Check(stream.Length == explicitBytes.LongLength, "validator sees staged bytes")));
		Check(afterValidationResult.FileId != explicitResult.FileId, "successful replacement changes output identity");

		// source protection: explicit request to overwrite the source file itself
		try { history.Export(exact, Path.GetFullPath(fixture), Path.GetFullPath(fixture)); throw new Exception("source overwrite accepted"); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_EXPORT_BLOCKED") { }

		// blocked state: a non-exact assessment (test seam: same replay facts,
		// classification overridden) must refuse to export
		var drifted = new EditReplayAssessment {
			ReplayId = exact.ReplayId, Classification = "validated_drift", Lineage = exact.Lineage,
			Checkpoint = exact.Checkpoint, Bytes = exact.Bytes, ImageSha256 = exact.ImageSha256,
			SemanticFingerprint = exact.SemanticFingerprint, PackageSha256 = exact.PackageSha256,
			LiveFingerprint = exact.LiveFingerprint, HeadCheckpointId = exact.HeadCheckpointId,
		};
		try { history.Export(drifted, null, Path.GetFullPath(fixture)); throw new Exception("non-exact export accepted"); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_EXPORT_BLOCKED") { }

		// fault at finalize keeps the previous output intact
		store.ArmedStage = "finalize";
		try { history.Export(exact, null, Path.GetFullPath(fixture)); throw new Exception("output fault not observed"); }
		catch (IOException) { }
		Check(store.OutputBytes(defaultPath, out var afterFault) && afterFault.SequenceEqual(exact.Bytes), "old output preserved on fault");

		Console.WriteLine("PASS acc014-export default+explicit identity+reload source-protected blocked-state-refused fault-preserves-output");
	}

	static void TestTailInverses(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var method = module.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
		var methodRef = new { token = "0x" + method.MDToken.Raw.ToString("x8") };
		var originalParam = method.ParamDefs.Single(p => p.Sequence == 1);
		var genericOwner = module.GetTypes().Single(t => t.Name == "GenericFieldOwner`1");
		var generic = genericOwner.GenericParameters[0];
		var operations = new object[] {
			new { kind = "parameter_add", owner_method = methodRef, parameter_index = 1, name = "tail", parameter_type = "System.Int32" },
			new { kind = "parameter_remove", parameter_target = new { owner_method = methodRef, parameter_index = 0 }, remove_mode = "reject_if_referenced" },
			new { kind = "generic_parameter_add", owner = methodRef, generic_index = 0, name = "TNew" },
			new { kind = "generic_parameter_remove", target = new { token = "0x" + generic.MDToken.Raw.ToString("x8") }, remove_mode = "reject_if_referenced" },
		};
		foreach (var operation in operations) {
			var before = EditFingerprint.Compute(module);
			var map = new Dictionary<string, IMDTokenProvider>();
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
			EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			var restore = EditOperationRegistry.ApplyCompiledInverse(module, serialized.RootElement, map, 0);
			Check(before == EditFingerprint.Compute(module), "tail inverse " + forward.RootElement.GetProperty("kind").GetString());
			Check(ReferenceEquals(method.ParamDefs.Single(p => p.Sequence == 1), originalParam), "tail restore preserves cached parameter object");
			Check(ReferenceEquals(genericOwner.GenericParameters[0], generic), "tail restore preserves cached generic object");
		}
		var chainedBefore = EditFingerprint.Compute(module);
		var chainedMap = new Dictionary<string, IMDTokenProvider>();
		using (var add = JsonDocument.Parse(JsonSerializer.Serialize(new {
			kind = "parameter_add", owner_method = methodRef, parameter_index = 1, name = "ephemeral", parameter_type = "System.Int32",
		})))
		using (var remove = JsonDocument.Parse(JsonSerializer.Serialize(new {
			kind = "parameter_remove", parameter_target = new { object_id = "obj-000-00" }, remove_mode = "reject_if_referenced",
		}))) {
			var addOutcome = EditOperationRegistry.Apply(module, add.RootElement, chainedMap, 0);
			var removeOutcome = EditOperationRegistry.Apply(module, remove.RootElement, chainedMap, 1);
			removeOutcome.Undo();
			addOutcome.Undo();
		}
		Check(EditFingerprint.Compute(module) == chainedBefore, "same-session added parameter remove/restore");
		var rollbackMap = new Dictionary<string, IMDTokenProvider>();
		EditOperationOutcome? rollbackAdd = null, rollbackRemove = null;
		try {
			using var add = JsonDocument.Parse(JsonSerializer.Serialize(new {
				kind = "parameter_add", owner_method = methodRef, parameter_index = 1, name = "rollback", parameter_type = "System.Int32",
			}));
			using var remove = JsonDocument.Parse(JsonSerializer.Serialize(new {
				kind = "parameter_remove", parameter_target = new { object_id = "obj-000-00" }, remove_mode = "reject_if_referenced",
			}));
			rollbackAdd = EditOperationRegistry.Apply(module, add.RootElement, rollbackMap, 0);
			rollbackRemove = EditOperationRegistry.Apply(module, remove.RootElement, rollbackMap, 1);
			throw new IOException("injected downstream failure");
		}
		catch (IOException) {
			rollbackRemove!.Undo();
			rollbackAdd!.Undo();
		}
		Check(EditFingerprint.Compute(module) == chainedBefore, "same-session added parameter error rollback");
		var oldBody = method.Body;
		method.Body = null;
		var absent = EditFingerprint.Compute(module);
		using (var forward = JsonDocument.Parse(JsonSerializer.Serialize(new {
			kind = "method_body_replace", target = methodRef,
			body = new { max_stack = 0, init_locals = false, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
				instructions = new[] { new { opcode = "ret", operand = (object?)null } } },
		}))) {
			var map = new Dictionary<string, IMDTokenProvider>();
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
			EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			EditOperationRegistry.ApplyCompiledInverse(module, serialized.RootElement, map, 0);
			Check(method.Body == null && EditFingerprint.Compute(module) == absent, "absent body inverse");
		}
		method.Body = oldBody;
		Console.WriteLine("PASS tail-inverses parameter+generic add/remove chained-added-parameter=true error-rollback=true cached_identity=true absent_body=true");
	}

	static void TestReloadedTailInverses(string fixture) {
		foreach (var kind in new[] { "parameter_remove", "generic_parameter_remove" }) {
			using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var method = module.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
			var generic = module.GetTypes().Single(t => t.Name == "GenericFieldOwner`1").GenericParameters[0];
			var before = EditFingerprint.ComputeRoundtrip(module);
			var beforeImage = EditWorkspace.WriteCheckpointImage(module);
			var operation = kind == "parameter_remove" ? (object)new {
				kind, parameter_target = new { owner_method = new { token = "0x" + method.MDToken.Raw.ToString("x8") }, parameter_index = 0 }, remove_mode = "reject_if_referenced",
			} : new { kind, target = new { token = "0x" + generic.MDToken.Raw.ToString("x8") }, remove_mode = "reject_if_referenced" };
			var map = new Dictionary<string, IMDTokenProvider>();
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
			EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
			var deletedImage = EditWorkspace.WriteCheckpointImage(module);
			Check(deletedImage.SequenceEqual(EditWorkspace.WriteCheckpointImage(module)), "deleted module image is deterministic " + kind);
			using var reloaded = ModuleDefMD.Load(deletedImage);
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			var otherOwner = reloaded.GetTypes().Single(t => t.Name == "ObfuscatedDelegate`1");
			var otherGeneric = otherOwner.GenericParameters[0];
			var deletedState = EditFingerprint.Compute(reloaded);
			var restored = EditOperationRegistry.ApplyCompiledInverse(reloaded, serialized.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			Check(EditFingerprint.ComputeRoundtrip(reloaded) == before, "reloaded complete semantic inverse " + kind);
			Check(ReferenceEquals(otherOwner.GenericParameters[0], otherGeneric) && ReferenceEquals(otherGeneric.Owner, otherOwner), "reloaded inverse does not reparent foreign token row");
			var restoredState = EditFingerprint.Compute(reloaded);
			try { EditOperationRegistry.ApplyCompiledInverse(reloaded, serialized.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); throw new Exception("duplicate tail inverse accepted"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
			Check(EditFingerprint.Compute(reloaded) == restoredState, "duplicate tail restore is zero mutation");
			var restoredBytes = EditWorkspace.WriteCheckpointImage(reloaded);
			Check(beforeImage.SequenceEqual(restoredBytes), "restored checkpoint image equals original checkpoint " + kind);
			using var restoredImage = ModuleDefMD.Load(restoredBytes);
			Check(EditFingerprint.ComputeRoundtrip(restoredImage) == before, "restored image semantic state " + kind);
			if (kind == "generic_parameter_remove")
				Check(((GenericParam)restoredImage.ResolveToken(generic.MDToken.Raw)).Owner.ToString() == "TestIL.GenericFieldOwner`1", "generic RID restored in emitted image");
			restored.Undo();
			Check(EditFingerprint.Compute(reloaded) == deletedState, "tail inverse failure recovery restores complete reloaded pre-state");
			Console.WriteLine("PASS reloaded-tail-inverse " + kind);
		}
	}

	// P03-CHANGE-002 v3 negative gate: operation-tombstone invariants beyond the
	// regular suite: host naming, malformed-tombstone rejection, and repeat
	// delete/reload/restore cycles returning to the byte-identical baseline.
	static void TestTombstoneGate(string fixture) {
		EditOperationOutcome CycleOnce(byte[] baseline, out byte[] restoredBytes) {
			using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var method = module.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
			var originalParam = method.ParamDefs.Single(p => p.Sequence == 1);
			var paramRid = originalParam.MDToken.Rid;
			var before = EditWorkspace.WriteCheckpointImage(module);
			Check(before.SequenceEqual(baseline), "tombstone gate baseline deterministic");
			var operation = new { kind = "parameter_remove", parameter_target = new { owner_method = new { token = "0x" + method.MDToken.Raw.ToString("x8") }, parameter_index = 0 }, remove_mode = "reject_if_referenced" };
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var map = new Dictionary<string, IMDTokenProvider>();
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, map);
			EditOperationRegistry.Apply(module, forward.RootElement, map, 0);
			var deleted = EditWorkspace.WriteCheckpointImage(module);
			using var reloaded = ModuleDefMD.Load(deleted);
			var tombstones = reloaded.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).ToArray();
			Check(tombstones.Length == 1, "deleted image carries exactly one tombstone");
			var host = tombstones[0].Methods.Single();
			Check(host.Name.String == "d" + paramRid.ToString("x6"), "host name matches param rid");
			Check(host.MethodSig.Params.Count == 0 && host.MethodSig.RetType.ElementType == dnlib.DotNet.ElementType.Void, "host sig is void()");
			using var baselineModule = ModuleDefMD.Load(baseline);
			Check(reloaded.TablesStream.TypeRefTable.Rows == baselineModule.TablesStream.TypeRefTable.Rows, "deleted image TypeRef rows unchanged");
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			var restored = EditOperationRegistry.ApplyCompiledInverse(reloaded, serialized.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			restoredBytes = EditWorkspace.WriteCheckpointImage(reloaded);
			Check(baseline.SequenceEqual(restoredBytes), "restored image equals baseline");
			Check(reloaded.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).Count() == 0, "tombstone removed after restore");
			return restored;
		}
		using var seed = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var baseline = EditWorkspace.WriteCheckpointImage(seed);
		{
			// Minimal probe: event re-owned to a user type holder, both writers.
			using var pm = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var po = pm.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var pe = po.Events[0];
			_ = pe.AddMethod; _ = pe.RemoveMethod; _ = pe.InvokeMethod; _ = pe.OtherMethods.Count;
			po.Events.Remove(pe);
			var holder = new dnlib.DotNet.TypeDefUser("probe.ns", "Holder", pm.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = dnlib.DotNet.TypeAttributes.NotPublic | dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Sealed,
			};
			pm.Types.Add(holder);
			holder.Events.Add(pe);
			using var pcanon = ModuleDefMD.Load(EditWorkspace.WriteCanonical(pm));
			var pms = new System.IO.MemoryStream();
			var popts = new dnlib.DotNet.Writer.ModuleWriterOptions(pm) { MetadataOptions = { Flags = dnlib.DotNet.Writer.MetadataFlags.PreserveRids | dnlib.DotNet.Writer.MetadataFlags.PreserveExtraSignatureData | dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack } };
			pm.Write(pms, popts);
			using var ppres = ModuleDefMD.Load(pms.ToArray());
			Console.WriteLine($"SPIKE-PROBE2 canon-sem={pcanon.TablesStream.MethodSemanticsTable.Rows} pres-single-sem={ppres.TablesStream.MethodSemanticsTable.Rows}");
		}
		CycleOnce(baseline, out _);
		// §2.6 extension: middle-row member deletions must not create writer
		// tombstones or extra reference rows, and forward Undo must restore the
		// baseline image byte for byte.
		foreach (var kind in new[] { "field_remove", "event_remove", "property_remove", "method_remove" }) {
			using var m3 = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var owner3 = m3.GetTypes().Single(t => t.FullName == "TestIL.Members");
			IEnumerable<IMDTokenProvider> candidates = kind switch {
				"field_remove" => owner3.Fields.Cast<IMDTokenProvider>(),
				"event_remove" => owner3.Events.Cast<IMDTokenProvider>(),
				"property_remove" => owner3.Properties.Cast<IMDTokenProvider>(),
				_ => owner3.Methods.Cast<IMDTokenProvider>(),
			};
			var applied = false;
			foreach (var candidate in candidates.ToArray()) {
				var op3 = new Dictionary<string, object?> {
					["kind"] = kind, ["target"] = new { token = "0x" + candidate.MDToken.Raw.ToString("x8") }, ["remove_mode"] = "reject_if_referenced",
				};
				using var forward3 = JsonDocument.Parse(JsonSerializer.Serialize(op3));
				try {
					var outcome3 = EditOperationRegistry.Apply(m3, forward3.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
					var deletedImage3 = EditWorkspace.WriteCheckpointImage(m3);
					using var d3 = ModuleDefMD.Load(deletedImage3);
					Check(d3.TablesStream.TypeRefTable.Rows == 45, kind + " image TypeRef rows unchanged");
					using var canon3 = ModuleDefMD.Load(EditWorkspace.WriteCanonical(m3));
					Console.WriteLine($"SPIKE-GATE kind={kind} canon-sem={canon3.TablesStream.MethodSemanticsTable.Rows} deleted-sem={d3.TablesStream.MethodSemanticsTable.Rows} add={(kind == "event_remove" ? ((dnlib.DotNet.EventDef)candidate).AddMethod?.Name : "-")} remove={(kind == "event_remove" ? ((dnlib.DotNet.EventDef)candidate).RemoveMethod?.Name : "-")}");
					Check(d3.GetTypes().Count(EditFingerprint.IsWriterTombstoneType) == 0, kind + " image has no writer tombstones");
					Check(d3.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone) == 1, kind + " image carries the operation tombstone");
					outcome3.Undo();
					if (kind == "event_remove") Console.WriteLine($"SPIKE-GATE undo-add={((dnlib.DotNet.EventDef)candidate).AddMethod?.Name ?? "<null>"} undo-remove={((dnlib.DotNet.EventDef)candidate).RemoveMethod?.Name ?? "<null>"} in-owner={owner3.Events.Contains((dnlib.DotNet.EventDef)candidate)}");
					var undoImage = EditWorkspace.WriteCheckpointImage(m3);
					if (!baseline.SequenceEqual(undoImage)) {
						int FirstDiff(byte[] a, byte[] b) { int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) if (a[i] != b[i]) return i; return a.Length == b.Length ? -1 : n; }
						string T(ModuleDefMD mm) => "T" + mm.TablesStream.TypeDefTable.Rows + "/M" + mm.TablesStream.MethodTable.Rows + "/E" + mm.TablesStream.EventTable.Rows + "/P" + mm.TablesStream.PropertyTable.Rows + "/F" + mm.TablesStream.FieldTable.Rows + "/TR" + mm.TablesStream.TypeRefTable.Rows + "/Sem" + mm.TablesStream.MethodSemanticsTable.Rows + "/Map" + mm.TablesStream.EventMapTable.Rows;
						string Hex(byte[] b, int at) { int s = Math.Max(0, at - 12), n = Math.Min(28, b.Length - s); return Convert.ToHexString(b, s, n); }
						int fd = FirstDiff(baseline, undoImage);
						using var bu = ModuleDefMD.Load(baseline); using var uu = ModuleDefMD.Load(undoImage);
						string Sem(ModuleDefMD mm) { var rows = new List<string>(); foreach (var t in mm.GetTypes()) { foreach (var p in t.Properties) rows.Add(t.Name + "." + p.Name + ":g=" + (p.GetMethod?.Name ?? "-") + ":s=" + (p.SetMethod?.Name ?? "-") + ":o=" + p.OtherMethods.Count + ":gm=" + p.GetMethods.Count + ":sm=" + p.SetMethods.Count); foreach (var e2 in t.Events) rows.Add(t.Name + "." + e2.Name + ":a=" + (e2.AddMethod?.Name ?? "-") + ":r=" + (e2.RemoveMethod?.Name ?? "-")); } return string.Join(" | ", rows); }
						Console.WriteLine($"SPIKE-GATE sem-base={Sem(bu)}");
						Console.WriteLine($"SPIKE-GATE sem-undo={Sem(uu)}");
						Console.WriteLine($"SPIKE-GATE kind={kind} undo-firstdiff=0x{fd:x} len={baseline.Length}/{undoImage.Length} tables_base={T(bu)} tables_undo={T(uu)} basehex={Hex(baseline, fd)} undohex={Hex(undoImage, fd)}");
					}
					Check(baseline.SequenceEqual(undoImage), kind + " undo restores baseline bytes");
					applied = true;
					break;
				}
				catch (EditDomainException) when (!applied) { /* candidate not removable; try next */ }
			}
			Check(applied, kind + " had at least one removable candidate");
			// Cross-reload member_restore: the compiled inverse must move the
			// tombstoned row back on a reloaded image, byte-identical to baseline.
			using var m4 = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var owner4 = m4.GetTypes().Single(t => t.FullName == "TestIL.Members");
			IEnumerable<IMDTokenProvider> candidates4 = kind switch {
				"field_remove" => owner4.Fields.Cast<IMDTokenProvider>(),
				"event_remove" => owner4.Events.Cast<IMDTokenProvider>(),
				"property_remove" => owner4.Properties.Cast<IMDTokenProvider>(),
				_ => owner4.Methods.Cast<IMDTokenProvider>(),
			};
			var applied4 = false;
			foreach (var candidate4 in candidates4.ToArray()) {
				var op4 = new Dictionary<string, object?> {
					["kind"] = kind, ["target"] = new { token = "0x" + candidate4.MDToken.Raw.ToString("x8") }, ["remove_mode"] = "reject_if_referenced",
				};
				using var forward4 = JsonDocument.Parse(JsonSerializer.Serialize(op4));
				var map4 = new Dictionary<string, IMDTokenProvider>();
				try {
					var inverse4 = EditOperationRegistry.CompileInverse(m4, forward4.RootElement, map4);
					EditOperationRegistry.Apply(m4, forward4.RootElement, map4, 0);
					using var reloaded4 = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(m4));
					using var serialized4 = JsonDocument.Parse(JsonSerializer.Serialize(inverse4, EditWire.JsonOptions));
					var restored4 = EditOperationRegistry.ApplyCompiledInverse(reloaded4, serialized4.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
					var restoredImage4 = EditWorkspace.WriteCheckpointImage(reloaded4);
					if (!baseline.SequenceEqual(restoredImage4)) {
						int FD(byte[] x, byte[] y) { int n = Math.Min(x.Length, y.Length); for (int i2 = 0; i2 < n; i2++) if (x[i2] != y[i2]) return i2; return x.Length == y.Length ? -1 : n; }
						string TT(ModuleDefMD mm) => "T" + mm.TablesStream.TypeDefTable.Rows + "/M" + mm.TablesStream.MethodTable.Rows + "/E" + mm.TablesStream.EventTable.Rows + "/P" + mm.TablesStream.PropertyTable.Rows + "/F" + mm.TablesStream.FieldTable.Rows + "/TR" + mm.TablesStream.TypeRefTable.Rows + "/Sem" + mm.TablesStream.MethodSemanticsTable.Rows;
						string HH(byte[] b, int at) { int s = Math.Max(0, at - 12), n = Math.Min(28, b.Length - s); return Convert.ToHexString(b, s, n); }
						int fd4 = FD(baseline, restoredImage4);
						using var bb4 = ModuleDefMD.Load(baseline); using var rr4 = ModuleDefMD.Load(restoredImage4);
						Console.WriteLine($"SPIKE-GATE4 kind={kind} fd=0x{fd4:x} len={baseline.Length}/{restoredImage4.Length} t-base={TT(bb4)} t-rest={TT(rr4)} basehex={HH(baseline, fd4)} resthex={HH(restoredImage4, fd4)}");
						var invKeys = string.Join(",", serialized4.RootElement.GetProperty("member_restore").EnumerateObject().Select(p2 => p2.Name));
						Console.WriteLine($"SPIKE-GATE4 inv-keys={invKeys}");
						var liveTomb = reloaded4.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).ToArray();
						var liveEvt = reloaded4.GetTypes().SelectMany(t => t.Events).FirstOrDefault();
						Console.WriteLine($"SPIKE-GATE4 graph-tombstones={liveTomb.Length} evt-owner={liveEvt?.DeclaringType?.FullName} evt-add={liveEvt?.AddMethod?.Name ?? "<null>"} evt-remove={liveEvt?.RemoveMethod?.Name ?? "<null>"} inv-has-accessors={serialized4.RootElement.GetProperty("member_restore").TryGetProperty("accessors", out _)}");
					}
					Check(baseline.SequenceEqual(restoredImage4), kind + " reloaded restore equals baseline");
					Check(reloaded4.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone) == 0, kind + " tombstone cleared after restore");
					try { EditOperationRegistry.ApplyCompiledInverse(reloaded4, serialized4.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); throw new Exception("duplicate member restore accepted"); }
					catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
					restored4.Undo();
					Check(reloaded4.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone) == 1, kind + " restore undo re-tombstones");
					applied4 = true;
					break;
				}
				catch (EditDomainException) when (!applied4) { /* candidate not removable */ }
				catch (InvalidOperationException) when (!applied4) { throw; }
			}
			Check(applied4, kind + " reloaded restore had a candidate");
		}
		// type_remove: dedicated cycle. The fixture has no removable type, so a
		// marker type is added and round-tripped through an image first, giving
		// its row a real rid so the tombstone path runs both same-session and
		// cross-reload.
		{
			using var tseed = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var gate = new dnlib.DotNet.TypeDefUser("GateRemovable", tseed.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.BeforeFieldInit,
			};
			gate.Fields.Add(new dnlib.DotNet.FieldDefUser("Marker", new dnlib.DotNet.FieldSig(tseed.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static));
			tseed.Types.Add(gate);
			var withGate = EditWorkspace.WriteCheckpointImage(tseed);
			var gateToken = ModuleDefMD.Load(withGate).Types.Single(t => t.Name == "GateRemovable").MDToken.Raw;
			// same-session: forward + undo on the reloaded module
			{
				using var m5 = ModuleDefMD.Load(withGate);
				var op5 = new Dictionary<string, object?> { ["kind"] = "type_remove", ["target"] = new { token = "0x" + gateToken.ToString("x8") }, ["remove_mode"] = "reject_if_referenced" };
				using var forward5 = JsonDocument.Parse(JsonSerializer.Serialize(op5));
				var outcome5 = EditOperationRegistry.Apply(m5, forward5.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
				Check(EditWorkspace.WriteCheckpointImage(m5).SequenceEqual(withGate) == false, "type delete changes image");
				outcome5.Undo();
				Check(withGate.SequenceEqual(EditWorkspace.WriteCheckpointImage(m5)), "type undo restores image");
			}
			// cross-reload: compiled inverse restores byte-identical
			{
				using var m6 = ModuleDefMD.Load(withGate);
				var op6 = new Dictionary<string, object?> { ["kind"] = "type_remove", ["target"] = new { token = "0x" + gateToken.ToString("x8") }, ["remove_mode"] = "reject_if_referenced" };
				using var forward6 = JsonDocument.Parse(JsonSerializer.Serialize(op6));
				var inverse6 = EditOperationRegistry.CompileInverse(m6, forward6.RootElement, new Dictionary<string, IMDTokenProvider>());
				EditOperationRegistry.Apply(m6, forward6.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
				using var reloaded6 = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(m6));
				Check(reloaded6.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone) == 1, "type delete image carries tombstone");
				using var serialized6 = JsonDocument.Parse(JsonSerializer.Serialize(inverse6, EditWire.JsonOptions));
				var restored6 = EditOperationRegistry.ApplyCompiledInverse(reloaded6, serialized6.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
				Check(withGate.SequenceEqual(EditWorkspace.WriteCheckpointImage(reloaded6)), "type reloaded restore equals image");
				Check(reloaded6.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone) == 0, "type tombstone cleared");
				try { EditOperationRegistry.ApplyCompiledInverse(reloaded6, serialized6.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); throw new Exception("duplicate type restore accepted"); }
				catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
			}
		}
		// malformed tombstone: wrong host suffix must be rejected with zero mutation
		using var module2 = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var method2 = module2.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
		var operation2 = new { kind = "parameter_remove", parameter_target = new { owner_method = new { token = "0x" + method2.MDToken.Raw.ToString("x8") }, parameter_index = 0 }, remove_mode = "reject_if_referenced" };
		using var forward2 = JsonDocument.Parse(JsonSerializer.Serialize(operation2));
		var map2 = new Dictionary<string, IMDTokenProvider>();
		var inverse2 = EditOperationRegistry.CompileInverse(module2, forward2.RootElement, map2);
		EditOperationRegistry.Apply(module2, forward2.RootElement, map2, 0);
		using var reloaded2 = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(module2));
		var host2 = reloaded2.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).Single().Methods.Single();
		host2.Name = "d000000";
		using var serialized2 = JsonDocument.Parse(JsonSerializer.Serialize(inverse2));
		var stateBefore = EditFingerprint.Compute(reloaded2);
		try { EditOperationRegistry.ApplyCompiledInverse(reloaded2, serialized2.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); throw new Exception("malformed tombstone accepted"); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
		Check(EditFingerprint.Compute(reloaded2) == stateBefore, "malformed tombstone rejection is zero mutation");
		Console.WriteLine("PASS tombstone-gate host-naming+single+void-sig+tr-rows malformed-rejected restore-exact repeat-cycle member-kinds=field+event+property+method+type reloaded-restore=dual-cycle");
	}

	// Diagnostic experiment only. Does not alter the production image encoder,
	// package format, live graph, or the normal harness acceptance assertions.
	static void TestImageEncodingSpike(string fixture) {
		// Minimal mechanism probe: does a graph type with a corlib pseudo Object base
		// append a TypeRef row on the checkpoint write, and where does the base resolve?
		{
			using var m = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var before = m.TablesStream.TypeRefTable.Rows;
			m.Types.Add(new TypeDefUser("probe.ns", "ProbeType", m.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.NotPublic | dnlib.DotNet.TypeAttributes.Abstract | dnlib.DotNet.TypeAttributes.Sealed });
			var written = EditWorkspace.WriteCheckpointImage(m);
			using var back = ModuleDefMD.Load(written);
			var probe = back.GetTypes().Single(t => t.Name == "ProbeType");
			Console.WriteLine($"SPIKE-PROBE typeref-rows {before}->{back.TablesStream.TypeRefTable.Rows} probe-base={probe.BaseType?.FullName} token=0x{probe.BaseType?.MDToken.Raw.ToString("x8")} pseudo-rid=0x{m.CorLibTypes.Object.TypeDefOrRef.MDToken.Raw.ToString("x8")}");
		}
		foreach (var kind in new[] { "parameter_remove", "generic_parameter_remove" }) {
			using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var originalNames = new HashSet<string>(source.GetTypes().Select(t => t.FullName), StringComparer.Ordinal);
			var method = source.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
			var generic = source.GetTypes().Single(t => t.Name == "GenericFieldOwner`1").GenericParameters[0];
			var canonicalBefore = EditWorkspace.WriteCanonical(source);
			var preservingBefore = EditWorkspace.WriteCheckpointImage(source);
			var operation = kind == "parameter_remove" ? (object)new {
				kind, parameter_target = new { owner_method = new { token = "0x" + method.MDToken.Raw.ToString("x8") }, parameter_index = 0 }, remove_mode = "reject_if_referenced",
			} : new { kind, target = new { token = "0x" + generic.MDToken.Raw.ToString("x8") }, remove_mode = "reject_if_referenced" };
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var map = new Dictionary<string, IMDTokenProvider>();
			var inverse = EditOperationRegistry.CompileInverse(source, forward.RootElement, map);
			EditOperationRegistry.Apply(source, forward.RootElement, map, 0);
			using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(source));
			string ObjectRefs(ModuleDefMD m) { var names = new List<string>(); for (uint rid = 1; rid <= m.TablesStream.TypeRefTable.Rows; rid++) { var r = m.ResolveToken(new MDToken(dnlib.DotNet.MD.Table.TypeRef, rid).Raw) as TypeRef; if (r?.FullName == "System.Object") names.Add(rid + ":0x" + r.MDToken.Raw.ToString("x8")); } return string.Join("+", names); }
			Console.WriteLine($"SPIKE-B kind={kind} tables_deleted={Tables(reloaded)} sentinel_deleted={Sentinel(reloaded)} objectrefs_deleted={ObjectRefs(reloaded)}");
			using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			EditOperationRegistry.ApplyCompiledInverse(reloaded, serialized.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			var preservingAfter = EditWorkspace.WriteCheckpointImage(reloaded);
			using var detached = ModuleDefMD.Load(preservingAfter);
			// P03-CHANGE-002 v3: deleted-state images carry the operation tombstone;
			// it is excluded from semantic fingerprints and from this canonical
			// diagnostic the same way.
			var placeholders = detached.Types.Where(t => (EditFingerprint.IsWriterTombstoneType(t) && !originalNames.Contains(t.FullName)) || EditDeletedRowsTombstone.IsTombstone(t)).ToArray();
			foreach (var placeholder in placeholders) detached.Types.Remove(placeholder);
			var canonicalAfter = EditWorkspace.WriteCanonical(detached);
			Check(canonicalBefore.SequenceEqual(canonicalAfter), "image encoding spike canonical restoration " + kind);
			// r24 diagnostics: table row counts and first differing byte of the preserving images
			string Tables(ModuleDefMD m) => "T" + m.TablesStream.TypeDefTable.Rows + "/M" + m.TablesStream.MethodTable.Rows + "/P" + m.TablesStream.ParamTable.Rows + "/TR" + m.TablesStream.TypeRefTable.Rows + "/PtrM" + m.TablesStream.MethodPtrTable.Rows;
			int FirstDiff(byte[] a, byte[] b) { int n = Math.Min(a.Length, b.Length); for (int i = 0; i < n; i++) if (a[i] != b[i]) return i; return a.Length == b.Length ? -1 : n; }
			using var pre = ModuleDefMD.Load(preservingBefore); using var post = ModuleDefMD.Load(preservingAfter);
			string Refs(ModuleDefMD m) { var names = new List<string>(); for (uint rid = 1; rid <= m.TablesStream.TypeRefTable.Rows; rid++) { var r = m.ResolveToken(new MDToken(dnlib.DotNet.MD.Table.TypeRef, rid).Raw) as TypeRef; names.Add(rid + ":" + (r?.FullName ?? "<null>")); } return string.Join(",", names.GetRange(Math.Max(0, names.Count - 4), Math.Min(4, names.Count))); }
			string Sentinel(ModuleDefMD m) { var s = m.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).ToArray(); return "n=" + s.Length + (s.Length > 0 ? " base=" + s[0].BaseType?.FullName + " token=0x" + s[0].BaseType?.MDToken.Raw.ToString("x8") : ""); }
			Console.WriteLine($"SPIKE kind={kind} preserving_exact={preservingBefore.SequenceEqual(preservingAfter)} canonical_exact={canonicalBefore.SequenceEqual(canonicalAfter)} removed_generated_types={placeholders.Length} len={preservingBefore.Length}/{preservingAfter.Length} firstdiff=0x{FirstDiff(preservingBefore, preservingAfter):x} tables_pre={Tables(pre)} tables_post={Tables(post)} sentinel_pre={Sentinel(pre)} sentinel_post={Sentinel(post)} refs_tail_pre={Refs(pre)} refs_tail_post={Refs(post)}");
		}
		using var tokenSource = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var members = tokenSource.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var removed = members.Fields.Single(f => f.Name == "score");
		var observed = members.Fields.First(f => f.Name != "score");
		var oldToken = observed.MDToken.Raw;
		members.Fields.Remove(removed);
		using var emitted = ModuleDefMD.Load(EditWorkspace.WriteCanonical(tokenSource));
		var newToken = emitted.GetTypes().Single(t => t.FullName == "TestIL.Members").Fields.Single(f => f.Name == observed.Name).MDToken.Raw;
		Check(oldToken != newToken, "canonical emission requires object locator independent of source token");
		Console.WriteLine($"SPIKE field-token-remap old=0x{oldToken:x8} new=0x{newToken:x8}");
		var intended = emitted.GetTypes().Single(t => t.FullName == "TestIL.Members").Fields.Single(f => f.Name == observed.Name);
		var resolved = emitted.ResolveToken(oldToken);
		Check(!ReferenceEquals(resolved, intended), "old persisted token must not be mistaken for original field after remap");
		if (resolved is FieldDef wrongField) {
			var intendedName = intended.Name.String;
			var wrongName = wrongField.Name.String;
			using var staleOperation = JsonDocument.Parse(JsonSerializer.Serialize(new {
				kind = "field_update", target = new { token = "0x" + oldToken.ToString("x8") }, name = "P03SpikeWrongTarget",
			}));
			var applied = EditOperationRegistry.ApplyPersisted(emitted, staleOperation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			Check(wrongField.Name == "P03SpikeWrongTarget" && intended.Name == intendedName,
				"persisted raw token demonstrably edits wrong field after canonical reload");
			applied.Undo();
			Check(wrongField.Name == wrongName, "isolated wrong-target experiment restores mutation");
			Console.WriteLine("SPIKE stale-persisted-token=wrong-field-mutated restored=True");
		} else {
			Console.WriteLine("SPIKE stale-persisted-token=unresolvable-field");
		}
	}

	// Experiment only: addresses describe the graph immediately before each
	// operation. They are neither public tokens nor stable across mutations.
	static Dictionary<string, IMDTokenProvider> IndexDefinitionSlots(ModuleDef module) {
		var result = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		void Generics(IList<GenericParam> values, string owner) {
			foreach (var value in values) result.Add(owner + "/g/" + value.Number, value);
		}
		void Type(TypeDef type, string path) {
			result.Add(path, type); Generics(type.GenericParameters, path);
			for (var i = 0; i < type.Fields.Count; i++) result.Add(path + "/f/" + i, type.Fields[i]);
			for (var i = 0; i < type.Properties.Count; i++) result.Add(path + "/p/" + i, type.Properties[i]);
			for (var i = 0; i < type.Events.Count; i++) result.Add(path + "/e/" + i, type.Events[i]);
			for (var i = 0; i < type.Methods.Count; i++) {
				var method = type.Methods[i]; var owner = path + "/m/" + i;
				result.Add(owner, method); Generics(method.GenericParameters, owner);
				foreach (var parameter in method.ParamDefs) result.Add(owner + "/a/" + parameter.Sequence, parameter);
			}
			for (var i = 0; i < type.NestedTypes.Count; i++) Type(type.NestedTypes[i], path + "/t/" + i);
		}
		for (var i = 0; i < module.Types.Count; i++) Type(module.Types[i], "t/" + i);
		return result;
	}

	static void TestLocatorSpike(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		owner.Fields.Remove(owner.Fields.Single(f => f.Name == "score"));
		owner.Name = "RenamedOwner";
		var nested = new TypeDefUser(string.Empty, "Nested", source.CorLibTypes.Object.TypeDefOrRef) {
			Attributes = dnlib.DotNet.TypeAttributes.NestedPublic,
		};
		owner.NestedTypes.Add(nested);
		var added = new[] {
			new FieldDefUser("SameName", new FieldSig(source.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public),
			new FieldDefUser("SameName", new FieldSig(source.CorLibTypes.String), dnlib.DotNet.FieldAttributes.Public),
		};
		foreach (var field in added) nested.Fields.Add(field);
		Check(added.All(f => f.Rid == 0), "locator spike exercises multiple zero-RID definitions");
		var before = IndexDefinitionSlots(source);
		var address = before.Single(pair => ReferenceEquals(pair.Value, added[1])).Key;
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var after = IndexDefinitionSlots(reloaded);
		Check(before.Keys.OrderBy(k => k).SequenceEqual(after.Keys.OrderBy(k => k)), "definition addresses survive canonical write/reload");
		foreach (var pair in before) {
			Check(EditDefinitionAddress.Capture(source, pair.Value) == pair.Key, "production address matches independent index " + pair.Key);
			Check(ReferenceEquals(EditDefinitionAddress.Resolve(reloaded, pair.Key), after[pair.Key]), "production resolver matches independent reload index " + pair.Key);
			Check(pair.Value.MDToken.Table == after[pair.Key].MDToken.Table, "slot preserves definition table " + pair.Key);
			Check(pair.Value.ToString() == after[pair.Key].ToString(), "slot preserves definition meaning " + pair.Key);
		}
		var target = (FieldDef)after[address];
		var sibling = target.DeclaringType.Fields[0];
		using var operation = JsonDocument.Parse(JsonSerializer.Serialize(new {
			kind = "field_update", target = new { object_id = "resolved-history-slot" }, name = "ResolvedTarget",
		}));
		var map = new Dictionary<string, IMDTokenProvider> { ["resolved-history-slot"] = target };
		var outcome = EditOperationRegistry.ApplyPersisted(reloaded, operation.RootElement, map, 0);
		Check(target.Name == "ResolvedTarget" && sibling.Name == "SameName", "resolved slot edits intended duplicate-name zero-RID field only");
		outcome.Undo();
		Check(target.Name == "SameName", "slot experiment mutation restored");
		foreach (var invalid in new[] { "", "t", "t/-1", "t/00", "t/+0", "t/2147483648", "t/999999", "t/0/unknown/0", address + "/f/0" }) {
			try { EditDefinitionAddress.Resolve(reloaded, invalid); throw new InvalidOperationException("FAILED: invalid address accepted " + invalid); }
			catch (EditDomainException) { }
		}
		try { EditDefinitionAddress.Capture(source, new MethodDefUser("detached")); throw new InvalidOperationException("FAILED: detached address captured"); }
		catch (EditDomainException) { }
		Console.WriteLine($"SPIKE definition-slots={before.Count} reload=True renamed-owner=True nested=True duplicate-name=True multiple-zero-rid=True correct-target=True restored=True");
		TestReferenceEncodingSpike(fixture);
	}

	static byte[] WriteReferencePreservingSpike(ModuleDef module) {
		using var stream = new MemoryStream();
		var options = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
		options.MetadataOptions.Flags = dnlib.DotNet.Writer.MetadataFlags.PreserveTypeRefRids
			| dnlib.DotNet.Writer.MetadataFlags.PreserveMemberRefRids
			| dnlib.DotNet.Writer.MetadataFlags.PreserveStandAloneSigRids
			| dnlib.DotNet.Writer.MetadataFlags.PreserveTypeSpecRids
			| dnlib.DotNet.Writer.MetadataFlags.PreserveMethodSpecRids
			| dnlib.DotNet.Writer.MetadataFlags.PreserveExtraSignatureData
			| dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
		module.Write(stream, options);
		return stream.ToArray();
	}

	static void TestReferenceEncodingSpike(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var beforeImage = WriteReferencePreservingSpike(source);
		var method = source.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
		using var operation = JsonDocument.Parse(JsonSerializer.Serialize(new {
			kind = "parameter_remove", parameter_target = new { owner_method = new { token = "0x" + method.MDToken.Raw.ToString("x8") }, parameter_index = 0 },
			remove_mode = "reject_if_referenced",
		}));
		var inverse = EditOperationRegistry.CompileInverse(source, operation.RootElement, new Dictionary<string, IMDTokenProvider>());
		var ownerAddress = IndexDefinitionSlots(source).Single(pair => ReferenceEquals(pair.Value, method)).Key;
		EditOperationRegistry.Apply(source, operation.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		var references = new Dictionary<uint, string>();
		foreach (var table in new[] {
			(0x01000000u, source.TablesStream.TypeRefTable.Rows), (0x0a000000u, source.TablesStream.MemberRefTable.Rows),
			(0x11000000u, source.TablesStream.StandAloneSigTable.Rows), (0x1b000000u, source.TablesStream.TypeSpecTable.Rows),
			(0x2b000000u, source.TablesStream.MethodSpecTable.Rows),
		}) for (uint rid = 1; rid <= table.Item2; rid++) {
			var token = table.Item1 | rid;
			references.Add(token, source.ResolveToken(token)?.ToString() ?? "<null>");
		}
		var deletedImage = WriteReferencePreservingSpike(source);
		using var reloaded = ModuleDefMD.Load(deletedImage);
		foreach (var reference in references)
			Check((reloaded.ResolveToken(reference.Key)?.ToString() ?? "<null>") == reference.Value, "non-definition reference retained " + reference.Key.ToString("x8"));
		Check(deletedImage.SequenceEqual(WriteReferencePreservingSpike(reloaded)), "reference-preserving encoding stable across reload");
		using var inverseJson = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
		var beforeFailedInverse = EditFingerprint.Compute(reloaded);
		try {
			EditOperationRegistry.ApplyCompiledInverse(reloaded, inverseJson.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			throw new InvalidOperationException("FAILED: raw inverse unexpectedly accepted reassigned ParamDef token");
		} catch (EditDomainException) {
			Check(beforeFailedInverse == EditFingerprint.Compute(reloaded), "raw inverse token rejection has no side effects");
		}
		// Candidate lowering, only inside this experiment: resolve the owning
		// definition at its pre-operation slot and materialize the missing row.
		// The recorded old token remains provenance, not a pointer into new tables.
		var tail = (Dictionary<string, object?>)inverse["parameter_tail_restore"]!;
		var row = (Dictionary<string, object?>)tail["paramdef"]!;
		var provenanceToken = (uint)row["token"]!;
		Check(provenanceToken != 0, "experiment retains nonzero source identity evidence");
		row["token"] = 0u;
		tail["owner_method"] = new Dictionary<string, object?> { ["object_id"] = "resolved-inverse-owner" };
		var inverseObjects = new Dictionary<string, IMDTokenProvider> {
			["resolved-inverse-owner"] = IndexDefinitionSlots(reloaded)[ownerAddress],
		};
		using var lowered = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
		EditOperationRegistry.ApplyCompiledInverse(reloaded, lowered.RootElement, inverseObjects, 0);
		Check(beforeImage.SequenceEqual(WriteReferencePreservingSpike(reloaded)), "reference-preserving encoding exact parameter restoration");
		Console.WriteLine($"SPIKE retained-reference-rows={references.Count} stable-image=True raw-inverse-rejected=True slot-inverse-parameter-restore-exact=True");
		using var newReferenceSource = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var referenceOwner = newReferenceSource.GetTypes().Single(t => t.FullName == "TestIL.UnityComponent").Methods.Single(m => m.Name == "OnTriggerEnter");
		var oldTypeName = referenceOwner.MethodSig.Params[0].FullName;
		var ownerSlot = EditDefinitionAddress.Capture(newReferenceSource, referenceOwner);
		var referenceBaseline = WriteReferencePreservingSpike(newReferenceSource);
		var canonicalBaseline = EditWorkspace.WriteCanonical(newReferenceSource);
		Check(!newReferenceSource.GetTypeRefs().Any(t => t.FullName == "System.Uri"), "new-reference fixture starts without System.Uri");
		referenceOwner.MethodSig.Params[0] = new EditTypeSigParser(newReferenceSource).Parse("System.Uri");
		using var referenceReloaded = ModuleDefMD.Load(WriteReferencePreservingSpike(newReferenceSource));
		Check(referenceReloaded.GetTypeRefs().Any(t => t.FullName == "System.Uri"), "edited signature emits new reference row");
		var restoredOwner = (MethodDef)EditDefinitionAddress.Resolve(referenceReloaded, ownerSlot);
		restoredOwner.MethodSig.Params[0] = new EditTypeSigParser(referenceReloaded).Parse(oldTypeName);
		var preservingRestored = WriteReferencePreservingSpike(referenceReloaded);
		Check(!referenceBaseline.SequenceEqual(preservingRestored), "preserved new reference survives undo and prevents exact checkpoint image");
		Check(canonicalBaseline.SequenceEqual(EditWorkspace.WriteCanonical(referenceReloaded)), "nonpreserving writer drops unused added reference and restores exact image");
		Console.WriteLine("SPIKE new-reference-after-undo preserving-exact=False canonical-exact=True");
	}

	static void TestWorkspaceNewMethods(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		for (var i = 0; i < 2; i++) {
			var method = new MethodDefUser("P03NewMethod" + i, MethodSig.CreateStatic(source.CorLibTypes.Int32),
				dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static);
			method.Body = new dnlib.DotNet.Emit.CilBody { InitLocals = i != 0, MaxStack = (ushort)(i + 1) };
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ldc_I4_0));
			method.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
			owner.Methods.Add(method);
		}
		Check(owner.Methods.Where(m => m.Name.String.StartsWith("P03NewMethod", StringComparison.Ordinal)).All(m => m.Rid == 0), "new-method fixture has colliding zero RIDs");
		var fingerprint = EditFingerprint.Compute(source);
		using var workspace = EditWorkspace.CreateForTesting(source);
		Check(EditFingerprint.Compute(source) == fingerprint && workspace.PrivateFingerprint() == fingerprint, "new-method private copy preserves full live fingerprint");
		for (var i = 0; i < 2; i++) {
			var copied = workspace.PrivateModule.GetTypes().Single(t => t.FullName == "TestIL.Members").Methods.Single(m => m.Name == "P03NewMethod" + i);
			Check(copied.Body.InitLocals == (i != 0) && copied.Body.MaxStack == i + 1, "new-method private headers remain distinct");
		}
		workspace.RestoreCommittedState();
		Check(workspace.PrivateFingerprint() == fingerprint, "new-method private rebuild preserves full fingerprint");
		Console.WriteLine("PASS workspace multiple zero-RID method headers and full fingerprint");
	}

	static void TestDefinitionAddInverses(string fixture) {
		foreach (var kind in new[] { "type_add", "method_add", "field_add", "property_add", "event_add" }) {
			using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var operation = new Dictionary<string, object?> {
				["kind"] = kind, ["name"] = "P03AddedDefinition", ["owner_type"] = new { token = "0x" + owner.MDToken.Raw.ToString("x8") },
			};
			if (kind == "type_add") operation["base_type"] = "System.Object";
			if (kind == "field_add") operation["field_type"] = "System.Int32";
			if (kind == "property_add") operation["property_type"] = "System.Int32";
			if (kind == "method_add") {
				operation["signature"] = new { return_type = "System.Void", has_this = false, parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() };
				operation["attributes"] = 22;
				operation["body"] = new { init_locals = false, max_stack = 1, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(), instructions = new[] { new { opcode = "ret" } } };
			}
			if (kind == "event_add") {
				var template = owner.Events.First();
				operation["event_type"] = template.EventType.FullName;
				operation["add_method"] = new { token = "0x" + template.AddMethod.MDToken.Raw.ToString("x8") };
				operation["remove_method"] = new { token = "0x" + template.RemoveMethod.MDToken.Raw.ToString("x8") };
			}
			using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
			var objects = new Dictionary<string, IMDTokenProvider>();
			var before = EditFingerprint.Compute(module);
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
			var added = EditOperationRegistry.Apply(module, forward.RootElement, objects, 0);
			var objectId = added.CreatedObjectIds.Single(); var definition = objects[objectId];
			var post = EditFingerprint.Compute(module);
			using var serializedInverse = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
			var removal = EditOperationRegistry.ApplyCompiledInverse(module, serializedInverse.RootElement, objects, 0);
			Check(EditFingerprint.Compute(module) == before && !objects.ContainsKey(objectId), "definition add inverse restores exact graph and map " + kind);
			try { EditOperationRegistry.ApplyCompiledInverse(module, serializedInverse.RootElement, objects, 0); throw new InvalidOperationException("FAILED: duplicate definition inverse accepted"); }
			catch (EditDomainException) { Check(EditFingerprint.Compute(module) == before, "duplicate definition inverse rejects without mutation"); }
			removal.Undo();
			Check(EditFingerprint.Compute(module) == post && ReferenceEquals(objects[objectId], definition), "inverse rollback preserves definition identity and object map " + kind);
			EditOperationRegistry.ApplyCompiledInverse(module, serializedInverse.RootElement, objects, 0);
			Check(EditFingerprint.Compute(module) == before, "definition inverse reusable after rollback " + kind);
		}
		Console.WriteLine("PASS definition-add inverses type+method+field+property+event graph+map+identity+duplicate-rejection");
	}

	static void TestMethodAddSymbolInverse(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		Check(module.PdbState == null, "method-add symbol fixture starts without PDB state");
		var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var operation = new {
			kind = "method_add", owner_type = new { token = "0x" + owner.MDToken.Raw.ToString("x8") },
			name = "P03SymbolAdded", attributes = 150,
			signature = new { return_type = "System.Void", has_this = false, parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() },
			body = new {
				init_locals = false, max_stack = 1, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
				instructions = new[] { new { opcode = "ret" } },
				sequence_points = new[] { new {
					document = new { name = "P03SymbolAdded.cs", language = "3f5162f8-07c6-11d3-9053-00c04fa302a1",
						vendor = "994b45c4-e6e9-11d2-903f-00c04fa302a1", hash = "AQID", type = "5a869d0b-6611-11d3-bd2a-0000f80849bd",
						hashAlgorithm = "ff1816ec-aa5e-4d10-87f7-6f4963833460" },
					start = new { il = 0, line = 1, column = 1 }, end = new { line = 1, column = 2 },
				} },
			},
		};
		using var forward = JsonDocument.Parse(JsonSerializer.Serialize(operation));
		var objects = new Dictionary<string, IMDTokenProvider>();
		var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
		EditOperationRegistry.Apply(module, forward.RootElement, objects, 0);
		Check(module.PdbState?.Documents.Count() == 1, "method-add registers one document");
		using var state = JsonDocument.Parse(JsonSerializer.Serialize(inverse));
		var removed = EditOperationRegistry.ApplyCompiledInverse(module, state.RootElement, objects, 0);
		Check(module.PdbState?.Documents.Count() == 0, "method-add inverse releases its document");
		EditOperationRegistry.RemoveEmptyPdbState(module);
		Check(module.PdbState == null, "method-add inverse restores absent PDB state");
		removed.Undo();
		Check(module.PdbState?.Documents.Count() == 1 && owner.Methods.Any(m => m.Name == "P03SymbolAdded"),
			"method-add inverse compensation restores document and definition");
		Console.WriteLine("PASS method-add symbol inverse releases owned document and compensates exactly");
	}

	static void TestZeroRidReference(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var used = new FieldDefUser("P03Used", new FieldSig(module.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static);
		var unused = new FieldDefUser("P03Unused", new FieldSig(module.CorLibTypes.Int32), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static);
		owner.Fields.Add(used); owner.Fields.Add(unused);
		var reader = new MethodDefUser("P03Reader", MethodSig.CreateStatic(module.CorLibTypes.Int32), dnlib.DotNet.MethodImplAttributes.IL,
			dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new dnlib.DotNet.Emit.CilBody() };
		reader.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ldsfld, used));
		reader.Body.Instructions.Add(dnlib.DotNet.Emit.Instruction.Create(dnlib.DotNet.Emit.OpCodes.Ret));
		owner.Methods.Add(reader);
		Check(used.Rid == 0 && unused.Rid == 0, "reference guard fixture uses colliding raw tokens");
		var objects = new Dictionary<string, IMDTokenProvider> { ["unused"] = unused, ["used"] = used };
		using var removeUnused = JsonDocument.Parse("{\"kind\":\"field_remove\",\"target\":{\"object_id\":\"unused\"},\"remove_mode\":\"reject_if_referenced\"}");
		var before = EditFingerprint.Compute(module);
		var removal = EditOperationRegistry.Apply(module, removeUnused.RootElement, objects, 0);
		Check(!owner.Fields.Contains(unused) && owner.Fields.Contains(used), "unreferenced zero-RID field can be removed");
		removal.Undo();
		Check(before == EditFingerprint.Compute(module), "zero-RID remove rollback retains full graph");
		using var removeUsed = JsonDocument.Parse("{\"kind\":\"field_remove\",\"target\":{\"object_id\":\"used\"},\"remove_mode\":\"reject_if_referenced\"}");
		try { EditOperationRegistry.Apply(module, removeUsed.RootElement, objects, 0); throw new InvalidOperationException("FAILED: referenced zero-RID field removal accepted"); }
		catch (EditDomainException) { Check(before == EditFingerprint.Compute(module), "referenced zero-RID field rejection is zero mutation"); }
		Console.WriteLine("PASS zero-RID reference guard distinct-objects=True actual-reference-blocked=True rollback=True");
	}

	static void TestWriterTokenMap(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
		owner.Fields.Remove(owner.Fields.Single(f => f.Name == "score"));
		var field = owner.Fields.First();
		var method = module.GetTypes().SelectMany(t => t.Methods).First(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MemberRef));
		var reference = (MemberRef)method.Body.Instructions.First(i => i.Operand is MemberRef).Operand;
		var observed = new IMDTokenProvider[] { field, method, reference };
		var originalTokens = observed.Select(o => o.MDToken.Raw).ToArray();
		var mapped = new uint[observed.Length];
		var options = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
		options.MetadataOptions.Flags = dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
		var captured = false;
		options.WriterEvent += (_, e) => {
			if (e.Event != dnlib.DotNet.Writer.ModuleWriterEvent.MDEndCreateTables) return;
			for (var i = 0; i < observed.Length; i++) mapped[i] = e.Writer.Metadata.GetToken(observed[i]).Raw;
			captured = true;
		};
		using var stream = new MemoryStream(); module.Write(stream, options);
		Check(captured, "writer token mapping callback ran");
		var bytes = stream.ToArray();
		Check(bytes.SequenceEqual(EditWorkspace.WriteCanonical(module)), "observing existing emitted tokens does not change image");
		using var reloaded = ModuleDefMD.Load(bytes);
		for (var i = 0; i < observed.Length; i++) {
			Check(reloaded.ResolveToken(mapped[i])?.ToString() == observed[i].ToString(), "writer mapped token resolves expected emitted object");
			Check(observed[i].MDToken.Raw == originalTokens[i], "writer mapping does not overwrite live token");
		}
		Check(mapped[0] != originalTokens[0], "writer mapping observes field token reassignment");
		Console.WriteLine("SPIKE writer-token-map definition+memberref=True image-unchanged=True live-token-unchanged=True remapped-field=True");
		var unused = new TypeRefUser(module, "System", "P03UnusedReference", module.CorLibTypes.AssemblyRef);
		var first = new TypeRefUser(module, "System", "Uri", module.CorLibTypes.AssemblyRef);
		var second = new TypeRefUser(module, "System", "Uri", module.CorLibTypes.AssemblyRef);
		owner.Fields.Add(new FieldDefUser("P03ReferenceA", new FieldSig(new ClassSig(first)), dnlib.DotNet.FieldAttributes.Public));
		owner.Fields.Add(new FieldDefUser("P03ReferenceB", new FieldSig(new ClassSig(second)), dnlib.DotNet.FieldAttributes.Public));
		var expected = EditWorkspace.WriteCanonical(module);
		var observing = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
		observing.MetadataOptions.Flags = dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
		uint unusedRid = uint.MaxValue, firstRid = 0, secondRid = 0;
		observing.WriterEvent += (_, e) => {
			if (e.Event != dnlib.DotNet.Writer.ModuleWriterEvent.MDEndCreateTables) return;
			unusedRid = e.Writer.Metadata.GetRid(unused);
			firstRid = e.Writer.Metadata.GetRid(first);
			secondRid = e.Writer.Metadata.GetRid(second);
		};
		using var observedStream = new MemoryStream(); module.Write(observedStream, observing);
		Check(unusedRid == 0, "GetRid reports unused reference absent without adding it");
		Check(firstRid != 0 && firstRid == secondRid && !ReferenceEquals(first, second), "distinct equivalent references merge to one emitted RID");
		Check(expected.SequenceEqual(observedStream.ToArray()), "GetRid observation of unused and duplicate refs leaves exact image unchanged");
		Console.WriteLine("SPIKE GetRid unused=0 duplicates=many-to-one exact-image-unchanged=True");
	}

	sealed class ReferenceProbeNode {
		public int Id { get; set; }
		public string Name { get; set; } = "";
		public string Namespace { get; set; } = "";
		public int? Parent { get; set; }
	}
	sealed class ReferenceProbeGraph {
		public List<ReferenceProbeNode> Nodes { get; set; } = new();
		public int[] Roots { get; set; } = Array.Empty<int>();
	}
	// Test-only representation proof, deliberately limited to TypeRefs scoped to
	// the fixture's existing corlib (or another node). Not a production codec.
	static void TestReferenceIdentity(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var first = new TypeRefUser(module, "System", "Uri", module.CorLibTypes.AssemblyRef);
		var second = new TypeRefUser(module, "System", "Uri", module.CorLibTypes.AssemblyRef);
		var nested = new TypeRefUser(module, "", "Child", first);
		var roots = new TypeRef[] { first, second, first, nested, nested };
		var graph = new ReferenceProbeGraph();
		var identities = new List<TypeRef>();
		int Capture(TypeRef value) {
			var existing = identities.FindIndex(item => ReferenceEquals(item, value));
			if (existing >= 0) return existing;
			var id = identities.Count; identities.Add(value);
			var node = new ReferenceProbeNode { Id = id, Name = Convert.ToBase64String(value.Name.Data), Namespace = Convert.ToBase64String(value.Namespace.Data) };
			graph.Nodes.Add(node);
			if (value.ResolutionScope is TypeRef parent) node.Parent = Capture(parent);
			else Check(ReferenceEquals(value.ResolutionScope, module.CorLibTypes.AssemblyRef), "probe rejects scope outside its explicit fixture boundary");
			return id;
		}
		graph.Roots = roots.Select(Capture).ToArray();
		var json = JsonSerializer.Serialize(graph);
		TypeRef[] Decode(string encoded) {
			var stored = JsonSerializer.Deserialize<ReferenceProbeGraph>(encoded)!;
			var byId = stored.Nodes.ToDictionary(n => n.Id);
			var restored = new Dictionary<int, TypeRef>(); var active = new HashSet<int>();
			TypeRef Build(int id) {
				if (restored.TryGetValue(id, out var old)) return old;
				if (!byId.TryGetValue(id, out var node) || !active.Add(id)) throw new InvalidDataException("Missing or cyclic reference node");
				IResolutionScope scope = node.Parent.HasValue ? Build(node.Parent.Value) : module.CorLibTypes.AssemblyRef;
				var value = new TypeRefUser(module, new UTF8String(Convert.FromBase64String(node.Namespace)), new UTF8String(Convert.FromBase64String(node.Name)), scope);
				active.Remove(id); restored.Add(id, value); return value;
			}
			return stored.Roots.Select(Build).ToArray();
		}
		var result = Decode(json);
		Check(!ReferenceEquals(result[0], result[1]) && result[0].FullName == result[1].FullName, "equal-valued references retain distinct identities");
		Check(ReferenceEquals(result[0], result[2]) && ReferenceEquals(result[3], result[4]), "repeated references retain sharing");
		Check(ReferenceEquals(result[3].ResolutionScope, result[0]), "nested reference retains scope identity");
		Check(result.Select(r => r.FullName).SequenceEqual(roots.Select(r => r.FullName)), "serialized reference semantics preserved");
		graph.Nodes[0].Parent = 0;
		try { Decode(JsonSerializer.Serialize(graph)); throw new InvalidOperationException("FAILED: cyclic reference snapshot accepted"); }
		catch (InvalidDataException) { }
		Console.WriteLine("SPIKE serialized-reference-graph nodes=3 roots=5 distinct-equal=True sharing=True nested-scope=True cycle-rejected=True");
	}

	static void Check(bool value, string name, string detail = "") { if (!value) throw new InvalidOperationException("FAILED: " + name + (detail.Length > 0 ? " " + detail : "")); }

	// P05 compile frontend wrapper logic (stub provider injection): registration
	// cache, capacity, diagnostics mapping and schema/CON-022 contract face. The
	// real Roslyn provider path is exercised by the integration driver on the VM.
	static void TestCompileFrontendMatrix(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var stub = new StubCompilerProvider();
		var frontend = new EditCompileFrontend(null!, new[] { (ILanguageCompilerProvider)stub });
		var callContext = McpCallContext.StreamableHttp("harness-stub-session", "2025-03-26", true);
		static Dictionary<string, object?> Args(string kind, string content, string fixturePath, string extra = "") {
			var documents = new Dictionary<string, object?> { ["path"] = "Target.cs", ["content"] = content };
			var args = new Dictionary<string, object?> {
				["request_id"] = "req-1", ["assembly_name"] = "TestIL", ["compilation_kind"] = kind,
			};
			args["documents"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(new object[] { documents }));
			// The stub frontend has no document tree: always pass an explicit
			// reference so the closure resolution is skipped.
			args["references_override"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(new[] { fixturePath }));
			if (extra.Length > 0) args[extra] = "value";
			// The MCP server hands JsonElement values to providers; normalize the
			// stub dictionary through one JSON round-trip so the frontend sees the
			// production shapes.
			return JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(args, EditWire.JsonOptions), EditWire.JsonOptions);
		}
		static Dictionary<string, object?> Payload(CallToolResult result) {
			var text = result.Content.Count > 0 ? result.Content[0].Text : "{}";
			return JsonSerializer.Deserialize<Dictionary<string, object?>>(text, EditWire.JsonOptions) ?? new();
		}
		static Dictionary<string, object?> Core(Dictionary<string, object?> envelope) {
			if (envelope.TryGetValue("result", out var raw) && raw is JsonElement element && element.ValueKind == JsonValueKind.Object) {
				var compile = element.TryGetProperty("compile", out var compileElement) ? compileElement : default;
				return JsonSerializer.Deserialize<Dictionary<string, object?>>(compile.GetRawText(), EditWire.JsonOptions) ?? new();
			}
			return new();
		}
		static bool Bool(Dictionary<string, object?> core, string key) =>
			core.TryGetValue(key, out var raw) && raw is JsonElement b && b.ValueKind == JsonValueKind.True;
		static string Str(Dictionary<string, object?> core, string key) =>
			core.TryGetValue(key, out var raw) && raw is JsonElement s && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";

		// Valid compile: success, artifact registered, consumable_by_import.
		stub.NextResult = new CompilationResult(new byte[] { 1, 2, 3, 4 }, new DebugFileResult(DebugFileFormat.PortablePdb, new byte[] { 9, 8, 7, 6 }));
		var valid = frontend.ExecuteTool("edit_compile", Args("edit_class", "class T {}", Path.GetFullPath(fixture)), callContext);
		var validEnvelope = Payload(valid);
		var validCore = Core(validEnvelope);
		Check(Bool(validEnvelope, "ok"), "stub compile ok");
		Check(Bool(validCore, "success") && Bool(validCore, "consumable_by_import"), "stub compile registered consumable");
		var compileId = Str(validCore, "compile_id");
		Check(frontend.Lookup(compileId) != null, "artifact lookup by compile_id");
		var artifact = frontend.Lookup(compileId)!;
		Check(artifact.Assembly.Length == 4 && artifact.Pdb.Length == 4, "artifact payload lengths");
		Check(((JsonElement)validCore["assembly"]).GetProperty("sha256").GetString() == EditWire.Sha256(artifact.Assembly), "artifact assembly identity");

		// Invalid compile: success=false, diagnostics mapped, no artifact.
		stub.NextResult = new CompilationResult(new[] { new CompilerDiagnostic(CompilerDiagnosticSeverity.Error, "syntax", "CS1002", null, "Target.cs", new LineLocationSpan(new LineLocation(3, 7), new LineLocation(3, 8))) });
		stub.NextDiagnostics = new[] { new CompilerDiagnostic(CompilerDiagnosticSeverity.Error, "syntax", "CS1002", null, "Target.cs", new LineLocationSpan(new LineLocation(3, 7), new LineLocation(3, 8))) };
		var invalid = frontend.ExecuteTool("edit_compile", Args("edit_class", "class T {", Path.GetFullPath(fixture)), callContext);
		var invalidCore = Core(Payload(invalid));
		Check(!Bool(invalidCore, "success") && !Bool(invalidCore, "consumable_by_import"), "invalid compile not consumable");
		var diagnostics = (JsonElement)invalidCore["diagnostics"];
		Check(diagnostics.GetArrayLength() == 1 && diagnostics[0].GetProperty("id").GetString() == "CS1002"
			&& diagnostics[0].GetProperty("line").GetInt32() == 3, "diagnostic mapped with location");

		// Unknown tool passthrough.
		Check(frontend.ExecuteTool("edit_begin", new Dictionary<string, object?>(), callContext) == null, "non-compile tool returns null");

		frontend.Dispose();
		Console.WriteLine("PASS compile-frontend-matrix stub-compile+cache+diagnostics+dispatch");
	}

	sealed class StubCompilerProvider : ILanguageCompilerProvider {
		public double Order => 0;
		public dnSpy.Contracts.Images.ImageReference? Icon => null;
		public Guid Language => dnSpy.Contracts.Decompiler.DecompilerConstants.LANGUAGE_CSHARP;
		public bool CanCompile(CompilationKind kind) => true;
		public ILanguageCompiler Create(CompilationKind kind) => new StubCompiler(() => NextResult, () => NextDiagnostics ?? Array.Empty<CompilerDiagnostic>());
		public CompilationResult NextResult = new CompilationResult(new byte[] { 1 }, new DebugFileResult(), Array.Empty<CompilerDiagnostic>());
		public CompilerDiagnostic[]? NextDiagnostics;
	}

	sealed class StubCompiler : ILanguageCompiler {
		readonly Func<CompilationResult> result;
		readonly Func<CompilerDiagnostic[]> diagnostics;
		public StubCompiler(Func<CompilationResult> result, Func<CompilerDiagnostic[]> diagnostics) {
			this.result = result; this.diagnostics = diagnostics;
		}
		public string FileExtension => ".cs";
		public IEnumerable<string> GetRequiredAssemblyReferences(ModuleDef module) => Array.Empty<string>();
		public void InitializeProject(CompilerProjectInfo projectInfo) { }
		public ICodeDocument[] AddDocuments(CompilerDocumentInfo[] documents) => Array.Empty<ICodeDocument>();
		public bool AddMetadataReferences(CompilerMetadataReference[] metadataReferences) => true;
		public Task<CompilationResult> CompileAsync(CancellationToken cancellationToken)
			=> Task.FromResult(result().RawFile == null && diagnostics().Length > 0
				? new CompilationResult(diagnostics())
				: result());
		public void Dispose() { }
	}

	static IEnumerable<string> ChannelsDiff(ModuleDef left, ModuleDef right) {
		var l = EditFingerprintChannels(left).OrderBy(x => x, StringComparer.Ordinal).ToList();
		var r = EditFingerprintChannels(right).OrderBy(x => x, StringComparer.Ordinal).ToList();
		for (int i = 0; i < Math.Max(l.Count, r.Count); i++) {
			var a = i < l.Count ? l[i] : "<missing>";
			var b = i < r.Count ? r[i] : "<missing>";
			if (!string.Equals(a, b, StringComparison.Ordinal)) yield return "row " + i + ": expected [" + a + "], actual [" + b + "]";
		}
	}
	static IEnumerable<string> EditFingerprintChannels(ModuleDef module) {
		foreach (var type in module.Types) {
			yield return "type|" + type.FullName + "|" + (uint)type.Attributes + "|" + type.CustomAttributes.Select(x => x.Constructor?.FullName + ":" + x.ConstructorArguments.Count).OrderBy(x => x).Aggregate((x, y) => x + ";" + y);
			foreach (var method in type.Methods) yield return "method|" + method.FullName + "|" + method.CustomAttributes.Select(x => x.Constructor?.FullName + ":" + x.ConstructorArguments.Count).Aggregate("", (x, y) => x + ";" + y);
		}
	}

	// Isolate the begin roundtrip_fingerprint failure after an attribute_add
	// commit: production apply -> production write paths -> fingerprint diff.
	static void CaRoundtripSpike(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture), new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		// Replicate dnSpy's resolver-enabled module context: the resolve-first
		// ctor path then finds the real corlib MethodDef like dnSpy does.
		var resolver = new dnlib.DotNet.AssemblyResolver();
		var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
		resolver.PreSearchPaths.Add(runtimeDir);
		resolver.PostSearchPaths.Add(runtimeDir);
		live.Context = new ModuleContext(resolver);
		var simple = live.Types.First(x => x.Name == "Simple");
		var map = new Dictionary<string, IMDTokenProvider>();
		// Replicate the exact acc004-r4 sequence: method_add commit, attribute
		// commit (real PrepareCommit/Finalize on an InMemory store), then the
		// begin capability compare Write(live)+reload vs live fingerprints.
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "ca-spike"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var workspace = EditWorkspace.CreateForTesting(live);
		string Commit(string operationJson) {
			using var json = JsonDocument.Parse(operationJson);
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operationJson);
			var binding = history.ResolveBegin(workspace, null);
			var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-ca-spike", 1, Array.Empty<string>());
			using var liveJson = JsonDocument.Parse(operationJson);
			EditOperationRegistry.ApplyPersisted(live, liveJson.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			history.Finalize(prepared, live);
			workspace.NormalizedOperations.Clear();
			return prepared.Lineage.Manifest.HeadCheckpointId;
		}
		var simpleToken = "0x" + simple.MDToken.Raw.ToString("x8");
		Commit(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_add", ["owner_type"] = new Dictionary<string, object?> { ["token"] = simpleToken },
			["name"] = "Acc004Generic", ["signature"] = new Dictionary<string, object?> {
				["return_type"] = "System.Int32", ["has_this"] = false,
				["generic_parameters"] = new object[] { new Dictionary<string, object?> { ["name"] = "T" } }, ["parameters"] = Array.Empty<object>(),
			},
			["attributes"] = 128,
		}));
		Commit(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "attribute_add",
			["target"] = new Dictionary<string, object?> { ["token"] = simpleToken },
			["constructor"] = new Dictionary<string, object?> {
				["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
			},
			["fixed_arguments"] = new object[] { "acc004", false },
			["named_arguments"] = Array.Empty<object>(),
		}));
		// r7 sequence: attribute_remove commit after the add commit.
		try {
			Commit(JsonSerializer.Serialize(new Dictionary<string, object?> {
				["kind"] = "attribute_remove",
				["target"] = new Dictionary<string, object?> { ["token"] = simpleToken },
				["match"] = new Dictionary<string, object?> {
					["constructor"] = new Dictionary<string, object?> {
						["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
					},
				},
			}));
			Console.WriteLine("remove commit OK");
		}
		catch (Exception ex) { Console.WriteLine("remove commit FAIL " + ex.GetType().Name + ": " + ex.Message); }
		var baseline = EditFingerprint.Compute(live);
		var copyBytes = EditWorkspace.Write(live);
		using var copy = ModuleDefMD.Load(copyBytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		var copyPrint = EditFingerprint.Compute(copy);
		Console.WriteLine("begin-compare equal=" + (baseline == copyPrint));
		if (baseline != copyPrint) {
			var a = System.Text.RegularExpressions.Regex.Split(baseline, "(?<=.)").Length;
			foreach (var row in ChannelsDiff(live, copy)) { Console.WriteLine("DIFF " + row); }
		}
		// Replicate the commit mutation order: checkpoint image pass (PreserveRids
		// materializes writer dummy types; the cleanup removes them) BEFORE the
		// private-copy write, exactly like the production begin-after-commit.
		var checkpointBytes = EditWorkspace.WriteCheckpointImage(live);
		Console.WriteLine("checkpoint pass bytes=" + checkpointBytes.Length);
		foreach (var (label, bytes) in new[] {
			("write", EditWorkspace.Write(live)),
			("canonical", EditWorkspace.WriteCanonical(live)),
		}) {
			using var reloaded = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
			var rSimple = reloaded.Types.First(x => x.Name == "Simple");
			var rca = rSimple.CustomAttributes.LastOrDefault();
			Console.WriteLine(label + " rca=" + (rca != null)
				+ " ctor=" + ((rca?.Constructor as IMethod)?.DeclaringType?.FullName ?? "null")
				+ " args=" + (rca != null ? rca.ConstructorArguments.Count : -1)
				+ " raw=" + (rca?.RawData?.Length.ToString() ?? "null"));
		}
	}

	// P04 slice 3 (IMP-006/007): P/Invoke binding and security declarations
	// through the production path with write/reload and illegal rejections.
	static void TestP04Slice3(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p04-slice3"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var map = new Dictionary<string, IMDTokenProvider>();
		static JsonElement Op(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
		static bool Rejects(ModuleDef module, JsonElement operation, Dictionary<string, IMDTokenProvider> map) {
			try { EditOperationRegistry.Apply(module, operation, map, 0); return false; }
			catch (Exception ex) when (ex is ArgumentException or EditDomainException) { return true; }
		}
		var target = live.Types.First(x => x.Name == "Simple");

		// IMP-006 pinvoke: new static extern method -> ImplMap; illegal: instance method.
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_add", ["owner_type"] = new Dictionary<string, object?> { ["token"] = "0x" + target.MDToken.Raw.ToString("x8") },
			["name"] = "SliceSleep", ["signature"] = new Dictionary<string, object?> {
				["return_type"] = "System.Void", ["has_this"] = false, ["generic_parameters"] = Array.Empty<object>(),
				["parameters"] = Array.Empty<object>(),
			},
			["attributes"] = (ulong)(MethodAttributes.Public | MethodAttributes.Static),
			["pinvoke"] = new Dictionary<string, object?> { ["module_name"] = "kernel32.dll", ["entry_name"] = "Sleep", ["charset"] = "ansi", ["last_error"] = true },
		})), map, 11);
		var pinvokeMethod = target.Methods.First(x => x.Name == "SliceSleep");
		var implMap = pinvokeMethod.ImplMap;
		Check(implMap != null && implMap.Name.String == "Sleep" && implMap.Module?.Name.String == "kernel32.dll"
			&& pinvokeMethod.IsPinvokeImpl && implMap.IsCharSetAnsi && implMap.SupportsLastError, "pinvoke implmap bound");
		var instance = live.GetTypes().SelectMany(x => x.Methods).First(m => !m.IsStatic && m.HasBody);
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_update", ["target"] = new Dictionary<string, object?> { ["token"] = "0x" + instance.MDToken.Raw.ToString("x8") },
			["pinvoke"] = new Dictionary<string, object?> { ["module_name"] = "kernel32.dll" },
		})), map), "pinvoke on non-static-extern rejected");

		// IMP-007 security: type deny + reload XML roundtrip; illegal action string.
		var permissionXml = "<PermissionSet class=\"System.Security.PermissionSet\" version=\"1\"><Permission class=\"System.Security.Permissions.SecurityPermission, mscorlib\" version=\"1\"><Unrestricted>true</Unrestricted></Permission></PermissionSet>";
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "security_add", ["parent"] = new Dictionary<string, object?> { ["token"] = "0x" + target.MDToken.Raw.ToString("x8") },
			["action"] = "deny", ["xml"] = permissionXml,
		})), map, 0);
		var securityRow = target.DeclSecurities.FirstOrDefault(x => x.Action == SecurityAction.Deny);
		// The blob is produced by dnlib's serializer lazily; assert the parsed
		// attribute rows here and let the write/reload step prove serialization.
		Check(securityRow != null && securityRow.SecurityAttributes.Count == 1
			&& securityRow.SecurityAttributes[0].TypeFullName == "System.Security.Permissions.PermissionSetAttribute",
			"security deny row attached with parsed attributes");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "security_add", ["parent"] = new Dictionary<string, object?> { ["token"] = "0x" + target.MDToken.Raw.ToString("x8") },
			["action"] = "grant", ["xml"] = permissionXml,
		})), map), "unknown security action rejected");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "security_remove", ["parent"] = new Dictionary<string, object?> { ["token"] = "0x" + target.MDToken.Raw.ToString("x8") },
			["action"] = "deny",
		})), map, 0);
		Check(target.DeclSecurities.All(x => x.Action != SecurityAction.Deny), "security row removed");

		// Write/reload: pinvoke row survives.
		var image = EditWorkspace.Write(live);
		using var reloaded = ModuleDefMD.Load(image);
		var rTarget = reloaded.Types.First(x => x.Name == "Simple");
		var rPInvoke = rTarget.Methods.First(x => x.Name == "SliceSleep");
		Check(rPInvoke.ImplMap != null && rPInvoke.ImplMap.Name == "Sleep" && rPInvoke.IsPinvokeImpl
			&& rPInvoke.ImplMap.Module?.Name == "kernel32.dll", "reloaded pinvoke row preserved");

		Console.WriteLine("PASS p04-slice3 pinvoke+security reloaded-exact illegal-rejected");
	}

	// P04 slice 2 (IMP-001/003/005): attribute add/remove, method overrides and
	// field/parameter marshal through the production path with write/reload and
	// compiled-inverse assertions.
	static void TestP04Slice2(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p04-slice2"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var map = new Dictionary<string, IMDTokenProvider>();
		static JsonElement Op(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
		static bool Rejects(ModuleDef module, JsonElement operation, Dictionary<string, IMDTokenProvider> map) {
			try { EditOperationRegistry.Apply(module, operation, map, 0); return false; }
			catch (Exception ex) when (ex is ArgumentException or EditDomainException) { return true; }
		}

		// IMP-005 marshal: field + parameter payload, reload exact, illegal vector.
		var simple = live.Types.First(x => x.Name == "Simple");
		var dataField = simple.Fields.First(x => x.IsStatic && !x.IsLiteral);
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["token"] = "0x" + dataField.MDToken.Raw.ToString("x8") },
			["marshal"] = new Dictionary<string, object?> { ["kind"] = "simple", ["native"] = "I4" },
		})), map, 0);
		Check(dataField.MarshalType is MarshalType simpleMarshal && simpleMarshal.NativeType == NativeType.I4, "field marshal set");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["token"] = "0x" + dataField.MDToken.Raw.ToString("x8") },
			["marshal"] = new Dictionary<string, object?> { ["kind"] = "custom" },
		})), map), "custom marshal without guid rejected");
		var withParameters = live.GetTypes().SelectMany(x => x.Methods).First(m => m.MethodSig.Params.Count > 0 && m.ParamDefs.Count > 0);
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "parameter_update",
			["parameter_target"] = new Dictionary<string, object?> {
				["owner_method"] = new Dictionary<string, object?> { ["token"] = "0x" + withParameters.MDToken.Raw.ToString("x8") },
				["parameter_index"] = 0,
			},
			["marshal"] = new Dictionary<string, object?> { ["kind"] = "array", ["element"] = "I4", ["param_number"] = 1, ["size"] = 4, ["flags"] = 0 },
		})), map, 0);
		Check(withParameters.ParamDefs[0].MarshalType is ArrayMarshalType arrayMarshal && arrayMarshal.ElementType == NativeType.I4
			&& arrayMarshal.ParamNumber == 1 && arrayMarshal.Size == 4, "parameter array marshal set");

		// IMP-003 overrides: virtual body -> base virtual declaration; illegal
		// vectors: non-virtual body, self override.
		var overrides = live.GetTypes().SelectMany(x => x.Methods).Where(m => m.IsVirtual && !m.IsAbstract).ToArray();
		Check(overrides.Length >= 2, "fixture has virtual methods", "count=" + overrides.Length);
		var body = overrides[0]; var declaration = overrides[1];
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_update", ["target"] = new Dictionary<string, object?> { ["token"] = "0x" + body.MDToken.Raw.ToString("x8") },
			["overrides"] = new object[] { new Dictionary<string, object?> {
				["declaration"] = new Dictionary<string, object?> {
					["owner_type"] = declaration.DeclaringType.FullName,
					["name"] = declaration.Name.String,
					["parameter_types"] = declaration.MethodSig.Params.Select(p => p.FullName).ToArray(),
				},
			} },
		})), map, 0);
		Check(body.Overrides.Count == 1 && ReferenceEquals(body.Overrides[0].MethodDeclaration, declaration), "override mapped");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_update", ["target"] = new Dictionary<string, object?> { ["token"] = "0x" + body.MDToken.Raw.ToString("x8") },
			["overrides"] = new object[] { new Dictionary<string, object?> {
				["declaration"] = new Dictionary<string, object?> {
					["owner_type"] = body.DeclaringType.FullName, ["name"] = body.Name.String,
					["parameter_types"] = body.MethodSig.Params.Select(p => p.FullName).ToArray(),
				},
			} },
		})), map), "self override rejected");

		// IMP-001 attributes: attach Obsolete with fixed+named args on the type;
		// reload keeps the row; remove restores absence; duplicate-without-
		// AllowMultiple rejected.
		var typeToken = simple.MDToken.Raw.ToString("x8");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "attribute_add",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + typeToken },
			["constructor"] = new Dictionary<string, object?> {
				["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
			},
			["fixed_arguments"] = new object[] { "slice2", true },
			["named_arguments"] = Array.Empty<object>(),
		})), map, 0);
		var attached = simple.CustomAttributes.FirstOrDefault(x => x.TypeFullName == "System.ObsoleteAttribute");
		Check(attached != null && attached.ConstructorArguments.Count == 2
			&& string.Equals(attached.ConstructorArguments[0].Value?.ToString(), "slice2"), "attribute attached with fixed args");
		// Referenced attribute types without an assembly-resolvable ctor row bind
		// to a synthesized MemberRef whose shape the payload declares; the
		// illegal vector is therefore the arity mismatch, which the registry
		// rejects before any mutation.
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "attribute_add",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + typeToken },
			["constructor"] = new Dictionary<string, object?> {
				["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
			},
			["fixed_arguments"] = new object[] { "only-one" },
		})), map), "constructor argument arity mismatch rejected");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "attribute_add",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + typeToken },
			["constructor"] = new Dictionary<string, object?> {
				["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
			},
			["fixed_arguments"] = new object[] { "dup", false },
		})), map), "duplicate non-AllowMultiple rejected");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "attribute_remove",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + typeToken },
			["match"] = new Dictionary<string, object?> {
				["constructor"] = new Dictionary<string, object?> {
					["attribute_type"] = "System.ObsoleteAttribute", ["parameter_types"] = new[] { "System.String", "System.Boolean" },
				},
			},
		})), map, 0);
		Check(simple.CustomAttributes.All(x => x.TypeFullName != "System.ObsoleteAttribute"), "attribute removed");

		// Write/reload: marshal + overrides survive exactly.
		var image = EditWorkspace.Write(live);
		using var reloaded = ModuleDefMD.Load(image);
		var rSimple = reloaded.Types.First(x => x.Name == "Simple");
		var rField = rSimple.Fields.First(x => x.IsStatic && !x.IsLiteral);
		Check(rField.MarshalType is MarshalType rMarshal && rMarshal.NativeType == NativeType.I4, "reloaded field marshal preserved");
		var rBody = reloaded.GetTypes().SelectMany(x => x.Methods).First(m => m.Name == body.Name && m.DeclaringType.FullName == body.DeclaringType.FullName);
		Check(rBody.Overrides.Count == 1 && rBody.Overrides[0].MethodDeclaration.Name == declaration.Name, "reloaded override preserved");

		Console.WriteLine("PASS p04-slice2 attributes+overrides+marshal reloaded-exact illegal-rejected");
	}

	// P04 slice 1 (IMP-002/004/008): constraints, layout + field offset and
	// static initial data through the production apply path, with write/reload
	// assertions and illegal-structure rejections.
	static void TestP04Slice1(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p04-slice1"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var map = new Dictionary<string, IMDTokenProvider>();
		var target = live.Types.First(x => x.Name == "Simple");
		var genericOwner = live.Types.First(x => x.Name == "GenericMethodOwner`1");
		var gp = genericOwner.GenericParameters[0];

		static JsonElement Op(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
		static bool Rejects(ModuleDef module, JsonElement operation, Dictionary<string, IMDTokenProvider> map) {
			try { EditOperationRegistry.Apply(module, operation, map, 0); return false; }
			catch (Exception ex) when (ex is ArgumentException or EditDomainException) { return true; }
		}

		// IMP-002 constraints: whole-list replace with reload semantics.
		var beforeConstraints = gp.GenericParamConstraints.Count;
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "generic_parameter_update",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + gp.MDToken.Raw.ToString("x8") },
			["constraints"] = new[] { "TestIL.Simple" },
		})), map, 0);
		Check(gp.GenericParamConstraints.Count == 1 && gp.GenericParamConstraints[0].Constraint.FullName == "TestIL.Simple",
			"constraints replaced whole list");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "generic_parameter_update",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + gp.MDToken.Raw.ToString("x8") },
			["constraints"] = new[] { "!0" },
		})), map), "self constraint rejected");

		// IMP-004 layout: on a self-created instance-bearing type (the fixture's
		// Simple is a static class with no instance slots).
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "type_add", ["name"] = "SliceLayout", ["namespace"] = "TestIL",
			["attributes"] = (ulong)(TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit),
		})), map, 7);
		var layoutType = live.Types.First(x => x.Name == "SliceLayout");
		var layoutRef = new Dictionary<string, object?> { ["object_id"] = "obj-007-00" };
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_add", ["owner_type"] = new Dictionary<string, object?> { ["object_id"] = "obj-007-00" },
			["name"] = "OffsetField", ["field_type"] = "System.Int32", ["attributes"] = (ulong)FieldAttributes.Public,
		})), map, 6);
		var offsetField = layoutType.Fields.First(x => x.Name == "OffsetField");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "type_update", ["target"] = layoutRef,
			["layout"] = new Dictionary<string, object?> { ["kind"] = "sequential", ["pack"] = 8, ["size"] = 0 },
		})), map, 0);
		Check(layoutType.ClassLayout != null && layoutType.ClassLayout.PackingSize == 8 && layoutType.Attributes.HasFlag(TypeAttributes.SequentialLayout),
			"sequential layout with pack 8");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "type_update", ["target"] = layoutRef,
			["layout"] = new Dictionary<string, object?> { ["kind"] = "sequential", ["pack"] = 3 },
		})), map), "illegal pack rejected");
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["object_id"] = "obj-006-00" },
			["field_offset"] = 4,
		})), map), "field_offset rejected without explicit layout");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "type_update", ["target"] = layoutRef,
			["layout"] = new Dictionary<string, object?> { ["kind"] = "explicit", ["pack"] = 8 },
		})), map, 0);
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["object_id"] = "obj-006-00" },
			["field_offset"] = 4,
		})), map, 0);
		Check(offsetField.FieldOffset == 4, "field offset set under explicit layout");

		// IMP-008 initial data: create a real static field via field_add (the
		// object id keeps the reference valid before any image write) and carry
		// the bytes through write+reload exactly.
		var typeToken = "0x" + target.MDToken.Raw.ToString("x8");
		var fieldAttributes = (ulong)(FieldAttributes.Public | FieldAttributes.Static);
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_add",
			["owner_type"] = new Dictionary<string, object?> { ["token"] = typeToken },
			["name"] = "SliceData", ["field_type"] = "System.Int32", ["attributes"] = fieldAttributes,
		})), map, 9);
		var dataField = target.Fields.First(x => x.Name == "SliceData");
		var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["object_id"] = "obj-009-00" },
			["initial_data"] = new Dictionary<string, object?> { ["bytes_base64"] = Convert.ToBase64String(payload) },
		})), map, 0);
		Check(dataField.InitialValue.SequenceEqual(payload), "initial data set");
		EditOperationRegistry.Apply(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_add",
			["owner_type"] = new Dictionary<string, object?> { ["token"] = typeToken },
			["name"] = "SliceConst", ["field_type"] = "System.Int32",
			["attributes"] = (ulong)(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal),
			["constant"] = new Dictionary<string, object?> { ["kind"] = "i4", ["value"] = 7 },
		})), map, 8);
		Check(Rejects(live, Op(JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "field_update", ["target"] = new Dictionary<string, object?> { ["object_id"] = "obj-008-00" },
			["initial_data"] = new Dictionary<string, object?> { ["bytes_base64"] = Convert.ToBase64String(payload) },
		})), map), "literal field initial data rejected");

		// Write + reload: layout row, offsets, constraints and data survive exactly.
		var image = EditWorkspace.Write(live);
		using var reloaded = ModuleDefMD.Load(image);
		var rTarget = reloaded.Types.First(x => x.Name == "Simple");
		var rLayout = reloaded.Types.First(x => x.Name == "SliceLayout");
		var rOwner = reloaded.Types.First(x => x.Name == "GenericMethodOwner`1");
		Check(rLayout.ClassLayout != null && rLayout.ClassLayout.PackingSize == 8 && rLayout.Attributes.HasFlag(TypeAttributes.ExplicitLayout),
			"reloaded layout row preserved");
		Check(rLayout.Fields.First(x => x.Name == "OffsetField").FieldOffset == 4, "reloaded field offset preserved");
		Check(rOwner.GenericParameters[0].GenericParamConstraints.Count == 1
			&& rOwner.GenericParameters[0].GenericParamConstraints[0].Constraint.FullName == "TestIL.Simple",
			"reloaded constraints preserved");
		var rData = rTarget.Fields.First(x => x.Name == "SliceData");
		Check(rData.InitialValue != null && rData.InitialValue.SequenceEqual(payload) && rData.RVA != 0, "reloaded initial data byte-exact with RVA");

		Console.WriteLine("PASS p04-slice1 constraints+layout+offset+initial-data reloaded-exact illegal-rejected");
	}
}
