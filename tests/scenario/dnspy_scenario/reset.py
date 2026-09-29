"""Standard reset routine (REQ-009 / F-04 / ACC-043) — five checks.

Runs from the conftest teardown for every scenario, always in a finally block.
Any failing check writes a reset row with verdict=blocked and marks the
scenario BLOCKED; the batch-level stop mapping belongs to the P04 batch driver.

Checks:
  1. edit_idle        — edit_status reports no in-flight transaction (state=idle)
  2. debug_idle       — debug_capabilities debug_context.state == idle
  3. session_closed   — client-side lifecycle: the fixture-constructed recording
                        client must have close()d (server exposes no session count;
                        construction is only possible through the fixture helper,
                        so client-side tracking is sound)
  4. samples_hash     — VM samples tree hashes equal the baseline (recursive)
  5. artifacts_inventory — ArtifactRoot inventory: already-recorded files must be
                        unchanged; new files are appended to the inventory; files
                        must not appear in the samples root
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from .ledger import LedgerWriter
from .vmbridge import VmBridge, VmBridgeError

SAMPLES_ROOT = r"E:\dnspy-scenario\samples"
ARTIFACTS_ROOT = r"E:\dnspy-scenario\artifacts"


class ResetBlocked(Exception):
    """Standard reset routine found dirty state; scenario must be BLOCKED."""

    def __init__(self, failures: list[dict]) -> None:
        self.failures = failures
        super().__init__("reset blocked: " + "; ".join(
            f"{f['check']}: {f['detail']}" for f in failures))


@dataclass
class ResetContext:
    ledger: LedgerWriter
    scenario_id: str
    batch: str
    probe_edit_status: object = None          # callable() -> dict
    probe_debug_state: object = None          # callable() -> str
    client_closed: Optional[bool] = None
    bridge: Optional[VmBridge] = None
    samples_baseline_path: Path = None        # type: ignore[assignment]
    artifacts_inventory_path: Path = None     # type: ignore[assignment]
    failures: list = field(default_factory=list)

    def fail(self, check: str, detail: str) -> None:
        self.failures.append({"check": check, "detail": detail[:200]})
        self.ledger.reset(self.scenario_id, self.batch, check, "blocked", detail)

    def pass_(self, check: str, detail: str = "") -> None:
        self.ledger.reset(self.scenario_id, self.batch, check, "pass", detail)


def run_standard_reset(ctx: ResetContext, drain=None) -> None:
    """Run the five checks; `drain` (best-effort session close) runs between
    check 2 and check 3 — live state must be probed BEFORE the drain closes
    the session (the server aborts an orphan session's edit transaction, so
    probing after the drain would mask a dirty transaction)."""
    # 1. edit transaction drained
    try:
        status = ctx.probe_edit_status() if callable(ctx.probe_edit_status) else None
        state = status.get("state") if isinstance(status, dict) else None
        if state == "idle":
            ctx.pass_("edit_idle", f"state={state}")
        else:
            ctx.fail("edit_idle", f"state={state!r} (in-flight transaction?)")
    except Exception as exc:  # probe itself failed: environment-class blocker
        ctx.fail("edit_idle", f"probe error: {type(exc).__name__}: {exc}")
        ctx.failures[-1]["check"] = "edit_idle"
        ctx.failures[-1]["env"] = True

    # 2. debug idle
    try:
        dbg = ctx.probe_debug_state() if callable(ctx.probe_debug_state) else None
        if dbg == "idle":
            ctx.pass_("debug_idle", f"state={dbg}")
        else:
            ctx.fail("debug_idle", f"state={dbg!r}")
    except Exception as exc:
        ctx.fail("debug_idle", f"probe error: {type(exc).__name__}: {exc}")
        ctx.failures[-1]["env"] = True

    # 3. session closed (client-side lifecycle tracking) — drain first if asked
    if drain is not None:
        try:
            drain()
        except Exception:
            pass
    if ctx.client_closed is True:
        ctx.pass_("session_closed", "recording client closed")
    elif ctx.client_closed is False:
        ctx.fail("session_closed", "scenario left its recording client open")
    else:
        ctx.fail("session_closed", "no client lifecycle recorded")

    # 4. samples hash baseline
    if ctx.bridge is None:
        ctx.fail("samples_hash", "no vm bridge configured (env_blocked)")
        ctx.failures[-1]["env"] = True
    else:
        try:
            baseline = json.loads(Path(ctx.samples_baseline_path).read_text(encoding="utf-8"))
            expect = {row["path"]: row["sha256"] for row in baseline["files"]}
            actual = ctx.bridge.hash_tree(SAMPLES_ROOT)
            missing = sorted(set(expect) - set(actual))
            extra = sorted(set(actual) - set(expect))
            changed = sorted(p for p in set(expect) & set(actual) if expect[p] != actual[p])
            if missing or extra or changed:
                ctx.fail("samples_hash",
                         f"missing={missing[:3]} extra={extra[:3]} changed={changed[:3]}")
            else:
                ctx.pass_("samples_hash", f"{len(expect)} files match baseline")
        except VmBridgeError as exc:
            ctx.fail("samples_hash", f"bridge error: {exc}")
            ctx.failures[-1]["env"] = True
        except (OSError, ValueError, KeyError) as exc:
            ctx.fail("samples_hash", f"baseline error: {type(exc).__name__}: {exc}")
            ctx.failures[-1]["env"] = True

    # 5. artifacts inventory (append-only)
    try:
        inv_path = Path(ctx.artifacts_inventory_path)
        inventory = json.loads(inv_path.read_text(encoding="utf-8")) if inv_path.exists() else {
            "schema_version": "dnspy.scenario.inventory.v1", "files": []}
        recorded = {row["path"]: row for row in inventory["files"]}
        if ctx.bridge is None:
            ctx.fail("artifacts_inventory", "no vm bridge configured (env_blocked)")
            ctx.failures[-1]["env"] = True
        else:
            actual = ctx.bridge.hash_tree(ARTIFACTS_ROOT)
            # edit-checkpoints/ is product-managed state (commit persists and
            # may REWRITE checkpoint files there); only files directly under
            # ArtifactRoot are test products and must not mutate once recorded.
            def product_managed(p):
                return p.replace("\\", "/").startswith("edit-checkpoints/")
            mutated = sorted(p for p, row in recorded.items()
                             if (p not in actual or actual[p] != row["sha256"])
                             and not product_managed(p))
            churned = sorted(p for p, row in recorded.items()
                             if (p not in actual or actual[p] != row["sha256"])
                             and product_managed(p))
            if mutated:
                ctx.fail("artifacts_inventory", f"recorded files changed/removed: {mutated[:3]}")
            else:
                new_paths = sorted(set(actual) - set(recorded))
                for path in new_paths:
                    inventory["files"].append({
                        "path": path, "sha256": actual[path],
                        "first_seen_scenario": ctx.scenario_id})
                inv_path.parent.mkdir(parents=True, exist_ok=True)
                inv_path.write_text(json.dumps(inventory, ensure_ascii=False, indent=1),
                                    encoding="utf-8")
                ctx.pass_("artifacts_inventory",
                          f"{len(recorded)} recorded, +{len(new_paths)} new, "
                          f"{len(churned)} product-managed churned")
    except (OSError, ValueError, KeyError) as exc:
        ctx.fail("artifacts_inventory", f"inventory error: {type(exc).__name__}: {exc}")
        ctx.failures[-1]["env"] = True

    if ctx.failures:
        raise ResetBlocked(ctx.failures)
