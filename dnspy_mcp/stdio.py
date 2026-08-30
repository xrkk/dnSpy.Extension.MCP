"""Transparent stdio MCP bridge to a remote dnSpy Streamable HTTP server."""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Mapping, TextIO

from .client import DnSpyClient, DnSpyConnectionError, DnSpyProtocolError


class StdioProxy:
    """Forward newline-delimited stdio JSON-RPC messages to dnSpy over HTTP."""

    def __init__(self, client: DnSpyClient, *, stderr: TextIO | None = None) -> None:
        self.client = client
        self.stderr = stderr or sys.stderr

    @staticmethod
    def _error(message: Mapping[str, Any], code: int, text: str, data: Any = None) -> dict[str, Any] | None:
        if "id" not in message:
            return None
        error: dict[str, Any] = {"code": code, "message": text}
        if data is not None:
            error["data"] = data
        return {"jsonrpc": "2.0", "id": message.get("id"), "error": error}

    def forward(self, message: Mapping[str, Any]) -> dict[str, Any] | None:
        method = message.get("method")
        if not isinstance(method, str):
            return self._error(message, -32600, "Invalid Request")
        include_session = method != "initialize"
        if method == "initialize":
            params = message.get("params")
            if isinstance(params, dict) and isinstance(params.get("protocolVersion"), str):
                self.client.protocol_version = params["protocolVersion"]
        try:
            response = self.client.request_object(message, include_session=include_session)
        except DnSpyConnectionError as exc:
            return self._error(message, -32000, "dnSpy MCP is unreachable", str(exc))
        if not 200 <= response.status < 300:
            return self._error(
                message,
                -32001,
                f"dnSpy MCP returned HTTP {response.status}",
                response.text or None,
            )
        if "id" not in message:
            return None
        if not response.body:
            return self._error(message, -32002, "dnSpy MCP returned an empty response")
        try:
            payload = response.json()
        except DnSpyProtocolError as exc:
            return self._error(message, -32002, str(exc), response.text)
        if not isinstance(payload, dict):
            return self._error(message, -32002, "dnSpy MCP returned a non-object response")
        negotiated = payload.get("result")
        if method == "initialize" and isinstance(negotiated, dict):
            version = negotiated.get("protocolVersion")
            if isinstance(version, str):
                self.client.protocol_version = version
        return payload

    def run(self, stdin: TextIO | None = None, stdout: TextIO | None = None) -> int:
        source = stdin or sys.stdin
        sink = stdout or sys.stdout
        try:
            for line in source:
                if not line.strip():
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError as exc:
                    reply = {
                        "jsonrpc": "2.0",
                        "id": None,
                        "error": {"code": -32700, "message": "Parse error", "data": str(exc)},
                    }
                else:
                    if not isinstance(message, dict):
                        reply = {
                            "jsonrpc": "2.0",
                            "id": None,
                            "error": {"code": -32600, "message": "Invalid Request"},
                        }
                    else:
                        reply = self.forward(message)
                if reply is not None:
                    sink.write(json.dumps(reply, ensure_ascii=False, separators=(",", ":")) + "\n")
                    sink.flush()
        finally:
            try:
                self.client.close()
            except DnSpyConnectionError as exc:
                print(f"dnspy-mcp-stdio: session cleanup failed: {exc}", file=self.stderr)
        return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Expose a remote dnSpy MCP as a local stdio MCP")
    parser.add_argument("--url", default=os.getenv("DNSPY_MCP_URL", "http://localhost:15378/"))
    parser.add_argument("--token", default=os.getenv("DNSPY_MCP_TOKEN"))
    parser.add_argument("--timeout", type=float, default=float(os.getenv("DNSPY_MCP_TIMEOUT", "40")))
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    client = DnSpyClient(args.url, token=args.token, timeout=args.timeout)
    return StdioProxy(client).run()


if __name__ == "__main__":
    raise SystemExit(main())
