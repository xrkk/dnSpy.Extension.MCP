using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Debugger;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditSourceIdentity {
	[JsonPropertyName("format")] public string Format { get; set; } = "source_identity.v1";
	[JsonPropertyName("origin_mvid")] public string OriginMvid { get; set; } = string.Empty;
	[JsonPropertyName("baseline_image_sha256")] public string BaselineImageSha256 { get; set; } = string.Empty;
	[JsonPropertyName("baseline_semantic_fingerprint")] public string BaselineSemanticFingerprint { get; set; } = string.Empty;
	[JsonPropertyName("path_hash"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? PathHash { get; set; }
	[JsonPropertyName("on_disk_sha256"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? OnDiskSha256 { get; set; }
	[JsonPropertyName("volume_serial"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? VolumeSerial { get; set; }
	[JsonPropertyName("file_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? FileId { get; set; }
}

internal sealed class EditBaselineEntry {
	[JsonPropertyName("entry")] public string Entry { get; set; } = "baseline/module.bin";
	[JsonPropertyName("length")] public long Length { get; set; }
	[JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
}

internal sealed class EditCheckpointNode {
	[JsonPropertyName("checkpoint_id")] public string CheckpointId { get; set; } = string.Empty;
	[JsonPropertyName("parent_checkpoint_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ParentCheckpointId { get; set; }
	[JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
	[JsonPropertyName("operation_entry")] public string OperationEntry { get; set; } = string.Empty;
	[JsonPropertyName("operation_sha256")] public string OperationSha256 { get; set; } = string.Empty;
	[JsonPropertyName("producer")] public Dictionary<string, object?> Producer { get; set; } = new();
	[JsonPropertyName("review")] public Dictionary<string, object?> Review { get; set; } = new();
	[JsonPropertyName("confirmed_risks")] public string[] ConfirmedRisks { get; set; } = Array.Empty<string>();
	[JsonPropertyName("result_image_sha256")] public string ResultImageSha256 { get; set; } = string.Empty;
	[JsonPropertyName("result_semantic_fingerprint")] public string ResultSemanticFingerprint { get; set; } = string.Empty;
	[JsonPropertyName("sequence")] public int Sequence { get; set; }
}

internal sealed class EditPayloadEntry {
	[JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
	[JsonPropertyName("entry")] public string Entry { get; set; } = string.Empty;
	[JsonPropertyName("length")] public long Length { get; set; }
}

internal sealed class EditDefaultOutput {
	[JsonPropertyName("relative_path")] public string RelativePath { get; set; } = string.Empty;
	[JsonPropertyName("path_hash")] public string PathHash { get; set; } = string.Empty;
	[JsonPropertyName("image_sha256")] public string ImageSha256 { get; set; } = string.Empty;
}

internal sealed class EditCheckpointManifest {
	[JsonPropertyName("format")] public string Format { get; set; } = "dnspy.edit.checkpoints.v1";
	[JsonPropertyName("lineage_id")] public string LineageId { get; set; } = string.Empty;
	[JsonPropertyName("family_id")] public string FamilyId { get; set; } = string.Empty;
	[JsonPropertyName("superseded_lineage_id")] public string? SupersededLineageId { get; set; }
	[JsonPropertyName("source_identity")] public EditSourceIdentity SourceIdentity { get; set; } = new();
	[JsonPropertyName("baseline")] public EditBaselineEntry Baseline { get; set; } = new();
	[JsonPropertyName("head_checkpoint_id")] public string HeadCheckpointId { get; set; } = string.Empty;
	[JsonPropertyName("checkpoints")] public List<EditCheckpointNode> Checkpoints { get; set; } = new();
	[JsonPropertyName("payloads")] public List<EditPayloadEntry> Payloads { get; set; } = new();
	[JsonPropertyName("default_output")] public EditDefaultOutput DefaultOutput { get; set; } = new();
}

internal sealed class EditSerializedOperation {
	[JsonPropertyName("operation_id")] public string OperationId { get; set; } = string.Empty;
	[JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
	[JsonPropertyName("kind_version")] public int KindVersion { get; set; } = 1;
	[JsonPropertyName("forward")] public Dictionary<string, object?> Forward { get; set; } = new();
	[JsonPropertyName("inverse")] public Dictionary<string, object?> Inverse { get; set; } = new();
	[JsonPropertyName("payload_sha256")] public string[] PayloadSha256 { get; set; } = Array.Empty<string>();
}

internal sealed class EditCheckpointOperations {
	[JsonPropertyName("format")] public string Format { get; set; } = "dnspy.edit.op.v1";
	[JsonPropertyName("checkpoint_id")] public string CheckpointId { get; set; } = string.Empty;
	[JsonPropertyName("operations")] public List<EditSerializedOperation> Operations { get; set; } = new();
}

internal sealed class EditLoadedLineage {
	// ZIP entry timestamps are display facts, not part of the checkpoint identity.
	public Dictionary<string, DateTimeOffset> CheckpointTimes { get; init; } = new(StringComparer.Ordinal);
	public EditCheckpointManifest Manifest { get; init; } = new();
	public byte[] BaselineBytes { get; init; } = Array.Empty<byte>();
	public Dictionary<string, EditCheckpointOperations> Operations { get; init; } = new(StringComparer.Ordinal);
	public Dictionary<string, byte[]> PayloadBytes { get; init; } = new(StringComparer.Ordinal);
	public byte[] PackageBytes { get; init; } = Array.Empty<byte>();
	public string PackageSha256 { get; init; } = string.Empty;
	public EditCheckpointNode Head => Checkpoint(Manifest.HeadCheckpointId);
	public EditCheckpointNode Checkpoint(string id) => Manifest.Checkpoints.Single(x => x.CheckpointId == id);
}

internal sealed class EditHistoryBinding {
	public string FamilyId { get; init; } = string.Empty;
	public string? LineageId { get; init; }
	public string? BaseCheckpointId { get; init; }
	public string[] MatchBasis { get; init; } = Array.Empty<string>();
	public bool IsNewFamily => LineageId == null;
}

internal sealed class EditPreparedHistoryWrite {
	public EditLoadedLineage Lineage { get; init; } = new();
	public EditOwnedTemp Temp { get; init; } = new();
	public bool ReplacesExisting { get; init; }
	public string PreHeadCheckpointId { get; init; } = string.Empty;
	public string PostHeadCheckpointId { get; init; } = string.Empty;
	public string OperationKind { get; init; } = string.Empty;
}

internal sealed class EditStagedCleanupException : Exception {
	public EditPreparedHistoryWrite Prepared { get; }
	public string OriginalFailure { get; }
	public EditStagedCleanupException(EditPreparedHistoryWrite prepared, Exception original, Exception cleanup)
		: base("A validated history write could not be prepared and its owned temporary package could not be removed", cleanup) {
		Prepared = prepared;
		OriginalFailure = original is EditDomainException domain ? domain.Code : original.GetType().Name;
	}
}

internal sealed class EditReplayAssessment {
	public string ReplayId { get; init; } = string.Empty;
	public string Classification { get; init; } = string.Empty;
	public EditLoadedLineage Lineage { get; init; } = new();
	public EditCheckpointNode Checkpoint { get; init; } = new();
	public byte[] Bytes { get; init; } = Array.Empty<byte>();
	public string ImageSha256 { get; init; } = string.Empty;
	public string SemanticFingerprint { get; init; } = string.Empty;
	public string PackageSha256 { get; init; } = string.Empty;
	public string LiveFingerprint { get; init; } = string.Empty;
	public string HeadCheckpointId { get; init; } = string.Empty;
}

/// <summary>
/// Deep checkpoint/history module.  The coordinator supplies intent and owns the process state;
/// this module owns package validation, lineage resolution, replay, bounded ZIP construction,
/// branch planning and atomic store operations.
/// </summary>
internal sealed class EditHistoryModule : IDisposable {
	const int MaxZipEntries = 4096;
	const int MaxLineages = 128;
	readonly Func<McpSettingsSnapshot?> snapshot;
	readonly bool injectedStore;
	readonly JsonElement manifestSchema;
	readonly JsonElement operationSchema;
	readonly Dictionary<ModuleDef, EditHistoryBinding> processBindings = new();
	readonly Dictionary<string, EditReplayAssessment> replayTickets = new(StringComparer.Ordinal);
	IEditCheckpointStore? store;
	string? storeRoot;
	bool disposed;

	public EditHistoryModule(Func<McpSettingsSnapshot?> snapshot, JsonElement checkpointContract) {
		this.snapshot = snapshot;
		manifestSchema = checkpointContract.GetProperty("manifestSchema").Clone();
		operationSchema = checkpointContract.GetProperty("operationSchema").Clone();
	}

	internal EditHistoryModule(IEditCheckpointStore testStore, JsonElement checkpointContract) {
		injectedStore = true;
		snapshot = () => null;
		manifestSchema = checkpointContract.GetProperty("manifestSchema").Clone();
		operationSchema = checkpointContract.GetProperty("operationSchema").Clone();
		store = testStore;
		storeRoot = testStore.ArtifactRoot;
	}

	internal EditLoadedLineage ValidatePackageForTesting(byte[] package) => ParsePackage(package);

	public EditHistoryBinding ResolveBegin(EditWorkspace workspace, string? explicitFamilyId) {
		ThrowIfDisposed();
		if (processBindings.TryGetValue(workspace.LiveModule, out var bound)) {
			if (explicitFamilyId != null && explicitFamilyId != bound.FamilyId) throw SourceConflict(Array.Empty<object>());
			if (bound.LineageId != null) RequireLiveAtHead(workspace, Load(bound.LineageId));
			return bound;
		}
		var lineages = LoadAll();
		var active = ActiveLeaves(lineages).ToArray();
		EditSourceFileObservation? source;
		try { source = EditSourceFileIdentity.Observe(workspace.FilePath); }
		catch (Exception ex) { throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
			new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "source_identity", ["reason"] = ex.Message }); }
		var liveSemantic = workspace.CurrentLiveSemanticFingerprint();
		var liveImage = workspace.CurrentLiveImageSha256();
		var candidates = new List<(EditLoadedLineage Lineage, string[] Basis, EditReplayAssessment Replay)>();
		var aliasConflicts = new List<object>();
		foreach (var lineage in active) {
			if (explicitFamilyId != null && lineage.Manifest.FamilyId != explicitFamilyId) continue;
			var identity = lineage.Manifest.SourceIdentity;
			var pathAlias = source != null && (identity.PathHash == source.PathHash || lineage.Manifest.DefaultOutput.PathHash == source.PathHash);
			var originAlias = source != null && identity.PathHash == source.PathHash && identity.VolumeSerial == source.VolumeSerial
				&& identity.FileId == source.FileId && identity.OnDiskSha256 == source.Sha256;
			var outputAlias = source != null && lineage.Manifest.DefaultOutput.PathHash == source.PathHash
				&& lineage.Manifest.DefaultOutput.ImageSha256 == source.Sha256;
			if (!string.Equals(identity.OriginMvid, workspace.ModuleMvid, StringComparison.OrdinalIgnoreCase)) {
				if (pathAlias) aliasConflicts.Add(CandidateSummary(lineage, new[] { "path_alias_replaced" }));
				continue;
			}
			var replay = Replay(lineage, lineage.Manifest.HeadCheckpointId, workspace.CurrentLiveFingerprint());
			var contentExact = replay.Classification == "exact" && replay.SemanticFingerprint == liveSemantic && replay.ImageSha256 == liveImage;
			var basis = new List<string>();
			if (contentExact) basis.Add("exact_head_content");
			if (originAlias) basis.Add("origin_file_identity");
			if (outputAlias) basis.Add("default_output_alias");
			if (basis.Count != 0) candidates.Add((lineage, basis.ToArray(), replay));
			else if (pathAlias) aliasConflicts.Add(CandidateSummary(lineage, new[] { "path_alias_replaced" }));
		}
		if (candidates.Count == 1) {
			var candidate = candidates[0];
			if (candidate.Replay.Classification != "exact" || candidate.Replay.SemanticFingerprint != liveSemantic || candidate.Replay.ImageSha256 != liveImage)
				throw new EditDomainException("EDIT_LINEAGE_DIVERGED", new Dictionary<string, object?> {
					["kind"] = "lineage_diverged", ["family_id"] = candidate.Lineage.Manifest.FamilyId,
					["lineage_id"] = candidate.Lineage.Manifest.LineageId, ["match_basis"] = candidate.Basis,
				});
			bound = new EditHistoryBinding { FamilyId = candidate.Lineage.Manifest.FamilyId, LineageId = candidate.Lineage.Manifest.LineageId,
				BaseCheckpointId = candidate.Lineage.Manifest.HeadCheckpointId, MatchBasis = candidate.Basis };
			processBindings[workspace.LiveModule] = bound;
			return bound;
		}
		if (candidates.Count > 1 || aliasConflicts.Count != 0 || explicitFamilyId != null)
			throw SourceConflict(candidates.Select(x => CandidateSummary(x.Lineage, x.Basis)).Concat(aliasConflicts).ToArray());
		bound = new EditHistoryBinding { FamilyId = EditWire.NewId("family"), MatchBasis = new[] { "new_source" } };
		processBindings[workspace.LiveModule] = bound;
		return bound;
	}

	public EditPreparedHistoryWrite PrepareCommit(EditWorkspace workspace, EditHistoryBinding binding,
		IReadOnlyList<string> normalizedOperations, string reviewId, uint reviewRevision,
		IReadOnlyList<string> confirmedRisks, string operationKind = "commit") {
		workspace.ValidateRoundtrip();
		var targetBytes = EditWorkspace.WriteCheckpointImage(workspace.PrivateModule);
		var targetImage = EditWire.Sha256(targetBytes);
		var targetSemantic = EditFingerprint.ComputeRoundtrip(workspace.PrivateModule);
		EditLoadedLineage next;
		string preHead;
		var replace = binding.LineageId != null;
		if (!replace) {
			var currentLineages = LoadAll().Count;
			if (currentLineages >= MaxLineages) throw Capacity("lineages", currentLineages + 1L, MaxLineages);
			var lineageId = EditWire.NewId("lineage");
			var rootId = EditWire.NewId("checkpoint");
			var rootOps = EmptyOperations(rootId);
			var rootBytes = JsonSerializer.SerializeToUtf8Bytes(rootOps, EditWire.JsonOptions);
			var root = Node(rootId, null, "baseline", rootBytes, workspace.BaselineImageSha256,
				workspace.BaselineSemanticFingerprint, 0, reviewId, reviewRevision, Array.Empty<string>());
			var manifest = new EditCheckpointManifest {
				LineageId = lineageId, FamilyId = binding.FamilyId,
				SourceIdentity = SourceIdentity(workspace),
				Baseline = new EditBaselineEntry { Length = workspace.BaselineBytes.LongLength, Sha256 = workspace.BaselineImageSha256 },
				HeadCheckpointId = rootId, Checkpoints = new List<EditCheckpointNode> { root },
				DefaultOutput = CreateDefaultOutput(lineageId, workspace.FilePath, workspace.BaselineImageSha256),
			};
			next = new EditLoadedLineage {
				Manifest = manifest, BaselineBytes = workspace.BaselineBytes,
				Operations = new Dictionary<string, EditCheckpointOperations>(StringComparer.Ordinal) { [rootId] = rootOps },
			};
			preHead = rootId;
		}
		else {
			next = Clone(Load(binding.LineageId!));
			preHead = next.Manifest.HeadCheckpointId;
			if (binding.BaseCheckpointId != preHead) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		}
		var checkpointId = EditWire.NewId("checkpoint");
		var operations = SerializeOperations(next, checkpointId, preHead, normalizedOperations, out var replayModule);
		// P04: the recorded head identity comes from the deterministic replay graph,
		// not the private copy — reference rows created by prefix operations (e.g.
		// attribute ctors) have different provenance in a private module loaded
		// from live bytes, which made the staged exact gate and every later
		// begin/replay compare images across provenance classes. The replay graph
		// is the single canonical image source; semantics still cross-check
		// against the validated private module below.
		string replayImage; string replaySemantic;
		try {
			replayImage = EditWire.Sha256(EditWorkspace.WriteCheckpointImage(replayModule));
			replaySemantic = EditFingerprint.ComputeRoundtrip(replayModule);
			if (replaySemantic != targetSemantic) throw new EditDomainException("EDIT_VALIDATION_FAILED",
				EditWorkspace.ValidationDetails("replay_private_semantic", operationKind, EditFingerprint.Difference(replayModule, workspace.PrivateModule)));
		}
		finally { replayModule.Dispose(); }
		targetImage = replayImage; targetSemantic = replaySemantic;
		var operationBytes = JsonSerializer.SerializeToUtf8Bytes(operations, EditWire.JsonOptions);
		var node = Node(checkpointId, preHead, operationKind, operationBytes, targetImage, targetSemantic,
			next.Manifest.Checkpoints.Count, reviewId, reviewRevision, confirmedRisks);
		next.Manifest.Checkpoints.Add(node);
		next.Manifest.HeadCheckpointId = checkpointId;
		next.Manifest.DefaultOutput = CreateDefaultOutput(next.Manifest.LineageId, workspace.FilePath, targetImage);
		next.Operations[checkpointId] = operations;
		return StageValidated(next, BuildPackage(next), replace, preHead, checkpointId, operationKind);
	}

	/// <summary>Creates the one permitted history object for a legacy save of an unchanged source:
	/// a baseline/root only.  It does not invent an empty semantic commit.</summary>
	public EditPreparedHistoryWrite PrepareInitialBaseline(EditWorkspace workspace, EditHistoryBinding binding) {
		if (!binding.IsNewFamily) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var currentLineages = LoadAll().Count;
		if (currentLineages >= MaxLineages) throw Capacity("lineages", currentLineages + 1L, MaxLineages);
		var lineageId = EditWire.NewId("lineage");
		var rootId = EditWire.NewId("checkpoint");
		var rootOps = EmptyOperations(rootId);
		var rootBytes = JsonSerializer.SerializeToUtf8Bytes(rootOps, EditWire.JsonOptions);
		var root = Node(rootId, null, "baseline", rootBytes, workspace.BaselineImageSha256,
			workspace.BaselineSemanticFingerprint, 0, "legacy-save-baseline", 0, Array.Empty<string>());
		var manifest = new EditCheckpointManifest {
			LineageId = lineageId, FamilyId = binding.FamilyId,
			SourceIdentity = SourceIdentity(workspace),
			Baseline = new EditBaselineEntry { Length = workspace.BaselineBytes.LongLength, Sha256 = workspace.BaselineImageSha256 },
			HeadCheckpointId = rootId, Checkpoints = new List<EditCheckpointNode> { root },
			DefaultOutput = CreateDefaultOutput(lineageId, workspace.FilePath, workspace.BaselineImageSha256),
		};
		var lineage = new EditLoadedLineage {
			Manifest = manifest, BaselineBytes = workspace.BaselineBytes,
			Operations = new Dictionary<string, EditCheckpointOperations>(StringComparer.Ordinal) { [rootId] = rootOps },
		};
		return StageValidated(lineage, BuildPackage(lineage), false, rootId, rootId, "legacy_save_assembly");
	}

	/// <summary>Proves that the mutable head is exactly one direct compatibility IL edit for
	/// <paramref name="methodToken"/>.  Legacy revert may only navigate to this head's parent.</summary>
	public EditCheckpointNode RequireLegacyMethodHead(string lineageId, string expectedHead, uint methodToken) {
		var lineage = Load(lineageId);
		if (!string.Equals(lineage.Manifest.HeadCheckpointId, expectedHead, StringComparison.Ordinal))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var head = lineage.Head;
		if (head.ParentCheckpointId == null || head.Kind is not ("legacy_patch_method_il" or "legacy_force_return" or "legacy_nop_method")
			|| !lineage.Operations.TryGetValue(head.CheckpointId, out var entry) || entry.Operations.Count != 1
			|| entry.Operations[0].Kind != "method_body_replace")
			throw new ArgumentException("No pending compatible method patch exists at the current history head");
		using var document = JsonDocument.Parse(JsonSerializer.Serialize(entry.Operations[0].Forward, EditWire.JsonOptions));
		var tokenText = document.RootElement.TryGetProperty("target", out var target)
			&& target.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
			? tokenElement.GetString() : null;
		if (tokenText == null || tokenText.Length != 10 || !tokenText.StartsWith("0x", StringComparison.Ordinal)
			|| !uint.TryParse(tokenText.Substring(2), System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out var recorded)
			|| recorded != methodToken)
			throw new ArgumentException("No pending compatible method patch exists for the requested method");
		return head;
	}

	public bool IsLegacyMethodHead(string lineageId, string expectedHead, uint methodToken) {
		try { RequireLegacyMethodHead(lineageId, expectedHead, methodToken); return true; }
		catch (ArgumentException) { return false; }
		catch (EditDomainException) { return false; }
	}

	public void Finalize(EditPreparedHistoryWrite prepared, ModuleDef liveModule) {
		Store.FinalizeTemp(prepared.Temp, prepared.ReplacesExisting);
		processBindings[liveModule] = new EditHistoryBinding {
			FamilyId = prepared.Lineage.Manifest.FamilyId, LineageId = prepared.Lineage.Manifest.LineageId,
			BaseCheckpointId = prepared.PostHeadCheckpointId, MatchBasis = new[] { "process_binding" },
		};
	}

	public EditPreparedHistoryWrite PrepareHeadMove(string lineageId, string expectedHead, string targetCheckpointId, string operationKind) {
		var next = Clone(Load(lineageId));
		if (next.Manifest.HeadCheckpointId != expectedHead || !next.Manifest.Checkpoints.Any(x => x.CheckpointId == targetCheckpointId))
			throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		next.Manifest.HeadCheckpointId = targetCheckpointId;
		next.Manifest.DefaultOutput.ImageSha256 = next.Checkpoint(targetCheckpointId).ResultImageSha256;
		return StageValidated(next, BuildPackage(next), true, expectedHead, targetCheckpointId, operationKind);
	}

	public EditPreparedHistoryWrite PrepareMigration(EditReplayAssessment target, byte[] currentImage, string reviewId) {
		var next = Clone(Load(target.Lineage.Manifest.LineageId));
		if (next.Manifest.HeadCheckpointId != target.HeadCheckpointId) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var preHead = next.Manifest.HeadCheckpointId;
		var checkpointId = EditWire.NewId("checkpoint"); var entry = EmptyOperations(checkpointId);
		var entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, EditWire.JsonOptions);
		var semantic = target.SemanticFingerprint; var image = EditWire.Sha256(currentImage);
		next.Manifest.Checkpoints.Add(Node(checkpointId, target.Checkpoint.CheckpointId, "migration", entryBytes,
			image, semantic, next.Manifest.Checkpoints.Count, reviewId, 0, Array.Empty<string>()));
		next.Operations[checkpointId] = entry; next.Manifest.HeadCheckpointId = checkpointId;
		next.Manifest.DefaultOutput.ImageSha256 = image;
		return StageValidated(next, BuildPackage(next), true, preHead, checkpointId, "migration");
	}

	public EditPreparedHistoryWrite PrepareAcceptedBaseline(EditWorkspace workspace, string familyId, string supersededLineageId) {
		var old = Load(supersededLineageId);
		if (old.Manifest.FamilyId != familyId) throw new EditDomainException("EDIT_SOURCE_IDENTITY_CONFLICT");
		var currentLineages = LoadAll().Count;
		if (currentLineages >= MaxLineages) throw Capacity("lineages", currentLineages + 1L, MaxLineages);
		var lineageId = EditWire.NewId("lineage"); var rootId = EditWire.NewId("checkpoint");
		var rootOps = EmptyOperations(rootId); var rootOpsBytes = JsonSerializer.SerializeToUtf8Bytes(rootOps, EditWire.JsonOptions);
		var bytes = workspace.BaselineBytes; var image = EditWire.Sha256(bytes); var semantic = workspace.BaselineSemanticFingerprint;
		var root = Node(rootId, null, "accepted_baseline", rootOpsBytes, image, semantic, 0, "accepted-live", 0, Array.Empty<string>());
		var manifest = new EditCheckpointManifest {
			LineageId = lineageId, FamilyId = familyId, SupersededLineageId = supersededLineageId,
			SourceIdentity = SourceIdentity(workspace), Baseline = new EditBaselineEntry { Length = bytes.LongLength, Sha256 = image },
			HeadCheckpointId = rootId, Checkpoints = new List<EditCheckpointNode> { root },
			DefaultOutput = CreateDefaultOutput(lineageId, workspace.FilePath, image),
		};
		var next = new EditLoadedLineage { Manifest = manifest, BaselineBytes = bytes,
			Operations = new Dictionary<string, EditCheckpointOperations>(StringComparer.Ordinal) { [rootId] = rootOps } };
		return StageValidated(next, BuildPackage(next), false, old.Manifest.HeadCheckpointId, rootId, "accept_live");
	}

	EditPreparedHistoryWrite StageValidated(EditLoadedLineage candidate, byte[] package, bool replacesExisting,
		string preHeadCheckpointId, string postHeadCheckpointId, string operationKind) {
		var staged = Store.CreateTemp(candidate.Manifest.LineageId, package);
		var provisional = new EditPreparedHistoryWrite {
			Lineage = candidate, Temp = staged, ReplacesExisting = replacesExisting,
			PreHeadCheckpointId = preHeadCheckpointId, PostHeadCheckpointId = postHeadCheckpointId,
			OperationKind = operationKind,
		};
		try {
			var persisted = Store.ReadTemp(staged);
			var verified = ParsePackage(persisted);
			if (verified.Manifest.HeadCheckpointId != postHeadCheckpointId
				|| verified.PackageSha256 != staged.Sha256
				|| verified.PackageSha256 != EditWire.Sha256(package))
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			var stagedReplay = Replay(verified, postHeadCheckpointId, verified.Head.ResultSemanticFingerprint);
			if (stagedReplay.Classification != "exact")
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID", new Dictionary<string, object?> { ["kind"] = "ck_invalid_2",
					["classification"] = stagedReplay.Classification, ["replayed_semantic"] = stagedReplay.SemanticFingerprint,
					["recorded_semantic"] = verified.Head.ResultSemanticFingerprint,
					["replayed_image"] = stagedReplay.ImageSha256, ["recorded_image"] = verified.Head.ResultImageSha256 });
			return new EditPreparedHistoryWrite {
				Lineage = verified, Temp = staged, ReplacesExisting = replacesExisting,
				PreHeadCheckpointId = preHeadCheckpointId, PostHeadCheckpointId = postHeadCheckpointId,
				OperationKind = operationKind,
			};
		}
		catch (Exception original) {
			try { Store.DeleteTemp(staged); }
			catch (Exception cleanup) { throw new EditStagedCleanupException(provisional, original, cleanup); }
			throw;
		}
	}

	public EditLoadedLineage ValidateAcceptLive(EditWorkspace workspace, string familyId, string supersededLineageId) {
		var active = ActiveLeaves(LoadAll()).Where(x => x.Manifest.FamilyId == familyId).ToArray();
		if (active.Length != 1 || active[0].Manifest.LineageId != supersededLineageId
			|| !string.Equals(active[0].Manifest.SourceIdentity.OriginMvid, workspace.ModuleMvid, StringComparison.OrdinalIgnoreCase))
			throw SourceConflict(active.Select(LineageSummary).ToArray());
		var processMatch = processBindings.TryGetValue(workspace.LiveModule, out var binding)
			&& binding.FamilyId == familyId && binding.LineageId == supersededLineageId;
		var source = EditSourceFileIdentity.Observe(workspace.FilePath);
		var identity = active[0].Manifest.SourceIdentity;
		var fileMatch = source != null && identity.PathHash == source.PathHash && identity.VolumeSerial == source.VolumeSerial
			&& identity.FileId == source.FileId && identity.OnDiskSha256 == source.Sha256;
		if (!processMatch && !fileMatch) throw SourceConflict(new object[] { CandidateSummary(active[0], new[] { "identity_not_current" }) });
		var replay = Replay(active[0], active[0].Manifest.HeadCheckpointId, workspace.BaselineLiveFingerprint);
		if (replay.Classification == "exact" && replay.SemanticFingerprint == workspace.BaselineSemanticFingerprint
			&& replay.ImageSha256 == workspace.CurrentLiveImageSha256())
			throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> { ["kind"] = "not_diverged" });
		return active[0];
	}

	public void DeleteOwnedTemp(EditPreparedHistoryWrite prepared) => Store.DeleteTemp(prepared.Temp);
	public bool TempMatches(EditPreparedHistoryWrite prepared) => Store.Matches(prepared.Temp);
	public bool PreHeadUnchanged(EditPreparedHistoryWrite prepared) {
		if (!prepared.ReplacesExisting) return !Store.FinalExists(prepared.Lineage.Manifest.LineageId);
		try { return Load(prepared.Lineage.Manifest.LineageId).Manifest.HeadCheckpointId == prepared.PreHeadCheckpointId; }
		catch { return false; }
	}

	public EditReplayAssessment Assess(string lineageId, string checkpointId, string liveFingerprint) {
		var result = Replay(Load(lineageId), checkpointId, liveFingerprint);
		replayTickets[result.ReplayId] = result;
		while (replayTickets.Count > 128) replayTickets.Remove(replayTickets.Keys.First());
		return result;
	}

	public EditReplayAssessment RequireTicket(string replayId, string lineageId, string checkpointId,
		string liveFingerprint, string expectedHead) {
		if (!replayTickets.TryGetValue(replayId, out var ticket)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (ticket.Lineage.Manifest.LineageId != lineageId || ticket.Checkpoint.CheckpointId != checkpointId
			|| ticket.LiveFingerprint != liveFingerprint || ticket.HeadCheckpointId != expectedHead
			|| Load(lineageId).PackageSha256 != ticket.PackageSha256) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return ticket;
	}

	public EditLoadedLineage Load(string lineageId) {
		if (!EditHistoryIds.Is(lineageId, "lineage")) throw new ArgumentException("invalid lineage_id", "lineage_id");
		return ParsePackage(Store.ReadFinal(lineageId));
	}

	public IReadOnlyList<EditLoadedLineage> LoadAll() {
		var objects = Store.EnumerateCheckpointObjects();
		var finals = objects.Where(x => x.IsTrustedFinal).ToArray();
		if (finals.Length > MaxLineages) throw Capacity("lineages", finals.Length, MaxLineages);
		long bytes = 0; foreach (var row in objects) bytes = CheckedAdd(bytes, row.Length, ArtifactStoreLedger.MaxStoreBytes, "store_bytes");
		if (objects.Count > ArtifactStoreLedger.MaxStoreChildren) throw Capacity("store_children", objects.Count, ArtifactStoreLedger.MaxStoreChildren);
		var result = new List<EditLoadedLineage>();
		foreach (var row in finals.OrderBy(x => x.Name, StringComparer.Ordinal)) {
			var id = row.Name.Substring(0, row.Name.Length - ".dnspy-mcp-checkpoints".Length);
			result.Add(Load(id));
		}
		return result;
	}

	public Dictionary<string, object?> HistoryView(string? lineageId, string? checkpointId, int offset, int pageSize) {
		if (checkpointId != null) {
			EditLoadedLineage lineage;
			if (lineageId != null) lineage = Load(lineageId);
			else {
				var matches = LoadAll().Where(x => x.Manifest.Checkpoints.Any(c => c.CheckpointId == checkpointId)).ToArray();
				if (matches.Length != 1) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				lineage = matches[0];
			}
			var node = lineage.Manifest.Checkpoints.SingleOrDefault(x => x.CheckpointId == checkpointId)
				?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			return new Dictionary<string, object?> { ["view"] = "checkpoint", ["checkpoint"] = CheckpointSummary(lineage, node), ["next_cursor"] = null };
		}
		if (lineageId != null) {
			var lineage = Load(lineageId); var all = lineage.Manifest.Checkpoints.OrderBy(x => x.Sequence).ToArray();
			var page = all.Skip(offset).Take(pageSize).Select(x => CheckpointSummary(lineage, x)).ToArray();
			return new Dictionary<string, object?> { ["view"] = "checkpoints", ["checkpoints"] = page,
				["next_cursor"] = offset + page.Length < all.Length ? EncodeCursor(offset + page.Length) : null };
		}
		var lineages = LoadAll().OrderBy(x => x.Manifest.Checkpoints.Min(c => c.Sequence)).ThenBy(x => x.Manifest.LineageId, StringComparer.Ordinal).ToArray();
		var rows = lineages.Skip(offset).Take(pageSize).Select(LineageSummary).ToArray();
		return new Dictionary<string, object?> { ["view"] = "lineages", ["lineages"] = rows,
			["next_cursor"] = offset + rows.Length < lineages.Length ? EncodeCursor(offset + rows.Length) : null };
	}

	public EditOutputResult Export(EditReplayAssessment replay, string? requestedPath, string sourcePath) {
		if (replay.Classification != "exact") throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		using var module = ModuleDefMD.Load(replay.Bytes);
		EditStructuralValidator.Validate(module);
		var readback = EditWorkspace.WriteCheckpointImage(module);
		using var reloaded = ModuleDefMD.Load(readback);
		EditStructuralValidator.Validate(reloaded);
		if (EditWire.Sha256(readback) != replay.ImageSha256 || EditFingerprint.ComputeRoundtrip(reloaded) != replay.SemanticFingerprint)
			throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		return WriteValidatedOutput(replay.Bytes, requestedPath ?? replay.Lineage.Manifest.DefaultOutput.RelativePath, sourcePath);
	}

	public EditOutputResult ExportResource(byte[] bytes, string path, string sourcePath) {
		if (bytes.LongLength > EditWire.MaxResourceBytes)
			throw Capacity("resource_bytes", bytes.LongLength, EditWire.MaxResourceBytes);
		return WriteValidatedOutput(bytes, path, sourcePath);
	}

	EditOutputResult WriteValidatedOutput(byte[] bytes, string path, string sourcePath) {
		if (!string.IsNullOrEmpty(sourcePath) && Path.IsPathRooted(path)
			&& string.Equals(Path.GetFullPath(path), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
			throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		var absolute = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Store.ArtifactRoot, path));
		var exists = File.Exists(absolute);
		if (exists && !string.IsNullOrEmpty(sourcePath)) {
			var sourceIdentity = EditSourceFileIdentity.Observe(sourcePath);
			var outputIdentity = EditSourceFileIdentity.Observe(absolute);
			if (sourceIdentity != null && outputIdentity != null && sourceIdentity.VolumeSerial == outputIdentity.VolumeSerial
				&& sourceIdentity.FileId == outputIdentity.FileId) throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		}
		return Store.WriteOutputAtomic(path, bytes, exists);
	}

	public void BindHead(ModuleDef module, EditLoadedLineage lineage) => processBindings[module] = new EditHistoryBinding {
		FamilyId = lineage.Manifest.FamilyId, LineageId = lineage.Manifest.LineageId, BaseCheckpointId = lineage.Manifest.HeadCheckpointId,
		MatchBasis = new[] { "process_binding" },
	};

	public Dictionary<string, object?> CapacityView() {
		var rows = Store.EnumerateCheckpointObjects(); long bytes = 0; foreach (var row in rows) bytes = checked(bytes + row.Length);
		return new Dictionary<string, object?> {
			["store_bytes"] = Meter(bytes, ArtifactStoreLedger.MaxStoreBytes), ["store_children"] = Meter(rows.Count, ArtifactStoreLedger.MaxStoreChildren),
			["lineages"] = Meter(rows.Count(x => x.IsTrustedFinal), MaxLineages), ["residuals"] = Meter(rows.Count(x => x.IsResidual), ArtifactStoreLedger.MaxStoreChildren),
		};
	}

	EditReplayAssessment Replay(EditLoadedLineage lineage, string checkpointId, string liveFingerprint) {
		var checkpoint = lineage.Manifest.Checkpoints.SingleOrDefault(x => x.CheckpointId == checkpointId)
			?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var path = PathTo(lineage, checkpointId);
		using var module = ModuleDefMD.Load(lineage.BaselineBytes);
		foreach (var node in path.Skip(1)) {
			if (!lineage.Operations.TryGetValue(node.CheckpointId, out var entry)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
			for (var i = 0; i < entry.Operations.Count; i++) {
				var row = entry.Operations[i];
				if (row.KindVersion != 1 || row.Forward.TryGetValue("kind", out var rawKind) == false
					|| !string.Equals(rawKind?.ToString(), row.Kind, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
				using var json = ExpandedForward(lineage, row);
				EditOperationRegistry.ApplyPersisted(module, json.RootElement, map, i);
			}
			EditStructuralValidator.Validate(module);
		}
		var bytes = EditWorkspace.WriteCheckpointImage(module);
		using var reloaded = ModuleDefMD.Load(bytes);
		EditStructuralValidator.Validate(reloaded);
		if (EditFingerprint.ComputeRoundtrip(module) != EditFingerprint.ComputeRoundtrip(reloaded))
			throw new EditDomainException("EDIT_VALIDATION_FAILED");
		var image = EditWire.Sha256(bytes); var semantic = EditFingerprint.ComputeRoundtrip(reloaded);
		var classification = image == checkpoint.ResultImageSha256 && semantic == checkpoint.ResultSemanticFingerprint
			? "exact" : semantic == checkpoint.ResultSemanticFingerprint ? "validated_drift" : "unverified_drift";
		return new EditReplayAssessment {
			ReplayId = EditWire.NewId("replay"), Classification = classification, Lineage = lineage, Checkpoint = checkpoint,
			Bytes = bytes, ImageSha256 = image, SemanticFingerprint = semantic, PackageSha256 = lineage.PackageSha256,
			LiveFingerprint = liveFingerprint, HeadCheckpointId = lineage.Manifest.HeadCheckpointId,
		};
	}

	static List<EditCheckpointNode> PathTo(EditLoadedLineage lineage, string checkpointId) {
		var byId = lineage.Manifest.Checkpoints.ToDictionary(x => x.CheckpointId, StringComparer.Ordinal);
		var path = new List<EditCheckpointNode>(); var seen = new HashSet<string>(StringComparer.Ordinal); var current = checkpointId;
		while (true) {
			if (!seen.Add(current) || !byId.TryGetValue(current, out var node)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			path.Add(node); if (node.ParentCheckpointId == null) break; current = node.ParentCheckpointId;
		}
		path.Reverse(); return path;
	}

	public EditHistoryNavigationPlan PlanNavigation(EditLoadedLineage lineage, string fromId, string targetId) {
		var fromPath = PathTo(lineage, fromId);
		var toPath = PathTo(lineage, targetId);
		var shared = 0;
		while (shared < fromPath.Count && shared < toPath.Count && fromPath[shared].CheckpointId == toPath[shared].CheckpointId) shared++;
		if (shared == 0) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var undoByCheckpoint = new Dictionary<string, List<EditHistoryNavigationPlan.Step>>(StringComparer.Ordinal);
		using var replay = ModuleDefMD.Load(lineage.BaselineBytes);
		foreach (var node in fromPath.Skip(1)) {
			var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
			var inverses = new List<EditHistoryNavigationPlan.Step>();
			var operations = lineage.Operations[node.CheckpointId].Operations;
			for (var index = 0; index < operations.Count; index++) {
				using var forward = ExpandedForward(lineage, operations[index]);
				if (node.Sequence >= fromPath[shared - 1].Sequence && node.CheckpointId != fromPath[shared - 1].CheckpointId) {
					// Consume the persisted compiled inverse; there is no silent
					// recompile fallback (P03-CHANGE-001 single representation).
					var envelope = operations[index].Inverse;
					if (!envelope.TryGetValue("state", out var persisted) || persisted is not JsonElement stateElement
						|| stateElement.ValueKind != JsonValueKind.Object)
						throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
					inverses.Add(new EditHistoryNavigationPlan.Step { CheckpointId = node.CheckpointId, Index = index,
						IsInverse = true, Operation = stateElement.GetRawText() });
				}
				EditOperationRegistry.ApplyPersisted(replay, forward.RootElement, map, index);
			}
			undoByCheckpoint[node.CheckpointId] = inverses;
		}
		var beforeFingerprint = EditFingerprint.ComputeRoundtrip(replay);
		var steps = fromPath.Skip(shared).Reverse().SelectMany(x => undoByCheckpoint[x.CheckpointId].AsEnumerable().Reverse()).ToList();
		foreach (var node in toPath.Skip(shared)) {
			var operations = lineage.Operations[node.CheckpointId].Operations;
			for (var index = 0; index < operations.Count; index++) {
				using var forward = ExpandedForward(lineage, operations[index]);
				steps.Add(new EditHistoryNavigationPlan.Step { CheckpointId = node.CheckpointId, Index = index,
					IsInverse = false, Operation = forward.RootElement.GetRawText() });
			}
		}
		var target = Replay(lineage, targetId, beforeFingerprint);
		var plan = new EditHistoryNavigationPlan(steps, beforeFingerprint, target.SemanticFingerprint);
		plan.Apply(replay);
		if (EditWire.Sha256(EditWorkspace.WriteCheckpointImage(replay)) != target.ImageSha256) throw new EditDomainException("EDIT_VALIDATION_FAILED");
		return plan;
	}

	EditLoadedLineage ParsePackage(byte[] package) {
		if (package.LongLength > ArtifactStoreLedger.MaxFileBytes) throw Capacity("package_file_bytes", package.LongLength, ArtifactStoreLedger.MaxFileBytes);
		var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		var entryTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
		try {
			using var stream = new MemoryStream(package, writable: false);
			using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
			if (zip.Entries.Count > MaxZipEntries) throw Capacity("zip_entries", zip.Entries.Count, MaxZipEntries);
			long expanded = 0;
			foreach (var entry in zip.Entries) {
				ValidateEntryName(entry.FullName);
				if (entries.Keys.Any(x => string.Equals(x, entry.FullName, StringComparison.OrdinalIgnoreCase))) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
				expanded = CheckedAdd(expanded, entry.Length, ArtifactStoreLedger.MaxSessionBytes, "expanded_bytes");
				var limit = entry.FullName == "manifest.json" || entry.FullName.StartsWith("operations/", StringComparison.Ordinal)
					? EditWire.MaxNormalizedOperationBytes : entry.FullName == "baseline/module.bin" ? EditWire.MaxModuleBytes : EditWire.MaxResourceBytes;
				if (entry.Length > limit) throw Capacity("zip_entry_bytes", entry.Length, limit);
				using var source = entry.Open();
				var bytes = new byte[checked((int)entry.Length)];
				var offset = 0;
				while (offset < bytes.Length) {
					var count = source.Read(bytes, offset, Math.Min(81920, bytes.Length - offset));
					if (count == 0) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
					offset += count;
				}
				if (source.ReadByte() != -1) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
				entries.Add(entry.FullName, bytes);
				entryTimes.Add(entry.FullName, entry.LastWriteTime);
			}
		}
		catch (InvalidDataException) { throw new EditDomainException("EDIT_CHECKPOINT_INVALID"); }
		if (!entries.TryGetValue("manifest.json", out var manifestBytes)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		EditCheckpointManifest manifest;
		try {
			using var manifestDocument = JsonDocument.Parse(manifestBytes);
			// An unknown package format is a producer-version event, not corruption
			// (contract section 8): it must classify as VERSION_UNSUPPORTED before
			// the frozen v1 schema rejects the well-formed future manifest.
			if (manifestDocument.RootElement.TryGetProperty("format", out var manifestFormat)
				&& manifestFormat.ValueKind == JsonValueKind.String
				&& manifestFormat.GetString() != "dnspy.edit.checkpoints.v1")
				throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
			EditJsonSchemaValidator.ValidateValue(manifestSchema, manifestDocument.RootElement, "checkpoint manifest");
			manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(manifestBytes, EditWire.JsonOptions) ?? throw new JsonException();
		}
		catch (EditDomainException) { throw; }
		catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException) { throw new EditDomainException("EDIT_CHECKPOINT_INVALID"); }
		ValidateManifest(manifest, entries);
		var operations = new Dictionary<string, EditCheckpointOperations>(StringComparer.Ordinal);
		long normalizedOperations = 0;
		foreach (var node in manifest.Checkpoints) {
			var bytes = entries[node.OperationEntry];
			if (EditWire.Sha256(bytes) != node.OperationSha256) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			EditCheckpointOperations op;
			try {
				using var operationDocument = JsonDocument.Parse(bytes);
				// Same producer-version precedence as the manifest: an unknown
				// envelope format is VERSION_UNSUPPORTED, not corruption.
				if (operationDocument.RootElement.TryGetProperty("format", out var envelopeFormat)
					&& envelopeFormat.ValueKind == JsonValueKind.String
					&& envelopeFormat.GetString() != "dnspy.edit.op.v1")
					throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
				EditJsonSchemaValidator.ValidateValue(operationSchema, operationDocument.RootElement, "checkpoint operation entry");
				op = JsonSerializer.Deserialize<EditCheckpointOperations>(bytes, EditWire.JsonOptions) ?? throw new JsonException();
			}
			catch (EditDomainException) { throw; }
			catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException) { throw new EditDomainException("EDIT_CHECKPOINT_INVALID"); }
			if (op.Format != "dnspy.edit.op.v1" || op.CheckpointId != node.CheckpointId || op.Operations.Count > EditWire.MaxOperations)
				throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
			normalizedOperations = CheckedAdd(normalizedOperations, op.Operations.Count, (MaxZipEntries - 2L) * EditWire.MaxOperations, "replay_operations");
			ValidateOperationEnvelopes(node, op, manifest);
			operations.Add(node.CheckpointId, op);
		}
		return new EditLoadedLineage { Manifest = manifest, BaselineBytes = entries["baseline/module.bin"], Operations = operations,
			CheckpointTimes = manifest.Checkpoints.ToDictionary(x => x.CheckpointId, x => entryTimes[x.OperationEntry], StringComparer.Ordinal),
			PayloadBytes = manifest.Payloads.ToDictionary(x => x.Sha256, x => entries[x.Entry], StringComparer.Ordinal),
			PackageBytes = package, PackageSha256 = EditWire.Sha256(package) };
	}

	static byte[] BuildPackage(EditLoadedLineage lineage) {
		using var stream = new MemoryStream();
		using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
			WriteEntry(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(lineage.Manifest, EditWire.JsonOptions));
			WriteEntry(zip, "baseline/module.bin", lineage.BaselineBytes);
			foreach (var row in lineage.Manifest.Checkpoints.OrderBy(x => x.Sequence))
				WriteEntry(zip, row.OperationEntry, JsonSerializer.SerializeToUtf8Bytes(lineage.Operations[row.CheckpointId], EditWire.JsonOptions),
					lineage.CheckpointTimes.TryGetValue(row.CheckpointId, out var recordedTime) ? recordedTime : (DateTimeOffset?)null);
			foreach (var row in lineage.Manifest.Payloads) WriteEntry(zip, row.Entry, lineage.PayloadBytes[row.Sha256]);
		}
		return stream.ToArray();
	}

	static void WriteEntry(ZipArchive zip, string name, byte[] bytes, DateTimeOffset? recordedTime = null) {
		var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		if (recordedTime.HasValue) entry.LastWriteTime = recordedTime.Value;
		using var output = entry.Open(); output.Write(bytes, 0, bytes.Length);
	}

	static void ValidateManifest(EditCheckpointManifest manifest, IReadOnlyDictionary<string, byte[]> entries) {
		if (manifest.Format != "dnspy.edit.checkpoints.v1") throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
		if (!EditHistoryIds.Is(manifest.LineageId, "lineage") || !EditHistoryIds.Is(manifest.FamilyId, "family")
			|| !EditHistoryIds.Is(manifest.HeadCheckpointId, "checkpoint")) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (manifest.Checkpoints.Count == 0 || manifest.Checkpoints.Count > MaxZipEntries - 2) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (!entries.TryGetValue("baseline/module.bin", out var baseline) || baseline.LongLength != manifest.Baseline.Length
			|| EditWire.Sha256(baseline) != manifest.Baseline.Sha256) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (manifest.SourceIdentity.Format != "source_identity.v1"
			|| !string.Equals(manifest.SourceIdentity.BaselineImageSha256, manifest.Baseline.Sha256, StringComparison.Ordinal)
			|| manifest.Baseline.Entry != "baseline/module.bin") throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (manifest.Checkpoints.Count + manifest.Payloads.Count + 2 > MaxZipEntries)
			throw Capacity("zip_entries", manifest.Checkpoints.Count + manifest.Payloads.Count + 2L, MaxZipEntries);
		if (Path.IsPathRooted(manifest.DefaultOutput.RelativePath)
			|| manifest.DefaultOutput.RelativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Any(x => x == "." || x == "..")
			|| !manifest.DefaultOutput.RelativePath.StartsWith("edit-output" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		var ids = new HashSet<string>(StringComparer.Ordinal); var sequences = new HashSet<int>();
		foreach (var node in manifest.Checkpoints) {
			if (!EditHistoryIds.Is(node.CheckpointId, "checkpoint") || !ids.Add(node.CheckpointId) || !sequences.Add(node.Sequence)
				|| node.OperationEntry != "operations/" + node.CheckpointId + ".json" || !entries.ContainsKey(node.OperationEntry))
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		}
		if (!sequences.SetEquals(Enumerable.Range(0, manifest.Checkpoints.Count))) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (manifest.Checkpoints.Count(x => x.ParentCheckpointId == null) != 1) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		var root = manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
		if (root == null || root.Sequence != 0 || (root.Kind != "baseline" && root.Kind != "accepted_baseline")) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (!ids.Contains(manifest.HeadCheckpointId)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		var sequenceById = manifest.Checkpoints.ToDictionary(x => x.CheckpointId, x => x.Sequence, StringComparer.Ordinal);
		foreach (var node in manifest.Checkpoints) if (node.ParentCheckpointId != null
			&& (!ids.Contains(node.ParentCheckpointId) || sequenceById[node.ParentCheckpointId] >= node.Sequence)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		if (manifest.Checkpoints.Count(x => x.ParentCheckpointId == null) != 1) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		var payloadIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var payload in manifest.Payloads) {
			if (!payloadIds.Add(payload.Sha256) || payload.Entry != "payloads/" + payload.Sha256 + ".bin"
				|| !entries.TryGetValue(payload.Entry, out var payloadBytes) || payloadBytes.LongLength != payload.Length
				|| EditWire.Sha256(payloadBytes) != payload.Sha256) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		}
		var allowed = new HashSet<string>(StringComparer.Ordinal) { "manifest.json", "baseline/module.bin" };
		foreach (var node in manifest.Checkpoints) allowed.Add(node.OperationEntry);
		foreach (var payload in manifest.Payloads) allowed.Add(payload.Entry);
		if (entries.Keys.Any(x => !allowed.Contains(x)) || allowed.Any(x => !entries.ContainsKey(x))) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
		var graph = new EditLoadedLineage { Manifest = manifest };
		foreach (var node in manifest.Checkpoints) PathTo(graph, node.CheckpointId);
	}

	static void ValidateOperationEnvelopes(EditCheckpointNode node, EditCheckpointOperations entry, EditCheckpointManifest manifest) {
		var operationIds = new HashSet<string>(StringComparer.Ordinal);
		var payloads = new HashSet<string>(manifest.Payloads.Select(x => x.Sha256), StringComparer.Ordinal);
		for (var index = 0; index < entry.Operations.Count; index++) {
			var operation = entry.Operations[index];
			if (!EditHistoryIds.Is(operation.OperationId, "operation") || !operationIds.Add(operation.OperationId)
				|| operation.KindVersion != 1 || (!EditWire.OperationKinds.Contains(operation.Kind, StringComparer.Ordinal)
					&& operation.Kind != "legacy_symbol_rename")) throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
			if (!operation.Forward.TryGetValue("kind", out var forwardKind) || !string.Equals(ValueString(forwardKind), operation.Kind, StringComparison.Ordinal))
				throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
			if (operation.PayloadSha256.Distinct(StringComparer.Ordinal).Count() != operation.PayloadSha256.Length
				|| operation.PayloadSha256.Any(x => !payloads.Contains(x))) throw new EditDomainException("EDIT_CHECKPOINT_INVALID", new Dictionary<string, object?> { ["kind"] = "envelope_payload" });
			if (node.ParentCheckpointId == null) throw new EditDomainException("EDIT_CHECKPOINT_INVALID", new Dictionary<string, object?> { ["kind"] = "envelope_parent" });
			using var inverse = JsonDocument.Parse(JsonSerializer.Serialize(operation.Inverse, EditWire.JsonOptions));
			var root = inverse.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !StringProperty(root, "format", "dnspy.edit.inverse.v1")
				|| !StringProperty(root, "strategy", "compiled_state")
				|| !StringProperty(root, "parent_checkpoint_id", node.ParentCheckpointId)
				|| !StringProperty(root, "operation_kind", operation.Kind)
				|| !StringProperty(root, "semantic_source", "compiled_pre_state")
				|| !root.TryGetProperty("prefix_operation_count", out var count) || !count.TryGetInt32(out var prefix) || prefix != index)
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID", new Dictionary<string, object?> { ["kind"] = "envelope_root", ["operation_kind"] = operation.Kind });
			// The state must carry exactly one known executable inverse shape.
			if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID", new Dictionary<string, object?> { ["kind"] = "envelope_state_missing", ["operation_kind"] = operation.Kind });
			var shapes = new[] {
				"field_state", "type_state", "method_state", "property_state", "event_state", "generic_state",
				"parameter_state", "member_restore", "definition_tail_remove", "absent_body",
				"parameter_tail_restore", "generic_tail_restore", "generic_tail_remove", "legacy",
				// P04 compiled-state shapes: attribute/security add-remove inverse states.
				"attribute_remove_state", "attribute_add_state", "security_remove_state", "security_add_state",
				"assembly_update_state", "module_update_state", "assembly_ref_update_state", "entry_point_set_state",
				"managed_resource_remove_state", "managed_resource_restore_state", "managed_resource_update_state",
				"win32_resource_remove_state", "win32_resource_restore_state", "win32_resource_update_state",
				"strong_name_restore_state",
			};
			var hits = shapes.Count(shape => state.TryGetProperty(shape, out _));
			var bodyShape = state.TryGetProperty("kind", out var stateKind)
				&& string.Equals(ValueString(stateKind), "method_body_replace", StringComparison.Ordinal);
			if (hits != 1 && !bodyShape) throw new EditDomainException("EDIT_CHECKPOINT_INVALID",
				new Dictionary<string, object?> { ["kind"] = "envelope_shape", ["operation_kind"] = operation.Kind, ["state_keys"] = state.EnumerateObject().Select(x => x.Name).ToArray() });
			if (operation.Kind == "legacy_symbol_rename") {
				if (!state.TryGetProperty("legacy", out var legacy) || legacy.ValueKind != JsonValueKind.Object)
					throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
				EditLegacyRenameOperation.ValidateShape(legacy);
			}
		}
	}

	static string? ValueString(object? value) => value switch {
		string text => text,
		JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
		_ => value?.ToString(),
	};
	static bool StringProperty(JsonElement value, string name, string expected) =>
		value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
		&& string.Equals(property.GetString(), expected, StringComparison.Ordinal);

	static void ValidateEntryName(string name) {
		if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.StartsWith("/", StringComparison.Ordinal)
			|| name.Split('/').Any(x => x.Length == 0 || x == "." || x == "..")
			|| !(name == "manifest.json" || name == "baseline/module.bin" || name.StartsWith("operations/", StringComparison.Ordinal) || name.StartsWith("payloads/", StringComparison.Ordinal)))
			throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
	}

	static EditCheckpointOperations SerializeOperations(EditLoadedLineage lineage, string checkpointId, string parentId, IReadOnlyList<string> rows, out ModuleDef replayModule) {
		var result = new EditCheckpointOperations { CheckpointId = checkpointId };
		// P03-CHANGE-001: the envelope carries the executable compiled inverse.
		// Each inverse is compiled against the replayed pre-state of its own
		// operation, so its references are either metadata tokens or the same
		// deterministic object ids any later replay of this prefix assigns.
		// The module outlives this method: PrepareCommit records the canonical
		// head image from it after return.
		var module = ModuleDefMD.Load(lineage.BaselineBytes);
		foreach (var node in PathTo(lineage, parentId).Skip(1)) {
			if (!lineage.Operations.TryGetValue(node.CheckpointId, out var ancestor)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			var ancestorMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
			for (var i = 0; i < ancestor.Operations.Count; i++) {
				using var json = ExpandedForward(lineage, ancestor.Operations[i]);
				EditOperationRegistry.ApplyPersisted(module, json.RootElement, ancestorMap, i);
			}
			EditStructuralValidator.Validate(module);
		}
		var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		for (var index = 0; index < rows.Count; index++) {
			var raw = rows[index];
			using var document = JsonDocument.Parse(raw);
			var kind = document.RootElement.GetProperty("kind").GetString() ?? throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			var forward = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText()) ?? new();
			// Compile before applying so the state binds to the pre-operation graph.
			var compiled = EditOperationRegistry.CompileInverse(module, document.RootElement, map);
			var payloads = Array.Empty<string>();
			if (kind is "method_body_replace" or "method_add" && document.RootElement.TryGetProperty("body", out var body)) {
				var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
				if (bytes.LongLength > EditWire.MaxResourceBytes) throw Capacity("payload_bytes", bytes.LongLength, EditWire.MaxResourceBytes);
				var hash = EditWire.Sha256(bytes);
				if (!lineage.PayloadBytes.ContainsKey(hash)) {
					lineage.PayloadBytes.Add(hash, bytes);
					lineage.Manifest.Payloads.Add(new EditPayloadEntry { Sha256 = hash, Entry = "payloads/" + hash + ".bin", Length = bytes.LongLength });
				}
				forward["body"] = new Dictionary<string, object?> { ["payload_sha256"] = hash };
				payloads = new[] { hash };
			}
			EditOperationRegistry.ApplyPersisted(module, document.RootElement, map, index);
			var inverse = new Dictionary<string, object?> {
				["format"] = "dnspy.edit.inverse.v1",
				["strategy"] = "compiled_state",
				["parent_checkpoint_id"] = parentId,
				["prefix_operation_count"] = index,
				["operation_kind"] = kind,
				["semantic_source"] = "compiled_pre_state",
				["state"] = compiled,
			};
			result.Operations.Add(new EditSerializedOperation {
				OperationId = EditWire.NewId("operation"), Kind = kind, Forward = forward,
				Inverse = inverse, PayloadSha256 = payloads,
			});
		}
		EditStructuralValidator.Validate(module);
		replayModule = module;
		return result;
	}

	static JsonDocument ExpandedForward(EditLoadedLineage lineage, EditSerializedOperation operation) {
		var forward = new Dictionary<string, object?>(operation.Forward, StringComparer.Ordinal);
		if (operation.PayloadSha256.Length != 0) {
			if (operation.Kind is not ("method_body_replace" or "method_add") || operation.PayloadSha256.Length != 1
				|| !forward.TryGetValue("body", out var body)) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			using var reference = JsonDocument.Parse(JsonSerializer.Serialize(body));
			var hash = operation.PayloadSha256[0];
			if (reference.RootElement.ValueKind != JsonValueKind.Object || reference.RootElement.EnumerateObject().Count() != 1
				|| !StringProperty(reference.RootElement, "payload_sha256", hash)
				|| !lineage.PayloadBytes.TryGetValue(hash, out var bytes) || EditWire.Sha256(bytes) != hash)
				throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			using var payload = JsonDocument.Parse(bytes);
			if (payload.RootElement.ValueKind != JsonValueKind.Object) throw new EditDomainException("EDIT_CHECKPOINT_INVALID");
			forward["body"] = payload.RootElement.Clone();
		}
		return JsonDocument.Parse(JsonSerializer.Serialize(forward, EditWire.JsonOptions));
	}

	static EditCheckpointOperations EmptyOperations(string checkpointId) => new() { CheckpointId = checkpointId };
	static EditCheckpointNode Node(string id, string? parent, string kind, byte[] operationBytes, string image, string semantic,
		int sequence, string reviewId, uint reviewRevision, IReadOnlyList<string> confirmed) => new() {
		CheckpointId = id, ParentCheckpointId = parent, Kind = kind, OperationEntry = "operations/" + id + ".json",
		OperationSha256 = EditWire.Sha256(operationBytes), Producer = Producer(),
		Review = new Dictionary<string, object?> { ["review_id"] = reviewId, ["review_revision"] = reviewRevision, ["structural"] = "passed", ["roundtrip"] = "passed" },
		ConfirmedRisks = confirmed.ToArray(), ResultImageSha256 = image, ResultSemanticFingerprint = semantic, Sequence = sequence,
	};

	static Dictionary<string, object?> Producer() => new() {
		["mcp_version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
		["dnlib_version"] = typeof(ModuleDef).Assembly.GetName().Version?.ToString() ?? "unknown",
		["runtime"] = Environment.Version.ToString(), ["compiler"] = "csharp",
	};

	static EditSourceIdentity SourceIdentity(EditWorkspace workspace) {
		EditSourceFileObservation? observed;
		try { observed = EditSourceFileIdentity.Observe(workspace.FilePath); }
		catch (Exception ex) { throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
			new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "source_identity", ["reason"] = ex.Message }); }
		return new EditSourceIdentity {
			OriginMvid = workspace.ModuleMvid, BaselineImageSha256 = workspace.BaselineImageSha256,
			BaselineSemanticFingerprint = workspace.BaselineSemanticFingerprint,
			PathHash = observed?.PathHash, OnDiskSha256 = observed?.Sha256,
			VolumeSerial = observed?.VolumeSerial, FileId = observed?.FileId,
		};
	}

	static string DefaultOutputRelative(string lineageId, string path) {
		var name = Path.GetFileName(path); if (string.IsNullOrWhiteSpace(name)) name = "module.bin";
		foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
		return Path.Combine("edit-output", lineageId, name);
	}
	EditDefaultOutput CreateDefaultOutput(string lineageId, string sourcePath, string imageSha256) {
		var relative = DefaultOutputRelative(lineageId, sourcePath);
		var absolute = Path.GetFullPath(Path.Combine(Store.ArtifactRoot, relative));
		return new EditDefaultOutput {
			RelativePath = relative, PathHash = EditSourceFileIdentity.HashNormalizedPath(absolute), ImageSha256 = imageSha256,
		};
	}

	static EditLoadedLineage Clone(EditLoadedLineage source) {
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(JsonSerializer.Serialize(source.Manifest, EditWire.JsonOptions), EditWire.JsonOptions)!;
		var operations = source.Operations.ToDictionary(x => x.Key,
			x => JsonSerializer.Deserialize<EditCheckpointOperations>(JsonSerializer.Serialize(x.Value, EditWire.JsonOptions), EditWire.JsonOptions)!, StringComparer.Ordinal);
		return new EditLoadedLineage { Manifest = manifest, BaselineBytes = (byte[])source.BaselineBytes.Clone(), Operations = operations,
			CheckpointTimes = new Dictionary<string, DateTimeOffset>(source.CheckpointTimes, StringComparer.Ordinal),
			PayloadBytes = source.PayloadBytes.ToDictionary(x => x.Key, x => (byte[])x.Value.Clone(), StringComparer.Ordinal),
			PackageBytes = source.PackageBytes, PackageSha256 = source.PackageSha256 };
	}

	static IEnumerable<EditLoadedLineage> ActiveLeaves(IReadOnlyList<EditLoadedLineage> lineages) {
		var superseded = new HashSet<string>(lineages.Select(x => x.Manifest.SupersededLineageId).Where(x => x != null)!, StringComparer.Ordinal);
		return lineages.Where(x => !superseded.Contains(x.Manifest.LineageId));
	}

	static Dictionary<string, object?> LineageSummary(EditLoadedLineage lineage) => new() {
		["lineage_id"] = lineage.Manifest.LineageId, ["family_id"] = lineage.Manifest.FamilyId,
		["head_checkpoint_id"] = lineage.Manifest.HeadCheckpointId, ["checkpoint_count"] = lineage.Manifest.Checkpoints.Count,
		["superseded_lineage_id"] = lineage.Manifest.SupersededLineageId,
		["source_identity"] = lineage.Manifest.SourceIdentity,
	};
	static Dictionary<string, object?> CandidateSummary(EditLoadedLineage lineage, string[] basis) => new() {
		["family_id"] = lineage.Manifest.FamilyId, ["lineage_id"] = lineage.Manifest.LineageId,
		["origin_mvid"] = lineage.Manifest.SourceIdentity.OriginMvid,
		["result_image_sha256"] = lineage.Head.ResultImageSha256,
		["result_semantic_fingerprint"] = lineage.Head.ResultSemanticFingerprint,
		["path_hash"] = lineage.Manifest.SourceIdentity.PathHash, ["match_basis"] = basis,
	};

	static Dictionary<string, object?> CheckpointSummary(EditLoadedLineage lineage, EditCheckpointNode node) => new() {
		["lineage_id"] = lineage.Manifest.LineageId, ["checkpoint_id"] = node.CheckpointId,
		["parent_checkpoint_id"] = node.ParentCheckpointId, ["kind"] = node.Kind, ["sequence"] = node.Sequence,
		["is_head"] = node.CheckpointId == lineage.Manifest.HeadCheckpointId, ["result_image_sha256"] = node.ResultImageSha256,
		["result_semantic_fingerprint"] = node.ResultSemanticFingerprint, ["producer"] = node.Producer,
	};

	static Dictionary<string, object?> Meter(long current, long maximum) => new() { ["current"] = current, ["maximum"] = maximum };
	static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("p03:" + offset));
	public static int DecodeCursor(string? cursor) {
		if (string.IsNullOrEmpty(cursor)) return 0;
		try { var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); return text.StartsWith("p03:", StringComparison.Ordinal) && int.TryParse(text.Substring(4), out var value) && value >= 0 ? value : throw new Exception(); }
		catch { throw new ArgumentException("invalid cursor", "cursor"); }
	}

	void RequireLiveAtHead(EditWorkspace workspace, EditLoadedLineage lineage) {
		var replay = Replay(lineage, lineage.Manifest.HeadCheckpointId, workspace.CurrentLiveFingerprint());
		if (replay.Classification != "exact" || replay.SemanticFingerprint != workspace.CurrentLiveSemanticFingerprint()
			|| replay.ImageSha256 != workspace.CurrentLiveImageSha256()) throw new EditDomainException("EDIT_LINEAGE_DIVERGED",
			new Dictionary<string, object?> { ["kind"] = "lineage_diverged", ["family_id"] = lineage.Manifest.FamilyId, ["lineage_id"] = lineage.Manifest.LineageId });
	}

	IEditCheckpointStore Store {
		get {
			ThrowIfDisposed();
			if (injectedStore) return store!;
			var current = snapshot() ?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE");
			var root = Path.GetFullPath(current.ArtifactRoot);
			if (store == null || !string.Equals(root, storeRoot, StringComparison.OrdinalIgnoreCase)) {
				store?.Dispose(); store = new WindowsEditCheckpointStore(current); storeRoot = root;
			}
			return store;
		}
	}

	static EditDomainException SourceConflict(object[] candidates) => new("EDIT_SOURCE_IDENTITY_CONFLICT",
		new Dictionary<string, object?> { ["kind"] = "source_identity_conflict", ["candidates"] = candidates });
	static EditDomainException Capacity(string limit, long current, long maximum) => new("EDIT_CAPACITY_EXCEEDED",
		new Dictionary<string, object?> { ["kind"] = "capacity", ["limit"] = limit, ["current"] = current, ["maximum"] = maximum });
	static long CheckedAdd(long value, long add, long maximum, string limit) {
		try { var result = checked(value + add); if (result > maximum) throw Capacity(limit, result, maximum); return result; }
		catch (OverflowException) { throw Capacity(limit, long.MaxValue, maximum); }
	}
	void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(EditHistoryModule)); }
	public void Dispose() { if (disposed) return; disposed = true; store?.Dispose(); replayTickets.Clear(); processBindings.Clear(); }
}
