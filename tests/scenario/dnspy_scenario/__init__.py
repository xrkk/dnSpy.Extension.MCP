"""Scenario-chain test infrastructure for dnSpy-MCP (P02 deliverable).

Components: ledger (JSONL execution ledger), recorder (recording proxy over
dnspy_mcp), assertions (strong/weak unified API), reset (standard reset
routine), vmbridge (deployment-side helper client), aggregator (coverage
matrix CLI), static_check (scenario structure CLI). Specs: tests/scenario/
SPEC-ledger.md and SPEC-authoring.md.
"""

from .assertions import AssertionApi
from .ledger import LedgerSchemaError, LedgerWriter, SCHEMA_VERSION
from .recorder import RecordingClient
from .reset import ResetBlocked, ResetContext, run_standard_reset
from .vmbridge import VmBridge, VmBridgeError

__all__ = [
    "AssertionApi", "LedgerSchemaError", "LedgerWriter", "SCHEMA_VERSION",
    "RecordingClient", "ResetBlocked", "ResetContext", "run_standard_reset",
    "VmBridge", "VmBridgeError",
]
