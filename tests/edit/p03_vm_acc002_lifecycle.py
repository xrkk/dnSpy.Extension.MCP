#!/usr/bin/env python3
"""ACC-002/019 real-listener lifecycle acceptance.

Runs inside the dedicated Windows VM. Listener changes are requested through
isolated work-root signal files and are fulfilled by the host-side UI
orchestrator through dnSpy's real Options/Apply path.
"""

from __future__ import annotations

import json
import hashlib
import http.client
import http.server
import socket
import subprocess
import sys
import threading
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15730/mcp"
FIXTURE = r"C:\Tools\dnspy-fix-20260919-t016\x64\fixtures\TestIL.dll"
WORK_ROOT = Path(r"C:\Tools\dnspy-fix-20260919-t016\x64\work\acc002-lifecycle")
REPO_ROOT = Path(__file__).resolve().parents[2]
FAILURES: list[str] = []
PASSES: list[str] = []
SIGNAL_SEQUENCE = 0
JOURNAL: Path | None = None


def configure_isolation(context) -> None:
    global URL, FIXTURE, WORK_ROOT, JOURNAL
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")
    WORK_ROOT = Path(context.work_root)
    JOURNAL = WORK_ROOT / "t016-lifecycle-events.jsonl"


def rid(prefix: str = "t016") -> str:
    return f"{prefix}-{uuid.uuid4().hex}"


def emit(label: str, value) -> None:
    row = json.dumps({"label": label, "value": value}, ensure_ascii=False, sort_keys=True)
    print(row, flush=True)
    if JOURNAL is not None:
        JOURNAL.parent.mkdir(parents=True, exist_ok=True)
        with JOURNAL.open("a", encoding="utf-8") as handle:
            handle.write(row + "\n")


def check(name: str, condition: bool, detail="") -> None:
    print(("PASS " if condition else "FAIL ") + name, flush=True)
    emit(name, {"pass": bool(condition), "detail": detail})
    (PASSES if condition else FAILURES).append(name)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        return client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                return json.loads(text[start:])
            except json.JSONDecodeError:
                pass
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def error_code(envelope: dict) -> str:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def new_client(name: str, url: str | None = None) -> DnSpyClient:
    return DnSpyClient.connect(url or URL, timeout=30, client_name=name)


def close_quietly(value) -> None:
    try:
        value.close()
    except Exception:  # noqa: BLE001
        pass


def same_process_auto_recovery_pass(*, old_session: str, new_session: str,
                                    pid_before: int, pid_after: int,
                                    automatic_raw: dict, automatic_status: dict,
                                    explicit_recovery_used: bool) -> bool:
    return bool(old_session and new_session and old_session != new_session
                and pid_before == pid_after and not explicit_recovery_used
                and "error" not in automatic_raw
                and payload(automatic_status).get("state") == "idle")


def uncertain_request_pass(*, ambiguous_raw: dict, receipts_before: list[dict],
                           receipts_after: list[dict], old_url_raw: dict,
                           new_status: dict, search: dict,
                           new_url_mutations: list[dict]) -> bool:
    return bool("error" in ambiguous_raw and len(receipts_before) == 1
                and receipts_before[0].get("upstream_status") == 200
                and receipts_before[0].get("response_dropped") is True
                and len(receipts_after) == 1 and "error" in old_url_raw
                and payload(new_status).get("state") == "idle"
                and search.get("items") == [] and not new_url_mutations)


def begin_apply(client: DnSpyClient, name: str) -> tuple[str, int, dict]:
    begun = call(client, "edit_begin", {"request_id": rid("begin"), "assembly_name": "TestIL"})
    tx = payload(begun).get("transaction", {})
    transaction_id = str(tx.get("transaction_id", ""))
    revision = int(tx.get("work_revision", 0))
    if not begun.get("ok") or not transaction_id:
        raise RuntimeError("edit_begin failed: " + json.dumps(begun)[:500])
    applied = call(client, "edit_apply", {
        "request_id": rid("apply"), "transaction_id": transaction_id,
        "expected_revision": revision,
        "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
    })
    if not applied.get("ok"):
        raise RuntimeError("edit_apply failed: " + json.dumps(applied)[:500])
    return transaction_id, revision + 1, applied


def commit_name(client: DnSpyClient, name: str, *, fault: bool = False) -> dict:
    transaction_id, revision, _ = begin_apply(client, name)
    reviewed = call(client, "edit_review", {
        "request_id": rid("review"), "transaction_id": transaction_id,
        "expected_revision": revision,
    })
    review = payload(reviewed).get("review", {})
    if fault:
        armed = call(client, "edit_test_storage_fault", {"action": "arm", "stage": "finalize"})
        emit("partial_fault_armed", armed)
    committed = call(client, "edit_commit", {
        "request_id": rid("commit"), "transaction_id": transaction_id,
        "expected_revision": revision, "review_id": review.get("review_id", ""),
        "review_revision": review.get("review_revision", 0), "confirmed_risk_ids": [],
    })
    return committed


def request_listener(action: str, port: int) -> dict:
    global SIGNAL_SEQUENCE
    SIGNAL_SEQUENCE += 1
    signal_dir = WORK_ROOT / "ui-signals"
    signal_dir.mkdir(parents=True, exist_ok=True)
    request = signal_dir / f"request-{SIGNAL_SEQUENCE:02d}.json"
    acknowledgement = signal_dir / f"ack-{SIGNAL_SEQUENCE:02d}.json"
    request.write_text(json.dumps({
        "schema_version": "dnspy.t016.listener-signal.v1", "sequence": SIGNAL_SEQUENCE,
        "action": action, "host": "localhost", "port": port,
        "request_monotonic_ns": time.monotonic_ns(),
    }, indent=2) + "\n", encoding="utf-8")
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        if acknowledgement.is_file():
            value = json.loads(acknowledgement.read_text(encoding="utf-8"))
            emit("listener_ack", value)
            if value.get("result") != "PASS":
                raise RuntimeError("listener action failed: " + json.dumps(value))
            return value
        time.sleep(0.2)
    raise TimeoutError(f"listener acknowledgement timed out: {request}")


def rpc_payload(response: dict) -> dict:
    result = response.get("result") if isinstance(response, dict) else None
    if not isinstance(result, dict):
        return {}
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    for row in result.get("content", []):
        if isinstance(row, dict) and row.get("type") == "text":
            try:
                parsed = json.loads(row.get("text", ""))
            except json.JSONDecodeError:
                continue
            if isinstance(parsed, dict):
                return parsed
    return {}


class StdioBridge:
    def __init__(self, url: str, transcript: list[dict]) -> None:
        self.url = url
        self.transcript = transcript
        self.next_id = 1
        self.process = subprocess.Popen(
            [sys.executable, "-m", "dnspy_mcp.stdio", "--url", url, "--timeout", "20"],
            cwd=str(REPO_ROOT), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, bufsize=1,
        )
        initialized = self.initialize()
        if "error" in initialized:
            raise RuntimeError("stdio initialize failed: " + json.dumps(initialized))

    @property
    def pid(self) -> int:
        return self.process.pid

    def initialize(self) -> dict:
        initialized = self.rpc("initialize", {
            "protocolVersion": "2025-03-26", "capabilities": {},
            "clientInfo": {"name": "t016-stdio", "version": "1"},
        })
        if "error" not in initialized:
            self.notify("notifications/initialized", {})
        return initialized

    def rpc(self, method: str, params) -> dict:
        request_id = self.next_id
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        self.transcript.append({"direction": "request", "url": self.url,
                                "bridge_pid": self.pid, "message": message,
                                "monotonic_ns": time.monotonic_ns()})
        assert self.process.stdin is not None and self.process.stdout is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            stderr = self.process.stderr.read() if self.process.stderr is not None else ""
            raise RuntimeError("stdio bridge exited: " + stderr[-500:])
        response = json.loads(line)
        self.transcript.append({"direction": "response", "url": self.url,
                                "bridge_pid": self.pid, "message": response,
                                "monotonic_ns": time.monotonic_ns()})
        return response

    def notify(self, method: str, params) -> None:
        message = {"jsonrpc": "2.0", "method": method, "params": params}
        self.transcript.append({"direction": "request", "url": self.url,
                                "bridge_pid": self.pid, "message": message,
                                "monotonic_ns": time.monotonic_ns()})
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def tool(self, name: str, arguments: dict) -> tuple[dict, dict]:
        raw = self.rpc("tools/call", {"name": name, "arguments": arguments})
        return raw, rpc_payload(raw)

    def close(self) -> None:
        if self.process.stdin is not None and not self.process.stdin.closed:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            self.process.wait(timeout=10)


class ResponseDropProxy:
    """Loopback HTTP proxy that drops one selected response after upstream processed it."""

    def __init__(self, upstream_url: str) -> None:
        from urllib.parse import urlparse

        parsed = urlparse(upstream_url)
        self.upstream_host = parsed.hostname or "127.0.0.1"
        self.upstream_port = parsed.port or 80
        self.upstream_path = parsed.path or "/mcp"
        self.target_request_id = ""
        self.events: list[dict] = []
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, _format, *_args) -> None:
                return

            def do_POST(self) -> None:  # noqa: N802
                self._forward()

            def do_DELETE(self) -> None:  # noqa: N802
                self._forward()

            def _forward(self) -> None:
                length = int(self.headers.get("Content-Length", "0"))
                body = self.rfile.read(length) if length else b""
                try:
                    message = json.loads(body) if body else {}
                except json.JSONDecodeError:
                    message = {}
                arguments = message.get("params", {}).get("arguments", {}) if isinstance(message, dict) else {}
                request_id = arguments.get("request_id") if isinstance(arguments, dict) else None
                tool = message.get("params", {}).get("name") if isinstance(message, dict) else None
                headers = {key: value for key, value in self.headers.items()
                           if key.casefold() not in {"host", "connection", "content-length"}}
                connection = http.client.HTTPConnection(
                    owner.upstream_host, owner.upstream_port, timeout=30)
                try:
                    connection.request(self.command, owner.upstream_path, body=body, headers=headers)
                    upstream = connection.getresponse()
                    response_body = upstream.read()
                    response_headers = dict(upstream.getheaders())
                    event = {
                        "method": self.command,
                        "tool": tool,
                        "request_id": request_id,
                        "upstream_status": upstream.status,
                        "request_sha256": hashlib.sha256(body).hexdigest(),
                        "response_sha256": hashlib.sha256(response_body).hexdigest(),
                    }
                    try:
                        event["upstream_json"] = json.loads(response_body)
                    except (UnicodeDecodeError, json.JSONDecodeError):
                        event["upstream_json"] = None
                    owner.events.append(event)
                    if (tool == "edit_apply" and request_id
                            and request_id == owner.target_request_id):
                        event["response_dropped"] = True
                        self.close_connection = True
                        try:
                            self.connection.shutdown(socket.SHUT_RDWR)
                        except OSError:
                            pass
                        self.connection.close()
                        return
                    self.send_response(upstream.status, upstream.reason)
                    for key, value in response_headers.items():
                        if key.casefold() not in {"connection", "content-length", "transfer-encoding"}:
                            self.send_header(key, value)
                    self.send_header("Content-Length", str(len(response_body)))
                    self.end_headers()
                    if response_body:
                        self.wfile.write(response_body)
                except OSError as exc:
                    owner.events.append({"method": self.command, "tool": tool,
                                         "request_id": request_id,
                                         "upstream_error": f"{type(exc).__name__}: {exc}"})
                    self.close_connection = True
                    try:
                        self.connection.shutdown(socket.SHUT_RDWR)
                    except OSError:
                        pass
                    self.connection.close()
                finally:
                    connection.close()

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever,
                                       name="t016-response-drop-proxy", daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}/mcp"

    def arm(self, request_id: str) -> None:
        self.target_request_id = request_id

    def target_events(self) -> list[dict]:
        return [row for row in self.events if row.get("tool") == "edit_apply"
                and row.get("request_id") == self.target_request_id]

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=10)


def main() -> int:
    original_url = URL
    original_port = int(original_url.rsplit(":", 1)[1].split("/", 1)[0])
    alternate_port = original_port + 1
    transcript: list[dict] = []
    open_values: list = []
    current_port = original_port
    try:
        bootstrap = new_client("t016-bootstrap")
        opened = call(bootstrap, "open_files", {"paths": [FIXTURE]})
        check("K0 fixture opened", int(opened.get("loaded_count", 0)) == 1
              and int(opened.get("failed_count", 0)) == 0, opened)
        close_quietly(bootstrap)

        # K1a: authoritative owner DELETE rolls back its uncommitted private workspace.
        owner = new_client("t016-delete-owner")
        owner_session = str(owner.session_id)
        tx, revision, _ = begin_apply(owner, "T016_DeletePending")
        deleted = owner.close()
        observer = new_client("t016-delete-observer")
        after_delete = payload(call(observer, "edit_status", {}))
        stale_delete = DnSpyClient(original_url, timeout=10, client_name="t016-stale-delete")
        stale_delete.session_id = owner_session
        stale_delete.protocol_version = "2025-03-26"
        stale_result = call(stale_delete, "edit_apply", {
            "request_id": rid("stale-delete"), "transaction_id": tx,
            "expected_revision": revision,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_DeleteMustFail"},
        })
        check("K1 DELETE rolls back uncommitted transaction",
              getattr(deleted, "status", None) == 200 and after_delete.get("state") == "idle",
              {"session": owner_session, "delete_status": getattr(deleted, "status", None),
               "after": after_delete})
        check("K1 deleted session cannot continue old transaction",
              not stale_result.get("ok"), stale_result)
        close_quietly(stale_delete)
        close_quietly(observer)

        # K1b: real monotonic wall-clock idle, with no owner request during the wait.
        idle_owner = new_client("t016-real-idle-owner")
        idle_tx, idle_revision, idle_apply = begin_apply(idle_owner, "T016_RealIdlePending")
        server_last_activity = payload(idle_apply).get("transaction", {}).get("last_activity_monotonic_ms")
        wait_start_ns = time.monotonic_ns()
        emit("real_idle_wait_start", {
            "owner_session": idle_owner.session_id, "transaction_id": idle_tx,
            "revision": idle_revision, "server_last_activity_monotonic_ms": server_last_activity,
            "local_monotonic_ns": wait_start_ns, "wait_seconds": 601.5,
            "owner_business_requests_during_wait": 0,
        })
        time.sleep(601.5)
        wait_end_ns = time.monotonic_ns()
        idle_observer = new_client("t016-real-idle-observer")
        after_idle = payload(call(idle_observer, "edit_status", {}))
        expired_args = {
            "request_id": rid("expired"), "transaction_id": idle_tx,
            "expected_revision": idle_revision,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_ExpiredMustFail"},
        }
        expired_owner = call(idle_owner, "edit_apply", expired_args)
        if error_code(expired_owner) == "DRIVER_TRANSPORT":
            expired_args["request_id"] = rid("expired-retry")
            expired_owner_retry = call(idle_owner, "edit_apply", expired_args)
        else:
            expired_owner_retry = expired_owner
        elapsed_ms = (wait_end_ns - wait_start_ns) / 1_000_000
        emit("real_idle_wait_end", {"local_monotonic_ns": wait_end_ns, "elapsed_ms": elapsed_ms,
                                     "status": after_idle, "old_owner_response": expired_owner,
                                     "old_owner_retry": expired_owner_retry})
        check("K1 real 600-second idle rolls back", elapsed_ms >= 600000
              and after_idle.get("state") == "idle"
              and error_code(expired_owner_retry) == "EDIT_TRANSACTION_NOT_FOUND",
              {"elapsed_ms": elapsed_ms, "status": after_idle,
               "old_owner": expired_owner, "old_owner_retry": expired_owner_retry})
        close_quietly(idle_owner)
        close_quietly(idle_observer)

        # Persist one committed checkpoint before listener lifecycle testing.
        seed = new_client("t016-seed")
        committed = commit_name(seed, "T016_CommittedSeed")
        lineage_id = str(payload(committed).get("history", {}).get("lineage_id", ""))
        checkpoint_id = str(payload(committed).get("checkpoint", {}).get("checkpoint_id", ""))
        check("K3 committed seed created", bool(committed.get("ok")) and bool(lineage_id) and bool(checkpoint_id), committed)
        close_quietly(seed)

        # K2/K3: 16 live sessions include one real stdio bridge. Same-URL Apply
        # restarts the listener without restarting the dnSpy process.
        bridge = StdioBridge(original_url, transcript)
        _, bridge_context = bridge.tool("debug_test_transport", {"p01_action": "snapshot"})
        bridge_session = str(payload(bridge_context).get("authoritative_session_id", ""))
        sessions = [new_client(f"t016-quota-before-{i}") for i in range(15)]
        open_values.extend(sessions)
        old_direct_sessions = {str(item.session_id) for item in sessions}
        overflow_refused = False
        try:
            overflow = new_client("t016-quota-before-overflow")
            close_quietly(overflow)
        except Exception:  # noqa: BLE001
            overflow_refused = True
        restart_owner = sessions[0]
        restart_owner_session = str(restart_owner.session_id)
        restart_tx, restart_revision, _ = begin_apply(restart_owner, "T016_ListenerPending")
        ack1 = request_listener("restart_same_url", original_port)
        stale_after_restart = call(restart_owner, "edit_apply", {
            "request_id": rid("stale-listener"), "transaction_id": restart_tx,
            "expected_revision": restart_revision,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_ListenerMustFail"},
        })
        bridge_pid = bridge.pid
        automatic_raw, automatic_status = bridge.tool("edit_status", {})
        explicit_recovery_used = "error" in automatic_raw
        explicit_initialize = None
        if explicit_recovery_used:
            explicit_initialize = bridge.initialize()
        for item in sessions:
            close_quietly(item)
        open_values.clear()

        _, new_context = bridge.tool("debug_test_transport", {"p01_action": "snapshot"})
        new_bridge_session = str(payload(new_context).get("authoritative_session_id", ""))
        _, status_via_bridge = bridge.tool("edit_status", {})
        history_raw, history_via_bridge = bridge.tool("edit_history", {
            "lineage_id": lineage_id, "page_size": 100,
        })
        checkpoints = payload(history_via_bridge).get("checkpoints", [])
        committed_visible = any(isinstance(row, dict) and row.get("checkpoint_id") == checkpoint_id
                                for row in checkpoints)
        lifecycle = payload(new_context).get("lifecycle", {})
        events = lifecycle.get("events", []) if isinstance(lifecycle, dict) else []
        stopped_ids = {str(row.get("session_id")) for row in events
                       if isinstance(row, dict) and row.get("reason") == "listener_stop"}
        listener_zero = any(isinstance(row, dict) and row.get("reason") == "listener_stop"
                            and row.get("active_session_count_after_removal") == 0 for row in events)
        check("K1 listener restart rolls back uncommitted transaction",
              payload(status_via_bridge).get("state") == "idle", status_via_bridge)
        check("K2 same-process stdio bridge automatically reinitializes",
              same_process_auto_recovery_pass(
                  old_session=bridge_session, new_session=new_bridge_session,
                  pid_before=bridge_pid, pid_after=bridge.pid,
                  automatic_raw=automatic_raw, automatic_status=automatic_status,
                  explicit_recovery_used=explicit_recovery_used)
              and history_raw.get("result") is not None,
              {"old_session": bridge_session, "new_session": new_bridge_session,
               "bridge_pid_before": bridge_pid, "bridge_pid_after": bridge.pid,
               "automatic_read": automatic_raw,
               "explicit_recovery_used": explicit_recovery_used,
               "explicit_initialize": explicit_initialize})
        check("K3 old sessions invalid and listener stop drains to zero",
              overflow_refused and not stale_after_restart.get("ok")
              and old_direct_sessions.issubset(stopped_ids)
              and restart_owner_session in stopped_ids and bridge_session in stopped_ids and listener_zero,
              {"overflow_refused": overflow_refused, "stale": stale_after_restart,
               "restart_owner_session": restart_owner_session, "bridge_session": bridge_session,
               "expected_old_sessions": sorted(old_direct_sessions | {bridge_session}),
               "listener_stop_sessions": sorted(stopped_ids), "ack": ack1})
        check("K3 committed history survives listener stop", committed_visible,
              {"lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
               "history": history_via_bridge})

        # With the new bridge occupying one session, 15 more must be admitted;
        # the 17th must be rejected. This proves the old 16 slots were released.
        new_sessions = [new_client(f"t016-quota-after-{i}") for i in range(15)]
        open_values.extend(new_sessions)
        after_overflow_refused = False
        try:
            overflow = new_client("t016-quota-after-overflow")
            close_quietly(overflow)
        except Exception:  # noqa: BLE001
            after_overflow_refused = True
        check("K3 all 16 session slots reusable after listener stop",
              len(new_sessions) == 15 and after_overflow_refused,
              {"stdio_sessions": 1, "direct_sessions": len(new_sessions),
               "seventeenth_refused": after_overflow_refused})
        for item in new_sessions:
            close_quietly(item)
        open_values.clear()
        bridge.close()

        # K3: a live-linearized partial is process state and remains queryable
        # across a second real listener Stop/Start.
        partial_owner = new_client("t016-partial-owner")
        partial = commit_name(partial_owner, "T016_Partial", fault=True)
        partial_before = payload(call(partial_owner, "edit_status", {}))
        recovery_before = partial_before.get("recovery")
        partial_owner_session = str(partial_owner.session_id)
        check("K3 partial seeded", error_code(partial) == "EDIT_CHECKPOINT_COMMIT_FAILED"
              and partial_before.get("state") == "committed_without_checkpoint"
              and isinstance(recovery_before, dict), {"commit": partial, "status": partial_before})
        ack2 = request_listener("restart_same_url", original_port)
        stale_partial = call(partial_owner, "edit_status", {})
        close_quietly(partial_owner)
        partial_observer = new_client("t016-partial-observer")
        partial_after = payload(call(partial_observer, "edit_status", {}))
        history_after = payload(call(partial_observer, "edit_history", {
            "lineage_id": lineage_id, "page_size": 100,
        }))
        recovery_after = partial_after.get("recovery")
        same_recovery = (isinstance(recovery_before, dict) and isinstance(recovery_after, dict)
                         and recovery_before.get("recovery_id") == recovery_after.get("recovery_id"))
        check("K3 partial and committed records survive listener stop",
              not stale_partial.get("ok") and partial_after.get("state") == "committed_without_checkpoint"
              and same_recovery and any(isinstance(row, dict) and row.get("checkpoint_id") == checkpoint_id
                                        for row in history_after.get("checkpoints", [])),
              {"owner_session": partial_owner_session, "stale": stale_partial,
               "before": partial_before, "after": partial_after, "ack": ack2})
        recovered = call(partial_observer, "edit_recover", {
            "request_id": rid("recover"), "recovery_id": recovery_after.get("recovery_id", ""),
            "action": "retry_checkpoint",
        })
        check("K3 fresh session resolves retained partial", bool(recovered.get("ok")), recovered)
        close_quietly(partial_observer)

        # K2: a port change requires an explicit new URL. The old bridge is not
        # redirected and the uncommitted mutation is never replayed.
        response_proxy = ResponseDropProxy(original_url)
        port_bridge = StdioBridge(response_proxy.url, transcript)
        uncertain_bridge_pid = port_bridge.pid
        mutation_id = rid("port-mutation")
        _, port_begin = port_bridge.tool("edit_begin", {
            "request_id": rid("port-begin"), "assembly_name": "TestIL",
        })
        port_tx = str(payload(port_begin).get("transaction", {}).get("transaction_id", ""))
        response_proxy.arm(mutation_id)
        old_apply_raw, old_apply = port_bridge.tool("edit_apply", {
            "request_id": mutation_id, "transaction_id": port_tx, "expected_revision": 0,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"},
                          "name": "T016_PortMustNotReplay"},
        })
        receipt_before_change = response_proxy.target_events()
        ack3 = request_listener("change_port", alternate_port)
        current_port = alternate_port
        old_url_raw, _ = port_bridge.tool("edit_status", {})
        receipt_after_change = response_proxy.target_events()
        port_bridge.close()
        response_proxy.close()
        new_url = f"http://127.0.0.1:{alternate_port}/mcp"
        new_port_bridge = StdioBridge(new_url, transcript)
        _, new_port_status = new_port_bridge.tool("edit_status", {})
        _, search = new_port_bridge.tool("search_types", {
            "query": "T016_PortMustNotReplay", "assembly_name": "TestIL", "page_size": 20,
        })
        new_url_mutations = [row for row in transcript if row.get("direction") == "request"
                             and row.get("url") == new_url
                             and row.get("message", {}).get("method") == "tools/call"
                             and row.get("message", {}).get("params", {}).get("name") == "edit_apply"]
        check("K2 port change requires explicit URL and does not replay",
              uncertain_request_pass(
                  ambiguous_raw=old_apply_raw, receipts_before=receipt_before_change,
                  receipts_after=receipt_after_change, old_url_raw=old_url_raw,
                  new_status=new_port_status, search=search,
                  new_url_mutations=new_url_mutations),
              {"old_url": original_url, "new_url": new_url, "old_url_response": old_url_raw,
               "new_status": new_port_status, "search": search,
               "new_url_mutation_requests": new_url_mutations, "mutation_id": mutation_id,
               "uncertain_bridge_pid": uncertain_bridge_pid,
               "bridge_configured_url": response_proxy.url,
               "http_receipt_before_change": receipt_before_change,
               "http_receipt_after_change": receipt_after_change,
               "ambiguous_client_response": old_apply_raw, "ack": ack3})
        new_port_bridge.close()
        ack4 = request_listener("restore_port", original_port)
        current_port = original_port
        final = new_client("t016-final", original_url)
        final_status = payload(call(final, "edit_status", {}))
        check("K2 original URL explicitly restored", final_status.get("state") == "idle",
              {"status": final_status, "ack": ack4})
        close_quietly(final)
        emit("stdio_transcript", transcript)
    except Exception as ex:  # noqa: BLE001
        FAILURES.append("driver_exception")
        emit("driver_exception", {"type": type(ex).__name__, "message": str(ex),
                                  "current_port": current_port})
    finally:
        for value in open_values:
            close_quietly(value)

    emit("summary", {"status": "PASS" if not FAILURES else "FAIL",
                     "passes": PASSES, "failures": FAILURES,
                     "original_url": original_url, "final_port": current_port})
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
