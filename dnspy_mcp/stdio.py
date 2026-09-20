"""Transparent stdio MCP bridge to a remote dnSpy Streamable HTTP server."""

from __future__ import annotations

import argparse
import copy
import json
import os
import sys
import time
from typing import Any, Mapping, TextIO

from .client import DnSpyClient, DnSpyConnectionError, DnSpyProtocolError, HttpResponse


_RECOVERY_TIMEOUT_SECONDS = 5.0
_RECOVERY_TOTAL_TIMEOUT_SECONDS = 15.0
_SAFE_RETRY_METHODS = frozenset({"ping", "tools/list"})
_SAFE_RETRY_TOOLS = frozenset({"edit_status"})
_UNKNOWN_SESSION_RESPONSE = "Unknown Mcp-Session-Id"


class StdioProxy:
    """Forward newline-delimited stdio JSON-RPC messages to dnSpy over HTTP."""

    def __init__(self, client: DnSpyClient, *, stderr: TextIO | None = None) -> None:
        self.client = client
        self.stderr = stderr or sys.stderr
        self._initialize_message: dict[str, Any] | None = None
        self._initialized_message: dict[str, Any] = {
            "jsonrpc": "2.0", "method": "notifications/initialized",
        }

    @staticmethod
    def _error(message: Mapping[str, Any], code: int, text: str, data: Any = None) -> dict[str, Any] | None:
        if "id" not in message:
            return None
        error: dict[str, Any] = {"code": code, "message": text}
        if data is not None:
            error["data"] = data
        return {"jsonrpc": "2.0", "id": message.get("id"), "error": error}

    @staticmethod
    def _can_retry(message: Mapping[str, Any]) -> bool:
        if "id" not in message:
            return False
        method = message.get("method")
        if method in _SAFE_RETRY_METHODS:
            return True
        if method != "tools/call":
            return False
        params = message.get("params")
        tool = params.get("name") if isinstance(params, dict) else None
        return isinstance(tool, str) and tool in _SAFE_RETRY_TOOLS

    @staticmethod
    def _is_unknown_session(response: HttpResponse) -> bool:
        return response.status == 404 and response.text.strip() == _UNKNOWN_SESSION_RESPONSE

    @staticmethod
    def _validate_initialize_payload(
        message: Mapping[str, Any],
        response: HttpResponse,
        payload: Mapping[str, Any],
        *,
        previous_session: str | None = None,
    ) -> tuple[dict[str, Any] | None, str | None]:
        if payload.get("jsonrpc") != "2.0":
            return None, "initialize response has an invalid jsonrpc version"
        if "id" not in message or "id" not in payload:
            return None, "initialize response is missing its request id"
        request_id = message["id"]
        response_id = payload["id"]
        if type(response_id) is not type(request_id) or response_id != request_id:
            return None, "initialize response id does not match the request"
        if "error" in payload:
            return None, "initialize returned a JSON-RPC error"
        result = payload.get("result")
        if not isinstance(result, dict):
            return None, "initialize response result is not an object"
        protocol = result.get("protocolVersion")
        if not isinstance(protocol, str) or not protocol.strip():
            return None, "initialize response has no negotiated protocol version"
        session = response.header("Mcp-Session-Id")
        if not isinstance(session, str) or not session.strip():
            return None, "initialize response has no session id"
        if previous_session is not None and session == previous_session:
            return None, "initialize response reused the invalid session id"
        return result, None

    def _decode(self, message: Mapping[str, Any], response: HttpResponse) -> dict[str, Any] | None:
        method = message.get("method")
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
        if method == "initialize":
            negotiated, _ = self._validate_initialize_payload(message, response, payload)
            if negotiated is not None:
                self.client.protocol_version = negotiated["protocolVersion"]
                self._initialize_message = copy.deepcopy(dict(message))
        return payload

    def _send(self, message: Mapping[str, Any], *, timeout: float | None = None) -> HttpResponse:
        return self.client.request_object(
            message,
            include_session=message.get("method") != "initialize",
            timeout=timeout,
        )

    def _recovery_timeout(self, deadline: float) -> float | None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return None
        return min(float(self.client.timeout), _RECOVERY_TIMEOUT_SECONDS, remaining)

    def _recover_session(self, deadline: float) -> tuple[bool, str | None]:
        if self._initialize_message is None:
            return False, "no successful initialize is available"
        timeout = self._recovery_timeout(deadline)
        if timeout is None:
            return False, "automatic recovery time budget was exhausted before initialize"
        previous_session = self.client.session_id
        self.client.session_id = None
        try:
            initialized = self._send(self._initialize_message, timeout=timeout)
        except (DnSpyConnectionError, DnSpyProtocolError) as exc:
            return False, str(exc)
        if not 200 <= initialized.status < 300:
            self.client.session_id = None
            return False, f"initialize returned HTTP {initialized.status}: {initialized.text[:200]}"
        if not initialized.body:
            self.client.session_id = None
            return False, "initialize returned an empty response"
        try:
            payload = initialized.json()
        except DnSpyProtocolError as exc:
            self.client.session_id = None
            return False, str(exc)
        if not isinstance(payload, dict):
            self.client.session_id = None
            return False, "initialize returned a non-object response"
        negotiated, detail = self._validate_initialize_payload(
            self._initialize_message,
            initialized,
            payload,
            previous_session=previous_session,
        )
        if negotiated is None:
            self.client.session_id = None
            return False, detail
        self.client.session_id = initialized.header("Mcp-Session-Id")
        self.client.protocol_version = negotiated["protocolVersion"]
        timeout = self._recovery_timeout(deadline)
        if timeout is None:
            self.client.session_id = None
            return False, "automatic recovery time budget was exhausted before initialized notification"
        try:
            ready = self._send(self._initialized_message, timeout=timeout)
        except (DnSpyConnectionError, DnSpyProtocolError) as exc:
            self.client.session_id = None
            return False, str(exc)
        if not 200 <= ready.status < 300:
            self.client.session_id = None
            return False, f"notifications/initialized returned HTTP {ready.status}: {ready.text[:200]}"
        return True, None

    def _recover_after_failure(
        self,
        message: Mapping[str, Any],
        original: dict[str, Any] | None,
    ) -> dict[str, Any] | None:
        deadline = time.monotonic() + _RECOVERY_TOTAL_TIMEOUT_SECONDS
        recovered, detail = self._recover_session(deadline)
        if not recovered:
            if self._initialize_message is not None:
                print(f"dnspy-mcp-stdio: automatic session recovery failed: {detail}", file=self.stderr)
            return original
        if not self._can_retry(message):
            return original
        timeout = self._recovery_timeout(deadline)
        if timeout is None:
            print("dnspy-mcp-stdio: automatic session recovery retry skipped: time budget exhausted",
                  file=self.stderr)
            return original
        try:
            return self._decode(message, self._send(message, timeout=timeout))
        except DnSpyConnectionError as exc:
            return self._error(message, -32000, "dnSpy MCP is unreachable", str(exc))

    def forward(self, message: Mapping[str, Any]) -> dict[str, Any] | None:
        method = message.get("method")
        if not isinstance(method, str):
            return self._error(message, -32600, "Invalid Request")
        if method == "initialize":
            params = message.get("params")
            if isinstance(params, dict) and isinstance(params.get("protocolVersion"), str):
                self.client.protocol_version = params["protocolVersion"]
        elif method == "notifications/initialized":
            self._initialized_message = copy.deepcopy(dict(message))
        try:
            response = self._send(message)
        except DnSpyConnectionError as exc:
            original = self._error(message, -32000, "dnSpy MCP is unreachable", str(exc))
            if method == "initialize" or self._initialize_message is None:
                return original
            return self._recover_after_failure(message, original)
        original = self._decode(message, response)
        if method != "initialize" and self._initialize_message is not None and self._is_unknown_session(response):
            return self._recover_after_failure(message, original)
        return original

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
