#!/usr/bin/env python3
"""T027: real debugger negatives for self-thrown and dependency-thrown COR_E_STRONGNAME."""
from __future__ import annotations

import hashlib
import json
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402


def rid() -> str:
    return str(uuid.uuid4())


def sha(path: str) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def payload(value: dict) -> dict:
    row = value.get("result") if isinstance(value, dict) else None
    return row if isinstance(row, dict) else {}


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}


def exercise(client: DnSpyClient, target: str, assembly: str, arch: str) -> dict:
    launched = call(client, "debug_launch", {"request_id": rid(), "target_path": target,
        "expected_sha256": sha(target), "launch_mode": "net48-exe", "architecture": arch,
        "break_kind": "none"})
    launch = payload(launched)
    session = str(launch.get("session_id", ""))
    events: list[dict] = []
    deadline = time.monotonic() + 40
    while session and time.monotonic() < deadline:
        waited = call(client, "debug_wait_event", {"session_id": session, "after_cursor": 0,
            "limit": 64, "kinds": ["exception", "process_exited", "start_failed"], "timeout_ms": 2500})
        got = payload(waited).get("events", [])
        if isinstance(got, list):
            events = [row for row in got if isinstance(row, dict)]
        if any(row.get("kind") in ("exception", "process_exited", "start_failed") for row in events):
            break
    exception = next((row for row in events if row.get("kind") == "exception"), {})
    begun = call(client, "edit_begin", {"assembly_name": assembly, "request_id": rid()})
    txrow = payload(begun).get("transaction", {})
    tx = str(txrow.get("transaction_id", ""))
    revision = int(txrow.get("work_revision", 0))
    evidence = {"session_id": session, "event_cursor": int(exception.get("cursor", 0)), "event_kind": "exception"}
    applied = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "operation": {"kind": "strong_name_remove", "dynamic_failure": evidence}})
    mismatch = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "operation": {"kind": "strong_name_remove", "dynamic_failure":
        {"session_id": session + "-wrong", "event_cursor": int(exception.get("cursor", 0)), "event_kind": "exception"}}})
    expired = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "operation": {"kind": "strong_name_remove", "dynamic_failure":
        {"session_id": session, "event_cursor": int(exception.get("cursor", 0)) + 999, "event_kind": "exception"}}})
    rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx}) if tx else {}
    status = call(client, "edit_status", {})
    if session:
        call(client, "debug_terminate", {"request_id": rid(), "session_id": session,
            "generation": int(launch.get("generation", 0))})
    return {"launch": launched, "events": events, "evidence": evidence, "apply": applied,
            "session_mismatch": mismatch, "expired_cursor": expired, "rollback": rolled, "status": status,
            "pass": bool(exception) and not applied.get("ok") and not mismatch.get("ok") and not expired.get("ok")}


def main() -> int:
    if len(sys.argv) != 5:
        raise SystemExit("usage: p03_vm_t027_negative.py <url> <arch> <fixture-dir> <result-json>")
    url, arch, fixture_dir, result_path = sys.argv[1:]
    fixture = Path(fixture_dir)
    targets = [(fixture / "SelfThrow" / "SelfThrow.exe", "SelfThrow"),
               (fixture / "DependencyHost" / "DependencyHost.exe", "DependencyHost")]
    client = DnSpyClient(url, client_name=f"t027-r2-{arch}", timeout=180)
    client.initialize()
    try:
        opened = call(client, "open_files", {"paths": [str(path) for path, _ in targets]
            + [str(fixture / "DependencyHost" / "ThrowingDependency.dll")]})
        rows = {assembly: exercise(client, str(path), assembly, arch) for path, assembly in targets}
        result = {"arch": arch, "opened": opened, "cases": rows,
                  "pass": all(row["pass"] for row in rows.values())}
    finally:
        client.close()
    Path(result_path).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"arch": arch, "pass": result["pass"],
        "accepted": {name: bool(row["apply"].get("ok")) for name, row in rows.items()}}, ensure_ascii=False))
    return 0 if result["pass"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
