using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace dnSpy.Extension.MCP.Editing;

internal static class EditWire {
	public const string SchemaVersion = "dnspy.edit.v1";
	public const int MaxOperations = 256;
	public const int MaxObjectIds = 4096;
	public const int MaxNormalizedOperationBytes = 8 * 1024 * 1024;
	public const int MaxDiffBytes = 512 * 1024;
	public const int MaxBodyInstructions = 4096;
	public const int MaxBodyLocals = 1024;
	public const int MaxBodyExceptionHandlers = 512;
	public const int MaxLiveSteps = 256;
	public const int MaxModuleBytes = 16 * 1024 * 1024;
	public const int MaxMetadataRows = 100000;
	public const int MaxResourceBytes = 8 * 1024 * 1024;
	public const int MaxPdbBytes = 8 * 1024 * 1024;
	public const int MaxIlInstructions = 250000;
	public const int MaxDispatcherMs = 1000;
	public const int ApplyCacheEntries = 1024;
	public const int ApplyCacheBytes = 32 * 1024 * 1024;
	public const int ReviewTombstoneEntries = 64;
	public const int ReviewTombstoneBytes = 256 * 1024;
	public const int ReviewSlotBytes = 2560 * 1024;
	public const int RollbackSlotBytes = 1024 * 1024;
	public const long IdleTimeoutMs = 600000;

	public static readonly string[] OperationKinds = {
		"type_add", "type_update", "type_remove", "method_add", "method_update", "method_remove",
		"field_add", "field_update", "field_remove", "property_add", "property_update", "property_remove",
		"event_add", "event_update", "event_remove", "parameter_add", "parameter_update", "parameter_remove",
		"generic_parameter_add", "generic_parameter_update", "generic_parameter_remove", "method_body_replace",
	};

	public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	public static Dictionary<string, object?> Success(string state, object result) => new() {
		["schema_version"] = SchemaVersion,
		["ok"] = true,
		["state"] = state,
		["result"] = result,
		["warnings"] = Array.Empty<string>(),
		["untrusted_sample_data"] = true,
	};

	public static Dictionary<string, object?> Failure(string state, string code, object? details = null,
		string? message = null, string? recovery = null) => new() {
		["schema_version"] = SchemaVersion,
		["ok"] = false,
		["state"] = state,
		["error"] = new Dictionary<string, object?> {
			["code"] = code,
			["message"] = message ?? Message(code),
			["current_state"] = state,
			["recovery"] = recovery ?? Recovery(code),
			["details"] = details,
		},
		["warnings"] = Array.Empty<string>(),
		["untrusted_sample_data"] = true,
	};

	public static CallToolResult Result(Dictionary<string, object?> envelope) {
		var json = JsonSerializer.Serialize(envelope, JsonOptions);
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } },
			IsError = envelope.TryGetValue("ok", out var ok) && ok is false,
		};
	}

	public static string Message(string code) => code switch {
		"EDIT_TRANSACTION_BUSY" => "Another edit transaction is active",
		"EDIT_TRANSACTION_NOT_FOUND" => "The edit transaction does not exist",
		"EDIT_OWNER_REQUIRED" => "An initialized MCP transport session is required",
		"EDIT_OWNER_MISMATCH" => "The edit transaction belongs to another MCP session",
		"EDIT_REVISION_CONFLICT" => "The expected work revision does not match",
		"EDIT_LIVE_MODULE_CONFLICT" => "The live module changed after the transaction began",
		"EDIT_REVIEW_STALE" => "The review no longer matches this transaction revision",
		"EDIT_VALIDATION_FAILED" => "Structural validation failed",
		"EDIT_RISK_CONFIRMATION_REQUIRED" => "All required review risks must be confirmed",
		"EDIT_CAPABILITY_UNAVAILABLE" => "The requested edit capability is unavailable for this target",
		"EDIT_CAPACITY_EXCEEDED" => "An edit transaction capacity limit was exceeded",
		"EDIT_DEBUG_NOT_IDLE" => "The debugger must be idle",
		"EDIT_LIVE_STATE_UNKNOWN" => "Live state could not be restored reliably",
		"REQUEST_ID_REUSE" => "The request ID was reused with a different payload",
		_ => "The edit operation failed",
	};

	public static string Recovery(string code) => code switch {
		"EDIT_TRANSACTION_BUSY" => "Wait for or roll back the active transaction",
		"EDIT_OWNER_REQUIRED" => "Use an initialized SSE or Streamable HTTP MCP session",
		"EDIT_OWNER_MISMATCH" => "Retry from the owning MCP session",
		"EDIT_REVISION_CONFLICT" => "Read edit_status and retry with its work_revision",
		"EDIT_LIVE_MODULE_CONFLICT" => "Roll back and begin a new transaction from the current live module",
		"EDIT_REVIEW_STALE" => "Run edit_review again",
		"EDIT_RISK_CONFIRMATION_REQUIRED" => "Confirm every required risk ID from edit_review",
		"EDIT_LIVE_STATE_UNKNOWN" => "Use the DNMCP_TEST emergency cleanup seam or restart dnSpy",
		"REQUEST_ID_REUSE" => "Use a new request_id",
		_ => "Correct the request or roll back the transaction",
	};

	public static string NewId(string prefix) {
		var bytes = new byte[16];
		using var rng = RandomNumberGenerator.Create();
		rng.GetBytes(bytes);
		return prefix + "-" + string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
	}

	public static string Sha256(byte[] bytes) {
		using var hash = SHA256.Create();
		return string.Concat(hash.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
	}

	public static string CanonicalPayload(object? value) => JsonSerializer.Serialize(value, JsonOptions);
	public static int Utf8Bytes(object? value) => Encoding.UTF8.GetByteCount(CanonicalPayload(value));

	public static Dictionary<string, object?> Object(Dictionary<string, object>? args, string name, bool required = true) {
		if (args != null && args.TryGetValue(name, out var raw) && raw != null) {
			if (raw is JsonElement e && e.ValueKind == JsonValueKind.Object)
				return JsonSerializer.Deserialize<Dictionary<string, object?>>(e.GetRawText()) ?? new();
			if (raw is Dictionary<string, object?> nullable) return nullable;
			if (raw is Dictionary<string, object> plain) return plain.ToDictionary(p => p.Key, p => (object?)p.Value);
		}
		if (required) throw new ArgumentException(name + " is required and must be an object", name);
		return new();
	}

	public static string String(Dictionary<string, object>? args, string name, bool required = true) {
		if (args != null && args.TryGetValue(name, out var raw)) {
			if (raw is string s && (!required || s.Length != 0)) return s;
			if (raw is JsonElement e && e.ValueKind == JsonValueKind.String) {
				var s2 = e.GetString() ?? string.Empty;
				if (!required || s2.Length != 0) return s2;
			}
		}
		if (required) throw new ArgumentException(name + " is required and must be a non-empty string", name);
		return string.Empty;
	}

	public static long Integer(Dictionary<string, object>? args, string name, bool required = true, long minimum = 0) {
		if (args != null && args.TryGetValue(name, out var raw) && TryInt64(raw, out var value) && value >= minimum)
			return value;
		if (required) throw new ArgumentException(name + " is required and must be an integer >= " + minimum, name);
		return minimum;
	}

	public static bool TryInt64(object? raw, out long value) {
		if (raw is JsonElement e && e.ValueKind == JsonValueKind.Number) return e.TryGetInt64(out value);
		try { value = Convert.ToInt64(raw, CultureInfo.InvariantCulture); return raw != null; }
		catch { value = 0; return false; }
	}

	public static bool Bool(Dictionary<string, object>? args, string name, bool defaultValue = false) {
		if (args == null || !args.TryGetValue(name, out var raw)) return defaultValue;
		if (raw is bool b) return b;
		if (raw is JsonElement e && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)) return e.GetBoolean();
		throw new ArgumentException(name + " must be a boolean", name);
	}

	public static string? NullableString(Dictionary<string, object?> map, string name, bool distinguishMissing, out bool present) {
		present = map.TryGetValue(name, out var raw);
		if (!present) return null;
		if (raw == null || raw is JsonElement { ValueKind: JsonValueKind.Null }) return null;
		if (raw is string s) return s;
		if (raw is JsonElement e && e.ValueKind == JsonValueKind.String) return e.GetString();
		throw new ArgumentException(name + " must be a string or null", name);
	}
}

internal sealed class EditDomainException : Exception {
	public string Code { get; }
	public object? Details { get; }
	public EditDomainException(string code, object? details = null, string? message = null) : base(message ?? EditWire.Message(code)) {
		Code = code;
		Details = details;
	}
}
