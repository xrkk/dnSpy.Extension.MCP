using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>Generated lowering/fault catalog consumer used by the live mutation adapter.</summary>
internal sealed class EditFaultPlan {
	readonly object[] manifest;
	readonly Dictionary<string, JsonElement> rows;
	readonly Dictionary<string, List<object>> fullTraces;

	public EditFaultPlan(JsonElement lowering, JsonElement faults) {
		manifest = faults.GetProperty("faults").EnumerateArray()
			.Select(x => JsonSerializer.Deserialize<object>(x.GetRawText())!).ToArray();
		rows = faults.GetProperty("faults").EnumerateArray().ToDictionary(
			x => x.GetProperty("fault_id").GetString()!, x => x.Clone(), StringComparer.Ordinal);
		fullTraces = new Dictionary<string, List<object>>(StringComparer.Ordinal);
		foreach (var operation in lowering.GetProperty("operations").EnumerateArray()) {
			var kind = operation.GetProperty("kind").GetString()!;
			fullTraces[kind] = faults.GetProperty("faults").EnumerateArray()
				.Where(x => x.GetProperty("operation_kind").GetString() == kind)
				.OrderBy(x => x.GetProperty("direction").GetString() == "forward" ? 0 : 1)
				.ThenBy(x => x.GetProperty("step_index").GetInt32())
				.ThenBy(x => x.GetProperty("boundary").GetString() == "before" ? 0 : 1)
				.Select(x => JsonSerializer.Deserialize<object>(x.GetRawText())!).ToList();
		}
	}

	public object[] Manifest => manifest;
	public IReadOnlyList<object> BoundaryRows(string operationKind, string direction) =>
		fullTraces.TryGetValue(operationKind, out var trace)
			? trace.Where(x => Direction(x) == direction).ToArray()
			: Array.Empty<object>();
	public JsonElement? Armed(string? faultId) => faultId != null && rows.TryGetValue(faultId, out var row) ? row : null;
	public string? ArmedDirection(string? faultId) => Armed(faultId)?.GetProperty("direction").GetString();
	public string? ArmedBoundary(string? faultId) => Armed(faultId)?.GetProperty("boundary").GetString();
	public string? ArmedKind(string? faultId) => Armed(faultId)?.GetProperty("operation_kind").GetString();

	public object[] Trace(IReadOnlyList<string> operationKinds, string? armedFaultId) {
		var trace = new List<object>();
		foreach (var kind in operationKinds) {
			if (!fullTraces.TryGetValue(kind, out var rowsForKind)) continue;
			foreach (var row in rowsForKind.Where(x => Direction(x) == "forward")) {
				trace.Add(row);
				if (Id(row) == armedFaultId) return trace.ToArray();
			}
		}
		for (var i = operationKinds.Count - 1; i >= 0; i--) {
			if (!fullTraces.TryGetValue(operationKinds[i], out var rowsForKind)) continue;
			foreach (var row in rowsForKind.Where(x => Direction(x) == "reverse")) {
				trace.Add(row);
				if (Id(row) == armedFaultId) return trace.ToArray();
			}
		}
		return trace.Take(24).ToArray();
	}

	public object? ArmedObject(string? faultId) => faultId == null ? null : manifest.FirstOrDefault(x => Id(x) == faultId);
	public static string Id(object row) => ((JsonElement)row).GetProperty("fault_id").GetString()!;
	public static string Boundary(object row) => ((JsonElement)row).GetProperty("boundary").GetString()!;
	static string Direction(object row) => ((JsonElement)row).GetProperty("direction").GetString()!;
}
