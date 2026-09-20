#!/usr/bin/env python3
"""P07 ACC-015: cross-assembly impact scan over the loaded modules.  A rename
transaction on ImportHost (with TestIL loaded alongside carrying an inbound
reference after the P07 fixture prep) is scanned: the report must carry
scope=loaded_modules with the ACTUAL module list, inbound references with risk
ids; unconfirmed risks block commit; confirmation is echoed in the commit
result; structural errors (wrong transaction) cannot be bypassed."""

from __future__ import annotations

import json
import hashlib
import re
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe"
INBOUND = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\InboundRef.exe"
STORE: Path | None = None
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE, INBOUND, STORE
    context.validate()
    URL = context.mcp_url
    FIXTURE = context.fixture("ImportHost/ImportHost.exe")
    INBOUND = context.fixture("ImportHost/InboundRef.exe")
    STORE = Path(context.checkpoint_store)


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


def directory_snapshot(path: Path | None) -> dict:
    if path is None:
        return {"configured": False, "files": [], "manifest_sha256": ""}
    rows = []
    if path.is_dir():
        for item in sorted((p for p in path.rglob("*") if p.is_file()), key=lambda p: str(p).lower()):
            data = item.read_bytes()
            rows.append({"path": str(item.relative_to(path)).replace("\\", "/"),
                         "length": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    encoded = json.dumps(rows, sort_keys=True, separators=(",", ":")).encode()
    return {"configured": True, "files": rows, "manifest_sha256": hashlib.sha256(encoded).hexdigest()}


def method_token(client: DnSpyClient) -> str:
    for delay in (0.0, 0.1, 0.25, 0.5, 1.0):
        if delay:
            time.sleep(delay)
        response = call(client, "list_methods", {
            "assembly_name": "ImportHost", "type_full_name": "ImportHost.Program"})
        items = response.get("items") or response.get("Items") or []
        row = next((item for item in items if isinstance(item, dict)
                    and str(item.get("name") or item.get("Name")) == "Main"), None)
        raw = row.get("token") if isinstance(row, dict) else None
        if raw is None and isinstance(row, dict):
            raw = row.get("Token")
        if isinstance(raw, str) and raw.startswith("0x"):
            return raw.lower()
        if raw is not None:
            return f"0x{int(raw):08x}"
    return ""


def main() -> int:
    client = DnSpyClient(URL, client_name="p07-acc015", timeout=120)
    client.initialize()
    # both modules loaded: the edit target and a module referencing it by name
    opened = call(client, "open_files", {"paths": [FIXTURE, INBOUND]})
    check("L1 both modules loaded", "error" not in opened, json.dumps(opened)[:240])
    main_token = method_token(client)
    check("L2 structural target resolved", bool(re.fullmatch(r"0x06[0-9a-f]{6}", main_token)), main_token)
    loaded = call(client, "list_assemblies", {})
    loaded_rows = loaded.get("assemblies") or loaded.get("items") or loaded.get("Items") or []
    loaded_names = [str(row.get("name") or row.get("Name")) for row in loaded_rows if isinstance(row, dict)]
    # dnSpy's tree includes its automatically loaded corlib module even though
    # the public assembly list reports only opened document assemblies.
    expected_scanned = [name for name in loaded_names if name != "ImportHost"] + ["mscorlib"]

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
    check("S1 actual module list", sorted(module_names) == sorted(expected_scanned)
          and len(module_names) == len(expected_scanned),
          json.dumps({"loaded": loaded_names, "reported": module_names}))
    check("S1 unloaded module not claimed", "TestIL" not in loaded_names and "TestIL" not in module_names,
          json.dumps({"loaded": loaded_names, "reported": module_names}))
    module_counts = {str(row.get("name")): row.get("inbound_reference_count")
                     for row in module_rows if isinstance(row, dict)}
    check("S1 module count matches inbound facts", module_counts.get("InboundRef") == 1
          and all(count == 0 for name, count in module_counts.items() if name != "InboundRef"),
          json.dumps(module_rows))
    inbound_rows = impact.get("inbound_references", [])
    inbound_risk_ids = [str(row.get("risk_id")) for row in inbound_rows if isinstance(row, dict)]
    token_text = str(inbound_rows[0].get("assembly_ref_token", "")) if len(inbound_rows) == 1 else ""
    expected_inbound_risk = ("risk-cross_assembly_inbound-InboundRef-"
                             + str(int(token_text, 16) & 0x00ffffff)) if re.fullmatch(r"0x23[0-9a-f]{6}", token_text) else ""
    check("S1 inbound reference reported", len(inbound_rows) == 1
          and inbound_rows[0].get("module") == "InboundRef"
          and inbound_rows[0].get("matched_name") == "ImportHost"
          and bool(expected_inbound_risk) and inbound_risk_ids == [expected_inbound_risk],
          json.dumps(inbound_rows)[:300])
    check("S1 risk ids echoed", set(inbound_risk_ids) == {str(r) for r in (impact.get("risk_ids") or [])}, str(impact.get("risk_ids")))
    text = json.dumps(scanned, ensure_ascii=False).lower()
    check("S1 no global claim", "global" not in text and "complete" not in text and "gac" not in text and "filesystem" not in text, "")

    # Adjacent protocol regressions are retained, but do not stand in for the
    # real metadata/IL structural rejection below.
    wrong = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": "edit-does-not-exist", "expected_revision": revision})
    check("S2 wrong transaction rejected", envelope_error(wrong) == "EDIT_TRANSACTION_NOT_FOUND", json.dumps(wrong)[:200])
    stale = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 77})
    check("S2 revision mismatch rejected", envelope_error(stale) == "EDIT_REVISION_CONFLICT", json.dumps(stale)[:200])

    # S2: high-risk edit is reviewable, blocked without confirmation, and the
    # successful result must echo the full review/impact fact rather than IDs.
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    check("S3 review lists inbound risk", any(r.startswith("risk-cross_assembly_inbound-") for r in required), str(required))
    review_risks = payload(reviewed).get("risks") or []
    matching_review = [row for row in review_risks if isinstance(row, dict)
                       and row.get("risk_id") in inbound_risk_ids]
    check("S3 review risk matches impact", len(matching_review) == 1
          and matching_review[0].get("kind") == "cross_assembly_inbound"
          and matching_review[0].get("object") == "loaded_modules"
          and matching_review[0].get("confirmation_required") is True
          and set(inbound_risk_ids).issubset(required), json.dumps(matching_review)[:300])
    blocked = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": []})
    check("S3 unconfirmed risk blocks commit", envelope_error(blocked) == "EDIT_RISK_CONFIRMATION_REQUIRED", json.dumps(blocked)[:240])

    # S3: a schema-valid method_body_replace creates a genuinely invalid IL
    # stack.  The private mutation is rejected by the structural validator;
    # revision, private/live fingerprints and checkpoint directory stay exact.
    before_invalid = payload(call(client, "edit_status", {}))
    before_store = directory_snapshot(STORE)
    invalid = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "method_body_replace", "target": {"token": main_token},
                      "body": {"init_locals": False, "max_stack": 1, "locals": [],
                               "exception_handlers": [],
                               "instructions": [{"opcode": "pop"}, {"opcode": "ret"}]}}})
    after_invalid = payload(call(client, "edit_status", {}))
    after_store = directory_snapshot(STORE)
    before_fp = before_invalid.get("fingerprints", {})
    after_fp = after_invalid.get("fingerprints", {})
    check("S3 structural IL rejected at apply", envelope_error(invalid) == "EDIT_VALIDATION_FAILED",
          json.dumps(invalid)[:300])
    check("S3 structural rejection preserves revision", after_invalid.get("transaction", {}).get("work_revision") == revision,
          json.dumps(after_invalid)[:240])
    check("S3 structural rejection preserves live/private", before_fp.get("current_live") == after_fp.get("current_live")
          and before_fp.get("private") == after_fp.get("private"), json.dumps({"before": before_fp, "after": after_fp}))
    check("S3 structural rejection has no store side effect", before_store == after_store,
          json.dumps({"before": before_store, "after": after_store})[:500])

    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    confirmed_rows = [row for row in (commit_row.get("confirmed_risks") or []) if isinstance(row, dict)]
    confirmed = [str(row.get("risk_id")) for row in confirmed_rows]
    confirmed_inbound = [row for row in confirmed_rows if row.get("risk_id") in inbound_risk_ids]
    check("S4 commit echoes confirmation", bool(committed.get("ok")) and set(confirmed) == set(required)
          and len(confirmed_inbound) == 1
          and len(matching_review) == 1
          and confirmed_inbound[0].get("kind") == matching_review[0].get("kind")
          and confirmed_inbound[0].get("object") == matching_review[0].get("object")
          and confirmed_inbound[0].get("description") == matching_review[0].get("description")
          and confirmed_inbound[0].get("affected_references") == inbound_rows[0],
          json.dumps(committed)[:300])
    final_store = directory_snapshot(STORE)
    commit_fp = commit_row.get("fingerprints", {})
    check("S4 commit reports real fingerprint transition", bool(commit_fp.get("before"))
          and bool(commit_fp.get("after")) and commit_fp.get("before") != commit_fp.get("after"),
          json.dumps(commit_fp))
    check("S4 committed package recorded", len(final_store.get("files", [])) == 1
          and str(final_store["files"][0]["path"]).endswith(".dnspy-mcp-checkpoints"),
          json.dumps(final_store)[:500])
    print("INFO ACC015_EVIDENCE " + json.dumps({
        "impact": impact, "review_required": required, "review_risks": matching_review,
        "invalid_error": invalid.get("error"), "invalid_before_fingerprints": before_fp,
        "invalid_after_fingerprints": after_fp, "store_before_invalid": before_store,
        "store_after_invalid": after_store, "commit_fingerprints": commit_fp,
        "confirmed_risks": confirmed_rows, "final_store": final_store,
    }, ensure_ascii=False, sort_keys=True), flush=True)

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
