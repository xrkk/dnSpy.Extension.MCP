#!/usr/bin/env python3
"""P09 AUD-006: export the MCP tool registry snapshot from TWO sources and
verify they agree:

  1. live wire   — tools/list over the dnSpy loopback (Mcp-Session-Id handshake)
  2. source scan — the plugin's tool provider registration lists

Any disagreement fails (exit 1).  The JSON snapshot is the single source of
truth for documentation tool counts."""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def wire_tools(url: str) -> dict:
    init = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                       "clientInfo": {"name": "registry-export", "version": "1"}}}
    request = urllib.request.Request(url, data=json.dumps(init).encode(), headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    with urllib.request.urlopen(request, timeout=30) as response:
        session = response.headers.get("Mcp-Session-Id")
    listing = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
    request = urllib.request.Request(url, data=json.dumps(listing).encode(), headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": session})
    with urllib.request.urlopen(request, timeout=60) as response:
        wire = response.read().decode("utf-8", "replace")
    for chunk in wire.split("data: "):
        chunk = chunk.strip()
        if not chunk:
            continue
        try:
            message = json.loads(chunk.splitlines()[0])
        except (json.JSONDecodeError, IndexError):
            continue
        if message.get("id") == 2:
            return {tool["name"]: tool for tool in message["result"]["tools"]}
    raise RuntimeError("tools/list returned no result")


def source_tools() -> dict | None:
    if not (ROOT / "Editing/EditToolProvider.cs").exists():
        return None  # deployed standalone (VM): wire-only mode
    tools: dict[str, dict] = {}
    providers = []
    for pattern, provider in (
        (r"static readonly string\[\] ProductTools = \{(.*?)\};", "edit"),
        (r"static readonly string\[\] TestTools = \{(.*?)\};", "edit-test"),
    ):
        text = (ROOT / "Editing/EditToolProvider.cs").read_text(encoding="utf-8")
        match = re.search(pattern, text, re.S)
        if match:
            providers.append(("Editing/EditToolProvider.cs", pattern, re.findall(r'"([a-z0-9_]+)"', match.group(1))))
    compile_text = (ROOT / "Editing/EditCompileFrontend.cs").read_text(encoding="utf-8")
    match = re.search(r"static readonly string\[\] ProductTools = \{(.*?)\};", compile_text, re.S)
    if match:
        providers.append(("Editing/EditCompileFrontend.cs", "compile", re.findall(r'"([a-z0-9_]+)"', match.group(1))))
    debug_text = (ROOT / "Debugger/DebugToolProvider.cs").read_text(encoding="utf-8")
    match = re.search(r"static readonly string\[\] AdvertisedSessionTools = \{(.*?)\};", debug_text, re.S)
    debug_names = re.findall(r'"([a-z0-9_]+)"', match.group(1)) if match else []
    debug_names += [name for name in ("debug_capabilities",) if f'Name = "{name}"' in debug_text]
    providers.append(("Debugger/DebugToolProvider.cs", "debug", debug_names))
    static_text = (ROOT / "Tools/StaticToolProvider.cs").read_text(encoding="utf-8")
    names = sorted(set(re.findall(r'Name = "([a-z0-9_]+)"', static_text)))
    providers.append(("Tools/StaticToolProvider.cs", "static", names))
    # only names are comparable cross-source; details come from the wire
    for _, _, names in providers:
        for name in names:
            tools.setdefault(name, {"name": name, "source_scan": True})
    return tools


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:15378/mcp")
    parser.add_argument("--output", default=str(ROOT / "dist/tool-registry-snapshot.json"))
    args = parser.parse_args()
    wire = wire_tools(args.url)
    wire_names = set(wire)
    source = source_tools()
    if source is None:
        snapshot = {
            "format": "dnspy.mcp.tool-registry-snapshot.v1",
            "url": args.url,
            "tool_count": len(wire_names),
            "tools": {name: {"description": tool.get("description", ""), "has_input_schema": bool(tool.get("inputSchema"))}
                      for name, tool in sorted(wire.items())},
            "consistency": {"mode": "wire-only", "consistent": True,
                            "note": "deployed standalone (VM): source scan unavailable; the wire registry is the fact source"},
        }
        Path(args.output).write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"tools={len(wire_names)} mode=wire-only")
        print(f"snapshot: {args.output}")
        return 0
    source_names = set(source)
    missing_on_wire = sorted(source_names - wire_names)
    unknown_on_wire = sorted(wire_names - source_names)
    snapshot = {
        "format": "dnspy.mcp.tool-registry-snapshot.v1",
        "url": args.url,
        "tool_count": len(wire_names),
        "tools": {name: {"description": tool.get("description", ""), "has_input_schema": bool(tool.get("inputSchema"))}
                  for name, tool in sorted(wire.items())},
        "consistency": {
            "source_scan_count": len(source_names),
            "missing_on_wire": missing_on_wire,
            "unknown_on_wire": unknown_on_wire,
            "consistent": not missing_on_wire and not unknown_on_wire,
        },
    }
    Path(args.output).write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"tools={len(wire_names)} source={len(source_names)} "
          f"missing_on_wire={missing_on_wire} unknown_on_wire={unknown_on_wire[:8]}{'...' if len(unknown_on_wire) > 8 else ''}")
    print(f"snapshot: {args.output}")
    return 0 if snapshot["consistency"]["consistent"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
