using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>One operation-level LCA path, compiled and checked off the UI thread.</summary>
internal sealed class EditHistoryNavigationPlan {
	internal sealed class Step {
		public string CheckpointId { get; init; } = string.Empty;
		public int Index { get; init; }
		public bool IsInverse { get; init; }
		public string Operation { get; init; } = string.Empty;
	}
	readonly string format;
	readonly IReadOnlyList<Step> steps;
	readonly string beforeFingerprint;
	readonly string afterFingerprint;
	readonly bool targetHasPdb;

	internal EditHistoryNavigationPlan(string format, IReadOnlyList<Step> steps, string beforeFingerprint, string afterFingerprint, bool targetHasPdb) {
		this.format = format; this.steps = steps; this.beforeFingerprint = beforeFingerprint; this.afterFingerprint = afterFingerprint;
		this.targetHasPdb = targetHasPdb;
	}

	public Action Apply(ModuleDef live) {
		// T003-R03: both plan gates use the lineage's own semantic algorithm.
		// For v2 this rejects a same-weak-hash method-ownership change before
		// any operation is applied.
		if (EditHistoryModule.SemanticDigest(format, live) != beforeFingerprint) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var beforeLiveFingerprint = EditFingerprint.Compute(live);
		var inverses = new List<Action>();
		var maps = new Dictionary<string, Dictionary<string, IMDTokenProvider>>(StringComparer.Ordinal);
		ModuleDefMD? tokenSource = null;
		IDisposable? tokenBindings = null;
		string? boundCheckpoint = null;
		try {
			foreach (var step in steps) {
				if (boundCheckpoint != step.CheckpointId) {
					tokenBindings?.Dispose(); tokenSource?.Dispose();
					tokenSource = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(live));
					tokenBindings = EditOperationRegistry.BindSerializedTokens(tokenSource, live);
					boundCheckpoint = step.CheckpointId;
				}
				if (!maps.TryGetValue(step.CheckpointId, out var map)) maps[step.CheckpointId] = map = new(StringComparer.Ordinal);
				using var document = JsonDocument.Parse(step.Operation);
				var outcome = step.IsInverse
					? EditOperationRegistry.ApplyCompiledInverse(live, document.RootElement, map, step.Index)
					: EditOperationRegistry.ApplyPersisted(live, document.RootElement, map, step.Index);
				inverses.Add(outcome.Undo);
			}
			// Target replay proves whether a PDB container existed. Do not rewrite
			// old inverse rows or discard an originally present empty container.
			if (!targetHasPdb && live.PdbState != null) {
				var removed = live.PdbState;
				EditOperationRegistry.RemoveEmptyPdbState(live);
				inverses.Add(() => live.SetPdbState(removed));
			}
			EditStructuralValidator.Validate(live);
			if (EditHistoryModule.SemanticDigest(format, live) != afterFingerprint)
				throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("navigation_semantics", "module", "Compiled inverse operations did not restore the target semantic fingerprint"));
		}
		catch {
			Restore(live, inverses, beforeLiveFingerprint);
			throw;
		}
		finally { tokenBindings?.Dispose(); tokenSource?.Dispose(); }
		var used = false;
		var afterLiveFingerprint = EditFingerprint.Compute(live);
		return () => {
			if (used || EditFingerprint.Compute(live) != afterLiveFingerprint) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			Restore(live, inverses, beforeLiveFingerprint);
			used = true;
		};
	}

	static void Restore(ModuleDef live, List<Action> inverses, string fingerprint) {
		try {
			for (var i = inverses.Count - 1; i >= 0; i--) inverses[i]();
			if (EditFingerprint.Compute(live) != fingerprint) throw new InvalidOperationException("navigation inverse fingerprint mismatch");
		}
		catch (Exception ex) {
			throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", new Dictionary<string, object?> {
				["kind"] = "navigation_inverse", ["reason"] = ex.GetType().Name,
			});
		}
	}
}
