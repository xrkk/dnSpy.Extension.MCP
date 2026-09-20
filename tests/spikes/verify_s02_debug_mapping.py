#!/usr/bin/env python3
"""Verify S02's embedded-symbol result with a real dnSpy debugger hit."""

from __future__ import annotations

import json
import argparse
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from dnspy_mcp.client import DnSpyClient, ToolCallError  # noqa: E402
from win10vm_mcp import Win10VmClient  # noqa: E402


DNSPY_URL = "http://192.168.204.149:15378/"
VM_URL = "http://192.168.204.149:28787/mcp"


def request_id() -> str:
    return str(uuid.uuid4())


def require_ok(name: str, value: Any) -> dict[str, Any]:
    if not isinstance(value, dict) or value.get("ok") is not True:
        raise RuntimeError(f"{name} failed: {json.dumps(value, ensure_ascii=False)}")
    return value


def powershell_result(value: Any) -> str:
    text = value.get("result", "") if isinstance(value, dict) else str(value)
    marker = "Response: "
    status = "\n\nStatus Code:"
    start = text.find(marker)
    end = text.rfind(status)
    if start < 0 or end < 0:
        raise RuntimeError(f"Unexpected Win10VM PowerShell response: {text[:500]}")
    return text[start + len(marker):end].strip()


def main(architecture: str) -> int:
    artifact_path = rf"C:\dnspy-mcp-artifacts\spikes\2026-08-31\s02-imported-embedded-{architecture}.exe"
    evidence_path = ROOT / f"PLAN/2026.08.31/技术Spike/raw/s02-debug-mapping-{architecture}.json"
    evidence: dict[str, Any] = {
        "format": "dnspy.mcp.spike.s02-debug-mapping.v1",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "automation": "AI -> Python client -> dnSpy MCP -> real dnSpy debugger",
        "artifact_path": artifact_path,
        "architecture": architecture,
        "checks": {},
        "responses": {},
        "pass": False,
    }
    vm = Win10VmClient.connect(VM_URL, timeout=60, client_name="s02-debug-artifact-check")
    try:
        command = (
            f'$p="{artifact_path}"; '
            '[pscustomobject]@{Exists=(Test-Path -LiteralPath $p); '
            'Sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant(); '
            'SidecarPdb=(Test-Path -LiteralPath ($p+".pdb"))}|ConvertTo-Json -Compress'
        )
        artifact = json.loads(powershell_result(vm.call_tool_json(
            "PowerShell", {"command": command, "timeout": 30}
        )))
    finally:
        vm.close()
    if not artifact["Exists"] or artifact["SidecarPdb"]:
        raise RuntimeError(f"Artifact precondition failed: {artifact}")
    sha256 = artifact["Sha256"]
    evidence["artifact"] = artifact

    client = DnSpyClient.connect(DNSPY_URL, timeout=45, client_name="s02-debug-mapping")
    session_id: str | None = None
    generation: int | None = None
    try:
        opened = client.call_tool_json("open_files", {"paths": [artifact_path]})
        methods = client.call_tool_json("list_methods", {
            "assembly_name": "SpikeFixture",
            "type_full_name": "SpikeFixture.Target",
        })
        compute = next(item for item in methods["items"] if item["name"] == "Compute")
        token = f"0x{int(compute['token']):08x}"
        il = client.call_tool_json("get_method_il", {
            "assembly_name": "SpikeFixture",
            "type_full_name": "SpikeFixture.Target",
            "method_name": "Compute",
            "method_token": token,
        })
        il_text = json.dumps(il, ensure_ascii=False)
        instructions = il.get("instructions", []) if isinstance(il, dict) else []
        evidence["responses"]["open_files"] = opened
        evidence["responses"]["compute_method"] = compute
        evidence["responses"]["compute_il"] = il
        evidence["checks"].update({
            "edited_il_constants_present": "ldc.i4.3" in il_text and "ldc.i4.7" in il_text,
            "reloaded_field_reference_resolved": any(
                item.get("opcode") == "ldfld"
                and item.get("operand") == "field:System.Int32 SpikeFixture.Target::Field"
                for item in instructions
            ),
            "reloaded_method_reference_resolved": any(
                item.get("opcode") == "call"
                and item.get("operand") == "method:System.Int32 SpikeFixture.Target::Helper(System.Int32)"
                for item in instructions
            ),
        })
        if not all(evidence["checks"].values()):
            raise RuntimeError("Edited Compute IL or target member references are missing")

        launch = require_ok("debug_launch", client.call_tool_json("debug_launch", {
            "request_id": request_id(),
            "target_path": artifact_path,
            "expected_sha256": sha256,
            "launch_mode": "net48-exe",
            "architecture": architecture,
            "break_kind": "entry",
        }))
        evidence["responses"]["launch"] = launch
        session_id = launch["result"]["session_id"]
        generation = int(launch["result"]["generation"])

        pause_epoch: int | None = None
        status: dict[str, Any] | None = None
        module: dict[str, Any] | None = None
        startup_observations: list[dict[str, Any]] = []
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            status = require_ok("debug_status", client.call_tool_json(
                "debug_status", {"session_id": session_id}
            ))
            if status["result"]["state"] == "paused":
                candidate_epoch = int(status["debug_context"]["pause_epoch"])
                modules = require_ok("debug_list_modules", client.call_tool_json(
                    "debug_list_modules", {"session_id": session_id, "generation": generation}
                ))
                module = next((
                    item for item in modules["result"]["items"]
                    if item.get("sha256", "").casefold() == sha256.casefold()
                ), None)
                startup_observations.append({
                    "pause_epoch": candidate_epoch,
                    "module_names": [item.get("name") for item in modules["result"]["items"]],
                    "target_loaded": module is not None,
                })
                if module is not None:
                    pause_epoch = candidate_epoch
                    break
                # The first create-break pause can precede target module loading. Let the
                # loader advance; break_kind=entry will stop again at the actual entry point.
                try:
                    client.call_tool_json("debug_continue", {
                        "session_id": session_id,
                        "generation": generation,
                        "pause_epoch": candidate_epoch,
                        "request_id": request_id(),
                    })
                except ToolCallError as exc:
                    # The debugger can auto-continue this transient pause between the
                    # status sample and our request. INVALID_STATE/running is expected.
                    startup_observations[-1]["continue_race"] = str(exc)
            time.sleep(0.25)
        if pause_epoch is None or status is None or module is None:
            raise RuntimeError("Target module entry pause was not acquired")
        evidence["responses"]["entry_status"] = status
        evidence["responses"]["startup_observations"] = startup_observations
        evidence["responses"]["target_module"] = module

        breakpoint = require_ok("debug_set_breakpoint", client.call_tool_json(
            "debug_set_breakpoint", {
                "session_id": session_id,
                "generation": generation,
                "pause_epoch": pause_epoch,
                "request_id": request_id(),
                "module_handle": module["module_handle"],
                "module_sha256": sha256,
                "mvid": module["mvid"],
                "method_token": token,
                "il_offset": 0,
                "enabled": True,
            }
        ))
        evidence["responses"]["breakpoint"] = breakpoint
        after_cursor = int(breakpoint["debug_context"].get("event_cursor", 0))

        continued = require_ok("debug_continue", client.call_tool_json("debug_continue", {
            "session_id": session_id,
            "generation": generation,
            "pause_epoch": pause_epoch,
            "request_id": request_id(),
        }))
        evidence["responses"]["continue"] = continued

        all_events: list[dict[str, Any]] = []
        hit_event: dict[str, Any] | None = None
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and hit_event is None:
            waited = require_ok("debug_wait_event", client.call_tool_json(
                "debug_wait_event", {
                    "session_id": session_id,
                    "after_cursor": after_cursor,
                    "limit": 20,
                    "kinds": ["breakpoint_bound", "breakpoint_hit", "paused", "process_exited"],
                    "timeout_ms": 2500,
                }
            ))
            events = waited["result"].get("events", [])
            all_events.extend(events)
            if events:
                after_cursor = max(int(event["cursor"]) for event in events)
            hit_event = next((event for event in events if event["kind"] == "breakpoint_hit"), None)
        evidence["responses"]["events"] = all_events
        if hit_event is None:
            raise RuntimeError("Compute breakpoint was not hit")

        hit_status = require_ok("debug_status", client.call_tool_json(
            "debug_status", {"session_id": session_id}
        ))
        hit_epoch = int(hit_status["debug_context"]["pause_epoch"])
        threads = require_ok("debug_list_threads", client.call_tool_json(
            "debug_list_threads", {
                "session_id": session_id,
                "generation": generation,
                "pause_epoch": hit_epoch,
            }
        ))
        frames: list[dict[str, Any]] = []
        for thread in threads["result"]["items"]:
            stack = require_ok("debug_get_stack", client.call_tool_json(
                "debug_get_stack", {
                    "session_id": session_id,
                    "generation": generation,
                    "pause_epoch": hit_epoch,
                    "thread_handle": thread["thread_handle"],
                }
            ))
            frames.extend(stack["result"]["items"])
        matching_frame = next((
            frame for frame in frames
            if frame.get("location", {}).get("method_token", "").casefold() == token.casefold()
            and int(frame.get("location", {}).get("il_offset", -1)) == 0
        ), None)
        evidence["responses"]["hit_status"] = hit_status
        evidence["responses"]["hit_event"] = hit_event
        evidence["responses"]["matching_frame"] = matching_frame
        evidence["checks"].update({
            "breakpoint_event_received": True,
            "compute_frame_at_il_zero": matching_frame is not None,
            "embedded_symbols_map_edited_compute": matching_frame is not None,
        })
        if matching_frame is None:
            raise RuntimeError("Breakpoint event arrived but no Compute IL 0 frame was found")
        evidence["pass"] = True
    finally:
        if session_id is not None and generation is not None:
            try:
                terminated = client.call_tool_json("debug_terminate", {
                    "session_id": session_id,
                    "generation": generation,
                    "request_id": request_id(),
                })
                evidence["responses"]["terminate"] = terminated
                idle = client.call_tool_json("debug_status", {"session_id": session_id})
                evidence["responses"]["final_status"] = idle
                evidence["checks"]["final_state_idle"] = (
                    isinstance(idle, dict) and idle.get("result", {}).get("state") == "idle"
                )
                evidence["pass"] = bool(evidence["pass"] and evidence["checks"]["final_state_idle"])
            except Exception as exc:  # preserve cleanup failure in the evidence
                evidence["cleanup_error"] = repr(exc)
                evidence["pass"] = False
        client.close()
        evidence_path.parent.mkdir(parents=True, exist_ok=True)
        evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps({
        "pass": evidence["pass"],
        "method_token": evidence["responses"]["compute_method"]["token"],
        "event_kind": evidence["responses"]["hit_event"]["kind"],
        "frame": evidence["responses"]["matching_frame"],
        "evidence": str(evidence_path),
    }, ensure_ascii=False, indent=2))
    return 0 if evidence["pass"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--architecture", choices=("x64", "x86"), default="x64")
    args = parser.parse_args()
    raise SystemExit(main(args.architecture))
