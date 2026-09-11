#!/usr/bin/env python3
"""Round-61 packaging orchestrator (host side): for each driver case and each
architecture, provide a fresh dnSpy + clean ArtifactRoot (the pairing rule),
run the evidence runner for that single case, and read back its summary.json.
The headless harness case runs without dnSpy."""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402

DEST = r"C:\Tools\dnspy-mcp-edit-tests\p03-integration-r1"
DRIVER_CASES = ("EDIT-ACC-013", "EDIT-ACC-014", "EDIT-ACC-020", "EDIT-ACC-024")
HARNESS_CASES = ("EDIT-ACC-029",)


def health_up(client: UiMcpClient) -> bool:
    result = powershell(client, (
        '& curl.exe -fsS --max-time 2 http://127.0.0.1:15378/health 2>$null | Out-Null; '
        'if($LASTEXITCODE -eq 0){"up"}else{"down"}'
    ), allow_failure=True)
    return "up" in result


def fresh_dnspy(client: UiMcpClient, arch: str) -> bool:
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    start_dnspy(client, arch)
    apply_settings(client, True, "localhost")
    deadline = time.time() + 60
    while time.time() < deadline:
        if health_up(client):
            return True
        time.sleep(1.5)
    return False


def run_case_on_vm(client: UiMcpClient, case: str, arch: str, run_id: str) -> dict | None:
    log = f"evidence-{case}-{arch}.log"
    err = f"evidence-{case}-{arch}.err"
    powershell(client, (
        f'Remove-Item "{DEST}\\{log}","{DEST}\\{err}" -Force -ErrorAction SilentlyContinue; '
        f'$p = Start-Process -FilePath "C:\\Python313\\python.exe" -ArgumentList \'p03_vm_edit_acc_evidence.py\',\'--case\',\'{case}\',\'--arch\',\'{arch}\',\'--run-id\',\'{run_id}\' '
        f'-WorkingDirectory "{DEST}" -RedirectStandardOutput "{DEST}\\{log}" -RedirectStandardError "{DEST}\\{err}" -PassThru -WindowStyle Hidden; "launched=$($p.Id)"'
    ))
    deadline = time.time() + 420
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
        tail = powershell(client, f'Get-Content "{DEST}\\{log}" -Tail 6 -ErrorAction SilentlyContinue', allow_failure=True)
        return {"case": case, "arch": arch, "status": "missing-summary", "log_tail": tail[:400]}
    return json.loads(read.split("Response:", 1)[-1].split("Status Code", 1)[0].strip()) if isinstance(read, str) else None


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="p03-round61-orchestrator")
    client.initialize()
    results: list[dict] = []
    for arch in ("x64", "x86"):
        run_id = f"p03-evidence-20260913-r1-{arch}"
        for case in DRIVER_CASES:
            print(f"[{arch}] fresh dnSpy for {case}", flush=True)
            if not fresh_dnspy(client, arch):
                results.append({"case": case, "arch": arch, "status": "dnspy-start-failed"})
                continue
            summary = run_case_on_vm(client, case, arch, run_id)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
        for case in HARNESS_CASES:
            summary = run_case_on_vm(client, case, arch, run_id)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')
    powershell(client, '$root="$env:USERPROFILE\\Desktop\\dnspy-mcp-artifacts"; Remove-Item "$root\\edit-checkpoints\\*","$root\\edit-output\\*" -Recurse -Force -ErrorAction SilentlyContinue; "cleaned"')
    print(json.dumps({"results": results}, ensure_ascii=False, default=str)[:4000], flush=True)
    failed = [r for r in results if r.get("status") != "pass"]
    print(f"orchestrator done: {len(results) - len(failed)}/{len(results)} pass", flush=True)
    return 0 if not failed else 1


if __name__ == "__main__":
    raise SystemExit(main())
