using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>T002-R02 / CHK-021: complete content projection for the transient
/// external guard.  Every bound dnlib PdbCustomDebugInfo type is encoded
/// explicitly (no reflection/ToString); instructions, locals and methods are
/// bound to deterministic module-internal owner paths; lists keep their full
/// length and order; shared and cyclic graphs use stable traversal ids with
/// back references.  A projection that cannot be formed completely fails with
/// EDIT_CAPABILITY_UNAVAILABLE instead of producing a hash that looks
/// successful.  The frozen semantic fingerprints never consume this class, so
/// Compute/ComputeRoundtrip and every persisted checkpoint value are
/// byte-identical.</summary>
internal sealed class EditCdiGuard {
	readonly ModuleDef module;
	readonly Dictionary<object, int> ids = new(ReferenceComparer.Instance);
	readonly Dictionary<object, InstructionOwner> instructionOwners = new(ReferenceComparer.Instance);
	readonly Dictionary<object, LocalOwner> localOwners = new(ReferenceComparer.Instance);
	int nextId;

	static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
		WriteIndented = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
	};

	sealed class InstructionOwner {
		public MethodDef Method = null!;
		public int Index;
		public bool Ambiguous;
	}

	sealed class LocalOwner {
		public MethodDef Method = null!;
		public int Index;
		public bool Ambiguous;
	}

	sealed class ReferenceComparer : IEqualityComparer<object> {
		public static readonly ReferenceComparer Instance = new();
		bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
		int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	// The bound dnlib marks one CDI type internal (the async stepping-information
	// row).  Its type identity is pinned to dnlib 4.5.0 and its two content
	// members stay typed (Instruction / IList<PdbAsyncStepInfo>); no arbitrary
	// property reflection or ToString is used for the payload.
	static readonly Type? SteppingInformationType = typeof(PdbCustomDebugInfo).Assembly.GetType("dnlib.DotNet.Pdb.PdbAsyncMethodSteppingInformationCustomDebugInfo");
	static readonly PropertyInfo? SteppingInformationCatchHandler = SteppingInformationType?.GetProperty("CatchHandler", BindingFlags.Public | BindingFlags.Instance);
	static readonly PropertyInfo? SteppingInformationStepInfos = SteppingInformationType?.GetProperty("AsyncStepInfos", BindingFlags.Public | BindingFlags.Instance);
	static HashSet<Type>? supportedCdiTypes;

	EditCdiGuard(ModuleDef module) => this.module = module;

	/// <summary>One guard row per CDI owner that actually carries rows; the
	/// module owner is always present so an empty list stays observable.</summary>
	public static IList<string> Rows(ModuleDef module) {
		var guard = new EditCdiGuard(module);
		var rows = new List<string> { guard.Row("module", module.CustomDebugInfos) };
		foreach (var type in module.GetTypes()) {
			var typePath = guard.TypePath(type);
			guard.AddOwnerRow(rows, "t:" + typePath, type.CustomDebugInfos);
			foreach (var method in type.Methods)
				guard.AddOwnerRow(rows, "m:" + guard.MethodPath(method), method.CustomDebugInfos);
			for (var index = 0; index < type.Fields.Count; index++)
				guard.AddOwnerRow(rows, "f:" + typePath + "/f/" + index.ToString(CultureInfo.InvariantCulture), type.Fields[index].CustomDebugInfos);
			for (var index = 0; index < type.Properties.Count; index++)
				guard.AddOwnerRow(rows, "p:" + typePath + "/p/" + index.ToString(CultureInfo.InvariantCulture), type.Properties[index].CustomDebugInfos);
			for (var index = 0; index < type.Events.Count; index++)
				guard.AddOwnerRow(rows, "e:" + typePath + "/e/" + index.ToString(CultureInfo.InvariantCulture), type.Events[index].CustomDebugInfos);
		}
		return rows;
	}

	void AddOwnerRow(List<string> rows, string owner, IList<PdbCustomDebugInfo> infos) {
		if (infos.Count != 0) rows.Add(Row(owner, infos));
	}

	string Row(string owner, IList<PdbCustomDebugInfo> infos) {
		var list = infos.Select(EncodeCdi).ToArray();
		return "cdi|" + owner + "|" + JsonSerializer.Serialize(list, Json);
	}

	object? EncodeCdi(PdbCustomDebugInfo? info) {
		if (info == null) return null;
		try { return EncodeCdiCore(info); }
		catch (EditDomainException) { throw; }
		catch (Exception ex) {
			throw Capability("unreadable CDI " + info.GetType().FullName + ": " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	object EncodeCdiCore(PdbCustomDebugInfo info) {
		// B2: exact bound-type gate.  A derived class of a known CDI type must
		// not slip through the base dispatch with unencoded extra state; only the
		// exact bound types (public concrete + the two exact internal shapes) are
		// supported, and anything else fails before any hash is produced.
		if (!SupportedCdiTypes.Contains(info.GetType())) throw Capability("unsupported CDI type: " + info.GetType().FullName);
		if (ids.TryGetValue(info, out var existing)) return Ref(existing);
		var id = nextId++;
		ids[info] = id;
		var body = new Dictionary<string, object?> {
			["id"] = id,
			["type"] = info.GetType().Name,
			["guid"] = info.Guid.ToString("D"),
			["kind"] = info.Kind.ToString(),
		};
		if (SteppingInformationType != null && info.GetType() == SteppingInformationType)
			return EncodeSteppingInformation(info, body);
		switch (info) {
		case PdbAsyncMethodCustomDebugInfo x:
			body["kickoff_method"] = MethodRef(x.KickoffMethod);
			body["catch_handler"] = InstructionRef(x.CatchHandlerInstruction);
			body["step_infos"] = ArrayOf(x.StepInfos, EncodeAsyncStep);
			break;
		case PdbCompilationMetadataReferencesCustomDebugInfo x:
			body["references"] = ArrayOf(x.References, EncodeCompilationReference);
			break;
		case PdbCompilationOptionsCustomDebugInfo x:
			body["options"] = ArrayOf(x.Options, pair => new Dictionary<string, object?> {
				["key"] = pair.Key,
				["value"] = pair.Value,
			});
			break;
		case PdbDefaultNamespaceCustomDebugInfo x:
			body["namespace"] = x.Namespace;
			break;
		case PdbDynamicLocalVariablesCustomDebugInfo x:
			body["flags"] = ArrayOf(x.Flags, flag => flag);
			break;
		case PdbDynamicLocalsCustomDebugInfo x:
			body["locals"] = ArrayOf(x.Locals, EncodeDynamicLocal);
			break;
		case PdbEditAndContinueLambdaMapCustomDebugInfo x:
			body["data"] = Bytes(x.Data);
			break;
		case PdbEditAndContinueLocalSlotMapCustomDebugInfo x:
			body["data"] = Bytes(x.Data);
			break;
		case PdbEditAndContinueStateMachineStateMapDebugInfo x:
			body["state_machine_states"] = ArrayOf(x.StateMachineStates, state => new Dictionary<string, object?> {
				["syntax_offset"] = state.SyntaxOffset,
				["state"] = state.State.ToString(),
			});
			break;
		case PdbEmbeddedSourceCustomDebugInfo x:
			body["source_code_blob"] = Bytes(x.SourceCodeBlob);
			break;
		case PdbForwardMethodInfoCustomDebugInfo x:
			body["method"] = MethodDefOrRefRef(x.Method);
			break;
		case PdbForwardModuleInfoCustomDebugInfo x:
			body["method"] = MethodDefOrRefRef(x.Method);
			break;
		case PdbIteratorMethodCustomDebugInfo x:
			body["kickoff_method"] = MethodRef(x.KickoffMethod);
			break;
		case PdbSourceLinkCustomDebugInfo x:
			body["file_blob"] = Bytes(x.FileBlob);
			break;
		case PdbSourceServerCustomDebugInfo x:
			body["file_blob"] = Bytes(x.FileBlob);
			break;
		case PdbStateMachineHoistedLocalScopesCustomDebugInfo x:
			body["scopes"] = ArrayOf(x.Scopes, EncodeHoistedScope);
			break;
		case PdbStateMachineTypeNameCustomDebugInfo x:
			body["type"] = TypeDefRef(x.Type);
			break;
		case PdbTupleElementNamesCustomDebugInfo x:
			body["names"] = ArrayOf(x.Names, EncodeTupleNames);
			break;
		// dnlib's internal PdbTypeDefinitionDocumentsDebugInfoMD derives from this
		// public type; the base branch is its semantic encoder.
		case PdbTypeDefinitionDocumentsDebugInfo x:
			body["documents"] = ArrayOf(x.Documents, EncodeDocument);
			break;
		case PdbUnknownCustomDebugInfo x:
			body["data"] = Bytes(x.Data);
			break;
		case PdbUsingGroupsCustomDebugInfo x:
			body["using_counts"] = ArrayOf(x.UsingCounts, count => count);
			break;
		case PortablePdbTupleElementNamesCustomDebugInfo x:
			body["names"] = ArrayOf(x.Names, name => name);
			break;
		case PrimaryConstructorInformationBlobDebugInfo x:
			body["blob"] = Bytes(x.Blob);
			break;
		default:
			throw Capability("unknown custom debug info type: " + info.GetType().FullName);
		}
		return body;
	}

	object EncodeSteppingInformation(PdbCustomDebugInfo info, Dictionary<string, object?> body) {
		var catchHandler = SteppingInformationCatchHandler ?? throw Capability("bound dnlib stepping-information shape changed: CatchHandler missing");
		var stepInfos = SteppingInformationStepInfos ?? throw Capability("bound dnlib stepping-information shape changed: AsyncStepInfos missing");
		try {
			body["catch_handler"] = InstructionRef(catchHandler.GetValue(info) as Instruction);
			var steps = stepInfos.GetValue(info) as System.Collections.IEnumerable;
			body["async_step_infos"] = steps == null ? null : steps.Cast<object?>().Select(step => EncodeAsyncStep((PdbAsyncStepInfo)step!)).ToArray();
			// (internal stepping rows always materialize their list in dnlib 4.5.0)
		}
		catch (Exception ex) when (ex is not EditDomainException) {
			throw Capability("unreadable internal stepping-information CDI: " + ex.GetType().Name + ": " + ex.Message);
		}
		return body;
	}

	object? EncodeAsyncStep(PdbAsyncStepInfo step) => new Dictionary<string, object?> {
		["yield_instruction"] = InstructionRef(step.YieldInstruction),
		["breakpoint_method"] = MethodRef(step.BreakpointMethod),
		["breakpoint_instruction"] = InstructionRef(step.BreakpointInstruction),
	};

	object? EncodeHoistedScope(StateMachineHoistedLocalScope scope) => new Dictionary<string, object?> {
		["start"] = InstructionRef(scope.Start),
		["end"] = InstructionRef(scope.End),
		["is_synthesized_local"] = scope.IsSynthesizedLocal,
	};

	object? EncodeCompilationReference(PdbCompilationMetadataReference reference) {
		if (ids.TryGetValue(reference, out var existing)) return Ref(existing);
		var id = nextId++;
		ids[reference] = id;
		return new Dictionary<string, object?> {
			["id"] = id,
			["name"] = reference.Name,
			["aliases"] = reference.Aliases,
			["flags"] = reference.Flags.ToString(),
			["timestamp"] = reference.Timestamp,
			["size_of_image"] = reference.SizeOfImage,
			["mvid"] = reference.Mvid.ToString("D"),
		};
	}

	object? EncodeDynamicLocal(PdbDynamicLocal local) {
		if (ids.TryGetValue(local, out var existing)) return Ref(existing);
		var id = nextId++;
		ids[local] = id;
		return new Dictionary<string, object?> {
			["id"] = id,
			["name"] = local.Name,
			["flags"] = ArrayOf(local.Flags, flag => flag),
			["is_constant"] = local.IsConstant,
			["is_variable"] = local.IsVariable,
			["local"] = LocalRef(local.Local),
		};
	}

	object? EncodeTupleNames(PdbTupleElementNames names) {
		if (ids.TryGetValue(names, out var existing)) return Ref(existing);
		var id = nextId++;
		ids[names] = id;
		return new Dictionary<string, object?> {
			["id"] = id,
			["name"] = names.Name,
			["is_constant"] = names.IsConstant,
			["is_variable"] = names.IsVariable,
			["local"] = LocalRef(names.Local),
			["scope_start"] = InstructionRef(names.ScopeStart),
			["scope_end"] = InstructionRef(names.ScopeEnd),
			["tuple_element_names"] = ArrayOf(names.TupleElementNames, name => name),
		};
	}

	object? EncodeDocument(PdbDocument document) {
		if (ids.TryGetValue(document, out var existing)) return Ref(existing);
		var id = nextId++;
		ids[document] = id;
		return new Dictionary<string, object?> {
			["id"] = id,
			["url"] = document.Url,
			["language"] = document.Language.ToString("D"),
			["language_vendor"] = document.LanguageVendor.ToString("D"),
			["document_type"] = document.DocumentType.ToString("D"),
			["checksum_algorithm"] = document.CheckSumAlgorithmId.ToString("D"),
			["checksum"] = Bytes(document.CheckSum),
			["md_token"] = document.MDToken?.Raw,
			["custom_debug_infos"] = ArrayOf(document.CustomDebugInfos, EncodeCdi),
		};
	}

	object? InstructionRef(Instruction? instruction) {
		if (instruction == null) return null;
		var owner = InstructionOwnerOf(instruction) ?? throw Capability("dangling instruction reference in CDI content");
		return new Dictionary<string, object?> {
			["method"] = MethodPath(owner.Method),
			["index"] = owner.Index,
		};
	}

	object? LocalRef(Local? local) {
		if (local == null) return null;
		var owner = LocalOwnerOf(local) ?? throw Capability("dangling local reference in CDI content");
		return new Dictionary<string, object?> {
			["method"] = MethodPath(owner.Method),
			["index"] = owner.Index,
		};
	}

	object? MethodRef(MethodDef? method) =>
		method == null ? null : (object)new Dictionary<string, object?> { ["method"] = MethodPath(method) };

	object? MethodDefOrRefRef(IMethodDefOrRef? method) {
		switch (method) {
		case null: return null;
		case MethodDef definition: return new Dictionary<string, object?> { ["kind"] = "method", ["path"] = MethodPath(definition) };
		case MemberRef reference: return new Dictionary<string, object?> {
			["kind"] = "member_ref",
			["parent"] = MemberRefParentRef(reference.Class),
			["name"] = reference.Name?.String,
			["signature"] = CallingConventionRef(reference.Signature),
		};
		case MethodSpec spec: return new Dictionary<string, object?> {
			["kind"] = "method_spec",
			["method"] = MethodDefOrRefRef(spec.Method),
			["generic_arguments"] = spec.GenericInstMethodSig?.GenericArguments.Select(argument => (object?)Sig(argument)).ToArray(),
		};
		default: throw Capability("unsupported method reference kind: " + method.GetType().FullName);
		}
	}

	object? TypeDefOrRefRef(ITypeDefOrRef? type) {
		switch (type) {
		case null: return null;
		case TypeDef definition: return new Dictionary<string, object?> { ["kind"] = "type_def", ["path"] = TypePath(definition) };
		case TypeRef reference: return TypeRefRef(reference);
		case TypeSpec spec: return new Dictionary<string, object?> { ["kind"] = "type_spec", ["signature"] = Sig(spec.TypeSig) };
		default: throw Capability("unsupported type reference kind: " + type.GetType().FullName);
		}
	}

	object? TypeDefRef(TypeDef? type) =>
		type == null ? null : (object)new Dictionary<string, object?> { ["path"] = TypePath(type) };

	string TypePath(TypeDef type) {
		if (type.DeclaringType is TypeDef parent) {
			var nested = parent.NestedTypes.IndexOf(type);
			if (nested < 0) throw Capability("nested type is not part of its declaring type");
			return TypePath(parent) + "/t/" + nested.ToString(CultureInfo.InvariantCulture);
		}
		var top = module.Types.IndexOf(type);
		if (top < 0) throw Capability("type is not part of the module");
		return "t/" + top.ToString(CultureInfo.InvariantCulture);
	}

	string MethodPath(MethodDef method) {
		var type = method.DeclaringType ?? throw Capability("method reference without declaring type");
		var index = type.Methods.IndexOf(method);
		if (index < 0) throw Capability("method is not part of its declaring type");
		return TypePath(type) + "/m/" + index.ToString(CultureInfo.InvariantCulture);
	}

	InstructionOwner? InstructionOwnerOf(Instruction instruction) {
		if (instructionOwners.Count == 0) BuildOwnerMaps();
		if (!instructionOwners.TryGetValue(instruction, out var owner)) return null;
		if (owner.Ambiguous) throw Capability("instruction reference is shared by multiple method bodies");
		return owner;
	}

	LocalOwner? LocalOwnerOf(Local local) {
		if (localOwners.Count == 0) BuildOwnerMaps();
		if (!localOwners.TryGetValue(local, out var owner)) return null;
		if (owner.Ambiguous) throw Capability("local reference is shared by multiple method bodies");
		return owner;
	}

	void BuildOwnerMaps() {
		foreach (var type in module.GetTypes()) {
			foreach (var method in type.Methods) {
				if (!method.HasBody) continue;
				var instructions = method.Body.Instructions;
				for (var index = 0; index < instructions.Count; index++) {
					var instruction = instructions[index];
					if (instructionOwners.TryGetValue(instruction, out var existing)) existing.Ambiguous = true;
					else instructionOwners[instruction] = new InstructionOwner { Method = method, Index = index };
				}
				var variables = method.Body.Variables;
				for (var index = 0; index < variables.Count; index++) {
					var local = variables[index];
					if (localOwners.TryGetValue(local, out var existing)) existing.Ambiguous = true;
					else localOwners[local] = new LocalOwner { Method = method, Index = index };
				}
			}
		}
	}

	static object? ArrayOf<T>(IEnumerable<T>? items, Func<T, object?> encode) =>
		items == null ? null : items.Select(encode).ToArray();

	static Dictionary<string, object?> Ref(int id) => new() { ["ref"] = id };

	static object? Bytes(byte[]? bytes) => bytes == null ? null : (object?)new Dictionary<string, object?> {
		["sha256"] = EditWire.Sha256(bytes),
		["length"] = bytes.LongLength,
	};

	// B1: complete structured signature identity.  Every element is encoded as a
	// JSON object (no flat separator concatenation), so a scope-only change, a
	// modifier identity, a nested TypeRef chain or punctuation inside a name
	// cannot collide with another shape; unknown elements fail explicitly.
	object? MethodSignature(MethodSig? value) {
		if (value == null) return null;
		return new Dictionary<string, object?> {
			["kind"] = "method_sig",
			["calling_convention"] = (byte)value.CallingConvention,
			["has_this"] = value.HasThis,
			["explicit_this"] = value.ExplicitThis,
			["generic_parameter_count"] = value.GenParamCount,
			["return_type"] = Sig(value.RetType),
			["parameters"] = ArrayOf(value.Params, parameter => Sig(parameter)),
			["sentinel_parameters"] = ArrayOf(value.ParamsAfterSentinel, parameter => Sig(parameter)),
		};
	}

	object? CallingConventionRef(CallingConventionSig? signature) {
		switch (signature) {
		case null: return null;
		case MethodSig method: return MethodSignature(method);
		case FieldSig field: return new Dictionary<string, object?> {
			["kind"] = "field_sig",
			["type"] = Sig(field.Type),
		};
		default: throw Capability("unsupported member reference signature kind: " + signature.GetType().FullName);
		}
	}

	object? MemberRefParentRef(IMemberRefParent? parent) {
		switch (parent) {
		case null: return null;
		case TypeDef definition: return new Dictionary<string, object?> { ["kind"] = "type_def", ["path"] = TypePath(definition) };
		case TypeRef reference: return TypeRefRef(reference);
		case TypeSpec spec: return new Dictionary<string, object?> { ["kind"] = "type_spec", ["signature"] = Sig(spec.TypeSig) };
		case ModuleRef moduleRef: return new Dictionary<string, object?> { ["kind"] = "module_ref", ["name"] = moduleRef.Name?.String };
		case MethodDef method: return new Dictionary<string, object?> { ["kind"] = "method_def", ["path"] = MethodPath(method) };
		default: throw Capability("unsupported member reference parent kind: " + parent.GetType().FullName);
		}
	}

	object? TypeRefRef(TypeRef reference) => new Dictionary<string, object?> {
		["kind"] = "type_ref",
		["scope"] = ResolutionScopeRef(reference.ResolutionScope),
		["namespace"] = reference.Namespace?.String,
		["name"] = reference.Name?.String,
	};

	object? ResolutionScopeRef(IResolutionScope? scope) {
		switch (scope) {
		case null: return null;
		case AssemblyRef assembly: return new Dictionary<string, object?> {
			["kind"] = "assembly_ref",
			["name"] = assembly.Name?.String,
			["version"] = assembly.Version?.ToString(),
			["culture"] = assembly.Culture?.String,
			["public_key_or_token"] = Bytes(assembly.PublicKeyOrToken?.Data),
			["attributes"] = (uint)assembly.Attributes,
			["hash"] = Bytes(assembly.Hash),
		};
		case ModuleRef moduleRef: return new Dictionary<string, object?> { ["kind"] = "module_ref", ["name"] = moduleRef.Name?.String };
		case TypeRef nested: return TypeRefRef(nested);
		case ModuleDef definition: return new Dictionary<string, object?> {
			["kind"] = "module_def", ["name"] = definition.Name?.String, ["mvid"] = definition.Mvid?.ToString("D"),
		};
		case AssemblyDef assemblyDefinition: return new Dictionary<string, object?> {
			["kind"] = "assembly_def", ["full_name"] = assemblyDefinition.FullName,
		};
		default: throw Capability("unsupported resolution scope kind: " + scope.GetType().FullName);
		}
	}

	object? Sig(TypeSig? value) {
		switch (value) {
		case null: return null;
		case GenericSig generic: return new Dictionary<string, object?> {
			["kind"] = generic.IsMethodVar ? "mvar" : "var", ["number"] = generic.Number,
		};
		case GenericInstSig instance: return new Dictionary<string, object?> {
			["kind"] = "generic_inst", ["generic_type"] = Sig(instance.GenericType),
			["arguments"] = ArrayOf(instance.GenericArguments, argument => Sig(argument)),
		};
		case TypeDefOrRefSig type: return new Dictionary<string, object?> {
			["kind"] = "type_def_or_ref", ["element_type"] = type.ElementType.ToString(), ["type"] = TypeDefOrRefRef(type.TypeDefOrRef),
		};
		case FnPtrSig function: return new Dictionary<string, object?> {
			["kind"] = "fnptr", ["signature"] = CallingConventionRef(function.Signature),
		};
		case ArraySig array: return new Dictionary<string, object?> {
			["kind"] = "array", ["next"] = Sig(array.Next), ["rank"] = array.Rank,
			["sizes"] = ArrayOf(array.Sizes, size => size), ["lower_bounds"] = ArrayOf(array.LowerBounds, bound => bound),
		};
		case SZArraySig szarray: return new Dictionary<string, object?> { ["kind"] = "szarray", ["next"] = Sig(szarray.Next) };
		case PtrSig pointer: return new Dictionary<string, object?> { ["kind"] = "ptr", ["next"] = Sig(pointer.Next) };
		case ByRefSig byref: return new Dictionary<string, object?> { ["kind"] = "byref", ["next"] = Sig(byref.Next) };
		case PinnedSig pinned: return new Dictionary<string, object?> { ["kind"] = "pinned", ["next"] = Sig(pinned.Next) };
		case CModReqdSig required: return new Dictionary<string, object?> {
			["kind"] = "modreq", ["modifier"] = TypeDefOrRefRef(required.Modifier), ["next"] = Sig(required.Next),
		};
		case CModOptSig optional: return new Dictionary<string, object?> {
			["kind"] = "modopt", ["modifier"] = TypeDefOrRefRef(optional.Modifier), ["next"] = Sig(optional.Next),
		};
		case SentinelSig: return new Dictionary<string, object?> { ["kind"] = "sentinel" };
		case ModuleSig moduleSignature: return new Dictionary<string, object?> {
			["kind"] = "module_sig", ["index"] = moduleSignature.Index, ["next"] = Sig(moduleSignature.Next),
		};
		default: throw Capability("unsupported signature element: " + value.GetType().FullName);
		}
	}

	static HashSet<Type> SupportedCdiTypes => supportedCdiTypes ??= BuildSupportedCdiTypes();

	static HashSet<Type> BuildSupportedCdiTypes() {
		var types = new HashSet<Type> {
			typeof(PdbAsyncMethodCustomDebugInfo),
			typeof(PdbCompilationMetadataReferencesCustomDebugInfo),
			typeof(PdbCompilationOptionsCustomDebugInfo),
			typeof(PdbDefaultNamespaceCustomDebugInfo),
			typeof(PdbDynamicLocalVariablesCustomDebugInfo),
			typeof(PdbDynamicLocalsCustomDebugInfo),
			typeof(PdbEditAndContinueLambdaMapCustomDebugInfo),
			typeof(PdbEditAndContinueLocalSlotMapCustomDebugInfo),
			typeof(PdbEditAndContinueStateMachineStateMapDebugInfo),
			typeof(PdbEmbeddedSourceCustomDebugInfo),
			typeof(PdbForwardMethodInfoCustomDebugInfo),
			typeof(PdbForwardModuleInfoCustomDebugInfo),
			typeof(PdbIteratorMethodCustomDebugInfo),
			typeof(PdbSourceLinkCustomDebugInfo),
			typeof(PdbSourceServerCustomDebugInfo),
			typeof(PdbStateMachineHoistedLocalScopesCustomDebugInfo),
			typeof(PdbStateMachineTypeNameCustomDebugInfo),
			typeof(PdbTupleElementNamesCustomDebugInfo),
			typeof(PdbTypeDefinitionDocumentsDebugInfo),
			typeof(PdbUnknownCustomDebugInfo),
			typeof(PdbUsingGroupsCustomDebugInfo),
			typeof(PortablePdbTupleElementNamesCustomDebugInfo),
			typeof(PrimaryConstructorInformationBlobDebugInfo),
		};
		var assembly = typeof(PdbCustomDebugInfo).Assembly;
		foreach (var name in new[] {
			"dnlib.DotNet.Pdb.PdbAsyncMethodSteppingInformationCustomDebugInfo",
			"dnlib.DotNet.Pdb.PdbTypeDefinitionDocumentsDebugInfoMD",
		}) {
			types.Add(assembly.GetType(name) ?? throw Capability("bound dnlib is missing CDI type " + name));
		}
		return types;
	}

	static EditDomainException Capability(string reason) => new("EDIT_CAPABILITY_UNAVAILABLE",
		new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "external_guard_cdi", ["reason"] = reason });
}
