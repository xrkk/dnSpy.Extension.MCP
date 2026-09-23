using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

// Exercises the persisted body inverse itself. Each corrupt inverse is applied
// to a fresh materialized module, and is rejected only after BodyReplace ran.
internal static class T065InverseBoundaryProbe {
	static void Check(bool condition, string label) {
		if (!condition) throw new InvalidOperationException("FAIL " + label);
		Console.WriteLine("PASS " + label);
	}
	static byte[] Fixture(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = module.GetTypes().First(type => !type.IsGlobalModuleType && !type.IsInterface);
		var method = new MethodDefUser("R065LocalInverse", MethodSig.CreateStatic(module.CorLibTypes.Void),
			MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
		owner.Methods.Add(method);
		var date = new TypeRefUser(module, "System", "DateTime", module.CorLibTypes.AssemblyRef);
		var list = new TypeRefUser(module, "System.Collections.Generic", "List`1", module.CorLibTypes.AssemblyRef);
		method.Body.Variables.Add(new Local(new ValueTypeSig(date), "value"));
		method.Body.Variables.Add(new Local(new GenericInstSig(new ClassSig(list), module.CorLibTypes.Int32), "generic"));
		method.Body.InitLocals = true;
		var instruction = Instruction.Create(OpCodes.Ret);
		method.Body.Instructions.Add(instruction);
		var document = module.PdbState?.Documents.FirstOrDefault()
			?? throw new InvalidOperationException("The generated source fixture must carry an embedded PDB document");
		instruction.SequencePoint = new SequencePoint { Document = document, StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 };
		return EditWorkspace.WriteCheckpointImage(module);
	}
	static MethodDef Subject(ModuleDef module) => module.GetTypes().SelectMany(type => type.Methods)
		.Single(method => method.Name == "R065LocalInverse");
	static JsonDocument Forward(MethodDef method) => JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "method_body_replace",
		["target"] = new Dictionary<string, object?> { ["token"] = "0x" + method.MDToken.Raw.ToString("x8") },
		["body"] = new Dictionary<string, object?> {
			["max_stack"] = 1, ["init_locals"] = false,
			["locals"] = Array.Empty<object>(), ["instructions"] = new[] { new { opcode = "ret" } },
			["exception_handlers"] = Array.Empty<object>(),
		},
	}, EditWire.JsonOptions));
	static void Case(byte[] fixture, string label, Action<JsonObject> mutate, bool success, bool exactImage = false) {
		using var module = ModuleDefMD.Load(fixture);
		var method = Subject(module);
		var originalToken = method.Body.LocalVarSigTok;
		Check(originalToken != 0 && module.ResolveToken(originalToken) is StandAloneSig,
			label + " generated StandAloneSig is present");
		using var forward = Forward(method);
		var objects = new Dictionary<string, IMDTokenProvider>();
		var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
		var beforeImage = EditWorkspace.WriteCheckpointImage(module);
		EditOperationRegistry.Apply(module, forward.RootElement, objects, 0);
		var afterImage = EditWorkspace.WriteCheckpointImage(module);
		var afterFingerprint = EditFingerprint.Compute(module);
		var afterBody = method.Body;
		var afterToken = afterBody.LocalVarSigTok;
		var afterDocuments = module.PdbState?.Documents.ToArray() ?? Array.Empty<PdbDocument>();
		var state = JsonNode.Parse(JsonSerializer.Serialize(inverse, EditWire.JsonOptions))!.AsObject();
		mutate(state);
		using var document = JsonDocument.Parse(state.ToJsonString());
		if (success) {
			EditOperationRegistry.ApplyCompiledInverse(module, document.RootElement, objects, 0);
			Check(method.Body.LocalVarSigTok == (state["body"]!["local_var_sig_token"] == null ? 0
				: uint.Parse(state["body"]!["local_var_sig_token"]!.ToJsonString())),
				label + " optional/original token behavior");
			Check(method.Body.Variables.Count == 2 && method.Body.Variables[0].Type is ValueTypeSig
				&& method.Body.Variables[1].Type is GenericInstSig, label + " structured locals preserved");
			if (exactImage) Check(EditWorkspace.WriteCheckpointImage(module).SequenceEqual(beforeImage), label + " image restored");
		} else {
			var rejected = false;
			try { EditOperationRegistry.ApplyCompiledInverse(module, document.RootElement, objects, 0); }
			catch (EditDomainException error) when (error.Code == "EDIT_HISTORY_CONFLICT") { rejected = true; }
			Check(rejected, label + " rejected");
			Check(ReferenceEquals(method.Body, afterBody) && method.Body.LocalVarSigTok == afterToken,
				label + " body and local token compensated");
			Check(EditFingerprint.Compute(module) == afterFingerprint && EditWorkspace.WriteCheckpointImage(module).SequenceEqual(afterImage),
				label + " live semantic and complete image compensated");
			Check((module.PdbState?.Documents.ToArray() ?? Array.Empty<PdbDocument>()).SequenceEqual(afterDocuments),
				label + " PDB document graph compensated");
		}
	}
	public static void Run(string fixture) {
		var generated = Fixture(fixture);
		Case(generated, "valid token", _ => { }, true, true);
		Case(generated, "legacy missing token", state => state["body"]!.AsObject().Remove("local_var_sig_token"), true);
		Case(generated, "valid zero token", state => state["body"]!["local_var_sig_token"] = 0, true);
		Case(generated, "wrong token type", state => state["body"]!["local_var_sig_token"] = "bad", false);
		Case(generated, "token overflow", state => state["body"]!["local_var_sig_token"] = 4294967296L, false);
		Case(generated, "wrong token table", state => state["body"]!["local_var_sig_token"] = 0x06000001, false);
		Case(generated, "missing signature row", state => state["body"]!["local_var_sig_token"] = 0x11ffffff, false);
		Case(generated, "local count drift", state => state["body"]!["locals"]!.AsArray().RemoveAt(1), false);
		Case(generated, "local type drift", state => state["body"]!["locals"]![0]!["type"] = "System.String", false);
		Console.WriteLine("PASS t065-inverse-boundary");
	}
	static byte[] RewritePackage(byte[] package, Action<JsonObject> mutate) {
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		using (var input = new MemoryStream(package, writable: false))
		using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
			foreach (var entry in archive.Entries) {
				using var source = entry.Open(); using var bytes = new MemoryStream(); source.CopyTo(bytes);
				entries[entry.FullName] = bytes.ToArray();
			}
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var head = manifest.Checkpoints.Single(point => point.CheckpointId == manifest.HeadCheckpointId);
		var document = JsonNode.Parse(entries[head.OperationEntry])!;
		mutate(document["operations"]![0]!["inverse"]!["state"]!.AsObject());
		entries[head.OperationEntry] = JsonSerializer.SerializeToUtf8Bytes(document, EditWire.JsonOptions);
		head.OperationSha256 = EditWire.Sha256(entries[head.OperationEntry]);
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
			foreach (var entry in entries) {
				using var target = archive.CreateEntry(entry.Key).Open(); target.Write(entry.Value);
			}
		return output.ToArray();
	}
	public static void RunPackage(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "t065-r03-package-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Fixture(fixture));
		var method = Subject(live);
		using var forward = Forward(method);
		var operation = forward.RootElement.GetRawText();
		using var workspace = EditWorkspace.CreateForTesting(live);
		EditOperationRegistry.Apply(workspace.PrivateModule, forward.RootElement, workspace.ObjectIds, 0);
		workspace.NormalizedOperations.Add(operation);
		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations,
			"t065-r03-inverse-package", 1, Array.Empty<string>());
		EditOperationRegistry.ApplyPersisted(live, forward.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		history.Finalize(prepared, live);
		var lineageId = prepared.Lineage.Manifest.LineageId;
		var headId = prepared.PostHeadCheckpointId;
		var rootId = prepared.Lineage.Manifest.Checkpoints.Single(point => point.ParentCheckpointId == null).CheckpointId;
		var realPackage = store.FinalBytes(lineageId);
		Check(realPackage.Length > 0 && history.Load(lineageId).Manifest.HeadCheckpointId == headId,
			"generated package has committed head");
		var postImage = EditWorkspace.WriteCheckpointImage(live);
		var postFingerprint = EditFingerprint.Compute(live);
		var postBody = method.Body;
		var postDocuments = live.PdbState?.Documents.ToArray() ?? Array.Empty<PdbDocument>();
		foreach (var (label, mutate, late) in new (string, Action<JsonObject>, bool)[] {
			("wrong token type", state => state["body"]!["local_var_sig_token"] = "bad", true),
			("token overflow", state => state["body"]!["local_var_sig_token"] = 4294967296L, true),
			("wrong token table", state => state["body"]!["local_var_sig_token"] = 0x06000001, true),
			("missing signature row", state => state["body"]!["local_var_sig_token"] = 0x11ffffff, true),
			("local count drift", state => state["body"]!["locals"]!.AsArray().RemoveAt(1), true),
			("local type drift", state => state["body"]!["locals"]![0]!["type"] = "System.String", true),
		}) {
			var copy = RewritePackage(realPackage, mutate);
			var phase = "none";
			try {
				var changed = history.ValidatePackageForTesting(copy);
				phase = "validated";
				history.PlanNavigation(changed, headId, rootId).Apply(live);
			} catch (EditDomainException error) when (error.Code is "EDIT_CHECKPOINT_INVALID" or "EDIT_HISTORY_CONFLICT") {
				phase = error.Code == "EDIT_CHECKPOINT_INVALID" ? "load-reject" : "navigation-reject";
			}
			Check(phase == (late ? "navigation-reject" : "load-reject"), "package " + label + " rejects at " + phase);
			Check(ReferenceEquals(method.Body, postBody) && EditFingerprint.Compute(live) == postFingerprint
				&& EditWorkspace.WriteCheckpointImage(live).SequenceEqual(postImage), "package " + label + " leaves live image/body untouched");
			Check((live.PdbState?.Documents.ToArray() ?? Array.Empty<PdbDocument>()).SequenceEqual(postDocuments)
				&& store.FinalBytes(lineageId).SequenceEqual(realPackage)
				&& history.Load(lineageId).Manifest.HeadCheckpointId == headId,
				"package " + label + " keeps PDB, package bytes and head exact");
		}
		Console.WriteLine("PASS t065-inverse-package");
	}
}
