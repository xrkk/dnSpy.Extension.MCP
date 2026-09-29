"""Component unit tests: standard reset routine five checks (IMP-204, fakes only)."""

from __future__ import annotations

import json

import pytest

from dnspy_scenario.ledger import LedgerWriter
from dnspy_scenario.reset import ResetBlocked, ResetContext, run_standard_reset


class FakeBridge:
    def __init__(self, samples: dict, artifacts: dict):
        self._samples = samples
        self._artifacts = artifacts

    def hash_tree(self, root: str) -> dict:
        if "samples" in root:
            return dict(self._samples)
        return dict(self._artifacts)


def make_ctx(tmp_path, *, edit_state="idle", debug_state="idle", client_closed=True,
             samples=None, artifacts=None, baseline_files=None, inventory_files=None):
    samples = samples if samples is not None else {
        "TestIL.dll": "h1", "p01-fixtures/TestIL.dll": "h2",
        "p01-fixtures/TestIL.deps.json": "h3", "p01-fixtures/TestIL.pdb": "h4"}
    baseline = {"files": baseline_files if baseline_files is not None else [
        {"path": "TestIL.dll", "sha256": "h1"},
        {"path": "p01-fixtures/TestIL.dll", "sha256": "h2"},
        {"path": "p01-fixtures/TestIL.deps.json", "sha256": "h3"},
        {"path": "p01-fixtures/TestIL.pdb", "sha256": "h4"}]}
    baseline_path = tmp_path / "samples-baseline.json"
    baseline_path.write_text(json.dumps(baseline), encoding="utf-8")
    inventory_path = tmp_path / "artifacts-inventory.json"
    inventory_path.write_text(json.dumps(
        {"schema_version": "dnspy.scenario.inventory.v1",
         "files": inventory_files or []}), encoding="utf-8")
    ledger = LedgerWriter(tmp_path / "l.jsonl")
    ledger.meta("S-T-01", "net48-fam-01", ["x"], "net48", "d")
    return ResetContext(
        ledger=ledger, scenario_id="S-T-01", batch="net48-fam-01",
        probe_edit_status=lambda: {"state": edit_state},
        probe_debug_state=lambda: debug_state,
        client_closed=client_closed,
        bridge=FakeBridge(samples, artifacts if artifacts is not None else {}),
        samples_baseline_path=baseline_path,
        artifacts_inventory_path=inventory_path,
    ), ledger


def read_resets(ledger):
    from dnspy_scenario.ledger import read_ledger
    return [r for r in read_ledger(ledger.path) if r["kind"] == "reset"]


def test_clean_state_passes(tmp_path):
    ctx, ledger = make_ctx(tmp_path)
    run_standard_reset(ctx)
    resets = read_resets(ledger)
    assert all(r["verdict"] == "pass" for r in resets)
    assert len(resets) == 5


def test_dirty_edit_transaction_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, edit_state="open")
    with pytest.raises(ResetBlocked) as info:
        run_standard_reset(ctx)
    failed = {f["check"] for f in info.value.failures}
    assert failed == {"edit_idle"}


def test_dirty_open_session_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, client_closed=False)
    with pytest.raises(ResetBlocked) as info:
        run_standard_reset(ctx)
    assert {f["check"] for f in info.value.failures} == {"session_closed"}


def test_dirty_sample_file_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, samples={
        "TestIL.dll": "h1", "stray.exe": "hx",
        "p01-fixtures/TestIL.dll": "h2",
        "p01-fixtures/TestIL.deps.json": "h3", "p01-fixtures/TestIL.pdb": "h4"})
    with pytest.raises(ResetBlocked) as info:
        run_standard_reset(ctx)
    assert {f["check"] for f in info.value.failures} == {"samples_hash"}


def test_sample_modified_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, samples={
        "TestIL.dll": "CHANGED", "p01-fixtures/TestIL.dll": "h2",
        "p01-fixtures/TestIL.deps.json": "h3", "p01-fixtures/TestIL.pdb": "h4"})
    with pytest.raises(ResetBlocked):
        run_standard_reset(ctx)


def test_artifact_new_file_appended_not_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, artifacts={"out.dll": "ah1"})
    run_standard_reset(ctx)
    inventory = json.loads(ctx.artifacts_inventory_path.read_text(encoding="utf-8"))
    assert inventory["files"] == [{"path": "out.dll", "sha256": "ah1",
                                   "first_seen_scenario": "S-T-01"}]
    resets = read_resets(ledger)
    assert [r for r in resets if r["check"] == "artifacts_inventory"][0]["verdict"] == "pass"


def test_artifact_recorded_file_mutated_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path, artifacts={},
                           inventory_files=[{"path": "out.dll", "sha256": "old",
                                             "first_seen_scenario": "S-X"}])
    # artifacts now report a different hash for the recorded file
    ctx.bridge = FakeBridge(samples={
        "TestIL.dll": "h1", "p01-fixtures/TestIL.dll": "h2",
        "p01-fixtures/TestIL.deps.json": "h3", "p01-fixtures/TestIL.pdb": "h4"},
        artifacts={"out.dll": "new"})
    with pytest.raises(ResetBlocked) as info:
        run_standard_reset(ctx)
    assert {f["check"] for f in info.value.failures} == {"artifacts_inventory"}


def test_missing_bridge_env_blocked(tmp_path):
    ctx, ledger = make_ctx(tmp_path)
    ctx.bridge = None
    with pytest.raises(ResetBlocked) as info:
        run_standard_reset(ctx)
    env_flags = [f.get("env") for f in info.value.failures]
    assert all(env_flags), "bridge absence must be env-class"
    checks = {f["check"] for f in info.value.failures}
    assert {"samples_hash", "artifacts_inventory"} <= checks
