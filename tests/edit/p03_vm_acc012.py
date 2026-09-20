#!/usr/bin/env python3
"""ACC-012/029 real-loopback checkpoint package, dedup, and branch proof."""
from __future__ import annotations

import base64
import hashlib
import json
import shutil
import sys
import uuid
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
STORE = Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts\edit-checkpoints")
EVIDENCE = Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts\acc012")
FAILURES: list[str] = []
PASSES: list[str] = []
SNAPSHOTS: list[dict] = []
PACKAGE_REPORTS: list[dict] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE, STORE, EVIDENCE
    context.validate()
    URL = context.mcp_url
    FIXTURE = context.fixture("TestIL.dll")
    STORE = Path(context.checkpoint_store)
    EVIDENCE = Path(context.artifact_root) / "edit-tests" / context.run_id / "EDIT-ACC-012"


def rid() -> str:
    return str(uuid.uuid4())


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check(name: str, condition: bool, detail: object = "") -> None:
    (PASSES if condition else FAILURES).append(name)
    print(f"{'PASS' if condition else 'FAIL'} {name}" + ("" if condition else f" {detail}"), flush=True)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        result = client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                result = json.loads(text[start:])
            except json.JSONDecodeError:
                result = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:400]}}
        else:
            result = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:400]}}
    print("MCP " + tool + " " + json.dumps(result, ensure_ascii=False, sort_keys=True), flush=True)
    return result


def payload(value: dict) -> dict:
    result = value.get("result") if isinstance(value, dict) else None
    return result if isinstance(result, dict) else {}


def files_snapshot(label: str) -> dict:
    rows = []
    if STORE.is_dir():
        for path in sorted((p for p in STORE.rglob("*") if p.is_file()), key=lambda p: str(p).lower()):
            data = path.read_bytes()
            rows.append({"path": str(path.relative_to(STORE)).replace("\\", "/"), "length": len(data), "sha256": sha(data)})
    row = {"label": label, "files": rows, "manifest_sha256": sha(json.dumps(rows, sort_keys=True).encode())}
    SNAPSHOTS.append(row)
    print("DISK " + json.dumps(row, sort_keys=True), flush=True)
    return row


def package_path(lineage: str) -> Path:
    return STORE / f"{lineage}.dnspy-mcp-checkpoints"


def inspect_package(label: str, lineage: str) -> dict:
    path = package_path(lineage)
    raw = path.read_bytes()
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    package_copy = EVIDENCE / "packages" / f"{len(PACKAGE_REPORTS) + 1:02d}-{label}.dnspy-mcp-checkpoints"
    package_copy.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, package_copy)
    with zipfile.ZipFile(path) as archive:
        entries = []
        content_paths: dict[str, list[str]] = {}
        content: dict[str, bytes] = {}
        for info in sorted(archive.infolist(), key=lambda x: x.filename):
            data = archive.read(info.filename)
            digest = sha(data)
            entries.append({"path": info.filename, "length": len(data), "sha256": digest,
                            "compressed_length": info.compress_size})
            content_paths.setdefault(digest, []).append(info.filename)
            content[info.filename] = data
        manifest = json.loads(content["manifest.json"])
        operations = {name: json.loads(data) for name, data in content.items() if name.startswith("operations/")}
    duplicate_payload_paths = {digest: paths for digest, paths in content_paths.items()
                               if len(paths) > 1 and any(p.startswith("payloads/") for p in paths)}
    report = {
        "label": label, "package": str(path), "archive_copy": str(package_copy),
        "length": len(raw), "sha256": sha(raw), "entries": entries,
        "manifest": manifest, "operations": operations,
        "duplicate_payload_paths": duplicate_payload_paths,
    }
    PACKAGE_REPORTS.append(report)
    print("PACKAGE " + json.dumps(report, ensure_ascii=False, sort_keys=True), flush=True)
    return report


def status(client: DnSpyClient, label: str) -> dict:
    row = payload(call(client, "edit_status", {}))
    print("STATUS " + label + " " + json.dumps(row, sort_keys=True), flush=True)
    return row


def history(client: DnSpyClient, lineage: str, label: str) -> dict:
    row = payload(call(client, "edit_history", {"lineage_id": lineage, "page_size": 100}))
    print("HISTORY " + label + " " + json.dumps(row, sort_keys=True), flush=True)
    return row


def commit(client: DnSpyClient, operations: list[dict], label: str) -> tuple[str, str, str, str]:
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    core = payload(begin)
    tx = str(core.get("transaction", {}).get("transaction_id", ""))
    revision = int(core.get("transaction", {}).get("work_revision", 0))
    check(label + " begin", bool(begin.get("ok")) and bool(tx), begin)
    for operation in operations:
        applied = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
                                               "expected_revision": revision, "operation": operation})
        check(label + " apply " + str(operation.get("kind")), bool(applied.get("ok")), applied)
        revision = int(payload(applied).get("transaction", {}).get("work_revision", revision + 1))
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx,
                                             "expected_revision": revision})
    review = payload(reviewed).get("review", {})
    check(label + " review", bool(reviewed.get("ok")) and bool(review.get("review_id")), reviewed)
    required = [str(value) for value in review.get("required_confirmation_ids", [])]
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review.get("review_id", ""), "review_revision": review.get("review_revision", revision),
        "confirmed_risk_ids": required,
    })
    result = payload(committed)
    checkpoint = result.get("checkpoint", {})
    lineage = str(result.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(checkpoint.get("checkpoint_id", ""))
    parent = str(checkpoint.get("parent_checkpoint_id") or "")
    live = str(result.get("fingerprints", {}).get("after", ""))
    check(label + " commit", bool(committed.get("ok")) and bool(lineage) and bool(checkpoint_id), committed)
    print("COMMIT " + json.dumps({"label": label, "lineage_id": lineage, "checkpoint_id": checkpoint_id,
                                  "parent_checkpoint_id": parent, "live_fingerprint": live}, sort_keys=True), flush=True)
    files_snapshot(label)
    inspect_package(label, lineage)
    status(client, label)
    history(client, lineage, label)
    return lineage, checkpoint_id, parent, live


def restore(client: DnSpyClient, lineage: str, checkpoint: str, label: str) -> tuple[dict, dict]:
    status(client, label + "-before")
    live = live_fingerprint(client)
    assessed = call(client, "edit_restore", {"request_id": rid(), "lineage_id": lineage,
                                              "checkpoint_id": checkpoint, "action": "assess"})
    replay = payload(assessed).get("replay", {})
    args = {"request_id": rid(), "lineage_id": lineage, "checkpoint_id": checkpoint, "action": "apply",
            "replay_id": replay.get("replay_id", ""), "expected_live_fingerprint": live}
    if replay.get("classification") == "validated_drift":
        args["confirm_validated_drift"] = True
    applied = call(client, "edit_restore", args)
    check(label + " restore", bool(assessed.get("ok")) and bool(applied.get("ok")), {"assess": assessed, "apply": applied})
    files_snapshot(label)
    inspect_package(label, lineage)
    return replay, payload(applied)


def type_name(client: DnSpyClient, target_token: str) -> str:
    response = call(client, "list_types", {"assembly_name": "TestIL", "include_nested": False})
    rows = response.get("items") or response.get("Items") or []
    def token_value(row: dict) -> int:
        raw = row.get("token") or row.get("Token") or 0
        return int(raw, 16) if isinstance(raw, str) and raw.startswith("0x") else int(raw)
    expected = int(target_token, 16)
    row = next((r for r in rows if isinstance(r, dict) and token_value(r) == expected), {})
    return str(row.get("name") or row.get("Name") or row.get("full_name") or row.get("FullName") or "")


def type_token(client: DnSpyClient) -> str:
    response = call(client, "list_types", {"assembly_name": "TestIL", "include_nested": False})
    rows = response.get("items") or response.get("Items") or []
    row = next((r for r in rows if isinstance(r, dict) and str(r.get("FullName") or r.get("full_name")) == "TestIL.Simple"), {})
    raw = row.get("token") or row.get("Token")
    if isinstance(raw, str) and raw.startswith("0x"):
        return raw.lower()
    return f"0x{int(raw):08x}" if raw is not None else ""


def method_token(client: DnSpyClient) -> str:
    response = call(client, "list_methods", {"assembly_name": "TestIL", "type_full_name": "TestIL.Simple"})
    rows = response.get("items") or response.get("Items") or []
    row = next((r for r in rows if isinstance(r, dict) and str(r.get("name") or r.get("Name")) == "AddOne"), {})
    raw = row.get("token") or row.get("Token")
    if isinstance(raw, str) and raw.startswith("0x"):
        return raw.lower()
    return f"0x{int(raw):08x}" if raw is not None else ""


def live_fingerprint(client: DnSpyClient) -> str:
    begun = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    core = payload(begun)
    tx = str(core.get("transaction", {}).get("transaction_id", ""))
    value = str(core.get("fingerprints", {}).get("current_live", ""))
    if tx:
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    return value


def export_resource(client: DnSpyClient, name: str, leaf: str, expected: bytes) -> bool:
    result = call(client, "edit_resource_export", {"request_id": rid(), "assembly_name": "TestIL",
                                                    "resource_name": name, "output_path": f"edit-output\\acc012\\{leaf}"})
    row = payload(result).get("export", {})
    return bool(result.get("ok")) and int(row.get("length", -1)) == len(expected) and str(row.get("sha256", "")) == sha(expected)


def main() -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    client = DnSpyClient(URL, client_name="p03-vm-acc012", timeout=120)
    client.initialize()
    opened = call(client, "open_files", {"paths": [FIXTURE]})
    check("fixture opened", "error" not in opened, opened)
    files_snapshot("initial")
    add_one_token = method_token(client)
    simple_token = type_token(client)
    check("AddOne token resolved", bool(add_one_token), add_one_token)
    check("Simple type token resolved", bool(simple_token), simple_token)

    shared = bytes(range(256)) * 16
    shared_b64 = base64.b64encode(shared).decode()
    body = {"max_stack": 1, "init_locals": False, "locals": [], "exception_handlers": [],
            "instructions": [{"opcode": "ldc.i4.7", "operand": None}, {"opcode": "ret", "operand": None}]}
    code = {"kind": "method_body_replace", "target": {"token": add_one_token}, "body": body}
    resource_one = {"kind": "managed_resource_add", "name": "T022.Shared.One", "data_base64": shared_b64}
    resource_two = {"kind": "managed_resource_add", "name": "T022.Shared.Two", "data_base64": shared_b64}
    rename_original = {"kind": "type_update", "target": {"token": simple_token}, "name": "T022Original"}
    rename_branch = {"kind": "type_update", "target": {"token": simple_token}, "name": "T022Branch"}

    lineage, c1, _, _ = commit(client, [code, resource_one], "c1-code-resource")
    lineage2, c2, p2, _ = commit(client, [code, resource_two], "c2-repeat-payloads")
    lineage3, c3, p3, fp3 = commit(client, [rename_original], "c3-original-branch")
    check("single lineage and linear parents", lineage == lineage2 == lineage3 and p2 == c1 and p3 == c2,
          {"lineages": [lineage, lineage2, lineage3], "parents": [p2, p3]})

    final_linear = PACKAGE_REPORTS[-1]
    payload_rows = final_linear["manifest"].get("payloads", [])
    payload_entries = [row for row in final_linear["entries"] if row["path"].startswith("payloads/")]
    resource_sha = sha(shared)
    all_ops = json.dumps(final_linear["operations"], sort_keys=True)
    references = all_ops.count(resource_sha)
    check("resource payload one physical entry and multiple node references",
          sum(row["sha256"] == resource_sha for row in payload_entries) == 1 and references >= 2,
          {"sha": resource_sha, "entry_count": sum(row["sha256"] == resource_sha for row in payload_entries), "refs": references})
    code_payloads = [str(row.get("sha256")) for row in payload_rows if str(row.get("sha256")) != resource_sha]
    code_refs = {digest: all_ops.count(digest) for digest in code_payloads}
    check("repeated code payload deduplicated", any(count >= 2 for count in code_refs.values()), code_refs)
    check("one baseline and no duplicate payload path",
          sum(row["path"] == "baseline/module.bin" for row in final_linear["entries"]) == 1
          and not final_linear["duplicate_payload_paths"], final_linear["duplicate_payload_paths"])
    check("single final package no temp", len(files_snapshot("linear-final")["files"]) == 1
          and package_path(lineage).is_file() and not list(STORE.glob("*.tmp-*")))

    restore(client, lineage, c1, "restore-c1-content")
    check("c1 resource content recovered", export_resource(client, "T022.Shared.One", "one.bin", shared))
    missing_two = call(client, "edit_resource_export", {"request_id": rid(), "assembly_name": "TestIL",
                                                         "resource_name": "T022.Shared.Two", "output_path": "edit-output\\acc012\\missing.bin"})
    check("c1 distinct content excludes c2 resource", not missing_two.get("ok"), missing_two)
    restore(client, lineage, c2, "restore-c2-parent")
    check("c2 both shared resources recover", export_resource(client, "T022.Shared.One", "one-c2.bin", shared)
          and export_resource(client, "T022.Shared.Two", "two-c2.bin", shared))

    lineage4, c4, p4, fp4 = commit(client, [rename_branch], "c4-new-branch")
    check("new branch parent is restored old checkpoint", lineage4 == lineage and p4 == c2, {"parent": p4, "expected": c2})
    view = history(client, lineage, "branched")
    nodes = [row for row in view.get("checkpoints", []) if isinstance(row, dict)]
    children = [row for row in nodes if str(row.get("parent_checkpoint_id") or "") == c2]
    check("original and new branches both browsable", {str(row.get("checkpoint_id")) for row in children} == {c3, c4}, children)

    restore(client, lineage, c3, "restore-original-branch")
    original_name = type_name(client, simple_token)
    status(client, "original-branch")
    original_live = live_fingerprint(client)
    check("original branch restores distinct name/fingerprint", "T022Original" in original_name
          and original_live == fp3, {"name": original_name, "live": original_live, "expected": fp3})
    restore(client, lineage, c4, "restore-new-branch")
    branch_name = type_name(client, simple_token)
    status(client, "new-branch")
    branch_live = live_fingerprint(client)
    check("new branch restores distinct name/fingerprint", "T022Branch" in branch_name
          and branch_live == fp4, {"name": branch_name, "live": branch_live, "expected": fp4})

    final = inspect_package("final", lineage)
    final_files = files_snapshot("final")
    check("final single package and no orphan temp", len(final_files["files"]) == 1
          and final_files["files"][0]["path"] == package_path(lineage).name and not list(STORE.glob("*.tmp-*")), final_files)
    (EVIDENCE / "directory-snapshots.json").write_text(json.dumps(SNAPSHOTS, indent=2), encoding="utf-8")
    (EVIDENCE / "package-inspection.json").write_text(json.dumps(PACKAGE_REPORTS, indent=2), encoding="utf-8")
    (EVIDENCE / "final-summary.json").write_text(json.dumps({"lineage": lineage, "checkpoints": [c1, c2, c3, c4],
        "branch_parent": c2, "original_fingerprint": fp3, "branch_fingerprint": fp4,
        "final_package_sha256": final["sha256"], "passes": PASSES, "failures": FAILURES}, indent=2), encoding="utf-8")
    client.close()
    print(f"ACC012 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
