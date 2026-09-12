using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P06 importer core (IMP-002/003): compiles compile-artifact members into
/// frozen P02/P03 operation payloads against the transaction private copy.
/// The compile pass is pure — every rejection (missing artifact member,
/// ambiguous target, unmappable reference, symbol row outside the domain)
/// fires before any private-module write, so a rejected edit_import leaves the
/// transaction untouched.  Operation references use the frozen grammar only:
/// tokens for rows that already exist in the target, object IDs for rows an
/// earlier operation of this import creates.  Any metadata operand that cannot
/// be mapped is a hard rejection (ACC-005: 任一引用未映射＝失败).
/// </summary>
internal sealed class EditCSharpImporter : IDisposable {
	readonly ModuleDef artifact;
	readonly ModuleDef target;
	readonly EditImportMatcher matcher;
	readonly Dictionary<string, IMDTokenProvider> objectIds;
	readonly int baseOperationIndex;
	readonly List<PlanRow> rows = new();
	string TargetAssemblyName => target.Assembly?.Name?.String ?? string.Empty;
	// artifact row -> operation reference text ("0x…" for an existing target
	// row, "obj-…" for a row created by an earlier operation of this import)
	readonly Dictionary<IMDTokenProvider, string> referenceText = new(ReferenceTextComparer.Instance);
	readonly HashSet<string> createdTypeNames = new(StringComparer.Ordinal);
	readonly Dictionary<TypeDef, TypeDef?> generatedTargets = new(ReferenceTextComparer.Instance);
	readonly Dictionary<string, string> memberRefCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> typeRefCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> methodSpecCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> typeSpecCache = new(StringComparer.Ordinal);

	sealed class ReferenceTextComparer : IEqualityComparer<IMDTokenProvider> {
		public static readonly ReferenceTextComparer Instance = new();
		public bool Equals(IMDTokenProvider? x, IMDTokenProvider? y) => ReferenceEquals(x, y);
		public int GetHashCode(IMDTokenProvider obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	public sealed class PlanRow {
		public Dictionary<string, object?> Operation = new();
		public string Kind = string.Empty;
		public string ArtifactMember = string.Empty;
		public string Target = string.Empty;
	}

	public EditCSharpImporter(ModuleDef artifact, ModuleDef target, Dictionary<string, IMDTokenProvider> objectIds, int baseOperationIndex) {
		this.artifact = artifact;
		this.target = target;
		this.objectIds = objectIds;
		this.baseOperationIndex = baseOperationIndex;
		matcher = new EditImportMatcher(artifact, target);
	}

	public void Dispose() => artifact.Dispose();

	public IReadOnlyList<PlanRow> Compile(JsonElement targets) {
		if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() == 0)
			throw Reject("targets", "targets must be a non-empty array");
		if (targets.GetArrayLength() > EditWire.MaxOperations)
			throw Reject("targets", "targets exceed the operation capacity");
		foreach (var row in targets.EnumerateArray())
			ImportRow(row);
		return rows;
	}

	void ImportRow(JsonElement row) {
		if (row.ValueKind != JsonValueKind.Object) throw Reject("targets", "each target row must be an object");
		var compiled = RequiredString(row, "compiled");
		var action = RequiredString(row, "action");
		var reference = EditImportMatcher.ParseCompiled(compiled);
		if (action == "replace_body") {
			if (reference.MemberName.Length == 0)
				throw Reject("action", "replace_body needs a member reference");
			ReplaceBody(matcher.ArtifactMethod(reference), OptionalTargetRef(row, "target"));
			return;
		}
		if (action != "add") throw Reject("action", "action must be replace_body or add");
		if (reference.MemberName.Length == 0) {
			AddType(reference);
			return;
		}
		var owner = matcher.ArtifactType(reference.TypeFullName);
		var methods = owner.Methods.Where(m => string.Equals(m.Name.String, reference.MemberName, StringComparison.Ordinal)
			&& m.GenericParameters.Count == reference.Arity).ToArray();
		var fields = owner.Fields.Where(f => string.Equals(f.Name.String, reference.MemberName, StringComparison.Ordinal)).ToArray();
		if (methods.Length + fields.Length != 1)
			throw Reject("compiled", "the compiled reference must address exactly one member for add");
		if (methods.Length == 1) AddMethod(methods[0], OptionalTargetRef(row, "target"));
		else AddField(fields[0], OptionalTargetRef(row, "target"));
	}

	// ---------------------------------------------------------------- targets

	Dictionary<string, object?>? OptionalTargetRef(JsonElement row, string name) {
		if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
		return TargetRef(value);
	}

	Dictionary<string, object?> TargetRef(JsonElement value) {
		if (value.ValueKind != JsonValueKind.Object) throw Reject("target", "target must be an object");
		if (value.TryGetProperty("token", out var token))
			return new Dictionary<string, object?> { ["token"] = RequireToken(token.GetString()!) };
		if (value.TryGetProperty("object_id", out var id))
			return new Dictionary<string, object?> { ["object_id"] = RequireText(id) };
		throw Reject("target", "target must carry a token or an object_id");
	}

	static string RequireToken(string text) {
		if (text.Length != 10 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			|| !uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
			throw Reject("target", "target token must be 0x plus eight hex digits");
		return text;
	}
	static string RequireText(JsonElement element) =>
		element.ValueKind == JsonValueKind.String && element.GetString()!.Length != 0
			? element.GetString()! : throw Reject("target", "a target string field is empty");

	IMDTokenProvider ResolveTarget(Dictionary<string, object?> reference) {
		if (reference.TryGetValue("token", out var token))
			return target.ResolveToken(uint.Parse(((token as string)!).Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
		return objectIds.TryGetValue((reference["object_id"] as string)!, out var found)
			? found : throw Reject("target", "the target object_id does not exist in this transaction");
	}

	// ------------------------------------------------------------ replace_body

	void ReplaceBody(MethodDef artifactMethod, Dictionary<string, object?>? explicitTarget) {
		var artifactOwner = artifactMethod.DeclaringType ?? throw Reject("compiled", "the compiled method has no declaring type");
		MethodDef targetMethod;
		if (explicitTarget != null) {
			targetMethod = ResolveTarget(explicitTarget) as MethodDef ?? throw Reject("target", "the explicit target is not a method");
			if (!string.Equals(targetMethod.DeclaringType?.FullName, artifactOwner.FullName, StringComparison.Ordinal)
				|| !string.Equals(matcher.TargetMethodKey(targetMethod), matcher.MethodKey(artifactMethod), StringComparison.Ordinal))
				throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> {
					["kind"] = "import_target_mismatch", ["expected"] = artifactMethod.FullName, ["actual"] = targetMethod.FullName });
		}
		else {
			var targetOwner = TargetFor(artifactOwner)
				?? throw Reject("match", "the member's declaring type has no target counterpart: " + artifactOwner.FullName);
			targetMethod = matcher.MatchTargetMethod(artifactMethod, targetOwner);
		}
		EditImportMatcher.VerifyAccessorPair(artifactMethod, targetMethod);
		var stateMachine = EditImportMatcher.StateMachineType(artifactMethod) ?? StateMachineByPattern(artifactMethod);
		if (stateMachine != null)
			SyncGeneratedSubtree(stateMachine, artifactMethod);
		if (artifactMethod.HasBody)
			EmitBodyReplace(artifactMethod, targetMethod);
		else if (targetMethod.HasBody)
			throw Reject("match", "the compiled member has no body but the target does: " + artifactMethod.FullName);
	}

	/// <summary>Generated-subtree unit: every compiled member of the generated
	/// type (and its generated siblings) lands on its target counterpart —
	/// fields that are missing are added, missing methods are added, shared
	/// methods get their bodies replaced.  A generated type with no target
	/// counterpart is added whole when it is a plain class (closure); new state
	/// machine subtrees (interfaces plus generic-instantiation rows) are outside
	/// the frozen operation language and are rejected.</summary>
	void SyncGeneratedSubtree(TypeDef generated, MethodDef kickoff) {
		var counterpart = TargetFor(generated);
		if (counterpart == null) {
			if (EditImportMatcher.IsStateMachineType(generated))
				throw Reject("generated_subtree", "the compiled state machine has no target counterpart, and new generated state machine subtrees are outside the importable domain: " + generated.FullName);
			AddTypeSubtree(generated, ContainerFor(generated));
			return;
		}
		// Interface-driven accessor rows (IEnumerator.Current pairs) exist on both
		// compilers' state machines and stay the target's; the subtree sync lands
		// fields and method bodies.  A property/event the counterpart lacks is a
		// shape the frozen operation language cannot reconcile — reject.
		foreach (var property in generated.Properties)
			if (!counterpart.Properties.Any(p => string.Equals(p.Name.String, property.Name.String, StringComparison.Ordinal)))
				throw Reject("generated_subtree", "the generated state machine property has no target counterpart: " + property.FullName);
		foreach (var evt in generated.Events)
			if (!counterpart.Events.Any(e => string.Equals(e.Name.String, evt.Name.String, StringComparison.Ordinal)))
				throw Reject("generated_subtree", "the generated state machine event has no target counterpart: " + evt.FullName);
		foreach (var field in generated.Fields) {
			if (counterpart.Fields.Any(f => string.Equals(f.Name.String, field.Name.String, StringComparison.Ordinal))) continue;
			EmitFieldAdd(field, RefOf(counterpart));
		}
		foreach (var method in generated.Methods) {
			var existing = counterpart.Methods.FirstOrDefault(m => string.Equals(m.Name.String, method.Name.String, StringComparison.Ordinal)
				&& string.Equals(matcher.TargetMethodKey(m), matcher.MethodKey(method), StringComparison.Ordinal));
			if (existing == null) EmitMethodAdd(method, RefOf(counterpart));
			else if (method.HasBody) EmitBodyReplace(method, existing);
		}
		foreach (var nested in generated.NestedTypes)
			SyncGeneratedSubtree(nested, null!);
	}

	/// <summary>Container reference for a member's declaring type: the token of
	/// the target counterpart, or the object ID when this import created it.</summary>
	Dictionary<string, object?> OwnerRefFor(TypeDef artifactType) {
		if (referenceText.TryGetValue(artifactType, out var created))
			return new Dictionary<string, object?> { ["object_id"] = created };
		var targetOwner = TargetFor(artifactType)
			?? throw Reject("match", "the member's declaring type has no target counterpart: " + artifactType.FullName);
		return TokenRef(targetOwner);
	}

	/// <summary>Container reference for a new type_add: null at module level, the
	/// parent's reference when nested.</summary>
	Dictionary<string, object?>? ContainerFor(TypeDef artifactType) {
		var declaring = artifactType.DeclaringType;
		return declaring == null ? null : OwnerRefFor(declaring);
	}

	/// <summary>Attribute-independent state machine discovery: the kickoff's
	/// own generated type by kickoff-name pattern (exactly one candidate).</summary>
	static TypeDef? StateMachineByPattern(MethodDef kickoff) {
		var owner = kickoff.DeclaringType;
		if (owner == null || !EditImportMatcher.IsGeneratedType(kickoff.DeclaringType)) {
			if (owner == null) return null;
			var prefix = "<" + kickoff.Name.String + ">";
			var matches = owner.NestedTypes.Where(t => {
				var name = t.Name.String;
				return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
					&& name.Substring(prefix.Length).StartsWith("d__", StringComparison.Ordinal)
					&& int.TryParse(name.Substring(prefix.Length + 3), out _);
			}).ToArray();
			return matches.Length == 1 ? matches[0] : null;
		}
		return null;
	}

	// ------------------------------------------------------------------- adds

	void AddType(EditImportMatcher.CompiledReference reference) {
		var artifactType = matcher.ArtifactType(reference.TypeFullName);
		if (matcher.TargetType(artifactType) != null)
			throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> {
				["kind"] = "import_target_exists", ["target"] = artifactType.FullName });
		AddTypeSubtree(artifactType, ContainerFor(artifactType)!);
	}

	/// <summary>Whole-subtree add for a new type: type_add, then fields, then
	/// accessor methods, then plain methods, then property and event rows, then
	/// zero-argument custom attributes.  Generic types, interface lists,
	/// layouts and parameterized attributes are outside the frozen operation
	/// language and are rejected.</summary>
	void AddTypeSubtree(TypeDef artifactType, Dictionary<string, object?>? container) {
		if (artifactType.GenericParameters.Count != 0)
			throw Reject("add", "generic type adds are outside the frozen operation language: " + artifactType.FullName);
		if (artifactType.Interfaces.Count != 0)
			throw Reject("add", "types with interface lists cannot be added by the frozen operation language: " + artifactType.FullName);
		if (artifactType.ClassLayout != null || artifactType.DeclSecurities.Count != 0)
			throw Reject("add", "the type shape is outside the frozen operation language: " + artifactType.FullName);
		RejectCustomAttributes(artifactType.FullName, artifactType.CustomAttributes);
		var self = EmitTypeAdd(artifactType, container);
		foreach (var field in artifactType.Fields) EmitFieldAdd(field, self);
		foreach (var property in artifactType.Properties) {
			if (property.GetMethod != null) EmitMethodAdd(property.GetMethod, self);
			if (property.SetMethod != null) EmitMethodAdd(property.SetMethod, self);
		}
		foreach (var evt in artifactType.Events) {
			if (evt.AddMethod != null) EmitMethodAdd(evt.AddMethod, self);
			if (evt.RemoveMethod != null) EmitMethodAdd(evt.RemoveMethod, self);
			if (evt.InvokeMethod != null) EmitMethodAdd(evt.InvokeMethod, self);
		}
		foreach (var method in artifactType.Methods.Where(m => !IsAccessor(m, artifactType)))
			EmitMethodAdd(method, self);
		foreach (var property in artifactType.Properties) EmitPropertyAdd(property, self);
		foreach (var evt in artifactType.Events) EmitEventAdd(evt, self);
		foreach (var nested in artifactType.NestedTypes)
			AddTypeSubtree(nested, self);
	}

	static bool IsAccessor(MethodDef method, TypeDef owner) =>
		owner.Properties.Any(p => ReferenceEquals(p.GetMethod, method) || ReferenceEquals(p.SetMethod, method))
		|| owner.Events.Any(e => ReferenceEquals(e.AddMethod, method) || ReferenceEquals(e.RemoveMethod, method) || ReferenceEquals(e.InvokeMethod, method));

	void AddMethod(MethodDef artifactMethod, Dictionary<string, object?>? explicitContainer) {
		if (EditImportMatcher.StateMachineType(artifactMethod) != null)
			throw Reject("add", "new async/iterator members are outside the importable domain: state machine subtrees need interface and generic-instantiation rows");
		var artifactOwner = artifactMethod.DeclaringType!;
		Dictionary<string, object?> ownerRef;
		if (explicitContainer != null) {
			var targetOwner = ResolveTarget(explicitContainer) as TypeDef
				?? throw Reject("target", "the add container must be a type");
			if (!string.Equals(targetOwner.FullName, artifactOwner.FullName, StringComparison.Ordinal))
				throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> {
					["kind"] = "import_target_mismatch", ["expected"] = artifactOwner.FullName, ["actual"] = targetOwner.FullName });
			ownerRef = TokenRef(targetOwner);
		}
		else {
			ownerRef = OwnerRefFor(artifactOwner);
		}
		EnsureNoSignatureConflict(artifactMethod, ownerRef);
		EmitMethodAdd(artifactMethod, ownerRef);
		var property = artifactOwner.Properties.FirstOrDefault(p => ReferenceEquals(p.GetMethod, artifactMethod) || ReferenceEquals(p.SetMethod, artifactMethod));
		if (property != null && !TargetPropertyExists(property))
			EmitPropertyAdd(property, ownerRef);
		var evt = artifactOwner.Events.FirstOrDefault(e => ReferenceEquals(e.AddMethod, artifactMethod) || ReferenceEquals(e.RemoveMethod, artifactMethod));
		if (evt != null && !TargetEventExists(evt))
			EmitEventAdd(evt, ownerRef);
	}

	void AddField(FieldDef artifactField, Dictionary<string, object?>? explicitContainer) {
		var artifactOwner = artifactField.DeclaringType!;
		Dictionary<string, object?> ownerRef;
		if (explicitContainer != null) {
			var targetOwner = ResolveTarget(explicitContainer) as TypeDef
				?? throw Reject("target", "the add container must be a type");
			if (!string.Equals(targetOwner.FullName, artifactOwner.FullName, StringComparison.Ordinal))
				throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> {
					["kind"] = "import_target_mismatch", ["expected"] = artifactOwner.FullName, ["actual"] = targetOwner.FullName });
			ownerRef = TokenRef(targetOwner);
		}
		else ownerRef = OwnerRefFor(artifactOwner);
		EmitFieldAdd(artifactField, ownerRef);
	}

	bool TargetPropertyExists(PropertyDef artifactProperty) =>
		matcher.TargetType(artifactProperty.DeclaringType!)?.Properties.Any(p =>
			string.Equals(p.Name.String, artifactProperty.Name.String, StringComparison.Ordinal)) == true;

	bool TargetEventExists(EventDef artifactEvent) =>
		matcher.TargetType(artifactEvent.DeclaringType!)?.Events.Any(e =>
			string.Equals(e.Name.String, artifactEvent.Name.String, StringComparison.Ordinal)) == true;

	void EnsureNoSignatureConflict(MethodDef artifactMethod, Dictionary<string, object?> ownerRef) {
		var ownerName = ownerRef.TryGetValue("object_id", out var objectId) ? CreatedTypeName(objectId as string ?? string.Empty)
			: TypeNameOfToken(ownerRef);
		var targetOwner = ownerName == null ? null : matcher.TargetType(ownerName);
		if (targetOwner == null) return;
		var conflict = targetOwner.Methods.Any(m => string.Equals(m.Name.String, artifactMethod.Name.String, StringComparison.Ordinal)
			&& m.GenericParameters.Count == artifactMethod.GenericParameters.Count
			&& string.Equals(matcher.TargetMethodKey(m), matcher.MethodKey(artifactMethod), StringComparison.Ordinal));
		if (conflict)
			throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> {
				["kind"] = "import_target_exists", ["target"] = artifactMethod.FullName });
	}

	string? TypeNameOfToken(Dictionary<string, object?> reference) {
		var resolved = ResolveTarget(reference);
		return resolved is TypeDef type ? type.FullName : null;
	}

	string? CreatedTypeName(string objectId) => null;  // conflict checks against not-yet-applied adds are covered by later staging validation

	// ------------------------------------------------------------ op emission

	int NextIndex => baseOperationIndex + rows.Count;

	Dictionary<string, object?> TokenRef(IMDTokenProvider row) =>
		new() { ["token"] = "0x" + row.MDToken.Raw.ToString("x8", CultureInfo.InvariantCulture) };

	Dictionary<string, object?> ObjectRef(int operationIndex) =>
		new() { ["object_id"] = "obj-" + operationIndex.ToString("D3", CultureInfo.InvariantCulture) + "-00" };

	Dictionary<string, object?> RefOf(IMDTokenProvider targetRow) => TokenRef(targetRow);

	void Record(Dictionary<string, object?> operation, string kind, IMDTokenProvider? createdRow, string artifactMember, string target) {
		rows.Add(new PlanRow { Operation = operation, Kind = kind, ArtifactMember = artifactMember, Target = target });
		if (createdRow != null) {
			var id = (operation["__object_id"] as string)!;
			operation.Remove("__object_id");
			if (!referenceText.ContainsKey(createdRow)) referenceText[createdRow] = id;
		}
	}

	Dictionary<string, object?> EmitTypeAdd(TypeDef artifactType, Dictionary<string, object?>? container) {
		var index = NextIndex;
		var operation = new Dictionary<string, object?> {
			["kind"] = "type_add",
			["name"] = artifactType.Name.String,
			["namespace"] = artifactType.Namespace?.String ?? string.Empty,
			["attributes"] = (uint)artifactType.Attributes,
			["__object_id"] = ObjectId(index),
		};
		if (artifactType.BaseType != null)
			operation["base_type"] = ResolveTypeText(artifactType.BaseType.ToTypeSig() ?? throw Reject("add", "the base type signature is outside the importable domain"));
		if (container != null) operation["owner_type"] = container;
		Record(operation, "type_add", artifactType, artifactType.FullName, container == null ? "module" : "nested");
		createdTypeNames.Add(artifactType.FullName);
		return ObjectRef(index);
	}

	static string ObjectId(int index) => "obj-" + index.ToString("D3", CultureInfo.InvariantCulture) + "-00";

	void EmitFieldAdd(FieldDef field, Dictionary<string, object?> ownerRef) {
		if (field.Constant != null)
			throw Reject("add", "literal fields are outside the importable member domain: " + field.FullName);
		if (field.MarshalType != null || field.FieldOffset != null || (field.InitialValue != null && field.InitialValue.Length != 0))
			throw Reject("add", "the field shape is outside the frozen operation language: " + field.FullName);
		RejectCustomAttributes(field.FullName, field.CustomAttributes);
		var index = NextIndex;
		var operation = new Dictionary<string, object?> {
			["kind"] = "field_add",
			["owner_type"] = ownerRef,
			["field_type"] = ResolveTypeText(field.FieldType),
			["name"] = field.Name.String,
			["attributes"] = (uint)field.Attributes,
			["__object_id"] = ObjectId(index),
		};
		Record(operation, "field_add", field, field.FullName, field.FullName);
	}

	void EmitMethodAdd(MethodDef method, Dictionary<string, object?> ownerRef) {
		if (method.Overrides.Count != 0)
			throw Reject("add", "explicit override mappings are outside the importable member domain: " + method.FullName);
		if (method.ImplMap != null || method.DeclSecurities.Count != 0)
			throw Reject("add", "the method shape is outside the frozen operation language: " + method.FullName);
		RejectCustomAttributes(method.FullName, method.CustomAttributes);
		var index = NextIndex;
		var ownerArity = method.DeclaringType?.GenericParameters.Count ?? 0;
		var signature = new Dictionary<string, object?> {
			["return_type"] = ResolveTypeText(method.MethodSig.RetType, ownerArity, method.GenericParameters.Count),
			["parameters"] = method.MethodSig.Params.Select((p, i) => new Dictionary<string, object?> {
				["type"] = ResolveTypeText(p, ownerArity, method.GenericParameters.Count),
				["name"] = ParamName(method, i),
			}).ToArray(),
			["has_this"] = method.MethodSig.HasThis,
			["generic_parameters"] = method.GenericParameters.Select(gp => new Dictionary<string, object?> {
				["name"] = gp.Name?.String ?? string.Empty,
				["attributes"] = (uint)gp.Flags,
				["constraints"] = gp.GenericParamConstraints.Count == 0 ? null
					: gp.GenericParamConstraints.Select(c => ResolveTypeText(c.Constraint?.ToTypeSig(), ownerArity, method.GenericParameters.Count)).ToArray(),
			}).ToArray(),
		};
		var operation = new Dictionary<string, object?> {
			["kind"] = "method_add",
			["owner_type"] = ownerRef,
			["name"] = method.Name.String,
			["signature"] = signature,
			["attributes"] = (uint)method.Attributes,
			["impl_attributes"] = (uint)method.ImplAttributes,
			["__object_id"] = ObjectId(index),
		};
		if (method.HasBody) operation["body"] = EncodeBody(method);
		var cdi = EditPdbTransferCodec.CaptureMethodDebugInfo(method, BindReference);
		if (cdi.Count != 0) operation["custom_debug_infos"] = cdi;
		Record(operation, "method_add", method, method.FullName, method.FullName);
	}

	static string ParamName(MethodDef method, int index) {
		var sequence = (ushort)(index + 1);
		return method.ParamDefs.FirstOrDefault(p => p.Sequence == sequence)?.Name?.String ?? string.Empty;
	}

	void EmitPropertyAdd(PropertyDef property, Dictionary<string, object?> ownerRef) {
		var getter = property.GetMethod == null ? null : AccessorRef(property.GetMethod);
		var setter = property.SetMethod == null ? null : AccessorRef(property.SetMethod);
		if (getter == null && setter == null)
			throw Reject("add", "a property without accessors is outside the importable domain: " + property.FullName);
		var operation = new Dictionary<string, object?> {
			["kind"] = "property_add",
			["owner_type"] = ownerRef,
			["name"] = property.Name.String,
			["property_type"] = ResolveTypeText(property.PropertySig.RetType),
			["attributes"] = (uint)property.Attributes,
		};
		if (property.PropertySig.Params.Count != 0)
			operation["index_parameter_types"] = property.PropertySig.Params.Select(p => ResolveTypeText(p)).ToArray();
		if (getter != null) operation["getter"] = new Dictionary<string, object?> { ["object_id"] = getter };
		if (setter != null) operation["setter"] = new Dictionary<string, object?> { ["object_id"] = setter };
		Record(operation, "property_add", null, property.FullName, property.FullName);
	}

	void EmitEventAdd(EventDef evt, Dictionary<string, object?> ownerRef) {
		var add = AccessorRef(evt.AddMethod ?? throw Reject("add", "an event without an add accessor is outside the importable domain: " + evt.FullName));
		var remove = AccessorRef(evt.RemoveMethod ?? throw Reject("add", "an event without a remove accessor is outside the importable domain: " + evt.FullName));
		var operation = new Dictionary<string, object?> {
			["kind"] = "event_add",
			["owner_type"] = ownerRef,
			["name"] = evt.Name.String,
			["event_type"] = ResolveTypeText(evt.EventType?.ToTypeSig() ?? throw Reject("add", "the event type signature is outside the importable domain")),
			["attributes"] = (uint)evt.Attributes,
			["add_method"] = new Dictionary<string, object?> { ["object_id"] = add },
			["remove_method"] = new Dictionary<string, object?> { ["object_id"] = remove },
		};
		if (evt.InvokeMethod != null) operation["raise_method"] = new Dictionary<string, object?> { ["object_id"] = AccessorRef(evt.InvokeMethod) };
		Record(operation, "event_add", null, evt.FullName, evt.FullName);
	}

	string AccessorRef(MethodDef accessor) =>
		referenceText.TryGetValue(accessor, out var text) ? text : throw Reject("internal", "an accessor reference was not created by this import");

	void EmitBodyReplace(MethodDef artifactMethod, MethodDef targetMethod) {
		var operation = new Dictionary<string, object?> {
			["kind"] = "method_body_replace",
			["target"] = TokenRef(targetMethod),
			["body"] = EncodeBody(artifactMethod),
		};
		var cdi = EditPdbTransferCodec.CaptureMethodDebugInfo(artifactMethod, BindReference);
		if (cdi.Count != 0) operation["custom_debug_infos"] = cdi;
		Record(operation, "method_body_replace", null, artifactMethod.FullName, targetMethod.FullName);
	}

	// ------------------------------------------------------------- body encode

	Dictionary<string, object?> EncodeBody(MethodDef method) {
		var body = method.Body ?? throw Reject("body", "the compiled member has no body: " + method.FullName);
		var ownerArity = method.DeclaringType?.GenericParameters.Count ?? 0;
		var locals = body.Variables.Select(local => new Dictionary<string, object?> {
			["type"] = ResolveTypeText(local.Type, ownerArity, method.GenericParameters.Count),
			["name"] = local.Name,
		}).ToArray();
		var instructions = new List<Dictionary<string, object?>>();
		foreach (var instruction in body.Instructions) {
			var row = new Dictionary<string, object?> { ["opcode"] = instruction.OpCode.Name };
			var operand = EncodeOperand(instruction.Operand, method, body);
			if (operand != null) row["operand"] = operand;
			instructions.Add(row);
		}
		var handlers = body.ExceptionHandlers.Select(eh => new Dictionary<string, object?> {
			["kind"] = eh.HandlerType switch {
				ExceptionHandlerType.Catch => "catch", ExceptionHandlerType.Finally => "finally",
				ExceptionHandlerType.Fault => "fault", ExceptionHandlerType.Filter => "filter",
				_ => throw Reject("body", "the exception handler shape is outside the importable domain"),
			},
			["try_start"] = InstructionIndex(body, eh.TryStart),
			["try_end"] = InstructionIndex(body, eh.TryEnd),
			["handler_start"] = InstructionIndex(body, eh.HandlerStart),
			["handler_end"] = InstructionIndex(body, eh.HandlerEnd),
			["filter_start"] = eh.FilterStart == null ? null : (object)body.Instructions.IndexOf(eh.FilterStart),
			["catch_type"] = eh.CatchType == null ? null : ResolveTypeText(eh.CatchType.ToTypeSig(), ownerArity, method.GenericParameters.Count),
		}).ToArray();
		var payload = new Dictionary<string, object?> {
			["init_locals"] = body.InitLocals,
			["max_stack"] = body.MaxStack,
			["locals"] = locals,
			["instructions"] = instructions,
			["exception_handlers"] = handlers,
		};
		var points = EditPdbTransferCodec.CapturePoints(body);
		if (points.Count != 0) payload["sequence_points"] = points;
		var imports = new Dictionary<string, EditPdbTransferCodec.ImportScopeRow>();
		var scope = EditPdbTransferCodec.CaptureScope(body, imports);
		if (scope != null) {
			payload["scope"] = scope;
			if (imports.Count != 0) payload["import_scopes"] = EditPdbTransferCodec.ToWireRows(imports);
		}
		return payload;
	}

	static int InstructionIndex(CilBody body, Instruction? instruction) =>
		instruction == null ? body.Instructions.Count : body.Instructions.IndexOf(instruction);

	object? EncodeOperand(object? operand, MethodDef method, CilBody body) {
		switch (operand) {
		case null: return null;
		case Instruction instruction: return new Dictionary<string, object?> {
			["kind"] = "label", ["instruction_index"] = body.Instructions.IndexOf(instruction) };
		case Instruction[] targets: return new Dictionary<string, object?> {
			["kind"] = "switch", ["instruction_indices"] = targets.Select(t => (object)body.Instructions.IndexOf(t)).ToArray() };
		case Local local: return new Dictionary<string, object?> { ["kind"] = "local", ["local_index"] = body.Variables.IndexOf(local) };
		case Parameter parameter: return new Dictionary<string, object?> { ["kind"] = "arg", ["argument_index"] = parameter.Index };
		case sbyte value:
			return new Dictionary<string, object?> { ["kind"] = "i32", ["value"] = (int)value };
		case byte value:
			return new Dictionary<string, object?> { ["kind"] = "i32", ["value"] = (int)value };
		case int value:
			return new Dictionary<string, object?> { ["kind"] = "i32", ["value"] = value };
		case long value: return new Dictionary<string, object?> { ["kind"] = "i64", ["value"] = value };
		case float value: return new Dictionary<string, object?> { ["kind"] = "f32", ["value"] = value };
		case double value: return new Dictionary<string, object?> { ["kind"] = "f64", ["value"] = value };
		case string value: return new Dictionary<string, object?> { ["kind"] = "string", ["value"] = value };
		case MethodDef artifactMethod: return TokenOperand(FindMethodRow(artifactMethod), "method", artifactMethod.FullName);
		case FieldDef artifactField: return TokenOperand(FindFieldRow(artifactField), "field", artifactField.FullName);
		case TypeDef artifactType: return TokenOperand(FindTypeRow(artifactType), "type", artifactType.FullName);
		case MemberRef member: return member.IsMethodRef
			? TokenOperand(FindMemberRefMethodRow(member), "method", member.FullName)
			: TokenOperand(FindMemberRefFieldRow(member), "field", member.FullName);
		case MethodSpec specification: return TokenOperand(FindMethodSpecRow(specification), "method", specification.FullName,
			"artifact_key=" + ArtifactSpecKey(specification) + " target_keys=" + string.Join(" | ", methodSpecCache.Keys.Take(6)));
		case TypeSpec specification: return TokenOperand(FindTypeSpecRow(specification.TypeSig), "type", specification.FullName);
		case TypeSig signature: return TokenOperand(FindTypeSpecRow(signature), "type", signature.FullName);
		case ITypeDefOrRef typeReference: return TokenOperand(FindExternalTypeRow(typeReference), "type", typeReference.FullName);
		case StandAloneSig or MethodSig:
			throw Reject("operand", "call-site signatures are outside the importable body domain: " + method.FullName);
		default:
			throw Reject("operand", "the operand shape is outside the importable body domain: " + operand.GetType().Name);
		}
	}

	static object TokenOperand(string token, string kind, string member) => TokenOperand(token, kind, member, null);

	static object TokenOperand(string token, string kind, string member, string? diagnostic) {
		if (token.Length == 0)
			throw Reject("operand", "the " + kind + " reference did not map to a target row: " + member
				+ (diagnostic == null ? string.Empty : "; " + diagnostic));
		if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			return new Dictionary<string, object?> { ["kind"] = "token", ["token"] = token };
		return new Dictionary<string, object?> { ["kind"] = "object", ["object_id"] = token };
	}

	// -------------------------------------------------------- row resolution

	/// <summary>A compile artifact references the target assembly like any
	/// other reference assembly, so its calls into target members arrive as
	/// MemberRefs scoped by the target's AssemblyRef.  Those bind straight to
	/// the target definitions by identity; every other MemberRef maps to an
	/// existing target row of the same shape.</summary>
	string FindMemberRefMethodRow(MemberRef member) {
		if (ReferencesTargetAssembly(member, out var owner)) {
			var match = owner!.Methods.Where(m => string.Equals(m.Name.String, member.Name.String, StringComparison.Ordinal)
				&& m.MethodSig.Params.Count == member.MethodSig.Params.Count
				&& string.Equals(matcher.TargetMethodKey(m), ArtifactMemberKey(member), StringComparison.Ordinal)).ToArray();
			return match.Length == 1 ? Token(match[0]) : string.Empty;
		}
		return ScanMemberRefs(member.DeclaringType?.FullName, member.Name.String, true,
			member.MethodSig.Params.Select(p => EditImportMatcher.Key(p, artifact))
				.Concat(new[] { EditImportMatcher.Key(member.MethodSig.RetType, artifact) }));
	}

	string FindMemberRefFieldRow(MemberRef member) {
		if (ReferencesTargetAssembly(member, out var owner)) {
			var key = EditImportMatcher.Key(member.FieldSig.Type, artifact);
			var match = owner!.Fields.Where(f => string.Equals(f.Name.String, member.Name.String, StringComparison.Ordinal)
				&& string.Equals(EditImportMatcher.Key(f.FieldType, target), key, StringComparison.Ordinal)).ToArray();
			return match.Length == 1 ? Token(match[0]) : string.Empty;
		}
		return ScanMemberRefs(member.DeclaringType?.FullName, member.Name.String, false,
			new[] { EditImportMatcher.Key(member.FieldSig.Type, artifact) });
	}

	string ArtifactMemberKey(MemberRef member) =>
		"m:" + member.Name.String + "|" + member.MethodSig.GenParamCount
		+ "(" + string.Join(",", member.MethodSig.Params.Select(p => EditImportMatcher.Key(p, artifact))) + ")";

	bool ReferencesTargetAssembly(MemberRef member, out TypeDef? owner) {
		owner = null;
		if (member.DeclaringType is not TypeRef declaring) return false;
		if (declaring.ResolutionScope is not AssemblyRef scope) return false;
		if (!string.Equals(scope.Name, target.Assembly?.Name?.String, StringComparison.OrdinalIgnoreCase)) return false;
		owner = matcher.TargetType(declaring.FullName);
		return owner != null;
	}

	/// <summary>Bind for CDI capture: reference text of artifact rows (token of
	/// the matched target row, or object ID of a row this import creates).</summary>
	string BindReference(IMDTokenProvider artifactRow) => artifactRow switch {
		MethodDef method => FindMethodRow(method),
		FieldDef field => FindFieldRow(field),
		TypeDef type => FindTypeRow(type),
		_ => throw Reject("internal", "an unsupported reference row was bound: " + artifactRow.GetType().Name),
	};

	/// <summary>Target counterpart of an artifact type: exact full name, or the
	/// generated kickoff-name pattern when the compilers numbered the state
	/// machines differently (cached; null means unmapped).</summary>
	TypeDef? TargetFor(TypeDef artifactType) {
		if (generatedTargets.TryGetValue(artifactType, out var cached)) return cached;
		var match = EditImportMatcher.IsGeneratedType(artifactType) && artifactType.DeclaringType != null
			? matcher.TargetGeneratedType(artifactType, artifactType.DeclaringType)
			: matcher.TargetType(artifactType);
		generatedTargets[artifactType] = match;
		return match;
	}

	string FindMethodRow(MethodDef artifactMethod) {
		if (referenceText.TryGetValue(artifactMethod, out var created)) return created;
		if (!ReferenceEquals(artifactMethod.Module, artifact)) {
			var external = ScanMemberRefs(artifactMethod.DeclaringType?.FullName, artifactMethod.Name.String, true,
				artifactMethod.MethodSig.Params.Select(p => EditImportMatcher.Key(p, artifact)).Concat(new[] { EditImportMatcher.Key(artifactMethod.MethodSig.RetType, artifact) }));
			return external;
		}
		var targetOwner = TargetFor(artifactMethod.DeclaringType!);
		if (targetOwner == null) return string.Empty;
		var match = targetOwner.Methods.Where(m => string.Equals(m.Name.String, artifactMethod.Name.String, StringComparison.Ordinal)
			&& m.GenericParameters.Count == artifactMethod.GenericParameters.Count
			&& string.Equals(matcher.TargetMethodKey(m), matcher.MethodKey(artifactMethod), StringComparison.Ordinal)).ToArray();
		return match.Length == 1 ? Token(match[0]) : string.Empty;
	}

	string FindFieldRow(FieldDef artifactField) {
		if (referenceText.TryGetValue(artifactField, out var created)) return created;
		if (!ReferenceEquals(artifactField.Module, artifact))
			return ScanMemberRefs(artifactField.DeclaringType?.FullName, artifactField.Name.String, false,
				new[] { EditImportMatcher.Key(artifactField.FieldType, artifact) });
		var targetOwner = TargetFor(artifactField.DeclaringType!);
		if (targetOwner == null) return string.Empty;
		var key = EditImportMatcher.Key(artifactField.FieldType, artifact);
		var match = targetOwner.Fields.Where(f => string.Equals(f.Name.String, artifactField.Name.String, StringComparison.Ordinal)
			&& string.Equals(EditImportMatcher.Key(f.FieldType, target), key, StringComparison.Ordinal)).ToArray();
		return match.Length == 1 ? Token(match[0]) : string.Empty;
	}

	string FindTypeRow(TypeDef artifactType) {
		if (referenceText.TryGetValue(artifactType, out var created)) return created;
		var match = TargetFor(artifactType);
		return match == null ? string.Empty : Token(match);
	}

	string FindExternalTypeRow(ITypeDefOrRef typeReference) {
		if (typeReference is TypeSpec specification) return FindTypeSpecRow(specification.TypeSig);
		var fullname = typeReference.FullName;
		if (typeRefCache.TryGetValue(fullname, out var cached)) return cached;
		var definition = target.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, fullname, StringComparison.Ordinal));
		string token;
		if (definition != null) token = Token(definition);
		else {
			var reference = target.GetTypeRefs().Where(t => string.Equals(t.FullName, fullname, StringComparison.Ordinal)).ToArray();
			if (reference.Length != 1) return string.Empty;
			token = Token(reference[0]);
		}
		typeRefCache[fullname] = token;
		return token;
	}

	string MatcherKeyTarget(TypeSig signature) => EditImportMatcher.Key(signature, target);

	// dnlib exposes no whole-table enumerators for MethodSpec/TypeSpec rows;
	// collect the reachable rows once from base types, interfaces, constraints,
	// exception handlers and IL operands (the surfaces that carry token rows).
	void ScanSpecRows() {
		if (specRowsScanned) return;
		specRowsScanned = true;
		void TypeSpecRow(ITypeDefOrRef? row) {
			if (row is not TypeSpec specification) return;
			var key = EditImportMatcher.Key(specification.TypeSig, target);
			if (!typeSpecCache.ContainsKey(key)) typeSpecCache[key] = Token(specification);
		}
		void Operands(CilBody body) {
			foreach (var instruction in body.Instructions) {
				switch (instruction.Operand) {
				case MethodSpec methodSpecification: {
					var key = matcher.TargetKeyRef(methodSpecification);
					if (!methodSpecCache.ContainsKey(key)) methodSpecCache[key] = Token(methodSpecification);
					break;
				}
				case TypeSpec typeSpecification: {
					var key = EditImportMatcher.Key(typeSpecification.TypeSig, target);
					if (!typeSpecCache.ContainsKey(key)) typeSpecCache[key] = Token(typeSpecification);
					break;
				}
				case MemberRef parented when parented.Class is TypeSpec parentSpecification:
					TypeSpecRow(parentSpecification);
					break;
				}
			}
			foreach (var handler in body.ExceptionHandlers) TypeSpecRow(handler.CatchType);
		}
		foreach (var type in target.GetTypes()) {
			TypeSpecRow(type.BaseType);
			foreach (var implemented in type.Interfaces) TypeSpecRow(implemented.Interface);
			foreach (var parameter in type.GenericParameters)
				foreach (var constraint in parameter.GenericParamConstraints) TypeSpecRow(constraint.Constraint);
			foreach (var method in type.Methods) {
				foreach (var parameter in method.GenericParameters)
					foreach (var constraint in parameter.GenericParamConstraints) TypeSpecRow(constraint.Constraint);
				if (method.HasBody) Operands(method.Body);
			}
		}
		foreach (var member in target.GetMemberRefs())
			TypeSpecRow(member.Class as ITypeDefOrRef);
	}
	bool specRowsScanned;

	string FindMethodSpecRow(MethodSpec specification) {
		var key = ArtifactSpecKey(specification);
		if (methodSpecCache.TryGetValue(key, out var cached)) return cached;
		ScanSpecRows();
		return methodSpecCache.TryGetValue(key, out var row) ? row : string.Empty;
	}

	string FindTypeSpecRow(TypeSig signature) {
		var key = ArtifactTypeKey(signature);
		if (typeSpecCache.TryGetValue(key, out var cached)) return cached;
		ScanSpecRows();
		return typeSpecCache.TryGetValue(key, out var row) ? row : string.Empty;
	}

	/// <summary>Artifact-side instantiation key with generated types rebound to
	/// their target counterparts: compilers number state machines per
	/// compilation, so the raw artifact full name would not equal the target row
	/// (AwaitUnsafeOnCompleted&lt;T,StateMachine&gt; instances drift with it).</summary>
	string ArtifactSpecKey(MethodSpec specification) =>
		"ms:" + matcher.ArtifactKeyRef(specification.Method) + "<"
		+ string.Join(",", (specification.GenericInstMethodSig?.GenericArguments ?? Array.Empty<TypeSig>())
			.Select(ArtifactTypeKey)) + ">";

	string ArtifactTypeKey(TypeSig signature) => signature switch {
		CorLibTypeSig core => "t:" + (core.TypeDefOrRef?.FullName ?? "System." + core.ElementType),
		GenericVar typeVar => "!" + typeVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericMVar methodVar => "!!" + methodVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericInstSig instance => "gi:" + ArtifactTypeKey(instance.GenericType)
			+ "<" + string.Join(",", instance.GenericArguments.Select(ArtifactTypeKey)) + ">",
		SZArraySig array => ArtifactTypeKey(array.Next) + "[]",
		ArraySig array => ArtifactTypeKey(array.Next) + "[" + array.Rank.ToString(CultureInfo.InvariantCulture) + "]",
		ByRefSig byRef => ArtifactTypeKey(byRef.Next) + "&",
		PtrSig pointer => ArtifactTypeKey(pointer.Next) + "*",
		TypeDefOrRefSig reference => ArtifactRefKey(reference.TypeDefOrRef),
		_ => "x:" + signature.FullName,
	};

	string ArtifactRefKey(ITypeDefOrRef? row) => row switch {
		null => "",
		TypeDef definition => "t:" + (TargetFor(definition)?.FullName ?? definition.FullName),
		TypeRef reference => string.Equals(reference.ResolutionScope is AssemblyRef scopeRef
			? scopeRef.Name?.String ?? string.Empty : string.Empty, TargetAssemblyName, StringComparison.OrdinalIgnoreCase)
			? "t:" + reference.FullName
			: "x:" + reference.FullName,
		TypeSpec specification => "ts:" + ArtifactTypeKey(specification.TypeSig),
		_ => "x:" + row.FullName,
	};

	/// <summary>External (non-artifact) member references map to an existing
	/// target MemberRef row with the same declaring type, name and structured
	/// parameter shape.  Zero or multiple candidate rows are unmapped.</summary>
	string ScanMemberRefs(string? declaringType, string name, bool isMethod, IEnumerable<string> parameterKeys) {		var key = (isMethod ? "m:" : "f:") + declaringType + "::" + name;
		if (memberRefCache.TryGetValue(key, out var cached)) return cached;
		var keys = parameterKeys.ToArray();
		var matches = target.GetMemberRefs()
			.Where(candidate => candidate.IsMethodRef == isMethod
				&& string.Equals(candidate.DeclaringType?.FullName, declaringType, StringComparison.Ordinal)
				&& string.Equals(candidate.Name.String, name, StringComparison.Ordinal)
				&& ParameterKeys(candidate).SequenceEqual(keys))
			.OrderBy(candidate => candidate.MDToken.Rid)
			.ToArray();
		var token = matches.Length == 1 ? Token(matches[0]) : string.Empty;
		memberRefCache[key] = token;
		return token;
	}

	IEnumerable<string> ParameterKeys(MemberRef candidate) => candidate.IsMethodRef
		? candidate.MethodSig.Params.Select(p => EditImportMatcher.Key(p, target)).Concat(new[] { EditImportMatcher.Key(candidate.MethodSig.RetType, target) })
		: new[] { EditImportMatcher.Key(candidate.FieldSig.Type, target) };

	static string Token(IMDTokenProvider row) => "0x" + row.MDToken.Raw.ToString("x8", CultureInfo.InvariantCulture);

	// ------------------------------------------------------- signature checks

	/// <summary>Signature text for payloads; every named reference must resolve
	/// to an existing target row or to a type this import creates — a name that
	/// would make the parser synthesize a reference is an unmapped reference and
	/// is rejected.</summary>
	string ResolveTypeText(TypeSig? signature, int ownerTypeArity = 0, int ownerMethodArity = 0) {
		if (signature == null) throw Reject("signature", "a signature is missing");
		var text = EditPdbTransferCodec.SigText(signature, RebindGenerated);
		var parser = new EditTypeSigParser(target, ownerTypeArity, ownerMethodArity);
		TypeSig parsed;
		try { parsed = parser.Parse(text, signature.ElementType == ElementType.Void); }
		catch (Exception ex) when (ex is ArgumentException or EditDomainException) {
			var detail = ex is EditDomainException domain ? EditWire.CanonicalPayload(domain.Details) : ex.Message;
			throw Reject("signature", "the signature text did not reparse in the target: " + text + " (" + detail + ")");
		}
		if (!ReferencesResolve(parsed))
			throw Reject("signature", "the signature references a type the target does not have: " + text);
		return text;
	}

	TypeDef? RebindGenerated(TypeDef artifactType) =>
		EditImportMatcher.IsGeneratedType(artifactType) ? TargetFor(artifactType) : null;

	bool ReferencesResolve(TypeSig? signature) {
		for (var current = signature; current != null; current = current.Next) {
			switch (current) {
			case CorLibTypeSig:
				return true;
			case GenericInstSig instance:
				if (!ReferencesResolve(instance.GenericType)) return false;
				foreach (var argument in instance.GenericArguments)
					if (!ReferencesResolve(argument)) return false;
				return true;
			case TypeDefOrRefSig { TypeDefOrRef: { } row }:
				if (row is TypeDef definition)
					return createdTypeNames.Contains(definition.FullName) || matcher.TargetType(definition) != null;
				if (row is TypeRef reference)
					return createdTypeNames.Contains(reference.FullName)
						|| target.GetTypeRefs().Any(t => string.Equals(t.FullName, reference.FullName, StringComparison.Ordinal));
				return row is TypeSpec specification && ReferencesResolve(specification.TypeSig);
			case GenericVar or GenericMVar or SentinelSig or PtrSig or ByRefSig or SZArraySig or ArraySig:
				continue;
			default:
				return false;
			}
		}
		return true;
	}

	// The four compiler-marker attributes the Roslyn codegen stamps onto
	// generated members; they carry no fixed/named arguments and no program
	// semantics, and the replace path keeps the target's rows untouched.
	static readonly string[] CompilerMarkers = {
		"System.Runtime.CompilerServices.CompilerGeneratedAttribute",
		"System.Diagnostics.DebuggerHiddenAttribute",
		"System.Diagnostics.DebuggerNonUserCodeAttribute",
		"System.Diagnostics.DebuggerStepThroughAttribute",
	};

	/// <summary>Non-marker custom attributes are outside the P06 add domain;
	/// compiler markers are dropped because the replace path keeps the target's
	/// rows and a fresh marker row is not expressible without a resolvable
	/// constructor in the target.</summary>
	static void RejectCustomAttributes(string member, IEnumerable<dnlib.DotNet.CustomAttribute> attributes) {
		foreach (var attribute in attributes)
			if (!CompilerMarkers.Contains(attribute.TypeFullName))
				throw Reject("add", "members with custom attributes are outside the importable add domain: " + member
					+ " (" + attribute.TypeFullName + ")");
	}

	static string RequiredString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
			? value.GetString()! : throw Reject(name, name + " is required");

	static EditDomainException Reject(string location, string reason) =>
		new("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("import_" + location, reason));
}
