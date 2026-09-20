using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>Package-only composite that preserves the old rename tool's same-module
/// TypeRef/MemberRef repair. Public edit_apply never dispatches to this operation.</summary>
internal static class EditLegacyRenameOperation {
	internal static Action<string, int>? TestMutationHook { get; set; }

	public static EditOperationOutcome Apply(ModuleDef module, JsonElement operation) => ApplyCore(module, operation, ascendingReferences: true);

	public static EditOperationOutcome ApplyInverse(ModuleDef module, JsonElement inverseEnvelope) {
		if (inverseEnvelope.ValueKind != JsonValueKind.Object || !inverseEnvelope.TryGetProperty("legacy", out var legacy))
			throw new EditDomainException("EDIT_VALIDATION_FAILED");
		return ApplyCore(module, legacy, ascendingReferences: false);
	}

	static EditOperationOutcome ApplyCore(ModuleDef module, JsonElement operation, bool ascendingReferences) {
		ValidateShape(operation);
		var targetKind = Text(operation, "target_kind");
		var definitionRow = operation.GetProperty("definition");
		var token = Token(definitionRow, "token");
		var oldName = Text(definitionRow, "old_name");
		var newName = Text(definitionRow, "new_name");
		var definition = ResolveDefinition(module, targetKind, token);
		if (Name(definition) != oldName) Invalid("definition_name_drift");
		RejectDuplicateDefinitionName(definition, newName);

		var expectedTable = targetKind == "type" ? "TypeRef" : "MemberRef";
		var rows = new List<(uint Token, string OldName, string NewName, IMDTokenProvider[] Values)>();
		uint? previous = null;
		foreach (var row in operation.GetProperty("references").EnumerateArray()) {
			if (Text(row, "table") != expectedTable) Invalid("reference_table");
			var rowToken = Token(row, "token");
			if (rowToken == 0 || previous != null && (ascendingReferences ? rowToken <= previous.Value : rowToken >= previous.Value)) Invalid("reference_order");
			previous = rowToken;
			var rowOld = Text(row, "old_name"); var rowNew = Text(row, "new_name");
			if (rowOld != oldName || rowNew != newName) Invalid("reference_name_contract");
			var values = ResolveReferenceInstances(module, expectedTable, rowToken);
			if (values.Length == 0 || values.Any(x => Name(x) != rowOld)) Invalid("reference_missing_or_drifted");
			rows.Add((rowToken, rowOld, rowNew, values));
		}

		// Build one ordered list before touching any name. Inverse applies the exact
		// reverse order, including restoring the definition last.
		var mutations = new List<(IMDTokenProvider Value, string OldName, string NewName, string Kind)>();
		if (ascendingReferences) mutations.Add((definition, oldName, newName, "definition"));
		foreach (var row in rows) {
			var values = ascendingReferences ? row.Values : Enumerable.Reverse(row.Values);
			foreach (var value in values) mutations.Add((value, row.OldName, row.NewName, "reference"));
		}
		if (!ascendingReferences) mutations.Add((definition, oldName, newName, "definition"));

		if (oldName != newName) {
			var changed = new List<(IMDTokenProvider Value, string OldName)>();
			try {
				var mutationIndex = 0;
				foreach (var mutation in mutations) {
					SetName(mutation.Value, mutation.NewName); changed.Add((mutation.Value, mutation.OldName));
					TestMutationHook?.Invoke(mutation.Kind, mutation.Kind == "definition" ? 0 : ++mutationIndex);
				}
			}
			catch {
				for (var index = changed.Count - 1; index >= 0; index--) SetName(changed[index].Value, changed[index].OldName);
				throw;
			}
		}
		return new EditOperationOutcome {
			Kind = EditOperationVersions.LegacySymbolRename, Target = "0x" + token.ToString("x8"), Before = oldName, After = newName,
			Undo = () => {
				// A late drift must not leave an earlier reference half-restored.
				for (var i = mutations.Count - 1; i >= 0; i--)
					if (Name(mutations[i].Value) != mutations[i].NewName)
						throw new InvalidOperationException("legacy rename " + mutations[i].Kind + " drift");
				for (var i = mutations.Count - 1; i >= 0; i--) SetName(mutations[i].Value, mutations[i].OldName);
			},
		};
	}

	public static void ValidateShape(JsonElement operation) {
		RequireOnly(operation, "kind", "target_kind", "definition", "references");
		if (Text(operation, "kind") != EditOperationVersions.LegacySymbolRename) Invalid();
		var targetKind = Text(operation, "target_kind");
		if (targetKind is not ("type" or "method" or "field")) Invalid();
		var definition = operation.GetProperty("definition");
		RequireOnly(definition, "token", "old_name", "new_name");
		Token(definition, "token");
		var oldName = Text(definition, "old_name"); var newName = Text(definition, "new_name");
		if (oldName.Length == 0 || newName.Length == 0 || oldName.IndexOf('\0') >= 0 || newName.IndexOf('\0') >= 0) Invalid();
		if (!operation.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array) Invalid();
		foreach (var row in references.EnumerateArray()) {
			RequireOnly(row, "table", "token", "old_name", "new_name");
			Token(row, "token"); Text(row, "table"); Text(row, "old_name"); Text(row, "new_name");
		}
	}

	public static Dictionary<string, object?> Reverse(JsonElement operation) {
		ValidateShape(operation);
		var definition = operation.GetProperty("definition");
		return new Dictionary<string, object?> {
			["kind"] = EditOperationVersions.LegacySymbolRename, ["target_kind"] = Text(operation, "target_kind"),
			["definition"] = new Dictionary<string, object?> {
				["token"] = Text(definition, "token"), ["old_name"] = Text(definition, "new_name"), ["new_name"] = Text(definition, "old_name"),
			},
			["references"] = operation.GetProperty("references").EnumerateArray().Reverse().Select(row => new Dictionary<string, object?> {
				["table"] = Text(row, "table"), ["token"] = Text(row, "token"),
				["old_name"] = Text(row, "new_name"), ["new_name"] = Text(row, "old_name"),
			}).ToArray(),
		};
	}

	static IMDTokenProvider ResolveDefinition(ModuleDef module, string kind, uint token) {
		IMDTokenProvider value;
		try { value = module.ResolveToken(token) ?? throw new Exception(); } catch { throw new EditDomainException("EDIT_VALIDATION_FAILED"); }
		if ((kind == "type" && value is TypeDef) || (kind == "method" && value is MethodDef) || (kind == "field" && value is FieldDef)) return value;
		throw new EditDomainException("EDIT_VALIDATION_FAILED");
	}

	static IMDTokenProvider[] ResolveReferenceInstances(ModuleDef module, string table, uint token) {
		IEnumerable<IMDTokenProvider> values = table == "TypeRef"
			? module.GetTypeRefs().Where(x => x.MDToken.Raw == token).Cast<IMDTokenProvider>()
			: EnumerateMemberRefs(module).Where(x => x.MDToken.Raw == token).Cast<IMDTokenProvider>();
		return values.Distinct(ReferenceComparer.Instance).ToArray();
	}

	static IEnumerable<MemberRef> EnumerateMemberRefs(ModuleDef module) {
		// dnlib GetMemberRefs explicitly returns a fresh object for generic rows.
		// Such a read-only table projection is not the attached, editable reference:
		// re-enumerating it after rename would falsely report the original disk name.
		// Prefer all attached instances for those tokens, retaining stable cached rows.
		var attached = new List<MemberRef>();
		foreach (var method in module.GetTypes().SelectMany(x => x.Methods).Where(x => x.HasBody)) foreach (var instruction in method.Body.Instructions) {
			if (instruction.Operand is MemberRef member) attached.Add(member);
			else if (instruction.Operand is MethodSpec spec && spec.Method is MemberRef methodRef) attached.Add(methodRef);
		}
		foreach (var row in attached) yield return row;
		var attachedTokens = new HashSet<uint>(attached.Select(x => x.MDToken.Raw));
		foreach (var row in module.GetMemberRefs()) {
			if (!attachedTokens.Contains(row.MDToken.Raw) || ReferenceEquals(row, module.ResolveToken(row.MDToken.Raw))) yield return row;
		}
	}

	static void RejectDuplicateDefinitionName(IMDTokenProvider value, string newName) {
		if (value is TypeDef type) {
			var duplicate = type.DeclaringType == null
				? type.Module.Types.Any(x => x != type && x.Namespace == type.Namespace && x.Name == newName)
				: type.DeclaringType.NestedTypes.Any(x => x != type && x.Name == newName);
			if (duplicate) Invalid();
		} else if (value is MethodDef method && method.DeclaringType.Methods.Any(x => x != method && x.Name == newName && new SigComparer().Equals(x.MethodSig, method.MethodSig))) Invalid();
		else if (value is FieldDef field && field.DeclaringType.Fields.Any(x => x != field && x.Name == newName)) Invalid();
	}

	static string Name(IMDTokenProvider value) => value switch { TypeRef x => x.Name, MemberRef x => x.Name, TypeDef x => x.Name, MethodDef x => x.Name, FieldDef x => x.Name, _ => string.Empty };
	static void SetName(IMDTokenProvider value, string name) { var utf8 = new UTF8String(name); switch (value) { case TypeRef x: x.Name = utf8; break; case MemberRef x: x.Name = utf8; break; case TypeDef x: x.Name = utf8; break; case MethodDef x: x.Name = utf8; break; case FieldDef x: x.Name = utf8; break; default: Invalid(); break; } }
	static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var row) && row.ValueKind == JsonValueKind.String ? row.GetString() ?? string.Empty : throw new EditDomainException("EDIT_VALIDATION_FAILED");
	static uint Token(JsonElement value, string name) { var text = Text(value, name); return text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal) && uint.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out var token) ? token : throw new EditDomainException("EDIT_VALIDATION_FAILED"); }
	static void RequireOnly(JsonElement value, params string[] names) { if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(x => !names.Contains(x.Name, StringComparer.Ordinal)) || names.Any(x => !value.TryGetProperty(x, out _))) Invalid(); }
	static void Invalid(string reason = "legacy_rename_invalid") => throw new EditDomainException("EDIT_VALIDATION_FAILED",
		new Dictionary<string, object?> { ["kind"] = "validation", ["reason"] = reason }, reason);

	sealed class ReferenceComparer : IEqualityComparer<IMDTokenProvider> {
		public static readonly ReferenceComparer Instance = new();
		public bool Equals(IMDTokenProvider? x, IMDTokenProvider? y) => ReferenceEquals(x, y);
		public int GetHashCode(IMDTokenProvider obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
