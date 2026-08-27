using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace dnSpy.Extension.MCP.Tools;

/// <summary>
/// Adapts the 32 existing static-analysis tools (<see cref="McpTools"/>) to
/// <see cref="IMcpToolProvider"/>. Thread domain unchanged: every dispatch still goes through
/// McpTools.ExecuteTool's WPF UI-Dispatcher marshal, including its existing per-tool exception
/// wrapping (CON-DYN-003: static providers stay on the WPF Dispatcher).
/// </summary>
[Export(typeof(IMcpToolProvider))]
internal sealed class StaticToolProvider : IMcpToolProvider
{
    readonly McpTools tools;
    HashSet<string>? knownNames;

    [ImportingConstructor]
    public StaticToolProvider(McpTools tools)
    {
        this.tools = tools;
    }

    public string Name => "static";

    public IReadOnlyList<ToolInfo> GetTools() => tools.GetAvailableTools();

    public CallToolResult? ExecuteTool(string toolName, Dictionary<string, object>? arguments)
    {
        var known = knownNames ??= new HashSet<string>(tools.GetAvailableTools().Select(t => t.Name));
        if (!known.Contains(toolName))
            return null;
        return tools.ExecuteTool(toolName, arguments);
    }
}
