#!/usr/bin/env python3
"""P08 ACC-007: managed/Win32 resources through the real loopback.  Standard
.resources entries edit and survive commit+export+reload; a custom-serialized
object is only ever metadata + whole-blob replacement (the sentinel file must
never appear); the icon group relationship stays valid (dangling rejects)."""

from __future__ import annotations

import base64
import json
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ResourceHost\ResourceHost.dll"
SENTINEL = r"C:\Tools\dnspy-mcp-edit-tests\p08-sentinel.flag"
FAILURES: list[str] = []
PASSES: list[str] = []


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


def main() -> int:
    client = DnSpyClient(URL, client_name="p08-acc007", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    begin = call(client, "edit_begin", {"assembly_name": "ResourceHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    check("T1 transaction began", bool(tx), json.dumps(begin)[:200])

    def apply_op(operation: dict) -> dict:
        nonlocal revision
        envelope = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "operation": operation})
        row = payload(envelope).get("transaction")
        if isinstance(row, dict):
            revision = int(row.get("work_revision", revision))
        return envelope

    # standard entries: string / i4 / r8 / bool / bytes — one edit each
    edits = [
        {"name": "greeting", "value_kind": "string", "value": "changed-by-p08"},
        {"name": "number", "value_kind": "i4", "value": 1337},
        {"name": "ratio", "value_kind": "r8", "value": 9.75},
        {"name": "enabled", "value_kind": "boolean", "value": False},
        {"name": "payload", "value_kind": "bytes", "value": base64.b64encode(bytes([3, 1, 4])).decode()},
    ]
    for entry in edits:
        applied = apply_op({"kind": "managed_resource_update",
                            "target": {"name": "ResourceHost.Strings.resources"}, "entry": entry})
        check(f"A1 entry {entry['name']}", bool(applied.get("ok")), json.dumps(applied)[:260])

    # custom serialized object: metadata-only view + whole-blob replacement
    applied = apply_op({"kind": "managed_resource_update",
                        "target": {"name": "ResourceHost.Strings.resources"},
                        "entry": {"name": "ghost", "value_kind": "string", "value": "x"}})
    check("R1 custom entry edit rejected", not applied.get("ok"), json.dumps(applied)[:240])
    replacement = base64.b64encode(b"P08-WHOLE-BLOB").decode()
    applied = apply_op({"kind": "managed_resource_add", "name": "ResourceHost.Replaced.resources",
                        "data_base64": replacement})
    check("R2 whole-blob add", bool(applied.get("ok")), json.dumps(applied)[:240])

    # icon group (fixture rows: RT_ICON id 2, RT_GROUP_ICON id 32512 referencing icon 2)
    dangling = apply_op({"kind": "win32_resource_remove", "type_id": 3, "name_id": 2,
                         "remove_mode": "reject_if_referenced"})
    rejected_for_dangling = (not dangling.get("ok")) and "dangle" in json.dumps(dangling)
    check("I1 referenced icon removal rejected", rejected_for_dangling, json.dumps(dangling)[:300])
    applied = apply_op({"kind": "win32_resource_remove", "type_id": 14, "name_id": 32512,
                        "remove_mode": "reject_if_referenced"})
    check("I2 group removal ok", bool(applied.get("ok")), json.dumps(applied)[:300])
    applied = apply_op({"kind": "win32_resource_remove", "type_id": 3, "name_id": 2,
                        "remove_mode": "reject_if_referenced"})
    check("I3 icon removal after group ok", bool(applied.get("ok")), json.dumps(applied)[:300])

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
        "output_path": "edit-output\\acc007\\ResourceHost-p08.dll"})
    output_row = payload(exported).get("output", {})
    export_path = str(output_row.get("path", ""))
    sha256 = str(output_row.get("sha256", ""))
    check("X1 export ok", bool(exported.get("ok")) and export_path.lower().endswith(".dll"), json.dumps(exported)[:240])

    # authoritative readback through edit_resource_export + byte assertions
    exported_resource = call(client, "edit_resource_export", {
        "request_id": rid(), "assembly_name": "ResourceHost",
        "resource_name": "ResourceHost.Replaced.resources",
        "output_path": "edit-output\\acc007\\Replaced.resources"})
    resource_row = payload(exported_resource).get("export", {})
    check("E1 resource export identity",
          bool(exported_resource.get("ok"))
          and int(resource_row.get("length", 0)) == len("P08-WHOLE-BLOB")
          and str(resource_row.get("sha256", "")) == __import__("hashlib").sha256(b"P08-WHOLE-BLOB").hexdigest(),
          json.dumps(exported_resource)[:300])

    strings_export = call(client, "edit_resource_export", {
        "request_id": rid(), "assembly_name": "ResourceHost",
        "resource_name": "ResourceHost.Strings.resources",
        "output_path": "edit-output\\acc007\\Strings.resources"})
    check("E2 strings export ok", bool(strings_export.get("ok")), json.dumps(strings_export)[:600])
    # reopen last: after this, two modules share the assembly name
    reopened = call(client, "open_files", {"paths": [export_path]})
    check("L2 exported image reopened", "error" not in reopened, json.dumps(reopened)[:200])

    # CON-019 sentinel: nothing may ever instantiate the custom object
    import os
    check("S1 sentinel never fired", not os.path.exists(SENTINEL), SENTINEL)

    print(f"ACC007 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
