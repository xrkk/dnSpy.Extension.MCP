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


def options_dialog_present(tree: str) -> bool:
    return re.search(r'(?:window|\u7a97\u53e3) "(?:\u9009\u9879|Options)"', tree, re.IGNORECASE) is not None


def snapshot(client: DnSpyClient, region: list[int]) -> str:
    return result_text(client.call_tool_json("Snapshot", {
        "use_vision": False,
        "use_ui_tree": True,
        "region": region,
    }))


def open_options(client: DnSpyClient) -> None:
    for attempt in range(4):
        client.call_tool_json("App", {"mode": "switch", "name": "dnSpy"})
        time.sleep(0.5)
        tree = snapshot(client, DESKTOP_REGION)
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


def apply_settings(client: DnSpyClient, enable: bool, host: str = "", port: int | None = None) -> None:
    open_options(client)
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
    parser.add_argument("--ui-url", default="http://192.168.204.149:28787/mcp")
    args = parser.parse_args()

    client = UiMcpClient.connect(args.ui_url, client_name="dnspy-p01-ui-driver", timeout=30)
    try:
        apply_settings(client, args.enable == "true", args.host, args.port)
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
