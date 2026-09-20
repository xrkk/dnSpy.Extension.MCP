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
///
/// T004: the emission layer covers the whole first-add surface.  External
/// rows the target lacks are synthesized bottom-up through explicit-scope
/// <c>reference_add</c> operations (assembly/type/member/spec), signatures
/// that must point at batch rows carry structured v2 nodes, and generated
/// state-machine subtrees are emitted as type/interface/generic/field/method
/// shells whose bodies and symbol rows are filled once every row of the plan
/// exists — so kickoff and state machine can reference each other without
/// depending on compiler emission order.</summary>
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
	readonly ModuleDef? referenceSourceModule;
	readonly HashSet<string> createdTypeNames = new(StringComparer.Ordinal);
	readonly Dictionary<TypeDef, TypeDef?> generatedTargets = new(ReferenceTextComparer.Instance);
	readonly Dictionary<string, string> memberRefCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> typeRefCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> methodSpecCache = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> typeSpecCache = new(StringComparer.Ordinal);
	// bodies and symbol rows deferred until every shell of this import exists
	sealed class PendingFill {
		public MethodDef Method = null!;
		public Dictionary<string, object?> Target = null!;
		public string Member = string.Empty;
	}
	readonly List<PendingFill> pendingFills = new();

	// Owner positions of generic variables may point at the row the very
	// operation being built creates; those bind to null (dnlib resolves by
	// number against the actual owner on use).
	const string OwnerSentinel = "\u0001owner";

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

	public EditCSharpImporter(ModuleDef artifact, ModuleDef target, Dictionary<string, IMDTokenProvider> objectIds, int baseOperationIndex, ModuleDef? referenceSourceModule = null) {
		this.artifact = artifact;
		this.target = target;
		this.objectIds = objectIds;
		this.baseOperationIndex = baseOperationIndex;
		// T036: the live dnSpy module supplies the assembly resolver used ONLY
		// to READ metadata of referenced assemblies (type forwarders). Optional
		// and absent for every existing caller — the gate then simply never
		// proves equivalence and keeps full identity.
		this.referenceSourceModule = referenceSourceModule;
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
		FlushFills();
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
	/// counterpart is added whole through the full emission path (state
	/// machines included: interface rows, explicit-override declarations and
	/// marker attributes are explicit operations now).</summary>
	void SyncGeneratedSubtree(TypeDef generated, MethodDef kickoff) {
		var counterpart = TargetFor(generated);
		if (counterpart == null) {
			EmitTypeTree(generated, ContainerFor(generated));
			return;
		}
		// Interface-driven accessor rows (IEnumerator.Current pairs) exist on both
		// compilers' state machines and stay the target's; the subtree sync lands
		// fields and method bodies.  A property/event the counterpart lacks is a
		// shape the operation language cannot reconcile — reject.
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
			if (existing == null) EmitMethodAdd(method, RefOf(counterpart), deferBody: true);
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
		EmitTypeTree(artifactType, ContainerFor(artifactType)!);
	}

	/// <summary>Whole-subtree emission for a new type: type_add, then generic
	/// parameters, interface rows, fields, nested types (state machines and
	/// closures included — a method's state-machine attribute references its
	/// nested generated type, so the nested trees must exist first), then
	/// accessor and plain method shells, property and event rows, attribute rows
	/// (compiler markers included), and finally the deferred bodies and symbol
	/// rows of every method once the whole plan exists.  Layouts, security rows
	/// and P/Invoke shapes stay outside the operation language and are
	/// rejected.</summary>
	Dictionary<string, object?> EmitTypeTree(TypeDef artifactType, Dictionary<string, object?>? container) {
		if (artifactType.ClassLayout != null || artifactType.DeclSecurities.Count != 0)
			throw Reject("add", "the type shape is outside the frozen operation language: " + artifactType.FullName);
		var self = EmitTypeAdd(artifactType, container);
		foreach (var gp in artifactType.GenericParameters)
			EmitGenericParameterAdd(gp, self);
		foreach (var implemented in artifactType.Interfaces)
			EmitInterfaceAdd(self, implemented, artifactType.FullName);
		foreach (var field in artifactType.Fields)
			EmitFieldAdd(field, self);
		foreach (var nested in artifactType.NestedTypes)
			EmitTypeTree(nested, self);
		foreach (var method in artifactType.Methods)
			EmitMethodAdd(method, self, deferBody: true);
		foreach (var property in artifactType.Properties)
			EmitPropertyAdd(property, self);
		foreach (var evt in artifactType.Events)
			EmitEventAdd(evt, self);
		EmitAttributeRows(self, artifactType.CustomAttributes, artifactType.FullName);
		return self;
	}

	static bool IsAccessor(MethodDef method, TypeDef owner) =>
		owner.Properties.Any(p => ReferenceEquals(p.GetMethod, method) || ReferenceEquals(p.SetMethod, method))
		|| owner.Events.Any(e => ReferenceEquals(e.AddMethod, method) || ReferenceEquals(e.RemoveMethod, method) || ReferenceEquals(e.InvokeMethod, method));

	void AddMethod(MethodDef artifactMethod, Dictionary<string, object?>? explicitContainer) {
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
		// New async/iterator members carry their state-machine subtree: the
		// generated type is emitted whole before the kickoff so the kickoff's
		// body, attribute and symbol rows see every generated row, while the
		// state machine's own bodies are deferred past the kickoff.
		var stateMachine = EditImportMatcher.StateMachineType(artifactMethod) ?? StateMachineByPattern(artifactMethod);
		if (stateMachine != null) {
			if (TargetFor(stateMachine) != null)
				throw Reject("add", "the compiled state machine name collides with an existing target generated type: " + stateMachine.FullName);
			EmitTypeTree(stateMachine, ownerRef);
		}
		EmitMethodAdd(artifactMethod, ownerRef, deferBody: false);
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

	// ------------------------------------------------------- reference planning

	/// <summary>Plan the target-side reference for an artifact row: the token of
	/// an existing target row, the object ID of a row this import creates, or a
	/// synthesized <c>reference_add</c> row (emitted here, dependencies first).
	/// Scope identity is never guessed and never reduced to a FullName.</summary>
	string Bind(IMDTokenProvider? row) {
		if (row == null) throw Reject("reference", "a reference row is missing");
		if (referenceText.TryGetValue(row, out var cached)) return cached;
		var text = row switch {
			TypeDef definition => BindDefinition(definition),
			TypeRef reference => BindTypeRef(reference),
			TypeSpec specification => BindTypeSpec(specification),
			MemberRef member => BindMemberRef(member),
			MethodSpec specification => BindMethodSpec(specification),
			MethodDef method => BindMethodRow(method),
			FieldDef field => BindFieldRow(field),
			AssemblyRef assembly => BindAssemblyRef(assembly),
			_ => throw Reject("reference", "the artifact reference shape is outside the importable domain: " + row.GetType().Name),
		};
		referenceText[row] = text;
		return text;
	}

	// Owner positions bind through this wrapper so a generic variable owned by
	// the row the operation under construction creates degrades to an ownerless
	// node instead of deadlocking the emission order.
	string BindForCapture(IMDTokenProvider row) {
		if (row is MethodDef method && ReferenceEquals(method.Module, artifact) && FindMethodRow(method).Length == 0)
			return OwnerSentinel;
		if (row is TypeDef type && ReferenceEquals(type.Module, artifact) && FindTypeRow(type).Length == 0)
			return OwnerSentinel;
		return Bind(row);
	}

	string BindDefinition(TypeDef definition) {
		var mapped = FindTypeRow(definition);
		if (mapped.Length != 0) return mapped;
		if (ReferenceEquals(definition.Module, artifact))
			throw Reject("reference", "an artifact type has no target counterpart and is not created by this import: " + definition.FullName);
		var targetDefinition = target.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, definition.FullName, StringComparison.Ordinal));
		if (targetDefinition != null) return Token(targetDefinition);
		var scope = definition.DeclaringType != null
			? RowDescriptor(Bind(definition.DeclaringType))
			: ScopeDescriptor(AssemblyScopeOf(definition));
		return SynthesizeTypeRef(definition.Namespace?.String ?? string.Empty, definition.Name.String, scope, definition.FullName);
	}

	static AssemblyRef AssemblyScopeOf(TypeDef externalDefinition) {
		var assembly = externalDefinition.Module?.Assembly;
		return new AssemblyRefUser(assembly?.Name?.String ?? string.Empty, assembly?.Version, assembly?.PublicKey) {
			Culture = assembly?.Culture?.String,
			Attributes = assembly != null ? assembly.Attributes : 0,
		};
	}

	string BindTypeRef(TypeRef reference) {
		// T036: an artifact type scoped to the ARTIFACT's own corlib assembly
		// (e.g. Roslyn bound System.Text.StringBuilder to the process
		// mscorlib) must not inject a second, differently-identified corlib
		// row into a target whose own corlib resolves elsewhere (e.g. the
		// netstandard facade) — the written image would then reload with a
		// different corlib selection and every primitive-typed row would flip
		// in the strong projection (T035: commit-time rejection). Rebinding to
		// the TARGET's corlib row is allowed ONLY on a proven type-forwarder
		// equivalence: the target corlib assembly's metadata must forward the
		// exact type, through a non-cyclic unambiguous chain, to a terminal
		// assembly whose FULL identity (name/version/culture/key) equals the
		// artifact scope's and which really defines the type. Anything less —
		// no resolver, unresolved assemblies, missing/ambiguous/cyclic
		// forwarders, identity mismatch — keeps the historical full-identity
		// binding. Names alone never prove equivalence.
		if (reference.ResolutionScope is AssemblyRef artifactCorlibScope
			&& TargetCorLibRebindProven(artifactCorlibScope, reference)) {
			var targetCorlib = target.CorLibTypes.AssemblyRef;
			return SynthesizeTypeRef(reference.Namespace?.String ?? string.Empty,
				reference.Name.String, ScopeDescriptor(targetCorlib), reference.FullName);
		}
		// A compile artifact references the target assembly like any other
		// reference assembly; those rows rebind to the target definitions.
		if (reference.ResolutionScope is AssemblyRef scope && string.Equals(scope.Name, TargetAssemblyName, StringComparison.OrdinalIgnoreCase)) {
			var rebound = target.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, reference.FullName, StringComparison.Ordinal))
				?? throw Reject("reference", "the artifact references a target-assembly type the target does not define: " + reference.FullName);
			return Token(rebound);
		}
		var scopeDescriptor = reference.ResolutionScope switch {
			AssemblyRef assembly => ScopeDescriptor(assembly),
			TypeRef parent => RowDescriptor(BindTypeRef(parent)),
			TypeDef parent => RowDescriptor(BindDefinition(parent)),
			ModuleRef module => ModuleScope(module),
			_ => throw Reject("reference", "the type reference scope is outside the importable domain: " + reference.FullName),
		};
		return SynthesizeTypeRef(reference.Namespace?.String ?? string.Empty, reference.Name.String, scopeDescriptor, reference.FullName);
	}

	static CorLibTypeSig? CorLibSig(ICorLibTypes types, ElementType element) => element switch {
		ElementType.Void => types.Void,
		ElementType.Boolean => types.Boolean,
		ElementType.Char => types.Char,
		ElementType.I1 => types.SByte,
		ElementType.U1 => types.Byte,
		ElementType.I2 => types.Int16,
		ElementType.U2 => types.UInt16,
		ElementType.I4 => types.Int32,
		ElementType.U4 => types.UInt32,
		ElementType.I8 => types.Int64,
		ElementType.U8 => types.UInt64,
		ElementType.R4 => types.Single,
		ElementType.R8 => types.Double,
		ElementType.String => types.String,
		ElementType.TypedByRef => types.TypedReference,
		ElementType.I => types.IntPtr,
		ElementType.U => types.UIntPtr,
		ElementType.Object => types.Object,
		_ => null,
	};

	static bool SameAssemblyIdentity(AssemblyRef left, AssemblyRef right) =>
		string.Equals(left.Name?.String, right.Name?.String, StringComparison.OrdinalIgnoreCase)
		&& left.Version == right.Version
		&& string.Equals(left.Culture?.String ?? string.Empty, right.Culture?.String ?? string.Empty, StringComparison.Ordinal)
		&& (left.PublicKeyOrToken?.Data ?? Array.Empty<byte>()).SequenceEqual(right.PublicKeyOrToken?.Data ?? Array.Empty<byte>());

	/// <summary>T036 corlib rebind proof. True only when (a) the artifact scope
	/// IS the artifact module's own corlib assembly and the target resolves a
	/// DIFFERENT corlib assembly, (b) the target's corlib assembly metadata is
	/// resolvable and forwards the exact type through a legal, non-cyclic,
	/// unambiguous ExportedType chain whose every hop's RESOLVED assembly
	/// identity matches its request, to a terminal assembly with the artifact
	/// scope's FULL identity that really defines the type with the same nested
	/// name. The resolver is BORROWED from the live module context: nothing it
	/// returns is disposed here. Pure metadata reads; no target code executes.</summary>
	bool TargetCorLibRebindProven(AssemblyRef artifactScope, TypeRef reference) {
		var artifactCorlib = artifact.CorLibTypes.AssemblyRef;
		if (artifactCorlib == null || !SameAssemblyIdentity(artifactScope, artifactCorlib)) return false;
		var targetCorlib = target.CorLibTypes.AssemblyRef;
		if (targetCorlib == null || SameAssemblyIdentity(artifactScope, targetCorlib)) return false;
		var resolver = referenceSourceModule?.Context?.AssemblyResolver;
		if (resolver == null) return false;

		// Borrowed resolver: resolved modules are owned by the live module's
		// context and must stay readable for later imports — never disposed here.
		ModuleDef? Resolve(AssemblyRef row) {
			try {
				var assembly = resolver.Resolve(row, referenceSourceModule!);
				if (assembly == null) return null;
				// K2: the resolved assembly's ACTUAL identity must match the request
				// (dnlib public-key/token semantics), not just carry the request's name.
				if (!ResolvedIdentityMatches(assembly, row)) return null;
				return assembly.ManifestModule;
			}
			catch { return null; }
		}

		// K2: prove the ARTIFACT side really resolves the reference to a terminal
		// TypeDef through its own scope before comparing anything. The resolved
		// module is BORROWED from the live context — never disposed here.
		var artifactTerminal = Resolve(artifactScope);
		if (artifactTerminal == null) return false;
		var artifactDefinition = FindUniqueDefinition(artifactTerminal, reference);
		if (artifactDefinition == null) return false;

		// K3: walk the target corlib's LEGAL forwarder chain hop by hop. The
		// cycle set keys on FULL identity (name+version+key), so a legal chain
		// that revisits a simple name with a different version is NOT a cycle.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var currentRequest = targetCorlib;
		ModuleDef? currentModule = null;
		for (var depth = 0; depth < 8; depth++) {
			if (!seen.Add(FullIdentityKey(currentRequest))) return false;  // cycle
			currentModule ??= Resolve(currentRequest);
			if (currentModule == null) return false;
			ExportedType? match = null;
			foreach (var forwarded in currentModule.ExportedTypes) {
				if (!forwarded.IsForwarder) continue;  // K3: only legal forwarders
				if (!string.Equals(forwarded.TypeName?.String ?? "", reference.Name.String, StringComparison.Ordinal)) continue;
				if (!string.Equals(forwarded.TypeNamespace?.String ?? "", reference.Namespace?.String ?? string.Empty, StringComparison.Ordinal)) continue;
				if (match != null) return false;  // ambiguous
				match = forwarded;
			}
			if (match == null) return false;
			if (match.Implementation is not AssemblyRef next) return false;  // no TypeRef pseudo-hops
			var nextModule = Resolve(next);
			if (nextModule == null) return false;
			if (SameAssemblyIdentity(next, artifactScope)) {
				// Terminal: the resolved module must really define the full nested
				// identity, and be THE SAME RESOLVED MODULE the artifact side
				// resolved to (same actual ModuleDef instance, or a provably same
				// source: equal MVID and matching definition token).
				var terminalDefinition = FindUniqueDefinition(nextModule, reference);
				return terminalDefinition != null
					&& SameDefinitionIdentity(artifactDefinition, terminalDefinition)
					&& SameTerminalModule(artifactTerminal, nextModule, artifactDefinition, terminalDefinition);
			}
			currentRequest = next;
			currentModule = nextModule;
		}
		return false;
	}

	static bool ResolvedIdentityMatches(AssemblyDef assembly, AssemblyRef request) {
		if (!string.Equals(assembly.Name?.String, request.Name?.String, StringComparison.OrdinalIgnoreCase)) return false;
		// Culture must match exactly (empty == neutral).
		if (!string.Equals(assembly.Culture?.String ?? string.Empty, request.Culture?.String ?? string.Empty, StringComparison.Ordinal)) return false;
		// A missing requested version is NOT a wildcard: the resolved assembly
		// must carry a version and it must equal the request when the request
		// has one; a versionless request only accepts a versionless definition.
		var resolvedVersion = assembly.Version;
		var requestedVersion = request.Version;
		if (requestedVersion is null || resolvedVersion is null) {
			if (requestedVersion is not null || resolvedVersion is not null) return false;
		}
		else if (resolvedVersion != requestedVersion) return false;
		var requestKey = request.PublicKeyOrToken;
		var resolvedKey = assembly.PublicKeyOrToken;
		// dnlib semantics: a request with a public key can match a token-only
		// definition (the token is the key's hash); identical kinds compare raw.
		// Explicit unsigned semantics: only a request with NO key material may
		// match a definition with NO key material; a signed side never matches
		// an unsigned side.
		if (requestKey == null || resolvedKey == null) return requestKey == null && resolvedKey == null;
		if (requestKey.Data.AsSpan().SequenceEqual(resolvedKey.Data.AsSpan())) return true;
		return requestKey.Token is { } expected && resolvedKey.Token is { } actual && expected.Data.AsSpan().SequenceEqual(actual.Data.AsSpan());
	}

	/// <summary>Find the unique type definition matching the reference's
	/// namespace/name at the TOP level of the declaring chain. A nested
	/// reference must arrive with its declaring path bound through the parent
	/// (callers bind parents first); searching GetTypes for any same-named
	/// nested row is NOT proof, so nested references without a bound parent
	/// path are rejected here.</summary>
	static TypeDef? FindUniqueDefinition(ModuleDef module, TypeRef reference) {
		var namespaceText = reference.Namespace?.String ?? string.Empty;
		var nameText = reference.Name.String;
		TypeDef? found = null;
		foreach (var type in module.GetTypes()) {
			if (type.DeclaringType != null) continue;  // nested rows never satisfy a top-level lookup
			if (!string.Equals(type.Namespace?.String ?? string.Empty, namespaceText, StringComparison.Ordinal)) continue;
			if (!string.Equals(type.Name?.String, nameText, StringComparison.Ordinal)) continue;
			if (found != null) return null;  // duplicate top-level definitions: ambiguous
			found = type;
		}
		return found;
	}

	static string FullIdentityKey(AssemblyRef row) =>
		(row.Name?.String ?? "").ToLowerInvariant() + "|" + (row.Version?.ToString() ?? "") + "|"
		+ (row.Culture?.String ?? "").ToLowerInvariant() + "|"
		+ NormalizedToken(row.PublicKeyOrToken);

	/// <summary>Normalized public-key-token text: a full key and its token form
	/// are the same identity (the token IS the key's hash), so both collapse to
	/// the token bytes.</summary>
	static string NormalizedToken(PublicKeyBase? key) {
		if (key == null) return "";
		var data = key.Data ?? Array.Empty<byte>();
		if (key.Token is { } token) return BitConverter.ToString(token.Data ?? Array.Empty<byte>()).Replace("-", "");
		// full public key: compute the token form (SHA-1 of the key, last 8 bytes)
		using var sha = System.Security.Cryptography.SHA1.Create();
		var hash = sha.ComputeHash(data);
		return BitConverter.ToString(hash, hash.Length - 8, 8).Replace("-", "");
	}

	/// <summary>Terminal module identity (minimal conservative form): both
	/// sides must resolve to THE SAME actual ModuleDef instance AND the same
	/// actual TypeDef instance. A different instance is never proven to be the
	/// same fixed metadata source — identical MVID/token do not constitute
	/// proof, so such cases keep the original full-identity path.</summary>
	static bool SameTerminalModule(ModuleDef artifactSide, ModuleDef targetSide, TypeDef artifactDefinition, TypeDef targetDefinition) {
		return ReferenceEquals(artifactSide, targetSide) && ReferenceEquals(artifactDefinition, targetDefinition);
	}

	static bool SameDefinitionIdentity(TypeDef left, TypeDef right) {
		// Full nested identity: walk to top comparing names at each level.
		var a = left; var b = right;
		while (a != null && b != null) {
			if (!string.Equals(a.Name?.String, b.Name?.String, StringComparison.Ordinal)) return false;
			if (!string.Equals(a.Namespace?.String ?? "", b.Namespace?.String ?? "", StringComparison.Ordinal)) return false;
			a = a.DeclaringType; b = b.DeclaringType;
		}
		return a == null && b == null;
	}

	/// <summary>True when <paramref name="reference"/> IS the artifact module's
	/// built-in corlib row for a CLI primitive type. Identity is established by
	/// row identity against the artifact's own CorLibTypes set, or — for an
	/// equivalent duplicate row — by being scoped to the artifact's corlib
	/// assembly instance while carrying that built-in's exact System name. A
	/// user-defined cross-assembly type named like a primitive never matches:
	/// its scope is a different assembly, and non-primitive System.* types
	/// (classes like System.Console) are not in the CorLibTypes set at all.</summary>
	static bool ArtifactCorLibElement(ModuleDef artifact, TypeRef reference, out ElementType elementType) {
		elementType = default;
		var corlib = artifact.CorLibTypes;
		var corlibAssembly = corlib.AssemblyRef;
		foreach (var candidate in CorLibElements) {
			var builtIn = CorLibSig(corlib, candidate);
			var builtInRow = builtIn?.TypeDefOrRef as TypeRef;
			if (builtInRow == null) continue;
			if (ReferenceEquals(builtInRow, reference)) {
				elementType = candidate;
				return true;
			}
			if (reference.ResolutionScope is AssemblyRef scope && ReferenceEquals(scope, corlibAssembly)
				&& string.Equals(reference.Namespace?.String ?? string.Empty, "System", StringComparison.Ordinal)
				&& string.Equals(reference.Name?.String ?? string.Empty, builtInRow.Name?.String ?? string.Empty, StringComparison.Ordinal)
				&& string.Equals(builtInRow.Namespace?.String ?? string.Empty, "System", StringComparison.Ordinal)) {
				elementType = candidate;
				return true;
			}
		}
		return false;
	}

	static readonly ElementType[] CorLibElements = {
		ElementType.Void, ElementType.Boolean, ElementType.Char, ElementType.I1, ElementType.U1,
		ElementType.I2, ElementType.U2, ElementType.I4, ElementType.U4, ElementType.I8, ElementType.U8,
		ElementType.R4, ElementType.R8, ElementType.String, ElementType.TypedByRef, ElementType.I,
		ElementType.U, ElementType.Object,
	};

	/// <summary>T034 capture hook, passed only to the importer's explicit
	/// EditStructuredSignatureCodec.Capture call sites. Invoked solely from the
	/// CorLibTypeSig branch — the signature context a plain row binder cannot
	/// recover — it binds a confirmed CLI built-in primitive (by element
	/// identity, never by System.* name) to the TARGET's corlib representation:
	/// same primitive name scoped to the target's corlib assembly, through the
	/// materializable SynthesizeTypeRef path (persisted-row reuse or an
	/// explicit reference_add; never an on-demand CorLibTypes row with a
	/// synthetic token). Everything else keeps the historical full-identity
	/// binding, so plain ClassOrValueTypeSig rows and cross-assembly same-name
	/// types are untouched.</summary>
	string? BindCorLibForCapture(CorLibTypeSig core) {
		if (core.TypeDefOrRef is not TypeRef reference) return null;
		if (!ArtifactCorLibElement(artifact, reference, out var elementType)) return null;
		var builtIn = CorLibSig(target.CorLibTypes, elementType);
		if (builtIn?.TypeDefOrRef is not TypeRef targetRow) return null;
		return SynthesizeTypeRef(targetRow.Namespace?.String ?? string.Empty,
			targetRow.Name.String,
			ScopeDescriptor(target.CorLibTypes.AssemblyRef),
			reference.FullName);
	}

	Dictionary<string, object?> ModuleScope(ModuleRef module) {
		var match = target.GetModuleRefs().FirstOrDefault(m => string.Equals(m.Name?.String, module.Name?.String, StringComparison.Ordinal));
		return match == null
			? throw Reject("reference", "the target has no module reference row for the artifact scope: " + module.Name)
			: TokenRef(match);
	}

	/// <summary>Reuse an existing target TypeRef with the same name and scope
	/// identity, or synthesize one through reference_add.</summary>
	string SynthesizeTypeRef(string ns, string name, Dictionary<string, object?> scopeDescriptor, string fullName) {
		var matches = target.GetTypeRefs()
			.Where(t => string.Equals(t.Namespace?.String ?? string.Empty, ns, StringComparison.Ordinal)
				&& string.Equals(t.Name?.String, name, StringComparison.Ordinal)
				&& ScopeMatchesPlanned(t.ResolutionScope, scopeDescriptor))
			.OrderBy(t => t.MDToken.Rid)
			.ToArray();
		if (matches.Length != 0) return Token(matches[0]);
		var descriptor = new Dictionary<string, object?> {
			["form"] = "type_ref",
			["scope"] = scopeDescriptor,
			["namespace"] = ns,
			["name"] = name,
		};
		return EmitReferenceAdd(descriptor, fullName);
	}

	// A planned scope descriptor points at a target row (token) or a batch row
	// (object ID); existing target scopes compare by identity, never by name
	// alone.
	bool ScopeMatchesPlanned(IResolutionScope? existing, Dictionary<string, object?> planned) {
		IMDTokenProvider? plannedRow = null;
		if (planned.TryGetValue("token", out var tokenText))
			plannedRow = target.ResolveToken(uint.Parse(((tokenText as string)!).Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
		else if (planned.TryGetValue("object_id", out var idText) && objectIds.TryGetValue((idText as string)!, out var batchRow))
			plannedRow = batchRow;
		if (plannedRow == null) return false;
		if (ReferenceEquals(existing, plannedRow)) return true;
		if (existing is AssemblyRef assembly && plannedRow is AssemblyRef plannedAssembly)
			return AssemblyIdentityEquals(assembly, plannedAssembly);
		if (existing is TypeRef reference && plannedRow is TypeRef plannedReference)
			return string.Equals(reference.Namespace?.String ?? string.Empty, plannedReference.Namespace?.String ?? string.Empty, StringComparison.Ordinal)
				&& string.Equals(reference.Name?.String, plannedReference.Name?.String, StringComparison.Ordinal)
				&& ScopeMatchesPlanned(reference.ResolutionScope, planned);
		if (existing is TypeDef definition && plannedRow is TypeDef plannedDefinition)
			return string.Equals(definition.FullName, plannedDefinition.FullName, StringComparison.Ordinal);
		if (existing is ModuleRef module && plannedRow is ModuleRef plannedModule)
			return string.Equals(module.Name?.String, plannedModule.Name?.String, StringComparison.Ordinal);
		return false;
	}

	Dictionary<string, object?> ScopeDescriptor(AssemblyRef assembly) => RowDescriptor(BindAssemblyRef(assembly));

	static Dictionary<string, object?> RowDescriptor(string text) =>
		text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
			? new Dictionary<string, object?> { ["token"] = text }
			: new Dictionary<string, object?> { ["object_id"] = text };

	string BindTypeSpec(TypeSpec specification) {
		var mapped = FindTypeSpecRow(specification.TypeSig);
		if (mapped.Length != 0) return mapped;
		var descriptor = new Dictionary<string, object?> {
			["form"] = "type_spec",
			["signature"] = NodeJson(CaptureNode(specification.TypeSig)),
		};
		return EmitReferenceAdd(descriptor, specification.FullName);
	}

	string BindMemberRef(MemberRef member) {
		var mapped = member.IsMethodRef ? FindMemberRefMethodRow(member) : FindMemberRefFieldRow(member);
		if (mapped.Length != 0) return mapped;
		var owner = member.DeclaringType ?? throw Reject("reference", "the member reference has no declaring type: " + member.FullName);
		var descriptor = new Dictionary<string, object?> {
			["form"] = "member_ref",
			["member_kind"] = member.IsMethodRef ? "method" : "field",
			["owner"] = RowDescriptor(Bind(owner)),
			["name"] = member.Name.String,
			["signature"] = JsonSerializer.SerializeToElement(
				NormalizeCall(EditStructuredSignatureCodec.Capture((CallingConventionSig)(member.IsMethodRef ? member.MethodSig! : member.FieldSig!), BindForCapture, BindCorLibForCapture)), EditWire.JsonOptions),
		};
		return EmitReferenceAdd(descriptor, member.FullName);
	}

	string BindMethodSpec(MethodSpec specification) {
		var mapped = FindMethodSpecRow(specification);
		if (mapped.Length != 0) return mapped;
		var method = specification.Method as IMDTokenProvider
			?? throw Reject("reference", "the method specification has no method: " + specification.FullName);
		var arguments = (specification.GenericInstMethodSig?.GenericArguments ?? Array.Empty<TypeSig>()).ToArray();
		var descriptor = new Dictionary<string, object?> {
			["form"] = "method_spec",
			["method"] = RowDescriptor(Bind(method)),
			["arguments"] = arguments.Select(argument => (object)NodeJson(CaptureNode(argument))).ToArray(),
		};
		return EmitReferenceAdd(descriptor, specification.FullName);
	}

	string BindMethodRow(MethodDef method) {
		var mapped = FindMethodRow(method);
		if (mapped.Length != 0) return mapped;
		if (!ReferenceEquals(method.Module, artifact))
			return SynthesizeExternalMember(method.DeclaringType, method.Name.String, true,
				method.MethodSig ?? throw Reject("reference", "the external method has no signature: " + method.FullName), method.FullName);
		throw Reject("reference", "an artifact method has no target counterpart and is not created by this import: " + method.FullName);
	}

	string BindFieldRow(FieldDef field) {
		var mapped = FindFieldRow(field);
		if (mapped.Length != 0) return mapped;
		if (!ReferenceEquals(field.Module, artifact))
			return SynthesizeExternalMember(field.DeclaringType, field.Name.String, false,
				field.FieldSig ?? throw Reject("reference", "the external field has no signature: " + field.FullName), field.FullName);
		throw Reject("reference", "an artifact field has no target counterpart and is not created by this import: " + field.FullName);
	}

	string SynthesizeExternalMember(TypeDef? declaring, string name, bool isMethod, CallingConventionSig signature, string fullName) {
		var owner = declaring ?? throw Reject("reference", "the external member has no declaring type: " + fullName);
		var descriptor = new Dictionary<string, object?> {
			["form"] = "member_ref",
			["member_kind"] = isMethod ? "method" : "field",
			["owner"] = RowDescriptor(BindDefinition(owner)),
			["name"] = name,
			["signature"] = JsonSerializer.SerializeToElement(
				NormalizeCall(EditStructuredSignatureCodec.Capture(signature, BindForCapture, BindCorLibForCapture)), EditWire.JsonOptions),
		};
		return EmitReferenceAdd(descriptor, fullName);
	}

	string BindAssemblyRef(AssemblyRef assembly) {
		var name = assembly.Name?.String ?? throw Reject("reference", "the assembly reference has no name");
		if (string.Equals(name, TargetAssemblyName, StringComparison.OrdinalIgnoreCase))
			throw Reject("reference", "a type cannot be scoped to the target's own assembly through a reference row");
		var match = target.GetAssemblyRefs().FirstOrDefault(t => AssemblyIdentityEquals(t, assembly));
		if (match != null) return Token(match);
		var descriptor = new Dictionary<string, object?> {
			["form"] = "assembly_ref",
			["name"] = name,
			["version"] = (assembly.Version ?? new Version(0, 0, 0, 0)).ToString(),
			["culture"] = string.IsNullOrEmpty(assembly.Culture?.String) ? null : assembly.Culture?.String,
			["flags"] = (ulong)assembly.Attributes,
		};
		var data = assembly.PublicKeyOrToken?.Data;
		if (data is { Length: > 0 })
			descriptor["public_key_or_token"] = new Dictionary<string, object?> {
				["kind"] = assembly.PublicKeyOrToken is PublicKey ? "public_key" : "token",
				["base64"] = Convert.ToBase64String(data),
			};
		return EmitReferenceAdd(descriptor, name);
	}

	static bool AssemblyIdentityEquals(AssemblyRef left, AssemblyRef right) {
		if (!string.Equals(left.Name?.String, right.Name?.String, StringComparison.OrdinalIgnoreCase)) return false;
		if (left.Version != right.Version) return false;
		if (!string.Equals(left.Culture?.String ?? string.Empty, right.Culture?.String ?? string.Empty, StringComparison.Ordinal)) return false;
		if (left.Attributes != right.Attributes) return false;
		return (left.PublicKeyOrToken?.Data ?? Array.Empty<byte>()).SequenceEqual(right.PublicKeyOrToken?.Data ?? Array.Empty<byte>());
	}

	string EmitReferenceAdd(Dictionary<string, object?> descriptor, string label) {
		var index = NextIndex;
		var operation = new Dictionary<string, object?> {
			["kind"] = "reference_add",
			["reference"] = descriptor,
		};
		rows.Add(new PlanRow { Operation = operation, Kind = "reference_add", ArtifactMember = label,
			Target = descriptor.TryGetValue("form", out var form) ? form as string ?? string.Empty : string.Empty });
		return ObjectId(index);
	}

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
		var operation = new Dictionary<string, object?> {
			["kind"] = "type_add",
			["name"] = artifactType.Name.String,
			["namespace"] = artifactType.Namespace?.String ?? string.Empty,
			["attributes"] = (uint)artifactType.Attributes,
		};
		if (artifactType.BaseType != null) {
			var arity = artifactType.GenericParameters.Count;
			operation["base_type"] = EmitType(artifactType.BaseType.ToTypeSig()
				?? throw Reject("add", "the base type signature is outside the importable domain"), arity, 0, forceStructured: arity != 0);
		}
		if (container != null) operation["owner_type"] = container;
		// The object id is pinned after signature planning: planning can append
		// reference_add rows, so the id must match the row's final position.
		var index = NextIndex;
		operation["__object_id"] = ObjectId(index);
		Record(operation, "type_add", artifactType, artifactType.FullName, container == null ? "module" : "nested");
		createdTypeNames.Add(artifactType.FullName);
		return ObjectRef(index);
	}

	static string ObjectId(int index) => "obj-" + index.ToString("D3", CultureInfo.InvariantCulture) + "-00";

	void EmitGenericParameterAdd(GenericParam gp, Dictionary<string, object?> ownerRef) {
		var operation = new Dictionary<string, object?> {
			["kind"] = "generic_parameter_add",
			["owner"] = ownerRef,
			["generic_index"] = (int)gp.Number,
			["name"] = gp.Name?.String ?? string.Empty,
			["attributes"] = (uint)gp.Flags,
		};
		if (gp.GenericParamConstraints.Count != 0)
			operation["constraints"] = gp.GenericParamConstraints
				.Select(constraint => EmitType(constraint.Constraint?.ToTypeSig()
					?? throw Reject("add", "the generic constraint is outside the importable domain"), (int)gp.Number, 0))
				.ToArray();
		Record(operation, "generic_parameter_add", null, gp.Owner is TypeDef owner ? owner.FullName : gp.Name?.String ?? string.Empty, gp.Name?.String ?? string.Empty);
	}

	void EmitInterfaceAdd(Dictionary<string, object?> ownerRef, InterfaceImpl implemented, string ownerName) {
		var signature = implemented.Interface?.ToTypeSig()
			?? throw Reject("add", "the interface signature is outside the importable domain on " + ownerName);
		var operation = new Dictionary<string, object?> {
			["kind"] = "interface_add",
			["owner_type"] = ownerRef,
			["interface"] = new Dictionary<string, object?> { ["type"] = NodeJson(CaptureNode(signature)) },
		};
		rows.Add(new PlanRow { Operation = operation, Kind = "interface_add", ArtifactMember = implemented.Interface.FullName, Target = ownerName });
	}

	Dictionary<string, object?> EmitFieldAdd(FieldDef field, Dictionary<string, object?> ownerRef) {
		if (field.Constant != null)
			throw Reject("add", "literal fields are outside the importable member domain: " + field.FullName);
		if (field.MarshalType != null || field.FieldOffset != null || (field.InitialValue != null && field.InitialValue.Length != 0))
			throw Reject("add", "the field shape is outside the frozen operation language: " + field.FullName);
		var ownerArity = field.DeclaringType?.GenericParameters.Count ?? 0;
		var operation = new Dictionary<string, object?> {
			["kind"] = "field_add",
			["owner_type"] = ownerRef,
			["field_type"] = EmitType(field.FieldType, ownerArity),
			["name"] = field.Name.String,
			["attributes"] = (uint)field.Attributes,
		};
		var index = NextIndex;
		operation["__object_id"] = ObjectId(index);
		Record(operation, "field_add", field, field.FullName, field.FullName);
		var self = ObjectRef(index);
		EmitAttributeRows(self, field.CustomAttributes, field.FullName);
		return self;
	}

	Dictionary<string, object?> EmitMethodAdd(MethodDef method, Dictionary<string, object?> ownerRef, bool deferBody) {
		if (method.ImplMap != null || method.DeclSecurities.Count != 0)
			throw Reject("add", "the method shape is outside the frozen operation language: " + method.FullName);
		var ownerArity = method.DeclaringType?.GenericParameters.Count ?? 0;
		var signature = new Dictionary<string, object?> {
			["return_type"] = EmitType(method.MethodSig.RetType, ownerArity, method.GenericParameters.Count, allowVoid: true),
			["parameters"] = method.MethodSig.Params.Select((p, i) => new Dictionary<string, object?> {
				["type"] = EmitType(p, ownerArity, method.GenericParameters.Count),
				["name"] = ParamName(method, i),
			}).ToArray(),
			["has_this"] = method.MethodSig.HasThis,
			["generic_parameters"] = method.GenericParameters.Select(gp => new Dictionary<string, object?> {
				["name"] = gp.Name?.String ?? string.Empty,
				["attributes"] = (uint)gp.Flags,
				["constraints"] = gp.GenericParamConstraints.Count == 0 ? null
					: gp.GenericParamConstraints.Select(c => EmitType(c.Constraint?.ToTypeSig(), ownerArity, (int)gp.Number)).ToArray(),
			}).ToArray(),
		};
		var operation = new Dictionary<string, object?> {
			["kind"] = "method_add",
			["owner_type"] = ownerRef,
			["name"] = method.Name.String,
			["signature"] = signature,
			["attributes"] = (uint)method.Attributes,
			["impl_attributes"] = (uint)method.ImplAttributes,
		};
		if (method.Overrides.Count != 0)
		operation["overrides"] = method.Overrides.Select(overrideRow => (object)new Dictionary<string, object?> {
				["declaration"] = RowDescriptor(Bind(overrideRow.MethodDeclaration switch {
					MemberRef declaration => declaration,
					MethodDef declaration => declaration,
					_ => throw Reject("add", "the override declaration shape is outside the importable domain: " + method.FullName),
				})),
			}).ToArray();
		if (!deferBody && method.HasBody) {
			operation["body"] = EncodeBody(method);
			var inlineCdi = EditPdbTransferCodec.CaptureMethodDebugInfo(method, BindReference);
			if (inlineCdi.Count != 0) operation["custom_debug_infos"] = inlineCdi;
		}
		// The object id is pinned after override/body planning so it matches
		// the row's final position in the plan.
		var index = NextIndex;
		operation["__object_id"] = ObjectId(index);
		Record(operation, "method_add", method, method.FullName, method.FullName);
		var self = ObjectRef(index);
		EmitAttributeRows(self, method.CustomAttributes, method.FullName);
		if (deferBody && method.HasBody)
			pendingFills.Add(new PendingFill { Method = method, Target = self, Member = method.FullName });
		return self;
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
		var ownerArity = property.DeclaringType?.GenericParameters.Count ?? 0;
		var operation = new Dictionary<string, object?> {
			["kind"] = "property_add",
			["owner_type"] = ownerRef,
			["name"] = property.Name.String,
			["property_type"] = EmitType(property.PropertySig.RetType, ownerArity),
			["attributes"] = (uint)property.Attributes,
		};
		if (property.PropertySig.Params.Count != 0)
			operation["index_parameter_types"] = property.PropertySig.Params
				.Select(p => EmitType(p, ownerArity)).ToArray();
		if (getter != null) operation["getter"] = new Dictionary<string, object?> { ["object_id"] = getter };
		if (setter != null) operation["setter"] = new Dictionary<string, object?> { ["object_id"] = setter };
		var index = NextIndex;
		operation["__object_id"] = ObjectId(index);
		Record(operation, "property_add", property, property.FullName, property.FullName);
		EmitAttributeRows(ObjectRef(index), property.CustomAttributes, property.FullName);
	}

	void EmitEventAdd(EventDef evt, Dictionary<string, object?> ownerRef) {
		var add = AccessorRef(evt.AddMethod ?? throw Reject("add", "an event without an add accessor is outside the importable domain: " + evt.FullName));
		var remove = AccessorRef(evt.RemoveMethod ?? throw Reject("add", "an event without a remove accessor is outside the importable domain: " + evt.FullName));
		var ownerArity = evt.DeclaringType?.GenericParameters.Count ?? 0;
		var operation = new Dictionary<string, object?> {
			["kind"] = "event_add",
			["owner_type"] = ownerRef,
			["name"] = evt.Name.String,
			["event_type"] = EmitType(evt.EventType?.ToTypeSig() ?? throw Reject("add", "the event type signature is outside the importable domain"), ownerArity),
			["attributes"] = (uint)evt.Attributes,
			["add_method"] = new Dictionary<string, object?> { ["object_id"] = add },
			["remove_method"] = new Dictionary<string, object?> { ["object_id"] = remove },
		};
		if (evt.InvokeMethod != null) operation["raise_method"] = new Dictionary<string, object?> { ["object_id"] = AccessorRef(evt.InvokeMethod) };
		var index = NextIndex;
		operation["__object_id"] = ObjectId(index);
		Record(operation, "event_add", evt, evt.FullName, evt.FullName);
		EmitAttributeRows(ObjectRef(index), evt.CustomAttributes, evt.FullName);
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

	/// <summary>Deferred bodies and symbol rows: emitted once every shell of
	/// the plan exists, so state-machine bodies can bind their kickoff and
	/// kickoffs their generated rows regardless of compiler order.</summary>
	void FlushFills() {
		foreach (var fill in pendingFills) {
			var operation = new Dictionary<string, object?> {
				["kind"] = "method_body_replace",
				["target"] = fill.Target,
				["body"] = EncodeBody(fill.Method),
			};
			var cdi = EditPdbTransferCodec.CaptureMethodDebugInfo(fill.Method, BindReference);
			if (cdi.Count != 0) operation["custom_debug_infos"] = cdi;
			Record(operation, "method_body_replace", null, fill.Member, fill.Member);
		}
		pendingFills.Clear();
	}

	// ------------------------------------------------------------- attributes

	/// <summary>Emit attribute_add rows for every custom attribute of a new
	/// member — compiler markers and state-machine attributes included (T004:
	/// markers are real rows now, never dropped).  System.Type values travel as
	/// structured nodes so typeof(state-machine) binds to the batch row.</summary>
	void EmitAttributeRows(Dictionary<string, object?> targetRef, IEnumerable<dnlib.DotNet.CustomAttribute> attributes, string member) {
		foreach (var attribute in attributes) {
			var constructor = attribute.Constructor as IMethod
				?? throw Reject("add", "the attribute constructor is outside the importable domain on " + member);
			var constructorOwner = constructor.DeclaringType
				?? throw Reject("add", "the attribute constructor has no declaring type on " + member);
			var operation = new Dictionary<string, object?> {
				["kind"] = "attribute_add",
				["target"] = targetRef,
				["constructor"] = new Dictionary<string, object?> {
					["attribute_type"] = EditPdbTransferCodec.SigText(constructorOwner.ToTypeSig()
						?? throw Reject("add", "the attribute type signature is outside the importable domain: " + constructorOwner.FullName)),
					["parameter_types"] = (constructor.MethodSig?.Params ?? Array.Empty<TypeSig>())
						.Select(p => (object)EditPdbTransferCodec.SigText(p)).ToArray(),
				},
			};
			var fixedArguments = attribute.ConstructorArguments.Select(argument => EmitCaValue(argument, member)).ToArray();
			if (fixedArguments.Length != 0) operation["fixed_arguments"] = fixedArguments;
			if (attribute.NamedArguments.Count != 0)
				operation["named_arguments"] = attribute.NamedArguments.Select(named => {
					if (named.Argument.Value == null)
						throw Reject("add", "null named argument values are outside the importable domain on " + member);
					return new Dictionary<string, object?> {
						["kind"] = named.IsField ? "field" : "property",
						["name"] = named.Name?.String ?? string.Empty,
						["type"] = EditPdbTransferCodec.SigText(named.Type ?? throw Reject("add", "the named argument type is missing on " + member)),
						["value"] = EmitCaValue(named.Argument, member),
					};
				}).ToArray();
			rows.Add(new PlanRow { Operation = operation, Kind = "attribute_add", ArtifactMember = member, Target = constructorOwner.FullName });
		}
	}

	object EmitCaValue(CAArgument argument, string member) {
		// Enum-typed arguments (and null named values) have no v1/v2 encoding
		// in the attribute domain; reject them at compile time so the staging
		// pass never mutates for an unsupported shape.
		if (argument.Type is TypeDefOrRefSig declared && declared.TypeDefOrRef.ResolveTypeDef()?.IsEnum == true)
			throw Reject("add", "enum attribute arguments are outside the importable domain on " + member);
		switch (argument.Value) {
		case null:
			return null!;
		case TypeSig signature:
			return TypeEntry(CaptureNode(signature));
		case ITypeDefOrRef reference:
			return TypeEntry(CaptureNode(reference.ToTypeSig()));
		case TypeSig[] signatures:
			return signatures.Select(signature => (object)TypeEntry(CaptureNode(signature))).ToArray();
		case bool value: return value;
		case char value: return (ushort)value;
		case sbyte value: return value;
		case byte value: return value;
		case short value: return value;
		case ushort value: return value;
		case int value: return value;
		case uint value: return value;
		case long value: return value;
		case ulong value: return value;
		case float value: return value;
		case double value: return value;
		case string value: return value;
		default:
			throw Reject("add", "the attribute argument shape is outside the importable domain on " + member + ": " + argument.Value.GetType().Name);
		}
	}

	// ------------------------------------------------- structured type entries

	/// <summary>Loss-free capture of a signature with every reference planned
	/// (existing target rows, batch rows or synthesized reference rows).</summary>
	EditStructuredSignatureCodec.TypeNode CaptureNode(TypeSig signature) =>
		EditStructuredSignatureCodec.Capture(signature, BindForCapture, BindCorLibForCapture) is { } node ? NormalizeNode(node) : throw Reject("signature", "a signature is missing");

	/// <summary>Resolve owner sentinels (rows created by the operation under
	/// construction) to ownerless nodes; a sentinel in a reference position is a
	/// real ordering violation.</summary>
	static EditStructuredSignatureCodec.TypeNode NormalizeNode(EditStructuredSignatureCodec.TypeNode node) {
		if (node.Owner == OwnerSentinel) node.Owner = null;
		if (node.Reference == OwnerSentinel)
			throw Reject("signature", "a signature references a row this import has not created yet");
		if (node.Owner != null && node.Owner.Length == 0) node.Owner = null;
		foreach (var child in node.Children) NormalizeNode(child);
		if (node.Call != null) NormalizeCall(node.Call);
		return node;
	}

	static EditStructuredSignatureCodec.CallNode NormalizeCall(EditStructuredSignatureCodec.CallNode call) {
		if (call.Result != null) NormalizeNode(call.Result);
		foreach (var parameter in call.Parameters) NormalizeNode(parameter);
		if (call.Optional != null)
			foreach (var optional in call.Optional) NormalizeNode(optional);
		return call;
	}

	static JsonElement NodeJson(EditStructuredSignatureCodec.TypeNode node) =>
		JsonSerializer.SerializeToElement(node, EditWire.JsonOptions);

	static Dictionary<string, object?> TypeEntry(EditStructuredSignatureCodec.TypeNode node) => new() {
		["kind"] = "type",
		["type"] = NodeJson(node),
	};

	/// <summary>Emit a type entry for a payload: the frozen v1 text when every
	/// planned reference resolves to an existing target row, otherwise a
	/// structured v2 node bound to the planned references.</summary>
	object EmitType(TypeSig? signature, int ownerTypeArity = 0, int ownerMethodArity = 0, bool allowVoid = false, bool forceStructured = false) {
		if (signature == null) throw Reject("signature", "a signature is missing");
		bool sawObject = false;
		var node = EditStructuredSignatureCodec.Capture(signature, row => {
			var text = BindForCapture(row);
			if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) sawObject = true;
			return text;
		}, BindCorLibForCapture);
		if (sawObject || forceStructured)
			return TypeEntry(NormalizeNode(node));
		try {
			return ResolveTypeText(signature, ownerTypeArity, ownerMethodArity, allowVoid);
		}
		catch (Exception ex) when (ex is ArgumentException or EditDomainException) {
			return TypeEntry(NormalizeNode(node));
		}
	}

	// ------------------------------------------------------------- body encode

	Dictionary<string, object?> EncodeBody(MethodDef method) {
		var body = method.Body ?? throw Reject("body", "the compiled member has no body: " + method.FullName);
		var ownerArity = method.DeclaringType?.GenericParameters.Count ?? 0;
		var methodArity = method.GenericParameters.Count;
		var locals = body.Variables.Select(local => new Dictionary<string, object?> {
			["type"] = EmitType(local.Type, ownerArity, methodArity),
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
			["catch_type"] = eh.CatchType == null ? null : EmitType(eh.CatchType.ToTypeSig(), ownerArity, methodArity),
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
		case MethodDef artifactMethod: return TokenOperand(PlannedText(FindMethodRow(artifactMethod), artifactMethod), "method", artifactMethod.FullName);
		case FieldDef artifactField: return TokenOperand(PlannedText(FindFieldRow(artifactField), artifactField), "field", artifactField.FullName);
		case TypeDef artifactType: return TokenOperand(PlannedText(FindTypeRow(artifactType), artifactType), "type", artifactType.FullName);
		case MemberRef member: return member.IsMethodRef
			? TokenOperand(PlannedText(FindMemberRefMethodRow(member), member), "method", member.FullName)
			: TokenOperand(PlannedText(FindMemberRefFieldRow(member), member), "field", member.FullName);
		case MethodSpec specification: return TokenOperand(PlannedText(FindMethodSpecRow(specification), specification), "method", specification.FullName,
			"artifact_key=" + ArtifactSpecKey(specification) + " target_keys=" + string.Join(" | ", methodSpecCache.Keys.Take(6)));
		case TypeSpec specification: return TokenOperand(PlannedText(FindTypeSpecRow(specification.TypeSig), specification), "type", specification.FullName);
		case TypeSig signature: return TokenOperand(PlannedText(FindTypeSpecRow(signature), signature), "type", signature.FullName);
		case ITypeDefOrRef typeReference: return TokenOperand(PlannedText(FindExternalTypeRow(typeReference), typeReference), "type", typeReference.FullName);
		case StandAloneSig or MethodSig:
			throw Reject("operand", "call-site signatures are outside the importable body domain: " + method.FullName);
		default:
			throw Reject("operand", "the operand shape is outside the importable body domain: " + operand.GetType().Name);
		}
	}

	string PlannedText(string mapped, IMDTokenProvider artifactRow) =>
		mapped.Length != 0 ? mapped : Bind(artifactRow);

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
	/// existing target row of the same shape or is synthesized.</summary>
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
	string BindReference(IMDTokenProvider artifactRow) {
		var mapped = artifactRow switch {
			MethodDef method => FindMethodRow(method),
			FieldDef field => FindFieldRow(field),
			TypeDef type => FindTypeRow(type),
			_ => string.Empty,
		};
		return mapped.Length != 0 ? mapped : Bind(artifactRow);
	}

	/// <summary>Target counterpart of an artifact type: a row this import
	/// created has none by definition; otherwise the exact full name, or the
	/// generated kickoff-name pattern when the compilers numbered the state
	/// machines differently (cached; null means unmapped).</summary>
	TypeDef? TargetFor(TypeDef artifactType) {
		if (generatedTargets.TryGetValue(artifactType, out var cached)) return cached;
		TypeDef? match = referenceText.ContainsKey(artifactType) ? null
			: EditImportMatcher.IsGeneratedType(artifactType) && artifactType.DeclaringType != null
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
	string ResolveTypeText(TypeSig? signature, int ownerTypeArity = 0, int ownerMethodArity = 0, bool allowVoid = false) {
		if (signature == null) throw Reject("signature", "a signature is missing");
		var text = EditPdbTransferCodec.SigText(signature, RebindGenerated);
		var parser = new EditTypeSigParser(target, ownerTypeArity, ownerMethodArity);
		TypeSig parsed;
		try { parsed = parser.Parse(text, allowVoid || signature.ElementType == ElementType.Void); }
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

	static string RequiredString(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())
			? value.GetString()! : throw Reject(name, name + " is required");

	static EditDomainException Reject(string location, string reason) =>
		new("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("import_" + location, reason));
}
