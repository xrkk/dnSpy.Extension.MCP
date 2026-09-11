using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class ReferenceAttributeCycleProbe {
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var reference = new TypeRefUser(source, "Example", "SelfAttribute", source.CorLibTypes.AssemblyRef);
		var constructor = new MemberRefUser(source, ".ctor", MethodSig.CreateInstance(source.CorLibTypes.Void), reference);
		reference.CustomAttributes.Add(new CustomAttribute(constructor));
		reference.CustomAttributes.Add(new CustomAttribute(constructor, new byte[] { 0xff, 0x42 }));
		var field = new FieldDefUser("AttributedReference", new FieldSig(new ClassSig(reference)), FieldAttributes.Public);
		owner.Fields.Add(field);
		var before = EditWorkspace.WriteCanonical(source);
		var graph = new ReferenceGraphCandidate(source);
		var id = graph.Bind(reference);
		var json = JsonSerializer.Serialize(graph.Nodes);
		owner.Fields.Remove(field);
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var nodes = JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(json)!;
		var resolve = ReferenceGraphCandidate.Restore(nodes, reloaded);
		var restored = (TypeRef)resolve(id);
		if (restored.CustomAttributes.Count != 2 || restored.CustomAttributes[0].IsRawBlob || !restored.CustomAttributes[1].IsRawBlob)
			throw new InvalidOperationException("Attribute order/representation changed");
		if (!ReferenceEquals(restored.CustomAttributes[0].Constructor, restored.CustomAttributes[1].Constructor)
			|| !ReferenceEquals(((MemberRef)restored.CustomAttributes[0].Constructor).Class, restored))
			throw new InvalidOperationException("Attribute constructor cycle lost");
		reloaded.GetTypes().Single(t => t.FullName == owner.FullName).Fields.Add(new FieldDefUser(field.Name, new FieldSig(new ClassSig(restored)), field.Attributes));
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Attribute graph restore image changed");
		var unchanged = EditWorkspace.WriteCanonical(reloaded);
		var malformed = JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(json)!;
		malformed[id].Link = id;
		try { ReferenceGraphCandidate.Restore(malformed, reloaded); throw new InvalidOperationException("Cyclic resolution scope accepted"); }
		catch (InvalidDataException) { }
		if (!unchanged.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Rejected graph mutated target");
		Console.WriteLine("SPIKE reference-attribute-cycle parsed+raw+order=True constructor-sharing+cycle=True scope-cycle-rejected=True exact-image=True");
	}
}
