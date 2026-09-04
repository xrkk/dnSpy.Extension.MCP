#!/usr/bin/env python3
"""VM-side P02 runtime acceptance driver.

The host orchestrator copies this file and the installed dnspy_mcp package into the isolated
Windows fixture root.  It deliberately talks only through the Python MCP client; no curl or
hand-written JSON-RPC packet is used.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import threading
import time
import traceback
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp.client import DnSpyClient, DnSpyProtocolError, ToolCallError


def rid(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex}"


def body(*instructions: dict[str, Any], max_stack: int = 0) -> dict[str, Any]:
    return {
        "init_locals": False,
        "max_stack": max_stack,
        "instructions": list(instructions),
        "locals": [],
        "exception_handlers": [],
    }


def method(owner: str, name: str, parameters: list[dict[str, Any]] | None = None) -> dict[str, Any]:
    return {
        "kind": "method_add",
        "owner_type": {"object_id": owner},
        "name": name,
        "signature": {
            "return_type": "System.Void",
            "parameters": parameters or [],
            "has_this": True,
            "generic_parameters": [],
        },
        "body": body({"opcode": "ret"}),
    }


def positive_operations() -> list[dict[str, Any]]:
    """One valid dependency-ordered chain covering every public P02 operation kind."""

    return [
        {"kind": "type_add", "namespace": "P02", "name": "AllKinds"},
        {"kind": "type_update", "target": {"object_id": "obj-000-00"}, "name": "AllKindsUpdated"},
        method("obj-000-00", "Primary"),
        {"kind": "method_update", "target": {"object_id": "obj-002-00"}, "name": "PrimaryUpdated"},
        {"kind": "field_add", "owner_type": {"object_id": "obj-000-00"}, "name": "Value", "field_type": "System.Int32"},
        {"kind": "field_update", "target": {"object_id": "obj-004-00"}, "name": "ValueUpdated", "constant": {"kind": "i4", "value": 7}},
        {"kind": "property_add", "owner_type": {"object_id": "obj-000-00"}, "name": "Label", "property_type": "System.String"},
        {"kind": "property_update", "target": {"object_id": "obj-006-00"}, "name": "LabelUpdated", "property_type": "System.Object"},
        {"kind": "property_remove", "target": {"object_id": "obj-006-00"}, "remove_mode": "reject_if_referenced"},
        method("obj-000-00", "add_Changed", [{"name": "value", "type": "System.Action"}]),
        method("obj-000-00", "remove_Changed", [{"name": "value", "type": "System.Action"}]),
        {"kind": "event_add", "owner_type": {"object_id": "obj-000-00"}, "name": "Changed", "event_type": "System.Action", "add_method": {"object_id": "obj-009-00"}, "remove_method": {"object_id": "obj-010-00"}},
        {"kind": "event_update", "target": {"object_id": "obj-011-00"}, "name": "ChangedUpdated"},
        {"kind": "event_remove", "target": {"object_id": "obj-011-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "generic_parameter_add", "owner": {"object_id": "obj-000-00"}, "generic_index": 0, "name": "T"},
        {"kind": "generic_parameter_update", "target": {"object_id": "obj-014-00"}, "name": "TUpdated"},
        {"kind": "generic_parameter_remove", "target": {"object_id": "obj-014-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "parameter_add", "owner_method": {"object_id": "obj-002-00"}, "parameter_index": 0, "name": "arg", "parameter_type": "System.Int32"},
        {"kind": "parameter_update", "parameter_target": {"object_id": "obj-017-00"}, "name": "argUpdated", "parameter_type": "System.Int64"},
        {"kind": "parameter_remove", "parameter_target": {"object_id": "obj-017-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "method_body_replace", "target": {"object_id": "obj-002-00"}, "body": body({"opcode": "ret"})},
        {"kind": "field_remove", "target": {"object_id": "obj-004-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "method_remove", "target": {"object_id": "obj-002-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "method_remove", "target": {"object_id": "obj-009-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "method_remove", "target": {"object_id": "obj-010-00"}, "remove_mode": "reject_if_referenced"},
        {"kind": "type_remove", "target": {"object_id": "obj-000-00"}, "remove_mode": "reject_if_referenced"},
    ]


def error_code(value: dict[str, Any]) -> str | None:
    error = value.get("error")
    return error.get("code") if isinstance(error, dict) else None


def failing_call(call: Any) -> dict[str, Any]:
    try:
        call()
    except ToolCallError as exc:
        return json.loads(str(exc))
    raise AssertionError("tool call unexpectedly succeeded")


def run_positive(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    begin = client.edit_begin(rid("begin"), assembly)
    assert begin["ok"] is True, begin
    tx = begin["result"]["transaction"]["transaction_id"]
    baseline = begin["result"]["fingerprints"]["baseline_live"]
    revision = 0
    transcript: list[dict[str, Any]] = []
    try:
        for operation in positive_operations():
            print(json.dumps({"phase": "apply", "kind": operation["kind"], "revision": revision}), flush=True)
            response = client.edit_apply(rid("apply"), tx, revision, operation)
            assert response["ok"] is True, (operation, response)
            revision = response["result"]["transaction"]["work_revision"]
            transcript.append({
                "kind": operation["kind"],
                "revision": revision,
                "private": response["result"]["fingerprints"]["private"],
                "live": response["result"]["fingerprints"]["current_live"],
            })
            assert transcript[-1]["live"] == baseline

        review = client.edit_review(rid("review"), tx, revision)
        assert review["ok"] is True, review
        review_value = review["result"]["review"]
        live = client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live"), "transaction_id": tx,
            "review_id": review_value["review_id"], "expected_revision": revision,
            "confirmed_risk_ids": review_value["required_confirmation_ids"],
        })
        assert live["ok"] is True, live
        result = live["result"]
        assert result["pre_live_fingerprint"] == baseline
        assert result["post_restore_fingerprint"] == baseline
        manifest = result["execution_evidence"]["fault_manifest"]
        oracle = result["execution_evidence"]["oracle_faults"]
        assert manifest == oracle and len(manifest) == 240
        return {
            "transaction_id": tx,
            "baseline": baseline,
            "revision": revision,
            "operation_kinds": [row["kind"] for row in transcript],
            "distinct_operation_kinds": sorted(set(row["kind"] for row in transcript)),
            "transcript": transcript,
            "review": review,
            "live_result": result,
        }
    finally:
        rollback = client.edit_rollback(rid("rollback"), tx)
        assert rollback["ok"] is True, rollback


def run_idempotency_and_raw_rejection(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    request_id = rid("begin-idem")
    begin = client.edit_begin(request_id, assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        apply_id = rid("apply-idem")
        operation = {"kind": "type_add", "namespace": "P02", "name": "Replay"}
        first = client.edit_apply(apply_id, tx, 0, operation)
        replay = client.edit_apply(apply_id, tx, 0, operation)
        assert first == replay
        try:
            client.edit_apply(apply_id, tx, 0, {**operation, "name": "Different"})
            raise AssertionError("changed request payload was accepted")
        except ToolCallError as exc:
            changed = json.loads(str(exc))
        assert error_code(changed) == "REQUEST_ID_REUSE", changed

        raw_error: dict[str, Any]
        try:
            client.call_tool_json("edit_apply", {
                "request_id": rid("raw"), "transaction_id": tx, "expected_revision": 1,
                "operation": {"kind": "type_add", "name": "Raw", "raw_metadata": "00"},
            })
            raise AssertionError("raw metadata field was accepted")
        except DnSpyProtocolError as exc:
            assert exc.code == -32602, exc
            raw_error = {"code": exc.code, "message": str(exc)}
        return {"first": first, "replay_equal": first == replay, "reuse": changed, "raw": raw_error}
    finally:
        client.edit_rollback(rid("rollback"), tx)


def run_review_lifecycle(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    begin = client.edit_begin(rid("begin-review"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        applied = client.edit_apply(rid("apply-review"), tx, 0, {
            "kind": "type_add", "namespace": "P02", "name": "ReviewLifecycle",
        })
        revision = applied["result"]["transaction"]["work_revision"]
        request_one = rid("review-one")
        first = client.edit_review(request_one, tx, revision)
        replay = client.edit_review(request_one, tx, revision)
        assert first == replay
        second = client.edit_review(rid("review-two"), tx, revision)
        old_replay = client.edit_review(request_one, tx, revision)
        assert first == old_replay
        status = client.edit_status()
        assert status["result"]["capacity"]["review_tombstone_entries"]["current"] == 1
        client.edit_apply(rid("apply-after-review"), tx, revision, {
            "kind": "type_update", "target": {"object_id": "obj-000-00"}, "name": "ReviewLifecycle2",
        })
        stale = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-stale"), "transaction_id": tx,
            "review_id": second["result"]["review"]["review_id"],
            "expected_revision": revision + 1, "confirmed_risk_ids": [],
        }))
        assert error_code(stale) == "EDIT_REVIEW_STALE", stale
        return {"first": first, "replay_equal": first == replay, "old_replay_equal": first == old_replay,
                "capacity": status["result"]["capacity"], "stale": stale}
    finally:
        client.edit_rollback(rid("rollback-review"), tx)


def run_fault_smoke(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    results: dict[str, Any] = {}
    for label, fault_id, expected in (
        ("forward", "fp-0-forward-0-insert-after", "EDIT_INTERNAL_ERROR"),
        ("reverse", "fp-0-reverse-0-remove-before", "EDIT_LIVE_STATE_UNKNOWN"),
    ):
        begin = client.edit_begin(rid("begin-fault"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        applied = client.edit_apply(rid("apply-fault"), tx, 0, {
            "kind": "type_add", "namespace": "P02", "name": "FaultSmoke",
        })
        revision = applied["result"]["transaction"]["work_revision"]
        review = client.edit_review(rid("review-fault"), tx, revision)
        client.call_tool_json("edit_test_fault", {"action": "arm", "fault_id": fault_id})
        failed = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-fault"), "transaction_id": tx,
            "review_id": review["result"]["review"]["review_id"], "expected_revision": revision,
            "confirmed_risk_ids": review["result"]["review"]["required_confirmation_ids"],
        }))
        assert error_code(failed) == expected, failed
        evidence = failed["execution_evidence"]
        assert evidence["covered_faults"][0]["fault_id"] == fault_id
        assert evidence["actual_mutation_trace"][-1]["fault_id"] == fault_id
        status = client.edit_status()
        if label == "forward":
            assert status["state"] == "reviewed", status
            client.edit_rollback(rid("rollback-fault"), tx)
        else:
            assert status["state"] == "live_state_unknown", status
            blocked = failing_call(lambda: client.edit_review(rid("blocked-review"), tx, revision))
            assert error_code(blocked) == "EDIT_LIVE_STATE_UNKNOWN", blocked
            client.call_tool_json("edit_test_fault", {"action": "reset"})
            assert client.edit_status()["state"] == "idle"
        results[label] = {"fault_id": fault_id, "failure": failed, "status": status}
    return results


def inject_environment(client: DnSpyClient, classification: str) -> dict[str, Any]:
    fixtures = {
        "physical": {"manufacturer": "Dell Inc.", "product_name": "Precision", "bios_vendor": "Dell Inc."},
        "vmware": {"manufacturer": "VMware, Inc.", "product_name": "VMware Virtual Platform", "bios_vendor": "Phoenix"},
    }
    return client.call_tool_json("debug_test_environment", {
        "p01_action": "inject_signals", **fixtures[classification],
    })


def review_with_dynamic(
    client: DnSpyClient, assembly: str, profile: str | None, *, expect_error: bool = False,
) -> dict[str, Any]:
    begin = client.edit_begin(rid("begin-dynamic"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        request = None if profile is None else {
            "mode": "run", "runtime_profile": profile, "timeout_ms": 30000,
        }
        if expect_error or (profile and profile.startswith("test-fail")):
            response = failing_call(lambda: client.edit_review(
                rid("review-dynamic"), tx, 0, dynamic_validation=request))
        else:
            response = client.edit_review(rid("review-dynamic"), tx, 0, dynamic_validation=request)
        return response
    finally:
        client.edit_rollback(rid("rollback-dynamic"), tx)


def run_dynamic_validation(client: DnSpyClient, static_assembly: str, dynamic_assembly: str) -> dict[str, Any]:
    cases: dict[str, Any] = {}
    cases["not_requested"] = review_with_dynamic(client, dynamic_assembly, None)
    assert cases["not_requested"]["result"]["dynamic_validation"]["state"] == "not_requested"
    cases["not_applicable"] = review_with_dynamic(client, static_assembly, "net48-exe")
    assert cases["not_applicable"]["result"]["dynamic_validation"]["state"] == "not_applicable"
    cases["passed"] = review_with_dynamic(client, dynamic_assembly, "net48-exe")
    passed = cases["passed"]["result"]["dynamic_validation"]
    assert passed["state"] == "passed" and passed["cleanup"]["debug_idle"] is True
    assert [event["kind"] for event in passed["events"]] == [
        "artifact_written", "debug_started", "entry_paused", "debug_terminated",
    ]

    for profile in ("test-fail-launch", "test-fail-terminate", "test-fail-delete"):
        value = review_with_dynamic(client, dynamic_assembly, profile)
        assert error_code(value) == "EDIT_VALIDATION_FAILED", value
        attempt = value["validation_attempt"]
        assert attempt["state"] == "failed" and attempt["cleanup"]["debug_idle"] is True
        residual = attempt["cleanup"].get("residual_path")
        if residual:
            residual_path = Path(residual)
            assert residual_path.is_file(), residual
            residual_path.unlink()
            assert not residual_path.exists(), residual
        cases[profile] = value

    inject_environment(client, "physical")
    try:
        blocked = review_with_dynamic(client, dynamic_assembly, "net48-exe", expect_error=True)
        assert error_code(blocked) == "EDIT_CAPABILITY_UNAVAILABLE", blocked
        assert blocked["validation_attempt"]["state"] == "blocked", blocked
        cases["blocked_physical"] = blocked
    finally:
        client.call_tool_json("debug_test_environment", {"p01_action": "clear_signals"})

    status = client.call_tool_json("debug_status", {})
    assert status["ok"] is True and status["result"]["state"] == "idle", status
    cases["final_debug_status"] = status
    return cases


def run_fingerprint_components(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    case_ids = (
        "fp-channel-modulemetadata:mutate",
        "fp-channel-dnlibobjectgraph:mutate",
        "fp-channel-methodbodyil:mutate",
        "fp-channel-managedresource:mutate",
        "fp-channel-embeddedpdb:mutate",
        "fp-canonical-global-order:reorder",
    )
    begin = client.edit_begin(rid("begin-fingerprint"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    results: dict[str, Any] = {}
    try:
        for case_id in case_ids:
            result = client.call_tool_json("edit_test_external_mutation", {
                "transaction_id": tx, "case_id": case_id,
            })
            assert result["ok"] is True, result
            value = result["result"]
            assert value["changed"] is True and value["restored"] is True, value
            if case_id == "fp-canonical-global-order:reorder":
                assert value["semantic_change"] is False
                assert value["raw_order_before"] != value["raw_order_after"]
                assert value["canonical_readback_before"] == value["canonical_readback_after"]
            else:
                assert value["semantic_change"] is True
                assert value["before_fingerprint"] != value["after_fingerprint"]
            results[case_id] = result
        return results
    finally:
        client.edit_rollback(rid("rollback-fingerprint"), tx)


def token(value: int) -> dict[str, str]:
    return {"token": f"0x{value:08x}"}


def discovered_token(value: Any) -> dict[str, str]:
    return token(int(value))


def exact_member(client: DnSpyClient, assembly: str, declaring_type: str,
                 name: str, kind: str) -> dict[str, Any]:
    response = client.call_tool_json("search_members", {
        "query": name, "assembly_name": assembly, "kinds": [kind], "page_size": 500,
    })
    matches = [row for row in response["items"]
               if row["declaring_type"] == declaring_type and row["name"] == name]
    assert len(matches) == 1, {"declaring_type": declaring_type, "name": name,
                               "kind": kind, "matches": matches}
    return matches[0]


def exact_type(client: DnSpyClient, assembly: str, full_name: str) -> dict[str, Any]:
    response = client.call_tool_json("search_types", {
        "query": full_name, "assembly_name": assembly, "page_size": 500,
    })
    def field(row: dict[str, Any], *names: str) -> Any:
        return next((row[name] for name in names if name in row), None)
    matches = [row for row in response["items"]
               if field(row, "full_name", "fullName", "FullName") == full_name]
    assert len(matches) == 1, {"full_name": full_name, "matches": matches}
    return {"token": field(matches[0], "token", "Token"), "row": matches[0]}


def fault_operations(kind: str) -> list[dict[str, Any]]:
    owner = token(0x02000004)
    operations: dict[str, list[dict[str, Any]]] = {
        "type_add": [{"kind": "type_add", "namespace": "P02Fault", "name": "Added"}],
        "type_update": [{"kind": "type_update", "target": owner, "name": "EditTargetsFault"}],
        "type_remove": [{"kind": "type_remove", "target": token(0x02000006), "remove_mode": "reject_if_referenced"}],
        "method_add": [method("unused", "Added")],
        "method_update": [{"kind": "method_update", "target": token(0x0600000B), "name": "UpdateMethodFault"}],
        "method_remove": [{"kind": "method_remove", "target": token(0x0600000C), "remove_mode": "reject_if_referenced"}],
        "field_add": [{"kind": "field_add", "owner_type": owner, "name": "AddedField", "field_type": "System.Int32"}],
        "field_update": [{"kind": "field_update", "target": token(0x04000002), "name": "UpdateFieldFault"}],
        "field_remove": [{"kind": "field_remove", "target": token(0x04000003), "remove_mode": "reject_if_referenced"}],
        "property_add": [{"kind": "property_add", "owner_type": owner, "name": "AddedProperty", "property_type": "System.String"}],
        "property_update": [{"kind": "property_update", "target": token(0x17000001), "name": "UpdatePropertyFault"}],
        "property_remove": [{"kind": "property_remove", "target": token(0x17000002), "remove_mode": "reject_if_referenced"}],
        "event_add": [{"kind": "event_add", "owner_type": owner, "name": "AddedEvent", "event_type": "System.Action", "add_method": token(0x06000014), "remove_method": token(0x06000015)}],
        "event_update": [{"kind": "event_update", "target": token(0x14000001), "name": "UpdateEventFault"}],
        "event_remove": [{"kind": "event_remove", "target": token(0x14000002), "remove_mode": "reject_if_referenced"}],
        "parameter_add": [{"kind": "parameter_add", "owner_method": token(0x0600000D), "parameter_index": 0, "name": "added", "parameter_type": "System.Int32"}],
        "parameter_update": [{"kind": "parameter_update", "parameter_target": {"owner_method": token(0x0600000E), "parameter_index": 0}, "name": "updated"}],
        "parameter_remove": [{"kind": "parameter_remove", "parameter_target": {"owner_method": token(0x0600000F), "parameter_index": 0}, "remove_mode": "reject_if_referenced"}],
        "generic_parameter_add": [{"kind": "generic_parameter_add", "owner": token(0x06000010), "generic_index": 0, "name": "TAdded"}],
        "generic_parameter_update": [
            {"kind": "generic_parameter_add", "owner": token(0x06000010), "generic_index": 0, "name": "TBefore"},
            {"kind": "generic_parameter_update", "target": {"object_id": "obj-000-00"}, "name": "TAfter"},
        ],
        "generic_parameter_remove": [
            {"kind": "generic_parameter_add", "owner": token(0x06000010), "generic_index": 0, "name": "TRemove"},
            {"kind": "generic_parameter_remove", "target": {"object_id": "obj-000-00"}, "remove_mode": "reject_if_referenced"},
        ],
        "method_body_replace": [{"kind": "method_body_replace", "target": token(0x06000013), "body": body({"opcode": "ret"})}],
    }
    value = operations[kind]
    if kind == "method_add":
        value[0]["owner_type"] = owner
    return value


def fetch_fault_manifest(client: DnSpyClient, assembly: str) -> list[dict[str, Any]]:
    begin = client.edit_begin(rid("begin-manifest"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        applied = client.edit_apply(rid("apply-manifest"), tx, 0, {
            "kind": "type_add", "namespace": "P02Fault", "name": "Manifest",
        })
        review = client.edit_review(rid("review-manifest"), tx, 1)
        result = client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-manifest"), "transaction_id": tx,
            "review_id": review["result"]["review"]["review_id"], "expected_revision": 1,
            "confirmed_risk_ids": review["result"]["review"]["required_confirmation_ids"],
        })
        evidence = result["result"]["execution_evidence"]
        assert evidence["fault_manifest"] == evidence["oracle_faults"]
        assert len(evidence["fault_manifest"]) == 240
        return evidence["fault_manifest"]
    finally:
        client.edit_rollback(rid("rollback-manifest"), tx)


def run_fault_suite(
    client: DnSpyClient, assembly: str, artifact_root: Path, suite_id: str,
    fault_limit: int | None = None,
) -> dict[str, Any]:
    manifest = fetch_fault_manifest(client, assembly)
    expected_ids = [row["fault_id"] for row in manifest]
    suite_root = artifact_root / "fault-suite" / suite_id
    suite_root.mkdir(parents=True, exist_ok=True)
    covered: list[str] = []
    completed = 0
    newly_executed = 0
    session_rotations: list[dict[str, Any]] = []
    original_client = client
    # Keep the caller's session for the final status check, but never accumulate
    # the matrix's begin tombstones in it.  Each owned batch session is closed,
    # which is the contract-defined release event for its cache prefix.
    owned_client: DnSpyClient | None = DnSpyClient.connect(
        original_client.base_url, timeout=original_client.timeout,
        client_name="dnspy-p02-fault-suite-batch")
    client = owned_client
    session_rotations.append({
        "before_ordinal": 1,
        "newly_executed": 0,
        "state": client.edit_status()["state"],
        "initial_batch_session": True,
    })
    started = time.time()
    try:
        for ordinal, row in enumerate(manifest, 1):
            fault_id = row["fault_id"]
            artifact = suite_root / f"{fault_id}.json"
            if artifact.is_file():
                prior = json.loads(artifact.read_text(encoding="utf-8"))
                if prior.get("result") == "PASS" and prior.get("fault_id") == fault_id:
                    covered.append(fault_id)
                    completed += 1
                    continue
            if fault_limit is not None and newly_executed >= fault_limit:
                break
            # Request-id caches are deliberately bounded per transport session.
            # Rotate only between cases while globally idle, before the 64-entry
            # boundary, and retain explicit evidence of every rotation.
            if newly_executed and newly_executed % 48 == 0:
                assert client.edit_status()["state"] == "idle"
                if owned_client is not None:
                    owned_client.close()
                owned_client = DnSpyClient.connect(
                    original_client.base_url, timeout=original_client.timeout,
                    client_name="dnspy-p02-fault-suite-rotation")
                client = owned_client
                session_rotations.append({
                    "before_ordinal": ordinal,
                    "newly_executed": newly_executed,
                    "state": client.edit_status()["state"],
                })
            print(json.dumps({"phase": "fault", "ordinal": ordinal, "total": len(manifest), "fault_id": fault_id}), flush=True)
            tx: str | None = None
            case: dict[str, Any] = {"schema_version": "dnspy.p02.fault-case.v1", "fault_id": fault_id, "golden": row}
            try:
                reset_before = client.call_tool_json("edit_test_fault", {"action": "reset"})
                begin = client.edit_begin(rid("begin-fault-suite"), assembly)
                tx = begin["result"]["transaction"]["transaction_id"]
                revision = 0
                applies: list[dict[str, Any]] = []
                for operation in fault_operations(row["operation_kind"]):
                    applied = client.edit_apply(rid("apply-fault-suite"), tx, revision, operation)
                    revision = applied["result"]["transaction"]["work_revision"]
                    applies.append(applied)
                review = client.edit_review(rid("review-fault-suite"), tx, revision)
                review_value = review["result"]["review"]
                armed = client.call_tool_json("edit_test_fault", {"action": "arm", "fault_id": fault_id})
                failed = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
                    "request_id": rid("live-fault-suite"), "transaction_id": tx,
                    "review_id": review_value["review_id"], "expected_revision": revision,
                    "confirmed_risk_ids": review_value["required_confirmation_ids"],
                }))
                expected_error = "EDIT_LIVE_STATE_UNKNOWN" if row["direction"] == "reverse" else "EDIT_INTERNAL_ERROR"
                assert error_code(failed) == expected_error, failed
                evidence = failed["execution_evidence"]
                assert evidence["fault_manifest"] == manifest and evidence["oracle_faults"] == manifest
                assert evidence["covered_faults"] == [row], evidence["covered_faults"]
                assert evidence["actual_mutation_trace"][-1] == row, evidence["actual_mutation_trace"]
                reset_after = client.call_tool_json("edit_test_fault", {"action": "reset"})
                if row["direction"] == "forward":
                    assert reset_after["state"] == "reviewed", reset_after
                    rolled_back = client.edit_rollback(rid("rollback-fault-suite"), tx)
                    tx = None
                else:
                    assert reset_after["state"] == "idle", reset_after
                    rolled_back = None
                    tx = None
                assert client.edit_status()["state"] == "idle"
                case.update({"result": "PASS", "reset_before": reset_before, "begin": begin,
                             "applies": applies, "review": review, "armed": armed, "failure": failed,
                             "reset_after": reset_after, "rollback": rolled_back})
                covered.append(fault_id)
                completed += 1
                newly_executed += 1
            except Exception as exc:
                case.update({"result": "FAIL", "error": {"type": type(exc).__name__, "message": str(exc)}})
                raise
            finally:
                if tx is not None:
                    try:
                        client.call_tool_json("edit_test_fault", {"action": "reset"})
                        if client.edit_status()["state"] != "idle":
                            client.edit_rollback(rid("rollback-fault-suite-cleanup"), tx)
                    except Exception as cleanup_exc:
                        case["cleanup_error"] = repr(cleanup_exc)
                artifact.write_text(json.dumps(case, ensure_ascii=False, indent=2), encoding="utf-8")
    finally:
        if owned_client is not None:
            owned_client.close()
    complete = covered == expected_ids and len(set(covered)) == 240
    if fault_limit is None:
        assert complete
    index = {
        "schema_version": "dnspy.p02.fault-suite.v1", "suite_id": suite_id,
        "manifest_count": len(manifest), "transaction_count": completed,
        "artifact_count": len([
            path for path in suite_root.glob("*.json")
            if path.name != "suite-index.json"
        ]),
        "covered_fault_ids": covered, "expected_fault_ids": expected_ids,
        "session_rotations": session_rotations,
        "elapsed_seconds": round(time.time() - started, 3), "result": "PASS" if complete else "PARTIAL",
    }
    (suite_root / "suite-index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    return index


def clone_session(client: DnSpyClient) -> DnSpyClient:
    clone = DnSpyClient(client.base_url, timeout=client.timeout)
    clone.session_id = client.session_id
    clone.protocol_version = client.protocol_version
    return clone


def barrier_call(client: DnSpyClient, action: str, name: str | None = None) -> dict[str, Any]:
    arguments: dict[str, Any] = {"action": action}
    if name is not None:
        arguments["name"] = name
    return client.call_tool_json("edit_test_barrier", arguments)


def wait_barrier(client: DnSpyClient, *, entered: bool = False,
                 waiters: int = 0, timeout: float = 30.0) -> dict[str, Any]:
    deadline = time.time() + timeout
    last: dict[str, Any] | None = None
    while time.time() < deadline:
        last = barrier_call(client, "snapshot")
        row = last["result"]
        if (not entered or row["entered"] is True) and row["operation_waiters"] >= waiters:
            return last
        time.sleep(0.025)
    raise AssertionError(f"test barrier did not reach entered={entered}, waiters={waiters}: {last}")


def captured_call(call: Any) -> dict[str, Any]:
    try:
        return {"kind": "response", "value": call()}
    except ToolCallError as exc:
        return {"kind": "tool_error", "value": json.loads(str(exc))}
    except Exception as exc:
        return {"kind": "transport_error", "type": type(exc).__name__, "message": str(exc)}


def run_transaction_lifecycle(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    result: dict[str, Any] = {}
    second = DnSpyClient.connect(client.base_url, timeout=180)
    try:
        print(json.dumps({"phase": "lifecycle", "case": "ownership"}), flush=True)
        begin_id = rid("begin-lifecycle")
        begin = client.edit_begin(begin_id, assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        busy = failing_call(lambda: second.edit_begin(rid("begin-second"), assembly))
        assert error_code(busy) == "EDIT_TRANSACTION_BUSY", busy
        owner = failing_call(lambda: second.edit_apply(rid("apply-owner"), tx, 0, {
            "kind": "type_add", "namespace": "P02", "name": "WrongOwner",
        }))
        assert error_code(owner) == "EDIT_OWNER_MISMATCH", owner
        rollback_id = rid("rollback-lifecycle")
        rollback = client.edit_rollback(rollback_id, tx)
        old_begin = client.edit_begin(begin_id, assembly)
        assert old_begin == begin and client.edit_status()["state"] == "idle"
        old_apply = failing_call(lambda: client.edit_apply(rid("old-apply"), tx, 0, {
            "kind": "type_add", "namespace": "P02", "name": "Old",
        }))
        old_review = failing_call(lambda: client.edit_review(rid("old-review"), tx, 0))
        old_rollback = client.edit_rollback(rollback_id, tx)
        assert error_code(old_apply) == "EDIT_TRANSACTION_NOT_FOUND"
        assert error_code(old_review) == "EDIT_TRANSACTION_NOT_FOUND"
        assert old_rollback == rollback
        result["ownership_and_terminal_replay"] = {
            "begin": begin, "busy": busy, "owner": owner, "rollback": rollback,
            "old_begin": old_begin, "old_apply": old_apply, "old_review": old_review,
            "old_rollback": old_rollback,
        }

        print(json.dumps({"phase": "lifecycle", "case": "timeout"}), flush=True)
        client.call_tool_json("edit_test_clock", {"action": "reset"})
        timeout_begin = client.edit_begin(rid("begin-timeout"), assembly)
        timeout_tx = timeout_begin["result"]["transaction"]["transaction_id"]
        at_599999 = client.call_tool_json("edit_test_clock", {"action": "advance", "advance_ms": 599999})
        still_active = client.edit_status()
        assert still_active["state"] == "editing" and still_active["result"]["transaction"]["transaction_id"] == timeout_tx
        at_600000 = client.call_tool_json("edit_test_clock", {"action": "advance", "advance_ms": 1})
        expired = client.edit_status()
        assert at_599999["state"] == "editing" and at_600000["state"] == "idle" and expired["state"] == "idle"
        client.call_tool_json("edit_test_clock", {"action": "reset"})
        result["timeout"] = {"begin": timeout_begin, "at_599999": at_599999,
                             "still_active": still_active, "at_600000": at_600000, "expired": expired}

        print(json.dumps({"phase": "lifecycle", "case": "delete-reconnect"}), flush=True)
        owner_client = DnSpyClient.connect(client.base_url, timeout=180)
        close_begin = owner_client.edit_begin(rid("begin-close"), assembly)
        old_session = owner_client.session_id
        owner_client.close()
        after_close = client.edit_status()
        assert after_close["state"] == "idle", after_close
        reconnected = DnSpyClient.connect(client.base_url, timeout=180)
        new_session = reconnected.session_id
        try:
            assert new_session != old_session
            new_begin = reconnected.edit_begin(rid("begin-reconnect"), assembly)
            reconnected.edit_rollback(rid("rollback-reconnect"), new_begin["result"]["transaction"]["transaction_id"])
        finally:
            reconnected.close()
        result["delete_and_same_url_reconnect"] = {
            "close_begin": close_begin, "old_session": old_session,
            "after_close": after_close, "new_session": new_session,
        }
        return result
    finally:
        second.close()
        status = client.edit_status()
        if status["state"] != "idle" and "transaction" in status.get("result", {}):
            client.edit_rollback(rid("rollback-lifecycle-cleanup"), status["result"]["transaction"]["transaction_id"])


def run_concurrency(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    results: dict[str, Any] = {}

    def pair_call(left: Any, right: Any) -> list[Any]:
        barrier = threading.Barrier(2)
        def invoke(call: Any) -> Any:
            barrier.wait()
            try:
                return call()
            except ToolCallError as exc:
                return json.loads(str(exc))
        with ThreadPoolExecutor(max_workers=2) as pool:
            return [future.result() for future in (pool.submit(invoke, left), pool.submit(invoke, right))]

    print(json.dumps({"phase": "concurrency", "case": "same-request"}), flush=True)
    begin = client.edit_begin(rid("begin-followers"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    clone = clone_session(client)
    try:
        request_id = rid("apply-follower")
        operation = {"kind": "type_add", "namespace": "P02", "name": "Follower"}
        same = pair_call(
            lambda: client.edit_apply(request_id, tx, 0, operation),
            lambda: clone.edit_apply(request_id, tx, 0, operation),
        )
        assert same[0] == same[1] and same[0]["ok"] is True, same
        results["same_request_follower"] = same
    finally:
        clone.session_id = None
        clone.close()
        client.edit_rollback(rid("rollback-followers"), tx)

    print(json.dumps({"phase": "concurrency", "case": "changed-request"}), flush=True)
    begin = client.edit_begin(rid("begin-reuse-race"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    clone = clone_session(client)
    try:
        request_id = rid("apply-reuse-race")
        changed = pair_call(
            lambda: client.edit_apply(request_id, tx, 0, {"kind": "type_add", "namespace": "P02", "name": "One"}),
            lambda: clone.edit_apply(request_id, tx, 0, {"kind": "type_add", "namespace": "P02", "name": "Two"}),
        )
        assert sum(item.get("ok") is True for item in changed) == 1
        assert [error_code(item) for item in changed].count("REQUEST_ID_REUSE") == 1
        results["changed_request_follower"] = changed
    finally:
        clone.session_id = None
        clone.close()
        client.edit_rollback(rid("rollback-reuse-race"), tx)

    print(json.dumps({"phase": "concurrency", "case": "different-request"}), flush=True)
    begin = client.edit_begin(rid("begin-different-race"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    clone = clone_session(client)
    try:
        long_a = body(*([{"opcode": "nop"}] * 4095 + [{"opcode": "ret"}]), max_stack=1)
        long_b = body(*([{"opcode": "break"}] + [{"opcode": "nop"}] * 4094 + [{"opcode": "ret"}]), max_stack=1)
        different = pair_call(
            lambda: client.edit_apply(rid("apply-different-a"), tx, 0, {"kind": "method_body_replace", "target": token(0x06000001), "body": long_a}),
            lambda: clone.edit_apply(rid("apply-different-b"), tx, 0, {"kind": "method_body_replace", "target": token(0x06000001), "body": long_b}),
        )
        assert sum(item.get("ok") is True for item in different) == 1, different
        loser_code = next(error_code(item) for item in different if item.get("ok") is not True)
        # This scheduling-only probe has no server-side barrier: the losing
        # request may observe the operation while it is active (BUSY), or just
        # after it commits (REVISION_CONFLICT).  The following barrier suite
        # proves the exact in-flight BUSY contract deterministically.
        assert loser_code in ("EDIT_TRANSACTION_BUSY", "EDIT_REVISION_CONFLICT"), different
        results["different_request_serialized"] = different
    finally:
        clone.session_id = None
        clone.close()
        client.edit_rollback(rid("rollback-different-race"), tx)
    assert client.edit_status()["state"] == "idle"
    return results


def run_barrier_concurrency(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    """Deterministic lease/close races required by ACC-002."""
    observer = DnSpyClient.connect(client.base_url, timeout=180)
    results: dict[str, Any] = {}

    def reset() -> None:
        barrier_call(observer, "reset")

    try:
        # Identical request followers wait outside the coordinator monitor and
        # receive the byte-identical settled response.
        begin = client.edit_begin(rid("begin-barrier-follower"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        clone = clone_session(client)
        request_id = rid("barrier-follower")
        operation = {"kind": "type_add", "namespace": "P02Barrier", "name": "Follower"}
        barrier_call(client, "arm", "apply_before_mutation")
        with ThreadPoolExecutor(max_workers=2) as pool:
            first = pool.submit(captured_call, lambda: client.edit_apply(request_id, tx, 0, operation))
            entered = wait_barrier(observer, entered=True)
            follower = pool.submit(captured_call, lambda: clone.edit_apply(request_id, tx, 0, operation))
            waiting = wait_barrier(observer, entered=True, waiters=1)
            released = barrier_call(observer, "release")
            first_result, follower_result = first.result(), follower.result()
        assert first_result["kind"] == follower_result["kind"] == "response"
        assert first_result["value"] == follower_result["value"]
        results["same_request_follower"] = {"begin": begin, "entered": entered, "waiting": waiting,
                                               "released": released, "original": first_result,
                                               "follower": follower_result}
        reset(); clone.session_id = None; clone.close()
        client.edit_rollback(rid("rollback-barrier-follower"), tx)

        # Same ID with a different payload is a follower only for scheduling;
        # after the original settles the request cache rejects its payload.
        begin = client.edit_begin(rid("begin-barrier-reuse"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        clone = clone_session(client); request_id = rid("barrier-reuse")
        barrier_call(client, "arm", "apply_before_mutation")
        with ThreadPoolExecutor(max_workers=2) as pool:
            first = pool.submit(captured_call, lambda: client.edit_apply(request_id, tx, 0, {
                "kind": "type_add", "namespace": "P02Barrier", "name": "One"}))
            wait_barrier(observer, entered=True)
            follower = pool.submit(captured_call, lambda: clone.edit_apply(request_id, tx, 0, {
                "kind": "type_add", "namespace": "P02Barrier", "name": "Two"}))
            waiting = wait_barrier(observer, entered=True, waiters=1)
            barrier_call(observer, "release")
            pair = [first.result(), follower.result()]
        codes = [error_code(x.get("value", {})) for x in pair if x["kind"] == "tool_error"]
        assert sum(x["kind"] == "response" for x in pair) == 1 and codes == ["REQUEST_ID_REUSE"], pair
        results["changed_request_follower"] = {"waiting": waiting, "pair": pair}
        reset(); clone.session_id = None; clone.close()
        client.edit_rollback(rid("rollback-barrier-reuse"), tx)

        # Different IDs/tools never enter the waiter path.
        begin = client.edit_begin(rid("begin-barrier-busy"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        clone = clone_session(client)
        barrier_call(client, "arm", "apply_before_mutation")
        with ThreadPoolExecutor(max_workers=1) as pool:
            first = pool.submit(captured_call, lambda: clone.edit_apply(rid("barrier-busy-apply"), tx, 0, {
                "kind": "type_add", "namespace": "P02Barrier", "name": "Busy"}))
            entered = wait_barrier(observer, entered=True)
            review_busy = captured_call(lambda: client.edit_review(rid("barrier-busy-review"), tx, 0))
            assert review_busy["kind"] == "tool_error" and error_code(review_busy["value"]) == "EDIT_TRANSACTION_BUSY"
            assert wait_barrier(observer, entered=True)["result"]["operation_waiters"] == 0
            barrier_call(observer, "release")
            applied = first.result()
        assert applied["kind"] == "response"
        results["different_request_busy"] = {"entered": entered, "review": review_busy, "apply": applied}
        reset(); clone.session_id = None; clone.close()
        client.edit_rollback(rid("rollback-barrier-busy"), tx)

        # Rollback cancels an in-flight apply, releases its barrier, then waits
        # outside the monitor and completes terminal cleanup.
        begin = client.edit_begin(rid("begin-barrier-rollback"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        clone = clone_session(client)
        barrier_call(client, "arm", "apply_before_mutation")
        with ThreadPoolExecutor(max_workers=1) as pool:
            in_flight = pool.submit(captured_call, lambda: clone.edit_apply(rid("barrier-rollback-apply"), tx, 0, {
                "kind": "type_add", "namespace": "P02Barrier", "name": "Cancelled"}))
            entered = wait_barrier(observer, entered=True)
            rollback = client.edit_rollback(rid("barrier-rollback"), tx)
            apply_result = in_flight.result()
        assert apply_result["kind"] == "tool_error" and error_code(apply_result["value"]) == "EDIT_TRANSACTION_NOT_FOUND"
        assert rollback["state"] == "idle" and client.edit_status()["state"] == "idle"
        results["apply_vs_rollback"] = {"entered": entered, "apply": apply_result, "rollback": rollback}
        reset(); clone.session_id = None; clone.close()

        # Advancing the deterministic clock expires a review that is paused
        # outside the monitor at exactly the 600000ms boundary.
        client.call_tool_json("edit_test_clock", {"action": "reset"})
        begin = client.edit_begin(rid("begin-barrier-timeout"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        applied = client.edit_apply(rid("barrier-timeout-apply"), tx, 0, {
            "kind": "type_add", "namespace": "P02Barrier", "name": "Timeout"})
        clone = clone_session(client)
        barrier_call(client, "arm", "review_before_validation")
        with ThreadPoolExecutor(max_workers=1) as pool:
            in_flight = pool.submit(captured_call, lambda: clone.edit_review(
                rid("barrier-timeout-review"), tx, applied["result"]["transaction"]["work_revision"]))
            entered = wait_barrier(observer, entered=True)
            expired = observer.call_tool_json("edit_test_clock", {"action": "advance", "advance_ms": 600000})
            review_result = in_flight.result()
        assert expired["state"] == "idle"
        assert review_result["kind"] == "tool_error" and error_code(review_result["value"]) == "EDIT_TRANSACTION_NOT_FOUND"
        results["review_vs_timeout"] = {"entered": entered, "expired": expired, "review": review_result}
        reset(); clone.session_id = None; clone.close()
        client.call_tool_json("edit_test_clock", {"action": "reset"})

        # A session that closes while begin is copying the module cannot publish
        # a transaction after its transport generation has ended.
        owner = DnSpyClient.connect(client.base_url, timeout=180)
        clone = clone_session(owner)
        barrier_call(owner, "arm", "begin_after_copy")
        with ThreadPoolExecutor(max_workers=1) as pool:
            in_flight = pool.submit(captured_call, lambda: clone.edit_begin(rid("barrier-begin-close"), assembly))
            entered = wait_barrier(observer, entered=True)
            old_session = owner.session_id
            owner.close()
            begin_result = in_flight.result()
        assert begin_result["kind"] in {"tool_error", "transport_error"}, begin_result
        if begin_result["kind"] == "tool_error":
            assert error_code(begin_result["value"]) == "EDIT_TRANSACTION_NOT_FOUND", begin_result
        assert client.edit_status()["state"] == "idle"
        results["begin_vs_session_close"] = {"entered": entered, "old_session": old_session,
                                                "begin": begin_result, "status": client.edit_status()}
        reset(); clone.session_id = None; clone.close()

        # DELETE during a paused apply releases the private transaction; a new
        # session at the same explicit URL can immediately begin and roll back.
        owner = DnSpyClient.connect(client.base_url, timeout=180)
        begin = owner.edit_begin(rid("begin-barrier-delete"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        clone = clone_session(owner)
        barrier_call(owner, "arm", "apply_before_mutation")
        with ThreadPoolExecutor(max_workers=1) as pool:
            in_flight = pool.submit(captured_call, lambda: clone.edit_apply(rid("barrier-delete-apply"), tx, 0, {
                "kind": "type_add", "namespace": "P02Barrier", "name": "Delete"}))
            entered = wait_barrier(observer, entered=True)
            old_session = owner.session_id
            owner.close()
            apply_result = in_flight.result()
        assert apply_result["kind"] in {"tool_error", "transport_error"}, apply_result
        assert client.edit_status()["state"] == "idle"
        fresh = DnSpyClient.connect(client.base_url, timeout=180)
        fresh_begin = fresh.edit_begin(rid("begin-after-delete"), assembly)
        fresh_rollback = fresh.edit_rollback(rid("rollback-after-delete"), fresh_begin["result"]["transaction"]["transaction_id"])
        results["apply_vs_delete"] = {"entered": entered, "old_session": old_session,
                                          "apply": apply_result, "new_session": fresh.session_id,
                                          "fresh_begin": fresh_begin, "fresh_rollback": fresh_rollback}
        reset(); clone.session_id = None; clone.close(); fresh.close()

        assert client.edit_status()["state"] == "idle"
        return results
    finally:
        try:
            reset()
        except Exception:
            pass
        observer.close()


def run_operation_matrix(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    kinds = (
        "type_add", "type_update", "type_remove", "method_add", "method_update", "method_remove",
        "field_add", "field_update", "field_remove", "property_add", "property_update", "property_remove",
        "event_add", "event_update", "event_remove", "parameter_add", "parameter_update", "parameter_remove",
        "generic_parameter_add", "generic_parameter_update", "generic_parameter_remove", "method_body_replace",
    )
    results: dict[str, Any] = {}
    for ordinal, kind in enumerate(kinds, 1):
        print(json.dumps({"phase": "operation-matrix", "ordinal": ordinal, "kind": kind}), flush=True)
        begin = client.edit_begin(rid("begin-operation"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        baseline = begin["result"]["fingerprints"]["baseline_live"]
        revision = 0
        row: dict[str, Any] = {"begin": begin, "kind": kind}
        try:
            operations = fault_operations(kind)
            for prerequisite in operations[:-1]:
                applied = client.edit_apply(rid("apply-prerequisite"), tx, revision, prerequisite)
                revision = applied["result"]["transaction"]["work_revision"]
                row.setdefault("prerequisites", []).append(applied)
            invalid = dict(operations[-1])
            invalid["raw_metadata"] = "00"
            before_invalid = client.edit_status()
            try:
                client.call_tool_json("edit_apply", {
                    "request_id": rid("apply-invalid"), "transaction_id": tx,
                    "expected_revision": revision, "operation": invalid,
                })
                raise AssertionError(f"{kind} accepted an unknown raw field")
            except DnSpyProtocolError as exc:
                assert exc.code == -32602, exc
                invalid_result = {"code": exc.code, "message": str(exc)}
            after_invalid = client.edit_status()
            assert after_invalid["result"]["transaction"]["work_revision"] == revision
            assert after_invalid["result"]["fingerprints"] == before_invalid["result"]["fingerprints"]
            applied = client.edit_apply(rid("apply-operation"), tx, revision, operations[-1])
            revision = applied["result"]["transaction"]["work_revision"]
            review = client.edit_review(rid("review-operation"), tx, revision)
            assert review["result"]["roundtrip_validation"]["state"] == "passed"
            review_value = review["result"]["review"]
            live = client.call_tool_json("edit_test_apply_and_restore", {
                "request_id": rid("live-operation"), "transaction_id": tx,
                "review_id": review_value["review_id"], "expected_revision": revision,
                "confirmed_risk_ids": review_value["required_confirmation_ids"],
            })
            assert live["ok"] is True, live
            live_result = live["result"]
            assert live_result["pre_live_fingerprint"] == baseline
            assert live_result["post_restore_fingerprint"] == baseline
            row.update({"invalid": invalid_result, "before_invalid": before_invalid,
                        "after_invalid": after_invalid, "apply": applied,
                        "review": review, "live": live})
        finally:
            rollback = client.edit_rollback(rid("rollback-operation"), tx)
            row["rollback"] = rollback
        results[kind] = row
    assert len(results) == 22 and client.edit_status()["state"] == "idle"
    return results


def run_unsupported_targets(client: DnSpyClient, values: list[str]) -> dict[str, Any]:
    results: dict[str, Any] = {}
    for value in values:
        kind, path_text = value.split("=", 1)
        path = str(Path(path_text).resolve())
        opened = client.call_tool_json("open_files", {"paths": [path]})
        assert opened["failed_count"] == 0 and opened["loaded"], opened
        loaded_name = opened["loaded"][0]["name"]
        assembly = "P02DynamicFixture" if kind == "mixed-mode" else Path(path).stem
        assemblies = client.call_tool_json("list_assemblies", {})
        begin = failing_call(lambda: client.edit_begin(rid("begin-unsupported"), assembly))
        assert error_code(begin) == "EDIT_CAPABILITY_UNAVAILABLE", begin
        status = client.edit_status()
        assert status["state"] == "idle", status
        results[kind] = {"path": path, "loaded_name": loaded_name, "open_files": opened,
                         "analysis": assemblies, "begin": begin, "status": status}
    return results


def protocol_rejection(call: Any) -> dict[str, Any]:
    try:
        call()
    except DnSpyProtocolError as exc:
        assert exc.code == -32602, exc
        return {"code": exc.code, "message": str(exc)}
    raise AssertionError("request unexpectedly passed inputSchema validation")


def apply_expect_domain(client: DnSpyClient, tx: str, revision: int,
                        operation: dict[str, Any], code: str = "EDIT_VALIDATION_FAILED") -> dict[str, Any]:
    before = client.edit_status()
    failed = failing_call(lambda: client.edit_apply(rid("apply-negative"), tx, revision, operation))
    assert error_code(failed) == code, failed
    after = client.edit_status()
    assert after["result"]["transaction"]["work_revision"] == revision
    assert after["result"]["fingerprints"] == before["result"]["fingerprints"]
    return {"failure": failed, "before": before, "after": after}


def run_typesig_vectors(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    vectors = [
        ("type-var-zero-valid", "!0", True, "field", token(0x02000004)),
        ("type-var-zero-invalid", "!0", False, "base", None),
        ("type-var-boundary-invalid", "!1", False, "field", token(0x02000004)),
        ("method-var-zero-valid", "!!0", True, "parameter", token(0x06000011)),
        ("method-var-zero-invalid", "!!0", False, "parameter", token(0x0600000D)),
        ("method-var-boundary-invalid", "!!1", False, "parameter", token(0x06000011)),
        ("void-return-valid", "System.Void", True, "return", token(0x0600000B)),
        ("void-field-invalid", "System.Void", False, "field", token(0x02000004)),
        ("void-generic-invalid", "Example.Box`1<System.Void>", False, "field", token(0x02000004)),
        ("final-byref-valid", "Example.Widget*&", True, "parameter", token(0x0600000D)),
        ("double-byref-invalid", "System.Int32&&", False, "parameter", token(0x0600000D)),
        ("suffix-after-byref-invalid", "System.Int32&*", False, "parameter", token(0x0600000D)),
        ("generic-arity-valid", "Example.Map`2<System.String,System.Int32[]>", True, "field", token(0x02000004)),
        ("generic-arity-mismatch", "Example.Box`1<System.String,System.Int32>", False, "field", token(0x02000004)),
    ]
    begin = client.edit_begin(rid("begin-typesig"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    revision = 0
    results: dict[str, Any] = {}
    try:
        for index, (vector_id, text, expected, context, owner) in enumerate(vectors):
            print(json.dumps({"phase": "typesig", "case_id": vector_id, "expected": expected}), flush=True)
            if context == "field":
                operation = {"kind": "field_add", "owner_type": owner, "name": f"Sig{index}", "field_type": text}
            elif context == "base":
                operation = {"kind": "type_add", "name": f"Sig{index}", "base_type": text}
            elif context == "parameter":
                operation = {"kind": "parameter_add", "owner_method": owner, "parameter_index": 0,
                             "name": f"sig{index}", "parameter_type": text}
            else:
                operation = {"kind": "method_update", "target": owner, "return_type": text}
            if expected:
                response = client.edit_apply(rid("apply-typesig"), tx, revision, operation)
                revision = response["result"]["transaction"]["work_revision"]
                results[vector_id] = {"expected": True, "response": response}
            else:
                results[vector_id] = {"expected": False,
                                      **apply_expect_domain(client, tx, revision, operation)}
        review = client.edit_review(rid("review-typesig"), tx, revision)
        assert review["result"]["roundtrip_validation"]["state"] == "passed"
        return {"begin": begin, "vectors": results, "review": review}
    finally:
        client.edit_rollback(rid("rollback-typesig"), tx)


def run_attribute_vectors(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    domains = {
        "TypeAttributes": (16219583, 64, lambda value, n: {"kind": "type_add", "name": n, "attributes": value}),
        "MethodAttributes": (65535, 65536, lambda value, n: {**method("unused", n), "owner_type": token(0x02000004), "attributes": value}),
        "MethodImplAttributes": (6143, 2048, lambda value, n: {**method("unused", n), "owner_type": token(0x02000004), "impl_attributes": value}),
        "FieldAttributes": (47095, 8, lambda value, n: {"kind": "field_add", "owner_type": token(0x02000004), "name": n, "field_type": "System.Int32", "attributes": value}),
        "PropertyAttributes": (5632, 1, lambda value, n: {"kind": "property_add", "owner_type": token(0x02000004), "name": n, "property_type": "System.String", "attributes": value}),
        "EventAttributes": (1536, 1, lambda value, n: {"kind": "event_add", "owner_type": token(0x02000004), "name": n, "event_type": "System.Action", "add_method": token(0x06000014), "remove_method": token(0x06000015), "attributes": value}),
        "ParamAttributes": (12319, 32, lambda value, n: {"kind": "parameter_add", "owner_method": token(0x0600000D), "parameter_index": 0, "name": n, "parameter_type": "System.Int32", "attributes": value}),
        "GenericParamAttributes": (63, 64, lambda value, n: {"kind": "generic_parameter_add", "owner": token(0x06000010), "generic_index": 0, "name": n, "attributes": value}),
    }
    results: dict[str, Any] = {}
    for ordinal, (domain, (mask, invalid_bit, factory)) in enumerate(domains.items()):
        for label, value, expected in (("zero", 0, True), ("mask", mask, True), ("sparse-invalid", invalid_bit, False)):
            print(json.dumps({"phase": "attributes", "case_id": f"{domain}:{label}",
                              "expected": expected}), flush=True)
            begin = client.edit_begin(rid("begin-attrs"), assembly)
            tx = begin["result"]["transaction"]["transaction_id"]
            operation = factory(value, f"Attr{ordinal}{label.replace('-', '')}")
            try:
                if expected:
                    response = client.edit_apply(rid("apply-attrs"), tx, 0, operation)
                    results[f"{domain}:{label}"] = {"expected": True, "response": response}
                else:
                    # Sparse undefined bits are intentionally inside several numeric maxima;
                    # the runtime enum-mask guard must still reject them as -32602.
                    results[f"{domain}:{label}"] = {"expected": False,
                        "response": protocol_rejection(lambda: client.edit_apply(rid("apply-attrs"), tx, 0, operation))}
            finally:
                client.edit_rollback(rid("rollback-attrs"), tx)
    return results


def run_constant_vectors(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    values = [
        ("i1-min", "System.SByte", "i1", -128), ("i1-max", "System.SByte", "i1", 127),
        ("u1-min", "System.Byte", "u1", 0), ("u1-max", "System.Byte", "u1", 255),
        ("i2-min", "System.Int16", "i2", -32768), ("i2-max", "System.Int16", "i2", 32767),
        ("u2-min", "System.UInt16", "u2", 0), ("u2-max", "System.UInt16", "u2", 65535),
        ("i4-min", "System.Int32", "i4", -(2**31)), ("i4-max", "System.Int32", "i4", 2**31-1),
        ("u4-min", "System.UInt32", "u4", 0), ("u4-max", "System.UInt32", "u4", 2**32-1),
        ("i8-min", "System.Int64", "i8", -(2**63)), ("i8-max", "System.Int64", "i8", 2**63-1),
        ("u8-min", "System.UInt64", "u8", 0), ("u8-max", "System.UInt64", "u8", 2**64-1),
        ("r4-min", "System.Single", "r4", -3.4028234663852886e38), ("r4-max", "System.Single", "r4", 3.4028234663852886e38),
        ("r8-min", "System.Double", "r8", -1.7976931348623157e308), ("r8-max", "System.Double", "r8", 1.7976931348623157e308),
        ("false", "System.Boolean", "boolean", False), ("true", "System.Boolean", "boolean", True),
        ("char-bmp", "System.Char", "char", "A"), ("char-supplementary", "System.Char", "char", "😀"),
        ("string-empty", "System.String", "string", ""), ("null-reference", "System.String", "null", None),
    ]
    begin = client.edit_begin(rid("begin-constants"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    revision = 0
    results: dict[str, Any] = {}
    try:
        for index, (case_id, field_type, kind, value) in enumerate(values):
            print(json.dumps({"phase": "constants", "case_id": case_id, "expected": True}), flush=True)
            operation = {"kind": "field_add", "owner_type": token(0x02000004), "name": f"Const{index}",
                         "field_type": field_type, "constant": {"kind": kind, "value": value}}
            response = client.edit_apply(rid("apply-constant"), tx, revision, operation)
            revision = response["result"]["transaction"]["work_revision"]
            results[case_id] = response
        invalids = {
            "i1-under": {"kind": "i1", "value": -129},
            "u1-over": {"kind": "u1", "value": 256},
            "char-empty": {"kind": "char", "value": ""},
            "char-many": {"kind": "char", "value": "AB"},
        }
        for case_id, constant in invalids.items():
            print(json.dumps({"phase": "constants", "case_id": case_id, "expected": False}), flush=True)
            operation = {"kind": "field_add", "owner_type": token(0x02000004), "name": case_id,
                         "field_type": "System.Int32", "constant": constant}
            results[case_id] = protocol_rejection(lambda op=operation: client.edit_apply(rid("apply-constant-invalid"), tx, revision, op))
        results["null-value-type"] = apply_expect_domain(client, tx, revision, {
            "kind": "field_add", "owner_type": token(0x02000004), "name": "NullInt",
            "field_type": "System.Int32", "constant": {"kind": "null", "value": None},
        })
        added = client.edit_apply(rid("apply-clear-source"), tx, revision, {
            "kind": "field_add", "owner_type": token(0x02000004), "name": "ClearMe",
            "field_type": "System.Int32", "constant": {"kind": "i4", "value": 7},
        })
        revision = added["result"]["transaction"]["work_revision"]
        cleared = client.edit_apply(rid("apply-clear"), tx, revision, {
            "kind": "field_update", "target": {"object_id": added["result"]["created_object_ids"][0]},
            "clear_constant": True,
        })
        revision = cleared["result"]["transaction"]["work_revision"]
        results["clear-constant"] = {"add": added, "clear": cleared}
        review = client.edit_review(rid("review-constants"), tx, revision)
        assert review["result"]["roundtrip_validation"]["state"] == "passed"
        return {"vectors": results, "review": review}
    finally:
        client.edit_rollback(rid("rollback-constants"), tx)


def opcode_operand(operand_type: str) -> dict[str, Any] | None:
    return {
        "ShortInlineI": {"kind": "i32", "value": 0}, "InlineI": {"kind": "i32", "value": 0},
        "InlineI8": {"kind": "i64", "value": 0}, "ShortInlineR": {"kind": "f32", "value": 0.0},
        "InlineR": {"kind": "f64", "value": 0.0}, "InlineString": {"kind": "string", "value": "p02"},
        "InlineBrTarget": {"kind": "label", "instruction_index": 0},
        "ShortInlineBrTarget": {"kind": "label", "instruction_index": 0},
        "InlineSwitch": {"kind": "switch", "instruction_indices": [0]},
        "InlineVar": {"kind": "local", "local_index": 0}, "ShortInlineVar": {"kind": "local", "local_index": 0},
        "InlineField": {"kind": "token", "token": "0x04000002"},
        "InlineMethod": {"kind": "token", "token": "0x0600000b"},
        "InlineType": {"kind": "token", "token": "0x02000004"},
        "InlineTok": {"kind": "token", "token": "0x02000004"},
        "InlineSig": {"kind": "token", "token": "0x11000001"},
    }.get(operand_type)


def opcode_body(opcode: str, operand_type: str, operand: dict[str, Any] | None) -> dict[str, Any]:
    instruction: dict[str, Any] = {"opcode": opcode}
    if operand is not None:
        instruction["operand"] = operand
    instructions = [{"opcode": "ret"}, instruction]
    required_followers = {
        "tail.": {"opcode": "call", "operand": {"kind": "token", "token": "0x0600000b"}},
        "constrained.": {"opcode": "callvirt", "operand": {"kind": "token", "token": "0x0600000b"}},
        "readonly.": {"opcode": "ldelema", "operand": {"kind": "token", "token": "0x02000004"}},
        "no.": {"opcode": "ldelem.i4"},
        "unaligned.": {"opcode": "ldind.i4"},
        "volatile.": {"opcode": "ldfld", "operand": {"kind": "token", "token": "0x04000002"}},
    }
    if opcode.lower() in required_followers:
        instructions.append(required_followers[opcode.lower()])
    return {"init_locals": True, "max_stack": 8, "instructions": instructions,
            "locals": [{"type": "System.Int32", "name": "v"}], "exception_handlers": []}


def run_opcode_operand_matrix(client: DnSpyClient, assembly: str, facts_path: Path) -> dict[str, Any]:
    facts = json.loads(facts_path.read_text(encoding="utf-8"))
    opcodes: dict[str, str] = facts["opcodes"]
    begin = client.edit_begin(rid("begin-opcodes"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    revision = 0
    rows: dict[str, Any] = {}
    try:
        for ordinal, (opcode, operand_type) in enumerate(sorted(opcodes.items())):
            print(json.dumps({"phase": "opcode-matrix", "ordinal": ordinal + 1,
                              "total": len(opcodes), "opcode": opcode}), flush=True)
            correct = opcode_operand(operand_type)
            if opcode.lower() in {"no.", "unaligned."}:
                # Both prefixes use ShortInlineI, but zero is not a legal semantic
                # value: no. uses a non-empty check mask and unaligned. accepts
                # alignment 1, 2, or 4.
                correct = {"kind": "i32", "value": 1}
            operation = {"kind": "method_body_replace", "target": token(0x06000013),
                         "body": opcode_body(opcode, operand_type, correct)}
            if opcode.lower() in {
                "prefix1", "prefix2", "prefix3", "prefix4", "prefix5", "prefix6", "prefix7", "prefixref",
            }:
                rejected = apply_expect_domain(client, tx, revision, operation)
                rows[opcode] = {"operand_type": operand_type, "reserved_prefix": True,
                                "apply": rejected}
                continue
            applied = client.edit_apply(rid("apply-opcode"), tx, revision, operation)
            revision = applied["result"]["transaction"]["work_revision"]
            mismatch_operand = {"kind": "i32", "value": 1} if operand_type == "InlineNone" else None
            mismatch = {"kind": "method_body_replace", "target": token(0x06000013),
                        "body": opcode_body(opcode, operand_type, mismatch_operand)}
            negative = apply_expect_domain(client, tx, revision, mismatch)
            rows[opcode] = {"operand_type": operand_type, "apply": applied, "mismatch": negative}
        review = client.edit_review(rid("review-opcodes"), tx, revision)
        assert review["result"]["roundtrip_validation"]["state"] == "passed"
        return {"facts_sha256": hashlib.sha256(facts_path.read_bytes()).hexdigest(),
                "row_count": len(rows), "rows": rows, "review": review}
    finally:
        client.edit_rollback(rid("rollback-opcodes"), tx)


def run_accessor_nullability(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    results: dict[str, Any] = {}
    for name, operation in {
        "property-add-null": {"kind": "property_add", "owner_type": token(0x02000004), "name": "BadProperty", "property_type": "System.String", "getter": None},
        "event-add-null": {"kind": "event_add", "owner_type": token(0x02000004), "name": "BadEvent", "event_type": "System.Action", "add_method": None, "remove_method": token(0x06000015)},
    }.items():
        print(json.dumps({"phase": "accessor-nullability", "case_id": name, "expected": False}), flush=True)
        begin = client.edit_begin(rid("begin-accessor-negative"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        try:
            results[name] = protocol_rejection(lambda op=operation: client.edit_apply(rid("apply-accessor-negative"), tx, 0, op))
        finally:
            client.edit_rollback(rid("rollback-accessor-negative"), tx)
    begin = client.edit_begin(rid("begin-accessor-update"), assembly)
    print(json.dumps({"phase": "accessor-nullability", "case_id": "update-null-clears",
                      "expected": True}), flush=True)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        property_clear = client.edit_apply(rid("clear-property-getter"), tx, 0, {
            "kind": "property_update", "target": token(0x17000001), "getter": None,
        })
        event_set = client.edit_apply(rid("set-event-raise"), tx, 1, {
            "kind": "event_update", "target": token(0x14000001), "raise_method": token(0x0600000B),
        })
        event_clear = client.edit_apply(rid("clear-event-raise"), tx, 2, {
            "kind": "event_update", "target": token(0x14000001), "raise_method": None,
        })
        review = client.edit_review(rid("review-accessor"), tx, 3)
        assert review["result"]["roundtrip_validation"]["state"] == "passed"
        results["update-null-clears"] = {"property": property_clear, "event_set": event_set,
                                          "event_clear": event_clear, "review": review}
        return results
    finally:
        client.edit_rollback(rid("rollback-accessor"), tx)


def run_structural_guards(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    """Hard validation cases whose failure must leave revision and fingerprints unchanged."""

    def rejected(case_id: str, operation: dict[str, Any]) -> dict[str, Any]:
        print(json.dumps({"phase": "structural-guards", "case_id": case_id}), flush=True)
        begin = client.edit_begin(rid("begin-structural"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        try:
            return {"begin": begin, **apply_expect_domain(client, tx, 0, operation)}
        finally:
            client.edit_rollback(rid("rollback-structural"), tx)

    results: dict[str, Any] = {}
    bodies = {
        "stack-underflow": body({"opcode": "pop"}, {"opcode": "ret"}, max_stack=1),
        "wrong-void-ret": body({"opcode": "ldc.i4.0"}, {"opcode": "ret"}, max_stack=1),
        "maxstack-too-small": body({"opcode": "ldc.i4.0"}, {"opcode": "pop"}, {"opcode": "ret"}, max_stack=0),
        "invalid-prefix": body({"opcode": "tail."}, {"opcode": "nop"}, {"opcode": "ret"}, max_stack=0),
        "reserved-prefix": body({"opcode": "prefix1"}, {"opcode": "nop"}, {"opcode": "ret"}, max_stack=0),
        "invalid-unaligned-value": body(
            {"opcode": "unaligned.", "operand": {"kind": "i32", "value": 3}},
            {"opcode": "ldind.i4"}, {"opcode": "pop"}, {"opcode": "ret"}, max_stack=1),
        "local-out-of-range": {
            "init_locals": True, "max_stack": 1,
            "instructions": [{"opcode": "ldloc", "operand": {"kind": "local", "local_index": 0}},
                             {"opcode": "pop"}, {"opcode": "ret"}],
            "locals": [], "exception_handlers": [],
        },
        "arg-out-of-range": body(
            {"opcode": "ldarg", "operand": {"kind": "arg", "argument_index": 1}},
            {"opcode": "pop"}, {"opcode": "ret"}, max_stack=1),
        "branch-into-handler": {
            "init_locals": False, "max_stack": 1,
            "instructions": [
                {"opcode": "br", "operand": {"kind": "label", "instruction_index": 1}},
                {"opcode": "pop"}, {"opcode": "ret"},
            ],
            "locals": [],
            "exception_handlers": [{
                "kind": "catch", "try_start": 0, "try_end": 1,
                "handler_start": 1, "handler_end": 2, "filter_start": None,
                "catch_type": "System.Exception",
            }],
        },
        "finally-with-catch-type": {
            "init_locals": False, "max_stack": 0,
            "instructions": [{"opcode": "leave", "operand": {"kind": "label", "instruction_index": 2}},
                             {"opcode": "endfinally"}, {"opcode": "ret"}],
            "locals": [],
            "exception_handlers": [{
                "kind": "finally", "try_start": 0, "try_end": 1,
                "handler_start": 1, "handler_end": 2, "filter_start": None,
                "catch_type": "System.Exception",
            }],
        },
        "crossing-eh-regions": {
            "init_locals": False, "max_stack": 1,
            "instructions": [{"opcode": "nop"}, {"opcode": "nop"}, {"opcode": "nop"},
                             {"opcode": "nop"}, {"opcode": "pop"}, {"opcode": "leave", "operand": {"kind": "label", "instruction_index": 8}},
                             {"opcode": "pop"}, {"opcode": "leave", "operand": {"kind": "label", "instruction_index": 8}},
                             {"opcode": "ret"}],
            "locals": [],
            "exception_handlers": [
                {"kind": "catch", "try_start": 0, "try_end": 3,
                 "handler_start": 4, "handler_end": 6, "filter_start": None,
                 "catch_type": "System.Exception"},
                {"kind": "catch", "try_start": 2, "try_end": 4,
                 "handler_start": 6, "handler_end": 8, "filter_start": None,
                 "catch_type": "System.Exception"},
            ],
        },
        "filter-missing-start": {
            "init_locals": False, "max_stack": 1,
            "instructions": [{"opcode": "nop"}, {"opcode": "pop"}, {"opcode": "ret"}],
            "locals": [],
            "exception_handlers": [{
                "kind": "filter", "try_start": 0, "try_end": 1,
                "handler_start": 1, "handler_end": 2, "filter_start": None,
                "catch_type": None,
            }],
        },
    }
    for case_id, invalid_body in bodies.items():
        results[case_id] = rejected(case_id, {
            "kind": "method_body_replace", "target": token(0x06000013), "body": invalid_body,
        })

    results["accessor-signature"] = rejected("accessor-signature", {
        "kind": "event_add", "owner_type": token(0x02000004), "name": "BadEvent",
        "event_type": "System.Action", "add_method": token(0x0600000B),
        "remove_method": token(0x06000015),
    })
    results["accessor-owner"] = rejected("accessor-owner", {
        "kind": "property_update", "target": token(0x17000001),
        "getter": token(0x06000016),
    })
    results["nested-owner-kind"] = rejected("nested-owner-kind", {
        "kind": "type_add", "owner_type": token(0x0600000B), "name": "BadNested",
    })
    results["generic-non-tail-add"] = rejected("generic-non-tail-add", {
        "kind": "generic_parameter_add", "owner": token(0x02000004),
        "generic_index": 0, "name": "BadPosition",
    })

    # A newly added, unused type generic can be removed; a non-tail or used one cannot.
    print(json.dumps({"phase": "structural-guards", "case_id": "generic-scope-and-tail"}), flush=True)
    begin = client.edit_begin(rid("begin-generic-guards"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        first = client.edit_apply(rid("generic-first"), tx, 0, {
            "kind": "generic_parameter_add", "owner": token(0x02000004),
            "generic_index": 1, "name": "TFirst",
        })
        second = client.edit_apply(rid("generic-second"), tx, 1, {
            "kind": "generic_parameter_add", "owner": token(0x02000004),
            "generic_index": 2, "name": "TSecond",
        })
        non_tail = apply_expect_domain(client, tx, 2, {
            "kind": "generic_parameter_remove", "target": {"object_id": "obj-000-00"},
            "remove_mode": "reject_if_referenced",
        })
        used = client.edit_apply(rid("generic-use"), tx, 2, {
            "kind": "field_add", "owner_type": token(0x02000004), "name": "UsesTSecond",
            "field_type": "!2",
        })
        used_reject = apply_expect_domain(client, tx, 3, {
            "kind": "generic_parameter_remove", "target": {"object_id": "obj-001-00"},
            "remove_mode": "reject_if_referenced",
        })
        results["generic-scope-and-tail"] = {
            "first": first, "second": second, "non_tail": non_tail,
            "used": used, "used_reject": used_reject,
        }
    finally:
        client.edit_rollback(rid("rollback-generic-guards"), tx)

    assert client.edit_status()["state"] == "idle"
    return results


def run_attachment_guards(client: DnSpyClient, fixture: Path) -> dict[str, Any]:
    """Reject deletion of metadata rows still owned by attachment tables."""
    print(json.dumps({"phase": "structural-guards", "case_id": "open-attachment-fixture"}), flush=True)
    opened = client.call_tool_json("open_files", {"paths": [str(fixture.resolve())]})
    assert opened["failed_count"] == 0, opened
    assembly = fixture.stem

    def rejected(case_id: str, operation: dict[str, Any]) -> dict[str, Any]:
        print(json.dumps({"phase": "structural-guards", "case_id": case_id}), flush=True)
        begin = client.edit_begin(rid("begin-attachment"), assembly)
        tx = begin["result"]["transaction"]["transaction_id"]
        try:
            return {"begin": begin, **apply_expect_domain(client, tx, 0, operation)}
        finally:
            client.edit_rollback(rid("rollback-attachment"), tx)

    # Tokens are discovered from this isolated fixture so the stable token
    # layout of P02DynamicFixture remains suitable for the fault matrix.
    attachment_type = "P02AttachmentFixture.AttachmentTargets"
    attribute_ctor = exact_member(
        client, assembly, "P02AttachmentFixture.AttachedAttribute", ".ctor", "method")
    interface_attachment = exact_type(
        client, assembly, "P02AttachmentFixture.InterfaceAttachment")
    impl_map = exact_member(client, assembly, attachment_type, "Beep", "method")
    marshal_field = exact_member(client, assembly, attachment_type, "MarshaledField", "field")
    secured_method = exact_member(client, assembly, attachment_type, "SecuredMethod", "method")
    semantic_getter = exact_member(client, assembly, attachment_type, "get_SemanticProperty", "method")
    method_rows = client.call_tool_json("list_methods", {
        "assembly_name": assembly, "type_full_name": attachment_type, "page_size": 500,
    })["items"]
    marshaled_method = next(row for row in method_rows if row["name"] == "MarshaledParameter")
    constrained_method = next(row for row in method_rows if row["name"] == "ConstrainedMethod")
    marshaled_param_token = marshaled_method["parameters"][0]["token"]
    constrained_gp_token = constrained_method["generic_parameters"][0]["token"]
    assert marshaled_param_token and constrained_gp_token

    all_fields = client.call_tool_json("search_members", {
        "query": "*", "assembly_name": assembly, "kinds": ["field"], "page_size": 500,
    })["items"]
    rva_fields = [row for row in all_fields if "<PrivateImplementationDetails>" in row["declaring_type"]]
    assert len(rva_fields) == 1, {"rva_fields": rva_fields}

    attachment_operations: dict[str, dict[str, Any]] = {
        "attachment-custom-attribute-constructor": {
            "kind": "method_remove", "target": discovered_token(attribute_ctor["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-interface-impl": {
            "kind": "type_remove", "target": discovered_token(interface_attachment["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-generic-constraint": {
            "kind": "generic_parameter_remove", "target": discovered_token(constrained_gp_token),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-impl-map": {
            "kind": "method_remove", "target": discovered_token(impl_map["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-field-marshal": {
            "kind": "field_remove", "target": discovered_token(marshal_field["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-parameter-marshal": {
            "kind": "parameter_remove", "parameter_target": discovered_token(marshaled_param_token),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-declarative-security": {
            "kind": "method_remove", "target": discovered_token(secured_method["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-field-rva": {
            "kind": "field_remove", "target": discovered_token(rva_fields[0]["token"]),
            "remove_mode": "reject_if_referenced",
        },
        "attachment-method-semantics": {
            "kind": "method_remove", "target": discovered_token(semantic_getter["token"]),
            "remove_mode": "reject_if_referenced",
        },
    }
    results: dict[str, Any] = {"open_files": opened}
    for case_id, operation in attachment_operations.items():
        results[case_id] = rejected(case_id, operation)

    assert client.edit_status()["state"] == "idle"
    return results


def run_duplicate_param_guard(client: DnSpyClient, fixture: Path) -> dict[str, Any]:
    print(json.dumps({"phase": "structural-guards", "case_id": "duplicate-paramdef-sequence"}), flush=True)
    opened = client.call_tool_json("open_files", {"paths": [str(fixture.resolve())]})
    assert opened["failed_count"] == 0, opened
    begin = client.edit_begin(rid("begin-duplicate-param"), fixture.stem)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        rejected = apply_expect_domain(client, tx, 0, {
            "kind": "type_add", "namespace": "P02Guard", "name": "DuplicateParamProbe",
        })
        return {"open_files": opened, "begin": begin, "rejected": rejected}
    finally:
        client.edit_rollback(rid("rollback-duplicate-param"), tx)


def run_semantic_vectors(
    client: DnSpyClient, assembly: str, facts_path: Path, attachment_fixture: Path,
) -> dict[str, Any]:
    return {
        "typesig": run_typesig_vectors(client, assembly),
        "attributes": run_attribute_vectors(client, assembly),
        "constants": run_constant_vectors(client, assembly),
        "accessor_nullability": run_accessor_nullability(client, assembly),
        "structural_guards": run_structural_guards(client, assembly),
        "attachment_guards": run_attachment_guards(client, attachment_fixture),
        "opcode_operand": run_opcode_operand_matrix(client, assembly, facts_path),
    }


def run_capacity_matrix(client: DnSpyClient, assembly: str) -> dict[str, Any]:
    results: dict[str, Any] = {}

    # Begin tombstones are bounded per live transport session and are released on DELETE.
    begin_client = DnSpyClient.connect(client.base_url, timeout=180)
    begin_rows: list[dict[str, Any]] = []
    try:
        for ordinal in range(64):
            started = begin_client.edit_begin(rid("begin-capacity"), assembly)
            rolled = begin_client.edit_rollback(rid("rollback-capacity"), started["result"]["transaction"]["transaction_id"])
            begin_rows.append({"ordinal": ordinal + 1, "begin": started, "rollback": rolled})
        over = failing_call(lambda: begin_client.edit_begin(rid("begin-capacity-over"), assembly))
        assert error_code(over) == "EDIT_CAPACITY_EXCEEDED", over
        assert begin_client.edit_status()["state"] == "idle"
        results["begin"] = {"boundary": 64, "rows": begin_rows, "over_one": over}
    finally:
        begin_client.close()
    released_client = DnSpyClient.connect(client.base_url, timeout=180)
    try:
        released = released_client.edit_begin(rid("begin-after-release"), assembly)
        released_client.edit_rollback(rid("rollback-after-release"), released["result"]["transaction"]["transaction_id"])
        results["begin"]["after_session_release"] = released
    finally:
        released_client.close()

    # A current full review plus 64 compact tombstones is the exact accepted boundary.
    begin = client.edit_begin(rid("begin-review-capacity"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    review_rows: list[dict[str, Any]] = []
    try:
        for ordinal in range(65):
            reviewed = client.edit_review(rid("review-capacity"), tx, 0)
            status = client.edit_status()
            expected_tombstones = max(0, ordinal)
            assert status["result"]["capacity"]["review_tombstone_entries"]["current"] == expected_tombstones
            review_rows.append({"ordinal": ordinal + 1, "review": reviewed,
                                "capacity": status["result"]["capacity"]})
        before = client.edit_status()
        over = failing_call(lambda: client.edit_review(rid("review-capacity-over"), tx, 0))
        assert error_code(over) == "EDIT_CAPACITY_EXCEEDED", over
        after = client.edit_status()
        assert before["result"]["fingerprints"] == after["result"]["fingerprints"]
        assert after["result"]["capacity"]["review_tombstone_entries"]["current"] == 64
        cleared = client.edit_apply(rid("apply-clears-reviews"), tx, 0, {
            "kind": "type_add", "namespace": "P02Capacity", "name": "ClearsReviews",
        })
        cleared_status = client.edit_status()
        assert cleared_status["result"]["capacity"]["review_tombstone_entries"]["current"] == 0
        results["review"] = {"boundary": 65, "rows": review_rows, "before_over": before,
                             "over_one": over, "after_over": after, "cleared": cleared,
                             "cleared_status": cleared_status}
    finally:
        client.edit_rollback(rid("rollback-review-capacity"), tx)

    # edit_apply is additionally bounded by 256 normalized operations.  The 257th
    # request must not mutate revision, fingerprints, or cache accounting.
    begin = client.edit_begin(rid("begin-apply-capacity"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    apply_rows: list[dict[str, Any]] = []
    try:
        for ordinal in range(256):
            applied = client.edit_apply(rid("apply-capacity"), tx, ordinal, {
                "kind": "type_update", "target": token(0x02000004), "name": f"EditTargetsCapacity{ordinal}",
            })
            if ordinal in {0, 254, 255}:
                apply_rows.append({"ordinal": ordinal + 1, "response": applied})
        before = client.edit_status()
        over = failing_call(lambda: client.edit_apply(rid("apply-capacity-over"), tx, 256, {
            "kind": "type_update", "target": token(0x02000004), "name": "OverLimit",
        }))
        assert error_code(over) == "EDIT_CAPACITY_EXCEEDED", over
        after = client.edit_status()
        assert before["result"]["transaction"]["work_revision"] == 256
        assert after["result"]["transaction"]["work_revision"] == 256
        assert before["result"]["fingerprints"] == after["result"]["fingerprints"]
        results["apply"] = {"boundary": 256, "sample_rows": apply_rows,
                            "before_over": before, "over_one": over, "after_over": after}
    finally:
        rollback_id = rid("rollback-apply-capacity")
        rolled = client.edit_rollback(rollback_id, tx)
        replay = client.edit_rollback(rollback_id, tx)
        assert rolled == replay
        results["rollback"] = {"response": rolled, "byte_identical_replay": replay,
                               "final_status": client.edit_status()}
    return results


def run_review_guards(client: DnSpyClient, assembly: str, dynamic_path: Path,
                      architecture: str) -> dict[str, Any]:
    results: dict[str, Any] = {}
    print(json.dumps({"phase": "review-guards", "case": "review-stale"}), flush=True)
    results["review_stale"] = run_review_lifecycle(client, assembly)

    print(json.dumps({"phase": "review-guards", "case": "live-conflict"}), flush=True)
    begin = client.edit_begin(rid("begin-live-conflict"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    external_active = False
    try:
        applied = client.edit_apply(rid("apply-live-conflict"), tx, 0, {
            "kind": "type_add", "namespace": "P02Guard", "name": "ExternalConflict",
        })
        review = client.edit_review(rid("review-live-conflict"), tx, 1)
        mutated = client.call_tool_json("edit_test_live_mutation", {
            "transaction_id": tx, "action": "mutate",
        })
        external_active = True
        assert mutated["result"]["changed"] is True and mutated["result"]["restored"] is False
        conflict = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-conflict"), "transaction_id": tx,
            "review_id": review["result"]["review"]["review_id"], "expected_revision": 1,
            "confirmed_risk_ids": review["result"]["review"]["required_confirmation_ids"],
        }))
        assert error_code(conflict) == "EDIT_LIVE_MODULE_CONFLICT", conflict
        during = client.edit_status()
        # Status exposes the review-bound fingerprint, not an unsynchronised
        # live rescan.  The mutation seam supplies the independent live readback
        # proving that the external edit remains present after conflict rejection.
        assert during["state"] == "reviewed", during
        assert mutated["result"]["after_fingerprint"] != mutated["result"]["before_fingerprint"]
        assert during["result"]["fingerprints"]["current_live"] == mutated["result"]["before_fingerprint"]
        restored = client.call_tool_json("edit_test_live_mutation", {
            "transaction_id": tx, "action": "restore",
        })
        external_active = False
        assert restored["result"]["restored"] is True
        results["live_conflict"] = {"apply": applied, "review": review, "mutated": mutated,
                                    "failure": conflict, "during": during, "restored": restored}
    finally:
        if external_active:
            client.call_tool_json("edit_test_live_mutation", {
                "transaction_id": tx, "action": "restore",
            })
        client.edit_rollback(rid("rollback-live-conflict"), tx)

    print(json.dumps({"phase": "review-guards", "case": "risk-confirmation"}), flush=True)
    begin = client.edit_begin(rid("begin-risk-guard"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    try:
        applied = client.edit_apply(rid("apply-risk-guard"), tx, 0, {
            "kind": "field_update", "target": token(0x04000002), "field_type": "System.Int64",
        })
        review = client.edit_review(rid("review-risk-guard"), tx, 1)
        required = review["result"]["review"]["required_confirmation_ids"]
        assert required
        missing = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-risk-guard"), "transaction_id": tx,
            "review_id": review["result"]["review"]["review_id"], "expected_revision": 1,
            "confirmed_risk_ids": required[1:] if len(required) > 1 else [],
        }))
        assert error_code(missing) == "EDIT_RISK_CONFIRMATION_REQUIRED", missing
        results["risk_confirmation"] = {"apply": applied, "review": review, "failure": missing}
    finally:
        client.edit_rollback(rid("rollback-risk-guard"), tx)

    print(json.dumps({"phase": "review-guards", "case": "debug-not-idle"}), flush=True)
    begin = client.edit_begin(rid("begin-debug-guard"), assembly)
    tx = begin["result"]["transaction"]["transaction_id"]
    debug_context: dict[str, Any] | None = None
    try:
        applied = client.edit_apply(rid("apply-debug-guard"), tx, 0, {
            "kind": "type_add", "namespace": "P02Guard", "name": "DebugNotIdle",
        })
        review = client.edit_review(rid("review-debug-guard"), tx, 1)
        launch = client.call_tool_json("debug_launch", {
            "request_id": str(uuid.uuid4()), "target_path": str(dynamic_path.resolve()),
            "expected_sha256": hashlib.sha256(dynamic_path.read_bytes()).hexdigest(),
            "launch_mode": "net48-exe", "architecture": architecture, "break_kind": "entry",
        })
        assert launch["ok"] is True, launch
        debug_context = launch["debug_context"]
        blocked = failing_call(lambda: client.call_tool_json("edit_test_apply_and_restore", {
            "request_id": rid("live-debug-guard"), "transaction_id": tx,
            "review_id": review["result"]["review"]["review_id"], "expected_revision": 1,
            "confirmed_risk_ids": review["result"]["review"]["required_confirmation_ids"],
        }))
        assert error_code(blocked) == "EDIT_DEBUG_NOT_IDLE", blocked
        results["debug_not_idle"] = {"apply": applied, "review": review,
                                     "launch": launch, "failure": blocked}
    finally:
        if debug_context is not None:
            terminate = client.call_tool_json("debug_terminate", {
                "session_id": debug_context["session_id"], "generation": debug_context["generation"],
                "request_id": str(uuid.uuid4()),
            })
            results.setdefault("debug_not_idle", {})["terminate"] = terminate
        client.edit_rollback(rid("rollback-debug-guard"), tx)
    debug_status = client.call_tool_json("debug_status", {})
    assert debug_status["result"]["state"] == "idle", debug_status
    results["final_debug_status"] = debug_status
    return results


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:15378/")
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--dynamic-fixture")
    parser.add_argument("--assembly", default="TestIL")
    parser.add_argument("--architecture", required=True, choices=["x64", "x86"])
    parser.add_argument("--output", required=True)
    parser.add_argument("--only-expanded", action="store_true")
    parser.add_argument("--only-lifecycle", action="store_true")
    parser.add_argument("--only-operation-matrix", action="store_true")
    parser.add_argument("--only-semantic-matrix", action="store_true")
    parser.add_argument("--only-opcode-matrix", action="store_true")
    parser.add_argument("--only-structural-guards", action="store_true")
    parser.add_argument("--only-capacity-matrix", action="store_true")
    parser.add_argument("--only-review-guards", action="store_true")
    parser.add_argument("--fault-suite", action="store_true")
    parser.add_argument("--only-fault-suite", action="store_true")
    parser.add_argument("--fault-suite-id", default="p02-fault-suite")
    parser.add_argument("--artifact-root", default=os.environ.get("DNSPY_MCP_ARTIFACT_ROOT", r"C:\Users\xxx\Desktop\dnspy-mcp-artifacts\edit-tests"))
    parser.add_argument("--fault-limit", type=int)
    parser.add_argument("--lifecycle", action="store_true")
    parser.add_argument("--operation-matrix", action="store_true")
    parser.add_argument("--semantic-matrix", action="store_true")
    parser.add_argument("--capacity-matrix", action="store_true")
    parser.add_argument("--dnlib-facts")
    parser.add_argument("--unsupported", action="append", default=[])
    parser.add_argument("--only-unsupported", action="store_true")
    args = parser.parse_args()

    started = time.time()
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    report: dict[str, Any] = {
        "schema_version": "dnspy.p02.vm-driver.v1",
        "architecture": args.architecture,
        "fixture": str(Path(args.fixture).resolve()),
        "fixture_sha256": hashlib.sha256(Path(args.fixture).read_bytes()).hexdigest(),
        "started_unix": started,
        "cases": {},
    }
    exit_code = 1
    client = DnSpyClient(args.url, timeout=180)
    try:
        client.initialize()
        opened = client.call_tool_json("open_files", {"paths": [str(Path(args.fixture).resolve())]})
        report["open_files"] = opened
        only_mode = any((args.only_expanded, args.only_lifecycle, args.only_operation_matrix,
                         args.only_semantic_matrix, args.only_opcode_matrix, args.only_structural_guards,
                         args.only_capacity_matrix,
                         args.only_review_guards,
                         args.only_fault_suite, args.only_unsupported))
        if not only_mode:
            report["cases"]["positive_22_kind"] = run_positive(client, args.assembly)
            report["cases"]["idempotency_raw"] = run_idempotency_and_raw_rejection(client, args.assembly)
            report["cases"]["review_lifecycle"] = run_review_lifecycle(client, args.assembly)
            report["cases"]["fault_smoke"] = run_fault_smoke(client, args.assembly)
        if args.dynamic_fixture and not args.only_unsupported:
            dynamic_path = str(Path(args.dynamic_fixture).resolve())
            dynamic_open = client.call_tool_json("open_files", {"paths": [dynamic_path]})
            report["dynamic_open_files"] = dynamic_open
            dynamic_assembly = Path(dynamic_path).stem
            if args.only_expanded or not only_mode:
                report["cases"]["dynamic_validation"] = run_dynamic_validation(
                    client, args.assembly, dynamic_assembly)
                report["cases"]["fingerprint_components"] = run_fingerprint_components(
                    client, dynamic_assembly)
        if args.fault_suite or args.only_fault_suite:
            fault_assembly = Path(args.dynamic_fixture).stem if args.dynamic_fixture else args.assembly
            report["cases"]["fault_suite"] = run_fault_suite(
                client, fault_assembly, Path(args.artifact_root), args.fault_suite_id, args.fault_limit)
        if (args.lifecycle or args.only_lifecycle) and not args.only_fault_suite:
            report["cases"]["transaction_lifecycle"] = run_transaction_lifecycle(client, args.assembly)
            report["cases"]["concurrency"] = run_concurrency(client, args.assembly)
            report["cases"]["barrier_concurrency"] = run_barrier_concurrency(client, args.assembly)
        if (args.operation_matrix or args.only_operation_matrix) and args.dynamic_fixture and not args.only_fault_suite:
            report["cases"]["operation_matrix"] = run_operation_matrix(
                client, Path(args.dynamic_fixture).stem)
        if (args.semantic_matrix or args.only_semantic_matrix) and args.dynamic_fixture and not args.only_fault_suite:
            if not args.dnlib_facts:
                raise AssertionError("--semantic-matrix requires --dnlib-facts")
            report["cases"]["semantic_vectors"] = run_semantic_vectors(
                client, Path(args.dynamic_fixture).stem, Path(args.dnlib_facts),
                Path(args.dynamic_fixture).with_name("P02AttachmentFixture.dll"))
            report["cases"]["duplicate_param_guard"] = run_duplicate_param_guard(
                client, Path(args.dynamic_fixture).with_name("P02DuplicateParamFixture.exe"))
        if args.only_opcode_matrix and args.dynamic_fixture:
            if not args.dnlib_facts:
                raise AssertionError("--only-opcode-matrix requires --dnlib-facts")
            report["cases"]["opcode_operand"] = run_opcode_operand_matrix(
                client, Path(args.dynamic_fixture).stem, Path(args.dnlib_facts))
        if args.only_structural_guards and args.dynamic_fixture:
            report["cases"]["structural_guards"] = run_structural_guards(
                client, Path(args.dynamic_fixture).stem)
            report["cases"]["attachment_guards"] = run_attachment_guards(
                client, Path(args.dynamic_fixture).with_name("P02AttachmentFixture.dll"))
            report["cases"]["duplicate_param_guard"] = run_duplicate_param_guard(
                client, Path(args.dynamic_fixture).with_name("P02DuplicateParamFixture.exe"))
        if (args.capacity_matrix or args.only_capacity_matrix) and args.dynamic_fixture and not args.only_fault_suite:
            report["cases"]["capacity_matrix"] = run_capacity_matrix(
                client, Path(args.dynamic_fixture).stem)
        if args.only_review_guards and args.dynamic_fixture:
            report["cases"]["review_guards"] = run_review_guards(
                client, Path(args.dynamic_fixture).stem, Path(args.dynamic_fixture), args.architecture)
        if args.unsupported:
            report["cases"]["unsupported_targets"] = run_unsupported_targets(client, args.unsupported)
        # A full fault run can leave the caller's persistent TCP connection idle
        # for tens of minutes while disposable batch sessions do the work.  Do
        # not replay a POST on a stale connection: verify the terminal invariant
        # through a newly initialized read-only session instead.
        status_client: DnSpyClient | None = None
        if args.only_fault_suite:
            status_client = DnSpyClient.connect(
                client.base_url, timeout=client.timeout,
                client_name="dnspy-p02-fault-final-status")
        try:
            status = (status_client or client).edit_status()
        finally:
            if status_client is not None:
                status_client.close()
        assert status["ok"] is True and status["state"] == "idle", status
        report["final_status"] = status
        report["result"] = "PASS"
        exit_code = 0
    except Exception as exc:  # evidence must survive any assertion/protocol failure
        report["result"] = "FAIL"
        report["error"] = {"type": type(exc).__name__, "message": str(exc),
                           "traceback": traceback.format_exc()}
        try:
            report["final_status"] = client.edit_status()
        except Exception as status_exc:
            report["status_error"] = repr(status_exc)
    finally:
        try:
            client.close()
        except Exception as close_exc:
            # Closing the outer session is best-effort here.  In particular, a
            # full fault suite intentionally uses disposable sessions for a
            # long time, so the original persistent TCP connection may have
            # been closed by the listener already.  The terminal idle state is
            # independently verified above through a fresh session; do not
            # discard that evidence just because DELETE cannot reuse the stale
            # transport.
            report["close_warning"] = {
                "type": type(close_exc).__name__,
                "message": str(close_exc),
            }
        report["elapsed_seconds"] = round(time.time() - started, 3)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"result": report["result"], "output": str(output), "elapsed_seconds": report["elapsed_seconds"]}))
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
