using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

internal static class AttributeSnapshotProbe {
	sealed class WriterHelper : ICustomAttributeWriterHelper {
		public bool MustUseAssemblyName(IType type) => true;
		public void Error(string message) => throw new InvalidDataException(message);
	}
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var identities = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
		var values = new Dictionary<string, IMDTokenProvider>();
		string Bind(IMDTokenProvider value) {
			if (!identities.TryGetValue(value, out var id)) { id = "r" + identities.Count; identities.Add(value, id); values.Add(id, value); }
			return id;
		}
		var core = module.CorLibTypes;
		var type = new TypeRefUser(module, "Example", "MetadataAttribute", core.AssemblyRef);
		var ctor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(core.Void, core.Int32, core.Single, core.String, new SZArraySig(core.Int32), core.Object), type);
		var parsed = new CustomAttribute(ctor, new[] {
			new CAArgument(core.Int32, 42), new CAArgument(core.Single, BitConverter.Int32BitsToSingle(unchecked((int)0x7fc12345))),
			new CAArgument(core.String, new UTF8String(new byte[] { 0x41, 0xff })),
			new CAArgument(new SZArraySig(core.Int32), new List<CAArgument> { new(core.Int32, 1), new(core.Int32, -2) }),
			// Constructor declares object; dnlib stores the actual boxed type here.
			new CAArgument(core.String, new UTF8String("boxed")),
		}, new[] { new CANamedArgument(true, core.Boolean, "Flag", new CAArgument(core.Boolean, true)) });
		var raw = new CustomAttribute(ctor, new byte[] { 0xff, 0x00, 0x42 });
		var helper = new WriterHelper();
		foreach (var attribute in new[] { parsed, raw }) {
			var encoded = JsonSerializer.Serialize(AttributeSnapshotCandidate.Capture(attribute, Bind));
			var restored = AttributeSnapshotCandidate.Restore(JsonSerializer.Deserialize<AttributeSnapshotCandidate.AttributeNode>(encoded)!, id => values[id]);
			if (encoded != JsonSerializer.Serialize(AttributeSnapshotCandidate.Capture(restored, Bind))) throw new InvalidOperationException("Attribute structure/value changed");
			if (restored.IsRawBlob != attribute.IsRawBlob || !ReferenceEquals(restored.Constructor, attribute.Constructor)) throw new InvalidOperationException("Attribute representation/constructor changed");
			if (!CustomAttributeWriter.Write(helper, attribute).SequenceEqual(CustomAttributeWriter.Write(helper, restored))) throw new InvalidOperationException("Independent attribute bytes changed");
		}
		Console.WriteLine("SPIKE attribute-snapshot parsed+raw=True nan-bits+utf8+array+boxed+named=True constructor-identity=True independent-dnlib-bytes=True");
	}
}
