using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// Small Draft-2020-12 subset used by the generated, fully-expanded edit schemas.  Keeping the
/// validator beside the generated contract makes server-side -32602 rejection agree with hosts
/// that validate the same schema before dispatching a tool call.
/// </summary>
internal static class EditJsonSchemaValidator {
	public static void Validate(JsonElement schema, Dictionary<string, object>? arguments, string toolName) {
		using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments ?? new Dictionary<string, object>()));
		ValidateValue(schema, document.RootElement, toolName + " arguments");
	}

	public static void ValidateValue(JsonElement schema, JsonElement value, string subject) {
		if (!Matches(schema, value, out var reason))
			throw new ArgumentException($"{subject} does not match schema: {reason}", subject);
	}

	static bool Matches(JsonElement schema, JsonElement value, out string reason) {
		if (schema.ValueKind != JsonValueKind.Object) { reason = "schema node is not an object"; return false; }

		if (schema.TryGetProperty("oneOf", out var oneOf)) {
			var matches = 0;
			foreach (var candidate in oneOf.EnumerateArray())
				if (Matches(candidate, value, out _)) matches++;
			if (matches != 1) { reason = $"oneOf matched {matches} branches"; return false; }
		}
		if (schema.TryGetProperty("allOf", out var allOf)) {
			foreach (var candidate in allOf.EnumerateArray())
				if (!Matches(candidate, value, out reason)) return false;
		}
		if (schema.TryGetProperty("not", out var notSchema) && Matches(notSchema, value, out _)) {
			reason = "value matches forbidden schema"; return false;
		}
		if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value)) {
			reason = "value does not equal const"; return false;
		}
		if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => JsonEquals(x, value))) {
			reason = "value is not in enum"; return false;
		}

		if (schema.TryGetProperty("type", out var typeNode)) {
			var type = typeNode.GetString();
			if (!HasType(value, type)) { reason = $"expected {type}"; return false; }
		}

		switch (value.ValueKind) {
		case JsonValueKind.Object:
			return MatchesObject(schema, value, out reason);
		case JsonValueKind.Array:
			return MatchesArray(schema, value, out reason);
		case JsonValueKind.String:
			return MatchesString(schema, value.GetString() ?? string.Empty, out reason);
		case JsonValueKind.Number:
			return MatchesNumber(schema, value, out reason);
		default:
			reason = string.Empty; return true;
		}
	}

	static bool MatchesObject(JsonElement schema, JsonElement value, out string reason) {
		var properties = value.EnumerateObject().ToArray();
		if (schema.TryGetProperty("minProperties", out var minimum) && properties.Length < minimum.GetInt32()) {
			reason = "too few properties"; return false;
		}
		if (schema.TryGetProperty("maxProperties", out var maximum) && properties.Length > maximum.GetInt32()) {
			reason = "too many properties"; return false;
		}
		if (schema.TryGetProperty("required", out var required)) {
			foreach (var name in required.EnumerateArray().Select(x => x.GetString()!))
				if (!value.TryGetProperty(name, out _)) { reason = $"missing required property {name}"; return false; }
		}
		var hasPropertySchemas = schema.TryGetProperty("properties", out var propertySchemas);
		var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
		foreach (var property in properties) {
			if (hasPropertySchemas && propertySchemas.TryGetProperty(property.Name, out var propertySchema)) {
				if (!Matches(propertySchema, property.Value, out var nested)) { reason = property.Name + ": " + nested; return false; }
			}
			else if (rejectAdditional) { reason = $"additional property {property.Name}"; return false; }
		}
		reason = string.Empty; return true;
	}

	static bool MatchesArray(JsonElement schema, JsonElement value, out string reason) {
		var count = value.GetArrayLength();
		if (schema.TryGetProperty("minItems", out var minimum) && count < minimum.GetInt32()) {
			reason = "too few items"; return false;
		}
		if (schema.TryGetProperty("maxItems", out var maximum) && count > maximum.GetInt32()) {
			reason = "too many items"; return false;
		}
		if (schema.TryGetProperty("items", out var itemSchema)) {
			var index = 0;
			foreach (var item in value.EnumerateArray()) {
				if (!Matches(itemSchema, item, out var nested)) { reason = $"item {index}: {nested}"; return false; }
				index++;
			}
		}
		reason = string.Empty; return true;
	}

	static bool MatchesString(JsonElement schema, string value, out string reason) {
		if (schema.TryGetProperty("minLength", out var minimum) && value.Length < minimum.GetInt32()) {
			reason = "string is too short"; return false;
		}
		if (schema.TryGetProperty("maxLength", out var maximum) && value.Length > maximum.GetInt32()) {
			reason = "string is too long"; return false;
		}
		if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant)) {
			reason = "string does not match pattern"; return false;
		}
		reason = string.Empty; return true;
	}

	static bool MatchesNumber(JsonElement schema, JsonElement value, out string reason) {
		// Decimal preserves exact integer-boundary checks (notably UInt64.MaxValue), but
		// cannot represent the finite Single/Double range published by this schema. Fall
		// back to IEEE-754 only when either side is outside Decimal's range.
		var hasDecimal = value.TryGetDecimal(out var decimalNumber);
		if (!value.TryGetDouble(out var doubleNumber) || double.IsNaN(doubleNumber) || double.IsInfinity(doubleNumber)) {
			reason = "number is not a finite supported value"; return false;
		}
		if (schema.TryGetProperty("minimum", out var minimum)) {
			if (hasDecimal && minimum.TryGetDecimal(out var decimalMin) ? decimalNumber < decimalMin :
				!minimum.TryGetDouble(out var doubleMin) || doubleNumber < doubleMin) {
				reason = "number is below minimum"; return false;
			}
		}
		if (schema.TryGetProperty("maximum", out var maximum)) {
			if (hasDecimal && maximum.TryGetDecimal(out var decimalMax) ? decimalNumber > decimalMax :
				!maximum.TryGetDouble(out var doubleMax) || doubleNumber > doubleMax) {
				reason = "number is above maximum"; return false;
			}
		}
		reason = string.Empty; return true;
	}

	static bool HasType(JsonElement value, string? type) => type switch {
		"object" => value.ValueKind == JsonValueKind.Object,
		"array" => value.ValueKind == JsonValueKind.Array,
		"string" => value.ValueKind == JsonValueKind.String,
		"integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
		"number" => value.ValueKind == JsonValueKind.Number,
		"boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
		"null" => value.ValueKind == JsonValueKind.Null,
		_ => true,
	};

	static bool IsInteger(JsonElement value) {
		if (value.TryGetInt64(out _) || value.TryGetUInt64(out _)) return true;
		return value.TryGetDecimal(out var number) && decimal.Truncate(number) == number;
	}

	static bool JsonEquals(JsonElement left, JsonElement right) {
		if (left.ValueKind != right.ValueKind) return false;
		return left.ValueKind switch {
			JsonValueKind.Object or JsonValueKind.Array => left.GetRawText() == right.GetRawText(),
			JsonValueKind.String => left.GetString() == right.GetString(),
			JsonValueKind.Number => left.TryGetDecimal(out var a) && right.TryGetDecimal(out var b) && a == b,
			_ => true,
		};
	}
}
