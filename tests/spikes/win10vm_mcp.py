#!/usr/bin/env python3
"""Throwaway Streamable HTTP MCP client for the Win10VM feasibility spikes.

Unlike dnSpy.Extension.MCP, Win10VM returns JSON-RPC messages in an SSE body.
This adapter deliberately lives under tests/spikes and is not production code.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Mapping

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp.client import (
    DnSpyClient,
    DnSpyProtocolError,
    ToolCallError,
    _MISSING,
)


def _decode_sse(body: bytes) -> Mapping[str, Any]:
    text = body.decode("utf-8")
    data_lines: list[str] = []
    messages: list[Mapping[str, Any]] = []
    for line in text.splitlines() + [""]:
        if line == "":
            if data_lines:
                value = json.loads("\n".join(data_lines))
                if isinstance(value, dict):
                    messages.append(value)
                data_lines.clear()
            continue
        if line.startswith("data:"):
            data_lines.append(line[5:].lstrip())
    if not messages:
        raise DnSpyProtocolError("Win10VM returned SSE without a JSON-RPC message")
    return messages[-1]


class Win10VmClient(DnSpyClient):
    """Minimal SSE-capable client used only by S01/S02."""

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
        response = self.request_object(
            message,
            include_session=include_session,
            timeout=timeout,
        )
        response.raise_for_status()
        if actual_id is None:
            return None
        content_type = response.header("Content-Type", "") or ""
        payload = (
            _decode_sse(response.body)
            if "text/event-stream" in content_type.casefold()
            else response.json()
        )
        if payload.get("id") != actual_id:
            raise DnSpyProtocolError(
                f"response id {payload.get('id')!r} does not match request id {actual_id!r}"
            )
        error = payload.get("error")
        if isinstance(error, dict):
            raise DnSpyProtocolError(
                str(error.get("message", "Unknown JSON-RPC error")),
                code=error.get("code") if isinstance(error.get("code"), int) else None,
                data=error.get("data"),
                response=response,
            )
        if "result" not in payload:
            raise DnSpyProtocolError("JSON-RPC response has neither result nor error")
        return payload["result"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("tools", "call"))
    parser.add_argument("tool", nargs="?")
    parser.add_argument("arguments", nargs="?", default="{}")
    parser.add_argument("--url", default="http://192.168.204.149:28787/mcp")
    args = parser.parse_args()
    client = Win10VmClient.connect(args.url, client_name="dnspy-spike-runner")
    try:
        if args.command == "tools":
            result: Any = {"server": client.server_info, "tools": list(client.iter_tools())}
        else:
            if not args.tool:
                parser.error("call requires TOOL")
            result = client.call_tool_json(args.tool, json.loads(args.arguments))
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except ToolCallError as exc:
        print(json.dumps({"error": str(exc)}, ensure_ascii=False, indent=2))
        return 1
    finally:
        client.close()


if __name__ == "__main__":
    raise SystemExit(main())
