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


def snapshot(client: DnSpyClient, region: list[int]) -> str:
    return result_text(client.call_tool_json("Snapshot", {
        "use_vision": False,
        "use_ui_tree": True,
        "region": region,
    }))


def _guard(target: dict) -> str:
    return (
        "$p=Get-Process -Id " + str(int(target["pid"])) + " -ErrorAction SilentlyContinue;"
        "if(-not $p){ throw 'target pid gone' }"
        "if(-not $p.Path -or -not ($p.Path -ieq '" + str(target["path"]).replace("'", "''") + "')){ throw ('target path mismatch: ' + $p.Path) }"
        "if(-not $p.MainWindowHandle){ throw 'target has no main window' }"
    )


def _uia_prelude(target: dict) -> str:
    return (
        _guard(target)
        + "Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes;"
        + "$main=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle);"
    )


def _owned_options_window(client: DnSpyClient, target: dict, timeout_s: float = 12.0) -> dict:
    """Find the Options dialog INSIDE the verified target's own window tree (dnSpy hosts it
    inside the main window, so ownership = descendant of the target's main window)."""
    import time as _time

    script = (
        _uia_prelude(target)
        + "$hits=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -match '^(选项|Options)' });"
        "if($hits.Count -ne 1){ [pscustomobject]@{found=$false;n=$hits.Count}|ConvertTo-Json -Compress }"
        "else{ $r=$hits[0].Current.BoundingRectangle;"
        "[pscustomobject]@{found=$true;n=1;name=$hits[0].Current.Name;cx=[int]($r.X+$r.Width/2);cy=[int]($r.Y+$r.Height/2)}|ConvertTo-Json -Compress }"
    )
    deadline = _time.time() + timeout_s
    last = None
    while _time.time() < deadline:
        last = _ps_json(client, script)
        if last.get("found"):
            return last
        _time.sleep(0.5)
    raise RuntimeError(f"owned Options dialog not found for pid={target['pid']} (n={last.get('n') if last else '?'})")


def open_options(client: DnSpyClient, target: dict | None = None) -> dict:
    """Open View->Options ON THE TARGET via its own menu bar; return the owned dialog info."""
    if target is None:
        raise RuntimeError("open_options refused: no explicit UI target supplied")
    menu = (
        _uia_prelude(target)
        + "$bar=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,"
        "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Menu)));"
        "if(-not $bar){ throw 'target menu not found' }"
        "$view=@($bar.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{ $_.Current.Name -match '^(视图|View)' }|Select-Object -First 1);"
        "if($view.Count -lt 1){ throw 'view menu not found on target' }"
        "$view=$view[0];"
        "try{ $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }catch{ }"
        "Start-Sleep -Milliseconds 300;"
        "$opt=@($view.FindAll([System.Windows.Automation.TreeScope]::Subtree,"
        "[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{ $_.Current.Name -match '^(选项|Options)' }|Select-Object -First 1);"
        "if($opt.Count -lt 1){ throw 'options menu item not found on target' }"
        "$opt=$opt[0];"
        "try{ $opt.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{ throw 'options menu invoke failed' }"
        "[pscustomobject]@{invoked=$true}|ConvertTo-Json -Compress"
    )
    _ps_json(client, menu)
    return _owned_options_window(client, target)


def _verify_foreground_then_click(client: DnSpyClient, target: dict, x: int, y: int, what: str) -> None:
    """Pre-click gate: foreground window must be the target's; the click lands on (x, y)
    that came from an OWNED control's rectangle. No blind retries - a failed gate raises."""
    script = (
        _guard(target)
        + "Add-Type -Namespace W -Name U -MemberDefinition '[DllImport(\"user32.dll\")] public static extern IntPtr GetForegroundWindow();"
        "[DllImport(\"user32.dll\")] public static extern bool SetCursorPos(int x,int y);"
        "[DllImport(\"user32.dll\")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);';"
        "Add-Type -Namespace W -Name U2 -MemberDefinition '[DllImport(\"user32.dll\")] public static extern uint GetWindowThreadProcessId(IntPtr h, ref uint pid);';"
        "$fg=[W.U]::GetForegroundWindow();"
        "$fgpid=0;"
        "[void][W.U2]::GetWindowThreadProcessId($fg,[ref]$fgpid);"
        "if($fgpid -ne $p.Id){ throw ('foreground window pid ' + $fgpid + ' is not the target ' + $p.Id) }"
        "[pscustomobject]@{gated=$true;what='" + what + "'}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("gated"):
        raise RuntimeError(f"pre-click gate failed for {what!r}: {row}")
    # gated: foreground is the target; click through the endpoint's injector at the same
    # owned-control coordinates
    client.call_tool_json("Click", {"loc": [int(x), int(y)]})


def _owned_edits(client: DnSpyClient, target: dict) -> list[dict]:
    """All Edit controls of the target-owned Options dialog with values and rectangles."""
    script = (
        _uia_prelude(target)
        + "$opts=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -match '^(选项|Options)' });"
        "if($opts.Count -ne 1){ throw ('owned options window count=' + $opts.Count) }"
        "$edits=@($opts[0].FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))));"
        "$rows=@();foreach($e in $edits){$v='';try{$v=[string]$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value}catch{};"
        "$r=$e.Current.BoundingRectangle;"
        "$rows+=[pscustomobject]@{value=$v;cx=[int]($r.X+$r.Width/2);cy=[int]($r.Y+$r.Height/2)}};"
        "$rows|ConvertTo-Json -Depth 4 -Compress"
    )
    row = _ps_json(client, script)
    rows = row if isinstance(row, list) else [row]
    return [r for r in rows if isinstance(r, dict) and "cx" in r]


def _owned_options_rect(client: DnSpyClient, target: dict) -> dict:
    """The owned Options dialog's BoundingRectangle (left/top/right/bottom)."""
    script = (
        _uia_prelude(target)
        + "$hits=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -match '^(选项|Options)' });"
        "if($hits.Count -ne 1){ throw ('owned options window count=' + $hits.Count) }"
        "$r=$hits[0].Current.BoundingRectangle;"
        "[pscustomobject]@{found=$true;l=[int]$r.X;t=[int]$r.Y;rt=[int]$r.Right;b=[int]$r.Bottom}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("found"):
        raise RuntimeError("owned Options rect unavailable")
    return row


def _label_inside_owned_rect(client: DnSpyClient, target: dict, label_regex: str,
                             toggle_hint: bool = False) -> dict:
    """Locate a labeled control for clicking, constrained to the owned Options dialog.

    dnSpy's settings tree exposes unnamed FastTextBlock items, so the label comes from the
    desktop snapshot text; the click is only allowed when the label's coordinates fall
    INSIDE the owned dialog's UIA rectangle. Ownership therefore stays proven even though
    the item itself has no accessible name."""
    rect = _owned_options_rect(client, target)
    tree = snapshot(client, DESKTOP_REGION)
    for line in tree.splitlines():
        if not re.search(label_regex, line, re.IGNORECASE):
            continue
        m = re.search(r"\((\d+),(\d+)\)", line)
        if not m:
            continue
        x, y = int(m.group(1)), int(m.group(2))
        if rect["l"] <= x <= rect["rt"] and rect["t"] <= y <= rect["b"]:
            out = {"cx": x, "cy": y, "line": line.strip()[:120], "rect": rect}
            if toggle_hint:
                out["toggle_on"] = "[toggle:on]" in line.lower()
            return out
    raise RuntimeError(f"owned control not found inside the Options rect: {label_regex}")


def _click_owned_control(client: DnSpyClient, target: dict, label_regex: str, what: str) -> dict:
    ctl = _label_inside_owned_rect(client, target, label_regex, toggle_hint=True)
    focus_target_window(client, target)
    _verify_foreground_then_click(client, target, ctl["cx"], ctl["cy"], what)
    return ctl


def _click_owned_control(client: DnSpyClient, target: dict, name_regex: str, what: str) -> dict:
    ctl = _label_inside_owned_rect(client, target, name_regex, toggle_hint=True)
    focus_target_window(client, target)
    _verify_foreground_then_click(client, target, ctl["cx"], ctl["cy"], what)
    return ctl


def _type_owned(client: DnSpyClient, target: dict, text: str, cx: int, cy: int, what: str) -> None:
    """Verified keyboard input as ONE PowerShell operation: foreground gate, click on the
    owned box, select-all and SendKeys the text - atomic so no interleaved desktop churn
    (the management endpoint's own console writes) can steal focus between the click and
    the keystrokes."""
    safe = "".join(ch for ch in str(text) if ch.isalnum() or ch in ".-")
    script = (
        _guard(target)
        + "Add-Type -AssemblyName System.Windows.Forms;"
        + "Add-Type -Namespace W -Name U -MemberDefinition '[DllImport(\"user32.dll\")] public static extern IntPtr GetForegroundWindow();"
        "[DllImport(\"user32.dll\")] public static extern bool SetCursorPos(int x,int y);"
        "[DllImport(\"user32.dll\")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);';"
        "Add-Type -Namespace W -Name U2 -MemberDefinition '[DllImport(\"user32.dll\")] public static extern uint GetWindowThreadProcessId(IntPtr h, ref uint pid);';"
        "$ws=New-Object -ComObject WScript.Shell;[void]$ws.AppActivate(" + str(int(target["pid"])) + ");Start-Sleep -Milliseconds 250;"
        "$fg=[W.U]::GetForegroundWindow();$fgpid=0;[void][W.U2]::GetWindowThreadProcessId($fg,[ref]$fgpid);"
        "if($fgpid -ne $p.Id){ throw ('foreground pid ' + $fgpid + ' not target') }"
        "[void][W.U]::SetCursorPos(" + str(int(cx)) + "," + str(int(cy)) + ");"
        "[W.U]::mouse_event(2,0,0,0,[IntPtr]::Zero);Start-Sleep -Milliseconds 60;[W.U]::mouse_event(4,0,0,0,[IntPtr]::Zero);"
        "Start-Sleep -Milliseconds 150;"
        "[System.Windows.Forms.SendKeys]::SendWait('^a');Start-Sleep -Milliseconds 40;"
        "[System.Windows.Forms.SendKeys]::SendWait('" + safe + "');Start-Sleep -Milliseconds 200;"
        "[pscustomobject]@{typed=$true;what='" + what + "'}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("typed"):
        raise RuntimeError(f"atomic type failed for {what!r}: {row}")
    time.sleep(0.3)


def _select_mcp_page(client: DnSpyClient, target: dict) -> None:
    """Click the settings-tree rows of the OWNED Options dialog until the MCP page is
    active. Rows are anonymous FastTextBlocks, so selection is verified the only reliable
    way: the owned dialog's UIA edit list starts containing a numeric (port) edit."""
    rect = _owned_options_rect(client, target)
    tree = snapshot(client, DESKTOP_REGION)
    rows: list[tuple[int, int]] = []
    for line in tree.splitlines():
        if "树视图项" not in line and "tree item" not in line.lower():
            continue
        m = re.search(r"\((\d+),(\d+)\)", line)
        if not m:
            continue
        x, y = int(m.group(1)), int(m.group(2))
        if rect["l"] <= x < rect["l"] + 240 and rect["t"] <= y <= rect["b"]:
            if (x, y) not in rows:
                rows.append((x, y))
    if not rows:
        raise RuntimeError("no settings-tree rows found inside the owned Options rect")
    def mcp_page_visible() -> bool:
        tr = snapshot(client, DESKTOP_REGION)
        for ln in tr.splitlines():
            if "启用 MCP" not in ln and "Enable MCP" not in ln:
                continue
            m = re.search(r"\((\d+),(\d+)\)", ln)
            if not m:
                continue
            x, y = int(m.group(1)), int(m.group(2))
            if rect["l"] <= x <= rect["rt"] and rect["t"] <= y <= rect["b"]:
                return True
        return False

    for (x, y) in rows:
        focus_target_window(client, target)
        _verify_foreground_then_click(client, target, x, y, "settings tree row")
        time.sleep(0.25)
        if mcp_page_visible():
            return
    raise RuntimeError("MCP settings page not reached: no tree row showed the MCP page")


def apply_settings(client: DnSpyClient, enable: bool, host: str = "", port: int | None = None,
                   target: dict | None = None) -> None:
    """Drive the MCP settings page of the verified target: open Options via its own menu,
    select the MCP page in the owned dialog's tree, type host/port into owned edits with
    pre-input foreground verification, honor the loopback combination rules, apply OK."""
    if target is None:
        raise RuntimeError("apply_settings refused: no explicit UI target supplied (pid + exe path)")
    # re-verify the target at entry (stale/fake targets refused here, before any UI write)
    resolve_target(client, target["pid"], target["path"])
    open_options(client, target)
    # MCP page: walk the owned dialog's tree rows until host/port edits appear
    _select_mcp_page(client, target)
    time.sleep(0.4)
    # One full locate->type->verify->OK pass, retried once with fresh locators if any
    # verification fails (each attempt re-locates; a failed verification aborts BEFORE the
    # OK click, so no unverified state is ever committed).
    last_err: Exception | None = None
    for attempt in range(2):
        try:
            # host/port boxes expose their CURRENT values as word runs inside the owned rect:
            # locate them by value (host text / numeric port), then type at those coordinates
            rect = _owned_options_rect(client, target)
            tr = snapshot(client, DESKTOP_REGION)
            host_box = None
            digit_words = []
            for ln in tr.splitlines():
                m = re.search(r"\((\d+),(\d+)\)", ln)
                if not m:
                    continue
                x, y = int(m.group(1)), int(m.group(2))
                if not (rect["l"] <= x <= rect["rt"] and rect["t"] <= y <= rect["b"]):
                    continue
                if '"localhost"' in ln or '"127.0.0.1"' in ln:
                    host_box = (x, y)
                m5 = re.search(r'word "(\d{2,5})"', ln)
                if m5:
                    digit_words.append((x, y, m5.group(1)))
            if not host_box:
                raise RuntimeError("owned host box not located by its current value")
            # the port box is the digit word on the next row under the host box, near its
            # column; hint-text digits (e.g. a CIDR /32 example) live elsewhere
            port_candidates = [(x, y) for (x, y, v) in digit_words
                               if host_box[1] + 8 <= y <= host_box[1] + 48 and abs(x - host_box[0]) <= 60]
            if not port_candidates:
                raise RuntimeError(f"owned port box not located under the host row (digits={digit_words!r})")
            port_box = port_candidates[0]
            # read the current values from the located words; only touch what actually changes
            def word_at(x, y):
                tr2 = snapshot(client, DESKTOP_REGION)
                for ln in tr2.splitlines():
                    m2 = re.search(r"\((\d+),(\d+)\)", ln)
                    if m2 and int(m2.group(1)) == x and int(m2.group(2)) == y:
                        m3 = re.search(r'"([^"]*)"', ln)
                        return m3.group(1) if m3 else ""
                return None

            current_host = word_at(*host_box)
            if host and (current_host or "").strip().casefold() != host.casefold():
                _type_owned(client, target, host, host_box[0], host_box[1], "host box")
            if port is not None and str(port) != (word_at(*port_box) or ""):
                _type_owned(client, target, str(port), port_box[0], port_box[1], "port box")
                # verify the typed value took: the port row (same y band, right of the tree
                # column) must show the requested number before anything is committed
                time.sleep(0.4)
                tr3 = snapshot(client, DESKTOP_REGION)
                typed_visible = False
                for ln in tr3.splitlines():
                    m4 = re.search(r"\((\d+),(\d+)\)", ln)
                    if not m4:
                        continue
                    wx, wy = int(m4.group(1)), int(m4.group(2))
                    if rect["l"] <= wx <= rect["rt"] and rect["t"] <= wy <= rect["b"] and f'word "{port}"' in ln:
                        typed_visible = True
                        break
                if not typed_visible:
                    raise RuntimeError(f"the typed port {port} is not visible inside the owned dialog - refusing to apply")
            # loopback combination: empty CIDRs + no ack (only when a remote ack exists to undo)
            try:
                ack = _label_inside_owned_rect(client, target, "远程主机|Host-Only", toggle_hint=True)
            except RuntimeError:
                ack = None
            if host in ("localhost", "127.0.0.1", "::1"):
                # CIDR rows must be empty for loopback; clear only if the owned rows hold text
                for e in _owned_edits(client, target):
                    if e is host_box or e is port_box:
                        continue
                    if e["value"] and ("/" in e["value"] or e["value"].strip().isdigit() is False and "*" in e["value"]):
                        _type_owned(client, target, "", e["cx"], e["cy"], "cidr edit clear")
                if ack and ack.get("toggle_on"):
                    _click_owned_control(client, target, "远程主机|Host-Only", "remote ack checkbox off")
            else:
                if ack and not ack.get("toggle_on"):
                    _click_owned_control(client, target, "远程主机|Host-Only", "remote ack checkbox on")
            # local override checkbox must match `enable`
            ovr = _label_inside_owned_rect(client, target, "物理机|未知环境|physical/unknown", toggle_hint=True)
            toggle_on = bool(ovr.get("toggle_on"))
            if toggle_on != enable:
                _click_owned_control(client, target, "物理机|未知环境|physical/unknown", "local override checkbox")
            # OK button of the OWNED dialog, then it must close
            # commit via the dialog's default accept button with the KEYBOARD: the
            # endpoint's own console window can overlap the OK button and swallow clicks,
            # while ENTER goes to the focused owned dialog directly. Gate: foreground must
            # be the target and the owned dialog must still be present in its tree.
            script = (
                _guard(target)
                + "Add-Type -AssemblyName System.Windows.Forms;"
                + "Add-Type -Namespace W -Name U -MemberDefinition '[DllImport(\"user32.dll\")] public static extern IntPtr GetForegroundWindow();';"
                + "Add-Type -Namespace W -Name U2 -MemberDefinition '[DllImport(\"user32.dll\")] public static extern uint GetWindowThreadProcessId(IntPtr h, ref uint pid);';"
                + "$fg=[W.U]::GetForegroundWindow();$fgpid=0;[void][W.U2]::GetWindowThreadProcessId($fg,[ref]$fgpid);"
                + "if($fgpid -ne $p.Id){ throw ('foreground pid ' + $fgpid + ' not target') }"
                + "[System.Windows.Forms.SendKeys]::SendWait('{ENTER}');"
                + "[pscustomobject]@{entered=$true}|ConvertTo-Json -Compress"
            )
            _ps_json(client, script)
            deadline = time.time() + 12
            while time.time() < deadline:
                try:
                    _owned_options_window(client, target, timeout_s=0.2)
                    time.sleep(0.4)
                except RuntimeError:
                    return  # owned dialog closed: committed
            raise RuntimeError("owned Options dialog did not close after OK")
            break
        except RuntimeError as err:
            last_err = err
            try:
                _owned_options_window(client, target, timeout_s=1.0)
            except RuntimeError:
                raise
    else:
        raise last_err  # type: ignore[misc]



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
