using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Extension.MCP.Tools;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

[Export(typeof(IMcpToolProvider))]
internal sealed class EditToolProvider : IMcpToolProvider, IDisposable {
	static readonly string[] ProductTools = { "edit_begin", "edit_status", "edit_apply", "edit_review", "edit_rollback" };
	static readonly string[] TestTools = { "edit_test_clock", "edit_test_barrier", "edit_test_external_mutation", "edit_test_live_mutation", "edit_test_fault", "edit_test_apply_and_restore" };
	readonly EditTransactionCoordinator coordinator;
	readonly EditSchemaCatalog schemas = new();
	readonly IReadOnlyList<ToolInfo> tools;

	[ImportingConstructor]
	public EditToolProvider(EditTransactionCoordinator coordinator) {
		this.coordinator = coordinator;
		var list = new List<ToolInfo>();
		foreach (var name in ProductTools) list.Add(Tool(name));
		tools = list;
	}

	public string Name => "edit";
	public IReadOnlyList<ToolInfo> GetTools() => tools;
	public IReadOnlyCollection<string> UnadvertisedTools => TestTools;
	public CallToolResult? ExecuteTool(string toolName, Dictionary<string, object>? arguments, McpCallContext callContext) {
		if (!Array.Exists(ProductTools, n => n == toolName) && !Array.Exists(TestTools, n => n == toolName)) return null;
		EditJsonSchemaValidator.Validate(schemas.InputElement(toolName), arguments, toolName);
		return coordinator.Execute(toolName, arguments, callContext);
	}
	ToolInfo Tool(string name) => new() { Name = name, Description = Description(name), InputSchema = schemas.InputSchema(name), OutputSchema = schemas.OutputSchema(name) };
	static string Description(string name) => name switch {
		"edit_begin" => "Begin the process-wide structured edit transaction for one loaded pure-managed single-module assembly. All changes remain private until a later P03 commit workflow.",
		"edit_status" => "Read process-wide edit state. The owning MCP session receives transaction fingerprints, capacity, review and risk details.",
		"edit_apply" => "Apply one of the 22 structured metadata/body operations atomically to the transaction private copy.",
		"edit_review" => "Validate and review the current private revision, returning canonical diffs, risks and roundtrip evidence.",
		"edit_rollback" => "Discard the private transaction and release all in-memory edit state.",
		_ => "DNMCP_TEST-only edit acceptance seam; never advertised.",
	};
	public void Dispose() => schemas.Dispose();
}
