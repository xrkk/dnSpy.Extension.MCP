"""Explicit, fail-closed execution context for P03 VM evidence drivers.

The context is deliberately passed to each driver through its
``configure_isolation`` function.  It does not inspect or mutate arbitrary
module globals, and it never creates directories, performs RPC, or starts a
process while a plan is being built.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import PurePath
from urllib.parse import urlparse


class IsolationError(ValueError):
    """An isolation configuration is incomplete or points at a shared default."""


_SHARED_MARKERS = ("c:\\tools\\dnspy-mcp-edit-tests", "c:\\tools\\dnspy", "15378")


def _require_path(name: str, value: str, *, writable: bool) -> str:
    if not value:
        raise IsolationError(f"{name} is required")
    normalized = value.replace("/", "\\").rstrip("\\").lower()
    if writable and any(marker in normalized for marker in _SHARED_MARKERS[:2]):
        raise IsolationError(f"{name} must not point at a shared C:\\Tools root")
    return value.rstrip("\\/")


@dataclass(frozen=True)
class IsolationContext:
    run_id: str
    architecture: str
    mcp_url: str
    isolation_root: str
    fixture_root: str
    artifact_root: str
    checkpoint_store: str
    work_root: str
    harness_dir: str
    dotnet_host: str
    ui_deployment_root: str | None = None

    def validate(self, *, require_ui: bool = False) -> None:
        if not self.run_id or any(ch.isspace() for ch in self.run_id):
            raise IsolationError("run_id must be a non-empty whitespace-free value")
        if self.architecture not in ("x64", "x86"):
            raise IsolationError("architecture must be x64 or x86")
        parsed = urlparse(self.mcp_url)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise IsolationError("mcp_url must be an absolute HTTP(S) URL")
        try:
            port = parsed.port
        except ValueError as ex:
            raise IsolationError("mcp_url has an invalid port") from ex
        if port == 15378:
            raise IsolationError("mcp_url must not use the shared default port 15378 in isolation mode")
        root = _require_path("isolation_root", self.isolation_root, writable=True).replace("/", "\\").lower()
        _require_path("fixture_root", self.fixture_root, writable=False)
        for name in ("artifact_root", "checkpoint_store", "work_root"):
            value = _require_path(name, getattr(self, name), writable=True)
            if not value.replace("/", "\\").lower().startswith(root + "\\"):
                raise IsolationError(f"{name} must be below isolation_root")
        _require_path("harness_dir", self.harness_dir, writable=False)
        _require_path("dotnet_host", self.dotnet_host, writable=False)
        if require_ui:
            deployment = _require_path("ui_deployment_root", self.ui_deployment_root or "", writable=True)
            if not deployment.replace("/", "\\").lower().startswith(root + "\\"):
                raise IsolationError("ui_deployment_root must be below isolation_root")

    def fixture(self, relative: str) -> str:
        return str(PurePath(self.fixture_root) / PurePath(relative))

    def work_file(self, name: str) -> str:
        return str(PurePath(self.work_root) / PurePath(name))

    def plan(self, case_id: str, *, harness: bool, requires_ui: bool) -> dict[str, object]:
        self.validate(require_ui=requires_ui)
        return {
            "case_id": case_id,
            "architecture": self.architecture,
            "mcp_url": self.mcp_url,
            "fixture_root": self.fixture_root,
            "isolation_root": self.isolation_root,
            "artifact_root": self.artifact_root,
            "checkpoint_store": self.checkpoint_store,
            "work_root": self.work_root,
            "harness_dir": self.harness_dir if harness else None,
            "dotnet_host": self.dotnet_host if harness else None,
            "ui_deployment_root": self.ui_deployment_root if requires_ui else None,
            "writes": [self.artifact_root, self.checkpoint_store, self.work_root],
            "cleanup": [self.artifact_root + "\\edit-tests\\" + self.run_id],
            "rpc": not harness,
            "subprocess": "P03StoreHarness.dll" if harness else None,
        }
