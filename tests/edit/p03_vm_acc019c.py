#!/usr/bin/env python3
"""ACC-019 listener dimension (round 54): six commit-phase barriers x MCP
listener restart. Each phase parks a commit inside a barrier, requests an
AI-side listener restart through the P02 UI-signal contract (disable ->
wait-down -> enable -> wait-up, one ack), lets the in-flight commit settle
under teardown (session close cancels/releases per plan section 6), then a
fresh post-restart session verifies the unique legal terminal state, resolves
any partial, and proves new sessions are usable."""

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
UI_SIGNAL_DIR = Path(__file__).resolve().parent / "ui-signals"
BARRIERS = [
    "commit_after_guard_before_temp",
    "commit_after_temp_validate",
    "commit_dispatcher_queued",
    "commit_after_live_first_mutation",
    "commit_after_live_complete",
    "commit_after_package_switch_before_response",
]
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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def new_client(name: str) -> DnSpyClient:
    client = DnSpyClient(URL, client_name=name)
    client.initialize()
    return client


def request_listener_restart(index: int) -> bool:
    UI_SIGNAL_DIR.mkdir(parents=True, exist_ok=True)
    request = UI_SIGNAL_DIR / f"request-{index:02d}-15378.json"
    acknowledgement = UI_SIGNAL_DIR / f"ack-{index:02d}-15378.json"
    acknowledgement.unlink(missing_ok=True)
    request.write_text(json.dumps({
        "schema_version": "dnspy.p03.ui-signal.v1",
        "sequence": index,
        "action": "restart",
        "host": "localhost",
        "port": 15378,
    }, indent=2) + "\n", encoding="utf-8")
    deadline = time.time() + 300
    while time.time() < deadline:
        if acknowledgement.is_file():
            value = json.loads(acknowledgement.read_text(encoding="utf-8"))
            return value.get("result") == "PASS"
        time.sleep(0.2)
    return False


def prepare_reviewed_tx(client: DnSpyClient, name: str) -> tuple[str, str, int, dict]:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = payload(begin).get("transaction", {}).get("work_revision", 0)
    if not tx:
        raise RuntimeError(f"begin failed: {json.dumps(begin)[:240]}")
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    if not applied.get("ok"):
        raise RuntimeError(f"apply failed: {json.dumps(applied)[:240]}")
    review = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    if not review_core.get("review_id"):
        raise RuntimeError(f"review failed: {json.dumps(review)[:240]}")
    return tx, review_core.get("review_id", ""), revision + 1, review_core.get("review_revision", 0)


def phase_case(barrier: str, index: int) -> None:
    tag = f"S{index + 1}[{barrier}]"
    control = new_client(f"acc019c-control-{index}")
    owner = new_client(f"acc019c-owner-{index}")
    try:
        if index == 0:
            call(control, "open_files", {"paths": [FIXTURE]})
        call(control, "edit_test_barrier", {"action": "reset"})
        tx, review_id, revision, review_rev = prepare_reviewed_tx(owner, f"Acc019C_Ph{index + 1}")
        armed = call(owner, "edit_test_barrier", {"action": "arm", "name": barrier})
        if not armed.get("ok"):
            check(f"{tag} arm", False, json.dumps(armed)[:200])
            return

        result: dict = {}
        done = threading.Event()

        def run_commit() -> None:
            result["envelope"] = call(owner, "edit_commit", {
                "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
                "review_id": review_id, "review_revision": review_rev,
                "confirmed_risk_ids": [],
            })
            done.set()

        thread = threading.Thread(target=run_commit, daemon=True)
        thread.start()
        entered = False
        for _ in range(120):
            if done.is_set() and result.get("envelope"):
                break
            snap = payload(call(control, "edit_test_barrier", {"action": "snapshot"}))
            if isinstance(snap, dict) and snap.get("entered"):
                entered = True
                break
            time.sleep(0.25)
        if not entered:
            call(control, "edit_test_barrier", {"action": "reset"})
            thread.join(timeout=30)
            envelope = result.get("envelope")
            print(f"INFO {tag} commit envelope: {json.dumps(envelope)[:300] if envelope else '<still blocked>'}", flush=True)
            drain = payload(call(owner, "edit_status", {}))
            drain_tx = drain.get("transaction", {}) if isinstance(drain.get("transaction"), dict) else {}
            if drain_tx.get("transaction_id"):
                call(owner, "edit_rollback", {"request_id": rid(), "transaction_id": drain_tx["transaction_id"]})
            check(f"{tag} barrier entered", False, "barrier never entered")
            return

        # Listener restart while the commit is parked: teardown closes every
        # transport session, which cancels or releases the parked commit per
        # plan section 6; the in-flight HTTP call settles with a transport error.
        restarted = request_listener_restart(index)
        if not restarted:
            call(control, "edit_test_barrier", {"action": "reset"})
            thread.join(timeout=30)
            check(f"{tag} listener restart ack", False, "no ack within 300s")
            return
        thread.join(timeout=60)
        print(f"INFO {tag} commit settled under teardown: alive={thread.is_alive()}", flush=True)
        time.sleep(1.0)

        # Everything from before the restart is gone; observe with fresh sessions.
        observer = new_client(f"acc019c-observer-{index}")
        status = payload(call(observer, "edit_status", {}))
        state = str(status.get("state", ""))
        recovery = status.get("recovery")
        legal = (state == "idle") or (state == "committed_without_checkpoint" and isinstance(recovery, dict))
        check(f"{tag} terminal state legal", legal, f"state={state} recovery={json.dumps(recovery or {})[:160]}")
        if state == "committed_without_checkpoint" and isinstance(recovery, dict):
            resolved = call(observer, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
            check(f"{tag} partial resolvable", bool(resolved.get("ok")), json.dumps(resolved)[:200])
        probe = new_client(f"acc019c-probe-{index}")
        begin = call(probe, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
        ok_begin = bool(begin.get("ok"))
        if ok_begin:
            tx_probe = payload(begin).get("transaction", {}).get("transaction_id", "")
            call(probe, "edit_rollback", {"request_id": rid(), "transaction_id": tx_probe})
        probe.close()
        check(f"{tag} new session usable", ok_begin, json.dumps(begin)[:200])
        call(observer, "edit_test_barrier", {"action": "reset"})
        observer.close()
    finally:
        for closer in (owner, control):
            try:
                closer.close()
            except Exception:  # noqa: BLE001
                pass


def main() -> int:
    for index, barrier in enumerate(BARRIERS):
        phase_case(barrier, index)
    print(f"ACC019C {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
