#!/usr/bin/env python3
"""RACC-013 version compatibility over the real MCP surface.

Phase v1 consumes the committed pre-T003 v1 packages and explicitly accepts
the live module into a new v2 lineage.  After an owned dnSpy restart, phase v2
uses that accepted lineage to exercise current v2 replay, migration, and
fail-closed version gates.  The phase split resets only the process binding;
the isolated checkpoint store and evidence state are preserved.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import uuid
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE_ROOT = Path(r"C:\Tools\mcp-repo\tests\fixtures\bin")
FIXTURE = FIXTURE_ROOT / "MethodOwnerProbeG.dll"
STORE = Path.home() / "Desktop/dnspy-mcp-artifacts/edit-checkpoints"
WORK = Path.home() / "Desktop/dnspy-mcp-artifacts/t019-work"
ARTIFACT_ROOT = Path.home() / "Desktop/dnspy-mcp-artifacts"
DOTNET = Path("dotnet")
RAW_WRITER = Path("T019RawWriter.dll")
ARCH = os.environ.get("DNMCP_ARCH", "unknown")
PHASE = os.environ.get("DNMCP_T019_PHASE", "v1")
PASSES: list[str] = []
FAILURES: list[str] = []


def configure_isolation(context) -> None:
    global URL, FIXTURE_ROOT, FIXTURE, STORE, WORK, ARTIFACT_ROOT, DOTNET, RAW_WRITER, ARCH
    URL = context.mcp_url
    FIXTURE_ROOT = Path(context.fixture_root)
    fixture_name = "MethodOwnerProbeG-head.dll" if PHASE in ("v1_drift", "v2") else "MethodOwnerProbeG.dll"
    FIXTURE = Path(context.fixture(fixture_name))
    STORE = Path(context.checkpoint_store)
    WORK = Path(context.work_root)
    ARTIFACT_ROOT = Path(context.artifact_root)
    DOTNET = Path(context.dotnet_host)
    RAW_WRITER = Path(context.harness_dir) / "T019RawWriter.dll"
    ARCH = context.architecture


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail="") -> None:
    if condition:
        PASSES.append(name)
        print("PASS " + name, flush=True)
    else:
        FAILURES.append(name)
        print("FAIL " + name + " " + str(detail)[:500], flush=True)


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def error_code(envelope: dict) -> str:
    value = envelope.get("error") if isinstance(envelope, dict) else None
    return str(value.get("code", "")) if isinstance(value, dict) else ""


def sha_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha_file(path: Path) -> str:
    return sha_bytes(path.read_bytes())


def read_package(data: bytes) -> tuple[dict, dict[str, bytes]]:
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = {row.filename: archive.read(row.filename) for row in archive.infolist()}
    return json.loads(entries["manifest.json"]), entries


def build_package(entries: dict[str, bytes]) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return output.getvalue()


def platform_v1_package(source: bytes) -> bytes:
    """Adapt only the historical output separator required by the v1 contract."""
    manifest, entries = read_package(source)
    relative = str(manifest["default_output"]["relative_path"])
    platform_relative = relative.replace("/", os.sep)
    if platform_relative == relative:
        return source
    manifest["default_output"]["relative_path"] = platform_relative
    entries["manifest.json"] = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
    return build_package(entries)


def retarget_v1_package(source: bytes, lineage_id: str, family_id: str) -> bytes:
    manifest, entries = read_package(source)
    manifest["lineage_id"] = lineage_id
    manifest["family_id"] = family_id
    entries["manifest.json"] = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
    return build_package(entries)


def rewrite_package(source: bytes, *, image_sha: str | None = None,
                    semantic_flip: bool = False, package_format: str | None = None,
                    unknown_kind: bool = False, unknown_version: bool = False,
                    image_checkpoint_id: str | None = None) -> tuple[str, bytes]:
    manifest, entries = read_package(source)
    lineage_id = "lineage-" + uuid.uuid4().hex
    manifest["lineage_id"] = lineage_id
    manifest["family_id"] = "family-" + uuid.uuid4().hex
    if package_format:
        manifest["format"] = package_format
    selected_id = image_checkpoint_id or manifest["head_checkpoint_id"]
    head = next(row for row in manifest["checkpoints"] if row["checkpoint_id"] == selected_id)
    if image_sha:
        head["result_image_sha256"] = image_sha
    if semantic_flip:
        value = head["result_semantic_fingerprint"]
        head["result_semantic_fingerprint"] = ("0" if value[0] != "0" else "f") + value[1:]
    if unknown_kind or unknown_version:
        op = json.loads(entries[head["operation_entry"]])
        if not op.get("operations"):
            raise RuntimeError("version fixture requires a non-empty head operation")
        row = op["operations"][0]
        if unknown_kind:
            row["kind"] = "future_unknown_kind"
            row["forward"]["kind"] = "future_unknown_kind"
            row["inverse"]["operation_kind"] = "future_unknown_kind"
        if unknown_version:
            row["kind_version"] = 99
        encoded = json.dumps(op, ensure_ascii=False, separators=(",", ":")).encode()
        entries[head["operation_entry"]] = encoded
        head["operation_sha256"] = sha_bytes(encoded)
    entries["manifest.json"] = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode()
    return lineage_id, build_package(entries)


def package_path(lineage_id: str) -> Path:
    return STORE / f"{lineage_id}.dnspy-mcp-checkpoints"


def inject(package: bytes) -> tuple[str, Path]:
    manifest, _ = read_package(package)
    lineage_id = str(manifest["lineage_id"])
    path = package_path(lineage_id)
    path.write_bytes(package)
    return lineage_id, path


def store_snapshot() -> dict[str, dict[str, object]]:
    return {path.name: {"length": path.stat().st_size, "sha256": sha_file(path)}
            for path in sorted(STORE.glob("*.dnspy-mcp-checkpoints"))}


def live_fingerprint(client: DnSpyClient) -> str:
    begin = call(client, "edit_begin", {"assembly_name": "MethodOwnerProbeG", "request_id": rid()})
    check("fingerprint begin succeeds", bool(begin.get("ok")), begin)
    core = payload(begin)
    value = str(core.get("source", {}).get("live_fingerprint", ""))
    tx = str(core.get("transaction", {}).get("transaction_id", ""))
    if tx:
        rolled = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
        check("fingerprint transaction rolled back", bool(rolled.get("ok")), rolled)
    check("live fingerprint observed", len(value) == 64, value)
    return value


def assess(client: DnSpyClient, lineage_id: str, checkpoint_id: str) -> dict:
    return payload(call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": lineage_id,
        "checkpoint_id": checkpoint_id, "action": "assess",
    })).get("replay", {})


def history_lineage(client: DnSpyClient, lineage_id: str) -> dict:
    return payload(call(client, "edit_history", {"lineage_id": lineage_id, "page_size": 100}))


def write_facts(name: str, facts: dict) -> None:
    WORK.mkdir(parents=True, exist_ok=True)
    (WORK / name).write_text(json.dumps(facts, ensure_ascii=False, indent=2), encoding="utf-8")


def phase_v1_exact(client: DnSpyClient) -> None:
    STORE.mkdir(parents=True, exist_ok=True)
    check("v1 phase starts with empty store", not list(STORE.glob("*.dnspy-mcp-checkpoints")))

    fixed_src = FIXTURE_ROOT / "legacy-v1/v1-fixed-package.dnspy-mcp-checkpoints"
    swapped_src = FIXTURE_ROOT / "legacy-v1/v1-swapped-baseline-package.dnspy-mcp-checkpoints"
    fixed_original, swapped_original = fixed_src.read_bytes(), swapped_src.read_bytes()
    check("historical v1 fixed SHA", sha_bytes(fixed_original) == "3db186f57b86b5b6f9f4774529333483853cadc5b080bff0e1dc811e57a9da26")
    check("historical v1 swapped SHA", sha_bytes(swapped_original) == "ac4d4b48a9014da768ebbb9e11bdcf7ddf0ab4acee54389fe85d3198ef5cc7e6")
    fixed_bytes, swapped_bytes = platform_v1_package(fixed_original), platform_v1_package(swapped_original)
    fixed_manifest, _ = read_package(fixed_bytes)
    fixed_id, fixed_path = inject(fixed_bytes)
    fixed_replay = assess(client, fixed_id, fixed_manifest["head_checkpoint_id"])
    check("v1 historical package classifies exact", fixed_replay.get("classification") == "exact", fixed_replay)
    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": fixed_id,
        "checkpoint_id": fixed_manifest["head_checkpoint_id"],
        "output_path": f"edit-output/t019-{ARCH}-v1-head.dll",
    })
    export_path = Path(str(payload(exported).get("output", {}).get("path", "")))
    head_fixture = FIXTURE_ROOT / "MethodOwnerProbeG-head.dll"
    check("v1 exact head exports", bool(exported.get("ok")) and export_path.is_file(), exported)
    if export_path.is_file():
        shutil.copy2(export_path, head_fixture)
    check("v1 exact head staged for bound drift", head_fixture.is_file())
    state = {
        "arch": ARCH,
        "fixed": {"lineage_id": fixed_id, "source_sha256": sha_bytes(fixed_original), "platform_sha256": sha_bytes(fixed_bytes), "package_sha256": sha_file(fixed_path), "manifest": fixed_manifest},
        "swapped_source": {"source_sha256": sha_bytes(swapped_original), "platform_sha256": sha_bytes(swapped_bytes)},
        "producer": {"revision": "115b09a9ea856432eb2fb7ec24a2df9652261329", "product_dll_sha256": "869123f327b16457ada79251f03620a53ea0a709600c096dd348df5ba16dbe37", "package_nodes": fixed_manifest["checkpoints"]},
        "responses": {"fixed_assess": fixed_replay},
        "exported_head_sha256": sha_file(head_fixture),
    }
    write_facts(f"t019-{ARCH}-v1-exact.json", state)
    (WORK / f"t019-{ARCH}-state.json").write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")


def phase_v1_drift(client: DnSpyClient) -> None:
    state = json.loads((WORK / f"t019-{ARCH}-state.json").read_text(encoding="utf-8"))
    fixed_id = state["fixed"]["lineage_id"]
    fixed_manifest = state["fixed"]["manifest"]
    fixed_path = package_path(fixed_id)
    begin = call(client, "edit_begin", {"assembly_name": "MethodOwnerProbeG", "source_family_id": fixed_manifest["family_id"], "request_id": rid()})
    check("exact v1 head binds process lineage", bool(begin.get("ok")) and payload(begin).get("history", {}).get("lineage_id") == fixed_id, begin)
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    live_fp = str(payload(begin).get("source", {}).get("live_fingerprint", ""))
    if tx:
        check("bound transaction rolled back", bool(call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx}).get("ok")))

    swapped_original = (FIXTURE_ROOT / "legacy-v1/v1-swapped-baseline-package.dnspy-mcp-checkpoints").read_bytes()
    swapped_platform = platform_v1_package(swapped_original)
    swapped_bytes = retarget_v1_package(swapped_platform, fixed_id, fixed_manifest["family_id"])
    fixed_path.write_bytes(swapped_bytes)
    swapped_manifest, _ = read_package(swapped_bytes)
    before = store_snapshot()
    swapped_id, swapped_path = fixed_id, fixed_path
    swapped_replay = assess(client, swapped_id, swapped_manifest["head_checkpoint_id"])
    check("v1 historical image drift classifies unverified", swapped_replay.get("classification") == "unverified_drift", swapped_replay)
    check("v1 drift keeps frozen semantic but changes image",
          swapped_replay.get("semantic_fingerprint") == swapped_replay.get("recorded_semantic_fingerprint")
          and swapped_replay.get("image_sha256") != swapped_replay.get("recorded_image_sha256"), swapped_replay)

    denied = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": swapped_id,
        "checkpoint_id": swapped_manifest["head_checkpoint_id"], "action": "apply",
        "replay_id": swapped_replay.get("replay_id", ""), "expected_live_fingerprint": live_fp,
        "confirm_validated_drift": True,
    })
    check("v1 drift refuses confirmed migration", error_code(denied) == "EDIT_REPLAY_UNVERIFIED", denied)
    check("v1 refusal preserves package directory", store_snapshot() == before)

    accepted = call(client, "edit_accept_live", {
        "request_id": rid(), "assembly_name": "MethodOwnerProbeG",
        "source_family_id": swapped_manifest["family_id"],
        "superseded_lineage_id": swapped_id, "expected_live_fingerprint": live_fp,
        "acknowledge_new_baseline": True,
    })
    accepted_core = payload(accepted)
    new_lineage = accepted_core.get("lineage", {})
    new_id = str(new_lineage.get("lineage_id", ""))
    check("explicit accept_live succeeds", bool(accepted.get("ok")) and new_id.startswith("lineage-"), accepted)
    check("v1 refusal preserved expected live fingerprint", bool(accepted.get("ok")), accepted)
    new_manifest, _ = read_package(package_path(new_id).read_bytes())
    check("accept_live creates v2 lineage", new_manifest.get("format") == "dnspy.edit.checkpoints.v2")
    check("accept_live links superseded v1", new_manifest.get("superseded_lineage_id") == swapped_id)
    check("old v1 drift package remains byte-identical", sha_file(swapped_path) == sha_bytes(swapped_bytes))
    check("old v1 lineage remains queryable", len(history_lineage(client, swapped_id).get("checkpoints", [])) == 3)

    state.update({
        "live_fingerprint": live_fp,
        "swapped": {"lineage_id": swapped_id, "source_sha256": sha_bytes(swapped_original), "platform_sha256": sha_bytes(swapped_platform), "retargeted_sha256": sha_bytes(swapped_bytes), "manifest": swapped_manifest},
        "accepted": {"lineage_id": new_id, "package_sha256": sha_file(package_path(new_id)), "manifest": new_manifest},
        "responses": {**state["responses"], "swapped_assess": swapped_replay,
                      "confirmed_v1_restore_refusal": denied, "accept_live": accepted},
    })
    write_facts(f"t019-{ARCH}-v1-drift.json", state)
    (WORK / f"t019-{ARCH}-state.json").write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")


def phase_v2(client: DnSpyClient) -> None:
    state = json.loads((WORK / f"t019-{ARCH}-state.json").read_text(encoding="utf-8"))
    fixed_id = state["fixed"]["lineage_id"]
    swapped_id = state["swapped"]["lineage_id"]
    accepted_id = state["accepted"]["lineage_id"]
    before_old = {key: sha_file(package_path(key)) for key in (fixed_id, swapped_id)}

    initial_fp = live_fingerprint(client)
    begin = call(client, "edit_begin", {"assembly_name": "MethodOwnerProbeG", "request_id": rid()})
    tx = payload(begin).get("transaction", {})
    check("v2 begin binds accepted lineage", payload(begin).get("history", {}).get("lineage_id") == accepted_id, begin)
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx.get("transaction_id", ""),
        "expected_revision": tx.get("work_revision", 0),
        "operation": {"kind": "module_update", "name": "T019V2"},
    })
    reviewed = call(client, "edit_review", {
        "request_id": rid(), "transaction_id": tx.get("transaction_id", ""), "expected_revision": 1,
    })
    review = payload(reviewed).get("review", {})
    risks = [str(value) for value in review.get("required_confirmation_ids", [])]
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx.get("transaction_id", ""), "expected_revision": 1,
        "review_id": review.get("review_id", ""), "review_revision": review.get("review_revision", 0),
        "confirmed_risk_ids": risks,
    })
    check("v2 real commit succeeds", bool(applied.get("ok")) and bool(committed.get("ok")), committed)
    committed_core = payload(committed)
    real_head = str(committed_core.get("history", {}).get("head_checkpoint_id", ""))
    live_fp = str(committed_core.get("fingerprints", {}).get("after", ""))
    real_package = package_path(accepted_id).read_bytes()
    real_manifest, _ = read_package(real_package)
    validated_target = min(real_manifest["checkpoints"], key=lambda row: row["sequence"])["checkpoint_id"]
    exact = assess(client, accepted_id, real_head)
    check("v2 current lineage classifies exact", exact.get("classification") == "exact", exact)

    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": accepted_id, "checkpoint_id": validated_target,
        "output_path": f"edit-output/t019-{ARCH}-v2-ancestor.dll",
    })
    export_path = Path(str(payload(exported).get("output", {}).get("path", "")))
    raw_path = WORK / f"t019-{ARCH}-raw-writer.dll"
    process = subprocess.run([str(DOTNET), str(RAW_WRITER), str(export_path), str(raw_path)],
                             capture_output=True, text=True, timeout=120)
    check("alternate writer executes", process.returncode == 0 and raw_path.is_file(), process.stderr)
    raw_sha = sha_file(raw_path)
    canonical_sha = sha_file(export_path)
    check("alternate writer produces byte drift", raw_sha != canonical_sha, f"{raw_sha} {canonical_sha}")

    validated_id, validated_package = rewrite_package(real_package, image_sha=raw_sha, image_checkpoint_id=validated_target)
    unverified_id, unverified_package = rewrite_package(real_package, image_sha=raw_sha, semantic_flip=True)
    validated_path = inject(validated_package)[1]
    unverified_path = inject(unverified_package)[1]
    valid_before = store_snapshot()
    validated_manifest, _ = read_package(validated_package)
    unverified_manifest, _ = read_package(unverified_package)
    validated = assess(client, validated_id, validated_target)
    unverified = assess(client, unverified_id, unverified_manifest["head_checkpoint_id"])
    check("v2 alternate-writer drift classifies validated", validated.get("classification") == "validated_drift", validated)
    check("v2 semantic drift classifies unverified", unverified.get("classification") == "unverified_drift", unverified)

    denied_validated = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": validated_id, "checkpoint_id": validated_target,
        "action": "apply", "replay_id": validated.get("replay_id", ""), "expected_live_fingerprint": live_fp,
    })
    denied_unverified = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": unverified_id, "checkpoint_id": unverified_manifest["head_checkpoint_id"],
        "action": "apply", "replay_id": unverified.get("replay_id", ""), "expected_live_fingerprint": live_fp,
        "confirm_validated_drift": True,
    })
    check("validated drift requires confirmation", error_code(denied_validated) == "EDIT_REPLAY_CONFIRMATION_REQUIRED", denied_validated)
    check("unverified drift is never applicable", error_code(denied_unverified) == "EDIT_REPLAY_UNVERIFIED", denied_unverified)
    check("rejected restores preserve store", store_snapshot() == valid_before)
    check("rejected restores preserve live", live_fingerprint(client) == live_fp)

    # Re-assess because live_fingerprint() creates/consumes a new MCP session
    # transaction but does not alter the validated package ticket facts.
    validated = assess(client, validated_id, validated_target)
    confirmed = call(client, "edit_restore", {
        "request_id": rid(), "lineage_id": validated_id, "checkpoint_id": validated_target,
        "action": "apply", "replay_id": validated.get("replay_id", ""), "expected_live_fingerprint": live_fp,
        "confirm_validated_drift": True,
    })
    check("confirmed validated drift migrates", bool(confirmed.get("ok")), confirmed)
    confirmed_core = payload(confirmed)
    migration_head = str(confirmed_core.get("history", {}).get("head_checkpoint_id", ""))
    migrated_view = history_lineage(client, validated_id)
    nodes = [row for row in migrated_view.get("checkpoints", []) if isinstance(row, dict)]
    migration = next((row for row in nodes if row.get("checkpoint_id") == migration_head), {})
    check("migration child parent and kind", migration.get("kind") == "migration" and migration.get("parent_checkpoint_id") == validated_target, migration)
    check("migration child is new head", bool(migration.get("is_head")))
    check("migration child assesses exact", assess(client, validated_id, migration_head).get("classification") == "exact")
    migrated_live_fp = live_fingerprint(client)

    # Inject malformed futures only after all valid-lineage list operations.
    invalid_facts = []
    for label, kwargs in (
        ("package", {"package_format": "dnspy.edit.checkpoints.v99"}),
        ("kind", {"unknown_kind": True}),
        ("kind_version", {"unknown_version": True}),
    ):
        bad_id, bad_package = rewrite_package(real_package, **kwargs)
        bad_path = inject(bad_package)[1]
        pre_request = store_snapshot()
        response = call(client, "edit_history", {"lineage_id": bad_id, "page_size": 100})
        code = error_code(response)
        check(f"unknown {label} fails explicitly", code == "EDIT_OPERATION_VERSION_UNSUPPORTED", response)
        check(f"unknown {label} has no disk side effect", store_snapshot() == pre_request)
        invalid_facts.append({"label": label, "lineage_id": bad_id, "sha256": sha_file(bad_path),
                              "error_code": code, "response": response})

    final_fp = live_fingerprint(client)
    check("unknown versions preserve live", final_fp == migrated_live_fp)
    check("v1 packages remain unchanged after v2 flow", {key: sha_file(package_path(key)) for key in before_old} == before_old)
    check("v1 old lineages remain queryable by direct id", len(history_lineage(client, fixed_id).get("checkpoints", [])) == 3)

    facts = {
        "arch": ARCH, "consumer": {"deployed_dll_sha256": os.environ.get("DNMCP_T019_DLL_SHA", ""), "python_client_sha256": os.environ.get("DNMCP_T019_CLIENT_SHA", "")},
        "v1_producer": state["producer"], "v1_package_sha256": before_old,
        "v2": {"real_lineage": accepted_id, "real_head": real_head, "exact": exact,
               "canonical_image_sha256": canonical_sha, "alternate_writer_image_sha256": raw_sha,
               "alternate_writer_stdout": process.stdout.strip(), "alternate_writer_dll_sha256": sha_file(RAW_WRITER),
               "validated_lineage": validated_id, "unverified_lineage": unverified_id,
               "validated_target": validated_target,
               "migration_head": migration_head, "migration": migration,
               "validated_package_sha256_before": sha_bytes(validated_package),
               "validated_package_sha256_after": sha_file(validated_path),
               "unverified_package_sha256": sha_file(unverified_path)},
        "invalid": invalid_facts, "initial_live_fingerprint": initial_fp,
        "responses": {"exact_assess": exact, "validated_assess": validated,
                      "unverified_assess": unverified,
                      "validated_without_confirmation": denied_validated,
                      "unverified_apply": denied_unverified,
                      "validated_confirmed_migration": confirmed},
        "committed_live_fingerprint": live_fp, "migrated_live_fingerprint": migrated_live_fp,
        "final_live_fingerprint": final_fp,
        "final_store": store_snapshot(),
    }
    write_facts(f"t019-{ARCH}-v2.json", facts)


def main() -> int:
    client = DnSpyClient(URL, client_name=f"p03-vm-acc013-version-{PHASE}-{ARCH}")
    client.initialize()
    opened = call(client, "open_files", {"paths": [str(FIXTURE)]})
    check("fixture opened", bool(opened.get("ok")) or int(opened.get("loaded_count", 0)) == 1, opened)
    try:
        if PHASE == "v1_exact":
            phase_v1_exact(client)
        elif PHASE == "v1_drift":
            phase_v1_drift(client)
        elif PHASE == "v2":
            phase_v2(client)
        else:
            check("known T019 phase", False, PHASE)
    finally:
        client.close()
    print(f"ACC013-VERSION-{PHASE.upper()} {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
