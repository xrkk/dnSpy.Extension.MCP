using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Captures the complete field state permitted by the existing field_remove
// guard, then tests restoration after an actual registry removal and reload.
internal static class FieldDeletionSnapshotProbe {
	public sealed class FieldNode {
		public string Owner { get; set; } = "";
		public int Position { get; set; }
		public uint SourceToken { get; set; }
		public byte[] Name { get; set; } = Array.Empty<byte>();
		public uint Attributes { get; set; }
		public EditStructuredSignatureCodec.CallNode Signature { get; set; } = null!;
		public uint? ConstantType { get; set; }
		public AttributeSnapshotCandidate.ValueNode? Constant { get; set; }
		public byte[]? InitialValue { get; set; }
	}
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var modifier = new TypeRefUser(source, "System.Runtime.CompilerServices", "IsVolatile", source.CorLibTypes.AssemblyRef);
		var fields = new[] {
			new FieldDefUser(new UTF8String(new byte[] { 0x46, 0xff }), new FieldSig(new CModReqdSig(modifier,
				new ArraySig(source.CorLibTypes.Int32, 2, new uint[] { 3 }, new[] { -1 }))) { ExtraData = new byte[] { 0xaa, 0xbb } }, FieldAttributes.Public),
			new FieldDefUser("LiteralNan", new FieldSig(source.CorLibTypes.Single), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault) {
				Constant = new ConstantUser(BitConverter.Int32BitsToSingle(unchecked((int)0x7fc12345))),
			},
		};
		foreach (var field in fields) owner.Fields.Insert(0, field);
		var before = EditWorkspace.WriteCanonical(source);
		var graph = new ReferenceGraphCandidate(source);
		var snapshots = fields.Select(f => new FieldNode {
			Owner = EditDefinitionAddress.Capture(source, f.DeclaringType), Position = f.DeclaringType.Fields.IndexOf(f), SourceToken = f.MDToken.Raw,
			Name = (byte[])f.Name.Data.Clone(), Attributes = (uint)f.Attributes, Signature = EditStructuredSignatureCodec.Capture(f.FieldSig, graph.Bind),
			ConstantType = f.Constant == null ? null : (uint)f.Constant.Type,
			Constant = f.Constant == null ? null : AttributeSnapshotCandidate.CaptureValue(f.Constant.Value, graph.Bind),
			InitialValue = f.InitialValue == null ? null : (byte[])f.InitialValue.Clone(),
		}).ToArray();
		var json = JsonSerializer.Serialize(snapshots);
		var referenceJson = JsonSerializer.Serialize(graph.Nodes);
		foreach (var field in fields) {
			var map = new Dictionary<string, IMDTokenProvider> { ["delete-subject"] = field };
			using var operation = JsonDocument.Parse("{\"kind\":\"field_remove\",\"target\":{\"object_id\":\"delete-subject\"},\"remove_mode\":\"reject_if_referenced\"}");
			EditOperationRegistry.Apply(source, operation.RootElement, map, 0);
			if (field.DeclaringType != null || map.ContainsKey("delete-subject")) throw new InvalidOperationException("Actual field removal did not detach");
		}
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var resolve = ReferenceGraphCandidate.Restore(JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(referenceJson)!, reloaded);
		var restoredNodes = JsonSerializer.Deserialize<FieldNode[]>(json)!;
		foreach (var node in restoredNodes.OrderBy(n => n.Position)) {
			var targetOwner = (TypeDef)EditDefinitionAddress.Resolve(reloaded, node.Owner);
			var restored = new FieldDefUser(new UTF8String(node.Name), (FieldSig)EditStructuredSignatureCodec.Restore(node.Signature, resolve), (FieldAttributes)node.Attributes) {
				Constant = node.Constant == null ? null : new ConstantUser(AttributeSnapshotCandidate.RestoreValue(node.Constant, resolve), (ElementType)node.ConstantType!.Value),
				InitialValue = node.InitialValue, Rid = node.SourceToken & 0xffffff,
			};
			targetOwner.Fields.Insert(node.Position, restored);
			if (!restored.Name.Data.SequenceEqual(node.Name)) throw new InvalidOperationException("Restored raw name changed");
			if (!(restored.FieldSig.ExtraData ?? Array.Empty<byte>()).SequenceEqual(node.Signature.Extra ?? Array.Empty<byte>())) throw new InvalidOperationException("Signature extra bytes changed");
			if (restored.Constant != null && JsonSerializer.Serialize(AttributeSnapshotCandidate.CaptureValue(restored.Constant.Value, _ => throw new InvalidOperationException())) != JsonSerializer.Serialize(node.Constant))
				throw new InvalidOperationException("Constant type/bits changed");
		}
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Complete field deletion restoration image changed");
		Console.WriteLine("SPIKE field-deletion-snapshot actual-registry-removal=2 raw-name+signature-extra+array-bounds+nan=True positions=True reload-restore-exact=True");
	}
}
