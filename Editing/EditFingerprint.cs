using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

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
					rows.Add("field|" + FieldKey(field) + "|" + (uint)field.Attributes + "|" + Constant(field.Constant) + "|" + Bytes(field.InitialValue) + "|" + field.FieldOffset + "|" + field.MarshalType + "|" + Attributes(field.CustomAttributes));
				foreach (var method in type.Methods.OrderBy(MethodKey, StringComparer.Ordinal)) {
					rows.Add("method|" + MethodKey(method) + "|" + (uint)method.Attributes + "|" + (uint)method.ImplAttributes + "|" + method.ImplMap + "|" + Attributes(method.CustomAttributes));
					foreach (var gp in method.GenericParameters.OrderBy(g => g.Number)) rows.Add(GenericRow("mgp", gp));
					foreach (var p in method.ParamDefs.OrderBy(p => p.Sequence)) rows.Add("param|" + p.Sequence + "|" + p.Name + "|" + (uint)p.Attributes + "|" + Constant(p.Constant) + "|" + p.MarshalType + "|" + Attributes(p.CustomAttributes));
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

	internal static bool IsWriterTombstoneType(TypeDef type) {
		// dnlib 4.5.0 PreserveTokensMetadata creates dummy.{GUID} for removed TypeDef
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
	static string Attributes(CustomAttributeCollection attributes) => string.Join(",", attributes.Select(a => a.Constructor?.FullName + ":" + a.RawData).OrderBy(x => x, StringComparer.Ordinal));
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
