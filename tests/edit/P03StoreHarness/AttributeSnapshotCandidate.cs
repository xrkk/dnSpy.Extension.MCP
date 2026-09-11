using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Test-only metadata preservation, never an arbitrary public attribute editor.
internal static class AttributeSnapshotCandidate {
	public sealed class AttributeNode {
		public string Constructor { get; set; } = "";
		public byte[]? Raw { get; set; }
		public uint BlobOffset { get; set; }
		public ArgumentNode[] Arguments { get; set; } = Array.Empty<ArgumentNode>();
		public NamedNode[] Named { get; set; } = Array.Empty<NamedNode>();
	}
	public sealed class NamedNode {
		public bool Field { get; set; }
		public byte[]? Name { get; set; }
		public EditStructuredSignatureCodec.TypeNode Type { get; set; } = null!;
		public ArgumentNode Argument { get; set; } = null!;
	}
	public sealed class ArgumentNode {
		public EditStructuredSignatureCodec.TypeNode Type { get; set; } = null!;
		public ValueNode Value { get; set; } = null!;
	}
	public sealed class ValueNode {
		public string Kind { get; set; } = "";
		public string? Scalar { get; set; }
		public byte[]? Bytes { get; set; }
		public int[]? Chars { get; set; }
		public EditStructuredSignatureCodec.TypeNode? Type { get; set; }
		public ArgumentNode[]? Arguments { get; set; }
	}
	public static AttributeNode Capture(CustomAttribute attribute, Func<IMDTokenProvider, string> bind) => new() {
		Constructor = bind(attribute.Constructor), Raw = attribute.RawData == null ? null : (byte[])attribute.RawData.Clone(), BlobOffset = attribute.BlobOffset,
		Arguments = attribute.ConstructorArguments.Select(a => CaptureArgument(a, bind)).ToArray(),
		Named = attribute.NamedArguments.Select(a => new NamedNode { Field = a.IsField, Name = a.Name?.Data == null ? null : (byte[])a.Name.Data.Clone(),
			Type = EditStructuredSignatureCodec.Capture(a.Type, bind), Argument = CaptureArgument(a.Argument, bind) }).ToArray(),
	};
	static ArgumentNode CaptureArgument(CAArgument argument, Func<IMDTokenProvider, string> bind) => new() {
		Type = EditStructuredSignatureCodec.Capture(argument.Type, bind), Value = CaptureValue(argument.Value, bind),
	};
	internal static ValueNode CaptureValue(object? value, Func<IMDTokenProvider, string> bind) => value switch {
		null => new() { Kind = "null" },
		UTF8String text => new() { Kind = "utf8", Bytes = (byte[])text.Data.Clone() },
		string text => new() { Kind = "string", Chars = text.Select(c => (int)c).ToArray() },
		TypeSig type => new() { Kind = "type", Type = EditStructuredSignatureCodec.Capture(type, bind) },
		CAArgument boxed => new() { Kind = "boxed", Arguments = new[] { CaptureArgument(boxed, bind) } },
		IList<CAArgument> array => new() { Kind = "array", Arguments = array.Select(a => CaptureArgument(a, bind)).ToArray() },
		float number => new() { Kind = "r4", Scalar = unchecked((uint)BitConverter.SingleToInt32Bits(number)).ToString("x8") },
		double number => new() { Kind = "r8", Scalar = unchecked((ulong)BitConverter.DoubleToInt64Bits(number)).ToString("x16") },
		char character => new() { Kind = "char", Scalar = ((int)character).ToString(CultureInfo.InvariantCulture) },
		bool or byte or sbyte or short or ushort or int or uint or long or ulong => new() { Kind = value.GetType().Name, Scalar = Convert.ToString(value, CultureInfo.InvariantCulture) },
		_ => throw new InvalidDataException("Unsupported attribute value: " + value.GetType().FullName),
	};
	public static CustomAttribute Restore(AttributeNode node, Func<string, IMDTokenProvider> resolve) {
		var constructor = resolve(node.Constructor) as ICustomAttributeType ?? throw new InvalidDataException("Invalid attribute constructor");
		var args = node.Arguments.Select(a => RestoreArgument(a, resolve)).ToArray();
		var named = node.Named.Select(a => new CANamedArgument(a.Field, EditStructuredSignatureCodec.Restore(a.Type, resolve),
			a.Name == null ? null : new UTF8String((byte[])a.Name.Clone()), RestoreArgument(a.Argument, resolve))).ToArray();
		if (node.Raw == null) return new CustomAttribute(constructor, args, named, node.BlobOffset);
		var raw = new CustomAttribute(constructor, (byte[])node.Raw.Clone());
		foreach (var arg in args) raw.ConstructorArguments.Add(arg);
		foreach (var arg in named) raw.NamedArguments.Add(arg);
		return raw;
	}
	static CAArgument RestoreArgument(ArgumentNode node, Func<string, IMDTokenProvider> resolve) => new(EditStructuredSignatureCodec.Restore(node.Type, resolve), RestoreValue(node.Value, resolve));
	internal static object? RestoreValue(ValueNode node, Func<string, IMDTokenProvider> resolve) => node.Kind switch {
		"null" => null,
		"utf8" => new UTF8String((byte[])node.Bytes!.Clone()),
		"string" => new string(node.Chars!.Select(c => checked((char)c)).ToArray()),
		"type" => EditStructuredSignatureCodec.Restore(node.Type!, resolve),
		"boxed" => RestoreArgument(node.Arguments!.Single(), resolve),
		"array" => node.Arguments!.Select(a => RestoreArgument(a, resolve)).ToList(),
		"r4" => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(node.Scalar, 16))),
		"r8" => BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(node.Scalar, 16))),
		"char" => checked((char)int.Parse(node.Scalar!, CultureInfo.InvariantCulture)),
		"Boolean" => bool.Parse(node.Scalar!), "Byte" => byte.Parse(node.Scalar!, CultureInfo.InvariantCulture),
		"SByte" => sbyte.Parse(node.Scalar!, CultureInfo.InvariantCulture), "Int16" => short.Parse(node.Scalar!, CultureInfo.InvariantCulture),
		"UInt16" => ushort.Parse(node.Scalar!, CultureInfo.InvariantCulture), "Int32" => int.Parse(node.Scalar!, CultureInfo.InvariantCulture),
		"UInt32" => uint.Parse(node.Scalar!, CultureInfo.InvariantCulture), "Int64" => long.Parse(node.Scalar!, CultureInfo.InvariantCulture),
		"UInt64" => ulong.Parse(node.Scalar!, CultureInfo.InvariantCulture),
		_ => throw new InvalidDataException("Unknown attribute value kind"),
	};
}
