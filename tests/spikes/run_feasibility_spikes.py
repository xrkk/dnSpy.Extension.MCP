#!/usr/bin/env python3
"""Deploy and run S01/S02 through Win10VM MCP without human VM interaction."""

from __future__ import annotations

import base64
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from win10vm_mcp import Win10VmClient  # noqa: E402


VM_URL = "http://192.168.204.149:28787/mcp"
VM_STAGE = r"C:\dnspy-mcp-spikes"
VM_EVIDENCE = r"C:\dnspy-mcp-artifacts\spikes\2026-08-31"
VM_EXTENSIONS = r"C:\Tools\dnSpy\bin\Extensions"
SPIKE_DLL = ROOT / "tests/spikes/dnspy-feasibility/bin/Release/net48/dnSpy.Feasibility.Spike.x.dll"
FIXTURES = {
    "x64": ROOT / "tests/spikes/dnspy-feasibility/Fixture/bin/x64/Release/net48/SpikeFixture.exe",
    "x86": ROOT / "tests/spikes/dnspy-feasibility/Fixture/bin/x86/Release/net48/SpikeFixture.exe",
}
HOST_EVIDENCE = ROOT / "PLAN/2026.08.31/技术Spike/raw"


def unwrap(value: Any) -> str:
    if isinstance(value, dict) and isinstance(value.get("result"), str):
        return value["result"]
    return json.dumps(value, ensure_ascii=False)


def call(client: Win10VmClient, tool: str, arguments: dict[str, Any]) -> Any:
    return client.call_tool_json(tool, arguments)


def powershell(client: Win10VmClient, command: str, timeout: int = 30) -> str:
    return unwrap(call(client, "PowerShell", {"command": command, "timeout": timeout}))


def upload_base64(client: Win10VmClient, source: Path, destination: str) -> None:
    encoded = base64.b64encode(source.read_bytes()).decode("ascii")
    call(client, "FileSystem", {
        "mode": "write",
        "path": destination + ".b64",
        "content": encoded,
        "overwrite": True,
        "encoding": "utf-8",
    })


def wait_and_read(client: Win10VmClient) -> str:
    command = (
        f'$done="{VM_EVIDENCE}\\DONE"; '
        'for($i=0;$i -lt 150;$i++){ if(Test-Path -LiteralPath $done){ break }; Start-Sleep -Seconds 1 }; '
        'if(-not (Test-Path -LiteralPath $done)){ throw "Spike timed out" }; '
        f'Get-Content -LiteralPath "{VM_EVIDENCE}\\evidence.json" -Raw'
    )
    response = powershell(client, command, timeout=180)
    marker = "Response: "
    status = "\n\nStatus Code:"
    start = response.find(marker)
    end = response.rfind(status)
    if start < 0 or end < 0:
        raise RuntimeError("Unexpected PowerShell response: " + response[:500])
    return response[start + len(marker):end].strip()


def run_arch(client: Win10VmClient, executable: str, tag: str) -> dict[str, Any]:
    powershell(client, (
        'Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Stop-Process -Force; '
        f'Remove-Item -LiteralPath "{VM_EVIDENCE}\\DONE","{VM_EVIDENCE}\\evidence.json" -Force -ErrorAction SilentlyContinue; '
        f'Copy-Item -LiteralPath "{VM_STAGE}\\SpikeFixture-{tag}.exe" -Destination "{VM_STAGE}\\SpikeFixture.exe" -Force'
    ))
    call(client, "App", {
        "mode": "launch_executable",
        "executable": executable,
        "args": [],
        "cwd": r"C:\Tools\dnSpy",
    })
    evidence_text = wait_and_read(client)
    evidence = json.loads(evidence_text)
    powershell(client, (
        f'Copy-Item -LiteralPath "{VM_EVIDENCE}\\s02-imported-embedded.exe" '
        f'-Destination "{VM_EVIDENCE}\\s02-imported-embedded-{tag}.exe" -Force'
    ))
    HOST_EVIDENCE.mkdir(parents=True, exist_ok=True)
    (HOST_EVIDENCE / f"{tag}-evidence.json").write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return evidence


def main() -> int:
    for path in (SPIKE_DLL, *FIXTURES.values()):
        if not path.is_file():
            raise FileNotFoundError(path)
    client = Win10VmClient(VM_URL, timeout=240, client_name="dnspy-feasibility-spike")
    client.initialize()
    results: dict[str, Any] = {}
    deployed = f"{VM_EXTENSIONS}\\dnSpy.Feasibility.Spike.x.dll"
    try:
        powershell(client, (
            'Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Stop-Process -Force; '
            f'New-Item -ItemType Directory -Force -Path "{VM_STAGE}","{VM_EVIDENCE}" | Out-Null'
        ))
        upload_base64(client, SPIKE_DLL, f"{VM_STAGE}\\dnSpy.Feasibility.Spike.x.dll")
        for tag, fixture in FIXTURES.items():
            upload_base64(client, fixture, f"{VM_STAGE}\\SpikeFixture-{tag}.exe")
        powershell(client, (
            f'[IO.File]::WriteAllBytes("{VM_STAGE}\\dnSpy.Feasibility.Spike.x.dll",'
            f'[Convert]::FromBase64String((Get-Content -LiteralPath "{VM_STAGE}\\dnSpy.Feasibility.Spike.x.dll.b64" -Raw))); '
            f'Copy-Item -LiteralPath "{VM_STAGE}\\dnSpy.Feasibility.Spike.x.dll" -Destination "{deployed}" -Force; '
            f'Remove-Item -LiteralPath "{VM_STAGE}\\dnSpy.Feasibility.Spike.x.dll.b64" -Force; '
            f'Get-FileHash -Algorithm SHA256 "{deployed}" | '
            'Select-Object Path,Hash | ConvertTo-Json'
        ))
        for tag in FIXTURES:
            powershell(client, (
                f'[IO.File]::WriteAllBytes("{VM_STAGE}\\SpikeFixture-{tag}.exe",'
                f'[Convert]::FromBase64String((Get-Content -LiteralPath "{VM_STAGE}\\SpikeFixture-{tag}.exe.b64" -Raw))); '
                f'Remove-Item -LiteralPath "{VM_STAGE}\\SpikeFixture-{tag}.exe.b64" -Force'
            ))
        results["x64"] = run_arch(client, r"C:\Tools\dnSpy\dnSpy.exe", "x64")
        results["x86"] = run_arch(client, r"C:\Tools\dnSpy\dnSpy-x86.exe", "x86")
    finally:
        powershell(client, (
            '$p=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); '
            'if($p.Count -gt 0){ $p | Stop-Process -Force; $p | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue }'
        ))
        call(client, "FileSystem", {"mode": "delete", "path": deployed})
        powershell(client, f'if(Test-Path -LiteralPath "{deployed}"){{ throw "Spike DLL cleanup failed" }}')
        call(client, "App", {
            "mode": "launch_executable",
            "executable": r"C:\Tools\dnSpy\dnSpy.exe",
            "args": [],
            "cwd": r"C:\Tools\dnSpy",
        })
        client.close()
    HOST_EVIDENCE.mkdir(parents=True, exist_ok=True)
    summary = {
        "format": "dnspy.mcp.spike.host-summary.v1",
        "automation": "AI -> Win10VM MCP -> PowerShell/App/FileSystem -> real dnSpy x64/x86",
        "x64_pass": bool(results.get("x64", {}).get("s01", {}).get("pass")) and bool(results.get("x64", {}).get("s02", {}).get("pass")),
        "x86_pass": bool(results.get("x86", {}).get("s01", {}).get("pass")) and bool(results.get("x86", {}).get("s02", {}).get("pass")),
        "results": results,
    }
    (HOST_EVIDENCE / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps({
        "x64_s01": results.get("x64", {}).get("s01", {}).get("pass", False),
        "x64_s02": results.get("x64", {}).get("s02", {}).get("pass", False),
        "x86_s01": results.get("x86", {}).get("s01", {}).get("pass", False),
        "x86_s02": results.get("x86", {}).get("s02", {}).get("pass", False),
        "evidence": str(HOST_EVIDENCE),
    }, ensure_ascii=False, indent=2))
    return 0 if summary["x64_pass"] and summary["x86_pass"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
