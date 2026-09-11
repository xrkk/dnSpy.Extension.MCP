#!/usr/bin/env python3
"""ACC-013/ACC-011 integration matrix (round 56): edit_restore confirmation
semantics through the real dnSpy MCP loopback, using dual-tool fixture
packages built by rewriting a real commit package's manifest (the
producer-B image stand-in records a distinct image for the same semantics;
the real dual-writer divergence itself is proven by the round-55 harness
probe). Fixtures:
  - validated head  (restore apply without confirm rejected; with confirm
                     creates an exact migration child)
  - validated ancestor with exact head (edit_undo to the validated ancestor
                     rejected with EDIT_REPLAY_CONFIRMATION_REQUIRED)
  - unverified head (apply rejected with EDIT_REPLAY_UNVERIFIED, zero effects)
Plus: plain begin/commit never bypasses the confirmation gate."""

from __future__ import annotations

import hashlib
import io
import json
import sys
import uuid
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
STORE = Path(__file__).resolve().parents[2] / "store"
ARTIFACT_STORE = Path.home() / "Desktop" / "dnspy-mcp-artifacts" / "edit-checkpoints"
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


def craft(package: bytes, new_lineage_id: str, mutate_head_image: bool, mutate_head_semantic: bool,
          mutate_ancestor_image: bool) -> bytes:
    source = zipfile.ZipFile(io.BytesIO(package))
    entries = {name: source.read(name) for name in source.namelist()}
    source.close()
    manifest = json.loads(entries["manifest.json"])
    manifest["lineage_id"] = new_lineage_id
    manifest["family_id"] = "family-" + new_lineage_id[len("lineage-"):]
    stand_in = hashlib.sha256(package).hexdigest()
    nodes = sorted(manifest["checkpoints"], key=lambda row: row["sequence"])
    if mutate_ancestor_image and len(nodes) >= 2:
        nodes[-2]["result_image_sha256"] = stand_in
    head = nodes[-1]
    if mutate_head_image:
        head["result_image_sha256"] = stand_in
    if mutate_head_semantic:
        semantic = head["result_semantic_fingerprint"]
        head["result_semantic_fingerprint"] = ("0" if semantic[0] == "f" else "f") + semantic[1:]
    entries["manifest.json"] = json.dumps(manifest, ensure_ascii=False).encode("utf-8")
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as target:
        for name, data in entries.items():
            target.writestr(name, data)
    return output.getvalue()


def store_fixture(lineage_id: str, package: bytes) -> None:
    ARTIFACT_STORE.mkdir(parents=True, exist_ok=True)
    (ARTIFACT_STORE / f"{lineage_id}.dnspy-mcp-checkpoints").write_bytes(package)


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc013")
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # Real lineage with two checkpoints (C1 ancestor, C2 head).
    names = []
    tx = ""
    revision = 0
    live_fp = ""
    for index, name in enumerate(("Acc013A", "Acc013B")):
        begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
        tx = payload(begin).get("transaction", {}).get("transaction_id", "")
        revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
        applied = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "operation": {"kind": "type_update", "target": {"token": "0x02000002"}, "name": name},
        })
        review = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1})
        review_core = payload(review).get("review", {})
        committed = call(client, "edit_commit", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision + 1,
            "review_id": review_core.get("review_id", ""),
            "review_revision": review_core.get("review_revision", 0),
            "confirmed_risk_ids": [],
        })
        if not committed.get("ok"):
            print(f"seed commit failed: {json.dumps(committed)[:300]}", flush=True)
            return 1
        names.append(name)
        # The live fingerprint the restore ticket binds to is the state AFTER
        # the last commit, not the state at the first begin.
        live_fp = str(payload(committed).get("fingerprints", {}).get("after", ""))
    history = payload(call(client, "edit_history", {}))
    lineages = [row for row in history.get("lineages", []) if isinstance(row, dict)]
    check("B1 real lineage with two commits (baseline+2)", len(lineages) == 1 and int(lineages[0].get("checkpoint_count", 0)) == 3,
          json.dumps(history)[:240])
    real_id = str(lineages[0].get("lineage_id", ""))
    real_head = str(lineages[0].get("head_checkpoint_id", ""))
    real_package = (ARTIFACT_STORE / f"{real_id}.dnspy-mcp-checkpoints").read_bytes()

    validated_id = "lineage-" + uuid.uuid4().hex
    store_fixture(validated_id, craft(real_package, validated_id, True, False, False))
    ancestor_id = "lineage-" + uuid.uuid4().hex
    store_fixture(ancestor_id, craft(real_package, ancestor_id, False, False, True))
    unverified_id = "lineage-" + uuid.uuid4().hex
    store_fixture(unverified_id, craft(real_package, unverified_id, True, True, False))

    listing = payload(call(client, "edit_history", {}))
    rows = [row for row in listing.get("lineages", []) if isinstance(row, dict)]
    check("B2 fixtures listed", len(rows) == 4, f"rows={len(rows)}")

    # Locate fixture heads (same checkpoint ids as the real lineage).
    def head_of(lineage_id: str) -> str:
        view = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
        nodes = [row for row in view.get("checkpoints", []) if isinstance(row, dict) and row.get("is_head")]
        return str(nodes[0].get("checkpoint_id", "")) if nodes else ""

    def ancestor_of(lineage_id: str) -> str:
        view = payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))
        nodes = sorted([row for row in view.get("checkpoints", []) if isinstance(row, dict)], key=lambda row: row.get("sequence", 0))
        return str(nodes[-2].get("checkpoint_id", "")) if len(nodes) >= 2 else ""

    validated_head = head_of(validated_id)
    ancestor_head = head_of(ancestor_id)
    ancestor_target = ancestor_of(ancestor_id)
    unverified_head = head_of(unverified_id)

    # Classifications through assess.
    v_assess = payload(call(client, "edit_restore", {"request_id": rid(), "lineage_id": validated_id, "checkpoint_id": validated_head, "action": "assess"}))
    v_replay = v_assess.get("replay", {}) if isinstance(v_assess.get("replay"), dict) else {}
    check("B3 validated assess", v_replay.get("classification") == "validated_drift", json.dumps(v_assess)[:240])
    u_assess = payload(call(client, "edit_restore", {"request_id": rid(), "lineage_id": unverified_id, "checkpoint_id": unverified_head, "action": "assess"}))
    u_replay = u_assess.get("replay", {}) if isinstance(u_assess.get("replay"), dict) else {}
    check("B3 unverified assess", u_replay.get("classification") == "unverified_drift", json.dumps(u_assess)[:240])

    # apply without confirmation must be rejected (ACC-011 auto-navigation rejection).
    denied = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": validated_id, "checkpoint_id": validated_head, "action": "apply",
        "replay_id": v_replay.get("replay_id", ""), "expected_live_fingerprint": live_fp,
    })
    check("B4 validated apply without confirm rejected", err_code(denied) == "EDIT_REPLAY_CONFIRMATION_REQUIRED", err_code(denied))
    unverified_apply = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": unverified_id, "checkpoint_id": unverified_head, "action": "apply",
        "replay_id": u_replay.get("replay_id", ""), "expected_live_fingerprint": live_fp,
    })
    check("B5 unverified apply rejected", err_code(unverified_apply) == "EDIT_REPLAY_UNVERIFIED", err_code(unverified_apply))

    # edit_undo to a validated ancestor must reject with the same gate.
    undo_denied = call(client, "edit_undo", {"request_id": rid(), "lineage_id": ancestor_id, "expected_checkpoint_id": ancestor_head})
    check("B6 undo to validated ancestor rejected", err_code(undo_denied) == "EDIT_REPLAY_CONFIRMATION_REQUIRED", err_code(undo_denied))

    # Zero side effects after the rejections: lineage count and real head unchanged,
    # live fingerprint unchanged.
    after = payload(call(client, "edit_history", {}))
    after_rows = [row for row in after.get("lineages", []) if isinstance(row, dict)]
    real_row = next((row for row in after_rows if row.get("lineage_id") == real_id), {})
    check("B7 rejections leave store unchanged",
          len(after_rows) == 4 and real_row.get("head_checkpoint_id") == real_head,
          f"rows={len(after_rows)} head={real_row.get('head_checkpoint_id')}")

    # Plain begin/commit must not bypass the confirmation gate: the fixture
    # lineages keep their own families; a normal transaction still binds the
    # real lineage and rolls back cleanly.  Runs BEFORE the confirmed
    # migration because that step legitimately moves live to the ancestor.
    begin2 = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    begin_ok = bool(begin2.get("ok"))
    check("B9 plain begin still binds real lineage", begin_ok, json.dumps(begin2)[:240])
    if begin_ok:
        tx2 = payload(begin2).get("transaction", {}).get("transaction_id", "")
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx2})
        check("B9 rollback clean", bool(rolled.get("ok")), json.dumps(rolled)[:200])

    # Confirmed migration: the contract requires an exact head and a validated
    # TARGET, so the ancestor fixture (C1 validated, C2 head exact) is the
    # migration input; live legitimately moves to the ancestor content.
    a_assess = payload(call(client, "edit_restore", {"request_id": rid(), "lineage_id": ancestor_id, "checkpoint_id": ancestor_target, "action": "assess"}))
    a_replay = a_assess.get("replay", {}) if isinstance(a_assess.get("replay"), dict) else {}
    check("B8 ancestor target assesses validated", a_replay.get("classification") == "validated_drift", json.dumps(a_assess)[:240])
    confirmed = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": ancestor_id, "checkpoint_id": ancestor_target, "action": "apply",
        "replay_id": a_replay.get("replay_id", ""), "expected_live_fingerprint": live_fp,
        "confirm_validated_drift": True,
    })
    check("B8 confirmed migration ok", bool(confirmed.get("ok")), json.dumps(confirmed)[:300])
    if confirmed.get("ok"):
        core = payload(confirmed)
        history_row = core.get("history", {}) if isinstance(core.get("history"), dict) else {}
        migrated_head = str(history_row.get("head_checkpoint_id", ""))
        m_assess = payload(call(client, "edit_restore", {"request_id": rid(), "lineage_id": ancestor_id, "checkpoint_id": migrated_head, "action": "assess"}))
        m_replay = m_assess.get("replay", {}) if isinstance(m_assess.get("replay"), dict) else {}
        check("B8 migration child exact", m_replay.get("classification") == "exact", json.dumps(m_assess)[:240])
        view = payload(call(client, "edit_history", {"lineage_id": ancestor_id, "page_size": 100}))
        heads = [row for row in view.get("checkpoints", []) if isinstance(row, dict) and row.get("is_head")]
        check("B8 fixture head is the migration child", bool(heads) and heads[0].get("checkpoint_id") == migrated_head)
        # The process binding follows the applied history (same mechanism that
        # keeps begin working after undo/redo): the migration moved both live
        # and the binding to the exact migration child, so a new begin binds
        # consistently instead of reporting divergence.
        rebound = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
        check("B10 post-migration begin usable", bool(rebound.get("ok")), err_code(rebound))
        if rebound.get("ok"):
            tx3 = payload(rebound).get("transaction", {}).get("transaction_id", "")
            rolled3 = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx3})
            check("B10 post-migration rollback clean", bool(rolled3.get("ok")), json.dumps(rolled3)[:200])

    print(f"ACC013 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
