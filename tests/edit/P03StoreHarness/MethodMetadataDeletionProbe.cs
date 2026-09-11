using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

// A deletion/reload experiment, not a product inverse or runtime acceptance.
internal static class MethodMetadataDeletionProbe {
	public sealed class ParameterNode {
		public byte[] Name { get; set; } = Array.Empty<byte>();
		public ushort Sequence { get; set; }
		public ushort Attributes { get; set; }
		public MarshalSnapshotCandidate.Node? Marshal { get; set; }
		public uint? ConstantType { get; set; }
		public AttributeSnapshotCandidate.ValueNode? Constant { get; set; }
		public AttributeSnapshotCandidate.AttributeNode[] CustomAttributes { get; set; } = Array.Empty<AttributeSnapshotCandidate.AttributeNode>();
	}
	public sealed class ConstraintNode {
		public string Type { get; set; } = "";
		public AttributeSnapshotCandidate.AttributeNode[] CustomAttributes { get; set; } = Array.Empty<AttributeSnapshotCandidate.AttributeNode>();
	}
	public sealed class GenericNode {
		public byte[] Name { get; set; } = Array.Empty<byte>();
		public ushort Number { get; set; }
		public ushort Flags { get; set; }
		public ConstraintNode[] Constraints { get; set; } = Array.Empty<ConstraintNode>();
		public AttributeSnapshotCandidate.AttributeNode[] CustomAttributes { get; set; } = Array.Empty<AttributeSnapshotCandidate.AttributeNode>();
	}
	public sealed class Snapshot {
		public byte[] Name { get; set; } = Array.Empty<byte>();
		public ushort Attributes { get; set; }
		public ushort ImplAttributes { get; set; }
		public EditStructuredSignatureCodec.CallNode Signature { get; set; } = new();
		public BodySnapshotCandidate.Node? Body { get; set; }
		public ParameterNode[] Parameters { get; set; } = Array.Empty<ParameterNode>();
		public GenericNode[] Generics { get; set; } = Array.Empty<GenericNode>();
	}
	public static void Run(string fixture) {
		using var source = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var owner = source.GetTypes().Single(t => t.FullName == "TestIL.Members");
		var method = new MethodDefUser(new UTF8String(new byte[] { 77, 0xff }), MethodSig.CreateStatic(source.CorLibTypes.Void),
			MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
		owner.Methods.Insert(0, method);
		method.MethodSig.Generic = true; method.MethodSig.GenParamCount = 1;
		method.MethodSig.Params.Add(new GenericMVar(0, method));
		method.MethodSig.Params.Add(source.CorLibTypes.Int32);
		method.MethodSig.ExtraData = new byte[] { 0xab, 0xcd };
		method.Parameters.UpdateParameterTypes();
		var ctor = new MemberRefUser(source, ".ctor", MethodSig.CreateInstance(source.CorLibTypes.Void),
			new TypeRefUser(source, "System", "ObsoleteAttribute", source.CorLibTypes.AssemblyRef));
		CustomAttribute Attribute() => new CustomAttribute(ctor, new byte[] { 1, 0, 0, 0 });
		var ret = new ParamDefUser("return", 0); ret.CustomAttributes.Add(Attribute());
		var first = new ParamDefUser(new UTF8String(new byte[] { 80, 0xfe }), 1);
		var second = new ParamDefUser("optional", 2, ParamAttributes.Optional | ParamAttributes.HasDefault) { Constant = new ConstantUser(13) };
		second.CustomAttributes.Add(Attribute());
		first.MarshalType = new RawMarshalType(new byte[] { 0xff, 0x42 }); first.Attributes |= ParamAttributes.HasFieldMarshal;
		second.MarshalType = new ArrayMarshalType(NativeType.I4, 0, 3, 1); second.Attributes |= ParamAttributes.HasFieldMarshal;
		// Non-sequence order is intentional: restoration preserves the live list,
		// while the independent module writer determines metadata table order.
		method.ParamDefs.Add(second); method.ParamDefs.Add(ret); method.ParamDefs.Add(first);
		var generic = new GenericParamUser(0, GenericParamAttributes.ReferenceTypeConstraint, "T");
		generic.CustomAttributes.Add(Attribute());
		var constraint = new GenericParamConstraintUser(new TypeRefUser(source, "System", "IDisposable", source.CorLibTypes.AssemblyRef));
		constraint.CustomAttributes.Add(Attribute()); generic.GenericParamConstraints.Add(constraint); method.GenericParameters.Add(generic);
		method.Body = new CilBody { InitLocals = true, KeepOldMaxStack = true, MaxStack = 2, HeaderSize = 12 };
		var local = new Local(source.CorLibTypes.Int32); method.Body.Variables.Add(local);
		var exit = Instruction.Create(OpCodes.Ret);
		var leave = Instruction.Create(OpCodes.Leave, exit);
		var handler = Instruction.Create(OpCodes.Pop);
		var print = new MemberRefUser(source, "WriteLine", MethodSig.CreateStatic(source.CorLibTypes.Void, source.CorLibTypes.String),
			new TypeRefUser(source, "System", "Console", source.CorLibTypes.AssemblyRef));
		var instructions = new[] {
			Instruction.Create(OpCodes.Ldarg, method.Parameters[0]), Instruction.Create(OpCodes.Pop),
			Instruction.Create(OpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(unchecked((int)0x7fc12345))), Instruction.Create(OpCodes.Pop),
			Instruction.Create(OpCodes.Ldarg, method.Parameters[1]), Instruction.Create(OpCodes.Stloc, local), Instruction.Create(OpCodes.Ldloc, local),
			Instruction.Create(OpCodes.Switch, new[] { leave, leave }), Instruction.Create(OpCodes.Ldstr, "snapshot-body"), Instruction.Create(OpCodes.Call, print),
			leave, handler, Instruction.Create(OpCodes.Leave, exit), exit,
		};
		foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
		method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) { TryStart = instructions[0], TryEnd = handler,
			HandlerStart = handler, HandlerEnd = exit, CatchType = new TypeRefUser(source, "System", "Exception", source.CorLibTypes.AssemblyRef) });
		var before = EditWorkspace.WriteCanonical(source);
		var address = EditDefinitionAddress.Capture(source, owner);
		var graph = new ReferenceGraphCandidate(source);
		AttributeSnapshotCandidate.AttributeNode[] CaptureAttributes(IHasCustomAttribute value) => value.CustomAttributes.Select(a => AttributeSnapshotCandidate.Capture(a, graph.Bind)).ToArray();
		var snapshot = new Snapshot {
			Name = method.Name.Data, Attributes = (ushort)method.Attributes, ImplAttributes = (ushort)method.ImplAttributes,
			Signature = EditStructuredSignatureCodec.Capture(method.MethodSig, graph.Bind),
			Body = BodySnapshotCandidate.Capture(method, graph.Bind),
			Parameters = method.ParamDefs.Select(p => new ParameterNode { Name = p.Name.Data, Sequence = p.Sequence, Attributes = (ushort)p.Attributes,
				Marshal = p.MarshalType == null ? null : MarshalSnapshotCandidate.Capture(p.MarshalType, graph.Bind),
				ConstantType = p.Constant == null ? null : (uint)p.Constant.Type,
				Constant = p.Constant == null ? null : AttributeSnapshotCandidate.CaptureValue(p.Constant.Value, graph.Bind), CustomAttributes = CaptureAttributes(p) }).ToArray(),
			Generics = method.GenericParameters.Select(g => new GenericNode { Name = g.Name.Data, Number = g.Number, Flags = (ushort)g.Flags,
				CustomAttributes = CaptureAttributes(g), Constraints = g.GenericParamConstraints.Select(c => new ConstraintNode { Type = graph.Bind(c.Constraint), CustomAttributes = CaptureAttributes(c) }).ToArray() }).ToArray(),
		};
		var json = JsonSerializer.Serialize(snapshot); var references = JsonSerializer.Serialize(graph.Nodes);
		using (var operation = JsonDocument.Parse("{\"kind\":\"method_remove\",\"target\":{\"object_id\":\"subject\"},\"remove_mode\":\"reject_if_referenced\"}"))
			EditOperationRegistry.Apply(source, operation.RootElement, new Dictionary<string, IMDTokenProvider> { ["subject"] = method }, 0);
		if (owner.Methods.Contains(method)) throw new InvalidOperationException("Method was not removed");
		using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(source));
		var targetOwner = (TypeDef)EditDefinitionAddress.Resolve(reloaded, address);
		var value = JsonSerializer.Deserialize<Snapshot>(json)!;
		// Allocate an owned definition before resolving its generic-owner edge.
		// Only the detached reload is touched, never the live module.
		var restored = new MethodDefUser(new UTF8String(value.Name), MethodSig.CreateStatic(reloaded.CorLibTypes.Void),
			(MethodImplAttributes)value.ImplAttributes, (MethodAttributes)value.Attributes);
		targetOwner.Methods.Insert(0, restored);
		var resolve = ReferenceGraphCandidate.Restore(JsonSerializer.Deserialize<Dictionary<string, ReferenceGraphCandidate.Node>>(references)!, reloaded);
		void RestoreAttributes(IHasCustomAttribute target, AttributeSnapshotCandidate.AttributeNode[] attributes) {
			foreach (var attribute in attributes) target.CustomAttributes.Add(AttributeSnapshotCandidate.Restore(attribute, resolve));
		}
		restored.MethodSig = (MethodSig)EditStructuredSignatureCodec.Restore(value.Signature, resolve);
		restored.Parameters.UpdateParameterTypes();
		foreach (var p in value.Parameters) {
			var param = new ParamDefUser(new UTF8String(p.Name), p.Sequence, (ParamAttributes)p.Attributes);
			if (p.Marshal != null) param.MarshalType = MarshalSnapshotCandidate.Restore(p.Marshal, resolve);
			if (p.ConstantType.HasValue) param.Constant = new ConstantUser(AttributeSnapshotCandidate.RestoreValue(p.Constant!, resolve), (ElementType)p.ConstantType.Value);
			RestoreAttributes(param, p.CustomAttributes); restored.ParamDefs.Add(param);
		}
		foreach (var g in value.Generics) {
			var gp = new GenericParamUser(g.Number, (GenericParamAttributes)g.Flags, new UTF8String(g.Name));
			RestoreAttributes(gp, g.CustomAttributes);
			foreach (var c in g.Constraints) {
				var item = new GenericParamConstraintUser((ITypeDefOrRef)resolve(c.Type)); RestoreAttributes(item, c.CustomAttributes); gp.GenericParamConstraints.Add(item);
			}
			restored.GenericParameters.Add(gp);
		}
		if (value.Body != null) restored.Body = BodySnapshotCandidate.Restore(value.Body, restored, resolve);
		if (!ReferenceEquals(restored.Body.Instructions[0].Operand, restored.Parameters[0]) ||
			!ReferenceEquals(restored.Body.Instructions[5].Operand, restored.Body.Variables[0])) throw new InvalidOperationException("Body parameter/local identity lost");
		var targets = (IList<Instruction>)restored.Body.Instructions[7].Operand;
		if (!ReferenceEquals(targets[0], restored.Body.Instructions[10]) || !ReferenceEquals(targets[0], targets[1]) ||
			!ReferenceEquals(restored.Body.ExceptionHandlers[0].HandlerStart, restored.Body.Instructions[11])) throw new InvalidOperationException("Body branch/EH identity lost");
		if (BitConverter.SingleToInt32Bits((float)restored.Body.Instructions[2].Operand) != unchecked((int)0x7fc12345)) throw new InvalidOperationException("Body NaN bits lost");
		if (!ReferenceEquals(((GenericMVar)restored.MethodSig.Params[0]).OwnerMethod, restored)) throw new InvalidOperationException("Generic owner not rebound");
		if (!restored.ParamDefs.Select(p => p.Sequence).SequenceEqual(value.Parameters.Select(p => p.Sequence))) throw new InvalidOperationException("ParamDef order lost");
		if (!restored.ParamDefs[2].Name.Data.SequenceEqual(first.Name.Data) || !restored.MethodSig.ExtraData.SequenceEqual(value.Signature.Extra!)) throw new InvalidOperationException("Raw metadata lost");
		if (restored.ParamDefs[0].CustomAttributes.Count != 1 || restored.GenericParameters[0].GenericParamConstraints[0].CustomAttributes.Count != 1) throw new InvalidOperationException("Attachment lost");
		if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Method metadata restore image changed");
		if (restored.ParamDefs[0].MarshalType is not ArrayMarshalType || restored.ParamDefs[2].MarshalType is not RawMarshalType) throw new InvalidOperationException("Marshal representation lost");
		Console.WriteLine("SPIKE method-metadata-deletion actual-removal=True generic-owner-rebound=True parameter-order+constant+raw-name+signature-extra=True parameter+generic+constraint-attributes=True raw+parsed-marshal=True body-parameter+local+switch+catch+external-call+nan=True reload-restore-exact=True");
	}
}
