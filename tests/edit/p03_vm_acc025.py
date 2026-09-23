#!/usr/bin/env python3
"""ACC-025 through dnSpy's native Edit Type dialog on an isolated instance.

The test-only lineage seam is never the source of the accepted change.  After
the native UI change has independently been observed by list_types and rejected
by edit_begin, a mutate/restore pair is used only as a read-back probe for the
otherwise non-public live fingerprint required by edit_accept_live.  The pair
must restore to the same UI-modified state before acceptance.
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import uuid
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []
CALLS: list[dict] = []
NEXT_ID = 25000
DEPLOYMENT_ROOT = ""
ARCH = "x64"
OUTPUT_ROOT = ""
PACKAGE_ROOT = ""


def configure_isolation(context) -> None:
    global URL, FIXTURE, DEPLOYMENT_ROOT, ARCH, OUTPUT_ROOT, PACKAGE_ROOT
    if not context.ui_deployment_root:
        raise ValueError("ui_deployment_root is required for EDIT-ACC-025 isolation")
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")
    DEPLOYMENT_ROOT = context.ui_deployment_root
    ARCH = context.architecture
    OUTPUT_ROOT = str(Path(context.artifact_root) / "ui-evidence")
    PACKAGE_ROOT = context.artifact_root


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
    global NEXT_ID
    NEXT_ID += 1
    request = {"jsonrpc": "2.0", "id": NEXT_ID, "method": "tools/call",
               "params": {"name": tool, "arguments": args}}
    try:
        response = client.request_object(request)
        raw_body = response.body.decode("utf-8", "replace")
        candidates = [raw_body, *(line[6:] for line in raw_body.splitlines() if line.startswith("data: "))]
        message = None
        for item in candidates:
            try:
                row = json.loads(item)
            except ValueError:
                continue
            if isinstance(row, dict) and row.get("id") == NEXT_ID:
                message = row
                break
        if message is None:
            raise RuntimeError("JSON-RPC response ID not found")
        result = message.get("result") or {}
        structured = result.get("structuredContent")
        content = result.get("content") or []
        parsed = json.loads(content[0]["text"]) if content else None
        value = structured if isinstance(structured, dict) else parsed
        if not isinstance(value, dict):
            value = {"ok": False, "error": message.get("error", {"code": "DRIVER_TRANSPORT", "message": "empty tool response"})}
        CALLS.append({"request": request, "raw_body": raw_body, "message": message,
                      "content_structured_mirror": parsed == structured if isinstance(structured, dict) else None,
                      "parsed_response": value})
        return value
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                value = json.loads(text[start:])
                CALLS.append({"request": request, "transport_exception": text[:300], "parsed_response": value})
                return value
            except json.JSONDecodeError:
                pass
        value = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}
        CALLS.append({"request": request, "transport_exception": text[:300], "parsed_response": value})
        return value


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


def file_sha256(path: str | Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def powershell(script: str) -> str:
    process = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
    if process.returncode:
        raise RuntimeError(process.stderr[-1800:] or process.stdout[-1800:])
    return process.stdout.strip()


def native_type_rename(old_name: str, new_name: str, output: Path) -> dict:
    """Select a real type node, open dnSpy's Edit Type dialog, and press OK."""
    pid = int((Path(DEPLOYMENT_ROOT) / ARCH / "pid.txt").read_text(encoding="utf-8"))
    output.mkdir(parents=True, exist_ok=False)
    before_png = str(output / "native-tree-before.png").replace("'", "''")
    open_png = str(output / "native-open-file.png").replace("'", "''")
    token_png = str(output / "native-search-assemblies.png").replace("'", "''")
    dialog_png = str(output / "native-edit-type-dialog.png").replace("'", "''")
    after_png = str(output / "native-tree-after.png").replace("'", "''")
    script = r"""
$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new()
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class NativeWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
 [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hWnd, int id);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowText(IntPtr hWnd, string text);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@
$root=[System.Windows.Automation.AutomationElement]::RootElement
$pidCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,PIDVALUE)
function Shot($element,$path){$b=$element.Current.BoundingRectangle;$img=[Drawing.Bitmap]::new([int]$b.Width,[int]$b.Height);$g=[Drawing.Graphics]::FromImage($img);$g.CopyFromScreen([int]$b.X,[int]$b.Y,0,0,$img.Size);$img.Save($path,[Drawing.Imaging.ImageFormat]::Png);$g.Dispose();$img.Dispose()}
$proc=Get-Process -Id PIDVALUE;$main=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
if($null -eq $main){throw 'dnSpy main window missing'}
[NativeWindow]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)|Out-Null;$main.SetFocus();Shot $main 'BEFOREPNG'
# Use dnSpy's native File/Open route so the module has a real active document
# tab; MCP open_files alone intentionally does not fabricate a UI selection.
$openCommand=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'Open'))
if($null -eq $openCommand){throw 'native Open toolbar command missing'};$openCommand.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$openDialog=$null
for($n=0;$n -lt 30 -and $null -eq $openDialog;$n++){
 Start-Sleep -Milliseconds 150
 $proc.Refresh();$candidate=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
 if($null -ne $candidate -and $candidate.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle -and $candidate.Current.Name -match '打开|Open'){$openDialog=$candidate}
 if($null -eq $openDialog){$openDialog=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window))|Where-Object {$_.Current.Name -match '打开|Open'})[0]}
}
if($null -eq $openDialog){throw 'native File Open dialog did not open'}
$openHwnd=[IntPtr]$openDialog.Current.NativeWindowHandle;$openButtonHwnd=[NativeWindow]::GetDlgItem($openHwnd,1)
$fileNameElement=[System.Windows.Automation.AutomationElement]::FocusedElement
$openButtonElement=$openDialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'1'))
if($openButtonHwnd -eq [IntPtr]::Zero -or $null -eq $fileNameElement -or $null -eq $openButtonElement){throw 'native File Open controls missing'}
[NativeWindow]::SetForegroundWindow($openHwnd)|Out-Null;[System.Windows.Forms.SendKeys]::SendWait('^a');[System.Windows.Forms.SendKeys]::SendWait('FIXTUREPATH');[System.Windows.Forms.SendKeys]::SendWait('{TAB}');Start-Sleep -Milliseconds 150
Shot $openDialog 'OPENPNG';$openButtonElement.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 1200
$openStill=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window))|Where-Object {$_.Current.Name -match '打开|Open'}).Count
if($openStill -ne 0){throw 'native File Open did not close after Open invoke'}
[NativeWindow]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle)|Out-Null;$main.SetFocus()
# Select the type through dnSpy's native Search Assemblies view. The search
# result activation establishes the real Assembly Explorer/type selection.
[System.Windows.Forms.SendKeys]::SendWait('^+k');Start-Sleep -Milliseconds 500;$searchEdit=[System.Windows.Automation.AutomationElement]::FocusedElement;$searchEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('TestIL.Simple');Start-Sleep -Milliseconds 1500;Shot $main 'TOKENPNG'
$simpleResult=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Simple'))|Where-Object {$_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem})[0]
if($null -ne $simpleResult){$simpleResult.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();$simpleResult.SetFocus();[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')}else{[System.Windows.Forms.SendKeys]::SendWait('{DOWN 8}{ENTER}')};Start-Sleep -Milliseconds 1600;[System.Windows.Forms.SendKeys]::SendWait('^%l');Start-Sleep -Milliseconds 500
$tokenTitle='Search Assemblies (Ctrl+Shift+K)'
[System.Windows.Forms.SendKeys]::SendWait('%{ENTER}')
$dialog=$null
for($n=0;$n -lt 30 -and $null -eq $dialog;$n++){
 Start-Sleep -Milliseconds 150
 $proc.Refresh();$candidate=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
 if($null -ne $candidate -and $candidate.Current.NativeWindowHandle -ne $main.Current.NativeWindowHandle -and $candidate.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)).Count -ge 2){$dialog=$candidate}
 if($null -eq $dialog){$dialog=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window))|Where-Object {$_.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)).Count -ge 2})[0]}
}
if($null -eq $dialog){throw 'native Edit Type dialog did not open'}
$focused=[System.Windows.Automation.AutomationElement]::FocusedElement
$editElements=@($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
$nameEdit=@($editElements|Where-Object {$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ceq 'OLDNAME'})[0]
if($null -eq $nameEdit){$vals=@($editElements|ForEach-Object {$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value});throw ('Edit Type Name field with expected value missing; title='+$dialog.Current.Name+'; values='+($vals -join '|'))}
$value=$nameEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern);$oldValue=$value.Current.Value
$edits=@($editElements|ForEach-Object {[ordered]@{name=$_.Current.Name;value=$_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value;automation_id=$_.Current.AutomationId}})
$buttons=@($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
$dialogTitle=$dialog.Current.Name;Shot $dialog 'DIALOGPNG';$value.SetValue('NEWNAME');Start-Sleep -Milliseconds 100
$ok=@($buttons|Where-Object {$_.Current.Name -match '确定|OK'})[0];if($null -eq $ok){throw 'Edit Type OK button missing'};$okName=$ok.Current.Name;$ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 500
Start-Sleep -Milliseconds 350;Shot $main 'AFTERPNG'
[ordered]@{process_id=PIDVALUE;main_window=$main.Current.Name;selection_route=@{native_open='FIXTUREPATH';search_shortcut='Ctrl+Shift+K';search_query='TestIL.Simple';result_selection='UIA ListItem named Simple (keyboard fallback: Down x8)';assembly_explorer_focus='Ctrl+Alt+L';search_view=$tokenTitle};selected_tree_item='OLDNAME';shortcut='Alt+Enter';dialog_title=$dialogTitle;focused_control_type=$focused.Current.ControlType.ProgrammaticName;focused_value_before=$oldValue;focused_value_after='NEWNAME';default_button=$okName;textboxes=$edits;screenshots=@('BEFOREPNG','OPENPNG','TOKENPNG','DIALOGPNG','AFTERPNG')}|ConvertTo-Json -Depth 8 -Compress
""".replace("PIDVALUE", str(pid)).replace("FIXTUREPATH", str(FIXTURE).replace("'", "''")).replace("OLDNAME", old_name).replace("NEWNAME", new_name).replace(
        "BEFOREPNG", before_png).replace("OPENPNG", open_png).replace("TOKENPNG", token_png).replace("DIALOGPNG", dialog_png).replace("AFTERPNG", after_png)
    facts = json.loads(powershell(script))
    (output / "native-edit-type.uia.json").write_text(
        json.dumps(facts, ensure_ascii=False, indent=2), encoding="utf-8")
    return facts


def type_names(client: DnSpyClient) -> set[str]:
    response = call(client, "list_types", {"assembly_name": "TestIL", "page_size": 1000})
    rows = response.get("items") if isinstance(response, dict) else None
    if not isinstance(rows, list):
        rows = payload(response).get("items", [])
    return {str(row.get("name", row.get("Name", ""))) for row in rows if isinstance(row, dict)}


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc025")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    output = Path(OUTPUT_ROOT or Path(FIXTURE).parent) / ("acc025-" + ARCH + "-" + uuid.uuid4().hex)
    fixture_sha_before = file_sha256(FIXTURE)

    # Precondition: an active lineage must exist; create one through a real
    # commit when the store is empty so the driver is self-contained.
    hist = payload(call(client, "edit_history", {}))
    lineages = hist.get("lineages", []) if isinstance(hist.get("lineages"), list) else []
    active = [row for row in lineages if isinstance(row, dict) and not row.get("superseded_lineage_id")]
    if not active:
        begin0 = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
        tx0 = payload(begin0).get("transaction", {})
        if not tx0.get("transaction_id"):
            print(f"seed begin failed: {json.dumps(begin0)[:300]}", flush=True)
            return 1
        applied0 = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0),
            "operation": {"kind": "module_update", "name": "Acc025SeedModule"},
        })
        review0 = call(client, "edit_review", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0) + 1,
        })
        review0_core = payload(review0).get("review", {})
        commit0 = call(client, "edit_commit", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0) + 1,
            "review_id": review0_core.get("review_id", ""),
            "review_revision": review0_core.get("review_revision", 0),
            "confirmed_risk_ids": review0_core.get("required_confirmation_ids", []),
        })
        check("A0 seed lineage committed", bool(commit0.get("ok")), json.dumps(commit0)[:240])
        hist = payload(call(client, "edit_history", {}))
        lineages = hist.get("lineages", []) if isinstance(hist.get("lineages"), list) else []
        active = [row for row in lineages if isinstance(row, dict) and not row.get("superseded_lineage_id")]
    check("A1 active lineage exists", len(active) == 1, f"active={len(active)} total={len(lineages)}")
    if not active:
        print(f"lineages: {json.dumps(lineages)[:400]}", flush=True)
        return 1
    old_lineage_id = str(active[0].get("lineage_id", ""))
    old_family_id = str(active[0].get("family_id", ""))
    old_head = str(active[0].get("head_checkpoint_id", ""))
    old_count = int(active[0].get("checkpoint_count", 0))
    print(f"INFO old lineage={old_lineage_id} family={old_family_id} head={old_head} checkpoints={old_count}", flush=True)

    # Establish the exact pre-UI live fingerprint, then leave no transaction.
    probe = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    probe_tx = payload(probe).get("transaction", {})
    before_fp = str(payload(probe).get("source", {}).get("live_fingerprint", ""))
    check("A2 pre-UI fingerprint observed", len(before_fp) == 64 and bool(probe_tx.get("transaction_id")), json.dumps(probe)[:240])
    if probe_tx.get("transaction_id"):
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": probe_tx["transaction_id"]})
        check("A2 no active transaction before native UI", bool(rolled.get("ok")), json.dumps(rolled)[:180])

    old_package = Path(PACKAGE_ROOT) / "edit-checkpoints" / (old_lineage_id + ".dnspy-mcp-checkpoints")
    old_package_sha = file_sha256(old_package) if old_package.is_file() else ""
    check("A1 old package exists before UI", bool(old_package_sha), str(old_package))

    native_name = "T068Ui" + ("64" if ARCH == "x64" else "86")
    names_before = type_names(client)
    ui_facts = native_type_rename("Simple", native_name, output)
    names_after = type_names(client)
    check("A2 native Edit Type UIA action recorded", ui_facts.get("shortcut") == "Alt+Enter" and ui_facts.get("focused_value_before") == "Simple", json.dumps(ui_facts)[:300])
    check("A2 native UI rename visible in live module", "Simple" in names_before and native_name in names_after and "Simple" not in names_after, f"before={sorted(names_before)} after={sorted(names_after)}")
    check("A2 native UI leaves fixture bytes unchanged", file_sha256(FIXTURE) == fixture_sha_before, f"before={fixture_sha_before} after={file_sha256(FIXTURE)}")

    # begin must reject with EDIT_LINEAGE_DIVERGED and zero side effects.
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    details = err_details(begin)
    check("A3 begin rejects diverged", err_code(begin) == "EDIT_LINEAGE_DIVERGED", err_code(begin))
    # The process-bound path omits match_basis (it is only meaningful in the
    # cross-process candidate-resolution path); family/lineage identify the
    # diverged lineage in both shapes.
    check("A3 begin error carries family/lineage",
          details.get("family_id") == old_family_id and details.get("lineage_id") == old_lineage_id,
          json.dumps(details)[:240])
    status = payload(call(client, "edit_status", {}))
    check("A3 begin zero side effects", status.get("state") == "idle" and not status.get("busy"),
          json.dumps(status)[:200])

    # Read back the already-diverged live fingerprint without attributing the
    # change to the test seam: begin rejected before this probe, and the probe's
    # own mutation is restored before accept_live.
    observation_mutate = payload(call(client, "edit_test_lineage_mutation", {"action": "mutate", "assembly_name": "TestIL"}))
    observation_restore = payload(call(client, "edit_test_lineage_mutation", {"action": "restore", "assembly_name": "TestIL"}))
    diverged_fp = str(observation_mutate.get("before_fingerprint", ""))
    check("A3 fingerprint probe restored native UI state",
          len(diverged_fp) == 64 and observation_restore.get("restored") is True
          and observation_restore.get("after_fingerprint") == diverged_fp and diverged_fp != before_fp
          and native_name in type_names(client),
          json.dumps({"mutate": observation_mutate, "restore": observation_restore})[:360])

    # accept_live with the WRONG fingerprint must be refused without effects.
    wrong = call(client, "edit_accept_live", {
        "request_id": rid(), "assembly_name": "TestIL", "source_family_id": old_family_id,
        "superseded_lineage_id": old_lineage_id, "expected_live_fingerprint": "0" * 64,
        "acknowledge_new_baseline": True,
    })
    check("A4 wrong fingerprint rejected", err_code(wrong) == "EDIT_HISTORY_CONFLICT", err_code(wrong))
    hist_after_wrong = payload(call(client, "edit_history", {}))
    rows_after_wrong = hist_after_wrong.get("lineages", []) if isinstance(hist_after_wrong.get("lineages"), list) else []
    check("A4 wrong accept no new lineage", len(rows_after_wrong) == len(lineages),
          f"before={len(lineages)} after={len(rows_after_wrong)}")

    # No implicit adoption: both omitted and false acknowledgement are rejected
    # by the advertised input contract before the coordinator can write a root.
    for label, acknowledged in (("omitted", None), ("false", False)):
        args = {"request_id": rid(), "assembly_name": "TestIL", "source_family_id": old_family_id,
                "superseded_lineage_id": old_lineage_id, "expected_live_fingerprint": diverged_fp}
        if acknowledged is not None:
            args["acknowledge_new_baseline"] = acknowledged
        denied = call(client, "edit_accept_live", args)
        no_ack_history = payload(call(client, "edit_history", {}))
        check("A4 " + label + " acknowledgement cannot accept",
              denied.get("ok") is not True and len(no_ack_history.get("lineages", [])) == len(lineages)
              and file_sha256(old_package) == old_package_sha and native_name in type_names(client),
              json.dumps(denied)[:240])

    # Explicit accept of the externally mutated live state.
    accept = call(client, "edit_accept_live", {
        "request_id": rid(), "assembly_name": "TestIL", "source_family_id": old_family_id,
        "superseded_lineage_id": old_lineage_id, "expected_live_fingerprint": diverged_fp,
        "acknowledge_new_baseline": True,
    })
    check("A5 accept_live ok", bool(accept.get("ok")), json.dumps(accept)[:300])
    result = payload(accept)
    new_lineage_id = str(result.get("lineage", {}).get("lineage_id", "")) if isinstance(result.get("lineage"), dict) else ""
    root = result.get("root_checkpoint", {}) if isinstance(result.get("root_checkpoint"), dict) else {}
    check("A5 accept returns new lineage and root",
          new_lineage_id.startswith("lineage-") and new_lineage_id != old_lineage_id and bool(root.get("checkpoint_id")),
          json.dumps(result)[:300])
    check("A5 accept reports superseded lineage", result.get("superseded_lineage_id") == old_lineage_id,
          str(result.get("superseded_lineage_id")))

    # Old package must stay readable.
    old_view = payload(call(client, "edit_history", {"lineage_id": old_lineage_id, "page_size": 100}))
    old_rows = old_view.get("checkpoints", []) if isinstance(old_view.get("checkpoints"), list) else []
    check("A6 old lineage readable", len(old_rows) == old_count and any(c.get("checkpoint_id") == old_head for c in old_rows if isinstance(c, dict)),
          f"rows={len(old_rows)} expected={old_count}")
    check("A6 old package and head byte-stable", file_sha256(old_package) == old_package_sha
          and any(c.get("checkpoint_id") == old_head and c.get("is_head") for c in old_rows if isinstance(c, dict)),
          f"before={old_package_sha} after={file_sha256(old_package)}")

    # New lineage: single root, no parent, no forged operations.
    new_view = payload(call(client, "edit_history", {"lineage_id": new_lineage_id, "page_size": 100}))
    new_rows = new_view.get("checkpoints", []) if isinstance(new_view.get("checkpoints"), list) else []
    single_root = len(new_rows) == 1 and isinstance(new_rows[0], dict) and new_rows[0].get("parent_checkpoint_id") is None
    check("A7 new lineage single root without parent", single_root, json.dumps(new_rows)[:300])

    # Inspect the exact accepted-baseline package bytes: its only operation
    # entry is empty and its baseline contains the native UI type name.
    package_match = None
    for package in Path(PACKAGE_ROOT).rglob("*.dnspy-mcp-checkpoints"):
        with zipfile.ZipFile(package) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            if manifest.get("lineage_id") != new_lineage_id:
                continue
            node = manifest["checkpoints"][0]
            operations = json.loads(archive.read(node["operation_entry"]))
            baseline = archive.read("baseline/module.bin")
            package_match = {"path": str(package), "sha256": file_sha256(package), "manifest": manifest,
                             "operations": operations, "baseline_has_native_name": native_name.encode("utf-8") in baseline}
            break
    (output / "accepted-package.json").write_text(json.dumps(package_match, ensure_ascii=False, indent=2), encoding="utf-8")
    check("A7 accepted root has no forged semantic operations",
          isinstance(package_match, dict) and package_match["operations"].get("operations") == [],
          json.dumps(package_match, default=str)[:300])
    check("A7 accepted baseline carries native UI rename",
          isinstance(package_match, dict) and package_match["baseline_has_native_name"] and native_name in type_names(client),
          json.dumps(package_match, default=str)[:260])

    # The superseded link is visible from the new lineage summary.
    hist_final = payload(call(client, "edit_history", {}))
    rows_final = hist_final.get("lineages", []) if isinstance(hist_final.get("lineages"), list) else []
    new_row = next((r for r in rows_final if isinstance(r, dict) and r.get("lineage_id") == new_lineage_id), {})
    check("A8 new lineage links superseded", new_row.get("superseded_lineage_id") == old_lineage_id,
          json.dumps(new_row)[:240])

    # The coordinator is usable again: bind the accepted baseline and commit a
    # normal MCP edit, not only a begin/rollback handshake.
    begin2 = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    begin_ok = bool(begin2.get("ok"))
    check("A9 begin usable after accept", begin_ok, json.dumps(begin2)[:240])
    if begin_ok:
        check("A9 new baseline fingerprint is accepted UI fingerprint",
              payload(begin2).get("source", {}).get("live_fingerprint") == diverged_fp,
              json.dumps(payload(begin2).get("source", {}))[:220])
        tx2 = payload(begin2).get("transaction", {}).get("transaction_id", "")
        rev2 = payload(begin2).get("transaction", {}).get("work_revision", 0)
        applied2 = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx2,
            "expected_revision": rev2, "operation": {"kind": "module_update", "name": "T068AfterUiAccept"}})
        check("A9 normal apply after accept", applied2.get("ok") is True, json.dumps(applied2)[:200])
        review2 = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx2,
            "expected_revision": rev2 + 1})
        reviewed2 = payload(review2).get("review", {})
        check("A9 normal review after accept", review2.get("ok") is True, json.dumps(review2)[:200])
        committed2 = call(client, "edit_commit", {"request_id": rid(), "transaction_id": tx2,
            "expected_revision": rev2 + 1, "review_id": reviewed2.get("review_id", ""),
            "review_revision": reviewed2.get("review_revision", 0),
            "confirmed_risk_ids": reviewed2.get("required_confirmation_ids", [])})
        final_history = payload(call(client, "edit_history", {"lineage_id": new_lineage_id, "page_size": 100}))
        check("A9 normal commit after accept", committed2.get("ok") is True
              and len(final_history.get("checkpoints", [])) == 2
              and file_sha256(old_package) == old_package_sha,
              json.dumps(committed2)[:260])

    (output / "calls.json").write_text(json.dumps(CALLS, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "summary.json").write_text(json.dumps({"arch": ARCH, "fixture": FIXTURE,
        "fixture_sha256_before": fixture_sha_before, "fixture_sha256_after": file_sha256(FIXTURE),
        "native_name": native_name, "before_live_fingerprint": before_fp,
        "accepted_live_fingerprint": diverged_fp, "old_lineage_id": old_lineage_id,
        "old_head_checkpoint_id": old_head, "old_package_sha256": old_package_sha,
        "passes": PASSES, "failures": FAILURES}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"EVIDENCE {output}", flush=True)
    print(f"ACC025 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
