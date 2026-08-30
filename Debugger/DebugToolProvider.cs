using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnSpy.Extension.MCP.Tools;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Debug tool advertisement and dispatch (CON-DYN-014 / §3.3). tools/list always gains exactly
/// <c>debug_capabilities</c>; the remaining 21 tools are advertised only while the frozen gate
/// is active. The gate is frozen once per process from the authoritative settings snapshot; the
/// StartupDbgWasIdle sampler is wired with the debugger contracts in IMP-005 — until then the
/// gate stays false and an unsampleable gate must never enable debug tools.
/// </summary>
[Export(typeof(IMcpToolProvider))]
public sealed class DebugToolProvider : IMcpToolProvider {
	readonly McpSettings settings;
	readonly DebugGateService gateService;
	readonly DebugSessionService sessionService;
	readonly object schemaLock = new object();
	JsonDocument? schemaDoc;

	[ImportingConstructor]
	public DebugToolProvider(McpSettings settings, DebugGateService gateService, DebugSessionService sessionService) {
		this.settings = settings;
		this.gateService = gateService;
		this.sessionService = sessionService;
	}

	public string Name => "debug";

	/// <summary>
	/// Tools answered without being advertised: the fixed-disabled APIs (API-DYN-004/005/010,
	/// domain CAPABILITY_UNAVAILABLE) and, while the frozen gate is off, every session tool —
	/// a schema-valid direct call must get the domain DEBUG_DISABLED envelope (ACC-002), not
	/// an unknown-tool text.
	/// </summary>
	public IReadOnlyCollection<string> UnadvertisedTools {
		get {
			var names = new List<string>(DisabledApiNames);
			// Deterministic product-seam probes are callable only by the VM acceptance driver;
			// they never change the advertised tool snapshot (including under DNMCP_TEST).
			names.AddRange(new[] { "debug_test_settings", "debug_test_artifact", "debug_test_transport" });
			if (!Gate.EffectiveDebugLaunch)
				names.AddRange(AdvertisedSessionTools);
			if (!DebugSessionService.TestModeEnabled)
				names.AddRange(new[] { "debug_test_spy", "debug_test_clock", "debug_test_adapter", "debug_test_flood", "debug_test_start", "debug_test_dump" });
			return names;
		}
	}

	ToolInfo TestSpyTool() => new ToolInfo {
		Name = "debug_test_spy",
		Description = "DNMCP_TEST-only: snapshot (or reset) the in-process spy counters the ACC fixtures assert deltas on (read_memory_executions, dbg_start_calls, break/terminate posts, dispatcher post counts, thread-domain classifications).",
		InputSchema = new Dictionary<string, object> {
			["type"] = "object",
			["properties"] = new Dictionary<string, object> {
				["reset"] = new Dictionary<string, object> { ["type"] = "boolean" },
			},
			["additionalProperties"] = false,
		},
	};

	/// <summary>The per-process frozen gate owned by DebugGateService (CON-DYN-014): the
	/// dispatcher-sampled value once it lands, otherwise the unsampleable gate (always false).</summary>
	public DebugFeatureGate.FrozenGate Gate => gateService.Current;

	static string HostArchitecture => IntPtr.Size == 8 ? Architectures.X64 : Architectures.X86;

	static string ExtensionVersion {
		get {
			var v = typeof(DebugToolProvider).Assembly.GetName().Version;
			return v is null ? "0.0.0" : v.ToString(3);
		}
	}

	public IReadOnlyList<ToolInfo> GetTools() {
		var tools = new List<ToolInfo> { CapabilitiesTool() };
		// The in-proc injection surface (DNMCP_TEST=1): diagnostic tools are advertised only in
		// test mode; they always answer (CAPABILITY_UNAVAILABLE outside it) via UnadvertisedTools.
		if (DebugSessionService.TestModeEnabled) {
			tools.Add(TestSpyTool());
			tools.Add(new ToolInfo {
				Name = "debug_test_flood",
				Description = "DNMCP_TEST-only: append N synthetic events to the active session buffer (eviction/payload_omitted observability).",
				InputSchema = new Dictionary<string, object> {
					["type"] = "object",
					["properties"] = new Dictionary<string, object> {
						["count"] = new Dictionary<string, object> { ["type"] = "integer" },
						["bytes_per_event"] = new Dictionary<string, object> { ["type"] = "integer" },
					},
					["additionalProperties"] = false,
				},
			});
			tools.Add(new ToolInfo {
				Name = "debug_test_start",
				Description = "DNMCP_TEST-only: arm the NEXT launch with fail_start (synchronous Start error), exit_before_claim, ui_debugging/ui_debugging_off (simulated human UI debug session), foreign_process (unregistered process observation -> ownership lost) or manager_idle (recovery).",
				InputSchema = new Dictionary<string, object> {
					["type"] = "object",
					["properties"] = new Dictionary<string, object> {
						["mode"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<string> { "fail_start", "exit_before_claim", "ui_debugging", "ui_debugging_off", "foreign_process", "manager_idle" } },
					},
					["additionalProperties"] = false,
				},
			});
			tools.Add(new ToolInfo {
				Name = "debug_test_dump",
				Description = "DNMCP_TEST-only: fix the NEXT debug_dump_module raw-bytes branch over the IRawModuleBytesSource seam: raw (production), force_memory (raw unavailable + ForceMemory reconstruction) or both_unavailable.",
				InputSchema = new Dictionary<string, object> {
					["type"] = "object",
					["properties"] = new Dictionary<string, object> {
						["mode"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<string> { "raw", "force_memory", "both_unavailable" } },
					},
					["additionalProperties"] = false,
				},
			});
			tools.Add(new ToolInfo {
				Name = "debug_test_clock",
				Description = "DNMCP_TEST-only: advance the virtual clock that control-operation deadlines run on (advance_ms), or read the virtual elapsed/offset.",
				InputSchema = new Dictionary<string, object> {
					["type"] = "object",
					["properties"] = new Dictionary<string, object> {
						["advance_ms"] = new Dictionary<string, object> { ["type"] = "integer" },
						["reset"] = new Dictionary<string, object> { ["type"] = "boolean" },
					},
					["additionalProperties"] = false,
				},
			});
			tools.Add(new ToolInfo {
				Name = "debug_test_adapter",
				Description = "DNMCP_TEST-only: install/uninstall the scriptable fake control adapter, arm fail_next=explicit_failure, or emit synthetic paused/removed observations with classified BreakInfos through the production observation path.",
				InputSchema = new Dictionary<string, object> {
					["type"] = "object",
					["properties"] = new Dictionary<string, object> {
						["install"] = new Dictionary<string, object> { ["type"] = "boolean" },
						["fail_next"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<string> { "explicit_failure" } },
						["emit"] = new Dictionary<string, object> {
							["type"] = "object",
							["properties"] = new Dictionary<string, object> {
								["kind"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new List<string> { "paused", "removed" } },
								["pid"] = new Dictionary<string, object> { ["type"] = "integer" },
								["exit_code"] = new Dictionary<string, object> { ["type"] = "integer" },
								["break_infos"] = new Dictionary<string, object> { ["type"] = "array" },
								["no_pause"] = new Dictionary<string, object> { ["type"] = "boolean" },
								["first_chance"] = new Dictionary<string, object> { ["type"] = "boolean" },
								["unhandled"] = new Dictionary<string, object> { ["type"] = "boolean" },
								["exception_type"] = new Dictionary<string, object> { ["type"] = "string" },
							},
						},
					},
					["additionalProperties"] = false,
				},
			});
		}
		// The 21 session-scoped tools are advertised only when the frozen gate is active AND
		// their handlers exist (staged with IMP-004..009); never advertise what cannot answer.
		if (Gate.EffectiveDebugLaunch) {
			// Never advertise what cannot answer: only session tools with a landed handler
			// (IMP-004..009 wiring) are listed while the rest stay hidden until implemented.
			foreach (var name in AdvertisedSessionTools)
				if (sessionService.Handles(name))
					tools.Add(SessionTool(name));
		}
		return tools;
	}

	public static readonly IReadOnlyList<string> DisabledApiNames = new[] {
		"debug_list_attachable_processes", "debug_attach", "debug_detach",
	};

	public CallToolResult? ExecuteTool(string toolName, Dictionary<string, object>? arguments) {
		if (toolName != "debug_capabilities") {
			// The three reserved disabled APIs are never advertised, but a schema-valid direct
			// call gets the fixed zero-side-effect CAPABILITY_UNAVAILABLE envelope (§3.3) —
			// never an "unknown tool" error and never a details object.
			if (System.Linq.Enumerable.Contains(DisabledApiNames, toolName))
				return FixedDisabledResult();
			if (toolName == "debug_test_spy" || toolName == "debug_test_clock" || toolName == "debug_test_adapter" || toolName == "debug_test_flood" || toolName == "debug_test_start" || toolName == "debug_test_dump" || toolName == "debug_test_settings" || toolName == "debug_test_artifact" || toolName == "debug_test_transport")
				return sessionService.Execute(toolName, arguments);
			if (sessionService.Handles(toolName))
				return sessionService.Execute(toolName, arguments);
			return null; // session tools dispatch here as their handlers land (IMP-004..009)
		}
		var gate = Gate;
		var snapshot = settings.CurrentSnapshot;
		bool remote = snapshot?.IsRemote == true;
		var cap = new DebugCapabilitiesResultDto {
			DebugEnabled = gate.EffectiveDebugLaunch,
			ExtensionVersion = ExtensionVersion,
			HostArchitecture = HostArchitecture,
			DedicatedInstanceAcknowledged = gate.DedicatedInstanceAcknowledged,
			Tools = DebugCapabilitiesResultDto.ToolsFor(gate.EffectiveDebugLaunch),
			RuntimeMatrix = DebugCapabilitiesResultDto.MatrixFor(HostArchitecture),
			// Security posture reflects the ACTIVE snapshot: loopback vs remote_host_only with
			// its token/CIDR requirements (the DTO defaults describe loopback only).
			Security = new DebugCapabilitiesResultDto.SecurityDto {
				BindMode = remote ? "remote_host_only" : "loopback",
				AuthRequired = snapshot?.RequiresRemoteToken == true,
				CidrRequired = remote,
			},
		};
		var envelope = new DebugSuccessEnvelope {
			DebugContext = new DebugContextDto { State = DebugStates.Idle },
			Result = cap,
		};
		var json = JsonSerializer.Serialize(envelope, CanonicalOptions);
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = json } },
		};
	}

	static readonly JsonSerializerOptions CanonicalOptions = new JsonSerializerOptions {
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Fixed failure envelope for the three v1-disabled attach APIs: loopback-style no-session
	/// context (idle, zero counters), CAPABILITY_UNAVAILABLE with the §3.4 message/recovery and
	/// no details; identical in every state and protocol version, zero side effects.
	/// </summary>
	static CallToolResult FixedDisabledResult() {
		var envelope = new DebugFailureEnvelope {
			DebugContext = new DebugContextDto { State = DebugStates.Idle },
			Error = DomainErrorDto.Create(DomainErrorCodes.CapabilityUnavailable, DebugStates.Idle),
		};
		return new CallToolResult {
			Content = new List<ToolContent> { new ToolContent { Text = JsonSerializer.Serialize(envelope, CanonicalOptions) } },
			IsError = true,
		};
	}

	ToolInfo CapabilitiesTool() => new ToolInfo {
		Name = "debug_capabilities",
		OutputSchema = ResultSchema("debug_capabilities"),
		Description = "Report the dynamic-debugging capability set of this dnSpy MCP instance: frozen enablement gate, host architecture, the fixed runtime/launch matrix, security posture, artifact policy and every fixed transport/resource limit.",
		InputSchema = new Dictionary<string, object> {
			["type"] = "object",
			["properties"] = new Dictionary<string, object>(),
			["additionalProperties"] = false,
		},
	};

	/// <summary>The §3.3 session tools whose input schemas come from the frozen contract.</summary>
	static readonly string[] AdvertisedSessionTools = {
		"debug_status", "debug_launch", "debug_pause", "debug_continue", "debug_restart",
		"debug_terminate", "debug_read_events", "debug_wait_event", "debug_set_breakpoint",
		"debug_list_breakpoints", "debug_set_breakpoint_enabled", "debug_remove_breakpoint",
		"debug_list_threads", "debug_get_stack", "debug_step", "debug_get_locals",
		"debug_expand_value", "debug_list_modules", "debug_read_memory", "debug_dump_module",
		"debug_set_exception_policy",
	};

	ToolInfo SessionTool(string name) => new ToolInfo {
		Name = name,
		Description = SessionToolDescriptions.TryGetValue(name, out var d) ? d : name,
		InputSchema = ArgsSchema(name),
		OutputSchema = ResultSchema(name),
	};

	/// <summary>
	/// Loads the frozen structural contract (the same dnspy.debug.v1.schema.json frozen by
	/// IMP-002) as an assembly resource and derives the advertised, self-contained tool schema
	/// from that definition, so its fields cannot drift from the contract fixtures.
	/// </summary>
	Dictionary<string, object> ArgsSchema(string toolName) {
		lock (schemaLock) {
			schemaDoc ??= LoadEmbeddedSchema();
			var defs = schemaDoc.RootElement.GetProperty("$defs");
			if (defs.TryGetProperty(toolName + "_args", out var args))
				return ExpandToolSchema(args, defs);
		}
		return new Dictionary<string, object> { ["type"] = "object" };
	}

	/// <summary>The tool's result definition from the frozen contract (outputSchema).</summary>
	Dictionary<string, object>? ResultSchema(string toolName) {
		lock (schemaLock) {
			schemaDoc ??= LoadEmbeddedSchema();
			var defs = schemaDoc.RootElement.GetProperty("$defs");
			if (defs.TryGetProperty(toolName + "_result", out var result))
				return BuildEnvelopeOutputSchema(result, defs);
		}
		return null;
	}

	/// <summary>
	/// Produces the compatibility schema advertised to MCP clients. The frozen aggregate schema
	/// remains the validation source of truth, but many tool registries do not dereference local
	/// <c>$defs</c> or conditional <c>allOf</c> nodes. Inline local references and omit conditional
	/// validation-only branches so every top-level field has a directly consumable type. Runtime
	/// argument validation continues to enforce the complete frozen contract.
	/// </summary>
	static Dictionary<string, object> ExpandToolSchema(JsonElement definition, JsonElement allDefinitions) {
		return ExpandSchemaNode(definition, allDefinitions, new HashSet<string>(StringComparer.Ordinal))
			as Dictionary<string, object> ?? new Dictionary<string, object> { ["type"] = "object" };
	}

	/// <summary>
	/// tools/call returns the complete debug envelope, not the inner result DTO. Advertise that
	/// actual wire object and specialize its optional result property for each tool. The common
	/// required fields apply to both success and failure; the frozen runtime contract enforces
	/// the exact success/result versus failure/error conditional.
	/// </summary>
	static Dictionary<string, object> BuildEnvelopeOutputSchema(JsonElement resultDefinition, JsonElement allDefinitions) {
		object Expanded(string name) => allDefinitions.TryGetProperty(name, out var value)
			? ExpandSchemaNode(value, allDefinitions, new HashSet<string>(StringComparer.Ordinal))
			: new Dictionary<string, object> { ["type"] = "object" };

		return new Dictionary<string, object> {
			["type"] = "object",
			["description"] = "dnspy.debug.v1 success/failure envelope; success carries result, failure carries error.",
			["properties"] = new Dictionary<string, object> {
				["schema_version"] = new Dictionary<string, object> { ["const"] = DebugWire.SchemaVersion },
				["ok"] = new Dictionary<string, object> { ["type"] = "boolean" },
				["debug_context"] = Expanded("debug_context"),
				["result"] = ExpandToolSchema(resultDefinition, allDefinitions),
				["error"] = Expanded("domain_error"),
				["warnings"] = new Dictionary<string, object> {
					["type"] = "array", ["maxItems"] = 32, ["items"] = Expanded("warning"),
				},
				["untrusted_sample_data"] = new Dictionary<string, object> { ["type"] = "boolean" },
			},
			["required"] = new List<string> { "schema_version", "ok", "debug_context", "warnings", "untrusted_sample_data" },
			["additionalProperties"] = false,
		};
	}

	static object ExpandSchemaNode(JsonElement element, JsonElement allDefinitions, HashSet<string> expansionStack) {
		switch (element.ValueKind) {
		case JsonValueKind.Object:
			var expanded = new Dictionary<string, object>(StringComparer.Ordinal);
			if (element.TryGetProperty("$ref", out var refElement)
				&& refElement.ValueKind == JsonValueKind.String
				&& TryGetLocalDefinitionName(refElement.GetString(), out var definitionName)
				&& allDefinitions.TryGetProperty(definitionName, out var referenced)) {
				if (!expansionStack.Add(definitionName)) {
					return new Dictionary<string, object> {
						["type"] = "object",
						["description"] = "Recursive child object; bounded runtime representation.",
						["additionalProperties"] = true,
					};
				}
				if (ExpandSchemaNode(referenced, allDefinitions, expansionStack) is Dictionary<string, object> referencedObject)
					foreach (var pair in referencedObject)
						expanded[pair.Key] = pair.Value;
				expansionStack.Remove(definitionName);
			}

			foreach (var property in element.EnumerateObject()) {
				// Conditional branches constrain combinations but obscure the callable object shape
				// in strict/LLM tool registries. The server still validates them at dispatch.
				if (property.NameEquals("$ref") || property.NameEquals("$defs")
					|| property.NameEquals("allOf") || property.NameEquals("if")
					|| property.NameEquals("then") || property.NameEquals("else")
					|| property.NameEquals("not"))
					continue;
				expanded[property.Name] = ExpandSchemaNode(property.Value, allDefinitions, expansionStack);
			}
			return expanded;

		case JsonValueKind.Array:
			var list = new List<object>();
			foreach (var item in element.EnumerateArray())
				list.Add(ExpandSchemaNode(item, allDefinitions, expansionStack));
			return list;
		case JsonValueKind.String:
			return element.GetString() ?? string.Empty;
		case JsonValueKind.Number:
			if (element.TryGetInt32(out var intValue)) return intValue;
			if (element.TryGetInt64(out var longValue)) return longValue;
			return element.GetDouble();
		case JsonValueKind.True:
			return true;
		case JsonValueKind.False:
			return false;
		case JsonValueKind.Null:
		case JsonValueKind.Undefined:
		default:
			return null!;
		}
	}

	static bool TryGetLocalDefinitionName(string? reference, out string name) {
		name = string.Empty;
		const string prefix = "#/$defs/";
		if (reference == null || !reference.StartsWith(prefix, StringComparison.Ordinal))
			return false;
		var tail = reference.Substring(prefix.Length);
		var slash = tail.IndexOf('/');
		name = (slash < 0 ? tail : tail.Substring(0, slash)).Replace("~1", "/").Replace("~0", "~");
		return name.Length != 0;
	}

	static JsonDocument LoadEmbeddedSchema() {
		var assembly = typeof(DebugToolProvider).Assembly;
		foreach (var name in assembly.GetManifestResourceNames()) {
			if (name.EndsWith("dnspy.debug.v1.schema.json", StringComparison.OrdinalIgnoreCase)) {
				using var stream = assembly.GetManifestResourceStream(name);
				return JsonDocument.Parse(stream ?? throw new InvalidOperationException("schema resource empty"),
					new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
			}
		}
		throw new InvalidOperationException("dnspy.debug.v1.schema.json embedded resource is missing");
	}

	static readonly Dictionary<string, string> SessionToolDescriptions = new() {
		["debug_status"] = "Query the coordinator state, active/last session, owned process and observed process state.",
		["debug_launch"] = "Launch a target under the debugger (launch-only v1; five explicit modes; four break kinds).",
		["debug_pause"] = "Request a pause of the owned process; settles on authoritative state observation.",
		["debug_continue"] = "Resume the paused owned process.",
		["debug_restart"] = "Terminate and relaunch the owned target with revalidated launch parameters.",
		["debug_terminate"] = "Terminate the owned process and end the session.",
		["debug_read_events"] = "Read debug events from a session cursor (bounded, monotonic).",
		["debug_wait_event"] = "Wait up to 30s for new debug events.",
		["debug_set_breakpoint"] = "Create a strong-identity managed breakpoint (module/MVID/token/IL offset).",
		["debug_list_breakpoints"] = "Page through owned breakpoints.",
		["debug_set_breakpoint_enabled"] = "Enable or disable an owned breakpoint.",
		["debug_remove_breakpoint"] = "Remove an owned breakpoint.",
		["debug_list_threads"] = "Page through managed threads of the paused process.",
		["debug_get_stack"] = "Page through the call stack of a thread.",
		["debug_step"] = "Step into/over/out on a thread.",
		["debug_get_locals"] = "Read locals/args/this of a frame (no function evaluation, raw views only).",
		["debug_expand_value"] = "Expand a value handle's children (raw field/array access only).",
		["debug_list_modules"] = "Page through loaded modules with identity metadata.",
		["debug_read_memory"] = "Read at most 64 KiB of target memory (zero-fill semantics reported).",
		["debug_dump_module"] = "Export a module's raw/reconstructed bytes into the session artifact store.",
		["debug_set_exception_policy"] = "Set this session's exception break policy.",
	};
}
