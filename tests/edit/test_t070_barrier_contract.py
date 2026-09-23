#!/usr/bin/env python3
"""Keep the test-only barrier's declared responses aligned with its call sites."""
import json
import re
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator


ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / "Editing/Contracts/p03-tool-schemas.json"
SOURCE = ROOT / "Editing/EditTransactionCoordinator.cs"
MANUAL = ROOT / "docs/AI-TOOL-REFERENCE.zh-CN.md"


def schemas():
    return json.loads(SCHEMA.read_text(encoding="utf-8"))["edit_test_barrier"]


def names(block):
    return block["outputSchema"]["oneOf"][0]["properties"]["result"]["properties"]["name"]["oneOf"][0]["enum"]


class BarrierContractTests(unittest.TestCase):
    def test_supported_call_sites_and_output_names_match(self):
        block = schemas()
        accepted = block["inputSchema"]["oneOf"][0]["properties"]["name"]["enum"]
        call_sites = re.findall(r'BarrierPoint\("([a-z_]+)"', SOURCE.read_text(encoding="utf-8"))
        self.assertEqual(set(call_sites), set(accepted))
        self.assertEqual(names(block), accepted)
        validator = Draft202012Validator(block["inputSchema"])
        for name in accepted:
            self.assertTrue(validator.is_valid({"action": "arm", "name": name}), name)
        self.assertFalse(validator.is_valid({"action": "arm", "name": "not_a_barrier"}))

    def test_commit_snapshot_is_a_valid_test_only_response(self):
        block = schemas()
        snapshot = {"schema_version": "dnspy.edit.v1", "ok": True, "state": "reviewed",
                    "result": {"armed": True, "name": "commit_after_live_complete",
                               "owner_session_id": "session-test", "entered": True,
                               "released": False, "operation_waiters": 0,
                               "pending_request_key": None, "active_generation": 1,
                               "active_transaction_id": "edit-test"},
                    "warnings": [], "untrusted_sample_data": True}
        Draft202012Validator(block["outputSchema"]).validate(snapshot)

    def test_single_file_manual_embeds_same_barrier_schema(self):
        manual = MANUAL.read_text(encoding="utf-8")
        blocks = [json.loads(text) for text in re.findall(r"^```json\n(.*?)^```", manual, re.M | re.S)
                  if '"edit_test_barrier"' in text]
        embedded = [row["edit_test_barrier"] for row in blocks if isinstance(row, dict) and "edit_test_barrier" in row]
        self.assertEqual(len(embedded), 1)
        self.assertEqual(embedded[0]["inputSchema"]["oneOf"][0]["properties"]["name"]["enum"],
                         schemas()["inputSchema"]["oneOf"][0]["properties"]["name"]["enum"])
        self.assertEqual(names(embedded[0]), names(schemas()))


if __name__ == "__main__":
    unittest.main()
