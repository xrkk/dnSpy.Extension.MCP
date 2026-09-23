using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class NewMethodHistoryProbe {
	static void Check(bool value, string label) {
		if (!value) throw new InvalidOperationException("FAILED: " + label);
		Console.WriteLine("PASS " + label);
	}

	static ModuleDef Artifact(int value) {
		var module = new ModuleDefUser("TestIL.dll");
		var owner = new TypeDefUser("TestIL", "Simple", null);
		module.Types.Add(owner);
		var method = new MethodDefUser("R059Added", MethodSig.CreateStatic(module.CorLibTypes.Int32),
			MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
		method.Body = new CilBody { MaxStack = 8, InitLocals = true };
		method.Body.Instructions.Add(Instruction.CreateLdcI4(value));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		owner.Methods.Add(method);
		return module;
	}

	static JsonElement Targets(string action) => JsonDocument.Parse(JsonSerializer.Serialize(new[] {
		new Dictionary<string, object?> { ["compiled"] = "TestIL.Simple::R059Added()", ["action"] = action },
	})).RootElement.Clone();

	static MethodDef Method(ModuleDef module) => module.GetTypes().Single(t => t.FullName == "TestIL.Simple")
		.Methods.Single(m => m.Name == "R059Added");

	static void Value(ModuleDef module, int expected, string label) {
		var body = Method(module).Body;
		Check(body != null && body.Instructions[0].GetLdcI4Value() == expected, label);
	}

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "t059-new-method-history"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		string? lineageId = null;
		string? firstHead = null;
		string? secondHead = null;
		for (var turn = 0; turn < 2; turn++) {
			using var workspace = EditWorkspace.CreateForTesting(live);
			using (var importer = new EditCSharpImporter(Artifact(turn == 0 ? 47 : 49), workspace.PrivateModule,
				workspace.ObjectIds, 0)) {
				var plan = importer.Compile(Targets(turn == 0 ? "add" : "replace_body"));
				foreach (var row in plan) {
					var payload = EditWire.CanonicalPayload(row.Operation);
					using var document = JsonDocument.Parse(payload);
					EditOperationRegistry.Apply(workspace.PrivateModule, document.RootElement, workspace.ObjectIds,
						workspace.NormalizedOperations.Count);
					workspace.NormalizedOperations.Add(payload);
				}
			}
			if (turn == 1) {
				Check(Method(live).MDToken.Rid == 0, "committed live addition retains zero RID");
				Check(Method(workspace.PrivateModule).MDToken.Rid != 0, "next private copy has serialized token");
			}
			var bound = history.ResolveBegin(workspace, null);
			var prepared = history.PrepareCommit(workspace, bound, workspace.NormalizedOperations,
				"review-t059-" + turn, 1, Array.Empty<string>());
			using (var source = ModuleDefMD.Load(workspace.BaselineBytes))
			using (EditOperationRegistry.BindSerializedTokens(source, live)) {
				var objects = new Dictionary<string, IMDTokenProvider>();
				for (var index = 0; index < workspace.NormalizedOperations.Count; index++) {
					using var operation = JsonDocument.Parse(workspace.NormalizedOperations[index]);
					EditOperationRegistry.ApplyPersisted(live, operation.RootElement, objects, index);
				}
			}
			history.Finalize(prepared, live);
			lineageId = prepared.Lineage.Manifest.LineageId;
			if (turn == 0) firstHead = prepared.PostHeadCheckpointId;
			else secondHead = prepared.PostHeadCheckpointId;
			Value(live, turn == 0 ? 47 : 49, "commit " + turn + " body");
			Check(history.Assess(lineageId, prepared.PostHeadCheckpointId, EditFingerprint.Compute(live)).Classification == "exact",
				"commit " + turn + " checkpoint exact");
		}
		var lineage = history.Load(lineageId!);
		var undoWrite = history.PrepareHeadMove(lineageId!, secondHead!, firstHead!, "undo");
		history.PlanNavigation(lineage, secondHead!, firstHead!).Apply(live);
		history.Finalize(undoWrite, live);
		Value(live, 47, "undo body");
		var redoWrite = history.PrepareHeadMove(lineageId!, firstHead!, secondHead!, "redo");
		history.PlanNavigation(history.Load(lineageId!), firstHead!, secondHead!).Apply(live);
		history.Finalize(redoWrite, live);
		Value(live, 49, "redo body");
		Console.WriteLine("PASS new-method-history");
	}
}
