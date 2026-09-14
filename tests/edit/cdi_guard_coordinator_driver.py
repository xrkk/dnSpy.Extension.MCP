#!/usr/bin/env python3
"""T002-R03 CHK-021 coordinator-path driver (VM side, real loopback).

Covers the real coordinator consumption of the complete CDI guard with an
EXISTING same-kind/same-row CDI row (adding a row is not the counterexample):

  A) edit_review rejects a 65th flag / hoisted-scope End content drift with
     EDIT_LIVE_MODULE_CONFLICT + external_drift_conflict, and the live/store
     fingerprints do not move.
  B) a review taken before the drift cannot be committed afterwards; commit
     entry rejects with the same conflict family (no checkpoint/head advance).
  C) after a committed checkpoint, edit_undo is rejected while the live module
     carries the CDI drift; the recorded head does not move.
  D) the seam restore returns the exact original CDI content and a fresh
     review succeeds again (guard restored).

Fixture precondition (checked through the seam artifact's cdi_shape): the
opened assembly's live module must already carry either
  - PdbDynamicLocalVariablesCustomDebugInfo with >=65 flags, or
  - PdbStateMachineHoistedLocalScopesCustomDebugInfo with a bound End,
e.g. after materializing an iterator/async import with
tests/edit/p03_vm_acc005full.py in the same dnSpy instance, or a prebuilt
CDI-bearing fixture shipped with its portable PDB.  Otherwise the driver fails
with FIXTURE_REQUIRED instead of pretending the counterexample ran.

Limitation (recorded, not faked): the Dispatcher second gate cannot be injected
through this edit-family seam because the paused commit holds the operation
gate; that path is covered by chk_targeted_driver.case_chk013_gate with its
debug-tool drift against the same guard.

Run on the VM:
  python cdi_guard_coordinator_driver.py --assembly <name> [--fixture <dll>]
      [--url http://127.0.0.1:15378/mcp] [--run-id <id>]
"""

from __future__ import annotations

import argparse
import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

FAILURES: list[str] = []
PASSES: list[str] = []
ELIGIBLE_SHAPES = {"dynamic-flags-65", "hoisted-end"}


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:400]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def error_code(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def error_details(envelope: dict) -> dict:
    error = envelope.get("error")
    if isinstance(error, dict):
        for key in ("details", "data"):
            value = error.get(key)
            if isinstance(value, dict):
                return value
    return {}


def begin_tx(client: DnSpyClient, assembly: str) -> tuple[str, int]:
    envelope = call(client, "edit_begin", {"assembly_name": assembly, "request_id": rid()})
    row = payload(envelope).get("transaction", {})
    return str(row.get("transaction_id", "")), int(row.get("work_revision", 0))


def apply_op(client: DnSpyClient, tx: str, revision: int, operation: dict) -> tuple[dict, int]:
    envelope = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": operation})
    row = payload(envelope).get("transaction", {})
    if row:
        revision = int(row.get("work_revision", revision))
    return envelope, revision


def review_tx(client: DnSpyClient, tx: str, revision: int) -> tuple[dict, str, list[str]]:
    envelope = call(client, "edit_review", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    row = payload(envelope).get("review", {})
    required = [str(r) for r in (row.get("required_confirmation_ids") or [])]
    return envelope, str(row.get("review_id", "")), required


def commit_tx(client: DnSpyClient, tx: str, revision: int, review_id: str, required: list[str]) -> dict:
    return call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})


def live_fingerprint(client: DnSpyClient) -> str:
    return str(payload(call(client, "edit_status", {})).get("fingerprints", {}).get("live", "?"))


def head_checkpoint(client: DnSpyClient) -> str:
    rows = payload(call(client, "edit_history", {})).get("lineages", [])
    return str(rows[0].get("head_checkpoint_id", "")) if rows else ""


def mutate(client: DnSpyClient, tx: str) -> dict:
    return call(client, "edit_test_external_mutation", {"transaction_id": tx, "case_id": "live-conflict:mutate-cdi"})


def restore(client: DnSpyClient, tx: str) -> dict:
    return call(client, "edit_test_external_mutation", {"transaction_id": tx, "case_id": "live-conflict:restore"})


def artifact_shape(envelope: dict) -> str:
    artifact = payload(envelope).get("evidence_artifact", {})
    path = artifact.get("path") if isinstance(artifact, dict) else None
    if not path:
        return ""
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return str(json.load(handle).get("cdi_shape", ""))
    except OSError:
        return ""


def rollback(client: DnSpyClient, tx: str) -> dict:
    return call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})


def require_eligible(client: DnSpyClient, tx: str, mutation: dict, label: str) -> bool:
    shape = artifact_shape(mutation)
    if shape in ELIGIBLE_SHAPES:
        check(f"{label} existing CDI shape used ({shape})", True)
        return True
    check(f"{label} existing CDI shape used", False,
          "FIXTURE_REQUIRED: seam reported cdi_shape=" + (shape or "unknown")
          + "; provide a live module that already carries a >=65-flag dynamic-local CDI or a bound hoisted-scope CDI"
          + " (e.g. materialize an iterator/async import with p03_vm_acc005full.py in the same instance, or a CDI-bearing fixture with its PDB)")
    restore(client, tx)
    return False


def case_review(client: DnSpyClient, assembly: str) -> None:
    tx, revision = begin_tx(client, assembly)
    check("R1 transaction began", bool(tx))
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": assembly + "CdiReview"})
    live_before = live_fingerprint(client)
    head_before = head_checkpoint(client)
    mutation = mutate(client, tx)
    if not require_eligible(client, tx, mutation, "R2"):
        rollback(client, tx)
        return
    check("R2 semantic fingerprint unchanged by CDI-only drift",
          bool(payload(mutation).get("restored") is False and payload(mutation).get("changed") is True),
          json.dumps(payload(mutation))[:240])
    check("R3 guard changed while semantic unchanged",
          bool(payload(mutation).get("changed")) and not bool(payload(mutation).get("semantic_change")),
          json.dumps(payload(mutation))[:240])
    reviewed, _review_id, _required = review_tx(client, tx, revision)
    check("R4 review rejects CDI drift", error_code(reviewed) == "EDIT_LIVE_MODULE_CONFLICT", json.dumps(reviewed)[:240])
    check("R4 conflict family is external_drift_conflict", error_details(reviewed).get("kind") == "external_drift_conflict",
          json.dumps(error_details(reviewed))[:200])
    check("R5 no live write from the rejected review", live_fingerprint(client) == live_before)
    check("R5 no head/store advance", head_checkpoint(client) == head_before)
    restored = restore(client, tx)
    check("R6 seam restore succeeds", bool(payload(restored).get("restored")), json.dumps(restored)[:200])
    reviewed_after, _id, _req = review_tx(client, tx, revision)
    check("R7 review succeeds after exact CDI restore", bool(reviewed_after.get("ok")), json.dumps(reviewed_after)[:240])
    rollback(client, tx)


def case_commit_entry(client: DnSpyClient, assembly: str) -> None:
    tx, revision = begin_tx(client, assembly)
    check("C1 transaction began", bool(tx))
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": assembly + "CdiCommit"})
    reviewed, review_id, required = review_tx(client, tx, revision)
    check("C2 clean review taken", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:200])
    live_before = live_fingerprint(client)
    head_before = head_checkpoint(client)
    mutation = mutate(client, tx)
    if not require_eligible(client, tx, mutation, "C3"):
        rollback(client, tx)
        return
    committed = commit_tx(client, tx, revision, review_id, required)
    check("C4 commit entry rejects CDI drift", error_code(committed) == "EDIT_LIVE_MODULE_CONFLICT",
          json.dumps(committed)[:240])
    check("C5 no live write and no head/store advance",
          live_fingerprint(client) == live_before and head_checkpoint(client) == head_before)
    restore(client, tx)
    rollback(client, tx)


def case_undo_protection(client: DnSpyClient, assembly: str) -> None:
    tx, revision = begin_tx(client, assembly)
    _e, revision = apply_op(client, tx, revision, {"kind": "module_update", "name": assembly + "CdiBase"})
    reviewed, review_id, required = review_tx(client, tx, revision)
    committed = commit_tx(client, tx, revision, review_id, required)
    check("U1 baseline checkpoint committed", bool(committed.get("ok")), json.dumps(committed)[:240])
    lineages = payload(call(client, "edit_history", {})).get("lineages", [])
    lineage_id = str(lineages[0].get("lineage_id", "")) if lineages else ""
    head = str(lineages[0].get("head_checkpoint_id", "")) if lineages else ""
    check("U2 lineage head recorded", bool(lineage_id) and bool(head))

    tx2, _rev2 = begin_tx(client, assembly)
    check("U3 second transaction began", bool(tx2))
    mutation = mutate(client, tx2)
    if not require_eligible(client, tx2, mutation, "U4"):
        rollback(client, tx2)
        return
    undo = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": head})
    check("U5 undo rejected while live carries CDI drift",
          error_code(undo) in ("EDIT_LINEAGE_DIVERGED", "EDIT_LIVE_MODULE_CONFLICT", "EDIT_HISTORY_CONFLICT"),
          json.dumps(undo)[:240])
    check("U6 recorded head unchanged", head_checkpoint(client) == head)
    restore(client, tx2)
    rollback(client, tx2)
    check("U7 rollback returns idle with exact restore",
          str(payload(call(client, "edit_status", {})).get("state", "")) == "idle")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:15378/mcp")
    parser.add_argument("--assembly", required=True)
    parser.add_argument("--fixture", default="")
    parser.add_argument("--run-id", default="cdi-guard-coordinator")
    parser.add_argument("--case", default="all", choices=["all", "review", "commit", "undo"])
    options = parser.parse_args()
    client = DnSpyClient(options.url)
    if options.fixture:
        call(client, "open_files", {"paths": [options.fixture]})
    print("RUN " + options.run_id + " assembly=" + options.assembly, flush=True)
    if options.case in ("all", "review"):
        case_review(client, options.assembly)
    if options.case in ("all", "commit"):
        case_commit_entry(client, options.assembly)
    if options.case in ("all", "undo"):
        case_undo_protection(client, options.assembly)
    print(f"SUMMARY passes={len(PASSES)} failures={len(FAILURES)}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
