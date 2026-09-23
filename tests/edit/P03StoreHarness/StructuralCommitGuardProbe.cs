using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class StructuralCommitGuardProbe {
	public static void RunParameterAdd(string fixture) {
		using var catalog = new EditSchemaCatalog();
		using var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-parameter-commit-" + Guid.NewGuid().ToString("N")));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var method = live.ResolveToken(0x0600000D) as MethodDef;
		Check(method != null && method.MethodSig.Params.Count == 0, "parameter fixture has empty target signature");
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
		Console.WriteLine("PASS parameter-add-checkpoint envelope=parameter_remove committed=true");
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
