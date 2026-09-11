using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class AccessorDeletionSnapshotProbe {
	public sealed class Snapshot {
		public string Kind { get; set; } = "";
		public string Owner { get; set; } = "";
		public int Position { get; set; }
		public uint SourceToken { get; set; }
		public byte[] Name { get; set; } = Array.Empty<byte>();
		public uint Attributes { get; set; }
		public EditStructuredSignatureCodec.CallNode? Signature { get; set; }
		public string? EventType { get; set; }
		public string[] Getters { get; set; } = Array.Empty<string>();
		public string[] Setters { get; set; } = Array.Empty<string>();
		public string[] Others { get; set; } = Array.Empty<string>();
		public string? Add { get; set; }
		public string? Remove { get; set; }
		public string? Invoke { get; set; }
		public uint? ConstantType { get; set; }
		public AttributeSnapshotCandidate.ValueNode? Constant { get; set; }
	}
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		MethodDef Method(string name, bool returnsInt = false) {
			var method = new MethodDefUser(name, MethodSig.CreateStatic(returnsInt ? source.CorLibTypes.Int32 : source.CorLibTypes.Void), MethodImplAttributes.IL,
				MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
			if (returnsInt) method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); owner.Methods.Add(method); return method;
		}
		var property = new PropertyDefUser("SnapshotProperty", new PropertySig(false, source.CorLibTypes.Int32) { ExtraData = new byte[] { 0xaa } }, PropertyAttributes.HasDefault) {
			Constant = new ConstantUser(7),
		};
		property.GetMethods.Add(Method("SnapshotGet1", true)); property.GetMethods.Add(Method("SnapshotGet2", true));
		property.SetMethods.Add(Method("SnapshotSet1")); property.SetMethods.Add(Method("SnapshotSet2"));
		property.OtherMethods.Add(Method("SnapshotOther1")); property.OtherMethods.Add(Method("SnapshotOther2"));
		var evt = new EventDefUser("SnapshotEvent", new TypeRefUser(source, "System", "Action", source.CorLibTypes.AssemblyRef), EventAttributes.SpecialName) {
			AddMethod = Method("SnapshotAdd"), RemoveMethod = Method("SnapshotRemove"), InvokeMethod = Method("SnapshotInvoke"),
		};
		evt.OtherMethods.Add(property.OtherMethods[0]); evt.OtherMethods.Add(property.OtherMethods[1]);
		owner.Properties.Insert(0, property); owner.Events.Insert(0, evt);
		var before = EditWorkspace.WriteCanonical(source);
		var graph = new ReferenceGraphCandidate(source);
		var ownerAddress = EditDefinitionAddress.Capture(source, owner);
		var nodes = new[] {
			new Snapshot { Kind = "property", Owner = ownerAddress, Position = 0, SourceToken = property.MDToken.Raw, Name = property.Name.Data, Attributes = (uint)property.Attributes,
				Signature = EditStructuredSignatureCodec.Capture(property.PropertySig, graph.Bind), ConstantType = (uint)property.Constant.Type,
				Constant = AttributeSnapshotCandidate.CaptureValue(property.Constant.Value, graph.Bind),
				Getters = property.GetMethods.Select(graph.Bind).ToArray(), Setters = property.SetMethods.Select(graph.Bind).ToArray(), Others = property.OtherMethods.Select(graph.Bind).ToArray() },
			new Snapshot { Kind = "event", Owner = ownerAddress, Position = 0, SourceToken = evt.MDToken.Raw, Name = evt.Name.Data, Attributes = (uint)evt.Attributes,
				EventType = graph.Bind(evt.EventType), Add = graph.Bind(evt.AddMethod), Remove = graph.Bind(evt.RemoveMethod), Invoke = graph.Bind(evt.InvokeMethod), Others = evt.OtherMethods.Select(graph.Bind).ToArray() },
		};
		var json = JsonSerializer.Serialize(nodes); var referenceJson = JsonSerializer.Serialize(graph.Nodes);
		var originalMethods = owner.Methods.ToArray();
		foreach (var target in new IMDTokenProvider[] { property, evt }) {
			using var operation = JsonDocument.Parse(JsonSerializer.Serialize(new { kind = target is PropertyDef ? "property_remove" : "event_remove", target = new { object_id = "subject" }, remove_mode = "reject_if_referenced" }));
			EditOperationRegistry.Apply(source, operation.RootElement, new Dictionary<string, IMDTokenProvider> { ["subject"] = target }, 0);
		}
		if (!owner.Methods.SequenceEqual(originalMethods)) throw new InvalidOperationException("Removal changed accessor definitions");
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var resolve = ReferenceGraphCandidate.Restore(JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(referenceJson)!, reloaded);
		var reloadedOwner = (TypeDef)EditDefinitionAddress.Resolve(reloaded, ownerAddress);
		var survivingMethods = reloadedOwner.Methods.ToArray();
		MethodDef Accessor(string id) => (MethodDef)resolve(id);
		foreach (var node in JsonSerializer.Deserialize<Snapshot[]>(json)!) {
			var targetOwner = (TypeDef)EditDefinitionAddress.Resolve(reloaded, node.Owner);
			if (node.Kind == "property") {
				var restored = new PropertyDefUser(new UTF8String(node.Name), (PropertySig)EditStructuredSignatureCodec.Restore(node.Signature!, resolve), (PropertyAttributes)node.Attributes) {
					Constant = new ConstantUser(AttributeSnapshotCandidate.RestoreValue(node.Constant!, resolve), (ElementType)node.ConstantType!.Value),
				};
				foreach (var id in node.Getters) restored.GetMethods.Add(Accessor(id));
				foreach (var id in node.Setters) restored.SetMethods.Add(Accessor(id));
				foreach (var id in node.Others) restored.OtherMethods.Add(Accessor(id));
				targetOwner.Properties.Insert(node.Position, restored);
				if (!restored.PropertySig.ExtraData.SequenceEqual(node.Signature!.Extra!)) throw new InvalidOperationException("Property extra signature lost");
				if (!restored.GetMethods.SequenceEqual(node.Getters.Select(Accessor)) || !restored.SetMethods.SequenceEqual(node.Setters.Select(Accessor))) throw new InvalidOperationException("Accessor order changed");
			}
			else {
				var restored = new EventDefUser(new UTF8String(node.Name), (ITypeDefOrRef)resolve(node.EventType!), (EventAttributes)node.Attributes) {
					AddMethod = Accessor(node.Add!), RemoveMethod = Accessor(node.Remove!), InvokeMethod = Accessor(node.Invoke!),
				};
				foreach (var id in node.Others) restored.OtherMethods.Add(Accessor(id));
				targetOwner.Events.Insert(node.Position, restored);
			}
		}
		if (!reloadedOwner.Methods.SequenceEqual(survivingMethods) || !ReferenceEquals(reloadedOwner.Properties[0].OtherMethods[0], reloadedOwner.Events[0].OtherMethods[0])) throw new InvalidOperationException("Surviving method identity lost");
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Accessor deletion restore image changed");
		Console.WriteLine("SPIKE accessor-deletion-snapshot actual-removal=property+event multi-accessors+order+constant+extra=True surviving-method-identity=True reload-restore-exact=True");
	}
}
