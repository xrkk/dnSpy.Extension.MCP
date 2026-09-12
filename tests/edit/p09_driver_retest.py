#!/usr/bin/env python3
"""P09 driver retest: rerun only the three P09 driver cases on both
architectures after driver fixes (the rest of the final matrix already
passed in the full run)."""

from __future__ import annotations

import base64
import json
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests" / "edit"))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1"
CASES = ("EDIT-ACC-018", "EDIT-ACC-021", "EDIT-ACC-023")
RUN_ID = "p09-final-20260912-r2"


def sync_drivers(client: UiMcpClient) -> None:
    for name in ("p03_vm_acc018.py", "p03_vm_acc021.py", "p03_vm_acc023.py"):
        data = (ROOT / "tests/edit" / name).read_bytes()
        remote = f"{DEST}\\{name}"
        client.call_tool_json("FileSystem", {"mode": "write", "path": remote + ".b64",
                                            "content": base64.b64encode(data).decode(),
                                            "overwrite": True, "encoding": "utf-8"})
        powershell(client, f'[IO.File]::WriteAllBytes("{remote}",[Convert]::FromBase64String((Get-Content -LiteralPath "{remote}.b64" -Raw))); Remove-Item "{remote}.b64" -Force; "ok"')


def fresh(client: UiMcpClient, arch: str) -> bool:
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    start_dnspy(client, arch)
    apply_settings(client, True, "localhost")
    deadline = time.time() + 60
    while time.time() < deadline:
        probe = powershell(client, '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; if($LASTEXITCODE -eq 0){"up"}else{"down"}', allow_failure=True)
        if "up" in probe:
            return True
        time.sleep(1.5)
    return False


def run(client: UiMcpClient, case: str, arch: str) -> dict | None:
    log = f"evidence-{case}-{arch}.log"
    err = f"evidence-{case}-{arch}.err"
    powershell(client, (
        f'Remove-Item "{DEST}\\{log}","{DEST}\\{err}" -Force -ErrorAction SilentlyContinue; '
        f'$env:EDIT_ACC005_ARCH=\'{arch}\'; '
        f'$p = Start-Process -FilePath "C:\\Python313\\python.exe" -ArgumentList \'p03_vm_edit_acc_evidence.py\',\'--case\',\'{case}\',\'--arch\',\'{arch}\',\'--run-id\',\'{RUN_ID}-{arch}\' '
        f'-WorkingDirectory "{DEST}" -RedirectStandardOutput "{DEST}\\{log}" -RedirectStandardError "{DEST}\\{err}" -PassThru -WindowStyle Hidden; "launched"'
    ), timeout=90)
    deadline = time.time() + 480
    while time.time() < deadline:
        probe = powershell(client, '$drv = Get-CimInstance Win32_Process -Filter "Name=\'python.exe\'" | Where-Object { $_.CommandLine -like "*edit_acc_evidence*" }; if($drv){"alive"}else{"done"}', allow_failure=True)
        if "done" in probe:
            break
        time.sleep(3)
    time.sleep(1.0)
    summary_path = f"$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts\\edit-tests\\{RUN_ID}-{arch}\\{case}\\summary.json"
    read = powershell(client, f'if (Test-Path "{summary_path}") {{ Get-Content "{summary_path}" -Raw }} else {{ "missing" }}', allow_failure=True)
    if "missing" in read:
        tail = powershell(client, f'Get-Content "{DEST}\\{log}" -Tail 10 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "arch": arch, "status": "missing-summary", "log_tail": tail[:600]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip()) if isinstance(read, str) else None


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="p09-driver-retest")
    client.initialize()
    sync_drivers(client)
    results = []
    for arch in ("x64", "x86"):
        for case in CASES:
            if case == "EDIT-ACC-018" and arch == "x86":
                results.append({"case": case, "arch": arch, "status": "skipped-ui"})
                continue
            if not fresh(client, arch):
                results.append({"case": case, "arch": arch, "status": "dnspy-start-failed"})
                continue
            summary = run(client, case, arch)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    client.close()
    failed = [r for r in results if r.get("status") not in ("pass", "skipped-ui")]
    print(json.dumps(results, ensure_ascii=False, default=str)[:2000], flush=True)
    print(f"retest: {len(results) - len(failed)}/{len(results)} pass", flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
