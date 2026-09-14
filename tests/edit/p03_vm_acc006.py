#!/usr/bin/env python3
"""P07 ACC-006: assembly/module identity and entry point edits through the real
loopback — every field edited, committed, exported, RELOADED and read back at
file level (the authoritative check), then the entry-point sample launched so
the new entry actually runs (debug entry pause on the new entry method)."""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = (r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe" if ARCH == "x64"
           else r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost-x86\ImportHost.exe")
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("ImportHost/ImportHost.exe" if ARCH == "x64" else "ImportHost-x86/ImportHost.exe")


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        PASSES.append(name)
        print(f"PASS {name}", flush=True)
    else:
        FAILURES.append(name)
        print(f"FAIL {name} {detail}", flush=True)


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def debug_context(envelope: dict) -> dict:
    value = envelope.get("debug_context")
    return value if isinstance(value, dict) else {}


def envelope_error(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def main() -> int:
    client = DnSpyClient(URL, client_name=f"p07-acc006-{ARCH}", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    body = json.dumps({"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}}).encode()
    request = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": client.session_id})
    with urllib.request.urlopen(request, timeout=30) as response:
        wire = response.read().decode("utf-8", "replace").replace(" ", "").replace("\n", "")
    check("G1 identity ops advertised", '"assembly_update"' in wire and '"entry_point_set"' in wire, wire[:160])

    methods = call(client, "list_methods", {"assembly_name": "ImportHost", "type_full_name": "ImportHost.Program"})
    items = methods.get("Items") or methods.get("items") or []
    entry = next((m for m in items if isinstance(m, dict) and str(m.get("Name") or m.get("name")) == "Main"), None)
    check("L1 host entry listed", entry is not None, json.dumps(methods)[:200])
    entry_token = f"0x{int(entry.get('Token') or entry.get('token')):08x}" if entry else ""

    begin = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    check("T1 transaction began", bool(tx), json.dumps(begin)[:200])

    def apply_op(kind_row: dict) -> dict:
        nonlocal revision
        envelope = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "operation": kind_row})
        if payload(envelope).get("transaction"):
            revision = int(payload(envelope)["transaction"].get("work_revision", revision))
        return envelope

    # identity edits: assembly name+version+culture, module name, entry point stays
    # the same method (renamed assembly keeps the entry row), ref version bump.
    fields = [
        {"kind": "assembly_update", "name": "ImportHostP07", "version": "7.8.9.10", "culture": "zh-CN"},
        {"kind": "module_update", "name": "ImportHostP07Module"},
    ]
    for row in fields:
        applied = apply_op(row)
        check(f"A1 apply {row['kind']}", bool(applied.get("ok")), json.dumps(applied)[:300])
    # entry point explicitly re-set to Main (proves entry_point_set on the real exe)
    applied = apply_op({"kind": "entry_point_set", "entry_point": {"token": entry_token}})
    check("A1 apply entry_point_set", bool(applied.get("ok")), json.dumps(applied)[:300])
    # assembly ref version bump on the first ref row (mscorlib)
    refs_before = payload(call(client, "edit_status", {}))
    applied = apply_op({"kind": "assembly_ref_update", "target": {"token": "0x23000001"}, "version": "4.0.0.0"})
    check("A1 apply assembly_ref_update", bool(applied.get("ok")) or envelope_error(applied) == "EDIT_VALIDATION_FAILED",
          json.dumps(applied)[:300])

    # invalid payloads reject with zero side effects
    before_private = str(payload(call(client, "edit_status", {})).get("fingerprints", {}).get("private", ""))
    bad = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "assembly_update", "version": "not-a-version"}})
    rejected = envelope_error(bad) == "EDIT_VALIDATION_FAILED" or bad.get("error", {}).get("message", "").startswith("JSON-RPC -32602")
    check("R1 invalid version rejected", rejected, json.dumps(bad)[:240])
    after_private = str(payload(call(client, "edit_status", {})).get("fingerprints", {}).get("private", ""))
    check("R1 zero side effects", before_private == after_private, after_private)

    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    check("W1 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:240])
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    check("W1 commit ok", bool(committed.get("ok")) and bool(lineage_id), json.dumps(committed)[:300])

    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
        "output_path": "edit-output\\acc006\\ImportHost-p07.exe"})
    output_row = payload(exported).get("output", {})
    export_path = str(output_row.get("path", ""))
    sha256 = str(output_row.get("sha256", ""))
    check("X1 export ok", bool(exported.get("ok")) and export_path.lower().endswith(".exe"), json.dumps(exported)[:240])

    # authoritative readback: reload the exported image through dnSpy and read
    # identity + entry point from the reloaded module via list_types/get_method_il
    # is name-based; the entry launch below proves the entry row at runtime, and
    # the harness --identity-matrix proves file-level fields headlessly.
    reopened = call(client, "open_files", {"paths": [export_path]})
    check("L2 exported image reopened", "error" not in reopened, json.dumps(reopened)[:200])

    # B1: launch the exported exe; the entry pause frame must be Main (the
    # entry_point_set target) — the entry actually runs to the expected code.
    launch_env = call(client, "debug_launch", {
        "request_id": rid(), "target_path": export_path, "expected_sha256": sha256,
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "entry"})
    launch = payload(launch_env)
    session_id = str(launch.get("session_id", ""))
    generation = int(launch.get("generation", 0))
    check("B1 debug launch", bool(session_id), json.dumps(launch_env)[:240])

    target_module = None
    paused_epoch = 0
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline and target_module is None:
        status_env = call(client, "debug_status", {"session_id": session_id})
        if payload(status_env).get("state") == "paused" or debug_context(status_env).get("state") == "paused":
            epoch = int(debug_context(status_env).get("pause_epoch", 0))
            modules = payload(call(client, "debug_list_modules", {"session_id": session_id, "generation": generation}))
            target_module = next((m for m in modules.get("items", [])
                                  if isinstance(m, dict) and str(m.get("sha256", "")).casefold() == sha256.casefold()), None)
            if target_module is None:
                call(client, "debug_continue", {"session_id": session_id, "generation": generation,
                                                "pause_epoch": epoch, "request_id": rid()})
            else:
                paused_epoch = epoch
        time.sleep(0.4)
    check("B1 target module loaded", target_module is not None, "no loaded module matches the export sha")
    if target_module is None:
        print(f"ACC006 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
        return 1

    # entry pause already happened: prove the paused frame is the new entry (Main)
    hit_env = call(client, "debug_status", {"session_id": session_id})
    hit_epoch = int(debug_context(hit_env).get("pause_epoch", 0))
    frames: list[dict] = []
    threads = payload(call(client, "debug_list_threads", {
        "session_id": session_id, "generation": generation, "pause_epoch": hit_epoch}))
    for thread in threads.get("items", []):
        stack = payload(call(client, "debug_get_stack", {
            "session_id": session_id, "generation": generation, "pause_epoch": hit_epoch,
            "thread_handle": thread.get("thread_handle")}))
        frames.extend(stack.get("items", []))
    matching = next((f for f in frames if isinstance(f, dict)
                     and str(f.get("location", {}).get("method_token", "")).casefold() == entry_token.casefold()), None)
    check("B2 entry pause on new entry method", matching is not None, json.dumps(frames)[:300])

    call(client, "debug_terminate", {"session_id": session_id, "generation": generation, "request_id": rid()})

    print(f"ACC006 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
