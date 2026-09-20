from __future__ import annotations

import json
import unittest
from collections import deque
from typing import Any
from unittest.mock import patch

from dnspy_mcp.client import DnSpyConnectionError, DnSpyProtocolError, HttpResponse
from dnspy_mcp.stdio import StdioProxy


def response(status: int, body: Any = None, *, session: str | None = None) -> HttpResponse:
    headers = {"Content-Type": "application/json"}
    if session:
        headers["Mcp-Session-Id"] = session
    encoded = b"" if body is None else (
        body if isinstance(body, bytes) else
        body.encode("utf-8") if isinstance(body, str) else
        json.dumps(body, separators=(",", ":")).encode("utf-8"))
    return HttpResponse(status, "test", headers, encoded)


def rpc(request_id: object, result: Any) -> dict[str, Any]:
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


class ScriptedClient:
    timeout = 40.0

    def __init__(self, outcomes: list[HttpResponse | Exception]) -> None:
        self.outcomes = deque(outcomes)
        self.calls: list[dict[str, Any]] = []
        self.session_id: str | None = None
        self.protocol_version = "2025-06-18"

    def request_object(self, message, *, include_session=True, timeout=None):
        self.calls.append({"message": message, "include_session": include_session,
                           "timeout": timeout, "session_id": self.session_id})
        outcome = self.outcomes.popleft()
        if isinstance(outcome, Exception):
            raise outcome
        session = outcome.header("Mcp-Session-Id")
        if session:
            self.session_id = session
        return outcome

    def close(self):
        self.session_id = None
        return None


INITIALIZE = {
    "jsonrpc": "2.0", "id": "initial",
    "method": "initialize",
    "params": {
        "protocolVersion": "2025-03-26",
        "capabilities": {"sampling": {}},
        "clientInfo": {"name": "recovery-test", "version": "7"},
    },
}
INITIALIZED = {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {"ready": True}}


def initialized_proxy(outcomes: list[HttpResponse | Exception]) -> tuple[StdioProxy, ScriptedClient]:
    client = ScriptedClient([
        response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-1"),
        response(202),
        *outcomes,
    ])
    proxy = StdioProxy(client)  # type: ignore[arg-type]
    assert "error" not in proxy.forward(INITIALIZE)
    assert proxy.forward(INITIALIZED) is None
    return proxy, client


class StdioRecoveryTests(unittest.TestCase):
    def test_connection_reset_reinitializes_same_proxy_and_retries_edit_status_once(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("reset after listener restart"),
            response(200, rpc("initial", {"protocolVersion": "2025-06-18"}), session="session-2"),
            response(202),
            response(200, rpc(9, {"content": [], "structuredContent": {"state": "idle"}})),
        ])

        reply = proxy.forward({"jsonrpc": "2.0", "id": 9, "method": "tools/call",
                               "params": {"name": "edit_status", "arguments": {}}})

        self.assertEqual("session-2", client.session_id)
        self.assertEqual("2025-06-18", client.protocol_version)
        self.assertEqual({"state": "idle"}, reply["result"]["structuredContent"])
        self.assertEqual(["initialize", "notifications/initialized", "tools/call",
                          "initialize", "notifications/initialized", "tools/call"],
                         [row["message"]["method"] for row in client.calls])
        self.assertEqual(INITIALIZE["params"], client.calls[3]["message"]["params"])
        self.assertEqual(INITIALIZED["params"], client.calls[4]["message"]["params"])
        self.assertEqual(5.0, client.calls[3]["timeout"])
        self.assertEqual(5.0, client.calls[5]["timeout"])

    def test_exact_unknown_session_404_recovers_tools_list(self):
        proxy, client = initialized_proxy([
            response(404, "Unknown Mcp-Session-Id"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
            response(200, rpc(3, {"tools": []})),
        ])

        reply = proxy.forward({"jsonrpc": "2.0", "id": 3, "method": "tools/list", "params": {}})

        self.assertEqual([], reply["result"]["tools"])
        self.assertEqual(2, sum(row["message"]["method"] == "tools/list" for row in client.calls))

    def test_ping_is_the_only_other_retried_method(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("reset"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
            response(200, rpc(4, {})),
        ])
        self.assertNotIn("error", proxy.forward({"jsonrpc": "2.0", "id": 4, "method": "ping"}))
        self.assertEqual(2, sum(row["message"]["method"] == "ping" for row in client.calls))

    def test_mutation_is_not_replayed_but_session_is_ready_for_next_explicit_read(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("response lost after send"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
            response(200, rpc(11, {"content": [], "structuredContent": {"state": "idle"}})),
        ])
        mutation = {"jsonrpc": "2.0", "id": 10, "method": "tools/call",
                    "params": {"name": "edit_apply", "arguments": {"request_id": "once"}}}

        uncertain = proxy.forward(mutation)
        safe = proxy.forward({"jsonrpc": "2.0", "id": 11, "method": "tools/call",
                              "params": {"name": "edit_status", "arguments": {}}})

        self.assertEqual(-32000, uncertain["error"]["code"])
        self.assertEqual({"state": "idle"}, safe["result"]["structuredContent"])
        self.assertEqual(1, sum(row["message"] == mutation for row in client.calls))

    def test_unknown_and_other_tools_calls_never_replay(self):
        for tool in ("future_read", "debug_status", "edit_export", "edit_recover"):
            with self.subTest(tool=tool):
                proxy, client = initialized_proxy([
                    DnSpyConnectionError("reset"),
                    response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
                    response(202),
                ])
                message = {"jsonrpc": "2.0", "id": 20, "method": "tools/call",
                           "params": {"name": tool, "arguments": {}}}
                self.assertEqual(-32000, proxy.forward(message)["error"]["code"])
                self.assertEqual(1, sum(row["message"] == message for row in client.calls))

    def test_auth_rate_limit_and_non_session_404_do_not_recover(self):
        for status, body in ((401, "unauthorized"), (403, "forbidden"), (429, "slow down"),
                             (404, "ordinary missing route"), (500, "unknown")):
            with self.subTest(status=status):
                proxy, client = initialized_proxy([response(status, body)])
                reply = proxy.forward({"jsonrpc": "2.0", "id": 30,
                                       "method": "tools/list", "params": {}})
                self.assertEqual(-32001, reply["error"]["code"])
                self.assertEqual(3, len(client.calls))

    def test_json_rpc_business_error_does_not_recover(self):
        proxy, client = initialized_proxy([
            response(200, {"jsonrpc": "2.0", "id": 35,
                           "error": {"code": -32602, "message": "bad input"}}),
        ])
        reply = proxy.forward({"jsonrpc": "2.0", "id": 35,
                               "method": "tools/list", "params": {}})
        self.assertEqual(-32602, reply["error"]["code"])
        self.assertEqual(3, len(client.calls))

    def test_no_successful_initialize_means_no_automatic_recovery(self):
        client = ScriptedClient([DnSpyConnectionError("offline")])
        proxy = StdioProxy(client)  # type: ignore[arg-type]
        reply = proxy.forward({"jsonrpc": "2.0", "id": 40, "method": "tools/list", "params": {}})
        self.assertEqual(-32000, reply["error"]["code"])
        self.assertEqual(1, len(client.calls))

    def test_failed_recovery_is_attempted_once_and_preserves_original_error(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("original reset"),
            DnSpyConnectionError("initialize still offline"),
        ])
        reply = proxy.forward({"jsonrpc": "2.0", "id": 50, "method": "tools/list", "params": {}})
        self.assertEqual(-32000, reply["error"]["code"])
        self.assertIn("original reset", str(reply["error"]["data"]))
        self.assertEqual(4, len(client.calls))

    def test_invalid_initialize_handshakes_do_not_notify_ready_or_retry(self):
        invalid = (
            ("empty object", response(200, {}, session="session-2")),
            ("wrong jsonrpc", response(200, {
                "jsonrpc": "1.0", "id": "initial",
                "result": {"protocolVersion": "2025-03-26"},
            }, session="session-2")),
            ("wrong id", response(200, rpc("other", {
                "protocolVersion": "2025-03-26",
            }), session="session-2")),
            ("missing result", response(200, {
                "jsonrpc": "2.0", "id": "initial",
            }, session="session-2")),
            ("missing protocol", response(200, rpc("initial", {}), session="session-2")),
            ("missing new session", response(200, rpc("initial", {
                "protocolVersion": "2025-03-26",
            }))),
            ("stale session", response(200, rpc("initial", {
                "protocolVersion": "2025-03-26",
            }), session="session-1")),
            ("invalid json", response(200, b"not-json", session="session-2")),
            ("protocol decode", DnSpyProtocolError("invalid gzip response")),
        )
        request = {"jsonrpc": "2.0", "id": 51, "method": "tools/list", "params": {}}
        for label, outcome in invalid:
            with self.subTest(label=label):
                proxy, client = initialized_proxy([
                    DnSpyConnectionError("original reset"), outcome,
                ])

                reply = proxy.forward(request)

                self.assertEqual(-32000, reply["error"]["code"])
                self.assertIn("original reset", str(reply["error"]["data"]))
                self.assertEqual(["initialize", "notifications/initialized", "tools/list", "initialize"],
                                 [row["message"]["method"] for row in client.calls])
                self.assertIsNone(client.session_id)

    def test_recovery_uses_one_monotonic_budget_and_stops_when_it_is_exhausted(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("original reset"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}),
                     session="session-2"),
            response(202),
        ])
        request = {"jsonrpc": "2.0", "id": 52, "method": "tools/list", "params": {}}

        with patch("dnspy_mcp.stdio.time.monotonic", side_effect=(100.0, 100.0, 114.0, 115.01)):
            reply = proxy.forward(request)

        self.assertEqual(-32000, reply["error"]["code"])
        self.assertEqual(["initialize", "notifications/initialized", "tools/list",
                          "initialize", "notifications/initialized"],
                         [row["message"]["method"] for row in client.calls])
        self.assertEqual([5.0, 1.0], [row["timeout"] for row in client.calls[-2:]])

    def test_recovery_budget_exhaustion_after_initialize_skips_ready_and_retry(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("original reset"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}),
                     session="session-2"),
        ])
        request = {"jsonrpc": "2.0", "id": 53, "method": "tools/list", "params": {}}

        with patch("dnspy_mcp.stdio.time.monotonic", side_effect=(200.0, 200.0, 215.01)):
            reply = proxy.forward(request)

        self.assertEqual(-32000, reply["error"]["code"])
        self.assertEqual(["initialize", "notifications/initialized", "tools/list", "initialize"],
                         [row["message"]["method"] for row in client.calls])
        self.assertEqual(5.0, client.calls[-1]["timeout"])
        self.assertIsNone(client.session_id)

    def test_notification_remains_silent_and_is_not_replayed(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("notification outcome unknown"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
        ])
        notification = {"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 1}}
        self.assertIsNone(proxy.forward(notification))
        self.assertEqual(1, sum(row["message"] == notification for row in client.calls))

    def test_ping_notification_is_not_replayed(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("notification outcome unknown"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
        ])
        notification = {"jsonrpc": "2.0", "method": "ping"}
        self.assertIsNone(proxy.forward(notification))
        self.assertEqual(1, sum(row["message"] == notification for row in client.calls))

    def test_recovery_timeout_is_capped_by_client_timeout(self):
        proxy, client = initialized_proxy([
            DnSpyConnectionError("reset"),
            response(200, rpc("initial", {"protocolVersion": "2025-03-26"}), session="session-2"),
            response(202),
            response(200, rpc(60, {"tools": []})),
        ])
        client.timeout = 1.25
        self.assertNotIn("error", proxy.forward(
            {"jsonrpc": "2.0", "id": 60, "method": "tools/list", "params": {}}))
        self.assertEqual([1.25, 1.25, 1.25], [row["timeout"] for row in client.calls[-3:]])


if __name__ == "__main__":
    unittest.main()
