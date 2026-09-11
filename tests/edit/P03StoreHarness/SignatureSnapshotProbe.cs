using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Isolated representation experiment. No production operation or schema uses it.
internal static class SignatureSnapshotProbe {
	public sealed class Node {
		public string Kind { get; set; } = "";
		public string? Name { get; set; }
		public string? Namespace { get; set; }
		public uint Rank { get; set; }
		public uint[] Sizes { get; set; } = Array.Empty<uint>();
		public int[] LowerBounds { get; set; } = Array.Empty<int>();
		public Node[] Children { get; set; } = Array.Empty<Node>();
	}
	static Node Encode(TypeSig type, AssemblyRef scope) {
		Node Reference(ITypeDefOrRef value) {
			if (value is not TypeRef reference || !ReferenceEquals(reference.ResolutionScope, scope))
				throw new InvalidDataException("Scope outside this experiment");
			return new Node { Kind = "reference", Name = Convert.ToBase64String(reference.Name.Data), Namespace = Convert.ToBase64String(reference.Namespace.Data) };
		}
		return type switch {
			CorLibTypeSig primitive when primitive.ElementType == ElementType.I4 => new Node { Kind = "i4" },
			ClassSig cls => new Node { Kind = "class", Children = new[] { Reference(cls.TypeDefOrRef) } },
			CModReqdSig modifier => new Node { Kind = "modreq", Children = new[] { Reference(modifier.Modifier), Encode(modifier.Next, scope) } },
			CModOptSig modifier => new Node { Kind = "modopt", Children = new[] { Reference(modifier.Modifier), Encode(modifier.Next, scope) } },
			SZArraySig array => new Node { Kind = "szarray", Children = new[] { Encode(array.Next, scope) } },
			ArraySig array => new Node { Kind = "array", Rank = array.Rank, Sizes = array.Sizes.ToArray(), LowerBounds = array.LowerBounds.ToArray(), Children = new[] { Encode(array.Next, scope) } },
			GenericInstSig generic => new Node { Kind = "generic", Children = new[] { Encode(generic.GenericType, scope) }.Concat(generic.GenericArguments.Select(t => Encode(t, scope))).ToArray() },
			_ => throw new InvalidDataException("Signature outside this experiment"),
		};
	}
	static TypeSig Decode(Node node, ModuleDef module) {
		TypeRef Reference(Node value) {
			if (value.Kind != "reference") throw new InvalidDataException("Expected reference");
			return new TypeRefUser(module, new UTF8String(Convert.FromBase64String(value.Namespace!)), new UTF8String(Convert.FromBase64String(value.Name!)), module.CorLibTypes.AssemblyRef);
		}
		return node.Kind switch {
			"i4" => module.CorLibTypes.Int32,
			"class" => new ClassSig(Reference(node.Children.Single())),
			"modreq" when node.Children.Length == 2 => new CModReqdSig(Reference(node.Children[0]), Decode(node.Children[1], module)),
			"modopt" when node.Children.Length == 2 => new CModOptSig(Reference(node.Children[0]), Decode(node.Children[1], module)),
			"szarray" => new SZArraySig(Decode(node.Children.Single(), module)),
			"array" => new ArraySig(Decode(node.Children.Single(), module), node.Rank, node.Sizes, node.LowerBounds),
			"generic" when node.Children.Length >= 2 => new GenericInstSig((ClassOrValueTypeSig)Decode(node.Children[0], module), node.Children.Skip(1).Select(t => Decode(t, module)).ToArray()),
			_ => throw new InvalidDataException("Unknown signature node"),
		};
	}
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var scope = source.CorLibTypes.AssemblyRef;
		var genericType = new TypeRefUser(source, "System.Collections.Generic", "List`1", scope);
		var modifier = new TypeRefUser(source, "System.Runtime.CompilerServices", "IsVolatile", scope);
		var signature = new CModReqdSig(modifier, new ArraySig(
			new GenericInstSig(new ClassSig(genericType), source.CorLibTypes.Int32), 3, new uint[] { 2, 3 }, new[] { -1, 0 }));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var field = new FieldDefUser("P03ComplexSnapshot", new FieldSig(signature), FieldAttributes.Public);
		owner.Fields.Add(field);
		var before = EditWorkspace.WriteCanonical(source);
		var encoded = JsonSerializer.Serialize(Encode(field.FieldType, scope));
		owner.Fields.Remove(field);
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var recreated = Decode(JsonSerializer.Deserialize<Node>(encoded)!, reloaded);
		var newOwner = reloaded.GetTypes().Single(t => t.FullName == "TestIL.Members");
		newOwner.Fields.Add(new FieldDefUser(field.Name, new FieldSig(recreated), field.Attributes));
		if (!new SigComparer().Equals(signature, recreated)) throw new InvalidOperationException("Signature semantics changed");
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Complex signature restoration image changed");
		var array = (ArraySig)recreated.Next;
		if (array.Rank != 3 || !array.Sizes.SequenceEqual(new uint[] { 2, 3 }) || !array.LowerBounds.SequenceEqual(new[] { -1, 0 }))
			throw new InvalidOperationException("Array bounds lost");
		Console.WriteLine("SPIKE signature-snapshot modreq+generic+array-bounds=True json-roundtrip=True delete-reload-restore-exact=True");
	}
}
