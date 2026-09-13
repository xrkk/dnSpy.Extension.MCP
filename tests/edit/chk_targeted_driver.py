#!/usr/bin/env python3
"""CHK-001..010 remediation evidence driver (VM side).  Positive cases for the
independent-verification findings, executed against the real loopback with the
remediated plugin build:

  chk003-drift    entry-point-only external edit is invisible to the frozen
                  semantic fingerprint but review still rejects it
                  (external_drift_conflict via the full external guard).
  chk004-identity entry point CHANGED to a different valid method and an
                  AssemblyRef version actually changed: apply->review->commit->
                  export->reload->launch, review must NOT return conflict.
  chk005-resource a BCL-written container with an EMPTY-NAME entry, a Stream
                  entry and a custom AddResourceData payload: editing a named
                  string preserves every unedited row, the empty name and the
                  Stream type code through commit/export/readback.
  chk007-risks    cross-assembly inbound rename: commit response echoes the
                  full confirmed risk facts including affected_references.
  chk008-inverse  live_apply storage fault between mutation and undo capture:
                  recovery consumes the pre-generated inverse plan, live stays
                  intact, retry succeeds, commit reports the plan binding.

Run on the VM:  python chk_targeted_driver.py --case <case> --run-id <id>
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURES = r"C:\Tools\mcp-repo\tests\fixtures\bin"
IDENTITY_HOST = FIXTURES + r"\chk-remediation\IdentityHost.exe"
RESOURCE_HOST = FIXTURES + r"\chk-remediation\ResourceHost2.dll"
TARGET_HOST = FIXTURES + r"\chk-remediation\TargetHost.dll"
INBOUND_HOST = FIXTURES + r"\chk-remediation\InboundHost.exe"
FAILURES: list[str] = []
PASSES: list[str] = []


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        PASSES.append(name)
        print(f"PASS {name}", flush=True)
    else:
        FAILURES.append(name)
        print(f"FAIL {name} {detail}", flush=True)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        return client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                return json.loads(text[start:])
            except json.JSONDecodeError:
                pass
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:400]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def error_code(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def error_details(envelope: dict) -> dict:
    error = envelope.get("error")
    if isinstance(error, dict):
        details = error.get("details")
        if isinstance(details, dict):
            return details
        data = error.get("data")
        if isinstance(data, dict):
            return data
    return {}


def begin_tx(client: DnSpyClient, assembly: str) -> tuple[str, int]:
    envelope = call(client, "edit_begin", {"assembly_name": assembly, "request_id": rid()})
    row = payload(envelope).get("transaction", {})
    return str(row.get("transaction_id", "")), int(row.get("work_revision", 0))


def apply_op(client: DnSpyClient, tx: str, revision: int, operation: dict) -> tuple[dict, int]:
    envelope = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": operation})
    row = payload(envelope).get("transaction", {})
    if row:
        revision = int(row.get("work_revision", revision))
    return envelope, revision


def review_tx(client: DnSpyClient, tx: str, revision: int) -> tuple[dict, str, list[str]]:
    envelope = call(client, "edit_review", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    row = payload(envelope).get("review", {}) if isinstance(payload(envelope), dict) else {}
    required = [str(r) for r in (row.get("required_confirmation_ids") or [])]
    return envelope, str(row.get("review_id", "")), required


def commit_tx(client: DnSpyClient, tx: str, revision: int, review_id: str, required: list[str]) -> dict:
    return call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})


def case_chk003_drift(client: DnSpyClient) -> None:
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    tx, revision = begin_tx(client, "IdentityHost")
    check("D1 transaction began", bool(tx))
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": "IdentityHostDrift"})

    # the external mutation swaps ONLY the managed entry point: the frozen
    # semantic fingerprint must stay equal (that was the CHK-003 hole) ...
    mutation = call(client, "edit_test_external_mutation", {
        "transaction_id": tx, "case_id": "live-conflict:mutate-entrypoint"})
    row = payload(mutation)
    semantic_unchanged = row.get("before_fingerprint") == row.get("after_fingerprint")
    guard_changed = row.get("guard_before") != row.get("guard_after")
    check("D2 entry swap invisible to semantic fingerprint", semantic_unchanged and guard_changed,
          json.dumps({k: str(row.get(k))[:16] for k in ("before_fingerprint", "after_fingerprint", "guard_before", "guard_after")})[:300])

    # ... and review must still reject via the full external drift guard.
    reviewed, _review_id, _required = review_tx(client, tx, revision)
    code = error_code(reviewed)
    details = error_details(reviewed)
    check("D3 review rejects entry-point drift", code == "EDIT_LIVE_MODULE_CONFLICT"
          and details.get("kind") == "external_drift_conflict", f"{code} {json.dumps(details)[:240]}")

    restored = call(client, "edit_test_live_mutation", {
        "transaction_id": tx, "action": "restore"})
    check("D4 external mutation restored", bool(restored.get("ok")) and bool(payload(restored).get("restored")),
          json.dumps(restored)[:240])
    reviewed, review_id, required = review_tx(client, tx, revision)
    check("D5 review ok after restore", bool(reviewed.get("ok")) and bool(review_id),
          json.dumps(reviewed)[:240])
    committed = commit_tx(client, tx, revision, review_id, required)
    check("D6 commit ok", bool(committed.get("ok")), json.dumps(committed)[:300])


def case_chk004_identity(client: DnSpyClient) -> None:
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    methods = call(client, "list_methods", {"assembly_name": "IdentityHost", "type_full_name": "Program"})
    items = methods.get("Items") or methods.get("items") or []
    def token_of(name: str) -> str:
        for m in items:
            if isinstance(m, dict) and str(m.get("Name") or m.get("name")) == name:
                return f"0x{int(m.get('Token') or m.get('token')):08x}"
        return ""
    alt_token = token_of("AltEntry")
    check("I1 AltEntry listed", bool(alt_token), json.dumps(methods)[:200])

    tx, revision = begin_tx(client, "IdentityHost")
    check("I2 transaction began", bool(tx))
    ops = [
        {"kind": "entry_point_set", "entry_point": {"token": alt_token}},
        {"kind": "assembly_update", "name": "IdentityHostChk", "version": "3.4.5.6"},
        {"kind": "assembly_ref_update", "target": {"token": "0x23000001"}, "version": "4.0.1.0"},
    ]
    for op in ops:
        envelope, revision = apply_op(client, tx, revision, op)
        check(f"I3 apply {op['kind']}", bool(envelope.get("ok")), json.dumps(envelope)[:300])

    reviewed, review_id, required = review_tx(client, tx, revision)
    check("I4 review accepts changed identity", bool(reviewed.get("ok")) and bool(review_id),
          f"code={error_code(reviewed)} {json.dumps(reviewed)[:300]}")

    committed = commit_tx(client, tx, revision, review_id, required)
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    live_recovery = commit_row.get("live_recovery", {}) if isinstance(commit_row, dict) else {}
    check("I5 commit ok", bool(committed.get("ok")) and bool(lineage_id), json.dumps(committed)[:300])
    check("I6 commit reports inverse plan", bool(live_recovery.get("inverse_plan_complete_before_live_write"))
          and int(live_recovery.get("inverse_plan_operations", 0) or 0) >= len(ops),
          json.dumps(live_recovery)[:240])

    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
        "output_path": "edit-output\\chk-remediation\\IdentityHost-chk004.exe"})
    output_row = payload(exported).get("output", {})
    export_path = str(output_row.get("path", ""))
    sha256 = str(output_row.get("sha256", ""))
    check("I7 export ok", bool(exported.get("ok")) and export_path.lower().endswith(".exe"),
          json.dumps(exported)[:240])
    call(client, "open_files", {"paths": [export_path]})

    import hashlib as _hashlib4
    import shutil as _shutil4
    launch_target = r"C:\Tools\MefCheck\chk004-IdentityHost.exe"
    _shutil4.copyfile(export_path, launch_target)
    sha256 = _hashlib4.sha256(open(launch_target, "rb").read()).hexdigest()
    launch = call(client, "debug_launch", {
        "request_id": rid(), "target_path": launch_target, "expected_sha256": sha256,
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "entry"})
    launch_row = payload(launch)
    session_id = str(launch_row.get("session_id", ""))
    generation = int(launch_row.get("generation", 0))
    check("I8 debug launch", bool(session_id), json.dumps(launch)[:300])
    paused, frames = False, []
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline and not paused:
        status_env = call(client, "debug_status", {"session_id": session_id})
        paused = payload(status_env).get("state") == "paused" or (status_env.get("debug_context") or {}).get("state") == "paused"
        time.sleep(0.5)
    for _attempt in range(3):
        frames = []
        status_env = call(client, "debug_status", {"session_id": session_id})
        epoch = int((status_env.get("debug_context") or {}).get("pause_epoch", 0))
        threads = payload(call(client, "debug_list_threads", {
            "session_id": session_id, "generation": generation, "pause_epoch": epoch}))
        for thread in threads.get("items", []):
            stack = payload(call(client, "debug_get_stack", {
                "session_id": session_id, "generation": generation, "pause_epoch": epoch,
                "thread_handle": thread.get("thread_handle")}))
            frames.extend(stack.get("items", []))
        if frames:
            break
        time.sleep(1.0)
    matching = next((f for f in frames if isinstance(f, dict)
                     and str((f.get("location") or {}).get("method_token", "")).casefold() == alt_token.casefold()), None)
    check("I9 entry pause on AltEntry", matching is not None, json.dumps(frames)[:400])
    call(client, "debug_terminate", {"session_id": session_id, "generation": generation, "request_id": rid()})


def case_chk005_resource(client: DnSpyClient) -> None:
    call(client, "open_files", {"paths": [RESOURCE_HOST]})
    tx, revision = begin_tx(client, "ResourceHost2")
    check("R1 transaction began", bool(tx))
    envelope, revision = apply_op(client, tx, revision, {
        "kind": "managed_resource_update", "target": {"name": "ResourceHost2.Strings2.resources"},
        "entry": {"name": "greeting", "value_kind": "string", "value": "remediated"}})
    check("R2 named string entry updated", bool(envelope.get("ok")), json.dumps(envelope, ensure_ascii=False)[:1400])

    reviewed, review_id, required = review_tx(client, tx, revision)
    check("R3 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:240])
    committed = commit_tx(client, tx, revision, review_id, required)
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    check("R4 commit ok", bool(committed.get("ok")) and bool(lineage_id), json.dumps(committed)[:300])
    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
        "output_path": "edit-output\\chk-remediation\\ResourceHost2-chk005.dll"})
    export_path = str(payload(exported).get("output", {}).get("path", ""))
    check("R5 export ok", bool(exported.get("ok")) and export_path.lower().endswith(".dll"),
          json.dumps(exported)[:240])

    # authoritative readback with the BCL reader lives in the orchestrator
    # (PowerShell); here we record the exported path for it.
    Path(os.environ.get("CHK_EXPORT_DIR", ".")).mkdir(parents=True, exist_ok=True)
    marker = Path(os.environ.get("CHK_EXPORT_DIR", ".")) / "chk005-export-path.txt"
    marker.write_text(export_path, encoding="utf-8")
    check("R6 export path recorded", marker.exists(), export_path)


def case_chk007_risks(client: DnSpyClient) -> None:
    call(client, "open_files", {"paths": [TARGET_HOST, INBOUND_HOST]})
    tx, revision = begin_tx(client, "TargetHost")
    check("K1 transaction began", bool(tx))
    envelope, revision = apply_op(client, tx, revision, {"kind": "assembly_update", "name": "TargetHostRenamed"})
    check("K2 rename staged", bool(envelope.get("ok")), json.dumps(envelope)[:240])

    scan = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    inbound = payload(scan).get("impact", {}).get("inbound_references", [])
    check("K3 impact scan reports inbound", isinstance(inbound, list) and len(inbound) >= 1,
          json.dumps(scan)[:300])

    reviewed, review_id, required = review_tx(client, tx, revision)
    check("K4 review lists required confirmation", bool(review_id) and len(required) >= 1,
          f"required={required} {json.dumps(reviewed)[:240]}")
    committed = commit_tx(client, tx, revision, review_id, required)
    commit_row = payload(committed)
    confirmed = commit_row.get("confirmed_risks", []) if isinstance(commit_row, dict) else []
    full_facts = [r for r in confirmed if isinstance(r, dict) and r.get("kind") and r.get("risk_id") and r.get("description")]
    inbound_rows = [r for r in confirmed if isinstance(r, dict) and r.get("kind") == "cross_assembly_inbound"]
    affected = inbound_rows[0].get("affected_references") if inbound_rows else None
    check("K5 commit echoes full risk facts", bool(committed.get("ok")) and len(full_facts) == len(confirmed)
          and len(confirmed) >= 2 and isinstance(affected, dict) and bool(affected.get("module"))
          and isinstance(affected.get("sites"), list) and bool(affected.get("assembly_ref_token")),
          json.dumps(confirmed)[:700])


def case_chk008_inverse(client: DnSpyClient) -> None:
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    begin_env = call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()})
    tx_row = payload(begin_env).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    original_baseline = str(payload(begin_env).get("source", {}).get("live_fingerprint", ""))
    check("V1 transaction began", bool(tx) and bool(original_baseline), json.dumps(begin_env)[:200])
    for op in ({"kind": "module_update", "name": "IdentityHostFault1"},
               {"kind": "assembly_update", "version": "9.9.9.9"}):
        envelope, revision = apply_op(client, tx, revision, op)
        check(f"V2 apply {op['kind']}", bool(envelope.get("ok")), json.dumps(envelope)[:240])
    reviewed, review_id, required = review_tx(client, tx, revision)
    check("V3 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:240])

    armed = call(client, "edit_test_storage_fault", {"action": "arm", "stage": "live_apply"})
    check("V4 live_apply fault armed", bool(armed.get("ok")), json.dumps(armed)[:240])
    failed = commit_tx(client, tx, revision, review_id, required)
    code = error_code(failed)
    details = error_details(failed)
    stage = details.get("stage") or (details.get("details") or {}).get("stage")
    check("V5 commit fails at live_apply", code == "EDIT_CHECKPOINT_COMMIT_FAILED"
          and str(stage) == "live_apply", f"{code} {json.dumps(details)[:300]}")

    # The designed contract for a post-prepare commit failure: the pre-generated
    # inverse plan fully restores live, the owned temp is cleaned and the failed
    # transaction is ended — the coordinator must be cleanly idle (NOT
    # live_state_unknown) and the live fingerprint must equal the original
    # baseline captured before the transaction.
    status = payload(call(client, "edit_status", {}))
    begin2_env = call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()})
    begin2 = payload(begin2_env)
    live_now = str(begin2.get("source", {}).get("live_fingerprint", ""))
    rolled_back = call(client, "edit_rollback", {
        "request_id": rid(), "transaction_id": str(begin2.get("transaction", {}).get("transaction_id", "x"))})
    check("V6 clean idle after full recovery", status.get("state") == "idle" and bool(begin2.get("transaction")),
          json.dumps({"status": status.get("state"), "begin": bool(begin2.get("transaction"))})[:300])
    check("V6 live restored to pre-transaction baseline", bool(live_now) and live_now == original_baseline,
          f"now={live_now[:16]} base={original_baseline[:16]}")
    check("V7 rollback of the probe transaction", bool(rolled_back.get("ok")), json.dumps(rolled_back)[:240])

    # V8/V9: a fresh transaction with the same edits commits cleanly and reports
    # the pre-generated inverse plan binding.
    tx2, revision2 = begin_tx(client, "IdentityHost")
    for op in ({"kind": "module_update", "name": "IdentityHostFault2"},
               {"kind": "assembly_update", "version": "9.9.9.9"}):
        _env, revision2 = apply_op(client, tx2, revision2, op)
    reviewed2, review_id2, required2 = review_tx(client, tx2, revision2)
    committed = commit_tx(client, tx2, revision2, review_id2, required2)
    commit_row = payload(committed)
    live_recovery = commit_row.get("live_recovery", {}) if isinstance(commit_row, dict) else {}
    check("V8 fresh commit succeeds after recovery", bool(committed.get("ok")), json.dumps(committed)[:300])
    check("V9 inverse plan binding reported", bool(live_recovery.get("inverse_plan_complete_before_live_write"))
          and str(live_recovery.get("inverse_plan")) == "pregenerated_compiled_state",
          json.dumps(live_recovery)[:240])


def case_chk012_drift(client: DnSpyClient) -> None:
    """CHK-012: layout and CDI-content edits are invisible to the semantic
    fingerprint; the extended external guard must reject them at review."""
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    for case_id, label in (("live-conflict:mutate-layout", "layout"),
                           ("live-conflict:mutate-cdi", "cdi-content")):
        tx, revision = begin_tx(client, "IdentityHost")
        check(f"L1-{label} transaction began", bool(tx))
        _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": "IdentityHostGuard"})
        mutation = call(client, "edit_test_external_mutation", {
            "transaction_id": tx, "case_id": case_id})
        row = payload(mutation)
        semantic_unchanged = row.get("before_fingerprint") == row.get("after_fingerprint")
        guard_changed = row.get("guard_before") != row.get("guard_after")
        check(f"L2-{label} semantic blind / guard sees", semantic_unchanged and guard_changed,
              json.dumps({k: str(row.get(k))[:16] for k in ("before_fingerprint", "after_fingerprint", "guard_before", "guard_after")})[:260])
        reviewed, _rid, _req = review_tx(client, tx, revision)
        details = error_details(reviewed)
        check(f"L3-{label} review rejects drift",
              error_code(reviewed) == "EDIT_LIVE_MODULE_CONFLICT"
              and details.get("kind") == "external_drift_conflict",
              f"{error_code(reviewed)} {json.dumps(details)[:200]}")
        restored = call(client, "edit_test_live_mutation", {
            "transaction_id": tx, "action": "restore"})
        check(f"L4-{label} restored", bool(restored.get("ok")) and bool(payload(restored).get("restored")),
              json.dumps(restored)[:200])
        rollback = call(client, "edit_rollback", {
            "request_id": rid(), "transaction_id": tx})
        check(f"L5-{label} rollback", bool(rollback.get("ok")), json.dumps(rollback)[:160])


def case_chk013_gate(client: DnSpyClient) -> None:
    """CHK-013: a state change during the barrier pause between the commit-
    entry guards and the live-apply critical section is caught by the SECOND
    gate.  The concurrent vector is a debug launch (debug tools bypass the
    edit operation gate, exactly like a user starting a debug session); the
    entry-drift hook cannot be used here because every edit-family tool is
    serialized behind the paused commit's operation gate."""
    call(client, "edit_test_barrier", {"action": "reset"})
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    begin_env = call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()})
    tx = str(payload(begin_env).get("transaction", {}).get("transaction_id", ""))
    revision = int(payload(begin_env).get("transaction", {}).get("work_revision", 0))
    baseline = str(payload(begin_env).get("source", {}).get("live_fingerprint", ""))
    check("G1 transaction began", bool(tx) and bool(baseline),
          f"open={json.dumps(call(client, 'list_assemblies', {}))[:160]} begin={json.dumps(begin_env)[:260]}")
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": "IdentityHostGate"})
    reviewed, review_id, required = review_tx(client, tx, revision)
    check("G2 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:200])

    armed = call(client, "edit_test_barrier", {"action": "arm", "name": "commit_dispatcher_queued"})
    check("G3 barrier armed", bool(armed.get("ok")), json.dumps(armed)[:200])

    import threading
    result: dict = {}

    def committer() -> None:
        try:
            result["commit"] = commit_tx(client, tx, revision, review_id, required)
        except Exception as ex:  # noqa: BLE001
            result["commit_error"] = str(ex)[:300]

    thread = threading.Thread(target=committer)
    thread.start()
    entered = False
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline and not entered:
        snap = call(client, "edit_test_barrier", {"action": "snapshot"})
        entered = bool(payload(snap).get("entered"))
        time.sleep(0.4)
    check("G4 commit paused at barrier", entered, json.dumps(snap)[:200])

    # concurrent state change INSIDE the pause window: a debug session launch
    # (the entry guard already passed; only the second gate can still refuse).
    # The launch target must live inside the configured AllowedSampleRoot.
    import hashlib as _hashlib
    import shutil as _shutil
    launch_target = r"C:\Tools\MefCheck\chk013-IdentityHost.exe"
    _shutil.copyfile(IDENTITY_HOST, launch_target)
    fixture_sha = _hashlib.sha256(open(launch_target, "rb").read()).hexdigest()
    launch = call(client, "debug_launch", {
        "request_id": rid(), "target_path": launch_target, "expected_sha256": fixture_sha,
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "entry"})
    session_id = str(payload(launch).get("session_id", ""))
    check("G5 debug session started during pause", bool(session_id), json.dumps(launch)[:240])

    release = call(client, "edit_test_barrier", {"action": "release"})
    check("G6 barrier released", bool(release.get("ok")), json.dumps(release)[:160])
    thread.join(timeout=90)

    code = error_code(result.get("commit", {}))
    transport_error = result.get("commit_error", "")
    check("G7 second gate refuses with debug active", code == "EDIT_DEBUG_NOT_IDLE",
          f"{code} transport={transport_error} {json.dumps(result.get('commit', {}))[:200]}")

    # zero live writes: after cleanup, live is still the transaction baseline
    if session_id:
        call(client, "debug_terminate", {"session_id": session_id, "request_id": rid()})
    begin2 = payload(call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()}))
    live_now = str(begin2.get("source", {}).get("live_fingerprint", ""))
    check("G8 zero live writes", bool(live_now) and live_now == baseline,
          f"now={live_now[:14]} base={baseline[:14]}")
    tx2 = str(begin2.get("transaction", {}).get("transaction_id", "x"))
    if tx2 != "x":
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})
    call(client, "edit_test_barrier", {"action": "reset"})


def case_chk017_partial(client: DnSpyClient) -> None:
    """CHK-017: a finalize-fault partial carries the external-guard bounds and
    the clean no-drift undo restores live exactly.  (Drift-refusal during the
    partial state is source-wired — every edit-family tool is refused by the
    committed_without_checkpoint state gate, so the only drift vector is the
    dnSpy UI itself, matching the verifier's control-flow counterexample.)
    """
    call(client, "edit_test_barrier", {"action": "reset"})
    call(client, "open_files", {"paths": [IDENTITY_HOST]})
    begin_env = call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()})
    tx = str(payload(begin_env).get("transaction", {}).get("transaction_id", ""))
    revision = int(payload(begin_env).get("transaction", {}).get("work_revision", 0))
    baseline = str(payload(begin_env).get("source", {}).get("live_fingerprint", ""))
    check("P1 transaction began", bool(tx) and bool(baseline))
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": "IdentityHostPartial"})
    reviewed, review_id, required = review_tx(client, tx, revision)
    check("P2 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:200])

    armed = call(client, "edit_test_storage_fault", {"action": "arm", "stage": "finalize"})
    check("P3 finalize fault armed", bool(armed.get("ok")))
    failed = commit_tx(client, tx, revision, review_id, required)
    status = payload(call(client, "edit_status", {}))
    check("P4 partial commit state", error_code(failed) == "EDIT_CHECKPOINT_COMMIT_FAILED"
          and status.get("state") == "committed_without_checkpoint",
          f"{error_code(failed)} {status.get('state')}")
    recovery = status.get("recovery") or {}
    recovery_id = str(recovery.get("recovery_id", ""))
    allowed = [str(a) for a in (recovery.get("allowed_actions") or [])]
    check("P5 recovery fact present", bool(recovery_id) and "undo_live" in allowed,
          json.dumps(recovery)[:240])
    call(client, "edit_test_storage_fault", {"action": "reset"})

    # no drift vector ran: undo must succeed and restore live to the baseline
    undone = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery_id, "action": "undo_live"})
    check("P6 clean undo succeeds", bool(undone.get("ok")), json.dumps(undone)[:300])
    begin2 = payload(call(client, "edit_begin", {"assembly_name": "IdentityHost", "request_id": rid()}))
    live_now = str(begin2.get("source", {}).get("live_fingerprint", ""))
    check("P7 live restored to baseline", bool(live_now) and live_now == baseline,
          f"now={live_now[:14]} base={baseline[:14]}")
    tx2 = str(begin2.get("transaction", {}).get("transaction_id", "x"))
    if tx2 != "x":
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})



CASES = {
    "chk003-drift": case_chk003_drift,
    "chk004-identity": case_chk004_identity,
    "chk005-resource": case_chk005_resource,
    "chk007-risks": case_chk007_risks,
    "chk008-inverse": case_chk008_inverse,
    "chk012-drift": case_chk012_drift,
    "chk013-gate": case_chk013_gate,
    "chk017-partial": case_chk017_partial,
}


def main() -> int:
    case = os.environ.get("CHK_CASE", "") or "chk003-drift"
    for argument in sys.argv[1:]:
        if argument.startswith("--case="):
            case = argument.split("=", 1)[1]
    run_id = os.environ.get("CHK_RUN_ID", "chk-remediation")
    client = DnSpyClient(URL, client_name=f"chk-remediation-{case}", timeout=120)
    client.initialize()
    CASES[case](client)
    client.close()
    artifacts = Path(os.environ["USERPROFILE"]) / "Desktop" / "dnspy-mcp-artifacts" / "edit-tests" / run_id / case
    artifacts.mkdir(parents=True, exist_ok=True)
    summary = {
        "case": case,
        "status": "PASS" if not FAILURES else "FAIL",
        "passes": PASSES,
        "failures": FAILURES,
    }
    (artifacts / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{case} {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
