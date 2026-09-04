using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Debugger;
using dnSpy.Extension.MCP.Execution;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// Runs the optional review-time executable validation.  The private module is written only below
/// ArtifactRoot, executed only after the P01 VM/debug-idle gate has admitted the request, and the
/// temporary image is removed before a successful review is published.
/// </summary>
internal sealed class EditDynamicValidationService {
	readonly IEditDynamicValidationGate gate;
	readonly McpSettings settings;

	public EditDynamicValidationService(IEditDynamicValidationGate gate, McpSettings settings) {
		this.gate = gate;
		this.settings = settings;
	}

	public object Run(EditWorkspace workspace, Dictionary<string, object>? arguments) {
		if (arguments == null || !arguments.ContainsKey("dynamic_validation"))
			return NotRun("not_requested", false, null);

		var request = EditWire.Object(arguments, "dynamic_validation");
		var profile = RequiredString(request, "runtime_profile");
		var decision = gate.EvaluateEditDynamicValidation();
		if (!decision.Allowed) {
			var code = decision.State == Debugger.DebugStates.Idle
				? "EDIT_CAPABILITY_UNAVAILABLE" : "EDIT_DEBUG_NOT_IDLE";
			throw new EditReviewAttemptException(code,
				code == "EDIT_CAPABILITY_UNAVAILABLE" ? Capability("vm_execution_gate", "Virtualization execution gate rejected the request") : null,
				Blocked(profile, code,
				decision.State == Debugger.DebugStates.Idle
					? $"VM execution gate rejected {decision.Environment.Classification}"
					: $"debugger state is {decision.State}"));
		}

		if (workspace.PrivateModule.Kind == ModuleKind.Dll || workspace.PrivateModule.EntryPoint == null)
			return NotRun("not_applicable", true, profile);

		var root = settings.CurrentSnapshot?.ArtifactRoot;
		if (string.IsNullOrWhiteSpace(root))
			throw new EditReviewAttemptException("EDIT_CAPABILITY_UNAVAILABLE",
				Capability("artifact_root", "ArtifactRoot is not configured"),
				Blocked(profile, "artifact_root", "ArtifactRoot is not configured"));

		var directory = Path.Combine(root!, "edit-validation");
		var pathKey = EditWire.Sha256(System.Text.Encoding.UTF8.GetBytes(workspace.PrivateFingerprint() + "\n" + profile));
		var path = Path.Combine(directory, "dnspy-edit-validation-" + pathKey.Substring(0, 24) + ".exe");
		var events = new List<object>();
		byte[] bytes;
		try {
			Directory.CreateDirectory(directory);
			bytes = workspace.ValidateRoundtrip();
			File.WriteAllBytes(path, bytes);
			events.Add(Event(0, "artifact_written", "private executable written for validation"));
		}
		catch (Exception ex) {
			throw new EditReviewAttemptException("EDIT_VALIDATION_FAILED",
				EditWorkspace.ValidationDetails("dynamic_write", "dynamic_validation", ex.Message),
				FailedBeforeArtifact(profile, "write", ex.Message));
		}

		var sha256 = Hash(bytes);
		var debugger = gate as DebugSessionService;
		string? debugSessionId = null;
		long debugGeneration = 0;
		bool terminateAttempted = false;
		bool deleteAttempted = false;
		bool deleted = false;
		string? residual = null;
		try {
			if (TestMode && profile == "test-fail-launch")
				throw new DynamicPhaseException("launch", "test launch failure");

			if (debugger == null)
				throw new DynamicPhaseException("launch", "dnSpy debugger service is unavailable");
			var architecture = IntPtr.Size == 8 ? "x64" : "x86";
			var launch = InvokeDebug(debugger, "debug_launch", new Dictionary<string, object> {
				["request_id"] = EditWire.NewId("edit-dynamic-launch"), ["target_path"] = path,
				["expected_sha256"] = sha256, ["launch_mode"] = "net48-exe",
				["architecture"] = architecture, ["break_kind"] = "entry",
				["target_argv"] = StringArray(request, "args").ToArray(),
				["working_directory"] = OptionalString(request, "working_directory") ?? directory,
			});
			if (!IsOk(launch)) throw new DynamicPhaseException("launch", DebugMessage(launch));
			debugSessionId = launch.GetProperty("result").GetProperty("session_id").GetString();
			debugGeneration = launch.GetProperty("result").GetProperty("generation").GetInt64();
			events.Add(Event(1, "debug_started", "dnSpy debugger accepted validation target"));
			var timeout = OptionalInt(request, "timeout_ms", 30000);
			var pauseWait = Stopwatch.StartNew();
			JsonElement status = default;
			do {
				status = InvokeDebug(debugger, "debug_status", new Dictionary<string, object> { ["session_id"] = debugSessionId! });
				if (IsOk(status) && status.GetProperty("result").GetProperty("state").GetString() == "paused") break;
				System.Threading.Thread.Sleep(20);
			} while (pauseWait.ElapsedMilliseconds < timeout);
			if (!IsOk(status) || status.GetProperty("result").GetProperty("state").GetString() != "paused")
				throw new DynamicPhaseException("run", "dnSpy debugger did not reach the entry pause before timeout");
			events.Add(Event(2, "entry_paused", "validation target paused at managed entry"));
			if (TestMode && profile == "test-fail-terminate") {
				terminateAttempted = true;
				throw new DynamicPhaseException("terminate", "test terminate failure");
			}
			terminateAttempted = true;
			var terminated = InvokeDebug(debugger, "debug_terminate", new Dictionary<string, object> {
				["session_id"] = debugSessionId!, ["generation"] = debugGeneration,
				["request_id"] = EditWire.NewId("edit-dynamic-terminate"),
			});
			if (!IsOk(terminated)) throw new DynamicPhaseException("terminate", DebugMessage(terminated));
			events.Add(Event(3, "debug_terminated", "validation debug session terminated"));
			debugSessionId = null;
		}
		catch (Exception ex) {
			var phase = ex is DynamicPhaseException dynamic ? dynamic.Phase : "launch";
			TryTerminate(debugger, debugSessionId, debugGeneration, ref terminateAttempted);
			TryDelete(path, profile == "test-fail-delete", ref deleteAttempted, ref deleted, ref residual);
			var attempt = FailedAfterArtifact(profile, path, sha256, events, phase, ex.Message,
				terminateAttempted, deleteAttempted, deleted, residual);
			throw new EditReviewAttemptException("EDIT_VALIDATION_FAILED",
				EditWorkspace.ValidationDetails("dynamic_" + phase, "dynamic_validation", ex.Message), attempt);
		}
		if (TestMode && profile == "test-fail-delete") {
			deleteAttempted = true;
			residual = path;
			throw new EditReviewAttemptException("EDIT_VALIDATION_FAILED",
				EditWorkspace.ValidationDetails("dynamic_delete", "dynamic_validation", "test delete failure"),
				FailedAfterArtifact(profile, path, sha256, events, "delete", "test delete failure",
					terminateAttempted, deleteAttempted, false, residual));
		}
		TryDelete(path, false, ref deleteAttempted, ref deleted, ref residual);
		if (!deleted) {
			throw new EditReviewAttemptException("EDIT_VALIDATION_FAILED",
				EditWorkspace.ValidationDetails("dynamic_delete", "dynamic_validation", "temporary validation image could not be deleted"),
				FailedAfterArtifact(profile, path, sha256, events, "delete", "temporary validation image could not be deleted",
					terminateAttempted, deleteAttempted, false, residual ?? path));
		}
		terminateAttempted = true;
		return new Dictionary<string, object?> {
			["state"] = "passed", ["requested"] = true, ["runtime_profile"] = profile,
			["artifact"] = Artifact(true, path, sha256), ["events"] = events.ToArray(),
			["cleanup"] = Cleanup(terminateAttempted, true, true, null), ["failure"] = null,
		};
	}

	static object NotRun(string state, bool requested, string? profile) => new Dictionary<string, object?> {
		["state"] = state, ["requested"] = requested, ["runtime_profile"] = profile,
		["artifact"] = Artifact(false, null, null), ["events"] = Array.Empty<object>(),
		["cleanup"] = Cleanup(false, false, true, null), ["failure"] = null,
	};

	static object Blocked(string profile, string code, string message) => new Dictionary<string, object?> {
		["state"] = "blocked", ["requested"] = true, ["runtime_profile"] = profile,
		["artifact"] = Artifact(false, null, null), ["events"] = Array.Empty<object>(),
		["cleanup"] = Cleanup(false, false, true, null),
		["failure"] = Failure(code, message, "precheck"),
	};

	static object FailedBeforeArtifact(string profile, string phase, string message) => new Dictionary<string, object?> {
		["state"] = "failed", ["requested"] = true, ["runtime_profile"] = profile,
		["artifact"] = Artifact(false, null, null), ["events"] = Array.Empty<object>(),
		["cleanup"] = Cleanup(false, false, true, null),
		["failure"] = Failure("EDIT_DYNAMIC_VALIDATION_FAILED", Truncate(message, 512), phase),
	};

	static object FailedAfterArtifact(string profile, string path, string sha256, IReadOnlyCollection<object> events,
		string phase, string message, bool terminateAttempted, bool deleteAttempted, bool deleted, string? residual) =>
		new Dictionary<string, object?> {
			["state"] = "failed", ["requested"] = true, ["runtime_profile"] = profile,
			["artifact"] = Artifact(true, path, sha256), ["events"] = events.ToArray(),
			["cleanup"] = Cleanup(terminateAttempted, deleteAttempted, deleted, residual),
			["failure"] = Failure("EDIT_DYNAMIC_VALIDATION_FAILED", Truncate(message, 512), phase),
		};

	static object Artifact(bool created, string? path, string? sha256) => new Dictionary<string, object?> {
		["created"] = created, ["path"] = path, ["sha256"] = sha256,
	};
	static object Cleanup(bool terminate, bool deleteAttempted, bool deleted, string? residual) => new Dictionary<string, object?> {
		["terminate_attempted"] = terminate, ["debug_idle"] = true,
		["temp_delete_attempted"] = deleteAttempted, ["temp_deleted"] = deleted, ["residual_path"] = residual,
	};
	static object Failure(string code, string message, string phase) => new Dictionary<string, object?> {
		["code"] = code, ["message"] = message, ["phase"] = phase,
	};
	static object Capability(string capability, string reason) => new Dictionary<string, object?> {
		["kind"] = "capability", ["capability"] = capability, ["reason"] = reason,
	};
	static object Event(uint sequence, string kind, string summary) => new Dictionary<string, object?> {
		["sequence"] = sequence, ["kind"] = kind, ["summary"] = summary,
	};

	static string RequiredString(Dictionary<string, object?> value, string name) {
		if (!value.TryGetValue(name, out var raw)) throw new ArgumentException(name + " is required", name);
		if (raw is string s && s.Length != 0) return s;
		if (raw is JsonElement e && e.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(e.GetString())) return e.GetString()!;
		throw new ArgumentException(name + " is required", name);
	}
	static string? OptionalString(Dictionary<string, object?> value, string name) {
		if (!value.TryGetValue(name, out var raw)) return null;
		return raw is JsonElement e ? e.GetString() : raw?.ToString();
	}
	static int OptionalInt(Dictionary<string, object?> value, string name, int fallback) {
		if (!value.TryGetValue(name, out var raw)) return fallback;
		if (EditWire.TryInt64(raw, out var parsed) && parsed >= 1000 && parsed <= 120000) return (int)parsed;
		throw new ArgumentException(name + " must be within 1000..120000", name);
	}
	static IEnumerable<string> StringArray(Dictionary<string, object?> value, string name) {
		if (!value.TryGetValue(name, out var raw)) yield break;
		if (raw is not JsonElement e || e.ValueKind != JsonValueKind.Array) throw new ArgumentException(name + " must be an array", name);
		foreach (var item in e.EnumerateArray()) yield return item.GetString() ?? string.Empty;
	}
	static string Truncate(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
	static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2"))); }
	static JsonElement InvokeDebug(DebugSessionService service, string tool, Dictionary<string, object>? arguments) {
		// DebugSessionService normally receives values produced by the MCP JSON parser.  Preserve
		// exactly that boundary when the edit subsystem invokes it in-process; its strict argument
		// readers intentionally consume JsonElement values rather than arbitrary CLR objects.
		var normalized = arguments == null ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(
			JsonSerializer.Serialize(arguments));
		if (tool == "debug_launch") {
			using var launchDocument = JsonDocument.Parse(service.LaunchEditValidation(normalized));
			return launchDocument.RootElement.Clone();
		}
		var toolResult = service.Execute(tool, normalized);
		if (toolResult.Content.Count == 0) throw new InvalidOperationException("dnSpy debugger returned no content");
		using var document = JsonDocument.Parse(toolResult.Content[0].Text);
		return document.RootElement.Clone();
	}
	static bool IsOk(JsonElement envelope) => envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("ok", out var ok) && ok.GetBoolean();
	static string DebugMessage(JsonElement envelope) => envelope.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
		? message.GetString() ?? "dnSpy debugger rejected validation" : "dnSpy debugger rejected validation";
	static void TryTerminate(DebugSessionService? debugger, string? sessionId, long generation, ref bool attempted) {
		if (debugger == null || sessionId == null) return;
		attempted = true;
		try { InvokeDebug(debugger, "debug_terminate", new Dictionary<string, object> {
			["session_id"] = sessionId, ["generation"] = generation,
			["request_id"] = EditWire.NewId("edit-dynamic-cleanup"),
		}); } catch { }
	}
	static void TryDelete(string path, bool forceFailure, ref bool attempted, ref bool deleted, ref string? residual) {
		attempted = true;
		if (!forceFailure) { try { File.Delete(path); } catch { } }
		deleted = !File.Exists(path);
		residual = deleted ? null : path;
	}
	static bool TestMode => string.Equals(Environment.GetEnvironmentVariable("DNMCP_TEST"), "1", StringComparison.Ordinal);

	sealed class DynamicPhaseException : Exception {
		public string Phase { get; }
		public DynamicPhaseException(string phase, string message) : base(message) => Phase = phase;
	}
}

internal sealed class EditReviewAttemptException : Exception {
	public string Code { get; }
	public object? Details { get; }
	public object Attempt { get; }
	public EditReviewAttemptException(string code, object? details, object attempt) : base(EditWire.Message(code)) {
		Code = code;
		Details = details;
		Attempt = attempt;
	}
}
