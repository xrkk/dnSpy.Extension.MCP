#!/usr/bin/env python3
"""Real-MCP regression for constrained legacy revert and legacy write gates.

Run only in a fresh isolated host with a disposable TestIL.dll and artifact root.
The JSON output preserves every tools/call result before client conversion.
"""
import argparse
import hashlib
import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient


def main(url: str, fixture: Path, output: Path) -> int:
    client = DnSpyClient(url, client_name="p03-vm-legacy-revert-contract")
    rows: list[dict] = []
    failures: list[str] = []
    original_sha = hashlib.sha256(fixture.read_bytes()).hexdigest()

    def call(name: str, args: dict) -> dict:
        raw = client.request("tools/call", {"name": name, "arguments": args})
        content = raw.get("content") or []
        text = content[0].get("text", "") if content else ""
        try:
            parsed = json.loads(text)
        except (TypeError, ValueError):
            parsed = None
        structured = raw.get("structuredContent")
        rows.append({"tool": name, "arguments": args, "raw": raw,
                     "text_json": parsed, "mirror": parsed == structured if structured is not None else None})
        return structured if isinstance(structured, dict) else (parsed if isinstance(parsed, dict) else {})

    def require(name: str, condition: bool) -> None:
        if not condition:
            failures.append(name)

    def triple(envelope: dict, state: str) -> bool:
        error = envelope.get("error") or {}
        return (envelope.get("ok") is False and envelope.get("state") == state
                and error.get("code") == "EDIT_HISTORY_CONFLICT"
                and error.get("current_state") == state and bool(error.get("recovery")))

    try:
        client.initialize()
        listed = client.request("tools/list", {})
        legacy = ("force_return", "nop_method", "patch_method_il", "revert_method_il",
                  "rename_symbol_by_token", "save_assembly")
        definitions = {tool["name"]: tool for tool in listed["tools"] if tool["name"] in legacy}
        require("six_registered", set(definitions) == set(legacy))
        require("legacy_output_schema_absent", all("outputSchema" not in tool for tool in definitions.values()))
        open_result = call("open_files", {"paths": [str(fixture)]})
        require("fixture_opened", open_result.get("loaded_count") == 1)
        greet = {"assembly_name": "TestIL", "type_full_name": "TestIL.Simple", "method_name": "Greet"}
        empty = call("revert_method_il", greet)
        require("empty_revert_triple", triple(empty, "idle"))
        empty_history = call("edit_history", {})
        require("empty_revert_no_history", not empty_history.get("result", {}).get("lineages"))

        nopped = call("nop_method", greet)
        require("legacy_mutation_checkpoint", nopped.get("nopped") is True and isinstance(nopped.get("checkpoint"), dict))
        before_wrong = call("edit_history", {})
        wrong = call("revert_method_il", {**greet, "method_name": "Inc"})
        after_wrong = call("edit_history", {})
        require("wrong_method_triple", triple(wrong, "idle"))
        require("wrong_method_no_history_change", before_wrong == after_wrong)

        begun = call("edit_begin", {"request_id": str(uuid.uuid4()), "assembly_name": "TestIL"})
        tx = begun.get("result", {}).get("transaction", {}).get("transaction_id")
        require("begin", bool(begun.get("ok")) and bool(tx))
        if tx:
            busy_revert = call("revert_method_il", greet)
            revert_error = busy_revert.get("error") or {}
            require("legacy_revert_busy", busy_revert.get("state") == "editing"
                    and revert_error.get("code") == "EDIT_TRANSACTION_BUSY"
                    and revert_error.get("current_state") == "editing" and bool(revert_error.get("recovery")))
            busy = call("save_assembly", {"assembly_name": "TestIL"})
            error = busy.get("error") or {}
            require("legacy_save_busy", busy.get("state") == "editing"
                    and error.get("code") == "EDIT_TRANSACTION_BUSY"
                    and error.get("current_state") == "editing" and bool(error.get("recovery")))
            rolled = call("edit_rollback", {"request_id": str(uuid.uuid4()), "transaction_id": tx})
            require("rollback", rolled.get("ok") is True)

        reverted = call("revert_method_il", greet)
        require("valid_revert", reverted.get("reverted") is True and reverted.get("has_pending_patch") is False)
        saved = call("save_assembly", {"assembly_name": "TestIL"})
        require("safe_export", saved.get("source_preserved") is True and saved.get("backup_path") is None
                and str(saved.get("saved_to", "")).lower() != str(fixture).lower())
        final_sha = hashlib.sha256(fixture.read_bytes()).hexdigest()
        require("source_unchanged", final_sha == original_sha)
        require("wire_mirror", all(row["mirror"] is True for row in rows))
        evidence = {"definitions": definitions, "rows": rows, "failures": failures,
                    "source_sha_before": original_sha, "source_sha_after": final_sha}
        output.write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"failures": failures, "rows": len(rows), "source_unchanged": final_sha == original_sha}))
        return 0 if not failures else 1
    finally:
        client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    raise SystemExit(main(args.url, args.fixture, args.output))
