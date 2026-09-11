using System.Collections.Generic;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class LegacyEditPlan {
	public List<Dictionary<string, object?>> Operations { get; } = new();
	public Dictionary<string, object?> Projection { get; } = new();
	public bool Changed => Operations.Count != 0;
}
