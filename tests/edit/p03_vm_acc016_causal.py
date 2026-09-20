#!/usr/bin/env python3
"""T026: causal, fail-closed ACC-016 strong-name branch evidence."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = ""
INBOUND = ""
UNSIGNED = ""
LAUNCH_ROOT = ""
PASSES: list[str] = []
FAILURES: list[str] = []


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE, INBOUND, UNSIGNED, LAUNCH_ROOT
    context.validate()
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("StrongHost/StrongHost.exe")
    INBOUND = context.fixture("StrongHost/InboundStrong.dll")
    UNSIGNED = context.fixture("UnsignedHost/UnsignedHost.exe")
    LAUNCH_ROOT = context.fixture_output(f".acc016causal-launch/{context.run_id}/{ARCH}")


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail: object = "") -> None:
    (PASSES if condition else FAILURES).append(name)
    print(f"{'PASS' if condition else 'FAIL'} {name} {'' if condition else detail}", flush=True)


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


def payload(value: dict) -> dict:
    row = value.get("result") if isinstance(value, dict) else None
    return row if isinstance(row, dict) else {}


def error_code(value: dict) -> str:
    row = value.get("error") if isinstance(value, dict) else None
    return str(row.get("code", "")) if isinstance(row, dict) else ""


def sha256(path: str) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def identity(path: str) -> dict:
    escaped = path.replace("'", "''")
    script = (
        "$a=[Reflection.AssemblyName]::GetAssemblyName('" + escaped + "');"
        "$t=($a.GetPublicKeyToken()|ForEach-Object {$_.ToString('x2')})-join '';"
        "[pscustomobject]@{name=$a.Name;version=$a.Version.ToString();culture=($a.CultureName);"
        "flags=$a.Flags.ToString();public_key_token=$t}|ConvertTo-Json -Compress"
    )
    run = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                         capture_output=True, text=True, errors="replace", timeout=30, check=False)
    if run.returncode != 0:
        return {"error": run.stderr.strip(), "exit_code": run.returncode}
    return json.loads(run.stdout.strip())


def sn_verify(path: str) -> dict:
    sn = r"C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\sn.exe"
    run = subprocess.run([sn, "-vf", path], capture_output=True, text=True, errors="replace", timeout=30, check=False)
    return {"exit_code": run.returncode, "stdout": run.stdout.strip(), "stderr": run.stderr.strip()}


def direct_run(path: str) -> dict:
    run = subprocess.run([path], capture_output=True, text=True, errors="replace", timeout=30, check=False)
    return {"exit_code": run.returncode, "stdout": run.stdout.strip(), "stderr": run.stderr.strip()}


def copy_verified(source: str, name: str) -> tuple[str, dict]:
    root = Path(LAUNCH_ROOT)
    root.mkdir(parents=True, exist_ok=True)
    target = root / name
    if target.exists():
        raise RuntimeError(f"launch target already exists: {target}")
    before = sha256(source)
    shutil.copyfile(source, target)
    after = sha256(source)
    copied = sha256(str(target))
    if before != after or before != copied:
        raise RuntimeError("launch copy hash mismatch")
    return str(target), {"source": source, "target": str(target), "sha256": copied,
                         "source_size": Path(source).stat().st_size, "target_size": target.stat().st_size}


def wait_exit(client: DnSpyClient, target: str) -> tuple[dict, dict]:
    launch = call(client, "debug_launch", {
        "request_id": rid(), "target_path": target, "expected_sha256": sha256(target),
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "none"})
    row = payload(launch)
    session_id = str(row.get("session_id", ""))
    selected: dict = {}
    all_events: list[dict] = []
    deadline = time.monotonic() + 40
    while session_id and time.monotonic() < deadline and not selected:
        waited = call(client, "debug_wait_event", {"session_id": session_id, "after_cursor": 0,
            "limit": 50, "kinds": ["start_failed", "process_exited", "exception", "module_load_failed"],
            "timeout_ms": 2500})
        events = payload(waited).get("events", [])
        if isinstance(events, list):
            all_events = [e for e in events if isinstance(e, dict)]
            selected = next((e for e in all_events if e.get("kind") in
                             ("start_failed", "process_exited", "exception", "module_load_failed")), {})
    print("DYNAMIC-EVIDENCE " + json.dumps({"launch": launch, "events": all_events}, ensure_ascii=False), flush=True)
    if session_id:
        call(client, "debug_terminate", {"session_id": session_id,
            "generation": int(row.get("generation", 0)), "request_id": rid()})
    return launch, selected


def begin(client: DnSpyClient, assembly: str, mvid: str = "") -> tuple[str, int, dict]:
    args = {"assembly_name": assembly, "request_id": rid()}
    if mvid:
        args["module_mvid"] = mvid
    envelope = call(client, "edit_begin", args)
    row = payload(envelope).get("transaction", {})
    return str(row.get("transaction_id", "")), int(row.get("work_revision", 0)), envelope


def main() -> int:
    if not LAUNCH_ROOT:
        check("C0 isolated launch root configured", False, "configure_isolation was not called")
        return 1
    client = DnSpyClient(URL, client_name=f"t026-acc016-causal-{ARCH}", timeout=180)
    client.initialize()
    opened = call(client, "open_files", {"paths": [FIXTURE, INBOUND, UNSIGNED]})
    check("P0 isolated fixtures opened", opened.get("loaded_count") == 3 and opened.get("failed_count") == 0, opened)

    original_identity = identity(FIXTURE)
    original_verify = sn_verify(FIXTURE)
    original_sha = sha256(FIXTURE)
    check("P1 fixture is cryptographically strong-named",
          len(str(original_identity.get("public_key_token", ""))) == 16 and original_verify["exit_code"] == 0,
          {"identity": original_identity, "verify": original_verify})

    tx, revision, begun = begin(client, "StrongHost")
    source = payload(begun).get("source", {})
    host_mvid = str(source.get("mvid", ""))
    applied = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "operation": {"kind": "module_update", "name": "StrongHostEdited"}})
    revision = int(payload(applied).get("transaction", {}).get("work_revision", revision))
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review = payload(reviewed).get("review", {})
    required = list(review.get("required_confirmation_ids") or [])
    committed = call(client, "edit_commit", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "review_id": str(review.get("review_id", "")),
        "review_revision": revision, "confirmed_risk_ids": required})
    commit = payload(committed)
    lineage_id = str(commit.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit.get("checkpoint", {}).get("checkpoint_id", ""))
    check("D1 default edit commits", bool(committed.get("ok")) and bool(lineage_id), committed)
    exported = call(client, "edit_export", {"request_id": rid(), "lineage_id": lineage_id,
        "checkpoint_id": checkpoint_id, "output_path": r"edit-output\acc016-causal\StrongHost-edited.exe"})
    output = payload(exported).get("output", {})
    exported_path = str(output.get("path", ""))
    exported_identity = identity(exported_path) if exported_path else {}
    exported_verify = sn_verify(exported_path) if exported_path else {"exit_code": -1}
    check("D2 default path preserves strong-name identity fields",
          all(original_identity.get(k) == exported_identity.get(k)
              for k in ("name", "version", "culture", "flags", "public_key_token"))
          and sha256(exported_path) != original_sha,
          {"original": original_identity, "exported": exported_identity})
    edited_copy, copy_facts = copy_verified(exported_path, "StrongHost-edited.exe")
    edited_run = direct_run(edited_copy)
    print("IDENTITY-EVIDENCE " + json.dumps({"original_sha256": original_sha,
        "original_identity": original_identity, "original_sn_verify": original_verify,
        "exported_sha256": sha256(exported_path), "exported_identity": exported_identity,
        "exported_sn_verify": exported_verify, "copy": copy_facts, "direct_run": edited_run},
        ensure_ascii=False), flush=True)

    # Negative causal control: the original is cryptographically valid and its
    # program deliberately returns 3.  Its process_exited event cannot prove a
    # strong-name loader failure.
    valid_copy, valid_copy_facts = copy_verified(FIXTURE, "StrongHost-valid.exe")
    launch, event = wait_exit(client, valid_copy)
    check("F1 valid signed control yields an observable normal exit",
          bool(launch.get("ok")) and event.get("kind") == "process_exited",
          {"copy": valid_copy_facts, "event": event})
    session_id = str(payload(launch).get("session_id", ""))
    cursor = int(event.get("cursor", 0))
    event_kind = str(event.get("kind", ""))

    tx2, revision2, _ = begin(client, "StrongHost", host_mvid)
    ghost = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx2,
        "expected_revision": revision2, "operation": {"kind": "strong_name_remove",
        "dynamic_failure": {"session_id": "ghost", "event_cursor": 999, "event_kind": "start_failed"}}})
    check("B1 nonexistent evidence rejects", not ghost.get("ok"), ghost)
    unrelated = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx2,
        "expected_revision": revision2, "operation": {"kind": "strong_name_remove",
        "dynamic_failure": {"session_id": session_id, "event_cursor": cursor, "event_kind": event_kind}}})
    check("B2 unrelated normal exit cannot unlock strong-name removal", not unrelated.get("ok"), unrelated)
    if unrelated.get("ok"):
        revision2 = int(payload(unrelated).get("transaction", {}).get("work_revision", revision2))
        impact_env = call(client, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx2,
            "expected_revision": revision2})
        impact = payload(impact_env).get("impact", {})
        inbound = impact.get("inbound_references", [])
        exact = [row for row in inbound if isinstance(row, dict) and row.get("module") == "InboundStrong"
                 and row.get("matched_name") == "StrongHost" and str(row.get("assembly_ref_token", "")).startswith("0x23")]
        check("B3 actual loaded inbound strong reference is exact", impact.get("scope") == "loaded_modules"
              and len(exact) == 1 and bool(exact[0].get("risk_id")), impact)
    rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})
    check("B4 invalid causal branch was not committed", bool(rolled.get("ok")), rolled)

    tx3, revision3, _ = begin(client, "StrongHost", host_mvid)
    replay = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx3,
        "expected_revision": revision3, "operation": {"kind": "strong_name_remove",
        "dynamic_failure": {"session_id": session_id, "event_cursor": cursor, "event_kind": event_kind}}})
    check("B5 consumed evidence replay rejects", not replay.get("ok"), replay)
    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx3})

    # Explicit capability boundary: removal is inapplicable to an unsigned
    # target.  A second unrelated exit must not turn that no-op into success.
    unsigned_copy, _ = copy_verified(UNSIGNED, "UnsignedHost-valid.exe")
    unsigned_launch, unsigned_event = wait_exit(client, unsigned_copy)
    unsigned_session = str(payload(unsigned_launch).get("session_id", ""))
    tx4, revision4, _ = begin(client, "UnsignedHost")
    unavailable = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx4,
        "expected_revision": revision4, "operation": {"kind": "strong_name_remove",
        "dynamic_failure": {"session_id": unsigned_session,
            "event_cursor": int(unsigned_event.get("cursor", 0)),
            "event_kind": str(unsigned_event.get("kind", ""))}}})
    check("S1 inapplicable unsigned target returns capability limit",
          not unavailable.get("ok") and error_code(unavailable) == "EDIT_CAPABILITY_UNAVAILABLE", unavailable)
    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx4})

    print("T026-BOUNDARY " + json.dumps({"arch": ARCH, "default_lineage_id": lineage_id,
        "default_checkpoint_id": checkpoint_id, "invalid_signature_runtime_enforced": edited_run["exit_code"] != 3,
        "unrelated_evidence_response": unrelated, "unsigned_response": unavailable}, ensure_ascii=False), flush=True)
    print(f"ACC016C {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
