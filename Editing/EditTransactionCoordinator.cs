using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Extension.MCP.Debugger;
using dnSpy.Extension.MCP.Execution;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

[Export(typeof(EditTransactionCoordinator))]
[Export(typeof(IMcpTransportSessionObserver))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class EditTransactionCoordinator : IMcpTransportSessionObserver, IDisposable {
	sealed class Transaction {
		public string Id = string.Empty;
		public string Owner = string.Empty;
		public McpTransportKind Transport;
		public long Generation;
		public long Started;
		public long LastActivity;
		public uint Revision;
		public EditWorkspace Workspace = null!;
		public EditRequestCache ApplyCache = new(EditWire.ApplyCacheEntries, EditWire.ApplyCacheBytes);
		public EditReviewCache ReviewCache = new();
		public string? ReviewId;
		public uint? ReviewRevision;
		public readonly List<Action> PrivateUndo = new();
		public bool CancelRequested;
		public bool OwnerClosed;
		public bool OperationBusy;
		public string CurrentLiveFingerprint = string.Empty;
		public string PrivateFingerprint = string.Empty;
		public EditHistoryBinding HistoryBinding = null!;
		public string CommitOperationKind = "commit";
		public bool CommitStarted;
		public bool LiveLinearized;
	}
	sealed class PartialCommit {
		public string RecoveryId = string.Empty;
		public string Kind = "checkpoint_finalize";
		public string OperationKind = string.Empty;
		public EditPreparedHistoryWrite Prepared = null!;
		public ModuleDef LiveModule = null!;
		public EditWorkspace? Workspace;
		public List<Action> LiveUndo = new();
		public string PreLiveFingerprint = string.Empty;
		public string PostLiveFingerprint = string.Empty;
		// CHK-017: the external-drift guard bounds of the partial state.  The
		// semantic fingerprint alone cannot see entry-point/AssemblyRef/Win32/
		// layout/CDI/declsec edits, so recovery must compare both.
		public string PreExternalGuard = string.Empty;
		public string PostExternalGuard = string.Empty;
		public string OriginalFailure = string.Empty;
		public string[] AllowedActions = Array.Empty<string>();
		public bool Resolved;
	}
	sealed class TestBarrierState : IDisposable {
		public string Name = string.Empty;
		public string Owner = string.Empty;
		public bool Entered;
		public bool Released;
		public readonly ManualResetEventSlim Release = new(false);
		public void Dispose() => Release.Dispose();
	}

	readonly object gate = new();
	readonly SemaphoreSlim operationGate = new(1, 1);
	readonly IDocumentTreeView tree;
	readonly StaticWriteGate staticWriteGate;
	readonly IEditDynamicValidationGate dynamicGate;
	readonly McpSettings settings;
	readonly EditDynamicValidationService dynamicValidation;
	readonly EditSchemaCatalog catalog = new();
	readonly EditFaultPlan faultPlan;
	readonly EditRequestCache beginCache = new(64, 4 * 1024 * 1024);
	readonly EditTerminalCache terminalCache = new();
	readonly EditRequestCache commandCache = new(1024, 32 * 1024 * 1024);
	readonly Dictionary<string, string> resolvedRecoveries = new(StringComparer.Ordinal);
	readonly Queue<string> resolvedRecoveryOrder = new();
	readonly EditHistoryModule history;
	readonly Stopwatch clock = Stopwatch.StartNew();
	Transaction? active;
	long generation;
	long testOffset;
	bool testClockActive;
	string? armedFault;
	string? lastArmedFault;
	object[] lastMutationTrace = Array.Empty<object>();
	object[] lastCoveredFaults = Array.Empty<object>();
	readonly List<Action> emergencyLiveUndo = new();
	Action? testExternalUndo;
	string? testExternalTransactionId;
	string? testExternalOriginalFingerprint;
	bool emergencyCleanup;
	string state = "idle";
	string? pendingRequestKey;
	int operationWaiters;
	TestBarrierState? testBarrier;
	readonly HashSet<string> pendingBeginSessions = new(StringComparer.Ordinal);
	readonly HashSet<string> closedPendingBeginSessions = new(StringComparer.Ordinal);
	string? pendingBeginOwner;
	McpTransportKind pendingBeginTransport;
	PartialCommit? partial;
	string? armedStorageFault;
	bool navigateInverseFailure;

	[ImportingConstructor]
	public EditTransactionCoordinator(IDocumentTreeView tree, StaticWriteGate staticWriteGate, IEditDynamicValidationGate dynamicGate, McpSettings settings, EditCompileFrontend compileFrontend, Debugger.DebugSessionService debugSessions) {
		this.tree = tree; this.staticWriteGate = staticWriteGate; this.dynamicGate = dynamicGate; this.settings = settings;
		this.compileFrontend = compileFrontend; this.debugSessions = debugSessions;
		dynamicValidation = new EditDynamicValidationService(dynamicGate, settings);
		faultPlan = new EditFaultPlan(catalog.Lowering, catalog.Faults);
		history = new EditHistoryModule(() => settings.CurrentSnapshot, catalog.CheckpointPackage);
		staticWriteGate.CoordinatorStateProvider = () => State == "idle" ? DebugStates.Idle : "editing";
	}
	readonly EditCompileFrontend compileFrontend;
	readonly Debugger.DebugSessionService debugSessions;
	// CHK-007: last scanned inbound references keyed by risk_id for commit echo
	readonly Dictionary<string, object?> LastInboundReferences = new(StringComparer.Ordinal);
	// P08 one-time strong-name failure evidence: (session_id, event_cursor) -> consumed module mvid
	readonly Dictionary<string, string> consumedStrongNameEvidence = new(StringComparer.Ordinal);

	public string State { get { lock (gate) { ExpireLocked(); return state; } } }

	// P09 (ACC-018): immutable explorer snapshot for the read-only UI.  Built
	// inside the coordinator lock; the UI thread only ever sees these rows and
	// never calls back into locking methods (adjudicated AUD-005).
	public sealed class ExplorerCheckpointRow {
		public string FamilyId = string.Empty;
		public string LineageId = string.Empty;
		public string CheckpointId = string.Empty;
		public string ParentCheckpointId = string.Empty;
		public string Kind = string.Empty;
		public string ImageShaPrefix = string.Empty;
		public string SemanticPrefix = string.Empty;
		// CHK-014: the full facts REQ-016/P09 IMP-001 require for browsing —
		// complete hashes, sequence, review binding, validation summary,
		// confirmed-risk set and recorded package-entry wall time.
		public string ImageSha256 = string.Empty;
		public string SemanticFingerprint = string.Empty;
		public int Sequence;
		public string ReviewId = string.Empty;
		public uint ReviewRevision;
		public string Structural = string.Empty;
		public string Roundtrip = string.Empty;
		public List<string> ConfirmedRisks = new();
		public string EntryTime = string.Empty;
		public string Detail = string.Empty;
	}

	public sealed class ExplorerSnapshot {
		public string State = string.Empty;
		public string? TransactionId;
		public uint Revision;
		public string OwnerTransport = string.Empty;
		public bool OwnerClosed;
		public bool OperationBusy;
		public bool CommitStarted;
		public bool CanCancel;
		public List<string> Validation = new();
		public List<string> Operations = new();
		public List<string> Diffs = new();
		public List<string> Risks = new();
		public List<string> CapacityRows = new();
		public Dictionary<string, string> LineageFamilies = new(StringComparer.Ordinal);
		public List<string> Lineages = new();
		public List<ExplorerCheckpointRow> Checkpoints = new();
	}

	public ExplorerSnapshot BuildExplorerSnapshot() {
		static string ReviewText(Dictionary<string, object?> review, string key) {
			if (!review.TryGetValue(key, out var value)) return string.Empty;
			return value is string text ? text : value is JsonElement element && element.ValueKind == JsonValueKind.String
				? element.GetString() ?? string.Empty : string.Empty;
		}
		var snapshot = new ExplorerSnapshot();
		void AddCapacity(Dictionary<string, object?> meters) {
			foreach (var row in meters.OrderBy(x => x.Key, StringComparer.Ordinal))
				if (row.Value is Dictionary<string, object?> meter)
					snapshot.CapacityRows.Add(row.Key + ": " + Convert.ToString(meter["current"], System.Globalization.CultureInfo.InvariantCulture)
						+ "/" + Convert.ToString(meter["maximum"], System.Globalization.CultureInfo.InvariantCulture));
		}
		lock (gate) {
			ExpireLocked();
			snapshot.State = state;
			if (active == null) {
				// CHK-002: the explorer must show checkpoint history even when idle —
				// the lineage browsing is a primary read-only purpose of the window
			}
			else {
				snapshot.TransactionId = active.Id;
				snapshot.Revision = active.Revision;
				snapshot.OwnerTransport = active.Transport.ToWireName();
				snapshot.OwnerClosed = active.OwnerClosed;
				snapshot.OperationBusy = active.OperationBusy;
				snapshot.CommitStarted = active.CommitStarted;
				// CHK-001 / REQ-016: the current transaction is locally cancelable
				// whenever it is active and no operation or commit is executing —
				// the owner being still connected does not block local cancel.
				snapshot.CanCancel = !active.OperationBusy && !active.CommitStarted;
				if (!active.OperationBusy) {
					AddCapacity(Capacity(active));
					foreach (var operation in active.Workspace.NormalizedOperations) {
						using var document = System.Text.Json.JsonDocument.Parse(operation);
						var kind = document.RootElement.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == System.Text.Json.JsonValueKind.String ? kindElement.GetString() : "?";
						// CHK-014: the operation row shows kind AND target (name /
						// token / owner) so same-kind rows stay distinguishable.
						var target = "?";
						if (document.RootElement.TryGetProperty("target", out var targetElement)) {
							if (targetElement.ValueKind == System.Text.Json.JsonValueKind.Object && targetElement.TryGetProperty("token", out var tokenElement)) target = tokenElement.GetString() ?? "?";
							else if (targetElement.ValueKind == System.Text.Json.JsonValueKind.String) target = targetElement.GetString() ?? "?";
						}
						foreach (var nameField in (string[])["name", "assembly_name", "entry_point"]) {
							if (target != "?") break;
							if (document.RootElement.TryGetProperty(nameField, out var nameElement) && nameElement.ValueKind == System.Text.Json.JsonValueKind.String) target = nameElement.GetString() ?? "?";
						}
						snapshot.Operations.Add(kind + "|" + target);
					}
					// CHK-014: validation summary channel (structural/roundtrip/dynamic
					// from the last review of the active transaction).
					snapshot.Validation = txValidationSummary(active);
					foreach (var diff in active.Workspace.Diffs)
						snapshot.Diffs.Add(string.Join("/", diff.TryGetValue("kind", out var diffKind) ? diffKind : "?", diff.TryGetValue("target", out var diffTarget) ? diffTarget : "?"));
					foreach (var risk in active.Workspace.Risks)
						snapshot.Risks.Add(string.Join("/", risk.TryGetValue("risk_id", out var riskId) ? riskId : "?", risk.TryGetValue("kind", out var riskKind) ? riskKind : "?", risk.TryGetValue("confirmation_required", out var riskRequired) ? riskRequired : false));
				}
			}
		}
		try {
			AddCapacity(SafeHistoryCapacity());
			foreach (var lineage in history.LoadAll()) {
				var builder = new System.Text.StringBuilder();
				builder.Append(lineage.Manifest.LineageId).Append(" head:").Append(lineage.Manifest.HeadCheckpointId);
				snapshot.LineageFamilies.Add(lineage.Manifest.LineageId, lineage.Manifest.FamilyId);
				snapshot.Lineages.Add(builder.ToString());
				// CHK-002: expose every checkpoint row (parent, kind, image and
				// semantic prefixes) so the explorer tree can show the branching
				// history even when no transaction is active.
				foreach (var checkpoint in lineage.Manifest.Checkpoints) {
					var time = lineage.CheckpointTimes.TryGetValue(checkpoint.CheckpointId, out var recordedTime)
						? recordedTime.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
						: string.Empty;
					var reviewId = ReviewText(checkpoint.Review, "review_id");
					var reviewRevision = checkpoint.Review.TryGetValue("review_revision", out var rev) && rev is System.Text.Json.JsonElement revElement && revElement.ValueKind == System.Text.Json.JsonValueKind.Number ? revElement.GetUInt32() : 0u;
					var structural = ReviewText(checkpoint.Review, "structural");
					var roundtrip = ReviewText(checkpoint.Review, "roundtrip");
					var row = new ExplorerCheckpointRow {
						FamilyId = lineage.Manifest.FamilyId,
						LineageId = lineage.Manifest.LineageId,
						CheckpointId = checkpoint.CheckpointId,
						ParentCheckpointId = checkpoint.ParentCheckpointId ?? string.Empty,
						Kind = checkpoint.Kind,
						ImageShaPrefix = checkpoint.ResultImageSha256.Substring(0, Math.Min(12, checkpoint.ResultImageSha256.Length)),
						SemanticPrefix = checkpoint.ResultSemanticFingerprint.Substring(0, Math.Min(12, checkpoint.ResultSemanticFingerprint.Length)),
						ImageSha256 = checkpoint.ResultImageSha256,
						SemanticFingerprint = checkpoint.ResultSemanticFingerprint,
						Sequence = checkpoint.Sequence,
						ReviewId = reviewId,
						ReviewRevision = reviewRevision,
						Structural = structural,
						Roundtrip = roundtrip,
						ConfirmedRisks = checkpoint.ConfirmedRisks.ToList(),
						EntryTime = time,
					};
					row.Detail = "checkpoint " + row.CheckpointId
						+ " | family " + row.FamilyId + " | lineage " + row.LineageId
						+ " | parent " + (row.ParentCheckpointId == string.Empty ? "root" : row.ParentCheckpointId)
						+ " | kind " + row.Kind
						+ " | sequence " + row.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
						+ " | review " + row.ReviewId + " rev " + row.ReviewRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
						+ " | structural " + (row.Structural == string.Empty ? "?" : row.Structural)
						+ " | roundtrip " + (row.Roundtrip == string.Empty ? "?" : row.Roundtrip)
						+ " | confirmed_risks " + (row.ConfirmedRisks.Count == 0 ? "none" : string.Join(",", row.ConfirmedRisks))
						+ " | image_sha256 " + row.ImageSha256
						+ " | semantic " + row.SemanticFingerprint
						+ (row.EntryTime == string.Empty ? string.Empty : " | package_entry_time " + row.EntryTime + " (timezone unknown)");
					snapshot.Checkpoints.Add(row);
				}
			}
		}
		catch {
			// a corrupt store is read-only noise for the explorer; the MCP tools
			// keep their own honest error reporting
		}
		return snapshot;
	}

	/// <summary>REQ-016 / CHK-001: local cancel of the CURRENT transaction from
	/// the dnSpy UI.  Cancelable whenever the transaction is active and neither
	/// an edit operation nor a commit is executing; the owner session may still
	/// be connected (its next edit call then reports EDIT_TRANSACTION_NOT_FOUND).
	/// Owner closure does not bypass the operation/commit guard.
	/// This entry reuses the rollback release path — there is no second
	/// commit/recovery implementation.</summary>
	public string CancelTransactionFromUi() {
		lock (gate) {
			var tx = active;
			if (tx == null) return "no_transaction";
			if (tx.OperationBusy || tx.CommitStarted) return "busy";
			tx.CancelRequested = true;
			ReleaseBarrierLocked(tx.Owner);
			EndLocked(tx, "ui_cancel");
			return "canceled";
		}
	}
	public JsonElement FaultGolden => catalog.Faults;

	static List<string> txValidationSummary(Transaction tx) {
		var rows = new List<string>();
		if (tx.ReviewId == null) { rows.Add("validation|not_reviewed_yet"); return rows; }
		rows.Add("validation|review_id|" + tx.ReviewId);
		rows.Add("validation|review_revision|" + (tx.ReviewRevision ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
		rows.Add("validation|operation_count|" + tx.Workspace.NormalizedOperations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
		rows.Add("validation|risks|" + tx.Workspace.Risks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
			+ "|confirmations_required|" + tx.Workspace.Risks.Count(r => Equals(r["confirmation_required"], true)).ToString(System.Globalization.CultureInfo.InvariantCulture));
		return rows;
	}
	public JsonElement MutationCorpus => catalog.Mutations;

	public CallToolResult Execute(string toolName, Dictionary<string, object>? args, McpCallContext context) {
		var serialized = toolName is not "edit_status" and not "edit_test_clock" and not "edit_test_barrier";
		var acquired = false;
		string? requestKey = null;
		string? requestPayload = null;
		try {
			if (serialized) {
				requestKey = RequestKey(toolName, args, context);
				requestPayload = requestKey == null ? null : PayloadHash(args);
				if (!operationGate.Wait(0)) {
					bool follower;
					lock (gate) {
						follower = requestKey != null && string.Equals(requestKey, pendingRequestKey, StringComparison.Ordinal);
						if (toolName == "edit_rollback" && active != null) { active.CancelRequested = true; ReleaseBarrierLocked(active.Owner); }
					}
					if (!follower && toolName != "edit_rollback") throw new EditDomainException("EDIT_TRANSACTION_BUSY");
					lock (gate) operationWaiters++;
					try { operationGate.Wait(); }
					finally { lock (gate) operationWaiters--; }
				}
				acquired = true;
				lock (gate) pendingRequestKey = requestKey;
			}
			if (requestKey != null && requestPayload != null && CacheableCommand(toolName)
				&& commandCache.TryReplay(requestKey, requestPayload, out var cached))
				return EditWire.Result(ParseEnvelope(cached));
			lock (gate) {
				ExpireLocked();
				if (state == "live_state_unknown" && toolName is not "edit_status" and not "edit_history" and not "edit_test_fault")
					throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("live state requires explicit recovery"));
				if ((state == "committed_without_checkpoint" || state == "committing")
					&& toolName is not "edit_status" and not "edit_history" and not "edit_recover"
					and not "edit_test_storage_fault" and not "edit_test_fault" and not "edit_test_barrier")
					throw new EditDomainException(state == "committing" ? "EDIT_CHECKPOINT_CLEANUP_FAILED" : "EDIT_CHECKPOINT_COMMIT_FAILED");
			}
			var envelope = toolName switch {
					"edit_begin" => Begin(args, context),
					"edit_status" => Status(context),
					"edit_apply" => Apply(args, context),
					"edit_import" => Import(args, context),
					"edit_impact_scan" => ImpactScan(args, context),
					"edit_resource_import" => ResourceImport(args, context),
					"edit_resource_export" => ResourceExport(args, context),
					"edit_review" => Review(args, context),
					"edit_rollback" => Rollback(args, context),
					"edit_commit" => Commit(args, context),
					"edit_history" => History(args, context),
					"edit_undo" => Undo(args, context),
					"edit_redo" => Redo(args, context),
					"edit_restore" => Restore(args, context),
					"edit_export" => Export(args, context),
					"edit_recover" => Recover(args, context),
					"edit_accept_live" => AcceptLive(args, context),
					"edit_test_clock" => TestClock(args),
					"edit_test_barrier" => TestBarrier(args, context),
					"edit_test_fault" => TestFault(args),
					"edit_test_external_mutation" => TestExternalMutation(args, context),
					"edit_test_live_mutation" => TestLiveMutation(args, context),
					"edit_test_apply_and_restore" => TestApplyAndRestore(args, context),
					"edit_test_storage_fault" => TestStorageFault(args),
					"edit_test_lineage_mutation" => TestLineageMutation(args, context),
					_ => throw new ArgumentException("Unknown edit tool", nameof(toolName)),
				};
			if (requestKey != null && requestPayload != null && CacheableCommand(toolName))
				commandCache.Add(requestKey, requestPayload, EditWire.CanonicalPayload(envelope));
			return EditWire.Result(envelope);
		}
		catch (EditReviewAttemptException ex) { lock (gate) { var failure=EditWire.Failure(state, ex.Code, ex.Details, ex.Message);failure["validation_attempt"]=ex.Attempt;CacheFailure(toolName,requestKey,requestPayload,failure);return EditWire.Result(failure); } }
		catch (EditDomainException ex) { lock (gate) { var failure=EditWire.Failure(state, ex.Code, ex.Details, ex.Message);if(toolName=="edit_test_apply_and_restore")failure["execution_evidence"]=ExecutionEvidence();CacheFailure(toolName,requestKey,requestPayload,failure);return EditWire.Result(failure); } }
		catch (ArgumentException) { throw; }
		catch (Exception ex) { lock (gate) { var failure=EditWire.Failure(state, "EDIT_INTERNAL_ERROR", Internal(ex.GetType().Name + ": " + ex.Message));if(toolName=="edit_test_apply_and_restore")failure["execution_evidence"]=ExecutionEvidence();CacheFailure(toolName,requestKey,requestPayload,failure);return EditWire.Result(failure); } }
		finally { if(acquired){lock(gate)pendingRequestKey=null;operationGate.Release();} }
	}

	/// <summary>Runs one legacy mutation through the same private graph, review, live apply and
	/// checkpoint protocol. It is an internal adapter seam, not a second public transaction API.</summary>
	internal Dictionary<string, object?> ExecuteLegacyMutation(string toolName, Dictionary<string, object>? sourceArgs,
		McpCallContext context, Func<EditWorkspace, LegacyEditPlan> lower, out LegacyEditPlan plan) {
		plan = null!;
		if (!operationGate.Wait(0)) throw new EditDomainException("EDIT_TRANSACTION_BUSY");
		Transaction? tx = null;
		try {
			RequireOwnerContext(context); RequireIdleForHistoryMutation();
			var assembly = EditWire.String(sourceArgs, "assembly_name");
			var beginArgs = new Dictionary<string, object> {
				["request_id"] = EditWire.NewId("legacy"), ["assembly_name"] = assembly,
			};
			if (sourceArgs != null && sourceArgs.TryGetValue("module_mvid", out var mvid) && mvid != null) beginArgs["module_mvid"] = mvid;
			Begin(beginArgs, context);
			lock (gate) tx = active ?? throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
			plan = lower(tx.Workspace);
			if (!plan.Changed) {
				lock (gate) EndLocked(tx, "legacy_no_change");
				return EditWire.Success("idle", new Dictionary<string, object?> { ["legacy_no_change"] = true });
			}
			foreach (var operation in plan.Operations) {
				if (tx.Workspace.NormalizedOperations.Count >= EditWire.MaxOperations) CapacityError("operations", tx.Workspace.NormalizedOperations.Count, EditWire.MaxOperations);
				var normalized = EditWire.CanonicalPayload(operation);
				if (tx.Workspace.NormalizedOperations.Sum(Encoding.UTF8.GetByteCount) + Encoding.UTF8.GetByteCount(normalized) > EditWire.MaxNormalizedOperationBytes)
					CapacityError("normalized_operation_bytes", Encoding.UTF8.GetByteCount(normalized), EditWire.MaxNormalizedOperationBytes);
				using var document = JsonDocument.Parse(normalized);
				var outcome = EditOperationRegistry.ApplyPersisted(tx.Workspace.PrivateModule, document.RootElement,
					tx.Workspace.ObjectIds, tx.Workspace.NormalizedOperations.Count);
				EditStructuralValidator.Validate(tx.Workspace.PrivateModule);
				var diff = new Dictionary<string, object?> {
					["operation_index"] = tx.Workspace.NormalizedOperations.Count, ["kind"] = outcome.Kind,
					["target"] = outcome.Target, ["path"] = "metadata/" + outcome.Kind,
					["before"] = outcome.Before, ["after"] = outcome.After,
					["risk_ids"] = outcome.Risks.Select(x => x["risk_id"]).ToArray(),
				};
				tx.Workspace.NormalizedOperations.Add(normalized); tx.Workspace.Diffs.Add(diff); tx.PrivateUndo.Add(outcome.Undo);
				foreach (var risk in outcome.Risks) if (!tx.Workspace.Risks.Any(x => Equals(x["risk_id"], risk["risk_id"]))) tx.Workspace.Risks.Add(risk);
				tx.Revision++; tx.PrivateFingerprint = tx.Workspace.PrivateFingerprint(); tx.ReviewId = null; tx.ReviewRevision = null;
			}
			tx.CommitOperationKind = "legacy_" + toolName;
			var reviewArgs = new Dictionary<string, object> {
				["request_id"] = EditWire.NewId("legacy-review"), ["transaction_id"] = tx.Id,
				["expected_revision"] = (long)tx.Revision,
			};
			Review(reviewArgs, context);
			var confirmed = tx.Workspace.Risks.Where(x => Equals(x["confirmation_required"], true)).Select(x => (string)x["risk_id"]!).ToArray();
			var commitArgs = JsonArguments(new Dictionary<string, object?> {
				["request_id"] = EditWire.NewId("legacy-commit"), ["transaction_id"] = tx.Id,
				["expected_revision"] = tx.Revision, ["review_id"] = tx.ReviewId,
				["review_revision"] = tx.ReviewRevision, ["confirmed_risk_ids"] = confirmed,
			});
			return Commit(commitArgs, context);
		}
		catch {
			lock (gate) if (tx != null && ReferenceEquals(active, tx) && state != "live_state_unknown") EndLocked(tx, "legacy_failed");
			throw;
		}
		finally { operationGate.Release(); }
	}

	/// <summary>Maps the historical single-method revert to a constrained history Undo.  It is
	/// intentionally valid only when the current head is the requested method's direct legacy IL
	/// checkpoint, so it cannot silently cross another edit.</summary>
	internal Dictionary<string, object?> ExecuteLegacyRevert(Dictionary<string, object>? sourceArgs,
		McpCallContext context, Func<EditWorkspace, uint> resolveMethodToken) {
		if (!operationGate.Wait(0)) throw new EditDomainException("EDIT_TRANSACTION_BUSY");
		try {
			RequireOwnerContext(context); RequireIdleForHistoryMutation();
			var assembly = EditWire.String(sourceArgs, "assembly_name");
			var mvid = OptionalArgument(sourceArgs, "module_mvid");
			using var workspace = EditWorkspace.Create(tree, assembly, mvid);
			var binding = history.ResolveBegin(workspace, null);
			if (binding.LineageId == null || binding.BaseCheckpointId == null)
				throw new ArgumentException("No pending compatible method patch exists for this module");
			var token = resolveMethodToken(workspace);
			var head = history.RequireLegacyMethodHead(binding.LineageId, binding.BaseCheckpointId, token);
			return Navigate(context, history.Load(binding.LineageId), head.CheckpointId,
				head.ParentCheckpointId!, "undo");
		}
		finally { operationGate.Release(); }
	}

	/// <summary>Maps the historical save call to the safe checkpoint export path.  For an
	/// unchanged source with no history it first creates exactly one baseline/root lineage.</summary>
	internal Dictionary<string, object?> ExecuteLegacyExport(Dictionary<string, object>? sourceArgs, McpCallContext context) {
		if (!operationGate.Wait(0)) throw new EditDomainException("EDIT_TRANSACTION_BUSY");
		try {
			RequireOwnerContext(context); RequireIdleForHistoryMutation();
			if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle)
				throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
			var assembly = EditWire.String(sourceArgs, "assembly_name");
			var mvid = OptionalArgument(sourceArgs, "module_mvid");
			using var workspace = EditWorkspace.Create(tree, assembly, mvid);
			var binding = history.ResolveBegin(workspace, null);
			EditLoadedLineage lineage;
			if (binding.IsNewFamily) {
				EditPreparedHistoryWrite? prepared = null;
				try {
					lock (gate) state = "committing";
					StorageFault("prewrite");
					prepared = history.PrepareInitialBaseline(workspace, binding);
					StorageFault("readback"); StorageFault("finalize");
					history.Finalize(prepared, workspace.LiveModule);
					lineage = prepared.Lineage; lock (gate) state = "idle";
				}
				catch (EditStagedCleanupException ex) {
					EnterCleanupRecovery(workspace.LiveModule, null, ex.Prepared, "legacy_save_assembly",
						workspace.BaselineLiveFingerprint, ex.OriginalFailure);
					throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
				}
				catch (Exception ex) {
					if (prepared != null) CleanupPreparedOrEnterRecovery(workspace.LiveModule, prepared,
						"legacy_save_assembly", workspace.BaselineLiveFingerprint,
						ex is EditDomainException domain ? domain.Code : ex.GetType().Name);
					lock (gate) state = "idle"; throw;
				}
			}
			else {
				lineage = history.Load(binding.LineageId!);
				if (!string.Equals(lineage.Manifest.HeadCheckpointId, binding.BaseCheckpointId, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			}
			var liveFingerprint = workspace.CurrentLiveFingerprint();
			var replay = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, liveFingerprint);
			if (replay.Classification != "exact" || replay.SemanticFingerprint != workspace.CurrentLiveSemanticFingerprint()
				|| replay.ImageSha256 != workspace.CurrentLiveImageSha256())
				throw new EditDomainException("EDIT_EXPORT_BLOCKED");
			var output = history.Export(replay, OptionalArgument(sourceArgs, "output_path"), workspace.FilePath);
			return EditWire.Success("idle", new Dictionary<string, object?> {
				["checkpoint"] = CheckpointResult(lineage, lineage.Manifest.HeadCheckpointId),
				["history"] = LineageResult(lineage), ["output"] = OutputResult(output), ["replay"] = ReplayResult(replay),
			});
		}
		finally { operationGate.Release(); }
	}

	internal bool HasPendingLegacyMethod(Dictionary<string, object>? sourceArgs, Func<EditWorkspace, uint> resolveMethodToken) {
		if (!operationGate.Wait(0)) return false;
		try {
			lock (gate) if (active != null || partial != null || state != "idle") return false;
			var assembly = EditWire.String(sourceArgs, "assembly_name");
			using var workspace = EditWorkspace.Create(tree, assembly, OptionalArgument(sourceArgs, "module_mvid"));
			var binding = history.ResolveBegin(workspace, null);
			return binding.LineageId != null && binding.BaseCheckpointId != null
				&& history.IsLegacyMethodHead(binding.LineageId, binding.BaseCheckpointId, resolveMethodToken(workspace));
		}
		catch { return false; }
		finally { operationGate.Release(); }
	}

	static Dictionary<string, object> JsonArguments(object value) =>
		JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(value, EditWire.JsonOptions), EditWire.JsonOptions) ?? new();

	static bool CacheableCommand(string toolName) => toolName is "edit_commit" or "edit_undo" or "edit_redo"
		or "edit_restore" or "edit_export" or "edit_recover" or "edit_accept_live";
	void CacheFailure(string toolName,string? key,string? payload,Dictionary<string,object?> failure){
		if(key==null||payload==null||!CacheableCommand(toolName))return;
		try{commandCache.Add(key,payload,EditWire.CanonicalPayload(failure));}catch(EditDomainException){ }
	}

	static string? RequestKey(string toolName, Dictionary<string,object>? args, McpCallContext context) {
		if(args==null||!args.TryGetValue("request_id",out var raw))return null;
		var id=raw is JsonElement e&&e.ValueKind==JsonValueKind.String?e.GetString():raw?.ToString();
		return id==null?null:context.AuthoritativeSessionId+":"+toolName+":"+id;
	}

	Dictionary<string, object?> Begin(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); var requestId = EditWire.String(args, "request_id"); var payload = PayloadHash(args);
		var session=context.AuthoritativeSessionId!;
		lock(gate){if (beginCache.TryReplay(session + ":" + requestId, payload, out var replay)) return ParseEnvelope(replay);if (active != null) throw new EditDomainException("EDIT_TRANSACTION_BUSY");state="editing";pendingBeginSessions.Add(session);pendingBeginOwner=session;pendingBeginTransport=context.TransportKind;}
		var assembly = EditWire.String(args, "assembly_name"); string? mvid = null;
		if (args != null && args.TryGetValue("module_mvid", out var m) && m != null) mvid = m is JsonElement je ? je.GetString() : m.ToString();
		string? sourceFamilyId = null;
		if (args != null && args.TryGetValue("source_family_id", out var family) && family != null)
			sourceFamilyId = family is JsonElement familyElement ? familyElement.GetString() : family.ToString();
		EditWorkspace? workspace=null;
		EditHistoryBinding? historyBinding=null;
		try{
			workspace = EditWorkspace.Create(tree, assembly, mvid);
			BarrierPoint("begin_after_copy",session);
			lock(gate)if(closedPendingBeginSessions.Contains(session))throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
			historyBinding = history.ResolveBegin(workspace, sourceFamilyId);
		}catch{
			workspace?.Dispose();
			lock(gate){pendingBeginSessions.Remove(session);closedPendingBeginSessions.Remove(session);pendingBeginOwner=null;state="idle";}
			throw;
		}
		// Create() has already proved the private copy and live module have the same complete
		// fingerprint.  Reuse that proven value rather than serializing the module a third time.
		var now = Now; var tx = new Transaction { Id = EditWire.NewId("edit"), Owner = session, Transport = context.TransportKind, Generation = ++generation, Started = now, LastActivity = now, Workspace = workspace, CurrentLiveFingerprint=workspace.BaselineLiveFingerprint, PrivateFingerprint=workspace.BaselineLiveFingerprint, HistoryBinding=historyBinding! };
		lock(gate){pendingBeginSessions.Remove(session);closedPendingBeginSessions.Remove(session);pendingBeginOwner=null;active = tx; state = "editing";}
		var env = EditWire.Success(state, new Dictionary<string, object?> {
			["transaction"] = TransactionResult(tx), ["source"] = SourceResult(workspace), ["fingerprints"] = Fingerprints(tx),
			["limits"] = Limits(), ["capacity"] = Capacity(tx), ["history"] = HistoryBindingResult(historyBinding!), ["capabilities"] = new Dictionary<string, object?> {
				["operation_kinds"] = EditWire.OperationKinds, ["dynamic_validation"] = true, ["test_apply_restore"] = TestMode,
			},
		});
		var json = EditWire.CanonicalPayload(env);
		try { lock(gate) beginCache.Add(session + ":" + requestId, payload, json); }
		catch { lock(gate) EndLocked(tx, "begin_capacity"); throw; }
		return env;
	}

	Dictionary<string, object?> Status(McpCallContext context) {
		lock(gate){ExpireLocked();if(active==null&&pendingBeginOwner!=null)return EditWire.Success("editing",new Dictionary<string,object?>{{"busy",true},{"state","editing"},{"owner_transport_kind",pendingBeginTransport.ToWireName()}});if (active == null) return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = state!="idle", ["state"] = state, ["history"] = SafeHistorySummary(), ["recovery"] = RecoveryResult(partial), ["capacity"] = SafeHistoryCapacity() });
		if (context.AuthoritativeSessionId != active.Owner) return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = true, ["state"] = state, ["owner_transport_kind"] = active.Transport.ToWireName() });
		return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = true, ["state"] = state, ["transaction"] = TransactionResult(active), ["fingerprints"] = Fingerprints(active), ["review"] = ReviewSummary(active), ["history"] = HistoryBindingResult(active.HistoryBinding), ["recovery"] = RecoveryResult(partial), ["capacity"] = MergeCapacity(Capacity(active), SafeHistoryCapacity()), ["risks"] = active.Workspace.Risks.ToArray() });}
	}

	Dictionary<string, object?> Apply(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx;var requestId = EditWire.String(args, "request_id"); var payload = PayloadHash(args);uint expected;
		lock(gate){tx = RequireTransactionLocked(args, context);if (tx.ApplyCache.TryReplay(requestId, payload, out var replay)) return ParseEnvelope(replay);expected = checked((uint)EditWire.Integer(args, "expected_revision")); if (expected != tx.Revision) throw Revision(expected, tx.Revision);if (tx.Workspace.NormalizedOperations.Count >= EditWire.MaxOperations) CapacityError("operations", tx.Workspace.NormalizedOperations.Count, EditWire.MaxOperations);tx.OperationBusy=true;}
		var oldPrivate = tx.PrivateFingerprint;
		var oldRisks = tx.Workspace.Risks.Select(x => new Dictionary<string, object?>(x, StringComparer.Ordinal)).ToList();
		var staged = false;
		try {
			BarrierPoint("apply_before_mutation",tx.Owner);
			if (args == null || !args.TryGetValue("operation", out var raw) || raw is not JsonElement op || op.ValueKind != JsonValueKind.Object) throw new ArgumentException("operation is required", "operation");
			ValidateStrongNameEvidence(tx, op);
			var normalized = op.GetRawText(); var newBytes = Encoding.UTF8.GetByteCount(normalized); if (tx.Workspace.NormalizedOperations.Sum(Encoding.UTF8.GetByteCount) + newBytes > EditWire.MaxNormalizedOperationBytes) CapacityError("normalized_operation_bytes", newBytes, EditWire.MaxNormalizedOperationBytes);
			var outcome = EditOperationRegistry.Apply(tx.Workspace.PrivateModule, op, tx.Workspace.ObjectIds, tx.Workspace.NormalizedOperations.Count);
			// Apply is an in-memory atomic edit plus hard structural validation.  The single
			// authoritative write/reload gate is review(), where the fixed revision is assessed.
			EditStructuralValidator.Validate(tx.Workspace.PrivateModule);var newPrivate=tx.Workspace.PrivateFingerprint();
			var diff = new Dictionary<string, object?> { ["operation_index"] = tx.Workspace.NormalizedOperations.Count, ["kind"] = outcome.Kind, ["target"] = outcome.Target, ["path"] = "metadata/" + outcome.Kind, ["before"] = outcome.Before, ["after"] = outcome.After, ["risk_ids"] = outcome.Risks.Select(r => r["risk_id"]).ToArray() };
			var prospectiveDiffBytes = EditWire.Utf8Bytes(tx.Workspace.Diffs.Concat(new[] { diff }).ToArray());
			if (prospectiveDiffBytes > EditWire.MaxDiffBytes) throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", CapacityDetails("diff_bytes", prospectiveDiffBytes, EditWire.MaxDiffBytes));
			if (tx.Workspace.ObjectIds.Count > EditWire.MaxObjectIds) CapacityError("object_ids", tx.Workspace.ObjectIds.Count, EditWire.MaxObjectIds);
			lock(gate){if(tx.CancelRequested||!ReferenceEquals(active,tx)){tx.Workspace.RestoreCommittedState();throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");}
			tx.Workspace.NormalizedOperations.Add(normalized); tx.Workspace.Diffs.Add(diff); tx.Revision++; tx.LastActivity = Now;tx.PrivateFingerprint=newPrivate; staged=true;
			foreach (var risk in outcome.Risks) if (!tx.Workspace.Risks.Any(r => Equals(r["risk_id"], risk["risk_id"]))) tx.Workspace.Risks.Add(risk);
			var env = EditWire.Success("editing", new Dictionary<string, object?> { ["transaction"] = TransactionResult(tx,tx.LastActivity,tx.Revision,null), ["operation_index"] = tx.Workspace.NormalizedOperations.Count - 1, ["kind"] = outcome.Kind, ["created_object_ids"] = outcome.CreatedObjectIds, ["fingerprints"] = Fingerprints(tx), ["diffs"] = new[] { diff }, ["risks"] = outcome.Risks, ["review_cleared"] = true, ["capacity"] = CapacityAfterApply(tx) });
			var json = EditWire.CanonicalPayload(env); tx.ApplyCache.EnsureCanAdd(json);
			tx.PrivateUndo.Add(outcome.Undo); tx.ReviewCache.Clear(); tx.ReviewId = null; tx.ReviewRevision = null; state = "editing";
			tx.ApplyCache.Add(requestId, payload, json);return env;}
		}
		catch {
			if (staged) {
				tx.Workspace.NormalizedOperations.RemoveAt(tx.Workspace.NormalizedOperations.Count - 1);
				tx.Workspace.Diffs.RemoveAt(tx.Workspace.Diffs.Count - 1);
				tx.Revision--;
				tx.Workspace.Risks.Clear(); foreach (var risk in oldRisks) tx.Workspace.Risks.Add(risk);
				staged = false;
			}
			if (!tx.CancelRequested) {
				try { if (tx.Workspace.PrivateFingerprint() != oldPrivate) tx.Workspace.RestoreCommittedState(); }
				catch { tx.Workspace.RestoreCommittedState(); }
				tx.PrivateFingerprint = tx.Workspace.PrivateFingerprint();
			}
			throw;
		}
		finally{lock(gate){tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();else if(tx.CancelRequested&&tx.OwnerClosed&&ReferenceEquals(active,tx))EndLocked(tx,"session_closed");}}
	}

	// P06 edit_import: compile the registered artifact members into frozen
	// operations on the active transaction's private copy.  The compile pass is
	// pure — every import rejection (missing artifact, ambiguous target,
	// unmapped reference) fires before any private-module mutation.  Staging
	// then follows the edit_apply semantics per operation; any staging failure
	// rolls the transaction back to its pre-import revision exactly.
	Dictionary<string, object?> Import(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx; uint expected;
		lock (gate) {
			tx = RequireTransactionLocked(args, context);
			expected = checked((uint)EditWire.Integer(args, "expected_revision"));
			if (expected != tx.Revision) throw Revision(expected, tx.Revision);
			if (tx.Workspace.NormalizedOperations.Count >= EditWire.MaxOperations) CapacityError("operations", tx.Workspace.NormalizedOperations.Count, EditWire.MaxOperations);
			tx.OperationBusy = true;
		}
		var oldPrivate = tx.PrivateFingerprint;
		var oldRisks = tx.Workspace.Risks.Select(x => new Dictionary<string, object?>(x, StringComparer.Ordinal)).ToList();
		var stagedCount = 0;
		try {
			BarrierPoint("apply_before_mutation", tx.Owner);
			var compileId = EditWire.String(args, "compile_id");
			if (args == null || !args.TryGetValue("targets", out var rawTargets) || rawTargets is not JsonElement targets || targets.ValueKind != JsonValueKind.Array)
				throw new ArgumentException("targets is required", "targets");
			var artifact = compileFrontend.Lookup(compileId)
				?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
					["kind"] = "capability", ["capability"] = "compile_artifact", ["reason"] = "compile_id is not registered in this process: " + compileId });
			var creation = new ModuleCreationOptions { TryToLoadPdbFromDisk = false };
			if (artifact.Pdb.Length != 0) creation.PdbFileOrData = artifact.Pdb;
			using var artifactModule = ModuleDefMD.Load(artifact.Assembly, creation);
			using var importer = new EditCSharpImporter(artifactModule, tx.Workspace.PrivateModule, tx.Workspace.ObjectIds, tx.Workspace.NormalizedOperations.Count);
			var plan = importer.Compile(targets);  // pure: any rejection lands here, before private writes
			var newDiffs = new List<Dictionary<string, object?>>();
			var createdIds = new List<string>();
			foreach (var row in plan) {
				if (tx.Workspace.NormalizedOperations.Count + stagedCount >= EditWire.MaxOperations) CapacityError("operations", tx.Workspace.NormalizedOperations.Count + stagedCount, EditWire.MaxOperations);
				var normalized = EditWire.CanonicalPayload(row.Operation);
				var newBytes = Encoding.UTF8.GetByteCount(normalized);
				if (tx.Workspace.NormalizedOperations.Sum(Encoding.UTF8.GetByteCount) + newBytes > EditWire.MaxNormalizedOperationBytes)
					CapacityError("normalized_operation_bytes", newBytes, EditWire.MaxNormalizedOperationBytes);
				using var document = JsonDocument.Parse(normalized);
				var outcome = EditOperationRegistry.Apply(tx.Workspace.PrivateModule, document.RootElement, tx.Workspace.ObjectIds, tx.Workspace.NormalizedOperations.Count + stagedCount);
				EditStructuralValidator.Validate(tx.Workspace.PrivateModule);
				var diff = new Dictionary<string, object?> {
					["operation_index"] = tx.Workspace.NormalizedOperations.Count + stagedCount, ["kind"] = outcome.Kind,
					["target"] = outcome.Target, ["path"] = "metadata/" + outcome.Kind,
					["before"] = outcome.Before, ["after"] = outcome.After,
					["risk_ids"] = outcome.Risks.Select(x => x["risk_id"]).ToArray(),
				};
				newDiffs.Add(diff);
				createdIds.AddRange(outcome.CreatedObjectIds);
				foreach (var risk in outcome.Risks)
					if (!tx.Workspace.Risks.Any(r => Equals(r["risk_id"], risk["risk_id"]))) tx.Workspace.Risks.Add(risk);
				tx.PrivateUndo.Add(outcome.Undo);
				stagedCount++;
			}
			var newPrivate = tx.Workspace.PrivateFingerprint();
			var prospectiveDiffBytes = EditWire.Utf8Bytes(tx.Workspace.Diffs.Concat(newDiffs).ToArray());
			if (prospectiveDiffBytes > EditWire.MaxDiffBytes) throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", CapacityDetails("diff_bytes", prospectiveDiffBytes, EditWire.MaxDiffBytes));
			if (tx.Workspace.ObjectIds.Count > EditWire.MaxObjectIds) CapacityError("object_ids", tx.Workspace.ObjectIds.Count, EditWire.MaxObjectIds);
			lock (gate) {
				if (tx.CancelRequested || !ReferenceEquals(active, tx)) { tx.Workspace.RestoreCommittedState(); throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND"); }
				foreach (var row in plan) tx.Workspace.NormalizedOperations.Add(EditWire.CanonicalPayload(row.Operation));
				foreach (var diff in newDiffs) tx.Workspace.Diffs.Add(diff);
				tx.Revision = checked((uint)(tx.Revision + stagedCount));
				tx.LastActivity = Now;
				tx.PrivateFingerprint = newPrivate;
				tx.ReviewCache.Clear(); tx.ReviewId = null; tx.ReviewRevision = null; state = "editing";
				var envelope = EditWire.Success("editing", new Dictionary<string, object?> {
					["transaction"] = TransactionResult(tx, tx.LastActivity, tx.Revision, null),
					["import"] = new Dictionary<string, object?> {
						["compile_id"] = compileId,
						["target_count"] = plan.Count,
						["rows"] = plan.Select(row => new Dictionary<string, object?> {
							["kind"] = row.Kind, ["artifact_member"] = row.ArtifactMember, ["target"] = row.Target }).ToArray(),
						["created_object_ids"] = createdIds.ToArray(),
					},
					["operation_count"] = stagedCount,
					["fingerprints"] = Fingerprints(tx),
					["diffs"] = newDiffs.ToArray(),
					["risks"] = tx.Workspace.Risks.ToArray(),
					["review_cleared"] = true,
					["capacity"] = CapacityAfterApply(tx),
				});
				return envelope;
			}
		}
		catch {
			// Roll the transaction back to its pre-import state: drop the staged
			// undo prefix, rebuild the private graph and object map from the
			// committed operations, and restore the risk snapshot.
			if (stagedCount != 0) {
				tx.PrivateUndo.RemoveRange(tx.PrivateUndo.Count - stagedCount, stagedCount);
				if (tx.Workspace.PrivateFingerprint() != oldPrivate) tx.Workspace.RestoreCommittedState();
				tx.PrivateFingerprint = tx.Workspace.PrivateFingerprint();
				tx.Workspace.Risks.Clear();
				foreach (var risk in oldRisks) tx.Workspace.Risks.Add(risk);
			}
			throw;
		}
		finally {
			lock (gate) {
				tx.OperationBusy = false;
				if (tx.CancelRequested && !ReferenceEquals(active, tx)) tx.Workspace.Dispose();
				else if (tx.CancelRequested && tx.OwnerClosed && ReferenceEquals(active, tx)) EndLocked(tx, "session_closed");
			}
		}
	}

	// P07 (adjudicated AUD-001/002): AssemblyRef rows and the entry point are
	// outside the conflict fingerprint projection by design; when identity
	// operations are staged, review compares those rows directly between the
	// private copy and the live module so external drift still fails the gate.
	static void AssertIdentityRowsUnchanged(Transaction tx) {
		// CHK-004 fix: compare against the BASELINE image (the state when the
		// transaction began), not the post-edit private copy.  Identity EDITS are
		// the intended change; the guard detects EXTERNAL drift of the live module
		// relative to what the transaction started from.
		// The baseline is captured at Begin(): the workspace's BaselineBytes
		// round-trips the module at that moment.  Reload the identity rows from
		// it for the comparison.
		var baseline = EditWorkspace.OnDispatcher(() => {
			using var rebuilt = ModuleDefMD.Load(tx.Workspace.BaselineBytes);
			string RefRow(AssemblyRef reference) => reference.Name + "|" + reference.Version + "|" + reference.Culture;
			return (
				refs: rebuilt.GetAssemblyRefs().Select(r => RefRow(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
				entry: rebuilt.ManagedEntryPoint?.MDToken.Raw.ToString("x8") ?? ""
			);
		});
		var live = tx.Workspace.LiveModule;
		string LiveRow(AssemblyRef reference) => reference.Name + "|" + reference.Version + "|" + reference.Culture;
		var liveRefs = EditWorkspace.OnDispatcher(() => live.GetAssemblyRefs().Select(r => LiveRow(r)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
		if (!liveRefs.SequenceEqual(baseline.refs, StringComparer.Ordinal))
			throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT", new Dictionary<string, object?> {
				["kind"] = "identity_rows_conflict", ["expected"] = baseline.refs, ["actual"] = liveRefs });
		var liveEntry = EditWorkspace.OnDispatcher(() => live.ManagedEntryPoint?.MDToken.Raw.ToString("x8") ?? "");
		if (!string.Equals(liveEntry, baseline.entry, StringComparison.Ordinal))
			throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT", new Dictionary<string, object?> {
				["kind"] = "entry_point_conflict", ["expected"] = baseline.entry, ["actual"] = liveEntry });
	}

	static bool ScopeIs(TypeDefOrRefSig signature, AssemblyRef reference) =>
		signature.TypeDefOrRef is TypeRef typeRef && ReferenceEquals(typeRef.ResolutionScope, reference);

	// P08 (adjudicated AUD-001): Win32 rows are outside the fingerprint
	// projection like AssemblyRefs; when Win32 operations are staged, review
	// compares the native row sets directly between private copy and live.
	static void AssertNativeRowsUnchanged(Transaction tx) {
		// Same baseline-comparison pattern as AssertIdentityRowsUnchanged (CHK-004)
		var baselineRows = EditWorkspace.OnDispatcher(() => {
			using var rebuilt = ModuleDefMD.Load(tx.Workspace.BaselineBytes);
			return NativeRows(rebuilt);
		});
		var liveRows = EditWorkspace.OnDispatcher(() => NativeRows(tx.Workspace.LiveModule));
		if (!liveRows.SequenceEqual(baselineRows, StringComparer.Ordinal))
			throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT", new Dictionary<string, object?> {
				["kind"] = "native_rows_conflict", ["expected"] = baselineRows, ["actual"] = liveRows });
	}

	static string[] NativeRows(ModuleDef module) {
		var rows = new List<string>();
		foreach (var typeDirectory in module.Win32Resources.Root.Directories) {
			var type = typeDirectory.Name.HasId ? "id:" + typeDirectory.Name.Id : "name:" + typeDirectory.Name.Name;
			foreach (var nameDirectory in typeDirectory.Directories)
				foreach (var data in nameDirectory.Data) {
					var name = nameDirectory.Name.HasId ? "id:" + nameDirectory.Name.Id : "name:" + nameDirectory.Name.Name;
					var lang = data.Name.HasId ? data.Name.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : data.Name.Name;
					rows.Add(type + "/" + name + "@" + lang + ":" + EditWire.Sha256(data.CreateReader().ToArray()));
				}
		}
		return rows.OrderBy(x => x, StringComparer.Ordinal).ToArray();
	}

	/// <summary>P08 strong-name gate (adjudicated AUD-004): strong_name_remove
	/// carries a one-time dynamic-failure evidence tuple; the event at that
	/// cursor must be a module load/validation failure naming the target
	/// assembly, and each evidence tuple is consumed exactly once.</summary>
	void ValidateStrongNameEvidence(Transaction tx, JsonElement op) {
		if (!op.TryGetProperty("kind", out var kindValue) || kindValue.GetString() != "strong_name_remove")
			return;
		if (!op.TryGetProperty("dynamic_failure", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
			throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence", "strong_name_remove requires a dynamic_failure evidence object"));
		var sessionId = evidence.TryGetProperty("session_id", out var sessionValue) && sessionValue.ValueKind == JsonValueKind.String ? sessionValue.GetString()! : throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence", "the evidence session_id is missing"));
		var cursor = evidence.TryGetProperty("event_cursor", out var cursorValue) && cursorValue.ValueKind == JsonValueKind.Number && cursorValue.TryGetInt64(out var parsedCursor) ? parsedCursor : -1;
		if (cursor <= 0)
			throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence", "the evidence event_cursor must be a positive cursor"));
		var claimedKind = evidence.TryGetProperty("event_kind", out var kindText) && kindText.ValueKind == JsonValueKind.String ? kindText.GetString()! : string.Empty;
		var failureKinds = new[] { "start_failed", "process_exited", "exception", "module_load_failed" };
		if (!failureKinds.Contains(claimedKind, StringComparer.Ordinal))
			throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence",
				"the evidence event kind must be a module load/validation failure: " + claimedKind));
		var consumedKey = sessionId + ":" + cursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
		var targetMvid = tx.Workspace.ModuleMvid;
		lock (consumedStrongNameEvidence) {
			if (consumedStrongNameEvidence.ContainsKey(consumedKey))
				throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence", "the evidence tuple was already consumed (one-time gate)"));
			{
				var read = debugSessions.ReadEventsForEvidence(sessionId, cursor - 1, 1, null);
				var target = read?.Events.FirstOrDefault();
				if (target == null)
					throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence",
						"no retained debug event exists at the evidence cursor " + cursor));
				string retainedKind;
				try {
					using var eventDocument = System.Text.Json.JsonDocument.Parse(target);
					retainedKind = eventDocument.RootElement.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == System.Text.Json.JsonValueKind.String
						? kindElement.GetString()! : string.Empty;
				}
				catch (System.Text.Json.JsonException) { retainedKind = string.Empty; }
				if (!string.Equals(retainedKind, claimedKind, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence",
						"the retained event at cursor " + cursor + " is a '" + retainedKind + "' event, not '" + claimedKind + "'"));
				// attribution: the one-time evidence tuple binds the retained event of THIS
				// debug session (session_id at the exact cursor); the session's launch
				// target is the tampered image by construction of the driver flow.
				if (!target.Contains(sessionId, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("strong_name_evidence",
						"the retained event does not name the evidence session"));
				consumedStrongNameEvidence[consumedKey] = targetMvid;
			}
		}
	}

	// P08 edit_resource_import: reads VM file bytes server-side and stages the
	// inline-payload operation (bytes never ride the MCP request body).
	Dictionary<string, object?> ResourceImport(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx;
		lock (gate) {
			tx = RequireTransactionLocked(args, context);
			var expected = checked((uint)EditWire.Integer(args, "expected_revision"));
			if (expected != tx.Revision) throw Revision(expected, tx.Revision);
			tx.OperationBusy = true;
		}
		try {
			BarrierPoint("apply_before_mutation", tx.Owner);
			var path = EditWire.String(args, "vm_path");
			var name = EditWire.String(args, "resource_name");
			var typeText = OptionalArgument(args, "resource_type") ?? "embedded";
			byte[] bytes;
			EditSourceFileObservation inputIdentity;
			try {
				(bytes, inputIdentity) = EditSourceFileIdentity.ReadAllowedResource(path,
					settings.CurrentSnapshot?.AllowedSampleRoot, EditWire.MaxResourceBytes);
			}
			catch (Exception ex) when (ex is not EditDomainException) {
				throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
					["kind"] = "capability", ["capability"] = "resource_path", ["reason"] = ex.Message });
			}

			Dictionary<string, object?> operation = typeText switch {
				"embedded" or "linked" => new Dictionary<string, object?> {
					["kind"] = "managed_resource_add", ["name"] = name,
					["attributes"] = 2u /* Private */,
					["data_base64"] = Convert.ToBase64String(bytes),
				},
				"win32" => new Dictionary<string, object?> {
					["kind"] = "win32_resource_add", ["type_name"] = "RCDATA",
					["name_string"] = name, ["lang_id"] = 0u,
					["data_base64"] = Convert.ToBase64String(bytes),
				},
				_ => throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
					["kind"] = "capability", ["capability"] = "resource_type",
					["reason"] = "resource_type must be embedded, linked or win32" }),
			};
			if (typeText == "win32") {
				if (args != null && args.ContainsKey("type_id")) { operation.Remove("type_name"); operation["type_id"] = EditWire.Integer(args, "type_id"); }
				else operation["type_name"] = OptionalArgument(args, "type_name") ?? "RCDATA";
				if (args != null && args.ContainsKey("name_id")) { operation.Remove("name_string"); operation["name_id"] = EditWire.Integer(args, "name_id"); }
				operation["lang_id"] = EditWire.Integer(args, "lang_id", required: false, minimum: 0);
			}

			var revision = tx.Revision;
			// Apply expects the operation as a JSON element (wire-shaped argument)
			using var operationDocument = System.Text.Json.JsonDocument.Parse(EditWire.CanonicalPayload(operation));
			var applyArgs = new Dictionary<string, object?> {
				["request_id"] = EditWire.String(args, "request_id"), ["transaction_id"] = tx.Id,
				["expected_revision"] = revision, ["operation"] = operationDocument.RootElement.Clone(),
			};
			var applied = Apply(applyArgs, context);
			if (applied.TryGetValue("ok", out var appliedOk) && appliedOk is true) {
				var staged = applied.TryGetValue("result", out var appliedResult) && appliedResult is Dictionary<string, object?> resultRow
					? new Dictionary<string, object?>(resultRow, StringComparer.Ordinal) : new Dictionary<string, object?>();
				staged["import"] = new Dictionary<string, object?> {
					["file_id"] = inputIdentity.FileId, ["vm_path"] = inputIdentity.FinalPath, ["resource_name"] = name,
					["resource_type"] = typeText, ["length"] = bytes.Length,
					["sha256"] = EditWire.Sha256(bytes),
				};
				return EditWire.Success(state, staged);
			}
			// surface the apply failure envelope
			return applied;
		}
		finally {
			lock (gate) {
				tx.OperationBusy = false;
				if (tx.CancelRequested && !ReferenceEquals(active, tx)) tx.Workspace.Dispose();
				else if (tx.CancelRequested && tx.OwnerClosed && ReferenceEquals(active, tx)) EndLocked(tx, "session_closed");
			}
		}
	}

	// P08 edit_resource_export: writes a committed resource's bytes below
	// ArtifactRoot and returns the full file identity.
	Dictionary<string, object?> ResourceExport(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context);
		RequireIdleForHistoryMutation();
		var name = EditWire.String(args, "resource_name");
		var outputPath = EditWire.String(args, "output_path");
		var assemblyName = EditWire.String(args, "assembly_name");
		var resource = EditWorkspace.OnDispatcher(() => {
			ModuleDef? module = null;
			foreach (var node in tree.GetAllModuleNodes()) {
				var candidate = node.Document?.ModuleDef;
				if (candidate?.Assembly?.Name is { } loaded && string.Equals(loaded, assemblyName, StringComparison.OrdinalIgnoreCase)) {
					if (module != null) return (ModuleDef?)null;
					module = candidate;
				}
			}
			return module;
		}) ?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
			["kind"] = "capability", ["capability"] = "loaded_module", ["reason"] = "the assembly is not uniquely loaded: " + assemblyName });
		var typeText = OptionalArgument(args, "resource_type") ?? "embedded";
		var typeId = args != null && args.ContainsKey("type_id") ? (int?)checked((int)EditWire.Integer(args, "type_id")) : null;
		var nameId = args != null && args.ContainsKey("name_id") ? (int?)checked((int)EditWire.Integer(args, "name_id")) : null;
		var typeName = OptionalArgument(args, "type_name") ?? "RCDATA";
		var language = checked((int)EditWire.Integer(args, "lang_id", required: false, minimum: 0));
		var data = EditWorkspace.OnDispatcher(() => (
			Bytes: EditResourceCodec.ReadExportBytes(resource, typeText, name, typeId, typeName, nameId, language),
			SourcePath: resource.Location));
		var output = history.ExportResource(data.Bytes, outputPath, data.SourcePath);
		return EditWire.Success(state, new Dictionary<string, object?> { ["export"] = OutputResult(output) });
	}

	// P07 edit_impact_scan: machine-readable cross-assembly impact report over
	// the CURRENTLY LOADED modules only (CON-013/CON-017/NON-017 — never a
	// global-completeness claim).  Inbound references match the union of the
	// baseline assembly name and any staged new name (AUD-003).
	Dictionary<string, object?> ImpactScan(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx;
		lock (gate) {
			tx = RequireTransactionLocked(args, context);
			var expected = checked((uint)EditWire.Integer(args, "expected_revision"));
			if (expected != tx.Revision) throw Revision(expected, tx.Revision);
			tx.OperationBusy = true;
		}
		try {
			BarrierPoint("apply_before_mutation", tx.Owner);
			var identityRows = new List<(int index, string kind, string? name)>();
			for (var index = 0; index < tx.Workspace.NormalizedOperations.Count; index++) {
				using var document = System.Text.Json.JsonDocument.Parse(tx.Workspace.NormalizedOperations[index]);
				var kind = document.RootElement.GetProperty("kind").GetString();
				if (kind is not ("assembly_update" or "module_update" or "assembly_ref_update" or "entry_point_set" or "strong_name_remove")) continue;
				string? stagedName = null;
				if (kind == "assembly_update" && document.RootElement.TryGetProperty("name", out var nameValue)
					&& nameValue.ValueKind == System.Text.Json.JsonValueKind.String)
					stagedName = nameValue.GetString();
				identityRows.Add((index, kind!, stagedName));
			}
			var live = tx.Workspace.LiveModule;
			var oldName = live.Assembly?.Name?.String ?? string.Empty;
			var names = new HashSet<string>(StringComparer.Ordinal) { oldName };
			foreach (var row in identityRows)
				if (row.name is { Length: > 0 }) names.Add(row.name);
			var liveMvid = live.Mvid?.ToString("D") ?? string.Empty;
			var modules = new List<object>();
			var inbound = new List<object>();
			var riskIds = new List<string>();
			var scanned = EditWorkspace.OnDispatcher(() => tree.GetAllModuleNodes()
				.Select(node => node.Document?.ModuleDef).Where(m => m != null).Cast<ModuleDef>()
				.Where(m => !string.Equals(m.Mvid?.ToString("D") ?? string.Empty, liveMvid, StringComparison.OrdinalIgnoreCase))
				.Select(m => (module: m, hits: m.GetAssemblyRefs()
					.Where(r => names.Contains(r.Name?.String ?? string.Empty))
					.Select(r => (row: r, matched: r.Name?.String ?? string.Empty)).ToArray()))
				.ToList());
			foreach (var entry in scanned) {
				modules.Add(new Dictionary<string, object?> {
					["name"] = entry.module.Assembly?.Name?.String ?? entry.module.Name.String,
					["inbound_reference_count"] = entry.hits.Length,
				});
				foreach (var hit in entry.hits) {
					var riskId = "risk-cross_assembly_inbound-" + (entry.module.Assembly?.Name?.String ?? entry.module.Name.String).Replace('.', '_') + "-" + hit.row.MDToken.Rid.ToString(System.Globalization.CultureInfo.InvariantCulture);
					var sites = entry.module.GetTypes()
						.Where(type => type.Fields.Any(f => f.FieldType is TypeDefOrRefSig fieldRef && ScopeIs(fieldRef, hit.row))
							|| type.Methods.Any(m => m.MethodSig.Params.Any(p => p is TypeDefOrRefSig paramRef && ScopeIs(paramRef, hit.row))))
						.Take(50).Select(type => (object)type.FullName).ToArray();
					var inboundRow = new Dictionary<string, object?> {
						["module"] = entry.module.Assembly?.Name?.String ?? entry.module.Name.String,
						["assembly_ref_token"] = "0x" + hit.row.MDToken.Raw.ToString("x8"),
						["matched_name"] = hit.matched,
						["sites"] = sites,
						["risk_id"] = riskId,
					};
					inbound.Add(inboundRow);
					riskIds.Add(riskId);
					LastInboundReferences[riskId] = inboundRow;
				}
			}
			lock (gate) {
				if (tx.CancelRequested || !ReferenceEquals(active, tx)) throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
				foreach (var riskId in riskIds) {
					if (tx.Workspace.Risks.Any(r => Equals(r["risk_id"], riskId))) continue;
					tx.Workspace.Risks.Add(new Dictionary<string, object?> {
						["risk_id"] = riskId, ["kind"] = "cross_assembly_inbound",
						["object"] = "loaded_modules", ["description"] = "Another loaded module references this assembly by a staged identity name",
						["confirmation_required"] = true,
					});
				}
				if (riskIds.Count != 0) { tx.ReviewCache.Clear(); tx.ReviewId = null; tx.ReviewRevision = null; }
				return EditWire.Success(state, new Dictionary<string, object?> {
					["impact"] = new Dictionary<string, object?> {
						["scope"] = "loaded_modules",
						["modules"] = modules,
						["inbound_references"] = inbound,
						["risk_ids"] = riskIds.ToArray(),
						["identity_operations"] = identityRows.Select(row => (object)new Dictionary<string, object?> {
							["operation_index"] = row.index, ["kind"] = row.kind, ["staged_name"] = row.name }).ToArray(),
					},
					["transaction"] = TransactionResult(tx, tx.LastActivity, tx.Revision, tx.ReviewRevision),
				});
			}
		}
		finally {
			lock (gate) {
				tx.OperationBusy = false;
				if (tx.CancelRequested && !ReferenceEquals(active, tx)) tx.Workspace.Dispose();
				else if (tx.CancelRequested && tx.OwnerClosed && ReferenceEquals(active, tx)) EndLocked(tx, "session_closed");
			}
		}
	}

	Dictionary<string, object?> Review(Dictionary<string, object>? args, McpCallContext context) {		Transaction tx;uint expected;var requestId=EditWire.String(args,"request_id");var payload=PayloadHash(args);lock(gate){tx = RequireTransactionLocked(args, context);if(tx.ReviewCache.TryReplay(requestId,payload,out var replay,out var stale)){if(stale!=null)throw new EditDomainException("EDIT_REVIEW_STALE");return ParseEnvelope(replay);}expected = checked((uint)EditWire.Integer(args, "expected_revision")); if (expected != tx.Revision) throw Revision(expected, tx.Revision);tx.ReviewCache.EnsureCanReplace();tx.OperationBusy=true;}
		try{BarrierPoint("review_before_validation",tx.Owner);var currentLive=tx.Workspace.CurrentLiveFingerprint();EnsureLiveUnchanged(tx,currentLive);EnsureExternalUnchanged(tx,tx.Workspace.CurrentExternalGuard());AssertIdentityRowsUnchanged(tx);var structuralRules=EditStructuralValidator.Validate(tx.Workspace.PrivateModule); tx.Workspace.ValidateRoundtrip();
		var dynamic = dynamicValidation.Run(tx.Workspace, args);
		lock(gate){if(tx.CancelRequested||!ReferenceEquals(active,tx))throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
		var reviewId=EditWire.NewId("review");var activity=tx.LastActivity;var privateFingerprint=tx.PrivateFingerprint;
		var env=EditWire.Success("reviewed", new Dictionary<string, object?> { ["transaction"] = TransactionResult(tx, activity, tx.Revision, tx.Revision), ["review"] = ReviewSummary(reviewId, tx.Revision, tx.Workspace), ["fingerprints"] = new Dictionary<string,object?>{{"baseline_live",tx.Workspace.BaselineLiveFingerprint},{"current_live",currentLive},{"private",privateFingerprint}}, ["diffs"] = tx.Workspace.Diffs.ToArray(), ["structural_validation"] = ValidationResult(structuralRules), ["roundtrip_validation"] = ValidationResult(1), ["dynamic_validation"] = dynamic, ["risks"] = tx.Workspace.Risks.ToArray(), ["limits"] = Limits() });
		var json=EditWire.CanonicalPayload(env);tx.ReviewCache.EnsureResponseFits(json);tx.ReviewCache.Store(requestId,payload,reviewId,tx.Revision,json);
		tx.CurrentLiveFingerprint=currentLive;tx.PrivateFingerprint=privateFingerprint;tx.ReviewId=reviewId;tx.ReviewRevision=tx.Revision;tx.LastActivity=activity;state="reviewed";return env;}}
		finally{lock(gate){tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();else if(tx.CancelRequested&&tx.OwnerClosed&&ReferenceEquals(active,tx))EndLocked(tx,"session_closed");}}
	}

	Dictionary<string, object?> Rollback(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context);var requestId=EditWire.String(args,"request_id");var payload=PayloadHash(args);var session=context.AuthoritativeSessionId!;
		lock(gate)if(terminalCache.TryReplay(session,requestId,payload,out var replay))return ParseEnvelope(replay);
		Transaction tx;lock(gate)tx = RequireTransactionLocked(args, context); var original = tx.Workspace.BaselineLiveFingerprint; var released = new Dictionary<string, object?> { ["private_modules"] = 1, ["validation_modules"] = 0, ["apply_cache_entries"] = tx.ApplyCache.Count, ["review_slots"] = tx.ReviewId == null ? 0 : 1 };
		var env=EditWire.Success("idle", new Dictionary<string, object?> { ["rolled_back"] = true, ["end_reason"] = "client_rollback", ["original_live_fingerprint"] = original, ["released"] = released });var json=EditWire.CanonicalPayload(env);
		lock(gate){terminalCache.Store(session,requestId,payload,json);EndLocked(tx, "client_rollback");}
		return env;
	}

	Dictionary<string, object?> Commit(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx;
		lock (gate) {
			tx = RequireTransactionLocked(args, context);
			var expected = checked((uint)EditWire.Integer(args, "expected_revision"));
			var reviewRevision = checked((uint)EditWire.Integer(args, "review_revision"));
			if (expected != tx.Revision || reviewRevision != tx.Revision) throw Revision(expected, tx.Revision);
			if (tx.ReviewId == null || tx.ReviewRevision != tx.Revision || EditWire.String(args, "review_id") != tx.ReviewId)
				throw new EditDomainException("EDIT_REVIEW_STALE");
			tx.OperationBusy = true; tx.CommitStarted = true;
		}
		var confirmed = StringArray(args, "confirmed_risk_ids");
		var required = tx.Workspace.Risks.Where(r => Equals(r["confirmation_required"], true)).Select(r => (string)r["risk_id"]!).ToArray();
		var missing = required.Except(confirmed, StringComparer.Ordinal).ToArray();
		if (missing.Length != 0) {
			lock (gate) { tx.OperationBusy = false; tx.CommitStarted = false; }
			throw new EditDomainException("EDIT_RISK_CONFIRMATION_REQUIRED", new Dictionary<string, object?> { ["kind"] = "risk_confirmation", ["missing_risk_ids"] = missing });
		}
		var prepared = default(EditPreparedHistoryWrite);
		var inverses = new List<Action>();
		var preLive = string.Empty;
		var postLive = string.Empty;
		try {
			var currentLive = tx.Workspace.CurrentLiveFingerprint(); EnsureLiveUnchanged(tx, currentLive);
			EnsureExternalUnchanged(tx, tx.Workspace.CurrentExternalGuard());
			if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
			lock (gate) state = "committing";
			BarrierPoint("commit_after_guard_before_temp", tx.Owner);
			ThrowIfCanceledBeforeLinearization(tx);
			StorageFault("prewrite");
			prepared = history.PrepareCommit(tx.Workspace, tx.HistoryBinding, tx.Workspace.NormalizedOperations,
				tx.ReviewId!, tx.Revision, confirmed, tx.CommitOperationKind);
			StorageFault("readback");
			// CHK-008 / CON-005: PrepareCommit already compiled and validated a
			// persisted inverse for every operation against a replay graph
			// equivalent to live.  Materialize that complete inverse plan here so
			// it exists and is executable before the first live mutation; if an
			// operation handler throws after mutating live but before its Outcome
			// reaches this frame, recovery still owns a pre-generated undo for it.
			var inversePlan = prepared.Lineage.Operations[prepared.PostHeadCheckpointId].Operations
				.Select(op => JsonSerializer.Serialize(op.Inverse.TryGetValue("state", out var state)
					? state : throw new EditDomainException("EDIT_CHECKPOINT_INVALID"), EditWire.JsonOptions))
				.ToArray();
			BarrierPoint("commit_after_temp_validate", tx.Owner);
			ThrowIfCanceledBeforeLinearization(tx);
			BarrierPoint("commit_dispatcher_queued", tx.Owner);
			ThrowIfCanceledBeforeLinearization(tx);
			preLive = currentLive;
			postLive = ApplyTransactionToLive(tx, inverses, inversePlan);
			BarrierPoint("commit_after_live_complete", tx.Owner);
			StorageFault("finalize");
			history.Finalize(prepared, tx.Workspace.LiveModule);
			BarrierPoint("commit_after_package_switch_before_response", tx.Owner);
			var result = EditWire.Success("idle", new Dictionary<string, object?> {
				["checkpoint"] = CheckpointResult(prepared.Lineage, prepared.PostHeadCheckpointId),
				["history"] = LineageResult(prepared.Lineage),
				["fingerprints"] = new Dictionary<string, object?> { ["before"] = preLive, ["after"] = postLive, ["private"] = tx.PrivateFingerprint },
				// CHK-008: report the pre-generated live recovery plan binding.
				["live_recovery"] = new Dictionary<string, object?> {
					["inverse_plan"] = "pregenerated_compiled_state",
					["inverse_plan_operations"] = inversePlan.Length,
					["inverse_plan_bound_checkpoint_id"] = prepared.PostHeadCheckpointId,
					["inverse_plan_complete_before_live_write"] = true,
				},
				// CHK-007: echo the full confirmed risk facts (not just IDs)
				["confirmed_risks"] = confirmed.Select(id => {
					var risk = tx.Workspace.Risks.FirstOrDefault(r => Equals(r["risk_id"], id))
						?? new Dictionary<string, object?> { ["risk_id"] = id, ["kind"] = "unknown", ["description"] = "risk fact not found at commit" };
					var row = new Dictionary<string, object?>(risk, StringComparer.Ordinal);
					if (string.Equals(risk.TryGetValue("kind", out var riskKind) ? riskKind as string : null, "cross_assembly_inbound", StringComparison.Ordinal)
						&& LastInboundReferences.TryGetValue(id, out var inboundRow))
						row["affected_references"] = inboundRow;
					return (object)row;
				}).ToArray(),
			});
			lock (gate) { tx.OperationBusy = false; active = null; state = "idle"; }
			tx.Workspace.Dispose();
			return result;
		}
		catch (EditStagedCleanupException ex) {
			EnterCleanupRecovery(tx.Workspace.LiveModule, tx.Workspace, ex.Prepared, "commit", tx.Workspace.BaselineLiveFingerprint, ex.OriginalFailure);
			lock (gate) { tx.OperationBusy = false; active = null; }
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
		catch (Exception ex) when (prepared != null && postLive == tx.PrivateFingerprint && state != "live_state_unknown") {
			// Live reached the validated target but the one final package switch failed.  Keep
			// the exact temp and inverse plan as the sole process-level recovery fact.
			partial = new PartialCommit {
				RecoveryId = EditWire.NewId("recovery"), Kind = "checkpoint_finalize", OperationKind = "commit",
				Prepared = prepared, LiveModule = tx.Workspace.LiveModule, Workspace = tx.Workspace, LiveUndo = inverses,
				PreLiveFingerprint = preLive, PostLiveFingerprint = postLive,
				PreExternalGuard = tx.Workspace.BaselineExternalGuard,
				PostExternalGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(tx.Workspace.LiveModule)),
				OriginalFailure = ex is EditDomainException domain ? domain.Code : ex.GetType().Name,
				AllowedActions = new[] { "retry_checkpoint", "undo_live" },
			};
			lock (gate) { tx.OperationBusy = false; active = null; state = "committed_without_checkpoint"; }
			throw new EditDomainException("EDIT_CHECKPOINT_COMMIT_FAILED", RecoveryResult(partial));
		}
		catch {
			if (prepared != null && state != "live_state_unknown") CleanupPreparedOrEnterRecovery(tx, prepared, tx.CommitOperationKind + "_canceled");
			lock (gate) {
				tx.OperationBusy = false; tx.CommitStarted = false;
				if (state == "live_state_unknown" && ReferenceEquals(active, tx)) active = null;
				// A pre-prepared failure inside the committing window (e.g. an
				// injected prewrite/readback fault) owns no partial and must fall
				// back to the transaction state; only a live cleanup-recovery
				// partial keeps the coordinator in "committing".  A close fact
				// received before the first live mutation cancels the commit as an
				// uncommitted transaction: the owner session is gone, so retaining
				// the reviewed transaction would wedge every later session.
				else if (ReferenceEquals(active, tx) && (state != "committing" || partial == null)) {
					if (tx.OwnerClosed && !tx.LiveLinearized) EndLocked(tx, "session_closed");
					else state = tx.ReviewId == null ? "editing" : "reviewed";
				}
			}
			if (state == "live_state_unknown") tx.Workspace.Dispose();
			throw;
		}
	}

	Dictionary<string, object?> History(Dictionary<string, object>? args, McpCallContext context) {
		if (!context.IsInitializedSession) return EditWire.Success(state, new Dictionary<string, object?> { ["view"] = "summary", ["busy"] = state != "idle", ["state"] = state });
		var lineageId = OptionalArgument(args, "lineage_id"); var checkpointId = OptionalArgument(args, "checkpoint_id");
		var cursor = OptionalArgument(args, "cursor"); var pageSize = (int)EditWire.Integer(args, "page_size", required: false, minimum: 1);
		if (pageSize == 1 && (args == null || !args.ContainsKey("page_size"))) pageSize = 10;
		if (pageSize > 100) throw new ArgumentException("page_size must be <= 100", "page_size");
		var result = history.HistoryView(lineageId, checkpointId, EditHistoryModule.DecodeCursor(cursor), pageSize);
		result["capacity"] = history.CapacityView(); result["recovery"] = RecoveryResult(partial);
		return EditWire.Success(state, result);
	}

	Dictionary<string, object?> Export(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); RequireIdleForHistoryMutation();
		if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
		var lineageId = EditWire.String(args, "lineage_id"); var checkpointId = EditWire.String(args, "checkpoint_id");
		var lineage = history.Load(lineageId); var live = FindLoadedModule(lineage.Manifest.SourceIdentity.OriginMvid);
		var replay = history.Assess(lineageId, checkpointId, live == null ? string.Empty : EditFingerprint.Compute(live));
		if (replay.Classification != "exact") throw new EditDomainException("EDIT_EXPORT_BLOCKED");
		var output = history.Export(replay, OptionalArgument(args, "output_path"), live?.Location ?? string.Empty);
		return EditWire.Success("idle", new Dictionary<string, object?> {
			["checkpoint"] = CheckpointResult(lineage, checkpointId), ["output"] = OutputResult(output), ["replay"] = ReplayResult(replay),
		});
	}

	Dictionary<string, object?> Undo(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); RequireIdleForHistoryMutation();
		var lineageId = EditWire.String(args, "lineage_id"); var expected = EditWire.String(args, "expected_checkpoint_id");
		var lineage = history.Load(lineageId); if (lineage.Manifest.HeadCheckpointId != expected) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var current = lineage.Checkpoint(expected); if (current.ParentCheckpointId == null)
			throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> { ["kind"] = "history_root", ["checkpoint_id"] = expected });
		return Navigate(context, lineage, expected, current.ParentCheckpointId, "undo");
	}

	Dictionary<string, object?> Redo(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); RequireIdleForHistoryMutation();
		var lineageId = EditWire.String(args, "lineage_id"); var expected = EditWire.String(args, "expected_checkpoint_id");
		var lineage = history.Load(lineageId); if (lineage.Manifest.HeadCheckpointId != expected) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var children = lineage.Manifest.Checkpoints.Where(x => x.ParentCheckpointId == expected).OrderBy(x => x.Sequence).ToArray();
		if (children.Length == 0) throw new EditDomainException("EDIT_HISTORY_CONFLICT", new Dictionary<string, object?> { ["kind"] = "no_redo_child" });
		var selected = OptionalArgument(args, "child_checkpoint_id");
		if (selected == null && children.Length > 1) throw new EditDomainException("EDIT_BRANCH_SELECTION_REQUIRED",
			new Dictionary<string, object?> { ["kind"] = "branch_selection", ["candidates"] = children.Select(x => x.CheckpointId).ToArray() });
		var target = selected == null ? children[0] : children.SingleOrDefault(x => x.CheckpointId == selected)
			?? throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		return Navigate(context, lineage, expected, target.CheckpointId, "redo");
	}

	Dictionary<string, object?> Restore(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); RequireIdleForHistoryMutation();
		var lineageId = EditWire.String(args, "lineage_id"); var checkpointId = EditWire.String(args, "checkpoint_id");
		var action = EditWire.String(args, "action"); var lineage = history.Load(lineageId);
		var live = RequireLoadedModule(lineage.Manifest.SourceIdentity.OriginMvid); var liveFingerprint = EditFingerprint.Compute(live);
		if (action == "assess") return EditWire.Success("idle", new Dictionary<string, object?> { ["replay"] = ReplayResult(history.Assess(lineageId, checkpointId, liveFingerprint)) });
		if (action != "apply") throw new ArgumentException("action must be assess or apply", "action");
		var replayId = EditWire.String(args, "replay_id"); var expectedLive = EditWire.String(args, "expected_live_fingerprint");
		if (expectedLive != liveFingerprint) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		var replay = history.RequireTicket(replayId, lineageId, checkpointId, liveFingerprint, lineage.Manifest.HeadCheckpointId);
		if (replay.Classification == "unverified_drift") throw new EditDomainException("EDIT_REPLAY_UNVERIFIED");
		if (replay.Classification == "validated_drift") {
			if (!EditWire.Bool(args, "confirm_validated_drift")) throw new EditDomainException("EDIT_REPLAY_CONFIRMATION_REQUIRED");
			return MigrateValidated(lineage, replay, live, liveFingerprint);
		}
		return Navigate(context, lineage, lineage.Manifest.HeadCheckpointId, checkpointId, "restore", replay);
	}

	Dictionary<string, object?> Recover(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); var recoveryId = EditWire.String(args, "recovery_id"); var action = EditWire.String(args, "action");
		var resolvedKey = recoveryId + "\n" + action;
		lock (gate) if (resolvedRecoveries.TryGetValue(resolvedKey, out var resolved)) return ParseEnvelope(resolved);
		var current = partial; if (current == null || current.RecoveryId != recoveryId) throw new EditDomainException("EDIT_RECOVERY_NOT_FOUND");
		if (!current.AllowedActions.Contains(action, StringComparer.Ordinal)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (!history.TempMatches(current.Prepared) || !history.PreHeadUnchanged(current.Prepared)) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (action == "retry_checkpoint") {
			// CHK-017: retry requires the live module to still be exactly the
			// post-commit state — semantic fingerprint AND external guard.
			if (current.Kind != "checkpoint_finalize" || CurrentLiveFingerprint(current) != current.PostLiveFingerprint
				|| CurrentExternalGuard(current) != current.PostExternalGuard)
				throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			StorageFault("finalize"); history.Finalize(current.Prepared, current.LiveModule);
			current.Resolved = true; partial = null; lock (gate) state = "idle";
			var envelope = EditWire.Success("idle", new Dictionary<string, object?> { ["resolved"] = true, ["action"] = action,
				["checkpoint"] = CheckpointResult(current.Prepared.Lineage, current.Prepared.PostHeadCheckpointId), ["history"] = LineageResult(current.Prepared.Lineage) });
			RememberResolvedRecovery(resolvedKey, envelope); current.Workspace?.Dispose(); return envelope;
		}
		if (action == "undo_live") {
			// CHK-017: undo is only permitted while NOTHING has changed since the
			// partial commit — including drift the semantic fingerprint cannot
			// see (entry point, AssemblyRef, Win32, layout, CDI, declsec).
			if (current.Kind != "checkpoint_finalize" || CurrentLiveFingerprint(current) != current.PostLiveFingerprint
				|| CurrentExternalGuard(current) != current.PostExternalGuard)
				throw new EditDomainException("EDIT_HISTORY_CONFLICT");
			try {
				EditWorkspace.OnDispatcher(() => { for (var i = current.LiveUndo.Count - 1; i >= 0; i--) current.LiveUndo[i](); return 0; });
				if (CurrentLiveFingerprint(current) != current.PreLiveFingerprint
					|| CurrentExternalGuard(current) != current.PreExternalGuard) throw new Exception("inverse fingerprint mismatch");
			}
			catch {
				partial = null; lock (gate) state = "live_state_unknown";
				throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("partial inverse failed"));
			}
			try { StorageFault("cleanup"); history.DeleteOwnedTemp(current.Prepared); }
			catch {
				current.Kind = "aborted_temp_cleanup"; current.AllowedActions = new[] { "cleanup_temp" };
				current.LiveUndo.Clear(); lock (gate) state = "committing";
				throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(current));
			}
			partial = null; lock (gate) state = "idle"; var restored = CurrentLiveFingerprint(current);
			// CHK-017 evidence run: for a FIRST-checkpoint partial the lineage
			// package only exists in the staged temp just deleted — use the
			// in-memory lineage captured in the partial (as retry_checkpoint
			// does) instead of re-loading the store.
			var envelope = EditWire.Success("idle", new Dictionary<string, object?> { ["resolved"] = true, ["action"] = action,
				["restored_fingerprint"] = restored, ["history"] = LineageResult(current.Prepared.Lineage) });
			RememberResolvedRecovery(resolvedKey, envelope); current.Workspace?.Dispose(); return envelope;
		}
		if (action != "cleanup_temp" || current.Kind != "aborted_temp_cleanup") throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		if (CurrentLiveFingerprint(current) != current.PreLiveFingerprint
			|| CurrentExternalGuard(current) != current.PreExternalGuard) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		StorageFault("cleanup"); history.DeleteOwnedTemp(current.Prepared); partial = null; lock (gate) state = "idle";
		var cleanupEnvelope = EditWire.Success("idle", new Dictionary<string, object?> { ["resolved"] = true, ["action"] = action,
			["removed_temp"] = true, ["history"] = SafeHistorySummary() });
		RememberResolvedRecovery(resolvedKey, cleanupEnvelope); current.Workspace?.Dispose(); return cleanupEnvelope;
	}

	Dictionary<string, object?> AcceptLive(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context); RequireIdleForHistoryMutation();
		if (!EditWire.Bool(args, "acknowledge_new_baseline")) throw new ArgumentException("acknowledge_new_baseline must be true", "acknowledge_new_baseline");
		if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
		var assembly = EditWire.String(args, "assembly_name"); var family = EditWire.String(args, "source_family_id");
		var superseded = EditWire.String(args, "superseded_lineage_id"); var expected = EditWire.String(args, "expected_live_fingerprint");
		var mvid = OptionalArgument(args, "module_mvid"); using var workspace = EditWorkspace.Create(tree, assembly, mvid);
		if (workspace.BaselineLiveFingerprint != expected) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
		history.ValidateAcceptLive(workspace, family, superseded);
		EditPreparedHistoryWrite prepared;
		try { prepared = history.PrepareAcceptedBaseline(workspace, family, superseded); }
		catch (EditStagedCleanupException ex) {
			EnterCleanupRecovery(workspace.LiveModule, null, ex.Prepared, "accept_live", workspace.BaselineLiveFingerprint, ex.OriginalFailure);
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
		try { StorageFault("finalize"); history.Finalize(prepared, workspace.LiveModule); }
		catch (Exception ex) {
			CleanupPreparedOrEnterRecovery(workspace.LiveModule, prepared, "accept_live", workspace.BaselineLiveFingerprint,
				ex is EditDomainException domain ? domain.Code : ex.GetType().Name);
			throw;
		}
		return EditWire.Success("idle", new Dictionary<string, object?> { ["source_identity"] = prepared.Lineage.Manifest.SourceIdentity,
			["lineage"] = LineageResult(prepared.Lineage), ["root_checkpoint"] = CheckpointResult(prepared.Lineage, prepared.PostHeadCheckpointId),
			["superseded_lineage_id"] = superseded });
	}

	Dictionary<string, object?> TestClock(Dictionary<string, object>? args) {
		lock(gate){RequireTest(); var action = args != null && args.TryGetValue("action", out var a) ? (a is JsonElement e ? e.GetString() : a?.ToString()) : null;
		if (action == "reset") { testOffset = 0; testClockActive = true; }
		else if (action == "advance") { testClockActive = true; testOffset += EditWire.Integer(args, "advance_ms"); }
		else if (action != "read") throw new ArgumentException("action must be read, reset, or advance", "action");
		ExpireLocked(); return EditWire.Success(state, new Dictionary<string, object?> { ["monotonic_ms"] = Now, ["offset_ms"] = testOffset });}
	}

	Dictionary<string, object?> TestBarrier(Dictionary<string, object>? args, McpCallContext context) {
		RequireTest();RequireOwnerContext(context);var action=EditWire.String(args,"action");
		lock(gate){
			if(action=="arm"){
				if(testBarrier!=null)throw new ArgumentException("a test barrier is already armed","action");
				var name=EditWire.String(args,"name");
				testBarrier=new TestBarrierState{Name=name,Owner=context.AuthoritativeSessionId!};
			}else if(action=="release"){
				if(testBarrier==null)throw new ArgumentException("no test barrier is armed","action");
				testBarrier.Released=true;testBarrier.Release.Set();
			}else if(action=="reset"){
				if(testBarrier!=null){testBarrier.Released=true;testBarrier.Release.Set();testBarrier.Dispose();testBarrier=null;}
			}else if(action!="snapshot")throw new ArgumentException("action must be arm, snapshot, release, or reset","action");
			return BarrierSnapshotLocked();
		}
	}

	void BarrierPoint(string name,string owner){
		TestBarrierState? barrier;
		lock(gate){barrier=testBarrier;if(barrier==null||barrier.Name!=name||barrier.Owner!=owner)return;barrier.Entered=true;}
		if(!barrier.Release.Wait(TimeSpan.FromSeconds(120)))throw new EditDomainException("EDIT_INTERNAL_ERROR",Internal("test barrier timed out"));
	}

	Dictionary<string,object?> BarrierSnapshotLocked()=>EditWire.Success(state,new Dictionary<string,object?>{
		["armed"]=testBarrier!=null,["name"]=testBarrier?.Name,["owner_session_id"]=testBarrier?.Owner,
		["entered"]=testBarrier?.Entered??false,["released"]=testBarrier?.Released??false,
		["operation_waiters"]=operationWaiters,["pending_request_key"]=pendingRequestKey,
		["active_generation"]=active?.Generation,["active_transaction_id"]=active?.Id,
	});

	void ReleaseBarrierLocked(string owner){if(testBarrier!=null&&testBarrier.Owner==owner){testBarrier.Released=true;testBarrier.Release.Set();}}

	Dictionary<string, object?> TestFault(Dictionary<string, object>? args) {
		RequireTest(); var action = EditWire.String(args, "action"); var known = catalog.Faults.GetProperty("faults").EnumerateArray().Select(x => x.GetProperty("fault_id").GetString()!).ToArray();
		if (action == "arm") { var id = EditWire.String(args, "fault_id"); if (!known.Contains(id, StringComparer.Ordinal)) throw new ArgumentException("unknown fault_id", "fault_id"); armedFault = id; }
		else if (action == "reset") { armedFault = null; if (state == "live_state_unknown" && active != null) {
			var tx=active;tx.Workspace.OnLive(() => { foreach(var undo in emergencyLiveUndo)undo();return 0; });
			var restored=tx.Workspace.CurrentLiveFingerprint();if(restored!=tx.Workspace.BaselineLiveFingerprint||tx.Workspace.CurrentExternalGuard()!=tx.Workspace.BaselineExternalGuard)throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN",Internal("emergency cleanup did not restore the live fingerprint"));
			emergencyLiveUndo.Clear();EndLocked(tx, "emergency_cleanup"); emergencyCleanup = true;
		} }
		else if (action != "read") throw new ArgumentException("action must be read, reset, or arm", "action");
		return EditWire.Success(state, new Dictionary<string, object?> { ["armed_fault_id"] = armedFault, ["known_fault_ids"] = known, ["emergency_cleanup"] = emergencyCleanup });
	}

	Dictionary<string, object?> TestApplyAndRestore(Dictionary<string, object>? args, McpCallContext context) {
		RequireTest(); Transaction tx;uint expected;lock(gate){tx = RequireTransactionLocked(args, context);expected = checked((uint)EditWire.Integer(args, "expected_revision")); if (expected != tx.Revision) throw Revision(expected, tx.Revision);tx.OperationBusy=true;}
		var before=string.Empty;var applyStarted=false;var inverses = new List<(string Kind,Action Undo)>();var undone = new HashSet<int>();var restored=string.Empty;var actualTrace=new List<object>();
		try {
		if (tx.ReviewId == null || tx.ReviewRevision != tx.Revision || EditWire.String(args, "review_id") != tx.ReviewId) throw new EditDomainException("EDIT_REVIEW_STALE");
		var currentLive=tx.Workspace.CurrentLiveFingerprint();EnsureLiveUnchanged(tx,currentLive);EnsureExternalUnchanged(tx,tx.Workspace.CurrentExternalGuard()); var gateResult = dynamicGate.EvaluateEditDynamicValidation(); if (gateResult.State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
		var confirmed = StringArray(args, "confirmed_risk_ids"); var required = tx.Workspace.Risks.Where(r => Equals(r["confirmation_required"], true)).Select(r => (string)r["risk_id"]!).ToArray(); var missing = required.Except(confirmed, StringComparer.Ordinal).ToArray(); if (missing.Length != 0) throw new EditDomainException("EDIT_RISK_CONFIRMATION_REQUIRED", new Dictionary<string, object?> { ["kind"] = "risk_confirmation", ["missing_risk_ids"] = missing });
		state = "applying"; applyStarted=true; before = currentLive; var liveMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal); var callbacks = new List<double>(); string after = before; restored = before;
			lastArmedFault=armedFault;lastMutationTrace=Array.Empty<object>();var armedRow=faultPlan.ArmedObject(armedFault);lastCoveredFaults=armedRow==null?Array.Empty<object>():new[]{armedRow};
			var sw = Stopwatch.StartNew();
			try { tx.Workspace.OnLive(() => {
				for (int i = 0; i < tx.Workspace.NormalizedOperations.Count; i++) {
					using var doc = JsonDocument.Parse(tx.Workspace.NormalizedOperations[i]);
					var kind = doc.RootElement.GetProperty("kind").GetString()!;
					EditOperationOutcome? outcome = null;
					foreach (var row in faultPlan.BoundaryRows(kind, "forward")) {
						if (EditFaultPlan.Boundary(row) == "after" && outcome == null) {
							outcome = EditOperationRegistry.Apply(tx.Workspace.LiveModule, doc.RootElement, liveMap, i);
							inverses.Add((outcome.Kind, outcome.Undo));
						}
						RecordFaultBoundary(actualTrace, row, forward: true);
					}
					if (outcome == null) {
						outcome = EditOperationRegistry.Apply(tx.Workspace.LiveModule, doc.RootElement, liveMap, i);
						inverses.Add((outcome.Kind, outcome.Undo));
					}
				}
				return 0;
			}); }
			finally { sw.Stop();callbacks.Add(sw.Elapsed.TotalMilliseconds); }
			after = tx.Workspace.CurrentLiveFingerprint();
			if (!string.Equals(after, tx.PrivateFingerprint, StringComparison.Ordinal))
				throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails(
					"live_private_fingerprint", "live_apply", EditFingerprint.Difference(tx.Workspace.PrivateModule, tx.Workspace.LiveModule)));
			var undoSw = Stopwatch.StartNew();
			try { tx.Workspace.OnLive(() => { for (int i = inverses.Count - 1; i >= 0; i--) {
				var reversed = false;
				foreach (var row in faultPlan.BoundaryRows(inverses[i].Kind, "reverse")) {
					if (EditFaultPlan.Boundary(row) == "after" && !reversed) {
						inverses[i].Undo();undone.Add(i);reversed=true;
					}
					RecordFaultBoundary(actualTrace, row, forward: false, inverses, undone);
				}
				if (!reversed) { inverses[i].Undo();undone.Add(i); }
			} return 0; }); }
			finally { undoSw.Stop();callbacks.Add(undoSw.Elapsed.TotalMilliseconds); }
			restored = tx.Workspace.CurrentLiveFingerprint();lastMutationTrace=actualTrace.Take(24).ToArray();
			if (restored != before) throw new ReverseFaultException(); state = "reviewed";
			return EditWire.Success(state, new Dictionary<string, object?> { ["applied_and_restored"] = true, ["pre_live_fingerprint"] = before, ["private_fingerprint"] = tx.PrivateFingerprint, ["post_apply_fingerprint"] = after, ["post_restore_fingerprint"] = restored, ["execution_evidence"] = ExecutionEvidence(), ["dispatcher_callbacks_ms"] = callbacks });
		}
		catch (ReverseFaultException) { lastMutationTrace=actualTrace.Take(24).ToArray();state = "live_state_unknown"; throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("reverse operation failed")); }
		catch (ForwardFaultException) {
			lastMutationTrace=actualTrace.Take(24).ToArray();RestoreOutstanding(tx,inverses,undone);restored=tx.Workspace.CurrentLiveFingerprint();state=restored==before?"reviewed":"live_state_unknown";
			if(state=="live_state_unknown"){CaptureEmergency(inverses,undone);throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN",Internal("forward fault cleanup failed"));}
			throw new EditDomainException("EDIT_INTERNAL_ERROR",Internal("injected forward mutation fault"));
		}
		catch {
			lastMutationTrace=actualTrace.Take(24).ToArray();
			// Review, live-fingerprint, debugger-state, and confirmation guards run
			// before any live mutation.  Their domain failures must not enter the
			// recovery path (whose pre-apply fingerprint is intentionally unset).
			if(!applyStarted)throw;
			try { RestoreOutstanding(tx,inverses,undone); restored = tx.Workspace.CurrentLiveFingerprint(); state = restored == before ? "reviewed" : "live_state_unknown"; }
			catch { state = "live_state_unknown"; }
			throw;
		}
		finally { lock(gate){armedFault = null;tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();} }
	}

	void RecordFaultBoundary(List<object> trace, object row, bool forward,
		IReadOnlyList<(string Kind, Action Undo)>? inverses = null, ISet<int>? undone = null) {
		// A fault case may need dependency operations to construct the target object.  The
		// contract's per-call trace is nevertheless the prefix for the single armed operation,
		// so dependency rows are deliberately excluded while a fault is armed.
		if (trace.Count < 24 && (armedFault == null || SameFaultOperation(EditFaultPlan.Id(row), armedFault)))
			trace.Add(row);
		if (!string.Equals(EditFaultPlan.Id(row), armedFault, StringComparison.Ordinal)) return;
		if (forward) throw new ForwardFaultException();
		if (inverses != null && undone != null) CaptureEmergency(inverses, undone);
		throw new ReverseFaultException();
	}

	static bool SameFaultOperation(string left, string right) {
		var leftEnd = left.IndexOf('-', 3);
		var rightEnd = right.IndexOf('-', 3);
		return leftEnd > 3 && rightEnd > 3 && string.Equals(
			left.Substring(0, leftEnd), right.Substring(0, rightEnd), StringComparison.Ordinal);
	}

	Dictionary<string, object?> TestExternalMutation(Dictionary<string, object>? args, McpCallContext context) {
		RequireTest(); Transaction tx;lock(gate){tx = RequireTransactionLocked(args, context);tx.OperationBusy=true;} var caseId = EditWire.String(args, "case_id");
		try{
		if(caseId is "live-conflict:mutate" or "live-conflict:mutate-entrypoint" or "live-conflict:mutate-layout"
			or "live-conflict:mutate-cdi" or "live-conflict:restore")return TestPersistentExternalMutation(tx,caseId);
		var recipes = catalog.Mutations.GetProperty("cases").EnumerateArray().ToList(); var recipe = recipes.FirstOrDefault(x => x.GetProperty("case_id").GetString() == caseId); if (recipe.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("unknown case_id", "case_id");
		var before = tx.Workspace.CurrentLiveFingerprint(); string locatedBefore = before, locatedAfter = before, after = before, restored = before; string rawBefore = before, rawAfter = before; Action undo = () => { }; bool semantic = !caseId.Contains("canonical-global-order", StringComparison.Ordinal);
		tx.Workspace.OnLive(() => { var module = tx.Workspace.LiveModule; if (caseId.Contains("modulemetadata")) { var old=module.Name; module.Name=old+".changed"; undo=()=>module.Name=old; }
			else if(caseId.Contains("dnlibobjectgraph")){var t=module.GetTypes().First(x=>x.MDToken.Raw!=0x02000001);var old=t.Name;t.Name=old+"Changed";undo=()=>t.Name=old;}
			else if(caseId.Contains("methodbodyil")){var i=module.GetTypes().SelectMany(t=>t.Methods).First(m=>m.HasBody&&m.Body.Instructions.Count>0).Body.Instructions[0];var old=i.OpCode;i.OpCode=old.Code==Code.Nop?OpCodes.Break:OpCodes.Nop;undo=()=>i.OpCode=old;}
			else if(caseId.Contains("managedresource")){var r=module.Resources.OfType<EmbeddedResource>().FirstOrDefault();if(r==null)throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("managed_resource_fixture","Required embedded resource is absent"));var idx=module.Resources.IndexOf(r);var replacement=new EmbeddedResource(r.Name,Encoding.UTF8.GetBytes("changed"),r.Attributes);module.Resources[idx]=replacement;undo=()=>module.Resources[idx]=r;}
			else if(caseId.Contains("embeddedpdb")){var i=module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).FirstOrDefault(i=>i.SequencePoint?.Document!=null);if(i==null)throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("embedded_pdb_fixture","Required sequence point is absent"));var old=i.SequencePoint.Document.Url;i.SequencePoint.Document.Url="fingerprint-changed.cs";undo=()=>i.SequencePoint.Document.Url=old;}
			else if(caseId.Contains("canonical-global-order")){rawBefore=EditFingerprint.ChannelOrderHash(module,false);rawAfter=EditFingerprint.ChannelOrderHash(module,true);}
			return 0;});
		after=tx.Workspace.CurrentLiveFingerprint();locatedAfter=after;if(semantic)rawAfter=after;tx.Workspace.OnLive(()=>{undo();return 0;});restored=tx.Workspace.CurrentLiveFingerprint();
		var artifactRoot=settings.CurrentSnapshot?.ArtifactRoot;if(string.IsNullOrWhiteSpace(artifactRoot))throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("artifact_root","ArtifactRoot is not configured"));var artifactDirectory=Path.Combine(artifactRoot,"edit-tests","fingerprint");Directory.CreateDirectory(artifactDirectory);var artifactPath=Path.Combine(artifactDirectory,"dnspy-edit-"+caseId.Replace(':','-')+"-"+Guid.NewGuid().ToString("N")+".json");var artifactJson=JsonSerializer.Serialize(new{case_id=caseId,before,after,restored});File.WriteAllText(artifactPath,artifactJson);
		return EditWire.Success(state,new Dictionary<string,object?>{{"case_id",caseId},{"recipe_id",recipe.GetProperty("recipe_id").GetString()!},{"component",recipe.GetProperty("component").GetString()!},{"recipe_sha256",EditWire.Sha256(Encoding.UTF8.GetBytes(recipe.GetRawText()))},{"evidence_artifact",new Dictionary<string,object?>{{"path",artifactPath},{"sha256",EditWire.Sha256(Encoding.UTF8.GetBytes(artifactJson))}}},{"located_slice_before",locatedBefore},{"located_slice_after",locatedAfter},{"raw_order_before",rawBefore},{"raw_order_after",rawAfter},{"canonical_readback_before",before},{"canonical_readback_after",semantic?after:before},{"before_fingerprint",before},{"after_fingerprint",after},{"restored_fingerprint",restored},{"changed",semantic?after!=before:rawAfter!=rawBefore},{"semantic_change",semantic},{"restored",restored==before}});
		}finally{lock(gate){tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();}}
	}

	Dictionary<string,object?> TestLiveMutation(Dictionary<string,object>? args,McpCallContext context){
		RequireTest();var action=EditWire.String(args,"action");
		if(action is not ("mutate" or "restore"))throw new ArgumentException("action must be mutate or restore","action");
		var forwarded=new Dictionary<string,object>(StringComparer.Ordinal);
		if(args!=null&&args.TryGetValue("transaction_id",out var transactionId))forwarded["transaction_id"]=transactionId;
		forwarded["case_id"]="live-conflict:"+action;
		return TestExternalMutation(forwarded,context);
	}

	Dictionary<string,object?> TestPersistentExternalMutation(Transaction tx,string caseId){
		var before=tx.Workspace.CurrentLiveFingerprint();string after;bool restored;
		if(caseId is "live-conflict:mutate" or "live-conflict:mutate-entrypoint" or "live-conflict:mutate-layout" or "live-conflict:mutate-cdi"){
			if(testExternalUndo!=null)throw new ArgumentException("a persistent external mutation is already active","case_id");
			tx.Workspace.OnLive(()=>{
				var module=tx.Workspace.LiveModule;
				if(caseId=="live-conflict:mutate-entrypoint"){
					// CHK-003: entry point A->B is invisible to the frozen semantic
					// fingerprint; the external guard must still flag the drift.
					var candidates=module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.IsStatic&&m.IsPublic).ToList();
					var current=module.ManagedEntryPoint as MethodDef;
					var other=candidates.FirstOrDefault(m=>!ReferenceEquals(m,current))
						?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("entry_point_fixture","No second public static method to swap the entry point to"));
					var oldEntry=module.ManagedEntryPoint;module.ManagedEntryPoint=other;testExternalUndo=()=>module.ManagedEntryPoint=oldEntry;
					return 0;
				}
				if(caseId=="live-conflict:mutate-layout"){
					// CHK-012: ClassLayout packing/size changes are invisible to
					// both the semantic projection and the entry/ref/win32 rows.
					var layoutType=module.GetTypes().FirstOrDefault(t=>t.ClassLayout!=null);
					if(layoutType==null)throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("layout_fixture","No type with a ClassLayout row to mutate"));
					var oldLayout=layoutType.ClassLayout;
					var replacement=new dnlib.DotNet.ClassLayoutUser(oldLayout.PackingSize,(uint)(oldLayout.ClassSize+8));
					layoutType.ClassLayout=replacement;testExternalUndo=()=>layoutType.ClassLayout=oldLayout;
					return 0;
				}
				if(caseId=="live-conflict:mutate-cdi"){
					// CHK-012: same-kind different-value CDI content (the default
					// namespace) is invisible to type-name-only CDI rows.
					var existing=module.CustomDebugInfos.OfType<dnlib.DotNet.Pdb.PdbDefaultNamespaceCustomDebugInfo>().FirstOrDefault();
					if(existing==null){
						var created=new dnlib.DotNet.Pdb.PdbDefaultNamespaceCustomDebugInfo{Namespace="drift-namespace"};
						module.CustomDebugInfos.Add(created);testExternalUndo=()=>module.CustomDebugInfos.Remove(created);
					}
					else{
						var oldNamespace=existing.Namespace;existing.Namespace=(string.IsNullOrEmpty(oldNamespace)?"drift-namespace":oldNamespace+"-changed");
						testExternalUndo=()=>existing.Namespace=oldNamespace;
					}
					return 0;
				}
				var old=module.Name;module.Name=old+".external";testExternalUndo=()=>module.Name=old;return 0;});
			testExternalTransactionId=tx.Id;testExternalOriginalFingerprint=before;after=tx.Workspace.CurrentLiveFingerprint();restored=false;
		}else{
			if(testExternalUndo==null||testExternalTransactionId!=tx.Id)throw new ArgumentException("no matching persistent external mutation is active","case_id");
			tx.Workspace.OnLive(()=>{testExternalUndo();return 0;});testExternalUndo=null;testExternalTransactionId=null;after=tx.Workspace.CurrentLiveFingerprint();restored=after==testExternalOriginalFingerprint;testExternalOriginalFingerprint=null;
		}
		var artifactRoot=settings.CurrentSnapshot?.ArtifactRoot;if(string.IsNullOrWhiteSpace(artifactRoot))throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("artifact_root","ArtifactRoot is not configured"));var artifactDirectory=Path.Combine(artifactRoot,"edit-tests","fingerprint");Directory.CreateDirectory(artifactDirectory);var artifactPath=Path.Combine(artifactDirectory,"dnspy-edit-"+caseId.Replace(':','-')+"-"+Guid.NewGuid().ToString("N")+".json");var artifactJson=JsonSerializer.Serialize(new{case_id=caseId,before,after,restored});File.WriteAllText(artifactPath,artifactJson);
		return EditWire.Success(state,new Dictionary<string,object?>{{"case_id",caseId},{"recipe_id","live-conflict"},{"component","ModuleMetadata"},{"recipe_sha256",EditWire.Sha256(Encoding.UTF8.GetBytes("live-conflict-v1"))},{"evidence_artifact",new Dictionary<string,object?>{{"path",artifactPath},{"sha256",EditWire.Sha256(Encoding.UTF8.GetBytes(artifactJson))}}},{"located_slice_before",before},{"located_slice_after",after},{"raw_order_before",before},{"raw_order_after",after},{"canonical_readback_before",before},{"canonical_readback_after",after},{"before_fingerprint",before},{"after_fingerprint",after},{"restored_fingerprint",restored?after:before},{"guard_before",tx.Workspace.BaselineExternalGuard},{"guard_after",tx.Workspace.CurrentExternalGuard()},{"changed",tx.Workspace.CurrentExternalGuard()!=tx.Workspace.BaselineExternalGuard},{"semantic_change",caseId is not ("live-conflict:mutate-entrypoint" or "live-conflict:mutate-layout" or "live-conflict:mutate-cdi")},{"restored",restored}});
	}

	Dictionary<string, object?> MigrateValidated(EditLoadedLineage lineage, EditReplayAssessment target,
		ModuleDef live, string liveFingerprint) {
		if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
		var liveGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live));
		var liveImage = EditWorkspace.OnDispatcher(() => EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)));
		var current = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, liveFingerprint);
		if (current.Classification != "exact" || current.SemanticFingerprint != EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeRoundtrip(live)) || current.ImageSha256 != liveImage)
			throw new EditDomainException("EDIT_LINEAGE_DIVERGED");
		var navigationPlan = history.PlanNavigation(lineage, lineage.Manifest.HeadCheckpointId, target.Checkpoint.CheckpointId);
		EditPreparedHistoryWrite? prepared = null;
		Action? inverse = null;
		var liveApplied = false;
		var postActionFingerprint = string.Empty;
		try {
			lock (gate) state = "committing";
			StorageFault("prewrite"); prepared = history.PrepareMigration(target, target.Bytes, target.ReplayId);
			StorageFault("readback"); inverse = EditWorkspace.OnDispatcher(() => {
				if (EditFingerprint.Compute(live) != liveFingerprint || dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle)
					throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				return navigationPlan.Apply(live);
			});
			StorageFault("navigate_forward");
			StorageFault("navigate_inverse");
			var actual = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeRoundtrip(live));
			var image = EditWorkspace.OnDispatcher(() => EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)));
			if (actual != target.SemanticFingerprint || image != target.ImageSha256) throw new EditDomainException("EDIT_VALIDATION_FAILED");
			postActionFingerprint = EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(live));
			liveApplied = true; StorageFault("finalize"); history.Finalize(prepared, live); lock (gate) state = "idle";
			return EditWire.Success("idle", new Dictionary<string, object?> { ["replay"] = ReplayResult(target),
				["history"] = LineageResult(prepared.Lineage),
				["migration_checkpoint"] = CheckpointResult(prepared.Lineage, prepared.PostHeadCheckpointId),
				["from_checkpoint_id"] = lineage.Manifest.HeadCheckpointId, ["to_checkpoint_id"] = prepared.PostHeadCheckpointId });
		}
		catch (EditStagedCleanupException ex) {
			EnterCleanupRecovery(live, null, ex.Prepared, "migration", liveFingerprint, ex.OriginalFailure);
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
		catch (Exception ex) when (prepared != null && liveApplied) {
			partial = new PartialCommit {
				RecoveryId = EditWire.NewId("recovery"), Kind = "checkpoint_finalize", OperationKind = "migration",
				Prepared = prepared, LiveModule = live, LiveUndo = inverse == null ? new List<Action>() : new List<Action> { inverse },
				PreLiveFingerprint = liveFingerprint, PostLiveFingerprint = postActionFingerprint,
				PreExternalGuard = liveGuard,
				PostExternalGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live)),
				OriginalFailure = ex is EditDomainException domain ? domain.Code : ex.GetType().Name,
				AllowedActions = new[] { "retry_checkpoint", "undo_live" },
			};
			lock (gate) state = "committed_without_checkpoint";
			throw new EditDomainException("EDIT_CHECKPOINT_COMMIT_FAILED", RecoveryResult(partial));
		}
		catch (Exception ex) {
			if (inverse != null) {
				try {
					EditWorkspace.OnDispatcher(() => { if (navigateInverseFailure) { navigateInverseFailure = false; throw new EditDomainException("EDIT_CHECKPOINT_COMMIT_FAILED", new Dictionary<string, object?> { ["kind"] = "injected_storage_fault", ["stage"] = "navigate_inverse" }); } inverse(); return 0; });
					if (EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(live)) != liveFingerprint) throw new InvalidOperationException("migration inverse fingerprint mismatch");
				}
				catch {
					lock (gate) state = "live_state_unknown";
					throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("migration inverse failed"));
				}
			}
			if (ex is EditDomainException { Code: "EDIT_LIVE_STATE_UNKNOWN" }) {
				lock (gate) state = "live_state_unknown";
				throw;
			}
			if (prepared != null) CleanupPreparedOrEnterRecovery(live, prepared, "migration", liveFingerprint,
				ex is EditDomainException domain ? domain.Code : ex.GetType().Name);
			lock (gate) state = "idle"; throw;
		}
	}

	Dictionary<string, object?> Navigate(McpCallContext context, EditLoadedLineage lineage, string fromId,
		string targetId, string operationKind, EditReplayAssessment? existingAssessment = null) {
		if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
		var live = RequireLoadedModule(lineage.Manifest.SourceIdentity.OriginMvid); var liveFingerprint = EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(live));
		var liveGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live));
		var liveImage = EditWorkspace.OnDispatcher(() => EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)));
		var from = history.Assess(lineage.Manifest.LineageId, fromId, liveFingerprint);
		if (from.Classification != "exact" || from.SemanticFingerprint != EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeRoundtrip(live)) || from.ImageSha256 != liveImage)
			throw new EditDomainException("EDIT_LINEAGE_DIVERGED");
		var target = existingAssessment ?? history.Assess(lineage.Manifest.LineageId, targetId, liveFingerprint);
		if (target.Classification == "validated_drift") throw new EditDomainException("EDIT_REPLAY_CONFIRMATION_REQUIRED",
			new Dictionary<string, object?> { ["kind"] = "replay_confirmation", ["replay"] = ReplayResult(target) });
		if (target.Classification == "unverified_drift") throw new EditDomainException("EDIT_REPLAY_UNVERIFIED");
		if (target.Classification != "exact") throw new EditDomainException("EDIT_OPERATION_VERSION_UNSUPPORTED");
		var navigationPlan = history.PlanNavigation(lineage, fromId, targetId);
		EditPreparedHistoryWrite? prepared = null;
		Action? inverse = null;
		var liveApplied = false;
		var postActionFingerprint = string.Empty;
		try {
			lock (gate) state = "committing";
			StorageFault("prewrite");
			prepared = history.PrepareHeadMove(lineage.Manifest.LineageId, fromId, targetId, operationKind);
			StorageFault("readback");
			inverse = EditWorkspace.OnDispatcher(() => {
				if (EditFingerprint.Compute(live) != liveFingerprint || dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle)
					throw new EditDomainException("EDIT_HISTORY_CONFLICT");
				return navigationPlan.Apply(live);
			});
			StorageFault("navigate_forward");
			StorageFault("navigate_inverse");
			var actual = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeRoundtrip(live)); var image = EditWorkspace.OnDispatcher(() => EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)));
			if (actual != target.SemanticFingerprint || image != target.ImageSha256) throw new EditDomainException("EDIT_VALIDATION_FAILED");
			postActionFingerprint = EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(live));
			liveApplied = true;
			StorageFault("finalize"); history.Finalize(prepared, live); lock (gate) state = "idle";
			return EditWire.Success("idle", new Dictionary<string, object?> { ["from_checkpoint_id"] = fromId, ["to_checkpoint_id"] = targetId,
				["history"] = LineageResult(prepared.Lineage), ["replay"] = ReplayResult(target) });
		}
		catch (EditStagedCleanupException ex) {
			EnterCleanupRecovery(live, null, ex.Prepared, operationKind, liveFingerprint, ex.OriginalFailure);
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
		catch (Exception ex) when (prepared != null && liveApplied) {
			partial = new PartialCommit {
				RecoveryId = EditWire.NewId("recovery"), Kind = "checkpoint_finalize", OperationKind = operationKind,
				Prepared = prepared, LiveModule = live, LiveUndo = inverse == null ? new List<Action>() : new List<Action> { inverse },
				PreLiveFingerprint = liveFingerprint, PostLiveFingerprint = postActionFingerprint,
				PreExternalGuard = liveGuard,
				PostExternalGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live)),
				OriginalFailure = ex is EditDomainException domain ? domain.Code : ex.GetType().Name,
				AllowedActions = new[] { "retry_checkpoint", "undo_live" },
			};
			lock (gate) state = "committed_without_checkpoint";
			throw new EditDomainException("EDIT_CHECKPOINT_COMMIT_FAILED", RecoveryResult(partial));
		}
		catch (Exception ex) {
			if (inverse != null) {
				try {
					EditWorkspace.OnDispatcher(() => { if (navigateInverseFailure) { navigateInverseFailure = false; throw new EditDomainException("EDIT_CHECKPOINT_COMMIT_FAILED", new Dictionary<string, object?> { ["kind"] = "injected_storage_fault", ["stage"] = "navigate_inverse" }); } inverse(); return 0; });
					if (EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(live)) != liveFingerprint) throw new InvalidOperationException("navigation inverse fingerprint mismatch");
				}
				catch {
					lock (gate) state = "live_state_unknown";
					throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("navigation inverse failed"));
				}
			}
			if (ex is EditDomainException { Code: "EDIT_LIVE_STATE_UNKNOWN" }) {
				lock (gate) state = "live_state_unknown";
				throw;
			}
			if (prepared != null) CleanupPreparedOrEnterRecovery(live, prepared, operationKind, liveFingerprint,
				ex is EditDomainException domain ? domain.Code : ex.GetType().Name);
			lock (gate) state = "idle";
			throw;
		}
	}

	string ApplyTransactionToLive(Transaction tx, List<Action> inverses, IReadOnlyList<string> pregeneratedInverses) {
		var first = true;
		return tx.Workspace.OnLive(() => {
			var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
			// A rejected preflight has not written live. Never run an inverse
			// for it: that could overwrite the external edit that caused rejection.
			lock (gate) if (tx.CancelRequested && !tx.LiveLinearized) throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
			// CHK-013 / P02 §4 item 4: the SECOND gate, inside the same
			// dispatcher critical section that writes live.  The commit-entry
			// checks can pause at barriers before this section runs; a UI
			// edit or a debugger launch in that window must still be caught
			// HERE, before the first live mutation (zero writes on failure).
			{
				var liveNow = EditFingerprint.Compute(tx.Workspace.LiveModule);
				if (!string.Equals(liveNow, tx.Workspace.BaselineLiveFingerprint, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT",
						new Dictionary<string, object?> { { "kind", "fingerprint_conflict" }, { "stage", "live_apply_second_gate" },
							{ "expected", tx.Workspace.BaselineLiveFingerprint }, { "actual", liveNow } });
				var guardNow = EditFingerprint.ComputeExternalGuard(tx.Workspace.LiveModule);
				if (!string.Equals(guardNow, tx.Workspace.BaselineExternalGuard, StringComparison.Ordinal))
					throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT",
						new Dictionary<string, object?> { { "kind", "external_drift_conflict" }, { "stage", "live_apply_second_gate" },
							{ "expected", tx.Workspace.BaselineExternalGuard }, { "actual", guardNow } });
				if (dynamicGate.EvaluateEditDynamicValidation().State != DebugStates.Idle)
					throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
			}
			try {
				for (var i = 0; i < tx.Workspace.NormalizedOperations.Count; i++) {
					using var document = JsonDocument.Parse(tx.Workspace.NormalizedOperations[i]);
					var outcome = EditOperationRegistry.ApplyPersisted(tx.Workspace.LiveModule, document.RootElement, map, i);
					// CHK-008 repro hook: the operation has mutated live but its
					// Outcome was discarded before the outer list captured the undo.
					StorageFault("live_apply");
					inverses.Add(outcome.Undo);
					if (first) { first = false; lock (gate) tx.LiveLinearized = true; BarrierPoint("commit_after_live_first_mutation", tx.Owner); }
				}
				EditStructuralValidator.Validate(tx.Workspace.LiveModule);
				var actual = EditFingerprint.Compute(tx.Workspace.LiveModule);
				if (actual != tx.PrivateFingerprint) throw new EditDomainException("EDIT_VALIDATION_FAILED",
					EditWorkspace.ValidationDetails("live_private_fingerprint", "live_apply", EditFingerprint.Difference(tx.Workspace.PrivateModule, tx.Workspace.LiveModule)));
				return actual;
			}
			catch {
				var inverseIndex = inverses.Count - 1;
				try {
					// CHK-008: the operation that threw (index inverseIndex + 1) may
					// have mutated live inside its handler before failing.  Its
					// pre-generated compiled inverse — materialized before the first
					// live write — restores the pre-operation state.  A state-check
					// rejection means the handler never reached a live mutation, so
					// skipping it is the correct recovery.
					if (inverseIndex + 1 < pregeneratedInverses.Count) {
						try {
							// Applying the compiled inverse returns the graph to the
							// pre-operation state; its own undo (the forward redo) must
							// NOT be executed — only the fully-applied prefix undos run.
							using var failedOp = JsonDocument.Parse(pregeneratedInverses[inverseIndex + 1]);
							EditOperationRegistry.ApplyCompiledInverse(
								tx.Workspace.LiveModule, failedOp.RootElement, map, inverseIndex + 1);
						}
						catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { }
					}
					for (; inverseIndex >= 0; inverseIndex--) inverses[inverseIndex]();
					if (tx.Workspace.CurrentExternalGuard() != tx.Workspace.BaselineExternalGuard
						|| EditFingerprint.Compute(tx.Workspace.LiveModule) != tx.Workspace.BaselineLiveFingerprint)
						throw new InvalidOperationException("commit inverse fingerprint mismatch");
					// Full recovery returns to an uncommitted transaction. Its next
					// attempt must regain cancellation, expiry and close semantics.
					lock (gate) tx.LiveLinearized = false;
				}
				catch {
					emergencyLiveUndo.Clear();
					for (var i = inverseIndex; i >= 0; i--) emergencyLiveUndo.Add(inverses[i]);
					lock (gate) state = "live_state_unknown";
					throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN");
				}
				throw;
			}
		});
	}

	Dictionary<string, object?> TestStorageFault(Dictionary<string, object>? args) {
		RequireTest(); var action = EditWire.String(args, "action");
		if (action == "arm") { var stage = EditWire.String(args, "stage"); if (stage is not ("prewrite" or "readback" or "finalize" or "cleanup" or "navigate_forward" or "navigate_inverse" or "live_apply")) throw new ArgumentException("unknown storage stage", "stage"); armedStorageFault = stage; if (stage == "navigate_inverse") navigateInverseFailure = true; }
		else if (action == "reset") { armedStorageFault = null; navigateInverseFailure = false; }
		else throw new ArgumentException("action must be arm or reset", "action");
		return EditWire.Success(state, new Dictionary<string, object?> { ["action"] = action, ["stage"] = armedStorageFault, ["armed"] = armedStorageFault != null });
	}

	Dictionary<string, object?> TestLineageMutation(Dictionary<string, object>? args, McpCallContext context) {
		RequireTest(); RequireOwnerContext(context); RequireIdleForHistoryMutation();
		var action = EditWire.String(args, "action"); var assembly = EditWire.String(args, "assembly_name"); var mvid = OptionalArgument(args, "module_mvid");
		var module = EditWorkspace.OnDispatcher(() => tree.GetAllModuleNodes().Select(n => n.Document?.ModuleDef).Where(x => x != null).Cast<ModuleDef>()
			.SingleOrDefault(x => string.Equals(x.Assembly?.Name, assembly, StringComparison.OrdinalIgnoreCase)
				&& (mvid == null || string.Equals(x.Mvid?.ToString("D"), mvid, StringComparison.OrdinalIgnoreCase))))
			?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE");
		var before = EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(module));
		if (action == "mutate") {
			if (testExternalUndo != null) throw new ArgumentException("a test mutation is already active", "action");
			EditWorkspace.OnDispatcher(() => { var old = module.Name; module.Name = old + ".lineage"; testExternalUndo = () => module.Name = old; return 0; });
			testExternalOriginalFingerprint = before;
		}
		else if (action == "restore") {
			if (testExternalUndo == null) throw new ArgumentException("no test mutation is active", "action");
			EditWorkspace.OnDispatcher(() => { testExternalUndo(); return 0; }); testExternalUndo = null;
		}
		else throw new ArgumentException("action must be mutate or restore", "action");
		var after = EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(module)); var restored = action == "restore" && after == testExternalOriginalFingerprint;
		if (restored) testExternalOriginalFingerprint = null;
		return EditWire.Success(state, new Dictionary<string, object?> { ["action"] = action, ["before_fingerprint"] = before, ["after_fingerprint"] = after, ["restored"] = restored });
	}

	void ThrowIfCanceledBeforeLinearization(Transaction tx) {
		lock (gate) if (tx.CancelRequested && !tx.LiveLinearized) throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
	}

	void CleanupPreparedOrEnterRecovery(Transaction tx, EditPreparedHistoryWrite prepared, string failure) {
		try {
			StorageFault("cleanup"); history.DeleteOwnedTemp(prepared);
			lock (gate) { tx.OperationBusy = false; if (ReferenceEquals(active, tx)) EndLocked(tx, failure); state = "idle"; }
		}
		catch {
			partial = new PartialCommit {
				RecoveryId = EditWire.NewId("recovery"), Kind = "aborted_temp_cleanup", OperationKind = "commit",
				Prepared = prepared, LiveModule = tx.Workspace.LiveModule, Workspace = tx.Workspace, PreLiveFingerprint = tx.Workspace.BaselineLiveFingerprint,
				PostLiveFingerprint = tx.Workspace.BaselineLiveFingerprint,
				PreExternalGuard = tx.Workspace.BaselineExternalGuard, PostExternalGuard = tx.Workspace.BaselineExternalGuard,
				OriginalFailure = failure,
				AllowedActions = new[] { "cleanup_temp" },
			};
			lock (gate) { active = null; state = "committing"; }
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
	}

	void CleanupPreparedOrEnterRecovery(ModuleDef live, EditPreparedHistoryWrite prepared, string operationKind,
		string preLiveFingerprint, string failure) {
		try {
			StorageFault("cleanup"); history.DeleteOwnedTemp(prepared);
		}
		catch {
			var preGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live));
			partial = new PartialCommit {
				RecoveryId = EditWire.NewId("recovery"), Kind = "aborted_temp_cleanup", OperationKind = operationKind,
				Prepared = prepared, LiveModule = live, PreLiveFingerprint = preLiveFingerprint,
				PostLiveFingerprint = preLiveFingerprint,
				PreExternalGuard = preGuard, PostExternalGuard = preGuard, OriginalFailure = failure,
				AllowedActions = new[] { "cleanup_temp" },
			};
			lock (gate) state = "committing";
			throw new EditDomainException("EDIT_CHECKPOINT_CLEANUP_FAILED", RecoveryResult(partial));
		}
	}

	void EnterCleanupRecovery(ModuleDef live, EditWorkspace? workspace, EditPreparedHistoryWrite prepared,
		string operationKind, string preLiveFingerprint, string failure) {
		// CHK-017: cleanup recovery also binds the external-drift guard of the
		// pre state; cleanup_temp must refuse when anything drifted meanwhile.
		var cleanupGuard = EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(live));
		partial = new PartialCommit {
			RecoveryId = EditWire.NewId("recovery"), Kind = "aborted_temp_cleanup", OperationKind = operationKind,
			Prepared = prepared, LiveModule = live, Workspace = workspace,
			PreLiveFingerprint = preLiveFingerprint, PostLiveFingerprint = preLiveFingerprint,
			PreExternalGuard = cleanupGuard, PostExternalGuard = cleanupGuard,
			OriginalFailure = failure, AllowedActions = new[] { "cleanup_temp" },
		};
		lock (gate) state = "committing";
	}

	void TryDeletePrepared(EditPreparedHistoryWrite prepared) { try { history.DeleteOwnedTemp(prepared); } catch { } }
	static string CurrentLiveFingerprint(PartialCommit value) =>
		EditWorkspace.OnDispatcher(() => EditFingerprint.Compute(value.LiveModule));
	static string CurrentExternalGuard(PartialCommit value) =>
		EditWorkspace.OnDispatcher(() => EditFingerprint.ComputeExternalGuard(value.LiveModule));
	void RememberResolvedRecovery(string key, Dictionary<string, object?> envelope) {
		lock (gate) {
			if (!resolvedRecoveries.ContainsKey(key)) resolvedRecoveryOrder.Enqueue(key);
			resolvedRecoveries[key] = EditWire.CanonicalPayload(envelope);
			while (resolvedRecoveryOrder.Count > 128) resolvedRecoveries.Remove(resolvedRecoveryOrder.Dequeue());
		}
	}
	void StorageFault(string stage) {
		if (!string.Equals(armedStorageFault, stage, StringComparison.Ordinal)) return;
		armedStorageFault = null; throw new EditDomainException(stage == "cleanup" ? "EDIT_CHECKPOINT_CLEANUP_FAILED" : "EDIT_CHECKPOINT_COMMIT_FAILED",
			new Dictionary<string, object?> { ["kind"] = "injected_storage_fault", ["stage"] = stage });
	}

	void RequireIdleForHistoryMutation() {
		lock (gate) {
			if (state == "live_state_unknown") throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN");
			if (active != null) throw new EditDomainException("EDIT_TRANSACTION_BUSY");
			if (partial != null || state != "idle") throw new EditDomainException("EDIT_TRANSACTION_BUSY");
		}
	}

	ModuleDef? FindLoadedModule(string mvid) => EditWorkspace.OnDispatcher(() => {
		var matches = tree.GetAllModuleNodes().Select(n => n.Document?.ModuleDef).Where(x => x != null).Cast<ModuleDef>()
			.Where(x => string.Equals(x.Mvid?.ToString("D"), mvid, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
		return matches.Length == 1 ? matches[0] : null;
	});
	ModuleDef RequireLoadedModule(string mvid) => FindLoadedModule(mvid) ?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
		Capability("loaded_module", "The lineage source module is not uniquely loaded"));

	static string? OptionalArgument(Dictionary<string, object>? args, string name) {
		if (args == null || !args.TryGetValue(name, out var raw) || raw == null || raw is JsonElement { ValueKind: JsonValueKind.Null }) return null;
		if (raw is string value) return value;
		if (raw is JsonElement element && element.ValueKind == JsonValueKind.String) return element.GetString();
		throw new ArgumentException(name + " must be a string", name);
	}

	Dictionary<string, object?> SafeHistorySummary() {
		try { return new Dictionary<string, object?> { ["lineage_count"] = history.LoadAll().Count, ["available"] = true }; }
		catch (Exception ex) { return new Dictionary<string, object?> { ["available"] = false, ["error"] = ex.GetType().Name }; }
	}
	Dictionary<string, object?> SafeHistoryCapacity() {
		try { return history.CapacityView(); }
		catch { return new Dictionary<string, object?>(); }
	}
	static Dictionary<string, object?> MergeCapacity(Dictionary<string, object?> left, Dictionary<string, object?> right) {
		var result = new Dictionary<string, object?>(left, StringComparer.Ordinal); foreach (var row in right) result[row.Key] = row.Value; return result;
	}
	static Dictionary<string, object?> HistoryBindingResult(EditHistoryBinding binding) => new() {
		["family_id"] = binding.FamilyId, ["lineage_id"] = binding.LineageId,
		["base_checkpoint_id"] = binding.BaseCheckpointId, ["new_family"] = binding.IsNewFamily,
		["match_basis"] = binding.MatchBasis,
	};
	static Dictionary<string, object?> LineageResult(EditLoadedLineage lineage) => new() {
		["lineage_id"] = lineage.Manifest.LineageId, ["family_id"] = lineage.Manifest.FamilyId,
		["head_checkpoint_id"] = lineage.Manifest.HeadCheckpointId, ["checkpoint_count"] = lineage.Manifest.Checkpoints.Count,
		["superseded_lineage_id"] = lineage.Manifest.SupersededLineageId,
	};
	static Dictionary<string, object?> CheckpointResult(EditLoadedLineage lineage, string id) {
		var row = lineage.Checkpoint(id); return new Dictionary<string, object?> {
			["checkpoint_id"] = row.CheckpointId, ["parent_checkpoint_id"] = row.ParentCheckpointId,
			["kind"] = row.Kind, ["sequence"] = row.Sequence, ["result_image_sha256"] = row.ResultImageSha256,
			["result_semantic_fingerprint"] = row.ResultSemanticFingerprint,
		};
	}
	static Dictionary<string, object?> ReplayResult(EditReplayAssessment replay) => new() {
		["replay_id"] = replay.ReplayId, ["classification"] = replay.Classification,
		["lineage_id"] = replay.Lineage.Manifest.LineageId, ["checkpoint_id"] = replay.Checkpoint.CheckpointId,
		["image_sha256"] = replay.ImageSha256, ["semantic_fingerprint"] = replay.SemanticFingerprint,
		["recorded_image_sha256"] = replay.Checkpoint.ResultImageSha256,
		["recorded_semantic_fingerprint"] = replay.Checkpoint.ResultSemanticFingerprint,
	};
	static Dictionary<string, object?> OutputResult(EditOutputResult output) => new() {
		["path"] = output.Path, ["length"] = output.Length, ["sha256"] = output.Sha256, ["file_id"] = output.FileId,
	};
	static object? RecoveryResult(PartialCommit? value) => value == null ? null : new Dictionary<string, object?> {
		["recovery_id"] = value.RecoveryId, ["recovery_kind"] = value.Kind, ["operation_kind"] = value.OperationKind,
		["allowed_actions"] = value.AllowedActions, ["pre_live_fingerprint"] = value.PreLiveFingerprint,
		["post_live_fingerprint"] = value.PostLiveFingerprint, ["pre_head_checkpoint_id"] = value.Prepared.PreHeadCheckpointId,
		["post_head_checkpoint_id"] = value.Prepared.PostHeadCheckpointId,
		["temp"] = new Dictionary<string, object?> { ["path"] = value.Prepared.Temp.Path, ["file_id"] = value.Prepared.Temp.FileId,
			["length"] = value.Prepared.Temp.Length, ["sha256"] = value.Prepared.Temp.Sha256 },
		["original_failure"] = value.OriginalFailure,
	};

	public void OnSessionClosed(McpTransportSessionClosed closed) { lock (gate) {
		if(pendingBeginSessions.Contains(closed.SessionId))closedPendingBeginSessions.Add(closed.SessionId);
		ReleaseBarrierLocked(closed.SessionId);
		if (active?.Owner == closed.SessionId) {
			if (active.CommitStarted && active.LiveLinearized) active.CancelRequested = false;
			else if (active.OperationBusy) { active.CancelRequested = true; active.OwnerClosed = true; }
			else EndLocked(active, closed.Reason);
		}
		beginCache.RemovePrefix(closed.SessionId + ":"); commandCache.RemovePrefix(closed.SessionId + ":"); terminalCache.RemoveSession(closed.SessionId);
	} }
	void ExpireLocked() {
		// Before linearization a parked operation may expire and release its
		// waiter. After the first live write, commit/recovery owns the state;
		// even a test barrier must not expose idle or admit another transaction.
		if (active == null || active.LiveLinearized) return;
		var parkedAtBarrier = testBarrier != null && testBarrier.Entered;
		if ((!active.OperationBusy || parkedAtBarrier) && Now - active.LastActivity >= EditWire.IdleTimeoutMs)
			EndLocked(active, "timeout");
	}

	void EndLocked(Transaction tx, string reason) { if (!ReferenceEquals(active, tx)) return; tx.CancelRequested=true;ReleaseBarrierLocked(tx.Owner);tx.ApplyCache.Clear();tx.ReviewCache.Clear();active = null; state = "idle";if(!tx.OperationBusy)tx.Workspace.Dispose(); }

	Transaction RequireTransactionLocked(Dictionary<string, object>? args, McpCallContext context) { RequireOwnerContext(context); if (active == null || EditWire.String(args,"transaction_id") != active.Id) throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND"); if (active.Owner != context.AuthoritativeSessionId) throw new EditDomainException("EDIT_OWNER_MISMATCH"); return active; }
	static void RequireOwnerContext(McpCallContext context) { if (!context.CanOwnEditTransaction) throw new EditDomainException("EDIT_OWNER_REQUIRED"); }
	static void EnsureLiveUnchanged(Transaction tx,string actual) { if(actual!=tx.Workspace.BaselineLiveFingerprint)throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT",new Dictionary<string,object?>{{"kind","fingerprint_conflict"},{"expected",tx.Workspace.BaselineLiveFingerprint},{"actual",actual}}); }
	/// <summary>CHK-003 / CON-004: full-coverage external-drift guard.  Layers the
	/// semantic fingerprint with entry point/AssemblyRef/Win32/CDI rows so a dnSpy
	/// UI edit confined to those tables is still detected before review/commit.
	/// Never feeds stored checkpoint values — those keep the frozen semantic
	/// fingerprint.</summary>
	static void EnsureExternalUnchanged(Transaction tx,string actual) { if(actual!=tx.Workspace.BaselineExternalGuard)throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT",new Dictionary<string,object?>{{"kind","external_drift_conflict"},{"expected",tx.Workspace.BaselineExternalGuard},{"actual",actual}}); }
	static EditDomainException Revision(uint expected,uint actual)=>new("EDIT_REVISION_CONFLICT",new Dictionary<string,object?>{{"kind","revision_conflict"},{"expected",expected},{"actual",actual}});
	static void CapacityError(string resource,long current,long maximum)=>throw new EditDomainException("EDIT_CAPACITY_EXCEEDED",CapacityDetails(resource,current,maximum));
	static object CapacityDetails(string resource,long current,long maximum)=>new Dictionary<string,object?>{{"kind","capacity"},{"limit",resource},{"current",current},{"maximum",maximum}};
	static object Internal(string reason)=>new Dictionary<string,object?>{{"kind","internal"},{"correlation_id",EditWire.NewId("incident")},{"reason",reason}};
	static object Capability(string capability,string reason)=>new Dictionary<string,object?>{{"kind","capability"},{"capability",capability},{"reason",reason}};
	static string PayloadHash(Dictionary<string,object>? args)=>EditWire.Sha256(Encoding.UTF8.GetBytes(EditWire.CanonicalPayload(args)));
	static Dictionary<string,object?> ParseEnvelope(string json)=>JsonSerializer.Deserialize<Dictionary<string,object?>>(json)??new();
	long Now=>testClockActive ? testOffset : clock.ElapsedMilliseconds;
	static bool TestMode=>string.Equals(Environment.GetEnvironmentVariable("DNMCP_TEST"),"1",StringComparison.Ordinal);
	static void RequireTest(){if(!TestMode)throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("test_mode","DNMCP_TEST=1 is required"));}

	static Dictionary<string,object?> TransactionResult(Transaction tx)=>TransactionResult(tx,tx.LastActivity,tx.Revision,tx.ReviewRevision);
	static Dictionary<string,object?> TransactionResult(Transaction tx,long activity,uint revision,uint? reviewRevision)=>new(){{"transaction_id",tx.Id},{"work_revision",revision},{"review_revision",reviewRevision},{"operation_count",tx.Workspace.NormalizedOperations.Count},{"started_at_monotonic_ms",tx.Started},{"last_activity_monotonic_ms",activity}};
	static Dictionary<string,object?> SourceResult(EditWorkspace w)=>new(){{"assembly_name",w.AssemblyName},{"module_name",w.ModuleName},{"mvid",(w.LiveModule.Mvid?.ToString("D")??string.Empty).ToLowerInvariant()},{"file_path",w.FilePath},{"file_sha256",w.FileSha256},{"live_fingerprint",w.BaselineLiveFingerprint}};
	static Dictionary<string,object?> Fingerprints(Transaction tx)=>new(){{"baseline_live",tx.Workspace.BaselineLiveFingerprint},{"current_live",tx.CurrentLiveFingerprint},{"private",tx.PrivateFingerprint}};
	static Dictionary<string,object?> Limits()=>new(){{"max_operations",EditWire.MaxOperations},{"max_object_ids",EditWire.MaxObjectIds},{"max_normalized_operation_bytes",EditWire.MaxNormalizedOperationBytes},{"max_diff_bytes",EditWire.MaxDiffBytes},{"max_body_instructions",EditWire.MaxBodyInstructions},{"max_body_locals",EditWire.MaxBodyLocals},{"max_body_exception_handlers",EditWire.MaxBodyExceptionHandlers},{"max_live_steps",EditWire.MaxLiveSteps},{"max_module_bytes",EditWire.MaxModuleBytes},{"max_metadata_rows",EditWire.MaxMetadataRows},{"max_resource_bytes",EditWire.MaxResourceBytes},{"max_pdb_bytes",EditWire.MaxPdbBytes},{"max_il_instructions",EditWire.MaxIlInstructions},{"max_dispatcher_ms",EditWire.MaxDispatcherMs}};
	static Dictionary<string,object?> Capacity(Transaction tx)=>new(){{"operations",Meter(tx.Workspace.NormalizedOperations.Count,EditWire.MaxOperations)},{"object_ids",Meter(tx.Workspace.ObjectIds.Count,EditWire.MaxObjectIds)},{"normalized_operation_bytes",Meter(tx.Workspace.NormalizedOperations.Sum(Encoding.UTF8.GetByteCount),EditWire.MaxNormalizedOperationBytes)},{"diff_bytes",Meter(EditWire.Utf8Bytes(tx.Workspace.Diffs),EditWire.MaxDiffBytes)},{"apply_cache_entries",Meter(tx.ApplyCache.Count,EditWire.ApplyCacheEntries)},{"apply_cache_bytes",Meter(tx.ApplyCache.Bytes,EditWire.ApplyCacheBytes)},{"review_tombstone_entries",Meter(tx.ReviewCache.TombstoneCount,EditWire.ReviewTombstoneEntries)},{"review_tombstone_bytes",Meter(tx.ReviewCache.TombstoneBytes,EditWire.ReviewTombstoneBytes)}};
	static Dictionary<string,object?> CapacityAfterApply(Transaction tx)=>new(){{"operations",Meter(tx.Workspace.NormalizedOperations.Count,EditWire.MaxOperations)},{"object_ids",Meter(tx.Workspace.ObjectIds.Count,EditWire.MaxObjectIds)},{"normalized_operation_bytes",Meter(tx.Workspace.NormalizedOperations.Sum(Encoding.UTF8.GetByteCount),EditWire.MaxNormalizedOperationBytes)},{"diff_bytes",Meter(EditWire.Utf8Bytes(tx.Workspace.Diffs),EditWire.MaxDiffBytes)},{"apply_cache_entries",Meter(tx.ApplyCache.Count+1,EditWire.ApplyCacheEntries)},{"apply_cache_bytes",Meter(tx.ApplyCache.Bytes,EditWire.ApplyCacheBytes)},{"review_tombstone_entries",Meter(0,EditWire.ReviewTombstoneEntries)},{"review_tombstone_bytes",Meter(0,EditWire.ReviewTombstoneBytes)}};
	static Dictionary<string,object?> Meter(long current,long maximum)=>new(){{"current",current},{"maximum",maximum}};
	static object? ReviewSummary(Transaction tx)=>tx.ReviewId==null?null:new Dictionary<string,object?>{{"review_id",tx.ReviewId},{"review_revision",tx.ReviewRevision},{"required_confirmation_ids",tx.Workspace.Risks.Where(r=>Equals(r["confirmation_required"],true)).Select(r=>r["risk_id"]).ToArray()}};
	static object ReviewSummary(string reviewId,uint revision,EditWorkspace workspace)=>new Dictionary<string,object?>{{"review_id",reviewId},{"review_revision",revision},{"required_confirmation_ids",workspace.Risks.Where(r=>Equals(r["confirmation_required"],true)).Select(r=>r["risk_id"]).ToArray()}};
	static Dictionary<string,object?> ValidationResult(int rules)=>new(){{"state","passed"},{"rule_count",rules},{"errors",Array.Empty<object>()}};
	object ExecutionEvidence()=>new Dictionary<string,object?>{{"armed_fault",faultPlan.ArmedObject(lastArmedFault)},{"fault_manifest",faultPlan.Manifest},{"oracle_faults",faultPlan.Manifest},{"actual_mutation_trace",lastMutationTrace},{"covered_faults",lastCoveredFaults}};
	static List<string> OperationKinds(Transaction tx)=>tx.Workspace.NormalizedOperations.Select(x=>{using var d=JsonDocument.Parse(x);return d.RootElement.GetProperty("kind").GetString()!;}).ToList();
	void CaptureEmergency(IReadOnlyList<(string Kind,Action Undo)> inverses,ISet<int> undone){emergencyLiveUndo.Clear();for(int i=inverses.Count-1;i>=0;i--)if(!undone.Contains(i))emergencyLiveUndo.Add(inverses[i].Undo);}
	static void RestoreOutstanding(Transaction tx,IReadOnlyList<(string Kind,Action Undo)> inverses,ISet<int> undone)=>tx.Workspace.OnLive(()=>{for(int i=inverses.Count-1;i>=0;i--)if(!undone.Contains(i)){inverses[i].Undo();undone.Add(i);}return 0;});
	static string[] StringArray(Dictionary<string,object>? args,string name){if(args==null||!args.TryGetValue(name,out var raw)||raw is not JsonElement e||e.ValueKind!=JsonValueKind.Array)throw new ArgumentException(name+" is required",name);return e.EnumerateArray().Select(x=>x.GetString()??string.Empty).ToArray();}
	public void Dispose(){lock(gate){if(active!=null)EndLocked(active,"dispose");if(testBarrier!=null){testBarrier.Released=true;testBarrier.Release.Set();testBarrier.Dispose();testBarrier=null;}history.Dispose();catalog.Dispose();}}
	sealed class ReverseFaultException:Exception{}
	sealed class ForwardFaultException:Exception{}
}
