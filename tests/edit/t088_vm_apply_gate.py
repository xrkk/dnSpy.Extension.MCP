#!/usr/bin/env python3
"""T088 public apply publication chain on one private fixed-VM host."""
from __future__ import annotations

import argparse
import hashlib
import json
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
    p = argparse.ArgumentParser()
    p.add_argument("--url", required=True)
    p.add_argument("--fixture", required=True)
    p.add_argument("--evidence", required=True)
    a = p.parse_args()
    evidence = {"fixture": a.fixture, "fixture_sha256": "", "calls": [], "checks": [],
                "status": "FAILED", "failure": None}
    client = None
    active = ""

    def call(tool: str, args: dict) -> dict:
        wire = None
        try:
            wire = client.request("tools/call", {"name": tool, "arguments": args})
            if not isinstance(wire, dict):
                raise ValueError("tools/call result is not an object")
            if "structuredContent" in wire:
                reply = wire["structuredContent"]
            else:
                text = next(item["text"] for item in wire.get("content", [])
                            if item.get("type") == "text")
                reply = json.loads(text)
        except Exception as ex:
            reply = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": str(ex)}}
        row = {"tool": tool, "request": args, "response": reply, "wire_result": wire}
        evidence["calls"].append(row)
        print(json.dumps(row, ensure_ascii=False, default=str), flush=True)
        return reply

    def check(ok: bool, label: str, detail: object = None) -> None:
        row = {"label": label, "pass": bool(ok), "detail": detail}
        evidence["checks"].append(row)
        print(json.dumps(row, ensure_ascii=False, default=str), flush=True)
        if not ok:
            raise AssertionError(label)

    try:
        fixture = Path(a.fixture)
        check(fixture.is_file(), "fixture exists", a.fixture)
        evidence["fixture_sha256"] = hashlib.sha256(fixture.read_bytes()).hexdigest()
        client = DnSpyClient(a.url, client_name="t088-apply-gate", timeout=180)
        client.initialize()
        opened = call("open_files", {"paths": [a.fixture]})
        check(opened.get("failed_count") == 0 and opened.get("loaded_count") == 1,
              "private fixture opened", opened)
        methods = call("list_methods", {"assembly_name": "ImportHost",
                                        "type_full_name": "ImportHost.Program"})
        rows = methods.get("items") or methods.get("Items") or []
        main_row = next((r for r in rows if (r.get("name") or r.get("Name")) == "Main"), None)
        token = main_row.get("token") or main_row.get("Token") if main_row else None
        if isinstance(token, int):
            token = f"0x{token:08x}"
        check(isinstance(token, str) and token.startswith("0x06"), "Main token", token)
        begin = call("edit_begin", {"assembly_name": "ImportHost", "request_id": ident()})
        tx_row = body(begin).get("transaction", {})
        active = str(tx_row.get("transaction_id", ""))
        revision = int(tx_row.get("work_revision", 0))
        check(begin.get("ok") is True and active and revision == 0, "begin", begin)
        initial_status = body(call("edit_status", {}))
        review_request = ident()
        review_args = {"request_id": review_request, "transaction_id": active, "expected_revision": revision}
        reviewed = call("edit_review", review_args)
        review_id = body(reviewed).get("review", {}).get("review_id")
        check(reviewed.get("ok") is True and review_id, "baseline review", reviewed)
        before = body(call("edit_status", {}))
        failed_request = ident()
        invalid = call("edit_apply", {"request_id": failed_request, "transaction_id": active,
                    "expected_revision": revision, "operation": {"kind": "method_body_replace",
                    "target": {"token": token}, "body": {"init_locals": False, "max_stack": 1,
                    "locals": [], "exception_handlers": [],
                    "instructions": [{"opcode": "pop"}, {"opcode": "ret"}]}}})
        after = body(call("edit_status", {}))
        check(code(invalid) == "EDIT_VALIDATION_FAILED" and
              after.get("transaction", {}).get("work_revision") == revision and
              after.get("fingerprints") == before.get("fingerprints") and
              after.get("risks") == before.get("risks") and
              isinstance(after.get("review"), dict) and after["review"].get("review_id") == review_id and
              hashlib.sha256(fixture.read_bytes()).hexdigest() == evidence["fixture_sha256"],
              "rejected apply leaves revision/fingerprints/risks/review/source exact",
              {"invalid": invalid, "before": before, "after": after})
        replay = call("edit_review", review_args)
        check(body(replay).get("review", {}).get("review_id") == review_id,
              "prior review cache still usable", replay)

        # Reuse the failed request ID. It must not have cached a success.
        added = call("edit_apply", {"request_id": failed_request, "transaction_id": active,
                    "expected_revision": revision, "operation": {"kind": "type_add",
                    "namespace": "T088", "name": "Created", "base_type": "System.Object"}})
        created = body(added).get("created_object_ids") or []
        revision = int(body(added).get("transaction", {}).get("work_revision", -1))
        check(added.get("ok") is True and revision == 1 and len(created) == 1,
              "type_add crosses writer gate and allocates ID; failed request uncached", added)
        check(body(call("edit_status", {})).get("review") is None,
              "successful apply clears old review", None)
        method = call("edit_apply", {"request_id": ident(), "transaction_id": active,
                    "expected_revision": revision, "operation": {"kind": "method_add",
                    "owner_type": {"object_id": created[0]}, "name": "Added",
                    "signature": {"return_type": "System.Void", "has_this": False,
                                  "generic_parameters": [], "parameters": []},
                    "attributes": 22,
                    "body": {"init_locals": False, "max_stack": 1, "locals": [],
                             "exception_handlers": [], "instructions": [{"opcode": "ret"}]}}})
        revision = int(body(method).get("transaction", {}).get("work_revision", -1))
        check(method.get("ok") is True and revision == 2 and
              len(body(method).get("created_object_ids") or []) == 1,
              "method_add resolves prior object ID after writer gate", method)
        reviewed = call("edit_review", {"request_id": ident(), "transaction_id": active,
                                        "expected_revision": revision})
        review = body(reviewed).get("review", {})
        required = review.get("required_confirmation_ids") or []
        check(reviewed.get("ok") is True and review.get("review_id"), "review accepted candidate", reviewed)
        committed = call("edit_commit", {"request_id": ident(), "transaction_id": active,
                    "expected_revision": revision, "review_id": review["review_id"],
                    "review_revision": revision, "confirmed_risk_ids": required})
        commit = body(committed)
        lineage = commit.get("history", {}).get("lineage_id", "")
        head = commit.get("checkpoint", {}).get("checkpoint_id", "")
        check(committed.get("ok") is True and lineage and head,
              "success commit publishes history", committed)
        active = ""
        added_methods = call("list_methods", {"assembly_name": "ImportHost",
                                              "type_full_name": "T088.Created"})
        check(any((r.get("name") or r.get("Name")) == "Added" for r in
                  (added_methods.get("items") or added_methods.get("Items") or [])),
              "committed method visible", added_methods)
        undone = call("edit_undo", {"request_id": ident(), "lineage_id": lineage,
                                    "expected_checkpoint_id": head})
        root = body(undone).get("history", {}).get("head_checkpoint_id", "")
        check(undone.get("ok") is True and root and root != head, "undo", undone)
        redone = call("edit_redo", {"request_id": ident(), "lineage_id": lineage,
                                    "expected_checkpoint_id": root})
        check(redone.get("ok") is True and
              body(redone).get("history", {}).get("head_checkpoint_id") == head,
              "redo", redone)
        check(hashlib.sha256(fixture.read_bytes()).hexdigest() == evidence["fixture_sha256"],
              "source file unchanged after navigation")
        evidence["status"] = "PASS"
        return 0
    except Exception as ex:
        evidence["failure"] = {"type": type(ex).__name__, "message": str(ex),
                               "traceback": traceback.format_exc()}
        return 1
    finally:
        if active and client is not None:
            call("edit_rollback", {"request_id": ident(), "transaction_id": active})
        output = Path(a.evidence)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(evidence, ensure_ascii=False, indent=2, default=str) + "\n")
        print(json.dumps({"status": evidence["status"], "failure": evidence["failure"],
                          "evidence": a.evidence}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
