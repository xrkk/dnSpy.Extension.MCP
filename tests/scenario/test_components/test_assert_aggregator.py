"""Component unit tests: assertion API + aggregator matrix math (IMP-204)."""

from __future__ import annotations

import json

import pytest

from dnspy_scenario.assertions import AssertionApi
from dnspy_scenario.ledger import LedgerWriter
from dnspy_scenario.aggregator import build_report


@pytest.fixture
def api(tmp_path):
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    return AssertionApi(ledger, "S-A-01", "net48-fam-01")


def test_strong_equal_pass_fail(api):
    api.strong_equal(1, 5, 5, "x")
    assert api.strong_passed == 1
    with pytest.raises(AssertionError):
        api.strong_equal(1, 5, 6, "x")
    assert api.strong_failed == 1


def test_strong_set_ignores_order(api):
    api.strong_set(1, ["b", "a", "b"], {"a", "b"}, "set")


def test_strong_set_detects_missing(api):
    with pytest.raises(AssertionError):
        api.strong_set(1, ["a"], {"a", "b"}, "set")


def test_weak_fields(api):
    api.weak_fields(1, {"state": "idle", "ok": True}, ["state", "ok"], "s")
    with pytest.raises(AssertionError):
        api.weak_fields(1, {"state": None}, ["state"], "s")


def _write_ledger(path, scenario, batch, tools, outcome="pass"):
    ledger = LedgerWriter(path)
    ledger.meta(scenario, batch, tools, "net48", "d")
    for i, tool in enumerate(tools, 1):
        ledger.call(scenario, batch, i, tool, {},
                    {"is_error": False, "error_code": None, "content_head": ""}, 1, "ok")
    ledger.assertion(scenario, batch, 1, "strong", "pass", "d")
    ledger.result(scenario, batch, outcome, 1, 0, 0)


def test_matrix_dedup_by_scenario(tmp_path):
    p1 = tmp_path / "a.jsonl"
    _write_ledger(p1, "S-1", "net48-fam-01", ["t1", "t2", "t1"])  # t1 twice in scenario
    p2 = tmp_path / "b.jsonl"
    _write_ledger(p2, "S-2", "net48-fam-01", ["t1", "t3"])
    report = build_report([str(p1), str(p2)], None, 5)
    assert report["matrix_counts"] == {"t1": 2, "t2": 1, "t3": 1}  # per-scenario dedup


def test_rerun_batches_excluded_from_matrix(tmp_path):
    p1 = tmp_path / "a.jsonl"
    _write_ledger(p1, "S-1", "net48-fam-01", ["t1"])
    p2 = tmp_path / "b.jsonl"
    _write_ledger(p2, "S-1", "net10-sample", ["t2"])   # same scenario re-run, excluded batch
    p3 = tmp_path / "c.jsonl"
    _write_ledger(p3, "S-9", "net10-rerun-family3", ["t3"])  # upgrade rerun, excluded
    report = build_report([str(p1), str(p2), str(p3)], None, 5)
    assert report["matrix_counts"] == {"t1": 1}
    assert set(report["excluded_rerun_cells"]) == {"t2", "t3"}


def test_baseline_gap_detection(tmp_path):
    p1 = tmp_path / "a.jsonl"
    _write_ledger(p1, "S-1", "net48-fam-01", ["t1"])
    baseline = tmp_path / "baseline.json"
    baseline.write_text(json.dumps({"tools": [{"name": "t1"}, {"name": "t2"}]}), encoding="utf-8")
    report = build_report([str(p1)], baseline, 2)
    gaps = {g["tool"] for g in report["coverage_gaps"]}
    assert gaps == {"t1", "t2"}  # t1 under min, t2 uncovered


def test_schema_error_reported(tmp_path):
    bad = tmp_path / "bad.jsonl"
    bad.write_text('{"kind": "call"}\n', encoding="utf-8")
    report = build_report([str(bad)], None, 5)
    assert report["schema_errors"]
