#!/usr/bin/env python3
"""Focused public entry matrix for RACC-017/030 on the current product DLL."""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient

SIGNALS = {
    "virtualbox": {"manufacturer": "innotek GmbH", "product_name": "VirtualBox", "bios_vendor": "Oracle Corporation"},
    "physical": {"manufacturer": "Dell Inc.", "product_name": "Precision 5820", "bios_vendor": "Dell Inc."},
    "unknown": {"read_failure": True},
}


def rid() -> str:
    return str(uuid.uuid4())


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def powershell(script: str) -> dict:
    run = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                         capture_output=True, text=True, timeout=45, check=False)
    if run.returncode != 0:
        raise RuntimeError("PowerShell probe failed: " + run.stderr[-600:])
    return json.loads(run.stdout.strip())


def external(fixture: Path, artifact: Path) -> dict:
    f = str(fixture).replace("'", "''")
    a = str(artifact).replace("'", "''")
    return powershell(
        "$p=@(Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -ieq '" + f +
        "'}|ForEach-Object{[pscustomobject]@{pid=$_.ProcessId;exe=$_.ExecutablePath;"
        "ticks=([datetime]$_.CreationDate).ToUniversalTime().Ticks.ToString()}});"
        "$files=@(Get-ChildItem -LiteralPath '" + a + "' -Recurse -File -ErrorAction SilentlyContinue|"
        "ForEach-Object{[pscustomobject]@{path=$_.FullName;length=$_.Length;"
        "sha=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}});"
        "[pscustomobject]@{processes=$p;files=$files}|ConvertTo-Json -Depth 6 -Compress")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--artifact", type=Path, required=True)
    parser.add_argument("--arch", choices=("x64", "x86"), required=True)
    parser.add_argument("--result", type=Path, required=True)
    args = parser.parse_args()
    record: dict = {"status": "FAIL", "arch": args.arch,
                    "fixture": {"path": str(args.fixture), "sha256": sha(args.fixture)},
                    "calls": [], "checks": [], "cases": []}
    client = None

    def call(tool: str, arguments: dict | None = None) -> dict:
        immutable = json.dumps({"tool": tool, "arguments": arguments or {}}, ensure_ascii=False,
                               sort_keys=True, separators=(",", ":"))
        try:
            response = client.call_tool_json(tool, json.loads(immutable)["arguments"])
        except Exception as exc:
            text = str(exc)
            start = text.find("{")
            if start >= 0:
                try:
                    response = json.loads(text[start:])
                except json.JSONDecodeError:
                    response = {"error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}
            else:
                response = {"error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}
        record["calls"].append({"index": len(record["calls"]), "request_snapshot": immutable,
                                "response": response})
        return response

    def result(row: dict) -> dict:
        value = row.get("result")
        return value if isinstance(value, dict) else {}

    def code(row: dict) -> str:
        err = row.get("error")
        return str(err.get("code", "")) if isinstance(err, dict) else ""

    def ok(row: dict, label: str) -> dict:
        if not row.get("ok"):
            raise AssertionError(label + ": " + json.dumps(row, ensure_ascii=False)[:750])
        return result(row)

    def check(label: str, value: bool, detail: dict | None = None) -> None:
        record["checks"].append({"label": label, "pass": bool(value), "detail": detail})
        print(("PASS " if value else "FAIL ") + label, flush=True)
        if not value:
            raise AssertionError(label + " " + json.dumps(detail, ensure_ascii=False)[:750])

    def env(action: str, **kw) -> dict:
        return ok(call("debug_test_environment", {"p01_action": action, **kw}), action)

    def spy() -> dict:
        return ok(call("debug_test_spy"), "spy").get("counters", {})

    def status() -> tuple[dict, dict]:
        row = call("debug_status")
        return row, ok(row, "debug_status")

    def capabilities() -> dict:
        return ok(call("debug_capabilities"), "debug_capabilities")["execution_environment"]

    def launch() -> dict:
        return call("debug_launch", {"request_id": rid(), "target_path": str(args.fixture),
                                     "expected_sha256": record["fixture"]["sha256"],
                                     "launch_mode": "net48-exe", "architecture": args.arch,
                                     "break_kind": "none"})

    def restart(session: str, generation: int) -> dict:
        return call("debug_restart", {"request_id": rid(), "session_id": session, "generation": generation})

    def terminate(session: str, generation: int) -> None:
        ok(call("debug_terminate", {"request_id": rid(), "session_id": session,
                                    "generation": generation}), "terminate")

    def begin() -> tuple[str, int]:
        row = ok(call("edit_begin", {"assembly_name": "P01Fixture", "request_id": rid()}), "begin")
        tx = row["transaction"]
        return str(tx["transaction_id"]), int(tx["work_revision"])

    def review(tx: str, revision: int) -> dict:
        return call("edit_review", {"request_id": rid(), "transaction_id": tx,
                                    "expected_revision": revision,
                                    "dynamic_validation": {"mode": "run", "runtime_profile": "net48-exe",
                                                           "timeout_ms": 15000}})

    def rollback(tx: str) -> None:
        ok(call("edit_rollback", {"request_id": rid(), "transaction_id": tx}), "rollback")

    try:
        client = DnSpyClient.connect(args.url, client_name="t077-entry-" + args.arch, timeout=120)
        advertised = {str(row.get("name")) for row in client.iter_tools()}
        check("test injection not advertised", "debug_test_environment" not in advertised)
        real = capabilities()
        check("real VMware default", real["classification"] == "vmware"
              and real["execution_allowed"] and not real["local_process_override_active"], real)
        record["real_environment"] = real
        opened = call("open_files", {"paths": [str(args.fixture)]})
        check("private fixture loaded", opened.get("loaded_count") == 1 and opened.get("failed_count") == 0, opened)
        baseline_external = external(args.fixture, args.artifact)
        check("private target initially absent", not baseline_external["processes"], baseline_external)

        for classification in ("vmware", "virtualbox", "physical", "unknown"):
            env("clear_signals")
            if classification != "vmware":
                env("inject_signals", **SIGNALS[classification])
            snapshot = capabilities()
            allowed = classification in ("vmware", "virtualbox")
            check(classification + " classification", snapshot["classification"] == classification
                  and snapshot["execution_allowed"] is allowed
                  and not snapshot["local_process_override_active"], snapshot)
            row = {"classification": classification, "snapshot": snapshot}
            record["cases"].append(row)
            if allowed:
                before = spy()
                launched = launch()
                started = ok(launched, classification + " launch")
                sid, generation = str(started["session_id"]), int(started["generation"])
                after_launch = external(args.fixture, args.artifact)
                check(classification + " launch target created", len(after_launch["processes"]) == 1
                      and after_launch["processes"][0]["exe"].casefold() == str(args.fixture).casefold()
                      and spy().get("dbg_start_calls", 0) > before.get("dbg_start_calls", 0), after_launch)
                restarted = ok(restart(sid, generation), classification + " restart")
                next_generation = int(restarted["generation"])
                check(classification + " restart generation", next_generation == generation + 1,
                      {"start": started, "restart": restarted})
                terminate(sid, next_generation)
                check(classification + " debugger idle after terminate", status()[1]["state"] == "idle")
                tx, revision = begin()
                before_dynamic = spy()
                dynamic = review(tx, revision)
                row["dynamic_response"] = dynamic
                attempt = result(dynamic).get("dynamic_validation", {})
                check(classification + " dynamic validation allowed", dynamic.get("ok") is True
                      and attempt.get("requested") is True and attempt.get("state") == "passed"
                      and spy().get("dbg_start_calls", 0) > before_dynamic.get("dbg_start_calls", 0),
                      {"response": dynamic, "attempt": attempt})
                rollback(tx)
                check(classification + " no target/artifact residue", not external(args.fixture, args.artifact)["processes"]
                      and not external(args.fixture, args.artifact)["files"])
            else:
                before_status, before_state = status()
                before_spy = spy()
                before_external = external(args.fixture, args.artifact)
                launch_response = launch()
                after_status, after_state = status()
                after_spy = spy()
                after_external = external(args.fixture, args.artifact)
                check(classification + " launch refused before side effects",
                      code(launch_response) == "CAPABILITY_UNAVAILABLE"
                      and before_status.get("debug_context") == after_status.get("debug_context")
                      and before_state == after_state and before_external == after_external
                      and before_spy.get("dbg_start_calls", 0) == after_spy.get("dbg_start_calls", 0),
                      {"response": launch_response, "before": before_external, "after": after_external})
                tx, revision = begin()
                before_spy = spy()
                before_external = external(args.fixture, args.artifact)
                dynamic = review(tx, revision)
                after_spy = spy()
                after_external = external(args.fixture, args.artifact)
                attempt = dynamic.get("validation_attempt", {})
                check(classification + " edit validation refused before artifact/start",
                      code(dynamic) == "EDIT_CAPABILITY_UNAVAILABLE"
                      and attempt.get("artifact", {}).get("created") is False
                      and not attempt.get("events") and before_external == after_external
                      and before_spy.get("dbg_start_calls", 0) == after_spy.get("dbg_start_calls", 0),
                      {"response": dynamic, "before": before_external, "after": after_external})
                rollback(tx)
                env("clear_signals")
                active = ok(launch(), "real VMware target for restart negative")
                sid, generation = str(active["session_id"]), int(active["generation"])
                before_status, before_state = status()
                before_spy = spy()
                before_external = external(args.fixture, args.artifact)
                env("inject_signals", **SIGNALS[classification])
                # Launch events can move entry pause to running asynchronously.
                # Bind the refusal baseline after the signal change, immediately
                # before restart, so those legitimate events are not blamed on it.
                before_status, before_state = status()
                before_spy = spy()
                before_external = external(args.fixture, args.artifact)
                rejected = restart(sid, generation)
                after_status, after_state = status()
                after_spy = spy()
                after_external = external(args.fixture, args.artifact)
                before_context = before_status.get("debug_context", {})
                after_context = after_status.get("debug_context", {})
                before_owner = before_state.get("owned_process") or {}
                after_owner = after_state.get("owned_process") or {}
                check(classification + " restart refused without terminate/replacement",
                      code(rejected) == "CAPABILITY_UNAVAILABLE"
                      and before_context.get("session_id") == after_context.get("session_id")
                      and before_context.get("generation") == after_context.get("generation")
                      and before_context.get("state") == after_context.get("state")
                      and before_owner.get("process_handle") == after_owner.get("process_handle")
                      and before_external == after_external
                      and before_spy.get("dbg_start_calls", 0) == after_spy.get("dbg_start_calls", 0),
                      {"response": rejected, "before": before_external, "after": after_external})
                env("clear_signals")
                terminate(sid, generation)
                check(classification + " debugger idle after retained target terminated", status()[1]["state"] == "idle")

        env("inject_signals", manufacturer="Oracle Corporation", product_name="Oracle Server", bios_vendor="Oracle Corporation")
        oracle = capabilities()
        check("Oracle-only physical", oracle["classification"] == "physical" and not oracle["execution_allowed"], oracle)
        env("clear_signals")
        tx, revision = begin()
        active = ok(launch(), "busy gate setup")
        sid, generation = str(active["session_id"]), int(active["generation"])
        before_spy = spy()
        before_external = external(args.fixture, args.artifact)
        busy = review(tx, revision)
        check("active debug edit idle gate", code(busy) == "EDIT_DEBUG_NOT_IDLE"
              and busy.get("validation_attempt", {}).get("artifact", {}).get("created") is False
              and before_external == external(args.fixture, args.artifact)
              and before_spy.get("dbg_start_calls", 0) == spy().get("dbg_start_calls", 0), busy)
        terminate(sid, generation)
        rollback(tx)
        check("final idle and no target/artifact", status()[1]["state"] == "idle"
              and not external(args.fixture, args.artifact)["processes"]
              and not external(args.fixture, args.artifact)["files"])
        record["status"] = "PASS"
    except Exception as exc:
        record["failure"] = {"type": type(exc).__name__, "message": str(exc)}
    finally:
        if client is not None:
            try:
                env("clear_signals")
            except Exception:
                pass
            try:
                client.close()
            except Exception:
                pass
        args.result.parent.mkdir(parents=True, exist_ok=True)
        args.result.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": record["status"], "checks": len(record["checks"]),
                      "failure": record.get("failure")}, ensure_ascii=False), flush=True)
    return 0 if record["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
