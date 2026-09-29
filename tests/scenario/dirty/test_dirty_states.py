"""ACC-043 dirty-state construction tests (P02 evidence; NOT corpus scenarios).

Each test deliberately leaves one check's state dirty and returns; the
conftest teardown standard reset routine must detect it and BLOCK the run.
Expected pytest outcome for each: FAILED with "BLOCKED: ..." — that failure
signal IS the pass condition of ACC-043's dirty arm.

Cleanup note: dirty-A leaves a server-side edit transaction; the response is
dumped to .tmp/scenario-ledgers/dirty-a-state.json for manual rollback after
the evidence run (the reset routine itself must NOT auto-heal — fail-safe).
"""

import json
from pathlib import Path

SCENARIO_ID = "S-DIRTY-A"
DECLARED_TOOLS = ["edit_begin", "edit_status"]

DIRTY_STATE_DUMP = Path(".tmp/scenario-ledgers/dirty-a-state.json")


def test_dirty_a_edit_transaction_left_open(scenario_env):
    """Dirty A: start an edit transaction and return without rollback (check 1 must BLOCK)."""
    response = scenario_env.client.edit_begin("dirty-a-req-001", "TestIL")
    DIRTY_STATE_DUMP.parent.mkdir(parents=True, exist_ok=True)
    DIRTY_STATE_DUMP.write_text(json.dumps(
        {"request_id": "dirty-a-req-001",
         "transaction_id": (response.get("result") or response).get("transaction_id")
         if isinstance(response, dict) else None,
         "raw": str(response)[:1000]}, ensure_ascii=False, indent=1), encoding="utf-8")
