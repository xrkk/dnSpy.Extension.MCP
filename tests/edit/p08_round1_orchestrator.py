#!/usr/bin/env python3
"""P08 round-1 packaging orchestrator (host side).

Deploys the P08 build and builds the fixtures ON THE VM:
  ResourceHost.dll  — standard .resources + a BinaryFormatter custom object
                      (sentinel type writing p08-sentinel.flag if ever
                      deserialized) + an icon group (RT_GROUP_ICON + 2 icons)
  StrongHost.exe    — fully strong-name-signed net48 exe + InboundStrong.exe
                      (an AssemblyRef carrying the strong public key)
Then runs the ACC-007/008/016 evidence and the headless resource matrix for
both architectures."""

from __future__ import annotations

import base64
import hashlib
import json
import sys
import tarfile
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1"
HARNESS_DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-harness-20260912-r1"
REPO_FIXTURES = r"C:\Tools\mcp-repo\tests\fixtures"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"
PLUGIN = ROOT / "dist/dnSpy.Extension.MCP-net48.x.dll"
DRIVER_CASES = ("EDIT-ACC-007", "EDIT-ACC-008", "EDIT-ACC-016")
HARNESS_CASES = ("EDIT-ACC-033",)
SENTINEL = r"C:\Tools\dnspy-mcp-edit-tests\p08-sentinel.flag"

FIXTURE_BUILDER = r'''
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Resources;
using System.Runtime.Serialization;

namespace P08Fx
{
    [Serializable]
    public class Ghost
    {
        public string Value = "payload";
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            File.WriteAllText(@"SENTINEL_PATH", "instantiated");
        }
    }
}
'@
$out = 'FIXTURES\bin\ResourceHost'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$strings = Join-Path $out 'Strings.resources'
$writer = New-Object System.Resources.ResourceWriter($strings)
$writer.AddResource('greeting', 'hello')
$writer.AddResource('number', 42)
$writer.AddResource('ratio', 2.5)
$writer.AddResource('enabled', $true)
$writer.AddResource('payload', [byte[]](1,2,3))
$ghost = New-Object P08Fx.Ghost
$ms = New-Object System.IO.MemoryStream
$bf = New-Object System.Runtime.Serialization.Formatters.Binary.BinaryFormatter
$bf.Serialize($ms, $ghost)
$writer.AddResource('ghost', $ms.ToArray())
$writer.Generate()
$writer.Close()
# icon resources: build a minimal .ico (one 1x1 icon) via System.Drawing
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(16,16)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::SteelBlue)
$g.Dispose()
$iconMs = New-Object System.IO.MemoryStream
$bmp.Save($iconMs, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $iconMs.ToArray()
# ICO container: header + one directory entry + png payload
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
$bw.Write([byte]16); $bw.Write([byte]16); $bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([uint16]1); $bw.Write([uint16]32)
$bw.Write([uint32]$png.Length); $bw.Write([uint32](6 + 16))
$bw.Write($png)
$bw.Close()
[IO.File]::WriteAllBytes((Join-Path $out 'app.ico'), $ico.ToArray())
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dll = Join-Path $out 'ResourceHost.dll'
$src = Join-Path 'FIXTURES' 'ResourceHost.cs'
& $csc @('/nologo','/target:library','/platform:anycpu','/optimize-',"`/resource:$strings,ResourceHost.Strings.resources","`/win32icon:$(Join-Path $out 'app.ico')","`/out:$dll",$src) | Out-Null
if (-not (Test-Path $dll)) { throw 'ResourceHost build failed' }
# strong-name fixture: generate an snk via RSACryptoServiceProvider, sign StrongHost, reference from InboundStrong
$sn = '${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\sn.exe'
& $sn -k 'FIXTURES\bin\p08.snk' | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$strong = 'FIXTURES\bin\StrongHost'
New-Item -ItemType Directory -Force -Path $strong | Out-Null
$src = 'FIXTURES\StrongHost.cs'
[IO.File]::WriteAllText($src, 'using System; class StrongHost { static int Main() { Console.WriteLine(""signed""); return 3; } }')
& $csc /nologo /target:exe /platform:anycpu /optimize- /keyfile:'FIXTURES\bin\p08.snk' /out:"$strong\StrongHost.exe" $src | Out-Null
$strong86 = 'FIXTURES\bin\StrongHost-x86'
New-Item -ItemType Directory -Force -Path $strong86 | Out-Null
& $csc /nologo /target:exe /platform:x86 /optimize- /keyfile:'FIXTURES\bin\p08.snk' /out:"$strong86\StrongHost.exe" $src | Out-Null
$inbSrc = Join-Path 'FIXTURES' 'InboundStrong.cs'
& $csc /nologo /target:exe /platform:anycpu /optimize- /r:"$strong\StrongHost.exe" /out:"$strong\InboundStrong.exe" $inbSrc | Out-Null
if (-not (Test-Path "$strong\StrongHost.exe")) { throw 'StrongHost build failed' }
'ready'
'''


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
    with tempfile.NamedTemporaryFile(prefix="p08-deploy-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        with tarfile.open(archive_path, "w:gz") as archive:
            for source, arcname in entries:
                if source.is_file():
                    archive.add(source, arcname=arcname)
        upload(client, archive_path, destination_root + r"\p08-deploy.tar.gz")
    finally:
        archive_path.unlink(missing_ok=True)
    powershell(client, f'tar.exe -xzf "{destination_root}\\p08-deploy.tar.gz" -C "{destination_root}"; "extracted"')


def deploy_plugin(client: UiMcpClient) -> str:
    expected = hashlib.sha256(PLUGIN.read_bytes()).hexdigest().upper()
    staged = VM_EXTENSION + ".p08.new"
    upload(client, PLUGIN, staged)
    return powershell(client, (
        '$ErrorActionPreference="Stop"; '
        f'$expected="{expected}"; $staged=(Get-FileHash -Algorithm SHA256 "{staged}").Hash; '
        'if($staged -ne $expected){ throw "staged DLL hash mismatch" }; '
        f'Move-Item -LiteralPath "{staged}" -Destination "{VM_EXTENSION}" -Force; '
        f'$deployed=(Get-FileHash -Algorithm SHA256 "{VM_EXTENSION}").Hash; '
        'if($deployed -ne $expected){ throw "deployed DLL hash mismatch" }; '
        '$cacheRoots=@(($env:LOCALAPPDATA+"\\dnSpy\\Startup32"),($env:LOCALAPPDATA+"\\dnSpy\\Startup64")); '
        'foreach($cacheRoot in $cacheRoots){ if(Test-Path -LiteralPath $cacheRoot){ '
        'Get-ChildItem -LiteralPath $cacheRoot -Filter dnSpy-mef-info.bin -Recurse -File | Remove-Item -Force } }; '
        '"deployed " + $deployed'
    ))


def deploy(client: UiMcpClient) -> None:
    print("[deploy] stopping dnSpy", flush=True)
    powershell(client, (
        '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); '
        'if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; '
        f'Remove-Item "{SENTINEL}" -Force -ErrorAction SilentlyContinue; "stopped"'))
    print("[deploy] integration drivers + cases + runner", flush=True)
    entries = [
        (ROOT / "tests/edit/p03_vm_edit_acc_evidence.py", "p03_vm_edit_acc_evidence.py"),
        (ROOT / "tests/edit/p03_vm_acc007.py", "p03_vm_acc007.py"),
        (ROOT / "tests/edit/p03_vm_acc008.py", "p03_vm_acc008.py"),
        (ROOT / "tests/edit/p03_vm_acc016.py", "p03_vm_acc016.py"),
        (ROOT / "tests/edit/run_p01_vm_tests.py", "run_p01_vm_tests.py"),
        (ROOT / "tests/edit/ui_apply_settings.py", "ui_apply_settings.py"),
        (ROOT / "tests/edit/cases/EDIT-ACC-007.json", "cases/EDIT-ACC-007.json"),
        (ROOT / "tests/edit/cases/EDIT-ACC-008.json", "cases/EDIT-ACC-008.json"),
        (ROOT / "tests/edit/cases/EDIT-ACC-016.json", "cases/EDIT-ACC-016.json"),
        (ROOT / "tests/edit/cases/EDIT-ACC-033.json", "cases/EDIT-ACC-033.json"),
        (ROOT / "dnspy_mcp/__init__.py", "dnspy_mcp/__init__.py"),
        (ROOT / "dnspy_mcp/client.py", "dnspy_mcp/client.py"),
    ]
    upload_tree(client, entries, DEST)
    print("[deploy] harness publish tree", flush=True)
    publish = Path("/home/adminn/tmp/dnSpyEx/Extensions/dnSpy.Extension.MCP/tests/edit/P03StoreHarness/bin/Release/net10.0-windows")
    with tempfile.NamedTemporaryFile(prefix="p08-harness-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        with tarfile.open(archive_path, "w:gz") as archive:
            for source in publish.rglob("*"):
                if source.is_file() and source.suffix.lower() not in {".pdb"}:
                    archive.add(source, arcname=str(source.relative_to(publish)))
        upload(client, archive_path, HARNESS_DEST + r"\p08-harness.tar.gz")
    finally:
        archive_path.unlink(missing_ok=True)
    powershell(client, (
        f'New-Item -ItemType Directory -Force -Path "{HARNESS_DEST}" | Out-Null; '
        f'Get-ChildItem "{HARNESS_DEST}" -Exclude p08-harness.tar.gz | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; '
        f'tar.exe -xzf "{HARNESS_DEST}\\p08-harness.tar.gz" -C "{HARNESS_DEST}"; "harness ready"'))
    print("[deploy] strong-name bypass enforcement", flush=True)
    registry = powershell(client, (
        'foreach ($hive in @("HKLM:\\SOFTWARE\\Microsoft\\.NETFramework", "HKLM:\\SOFTWARE\\Wow6432Node\\Microsoft\\.NETFramework")) { '
        'New-ItemProperty -Path $hive -Name EnableStrongNameBypass -Value 0 -PropertyType DWord -Force | Out-Null }; '
        '(Get-ItemProperty "HKLM:\\SOFTWARE\\Microsoft\\.NETFramework" -Name EnableStrongNameBypass).EnableStrongNameBypass'
    ), allow_failure=True)
    print(f"[deploy] bypass: {registry.strip()[-80:]}", flush=True)
    print("[deploy] VM fixtures (ResourceHost/StrongHost)", flush=True)
    script = (FIXTURE_BUILDER
              .replace("SENTINEL_PATH", SENTINEL)
              .replace("FIXTURES", REPO_FIXTURES))
    result = powershell(client, script, timeout=300, allow_failure=True)
    print(f"[deploy] fixtures: {result.strip()[-400:]}", flush=True)
    # verify sentinel-prone fixture exists
    listing = powershell(client, (
        f'Get-ChildItem "{REPO_FIXTURES}\\bin\\ResourceHost","{REPO_FIXTURES}\\bin\\StrongHost" -ErrorAction SilentlyContinue '
        '| Select-Object Name,Length | ConvertTo-Json -Compress'), allow_failure=True)
    print(f"[deploy] fixture listing: {listing.strip()[:300]}", flush=True)


def fresh_dnspy(client: UiMcpClient, arch: str) -> bool:
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    start_dnspy(client, arch)
    apply_settings(client, True, "localhost")
    deadline = time.time() + 60
    while time.time() < deadline:
        probe = powershell(client, (
            '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
            'if($LASTEXITCODE -eq 0){"up"}else{"down"}'
        ), allow_failure=True)
        if "up" in probe:
            return True
        time.sleep(1.5)
    return False


def run_case_on_vm(client: UiMcpClient, case: str, arch: str, run_id: str) -> dict | None:
    log = f"evidence-{case}-{arch}.log"
    err = f"evidence-{case}-{arch}.err"
    powershell(client, (
        f'Remove-Item "{DEST}\\{log}","{DEST}\\{err}" -Force -ErrorAction SilentlyContinue; '
        f'$env:EDIT_ACC005_ARCH=\'{arch}\'; '
        f'$p = Start-Process -FilePath "C:\\Python313\\python.exe" -ArgumentList \'p03_vm_edit_acc_evidence.py\',\'--case\',\'{case}\',\'--arch\',\'{arch}\',\'--run-id\',\'{run_id}\' '
        f'-WorkingDirectory "{DEST}" -RedirectStandardOutput "{DEST}\\{log}" -RedirectStandardError "{DEST}\\{err}" -PassThru -WindowStyle Hidden; "launched=$($p.Id)"'
    ))
    deadline = time.time() + 480
    while time.time() < deadline:
        probe = powershell(client, (
            '$drv = Get-CimInstance Win32_Process -Filter "Name=\'python.exe\'" | '
            'Where-Object { $_.CommandLine -like "*edit_acc_evidence*" }; '
            'if($drv){"alive"}else{"done"}'
        ), allow_failure=True)
        if "done" in probe:
            break
        time.sleep(3)
    time.sleep(1.0)
    summary_path = f"$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts\\edit-tests\\{run_id}\\{case}\\summary.json"
    read = powershell(client, f'if (Test-Path "{summary_path}") {{ Get-Content "{summary_path}" -Raw }} else {{ "missing" }}', allow_failure=True)
    if "missing" in read:
        tail = powershell(client, f'Get-Content "{DEST}\\{log}" -Tail 12 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "arch": arch, "status": "missing-summary", "log_tail": tail[:800]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip()) if isinstance(read, str) else None


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="p08-round1-orchestrator")
    client.initialize()
    deploy(client)
    results: list[dict] = []
    deployed_plugin = False
    for arch in ("x64", "x86"):
        run_id = f"p08-resource-20260912-r1-{arch}"
        if not deployed_plugin:
            print(f"[{arch}] deploying plugin", flush=True)
            deploy_plugin(client)
            deployed_plugin = True
        for case in HARNESS_CASES:
            summary = run_case_on_vm(client, case, arch, run_id)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
        for case in DRIVER_CASES:
            print(f"[{arch}] fresh dnSpy for {case}", flush=True)
            if not fresh_dnspy(client, arch):
                results.append({"case": case, "arch": arch, "status": "dnspy-start-failed"})
                continue
            summary = run_case_on_vm(client, case, arch, run_id)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    print(json.dumps({"results": results}, ensure_ascii=False, default=str)[:5000], flush=True)
    failed = [r for r in results if r.get("status") != "pass"]
    print(f"orchestrator done: {len(results) - len(failed)}/{len(results)} pass", flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
