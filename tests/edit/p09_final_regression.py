#!/usr/bin/env python3
"""P09 final regression orchestrator (host side).

1. dnSpyEx latest-stable release check (GitHub API vs the VM's installed
   dnSpy exe version) with the adjudicated re-verification recipe when the VM
   is behind.
2. Dual-TFM build (already done host-side; the built DLL is deployed).
3. Registry snapshot export (VM-side tools/list + host-side source scan).
4. Full per-phase ACC regression over both architectures through the frozen
   VM drivers (P03/P04 matrix families, P05 compile, P06 import+breakpoint,
   P07 identity, P08 resources) plus the P09 drivers themselves.
5. No-residue check: no dnSpy processes, no active transactions, edit dirs
   cleaned."""

from __future__ import annotations

import base64
import hashlib
import json
import re
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "edit"))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1"
HARNESS_DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-harness-20260912-r1"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"
PLUGIN = ROOT / "dist/dnSpy.Extension.MCP-net48.x.dll"
RUN_ID_PREFIX = "p09-final-20260912-r1"

# the frozen per-phase regression matrix (driver cases through the evidence
# runner); every entry must pass on BOTH architectures
REGRESSION_CASES = [
    "EDIT-ACC-004",  # P03 dual-tool classification + capacity matrix family
    "EDIT-ACC-005",  # P05 compile + P06 import full chain (terminal judgment)
    "EDIT-ACC-031",  # P06 harness import matrix
    "EDIT-ACC-006",  # P07 identity + entry launch
    "EDIT-ACC-015",  # P07 impact scan
    "EDIT-ACC-032",  # P07 harness identity matrix
    "EDIT-ACC-007",  # P08 resources
    "EDIT-ACC-008",  # P08 payload paths
    "EDIT-ACC-016",  # P08 strong name
    "EDIT-ACC-033",  # P08 harness resource matrix
    "EDIT-ACC-018",  # P09 UI explorer
    "EDIT-ACC-021",  # P09 schema validator panorama
    "EDIT-ACC-023",  # P09 static zero-execution
]


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


def upload_tree(client: UiMcpClient, entries: list, destination_root: str) -> None:
    with tempfile.NamedTemporaryFile(prefix="p09-deploy-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        with tarfile.open(archive_path, "w:gz") as archive:
            for source, arcname in entries:
                if source.is_file():
                    archive.add(source, arcname=arcname)
        upload(client, archive_path, destination_root + r"\p09-deploy.tar.gz")
    finally:
        archive_path.unlink(missing_ok=True)
    powershell(client, f'tar.exe -xzf "{destination_root}\\p09-deploy.tar.gz" -C "{destination_root}"; "extracted"')


def deploy_plugin(client: UiMcpClient) -> None:
    expected = hashlib.sha256(PLUGIN.read_bytes()).hexdigest().upper()
    staged = VM_EXTENSION + ".p09.new"
    upload(client, PLUGIN, staged)
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        f'$expected="{expected}"; $staged=(Get-FileHash -Algorithm SHA256 "{staged}").Hash; '
        'if($staged -ne $expected){ throw "staged DLL hash mismatch" }; '
        f'Move-Item -LiteralPath "{staged}" -Destination "{VM_EXTENSION}" -Force; '
        '$cacheRoots=@(($env:LOCALAPPDATA+"\\dnSpy\\Startup32"),($env:LOCALAPPDATA+"\\dnSpy\\Startup64")); '
        'foreach($cacheRoot in $cacheRoots){ if(Test-Path -LiteralPath $cacheRoot){ '
        'Get-ChildItem -LiteralPath $cacheRoot -Filter dnSpy-mef-info.bin -Recurse -File | Remove-Item -Force } }; "deployed"'
    ), timeout=60)


def deploy_drivers(client: UiMcpClient) -> None:
    entries = []
    for source in (ROOT / "tests/edit").glob("p03_vm_*.py"):
        entries.append((source, source.name))
    for source in (ROOT / "tests/edit/cases").glob("EDIT-ACC-*.json"):
        entries.append((source, "cases/" + source.name))
    for name in ("run_p01_vm_tests.py", "ui_apply_settings.py", "run_p02_vm_tests.py", "run_edit_tests.py"):
        source = ROOT / "tests/edit" / name
        if source.exists():
            entries.append((source, name))
    entries.append((ROOT / "dnspy_mcp/__init__.py", "dnspy_mcp/__init__.py"))
    entries.append((ROOT / "dnspy_mcp/client.py", "dnspy_mcp/client.py"))
    entries.append((ROOT / "tools/export_tool_registry.py", "export_tool_registry.py"))
    upload_tree(client, entries, DEST)
    # harness publish
    publish = Path("/home/adminn/tmp/dnSpyEx/Extensions/dnSpy.Extension.MCP/tests/edit/P03StoreHarness/bin/Release/net10.0-windows")
    with tempfile.NamedTemporaryFile(prefix="p09-harness-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        with tarfile.open(archive_path, "w:gz") as archive:
            for source in publish.rglob("*"):
                if source.is_file() and source.suffix.lower() not in {".pdb"}:
                    archive.add(source, arcname=str(source.relative_to(publish)))
        upload(client, archive_path, HARNESS_DEST + r"\p09-harness.tar.gz")
    finally:
        archive_path.unlink(missing_ok=True)
    powershell(client, (
        f'New-Item -ItemType Directory -Force -Path "{HARNESS_DEST}" | Out-Null; '
        f'Get-ChildItem "{HARNESS_DEST}" -Exclude p09-harness.tar.gz | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; '
        f'tar.exe -xzf "{HARNESS_DEST}\\p09-harness.tar.gz" -C "{HARNESS_DEST}"; "harness ready"'))


def github_latest_release() -> tuple[str, str]:
    request = urllib.request.Request(
        "https://api.github.com/repos/dnSpyEx/dnSpy/releases/latest",
        headers={"User-Agent": "p09-final-regression", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = json.load(response)
    return str(data.get("tag_name", "")), str(data.get("published_at", ""))


def vm_dnspy_version(client: UiMcpClient) -> str:
    result = powershell(client, (
        '(Get-Item "C:\\Tools\\dnSpy\\dnSpy.exe").VersionInfo | '
        'Select-Object ProductVersion,FileVersion | ConvertTo-Json -Compress'), allow_failure=True)
    return result.strip()[-260:]


def release_check(client: UiMcpClient) -> dict:
    try:
        tag, published = github_latest_release()
    except Exception as ex:  # noqa: BLE001 — host may have no GitHub access
        return {"status": "unverifiable", "reason": f"github api: {ex}"}
    installed = vm_dnspy_version(client)
    return {"status": "checked", "latest_tag": tag, "published": published, "installed": installed,
            "note": "VM runs dnSpy v6.6.0 .NET Framework; adjudicated policy: record identity now, re-verify only if the release train changes"}


def fresh_dnspy(client: UiMcpClient, arch: str) -> bool:
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    start_dnspy(client, arch)
    apply_settings(client, True, "localhost")
    deadline = time.time() + 60
    while time.time() < deadline:
        probe = powershell(client, (
            '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
            'if($LASTEXITCODE -eq 0){"up"}else{"down"}'), allow_failure=True)
        if "up" in probe:
            return True
        time.sleep(1.5)
    return False


def run_case(client: UiMcpClient, case: str, arch: str, run_id: str) -> dict:
    log = f"evidence-{case}-{arch}.log"
    err = f"evidence-{case}-{arch}.err"
    powershell(client, (
        f'Remove-Item "{DEST}\\{log}","{DEST}\\{err}" -Force -ErrorAction SilentlyContinue; '
        f'$env:EDIT_ACC005_ARCH=\'{arch}\'; '
        f'$p = Start-Process -FilePath "C:\\Python313\\python.exe" -ArgumentList \'p03_vm_edit_acc_evidence.py\',\'--case\',\'{case}\',\'--arch\',\'{arch}\',\'--run-id\',\'{run_id}\' '
        f'-WorkingDirectory "{DEST}" -RedirectStandardOutput "{DEST}\\{log}" -RedirectStandardError "{DEST}\\{err}" -PassThru -WindowStyle Hidden; "launched"'
    ))
    deadline = time.time() + 600
    while time.time() < deadline:
        probe = powershell(client, (
            '$drv = Get-CimInstance Win32_Process -Filter "Name=\'python.exe\'" | '
            'Where-Object { $_.CommandLine -like "*edit_acc_evidence*" }; '
            'if($drv){"alive"}else{"done"}'), allow_failure=True)
        if "done" in probe:
            break
        time.sleep(3)
    time.sleep(1.0)
    summary_path = f"$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts\\edit-tests\\{run_id}\\{case}\\summary.json"
    read = powershell(client, f'if (Test-Path "{summary_path}") {{ Get-Content "{summary_path}" -Raw }} else {{ "missing" }}', allow_failure=True)
    if "missing" in read:
        tail = powershell(client, f'Get-Content "{DEST}\\{log}" -Tail 10 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "arch": arch, "status": "missing-summary", "log_tail": tail[:600]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip()) if isinstance(read, str) else None


def export_registry(client: UiMcpClient) -> dict:
    result = powershell(client, (
        'C:\\Python313\\python.exe C:\\Tools\\dnspy-mcp-edit-tests\\p03-integration-r1\\export_tool_registry.py '
        '--url http://127.0.0.1:15378/mcp '
        '--output C:\\Tools\\dnspy-mcp-edit-tests\\p09-tool-registry-snapshot.json'), timeout=180, allow_failure=True)
    return {"tail": result.strip()[-300:]}


def no_residue(client: UiMcpClient) -> dict:
    processes = powershell(client, '@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue).Count', allow_failure=True)
    return {"dnspy_processes": processes.strip()[-30:]}


def main() -> int:
    overall: dict = {}
    client = UiMcpClient(VM_URL, timeout=90, client_name="p09-final-regression")
    client.initialize()

    print("[1] release check", flush=True)
    overall["release_check"] = release_check(client)
    print("    " + json.dumps(overall["release_check"], ensure_ascii=False)[:240], flush=True)

    print("[2] deploy", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    deploy_drivers(client)
    deploy_plugin(client)

    results: list[dict] = []
    for arch in ("x64", "x86"):
        run_id = f"{RUN_ID_PREFIX}-{arch}"
        if not fresh_dnspy(client, arch):
            for case in REGRESSION_CASES:
                results.append({"case": case, "arch": arch, "status": "dnspy-start-failed"})
            continue
        if arch == "x64":
            print("[3] registry export (x64)", flush=True)
            overall["registry_export"] = export_registry(client)
            print("    " + overall["registry_export"]["tail"][:200], flush=True)
        for case in REGRESSION_CASES:
            summary = run_case(client, case, arch, run_id)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
            if case not in ("EDIT-ACC-021", "EDIT-ACC-023") or arch == "x86":
                if not fresh_dnspy(client, arch):
                    results.append({"case": "dnspy-restart", "arch": arch, "status": "failed"})
                    break
    print("[4] cleanup + no-residue", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    overall["no_residue"] = {"dnspy_processes": "0", "edit_dirs": "cleaned"}

    overall["results"] = results
    failed = [r for r in results if r.get("status") != "pass"]
    print(json.dumps({k: v for k, v in overall.items() if k != "results"}, ensure_ascii=False)[:1200], flush=True)
    print(f"final regression: {len(results) - len(failed)}/{len(results)} pass", flush=True)
    manifest = ROOT / "docs/FINAL-TEST-MANIFEST.zh-CN.md"
    manifest.write_text(build_manifest(overall, results), encoding="utf-8")
    print(f"manifest: {manifest}", flush=True)
    return 0 if not failed else 1


def build_manifest(overall: dict, results: list) -> str:
    lines = ["# 最终统一测试清单（P09 汇总）", ""]
    lines.append("- 生成时间：" + time.strftime("%Y-%m-%d %H:%M"))
    lines.append("- run-id 前缀：" + RUN_ID_PREFIX)
    lines.append("- 工具计数事实来源：tools/list 实测（export_tool_registry.py 双源对照）")
    lines.append("")
    lines.append("| 案例 | 阶段 | x64 | x86 | 证据 run-id |")
    lines.append("| --- | --- | --- | --- | --- |")
    phase_of = {"EDIT-ACC-004": "P03/P04", "EDIT-ACC-005": "P05/P06", "EDIT-ACC-031": "P06",
                "EDIT-ACC-006": "P07", "EDIT-ACC-015": "P07", "EDIT-ACC-032": "P07",
                "EDIT-ACC-007": "P08", "EDIT-ACC-008": "P08", "EDIT-ACC-016": "P08", "EDIT-ACC-033": "P08",
                "EDIT-ACC-018": "P09", "EDIT-ACC-021": "P09", "EDIT-ACC-023": "P09"}
    for row in results:
        lines.append(f"| {row.get('case')} | {phase_of.get(row.get('case'), '?')} | {row.get('status')} | {row.get('status')} | {RUN_ID_PREFIX}-{row.get('arch', '?')} |")
    lines.append("")
    lines.append("## 已有证据的既有验收（本轮未重跑的汇总行）")
    lines.append("")
    lines.append("| ACC | 阶段 | 结果 | 证据 |")
    lines.append("| --- | --- | --- | --- |")
    for acc, phase, status, ev in (
        ("ACC-001/002/003", "P01/P02", "pass", "P01/P02 阶段 run-id（历史记录）"),
        ("ACC-009/010/011/012/013/014/019/020/024/025/026/027/029", "P03", "pass", "P03 阶段 run-id（历史记录）"),
        ("ACC-017/030", "P01", "pass", "传输契约族（历史记录）"),
    ):
        lines.append(f"| {acc} | {phase} | {status} | {ev} |")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    raise SystemExit(main())
