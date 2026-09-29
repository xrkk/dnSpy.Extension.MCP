#!/usr/bin/env python3
"""P04 batch driver: per-scenario pytest processes with F-04 stop semantics.

Usage:
    python3 tests/scenario/run_batches.py --tfm net48 [--families 01..10]
                                          [--url http://.../mcp] [--ledger-root DIR]
    python3 tests/scenario/run_batches.py --tfm net10            # frozen sample set

Design (P04 sub-plan §2, AUD-502/508/509/514):
- One pytest process per scenario (single-VM serial, NON-004); after each
  scenario the ledger result row decides: blocked -> stop this batch at that
  point; fail -> record and continue; consecutive blocked -> batch stop.
- Idempotent: a scenario whose ledger already ends in result=pass is skipped;
  reruns archive the previous ledger as rerun-<sid>-<n>.jsonl first.
- Rerun archives are EXCLUDED from aggregation inputs (AUD-514).
- Declared-vs-actual reconciliation: each ledger's call-row tool set must
  equal its meta row's declared_tools (AUD-506); mismatches go to the report.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CORPUS = Path(__file__).resolve().parent / "corpus"
PYTEST = REPO / ".tmp" / "venv-scenario" / "bin" / "python"

NET10_SAMPLE = ["S-F01-01", "S-F02-01", "S-F03-05", "S-F04-03", "S-F05-01",
                "S-F06-02", "S-F07-04", "S-F08-07", "S-F09-01", "S-F10-05"]


def family_variants(fam: str) -> list[str]:
    fam_dir = CORPUS / f"fam{fam}"
    sids = []
    for d in sorted(fam_dir.glob("s_s_f*")):
        test = d / "test_variant.py"
        if test.exists():
            text = test.read_text(encoding="utf-8")
            for line in text.splitlines():
                if line.startswith("SCENARIO_ID ="):
                    sids.append(line.split("=", 1)[1].strip().strip('"\''))
                    break
    return sids


def ledger_path(root: Path, batch: str, sid: str) -> Path:
    return root / batch / f"{sid}.jsonl"


def last_result(path: Path) -> dict | None:
    if not path.exists():
        return None
    result = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if row.get("kind") == "result":
            result = row
    return result


def run_scenario(sid: str, url: str, batch: str, root: Path, tfm: str) -> dict:
    fam = sid[2:4]
    target = CORPUS / f"fam{fam}" / f"s_{sid.lower().replace('-', '_')}" / "test_variant.py"
    if not target.exists():
        return {"scenario": sid, "status": "missing-module"}

    lp = ledger_path(root, batch, sid)
    prev = last_result(lp)
    if prev and prev.get("outcome") == "pass":
        return {"scenario": sid, "status": "skip-pass"}

    if lp.exists():  # archive previous attempt (rerun naming, AUD-509/514)
        n = 1
        while (root / batch / f"rerun-{sid}-{n}.jsonl").exists():
            n += 1
        shutil.move(str(lp), root / batch / f"rerun-{sid}-{n}.jsonl")

    env = dict(os.environ)
    env.update({"SCENARIO_BATCH": batch, "SCENARIO_URL": url, "SCENARIO_TFM": tfm,
                "SCENARIO_LEDGER_ROOT": str(root)})
    proc = subprocess.run(
        [str(PYTEST), "-q", "--no-header", "-p", "no:cacheprovider", str(target)],
        cwd=str(REPO), env=env, capture_output=True, text=True, timeout=300)
    result = last_result(lp)
    status = (result or {}).get("outcome") or ("no-ledger" if proc.returncode else "unknown")
    return {"scenario": sid, "status": status,
            "failure_class": (result or {}).get("failure_class"),
            "pytest_rc": proc.returncode}


def declared_vs_actual(root: Path, batch: str) -> list[dict]:
    mismatches = []
    for lp in sorted((root / batch).glob("*.jsonl")):
        if lp.name.startswith("rerun-"):
            continue  # AUD-514: rerun archives excluded
        declared, actual = None, set()
        for line in lp.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            row = json.loads(line)
            if row.get("kind") == "meta":
                declared = set(row.get("declared_tools") or [])
            elif row.get("kind") == "call":
                actual.add(row.get("tool"))
        if declared is None:
            continue
        if declared != actual:
            mismatches.append({"scenario": lp.stem, "batch": batch,
                               "declared_not_called": sorted(declared - actual),
                               "called_not_declared": sorted(actual - declared)})
    return mismatches


def main() -> int:
    parser = argparse.ArgumentParser(description="Scenario batch driver (P04)")
    parser.add_argument("--tfm", choices=["net48", "net10"], required=True)
    parser.add_argument("--url", default=None)
    parser.add_argument("--ledger-root", default=None)
    parser.add_argument("--families", default="01,02,03,04,05,06,07,08,09,10")
    parser.add_argument("--only", default=None, help="comma-separated scenario ids")
    args = parser.parse_args()

    url = args.url or ("http://192.168.204.240:15100/mcp" if args.tfm == "net48"
                       else "http://192.168.204.240:15101/mcp")
    root = Path(args.ledger_root or REPO / ".tmp" / "scenario-ledgers")
    plan: list[tuple[str, list[str]]] = []
    if args.tfm == "net10":
        plan.append(("net10-sample", NET10_SAMPLE))
    else:
        for fam in args.families.split(","):
            fam = fam.strip().zfill(2)
            plan.append((f"net48-fam{fam}", family_variants(fam)))
    if args.only:
        only = {s.strip() for s in args.only.split(",")}
        plan = [(b, [s for s in sids if s in only]) for b, sids in plan]

    report = {"tfm": args.tfm, "url": url, "batches": [], "reconciliation": []}
    for batch, sids in plan:
        entry = {"batch": batch, "scenarios": [], "stopped_at": None}
        consecutive_blocked = 0
        for sid in sids:
            outcome = run_scenario(sid, url, batch, root, args.tfm)
            entry["scenarios"].append(outcome)
            if outcome["status"] == "blocked":
                consecutive_blocked += 1
                entry["stopped_at"] = sid  # F-04: stop the batch here
                if consecutive_blocked >= 1:
                    break
            else:
                consecutive_blocked = 0
        entry["reconciliation"] = declared_vs_actual(root, batch)
        report["reconciliation"].extend(entry["reconciliation"])
        report["batches"].append(entry)
        print(f"[batch {batch}] " + " ".join(
            f"{s['scenario']}:{s['status']}" for s in entry["scenarios"]))

    out = root / f"driver-report-{args.tfm}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print("report:", out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
