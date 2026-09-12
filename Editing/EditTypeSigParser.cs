using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditTypeSigParser {
	readonly ModuleDef module;
	readonly int ownerTypeArity;
	readonly int ownerMethodArity;
	string text = string.Empty;
	int pos;

	public EditTypeSigParser(ModuleDef module, int ownerTypeArity = 0, int ownerMethodArity = 0) {
		this.module = module;
		this.ownerTypeArity = ownerTypeArity;
		this.ownerMethodArity = ownerMethodArity;
	}

	public TypeSig Parse(string input, bool allowVoid = false) {
		if (string.IsNullOrEmpty(input) || input.Any(char.IsWhiteSpace)) Invalid("TypeSig is empty or contains whitespace");
		if (input.Contains("modreq", StringComparison.Ordinal) || input.Contains("modopt", StringComparison.Ordinal) || input.Contains("fnptr", StringComparison.Ordinal) || input.Contains("sentinel", StringComparison.Ordinal))
			throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", Capability("typesig_modifier", "modreq/modopt/fnptr/sentinel are outside P02"));
		text = input; pos = 0;
		var sig = ParsePrimary();
		bool byRef = false;
		while (pos < text.Length) {
			if (byRef) Invalid("No suffix is allowed after byref");
			if (Take("[]")) sig = new SZArraySig(NonVoid(sig));
			else if (Peek('[')) {
				pos++; int rank = 1; while (Peek(',')) { pos++; rank++; }
				if (!Take("]")) Invalid("Unterminated array suffix");
				sig = new ArraySig(NonVoid(sig), (uint)rank);
			}
			else if (Take("*")) sig = new PtrSig(NonVoid(sig));
			else if (Take("&")) { sig = new ByRefSig(NonVoid(sig)); byRef = true; }
			else Invalid("Invalid TypeSig suffix at offset " + pos);
		}
		if (!allowVoid && sig.ElementType == ElementType.Void) Invalid("System.Void is only legal as a naked method return type");
		if (allowVoid && sig.ElementType == ElementType.Void && input != "System.Void") Invalid("System.Void cannot have suffixes");
		return sig;
	}

	TypeSig ParsePrimary() {
		if (Take("!!")) {
			var i = ReadUInt(); if (i >= ownerMethodArity) Invalid("Method generic parameter index is outside owner arity");
			return new GenericMVar((ushort)i);
		}
		if (Take("!")) {
			var i = ReadUInt(); if (i >= ownerTypeArity) Invalid("Type generic parameter index is outside owner arity");
			return new GenericVar((ushort)i);
		}
		bool? forcedValue = null;
		if (Take("valuetype:")) forcedValue = true;
		else if (Take("class:")) forcedValue = false;
		var start = pos;
		while (pos < text.Length) {
			var character = text[pos];
			if ("[]*&,".Contains(character)) break;
			// A '/' makes this a nested-type name; compiler-generated nested
			// names (<M>d__N) carry '<' as part of the name, not as generics.
			if (character == '<' && !text.Substring(start, pos - start).Contains('/')) break;
			pos++;
		}
		if (pos == start) Invalid("Expected a type name");
		var name = text.Substring(start, pos - start);
		var primitive = Primitive(name);
			if (Peek('<')) {
				if (primitive != null) Invalid("Primitive types cannot be generic instances");
				var tick = name.LastIndexOf('`');
				int arity;
				if (tick < 0 || !int.TryParse(name.Substring(tick + 1), out arity) || arity <= 0)
					throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("type_sig", "Generic type name must include a positive arity"));
			pos++;
			var args = new List<TypeSig>();
			if (Peek('>')) Invalid("Generic instance requires arguments");
			while (true) {
				var arg = ParsePrimaryWithSuffixUntilComma();
				args.Add(NonVoid(arg));
				if (Take(">")) break;
				if (!Take(",")) Invalid("Expected ',' or '>' in generic instance");
			}
			if (args.Count != arity) Invalid("Generic argument count does not match name arity");
			var type = Resolve(name);
			ClassOrValueTypeSig owner = type.ResolveTypeDef()?.IsValueType == true ? new ValueTypeSig(type) : new ClassSig(type);
			return new GenericInstSig(owner, args.ToArray());
		}
		if (primitive != null) return primitive;
		var resolved = Resolve(name);
		// A forced prefix pins the class/value element kind instead of relying
		// on the module resolver, which varies between module contexts.
		if (forcedValue == true) return new ValueTypeSig(resolved);
		if (forcedValue == false) return new ClassSig(resolved);
		return ToSig(resolved);
	}

	TypeSig ParsePrimaryWithSuffixUntilComma() {
		var sig = ParsePrimary(); bool byRef = false;
		while (pos < text.Length && text[pos] != ',' && text[pos] != '>') {
			if (byRef) Invalid("No suffix is allowed after byref");
			if (Take("[]")) sig = new SZArraySig(NonVoid(sig));
			else if (Peek('[')) { pos++; int rank = 1; while (Peek(',')) { pos++; rank++; } if (!Take("]")) Invalid("Unterminated array"); sig = new ArraySig(NonVoid(sig), (uint)rank); }
			else if (Take("*")) sig = new PtrSig(NonVoid(sig));
			else if (Take("&")) { sig = new ByRefSig(NonVoid(sig)); byRef = true; }
			else Invalid("Invalid generic argument suffix");
		}
		return sig;
	}

	ITypeDefOrRef Resolve(string fullName) {
		var local = module.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal));
		if (local != null) return local;
		var existing = module.GetTypeRefs().Where(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal)).ToList();
		if (existing.Count == 1) return existing[0];
		if (existing.Count > 1) Invalid("TypeSig resolves ambiguously: " + fullName);
		if (fullName.Contains('/')) {
			var parentName = fullName.Substring(0, fullName.LastIndexOf('/'));
			var nestedName = fullName.Substring(fullName.LastIndexOf('/') + 1);
			var parent = Resolve(parentName);
			if (parent is TypeDef parentType) {
				var nested = parentType.NestedTypes.FirstOrDefault(n => string.Equals(n.Name.String, nestedName, StringComparison.Ordinal));
				if (nested != null) return nested;
			}
			if (parent is TypeRef parentRef)
				return new TypeRefUser(module, string.Empty, nestedName, parentRef);
			throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("type_sig", "Nested external type scope could not be represented"));
		}
		var lastDot = fullName.LastIndexOf('.');
		var ns = lastDot < 0 ? string.Empty : fullName.Substring(0, lastDot);
		var name = lastDot < 0 ? fullName : fullName.Substring(lastDot + 1);
		var corlib = module.CorLibTypes.AssemblyRef;
		if (ns == "System") return new TypeRefUser(module, ns, name, corlib);
		var scope = module.GetAssemblyRefs().FirstOrDefault() ?? corlib;
		return new TypeRefUser(module, ns, name, scope);
	}

	TypeSig? Primitive(string name) => name switch {
		"System.Void" => module.CorLibTypes.Void,
		"System.Boolean" => module.CorLibTypes.Boolean,
		"System.Char" => module.CorLibTypes.Char,
		"System.SByte" => module.CorLibTypes.SByte,
		"System.Byte" => module.CorLibTypes.Byte,
		"System.Int16" => module.CorLibTypes.Int16,
		"System.UInt16" => module.CorLibTypes.UInt16,
		"System.Int32" => module.CorLibTypes.Int32,
		"System.UInt32" => module.CorLibTypes.UInt32,
		"System.Int64" => module.CorLibTypes.Int64,
		"System.UInt64" => module.CorLibTypes.UInt64,
		"System.Single" => module.CorLibTypes.Single,
		"System.Double" => module.CorLibTypes.Double,
		"System.String" => module.CorLibTypes.String,
		"System.Object" => module.CorLibTypes.Object,
		"System.IntPtr" => module.CorLibTypes.IntPtr,
		"System.UIntPtr" => module.CorLibTypes.UIntPtr,
		"System.TypedReference" => module.CorLibTypes.TypedReference,
		_ => null,
	};

	static TypeSig ToSig(ITypeDefOrRef type) => type.ResolveTypeDef()?.IsValueType == true ? new ValueTypeSig(type) : new ClassSig(type);
	static TypeSig NonVoid(TypeSig sig) { if (sig.ElementType == ElementType.Void) Invalid("System.Void is not legal in this context"); return sig; }
		int ReadUInt() {
			var start = pos;
			while (pos < text.Length && char.IsDigit(text[pos])) pos++;
			int value;
			if (start == pos || !int.TryParse(text.Substring(start, pos - start), out value) || value > 65535)
				throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("type_sig", "Invalid generic parameter index"));
			return value;
		}
	bool Peek(char c) => pos < text.Length && text[pos] == c;
	bool Take(string token) { if (!text.AsSpan(pos).StartsWith(token.AsSpan(), StringComparison.Ordinal)) return false; pos += token.Length; return true; }
	static TypeSig Invalid(string message) => throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("type_sig", message));
	static object Capability(string capability, string reason) => new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = capability, ["reason"] = reason };
}
