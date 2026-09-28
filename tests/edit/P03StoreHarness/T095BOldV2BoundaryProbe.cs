using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

namespace dnSpy.Extension.MCP.Tests.Edit;

/// <summary>
/// T095-B characterization probe for the registered v2 no-Object boundary
/// (PLAN-CHANGE-001, P03 implementation record structural conclusion):
/// a tail parameter removal whose ParamDef row has a physical RID re-owns the
/// row to the Object-marker tombstone, which adds a System.Object TypeRef row
/// when the baseline has none. Within the PreserveRids v2 encoding that row
/// cannot be shed by graph operations after a disk reload, so inverse
/// navigation restores the deleted member but leaves one extra reference row:
/// the restored image and the v2 reference-aware semantic digest cannot equal
/// the baseline checkpoint. This probe reproduces the retained R13
/// old-v2-x64 failure condition from the retained NoObject baseline module
/// (reconstructed lineage, not the lost original package) and asserts the
/// failure is deterministic and side-effect free until a PLAN-CHANGE ruling
/// replaces the encoding.
/// </summary>
internal static class T095BOldV2BoundaryProbe {
	public static void Run(string baselinePath) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		var baselineFull = Path.GetFullPath(baselinePath);
		var work = Path.Combine(Path.GetTempPath(), "t095b-oldv2-boundary");
		if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
		Directory.CreateDirectory(work);
		using var catalog = new EditSchemaCatalog();
		using var baselineModule = ModuleDefMD.Load(baselineFull);
		var baselineTr = baselineModule.TablesStream.TypeRefTable.Rows;
		var baselineHasObjectRef = baselineModule.GetTypeRefs().Any(t => t.FullName == "System.Object");
		Check(baselineTr > 0 && !baselineHasObjectRef, "baseline exposes reference rows but no System.Object reference row");
		var method = baselineModule.GetTypes().SelectMany(t => t.Methods).SingleOrDefault(m => m.MDToken.Rid == 1);
		Check(method != null, "baseline exposes method row 1");
		Check(method!.MethodSig.Params.Count == 1 && method.ParamDefs.Count == 1 && method.ParamDefs[0].MDToken.Rid != 0,
			"method row 1 owns exactly one physical tail ParamDef row");

		// Commit the same operation shape as the lost old-v2 package: a tail
		// parameter_remove on method row 1 with reject_if_referenced.
		var store = new InMemoryEditCheckpointStore(Path.Combine(work, "store"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var workspace = EditWorkspace.CreateForTesting(baselineModule);
		var operation = JsonSerializer.Serialize(new {
			kind = "parameter_remove",
			parameter_target = new { owner_method = new { token = "0x06000001" }, parameter_index = 0 },
			remove_mode = "reject_if_referenced",
		});
		using (var json = JsonDocument.Parse(operation)) {
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operation);
		}
		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "t095b-oldv2", 1, Array.Empty<string>());
		var liveMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(baselineModule, json.RootElement, liveMap, 0);
		history.Finalize(prepared, baselineModule);
		workspace.NormalizedOperations.Clear();
		var lineageId = prepared.Lineage.Manifest.LineageId;
		var packageBytes = store.FinalBytes(lineageId);
		var packageSha = EditWire.Sha256(packageBytes);
		var format = prepared.Lineage.Manifest.Format;
		Check(format == "dnspy.edit.checkpoints.v2", "reconstructed lineage uses the v2 format");
		var headId = prepared.Lineage.Manifest.HeadCheckpointId;
		var rootId = prepared.Lineage.Manifest.Checkpoints.Single(n => n.ParentCheckpointId == null).CheckpointId;
		Check(headId != rootId, "lineage has one commit beyond the baseline checkpoint");

		var headAssessment = history.Assess(lineageId, headId, string.Empty);
		var rootAssessment = history.Assess(lineageId, rootId, string.Empty);
		using (var deleted = ModuleDefMD.Load(headAssessment.Bytes!)) {
			var deletedTr = deleted.TablesStream.TypeRefTable.Rows;
			Check(deletedTr == baselineTr + 1, "deleted-state image adds exactly the tombstone System.Object reference row");
			Console.WriteLine($"T095B_PACKAGE format={format} sha={packageSha} baseline_tr={baselineTr} deleted_tr={deletedTr}");
		}

		// Disk-reload simulation: a fresh module loaded from the deleted-state
		// image bytes carries the extra row as a physical source row, exactly
		// like a host restart onto the written head state.
		var headImage = Path.Combine(work, "reloaded-head.dll");
		File.WriteAllBytes(headImage, headAssessment.Bytes!);
		using var reloaded = ModuleDefMD.Load(headImage);
		var reloadedTr = reloaded.TablesStream.TypeRefTable.Rows;
		Check(reloadedTr == baselineTr + 1, "reloaded live module carries the extra reference row as a physical source row");

		var store2 = new InMemoryEditCheckpointStore(Path.Combine(work, "store2"));
		using var history2 = new EditHistoryModule(store2, catalog.CheckpointPackage);
		var validated = history2.ValidatePackageForTesting(packageBytes);
		var temp = store2.CreateTemp(validated.Manifest.LineageId, packageBytes);
		store2.FinalizeTemp(temp, replaceExisting: false);
		var lineage2 = history2.Load(validated.Manifest.LineageId);
		var liveFingerprint = EditFingerprint.Compute(reloaded);
		var reloadedAssessment = history2.Assess(lineageId, headId, liveFingerprint);
		Check(reloadedAssessment.Classification == "exact" && reloadedAssessment.ImageSha256 == headAssessment.ImageSha256,
			"reloaded head state classifies exact against the stored checkpoint");

		// The plan itself verifies on the replay module rebuilt from the
		// lineage baseline, where the extra row is graph-added and can be
		// shed; the defect only appears when the verified plan hits the
		// reloaded physical tables.
		var plan = history2.PlanNavigation(lineage2, headId, rootId);
		var planUndo = plan.Apply(reloaded);
		var restoredTr = reloaded.TablesStream.TypeRefTable.Rows;
		var restoredImage = EditWire.Sha256(EditWorkspace.WriteCheckpointImage(reloaded));
		var restoredSemantic = EditHistoryModule.SemanticDigest(format, reloaded);
		var exact = restoredImage == rootAssessment.ImageSha256;
		var semanticExact = restoredSemantic == rootAssessment.SemanticFingerprint;
		var packageUnchanged = EditWire.Sha256(store2.FinalBytes(lineageId)) == packageSha;
		Check(packageUnchanged, "navigation left the stored package byte-for-byte unchanged");
		Console.WriteLine($"T095B_RESULT baseline_tr={baselineTr} deleted_tr={reloadedTr} restored_tr={restoredTr}"
			+ $" image={restoredImage} target={rootAssessment.ImageSha256} exact={exact}"
			+ $" semantic_exact={semanticExact} package_unchanged={packageUnchanged}");
		// Characterization outcome: the registered structural boundary is the
		// extra reference row surviving inverse navigation after a disk
		// reload (R13 old-v2-x64.log: restored_tr=7, applied image
		// 7c3456be..., exact=False; this probe reproduces the same applied
		// image bytes). The current v2 semantic digest treats the residual
		// unreferenced row as invisible (semantic_exact=True above), so only
		// the image gate observes the drift; the R13 log's digest differed.
		// Any change to these observed properties must be reconciled with the
		// P03 structural conclusion before this probe is treated as green.
		Check(restoredTr == baselineTr + 1 && !exact,
			"inverse navigation restores the member but cannot shed the tombstone reference row (registered v2 boundary)");
		// The applied navigation must remain reversible: replaying the commit
		// forward returns the live module to the head image byte-for-byte.
		planUndo();
		var returnedImage = EditWire.Sha256(EditWorkspace.WriteCheckpointImage(reloaded));
		Check(returnedImage == headAssessment.ImageSha256 && reloaded.TablesStream.TypeRefTable.Rows == baselineTr + 1,
			"replaying the commit forward restores the head image after the boundary-condition navigation");
		Console.WriteLine("PASS t095b-oldv2-boundary structural-failure-reproduced-side-effect-free");
	}

	static void Check(bool condition, string message) {
		if (!condition) throw new InvalidOperationException("T095B FAIL: " + message);
	}
}
