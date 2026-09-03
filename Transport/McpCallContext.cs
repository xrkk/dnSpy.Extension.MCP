namespace dnSpy.Extension.MCP.Transport;

/// <summary>
/// Server-authored context for one MCP request. Tool arguments can contain target/debug session
/// identifiers, but they can never create or replace this transport identity.
/// </summary>
internal sealed class McpCallContext
{
    public McpTransportKind TransportKind { get; }
    public string? AuthoritativeSessionId { get; }
    public string ProtocolVersion { get; }
    public bool IsInitializedSession { get; }
    public bool CanOwnEditTransaction => IsInitializedSession && AuthoritativeSessionId != null;

    McpCallContext(
        McpTransportKind transportKind,
        string? authoritativeSessionId,
        string protocolVersion,
        bool isInitializedSession)
    {
        TransportKind = transportKind;
        AuthoritativeSessionId = authoritativeSessionId;
        ProtocolVersion = protocolVersion;
        IsInitializedSession = isInitializedSession;
    }

    public static McpCallContext LegacySse(string sessionId, string protocolVersion, bool initialized) =>
        new McpCallContext(McpTransportKind.LegacySse, sessionId, protocolVersion, initialized);

    public static McpCallContext StreamableHttp(string sessionId, string protocolVersion, bool initialized) =>
        new McpCallContext(McpTransportKind.StreamableHttp, sessionId, protocolVersion, initialized);

    public static McpCallContext CompatibilityPlainHttp(string protocolVersion) =>
        new McpCallContext(McpTransportKind.CompatibilityPlainHttp, null, protocolVersion, false);
}

internal enum McpTransportKind
{
    LegacySse,
    StreamableHttp,
    CompatibilityPlainHttp,
}

internal static class McpTransportKindNames
{
    public static string ToWireName(this McpTransportKind kind) => kind switch
    {
        McpTransportKind.LegacySse => "legacy_sse",
        McpTransportKind.StreamableHttp => "streamable_http",
        _ => "compatibility_plain_http",
    };
}
