#!/usr/bin/env python3
"""ACC-012 source-path boundary: repeated C# compile/import through real MCP.

The contract intentionally keeps source documents and compile artifacts in
memory.  Checkpoints persist only frozen structured operations.  This driver
proves that boundary and the dedup/recovery of the source-derived method body.
"""
from __future__ import annotations

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
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe"
STORE = Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts\edit-checkpoints")
EVIDENCE = Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts\acc012-source")
FAILURES: list[str] = []
PASSES: list[str] = []

SOURCE = """namespace ImportHost
{
    public static class Program
    {
        public static int Main()
        {
            return 777;
        }
    }
}
"""


def configure_isolation(context) -> None:
    global URL, FIXTURE, STORE, EVIDENCE
    context.validate()
    URL = context.mcp_url
    FIXTURE = context.fixture("ImportHost/ImportHost.exe" if context.architecture == "x64" else "ImportHost-x86/ImportHost.exe")
    STORE = Path(context.checkpoint_store)
    EVIDENCE = Path(context.artifact_root) / "edit-tests" / context.run_id / "EDIT-ACC-012-SOURCE"


def rid() -> str:
    return str(uuid.uuid4())


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check(name: str, condition: bool, detail: object = "") -> None:
    (PASSES if condition else FAILURES).append(name)
    print(f"{'PASS' if condition else 'FAIL'} {name}" + ("" if condition else " " + str(detail)), flush=True)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        value = client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex); start = text.find("{")
        try:
            value = json.loads(text[start:]) if start >= 0 else None
        except json.JSONDecodeError:
            value = None
        if not isinstance(value, dict):
            value = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:400]}}
    print("MCP " + tool + " " + json.dumps(value, ensure_ascii=False, sort_keys=True), flush=True)
    return value


def payload(value: dict) -> dict:
    row = value.get("result") if isinstance(value, dict) else None
    return row if isinstance(row, dict) else {}


def live_fingerprint(client: DnSpyClient) -> str:
    begun = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    core = payload(begun); tx = str(core.get("transaction", {}).get("transaction_id", ""))
    value = str(core.get("fingerprints", {}).get("current_live", ""))
    if tx:
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    return value


def store_snapshot(label: str) -> dict:
    rows = []
    if STORE.is_dir():
        for path in sorted((p for p in STORE.rglob("*") if p.is_file()), key=lambda p: str(p).lower()):
            data = path.read_bytes()
            rows.append({"path": str(path.relative_to(STORE)).replace("\\", "/"), "length": len(data), "sha256": sha(data)})
    row = {"label": label, "files": rows}
    print("DISK " + json.dumps(row, sort_keys=True), flush=True)
    return row


def commit_source(client: DnSpyClient, label: str, marker_version: str) -> dict:
    begun = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    core = payload(begun); tx = str(core.get("transaction", {}).get("transaction_id", "")); revision = int(core.get("transaction", {}).get("work_revision", 0))
    check(label + " begin", bool(tx), begun)
    compiled = call(client, "edit_compile", {"request_id": rid(), "assembly_name": "ImportHost",
        "compilation_kind": "edit_class", "documents": [{"path": "T022Same.cs", "content": SOURCE}]})
    compile_row = payload(compiled).get("compile", {}); compile_id = str(compile_row.get("compile_id", ""))
    check(label + " compile same source", bool(compiled.get("ok")) and bool(compile_row.get("success")) and compile_id.startswith("compile-"), compiled)
    imported = call(client, "edit_import", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "compile_id": compile_id,
        "targets": [{"compiled": "ImportHost.Program::Main()", "action": "replace_body"}]})
    revision = int(payload(imported).get("transaction", {}).get("work_revision", revision))
    rows = payload(imported).get("import", {}).get("rows", [])
    check(label + " import frozen body", bool(imported.get("ok")) and any(r.get("kind") == "method_body_replace" for r in rows if isinstance(r, dict)), imported)
    marker = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
        "expected_revision": revision, "operation": {"kind": "assembly_update", "version": marker_version}})
    revision = int(payload(marker).get("transaction", {}).get("work_revision", revision))
    check(label + " distinct marker", bool(marker.get("ok")), marker)
    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review = payload(reviewed).get("review", {}); required = [str(v) for v in review.get("required_confirmation_ids", [])]
    committed = call(client, "edit_commit", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review.get("review_id", ""), "review_revision": review.get("review_revision", revision), "confirmed_risk_ids": required})
    result = payload(committed); checkpoint = result.get("checkpoint", {})
    row = {"label": label, "source_sha256": sha(SOURCE.encode()), "compile_id": compile_id,
        "compile_assembly": compile_row.get("assembly", {}), "compile_pdb": compile_row.get("portable_pdb", {}),
        "lineage_id": str(result.get("history", {}).get("lineage_id", "")),
        "checkpoint_id": str(checkpoint.get("checkpoint_id", "")),
        "parent_checkpoint_id": str(checkpoint.get("parent_checkpoint_id") or ""),
        "live_fingerprint": str(result.get("fingerprints", {}).get("after", ""))}
    check(label + " commit", bool(committed.get("ok")) and bool(row["lineage_id"]) and bool(row["checkpoint_id"]), committed)
    print("COMMIT " + json.dumps(row, sort_keys=True), flush=True)
    row["disk"] = store_snapshot(label)
    return row


def restore(client: DnSpyClient, lineage: str, checkpoint: str, label: str) -> dict:
    live = live_fingerprint(client)
    assessed = call(client, "edit_restore", {"request_id": rid(), "lineage_id": lineage, "checkpoint_id": checkpoint, "action": "assess"})
    replay = payload(assessed).get("replay", {})
    args = {"request_id": rid(), "lineage_id": lineage, "checkpoint_id": checkpoint, "action": "apply",
            "replay_id": replay.get("replay_id", ""), "expected_live_fingerprint": live}
    if replay.get("classification") == "validated_drift":
        args["confirm_validated_drift"] = True
    applied = call(client, "edit_restore", args)
    check(label + " restore", bool(assessed.get("ok")) and bool(applied.get("ok")), {"assess": assessed, "apply": applied})
    return {"assessment": replay, "apply": payload(applied), "disk": store_snapshot(label)}


def il_has_777(client: DnSpyClient) -> bool:
    methods = call(client, "list_methods", {"assembly_name": "ImportHost", "type_full_name": "ImportHost.Program"})
    items = methods.get("items") or methods.get("Items") or []
    main = next((r for r in items if isinstance(r, dict) and str(r.get("name") or r.get("Name")) == "Main"), {})
    raw = main.get("token") or main.get("Token"); token = raw if isinstance(raw, str) else (f"0x{int(raw):08x}" if raw is not None else "")
    response = call(client, "get_method_il", {"assembly_name": "ImportHost", "type_full_name": "ImportHost.Program",
                                               "method_name": "Main", "method_token": token})
    return "777" in json.dumps(response, ensure_ascii=False)


def inspect(lineage: str, commits: list[dict]) -> dict:
    package = STORE / f"{lineage}.dnspy-mcp-checkpoints"; raw = package.read_bytes()
    EVIDENCE.mkdir(parents=True, exist_ok=True); copy = EVIDENCE / "source-boundary.dnspy-mcp-checkpoints"; shutil.copyfile(package, copy)
    with zipfile.ZipFile(package) as archive:
        entries = [];
        contents = {}
        for info in sorted(archive.infolist(), key=lambda x: x.filename):
            data = archive.read(info.filename); contents[info.filename] = data
            entries.append({"path": info.filename, "length": len(data), "sha256": sha(data), "compressed_length": info.compress_size})
        manifest = json.loads(contents["manifest.json"])
        operations = {name: json.loads(data) for name, data in contents.items() if name.startswith("operations/")}
    source_bytes = SOURCE.encode(); source_sha = sha(source_bytes)
    text = json.dumps(operations, sort_keys=True)
    payloads = [row for row in entries if row["path"].startswith("payloads/")]
    payload_ref_counts = {row["sha256"]: text.count(row["sha256"]) for row in payloads}
    duplicates = {}
    for row in entries:
        duplicates.setdefault(row["sha256"], []).append(row["path"])
    duplicates = {key: value for key, value in duplicates.items() if len(value) > 1}
    report = {"source_sha256": source_sha, "source_length": len(source_bytes), "package": str(package),
        "archive_copy": str(copy), "package_length": len(raw), "package_sha256": sha(raw), "entries": entries,
        "manifest": manifest, "operations": operations, "payload_ref_counts": payload_ref_counts,
        "duplicate_entry_content": duplicates,
        "source_sha_is_entry": any(row["sha256"] == source_sha for row in entries),
        "source_bytes_present_in_uncompressed_entry": [name for name, data in contents.items() if source_bytes in data],
        "source_sha_text_present": [name for name, data in contents.items() if source_sha.encode() in data],
        "commits": commits}
    (EVIDENCE / "source-boundary-inspection.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("PACKAGE " + json.dumps(report, sort_keys=True), flush=True)
    return report


def main() -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    client = DnSpyClient(URL, client_name="p03-vm-acc012-source", timeout=120); client.initialize()
    opened = call(client, "open_files", {"paths": [FIXTURE]}); check("fixture opened", "error" not in opened, opened)
    initial = store_snapshot("initial"); check("fresh checkpoint store", not initial["files"], initial)
    first = commit_source(client, "source-commit-1", "1.0.0.1")
    second = commit_source(client, "source-commit-2", "1.0.0.2")
    check("same lineage and linear parent", first["lineage_id"] == second["lineage_id"] and second["parent_checkpoint_id"] == first["checkpoint_id"], [first, second])
    report = inspect(first["lineage_id"], [first, second])
    refs = report["payload_ref_counts"]
    check("source-derived body is one physical payload referenced by both nodes", len(refs) == 1 and next(iter(refs.values()), 0) >= 2, refs)
    check("raw C# source is intentionally absent from checkpoint package", not report["source_sha_is_entry"]
          and not report["source_bytes_present_in_uncompressed_entry"] and not report["source_sha_text_present"], report)
    check("single baseline single final no temp", sum(r["path"] == "baseline/module.bin" for r in report["entries"]) == 1
          and len(store_snapshot("after-two-commits")["files"]) == 1 and not list(STORE.glob("*.tmp-*")))
    restore(client, first["lineage_id"], first["checkpoint_id"], "restore-source-node-1")
    check("source node 1 restores compiled semantics and fingerprint", il_has_777(client) and live_fingerprint(client) == first["live_fingerprint"])
    restore(client, first["lineage_id"], second["checkpoint_id"], "restore-source-node-2")
    check("source node 2 restores compiled semantics and fingerprint", il_has_777(client) and live_fingerprint(client) == second["live_fingerprint"])
    final = store_snapshot("final"); check("final store clean", len(final["files"]) == 1 and not list(STORE.glob("*.tmp-*")), final)
    (EVIDENCE / "source-boundary-summary.json").write_text(json.dumps({"source_sha256": sha(SOURCE.encode()),
        "source_persisted": False, "applicable_dedup_payload": "source-derived normalized method body",
        "lineage": first["lineage_id"], "checkpoints": [first["checkpoint_id"], second["checkpoint_id"]],
        "passes": PASSES, "failures": FAILURES}, indent=2), encoding="utf-8")
    client.close(); print(f"ACC012-SOURCE {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
