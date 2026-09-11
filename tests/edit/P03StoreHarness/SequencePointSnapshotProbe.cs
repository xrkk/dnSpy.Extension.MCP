using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class SequencePointSnapshotProbe {
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		module.CreatePdbState(PdbFileKind.EmbeddedPortablePDB);
		var method = module.GetTypes().SelectMany(t => t.Methods).First(m => m.HasBody && m.Body.Instructions.Count > 2);
		var document = SequencePointSnapshotCandidate.CreateDocument(new SequencePointSnapshotCandidate.DocumentNode {
			Url = "isolated-source.cs".Select(c => (int)c).ToArray(), Language = new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1"),
			ChecksumAlgorithm = new Guid("8829d00f-11b8-4213-878b-770e8597ac16"), Checksum = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray() });
		module.PdbState.Add(document);
		method.Body.Instructions[0].SequencePoint = new SequencePoint { Document = document, StartLine = 10, StartColumn = 2, EndLine = 10, EndColumn = 12 };
		method.Body.Instructions[1].SequencePoint = new SequencePoint { Document = document, StartLine = 0xfeefee, StartColumn = 0, EndLine = 0xfeefee, EndColumn = 0 };
		var local = new Local(module.CorLibTypes.Int32); method.Body.Variables.Add(local);
		var scope = new PdbScope { Start = method.Body.Instructions[0], End = null };
		var child = new PdbScope { Start = method.Body.Instructions[1], End = method.Body.Instructions.Last() };
		var importParent = new PdbImportScope(); importParent.Imports.Add(new PdbImportNamespace("System"));
		var importChild = new PdbImportScope { Parent = importParent };
		var coreAssembly = module.CorLibTypes.AssemblyRef;
		importChild.Imports.Add(new PdbImportAssemblyNamespace(coreAssembly, "System.Collections"));
		importChild.Imports.Add(new PdbImportType(module.CorLibTypes.String.TypeDefOrRef));
		importChild.Imports.Add(new PdbImportXmlNamespace("xml", "urn:snapshot"));
		importChild.Imports.Add(new PdbImportAssemblyReferenceAlias("core"));
		importChild.Imports.Add(new PdbAliasAssemblyReference("coreAlias", coreAssembly));
		importChild.Imports.Add(new PdbAliasNamespace("Alias", "System.Collections.Generic"));
		importChild.Imports.Add(new PdbAliasAssemblyNamespace("CoreSystem", coreAssembly, "System"));
		importChild.Imports.Add(new PdbAliasType("Text", module.CorLibTypes.String.TypeDefOrRef));
		scope.ImportScope = importChild; child.ImportScope = importChild;
		scope.Variables.Add(new PdbLocal(local, "snapshotLocal", PdbLocalAttributes.DebuggerHidden));
		child.Constants.Add(new PdbConstant("snapshotConstant", module.CorLibTypes.Int32, 37));
		scope.Scopes.Add(child); method.Body.PdbMethod = new PdbMethod { Scope = scope };
		var before = EditWorkspace.WriteCanonical(module);
		var refs = new Dictionary<string, IMDTokenProvider>(); var ids = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
		string Bind(IMDTokenProvider value) { if (!ids.TryGetValue(value, out var id)) { id = "r" + ids.Count; ids[value] = id; refs[id] = value; } return id; }
		var json = JsonSerializer.Serialize(BodySnapshotCandidate.Capture(method, Bind));
		var corrupt = JsonSerializer.Deserialize<BodySnapshotCandidate.Node>(json)!;
		var corruptId = corrupt.Imports.Keys.First(); corrupt.Imports[corruptId].Parent = corruptId;
		try { BodySnapshotCandidate.Restore(corrupt, method, id => refs[id]); throw new InvalidOperationException("Cyclic import scope accepted"); }
		catch (InvalidDataException e) when (e.Message == "Cyclic PDB import parent") { }
		method.Body = BodySnapshotCandidate.Restore(JsonSerializer.Deserialize<BodySnapshotCandidate.Node>(json)!, method, id => refs[id]);
		var restored = method.Body.Instructions[0].SequencePoint.Document;
		if (ReferenceEquals(restored, document) || !ReferenceEquals(restored, method.Body.Instructions[1].SequencePoint.Document)) throw new InvalidOperationException("Document sharing changed");
		if (json != JsonSerializer.Serialize(BodySnapshotCandidate.Capture(method, Bind))) throw new InvalidOperationException("Sequence point state changed");
		if (!ReferenceEquals(method.Body.PdbMethod.Scope.Variables[0].Local, method.Body.Variables.Last()) ||
			!ReferenceEquals(method.Body.PdbMethod.Scope.Scopes[0].Start, method.Body.Instructions[1]) ||
			!ReferenceEquals(method.Body.PdbMethod.Scope.ImportScope, method.Body.PdbMethod.Scope.Scopes[0].ImportScope)) throw new InvalidOperationException("PDB scope/local/import binding changed");
		var after = EditWorkspace.WriteCanonical(module);
		if (!before.SequenceEqual(after)) throw new InvalidOperationException("Embedded PDB image changed");
		using var reloaded = ModuleDefMD.Load(after, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		var points = reloaded.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.SequencePoint?.Document?.Url == "isolated-source.cs").ToArray();
		if (points.Length != 2 || !points[0].SequencePoint.Document.CheckSum.SequenceEqual(document.CheckSum)) throw new InvalidOperationException("Embedded sequence points not persisted");
		var loaded = reloaded.GetTypes().SelectMany(t => t.Methods).Single(m => m.HasBody && m.Body.Instructions.Contains(points[0]));
		var scopes = new List<PdbScope>(); void Walk(PdbScope s) { scopes.Add(s); foreach (var c in s.Scopes) Walk(c); } Walk(loaded.Body.PdbMethod.Scope);
		if (!scopes.SelectMany(s => s.Variables).Any(v => v.Name == "snapshotLocal" && v.IsDebuggerHidden) ||
			!scopes.SelectMany(s => s.Constants).Any(c => c.Name == "snapshotConstant" && Equals(c.Value, 37))) throw new InvalidOperationException("Embedded scope data not persisted");
		if (!scopes.Any(s => s.ImportScope?.Imports.Count == 8 && s.ImportScope.Parent?.Imports.Count == 1) ||
			scopes.SelectMany(s => s.ImportScope?.Imports ?? Array.Empty<PdbImport>()).Select(i => i.Kind).Distinct().Count() != 8) throw new InvalidOperationException("Embedded import scopes not persisted");
		Console.WriteLine("SPIKE sequence-point-snapshot points=2 shared-document=True visible+hidden+checksum=True nested-scope+local+constant=True import-kinds=9 shared-parent+cycle-rejected=True exact-embedded-image=True reload-symbols=True standalone-pdb-created=False");
	}
}
