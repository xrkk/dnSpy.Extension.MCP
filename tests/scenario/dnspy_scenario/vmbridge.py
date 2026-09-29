"""Client for the deployment-side helper (vm_bridge_server on the VM, IMP-205).

The Q-006 precondition allows reading VM sample hashes "via Win10VM MCP or a
deployment-side helper". The VM control MCP speaks a custom framing the generic
client cannot use, so P02 ships a minimal read-only stdlib HTTP bridge bound to
the host-only address: only /health, /hash and /list, restricted to the
scenario samples/artifacts roots. See SPEC-ledger §4b for the baseline files.
"""

from __future__ import annotations

import json
import urllib.request
from typing import Any

__all__ = ["VmBridgeError", "VmBridge"]


class VmBridgeError(RuntimeError):
    pass


class VmBridge:
    def __init__(self, base_url: str = "http://192.168.204.240:15110", timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def _post(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        req = urllib.request.Request(
            self.base_url + path,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", "replace")[:200]
            raise VmBridgeError(f"{path} -> HTTP {exc.code}: {body}") from exc
        except OSError as exc:
            raise VmBridgeError(f"{path} unreachable: {exc}") from exc

    def health(self) -> bool:
        req = urllib.request.Request(self.base_url + "/health", method="GET")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return resp.status == 200
        except OSError as exc:
            raise VmBridgeError(f"health unreachable: {exc}") from exc

    def hash_tree(self, root: str) -> dict[str, str]:
        """Recursive {relative_path: sha256} for every file under root."""
        data = self._post("/list", {"path": root})
        if not data.get("ok"):
            raise VmBridgeError(data.get("error", "list failed"))
        return {row["path"]: row["sha256"] for row in data["files"]}
