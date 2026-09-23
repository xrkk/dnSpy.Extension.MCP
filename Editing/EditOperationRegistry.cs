using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using TypeAttributes = dnlib.DotNet.TypeAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using PropertyAttributes = dnlib.DotNet.PropertyAttributes;
using EventAttributes = dnlib.DotNet.EventAttributes;
using ParamAttributes = dnlib.DotNet.ParamAttributes;
using GenericParamAttributes = dnlib.DotNet.GenericParamAttributes;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditOperationOutcome {
	public string Kind { get; init; } = string.Empty;
	public IReadOnlyList<string> CreatedObjectIds { get; init; } = Array.Empty<string>();
	public Action Undo { get; init; } = () => { };
	public string Target { get; init; } = string.Empty;
	public string? Before { get; init; }
	public string? After { get; init; }
	public IReadOnlyList<Dictionary<string, object?>> Risks { get; init; } = Array.Empty<Dictionary<string, object?>>();
}

internal static partial class EditOperationRegistry {
	static readonly Dictionary<string, OpCode> OpCodesByName = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
		.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
	static readonly Dictionary<string, uint> AttributeMasks = new Dictionary<string, uint>(StringComparer.Ordinal) {
		["type"] = 16219583, ["method"] = 65535, ["method_impl"] = 6143, ["field"] = 47095,
		["property"] = 5632, ["event"] = 1536, ["parameter"] = 12319, ["generic"] = 63, ["resource"] = 3,
	};

	public static EditOperationOutcome Apply(ModuleDef module, JsonElement operation,
		Dictionary<string, IMDTokenProvider> objects, int operationIndex) {
		if (operation.ValueKind != JsonValueKind.Object) Invalid("operation", "Operation must be an object");
		RejectUnknownRawFields(operation);
		var kind = RequiredString(operation, "kind");
		if (!EditWire.OperationKinds.Contains(kind, StringComparer.Ordinal)) Invalid("operation.kind", "Unknown operation kind");
		var pdbBefore = module.PdbState;
		var outcome = kind switch {
			"type_add" => TypeAdd(module, operation, objects, operationIndex),
			"type_update" => TypeUpdate(module, operation, objects),
			"type_remove" => TypeRemove(module, operation, objects),
			"method_add" => MethodAdd(module, operation, objects, operationIndex),
			"method_update" => MethodUpdate(module, operation, objects),
			"method_remove" => MethodRemove(module, operation, objects),
			"field_add" => FieldAdd(module, operation, objects, operationIndex),
			"field_update" => FieldUpdate(module, operation, objects),
			"field_remove" => FieldRemove(module, operation, objects),
			"property_add" => PropertyAdd(module, operation, objects, operationIndex),
			"property_update" => PropertyUpdate(module, operation, objects),
			"property_remove" => PropertyRemove(module, operation, objects),
			"event_add" => EventAdd(module, operation, objects, operationIndex),
			"event_update" => EventUpdate(module, operation, objects),
			"event_remove" => EventRemove(module, operation, objects),
			"parameter_add" => ParameterAdd(module, operation, objects, operationIndex),
			"parameter_update" => ParameterUpdate(module, operation, objects),
			"parameter_remove" => ParameterRemove(module, operation, objects),
			"generic_parameter_add" => GenericAdd(module, operation, objects, operationIndex),
			"generic_parameter_update" => GenericUpdate(module, operation, objects),
			"generic_parameter_remove" => GenericRemove(module, operation, objects),
			"method_body_replace" => BodyReplace(module, operation, objects),
			"attribute_add" => AttributeAdd(module, operation, objects),
			"attribute_remove" => AttributeRemove(module, operation, objects),
			"security_add" => SecurityAdd(module, operation, objects),
			"security_remove" => SecurityRemove(module, operation, objects),
			"assembly_update" => AssemblyUpdate(module, operation),
			"module_update" => ModuleUpdate(module, operation),
			"assembly_ref_update" => AssemblyRefUpdate(module, operation, objects),
			"entry_point_set" => EntryPointSet(module, operation, objects),
			"managed_resource_add" => ManagedResourceAdd(module, operation),
			"managed_resource_update" => ManagedResourceUpdate(module, operation),
			"managed_resource_remove" => ManagedResourceRemove(module, operation),
			"win32_resource_add" => Win32ResourceAdd(module, operation),
			"win32_resource_update" => Win32ResourceUpdate(module, operation),
			"win32_resource_remove" => Win32ResourceRemove(module, operation),
			"strong_name_remove" => StrongNameRemove(module, operation),
			"interface_add" => InterfaceAdd(module, operation, objects, operationIndex),
			"reference_add" => ReferenceAdd(module, operation, objects, operationIndex),
			_ => throw new EditDomainException("EDIT_VALIDATION_FAILED"),
		};
		if (pdbBefore != null || module.PdbState == null) return outcome;
		return new EditOperationOutcome {
			Kind = outcome.Kind, CreatedObjectIds = outcome.CreatedObjectIds, Target = outcome.Target,
			Before = outcome.Before, After = outcome.After, Risks = outcome.Risks,
			Undo = () => { outcome.Undo(); RemoveEmptyPdbState(module); },
		};
	}

	// A newly allocated, now empty container is different from an originally
	// present empty PDB: only callers holding evidence of prior absence use this.
	internal static void RemoveEmptyPdbState(ModuleDef module) {
		var state = module.PdbState;
		if (state == null) return;
		// Method bodies/CDI belong to their definitions, not to this container;
		// earlier prefix operations may still own them during reverse traversal.
		if (state.HasDocuments || state.UserEntryPoint != null)
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		// dnlib 4.5.0 exposes a getter and a one-time non-null setter only.
		// Detach this proven-empty container without disposing the module or
		// the container: navigation compensation may reattach the same object.
		var field = typeof(ModuleDef).GetField("pdbState", BindingFlags.Instance | BindingFlags.NonPublic);
		if (field == null || field.FieldType != typeof(PdbState) || !field.IsFamily)
			throw new NotSupportedException("The pinned dnlib PDB-state layout is unavailable");
		field.SetValue(module, null);
	}

	/// <summary>Replay a package-owned operation. The legacy composite is deliberately
	/// unavailable through <see cref="Apply"/>, which remains the public edit_apply gate.</summary>
	public static EditOperationOutcome ApplyPersisted(ModuleDef module, JsonElement operation,
		Dictionary<string, IMDTokenProvider> objects, int operationIndex) {
		if (operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("kind", out var kind)
			&& kind.ValueKind == JsonValueKind.String && kind.GetString() == EditOperationVersions.LegacySymbolRename)
			return EditLegacyRenameOperation.Apply(module, operation);
		return Apply(module, operation, objects, operationIndex);
	}

	static EditOperationOutcome TypeAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var name = RequiredString(op, "name"); var ns = OptionalString(op, "namespace") ?? string.Empty;
		// T004: interface rows are exclusively interface_add's domain — an
		// `interfaces` field here must fail loudly, never silently drop rows.
		if (op.TryGetProperty("interfaces", out _)) Invalid("interfaces", "type_add does not carry interface rows; add them with interface_add");
		var attrs = (TypeAttributes)OptionalAttributes(op, "attributes", "type", 0);
		var parser = new EditTypeSigParser(module);
		ITypeDefOrRef? baseType = null;
		if (op.TryGetProperty("base_type", out var b) && b.ValueKind != JsonValueKind.Null) baseType = ResolveTypeEntry(b, module, map).ToTypeDefOrRef();
		var type = new TypeDefUser(ns, name, baseType) { Attributes = attrs };
		var id = ObjectId(index, 0); var owner = OptionalRef<TypeDef>(module, op, "owner_type", map);
		if (owner == null) module.Types.Add(type); else owner.NestedTypes.Add(type);
		map[id] = type;
		return Outcome("type_add", id, type, null, type.FullName, () => { if (owner == null) module.Types.Remove(type); else owner.NestedTypes.Remove(type); map.Remove(id); });
	}

	static EditOperationOutcome TypeUpdate(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var t = Ref<TypeDef>(module, op.GetProperty("target"), map); var before = t.FullName;
		var oldName = t.Name; var oldNs = t.Namespace; var oldAttrs = t.Attributes; var oldBase = t.BaseType; var oldLayout = t.ClassLayout;
		if (op.TryGetProperty("name", out var n)) t.Name = NonEmpty(n, "name");
		if (op.TryGetProperty("namespace", out var ns)) t.Namespace = ns.GetString() ?? string.Empty;
		if (op.TryGetProperty("attributes", out var a)) {
			var updated = (TypeAttributes)Attributes(a, "type");
			// P04 bit ownership: the two layout bits belong to the structured
			// layout field exclusively (ECMA-335 II 23.1.3 via ClassLayout).
			if ((((ulong)updated ^ (ulong)t.Attributes) & 0x18ul) != 0) Invalid("attributes", "layout bits are owned by the layout field");
			t.Attributes = updated;
		}
		if (op.TryGetProperty("base_type", out var b)) t.BaseType = b.ValueKind == JsonValueKind.Null ? null : ResolveTypeEntry(b, module, map, t.GenericParameters.Count).ToTypeDefOrRef();
		Dictionary<string, object?>? layoutRisk = null;
		if (op.TryGetProperty("layout", out var lay)) { SetLayout(t, lay); layoutRisk = Risk("layout_change", t); }
		var risks = layoutRisk == null ? Array.Empty<Dictionary<string, object?>>() : new[] { layoutRisk };
		return Outcome("type_update", null, t, before, t.FullName, () => { t.Name = oldName; t.Namespace = oldNs; t.Attributes = oldAttrs; t.BaseType = oldBase; t.ClassLayout = oldLayout; }, risks);
	}

	// P04 IMP-004: whole-row layout semantics. auto clears the row and both
	// bits; sequential/explicit own exactly one bit and materialize the row.
	static void SetLayout(TypeDef t, JsonElement lay) {
		var kind = RequiredString(lay, "kind");
		ushort pack = 0; uint size = 0;
		if (lay.TryGetProperty("pack", out var p)) {
			pack = (ushort)p.GetUInt32();
			if (Array.IndexOf(LegalPacks, pack) < 0) Invalid("layout.pack", "pack must be one of 0,1,2,4,8,16,32,64,128 (ECMA-335 II 23.11)");
		}
		if (lay.TryGetProperty("size", out var s)) size = s.GetUInt32();
		var layoutBits = TypeAttributes.SequentialLayout | TypeAttributes.ExplicitLayout;
		switch (kind) {
			case "auto": t.ClassLayout = null; t.Attributes &= ~layoutBits; return;
			case "sequential":
			case "explicit":
				if (t.ClassLayout == null) t.ClassLayout = new ClassLayoutUser(pack, size);
				else { t.ClassLayout.PackingSize = pack; t.ClassLayout.ClassSize = size; }
				t.Attributes = kind == "sequential"
					? (t.Attributes & ~layoutBits) | TypeAttributes.SequentialLayout
					: (t.Attributes & ~layoutBits) | TypeAttributes.ExplicitLayout;
				return;
			default: Invalid("layout.kind", "layout.kind must be auto, sequential or explicit"); return;
		}
	}
	static readonly ushort[] LegalPacks = { 0, 1, 2, 4, 8, 16, 32, 64, 128 };

	static EditOperationOutcome TypeRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		RequireRemoveMode(op); var t = Ref<TypeDef>(module, op.GetProperty("target"), map);
		if (HasReference(module, t) || HasAttachment(module,t)) Invalid("operation.target", "Type is referenced or attached");
		var owner = t.DeclaringType; var collection = owner == null ? module.Types : owner.NestedTypes; var index = collection.IndexOf(t); var before = t.FullName;
		collection.Remove(t); RemoveMapValue(map, t);
		// P03-CHANGE-002 v3 §2.6: the whole subtree travels into the tombstone so
		// its TypeDef row (and every member row) survives the deleted-state image.
		var tombstoneRow = t.MDToken.Rid != 0; dnlib.DotNet.TypeDef? tombstone = null;
		if (tombstoneRow) { tombstone = EditDeletedRowsTombstone.GetOrCreate(module); EditDeletedRowsTombstone.AcquireRow(module, t); }
		var risks = IsPublic(t.Attributes) ? new[] { Risk("public_delete", t) } : Array.Empty<Dictionary<string, object?>>();
		return Outcome("type_remove", null, t, before, null, () => {
			if (tombstoneRow) EditDeletedRowsTombstone.ReleaseRow(t, tombstone!);
			collection.Insert(index, t);
		}, risks);
	}

	static EditOperationOutcome MethodAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var owner = Ref<TypeDef>(module, op.GetProperty("owner_type"), map); var signature = op.GetProperty("signature");
		var gps = signature.GetProperty("generic_parameters").EnumerateArray().ToList();
		var ownerArity = owner.GenericParameters.Count;
		var ret = ResolveTypeEntry(signature.GetProperty("return_type"), module, map, ownerArity, gps.Count, allowVoid: true);
		var parameters = signature.GetProperty("parameters").EnumerateArray().ToList();
		var paramTypes = parameters.Select(p => ResolveTypeEntry(p.GetProperty("type"), module, map, ownerArity, gps.Count)).ToArray();
		var sig = MethodSig.CreateStatic(ret, paramTypes); if (RequiredBool(signature, "has_this")) sig.HasThis = true;
		sig.GenParamCount = (uint)gps.Count;
		if (gps.Count != 0) sig.CallingConvention |= CallingConvention.Generic;
		var method = new MethodDefUser(RequiredString(op, "name"), sig,
			(MethodImplAttributes)OptionalAttributes(op, "impl_attributes", "method_impl", 0),
			(MethodAttributes)OptionalAttributes(op, "attributes", "method", 128));
		for (int i = 0; i < gps.Count; i++) {
			var gp = new GenericParamUser((ushort)i, (GenericParamAttributes)OptionalAttributes(gps[i], "attributes", "generic", 0), RequiredString(gps[i], "name"));
			if (gps[i].TryGetProperty("constraints", out var constraints) && constraints.ValueKind == JsonValueKind.Array)
				SetGenericConstraints(module, gp, constraints, map);
			method.GenericParameters.Add(gp);
		}
		for (int i = 0; i < parameters.Count; i++) {
			var p = parameters[i];
			if (p.TryGetProperty("name", out var pn) && pn.ValueKind != JsonValueKind.Null)
				method.ParamDefs.Add(new ParamDefUser(pn.GetString(), (ushort)(i + 1), (ParamAttributes)OptionalAttributes(p, "attributes", "parameter", 0)));
		}
		PdbDocument[] documentsBefore = CapturePdbDocuments(module);
		if (op.TryGetProperty("body", out var body)) method.Body = BuildBody(module, method, body, map);
		var addedDocuments = module.PdbState == null ? Array.Empty<PdbDocument>() : module.PdbState.Documents.Where(d => !documentsBefore.Contains(d)).ToArray();
		ApplyMethodDebugInfo(module, op, method, map);
		if (op.TryGetProperty("overrides", out var addOverrides)) ApplyOverrides(module, method, addOverrides, map);
		Dictionary<string, object?>? pinvokeAddRisk = null;
		if (op.TryGetProperty("pinvoke", out var addPInvoke)) { ApplyPInvoke(module, method, addPInvoke); pinvokeAddRisk = Risk("external_code_entry", method); }
		owner.Methods.Add(method); var id = ObjectId(index, 0); map[id] = method;
		var methodAddRisks = pinvokeAddRisk == null ? Array.Empty<Dictionary<string, object?>>() : new[] { pinvokeAddRisk! };
		return Outcome("method_add", id, method, null, method.FullName, () => { owner.Methods.Remove(method); ReleasePdbDocuments(module, addedDocuments); map.Remove(id); }, methodAddRisks);
	}

	static EditOperationOutcome MethodUpdate(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var m = Ref<MethodDef>(module, op.GetProperty("target"), map); var before = m.FullName;
		var oldName=m.Name; var oldAttrs=m.Attributes; var oldImpl=m.ImplAttributes; var oldRet=m.ReturnType; var oldHas=m.MethodSig.HasThis;
		if (op.TryGetProperty("name",out var n)) m.Name=NonEmpty(n,"name");
		if (op.TryGetProperty("attributes",out var a)) m.Attributes=(MethodAttributes)Attributes(a,"method");
		if (op.TryGetProperty("impl_attributes",out var ia)) m.ImplAttributes=(MethodImplAttributes)Attributes(ia,"method_impl");
		if (op.TryGetProperty("return_type",out var r)) m.MethodSig.RetType=ResolveTypeEntry(r,module,map,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count,allowVoid:true);
		if (op.TryGetProperty("has_this",out var h)) m.MethodSig.HasThis=h.GetBoolean();
		var risks = new List<Dictionary<string, object?>>(); if (m.ReturnType != oldRet || m.MethodSig.HasThis != oldHas) risks.Add(Risk("signature_change",m)); if (m.Attributes != oldAttrs) risks.Add(Risk("visibility_change",m));
		var oldOverrides=m.Overrides.ToArray();var overrideRisk=false;
		if(op.TryGetProperty("overrides",out var ov)){ApplyOverrides(module,m,ov,map);overrideRisk=true;}
		var oldImplMap=m.ImplMap;var oldPInvokeBit=m.IsPinvokeImpl;
		var pinvokeRisk=false;
		if(op.TryGetProperty("pinvoke",out var pi)){ApplyPInvoke(module,m,pi);pinvokeRisk=true;}
		if((overrideRisk||pinvokeRisk)&&!risks.Any(r=>Equals(r["risk_id"],Risk("signature_change",m)["risk_id"])))risks.Add(Risk("signature_change",m));
		if(pinvokeRisk)risks.Add(Risk("external_code_entry",m));
		return Outcome("method_update",null,m,before,m.FullName,()=>{m.Name=oldName;m.Attributes=oldAttrs;m.ImplAttributes=oldImpl;m.MethodSig.RetType=oldRet;m.MethodSig.HasThis=oldHas;m.Overrides.Clear();foreach(var o in oldOverrides)m.Overrides.Add(o);m.ImplMap=oldImplMap;if(oldPInvokeBit)m.IsPinvokeImpl=true;else m.IsPinvokeImpl=false;},risks);
	}

	static EditOperationOutcome MethodRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		RequireRemoveMode(op); var m=Ref<MethodDef>(module,op.GetProperty("target"),map); if(HasReference(module,m)||HasAttachment(module,m)) Invalid("operation.target","Method is referenced or attached");
		var owner=m.DeclaringType; var index=owner.Methods.IndexOf(m); var before=m.FullName; owner.Methods.Remove(m); RemoveMapValue(map,m);
		// P03-CHANGE-002 v3 §2.6: real-row removals re-own to the tombstone so the
		// writer never creates dummy_ptr placeholders (and their reference rows).
		var tombstoneRow=m.MDToken.Rid!=0; dnlib.DotNet.TypeDef? tombstone=null;
		if(tombstoneRow){tombstone=EditDeletedRowsTombstone.GetOrCreate(module);EditDeletedRowsTombstone.AcquireRow(module,m);}
		var risks=IsPublic(m.Attributes)?new[]{Risk("public_delete",m)}:Array.Empty<Dictionary<string,object?>>();
		return Outcome("method_remove",null,m,before,null,()=>{if(tombstoneRow)EditDeletedRowsTombstone.ReleaseRow(m,tombstone!);owner.Methods.Insert(index,m);},risks);
	}

	static EditOperationOutcome FieldAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map); var type=ResolveTypeEntry(op.GetProperty("field_type"),module,map,owner.GenericParameters.Count);
		var f=new FieldDefUser(RequiredString(op,"name"),new FieldSig(type),(FieldAttributes)OptionalAttributes(op,"attributes","field",0));
		if(op.TryGetProperty("constant",out var c)) f.Constant=ParseConstant(c,type);
		Dictionary<string,object?>? dataRisk=null;
		if(op.TryGetProperty("initial_data",out var init)){ApplyInitialData(f,init);dataRisk=Risk("data_section_change",f);}
		if(op.TryGetProperty("marshal",out var addMarshal))f.MarshalType=EditMarshalCodec.Parse(module,addMarshal,new EditTypeSigParser(module,owner.GenericParameters.Count));
		owner.Fields.Add(f); var id=ObjectId(index,0); map[id]=f;
		var addRisks=dataRisk==null?(f.MarshalType!=null?new[]{Risk("signature_change",f)}:Array.Empty<Dictionary<string,object?>>()):new[]{dataRisk!,Risk("signature_change",f)};
		return Outcome("field_add",id,f,null,f.FullName,()=>{owner.Fields.Remove(f);map.Remove(id);},addRisks);
	}

	// P04 IMP-008: static-field RVA initial data. Literal fields carry a
	// metadata constant instead; instance fields have no data slot.
	static void ApplyInitialData(FieldDef f, JsonElement v) {
		if (!f.IsStatic) Invalid("initial_data", "initial_data requires a static field");
		if (f.IsLiteral) Invalid("initial_data", "literal fields carry a constant, not initial data");
		if (v.ValueKind == JsonValueKind.Null) { f.InitialValue = null; f.HasFieldRVA = false; return; }
		var bytes = v.GetProperty("bytes_base64").GetBytesFromBase64();
		if (bytes.Length > EditWire.MaxInitialDataBytes) Invalid("initial_data", "initial_data exceeds MaxInitialDataBytes");
		// ECMA-335 FieldRVA rows carry no length: readers (dnlib included) return
		// exactly the field type's size, so byte-exact round trips require the
		// payload to equal that size; arbitrary-length blobs need a same-size
		// value type and stay outside the primitive domain declared here.
		var size = PrimitiveSize(f.FieldType);
		if (size < 0 || bytes.Length != size)
			Invalid("initial_data", "initial_data length must equal the field type size (primitive types only)");
		// dnlib 4.5.0 documented contract (FieldDef.InitialValue remarks): the
		// module writer only emits the FieldRVA row when HasFieldRVA is set.
		f.InitialValue = bytes; f.HasFieldRVA = true;
	}
	static int PrimitiveSize(TypeSig t) => t.ElementType switch {
		ElementType.Boolean or ElementType.U1 or ElementType.I1 => 1,
		ElementType.Char or ElementType.U2 or ElementType.I2 => 2,
		ElementType.I4 or ElementType.U4 or ElementType.R4 => 4,
		ElementType.I8 or ElementType.U8 or ElementType.R8 => 8,
		_ => -1,
	};

	static EditOperationOutcome FieldUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){
		var f=Ref<FieldDef>(module,op.GetProperty("target"),map);var before=f.FullName;var oldName=f.Name;var oldType=f.FieldType;var oldAttrs=f.Attributes;var oldConst=f.Constant;var oldOffset=f.FieldOffset;var oldInitial=f.InitialValue;
		if(op.TryGetProperty("name",out var n))f.Name=NonEmpty(n,"name"); if(op.TryGetProperty("field_type",out var t))f.FieldSig.Type=ResolveTypeEntry(t,module,map,f.DeclaringType.GenericParameters.Count);
		if(op.TryGetProperty("attributes",out var a))f.Attributes=(FieldAttributes)Attributes(a,"field"); if(op.TryGetProperty("clear_constant",out var clear)&&clear.GetBoolean())f.Constant=null; else if(op.TryGetProperty("constant",out var c))f.Constant=ParseConstant(c,f.FieldType);
		if(op.TryGetProperty("field_offset",out var fo)){
			var owner=f.DeclaringType;
			if(owner==null||(owner.Attributes&TypeAttributes.ExplicitLayout)==0)Invalid("field_offset","field_offset requires an explicit-layout owner type");
			f.FieldOffset=fo.ValueKind==JsonValueKind.Null?(uint?)null:fo.GetUInt32();
		}
		if(op.TryGetProperty("initial_data",out var init))ApplyInitialData(f,init);
		var oldMarshal=f.MarshalType;if(op.TryGetProperty("marshal",out var ma))f.MarshalType=EditMarshalCodec.Parse(module,ma,new EditTypeSigParser(module,f.DeclaringType.GenericParameters.Count));
		var risks=new List<Dictionary<string,object?>>();if(f.FieldType!=oldType||f.MarshalType!=oldMarshal)risks.Add(Risk("signature_change",f));if(f.Attributes!=oldAttrs)risks.Add(Risk("visibility_change",f));
		if(f.InitialValue!=oldInitial)risks.Add(Risk("data_section_change",f));if(f.FieldOffset!=oldOffset)risks.Add(Risk("layout_change",f));
		return Outcome("field_update",null,f,before,f.FullName,()=>{f.Name=oldName;f.FieldSig.Type=oldType;f.Attributes=oldAttrs;f.Constant=oldConst;f.FieldOffset=oldOffset;f.InitialValue=oldInitial;f.MarshalType=oldMarshal;},risks);
	}

	static EditOperationOutcome FieldRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var f=Ref<FieldDef>(module,op.GetProperty("target"),map);if(HasReference(module,f)||HasAttachment(module,f))Invalid("operation.target","Field is referenced or attached");var owner=f.DeclaringType;var i=owner.Fields.IndexOf(f);var before=f.FullName;owner.Fields.Remove(f);RemoveMapValue(map,f);var tombstoneRow=f.MDToken.Rid!=0;dnlib.DotNet.TypeDef? tombstone=null;if(tombstoneRow){tombstone=EditDeletedRowsTombstone.GetOrCreate(module);EditDeletedRowsTombstone.AcquireRow(module,f);}var risks=IsPublic(f.Attributes)?new[]{Risk("public_delete",f)}:Array.Empty<Dictionary<string,object?>>();return Outcome("field_remove",null,f,before,null,()=>{if(tombstoneRow)EditDeletedRowsTombstone.ReleaseRow(f,tombstone!);owner.Fields.Insert(i,f);},risks);}

	static EditOperationOutcome PropertyAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){
		var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map);var ret=ResolveTypeEntry(op.GetProperty("property_type"),module,map,owner.GenericParameters.Count);var indices=op.TryGetProperty("index_parameter_types",out var arr)?arr.EnumerateArray().Select(x=>ResolveTypeEntry(x,module,map,owner.GenericParameters.Count)).ToArray():Array.Empty<TypeSig>();
		var p=new PropertyDefUser(RequiredString(op,"name"),new PropertySig(true,ret,indices),(PropertyAttributes)OptionalAttributes(op,"attributes","property",0));
		if(op.TryGetProperty("getter",out var g)){if(g.ValueKind==JsonValueKind.Null)Invalid("getter","Explicit null is invalid for property_add");p.GetMethod=Ref<MethodDef>(module,g,map);}if(op.TryGetProperty("setter",out var s)){if(s.ValueKind==JsonValueKind.Null)Invalid("setter","Explicit null is invalid for property_add");p.SetMethod=Ref<MethodDef>(module,s,map);}
		owner.Properties.Add(p);var id=ObjectId(index,0);map[id]=p;return Outcome("property_add",id,p,null,p.FullName,()=>{owner.Properties.Remove(p);map.Remove(id);});}
	static EditOperationOutcome PropertyUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var p=Ref<PropertyDef>(module,op.GetProperty("target"),map);var before=p.FullName;var oldName=p.Name;var oldSig=p.PropertySig;var oldAttrs=p.Attributes;var oldGet=p.GetMethod;var oldSet=p.SetMethod;if(op.TryGetProperty("name",out var n))p.Name=NonEmpty(n,"name");var ret=op.TryGetProperty("property_type",out var pt)?ResolveTypeEntry(pt,module,map,p.DeclaringType.GenericParameters.Count):p.PropertySig.RetType;var args=op.TryGetProperty("index_parameter_types",out var a)?a.EnumerateArray().Select(x=>ResolveTypeEntry(x,module,map,p.DeclaringType.GenericParameters.Count)).ToArray():p.PropertySig.Params.ToArray();if(op.TryGetProperty("property_type",out _)||op.TryGetProperty("index_parameter_types",out _))p.PropertySig=new PropertySig(p.PropertySig.HasThis,ret,args);if(op.TryGetProperty("attributes",out var at))p.Attributes=(PropertyAttributes)Attributes(at,"property");if(op.TryGetProperty("getter",out var g))p.GetMethod=g.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,g,map);if(op.TryGetProperty("setter",out var s))p.SetMethod=s.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,s,map);var risks=new List<Dictionary<string,object?>>();if(p.PropertySig!=oldSig||p.GetMethod!=oldGet||p.SetMethod!=oldSet)risks.Add(Risk("signature_change",p));return Outcome("property_update",null,p,before,p.FullName,()=>{p.Name=oldName;p.PropertySig=oldSig;p.Attributes=oldAttrs;p.GetMethod=oldGet;p.SetMethod=oldSet;},risks);}
	static EditOperationOutcome PropertyRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var p=Ref<PropertyDef>(module,op.GetProperty("target"),map);if(HasAttachment(module,p))Invalid("operation.target","Property is referenced or attached");var owner=p.DeclaringType;var i=owner.Properties.IndexOf(p);var before=p.FullName;_=p.GetMethod;_=p.SetMethod;_=p.OtherMethods.Count;var slots=AccessorSlots(PropertyAccessors(p),owner);owner.Properties.Remove(p);RemoveMapValue(map,p);var tombstoneRow=p.MDToken.Rid!=0;dnlib.DotNet.TypeDef? tombstone=null;if(tombstoneRow){tombstone=EditDeletedRowsTombstone.GetOrCreate(module);EditDeletedRowsTombstone.AcquireRow(module,p);DetachAccessors(owner,tombstone,slots);}return Outcome("property_remove",null,p,before,null,()=>{if(tombstoneRow){ReattachAccessors(owner,tombstone!,slots);EditDeletedRowsTombstone.ReleaseRow(p,tombstone!);}owner.Properties.Insert(i,p);},new[]{Risk("public_delete",p)});}

	static EditOperationOutcome EventAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map);var et=ResolveTypeEntry(op.GetProperty("event_type"),module,map,owner.GenericParameters.Count).ToTypeDefOrRef()!;var e=new EventDefUser(RequiredString(op,"name"),et,(EventAttributes)OptionalAttributes(op,"attributes","event",0)){AddMethod=Ref<MethodDef>(module,op.GetProperty("add_method"),map),RemoveMethod=Ref<MethodDef>(module,op.GetProperty("remove_method"),map)};if(op.TryGetProperty("raise_method",out var r)){if(r.ValueKind==JsonValueKind.Null)Invalid("raise_method","Explicit null is invalid for event_add");e.InvokeMethod=Ref<MethodDef>(module,r,map);}owner.Events.Add(e);var id=ObjectId(index,0);map[id]=e;return Outcome("event_add",id,e,null,e.FullName,()=>{owner.Events.Remove(e);map.Remove(id);});}
	static EditOperationOutcome EventUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var e=Ref<EventDef>(module,op.GetProperty("target"),map);var before=e.FullName;var oldName=e.Name;var oldType=e.EventType;var oldAttrs=e.Attributes;var oldAdd=e.AddMethod;var oldRemove=e.RemoveMethod;var oldRaise=e.InvokeMethod;if(op.TryGetProperty("name",out var n))e.Name=NonEmpty(n,"name");if(op.TryGetProperty("event_type",out var t))e.EventType=ResolveTypeEntry(t,module,map,e.DeclaringType.GenericParameters.Count).ToTypeDefOrRef()!;if(op.TryGetProperty("attributes",out var a))e.Attributes=(EventAttributes)Attributes(a,"event");if(op.TryGetProperty("add_method",out var add))e.AddMethod=add.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,add,map);if(op.TryGetProperty("remove_method",out var rem))e.RemoveMethod=rem.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,rem,map);if(op.TryGetProperty("raise_method",out var raise))e.InvokeMethod=raise.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,raise,map);return Outcome("event_update",null,e,before,e.FullName,()=>{e.Name=oldName;e.EventType=oldType;e.Attributes=oldAttrs;e.AddMethod=oldAdd;e.RemoveMethod=oldRemove;e.InvokeMethod=oldRaise;},new[]{Risk("signature_change",e)});}
	static EditOperationOutcome EventRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var e=Ref<EventDef>(module,op.GetProperty("target"),map);if(HasAttachment(module,e))Invalid("operation.target","Event is referenced or attached");var owner=e.DeclaringType;var i=owner.Events.IndexOf(e);var before=e.FullName;_=e.AddMethod;_=e.RemoveMethod;_=e.InvokeMethod;_=e.OtherMethods.Count;
// dnlib resolves event/property accessors only within the owning type, so the
// accessor methods must travel with the row into the tombstone or the deleted
// image loses its MethodSemantics rows and reload caches null accessors.
var slots=AccessorSlots(EventAccessors(e),owner);owner.Events.Remove(e);RemoveMapValue(map,e);var tombstoneRow=e.MDToken.Rid!=0;dnlib.DotNet.TypeDef? tombstone=null;if(tombstoneRow){tombstone=EditDeletedRowsTombstone.GetOrCreate(module);EditDeletedRowsTombstone.AcquireRow(module,e);DetachAccessors(owner,tombstone,slots);}return Outcome("event_remove",null,e,before,null,()=>{if(tombstoneRow){ReattachAccessors(owner,tombstone!,slots);EditDeletedRowsTombstone.ReleaseRow(e,tombstone!);}owner.Events.Insert(i,e);},new[]{Risk("public_delete",e)});}
	static (string slot, dnlib.DotNet.MethodDef? method)[] EventAccessors(EventDef e)=>new (string,dnlib.DotNet.MethodDef?)[]{("add",e.AddMethod),("remove",e.RemoveMethod),("raise",e.InvokeMethod)}.Concat(e.OtherMethods.Select((m,n)=>("other"+n,(dnlib.DotNet.MethodDef?)m))).ToArray();
	static (string slot, dnlib.DotNet.MethodDef? method)[] PropertyAccessors(PropertyDef p)=>new (string,dnlib.DotNet.MethodDef?)[]{("get",p.GetMethod),("set",p.SetMethod)}.Concat(p.OtherMethods.Select((m,n)=>("other"+n,(dnlib.DotNet.MethodDef?)m))).ToArray();
	static (dnlib.DotNet.MethodDef? method,int index)[] AccessorSlots((string slot,dnlib.DotNet.MethodDef? method)[] accessors,TypeDef owner)=>accessors.Select(a=>(a.method,a.method==null?-1:owner.Methods.IndexOf(a.method))).ToArray();
	static void DetachAccessors(TypeDef owner,TypeDef tombstone,(dnlib.DotNet.MethodDef? method,int index)[] slots){foreach(var (method,_) in slots)if(method!=null){owner.Methods.Remove(method);tombstone.Methods.Add(method);}}
	static void ReattachAccessors(TypeDef owner,TypeDef tombstone,(dnlib.DotNet.MethodDef? method,int index)[] slots){foreach(var (method,index) in slots.OrderBy(s=>s.index))if(method!=null){tombstone.Methods.Remove(method);owner.Methods.Insert(index,method);}}


	static EditOperationOutcome ParameterAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var m=Ref<MethodDef>(module,op.GetProperty("owner_method"),map);var requested=(int)RequiredUInt(op,"parameter_index");if(requested!=m.MethodSig.Params.Count)Invalid("parameter_index","Only tail parameter insertion is supported");var type=ResolveTypeEntry(op.GetProperty("parameter_type"),module,map,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count);m.MethodSig.Params.Add(type);var p=new ParamDefUser(RequiredString(op,"name"),(ushort)(requested+1),(ParamAttributes)OptionalAttributes(op,"attributes","parameter",0));if(op.TryGetProperty("marshal",out var addMarshal))p.MarshalType=EditMarshalCodec.Parse(module,addMarshal,new EditTypeSigParser(module,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count));m.ParamDefs.Add(p);var id=ObjectId(index,0);map[id]=p;return Outcome("parameter_add",id,p,null,p.Name,()=>{m.ParamDefs.Remove(p);m.MethodSig.Params.RemoveAt(m.MethodSig.Params.Count-1);map.Remove(id);},new[]{Risk("signature_change",m)});}
	static EditOperationOutcome ParameterUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var target=ResolveParameter(module,op.GetProperty("parameter_target"),map);var m=target.method;var p=target.param;var index=target.index;var oldName=p?.Name;var oldAttrs=p?.Attributes??0;var oldType=m.MethodSig.Params[index];var materialized=p==null;if(p==null){p=new ParamDefUser(null,(ushort)(index+1));m.ParamDefs.Add(p);}if(op.TryGetProperty("name",out var n))p.Name=n.ValueKind==JsonValueKind.Null?null:NonEmpty(n,"name");if(op.TryGetProperty("attributes",out var a))p.Attributes=(ParamAttributes)Attributes(a,"parameter");var oldMarshal=p.MarshalType;var paramParser=new EditTypeSigParser(module,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count);if(op.TryGetProperty("parameter_type",out var t))m.MethodSig.Params[index]=ResolveTypeEntry(t,module,map,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count);if(op.TryGetProperty("marshal",out var ma))p.MarshalType=EditMarshalCodec.Parse(module,ma,paramParser);var current=p;return Outcome("parameter_update",null,current,"parameter:"+index,current.Name,()=>{m.MethodSig.Params[index]=oldType;current.MarshalType=oldMarshal;if(materialized)m.ParamDefs.Remove(current);else{current.Name=oldName;current.Attributes=oldAttrs;}},new[]{Risk("signature_change",m)});}
	static EditOperationOutcome ParameterRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var target=ResolveParameter(module,op.GetProperty("parameter_target"),map);var m=target.method;if(target.index!=m.MethodSig.Params.Count-1)Invalid("parameter_target","Only tail parameter removal is supported");var p=target.param;if(HasParameterReference(m,target.index)||(p!=null&&HasAttachment(module,p)))Invalid("parameter_target","Parameter is referenced or attached");var oldType=m.MethodSig.Params[target.index];var wasInParamDefs=p!=null;dnlib.DotNet.MethodDef? host=null;if(p!=null){m.ParamDefs.Remove(p);RemoveMapValue(map,p);if(p.MDToken.Rid!=0)host=EditDeletedRowsTombstone.AcquireParamHost(module,p);}m.MethodSig.Params.RemoveAt(target.index);IMDTokenProvider removedTarget=p is null ? m : p;return Outcome("parameter_remove",null,removedTarget,"parameter:"+target.index,null,()=>{m.MethodSig.Params.Add(oldType);if(wasInParamDefs){if(host!=null){host.ParamDefs.Remove(p!);EditDeletedRowsTombstone.ReleaseParamHost(host);}m.ParamDefs.Add(p!);}},new[]{Risk("signature_change",m)});}

	static EditOperationOutcome GenericAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var owner=Ref<IMDTokenProvider>(module,op.GetProperty("owner"),map);IList<GenericParam> collection;if(owner is TypeDef t)collection=t.GenericParameters;else if(owner is MethodDef m)collection=m.GenericParameters;else throw Validation("owner","Generic owner must be type or method");var oldArity=owner is MethodDef oldMethod ? oldMethod.MethodSig.GenParamCount : 0;var oldCallingConvention=owner is MethodDef oldConventionMethod?oldConventionMethod.MethodSig.CallingConvention:0;var requested=(int)RequiredUInt(op,"generic_index");if(requested!=collection.Count)Invalid("generic_index","Only tail generic parameter insertion is supported");var gp=new GenericParamUser((ushort)requested,(GenericParamAttributes)OptionalAttributes(op,"attributes","generic",0),RequiredString(op,"name"));if(op.TryGetProperty("constraints",out var addCons))SetGenericConstraints(module,gp,addCons,map);collection.Add(gp);if(owner is MethodDef method){method.MethodSig.GenParamCount=(uint)collection.Count;method.MethodSig.CallingConvention|=CallingConvention.Generic;}var id=ObjectId(index,0);map[id]=gp;return Outcome("generic_parameter_add",id,gp,null,gp.Name,()=>{collection.Remove(gp);if(owner is MethodDef mm){mm.MethodSig.GenParamCount=oldArity;mm.MethodSig.CallingConvention=oldCallingConvention;}map.Remove(id);},new[]{Risk("signature_change",owner)});}
	static EditOperationOutcome GenericUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var gp=Ref<GenericParam>(module,op.GetProperty("target"),map);var oldName=gp.Name;var oldFlags=gp.Flags;var oldConstraints=gp.GenericParamConstraints.ToArray();if(op.TryGetProperty("name",out var n))gp.Name=NonEmpty(n,"name");if(op.TryGetProperty("attributes",out var a))gp.Flags=(GenericParamAttributes)Attributes(a,"generic");if(op.TryGetProperty("constraints",out var cons))SetGenericConstraints(module,gp,cons,map);return Outcome("generic_parameter_update",null,gp,oldName,gp.Name,()=>{gp.Name=oldName;gp.Flags=oldFlags;gp.GenericParamConstraints.Clear();foreach(var c in oldConstraints)gp.GenericParamConstraints.Add(c);},new[]{Risk("signature_change",gp)});}

	// P04 IMP-002: whole-list generic constraint replacement. Self constraints
	// and constraint cycles are structural errors (dnlib GenericParamConstraints).
	static void SetGenericConstraints(ModuleDef module, GenericParam gp, JsonElement cons, Dictionary<string, IMDTokenProvider>? map = null) {
		if (cons.ValueKind == JsonValueKind.Null) { gp.GenericParamConstraints.Clear(); return; }
		if (cons.ValueKind != JsonValueKind.Array) Invalid("constraints", "constraints must be an array of type entries");
		var ownerType = gp.Owner as TypeDef; var ownerMethod = gp.Owner as MethodDef;
		var parsed = new List<ITypeDefOrRef>();
		foreach (var row in cons.EnumerateArray()) {
			// T004: v2 rows carry structured {kind:type,type:...} nodes so a
			// constraint can reference rows created earlier in the sequence;
			// plain strings keep the frozen v1 grammar.
			TypeSig signature = row.ValueKind == JsonValueKind.String
				? new EditTypeSigParser(module, ownerType?.GenericParameters.Count ?? 0, ownerMethod?.GenericParameters.Count ?? 0).Parse(row.GetString()!)
				: ResolveTypeEntry(row, module, map ?? new Dictionary<string, IMDTokenProvider>(), ownerType?.GenericParameters.Count ?? 0, ownerMethod?.GenericParameters.Count ?? 0);
			// A bare generic variable target becomes a TypeSpec row (ECMA-335
			// II 23.2.5: constraints to another generic parameter); its own
			// parameter is what the self/cycle checks reason about.
			ITypeDefOrRef? target = signature is GenericVar or GenericMVar ? new TypeSpecUser(signature) : signature.ToTypeDefOrRef();
			if (target == null) { Invalid("constraints", "constraint must resolve to a type definition, reference or generic parameter"); throw new InvalidOperationException(); }
			var parameter = GenericParameterOf(signature, gp);
			if (parameter != null) {
				if (ReferenceEquals(parameter, gp)) Invalid("constraints", "a generic parameter cannot constrain itself");
				if (ConstraintClosureUses(parameter, gp)) Invalid("constraints", "constraint cycle detected");
			}
			if (parsed.Any(x => string.Equals(x.FullName, target!.FullName, StringComparison.Ordinal))) Invalid("constraints", "duplicate constraint");
			parsed.Add(target);
		}
		gp.GenericParamConstraints.Clear();
		foreach (var target in parsed) gp.GenericParamConstraints.Add(new GenericParamConstraintUser(target!));
	}
	static GenericParam? GenericParameterOf(TypeSig signature, GenericParam owner) {
		if (signature is GenericVar variable) {
			var container = owner.Owner as TypeDef;
			var index = (int)variable.Number;
			return container != null && index < container.GenericParameters.Count ? container.GenericParameters[index] : null;
		}
		if (signature is GenericMVar methodVariable) {
			var container = owner.Owner as MethodDef;
			var index = (int)methodVariable.Number;
			return container != null && index < container.GenericParameters.Count ? container.GenericParameters[index] : null;
		}
		return null;
	}
	static bool ConstraintClosureUses(GenericParam start, GenericParam target) {
		var seen = new HashSet<GenericParam>(); var queue = new Queue<GenericParam>(); queue.Enqueue(start);
		while (queue.Count > 0) {
			var current = queue.Dequeue();
			foreach (var constraint in current.GenericParamConstraints)
				if (constraint.Constraint is GenericParam candidate) {
					if (ReferenceEquals(candidate, target)) return true;
					if (seen.Add(candidate)) queue.Enqueue(candidate);
				}
		}
		return false;
	}
	static EditOperationOutcome GenericRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var gp=Ref<GenericParam>(module,op.GetProperty("target"),map);IList<GenericParam> col;MethodDef? method=null;if(gp.Owner is TypeDef t)col=t.GenericParameters;else if(gp.Owner is MethodDef m){method=m;col=m.GenericParameters;}else throw Validation("target","Generic parameter has no owner");if(gp.Number!=col.Count-1)Invalid("target","Only tail generic parameter removal is supported");if(IsGenericUsed(module,gp)||HasAttachment(module,gp))Invalid("target","Generic parameter is used or attached");var oldArity=method?.MethodSig.GenParamCount??0;var oldCallingConvention=method?.MethodSig.CallingConvention??0;var riskOwner=(IMDTokenProvider?)gp.Owner??gp;col.Remove(gp);if(method!=null){method.MethodSig.GenParamCount=(uint)col.Count;if(col.Count==0)method.MethodSig.CallingConvention&=~CallingConvention.Generic;}RemoveMapValue(map,gp);return Outcome("generic_parameter_remove",null,gp,gp.Name,null,()=>{col.Add(gp);if(method!=null){method.MethodSig.GenParamCount=oldArity;method.MethodSig.CallingConvention=oldCallingConvention;}},new[]{Risk("signature_change",riskOwner)});}

	// P08 IMP-001/002/004: managed and Win32 resources, icon groups, and the
	// gated strong-name removal.  Payload bytes are always inline in the
	// operation (data_base64) — checkpoints and replay never depend on files.
	static EditOperationOutcome ManagedResourceAdd(ModuleDef module, JsonElement op) {
		var name = RequiredString(op, "name");
		var attributes = (dnlib.DotNet.ManifestResourceAttributes)OptionalAttributes(op, "attributes", "resource", 3);
		var bytes = DecodePayload(op);
		if (module.Resources.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
			Invalid("name", "a managed resource with this name already exists: " + name);
		var resource = new EmbeddedResource(name, bytes, attributes);
		module.Resources.Add(resource);
		var id = "res:" + name;
		return Outcome("managed_resource_add", null, resource, null, name,
			() => module.Resources.Remove(resource), ResourceRisks(name));
	}

	static EditOperationOutcome ManagedResourceUpdate(ModuleDef module, JsonElement op) {
		var resource = ManagedResource(module, op.GetProperty("target").GetProperty("name").GetString()!);
		var existing = (EmbeddedResource)resource;
		var before = existing.CreateReader().ToArray();
		byte[] after = Array.Empty<byte>();
		if (op.TryGetProperty("data_base64", out var blobValue)) {
			after = DecodePayload(op);
		}
		else if (op.TryGetProperty("entry", out var entryValue) && entryValue.ValueKind == JsonValueKind.Object) {
			var entryName = RequiredString(entryValue, "name");
			var kind = RequiredString(entryValue, "value_kind");
			if (!EditResourceCodec.EditableKinds.Contains(kind, StringComparer.Ordinal))
				Invalid("entry.value_kind", "the entry kind is outside the standard edit domain (use data_base64): " + kind);
			var parsed = EditResourceCodec.Parse(before);
			var target = parsed.Entries.FirstOrDefault(e => string.Equals(e.Name, entryName, StringComparison.Ordinal));
			if (target == null) Invalid("entry.name", "the resource entry was not found: " + entryName);
			if (EditResourceCodec.KindOf(target.TypeCode) == "custom")
				Invalid("entry.name", "the resource entry carries a payload outside the entry edit domain (use data_base64): " + entryName);
			// entry edits keep the stored kind (a byte-array row — which may hold a
			// serialized custom object payload — never becomes a scalar in place)
			if (!string.Equals(EditResourceCodec.KindOf(target.TypeCode), kind, StringComparison.Ordinal))
				Invalid("entry.value_kind", "the entry kind must match the stored kind '" + EditResourceCodec.KindOf(target.TypeCode) + "' (whole-blob data_base64 for shape changes): " + entryName);
			var encoded = EditResourceCodec.EncodeEntry(entryName, kind, entryValue.GetProperty("value"), target.TypeCode);
			if (!EditResourceCodec.IsStandardKind(encoded.TypeCode))
				Invalid("entry.value_kind", "the encoded entry kind is outside the standard domain");
			var edited = new Dictionary<string, EditResourceCodec.ResourceEntry> { [entryName] = encoded };
			after = EditResourceCodec.Rebuild(parsed, edited);
		}
		else Invalid("operation", "managed_resource_update needs entry or data_base64");
		var replacement = new EmbeddedResource(existing.Name, after, existing.Attributes);
		var index = module.Resources.IndexOf(existing);
		module.Resources[index] = replacement;
		return Outcome("managed_resource_update", null, replacement, null, replacement.Name.String,
			() => module.Resources[index] = existing, ResourceRisks(replacement.Name.String));
	}

	static EditOperationOutcome ManagedResourceRemove(ModuleDef module, JsonElement op) {
		RequireRemoveMode(op);
		var name = op.GetProperty("target").GetProperty("name").GetString()!;
		var resource = ManagedResource(module, name);
		var index = module.Resources.IndexOf(resource);
		var embedded = resource as EmbeddedResource;
		var bytes = embedded?.CreateReader().ToArray();
		module.Resources.RemoveAt(index);
		return Outcome("managed_resource_remove", null, resource, name, null,
			() => module.Resources.Insert(index, resource), ResourceRisks(name));
	}

	static dnlib.DotNet.Resource ManagedResource(ModuleDef module, string name) =>
		module.Resources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
		?? throw Validation("target.name", "the managed resource was not found: " + name);

	static byte[] DecodePayload(JsonElement op) {
		var text = RequiredString(op, "data_base64");
		if (text.Length > 12 * 1024 * 1024) Invalid("data_base64", "the resource payload exceeds the base64 budget");
		byte[] bytes;
		try { bytes = Convert.FromBase64String(text); }
		catch (FormatException) { Invalid("data_base64", "the resource payload is not valid base64"); throw; }
		if (bytes.Length > EditWire.MaxResourceBytes) Capacity("resource_bytes");
		return bytes;
	}

	static IReadOnlyList<Dictionary<string, object?>> ResourceRisks(string name) =>
		new[] { Risk("resource_change", new ResourceRiskTarget(name)) };

	sealed class ResourceRiskTarget : IMDTokenProvider {
		readonly string name;
		public ResourceRiskTarget(string name) => this.name = name;
		public MDToken MDToken { get; set; }
		public uint Rid { get; set; }
		public override string ToString() => name;
	}

	static dnlib.IO.DataReaderFactory Factory(byte[] bytes) =>
		dnlib.IO.ByteArrayDataReaderFactory.Create(bytes, null);

	// P08 IMP-002: Win32 rows address (type, name, language) through the
	// Win32Resources Root directory tree (type -> name -> language -> data).
	// RT_ICON removal validates the icon-group references first (an icon group
	// must never dangle — ACC-007 failure condition).
	static EditOperationOutcome Win32ResourceAdd(ModuleDef module, JsonElement op) {
		var type = Win32Name(op, "type_id", "type_name", "type");
		var rowName = Win32Name(op, "name_id", "name_string", "name");
		var langId = (uint)(op.TryGetProperty("lang_id", out var langValue) ? langValue.GetUInt32() : 0);
		var bytes = DecodePayload(op);
		var existing = FindWin32Data(module, type, rowName, langId);
		if (existing != null)
			Invalid("name", "a Win32 resource row with this identity already exists");
		var typeDirectory = DirectoryFor(module, type, create: true);
		var nameDirectory = typeDirectory.FindDirectory(rowName);
		if (nameDirectory == null) {
			nameDirectory = new dnlib.W32Resources.ResourceDirectoryUser(rowName);
			typeDirectory.Directories.Add(nameDirectory);
		}
		var data = new dnlib.W32Resources.ResourceData(new dnlib.W32Resources.ResourceName((int)langId),
			Factory(bytes), 0, (uint)bytes.Length);
		nameDirectory.Data.Add(data);
		if (IsIconType(type)) ValidateIconGroups(module);
		return Outcome("win32_resource_add", null, module, null, Win32Identity(type, rowName, langId),
			() => { nameDirectory.Data.Remove(data); PruneEmpty(nameDirectory, typeDirectory, module); },
			ResourceRisks(Win32Identity(type, rowName, langId)));
	}

	static EditOperationOutcome Win32ResourceUpdate(ModuleDef module, JsonElement op) {
		var type = Win32Name(op, "type_id", "type_name", "type");
		var rowName = Win32Name(op, "name_id", "name_string", "name");
		var langId = (uint)(op.TryGetProperty("lang_id", out var langValue) ? langValue.GetUInt32() : 0);
		var data = FindWin32Data(module, type, rowName, langId) ?? throw Validation("target", "the Win32 resource row was not found");
		var before = data.CreateReader().ToArray();
		var after = DecodePayload(op);
		var nameDirectory = typeDirectoryOf(module, type, rowName);
		var slot = nameDirectory.Data.IndexOf(data);
		var replacement = new dnlib.W32Resources.ResourceData(new dnlib.W32Resources.ResourceName((int)langId),
			Factory(after), 0, (uint)after.Length);
		nameDirectory.Data[slot] = replacement;
		try {
			if (IsIconType(type)) ValidateIconGroups(module);
		}
		catch {
			nameDirectory.Data[slot] = data;
			throw;
		}
		return Outcome("win32_resource_update", null, module, null, Win32Identity(type, rowName, langId),
			() => nameDirectory.Data[slot] = data,
			ResourceRisks(Win32Identity(type, rowName, langId)));
	}

	static EditOperationOutcome Win32ResourceRemove(ModuleDef module, JsonElement op) {
		RequireRemoveMode(op);
		var type = Win32Name(op, "type_id", "type_name", "type");
		var rowName = Win32Name(op, "name_id", "name_string", "name");
		var langId = (uint)(op.TryGetProperty("lang_id", out var langValue) ? langValue.GetUInt32() : 0);
		var typeDirectory = DirectoryFor(module, type, create: false) ?? throw Validation("target", "the Win32 type directory was not found");
		var nameDirectory = typeDirectory.FindDirectory(rowName) ?? throw Validation("target", "the Win32 resource row was not found");
		var langName = new dnlib.W32Resources.ResourceName((int)langId);
		var data = nameDirectory.Data.FirstOrDefault(d => d.Name == langName) ?? throw Validation("target", "the Win32 resource row was not found");
		nameDirectory.Data.Remove(data);
		try {
			if (IsIconType(type)) ValidateIconGroups(module);
		}
		catch {
			nameDirectory.Data.Add(data);
			throw;
		}
		PruneEmpty(nameDirectory, typeDirectory, module);
		var capture = data.CreateReader().ToArray();
		var removed = new dnlib.W32Resources.ResourceData(langName,
			Factory(capture), 0, (uint)capture.Length);
		PruneEmpty(nameDirectory, typeDirectory, module);
		return Outcome("win32_resource_remove", null, module, null, null,
			() => {
				var restoreType = DirectoryFor(module, type, create: true);
				var restoreName = restoreType.FindDirectory(rowName);
				if (restoreName == null) {
					restoreName = new dnlib.W32Resources.ResourceDirectoryUser(rowName);
					restoreType.Directories.Add(restoreName);
				}
				restoreName.Data.Add(removed);
			},
			ResourceRisks(Win32Identity(type, rowName, langId)));
	}

	static void PruneEmpty(dnlib.W32Resources.ResourceDirectory nameDirectory, dnlib.W32Resources.ResourceDirectory typeDirectory, ModuleDef module) {
		if (nameDirectory.Data.Count == 0 && nameDirectory.Directories.Count == 0) {
			typeDirectory.Directories.Remove(nameDirectory);
			if (typeDirectory.Data.Count == 0 && typeDirectory.Directories.Count == 0)
				module.Win32Resources.Root.Directories.Remove(typeDirectory);
		}
	}

	static bool IsIconType(dnlib.W32Resources.ResourceName type) =>
		type.HasId && type.Id is 3 or 14;

	static dnlib.W32Resources.ResourceData? FindWin32Data(ModuleDef module,
			dnlib.W32Resources.ResourceName type, dnlib.W32Resources.ResourceName name, uint langId) {
		var typeDirectory = DirectoryFor(module, type, create: false);
		if (typeDirectory == null) return null;
		var nameDirectory = typeDirectory.FindDirectory(name);
		if (nameDirectory == null) return null;
		var langName = new dnlib.W32Resources.ResourceName((int)langId);
		return nameDirectory.Data.FirstOrDefault(d => d.Name == langName);
	}

	static dnlib.W32Resources.ResourceDirectory typeDirectoryOf(ModuleDef module,
			dnlib.W32Resources.ResourceName type, dnlib.W32Resources.ResourceName name) {
		var typeDirectory = DirectoryFor(module, type, create: false)
			?? throw Validation("target", "the Win32 type directory was not found");
		return typeDirectory.FindDirectory(name) ?? throw Validation("target", "the Win32 resource row was not found");
	}

	static dnlib.W32Resources.ResourceDirectory? DirectoryFor(ModuleDef module,
			dnlib.W32Resources.ResourceName type, bool create) {
		var existing = module.Win32Resources.Root.FindDirectory(type);
		if (existing != null || !create) return existing;
		var created = new dnlib.W32Resources.ResourceDirectoryUser(type);
		module.Win32Resources.Root.Directories.Add(created);
		return created;
	}

	static dnlib.W32Resources.ResourceName Win32Name(JsonElement op, string idField, string nameField, string label) {
		if (op.TryGetProperty(idField, out var idValue) && idValue.ValueKind == JsonValueKind.Number)
			return new dnlib.W32Resources.ResourceName((int)idValue.GetUInt32());
		if (op.TryGetProperty(nameField, out var nameValue) && nameValue.ValueKind == JsonValueKind.String && nameValue.GetString()!.Length != 0)
			return new dnlib.W32Resources.ResourceName(nameValue.GetString()!);
		throw Validation(idField, label + " requires " + idField + " or " + nameField);
	}

	static string Win32Identity(dnlib.W32Resources.ResourceName type, dnlib.W32Resources.ResourceName name, uint langId) =>
		(type.HasId ? "id:" + type.Id.ToString(CultureInfo.InvariantCulture) : "name:" + type.Name)
		+ "/" + (name.HasId ? "id:" + name.Id.ToString(CultureInfo.InvariantCulture) : "name:" + name.Name)
		+ "@" + langId.ToString(CultureInfo.InvariantCulture);

	/// <summary>RT_GROUP_ICON directories must reference existing RT_ICON rows;
	/// an edit that would dangle an icon reference rejects.</summary>
	static void ValidateIconGroups(ModuleDef module) {
		var iconType = module.Win32Resources.Root.FindDirectory(new dnlib.W32Resources.ResourceName(3));
		// the icon ID lives on the NAME directory (type/name/lang tree); the data
		// row's own name is the language id
		var iconIds = new HashSet<int>();
		foreach (var nameDirectory in iconType?.Directories ?? Enumerable.Empty<dnlib.W32Resources.ResourceDirectory>())
			if (nameDirectory.Name.HasId && nameDirectory.Data.Count != 0) iconIds.Add(nameDirectory.Name.Id);
		var groupType = module.Win32Resources.Root.FindDirectory(new dnlib.W32Resources.ResourceName(14));
		if (groupType == null) return;
		foreach (var group in groupType.Directories.SelectMany(directory => directory.Data)) {
			var blob = group.CreateReader().ToArray();
			if (blob.Length < 6) Invalid("icon_group", "an RT_GROUP_ICON directory is malformed");
			var count = BitConverter.ToUInt16(blob, 4);
			for (var index = 0; index < count; index++) {
				var entryOffset = 6 + index * 14;
				if (entryOffset + 14 > blob.Length) Invalid("icon_group", "an RT_GROUP_ICON directory is truncated");
				var iconId = BitConverter.ToUInt16(blob, entryOffset + 12);
				if (!iconIds.Contains(iconId))
					Invalid("icon_group", "the edit would dangle icon id " + iconId
						+ " referenced by group " + (group.Name.HasId ? group.Name.Id.ToString(CultureInfo.InvariantCulture) : group.Name.Name));
			}
		}
	}

	// P08 IMP-004: gated strong-name removal.  The evidence gate itself lives in
	// the coordinator (it owns the debug-event buffer); the operation assumes the
	// gate already ran and removes the public key.
	static EditOperationOutcome StrongNameRemove(ModuleDef module, JsonElement op) {
		var assembly = module.Assembly ?? throw Validation("strong_name_remove", "module has no assembly row");
		if (assembly.PublicKey is null || assembly.PublicKey.Data is null || assembly.PublicKey.Data.Length == 0
			|| (assembly.Attributes & dnlib.DotNet.AssemblyAttributes.PublicKey) == 0)
			throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
				["kind"] = "capability", ["capability"] = "strong_name_remove",
				["reason"] = "the target assembly has no applicable strong-name public key",
			});
		if (op.TryGetProperty("dynamic_failure", out var evidence)) {
			if (!evidence.TryGetProperty("session_id", out _))
				Invalid("dynamic_failure", "the dynamic failure evidence must carry session_id and event_cursor");
			if (!evidence.TryGetProperty("event_cursor", out var cursorValue) || !cursorValue.TryGetInt64(out var cursor) || cursor <= 0)
				Invalid("dynamic_failure", "the evidence event_cursor must be a positive cursor");
		}
		else Invalid("dynamic_failure", "strong_name_remove requires dynamic failure evidence");
		var oldKey = assembly.PublicKey;
		var oldAttributes = assembly.Attributes;
		assembly.PublicKey = null;
		// strip the public-key signature flag; keep everything else
		assembly.Attributes = oldAttributes & ~dnlib.DotNet.AssemblyAttributes.PublicKey;
		return Outcome("strong_name_remove", null, assembly, oldKey == null ? null : "key:" + EditWire.Sha256(oldKey.Data), null,
			() => { assembly.PublicKey = oldKey; assembly.Attributes = oldAttributes; },
			new[] { Risk("strong_name_change", assembly) });
	}

	// P07 IMP-001: assembly/module identity, AssemblyRef and entry point rows.
	// Replay determinism rides the checkpoint image (byte-level); the conflict
	// fingerprint projection is deliberately unchanged so pre-P07 lineages keep
	// their exact/validated classifications (adjudicated AUD-001/002).
	static EditOperationOutcome AssemblyUpdate(ModuleDef module, JsonElement op) {
		var assembly = module.Assembly ?? throw Validation("assembly_update", "module has no assembly row");
		var oldName = assembly.Name; var oldVersion = assembly.Version; var oldCulture = assembly.Culture;
		var hasName = op.TryGetProperty("name", out var nameValue);
		var hasVersion = op.TryGetProperty("version", out var versionValue);
		var hasCulture = op.TryGetProperty("culture", out var cultureValue);
		if (!hasName && !hasVersion && !hasCulture) Invalid("assembly_update", "at least one of name, version, culture is required");
		if (hasName) assembly.Name = new UTF8String(NonEmpty(nameValue, "name"));
		if (hasVersion) assembly.Version = ParseVersionText(RequiredString(op, "version"));
		if (hasCulture) assembly.Culture = cultureValue.ValueKind == JsonValueKind.Null || cultureValue.GetString()!.Length == 0
			? UTF8String.Empty : new UTF8String(cultureValue.GetString()!);
		return Outcome("assembly_update", null, assembly, null, assembly.FullName,
			() => { assembly.Name = oldName; assembly.Version = oldVersion; assembly.Culture = oldCulture; },
			new[] { Risk("assembly_identity_change", assembly) });
	}

	static EditOperationOutcome ModuleUpdate(ModuleDef module, JsonElement op) {
		var oldName = module.Name;
		module.Name = new UTF8String(RequiredString(op, "name"));
		return Outcome("module_update", null, module, null, module.Name.String,
			() => module.Name = oldName, new[] { Risk("module_identity_change", module) });
	}

	static EditOperationOutcome AssemblyRefUpdate(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var reference = op.GetProperty("target").TryGetProperty("token", out var tokenValue)
			? ResolveToken(module, ParseToken(tokenValue.GetString()!)) as AssemblyRef
				?? throw Validation("target", "assembly_ref_update target must resolve to an AssemblyRef row")
			: map.TryGetValue(RequiredString(op.GetProperty("target"), "object_id"), out var bound) && bound is AssemblyRef boundRef
				? boundRef : throw Validation("target", "assembly_ref_update target must resolve to an AssemblyRef row");
		var oldName = reference.Name; var oldVersion = reference.Version; var oldCulture = reference.Culture;
		var hasName = op.TryGetProperty("name", out var nameValue);
		var hasVersion = op.TryGetProperty("version", out var versionValue);
		var hasCulture = op.TryGetProperty("culture", out var cultureValue);
		if (!hasName && !hasVersion && !hasCulture) Invalid("assembly_ref_update", "at least one of name, version, culture is required");
		if (hasName) reference.Name = new UTF8String(NonEmpty(nameValue, "name"));
		if (hasVersion) reference.Version = ParseVersionText(RequiredString(op, "version"));
		if (hasCulture) reference.Culture = cultureValue.ValueKind == JsonValueKind.Null || cultureValue.GetString()!.Length == 0
			? UTF8String.Empty : new UTF8String(cultureValue.GetString()!);
		return Outcome("assembly_ref_update", null, reference, null, reference.FullName,
			() => { reference.Name = oldName; reference.Version = oldVersion; reference.Culture = oldCulture; },
			new[] { Risk("assembly_ref_change", reference) });
	}

	static EditOperationOutcome EntryPointSet(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		MethodDef? entry = null;
		if (op.TryGetProperty("entry_point", out var entryValue) && entryValue.ValueKind == JsonValueKind.Object) {
			if (entryValue.TryGetProperty("token", out var tokenValue)) {
				entry = ResolveToken(module, ParseToken(tokenValue.GetString()!)) as MethodDef
					?? throw Validation("entry_point", "entry_point must resolve to a MethodDef in this module");
			}
			else if (map.TryGetValue(RequiredString(entryValue, "object_id"), out var bound) && bound is MethodDef boundMethod)
				entry = boundMethod;
			else throw Validation("entry_point", "entry_point must resolve to a MethodDef in this module");
			if (entry.DeclaringType == null || !ReferenceEquals(entry.Module, module))
				throw Validation("entry_point", "entry_point must be a method of this module");
		}
		var old = module.ManagedEntryPoint;
		module.ManagedEntryPoint = entry;
		return Outcome("entry_point_set", null, module, old == null ? null : "entry", entry == null ? null : entry.FullName,
			() => module.ManagedEntryPoint = old, new[] { Risk("entry_point_change", module) });
	}

	static Version ParseVersionText(string text) {
		if (string.IsNullOrEmpty(text) || text.Length > 64) Invalid("version", "version must be Major[.Minor[.Build[.Revision]]]");
		var parts = text.Split('.');
		if (parts.Length > 4) Invalid("version", "version must have at most four components");
		var numbers = new int[4];
		for (var index = 0; index < parts.Length; index++) {
			if (parts[index].Length == 0 || parts[index].Length > 9 || !int.TryParse(parts[index], out numbers[index]) || numbers[index] < 0)
				Invalid("version", "version components must be non-negative integers");
		}
		return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
	}

	static EditOperationOutcome BodyReplace(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var m=Ref<MethodDef>(module,op.GetProperty("target"),map);var old=m.Body;var documentsBefore=CapturePdbDocuments(module);var body=BuildBody(module,m,op.GetProperty("body"),map);m.Body=body;var addedDocuments=module.PdbState==null?Array.Empty<PdbDocument>():module.PdbState.Documents.Where(d=>!documentsBefore.Contains(d)).ToArray();var oldDebug=m.CustomDebugInfos.ToArray();ApplyMethodDebugInfo(module,op,m,map);var risks=new List<Dictionary<string,object?>> { Risk("body_change",m) };if((old?.ExceptionHandlers.Count??0)!=body.ExceptionHandlers.Count)risks.Add(Risk("eh_change",m));return Outcome("method_body_replace",null,m,old==null?null:"body", "body",()=>{m.Body=old;m.CustomDebugInfos.Clear();foreach(var row in oldDebug)m.CustomDebugInfos.Add(row);ReleasePdbDocuments(module,addedDocuments);},risks);}

	// P06: optional method custom debug info rows (reference grammar: tokens of
	// module rows or object IDs of operations in the same sequence).  Absent =
	// the frozen P02 behavior is unchanged.
	static void ApplyMethodDebugInfo(ModuleDef module,JsonElement op,MethodDef method,Dictionary<string,IMDTokenProvider> map){
		if(!op.TryGetProperty("custom_debug_infos",out var rowsElement))return;
		EditPdbTransferCodec.CdiRow[]? rows;
		try{rows=JsonSerializer.Deserialize<EditPdbTransferCodec.CdiRow[]>(rowsElement.GetRawText(),EditWire.JsonOptions);}
		catch(JsonException ex){throw Validation("custom_debug_infos",ex.Message);}
		EditPdbTransferCodec.ApplyMethodDebugInfo(module,method,rows??Array.Empty<EditPdbTransferCodec.CdiRow>(),text=>ResolveOperationReference(module,map,text));
	}

	static IMDTokenProvider ResolveOperationReference(ModuleDef module,Dictionary<string,IMDTokenProvider> map,string text){
		if(text.StartsWith("0x",StringComparison.OrdinalIgnoreCase))return ResolveToken(module,ParseToken(text));
		return map.TryGetValue(text,out var found)?found:throw Validation("custom_debug_infos","Unknown token or object ID: "+text);
	}

	static CilBody BuildBody(ModuleDef module,MethodDef method,JsonElement body,Dictionary<string,IMDTokenProvider> map){
		var insRows=body.GetProperty("instructions").EnumerateArray().ToList();var locals=body.GetProperty("locals").EnumerateArray().ToList();var ehs=body.GetProperty("exception_handlers").EnumerateArray().ToList();if(insRows.Count>EditWire.MaxBodyInstructions||locals.Count>EditWire.MaxBodyLocals||ehs.Count>EditWire.MaxBodyExceptionHandlers)Capacity("method_body");
		var result=new CilBody(RequiredBool(body,"init_locals"),new List<Instruction>(),new List<ExceptionHandler>(),new List<Local>()){
			MaxStack=(ushort)RequiredUInt(body,"max_stack"), KeepOldMaxStack=true, HeaderSize=12,
		};
		var parserOwnerType=method.DeclaringType?.GenericParameters.Count??0;var parserOwnerMethod=method.GenericParameters.Count;foreach(var l in locals)result.Variables.Add(new Local(ResolveTypeEntry(l.GetProperty("type"),module,map,parserOwnerType,parserOwnerMethod),l.GetProperty("name").ValueKind==JsonValueKind.Null?null:l.GetProperty("name").GetString()));
		foreach(var row in insRows){var name=RequiredString(row,"opcode");if(!OpCodesByName.TryGetValue(name,out var code))Invalid("body.instructions.opcode","Unknown opcode: "+name);result.Instructions.Add(new Instruction(code));}
		for(int i=0;i<insRows.Count;i++){var row=insRows[i];if(row.TryGetProperty("operand",out var operand)&&operand.ValueKind!=JsonValueKind.Null)result.Instructions[i].Operand=CanonicalOperand(result.Instructions[i],ParseOperand(module,method,result,operand,map));ValidateOperand(result.Instructions[i]);}
		foreach(var row in ehs){var kind=RequiredString(row,"kind");var eh=new ExceptionHandler(kind switch{"catch"=>ExceptionHandlerType.Catch,"finally"=>ExceptionHandlerType.Finally,"fault"=>ExceptionHandlerType.Fault,"filter"=>ExceptionHandlerType.Filter,_=>throw Validation("exception_handler","Unknown handler kind")}){TryStart=IndexOrEnd(result,row,"try_start"),TryEnd=IndexOrEnd(result,row,"try_end"),HandlerStart=IndexOrEnd(result,row,"handler_start"),HandlerEnd=IndexOrEnd(result,row,"handler_end"),FilterStart=NullableIndex(result,row,"filter_start")};if(row.GetProperty("catch_type").ValueKind!=JsonValueKind.Null)eh.CatchType=ResolveTypeEntry(row.GetProperty("catch_type"),module,map,parserOwnerType,parserOwnerMethod).ToTypeDefOrRef();result.ExceptionHandlers.Add(eh);}
		// P06: optional symbol payload — sequence points and the root PDB scope
		// travel with the body so private copy, live module and checkpoint replay
		// materialize identical symbol state.  Absent fields keep the frozen P02
		// behavior (a replaced body carries no symbol rows of its own).
		if(body.TryGetProperty("sequence_points",out var pointsElement)){
			EditPdbTransferCodec.PointRow[] points;
			try{points=JsonSerializer.Deserialize<EditPdbTransferCodec.PointRow[]>(pointsElement.GetRawText(),EditWire.JsonOptions)??Array.Empty<EditPdbTransferCodec.PointRow>();}
			catch(JsonException ex){throw Validation("body.sequence_points",ex.Message);}
			EditPdbTransferCodec.ApplyPoints(module,result,points);
		}
		if(body.TryGetProperty("scope",out var scopeElement)){
			EditPdbTransferCodec.ScopeRow? scope;
			var imports=new Dictionary<string,EditPdbTransferCodec.ImportScopeRow>(StringComparer.Ordinal);
			try{
				scope=JsonSerializer.Deserialize<EditPdbTransferCodec.ScopeRow>(scopeElement.GetRawText(),EditWire.JsonOptions);
				if(body.TryGetProperty("import_scopes",out var importsElement))
					imports=EditPdbTransferCodec.FromWireRows(JsonSerializer.Deserialize<EditPdbTransferCodec.ImportScopeWireRow[]>(importsElement.GetRawText(),EditWire.JsonOptions));
			}
			catch(JsonException ex){throw Validation("body.scope",ex.Message);}
			result.PdbMethod=new PdbMethod{Scope=EditPdbTransferCodec.RestoreScope(scope,result,module,imports)};
		}
		return result;
	}

	static object ParseOperand(ModuleDef module,MethodDef method,CilBody body,JsonElement op,Dictionary<string,IMDTokenProvider> map){var kind=RequiredString(op,"kind");return kind switch{"i32"=>(int)op.GetProperty("value").GetInt64(),"i64"=>op.GetProperty("value").GetInt64(),"f32"=>(float)op.GetProperty("value").GetDouble(),"f64"=>op.GetProperty("value").GetDouble(),"string"=>op.GetProperty("value").GetString()??string.Empty,"token"=>ResolveToken(module,ParseToken(op.GetProperty("token").GetString()!)),"object"=>map.TryGetValue(RequiredString(op,"object_id"),out var x)?x:throw Validation("operand.object_id","Unknown object ID"),"label"=>BodyIndex(body,(int)RequiredUInt(op,"instruction_index")),"switch"=>op.GetProperty("instruction_indices").EnumerateArray().Select(x=>BodyIndex(body,x.GetInt32())).ToArray(),"local"=>LocalAt(body,(int)RequiredUInt(op,"local_index")),"arg"=>ArgumentAt(method,(int)RequiredUInt(op,"argument_index")),_=>throw Validation("operand.kind","Unknown operand kind")};}
	// dnlib's writer requires the opcode's canonical operand type: ldc.i4.s is
	// the only ShortInlineI opcode and must carry a boxed sbyte, otherwise the
	// written image silently degrades the operand to 0 (round-57 evidence:
	// expected [il|Ldc_I4_S|42], actual [il|Ldc_I4_S|0]).  Validation accepts
	// sbyte/byte/int on the read side; the write side needs the canonical form.
	static object CanonicalOperand(Instruction instruction,object operand)=>instruction.OpCode.Code==Code.Ldc_I4_S&&operand is int value?(sbyte)value:operand;
	static Local LocalAt(CilBody body,int index){if(index<0||index>=body.Variables.Count)Invalid("operand.local_index","Local index is outside body variables");return body.Variables[index];}
	static Parameter ArgumentAt(MethodDef method,int index){if(index<0||index>=method.Parameters.Count)Invalid("operand.argument_index","Argument index is outside method parameters");return method.Parameters[index];}
	static void ValidateOperand(Instruction i){var o=i.Operand;bool valid=i.OpCode.OperandType switch{OperandType.InlineNone=>o==null,OperandType.ShortInlineI=>o is sbyte||o is byte||o is int,OperandType.InlineI=>o is int,OperandType.InlineI8=>o is long,OperandType.ShortInlineR=>o is float,OperandType.InlineR=>o is double,OperandType.InlineString=>o is string,OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget=>o is Instruction,OperandType.InlineSwitch=>o is Instruction[],OperandType.InlineVar or OperandType.ShortInlineVar=>o is Local||o is Parameter,OperandType.InlineField=>o is IField,OperandType.InlineMethod=>o is IMethod,OperandType.InlineType=>o is ITypeDefOrRef||o is TypeSig,OperandType.InlineTok=>o is ITokenOperand,OperandType.InlineSig=>o is StandAloneSig||o is CallingConventionSig,_=>false};if(!valid)Invalid("body.instructions.operand","Operand does not match opcode "+i.OpCode.Name);}

	static Constant ParseConstant(JsonElement c,TypeSig target){var kind=RequiredString(c,"kind");var v=c.GetProperty("value");object? value=kind switch{"null"=>null,"boolean"=>v.GetBoolean(),"char"=>(v.GetString()??throw Validation("constant","char missing"))[0],"string"=>v.GetString(),"r4"=>(float)v.GetDouble(),"r8"=>v.GetDouble(),"i1"=>(sbyte)v.GetInt64(),"u1"=>(byte)v.GetUInt64(),"i2"=>(short)v.GetInt64(),"u2"=>(ushort)v.GetUInt64(),"i4"=>(int)v.GetInt64(),"u4"=>(uint)v.GetUInt64(),"i8"=>v.GetInt64(),"u8"=>v.GetUInt64(),_=>throw Validation("constant.kind","Unknown constant kind")};if(value==null&&!IsReferenceType(target))Invalid("constant","null constant requires a reference type");return new ConstantUser(value);}

	static T Ref<T>(ModuleDef module,JsonElement reference,Dictionary<string,IMDTokenProvider> map) where T:class,IMDTokenProvider{IMDTokenProvider value;if(reference.TryGetProperty("token",out var token))value=ResolveToken(module,ParseToken(token.GetString()!));else if(reference.TryGetProperty("object_id",out var id)&&map.TryGetValue(id.GetString()!,out var found))value=found;else if(reference.TryGetProperty("object_id",out var objectId)&&reference.TryGetProperty("address",out var address)&&address.ValueKind==JsonValueKind.String){value=EditDefinitionAddress.Resolve(module,address.GetString()!);map[objectId.GetString()!]=value;}else throw Validation("reference","Unknown token or object ID");if(value is T typed)return typed;throw Validation("reference","Reference has the wrong metadata kind");}
	static T? OptionalRef<T>(ModuleDef module,JsonElement op,string name,Dictionary<string,IMDTokenProvider> map) where T:class,IMDTokenProvider=>op.TryGetProperty(name,out var r)&&r.ValueKind!=JsonValueKind.Null?Ref<T>(module,r,map):null;
	[ThreadStatic] static TokenBindingScope? serializedTokenBindings;
	sealed class TokenBindingScope : IDisposable {
		readonly TokenBindingScope? previous;
		public ModuleDef Target { get; }
		public Dictionary<uint, IMDTokenProvider> Bindings { get; }
		public TokenBindingScope(ModuleDef target, Dictionary<uint, IMDTokenProvider> bindings) {
			previous = serializedTokenBindings;
			Target = target; Bindings = bindings; serializedTokenBindings = this;
		}
		public void Dispose() => serializedTokenBindings = previous;
	}

	// A committed MethodDefUser has RID zero in dnSpy's live graph, while the
	// next transaction's serialized private copy gives it a metadata token.
	// Bind only definitions whose token is absent from the destination graph,
	// using the exact pre-operation graph's positional address.  Callers bind
	// this scope only after their existing live/checkpoint version gates.
	internal static IDisposable BindSerializedTokens(ModuleDef source, ModuleDef target) {
		var bindings = new Dictionary<uint, IMDTokenProvider>();
		foreach (var type in source.GetTypes()) {
			Bind(type);
			foreach (var method in type.Methods) {
				Bind(method);
				foreach (var parameter in method.ParamDefs) Bind(parameter);
				foreach (var generic in method.GenericParameters) Bind(generic);
			}
			foreach (var field in type.Fields) Bind(field);
			foreach (var property in type.Properties) Bind(property);
			foreach (var eventDef in type.Events) Bind(eventDef);
			foreach (var generic in type.GenericParameters) Bind(generic);
		}
		void Bind(IMDTokenProvider row) {
			if (row.MDToken.Rid == 0 || target.ResolveToken(row.MDToken.Raw) != null) return;
			var match = EditDefinitionAddress.Resolve(target, EditDefinitionAddress.Capture(source, row));
			if (match.MDToken.Table != row.MDToken.Table || !SameBindingIdentity(row, match))
				throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			if (bindings.ContainsKey(row.MDToken.Raw)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			bindings.Add(row.MDToken.Raw, match);
		}
		bool SameBindingIdentity(IMDTokenProvider serialized, IMDTokenProvider attached) {
			// dnlib renders a materialized generic method as Added<T>(T), but
			// an imported MethodDefUser as Added<T>(!!0).  The exact positional
			// address and caller's semantic gate already bind the graph; compare
			// the method's actual signature rather than its display rendering.
			if (serialized is MethodDef sourceMethod && attached is MethodDef targetMethod)
				return sourceMethod.Name == targetMethod.Name
					&& new SigComparer().Equals(sourceMethod.MethodSig, targetMethod.MethodSig);
			return serialized is not IFullName source || attached is not IFullName target
				|| string.Equals(source.FullName, target.FullName, StringComparison.Ordinal);
		}
		return new TokenBindingScope(target, bindings);
	}

	static IMDTokenProvider ResolveToken(ModuleDef module,uint token){try{return module.ResolveToken(token)??(serializedTokenBindings is { } scope && ReferenceEquals(scope.Target,module) && scope.Bindings.TryGetValue(token,out var bound) ? bound : throw new Exception());}catch{throw Validation("token","Metadata token could not be resolved: 0x"+token.ToString("x8"));}}
	static uint ParseToken(string text){uint token;if(text.Length!=10||!text.StartsWith("0x",StringComparison.OrdinalIgnoreCase)||!uint.TryParse(text.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out token))throw Validation("token","Expected 0x followed by eight hex digits");return token;}
	static (MethodDef method,ParamDef? param,int index) ResolveParameter(ModuleDef module,JsonElement r,Dictionary<string,IMDTokenProvider> map){if(r.TryGetProperty("owner_method",out var owner)){var m=Ref<MethodDef>(module,owner,map);var i=(int)RequiredUInt(r,"parameter_index");if(i>=m.MethodSig.Params.Count)Invalid("parameter_index","Parameter index is outside signature");return(m,m.ParamDefs.FirstOrDefault(p=>p.Sequence==i+1),i);}var p=Ref<ParamDef>(module,r,map);if(p.Sequence==0)Invalid("parameter_target","Return ParamDef cannot be edited");var method=p.DeclaringMethod;return(method,p,p.Sequence-1);}

	// Sequence points register documents in the module PdbState; when a body
	// is undone those rows must not linger (the checkpoint image gate compares
	// byte-level state, and an unreferenced document row is a residue).
	static PdbDocument[] CapturePdbDocuments(ModuleDef module) =>
		module.PdbState == null ? Array.Empty<PdbDocument>() : module.PdbState.Documents.ToArray();
	static void ReleasePdbDocuments(ModuleDef module, PdbDocument[] added) {
		var state = module.PdbState;
		if (state == null || added.Length == 0) return;
		bool Referenced(PdbDocument document) {
			foreach (var type in module.GetTypes())
				foreach (var method in type.Methods) {
					if (!method.HasBody) continue;
					foreach (var instruction in method.Body.Instructions)
						if (ReferenceEquals(instruction.SequencePoint?.Document, document)) return true;
				}
			return false;
		}
		foreach (var document in added)
			if (!Referenced(document)) state.Remove(document);
	}

	static EditOperationOutcome Outcome(string kind,string? created,IMDTokenProvider target,string? before,string? after,Action undo,IReadOnlyList<Dictionary<string,object?>>? risks=null)=>new(){Kind=kind,CreatedObjectIds=created==null?Array.Empty<string>():new[]{created},Undo=undo,Target=Target(target),Before=Truncate(before),After=Truncate(after),Risks=risks??Array.Empty<Dictionary<string,object?>>()};
	static string Target(IMDTokenProvider value)=>value.MDToken.Raw==0?value.GetType().Name:"0x"+value.MDToken.Raw.ToString("x8");
	static string? Truncate(string? value)=>value==null?null:value.Length<=32?value:value.Substring(0,32);
	static Dictionary<string,object?> Risk(string kind,IMDTokenProvider value){var id="risk-"+kind+"-"+Target(value).Replace("0x",string.Empty);return new(){["risk_id"]=id.Length<=48?id:id.Substring(0,48),["kind"]=kind,["object"]=Truncate(Target(value)),["description"]=kind switch{"public_delete"=>"Delete a public metadata object","signature_change"=>"Change a public signature","visibility_change"=>"Change visibility flags","body_change"=>"Replace a method body","eh_change"=>"Change exception handling",_=>"Metadata change"},["confirmation_required"]=true};}

	static bool HasReference(ModuleDef module,IMDTokenProvider target) {
		foreach(var type in module.GetTypes()) {
			// Rows re-owned into the deleted-rows tombstone are bookkeeping with
			// their references already semantically gone; they must not block
			// further deletions (P03-CHANGE-002 v3 §2.6).
			if(EditDeletedRowsTombstone.IsTombstone(type))continue;
			if(target is TypeDef targetType) {
				if(Same(type.BaseType,target)||type.Interfaces.Any(x=>Same(x.Interface,target)))return true;
				if(type.Fields.Any(x=>ContainsType(x.FieldType,targetType)))return true;
				if(type.Properties.Any(x=>ContainsType(x.PropertySig?.RetType,targetType)||x.PropertySig?.Params.Any(p=>ContainsType(p,targetType))==true))return true;
				if(type.Events.Any(x=>Same(x.EventType,target)))return true;
			}
			foreach(var gp in type.GenericParameters)if(gp.GenericParamConstraints.Any(x=>Same(x.Constraint,target)))return true;
			if(CustomAttributeConstructorIs(type.CustomAttributes,target))return true;
			foreach(var field in type.Fields)if(CustomAttributeConstructorIs(field.CustomAttributes,target))return true;
			foreach(var property in type.Properties) {
				if(CustomAttributeConstructorIs(property.CustomAttributes,target))return true;
				if(target is MethodDef && (Same(property.GetMethod,target)||Same(property.SetMethod,target)||property.OtherMethods.Any(x=>Same(x,target))))return true;
			}
			foreach(var evt in type.Events) {
				if(CustomAttributeConstructorIs(evt.CustomAttributes,target))return true;
				if(target is MethodDef && (Same(evt.AddMethod,target)||Same(evt.RemoveMethod,target)||Same(evt.InvokeMethod,target)||evt.OtherMethods.Any(x=>Same(x,target))))return true;
			}
			foreach(var method in type.Methods) {
				if(CustomAttributeConstructorIs(method.CustomAttributes,target))return true;
				if(target is TypeDef tt && (ContainsType(method.MethodSig?.RetType,tt)||method.MethodSig?.Params.Any(p=>ContainsType(p,tt))==true))return true;
				foreach(var gp in method.GenericParameters)if(gp.GenericParamConstraints.Any(x=>Same(x.Constraint,target)))return true;
				foreach(var parameter in method.ParamDefs)if(CustomAttributeConstructorIs(parameter.CustomAttributes,target))return true;
				foreach(var ov in method.Overrides)if(Same(ov.MethodBody,target)||Same(ov.MethodDeclaration,target))return true;
				if(method.HasBody&&method.Body.Instructions.Any(i=>Same(i.Operand as IMDTokenProvider,target)))return true;
			}
		}
		return false;
	}
	static bool HasAttachment(ModuleDef module,IMDTokenProvider target) {
		if(target is TypeDef t)return t.CustomAttributes.Count!=0||t.Interfaces.Count!=0||t.DeclSecurities.Count!=0;
		if(target is MethodDef m)return m.CustomAttributes.Count!=0||m.ImplMap!=null||m.DeclSecurities.Count!=0||m.Overrides.Count!=0||
			m.DeclaringType.Properties.Any(p=>Same(p.GetMethod,m)||Same(p.SetMethod,m)||p.OtherMethods.Any(x=>Same(x,m)))||
			m.DeclaringType.Events.Any(e=>Same(e.AddMethod,m)||Same(e.RemoveMethod,m)||Same(e.InvokeMethod,m)||e.OtherMethods.Any(x=>Same(x,m)));
		if(target is FieldDef f)return f.CustomAttributes.Count!=0||f.MarshalType!=null||f.RVA!=0||f.FieldOffset!=null||f.InitialValue!=null&&f.InitialValue.Length!=0;
		// Removing the Property/Event row is intentionally allowed to detach its
		// MethodSemantics rows while preserving the accessor MethodDefs.  Attributes
		// remain unsupported attachments and therefore still block removal.
		if(target is PropertyDef p)return p.CustomAttributes.Count!=0;
		if(target is EventDef e)return e.CustomAttributes.Count!=0;
		if(target is ParamDef pd)return pd.CustomAttributes.Count!=0||pd.MarshalType!=null||pd.Constant!=null;
		if(target is GenericParam gp)return gp.CustomAttributes.Count!=0||gp.GenericParamConstraints.Count!=0;
		return false;
	}
	static bool CustomAttributeConstructorIs(CustomAttributeCollection attributes,IMDTokenProvider target)=>attributes.Any(a=>Same(a.Constructor as IMDTokenProvider,target));
	// A zero RID is unassigned even when its table prefix makes Raw nonzero.
	// Distinct newly-created definitions in the same table are not aliases.
	static bool Same(IMDTokenProvider? candidate,IMDTokenProvider target)=>ReferenceEquals(candidate,target)||(candidate!=null&&candidate.Rid!=0&&target.Rid!=0&&candidate.MDToken.Raw==target.MDToken.Raw);
	static bool ContainsType(TypeSig? signature,TypeDef target){if(signature==null)return false;for(var current=signature;current!=null;current=current.Next){if(current.ToTypeDefOrRef() is IMDTokenProvider provider&&Same(provider,target))return true;if(string.Equals(current.FullName,target.FullName,StringComparison.Ordinal))return true;}return false;}
	static bool HasParameterReference(MethodDef m,int index)=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is Parameter p&&p.Index==index);
	static bool IsGenericUsed(ModuleDef module,GenericParam gp) {
		if (gp.Owner is MethodDef method)
			return MethodUsesGeneric(method, gp);
		if (gp.Owner is not TypeDef owner)
			return false;
		return TypeAndNested(owner).Any(type =>
			ContainsGeneric(type.BaseType?.ToTypeSig(), gp) ||
			type.Interfaces.Any(i => ContainsGeneric(i.Interface?.ToTypeSig(), gp)) ||
			type.Fields.Any(f => ContainsGeneric(f.FieldType, gp)) ||
			type.Properties.Any(p => ContainsGeneric(p.PropertySig?.RetType, gp) || p.PropertySig?.Params.Any(x => ContainsGeneric(x, gp)) == true) ||
			type.Events.Any(e => ContainsGeneric(e.EventType?.ToTypeSig(), gp)) ||
			type.Methods.Any(m => MethodUsesGeneric(m, gp)) ||
			type.GenericParameters.Any(x => !ReferenceEquals(x, gp) && ConstraintsUseGeneric(x, gp)));
	}
	static IEnumerable<TypeDef> TypeAndNested(TypeDef root) {
		yield return root;
		foreach (var nested in root.NestedTypes)
			foreach (var value in TypeAndNested(nested))
				yield return value;
	}
	static bool MethodUsesGeneric(MethodDef method, GenericParam target) {
		if (ContainsGeneric(method.MethodSig?.RetType, target) || method.MethodSig?.Params.Any(x => ContainsGeneric(x, target)) == true ||
			method.MethodSig?.ParamsAfterSentinel?.Any(x => ContainsGeneric(x, target)) == true)
			return true;
		if (method.GenericParameters.Any(x => !ReferenceEquals(x, target) && ConstraintsUseGeneric(x, target)))
			return true;
		if (method.HasBody && (method.Body.Variables.Any(v => ContainsGeneric(v.Type, target)) ||
			method.Body.Instructions.Any(i => OperandUsesGeneric(i.Operand, target))))
			return true;
		return false;
	}
	static bool ConstraintsUseGeneric(GenericParam parameter, GenericParam target) =>
		parameter.GenericParamConstraints.Any(x => ContainsGeneric(x.Constraint?.ToTypeSig(), target));
	static bool OperandUsesGeneric(object? operand, GenericParam target) {
		if (operand is TypeSig sig)
			return ContainsGeneric(sig, target);
		if (operand is TypeSpec spec)
			return ContainsGeneric(spec.TypeSig, target);
		if (operand is MethodSpec methodSpec)
			return methodSpec.GenericInstMethodSig?.GenericArguments.Any(x => ContainsGeneric(x, target)) == true;
		return false;
	}
	static bool ContainsGeneric(TypeSig? signature, GenericParam target) {
		if (signature == null)
			return false;
		if (signature is GenericSig generic) {
			var correctKind = target.Owner is MethodDef ? generic.IsMethodVar : generic.IsTypeVar;
			if (correctKind && generic.Number == target.Number &&
				(!generic.HasOwner || ReferenceEquals(generic.GenericParam, target) ||
				 (target.Owner is MethodDef targetMethod && ReferenceEquals(generic.OwnerMethod, targetMethod)) ||
				 (target.Owner is TypeDef targetType && ReferenceEquals(generic.OwnerType, targetType))))
				return true;
		}
		if (signature is GenericInstSig instance && instance.GenericArguments.Any(x => ContainsGeneric(x, target)))
			return true;
		if (signature is FnPtrSig function && function.Signature is MethodSig fnMethod &&
			(ContainsGeneric(fnMethod.RetType, target) || fnMethod.Params.Any(x => ContainsGeneric(x, target)) ||
			 fnMethod.ParamsAfterSentinel?.Any(x => ContainsGeneric(x, target)) == true))
			return true;
		return ContainsGeneric(signature.Next, target);
	}
	static bool IsPublic(TypeAttributes a)=>(a&TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic;
	static bool IsPublic(MethodAttributes a)=>(a&MethodAttributes.MemberAccessMask)==MethodAttributes.Public;
	static bool IsPublic(FieldAttributes a)=>(a&FieldAttributes.FieldAccessMask)==FieldAttributes.Public;
	static bool IsReferenceType(TypeSig sig)=>sig.RemovePinnedAndModifiers().ElementType is ElementType.Class or ElementType.Object or ElementType.String or ElementType.SZArray or ElementType.Array;
	static void RemoveMapValue(Dictionary<string,IMDTokenProvider> map,IMDTokenProvider value){foreach(var k in map.Where(p=>ReferenceEquals(p.Value,value)).Select(p=>p.Key).ToList())map.Remove(k);}
	static string ObjectId(int index,int sub)=>"obj-"+index.ToString("D3")+"-"+sub.ToString("D2");
	static void RequireRemoveMode(JsonElement op){if(RequiredString(op,"remove_mode")!="reject_if_referenced")Invalid("remove_mode","Only reject_if_referenced is supported");}
	// P04 IMP-003: whole-list method override mapping. Both sides must be
	// virtual methods of in-module types; declarations resolve by owner type,
	// name and parameter shape (FindMethod semantics).
	static void ApplyOverrides(ModuleDef module, MethodDef body, JsonElement value, Dictionary<string, IMDTokenProvider> map) {
		if (value.ValueKind == JsonValueKind.Null) { body.Overrides.Clear(); return; }
		if (!body.IsVirtual) Invalid("overrides", "override mapping requires a virtual method body");
		var rows = new List<MethodOverride>();
		foreach (var row in value.EnumerateArray()) {
			var declarationElement = row.GetProperty("declaration");
			IMethodDefOrRef declaration;
			if (declarationElement.ValueKind == JsonValueKind.Object
				&& (declarationElement.TryGetProperty("token", out _) || declarationElement.TryGetProperty("object_id", out _))) {
				declaration = ResolveReferenceRow(module, map, declarationElement) as IMethodDefOrRef
					?? throw Validation("overrides.declaration", "override declaration must resolve to a method reference");
			}
			else declaration = ResolveMethodReference(module, declarationElement);
			if (!IsVirtualMethod(declaration)) Invalid("overrides.declaration", "override declaration must be virtual");
			if (ReferenceEquals(declaration, body)) Invalid("overrides.declaration", "a method cannot override itself");
			if (rows.Any(x => SameMethodDefOrRef(x.MethodDeclaration, declaration))) Invalid("overrides", "duplicate override declaration");
			var methodRef = row.TryGetProperty("method", out var methodValue) ? ResolveMethodReference(module, methodValue) : body;
			if (!ReferenceEquals(methodRef, body)) Invalid("overrides.method", "override rows must map the updated method");
			rows.Add(new MethodOverride(body, declaration));
		}
		body.Overrides.Clear();
		foreach (var row in rows) body.Overrides.Add(row);
	}

	static bool IsVirtualMethod(IMethodDefOrRef method) => method switch {
		MethodDef definition => definition.IsVirtual,
		MemberRef reference => reference.ResolveMethodDef()?.IsVirtual ?? true,
		_ => true,
	};

	static bool SameMethodDefOrRef(IMethodDefOrRef left, IMethodDefOrRef right) => ReferenceEquals(left, right)
		|| (left is MemberRef memberLeft && right is MemberRef memberRight && SameMethodRow(memberLeft, memberRight))
		|| (left is MethodDef defLeft && right is MethodDef defRight && ReferenceEquals(defLeft, defRight));

	static bool IsSystemType(TypeSig? type) => type is TypeDefOrRefSig reference
		&& string.Equals(reference.TypeDefOrRef?.FullName, "System.Type", StringComparison.Ordinal);

	static void RequireSystemType(TypeSig type, string location) {
		if (!IsSystemType(type)) Invalid(location, "structured type values are only supported for System.Type parameters");
	}

	static void RequireSystemTypeArray(TypeSig type, string location) {
		if (type is SZArraySig array && IsSystemType(array.Next)) return;
		Invalid(location, "structured type arrays are only supported for System.Type[] parameters");
	}
	static MethodDef ResolveMethodReference(ModuleDef module, JsonElement reference) {
		var ownerRef = new EditTypeSigParser(module).Parse(RequiredString(reference, "owner_type")).ToTypeDefOrRef();
		if (ownerRef is not TypeDef ownerType) { Invalid("owner_type", "override owner_type must resolve to a type in the current module"); throw new InvalidOperationException(); }
		var parameterTypes = reference.TryGetProperty("parameter_types", out var parameters)
			? parameters.EnumerateArray().Select(x => new EditTypeSigParser(module, ownerType.GenericParameters.Count).Parse(x.GetString()!)).ToArray() : Array.Empty<TypeSig>();
		var candidates = ownerType.FindMethods(RequiredString(reference, "name"));
		foreach (var candidate in candidates) {
			if (candidate.MethodSig.Params.Count != parameterTypes.Length) continue;
			var match = true;
			for (int i = 0; i < parameterTypes.Length; i++)
				if (!string.Equals(candidate.MethodSig.Params[i].FullName, parameterTypes[i].FullName, StringComparison.Ordinal)) { match = false; break; }
			if (match) return candidate;
		}
		Invalid("overrides", "override method reference did not resolve to a unique method");
		throw new InvalidOperationException();
	}

	// P04 IMP-001: custom attributes on every definition kind plus the assembly.
	// T004: token targets address module rows; object_id targets address rows an
	// earlier operation of the same sequence created (state-machine kickoffs and
	// their generated subtrees are created and attributed in one import).
	static IHasCustomAttribute ResolveAttributeTarget(ModuleDef module, JsonElement reference, Dictionary<string, IMDTokenProvider> map) {
		if (reference.TryGetProperty("scope", out var scope) && scope.GetString() == "assembly") {
			if (module.Assembly == null) { Invalid("target", "module has no assembly"); throw new InvalidOperationException(); }
			return module.Assembly;
		}
		if (!reference.TryGetProperty("token", out _))
			return ResolveReferenceRow(module, map, reference) as IHasCustomAttribute
				?? throw Validation("target", "attribute target must be a definition that carries attributes");
		var resolved = ResolveToken(module, ParseToken(RequiredString(reference, "token")));
		if (resolved is not IHasCustomAttribute attributeTarget) { Invalid("target", "attribute target must be a definition that carries attributes"); throw new InvalidOperationException(); }
		return attributeTarget;
	}
	static ICustomAttributeType ResolveAttributeConstructor(ModuleDef module, JsonElement reference) {
		var attributeTypeRef = new EditTypeSigParser(module).Parse(RequiredString(reference, "attribute_type")).ToTypeDefOrRef();
		if (attributeTypeRef == null) { Invalid("constructor.attribute_type", "attribute type must resolve to a type"); throw new InvalidOperationException(); }
		var parameterSigs = reference.TryGetProperty("parameter_types", out var parameters)
			? parameters.EnumerateArray().Select(x => new EditTypeSigParser(module).Parse(x.GetString()!)).ToArray() : Array.Empty<TypeSig>();
		var parameterTypes = reference.TryGetProperty("parameter_types", out var parameterTexts)
			? parameterTexts.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
		// Bind to the real .ctor row whenever the type resolves (in-module or
		// through the module's assembly resolver, which the harness/runtimes
		// provide for corlib); only unresolved references synthesize a MemberRef
		// with the declared shape so the writer can still emit the row.
		var resolvedType = attributeTypeRef switch {
			TypeDef definition => definition,
			TypeRef typeReference => typeReference.ResolveTypeDef(),
			_ => null,
		};
		if (resolvedType != null) {
			foreach (var constructor in resolvedType.Methods.Where(x => x.IsConstructor)) {
				if (constructor.MethodSig.Params.Count != parameterSigs.Length) continue;
				var match = true;
				for (int i = 0; i < parameterSigs.Length; i++)
					if (!string.Equals(constructor.MethodSig.Params[i].TypeName, parameterTypes[i], StringComparison.Ordinal)
						&& !string.Equals(constructor.MethodSig.Params[i].FullName, parameterTypes[i], StringComparison.Ordinal)) { match = false; break; }
				if (!match) continue;
				// dnSpy's module context resolves corlib attribute types to MethodDefs
				// of ANOTHER module; a CustomAttribute ctor bound to a foreign
				// MethodDef writes an invalid row and reloads empty. Bind through a
				// same-module MemberRef in that case (dnlib CustomAttribute contract).
				if (!ReferenceEquals(constructor.Module, module))
					// The foreign MethodSig still contains foreign TypeDef rows (e.g.
					// System.Type in corlib). Reusing it writes invalid local tokens.
					// Bind the already validated declared parameters in this module.
					return new MemberRefUser(module, constructor.Name,
						MethodSig.CreateInstance(module.CorLibTypes.Void, parameterSigs), attributeTypeRef);
				return constructor;
			}
			Invalid("constructor", "attribute constructor with the given parameter shape was not found");
			throw new InvalidOperationException();
		}
		var signature = MethodSig.CreateInstance(module.CorLibTypes.Void, parameterSigs);
		return new MemberRefUser(module, ".ctor", signature, attributeTypeRef);
	}
	// Synthesized MemberRefs are per-call instances, so constructor identity
	// compares declaring type plus parameter shape, not references.
	static bool SameAttributeConstructor(ICustomAttributeType? left, ICustomAttributeType? right) {
		if (ReferenceEquals(left, right)) return true;
		if (left is not IMethod leftMethod || right is not IMethod rightMethod) return false;
		if (!string.Equals(leftMethod.DeclaringType?.FullName, rightMethod.DeclaringType?.FullName, StringComparison.Ordinal)) return false;
		if (leftMethod.MethodSig.Params.Count != rightMethod.MethodSig.Params.Count) return false;
		for (int i = 0; i < leftMethod.MethodSig.Params.Count; i++)
			if (!string.Equals(leftMethod.MethodSig.Params[i].FullName, rightMethod.MethodSig.Params[i].FullName, StringComparison.Ordinal)) return false;
		return true;
	}
	static EditOperationOutcome AttributeAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var target = ResolveAttributeTarget(module, op.GetProperty("target"), map);
		var constructor = ResolveAttributeConstructor(module, op.GetProperty("constructor"));
		var parameterTypes = constructor is MethodDef ctorDef ? ctorDef.MethodSig.Params : ((MemberRef)constructor).MethodSig.Params;
		var fixedArguments = op.TryGetProperty("fixed_arguments", out var fixedValues)
			? fixedValues.EnumerateArray().Select((value, i) => new CAArgument(parameterTypes[i], CaValue(parameterTypes[i], value, "fixed_arguments", module, map))).ToArray() : Array.Empty<CAArgument>();
		if (fixedArguments.Length != parameterTypes.Count) Invalid("fixed_arguments", "fixed_arguments must match the constructor parameter count");
		var namedArguments = op.TryGetProperty("named_arguments", out var namedValues)
			? namedValues.EnumerateArray().Select(value => NamedArgument(value, module, map)).ToList() : new List<CANamedArgument>();
		if (!AllowsMultiple((constructor as MethodDef)?.DeclaringType) && target.CustomAttributes.Any(x => SameAttributeConstructor(x.Constructor, constructor)))
			Invalid("target", "the attribute type does not allow multiple instances on one target");
		var attribute = new CustomAttribute(constructor, fixedArguments, namedArguments);
		target.CustomAttributes.Add(attribute);
		var index = AttributeIndex(target, constructor, attribute);
		return Outcome("attribute_add", null, target, null, target is TypeDef t ? t.FullName : constructor.DeclaringType?.FullName,
			() => { var rows = target.CustomAttributes.Where(x => x.Constructor == constructor).ToList(); if (index < rows.Count) target.CustomAttributes.Remove(rows[index]); },
			new[] { Risk("attribute_change", target is IMDTokenProvider provider ? provider : constructor) });
	}
	static EditOperationOutcome AttributeRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var target = ResolveAttributeTarget(module, op.GetProperty("target"), map);
		var constructor = ResolveAttributeConstructor(module, op.GetProperty("match").GetProperty("constructor"));
		var rows = target.CustomAttributes.Where(x => SameAttributeConstructor(x.Constructor, constructor)).ToList();
		var index = op.GetProperty("match").TryGetProperty("index", out var indexValue) ? (int)indexValue.GetUInt32() : 0;
		if (index >= rows.Count) Invalid("match.index", "attribute instance index is out of range");
		var row = rows[index];
		target.CustomAttributes.Remove(row);
		return Outcome("attribute_remove", null, target, null, target is TypeDef t ? t.FullName : constructor.DeclaringType?.FullName,
			() => target.CustomAttributes.Add(row), new[] { Risk("attribute_change", target is IMDTokenProvider provider ? provider : constructor) });
	}
	static int AttributeIndex(IHasCustomAttribute target, ICustomAttributeType constructor, CustomAttribute added)
		=> target.CustomAttributes.Where(x => x.Constructor == constructor).ToList().IndexOf(added);
	static bool AllowsMultiple(TypeDef? attributeType) {
		var usage = attributeType?.CustomAttributes.FirstOrDefault(x => x.TypeFullName == "System.AttributeUsageAttribute");
		if (usage == null) return false;
		foreach (var named in usage.NamedArguments)
			if (named.Name == "AllowMultiple" && named.Argument.Value is bool allow) return allow;
		return false;
	}
	static CANamedArgument NamedArgument(JsonElement value, ModuleDef module, Dictionary<string, IMDTokenProvider> map) {
		var kind = RequiredString(value, "kind");
		if (kind is not ("field" or "property")) Invalid("named_arguments.kind", "kind must be field or property");
		var type = new EditTypeSigParser(module).Parse(RequiredString(value, "type"));
		var argument = new CAArgument(type, CaValue(type, value.GetProperty("value"), "named_arguments.value", module, map));
		return new CANamedArgument(kind == "field", type, RequiredString(value, "name"), argument);
	}
	static object CaValue(TypeSig type, JsonElement value, string location, ModuleDef module, Dictionary<string, IMDTokenProvider> map) {
		if (value.ValueKind == JsonValueKind.Null) return null!;
		if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out var kindElement)
			&& kindElement.ValueKind == JsonValueKind.String && kindElement.GetString() == "type") {
			RequireSystemType(type, location);
			if (!value.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.Object)
				Invalid(location, "a structured type value requires the type node");
			return RestoreTypeNode(typeNode, module, map);
		}
		if (value.ValueKind == JsonValueKind.Array) {
			RequireSystemTypeArray(type, location);
			return value.EnumerateArray().Select(entry => {
				if (entry.ValueKind != JsonValueKind.Object) Invalid(location, "every structured type array entry must be {kind:type,type:...}");
				var entryKind = entry.GetProperty("kind").GetString();
				if (entryKind != "type") Invalid(location, "every structured type array entry must be {kind:type,type:...}");
				var entryNode = entry.GetProperty("type");
				return (TypeSig)RestoreTypeNode(entryNode, module, map);
			}).ToArray();
		}
		switch (type.ElementType) {
			case ElementType.Boolean: return value.GetBoolean();
			case ElementType.Char: return (char)value.GetUInt16();
			case ElementType.I1: return (sbyte)value.GetSByte();
			case ElementType.U1: return value.GetByte();
			case ElementType.I2: return value.GetInt16();
			case ElementType.U2: return value.GetUInt16();
			case ElementType.I4: return value.GetInt32();
			case ElementType.U4: return value.GetUInt32();
			case ElementType.I8: return value.GetInt64();
			case ElementType.U8: return value.GetUInt64();
			case ElementType.R4: return value.GetSingle();
			case ElementType.R8: return value.GetDouble();
			case ElementType.String: return value.GetString()!;
			default: Invalid(location, "attribute argument type must be a primitive, string or null in the P04 domain"); throw new InvalidOperationException();
		}
	}

	// P04 IMP-006: P/Invoke binding. dnlib 4.5.0 flag set (AUD-003): charset,
	// no_mangle, supports_last_error, calling_convention; exact-import and
	// best-fit flags do not exist in dnlib and are excluded by the schema.
	static void ApplyPInvoke(ModuleDef module, MethodDef method, JsonElement value) {
		if (value.ValueKind == JsonValueKind.Null) { method.ImplMap = null; method.IsPinvokeImpl = false; return; }
		if (!method.IsStatic || method.Body != null)
			Invalid("pinvoke", "pinvoke requires a static extern method");
		var moduleName = RequiredString(value, "module_name");
		var entryName = value.TryGetProperty("entry_name", out var entry) && entry.ValueKind == JsonValueKind.String ? entry.GetString()! : method.Name.String;
		var charset = value.TryGetProperty("charset", out var charsetValue) ? charsetValue.GetString()! : "none";
		var attributes = charset switch {
			"none" => PInvokeAttributes.CharSetNotSpec, "ansi" => PInvokeAttributes.CharSetAnsi,
			"unicode" => PInvokeAttributes.CharSetUnicode, "auto" => PInvokeAttributes.CharSetAuto,
			_ => throw (ArgumentException)InvalidArgument("pinvoke.charset", "charset must be none, ansi, unicode or auto"),
		};
		if (value.TryGetProperty("no_mangle", out var noMangle) && noMangle.GetBoolean()) attributes |= PInvokeAttributes.NoMangle;
		if (value.TryGetProperty("last_error", out var lastError) && lastError.GetBoolean()) attributes |= PInvokeAttributes.SupportsLastError;
		if (value.TryGetProperty("calling_convention", out var callConv) && callConv.GetString() is { } convention && convention != "winapi")
			attributes |= convention switch {
				"cdecl" => PInvokeAttributes.CallConvCdecl, "stdcall" => PInvokeAttributes.CallConvStdcall,
				"thiscall" => PInvokeAttributes.CallConvThiscall, "fastcall" => PInvokeAttributes.CallConvFastcall,
				_ => throw (ArgumentException)InvalidArgument("pinvoke.calling_convention", "calling_convention must be winapi, cdecl, stdcall, thiscall or fastcall"),
			};
		var moduleRef = module.GetModuleRefs().FirstOrDefault(x => string.Equals(x.Name, moduleName, StringComparison.Ordinal))
			?? new ModuleRefUser(module, moduleName);
		method.ImplMap = new ImplMapUser(moduleRef, entryName, attributes);
		method.IsPinvokeImpl = true;
	}
	static ArgumentException InvalidArgument(string location, string message) => new(message, location);

	// P04 IMP-007: security declarations on assembly/type/method. The XML path is
	// the round-1 spike fact: SecurityAttribute.CreateFromXml -> DeclSecurityUser.
	static readonly Dictionary<string, SecurityAction> SecurityActions = new(StringComparer.Ordinal) {
		["deny"] = SecurityAction.Deny, ["permit_only"] = SecurityAction.PermitOnly,
		["request_minimum"] = SecurityAction.RequestMinimum, ["request_optional"] = SecurityAction.RequestOptional,
		["request_refuse"] = SecurityAction.RequestRefuse, ["assert"] = SecurityAction.Assert,
		["link_demand"] = SecurityAction.LinktimeCheck, ["inherit_demand"] = SecurityAction.InheritDemand,
		["demand"] = SecurityAction.Demand,
	};
	static IMDTokenProvider SecurityProvider(ModuleDef module, JsonElement reference) {
		if (reference.TryGetProperty("scope", out var scope) && scope.GetString() == "assembly") {
			if (module.Assembly == null) { Invalid("parent", "module has no assembly"); throw new InvalidOperationException(); }
			return module.Assembly;
		}
		return ResolveToken(module, ParseToken(RequiredString(reference, "token")));
	}
	static IList<DeclSecurity> SecurityRows(ModuleDef module, JsonElement reference) {
		if (reference.TryGetProperty("scope", out var scope) && scope.GetString() == "assembly") {
			if (module.Assembly == null) { Invalid("parent", "module has no assembly"); throw new InvalidOperationException(); }
			return module.Assembly.DeclSecurities;
		}
		var resolved = ResolveToken(module, ParseToken(RequiredString(reference, "token")));
		if (resolved is TypeDef type) return type.DeclSecurities;
		if (resolved is MethodDef method) return method.DeclSecurities;
		Invalid("parent", "security parent must be assembly, type or method");
		throw new InvalidOperationException();
	}
	static EditOperationOutcome SecurityAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var provider = SecurityProvider(module, op.GetProperty("parent"));
		var rows = SecurityRows(module, op.GetProperty("parent"));
		var actionText = RequiredString(op, "action");
		if (!SecurityActions.TryGetValue(actionText, out var action))
			Invalid("action", "action must be one of " + string.Join(",", SecurityActions.Keys));
		var xml = RequiredString(op, "xml");
		var attribute = dnlib.DotNet.SecurityAttribute.CreateFromXml(module, xml)
			?? throw (ArgumentException)InvalidArgument("xml", "the permission-set XML did not parse into a security attribute");
		var row = new DeclSecurityUser(action, new[] { attribute });
		rows.Add(row);
		return Outcome("security_add", null, provider, null, actionText,
			() => rows.Remove(row), new[] { Risk("security_change", provider) });
	}
	static EditOperationOutcome SecurityRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var provider = SecurityProvider(module, op.GetProperty("parent"));
		var rows = SecurityRows(module, op.GetProperty("parent"));
		var actionText = RequiredString(op, "action");
		if (!SecurityActions.TryGetValue(actionText, out var action))
			Invalid("action", "action must be one of " + string.Join(",", SecurityActions.Keys));
		var index = op.TryGetProperty("index", out var indexValue) ? (int)indexValue.GetUInt32() : 0;
		var matching = rows.Where(x => x.Action == action).ToList();
		if (index >= matching.Count) Invalid("index", "security declaration index is out of range");
		var row = matching[index];
		rows.Remove(row);
		return Outcome("security_remove", null, provider, null, actionText,
			() => rows.Add(row), new[] { Risk("security_change", provider) });
	}

	static void RejectUnknownRawFields(JsonElement op){foreach(var name in new[]{"raw_metadata","pe_bytes","heap","rva","hex_patch"})if(op.TryGetProperty(name,out _))throw new ArgumentException("Unknown raw edit field: "+name,name);}
	static ulong OptionalAttributes(JsonElement op,string name,string domain,ulong fallback)=>op.TryGetProperty(name,out var v)?Attributes(v,domain):fallback;
	static ulong Attributes(JsonElement v,string domain){var value=v.GetUInt64();var mask=AttributeMasks[domain];if((value&~mask)!=0)throw new ArgumentException("Undefined "+domain+" attribute bits","attributes");return value;}
	static string RequiredString(JsonElement o,string n){if(!o.TryGetProperty(n,out var v)||v.ValueKind!=JsonValueKind.String||string.IsNullOrEmpty(v.GetString()))throw new ArgumentException(n+" is required",n);return v.GetString()!;}
	static string NonEmpty(JsonElement v,string n){if(v.ValueKind!=JsonValueKind.String||string.IsNullOrEmpty(v.GetString()))throw new ArgumentException(n+" must be non-empty",n);return v.GetString()!;}
	static string? OptionalString(JsonElement o,string n)=>o.TryGetProperty(n,out var v)?v.GetString():null;
	static bool RequiredBool(JsonElement o,string n){if(!o.TryGetProperty(n,out var v)||(v.ValueKind!=JsonValueKind.True&&v.ValueKind!=JsonValueKind.False))throw new ArgumentException(n+" is required",n);return v.GetBoolean();}
	static uint RequiredUInt(JsonElement o,string n){if(!o.TryGetProperty(n,out var v)||!v.TryGetUInt32(out var x))throw new ArgumentException(n+" must be uint",n);return x;}
	static Instruction BodyIndex(CilBody body,int index){if(index<0||index>=body.Instructions.Count)Invalid("instruction_index","Instruction index outside body");return body.Instructions[index];}
	static Instruction? IndexOrEnd(CilBody body,JsonElement row,string name){var i=(int)RequiredUInt(row,name);return i==body.Instructions.Count?null:BodyIndex(body,i);}
	static Instruction? NullableIndex(CilBody body,JsonElement row,string name){var v=row.GetProperty(name);return v.ValueKind==JsonValueKind.Null?null:BodyIndex(body,v.GetInt32());}
	static void Capacity(string location)=>throw new EditDomainException("EDIT_CAPACITY_EXCEEDED",new Dictionary<string,object?>{{"kind","capacity"},{"limit",location},{"current",1},{"maximum",0}});
	static EditDomainException Validation(string location,string message)=>new("EDIT_VALIDATION_FAILED",EditWorkspace.ValidationDetails("operation",location,message));
	static void Invalid(string location,string message)=>throw Validation(location,message);
}
