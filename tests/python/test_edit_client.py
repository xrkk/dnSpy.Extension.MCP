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


if __name__ == "__main__":
    unittest.main()
