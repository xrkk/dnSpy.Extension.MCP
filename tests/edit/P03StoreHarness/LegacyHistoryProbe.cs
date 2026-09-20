using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class LegacyHistoryProbe {
	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-legacy-history-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var operation = RenameOperation(live, "EchoHistory");

		// The compatibility operation is package-only: public edit_apply must still reject it.
		using (var publicModule = ModuleDefMD.Load(Path.GetFullPath(fixture)))
		using (var document = JsonDocument.Parse(operation)) {
			var before = EditFingerprint.Compute(publicModule);
			var rejected = false;
			try { EditOperationRegistry.Apply(publicModule, document.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { rejected = true; }
			Check(rejected && EditFingerprint.Compute(publicModule) == before, "public edit_apply rejects legacy kind without mutation");
		}

		using (var document = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(workspace.PrivateModule, document.RootElement, workspace.ObjectIds, 0);
		workspace.NormalizedOperations.Add(operation);
		var binding = new EditHistoryBinding { FamilyId = EditWire.NewId("family"), MatchBasis = new[] { "probe" } };
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations,
			"legacy-history-probe", 1, Array.Empty<string>(), "legacy_rename_symbol_by_token");
		var persisted = prepared.Lineage.Operations[prepared.PostHeadCheckpointId].Operations.Single();
		Check(persisted.Kind == "legacy_symbol_rename" && persisted.KindVersion == 1, "legacy rename is recorded as internal v1 operation");

		using (var document = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(live, document.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		history.Finalize(prepared, live);
		var lineage = history.Load(prepared.Lineage.Manifest.LineageId);
		Check(lineage.Head.Kind == "legacy_rename_symbol_by_token"
			&& lineage.Operations[lineage.Head.CheckpointId].Operations.Single().Kind == "legacy_symbol_rename",
			"legacy checkpoint is visible through unified history");
		Check(Method(live).Name == "EchoHistory" && References(live).All(x => x.Name == "EchoHistory"),
			"legacy rename reached definition and references");

		var validPackage = store.FinalBytes(lineage.Manifest.LineageId);
		var finalCount = store.ListFinalIds().Count;
		RejectPackage(history, RewriteOperation(validPackage, kind: null, version: 99), "future integer operation version");
		RejectPackage(history, RewriteOperation(validPackage, kind: null, version: 2), "legacy wrong version");
		RejectPackage(history, RewriteOperation(validPackage, kind: "unknown_history_kind", version: 1), "unknown history kind");
		RejectInvalidPackage(history, RewriteVersionValue(validPackage, JsonValue.Create("99")), "string operation version");
		RejectInvalidPackage(history, RewriteVersionValue(validPackage, null), "null operation version");
		RejectInvalidPackage(history, RewriteVersionValue(validPackage, new JsonArray(99)), "array operation version");
		RejectInvalidPackage(history, RewriteVersionValue(validPackage, JsonValue.Create(1.5)), "fractional operation version");
		RejectInvalidPackage(history, RewriteOperationsContainer(validPackage), "malformed operations container");
		RejectInvalidPackage(history, RewriteOperationDocument(validPackage, document => {
			document["operations"]![0]!["kind_version"] = 99;
			document["operations"]![0]!["inverse"] = null;
		}, updateHash: true), "future version with malformed inverse");
		RejectInvalidPackage(history, RewriteVersionValue(validPackage, JsonValue.Create(99), updateHash: false), "operation hash mismatch precedence");
		Check(store.ListFinalIds().Count == finalCount && Method(live).Name == "EchoHistory",
			"rejected history packages have no store or live side effect");

		var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
		var undoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, lineage.Head.CheckpointId, root.CheckpointId, "undo");
		history.PlanNavigation(lineage, lineage.Head.CheckpointId, root.CheckpointId).Apply(live);
		history.Finalize(undoWrite, live);
		Check(Method(live).Name == "Echo" && References(live).All(x => x.Name == "Echo"), "legacy history undo restores definition and references");

		var redoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, root.CheckpointId, prepared.PostHeadCheckpointId, "redo");
		var rootLineage = history.Load(lineage.Manifest.LineageId);
		history.PlanNavigation(rootLineage, root.CheckpointId, prepared.PostHeadCheckpointId).Apply(live);
		history.Finalize(redoWrite, live);
		Check(Method(live).Name == "EchoHistory" && References(live).All(x => x.Name == "EchoHistory"), "legacy history redo reapplies definition and references");
		Check(history.Load(lineage.Manifest.LineageId).Manifest.HeadCheckpointId == prepared.PostHeadCheckpointId,
			"redo restores the unified history head");
		Console.WriteLine("PASS legacy-history commit+visible+undo+redo invalid-kind-version-rejected public-kind-closed");
	}

	static string RenameOperation(ModuleDef module, string newName) {
		var method = Method(module);
		var references = References(module).OrderBy(x => x.MDToken.Raw).Select(x => new Dictionary<string, object?> {
			["table"] = "MemberRef", ["token"] = Token(x.MDToken.Raw), ["old_name"] = method.Name.String, ["new_name"] = newName,
		}).ToArray();
		Check(references.Length != 0, "legacy history fixture has a generic MemberRef");
		return JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "legacy_symbol_rename", ["target_kind"] = "method",
			["definition"] = new Dictionary<string, object?> {
				["token"] = Token(method.MDToken.Raw), ["old_name"] = method.Name.String, ["new_name"] = newName,
			},
			["references"] = references,
		}, EditWire.JsonOptions);
	}

	static MethodDef Method(ModuleDef module) => module.GetTypes().Single(x => x.Name == "GenericMethodOwner`1").Methods.Single(x => x.Name.String is "Echo" or "EchoHistory");
	static MemberRef[] References(ModuleDef module) => module.GetTypes().SelectMany(x => x.Methods).Where(x => x.HasBody)
		.SelectMany(x => x.Body.Instructions).Select(x => x.Operand).SelectMany(x => x switch {
			MemberRef member => new[] { member },
			MethodSpec { Method: MemberRef member } => new[] { member },
			_ => Array.Empty<MemberRef>(),
		}).Where(x => x.IsMethodRef && x.Name.String is "Echo" or "EchoHistory").Distinct().ToArray();
	static string Token(uint token) => "0x" + token.ToString("x8");

	static void RejectPackage(EditHistoryModule history, byte[] package, string label) {
		var rejected = false;
		try { history.ValidatePackageForTesting(package); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { rejected = true; }
		Check(rejected, label + " is rejected with EDIT_OPERATION_VERSION_UNSUPPORTED");
	}

	static void RejectInvalidPackage(EditHistoryModule history, byte[] package, string label) {
		var rejected = false;
		try { history.ValidatePackageForTesting(package); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { rejected = true; }
		Check(rejected, label + " is rejected with EDIT_CHECKPOINT_INVALID");
	}

	static byte[] RewriteOperation(byte[] package, string? kind, int version) {
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		using (var input = new MemoryStream(package, writable: false))
		using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
			foreach (var entry in archive.Entries) {
				using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); entries[entry.FullName] = bytes.ToArray();
			}
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var head = manifest.Checkpoints.Single(x => x.CheckpointId == manifest.HeadCheckpointId);
		var operations = JsonSerializer.Deserialize<EditCheckpointOperations>(entries[head.OperationEntry], EditWire.JsonOptions)!;
		var operation = operations.Operations.Single();
		operation.KindVersion = version;
		if (kind != null) {
			operation.Kind = kind;
			operation.Forward["kind"] = kind;
		}
		entries[head.OperationEntry] = JsonSerializer.SerializeToUtf8Bytes(operations, EditWire.JsonOptions);
		head.OperationSha256 = EditWire.Sha256(entries[head.OperationEntry]);
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
			foreach (var entry in entries) { var target = archive.CreateEntry(entry.Key); using var stream = target.Open(); stream.Write(entry.Value); }
		return output.ToArray();
	}

	static byte[] RewriteVersionValue(byte[] package, JsonNode? version, bool updateHash = true) =>
		RewriteOperationDocument(package, document => document["operations"]![0]!["kind_version"] = version, updateHash);

	static byte[] RewriteOperationsContainer(byte[] package) =>
		RewriteOperationDocument(package, document => document["operations"] = new JsonObject { ["bad"] = true }, updateHash: true);

	static byte[] RewriteOperationDocument(byte[] package, Action<JsonNode> rewrite, bool updateHash) {
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		using (var input = new MemoryStream(package, writable: false))
		using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
			foreach (var entry in archive.Entries) {
				using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); entries[entry.FullName] = bytes.ToArray();
			}
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var head = manifest.Checkpoints.Single(x => x.CheckpointId == manifest.HeadCheckpointId);
		var document = JsonNode.Parse(entries[head.OperationEntry])!;
		rewrite(document);
		entries[head.OperationEntry] = JsonSerializer.SerializeToUtf8Bytes(document, EditWire.JsonOptions);
		if (updateHash) head.OperationSha256 = EditWire.Sha256(entries[head.OperationEntry]);
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
			foreach (var entry in entries) { var target = archive.CreateEntry(entry.Key); using var stream = target.Open(); stream.Write(entry.Value); }
		return output.ToArray();
	}

	static void Check(bool condition, string message) {
		if (!condition) throw new InvalidOperationException(message);
		Console.WriteLine("PASS " + message);
	}
}
