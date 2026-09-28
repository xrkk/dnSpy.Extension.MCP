using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

namespace dnSpy.Extension.MCP.Tests.Edit;

/// <summary>
/// T095-B boundary verification for the approved v3 encoding
/// (P03-OLDV2-NOOBJECT-REVERSE-01 direction A, 2026-09-28): a module whose
/// baseline lacks a System.Object reference row now opens a v3 lineage, the
/// v3 tombstone (interface/struct owner) adds no reference row, and inverse
/// navigation after a disk reload restores the baseline checkpoint image
/// byte-exactly. The historical v2 boundary this probe used to characterize
/// (TypeRef 6-&gt;7-&gt;7, applied image 7c3456be..., exact=False) is closed
/// for new lineages; old v2 packages keep the documented image-gate
/// rejection behavior (see PLAN/2026.09.28-03/-04).
/// </summary>
internal static class T095BOldV2BoundaryProbe {
	public static void Run(string baselinePath) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		var baselineFull = Path.GetFullPath(baselinePath);
		var work = Path.Combine(Path.GetTempPath(), "t095b-noobject-v3-closure");
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

		// Commit the same operation shape as the historical old-v2 failure
		// package: a tail parameter_remove on method row 1.
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
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "t095b-v3-closure", 1, Array.Empty<string>());
		var liveMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		using (var json = JsonDocument.Parse(operation))
			EditOperationRegistry.ApplyPersisted(baselineModule, json.RootElement, liveMap, 0);
		history.Finalize(prepared, baselineModule);
		workspace.NormalizedOperations.Clear();
		var lineageId = prepared.Lineage.Manifest.LineageId;
		var packageBytes = store.FinalBytes(lineageId);
		var packageSha = EditWire.Sha256(packageBytes);
		var format = prepared.Lineage.Manifest.Format;
		Check(format == "dnspy.edit.checkpoints.v3", "no-Object baseline opens a v3 lineage");
		var headId = prepared.Lineage.Manifest.HeadCheckpointId;
		var rootId = prepared.Lineage.Manifest.Checkpoints.Single(n => n.ParentCheckpointId == null).CheckpointId;
		Check(headId != rootId, "lineage has one commit beyond the baseline checkpoint");

		var headAssessment = history.Assess(lineageId, headId, string.Empty);
		var rootAssessment = history.Assess(lineageId, rootId, string.Empty);
		using (var deleted = ModuleDefMD.Load(headAssessment.Bytes!)) {
			var deletedTr = deleted.TablesStream.TypeRefTable.Rows;
			Check(deletedTr == baselineTr, "v3 tombstone adds no reference row (interface owner needs none)");
			Console.WriteLine($"T095B_PACKAGE format={format} sha={packageSha} baseline_tr={baselineTr} deleted_tr={deletedTr}");
		}

		// Disk-reload simulation: a fresh module loaded from the deleted-state
		// image bytes, exactly like a host restart onto the written head state.
		var headImage = Path.Combine(work, "reloaded-head.dll");
		File.WriteAllBytes(headImage, headAssessment.Bytes!);
		using var reloaded = ModuleDefMD.Load(headImage);
		Check(reloaded.TablesStream.TypeRefTable.Rows == baselineTr, "reloaded live module carries no extra reference rows");

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

		var plan = history2.PlanNavigation(lineage2, headId, rootId);
		var planUndo = plan.Apply(reloaded);
		var restoredTr = reloaded.TablesStream.TypeRefTable.Rows;
		var restoredImage = EditWire.Sha256(EditWorkspace.WriteCheckpointImage(reloaded));
		var restoredSemantic = EditHistoryModule.SemanticDigest(format, reloaded);
		var exact = restoredImage == rootAssessment.ImageSha256;
		var semanticExact = restoredSemantic == rootAssessment.SemanticFingerprint;
		var packageUnchanged = EditWire.Sha256(store2.FinalBytes(lineageId)) == packageSha;
		Check(packageUnchanged, "navigation left the stored package byte-for-byte unchanged");
		Console.WriteLine($"T095B_RESULT baseline_tr={baselineTr} deleted_tr={reloaded.TablesStream.TypeRefTable.Rows} restored_tr={restoredTr}"
			+ $" image={restoredImage} target={rootAssessment.ImageSha256} exact={exact}"
			+ $" semantic_exact={semanticExact} package_unchanged={packageUnchanged}");
		// The approved v3 encoding must close the historical boundary: the
		// reloaded module restores the baseline checkpoint image byte-exactly.
		Check(restoredTr == baselineTr && exact && semanticExact,
			"v3 inverse navigation after disk reload restores the baseline checkpoint exactly");
		// The applied navigation remains reversible: replaying the commit
		// forward returns the live module to the head image byte-for-byte.
		planUndo();
		var returnedImage = EditWire.Sha256(EditWorkspace.WriteCheckpointImage(reloaded));
		Check(returnedImage == headAssessment.ImageSha256 && reloaded.TablesStream.TypeRefTable.Rows == baselineTr,
			"replaying the commit forward restores the head image after the closure navigation");
		Console.WriteLine("PASS t095b-noobject-v3-closure boundary-closed-exact-restore");
	}

	static void Check(bool condition, string message) {
		if (!condition) throw new InvalidOperationException("T095B FAIL: " + message);
	}
}
