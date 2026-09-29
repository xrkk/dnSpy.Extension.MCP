"""Component unit tests: batch driver stop/idempotency/exclusion semantics."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from run_batches import (NET10_SAMPLE, declared_vs_actual, family_variants,
                         last_result, ledger_path)


def test_net10_sample_frozen_one_per_family():
    assert len(NET10_SAMPLE) == 10
    fams = [s.split("-")[1][1:] for s in NET10_SAMPLE]
    assert sorted(set(fams)) == sorted(fams)  # exactly one per family 01..10
    assert "S-F02-01" in NET10_SAMPLE and "S-F05-01" in NET10_SAMPLE  # 编辑/调试链路


def test_family_variants_collects_ten():
    sids = family_variants("01")
    assert len(sids) == 10 and sids[0] == "S-F01-01"


def _write_ledger(path: Path, outcome: str, declared=None, called=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    rows = [{"kind": "meta", "scenario_id": path.stem, "batch": "b", "step_seq": 0,
             "declared_tools": sorted(declared or called or []), "tfm": "net48",
             "docstring": "d"}]
    for i, tool in enumerate(called or [], 1):
        rows.append({"kind": "call", "scenario_id": path.stem, "batch": "b",
                     "step_seq": i, "tool": tool, "request": {},
                     "response_summary": {"is_error": False}, "duration_ms": 1,
                     "outcome": "ok"})
    rows.append({"kind": "result", "scenario_id": path.stem, "batch": "b",
                 "step_seq": 0, "outcome": outcome, "strong_asserts": 1,
                 "weak_asserts": 0, "unexpected_errors": 0,
                 **({"failure_class": "x"} if outcome != "pass" else {})})
    path.write_text("\n".join(json.dumps(r) for r in rows) + "\n", encoding="utf-8")


def test_last_result_reads_outcome(tmp_path):
    lp = tmp_path / "b" / "S-X.jsonl"
    _write_ledger(lp, "pass")
    assert last_result(lp)["outcome"] == "pass"
    assert last_result(tmp_path / "b" / "missing.jsonl") is None


def test_reconciliation_detects_mismatch_and_excludes_reruns(tmp_path):
    batch = tmp_path / "b"
    _write_ledger(batch / "S-GOOD.jsonl", "pass",
                  declared=["a", "b"], called=["a", "b"])
    _write_ledger(batch / "S-BAD.jsonl", "pass",
                  declared=["a", "b"], called=["a", "c"])
    _write_ledger(batch / "rerun-S-BAD-1.jsonl", "pass",
                  declared=["a"], called=["a", "z"])  # excluded (AUD-514)
    mismatches = declared_vs_actual(tmp_path, "b")
    assert len(mismatches) == 1
    assert mismatches[0]["scenario"] == "S-BAD"
    assert mismatches[0]["declared_not_called"] == ["b"]
    assert mismatches[0]["called_not_declared"] == ["c"]
