"""S02-STRONGNAME-DEFER-01: current public refusal, not ACC-016 positive acceptance."""
from __future__ import annotations

import hashlib
import json
import os
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = (r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost\StrongHost.exe" if ARCH == "x64"
           else r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost-x86\StrongHost.exe")
INBOUND = r"C:\Tools\mcp-repo\tests\fixtures\bin\StrongHost\InboundStrong.exe"


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE, INBOUND
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("StrongHost/StrongHost.exe" if ARCH == "x64" else "StrongHost-x86/StrongHost.exe")
    INBOUND = context.fixture("StrongHost/InboundStrong.exe")


def rid() -> str:
    return str(uuid.uuid4())


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        response = client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                response = json.loads(text[start:])
            except json.JSONDecodeError:
                response = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}
        else:
            response = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}
    print(json.dumps({"tool": tool, "args": args, "response": response}, ensure_ascii=False), flush=True)
    return response


def result(row: dict) -> dict:
    value = row.get("result")
    return value if isinstance(value, dict) else {}


def check(condition: bool, name: str, row: object = None) -> None:
    print(json.dumps({"check": name, "pass": bool(condition), "detail": row}, ensure_ascii=False, default=str), flush=True)
    if not condition:
        raise AssertionError(name)


def main() -> int:
    client = DnSpyClient(URL, client_name=f"t083-strong-name-{ARCH}", timeout=180)
    client.initialize()
    before_file = hashlib.sha256(Path(FIXTURE).read_bytes()).hexdigest()
    opened = call(client, "open_files", {"paths": [FIXTURE, INBOUND]})
    check(opened.get("loaded_count") == 2 and opened.get("failed_count") == 0, "signed fixture and inbound opened", opened)
    begun = call(client, "edit_begin", {"assembly_name": "StrongHost", "request_id": rid()})
    check(begun.get("ok") is True, "begin", begun)
    transaction = result(begun).get("transaction", {})
    tx = transaction.get("transaction_id", "")
    revision = transaction.get("work_revision", 0)
    source = result(begun).get("source", {})
    check(source.get("file_sha256", "").lower() == before_file, "source identity", source)
    baseline = call(client, "edit_status", {})
    shapes = {
        "normal": {"kind": "strong_name_remove", "dynamic_failure": {"session_id": "absent", "event_cursor": 1, "event_kind": "exception"}},
        "forged": {"kind": "strong_name_remove", "dynamic_failure": {"session_id": "framework-frame-forgery", "event_cursor": 2147483647, "event_kind": "exception"}},
        "old_candidate": {"kind": "strong_name_remove", "dynamic_failure": {"session_id": "legacy-observation", "event_cursor": 2, "event_kind": "exception"}},
    }
    try:
        for name, operation in shapes.items():
            response = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
                "expected_revision": revision, "operation": operation})
            error = response.get("error", {})
            details = error.get("details", {}) if isinstance(error, dict) else {}
            check(response.get("ok") is False and error.get("code") == "EDIT_CAPABILITY_UNAVAILABLE"
                  and details.get("capability") == "strong_name_remove" and "deferred" in details.get("reason", ""),
                  name + " business refusal", response)
            now = call(client, "edit_status", {})
            check(result(now).get("transaction", {}).get("work_revision") == revision
                  and result(now).get("fingerprints") == result(baseline).get("fingerprints"),
                  name + " transaction unchanged", now)
        empty_scan = call(client, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx,
            "expected_revision": revision})
        empty_impact = result(empty_scan).get("impact", {})
        check(empty_scan.get("ok") is True and empty_impact.get("scope") == "loaded_modules"
              and empty_impact.get("identity_operations") == []
              and empty_impact.get("inbound_references") == [] and empty_impact.get("risk_ids") == []
              and any(row.get("name") == "InboundStrong" and row.get("inbound_reference_count") == 0
                      for row in empty_impact.get("modules", []) if isinstance(row, dict)),
              "no staged identity gives empty impact with scanned scope", empty_scan)
        ordinary = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
            "expected_revision": revision, "operation": {"kind": "module_update", "name": "StrongHostEdited"}})
        check(ordinary.get("ok") is True, "unrelated edit usable", ordinary)
        revision = result(ordinary).get("transaction", {}).get("work_revision", revision)
        scan = call(client, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx,
            "expected_revision": revision})
        impact = result(scan).get("impact", {})
        check(scan.get("ok") is True and impact.get("scope") == "loaded_modules"
              and any(row.get("kind") == "module_update" and row.get("operation_index") == 0
                      for row in impact.get("identity_operations", []) if isinstance(row, dict))
              and any(row.get("matched_name") == "StrongHost" and row.get("operation_indices") == [0]
                      for row in impact.get("inbound_references", []) if isinstance(row, dict)),
              "inbound impact after staged identity remains available", scan)
    finally:
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        check(rolled.get("ok") is True, "rollback", rolled)
    check(hashlib.sha256(Path(FIXTURE).read_bytes()).hexdigest() == before_file,
          "source disk unchanged")
    print("T083 DEFERRED NEGATIVE PASS; ACC-016 POSITIVE UNFINISHED", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
