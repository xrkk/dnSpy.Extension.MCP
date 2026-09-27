using System;
using System.Collections.Generic;
using dnSpy.Extension.MCP.Editing;

var events = new List<string>();
var acceptedGraph = new Dictionary<string, int> { ["existing"] = 7 };
var privateGraph = new Dictionary<string, int>(acceptedGraph);
var acceptedIds = new Dictionary<string, int> { ["old-id"] = 7 };
var objectIds = new Dictionary<string, int>(acceptedIds);
var revision = 1;
var diffs = new List<string> { "accepted" };
var risks = new List<string> { "accepted-risk" };
var review = "review-accepted";
var cache = new Dictionary<string, string> { ["old-request"] = "accepted" };

void Restore() {
	privateGraph = new Dictionary<string, int>(acceptedGraph);
	objectIds = new Dictionary<string, int>(acceptedIds);
	events.Add("restore");
}

try {
	EditApplyCandidateGate.Validate(
		() => { events.Add("apply"); privateGraph["new-object"] = 0; objectIds["new-id"] = 0; return "candidate"; },
		() => events.Add("structural"),
		() => { events.Add("writer-reload"); privateGraph["new-object"] = 42; objectIds["new-id"] = 42;
			throw new InvalidOperationException("injected writer failure after token assignment"); },
		Restore);
	throw new Exception("fault injection did not reject the candidate");
}
catch (InvalidOperationException ex) when (ex.Message.StartsWith("injected writer failure", StringComparison.Ordinal)) { }
if (string.Join(",", events) != "apply,structural,writer-reload,restore"
	|| privateGraph.Count != 1 || privateGraph["existing"] != 7
	|| objectIds.Count != 1 || objectIds["old-id"] != 7
	|| revision != 1 || diffs.Count != 1 || risks.Count != 1
	|| review != "review-accepted" || cache.Count != 1 || cache["old-request"] != "accepted")
	throw new Exception("rejected writer candidate published state or retained graph/object IDs");

events.Clear();
var accepted = EditApplyCandidateGate.Validate(
	() => { events.Add("apply"); privateGraph["new-object"] = 0; objectIds["new-id"] = 0; return "candidate"; },
	() => events.Add("structural"),
	() => { events.Add("writer-reload"); privateGraph["new-object"] = 42; objectIds["new-id"] = 42; },
	Restore);
if (accepted != "candidate" || string.Join(",", events) != "apply,structural,writer-reload"
	|| objectIds["new-id"] != 42)
	throw new Exception("successful candidate did not cross the gate before publication");
revision++;
diffs.Add(accepted);
cache["new-request"] = "accepted";
Console.WriteLine("PASS T088 apply candidate gate: injected post-token writer failure restores prior graph/map without publication; success crosses writer/reload before publish");
