using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Extension.MCP.Tools;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

[Export(typeof(IMcpToolProvider))]
internal sealed class EditToolProvider : IMcpToolProvider, IDisposable {
	static readonly string[] ProductTools = { "edit_begin", "edit_status", "edit_apply", "edit_import", "edit_impact_scan", "edit_review", "edit_rollback", "edit_commit", "edit_history", "edit_undo", "edit_redo", "edit_restore", "edit_export", "edit_recover", "edit_accept_live" };
	static readonly string[] TestTools = { "edit_test_clock", "edit_test_barrier", "edit_test_external_mutation", "edit_test_live_mutation", "edit_test_fault", "edit_test_apply_and_restore", "edit_test_storage_fault", "edit_test_lineage_mutation" };
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
		"edit_apply" => "Apply one of the 26 structured metadata/body operations atomically to the transaction private copy.",
		"edit_import" => "Import compiled C# members from a registered edit_compile artifact into the transaction private copy as frozen structured operations. The compile mapping is all-or-nothing: any unmapped reference or ambiguous target rejects the whole import with zero side effects (OUT-006).",
		"edit_impact_scan" => "Scan the currently loaded modules for inbound AssemblyRef references affected by this transaction's staged identity operations. The report carries scope=loaded_modules with the actual module list — never a global-completeness claim (OUT-007).",
		"edit_review" => "Validate and review the current private revision, returning canonical diffs, risks and roundtrip evidence.",
		"edit_rollback" => "Discard the private transaction and release all in-memory edit state.",
		"edit_commit" => "Commit an approved private edit to the live module and its persistent checkpoint as one recoverable operation.",
		"edit_history" => "Browse persistent edit lineages, checkpoint trees, capacity and recovery facts.",
		"edit_undo" => "Navigate an exact lineage head to its parent while preserving branches.",
		"edit_redo" => "Navigate an exact lineage head to a selected child without silently choosing a branch.",
		"edit_restore" => "Assess or explicitly restore a checkpoint using exact, validated-drift and unverified-drift rules.",
		"edit_export" => "Atomically export an exact checkpoint below ArtifactRoot without overwriting the source sample.",
		"edit_recover" => "Resolve a checkpoint-finalize or bound-temp cleanup recovery without repeating live mutations.",
		"edit_accept_live" => "Explicitly accept a UI-diverged live module as a new baseline lineage without inventing semantic operations.",
		_ => "DNMCP_TEST-only edit acceptance seam; never advertised.",
	};
	public void Dispose() => schemas.Dispose();
}
