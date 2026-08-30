"""Host-to-VM smoke probe for the Python client and transparent stdio bridge."""

from __future__ import annotations

import argparse
import io
import json
import os
from typing import Any

from dnspy_mcp import DnSpyClient
from dnspy_mcp.stdio import StdioProxy


def _stdio_probe(url: str, token: str | None) -> dict[str, Any]:
    messages = [
        {
            "jsonrpc": "2.0",
            "id": "host-init",
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "dnspy-host-probe", "version": "1"},
            },
        },
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": "host-tools", "method": "tools/list", "params": {}},
    ]
    source = io.StringIO("".join(json.dumps(message, separators=(",", ":")) + "\n" for message in messages))
    sink = io.StringIO()
    StdioProxy(DnSpyClient(url, token=token)).run(source, sink)
    replies = [json.loads(line) for line in sink.getvalue().splitlines()]
    if len(replies) != 2 or any("error" in reply for reply in replies):
        raise RuntimeError(f"stdio bridge probe failed: {replies!r}")
    tools = replies[1]["result"]["tools"]
    return {"reply_ids": [reply["id"] for reply in replies], "tool_count": len(tools)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--token", default=os.getenv("DNSPY_MCP_TOKEN"))
    args = parser.parse_args(argv)

    client = DnSpyClient(args.url, token=args.token, client_name="dnspy-host-probe")
    health = client.health()
    health.raise_for_status()
    initialized = client.initialize()
    tools = list(client.iter_tools())
    resources = client.list_resources().get("resources", [])
    resource_uris = {
        resource.get("uri") for resource in resources if isinstance(resource, dict)
    } if isinstance(resources, list) else set()
    docs_index = client.read_resource("dnspy://docs/index")
    dynamic_debugging = client.read_resource("dnspy://docs/dynamic-debugging")
    assemblies_result = client.call_tool_json("list_assemblies")
    assemblies = assemblies_result.get("assemblies") if isinstance(assemblies_result, dict) else None
    closed = client.close()
    summary = {
        "health_status": health.status,
        "protocol_version": initialized.get("protocolVersion"),
        "server_name": initialized.get("serverInfo", {}).get("name"),
        "tool_count": len(tools),
        "resource_count": len(resources) if isinstance(resources, list) else None,
        "instructions_present": isinstance(client.instructions, str) and "dnspy://docs/index" in client.instructions,
        "docs_index_present": "dnspy://docs/index" in resource_uris and bool(docs_index.get("contents")),
        "debug_artifact_subdir_documented": ".dnspy-mcp-debug" in json.dumps(dynamic_debugging),
        "list_assemblies_ok": isinstance(assemblies, list) and len(assemblies) > 0,
        "delete_status": closed.status if closed else None,
        "stdio": _stdio_probe(args.url, args.token),
    }
    if not (
        summary["health_status"] == 200
        and summary["protocol_version"] == "2025-06-18"
        and isinstance(summary["tool_count"], int)
        and summary["tool_count"] > 0
        and summary["resource_count"] == 14
        and summary["instructions_present"]
        and summary["docs_index_present"]
        and summary["debug_artifact_subdir_documented"]
        and summary["list_assemblies_ok"]
        and summary["delete_status"] == 200
        and summary["stdio"]["tool_count"] == summary["tool_count"]
    ):
        raise RuntimeError(f"remote probe assertions failed: {summary!r}")
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
