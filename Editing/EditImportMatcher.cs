using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P06 stable-identity matcher (IMP-001): locates compile-artifact members and
/// their target-module counterparts by namespace/nesting/name/generic arity and
/// a structured signature captured through <see cref="EditStructuredSignatureCodec"/>.
/// Cross-module reference keys rebind artifact references to the target assembly
/// ("t:") and keep every other external reference scoped ("x:") so an overload
/// never matches by name or parameter count alone.  Zero or multiple candidates
/// are hard rejections — never a guess.
/// </summary>
internal sealed class EditImportMatcher {
	readonly ModuleDef artifact;
	readonly ModuleDef target;
	readonly string targetAssemblyName;

	public EditImportMatcher(ModuleDef artifact, ModuleDef target) {
		this.artifact = artifact;
		this.target = target;
		targetAssemblyName = target.Assembly?.Name?.String ?? string.Empty;
	}

	/// <summary>A parsed <c>compiled</c> reference: the unique-signature form
	/// "Type.Full.Name::Member`Arity(param,param)" (fields and accessors carry
	/// no parameter list, constructors are .ctor/.cctor).</summary>
	public sealed class CompiledReference {
		public string TypeFullName { get; set; } = string.Empty;
		public string MemberName { get; set; } = string.Empty;
		public int Arity { get; set; }
		public string[] Parameters { get; set; } = Array.Empty<string>();
		public bool HasParameters { get; set; }
	}

	public static CompiledReference ParseCompiled(string compiled) {
		var separator = compiled.IndexOf("::", StringComparison.Ordinal);
		if (separator < 0) {
			// bare type reference (new-type adds)
			if (compiled.Length == 0 || compiled.Contains("("))
				throw Reject("compiled", "compiled must be 'Type.FullName::Member`Arity(params)' or a bare type full name");
			return new CompiledReference { TypeFullName = compiled };
		}
		if (separator == 0 || separator + 2 >= compiled.Length)
			throw Reject("compiled", "compiled must be 'Type.FullName::Member`Arity(params)'");
		var row = new CompiledReference { TypeFullName = compiled.Substring(0, separator) };
		var member = compiled.Substring(separator + 2);
		var open = member.IndexOf("(", StringComparison.Ordinal);
		var head = open < 0 ? member : member.Substring(0, open);
		if (open >= 0) {
			if (!member.EndsWith(")", StringComparison.Ordinal))
				throw Reject("compiled", "compiled parameter list is unterminated");
			row.HasParameters = true;
			var parameterBody = member.Substring(open + 1, member.Length - open - 2);
			row.Parameters = parameterBody.Length == 0 ? Array.Empty<string>() : SplitParameters(parameterBody);
		}
		var tick = head.LastIndexOf('`');
		if (tick > 0 && int.TryParse(head.Substring(tick + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var arity) && arity > 0) {
			row.MemberName = head.Substring(0, tick);
			row.Arity = arity;
		}
		else {
			row.MemberName = head;
			row.Arity = 0;
		}
		if (row.MemberName.Length == 0 || row.TypeFullName.Length == 0)
			throw Reject("compiled", "compiled member name or type name is empty");
		return row;
	}

	static string[] SplitParameters(string body) {
		var rows = new List<string>();
		var depth = 0;
		var start = 0;
		for (var index = 0; index < body.Length; index++) {
			var character = body[index];
			if (character == '<' || character == '[') depth++;
			else if (character == '>' || character == ']') depth--;
			else if (character == ',' && depth == 0) {
				rows.Add(body.Substring(start, index - start));
				start = index + 1;
			}
		}
		rows.Add(body.Substring(start));
		return rows.Select(p => p.Trim()).Where(p => p.Length != 0).ToArray();
	}

	public TypeDef ArtifactType(string fullName) =>
		artifact.GetTypes().SingleOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal))
		?? throw Reject("compiled", "the compiled type was not found in the artifact: " + fullName);

	public TypeDef? TargetType(TypeDef artifactType) =>
		target.GetTypes().SingleOrDefault(t => string.Equals(t.FullName, artifactType.FullName, StringComparison.Ordinal));

	/// <summary>Generated-type counterpart by kickoff-name pattern: compilers
	/// number state machines per compilation, so "&lt;DoCoroutine&gt;d__0" and
	/// "&lt;DoCoroutine&gt;d__1" are the same generated type.  The kickoff-name
	/// prefix must match exactly one candidate; zero or multiple are hard rejects.</summary>
	public TypeDef? TargetGeneratedType(TypeDef generated, TypeDef declaring) {
		var prefix = GeneratedPrefix(generated.Name.String);
		if (prefix == null) return TargetType(generated);
		// Walk the declaring chain: the direct owner resolves first (exact name
		// or by its own generated pattern), then the prefix must match exactly
		// one generated nested type under that owner.
		TypeDef? ResolveOwner(TypeDef owner) =>
			IsGeneratedType(owner) && owner.DeclaringType != null
				? TargetGeneratedType(owner, owner.DeclaringType)
				: TargetType(owner);
		var targetOwner = ResolveOwner(declaring)
			?? throw Reject("match", "the generated type's declaring type has no target counterpart: " + declaring.FullName);
		var candidates = targetOwner.NestedTypes
			.Where(t => { var candidatePrefix = GeneratedPrefix(t.Name.String); return candidatePrefix != null && string.Equals(candidatePrefix, prefix, StringComparison.Ordinal); })
			.ToArray();
		if (candidates.Length == 1) return candidates[0];
		if (candidates.Length == 0) return null;
		throw Reject("match", "the generated type matches " + candidates.Length + " target generated types: " + generated.FullName);
	}

	static string? GeneratedPrefix(string name) {
		if (!name.StartsWith("<", StringComparison.Ordinal)) return null;
		var closing = name.IndexOf(">", StringComparison.Ordinal);
		if (closing <= 1 || closing + 3 >= name.Length
			|| !name.Substring(closing + 1).StartsWith("d__", StringComparison.Ordinal)
			|| !int.TryParse(name.Substring(closing + 4), out _))
			return null;
		return name.Substring(0, closing + 1);
	}

	public TypeDef? TargetType(string fullName) =>
		target.GetTypes().SingleOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal));

	/// <summary>Locate the artifact member named by the parsed reference.  The
	/// parameter texts are parsed in the target binder context and compared as
	/// structured keys against the artifact signatures.</summary>
	public MethodDef ArtifactMethod(CompiledReference reference) {
		var owner = ArtifactType(reference.TypeFullName);
		var parsed = ParseTargetParameters(reference, owner);
		var key = ArtifactSignatureKey(reference.MemberName, reference.Arity, parsed);
		var matches = owner.Methods.Where(m => string.Equals(m.Name.String, reference.MemberName, StringComparison.Ordinal)
			&& m.GenericParameters.Count == reference.Arity
			&& string.Equals(MethodKey(m), key, StringComparison.Ordinal)).ToArray();
		return matches.Length == 1 ? matches[0] : throw Reject("compiled",
			"the compiled method reference resolved to " + matches.Length + " artifact members (need exactly one)");
	}

	public FieldDef ArtifactField(CompiledReference reference) {
		var owner = ArtifactType(reference.TypeFullName);
		var matches = owner.Fields.Where(f => string.Equals(f.Name.String, reference.MemberName, StringComparison.Ordinal)).ToArray();
		return matches.Length == 1 ? matches[0] : throw Reject("compiled",
			"the compiled field reference resolved to " + matches.Length + " artifact members (need exactly one)");
	}

	/// <summary>Parse the reference's parameter texts with the target module as
	/// binder; unresolved names may still address artifact-local members, so the
	/// parser output is only used for key comparison.</summary>
	TypeSig[] ParseTargetParameters(CompiledReference reference, TypeDef ownerType) {
		if (!reference.HasParameters) return Array.Empty<TypeSig>();
		var parser = new EditTypeSigParser(target, ownerType.GenericParameters.Count, reference.Arity);
		return reference.Parameters.Select(p => parser.Parse(p, p == "System.Void")).ToArray();
	}

	public string ArtifactSignatureKey(string name, int arity, TypeSig[] targetContextParameters) =>
		"m:" + name + "|" + arity + "(" + string.Join(",", targetContextParameters.Select(p => Key(p, target))) + ")";

	public string MethodKey(MethodDef method) =>
		"m:" + method.Name.String + "|" + method.GenericParameters.Count
		+ "(" + string.Join(",", method.MethodSig.Params.Select(p => Key(p, artifact))) + ")";

	public string TargetMethodKey(MethodDef method) =>
		"m:" + method.Name.String + "|" + method.GenericParameters.Count
		+ "(" + string.Join(",", method.MethodSig.Params.Select(p => Key(p, target))) + ")";

	/// <summary>Exactly-one target counterpart match by name, arity and
	/// structured parameter signatures; the return type is part of the captured
	/// signature comparison as well.</summary>
	public MethodDef MatchTargetMethod(MethodDef artifactMethod, TypeDef targetOwner) {
		var artifactKey = FullSignatureKey(artifactMethod.MethodSig, artifact, artifactMethod);
		var matches = targetOwner.Methods.Where(m =>
			string.Equals(m.Name.String, artifactMethod.Name.String, StringComparison.Ordinal)
			&& m.GenericParameters.Count == artifactMethod.GenericParameters.Count
			&& string.Equals(FullSignatureKey(m.MethodSig, target, artifactMethod), artifactKey, StringComparison.Ordinal)).ToArray();
		if (matches.Length == 1) return matches[0];
		throw Reject("match", matches.Length == 0
			? "no target counterpart for the compiled member " + artifactMethod.FullName
			: "the compiled member " + artifactMethod.FullName + " matches " + matches.Length + " target members (ambiguous)");
	}

	public FieldDef MatchTargetField(FieldDef artifactField, TypeDef targetOwner) {
		var artifactKey = Key(artifactField.FieldType, artifact);
		var matches = targetOwner.Fields.Where(f =>
			string.Equals(f.Name.String, artifactField.Name.String, StringComparison.Ordinal)
			&& string.Equals(Key(f.FieldType, target), artifactKey, StringComparison.Ordinal)).ToArray();
		if (matches.Length == 1) return matches[0];
		throw Reject("match", matches.Length == 0
			? "no target counterpart for the compiled field " + artifactField.FullName
			: "the compiled field " + artifactField.FullName + " matches " + matches.Length + " target fields (ambiguous)");
	}

	public PropertyDef MatchTargetProperty(PropertyDef artifactProperty, TypeDef targetOwner) {
		var matches = targetOwner.Properties.Where(p =>
			string.Equals(p.Name.String, artifactProperty.Name.String, StringComparison.Ordinal)
			&& string.Equals(Key(p.PropertySig.RetType, target), Key(artifactProperty.PropertySig.RetType, artifact), StringComparison.Ordinal)
			&& p.PropertySig.Params.Count == artifactProperty.PropertySig.Params.Count).ToArray();
		if (matches.Length == 1) return matches[0];
		throw Reject("match", "the compiled property " + artifactProperty.FullName + " matches " + matches.Length + " target properties");
	}

	/// <summary>Accessor composite identity: the artifact accessor's owner
	/// property/event must pair with the matched target method's owner.</summary>
	public static void VerifyAccessorPair(MethodDef artifactAccessor, MethodDef targetAccessor) {
		var artifactPair = PairName(artifactAccessor);
		var targetPair = PairName(targetAccessor);
		if (!string.Equals(artifactPair, targetPair, StringComparison.Ordinal))
			throw Reject("match", "the matched target method is not the accessor of " + artifactPair);
	}

	static string PairName(MethodDef accessor) {
		var name = accessor.Name.String;
		foreach (var prefix in new[] { "get_", "set_", "add_", "remove_", "raise_" })
			if (name.StartsWith(prefix, StringComparison.Ordinal)) {
				var owner = accessor.DeclaringType;
				var propertyName = name.Substring(prefix.Length);
				var isEvent = prefix is "add_" or "remove_" or "raise_";
				var paired = isEvent
					? owner.Events.Any(e => string.Equals(e.Name.String, propertyName, StringComparison.Ordinal))
					: owner.Properties.Any(p => string.Equals(p.Name.String, propertyName, StringComparison.Ordinal));
				if (paired) return prefix + propertyName;
			}
		return name;
	}

	/// <summary>The state-machine type a kickoff method points at through its
	/// AsyncStateMachine/IteratorStateMachine attribute (the generated subtree
	/// root; null for plain methods).</summary>
	public static TypeDef? StateMachineType(MethodDef method) {
		foreach (var attribute in method.CustomAttributes) {
			var name = attribute.TypeFullName;
			if (!string.Equals(name, "System.Runtime.CompilerServices.AsyncStateMachineAttribute", StringComparison.Ordinal)
				&& !string.Equals(name, "System.Runtime.CompilerServices.IteratorStateMachineAttribute", StringComparison.Ordinal))
				continue;
			if (attribute.ConstructorArguments.Count != 1) continue;
			// Real Roslyn output stores the typeof(...) argument as a TypeSig
			// (ClassSig), hand-built artifacts as an ITypeDefOrRef.
			var stateMachine = attribute.ConstructorArguments[0].Value switch {
				TypeSig signature => signature.ToTypeDefOrRef(),
				ITypeDefOrRef reference => reference,
				_ => null,
			};
			if (stateMachine != null) return stateMachine.ResolveTypeDef();
		}
		return null;
	}

	/// <summary>Compiler-generated support type (state machine or closure class)
	/// that travels as one subtree unit with its kickoff member.</summary>
	public static bool IsGeneratedType(TypeDef type) {
		var name = type.Name.String;
		return name.StartsWith("<", StringComparison.Ordinal) || name.Contains("<>c", StringComparison.Ordinal);
	}

	/// <summary>Generated types that implement interfaces (async/iterator state
	/// machines, enumerators) cannot be created by the frozen operation language;
	/// plain closure classes can.</summary>
	public static bool IsStateMachineType(TypeDef type) =>
		IsGeneratedType(type) && type.Interfaces.Count != 0;

	/// <summary>Target-side key for an artifact provider row; artifact rows that
	/// address the target assembly rebind to "t:" so they equal target rows.</summary>
	public string ArtifactKeyRef(IMDTokenProvider provider) => KeyRef(provider, artifact);

	/// <summary>Same key grammar evaluated in the target module (for scanning
	/// existing target rows such as MethodSpec instantiations).</summary>
	public string TargetKeyRef(IMDTokenProvider provider) => KeyRef(provider, target);

	/// <summary>Full structured signature key through the codec: same node shape,
	/// same cross-module reference keys, same generic owner keys.</summary>
	string FullSignatureKey(MethodSig signature, ModuleDef module, MethodDef referenceOwner) {
		var ownerTypeArity = referenceOwner.DeclaringType?.GenericParameters.Count ?? 0;
		var ownerMethodArity = referenceOwner.GenericParameters.Count;
		string Bind(IMDTokenProvider provider) => KeyRef(provider, module);
		var captured = EditStructuredSignatureCodec.Capture(signature, Bind);
		var json = JsonSerializer.Serialize(captured, EditWire.JsonOptions);
		return ownerTypeArity + "|" + ownerMethodArity + "|" + json;
	}

	/// <summary>Structured type key usable across modules: target-assembly
	/// references rebind ("t:"), everything else stays scope-qualified ("x:").</summary>
	public static string Key(TypeSig type, ModuleDef module) => type switch {
		null => "",
		CorLibTypeSig core => "t:" + (core.TypeDefOrRef?.FullName ?? "System." + core.ElementType),
		GenericVar typeVar => "!" + typeVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericMVar methodVar => "!!" + methodVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericInstSig instance => "gi:" + KeyRef(instance.GenericType.TypeDefOrRef, module)
			+ "<" + string.Join(",", instance.GenericArguments.Select(argument => Key(argument, module))) + ">",
		SZArraySig array => Key(array.Next, module) + "[]",
		ArraySig array => Key(array.Next, module) + "[" + array.Rank.ToString(CultureInfo.InvariantCulture) + "]",
		ByRefSig byRef => Key(byRef.Next, module) + "&",
		PtrSig pointer => Key(pointer.Next, module) + "*",
		TypeDefOrRefSig reference => KeyRef(reference.TypeDefOrRef, module),
		_ => "x:" + type.FullName,
	};

	public string Key(TypeSig type) => Key(type, artifact);
	public string TargetKey(TypeSig type) => Key(type, target);

	static string KeyRef(IMDTokenProvider? provider, ModuleDef module) {
		if (provider == null) return "";
		switch (provider) {
		case TypeDef definition:
			return "t:" + definition.FullName;
		case TypeRef reference: {
			var scope = reference.ResolutionScope;
			if (scope is AssemblyRef assembly && string.Equals(assembly.Name, TargetAssemblyOf(module), StringComparison.OrdinalIgnoreCase))
				return "t:" + reference.FullName;
			return "x:" + reference.FullName;
		}
		case TypeSpec specification:
			return "ts:" + Key(specification.TypeSig, module);
		case MethodDef method:
			return "m:" + method.FullName;
		case FieldDef field:
			return "f:" + field.FullName;
		case MemberRef member:
			return "mr:" + member.FullName;
		case MethodSpec methodSpecification:
			return "ms:" + KeyRef(methodSpecification.Method, module) + "<"
				+ string.Join(",", (methodSpecification.GenericInstMethodSig?.GenericArguments ?? Array.Empty<TypeSig>())
					.Select(argument => Key(argument, module))) + ">";
		case GenericParam parameter:
			return "gp:" + parameter.Number.ToString(CultureInfo.InvariantCulture);
		default:
			return "x:" + provider.GetType().Name + ":" + ((IFullName)provider).FullName;
		}
	}

	static string TargetAssemblyOf(ModuleDef module) => module.Assembly?.Name?.String ?? string.Empty;

	static EditDomainException Reject(string location, string reason) =>
		new("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("import_" + location, reason));
}
