#!/usr/bin/env python3
"""CHK-001..010 remediation orchestrator (host side).  Deploys the remediated
plugin build to the Win10 VM, builds the dedicated fixtures, then runs the
targeted evidence drivers for CHK-003/004/005/007/008 and the UI driver for
CHK-001/002, including the BCL ResourceReader readback for CHK-005."""

from __future__ import annotations

import hashlib
import json
import sys
import tarfile
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests" / "edit"))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

DEST = r"C:\Tools\dnspy-mcp-edit-tests\chk-remediation"
FIXTURES = r"C:\Tools\mcp-repo\tests\fixtures\bin\chk-remediation"
VM_EXTENSION = r"C:\Tools\dnSpy\bin\Extensions\dnSpy.Extension.MCP\dnSpy.Extension.MCP.x.dll"
PLUGIN = ROOT / "dist/dnSpy.Extension.MCP-net48.x.dll"
RUN_ID = "chk-remediation-" + time.strftime("%Y%m%d-%H%M%S")
ALL_CASES = ("chk003-drift", "chk004-identity", "chk005-resource", "chk007-risks", "chk008-inverse",
             "chk012-drift", "chk013-gate", "chk017-partial")
# optional subset via environment: CHK_CASES_FILTER="case1,case2"
import os as _os
_filter = _os.environ.get("CHK_CASES_FILTER", "")
CASES = tuple(c for c in ALL_CASES if not _filter or c in _filter.split(","))

FIXTURE_BUILDER = r'''
$ErrorActionPreference = 'Stop'
$fx = 'FIXTURES'
New-Item -ItemType Directory -Force -Path $fx | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
[IO.File]::WriteAllText("$fx\IdentityHost.cs", @'
using System;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 16)]
struct LayoutProbe { byte a; }
class Program {
  static int Main(){ Console.WriteLine("main"); return 0; }
  public static int AltEntry(){ Console.WriteLine("alt"); return 7; }
}
'@)
& $csc /nologo /target:exe /platform:anycpu /optimize- /out:"$fx\IdentityHost.exe" "$fx\IdentityHost.cs" | Out-Null
if (-not (Test-Path "$fx\IdentityHost.exe")) { throw 'IdentityHost build failed' }
# CHK-005 container: empty-name string + normal string + Stream entry + custom
# AddResourceData payload, all written by the BCL ResourceWriter.
$rw = New-Object System.Resources.ResourceWriter("$fx\Strings2.resources")
$rw.AddResource('greeting', 'hello')
$rw.AddResource('', 'empty-name-entry')
$stream = New-Object System.IO.MemoryStream(,[byte[]](9,8,7,6,5))
$rw.AddResource('streamRes', $stream)
$rw.AddResourceData('customBlob', 'ChkRemediation.Ghost, ChkRemediation', [byte[]](1,2,254,255))
$rw.Generate()
$rw.Close()
[IO.File]::WriteAllText("$fx\ResourceHost2.cs", 'class ResourceHost2 { }')
& $csc /nologo /target:library /platform:anycpu /optimize- "/resource:$fx\Strings2.resources,ResourceHost2.Strings2.resources" /out:"$fx\ResourceHost2.dll" "$fx\ResourceHost2.cs" | Out-Null
if (-not (Test-Path "$fx\ResourceHost2.dll")) { throw 'ResourceHost2 build failed' }
[IO.File]::WriteAllText("$fx\TargetHost.cs", 'public class Calc { public static int Add(int a, int b) { return a + b; } }')
& $csc /nologo /target:library /platform:anycpu /optimize- /out:"$fx\TargetHost.dll" "$fx\TargetHost.cs" | Out-Null
[IO.File]::WriteAllText("$fx\InboundHost.cs", 'using System; class Program { static int Main() { Console.WriteLine(Calc.Add(1, 2)); return 0; } }')
& $csc /nologo /target:exe /platform:anycpu /optimize- /r:"$fx\TargetHost.dll" /out:"$fx\InboundHost.exe" "$fx\InboundHost.cs" | Out-Null
if (-not (Test-Path "$fx\InboundHost.exe")) { throw 'InboundHost build failed' }
'fixtures-ok'
'''.replace("FIXTURES", FIXTURES)

READBACK = r'''
$ErrorActionPreference = 'Stop'
$marker = 'MARKER'
$dll = (Get-Content $marker -Raw).Trim()
if (-not [IO.Path]::IsPathRooted($dll)) { $dll = Join-Path "$env:USERPROFILE\Desktop\dnspy-mcp-artifacts" $dll }
if (-not (Test-Path $dll)) { throw "exported dll missing: $dll" }
$bytes = [IO.File]::ReadAllBytes($dll)
$asm = [System.Reflection.Assembly]::Load($bytes)
$res = $asm.GetManifestResourceStream('ResourceHost2.Strings2.resources')
if ($null -eq $res) { throw 'embedded resource stream missing' }
$reader = New-Object System.Resources.ResourceReader($res)
$keys = @()
$values = @{}
$enum = $reader.GetEnumerator()
while ($enum.MoveNext()) {
  $key = [string]$enum.Key
  $keys += $key
  if ($key -in @('greeting','','streamRes')) { $values[$key] = $enum.Value }
}
$out = [ordered]@{
  keys = ($keys | Sort-Object)
  greeting = [string]$values['greeting']
  empty_name_value = [string]$values['']
  stream_is_stream = ($values['streamRes'] -is [System.IO.Stream])
  stream_is_bytearray = ($values['streamRes'] -is [byte[]])
}
# raw custom payload via GetResourceData (no deserialization: REQ-021 sentinel);
# a C# helper binds the ByRef parameters natively (PowerShell [ref] cannot)
if (-not ('ChkGrd' -as [type])) {
Add-Type -TypeDefinition @'
using System.Resources;
public static class ChkGrd {
  public static string[] Get(ResourceReader reader, string name) {
    string typeName; byte[] raw;
    reader.GetResourceData(name, out typeName, out raw);
    return new string[] { typeName ?? "", raw == null ? "" : string.Join(",", raw) };
  }
}
'@
}
$custom = [ChkGrd]::Get($reader, 'customBlob')
$out['custom_type_name'] = $custom[0]
$out['custom_bytes'] = $custom[1]
$empty = [ChkGrd]::Get($reader, '')
$out['empty_type_name'] = $empty[0]
$reader.Close()
$out | ConvertTo-Json -Compress
'''.replace("MARKER", DEST + r"\chk005-export-path.txt")


def upload(client: UiMcpClient, source: Path, destination: str) -> None:
    import base64
    encoded = base64.b64encode(source.read_bytes()).decode("ascii")
    client.call_tool_json("FileSystem", {
        "mode": "write", "path": destination + ".b64", "content": encoded,
        "overwrite": True, "encoding": "utf-8",
    })
    powershell(client, (
        f'$parent=Split-Path -Parent "{destination}"; New-Item -ItemType Directory -Force -Path $parent | Out-Null; '
        f'[IO.File]::WriteAllBytes("{destination}",[Convert]::FromBase64String((Get-Content -LiteralPath "{destination}.b64" -Raw))); '
        f'Remove-Item -LiteralPath "{destination}.b64" -Force'
    ))


def deploy_plugin(client: UiMcpClient) -> str:
    expected = hashlib.sha256(PLUGIN.read_bytes()).hexdigest().upper()
    staged = VM_EXTENSION + ".chk.new"
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
        '"deployed " + $deployed.Substring(0,16)'
    ))


def deploy_drivers(client: UiMcpClient) -> None:
    entries = [
        (ROOT / "tests/edit/chk_targeted_driver.py", "chk_targeted_driver.py"),
        (ROOT / "tests/edit/chk_ui_driver.py", "chk_ui_driver.py"),
        (ROOT / "tests/edit/run_p01_vm_tests.py", "run_p01_vm_tests.py"),
        (ROOT / "tests/edit/ui_apply_settings.py", "ui_apply_settings.py"),
        (ROOT / "dnspy_mcp/__init__.py", "dnspy_mcp/__init__.py"),
        (ROOT / "dnspy_mcp/client.py", "dnspy_mcp/client.py"),
    ]
    with tempfile.NamedTemporaryFile(prefix="chk-deploy-", suffix=".tar.gz", delete=False) as temp:
        archive_path = Path(temp.name)
    try:
        import io
        with tarfile.open(archive_path, "w:gz") as archive:
            for source, arcname in entries:
                if source.is_file():
                    info = tarfile.TarInfo(arcname)
                    info.size = source.stat().st_size
                    info.mtime = int(source.stat().st_mtime)
                    with source.open("rb") as handle:
                        archive.addfile(info, handle)
        upload(client, archive_path, DEST + r"\chk-deploy.tar.gz")
    finally:
        archive_path.unlink(missing_ok=True)
    powershell(client, f'New-Item -ItemType Directory -Force -Path "{DEST}" | Out-Null; tar.exe -xzf "{DEST}\\chk-deploy.tar.gz" -C "{DEST}"; "extracted"')


def fresh_dnspy(client: UiMcpClient) -> bool:
    # Zero-UI lifecycle (management-service-friendly): stop dnSpy, clean BOTH
    # artifact stores, seed a valid LOOPBACK snapshot into the persisted
    # dnSpy.xml, then start dnSpy — the listener binds from persisted config;
    # no Options-dialog UI automation at all.
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; $t|Wait-Process -Timeout 15 -ErrorAction SilentlyContinue}; "stopped"')
    seed = (
        '$roots=@("$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts","C:\\dnspy-mcp-artifacts"); '
        'foreach($root in $roots){ Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\*" -Recurse -Force -ErrorAction SilentlyContinue }; '
        '$path = "$env:APPDATA\\dnSpy\\dnSpy.xml"; '
        '$c = Get-Content $path -Raw; '
        "$c = $c -replace '&quot;EnableServer&quot;:false', '&quot;EnableServer&quot;:true'; "
        "$c = $c -replace '&quot;Host&quot;:&quot;[^&]*&quot;', '&quot;Host&quot;:&quot;localhost&quot;'; "
        "$c = $c -replace '&quot;RemoteAllowedCidrs&quot;:\[[^\]]*\]', '&quot;RemoteAllowedCidrs&quot;:[]'; "
        "$c = $c -replace '&quot;RemoteHostOnlyAcknowledged&quot;:true', '&quot;RemoteHostOnlyAcknowledged&quot;:false'; "
        '[IO.File]::WriteAllText($path, $c); "prepared"')
    prepared = powershell(client, seed, allow_failure=True)
    prepared = powershell(client, seed, allow_failure=True)
    if "prepared" not in prepared:
        return False
    # verify the configured store actually emptied (a locked ledger file makes
    # Remove-Item fail silently and every subsequent begin diverge)
    for _clean_attempt in range(3):
        left = powershell(client, '(Get-ChildItem "C:\\dnspy-mcp-artifacts\\edit-checkpoints" -ErrorAction SilentlyContinue | Measure-Object).Count', allow_failure=True)
        if "0" in left.replace("Response:", "").split():
            break
        powershell(client, 'Start-Sleep -Seconds 5; Remove-Item "C:\\dnspy-mcp-artifacts\\edit-checkpoints\\*" -Recurse -Force -ErrorAction SilentlyContinue; "again"', allow_failure=True)
    else:
        return False
    deadline = time.time() + 90
    while time.time() < deadline:
        probe = powershell(client, (
            '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
            'if($LASTEXITCODE -eq 0){"up"}else{"down"}'
        ), allow_failure=True)
        if "up" in probe:
            return True
        time.sleep(2)
    return False


def run_vm_python(client: UiMcpClient, script: str, arguments: str, env: dict[str, str] | None = None) -> int:
    stem = script.replace(".py", "") + ("-" + arguments.split("=", 1)[-1] if "=" in arguments else "")
    log = stem + ".log"
    err = stem + ".err"
    env_prefix = "".join(f"$env:{key}='{value}'; " for key, value in (env or {}).items())
    launch = (
        f'Remove-Item "{DEST}\\{log}","{DEST}\\{err}" -Force -ErrorAction SilentlyContinue; '
        + env_prefix
        + f'$p = Start-Process -FilePath "C:\\Python313\\python.exe" -ArgumentList \'{script}\',\'{arguments}\' '
        f'-WorkingDirectory "{DEST}" -RedirectStandardOutput "{DEST}\\{log}" -RedirectStandardError "{DEST}\\{err}" -PassThru -WindowStyle Hidden; "launched=$($p.Id)"'
    )
    for attempt in range(4):
        try:
            powershell(client, launch, timeout=90)
            break
        except RuntimeError:
            if attempt == 3:
                raise
            time.sleep(20)
    deadline = time.time() + 420
    while time.time() < deadline:
        probe = powershell(client, (
            '$drv = Get-CimInstance Win32_Process -Filter "Name=\'python.exe\'" | '
            f'Where-Object {{ $_.CommandLine -like "*{script.replace(".py", "")}*" }}; '
            'if($drv){"alive"}else{"done"}'
        ), allow_failure=True)
        if "done" in probe:
            return 0
        time.sleep(3)
    print(f"[timeout] {script} did not finish in time", flush=True)
    return 1


def read_summary(client: UiMcpClient, case: str) -> dict:
    path = f"$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts\\edit-tests\\{RUN_ID}\\{case}\\summary.json"
    read = powershell(client, f'if (Test-Path "{path}") {{ Get-Content "{path}" -Raw }} else {{ "missing" }}', allow_failure=True)
    if "missing" in read or not read.strip():
        log = powershell(client, f'Get-Content "{DEST}\\chk_targeted_driver-{case}.log" -Tail 15 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "status": "missing-summary", "log_tail": log[:900]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip())


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="chk-remediation-orchestrator")
    client.initialize()
    print("[1] stopping dnSpy + deploying remediated plugin", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    print("[1] deploy:", deploy_plugin(client).strip(), flush=True)
    deploy_drivers(client)
    print("[2] building fixtures", flush=True)
    result = powershell(client, FIXTURE_BUILDER, timeout=300, allow_failure=True)
    print("[2] fixtures:", result.strip()[-200:], flush=True)
    if "fixtures-ok" not in result:
        print("[abort] fixture build failed", flush=True)
        return 2

    results: dict[str, dict] = {}
    for case in CASES:
        print(f"[3] {case}: fresh dnSpy", flush=True)
        ready = False
        for attempt in range(3):
            try:
                if fresh_dnspy(client):
                    ready = True
                    break
            except RuntimeError as ex:
                print(f"    [fresh retry {attempt}] {str(ex)[:100]}", flush=True)
                time.sleep(15)
        if not ready:
            results[case] = {"case": case, "status": "dnspy-not-ready"}
            continue
        env = {"CHK_CASE": case, "CHK_RUN_ID": RUN_ID, "CHK_EXPORT_DIR": DEST,
               "EDIT_ACC005_ARCH": "x64"}
        code = -1
        for attempt in range(3):
            try:
                code = run_vm_python(client, "chk_targeted_driver.py", f"--case={case}", env)
                break
            except RuntimeError as ex:
                print(f"[transient {attempt}] {case}: {str(ex)[:120]}", flush=True)
                time.sleep(15)
        if code != 0 and attempt < 2:
            pass  # last attempt below already ran
        # one full retry when the case itself failed wholesale
        if code == 0 and results.get(case, {}).get("status") != "PASS":
            pass
        if code == 0:
            results[case] = read_summary(client, case)
            # retry only near-total failures; a partial run (>= 3 passes) keeps
            # its log and summary for diagnosis instead of being overwritten
            passes = len(results[case].get("passes", []) or [])
            if results[case].get("status") != "PASS" and passes < 3:
                print(f"[retry-case] {case} failed once; retrying", flush=True)
                time.sleep(5)
                try:
                    if fresh_dnspy(client):
                        code = run_vm_python(client, "chk_targeted_driver.py", f"--case={case}", env)
                        if code == 0:
                            results[case] = read_summary(client, case)
                except RuntimeError as ex:
                    print(f"[retry-case failed] {str(ex)[:120]}", flush=True)
        else:
            results[case] = {"case": case, "status": "launch-timeout"}
        print(f"[3] {case}: {json.dumps(results[case], ensure_ascii=False)[:300]}", flush=True)

        if case == "chk005-resource" and results[case].get("status") == "PASS":
            # BCL ResourceReader readback must run BEFORE the next case's
            # fresh_dnspy cleans the edit-output directory
            print("[4] CHK-005 BCL readback (immediate)", flush=True)
            readback = powershell(client, READBACK, timeout=120, allow_failure=True)
            try:
                body = json.loads(readback.split("Response:", 1)[-1].split("Status Code", 1)[0].strip())
                ok = (sorted(body.get("keys", [])) == ["", "customBlob", "greeting", "streamRes"]
                      and body.get("greeting") == "remediated"
                      and body.get("empty_name_value") == "empty-name-entry"
                      and body.get("stream_is_stream") is True
                      and body.get("stream_is_bytearray") is False
                      and "ChkRemediation.Ghost" in str(body.get("custom_type_name"))
                      and body.get("custom_bytes") == "1,2,254,255")
                results["chk005-readback"] = {"case": "chk005-readback",
                                              "status": "PASS" if ok else "FAIL", "data": body}
            except (ValueError, IndexError):
                results["chk005-readback"] = {"case": "chk005-readback", "status": "FAIL",
                                              "data": {"raw": readback[:500]}}
            print(f"[4] readback: {results['chk005-readback']['status']} "
                  f"{json.dumps(results['chk005-readback'].get('data'), ensure_ascii=False)[:300]}", flush=True)

    print("[5] UI driver (CHK-001/002)", flush=True)
    if fresh_dnspy(client):
        run_vm_python(client, "chk_ui_driver.py", "", {"CHK_RUN_ID": RUN_ID})
        results["chk001-ui"] = read_summary(client, "chk001-ui")
    else:
        results["chk001-ui"] = {"case": "chk001-ui", "status": "dnspy-not-ready"}
    print(f"[5] ui: {json.dumps(results['chk001-ui'], ensure_ascii=False)[:300]}", flush=True)

    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')
    output = ROOT / "tests/edit/chk-remediation-summary.json"
    output.write_text(json.dumps({"run_id": RUN_ID, "results": results}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"[done] summary -> {output}", flush=True)
    client.close()
    failures = [name for name, row in results.items() if row.get("status") != "PASS"]
    print("PASS cases:", [n for n in results if results[n].get("status") == "PASS"], flush=True)
    print("non-PASS:", failures, flush=True)
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
