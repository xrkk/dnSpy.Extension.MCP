#!/usr/bin/env python3
"""P09 ACC-021 (terminal judgment): every edit-family tool's inline JSON Schema
from tools/list is validated by an independent Draft 2020-12 checker against
positive and negative payload instances, and each error class is triggered for
real with a stable code/state/recovery triple.

T032 strict pass adds (RACC-021 / CON-024 / P09 IMP-007):
  * per-tool schema self-check for BOTH inputSchema and outputSchema plus an
    explicit dangling-$defs reference-closure audit,
  * >=1 positive and >=1 negative schema instance for EVERY edit tool,
  * the edit_apply operation kinds interface_add / reference_add must validate
    against the live schema (no stale 37-kind rejection) and an unknown kind
    must be rejected by the schema itself,
  * real RPC structuredContent captured pre-conversion (object type recorded,
    never string-promoted) and validated against the tool's declared
    outputSchema; tools without outputSchema are listed, never claimed,
  * real error-class triggers with the stable code / business state /
    recovery triple taken from the formal status fields, including
    EDIT_OWNER_REQUIRED (sessionless), EDIT_OWNER_MISMATCH + two-session
    lease non-overwrite, EDIT_CAPABILITY_UNAVAILABLE, EDIT_CAPACITY_EXCEEDED
    (compile artifact registrations), EDIT_HISTORY_CONFLICT, and
    EDIT_REVISION_CONFLICT.
"""

from __future__ import annotations

import json
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

try:
    import jsonschema
    from jsonschema import Draft202012Validator
except ImportError:  # VM Python has jsonschema installed for this case
    jsonschema = None
    Draft202012Validator = None

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []
WORK_DIR = ""           # isolation work root for the strict-report artifacts
SEAM_NOTES: list[str] = []
RAW_WIRE: list[dict] = []
RAW_SEQ = {"n": 0}
BLOB_FILE = "t032-blob.bin"  # small fixture file for edit_resource_import


def configure_isolation(context) -> None:
    global URL, FIXTURE, WORK_DIR
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")
    work_root = getattr(context, "work_root", "")
    if work_root:
        WORK_DIR = str(work_root)
        Path(WORK_DIR).mkdir(parents=True, exist_ok=True)


def rid() -> str:
    return str(uuid.uuid4())


def hex32(seed: str) -> str:
    import hashlib

    return hashlib.sha256(seed.encode("utf-8")).hexdigest()[:32]


class _BisectStepFailed(Exception):
    """Internal: a bisect chain step failed; roll the transaction back."""


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        PASSES.append(name)
        print(f"PASS {name}", flush=True)
    else:
        FAILURES.append(name)
        print(f"FAIL {name} {detail}", flush=True)


def record_raw(tool: str, args, result=None, note: str = "", parsed_override=None) -> None:
    """Capture the pre-conversion wire shape of one tools/call result.

    ``wire.structured_content`` holds the raw structuredContent exactly as it
    arrived (object, string, or absent).  ``parsed`` is the driver's envelope
    view and may come from the text fallback — it is debug context only and is
    never sufficient for the A2 gate (see validate_structured).
    """
    RAW_SEQ["n"] += 1
    row = {"seq": RAW_SEQ["n"], "tool": tool, "args": args, "note": note}
    if isinstance(result, dict):
        structured = result.get("structuredContent")
        content = result.get("content")
        text = ""
        if isinstance(content, list) and content and isinstance(content[0], dict):
            text = str(content[0].get("text", ""))
        row["wire"] = {
            "isError": result.get("isError"),
            "structured_content_present": "structuredContent" in result,
            "structured_content_type": type(structured).__name__,
            "structured_content": structured,
            "content_text": text,
        }
        parsed = structured if isinstance(structured, dict) else None
        if parsed is None and text:
            try:
                candidate = json.loads(text)
                if isinstance(candidate, dict):
                    parsed = candidate
                    row["wire"]["text_fallback_parsed"] = True
            except json.JSONDecodeError:
                pass
        if parsed is not None:
            row["parsed"] = parsed
    elif isinstance(parsed_override, dict):
        # JSON-RPC-level failure: no tool result object exists at all, so there
        # is no raw structuredContent to gate on.  The envelope is kept for the
        # error matrix only.
        row["wire"] = {"isError": None, "structured_content_present": None,
                       "structured_content_type": "jsonrpc-exception",
                       "structured_content": None,
                       "content_text": ""}
        row["parsed"] = parsed_override
    RAW_WIRE.append(row)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    """Call a tool and return the edit envelope while keeping the raw result.

    Uses the raw JSON-RPC request (not call_tool/call_tool_json) so isError
    results are captured with their structuredContent intact instead of being
    collapsed into a client-side exception.  The public client behavior is
    untouched; this is a driver-local capture path.
    """
    try:
        result = client.request("tools/call", {"name": tool, "arguments": dict(args)})
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        envelope = None
        if start >= 0:
            try:
                candidate = json.loads(text[start:])
                envelope = candidate if isinstance(candidate, dict) else None
            except json.JSONDecodeError:
                envelope = None
        record_raw(tool, args, None, note="jsonrpc-exception:" + text[:200], parsed_override=envelope)
        if envelope is not None:
            return envelope
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}
    if not isinstance(result, dict):
        record_raw(tool, args, None, note="non-dict result:" + str(result)[:200])
        return {"ok": False, "error": {"code": "DRIVER_EMPTY_RESULT", "message": str(result)[:200]}}
    record_raw(tool, args, result)
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    content = result.get("content")
    if isinstance(content, list) and content and isinstance(content[0], dict):
        text = str(content[0].get("text", ""))
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            return {"ok": False, "error": {"code": "DRIVER_TEXT_PAYLOAD", "message": text[:300]}}
    return {"ok": False, "error": {"code": "DRIVER_EMPTY_RESULT", "message": json.dumps(result)[:200]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def envelope_error(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def error_triple(envelope: dict) -> dict:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    error = error if isinstance(error, dict) else {}
    return {
        "code": str(error.get("code", "")),
        "message": str(error.get("message", "")),
        "current_state": str(error.get("current_state", "")),
        "recovery": str(error.get("recovery", "")),
        "top_state": str(envelope.get("state", "")) if isinstance(envelope, dict) else "",
        "ok": envelope.get("ok") if isinstance(envelope, dict) else None,
    }


def _decode_rpc_response(wire: str, want_id):
    """Decode a JSON-RPC body that is either plain JSON or an SSE stream."""
    candidates = [wire.strip()]
    for chunk in wire.split("data: "):
        stripped = chunk.strip()
        if stripped and stripped not in candidates:
            candidates.append(stripped.splitlines()[0] if stripped.splitlines() else stripped)
    for candidate in candidates:
        try:
            message = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if isinstance(message, dict) and message.get("id") == want_id:
            return message
    raise RuntimeError(f"no JSON-RPC response with id {want_id}")


def tools_list(client: DnSpyClient) -> dict:
    # Routed through the client's own transport (same connection behavior as
    # every other call): a fresh urllib opener was refused by the loopback
    # listener on some hosts even while the session was healthy.
    response = client.request_object(
        {"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}})
    wire = response.body.decode("utf-8", "replace")
    if WORK_DIR:
        Path(WORK_DIR, "tools-list-raw.txt").write_text(wire, encoding="utf-8")
    message = _decode_rpc_response(wire, 999)
    return {tool["name"]: tool for tool in message["result"]["tools"]}


def sessionless_call(tool: str, args: dict) -> dict:
    """Plain-HTTP tools/call with no initialized session (owner-required probe)."""
    body = json.dumps({"jsonrpc": "2.0", "id": 7001, "method": "tools/call",
                       "params": {"name": tool, "arguments": args}}).encode()
    request = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
    with urllib.request.urlopen(request, timeout=60) as response:
        wire = response.read().decode("utf-8", "replace")
    RAW_SEQ["n"] += 1
    row = {"seq": RAW_SEQ["n"], "tool": tool, "args": args,
           "note": "sessionless plain HTTP tools/call (no Mcp-Session-Id)", "raw": wire[:4000]}
    RAW_WIRE.append(row)
    message = _decode_rpc_response(wire, 7001)
    if isinstance(message.get("error"), dict):
        return {"ok": False, "jsonrpc_error": message["error"]}
    result = message.get("result") or {}
    row["wire"] = {
        "isError": result.get("isError"),
        "structured_content_present": "structuredContent" in result,
        "structured_content_type": type(result.get("structuredContent")).__name__,
        "content_text": (result.get("content") or [{}])[0].get("text", "") if isinstance(result.get("content"), list) else "",
    }
    content = result.get("content")
    if isinstance(content, list) and content and isinstance(content[0], dict):
        try:
            return json.loads(content[0].get("text", ""))
        except json.JSONDecodeError:
            return {"ok": False, "raw_text": content[0].get("text", "")[:300]}
    return {"ok": result.get("isError") is not True, "result": result}


def collect_refs(node, defs: dict, found: list) -> None:
    if isinstance(node, dict):
        ref = node.get("$ref")
        if isinstance(ref, str):
            found.append(ref)
        for value in node.values():
            collect_refs(value, defs, found)
    elif isinstance(node, list):
        for value in node:
            collect_refs(value, defs, found)


def ref_closure_ok(schema: dict) -> tuple[bool, list[str]]:
    """CON-024: no $ref may dangle against the schema's own $defs."""
    defs = schema.get("$defs") if isinstance(schema.get("$defs"), dict) else {}
    found: list[str] = []
    collect_refs(schema, defs, found)
    dangling: list[str] = []
    for ref in found:
        if not ref.startswith("#"):
            dangling.append(ref + ":external")
            continue
        node = schema
        ok = True
        for part in ref.lstrip("#/").split("/"):
            if not isinstance(node, dict) or part not in node:
                ok = False
                break
            node = node[part]
        if not ok:
            dangling.append(ref)
    return not dangling, dangling[:8]


# ---------------------------------------------------------------------------
# Schema-instance samples.  Positives are hand-derived from the documented
# tool contracts and the known-good acceptance-driver payloads; negatives are
# the historical rejection categories (missing required field, empty required
# string, negative revision, unknown field, wrong type, unknown operation
# kind) sampled per tool.
# ---------------------------------------------------------------------------

LINEAGE_ID = "lineage-" + hex32("t032-lineage")
CHECKPOINT_ID = "checkpoint-" + hex32("t032-checkpoint")
FAMILY_ID = "family-" + hex32("t032-family")
TX_ID = "edit-" + hex32("t032-transaction")
COMPILE_ID = "compile-" + hex32("t032-compile")
REVIEW_ID = "review-" + hex32("t032-review")


def compile_source(marker: str) -> str:
    return (
        "namespace TestIL\n{\n    public static class Simple\n    {\n"
        "        public static int AddOne(int x) => x + 1;\n"
        f"        public static int {marker}() => 41;\n"
        "    }\n}\n"
    )


POSITIVES: dict[str, list[dict]] = {
    "edit_begin": [{"request_id": "s1", "assembly_name": "TestIL"}],
    "edit_status": [{}],
    "edit_history": [{}, {"lineage_id": LINEAGE_ID, "page_size": 10}],
    "edit_compile": [{"request_id": "s1", "assembly_name": "TestIL", "compilation_kind": "edit_class",
                      "documents": [{"path": "Simple.cs", "content": compile_source("T032Added")}]}],
    "edit_apply": [
        {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
         "operation": {"kind": "module_update", "name": "T032Module"}},
        {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
         "operation": {"kind": "interface_add", "owner_type": {"token": "0x02000002"},
                       "interface": {"type": {"Kind": "ClassSig"}}}},
        {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
         "operation": {"kind": "reference_add", "reference": {"form": "assembly_ref",
                                                              "name": "System.Runtime"}}},
    ],
    "edit_import": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
                     "compile_id": COMPILE_ID, "targets": [
                         {"compiled": "TestIL.Simple::T032Added()", "action": "add"}]}],
    "edit_impact_scan": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0}],
    "edit_resource_import": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
                              "vm_path": r"C:\fixtures\t032-blob.bin",
                              "resource_name": "TestIL.T032.resources", "resource_type": "embedded"}],
    "edit_resource_export": [{"request_id": "s1", "assembly_name": "TestIL",
                              "resource_name": "TestIL.T032.resources",
                              "output_path": r"edit-output\t032\T032.resources"}],
    "edit_review": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0}],
    "edit_rollback": [{"request_id": "s1", "transaction_id": TX_ID}],
    "edit_commit": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
                     "review_id": REVIEW_ID, "review_revision": 0, "confirmed_risk_ids": []}],
    "edit_undo": [{"request_id": "s1", "lineage_id": LINEAGE_ID,
                   "expected_checkpoint_id": CHECKPOINT_ID}],
    "edit_redo": [{"request_id": "s1", "lineage_id": LINEAGE_ID,
                   "expected_checkpoint_id": CHECKPOINT_ID}],
    "edit_restore": [{"request_id": "s1", "lineage_id": LINEAGE_ID,
                      "checkpoint_id": CHECKPOINT_ID, "action": "assess"}],
    "edit_export": [{"request_id": "s1", "lineage_id": LINEAGE_ID,
                     "checkpoint_id": CHECKPOINT_ID, "output_path": r"edit-output\t032\out.dll"}],
    "edit_recover": [{"request_id": "s1",
                      "recovery_id": "recovery-" + hex32("t032-recovery"),
                      "action": "retry_checkpoint"}],
    "edit_accept_live": [{"request_id": "s1", "assembly_name": "TestIL",
                          "source_family_id": FAMILY_ID, "superseded_lineage_id": LINEAGE_ID,
                          "expected_live_fingerprint": "0" * 64, "acknowledge_new_baseline": True}],
}

NEGATIVES: dict[str, list[dict]] = {
    "edit_begin": [{"assembly_name": "TestIL"}, {"request_id": "s1", "assembly_name": ""}],
    "edit_status": [{"__unknown_field__": True}, {"busy": "yes"}],
    "edit_history": [{"page_size": -1}, {"__unknown_field__": 1}],
    "edit_compile": [{"request_id": "s1", "assembly_name": "TestIL", "compilation_kind": "edit_class",
                      "documents": []},
                     {"request_id": "s1", "assembly_name": "TestIL",
                      "compilation_kind": "edit_class"}],
    "edit_apply": [
        {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": -1,
         "operation": {"kind": "type_add"}},
        {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
         "operation": {"kind": "type_teleport"}},
    ],
    "edit_import": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": -1}],
    "edit_impact_scan": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": -1},
                         {"request_id": "", "transaction_id": TX_ID, "expected_revision": 0}],
    "edit_resource_import": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
                              "vm_path": "x", "resource_name": "n", "resource_type": "linked"}],
    "edit_resource_export": [{"request_id": "", "assembly_name": "", "resource_name": "n",
                              "output_path": "ok.bin"}],
    "edit_review": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": -5}],
    "edit_rollback": [{"request_id": "", "transaction_id": ""}],
    "edit_commit": [{"request_id": "s1", "transaction_id": TX_ID, "expected_revision": -1,
                     "review_id": REVIEW_ID, "review_revision": 0, "confirmed_risk_ids": []}],
    "edit_undo": [{"request_id": "s1"}, {"request_id": "s1", "lineage_id": "lineage-bogus",
                                         "expected_checkpoint_id": "checkpoint-bogus"}],
    "edit_redo": [{"request_id": "s1", "lineage_id": "lineage-bogus"}],
    "edit_restore": [{"request_id": "s1", "lineage_id": LINEAGE_ID,
                      "checkpoint_id": CHECKPOINT_ID, "action": "detonate"}],
    "edit_export": [{"request_id": "s1", "lineage_id": "lineage-bogus",
                     "checkpoint_id": CHECKPOINT_ID, "output_path": ""}],
    "edit_recover": [{"request_id": "s1", "recovery_id": "recovery-bogus",
                      "action": "retry_checkpoint"}],
    "edit_accept_live": [{"request_id": "s1", "assembly_name": "TestIL",
                          "source_family_id": FAMILY_ID, "superseded_lineage_id": LINEAGE_ID,
                          "expected_live_fingerprint": "not-a-fingerprint",
                          "acknowledge_new_baseline": True}],
}


def samples(name: str):
    """Backwards-compatible sample accessor used by the S2 checks."""
    return POSITIVES.get(name, []), NEGATIVES.get(name, [])


# ---------------------------------------------------------------------------
# Live strict matrix
# ---------------------------------------------------------------------------

def validate_structured(tool: str, result_row: dict, output_schemas: dict, out_rows: list) -> None:
    """A2 gate (strict): the RAW structuredContent must be present and itself a
    JSON object, and that object must conform to the tool's declared
    outputSchema (which is a success/error union).  A text-fallback parse is
    debug context and can never satisfy the gate.  When no raw result exists at
    all (JSON-RPC-level failure) the row is unverified, never a pass."""
    schema = output_schemas.get(tool)
    wire = result_row.get("wire") or {}
    structured = wire.get("structured_content")
    row = {
        "tool": tool,
        "note": result_row.get("note", ""),
        "structured_content_present": wire.get("structured_content_present"),
        "structured_content_type": wire.get("structured_content_type"),
        "isError": wire.get("isError"),
        "has_output_schema": schema is not None,
        "schema_errors": [],
    }
    if wire.get("structured_content_type") == "jsonrpc-exception":
        row["verdict"] = "UNVERIFIED_NO_RAW_RESULT"
        out_rows.append(row)
        return
    if schema is None:
        row["verdict"] = "NO_OUTPUT_SCHEMA"
        out_rows.append(row)
        return
    if wire.get("structured_content_present") is not True or not isinstance(structured, dict):
        row["verdict"] = "FAIL_STRUCTURED_SHAPE"
        row["schema_errors"] = [
            f"structuredContent present={wire.get('structured_content_present')} "
            f"type={wire.get('structured_content_type')} (text fallback is not admissible)"]
        out_rows.append(row)
        return
    errors = [f"{list(e.absolute_path)}: {e.message[:120]}"
              for e in Draft202012Validator(schema).iter_errors(structured)]
    row["schema_errors"] = errors[:8]
    row["verdict"] = "CONFORMS" if not errors else "SCHEMA_VIOLATION"
    out_rows.append(row)


def last_raw(tool: str) -> dict:
    for row in reversed(RAW_WIRE):
        if row.get("tool") == tool and "wire" in row:
            return row
    return {}


def main() -> int:
    if Draft202012Validator is None:
        print("FAIL jsonschema library unavailable on this host", flush=True)
        return 1
    try:
        from importlib.metadata import version as _package_version
        validator_version = _package_version("jsonschema")
    except Exception:  # noqa: BLE001
        validator_version = getattr(jsonschema, "__version__", "unknown")
    strict: dict = {
        "validator": {"library": "jsonschema", "version": validator_version,
                      "draft": "Draft202012Validator"},
        "input_schema_checks": [], "positive_checks": [], "negative_checks": [],
        "kind_enum": {}, "output_schema_rows": [], "error_matrix": [],
        "live_success_rows": [], "unverified": [], "seam_notes": SEAM_NOTES,
    }

    client = DnSpyClient(URL, client_name="p09-acc021", timeout=120)
    client.initialize()
    registry = tools_list(client)
    edit_tools = {name: tool for name, tool in registry.items() if name.startswith("edit_")}
    check("G1 edit-family tools on the wire", len(edit_tools) >= 18, str(sorted(edit_tools)))

    output_schemas: dict[str, dict] = {}
    for name, tool in sorted(edit_tools.items()):
        if isinstance(tool.get("outputSchema"), dict) and tool["outputSchema"]:
            output_schemas[name] = tool["outputSchema"]

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
            continue
        refs_ok, dangling = ref_closure_ok(schema)
        strict["input_schema_checks"].append(
            {"tool": name, "check_schema": "ok", "$defs": sorted((schema.get("$defs") or {}).keys()),
             "ref_closure": "ok" if refs_ok else "dangling", "dangling_refs": dangling})
        if not refs_ok:
            schema_failures.append(name + ":dangling-refs:" + ",".join(dangling[:3]))
        out_schema = tool.get("outputSchema")
        if isinstance(out_schema, dict) and out_schema:
            try:
                Draft202012Validator.check_schema(out_schema)
                out_refs_ok, out_dangling = ref_closure_ok(out_schema)
                strict["input_schema_checks"][-1]["output_check_schema"] = "ok"
                strict["input_schema_checks"][-1]["output_ref_closure"] = "ok" if out_refs_ok else "dangling"
                if not out_refs_ok:
                    schema_failures.append(name + ":output-dangling-refs")
            except Exception as ex:  # noqa: BLE001
                schema_failures.append(name + ":output-invalid:" + str(ex)[:60])
    check("S1 all inline schemas are valid Draft 2020-12", not schema_failures, str(schema_failures[:6]))
    check("A1a all 18 edit tools declare an outputSchema",
          len(output_schemas) == len(edit_tools) and len(edit_tools) >= 18,
          f"edit_tools={len(edit_tools)} with_outputSchema={len(output_schemas)}")

    # S2: sampled positives validate, sampled negatives are rejected by the SCHEMA
    positive_failures = []
    negative_failures = []
    missing_samples = []
    for name, tool in sorted(edit_tools.items()):
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or not schema:
            continue
        positives, negatives = samples(name)
        if not positives:
            missing_samples.append(name + ":no-positive")
        if not negatives:
            missing_samples.append(name + ":no-negative")
        validator = Draft202012Validator(schema)
        pos_rows, neg_rows = [], []
        for sample in positives:
            errors = list(validator.iter_errors(sample))
            pos_rows.append({"sample": sample, "verdict": "valid" if not errors else errors[0].message[:120]})
            if errors:
                positive_failures.append(name + ":" + errors[0].message[:60])
        for sample in negatives:
            errors = list(validator.iter_errors(sample))
            neg_rows.append({"sample": sample, "verdict": "rejected" if errors else "ACCEPTED-INVALID"})
            if not errors:
                negative_failures.append(name + ":accepted-invalid")
        strict["positive_checks"].append({"tool": name, "rows": pos_rows})
        strict["negative_checks"].append({"tool": name, "rows": neg_rows})
    check("S2 positive payloads pass their schemas", not positive_failures, str(positive_failures[:5]))
    check("S2 negative payloads rejected by schemas", not negative_failures, str(negative_failures[:5]))
    check("A1b every edit tool has >=1 positive and >=1 negative instance",
          not missing_samples, str(missing_samples[:6]))

    # A1c: the live edit_apply schema must carry the full 39-kind operation set
    apply_schema = edit_tools.get("edit_apply", {}).get("inputSchema") or {}
    branches = (((apply_schema.get("properties") or {}).get("operation") or {}).get("oneOf") or [])
    live_kinds = []
    for branch in branches:
        if isinstance(branch, dict):
            kind_node = (branch.get("properties") or {}).get("kind") or {}
            value = kind_node.get("const")
            if value is None and isinstance(kind_node.get("enum"), list) and len(kind_node["enum"]) == 1:
                value = kind_node["enum"][0]
            if isinstance(value, str):
                live_kinds.append(value)
    strict["kind_enum"] = {"branch_count": len(branches), "kinds": sorted(live_kinds)}
    check("A1c edit_apply schema carries 39 operation kinds", len(live_kinds) == 39,
          f"count={len(live_kinds)} missing={sorted(set(['interface_add', 'reference_add']) - set(live_kinds))}")
    check("A1c interface_add and reference_add accepted by live schema",
          "interface_add" in live_kinds and "reference_add" in live_kinds, str(sorted(live_kinds)[-6:]))
    unknown_kind = {"request_id": "s1", "transaction_id": TX_ID, "expected_revision": 0,
                    "operation": {"kind": "type_teleport", "name": "X"}}
    unknown_errors = list(Draft202012Validator(apply_schema).iter_errors(unknown_kind)) if apply_schema else [True]
    check("A1c unknown operation kind rejected by schema itself", bool(unknown_errors),
          "schema accepted kind=type_teleport")

    # ------------------------------------------------------------------
    # Live RPC matrix on the isolated TestIL fixture.
    # ------------------------------------------------------------------
    call(client, "open_files", {"paths": [FIXTURE]})

    # Phase 0 - idle read-only tools.
    status_idle = call(client, "edit_status", {})
    validate_structured("edit_status", last_raw("edit_status"), output_schemas, strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_status", "scenario": "idle status",
                                        "ok": bool(status_idle.get("ok"))})
    check("L1 edit_status idle ok", bool(status_idle.get("ok")), json.dumps(status_idle)[:160])
    history_empty = call(client, "edit_history", {})
    validate_structured("edit_history", last_raw("edit_history"), output_schemas, strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_history", "scenario": "empty history",
                                        "ok": bool(history_empty.get("ok"))})
    check("L1 edit_history ok", bool(history_empty.get("ok")), json.dumps(history_empty)[:160])

    # Phase 1 - compile surface (capability + capacity + first artifact).
    first_compile = None
    compile_ok_count = 0
    for index in range(9):
        marker = f"T032Added{index}"
        envelope = call(client, "edit_compile", {
            "request_id": rid(), "assembly_name": "TestIL", "compilation_kind": "edit_class",
            "documents": [{"path": "Simple.cs", "content": compile_source(marker)}]})
        if index == 0:
            first_compile = envelope
            validate_structured("edit_compile", last_raw("edit_compile"), output_schemas,
                                strict["output_schema_rows"])
            compile_row = payload(first_compile).get("compile", {})
            strict["live_success_rows"].append({
                "tool": "edit_compile", "scenario": "edit_class compile of TestIL.Simple",
                "ok": bool(first_compile.get("ok")) and bool(compile_row.get("success")),
                "compile_id": compile_row.get("compile_id", "")})
            check("L1 edit_compile ok", bool(first_compile.get("ok")) and bool(compile_row.get("success")),
                  json.dumps(first_compile)[:240])
        if envelope.get("ok") and payload(envelope).get("compile", {}).get("success"):
            compile_ok_count += 1
        elif index < 8:
            strict["unverified"].append({"tool": "edit_compile",
                                         "item": f"compile registration {index}",
                                         "reason": "compile did not succeed: " + json.dumps(envelope)[:200]})
    capacity_envelope = call(client, "edit_compile", {
        "request_id": rid(), "assembly_name": "TestIL", "compilation_kind": "edit_class",
        "documents": [{"path": "Simple.cs", "content": compile_source("T032AddedCapacity")}]})
    capacity_triple = error_triple(capacity_envelope)
    strict["error_matrix"].append({"class": "EDIT_CAPACITY_EXCEEDED", "trigger":
                                   "9th distinct successful compile registration (limit 8)",
                                   "triple": capacity_triple,
                                   "successful_compiles_before": compile_ok_count})
    check("E1 capacity stable triple",
          envelope_error(capacity_envelope) == "EDIT_CAPACITY_EXCEEDED"
          and bool(capacity_triple["current_state"]) and bool(capacity_triple["recovery"]),
          json.dumps(capacity_triple)[:260])

    capability_envelope = call(client, "edit_compile", {
        "request_id": rid(), "assembly_name": "TestIL", "compilation_kind": "edit_class",
        "documents": [{"path": "Simple.cs", "content": compile_source("T032AddedCap")}],
        "references_override": [r"C:\absent\t032-missing-reference.dll"]})
    capability_triple = error_triple(capability_envelope)
    strict["error_matrix"].append({"class": "EDIT_CAPABILITY_UNAVAILABLE",
                                   "trigger": "references_override points at a nonexistent file",
                                   "triple": capability_triple})
    check("E1 capability stable triple",
          envelope_error(capability_envelope) == "EDIT_CAPABILITY_UNAVAILABLE"
          and bool(capability_triple["current_state"]) and bool(capability_triple["recovery"]),
          json.dumps(capability_triple)[:260])

    # Phase 2 - owner-required via a sessionless plain-HTTP tools/call.
    owner_envelope = sessionless_call("edit_compile", {
        "request_id": rid(), "assembly_name": "TestIL", "compilation_kind": "edit_class",
        "documents": [{"path": "Simple.cs", "content": compile_source("T032AddedOwner")}]})
    owner_triple = error_triple(owner_envelope)
    strict["error_matrix"].append({"class": "EDIT_OWNER_REQUIRED",
                                   "trigger": "tools/call over plain HTTP without an initialized session",
                                   "triple": owner_triple})
    check("E1 owner_required stable triple",
          envelope_error(owner_envelope) == "EDIT_OWNER_REQUIRED"
          and bool(owner_triple["current_state"]) and bool(owner_triple["recovery"]),
          json.dumps(owner_triple)[:260])

    # Phase 3 - rollback probe (clean begin/rollback pair).
    probe_begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    probe_tx = str(payload(probe_begin).get("transaction", {}).get("transaction_id", ""))
    validate_structured("edit_begin", last_raw("edit_begin"), output_schemas, strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_begin", "scenario": "begin on TestIL",
                                        "ok": bool(probe_begin.get("ok")) and bool(probe_tx)})
    check("L1 edit_begin ok", bool(probe_begin.get("ok")) and bool(probe_tx),
          json.dumps(probe_begin)[:200])
    if probe_tx:
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": probe_tx})
        validate_structured("edit_rollback", last_raw("edit_rollback"), output_schemas,
                            strict["output_schema_rows"])
        strict["live_success_rows"].append({"tool": "edit_rollback",
                                            "scenario": "rollback of the probe transaction",
                                            "ok": bool(rolled.get("ok"))})
        check("L1 edit_rollback ok", bool(rolled.get("ok")), json.dumps(rolled)[:200])

    # Phase 4 - the main transaction chain.
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = str(payload(begin).get("transaction", {}).get("transaction_id", ""))
    revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
    if not tx:
        print(f"main begin failed: {json.dumps(begin)[:300]}", flush=True)
        return 1

    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "T032StrictModule"}})
    validate_structured("edit_apply", last_raw("edit_apply"), output_schemas, strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_apply", "scenario": "module_update",
                                        "ok": bool(applied.get("ok"))})
    check("L1 edit_apply module_update ok", bool(applied.get("ok")), json.dumps(applied)[:240])
    if applied.get("ok"):
        revision = int(payload(applied).get("transaction", {}).get("work_revision", revision))

    # Two-session lease: a second session must not steal or overwrite the tx.
    lease = DnSpyClient(URL, client_name="p09-acc021-lease", timeout=120)
    lease.initialize()
    busy = call(lease, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    busy_triple = error_triple(busy)
    strict["error_matrix"].append({"class": "EDIT_TRANSACTION_BUSY",
                                   "trigger": "second MCP session edit_begin while tx active",
                                   "triple": busy_triple})
    cases = []
    cases.append(("busy", envelope_error(busy) == "EDIT_TRANSACTION_BUSY", busy))
    mismatch = call(lease, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "module_update", "name": "T032LeaseSteal"}})
    mismatch_triple = error_triple(mismatch)
    strict["error_matrix"].append({"class": "EDIT_OWNER_MISMATCH",
                                   "trigger": "second session edit_apply on the owner's tx",
                                   "triple": mismatch_triple})
    cases.append(("owner_mismatch", envelope_error(mismatch) == "EDIT_OWNER_MISMATCH", mismatch))
    for label, ok, envelope in cases:
        error = envelope.get("error", {}) if isinstance(envelope, dict) else {}
        triple = bool(error.get("code")) and bool(error.get("message")) and bool(error.get("recovery"))
        check(f"E1 {label} stable triple", ok and triple, json.dumps(envelope)[:220])

    # The owner keeps exclusive control: another apply from session A works.
    owner_again = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_add", "name": "TestIL.T032.resources",
                      "data_base64": "VDAzMlJFU09VUkNFQkxPQg=="}})
    check("E1 lease not stolen: owner apply still succeeds", bool(owner_again.get("ok")),
          json.dumps(owner_again)[:220])
    strict["error_matrix"].append({"class": "LEASE_NON_OVERWRITE",
                                   "trigger": "owner apply after foreign-session rejection",
                                   "triple": {"code": "", "ok": owner_again.get("ok"),
                                              "revision_before": revision,
                                              "note": "owner retains the transaction after the "
                                                      "second session was rejected"}})
    if owner_again.get("ok"):
        revision = int(payload(owner_again).get("transaction", {}).get("work_revision", revision))

    # edit_import from the first compiled artifact.
    compile_id = str(payload(first_compile).get("compile", {}).get("compile_id", "")) if first_compile else ""
    imported = None
    if compile_id:
        imported = call(client, "edit_import", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "compile_id": compile_id,
            "targets": [{"compiled": "TestIL.Simple::T032Added0()", "action": "add"}]})
        validate_structured("edit_import", last_raw("edit_import"), output_schemas,
                            strict["output_schema_rows"])
        strict["live_success_rows"].append({"tool": "edit_import",
                                            "scenario": "add compiled T032Added0 method",
                                            "ok": bool(imported.get("ok"))})
        check("L1 edit_import ok", bool(imported.get("ok")), json.dumps(imported)[:260])
        if imported.get("ok"):
            revision = int(payload(imported).get("transaction", {}).get("work_revision", revision))
    else:
        strict["unverified"].append({"tool": "edit_import",
                                     "reason": "no successful compile artifact available"})

    # edit_resource_import from a fixture file under the isolated sample root.
    blob_path = str(Path(FIXTURE).parent / BLOB_FILE)
    res_imported = call(client, "edit_resource_import", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "vm_path": blob_path, "resource_name": "TestIL.T032b.resources",
        "resource_type": "embedded"})
    validate_structured("edit_resource_import", last_raw("edit_resource_import"), output_schemas,
                        strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_resource_import",
                                        "scenario": "vm_path import of the fixture blob",
                                        "ok": bool(res_imported.get("ok"))})
    check("L1 edit_resource_import ok", bool(res_imported.get("ok")),
          json.dumps(res_imported)[:260])
    if res_imported.get("ok"):
        revision = int(payload(res_imported).get("transaction", {}).get("work_revision", revision))

    scanned = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    validate_structured("edit_impact_scan", last_raw("edit_impact_scan"), output_schemas,
                        strict["output_schema_rows"])
    strict["live_success_rows"].append({"tool": "edit_impact_scan",
                                        "scenario": "scan of the active transaction",
                                        "ok": bool(scanned.get("ok"))})
    check("L1 edit_impact_scan ok", bool(scanned.get("ok")), json.dumps(scanned)[:240])

    # Remaining real error classes on the live transaction.
    bad_apply = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "assembly_ref_update", "target": {"token": "0x02000002"},
                      "version": "1.0.0.0"}})
    cases = []
    cases.append(("validation", envelope_error(bad_apply) == "EDIT_VALIDATION_FAILED", bad_apply))
    stale = call(client, "edit_impact_scan", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 77})
    cases.append(("revision", envelope_error(stale) == "EDIT_REVISION_CONFLICT", stale))
    foreign = call(client, "edit_review", {"request_id": rid(), "transaction_id": "edit-none",
                                           "expected_revision": 0})
    cases.append(("not_found", envelope_error(foreign) == "EDIT_TRANSACTION_NOT_FOUND", foreign))
    missing_owner = call(client, "edit_recover", {
        "request_id": rid(),
        "recovery_id": "recovery-" + hex32("t032-missing"), "action": "retry_checkpoint"})
    cases.append(("recovery", envelope_error(missing_owner) == "EDIT_RECOVERY_NOT_FOUND", missing_owner))
    for label, ok, envelope in cases:
        error = envelope.get("error", {}) if isinstance(envelope, dict) else {}
        triple = bool(error.get("code")) and bool(error.get("message")) and bool(error.get("recovery"))
        check(f"E1 {label} stable triple", ok and triple, json.dumps(envelope)[:220])
        strict["error_matrix"].append({"class": {
            "validation": "EDIT_VALIDATION_FAILED", "revision": "EDIT_REVISION_CONFLICT",
            "not_found": "EDIT_TRANSACTION_NOT_FOUND",
            "recovery": "EDIT_RECOVERY_NOT_FOUND"}[label], "triple": error_triple(envelope)})

    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx,
                                            "expected_revision": revision})
    validate_structured("edit_review", last_raw("edit_review"), output_schemas,
                        strict["output_schema_rows"])
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required_risks = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    strict["live_success_rows"].append({"tool": "edit_review", "scenario": "review before commit",
                                        "ok": bool(reviewed.get("ok")) and bool(review_id)})
    check("L1 edit_review ok", bool(reviewed.get("ok")) and bool(review_id),
          json.dumps(reviewed)[:240])
    review_revision = int(review_row.get("review_revision", revision))

    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": review_revision,
        "confirmed_risk_ids": required_risks})
    validate_structured("edit_commit", last_raw("edit_commit"), output_schemas,
                        strict["output_schema_rows"])
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    family_id = str(commit_row.get("history", {}).get("family_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    strict["live_success_rows"].append({"tool": "edit_commit", "scenario": "commit the strict chain",
                                        "ok": bool(committed.get("ok")) and bool(lineage_id)})
    check("L1 edit_commit ok", bool(committed.get("ok")) and bool(lineage_id),
          json.dumps(committed)[:260])
    lease.close()

    commit_source = "main" if lineage_id else ""
    committed_resource_names: list[str] = []
    if lineage_id:
        committed_resource_names = ["TestIL.T032.resources", "TestIL.T032b.resources"]
    else:
        # Diagnostic bisect: the full chain failed its commit-time structural
        # validation.  Roll back and replay progressively larger chains so the
        # failing operation is isolated (A is the ACC-025-proven minimal shape)
        # and the history tools still get a lineage to work on.
        strict["diagnostics"] = {"failed_commit_envelope": last_raw("edit_commit").get("parsed"),
                                 "steps": []}
        rb = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        strict["diagnostics"]["steps"].append({"step": "rollback_full_chain", "ok": bool(rb.get("ok"))})

        def bisect_chain(label: str, operations: list[dict]) -> dict | None:
            begun = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
            chain_tx = str(payload(begun).get("transaction", {}).get("transaction_id", ""))
            chain_rev = int(payload(begun).get("transaction", {}).get("work_revision", 0))
            if not chain_tx:
                strict["diagnostics"]["steps"].append({"step": label + ":begin", "ok": False})
                return None
            step = {"step": label, "applied": [], "ok": False}
            try:
                for operation in operations:
                    applied_op = call(client, "edit_apply", {
                        "request_id": rid(), "transaction_id": chain_tx,
                        "expected_revision": chain_rev, "operation": operation})
                    ok_op = bool(applied_op.get("ok"))
                    step["applied"].append({"kind": operation.get("kind"), "ok": ok_op})
                    if not ok_op:
                        step["apply_envelope"] = last_raw("edit_apply").get("parsed")
                        return None
                    chain_rev = int(payload(applied_op).get("transaction", {}).get("work_revision", chain_rev))
                reviewed_op = call(client, "edit_review", {"request_id": rid(), "transaction_id": chain_tx,
                                                           "expected_revision": chain_rev})
                review_op_row = payload(reviewed_op).get("review", {}) if isinstance(payload(reviewed_op), dict) else {}
                committed_op = call(client, "edit_commit", {
                    "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                    "review_id": str(review_op_row.get("review_id", "")),
                    "review_revision": int(review_op_row.get("review_revision", chain_rev)),
                    "confirmed_risk_ids": [str(r) for r in (review_op_row.get("required_confirmation_ids") or [])]})
                step["ok"] = bool(committed_op.get("ok"))
                step["commit_envelope"] = last_raw("edit_commit").get("parsed")
                if step["ok"]:
                    validate_structured("edit_commit", last_raw("edit_commit"), output_schemas,
                                        strict["output_schema_rows"])
                    return payload(committed_op)
                return None
            finally:
                if not step["ok"]:
                    drained = call(client, "edit_status", {})
                    drain_tx = payload(drained).get("transaction", {}).get("transaction_id", "")
                    if drain_tx:
                        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": drain_tx})
                strict["diagnostics"]["steps"].append(step)

        module_op = {"kind": "module_update", "name": "T032BisectModule"}
        bisect_plan = [
            ("A_module_update_only", [module_op], None),
            ("B_managed_resource_add", [module_op, {"kind": "managed_resource_add",
                                                    "name": "TestIL.T032.resources",
                                                    "data_base64": "VDAzMlJFU09VUkNFQkxPQg=="}],
             "TestIL.T032.resources"),
            ("C_resource_import", [module_op, {"kind": "__resource_import__"}], "TestIL.T032b.resources"),
            ("D_import_add", [module_op, "__import_add__"], None),
            ("E_import_only", ["__import_add__"], None),
        ]
        for label, operations, resource_name in bisect_plan:
            if label == "C_resource_import":
                begun_c = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
                chain_tx = str(payload(begun_c).get("transaction", {}).get("transaction_id", ""))
                chain_rev = int(payload(begun_c).get("transaction", {}).get("work_revision", 0))
                step = {"step": label, "applied": [], "ok": False}
                if not chain_tx:
                    strict["diagnostics"]["steps"].append(step)
                    continue
                try:
                    apply_mod = call(client, "edit_apply", {
                        "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                        "operation": module_op})
                    step["applied"].append({"kind": module_op["kind"], "ok": bool(apply_mod.get("ok"))})
                    if apply_mod.get("ok"):
                        chain_rev = int(payload(apply_mod).get("transaction", {}).get("work_revision", chain_rev))
                        blob_path = str(Path(FIXTURE).parent / BLOB_FILE)
                        apply_res = call(client, "edit_resource_import", {
                            "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                            "vm_path": blob_path, "resource_name": "TestIL.T032b.resources",
                            "resource_type": "embedded"})
                        step["applied"].append({"tool": "edit_resource_import",
                                                "ok": bool(apply_res.get("ok"))})
                        if apply_res.get("ok"):
                            chain_rev = int(payload(apply_res).get("transaction", {}).get("work_revision", chain_rev))
                    if all(row.get("ok") for row in step["applied"]):
                        reviewed_c = call(client, "edit_review", {"request_id": rid(),
                                                                 "transaction_id": chain_tx,
                                                                 "expected_revision": chain_rev})
                        review_c = payload(reviewed_c).get("review", {}) if isinstance(payload(reviewed_c), dict) else {}
                        committed_c = call(client, "edit_commit", {
                            "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                            "review_id": str(review_c.get("review_id", "")),
                            "review_revision": int(review_c.get("review_revision", chain_rev)),
                            "confirmed_risk_ids": [str(r) for r in (review_c.get("required_confirmation_ids") or [])]})
                        step["ok"] = bool(committed_c.get("ok"))
                        step["commit_envelope"] = last_raw("edit_commit").get("parsed")
                        if step["ok"]:
                            validate_structured("edit_commit", last_raw("edit_commit"), output_schemas,
                                                strict["output_schema_rows"])
                            core_c = payload(committed_c)
                            lineage_id = str(core_c.get("history", {}).get("lineage_id", "")) or lineage_id
                            family_id = str(core_c.get("history", {}).get("family_id", "")) or family_id
                            commit_source = label
                            committed_resource_names.append(resource_name)
                finally:
                    if not step["ok"]:
                        drained = call(client, "edit_status", {})
                        drain_tx = payload(drained).get("transaction", {}).get("transaction_id", "")
                        if drain_tx:
                            call(client, "edit_rollback", {"request_id": rid(), "transaction_id": drain_tx})
                    strict["diagnostics"]["steps"].append(step)
                continue
            if label in ("D_import_add", "E_import_only"):
                compile_id = (str(payload(first_compile).get("compile", {}).get("compile_id", ""))
                              if first_compile else "")
                if not compile_id:
                    strict["diagnostics"]["steps"].append(
                        {"step": label, "ok": False, "skipped": "no compiled artifact"})
                    continue
                begun_d = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
                chain_tx = str(payload(begun_d).get("transaction", {}).get("transaction_id", ""))
                chain_rev = int(payload(begun_d).get("transaction", {}).get("work_revision", 0))
                step = {"step": label, "applied": [], "ok": False}
                if not chain_tx:
                    strict["diagnostics"]["steps"].append(step)
                    continue
                try:
                    if label == "D_import_add":
                        apply_mod = call(client, "edit_apply", {
                            "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                            "operation": module_op})
                        step["applied"].append({"kind": module_op["kind"], "ok": bool(apply_mod.get("ok"))})
                        if not apply_mod.get("ok"):
                            raise _BisectStepFailed()
                        chain_rev = int(payload(apply_mod).get("transaction", {}).get("work_revision", chain_rev))
                    apply_imp = call(client, "edit_import", {
                        "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                        "compile_id": compile_id,
                        "targets": [{"compiled": "TestIL.Simple::T032Added0()", "action": "add"}]})
                    step["applied"].append({"tool": "edit_import", "ok": bool(apply_imp.get("ok"))})
                    if not apply_imp.get("ok"):
                        raise _BisectStepFailed()
                    chain_rev = int(payload(apply_imp).get("transaction", {}).get("work_revision", chain_rev))
                    if all(row.get("ok") for row in step["applied"]):
                        reviewed_d = call(client, "edit_review", {"request_id": rid(),
                                                                 "transaction_id": chain_tx,
                                                                 "expected_revision": chain_rev})
                        review_d = payload(reviewed_d).get("review", {}) if isinstance(payload(reviewed_d), dict) else {}
                        committed_d = call(client, "edit_commit", {
                            "request_id": rid(), "transaction_id": chain_tx, "expected_revision": chain_rev,
                            "review_id": str(review_d.get("review_id", "")),
                            "review_revision": int(review_d.get("review_revision", chain_rev)),
                            "confirmed_risk_ids": [str(r) for r in (review_d.get("required_confirmation_ids") or [])]})
                        step["ok"] = bool(committed_d.get("ok"))
                        step["commit_envelope"] = last_raw("edit_commit").get("parsed")
                        if step["ok"]:
                            validate_structured("edit_commit", last_raw("edit_commit"), output_schemas,
                                                strict["output_schema_rows"])
                            core_d = payload(committed_d)
                            lineage_id = str(core_d.get("history", {}).get("lineage_id", "")) or lineage_id
                            family_id = str(core_d.get("history", {}).get("family_id", "")) or family_id
                            commit_source = label
                except _BisectStepFailed:
                    pass
                finally:
                    if not step["ok"]:
                        drained = call(client, "edit_status", {})
                        drain_tx = payload(drained).get("transaction", {}).get("transaction_id", "")
                        if drain_tx:
                            call(client, "edit_rollback", {"request_id": rid(), "transaction_id": drain_tx})
                    strict["diagnostics"]["steps"].append(step)
                continue
            core = bisect_chain(label, operations)
            if core:
                lineage_id = str(core.get("history", {}).get("lineage_id", "")) or lineage_id
                family_id = str(core.get("history", {}).get("family_id", "")) or family_id
                commit_source = label
                if resource_name:
                    committed_resource_names.append(resource_name)
        print(f"INFO commit bisect: source={commit_source} lineage={lineage_id} "
              f"resources={committed_resource_names}", flush=True)

    if lineage_id:
        # Phase 5 - history navigation on the committed lineage.
        hist_view = call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100})
        nodes = [row for row in payload(hist_view).get("checkpoints", []) if isinstance(row, dict)]
        head = str(nodes[-1].get("checkpoint_id", "")) if nodes else ""
        parent = str(nodes[-2].get("checkpoint_id", "")) if len(nodes) >= 2 else ""
        check("L1 edit_history lineage view ok", bool(hist_view.get("ok")) and bool(head),
              json.dumps(hist_view)[:200])

        unknown_checkpoint = "checkpoint-" + hex32("t032-unknown")
        conflict = call(client, "edit_history", {"checkpoint_id": unknown_checkpoint})
        conflict_triple = error_triple(conflict)
        strict["error_matrix"].append({"class": "EDIT_HISTORY_CONFLICT",
                                       "trigger": "well-formed but nonexistent checkpoint_id",
                                       "triple": conflict_triple})
        check("E1 history_conflict stable triple",
              envelope_error(conflict) == "EDIT_HISTORY_CONFLICT"
              and bool(conflict_triple["current_state"]) and bool(conflict_triple["recovery"]),
              json.dumps(conflict_triple)[:260])

        # History navigation runs BEFORE any export reopen: loading the
        # exported image next to the original makes "TestIL" ambiguous on the
        # wire (r2 evidence: capability loaded_module not-unique), which would
        # fail undo/restore/accept_live for driver-order reasons, not product
        # ones.
        undone = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id,
                                            "expected_checkpoint_id": head})
        validate_structured("edit_undo", last_raw("edit_undo"), output_schemas,
                            strict["output_schema_rows"])
        strict["live_success_rows"].append({"tool": "edit_undo", "scenario": "undo the head",
                                            "ok": bool(undone.get("ok"))})
        check("L1 edit_undo ok", bool(undone.get("ok")), json.dumps(undone)[:240])
        if parent and undone.get("ok"):
            redone = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage_id,
                                                "expected_checkpoint_id": parent})
            validate_structured("edit_redo", last_raw("edit_redo"), output_schemas,
                                strict["output_schema_rows"])
            strict["live_success_rows"].append({"tool": "edit_redo", "scenario": "redo the undo",
                                                "ok": bool(redone.get("ok"))})
            check("L1 edit_redo ok", bool(redone.get("ok")), json.dumps(redone)[:240])
        else:
            strict["unverified"].append({"tool": "edit_redo",
                                         "reason": f"undo did not complete with a parent (head={head}, parent={parent})"})

        restore_target = head  # after undo+redo the lineage head is the committed head again
        assessed = call(client, "edit_restore", {
            "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": restore_target,
            "action": "assess"})
        validate_structured("edit_restore", last_raw("edit_restore"), output_schemas,
                            strict["output_schema_rows"])
        strict["live_success_rows"].append({"tool": "edit_restore",
                                            "scenario": "assess the lineage head",
                                            "ok": bool(assessed.get("ok"))})
        check("L1 edit_restore assess ok", bool(assessed.get("ok")), json.dumps(assessed)[:240])

        # accept_live: wrong fingerprint must conflict; the diverged acceptance
        # uses the DNMCP_TEST lineage-mutation seam and is labeled as such.
        wrong_accept = call(client, "edit_accept_live", {
            "request_id": rid(), "assembly_name": "TestIL", "source_family_id": family_id,
            "superseded_lineage_id": lineage_id, "expected_live_fingerprint": "0" * 64,
            "acknowledge_new_baseline": True})
        wrong_accept_triple = error_triple(wrong_accept)
        strict["error_matrix"].append({"class": "EDIT_HISTORY_CONFLICT",
                                       "trigger": "accept_live with a wrong live fingerprint",
                                       "triple": wrong_accept_triple})
        check("E1 accept_live wrong fingerprint conflicts",
              envelope_error(wrong_accept) == "EDIT_HISTORY_CONFLICT",
              json.dumps(wrong_accept_triple)[:240])

        mutation = call(client, "edit_test_lineage_mutation",
                        {"action": "mutate", "assembly_name": "TestIL"})
        mutated_fp = str(payload(mutation).get("after_fingerprint", ""))
        if mutation.get("ok") and len(mutated_fp) == 64:
            SEAM_NOTES.append("edit_test_lineage_mutation (DNMCP_TEST seam) used only to diverge "
                              "the live module for the edit_accept_live success path")
            accepted = call(client, "edit_accept_live", {
                "request_id": rid(), "assembly_name": "TestIL", "source_family_id": family_id,
                "superseded_lineage_id": lineage_id, "expected_live_fingerprint": mutated_fp,
                "acknowledge_new_baseline": True})
            validate_structured("edit_accept_live", last_raw("edit_accept_live"), output_schemas,
                                strict["output_schema_rows"])
            strict["live_success_rows"].append({"tool": "edit_accept_live",
                                                "scenario": "accept seam-diverged live module (labeled seam)",
                                                "ok": bool(accepted.get("ok"))})
            check("L1 edit_accept_live ok (seam-labeled)", bool(accepted.get("ok")),
                  json.dumps(accepted)[:260])
        else:
            strict["unverified"].append({
                "tool": "edit_accept_live",
                "reason": "lineage-mutation seam unavailable on this host; no clean diverged-live "
                          "state was created, so the success output was not triggered "
                          f"(mutation={json.dumps(mutation)[:160]})"})
            print(f"INFO edit_accept_live success unverified: {json.dumps(mutation)[:200]}", flush=True)

        # resource_export reads the committed live module directly (still the
        # only loaded TestIL); the exported image is written afterwards and is
        # never reopened in this run.
        if committed_resource_names:
            res_exported = call(client, "edit_resource_export", {
                "request_id": rid(), "assembly_name": "TestIL",
                "resource_name": committed_resource_names[0],
                "output_path": r"edit-output\t032\T032.resources"})
            validate_structured("edit_resource_export", last_raw("edit_resource_export"),
                                output_schemas, strict["output_schema_rows"])
            strict["live_success_rows"].append({"tool": "edit_resource_export",
                                                "scenario": "read back the committed resource "
                                                            + committed_resource_names[0],
                                                "ok": bool(res_exported.get("ok"))})
            check("L1 edit_resource_export ok", bool(res_exported.get("ok")),
                  json.dumps(res_exported)[:240])
        else:
            strict["unverified"].append({"tool": "edit_resource_export",
                                         "reason": "no resource-bearing chain committed; the live "
                                                   "module has no resource to export"})

        exported = call(client, "edit_export", {
            "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": head,
            "output_path": r"edit-output\t032\strict.dll"})
        validate_structured("edit_export", last_raw("edit_export"), output_schemas,
                            strict["output_schema_rows"])
        export_path = str(payload(exported).get("output", {}).get("path", ""))
        strict["live_success_rows"].append({"tool": "edit_export",
                                            "scenario": "export the committed head",
                                            "ok": bool(exported.get("ok")) and export_path.lower().endswith(".dll")})
        check("L1 edit_export ok", bool(exported.get("ok")) and export_path.lower().endswith(".dll"),
              json.dumps(exported)[:240])
    else:
        for tool in ("edit_history", "edit_export", "edit_resource_export", "edit_undo",
                     "edit_redo", "edit_restore", "edit_accept_live"):
            strict["unverified"].append({"tool": tool,
                                         "reason": "edit_commit failed so no lineage exists"})

    # edit_recover success requires a genuine partial commit state (ACC-019's
    # session-kill matrix); triggering it here is out of scope and unsafe.
    strict["unverified"].append({
        "tool": "edit_recover",
        "reason": "success requires a real partial-commit recovery state produced by ACC-019's "
                  "barrier/session-kill matrix; only the EDIT_RECOVERY_NOT_FOUND error path is "
                  "triggered here"})

    # A2 summary: every captured raw structuredContent must itself be an object
    # and conform to the declared outputSchema; text fallbacks are inadmissible.
    bad_structured = [row for row in strict["output_schema_rows"]
                      if row.get("verdict") in ("FAIL_STRUCTURED_SHAPE", "SCHEMA_VIOLATION")]
    check("A2 real structuredContent objects conform to declared outputSchema",
          not bad_structured, json.dumps(bad_structured[:4])[:400])
    promoted = [row for row in RAW_WIRE
                if isinstance(row.get("wire"), dict)
                and (row["wire"].get("structured_content_present") is not True
                     or row["wire"].get("structured_content_type") != "dict")
                and isinstance(row.get("parsed"), dict)
                and row["wire"].get("structured_content_type") != "jsonrpc-exception"]
    check("A2 no string-promoted structuredContent", not promoted,
          json.dumps([r.get("tool") for r in promoted[:6]]))
    tools_only_unverified = sorted(
        {row["tool"] for row in strict["output_schema_rows"]
         if row.get("verdict") == "UNVERIFIED_NO_RAW_RESULT"}
        - {row["tool"] for row in strict["output_schema_rows"]
           if row.get("verdict") == "CONFORMS"})
    if tools_only_unverified:
        strict["unverified"].append(
            {"tool": ", ".join(tools_only_unverified),
             "reason": "only JSON-RPC-level failures captured; no raw tool result "
                       "existed to gate on"})

    strict["coverage"] = {
        "edit_tools": sorted(edit_tools),
        "live_success_tools": sorted({row["tool"] for row in strict["live_success_rows"]}),
        "live_output_validated_tools": sorted({row["tool"] for row in strict["output_schema_rows"]
                                               if row.get("verdict") == "CONFORMS"}),
        "unverified_tools": sorted({row["tool"] for row in strict["unverified"] if row.get("tool")}),
        "error_classes_triggered": sorted({row["class"] for row in strict["error_matrix"]
                                           if row.get("triple", {}).get("code")}),
    }
    if WORK_DIR:
        (Path(WORK_DIR) / "acc021-strict-report.json").write_text(
            json.dumps(strict, ensure_ascii=False, indent=2), encoding="utf-8")
        (Path(WORK_DIR) / "acc021-raw-wire.jsonl").write_text(
            "\n".join(json.dumps(row, ensure_ascii=False) for row in RAW_WIRE) + "\n",
            encoding="utf-8")

    client.close()
    print(f"ACC021 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
