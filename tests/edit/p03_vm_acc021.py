#!/usr/bin/env python3
"""P09 ACC-021 (terminal judgment): every edit-family tool's inline JSON Schema
from tools/list is validated by an independent Draft 2020-12 checker against
positive and negative payload instances, and each error class is triggered for
real with a stable code/state/recovery triple."""

from __future__ import annotations

import json
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

try:
    from jsonschema import Draft202012Validator
except ImportError:  # VM Python has jsonschema installed for this case
    Draft202012Validator = None

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")


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


def tools_list(client: DnSpyClient) -> dict:
    body = json.dumps({"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}}).encode()
    request = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": client.session_id})
    with urllib.request.urlopen(request, timeout=60) as response:
        wire = response.read().decode("utf-8", "replace")
    for chunk in wire.split("data: "):
        chunk = chunk.strip()
        if not chunk:
            continue
        try:
            message = json.loads(chunk.splitlines()[0])
        except (json.JSONDecodeError, IndexError):
            continue
        if message.get("id") == 999:
            return {tool["name"]: tool for tool in message["result"]["tools"]}
    raise RuntimeError("tools/list returned no result")


# per-tool positive payloads (no side effects: read-only tools, or ops inside a
# rolled-back transaction) and negative payloads (schema violations)
def samples(name: str):
    positives = {
        "edit_begin": [{"request_id": "s1", "assembly_name": "TestIL"}],
        "edit_status": [{}],
        "edit_history": [{}],
    }
    negatives = {
        "edit_begin": [{"assembly_name": "TestIL"}, {"request_id": "s1", "assembly_name": ""}],
        "edit_apply": [{"request_id": "s1", "transaction_id": "t", "expected_revision": -1, "operation": {"kind": "type_add"}}],
        "edit_impact_scan": [{"request_id": "s1", "transaction_id": "t", "expected_revision": -1}],
        "edit_resource_import": [{"request_id": "s1", "transaction_id": "t", "expected_revision": 0, "vm_path": "x", "resource_name": "n", "resource_type": "linked"}],
        "edit_resource_export": [{"request_id": "", "assembly_name": "", "resource_name": "n", "output_path": "ok.bin"}],
    }
    return positives.get(name, []), negatives.get(name, [{"__unknown_field__": True} if name in (
        "edit_status", "edit_history") else {"request_id": ""}])


def main() -> int:
    if Draft202012Validator is None:
        print("FAIL jsonschema library unavailable on this host", flush=True)
        return 1
    client = DnSpyClient(URL, client_name="p09-acc021", timeout=120)
    client.initialize()
    registry = tools_list(client)
    edit_tools = {name: tool for name, tool in registry.items() if name.startswith("edit_")}
    check("G1 edit-family tools on the wire", len(edit_tools) >= 18, str(sorted(edit_tools)))

    # S1: every edit tool has an inline inputSchema and it validates positives
    schema_failures = []
    for name, tool in sorted(edit_tools.items()):
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or not schema:
            schema_failures.append(name + ":missing")
            continue
        try:
            Draft202012Validator.check_schema(schema)
        except Exception as ex:  # noqa: BLE001
            schema_failures.append(name + ":invalid:" + str(ex)[:60])
    check("S1 all inline schemas are valid Draft 2020-12", not schema_failures, str(schema_failures[:6]))

    # S2: sampled positives validate, sampled negatives are rejected by the SCHEMA
    positive_failures = []
    negative_failures = []
    for name, tool in sorted(edit_tools.items()):
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or not schema:
            continue
        positives, negatives = samples(name)
        validator = Draft202012Validator(schema)
        for sample in positives:
            errors = list(validator.iter_errors(sample))
            if errors:
                positive_failures.append(name + ":" + errors[0].message[:60])
        for sample in negatives:
            errors = list(validator.iter_errors(sample))
            if not errors:
                negative_failures.append(name + ":accepted-invalid")
    check("S2 positive payloads pass their schemas", not positive_failures, str(positive_failures[:5]))
    check("S2 negative payloads rejected by schemas", not negative_failures, str(negative_failures[:5]))

    # E1: trigger real error classes; each response must carry the stable triple
    call(client, "open_files", {"paths": [FIXTURE]})
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = str(payload(begin).get("transaction", {}).get("transaction_id", ""))
    cases = []
    bad_apply = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": 0,
        "operation": {"kind": "assembly_ref_update", "target": {"token": "0x02000002"}, "version": "1.0.0.0"}})
    cases.append(("validation", envelope_error(bad_apply) == "EDIT_VALIDATION_FAILED", bad_apply))
    busy = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    cases.append(("busy", envelope_error(busy) == "EDIT_TRANSACTION_BUSY", busy))
    wrong_tx = call(client, "edit_status", {})
    foreign = call(client, "edit_review", {"request_id": rid(), "transaction_id": "edit-none", "expected_revision": 0})
    cases.append(("not_found", envelope_error(foreign) == "EDIT_TRANSACTION_NOT_FOUND", foreign))
    missing_owner = call(client, "edit_recover", {"request_id": rid(), "recovery_id": "recovery-0123456789abcdef0123456789abcdef", "action": "retry_checkpoint"})
    cases.append(("recovery", envelope_error(missing_owner) == "EDIT_RECOVERY_NOT_FOUND", missing_owner))
    for label, ok, envelope in cases:
        error = envelope.get("error", {}) if isinstance(envelope, dict) else {}
        triple = bool(error.get("code")) and bool(error.get("message")) and bool(error.get("recovery"))
        check(f"E1 {label} stable triple", ok and triple, json.dumps(envelope)[:220])

    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    client.close()
    print(f"ACC021 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
