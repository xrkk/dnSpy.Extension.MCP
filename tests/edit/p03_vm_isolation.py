"""Explicit, fail-closed execution context for P03 VM evidence drivers.

The context is deliberately passed to each driver through its
``configure_isolation`` function.  It does not inspect or mutate arbitrary
module globals, and it never creates directories, performs RPC, or starts a
process while a plan is being built.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path, PureWindowsPath
import os
import re
from urllib.parse import urlparse


class IsolationError(ValueError):
    """An isolation configuration is incomplete or points at a shared default."""


def _path(value: str):
    # Inspect Windows paths consistently even when generating plans on Linux.
    return PureWindowsPath(value) if PureWindowsPath(value).drive else Path(value)


def _require_path(name: str, value: str, *, writable: bool) -> str:
    if not value:
        raise IsolationError(f"{name} is required")
    path = _path(value)
    if not path.is_absolute() or ".." in path.parts:
        raise IsolationError(f"{name} must be absolute and contain no parent traversal")
    if writable:
        for shared in (r"C:\Tools\dnspy-mcp-edit-tests", r"C:\Tools\dnSpy"):
            if isinstance(path, PureWindowsPath) and path.is_relative_to(PureWindowsPath(shared)):
                raise IsolationError(f"{name} must not point at a shared C:\\Tools root")
    return str(path)


def _below(name: str, value: str, root: str) -> None:
    child, parent = _path(value), _path(root)
    if type(child) is not type(parent) or child == parent or not child.is_relative_to(parent):
        raise IsolationError(f"{name} must be below isolation_root")
    # On the execution platform also resolve existing symlinks/junctions.
    if os.name == "nt" or not isinstance(child, PureWindowsPath):
        real_child, real_parent = Path(value).resolve(), Path(root).resolve()
        if real_child == real_parent or not real_child.is_relative_to(real_parent):
            raise IsolationError(f"{name} resolves outside isolation_root")


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
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", self.run_id):
            raise IsolationError("run_id must be a single safe path component")
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
        root = _require_path("isolation_root", self.isolation_root, writable=True)
        _require_path("fixture_root", self.fixture_root, writable=False)
        for name in ("artifact_root", "checkpoint_store", "work_root"):
            value = _require_path(name, getattr(self, name), writable=True)
            _below(name, value, root)
        _require_path("harness_dir", self.harness_dir, writable=False)
        _require_path("dotnet_host", self.dotnet_host, writable=False)
        if require_ui:
            deployment = _require_path("ui_deployment_root", self.ui_deployment_root or "", writable=True)
            _below("ui_deployment_root", deployment, root)

    def fixture(self, relative: str) -> str:
        return str(_path(self.fixture_root) / relative)

    def fixture_output(self, relative: str) -> str:
        """Return a writable fixture child only when that root is isolated too."""
        self.validate()
        root = _require_path("isolation_root", self.isolation_root, writable=True)
        fixture_root = _require_path("fixture_root", self.fixture_root, writable=True)
        _below("fixture_root", fixture_root, root)
        parent = _path(fixture_root)
        child = PureWindowsPath(relative) if isinstance(parent, PureWindowsPath) else Path(relative)
        if child.is_absolute() or ".." in child.parts:
            raise IsolationError("fixture output must be relative and contain no parent traversal")
        output = str(parent / child)
        _below("fixture output", output, fixture_root)
        return output

    def work_file(self, name: str) -> str:
        return str(_path(self.work_root) / name)

    def plan(self, case_id: str, *, harness: bool, requires_ui: bool) -> dict[str, object]:
        self.validate(require_ui=requires_ui)
        writes = [self.artifact_root, self.checkpoint_store, self.work_root]
        cleanup = [self.artifact_root + "\\edit-tests\\" + self.run_id]
        if case_id in ("EDIT-ACC-005", "EDIT-ACC-006", "EDIT-ACC-016-CAUSAL"):
            launch_case = case_id.removeprefix("EDIT-").lower().replace("-", "")
            launch_root = self.fixture_output(
                f".{launch_case}-launch/{self.run_id}/{self.architecture}")
            writes.append(launch_root)
            cleanup.append(launch_root)
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
            "writes": writes,
            "cleanup": cleanup,
            "rpc": not harness,
            "subprocess": "P03StoreHarness.dll" if harness else None,
        }
