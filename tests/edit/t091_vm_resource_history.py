#!/usr/bin/env python3
"""T091 private-host public resource value and history regression."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import subprocess
import sys
import traceback
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402


def ident() -> str:
    return str(uuid.uuid4())


def body(reply: dict) -> dict:
    value = reply.get("result")
    return value if isinstance(value, dict) else {}


def code(reply: dict) -> str:
    value = reply.get("error")
    return str(value.get("code", "")) if isinstance(value, dict) else ""


def main() -> int:
    parser = argparse.ArgumentParser()
    for name in ("url", "fixture", "evidence", "reader-dotnet", "reader-dll", "output-prefix"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    fixture = Path(args.fixture)
    evidence = {"fixture": str(fixture), "source_sha256": None, "calls": [], "checks": [],
                "readbacks": [], "status": "FAILED", "failure": None}
    client = None
    active = ""

    def check(condition: bool, label: str, detail=None) -> None:
        row = {"label": label, "pass": bool(condition), "detail": detail}
        evidence["checks"].append(row)
        print(json.dumps(row, default=str), flush=True)
        if not condition:
            raise AssertionError(label)

    def call(tool: str, arguments: dict) -> dict:
        wire = None
        try:
            wire = client.request("tools/call", {"name": tool, "arguments": arguments})
            if not isinstance(wire, dict):
                raise ValueError("tools/call envelope is not an object")
            if "structuredContent" in wire:
                reply = wire["structuredContent"]
            else:
                text = next(item["text"] for item in wire.get("content", []) if item.get("type") == "text")
                reply = json.loads(text)
        except Exception as exc:  # transport is recorded separately from business errors
            reply = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": str(exc)}}
        row = {"tool": tool, "request": arguments, "response": reply, "wire_result": wire}
        evidence["calls"].append(row)
        print(json.dumps(row, ensure_ascii=False, default=str), flush=True)
        return reply

    def readback(path: str, profile: str, blob: bool = False) -> None:
        command = [args.reader_dotnet, args.reader_dll, "verify-blob" if blob else "verify", path, profile]
        process = subprocess.run(command, text=True, capture_output=True, timeout=90, check=False)
        row = {"command": command, "exit_code": process.returncode,
               "stdout": process.stdout, "stderr": process.stderr}
        evidence["readbacks"].append(row)
        try:
            result = json.loads(process.stdout.strip())
        except json.JSONDecodeError:
            result = {}
        check(process.returncode == 0 and result.get("status") == "PASS" and
              result.get("profile") == profile and result.get("value_kinds") == 13 and
              result.get("storage_codes") == 14 and result.get("custom_raw") is True,
              "independent BCL readback " + profile + (" blob" if blob else " image"), row)

    def resource_readback(label: str, profile: str) -> None:
        result = call("edit_resource_export", {"request_id": ident(), "assembly_name": "TestIL",
                      "resource_name": "T091.Values.resources",
                      "output_path": args.output_prefix + "\\" + label + ".resources"})
        path = body(result).get("export", {}).get("path", "")
        check(result.get("ok") is True and Path(path).is_file(), "resource export " + label, result)
        readback(path, profile, True)

    def commit(label: str, entries: list[dict]) -> tuple[str, str]:
        nonlocal active
        begun = call("edit_begin", {"assembly_name": "TestIL", "request_id": ident()})
        tx = body(begun).get("transaction", {})
        active = tx.get("transaction_id", "")
        revision = tx.get("work_revision", -1)
        check(begun.get("ok") is True and active and revision == 0, label + " begin", begun)
        if label == "first":
            review_args = {"request_id": ident(), "transaction_id": active, "expected_revision": revision}
            reviewed = call("edit_review", review_args)
            review_id = body(reviewed).get("review", {}).get("review_id")
            check(reviewed.get("ok") is True and review_id, "baseline review", reviewed)
            before = body(call("edit_status", {}))
            for bad in ({"name": "i4", "value_kind": "u4", "value": 1},
                        {"name": "char", "value_kind": "char", "value": "R"}):
                rejected = call("edit_apply", {"request_id": ident(), "transaction_id": active,
                                 "expected_revision": revision, "operation": {"kind": "managed_resource_update",
                                 "target": {"name": "T091.Values.resources"}, "entry": bad}})
                after = body(call("edit_status", {}))
                check(code(rejected) == "EDIT_VALIDATION_FAILED" and
                      after.get("transaction", {}).get("work_revision") == revision and
                      after.get("fingerprints") == before.get("fingerprints") and
                      after.get("review", {}).get("review_id") == review_id,
                      "rejected " + bad["name"] + " has no publication side effects",
                      {"rejected": rejected, "before": before, "after": after})
            replay = call("edit_review", review_args)
            check(body(replay).get("review", {}).get("review_id") == review_id,
                  "rejected edits preserve review cache", replay)
        for entry in entries:
            applied = call("edit_apply", {"request_id": ident(), "transaction_id": active,
                           "expected_revision": revision, "operation": {"kind": "managed_resource_update",
                           "target": {"name": "T091.Values.resources"}, "entry": entry}})
            new_revision = body(applied).get("transaction", {}).get("work_revision", -1)
            check(applied.get("ok") is True and new_revision == revision + 1,
                  label + " apply " + entry["name"], applied)
            revision = new_revision
        reviewed = call("edit_review", {"request_id": ident(), "transaction_id": active,
                                        "expected_revision": revision})
        review = body(reviewed).get("review", {})
        check(reviewed.get("ok") is True and review.get("review_id"), label + " review", reviewed)
        committed = call("edit_commit", {"request_id": ident(), "transaction_id": active,
                          "expected_revision": revision, "review_id": review["review_id"],
                          "review_revision": review.get("review_revision", revision),
                          "confirmed_risk_ids": review.get("required_confirmation_ids") or []})
        result = body(committed)
        lineage = result.get("history", {}).get("lineage_id", "")
        checkpoint = result.get("checkpoint", {}).get("checkpoint_id", "")
        check(committed.get("ok") is True and lineage and checkpoint, label + " commit", committed)
        active = ""
        exported = call("edit_export", {"request_id": ident(), "lineage_id": lineage,
                        "checkpoint_id": checkpoint,
                        "output_path": args.output_prefix + "\\" + label + ".dll"})
        path = body(exported).get("output", {}).get("path", "")
        check(exported.get("ok") is True and Path(path).is_file(), label + " image export", exported)
        readback(path, label)
        resource_readback(label, label)
        return lineage, checkpoint

    try:
        check(fixture.is_file(), "fixture exists", str(fixture))
        evidence["source_sha256"] = hashlib.sha256(fixture.read_bytes()).hexdigest()
        readback(str(fixture), "baseline")
        client = DnSpyClient(args.url, client_name="t091-resource-history", timeout=180)
        client.initialize()
        opened = call("open_files", {"paths": [str(fixture)]})
        check(opened.get("failed_count") == 0 and opened.get("loaded_count") == 1,
              "fixture opened", opened)
        entries = [
            {"name": "text", "value_kind": "string", "value": "edited"},
            {"name": "boolean", "value_kind": "boolean", "value": False},
            {"name": "i1", "value_kind": "i1", "value": -8},
            {"name": "u1", "value_kind": "u1", "value": 251},
            {"name": "i2", "value_kind": "i2", "value": -30001},
            {"name": "u2", "value_kind": "u2", "value": 60001},
            {"name": "i4", "value_kind": "i4", "value": -2000000001},
            {"name": "u4", "value_kind": "u4", "value": 4000000001},
            {"name": "i8", "value_kind": "i8", "value": -9007199254740995},
            {"name": "u8", "value_kind": "u8", "value": 18446744073709551611},
            {"name": "r4", "value_kind": "r4", "value": 2.5},
            {"name": "r8", "value_kind": "r8", "value": -3.25},
            {"name": "bytes", "value_kind": "bytes", "value": base64.b64encode(bytes([9, 8, 7, 6])).decode()},
            {"name": "stream", "value_kind": "bytes", "value": base64.b64encode(bytes([10, 11, 12])).decode()},
        ]
        lineage, first = commit("first", entries)
        second_lineage, second = commit("second", [{"name": "text", "value_kind": "string", "value": "followup"}])
        check(second_lineage == lineage and second != first, "second checkpoint descends in lineage")
        undone = call("edit_undo", {"request_id": ident(), "lineage_id": lineage,
                                    "expected_checkpoint_id": second})
        check(undone.get("ok") is True and body(undone).get("history", {}).get("head_checkpoint_id") == first,
              "undo to first", undone)
        resource_readback("undo", "first")
        redone = call("edit_redo", {"request_id": ident(), "lineage_id": lineage,
                                    "expected_checkpoint_id": first})
        check(redone.get("ok") is True and body(redone).get("history", {}).get("head_checkpoint_id") == second,
              "redo to second", redone)
        resource_readback("redo", "second")
        probe = call("edit_begin", {"request_id": ident(), "assembly_name": "TestIL"})
        fingerprint = body(probe).get("fingerprints", {}).get("current_live", "")
        transaction = body(probe).get("transaction", {}).get("transaction_id", "")
        check(probe.get("ok") is True and fingerprint and transaction, "restore fingerprint", probe)
        rolled = call("edit_rollback", {"request_id": ident(), "transaction_id": transaction})
        check(rolled.get("ok") is True, "fingerprint probe rollback", rolled)
        assessed = call("edit_restore", {"request_id": ident(), "lineage_id": lineage,
                                         "checkpoint_id": first, "action": "assess"})
        replay = body(assessed).get("replay", {})
        check(assessed.get("ok") is True and replay.get("replay_id"), "restore assess", assessed)
        restore_args = {"request_id": ident(), "lineage_id": lineage, "checkpoint_id": first,
                        "action": "apply", "replay_id": replay["replay_id"],
                        "expected_live_fingerprint": fingerprint}
        if replay.get("classification") == "validated_drift":
            restore_args["confirm_validated_drift"] = True
        restored = call("edit_restore", restore_args)
        check(restored.get("ok") is True, "restore apply", restored)
        resource_readback("restore", "first")
        check(hashlib.sha256(fixture.read_bytes()).hexdigest() == evidence["source_sha256"],
              "source fixture unchanged")
        evidence["status"] = "PASS"
        return 0
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc),
                               "traceback": traceback.format_exc()}
        return 1
    finally:
        if active and client is not None:
            call("edit_rollback", {"request_id": ident(), "transaction_id": active})
        output = Path(args.evidence)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2, default=str) + "\n", encoding="utf-8")
        print(json.dumps({"status": evidence["status"], "failure": evidence["failure"],
                          "evidence": args.evidence}), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
