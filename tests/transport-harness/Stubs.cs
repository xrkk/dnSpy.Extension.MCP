using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using dnSpy.Extension.MCP.Transport;

// Only host/MEF dependencies are replaced. HTTP routing, sessions, lifecycle and protocol are production source.
namespace System.ComponentModel.Composition {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] sealed class ExportAttribute : Attribute { public ExportAttribute(Type type) {} }
    [AttributeUsage(AttributeTargets.Constructor)] sealed class ImportingConstructorAttribute : Attribute {}
    [AttributeUsage(AttributeTargets.Parameter)] sealed class ImportManyAttribute : Attribute {}
}
namespace dnSpy.Extension.MCP {
    sealed class McpSettings {
        public bool EnableServer => true;
        public string Host => "localhost";
        public int Port { get; set; }
        public McpSettingsSnapshot? CurrentSnapshot => null;
        public readonly ConcurrentQueue<string> Logs = new();
        public void Log(string value) => Logs.Enqueue(value);
        public void SetServerRunning(bool value) {}
    }
    sealed class BepInExResources {
        public List<ResourceInfo> GetResources() => new();
        public string? ReadResource(string uri) => null;
    }
    static class McpDocumentationResources { public const string Instructions = "test"; }
}
namespace dnSpy.Extension.MCP.Tools {
    sealed class McpToolRegistry {
        public Func<CallToolResult>? OnCall;
        public List<ToolInfo> GetAvailableTools() => new();
        public CallToolResult ExecuteTool(string name, Dictionary<string, object>? args, McpCallContext context) => OnCall?.Invoke() ?? new();
    }
}
