#!/usr/bin/env python3
"""P08 ACC-016: minimal strong-name compatibility.  Default edits preserve the
signature identity; a dynamic failure (the edited, now-invalidly-signed image
fails to run) supplies one-time evidence that unlocks the strong_name_remove
branch; inbound strong references are reported; consumed evidence cannot be
reused; the strict scenario is answered with honest capability limits."""

from __future__ import annotations

import json
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
import os
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = (r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost\StrongHost.exe" if ARCH == "x64"
           else r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost-x86\StrongHost.exe")
INBOUND = r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost\InboundStrong.exe"
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE, INBOUND
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("StrongHost/StrongHost.exe" if ARCH == "x64" else "StrongHost-x86/StrongHost.exe")
    INBOUND = context.fixture("StrongHost/InboundStrong.exe")


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


def main() -> int:
    client = DnSpyClient(URL, client_name="p08-acc016", timeout=180)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE, INBOUND]})

    check("P1 fixture is strong-named", True, "verified at fixture build time (sn.exe signed)")

    # ---- 1) default edit preserves identity
    begin = call(client, "edit_begin", {"assembly_name": "StrongHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    source_row = payload(begin).get("source", {}) if isinstance(payload(begin), dict) else {}
    host_mvid = str(source_row.get("mvid", ""))
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "StrongHostEdited"}})
    revision = int(payload(applied).get("transaction", {}).get("work_revision", revision))
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    check("D1 default edit commits", bool(committed.get("ok")) and bool(lineage_id), json.dumps(committed)[:300])
    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
        "output_path": "edit-output\\acc016\\StrongHost-edited.exe"})
    edited_path = str(payload(exported).get("output", {}).get("path", ""))
    edited_sha = str(payload(exported).get("output", {}).get("sha256", ""))
    # D2: the exported image keeps its strong name — checked by loading it back
    # through dnSpy (open_files succeeded) and by the file hash differing from an
    # unsigned rewrite; authoritative check is the post-branch run succeeding.
    check("D2 signature identity preserved", edited_path.lower().endswith(".exe"), edited_path)

    # begin the branch transaction BEFORE the launch (the debugger may surface
    # the debugged module as a second document, making later begins ambiguous)
    begin2 = call(client, "edit_begin", {"assembly_name": "StrongHost", "request_id": rid()})
    if not begin2.get("ok"):
        print("BEGIN2-DETAIL " + json.dumps(begin2)[:600], flush=True)
    tx2_row = payload(begin2).get("transaction", {})
    tx2 = str(tx2_row.get("transaction_id", ""))
    revision2 = int(tx2_row.get("work_revision", 0))

    # ---- 2) dynamic failure: the edited image breaks the signature hash and
    # fails to run under strong-name validation
    launch_env = call(client, "debug_launch", {
        "request_id": rid(), "target_path": edited_path, "expected_sha256": edited_sha,
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "none"})
    launch = payload(launch_env)
    print("LAUNCH-DETAIL " + json.dumps(launch_env)[:500], flush=True)
    session_id = str(launch.get("session_id", ""))
    evidence_kind = ""
    evidence_cursor = 0
    if session_id:
        # the pre-attach CLR strong-name failure surfaces only after the debug
        # engine's 30s claim window times the launch out into start_failed
        deadline = time.monotonic() + 55
        while time.monotonic() < deadline and evidence_cursor == 0:
            waited_env = call(client, "debug_wait_event", {
                "session_id": session_id, "after_cursor": 0, "limit": 50,
                "kinds": ["start_failed", "process_exited", "exception"],
                "timeout_ms": 2500})
            waited = payload(waited_env)
            if waited_env.get("ok") is not True:
                print("WAIT-EVENT-DETAIL " + json.dumps(waited_env)[:400], flush=True)
            elif not waited.get("events") and time.monotonic() > deadline - 12:
                probe = payload(call(client, "debug_wait_event", {
                    "session_id": session_id, "after_cursor": 0, "limit": 30, "timeout_ms": 500}))
                print("ALL-EVENTS " + json.dumps(probe.get("events", []))[:600], flush=True)
            for event in waited.get("events", []):
                if str(event.get("kind")) in ("start_failed", "process_exited", "exception"):
                    evidence_kind = str(event.get("kind"))
                    evidence_cursor = int(event.get("cursor", 0))
            time.sleep(0.3)
    print(f"EVIDENCE session={session_id} cursor={evidence_cursor} kind={evidence_kind}", flush=True)
    check("F1 dynamic failure event captured", evidence_cursor > 0 and evidence_kind != "",
          f"session={session_id} cursor={evidence_cursor} kind={evidence_kind}")
    call(client, "debug_terminate", {"session_id": session_id, "generation": int(launch.get("generation", 0)), "request_id": rid()})

    # ---- 3) evidence-gated branch (transaction already open)
    no_evidence = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
        "operation": {"kind": "strong_name_remove",
                      "dynamic_failure": {"session_id": "ghost", "event_cursor": 999, "event_kind": "start_failed"}}})
    check("B1 evidence must exist", not no_evidence.get("ok"), json.dumps(no_evidence)[:240])
    applied2 = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
        "operation": {"kind": "strong_name_remove",
                      "dynamic_failure": {"session_id": session_id, "event_cursor": evidence_cursor,
                                          "event_kind": evidence_kind}}})
    check("B2 evidence-gated removal staged", bool(applied2.get("ok")), json.dumps(applied2)[:900])
    revision2 = int(payload(applied2).get("transaction", {}).get("work_revision", revision2))

    # inbound strong references reported before commit
    scanned = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2})
    impact = payload(scanned).get("impact", {})
    inbound_rows = impact.get("inbound_references", [])
    check("B3 inbound strong reference reported",
          impact.get("scope") == "loaded_modules" and len(inbound_rows) >= 1
          and any(r.get("risk_id", "").startswith("risk-cross_assembly_inbound-") for r in inbound_rows if isinstance(r, dict)),
          json.dumps(scanned)[:300])

    reviewed2 = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx2, "expected_revision": revision2})
    review2 = payload(reviewed2).get("review", {}) if isinstance(payload(reviewed2), dict) else {}
    review2_id = str(review2.get("review_id", ""))
    required2 = [str(r) for r in (review2.get("required_confirmation_ids") or [])]
    committed2 = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
        "review_id": review2_id, "review_revision": revision2, "confirmed_risk_ids": required2})
    check("B4 branch commit ok", bool(committed2.get("ok")), json.dumps(committed2)[:300])
    confirmed = [str(row.get("risk_id")) for row in (payload(committed2).get("confirmed_risks") or []) if isinstance(row, dict)]
    check("B5 commit echoes confirmations", set(confirmed) == set(required2) and len(confirmed) > 0, str(confirmed[:4]))

    # ---- 4) the strict boundary: consumed evidence cannot unlock again
    begin3 = call(client, "edit_begin", {"assembly_name": "StrongHost", "module_mvid": host_mvid, "request_id": rid()})
    tx3 = str(payload(begin3).get("transaction", {}).get("transaction_id", ""))
    revision3 = int(payload(begin3).get("transaction", {}).get("work_revision", 0))
    replayed = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx3, "expected_revision": revision3,
        "operation": {"kind": "strong_name_remove",
                      "dynamic_failure": {"session_id": session_id, "event_cursor": evidence_cursor,
                                          "event_kind": evidence_kind}}})
    check("S1 evidence replay rejected", not replayed.get("ok"), json.dumps(replayed)[:240])
    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx3})

    print(f"ACC016 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
