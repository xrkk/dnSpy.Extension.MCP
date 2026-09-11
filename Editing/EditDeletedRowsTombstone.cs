using System;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P03-CHANGE-002 v3 operation-time tombstone for deleted member rows. A forward
/// member removal re-owns the detached row to a host method inside this marker
/// type instead of leaving a metadata row hole, so the preserving writer never
/// runs its own dummy_ptr reuse (which appends reference-table rows that later
/// states can never shed). Restores and Undos move rows back and remove emptied
/// host methods, and the tombstone itself once it holds nothing; all rows live
/// at table tails, so removals never leave new holes.
/// Valid only while the module already exposes an equivalent System.Object
/// reference row (the tombstone base dedups onto it through the type-walk
/// path); Object-row-less modules are an explicit open boundary registered
/// under PLAN-CHANGE-001.
/// </summary>
internal static class EditDeletedRowsTombstone {
	internal const string Namespace = "dnspy.mcp.edit";
	internal const string NamePrefix = "DeletedMemberRows";

	// Marker only: namespace + name prefix reserved for this bookkeeping type.
	internal static bool IsMarker(TypeDef type) =>
		string.Equals(type.Namespace?.String, Namespace, StringComparison.Ordinal)
		&& (type.Name.String ?? string.Empty).StartsWith(NamePrefix, StringComparison.Ordinal);

	// The tombstone must match the exact created shape. Re-owned rows keep their
	// original names and content (the future inverse restores them verbatim), so
	// members are unconstrained; the reserved namespace + static-class shell +
	// no extras (nesting/generics/attributes/interfaces/layout/security) is the
	// discriminator. A sample type needs the exact reserved namespace AND prefix
	// AND shell to be mistaken for bookkeeping.
	internal static bool IsTombstone(TypeDef type) =>
		IsMarker(type)
		&& type.Attributes == (TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed)
		&& string.Equals(type.BaseType?.FullName, "System.Object", StringComparison.Ordinal)
		// Nested types are re-owned deleted type subtrees (type_remove); their
		// own content is sample data preserved verbatim for the restore.
		&& !type.HasGenericParameters && !type.HasCustomAttributes
		&& !type.HasInterfaces && type.ClassLayout == null && !type.HasDeclSecurities;

	// Host methods hold re-owned ParamDef rows while their signature stays void()
	// with zero parameters (AUD-106: exempted from EditStructuralValidator by the
	// registered rule, not by silently relaxing it).
	internal static bool IsParamHost(MethodDef method) =>
		method.Name.String is { } name && name.StartsWith("d", StringComparison.Ordinal)
		&& !method.HasBody && method.ImplMap == null && method.Overrides.Count == 0
		&& method.GenericParameters.Count == 0 && method.CustomAttributes.Count == 0
		&& method.MethodSig is { } sig && !sig.Generic && sig.Params.Count == 0
		&& sig.RetType.ElementType == ElementType.Void;

	internal static TypeDef GetOrCreate(ModuleDef module) {
		var existing = module.Types.Where(IsMarker).ToArray();
		if (existing.Length > 1) throw new EditDomainException("EDIT_VALIDATION_FAILED");
		if (existing.Length == 1) {
			if (!IsTombstone(existing[0])) throw new EditDomainException("EDIT_VALIDATION_FAILED");
			return existing[0];
		}
		var attempt = 0;
		string name;
		do {
			name = attempt == 0 ? NamePrefix : NamePrefix + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
			attempt++;
		} while (module.GetTypes().Any(t => string.Equals(t.FullName, Namespace + "." + name, StringComparison.Ordinal)));
		var tombstone = new TypeDefUser(Namespace, name, module.CorLibTypes.Object.TypeDefOrRef) {
			Attributes = TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
		};
		module.Types.Add(tombstone);
		return tombstone;
	}

	// Re-owns a detached ParamDef row to a fresh host method named after the row.
	// Zero-RID (session-created) rows share the "d000000" prefix; disambiguation
	// is deterministic per module state.
	internal static MethodDef AcquireParamHost(ModuleDef module, ParamDef param) {
		var tombstone = GetOrCreate(module);
		var suffix = param.MDToken.Rid.ToString("x6");
		var name = "d" + suffix;
		for (var attempt = 2; tombstone.Methods.Any(m => m.Name == name); attempt++)
			name = "d" + suffix + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var host = new MethodDefUser(name, MethodSig.CreateInstance(module.CorLibTypes.Void),
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
		if (tombstone == null || !IsTombstone(tombstone)) return;
		tombstone.Methods.Remove(host);
		RemoveIfEmpty(tombstone);
	}

	// P03-CHANGE-002 v3 §2.6 registered extension: field/event/property/method
	// middle-row deletions re-own their row to the tombstone the same way, so
	// committed deleted-state images never trigger the writer's dummy_ptr reuse.
	internal static void AcquireRow(ModuleDef module, IMDTokenProvider row) {
		var tombstone = GetOrCreate(module);
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
		RemoveIfEmpty(tombstone);
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
