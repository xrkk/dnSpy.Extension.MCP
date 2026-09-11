#!/usr/bin/env python3
"""ACC-020 integration regression (round 57): all six gated legacy write tools
through the real MCP shape, routed by the LegacyEditAdapter into the structured
transaction/checkpoint path. Includes the generic-reference rename integrity
check (GenericMethodOwner`1.Echo is referenced through a closed-generic
MemberRef), undo/redo navigation on the resulting single lineage, the
constrained legacy revert, save_assembly safety projection, and the ACC-014
MCP-shape EDIT_EXPORT_BLOCKED assertion under live divergence."""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        PASSES.append(name)
        print(f"PASS {name}", flush=True)
    else:
        FAILURES.append(name)
        print(f"FAIL {name} {detail}", flush=True)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        return client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                return json.loads(text[start:])
            except json.JSONDecodeError:
                pass
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def err_code(envelope: dict) -> str:
    error = envelope.get("error") if isinstance(envelope, dict) else None
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def as_text(envelope) -> str:
    """Static and legacy tools return the parsed projection object or plain
    text from call_tool_json (no edit_* envelope wrapper)."""
    if isinstance(envelope, str):
        return envelope
    if isinstance(envelope, dict):
        content = envelope.get("content")
        if isinstance(content, list) and content and isinstance(content[0], dict):
            return str(content[0].get("text", ""))
        return json.dumps(envelope)
    return str(envelope)


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc020")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # Locate the generic method token for the rename integrity check.
    methods = call(client, "list_methods", {"assembly_name": "TestIL", "type_full_name": "TestIL.GenericMethodOwner`1"})
    core = methods if isinstance(methods, dict) else {}
    rows = core.get("items", core.get("methods", [])) if isinstance(core.get("items", core.get("methods")), list) else []
    echo = next((row for row in rows if isinstance(row, dict) and row.get("name") == "Echo"), None)
    if echo is None:
        print(f"Echo not found: {json.dumps(methods)[:300]}", flush=True)
        return 1
    echo_token = echo.get("token")

    # 1) rename_symbol_by_token: generic MemberRef reference integrity.
    renamed = call(client, "rename_symbol_by_token", {
        "assembly_name": "TestIL", "target_kind": "method", "token": echo_token, "new_name": "EchoRenamed",
    })
    rename_text = as_text(renamed)
    check("C1 rename ok", "EchoRenamed" in rename_text or "checkpoint" in rename_text, rename_text[:240])
    caller = call(client, "get_method_il", {
        "assembly_name": "TestIL", "type_full_name": "TestIL.GenericMethodCaller", "method_name": "Call",
    })
    caller_il = as_text(caller)
    check("C1 generic MemberRef updated", "EchoRenamed" in caller_il, caller_il[:200])
    rename_projection = rename_text
    check("C1 legacy projection carries checkpoint history",
          "checkpoint" in rename_projection and "compatibility_warning" in rename_projection,
          rename_projection[:200])

    history = payload(call(client, "edit_history", {}))
    lineages = [row for row in history.get("lineages", []) if isinstance(row, dict)]
    check("C2 single lineage from legacy mutations", len(lineages) == 1, json.dumps(history)[:240])
    lineage_id = str(lineages[0].get("lineage_id", "")) if lineages else ""

    # 2) patch_method_il / force_return / nop_method through the same path.
    patched = call(client, "patch_method_il", {
        "assembly_name": "TestIL", "type_full_name": "TestIL.Simple", "method_name": "AddOne",
        "edits": [{"op": "insert", "index": 0, "opcode": "nop", "operand": ""}],
    })
    check("C3 patch_method_il ok", "edits_applied" in as_text(patched), as_text(patched)[:200])
    forced = call(client, "force_return", {
        "assembly_name": "TestIL", "type_full_name": "TestIL.Simple", "method_name": "Inc", "value": 42,
    })
    check("C3 force_return ok", "forced_return" in as_text(forced), as_text(forced)[:200])
    nopped = call(client, "nop_method", {
        "assembly_name": "TestIL", "type_full_name": "TestIL.Simple", "method_name": "Greet",
    })
    check("C3 nop_method ok", "nopped" in as_text(nopped), as_text(nopped)[:200])
    status = payload(call(client, "edit_status", {}))
    check("C3 idle after legacy mutations", status.get("state") == "idle", json.dumps(status)[:160])

    # 3) undo/redo navigate the legacy-built lineage.
    view = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
    nodes = sorted([row for row in view.get("checkpoints", []) if isinstance(row, dict)], key=lambda row: row.get("sequence", 0))
    check("C4 checkpoints accumulated", len(nodes) >= 4, f"count={len(nodes)}")
    head = str(nodes[-1].get("checkpoint_id", "")) if nodes else ""
    parent = str(nodes[-2].get("checkpoint_id", "")) if len(nodes) >= 2 else ""
    undone = call(client, "edit_undo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": head})
    check("C4 edit_undo ok", bool(undone.get("ok")), json.dumps(undone)[:240])
    redone = call(client, "edit_redo", {"request_id": rid(), "lineage_id": lineage_id, "expected_checkpoint_id": parent})
    check("C4 edit_redo ok", bool(redone.get("ok")), json.dumps(redone)[:240])

    # 4) revert_method_il: constrained legacy undo; the pending patch must be
    # at the current head, so revert the last mutated method (Greet's nop).
    reverted = call(client, "revert_method_il", {
        "assembly_name": "TestIL", "type_full_name": "TestIL.Simple", "method_name": "Greet",
    })
    reverted_text = as_text(reverted)
    check("C5 revert ok", "reverted" in reverted_text and "compatibility_warning" in reverted_text, reverted_text[:240])

    # 5) save_assembly safety projection.
    saved = call(client, "save_assembly", {"assembly_name": "TestIL"})
    saved_text = as_text(saved)
    check("C6 save_assembly projection",
          "saved_to" in saved_text and "source_preserved" in saved_text and "backup_path" in saved_text,
          saved_text[:240])

    # 6) ACC-014 (MCP shape): export is blocked for a checkpoint whose replay
    # classification is not exact (validated-drift fixture, round-56 method).
    import hashlib
    import io
    import zipfile
    package_path = Path.home() / "Desktop" / "dnspy-mcp-artifacts" / "edit-checkpoints" / f"{lineage_id}.dnspy-mcp-checkpoints"
    package = package_path.read_bytes()
    source = zipfile.ZipFile(io.BytesIO(package))
    entries = {name: source.read(name) for name in source.namelist()}
    source.close()
    manifest = json.loads(entries["manifest.json"])
    fixture_id = "lineage-" + uuid.uuid4().hex
    manifest["lineage_id"] = fixture_id
    manifest["family_id"] = "family-" + fixture_id[len("lineage-"):]
    nodes = sorted(manifest["checkpoints"], key=lambda row: row["sequence"])
    nodes[-1]["result_image_sha256"] = hashlib.sha256(package).hexdigest()
    entries["manifest.json"] = json.dumps(manifest, ensure_ascii=False).encode("utf-8")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for name, data in entries.items():
            target.writestr(name, data)
    (Path.home() / "Desktop" / "dnspy-mcp-artifacts" / "edit-checkpoints" / f"{fixture_id}.dnspy-mcp-checkpoints").write_bytes(output.getvalue())
    blocked = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": fixture_id, "checkpoint_id": nodes[-1]["checkpoint_id"],
    })
    check("C7 export blocked for non-exact checkpoint", err_code(blocked) == "EDIT_EXPORT_BLOCKED", err_code(blocked))
    exact_export = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": head,
    })
    check("C7 exact checkpoint still exports", bool(exact_export.get("ok")), json.dumps(exact_export)[:240])

    print(f"ACC020 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
