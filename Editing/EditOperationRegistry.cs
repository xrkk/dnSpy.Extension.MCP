using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
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

internal static class EditOperationRegistry {
	static readonly Dictionary<string, OpCode> OpCodesByName = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
		.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
	static readonly Dictionary<string, uint> AttributeMasks = new(StringComparer.Ordinal) {
		["type"] = 16219583, ["method"] = 65535, ["method_impl"] = 6143, ["field"] = 47095,
		["property"] = 5632, ["event"] = 1536, ["parameter"] = 12319, ["generic"] = 63,
	};

	public static EditOperationOutcome Apply(ModuleDef module, JsonElement operation,
		Dictionary<string, IMDTokenProvider> objects, int operationIndex) {
		if (operation.ValueKind != JsonValueKind.Object) Invalid("operation", "Operation must be an object");
		RejectUnknownRawFields(operation);
		var kind = RequiredString(operation, "kind");
		if (!EditWire.OperationKinds.Contains(kind, StringComparer.Ordinal)) Invalid("operation.kind", "Unknown operation kind");
		return kind switch {
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
			_ => throw new EditDomainException("EDIT_VALIDATION_FAILED"),
		};
	}

	static EditOperationOutcome TypeAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var name = RequiredString(op, "name"); var ns = OptionalString(op, "namespace") ?? string.Empty;
		var attrs = (TypeAttributes)OptionalAttributes(op, "attributes", "type", 0);
		var parser = new EditTypeSigParser(module);
		ITypeDefOrRef? baseType = null;
		if (op.TryGetProperty("base_type", out var b) && b.ValueKind != JsonValueKind.Null) baseType = parser.Parse(b.GetString()!, false).ToTypeDefOrRef();
		var type = new TypeDefUser(ns, name, baseType) { Attributes = attrs };
		var id = ObjectId(index, 0); var owner = OptionalRef<TypeDef>(module, op, "owner_type", map);
		if (owner == null) module.Types.Add(type); else owner.NestedTypes.Add(type);
		map[id] = type;
		return Outcome("type_add", id, type, null, type.FullName, () => { if (owner == null) module.Types.Remove(type); else owner.NestedTypes.Remove(type); map.Remove(id); });
	}

	static EditOperationOutcome TypeUpdate(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var t = Ref<TypeDef>(module, op.GetProperty("target"), map); var before = t.FullName;
		var oldName = t.Name; var oldNs = t.Namespace; var oldAttrs = t.Attributes; var oldBase = t.BaseType;
		if (op.TryGetProperty("name", out var n)) t.Name = NonEmpty(n, "name");
		if (op.TryGetProperty("namespace", out var ns)) t.Namespace = ns.GetString() ?? string.Empty;
		if (op.TryGetProperty("attributes", out var a)) t.Attributes = (TypeAttributes)Attributes(a, "type");
		if (op.TryGetProperty("base_type", out var b)) t.BaseType = b.ValueKind == JsonValueKind.Null ? null : new EditTypeSigParser(module, t.GenericParameters.Count).Parse(b.GetString()!).ToTypeDefOrRef();
		return Outcome("type_update", null, t, before, t.FullName, () => { t.Name = oldName; t.Namespace = oldNs; t.Attributes = oldAttrs; t.BaseType = oldBase; });
	}

	static EditOperationOutcome TypeRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		RequireRemoveMode(op); var t = Ref<TypeDef>(module, op.GetProperty("target"), map);
		if (HasReference(module, t) || HasAttachment(module,t)) Invalid("operation.target", "Type is referenced or attached");
		var owner = t.DeclaringType; var collection = owner == null ? module.Types : owner.NestedTypes; var index = collection.IndexOf(t); var before = t.FullName;
		collection.Remove(t); RemoveMapValue(map, t);
		var risks = IsPublic(t.Attributes) ? new[] { Risk("public_delete", t) } : Array.Empty<Dictionary<string, object?>>();
		return Outcome("type_remove", null, t, before, null, () => collection.Insert(index, t), risks);
	}

	static EditOperationOutcome MethodAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var owner = Ref<TypeDef>(module, op.GetProperty("owner_type"), map); var signature = op.GetProperty("signature");
		var gps = signature.GetProperty("generic_parameters").EnumerateArray().ToList();
		var parser = new EditTypeSigParser(module, owner.GenericParameters.Count, gps.Count);
		var ret = parser.Parse(RequiredString(signature, "return_type"), true);
		var parameters = signature.GetProperty("parameters").EnumerateArray().ToList();
		var paramTypes = parameters.Select(p => parser.Parse(RequiredString(p, "type"))).ToArray();
		var sig = MethodSig.CreateStatic(ret, paramTypes); if (RequiredBool(signature, "has_this")) sig.HasThis = true;
		sig.GenParamCount = (uint)gps.Count;
		if (gps.Count != 0) sig.CallingConvention |= CallingConvention.Generic;
		var method = new MethodDefUser(RequiredString(op, "name"), sig,
			(MethodImplAttributes)OptionalAttributes(op, "impl_attributes", "method_impl", 0),
			(MethodAttributes)OptionalAttributes(op, "attributes", "method", 128));
		for (int i = 0; i < gps.Count; i++) {
			var gp = new GenericParamUser((ushort)i, (GenericParamAttributes)OptionalAttributes(gps[i], "attributes", "generic", 0), RequiredString(gps[i], "name"));
			method.GenericParameters.Add(gp);
		}
		for (int i = 0; i < parameters.Count; i++) {
			var p = parameters[i];
			if (p.TryGetProperty("name", out var pn) && pn.ValueKind != JsonValueKind.Null)
				method.ParamDefs.Add(new ParamDefUser(pn.GetString(), (ushort)(i + 1), (ParamAttributes)OptionalAttributes(p, "attributes", "parameter", 0)));
		}
		if (op.TryGetProperty("body", out var body)) method.Body = BuildBody(module, method, body, map);
		owner.Methods.Add(method); var id = ObjectId(index, 0); map[id] = method;
		return Outcome("method_add", id, method, null, method.FullName, () => { owner.Methods.Remove(method); map.Remove(id); });
	}

	static EditOperationOutcome MethodUpdate(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		var m = Ref<MethodDef>(module, op.GetProperty("target"), map); var before = m.FullName;
		var oldName=m.Name; var oldAttrs=m.Attributes; var oldImpl=m.ImplAttributes; var oldRet=m.ReturnType; var oldHas=m.MethodSig.HasThis;
		if (op.TryGetProperty("name",out var n)) m.Name=NonEmpty(n,"name");
		if (op.TryGetProperty("attributes",out var a)) m.Attributes=(MethodAttributes)Attributes(a,"method");
		if (op.TryGetProperty("impl_attributes",out var ia)) m.ImplAttributes=(MethodImplAttributes)Attributes(ia,"method_impl");
		if (op.TryGetProperty("return_type",out var r)) m.MethodSig.RetType=new EditTypeSigParser(module,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count).Parse(r.GetString()!,true);
		if (op.TryGetProperty("has_this",out var h)) m.MethodSig.HasThis=h.GetBoolean();
		var risks = new List<Dictionary<string, object?>>(); if (m.ReturnType != oldRet || m.MethodSig.HasThis != oldHas) risks.Add(Risk("signature_change",m)); if (m.Attributes != oldAttrs) risks.Add(Risk("visibility_change",m));
		return Outcome("method_update",null,m,before,m.FullName,()=>{m.Name=oldName;m.Attributes=oldAttrs;m.ImplAttributes=oldImpl;m.MethodSig.RetType=oldRet;m.MethodSig.HasThis=oldHas;},risks);
	}

	static EditOperationOutcome MethodRemove(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map) {
		RequireRemoveMode(op); var m=Ref<MethodDef>(module,op.GetProperty("target"),map); if(HasReference(module,m)||HasAttachment(module,m)) Invalid("operation.target","Method is referenced or attached");
		var owner=m.DeclaringType; var index=owner.Methods.IndexOf(m); var before=m.FullName; owner.Methods.Remove(m); RemoveMapValue(map,m);
		var risks=IsPublic(m.Attributes)?new[]{Risk("public_delete",m)}:Array.Empty<Dictionary<string,object?>>();
		return Outcome("method_remove",null,m,before,null,()=>owner.Methods.Insert(index,m),risks);
	}

	static EditOperationOutcome FieldAdd(ModuleDef module, JsonElement op, Dictionary<string, IMDTokenProvider> map, int index) {
		var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map); var type=new EditTypeSigParser(module,owner.GenericParameters.Count).Parse(RequiredString(op,"field_type"));
		var f=new FieldDefUser(RequiredString(op,"name"),new FieldSig(type),(FieldAttributes)OptionalAttributes(op,"attributes","field",0));
		if(op.TryGetProperty("constant",out var c)) f.Constant=ParseConstant(c,type);
		owner.Fields.Add(f); var id=ObjectId(index,0); map[id]=f;
		return Outcome("field_add",id,f,null,f.FullName,()=>{owner.Fields.Remove(f);map.Remove(id);});
	}

	static EditOperationOutcome FieldUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){
		var f=Ref<FieldDef>(module,op.GetProperty("target"),map);var before=f.FullName;var oldName=f.Name;var oldType=f.FieldType;var oldAttrs=f.Attributes;var oldConst=f.Constant;
		if(op.TryGetProperty("name",out var n))f.Name=NonEmpty(n,"name"); if(op.TryGetProperty("field_type",out var t))f.FieldSig.Type=new EditTypeSigParser(module,f.DeclaringType.GenericParameters.Count).Parse(t.GetString()!);
		if(op.TryGetProperty("attributes",out var a))f.Attributes=(FieldAttributes)Attributes(a,"field"); if(op.TryGetProperty("clear_constant",out var clear)&&clear.GetBoolean())f.Constant=null; else if(op.TryGetProperty("constant",out var c))f.Constant=ParseConstant(c,f.FieldType);
		var risks=new List<Dictionary<string,object?>>();if(f.FieldType!=oldType)risks.Add(Risk("signature_change",f));if(f.Attributes!=oldAttrs)risks.Add(Risk("visibility_change",f));
		return Outcome("field_update",null,f,before,f.FullName,()=>{f.Name=oldName;f.FieldSig.Type=oldType;f.Attributes=oldAttrs;f.Constant=oldConst;},risks);
	}

	static EditOperationOutcome FieldRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var f=Ref<FieldDef>(module,op.GetProperty("target"),map);if(HasReference(module,f)||HasAttachment(module,f))Invalid("operation.target","Field is referenced or attached");var owner=f.DeclaringType;var i=owner.Fields.IndexOf(f);var before=f.FullName;owner.Fields.Remove(f);RemoveMapValue(map,f);var risks=IsPublic(f.Attributes)?new[]{Risk("public_delete",f)}:Array.Empty<Dictionary<string,object?>>();return Outcome("field_remove",null,f,before,null,()=>owner.Fields.Insert(i,f),risks);}

	static EditOperationOutcome PropertyAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){
		var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map);var parser=new EditTypeSigParser(module,owner.GenericParameters.Count);var ret=parser.Parse(RequiredString(op,"property_type"));var indices=op.TryGetProperty("index_parameter_types",out var arr)?arr.EnumerateArray().Select(x=>parser.Parse(x.GetString()!)).ToArray():Array.Empty<TypeSig>();
		var p=new PropertyDefUser(RequiredString(op,"name"),new PropertySig(true,ret,indices),(PropertyAttributes)OptionalAttributes(op,"attributes","property",0));
		if(op.TryGetProperty("getter",out var g)){if(g.ValueKind==JsonValueKind.Null)Invalid("getter","Explicit null is invalid for property_add");p.GetMethod=Ref<MethodDef>(module,g,map);}if(op.TryGetProperty("setter",out var s)){if(s.ValueKind==JsonValueKind.Null)Invalid("setter","Explicit null is invalid for property_add");p.SetMethod=Ref<MethodDef>(module,s,map);}
		owner.Properties.Add(p);var id=ObjectId(index,0);map[id]=p;return Outcome("property_add",id,p,null,p.FullName,()=>{owner.Properties.Remove(p);map.Remove(id);});}
	static EditOperationOutcome PropertyUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var p=Ref<PropertyDef>(module,op.GetProperty("target"),map);var before=p.FullName;var oldName=p.Name;var oldSig=p.PropertySig;var oldAttrs=p.Attributes;var oldGet=p.GetMethod;var oldSet=p.SetMethod;var parser=new EditTypeSigParser(module,p.DeclaringType.GenericParameters.Count);if(op.TryGetProperty("name",out var n))p.Name=NonEmpty(n,"name");var ret=op.TryGetProperty("property_type",out var pt)?parser.Parse(pt.GetString()!):p.PropertySig.RetType;var args=op.TryGetProperty("index_parameter_types",out var a)?a.EnumerateArray().Select(x=>parser.Parse(x.GetString()!)).ToArray():p.PropertySig.Params.ToArray();if(op.TryGetProperty("property_type",out _)||op.TryGetProperty("index_parameter_types",out _))p.PropertySig=new PropertySig(p.PropertySig.HasThis,ret,args);if(op.TryGetProperty("attributes",out var at))p.Attributes=(PropertyAttributes)Attributes(at,"property");if(op.TryGetProperty("getter",out var g))p.GetMethod=g.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,g,map);if(op.TryGetProperty("setter",out var s))p.SetMethod=s.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,s,map);var risks=new List<Dictionary<string,object?>>();if(p.PropertySig!=oldSig||p.GetMethod!=oldGet||p.SetMethod!=oldSet)risks.Add(Risk("signature_change",p));return Outcome("property_update",null,p,before,p.FullName,()=>{p.Name=oldName;p.PropertySig=oldSig;p.Attributes=oldAttrs;p.GetMethod=oldGet;p.SetMethod=oldSet;},risks);}
	static EditOperationOutcome PropertyRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var p=Ref<PropertyDef>(module,op.GetProperty("target"),map);if(HasAttachment(module,p))Invalid("operation.target","Property is referenced or attached");var owner=p.DeclaringType;var i=owner.Properties.IndexOf(p);var before=p.FullName;owner.Properties.Remove(p);RemoveMapValue(map,p);return Outcome("property_remove",null,p,before,null,()=>owner.Properties.Insert(i,p),new[]{Risk("public_delete",p)});}

	static EditOperationOutcome EventAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var owner=Ref<TypeDef>(module,op.GetProperty("owner_type"),map);var et=new EditTypeSigParser(module,owner.GenericParameters.Count).Parse(RequiredString(op,"event_type")).ToTypeDefOrRef()!;var e=new EventDefUser(RequiredString(op,"name"),et,(EventAttributes)OptionalAttributes(op,"attributes","event",0)){AddMethod=Ref<MethodDef>(module,op.GetProperty("add_method"),map),RemoveMethod=Ref<MethodDef>(module,op.GetProperty("remove_method"),map)};if(op.TryGetProperty("raise_method",out var r)){if(r.ValueKind==JsonValueKind.Null)Invalid("raise_method","Explicit null is invalid for event_add");e.InvokeMethod=Ref<MethodDef>(module,r,map);}owner.Events.Add(e);var id=ObjectId(index,0);map[id]=e;return Outcome("event_add",id,e,null,e.FullName,()=>{owner.Events.Remove(e);map.Remove(id);});}
	static EditOperationOutcome EventUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var e=Ref<EventDef>(module,op.GetProperty("target"),map);var before=e.FullName;var oldName=e.Name;var oldType=e.EventType;var oldAttrs=e.Attributes;var oldAdd=e.AddMethod;var oldRemove=e.RemoveMethod;var oldRaise=e.InvokeMethod;if(op.TryGetProperty("name",out var n))e.Name=NonEmpty(n,"name");if(op.TryGetProperty("event_type",out var t))e.EventType=new EditTypeSigParser(module,e.DeclaringType.GenericParameters.Count).Parse(t.GetString()!).ToTypeDefOrRef()!;if(op.TryGetProperty("attributes",out var a))e.Attributes=(EventAttributes)Attributes(a,"event");if(op.TryGetProperty("add_method",out var add))e.AddMethod=add.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,add,map);if(op.TryGetProperty("remove_method",out var rem))e.RemoveMethod=rem.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,rem,map);if(op.TryGetProperty("raise_method",out var raise))e.InvokeMethod=raise.ValueKind==JsonValueKind.Null?null:Ref<MethodDef>(module,raise,map);return Outcome("event_update",null,e,before,e.FullName,()=>{e.Name=oldName;e.EventType=oldType;e.Attributes=oldAttrs;e.AddMethod=oldAdd;e.RemoveMethod=oldRemove;e.InvokeMethod=oldRaise;},new[]{Risk("signature_change",e)});}
	static EditOperationOutcome EventRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var e=Ref<EventDef>(module,op.GetProperty("target"),map);if(HasAttachment(module,e))Invalid("operation.target","Event is referenced or attached");var owner=e.DeclaringType;var i=owner.Events.IndexOf(e);var before=e.FullName;owner.Events.Remove(e);RemoveMapValue(map,e);return Outcome("event_remove",null,e,before,null,()=>owner.Events.Insert(i,e),new[]{Risk("public_delete",e)});}

	static EditOperationOutcome ParameterAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var m=Ref<MethodDef>(module,op.GetProperty("owner_method"),map);var requested=(int)RequiredUInt(op,"parameter_index");if(requested!=m.MethodSig.Params.Count)Invalid("parameter_index","Only tail parameter insertion is supported");var type=new EditTypeSigParser(module,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count).Parse(RequiredString(op,"parameter_type"));m.MethodSig.Params.Add(type);var p=new ParamDefUser(RequiredString(op,"name"),(ushort)(requested+1),(ParamAttributes)OptionalAttributes(op,"attributes","parameter",0));m.ParamDefs.Add(p);var id=ObjectId(index,0);map[id]=p;return Outcome("parameter_add",id,p,null,p.Name,()=>{m.ParamDefs.Remove(p);m.MethodSig.Params.RemoveAt(m.MethodSig.Params.Count-1);map.Remove(id);},new[]{Risk("signature_change",m)});}
	static EditOperationOutcome ParameterUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var target=ResolveParameter(module,op.GetProperty("parameter_target"),map);var m=target.method;var p=target.param;var index=target.index;var oldName=p?.Name;var oldAttrs=p?.Attributes??0;var oldType=m.MethodSig.Params[index];var materialized=p==null;if(p==null){p=new ParamDefUser(null,(ushort)(index+1));m.ParamDefs.Add(p);}if(op.TryGetProperty("name",out var n))p.Name=n.ValueKind==JsonValueKind.Null?null:NonEmpty(n,"name");if(op.TryGetProperty("attributes",out var a))p.Attributes=(ParamAttributes)Attributes(a,"parameter");if(op.TryGetProperty("parameter_type",out var t))m.MethodSig.Params[index]=new EditTypeSigParser(module,m.DeclaringType.GenericParameters.Count,m.GenericParameters.Count).Parse(t.GetString()!);var current=p;return Outcome("parameter_update",null,current,"parameter:"+index,current.Name,()=>{m.MethodSig.Params[index]=oldType;if(materialized)m.ParamDefs.Remove(current);else{current.Name=oldName;current.Attributes=oldAttrs;}},new[]{Risk("signature_change",m)});}
	static EditOperationOutcome ParameterRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var target=ResolveParameter(module,op.GetProperty("parameter_target"),map);var m=target.method;if(target.index!=m.MethodSig.Params.Count-1)Invalid("parameter_target","Only tail parameter removal is supported");var p=target.param;if(HasParameterReference(m,target.index)||(p!=null&&HasAttachment(module,p)))Invalid("parameter_target","Parameter is referenced or attached");var oldType=m.MethodSig.Params[target.index];m.MethodSig.Params.RemoveAt(target.index);if(p!=null)m.ParamDefs.Remove(p);if(p!=null)RemoveMapValue(map,p);IMDTokenProvider removedTarget=p is null ? m : p;return Outcome("parameter_remove",null,removedTarget,"parameter:"+target.index,null,()=>{m.MethodSig.Params.Add(oldType);if(p!=null)m.ParamDefs.Add(p);},new[]{Risk("signature_change",m)});}

	static EditOperationOutcome GenericAdd(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map,int index){var owner=Ref<IMDTokenProvider>(module,op.GetProperty("owner"),map);IList<GenericParam> collection;if(owner is TypeDef t)collection=t.GenericParameters;else if(owner is MethodDef m)collection=m.GenericParameters;else throw Validation("owner","Generic owner must be type or method");var oldArity=owner is MethodDef oldMethod ? oldMethod.MethodSig.GenParamCount : 0;var oldCallingConvention=owner is MethodDef oldConventionMethod?oldConventionMethod.MethodSig.CallingConvention:0;var requested=(int)RequiredUInt(op,"generic_index");if(requested!=collection.Count)Invalid("generic_index","Only tail generic parameter insertion is supported");var gp=new GenericParamUser((ushort)requested,(GenericParamAttributes)OptionalAttributes(op,"attributes","generic",0),RequiredString(op,"name"));collection.Add(gp);if(owner is MethodDef method){method.MethodSig.GenParamCount=(uint)collection.Count;method.MethodSig.CallingConvention|=CallingConvention.Generic;}var id=ObjectId(index,0);map[id]=gp;return Outcome("generic_parameter_add",id,gp,null,gp.Name,()=>{collection.Remove(gp);if(owner is MethodDef mm){mm.MethodSig.GenParamCount=oldArity;mm.MethodSig.CallingConvention=oldCallingConvention;}map.Remove(id);},new[]{Risk("signature_change",owner)});}
	static EditOperationOutcome GenericUpdate(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var gp=Ref<GenericParam>(module,op.GetProperty("target"),map);var oldName=gp.Name;var oldFlags=gp.Flags;if(op.TryGetProperty("name",out var n))gp.Name=NonEmpty(n,"name");if(op.TryGetProperty("attributes",out var a))gp.Flags=(GenericParamAttributes)Attributes(a,"generic");return Outcome("generic_parameter_update",null,gp,oldName,gp.Name,()=>{gp.Name=oldName;gp.Flags=oldFlags;},new[]{Risk("signature_change",gp)});}
	static EditOperationOutcome GenericRemove(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){RequireRemoveMode(op);var gp=Ref<GenericParam>(module,op.GetProperty("target"),map);IList<GenericParam> col;MethodDef? method=null;if(gp.Owner is TypeDef t)col=t.GenericParameters;else if(gp.Owner is MethodDef m){method=m;col=m.GenericParameters;}else throw Validation("target","Generic parameter has no owner");if(gp.Number!=col.Count-1)Invalid("target","Only tail generic parameter removal is supported");if(IsGenericUsed(module,gp)||HasAttachment(module,gp))Invalid("target","Generic parameter is used or attached");var oldArity=method?.MethodSig.GenParamCount??0;var oldCallingConvention=method?.MethodSig.CallingConvention??0;var riskOwner=(IMDTokenProvider?)gp.Owner??gp;col.Remove(gp);if(method!=null){method.MethodSig.GenParamCount=(uint)col.Count;if(col.Count==0)method.MethodSig.CallingConvention&=~CallingConvention.Generic;}RemoveMapValue(map,gp);return Outcome("generic_parameter_remove",null,gp,gp.Name,null,()=>{col.Add(gp);if(method!=null){method.MethodSig.GenParamCount=oldArity;method.MethodSig.CallingConvention=oldCallingConvention;}},new[]{Risk("signature_change",riskOwner)});}

	static EditOperationOutcome BodyReplace(ModuleDef module,JsonElement op,Dictionary<string,IMDTokenProvider> map){var m=Ref<MethodDef>(module,op.GetProperty("target"),map);var old=m.Body;var body=BuildBody(module,m,op.GetProperty("body"),map);m.Body=body;var risks=new List<Dictionary<string,object?>> { Risk("body_change",m) };if((old?.ExceptionHandlers.Count??0)!=body.ExceptionHandlers.Count)risks.Add(Risk("eh_change",m));return Outcome("method_body_replace",null,m,old==null?null:"body", "body",()=>m.Body=old,risks);}

	static CilBody BuildBody(ModuleDef module,MethodDef method,JsonElement body,Dictionary<string,IMDTokenProvider> map){
		var insRows=body.GetProperty("instructions").EnumerateArray().ToList();var locals=body.GetProperty("locals").EnumerateArray().ToList();var ehs=body.GetProperty("exception_handlers").EnumerateArray().ToList();if(insRows.Count>EditWire.MaxBodyInstructions||locals.Count>EditWire.MaxBodyLocals||ehs.Count>EditWire.MaxBodyExceptionHandlers)Capacity("method_body");
		var result=new CilBody(RequiredBool(body,"init_locals"),new List<Instruction>(),new List<ExceptionHandler>(),new List<Local>()){
			MaxStack=(ushort)RequiredUInt(body,"max_stack"), KeepOldMaxStack=true, HeaderSize=12,
		};
		var parser=new EditTypeSigParser(module,method.DeclaringType?.GenericParameters.Count??0,method.GenericParameters.Count);foreach(var l in locals)result.Variables.Add(new Local(parser.Parse(RequiredString(l,"type")),l.GetProperty("name").ValueKind==JsonValueKind.Null?null:l.GetProperty("name").GetString()));
		foreach(var row in insRows){var name=RequiredString(row,"opcode");if(!OpCodesByName.TryGetValue(name,out var code))Invalid("body.instructions.opcode","Unknown opcode: "+name);result.Instructions.Add(new Instruction(code));}
		for(int i=0;i<insRows.Count;i++){var row=insRows[i];if(row.TryGetProperty("operand",out var operand)&&operand.ValueKind!=JsonValueKind.Null)result.Instructions[i].Operand=ParseOperand(module,method,result,operand,map);ValidateOperand(result.Instructions[i]);}
		foreach(var row in ehs){var kind=RequiredString(row,"kind");var eh=new ExceptionHandler(kind switch{"catch"=>ExceptionHandlerType.Catch,"finally"=>ExceptionHandlerType.Finally,"fault"=>ExceptionHandlerType.Fault,"filter"=>ExceptionHandlerType.Filter,_=>throw Validation("exception_handler","Unknown handler kind")}){TryStart=IndexOrEnd(result,row,"try_start"),TryEnd=IndexOrEnd(result,row,"try_end"),HandlerStart=IndexOrEnd(result,row,"handler_start"),HandlerEnd=IndexOrEnd(result,row,"handler_end"),FilterStart=NullableIndex(result,row,"filter_start")};if(row.GetProperty("catch_type").ValueKind!=JsonValueKind.Null)eh.CatchType=parser.Parse(row.GetProperty("catch_type").GetString()!).ToTypeDefOrRef();result.ExceptionHandlers.Add(eh);}
		return result;
	}

	static object ParseOperand(ModuleDef module,MethodDef method,CilBody body,JsonElement op,Dictionary<string,IMDTokenProvider> map){var kind=RequiredString(op,"kind");return kind switch{"i32"=>(int)op.GetProperty("value").GetInt64(),"i64"=>op.GetProperty("value").GetInt64(),"f32"=>(float)op.GetProperty("value").GetDouble(),"f64"=>op.GetProperty("value").GetDouble(),"string"=>op.GetProperty("value").GetString()??string.Empty,"token"=>ResolveToken(module,ParseToken(op.GetProperty("token").GetString()!)),"object"=>map.TryGetValue(RequiredString(op,"object_id"),out var x)?x:throw Validation("operand.object_id","Unknown object ID"),"label"=>BodyIndex(body,(int)RequiredUInt(op,"instruction_index")),"switch"=>op.GetProperty("instruction_indices").EnumerateArray().Select(x=>BodyIndex(body,x.GetInt32())).ToArray(),"local"=>LocalAt(body,(int)RequiredUInt(op,"local_index")),"arg"=>ArgumentAt(method,(int)RequiredUInt(op,"argument_index")),_=>throw Validation("operand.kind","Unknown operand kind")};}
	static Local LocalAt(CilBody body,int index){if(index<0||index>=body.Variables.Count)Invalid("operand.local_index","Local index is outside body variables");return body.Variables[index];}
	static Parameter ArgumentAt(MethodDef method,int index){if(index<0||index>=method.Parameters.Count)Invalid("operand.argument_index","Argument index is outside method parameters");return method.Parameters[index];}
	static void ValidateOperand(Instruction i){var o=i.Operand;bool valid=i.OpCode.OperandType switch{OperandType.InlineNone=>o==null,OperandType.ShortInlineI=>o is sbyte||o is byte||o is int,OperandType.InlineI=>o is int,OperandType.InlineI8=>o is long,OperandType.ShortInlineR=>o is float,OperandType.InlineR=>o is double,OperandType.InlineString=>o is string,OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget=>o is Instruction,OperandType.InlineSwitch=>o is Instruction[],OperandType.InlineVar or OperandType.ShortInlineVar=>o is Local||o is Parameter,OperandType.InlineField=>o is IField,OperandType.InlineMethod=>o is IMethod,OperandType.InlineType=>o is ITypeDefOrRef||o is TypeSig,OperandType.InlineTok=>o is ITokenOperand,OperandType.InlineSig=>o is StandAloneSig||o is CallingConventionSig,_=>false};if(!valid)Invalid("body.instructions.operand","Operand does not match opcode "+i.OpCode.Name);}

	static Constant ParseConstant(JsonElement c,TypeSig target){var kind=RequiredString(c,"kind");var v=c.GetProperty("value");object? value=kind switch{"null"=>null,"boolean"=>v.GetBoolean(),"char"=>(v.GetString()??throw Validation("constant","char missing"))[0],"string"=>v.GetString(),"r4"=>(float)v.GetDouble(),"r8"=>v.GetDouble(),"i1"=>(sbyte)v.GetInt64(),"u1"=>(byte)v.GetUInt64(),"i2"=>(short)v.GetInt64(),"u2"=>(ushort)v.GetUInt64(),"i4"=>(int)v.GetInt64(),"u4"=>(uint)v.GetUInt64(),"i8"=>v.GetInt64(),"u8"=>v.GetUInt64(),_=>throw Validation("constant.kind","Unknown constant kind")};if(value==null&&!IsReferenceType(target))Invalid("constant","null constant requires a reference type");return new ConstantUser(value);}

	static T Ref<T>(ModuleDef module,JsonElement reference,Dictionary<string,IMDTokenProvider> map) where T:class,IMDTokenProvider{IMDTokenProvider value;if(reference.TryGetProperty("token",out var token))value=ResolveToken(module,ParseToken(token.GetString()!));else if(reference.TryGetProperty("object_id",out var id)&&map.TryGetValue(id.GetString()!,out var found))value=found;else throw Validation("reference","Unknown token or object ID");if(value is T typed)return typed;throw Validation("reference","Reference has the wrong metadata kind");}
	static T? OptionalRef<T>(ModuleDef module,JsonElement op,string name,Dictionary<string,IMDTokenProvider> map) where T:class,IMDTokenProvider=>op.TryGetProperty(name,out var r)&&r.ValueKind!=JsonValueKind.Null?Ref<T>(module,r,map):null;
	static IMDTokenProvider ResolveToken(ModuleDef module,uint token){try{return module.ResolveToken(token)??throw new Exception();}catch{throw Validation("token","Metadata token could not be resolved: 0x"+token.ToString("x8"));}}
	static uint ParseToken(string text){uint token;if(text.Length!=10||!text.StartsWith("0x",StringComparison.OrdinalIgnoreCase)||!uint.TryParse(text.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out token))throw Validation("token","Expected 0x followed by eight hex digits");return token;}
	static (MethodDef method,ParamDef? param,int index) ResolveParameter(ModuleDef module,JsonElement r,Dictionary<string,IMDTokenProvider> map){if(r.TryGetProperty("owner_method",out var owner)){var m=Ref<MethodDef>(module,owner,map);var i=(int)RequiredUInt(r,"parameter_index");if(i>=m.MethodSig.Params.Count)Invalid("parameter_index","Parameter index is outside signature");return(m,m.ParamDefs.FirstOrDefault(p=>p.Sequence==i+1),i);}var p=Ref<ParamDef>(module,r,map);if(p.Sequence==0)Invalid("parameter_target","Return ParamDef cannot be edited");var method=p.DeclaringMethod;return(method,p,p.Sequence-1);}

	static EditOperationOutcome Outcome(string kind,string? created,IMDTokenProvider target,string? before,string? after,Action undo,IReadOnlyList<Dictionary<string,object?>>? risks=null)=>new(){Kind=kind,CreatedObjectIds=created==null?Array.Empty<string>():new[]{created},Undo=undo,Target=Target(target),Before=Truncate(before),After=Truncate(after),Risks=risks??Array.Empty<Dictionary<string,object?>>()};
	static string Target(IMDTokenProvider value)=>value.MDToken.Raw==0?value.GetType().Name:"0x"+value.MDToken.Raw.ToString("x8");
	static string? Truncate(string? value)=>value==null?null:value.Length<=32?value:value.Substring(0,32);
	static Dictionary<string,object?> Risk(string kind,IMDTokenProvider value){var id="risk-"+kind+"-"+Target(value).Replace("0x",string.Empty);return new(){["risk_id"]=id.Length<=48?id:id.Substring(0,48),["kind"]=kind,["object"]=Truncate(Target(value)),["description"]=kind switch{"public_delete"=>"Delete a public metadata object","signature_change"=>"Change a public signature","visibility_change"=>"Change visibility flags","body_change"=>"Replace a method body","eh_change"=>"Change exception handling",_=>"Metadata change"},["confirmation_required"]=true};}

	static bool HasReference(ModuleDef module,IMDTokenProvider target) {
		foreach(var type in module.GetTypes()) {
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
	static bool Same(IMDTokenProvider? candidate,IMDTokenProvider target)=>ReferenceEquals(candidate,target)||(candidate!=null&&candidate.MDToken.Raw!=0&&target.MDToken.Raw!=0&&candidate.MDToken.Raw==target.MDToken.Raw);
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
