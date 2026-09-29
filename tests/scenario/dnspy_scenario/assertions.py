"""Unified assertion API for scenario tests (REQ-008 / F-03).

Strong assertions: exact equality / set equality / count, applied to structured
fields only. Weak assertions: no isError plus structural validity (type
correctness). Every assertion writes an assert row into the ledger; a failing
strong assertion (or invalid weak assertion) raises AssertionError so pytest
marks the scenario failed.
"""

from __future__ import annotations

from typing import Any, Iterable, Mapping

from .ledger import LedgerWriter

__all__ = ["AssertionApi"]


class AssertionApi:
    """Bound to one scenario execution (ledger + ids)."""

    def __init__(self, ledger: LedgerWriter, scenario_id: str, batch: str) -> None:
        self._ledger = ledger
        self._scenario = scenario_id
        self._batch = batch
        self.strong_passed = 0
        self.strong_failed = 0
        self.weak_passed = 0
        self.weak_failed = 0

    # internals ------------------------------------------------------------

    def _record(self, step_seq: int, grade: str, verdict: str, detail: str,
                expected_error: bool = False) -> None:
        self._ledger.assertion(self._scenario, self._batch, step_seq, grade, verdict,
                               detail, expected_error=expected_error)
        counter = "strong" if grade == "strong" else "weak"
        attr = f"{counter}_{'passed' if verdict == 'pass' else 'failed'}"
        setattr(self, attr, getattr(self, attr) + 1)

    # strong ----------------------------------------------------------------

    def strong_equal(self, step_seq: int, actual: Any, expected: Any, label: str) -> None:
        """Exact equality on a structured value."""
        ok = actual == expected
        self._record(step_seq, "strong", "pass" if ok else "fail",
                     f"{label}: {actual!r} == {expected!r}")
        if not ok:
            raise AssertionError(f"strong_equal failed: {label}: {actual!r} != {expected!r}")

    def strong_set(self, step_seq: int, actual: Iterable[Any], expected: set[Any],
                   label: str) -> None:
        """Set equality: same distinct members regardless of order."""
        actual_set = set(actual)
        ok = actual_set == set(expected)
        detail = f"{label}: set({sorted(map(str, actual_set))}) == set({sorted(map(str, expected))})"
        self._record(step_seq, "strong", "pass" if ok else "fail", detail)
        if not ok:
            raise AssertionError(f"strong_set failed: {label}: {detail}")

    def strong_in_set(self, step_seq: int, member: Any, expected: set[Any], label: str) -> None:
        """Precise membership: member must be one of the expected set."""
        ok = member in set(expected)
        self._record(step_seq, "strong", "pass" if ok else "fail",
                     f"{label}: {member!r} in {sorted(map(str, expected))}")
        if not ok:
            raise AssertionError(f"strong_in_set failed: {label}: {member!r} not in expected")

    def strong_count(self, step_seq: int, actual_count: int, expected: int, label: str) -> None:
        ok = actual_count == expected
        self._record(step_seq, "strong", "pass" if ok else "fail",
                     f"{label}: count {actual_count} == {expected}")
        if not ok:
            raise AssertionError(f"strong_count failed: {label}: {actual_count} != {expected}")

    # weak ------------------------------------------------------------------

    def weak_ok(self, step_seq: int, value: Any, label: str) -> None:
        """Weak: value is truthy/successful (e.g. ok=true, non-error envelope)."""
        ok = bool(value)
        self._record(step_seq, "weak", "pass" if ok else "fail", f"{label}: truthy({bool(value)})")
        if not ok:
            raise AssertionError(f"weak_ok failed: {label}")

    def weak_fields(self, step_seq: int, obj: Mapping[str, Any], fields: Iterable[str],
                    label: str) -> None:
        """Weak: mapping has the expected fields with non-None values (schema shape)."""
        missing = [f for f in fields if f not in obj or obj[f] is None]
        ok = not missing
        self._record(step_seq, "weak", "pass" if ok else "fail",
                     f"{label}: fields {list(fields)} present" + (f", missing {missing}" if missing else ""))
        if not ok:
            raise AssertionError(f"weak_fields failed: {label}: missing {missing}")

    def weak_type(self, step_seq: int, value: Any, types: tuple[type, ...], label: str) -> None:
        ok = isinstance(value, types)
        self._record(step_seq, "weak", "pass" if ok else "fail",
                     f"{label}: type {type(value).__name__} in {[t.__name__ for t in types]}")
        if not ok:
            raise AssertionError(f"weak_type failed: {label}")
