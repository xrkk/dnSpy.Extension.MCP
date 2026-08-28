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
			if (!Gate.EffectiveDebugLaunch)
				names.AddRange(AdvertisedSessionTools);
			return names;
		}
	}

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
			if (sessionService.Handles(toolName))
				return sessionService.Execute(toolName, arguments);
			return null; // session tools dispatch here as their handlers land (IMP-004..009)
		}
		var gate = Gate;
		var cap = new DebugCapabilitiesResultDto {
			DebugEnabled = gate.EffectiveDebugLaunch,
			ExtensionVersion = ExtensionVersion,
			HostArchitecture = HostArchitecture,
			DedicatedInstanceAcknowledged = gate.DedicatedInstanceAcknowledged,
			Tools = DebugCapabilitiesResultDto.ToolsFor(gate.EffectiveDebugLaunch),
			RuntimeMatrix = DebugCapabilitiesResultDto.MatrixFor(HostArchitecture),
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
	};

	/// <summary>
	/// Loads the frozen structural contract (the same dnspy.debug.v1.schema.json frozen by
	/// IMP-002) as an assembly resource and returns the tool's args definition verbatim, so the
	/// advertised inputSchema can never drift from the contract fixtures.
	/// </summary>
	Dictionary<string, object> ArgsSchema(string toolName) {
		lock (schemaLock) {
			schemaDoc ??= LoadEmbeddedSchema();
			var defs = schemaDoc.RootElement.GetProperty("$defs");
			if (defs.TryGetProperty(toolName + "_args", out var args)) {
				return JsonSerializer.Deserialize<Dictionary<string, object>>(args.GetRawText()) ?? new Dictionary<string, object>();
			}
		}
		return new Dictionary<string, object> { ["type"] = "object" };
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
