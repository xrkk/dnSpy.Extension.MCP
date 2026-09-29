"""Scenario structure static check CLI (master plan §9 row 3, used by P03/ACC-045).

Usage:
    python -m tests.scenario.dnspy_scenario.static_check tests/scenario/corpus [--min-tools 10]

AST-based (no import side effects): for every test function in test_*.py files,
requires module-level SCENARIO_ID (str) and DECLARED_TOOLS (list of distinct
tool names) plus a docstring on every scenario test; reports the distinct
declared tool count per scenario. Exit 0 iff every scenario declares at least
--min-tools distinct tools and all docstrings are present.
"""

from __future__ import annotations

import argparse
import ast
import json
import sys
from pathlib import Path

REQUIRED_CONSTANTS = ("SCENARIO_ID", "DECLARED_TOOLS")


def check_module(path: Path) -> dict:
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    constants: dict[str, object] = {}
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1:
            target = node.targets[0]
            if isinstance(target, ast.Name) and target.id in REQUIRED_CONSTANTS:
                try:
                    constants[target.id] = ast.literal_eval(node.value)
                except ValueError:
                    constants[target.id] = None
        if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            if node.target.id in REQUIRED_CONSTANTS and node.value is not None:
                try:
                    constants[node.target.id] = ast.literal_eval(node.value)
                except ValueError:
                    constants[node.target.id] = None

    scenarios = []
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name.startswith("test"):
            declared = constants.get("DECLARED_TOOLS")
            distinct = sorted({t for t in declared}) if isinstance(declared, list) else None
            scenarios.append({
                "function": node.name,
                "module_scenario_id": constants.get("SCENARIO_ID"),
                "declared_tools_distinct": len(distinct) if distinct is not None else None,
                "docstring_present": ast.get_docstring(node) is not None,
                "line": node.lineno,
            })
    return {"file": str(path), "constants_ok": all(c in constants for c in REQUIRED_CONSTANTS),
            "scenarios": scenarios}


def run(directory: Path, min_tools: int) -> tuple[list[dict], list[str]]:
    problems: list[str] = []
    reports = []
    for path in sorted(directory.rglob("test_*.py")):
        report = check_module(path)
        reports.append(report)
        if not report["constants_ok"]:
            problems.append(f"{path}: missing module constants {REQUIRED_CONSTANTS}")
        for scenario in report["scenarios"]:
            if not scenario["docstring_present"]:
                problems.append(f"{path}:{scenario['line']}: {scenario['function']} lacks docstring")
            count = scenario["declared_tools_distinct"]
            if count is None or count < min_tools:
                problems.append(
                    f"{path}:{scenario['line']}: {scenario['function']} declares "
                    f"{count} distinct tools (< {min_tools})")
    return reports, problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Static scenario structure check")
    parser.add_argument("directory", type=Path)
    parser.add_argument("--min-tools", type=int, default=10)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args(argv)

    reports, problems = run(args.directory, args.min_tools)
    payload = {"directory": str(args.directory), "min_tools": args.min_tools,
               "files": reports, "problems": problems}
    if args.out is not None:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")

    total = sum(len(r["scenarios"]) for r in reports)
    print(f"files: {len(reports)}; scenarios: {total}; problems: {len(problems)}")
    for problem in problems:
        print(f"PROBLEM: {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
