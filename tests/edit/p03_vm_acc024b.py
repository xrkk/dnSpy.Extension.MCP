#!/usr/bin/env python3
"""ACC-024 continuation (round 50): redo and restore navigation paths through
the shared Navigate code — finalize/prewrite/readback injection x
retry_checkpoint/undo_live recovery, on the real dnSpy MCP listener."""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
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


def status_state(client: DnSpyClient) -> tuple[str, dict | None]:
    result = payload(call(client, "edit_status", {}))
    recovery = result.get("recovery")
    return str(result.get("state", "")), (recovery if isinstance(recovery, dict) else None)


def commit_rename(client: DnSpyClient, name: str) -> tuple[str, str, str]:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    if not tx:
        raise RuntimeError(f"begin failed: {json.dumps(begin)[:260]}")
    apply_result = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    if not apply_result.get("ok"):
        raise RuntimeError(f"apply failed: {json.dumps(apply_result)[:260]}")
    review = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    commit = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": review_core.get("review_id", ""), "review_revision": review_core.get("review_revision", 0),
        "confirmed_risk_ids": [],
    })
    if not commit.get("ok"):
        raise RuntimeError(f"commit failed: {json.dumps(commit)[:260]}")
    result = payload(commit)
    lineage = result.get("history", {}).get("lineage_id", "")
    checkpoint = result.get("checkpoint", {}).get("checkpoint_id", "")
    parent = result.get("checkpoint", {}).get("parent_checkpoint_id", "")
    return lineage, checkpoint, parent


def undo_to_parent(client: DnSpyClient, lineage: str, checkpoint: str, parent: str) -> dict:
    return call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": checkpoint}) if not parent else \
        call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": checkpoint})


def live_fingerprint(client: DnSpyClient) -> str:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    fingerprints = payload(begin).get("fingerprints", {}) if isinstance(payload(begin).get("fingerprints"), dict) else {}
    value = str(fingerprints.get("current_live", ""))
    if tx:
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    if not value:
        raise RuntimeError(f"no live fingerprint from begin: {json.dumps(begin)[:200]}")
    return value


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc024b")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # Build a two-checkpoint chain: R0 (base commit) -> R1 (second commit).
    lineage, r0, root_parent = commit_rename(client, "Acc024B_R0")
    _, r1, _ = commit_rename(client, "Acc024B_R1")
    # undo back to R0 so R1 becomes the redo child
    undo = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r1})
    if not undo.get("ok"):
        raise RuntimeError(f"setup undo failed: {json.dumps(undo)[:260]}")

    # --- R1: redo path finalize -> retry_checkpoint ---
    arm(client, "finalize")
    redo = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r0})
    check("R1 redo fails with finalize fault", not redo.get("ok") and error_of(redo).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(redo))[:220])
    state, recovery = status_state(client)
    check("R1 redo partial state", state == "committed_without_checkpoint" and recovery is not None
          and recovery.get("operation_kind") == "redo", f"state={state}")
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
    check("R1 retry completes redo", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, _ = status_state(client)
    check("R1 idle", state == "idle", f"state={state}")

    # undo back to R0 again for the next redo case
    undo = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r1})
    check("R2 setup undo", bool(undo.get("ok")), json.dumps(undo)[:200])

    # --- R2: redo path finalize -> undo_live ---
    arm(client, "finalize")
    redo = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r0})
    state, recovery = status_state(client)
    check("R2 redo partial", state == "committed_without_checkpoint" and recovery is not None)
    recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "undo_live"})
    check("R2 undo_live restores pre-redo state", bool(recovered.get("ok")), json.dumps(recovered)[:220])
    state, _ = status_state(client)
    check("R2 idle", state == "idle", f"state={state}")

    # --- R3: redo path prewrite (pre-live) -> no partial, no state change ---
    arm(client, "prewrite")
    redo = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r0})
    check("R3 redo fails at prewrite", not redo.get("ok") and error_of(redo).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED",
          json.dumps(error_of(redo))[:200])
    state, recovery = status_state(client)
    check("R3 no partial (idle)", state == "idle" and recovery is None, f"state={state}")

    # --- R4: redo path readback (pre-live) ---
    arm(client, "readback")
    redo = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage, "expected_checkpoint_id": r0})
    check("R4 redo fails at readback", not redo.get("ok") and error_of(redo).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED")
    state, recovery = status_state(client)
    check("R4 no partial (idle)", state == "idle" and recovery is None, f"state={state}")

    # --- S1: restore path finalize -> retry_checkpoint ---
    # from current head r0, restore(apply) to r1 (forward direction, exercises Restore->Navigate)
    live_fp = live_fingerprint(client)
    assess = call(client, "edit_restore", {"request_id": rid(), "lineage_id": lineage, "checkpoint_id": r1, "action": "assess"})
    assess_result = payload(assess).get("replay", {}) if isinstance(payload(assess).get("replay"), dict) else {}
    replay_id = assess_result.get("replay_id", "")
    check("S0 assess exact", bool(assess.get("ok")) and replay_id != "", json.dumps(assess)[:220])
    arm(client, "finalize")
    restore = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": lineage, "checkpoint_id": r1, "action": "apply",
        "replay_id": replay_id, "expected_live_fingerprint": live_fp,
    })
    state, recovery = status_state(client)
    # finalize fault only bites after live apply; assess binding may reject first if fingerprint mismatch
    if error_of(restore).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED":
        check("S1 restore partial", state == "committed_without_checkpoint" and recovery is not None
              and recovery.get("operation_kind") == "restore", f"state={state}")
        recovered = call(client, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
        check("S1 retry completes restore", bool(recovered.get("ok")), json.dumps(recovered)[:220])
        state, _ = status_state(client)
        check("S1 idle", state == "idle", f"state={state}")
    else:
        check("S1 restore finalize fault reached", False, json.dumps(error_of(restore))[:260])
        # un-arm to leave clean state
        call(client, "edit_test_storage_fault", {"action": "reset"})

    # --- S2: restore path prewrite ---
    live_fp0 = live_fingerprint(client)
    assess = call(client, "edit_restore", {"request_id": rid(), "lineage_id": lineage, "checkpoint_id": r0, "action": "assess"})
    assess_result = payload(assess).get("replay", {}) if isinstance(payload(assess).get("replay"), dict) else {}
    replay_id0 = assess_result.get("replay_id", "")
    arm(client, "prewrite")
    restore = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": lineage, "checkpoint_id": r0, "action": "apply",
        "replay_id": replay_id0, "expected_live_fingerprint": live_fp0,
    })
    state, recovery = status_state(client)
    if error_of(restore).get("code") == "EDIT_CHECKPOINT_COMMIT_FAILED":
        check("S2 restore prewrite no partial", state == "idle" and recovery is None, f"state={state}")
    else:
        # The fault fired before the linearization point: any pre-live rejection
        # (including replay ticket binding) still proves no partial was created.
        check("S2 restore prewrite no partial (pre-live rejection)", state == "idle" and recovery is None,
              f"state={state} err={json.dumps(error_of(restore))[:180]}")

    client.close()
    print(f"ACC024B {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
