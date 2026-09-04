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
		public bool OperationBusy;
		public string CurrentLiveFingerprint = string.Empty;
		public string PrivateFingerprint = string.Empty;
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

	[ImportingConstructor]
	public EditTransactionCoordinator(IDocumentTreeView tree, StaticWriteGate staticWriteGate, IEditDynamicValidationGate dynamicGate, McpSettings settings) {
		this.tree = tree; this.staticWriteGate = staticWriteGate; this.dynamicGate = dynamicGate; this.settings = settings;
		dynamicValidation = new EditDynamicValidationService(dynamicGate, settings);
		faultPlan = new EditFaultPlan(catalog.Lowering, catalog.Faults);
		staticWriteGate.CoordinatorStateProvider = () => State == "idle" ? DebugStates.Idle : "editing";
	}

	public string State { get { lock (gate) { ExpireLocked(); return state; } } }
	public JsonElement FaultGolden => catalog.Faults;
	public JsonElement MutationCorpus => catalog.Mutations;

	public CallToolResult Execute(string toolName, Dictionary<string, object>? args, McpCallContext context) {
		var serialized = toolName is not "edit_status" and not "edit_test_clock" and not "edit_test_barrier";
		var acquired = false;
		try {
			if (serialized) {
				var requestKey = RequestKey(toolName, args, context);
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
			lock (gate) {
				ExpireLocked();
				if (state == "live_state_unknown" && toolName is not "edit_status" and not "edit_rollback" and not "edit_test_fault")
					throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN", Internal("live state requires explicit recovery"));
			}
			var envelope = toolName switch {
					"edit_begin" => Begin(args, context),
					"edit_status" => Status(context),
					"edit_apply" => Apply(args, context),
					"edit_review" => Review(args, context),
					"edit_rollback" => Rollback(args, context),
					"edit_test_clock" => TestClock(args),
					"edit_test_barrier" => TestBarrier(args, context),
					"edit_test_fault" => TestFault(args),
					"edit_test_external_mutation" => TestExternalMutation(args, context),
					"edit_test_live_mutation" => TestLiveMutation(args, context),
					"edit_test_apply_and_restore" => TestApplyAndRestore(args, context),
					_ => throw new ArgumentException("Unknown edit tool", nameof(toolName)),
				};
			return EditWire.Result(envelope);
		}
		catch (EditReviewAttemptException ex) { lock (gate) { var failure=EditWire.Failure(state, ex.Code, ex.Details, ex.Message);failure["validation_attempt"]=ex.Attempt;return EditWire.Result(failure); } }
		catch (EditDomainException ex) { lock (gate) { var failure=EditWire.Failure(state, ex.Code, ex.Details, ex.Message);if(toolName=="edit_test_apply_and_restore")failure["execution_evidence"]=ExecutionEvidence();return EditWire.Result(failure); } }
		catch (ArgumentException) { throw; }
		catch (Exception ex) { lock (gate) { var failure=EditWire.Failure(state, "EDIT_INTERNAL_ERROR", Internal(ex.GetType().Name + ": " + ex.Message));if(toolName=="edit_test_apply_and_restore")failure["execution_evidence"]=ExecutionEvidence();return EditWire.Result(failure); } }
		finally { if(acquired){lock(gate)pendingRequestKey=null;operationGate.Release();} }
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
		EditWorkspace? workspace=null;
		try{
			workspace = EditWorkspace.Create(tree, assembly, mvid);
			BarrierPoint("begin_after_copy",session);
			lock(gate)if(closedPendingBeginSessions.Contains(session))throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
		}catch{
			workspace?.Dispose();
			lock(gate){pendingBeginSessions.Remove(session);closedPendingBeginSessions.Remove(session);pendingBeginOwner=null;state="idle";}
			throw;
		}
		// Create() has already proved the private copy and live module have the same complete
		// fingerprint.  Reuse that proven value rather than serializing the module a third time.
		var now = Now; var tx = new Transaction { Id = EditWire.NewId("edit"), Owner = session, Transport = context.TransportKind, Generation = ++generation, Started = now, LastActivity = now, Workspace = workspace, CurrentLiveFingerprint=workspace.BaselineLiveFingerprint, PrivateFingerprint=workspace.BaselineLiveFingerprint };
		lock(gate){pendingBeginSessions.Remove(session);closedPendingBeginSessions.Remove(session);pendingBeginOwner=null;active = tx; state = "editing";}
		var env = EditWire.Success(state, new Dictionary<string, object?> {
			["transaction"] = TransactionResult(tx), ["source"] = SourceResult(workspace), ["fingerprints"] = Fingerprints(tx),
			["limits"] = Limits(), ["capacity"] = Capacity(tx), ["capabilities"] = new Dictionary<string, object?> {
				["operation_kinds"] = EditWire.OperationKinds, ["dynamic_validation"] = true, ["test_apply_restore"] = TestMode,
			},
		});
		var json = EditWire.CanonicalPayload(env);
		try { lock(gate) beginCache.Add(session + ":" + requestId, payload, json); }
		catch { lock(gate) EndLocked(tx, "begin_capacity"); throw; }
		return env;
	}

	Dictionary<string, object?> Status(McpCallContext context) {
		lock(gate){ExpireLocked();if(active==null&&pendingBeginOwner!=null)return EditWire.Success("editing",new Dictionary<string,object?>{{"busy",true},{"state","editing"},{"owner_transport_kind",pendingBeginTransport.ToWireName()}});if (active == null) return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = state!="idle", ["state"] = state });
		if (context.AuthoritativeSessionId != active.Owner) return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = true, ["state"] = state, ["owner_transport_kind"] = active.Transport.ToWireName() });
		return EditWire.Success(state, new Dictionary<string, object?> { ["busy"] = true, ["state"] = state, ["transaction"] = TransactionResult(active), ["fingerprints"] = Fingerprints(active), ["review"] = ReviewSummary(active), ["capacity"] = Capacity(active), ["risks"] = active.Workspace.Risks.ToArray() });}
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
		finally{lock(gate){tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();}}
	}

	Dictionary<string, object?> Review(Dictionary<string, object>? args, McpCallContext context) {
		Transaction tx;uint expected;var requestId=EditWire.String(args,"request_id");var payload=PayloadHash(args);lock(gate){tx = RequireTransactionLocked(args, context);if(tx.ReviewCache.TryReplay(requestId,payload,out var replay,out var stale)){if(stale!=null)throw new EditDomainException("EDIT_REVIEW_STALE");return ParseEnvelope(replay);}expected = checked((uint)EditWire.Integer(args, "expected_revision")); if (expected != tx.Revision) throw Revision(expected, tx.Revision);tx.ReviewCache.EnsureCanReplace();tx.OperationBusy=true;}
		try{BarrierPoint("review_before_validation",tx.Owner);var currentLive=tx.Workspace.CurrentLiveFingerprint();EnsureLiveUnchanged(tx,currentLive);var structuralRules=EditStructuralValidator.Validate(tx.Workspace.PrivateModule); tx.Workspace.ValidateRoundtrip();
		var dynamic = dynamicValidation.Run(tx.Workspace, args);
		lock(gate){if(tx.CancelRequested||!ReferenceEquals(active,tx))throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND");
		var reviewId=EditWire.NewId("review");var activity=tx.LastActivity;var privateFingerprint=tx.PrivateFingerprint;
		var env=EditWire.Success("reviewed", new Dictionary<string, object?> { ["transaction"] = TransactionResult(tx, activity, tx.Revision, tx.Revision), ["review"] = ReviewSummary(reviewId, tx.Revision, tx.Workspace), ["fingerprints"] = new Dictionary<string,object?>{{"baseline_live",tx.Workspace.BaselineLiveFingerprint},{"current_live",currentLive},{"private",privateFingerprint}}, ["diffs"] = tx.Workspace.Diffs.ToArray(), ["structural_validation"] = ValidationResult(structuralRules), ["roundtrip_validation"] = ValidationResult(1), ["dynamic_validation"] = dynamic, ["risks"] = tx.Workspace.Risks.ToArray(), ["limits"] = Limits() });
		var json=EditWire.CanonicalPayload(env);tx.ReviewCache.EnsureResponseFits(json);tx.ReviewCache.Store(requestId,payload,reviewId,tx.Revision,json);
		tx.CurrentLiveFingerprint=currentLive;tx.PrivateFingerprint=privateFingerprint;tx.ReviewId=reviewId;tx.ReviewRevision=tx.Revision;tx.LastActivity=activity;state="reviewed";return env;}}
		finally{lock(gate){tx.OperationBusy=false;if(tx.CancelRequested&&!ReferenceEquals(active,tx))tx.Workspace.Dispose();}}
	}

	Dictionary<string, object?> Rollback(Dictionary<string, object>? args, McpCallContext context) {
		RequireOwnerContext(context);var requestId=EditWire.String(args,"request_id");var payload=PayloadHash(args);var session=context.AuthoritativeSessionId!;
		lock(gate)if(terminalCache.TryReplay(session,requestId,payload,out var replay))return ParseEnvelope(replay);
		Transaction tx;lock(gate)tx = RequireTransactionLocked(args, context); var original = tx.Workspace.BaselineLiveFingerprint; var released = new Dictionary<string, object?> { ["private_modules"] = 1, ["validation_modules"] = 0, ["apply_cache_entries"] = tx.ApplyCache.Count, ["review_slots"] = tx.ReviewId == null ? 0 : 1 };
		var env=EditWire.Success("idle", new Dictionary<string, object?> { ["rolled_back"] = true, ["end_reason"] = "client_rollback", ["original_live_fingerprint"] = original, ["released"] = released });var json=EditWire.CanonicalPayload(env);
		lock(gate){terminalCache.Store(session,requestId,payload,json);EndLocked(tx, "client_rollback");}
		return env;
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
			var restored=tx.Workspace.CurrentLiveFingerprint();if(restored!=tx.Workspace.BaselineLiveFingerprint)throw new EditDomainException("EDIT_LIVE_STATE_UNKNOWN",Internal("emergency cleanup did not restore the live fingerprint"));
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
		var currentLive=tx.Workspace.CurrentLiveFingerprint();EnsureLiveUnchanged(tx,currentLive); var gateResult = dynamicGate.EvaluateEditDynamicValidation(); if (gateResult.State != DebugStates.Idle) throw new EditDomainException("EDIT_DEBUG_NOT_IDLE");
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
		if(caseId is "live-conflict:mutate" or "live-conflict:restore")return TestPersistentExternalMutation(tx,caseId);
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
		if(caseId=="live-conflict:mutate"){
			if(testExternalUndo!=null)throw new ArgumentException("a persistent external mutation is already active","case_id");
			tx.Workspace.OnLive(()=>{var module=tx.Workspace.LiveModule;var old=module.Name;module.Name=old+".external";testExternalUndo=()=>module.Name=old;return 0;});
			testExternalTransactionId=tx.Id;testExternalOriginalFingerprint=before;after=tx.Workspace.CurrentLiveFingerprint();restored=false;
		}else{
			if(testExternalUndo==null||testExternalTransactionId!=tx.Id)throw new ArgumentException("no matching persistent external mutation is active","case_id");
			tx.Workspace.OnLive(()=>{testExternalUndo();return 0;});testExternalUndo=null;testExternalTransactionId=null;after=tx.Workspace.CurrentLiveFingerprint();restored=after==testExternalOriginalFingerprint;testExternalOriginalFingerprint=null;
		}
		var artifactRoot=settings.CurrentSnapshot?.ArtifactRoot;if(string.IsNullOrWhiteSpace(artifactRoot))throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",Capability("artifact_root","ArtifactRoot is not configured"));var artifactDirectory=Path.Combine(artifactRoot,"edit-tests","fingerprint");Directory.CreateDirectory(artifactDirectory);var artifactPath=Path.Combine(artifactDirectory,"dnspy-edit-"+caseId.Replace(':','-')+"-"+Guid.NewGuid().ToString("N")+".json");var artifactJson=JsonSerializer.Serialize(new{case_id=caseId,before,after,restored});File.WriteAllText(artifactPath,artifactJson);
		return EditWire.Success(state,new Dictionary<string,object?>{{"case_id",caseId},{"recipe_id","live-conflict"},{"component","ModuleMetadata"},{"recipe_sha256",EditWire.Sha256(Encoding.UTF8.GetBytes("live-conflict-v1"))},{"evidence_artifact",new Dictionary<string,object?>{{"path",artifactPath},{"sha256",EditWire.Sha256(Encoding.UTF8.GetBytes(artifactJson))}}},{"located_slice_before",before},{"located_slice_after",after},{"raw_order_before",before},{"raw_order_after",after},{"canonical_readback_before",before},{"canonical_readback_after",after},{"before_fingerprint",before},{"after_fingerprint",after},{"restored_fingerprint",restored?after:before},{"changed",after!=before},{"semantic_change",true},{"restored",restored}});
	}

	public void OnSessionClosed(McpTransportSessionClosed closed) { lock (gate) { if(pendingBeginSessions.Contains(closed.SessionId))closedPendingBeginSessions.Add(closed.SessionId);ReleaseBarrierLocked(closed.SessionId);if (active?.Owner == closed.SessionId) EndLocked(active, closed.Reason); beginCache.RemovePrefix(closed.SessionId + ":"); terminalCache.RemoveSession(closed.SessionId); } }
	void ExpireLocked() { if (active != null && Now - active.LastActivity >= EditWire.IdleTimeoutMs) EndLocked(active, "timeout"); }
	void EndLocked(Transaction tx, string reason) { if (!ReferenceEquals(active, tx)) return; tx.CancelRequested=true;ReleaseBarrierLocked(tx.Owner);tx.ApplyCache.Clear();tx.ReviewCache.Clear();active = null; state = "idle";if(!tx.OperationBusy)tx.Workspace.Dispose(); }

	Transaction RequireTransactionLocked(Dictionary<string, object>? args, McpCallContext context) { RequireOwnerContext(context); if (active == null || EditWire.String(args,"transaction_id") != active.Id) throw new EditDomainException("EDIT_TRANSACTION_NOT_FOUND"); if (active.Owner != context.AuthoritativeSessionId) throw new EditDomainException("EDIT_OWNER_MISMATCH"); return active; }
	static void RequireOwnerContext(McpCallContext context) { if (!context.CanOwnEditTransaction) throw new EditDomainException("EDIT_OWNER_REQUIRED"); }
	static void EnsureLiveUnchanged(Transaction tx,string actual) { if(actual!=tx.Workspace.BaselineLiveFingerprint)throw new EditDomainException("EDIT_LIVE_MODULE_CONFLICT",new Dictionary<string,object?>{{"kind","fingerprint_conflict"},{"expected",tx.Workspace.BaselineLiveFingerprint},{"actual",actual}}); }
	static EditDomainException Revision(uint expected,uint actual)=>new("EDIT_REVISION_CONFLICT",new Dictionary<string,object?>{{"kind","revision_conflict"},{"expected",expected},{"actual",actual}});
	static void CapacityError(string resource,long current,long maximum)=>throw new EditDomainException("EDIT_CAPACITY_EXCEEDED",CapacityDetails(resource,current,maximum));
	static object CapacityDetails(string resource,long current,long maximum)=>new Dictionary<string,object?>{{"kind","capacity"},{"limit",resource},{"current",current},{"maximum",maximum}};
	static object Internal(string reason)=>new Dictionary<string,object?>{{"kind","internal"},{"correlation_id",EditWire.NewId("incident")}};
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
	public void Dispose(){lock(gate){if(active!=null)EndLocked(active,"dispose");if(testBarrier!=null){testBarrier.Released=true;testBarrier.Release.Set();testBarrier.Dispose();testBarrier=null;}catalog.Dispose();}}
	sealed class ReverseFaultException:Exception{}
	sealed class ForwardFaultException:Exception{}
}
