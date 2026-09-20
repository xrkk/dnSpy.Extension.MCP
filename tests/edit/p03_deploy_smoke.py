#!/usr/bin/env python3
"""Round-48 deployment smoke: start dnSpy x64 with the current plugin, enable the
MCP server via the Options UI, then verify loopback health and tools/list."""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests" / "edit"))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

VM_URL = "http://192.168.204.240:28787/mcp"


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=60, client_name="p03-deploy-smoke")
    client.initialize()
    print("[1] starting dnSpy x64", flush=True)
    start_dnspy(client, "x64")
    print("[2] enabling MCP server (loopback)", flush=True)
    from ui_apply_settings import find_target
    target = find_target(client, r"C:\Tools\dnSpy\dnSpy.exe")
    apply_settings(client, None, "localhost", target=target)
    ready = powershell(client, (
        '$ready=$false; for($i=0;$i -lt 40;$i++){ '
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
        'if($LASTEXITCODE -eq 0){$ready=$true; break}; Start-Sleep -Milliseconds 500 }; '
        'if($ready){"health-ok"}else{"health-missing"}'
    ), timeout=60)
    print(f"[3] health: {ready.strip()}", flush=True)
    tools = powershell(client, (
        '$body = @{jsonrpc="2.0"; id=1; method="tools/list"; params=@{}} | ConvertTo-Json -Compress; '
        '$resp = & curl.exe -fsS --max-time 10 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" '
        '-d $body http://127.0.0.1:15378/mcp 2>$null; '
        'if($resp -match \'"name":"([^"]+)"\'){ ($Matches[0]) } else { $resp | Select-Object -First 1 }'
    ), timeout=60)
    print(f"[4] tools/list probe: {tools.strip()[:200]}", flush=True)
    count = powershell(client, (
        '$body = @{jsonrpc="2.0"; id=1; method="tools/list"; params=@{}} | ConvertTo-Json -Compress; '
        '$resp = & curl.exe -fsS --max-time 10 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" '
        '-d $body http://127.0.0.1:15378/mcp 2>$null; '
        '([regex]::Matches($resp, \'"name":"[a-z_]+"\' )).Count'
    ), timeout=60)
    print(f"[5] tool name occurrences: {count.strip()}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
