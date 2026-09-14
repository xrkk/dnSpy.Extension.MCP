#!/usr/bin/env python3
"""Master-plan 10.1 evidence runner for the P03 integration-layer ACC drivers.

Runs an existing VM driver module (p03_vm_acc019b / p03_vm_acc025) in-process,
records every MCP request/response pair through the driver's module-level
``call`` helper, and persists the frozen evidence layout:

    <ArtifactRoot>\\edit-tests\\<run-id>\\EDIT-ACC-xxx\\
        case.json          copy of the case input file
        driver.log         captured driver stdout/stderr
        actions.jsonl      one row per MCP tool call (request + response digest)
        summary.json       pass|fail|blocked, artifact identity, cleanup result

Exit codes follow the master plan: 0 = all pass, 1 = at least one failure,
2 = only blocked and no failure."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib
import io
import json
import os
import shutil
import sys
import time
from pathlib import Path

from p03_vm_isolation import IsolationContext, IsolationError

DRIVER_DIR = Path(__file__).resolve().parent
CASES_DIR = Path(__file__).resolve().parents[2] / "tests" / "edit" / "cases"
if not CASES_DIR.is_dir():
    # VM evidence layout: case files ride next to the runner under .\cases\
    CASES_DIR = DRIVER_DIR / "cases"
if str(DRIVER_DIR) not in sys.path:
    sys.path.insert(0, str(DRIVER_DIR))

CASE_MODULES = {
    "EDIT-ACC-019": "p03_vm_acc019b",
    "EDIT-ACC-025": "p03_vm_acc025",
    "EDIT-ACC-013": "p03_vm_acc013",
    "EDIT-ACC-014": "p03_vm_acc014b",
    "EDIT-ACC-020": "p03_vm_acc020",
    "EDIT-ACC-024": "p03_vm_acc024c",
    "EDIT-ACC-004": "p03_vm_acc004",
    "EDIT-ACC-005": "p03_vm_acc005full",
    "EDIT-ACC-006": "p03_vm_acc006",
    "EDIT-ACC-015": "p03_vm_acc015",
    "EDIT-ACC-007": "p03_vm_acc007",
    "EDIT-ACC-018": "p03_vm_acc018",
    "EDIT-ACC-021": "p03_vm_acc021",
    "EDIT-ACC-023": "p03_vm_acc023",
    "EDIT-ACC-008": "p03_vm_acc008",
    "EDIT-ACC-016": "p03_vm_acc016",
}

# Headless harness probes: run the registered P03StoreHarness mode and keep
# its output as the driver log (no MCP loopback involved).
HARNESS_CASES = {
    "EDIT-ACC-029": "--capacity-resolution",
    "EDIT-ACC-031": "--import-matrix",
    "EDIT-ACC-032": "--identity-matrix",
    "EDIT-ACC-033": "--resource-matrix",
}
HARNESS_DIR = Path(r"C:\Tools\dnspy-mcp-edit-tests\p03-harness-20260912-r1")
HARNESS_FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
DOTNET_BY_ARCH = {
    "x64": Path(r"C:\Tools\dotnet10-x64\dotnet.exe"),
    "x86": Path(r"C:\Tools\dotnet10-x86\dotnet.exe"),
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def response_digest(value) -> dict:
    """Compact structured digest of a tool response for the evidence log."""
    if not isinstance(value, dict):
        return {"kind": type(value).__name__}
    out: dict = {"ok": value.get("ok")}
    error = value.get("error")
    if isinstance(error, dict):
        out["error_code"] = error.get("code")
    result = value.get("result")
    if isinstance(result, dict):
        keys = sorted(k for k in result.keys() if isinstance(result.get(k), (str, int, bool, float)) or result.get(k) is None)
        out["result_scalars"] = {k: result.get(k) for k in keys[:24]}
    return out


def run_case(case_id: str, run_id: str, artifact_root: Path, arch: str = "x64",
             isolation: IsolationContext | None = None,
             module_loader=importlib.import_module) -> tuple[str, dict]:
    if case_id in HARNESS_CASES:
        return run_harness_case(case_id, run_id, artifact_root, arch, isolation)
    module_name = CASE_MODULES[case_id]
    if isolation is not None:
        try:
            # Configuration is validated before a driver import or evidence write.
            isolation.validate(require_ui=case_id == "EDIT-ACC-018")
        except IsolationError as ex:
            return blocked_summary(case_id, run_id, artifact_root, arch, str(ex))
    case_file = CASES_DIR / f"{case_id}.json"
    module = module_loader(module_name)
    if isolation is not None:
        try:
            configure = getattr(module, "configure_isolation")
            configure(isolation)
        except (IsolationError, AttributeError, ValueError) as ex:
            return blocked_summary(case_id, run_id, artifact_root, arch, str(ex))
    evidence_dir = artifact_root / "edit-tests" / run_id / case_id
    evidence_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(case_file, evidence_dir / "case.json")
    actions_path = evidence_dir / "actions.jsonl"
    actions_path.touch(exist_ok=True)
    log_path = evidence_dir / "driver.log"
    original_call = module.call
    original_client_cls = module.DnSpyClient
    call_index = {"n": 0}
    created_clients: list = []

    class TrackingClient(original_client_cls):  # type: ignore[misc, valid-type]
        def __init__(self, *client_args, **client_kwargs):
            super().__init__(*client_args, **client_kwargs)
            created_clients.append(self)

    def recording_call(client, tool, args):
        call_index["n"] += 1
        index = call_index["n"]
        outcome = original_call(client, tool, args)
        row = {
            "seq": index, "tool": tool,
            "args": {k: args.get(k) if isinstance(args, dict) else None for k in (args.keys() if isinstance(args, dict) else [])},
            "response": response_digest(outcome),
        }
        with open(actions_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")
        return outcome

    previous_ui_arch = os.environ.get("DNMCP_UI_ARCH")
    legacy_ui_arch = case_id == "EDIT-ACC-018" and isolation is None
    if legacy_ui_arch:
        os.environ["DNMCP_UI_ARCH"] = arch
    started = time.time()
    stdout = io.StringIO()
    exit_code: int | None = None
    error_text = ""
    try:
        module.call = recording_call
        module.DnSpyClient = TrackingClient
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stdout):
            exit_code = module.main()
    except Exception as ex:  # noqa: BLE001
        error_text = f"{type(ex).__name__}: {ex}"
        exit_code = 1
    finally:
        if legacy_ui_arch:
            if previous_ui_arch is None:
                os.environ.pop("DNMCP_UI_ARCH", None)
            else:
                os.environ["DNMCP_UI_ARCH"] = previous_ui_arch
        module.call = original_call
        module.DnSpyClient = original_client_cls
        closed = 0
        for created in created_clients:
            try:
                created.close()
                closed += 1
            except Exception:  # noqa: BLE001
                pass
    log_text = stdout.getvalue()
    log_path.write_text(log_text, encoding="utf-8")

    passed = "PASS " in log_text and "FAIL " not in log_text and exit_code == 0 and not error_text
    failed = "FAIL " in log_text or exit_code not in (0, None) or bool(error_text)
    status = "fail" if failed or not passed else "pass"
    transport_dead = ("DRIVER_TRANSPORT" in log_text) and ("PASS " not in log_text)
    if status == "fail" and transport_dead:
        status = "blocked"

    summary = {
        "suite": "structured-edit-p03",
        "case_id": case_id,
        "acc_id": case_id.replace("EDIT-", ""),
        "status": status,
        "driver": f"{module_name}.py",
        "driver_exit_code": exit_code,
        "driver_error": error_text,
        "pass_lines": log_text.count("PASS "),
        "fail_lines": log_text.count("FAIL "),
        "duration_s": round(time.time() - started, 1),
        "artifacts": {
            "case_json": {"path": str(evidence_dir / "case.json"), "sha256": sha256_file(evidence_dir / "case.json")},
            "driver_log": {"path": str(log_path), "sha256": sha256_file(log_path)},
            "actions": {"path": str(actions_path), "sha256": sha256_file(actions_path), "calls": call_index["n"]},
        },
        "cleanup": f"runner closed {closed}/{len(created_clients)} MCP sessions created by the driver; ArtifactRoot retained; this runner does not remove checkpoint, output or evidence files",
        "reason": "" if status == "pass" else (error_text or f"exit={exit_code} fail_lines={log_text.count('FAIL ')}"),
    }
    (evidence_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    return status, summary


def run_harness_case(case_id: str, run_id: str, artifact_root: Path, arch: str,
                     isolation: IsolationContext | None = None,
                     subprocess_runner=None) -> tuple[str, dict]:
    import subprocess
    if isolation is not None:
        try:
            isolation.validate()
        except IsolationError as ex:
            return blocked_summary(case_id, run_id, artifact_root, arch, str(ex))
    case_file = CASES_DIR / f"{case_id}.json"
    evidence_dir = artifact_root / "edit-tests" / run_id / case_id
    evidence_dir.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(case_file, evidence_dir / "case.json")
    log_path = evidence_dir / "driver.log"
    actions_path = evidence_dir / "actions.jsonl"
    actions_path.write_text("", encoding="utf-8")
    started = time.time()
    if isolation is not None:
        dotnet_host = isolation.dotnet_host
        harness_dir = Path(isolation.harness_dir)
        fixture = isolation.fixture("TestIL.dll")
    else:
        dotnet_host = str(DOTNET_BY_ARCH[arch])
        harness_dir = HARNESS_DIR
        fixture = HARNESS_FIXTURE
    runner = subprocess_runner or subprocess.run
    completed = runner(
        [dotnet_host, "P03StoreHarness.dll", fixture, HARNESS_CASES[case_id]],
        cwd=str(harness_dir), capture_output=True, text=True, timeout=600,
    )
    log_text = completed.stdout + completed.stderr
    log_path.write_text(log_text, encoding="utf-8")
    exit_code = completed.returncode
    passed = "PASS " in log_text and "FAILED" not in log_text and exit_code == 0
    status = "pass" if passed else "fail"
    summary = {
        "suite": "structured-edit-p03",
        "case_id": case_id,
        "acc_id": case_id.replace("EDIT-", ""),
        "status": status,
        "driver": f"P03StoreHarness {HARNESS_CASES[case_id]}",
        "architecture": arch,
        "driver_exit_code": exit_code,
        "pass_lines": log_text.count("PASS "),
        "fail_lines": log_text.count("FAILED"),
        "duration_s": round(time.time() - started, 1),
        "artifacts": {
            "case_json": {"path": str(evidence_dir / "case.json"), "sha256": sha256_file(evidence_dir / "case.json")},
            "driver_log": {"path": str(log_path), "sha256": sha256_file(log_path)},
            "actions": {"path": str(actions_path), "sha256": sha256_file(actions_path), "calls": 0, "note": "headless harness probe; no MCP transcript"},
        },
        "cleanup": "headless harness uses an isolated temp store; no dnSpy or ArtifactRoot side effects",
        "reason": "" if status == "pass" else f"exit={exit_code}",
    }
    (evidence_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    return status, summary


def blocked_summary(case_id: str, run_id: str, artifact_root: Path, arch: str, reason: str) -> tuple[str, dict]:
    """Report a configuration gap before any RPC, file write, or subprocess."""
    return "blocked", {
        "suite": "structured-edit-p03", "case_id": case_id,
        "acc_id": case_id.replace("EDIT-", ""), "status": "blocked",
        "architecture": arch, "reason": "isolation configuration: " + reason,
        "run_id": run_id, "artifact_root": str(artifact_root),
        "cleanup": "none; blocked before driver import side effects, RPC, write, or subprocess",
    }


def build_isolation(args: argparse.Namespace) -> IsolationContext | None:
    values = (args.isolation_root, args.mcp_url, args.fixture_root, args.checkpoint_store,
              args.work_root, args.harness_dir, args.dotnet_host)
    if not any(values) and not args.ui_deployment_root:
        return None
    if not all(values):
        raise IsolationError("isolation mode requires --isolation-root, --mcp-url, --fixture-root, "
                             "--checkpoint-store, --work-root, --harness-dir, and --dotnet-host")
    context = IsolationContext(
        run_id=args.run_id or "dry-run", architecture=args.arch, mcp_url=args.mcp_url,
        isolation_root=args.isolation_root, fixture_root=args.fixture_root,
        artifact_root=args.artifact_root, checkpoint_store=args.checkpoint_store,
        work_root=args.work_root, harness_dir=args.harness_dir, dotnet_host=args.dotnet_host,
        ui_deployment_root=args.ui_deployment_root)
    context.validate()
    return context


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", action="append", required=True, choices=sorted(CASE_MODULES | HARNESS_CASES))
    parser.add_argument("--arch", default="x64", choices=("x64", "x86"))
    parser.add_argument("--run-id", default=None)
    parser.add_argument("--artifact-root", default=os.path.join(os.environ.get("USERPROFILE", str(Path.home())), "Desktop", "dnspy-mcp-artifacts"))
    parser.add_argument("--isolation-root")
    parser.add_argument("--mcp-url")
    parser.add_argument("--fixture-root")
    parser.add_argument("--checkpoint-store")
    parser.add_argument("--work-root")
    parser.add_argument("--harness-dir")
    parser.add_argument("--dotnet-host")
    parser.add_argument("--ui-deployment-root")
    parser.add_argument("--plan", action="store_true")
    args = parser.parse_args()
    run_id = args.run_id or f"p03-evidence-{time.strftime('%Y%m%d-%H%M%S')}"
    artifact_root = Path(args.artifact_root)
    try:
        isolation = build_isolation(args)
    except IsolationError as ex:
        print(f"isolation configuration blocked: {ex}", file=sys.stderr)
        return 2

    if args.plan:
        if isolation is None:
            print("plan mode requires explicit isolation inputs", file=sys.stderr)
            return 2
        try:
            plans = [isolation.plan(case, harness=case in HARNESS_CASES,
                                    requires_ui=case == "EDIT-ACC-018") for case in args.case]
        except IsolationError as ex:
            print(f"isolation configuration blocked: {ex}", file=sys.stderr)
            return 2
        print(json.dumps({"result": "PLAN", "run_id": run_id, "cases": plans}, ensure_ascii=False, indent=2))
        return 0

    statuses: list[str] = []
    for case in args.case:
        status, summary = run_case(case, run_id, artifact_root, args.arch, isolation)
        statuses.append(status)
        print(f"{case} {status} pass={summary.get('pass_lines', 0)} fail={summary.get('fail_lines', 0)}", flush=True)
    if any(s == "fail" for s in statuses):
        return 1
    if statuses and all(s == "blocked" for s in statuses):
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
