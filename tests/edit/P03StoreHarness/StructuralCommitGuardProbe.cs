using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class StructuralCommitGuardProbe {
	public static void RunCreatedMethodParameter(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-created-parameter-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var owner = live.GetTypes().Single(t => t.FullName == "P02Fixture.Program");
		var ownerToken = "0x" + owner.MDToken.Raw.ToString("x8");
		var beforeFingerprint = EditFingerprint.Compute(live);
		var methodAdd = JsonSerializer.Serialize(new {
			kind = "method_add", owner_type = new { token = ownerToken }, name = "T064CreatedParameter",
			signature = new { return_type = "System.Void", has_this = false, parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() },
			body = new { init_locals = false, max_stack = 0, instructions = new[] { new { opcode = "ret" } },
				locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>() },
		}, EditWire.JsonOptions);
		var parameterAdd = "{\"kind\":\"parameter_add\",\"owner_method\":{\"object_id\":\"obj-000-00\"},\"parameter_index\":0,\"name\":\"createdArg\",\"parameter_type\":\"System.String\"}";
		var operations = new[] { methodAdd, parameterAdd };
		for (var index = 0; index < operations.Length; index++) {
			using var json = JsonDocument.Parse(operations[index]);
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, index);
			workspace.NormalizedOperations.Add(operations[index]);
		}
		var bound = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, bound, workspace.NormalizedOperations,
			"review-created-parameter", 1, Array.Empty<string>());
		var liveMap = new Dictionary<string, IMDTokenProvider>();
		for (var index = 0; index < operations.Length; index++) {
			using var json = JsonDocument.Parse(operations[index]);
			EditOperationRegistry.ApplyPersisted(live, json.RootElement, liveMap, index);
		}
		history.Finalize(prepared, live);
		MethodDef? Added() => owner.Methods.SingleOrDefault(m => m.Name == "T064CreatedParameter");
		Check(Added() is { } added && added.MethodSig.Params.Count == 1
			&& added.MethodSig.Params[0].FullName == "System.String" && added.ParamDefs.Single().Name == "createdArg",
			"created method object_id parameter commits on the intended owner");
		var afterFingerprint = EditFingerprint.Compute(live);
		var lineageId = prepared.Lineage.Manifest.LineageId;
		var headId = prepared.PostHeadCheckpointId;
		using var reopened = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = reopened.Load(lineageId);
		var rootId = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
		var undoWrite = reopened.PrepareHeadMove(lineageId, headId, rootId, "undo");
		reopened.PlanNavigation(lineage, headId, rootId).Apply(live);
		reopened.Finalize(undoWrite, live);
		Check(Added() == null && EditFingerprint.Compute(live) == beforeFingerprint
			&& reopened.Load(lineageId).Manifest.HeadCheckpointId == rootId,
			"created method and parameter undo removes only the new graph");
		var redoWrite = reopened.PrepareHeadMove(lineageId, rootId, headId, "redo");
		reopened.PlanNavigation(reopened.Load(lineageId), rootId, headId).Apply(live);
		reopened.Finalize(redoWrite, live);
		Check(Added() is { } restored && restored.MethodSig.Params.Count == 1
			&& restored.MethodSig.Params[0].FullName == "System.String" && restored.ParamDefs.Single().Sequence == 1
			&& restored.ParamDefs.Single().Name == "createdArg" && EditFingerprint.Compute(live) == afterFingerprint
			&& reopened.Load(lineageId).Manifest.HeadCheckpointId == headId,
			"created method object_id parameter redo preserves owner and full checkpoint identity");
	}

	public static void RunParameterAdd(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-parameter-commit-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.ResolveToken(0x0600000D) as MethodDef;
		Check(method != null && method.MethodSig.Params.Count == 0, "parameter fixture has empty target signature");
		var beforeFingerprint = EditFingerprint.Compute(live);
		var operation = "{\"kind\":\"parameter_add\",\"owner_method\":{\"token\":\"0x0600000D\"},\"parameter_index\":0,\"name\":\"added\",\"parameter_type\":\"System.Int32\"}";
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
		workspace.NormalizedOperations.Add(operation);
		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations,
			"review-parameter-add", 1, Array.Empty<string>());
		Check(store.TempCount == 1 && store.FinalCount == 0, "parameter_add inverse envelope prepares a package");
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(live, json.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		history.Finalize(prepared, live);
		Check(store.FinalCount == 1 && method!.MethodSig.Params.Count == 1
			&& method.MethodSig.Params[0].FullName == "System.Int32", "parameter_add package commits and preserves signature");
		var afterFingerprint = EditFingerprint.Compute(live);
		var lineageId = prepared.Lineage.Manifest.LineageId;
		var headId = prepared.PostHeadCheckpointId;
		using var reopened = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = reopened.Load(lineageId);
		var rootId = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
		Check(lineage.Manifest.HeadCheckpointId == headId && lineage.Operations[headId].Operations.Single().Kind == "parameter_add",
			"parameter_add persisted package cold-loads at committed head");
		var undoWrite = reopened.PrepareHeadMove(lineageId, headId, rootId, "undo");
		reopened.PlanNavigation(lineage, headId, rootId).Apply(live);
		reopened.Finalize(undoWrite, live);
		Check(method.MethodSig.Params.Count == 0 && method.ParamDefs.Count == 0
			&& EditFingerprint.Compute(live) == beforeFingerprint
			&& reopened.Load(lineageId).Manifest.HeadCheckpointId == rootId,
			"parameter_add undo restores old signature, parameter row, fingerprint and root head");
		var redoWrite = reopened.PrepareHeadMove(lineageId, rootId, headId, "redo");
		reopened.PlanNavigation(reopened.Load(lineageId), rootId, headId).Apply(live);
		reopened.Finalize(redoWrite, live);
		Check(method.MethodSig.Params.Count == 1 && method.MethodSig.Params[0].FullName == "System.Int32"
			&& method.ParamDefs.Count == 1 && method.ParamDefs[0].Sequence == 1 && method.ParamDefs[0].Name == "added"
			&& EditFingerprint.Compute(live) == afterFingerprint
			&& reopened.Load(lineageId).Manifest.HeadCheckpointId == headId,
			"parameter_add redo restores owner, sequence, type, name, fingerprint and committed head");
		var validPackage = store.FinalBytes(lineageId);
		var malformed = RewriteInverse(validPackage, inverse => inverse["state"]!["parameter_target"] = new JsonObject());
		var rejected = false;
		try { reopened.ValidatePackageForTesting(malformed); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { rejected = true; }
		Check(rejected && store.FinalBytes(lineageId).SequenceEqual(validPackage)
			&& EditFingerprint.Compute(live) == afterFingerprint,
			"empty parameter inverse target rejects without package or live mutation");
		var malformedStates = new (string label, Action<JsonObject> mutate)[] {
			("missing_kind", inverse => inverse["state"]!.AsObject().Remove("kind")),
			("extra_key", inverse => inverse["state"]!["extra"] = true),
			("wrong_kind", inverse => inverse["state"]!["kind"] = "field_remove"),
			("wrong_mode", inverse => inverse["state"]!["remove_mode"] = "cascade"),
			("target_string", inverse => inverse["state"]!["parameter_target"] = "invalid"),
			("target_missing_index", inverse => inverse["state"]!["parameter_target"]!.AsObject().Remove("parameter_index")),
			("target_extra_key", inverse => inverse["state"]!["parameter_target"]!["extra"] = true),
			("target_wrong_index_type", inverse => inverse["state"]!["parameter_target"]!["parameter_index"] = "0"),
			("owner_empty", inverse => inverse["state"]!["parameter_target"]!["owner_method"] = new JsonObject()),
			("owner_extra_key", inverse => inverse["state"]!["parameter_target"]!["owner_method"]!["extra"] = true),
		};
		foreach (var (label, mutate) in malformedStates) {
			var copy = RewriteInverse(validPackage, mutate);
			var invalid = false;
			try { reopened.ValidatePackageForTesting(copy); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { invalid = true; }
			Check(invalid && store.FinalBytes(lineageId).SequenceEqual(validPackage)
				&& EditFingerprint.Compute(live) == afterFingerprint, "parameter inverse load rejects " + label);
		}
		foreach (var (label, token) in new[] { ("wrong_metadata_kind", "0x02000001"), ("wrong_owner", "0x0600ffff") }) {
			var copy = RewriteInverse(validPackage,
				inverse => inverse["state"]!["parameter_target"]!["owner_method"]!["token"] = token);
			var loaded = reopened.ValidatePackageForTesting(copy);
			var invalid = false;
			try { reopened.PlanNavigation(loaded, headId, rootId); }
			catch (EditDomainException) { invalid = true; }
			Check(invalid && store.FinalBytes(lineageId).SequenceEqual(validPackage)
				&& EditFingerprint.Compute(live) == afterFingerprint, "parameter inverse navigation rejects " + label);
		}
		var otherMethod = live.GetTypes().SelectMany(type => type.Methods)
			.First(candidate => candidate != method && candidate.MethodSig.Params.Count == 1 && !candidate.IsConstructor);
		var wrongOwnerPackage = RewriteInverse(validPackage, inverse => inverse["state"]!["parameter_target"]!["owner_method"]!["token"]
			= "0x" + otherMethod.MDToken.Raw.ToString("x8"));
		var wrongOwnerLineage = reopened.ValidatePackageForTesting(wrongOwnerPackage);
		rejected = false;
		try { reopened.PlanNavigation(wrongOwnerLineage, headId, rootId); }
		catch (EditDomainException) { rejected = true; }
		Check(rejected && store.FinalBytes(lineageId).SequenceEqual(validPackage)
			&& EditFingerprint.Compute(live) == afterFingerprint, "different valid MethodDef owner rejects before live navigation");
		var wrongOperationKind = RewriteInverse(validPackage, inverse => inverse["operation_kind"] = "method_add");
		rejected = false;
		try { reopened.ValidatePackageForTesting(wrongOperationKind); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { rejected = true; }
		Check(rejected && store.FinalBytes(lineageId).SequenceEqual(validPackage)
			&& EditFingerprint.Compute(live) == afterFingerprint, "parameter inverse remains bound to operation.Kind");
		var differentOperation = RewriteInverse(validPackage, row => {
			row["kind"] = "method_add";
			row["forward"]!["kind"] = "method_add";
			row["inverse"]!["operation_kind"] = "method_add";
		}, wholeOperation: true);
		rejected = false;
		try { reopened.ValidatePackageForTesting(differentOperation); }
		catch (EditDomainException ex) when (ex.Code is "EDIT_CHECKPOINT_INVALID" or "EDIT_OPERATION_VERSION_UNSUPPORTED") { rejected = true; }
		Check(rejected && store.FinalBytes(lineageId).SequenceEqual(validPackage)
			&& EditFingerprint.Compute(live) == afterFingerprint, "non-parameter operation cannot reuse parameter inverse shape");
		Console.WriteLine("PASS parameter-add-checkpoint envelope=parameter_remove committed=true");
	}

	static byte[] RewriteInverse(byte[] package, Action<JsonObject> mutate, bool wholeOperation = false) {
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		using (var input = new MemoryStream(package, writable: false))
		using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
			foreach (var entry in archive.Entries) {
				using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
				entries[entry.FullName] = bytes.ToArray();
			}
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var head = manifest.Checkpoints.Single(x => x.CheckpointId == manifest.HeadCheckpointId);
		var document = JsonNode.Parse(entries[head.OperationEntry])!;
		var operation = document["operations"]![0]!.AsObject();
		mutate(wholeOperation ? operation : operation["inverse"]!.AsObject());
		entries[head.OperationEntry] = JsonSerializer.SerializeToUtf8Bytes(document, EditWire.JsonOptions);
		head.OperationSha256 = EditWire.Sha256(entries[head.OperationEntry]);
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
			foreach (var entry in entries) {
				using var stream = archive.CreateEntry(entry.Key).Open(); stream.Write(entry.Value);
			}
		return output.ToArray();
	}

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-structural-commit-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var beforeLive = EditFingerprint.Compute(live);
		var beforePrivate = workspace.PrivateFingerprint();
		var method = live.GetTypes().SelectMany(type => type.Methods).First(row => !row.IsConstructor && row.HasBody);
		var operation = JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "method_body_replace",
			["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
			["body"] = new Dictionary<string, object?> {
				["init_locals"] = false, ["max_stack"] = 1,
				["locals"] = Array.Empty<object>(), ["exception_handlers"] = Array.Empty<object>(),
				["instructions"] = new object[] {
					new Dictionary<string, object?> { ["opcode"] = "pop" },
					new Dictionary<string, object?> { ["opcode"] = "ret" },
				},
			},
		}, EditWire.JsonOptions);

		// Model the exact final commit-preparation boundary: all required risks have
		// already been confirmed, but the persisted operation stream is replayed and
		// structurally validated before a package can be staged or live can change.
		workspace.NormalizedOperations.Add(operation);
		var binding = history.ResolveBegin(workspace, null);
		var rejected = false;
		try {
			history.PrepareCommit(workspace, binding, workspace.NormalizedOperations,
				"review-structural-guard", 1,
				new[] { "risk-cross_assembly_inbound-InboundRef-1" });
		}
		catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { rejected = true; }

		Check(rejected, "confirmed risks cannot bypass structural validation during commit preparation");
		Check(store.TempCount == 0 && store.FinalCount == 0, "structural commit rejection creates no staged or final package");
		Check(EditFingerprint.Compute(live) == beforeLive, "structural commit rejection has no live side effect");
		Check(workspace.PrivateFingerprint() == beforePrivate, "structural commit rejection does not mutate the private graph");
		Console.WriteLine("PASS structural-commit-guard layer=PrepareCommit/SerializeOperations rule=stack_underflow confirmed_risks=1 live_unchanged=true store_unchanged=true");
	}

	static void Check(bool condition, string message) {
		if (!condition) throw new InvalidOperationException(message);
		Console.WriteLine("PASS " + message);
	}
}
