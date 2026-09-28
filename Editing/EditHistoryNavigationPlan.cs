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
		public uint[]? ReferenceRowsAfter { get; init; }
		public EditMethodImageProjection.Row[]? MethodRowsAfter { get; init; }
	}
	readonly string format;
	readonly IReadOnlyList<Step> steps;
	readonly string beforeFingerprint;
	readonly string afterFingerprint;
	readonly string beforeImageSha256;
	readonly string? afterImageSha256;
	readonly string[]? targetTypeRefs;
	readonly uint[]? targetReferenceRows;
	readonly string[]? sourceTypeRefs;
	readonly uint[]? sourceReferenceRows;
	readonly bool targetHasPdb;

	internal EditHistoryNavigationPlan(string format, IReadOnlyList<Step> steps, string beforeFingerprint, string afterFingerprint, string beforeImageSha256, bool targetHasPdb, string? afterImageSha256 = null, string[]? targetTypeRefs = null, uint[]? targetReferenceRows = null, string[]? sourceTypeRefs = null, uint[]? sourceReferenceRows = null) {
		this.format = format; this.steps = steps; this.beforeFingerprint = beforeFingerprint; this.afterFingerprint = afterFingerprint;
		this.beforeImageSha256 = beforeImageSha256;
		this.afterImageSha256 = afterImageSha256;
		this.targetTypeRefs = targetTypeRefs;
		this.targetReferenceRows = targetReferenceRows;
		this.sourceTypeRefs = sourceTypeRefs;
		this.sourceReferenceRows = sourceReferenceRows;
		this.targetHasPdb = targetHasPdb;
	}

	public Action Apply(ModuleDef live) {
		using var tombstoneMode = EditDeletedRowsTombstone.UseLegacy(!string.Equals(format, EditHistoryModule.PackageFormatV3, StringComparison.Ordinal));
		// T003-R03: both plan gates use the lineage's own semantic algorithm.
		// For v2 this rejects a same-weak-hash method-ownership change before
		// any operation is applied.
		if (EditHistoryModule.SemanticDigest(format, live) != beforeFingerprint) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		Action? sourceProjectionUndo = null;
		if (format == EditHistoryModule.PackageFormatV3
			&& EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)) != beforeImageSha256) {
			// Recover only a verified source checkpoint's writer view, on this live object.
			sourceProjectionUndo = TryRestoreReferenceTails(live, beforeImageSha256, sourceTypeRefs, sourceReferenceRows);
			if (sourceProjectionUndo == null) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		using var verifiedV3 = format == EditHistoryModule.PackageFormatV3 ? EditDeletedRowsTombstone.UseVerifiedV3(live) : null;
		var beforeLiveFingerprint = EditFingerprint.Compute(live);
		var inverses = new List<Action>();
		if (sourceProjectionUndo != null) inverses.Add(sourceProjectionUndo);
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
				if (step.MethodRowsAfter != null)
					inverses.Add(EditMethodImageProjection.Set(live, step.MethodRowsAfter));
				if (step.ReferenceRowsAfter != null)
					inverses.Add(EditReferenceImageProjection.Set(live, step.ReferenceRowsAfter));
			}
			// Target replay proves whether a PDB container existed. Do not rewrite
			// old inverse rows or discard an originally present empty container.
			if (!targetHasPdb && live.PdbState != null) {
				var removed = live.PdbState;
				EditOperationRegistry.RemoveEmptyPdbState(live);
				inverses.Add(() => live.SetPdbState(removed));
			}
			EditStructuralValidator.Validate(live);
			if (EditHistoryModule.SemanticDigest(format, live) != afterFingerprint) {
				throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("navigation_semantics", "module", "Compiled inverse operations did not restore the target semantic fingerprint"));
			}
			if (format == EditHistoryModule.PackageFormatV3 && afterImageSha256 != null
				&& EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)) != afterImageSha256) {
				var restoredRows = TryRestoreReferenceTails(live, afterImageSha256, targetTypeRefs, targetReferenceRows);
				if (restoredRows == null)
					throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails(
						"navigation_image", "module", "Compiled operations did not restore the exact target image"));
				inverses.Add(restoredRows);
			}
		}
		catch {
			Restore(live, inverses, beforeLiveFingerprint);
			throw;
		}
		finally { tokenBindings?.Dispose(); tokenSource?.Dispose(); }
		var used = false;
		var afterLiveFingerprint = EditFingerprint.Compute(live);
		return () => {
			using var undoTombstoneMode = EditDeletedRowsTombstone.UseLegacy(!string.Equals(format, EditHistoryModule.PackageFormatV3, StringComparison.Ordinal));
			using var undoVerifiedV3 = format == EditHistoryModule.PackageFormatV3 ? EditDeletedRowsTombstone.UseVerifiedV3(live) : null;
			if (used || EditFingerprint.Compute(live) != afterLiveFingerprint) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			Restore(live, inverses, beforeLiveFingerprint);
			used = true;
		};
	}

	// Private candidate: only a tail beyond the independently replayed
	// target's five RID-preserved reference tables can be withheld from dnlib's
	// preserving writer. The live metadata's physical row count stays intact;
	// the writer view changes only during checkpoint serialization.
	Action? TryRestoreReferenceTails(ModuleDef live, string targetImageSha256, string[]? targetTypeRefs, uint[]? targetReferenceRows) {
		if (targetTypeRefs == null || targetReferenceRows?.Length != 5 || live is not ModuleDefMD source) return null;
		var originals = new[] { source.TablesStream.TypeRefTable.Rows, source.TablesStream.MemberRefTable.Rows,
			source.TablesStream.StandAloneSigTable.Rows, source.TablesStream.TypeSpecTable.Rows,
			source.TablesStream.MethodSpecTable.Rows };
		var changed = false;
		for (var i = 0; i < originals.Length; i++) {
			if (targetReferenceRows[i] > originals[i]) return null;
			changed |= targetReferenceRows[i] < originals[i];
		}
		if (!changed) return null;
		if (targetReferenceRows[0] < originals[0]) {
			var current = source.GetTypeRefs().OrderBy(x => x.Rid).ToArray();
			if (current.Length != originals[0]) return null;
			for (var i = 0; i < targetTypeRefs.Length; i++)
				if (!string.Equals(current[i].FullName + "|" + current[i].ResolutionScope, targetTypeRefs[i], StringComparison.Ordinal)) return null;
		}
		var undoProjection = EditReferenceImageProjection.Set(live, targetReferenceRows);
		try {
			if (EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)) == targetImageSha256)
				return undoProjection;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) {
			// A retained graph reference may make the shorter source view invalid.
			undoProjection();
			return null;
		}
		undoProjection();
		return null;
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
