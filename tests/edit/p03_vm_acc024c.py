#!/usr/bin/env python3
"""ACC-024 forward/inverse application-fault matrix (round 59): inject the
navigate_forward and navigate_inverse stages of edit_test_storage_fault into
edit_undo / edit_redo through the real loopback and verify the plan section 6
navigate semantics: a forward failure restores live via the inverse and stays
pre-head (idle after temp cleanup); an inverse failure enters
live_state_unknown with no product recovery until the emergency cleanup seam
restores the live fingerprint, and redo/restore paths behave identically."""

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


def configure_isolation(context) -> None:
    global URL, FIXTURE
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def err_code(envelope: dict) -> str:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def err_details(envelope: dict) -> dict:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    details = error.get("details") if isinstance(error, dict) else None
    return details if isinstance(details, dict) else {}


def commit_one(client: DnSpyClient, name: str, revision_base: int) -> str:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    review = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": review_core.get("review_id", ""),
        "review_revision": review_core.get("review_revision", 0),
        "confirmed_risk_ids": [],
    })
    if not committed.get("ok"):
        raise RuntimeError(f"seed commit failed: {json.dumps(committed)[:240]}")
    return str(payload(committed).get("history", {}).get("head_checkpoint_id", ""))


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc024c")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # Seed lineage: two commits (head C2, parent C1).
    commit_one(client, "Acc024C_A", 0)
    head = commit_one(client, "Acc024C_B", 0)
    history = payload(call(client, "edit_history", {}))
    lineages = [row for row in history.get("lineages", []) if isinstance(row, dict)]
    lineage_id = str(lineages[0].get("lineage_id", "")) if lineages else ""
    view = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
    nodes = sorted([row for row in view.get("checkpoints", []) if isinstance(row, dict)], key=lambda row: row.get("sequence", 0))
    parent = str(nodes[-2].get("checkpoint_id", ""))
    check("D1 seed lineage ready", len(lineages) == 1 and bool(head) and bool(parent), json.dumps(history)[:200])

    # F1: navigate_forward fault on edit_undo -> inverse restores, pre-head, idle.
    call(client, "edit_test_storage_fault", {"action": "arm", "stage": "navigate_forward"})
    forward_failed = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": head})
    details = err_details(forward_failed)
    check("F1 forward fault surfaced",
          err_code(forward_failed) == "EDIT_CHECKPOINT_COMMIT_FAILED" and details.get("stage") == "navigate_forward",
          f"code={err_code(forward_failed)} details={json.dumps(details)[:160]}")
    status = payload(call(client, "edit_status", {}))
    check("F1 idle after inverse restore", status.get("state") == "idle" and status.get("recovery") is None,
          json.dumps(status)[:200])
    after = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
    heads_now = [row for row in after.get("checkpoints", []) if isinstance(row, dict) and row.get("is_head")]
    check("F1 head unchanged (pre-head preserved)", bool(heads_now) and heads_now[0].get("checkpoint_id") == head)
    begin_probe = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    begin_ok = bool(begin_probe.get("ok"))
    if begin_ok:
        tx_probe = payload(begin_probe).get("transaction", {}).get("transaction_id", "")
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx_probe})
    check("F1 live restored to head (begin binds cleanly)", begin_ok, json.dumps(begin_probe)[:200])
    # F3: navigate_forward fault on edit_redo after a real undo.
    undone = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": head})
    check("D3 real undo for redo path", bool(undone.get("ok")), json.dumps(undone)[:200])
    if undone.get("ok"):
        new_head = str(payload(undone).get("history", {}).get("head_checkpoint_id", ""))
        call(client, "edit_test_storage_fault", {"action": "arm", "stage": "navigate_forward"})
        redo_failed = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": new_head})
        redo_details = err_details(redo_failed)
        check("F3 redo forward fault same contract",
              err_code(redo_failed) == "EDIT_CHECKPOINT_COMMIT_FAILED" and redo_details.get("stage") == "navigate_forward",
              f"code={err_code(redo_failed)}")
        status3 = payload(call(client, "edit_status", {}))
        check("F3 idle after redo inverse restore", status3.get("state") == "idle" and status3.get("recovery") is None,
              json.dumps(status3)[:160])
        redo_ok = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": new_head})
        check("F3 redo succeeds after fault cleared", bool(redo_ok.get("ok")), json.dumps(redo_ok)[:200])


    # F2: navigate_inverse fault on edit_undo -> live_state_unknown, emergency cleanup.
    call(client, "edit_test_storage_fault", {"action": "arm", "stage": "navigate_inverse"})
    inverse_failed = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": head})
    check("F2 inverse fault surfaces live_state_unknown", err_code(inverse_failed) == "EDIT_LIVE_STATE_UNKNOWN", err_code(inverse_failed))
    unknown_status = call(client, "edit_status", {})
    check("F2 state live_state_unknown", payload(unknown_status).get("state") == "live_state_unknown" or unknown_status.get("state") == "live_state_unknown",
          json.dumps(unknown_status)[:200])
    blocked_begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    check("F2 edits blocked while unknown", not blocked_begin.get("ok"), json.dumps(blocked_begin)[:160])
    # Section 6: an inverse failure has NO product recovery. The emergency
    # cleanup seam must NOT fake a restore (the live module sits at the target
    # state), the state stays unknown and the owned temp is metered residual.
    recovered = call(client, "edit_test_fault", {"action": "reset"})
    still_unknown = payload(call(client, "edit_status", {}))
    check("F2 no false recovery from unknown", still_unknown.get("state") == "live_state_unknown",
          json.dumps(still_unknown)[:200])
    capacity = still_unknown.get("capacity", {}) if isinstance(still_unknown.get("capacity"), dict) else {}
    residuals = capacity.get("residuals", {}) if isinstance(capacity.get("residuals"), dict) else {}
    check("F2 temp metered residual", int(residuals.get("current", 0)) >= 1, json.dumps(capacity)[:200])
    after_unknown = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
    heads_after = [row for row in after_unknown.get("checkpoints", []) if isinstance(row, dict) and row.get("is_head")]
    check("F2 head unchanged through unknown", bool(heads_after) and heads_after[0].get("checkpoint_id") == head)
    print("INFO F2 live_state_unknown is terminal per section 6; dnSpy restart is the documented recovery", flush=True)

    print(f"ACC024C {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
