using System;
using System.IO;
using dnlib.DotNet;

// Test-only structured marshal state; preserves parsed subtype and raw bytes.
internal static class MarshalSnapshotCandidate {
	public sealed class Node {
		public string Kind { get; set; } = "";
		public int Native { get; set; }
		public int Element { get; set; }
		public int Size { get; set; }
		public int Parameter { get; set; }
		public int Flags { get; set; }
		public int Variant { get; set; }
		public string? Type { get; set; }
		public byte[]? Data { get; set; }
		public byte[]? Guid { get; set; }
		public byte[]? NativeName { get; set; }
		public byte[]? Cookie { get; set; }
	}
	static byte[]? Bytes(byte[]? value) => value == null ? null : (byte[])value.Clone();
	public static Node Capture(MarshalType value, Func<IMDTokenProvider, string> bind) {
		var n = new Node { Native = (int)value.NativeType };
		switch (value) {
		case RawMarshalType raw: n.Kind = "raw"; n.Data = Bytes(raw.Data); break;
		case FixedSysStringMarshalType s: n.Kind = "fixed_string"; n.Size = s.Size; break;
		case SafeArrayMarshalType a: n.Kind = "safe_array"; n.Variant = (int)a.VariantType; n.Type = a.UserDefinedSubType == null ? null : bind(a.UserDefinedSubType); break;
		case FixedArrayMarshalType a: n.Kind = "fixed_array"; n.Size = a.Size; n.Element = (int)a.ElementType; break;
		case ArrayMarshalType a: n.Kind = "array"; n.Element = (int)a.ElementType; n.Parameter = a.ParamNumber; n.Size = a.Size; n.Flags = a.Flags; break;
		case CustomMarshalType c: n.Kind = "custom"; n.Guid = Bytes(c.Guid?.Data); n.NativeName = Bytes(c.NativeTypeName?.Data); n.Cookie = Bytes(c.Cookie?.Data); n.Type = c.CustomMarshaler == null ? null : bind(c.CustomMarshaler); break;
		case InterfaceMarshalType i: n.Kind = "interface"; n.Parameter = i.IidParamIndex; break;
		case MarshalType m when m.GetType() == typeof(MarshalType): n.Kind = "simple"; break;
		default: throw new InvalidDataException("Unsupported marshal class");
		}
		return n;
	}
	public static MarshalType Restore(Node n, Func<string, IMDTokenProvider> resolve) {
		UTF8String? Text(byte[]? b) => b == null ? null : new UTF8String(Bytes(b));
		ITypeDefOrRef? Type() => n.Type == null ? null : (ITypeDefOrRef)resolve(n.Type);
		MarshalType value = n.Kind switch {
			"raw" => new RawMarshalType(Bytes(n.Data)),
			"fixed_string" => new FixedSysStringMarshalType(n.Size),
			"safe_array" => new SafeArrayMarshalType((VariantType)n.Variant, Type()),
			"fixed_array" => new FixedArrayMarshalType(n.Size, (NativeType)n.Element),
			"array" => new ArrayMarshalType((NativeType)n.Element, n.Parameter, n.Size, n.Flags),
			"custom" => new CustomMarshalType(Text(n.Guid), Text(n.NativeName), Type(), Text(n.Cookie)),
			"interface" => new InterfaceMarshalType((NativeType)n.Native, n.Parameter),
			"simple" => new MarshalType((NativeType)n.Native),
			_ => throw new InvalidDataException("Unknown marshal kind"),
		};
		if ((int)value.NativeType != n.Native) throw new InvalidDataException("Marshal kind/native mismatch");
		return value;
	}
}
