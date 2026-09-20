using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>T004-R02: single source of truth for the (kind, kind_version)
/// capability table.  New kinds start at version 1; an existing kind that uses
/// a structure/value domain introduced after the frozen v1 grammar is persisted
/// with version 2 so an old reader rejects the row before applying anything.
/// The forward payload alone determines the required version
/// (<see cref="RequiredVersion"/>); <see cref="Validate"/> enforces that a row
/// claiming a version stays inside that version's domain.</summary>
internal static class EditOperationVersions {
	internal const int V1 = 1;
	internal const int V2 = 2;
	internal const string LegacySymbolRename = "legacy_symbol_rename";

	// Kinds whose first release is part of the v1 grammar.
	static readonly HashSet<string> NewKinds = new(StringComparer.Ordinal) {
		"interface_add", "reference_add",
	};
	// Existing kinds that actually gained a new structure/value domain.  Every
	// other kind stays v1-only: claiming version 2 for it is a version error,
	// not a licence to bypass the strict per-version domain check.
	static readonly HashSet<string> V2Kinds = new(StringComparer.Ordinal) {
		"attribute_add",
		"type_add", "type_update",
		"field_add", "field_update",
		"method_add", "method_update", "method_body_replace",
		"property_add", "property_update",
		"event_add", "event_update",
		"parameter_add", "parameter_update",
		"generic_parameter_add", "generic_parameter_update",
	};
	// CDI kinds added after the frozen v1 symbol domain.
	internal static bool IsV2CdiKind(string kind) =>
		string.Equals(kind, "enc_state_map", StringComparison.Ordinal);

	// The historical rename composite is persisted and replayed only by the
	// compatibility adapter.  It belongs in the history version table, but not
	// in EditWire.OperationKinds (the public edit_apply capability surface).
	internal static bool IsKnownKind(string kind) =>
		EditWire.OperationKinds.Contains(kind, StringComparer.Ordinal)
		|| string.Equals(kind, LegacySymbolRename, StringComparison.Ordinal);

	internal static bool IsSupported(string kind, int version) {
		if (!IsKnownKind(kind)) return false;
		if (string.Equals(kind, LegacySymbolRename, StringComparison.Ordinal)) return version == V1;
		if (NewKinds.Contains(kind)) return version == V1;
		if (V2Kinds.Contains(kind)) return version == V1 || version == V2;
		return version == V1;
	}

	internal static int RequiredVersion(string kind, JsonElement forward) {
		if (NewKinds.Contains(kind)) return V1;
		if (UsesV2Domain(kind, forward)) return V2;
		return V1;
	}

	internal static void Validate(string kind, int version, JsonElement forward) {
		if (!IsSupported(kind, version)) throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
		if (version == V1 && RequiredVersion(kind, forward) != V1)
			throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
	}

	static bool UsesV2Domain(string kind, JsonElement forward) {
		switch (kind) {
		case "type_add":
		case "type_update":
			return Structured(forward, "base_type");
		case "field_add":
		case "field_update":
			return Structured(forward, "field_type");
		case "method_add":
		case "method_update":
			if (Structured(forward, "return_type")) return true;
			if (forward.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.Object) {
				if (Structured(signature, "return_type")) return true;
				if (signature.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array
					&& parameters.EnumerateArray().Any(p => Structured(p, "type"))) return true;
				if (signature.TryGetProperty("generic_parameters", out var gps) && gps.ValueKind == JsonValueKind.Array
					&& gps.EnumerateArray().Any(gp => gp.TryGetProperty("constraints", out var constraints)
						&& constraints.ValueKind == JsonValueKind.Array && constraints.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object)))
					return true;
			}
			return StructuredOverrides(forward) || V2Cdi(forward) || StructuredBody(forward);
		case "method_body_replace":
			return V2Cdi(forward) || StructuredBody(forward);
		case "property_add":
		case "property_update":
			return Structured(forward, "property_type") || StructuredArray(forward, "index_parameter_types");
		case "event_add":
		case "event_update":
			return Structured(forward, "event_type");
		case "parameter_add":
		case "parameter_update":
			return Structured(forward, "parameter_type");
		case "generic_parameter_add":
		case "generic_parameter_update":
			return StructuredArray(forward, "constraints");
		case "attribute_add":
			return StructuredAttributeArguments(forward);
		default:
			return false;
		}
	}

	static bool Structured(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.Object;

	static bool Structured(JsonElement element) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty("kind", out var kind)
			&& kind.ValueKind == JsonValueKind.String && kind.GetString() == "type";

	static bool StructuredArray(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(Structured);

	// token/object_id override declarations are the v2 form; the legacy
	// owner_type/name/parameter_types declaration stays v1.
	static bool StructuredOverrides(JsonElement forward) {
		if (forward.ValueKind != JsonValueKind.Object || !forward.TryGetProperty("overrides", out var overrides)
			|| overrides.ValueKind != JsonValueKind.Array) return false;
		return overrides.EnumerateArray().Any(row =>
			row.ValueKind == JsonValueKind.Object && row.TryGetProperty("declaration", out var declaration)
			&& declaration.ValueKind == JsonValueKind.Object
			&& !declaration.TryGetProperty("owner_type", out _));
	}

	static bool V2Cdi(JsonElement forward) {
		foreach (var container in EnumerateCdiContainers(forward))
			if (container.ValueKind == JsonValueKind.Array && container.EnumerateArray().Any(row =>
				row.ValueKind == JsonValueKind.Object && row.TryGetProperty("kind", out var kind)
				&& kind.ValueKind == JsonValueKind.String && IsV2CdiKind(kind.GetString()!))) return true;
		return false;
	}

	static bool StructuredBody(JsonElement forward) {
		if (forward.ValueKind != JsonValueKind.Object || !forward.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return false;
		if (body.TryGetProperty("locals", out var locals) && locals.ValueKind == JsonValueKind.Array
			&& locals.EnumerateArray().Any(local => Structured(local, "type"))) return true;
		if (body.TryGetProperty("exception_handlers", out var handlers) && handlers.ValueKind == JsonValueKind.Array
			&& handlers.EnumerateArray().Any(handler => Structured(handler, "catch_type"))) return true;
		return false;
	}

	static bool StructuredAttributeArguments(JsonElement forward) {
		if (forward.ValueKind != JsonValueKind.Object) return false;
		return AttributeValues(forward, "fixed_arguments") || AttributeValues(forward, "named_arguments");
	}

	static bool AttributeValues(JsonElement forward, string name) {
		if (!forward.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return false;
		foreach (var row in values.EnumerateArray()) {
			var value = name == "named_arguments" && row.ValueKind == JsonValueKind.Object && row.TryGetProperty("value", out var inner) ? inner : row;
			if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("kind", out var kind)
				&& kind.ValueKind == JsonValueKind.String && kind.GetString() == "type") return true;
			if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(entry =>
				entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("kind", out var entryKind)
				&& entryKind.ValueKind == JsonValueKind.String && entryKind.GetString() == "type")) return true;
		}
		return false;
	}

	internal static IEnumerable<JsonElement> EnumerateCdiContainers(JsonElement forward) {
		if (forward.ValueKind == JsonValueKind.Object && forward.TryGetProperty("custom_debug_infos", out var cdi))
			yield return cdi;
	}
}
