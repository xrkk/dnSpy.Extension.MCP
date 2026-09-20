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


def _ps_json(client: DnSpyClient, script: str, timeout: int = 60):
    """Run a PowerShell snippet and return its COMPLETE JSON result.

    The endpoint wraps stdout as {'result': 'Response: <payload>\n\nStatus Code: 0'};
    the payload may be a top-level object OR array. The whole payload is parsed - never a
    brace-matched first object that silently drops array items. Anything that is not valid
    JSON raises (comm/parse failures are failures, not empty results)."""
    value = client.call_tool_json("PowerShell", {"command": script, "timeout": timeout})
    if isinstance(value, dict) and set(value) == {"result"} and isinstance(value["result"], str):
        value = value["result"]
    text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
    marker = "Response: "
    if marker in text:
        head, _, tail = text.partition(marker)
        payload, sep, _rest = tail.rpartition("Status Code:")
        if not sep:
            payload = tail
        text = payload.strip()
    try:
        return json.loads(text)
    except ValueError as ex:
        raise RuntimeError(f"PowerShell result is not valid JSON: {text[:200]!r}") from ex


# --- T039: explicit UI target ownership (no first-instance fallback) ---------------------
#
# Every UI operation must belong to one explicit target: a PID plus the canonical exe
# path the caller expects. The pair is verified on the VM BEFORE any UI write.


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


# --- T039 R3: control-identity UI operations ---------------------------------------------
#
# Every control is located INSIDE the verified target's own UIA tree by structural
# identity - AutomationId / ControlType / label-name association / RuntimeId - never by
# desktop text coordinates falling inside a rectangle. The MCP settings page lives in a
# McpSettingsControl custom element whose edits are distinguished by their preceding
# TextBlock labels (主机：/端口：/允许的 CIDR：/Token 校验值：/产物目录：/允许的样本目录：),
# its checkboxes by their names, and the OK button is a direct child of the Options
# window named 确定(O)/OK. Ambiguous matches (multiple or zero candidates) are refused.

LABELS = {
    "host": ("主机：", "Host"),
    "port": ("端口：", "Port"),
    "cidr": ("允许的 CIDR：", "Allowed CIDR"),
    "token": ("Token 校验值：", "Token verifier"),
    "artifact": ("产物目录：", "Artifact"),
    "sample": ("允许的样本目录：", "Allowed sample"),
}
CHECKBOXES = {
    "enable_server": ("启用 MCP 服务器", "Enable MCP server"),
    "require_token": ("要求 Bearer Token", "Require Bearer Token"),
    "remote_host_only": ("远程主机仅限配置的 Host-Only 隔离网络", "Host-Only"),
    "debug_tools": ("启用调试工具", "Debug tools"),
    "dedicated_instance": ("此 dnSpy 实例专用于 MCP 调试", "dedicated"),
    "local_override": ("物理机/未知环境", "physical/unknown"),
}
_OK_NAMES = ("确定", "OK")
_OPTIONS_NAMES = ("选项", "Options")


def _find_settings_root_fragment() -> str:
    return (
        "$ctrl=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ClassName -eq 'McpSettingsControl' });"
        "if($ctrl.Count -ne 1){ throw ('McpSettingsControl count=' + $ctrl.Count) }"
        "$ctrl=$ctrl[0];"
    )


def _find_options_window_fragment() -> str:
    return (
        "$opts=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and "
        "($_.Current.Name -match '^(选项|Options)') });"
        "if($opts.Count -ne 1){ throw ('owned Options window count=' + $opts.Count) }"
        "$opts=$opts[0];"
    )


def _control_row(el_var: str) -> str:
    return ("$c=" + el_var + ".Current;"
            "$rt=($(" + el_var + ".GetRuntimeId()|ForEach-Object{$_}) -join '.');"
            "$r=$c.BoundingRectangle;"
            "[pscustomobject]@{id=$c.AutomationId;type=$c.ControlType.ProgrammaticName;name=$c.Name;"
            "cls=$c.ClassName;rt=$rt;cx=[int]($r.X+$r.Width/2);cy=[int]($r.Y+$r.Height/2)}")


def _edit_locator_fragment(field: str) -> str:
    """PS fragment: set $hits[0] to the Edit whose preceding Text sibling carries the
    field label (structural label association)."""
    return (
        "$kids=@($ctrl.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition));"
        "$hits=@();for($i=1;$i -lt $kids.Count;$i++){"
        "if($kids[$i].Current.ControlType.ProgrammaticName -eq 'ControlType.Edit'"
        " -and $kids[$i-1].Current.ControlType.ProgrammaticName -eq 'ControlType.Text'){"
        "$ln=$kids[$i-1].Current.Name;"
        "foreach($want in @('" + "','".join(LABELS[field]) + "')){"
        "if($ln -eq $want -or $ln -like ($want + '*')){ $hits += $kids[$i]; break } } } }"
        "if($hits.Count -ne 1){ throw ('edit[" + field + "] candidates=' + $hits.Count) }"
    )


def _checkbox_locator_fragment(key: str) -> str:
    """PS fragment: set $hits[0] to the checkbox matching the accessible name."""
    return (
        "$hits=@($ctrl.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox' -and "
        "($_.Current.Name -like '*" + CHECKBOXES[key][0] + "*' -or $_.Current.Name -like '*" + CHECKBOXES[key][1] + "*') });"
        "if($hits.Count -ne 1){ throw ('checkbox[" + key + "] candidates=' + $hits.Count) }"
    )


def _ps_lit(value: object) -> str:
    return str(value).replace("'", "''")


def locate_edit(client: DnSpyClient, target: dict, field: str) -> dict:
    """Locate a settings edit by its LABEL (preceding TextBlock sibling) and report its
    value + RuntimeId. Structural association, not coordinates."""
    if field not in LABELS:
        raise RuntimeError(f"unknown settings field {field!r}")
    script = (
        _uia_prelude(target)
        + _find_settings_root_fragment()
        + _edit_locator_fragment(field)
        + "$rt=(($hits[0].GetRuntimeId()|ForEach-Object{$_}) -join '.');"
        "$c=$hits[0].Current;"
        "$v='';try{ $v=[string]$hits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }catch{};"
        "[pscustomobject]@{field='" + field + "';value=$v;rt=$rt;id=$c.AutomationId;type=$c.ControlType.ProgrammaticName;name=$c.Name}|ConvertTo-Json -Compress"
    )
    return _ps_json(client, script)


def locate_checkbox(client: DnSpyClient, target: dict, key: str) -> dict:
    """Locate a settings checkbox by its accessible name; report its toggle state."""
    if key not in CHECKBOXES:
        raise RuntimeError(f"unknown checkbox {key!r}")
    script = (
        _uia_prelude(target)
        + _find_settings_root_fragment()
        + _checkbox_locator_fragment(key)
        + "$rt=(($hits[0].GetRuntimeId()|ForEach-Object{$_}) -join '.');"
        "$c=$hits[0].Current;"
        "$on='?';try{ $on=[string]($hits[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState) }catch{};"
        "[pscustomobject]@{key='" + key + "';toggle=$on;rt=$rt;id=$c.AutomationId;type=$c.ControlType.ProgrammaticName;name=$c.Name}|ConvertTo-Json -Compress"
    )
    return _ps_json(client, script)


def locate_ok_button(client: DnSpyClient, target: dict) -> dict:
    """The OK button is a DIRECT child of the owned Options window, named 确定/OK."""
    script = (
        _uia_prelude(target)
        + _find_options_window_fragment()
        + "$hits=@($opts.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and "
        "($_.Current.Name -like '" + _OK_NAMES[0] + "*' -or $_.Current.Name -like '" + _OK_NAMES[1] + "*') });"
        "if($hits.Count -ne 1){ throw ('OK button candidates=' + $hits.Count) }"
        + _control_row("$hits[0]").replace("[pscustomobject]@{", "$row=[pscustomobject]@{")
        + ";$row|ConvertTo-Json -Compress"
    )
    return _ps_json(client, script)


def read_field_values(client: DnSpyClient, target: dict) -> dict:
    """Snapshot every settings field (6 edits + 6 checkboxes) from its located control."""
    out: dict[str, object] = {}
    for field in LABELS:
        out[field] = locate_edit(client, target, field).get("value", "")
    for key in CHECKBOXES:
        out[key] = locate_checkbox(client, target, key).get("toggle", "?")
    return out


def _set_edit_value(client: DnSpyClient, target: dict, field: str, value: str) -> None:
    """Set an edit via its ValuePattern, commit focus, and verify FROM THE SAME CONTROL
    (re-found by RuntimeId) that it holds exactly `value` - byte-exact, no filtering."""
    script = (
        _uia_prelude(target)
        + _find_settings_root_fragment()
        + _edit_locator_fragment(field)
        + "$rt=(($hits[0].GetRuntimeId()|ForEach-Object{$_}) -join '.');"
        "$hits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('" + _ps_lit(value) + "');"
        "$main.SetFocus();Start-Sleep -Milliseconds 250;"
        "$again=@($ctrl.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ (($_.GetRuntimeId()|ForEach-Object{$_}) -join '.') -eq $rt });"
        "if($again.Count -ne 1){ throw ('re-find[" + field + "] by RuntimeId=' + $again.Count) }"
        "$v='';try{ $v=[string]$again[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }catch{};"
        "if($v -cne '" + _ps_lit(value) + "'){ throw ('set[' + '" + field + "' + '] mismatch: got [' + $v + '] want [' + '" + _ps_lit(value) + "' + ']') }"
        "[pscustomobject]@{set=$true;field='" + field + "';value=$v;rt=$rt}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("set"):
        raise RuntimeError(f"setting {field} failed: {row}")


def _toggle_checkbox(client: DnSpyClient, target: dict, key: str, want_on: bool) -> None:
    """Toggle via TogglePattern and verify the state FROM THE SAME control by RuntimeId."""
    script = (
        _uia_prelude(target)
        + _find_settings_root_fragment()
        + _checkbox_locator_fragment(key)
        + "$rt=(($hits[0].GetRuntimeId()|ForEach-Object{$_}) -join '.');"
        "$pat=$hits[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern);"
        "$cur=[string]$pat.Current.ToggleState;"
        "$want='" + ("On" if want_on else "Off") + "';"
        "if($cur -ne $want){ $pat.Toggle(); Start-Sleep -Milliseconds 250 };"
        "$again=@($ctrl.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ (($_.GetRuntimeId()|ForEach-Object{$_}) -join '.') -eq $rt });"
        "if($again.Count -ne 1){ throw ('re-find checkbox[" + key + "]=' + $again.Count) }"
        "$now=[string]$again[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState;"
        "if($now -ne $want){ throw ('toggle[" + key + '] now=' + "' + $now) }"
        "[pscustomobject]@{toggled=$true;key='" + key + "';state=$now;rt=$rt}|ConvertTo-Json -Compress"
    )
    row = _ps_json(client, script)
    if not row.get("toggled"):
        raise RuntimeError(f"toggling {key} failed: {row}")


def open_options(client: DnSpyClient, target: dict | None = None) -> dict:
    """Open View->Options ON THE TARGET via its own menu; return the owned dialog info."""
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
        "try{ $opt[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{ throw 'options menu invoke failed' }"
        "[pscustomobject]@{invoked=$true}|ConvertTo-Json -Compress"
    )
    _ps_json(client, menu)
    import time as _time
    deadline = _time.time() + 12
    while _time.time() < deadline:
        if _options_window_present(client, target):
            return {"pid": target["pid"], "options_window": "present"}
        _time.sleep(0.5)
    raise RuntimeError(f"owned Options dialog not found for pid={target['pid']}")


def _options_window_present(client: DnSpyClient, target: dict) -> bool:
    script = (
        _uia_prelude(target)
        + "$opts=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and "
        "($_.Current.Name -match '^(选项|Options)') });"
        "[pscustomobject]@{n=$opts.Count}|ConvertTo-Json -Compress"
    )
    count = _ps_json(client, script).get("n")
    if type(count) is not int or count not in (0, 1):
        raise RuntimeError(f"ambiguous or malformed Options count: {count!r}")
    return count == 1


def select_mcp_page(client: DnSpyClient, target: dict) -> None:
    """Select the MCP settings tree page. The tree items are unnamed FastTextBlocks, so
    the rows of the OWNED Options tree are invoked via UIA until the McpSettingsControl
    appears in the target's tree - control identity, no coordinates."""
    rows_script = (
        _uia_prelude(target)
        + _find_options_window_fragment()
        + "$tree=@($opts.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Tree))));"
        "if($tree.Count -ne 1){ throw ('options tree count=' + $tree.Count) }"
        "$items=@($tree[0].FindAll([System.Windows.Automation.TreeScope]::Descendants,"
        "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))));"
        "$ids=@($items|ForEach-Object{ (($_.GetRuntimeId()|ForEach-Object{$_}) -join '.') });"
        "[pscustomobject]@{count=$items.Count;ids=$ids}|ConvertTo-Json -Compress"
    )
    rows = _ps_json(client, rows_script)
    if not rows.get("count"):
        raise RuntimeError("no settings-tree rows in the owned Options dialog")
    for rid in rows["ids"]:
        invoke = (
            _uia_prelude(target)
            + _find_options_window_fragment()
            + "$tree=@($opts.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
            "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Tree))));"
            "$hit=@($tree[0].FindAll([System.Windows.Automation.TreeScope]::Descendants,"
            "(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem)))|"
            "Where-Object{ (($_.GetRuntimeId()|ForEach-Object{$_}) -join '.') -eq '" + str(rid) + "' });"
            "if($hit.Count -ne 1){ throw ('row re-find=' + $hit.Count) }"
            "$how='none';"
            "try{ $hit[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $how='selection' }catch{ "
            "$r=$hit[0].Current.BoundingRectangle;"
            "if($r.IsEmpty){ throw 'row rect empty' }"
            "Add-Type -Namespace M -Name C -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool SetCursorPos(int x,int y);"
            "[DllImport(\"user32.dll\")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);';"
            "$cx=[int]($r.X+$r.Width/2);$cy=[int]($r.Y+$r.Height/2);"
            "Add-Type -AssemblyName UIAutomationClient;"
            "$pt=New-Object System.Windows.Point($cx,$cy);"
            "$from=[System.Windows.Automation.AutomationElement]::FromPoint($pt);"
            "$frt=($from.GetRuntimeId()) -join '.';"
            "if($frt -ne ((($hit[0].GetRuntimeId())|ForEach-Object{$_}) -join '.')){ throw ('FromPoint hit ' + $frt + ' is not the row') }"
            "[void][M.C]::SetCursorPos($cx,$cy);"
            "[M.C]::mouse_event(2,0,0,0,[IntPtr]::Zero);Start-Sleep -Milliseconds 60;[M.C]::mouse_event(4,0,0,0,[IntPtr]::Zero);"
            "Start-Sleep -Milliseconds 250;"
            "$how='click-verified' };"
            "[pscustomobject]@{selected=$true;how=$how}|ConvertTo-Json -Compress"
        )
        _ps_json(client, invoke)
        time.sleep(0.3)
        probe = (
            _uia_prelude(target)
            + "$ctrl=@($main.FindAll([System.Windows.Automation.TreeScope]::Descendants,"
            "[System.Windows.Automation.Condition]::TrueCondition)|"
            "Where-Object{ $_.Current.ClassName -eq 'McpSettingsControl' });"
            "[pscustomobject]@{n=$ctrl.Count}|ConvertTo-Json -Compress"
        )
        if _ps_json(client, probe).get("n") == 1:
            return
    raise RuntimeError("MCP settings page not reached: no tree row produced McpSettingsControl")


def apply_settings(client: DnSpyClient, enable: bool | None = None, host: str = "",
                   port: int | None = None, target: dict | None = None) -> None:
    """Drive the MCP settings page of the verified target with control-identity writes.

    host='' leaves the host (and its combination-dependent fields) untouched; port-only
    applies do not modify host/CIDR/ack/local-override; enable=None (default) leaves the
    high-risk local override untouched - only an explicit True/False toggles it; every
    requested change is verified from the SAME
    located control (RuntimeId) before the single OK invoke, all untouched fields are
    compared before/after, and success requires the owned Options window to be GONE."""
    if target is None:
        raise RuntimeError("apply_settings refused: no explicit UI target supplied (pid + exe path)")
    resolve_target(client, target["pid"], target["path"])
    open_options(client, target)
    select_mcp_page(client, target)
    before = read_field_values(client, target)

    changes: list[tuple[str, str, object]] = []
    if host and before["host"] != host:
        changes.append(("edit", "host", host))
    if port is not None and str(before["port"]) != str(port):
        changes.append(("edit", "port", str(port)))

    # combination rules for the ack checkbox follow the FINAL host value
    final_host = host if host else str(before["host"])
    loopback = final_host.strip().casefold() in ("localhost", "127.0.0.1", "::1")
    if host and loopback and before["remote_host_only"] == "On":
        changes.append(("toggle", "remote_host_only", False))
    if host and not loopback and before["remote_host_only"] != "On":
        changes.append(("toggle", "remote_host_only", True))
    if enable is not None and before["local_override"] != ("On" if enable else "Off"):
        changes.append(("toggle", "local_override", bool(enable)))

    for kind, key, value in changes:
        if kind == "edit":
            _set_edit_value(client, target, key, value)
        else:
            _toggle_checkbox(client, target, key, value)

    # pre-commit readback: same controls; requested values present, everything else
    # byte-identical to `before`
    after = read_field_values(client, target)
    for kind, key, value in changes:
        got = after[key]
        want = value if kind == "edit" else ("On" if value else "Off")
        if str(got) != str(want):
            raise RuntimeError(f"pre-commit readback mismatch for {key}: {got!r} != {want!r}")
    touched = {key for _, key, _ in changes}
    for key, val in before.items():
        if key not in touched and str(after[key]) != str(val):
            raise RuntimeError(f"untouched field {key} changed: {val!r} -> {after[key]!r}")

    # commit: invoke the identified OK button of the owned Options window
    ok_row = locate_ok_button(client, target)
    ok_invoke = (
        _uia_prelude(target)
        + _find_options_window_fragment()
        + "$hits=@($opts.FindAll([System.Windows.Automation.TreeScope]::Children,"
        "[System.Windows.Automation.Condition]::TrueCondition)|"
        "Where-Object{ $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and "
        "($_.Current.Name -like '" + _OK_NAMES[0] + "*' -or $_.Current.Name -like '" + _OK_NAMES[1] + "*') });"
        "if($hits.Count -ne 1){ throw ('OK button candidates=' + $hits.Count) }"
        "$rt=(($hits[0].GetRuntimeId()|ForEach-Object{$_}) -join '.');"
        "if($rt -ne '" + str(ok_row.get("rt")) + "'){ throw 'OK button identity changed between locate and invoke' }"
        "try{ $hits[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }catch{ throw 'OK invoke failed' }"
        "[pscustomobject]@{invoked=$true;rt=$rt}|ConvertTo-Json -Compress"
    )
    _ps_json(client, ok_invoke)

    # success = the owned Options window is OBSERVED GONE (one identity check, no
    # except-return swallowing of comm/parse errors)
    deadline = time.time() + 12
    while time.time() < deadline:
        if not _options_window_present(client, target):
            return
        time.sleep(0.4)
    raise RuntimeError("owned Options dialog still present after OK invoke")


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
