#!/usr/bin/env python3
"""T094 R02 public boundary matrix and mixed resource v1/v2 history."""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import traceback
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402
from dnspy_mcp.client import DnSpyProtocolError  # noqa: E402
from t094_vm_resource_precision import ident, body, code  # noqa: E402

RESOURCE = "T091.Values.resources"
DATE_VECTORS = [
    ("0", "Unspecified"),
    ("3155378975999999999", "Unspecified"),
    ("638661942000000000", "Unspecified"),
    ("5250348104427387904", "Utc"),
    ("-8584709950854775808", "Local"),
    ("-8584709914854775808", "Local"),
]
VECTORS = [
    {"char": {"code_unit": 0}, "decimal": {"lo": 0, "mid": 0, "hi": 0, "negative": False, "scale": 0}, "span": {"ticks": "0"}},
    {"char": {"code_unit": 65535}, "decimal": {"lo": 0, "mid": 0, "hi": 0, "negative": True, "scale": 28}, "span": {"ticks": "-9223372036854775808"}},
    {"char": {"code_unit": 55296}, "decimal": {"lo": 4294967295, "mid": 4294967295, "hi": 4294967295, "negative": False, "scale": 0}, "span": {"ticks": "9223372036854775807"}},
    {"char": {"code_unit": 56320}, "decimal": {"lo": 25, "mid": 0, "hi": 0, "negative": True, "scale": 1}, "span": {"ticks": "-1"}},
    {"char": {"code_unit": 65}, "decimal": {"lo": 0, "mid": 0, "hi": 0, "negative": False, "scale": 28}, "span": {"ticks": "-42"}},
    {"char": {"code_unit": 66}, "decimal": {"lo": 0, "mid": 0, "hi": 0, "negative": True, "scale": 0}, "span": {"ticks": "1"}},
]
for item, (binary, kind) in zip(VECTORS, DATE_VECTORS, strict=True):
    item["date"] = {"binary": binary, "kind": kind}


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files(root: Path) -> dict[str, str]:
    return {str(p.relative_to(root)): sha(p) for p in root.rglob("*") if p.is_file()}


def main() -> int:
    parser = argparse.ArgumentParser()
    for name in ("url", "fixture", "artifact-root", "evidence", "reader-dotnet", "reader-dll", "output-prefix"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    fixture, artifact, evidence_path = Path(args.fixture), Path(args.artifact_root), Path(args.evidence)
    evidence = {"status": "FAILED", "fixture": str(fixture), "fixture_sha256": None,
                "runtime": {"host_timezone": None}, "vectors": VECTORS, "calls": [], "checks": [],
                "readbacks": [], "packages": [], "failure": None}
    client = None
    active = ""
    checkpoints: list[str] = []
    profiles: dict[str, dict] = {}
    lineage = ""

    def check(ok: bool, label: str, detail=None) -> None:
        row = {"label": label, "pass": bool(ok), "detail": detail}
        evidence["checks"].append(row)
        print(json.dumps(row, ensure_ascii=False, default=str), flush=True)
        if not ok:
            raise AssertionError(label)

    def call(tool: str, arguments: dict, schema_error: bool = False) -> dict:
        wire = None
        try:
            wire = client.request("tools/call", {"name": tool, "arguments": arguments})
            if "structuredContent" in wire:
                reply = wire["structuredContent"]
            else:
                reply = json.loads(next(x["text"] for x in wire.get("content", []) if x.get("type") == "text"))
        except DnSpyProtocolError as exc:
            if not schema_error or exc.code != -32602:
                raise
            raw = exc.response.json() if exc.response is not None else None
            wire = raw
            reply = {"schema_error": exc.code, "message": str(exc), "data": exc.data}
        evidence["calls"].append({"tool": tool, "request": arguments, "wire": wire, "response": reply})
        print(json.dumps({"tool": tool, "request": arguments, "response": reply}, ensure_ascii=False, default=str), flush=True)
        return reply

    def expected_file(label: str, profile: dict) -> Path:
        path = evidence_path.parent / ("expected-" + label + ".json")
        with path.open("x", encoding="utf-8") as stream:
            json.dump({"profile": label, **profile}, stream, ensure_ascii=False, indent=2)
        return path

    def read(path: str, label: str, profile: dict) -> None:
        expected = expected_file(label, profile)
        command = [args.reader_dotnet, args.reader_dll, "verify-matrix", path, str(fixture), str(expected)]
        process = subprocess.run(command, capture_output=True, text=True, timeout=90, check=False)
        try:
            result = json.loads(process.stdout.strip())
        except json.JSONDecodeError:
            result = {}
        row = {"label": label, "command": command, "exit_code": process.returncode,
               "stdout": process.stdout, "stderr": process.stderr, "parsed": result}
        evidence["readbacks"].append(row)
        if evidence["runtime"]["host_timezone"] is None and result.get("timezone"):
            evidence["runtime"]["host_timezone"] = result["timezone"]
        check(process.returncode == 0 and result.get("status") == "PASS" and
              result.get("profile") == label and result.get("value_kinds") == 17 and
              result.get("storage_codes") == 18 and result.get("custom_raw") is True,
              "independent BCL " + label, row)

    def resource_read(label: str, profile: dict) -> None:
        reply = call("edit_resource_export", {"request_id": ident(), "assembly_name": "TestIL",
                     "resource_name": RESOURCE, "output_path": args.output_prefix + "\\" + label + ".resources"})
        path = body(reply).get("export", {}).get("path", "")
        check(reply.get("ok") is True and Path(path).is_file(), "resource export " + label, reply)
        read(path, label, profile)

    def negative_gate(tx: str, revision: int) -> None:
        review_args = {"request_id": ident(), "transaction_id": tx, "expected_revision": revision}
        review = call("edit_review", review_args)
        review_id = body(review).get("review", {}).get("review_id")
        check(review.get("ok") is True and bool(review_id), "negative pre-review", review)
        prior = body(call("edit_status", {}))
        old_files = files(artifact)
        negatives = [
            ("business-timespan-overflow", {"name": "span", "value_kind": "timespan", "value": {"ticks": "9223372036854775808"}}, "EDIT_VALIDATION_FAILED"),
            ("business-datetime-invalid", {"name": "date", "value_kind": "datetime", "value": {"binary": "9223372036854775807"}}, "EDIT_VALIDATION_FAILED"),
            ("schema-char-overflow", {"name": "char", "value_kind": "char", "value": {"code_unit": 65536}}, -32602),
            ("schema-decimal-extra", {"name": "decimal", "value_kind": "decimal", "value": {"lo": 0, "mid": 0, "hi": 0, "negative": False, "scale": 0, "extra": 1}}, -32602),
        ]
        for label, entry, wanted in negatives:
            args_apply = {"request_id": ident(), "transaction_id": tx, "expected_revision": revision,
                          "operation": {"kind": "managed_resource_update", "target": {"name": RESOURCE}, "entry": entry}}
            result = call("edit_apply", args_apply, schema_error=isinstance(wanted, int))
            after = body(call("edit_status", {}))
            actual_code = result.get("schema_error") if isinstance(wanted, int) else code(result)
            check(actual_code == wanted and after.get("transaction", {}).get("work_revision") == revision and
                  after.get("fingerprints") == prior.get("fingerprints") and
                  after.get("review", {}).get("review_id") == review_id and
                  after.get("history") == prior.get("history") and files(artifact) == old_files,
                  label + " zero-publication", {"reply": result, "before": prior, "after": after})
        repeated = call("edit_review", review_args)
        check(body(repeated).get("review", {}).get("review_id") == review_id,
              "negative review cache stable", repeated)

    def commit(label: str, entries: list[dict], profile: dict, negatives: bool = False) -> None:
        nonlocal active, lineage
        begun = call("edit_begin", {"request_id": ident(), "assembly_name": "TestIL"})
        tx = body(begun).get("transaction", {})
        active, revision = tx.get("transaction_id", ""), tx.get("work_revision", -1)
        check(begun.get("ok") is True and bool(active) and revision == 0, label + " begin", begun)
        if negatives:
            negative_gate(active, revision)
        for entry in entries:
            applied = call("edit_apply", {"request_id": ident(), "transaction_id": active,
                           "expected_revision": revision,
                           "operation": {"kind": "managed_resource_update", "target": {"name": RESOURCE}, "entry": entry}})
            got = body(applied).get("transaction", {}).get("work_revision")
            check(applied.get("ok") is True and got == revision + 1, label + " apply " + entry["name"], applied)
            revision = got
        reviewed = call("edit_review", {"request_id": ident(), "transaction_id": active,
                                        "expected_revision": revision})
        review = body(reviewed).get("review", {})
        check(reviewed.get("ok") is True and bool(review.get("review_id")), label + " review", reviewed)
        committed = call("edit_commit", {"request_id": ident(), "transaction_id": active,
                          "expected_revision": revision, "review_id": review["review_id"],
                          "review_revision": review.get("review_revision", revision),
                          "confirmed_risk_ids": review.get("required_confirmation_ids") or []})
        result = body(committed)
        got_lineage = result.get("history", {}).get("lineage_id", "")
        cp = result.get("checkpoint", {}).get("checkpoint_id", "")
        check(committed.get("ok") is True and bool(got_lineage) and bool(cp) and
              (not lineage or lineage == got_lineage) and cp not in checkpoints,
              label + " commit", committed)
        lineage = got_lineage
        checkpoints.append(cp)
        active = ""
        exported = call("edit_export", {"request_id": ident(), "lineage_id": lineage,
                         "checkpoint_id": cp, "output_path": args.output_prefix + "\\" + label + ".dll"})
        path = body(exported).get("output", {}).get("path", "")
        check(exported.get("ok") is True and Path(path).is_file(), label + " image export", exported)
        read(path, label + "-image", profile)
        resource_read(label + "-blob", profile)

    def inspect_package() -> None:
        path = artifact / "edit-checkpoints" / (lineage + ".dnspy-mcp-checkpoints")
        check(path.is_file(), "mixed package exists", str(path))
        with zipfile.ZipFile(path) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            nodes = sorted(manifest["checkpoints"], key=lambda x: x["sequence"])
            check(len(nodes) == 8 and manifest["format"] == "dnspy.edit.checkpoints.v2" and
                  manifest["head_checkpoint_id"] == checkpoints[-1], "mixed manifest", manifest)
            payloads = {x["sha256"] for x in manifest["payloads"]}
            check(all(hashlib.sha256(archive.read(x["entry"])).hexdigest() == x["sha256"]
                      for x in manifest["payloads"]), "mixed payload hashes", manifest["payloads"])
            for index, cp in enumerate(checkpoints):
                node = nodes[index + 1]
                raw_operations = archive.read(node["operation_entry"])
                operations = json.loads(raw_operations)
                rows = operations["operations"]
                expected_names = ["text"] if index == 0 else ["char", "decimal", "span", "date"]
                ok = (node["checkpoint_id"] == cp and
                      node["parent_checkpoint_id"] == nodes[index]["checkpoint_id"] and
                      node["operation_sha256"] == hashlib.sha256(raw_operations).hexdigest() and
                      operations["format"] == "dnspy.edit.op.v1" and
                      operations["checkpoint_id"] == cp and len(rows) == len(expected_names))
                details = []
                for offset, (row, name) in enumerate(zip(rows, expected_names)):
                    forward = row["forward"]
                    inverse = row["inverse"]
                    state = inverse.get("state", {}).get("managed_resource_update_state", {})
                    payload_sha = state.get("data_base64", {}).get("payload_sha256")
                    wanted_version = 1 if index == 0 else 2
                    wanted_value = "matrix-v1" if index == 0 else {
                        key: value for key, value in VECTORS[index - 1][name].items() if key != "kind"}
                    row_ok = (
                        row["kind"] == "managed_resource_update" and row["kind_version"] == wanted_version and
                        forward.get("entry", {}).get("name") == name and
                        forward.get("entry", {}).get("value_kind") ==
                        ("string" if index == 0 else {"char":"char", "decimal":"decimal", "span":"timespan", "date":"datetime"}[name]) and
                        forward.get("entry", {}).get("value") == wanted_value and
                        inverse.get("format") == "dnspy.edit.inverse.v1" and inverse.get("strategy") == "compiled_state" and
                        inverse.get("parent_checkpoint_id") == nodes[index]["checkpoint_id"] and
                        inverse.get("prefix_operation_count") == offset and inverse.get("operation_kind") == "managed_resource_update" and
                        state.get("name") == RESOURCE and payload_sha in payloads
                    )
                    ok = ok and row_ok
                    details.append({"name": name, "kind_version": row["kind_version"], "inverse_payload_sha256": payload_sha, "pass": row_ok})
                evidence["packages"].append({"checkpoint": cp, "sequence": index + 1, "rows": details, "pass": ok})
                check(ok, "package rows " + str(index), details)
        evidence["package"] = {"path": str(path), "sha256": sha(path), "bytes": path.stat().st_size}

    def restore(target: int, label: str) -> None:
        begun = call("edit_begin", {"request_id": ident(), "assembly_name": "TestIL"})
        transaction = body(begun).get("transaction", {}).get("transaction_id", "")
        fingerprint = body(begun).get("fingerprints", {}).get("current_live", "")
        check(begun.get("ok") is True and bool(transaction) and bool(fingerprint), label + " fingerprint", begun)
        rolled = call("edit_rollback", {"request_id": ident(), "transaction_id": transaction})
        check(rolled.get("ok") is True, label + " probe rollback", rolled)
        cp = checkpoints[target]
        assessed = call("edit_restore", {"request_id": ident(), "lineage_id": lineage,
                                         "checkpoint_id": cp, "action": "assess"})
        replay = body(assessed).get("replay", {})
        check(assessed.get("ok") is True and bool(replay.get("replay_id")), label + " assess", assessed)
        args_restore = {"request_id": ident(), "lineage_id": lineage, "checkpoint_id": cp,
                        "action": "apply", "replay_id": replay["replay_id"],
                        "expected_live_fingerprint": fingerprint}
        if replay.get("classification") == "validated_drift":
            args_restore["confirm_validated_drift"] = True
        applied = call("edit_restore", args_restore)
        check(applied.get("ok") is True, label + " apply", applied)
        resource_read(label, profiles["c" + str(target)])

    try:
        check(fixture.is_file() and artifact.is_dir() and evidence_path.parent.is_dir(),
              "private inputs exist", {"fixture": str(fixture), "artifact": str(artifact)})
        evidence["fixture_sha256"] = sha(fixture)
        baseline = {"text": "original", "changes": {}}
        read(str(fixture), "baseline", baseline)
        client = DnSpyClient(args.url, client_name="t094-resource-matrix", timeout=180)
        client.initialize()
        opened = call("open_files", {"paths": [str(fixture)]})
        check(opened.get("failed_count") == 0 and opened.get("loaded_count") == 1, "fixture opened", opened)
        text_profile = {"text": "matrix-v1", "changes": {}}
        profiles["c0"] = text_profile
        commit("c0", [{"name": "text", "value_kind": "string", "value": "matrix-v1"}], text_profile)
        for index, vector in enumerate(VECTORS, start=1):
            entries = [{"name": name, "value_kind": {"char":"char", "decimal":"decimal", "span":"timespan", "date":"datetime"}[name],
                        "value": {k:v for k,v in value.items() if k != "kind"}}
                       for name, value in vector.items()]
            profile = {"text": "matrix-v1", "changes": vector}
            profiles["c" + str(index)] = profile
            commit("c" + str(index), entries, profile, negatives=index == 1)
        inspect_package()
        for index in range(6, 0, -1):
            undone = call("edit_undo", {"request_id": ident(), "lineage_id": lineage,
                                        "expected_checkpoint_id": checkpoints[index]})
            check(undone.get("ok") is True and body(undone).get("history", {}).get("head_checkpoint_id") == checkpoints[index - 1],
                  "undo to c" + str(index - 1), undone)
            resource_read("undo-c" + str(index - 1), profiles["c" + str(index - 1)])
        for index in range(1, 7):
            redone = call("edit_redo", {"request_id": ident(), "lineage_id": lineage,
                                        "expected_checkpoint_id": checkpoints[index - 1]})
            check(redone.get("ok") is True and body(redone).get("history", {}).get("head_checkpoint_id") == checkpoints[index],
                  "redo to c" + str(index), redone)
            resource_read("redo-c" + str(index), profiles["c" + str(index)])
        restore(0, "restore-c0")
        restore(6, "restore-c6")
        check(sha(fixture) == evidence["fixture_sha256"], "source fixture unchanged")
        evidence["status"] = "PASS"
        return 0
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc),
                               "traceback": traceback.format_exc()}
        return 1
    finally:
        if active and client is not None:
            try:
                call("edit_rollback", {"request_id": ident(), "transaction_id": active})
            except Exception as exc:
                evidence["cleanup_error"] = str(exc)
        evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2, default=str) + "\n", encoding="utf-8")
        print(json.dumps({"status": evidence["status"], "failure": evidence["failure"],
                          "evidence": str(evidence_path)}), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
