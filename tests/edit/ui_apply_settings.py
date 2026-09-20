#!/usr/bin/env python3
"""Open dnSpy Options through Win10VM MCP, then apply the MCP page through UIA."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Mapping
from urllib.request import ProxyHandler, build_opener

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
DESKTOP_REGION = [0, 0, 1696, 903]

from dnspy_mcp import DnSpyClient, DnSpyProtocolError  # noqa: E402
from dnspy_mcp.client import _MISSING  # noqa: E402


def decode_sse(body: bytes) -> Mapping[str, Any]:
    data: list[str] = []
    messages: list[Mapping[str, Any]] = []
    for line in body.decode("utf-8").splitlines() + [""]:
        if not line:
            if data:
                value = json.loads("\n".join(data))
                if isinstance(value, dict):
                    messages.append(value)
                data.clear()
        elif line.startswith("data:"):
            data.append(line[5:].lstrip())
    if not messages:
        raise DnSpyProtocolError("Win10VM returned SSE without a JSON-RPC message")
    return messages[-1]


class UiMcpClient(DnSpyClient):
    def __init__(self, *args: Any, **kwargs: Any) -> None:
        # Win10VM's MCP endpoint returns each JSON-RPC result as a finite SSE
        # response.  Keep the urllib one-request transport here; the persistent
        # HTTP path in the dnSpy client is intentionally for dnSpy HTTP.sys.
        kwargs.setdefault("opener", build_opener(ProxyHandler({})))
        super().__init__(*args, **kwargs)

    def request(
        self,
        method: str,
        params: Any = _MISSING,
        *,
        request_id: str | int | None | object = _MISSING,
        include_session: bool = True,
        timeout: float | None = None,
    ) -> Any:
        actual_id = self._next_id() if request_id is _MISSING else request_id
        message: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if actual_id is not None:
            message["id"] = actual_id
        if params is not _MISSING:
            message["params"] = params
        response = self.request_object(message, include_session=include_session, timeout=timeout)
        response.raise_for_status()
        if actual_id is None:
            return None
        content_type = response.header("Content-Type", "") or ""
        payload = decode_sse(response.body) if "text/event-stream" in content_type.casefold() else response.json()
        if payload.get("id") != actual_id:
            raise DnSpyProtocolError("Win10VM response id mismatch", response=response)
        error = payload.get("error")
        if isinstance(error, dict):
            raise DnSpyProtocolError(str(error.get("message", "Win10VM MCP error")), response=response)
        if "result" not in payload:
            raise DnSpyProtocolError("Win10VM response has no result", response=response)
        return payload["result"]


def result_text(value: Any) -> str:
    if isinstance(value, dict) and isinstance(value.get("result"), str):
        return value["result"]
    if isinstance(value, list):
        return "\n".join(item for item in value if isinstance(item, str))
    if isinstance(value, str):
        return value
    return json.dumps(value, ensure_ascii=False)


def menu_location(tree: str, labels: tuple[str, ...]) -> list[int]:
    for line in tree.splitlines():
        if not any(label.lower() in line.lower() for label in labels):
            continue
        match = re.search(r"\((\d+),(\d+)\)", line)
        if match:
            return [int(match.group(1)), int(match.group(2))]
    raise RuntimeError(f"menu item was not found: {labels}; tree={tree[:2000]!r}")


def matching_line(tree: str, labels: tuple[str, ...]) -> str | None:
    for line in tree.splitlines():
        if any(label.lower() in line.lower() for label in labels):
            return line
    return None


def line_location(line: str, label: str) -> list[int]:
    match = re.search(r"\((\d+),(\d+)\)", line)
    if not match:
        raise RuntimeError(f"{label} has no UI coordinate: {line!r}")
    return [int(match.group(1)), int(match.group(2))]


def is_mcp_settings_page(tree: str) -> bool:
    return matching_line(tree, ("\u542f\u7528 MCP \u670d\u52a1\u5668", "Enable MCP server")) is not None


# --- T039: explicit UI target ownership (no first-instance fallback) -----------------
#
# Every UI operation must belong to one explicit target: a PID plus the canonical exe path
# the caller expects. The pair is verified on the VM BEFORE any UI write; a missing PID, a
# dead process, an unreadable path, a path mismatch or a window-less process refuses the
# operation. The historical "Get-Process dnSpy | Select-Object -First 1" pattern could
# silently drive an unrelated (e.g. protected) instance and is removed.


def _ps_json(client: DnSpyClient, script: str, timeout: int = 60) -> dict:
    """Run a PowerShell snippet and extract its single JSON object result.

    The management endpoint may wrap stdout with CLIXML/progress noise, so the JSON
    object is located by brace matching rather than parsed whole."""
    value = client.call_tool_json("PowerShell", {"command": script, "timeout": timeout})
    if isinstance(value, dict) and set(value) == {"result"} and isinstance(value["result"], str):
        value = value["result"]
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
    start = text.find("{")
    while start != -1:
        for end in range(len(text), start, -1):
            candidate = text[start:end]
            try:
                parsed = json.loads(candidate)
            except ValueError:
                continue
            if isinstance(parsed, dict):
                return parsed
        start = text.find("{", start + 1)
    raise RuntimeError(f"no JSON object in PowerShell result: {text[:200]!r}")


def resolve_target(client: DnSpyClient, pid: int, exe_path: str) -> dict:
    """Verify the (pid, exe_path) pair on the VM; return pid/path/hwnd or raise."""
    if pid is None or not exe_path:
        raise RuntimeError("UI target refused: no explicit target (pid + exe path) supplied")
    script = (
        "$ErrorActionPreference='Stop';"
        "$p=Get-Process -Id " + str(int(pid)) + " -ErrorAction SilentlyContinue;"
        "if(-not $p){ [pscustomobject]@{ok=$false;reason='no-such-pid';path=$null;hwnd=0}|ConvertTo-Json -Compress; exit }"
        "$path=$p.Path;"
        "if(-not $path){ [pscustomobject]@{ok=$false;reason='path-unreadable';path=$null;hwnd=0}|ConvertTo-Json -Compress; exit }"
        "$expect='" + str(exe_path).replace("'", "''") + "';"
        "$match=($path -ieq $expect);"
        "$hwnd=if($p.MainWindowHandle){[int64]$p.MainWindowHandle}else{0};"
        "[pscustomobject]@{ok=($match -and $hwnd -ne 0);reason=if(-not $match){'path-mismatch'}elseif($hwnd -eq 0){'no-main-window'}else{'ok'};path=$path;hwnd=$hwnd}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("ok"):
        raise RuntimeError(f"UI target refused: pid={pid} exe={exe_path!r} reason={row.get('reason')} actual={row.get('path')!r}")
    return {"pid": int(pid), "path": row["path"], "hwnd": row["hwnd"]}


def find_target(client: DnSpyClient, exe_path: str) -> dict:
    """Locate the SINGLE windowed process whose path equals exe_path (ambiguity refuses)."""
    script = (
        "$ErrorActionPreference='Stop';"
        "$rows=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | "
        "Where-Object { $_.Path -and $_.Path -ieq '" + str(exe_path).replace("'", "''") + "' -and $_.MainWindowHandle -ne 0 } | "
        "ForEach-Object { [pscustomobject]@{pid=$_.Id;hwnd=[int64]$_.MainWindowHandle} });"
        "[pscustomobject]@{count=$rows.Count;rows=$rows}|ConvertTo-Json -Depth 4 -Compress"
    )
    row = _ps_json(client, script)
    if row.get("count") != 1:
        raise RuntimeError(f"UI target refused: expected exactly 1 windowed process at {exe_path!r}, found {row.get('count')}")
    return {"pid": row["rows"][0]["pid"], "path": exe_path, "hwnd": row["rows"][0]["hwnd"]}


def focus_target_window(client: DnSpyClient, target: dict) -> None:
    """Bring the verified target window to the foreground (PID-scoped, no name switching)."""
    script = (
        "$ErrorActionPreference='Stop';"
        "$ws=New-Object -ComObject WScript.Shell;"
        "$ok=$ws.AppActivate(" + str(int(target["pid"])) + ");"
        "Start-Sleep -Milliseconds 400;"
        "[pscustomobject]@{focused=[bool]$ok}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("focused"):
        raise RuntimeError(f"UI target refused: could not focus pid={target['pid']} hwnd={target['hwnd']}")


def options_dialog_present(tree: str) -> bool:
    return re.search(r'(?:window|\u7a97\u53e3) "(?:\u9009\u9879|Options)"', tree, re.IGNORECASE) is not None


def snapshot(client: DnSpyClient, region: list[int]) -> str:
    return result_text(client.call_tool_json("Snapshot", {
        "use_vision": False,
        "use_ui_tree": True,
        "region": region,
    }))


def open_options(client: DnSpyClient, target: dict | None = None) -> None:
    if target is None:
        raise RuntimeError("open_options refused: no explicit UI target supplied")
    focus_target_window(client, target)
    for attempt in range(4):
        time.sleep(0.5)
        tree = snapshot(client, DESKTOP_REGION)
        if options_dialog_present(tree):
            return
        if attempt % 2 == 0:
            view = menu_location(tree, ("\u89c6\u56fe(V)", "View"))
            client.call_tool_json("Click", {"loc": view})
        else:
            client.call_tool_json("Shortcut", {"shortcut": "alt+v"})
        options = None
        menu_deadline = time.time() + 4
        while time.time() < menu_deadline:
            time.sleep(0.25)
            tree = snapshot(client, DESKTOP_REGION)
            try:
                options = menu_location(tree, ("\u9009\u9879(O)", "Options"))
                break
            except RuntimeError:
                pass
        if options is None:
            client.call_tool_json("Shortcut", {"shortcut": "esc"})
            continue
        client.call_tool_json("Click", {"loc": options})
        deadline = time.time() + 10
        while time.time() < deadline:
            state = snapshot(client, DESKTOP_REGION)
            if options_dialog_present(state):
                return
            time.sleep(0.5)
        client.call_tool_json("Shortcut", {"shortcut": "esc"})
    raise RuntimeError("dnSpy Options dialog did not appear after UI click")


UIA_LOCATE_HOST_PORT = r'''
param([int]$TargetPid)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc=Get-Process -Id $TargetPid -ErrorAction Stop
if($proc.Path -eq $null){ throw 'target path unreadable' }
$main=[System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$opts=$null
foreach($name in @('选项','Options')){
  $opts=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)))
  if($null -ne $opts){ break }
}
if($null -eq $opts){ throw 'options dialog not open' }
$edits=@($opts.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))))
$hostBox=$null; $portBox=$null
foreach($e in $edits){
  try { $v=[string]$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { continue }
  if([string]::IsNullOrWhiteSpace($v)){ continue }
  if($null -eq $portBox -and $v -match '^\d{2,6}$'){ $portBox=$e; continue }
  if($null -eq $hostBox -and $v -match '^[A-Za-z0-9.\-]+$' -and $v -notmatch '^\d+$'){ $hostBox=$e }
}
if($null -eq $hostBox -or $null -eq $portBox){ throw ('host/port boxes not identified: edits=' + $edits.Count) }
$hr=$hostBox.Current.BoundingRectangle
$pr=$portBox.Current.BoundingRectangle
'hostrect:' + [int]($hr.X + $hr.Width/2) + ',' + [int]($hr.Y + $hr.Height/2)
'portrect:' + [int]($pr.X + $pr.Width/2) + ',' + [int]($pr.Y + $pr.Height/2)
# the CIDR new-row editor: the first EMPTY edit below the port box; the
# Host-Only acknowledgment checkbox by its 远程主机/Host-Only name
$cidrBox=$null
foreach($e in $edits){
  try { $v=[string]$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $v=' ' }
  $r=$e.Current.BoundingRectangle
  if([string]::IsNullOrWhiteSpace($v) -and $null -ne $pr -and $r.Y -gt ($pr.Y + 20) -and $r.Y -lt ($pr.Y + 120)){ $cidrBox=$e; break }
}
if($null -ne $cidrBox){
  $cr=$cidrBox.Current.BoundingRectangle
  'cidrrect:' + [int]($cr.X + $cr.Width/2) + ',' + [int]($cr.Y + $cr.Height/2)
}
$cidrPane=$null
foreach($p in $opts.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Pane)))){
  $r=$p.Current.BoundingRectangle
  if($null -ne $pr -and $r.Y -gt ($pr.Y + 20) -and $r.Y -lt ($pr.Y + 130) -and $r.Width -gt 200){ $cidrPane=$p; break }
}
if($null -ne $cidrPane){
  $cr2=$cidrPane.Current.BoundingRectangle
  'cidrpane:' + [int]($cr2.X + $cr2.Width/2) + ',' + [int]($cr2.Y + $cr2.Height/2)
}
$ackBox=$opts.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::CheckBox)))
$ack=$null
foreach($c in $opts.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::CheckBox)))){
  $n=[string]$c.Current.Name
  if($n -match '远程主机|Host-Only'){ $ack=$c; break }
}
if($null -ne $ack){
  $ar=$ack.Current.BoundingRectangle
  $on=$false
  try { $on=($ack.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) } catch {}
  'ackrect:' + [int]($ar.X + 10) + ',' + [int]($ar.Y + $ar.Height/2) + ':on=' + $on
}
'''



def _uia_locate_script(target: dict) -> str:
    return UIA_LOCATE_HOST_PORT.replace("param([int]$TargetPid)", "param([int]$TargetPid)  # " + str(target["pid"])) + "\n"


def apply_settings(client: DnSpyClient, enable: bool, host: str = "", port: int | None = None,
                   target: dict | None = None) -> None:
    """Drive the MCP settings page. `target` (from resolve_target/find_target) is REQUIRED:
    every window/control this touches must belong to that exact pid+exe pair."""
    if target is None:
        raise RuntimeError("apply_settings refused: no explicit UI target supplied (pid + exe path)")
    open_options(client, target)
    tree = snapshot(client, DESKTOP_REGION)
    item_lines = [line for line in tree.splitlines()
                  if "\u6811\u89c6\u56fe\u9879" in line or "tree item" in line.lower()]
    item_locations: list[list[int]] = []
    for line in item_lines:
        location = line_location(line, "settings tree item")
        if location not in item_locations:
            item_locations.append(location)
    if item_locations:
        settings_tree_x = max(location[0] for location in item_locations)
        item_locations = [location for location in item_locations if location[0] == settings_tree_x]

    observed_pages: list[str] = []
    page_found = is_mcp_settings_page(tree)
    for location in item_locations:
        if page_found:
            break
        client.call_tool_json("Click", {"loc": location})
        time.sleep(0.15)
        tree = snapshot(client, DESKTOP_REGION)
        observed_pages.extend(re.findall(r'(?:\u7a97\u683c|pane) "([^"]+)"', tree, re.IGNORECASE))
        page_found = is_mcp_settings_page(tree)
    if not page_found:
        raise RuntimeError(f"MCP settings page was not found; observed={sorted(set(observed_pages))}")

    # CHK-015 listener fix: UIA locates the host/port TextBox rectangles
    # deterministically; the actual entry uses keyboard Type at those exact
    # coordinates (UIA ValuePattern.SetValue does not commit the WPF binding,
    # and the historical checkbox-relative offsets drifted with the layout).
    import re as _re
    if host or port is not None:
        try:
            located = client.call_tool_json("PowerShell", {"command": _uia_locate_script(target), "timeout": 60})
            located_text = located if isinstance(located, str) else str(located)
            host_m = _re.search(r"hostrect:(\d+),(\d+)", located_text)
            port_m = _re.search(r"portrect:(\d+),(\d+)", located_text)
        except Exception:
            host_m = port_m = None
        cidr_m = _re.search(r"cidrrect:(\d+),(\d+)", located_text)
        ack_m = _re.search(r"ackrect:(\d+),(\d+):on=(True|False)", located_text)
        if host_m and host:
            client.call_tool_json("Type", {"loc": [int(host_m.group(1)), int(host_m.group(2))],
                                           "text": host, "clear": True})
            time.sleep(0.3)
            if port_m and port is not None:
                client.call_tool_json("Type", {"loc": [int(port_m.group(1)), int(port_m.group(2))],
                                               "text": str(port), "clear": True})
                time.sleep(0.3)
            # CHK-015: the settings validator enforces the loopback/remote
            # combination matrix — a loopback Host requires EMPTY Cidrs and no
            # ack; a non-loopback Host requires the ack (the trusted peer CIDR
            # stays whatever the deployment seeded/persisted).
            pane_m = _re.search(r"cidrpane:(\d+),(\d+)", located_text)
            if host in ("localhost", "127.0.0.1", "::1"):
                if pane_m:
                    client.call_tool_json("Click", {"loc": [int(pane_m.group(1)), int(pane_m.group(2))]})
                    time.sleep(0.2)
                    client.call_tool_json("Shortcut", {"shortcut": "ctrl+a"})
                    time.sleep(0.15)
                    client.call_tool_json("Shortcut", {"shortcut": "delete"})
                    time.sleep(0.2)
                if ack_m and ack_m.group(3) == "True":
                    client.call_tool_json("Click", {"loc": [int(ack_m.group(1)), int(ack_m.group(2))]})
                    time.sleep(0.3)
            else:
                if ack_m and ack_m.group(3) == "False":
                    client.call_tool_json("Click", {"loc": [int(ack_m.group(1)), int(ack_m.group(2))]})
                    time.sleep(0.3)
            host = ""          # already entered at the located rectangles
            port = None

    host_location: list[int] | None = None
    if host or port is not None:
        host_line = None
        for line in tree.splitlines():
            # The Options search field is also an Edit control. Only use an
            # explicitly named host editor; dnSpy 6.6 otherwise needs the
            # stable checkbox-relative fallback below.
            if re.search(r'(?:\u7f16\u8f91|\bedit)\s+"(?:\u4e3b\u673a|Host)"', line, re.IGNORECASE):
                host_line = line
                break
        if host_line:
            host_location = line_location(host_line, "MCP host edit")
        else:
            # dnSpy 6.6 exposes TextBox text as individual UIA words instead of an
            # edit element.  Anchor the click to the stable server-enable control.
            enable_line = matching_line(tree, ("\u542f\u7528 MCP \u670d\u52a1\u5668", "Enable MCP server"))
            if not enable_line:
                raise RuntimeError(f"MCP host edit anchor was not found; page_tree={tree[:8000]!r}")
            enable_location = line_location(enable_line, "MCP server enable checkbox")
            host_location = [enable_location[0] + 145, enable_location[1] + 72]
        if host:
            client.call_tool_json("Type", {
                "loc": host_location,
                "text": host,
                "clear": True,
            })
            time.sleep(0.2)

    if port is not None:
        port_line = None
        for line in tree.splitlines():
            if re.search(r'(?:\u7f16\u8f91|\bedit)\s+"(?:\u7aef\u53e3|Port)"', line, re.IGNORECASE):
                port_line = line
                break
        if port_line:
            port_location = line_location(port_line, "MCP port edit")
        elif host_location is not None:
            # The Auto-sized rows place the TextBox centres 30 device pixels
            # apart at the VM's 100% DPI.  dnSpy 6.6 may expose neither edit
            # by name, so use the observed row-centre delta from the host box.
            port_location = [host_location[0], host_location[1] + 30]
        else:
            raise RuntimeError("MCP port edit anchor was not found")
        client.call_tool_json("Type", {
            "loc": port_location, "text": str(port), "clear": True,
        })
        time.sleep(0.2)

    # UIA reports controls that are underneath the fixed OK/Cancel footer as
    # present and clickable. Scroll first so the process-local override is
    # actually visible instead of clicking through the footer overlay.
    client.call_tool_json("Scroll", {
        "loc": [900, 480], "type": "vertical", "direction": "down", "wheel_times": 8,
    })
    time.sleep(0.4)
    tree = snapshot(client, DESKTOP_REGION)
    checkbox_line = matching_line(tree, ("\u7269\u7406\u673a/\u672a\u77e5\u73af\u5883", "physical/unknown"))
    if not checkbox_line:
        raise RuntimeError("MCP local process override checkbox was not found")

    toggle_on = "[toggle:on]" in checkbox_line.lower()
    if toggle_on != enable:
        client.call_tool_json("Click", {"loc": line_location(checkbox_line, "local override checkbox")})
        time.sleep(0.2)

    tree = snapshot(client, DESKTOP_REGION)
    ok_line = matching_line(tree, ("\u6309\u94ae \"\u786e\u5b9a", 'button "OK'))
    if not ok_line:
        raise RuntimeError("dnSpy settings OK button was not found")
    client.call_tool_json("Click", {"loc": line_location(ok_line, "settings OK button")})
    deadline = time.time() + 15
    while time.time() < deadline:
        state = snapshot(client, DESKTOP_REGION)
        if not options_dialog_present(state):
            return
        time.sleep(0.5)
    raise RuntimeError("dnSpy Options dialog did not close after OK")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--enable", required=True, choices=("true", "false"))
    parser.add_argument("--host", default="")
    parser.add_argument("--port", type=int)
    parser.add_argument("--ui-url", default="http://192.168.204.240:28787/mcp")
    parser.add_argument("--target-pid", type=int, default=None,
                        help="required: PID of the exact dnSpy instance to drive")
    parser.add_argument("--target-exe", default=None,
                        help="required: canonical exe path that must match the target PID")
    args = parser.parse_args()

    if args.target_pid is None or not args.target_exe:
        raise SystemExit(
            "refused: an explicit UI target is required (--target-pid PID --target-exe PATH); "
            "this helper no longer falls back to the first dnSpy process"
        )
    client = UiMcpClient.connect(args.ui_url, client_name="dnspy-p01-ui-driver", timeout=30)
    try:
        target = resolve_target(client, args.target_pid, args.target_exe)
        apply_settings(client, args.enable == "true", args.host, args.port, target=target)
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
