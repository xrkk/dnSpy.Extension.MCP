#!/usr/bin/env python3
"""ACC-019 continuation (round 51): the six commit-phase barriers x session
close, plus the 16-session transport quota release, on the real dnSpy MCP
listener. Each barrier case arms a commit-phase barrier from a control session,
starts edit_commit on an owned session (which blocks inside the barrier), closes
the owning session mid-phase, then releases the barrier and verifies the unique
terminal state from a fresh observer session."""

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


def new_client(name: str = "p03-vm-acc019b") -> DnSpyClient:
    client = DnSpyClient(URL, client_name=name)
    client.initialize()
    return client


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


def barrier_case(control: DnSpyClient, observer: DnSpyClient, barrier: str, index: int) -> None:
    tag = f"P{index + 1}[{barrier}]"
    # Session close auto-releases the owner's parked barrier but leaves it armed;
    # clear any barrier a previous case left behind before arming this one.
    call(control, "edit_test_barrier", {"action": "reset"})
    owner = new_client(f"acc019b-owner-{index}")
    tx, review_id, revision, review_rev = prepare_reviewed_tx(owner, f"Acc019B_Ph{index + 1}")
    # The barrier owner must be the session that runs the commit; arm from it.
    armed = call(owner, "edit_test_barrier", {"action": "arm", "name": barrier})
    if not armed.get("ok"):
        check(f"{tag} arm", False, json.dumps(armed)[:200])
        owner.close()
        return

    review_revision_value = review_rev
    commit_result: dict = {}
    started = threading.Event()
    done = threading.Event()

    def run_commit() -> None:
        started.set()
        commit_result["envelope"] = call(owner, "edit_commit", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "review_id": review_id, "review_revision": review_revision_value,
            "confirmed_risk_ids": [],
        })
        done.set()

    thread = threading.Thread(target=run_commit, daemon=True)
    thread.start()
    started.wait(5)
    # wait until the barrier is entered (commit reached the phase)
    entered = False
    for _ in range(80):
        if done.is_set() and commit_result.get("envelope"):
            break
        snap = payload(call(control, "edit_test_barrier", {"action": "snapshot"}))
        if isinstance(snap, dict) and snap.get("entered"):
            entered = True
            break
        time.sleep(0.25)
    if not entered:
        # Reset from the control session: the owner's request pipeline may still be
        # occupied by the in-flight commit, so a same-session reset would queue
        # behind the very commit the reset is meant to unblock.
        call(control, "edit_test_barrier", {"action": "reset"})
        thread.join(timeout=30)
        envelope = commit_result.get("envelope")
        print(f"INFO {tag} commit envelope: {json.dumps(envelope)[:300] if envelope else '<still blocked>'}", flush=True)
        # Drain any active transaction from the owner before closing it.
        drain = payload(call(owner, "edit_status", {}))
        drain_tx = drain.get("transaction", {}) if isinstance(drain.get("transaction"), dict) else {}
        if drain_tx.get("transaction_id"):
            call(owner, "edit_rollback", {"request_id": rid(), "transaction_id": drain_tx["transaction_id"]})
        owner.close()
        check(f"{tag} barrier entered", False, "barrier never entered")
        return
    # Close the owning session mid-phase. The blocked commit holds the session
    # server-side; closing the HTTP session triggers OnSessionClosed handling.
    owner.close()
    released = call(control, "edit_test_barrier", {"action": "release"})
    thread.join(timeout=30)
    # Observer verifies a unique terminal state and no wedge
    time.sleep(0.5)
    status = payload(call(observer, "edit_status", {}))
    state = str(status.get("state", ""))
    busy = bool(status.get("busy"))
    recovery = status.get("recovery")
    # Pre-live barriers roll back; post-live barriers either finalize (the
    # release lets the commit continue on the dispatcher after session close)
    # or leave a resolvable partial. Any of idle / committed_without_checkpoint
    # with an actionable recovery is a legal unique terminal state; a stuck
    # committing with no recovery is the wedge signature.
    legal = (state == "idle") or (state == "committed_without_checkpoint" and isinstance(recovery, dict))
    check(f"{tag} terminal state legal", legal, f"state={state} busy={busy} recovery={json.dumps(recovery or {})[:160]}")
    # Resolve any partial to prove non-wedge
    if state == "committed_without_checkpoint" and isinstance(recovery, dict):
        resolved = call(observer, "edit_recover", {"request_id": rid(), "recovery_id": recovery["recovery_id"], "action": "retry_checkpoint"})
        check(f"{tag} partial resolvable", bool(resolved.get("ok")), json.dumps(resolved)[:200])
    # A new session can immediately start work
    probe = new_client(f"acc019b-probe-{index}")
    begin = call(probe, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    ok_begin = bool(begin.get("ok"))
    if ok_begin:
        tx_probe = payload(begin).get("transaction", {}).get("transaction_id", "")
        call(probe, "edit_rollback", {"request_id": rid(), "transaction_id": tx_probe})
    probe.close()
    check(f"{tag} new session usable", ok_begin, json.dumps(begin)[:200])


def main() -> int:
    control = new_client("acc019b-control")
    observer = new_client("acc019b-observer")
    call(control, "open_files", {"paths": [FIXTURE]})

    for index, barrier in enumerate(BARRIERS):
        barrier_case(control, observer, barrier, index)

    # --- Q1: 16-session transport quota release ---
    # Close the standing control/observer sessions first so the quota phase
    # starts from a clean slate, then open 16 concurrent sessions.
    control.close()
    observer.close()
    time.sleep(1.0)
    sessions = []
    admitted = 0
    for i in range(16):
        try:
            c = new_client(f"acc019b-quota-{i}")
            sessions.append(c)
            admitted += 1
        except Exception:  # noqa: BLE001
            break
    check("Q1 sixteen sessions admitted", admitted == 16, f"admitted={admitted}")
    extra_refused = False
    try:
        overflow = new_client("acc019b-overflow")
        overflow.close()
        extra_refused = False
    except Exception:  # noqa: BLE001
        extra_refused = True
    check("Q1 overflow refused", extra_refused)
    if sessions:
        sessions[0].close()
        time.sleep(0.5)
    readmitted = False
    try:
        again = new_client("acc019b-readmit")
        again.close()
        readmitted = True
    except Exception:  # noqa: BLE001
        readmitted = False
    check("Q1 slot released after close", readmitted)
    for c in sessions[1:]:
        try:
            c.close()
        except Exception:  # noqa: BLE001
            pass

    print(f"ACC019B {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
