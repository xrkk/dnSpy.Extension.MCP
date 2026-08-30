using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Fixed wire vocabulary of the dnspy.debug.v1 contract (plan §3.4—3.5). Wire names are
/// const strings, not enum members, because several canonical values ("net48-exe",
/// "module_cctor_or_entry") are not derivable from any .NET enum naming policy.
/// </summary>
static class DebugWire
{
    public const string SchemaVersion = "dnspy.debug.v1";
    public const string RequestEffectStateSatisfied = "state_satisfied";
}

static class DebugStates
{
    public const string Idle = "idle";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Restarting = "restarting";
    public const string Stopping = "stopping";
    public const string Faulted = "faulted";

    /// <summary>Fixed output order of DomainError.required_states (§3.5 TYPE-DYN-011).</summary>
    public static readonly string[] Order = { Idle, Starting, Running, Paused, Restarting, Stopping, Faulted };
}

static class LaunchModes
{
    public const string Auto = "auto";
    public const string Net48Exe = "net48-exe";
    public const string CoreClrAppHost = "coreclr-apphost";
    public const string CoreClrDotnet = "coreclr-dotnet";
    public const string Harness = "harness";
}

static class BreakKinds
{
    public const string None = "none";
    public const string Process = "process";
    public const string ModuleCctorOrEntryPoint = "module_cctor_or_entry";
    public const string EntryPoint = "entry";
}

static class RuntimeFamilies
{
    public const string Net48 = "net48";
    public const string CoreClr = "coreclr";
}

static class Architectures
{
    public const string X86 = "x86";
    public const string X64 = "x64";
}

static class EventKinds
{
    public const string SessionStart = "session_start";
    public const string StartFailed = "start_failed";
    public const string ProcessCreated = "process_created";
    public const string ProcessExited = "process_exited";
    public const string RuntimeCreated = "runtime_created";
    public const string ModuleLoaded = "module_loaded";
    public const string ModuleUnloaded = "module_unloaded";
    public const string ThreadCreated = "thread_created";
    public const string ThreadExited = "thread_exited";
    public const string Paused = "paused";
    public const string Continued = "continued";
    public const string BreakpointBound = "breakpoint_bound";
    public const string BreakpointHit = "breakpoint_hit";
    public const string Exception = "exception";
    public const string StepCompleted = "step_completed";
    public const string Output = "output";
    public const string OwnershipLost = "ownership_lost";
    public const string Recovery = "recovery";
    public const string PayloadOmitted = "payload_omitted";
    public const string SessionEnd = "session_end";
    public const string ControlFailed = "control_failed";
}

/// <summary>The 12 canonical domain-error codes (§3.4). "UNAUTHORIZED" must never be produced.</summary>
static class DomainErrorCodes
{
    public const string DebugDisabled = "DEBUG_DISABLED";
    public const string CapabilityUnavailable = "CAPABILITY_UNAVAILABLE";
    public const string InvalidState = "INVALID_STATE";
    public const string StaleHandle = "STALE_HANDLE";
    public const string TargetMismatch = "TARGET_MISMATCH";
    public const string NotFound = "NOT_FOUND";
    public const string AlreadyExists = "ALREADY_EXISTS";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string Timeout = "TIMEOUT";
    public const string OwnershipLost = "OWNERSHIP_LOST";
    public const string RequestIdReuse = "REQUEST_ID_REUSE";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed class DomainErrorDto
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("recovery")] public string Recovery { get; set; } = string.Empty;
    [JsonPropertyName("current_state")] public string CurrentState { get; set; } = string.Empty;
    [JsonPropertyName("required_states")] public List<string> RequiredStates { get; set; } = new();
    [JsonPropertyName("retry_after_ms")] public int? RetryAfterMs { get; set; }
    [JsonPropertyName("details")] public UnsupportedTargetDetailsDto? Details { get; set; }

    /// <summary>
    /// Builds an error with the fixed §3.4 code/message/recovery mapping and the TYPE-DYN-011
    /// retry rules: LIMIT_EXCEEDED→1000, TIMEOUT→0, all other codes omit the field.
    /// requiredStates must already be in DebugStates.Order sequence; only INVALID_STATE may be non-empty.
    /// </summary>
    public static DomainErrorDto Create(string code, string currentState, List<string>? requiredStates = null) {
        var (message, recovery) = Lookup(code);
        var dto = new DomainErrorDto {
            Code = code,
            Message = message,
            Recovery = recovery,
            CurrentState = currentState,
            RequiredStates = requiredStates ?? new List<string>(),
        };
        if (code == DomainErrorCodes.LimitExceeded)
            dto.RetryAfterMs = 1000;
        else if (code == DomainErrorCodes.Timeout)
            dto.RetryAfterMs = 0;
        return dto;
    }

    public static (string Message, string Recovery) Lookup(string code) => code switch {
        DomainErrorCodes.DebugDisabled => ("Debug tools are disabled.", "enable_debug_tools"),
        DomainErrorCodes.CapabilityUnavailable => ("The requested capability is unavailable.", "choose_supported_workflow"),
        DomainErrorCodes.InvalidState => ("The operation is invalid in the current state.", "query_status"),
        DomainErrorCodes.StaleHandle => ("The referenced handle is stale.", "reacquire_handles"),
        DomainErrorCodes.TargetMismatch => ("The target identity no longer matches.", "reacquire_target"),
        DomainErrorCodes.NotFound => ("The requested resource was not found.", "requery_resource"),
        DomainErrorCodes.AlreadyExists => ("The requested name already exists.", "choose_new_name"),
        DomainErrorCodes.LimitExceeded => ("A fixed resource limit was exceeded.", "reduce_request_or_wait"),
        DomainErrorCodes.Timeout => ("The operation timed out.", "wait_for_state"),
        DomainErrorCodes.OwnershipLost => ("Exclusive target ownership could not be established.", "manual_resolve_then_wait_idle"),
        DomainErrorCodes.RequestIdReuse => ("The request_id was reused with different arguments.", "use_new_request_id"),
        DomainErrorCodes.InternalError => ("An internal error occurred.", "inspect_server_log"),
        _ => throw new System.ArgumentException($"Unknown domain error code: {code}"),
    };
}

public sealed class UnsupportedTargetDetailsDto
{
    public sealed class EvidenceItem
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    }

    [JsonPropertyName("detected_target_kind")] public string DetectedTargetKind { get; set; } = string.Empty;
    [JsonPropertyName("evidence")] public List<EvidenceItem> Evidence { get; set; } = new();
    [JsonPropertyName("recommended_workflow")] public string RecommendedWorkflow { get; set; } = string.Empty;
}

/// <summary>TYPE-DYN-001. session_id omitted when the response has no session context.</summary>
public sealed class DebugContextDto
{
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("generation")] public int Generation { get; set; }
    [JsonPropertyName("pause_epoch")] public int PauseEpoch { get; set; }
    [JsonPropertyName("event_cursor")] public int EventCursor { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = DebugStates.Idle;
}

/// <summary>§3.4 success envelope. Property order is the canonical wire order and must not change.</summary>
public sealed class DebugSuccessEnvelope
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = DebugWire.SchemaVersion;
    [JsonPropertyName("ok")] public bool Ok { get; } = true;
    [JsonPropertyName("debug_context")] public DebugContextDto DebugContext { get; set; } = new();
    [JsonPropertyName("result")] public object Result { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("untrusted_sample_data")] public bool UntrustedSampleData { get; set; }
}

/// <summary>§3.4 failure envelope; carries error instead of result.</summary>
public sealed class DebugFailureEnvelope
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = DebugWire.SchemaVersion;
    [JsonPropertyName("ok")] public bool Ok { get; } = false;
    [JsonPropertyName("debug_context")] public DebugContextDto DebugContext { get; set; } = new();
    [JsonPropertyName("error")] public DomainErrorDto Error { get; set; } = new();
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new();
    [JsonPropertyName("untrusted_sample_data")] public bool UntrustedSampleData { get; set; }
}

/// <summary>API-DYN-006 pause result: fixed request_effect; no caused_pause under stock dnSpy v6.6.0.</summary>
public sealed class DebugPauseResultDto
{
    [JsonPropertyName("state")] public string State { get; set; } = DebugStates.Paused;
    [JsonPropertyName("pause_epoch")] public int PauseEpoch { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("request_effect")] public string RequestEffect { get; set; } = DebugWire.RequestEffectStateSatisfied;
    [JsonPropertyName("thread_handle")] public string? ThreadHandle { get; set; }
    [JsonPropertyName("location")] public object? Location { get; set; }
    [JsonPropertyName("breakpoint_id")] public string? BreakpointId { get; set; }
    [JsonPropertyName("step_id")] public string? StepId { get; set; }
}

/// <summary>TYPE-DYN-010. Fixed six-entry matrix order; host-mismatch rows carry the only allowed reason.</summary>
public sealed class RuntimeMatrixEntryDto
{
    [JsonPropertyName("launch_mode")] public string LaunchMode { get; set; } = string.Empty;
    [JsonPropertyName("runtime_family")] public string RuntimeFamily { get; set; } = string.Empty;
    [JsonPropertyName("architecture")] public string Architecture { get; set; } = string.Empty;
    [JsonPropertyName("product_supported")] public bool ProductSupported { get; } = true;
    [JsonPropertyName("launch")] public bool Launch { get; set; }
    [JsonPropertyName("attach")] public bool Attach { get; } = false;
    [JsonPropertyName("restart")] public bool Restart { get; set; }
    [JsonPropertyName("host_path_required")] public bool HostPathRequired { get; set; }
    [JsonPropertyName("unavailable_reason")] public string? UnavailableReason { get; set; }
}

/// <summary>API-DYN-001 capabilities result. All limit fields are fixed contract constants.</summary>
public sealed class DebugCapabilitiesResultDto
{
    public sealed class SecurityDto
    {
        [JsonPropertyName("bind_mode")] public string BindMode { get; set; } = "loopback";
        [JsonPropertyName("auth_required")] public bool AuthRequired { get; set; }
        [JsonPropertyName("cidr_required")] public bool CidrRequired { get; set; }
        [JsonPropertyName("sample_output_policy")] public string SampleOutputPolicy { get; } = "all_tool_output_is_untrusted_data";
    }

    public sealed class ArtifactPolicyDto
    {
        [JsonPropertyName("retention_scope")] public string RetentionScope { get; } = "current_extension_process";
        [JsonPropertyName("retained_integrity")] public string RetainedIntegrity { get; } = "process_lifetime_no_write_delete_share_handles";
        [JsonPropertyName("external_child_race")] public string ExternalChildRace { get; } = "current_admission_may_complete_next_admission_fail_closed";
        [JsonPropertyName("cancel_pending")] public string CancelPending { get; } = "control_proceeds_store_fail_closed_until_final_completion";
        [JsonPropertyName("restart_existing")] public string RestartExisting { get; } = "stale_untrusted_read_only_quota_counted";
        [JsonPropertyName("automatic_cleanup")] public bool AutomaticCleanup { get; } = false;
    }

    public sealed class LimitsDto
    {
        [JsonPropertyName("request_body_bytes")] public int RequestBodyBytes { get; } = 1048576;
        [JsonPropertyName("tool_result_bytes")] public int ToolResultBytes { get; } = 8388608;
        [JsonPropertyName("transport_sessions")] public int TransportSessions { get; } = 16;
        [JsonPropertyName("parallel_short_requests")] public int ParallelShortRequests { get; } = 16;
        [JsonPropertyName("long_connections")] public int LongConnections { get; } = 8;
        [JsonPropertyName("waits")] public int Waits { get; } = 8;
        [JsonPropertyName("transport_idle_seconds")] public int TransportIdleSeconds { get; } = 600;
        [JsonPropertyName("control_operation_seconds")] public int ControlOperationSeconds { get; } = 30;
        [JsonPropertyName("event_count")] public int EventCount { get; } = 4096;
        [JsonPropertyName("event_bytes")] public int EventBytes { get; } = 8388608;
        [JsonPropertyName("memory_read_bytes")] public int MemoryReadBytes { get; } = 65536;
        [JsonPropertyName("side_effect_cache_entries")] public int SideEffectCacheEntries { get; } = 4096;
        [JsonPropertyName("side_effect_cache_bytes")] public int SideEffectCacheBytes { get; } = 268435456;
        [JsonPropertyName("side_effect_cached_envelope_bytes")] public int SideEffectCachedEnvelopeBytes { get; } = 65536;
        [JsonPropertyName("command_queue_entries")] public int CommandQueueEntries { get; } = 64;
        [JsonPropertyName("control_queue_entries")] public int ControlQueueEntries { get; } = 8;
        [JsonPropertyName("general_queue_entries")] public int GeneralQueueEntries { get; } = 56;
        [JsonPropertyName("value_snapshots_per_pause")] public int ValueSnapshotsPerPause { get; } = 2;
        [JsonPropertyName("value_handles_per_pause")] public int ValueHandlesPerPause { get; } = 4096;
        [JsonPropertyName("artifact_operation_seconds")] public int ArtifactOperationSeconds { get; } = 30;
        [JsonPropertyName("artifact_cancel_grace_ms")] public int ArtifactCancelGraceMs { get; } = 2000;
        [JsonPropertyName("artifact_io_chunk_bytes")] public int ArtifactIoChunkBytes { get; } = 1048576;
        [JsonPropertyName("artifact_file_bytes")] public long ArtifactFileBytes { get; } = 536870912;
        [JsonPropertyName("artifact_session_bytes")] public long ArtifactSessionBytes { get; } = 1073741824;
        [JsonPropertyName("artifact_store_bytes")] public long ArtifactStoreBytes { get; } = 8589934592;
        [JsonPropertyName("artifact_sessions")] public int ArtifactSessions { get; } = 128;
        [JsonPropertyName("artifact_root_children")] public int ArtifactRootChildren { get; } = 128;
        [JsonPropertyName("artifact_session_children")] public int ArtifactSessionChildren { get; } = 4096;
        [JsonPropertyName("artifact_store_children")] public int ArtifactStoreChildren { get; } = 4096;
    }

    [JsonPropertyName("debug_enabled")] public bool DebugEnabled { get; set; }
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; } = DebugWire.SchemaVersion;
    [JsonPropertyName("extension_version")] public string ExtensionVersion { get; set; } = string.Empty;
    [JsonPropertyName("dnspy_api")] public string DnSpyApi { get; } = "v6.6.0";
    [JsonPropertyName("host_architecture")] public string HostArchitecture { get; set; } = string.Empty;
    [JsonPropertyName("ownership_model")] public string OwnershipModel { get; } = "dedicated_instance_operational_isolation";
    [JsonPropertyName("dedicated_instance_required")] public bool DedicatedInstanceRequired { get; } = true;
    [JsonPropertyName("dedicated_instance_acknowledged")] public bool DedicatedInstanceAcknowledged { get; set; }
    [JsonPropertyName("attach_supported")] public bool AttachSupported { get; } = false;
    [JsonPropertyName("tools")] public List<string> Tools { get; set; } = new();
    [JsonPropertyName("runtime_matrix")] public List<RuntimeMatrixEntryDto> RuntimeMatrix { get; set; } = new();
    [JsonPropertyName("security")] public SecurityDto Security { get; set; } = new();
    [JsonPropertyName("artifact_policy")] public ArtifactPolicyDto ArtifactPolicy { get; } = new();
    [JsonPropertyName("limits")] public LimitsDto Limits { get; } = new();
    [JsonPropertyName("unsupported")] public List<string> Unsupported { get; } = new() {
        "debug_list_attachable_processes", "debug_attach", "debug_detach",
    };

    /// <summary>The 22 advertised tools in §3.3 order (gate=true), or debug_capabilities alone (gate=false).</summary>
    public static List<string> ToolsFor(bool debugEnabled) => debugEnabled
        ? new List<string> {
            "debug_capabilities", "debug_status", "debug_launch", "debug_pause", "debug_continue",
            "debug_restart", "debug_terminate", "debug_read_events", "debug_wait_event",
            "debug_set_breakpoint", "debug_list_breakpoints", "debug_set_breakpoint_enabled",
            "debug_remove_breakpoint", "debug_list_threads", "debug_get_stack", "debug_step",
            "debug_get_locals", "debug_expand_value", "debug_list_modules", "debug_read_memory",
            "debug_dump_module", "debug_set_exception_policy",
        }
        : new List<string> { "debug_capabilities" };

    /// <summary>Six-entry matrix in the fixed §3.5 order; same-bitness rows launch/restart=true.</summary>
    public static List<RuntimeMatrixEntryDto> MatrixFor(string hostArchitecture) {
        var entries = new List<RuntimeMatrixEntryDto>();
        foreach (var (mode, family) in new[] {
            (LaunchModes.Net48Exe, RuntimeFamilies.Net48),
            (LaunchModes.CoreClrAppHost, RuntimeFamilies.CoreClr),
            (LaunchModes.CoreClrDotnet, RuntimeFamilies.CoreClr),
        })
        foreach (var arch in new[] { Architectures.X86, Architectures.X64 }) {
            bool match = arch == hostArchitecture;
            entries.Add(new RuntimeMatrixEntryDto {
                LaunchMode = mode, RuntimeFamily = family, Architecture = arch,
                Launch = match, Restart = match,
                HostPathRequired = mode == LaunchModes.CoreClrDotnet,
                UnavailableReason = match ? null : "host_architecture_mismatch",
            });
        }
        return entries;
    }
}
