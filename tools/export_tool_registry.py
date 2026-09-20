#!/usr/bin/env python3
"""Export and cross-check the advertised MCP tool registry.

The wire is authoritative for descriptions and schemas. Source scanning checks
the exact public provider sets selected by an explicit runtime profile and
debug-gate state; the expected set never depends on names returned by the wire.
Callable but unadvertised edit/debug test seams are deliberately excluded.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE_FILES = (
    "McpTools.cs", "Editing/EditToolProvider.cs", "Editing/EditCompileFrontend.cs",
    "Editing/EditContracts.cs", "Debugger/DebugToolProvider.cs",
)


def response_json(payload: bytes, request_id: int) -> dict:
    text = payload.decode("utf-8", "replace")
    candidates = [text] + [line[6:] for line in text.splitlines() if line.startswith("data: ")]
    for candidate in candidates:
        try:
            value = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if value.get("id") == request_id:
            return value
    raise RuntimeError(f"response {request_id} not found")


def index_tools(tools: object) -> dict[str, dict]:
    if not isinstance(tools, list):
        raise ValueError("wire registry must contain a tools array")
    indexed: dict[str, dict] = {}
    duplicates: list[str] = []
    for tool in tools:
        if not isinstance(tool, dict) or not isinstance(tool.get("name"), str) or not tool["name"]:
            raise ValueError("every wire tool must be an object with a non-empty string name")
        name = tool["name"]
        if name in indexed:
            duplicates.append(name)
        else:
            indexed[name] = tool
    if duplicates:
        raise ValueError("duplicate tool names on wire: " + ", ".join(sorted(set(duplicates))))
    return indexed


def wire_tools(url: str) -> tuple[dict[str, dict], dict]:
    init = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-06-18", "capabilities": {},
        "clientInfo": {"name": "registry-export", "version": "1"}}}
    request = urllib.request.Request(url, data=json.dumps(init).encode(), headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    with urllib.request.urlopen(request, timeout=30) as response:
        session = response.headers.get("Mcp-Session-Id")
        response_json(response.read(), 1)
    listing = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}
    request = urllib.request.Request(url, data=json.dumps(listing).encode(), headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": session})
    with urllib.request.urlopen(request, timeout=60) as response:
        message = response_json(response.read(), 2)
    return index_tools(message["result"]["tools"]), {"url": url}


def file_tools(path: Path) -> tuple[dict[str, dict], dict]:
    capture = json.loads(path.read_text(encoding="utf-8"))
    tools = capture.get("tools")
    if tools is None and "result" in capture:
        tools = capture["result"]["tools"]
    metadata = {key: capture[key] for key in
                ("format", "captured_at_utc", "architecture", "dll_sha256", "url") if key in capture}
    return index_tools(tools), metadata


def quoted_array(path: Path, declaration: str) -> list[str]:
    text = path.read_text(encoding="utf-8")
    match = re.search(declaration + r"\s*=\s*\{(.*?)\};", text, re.S)
    if not match:
        raise RuntimeError(f"declaration not found in {path}: {declaration}")
    return re.findall(r'"([a-z0-9_]+)"', match.group(1))


def source_registry(profile: str, debug_gate: str) -> tuple[dict[str, str], list[str]] | None:
    if any(not (ROOT / relative).exists() for relative in SOURCE_FILES):
        return None
    static_text = (ROOT / "McpTools.cs").read_text(encoding="utf-8")
    start = static_text.index("GetAvailableTools()")
    static_section = static_text[start:static_text.index("ExecuteTool(", start)]
    static_names = sorted(set(re.findall(r'Name\s*=\s*"([a-z0-9_]+)"', static_section)))
    edit_names = quoted_array(ROOT / "Editing/EditToolProvider.cs", r"static readonly string\[\] ProductTools")
    edit_names += quoted_array(ROOT / "Editing/EditCompileFrontend.cs", r"static readonly string\[\] ProductTools")
    debug_text = (ROOT / "Debugger/DebugToolProvider.cs").read_text(encoding="utf-8")
    debug_names = ["debug_capabilities"]
    if debug_gate == "enabled":
        debug_names += quoted_array(
            ROOT / "Debugger/DebugToolProvider.cs", r"static readonly string\[\] AdvertisedSessionTools")
    if profile == "acceptance":
        # These are the ToolInfo names actually constructed in GetTools()'s
        # DNMCP_TEST branch. The four callable-only seams have no ToolInfo and
        # therefore cannot enter this source-derived advertised set.
        debug_names += sorted(set(re.findall(r'Name\s*=\s*"(debug_test_[a-z0-9_]+)"', debug_text)))
    families = {name: family for family, names in
                (("static", static_names), ("debug", debug_names), ("edit", edit_names)) for name in names}
    operation_kinds = quoted_array(ROOT / "Editing/EditContracts.cs", r"public static readonly string\[\] OperationKinds")
    expected_count = len(static_names) + len(edit_names) + len(debug_names)
    if len(families) != expected_count:
        raise RuntimeError("duplicate name across source provider sets")
    return families, operation_kinds


def digest(value: object | None) -> str | None:
    if value is None:
        return None
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--url", default="http://127.0.0.1:15378/mcp")
    source.add_argument("--wire-json", type=Path)
    parser.add_argument("--profile", choices=("production", "acceptance"), default="acceptance",
                        help="expected runtime profile (default: acceptance, preserving the P09 caller)")
    parser.add_argument("--debug-gate", choices=("enabled", "disabled"), default="enabled",
                        help="expected frozen dynamic-debugging gate state (default: enabled)")
    parser.add_argument("--output", default=str(ROOT / "dist/tool-registry-snapshot.json"))
    args = parser.parse_args()
    wire, metadata = file_tools(args.wire_json) if args.wire_json else wire_tools(args.url)
    wire_names = set(wire)
    source_data = source_registry(args.profile, args.debug_gate)
    if source_data is None:
        inferred_families = {name: "edit" if name.startswith("edit_") else
                             "debug" if name.startswith("debug_") else "static"
                             for name in wire_names}
        snapshot = {
            "format": "dnspy.mcp.tool-registry-snapshot.v2", "wire_source": metadata,
            "runtime_expectation": {"profile": args.profile, "debug_gate": args.debug_gate},
            "tool_count": len(wire_names),
            "family_counts": {family: sum(value == family for value in inferred_families.values())
                              for family in ("static", "debug", "edit")},
            "operation_kind_count": None, "operation_kinds": None,
            "tools": {name: {"family": inferred_families[name],
                "description": tool.get("description", ""),
                "has_input_schema": "inputSchema" in tool, "has_output_schema": "outputSchema" in tool,
                "input_schema_sha256": digest(tool.get("inputSchema")),
                "output_schema_sha256": digest(tool.get("outputSchema"))}
                for name, tool in sorted(wire.items())},
            "consistency": {"mode": "wire-only", "dual_source_verified": False,
                "source_scan_count": None, "missing_on_wire": None, "unknown_on_wire": None,
                "consistent": None,
                "note": "source tree unavailable; snapshot exported but source/wire consistency was not verified"},
        }
        output = Path(args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"tools={len(wire_names)} mode=wire-only dual_source_verified=false")
        print(f"snapshot: {output}")
        return 0
    families, operation_kinds = source_data
    source_names = set(families)
    missing_on_wire = sorted(source_names - wire_names)
    unknown_on_wire = sorted(wire_names - source_names)
    family_counts = {family: sum(value == family for value in families.values())
                     for family in ("static", "debug", "edit")}
    snapshot = {
        "format": "dnspy.mcp.tool-registry-snapshot.v2", "wire_source": metadata,
        "runtime_expectation": {"profile": args.profile, "debug_gate": args.debug_gate},
        "tool_count": len(wire_names), "family_counts": family_counts,
        "operation_kind_count": len(operation_kinds), "operation_kinds": operation_kinds,
        "tools": {name: {"family": families.get(name, "unknown"),
            "description": tool.get("description", ""),
            "has_input_schema": "inputSchema" in tool, "has_output_schema": "outputSchema" in tool,
            "input_schema_sha256": digest(tool.get("inputSchema")),
            "output_schema_sha256": digest(tool.get("outputSchema"))}
            for name, tool in sorted(wire.items())},
        "consistency": {"mode": "dual-source", "dual_source_verified": True,
            "source_scan_count": len(source_names), "missing_on_wire": missing_on_wire,
            "unknown_on_wire": unknown_on_wire, "consistent": not missing_on_wire and not unknown_on_wire,
            "excluded_unadvertised_edit_test_count": len(quoted_array(
                ROOT / "Editing/EditToolProvider.cs", r"static readonly string\[\] TestTools"))},
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(snapshot, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"tools={len(wire_names)} source={len(source_names)} families={family_counts} "
          f"operations={len(operation_kinds)} missing_on_wire={missing_on_wire} unknown_on_wire={unknown_on_wire}")
    print(f"snapshot: {output}")
    return 0 if snapshot["consistency"]["consistent"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
