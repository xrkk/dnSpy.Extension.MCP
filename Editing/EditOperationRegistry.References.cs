using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

// T004-R02: explicit-scope reference synthesis (`reference_add`) and interface
// row creation (`interface_add`).  Both are v1 kinds; the descriptors never
// guess a scope and never reduce identity to RID0 or a FullName string.
internal static partial class EditOperationRegistry {
	internal static EditOperationOutcome ReferenceAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		if (!op.TryGetProperty("reference", out var descriptor) || descriptor.ValueKind != JsonValueKind.Object)
			Invalid("reference", "reference_add requires a reference descriptor object");
		var form = RequiredString(descriptor, "form");
		var (row, created) = form switch {
			"assembly_ref" => AssemblyRefRow(module, descriptor, map),
			"type_ref" => TypeRefRow(module, descriptor, map),
			"type_spec" => TypeSpecRow(module, descriptor, map),
			"member_ref" => MemberRefRow(module, descriptor, map),
			"method_spec" => MethodSpecRow(module, descriptor, map),
			_ => throw Validation("reference.form", "Unsupported reference form: " + form),
		};
		var id = ObjectId(index, 0);
		map[id] = row;
		return Outcome("reference_add", id, row, null, form,
			() => { ReleaseReference(module, row, created); map.Remove(id); });
	}

	static (IMDTokenProvider Row, bool Created) AssemblyRefRow(ModuleDef module, JsonElement descriptor, Dictionary<string, IMDTokenProvider> map) {
		var name = RequiredString(descriptor, "name");
		if (string.Equals(name, module.Assembly?.Name?.String, StringComparison.OrdinalIgnoreCase))
			throw Validation("reference.assembly_ref", "A type cannot be scoped to the module's own assembly; use the type definition object instead");
		var version = descriptor.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? new Version(v.GetString()!) : new Version(0, 0, 0, 0);
		var culture = descriptor.TryGetProperty("culture", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
		// Full public-key identity: the descriptor carries either a public key or a
		// token explicitly; neither kind is inferred from length.
		var keyKind = "none";
		var keyData = Array.Empty<byte>();
		if (descriptor.TryGetProperty("public_key_or_token", out var key) && key.ValueKind == JsonValueKind.Object) {
			keyKind = RequiredString(key, "kind");
			if (keyKind is not ("none" or "token" or "public_key"))
				throw Validation("reference.assembly_ref", "public_key_or_token.kind must be none, token or public_key");
			if (key.TryGetProperty("base64", out var data) && data.ValueKind == JsonValueKind.String)
				keyData = Convert.FromBase64String(data.GetString()!);
		}
		if (keyKind == "none" && keyData.Length != 0) throw Validation("reference.assembly_ref", "public key data requires an explicit kind");
		var flags = descriptor.TryGetProperty("flags", out var f) ? (AssemblyAttributes)(uint)f.GetUInt64() : (AssemblyAttributes)0;
		foreach (var existing in module.GetAssemblyRefs().Concat(map.Values.OfType<AssemblyRef>()))
			if (AssemblyIdentityMatches(existing, name, version, culture, keyKind, keyData, flags))
				return (existing, false);
		var created = new AssemblyRefUser(name, version, BuildPublicKeyOrToken(keyKind, keyData)) { Culture = culture, Attributes = flags };
		return (created, true);
	}

	static PublicKeyBase? BuildPublicKeyOrToken(string kind, byte[] data) => kind switch {
		"token" => new PublicKeyToken(data.Length == 0 ? null : data),
		"public_key" => new PublicKey(data.Length == 0 ? null : data),
		_ => null,
	};

	static bool AssemblyIdentityMatches(AssemblyRef existing, string name, Version version, string? culture,
		string keyKind, byte[] keyData, AssemblyAttributes flags) {
		if (!string.Equals(existing.Name?.String, name, StringComparison.OrdinalIgnoreCase)) return false;
		if (existing.Version != version) return false;
		if (!string.Equals(existing.Culture?.String ?? string.Empty, culture ?? string.Empty, StringComparison.Ordinal)) return false;
		if (existing.Attributes != flags) return false;
		var existingData = existing.PublicKeyOrToken?.Data ?? Array.Empty<byte>();
		return keyKind switch {
			"none" => existingData.Length == 0,
			_ => existingData.SequenceEqual(keyData),
		};
	}

	static IResolutionScope ResolveScope(ModuleDef module, Dictionary<string, IMDTokenProvider> map, JsonElement reference) {
		var row = ResolveReferenceRow(module, map, reference);
		return row as IResolutionScope ?? throw Validation("reference.scope", "Reference is not a resolution scope: " + row.GetType().Name);
	}

	static (IMDTokenProvider Row, bool Created) TypeRefRow(ModuleDef module, JsonElement descriptor, Dictionary<string, IMDTokenProvider> map) {
		if (!descriptor.TryGetProperty("scope", out var scopeElement) || scopeElement.ValueKind != JsonValueKind.Object)
			throw Validation("reference.type_ref", "type_ref requires an explicit scope descriptor");
		var scope = ResolveScope(module, map, scopeElement);
		var ns = descriptor.TryGetProperty("namespace", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
		var name = RequiredString(descriptor, "name");
		foreach (var existing in EnumerateTypeRefs(module, map))
			if (string.Equals(existing.Namespace?.String ?? string.Empty, ns, StringComparison.Ordinal)
				&& string.Equals(existing.Name?.String, name, StringComparison.Ordinal)
				&& SameScope(existing.ResolutionScope, scope))
				return (existing, false);
		return (new TypeRefUser(module, ns, name, scope), true);
	}

	static IEnumerable<TypeRef> EnumerateTypeRefs(ModuleDef module, Dictionary<string, IMDTokenProvider> map) =>
		module.GetTypeRefs().Concat(map.Values.OfType<TypeRef>()).Distinct();

	static bool SameScope(IResolutionScope? left, IResolutionScope? right) {
		if (ReferenceEquals(left, right)) return true;
		return left switch {
			AssemblyRef a when right is AssemblyRef b => string.Equals(a.Name?.String, b.Name?.String, StringComparison.OrdinalIgnoreCase)
				&& a.Version == b.Version && string.Equals(a.Culture?.String ?? string.Empty, b.Culture?.String ?? string.Empty, StringComparison.Ordinal)
				&& (a.PublicKeyOrToken?.Data ?? Array.Empty<byte>()).SequenceEqual(b.PublicKeyOrToken?.Data ?? Array.Empty<byte>())
				&& a.Attributes == b.Attributes,
			ModuleRef a when right is ModuleRef b => string.Equals(a.Name?.String, b.Name?.String, StringComparison.Ordinal),
			TypeRef a when right is TypeRef b => SameTypeRow(a, b),
			_ => false,
		};
	}

	static bool SameTypeRow(ITypeDefOrRef left, ITypeDefOrRef right) => left switch {
		TypeRef a when right is TypeRef b => string.Equals(a.Namespace?.String ?? string.Empty, b.Namespace?.String ?? string.Empty, StringComparison.Ordinal)
			&& string.Equals(a.Name?.String, b.Name?.String, StringComparison.Ordinal) && SameScope(a.ResolutionScope, b.ResolutionScope),
		TypeDef a when right is TypeDef b => ReferenceEquals(a, b) || (a.Rid != 0 && b.Rid != 0 && a.MDToken.Raw == b.MDToken.Raw),
		TypeSpec a when right is TypeSpec b => SignatureText(a.TypeSig) == SignatureText(b.TypeSig),
		_ => false,
	};

	static string SignatureText(TypeSig? signature) =>
		signature == null ? string.Empty : JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(signature, SignatureBind), EditWire.JsonOptions);
	static string CallText(CallingConventionSig? signature) =>
		signature == null ? string.Empty : JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(signature, SignatureBind), EditWire.JsonOptions);

	static IEnumerable<TypeSpec> EnumerateTypeSpecs(ModuleDef module, Dictionary<string, IMDTokenProvider> map) {
		var result = new List<TypeSpec>(map.Values.OfType<TypeSpec>());
		void Walk(TypeSig? signature) {
			for (var current = signature; current != null; current = current.Next)
				switch (current) {
				case TypeDefOrRefSig typeReference when typeReference.TypeDefOrRef is TypeSpec spec:
					result.Add(spec);
					break;
				case GenericInstSig instance:
					Walk(instance.GenericType);
					foreach (var argument in instance.GenericArguments) Walk(argument);
					break;
				case ModifierSig modifier:
					Walk(modifier.Modifier?.ToTypeSig());
					break;
				}
		}
		foreach (var type in module.GetTypes()) {
			Walk(type.BaseType?.ToTypeSig());
			foreach (var iface in type.Interfaces) Walk(iface.Interface?.ToTypeSig());
			foreach (var gp in type.GenericParameters) foreach (var constraint in gp.GenericParamConstraints) Walk(constraint.Constraint?.ToTypeSig());
			foreach (var field in type.Fields) Walk(field.FieldType);
			foreach (var method in type.Methods) {
				Walk(method.MethodSig?.RetType);
				if (method.MethodSig != null) foreach (var parameter in method.MethodSig.Params) Walk(parameter);
				foreach (var gp in method.GenericParameters) foreach (var constraint in gp.GenericParamConstraints) Walk(constraint.Constraint?.ToTypeSig());
				if (!method.HasBody) continue;
				foreach (var local in method.Body.Variables) Walk(local.Type);
				foreach (var instruction in method.Body.Instructions)
					if (instruction.Operand is TypeSpec spec) result.Add(spec);
				foreach (var handler in method.Body.ExceptionHandlers) Walk(handler.CatchType?.ToTypeSig());
			}
			foreach (var property in type.Properties) {
				Walk(property.PropertySig?.RetType);
				if (property.PropertySig != null) foreach (var parameter in property.PropertySig.Params) Walk(parameter);
			}
			foreach (var evt in type.Events) Walk(evt.EventType?.ToTypeSig());
		}
		return result.Distinct();
	}
	// Unassigned rows (Rid == 0) bind by object identity: the raw token of a
	// zero-Rid row still carries its table prefix (e.g. 0x01000000), so a Raw
	// check would alias every fresh row of one table into a single binding.
	static string SignatureBind(IMDTokenProvider row) => row.Rid == 0 ? "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(row) : "0x" + row.MDToken.Raw.ToString("x8");

	static (IMDTokenProvider Row, bool Created) TypeSpecRow(ModuleDef module, JsonElement descriptor, Dictionary<string, IMDTokenProvider> map) {
		if (!descriptor.TryGetProperty("signature", out var node) || node.ValueKind != JsonValueKind.Object)
			throw Validation("reference.type_spec", "type_spec requires a structured signature node");
		var signature = RestoreTypeNode(node, module, map);
		foreach (var existing in EnumerateTypeSpecs(module, map))
			if (SignatureText(existing.TypeSig) == SignatureText(signature)) return (existing, false);
		return (new TypeSpecUser(signature), true);
	}

	static (IMDTokenProvider Row, bool Created) MemberRefRow(ModuleDef module, JsonElement descriptor, Dictionary<string, IMDTokenProvider> map) {
		var kind = RequiredString(descriptor, "member_kind");
		var owner = ResolveReferenceRow(module, map, RequireChild(descriptor, "owner")) as ITypeDefOrRef
			?? throw Validation("reference.member_ref", "member owner must resolve to a type reference");
		var name = RequiredString(descriptor, "name");
		var signatureNode = RequireChild(descriptor, "signature");
		var callNode = signatureNode.Deserialize<EditStructuredSignatureCodec.CallNode>(EditWire.JsonOptions)
			?? throw Validation("reference.member_ref", "signature node is missing");
		if (kind == "method") {
			var methodSignature = (MethodSig)EditStructuredSignatureCodec.Restore(callNode, id => ResolveOperationReference(module, map, id));
			foreach (var existing in module.GetMemberRefs().Concat(map.Values.OfType<MemberRef>())) {
				if (!existing.IsMethodRef || !string.Equals(existing.Name?.String, name, StringComparison.Ordinal)) continue;
				if (existing.DeclaringType is not ITypeDefOrRef existingOwner || !SameTypeRow(existingOwner, owner)) continue;
				if (CallText(existing.MethodSig) == CallText(methodSignature)) return (existing, false);
			}
			return (new MemberRefUser(module, name, methodSignature, owner), true);
		}
		if (kind == "field") {
			var fieldSignature = (FieldSig)EditStructuredSignatureCodec.Restore(callNode, id => ResolveOperationReference(module, map, id));
			foreach (var existing in module.GetMemberRefs().Concat(map.Values.OfType<MemberRef>())) {
				if (existing.IsMethodRef || !string.Equals(existing.Name?.String, name, StringComparison.Ordinal)) continue;
				if (existing.DeclaringType is not ITypeDefOrRef existingOwner || !SameTypeRow(existingOwner, owner)) continue;
				if (CallText(existing.FieldSig) == CallText(fieldSignature)) return (existing, false);
			}
			return (new MemberRefUser(module, name, fieldSignature, owner), true);
		}
		throw Validation("reference.member_kind", "member_kind must be method or field");
	}

	static (IMDTokenProvider Row, bool Created) MethodSpecRow(ModuleDef module, JsonElement descriptor, Dictionary<string, IMDTokenProvider> map) {
		var method = ResolveReferenceRow(module, map, RequireChild(descriptor, "method")) as IMethodDefOrRef
			?? throw Validation("reference.method_spec", "method must resolve to a method reference");
		if (!descriptor.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
			throw Validation("reference.method_spec", "method_spec requires the full generic argument list");
		var args = arguments.EnumerateArray().Select(node => RestoreTypeNode(node, module, map)).ToArray();
		foreach (var existing in map.Values.OfType<MethodSpec>()) {
			if (!ReferenceEquals(existing.Method, method) && existing.Method is IMethodDefOrRef candidate && !SameMethodRow(candidate, method)) continue;
			if (existing.GenericInstMethodSig?.GenericArguments.Count == args.Length
				&& existing.GenericInstMethodSig.GenericArguments.Select(SignatureText).SequenceEqual(args.Select(SignatureText)))
				return (existing, false);
		}
		return (new MethodSpecUser(method, new GenericInstMethodSig(args)), true);
	}

	static bool SameMethodRow(IMethodDefOrRef left, IMethodDefOrRef right) => left switch {
		MethodDef a when right is MethodDef b => ReferenceEquals(a, b) || (a.Rid != 0 && b.Rid != 0 && a.MDToken.Raw == b.MDToken.Raw),
		MethodDef a when right is MemberRef b => b.IsMethodRef && string.Equals(a.Name?.String, b.Name?.String, StringComparison.Ordinal),
		MemberRef a when right is MemberRef b => a.IsMethodRef == b.IsMethodRef && string.Equals(a.Name?.String, b.Name?.String, StringComparison.Ordinal)
			&& a.DeclaringType is ITypeDefOrRef ao && b.DeclaringType is ITypeDefOrRef bo && SameTypeRow(ao, bo) && CallText(a.MethodSig) == CallText(b.MethodSig),
		_ => false,
	};

	static JsonElement RequireChild(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
			? value : throw Validation("reference." + name, name + " must be a reference descriptor");

	internal static IMDTokenProvider ResolveReferenceRow(ModuleDef module, Dictionary<string, IMDTokenProvider> map, JsonElement reference) {
		if (reference.ValueKind != JsonValueKind.Object) throw Validation("reference", "reference must be an object with token or object_id");
		if (reference.TryGetProperty("token", out var token))
			return ResolveToken(module, ParseToken(token.GetString()!));
		if (reference.TryGetProperty("object_id", out var id)) {
			var text = id.GetString()!;
			if (map.TryGetValue(text, out var found)) return found;
			throw Validation("reference", "Unknown object ID: " + text);
		}
		throw Validation("reference", "reference must carry token or object_id");
	}

	/// <summary>Restore a structured TypeNode whose reference bindings are tokens
	/// or batch object IDs.  This is the only sanctioned path for signatures that
	/// need references created earlier in the same transaction.</summary>
	internal static TypeSig RestoreTypeNode(JsonElement node, ModuleDef module, Dictionary<string, IMDTokenProvider> map) {
		var typed = node.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
			?? throw Validation("type", "structured type node is missing");
		try {
			return EditStructuredSignatureCodec.Restore(typed, id => ResolveOperationReference(module, map, id));
		}
		catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) {
			throw Validation("type", "structured type node could not be restored: " + ex.Message);
		}
	}

	/// <summary>Signature entry: legacy string (v1 domain, existing target rows)
	/// or a structured v2 node.</summary>
	internal static TypeSig ResolveTypeEntry(JsonElement entry, ModuleDef module, Dictionary<string, IMDTokenProvider> map, int ownerTypeArity = 0, int ownerMethodArity = 0, bool allowVoid = false) {
		if (entry.ValueKind == JsonValueKind.String) {
			var parser = new EditTypeSigParser(module, ownerTypeArity, ownerMethodArity);
			return parser.Parse(entry.GetString()!, allowVoid);
		}
		if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("kind", out var kind) && kind.GetString() == "type"
			&& entry.TryGetProperty("type", out var node)) return RestoreTypeNode(node, module, map);
		throw Validation("type", "a type entry must be a legacy string or a structured {kind:type,type:...} node");
	}

	internal static EditOperationOutcome InterfaceAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var owner = Ref<TypeDef>(module, RequireChild(op, "owner_type"), map);
		if (!op.TryGetProperty("interface", out var interfaceElement) || interfaceElement.ValueKind != JsonValueKind.Object)
			throw Validation("interface", "interface_add requires a structured interface entry");
		ITypeDefOrRef iface;
		TypeSig interfaceSig;
		if (interfaceElement.TryGetProperty("reference", out var reference)) {
			iface = ResolveReferenceRow(module, map, reference) as ITypeDefOrRef
				?? throw Validation("interface.reference", "interface reference must resolve to a type");
			interfaceSig = iface is TypeSpec spec ? spec.TypeSig : iface.ToTypeSig();
		}
		else if (interfaceElement.TryGetProperty("type", out var node)) {
			interfaceSig = RestoreTypeNode(node, module, map);
			iface = interfaceSig.ToTypeDefOrRef() ?? throw Validation("interface.type", "interface signature must resolve to a type");
		}
		else throw Validation("interface", "interface entry must carry reference or type");
		var comparer = new SigComparer();
		foreach (var existing in owner.Interfaces)
			try {
				if (comparer.Equals(existing.Interface?.ToTypeSig(), interfaceSig))
					throw Validation("interface", "the owner already implements this interface: " + iface.FullName);
			}
			catch (Exception ex) when (ex is not EditDomainException && ex is not ArgumentException) { }
		var row = new InterfaceImplUser(iface);
		owner.Interfaces.Add(row);
		var inserted = owner.Interfaces.Count - 1;
		return Outcome("interface_add", null, row, null, iface.FullName,
			() => { if (inserted < owner.Interfaces.Count && ReferenceEquals(owner.Interfaces[inserted], row)) owner.Interfaces.RemoveAt(inserted); else owner.Interfaces.Remove(row); });
	}

	internal static Dictionary<string, object?> CaptureInterfaceAddState(ModuleDef before, JsonElement forward, Dictionary<string, IMDTokenProvider> objects) {
		var owner = Ref<TypeDef>(before, RequireChild(forward, "owner_type"), objects);
		var ifaceElement = forward.GetProperty("interface");
		TypeSig interfaceSig;
		if (ifaceElement.TryGetProperty("reference", out var reference)) {
			var row = ResolveReferenceRow(before, objects, reference) as ITypeDefOrRef ?? throw Validation("interface.reference", "interface reference must resolve to a type");
			interfaceSig = row is TypeSpec spec ? spec.TypeSig : row.ToTypeSig();
		}
		else if (ifaceElement.TryGetProperty("type", out var node)) interfaceSig = RestoreTypeNode(node, before, objects);
		else throw Validation("interface", "interface entry must carry reference or type");
		return new() { ["interface_remove_state"] = new Dictionary<string, object?> {
			["owner"] = InverseReference(before, owner, objects),
			["index"] = owner.Interfaces.Count,
			["interface"] = JsonSerializer.Deserialize<Dictionary<string, object?>>(SignatureText(interfaceSig), EditWire.JsonOptions) ?? new(),
		} };
	}

	internal static EditOperationOutcome ApplyInterfaceRemoveState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var owner = Ref<TypeDef>(module, state.GetProperty("owner"), objects);
		var index = state.GetProperty("index").GetInt32();
		if (index < 0 || index >= owner.Interfaces.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var row = owner.Interfaces[index];
		owner.Interfaces.RemoveAt(index);
		return new EditOperationOutcome { Kind = "interface_remove", Target = owner.FullName,
			Undo = () => owner.Interfaces.Insert(Math.Min(index, owner.Interfaces.Count), row) };
	}

	internal static void ReleaseReference(ModuleDef module, IMDTokenProvider row, bool created) {
		if (!created) return;
		// Reference rows created by this op are graph-only objects (table-backed
		// rows are always reused, never re-created).  Release verifies through the
		// full signature/operand graph that no surviving edge still points at the
		// row; reverse-order inversion has already undone every user.
		if (IsReferencedByGraph(module, row))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}

	static bool IsReferencedByGraph(ModuleDef module, IMDTokenProvider row) {
		bool Same(IMDTokenProvider? candidate) => ReferenceEquals(candidate, row);
		void WalkSig(TypeSig? signature) {
			for (var current = signature; current != null; current = current.Next) {
				switch (current) {
				case TypeDefOrRefSig reference when Same(reference.TypeDefOrRef): throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				case GenericInstSig instance:
					WalkSig(instance.GenericType);
					foreach (var argument in instance.GenericArguments) WalkSig(argument);
					break;
				case ModifierSig modifier when Same(modifier.Modifier): throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				case ModifierSig modifier:
					WalkSig(modifier.Next);
					break;
				case FnPtrSig pointer:
					WalkCall(pointer.Signature);
					break;
				}
			}
		}
		foreach (var type in module.GetTypes()) {
			if (Same(type)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			WalkSig(type.BaseType?.ToTypeSig());
			foreach (var iface in type.Interfaces) if (Same(iface.Interface)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			foreach (var gp in type.GenericParameters) foreach (var constraint in gp.GenericParamConstraints) WalkSig(constraint.Constraint?.ToTypeSig());
			foreach (var field in type.Fields) { if (Same(field)) throw new EditDomainException("EDIT_HISTORY_CONFLICT"); WalkSig(field.FieldType); }
			foreach (var method in type.Methods) {
				if (Same(method)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				WalkSig(method.MethodSig?.RetType);
				if (method.MethodSig != null) foreach (var parameter in method.MethodSig.Params) WalkSig(parameter);
				foreach (var gp in method.GenericParameters) foreach (var constraint in gp.GenericParamConstraints) WalkSig(constraint.Constraint?.ToTypeSig());
				foreach (var ov in method.Overrides) if (Same(ov.MethodDeclaration) || Same(ov.MethodBody)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				if (!method.HasBody) continue;
				foreach (var local in method.Body.Variables) WalkSig(local.Type);
				foreach (var instruction in method.Body.Instructions) if (Same(instruction.Operand as IMDTokenProvider)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				foreach (var handler in method.Body.ExceptionHandlers) WalkSig(handler.CatchType?.ToTypeSig());
			}
			foreach (var property in type.Properties) { if (Same(property)) throw new EditDomainException("EDIT_HISTORY_CONFLICT"); WalkSig(property.PropertySig?.RetType); }
			foreach (var evt in type.Events) if (Same(evt.EventType)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		foreach (var member in module.GetMemberRefs()) {
			if (Same(member) || Same(member.DeclaringType)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			if (member.IsMethodRef) WalkCall(member.MethodSig);
			else WalkSig(member.FieldSig?.Type);
		}
		foreach (var reference in module.GetAssemblyRefs()) if (Same(reference)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return false;

		void WalkCall(CallingConventionSig? signature) {
			switch (signature) {
			case MethodSig method:
				WalkSig(method.RetType);
				foreach (var parameter in method.Params) WalkSig(parameter);
				break;
			case FieldSig field:
				WalkSig(field.Type);
				break;
			}
		}
	}

	static bool HasReferenceRowUsage(ModuleDef module, IMDTokenProvider row) {
		if (row.MDToken.Raw != 0) return false;
		bool Same(object? candidate) => ReferenceEquals(candidate, row);
		foreach (var type in module.GetTypes()) {
			if (Same(type.BaseType)) return true;
			if (type.Interfaces.Any(i => Same(i.Interface))) return true;
			if (type.GenericParameters.Any(gp => gp.GenericParamConstraints.Any(c => Same(c.Constraint)))) return true;
			foreach (var field in type.Fields) if (Same(field.FieldType)) return true;
			foreach (var method in type.Methods) {
				if (Same(method.MethodSig)) return true;
				foreach (var g in method.GenericParameters) if (g.GenericParamConstraints.Any(c => Same(c.Constraint))) return true;
				foreach (var ov in method.Overrides) if (Same(ov.MethodDeclaration)) return true;
				if (method.HasBody) {
					foreach (var local in method.Body.Variables) if (Same(local.Type)) return true;
					foreach (var instruction in method.Body.Instructions) if (Same(instruction.Operand)) return true;
					foreach (var handler in method.Body.ExceptionHandlers) if (Same(handler.CatchType)) return true;
				}
			}
			foreach (var property in type.Properties) if (Same(property.PropertySig)) return true;
			foreach (var evt in type.Events) if (Same(evt.EventType)) return true;
		}
		return module.GetMemberRefs().Any(m => Same(m.DeclaringType) || Same(m.MethodSig) || Same(m.FieldSig));
	}
}
