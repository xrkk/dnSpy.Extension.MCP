#!/usr/bin/env python3
"""Host-side, AI-operated Win10VM orchestration for the P02 acceptance contract."""

from __future__ import annotations

import argparse
import hashlib
import json
import time
import uuid
from pathlib import Path
from typing import Any

from run_p01_vm_tests import (UiMcpClient, powershell, read_vm_text, start_dnspy, upload)


ROOT = Path(__file__).resolve().parents[2]
VM_URL = "http://192.168.204.149:28787/mcp"
VM_ROOT = r"C:\Tools\dnspy-mcp-edit-tests\repo"
VM_SAMPLE_ROOT = r"C:\Tools\MefCheck\dnspy-mcp-p02-tests"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"
VM_ARTIFACTS = r"C:\Users\xxx\Desktop\dnspy-mcp-artifacts"


def deploy(client: UiMcpClient, dll: Path) -> str:
    powershell(client, (
        '$p=Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue; '
        'if($p){$p|Stop-Process -Force;$p|Wait-Process -ErrorAction SilentlyContinue}; '
        f'New-Item -ItemType Directory -Force -Path "{VM_ROOT}"|Out-Null'
    ))
    sources = [
        ROOT / "dnspy_mcp/__init__.py", ROOT / "dnspy_mcp/client.py",
        ROOT / "tests/edit/p02_vm_driver.py",
        ROOT / "PLAN/2026.09.03/P02-contract/dependency/dnlib-4.5.0-facts.json",
        ROOT / "PLAN/2026.09.03/P02-contract/generated/capacity-golden.json",
        ROOT / "PLAN/2026.09.03/P02-contract/generated/fault-golden.json",
        ROOT / "PLAN/2026.09.03/P02-contract/generated/operation-lowering.json",
        ROOT / "PLAN/2026.09.03/P02-contract/generated/requirements-map.json",
        ROOT / "dist/p01-fixtures/TestIL.dll",
    ]
    sources.extend(sorted((ROOT / "tests/edit/fixtures/bin").glob("x*/P02DynamicFixture.exe")))
    sources.extend(sorted((ROOT / "tests/edit/fixtures/bin").glob("x*/P02DynamicFixture.exe.config")))
    sources.extend(sorted((ROOT / "tests/edit/fixtures/bin").glob("x*/P02MixedModeFixture.exe")))
    sources.extend(sorted((ROOT / "tests/edit/fixtures/bin").glob("x*/P02DuplicateParamFixture.exe")))
    sources.extend(sorted((ROOT / "tests/edit/fixtures/bin").glob("x*/P02AttachmentFixture.dll")))
    sources.extend([
        ROOT / "tests/edit/fixtures/bin/P02ModuleFixture.netmodule",
        ROOT / "tests/edit/fixtures/bin/P02MultiFileFixture.dll",
    ])
    for source in sources:
        if source == ROOT / "dist/p01-fixtures/TestIL.dll":
            destination = VM_ROOT + r"\tests\fixtures\bin\TestIL.dll"
        elif (ROOT / "tests/edit/fixtures/bin") in source.parents:
            relative = source.relative_to(ROOT / "tests/edit/fixtures/bin").as_posix().replace("/", "\\")
            destination = VM_SAMPLE_ROOT + "\\" + relative
        else:
            relative = source.relative_to(ROOT).as_posix().replace("/", "\\")
            destination = VM_ROOT + "\\" + relative
        upload(client, source, destination)
    expected = hashlib.sha256(dll.read_bytes()).hexdigest().upper()
    staged = VM_EXTENSION + ".p02.new"
    upload(client, dll, staged)
    return powershell(client, (
        '$ErrorActionPreference="Stop"; '
        f'$expected="{expected}";$staged=(Get-FileHash "{staged}" -Algorithm SHA256).Hash;'
        'if($staged-ne$expected){throw "staged DLL SHA mismatch"};'
        f'Move-Item "{staged}" "{VM_EXTENSION}" -Force;'
        f'$actual=(Get-FileHash "{VM_EXTENSION}" -Algorithm SHA256).Hash;'
        'if($actual-ne$expected){throw "deployed DLL SHA mismatch"};'
        '[pscustomobject]@{sha256=$actual}|ConvertTo-Json -Compress'
    ))


def run_driver(client: UiMcpClient, architecture: str, label: str, arguments: list[str],
               timeout_seconds: int = 7200) -> dict[str, Any]:
    state = rf"C:\Tools\dnspy-mcp-edit-tests\state\p02-{architecture}-{label}-{uuid.uuid4().hex[:8]}"
    output = state + r"\result.json"
    stdout = state + r"\stdout.log"
    stderr = state + r"\stderr.log"
    fixture = VM_ROOT + r"\tests\fixtures\bin\TestIL.dll"
    dynamic = VM_SAMPLE_ROOT + rf"\{architecture}\P02DynamicFixture.exe"
    base = [
        VM_ROOT + r"\tests\edit\p02_vm_driver.py", "--url", "http://127.0.0.1:15378/",
        "--fixture", fixture, "--dynamic-fixture", dynamic, "--assembly", "TestIL",
        "--architecture", architecture, "--output", output,
    ] + arguments
    # Start-Process keeps redirected child handles attached to the PowerShell host on this
    # Win10VM MCP implementation, so the management call can time out even though the driver
    # continues correctly.  Win32_Process.Create is genuinely detached from that request.
    command_line = " ".join([r"C:\Python313\python.exe", *base])
    powershell(client, (
        f'New-Item -ItemType Directory -Force -Path "{state}"|Out-Null;'
        f'$cmd=\'{command_line} > {stdout} 2> {stderr}\';'
        "$created=([wmiclass]'Win32_Process').Create('cmd.exe /c '+$cmd);"
        'if($created.ReturnValue-ne 0){throw "driver process creation failed: $($created.ReturnValue)"};'
        '[pscustomobject]@{pid=$created.ProcessId}|ConvertTo-Json -Compress'
    ))
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        body = read_vm_text(client, output)
        if body.strip():
            result = json.loads(body)
            result["evidence_path"] = output
            if result.get("result") != "PASS":
                raise RuntimeError(f"{architecture}/{label} failed: {result.get('error')}")
            return result
        time.sleep(2)
    raise TimeoutError(f"{architecture}/{label} timed out; state={state}")


def run_architecture(client: UiMcpClient, architecture: str, include_faults: bool) -> dict[str, Any]:
    start_dnspy(client, architecture)
    rows: dict[str, Any] = {}
    facts = VM_ROOT + r"\PLAN\2026.09.03\P02-contract\dependency\dnlib-4.5.0-facts.json"
    rows["expanded"] = run_driver(client, architecture, "expanded", ["--only-expanded"])
    rows["operations"] = run_driver(client, architecture, "operations", ["--only-operation-matrix"])
    rows["semantic"] = run_driver(client, architecture, "semantic", ["--only-semantic-matrix", "--dnlib-facts", facts])
    rows["lifecycle"] = run_driver(client, architecture, "lifecycle", ["--only-lifecycle"])
    rows["review_guards"] = run_driver(client, architecture, "review-guards", ["--only-review-guards"])
    rows["capacity"] = run_driver(client, architecture, "capacity", ["--only-capacity-matrix"])
    mixed = VM_SAMPLE_ROOT + rf"\{architecture}\P02MixedModeFixture.exe"
    netmodule = VM_SAMPLE_ROOT + r"\P02ModuleFixture.netmodule"
    multifile = VM_SAMPLE_ROOT + r"\P02MultiFileFixture.dll"
    rows["unsupported"] = run_driver(client, architecture, "unsupported", [
        "--only-unsupported", "--unsupported", "mixed-mode=" + mixed,
        "--unsupported", "netmodule=" + netmodule, "--unsupported", "multi-file=" + multifile,
    ])
    if include_faults:
        # The mixed-mode fixture intentionally preserves the dynamic fixture's
        # assembly identity.  Start from an empty document tree so the fault
        # suite has exactly one strong target instead of two same-name modules.
        start_dnspy(client, architecture)
        rows["faults"] = run_driver(client, architecture, "faults", [
            "--only-fault-suite", "--fault-suite-id", f"p02-{architecture}-final",
            "--artifact-root", VM_ARTIFACTS,
        ], timeout_seconds=14400)
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--vm-url", default=VM_URL)
    parser.add_argument("--skip-deploy", action="store_true")
    parser.add_argument("--skip-faults", action="store_true")
    parser.add_argument("--architecture", choices=("x64", "x86", "both"), default="both")
    args = parser.parse_args()
    client = UiMcpClient.connect(args.vm_url, client_name="dnspy-p02-host-orchestrator", timeout=60)
    summary: dict[str, Any] = {"schema_version": "dnspy.p02.host-summary.v1", "architectures": {}}
    try:
        if not args.skip_deploy:
            summary["deployment"] = deploy(client, args.dll.resolve())
        architectures = ("x64", "x86") if args.architecture == "both" else (args.architecture,)
        for architecture in architectures:
            summary["architectures"][architecture] = run_architecture(
                client, architecture, not args.skip_faults)
        summary["result"] = "PASS"
    except Exception as exc:
        summary["result"] = "FAIL"
        summary["error"] = {"type": type(exc).__name__, "message": str(exc)}
        raise
    finally:
        summary["completed_unix"] = time.time()
        target = ROOT / "tests/edit/p02-vm-summary.json"
        target.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
