"""Scenario-layer pytest fixtures: the scenario_env public helper (REQ-006).

Single sanctioned construction path for the recording client, the unified
assertion API, and the teardown standard reset routine (REQ-009/F-04). The
result row is written by a finalizer in a finally block (SPEC-ledger §3.5):
scenario outcome, assertion counters and reset verdicts always land in the
ledger, including uncaught exceptions and BLOCKED teardowns.
"""

from __future__ import annotations

import os
from pathlib import Path
from types import SimpleNamespace

import pytest

from dnspy_mcp import DnSpyClient

from dnspy_scenario.assertions import AssertionApi
from dnspy_scenario.ledger import LedgerWriter
from dnspy_scenario.recorder import RecordingClient
from dnspy_scenario.reset import ResetBlocked, ResetContext, run_standard_reset
from dnspy_scenario.vmbridge import VmBridge

BASELINES = Path(__file__).parent / "baselines"
SCENARIO_URL = os.environ.get("SCENARIO_URL", "http://192.168.204.240:15100/mcp")
SCENARIO_TFM = os.environ.get("SCENARIO_TFM", "net48")
SCENARIO_BATCH = os.environ.get("SCENARIO_BATCH", "net48-smoke")
LEDGER_ROOT = Path(os.environ.get("SCENARIO_LEDGER_ROOT", ".tmp/scenario-ledgers"))
VM_BRIDGE_URL = os.environ.get("SCENARIO_BRIDGE_URL", "http://192.168.204.240:15110")
VM_SAMPLES_BASELINE = BASELINES / "samples-baseline.json"
VM_ARTIFACTS_INVENTORY = BASELINES / "artifacts-inventory.json"


@pytest.hookimpl(hookwrapper=True, tryfirst=True)
def pytest_runtest_makereport(item, call):
    """Stash the per-phase report so teardown knows whether the body failed."""
    outcome = yield
    rep = outcome.get_result()
    setattr(item, "rep_" + rep.when, rep)


def _probe_edit_status() -> dict:
    with DnSpyClient(SCENARIO_URL) as probe:
        return probe.edit_status()


def _probe_debug_state() -> str:
    with DnSpyClient(SCENARIO_URL) as probe:
        caps = probe.call_tool_json("debug_capabilities", {})
        return ((caps.get("debug_context") or {}).get("state")) if isinstance(caps, dict) else None


@pytest.fixture
def scenario_env(request):
    module = request.node.module
    scenario_id = getattr(module, "SCENARIO_ID")
    declared_tools = list(getattr(module, "DECLARED_TOOLS"))
    docstring = (request.function.__doc__ or "").strip()

    ledger = LedgerWriter(LEDGER_ROOT / SCENARIO_BATCH / f"{scenario_id}.jsonl")
    ledger.meta(scenario_id, SCENARIO_BATCH, declared_tools, SCENARIO_TFM, docstring)
    asserts = AssertionApi(ledger, scenario_id, SCENARIO_BATCH)
    client = RecordingClient(ledger, scenario_id, SCENARIO_BATCH, asserts, SCENARIO_URL)
    client.initialize()

    yield SimpleNamespace(client=client, asserts=asserts, ledger=ledger,
                          scenario_id=scenario_id, batch=SCENARIO_BATCH)

    # ---- teardown: standard reset routine + result finalizer (always runs) ----
    body_failed = bool(getattr(request.node, "rep_call", None) and request.node.rep_call.failed)
    client_closed = bool(client.closed_recorded)

    ctx = ResetContext(
        ledger=ledger, scenario_id=scenario_id, batch=SCENARIO_BATCH,
        probe_edit_status=_probe_edit_status,
        probe_debug_state=_probe_debug_state,
        client_closed=client_closed,
        bridge=VmBridge(VM_BRIDGE_URL),
        samples_baseline_path=VM_SAMPLES_BASELINE,
        artifacts_inventory_path=VM_ARTIFACTS_INVENTORY,
    )
    reset_failures: list[dict] = []
    try:
        run_standard_reset(ctx, drain=lambda: client.close() if not client_closed else None)
    except ResetBlocked as blocked:
        reset_failures = blocked.failures

    env_blocked = any(f.get("env") for f in reset_failures)
    if reset_failures:
        ledger.result(scenario_id, SCENARIO_BATCH, "blocked",
                      asserts.strong_passed, asserts.weak_passed,
                      client.unexpected_errors,
                      failure_class="env_blocked" if env_blocked else "reset_blocked")
        pytest.fail("BLOCKED: " + "; ".join(
            f"{f['check']}: {f['detail']}" for f in reset_failures), pytrace=False)
    elif body_failed or client.unexpected_errors or asserts.strong_failed:
        ledger.result(scenario_id, SCENARIO_BATCH, "fail",
                      asserts.strong_passed, asserts.weak_passed,
                      client.unexpected_errors,
                      failure_class="strong_assertion" if (asserts.strong_failed and not client.unexpected_errors) else "unexpected_error")
    else:
        ledger.result(scenario_id, SCENARIO_BATCH, "pass",
                      asserts.strong_passed, asserts.weak_passed,
                      client.unexpected_errors)
