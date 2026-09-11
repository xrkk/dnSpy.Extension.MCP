using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class ReferenceGraphReloadProbe {
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var scope = new AssemblyRefUser("ExternalCandidate", new Version(2, 3, 4, 5), new PublicKeyToken(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "en-US") {
			Hash = new byte[] { 0x42, 0x13 }, Attributes = AssemblyAttributes.Retargetable,
		};
		var parent = new TypeRefUser(source, "External", "Outer", scope);
		var shared = new TypeRefUser(source, "", "Inner", parent);
		var equalButDistinct = new TypeRefUser(source, "", "Inner", parent);
		var survivor = new FieldDefUser("GraphSurvivor", new FieldSig(new ClassSig(shared)), FieldAttributes.Public);
		owner.Fields.Add(survivor);
		var fields = new[] {
			new FieldDefUser("GraphOne", new FieldSig(new ClassSig(shared)), FieldAttributes.Public),
			new FieldDefUser("GraphTwo", new FieldSig(new GenericInstSig(new ClassSig(shared), source.CorLibTypes.Int32)), FieldAttributes.Public),
			new FieldDefUser("GraphThree", new FieldSig(new ClassSig(equalButDistinct)), FieldAttributes.Public),
		};
		foreach (var field in fields) owner.Fields.Add(field);
		var before = EditWorkspace.WriteCanonical(source);
		var graph = new ReferenceGraphCandidate(source);
		var signatures = fields.Select(f => EditStructuredSignatureCodec.Capture(f.FieldSig, graph.Bind)).ToArray();
		var member = new MemberRefUser(source, "Call", MethodSig.CreateStatic(source.CorLibTypes.Void), shared);
		var methodSpec = new MethodSpecUser(member, new GenericInstMethodSig(source.CorLibTypes.Int32));
		var memberId = graph.Bind(member);
		var methodId = graph.Bind(methodSpec);
		var localId = graph.Bind(owner);
		var specId = graph.Bind(new TypeSpecUser(new SZArraySig(new ClassSig(shared))));
		var moduleRefId = graph.Bind(new ModuleRefUser(source, "other.netmodule"));
		var signaturesJson = JsonSerializer.Serialize(signatures);
		var graphJson = JsonSerializer.Serialize(graph.Nodes);
		foreach (var field in fields) owner.Fields.Remove(field);
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var newOwner = reloaded.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var survivingType = (TypeRef)((ClassSig)newOwner.Fields.Single(f => f.Name == "GraphSurvivor").FieldType).TypeDefOrRef;
		var survivingParent = (TypeRef)survivingType.ResolutionScope;
		var bindings = new Dictionary<string, IMDTokenProvider> {
			[graph.Bind(shared)] = survivingType, [graph.Bind(parent)] = survivingParent, [graph.Bind(scope)] = survivingParent.ResolutionScope,
		};
		var nodes = JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(graphJson)!;
		var resolve = ReferenceGraphCandidate.Restore(nodes, reloaded, bindings);
		var restoredSignatures = JsonSerializer.Deserialize<EditStructuredSignatureCodec.CallNode[]>(signaturesJson)!
			.Select(s => (FieldSig)EditStructuredSignatureCodec.Restore(s, resolve)).ToArray();
		for (var i = 0; i < fields.Length; i++) newOwner.Fields.Add(new FieldDefUser(fields[i].Name, restoredSignatures[i], fields[i].Attributes));
		var first = (TypeRef)((ClassSig)restoredSignatures[0].Type).TypeDefOrRef;
		var second = ((GenericInstSig)restoredSignatures[1].Type).GenericType.TypeDefOrRef;
		var third = ((ClassSig)restoredSignatures[2].Type).TypeDefOrRef;
		if (!ReferenceEquals(first, second) || ReferenceEquals(first, third) || ReferenceEquals(first, shared) || !ReferenceEquals(first, survivingType)) throw new InvalidOperationException("Cross-reload reference identity lost");
		var restoredScope = (AssemblyRef)((TypeRef)first.ResolutionScope).ResolutionScope;
		if (restoredScope.FullName != scope.FullName || !restoredScope.Hash.SequenceEqual(scope.Hash)) throw new InvalidOperationException("Assembly scope changed");
		if (!ReferenceEquals(((MethodSpec)resolve(methodId)).Method, resolve(memberId)) || !ReferenceEquals(((MemberRef)resolve(memberId)).Class, first)) throw new InvalidOperationException("Member/MethodSpec sharing lost");
		if (!ReferenceEquals(resolve(localId), newOwner) || ((TypeSpec)resolve(specId)).TypeSig is not SZArraySig || ((ModuleRef)resolve(moduleRefId)).Name != "other.netmodule") throw new InvalidOperationException("Reference kind restore mismatch");
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Cross-reload field restore image changed");
		var unchanged = EditWorkspace.WriteCanonical(reloaded);
		var wrong = new Dictionary<string, IMDTokenProvider>(bindings) { [graph.Bind(shared)] = new TypeRefUser(reloaded, "", "Wrong", survivingParent) };
		try { ReferenceGraphCandidate.Restore(nodes, reloaded, wrong); throw new InvalidOperationException("Wrong binding accepted"); }
		catch (InvalidDataException) { }
		var collapsed = new Dictionary<string, IMDTokenProvider>(bindings) { [graph.Bind(equalButDistinct)] = survivingType };
		try { ReferenceGraphCandidate.Restore(nodes, reloaded, collapsed); throw new InvalidOperationException("Collapsed binding accepted"); }
		catch (InvalidDataException) { }
		if (!unchanged.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Rejected binding mutated target");
		Console.WriteLine($"SPIKE reference-graph-reload nodes={graph.Nodes.Count} fields=3 distinct+shared+nested=True assembly-identity=True member+methodspec+typespec+moduleref=True surviving-identity=True wrong+collapsed-rejected=True exact-image=True");
	}
}
