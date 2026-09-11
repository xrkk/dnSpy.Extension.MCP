using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class BodySnapshotProbe {
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var before = EditWorkspace.WriteCanonical(module);
		var refs = new Dictionary<string, IMDTokenProvider>(); var ids = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
		string Bind(IMDTokenProvider value) { if (!ids.TryGetValue(value, out var id)) { id = "r" + ids.Count; ids[value] = id; refs[id] = value; } return id; }
		var count = 0;
		foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)) {
			var json = JsonSerializer.Serialize(BodySnapshotCandidate.Capture(method, Bind));
			var original = method.Body;
			method.Body = BodySnapshotCandidate.Restore(JsonSerializer.Deserialize<BodySnapshotCandidate.Node>(json)!, method, id => refs[id]);
			if (json != JsonSerializer.Serialize(BodySnapshotCandidate.Capture(method, Bind))) throw new InvalidOperationException("Body shape changed");
			if (method.Body.Instructions.Any(i => original.Instructions.Contains(i))) throw new InvalidOperationException("Old instruction reused");
			count++;
		}
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(module))) throw new InvalidOperationException("Fixture body image changed");
		var subject = module.GetTypes().SelectMany(t => t.Methods).First(m => m.HasBody);
		var saved = subject.Body;
		subject.Body = new CilBody(); subject.Body.Instructions.Add(Instruction.Create(OpCodes.Br, Instruction.Create(OpCodes.Ret)));
		try { BodySnapshotCandidate.Capture(subject, Bind); throw new InvalidOperationException("Detached target accepted"); }
		catch (InvalidDataException e) when (e.Message == "Detached instruction") { }
		finally { subject.Body = saved; }
		Console.WriteLine("SPIKE body-snapshot fixture-methods=" + count + " json-shape=True distinct-instructions=True exact-image=True detached-target-rejected=True");
	}
}
