using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>Canonical semantic image used for private/live conflict detection.</summary>
internal static class EditFingerprint {
	public static string Compute(ModuleDef module) {
		return ComputeChannels(module, normalizeWriterManagedBodyHeader: false);
	}

	public static string ComputeRoundtrip(ModuleDef module) {
		// PreserveAll represents deleted metadata rows with dnlib-generated dummy_ptr types.
		// Their GUID names are writer bookkeeping rather than sample semantics and change on
		// a write/reload pass.  Round-trip validation therefore compares the complete semantic
		// projection (including resources and embedded sequence points) after removing only
		// those writer-owned rows.  The live/private conflict fingerprint above intentionally
		// keeps the canonical PE-image channel and remains stricter.
		return EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n",
			RoundtripProjection(module).OrderBy(x => x, StringComparer.Ordinal))));
	}

	static IEnumerable<string> RoundtripProjection(ModuleDef module) =>
		Projection(module, normalizeWriterManagedBodyHeader: true, excludeWriterTombstones: true);

	static string ComputeChannels(ModuleDef module, bool normalizeWriterManagedBodyHeader) {
		var channels = Channels(module, normalizeWriterManagedBodyHeader);
		return EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", channels.OrderBy(x => x, StringComparer.Ordinal))));
	}

	/// <summary>
	/// CHK-003 / CON-004: the external-drift guard.  Layers every canonical
	/// semantic channel with the identity/native rows that a dnSpy UI edit can
	/// touch but the frozen five-channel projection intentionally does not
	/// carry (managed entry point, AssemblyRef table, native Win32 resource
	/// tree and custom debug information rows).  Used ONLY for live-module
	/// drift comparisons against a baseline captured with the same function;
	/// the semantic fingerprint (<see cref="Compute"/>) and every persisted
	/// checkpoint value keep their frozen definition and historical values.
	/// </summary>
	public static string ComputeExternalGuard(ModuleDef module) {
		var rows = new List<string>(Channels(module, normalizeWriterManagedBodyHeader: false));
		rows.Add(module.ManagedEntryPoint is MethodDef entryMethod
			? "entrypoint|" + entryMethod.FullName + "|" + entryMethod.MDToken.Raw.ToString(CultureInfo.InvariantCulture)
			: "entrypoint|" + (module.ManagedEntryPoint?.ToString() ?? string.Empty));
		foreach (var reference in module.GetAssemblyRefs().OrderBy(r => r.FullName, StringComparer.Ordinal))
			rows.Add("asmref|" + reference.FullName + "|" + reference.MDToken.Raw.ToString(CultureInfo.InvariantCulture));
		// CHK-012: assembly flags and hash algorithm are editable metadata the
		// semantic projection does not carry (the assembly row is FullName only).
		rows.Add(module.Assembly == null ? "asmflags|none"
			: "asmflags|" + ((uint)module.Assembly.Attributes).ToString(CultureInfo.InvariantCulture)
				+ "|" + ((uint)module.Assembly.HashAlgorithm).ToString(CultureInfo.InvariantCulture));
		// assembly/module-level custom attributes are outside the semantic
		// projection's assembly row (FullName only) — render them into the guard
		foreach (var attribute in module.Assembly?.CustomAttributes ?? Enumerable.Empty<CustomAttribute>())
			rows.Add("asmattr|" + ExternalAttributeRow(attribute));
		foreach (var attribute in module.CustomAttributes)
			rows.Add("modattr|" + ExternalAttributeRow(attribute));
		// CHK-012: ClassLayout packing/size rows (P04 layout edits; the type row
		// carries only attributes).
		foreach (var type in module.GetTypes())
			if (type.ClassLayout != null)
				rows.Add("classlayout|" + TypeKey(type) + "|" + type.ClassLayout.PackingSize.ToString(CultureInfo.InvariantCulture)
					+ "|" + type.ClassLayout.ClassSize.ToString(CultureInfo.InvariantCulture));
		// CHK-012: declared-security rows on module/assembly/types/methods
		// (P04 security edits leave no row in the semantic projection).
		if (module.Assembly != null)
			rows.Add("declsec|assembly|" + DeclSecurityRows(module.Assembly.DeclSecurities));
		foreach (var type in module.GetTypes()) {
			rows.Add("declsec|t|" + TypeKey(type) + "|" + DeclSecurityRows(type.DeclSecurities));
			foreach (var method in type.Methods)
				rows.Add("declsec|m|" + MethodKey(method) + "|" + DeclSecurityRows(method.DeclSecurities));
		}
		rows.Add(Win32ResourceRows(module));
		// T002-R02 / CHK-021: complete CDI content rows.  The previous reflective
		// rendering truncated sequences, skipped public fields, degraded struct and
		// composite values to ToString and swallowed getter failures, so legal CDI
		// changes could stay invisible.  EditCdiGuard encodes every bound dnlib CDI
		// type explicitly with owner-bound references and full lists, and throws
		// EDIT_CAPABILITY_UNAVAILABLE instead of fabricating a hash when the
		// projection cannot be formed.  This transient guard is its only consumer.
		rows.AddRange(EditCdiGuard.Rows(module));
		return EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", rows.OrderBy(x => x, StringComparer.Ordinal))));
	}

	// This non-persistent guard must include argument values, including arrays
	// and boxed arguments. Type/constructor identity alone misses UI edits.
	static string ExternalAttributeRow(CustomAttribute attribute) => JsonSerializer.Serialize(new {
		constructor = attribute.Constructor?.FullName,
		arguments = attribute.ConstructorArguments.Select(ExternalAttributeArgument).ToArray(),
		named = attribute.NamedArguments.Select(ExternalNamedArgument).ToArray(),
	}, EditWire.JsonOptions);

	static object ExternalNamedArgument(CANamedArgument argument) => new {
		field = argument.IsField, name = argument.Name?.String,
		type = Sig(argument.Type), argument = ExternalAttributeArgument(argument.Argument),
	};

	static object ExternalAttributeArgument(CAArgument argument) => new {
		type = Sig(argument.Type), value = ExternalAttributeValue(argument.Value),
	};

	static object? ExternalAttributeValue(object? value) {
		if (value == null) return null;
		if (value is CAArgument argument) return ExternalAttributeArgument(argument);
		if (value is IList<CAArgument> array) return array.Select(ExternalAttributeArgument).ToArray();
		if (value is TypeSig type) return new { type = Sig(type) };
		if (value is UTF8String text) return new { utf8 = Convert.ToBase64String(text.Data) };
		if (value is string || value is bool || value is char || value.GetType().IsPrimitive)
			return new { kind = value.GetType().FullName, text = Convert.ToString(value, CultureInfo.InvariantCulture) };
		throw new InvalidOperationException("Unsupported custom attribute argument in live guard: " + value.GetType().FullName);
	}

	static string DeclSecurityRows(IList<dnlib.DotNet.DeclSecurity> rows) =>
		JsonSerializer.Serialize(rows.Select(row => new {
			action = (int)row.Action,
			attributes = row.SecurityAttributes.Select(attribute => new {
				type = attribute.AttributeType?.FullName,
				arguments = attribute.NamedArguments.Select(ExternalNamedArgument).ToArray(),
			}).ToArray(),
		}).ToArray(), EditWire.JsonOptions);

	static string Win32ResourceRows(ModuleDef module) {
		if (module.Win32Resources == null) return "win32|none";
		var rows = new List<string>();
		WalkWin32Directory(module.Win32Resources.Root, string.Empty, rows);
		rows.Sort(StringComparer.Ordinal);
		return "win32|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
	}

	static void WalkWin32Directory(dnlib.W32Resources.ResourceDirectory directory, string prefix, List<string> rows) {
		foreach (var data in directory.Data) {
			var blob = data.CreateReader().ToArray();
			rows.Add(prefix + "|" + ResourceNameKey(data.Name) + "|" + EditWire.Sha256(blob));
		}
		foreach (var child in directory.Directories)
			WalkWin32Directory(child, prefix + "/" + ResourceNameKey(child.Name), rows);
	}

	static string ResourceNameKey(dnlib.W32Resources.ResourceName name)
		=> name == null ? "?" : name.Name ?? ("#" + name.Id.ToString(CultureInfo.InvariantCulture));

	/// <summary>
	/// Test-only evidence seam for the canonical global-order invariant.  Both values are
	/// computed from the real channel enumerator; reversing enumeration changes the raw order
	/// hash while the production fingerprint remains the sorted canonical hash.
	/// </summary>
	public static string ChannelOrderHash(ModuleDef module, bool reverse) {
		IEnumerable<string> channels = Channels(module, normalizeWriterManagedBodyHeader: false);
		if (reverse)
			channels = channels.Reverse();
		return EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", channels)));
	}

	static string[] Channels(ModuleDef module, bool normalizeWriterManagedBodyHeader) {
		var projection = Projection(module, normalizeWriterManagedBodyHeader, excludeWriterTombstones: false);
		var pdbRows = projection.Where(x => x.StartsWith("sp|", StringComparison.Ordinal)).ToArray();
		var resourceRows = projection.Where(x => x.StartsWith("resource|", StringComparison.Ordinal)).ToArray();
		var bodyRows = projection.Where(x => x.StartsWith("body|", StringComparison.Ordinal) ||
			x.StartsWith("local|", StringComparison.Ordinal) || x.StartsWith("il|", StringComparison.Ordinal) ||
			x.StartsWith("eh|", StringComparison.Ordinal)).ToArray();
		var moduleRows = projection.Where(x => x.StartsWith("module|", StringComparison.Ordinal) ||
			x.StartsWith("assembly|", StringComparison.Ordinal)).ToArray();
		var graphRows = projection.Where(x => !x.StartsWith("sp|", StringComparison.Ordinal) &&
			!x.StartsWith("resource|", StringComparison.Ordinal) && !x.StartsWith("body|", StringComparison.Ordinal) &&
			!x.StartsWith("local|", StringComparison.Ordinal) && !x.StartsWith("il|", StringComparison.Ordinal) &&
			!x.StartsWith("eh|", StringComparison.Ordinal) && !x.StartsWith("module|", StringComparison.Ordinal) &&
			!x.StartsWith("assembly|", StringComparison.Ordinal)).ToArray();
		return new[] {
			"module-metadata|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", moduleRows))),
			"dnlib-object-graph|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", graphRows))),
			"method-body-il|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", bodyRows))),
			"managed-resource|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", resourceRows))),
			"embedded-pdb|" + EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", pdbRows))),
		};
	}

	public static string Difference(ModuleDef expected, ModuleDef actual) {
		var left = RoundtripProjection(expected).ToList();
		var right = RoundtripProjection(actual).ToList();
		var count = Math.Min(left.Count, right.Count);
		for (var i = 0; i < count; i++)
			if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
				return $"row {i}: expected [{left[i]}], actual [{right[i]}]";
		return left.Count == right.Count ? "no row difference" : $"row count expected {left.Count}, actual {right.Count}";
	}

	static List<string> Projection(ModuleDef module, bool normalizeWriterManagedBodyHeader, bool excludeWriterTombstones) {
		var rows = new List<string> {
				"module|" + module.Name + "|" + (module.Mvid?.ToString("D") ?? string.Empty) + "|" + module.Kind + "|" + module.RuntimeVersion,
			"assembly|" + (module.Assembly?.FullName ?? string.Empty),
		};
			foreach (var type in module.GetTypes().OrderBy(TypeKey, StringComparer.Ordinal)) {
			if (excludeWriterTombstones && (IsWriterTombstoneType(type) || EditDeletedRowsTombstone.IsTombstone(type)))
				continue;
				rows.Add("type|" + TypeKey(type) + "|" + (uint)type.Attributes + "|" + Sig(type.BaseType?.ToTypeSig()) + "|" + Attributes(type.CustomAttributes));
				foreach (var gp in type.GenericParameters.OrderBy(g => g.Number)) rows.Add(GenericRow("tgp", gp));
				foreach (var iface in type.Interfaces.OrderBy(i => i.Interface?.FullName, StringComparer.Ordinal)) rows.Add("interface|" + Sig(iface.Interface?.ToTypeSig()) + "|" + Attributes(iface.CustomAttributes));
				foreach (var field in type.Fields.OrderBy(FieldKey, StringComparer.Ordinal))
					rows.Add("field|" + FieldKey(field) + "|" + (uint)field.Attributes + "|" + Constant(field.Constant) + "|" + Bytes(field.InitialValue) + "|" + field.FieldOffset + "|" + MarshalRow(field.MarshalType) + "|" + Attributes(field.CustomAttributes));
				foreach (var method in type.Methods.OrderBy(MethodKey, StringComparer.Ordinal)) {
					rows.Add("method|" + MethodKey(method) + "|" + (uint)method.Attributes + "|" + (uint)method.ImplAttributes + "|" + ImplMapRow(method.ImplMap) + "|" + Attributes(method.CustomAttributes));
					foreach (var gp in method.GenericParameters.OrderBy(g => g.Number)) rows.Add(GenericRow("mgp", gp));
					foreach (var p in method.ParamDefs.OrderBy(p => p.Sequence)) rows.Add("param|" + p.Sequence + "|" + p.Name + "|" + (uint)p.Attributes + "|" + Constant(p.Constant) + "|" + MarshalRow(p.MarshalType) + "|" + Attributes(p.CustomAttributes));
				if (!method.HasBody) continue;
				// KeepOldMaxStack is a dnlib writer hint and is never part of the
				// encoded method body.  During writer/reload validation dnlib may also
				// canonicalize an otherwise equivalent empty/new body from false/0 to
				// true/8; structural validation independently proves the effective
				// stack/local invariants.  Live conflict fingerprints retain the two
				// encoded header values, while round-trip fingerprints omit only this
				// writer-managed header row.
				rows.Add(normalizeWriterManagedBodyHeader
					? "body|writer-normalized"
					: "body|" + method.Body.InitLocals + "|" + method.Body.MaxStack);
				foreach (var local in method.Body.Variables) rows.Add("local|" + local.Index + "|" + Sig(local.Type));
				for (var instructionIndex = 0; instructionIndex < method.Body.Instructions.Count; instructionIndex++) {
					var ins = method.Body.Instructions[instructionIndex];
					rows.Add("il|" + instructionIndex.ToString("X8", CultureInfo.InvariantCulture) + "|" + ins.OpCode.Code + "|" + Operand(ins.Operand, method.Body.Instructions));
					if (ins.SequencePoint != null) rows.Add("sp|" + ins.SequencePoint.Document?.Url + "|" + ins.SequencePoint.StartLine + "|" + ins.SequencePoint.StartColumn + "|" + ins.SequencePoint.EndLine + "|" + ins.SequencePoint.EndColumn);
				}
				foreach (var eh in method.Body.ExceptionHandlers) rows.Add("eh|" + eh.HandlerType + "|" + eh.CatchType?.FullName + "|" + InstructionIndex(method.Body.Instructions, eh.TryStart) + "|" + InstructionIndex(method.Body.Instructions, eh.TryEnd) + "|" + InstructionIndex(method.Body.Instructions, eh.HandlerStart) + "|" + InstructionIndex(method.Body.Instructions, eh.HandlerEnd) + "|" + InstructionIndex(method.Body.Instructions, eh.FilterStart));
			}
			foreach (var property in type.Properties.OrderBy(PropertyKey, StringComparer.Ordinal))
				rows.Add("property|" + PropertyKey(property) + "|" + (uint)property.Attributes + "|" + MethodKey(property.GetMethod) + "|" + MethodKey(property.SetMethod) + "|" + Attributes(property.CustomAttributes));
			foreach (var evt in type.Events.OrderBy(EventKey, StringComparer.Ordinal))
				rows.Add("event|" + EventKey(evt) + "|" + (uint)evt.Attributes + "|" + MethodKey(evt.AddMethod) + "|" + MethodKey(evt.RemoveMethod) + "|" + MethodKey(evt.InvokeMethod) + "|" + Attributes(evt.CustomAttributes));
		}
			foreach (var resource in module.Resources.OrderBy(r => r.Name.String, StringComparer.Ordinal)) {
			byte[] bytes = resource is EmbeddedResource embedded ? embedded.CreateReader().ToArray() : Array.Empty<byte>();
			rows.Add("resource|" + resource.ResourceType + "|" + resource.Name + "|" + EditWire.Sha256(bytes));
		}
		return rows;
	}

	/// <summary>CHK-022 / T003: strong persistent projection.  Every method-owned
	/// row (method/mgp/param/body/local/il/sp/eh) is bound to a structured method
	/// owner, every type-owned row (tgp/interface) to a structured type owner, and
	/// each group is encoded as a JSON array so owner and content can never
	/// collide.  Unknown signature or reference shapes fail with
	/// EDIT_CAPABILITY_UNAVAILABLE instead of degrading to a name; cyclic or
	/// over-deep signature/scope graphs fail loudly instead of looping or
	/// overflowing; and duplicate type/method owners are rejected because the
	/// projection cannot tell the two same-named entities apart.  The historical
	/// Compute/ComputeRoundtrip/Channels definitions above stay unchanged.</summary>
	public static string ComputeRoundtripStrong(ModuleDef module) =>
		EditWire.Sha256(Encoding.UTF8.GetBytes(string.Join("\n",
			StrongProjection(module).OrderBy(x => x, StringComparer.Ordinal))));

	// A legal metadata signature/scope graph is a tree (or a DAG); the bound is
	// far beyond what any real compiler emits.  Exceeding it fails with
	// EDIT_CAPABILITY_UNAVAILABLE rather than truncating or overflowing.
	internal const int StrongMaxGraphDepth = 2048;

	sealed class StrongWalk {
		readonly HashSet<object> active = new(ReferenceComparer.Instance);
		int depth;
		public void Enter(object node) {
			if (!active.Add(node)) throw StrongFailure("cyclic signature or scope graph");
			if (++depth > StrongMaxGraphDepth)
				throw StrongFailure("signature or scope graph exceeds the supported depth of " +
					StrongMaxGraphDepth.ToString(CultureInfo.InvariantCulture));
		}
		public void Exit(object node) { active.Remove(node); depth--; }
	}

	sealed class ReferenceComparer : IEqualityComparer<object> {
		internal static readonly ReferenceComparer Instance = new();
		public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
		public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
	}

	/// <summary>Strong projection.  Every signature, scope and declaring-type
	/// render goes through the guarded walk above; no historical projection
	/// helper (whose frozen behavior has no cycle guard) is used on this path,
	/// and the encoding is written as explicit structured text so no JSON
	/// serializer depth limit can truncate a legal deep signature.  Duplicate
	/// structured owners are rejected because the projection cannot tell two
	/// same-named entities apart, and encoding an ambiguous state as a stable
	/// hash would silently merge them.</summary>
	static IEnumerable<string> StrongProjection(ModuleDef module) {
		var rows = new List<string> {
				"module|" + module.Name + "|" + (module.Mvid?.ToString("D") ?? string.Empty) + "|" + module.Kind + "|" + module.RuntimeVersion,
			"assembly|" + (module.Assembly?.FullName ?? string.Empty),
		};
		var walk = new StrongWalk();
		var typeOwners = new HashSet<string>(StringComparer.Ordinal);
		var methodOwners = new HashSet<string>(StringComparer.Ordinal);
		foreach (var type in module.GetTypes().OrderBy(t => t.Name?.String, StringComparer.Ordinal)) {
			if (IsWriterTombstoneType(type) || EditDeletedRowsTombstone.IsTombstone(type)) continue;
			var typeOwner = StrongTypeOwnerText(type, walk);
			if (!typeOwners.Add(typeOwner))
				throw StrongFailure("duplicate type owner: " + OwnerLabel(type, null));
			rows.Add("type|" + StrongTypePath(type, walk) + "|" + (uint)type.Attributes + "|" + StrongSigText(type.BaseType?.ToTypeSig(), walk) + "|" + Attributes(type.CustomAttributes));
			foreach (var gp in type.GenericParameters.OrderBy(g => g.Number))
				rows.Add(OwnerGroup("tgp", typeOwner, new[] { StrongGenericRow("tgp", gp, walk) }));
			foreach (var iface in type.Interfaces.OrderBy(i => i.Interface?.Name?.String, StringComparer.Ordinal))
				rows.Add(OwnerGroup("interface", typeOwner,
					new[] { "interface|" + StrongTypeRefText(iface.Interface, walk) + "|" + Attributes(iface.CustomAttributes) }));
			foreach (var field in type.Fields.OrderBy(f => f.Name?.String, StringComparer.Ordinal))
				rows.Add("field|" + StrongFieldLabel(field, walk) + "|" + (uint)field.Attributes + "|" + Constant(field.Constant) + "|" + Bytes(field.InitialValue) + "|" + field.FieldOffset + "|" + MarshalRow(field.MarshalType) + "|" + Attributes(field.CustomAttributes));
			foreach (var method in type.Methods.OrderBy(m => m.Name?.String, StringComparer.Ordinal)) {
				var methodOwner = StrongMethodOwnerText(method, walk);
				if (!methodOwners.Add(methodOwner))
					throw StrongFailure("duplicate method owner: " + OwnerLabel(type, method));
				var content = new List<string> {
					"method|" + StrongMethodLabel(method, walk) + "|" + (uint)method.Attributes + "|" + (uint)method.ImplAttributes + "|" + ImplMapRow(method.ImplMap) + "|" + Attributes(method.CustomAttributes),
				};
				foreach (var gp in method.GenericParameters.OrderBy(g => g.Number)) content.Add(StrongGenericRow("mgp", gp, walk));
				foreach (var p in method.ParamDefs.OrderBy(p => p.Sequence))
					content.Add("param|" + p.Sequence + "|" + p.Name + "|" + (uint)p.Attributes + "|" + Constant(p.Constant) + "|" + MarshalRow(p.MarshalType) + "|" + Attributes(p.CustomAttributes));
				if (method.HasBody) {
					content.Add("body|writer-normalized");
					foreach (var local in method.Body.Variables) content.Add("local|" + local.Index + "|" + StrongSigText(local.Type, walk));
					for (var instructionIndex = 0; instructionIndex < method.Body.Instructions.Count; instructionIndex++) {
						var ins = method.Body.Instructions[instructionIndex];
						var indexText = instructionIndex.ToString("X8", CultureInfo.InvariantCulture);
						content.Add("il|" + indexText + "|" + ins.OpCode.Code + "|" + StrongOperand(ins.Operand, method.Body.Instructions, walk));
						// T003: a sequence point is bound to the exact instruction slot so
						// swapping two SPs inside one method cannot go unnoticed.
						if (ins.SequencePoint != null)
							content.Add("sp|" + indexText + "|" + ins.SequencePoint.Document?.Url + "|" + ins.SequencePoint.StartLine + "|" + ins.SequencePoint.StartColumn + "|" + ins.SequencePoint.EndLine + "|" + ins.SequencePoint.EndColumn);
					}
					foreach (var eh in method.Body.ExceptionHandlers)
						content.Add("eh|" + eh.HandlerType + "|" + StrongTypeRefText(eh.CatchType, walk) + "|" + InstructionIndex(method.Body.Instructions, eh.TryStart) + "|" + InstructionIndex(method.Body.Instructions, eh.TryEnd) + "|" + InstructionIndex(method.Body.Instructions, eh.HandlerStart) + "|" + InstructionIndex(method.Body.Instructions, eh.HandlerEnd) + "|" + InstructionIndex(method.Body.Instructions, eh.FilterStart));
				}
				rows.Add(OwnerGroup("method", methodOwner, content));
			}
			foreach (var property in type.Properties.OrderBy(p => p.Name?.String, StringComparer.Ordinal))
				rows.Add("property|" + StrongPropertyLabel(property, walk) + "|" + (uint)property.Attributes + "|" + StrongMethodRefLabel(property.GetMethod, walk) + "|" + StrongMethodRefLabel(property.SetMethod, walk) + "|" + Attributes(property.CustomAttributes));
			foreach (var evt in type.Events.OrderBy(e => e.Name?.String, StringComparer.Ordinal))
				rows.Add("event|" + StrongEventLabel(evt, walk) + "|" + (uint)evt.Attributes + "|" + StrongMethodRefLabel(evt.AddMethod, walk) + "|" + StrongMethodRefLabel(evt.RemoveMethod, walk) + "|" + StrongMethodRefLabel(evt.InvokeMethod, walk) + "|" + Attributes(evt.CustomAttributes));
		}
		foreach (var resource in module.Resources.OrderBy(r => r.Name.String, StringComparer.Ordinal)) {
			byte[] bytes = resource is EmbeddedResource embedded ? embedded.CreateReader().ToArray() : Array.Empty<byte>();
			rows.Add("resource|" + resource.ResourceType + "|" + resource.Name + "|" + EditWire.Sha256(bytes));
		}
		return rows;
	}

	static string OwnerLabel(TypeDef type, MethodDef? method) {
		var label = StrongTypePath(type, new StrongWalk());
		if (method == null) return label;
		return label + "::" + (method.Name?.String ?? string.Empty) + "(" +
			(method.MethodSig?.Params.Count ?? -1).ToString(CultureInfo.InvariantCulture) + ")";
	}

	// Owner groups embed the already-encoded owner text plus quoted content
	// rows; the result is a valid JSON object and every boundary is explicit,
	// so two owners or two content rows can never merge by delimiter collision.
	static string OwnerGroup(string kind, string ownerText, IReadOnlyList<string> content) =>
		"{\"kind\":" + StrongQuote(kind) + ",\"owner\":" + ownerText + ",\"content\":[" +
		string.Join(",", content.OrderBy(x => x, StringComparer.Ordinal).Select(StrongQuote)) + "]}";

	static string StrongQuote(string? value) => JsonSerializer.Serialize(value ?? string.Empty);
	static string StrongNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

	// Cycle-safe structured type path used by every strong row and label.  The
	// declaring-type walk refuses to loop when malformed metadata nests a type
	// inside itself.
	static string StrongTypePath(TypeDef type, StrongWalk walk) {
		var chain = new List<string>();
		var seen = new HashSet<TypeDef>(ReferenceComparer.Instance);
		var outer = type;
		for (var current = type; current != null; current = current.DeclaringType) {
			if (!seen.Add(current)) throw StrongFailure("cyclic type declaring chain");
			outer = current;
			chain.Insert(0, current.Name?.String ?? string.Empty);
		}
		var ns = outer.Namespace?.String;
		return (string.IsNullOrEmpty(ns) ? string.Empty : ns + ".") + string.Join("/", chain);
	}

	static string StrongTypeOwnerText(TypeDef type, StrongWalk walk) {
		var chain = new List<string>();
		var seen = new HashSet<TypeDef>(ReferenceComparer.Instance);
		var outer = type;
		for (var current = type; current != null; current = current.DeclaringType) {
			if (!seen.Add(current)) throw StrongFailure("cyclic type declaring chain");
			outer = current;
			chain.Insert(0, current.Name?.String ?? string.Empty);
		}
		return "[\"type_owner\"," + StrongQuote(outer.Namespace?.String) + ",[" +
			string.Join(",", chain.Select(StrongQuote)) + "]]";
	}

	static string StrongMethodOwnerText(MethodDef method, StrongWalk walk) {
		var declaring = method.DeclaringType ?? throw StrongFailure("method without declaring type");
		return "[\"method_owner\"," + StrongTypeOwnerText(declaring, walk) + "," +
			StrongQuote(method.Name?.String) + "," + StrongMethodSignatureText(method.MethodSig, walk) + "]";
	}

	static string StrongSigText(TypeSig? value, StrongWalk walk) {
		if (value == null) return "null";
		walk.Enter(value);
		try {
			switch (value) {
			case GenericSig generic:
				return "[\"" + (generic.IsMethodVar ? "mvar" : "var") + "\"," + StrongNumber(generic.Number) + "]";
			case GenericInstSig instance:
				return "[\"generic_inst\"," + StrongSigText(instance.GenericType, walk) + ",[" +
					string.Join(",", instance.GenericArguments.Select(argument => StrongSigText(argument, walk))) + "]]";
			case TypeDefOrRefSig type:
				return "[\"type_def_or_ref\"," + StrongQuote(type.ElementType.ToString()) + "," + StrongTypeRefText(type.TypeDefOrRef, walk) + "]";
			case FnPtrSig function:
				if (function.Signature is not MethodSig method)
					throw StrongFailure("fnptr signature is not a method signature: " + (function.Signature?.GetType().FullName ?? "null"));
				return "[\"fnptr\"," + StrongMethodSignatureText(method, walk) + "]";
			case ArraySig array:
				return "[\"array\"," + StrongSigText(array.Next, walk) + "," + StrongNumber(array.Rank) + ",[" +
					string.Join(",", array.Sizes) + "],[" + string.Join(",", array.LowerBounds) + "]]";
			case SZArraySig szarray:
				return "[\"szarray\"," + StrongSigText(szarray.Next, walk) + "]";
			case PtrSig pointer:
				return "[\"ptr\"," + StrongSigText(pointer.Next, walk) + "]";
			case ByRefSig byref:
				return "[\"byref\"," + StrongSigText(byref.Next, walk) + "]";
			case PinnedSig pinned:
				return "[\"pinned\"," + StrongSigText(pinned.Next, walk) + "]";
			case CModReqdSig required:
				return "[\"modreq\"," + StrongTypeRefText(required.Modifier, walk) + "," + StrongSigText(required.Next, walk) + "]";
			case CModOptSig optional:
				return "[\"modopt\"," + StrongTypeRefText(optional.Modifier, walk) + "," + StrongSigText(optional.Next, walk) + "]";
			case SentinelSig:
				return "[\"sentinel\"]";
			case ModuleSig moduleSignature:
				return "[\"module_sig\"," + StrongNumber(moduleSignature.Index) + "," + StrongSigText(moduleSignature.Next, walk) + "]";
			default:
				throw StrongFailure("unsupported signature element: " + value.GetType().FullName);
			}
		}
		finally { walk.Exit(value); }
	}

	static string StrongMethodSignatureText(MethodSig? value, StrongWalk walk) {
		if (value == null) return "null";
		walk.Enter(value);
		try {
			return "[" + StrongNumber((byte)value.CallingConvention) + "," + (value.HasThis ? "true" : "false") + "," +
				(value.ExplicitThis ? "true" : "false") + "," + StrongNumber(value.GenParamCount) + "," +
				StrongSigText(value.RetType, walk) + ",[" +
				string.Join(",", value.Params.Select(parameter => StrongSigText(parameter, walk))) + "]," +
				(value.ParamsAfterSentinel == null ? "null" : "[" +
					string.Join(",", value.ParamsAfterSentinel.Select(parameter => StrongSigText(parameter, walk))) + "]") + "]";
		}
		finally { walk.Exit(value); }
	}

	static string StrongPropertySignatureText(PropertySig? value, StrongWalk walk) {
		if (value == null) return "null";
		walk.Enter(value);
		try {
			return "[" + StrongNumber((byte)value.CallingConvention) + "," + (value.HasThis ? "true" : "false") + "," +
				StrongSigText(value.RetType, walk) + ",[" +
				string.Join(",", value.Params.Select(parameter => StrongSigText(parameter, walk))) + "]]";
		}
		finally { walk.Exit(value); }
	}

	static string StrongTypeRefText(ITypeDefOrRef? type, StrongWalk walk) {
		if (type == null) return "null";
		walk.Enter(type);
		try {
			switch (type) {
			case TypeDef definition: return "[\"type_def\"," + StrongTypeOwnerText(definition, walk) + "]";
			case TypeRef reference: return "[\"type_ref\"," + StrongScopeText(reference.ResolutionScope, walk) + "," +
				StrongQuote(reference.Namespace?.String) + "," + StrongQuote(reference.Name?.String) + "]";
			case TypeSpec spec: return "[\"type_spec\"," + StrongSigText(spec.TypeSig, walk) + "]";
			default: throw StrongFailure("unsupported type reference kind: " + type.GetType().FullName);
			}
		}
		finally { walk.Exit(type); }
	}

	static string StrongScopeText(IResolutionScope? scope, StrongWalk walk) {
		if (scope == null) return "null";
		// A nested TypeRef owns its own traversal entry; entering it here too
		// mistakes every valid nested reference for a cycle.
		if (scope is TypeRef nestedScope) return StrongTypeRefText(nestedScope, walk);
		walk.Enter(scope);
		try {
			switch (scope) {
			case AssemblyRef assembly: return "[\"assembly_ref\"," + StrongQuote(assembly.Name?.String) + "," +
				StrongQuote(assembly.Version?.ToString()) + "," + StrongQuote(assembly.Culture?.String) + "," +
				StrongQuote(assembly.PublicKeyOrToken?.Data == null ? null : EditWire.Sha256(assembly.PublicKeyOrToken.Data)) + "," +
				StrongNumber((uint)assembly.Attributes) + "]";
			case ModuleRef moduleRef: return "[\"module_ref\"," + StrongQuote(moduleRef.Name?.String) + "]";
			case ModuleDef definition: return "[\"module_def\"," + StrongQuote(definition.Name?.String) + "," + StrongQuote(definition.Mvid?.ToString("D")) + "]";
			case AssemblyDef assemblyDefinition: return "[\"assembly_def\"," + StrongQuote(assemblyDefinition.FullName) + "]";
			default: throw StrongFailure("unsupported resolution scope kind: " + scope.GetType().FullName);
			}
		}
		finally { walk.Exit(scope); }
	}

	static string StrongFieldLabel(FieldDef field, StrongWalk walk) =>
		StrongPathPrefix(field.DeclaringType, walk) + field.Name + ":" + StrongSigText(field.FieldType, walk);

	static string StrongMethodLabel(MethodDef method, StrongWalk walk) =>
		StrongPathPrefix(method.DeclaringType, walk) + method.Name + StrongMethodSignatureText(method.MethodSig, walk);

	static string StrongMethodRefLabel(IMethod? method, StrongWalk walk) {
		if (method == null) return string.Empty;
		return StrongTypeRefText(method.DeclaringType, walk) + "::" + method.Name + StrongMethodSignatureText(method.MethodSig, walk);
	}

	static string StrongPropertyLabel(PropertyDef property, StrongWalk walk) =>
		StrongPathPrefix(property.DeclaringType, walk) + property.Name + "|" + StrongPropertySignatureText(property.PropertySig, walk);

	static string StrongEventLabel(EventDef evt, StrongWalk walk) =>
		StrongPathPrefix(evt.DeclaringType, walk) + evt.Name + ":" + StrongSigText(evt.EventType?.ToTypeSig(), walk);

	static string StrongPathPrefix(TypeDef? type, StrongWalk walk) =>
		(type == null ? string.Empty : StrongTypePath(type, walk)) + "::";

	static string StrongGenericRow(string prefix, GenericParam gp, StrongWalk walk) =>
		prefix + "|" + gp.Number + "|" + gp.Name + "|" + (uint)gp.Flags + "|" +
		string.Join(",", gp.GenericParamConstraints.Select(c => StrongSigText(c.Constraint?.ToTypeSig(), walk)).OrderBy(x => x, StringComparer.Ordinal)) + "|" + Attributes(gp.CustomAttributes);

	// Strong IL operand rendering mirrors the historical grammar but never calls
	// the unguarded historical Sig/MethodSignature helpers.
	static string StrongOperand(object? operand, IList<Instruction> instructions, StrongWalk walk) {
		if (operand == null) return string.Empty;
		if (operand is Instruction instruction) return "label:" + instructions.IndexOf(instruction).ToString(CultureInfo.InvariantCulture);
		if (operand is IList<Instruction> list) return "switch:" + string.Join(",", list.Select(x => instructions.IndexOf(x).ToString(CultureInfo.InvariantCulture)));
		if (operand is TypeDef typeDefinition) return "type:" + StrongTypeRefText(typeDefinition, walk);
		if (operand is MethodDef method) return "method:" + StrongMethodLabel(method, walk);
		if (operand is FieldDef field) return "field:" + StrongFieldLabel(field, walk);
		if (operand is TypeSig typeSignature) return "type:" + StrongSigText(typeSignature, walk);
		if (operand is ITypeDefOrRef typeRef) return "type:" + StrongTypeRefText(typeRef, walk);
		if (operand is MethodSpec instance) {
			walk.Enter(instance);
			try {
				var signature = instance.GenericInstMethodSig ?? throw StrongFailure("method instantiation has no signature");
				return "method-spec:[" + StrongQuote(StrongMethodRefLabel(instance.Method, walk)) + ",[" +
					string.Join(",", signature.GenericArguments.Select(argument => StrongSigText(argument, walk))) + "]]";
			}
			finally { walk.Exit(instance); }
		}
		if (operand is MethodSig callSite) return "callsite:" + StrongMethodSignatureText(callSite, walk);
		if (operand is IMethod called) return "method:" + StrongMethodRefLabel(called, walk);
		if (operand is IField referencedField) return "field:" + StrongTypeRefText(referencedField.DeclaringType, walk) + "::" + referencedField.Name + ":" + StrongSigText(referencedField.FieldSig?.Type, walk);
		if (operand is Local local) return "local:" + local.Index + ":" + StrongSigText(local.Type, walk);
		if (operand is Parameter parameter) return "arg:" + parameter.Index + ":" + StrongSigText(parameter.Type, walk);
		if (operand is string || operand is bool || operand is char || operand.GetType().IsPrimitive)
			return Convert.ToString(operand, CultureInfo.InvariantCulture) ?? string.Empty;
		throw StrongFailure("unsupported IL operand kind: " + operand.GetType().FullName);
	}

	static EditDomainException StrongFailure(string reason) => new("EDIT_CAPABILITY_UNAVAILABLE",
		new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "strong_semantic", ["reason"] = reason });

	internal static bool IsWriterTombstoneType(TypeDef type) {		// dnlib 4.5.0 PreserveTokensMetadata creates dummy.{GUID} for removed TypeDef
		// rows and dummy_ptr.{GUID} as the owner of reused Field/Method/Param/Event/
		// Property rows.  Skip the whole writer-owned subtree during round-trip-only
		// comparison so its synthetic member rows cannot leak into the projection.
		var ns = type.Namespace?.String;
		return (string.Equals(ns, "dummy", StringComparison.Ordinal) ||
				string.Equals(ns, "dummy_ptr", StringComparison.Ordinal)) &&
			Guid.TryParse(type.Name?.String, out _);
	}


	static string GenericRow(string prefix, GenericParam gp) => prefix + "|" + gp.Number + "|" + gp.Name + "|" + (uint)gp.Flags + "|" + string.Join(",", gp.GenericParamConstraints.Select(c => Sig(c.Constraint?.ToTypeSig())).OrderBy(x => x, StringComparer.Ordinal)) + "|" + Attributes(gp.CustomAttributes);
	static string Token(IMDTokenProvider? provider) => provider == null ? string.Empty : "0x" + provider.MDToken.Raw.ToString("x8", CultureInfo.InvariantCulture);
	static string TypeKey(TypeDef? value) => value == null ? string.Empty : value.FullName;
	static string MethodKey(MethodDef? value) => value == null ? string.Empty : (value.DeclaringType?.FullName + "::" + value.Name + MethodSignature(value.MethodSig));
	static string FieldKey(FieldDef value) => value.DeclaringType?.FullName + "::" + value.Name + ":" + Sig(value.FieldType);
	static string PropertyKey(PropertyDef value) => value.DeclaringType?.FullName + "::" + value.Name + PropertySignature(value.PropertySig);
	static string EventKey(EventDef value) => value.DeclaringType?.FullName + "::" + value.Name + ":" + Sig(value.EventType?.ToTypeSig());
	static string Bytes(byte[]? bytes) => bytes == null ? string.Empty : EditWire.Sha256(bytes);
	static string Attributes(CustomAttributeCollection attributes) => string.Join(",", attributes.Select(CustomAttributeRow).OrderBy(x => x, StringComparer.Ordinal));
	// RawData is null for constructed attributes and a byte[] after a reload, so
	// the canonical row projects the parsed shape instead (P04 round-trip fact).
	static string CustomAttributeRow(CustomAttribute a) {
		var fixedArguments = string.Join(",", a.ConstructorArguments.Select(x => x.Value == null ? "null" : x.Value.ToString()));
		var named = string.Join(",", a.NamedArguments.Select(x => (x.IsField ? "f:" : "p:") + x.Name + "=" + (x.Argument.Value == null ? "null" : x.Argument.Value.ToString())).OrderBy(x => x, StringComparer.Ordinal));
		return a.Constructor?.FullName + ":" + fixedArguments + ":" + named;
	}
	// MarshalType and ImplMap rows differ as User-vs-MD object ToString; project
	// their semantic fields instead (P04).
	static string MarshalRow(MarshalType? marshal) {
		if (marshal == null) return "";
		return JsonSerializer.Serialize(EditMarshalCodec.Capture(marshal, sig => (object)sig.FullName), EditWire.JsonOptions);
	}
	static string ImplMapRow(ImplMap? map) {
		if (map == null) return "";
		return map.Module?.Name + ":" + map.Name + ":" + (uint)map.Attributes;
	}
	static string InstructionIndex(IList<Instruction> instructions, Instruction? instruction) => instruction == null ? "end" : instructions.IndexOf(instruction).ToString(CultureInfo.InvariantCulture);
	static string Constant(Constant? value) => value == null ? string.Empty : value.Type + ":" + System.Convert.ToString(value.Value, CultureInfo.InvariantCulture);
	static string Operand(object? operand, IList<Instruction> instructions) {
		if (operand == null) return string.Empty;
		if (operand is Instruction i) return "label:" + instructions.IndexOf(i).ToString(CultureInfo.InvariantCulture);
		if (operand is IList<Instruction> list) return "switch:" + string.Join(",", list.Select(x => instructions.IndexOf(x).ToString(CultureInfo.InvariantCulture)));
		if (operand is TypeDef td) return "type:" + TypeKey(td);
		if (operand is MethodDef method) return "method:" + MethodKey(method);
		if (operand is FieldDef field) return "field:" + FieldKey(field);
		if (operand is TypeSig operandSig) return "type:" + Sig(operandSig);
		// InlineType/InlineTok operands encode a TypeDef/TypeRef/TypeSpec token.  A
		// class-vs-valuetype prefix is not part of a TypeDefOrRef token, and dnlib can
		// infer a different prefix after write/reload when resolution availability
		// changes.  Keep the encoded semantic identity here; signatures continue to
		// retain their class/value element type through Sig().
		if (operand is ITypeDefOrRef typeRef) return "type:" + typeRef.FullName;
		if (operand is IType type) return "type:" + type.FullName;
		if (operand is IMethod called) return "method:" + called.DeclaringType?.FullName + "::" + called.Name + MethodSignature(called.MethodSig);
		if (operand is IField referencedField) return "field:" + referencedField.DeclaringType?.FullName + "::" + referencedField.Name + ":" + Sig(referencedField.FieldSig?.Type);
		if (operand is IMDTokenProvider md) return md.GetType().Name + ":" + Token(md) + ":" + operand;
		if (operand is Local local) return "local:" + local.Index + ":" + Sig(local.Type);
		if (operand is Parameter parameter) return "arg:" + parameter.Index + ":" + Sig(parameter.Type);
		return System.Convert.ToString(operand, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	static string MethodSignature(MethodSig? value) {
		if (value == null) return string.Empty;
		return "|" + (byte)value.CallingConvention + "|" + value.HasThis + "|" + value.ExplicitThis + "|" + value.GenParamCount +
			"|" + Sig(value.RetType) + "(" + string.Join(",", value.Params.Select(Sig)) + ")" +
			(value.ParamsAfterSentinel == null ? string.Empty : "...(" + string.Join(",", value.ParamsAfterSentinel.Select(Sig)) + ")");
	}

	static string PropertySignature(PropertySig? value) {
		if (value == null) return string.Empty;
		return "|" + (byte)value.CallingConvention + "|" + value.HasThis + "|" + Sig(value.RetType) +
			"(" + string.Join(",", value.Params.Select(Sig)) + ")";
	}

	static string Sig(TypeSig? value) {
		if (value == null) return string.Empty;
		if (value is GenericSig generic)
			return (generic.IsMethodVar ? "!!" : "!") + generic.Number.ToString(CultureInfo.InvariantCulture);
		if (value is GenericInstSig instance)
			return "GenericInst(" + Sig(instance.GenericType) + "<" + string.Join(",", instance.GenericArguments.Select(Sig)) + ">)";
		if (value is TypeDefOrRefSig type)
			return value.ElementType + ":" + type.TypeDefOrRef?.FullName;
		if (value is FnPtrSig function && function.Signature is MethodSig method)
			return "FnPtr(" + MethodSignature(method) + ")";
		if (value is ArraySig array)
			return "Array(" + Sig(array.Next) + ";rank=" + array.Rank + ";sizes=" + string.Join(",", array.Sizes) +
				";bounds=" + string.Join(",", array.LowerBounds) + ")";
		return value.ElementType + "(" + Sig(value.Next) + ")";
	}
}
