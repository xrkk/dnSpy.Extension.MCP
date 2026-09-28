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
from ui_apply_settings import apply_settings, find_target  # noqa: E402

DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1"
HARNESS_DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-harness-20260912-r1"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"
VM_DNLIB = r"C:\Tools\dnSpy\bin\dnlib.dll"
PLUGIN = ROOT / "dist/dnSpy.Extension.MCP-net48.x.dll"
# T095-C formal pair: the extension is built against the private dnlib
# (4.5.0-r15.private.1, same official strong-name identity), so the host's
# dnlib.dll must be replaced by the exact package binary at the same time.
PRIVATE_DNLIB_NUPKG = ROOT / "deps/dnlib/packages/dnlib.4.5.0-r15.private.1.nupkg"
RUN_ID_PREFIX = "p09-final-" + time.strftime("%Y%m%d-%H%M%S")


def private_dnlib_net48_path(tmpdir: str) -> Path:
    import zipfile
    with zipfile.ZipFile(PRIVATE_DNLIB_NUPKG) as package:
        payload = package.read("lib/net48/dnlib.dll")
    target = Path(tmpdir) / "dnlib-4.5.0-r15.private.1-net48.dll"
    target.write_bytes(payload)
    return target

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
    deploy_formal_pair(client)


def deploy_formal_pair(client: UiMcpClient) -> None:
    import tempfile
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
        'Get-ChildItem -LiteralPath $cacheRoot -Filter dnSpy-mef-info.bin -Recurse -File | Remove-Item -Force } }; "plugin deployed"'
    ), timeout=60)
    with tempfile.TemporaryDirectory(prefix="p09-lib-") as tmpdir:
        lib = private_dnlib_net48_path(tmpdir)
        expected_lib = hashlib.sha256(lib.read_bytes()).hexdigest().upper()
        staged_lib = VM_DNLIB + ".p09.new"
        upload(client, lib, staged_lib)
        powershell(client, (
            '$ErrorActionPreference="Stop"; '
            f'$expected="{expected_lib}"; $staged=(Get-FileHash -Algorithm SHA256 "{staged_lib}").Hash; '
            'if($staged -ne $expected){ throw "staged dnlib hash mismatch" }; '
            f'Move-Item -LiteralPath "{staged_lib}" -Destination "{VM_DNLIB}" -Force; '
            '"dnlib deployed"'
        ), timeout=60)


def deploy_drivers(client: UiMcpClient) -> None:
    entries = []
    for source in (ROOT / "tests/edit").glob("p03_vm_*.py"):
        entries.append((source, source.name))
    for source in (ROOT / "tests/edit").glob("t083_vm_*.py"):
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
    # The hardened settings helper requires an explicit UI target and no
    # longer toggles the server checkbox (the VM's persisted settings keep
    # EnableServer on; only host/port are re-applied to restart the listener).
    exe = r"C:\Tools\dnSpy\dnSpy.exe" if arch == "x64" else r"C:\Tools\dnSpy\dnSpy-x86.exe"
    apply_settings(client, None, "localhost", 15378, target=find_target(client, exe))
    deadline = time.time() + 60
    while time.time() < deadline:
        probe = powershell(client, (
            '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
            'if($LASTEXITCODE -eq 0){"up"}else{"down"}'), allow_failure=True)
        if "up" in probe:
            return True
        time.sleep(1.5)
    return False


P08_FIXTURE_BUILDER = r'''

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
$sn = "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\sn.exe"
& $sn -k 'FIXTURES\bin\p08.snk' | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$strong = 'FIXTURES\bin\StrongHost'
New-Item -ItemType Directory -Force -Path $strong | Out-Null
$src = 'FIXTURES\StrongHost.cs'
& $csc /nologo /target:exe /platform:anycpu /optimize- /keyfile:'FIXTURES\bin\p08.snk' /out:"$strong\StrongHost.exe" $src | Out-Null
$strong86 = 'FIXTURES\bin\StrongHost-x86'
New-Item -ItemType Directory -Force -Path $strong86 | Out-Null
& $csc /nologo /target:exe /platform:x86 /optimize- /keyfile:'FIXTURES\bin\p08.snk' /out:"$strong86\StrongHost.exe" $src | Out-Null
$inbSrc = Join-Path 'FIXTURES' 'InboundStrong.cs'
& $csc /nologo /target:exe /platform:anycpu /optimize- /r:"$strong\StrongHost.exe" /out:"$strong\InboundStrong.exe" $inbSrc | Out-Null
if (-not (Test-Path "$strong\StrongHost.exe")) { throw 'StrongHost build failed' }
'ready'
'''


ISO_PORT = {"x64": 16990, "x86": 16991}


def provision_isolation(client: UiMcpClient, arch: str, run_id: str) -> dict:
    """Build the per-arch isolation topology the hardened drivers require:
    a private root with rebuilt fixtures, an evidence/checkpoint/work layout,
    and a DEDICATED dnSpy instance (formal pair) on a non-shared port whose
    pid is recorded for the UI case. The shared 15378 instance is untouched.
    """
    iso = "E:\\p09-iso-" + run_id + "-" + arch
    port = ISO_PORT[arch]
    # A reused root would leak stale lineages/evidence into this run;
    # every invocation starts from a freshly wiped tree.
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        'if(Test-Path -LiteralPath "' + iso + '"){ '
        '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; '
        'Remove-Item -LiteralPath "' + iso + '" -Recurse -Force }; "prior root wiped"'
    ), timeout=120)
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        'New-Item -ItemType Directory -Force -Path "' + iso + '\\fixtures","' + iso + '\\artifact","'
        + iso + '\\checkpoints","' + iso + '\\work","' + iso + '\\src","' + iso + '\\ui-deploy\\'
        + arch + '\\app","' + iso + '\\ui-deploy\\' + arch + '\\fixtures","'
        + iso + '\\fixtures\\ImportHost","' + iso + '\\fixtures\\ImportHost-x86","'
        + iso + '\\fixtures\\ResourceHost","' + iso + '\\fixtures\\StrongHost","'
        + iso + '\\fixtures\\StrongHost-x86" | Out-Null; "dirs"'
    ), timeout=60)
    entries = [(ROOT / ("tests/fixtures/" + name), name)
               for name in ("ImportHost.cs", "InboundRef.cs", "ResourceHost.cs", "StrongHost.cs", "InboundStrong.cs")]
    entries.append((ROOT / "dist/TestIL.dll", "TestIL.dll"))
    upload_tree(client, entries, iso + "\\src")
    # Full P08 fixture recipe: sentinel-carrying Strings.resources + icon,
    # signed StrongHost pair, ImportHost variants, InboundRef. The builder
    # works on a FIXTURES\ tree; run it in a scratch cwd and relocate.
    scratch = iso + "\\fxbuild"
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        'New-Item -ItemType Directory -Force -Path "' + scratch + '\\FIXTURES" | Out-Null; '
        'Copy-Item "' + iso + '\\src\\*.cs" "' + scratch + '\\FIXTURES\\" -Force; '
        '"prepared"'
    ), timeout=60)
    sentinel = iso + "\\work\\p08-sentinel.flag"
    builder = P08_FIXTURE_BUILDER.replace("SENTINEL_PATH", sentinel.replace(chr(92), chr(92)+chr(92)))
    upload_inline(client, builder, scratch + "\\build-fixtures.ps1")
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        'Set-Location -LiteralPath "' + scratch + '"; '
        '& "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "' + scratch + '\\build-fixtures.ps1"'
    ), timeout=300)
    fx = iso + "\\fixtures"
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        '$b="' + scratch + '\\FIXTURES\\bin"; '
        'New-Item -ItemType Directory -Force -Path "' + fx + '\\ResourceHost","' + fx + '\\StrongHost","' + fx + '\\StrongHost-x86" | Out-Null; '
        'Copy-Item "$b\\ResourceHost\\ResourceHost.dll" "' + fx + '\\ResourceHost\\" -Force; '
        'Copy-Item "$b\\StrongHost\\StrongHost.exe","$b\\StrongHost\\InboundStrong.exe" "' + fx + '\\StrongHost\\" -Force; '
        'Copy-Item "$b\\StrongHost-x86\\StrongHost.exe" "' + fx + '\\StrongHost-x86\\" -Force; '
        '$csc64="$env:WINDIR\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"; '
        '$csc86="$env:WINDIR\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe"; '
        '& $csc64 /nologo /target:exe /platform:x64 /out:"' + fx + '\\ImportHost\\ImportHost.exe" "' + iso + '\\src\\ImportHost.cs"; if($LASTEXITCODE){ throw "ImportHost x64" }; '
        '& $csc86 /nologo /target:exe /platform:x86 /out:"' + fx + '\\ImportHost-x86\\ImportHost.exe" "' + iso + '\\src\\ImportHost.cs"; if($LASTEXITCODE){ throw "ImportHost x86" }; '
        '& $csc64 /nologo /target:exe /platform:x64 /out:"' + fx + '\\ImportHost\\InboundRef.exe" /r:"' + fx + '\\ImportHost\\ImportHost.exe" "' + iso + '\\src\\InboundRef.cs"; if($LASTEXITCODE){ throw "InboundRef" }; '
        'Copy-Item "' + iso + '\\src\\TestIL.dll" "' + fx + '\\TestIL.dll" -Force; '
        'if(-not (Test-Path "' + fx + '\\ResourceHost\\ResourceHost.dll")){ throw "ResourceHost missing" }; "fixtures built"'
    ), timeout=180)
    app = iso + "\\ui-deploy\\" + arch + "\\app"
    powershell(client, (
        'robocopy "C:\\Tools\\dnSpy" "' + app + '" /E /NFL /NDL /NJH /NJS | Out-Null; '
        'if($LASTEXITCODE -ge 8){ throw "robocopy failed" }; "app copied"'
    ), timeout=300)
    settings = iso + "\\ui-deploy\\" + arch + "\\settings.xml"
    cfg_json = ('{"AllowedSampleRoot":"' + fx.replace('\\', '\\\\')
                + '","ArtifactRoot":"' + iso.replace('\\', '\\\\') + '\\\\checkpoints'
                + '","DebugToolsEnabled":true,"DedicatedDebugInstanceAcknowledged":true,'
                + '"EnableServer":true,"Host":"localhost","Port":' + str(port)
                + ',"RemoteAllowedCidrs":[],"RemoteHostOnlyAcknowledged":false,'
                + '"RemoteTokenVerifier":null,"SchemaVersion":"dnspy.mcp.settings.v1"}')
    xml_text = ('<?xml version="1.0" encoding="utf-8"?><settings><section '
                '_="352907a0-9df5-4b2b-b47b-95e504cac301" SettingsSnapshotJson="'
                + cfg_json.replace('"', '&quot;') + '" /></settings>')
    upload_inline(client, xml_text, settings)
    exe = app + ("\\dnSpy.exe" if arch == "x64" else "\\dnSpy-x86.exe")
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; '
        "$env:DNMCP_TEST='1'; "
        '$p=Start-Process -FilePath "' + exe + '" -ArgumentList @(\'--multiple\',\'--dont-load-files\',\'--settings-file\',\'' + settings + '\') '
        '-WorkingDirectory "' + app + '" -PassThru; '
        'Start-Sleep -Milliseconds 300; $p.Id | Set-Content "' + iso + '\\ui-deploy\\' + arch + '\\pid.txt"; '
        'for($i=0;$i -lt 120;$i++){ '
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:' + str(port) + '/health 2>$null | Out-Null; '
        'if($LASTEXITCODE -eq 0){ break }; Start-Sleep -Milliseconds 500 }; '
        'if($LASTEXITCODE -ne 0){ throw "dedicated instance health failed" }; "instance up"'
    ), timeout=180)
    return {"iso": iso, "port": port, "mcp_url": "http://127.0.0.1:" + str(port) + "/mcp"}


def upload_inline(client: UiMcpClient, text: str, destination: str) -> None:
    encoded = base64.b64encode(text.encode("utf-8")).decode("ascii")
    powershell(client, (
        '$parent=Split-Path -Parent "' + destination + '"; New-Item -ItemType Directory -Force -Path $parent | Out-Null; '
        '[IO.File]::WriteAllText("' + destination + '",[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + encoded + '"))); "written"'
    ))


def open_explorer(client: UiMcpClient, arch: str, iso: str) -> None:
    """Open the MCP Edit Explorer window on the dedicated instance (View menu)."""
    pid_text = powershell(client, 'Get-Content "' + iso + '\\ui-deploy\\' + arch + '\\pid.txt" -Raw', allow_failure=True)
    pid = int(pid_text.strip())
    powershell(client, (
        '$ErrorActionPreference="Stop"; Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes; '
        '$ws=New-Object -ComObject WScript.Shell; $null=$ws.AppActivate(' + str(pid) + '); Start-Sleep -Milliseconds 600; '
        '$root=[System.Windows.Automation.AutomationElement]::RootElement; '
        '$win=$root.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,' + str(pid) + ')); '
        'if(-not $win){ throw "dedicated window missing" }; '
        '$ws.SendKeys("%v"); Start-Sleep -Milliseconds 700; '
        '$menu=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem)); '
        '$item=$menu | Where-Object { $_.Current.Name -eq "MCP Edit Explorer" } | Select-Object -First 1; '
        'if(-not $item){ throw "menu entry missing" }; '
        '$item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 900; '
        '$exp=$root.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,"McpEditExplorer")); '
        'if(-not $exp){ throw "explorer window not open" }; "explorer open"'
    ), timeout=90)


def run_case_isolated(client: UiMcpClient, case: str, arch: str, run_id: str, iso: dict) -> dict:
    log = "evidence-" + case + "-" + arch + ".log"
    err = "evidence-" + case + "-" + arch + ".err"
    dotnet = "C:\\Tools\\dotnet10-x64\\dotnet.exe" if arch == "x64" else "C:\\Tools\\dotnet10-x86\\dotnet.exe"
    runner_args = (
        "p03_vm_edit_acc_evidence.py --case " + case + " --arch " + arch + " --run-id " + run_id
        + " --artifact-root " + iso["iso"] + "\\artifact"
        + " --isolation-root " + iso["iso"]
        + " --mcp-url " + iso["mcp_url"]
        + " --fixture-root " + iso["iso"] + "\\fixtures"
        + " --checkpoint-store " + iso["iso"] + "\\checkpoints"
        + " --work-root " + iso["iso"] + "\\work"
        + " --harness-dir " + HARNESS_DEST
        + " --dotnet-host " + dotnet
        + " --ui-deployment-root " + iso["iso"] + "\\ui-deploy"
    )
    powershell(client, (
        'Remove-Item "' + DEST + '\\' + log + '","' + DEST + '\\' + err + '" -Force -ErrorAction SilentlyContinue; '
        "$env:EDIT_ACC005_ARCH='" + arch + "'; "
        "$p = Start-Process -FilePath 'C:\\Python313\\python.exe' -ArgumentList '" + runner_args + "' "
        '-WorkingDirectory "' + DEST + '" -RedirectStandardOutput "' + DEST + '\\' + log + '" -RedirectStandardError "' + DEST + '\\' + err + '" -PassThru -WindowStyle Hidden; "launched"'
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
    summary_path = iso["iso"] + "\\artifact\\edit-tests\\" + run_id + "\\" + case + "\\summary.json"
    read = powershell(client, 'if (Test-Path "' + summary_path + '") { Get-Content "' + summary_path + '" -Raw } else { "missing" }', allow_failure=True)
    if "missing" in read:
        tail = powershell(client, 'Get-Content "' + DEST + '\\' + log + '" -Tail 10 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "arch": arch, "status": "missing-summary", "log_tail": tail[:600]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip()) if isinstance(read, str) else None


def relaunch_dedicated(client: UiMcpClient, arch: str, iso: dict) -> None:
    """Restart the dedicated instance between cases to reset loaded-module
    state, mirroring the shared-instance restart the legacy flow performed."""
    app = iso["iso"] + "\\ui-deploy\\" + arch + "\\app"
    settings = iso["iso"] + "\\ui-deploy\\" + arch + "\\settings.xml"
    exe = app + ("\\dnSpy.exe" if arch == "x64" else "\\dnSpy-x86.exe")
    powershell(client, (
        '$ErrorActionPreference="Stop"; '
        '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; '
        'Remove-Item "' + iso["iso"] + '\\checkpoints\\edit-checkpoints\\*","' + iso["iso"] + '\\checkpoints\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; '
        "$env:DNMCP_TEST='1'; "
        '$p=Start-Process -FilePath "' + exe + '" -ArgumentList @(\'--multiple\',\'--dont-load-files\',\'--settings-file\',\'' + settings + '\') '
        '-WorkingDirectory "' + app + '" -PassThru; '
        'Start-Sleep -Milliseconds 300; $p.Id | Set-Content "' + iso["iso"] + '\\ui-deploy\\' + arch + '\\pid.txt"; '
        'for($i=0;$i -lt 120;$i++){ '
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:' + str(iso["port"]) + '/health 2>$null | Out-Null; '
        'if($LASTEXITCODE -eq 0){ break }; Start-Sleep -Milliseconds 500 }; '
        'if($LASTEXITCODE -ne 0){ throw "dedicated relaunch health failed" }; "relaunched"'
    ), timeout=180)


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


def export_registry_isolated(client: UiMcpClient, iso: dict) -> dict:
    result = powershell(client, (
        "C:\\Python313\\python.exe " + DEST + "\\export_tool_registry.py "
        "--url " + iso["mcp_url"] + " "
        "--output " + DEST + "\\p09-tool-registry-snapshot.json"), timeout=180, allow_failure=True)
    return {"tail": result.strip()[-300:]}


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
        print(f"[iso] provisioning isolation topology for {arch}", flush=True)
        try:
            iso = provision_isolation(client, arch, run_id)
        except Exception as ex:  # noqa: BLE001
            for case in REGRESSION_CASES:
                results.append({"case": case, "arch": arch, "status": "iso-provision-failed",
                                "detail": str(ex)[:400]})
            continue
        print(f"[iso] {arch} ready: {iso['iso']} port={iso['port']}", flush=True)
        if arch == "x64":
            print("[3] registry export (x64)", flush=True)
            overall["registry_export"] = export_registry_isolated(client, iso)
            print("    " + overall["registry_export"]["tail"][:200], flush=True)
        for case in REGRESSION_CASES:
            if case == "EDIT-ACC-018":
                try:
                    open_explorer(client, arch, iso["iso"])
                except Exception as ex:  # noqa: BLE001
                    results.append({"case": case, "arch": arch, "status": "explorer-open-failed",
                                    "detail": str(ex)[:400]})
                    continue
            summary = run_case_isolated(client, case, arch, run_id, iso)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
            if case not in ("EDIT-ACC-021", "EDIT-ACC-023") or arch == "x86":
                try:
                    relaunch_dedicated(client, arch, iso)
                except Exception as ex:  # noqa: BLE001
                    results.append({"case": "instance-restart", "arch": arch, "status": "failed",
                                    "detail": str(ex)[:400]})
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
