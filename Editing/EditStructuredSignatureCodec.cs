using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// Loss-free capture and restore of type and calling-convention signatures.
/// References and generic owners are bound by caller-supplied identity keys so
/// the same node shape serves live inverses and persisted packages; signatures
/// are never reduced to their FullName text.
/// </summary>
internal static class EditStructuredSignatureCodec {
	public sealed class TypeNode {
		public string Kind { get; set; } = "";
		public uint Value { get; set; }
		public string? Reference { get; set; }
		public string? Owner { get; set; }
		public TypeNode[] Children { get; set; } = Array.Empty<TypeNode>();
		public uint[] Sizes { get; set; } = Array.Empty<uint>();
		public int[] Bounds { get; set; } = Array.Empty<int>();
		public CallNode? Call { get; set; }
	}
	public sealed class CallNode {
		public string Kind { get; set; } = "";
		public byte Convention { get; set; }
		public uint Arity { get; set; }
		public byte[]? Extra { get; set; }
		public TypeNode? Result { get; set; }
		public TypeNode[] Parameters { get; set; } = Array.Empty<TypeNode>();
		public TypeNode[]? Optional { get; set; }
	}
	sealed class ReferenceComparer : IEqualityComparer<object> {
		public static readonly ReferenceComparer Instance = new();
		public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
		public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	public static TypeNode Capture(TypeSig type, Func<IMDTokenProvider, string> bind) => CaptureType(type, bind, new HashSet<object>(ReferenceComparer.Instance));
	public static CallNode Capture(CallingConventionSig sig, Func<IMDTokenProvider, string> bind) => CaptureCall(sig, bind, new HashSet<object>(ReferenceComparer.Instance));
	static TypeNode CaptureType(TypeSig type, Func<IMDTokenProvider, string> bind, HashSet<object> active) {
		if (type == null || !active.Add(type)) throw new InvalidDataException("Null or cyclic type signature");
		try {
			TypeNode Child(TypeSig value) => CaptureType(value, bind, active);
			var node = new TypeNode { Kind = type.GetType().Name };
			switch (type) {
			case CorLibTypeSig core: node.Value = (uint)core.ElementType; node.Reference = bind(core.TypeDefOrRef); break;
			case ClassOrValueTypeSig reference: node.Reference = bind(reference.TypeDefOrRef); break;
			case GenericSig generic:
				node.Value = generic.Number;
				node.Owner = generic.OwnerType != null ? bind(generic.OwnerType) : generic.OwnerMethod != null ? bind(generic.OwnerMethod) : null;
				break;
			case FnPtrSig pointer: node.Call = CaptureCall(pointer.Signature, bind, active); break;
			case GenericInstSig instance: node.Children = new[] { Child(instance.GenericType) }.Concat(instance.GenericArguments.Select(Child)).ToArray(); break;
			case ModifierSig modifier: node.Reference = bind(modifier.Modifier); node.Children = new[] { Child(modifier.Next) }; break;
			case ArraySig array: node.Value = array.Rank; node.Sizes = array.Sizes.ToArray(); node.Bounds = array.LowerBounds.ToArray(); node.Children = new[] { Child(array.Next) }; break;
			case ValueArraySig array: node.Value = array.Size; node.Children = new[] { Child(array.Next) }; break;
			case ModuleSig module: node.Value = module.Index; node.Children = new[] { Child(module.Next) }; break;
			case PtrSig or ByRefSig or SZArraySig or PinnedSig: node.Children = new[] { Child(type.Next) }; break;
			case SentinelSig: break;
			default: throw new InvalidDataException("Unknown signature class: " + node.Kind);
			}
			return node;
		}
		finally { active.Remove(type); }
	}
	static CallNode CaptureCall(CallingConventionSig sig, Func<IMDTokenProvider, string> bind, HashSet<object> active) {
		if (sig == null || !active.Add(sig)) throw new InvalidDataException("Null or cyclic calling signature");
		try {
			TypeNode Child(TypeSig type) => CaptureType(type, bind, active);
			var node = new CallNode { Kind = sig.GetType().Name, Convention = (byte)sig.GetCallingConvention(), Extra = sig.ExtraData == null ? null : (byte[])sig.ExtraData.Clone() };
			switch (sig) {
			case FieldSig field: node.Result = Child(field.Type); break;
			case MethodBaseSig method:
				node.Arity = method.GenParamCount; node.Result = Child(method.RetType);
				node.Parameters = method.Params.Select(Child).ToArray(); node.Optional = method.ParamsAfterSentinel?.Select(Child).ToArray(); break;
			case LocalSig locals: node.Parameters = locals.Locals.Select(Child).ToArray(); break;
			case GenericInstMethodSig instance: node.Parameters = instance.GenericArguments.Select(Child).ToArray(); break;
			default: throw new InvalidDataException("Unknown calling signature class: " + node.Kind);
			}
			return node;
		}
		finally { active.Remove(sig); }
	}
	public static TypeSig Restore(TypeNode node, Func<string, IMDTokenProvider> resolve) {
		TypeSig Child() => Restore(node.Children.Single(), resolve);
		ITypeDefOrRef Reference() => resolve(node.Reference ?? throw new InvalidDataException("Missing reference")) as ITypeDefOrRef ?? throw new InvalidDataException("Wrong reference kind: " + node.Reference + " on " + node.Kind);
		return node.Kind switch {
			nameof(CorLibTypeSig) => new CorLibTypeSig(Reference(), (ElementType)node.Value),
			nameof(ClassSig) => new ClassSig(Reference()),
			nameof(ValueTypeSig) => new ValueTypeSig(Reference()),
			nameof(GenericVar) => new GenericVar(node.Value, node.Owner == null ? null : resolve(node.Owner) as TypeDef ?? throw new InvalidDataException("Wrong generic owner")),
			nameof(GenericMVar) => new GenericMVar(node.Value, node.Owner == null ? null : resolve(node.Owner) as MethodDef ?? throw new InvalidDataException("Wrong generic owner")),
			nameof(FnPtrSig) => new FnPtrSig(Restore(node.Call ?? throw new InvalidDataException("Missing call signature"), resolve)),
			nameof(GenericInstSig) => new GenericInstSig((ClassOrValueTypeSig)Restore(node.Children.First(), resolve), node.Children.Skip(1).Select(n => Restore(n, resolve)).ToArray()),
			nameof(CModReqdSig) => new CModReqdSig(Reference(), Child()),
			nameof(CModOptSig) => new CModOptSig(Reference(), Child()),
			nameof(ArraySig) => new ArraySig(Child(), node.Value, node.Sizes, node.Bounds),
			nameof(ValueArraySig) => new ValueArraySig(Child(), node.Value),
			nameof(ModuleSig) => new ModuleSig(node.Value, Child()),
			nameof(PtrSig) => new PtrSig(Child()),
			nameof(ByRefSig) => new ByRefSig(Child()),
			nameof(SZArraySig) => new SZArraySig(Child()),
			nameof(PinnedSig) => new PinnedSig(Child()),
			nameof(SentinelSig) => new SentinelSig(),
			_ => throw new InvalidDataException("Unknown signature node: " + node.Kind),
		};
	}
	public static CallingConventionSig Restore(CallNode node, Func<string, IMDTokenProvider> resolve) {
		var convention = (CallingConvention)node.Convention;
		var args = node.Parameters.Select(n => Restore(n, resolve)).ToArray();
		var optional = node.Optional?.Select(n => Restore(n, resolve)).ToArray();
		TypeSig Result() => Restore(node.Result ?? throw new InvalidDataException("Missing result"), resolve);
		CallingConventionSig sig;
		switch (node.Kind) {
		case nameof(FieldSig): sig = new FieldSig(Result()); break;
		case nameof(MethodSig): sig = new MethodSig(convention, node.Arity, Result(), args, optional); break;
		case nameof(PropertySig): sig = new PropertySig(false, Result(), args) { CallingConvention = convention, GenParamCount = node.Arity, ParamsAfterSentinel = optional }; break;
		case nameof(LocalSig):
			var locals = new LocalSig(args); sig = locals; break;
		case nameof(GenericInstMethodSig):
			var generic = new GenericInstMethodSig(args); sig = generic; break;
		default: throw new InvalidDataException("Unknown calling signature node: " + node.Kind);
		}
		sig.Generic = (convention & CallingConvention.Generic) != 0;
		sig.HasThis = (convention & CallingConvention.HasThis) != 0;
		sig.ExplicitThis = (convention & CallingConvention.ExplicitThis) != 0;
		sig.ReservedByCLR = (convention & CallingConvention.ReservedByCLR) != 0;
		if (sig.GetCallingConvention() != convention) throw new InvalidDataException("Calling convention/type mismatch");
		sig.ExtraData = node.Extra == null ? null : (byte[])node.Extra.Clone();
		return sig;
	}
}
