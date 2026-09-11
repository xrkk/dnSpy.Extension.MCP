#!/usr/bin/env python3
"""ACC-019 session-close matrix on the real dnSpy MCP listener: closing the
transport session mid-transaction rolls back uncommitted edits; closing during
a committed_without_checkpoint partial leaves the recovery record queryable and
resolvable by a fresh session; idle closes are clean."""

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


def new_client() -> DnSpyClient:
    client = DnSpyClient(URL, client_name="p03-vm-acc019")
    client.initialize()
    return client


def begin_apply(client: DnSpyClient, name: str) -> None:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    if not tx:
        raise RuntimeError(f"begin failed: {json.dumps(begin)[:260]}")
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    if not applied.get("ok"):
        raise RuntimeError(f"apply failed: {json.dumps(applied)[:260]}")


def main() -> int:
    bootstrap = new_client()
    call(bootstrap, "open_files", {"paths": [FIXTURE]})
    bootstrap.close()

    # --- N1: idle close is clean ---
    c1 = new_client()
    state = payload(call(c1, "edit_status", {})).get("state")
    c1.close()
    c1b = new_client()
    state_after = payload(call(c1b, "edit_status", {})).get("state")
    check("N1 idle close keeps idle", state == "idle" and state_after == "idle", f"{state}->{state_after}")
    c1b.close()

    # --- N2: close mid-transaction rolls back uncommitted edits ---
    c2 = new_client()
    begin_apply(c2, "Acc019_Uncommitted")
    busy = payload(call(c2, "edit_status", {})).get("busy")
    c2.close()  # session deleted -> OnSessionClosed
    c2b = new_client()
    status = payload(call(c2b, "edit_status", {}))
    check("N2 uncommitted rolled back on close", status.get("state") == "idle" and not status.get("busy"),
          json.dumps(status)[:200])
    # the module must be usable for a new transaction (no lingering tx)
    begin_apply(c2b, "Acc019_AfterClose")
    rollback_tx = payload(call(c2b, "edit_status", {})).get("transaction", {})
    tx_id = rollback_tx.get("transaction_id", "") if isinstance(rollback_tx, dict) else ""
    rolled = call(c2b, "edit_rollback", {"request_id": rid(), "transaction_id": tx_id})
    check("N2 new session can own transactions", bool(rolled.get("ok")), json.dumps(rolled)[:200])
    c2b.close()

    # --- N3: close during committed_without_checkpoint partial survives reconnect ---
    c3 = new_client()
    begin = call(c3, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    applied = call(c3, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "Acc019_Partial"},
    })
    review = call(c3, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    call(c3, "edit_test_storage_fault", {"action": "arm", "stage": "finalize"})
    commit = call(c3, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": review_core.get("review_id", ""), "review_revision": review_core.get("review_revision", 0),
        "confirmed_risk_ids": [],
    })
    check("N3 partial created", not commit.get("ok") and payload(call(c3, "edit_status", {})).get("state") == "committed_without_checkpoint",
          json.dumps(commit)[:180])
    c3.close()  # close the owning session while the partial exists
    c3b = new_client()
    status = payload(call(c3b, "edit_status", {}))
    recovery = status.get("recovery")
    check("N3 recovery queryable after reconnect", status.get("state") == "committed_without_checkpoint"
          and isinstance(recovery, dict) and recovery.get("recovery_kind") == "checkpoint_finalize",
          json.dumps(status)[:260])
    resolved = call(c3b, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
    check("N3 fresh session resolves via retry", bool(resolved.get("ok")), json.dumps(resolved)[:220])
    final = payload(call(c3b, "edit_status", {}))
    check("N3 idle after recovery", final.get("state") == "idle" and final.get("recovery") is None, json.dumps(final)[:160])
    c3b.close()

    print(f"ACC019 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
