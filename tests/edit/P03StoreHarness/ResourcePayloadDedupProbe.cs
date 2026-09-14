using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// CHK-023 / P08 resource payload dedup probe.  Drives the production checkpoint
// path (PrepareCommit/Finalize on an in-memory store), inspects the actual
// package ZIP, re-parses persisted packages with a fresh history module,
// navigates persisted inverses, appends to legacy inline packages, and proves
// the envelope-level rejection rules with side-effect invariants.
internal static class ResourcePayloadDedupProbe {
	static readonly string[] ForwardPayloadKinds = { "managed_resource_add", "managed_resource_update", "win32_resource_add", "win32_resource_update" };
	static readonly string[] InversePayloadShapes = { "managed_resource_update_state", "managed_resource_restore_state", "win32_resource_restore_state" };
	const string CanonicalSha = "c8f5d0341d54d951a71b136e6e2afcb14d11ed8489a7ae126a8fee0df6ecf193";

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		TestPackageUniqueness(fixture);
		TestNavigationAndBranch(fixture);
		TestEntryModeInverse(fixture);
		TestLegacyInlinePackage(fixture);
		TestRejections(fixture);
		TestReadback(fixture);
		Console.WriteLine("PASS resource-payload-dedup A1=package-unique+envelope-union A2=persisted-inverse-navigation+branch "
			+ "A3=legacy-inline-readable+appendable A3b=entry-mode-inverse-dedup A4=rejections-zero-side-effect A5=reload-readback+empty-payload");
	}

	// A1: one lineage, one physical blob per distinct byte sequence, no inline
	// duplicate in any operation envelope, declarations exactly the used union.
	static void TestPackageUniqueness(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-dedup"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var a = Repeat256();
		Check(EditWire.Sha256(a) == CanonicalSha, "the 4096-byte repro payload hashes over its decoded bytes");
		var method = live.GetTypes().Single(x => x.FullName == "TestIL.Simple").Methods.Single(x => x.Name == "AddOne");
		var first = CommitOperations(history, workspace, live, "review-dedup-1",
			ManagedAdd("P08.Dedup.Managed.One", a), ManagedAdd("P08.Dedup.Managed.Two", a), MethodBodyReplace(method.MDToken.Raw));
		var second = CommitOperations(history, workspace, live, "review-dedup-2",
			Win32Add(10, 11, a), Win32Add(10, 12, a));
		var lineageId = store.ListFinalIds().Single();
		using var replayer = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = replayer.Load(lineageId);
		var bodyHash = lineage.Operations[first].Operations.Single(x => x.Kind == "method_body_replace").PayloadSha256.Single();
		Check(lineage.Manifest.Payloads.Count == 2, "payload pool holds exactly the resource blob and the method body");
		var canonical = lineage.Manifest.Payloads.Single(x => x.Sha256 == CanonicalSha);
		Check(canonical.Entry == "payloads/" + CanonicalSha + ".bin" && canonical.Length == a.LongLength, "canonical payload manifest row");
		Check(lineage.Manifest.Payloads.Any(x => x.Sha256 == bodyHash && x.Length > 0), "method body payload row retained");
		var entries = ReadEntries(store.FinalBytes(lineageId));
		Check(entries.ContainsKey("payloads/" + CanonicalSha + ".bin"), "canonical payload file present");
		Check(entries.Keys.Count(x => x.StartsWith("payloads/", StringComparison.Ordinal)) == 2, "one physical payload entry per distinct blob");
		Check(entries.Keys.Count(x => x == "baseline/module.bin") == 1, "baseline kept exactly once");
		foreach (var row in lineage.Manifest.Payloads)
			Check(entries.TryGetValue(row.Entry, out var bytes) && bytes.LongLength == row.Length && EditWire.Sha256(bytes) == row.Sha256,
				"payload entry identity " + row.Sha256);
		var inline = Convert.ToBase64String(a);
		foreach (var checkpointId in new[] { first, second })
			Check(!Encoding.UTF8.GetString(entries["operations/" + checkpointId + ".json"]).Contains(inline, StringComparison.Ordinal),
				"operation envelope carries no inline duplicate: " + checkpointId);
		foreach (var checkpointId in new[] { first, second })
			foreach (var operation in lineage.Operations[checkpointId].Operations.Where(x => ForwardPayloadKinds.Contains(x.Kind, StringComparer.Ordinal)))
				Check(operation.Forward.TryGetValue("data_base64", out var value)
					&& value is JsonElement { ValueKind: JsonValueKind.Object } reference
					&& reference.EnumerateObject().Count() == 1
					&& reference.TryGetProperty("payload_sha256", out var sha)
					&& sha.GetString() == CanonicalSha, "resource forward carries the shared reference: " + operation.Kind);
		AssertEnvelopeDeclarations(entries);
	}

	// A2: add(A) -> update(B) -> remove and the Win32 pair; persisted compiled
	// inverses drive byte-exact undo/redo, and a branch from an old checkpoint
	// keeps the parent link and the shared pool.
	static void TestNavigationAndBranch(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-nav"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var a = Repeat256();
		var b = Reverse256();
		var name = "P08.Nav.Managed";
		var add = CommitOperations(history, workspace, live, "review-nav-1", ManagedAdd(name, a), Win32Add(10, 1, a));
		var update = CommitOperations(history, workspace, live, "review-nav-2", ManagedUpdate(name, b));
		var remove = CommitOperations(history, workspace, live, "review-nav-3", ManagedRemove(name));
		var win32Update = CommitOperations(history, workspace, live, "review-nav-4", Win32Update(10, 1, b));
		var win32Remove = CommitOperations(history, workspace, live, "review-nav-5", Win32Remove(10, 1));
		var lineageId = store.ListFinalIds().Single();
		using var replayer = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = replayer.Load(lineageId);
		var expectedPool = new[] { EditWire.Sha256(a), EditWire.Sha256(b) }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
		Check(lineage.Manifest.Payloads.Count == 2
			&& lineage.Manifest.Payloads.Select(x => x.Sha256).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectedPool),
			"captured inverse old states share the forward payload pool");
		var entries = ReadEntries(store.FinalBytes(lineageId));
		var inlineA = Convert.ToBase64String(a);
		var inlineB = Convert.ToBase64String(b);
		foreach (var row in entries.Where(x => x.Key.StartsWith("operations/", StringComparison.Ordinal))) {
			var text = Encoding.UTF8.GetString(row.Value);
			Check(!text.Contains(inlineA, StringComparison.Ordinal) && !text.Contains(inlineB, StringComparison.Ordinal),
				"persisted inverse/forward bytes are references, not base64: " + row.Key);
		}
		AssertEnvelopeDeclarations(entries);
		var undo = replayer.PlanNavigation(lineage, win32Remove, add);
		undo.Apply(live);
		Check(ManagedBytes(live, name)?.SequenceEqual(a) == true, "persisted managed inverse restores A byte-for-byte");
		Check(Win32Bytes(live, 10, 1)?.SequenceEqual(a) == true, "persisted win32 inverse restores A byte-for-byte");
		var redo = replayer.PlanNavigation(lineage, add, win32Remove);
		redo.Apply(live);
		Check(ManagedBytes(live, name) == null, "redo through persisted forwards removes the managed resource");
		Check(Win32Bytes(live, 10, 1) == null, "redo through persisted forwards removes the win32 resource");
		replayer.PlanNavigation(lineage, win32Remove, add).Apply(live);
		var headMove = replayer.PrepareHeadMove(lineageId, win32Remove, add, "branch-undo");
		replayer.Finalize(headMove, live);
		using var branchWorkspace = EditWorkspace.CreateForTesting(live);
		var branch = CommitOperations(replayer, branchWorkspace, live, "review-nav-branch", null, new[] { ManagedAdd("P08.Nav.Branch", a) });
		var branched = replayer.Load(lineageId);
		var branchNode = branched.Manifest.Checkpoints.Single(x => x.CheckpointId == branch);
		Check(branchNode.ParentCheckpointId == add, "branch checkpoint links to the old checkpoint");
		Check(branched.Manifest.Checkpoints.Count == 7, "baseline + five steps + branch");
		Check(branched.Manifest.Payloads.Count == 2, "branching reuses the shared payload pool");
		Check(ManagedBytes(live, "P08.Nav.Branch")?.SequenceEqual(a) == true, "branch content applied to live");
	}

	// Rule 3: an entry-mode update creates no forward data_base64, but its
	// captured full old blob still enters the package payload pool.
	static void TestEntryModeInverse(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-entry"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var blob = BuildResourceBlob();
		var name = "P08.Entry.Managed";
		var add = CommitOperations(history, workspace, live, "review-entry-1", ManagedAdd(name, blob));
		var update = CommitOperations(history, workspace, live, "review-entry-2", JsonSerializer.Serialize(new Dictionary<string, object?> {
			["kind"] = "managed_resource_update",
			["target"] = new Dictionary<string, object?> { ["name"] = name },
			["entry"] = new Dictionary<string, object?> { ["name"] = "count", ["value_kind"] = "i4", ["value"] = 77 },
		}, EditWire.JsonOptions));
		var lineageId = store.ListFinalIds().Single();
		using var replayer = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = replayer.Load(lineageId);
		var blobSha = EditWire.Sha256(blob);
		Check(lineage.Manifest.Payloads.Count == 1 && lineage.Manifest.Payloads[0].Sha256 == blobSha,
			"entry-mode update deduplicates the full old blob against the add forward");
		var updateOperation = lineage.Operations[update].Operations.Single();
		Check(!updateOperation.Forward.ContainsKey("data_base64"), "entry-mode forward carries no data_base64");
		Check(updateOperation.PayloadSha256.SequenceEqual(new[] { blobSha }), "entry-mode declaration is the captured inverse blob");
		var entries = ReadEntries(store.FinalBytes(lineageId));
		Check(!Encoding.UTF8.GetString(entries["operations/" + update + ".json"]).Contains(Convert.ToBase64String(blob), StringComparison.Ordinal),
			"entry-mode envelope carries no inline blob");
		AssertEnvelopeDeclarations(entries);
		replayer.PlanNavigation(lineage, update, add).Apply(live);
		var restored = ManagedBytes(live, name) ?? throw new InvalidOperationException("FAILED: restored entry-mode resource missing");
		var value = EditResourceCodec.Parse(restored).Entries.First(x => x.Name == "count").Decoded;
		Check(value is int count && count == 42, "persisted entry-mode inverse restores the old entry value");
	}

	// A3: a pre-externalization package (inline base64, empty declarations, no
	// payload rows) stays readable, replayable, navigable, and appendable.
	static void TestLegacyInlinePackage(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-legacy"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var a = Repeat256();
		var b = Reverse256();
		var c = ThirdPattern();
		var name = "P08.Legacy.Managed";
		var first = CommitOperations(history, workspace, live, "review-legacy-1", ManagedAdd(name, a));
		var head = CommitOperations(history, workspace, live, "review-legacy-2", ManagedUpdate(name, b));
		var realId = store.ListFinalIds().Single();
		var legacyId = "lineage-" + Guid.NewGuid().ToString("N");
		var legacyFamily = "family-" + Guid.NewGuid().ToString("N");
		var legacyPackage = RewriteAsInline(store.FinalBytes(realId), legacyId, legacyFamily);
		{
			var temp = store.CreateTemp(legacyId, legacyPackage);
			store.FinalizeTemp(temp, replaceExisting: false);
		}
		using var replayer = new EditHistoryModule(store, catalog.CheckpointPackage);
		var legacy = replayer.Load(legacyId);
		Check(legacy.Manifest.Format == "dnspy.edit.checkpoints.v1", "legacy package keeps the frozen v1 format");
		Check(legacy.Manifest.Payloads.Count == 0 && legacy.PayloadBytes.Count == 0, "legacy package has no payload rows");
		Check(replayer.Assess(legacyId, head, EditFingerprint.Compute(live)).Classification == "exact", "legacy inline package replays exact");
		var undo = replayer.PlanNavigation(legacy, head, first);
		undo.Apply(live);
		Check(ManagedBytes(live, name)?.SequenceEqual(a) == true, "legacy inline inverse restores A");
		var redo = replayer.PlanNavigation(legacy, first, head);
		redo.Apply(live);
		Check(ManagedBytes(live, name)?.SequenceEqual(b) == true, "legacy inline forward restores B");
		using var appendWorkspace = EditWorkspace.CreateForTesting(live);
		var appended = CommitOperations(replayer, appendWorkspace, live, "review-legacy-append", legacyFamily, new[] { ManagedAdd("P08.Legacy.Appended", c) });
		var next = replayer.Load(legacyId);
		Check(next.Manifest.Payloads.Count == 1 && next.Manifest.Payloads[0].Sha256 == EditWire.Sha256(c), "append adds only the new payload");
		Check(next.Checkpoint(first).OperationSha256 == legacy.Checkpoint(first).OperationSha256, "legacy node bytes survive the append");
		var entries = ReadEntries(store.FinalBytes(legacyId));
		Check(Encoding.UTF8.GetString(entries["operations/" + first + ".json"]).Contains(Convert.ToBase64String(a), StringComparison.Ordinal),
			"legacy inline bytes remain readable in the appended package");
		var appendedRow = JsonSerializer.Deserialize<EditCheckpointOperations>(entries["operations/" + appended + ".json"], EditWire.JsonOptions)!;
		var appendedOperation = appendedRow.Operations.Single();
		Check(appendedOperation.PayloadSha256.SequenceEqual(new[] { EditWire.Sha256(c) })
			&& appendedOperation.Forward.TryGetValue("data_base64", out var value) && value is JsonElement { ValueKind: JsonValueKind.Object },
			"appended operation externalizes while the old nodes stay inline");
	}

	// A4: each malformed reference shape/declaration rejects with
	// EDIT_CHECKPOINT_INVALID and leaves live and the store untouched.
	static void TestRejections(string fixture) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-reject"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var a = Repeat256();
		var b = Reverse256();
		var name = "P08.Reject.Managed";
		var method = live.GetTypes().Single(x => x.FullName == "TestIL.Simple").Methods.Single(x => x.Name == "AddOne");
		var first = CommitOperations(history, workspace, live, "review-reject-1", ManagedAdd(name, a), MethodBodyReplace(method.MDToken.Raw));
		var second = CommitOperations(history, workspace, live, "review-reject-2", ManagedUpdate(name, b));
		var third = CommitOperations(history, workspace, live, "review-reject-3", Win32Add(10, 31, a));
		var realId = store.ListFinalIds().Single();
		var baselinePackage = store.FinalBytes(realId);
		var baseline = history.Load(realId);
		var bodyHash = baseline.Operations[first].Operations.Single(x => x.Kind == "method_body_replace").PayloadSha256.Single();
		Check(baseline.Operations[second].Operations.Single().Kind == "managed_resource_update", "managed update present for inverse variant");
		Check(third != first && third != second, "three reject checkpoints committed");

		void ExpectInvalid(string label, byte[] crafted) {
			var liveBefore = EditFingerprint.Compute(live);
			var storeBefore = store.FinalBytes(realId);
			var countBefore = store.ListFinalIds().Count;
			var rejected = false;
			try { history.ValidatePackageForTesting(crafted); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { rejected = true; }
			Check(rejected, "variant rejected with EDIT_CHECKPOINT_INVALID: " + label);
			Check(EditFingerprint.Compute(live) == liveBefore, "live fingerprint unchanged: " + label);
			Check(store.FinalBytes(realId).SequenceEqual(storeBefore) && store.ListFinalIds().Count == countBefore, "store finals unchanged: " + label);
		}
		byte[] Craft(string? label, Action<EditCheckpointManifest, Dictionary<string, EditCheckpointOperations>, Dictionary<string, byte[]>> mutate) =>
			CraftPackage(baselinePackage, "lineage-" + Guid.NewGuid().ToString("N"), "family-" + Guid.NewGuid().ToString("N"), mutate);

		ExpectInvalid("declared payload row and file missing", Craft(null, (manifest, _, entries) => {
			manifest.Payloads.RemoveAll(x => x.Sha256 == CanonicalSha);
			entries.Remove("payloads/" + CanonicalSha + ".bin");
		}));
		ExpectInvalid("payload bytes do not match the recorded hash", Craft(null, (_, _, entries) => {
			entries["payloads/" + CanonicalSha + ".bin"][0] ^= 0xFF;
		}));
		ExpectInvalid("actual reference not present in the declaration", Craft(null, (_, operations, _) => {
			FirstOperation(operations[first]).Forward["data_base64"] = PayloadReference(bodyHash);
		}));
		ExpectInvalid("declaration carries an unused hash", Craft(null, (_, operations, _) => {
			var operation = FirstOperation(operations[third]);
			operation.PayloadSha256 = operation.PayloadSha256.Concat(new[] { bodyHash }).ToArray();
		}));
		ExpectInvalid("reference object with an extra key", Craft(null, (_, operations, _) => {
			FirstOperation(operations[first]).Forward["data_base64"] = new Dictionary<string, object?> {
				["payload_sha256"] = CanonicalSha, ["extra"] = "1",
			};
		}));
		ExpectInvalid("pseudo reference at a non-whitelisted position", Craft(null, (_, operations, _) => {
			FirstOperation(operations[first]).Forward["note"] = PayloadReference(CanonicalSha);
		}));
		ExpectInvalid("inverse reference not present in the declaration", Craft(null, (_, operations, _) => {
			var operation = operations[second].Operations.Single(x => x.Kind == "managed_resource_update");
			if (operation.Inverse["state"] is not JsonElement { ValueKind: JsonValueKind.Object } state)
				throw new InvalidOperationException("FAILED: persisted update state is an object");
			var stateObject = JsonSerializer.Deserialize<Dictionary<string, object?>>(state.GetRawText(), EditWire.JsonOptions)!;
			var updateState = JsonSerializer.Deserialize<Dictionary<string, object?>>(
				((JsonElement)stateObject["managed_resource_update_state"]!).GetRawText(), EditWire.JsonOptions)!;
			updateState["data_base64"] = PayloadReference(bodyHash);
			stateObject["managed_resource_update_state"] = updateState;
			operation.Inverse["state"] = stateObject;
		}));
		{
			var badId = "lineage-" + Guid.NewGuid().ToString("N");
			var badPackage = CraftPackage(baselinePackage, badId, "family-" + Guid.NewGuid().ToString("N"), (_, operations, _) => {
				FirstOperation(operations[first]).Forward["data_base64"] = new Dictionary<string, object?> {
					["payload_sha256"] = CanonicalSha, ["extra"] = "1",
				};
			});
			var temp = store.CreateTemp(badId, badPackage);
			store.FinalizeTemp(temp, replaceExisting: false);
			var liveBefore = EditFingerprint.Compute(live);
			var storeBefore = store.FinalBytes(realId);
			var rejected = false;
			try { history.Load(badId); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_CHECKPOINT_INVALID") { rejected = true; }
			Check(rejected, "injected malformed package rejects on load");
			Check(EditFingerprint.Compute(live) == liveBefore && store.FinalBytes(realId).SequenceEqual(storeBefore),
				"malformed package load leaves live and the real lineage untouched");
		}
	}

	// A5: the replay image itself is reloaded and read back through dnlib; a
	// zero-byte captured payload uses the empty-content SHA without breaking
	// the package, and the resource limit is fixed.  (The frozen wire grammar
	// declares resource data_base64 minLength=1, so a zero-byte payload only
	// enters through inverse capture of a baseline resource.)
	static void TestReadback(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p08-readback"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = ModuleDefMD.Load(Path.GetFullPath(fixture));
		live.Resources.Add(new EmbeddedResource("P08.Empty.Resource", Array.Empty<byte>(), ManifestResourceAttributes.Private));
		using var workspace = EditWorkspace.CreateForTesting(live);
		var a = Repeat256();
		var resources = CommitOperations(history, workspace, live, "review-readback-1", ManagedAdd("P08.Readback.Managed", a), Win32Add(10, 41, a));
		var empty = CommitOperations(history, workspace, live, "review-readback-2", ManagedRemove("P08.Empty.Resource"));
		var lineageId = store.ListFinalIds().Single();
		using var replayer = new EditHistoryModule(store, catalog.CheckpointPackage);
		var lineage = replayer.Load(lineageId);
		var emptySha = EditWire.Sha256(Array.Empty<byte>());
		Check(lineage.Manifest.Payloads.Count == 2 && lineage.Manifest.Payloads.Single(x => x.Sha256 == emptySha).Length == 0,
			"zero-byte captured payload stored once under the empty-content SHA");
		Check(lineage.PayloadBytes[emptySha].Length == 0, "payload pool exposes the zero-byte blob");
		var entries = ReadEntries(store.FinalBytes(lineageId));
		Check(entries.TryGetValue("payloads/" + emptySha + ".bin", out var emptyEntry) && emptyEntry.Length == 0, "zero-byte payload file present");
		Check(entries.ContainsKey("payloads/" + CanonicalSha + ".bin"), "non-empty payload file present");
		AssertEnvelopeDeclarations(entries);
		var resourcesAssessment = replayer.Assess(lineageId, resources, EditFingerprint.Compute(live));
		Check(resourcesAssessment.Classification == "exact", "resource replay classifies exact");
		using var reloaded = ModuleDefMD.Load(resourcesAssessment.Bytes);
		Check(ManagedBytes(reloaded, "P08.Readback.Managed")?.SequenceEqual(a) == true, "managed resource reads back byte-for-byte from the replay image");
		Check(Win32Bytes(reloaded, 10, 41)?.SequenceEqual(a) == true, "win32 resource reads back byte-for-byte from the replay image");
		Check(ManagedBytes(reloaded, "P08.Empty.Resource") is { Length: 0 }, "baseline zero-byte resource survives the checkpoint image");
		var emptyAssessment = replayer.Assess(lineageId, empty, EditFingerprint.Compute(live));
		Check(emptyAssessment.Classification == "exact", "zero-byte capture replay classifies exact");
		using var afterRemove = ModuleDefMD.Load(emptyAssessment.Bytes);
		Check(ManagedBytes(afterRemove, "P08.Empty.Resource") == null, "the zero-byte resource removal replays");
		Check(EditWire.MaxResourceBytes == 8 * 1024 * 1024, "resource payload limit is unchanged");
		// Boundary evidence: the frozen public schema is minLength=1 for resource
		// data_base64, so a forward add of a zero-byte payload is out of domain.
		using var scratch = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var emptyAddRejected = false;
		try {
			using var json = JsonDocument.Parse(ManagedAdd("P08.Empty.Rejected", Array.Empty<byte>()));
			EditOperationRegistry.Apply(scratch, json.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
		}
		catch (ArgumentException) { emptyAddRejected = true; }
		Check(emptyAddRejected, "zero-byte forward payload rejected by the frozen grammar");
	}

	static string CommitOperations(EditHistoryModule history, EditWorkspace workspace, ModuleDef live, string review, params string[] operations) =>
		CommitOperations(history, workspace, live, review, null, operations);

	static string CommitOperations(EditHistoryModule history, EditWorkspace workspace, ModuleDef live, string review, string? familyId, IReadOnlyList<string> operations) {
		for (var index = 0; index < operations.Count; index++) {
			using var json = JsonDocument.Parse(operations[index]);
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operations[index]);
		}
		var binding = history.ResolveBegin(workspace, familyId);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, review, 1, Array.Empty<string>());
		var liveMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		foreach (var operation in operations) {
			using var json = JsonDocument.Parse(operation);
			EditOperationRegistry.ApplyPersisted(live, json.RootElement, liveMap, 0);
		}
		history.Finalize(prepared, live);
		workspace.NormalizedOperations.Clear();
		return prepared.PostHeadCheckpointId;
	}

	static void AssertEnvelopeDeclarations(Dictionary<string, byte[]> entries) {
		var count = 0;
		foreach (var row in entries.Where(x => x.Key.StartsWith("operations/", StringComparison.Ordinal))) {
			using var document = JsonDocument.Parse(row.Value);
			foreach (var operation in document.RootElement.GetProperty("operations").EnumerateArray()) {
				var kind = operation.GetProperty("kind").GetString()!;
				var declared = operation.GetProperty("payload_sha256").EnumerateArray().Select(x => x.GetString()!).ToArray();
				Check(declared.Distinct(StringComparer.Ordinal).Count() == declared.Length, "envelope declaration is unique");
				var actual = new List<string>();
				var forward = operation.GetProperty("forward");
				if (forward.TryGetProperty("data_base64", out var data) && data.ValueKind == JsonValueKind.Object)
					actual.Add(ReferenceHash(data, "forward data_base64"));
				if (kind is "method_body_replace" or "method_add" && forward.TryGetProperty("body", out var body)
					&& body.ValueKind == JsonValueKind.Object && body.TryGetProperty("payload_sha256", out _))
					actual.Add(ReferenceHash(body, "forward body"));
				var state = operation.GetProperty("inverse").GetProperty("state");
				foreach (var shape in InversePayloadShapes)
					if (state.TryGetProperty(shape, out var shapeValue) && shapeValue.TryGetProperty("data_base64", out var captured)
						&& captured.ValueKind == JsonValueKind.Object)
						actual.Add(ReferenceHash(captured, shape));
				Check(new HashSet<string>(actual, StringComparer.Ordinal).SetEquals(declared),
					"envelope declaration equals the persisted reference union for " + kind
					+ " declared=[" + string.Join(",", declared) + "] actual=[" + string.Join(",", actual) + "]");
				count++;
			}
		}
		Check(count > 0, "package carries operation envelopes");
	}

	static string ReferenceHash(JsonElement reference, string slot) {
		if (reference.ValueKind != JsonValueKind.Object || reference.EnumerateObject().Count() != 1
			|| !reference.TryGetProperty("payload_sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
			throw new InvalidOperationException("FAILED: reference shape at " + slot);
		return sha.GetString()!;
	}

	static EditSerializedOperation FirstOperation(EditCheckpointOperations entry) =>
		entry.Operations.First(x => ForwardPayloadKinds.Contains(x.Kind, StringComparer.Ordinal));

	static Dictionary<string, object?> PayloadReference(string hash) => new() { ["payload_sha256"] = hash };

	static byte[] CraftPackage(byte[] package, string lineageId, string familyId,
			Action<EditCheckpointManifest, Dictionary<string, EditCheckpointOperations>, Dictionary<string, byte[]>> mutate) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var operations = manifest.Checkpoints.ToDictionary(x => x.CheckpointId,
			x => JsonSerializer.Deserialize<EditCheckpointOperations>(entries[x.OperationEntry], EditWire.JsonOptions)!, StringComparer.Ordinal);
		mutate(manifest, operations, entries);
		manifest.LineageId = lineageId; manifest.FamilyId = familyId;
		foreach (var node in manifest.Checkpoints) {
			var bytes = JsonSerializer.SerializeToUtf8Bytes(operations[node.CheckpointId], EditWire.JsonOptions);
			entries[node.OperationEntry] = bytes;
			node.OperationSha256 = EditWire.Sha256(bytes);
		}
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static byte[] RewriteAsInline(byte[] package, string lineageId, string familyId) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		var payloads = manifest.Payloads.ToDictionary(x => x.Sha256, x => entries[x.Entry], StringComparer.Ordinal);
		foreach (var node in manifest.Checkpoints) {
			var operation = JsonSerializer.Deserialize<EditCheckpointOperations>(entries[node.OperationEntry], EditWire.JsonOptions)!;
			foreach (var row in operation.Operations) {
				InlineForwardPayload(row, payloads);
				InlineInversePayload(row, payloads);
				row.PayloadSha256 = Array.Empty<string>();
			}
			var bytes = JsonSerializer.SerializeToUtf8Bytes(operation, EditWire.JsonOptions);
			entries[node.OperationEntry] = bytes;
			node.OperationSha256 = EditWire.Sha256(bytes);
		}
		manifest.Payloads.Clear();
		foreach (var name in entries.Keys.Where(x => x.StartsWith("payloads/", StringComparison.Ordinal)).ToArray()) entries.Remove(name);
		manifest.LineageId = lineageId; manifest.FamilyId = familyId;
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static void InlineForwardPayload(EditSerializedOperation operation, Dictionary<string, byte[]> payloads) {
		if (!ForwardPayloadKinds.Contains(operation.Kind, StringComparer.Ordinal)) return;
		if (!operation.Forward.TryGetValue("data_base64", out var value) || value is not JsonElement element || element.ValueKind != JsonValueKind.Object) return;
		operation.Forward["data_base64"] = Convert.ToBase64String(payloads[element.GetProperty("payload_sha256").GetString()!]);
	}

	static void InlineInversePayload(EditSerializedOperation operation, Dictionary<string, byte[]> payloads) {
		if (!operation.Inverse.TryGetValue("state", out var stateValue) || stateValue is not JsonElement state || state.ValueKind != JsonValueKind.Object) return;
		var stateObject = JsonSerializer.Deserialize<Dictionary<string, object?>>(state.GetRawText(), EditWire.JsonOptions)!;
		var changed = false;
		foreach (var shape in InversePayloadShapes) {
			if (!stateObject.TryGetValue(shape, out var shapeValue) || shapeValue is not JsonElement shapeElement || shapeElement.ValueKind != JsonValueKind.Object) continue;
			if (!shapeElement.TryGetProperty("data_base64", out var captured) || captured.ValueKind != JsonValueKind.Object) continue;
			var restored = JsonSerializer.Deserialize<Dictionary<string, object?>>(shapeElement.GetRawText(), EditWire.JsonOptions)!;
			restored["data_base64"] = Convert.ToBase64String(payloads[captured.GetProperty("payload_sha256").GetString()!]);
			stateObject[shape] = restored;
			changed = true;
		}
		if (changed) operation.Inverse["state"] = stateObject;
	}

	static Dictionary<string, byte[]> ReadEntries(byte[] package) {
		using var input = new MemoryStream(package, writable: false);
		using var archive = new ZipArchive(input, ZipArchiveMode.Read);
		return archive.Entries.ToDictionary(x => x.FullName, x => {
			using var stream = x.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
		}, StringComparer.Ordinal);
	}

	static byte[] BuildZip(Dictionary<string, byte[]> entries) {
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
			foreach (var row in entries) {
				var entry = archive.CreateEntry(row.Key, CompressionLevel.Optimal);
				using var stream = entry.Open(); stream.Write(row.Value, 0, row.Value.Length);
			}
		return output.ToArray();
	}

	static byte[]? ManagedBytes(ModuleDef module, string name) =>
		(module.Resources.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal)) as EmbeddedResource)?.CreateReader().ToArray();

	static byte[]? Win32Bytes(ModuleDef module, int typeId, int nameId, uint langId = 0) {
		var typeDirectory = module.Win32Resources.Root.FindDirectory(new dnlib.W32Resources.ResourceName(typeId));
		var nameDirectory = typeDirectory?.FindDirectory(new dnlib.W32Resources.ResourceName(nameId));
		var data = nameDirectory?.Data.FirstOrDefault(x => x.Name == new dnlib.W32Resources.ResourceName((int)langId));
		return data?.CreateReader().ToArray();
	}

	static byte[] Repeat256() { var bytes = new byte[4096]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i; return bytes; }
	static byte[] Reverse256() { var bytes = new byte[4096]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(255 - (i & 0xFF)); return bytes; }
	static byte[] ThirdPattern() { var bytes = new byte[4096]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 7 + 3) & 0xFF); return bytes; }

	static byte[] BuildResourceBlob() {
		using var stream = new MemoryStream();
		using (var writer = new System.Resources.ResourceWriter(stream)) {
			writer.AddResource("count", 42);
			writer.AddResource("text", "hello");
		}
		return stream.ToArray();
	}

	static string ManagedAdd(string name, byte[] data) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "managed_resource_add", ["name"] = name, ["data_base64"] = Convert.ToBase64String(data),
	}, EditWire.JsonOptions);

	static string ManagedUpdate(string name, byte[] data) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "managed_resource_update", ["target"] = new Dictionary<string, object?> { ["name"] = name },
		["data_base64"] = Convert.ToBase64String(data),
	}, EditWire.JsonOptions);

	static string ManagedRemove(string name) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "managed_resource_remove", ["target"] = new Dictionary<string, object?> { ["name"] = name },
		["remove_mode"] = "reject_if_referenced",
	}, EditWire.JsonOptions);

	static string Win32Add(int typeId, int nameId, byte[] data) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "win32_resource_add", ["type_id"] = typeId, ["name_id"] = nameId, ["data_base64"] = Convert.ToBase64String(data),
	}, EditWire.JsonOptions);

	static string Win32Update(int typeId, int nameId, byte[] data) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "win32_resource_update", ["type_id"] = typeId, ["name_id"] = nameId, ["data_base64"] = Convert.ToBase64String(data),
	}, EditWire.JsonOptions);

	static string Win32Remove(int typeId, int nameId) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "win32_resource_remove", ["type_id"] = typeId, ["name_id"] = nameId, ["remove_mode"] = "reject_if_referenced",
	}, EditWire.JsonOptions);

	static string MethodBodyReplace(uint token) => JsonSerializer.Serialize(new Dictionary<string, object?> {
		["kind"] = "method_body_replace",
		["target"] = new Dictionary<string, object?> { ["token"] = "0x" + token.ToString("x8") },
		["body"] = new Dictionary<string, object?> {
			["max_stack"] = 1, ["init_locals"] = false, ["locals"] = Array.Empty<object>(), ["exception_handlers"] = Array.Empty<object>(),
			["instructions"] = new object[] {
				new Dictionary<string, object?> { ["opcode"] = "ldc.i4.7", ["operand"] = null },
				new Dictionary<string, object?> { ["opcode"] = "ret", ["operand"] = null },
			},
		},
	}, EditWire.JsonOptions);

	static void Check(bool value, string name, string detail = "") {
		if (!value) throw new InvalidOperationException("FAILED: " + name + (detail.Length > 0 ? " " + detail : ""));
	}
}
