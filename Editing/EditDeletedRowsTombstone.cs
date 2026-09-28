using System;
using System.Linq;
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// Operation-time owner for metadata rows that a preserving writer cannot
/// remove without leaving holes. v1/v2 retain the original Object marker.
/// The candidate v3 creates a fresh collision-free owner per deletion and
/// includes its complete content in the versioned semantic projection.
/// Inverse authority comes from a verified baseline replay, exact checkpoint
/// image and matching compiled inverse, never from a name or shape alone.
/// </summary>
internal static class EditDeletedRowsTombstone {
	static readonly AsyncLocal<bool> legacyMode = new();
	[ThreadStatic] static ModuleDef? verifiedV3Module;
	internal static IDisposable UseVerifiedV3(ModuleDef module) {
		var previous = verifiedV3Module; verifiedV3Module = module;
		return new Scope(() => verifiedV3Module = previous);
	}
	internal static void RequireVerifiedV3(ModuleDef module) {
		if (!LegacyMode && !ReferenceEquals(verifiedV3Module, module))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}
	internal static bool LegacyMode => legacyMode.Value;
	// Representation belongs to the lineage format. A v3 operation may add
	// an Object TypeRef after its no-Object baseline; that must not silently
	// turn its next deletion into the frozen v1/v2 marker shape.
	internal static bool UseObjectRepresentation(ModuleDef module) => LegacyMode;
	internal static IDisposable UseLegacy(bool legacy) {
		var previous = legacyMode.Value;
		legacyMode.Value = legacy;
		return new Scope(() => legacyMode.Value = previous);
	}
	sealed class Scope : IDisposable { readonly Action dispose; internal Scope(Action dispose) => this.dispose = dispose; public void Dispose() => dispose(); }
	internal const string Namespace = "dnspy.mcp.edit";
	internal const string NamePrefix = "DeletedMemberRows";
	internal const string ValueName = "DeletedMemberRowsValue";
	internal const string InterfaceName = "DeletedMemberRowsInterface";
	internal const TypeAttributes InterfaceAttributes = TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract;
	internal const string GlobalHostPrefix = "dnspy.mcp.edit.DeletedParamRows.";
	internal const TypeAttributes ValueAttributes = TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit;

	// Shape helpers are only guards inside a verified operation transition.
	// User definitions with the same name and flags remain ordinary metadata.
	internal static bool IsMarker(TypeDef type) =>
		string.Equals(type.Namespace?.String, Namespace, StringComparison.Ordinal)
		&& (type.Name.String ?? string.Empty).StartsWith(NamePrefix, StringComparison.Ordinal);

	internal const string GlobalTypePrefix = "dnspy.mcp.edit.DeletedType.";
	internal static bool IsLegacyTombstone(TypeDef type) =>
		IsMarker(type) && type.Attributes == (TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed)
		&& string.Equals(type.BaseType?.FullName, "System.Object", StringComparison.Ordinal)
		&& !type.HasGenericParameters && !type.HasCustomAttributes
		&& !type.HasInterfaces && type.ClassLayout == null && !type.HasDeclSecurities;
	internal static bool IsTombstone(TypeDef type) =>
		LegacyMode ? IsLegacyTombstone(type) : ReferenceEquals(verifiedV3Module, type.Module) && IsMarker(type)
		&& ((type.Attributes == (TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed)
			&& string.Equals(type.BaseType?.FullName, "System.Object", StringComparison.Ordinal))
			|| (type.Name.String?.StartsWith(ValueName, StringComparison.Ordinal) == true && type.Attributes == ValueAttributes
				&& string.Equals(type.BaseType?.FullName, "System.ValueType", StringComparison.Ordinal))
			|| (type.Name.String?.StartsWith(InterfaceName, StringComparison.Ordinal) == true && type.Attributes == InterfaceAttributes
				&& type.BaseType == null))
		&& !type.HasGenericParameters && !type.HasCustomAttributes
		&& !type.HasInterfaces && type.ClassLayout == null && !type.HasDeclSecurities;

	// v1/v2 keep the historical void() host. v3 uses a structurally valid
	// primitive signature with the original ParamDef sequence, so the owner
	// can be validated as ordinary metadata rather than hidden by shape.
	internal static bool IsParamHost(MethodDef method) =>
		method.Name.String is { } name && (name.StartsWith("d", StringComparison.Ordinal)
			|| (ReferenceEquals(method.DeclaringType, method.Module?.GlobalType)
				&& name.StartsWith(GlobalHostPrefix + "d", StringComparison.Ordinal)))
		&& !method.HasBody && method.ImplMap == null && method.Overrides.Count == 0
		&& method.GenericParameters.Count == 0 && method.CustomAttributes.Count == 0
		&& method.MethodSig is { } sig && !sig.Generic
		&& (UseObjectRepresentation(method.Module) ? sig.Params.Count == 0 : method.ParamDefs.Count == 1
			&& sig.Params.Count == method.ParamDefs[0].Sequence
			&& sig.Params.All(p => p.ElementType == ElementType.I4))
		&& sig.RetType.ElementType == ElementType.Void;

	internal static bool IsGlobalParamHost(MethodDef method) =>
		ReferenceEquals(method.DeclaringType, method.Module?.GlobalType)
		&& method.Name.String?.StartsWith(GlobalHostPrefix + "d", StringComparison.Ordinal) == true
		&& IsParamHost(method);

	internal static void ValidateParamHostAvailability(ModuleDef module) {
		var existing = module.Types.Where(IsMarker).ToArray();
		if (UseObjectRepresentation(module) && (existing.Length > 1 || existing.Length == 1 && !IsLegacyTombstone(existing[0])))
			throw new EditDomainException("EDIT_VALIDATION_FAILED");
	}

	internal static TypeDef GetOrCreate(ModuleDef module, string? exactName = null) {
		if (UseObjectRepresentation(module)) {
			var existing = module.Types.Where(IsMarker).ToArray();
			if (existing.Length > 1) throw new EditDomainException("EDIT_VALIDATION_FAILED");
			if (existing.Length == 1) {
				if (!IsLegacyTombstone(existing[0])) throw new EditDomainException("EDIT_VALIDATION_FAILED");
				return existing[0];
			}
		}
		var attempt = 0;
		string name;
		do {
			name = attempt == 0 ? NamePrefix : NamePrefix + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
			attempt++;
		} while (module.GetTypes().Any(t => string.Equals(t.FullName, Namespace + "." + name, StringComparison.Ordinal)));
		var hasObject = UseObjectRepresentation(module);
		var valueRef = module.GetTypeRefs().FirstOrDefault(t => t.FullName == "System.ValueType");
		var useValue = !hasObject && valueRef != null;
		var useInterface = !hasObject && valueRef == null;
		if (!UseObjectRepresentation(module)) name = exactName ?? NextMarkerName(module);
		if (module.GetTypes().Any(t => string.Equals(t.FullName, Namespace + "." + name, StringComparison.Ordinal)))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var tombstone = new TypeDefUser(Namespace, name, useInterface ? null : useValue ? valueRef : module.CorLibTypes.Object.TypeDefOrRef) {
			Attributes = useInterface ? InterfaceAttributes : useValue ? ValueAttributes : TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
		};
		module.Types.Add(tombstone);
		return tombstone;
	}

	internal static string NextMarkerName(ModuleDef module) {
		var stem = LegacyMode ? NamePrefix
			: module.GetTypeRefs().Any(t => t.FullName == "System.ValueType") ? ValueName : InterfaceName;
		for (var suffix = 0; ; suffix++) {
			var name = suffix == 0 ? stem : stem + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (!module.GetTypes().Any(t => string.Equals(t.FullName, Namespace + "." + name, StringComparison.Ordinal))) return name;
		}
	}

	internal static string NextGlobalTypeName(ModuleDef module, TypeDef row) {
		var stem = GlobalTypePrefix + row.MDToken.Rid.ToString("x6");
		for (var suffix = 0; ; suffix++) {
			var name = suffix == 0 ? stem : stem + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (!module.GetTypes().Any(t => !ReferenceEquals(t, row) && string.Equals(t.Name.String, name, StringComparison.Ordinal))) return name;
		}
	}

	// Re-owns a detached ParamDef row to a fresh host method named after the row.
	// Zero-RID (session-created) rows share the "d000000" prefix; disambiguation
	// is deterministic per module state.
	internal static MethodDef AcquireParamHost(ModuleDef module, ParamDef param) {
		var useInterface = !LegacyMode && !module.GetTypeRefs().Any(t => t.FullName == "System.ValueType");
		var tombstone = GetOrCreate(module);
		var suffix = param.MDToken.Rid.ToString("x6");
		var name = "d" + suffix;
		for (var attempt = 2; tombstone.Methods.Any(m => m.Name == name); attempt++)
			name = "d" + suffix + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var hostSig = UseObjectRepresentation(module) ? MethodSig.CreateInstance(module.CorLibTypes.Void)
			: MethodSig.CreateStatic(module.CorLibTypes.Void, Enumerable.Repeat(module.CorLibTypes.Int32, (int)param.Sequence).ToArray());
		var host = new MethodDefUser(name, hostSig,
			MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
		tombstone.Methods.Add(host);
		host.ParamDefs.Add(param);
		return host;
	}

	// Drops an emptied host method, and the tombstone itself once it holds no
	// rows at all. Callers must only invoke this after moving the last ParamDef out.
	internal static void ReleaseParamHost(MethodDef host) {
		if (host.ParamDefs.Count != 0) return;
		var tombstone = host.DeclaringType;
		if (tombstone == null) return;
		tombstone.Methods.Remove(host);
		RemoveIfEmpty(tombstone);
	}

	// P03-CHANGE-002 v3 §2.6 registered extension: field/event/property/method
	// middle-row deletions re-own their row to the tombstone the same way, so
	// committed deleted-state images never trigger the writer's dummy_ptr reuse.
	internal static void AcquireRow(ModuleDef module, IMDTokenProvider row, TypeDef tombstone) {
		switch (row) {
		case FieldDef field: tombstone.Fields.Add(field); break;
		case EventDef evt:
			// EventDefMD resolves accessors lazily and only from a TypeDefMD
			// owner; force resolution while the original owner is still set so
			// the move cannot cache null accessors.
			_ = evt.AddMethod; _ = evt.RemoveMethod; _ = evt.InvokeMethod; _ = evt.OtherMethods.Count;
			tombstone.Events.Add(evt); break;
		case PropertyDef property:
			_ = property.GetMethod; _ = property.SetMethod; _ = property.OtherMethods.Count;
			tombstone.Properties.Add(property); break;
		case MethodDef method: tombstone.Methods.Add(method); break;
		case TypeDef type: tombstone.NestedTypes.Add(type); break;
		default: throw new EditDomainException("EDIT_VALIDATION_FAILED");
		}
	}

	internal static void ReleaseRow(IMDTokenProvider row, TypeDef tombstone) {
		switch (row) {
		case FieldDef field: tombstone.Fields.Remove(field); break;
		case EventDef evt: tombstone.Events.Remove(evt); break;
		case PropertyDef property: tombstone.Properties.Remove(property); break;
		case MethodDef method: tombstone.Methods.Remove(method); break;
		case TypeDef type: tombstone.NestedTypes.Remove(type); break;
		default: throw new EditDomainException("EDIT_VALIDATION_FAILED");
		}
		if (!ReferenceEquals(tombstone, tombstone.Module?.GlobalType)) RemoveIfEmpty(tombstone);
	}

	internal static void RemoveIfEmpty(TypeDef tombstone) {
		if (tombstone.HasMethods || tombstone.HasFields || tombstone.HasEvents || tombstone.HasProperties || tombstone.HasNestedTypes) return;
		var list = tombstone.DeclaringType?.NestedTypes ?? tombstone.Module?.Types;
		if (list != null) {
			var index = list.IndexOf(tombstone);
			if (index >= 0) list.RemoveAt(index);
		}
	}
}
