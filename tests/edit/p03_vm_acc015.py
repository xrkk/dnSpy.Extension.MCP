#!/usr/bin/env python3
"""P07 ACC-015: cross-assembly impact scan over the loaded modules.  A rename
transaction on ImportHost (with TestIL loaded alongside carrying an inbound
reference after the P07 fixture prep) is scanned: the report must carry
scope=loaded_modules with the ACTUAL module list, inbound references with risk
ids; unconfirmed risks block commit; confirmation is echoed in the commit
result; structural errors (wrong transaction) cannot be bypassed."""

from __future__ import annotations

import json
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe"
INBOUND = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\InboundRef.exe"
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE, INBOUND
    URL = context.mcp_url
    FIXTURE = context.fixture("ImportHost/ImportHost.exe")
    INBOUND = context.fixture("ImportHost/InboundRef.exe")


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


def envelope_error(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def main() -> int:
    client = DnSpyClient(URL, client_name="p07-acc015", timeout=120)
    client.initialize()
    # both modules loaded: the edit target and a module referencing it by name
    opened = call(client, "open_files", {"paths": [FIXTURE, INBOUND]})
    check("L1 both modules loaded", "error" not in opened, json.dumps(opened)[:240])

    begin = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    check("T1 transaction began", bool(tx), json.dumps(begin)[:200])

    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "assembly_update", "name": "ImportHostRenamed"}})
    revision = int(payload(applied).get("transaction", {}).get("work_revision", revision))
    check("A1 rename staged", bool(applied.get("ok")), json.dumps(applied)[:240])

    # S1 scan: scope + actual module list + inbound reference + risk id
    scanned = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    impact = payload(scanned).get("impact", {})
    check("S1 scan ok", bool(scanned.get("ok")), json.dumps(scanned)[:300])
    check("S1 scope loaded_modules", impact.get("scope") == "loaded_modules", str(impact.get("scope")))
    module_rows = impact.get("modules", [])
    module_names = [str(row.get("name")) for row in module_rows if isinstance(row, dict)]
    check("S1 actual module list", len(module_names) >= 1 and "ImportHost" not in module_names, str(module_names))
    inbound_rows = impact.get("inbound_references", [])
    inbound_risk_ids = [str(row.get("risk_id")) for row in inbound_rows if isinstance(row, dict)]
    check("S1 inbound reference reported", len(inbound_rows) >= 1 and all(r.startswith("risk-cross_assembly_inbound-") for r in inbound_risk_ids),
          json.dumps(inbound_rows)[:300])
    check("S1 risk ids echoed", set(inbound_risk_ids) == {str(r) for r in (impact.get("risk_ids") or [])}, str(impact.get("risk_ids")))
    text = json.dumps(scanned, ensure_ascii=False).lower()
    check("S1 no global claim", "global" not in text and "complete" not in text and "gac" not in text and "filesystem" not in text, "")

    # S2 structural error: wrong transaction id cannot be scanned/confirmed
    wrong = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": "edit-does-not-exist", "expected_revision": revision})
    check("S2 wrong transaction rejected", envelope_error(wrong) == "EDIT_TRANSACTION_NOT_FOUND", json.dumps(wrong)[:200])
    stale = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 77})
    check("S2 revision mismatch rejected", envelope_error(stale) == "EDIT_REVISION_CONFLICT", json.dumps(stale)[:200])

    # S3 unconfirmed inbound risk blocks commit
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    check("S3 review lists inbound risk", any(r.startswith("risk-cross_assembly_inbound-") for r in required), str(required))
    blocked = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": []})
    check("S3 unconfirmed risk blocks commit", envelope_error(blocked) == "EDIT_RISK_CONFIRMATION_REQUIRED", json.dumps(blocked)[:240])

    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    confirmed = [str(row.get("risk_id")) for row in (commit_row.get("confirmed_risks") or []) if isinstance(row, dict)]
    check("S4 commit echoes confirmation", bool(committed.get("ok")) and set(confirmed) == set(required),
          json.dumps(committed)[:300])

    # S5 empty scan after the fact: new transaction without identity ops
    begin2 = call(client, "edit_begin", {"assembly_name": "ImportHostRenamed", "request_id": rid()})
    tx2_row = payload(begin2).get("transaction", {})
    tx2 = str(tx2_row.get("transaction_id", ""))
    revision2 = int(tx2_row.get("work_revision", 0))
    if tx2:
        applied2 = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
            "operation": {"kind": "assembly_update", "name": "ImportHostRestored"}})
        revision2 = int(payload(applied2).get("transaction", {}).get("work_revision", revision2))
        restored = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2,
            "operation": {"kind": "assembly_update", "name": "ImportHostRenamed", "version": "1.0.0.0", "culture": None}})
        _ = restored
        scanned2 = call(client, "edit_impact_scan", {
            "request_id": rid(), "transaction_id": tx2, "expected_revision": revision2})
        impact2 = payload(scanned2).get("impact", {})
        check("S5 empty scan keeps scope", impact2.get("scope") == "loaded_modules"
              and isinstance(impact2.get("modules"), list), json.dumps(impact2)[:240])
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})

    print(f"ACC015 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
