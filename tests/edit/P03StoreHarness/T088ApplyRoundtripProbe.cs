using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class T088ApplyRoundtripProbe {
	static void Check(bool condition, string message) {
		if (!condition) throw new InvalidOperationException(message);
	}

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture),
			new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		using var workspace = EditWorkspace.CreateForTesting(live);
		var liveBefore = EditFingerprint.Compute(live);
		var sourceFileBefore = File.ReadAllBytes(fixture);
		using var addType = JsonDocument.Parse("""{"kind":"type_add","namespace":"T088","name":"Created","base_type":"System.Object"}""");
		var typeOutcome = EditApplyCandidateGate.Validate(
			() => EditOperationRegistry.Apply(workspace.PrivateModule, addType.RootElement, workspace.ObjectIds, 0),
			() => EditStructuralValidator.Validate(workspace.PrivateModule),
			() => { workspace.ValidateRoundtrip(); },
			() => workspace.RestoreCommittedState());
		Check(typeOutcome.CreatedObjectIds.Count == 1, "type_add must allocate one object ID");
		workspace.NormalizedOperations.Add(addType.RootElement.GetRawText());
		var acceptedFingerprint = workspace.PrivateFingerprint();
		var acceptedId = typeOutcome.CreatedObjectIds.Single();
		Check(workspace.ObjectIds.ContainsKey(acceptedId), "accepted ID missing");
		var owner = workspace.PrivateModule.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var operation = new Dictionary<string, object?> {
			["kind"] = "method_add", ["name"] = "T088Added",
			["owner_type"] = new { token = "0x" + owner.MDToken.Raw.ToString("x8") },
			["signature"] = new { return_type = "System.Void", has_this = false,
				parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() },
			["attributes"] = 22,
			["body"] = new { init_locals = false, max_stack = 1,
				locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
				instructions = new[] { new { opcode = "ret" } } },
		};
		using var addMethod = JsonDocument.Parse(JsonSerializer.Serialize(operation));
		try {
			EditApplyCandidateGate.Validate(
				() => EditOperationRegistry.Apply(workspace.PrivateModule, addMethod.RootElement, workspace.ObjectIds, 1),
				() => EditStructuralValidator.Validate(workspace.PrivateModule),
				() => { workspace.ValidateRoundtrip(); throw new IOException("T088 injected post-writer failure"); },
				() => workspace.RestoreCommittedState());
			throw new InvalidOperationException("injected fault was not observed");
		}
		catch (IOException ex) when (ex.Message == "T088 injected post-writer failure") { }
		Check(workspace.PrivateFingerprint() == acceptedFingerprint
			&& workspace.NormalizedOperations.Count == 1
			&& workspace.ObjectIds.Count == 1 && workspace.ObjectIds.ContainsKey(acceptedId)
			&& !workspace.PrivateModule.GetTypes().SelectMany(t => t.Methods).Any(m => m.Name == "T088Added"),
			"post-writer failure must restore accepted private graph, object map and revision input");
		Check(EditFingerprint.Compute(live) == liveBefore && File.ReadAllBytes(fixture).SequenceEqual(sourceFileBefore),
			"failed candidate changed live module or source file");
		var acceptedMethod = EditApplyCandidateGate.Validate(
			() => EditOperationRegistry.Apply(workspace.PrivateModule, addMethod.RootElement, workspace.ObjectIds, 1),
			() => EditStructuralValidator.Validate(workspace.PrivateModule),
			() => { workspace.ValidateRoundtrip(); },
			() => workspace.RestoreCommittedState());
		Check(acceptedMethod.CreatedObjectIds.Count == 1, "valid next method_add rejected after writer fault");
		workspace.NormalizedOperations.Add(addMethod.RootElement.GetRawText());
		workspace.ValidateRoundtrip();
		Console.WriteLine("PASS T088 real workspace type_add/method_add writer roundtrip, injected post-writer failure recovery, ID map and next legal operation");
	}
}
