from __future__ import annotations

import unittest
from unittest.mock import patch

from dnspy_mcp.client import DnSpyClient


class EditClientConvenienceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.client = DnSpyClient("http://127.0.0.1:15378/")

    def _assert_call(self, expected_name: str, expected_args: dict, invoke) -> None:
        with patch.object(self.client, "call_tool_json", return_value={"ok": True}) as call:
            self.assertEqual({"ok": True}, invoke())
            call.assert_called_once_with(expected_name, expected_args)

    def test_begin_omits_optional_mvid(self) -> None:
        self._assert_call(
            "edit_begin",
            {"request_id": "r1", "assembly_name": "Fixture"},
            lambda: self.client.edit_begin("r1", "Fixture"),
        )

    def test_begin_carries_mvid(self) -> None:
        self._assert_call(
            "edit_begin",
            {"request_id": "r1", "assembly_name": "Fixture", "module_mvid": "m"},
            lambda: self.client.edit_begin("r1", "Fixture", module_mvid="m"),
        )

    def test_begin_carries_family(self) -> None:
        self._assert_call(
            "edit_begin",
            {"request_id": "r1", "assembly_name": "Fixture", "source_family_id": "family-a"},
            lambda: self.client.edit_begin("r1", "Fixture", source_family_id="family-a"),
        )

    def test_apply_preserves_id_revision_and_structured_operation(self) -> None:
        operation = {"kind": "type_add", "name": "Added"}
        self._assert_call(
            "edit_apply",
            {"request_id": "a1", "transaction_id": "tx", "expected_revision": 3, "operation": operation},
            lambda: self.client.edit_apply("a1", "tx", 3, operation),
        )

    def test_review_omits_unrequested_dynamic_validation(self) -> None:
        self._assert_call(
            "edit_review",
            {"request_id": "v1", "transaction_id": "tx", "expected_revision": 4},
            lambda: self.client.edit_review("v1", "tx", 4),
        )

    def test_rollback(self) -> None:
        self._assert_call(
            "edit_rollback",
            {"request_id": "x1", "transaction_id": "tx"},
            lambda: self.client.edit_rollback("x1", "tx"),
        )

    def test_commit(self) -> None:
        self._assert_call(
            "edit_commit",
            {"request_id": "c1", "transaction_id": "tx", "expected_revision": 4,
             "review_id": "review", "review_revision": 4, "confirmed_risk_ids": ["risk-a"]},
            lambda: self.client.edit_commit("c1", "tx", 4, "review", 4, ["risk-a"]),
        )

    def test_history_omits_optional_fields(self) -> None:
        self._assert_call("edit_history", {}, lambda: self.client.edit_history())

    def test_history_carries_page(self) -> None:
        self._assert_call(
            "edit_history",
            {"lineage_id": "lineage-a", "cursor": "cursor", "page_size": 25},
            lambda: self.client.edit_history(lineage_id="lineage-a", cursor="cursor", page_size=25),
        )

    def test_undo_and_redo(self) -> None:
        self._assert_call(
            "edit_undo",
            {"request_id": "u1", "lineage_id": "lineage-a", "expected_checkpoint_id": "checkpoint-a"},
            lambda: self.client.edit_undo("u1", "lineage-a", "checkpoint-a"),
        )
        self._assert_call(
            "edit_redo",
            {"request_id": "d1", "lineage_id": "lineage-a", "expected_checkpoint_id": "checkpoint-a",
             "child_checkpoint_id": "checkpoint-b"},
            lambda: self.client.edit_redo("d1", "lineage-a", "checkpoint-a", child_checkpoint_id="checkpoint-b"),
        )

    def test_restore(self) -> None:
        self._assert_call(
            "edit_restore",
            {"request_id": "s1", "lineage_id": "lineage-a", "checkpoint_id": "checkpoint-a",
             "action": "apply", "replay_id": "replay-a", "expected_live_fingerprint": "f" * 64,
             "confirm_validated_drift": True},
            lambda: self.client.edit_restore("s1", "lineage-a", "checkpoint-a", "apply",
                                             replay_id="replay-a", expected_live_fingerprint="f" * 64,
                                             confirm_validated_drift=True),
        )

    def test_export_recover_accept(self) -> None:
        self._assert_call(
            "edit_export",
            {"request_id": "e1", "lineage_id": "lineage-a", "checkpoint_id": "checkpoint-a",
             "output_path": r"C:\out.dll"},
            lambda: self.client.edit_export("e1", "lineage-a", "checkpoint-a", output_path=r"C:\out.dll"),
        )
        self._assert_call(
            "edit_recover",
            {"request_id": "r1", "recovery_id": "recovery-a", "action": "cleanup_temp"},
            lambda: self.client.edit_recover("r1", "recovery-a", "cleanup_temp"),
        )
        self._assert_call(
            "edit_accept_live",
            {"request_id": "a1", "assembly_name": "Fixture", "source_family_id": "family-a",
             "superseded_lineage_id": "lineage-a", "expected_live_fingerprint": "e" * 64,
             "acknowledge_new_baseline": True, "module_mvid": "mvid"},
            lambda: self.client.edit_accept_live("a1", "Fixture", "family-a", "lineage-a", "e" * 64,
                                                 module_mvid="mvid"),
        )


if __name__ == "__main__":
    unittest.main()
