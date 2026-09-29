"""Recording proxy over dnspy_mcp.DnSpyClient (REQ-006 / Q-009).

Subclasses the client so every MCP tool call funnels through call_tool /
call_tool_json and lands in the ledger — including calls made through the
edit_* convenience helpers and direct `tools/call` via request_object. The
error path is recorded too: call_tool raises ToolCallError on isError, so the
recorder catches, extracts the domain error code (§4a.2), records, re-raises.
Expected errors must be announced BEFORE the call via the expect_error context
manager (§4a.1); the auto-written verification assertion is strong (§4a.4).
"""

from __future__ import annotations

import json
import time
from contextlib import contextmanager
from typing import Any, Iterator, Mapping

from dnspy_mcp import DnSpyClient, DnSpyConnectionError, DnSpyHttpError, DnSpyProtocolError, ToolCallError

from .ledger import LedgerWriter

__all__ = ["RecordingClient", "extract_error_code"]

TRANSPORT_ERRORS = (DnSpyConnectionError, DnSpyHttpError, DnSpyProtocolError, OSError)


def _head(text: str | None, limit: int = 200) -> str:
    if text is None:
        return ""
    return text[:limit]


def extract_error_code(text: str | None) -> tuple[str | None, str]:
    """Domain error code extraction (frozen §4a.2).

    ToolCallError carries the first text item; domain envelopes embed
    {"error": {"code": ...}} (P01 direct-call evidence). Returns
    (code, content_head); code is None when no JSON envelope is found.
    """
    if not text:
        return None, ""
    try:
        payload = json.loads(text)
    except (json.JSONDecodeError, ValueError):
        return None, _head(text)
    if isinstance(payload, dict):
        err = payload.get("error")
        if isinstance(err, dict) and isinstance(err.get("code"), str):
            return err["code"], _head(text)
        if isinstance(payload.get("code"), str):
            return payload["code"], _head(text)
    return None, _head(text)


class RecordingClient(DnSpyClient):
    """DnSpyClient subclass that records every tool call into the ledger."""

    def __init__(self, ledger: LedgerWriter, scenario_id: str, batch: str,
                 assert_api, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self._ledger = ledger
        self._scenario = scenario_id
        self._batch = batch
        self._asserts = assert_api
        self._step = 0
        self.unexpected_errors = 0
        self.expected_errors = 0
        self._predictions: dict[str, str | None] = {}
        self.closed_recorded = False
        self._in_recorded_call = False

    # ledger bookkeeping -----------------------------------------------------

    @property
    def step_seq(self) -> int:
        return self._step

    def _next_step(self) -> int:
        self._step += 1
        return self._step

    @contextmanager
    def expect_error(self, tool: str, code: str | None = None) -> Iterator[None]:
        """Announce an expected error BEFORE the call (§4a.1). Verification is strong (§4a.4)."""
        if tool in self._predictions:
            raise RuntimeError(f"pending expect_error for {tool!r} already exists")
        self._predictions[tool] = code
        try:
            yield
        finally:
            self._predictions.pop(tool, None)

    # recording call paths -----------------------------------------------------

    def _record_call(self, tool: str, arguments: Mapping[str, Any], outcome: str,
                     is_error: bool, code: str | None, head: str, duration_ms: int,
                     step: int) -> None:
        self._ledger.call(
            self._scenario, self._batch, step, tool, dict(arguments),
            {"is_error": is_error, "error_code": code, "content_head": head},
            duration_ms, outcome,
        )

    def _handle_tool_error(self, tool: str, arguments: Mapping[str, Any], exc: ToolCallError,
                           step: int, duration_ms: int) -> None:
        code, head = extract_error_code(str(exc))
        if tool in self._predictions:
            expected_code = self._predictions.get(tool)
            hit = expected_code is None or expected_code == code
            self._ledger.assertion(
                self._scenario, self._batch, step, "strong",
                "pass" if hit else "fail",
                f"expect_error({tool}): code {code!r} ~ {expected_code!r}",
                expected_error=True,
            )
            if hit:
                self.expected_errors += 1
                self._record_call(tool, arguments, "expected_error", True, code, head,
                                  duration_ms, step)
                return
            # wrong error code: strong assertion failure
            self._asserts.strong_failed += 1
            self._record_call(tool, arguments, "unexpected_error", True, code, head,
                              duration_ms, step)
            raise AssertionError(
                f"expected error {expected_code!r} from {tool!r}, got {code!r}") from exc
        self.unexpected_errors += 1
        self._record_call(tool, arguments, "unexpected_error", True, code, head,
                          duration_ms, step)
        raise exc

    def call_tool(self, name: str, arguments: Mapping[str, Any] | None = None) -> Mapping[str, Any]:
        args = dict(arguments or {})
        step = self._next_step()
        started = time.monotonic()
        self._in_recorded_call = True
        try:
            result = super().call_tool(name, args)
        except ToolCallError as exc:
            self._in_recorded_call = False
            self._handle_tool_error(name, args, exc, step, int((time.monotonic() - started) * 1000))
            raise  # unreachable: _handle_tool_error either returns or raises
        except TRANSPORT_ERRORS as exc:
            self._in_recorded_call = False
            self._record_call(name, args, "transport_error", True, None,
                              _head(f"{type(exc).__name__}: {exc}"),
                              int((time.monotonic() - started) * 1000), step)
            raise
        finally:
            self._in_recorded_call = False
        duration_ms = int((time.monotonic() - started) * 1000)
        head = self._result_head(result)
        self._record_call(name, args, "ok", False, None, head, duration_ms, step)
        return result

    @staticmethod
    def _result_head(result: Any) -> str:
        """Prefer structuredContent; fall back to the first text item."""
        if isinstance(result, dict):
            if "structuredContent" in result:
                return _head(json.dumps(result["structuredContent"], ensure_ascii=False))
            text = None
            for item in result.get("content") or []:
                if isinstance(item, dict) and item.get("type") == "text":
                    text = item.get("text")
                    break
            return _head(text)
        return _head(str(result))

    def call_tool_json(self, name: str, arguments: Mapping[str, Any] | None = None) -> Any:
        """Delegates to self.call_tool — recording happens exactly once there.

        edit_* convenience helpers funnel through here, so every tool call in
        the scenario layer produces exactly one call row.
        """
        return super().call_tool_json(name, arguments)

    # Close the bypass through raw request paths: any direct tools/call made
    # OUTSIDE the recorded call paths is funneled through call_tool (guarded
    # against re-entry by _in_recorded_call, since super().call_tool itself
    # goes through request_object).

    def request_object(
        self,
        message: Mapping[str, Any],
        *,
        include_session: bool = True,
        timeout: float | None = None,
    ):
        if (not self._in_recorded_call and isinstance(message, Mapping)
                and message.get("method") == "tools/call"):
            params = message.get("params") or {}
            return self.call_tool(params.get("name", "<unnamed>"),
                                  params.get("arguments") or {})
        return super().request_object(message, include_session=include_session, timeout=timeout)

    # session lifecycle -------------------------------------------------------

    def close(self):  # type: ignore[override]
        try:
            response = super().close()
        except Exception:
            # A transport-level hiccup on the DELETE (observed post-commit:
            # empty 202 body) must not leave the session lifecycle unrecorded.
            response = None
        self.closed_recorded = True
        return response
