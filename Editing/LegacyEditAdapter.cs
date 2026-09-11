using System;
using System.Collections.Generic;
using System.Text.Json;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>Compatibility shell for the six historical static write tools. It owns no mutable
/// edit state; all mutation/history facts stay in <see cref="EditTransactionCoordinator"/>.</summary>
internal sealed class LegacyEditAdapter {
	readonly McpTools tools;
	readonly EditTransactionCoordinator coordinator;

	public LegacyEditAdapter(McpTools tools, EditTransactionCoordinator coordinator) {
		this.tools = tools; this.coordinator = coordinator;
	}

	public CallToolResult Execute(string toolName, Dictionary<string, object>? arguments, McpCallContext context) {
		try {
			if (toolName is "patch_method_il" or "force_return" or "nop_method")
				return Mutation(toolName, arguments, context, workspace => tools.BuildLegacyMethodPlan(toolName, arguments, workspace));
			if (toolName == "rename_symbol_by_token")
				return Mutation(toolName, arguments, context, workspace => tools.BuildLegacyRenamePlan(arguments, workspace));
			if (toolName == "revert_method_il") return Revert(arguments, context);
			if (toolName == "save_assembly") return Export(arguments, context);
			throw new ArgumentException("Legacy write adapter does not support " + toolName);
		}
		catch (EditDomainException ex) { return EditWire.Result(EditWire.Failure(coordinator.State, ex.Code, ex.Details, ex.Message)); }
		catch (Exception ex) { return new CallToolResult { IsError = true, Content = new List<ToolContent> { new ToolContent { Text = "Error executing tool " + toolName + ": " + ex.Message } } }; }
	}

	public CallToolResult EnrichMethodIl(Dictionary<string, object>? arguments, CallToolResult current) {
		if (current.IsError || current.Content.Count == 0) return current;
		var projection = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.Content[0].Text, EditWire.JsonOptions);
		if (projection == null) return current;
		projection["has_pending_patch"] = coordinator.HasPendingLegacyMethod(arguments,
			workspace => tools.ResolveLegacyMethodToken(arguments, workspace));
		return Project(projection);
	}

	CallToolResult Revert(Dictionary<string, object>? arguments, McpCallContext context) {
		var envelope = coordinator.ExecuteLegacyRevert(arguments, context,
			workspace => tools.ResolveLegacyMethodToken(arguments, workspace));
		var current = tools.ExecuteTool("get_method_il", arguments);
		if (current.IsError || current.Content.Count == 0) return current;
		var projection = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.Content[0].Text, EditWire.JsonOptions) ?? new();
		projection["reverted"] = true;
		CopyHistory(envelope, projection);
		projection["compatibility_warning"] = "This legacy revert performed one constrained checkpoint Undo; it never crosses another history head.";
		return Project(projection);
	}

	CallToolResult Export(Dictionary<string, object>? arguments, McpCallContext context) {
		var envelope = coordinator.ExecuteLegacyExport(arguments, context);
		var result = ResultObject(envelope);
		var output = ObjectValue(result, "output");
		var checkpoint = ObjectValue(result, "checkpoint");
		var history = ObjectValue(result, "history");
		var projection = new Dictionary<string, object?> {
			["saved_to"] = output["path"], ["bytes_written"] = output["length"], ["backup_path"] = null,
			["source_preserved"] = true, ["sha256"] = output["sha256"], ["file_id"] = output["file_id"],
			["lineage_id"] = history["lineage_id"], ["checkpoint_id"] = checkpoint["checkpoint_id"],
			["warnings"] = new[] { "Compatibility change: save_assembly exports under ArtifactRoot and never overwrites or backs up the source sample." },
		};
		return Project(projection);
	}

	CallToolResult Mutation(string toolName, Dictionary<string, object>? arguments, McpCallContext context,
		Func<EditWorkspace, LegacyEditPlan> lower) {
		var envelope = coordinator.ExecuteLegacyMutation(toolName, arguments, context, lower, out var plan);
		var projection = new Dictionary<string, object?>(plan.Projection, StringComparer.Ordinal) {
			["compatibility_warning"] = "This legacy call was committed through the structured edit transaction and checkpoint history; use edit_undo/edit_redo for navigation.",
			["backup_path"] = null,
		};
		CopyHistory(envelope, projection);
		return Project(projection);
	}

	static void CopyHistory(Dictionary<string, object?> envelope, Dictionary<string, object?> projection) {
		var result = ResultObject(envelope);
		if (result.TryGetValue("checkpoint", out var checkpoint)) projection["checkpoint"] = checkpoint;
		if (result.TryGetValue("history", out var history)) projection["history"] = history;
		if (result.TryGetValue("confirmed_risks", out var risks)) projection["confirmed_risks"] = risks;
	}
	static Dictionary<string, object?> ResultObject(Dictionary<string, object?> envelope) => ObjectValue(envelope, "result");
	static Dictionary<string, object?> ObjectValue(Dictionary<string, object?> source, string name) {
		if (!source.TryGetValue(name, out var value)) throw new InvalidOperationException("Missing " + name + " result");
		if (value is Dictionary<string, object?> dictionary) return dictionary;
		if (value is JsonElement { ValueKind: JsonValueKind.Object } element)
			return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), EditWire.JsonOptions) ?? new();
		throw new InvalidOperationException("Invalid " + name + " result");
	}
	static CallToolResult Project(Dictionary<string, object?> projection) => new() {
		Content = new List<ToolContent> { new ToolContent { Text = JsonSerializer.Serialize(projection, EditWire.JsonOptions) } },
	};
}
