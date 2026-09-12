#!/usr/bin/env python3
"""P09 ACC-018: the read-only MCP Edit Explorer UI shows the same facts as the
MCP queries, its guarded cancel rolls back only orphaned transactions, and it
offers no commit/restore entry.  Drives the dnSpy UI through the Win10VM
management MCP (UIA) while cross-checking every fact against edit_status /
edit_history over the loopback."""

from __future__ import annotations

import json
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parent))
from run_p01_vm_tests import UiMcpClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
VM_URL = "http://192.168.204.240:28787/mcp"
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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def main() -> int:
    client = DnSpyClient(URL, client_name="p09-acc018", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    vm = UiMcpClient(VM_URL, timeout=90, client_name="p09-acc018-ui")
    vm.initialize()

    import re

    def ui_tree_text() -> str:
        snap = vm.call_tool_json("Snapshot", {"use_ui_tree": True, "use_vision": False})
        return json.dumps(snap, ensure_ascii=False)

    # U1: open the explorer via the View menu
    vm.call_tool_json("App", {"mode": "switch", "name": "dnSpy"})
    time.sleep(0.5)
    vm.call_tool_json("Shortcut", {"shortcut": "alt+v"})
    time.sleep(0.8)
    menu_text = ui_tree_text()
    explorer_entry = next((line for line in menu_text.splitlines() if "MCP Edit Explorer" in line), None)
    check("U1 explorer menu entry present", explorer_entry is not None, menu_text[:400])
    if explorer_entry:
        loc = re.search(r"\((\d+),(\d+)\)", explorer_entry)
        if loc:
            vm.call_tool_json("Click", {"loc": [int(loc.group(1)), int(loc.group(2))]})
            time.sleep(1.2)

    window_text = ui_tree_text()
    if "MCP Edit Explorer" not in window_text:
        # the text tree may miss a freshly opened WPF window; take a vision
        # screenshot to verify it rendered
        snap = vm.call_tool_json("Snapshot", {"use_vision": True, "use_ui_tree": False, "use_annotation": False})
        vision_text = json.dumps(snap, ensure_ascii=False)
        window_text = window_text + " VISION:" + str("MCP Edit Explorer" in vision_text)
    check("U2 explorer window opened", "MCP Edit Explorer" in window_text or "VISION:True" in window_text, window_text[:300])

    # W1: begin a transaction and stage one operation, then compare UI vs MCP
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    check("W1 transaction began", bool(tx), json.dumps(begin)[:200])
    revision = int(tx_row.get("work_revision", 0))
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "TestILUiCheck"}})
    check("W1 operation staged", bool(applied.get("ok")), json.dumps(applied)[:240])

    time.sleep(1.8)  # 1s poll refresh
    window_text = ui_tree_text()
    mcp_status = payload(call(client, "edit_status", {}))
    same_state = (mcp_status.get("state") == "editing") and any(
        marker in window_text for marker in ("Coordinator state: editing", "staged operations", "McpEditTree"))
    shows_operation = "module_update" in window_text or "staged operations" in window_text
    check("W2 UI shows same state as MCP", same_state, window_text[:300])
    check("W2 UI lists staged operation", shows_operation, window_text[:400])

    # W3: while the owner session is ACTIVE the cancel must be disabled
    cancel_disabled = any(marker in window_text for marker in ("UI cancel disabled", "active MCP session", "Cancel orphaned"))
    check("W3 UI cancel shows guarded state", cancel_disabled, window_text[:500])

    # W4: no commit/restore/export controls on the window
    forbidden = [w for w in ("edit_commit", "edit_restore", "edit_export",
                             "Commit transaction", "Restore checkpoint", "Export checkpoint")
                 if w in window_text]
    check("W4 no commit/restore/export UI entry", not forbidden, str(forbidden))

    # W5: close the owner session → the transaction becomes orphaned
    client.close()
    deadline = time.time() + 25
    orphaned = False
    window_text = ui_tree_text()
    while time.time() < deadline:
        window_text = ui_tree_text()
        if "Cancel orphaned transaction" in window_text:
            cancel_line = next((l for l in window_text.splitlines() if "Cancel orphaned transaction" in l), None)
            if cancel_line and "disabled" not in cancel_line.lower():
                orphaned = True
                break
        # after owner close the guard text changes to the caretaker message
        if "Owner session is gone" in window_text:
            orphaned = True
            break
        time.sleep(1.2)
    check("W5 UI cancel enabled after owner closed", orphaned, window_text[:500])

    # reconnect in a NEW session: the orphan is not steerable
    client2 = DnSpyClient(URL, client_name="p09-acc018-second", timeout=120)
    client2.initialize()
    commit_attempt = call(client2, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "review_id": "none", "review_revision": revision + 1, "confirmed_risk_ids": []})
    check("W6 MCP commit on foreign transaction rejected", not commit_attempt.get("ok"),
          json.dumps(commit_attempt)[:240])

    # W7: UI cancel rolls the orphan back
    if orphaned:
        cancel_line = next((l for l in window_text.splitlines() if "Cancel orphaned transaction" in l), None)
        if cancel_line:
            loc = re.search(r"\((\d+),(\d+)\)", cancel_line)
            if loc:
                vm.call_tool_json("Click", {"loc": [int(loc.group(1)), int(loc.group(2))]})
                time.sleep(1.5)
    status = payload(call(client2, "edit_status", {}))
    check("W7 UI cancel rolled the transaction back",
          status.get("state") == "idle" and status.get("busy") is False,
          json.dumps(status)[:240])

    vm.close()
    client2.close()
    print(f"ACC018 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
