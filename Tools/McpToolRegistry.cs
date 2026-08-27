using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;

namespace dnSpy.Extension.MCP.Tools;

/// <summary>
/// Single dispatch surface between the MCP server and tool providers. Merges the tools of all
/// MEF-composed <see cref="IMcpToolProvider"/>s and routes each tools/call to the owning
/// provider. The registry holds no dnSpy objects and imposes no thread domain of its own — the
/// WPF/debugger marshaling contract lives inside each provider.
/// </summary>
[Export(typeof(McpToolRegistry))]
internal sealed class McpToolRegistry
{
    readonly IMcpToolProvider[] providers;
    Dictionary<string, IMcpToolProvider>? routeTable;

    [ImportingConstructor]
    public McpToolRegistry([ImportMany] IEnumerable<IMcpToolProvider> providers)
    {
        this.providers = providers.ToArray();
    }

    /// <summary>Aggregated tool list across providers, in provider composition order.</summary>
    public List<ToolInfo> GetAvailableTools()
    {
        var tools = new List<ToolInfo>();
        foreach (var provider in providers)
            tools.AddRange(provider.GetTools());
        return tools;
    }

    /// <summary>
    /// Routes a call to the owning provider. Provider-internal exceptions keep propagating with
    /// their original text; only the unknown-tool case gets the canonical error here.
    /// </summary>
    public CallToolResult ExecuteTool(string toolName, Dictionary<string, object>? arguments)
    {
        var route = routeTable ?? BuildRouteTable();
        if (route.TryGetValue(toolName, out var provider))
        {
            var result = provider.ExecuteTool(toolName, arguments);
            if (result != null)
                return result;
        }
        return new CallToolResult
        {
            Content = new List<ToolContent> { new ToolContent { Text = $"Unknown tool: {toolName}" } },
            IsError = true
        };
    }

    Dictionary<string, IMcpToolProvider> BuildRouteTable()
    {
        var table = new Dictionary<string, IMcpToolProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            foreach (var tool in provider.GetTools())
            {
                if (table.ContainsKey(tool.Name))
                    throw new InvalidOperationException($"Duplicate MCP tool name across providers: {tool.Name} ({table[tool.Name].Name}, {provider.Name})");
                table[tool.Name] = provider;
            }
        }
        routeTable = table;
        return table;
    }
}
