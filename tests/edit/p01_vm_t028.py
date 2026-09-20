#!/usr/bin/env python3
"""T028 isolated ACC017/030 acceptance through public MCP entry points."""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402


def rid() -> str:
    return str(uuid.uuid4())


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def call(client: DnSpyClient, tool: str, args: dict | None = None) -> dict:
    try:
        return client.call_tool_json(tool, args or {})
    except Exception as ex:  # noqa: BLE001
        text = str(ex); start = text.find("{")
        if start >= 0:
            try: return json.loads(text[start:])
            except json.JSONDecodeError: pass
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}


def payload(value: dict) -> dict:
    row = value.get("result") if isinstance(value, dict) else None
    return row if isinstance(row, dict) else {}


def error(value: dict) -> str:
    row = value.get("error") if isinstance(value, dict) else None
    return str(row.get("code", "")) if isinstance(row, dict) else ""


def require(value: dict, label: str) -> dict:
    if not value.get("ok"): raise AssertionError(label + ": " + json.dumps(value)[:800])
    return payload(value)


def env(client: DnSpyClient, action: str, **kwargs) -> dict:
    return require(call(client, "debug_test_environment", {"p01_action": action, **kwargs}), "environment " + action)


SIGNALS = {
    "vmware": {"manufacturer": "VMware, Inc.", "product_name": "VMware Virtual Platform", "bios_vendor": "Phoenix Technologies LTD"},
    "virtualbox": {"manufacturer": "innotek GmbH", "product_name": "VirtualBox", "bios_vendor": "Oracle Corporation"},
    "physical": {"manufacturer": "Dell Inc.", "product_name": "Precision 5820", "bios_vendor": "Dell Inc."},
    "unknown": {"read_failure": True},
}


def inject(client: DnSpyClient, name: str) -> dict:
    return env(client, "inject_signals", **SIGNALS[name])


def spy(client: DnSpyClient) -> dict:
    return require(call(client, "debug_test_spy"), "spy").get("counters", {})


def status(client: DnSpyClient) -> tuple[dict, dict]:
    raw = call(client, "debug_status"); return raw, require(raw, "status")


def fixture_processes(path: Path) -> list[dict]:
    escaped = str(path).replace("'", "''")
    script = ("$p=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq '" + escaped
              + "'}|Select-Object ProcessId,ExecutablePath);$p|ConvertTo-Json -Compress")
    run = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                         capture_output=True, text=True, timeout=30, check=False)
    text = run.stdout.strip()
    if not text: return []
    value = json.loads(text); return value if isinstance(value, list) else [value]


def reconnect(old: DnSpyClient, url: str) -> DnSpyClient:
    try: old.close()
    except Exception: pass
    deadline = time.monotonic() + 40; last = None
    while time.monotonic() < deadline:
        try: return DnSpyClient.connect(url, client_name="t028-reconnect", timeout=60)
        except Exception as ex: last = ex; time.sleep(.25)
    raise RuntimeError("listener reconnect failed: " + str(last))


def signal(client: DnSpyClient, url: str, signal_dir: Path, sequence: int, action: str, **values) -> DnSpyClient:
    request = signal_dir / f"request-{sequence:02d}.json"; ack = signal_dir / f"ack-{sequence:02d}.json"
    request.write_text(json.dumps({"sequence": sequence, "action": action, **values}, indent=2), encoding="utf-8")
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        if ack.is_file():
            result = json.loads(ack.read_text(encoding="utf-8"))
            if result.get("result") != "PASS": raise RuntimeError("signal failed: " + json.dumps(result))
            return reconnect(client, url)
        time.sleep(.1)
    raise TimeoutError("signal acknowledgement timed out: " + str(request))


def launch(client: DnSpyClient, fixture: Path, arch: str) -> dict:
    return call(client, "debug_launch", {"request_id": rid(), "target_path": str(fixture),
        "expected_sha256": sha(fixture), "launch_mode": "net48-exe", "architecture": arch,
        "break_kind": "none"})


def begin(client: DnSpyClient) -> tuple[str, int]:
    row = require(call(client, "edit_begin", {"assembly_name": "P01Fixture", "request_id": rid()}), "edit begin")
    tx = row.get("transaction", {}); return str(tx.get("transaction_id", "")), int(tx.get("work_revision", 0))


def dynamic_review(client: DnSpyClient, tx: str, revision: int) -> dict:
    return call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "dynamic_validation": {"mode": "run", "runtime_profile": "net48-exe", "timeout_ms": 10000}})


def terminate(client: DnSpyClient, session: str, generation: int) -> dict:
    return call(client, "debug_terminate", {"request_id": rid(), "session_id": session, "generation": generation})


def main() -> int:
    ap = argparse.ArgumentParser(); ap.add_argument("--url", required=True); ap.add_argument("--arch", required=True)
    ap.add_argument("--fixture", type=Path, required=True); ap.add_argument("--signal-dir", type=Path, required=True)
    ap.add_argument("--result", type=Path, required=True)
    ap.add_argument("--skip-ui-lifecycle", action="store_true")
    args = ap.parse_args()
    args.signal_dir.mkdir(parents=True, exist_ok=True); checks: list[dict] = []; facts: dict = {}
    def check(name: str, condition: bool, detail=None):
        checks.append({"name": name, "pass": bool(condition), "detail": detail})
        print(("PASS " if condition else "FAIL ") + name + ("" if condition else " " + json.dumps(detail, ensure_ascii=False)[:1000]), flush=True)
    client = DnSpyClient.connect(args.url, client_name="t028-" + args.arch, timeout=120)
    try:
        tools = {str(x.get("name")) for x in client.iter_tools()}
        check("test seam is unadvertised", "debug_test_environment" not in tools)
        real = require(call(client, "debug_capabilities"), "capabilities")["execution_environment"]
        check("real environment is allowed VMware", real["classification"] == "vmware" and real["execution_allowed"] and not real["local_process_override_active"], real)
        facts["real_environment"] = real
        matrix = {}
        for name, allowed in (("vmware", True), ("virtualbox", True), ("physical", False), ("unknown", False)):
            injected = inject(client, name)["execution_environment"]; entries = {}
            for entry in ("debug_launch", "debug_restart", "edit_dynamic_validation"):
                decision = env(client, "evaluate", entry_point=entry)
                entries[entry] = {"allowed": decision["allowed"], "error_code": decision.get("error_code"), "state": decision["state"]}
            matrix[name] = {"input": SIGNALS[name], "snapshot": injected, "entries": entries}
            check("classification/entry matrix " + name, injected["classification"] == name
                  and injected["execution_allowed"] is allowed
                  and all(bool(v["allowed"]) is allowed for v in entries.values()), matrix[name])
        spoof = env(client, "snapshot", local_process_override_active=True)["execution_environment"]
        check("MCP payload cannot enable override", not spoof["local_process_override_active"], spoof)

        opened = call(client, "open_files", {"paths": [str(args.fixture)]})
        check("fixture opened", opened.get("loaded_count") == 1 and opened.get("failed_count") == 0, opened)
        inject(client, "physical"); before_spy = spy(client); before_proc = fixture_processes(args.fixture)
        rejected_launch = launch(client, args.fixture, args.arch); after_spy = spy(client); after_proc = fixture_processes(args.fixture)
        check("physical debug_launch rejected before process start", error(rejected_launch) == "CAPABILITY_UNAVAILABLE"
              and after_spy.get("dbg_start_calls", 0) == before_spy.get("dbg_start_calls", 0)
              and not before_proc and not after_proc, {"response": rejected_launch, "before": before_spy, "after": after_spy, "processes": after_proc})
        tx_physical, rev_physical = begin(client); spy_before_edit = spy(client)
        rejected_edit = dynamic_review(client, tx_physical, rev_physical); spy_after_edit = spy(client)
        check("physical edit dynamic validation rejected before process start", error(rejected_edit) == "EDIT_CAPABILITY_UNAVAILABLE"
              and spy_after_edit.get("dbg_start_calls", 0) == spy_before_edit.get("dbg_start_calls", 0)
              and not fixture_processes(args.fixture), {"response": rejected_edit, "before": spy_before_edit, "after": spy_after_edit})
        require(call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx_physical}), "physical rollback")

        inject(client, "vmware"); tx_active, rev_active = begin(client)
        running = require(launch(client, args.fixture, args.arch), "VMware launch"); sid = str(running["session_id"]); gen = int(running["generation"])
        active_edit = dynamic_review(client, tx_active, rev_active)
        check("active debugger preserves edit idle gate", error(active_edit) == "EDIT_DEBUG_NOT_IDLE", active_edit)
        raw_before, state_before = status(client); spy_before_restart = spy(client); inject(client, "unknown")
        rejected_restart = call(client, "debug_restart", {"request_id": rid(), "session_id": sid, "generation": gen})
        raw_after, state_after = status(client); spy_after_restart = spy(client)
        check("unknown restart rejected without target replacement", error(rejected_restart) == "CAPABILITY_UNAVAILABLE"
              and raw_after.get("debug_context", {}).get("generation") == raw_before.get("debug_context", {}).get("generation")
              and (state_after.get("owned_process") or {}).get("process_handle") == (state_before.get("owned_process") or {}).get("process_handle")
              and spy_after_restart.get("dbg_start_calls", 0) == spy_before_restart.get("dbg_start_calls", 0),
              {"response": rejected_restart, "before": state_before, "after": state_after})
        inject(client, "virtualbox")
        restarted = require(call(client, "debug_restart", {"request_id": rid(), "session_id": sid, "generation": gen}), "VirtualBox restart")
        check("VirtualBox restart succeeds", int(restarted["generation"]) == gen + 1, restarted)
        require(terminate(client, sid, int(restarted["generation"])), "terminate restarted")
        require(call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx_active}), "active rollback")

        if not args.skip_ui_lifecycle:
            inject(client, "physical"); client = signal(client, args.url, args.signal_dir, 1, "apply_override", enable=True)
            enabled = require(call(client, "debug_capabilities"), "override enabled")["execution_environment"]
            check("local UI override enables physical process", enabled["classification"] == "physical" and enabled["execution_allowed"] and enabled["local_process_override_active"], enabled)
            override_launch = require(launch(client, args.fixture, args.arch), "override physical launch")
            require(terminate(client, str(override_launch["session_id"]), int(override_launch["generation"])), "override terminate")
            still_enabled = env(client, "snapshot", local_process_override_active=False)["execution_environment"]
            check("MCP payload cannot disable override", still_enabled["local_process_override_active"] and still_enabled["execution_allowed"], still_enabled)
            client = signal(client, args.url, args.signal_dir, 2, "apply_override", enable=False)
            disabled = require(call(client, "debug_capabilities"), "override disabled")["execution_environment"]
            check("local UI clears override", disabled["classification"] == "physical" and not disabled["execution_allowed"] and not disabled["local_process_override_active"], disabled)
            client = signal(client, args.url, args.signal_dir, 3, "apply_override", enable=True)
            pre_restart = require(call(client, "debug_capabilities"), "pre process restart")["execution_environment"]
            check("override active before process restart", pre_restart["local_process_override_active"], pre_restart)
            client = signal(client, args.url, args.signal_dir, 4, "restart_process")
            inject(client, "physical"); after_restart = require(call(client, "debug_capabilities"), "after process restart")["execution_environment"]
            check("process restart clears override", after_restart["classification"] == "physical" and not after_restart["execution_allowed"] and not after_restart["local_process_override_active"], after_restart)
            facts["override"] = {"enabled": enabled, "disabled": disabled, "after_restart": after_restart}
        env(client, "clear_signals"); final_raw, final_status = status(client)
        check("final debugger idle", final_status["state"] == "idle", final_raw)
        facts.update({"matrix": matrix, "physical_launch": rejected_launch, "physical_edit": rejected_edit,
                      "active_edit": active_edit, "rejected_restart": rejected_restart})
    except Exception as ex:  # noqa: BLE001
        check("driver exception", False, {"type": type(ex).__name__, "message": str(ex)})
    finally:
        try: env(client, "clear_signals")
        except Exception: pass
        try: client.close()
        except Exception: pass
    result = {"arch": args.arch, "status": "PASS" if checks and all(x["pass"] for x in checks) else "FAIL",
              "fixture": {"path": str(args.fixture), "sha256": sha(args.fixture)}, "checks": checks, "facts": facts}
    args.result.parent.mkdir(parents=True, exist_ok=True); args.result.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": result["status"], "checks": len(checks), "failures": [x["name"] for x in checks if not x["pass"]]}, ensure_ascii=False), flush=True)
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
