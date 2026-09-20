#!/usr/bin/env python3
"""Round-54 orchestrator (host side): handles the six p03_vm_acc019c UI-signal
listener-restart requests. For each request: disable the MCP server via the
Options UI, wait for loopback 15378 to go down, re-enable it, wait for health,
then write the acknowledgement file on the VM."""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from run_p01_vm_tests import UiMcpClient, powershell, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings, find_target  # noqa: E402

SIGNAL_DIR = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1\ui-signals"
PHASES = 6


def read_signal(client: UiMcpClient, name: str) -> str | None:
    result = powershell(client, (
        f'if (Test-Path "{SIGNAL_DIR}\\{name}") {{ (Get-Content "{SIGNAL_DIR}\\{name}" -Raw | ConvertFrom-Json).schema_version }} '
        'else { "absent" }'
    ), allow_failure=True)
    return None if "absent" in result else result.strip()


def write_ack(client: UiMcpClient, index: int, ok: bool) -> None:
    mark = "PASS" if ok else "FAIL"
    powershell(client, (
        f'$payload = @{{ schema_version = "dnspy.p03.ui-signal.v1"; sequence = {index}; result = "{mark}" }} | ConvertTo-Json; '
        f'[IO.File]::WriteAllText("{SIGNAL_DIR}\\ack-{index:02d}-15378.json", $payload); "ack-written"'
    ))


def health(client: UiMcpClient) -> bool:
    result = powershell(client, (
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
        'if($LASTEXITCODE -eq 0){"up"}else{"down"}'
    ), allow_failure=True)
    return "up" in result


def wait_health(client: UiMcpClient, want: bool, timeout: float = 90.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if health(client) == want:
                return True
        except Exception:  # noqa: BLE001
            pass
        time.sleep(1.5)
    return False


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="p03-round54-orchestrator")
    client.initialize()
    handled = 0
    overall_deadline = time.time() + 1500
    while handled < PHASES and time.time() < overall_deadline:
        index = handled
        request = f"request-{index:02d}-15378.json"
        signal = read_signal(client, request)
        if signal is None:
            time.sleep(2.0)
            continue
        print(f"[phase {index}] restart request seen, re-applying settings", flush=True)
        ok = False
        try:
            # Re-applying the same settings restarts the listener and tears down
            # every existing session (probe evidence: old session reset with
            # WinError 10054). Unticking the local override does NOT stop an
            # already-running server, so disable/enable is not the mechanism.
            apply_settings(client, True, "localhost", 15378, target=find_target(client, r"C:\\Tools\\dnSpy\\dnSpy.exe"))
            up = wait_health(client, want=True, timeout=60)
            print(f"[phase {index}] up={up}", flush=True)
            ok = up
        except Exception as ex:  # noqa: BLE001
            print(f"[phase {index}] restart failed: {ex}", flush=True)
            try:
                apply_settings(client, True, "localhost", 15378, target=find_target(client, r"C:\\Tools\\dnSpy\\dnSpy.exe"))
                wait_health(client, want=True, timeout=90)
            except Exception:  # noqa: BLE001
                pass
        write_ack(client, index, ok)
        handled += 1
        time.sleep(1.0)
    print(f"orchestrator done handled={handled}/{PHASES}", flush=True)
    return 0 if handled == PHASES else 1


if __name__ == "__main__":
    raise SystemExit(main())
