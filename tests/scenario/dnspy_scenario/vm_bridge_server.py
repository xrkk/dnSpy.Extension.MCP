"""Deployment-side read-only file bridge for the scenario reset routine.

Runs on the .240 VM under E:\\dnspy-scenario\\bridge\\, bound to the host-only
address so only the Linux host (192.168.204.1) can reach it. Read-only:
/health (GET), /hash and /list (POST), restricted to the scenario samples and
artifacts roots. Stdlib only — no third-party dependencies on the VM.

Launch (VM, via the VM control MCP):
    Set-Content E:\dnspy-scenario\bridge\vm_bridge_server.py <this file>
    Start-Process C:\Python313\python.exe E:\dnspy-scenario\bridge\vm_bridge_server.py
Undo: stop the process and delete E:\dnspy-scenario\bridge\\.
"""

from __future__ import annotations

import hashlib
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

BIND_HOST = "192.168.204.240"
BIND_PORT = 15110
ALLOWED_ROOTS = (r"E:\dnspy-scenario\samples", r"E:\dnspy-scenario\artifacts")


def _allowed(path: str) -> Path:
    resolved = Path(path)
    for root in ALLOWED_ROOTS:
        root_path = Path(root)
        try:
            resolved.relative_to(root_path)
            return resolved
        except ValueError:
            continue
    raise PermissionError(f"path outside allowed roots: {path}")


def _hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def list_tree(root: str) -> list[dict]:
    base = _allowed(root)
    rows = []
    if not base.exists():
        raise FileNotFoundError(f"root missing: {root}")
    for item in sorted(base.rglob("*")):
        if item.is_file():
            rows.append({
                "path": item.relative_to(base).as_posix(),
                "size": item.stat().st_size,
                "sha256": _hash_file(item),
            })
    return rows


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send_json(self, status: int, payload: dict) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._send_json(200, {"status": "ok", "service": "dnspy-scenario-bridge"})
        else:
            self._send_json(404, {"ok": False, "error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length) or b"{}")
        except (ValueError, json.JSONDecodeError):
            self._send_json(400, {"ok": False, "error": "invalid JSON body"})
            return
        if self.path == "/hash":
            try:
                path = _allowed(str(payload.get("path", "")))
                self._send_json(200, {"ok": True, "path": str(path), "sha256": _hash_file(path)})
            except (PermissionError, FileNotFoundError, OSError) as exc:
                self._send_json(400, {"ok": False, "error": str(exc)})
            return
        if self.path == "/list":
            try:
                self._send_json(200, {"ok": True, "root": str(payload.get("path", "")),
                                      "files": list_tree(str(payload.get("path", "")))})
            except (PermissionError, FileNotFoundError, OSError) as exc:
                self._send_json(400, {"ok": False, "error": str(exc)})
            return
        self._send_json(404, {"ok": False, "error": "not found"})

    def log_message(self, fmt: str, *args) -> None:  # quiet
        sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))


def main() -> int:
    server = ThreadingHTTPServer((BIND_HOST, BIND_PORT), Handler)
    print(f"bridge listening on http://{BIND_HOST}:{BIND_PORT}/ (roots: {ALLOWED_ROOTS})",
          flush=True)
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
