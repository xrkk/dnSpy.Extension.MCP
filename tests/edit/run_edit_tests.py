#!/usr/bin/env python3
"""P01 structured-edit acceptance driver. All dnSpy traffic uses dnspy_mcp clients."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Mapping

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from dnspy_mcp import (  # noqa: E402
    DnSpyClient,
    DnSpyConnectionError,
    DnSpyHttpError,
    LegacySseClient,
)


def rpc_tool(client: DnSpyClient, name: str, arguments: Mapping[str, Any] | None = None) -> dict[str, Any]:
    result = client.request("tools/call", {"name": name, "arguments": dict(arguments or {})})
    if not isinstance(result, dict):
        raise AssertionError(f"{name}: MCP result is not an object")
    payload = result.get("structuredContent")
    if payload is None:
        for item in result.get("content", []):
            if isinstance(item, dict) and item.get("type") == "text":
                payload = json.loads(item["text"])
                break
    if not isinstance(payload, dict):
        raise AssertionError(f"{name}: tool payload is not an object")
    return payload


def require_ok(payload: dict[str, Any], label: str) -> dict[str, Any]:
    if payload.get("ok") is not True or not isinstance(payload.get("result"), dict):
        raise AssertionError(f"{label}: expected ok=true, got {payload}")
    return payload["result"]


def error_code(payload: dict[str, Any]) -> str | None:
    error = payload.get("error")
    return error.get("code") if isinstance(error, dict) and isinstance(error.get("code"), str) else None


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class AcceptanceRun:
    def __init__(self, case_id: str, base_url: str, artifact_root: Path) -> None:
        self.case_id = case_id
        self.base_url = base_url
        self.run_id = time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8]
        self.run_root = artifact_root / "edit-tests" / self.run_id
        self.case_root = self.run_root / case_id
        self.case_root.mkdir(parents=True, exist_ok=False)
        self.responses: list[str] = []
        self.cleanup: dict[str, Any] = {"debug_idle": False, "clients_closed": False}
        self.outcome_checks: list[dict[str, Any]] = []

    def save(self, name: str, value: Any) -> str:
        path = self.case_root / name
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        self.responses.append(str(path))
        return str(path)

    def summary(self, status: str, reason: str | None, metadata: dict[str, Any]) -> None:
        case = {
            "suite": "structured-edit",
            "case_id": self.case_id,
            "acc_id": self.case_id.replace("EDIT-", ""),
            "status": status,
            "action_response_paths": self.responses,
            "artifact_identity": metadata,
            "cleanup": self.cleanup,
            "reason": reason,
        }
        if self.outcome_checks:
            case["outcome_checks"] = self.outcome_checks
        aggregate = [status] + [item["status"] for item in self.outcome_checks]
        overall = "fail" if "fail" in aggregate else "blocked" if "blocked" in aggregate else "pass"
        summary = {"suite": "structured-edit", "run_id": self.run_id, "status": overall, "cases": [case]}
        (self.run_root / "summary.json").write_text(
            json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )
        print(json.dumps({"status": overall, "summary": str(self.run_root / "summary.json")}, ensure_ascii=False))


def connect(url: str, name: str = "dnspy-p01-edit-tests") -> DnSpyClient:
    return DnSpyClient.connect(url, client_name=name, timeout=60)


def tool_names(client: DnSpyClient) -> set[str]:
    return {str(tool.get("name")) for tool in client.iter_tools()}


def env_probe(client: DnSpyClient, action: str, **kwargs: Any) -> dict[str, Any]:
    args = {"p01_action": action, **kwargs}
    return require_ok(rpc_tool(client, "debug_test_environment", args), f"environment:{action}")


def inject(client: DnSpyClient, classification: str) -> dict[str, Any]:
    fixtures = {
        "vmware": {"manufacturer": "VMware, Inc.", "product_name": "VMware Virtual Platform", "bios_vendor": "Phoenix"},
        "virtualbox": {"manufacturer": "innotek GmbH", "product_name": "VirtualBox", "bios_vendor": "Oracle Corporation"},
        "physical": {"manufacturer": "Dell Inc.", "product_name": "Precision", "bios_vendor": "Dell Inc."},
        "unknown": {"read_failure": True},
    }
    return env_probe(client, "inject_signals", **fixtures[classification])


def capabilities(client: DnSpyClient) -> dict[str, Any]:
    return require_ok(rpc_tool(client, "debug_capabilities"), "debug_capabilities")


def status_with_context(client: DnSpyClient, session_id: str | None = None) -> dict[str, Any]:
    arguments = {"session_id": session_id} if session_id else None
    payload = rpc_tool(client, "debug_status", arguments)
    result = require_ok(payload, "debug_status")
    context = payload.get("debug_context")
    if not isinstance(context, dict):
        raise AssertionError("debug_status omitted debug_context")
    return {**result, "generation": int(context["generation"]),
            "pause_epoch": int(context["pause_epoch"]),
            "event_cursor": int(context["event_cursor"])}


def compile_fixture(architecture: str) -> Path:
    # Existing debug acceptance persists C:\Tools\MefCheck as AllowedSampleRoot.
    # Keep P01 executables under the same accepted root so test order cannot turn
    # a legitimate launch into a path-policy TARGET_MISMATCH.
    work = Path(r"C:\Tools\MefCheck")
    work.mkdir(parents=True, exist_ok=True)
    # A failed debugger run can retain an executable lease. Give every case an
    # isolated fixture identity; the orchestrator removes these test-only files
    # before the next full run.
    output = work / f"P01Fixture-{architecture}-{uuid.uuid4().hex[:8]}.exe"
    csc = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe") if architecture == "x64" \
        else Path(r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe")
    subprocess.run([str(csc), "/nologo", f"/platform:{architecture}", "/optimize+", f"/out:{output}",
                    str(ROOT / "tests/edit/fixtures/P01Fixture.cs")], check=True)
    return output


def launch(client: DnSpyClient, fixture: Path, architecture: str, break_kind: str = "entry") -> dict[str, Any]:
    return rpc_tool(client, "debug_launch", {
        "request_id": str(uuid.uuid4()), "target_path": str(fixture), "expected_sha256": sha256(fixture),
        "launch_mode": "net48-exe", "architecture": architecture, "break_kind": break_kind,
    })


def terminate_if_active(client: DnSpyClient) -> None:
    try:
        status = status_with_context(client)
        sid = status.get("active_session_id")
        if sid:
            rpc_tool(client, "debug_terminate", {
                "session_id": sid, "generation": int(status["generation"]), "request_id": str(uuid.uuid4())
            })
            time.sleep(0.5)
    except Exception:
        pass


def verify_file_identity(artifact_result: dict[str, Any]) -> dict[str, Any]:
    artifact = Path(str(artifact_result["path"]))
    manifest = Path(str(artifact_result["manifest_path"]))
    if not artifact.is_file() or not manifest.is_file():
        raise AssertionError(f"dump artifact/manifest missing: {artifact}, {manifest}")
    actual = sha256(artifact)
    expected = str(artifact_result["sha256"]).lower()
    manifest_data = json.loads(manifest.read_text(encoding="utf-8"))
    manifest_sha = str(manifest_data.get("sha256", "")).lower()
    if actual != expected or manifest_sha != expected or artifact.stat().st_size != int(artifact_result["size"]):
        raise AssertionError("dump response/file/manifest identity mismatch")
    return {"artifact_path": str(artifact), "manifest_path": str(manifest), "sha256": actual,
            "length": artifact.stat().st_size}


def case_017(run: AcceptanceRun, client: DnSpyClient) -> dict[str, Any]:
    names = tool_names(client)
    if "debug_test_environment" in names:
        raise AssertionError("test environment probe must not be advertised")
    real = capabilities(client)["execution_environment"]
    if real["classification"] != "vmware" or not real["execution_allowed"]:
        raise AssertionError(f"real Win10VM did not classify as allowed VMware: {real}")
    matrix: dict[str, Any] = {"real": real}
    for classification, allowed in (("vmware", True), ("virtualbox", True), ("physical", False), ("unknown", False)):
        injected = inject(client, classification)["execution_environment"]
        spoofed = env_probe(client, "snapshot", local_process_override_active=True)["execution_environment"]
        if injected["classification"] != classification or bool(injected["execution_allowed"]) != allowed:
            raise AssertionError(f"classification matrix failed for {classification}: {injected}")
        if spoofed["local_process_override_active"]:
            raise AssertionError("MCP arguments changed the local-process override")
        matrix[classification] = injected
    inject(client, "physical")
    run.save("classification-matrix.json", matrix)
    # UI automation is the only permitted writer of the process override.
    apply_ui_settings("true")
    time.sleep(1.0)
    client = reconnect_after_listener_restart(client, run.base_url)
    enabled = capabilities(client)["execution_environment"]
    if not enabled["local_process_override_active"] or not enabled["execution_allowed"]:
        raise AssertionError(f"local UI override was not applied: {enabled}")
    apply_ui_settings("false")
    time.sleep(1.0)
    client = reconnect_after_listener_restart(client, run.base_url)
    disabled = capabilities(client)["execution_environment"]
    if disabled["local_process_override_active"] or disabled["execution_allowed"]:
        raise AssertionError(f"local UI override did not clear: {disabled}")
    env_probe(client, "clear_signals")
    inject(client, "physical")
    apply_ui_settings("true")
    time.sleep(1.0)
    client = reconnect_after_listener_restart(client, run.base_url)
    if not capabilities(client)["execution_environment"]["local_process_override_active"]:
        raise AssertionError("pre-restart override activation failed")
    client = restart_dnspy_process(client, run.base_url)
    inject(client, "physical")
    after_process_restart = capabilities(client)["execution_environment"]
    if after_process_restart["local_process_override_active"] or after_process_restart["execution_allowed"]:
        raise AssertionError(f"process restart did not reset local override: {after_process_restart}")
    env_probe(client, "clear_signals")
    run.cleanup["debug_idle"] = require_ok(rpc_tool(client, "debug_status"), "final idle")["state"] == "idle"
    client.close()
    run.cleanup["clients_closed"] = True
    run.save("ui-override.json", {"enabled": enabled, "disabled": disabled,
                                  "after_process_restart": after_process_restart})
    return {"classification": real["classification"], "detection_source": real["detection_source"]}


def reconnect_after_listener_restart(old: DnSpyClient, url: str) -> DnSpyClient:
    try:
        old.close()
    except Exception:
        pass
    deadline = time.time() + 20
    last: Exception | None = None
    while time.time() < deadline:
        try:
            return connect(url, "dnspy-p01-reconnect")
        except Exception as exc:
            last = exc
            time.sleep(0.25)
    raise DnSpyConnectionError(f"listener did not recover: {last}")


def apply_ui_settings(enable: str, host: str = "") -> None:
    command = [sys.executable, str(ROOT / "tests/edit/ui_apply_settings.py"), "--enable", enable]
    if host:
        command.extend(["--host", host])
    subprocess.run(command, check=True)


def restart_dnspy_process(old: DnSpyClient, url: str) -> DnSpyClient:
    try:
        old.close()
    except Exception:
        pass
    command = (
        "$p=Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1; "
        "if(-not $p){throw 'dnSpy process not found'}; "
        "$exe=$p.Path; $null=$p.CloseMainWindow(); "
        "if(-not $p.WaitForExit(15000)){Stop-Process -Id $p.Id -Force; $p.WaitForExit()}; "
        "$env:DNMCP_TEST='1'; Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", command], check=True)
    deadline = time.time() + 30
    last: Exception | None = None
    while time.time() < deadline:
        try:
            return connect(url, "dnspy-p01-process-restart")
        except Exception as exc:
            last = exc
            time.sleep(0.5)
    raise DnSpyConnectionError(f"dnSpy did not recover after process restart: {last}")


def case_027(run: AcceptanceRun, client: DnSpyClient) -> dict[str, Any]:
    cap = capabilities(client)
    architecture = str(cap["host_architecture"])
    fixture = compile_fixture(architecture)
    launched = require_ok(launch(client, fixture, architecture), "dump launch")
    sid, generation = str(launched["session_id"]), int(launched["generation"])

    def paused_status() -> dict[str, Any]:
        status = status_with_context(client, sid)
        if status["state"] != "paused":
            paused_payload = rpc_tool(client, "debug_pause", {
                "session_id": sid, "generation": int(status["generation"]), "request_id": str(uuid.uuid4())
            })
            paused_context = paused_payload.get("debug_context")
            already_paused = (
                error_code(paused_payload) == "INVALID_STATE"
                and isinstance(paused_context, dict)
                and paused_context.get("state") == "paused"
            )
            if not already_paused:
                require_ok(paused_payload, "pause")
            status = status_with_context(client, sid)
        return status

    def modules(status: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
        def aliases(item: dict[str, Any]) -> set[str]:
            name = str(item.get("name") or "")
            path = str(item.get("path") or "")
            values = {name.casefold(), Path(path).name.casefold()}
            values.update(Path(value).stem.casefold() for value in tuple(values) if value)
            return values
        target_aliases = {fixture.name.casefold(), fixture.stem.casefold()}
        deadline = time.time() + 15
        observed: list[dict[str, Any]] = []
        current = status
        while time.time() < deadline:
            if current["state"] != "paused":
                current = paused_status()
            listed = require_ok(rpc_tool(client, "debug_list_modules", {
                "session_id": sid, "generation": int(current["generation"]),
                "pause_epoch": int(current["pause_epoch"])
            }), "list modules")
            items = listed["items"]
            target = next((item for item in items if aliases(item) & target_aliases), None)
            core = next((item for item in items if aliases(item) & {"mscorlib", "mscorlib.dll"}), None)
            observed = [{"name": item.get("name"), "path": item.get("path")} for item in items]
            if target is not None and core is not None:
                return current, target, core
            # An initial runtime pause can precede the target module load. The
            # module cannot arrive while execution is stopped, so advance for a
            # bounded slice and reacquire a fresh pause/context instead of
            # waiting forever on the same frozen module set.
            if target is None and current["state"] == "paused":
                continued = rpc_tool(client, "debug_continue", {
                    "session_id": sid, "generation": int(current["generation"]),
                    "pause_epoch": int(current["pause_epoch"]), "request_id": str(uuid.uuid4())
                })
                # Entry/process pauses can be transient. If the engine resumed
                # between list_modules and this request, INVALID_STATE with a
                # running context is equivalent to the requested outcome.
                continued_context = continued.get("debug_context")
                already_running = (
                    error_code(continued) == "INVALID_STATE"
                    and isinstance(continued_context, dict)
                    and continued_context.get("state") == "running"
                )
                if not already_running:
                    require_ok(continued, "continue until target module load")
                time.sleep(0.1)
            else:
                time.sleep(0.25)
            current = status_with_context(client, sid)
        raise AssertionError(f"required dump modules not found: target={fixture.name}; observed={observed}")

    dumps: list[dict[str, Any]] = []
    status1 = paused_status()
    status1, old_target, old_core = modules(status1)

    def dump(status: dict[str, Any], module: dict[str, Any], label: str) -> None:
        result = require_ok(rpc_tool(client, "debug_dump_module", {
            "session_id": sid, "generation": int(status["generation"]), "pause_epoch": int(status["pause_epoch"]),
            "module_handle": module["module_handle"], "request_id": str(uuid.uuid4()), "relative_name": label + ".bin",
        }), label)
        artifact = result.get("artifact")
        if not isinstance(artifact, dict):
            raise AssertionError(f"{label}: dump result omitted artifact object: {result}")
        dumps.append({"label": label, **verify_file_identity(artifact)})

    dump(status1, old_target, "g1-e1-target")
    dump(status1, old_core, "g1-e1-mscorlib")
    require_ok(rpc_tool(client, "debug_continue", {
        "session_id": sid, "generation": int(status1["generation"]), "pause_epoch": int(status1["pause_epoch"]),
        "request_id": str(uuid.uuid4())}), "continue")
    time.sleep(0.3)
    status2 = paused_status()
    status2, target2, core2 = modules(status2)
    dump(status2, target2, "g1-e2-target")
    dump(status2, core2, "g1-e2-mscorlib")
    restarted = require_ok(rpc_tool(client, "debug_restart", {
        "session_id": sid, "generation": int(status2["generation"]), "request_id": str(uuid.uuid4())
    }), "restart")
    status3 = paused_status()
    if int(status3["generation"]) <= generation:
        raise AssertionError("restart did not increment generation")
    status3, target3, core3 = modules(status3)
    dump(status3, target3, "g2-target")
    dump(status3, core3, "g2-mscorlib")
    stale = rpc_tool(client, "debug_dump_module", {
        "session_id": sid, "generation": int(status3["generation"]), "pause_epoch": int(status3["pause_epoch"]),
        "module_handle": old_target["module_handle"], "request_id": str(uuid.uuid4()), "relative_name": "stale.bin",
    })
    if error_code(stale) not in {"STALE_HANDLE", "TARGET_MISMATCH", "NOT_FOUND"}:
        raise AssertionError(f"old module handle was not rejected: {stale}")
    require_ok(rpc_tool(client, "debug_terminate", {
        "session_id": sid, "generation": int(status3["generation"]), "request_id": str(uuid.uuid4())
    }), "terminate")
    run.cleanup["debug_idle"] = require_ok(rpc_tool(client, "debug_status"), "final status")["state"] == "idle"
    run.save("dump-matrix.json", {"fixture": str(fixture), "fixture_sha256": sha256(fixture),
                                  "initial_generation": generation, "final_generation": status3["generation"],
                                  "dumps": dumps, "stale_error": error_code(stale)})
    return {"fixture_path": str(fixture), "fixture_sha256": sha256(fixture), "dumps": dumps}


def case_030(run: AcceptanceRun, client: DnSpyClient) -> dict[str, Any]:
    # H01: three real transport paths, including an args owner spoof.
    stream_snapshot = require_ok(rpc_tool(client, "debug_test_transport", {
        "p01_action": "snapshot", "owner_session_id": "forged", "session_id": "forged-debug"
    }), "stream context")
    plain = DnSpyClient(run.base_url, client_name="dnspy-p01-plain")
    plain_snapshot = require_ok(rpc_tool(plain, "debug_test_transport", {
        "p01_action": "snapshot", "owner_session_id": "forged"
    }), "plain context")
    legacy = LegacySseClient.connect(run.base_url, client_name="dnspy-p01-legacy", protocol_version="2024-11-05")
    legacy_snapshot = require_ok(rpc_tool(legacy, "debug_test_transport", {
        "p01_action": "snapshot", "owner_session_id": "forged"
    }), "legacy context")
    unknown = DnSpyClient(run.base_url)
    unknown.session_id = "unknown-p01-session"
    unknown_status = None
    try:
        unknown.request("tools/list", {})
    except DnSpyHttpError as exc:
        unknown_status = exc.response.status
    h01_ok = (
        stream_snapshot["transport_kind"] == "streamable_http"
        and stream_snapshot["authoritative_session_id"] == client.session_id
        and stream_snapshot["can_own_edit_transaction"] is True
        and legacy_snapshot["transport_kind"] == "legacy_sse"
        and legacy_snapshot["authoritative_session_id"] == legacy.session_id
        and legacy_snapshot["can_own_edit_transaction"] is True
        and plain_snapshot["transport_kind"] == "compatibility_plain_http"
        and plain_snapshot["authoritative_session_id"] is None
        and plain_snapshot["can_own_edit_transaction"] is False
        and unknown_status == 404
    )
    h01_path = run.save("out-001-h01.json", {"streamable": stream_snapshot, "legacy": legacy_snapshot,
                                              "plain": plain_snapshot, "unknown_header_status": unknown_status})
    run.outcome_checks.append({"id": "OUT-001-H01", "status": "pass" if h01_ok else "fail",
                               "evidence_path": "out-001-h01.json", "reason": None if h01_ok else "authoritative context mismatch"})

    # H02: DELETE and disconnect are real client actions; Apply in the UI performs the real Stop.
    require_ok(rpc_tool(client, "debug_test_transport", {"p01_action": "reset"}), "reset lifecycle")
    observer = connect(run.base_url, "dnspy-p01-observer")
    victim = connect(run.base_url, "dnspy-p01-delete-victim")
    victim_id = victim.session_id
    require_ok(rpc_tool(observer, "debug_test_transport", {"p01_action": "arm_observer_fault"}), "arm observer fault")
    delete_statuses = [victim.raw_request("DELETE").status, victim.raw_request("DELETE").status]
    victim.session_id = None
    legacy_id = legacy.session_id
    legacy.close()
    # HttpListener observes a peer-side SSE close when the next keepalive write
    # fails. Poll across the fixed 15-second server interval instead of assuming
    # the TCP close is synchronously visible.
    disconnect_deadline = time.time() + 20
    while True:
        pre_stop = require_ok(rpc_tool(observer, "debug_test_transport", {"p01_action": "snapshot"}),
                              "pre-stop lifecycle")
        if any(event["transport_kind"] == "legacy_sse"
               and event["session_id"] == legacy_id
               and event["reason"] == "legacy_disconnect"
               for event in pre_stop["lifecycle"]["events"]):
            break
        if time.time() >= disconnect_deadline:
            break
        time.sleep(0.5)
    observer_id = observer.session_id
    apply_ui_settings("false")
    time.sleep(1.0)
    observer = reconnect_after_listener_restart(observer, run.base_url)
    post_stop = require_ok(rpc_tool(observer, "debug_test_transport", {"p01_action": "snapshot"}), "post-stop lifecycle")
    events = post_stop["lifecycle"]["events"]
    event_keys = [(event["transport_kind"], event["session_id"], event["reason"]) for event in events]
    h02_ok = (
        delete_statuses == [200, 200]
        and event_keys.count(("streamable_http", victim_id, "client_delete")) == 1
        and event_keys.count(("legacy_sse", legacy_id, "legacy_disconnect")) == 1
        and event_keys.count(("streamable_http", observer_id, "listener_stop")) == 1
        and post_stop["lifecycle"]["isolated_observer_faults"] == 1
        and all(event["active_session_count_after_removal"] >= 0 for event in events)
    )
    h02_path = run.save("out-001-h02.json", {"delete_statuses": delete_statuses,
                                              "before_listener_stop": pre_stop,
                                              "after_listener_stop": post_stop})
    run.outcome_checks.append({"id": "OUT-001-H02", "status": "pass" if h02_ok else "fail",
                               "evidence_path": "out-001-h02.json", "reason": None if h02_ok else "lifecycle mismatch"})

    # ACC-030: identical four-class matrix and real pre-side-effect launch/restart checks.
    cap = capabilities(observer)
    architecture = str(cap["host_architecture"])
    fixture = compile_fixture(architecture)
    matrix: dict[str, Any] = {}
    for classification, allowed in (("vmware", True), ("virtualbox", True), ("physical", False), ("unknown", False)):
        inject(observer, classification)
        row: dict[str, Any] = {}
        for entry in ("debug_launch", "debug_restart", "edit_dynamic_validation"):
            result = env_probe(observer, "evaluate", entry_point=entry)
            row[entry] = {"allowed": result["allowed"], "error_code": result.get("error_code")}
            if bool(result["allowed"]) != allowed:
                raise AssertionError(f"gate matrix mismatch: {classification}/{entry}: {result}")
        matrix[classification] = row

    inject(observer, "physical")
    before = require_ok(rpc_tool(observer, "debug_test_spy", {}), "spy before physical launch")["counters"]
    rejected_launch = launch(observer, fixture, architecture)
    after = require_ok(rpc_tool(observer, "debug_test_spy", {}), "spy after physical launch")["counters"]
    if error_code(rejected_launch) != "CAPABILITY_UNAVAILABLE" or after.get("dbg_start_calls", 0) != before.get("dbg_start_calls", 0):
        raise AssertionError("physical launch crossed the Start boundary")

    inject(observer, "vmware")
    active = require_ok(launch(observer, fixture, architecture), "VMware launch")
    sid, gen = str(active["session_id"]), int(active["generation"])
    active_edit = env_probe(observer, "evaluate", entry_point="edit_dynamic_validation")
    if active_edit.get("error_code") != "INVALID_STATE":
        raise AssertionError("edit dynamic validation did not preserve the debug-idle gate")
    status_before = status_with_context(observer, sid)
    spy_before_restart = require_ok(rpc_tool(observer, "debug_test_spy", {}), "spy before rejected restart")["counters"]
    inject(observer, "unknown")
    restart_rejected = rpc_tool(observer, "debug_restart", {"session_id": sid, "generation": gen,
                                                               "request_id": str(uuid.uuid4())})
    status_after = status_with_context(observer, sid)
    spy_after_restart = require_ok(rpc_tool(observer, "debug_test_spy", {}), "spy after rejected restart")["counters"]
    before_process = status_before.get("owned_process") or {}
    after_process = status_after.get("owned_process") or {}
    if error_code(restart_rejected) != "CAPABILITY_UNAVAILABLE" \
            or status_after["generation"] != status_before["generation"] \
            or after_process.get("process_handle") != before_process.get("process_handle") \
            or spy_after_restart.get("dbg_start_calls", 0) != spy_before_restart.get("dbg_start_calls", 0):
        raise AssertionError(f"restart rejection changed the active target: error={error_code(restart_rejected)}; "
                             f"before={status_before}; after={status_after}; response={restart_rejected}; "
                             f"spy_before={spy_before_restart}; spy_after={spy_after_restart}")
    inject(observer, "virtualbox")
    restarted = require_ok(rpc_tool(observer, "debug_restart", {"session_id": sid, "generation": gen,
                                                                  "request_id": str(uuid.uuid4())}), "VirtualBox restart")
    require_ok(rpc_tool(observer, "debug_terminate", {"session_id": sid, "generation": int(restarted["generation"]),
                                                       "request_id": str(uuid.uuid4())}), "terminate")
    env_probe(observer, "clear_signals")
    run.cleanup["debug_idle"] = require_ok(rpc_tool(observer, "debug_status"), "final idle")["state"] == "idle"
    run.save("acc-030-gate-matrix.json", {"matrix": matrix, "rejected_launch": rejected_launch,
                                          "rejected_restart": restart_rejected, "active_edit": active_edit})
    observer.close()
    run.cleanup["clients_closed"] = True
    return {"fixture_path": str(fixture), "fixture_sha256": sha256(fixture), "architecture": architecture}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", required=True, choices=("EDIT-ACC-017", "EDIT-ACC-027", "EDIT-ACC-030"))
    parser.add_argument("--base-url", default="http://localhost:15378/")
    parser.add_argument("--artifact-root", default=r"C:\dnspy-mcp-artifacts")
    args = parser.parse_args()
    run = AcceptanceRun(args.case, args.base_url, Path(args.artifact_root))
    case_spec = json.loads((ROOT / "tests/edit/cases" / f"{args.case}.json").read_text(encoding="utf-8"))
    if case_spec.get("suite") != "structured-edit" or case_spec.get("case_id") != args.case:
        raise SystemExit("invalid structured-edit case descriptor")
    run.save("case-input.json", case_spec)
    client: DnSpyClient | None = None
    status, reason, metadata = "fail", None, {}
    try:
        client = connect(args.base_url)
        functions = {"EDIT-ACC-017": case_017, "EDIT-ACC-027": case_027, "EDIT-ACC-030": case_030}
        metadata = functions[args.case](run, client)
        status = "pass"
    except (DnSpyConnectionError, OSError) as exc:
        status, reason = "blocked", f"external environment unavailable: {type(exc).__name__}: {exc}"
    except Exception as exc:
        status, reason = "fail", f"{type(exc).__name__}: {exc}"
        run.save("failure.json", {"type": type(exc).__name__, "message": str(exc)})
    finally:
        if client is not None:
            terminate_if_active(client)
            try:
                env_probe(client, "clear_signals")
            except Exception:
                pass
            try:
                client.close()
                run.cleanup["clients_closed"] = True
            except Exception:
                pass
        run.summary(status, reason, metadata)
    aggregate = [status] + [item["status"] for item in run.outcome_checks]
    return 1 if "fail" in aggregate else 2 if "blocked" in aggregate else 0


if __name__ == "__main__":
    raise SystemExit(main())
