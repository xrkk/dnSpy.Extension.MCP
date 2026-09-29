"""Component unit tests: recorder error-path accounting (§4a five frozen points).

Fakes stub the transport layer (instance-level `request`), so the real
RecordingClient.call_tool / call_tool_json recording paths are exercised.
"""

from __future__ import annotations

import pytest

from dnspy_mcp import ToolCallError

from dnspy_scenario.assertions import AssertionApi
from dnspy_scenario.ledger import LedgerWriter, read_ledger
from dnspy_scenario.recorder import RecordingClient, extract_error_code


def make_client(tmp_path):
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    asserts = AssertionApi(ledger, "S-R-01", "net48-fam-01")
    ledger.meta("S-R-01", "net48-fam-01", ["t"], "net48", "d")
    client = RecordingClient(ledger, "S-R-01", "net48-fam-01", asserts,
                             "http://127.0.0.1:1/mcp")
    client._queue = []

    def fake_request(method, params=None, ok=(200,)):
        assert method == "tools/call"
        kind, payload = client._queue.pop(0)
        if kind == "ok":
            return payload
        return {"isError": True, "content": [{"type": "text", "text": payload}]}

    client.request = fake_request
    return client, ledger, asserts


def enqueue_error(client, text):
    client._queue.append(("err", text))


def enqueue_ok(client, result):
    client._queue.append(("ok", result))


def test_error_code_extraction():
    code, head = extract_error_code(
        '{"error": {"code": "CAPABILITY_UNAVAILABLE", "message": "x"}}')
    assert code == "CAPABILITY_UNAVAILABLE"
    code2, _ = extract_error_code("not json")
    assert code2 is None


def test_unexpected_error_recorded_and_reraised(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_error(client, '{"error": {"code": "E_1"}}')
    with pytest.raises(ToolCallError):
        client.call_tool("t", {})  # no prediction -> raises
    rows = read_ledger(ledger.path)
    call_row = [r for r in rows if r["kind"] == "call"][0]
    assert call_row["outcome"] == "unexpected_error"
    assert call_row["response_summary"]["error_code"] == "E_1"
    assert client.unexpected_errors == 1


def test_expected_error_strong_assert_written_and_swallowed(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_error(client, '{"error": {"code": "E_1"}}')
    with client.expect_error("t", "E_1"):
        client.call_tool("t", {})  # expected hit: swallowed, no raise
    rows = read_ledger(ledger.path)
    assert_row = [r for r in rows if r["kind"] == "assert"][0]
    assert assert_row["grade"] == "strong"
    assert assert_row["verdict"] == "pass"
    assert assert_row["expected_error"] is True
    call_row = [r for r in rows if r["kind"] == "call"][0]
    assert call_row["outcome"] == "expected_error"
    assert client.expected_errors == 1


def test_expected_error_any_code_matches_when_none_given(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_error(client, '{"error": {"code": "WHATEVER"}}')
    with client.expect_error("t", None):
        client.call_tool("t", {})  # any code accepted: swallowed
    assert client.expected_errors == 1


def test_expected_error_wrong_code_fails_strong(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_error(client, '{"error": {"code": "OTHER"}}')
    with client.expect_error("t", "E_1"):
        with pytest.raises(AssertionError):
            client.call_tool("t", {})


def test_prediction_must_precede_call(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_error(client, '{"error": {"code": "E_1"}}')
    with pytest.raises(ToolCallError):
        client.call_tool("t", {})  # no prediction -> unexpected
    rows = read_ledger(ledger.path)
    call_row = [r for r in rows if r["kind"] == "call"][0]
    assert call_row["outcome"] == "unexpected_error"


def test_ok_call_and_json_call_recorded(tmp_path):
    client, ledger, _ = make_client(tmp_path)
    enqueue_ok(client, {"content": []})
    enqueue_ok(client, {"structuredContent": {"a": 1}, "content": []})
    client.call_tool("t1", {"a": 1})
    client.call_tool_json("t2", {})
    rows = [r for r in read_ledger(ledger.path) if r["kind"] == "call"]
    assert [r["tool"] for r in rows] == ["t1", "t2"]
    assert all(r["outcome"] == "ok" for r in rows)
    assert rows[0]["step_seq"] == 1 and rows[1]["step_seq"] == 2


def test_raw_tools_call_via_request_object_also_recorded(tmp_path):
    """The bypass path request_object(tools/call message) must be recorded too."""
    client, ledger, _ = make_client(tmp_path)
    enqueue_ok(client, {"content": []})
    client.request_object({"jsonrpc": "2.0", "id": 99, "method": "tools/call",
                           "params": {"name": "t-via-raw", "arguments": {}}})
    rows = [r for r in read_ledger(ledger.path) if r["kind"] == "call"]
    assert rows and rows[0]["tool"] == "t-via-raw" and rows[0]["outcome"] == "ok"
