#!/usr/bin/env python3
"""AI-operated ACC-002 listener/stdio/explicit-port acceptance.

All dnSpy requests use the installed Python client or its installed stdio bridge.  The only
other MCP connection is the Win10VM UI automation endpoint used to press Apply in dnSpy.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from dnspy_mcp import DnSpyClient
from dnspy_mcp.client import DnSpyConnectionError, ToolCallError

from ui_apply_settings import UiMcpClient, apply_settings


VM_UI_URL = "http://192.168.204.149:28787/mcp"
HOST = "192.168.204.149"
OLD_URL = f"http://{HOST}:15378/"
NEW_URL = f"http://{HOST}:15379/"
BRIDGE = "/opt/dnspy-mcp-client/bin/dnspy-mcp-stdio"
UI_SIGNAL_DIR: Path | None = None
UI_SIGNAL_INDEX = 0


def rid(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex}"


def payload(result: dict[str, Any]) -> dict[str, Any]:
    if "error" in result:
        raise AssertionError(result)
    value = result["result"]
    structured = value.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    for row in value.get("content", []):
        if row.get("type") == "text":
            parsed = json.loads(row["text"])
            if isinstance(parsed, dict):
                return parsed
    raise AssertionError(f"tool result has no object payload: {value}")


class StdioBridge:
    def __init__(self, url: str, transcript: list[dict[str, Any]]) -> None:
        self.url = url
        self.transcript = transcript
        self.next_id = 1
        self.process = subprocess.Popen(
            [BRIDGE, "--url", url, "--timeout", "60"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, bufsize=1,
        )
        self.rpc("initialize", {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "p02-listener-acceptance", "version": "1"},
        })
        self.notify("notifications/initialized", {})

    def rpc(self, method: str, params: Any) -> dict[str, Any]:
        request_id = self.next_id
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        self.transcript.append({"direction": "request", "url": self.url, "message": message})
        assert self.process.stdin is not None and self.process.stdout is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            stderr = self.process.stderr.read() if self.process.stderr is not None else ""
            raise RuntimeError(f"stdio bridge exited without a reply: {stderr}")
        response = json.loads(line)
        self.transcript.append({"direction": "response", "url": self.url, "message": response})
        assert response.get("id") == request_id, response
        return response

    def notify(self, method: str, params: Any) -> None:
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        self.transcript.append({"direction": "request", "url": self.url, "message": message})
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def tool(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        return payload(self.rpc("tools/call", {"name": name, "arguments": arguments}))

    def close(self) -> None:
        if self.process.stdin is not None and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=10)


def context(bridge: StdioBridge) -> dict[str, Any]:
    value = bridge.tool("debug_test_transport", {"p01_action": "snapshot"})
    assert value["ok"] is True
    return value["result"]


def call_captured(call: Callable[[], Any]) -> dict[str, Any]:
    try:
        return {"kind": "response", "value": call()}
    except ToolCallError as exc:
        return {"kind": "tool_error", "value": json.loads(str(exc))}
    except Exception as exc:
        return {"kind": "transport_error", "type": type(exc).__name__, "message": str(exc)}


def wait_barrier(observer: DnSpyClient) -> dict[str, Any]:
    deadline = time.time() + 30
    while time.time() < deadline:
        value = observer.call_tool_json("edit_test_barrier", {"action": "snapshot"})
        if value["result"]["entered"]:
            return value
        time.sleep(0.05)
    raise TimeoutError("apply_before_mutation barrier was not entered")


def ui_apply(port: int) -> None:
    global UI_SIGNAL_INDEX
    if UI_SIGNAL_DIR is not None:
        UI_SIGNAL_INDEX += 1
        UI_SIGNAL_DIR.mkdir(parents=True, exist_ok=True)
        request = UI_SIGNAL_DIR / f"request-{UI_SIGNAL_INDEX:02d}-{port}.json"
        acknowledgement = UI_SIGNAL_DIR / f"ack-{UI_SIGNAL_INDEX:02d}-{port}.json"
        request.write_text(json.dumps({
            "schema_version": "dnspy.p02.ui-signal.v1",
            "sequence": UI_SIGNAL_INDEX,
            "host": HOST,
            "port": port,
            "local_override": False,
        }, indent=2) + "\n", encoding="utf-8")
        deadline = time.time() + 300
        while time.time() < deadline:
            if acknowledgement.is_file():
                value = json.loads(acknowledgement.read_text(encoding="utf-8"))
                if value.get("result") != "PASS":
                    raise RuntimeError(f"AI UI apply failed: {value}")
                return
            time.sleep(0.1)
        raise TimeoutError(f"AI UI apply acknowledgement timed out: {request}")
    client = UiMcpClient.connect(VM_UI_URL, client_name="p02-listener-ui", timeout=40)
    try:
        apply_settings(client, False, HOST, port)
    finally:
        client.close()


def wait_bridge(url: str, transcript: list[dict[str, Any]]) -> StdioBridge:
    deadline = time.time() + 30
    last: Exception | None = None
    while time.time() < deadline:
        try:
            return StdioBridge(url, transcript)
        except Exception as exc:
            last = exc
            time.sleep(0.25)
    raise RuntimeError(f"listener did not recover at explicit URL {url}: {last}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--ui-signal-dir", type=Path)
    args = parser.parse_args()
    global UI_SIGNAL_DIR
    UI_SIGNAL_DIR = args.ui_signal_dir.resolve() if args.ui_signal_dir else None
    transcript: list[dict[str, Any]] = []
    report: dict[str, Any] = {"schema_version": "dnspy.p02.listener-acceptance.v1"}
    bridge: StdioBridge | None = None
    observer: DnSpyClient | None = None
    restored = False
    try:
        bridge = wait_bridge(OLD_URL, transcript)
        old_context = context(bridge)
        old_session = old_context["authoritative_session_id"]
        begin_id = rid("listener-begin")
        begun = bridge.tool("edit_begin", {"request_id": begin_id, "assembly_name": "TestIL"})
        tx = begun["result"]["transaction"]["transaction_id"]

        owner = DnSpyClient(OLD_URL, timeout=60)
        owner.session_id = old_session
        owner.protocol_version = "2025-03-26"
        observer = DnSpyClient.connect(OLD_URL, timeout=60, client_name="p02-listener-observer")
        # A prior interrupted acceptance run can leave the process-local test
        # barrier armed even though its owning transport session was removed.
        # Reset only this test seam before arming the run's own barrier.
        observer.call_tool_json("edit_test_barrier", {"action": "reset"})
        owner.call_tool_json("edit_test_barrier", {"action": "arm", "name": "apply_before_mutation"})
        operation = {"kind": "type_add", "namespace": "P02Listener", "name": "StoppedApply"}
        apply_id = rid("listener-apply")
        holder: dict[str, Any] = {}
        thread = threading.Thread(target=lambda: holder.update(
            call_captured(lambda: owner.edit_apply(apply_id, tx, 0, operation))), daemon=True)
        thread.start()
        entered = wait_barrier(observer)
        ui_apply(15378)
        thread.join(timeout=30)
        assert not thread.is_alive(), "listener-stop apply did not settle"
        try:
            owner.close()
        except Exception:
            pass
        try:
            observer.close()
        except Exception:
            pass
        observer = None
        bridge.close()
        bridge = wait_bridge(OLD_URL, transcript)
        same_context = context(bridge)
        same_status = bridge.tool("edit_status", {})
        assert same_context["authoritative_session_id"] != old_session
        assert same_status["state"] == "idle", same_status
        same_begin = bridge.tool("edit_begin", {"request_id": rid("same-url-begin"), "assembly_name": "TestIL"})
        same_rollback = bridge.tool("edit_rollback", {
            "request_id": rid("same-url-rollback"),
            "transaction_id": same_begin["result"]["transaction"]["transaction_id"],
        })
        report["listener_stop_same_url"] = {
            "old_context": old_context, "barrier_entered": entered,
            "in_flight_apply": holder, "new_context": same_context,
            "status": same_status, "begin": same_begin, "rollback": same_rollback,
        }

        port_begin = bridge.tool("edit_begin", {
            "request_id": rid("port-begin"), "assembly_name": "TestIL",
        })
        port_tx = port_begin["result"]["transaction"]["transaction_id"]
        old_mutation_id = rid("old-port-mutation")
        old_payload = {"kind": "type_add", "namespace": "P02Listener", "name": "MustNotReplay"}
        old_apply = bridge.tool("edit_apply", {
            "request_id": old_mutation_id, "transaction_id": port_tx,
            "expected_revision": 0, "operation": old_payload,
        })
        ui_apply(15379)
        bridge.close()
        bridge = None
        old_unreachable = call_captured(lambda: DnSpyClient.connect(OLD_URL, timeout=2))
        assert old_unreachable["kind"] == "transport_error", old_unreachable
        bridge = wait_bridge(NEW_URL, transcript)
        new_context = context(bridge)
        new_status = bridge.tool("edit_status", {})
        assert new_status["state"] == "idle", new_status
        search = bridge.tool("search_types", {
            "query": "P02Listener.MustNotReplay", "assembly_name": "TestIL", "page_size": 20,
        })
        assert search["items"] == [], search
        new_begin = bridge.tool("edit_begin", {
            "request_id": rid("new-port-begin"), "assembly_name": "TestIL",
        })
        new_rollback = bridge.tool("edit_rollback", {
            "request_id": rid("new-port-rollback"),
            "transaction_id": new_begin["result"]["transaction"]["transaction_id"],
        })
        requests_to_new = [row for row in transcript
                           if row["direction"] == "request" and row["url"] == NEW_URL]
        encoded_new = json.dumps(requests_to_new, separators=(",", ":"))
        # The verification search intentionally contains ``MustNotReplay`` as
        # its query.  Reject only an actual replay of the old edit_apply request.
        replayed_mutations = [
            row for row in requests_to_new
            if row["message"].get("method") == "tools/call"
            and row["message"].get("params", {}).get("name") == "edit_apply"
        ]
        assert old_mutation_id not in encoded_new and replayed_mutations == []
        assert {row["url"] for row in transcript} <= {OLD_URL, NEW_URL}
        report["explicit_port_change"] = {
            "old_url": OLD_URL, "new_url": NEW_URL, "old_unreachable": old_unreachable,
            "old_apply": old_apply, "new_context": new_context, "new_status": new_status,
            "no_live_replay_search": search, "new_begin": new_begin,
            "new_rollback": new_rollback, "requests_to_new": requests_to_new,
        }

        bridge.close()
        bridge = None
        ui_apply(15378)
        restored = True
        bridge = wait_bridge(OLD_URL, transcript)
        final_status = bridge.tool("edit_status", {})
        assert final_status["state"] == "idle", final_status
        report["final_status"] = final_status
        report["transcript"] = transcript
        report["result"] = "PASS"
        return 0
    except Exception as exc:
        report["result"] = "FAIL"
        report["error"] = {"type": type(exc).__name__, "message": str(exc)}
        report["transcript"] = transcript
        return 1
    finally:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if observer is not None:
            observer.close()
        if bridge is not None:
            bridge.close()
        if not restored:
            try:
                ui_apply(15378)
            except Exception as exc:
                report["restore_error"] = {"type": type(exc).__name__, "message": str(exc)}
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    raise SystemExit(main())
