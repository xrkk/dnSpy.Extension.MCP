#!/usr/bin/env python3
"""T087 public P07 lifecycle regression for one isolated dnSpy VM host.

The caller owns host deployment and cleanup. This driver records every MCP
request/response and rolls back its active transaction on failure.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import traceback
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402


def rid() -> str:
    return str(uuid.uuid4())


def data(reply: dict) -> dict:
    value = reply.get("result")
    return value if isinstance(value, dict) else {}


def error(reply: dict) -> str:
    value = reply.get("error")
    return str(value.get("code", "")) if isinstance(value, dict) else ""


def require(condition: bool, label: str, detail: object = None) -> None:
    print(json.dumps({"assertion": label, "pass": bool(condition), "detail": detail},
                     ensure_ascii=False, default=str), flush=True)
    if not condition:
        raise AssertionError(label)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--fixture-root", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--arch", choices=("x64", "x86"), required=True)
    args = parser.parse_args()
    root = Path(args.fixture_root)
    host = root / "ImportHost" / "ImportHost.exe"
    inbound = root / "ImportHost" / "InboundRef.exe"
    new_inbound = root / "ImportHost" / "InboundRenamed.exe"
    evidence = {"arch": args.arch, "url": args.url, "fixtures": {}, "calls": [],
                "assertions": [], "status": "FAILED", "failure": None}
    client = None
    other = None
    active = None

    def call(who: DnSpyClient, tool: str, values: dict) -> dict:
        wire = None
        try:
            wire = who.request("tools/call", {"name": tool, "arguments": values})
            if not isinstance(wire, dict):
                raise ValueError("tools/call result is not an object")
            if "structuredContent" in wire:
                reply = wire["structuredContent"]
            else:
                text = next(item["text"] for item in wire.get("content", [])
                            if item.get("type") == "text")
                reply = json.loads(text)
        except Exception as exc:  # transport errors remain failures, never business refusals
            reply = {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": str(exc)}}
        evidence["calls"].append({"session": "owner" if who is client else "foreign",
                                  "tool": tool, "request": values, "response": reply,
                                  "wire_result": wire})
        print(json.dumps(evidence["calls"][-1], ensure_ascii=False, default=str), flush=True)
        return reply

    def check(condition: bool, label: str, detail: object = None) -> None:
        evidence["assertions"].append({"label": label, "pass": bool(condition), "detail": detail})
        require(condition, label, detail)

    def begin(name: str) -> tuple[str, int]:
        nonlocal active
        reply = call(client, "edit_begin", {"assembly_name": name, "request_id": rid()})
        row = data(reply).get("transaction", {})
        tx = str(row.get("transaction_id", ""))
        check(reply.get("ok") is True and bool(tx), "begin " + name, reply)
        active = tx
        return tx, int(row.get("work_revision", 0))

    def scan(tx: str, revision: int) -> tuple[dict, dict]:
        reply = call(client, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx,
                                                 "expected_revision": revision})
        impact = data(reply).get("impact", {})
        check(reply.get("ok") is True and impact.get("scope") == "loaded_modules"
              and impact.get("scan_revision") == revision and impact.get("stale") is False,
              "scan success and scope", reply)
        return reply, impact

    def apply(tx: str, revision: int, operation: dict) -> tuple[int, dict]:
        reply = call(client, "edit_apply", {"request_id": rid(), "transaction_id": tx,
                                             "expected_revision": revision, "operation": operation})
        new_revision = data(reply).get("transaction", {}).get("work_revision")
        check(reply.get("ok") is True and new_revision == revision + 1,
              "apply " + operation["kind"], reply)
        return int(new_revision), reply

    def review(tx: str, revision: int) -> tuple[str, list[str], dict]:
        reply = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx,
                                              "expected_revision": revision})
        row = data(reply).get("review", {})
        review_id = str(row.get("review_id", ""))
        required = row.get("required_confirmation_ids") or []
        check(reply.get("ok") is True and bool(review_id), "review", reply)
        return review_id, required, reply

    try:
        for path in (host, inbound, new_inbound):
            check(path.is_file(), "fixture exists " + path.name, str(path))
            evidence["fixtures"][path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
        client = DnSpyClient(args.url, client_name="t087-impact-owner", timeout=180)
        other = DnSpyClient(args.url, client_name="t087-impact-foreign", timeout=180)
        client.initialize()
        other.initialize()
        opened = call(client, "open_files", {"paths": [str(host), str(inbound), str(new_inbound)]})
        check(opened.get("failed_count") == 0 and opened.get("loaded_count") == 3,
              "three fixtures loaded", opened)

        # Empty and nonidentity scans must beat the old unconditional inbound match.
        tx, rev = begin("ImportHost")
        data(call(client, "edit_status", {}))
        _, empty = scan(tx, rev)
        modules = empty.get("modules", [])
        check(empty.get("identity_operations") == [] and empty.get("inbound_references") == []
              and empty.get("risk_ids") == [] and any(row.get("name") == "InboundRef"
              and row.get("inbound_reference_count") == 0 for row in modules),
              "zero-operation empty report retains modules", empty)
        methods = call(client, "list_methods", {"assembly_name": "ImportHost",
                                                 "type_full_name": "ImportHost.Program"})
        rows = methods.get("items") or methods.get("Items") or []
        main_row = next((row for row in rows if row.get("name", row.get("Name")) == "Main"), None)
        token = main_row.get("token", main_row.get("Token")) if main_row else None
        if isinstance(token, int):
            token = f"0x{token:08x}"
        check(isinstance(token, str) and re.fullmatch(r"0x06[0-9a-fA-F]{6}", token) is not None,
              "Main token resolved", token)
        rev, _ = apply(tx, rev, {"kind": "method_update", "target": {"token": token},
                                 "name": "T087Main"})
        _, nonidentity = scan(tx, rev)
        check(nonidentity.get("identity_operations") == [] and nonidentity.get("inbound_references") == []
              and nonidentity.get("risk_ids") == [], "nonidentity empty report", nonidentity)
        before_reject = data(call(client, "edit_status", {}))
        wrong = call(other, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx,
                                                  "expected_revision": rev})
        conflict = call(client, "edit_impact_scan", {"request_id": rid(), "transaction_id": tx,
                                                     "expected_revision": rev + 1})
        after_reject = data(call(client, "edit_status", {}))
        check(error(wrong) == "EDIT_OWNER_MISMATCH" and error(conflict) == "EDIT_REVISION_CONFLICT"
              and after_reject.get("transaction", {}).get("work_revision") == rev
              and after_reject.get("fingerprints") == before_reject.get("fingerprints")
              and after_reject.get("risks") == before_reject.get("risks"),
              "owner/revision rejected without mutation", {"wrong": wrong, "conflict": conflict,
                                                             "status": after_reject})
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        check(rolled.get("ok") is True, "first rollback", rolled)
        retired_tx = tx
        active = None

        tx, rev = begin("ImportHost")
        _, empty2 = scan(tx, rev)
        check(empty2.get("inbound_references") == [] and empty2.get("risk_ids") == [],
              "rollback isolation", empty2)
        # New name is supported by InboundRenamed; old name by InboundRef.
        rev, _ = apply(tx, rev, {"kind": "assembly_update", "name": "ImportHostRenamed"})
        prior_review, _, _ = review(tx, rev)
        _, impact = scan(tx, rev)
        refs = impact.get("inbound_references") or []
        names = {(row.get("module"), row.get("matched_name")) for row in refs}
        check(names == {("InboundRef", "ImportHost"),
                        ("InboundRenamed", "ImportHostRenamed")}
              and all(row.get("operation_indices") == [0] and row.get("scan_revision") == rev
                      and row.get("transaction_id") == tx and re.fullmatch(r"0x23[0-9a-fA-F]{6}",
                          str(row.get("assembly_ref_token", ""))) for row in refs),
              "old/new identity facts bound to transaction/revision", impact)
        status = data(call(client, "edit_status", {}))
        check(not isinstance(status.get("review"), dict) or
              status["review"].get("review_id") != prior_review,
              "first risk invalidates prior review", status)
        review_id, required, reviewed = review(tx, rev)
        check(set(impact.get("risk_ids") or []).issubset(set(required)),
              "review requires both inbound risks", reviewed)
        _, repeat = scan(tx, rev)
        after_repeat = data(call(client, "edit_status", {}))
        check(repeat == impact and after_repeat.get("review", {}).get("review_id") == review_id
              and len([r for r in after_repeat.get("risks", [])
                       if r.get("kind") == "cross_assembly_inbound"]) == 2,
              "same scan preserves review and risk cardinality", after_repeat)

        rev, changed = apply(tx, rev, {"kind": "assembly_update", "version": "7.8.9.11"})
        status_stale_reply = call(client, "edit_status", {})
        status_stale = data(status_stale_reply)
        check(changed.get("warnings") and status_stale_reply.get("warnings")
              and not any(r.get("kind") == "cross_assembly_inbound"
                          for r in status_stale.get("risks", [])),
              "apply marks report stale and clears old facts", status_stale)
        _, rescanned = scan(tx, rev)
        check(len(rescanned.get("inbound_references") or []) == 2
              and all(row.get("scan_revision") == rev for row in rescanned["inbound_references"]),
              "rescan binds new revision", rescanned)
        review_id, required, reviewed = review(tx, rev)
        blocked = call(client, "edit_commit", {"request_id": rid(), "transaction_id": tx,
                                               "expected_revision": rev, "review_id": review_id,
                                               "review_revision": rev, "confirmed_risk_ids": []})
        check(error(blocked) == "EDIT_RISK_CONFIRMATION_REQUIRED", "unconfirmed commit refused", blocked)
        malformed = call(client, "edit_commit", {"request_id": rid(), "transaction_id": retired_tx,
                                                 "expected_revision": rev, "review_id": review_id,
                                                 "review_revision": rev, "confirmed_risk_ids": required})
        check(error(malformed) == "EDIT_TRANSACTION_NOT_FOUND", "structural confirmation rejected", malformed)
        committed = call(client, "edit_commit", {"request_id": rid(), "transaction_id": tx,
                                                 "expected_revision": rev, "review_id": review_id,
                                                 "review_revision": rev, "confirmed_risk_ids": required})
        confirmed = data(committed).get("confirmed_risks") or []
        check(committed.get("ok") is True and set(required) == {r.get("risk_id") for r in confirmed}
              and all(r.get("affected_references", {}).get("transaction_id") == tx
                      and r.get("affected_references", {}).get("scan_revision") == rev
                      for r in confirmed if r.get("kind") == "cross_assembly_inbound"),
              "commit echoes this transaction's current inbound facts", committed)
        active = None
        tx, rev = begin("ImportHostRenamed")
        rev, _ = apply(tx, rev, {"kind": "assembly_update", "name": "ImportHostAfterImport"})
        _, before_import = scan(tx, rev)
        check(any(row.get("matched_name") == "ImportHostRenamed"
                  for row in before_import.get("inbound_references", [])),
              "baseline name remains visible before import", before_import)
        source = ("namespace ImportHost { public static class Program { "
                  "public static int Main() { return 777; } } }")
        compiled = call(client, "edit_compile", {"request_id": rid(),
                          "assembly_name": "ImportHostRenamed", "compilation_kind": "edit_class",
                          "documents": [{"path": "T087Import.cs", "content": source}]})
        compile_row = data(compiled).get("compile", {})
        compile_id = str(compile_row.get("compile_id", ""))
        check(compiled.get("ok") is True and compile_row.get("success") is True
              and compile_id.startswith("compile-"), "compile import target", compiled)
        imported = call(client, "edit_import", {"request_id": rid(), "transaction_id": tx,
                            "expected_revision": rev, "compile_id": compile_id,
                            "targets": [{"compiled": "ImportHost.Program::Main()", "action": "replace_body"}]})
        new_rev = data(imported).get("transaction", {}).get("work_revision")
        import_status_reply = call(client, "edit_status", {})
        import_status = data(import_status_reply)
        check(imported.get("ok") is True and isinstance(new_rev, int) and new_rev > rev
              and imported.get("warnings") and import_status_reply.get("warnings")
              and not any(row.get("kind") == "cross_assembly_inbound"
                          for row in import_status.get("risks", [])),
              "import marks old scan stale", {"import": imported, "status": import_status})
        rev = new_rev
        _, after_import = scan(tx, rev)
        check(any(row.get("matched_name") == "ImportHostRenamed"
                  and row.get("scan_revision") == rev for row in after_import.get("inbound_references", [])),
              "import rescan binds current revision", after_import)
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        check(rolled.get("ok") is True, "import transaction rollback", rolled)
        active = None
        tx, rev = begin("ImportHostRenamed")
        _, next_empty = scan(tx, rev)
        check(next_empty.get("risk_ids") == [] and next_empty.get("inbound_references") == [],
              "new transaction has no old inbound facts", next_empty)
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        check(rolled.get("ok") is True, "second rollback", rolled)
        active = None
        evidence["status"] = "PASS"
        return 0
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc),
                               "traceback": traceback.format_exc()}
        return 1
    finally:
        if client is not None and active:
            try:
                call(client, "edit_rollback", {"request_id": rid(), "transaction_id": active})
            except Exception as exc:
                evidence["rollback_error"] = str(exc)
        Path(args.evidence).parent.mkdir(parents=True, exist_ok=True)
        Path(args.evidence).write_text(json.dumps(evidence, ensure_ascii=False, indent=2, default=str) + "\n")
        print(json.dumps({"status": evidence["status"], "failure": evidence["failure"],
                          "evidence": args.evidence}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
