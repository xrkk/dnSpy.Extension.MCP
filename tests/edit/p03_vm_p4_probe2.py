#!/usr/bin/env python3
"""Round-52 P4 probe #2: while the commit parks in commit_after_live_first_mutation,
poll the barrier snapshot with a RAW http.client request (bypassing DnSpyClient)
to discriminate a server-side hang from a client-side connection issue."""

from __future__ import annotations

import http.client
import json
import sys
import threading
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL_HOST = "127.0.0.1"
URL_PORT = 15378
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
    client = DnSpyClient(URL_HOST and f"http://{URL_HOST}:{URL_PORT}/mcp", client_name=name)
    client.initialize()
    return client


def raw_snapshot(session_id: str, timeout: float) -> dict:
    body = json.dumps({"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                       "params": {"name": "edit_test_barrier", "arguments": {"action": "snapshot"}}})
    conn = http.client.HTTPConnection(URL_HOST, URL_PORT, timeout=timeout)
    try:
        conn.request("POST", "/mcp", body=body, headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "Mcp-Session-Id": session_id,
        })
        resp = conn.getresponse()
        raw = resp.read().decode("utf-8", "replace")
        line = raw.splitlines()[0] if raw.splitlines() else raw
        obj = json.loads(line)
        return obj.get("result", {}).get("structuredContent") or {"http": resp.status, "raw": raw[:200]}
    finally:
        conn.close()


def main() -> int:
    control = new_client("p4p2-control")
    owner = new_client("p4p2-owner")
    call(control, "open_files", {"paths": [FIXTURE]})

    begin = call(owner, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    applied = call(owner, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "P4Probe2"},
    })
    review = call(owner, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    print(f"prepared tx={bool(tx)} apply={applied.get('ok')} review={bool(review_core.get('review_id'))}", flush=True)

    call(control, "edit_test_barrier", {"action": "reset"})
    armed = call(owner, "edit_test_barrier", {"action": "arm", "name": "commit_after_live_first_mutation"})
    print(f"arm ok={armed.get('ok')}", flush=True)

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

    # control session id for the raw probe
    control_sid = control.session_id
    print(f"control session={control_sid}", flush=True)

    for i in range(40):
        if done.is_set() and result.get("envelope"):
            print(f"t={time.time()-start:.1f}s commit returned early: {json.dumps(result['envelope'])[:200]}", flush=True)
            break
        t0 = time.time()
        try:
            snap = raw_snapshot(control_sid, 8.0)
            note = f"entered={payload(snap).get('entered')}" if snap.get("ok") else json.dumps(snap)[:120]
        except Exception as ex:  # noqa: BLE001
            note = f"RAW-ERROR {type(ex).__name__}: {str(ex)[:120]}"
        print(f"t={time.time()-start:.1f}s raw snapshot({time.time()-t0:.2f}s): {note}", flush=True)
        core = payload(snap) if isinstance(snap, dict) and snap.get("ok") else {}
        if core.get("entered"):
            print(f"t={time.time()-start:.1f}s ENTERED observed via raw", flush=True)
            # release from the control session through the raw path too
            body = json.dumps({"jsonrpc": "2.0", "id": 10, "method": "tools/call",
                               "params": {"name": "edit_test_barrier", "arguments": {"action": "release"}}})
            conn = http.client.HTTPConnection(URL_HOST, URL_PORT, timeout=8)
            conn.request("POST", "/mcp", body=body, headers={
                "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                "Mcp-Session-Id": control_sid})
            resp = conn.getresponse()
            print(f"release http={resp.status}", flush=True)
            conn.close()
            break
        time.sleep(0.5)

    thread.join(timeout=45)
    print(f"commit thread alive={thread.is_alive()}", flush=True)
    if result.get("envelope"):
        print(f"commit envelope: {json.dumps(result['envelope'])[:260]}", flush=True)
    time.sleep(1.0)
    status = call(control, "edit_status", {})
    print(f"final status ok={status.get('ok')} state={payload(status).get('state', status.get('state'))}", flush=True)
    call(control, "edit_test_barrier", {"action": "reset"})
    control.close()
    owner.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
