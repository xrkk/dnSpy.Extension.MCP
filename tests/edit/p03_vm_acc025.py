#!/usr/bin/env python3
"""ACC-025 end-to-end evidence on the real dnSpy MCP listener (round 52):
external live divergence via the registered UI seam, diverged begin with zero
side effects, wrong-fingerprint accept rejection, explicit accept_live linking
the superseded lineage, old-package readability, and a forged-operation-free
new root checkpoint."""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

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


def err_code(envelope: dict) -> str:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def err_details(envelope: dict) -> dict:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    details = error.get("details") if isinstance(error, dict) else None
    return details if isinstance(details, dict) else {}


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc025")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # Precondition: an active lineage must exist; create one through a real
    # commit when the store is empty so the driver is self-contained.
    hist = payload(call(client, "edit_history", {}))
    lineages = hist.get("lineages", []) if isinstance(hist.get("lineages"), list) else []
    active = [row for row in lineages if isinstance(row, dict) and not row.get("superseded_lineage_id")]
    if not active:
        begin0 = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
        tx0 = payload(begin0).get("transaction", {})
        if not tx0.get("transaction_id"):
            print(f"seed begin failed: {json.dumps(begin0)[:300]}", flush=True)
            return 1
        applied0 = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0),
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": "Acc025Seed"},
        })
        review0 = call(client, "edit_review", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0) + 1,
        })
        review0_core = payload(review0).get("review", {})
        commit0 = call(client, "edit_commit", {
            "request_id": rid(), "transaction_id": tx0["transaction_id"],
            "expected_revision": tx0.get("work_revision", 0) + 1,
            "review_id": review0_core.get("review_id", ""),
            "review_revision": review0_core.get("review_revision", 0),
            "confirmed_risk_ids": [],
        })
        check("A0 seed lineage committed", bool(commit0.get("ok")), json.dumps(commit0)[:240])
        hist = payload(call(client, "edit_history", {}))
        lineages = hist.get("lineages", []) if isinstance(hist.get("lineages"), list) else []
        active = [row for row in lineages if isinstance(row, dict) and not row.get("superseded_lineage_id")]
    check("A1 active lineage exists", len(active) == 1, f"active={len(active)} total={len(lineages)}")
    if not active:
        print(f"lineages: {json.dumps(lineages)[:400]}", flush=True)
        return 1
    old_lineage_id = str(active[0].get("lineage_id", ""))
    old_family_id = str(active[0].get("family_id", ""))
    old_head = str(active[0].get("head_checkpoint_id", ""))
    old_count = int(active[0].get("checkpoint_count", 0))
    print(f"INFO old lineage={old_lineage_id} family={old_family_id} head={old_head} checkpoints={old_count}", flush=True)

    # External (no-transaction) live mutation through the registered seam.
    mutation = payload(call(client, "edit_test_lineage_mutation", {"action": "mutate", "assembly_name": "TestIL"}))
    diverged_fp = str(mutation.get("after_fingerprint", ""))
    check("A2 seam mutate returned diverged fingerprint", len(diverged_fp) == 64, json.dumps(mutation)[:200])

    # begin must reject with EDIT_LINEAGE_DIVERGED and zero side effects.
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    details = err_details(begin)
    check("A3 begin rejects diverged", err_code(begin) == "EDIT_LINEAGE_DIVERGED", err_code(begin))
    # The process-bound path omits match_basis (it is only meaningful in the
    # cross-process candidate-resolution path); family/lineage identify the
    # diverged lineage in both shapes.
    check("A3 begin error carries family/lineage",
          details.get("family_id") == old_family_id and details.get("lineage_id") == old_lineage_id,
          json.dumps(details)[:240])
    status = payload(call(client, "edit_status", {}))
    check("A3 begin zero side effects", status.get("state") == "idle" and not status.get("busy"),
          json.dumps(status)[:200])

    # accept_live with the WRONG fingerprint must be refused without effects.
    wrong = call(client, "edit_accept_live", {
        "request_id": rid(), "assembly_name": "TestIL", "source_family_id": old_family_id,
        "superseded_lineage_id": old_lineage_id, "expected_live_fingerprint": "0" * 64,
        "acknowledge_new_baseline": True,
    })
    check("A4 wrong fingerprint rejected", err_code(wrong) == "EDIT_HISTORY_CONFLICT", err_code(wrong))
    hist_after_wrong = payload(call(client, "edit_history", {}))
    rows_after_wrong = hist_after_wrong.get("lineages", []) if isinstance(hist_after_wrong.get("lineages"), list) else []
    check("A4 wrong accept no new lineage", len(rows_after_wrong) == len(lineages),
          f"before={len(lineages)} after={len(rows_after_wrong)}")

    # Explicit accept of the externally mutated live state.
    accept = call(client, "edit_accept_live", {
        "request_id": rid(), "assembly_name": "TestIL", "source_family_id": old_family_id,
        "superseded_lineage_id": old_lineage_id, "expected_live_fingerprint": diverged_fp,
        "acknowledge_new_baseline": True,
    })
    check("A5 accept_live ok", bool(accept.get("ok")), json.dumps(accept)[:300])
    result = payload(accept)
    new_lineage_id = str(result.get("lineage", {}).get("lineage_id", "")) if isinstance(result.get("lineage"), dict) else ""
    root = result.get("root_checkpoint", {}) if isinstance(result.get("root_checkpoint"), dict) else {}
    check("A5 accept returns new lineage and root",
          new_lineage_id.startswith("lineage-") and new_lineage_id != old_lineage_id and bool(root.get("checkpoint_id")),
          json.dumps(result)[:300])
    check("A5 accept reports superseded lineage", result.get("superseded_lineage_id") == old_lineage_id,
          str(result.get("superseded_lineage_id")))

    # Old package must stay readable.
    old_view = payload(call(client, "edit_history", {"lineage_id": old_lineage_id, "page_size": 100}))
    old_rows = old_view.get("checkpoints", []) if isinstance(old_view.get("checkpoints"), list) else []
    check("A6 old lineage readable", len(old_rows) == old_count and any(c.get("checkpoint_id") == old_head for c in old_rows if isinstance(c, dict)),
          f"rows={len(old_rows)} expected={old_count}")

    # New lineage: single root, no parent, no forged operations.
    new_view = payload(call(client, "edit_history", {"lineage_id": new_lineage_id, "page_size": 100}))
    new_rows = new_view.get("checkpoints", []) if isinstance(new_view.get("checkpoints"), list) else []
    single_root = len(new_rows) == 1 and isinstance(new_rows[0], dict) and new_rows[0].get("parent_checkpoint_id") is None
    check("A7 new lineage single root without parent", single_root, json.dumps(new_rows)[:300])

    # The superseded link is visible from the new lineage summary.
    hist_final = payload(call(client, "edit_history", {}))
    rows_final = hist_final.get("lineages", []) if isinstance(hist_final.get("lineages"), list) else []
    new_row = next((r for r in rows_final if isinstance(r, dict) and r.get("lineage_id") == new_lineage_id), {})
    check("A8 new lineage links superseded", new_row.get("superseded_lineage_id") == old_lineage_id,
          json.dumps(new_row)[:240])

    # The coordinator is usable again: begin binds the new baseline, rollback cleanly.
    begin2 = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    begin_ok = bool(begin2.get("ok"))
    check("A9 begin usable after accept", begin_ok, json.dumps(begin2)[:240])
    if begin_ok:
        tx2 = payload(begin2).get("transaction", {}).get("transaction_id", "")
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})
        check("A9 rollback after accept", bool(rolled.get("ok")), json.dumps(rolled)[:200])

    print(f"ACC025 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
