using System;
using System.Collections.Generic;

namespace dnSpy.Extension.MCP.Tools;

/// <summary>
/// Contributes MCP tools that execute on a single thread domain. A provider owns its dispatch
/// thread domain: the registry only merges tool metadata and routes calls, it never touches
/// provider-internal dnSpy objects. Static providers marshal on the WPF UI Dispatcher; debug
/// providers resolve debugger objects on DbgManager.Dispatcher only (CON-DYN-003).
/// </summary>
internal interface IMcpToolProvider
{
    /// <summary>Stable provider identifier, diagnostics only.</summary>
    string Name { get; }

    /// <summary>Tools contributed by this provider; the registry treats the list as read-only.</summary>
    IReadOnlyList<ToolInfo> GetTools();

    /// <summary>
    /// Dispatches a call to a tool previously returned by <see cref="GetTools"/>. Must return
    /// null when the name is not one of this provider's tools; the registry then reports the
    /// canonical unknown-tool error.
    /// </summary>
    CallToolResult? ExecuteTool(string toolName, Dictionary<string, object>? arguments);

    /// <summary>
    /// Tool names this provider answers without advertising: the fixed-disabled debug APIs
    /// (API-DYN-004/005/010) stay out of tools/list yet must answer direct calls with the
    /// domain CAPABILITY_UNAVAILABLE envelope instead of an unknown-tool text. Providers
    /// without such names return an empty collection.
    /// </summary>
    IReadOnlyCollection<string> UnadvertisedTools { get; }
}
