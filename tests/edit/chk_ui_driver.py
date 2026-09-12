#!/usr/bin/env python3
"""CHK-001/CHK-002 remediation evidence driver (VM side).  REQ-016 as written:
the dnSpy UI offers a local cancel of the CURRENT transaction while its owner
session is still connected and no operation/commit is executing, and the idle
explorer browses the checkpoint history with per-checkpoint rows.

UI interaction uses PowerShell UIAutomation against the window's stable
AutomationIds (McpEditExplorer / McpEditStateLine / McpEditCancelLine /
McpEditCancelButton / McpEditTree); the management-MCP text tree does not
expose WPF TextBlock/Button content."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []

VM_URL = "http://192.168.204.240:28787/mcp"


def open_explorer_via_vm() -> str:
    import re as _re
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from run_p01_vm_tests import UiMcpClient
    vm = UiMcpClient(VM_URL, timeout=90, client_name="chk-ui-open")
    vm.initialize()

    def tree_text() -> str:
        snap = vm.call_tool_json("Snapshot", {"use_ui_tree": True, "use_vision": False})
        if isinstance(snap, list):
            return "\n".join(str(part) for part in snap)
        return str(snap)

    def click_line(marker: str) -> bool:
        text = tree_text()
        for line in text.splitlines():
            if marker in line:
                loc = _re.search(r"\((\d+),(\d+)\)", line)
                if loc:
                    vm.call_tool_json("Click", {"loc": [int(loc.group(1)), int(loc.group(2))]})
                    return True
                break
        return False

    vm.call_tool_json("App", {"mode": "switch", "name": "dnSpy"})
    time.sleep(0.8)
    clicked = False
    for _attempt in range(3):
        if not click_line("视图(V)"):
            vm.call_tool_json("Shortcut", {"shortcut": "alt+v"})
            time.sleep(0.8)
        time.sleep(1.0)
        if click_line("MCP Edit Explorer"):
            clicked = True
            break
        time.sleep(0.8)
    time.sleep(1.4)
    vm.close()
    return "clicked" if clicked else "menu-entry-missing"


UIA_READ = r'''
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root=[System.Windows.Automation.AutomationElement]::RootElement
$exp=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'McpEditExplorer')))
if($null -eq $exp){ throw 'explorer window not open' }
function Read-Text($id){
  $e=$exp.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)))
  if($null -eq $e){ return $null }
  $name=[string]$e.Current.Name
  if([string]::IsNullOrWhiteSpace($name)){
    try {
      $tp=$e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
      $name=[string]$tp.DocumentRange.GetText(-1)
    } catch { }
  }
  return $name
}
$btn=$exp.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'McpEditCancelButton')))
$treeItems=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))))
foreach ($item in $treeItems) {
  try {
    $toggle = $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($toggle.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $toggle.Expand() }
  } catch { }
}
Start-Sleep -Milliseconds 400
$treeItems=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))) | ForEach-Object { $_.Current.Name })
$buttons=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name })
[ordered]@{
  state_line = Read-Text 'McpEditStateLine'
  cancel_line = Read-Text 'McpEditCancelLine'
  cancel_enabled = if($btn){ $btn.Current.IsEnabled } else { $null }
  cancel_name = if($btn){ $btn.Current.Name } else { $null }
  tree_items = $treeItems
  buttons = $buttons
} | ConvertTo-Json -Compress -Depth 3
'''

UIA_CLICK_CANCEL = r'''
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root=[System.Windows.Automation.AutomationElement]::RootElement
$exp=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'McpEditExplorer')))
if($null -eq $exp){ throw 'explorer window not open' }
$btn=$exp.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'McpEditCancelButton')))
if($null -eq $btn){ throw 'cancel button not found' }
if(-not $btn.Current.IsEnabled){ throw 'cancel button disabled' }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 600
'clicked'
'''


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


def error_code(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def ps(script: str) -> str:
    result = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True, text=True, timeout=60)
    output = (result.stdout or "").strip()
    if not output:
        raise RuntimeError(f"powershell empty; stderr={(result.stderr or '')[:300]}")
    return output.split("Response:")[-1].strip()


def read_ui() -> dict:
    return json.loads(ps(UIA_READ))


def main() -> int:
    client = DnSpyClient(URL, client_name="chk-ui-remediation", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    opened = open_explorer_via_vm()
    ui_probe_ok = False
    try:
        ui_probe_ok = isinstance(read_ui(), dict)
    except Exception:  # noqa: BLE001
        ui_probe_ok = False
    check("U2 explorer window opened", "clicked" in opened and ui_probe_ok, opened[:200])
    ui = read_ui()
    check("U3 idle explorer shows lineage section",
          any("checkpoint lineages" in str(t) for t in ui.get("tree_items", [])), json.dumps(ui)[:300])

    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    check("W1 transaction began", bool(tx), json.dumps(begin)[:200])
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "TestILUiChk"}})
    check("W1 operation staged", bool(applied.get("ok")), json.dumps(applied)[:240])

    time.sleep(1.8)  # 1s poll refresh
    ui = read_ui()
    mcp_state = payload(call(client, "edit_status", {})).get("state")
    state_line = str(ui.get("state_line", ""))
    tree_items = [str(t) for t in ui.get("tree_items", [])]
    check("W2 UI shows same state as MCP",
          mcp_state == "editing" and "editing" in state_line and any("staged operations" in t for t in tree_items),
          f"mcp={mcp_state} ui={json.dumps(ui, ensure_ascii=False)[:400]}")

    # CHK-001: owner session still connected, no operation in flight — the
    # cancel must be ENABLED (REQ-016 local cancel of the current transaction).
    cancel_line = str(ui.get("cancel_line", ""))
    check("W3 cancel enabled while owner active",
          ui.get("cancel_enabled") is True and "canceled locally" in cancel_line,
          json.dumps({k: ui.get(k) for k in ("cancel_enabled", "cancel_line")}, ensure_ascii=False)[:400])

    # W4: the window's only action button is the cancel; no commit/restore/export.
    buttons = [str(b) for b in ui.get("buttons", [])]
    forbidden = [b for b in buttons if any(w in b.lower() for w in ("commit", "restore", "export"))]
    check("W4 no commit/restore/export UI entry", not forbidden and len(buttons) >= 1, str(buttons))

    # W5: click cancel with the owner session still alive -> rollback.
    click_result = ps(UIA_CLICK_CANCEL)
    time.sleep(1.2)
    after_status = payload(call(client, "edit_status", {}))
    gone = error_code(call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
        "operation": {"kind": "module_update", "name": "MustFail"}})) == "EDIT_TRANSACTION_NOT_FOUND"
    ui = read_ui()
    check("W5 owner-online cancel rolled the transaction back",
          "clicked" in click_result and after_status.get("state") == "idle" and gone
          and "last cancel result: canceled" in str(ui.get("cancel_line", "")),
          json.dumps({"click": click_result[:80], "status": after_status.get("state"), "gone": gone,
                      "line": ui.get("cancel_line")}, ensure_ascii=False)[:400])

    # CHK-002: after a real commit the idle explorer browses the checkpoint
    # history with per-checkpoint rows (parent/kind/image/semantic).
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "TestILCommitted"}})
    applied_row = payload(applied).get("transaction", {})
    if applied_row:
        revision = int(applied_row.get("work_revision", revision))
    reviewed = call(client, "edit_review", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": str(review_row.get("review_id", "")), "review_revision": revision,
        "confirmed_risk_ids": [str(r) for r in (review_row.get("required_confirmation_ids") or [])]})
    check("W6 checkpoint committed", bool(committed.get("ok")), json.dumps(committed)[:300])

    time.sleep(2.0)
    checkpoint_rows: list[str] = []
    state_line = ""
    for _attempt in range(4):
        ui = read_ui()
        state_line = str(ui.get("state_line", ""))
        tree_items = [str(t) for t in ui.get("tree_items", [])]
        checkpoint_rows = [t for t in tree_items if "parent" in t and "kind" in t and "image" in t and "semantic" in t]
        if "idle" in state_line and len(checkpoint_rows) >= 2:
            break
        time.sleep(1.5)
    check("W7 idle UI browses checkpoint history",
          "idle" in state_line and len(checkpoint_rows) >= 2,
          f"state={state_line} rows={checkpoint_rows[:3]}")

    client.close()
    run_id = os.environ.get("CHK_RUN_ID", "chk-remediation")
    artifacts = Path(os.environ["USERPROFILE"]) / "Desktop" / "dnspy-mcp-artifacts" / "edit-tests" / run_id / "chk001-ui"
    artifacts.mkdir(parents=True, exist_ok=True)
    summary = {"case": "chk001-ui", "status": "PASS" if not FAILURES else "FAIL",
               "passes": PASSES, "failures": FAILURES}
    (artifacts / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"chk001-ui {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
