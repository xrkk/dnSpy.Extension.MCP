#!/usr/bin/env python3
"""AI-operated Win10VM deployment and x64/x86 P01 acceptance orchestration."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import sys
import tarfile
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests/edit"))

from ui_apply_settings import UiMcpClient, apply_settings  # noqa: E402

VM_URL = "http://192.168.204.240:28787/mcp"
VM_ROOT = r"C:\Tools\dnspy-mcp-edit-tests\repo"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"


def unwrap(value: Any) -> str:
    if isinstance(value, dict) and isinstance(value.get("result"), str):
        return value["result"]
    if isinstance(value, list):
        return "\n".join(item for item in value if isinstance(item, str))
    return json.dumps(value, ensure_ascii=False)


def powershell(client: UiMcpClient, command: str, timeout: int = 30, allow_failure: bool = False) -> str:
    result = unwrap(client.call_tool_json("PowerShell", {"command": command, "timeout": timeout}))
    match = re.search(r"Status Code:\s*(-?\d+)\s*$", result)
    if match and int(match.group(1)) != 0 and not allow_failure:
        raise RuntimeError(f"Win10VM PowerShell failed with {match.group(1)}: {result[:1000]}")
    return result


def read_vm_text(client: UiMcpClient, path: str) -> str:
    """Return FileSystem.read content without its human-facing path header."""
    body = unwrap(client.call_tool_json("FileSystem", {"mode": "read", "path": path}))
    if body.startswith("Error:"):
        return ""
    if body.startswith("File:"):
        _, separator, content = body.partition("\n")
        return content if separator else ""
    return body


def upload(client: UiMcpClient, source: Path, destination: str) -> None:
    encoded = base64.b64encode(source.read_bytes()).decode("ascii")
    client.call_tool_json("FileSystem", {
        "mode": "write", "path": destination + ".b64", "content": encoded,
        "overwrite": True, "encoding": "utf-8",
    })
    powershell(client, (
        f'$parent=Split-Path -Parent "{destination}"; New-Item -ItemType Directory -Force -Path $parent | Out-Null; '
        f'[IO.File]::WriteAllBytes("{destination}",[Convert]::FromBase64String('
        f'(Get-Content -LiteralPath "{destination}.b64" -Raw))); '
        f'Remove-Item -LiteralPath "{destination}.b64" -Force'
    ))


def deploy_tree(client: UiMcpClient, dll: Path) -> str:
    files = [
        ROOT / "dnspy_mcp/__init__.py",
        ROOT / "dnspy_mcp/client.py",
        ROOT / "tests/edit/run-edit-tests.ps1",
        ROOT / "tests/edit/run_case_detached.ps1",
        ROOT / "tests/edit/run_regression_detached.ps1",
        ROOT / "tests/edit/run_edit_tests.py",
        ROOT / "tests/edit/ui_apply_settings.py",
        ROOT / "tests/run-verify-local.sh",
        ROOT / "tests/TEST-PLAN.zh-CN.md",
        ROOT / ".github/workflows/verify.yml",
        ROOT / "tests/edit/fixtures/P01Fixture.cs",
        ROOT / "tests/edit/cases/EDIT-ACC-017.json",
        ROOT / "tests/edit/cases/EDIT-ACC-027.json",
        ROOT / "tests/edit/cases/EDIT-ACC-030.json",
    ]
    print("[deploy] stopping dnSpy and exact DLL-locking debugger processes", flush=True)
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        '$targets=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); '
        '$targets+=@(Get-Process P01Fixture-* -ErrorAction SilentlyContinue); '
        '$targets+=@(Get-Process x64dbg,x32dbg -ErrorAction SilentlyContinue | '
        'Where-Object MainWindowTitle -like "dnSpy.Extension.MCP.x.dll*"); '
        'if($targets.Count){ $targets | Stop-Process -Force; $targets | Wait-Process -Timeout 15 -ErrorAction Stop }; '
        f'New-Item -ItemType Directory -Force -Path "{VM_ROOT}" | Out-Null; '
        'Get-ChildItem C:\\Tools\\dnspy-mcp-edit-tests,C:\\Tools\\MefCheck -Filter P01Fixture-*.exe -File -ErrorAction SilentlyContinue | Remove-Item -Force'
    ), timeout=30)
    print("[deploy] uploading P01 driver and plugin", flush=True)
    for source in files:
        relative = source.relative_to(ROOT).as_posix().replace("/", "\\")
        upload(client, source, VM_ROOT + "\\" + relative)
    # Build this fixture on the host. The isolated Win10VM intentionally has no
    # NuGet access, and the framework csc.exe is too old for TestIL.cs syntax.
    upload(client, ROOT / "dist/p01-fixtures/TestIL.dll",
           VM_ROOT + r"\tests\fixtures\bin\TestIL.dll")

    with tempfile.NamedTemporaryFile(prefix="dnspy-p01-regression-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        with tarfile.open(archive_path, "w:gz") as archive:
            for relative_root in ("tests/debug", "tests/fixtures", "tests/snapshots", "dnspy_mcp"):
                source_root = ROOT / relative_root
                for source in source_root.rglob("*"):
                    relative = source.relative_to(ROOT)
                    if not source.is_file() or any(part in {"__pycache__", "bin", "obj", "results"}
                                                   for part in relative.parts):
                        continue
                    archive.add(source, arcname=relative.as_posix())
        remote_archive = VM_ROOT + r"\p01-regression.tar.gz"
        upload(client, archive_path, remote_archive)
    finally:
        archive_path.unlink(missing_ok=True)
    expected = hashlib.sha256(dll.read_bytes()).hexdigest().upper()
    staged = VM_EXTENSION + ".p01.new"
    upload(client, dll, staged)
    print("[deploy] verifying DLL identity and clearing generated dnSpy MEF caches", flush=True)
    result = powershell(client, (
        '$ErrorActionPreference="Stop"; '
        f'$expected="{expected}"; $staged=(Get-FileHash -Algorithm SHA256 "{staged}").Hash; '
        'if($staged -ne $expected){ throw "staged DLL hash mismatch: $staged != $expected" }; '
        f'Move-Item -LiteralPath "{staged}" -Destination "{VM_EXTENSION}" -Force; '
        f'$deployed=Get-FileHash -Algorithm SHA256 "{VM_EXTENSION}"; '
        'if($deployed.Hash -ne $expected){ throw "deployed DLL hash mismatch" }; '
        '$cacheRoots=@(($env:LOCALAPPDATA+"\\dnSpy\\Startup32"),($env:LOCALAPPDATA+"\\dnSpy\\Startup64")); '
        'foreach($cacheRoot in $cacheRoots){ if(Test-Path -LiteralPath $cacheRoot){ '
        'Get-ChildItem -LiteralPath $cacheRoot -Filter dnSpy-mef-info.bin -Recurse -File | Remove-Item -Force } }; '
        '$deployed | Select-Object Path,Hash | ConvertTo-Json -Compress'
    ))
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        f'tar.exe -xzf "{remote_archive}" -C "{VM_ROOT}"; '
        f'Remove-Item -LiteralPath "{remote_archive}" -Force; '
        f'New-Item -ItemType Directory -Force -Path "{VM_ROOT}\\bin\\Release\\net48" | Out-Null; '
        f'Copy-Item -LiteralPath "{VM_EXTENSION}" -Destination "{VM_ROOT}\\bin\\Release\\net48\\dnSpy.Extension.MCP.x.dll" -Force; '
        f'if(-not (Test-Path -LiteralPath "{VM_ROOT}\\tests\\fixtures\\bin\\TestIL.dll"))'
        '{ throw "uploaded TestIL fixture is missing" }; '
        f'git -C "{VM_ROOT}" init | Out-Null; '
        f'git -C "{VM_ROOT}" config user.email "p01-acceptance@invalid"; '
        f'git -C "{VM_ROOT}" config user.name "P01 Acceptance"; '
        f'git -C "{VM_ROOT}" add tests dnspy_mcp bin; '
        f'git -C "{VM_ROOT}" commit --allow-empty -m "P01 regression baseline" | Out-Null; '
        'if($LASTEXITCODE -ne 0){ throw "git baseline commit failed: $LASTEXITCODE" }'
    ), timeout=60)
    return result


def start_dnspy(client: UiMcpClient, architecture: str) -> None:
    executable = r"C:\Tools\dnSpy\dnSpy.exe" if architecture == "x64" else r"C:\Tools\dnSpy\dnSpy-x86.exe"
    powershell(client, (
        'Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Stop-Process -Force; '
        "$env:DNMCP_TEST='1'; "
        f'Start-Process -FilePath "{executable}" -ArgumentList "--dont-load-files" -WorkingDirectory "C:\\Tools\\dnSpy"; '
        'for($i=0;$i -lt 60;$i++){ $p=Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | '
        'Where-Object MainWindowHandle -ne 0 | Select-Object -First 1; if($p){ break }; Start-Sleep -Milliseconds 500 }; '
        'if(-not $p){ throw "dnSpy main window did not start" }; Write-Output "started"'
    ), timeout=60)


def configure_host(client: UiMcpClient, host: str) -> None:
    apply_settings(client, False, host)
    powershell(client, (
        'if("' + host + '" -eq "localhost"){ '
        '$ready=$false; for($i=0;$i -lt 40;$i++){ '
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; $curlExit=$LASTEXITCODE; '
        'if($curlExit -eq 0){ $ready=$true; break }; '
        'Start-Sleep -Milliseconds 500 }; '
        'if(-not $ready){ throw "loopback MCP did not start" } }'
    ), timeout=60)


def run_arch(client: UiMcpClient, architecture: str) -> dict[str, Any]:
    print(f"[{architecture}] starting dnSpy and applying loopback settings", flush=True)
    start_dnspy(client, architecture)
    configure_host(client, "localhost")
    rows: dict[str, Any] = {}
    for case_id in ("EDIT-ACC-017", "EDIT-ACC-027", "EDIT-ACC-030"):
        print(f"[{architecture}] {case_id} started", flush=True)
        state_root = rf"C:\Tools\dnspy-mcp-edit-tests\state\{architecture}-{case_id}-{uuid.uuid4().hex[:8]}"
        signal_root = state_root + r"\ui-signals"
        powershell(client, (
            f'New-Item -ItemType Directory -Force -Path "{state_root}" | Out-Null; '
            f'Start-Process powershell -WindowStyle Hidden -ArgumentList @("-NoProfile","-ExecutionPolicy","Bypass",'
            f'"-File","{VM_ROOT}\\tests\\edit\\run_case_detached.ps1","-Case","{case_id}",'
            f'"-RepoRoot","{VM_ROOT}","-StateRoot","{state_root}",'
            f'"-UiSignalDir","{signal_root}"); Write-Output "started"'
        ))
        result_path = state_root + r"\result.json"
        deadline = time.time() + 300
        row: dict[str, Any] | None = None
        next_signal = 1
        while time.time() < deadline:
            request_path = signal_root + rf"\request-{next_signal:02d}.json"
            request_body = read_vm_text(client, request_path)
            if request_body.strip():
                request = json.loads(request_body)
                try:
                    apply_settings(client, bool(request["enable"]), str(request.get("host", "")))
                    acknowledgement = {"result": "PASS", "sequence": next_signal}
                except Exception as exc:
                    acknowledgement = {
                        "result": "FAIL", "sequence": next_signal,
                        "error": {"type": type(exc).__name__, "message": str(exc)},
                    }
                client.call_tool_json("FileSystem", {
                    "mode": "write",
                    "path": signal_root + rf"\ack-{next_signal:02d}.json",
                    "content": json.dumps(acknowledgement),
                    "encoding": "utf-8",
                    "overwrite": True,
                })
                next_signal += 1
            try:
                body = read_vm_text(client, result_path)
                parsed = json.loads(body)
                if isinstance(parsed, dict) and "exit_code" in parsed:
                    row = parsed
                    break
            except Exception:
                pass
            time.sleep(1.0)
        if row is None:
            raise RuntimeError(f"{architecture}/{case_id} detached test timed out: {state_root}")
        rows[case_id] = row
        print(f"[{architecture}] {case_id} completed: {rows[case_id]}", flush=True)
    return rows


def run_detached(client: UiMcpClient, *, mode: str, architecture: str = "", case_id: str = "",
                 timeout_seconds: int = 600) -> dict[str, Any]:
    label = architecture or case_id
    state_root = rf"C:\Tools\dnspy-mcp-edit-tests\state\regression-{mode}-{label}-{uuid.uuid4().hex[:8]}"
    mode_arguments = f',"-Architecture","{architecture}"' if architecture else f',"-Case","{case_id}"'
    powershell(client, (
        f'New-Item -ItemType Directory -Force -Path "{state_root}" | Out-Null; '
        f'Start-Process powershell -WindowStyle Hidden -ArgumentList @("-NoProfile","-ExecutionPolicy","Bypass",'
        f'"-File","{VM_ROOT}\\tests\\edit\\run_regression_detached.ps1","-Mode","{mode}",'
        f'{mode_arguments.lstrip(",")},'
        f'"-RepoRoot","{VM_ROOT}","-StateRoot","{state_root}"); Write-Output "started"'
    ))
    result_path = state_root + r"\result.json"
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        try:
            body = read_vm_text(client, result_path)
            if body.strip():
                return json.loads(body)
        except Exception:
            pass
        time.sleep(1.0)
    raise RuntimeError(f"regression {mode}/{label} timed out: {state_root}")


def run_existing_regressions(client: UiMcpClient) -> dict[str, Any]:
    summary: dict[str, Any] = {"fixtures": {}, "debug": {}}
    powershell(client, (
        '$targets=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); '
        'if($targets.Count){ $targets | Stop-Process -Force; $targets | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue }; '
        'Write-Output "stopped"'
    ))
    for architecture in ("x64", "x86"):
        print(f"[regression] fixtures/{architecture} started", flush=True)
        # run-tests.ps1 intentionally exercises overwrite-save against its
        # input. Restore the pristine host-built fixture before each bitness so
        # x86 never inherits x64's on-disk patch.
        upload(client, ROOT / "dist/p01-fixtures/TestIL.dll",
               VM_ROOT + r"\tests\fixtures\bin\TestIL.dll")
        summary["fixtures"][architecture] = run_detached(
            client, mode="fixtures", architecture=architecture, timeout_seconds=600)
    for number in range(1, 37):
        case_id = f"ACC-{number:03d}"
        print(f"[regression] debug/{case_id} started", flush=True)
        summary["debug"][case_id] = run_detached(
            client, mode="debug", case_id=case_id, timeout_seconds=900)
    return summary


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", required=True, type=Path)
    parser.add_argument("--vm-url", default=VM_URL)
    parser.add_argument("--architectures", nargs="+", choices=("x64", "x86"), default=("x64", "x86"))
    parser.add_argument("--skip-regressions", action="store_true")
    args = parser.parse_args()
    if not args.dll.is_file():
        raise FileNotFoundError(args.dll)
    client = UiMcpClient(args.vm_url, timeout=60, client_name="dnspy-p01-vm-orchestrator")
    client.initialize()
    summary: dict[str, Any] = {"automation": "AI -> Win10VM MCP -> PowerShell/App/FileSystem -> dnSpy MCP", "architectures": {}}
    try:
        summary["deployed"] = deploy_tree(client, args.dll)
        for architecture in args.architectures:
            summary["architectures"][architecture] = run_arch(client, architecture)
        if not args.skip_regressions:
            summary["existing_regressions"] = run_existing_regressions(client)
    finally:
        try:
            configure_host(client, "192.168.204.240")
        except Exception:
            pass
        powershell(client, 'Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Stop-Process -Force; Write-Output "stopped"')
        client.call_tool_json("App", {"mode": "launch_executable", "executable": r"C:\Tools\dnSpy\dnSpy.exe",
                                      "args": [], "cwd": r"C:\Tools\dnSpy"})
        client.close()
    output = ROOT / "tests/edit/p01-vm-summary.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"summary": str(output), "architectures": summary["architectures"]}, ensure_ascii=False, indent=2))
    passes = []
    for rows in summary["architectures"].values():
        for row in rows.values():
            passes.append(isinstance(row, dict) and row.get("exit_code") == 0)
    regression_rows = [*summary.get("existing_regressions", {}).get("fixtures", {}).values(),
                       *summary.get("existing_regressions", {}).get("debug", {}).values()]
    regression_passes = [isinstance(row, dict) and row.get("exit_code") == 0 for row in regression_rows]
    expected_p01 = 3 * len(args.architectures)
    regressions_ok = args.skip_regressions or (len(regression_passes) == 38 and all(regression_passes))
    return 0 if len(passes) == expected_p01 and all(passes) and regressions_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
