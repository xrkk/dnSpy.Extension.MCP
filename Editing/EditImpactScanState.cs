using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Editing;

// Facts belong to one edit transaction. They must never be reused for another
// transaction's confirmation response.
internal sealed class EditImpactScanState {
	internal uint? Revision { get; private set; }
	internal bool Stale { get; private set; }
	internal string? Facts { get; private set; }
	internal readonly Dictionary<string, Dictionary<string, object?>> InboundReferences = new(StringComparer.Ordinal);

	internal void MarkStale(List<Dictionary<string, object?>> workspaceRisks) {
		if (!Revision.HasValue) return;
		Stale = true;
		Facts = null;
		RemovePreviousRisks(workspaceRisks);
		InboundReferences.Clear();
	}

	// True means an existing review must be cleared. An unchanged repeat at the
	// same revision does not mutate risks or invalidate that review.
	internal bool Accept(uint revision, string facts,
		IReadOnlyDictionary<string, Dictionary<string, object?>> inbound,
		IReadOnlyDictionary<string, Dictionary<string, object?>> riskFacts,
		List<Dictionary<string, object?>> workspaceRisks) {
		var changed = Facts != null && !string.Equals(Facts, facts, StringComparison.Ordinal);
		var newlyAdded = inbound.Keys.Any(id => !InboundReferences.ContainsKey(id));
		if (changed || newlyAdded || Stale || Revision != revision) {
			RemovePreviousRisks(workspaceRisks);
			InboundReferences.Clear();
			foreach (var pair in inbound) InboundReferences.Add(pair.Key, pair.Value);
			foreach (var risk in riskFacts.Values) workspaceRisks.Add(risk);
			Facts = facts;
			Revision = revision;
			Stale = false;
		}
		return changed || newlyAdded;
	}

	void RemovePreviousRisks(List<Dictionary<string, object?>> workspaceRisks) =>
		workspaceRisks.RemoveAll(r => r.TryGetValue("risk_id", out var id)
			&& id is string riskId && InboundReferences.ContainsKey(riskId));
}
