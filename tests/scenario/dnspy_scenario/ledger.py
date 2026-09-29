"""Scenario execution ledger: append-only JSONL writer with per-line schema checks.

Implements the frozen ledger spec (tests/scenario/SPEC-ledger.md, schema
dnspy.scenario.ledger.v1). Rows violating the schema are rejected before they
reach disk; the ledger itself never rewrites or truncates.
"""

from __future__ import annotations

import datetime
import json
import os
import threading
from pathlib import Path
from typing import Any, Mapping

SCHEMA_VERSION = "dnspy.scenario.ledger.v1"

KINDS = ("meta", "call", "assert", "reset", "result")
OUTCOMES = ("ok", "expected_error", "unexpected_error", "transport_error")
GRADES = ("strong", "weak")
VERDICTS = ("pass", "fail")
RESET_VERDICTS = ("pass", "blocked")
RESULT_OUTCOMES = ("pass", "fail", "blocked")
FAILURE_CLASSES = (
    "strong_assertion",
    "unexpected_error",
    "reset_blocked",
    "env_blocked",
    "collection_error",
)
# F-01: re-run batches must not add coverage cells to the matrix.
MATRIX_EXCLUDED_BATCH_PREFIXES = ("net10-sample", "net10-rerun-")


class LedgerSchemaError(ValueError):
    """A ledger row does not satisfy the frozen schema."""


def utc_now_iso() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds")


class LedgerWriter:
    """Append-only JSONL ledger for one scenario execution (one file per run)."""

    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._rows = 0

    @property
    def rows(self) -> int:
        return self._rows

    def write(self, record: Mapping[str, Any]) -> dict[str, Any]:
        row = validate_row(dict(record))
        line = json.dumps(row, ensure_ascii=False, separators=(",", ":"))
        with self._lock, self.path.open("a", encoding="utf-8") as fh:
            fh.write(line + "\n")
        self._rows += 1
        return row

    # Convenience builders -------------------------------------------------

    def meta(self, scenario_id: str, batch: str, declared_tools: list[str], tfm: str,
             docstring: str) -> dict[str, Any]:
        return self.write({
            "kind": "meta", "scenario_id": scenario_id, "batch": batch, "step_seq": 0,
            "declared_tools": list(declared_tools), "tfm": tfm, "docstring": docstring,
        })

    def call(self, scenario_id: str, batch: str, step_seq: int, tool: str,
             request: Mapping[str, Any], response_summary: Mapping[str, Any],
             duration_ms: int, outcome: str) -> dict[str, Any]:
        return self.write({
            "kind": "call", "scenario_id": scenario_id, "batch": batch,
            "step_seq": step_seq, "tool": tool, "request": dict(request),
            "response_summary": dict(response_summary), "duration_ms": duration_ms,
            "outcome": outcome,
        })

    def assertion(self, scenario_id: str, batch: str, step_seq: int, grade: str,
                  verdict: str, detail: str, *, expected_error: bool = False) -> dict[str, Any]:
        return self.write({
            "kind": "assert", "scenario_id": scenario_id, "batch": batch,
            "step_seq": step_seq, "grade": grade, "verdict": verdict,
            "expected_error": expected_error, "detail": detail[:200],
        })

    def reset(self, scenario_id: str, batch: str, check: str, verdict: str,
              detail: str) -> dict[str, Any]:
        return self.write({
            "kind": "reset", "scenario_id": scenario_id, "batch": batch,
            "step_seq": 0, "check": check, "verdict": verdict, "detail": detail[:200],
        })

    def result(self, scenario_id: str, batch: str, outcome: str,
               strong_asserts: int, weak_asserts: int, unexpected_errors: int,
               failure_class: str | None = None) -> dict[str, Any]:
        row: dict[str, Any] = {
            "kind": "result", "scenario_id": scenario_id, "batch": batch, "step_seq": 0,
            "outcome": outcome, "strong_asserts": strong_asserts,
            "weak_asserts": weak_asserts, "unexpected_errors": unexpected_errors,
        }
        if outcome != "pass":
            row["failure_class"] = failure_class or "collection_error"
        return self.write(row)


def _require(row: dict[str, Any], field: str, allowed=None, *, type_of=None) -> Any:
    if field not in row:
        raise LedgerSchemaError(f"missing field {field!r} in {row.get('kind')} row")
    value = row[field]
    if allowed is not None and value not in allowed:
        raise LedgerSchemaError(f"{field}={value!r} not in {list(allowed)}")
    if type_of is not None and not isinstance(value, type_of):
        raise LedgerSchemaError(f"{field} has type {type(value).__name__}, want {type_of.__name__}")
    return value


def validate_row(row: dict[str, Any]) -> dict[str, Any]:
    """Validate one candidate row; returns it with common fields filled in."""
    if row.get("schema_version") not in (None, SCHEMA_VERSION):
        raise LedgerSchemaError(f"schema_version {row.get('schema_version')!r} unsupported")
    row = dict(row)
    row["schema_version"] = SCHEMA_VERSION
    kind = _require(row, "kind", KINDS)
    _require(row, "scenario_id", type_of=str)
    _require(row, "batch", type_of=str)
    if "timestamp" not in row:
        row["timestamp"] = utc_now_iso()
    step = _require(row, "step_seq", type_of=int)
    if step < 0:
        raise LedgerSchemaError("step_seq must be >= 0")
    if kind == "meta":
        _require(row, "declared_tools", type_of=list)
        _require(row, "tfm", ("net48", "net10"))
        _require(row, "docstring", type_of=str)
    elif kind == "call":
        _require(row, "tool", type_of=str)
        _require(row, "request", type_of=dict)
        _require(row, "response_summary", type_of=dict)
        _require(row, "duration_ms", type_of=int)
        _require(row, "outcome", OUTCOMES)
        summary = row["response_summary"]
        if "is_error" not in summary:
            raise LedgerSchemaError("response_summary.is_error required")
        if summary.get("is_error") and row["outcome"] in ("ok",):
            raise LedgerSchemaError("is_error=true but outcome=ok")
    elif kind == "assert":
        _require(row, "grade", GRADES)
        _require(row, "verdict", VERDICTS)
        _require(row, "detail", type_of=str)
        if "expected_error" not in row:
            row["expected_error"] = False
    elif kind == "reset":
        _require(row, "check", type_of=str)
        _require(row, "verdict", RESET_VERDICTS)
        _require(row, "detail", type_of=str)
    elif kind == "result":
        _require(row, "outcome", RESULT_OUTCOMES)
        for field in ("strong_asserts", "weak_asserts", "unexpected_errors"):
            _require(row, field, type_of=int)
        if row["outcome"] != "pass":
            _require(row, "failure_class", FAILURE_CLASSES)
    return row


def read_ledger(path: str | os.PathLike[str]) -> list[dict[str, Any]]:
    """Read and validate a ledger file; raises LedgerSchemaError on any bad line."""
    rows: list[dict[str, Any]] = []
    with Path(path).open("r", encoding="utf-8") as fh:
        for lineno, line in enumerate(fh, 1):
            if not line.strip():
                continue
            try:
                parsed = json.loads(line)
            except json.JSONDecodeError as exc:
                raise LedgerSchemaError(f"{path}:{lineno}: invalid JSON ({exc})") from exc
            try:
                rows.append(validate_row(parsed))
            except LedgerSchemaError as exc:
                raise LedgerSchemaError(f"{path}:{lineno}: {exc}") from exc
    return rows
