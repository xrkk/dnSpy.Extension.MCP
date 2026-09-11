#!/usr/bin/env python3
"""Round-52 P4 probe: arm commit_after_live_first_mutation, run a commit in a
thread, and log every barrier snapshot verbatim to explain why `entered` was
never observed in the r8 matrix run."""

from __future__ import annotations

import json
import sys
import threading
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"


def rid() -> str:
    return str(uuid.uuid4())


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


def new_client(name: str) -> DnSpyClient:
    client = DnSpyClient(URL, client_name=name)
    client.initialize()
    return client


def main() -> int:
    control = new_client("p4-probe-control")
    owner = new_client("p4-probe-owner")
    call(control, "open_files", {"paths": [FIXTURE]})

    begin = call(owner, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    if not tx:
        print(f"begin failed: {json.dumps(begin)[:300]}", flush=True)
        return 1
    applied = call(owner, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "P4Probe"},
    })
    if not applied.get("ok"):
        print(f"apply failed: {json.dumps(applied)[:300]}", flush=True)
        return 1
    review = call(owner, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    if not review_core.get("review_id"):
        print(f"review failed: {json.dumps(review)[:300]}", flush=True)
        return 1

    call(control, "edit_test_barrier", {"action": "reset"})
    armed = call(owner, "edit_test_barrier", {"action": "arm", "name": "commit_after_live_first_mutation"})
    print(f"arm: {json.dumps(armed)[:200]}", flush=True)

    result: dict = {}
    done = threading.Event()

    def run_commit() -> None:
        result["envelope"] = call(owner, "edit_commit", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
            "review_id": review_core["review_id"], "review_revision": review_core.get("review_revision", 0),
            "confirmed_risk_ids": [],
        })
        done.set()

    thread = threading.Thread(target=run_commit, daemon=True)
    start = time.time()
    thread.start()

    for i in range(60):
        if done.is_set() and result.get("envelope"):
            print(f"t={time.time()-start:.1f}s commit returned: {json.dumps(result['envelope'])[:300]}", flush=True)
            break
        t0 = time.time()
        snap = call(control, "edit_test_barrier", {"action": "snapshot"})
        dt = time.time() - t0
        print(f"t={time.time()-start:.1f}s snapshot({dt:.2f}s): {json.dumps(snap)[:260]}", flush=True)
        core = payload(snap) if snap.get("ok") else {}
        if core.get("entered"):
            print(f"t={time.time()-start:.1f}s ENTERED observed", flush=True)
            break
        time.sleep(0.5)

    # always leave a clean barrier behind
    call(control, "edit_test_barrier", {"action": "reset"})
    thread.join(timeout=45)
    if thread.is_alive():
        print("commit thread still blocked after reset+45s", flush=True)
    else:
        print(f"final commit envelope: {json.dumps(result.get('envelope'))[:300]}", flush=True)
    time.sleep(1.0)
    status = call(control, "edit_status", {})
    print(f"final status: {json.dumps(status)[:300]}", flush=True)
    control.close()
    owner.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
