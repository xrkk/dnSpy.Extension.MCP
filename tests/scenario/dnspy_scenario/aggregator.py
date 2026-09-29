"""Ledger aggregation CLI: coverage matrix + batch summary (REQ-006 / F-01).

Usage:
    python -m tests.scenario.dnspy_scenario.aggregator LEDGER.jsonl [MORE...] \
        [--baseline tools-list.json] [--min-per-tool 5] [--enforce] [--out report.json]

Exit 0: all ledger lines valid and (with --enforce) every advertised tool in the
baseline covered by >= min-per-tool DISTINCT scenario IDs. Any invalid line or
coverage gap -> exit non-zero (master plan §9 contract).
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

from .ledger import MATRIX_EXCLUDED_BATCH_PREFIXES, LedgerSchemaError, read_ledger


def build_report(ledger_paths: list[str], baseline: Path | None, min_per_tool: int) -> dict:
    matrix: dict[str, set[str]] = defaultdict(set)
    scenario_outcomes: dict[tuple[str, str], dict] = {}
    totals = {"calls": 0, "asserts_strong": 0, "asserts_weak": 0, "unexpected_errors": 0}
    excluded: dict[str, set[str]] = defaultdict(set)
    errors: list[str] = []

    for path in ledger_paths:
        try:
            rows = read_ledger(path)
        except LedgerSchemaError as exc:
            errors.append(str(exc))
            continue
        for row in rows:
            kind = row["kind"]
            if kind == "call":
                totals["calls"] += 1
                if row["outcome"] == "unexpected_error":
                    totals["unexpected_errors"] += 1
                batch = row["batch"]
                if batch.startswith(MATRIX_EXCLUDED_BATCH_PREFIXES):
                    excluded[row["tool"]].add(row["scenario_id"])
                else:
                    matrix[row["tool"]].add(row["scenario_id"])
            elif kind == "assert":
                if row["grade"] == "strong":
                    totals["asserts_strong"] += 1
                else:
                    totals["asserts_weak"] += 1
            elif kind == "result":
                scenario_outcomes[(row["scenario_id"], row["batch"])] = {
                    "outcome": row["outcome"],
                    "failure_class": row.get("failure_class"),
                }

    report: dict = {
        "ledgers": [str(p) for p in ledger_paths],
        "totals": totals,
        "matrix": {tool: sorted(ids) for tool, ids in sorted(matrix.items())},
        "matrix_counts": {tool: len(ids) for tool, ids in sorted(matrix.items())},
        "excluded_rerun_cells": {tool: sorted(ids) for tool, ids in sorted(excluded.items())},
        "scenarios": [
            {"scenario_id": sid, "batch": batch, **info}
            for (sid, batch), info in sorted(scenario_outcomes.items())
        ],
        "schema_errors": errors,
    }

    gaps: list[dict] = []
    if baseline is not None:
        names = json.loads(Path(baseline).read_text(encoding="utf-8"))
        if isinstance(names, dict):  # accept a raw tools/list result envelope too
            names = [t["name"] for t in names.get("tools", [])]
        for name in sorted(names):
            count = len(matrix.get(name, ()))
            if count < min_per_tool:
                gaps.append({"tool": name, "scenarios": count, "required": min_per_tool})
        report["baseline"] = str(baseline)
        report["baseline_size"] = len(names)
    report["coverage_gaps"] = gaps
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Aggregate scenario ledgers into a coverage matrix")
    parser.add_argument("ledgers", nargs="+", help="JSONL ledger files")
    parser.add_argument("--baseline", type=Path, default=None,
                        help="tools/list snapshot JSON (raw envelope or name list)")
    parser.add_argument("--min-per-tool", type=int, default=5)
    parser.add_argument("--enforce", action="store_true",
                        help="exit non-zero when any baseline tool is under min-per-tool")
    parser.add_argument("--out", type=Path, default=None, help="write the JSON report here")
    args = parser.parse_args(argv)

    report = build_report(args.ledgers, args.baseline, args.min_per_tool)
    if args.out is not None:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")

    print(f"tools-in-matrix: {len(report['matrix'])}; calls: {report['totals']['calls']}; "
          f"scenarios: {len(report['scenarios'])}")
    if report["schema_errors"]:
        for err in report["schema_errors"]:
            print(f"SCHEMA-ERROR: {err}", file=sys.stderr)
    if args.enforce:
        for gap in report["coverage_gaps"]:
            print(f"GAP: {gap['tool']} covered by {gap['scenarios']} scenarios (< {gap['required']})",
                  file=sys.stderr)

    if report["schema_errors"]:
        return 2
    if args.enforce and report["coverage_gaps"]:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
