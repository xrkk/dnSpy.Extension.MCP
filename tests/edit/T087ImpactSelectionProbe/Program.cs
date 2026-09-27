using System;
using System.Collections.Generic;
using System.Linq;
using dnSpy.Extension.MCP.Editing;

var mvid = Guid.NewGuid();
var edited = new LoadedModule { Mvid = mvid, Path = "edited.dll" };
var sameMvidOtherObject = new LoadedModule { Mvid = mvid, Path = "other.dll" };
var ordinaryOther = new LoadedModule { Mvid = Guid.NewGuid(), Path = "third.dll" };
var selected = EditImpactScanSelection.OtherLoadedModules(
	new[] { edited, sameMvidOtherObject, ordinaryOther }, edited).ToArray();
if (selected.Length != 2 || !ReferenceEquals(selected[0], sameMvidOtherObject)
	|| !ReferenceEquals(selected[1], ordinaryOther))
	throw new Exception("Impact scan must exclude only the edited object, not another loaded module with the same MVID");
Console.WriteLine("T087 impact selection PASS: same-MVID distinct object retained; exact edited object excluded");

var state = new EditImpactScanState();
var risks = new List<Dictionary<string, object?>> {
	new() { ["risk_id"] = "unrelated", ["kind"] = "structural" }
};
var inbound = new Dictionary<string, Dictionary<string, object?>> {
	["inbound"] = new() { ["risk_id"] = "inbound", ["scan_revision"] = 1u }
};
var facts = new Dictionary<string, Dictionary<string, object?>> {
	["inbound"] = new() { ["risk_id"] = "inbound", ["kind"] = "cross_assembly_inbound" }
};
if (!state.Accept(1, "facts-1", inbound, facts, risks) || risks.Count != 2)
	throw new Exception("First inbound scan must add a risk and invalidate a prior review");
if (state.Accept(1, "facts-1", inbound, facts, risks) || risks.Count != 2)
	throw new Exception("Identical scan must preserve the review and avoid duplicate risks");
state.MarkStale(risks);
if (!state.Stale || state.InboundReferences.Count != 0 || risks.Count != 1 || risks[0]["risk_id"]?.ToString() != "unrelated")
	throw new Exception("Apply/import must clear old inbound facts but preserve unrelated risks");
if (!state.Accept(2, "facts-2", inbound, facts, risks) || state.Stale || risks.Count != 2)
	throw new Exception("Rescan at new revision must restore current inbound facts");
if (!state.Accept(2, "facts-3", new Dictionary<string, Dictionary<string, object?>>(),
	new Dictionary<string, Dictionary<string, object?>>(), risks) || risks.Count != 1)
	throw new Exception("Changed scan must remove obsolete inbound risk and invalidate review");
var nextTransaction = new EditImpactScanState();
if (nextTransaction.InboundReferences.Count != 0 || nextTransaction.Revision.HasValue)
	throw new Exception("Inbound facts must not leak to a later transaction");
if (nextTransaction.Accept(0, "empty", new Dictionary<string, Dictionary<string, object?>>(),
	new Dictionary<string, Dictionary<string, object?>>(), risks) || risks.Count != 1)
	throw new Exception("An empty first scan must not create risk or invalidate review");
Console.WriteLine("T087 impact lifecycle PASS: first risk, idempotence, stale/rescan, changed facts, transaction isolation");

sealed class LoadedModule {
	public Guid Mvid { get; init; }
	public string Path { get; init; } = string.Empty;
}
