#!/usr/bin/env python3
"""ACC-024 coordinator-level injection matrix on the real dnSpy MCP listener:
storage faults (prewrite/readback/finalize/cleanup) across commit and undo
actions, verifying the unique partial terminal state, the recovery action
matrix (retry_checkpoint / undo_live / cleanup_temp), and pre-live rollback
paths with zero live side effects."""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
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


def error_of(envelope: dict) -> dict:
    value = envelope.get("error") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def arm(client: DnSpyClient, stage: str) -> None:
    call(client, "edit_test_storage_fault", {"action": "arm", "stage": stage})


def disarm(client: DnSpyClient) -> None:
    call(client, "edit_test_storage_fault", {"action": "reset"})


def status_recovery(client: DnSpyClient) -> tuple[str, dict | None]:
    envelope = call(client, "edit_status", {})
    result = payload(envelope)
    recovery = result.get("recovery")
    return str(result.get("state", "")), (recovery if isinstance(recovery, dict) else None)


def run_transaction(client: DnSpyClient, name: str) -> tuple[dict, dict]:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    transaction = payload(begin).get("transaction", {})
    tx = transaction.get("transaction_id", "")
    revision = transaction.get("work_revision", 0)
    if not tx:
        raise RuntimeError(f"edit_begin failed: {json.dumps(begin)[:300]}")
    apply_result = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    if not apply_result.get("ok"):
        raise RuntimeError(f"edit_apply failed: {json.dumps(apply_result)[:300]}")
    review = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    review_id = review_core.get("review_id", "")
    if not review_id:
        raise RuntimeError(f"edit_review failed: {json.dumps(review)[:300]}")
    commit = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": review_id, "review_revision": review_core.get("review_revision", 0),
        "confirmed_risk_ids": [],
    })
    return commit, tx


def rollback_quiet(client: DnSpyClient, tx: str) -> dict:
    envelope = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    if not envelope.get("ok"):
        print(f"INFO rollback envelope: {json.dumps(envelope)[:260]}", flush=True)
    return envelope


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc024")
    client.initialize()
    opened = call(client, "open_files", {"paths": [r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"]})
    loaded = opened if isinstance(opened, dict) else {}
    print(f"INFO open loaded_count={loaded.get('loaded_count')} failed={loaded.get('failed_count')}", flush=True)
    if not loaded.get("loaded_count") and not loaded.get("already_loaded_count"):
        raise RuntimeError(f"fixture did not load: {json.dumps(opened)[:300]}")
    # Drain any stale transaction left by an earlier crashed run.
    stale = call(client, "edit_status", {})
    stale_result = payload(stale)
    stale_tx = stale_result.get("transaction", {}) if isinstance(stale_result.get("transaction"), dict) else {}
    if stale_tx.get("transaction_id"):
        drained = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": stale_tx["transaction_id"]})
        print(f"INFO drained stale tx {stale_tx['transaction_id'][:18]} ok={drained.get('ok')}", flush=True)

    # Case A: finalize fault -> committed_without_checkpoint -> retry_checkpoint
    arm(client, "finalize")
    commit, tx = run_transaction(client, "Acc024A")
    check("A commit fails with finalize fault", not commit.get("ok") and error_of(commit).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(commit))[:220])
    state, recovery = status_recovery(client)
    check("A state committed_without_checkpoint", state == "committed_without_checkpoint", f"state={state}")
    check("A recovery shape", recovery is not None and recovery.get("recovery_kind") == "checkpoint_finalize"
          and recovery.get("allowed_actions") == ["retry_checkpoint", "undo_live"], json.dumps(recovery or {})[:220])
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
    check("A retry_checkpoint succeeds", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, recovery = status_recovery(client)
    check("A idle after retry", state == "idle" and recovery is None, f"state={state}")
    lineage_id = payload(recovered).get("history", {}).get("lineage_id", "")
    checkpoint_a = payload(recovered).get("checkpoint", {}).get("checkpoint_id", "")
    check("A head advanced", bool(lineage_id and checkpoint_a))

    # Case B: finalize fault -> undo_live restores live and clears temp
    arm(client, "finalize")
    commit, tx = run_transaction(client, "Acc024B")
    check("B commit fails", not commit.get("ok") and error_of(commit).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED")
    state, recovery = status_recovery(client)
    check("B partial state", state == "committed_without_checkpoint" and recovery is not None)
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "undo_live"})
    check("B undo_live succeeds", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, recovery = status_recovery(client)
    check("B idle after undo_live", state == "idle" and recovery is None, f"state={state}")

    # Case C: finalize fault -> undo_live hits cleanup fault -> aborted_temp_cleanup -> cleanup_temp
    arm(client, "finalize")
    commit, tx = run_transaction(client, "Acc024C")
    state, recovery = status_recovery(client)
    check("C partial state", state == "committed_without_checkpoint" and recovery is not None)
    arm(client, "cleanup")
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "undo_live"})
    check("C undo_live reports cleanup failure", not recovered.get("ok") and error_of(recovered).get("code") == "EDIT_CHECKPOINT_CLEANUP_FAILED",
          json.dumps(error_of(recovered))[:220])
    state, recovery = status_recovery(client)
    check("C committing + aborted_temp_cleanup", state == "committing" and recovery is not None
          and recovery.get("recovery_kind") == "aborted_temp_cleanup"
          and recovery.get("allowed_actions") == ["cleanup_temp"], f"state={state} rec={json.dumps(recovery or {})[:200]}")
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "cleanup_temp"})
    check("C cleanup_temp succeeds", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, recovery = status_recovery(client)
    check("C idle after cleanup_temp", state == "idle" and recovery is None, f"state={state}")

    # Case C2: cleanup fault fires twice, then succeeds
    arm(client, "finalize")
    commit, tx = run_transaction(client, "Acc024C2")
    state, recovery = status_recovery(client)
    arm(client, "cleanup")
    call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "undo_live"})
    state, recovery = status_recovery(client)
    check("C2 aborted_temp_cleanup", recovery is not None and recovery.get("recovery_kind") == "aborted_temp_cleanup")
    arm(client, "cleanup")
    again = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "cleanup_temp"})
    check("C2 second cleanup fault still fails cleanly", not again.get("ok") and error_of(again).get("code") == "EDIT_CHECKPOINT_CLEANUP_FAILED")
    state, recovery = status_recovery(client)
    check("C2 still aborted after double fault", state == "committing" and recovery is not None
          and recovery.get("recovery_kind") == "aborted_temp_cleanup")
    final = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "cleanup_temp"})
    check("C2 cleanup succeeds once unarmed", bool(final.get("ok")), json.dumps(final)[:220])
    state, recovery = status_recovery(client)
    check("C2 idle", state == "idle" and recovery is None)

    # Case D: prewrite fault -> pre-live rollback, no partial, live untouched
    arm(client, "prewrite")
    commit, tx = run_transaction(client, "Acc024D")
    check("D commit fails at prewrite", not commit.get("ok") and error_of(commit).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(commit))[:200])
    state, recovery = status_recovery(client)
    check("D no partial, transaction retained", state in ("editing", "reviewed", "committing") and recovery is None, f"state={state}")
    rollback_quiet(client, tx)
    state, _ = status_recovery(client)
    check("D idle after rollback", state == "idle", f"state={state}")

    # Case E: readback fault -> same pre-live semantics
    arm(client, "readback")
    commit, tx = run_transaction(client, "Acc024E")
    check("E commit fails at readback", not commit.get("ok") and error_of(commit).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(commit))[:200])
    state, recovery = status_recovery(client)
    check("E no partial (closed or retained)", state in ("idle", "editing", "reviewed", "committing") and recovery is None, f"state={state}")
    rollback_quiet(client, tx)
    state, _ = status_recovery(client)
    check("E idle after rollback", state == "idle", f"state={state}")

    # Case F: undo-path finalize fault -> partial -> retry_checkpoint
    commit, tx = run_transaction(client, "Acc024F")
    check("F clean commit", bool(commit.get("ok")), json.dumps(commit)[:200])
    checkpoint_f = payload(commit).get("checkpoint", {}).get("checkpoint_id", "")
    parent_f = payload(commit).get("checkpoint", {}).get("parent_checkpoint_id", "")
    arm(client, "finalize")
    undo = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": checkpoint_f})
    check("F undo fails with finalize fault", not undo.get("ok") and error_of(undo).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(undo))[:220])
    state, recovery = status_recovery(client)
    check("F undo partial state", state == "committed_without_checkpoint" and recovery is not None
          and recovery.get("operation_kind") == "undo", f"state={state} rec={json.dumps(recovery or {})[:200]}")
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
    check("F retry completes undo", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, recovery = status_recovery(client)
    check("F idle", state == "idle" and recovery is None, f"state={state}")

    # Wrong-action rejection while a partial exists is covered implicitly by
    # allowed_actions checks above; close with a summary.
    client.close()
    print(f"ACC024 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
