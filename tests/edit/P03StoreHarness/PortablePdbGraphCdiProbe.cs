using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

internal static class PortablePdbGraphCdiProbe {
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture)); module.CreatePdbState(PdbFileKind.EmbeddedPortablePDB);
		var methods = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Count > 3).Take(3).ToArray();
		if (methods.Length != 3) throw new InvalidOperationException("Fixture needs three methods");
		var owner = methods[0]; var other = methods[1]; var iteratorOwner = methods[2];
		var hoisted = new PdbStateMachineHoistedLocalScopesCustomDebugInfo(); hoisted.Scopes.Add(new StateMachineHoistedLocalScope(null, null)); hoisted.Scopes.Add(new StateMachineHoistedLocalScope(owner.Body.Instructions[0], owner.Body.Instructions[2]));
		var async = new PdbAsyncMethodCustomDebugInfo { KickoffMethod = other, CatchHandlerInstruction = owner.Body.Instructions[3] };
		async.StepInfos.Add(new PdbAsyncStepInfo(owner.Body.Instructions[1], other, other.Body.Instructions[1]));
		var iterator = new PdbIteratorMethodCustomDebugInfo(other);
		var document = SequencePointSnapshotCandidate.CreateDocument(new SequencePointSnapshotCandidate.DocumentNode { Url = "type.cs".Select(c => (int)c).ToArray(), Checksum = new byte[] { 1, 2 } }); module.PdbState.Add(document);
		// dnlib writes no MethodDebugInformation row for a method without sequence points, and the reader attaches
		// method CDIs only after GetMethod succeeds, so graph CDIs need sequence points on their owner to roundtrip.
		void AddSeq(MethodDef m, int idx, int line) => m.Body.Instructions[idx].SequencePoint = new SequencePoint { Document = document, StartLine = line, StartColumn = 1, EndLine = line, EndColumn = 2 };
		AddSeq(owner, 0, 10); AddSeq(owner, 1, 11); AddSeq(owner, 3, 12); AddSeq(iteratorOwner, 0, 20);
		var typeDocuments = new PdbTypeDefinitionDocumentsDebugInfo(); typeDocuments.Documents.Add(document);
		var samples = new PdbCustomDebugInfo[] { hoisted, async, iterator, typeDocuments };
		var identities = new Dictionary<object, string>(ReferenceEqualityComparer.Instance); var refs = new Dictionary<string, IMDTokenProvider>();
		string Bind(IMDTokenProvider value) { if (!identities.TryGetValue(value, out var id)) { id = "r" + identities.Count; identities[value] = id; refs[id] = value; } return id; }
		PortablePdbCdiCandidate.Node[] CaptureNodes() => new[] {
			PortablePdbCdiCandidate.Capture(hoisted, owner, Bind),
			PortablePdbCdiCandidate.Capture(async, owner, Bind),
			PortablePdbCdiCandidate.Capture(iterator, iteratorOwner, Bind),
			PortablePdbCdiCandidate.Capture(typeDocuments, owner, Bind),
		};
		var json = JsonSerializer.Serialize(CaptureNodes());
		owner.CustomDebugInfos.Add(hoisted); owner.CustomDebugInfos.Add(async); iteratorOwner.CustomDebugInfos.Add(iterator); owner.DeclaringType.CustomDebugInfos.Add(typeDocuments);
		var before = EditWorkspace.WriteCanonical(module);
		owner.CustomDebugInfos.Clear(); iteratorOwner.CustomDebugInfos.Clear(); owner.DeclaringType.CustomDebugInfos.Clear();
		var nodes = JsonSerializer.Deserialize<PortablePdbCdiCandidate.Node[]>(json)!;
		for (var i = 0; i < 2; i++) owner.CustomDebugInfos.Add(PortablePdbCdiCandidate.Restore(nodes[i], owner, id => refs[id]));
		iteratorOwner.CustomDebugInfos.Add(PortablePdbCdiCandidate.Restore(nodes[2], iteratorOwner, id => refs[id]));
		owner.DeclaringType.CustomDebugInfos.Add(PortablePdbCdiCandidate.Restore(nodes[3], owner, id => refs[id]));
		var recaptured = new[] {
			PortablePdbCdiCandidate.Capture(owner.CustomDebugInfos[0], owner, Bind),
			PortablePdbCdiCandidate.Capture(owner.CustomDebugInfos[1], owner, Bind),
			PortablePdbCdiCandidate.Capture(iteratorOwner.CustomDebugInfos[0], iteratorOwner, Bind),
			PortablePdbCdiCandidate.Capture(owner.DeclaringType.CustomDebugInfos[0], owner, Bind),
		};
		if (json != JsonSerializer.Serialize(recaptured)) throw new InvalidOperationException("Graph CDI JSON changed");
		var restoredAsync = (PdbAsyncMethodCustomDebugInfo)owner.CustomDebugInfos[1];
		if (!ReferenceEquals(restoredAsync.StepInfos[0].BreakpointMethod, other) || !ReferenceEquals(restoredAsync.StepInfos[0].BreakpointInstruction, other.Body.Instructions[1])) throw new InvalidOperationException("Async cross-method edge lost");
		var after = EditWorkspace.WriteCanonical(module); if (!before.SequenceEqual(after)) throw new InvalidOperationException("Graph CDI embedded image changed");
		using var loaded = ModuleDefMD.Load(after, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		var kinds = loaded.GetTypes().SelectMany(t => t.CustomDebugInfos.Concat(t.Methods.SelectMany(m => m.CustomDebugInfos))).Select(c => c.Kind).ToArray();
		var expectedKinds = samples.Select(s => s.Kind).ToArray();
		var missingKinds = expectedKinds.Where(kind => !kinds.Contains(kind)).ToArray();
		if (missingKinds.Length != 0)
			throw new InvalidOperationException("Graph CDI kind missing after reload: missing=" + string.Join(",", missingKinds) + "; loaded=" + string.Join(",", kinds));
		Console.WriteLine("SPIKE portable-pdb-graph-cdi kinds=4 hoisted+synthesized+async-cross-method+iterator+type-doc=True owner-sequence-points=True json-shape=True exact-embedded-image=True reload-kinds=True");
	}
}
