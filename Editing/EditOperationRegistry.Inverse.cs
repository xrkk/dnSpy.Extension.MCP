using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

internal static partial class EditOperationRegistry {
	// Compile the package's baseline/parent/prefix inverse against a detached replay
	// graph. The resulting operation addresses the existing live object; it never
	// replaces ModuleDef.Types or the ModuleDefMD token caches.
	internal static Dictionary<string, object?> CompileInverse(ModuleDef before, JsonElement forward,
		Dictionary<string, IMDTokenProvider> objects) {
		var kind = RequiredString(forward, "kind");
		if (kind == "legacy_symbol_rename") return new() { ["legacy"] = EditLegacyRenameOperation.Reverse(forward) };
		var inverse = new Dictionary<string, object?> { ["kind"] = kind };
		if (forward.TryGetProperty("target", out var target)) inverse["target"] = target.Clone();
		switch (kind) {
		case "type_add":
		case "method_add":
		case "field_add":
		case "property_add":
		case "event_add": {
			var owner = kind == "type_add" ? OptionalRef<TypeDef>(before, forward, "owner_type", objects)
				: Ref<TypeDef>(before, forward.GetProperty("owner_type"), objects);
			var count = DefinitionChildren(before, owner, kind).Count;
			return new() { ["definition_tail_remove"] = new Dictionary<string, object?> {
				["add_kind"] = kind, ["owner_address"] = owner == null ? null : EditDefinitionAddress.Capture(before, owner),
				["index"] = count, ["name"] = RequiredString(forward, "name"),
			} };
		}
		case "type_update": {
			var value = Ref<TypeDef>(before, forward.GetProperty("target"), objects);
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("namespace", out _)) state["namespace_utf8"] = (byte[])value.Namespace.Data.Clone();
			if (forward.TryGetProperty("attributes", out _) || forward.TryGetProperty("layout", out _)) state["attributes"] = (uint)value.Attributes;
			if (forward.TryGetProperty("base_type", out _)) {
				if (value.BaseType == null) state["base_null"] = true;
				else state["base_signature"] = EditStructuredSignatureCodec.Capture(value.BaseType.ToTypeSig(), InverseBinder(before, objects));
			}
			if (forward.TryGetProperty("layout", out _)) {
				if (value.ClassLayout == null) state["layout_null"] = true;
				else state["layout"] = new Dictionary<string, object?> {
					["pack"] = value.ClassLayout.PackingSize, ["size"] = value.ClassLayout.ClassSize,
				};
			}
			return new() { ["type_state"] = new Dictionary<string, object?> {
				["type"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "method_update": {
			var value = Ref<MethodDef>(before, forward.GetProperty("target"), objects);
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("attributes", out _)) state["attributes"] = (uint)value.Attributes;
			if (forward.TryGetProperty("impl_attributes", out _)) state["impl_attributes"] = (uint)value.ImplAttributes;
			if (forward.TryGetProperty("return_type", out _))
				state["return_signature"] = EditStructuredSignatureCodec.Capture(value.ReturnType, InverseBinder(before, objects));
			if (forward.TryGetProperty("has_this", out _)) state["has_this"] = value.MethodSig.HasThis;
			if (forward.TryGetProperty("overrides", out _)) {
				// P04: whole-list override capture; both sides are in-module
				// method rows, so structured references round-trip exactly.
				state["overrides"] = value.Overrides.Select(x => (object)new Dictionary<string, object?> {
					["method"] = InverseReference(x.MethodBody, objects),
					["declaration"] = InverseReference((IMDTokenProvider)x.MethodDeclaration, objects),
				}).ToArray();
			}
			if (forward.TryGetProperty("pinvoke", out _)) {
				state["attributes"] = (uint)value.Attributes;  // the PInvokeImpl bit rides here
				if (value.ImplMap == null) state["pinvoke_null"] = true;
				else state["pinvoke"] = new Dictionary<string, object?> {
					["module_name"] = value.ImplMap.Module?.Name?.String, ["entry_name"] = value.ImplMap.Name?.String,
					["flags"] = (uint)value.ImplMap.Attributes,
				};
			}
			return new() { ["method_state"] = new Dictionary<string, object?> {
				["method"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "field_update": {
			var value = Ref<FieldDef>(before, forward.GetProperty("target"), objects);
			// Name text, signature text and JSON numbers all lose semantics; capture
			// the raw state instead of routing the inverse through the public schema.
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("attributes", out _) || forward.TryGetProperty("initial_data", out _))
				state["attributes"] = (uint)value.Attributes;
			if (forward.TryGetProperty("field_type", out _))
				state["field_signature"] = EditStructuredSignatureCodec.Capture(value.FieldSig, InverseBinder(before, objects));
			if (forward.TryGetProperty("constant", out _) || forward.TryGetProperty("clear_constant", out _)) {
				// EditWire drops JSON nulls, so a null constant needs its own marker.
				if (value.Constant == null) state["constant_null"] = true;
				else state["constant"] = InverseConstant(value.Constant);
			}
			if (forward.TryGetProperty("field_offset", out _)) {
				if (value.FieldOffset == null) state["field_offset_null"] = true;
				else state["field_offset"] = value.FieldOffset.Value;
			}
			if (forward.TryGetProperty("initial_data", out _)) {
				if (value.InitialValue == null || value.InitialValue.Length == 0) state["initial_data_null"] = true;
				else state["initial_data"] = Convert.ToBase64String(value.InitialValue);
			}
			if (forward.TryGetProperty("marshal", out _)) {
				var binder = InverseBinder(before, objects);
				if (value.MarshalType == null) state["marshal_null"] = true;
				else state["marshal"] = EditMarshalCodec.Capture(value.MarshalType, binder);
			}
			return new() { ["field_state"] = new Dictionary<string, object?> {
				["field"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "property_update": {
			var value = Ref<PropertyDef>(before, forward.GetProperty("target"), objects);
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("attributes", out _)) state["attributes"] = (uint)value.Attributes;
			if (forward.TryGetProperty("property_type", out _) || forward.TryGetProperty("index_parameter_types", out _))
				state["property_signature"] = EditStructuredSignatureCodec.Capture(value.PropertySig, InverseBinder(before, objects));
			if (forward.TryGetProperty("getter", out _)) {
				if (value.GetMethod == null) state["getter_null"] = true;
				else state["getter"] = InverseReference(value.GetMethod, objects);
			}
			if (forward.TryGetProperty("setter", out _)) {
				if (value.SetMethod == null) state["setter_null"] = true;
				else state["setter"] = InverseReference(value.SetMethod, objects);
			}
			return new() { ["property_state"] = new Dictionary<string, object?> {
				["property"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "event_update": {
			var value = Ref<EventDef>(before, forward.GetProperty("target"), objects);
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("attributes", out _)) state["attributes"] = (uint)value.Attributes;
			if (forward.TryGetProperty("event_type", out _)) {
				if (value.EventType == null) state["event_null"] = true;
				else state["event_signature"] = EditStructuredSignatureCodec.Capture(value.EventType.ToTypeSig(), InverseBinder(before, objects));
			}
			if (forward.TryGetProperty("add_method", out _)) {
				if (value.AddMethod == null) state["add_null"] = true;
				else state["add_method"] = InverseReference(value.AddMethod, objects);
			}
			if (forward.TryGetProperty("remove_method", out _)) {
				if (value.RemoveMethod == null) state["remove_null"] = true;
				else state["remove_method"] = InverseReference(value.RemoveMethod, objects);
			}
			if (forward.TryGetProperty("raise_method", out _)) {
				if (value.InvokeMethod == null) state["raise_null"] = true;
				else state["raise_method"] = InverseReference(value.InvokeMethod, objects);
			}
			return new() { ["event_state"] = new Dictionary<string, object?> {
				["event"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "generic_parameter_update": {
			var value = Ref<GenericParam>(before, forward.GetProperty("target"), objects);
			var state = new Dictionary<string, object?>();
			if (forward.TryGetProperty("name", out _)) state["name_utf8"] = (byte[])value.Name.Data.Clone();
			if (forward.TryGetProperty("attributes", out _)) state["attributes"] = (uint)value.Flags;
			if (forward.TryGetProperty("constraints", out _)) {
				var binder = InverseBinder(before, objects);
				state["constraints"] = value.GenericParamConstraints
					.Select(x => (object)EditStructuredSignatureCodec.Capture(x.Constraint.ToTypeSig(), binder)).ToArray();
			}
			return new() { ["generic_state"] = new Dictionary<string, object?> {
				["generic"] = InverseReference(value, objects), ["state"] = state,
			} };
		}
		case "security_add": {
			var provider = SecurityProvider(before, forward.GetProperty("parent"));
			var rows = SecurityRows(before, forward.GetProperty("parent"));
			var action = SecurityActions[RequiredString(forward, "action")];
			var index = rows.Where(x => x.Action == action).Count();
			return new() { ["security_remove_state"] = new Dictionary<string, object?> {
				["parent"] = forward.GetProperty("parent").Clone(), ["action"] = RequiredString(forward, "action"), ["index"] = index,
			} };
		}
		case "security_remove": {
			var rows = SecurityRows(before, forward.GetProperty("parent"));
			var action = SecurityActions[RequiredString(forward, "action")];
			var index = forward.TryGetProperty("index", out var indexValue) ? (int)indexValue.GetUInt32() : 0;
			var row = rows.Where(x => x.Action == action).Skip(index).FirstOrDefault()
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			return new() { ["security_add_state"] = new Dictionary<string, object?> {
				["parent"] = forward.GetProperty("parent").Clone(),
				["action"] = RequiredString(forward, "action"),
				["xml"] = row.GetNet1xXmlString() ?? string.Empty,
			} };
		}
		case "attribute_add": {
			// Inverse = remove the exact instance among same-constructor rows.
			var addTarget = AttributeInverseTarget(before, forward, objects, out var addAssemblyScoped);
			var addConstructor = ResolveAttributeConstructor(before, forward.GetProperty("constructor"));
			var rows = addTarget.CustomAttributes.Where(x => SameAttributeConstructor(x.Constructor, addConstructor)).Count();
			return new() { ["attribute_remove_state"] = new Dictionary<string, object?> {
				["target"] = addAssemblyScoped ? new Dictionary<string, object?> { ["scope"] = "assembly" } : InverseReference((IMDTokenProvider)addTarget, objects),
				["constructor"] = InverseReference(addConstructor, objects),
				["index"] = rows,
			} };
		}
		case "attribute_remove": {
			// Inverse = re-attach the captured instance verbatim.
			var removeTarget = AttributeInverseTarget(before, forward, objects, out var removeAssemblyScoped);
			var removeConstructor = ResolveAttributeConstructor(before, forward.GetProperty("match").GetProperty("constructor"));
			var removeIndex = forward.GetProperty("match").TryGetProperty("index", out var indexValue) ? (int)indexValue.GetUInt32() : 0;
			var removedRow = removeTarget.CustomAttributes.Where(x => SameAttributeConstructor(x.Constructor, removeConstructor)).Skip(removeIndex).FirstOrDefault()
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			return new() { ["attribute_add_state"] = new Dictionary<string, object?> {
				["target"] = removeAssemblyScoped ? new Dictionary<string, object?> { ["scope"] = "assembly" } : InverseReference((IMDTokenProvider)removeTarget, objects),
				["constructor"] = InverseReference(removeConstructor, objects),
				["fixed_arguments"] = removedRow.ConstructorArguments.Select(x => CaInverseValue(x.Value)).ToArray(),
				["named_arguments"] = removedRow.NamedArguments.Select(x => (object)new Dictionary<string, object?> {
					["kind"] = x.IsField ? "field" : "property", ["name"] = x.Name?.String,
					["type"] = x.Type.FullName, ["value"] = CaInverseValue(x.Argument.Value),
				}).ToArray(),
			} };
		}
		case "method_body_replace": {
			var value = Ref<MethodDef>(before, forward.GetProperty("target"), objects);
			if (value.Body == null) return new() { ["absent_body"] = InverseReference(value, objects) };
			inverse["body"] = McpTools.StructuredBody(value);
			break;
		}
		case "parameter_add":
			return new() { ["kind"] = "parameter_remove", ["remove_mode"] = "reject_if_referenced",
				["parameter_target"] = new Dictionary<string, object?> {
					["owner_method"] = forward.GetProperty("owner_method").Clone(),
					["parameter_index"] = forward.GetProperty("parameter_index").GetInt32(),
				} };
		case "parameter_remove": {
			var value = ResolveParameter(before, forward.GetProperty("parameter_target"), objects);
			return new() { ["parameter_tail_restore"] = new Dictionary<string, object?> {
				["owner_method"] = InverseReference(value.method, objects), ["parameter_index"] = value.index,
				// FullName text loses fidelity and reparsing appends duplicate TypeRef
				// rows; capture the signature with reference identity instead.
				["parameter_signature"] = EditStructuredSignatureCodec.Capture(value.method.MethodSig.Params[value.index], InverseBinder(before, objects)),
				["paramdef"] = value.param == null ? null : new Dictionary<string, object?> {
					["token"] = value.param.MDToken.Raw, ["position"] = value.method.ParamDefs.IndexOf(value.param),
					["name"] = value.param.Name?.String, ["attributes"] = (uint)value.param.Attributes,
				},
			} };
		}
		case "generic_parameter_add":
			return new() { ["generic_tail_remove"] = new Dictionary<string, object?> {
				["owner"] = forward.GetProperty("owner").Clone(), ["generic_index"] = forward.GetProperty("generic_index").GetInt32(),
			} };
		case "generic_parameter_remove": {
			var value = Ref<GenericParam>(before, forward.GetProperty("target"), objects);
			var method = value.Owner as MethodDef;
			return new() { ["generic_tail_restore"] = new Dictionary<string, object?> {
				["owner"] = InverseReference(value.Owner, objects), ["generic_index"] = (int)value.Number,
				["token"] = value.MDToken.Raw, ["name"] = value.Name?.String, ["attributes"] = (uint)value.Flags,
				["method_arity"] = method?.MethodSig.GenParamCount, ["method_calling_convention"] = method == null ? null : (uint?)method.MethodSig.CallingConvention,
			} };
		}
		case "parameter_update": {
			var value = ResolveParameter(before, forward.GetProperty("parameter_target"), objects);
			var state = new Dictionary<string, object?> {
				["owner_method"] = InverseReference(value.method, objects), ["parameter_index"] = value.index,
				["had_paramdef"] = value.param != null,
			};
			if (forward.TryGetProperty("name", out _)) {
				var parameterName = value.param?.Name;
				if (parameterName is not UTF8String present) state["name_null"] = true;
				else state["name_utf8"] = (byte[])present.Data.Clone();
			}
			if (forward.TryGetProperty("attributes", out _)) state["attributes"] = (uint)(value.param?.Attributes ?? 0);
			if (forward.TryGetProperty("parameter_type", out _))
				state["parameter_signature"] = EditStructuredSignatureCodec.Capture(value.method.MethodSig.Params[value.index], InverseBinder(before, objects));
			if (forward.TryGetProperty("marshal", out _)) {
				var binder = InverseBinder(before, objects);
				var existing = value.param?.MarshalType;
				if (existing == null) state["marshal_null"] = true;
				else state["marshal"] = EditMarshalCodec.Capture(existing, binder);
			}
			return new() { ["parameter_state"] = state };
		}
		case "field_remove":
		case "method_remove":
		case "property_remove":
		case "event_remove":
		case "type_remove": {
			// The forward removal re-owns the row into the operation tombstone,
			// which the checkpoint image persists with full semantics. The
			// compiled inverse only needs the row/owner identities and position
			// to move it back; no parallel snapshot representation exists.
			var rowTarget = forward.GetProperty("target");
			Dictionary<string, object?> Emit(IMDTokenProvider row, TypeDef owner, int index) => new() {
				["kind"] = kind, ["row"] = InverseReference(row, objects),
				["owner"] = InverseReference(owner, objects), ["owner_index"] = index,
			};
			switch (kind) {
			case "field_remove": { var row = Ref<FieldDef>(before, rowTarget, objects); return new() { ["member_restore"] = Emit(row, row.DeclaringType!, row.DeclaringType!.Fields.IndexOf(row)) }; }
			case "method_remove": { var row = Ref<MethodDef>(before, rowTarget, objects); return new() { ["member_restore"] = Emit(row, row.DeclaringType!, row.DeclaringType!.Methods.IndexOf(row)) }; }
			case "property_remove": {
				var row = Ref<PropertyDef>(before, rowTarget, objects);
				var state = Emit(row, row.DeclaringType!, row.DeclaringType!.Properties.IndexOf(row));
				state["accessors"] = AccessorState(PropertyAccessors(row), row.DeclaringType!, objects);
				return new() { ["member_restore"] = state };
			}
			default: {
				var row = Ref<EventDef>(before, rowTarget, objects);
				var state = Emit(row, row.DeclaringType!, row.DeclaringType!.Events.IndexOf(row));
				state["accessors"] = AccessorState(EventAccessors(row), row.DeclaringType!, objects);
				return new() { ["member_restore"] = state };
			}
			case "type_remove": {
				// The deleted subtree is re-owned into the tombstone's nested
				// types; the restore only needs the row, its original parent (or
				// the top-level flag) and the index inside that parent's list.
				var row = Ref<TypeDef>(before, rowTarget, objects);
				var parent = row.DeclaringType;
				var collection = parent == null ? before.Types : parent.NestedTypes;
				var state = new Dictionary<string, object?> {
					["kind"] = "type_remove", ["row"] = InverseReference(row, objects),
					["top_level"] = parent == null, ["parent"] = parent == null ? null : InverseReference(parent, objects),
					["owner_index"] = collection.IndexOf(row),
				};
				return new() { ["member_restore"] = state };
			}
			}
		}
		default:
			// P03 is not deployable until all registry kinds have executable inverses.
			// Never substitute a whole-graph transplant for an unimplemented kind.
			// type_remove needs whole-subtree ownership handling and stays open.
			throw new NotSupportedException("P03 inverse lowering is not implemented: " + kind);
		}
		return inverse;
	}

	internal static EditOperationOutcome ApplyCompiledInverse(ModuleDef module, JsonElement inverse,
		Dictionary<string, IMDTokenProvider> objects, int index) {
		if (inverse.TryGetProperty("legacy", out _)) return EditLegacyRenameOperation.ApplyInverse(module, inverse);
		if (inverse.TryGetProperty("definition_tail_remove", out var definitionTail)) {
			var kind = RequiredString(definitionTail, "add_kind");
			var ownerValue = definitionTail.GetProperty("owner_address");
			var owner = ownerValue.ValueKind == JsonValueKind.Null ? null
				: EditDefinitionAddress.Resolve(module, ownerValue.GetString()!) as TypeDef ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var children = DefinitionChildren(module, owner, kind);
			var position = definitionTail.GetProperty("index").GetInt32();
			if (position < 0 || children.Count != position + 1) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var target = children[position];
			var name = target switch { TypeDef t => t.Name, MethodDef m => m.Name, FieldDef f => f.Name,
				PropertyDef p => p.Name, EventDef e => e.Name, _ => throw new EditDomainException("EDIT_HISTORY_CONFLICT") };
			if (name != RequiredString(definitionTail, "name")) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var bindings = objects.Where(pair => ReferenceEquals(pair.Value, target)).ToArray();
			var temporary = new Dictionary<string, IMDTokenProvider> { ["inverse-definition-tail"] = target };
			using var removal = JsonDocument.Parse(JsonSerializer.Serialize(new {
				kind = kind.Substring(0, kind.Length - 4) + "_remove", target = new { object_id = "inverse-definition-tail" }, remove_mode = "reject_if_referenced",
			}));
			var applied = Apply(module, removal.RootElement, temporary, index);
			foreach (var binding in bindings) objects.Remove(binding.Key);
			return new EditOperationOutcome { Kind = applied.Kind, Target = applied.Target, Before = applied.Before,
				After = applied.After, Risks = applied.Risks, Undo = () => {
					applied.Undo();
					foreach (var binding in bindings) objects[binding.Key] = binding.Value;
				} };
		}
		if (inverse.TryGetProperty("absent_body", out var absentBody)) {
			var method = Ref<MethodDef>(module, absentBody, objects);
			var previous = method.Body;
			method.Body = null;
			return new EditOperationOutcome { Kind = "method_body_replace", Undo = () => method.Body = previous };
		}
		if (inverse.TryGetProperty("field_state", out var fieldState))
			return RestoreFieldState(module, fieldState, objects);
		if (inverse.TryGetProperty("type_state", out var typeState))
			return RestoreTypeState(module, typeState, objects);
		if (inverse.TryGetProperty("method_state", out var methodState))
			return RestoreMethodState(module, methodState, objects);
		if (inverse.TryGetProperty("property_state", out var propertyState))
			return RestorePropertyState(module, propertyState, objects);
		if (inverse.TryGetProperty("event_state", out var eventState))
			return RestoreEventState(module, eventState, objects);
		if (inverse.TryGetProperty("generic_state", out var genericState))
			return RestoreGenericState(module, genericState, objects);
		if (inverse.TryGetProperty("attribute_remove_state", out var attributeRemove)) {
			var removeTarget = AttributeRestoreTarget(module, attributeRemove.GetProperty("target"), objects);
			var removeConstructor = Ref<MethodDef>(module, attributeRemove.GetProperty("constructor"), objects);
			var removeRows = removeTarget.CustomAttributes.Where(x => ReferenceEquals(x.Constructor, removeConstructor)).ToList();
			var removeIndex = attributeRemove.GetProperty("index").GetInt32();
			if (removeIndex >= removeRows.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var removedRow = removeRows[removeIndex];
			removeTarget.CustomAttributes.Remove(removedRow);
			return new EditOperationOutcome { Kind = "attribute_remove", Target = removeConstructor.DeclaringType.FullName, Undo = () => removeTarget.CustomAttributes.Add(removedRow) };
		}
		if (inverse.TryGetProperty("attribute_add_state", out var attributeAdd)) {
			var addTarget = AttributeRestoreTarget(module, attributeAdd.GetProperty("target"), objects);
			var addConstructor = Ref<MethodDef>(module, attributeAdd.GetProperty("constructor"), objects);
			var fixedArguments = attributeAdd.GetProperty("fixed_arguments").EnumerateArray()
				.Select((value, i) => new CAArgument(addConstructor.MethodSig.Params[i], CaValue(addConstructor.MethodSig.Params[i], value, "inverse")))
				.ToArray();
			var namedArguments = attributeAdd.GetProperty("named_arguments").EnumerateArray().Select(x => {
				var kind = x.GetProperty("kind").GetString()!;
				var type = new EditTypeSigParser(module).Parse(x.GetProperty("type").GetString()!);
				return new CANamedArgument(kind == "field", type, x.GetProperty("name").GetString()!,
					new CAArgument(type, CaValue(type, x.GetProperty("value"), "inverse")));
			}).ToList();
			var attribute = new CustomAttribute(addConstructor, fixedArguments, namedArguments);
			addTarget.CustomAttributes.Add(attribute);
			return new EditOperationOutcome { Kind = "attribute_add", Target = addConstructor.DeclaringType.FullName, Undo = () => addTarget.CustomAttributes.Remove(attribute) };
		}
		if (inverse.TryGetProperty("security_remove_state", out var securityRemove)) {
			var removeRows = SecurityRows(module, securityRemove.GetProperty("parent"));
			var removeAction = SecurityActions[securityRemove.GetProperty("action").GetString()!];
			var removeIndex = securityRemove.GetProperty("index").GetInt32();
			var removeMatches = removeRows.Where(x => x.Action == removeAction).ToList();
			if (removeIndex >= removeMatches.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var removedSecurity = removeMatches[removeIndex];
			removeRows.Remove(removedSecurity);
			return new EditOperationOutcome { Kind = "security_remove", Target = securityRemove.GetProperty("action").GetString()!, Undo = () => removeRows.Add(removedSecurity) };
		}
		if (inverse.TryGetProperty("security_add_state", out var securityAdd)) {
			var addRows = SecurityRows(module, securityAdd.GetProperty("parent"));
			var addAttribute = dnlib.DotNet.SecurityAttribute.CreateFromXml(module, securityAdd.GetProperty("xml").GetString()!)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var addedRow = new DeclSecurityUser(SecurityActions[securityAdd.GetProperty("action").GetString()!], new[] { addAttribute });
			addRows.Add(addedRow);
			return new EditOperationOutcome { Kind = "security_add", Target = securityAdd.GetProperty("action").GetString()!, Undo = () => addRows.Remove(addedRow) };
		}
		if (inverse.TryGetProperty("parameter_state", out var parameterInverse) && parameterInverse.TryGetProperty("marshal", out _)) {
			// parameter marshal rides inside parameter_state; handled below by the existing branch.
		}
		if (inverse.TryGetProperty("member_restore", out var memberRestore))
			return RestoreMemberFromTombstone(module, memberRestore, objects);
		if (inverse.TryGetProperty("parameter_tail_restore", out var tail))
			return RestoreParameterTail(module, tail, objects);
		if (inverse.TryGetProperty("generic_tail_restore", out var generic))
			return RestoreGenericTail(module, generic, objects);
		if (inverse.TryGetProperty("generic_tail_remove", out var removeGeneric)) {
			var owner = Ref<IMDTokenProvider>(module, removeGeneric.GetProperty("owner"), objects);
			var collection = GenericCollection(owner);
			var number = removeGeneric.GetProperty("generic_index").GetInt32();
			if (number < 0 || collection.Count != number + 1) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var value = collection[number];
			var id = "inverse-generic-tail";
			if (objects.ContainsKey(id)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			objects.Add(id, value);
			try {
				using var operation = JsonDocument.Parse(JsonSerializer.Serialize(new {
					kind = "generic_parameter_remove", target = new { object_id = id }, remove_mode = "reject_if_referenced",
				}));
				return Apply(module, operation.RootElement, objects, index);
			}
			finally { objects.Remove(id); }
		}
		if (inverse.TryGetProperty("parameter_state", out var state)) {
			var owner = Ref<MethodDef>(module, state.GetProperty("owner_method"), objects);
			var parameterIndex = state.GetProperty("parameter_index").GetInt32();
			if (parameterIndex < 0 || parameterIndex >= owner.MethodSig.Params.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var previousType = owner.MethodSig.Params[parameterIndex];
			var previous = owner.ParamDefs.SingleOrDefault(x => x.Sequence == parameterIndex + 1);
			var previousName = previous?.Name; var previousAttributes = previous?.Attributes ?? 0;
			var previousIndex = previous == null ? -1 : owner.ParamDefs.IndexOf(previous);
			TypeSig type;
			var previousMarshal = previous?.MarshalType;
			if (state.TryGetProperty("marshal", out var marshalNode))
				(previous ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT")).MarshalType =
					EditMarshalCodec.Restore(marshalNode, node => RestoreInverseType(module, node, objects));
			else if (state.TryGetProperty("marshal_null", out _)) previous?.MarshalType?.GetType(); // cleared below via restore of null
			if (state.TryGetProperty("parameter_signature", out var parameterSignature)) {
				var node = parameterSignature.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
					?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				type = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects));
			}
			else if (state.TryGetProperty("parameter_type", out var parameterTypeText))
				type = new EditTypeSigParser(module, owner.DeclaringType.GenericParameters.Count, owner.GenericParameters.Count)
					.Parse(parameterTypeText.GetString()!);
			else
				type = owner.MethodSig.Params[parameterIndex];
			var restored = previous;
			if (state.GetProperty("had_paramdef").GetBoolean()) {
				if (restored == null) { restored = new ParamDefUser(null, (ushort)(parameterIndex + 1)); owner.ParamDefs.Add(restored); }
				if (state.TryGetProperty("name_utf8", out var nameBytes))
					restored.Name = new UTF8String(nameBytes.GetBytesFromBase64());
				else if (state.TryGetProperty("name_null", out _))
					restored.Name = null;
				else if (state.TryGetProperty("name", out var nameText))
					restored.Name = nameText.ValueKind == JsonValueKind.Null ? null : nameText.GetString();
				if (state.TryGetProperty("attributes", out var paramAttributes))
					restored.Attributes = (dnlib.DotNet.ParamAttributes)paramAttributes.GetUInt32();
			}
			else if (restored != null) owner.ParamDefs.Remove(restored);
			owner.MethodSig.Params[parameterIndex] = type;
			return new EditOperationOutcome { Kind = "parameter_update", Undo = () => {
				owner.MethodSig.Params[parameterIndex] = previousType;
				if (previous == null) { if (restored != null) owner.ParamDefs.Remove(restored); }
				else {
					if (!owner.ParamDefs.Contains(previous)) owner.ParamDefs.Insert(previousIndex, previous);
					previous.Name = previousName; previous.Attributes = previousAttributes;
				}
			} };
		}
		return Apply(module, inverse, objects, index);
	}

	static IList<GenericParam> GenericCollection(IMDTokenProvider owner) => owner switch {
		TypeDef type => type.GenericParameters,
		MethodDef method => method.GenericParameters,
		_ => throw new EditDomainException("EDIT_HISTORY_CONFLICT"),
	};

	static IReadOnlyList<IMDTokenProvider> DefinitionChildren(ModuleDef module, TypeDef? owner, string addKind) => addKind switch {
		"type_add" => (owner == null ? module.Types : owner.NestedTypes).Cast<IMDTokenProvider>().ToArray(),
		"method_add" when owner != null => owner.Methods.Cast<IMDTokenProvider>().ToArray(),
		"field_add" when owner != null => owner.Fields.Cast<IMDTokenProvider>().ToArray(),
		"property_add" when owner != null => owner.Properties.Cast<IMDTokenProvider>().ToArray(),
		"event_add" when owner != null => owner.Events.Cast<IMDTokenProvider>().ToArray(),
		_ => throw new EditDomainException("EDIT_HISTORY_CONFLICT"),
	};

	static EditOperationOutcome RestoreParameterTail(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var owner = Ref<MethodDef>(module, state.GetProperty("owner_method"), objects);
		var number = state.GetProperty("parameter_index").GetInt32();
		if (number < 0 || number != owner.MethodSig.Params.Count || owner.ParamDefs.Any(p => p.Sequence == number + 1))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		TypeSig type;
		if (state.TryGetProperty("parameter_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			type = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects));
		}
		else
			type = new EditTypeSigParser(module, owner.DeclaringType.GenericParameters.Count, owner.GenericParameters.Count)
				.Parse(state.GetProperty("parameter_type").GetString()!);
		ParamDef? value = null;
		var position = -1;
		MethodDef? placeholderOwner = null;
		var placeholderPosition = -1;
		UTF8String? oldName = null;
		ushort oldSequence = 0;
		dnlib.DotNet.ParamAttributes oldAttributes = 0;
		string? restoredName = null;
		dnlib.DotNet.ParamAttributes restoredAttributes = 0;
		// The emptied tombstone host method and (once hostless) the tombstone
		// type sit at the END of their tables, so removing exactly that structure
		// leaves no row gap and the re-emitted image can equal the pre-deletion
		// checkpoint byte for byte.
		TypeDef? tombstoneType = null;
		IList<TypeDef>? tombstoneTypeList = null;
		var tombstoneTypeIndex = -1;
		var tombstoneMethodIndex = -1;
		var removeTombstoneStructure = false;
		if (state.GetProperty("paramdef") is var row && row.ValueKind != JsonValueKind.Null) {
			var token = row.GetProperty("token").GetUInt32();
			position = row.GetProperty("position").GetInt32();
			if (position < 0 || position > owner.ParamDefs.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var name = row.GetProperty("name").ValueKind == JsonValueKind.Null ? null : row.GetProperty("name").GetString();
			var attrs = (dnlib.DotNet.ParamAttributes)row.GetProperty("attributes").GetUInt32();
			value = (token & 0xffffff) == 0 ? new ParamDefUser(name, (ushort)(number + 1), attrs) : module.ResolveToken(token) as ParamDef;
			if (value == null) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var owners = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.ParamDefs.Contains(value)).ToArray();
			if (owners.Length != 0) {
				// The row sits in the P03 operation tombstone (P03-CHANGE-002 v3):
				// the forward removal re-owns it to a d{rid} host method so the
				// writer never creates its own dummy_ptr structure.
				var hostSuffix = value.MDToken.Rid.ToString("x6");
				var hostName = owners[0].Name.String ?? string.Empty;
				if (owners.Length != 1 || owners[0].DeclaringType is not { } hostType
					|| !EditDeletedRowsTombstone.IsTombstone(hostType)
					|| !EditDeletedRowsTombstone.IsParamHost(owners[0])
					// The host is named after the row it holds; a mismatching name is
					// a foreign or tampered structure and must not be restored from.
					|| (!hostName.Equals("d" + hostSuffix, StringComparison.Ordinal)
						&& !hostName.StartsWith("d" + hostSuffix + "-", StringComparison.Ordinal)))
					throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				placeholderOwner = owners[0]; placeholderPosition = placeholderOwner.ParamDefs.IndexOf(value);
				// The emptied host and (once hostless) the tombstone type sit at
				// table tails; removing them leaves no row gap behind.
				var list = hostType.DeclaringType?.NestedTypes ?? module.Types;
				var index = list.IndexOf(hostType);
				if (index >= 0) {
					tombstoneType = hostType; tombstoneTypeList = list; tombstoneTypeIndex = index;
					tombstoneMethodIndex = hostType.Methods.IndexOf(placeholderOwner);
					removeTombstoneStructure = true;
				}
			}
			else if (value.Sequence != number + 1 || value.Name != name || value.Attributes != attrs)
				throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			oldName = value.Name; oldSequence = value.Sequence; oldAttributes = value.Attributes;
			restoredName = name; restoredAttributes = attrs;
		}
		bool tombstoneMethodRemoved = false, tombstoneTypeRemoved = false;
		void RestorePrevious() {
			if (tombstoneTypeRemoved) tombstoneTypeList!.Insert(tombstoneTypeIndex, tombstoneType!);
			if (tombstoneMethodRemoved) tombstoneType!.Methods.Insert(tombstoneMethodIndex, placeholderOwner!);
			if (value != null) {
				owner.ParamDefs.Remove(value);
				value.Name = oldName; value.Sequence = oldSequence; value.Attributes = oldAttributes;
				if (placeholderOwner != null && !placeholderOwner.ParamDefs.Contains(value)) placeholderOwner.ParamDefs.Insert(placeholderPosition, value);
			}
			if (owner.MethodSig.Params.Count == number + 1) owner.MethodSig.Params.RemoveAt(number);
		}
		try {
			owner.MethodSig.Params.Add(type);
			if (value != null) {
				placeholderOwner?.ParamDefs.Remove(value);
				value.Name = restoredName; value.Sequence = (ushort)(number + 1); value.Attributes = restoredAttributes;
				owner.ParamDefs.Insert(position, value);
				if (removeTombstoneStructure && placeholderOwner!.ParamDefs.Count == 0) {
					tombstoneType!.Methods.RemoveAt(tombstoneMethodIndex); tombstoneMethodRemoved = true;
					tombstoneTypeList!.RemoveAt(tombstoneTypeIndex); tombstoneTypeRemoved = true;
				}
			}
		}
		catch { RestorePrevious(); throw; }
		return new EditOperationOutcome { Kind = "parameter_remove", Undo = RestorePrevious };
	}

	static EditOperationOutcome RestoreGenericTail(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var owner = Ref<IMDTokenProvider>(module, state.GetProperty("owner"), objects);
		var collection = GenericCollection(owner);
		var number = state.GetProperty("generic_index").GetInt32();
		if (number < 0 || collection.Count != number) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var token = state.GetProperty("token").GetUInt32();
		var name = state.GetProperty("name").ValueKind == JsonValueKind.Null ? null : state.GetProperty("name").GetString();
		var attrs = (GenericParamAttributes)state.GetProperty("attributes").GetUInt32();
		var cached = (token & 0xffffff) == 0 ? null : module.ResolveToken(token) as GenericParam;
		var attached = cached != null && module.GetTypes().Any(t => t.GenericParameters.Contains(cached)
			|| t.Methods.Any(m => m.GenericParameters.Contains(cached)));
		// GenericParam RIDs are sorted by owner on write and are not covered by
		// PreserveRids. After reloading the edited image, this old RID can belong
		// to a different owner's parameter. Never mutate/reparent that cache row.
		// The persisted owner/index is the identity of the slot being restored.
		var value = cached == null || attached ? new GenericParamUser((ushort)number, attrs, name) : cached;
		if (value.Number != number || value.Name != name || value.Flags != attrs)
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var method = owner as MethodDef;
		var arity = method?.MethodSig.GenParamCount ?? 0;
		var convention = method?.MethodSig.CallingConvention ?? 0;
		var restoredArity = method == null ? 0 : state.GetProperty("method_arity").GetUInt32();
		var restoredConvention = method == null ? 0 : (CallingConvention)state.GetProperty("method_calling_convention").GetUInt32();
		collection.Add(value);
		if (method != null) { method.MethodSig.GenParamCount = restoredArity; method.MethodSig.CallingConvention = restoredConvention; }
		return new EditOperationOutcome { Kind = "generic_parameter_remove", Undo = () => {
			collection.Remove(value);
			if (method != null) { method.MethodSig.GenParamCount = arity; method.MethodSig.CallingConvention = convention; }
		} };
	}

	// One shared generator for every same-session binding so the signature binder
	// and member references can never mint colliding keys inside one object map.
	static int inverseBindingCounter;
	static string NextInverseBindingKey(Dictionary<string, IMDTokenProvider> objects) {
		string key;
		do { key = "inverse-ref-" + inverseBindingCounter++; } while (objects.ContainsKey(key));
		return key;
	}

	static object? InverseReference(IMDTokenProvider? value, Dictionary<string, IMDTokenProvider> objects) {
		if (value == null) return null;
		var objectId = objects.FirstOrDefault(x => ReferenceEquals(x.Value, value)).Key;
		if (objectId != null) return new Dictionary<string, object?> { ["object_id"] = objectId };
		if (value.MDToken.Rid != 0) return new Dictionary<string, object?> { ["token"] = "0x" + value.MDToken.Raw.ToString("x8") };
		// Zero-RID session-created rows get a same-session binding, mirroring the
		// signature binder; cross-reload persistence of such bindings stays open
		// under PLAN-CHANGE-001.
		var key = NextInverseBindingKey(objects);
		objects[key] = value;
		return new Dictionary<string, object?> { ["object_id"] = key };
	}

	static EditOperationOutcome RestoreTypeState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var type = Ref<TypeDef>(module, state.GetProperty("type"), objects);
		var changes = state.GetProperty("state");
		var previousName = type.Name; var previousNamespace = type.Namespace;
		var previousAttributes = type.Attributes; var previousBase = type.BaseType;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			type.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("namespace_utf8", out var namespaceBytes))
			type.Namespace = new UTF8String(namespaceBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			type.Attributes = (TypeAttributes)attributes.GetUInt32();
		if (changes.TryGetProperty("base_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			type.BaseType = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects)).ToTypeDefOrRef();
		}
		else if (changes.TryGetProperty("base_null", out _)) type.BaseType = null;
		ClassLayout? previousLayoutRef = type.ClassLayout;
		if (changes.TryGetProperty("layout", out var layout)) {
			var pack = layout.GetProperty("pack").GetUInt16(); var size = layout.GetProperty("size").GetUInt32();
			if (type.ClassLayout == null) type.ClassLayout = new ClassLayoutUser(pack, size);
			else { type.ClassLayout.PackingSize = pack; type.ClassLayout.ClassSize = size; }
		}
		else if (changes.TryGetProperty("layout_null", out _)) type.ClassLayout = null;
		return new EditOperationOutcome { Kind = "type_update", Target = type.FullName, Undo = () => {
			type.Name = previousName; type.Namespace = previousNamespace;
			type.Attributes = previousAttributes; type.BaseType = previousBase; type.ClassLayout = previousLayoutRef;
		} };
	}

	static EditOperationOutcome RestoreMethodState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var method = Ref<MethodDef>(module, state.GetProperty("method"), objects);
		var changes = state.GetProperty("state");
		var previousName = method.Name; var previousAttributes = method.Attributes;
		var previousImpl = method.ImplAttributes; var previousReturn = method.ReturnType; var previousHasThis = method.MethodSig.HasThis;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			method.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			method.Attributes = (MethodAttributes)attributes.GetUInt32();
		if (changes.TryGetProperty("impl_attributes", out var implAttributes))
			method.ImplAttributes = (MethodImplAttributes)implAttributes.GetUInt32();
		if (changes.TryGetProperty("return_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			method.MethodSig.RetType = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects));
		}
		if (changes.TryGetProperty("has_this", out var hasThis))
			method.MethodSig.HasThis = hasThis.GetBoolean();
		var previousOverrides = method.Overrides.ToArray();
		if (changes.TryGetProperty("overrides", out var overrides)) {
			method.Overrides.Clear();
			foreach (var row in overrides.EnumerateArray()) {
				var body = Ref<MethodDef>(module, row.GetProperty("method"), objects);
				var declaration = Ref<MethodDef>(module, row.GetProperty("declaration"), objects);
				method.Overrides.Add(new MethodOverride(body, declaration));
			}
		}
		var previousImplMap = method.ImplMap;
		if (changes.TryGetProperty("pinvoke", out var pinvoke)) {
			var moduleRef = module.GetModuleRefs().FirstOrDefault(x => string.Equals(x.Name?.String, pinvoke.GetProperty("module_name").GetString(), StringComparison.Ordinal))
				?? new ModuleRefUser(module, pinvoke.GetProperty("module_name").GetString()!);
			method.ImplMap = new ImplMapUser(moduleRef, pinvoke.GetProperty("entry_name").GetString()!, (PInvokeAttributes)pinvoke.GetProperty("flags").GetUInt32());
		}
		else if (changes.TryGetProperty("pinvoke_null", out _)) method.ImplMap = null;
		return new EditOperationOutcome { Kind = "method_update", Target = method.FullName, Undo = () => {
			method.Name = previousName; method.Attributes = previousAttributes; method.ImplAttributes = previousImpl;
			method.MethodSig.RetType = previousReturn; method.MethodSig.HasThis = previousHasThis;
			method.Overrides.Clear();
			foreach (var row in previousOverrides) method.Overrides.Add(row);
			method.ImplMap = previousImplMap;
		} };
	}

	// P04: attribute inverses restore the exact instance.
	static IHasCustomAttribute AttributeInverseTarget(ModuleDef module, JsonElement forward,
		Dictionary<string, IMDTokenProvider> objects, out bool assemblyScoped) {
		var reference = forward.GetProperty("target");
		if (reference.TryGetProperty("scope", out var scope) && scope.GetString() == "assembly") {
			assemblyScoped = true;
			return module.Assembly ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		assemblyScoped = false;
		return Ref<IMDTokenProvider>(module, reference, objects) as IHasCustomAttribute
			?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}
	static object? CaInverseValue(object? value) => value switch {
		null => null,
		bool b => b, char c => (ushort)c, sbyte v => v, byte v => v, short v => v, ushort v => v,
		int v => v, uint v => v, long v => v, ulong v => v, float v => v, double v => v,
		UTF8String s => s.String ?? string.Empty, string s => s,
		_ => throw new EditDomainException("EDIT_HISTORY_CONFLICT"),
	};
	static IHasCustomAttribute AttributeRestoreTarget(ModuleDef module, JsonElement reference, Dictionary<string, IMDTokenProvider> objects) {
		if (reference.TryGetProperty("scope", out var scope) && scope.GetString() == "assembly")
			return module.Assembly ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return Ref<IMDTokenProvider>(module, reference, objects) as IHasCustomAttribute
			?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}
	static ITypeDefOrRef RestoreInverseType(ModuleDef module, JsonElement node, Dictionary<string, IMDTokenProvider> objects) {
		var restored = node.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
			?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return EditStructuredSignatureCodec.Restore(restored, id => ResolveInverseReference(module, id, objects)).ToTypeDefOrRef();
	}

	static EditOperationOutcome RestorePropertyState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var property = Ref<PropertyDef>(module, state.GetProperty("property"), objects);
		var changes = state.GetProperty("state");
		var previousName = property.Name; var previousSignature = property.PropertySig;
		var previousAttributes = property.Attributes; var previousGetter = property.GetMethod; var previousSetter = property.SetMethod;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			property.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			property.Attributes = (PropertyAttributes)attributes.GetUInt32();
		if (changes.TryGetProperty("property_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.CallNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			property.PropertySig = (PropertySig)EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects));
		}
		if (changes.TryGetProperty("getter", out var getter))
			property.GetMethod = Ref<MethodDef>(module, getter, objects);
		else if (changes.TryGetProperty("getter_null", out _)) property.GetMethod = null;
		if (changes.TryGetProperty("setter", out var setter))
			property.SetMethod = Ref<MethodDef>(module, setter, objects);
		else if (changes.TryGetProperty("setter_null", out _)) property.SetMethod = null;
		return new EditOperationOutcome { Kind = "property_update", Target = property.FullName, Undo = () => {
			property.Name = previousName; property.PropertySig = previousSignature; property.Attributes = previousAttributes;
			property.GetMethod = previousGetter; property.SetMethod = previousSetter;
		} };
	}

	static EditOperationOutcome RestoreEventState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var evt = Ref<EventDef>(module, state.GetProperty("event"), objects);
		var changes = state.GetProperty("state");
		var previousName = evt.Name; var previousType = evt.EventType; var previousAttributes = evt.Attributes;
		var previousAdd = evt.AddMethod; var previousRemove = evt.RemoveMethod; var previousRaise = evt.InvokeMethod;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			evt.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			evt.Attributes = (EventAttributes)attributes.GetUInt32();
		if (changes.TryGetProperty("event_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			evt.EventType = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects)).ToTypeDefOrRef();
		}
		else if (changes.TryGetProperty("event_null", out _)) evt.EventType = null;
		if (changes.TryGetProperty("add_method", out var add))
			evt.AddMethod = Ref<MethodDef>(module, add, objects);
		else if (changes.TryGetProperty("add_null", out _)) evt.AddMethod = null;
		if (changes.TryGetProperty("remove_method", out var remove))
			evt.RemoveMethod = Ref<MethodDef>(module, remove, objects);
		else if (changes.TryGetProperty("remove_null", out _)) evt.RemoveMethod = null;
		if (changes.TryGetProperty("raise_method", out var raise))
			evt.InvokeMethod = Ref<MethodDef>(module, raise, objects);
		else if (changes.TryGetProperty("raise_null", out _)) evt.InvokeMethod = null;
		return new EditOperationOutcome { Kind = "event_update", Target = evt.FullName, Undo = () => {
			evt.Name = previousName; evt.EventType = previousType; evt.Attributes = previousAttributes;
			evt.AddMethod = previousAdd; evt.RemoveMethod = previousRemove; evt.InvokeMethod = previousRaise;
		} };
	}

	static EditOperationOutcome RestoreGenericState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var generic = Ref<GenericParam>(module, state.GetProperty("generic"), objects);
		var changes = state.GetProperty("state");
		var previousName = generic.Name; var previousFlags = generic.Flags;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			generic.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			generic.Flags = (GenericParamAttributes)attributes.GetUInt32();
		var previousConstraints = generic.GenericParamConstraints.ToArray();
		if (changes.TryGetProperty("constraints", out var constraints)) {
			generic.GenericParamConstraints.Clear();
			foreach (var row in constraints.EnumerateArray()) {
				var node = row.Deserialize<EditStructuredSignatureCodec.TypeNode>(EditWire.JsonOptions)
					?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				var restored = EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects)).ToTypeDefOrRef();
				generic.GenericParamConstraints.Add(new GenericParamConstraintUser(restored));
			}
		}
		return new EditOperationOutcome { Kind = "generic_parameter_update", Target = generic.Name?.String ?? string.Empty, Undo = () => {
			generic.Name = previousName; generic.Flags = previousFlags;
			generic.GenericParamConstraints.Clear();
			foreach (var constraint in previousConstraints) generic.GenericParamConstraints.Add(constraint);
		} };
	}

	static EditOperationOutcome RestoreFieldState(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var field = Ref<FieldDef>(module, state.GetProperty("field"), objects);
		var changes = state.GetProperty("state");
		var previousName = field.Name; var previousAttributes = field.Attributes;
		var previousSignature = field.FieldSig; var previousConstant = field.Constant;
		if (changes.TryGetProperty("name_utf8", out var nameBytes))
			field.Name = new UTF8String(nameBytes.GetBytesFromBase64());
		if (changes.TryGetProperty("attributes", out var attributes))
			field.Attributes = (FieldAttributes)attributes.GetUInt32();
		if (changes.TryGetProperty("field_signature", out var signature)) {
			var node = signature.Deserialize<EditStructuredSignatureCodec.CallNode>(EditWire.JsonOptions)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			field.FieldSig = (FieldSig)EditStructuredSignatureCodec.Restore(node, id => ResolveInverseReference(module, id, objects));
		}
		if (changes.TryGetProperty("constant", out var constant))
			field.Constant = RestoreInverseConstant(constant);
		else if (changes.TryGetProperty("constant_null", out _))
			field.Constant = null;
		var previousOffset = field.FieldOffset; var previousInitial = field.InitialValue;
		if (changes.TryGetProperty("field_offset", out var offset)) field.FieldOffset = offset.GetUInt32();
		else if (changes.TryGetProperty("field_offset_null", out _)) field.FieldOffset = null;
		if (changes.TryGetProperty("initial_data", out var initial))
			field.InitialValue = initial.GetBytesFromBase64();
		else if (changes.TryGetProperty("initial_data_null", out _)) field.InitialValue = null;
		var previousMarshal = field.MarshalType;
		if (changes.TryGetProperty("marshal", out var marshal))
			field.MarshalType = EditMarshalCodec.Restore(marshal, node => RestoreInverseType(module, node, objects));
		else if (changes.TryGetProperty("marshal_null", out _)) field.MarshalType = null;
		return new EditOperationOutcome { Kind = "field_update", Target = field.FullName, Undo = () => {
			field.Name = previousName; field.Attributes = previousAttributes;
			field.FieldSig = previousSignature; field.Constant = previousConstant;
			field.FieldOffset = previousOffset; field.InitialValue = previousInitial; field.MarshalType = previousMarshal;
		} };
	}

	static object?[] AccessorState((string slot, MethodDef? method)[] accessors, TypeDef owner, Dictionary<string, IMDTokenProvider> objects) =>
		accessors.Where(a => a.method != null)
			.Select(a => (object?)new Dictionary<string, object?> {
				["slot"] = a.slot, ["row"] = InverseReference(a.method!, objects),
				["owner_index"] = owner.Methods.IndexOf(a.method!),
			}).ToArray();

	static EditOperationOutcome RestoreMemberFromTombstone(ModuleDef module, JsonElement state, Dictionary<string, IMDTokenProvider> objects) {
		var kind = RequiredString(state, "kind");
		// type_restore addresses its parent (or the top-level list) explicitly.
		TypeDef? owner = state.TryGetProperty("owner", out var ownerRef) ? Ref<TypeDef>(module, ownerRef, objects) : null;
		var index = state.GetProperty("owner_index").GetInt32();
		var tombstones = module.Types.Where(EditDeletedRowsTombstone.IsTombstone).ToArray();
		if (tombstones.Length != 1) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var tombstone = tombstones[0];
		var rowRef = state.GetProperty("row");
		IMDTokenProvider? moved = null;
		Action? removeFromOwner = null;
		void Move<T>(Func<T> resolve, Func<TypeDef, IList<T>> list) where T : class, IMDTokenProvider {
			if (owner == null) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var value = resolve();
			var tombstoneList = list(tombstone);
			if (!tombstoneList.Contains(value)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var ownerList = list(owner);
			if (index < 0 || index > ownerList.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			tombstoneList.Remove(value);
			ownerList.Insert(index, value);
			moved = value;
			removeFromOwner = () => list(owner).Remove(value);
		}
		if (kind == "type_remove") {
			var typeRow = Ref<TypeDef>(module, rowRef, objects);
			if (!tombstone.NestedTypes.Contains(typeRow)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			var parentCollection = state.GetProperty("top_level").GetBoolean()
				? module.Types
				: Ref<TypeDef>(module, state.GetProperty("parent"), objects).NestedTypes;
			var typeIndex = state.GetProperty("owner_index").GetInt32();
			if (typeIndex < 0 || typeIndex > parentCollection.Count) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			tombstone.NestedTypes.Remove(typeRow);
			parentCollection.Insert(typeIndex, typeRow);
			moved = typeRow;
			removeFromOwner = () => parentCollection.Remove(typeRow);
		}
		else switch (kind) {
		case "field_remove": Move(() => Ref<FieldDef>(module, rowRef, objects), t => t.Fields); break;
		case "method_remove": Move(() => Ref<MethodDef>(module, rowRef, objects), t => t.Methods); break;
		case "property_remove": Move(() => Ref<PropertyDef>(module, rowRef, objects), t => t.Properties); break;
		case "event_remove": Move(() => Ref<EventDef>(module, rowRef, objects), t => t.Events); break;
		default: throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		// Event/property accessors travel with their row inside the tombstone;
		// move them back at their recorded positions (ascending keeps indexes).
		var accessorSlots = Array.Empty<(MethodDef method, int index)>();
		// Lazy so type_restore (owner-less by design) never trips the null guard;
		// accessor-carrying kinds always have an owning type.
		IList<MethodDef> OwnerMethods() => owner?.Methods ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (state.TryGetProperty("accessors", out var accessors)) {
			accessorSlots = accessors.EnumerateArray()
				.Select(a => (Ref<MethodDef>(module, a.GetProperty("row"), objects), a.GetProperty("owner_index").GetInt32()))
				.ToArray();
			foreach (var (accessor, accessorIndex) in accessorSlots.OrderBy(s => s.index)) {
				if (!tombstone.Methods.Contains(accessor)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				tombstone.Methods.Remove(accessor);
				OwnerMethods().Insert(accessorIndex, accessor);
			}
		}
		EditDeletedRowsTombstone.RemoveIfEmpty(tombstone);
		var restoredRow = moved!;
		var detach = removeFromOwner ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return new EditOperationOutcome { Kind = kind, Target = owner?.FullName ?? "<type>", Undo = () => {
			// Detach from the owner first (dnlib ownership), then re-own into the
			// (recreated if needed) tombstone, mirroring the forward removal.
			foreach (var (accessor, _) in accessorSlots) OwnerMethods().Remove(accessor);
			detach();
			var redoTombstone = EditDeletedRowsTombstone.GetOrCreate(module);
			EditDeletedRowsTombstone.AcquireRow(module, restoredRow);
			foreach (var (accessor, _) in accessorSlots) redoTombstone.Methods.Add(accessor);
		} };
	}

	static Func<IMDTokenProvider, string> InverseBinder(ModuleDef module, Dictionary<string, IMDTokenProvider> objects) {
		// CorLibTypes refs carry pseudo row ids (UpdateRowId) and never resolve
		// back from a token, so they get a self-contained key that works even
		// when the restore runs with a fresh object map.
		var corlib = new Dictionary<ITypeDefOrRef, string>(ReferenceComparerForTypes.Instance);
		void CorLib(string name, ITypeDefOrRef? reference) { if (reference != null) corlib[reference] = "corlib:" + name; }
		CorLib("Void", module.CorLibTypes.Void.TypeDefOrRef); CorLib("Boolean", module.CorLibTypes.Boolean.TypeDefOrRef);
		CorLib("Char", module.CorLibTypes.Char.TypeDefOrRef); CorLib("SByte", module.CorLibTypes.SByte.TypeDefOrRef);
		CorLib("Byte", module.CorLibTypes.Byte.TypeDefOrRef); CorLib("Int16", module.CorLibTypes.Int16.TypeDefOrRef);
		CorLib("UInt16", module.CorLibTypes.UInt16.TypeDefOrRef); CorLib("Int32", module.CorLibTypes.Int32.TypeDefOrRef);
		CorLib("UInt32", module.CorLibTypes.UInt32.TypeDefOrRef); CorLib("Int64", module.CorLibTypes.Int64.TypeDefOrRef);
		CorLib("UInt64", module.CorLibTypes.UInt64.TypeDefOrRef); CorLib("Single", module.CorLibTypes.Single.TypeDefOrRef);
		CorLib("Double", module.CorLibTypes.Double.TypeDefOrRef); CorLib("String", module.CorLibTypes.String.TypeDefOrRef);
		CorLib("TypedReference", module.CorLibTypes.TypedReference.TypeDefOrRef); CorLib("IntPtr", module.CorLibTypes.IntPtr.TypeDefOrRef);
		CorLib("UIntPtr", module.CorLibTypes.UIntPtr.TypeDefOrRef); CorLib("Object", module.CorLibTypes.Object.TypeDefOrRef);
		return value => {
			if (value is ITypeDefOrRef corlibRef && corlib.TryGetValue(corlibRef, out var corlibKey)) return corlibKey;
			var existing = objects.FirstOrDefault(pair => ReferenceEquals(pair.Value, value)).Key;
			if (existing != null) return "object_id:" + existing;
			// dnlib hands out user objects with pseudo row ids (UpdateRowId), e.g.
			// every CorLibTypes reference; only a token that resolves back to the
			// same cached instance is a real table row and safe to persist.
			try {
				if (value.MDToken.Rid != 0 && ReferenceEquals(module.ResolveToken(value.MDToken.Raw), value))
					return "token:0x" + value.MDToken.Raw.ToString("x8");
			}
			catch { /* pseudo row id; fall through to a live binding */ }
			// Same-session restore resolves this key from the same live object map.
			// Cross-reload persistence of zero-RID bindings is package work and stays open.
			var key = NextInverseBindingKey(objects);
			objects[key] = value;
			return "object_id:" + key;
		};
	}

	static IMDTokenProvider ResolveInverseReference(ModuleDef module, string id, Dictionary<string, IMDTokenProvider> objects) {
		if (id.StartsWith("corlib:", StringComparison.Ordinal)) {
			var name = id.Substring("corlib:".Length);
			return name switch {
				"Void" => module.CorLibTypes.Void.TypeDefOrRef, "Boolean" => module.CorLibTypes.Boolean.TypeDefOrRef,
				"Char" => module.CorLibTypes.Char.TypeDefOrRef, "SByte" => module.CorLibTypes.SByte.TypeDefOrRef,
				"Byte" => module.CorLibTypes.Byte.TypeDefOrRef, "Int16" => module.CorLibTypes.Int16.TypeDefOrRef,
				"UInt16" => module.CorLibTypes.UInt16.TypeDefOrRef, "Int32" => module.CorLibTypes.Int32.TypeDefOrRef,
				"UInt32" => module.CorLibTypes.UInt32.TypeDefOrRef, "Int64" => module.CorLibTypes.Int64.TypeDefOrRef,
				"UInt64" => module.CorLibTypes.UInt64.TypeDefOrRef, "Single" => module.CorLibTypes.Single.TypeDefOrRef,
				"Double" => module.CorLibTypes.Double.TypeDefOrRef, "String" => module.CorLibTypes.String.TypeDefOrRef,
				"TypedReference" => module.CorLibTypes.TypedReference.TypeDefOrRef, "IntPtr" => module.CorLibTypes.IntPtr.TypeDefOrRef,
				"UIntPtr" => module.CorLibTypes.UIntPtr.TypeDefOrRef, "Object" => module.CorLibTypes.Object.TypeDefOrRef,
				_ => throw new EditDomainException("EDIT_HISTORY_CONFLICT"),
			};
		}
		if (id.StartsWith("object_id:", StringComparison.Ordinal))
			return objects.TryGetValue(id.Substring("object_id:".Length), out var value) && value != null
				? value : throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (id.StartsWith("token:0x", StringComparison.Ordinal)) {
			var resolved = module.ResolveToken(Convert.ToUInt32(id.Substring("token:0x".Length), 16));
			return resolved ?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		throw new EditDomainException("EDIT_HISTORY_CONFLICT");
	}

	sealed class ReferenceComparerForTypes : IEqualityComparer<ITypeDefOrRef> {
		public static readonly ReferenceComparerForTypes Instance = new();
		public bool Equals(ITypeDefOrRef? x, ITypeDefOrRef? y) => ReferenceEquals(x, y);
		public int GetHashCode(ITypeDefOrRef obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	static int SingleToBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
	static float BitsToSingle(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

	static object? InverseConstant(Constant constant) {
		var value = constant.Value;
		var kind = value switch {
			null => "null", bool => "boolean", char => "char", string => "string", float => "r4", double => "r8",
			sbyte => "i1", byte => "u1", short => "i2", ushort => "u2", int => "i4", uint => "u4", long => "i8", ulong => "u8",
			_ => throw new NotSupportedException("Unsupported constant in inverse"),
		};
		// Floating point bit patterns (NaN payloads, negative zero) cannot survive
		// JSON numbers; chars are code units so lone surrogates survive as well.
		object? encoded = value switch {
			float single => SingleToBits(single).ToString("x8"),
			double dual => BitConverter.DoubleToInt64Bits(dual).ToString("x16"),
			char c => (int)c,
			_ => value,
		};
		return new Dictionary<string, object?> { ["kind"] = kind, ["value"] = encoded };
	}

	static Constant RestoreInverseConstant(JsonElement node) {
		var kind = RequiredString(node, "kind");
		var value = node.TryGetProperty("value", out var raw) ? raw : (JsonElement?)null;
		object? restored = kind switch {
			"null" => null,
			"boolean" => value!.Value.GetBoolean(),
			"char" => (char)value!.Value.GetInt32(),
			"string" => value!.Value.GetString(),
			"r4" => BitsToSingle(Convert.ToInt32(value!.Value.GetString(), 16)),
			"r8" => BitConverter.Int64BitsToDouble(Convert.ToInt64(value!.Value.GetString(), 16)),
			"i1" => value!.Value.GetSByte(),
			"u1" => value!.Value.GetByte(),
			"i2" => value!.Value.GetInt16(),
			"u2" => value!.Value.GetUInt16(),
			"i4" => value!.Value.GetInt32(),
			"u4" => value!.Value.GetUInt32(),
			"i8" => value!.Value.GetInt64(),
			"u8" => value!.Value.GetUInt64(),
			_ => throw new EditDomainException("EDIT_HISTORY_CONFLICT"),
		};
		return new ConstantUser(restored);
	}
}
