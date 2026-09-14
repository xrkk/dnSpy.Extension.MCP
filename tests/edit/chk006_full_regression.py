#!/usr/bin/env python3
"""CHK-006 full final regression (host side).  Runs, on the remediated build:
  1. release identity check (dnSpyEx latest stable vs VM install)
  2. tool registry export (dual source) + MCP resources/list + read-all
  3. the frozen 13-case edit regression on BOTH architectures
  4. the existing P01 VM suite and P02 VM + listener suites (ACC-028)
  5. host-side python client unit tests
and writes an honest pass/fail/blocked manifest section + raw JSON results.
"""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "tests" / "edit"))

from run_p01_vm_tests import UiMcpClient, powershell, start_dnspy, VM_URL  # noqa: E402
from ui_apply_settings import apply_settings  # noqa: E402
import p09_final_regression as p09  # noqa: E402

PLUGIN = ROOT / "dist/dnSpy.Extension.MCP-net48.x.dll"
RUN_PREFIX = "chk006-" + time.strftime("%Y%m%d-%H%M%S")
RESULTS: dict = {"run_prefix": RUN_PREFIX}


def fresh_dnspy(client: UiMcpClient, arch: str) -> bool:
    return p09.fresh_dnspy(client, arch)


def resources_probe(client: UiMcpClient) -> dict:
    listing = powershell(client, (
        '$body = @{jsonrpc="2.0"; id=1; method="resources/list"; params=@{}} | ConvertTo-Json -Compress; '
        '& curl.exe -fsS --max-time 10 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" '
        '-d $body http://127.0.0.1:15378/mcp 2>$null'
    ), timeout=60, allow_failure=True)
    import re as _re
    names = _re.findall(r'"uri"\s*:\s*"([^"]+)"', listing)
    read_ok = 0
    read_fail: list[str] = []
    for uri in names:
        read = powershell(client, (
            '$body = @{jsonrpc="2.0"; id=1; method="resources/read"; params=@{uri="' + uri + '"} } | ConvertTo-Json -Compress; '
            '$resp = & curl.exe -fsS --max-time 15 -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" '
            '-d $body http://127.0.0.1:15378/mcp 2>$null; '
            'if($LASTEXITCODE -eq 0 -and $resp.Length -gt 60 -and $resp -match "uri"){"read-ok"}else{"read-fail"}'
        ), timeout=60, allow_failure=True)
        if "read-ok" in read:
            read_ok += 1
        else:
            read_fail.append(uri)
    return {"listed": len(names), "uris": names, "read_ok": read_ok, "read_fail": read_fail}


def run_python_suite(host: str) -> dict:
    # jsonschema lives in the persistent verify venv (tests/run-verify-local.sh)
    verify_py = Path.home() / ".local/verify-venv/bin/python3"
    interpreter = str(verify_py) if verify_py.exists() else sys.executable
    proc = subprocess.run(
        [interpreter, "-m", "unittest", "discover", "-s", str(ROOT / "tests/python")],
        capture_output=True, text=True, timeout=900, cwd=ROOT)
    tail = ((proc.stdout or "") + (proc.stderr or ""))[-400:]
    return {"interpreter": interpreter, "returncode": proc.returncode,
            "ok": proc.returncode == 0, "tail": tail}


def run_host_driver(name: str, arguments: list[str], timeout: int = 2400) -> dict:
    try:
        proc = subprocess.run(
            [sys.executable, str(ROOT / "tests/edit" / name)] + arguments,
            capture_output=True, text=True, timeout=timeout, cwd=str(ROOT))
        return {"returncode": proc.returncode, "ok": proc.returncode == 0,
                "tail": ((proc.stdout or "") + (proc.stderr or ""))[-600:]}
    except subprocess.TimeoutExpired:
        return {"returncode": -1, "ok": False, "tail": "timeout"}


def main() -> int:
    client = UiMcpClient(VM_URL, timeout=90, client_name="chk006-full-regression")
    client.initialize()

    print("[1] release identity", flush=True)
    RESULTS["release_check"] = p09.release_check(client)
    print("    " + json.dumps(RESULTS["release_check"], ensure_ascii=False)[:240], flush=True)

    print("[2] deploy (plugin + drivers + harness)", flush=True)
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force; Start-Sleep -Seconds 2}; "stopped"')
    p09.deploy_drivers(client)
    p09.deploy_plugin(client)
    print("    plugin deployed", flush=True)

    results: list[dict] = []
    for arch in ("x64", "x86"):
        run_id = f"{RUN_PREFIX}-{arch}"
        if not fresh_dnspy(client, arch):
            for case in p09.REGRESSION_CASES:
                results.append({"case": case, "arch": arch, "status": "dnspy-start-failed"})
            continue
        if arch == "x64":
            print("[3] registry export (x64)", flush=True)
            RESULTS["registry_export"] = p09.export_registry(client)
            print("    " + RESULTS["registry_export"]["tail"][:200], flush=True)
            print("[3b] resources panorama (x64)", flush=True)
            RESULTS["resources"] = resources_probe(client)
            print(f"    listed={RESULTS['resources']['listed']} read_ok={RESULTS['resources']['read_ok']}", flush=True)
        for case in p09.REGRESSION_CASES:
            summary = None
            for attempt in range(3):
                try:
                    summary = p09.run_case(client, case, arch, run_id)
                    break
                except RuntimeError as ex:
                    print(f"    [retry {attempt + 1}] {case}: {str(ex)[:120]}", flush=True)
                    time.sleep(5)
            results.append(summary or {"case": case, "arch": arch, "status": "no-summary"})
            print(f"[{arch}] {case}: {(summary or {}).get('status')}", flush=True)
            if case not in ("EDIT-ACC-021", "EDIT-ACC-023") or arch == "x86":
                restarted = False
                for attempt in range(3):
                    try:
                        restarted = fresh_dnspy(client, arch)
                        break
                    except RuntimeError as ex:
                        print(f"    [retry {attempt + 1}] restart: {str(ex)[:120]}", flush=True)
                        time.sleep(8)
                if not restarted:
                    results.append({"case": "dnspy-restart", "arch": arch, "status": "failed"})
                    break
    RESULTS["edit_results"] = results
    powershell(client, '$t=@(Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue); if($t.Count){$t|Stop-Process -Force}; "stopped"')

    print("[4] P01 VM suite (existing)", flush=True)
    RESULTS["p01_suite"] = run_host_driver("run_p01_vm_tests.py", ["--dll", str(PLUGIN)], timeout=3000)
    print(f"    ok={RESULTS['p01_suite']['ok']} tail={RESULTS['p01_suite']['tail'][-200:]}", flush=True)

    print("[5] P02 VM suite (existing)", flush=True)
    RESULTS["p02_vm_suite"] = run_host_driver("run_p02_vm_tests.py", ["--dll", str(PLUGIN)], timeout=3000)
    print(f"    ok={RESULTS['p02_vm_suite']['ok']} tail={RESULTS['p02_vm_suite']['tail'][-200:]}", flush=True)

    print("[5b] P02 listener suite (existing)", flush=True)
    RESULTS["p02_listener_suite"] = run_host_driver(
        "run_p02_listener_tests.py", ["--dll", str(PLUGIN), "--output", "p02-listener-chk006.json"], timeout=3000)
    print(f"    ok={RESULTS['p02_listener_suite']['ok']} tail={RESULTS['p02_listener_suite']['tail'][-200:]}", flush=True)

    print("[6] python client unit tests (host)", flush=True)
    RESULTS["python_suite"] = run_python_suite("")
    print(f"    ok={RESULTS['python_suite']['ok']}", flush=True)

    client.close()
    output = ROOT / "tests/edit/chk006-regression-results.json"
    output.write_text(json.dumps(RESULTS, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"[done] {output}", flush=True)

    failed_cases = [r for r in results if r.get("status") != "pass"]
    suites_ok = all(RESULTS[k]["ok"] for k in ("p01_suite", "p02_vm_suite", "p02_listener_suite", "python_suite"))
    print(f"edit regression: {len(results) - len(failed_cases)}/{len(results)} pass; suites_ok={suites_ok}", flush=True)
    return 0 if not failed_cases and suites_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
