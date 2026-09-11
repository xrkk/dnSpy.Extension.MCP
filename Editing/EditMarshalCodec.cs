using System;
using System.Collections.Generic;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>P04 production marshal codec (IMP-005): one structured
/// representation shared by the public payload and the compiled-inverse state.
/// The shape set is the 13 writable dnlib marshal classes proven by the P03
/// round-28 harness probe (kept as the design reference per AUD-001); type
/// references in the public payload are signature strings, in the compiled
/// inverse they are structured-signature codec nodes.</summary>
internal static class EditMarshalCodec {
	public static MarshalType? Parse(ModuleDef module, JsonElement value, EditTypeSigParser parser) {
		if (value.ValueKind == JsonValueKind.Null) return null;
		var kind = RequiredString(value, "kind");
		switch (kind) {
			case "simple": return new MarshalType(Native(value.GetProperty("native").GetString()!));
			case "raw": return new RawMarshalType(Bytes(value, "data_base64", required: true));
			case "fixed_string": return new FixedSysStringMarshalType(value.GetProperty("size").GetInt32());
			case "fixed_array": return new FixedArrayMarshalType(value.GetProperty("size").GetInt32(), Native(value.GetProperty("element").GetString()!));
			case "array": return new ArrayMarshalType(Native(value.GetProperty("element").GetString()!),
				value.GetProperty("param_number").GetInt32(), value.GetProperty("size").GetInt32(), value.GetProperty("flags").GetInt32());
			case "safe_array": {
				var variant = Variant(value.GetProperty("variant").GetString()!);
				var subType = value.TryGetProperty("user_defined_type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
					? parser.Parse(typeValue.GetString()!).ToTypeDefOrRef() : null;
				return new SafeArrayMarshalType(variant, subType);
			}
			case "custom": {
				// dnlib's blob writer rejects null guid/native name (round-28
				// negative sample); the payload therefore requires both.
				var guid = Bytes(value, "guid_base64", required: true);
				var nativeName = Bytes(value, "native_name_base64", required: true);
				var cookie = Bytes(value, "cookie_base64", required: false);
				return new CustomMarshalType(new UTF8String(guid!), new UTF8String(nativeName!),
					value.TryGetProperty("custom_marshaler_type", out var marshaler) && marshaler.ValueKind == JsonValueKind.String
						? parser.Parse(marshaler.GetString()!).ToTypeDefOrRef() : null,
					cookie == null ? null : new UTF8String(cookie));
			}
			case "interface": return new InterfaceMarshalType(Native(value.GetProperty("native").GetString()!),
				value.GetProperty("iid_param_index").GetInt32());
			default: throw new ArgumentException("kind must be simple, raw, fixed_string, fixed_array, array, safe_array, custom or interface", "marshal.kind");
		}
	}

	/// <summary>Compiled-inverse capture as a JSON-friendly dictionary; type
	/// references are captured through the supplied structured-signature
	/// binder (the same mechanism generic constraints use).</summary>
	public static object Capture(MarshalType value, Func<TypeSig, object> captureType) => CaptureNode(value, captureType)!;

	static Dictionary<string, object?> CaptureNode(MarshalType value, Func<TypeSig, object> captureType) {
		switch (value) {
			case RawMarshalType raw: return new Dictionary<string, object?> { ["kind"] = "raw", ["data_base64"] = Convert.ToBase64String(raw.Data ?? Array.Empty<byte>()) };
			case FixedSysStringMarshalType fixedString: return new Dictionary<string, object?> { ["kind"] = "fixed_string", ["size"] = fixedString.Size };
			case SafeArrayMarshalType safeArray: {
				var node = new Dictionary<string, object?> { ["kind"] = "safe_array", ["variant"] = safeArray.VariantType.ToString() };
				if (safeArray.UserDefinedSubType != null) node["user_defined_type"] = captureType(safeArray.UserDefinedSubType.ToTypeSig());
				return node;
			}
			case FixedArrayMarshalType fixedArray: return new Dictionary<string, object?> {
				["kind"] = "fixed_array", ["size"] = fixedArray.Size, ["element"] = fixedArray.ElementType.ToString() };
			case ArrayMarshalType array: return new Dictionary<string, object?> {
				["kind"] = "array", ["element"] = array.ElementType.ToString(), ["param_number"] = array.ParamNumber,
				["size"] = array.Size, ["flags"] = array.Flags };
			case CustomMarshalType custom: {
				var node = new Dictionary<string, object?> { ["kind"] = "custom" };
				if (custom.Guid is UTF8String guid) node["guid_base64"] = Convert.ToBase64String(guid.Data ?? Array.Empty<byte>());
				if (custom.NativeTypeName is UTF8String nativeName) node["native_name_base64"] = Convert.ToBase64String(nativeName.Data ?? Array.Empty<byte>());
				if (custom.CustomMarshaler is ITypeDefOrRef customMarshaler) node["custom_marshaler_type"] = captureType(customMarshaler.ToTypeSig());
				if (custom.Cookie is UTF8String cookie) node["cookie_base64"] = Convert.ToBase64String(cookie.Data ?? Array.Empty<byte>());
				return node;
			}
			case InterfaceMarshalType @interface: return new Dictionary<string, object?> {
				["kind"] = "interface", ["native"] = @interface.NativeType.ToString(), ["iid_param_index"] = @interface.IidParamIndex };
			default:
				if (value.GetType() != typeof(MarshalType)) throw new InvalidOperationException("Unsupported marshal class: " + value.GetType().Name);
				return new Dictionary<string, object?> { ["kind"] = "simple", ["native"] = value.NativeType.ToString() };
		}
	}

	/// <summary>Restore from the compiled-inverse JSON node; structured type
	/// nodes resolve through the supplied restorer.</summary>
	public static MarshalType? Restore(JsonElement captured, Func<JsonElement, ITypeDefOrRef> restoreType) {
		if (captured.ValueKind == JsonValueKind.Null) return null!;
		if (captured.ValueKind == JsonValueKind.Undefined) return null!;
		var kind = RequiredString(captured, "kind");
		switch (kind) {
			case "simple": return new MarshalType(Native(RequiredString(captured, "native")));
			case "raw": return new RawMarshalType(captured.GetProperty("data_base64").GetBytesFromBase64());
			case "fixed_string": return new FixedSysStringMarshalType(captured.GetProperty("size").GetInt32());
			case "fixed_array": return new FixedArrayMarshalType(captured.GetProperty("size").GetInt32(), Native(RequiredString(captured, "element")));
			case "array": return new ArrayMarshalType(Native(RequiredString(captured, "element")),
				captured.GetProperty("param_number").GetInt32(), captured.GetProperty("size").GetInt32(), captured.GetProperty("flags").GetInt32());
			case "safe_array": {
				var subType = captured.TryGetProperty("user_defined_type", out var typeValue) && typeValue.ValueKind == JsonValueKind.Object
					? restoreType(typeValue) : null;
				return new SafeArrayMarshalType(Variant(RequiredString(captured, "variant")), subType);
			}
			case "custom": {
				var marshaler = captured.TryGetProperty("custom_marshaler_type", out var marshalerValue) && marshalerValue.ValueKind == JsonValueKind.Object
					? restoreType(marshalerValue) : null;
				return new CustomMarshalType(TextField(captured, "guid_base64"), TextField(captured, "native_name_base64"), marshaler, TextField(captured, "cookie_base64"));
			}
			case "interface": return new InterfaceMarshalType(Native(RequiredString(captured, "native")),
				captured.GetProperty("iid_param_index").GetInt32());
			default: throw new ArgumentException("Unknown marshal kind in inverse state", "kind");
		}
	}

	static NativeType Native(string name) {
		if (!Enum.TryParse<NativeType>(name, out var value) || !Enum.IsDefined(typeof(NativeType), value))
			throw new ArgumentException("Unknown native type: " + name, "marshal.native");
		return value;
	}
	static VariantType Variant(string name) {
		if (!Enum.TryParse<VariantType>(name, out var value) || !Enum.IsDefined(typeof(VariantType), value))
			throw new ArgumentException("Unknown variant type: " + name, "marshal.variant");
		return value;
	}
	static byte[]? Bytes(JsonElement value, string name, bool required) {
		if (!value.TryGetProperty(name, out var raw) || raw.ValueKind == JsonValueKind.Null) {
			if (required) throw new ArgumentException(name + " is required", "marshal." + name);
			return null;
		}
		return raw.GetBytesFromBase64();
	}
	static UTF8String TextField(JsonElement value, string name) {
		var bytes = Bytes(value, name, required: false);
		return bytes == null ? null! : new UTF8String(bytes);
	}
	static string RequiredString(JsonElement value, string name) {
		if (!value.TryGetProperty(name, out var raw) || raw.ValueKind != JsonValueKind.String)
			throw new ArgumentException(name + " is required", "marshal." + name);
		return raw.GetString()!;
	}
}
