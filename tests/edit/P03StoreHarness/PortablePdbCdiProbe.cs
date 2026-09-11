using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

internal static class PortablePdbCdiProbe {
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		module.CreatePdbState(PdbFileKind.EmbeddedPortablePDB);
		var tuple = new PortablePdbTupleElementNamesCustomDebugInfo(); tuple.Names.Add("Item"); tuple.Names.Add("");
		var refs = new PdbCompilationMetadataReferencesCustomDebugInfo(); refs.References.Add(new PdbCompilationMetadataReference("ref.dll", "global,Alias", (PdbCompilationMetadataReferenceFlags)3, 0x11223344, 0x55667788, Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")));
		var options = new PdbCompilationOptionsCustomDebugInfo(); options.Options.Add(new("language-version", "preview")); options.Options.Add(new("nullable", "enable"));
		var states = new PdbEditAndContinueStateMachineStateMapDebugInfo(); states.StateMachineStates.Add(new StateMachineStateInfo(-7, (StateMachineState)3));
		var samples = new PdbCustomDebugInfo[] {
			new PdbUnknownCustomDebugInfo(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), new byte[] { 0xff, 0, 1 }),
			new PdbEditAndContinueLocalSlotMapCustomDebugInfo(new byte[] { 1, 2, 3 }), new PdbEditAndContinueLambdaMapCustomDebugInfo(new byte[] { 4, 5 }),
			tuple, new PdbDefaultNamespaceCustomDebugInfo("Example.\ud800"), new PdbDynamicLocalVariablesCustomDebugInfo(new[] { true, false, true, true, false, false, false, false, true }),
			new PdbEmbeddedSourceCustomDebugInfo(new byte[] { 0, 0, 0, 0, 65, 0xff }), new PdbSourceLinkCustomDebugInfo(new byte[] { 123, 125 }), refs, options, states,
			new PrimaryConstructorInformationBlobDebugInfo(new byte[] { 9, 8, 7 }),
		};
		foreach (var sample in samples) module.CustomDebugInfos.Add(sample);
		var json = JsonSerializer.Serialize(samples.Select(PortablePdbCdiCandidate.Capture).ToArray());
		var before = EditWorkspace.WriteCanonical(module);
		module.CustomDebugInfos.Clear();
		foreach (var node in JsonSerializer.Deserialize<PortablePdbCdiCandidate.Node[]>(json)!) module.CustomDebugInfos.Add(PortablePdbCdiCandidate.Restore(node));
		if (json != JsonSerializer.Serialize(module.CustomDebugInfos.Select(PortablePdbCdiCandidate.Capture).ToArray())) throw new InvalidOperationException("Scalar CDI JSON changed");
		var after = EditWorkspace.WriteCanonical(module); if (!before.SequenceEqual(after)) throw new InvalidOperationException("Scalar CDI image changed");
		using var loaded = ModuleDefMD.Load(after, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
		if (loaded.CustomDebugInfos.Count != samples.Length || loaded.CustomDebugInfos.Select(c => c.Kind).Distinct().Count() != samples.Length) throw new InvalidOperationException("Scalar CDI reload changed");
		Console.WriteLine("SPIKE portable-pdb-cdi scalar-kinds=12 unknown+blobs+tuple+namespace+dynamic+embedded-source+source-link+metadata-refs+options+states+primary=True json-shape=True exact-embedded-image=True reload-kinds=12");
	}
}
