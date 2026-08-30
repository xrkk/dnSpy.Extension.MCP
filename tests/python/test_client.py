from __future__ import annotations

import io
import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any
from unittest.mock import patch

from dnspy_mcp import DnSpyClient, DnSpyConnectionError, DnSpyHttpError, DnSpyProtocolError
from dnspy_mcp.http_cli import main as http_cli_main
from dnspy_mcp.stdio import StdioProxy


class FakeDnSpyHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    sessions: set[str] = set()
    next_session = 0
    max_sessions = 16
    lock = threading.Lock()
    requests: list[dict[str, Any]] = []

    def log_message(self, *_: object) -> None:
        pass

    def _write(
        self,
        status: int,
        body: bytes = b"",
        *,
        headers: dict[str, str] | None = None,
    ) -> None:
        self.send_response(status)
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path == "/health":
            self._write(200, b'{"status":"ok"}', headers={"Content-Type": "application/json"})
        else:
            self._write(404)

    def do_DELETE(self) -> None:
        session_id = self.headers.get("Mcp-Session-Id")
        with self.lock:
            self.sessions.discard(session_id or "")
        self._write(200)

    def do_POST(self) -> None:
        size = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(size)
        try:
            message = json.loads(raw)
        except json.JSONDecodeError:
            body = b'{"jsonrpc":"2.0","error":{"code":-32700,"message":"Parse error"}}'
            self._write(200, body, headers={"Content-Type": "application/json"})
            return
        self.requests.append({"message": message, "headers": dict(self.headers.items()), "raw": raw})
        method = message.get("method")
        if method == "initialize":
            with self.lock:
                if len(self.sessions) >= self.max_sessions:
                    self._write(429, headers={"Retry-After": "1"})
                    return
                type(self).next_session += 1
                session_id = f"session-{self.next_session}"
                self.sessions.add(session_id)
            version = message.get("params", {}).get("protocolVersion", "2025-06-18")
            result = {
                "protocolVersion": version,
                "capabilities": {"tools": {}, "resources": {}},
                "serverInfo": {"name": "fake-dnspy", "version": "1"},
                "instructions": "Read dnspy://docs/index first.",
            }
            self._rpc(message, result, headers={"Mcp-Session-Id": session_id})
            return
        if method and (method.startswith("notifications/") or "id" not in message):
            self._write(202)
            return
        if method == "tools/list":
            self._rpc(
                message,
                {
                    "tools": [
                        {
                            "name": "echo",
                            "description": "echo arguments",
                            "inputSchema": {"type": "object"},
                        }
                    ]
                },
            )
            return
        if method == "tools/call":
            arguments = message.get("params", {}).get("arguments", {})
            result = {
                "content": [{"type": "text", "text": json.dumps(arguments)}],
                "structuredContent": arguments,
                "isError": False,
            }
            self._rpc(message, result)
            return
        if method == "resources/list":
            self._rpc(message, {"resources": [{"uri": "bepinex://docs/test", "name": "test"}]})
            return
        if method == "resources/read":
            self._rpc(message, {"contents": [{"uri": message["params"]["uri"], "text": "doc"}]})
            return
        if method == "test/error":
            body = json.dumps(
                {
                    "jsonrpc": "2.0",
                    "id": message.get("id"),
                    "error": {"code": -32602, "message": "bad input", "data": {"field": "x"}},
                },
                separators=(",", ":"),
            ).encode()
            self._write(200, body, headers={"Content-Type": "application/json"})
            return
        self._rpc(message, {})

    def _rpc(
        self,
        request: dict[str, Any],
        result: Any,
        *,
        headers: dict[str, str] | None = None,
    ) -> None:
        body = json.dumps(
            {"jsonrpc": "2.0", "id": request.get("id"), "result": result},
            separators=(",", ":"),
        ).encode()
        response_headers = {"Content-Type": "application/json"}
        response_headers.update(headers or {})
        self._write(200, body, headers=response_headers)


class ClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), FakeDnSpyHandler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.url = f"http://127.0.0.1:{cls.server.server_port}/"

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)

    def setUp(self) -> None:
        with FakeDnSpyHandler.lock:
            FakeDnSpyHandler.sessions.clear()
            FakeDnSpyHandler.next_session = 0
        FakeDnSpyHandler.requests.clear()

    def test_initialize_tools_resources_and_close(self) -> None:
        client = DnSpyClient(self.url, client_name="unit-test")
        initialized = client.initialize()
        self.assertEqual("2025-06-18", initialized["protocolVersion"])
        self.assertEqual("session-1", client.session_id)
        self.assertEqual("Read dnspy://docs/index first.", client.instructions)
        self.assertEqual(["echo"], [tool["name"] for tool in client.iter_tools()])
        self.assertEqual({"value": "quoted \" JSON"}, client.call_tool_json("echo", {"value": 'quoted " JSON'}))
        resources = client.list_resources()
        self.assertEqual("bepinex://docs/test", resources["resources"][0]["uri"])
        self.assertEqual("doc", client.read_resource("bepinex://docs/test")["contents"][0]["text"])
        self.assertEqual(200, client.close().status)
        self.assertIsNone(client.session_id)
        self.assertFalse(FakeDnSpyHandler.sessions)

        initialize_wire = FakeDnSpyHandler.requests[0]
        self.assertEqual("initialize", initialize_wire["message"]["method"])
        self.assertIn(b'"clientInfo":{"name":"unit-test"', initialize_wire["raw"])
        tool_wire = next(row for row in FakeDnSpyHandler.requests if row["message"]["method"] == "tools/call")
        self.assertEqual('quoted " JSON', tool_wire["message"]["params"]["arguments"]["value"])
        self.assertEqual("session-1", tool_wire["headers"]["Mcp-Session-Id"])

    def test_rpc_errors_keep_code_and_data(self) -> None:
        client = DnSpyClient.connect(self.url)
        with self.assertRaises(DnSpyProtocolError) as caught:
            client.request("test/error", {"x": 1})
        self.assertEqual(-32602, caught.exception.code)
        self.assertEqual({"field": "x"}, caught.exception.data)
        client.close()

    def test_raw_request_preserves_invalid_json_and_http_status(self) -> None:
        client = DnSpyClient(self.url)
        response = client.raw_request(
            "POST",
            body=b"{not-json",
            headers={"Content-Type": "application/json"},
            include_session=False,
        )
        self.assertEqual(200, response.status)
        self.assertEqual(-32700, response.json()["error"]["code"])

    def test_http_status_cli_maps_connection_failure_to_curl_000(self) -> None:
        with TemporaryDirectory() as directory:
            output = Path(directory) / "status.txt"
            with patch(
                "dnspy_mcp.http_cli.DnSpyClient.raw_request",
                side_effect=DnSpyConnectionError("connection refused"),
            ):
                exit_code = http_cli_main(["--url", self.url, "--format", "status", "--output", str(output)])
            self.assertEqual(0, exit_code)
            self.assertEqual(b"000", output.read_bytes())

    def test_seventeenth_session_is_429_without_shell_quoting(self) -> None:
        clients: list[DnSpyClient] = []
        try:
            for index in range(16):
                client = DnSpyClient(self.url, client_name=f"limit-{index}")
                client.initialize(send_initialized=False)
                clients.append(client)
            seventeenth = DnSpyClient(self.url)
            with self.assertRaises(DnSpyHttpError) as caught:
                seventeenth.initialize(send_initialized=False)
            self.assertEqual(429, caught.exception.response.status)
            self.assertEqual(16, len(FakeDnSpyHandler.sessions))
        finally:
            for client in clients:
                client.close()

    def test_stdio_proxy_is_transparent_and_silent_for_notifications(self) -> None:
        messages = [
            {
                "jsonrpc": "2.0",
                "id": "init",
                "method": "initialize",
                "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {}},
            },
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 7, "method": "tools/list", "params": {}},
        ]
        source = io.StringIO("".join(json.dumps(message) + "\n" for message in messages))
        sink = io.StringIO()
        proxy = StdioProxy(DnSpyClient(self.url))
        self.assertEqual(0, proxy.run(source, sink))
        replies = [json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertEqual(["init", 7], [reply["id"] for reply in replies])
        self.assertEqual("2025-03-26", replies[0]["result"]["protocolVersion"])
        self.assertEqual("Read dnspy://docs/index first.", replies[0]["result"]["instructions"])
        self.assertEqual("echo", replies[1]["result"]["tools"][0]["name"])
        self.assertFalse(FakeDnSpyHandler.sessions)


if __name__ == "__main__":
    unittest.main()
