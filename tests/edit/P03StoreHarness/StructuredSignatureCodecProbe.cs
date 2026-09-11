using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using dnSpy.Extension.MCP.Editing;

internal static class StructuredSignatureCodecProbe {
	sealed class SignatureTokens : ISignatureWriterHelper {
		readonly Dictionary<object, uint> tokens = new(ReferenceEqualityComparer.Instance);
		public uint ToEncodedToken(ITypeDefOrRef value) {
			if (!tokens.TryGetValue(value, out var encoded)) {
				var tag = value switch { TypeDef => 0u, TypeRef => 1u, TypeSpec => 2u, _ => throw new InvalidDataException("Not a type reference") };
				encoded = ((uint)(tokens.Count + 1) << 2) | tag;
				tokens.Add(value, encoded);
			}
			return encoded;
		}
		public void Error(string message) => throw new InvalidDataException("Signature writer: " + message);
	}
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var identities = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
		var references = new Dictionary<string, IMDTokenProvider>();
		string Bind(IMDTokenProvider value) {
			if (!identities.TryGetValue(value, out var id)) {
				id = "r" + identities.Count; identities.Add(value, id); references.Add(id, value);
			}
			return id;
		}
		IMDTokenProvider Resolve(string id) => references[id];
		var writerTokens = new SignatureTokens();
		var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var method = owner.Methods.First();
		var reference = new TypeRefUser(module, "Example", "Generic`1", module.CorLibTypes.AssemblyRef);
		var modifier = new TypeRefUser(module, "System.Runtime.CompilerServices", "IsVolatile", module.CorLibTypes.AssemblyRef);
		var i4 = module.CorLibTypes.Int32;
		var compound = new CModReqdSig(modifier, new ArraySig(i4, 2, new uint[] { 3 }, new[] { -1 }));
		var varargs = new MethodSig(CallingConvention.VarArg | CallingConvention.HasThis | CallingConvention.Generic, 1, i4,
			new TypeSig[] { new GenericVar(0, owner) }, new TypeSig[] { new GenericMVar(0, method) }) { ExtraData = new byte[] { 0xaa, 0xff } };
		var types = new TypeSig[] {
			i4, new ClassSig(reference), new ValueTypeSig(reference), new GenericVar(0, owner), new GenericMVar(0, method),
			new GenericVar(1), new GenericMVar(1), new FnPtrSig(varargs), new GenericInstSig(new ClassSig(reference), i4),
			compound, new CModOptSig(modifier, i4), new SZArraySig(i4), new ValueArraySig(i4, 7), new ModuleSig(3, i4),
			new PtrSig(i4), new ByRefSig(i4), new PinnedSig(i4), new SentinelSig(),
		};
		foreach (var type in types) {
			var json = JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(type, Bind));
			var restored = EditStructuredSignatureCodec.Restore(JsonSerializer.Deserialize<EditStructuredSignatureCodec.TypeNode>(json)!, Resolve);
			if (json != JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(restored, Bind))) throw new InvalidOperationException("Type shape/identity changed: " + type.GetType().Name);
			if (!SignatureWriter.Write(writerTokens, type).SequenceEqual(SignatureWriter.Write(writerTokens, restored)))
				throw new InvalidOperationException("Independent type signature bytes changed: " + type.GetType().Name);
		}
		var calls = new List<CallingConventionSig> {
			new FieldSig(compound) { ExtraData = new byte[] { 0x80, 0xfe } }, varargs,
			new PropertySig(true, i4, i4),
			new LocalSig(new PinnedSig(new ByRefSig(i4)), new GenericVar(0, owner)),
			new GenericInstMethodSig(new GenericMVar(0, method)),
		};
		calls.AddRange(module.GetTypes().SelectMany(t => t.Fields).Select(f => f.FieldSig));
		calls.AddRange(module.GetTypes().SelectMany(t => t.Methods).Select(m => m.MethodSig));
		calls.AddRange(module.GetTypes().SelectMany(t => t.Properties).Select(p => p.PropertySig));
		foreach (var call in calls) {
			var json = JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(call, Bind));
			var restored = EditStructuredSignatureCodec.Restore(JsonSerializer.Deserialize<EditStructuredSignatureCodec.CallNode>(json)!, Resolve);
			if (json != JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(restored, Bind))) throw new InvalidOperationException("Calling shape/identity changed: " + call.GetType().Name);
			if (!SignatureWriter.Write(writerTokens, call).SequenceEqual(SignatureWriter.Write(writerTokens, restored)))
				throw new InvalidOperationException("Independent calling signature bytes changed: " + call.GetType().Name);
		}
		// Negative controls establish that the independent comparison sees data
		// whose loss can be hidden by a matching pair of capture/restore routines.
		var changedBounds = new CModReqdSig(modifier, new ArraySig(i4, 2, new uint[] { 3 }, new[] { 0 }));
		if (SignatureWriter.Write(writerTokens, compound).SequenceEqual(SignatureWriter.Write(writerTokens, changedBounds)))
			throw new InvalidOperationException("Independent oracle missed array bound change");
		var changedArity = varargs.Clone(); changedArity.GenParamCount++;
		if (SignatureWriter.Write(writerTokens, varargs).SequenceEqual(SignatureWriter.Write(writerTokens, changedArity)))
			throw new InvalidOperationException("Independent oracle missed generic arity change");
		var field = new FieldDefUser("SignatureRestore", new FieldSig(compound), FieldAttributes.Public);
		owner.Fields.Add(field);
		var before = EditWorkspace.WriteCanonical(module);
		var snapshot = JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(field.FieldSig, Bind));
		using var operation = JsonDocument.Parse("{\"kind\":\"field_update\",\"target\":{\"object_id\":\"subject\"},\"field_type\":\"System.Int32\"}");
		EditOperationRegistry.Apply(module, operation.RootElement, new Dictionary<string, IMDTokenProvider> { ["subject"] = field }, 0);
		field.FieldSig = (FieldSig)EditStructuredSignatureCodec.Restore(JsonSerializer.Deserialize<EditStructuredSignatureCodec.CallNode>(snapshot)!, Resolve);
		if (!ReferenceEquals(((CModReqdSig)field.FieldType).Modifier, modifier) || !before.SequenceEqual(EditWorkspace.WriteCanonical(module)))
			throw new InvalidOperationException("Candidate field restoration changed reference identity/image");
		var recursiveCall = MethodSig.CreateStatic(i4);
		var cycle = new FnPtrSig(recursiveCall); recursiveCall.RetType = cycle;
		try { EditStructuredSignatureCodec.Capture(cycle, Bind); throw new InvalidOperationException("Cycle accepted"); }
		catch (InvalidDataException) { }
		Console.WriteLine($"SPIKE structured-signature-codec types={types.Length} calls={calls.Count} json-shape+reference-identity=True independent-dnlib-bytes=True negative-controls=2 field-update-restore-exact=True cycle-rejected=True");
	}
}
