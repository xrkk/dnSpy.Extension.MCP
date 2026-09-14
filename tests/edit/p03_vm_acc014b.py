#!/usr/bin/env python3
"""ACC-014 Windows adapter artifact-level injection (round 60): a REAL file
lock on the owned temp package (no product seam) turns the undo_live cleanup
into the genuine WindowsEditCheckpointStore.DeleteTemp IOException path:
finalize fault -> partial(undo_live) -> cleanup fails on the locked temp ->
aborted_temp_cleanup (committing, only cleanup_temp allowed) -> lock released
-> cleanup_temp -> idle, temp gone."""

from __future__ import annotations

import json
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
CHECKPOINTS = Path.home() / "Desktop" / "dnspy-mcp-artifacts" / "edit-checkpoints"
WORK_ROOT = Path(__file__).resolve().parent / "locker"
LOCKER = r"""
import sys, time
from pathlib import Path
path, signal_file = sys.argv[1], sys.argv[2]
handle = open(path, 'rb')   # read-only: identity observation still works, but CPython omits FILE_SHARE_DELETE so deletion stays blocked
Path(signal_file.replace('.release', '.held')).write_text('held', encoding='utf-8')
deadline = time.time() + 240
while time.time() < deadline and not Path(signal_file).exists():
    time.sleep(0.2)
handle.close()
"""
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE, CHECKPOINTS, WORK_ROOT
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")
    CHECKPOINTS = Path(context.checkpoint_store)
    WORK_ROOT = Path(context.work_root)


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


def main() -> int:
    control = DnSpyClient(URL, client_name="p03-vm-acc014b-ctrl", timeout=60)
    control.initialize()
    call(control, "open_files", {"paths": [FIXTURE]})

    # Seed a lineage so the temp write targets a known lineage id.
    owner = DnSpyClient(URL, client_name="p03-vm-acc014b-owner", timeout=60)
    owner.initialize()
    begin = call(owner, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
    call(owner, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "Acc014BSeed"},
    })
    review = call(owner, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
    review_core = payload(review).get("review", {})
    committed = call(owner, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": review_core.get("review_id", ""),
        "review_revision": review_core.get("review_revision", 0),
        "confirmed_risk_ids": [],
    })
    if not committed.get("ok"):
        print(f"seed failed: {json.dumps(committed)[:240]}", flush=True)
        return 1
    lineage_id = str(payload(committed).get("history", {}).get("lineage_id", ""))
    check("E1 seed lineage", bool(lineage_id))

    # Second transaction parked just before finalize, with the finalize fault armed.
    begin2 = call(owner, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx2 = payload(begin2).get("transaction", {}).get("transaction_id", "")
    revision2 = int(payload(begin2).get("transaction", {}).get("work_revision", 0))
    call(owner, "edit_apply", {
        "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "Acc014BLocked"},
    })
    review2 = call(owner, "edit_review", {"request_id": rid(), "transaction_id": tx2, "expected_revision": revision2 + 1})
    review2_core = payload(review2).get("review", {})
    call(owner, "edit_test_storage_fault", {"action": "arm", "stage": "finalize"})
    call(control, "edit_test_barrier", {"action": "reset"})
    armed = call(owner, "edit_test_barrier", {"action": "arm", "name": "commit_after_live_complete"})
    check("E2 barriers armed", armed.get("ok"), json.dumps(armed)[:160])

    result: dict = {}
    done = threading.Event()

    def run_commit() -> None:
        result["envelope"] = call(owner, "edit_commit", {
            "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2 + 1,
            "review_id": review2_core.get("review_id", ""),
            "review_revision": review2_core.get("review_revision", 0),
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
        check("E3 barrier entered", False, "never entered")
        return 1

    # Take a REAL filesystem lock on the owned temp package.
    temps = sorted(CHECKPOINTS.glob(f"{lineage_id}.dnspy-mcp-checkpoints.tmp-*"))
    check("E3 temp file exists on disk", len(temps) == 1, f"count={len(temps)} pattern={lineage_id}.dnspy-mcp-checkpoints.tmp-*")
    if not temps:
        call(control, "edit_test_barrier", {"action": "reset"})
        thread.join(timeout=30)
        return 1
    temp_path = temps[0]
    work_dir = WORK_ROOT / "acc014-locker"
    work_dir.mkdir(parents=True, exist_ok=True)
    lock_script = work_dir / "locker.py"
    lock_script.write_text(LOCKER.lstrip(), encoding="utf-8")
    held_signal = work_dir / f"{temp_path.name}.held"
    release_signal = work_dir / f"{temp_path.name}.release"
    held_signal.unlink(missing_ok=True)
    release_signal.unlink(missing_ok=True)
    locker = subprocess.Popen([sys.executable, str(lock_script), str(temp_path), str(release_signal)])
    for _ in range(50):
        if held_signal.is_file():
            break
        time.sleep(0.1)
    check("E4 real file lock held", held_signal.is_file())

    # Release the barrier: finalize fault fires -> partial; undo_live runs the
    # real cleanup, which fails on the locked file.
    call(control, "edit_test_barrier", {"action": "release"})
    thread.join(timeout=60)
    envelope = result.get("envelope") or {}
    check("E5 finalize fault partial", err_code(envelope) == "EDIT_CHECKPOINT_COMMIT_FAILED", json.dumps(envelope)[:240])
    details = err_details(envelope)
    recovery_id = str(details.get("recovery_id", ""))

    undo = call(control, "edit_recover", {"request_id": rid(), "recovery_id": recovery_id, "action": "undo_live"})
    undo_details = err_details(undo)
    undo_recovery_id = str(undo_details.get("recovery_id", ""))
    check("E6 real adapter cleanup fails on lock",
          err_code(undo) == "EDIT_CHECKPOINT_CLEANUP_FAILED" and undo_details.get("recovery_kind") == "aborted_temp_cleanup",
          json.dumps(undo)[:300])
    blocked_status = payload(call(control, "edit_status", {}))
    allowed = undo_details.get("allowed_actions", [])
    check("E7 wedged committing with only cleanup_temp",
          blocked_status.get("state") == "committing" and allowed == ["cleanup_temp"],
          f"state={blocked_status.get('state')} allowed={allowed}")

    # Release the lock; the recovery completes.
    release_signal.write_text("release", encoding="utf-8")
    locker.wait(timeout=30)
    cleaned = call(control, "edit_recover", {"request_id": rid(), "recovery_id": undo_recovery_id, "action": "cleanup_temp"})
    check("E8 cleanup_temp succeeds after unlock", bool(cleaned.get("ok")), json.dumps(cleaned)[:240])
    final_status = payload(call(control, "edit_status", {}))
    check("E8 idle with no recovery", final_status.get("state") == "idle" and final_status.get("recovery") is None,
          json.dumps(final_status)[:200])
    leftover = sorted(CHECKPOINTS.glob(f"{lineage_id}.dnspy-mcp-checkpoints.tmp-*"))
    check("E8 temp removed from disk", len(leftover) == 0, f"leftover={len(leftover)}")

    owner.close()
    control.close()
    print(f"ACC014B {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
