"""Component unit tests: ledger schema enforcement (IMP-204, no VM needed)."""

from __future__ import annotations

import json

import pytest

from dnspy_scenario.ledger import LedgerSchemaError, LedgerWriter, read_ledger


def test_meta_call_result_roundtrip(tmp_path):
    ledger = LedgerWriter(tmp_path / "batch" / "S-X-01.jsonl")
    ledger.meta("S-X-01", "net48-fam-01", ["a", "b"], "net48", "doc")
    ledger.call("S-X-01", "net48-fam-01", 1, "a", {"x": 1},
                {"is_error": False, "error_code": None, "content_head": "ok"}, 12, "ok")
    ledger.assertion("S-X-01", "net48-fam-01", 1, "strong", "pass", "a == b")
    ledger.reset("S-X-01", "net48-fam-01", "edit_idle", "pass", "state=idle")
    ledger.result("S-X-01", "net48-fam-01", "pass", 1, 0, 0)

    rows = read_ledger(ledger.path)
    assert [r["kind"] for r in rows] == ["meta", "call", "assert", "reset", "result"]
    assert rows[0]["schema_version"] == "dnspy.scenario.ledger.v1"
    assert rows[-1]["outcome"] == "pass"
    assert "failure_class" not in rows[-1]


def test_missing_field_rejected(tmp_path):
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    with pytest.raises(LedgerSchemaError):
        ledger.write({"kind": "call", "scenario_id": "S", "batch": "b"})  # missing requireds


def test_is_error_with_ok_outcome_rejected(tmp_path):
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    with pytest.raises(LedgerSchemaError):
        ledger.call("S", "b", 1, "t", {}, {"is_error": True}, 1, "ok")


def test_result_fail_requires_failure_class(tmp_path):
    """Raw rows without failure_class on non-pass outcomes are schema-rejected;
    the builder fills a default (collection_error) for the finalizer."""
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    with pytest.raises(LedgerSchemaError):
        ledger.write({"kind": "result", "scenario_id": "S", "batch": "b", "step_seq": 0,
                      "outcome": "fail", "strong_asserts": 0, "weak_asserts": 0,
                      "unexpected_errors": 1})


def test_result_fail_with_class_accepted(tmp_path):
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    ledger.result("S", "b", "blocked", 0, 0, 0, failure_class="reset_blocked")
    rows = read_ledger(ledger.path)
    assert rows[0]["failure_class"] == "reset_blocked"


def test_bad_line_in_file_rejected(tmp_path):
    path = tmp_path / "l.jsonl"
    path.write_text(json.dumps({"kind": "meta", "scenario_id": "S", "batch": "b",
                                "step_seq": 0, "declared_tools": [], "tfm": "net48",
                                "docstring": "d"}) + "\nnot-json\n", encoding="utf-8")
    with pytest.raises(LedgerSchemaError):
        read_ledger(path)
