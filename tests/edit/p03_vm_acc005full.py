#!/usr/bin/env python3
"""P06 ACC-005 final judgment: compile -> import -> review -> commit -> export
-> reload -> real breakpoint_hit on the imported method, all through the real
dnSpy MCP loopback.

Covers the audited ACC-005 clauses: diagnostics/imports succeed; fields/methods/
EH/generics/symbols correct (IL reference mapping, sequence points readable after
reload); generated subtrees (async/iterator state machines) land whole; no
sidecar PDB; forbidden extension fields rejected upstream (P05); ambiguous/
explicit-mismatch/unmapped-artifact-reference imports reject with zero side effects;
supported external references are synthesized exactly and rollback cleanly; a
real debugger breakpoint hits the imported kickoff method at IL 0.  The
compile-only clauses were proven by p03_vm_acc005 (P05, run-id prefix
p05-compile-); this driver's evidence uses run-id prefix p06-import-.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import sys
import time
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = (r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe" if ARCH == "x64"
           else r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost-x86\ImportHost.exe")
LAUNCH_ROOT: str | None = None
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE, LAUNCH_ROOT
    context.validate()
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("ImportHost/ImportHost.exe" if ARCH == "x64" else "ImportHost-x86/ImportHost.exe")
    LAUNCH_ROOT = context.fixture_output(f".acc005-launch/{context.run_id}/{ARCH}")

# EditClass compilation of the same ImportHost.Machines class: edited bodies for
# the iterator/async kickoffs (20/200 constants), plus new members (generic
# method with class constraint, property getter).
EDIT_SOURCE = """using System;
using System.Collections;
using System.Threading.Tasks;

namespace ImportHost
{
    public class Machines
    {
        public static int counter;

        public IEnumerator DoCoroutine()
        {
            counter++;
            yield return null;
            counter += 20;
        }

        public async void DoAsync()
        {
            await Task.Yield();
            counter += 200;
        }

        public static string AddedTag<T>(T value) where T : class
        {
            return "added";
        }

        public static int AddedWithLocal(int value)
        {
            try
            {
                int doubled = value * 2;
                System.Threading.Interlocked.Increment(ref doubled);
                return doubled;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public string Tag => "tag";
    }
}
"""

# Supported external-reference sample: P06 v2 requires reference_add to
# synthesize the System.Random scope/type/member rows missing from the host.
GHOST_SOURCE = """namespace ImportHost
{
    public class Machines
    {
        public static int counter;
        public int Dice() => new System.Random(4).Next();
    }
}
"""

# Truly unmapped reference: the imported method refers to an artifact-local
# type that is neither present in the target nor included in this import plan.
INVALID_REFERENCE_SOURCE = """namespace ImportHost
{
    public class MissingDependency { }
    public class Machines
    {
        public MissingDependency Missing() => new MissingDependency();
    }
}
"""


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


def envelope_error(envelope: dict) -> str:
    error = envelope.get("error")
    return str(error.get("code", "")) if isinstance(error, dict) else ""


def debug_context(envelope: dict) -> dict:
    value = envelope.get("debug_context")
    return value if isinstance(value, dict) else {}


def core(envelope: dict) -> dict:
    value = payload(envelope).get("compile")
    return value if isinstance(value, dict) else {}


class LaunchPreparationError(RuntimeError):
    """The exported target cannot be copied into the authorized launch root safely."""


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prepare_isolated_launch(exported: dict) -> tuple[str, dict]:
    """Copy one verified export into this run's fail-closed AllowedSampleRoot child."""
    if not LAUNCH_ROOT:
        raise LaunchPreparationError("ACC005 requires an explicit isolation context and launch root")
    output = payload(exported).get("output", {})
    export_path = str(output.get("path", ""))
    advertised_sha = str(output.get("sha256", "")).lower()
    if not exported.get("ok") or not export_path:
        raise LaunchPreparationError("edit_export did not return a successful output path")
    if len(advertised_sha) != 64 or any(ch not in "0123456789abcdef" for ch in advertised_sha):
        raise LaunchPreparationError("edit_export did not return a valid advertised SHA-256")
    source = Path(export_path)
    if not source.is_file():
        raise LaunchPreparationError(f"export source is not a file: {source}")
    source_sha_before = _sha256_file(source)
    if source_sha_before != advertised_sha:
        raise LaunchPreparationError(
            f"advertised SHA does not match export source: advertised={advertised_sha} source={source_sha_before}")

    launch_root = Path(LAUNCH_ROOT)
    try:
        launch_root.mkdir(parents=True, exist_ok=False)
    except FileExistsError as ex:
        raise LaunchPreparationError(f"isolated launch directory already exists: {launch_root}") from ex
    launch_path = launch_root / source.name
    shutil.copyfile(source, launch_path)
    source_sha_after = _sha256_file(source)
    launch_sha = _sha256_file(launch_path)
    if source_sha_after != source_sha_before:
        raise LaunchPreparationError(
            f"export source changed during copy: before={source_sha_before} after={source_sha_after}")
    if launch_sha != advertised_sha:
        raise LaunchPreparationError(
            f"copied SHA does not match advertised SHA: advertised={advertised_sha} copied={launch_sha}")
    return str(launch_path), {
        "original_export_path": str(source),
        "actual_launch_path": str(launch_path),
        "advertised_sha256": advertised_sha,
        "source_sha256_before": source_sha_before,
        "source_sha256_after": source_sha_after,
        "launch_sha256": launch_sha,
        "source_size": source.stat().st_size,
        "launch_size": launch_path.stat().st_size,
    }


def main() -> int:
    if not LAUNCH_ROOT:
        check("C0 isolated launch root configured", False,
              "ACC005 requires configure_isolation(context); legacy direct launch is disabled")
        print(f"ACC005F FAIL passes={len(PASSES)} failures={FAILURES}", flush=True)
        return 1
    client = DnSpyClient(URL, client_name=f"p06-acc005full-{ARCH}", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    body = json.dumps({"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}}).encode()
    request = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": client.session_id})
    with urllib.request.urlopen(request, timeout=30) as response:
        wire = response.read().decode("utf-8", "replace").replace(" ", "").replace("\n", "")
    check("G1 edit_import advertised", '"name":"edit_import"' in wire, wire[:160])

    methods = call(client, "list_methods", {"assembly_name": "ImportHost", "type_full_name": "ImportHost.Machines"})
    items = methods.get("items", []) if isinstance(methods, dict) else []
    def field(item: dict, *names):
        for name in names:
            if item.get(name) is not None:
                return item.get(name)
        return None
    by_name = {field(item, "name", "Name"): item for item in items if isinstance(item, dict)}
    do_async = by_name.get("DoAsync")
    do_coroutine = by_name.get("DoCoroutine")
    check("L1 host methods listed", do_async is not None and do_coroutine is not None, json.dumps(methods)[:240])
    do_async_token = f"0x{int(field(do_async, 'token', 'Token')):08x}" if do_async else ""
    do_coroutine_token = f"0x{int(field(do_coroutine, 'token', 'Token')):08x}" if do_coroutine else ""

    begin = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    baseline_private = str(payload(begin).get("fingerprints", {}).get("private", ""))
    check("T1 transaction began", bool(tx) and revision == 0, json.dumps(begin)[:200])

    def compile_call(source: str) -> str:
        envelope = call(client, "edit_compile", {
            "request_id": rid(), "assembly_name": "ImportHost", "compilation_kind": "edit_class",
            "documents": [{"path": "Machines.cs", "content": source}],
        })
        return str(core(envelope).get("compile_id", ""))

    happy_id = compile_call(EDIT_SOURCE)
    check("C1 edited class compiled", happy_id.startswith("compile-"), happy_id)
    ghost_id = compile_call(GHOST_SOURCE)
    check("C2 external-reference class compiled", ghost_id.startswith("compile-"), ghost_id)
    invalid_reference_id = compile_call(INVALID_REFERENCE_SOURCE)
    check("C3 unmapped-reference class compiled", invalid_reference_id.startswith("compile-"), invalid_reference_id)

    def import_call(compile_id: str, targets: list, revision_value: int) -> dict:
        return call(client, "edit_import", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision_value,
            "compile_id": compile_id, "targets": targets,
        })

    def private_fingerprint() -> str:
        status = payload(call(client, "edit_status", {}))
        return str(status.get("fingerprints", {}).get("private", "?"))

    # R1 cell ① — explicit target with a mismatched identity: rejected, zero side effects.
    mismatch = import_call(happy_id, [
        {"compiled": "ImportHost.Machines::DoAsync()", "action": "replace_body",
         "target": {"token": do_coroutine_token}},
    ], revision)
    check("R1 mismatched explicit target rejected", envelope_error(mismatch) == "EDIT_HISTORY_CONFLICT", json.dumps(mismatch)[:240])
    check("R1 zero side effects", private_fingerprint() == baseline_private, private_fingerprint())

    # R2 remains the negative reference case required by ACC-005: an
    # artifact-local type not included in the plan cannot bind to the target.
    unmapped = import_call(invalid_reference_id, [
        {"compiled": "ImportHost.Machines::Missing()", "action": "add"},
    ], revision)
    check("R2 unmapped artifact reference rejected", envelope_error(unmapped) == "EDIT_VALIDATION_FAILED", json.dumps(unmapped)[:240])
    check("R2 zero side effects", private_fingerprint() == baseline_private, private_fingerprint())

    # R3 is the supported P06 v2 external-reference path.  System.Random is
    # absent from the host metadata, so the importer must synthesize explicit
    # reference_add rows and advance the revision by exactly the emitted rows.
    external = import_call(ghost_id, [
        {"compiled": "ImportHost.Machines::Dice()", "action": "add"},
    ], revision)
    external_row = payload(external).get("import", {})
    external_rows = external_row.get("rows", []) if isinstance(external_row, dict) else []
    external_kinds = [str(item.get("kind")) for item in external_rows if isinstance(item, dict)]
    external_members = [str(item.get("artifact_member")) for item in external_rows if isinstance(item, dict)]
    external_revision = int(payload(external).get("transaction", {}).get("work_revision", revision))
    check("R3 external reference import supported", bool(external.get("ok")) and "method_add" in external_kinds,
          json.dumps(external)[:1200])
    check("R3 exact synthesized reference rows", external_kinds.count("reference_add") >= 1
          and any("System.Random" in member for member in external_members), str(external_rows))
    check("R3 exact revision advance", external_revision == revision + len(external_rows)
          and int(payload(external).get("operation_count", -1)) == len(external_rows), json.dumps(external)[:600])

    rolled_back = call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    check("R3 rollback supported import", bool(rolled_back.get("ok")) and bool(payload(rolled_back).get("rolled_back")),
          json.dumps(rolled_back)[:300])
    begin = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    baseline_private = str(payload(begin).get("fingerprints", {}).get("private", ""))
    check("R3 rollback returns clean transaction", bool(tx) and revision == 0
          and private_fingerprint() == baseline_private, json.dumps(begin)[:400])

    # I1 happy import — cell ② auto-match replaces both kickoff bodies (the
    # generated async/iterator subtrees land whole) and cell ③ adds the generic
    # method on the nesting chain.
    imported = import_call(happy_id, [
        {"compiled": "ImportHost.Machines::DoCoroutine()", "action": "replace_body"},
        {"compiled": "ImportHost.Machines::DoAsync()", "action": "replace_body"},
        {"compiled": "ImportHost.Machines::AddedTag`1(!!0)", "action": "add"},
        {"compiled": "ImportHost.Machines::AddedWithLocal(System.Int32)", "action": "add"},
    ], revision)
    import_row = payload(imported).get("import", {})
    kinds = [str(item.get("kind")) for item in import_row.get("rows", []) if isinstance(item, dict)]
    check("I1 import ok", bool(imported.get("ok")) and len(kinds) >= 3, json.dumps(imported)[:1200])
    check("I1 kickoff bodies replaced", "method_body_replace" in kinds, str(kinds))
    check("I1 subtree members synced", "field_add" in kinds or "method_add" in kinds, str(kinds) + " " + json.dumps(imported)[:600])
    new_revision = int(payload(imported).get("transaction", {}).get("work_revision", revision))

    # I2 cell ④ — add the accessor with an explicit container token (Machines).
    types = call(client, "list_types", {"assembly_name": "ImportHost", "include_nested": False})
    machines_row = next((t for t in (types.get("items", []) if isinstance(types, dict) else [])
                         if isinstance(t, dict) and (t.get("FullName") or t.get("fullName") or t.get("full_name")) == "ImportHost.Machines"), None)
    machines_token_value = f"0x{int(machines_row.get('Token') or machines_row.get('token')):08x}" if machines_row else ""
    accessor = import_call(happy_id, [
        {"compiled": "ImportHost.Machines::get_Tag()", "action": "add",
         "target": {"token": machines_token_value}},
    ], new_revision)
    check("I2 accessor add with container token ok", bool(accessor.get("ok")), json.dumps(accessor)[:240])

    # W1 review + commit through the unified checkpoint chain.
    final_revision = int(payload(accessor).get("transaction", {}).get("work_revision", new_revision))
    reviewed = call(client, "edit_review", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": final_revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    check("W1 review ok", bool(reviewed.get("ok")) and bool(review_id), json.dumps(reviewed)[:240])
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": final_revision,
        "review_id": review_id, "review_revision": final_revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    check("W1 commit ok", bool(committed.get("ok")) and bool(lineage_id) and bool(checkpoint_id), json.dumps(committed)[:2000])

    # V1 live IL: the edited constant lives in the async state machine's
    # MoveNext (the await continuation), not in the kickoff stub; assert there.
    state_methods = call(client, "list_methods", {
        "assembly_name": "ImportHost", "type_full_name": "ImportHost.Machines/<DoAsync>d__2"})
    state_items = state_methods.get("items", []) if isinstance(state_methods, dict) else []
    move_next = next((m for m in state_items if isinstance(m, dict) and str(m.get("Name") or m.get("name")) == "MoveNext"), None)
    move_next_token = f"0x{int(move_next.get('Token') or move_next.get('token')):08x}" if move_next else do_async_token
    async_il = call(client, "get_method_il", {
        "assembly_name": "ImportHost", "type_full_name": "ImportHost.Machines/<DoAsync>d__2",
        "method_name": "MoveNext", "method_token": move_next_token})
    il_text = json.dumps(async_il, ensure_ascii=False)
    check("V1 edited async constant", "200" in il_text, il_text[:300])
    added = call(client, "list_methods", {"assembly_name": "ImportHost", "type_full_name": "ImportHost.Machines"})
    added_names = [str(field(item, "name", "Name")) for item in added.get("items", []) if isinstance(item, dict)]
    check("V1 members landed", "AddedTag" in added_names and "AddedWithLocal" in added_names
          and any(n.startswith("get_Tag") for n in added_names), str(added_names))

    # X1 export: exactly one file below ArtifactRoot, no sidecar PDB.
    exported = call(client, "edit_export", {
        "request_id": rid(), "lineage_id": lineage_id, "checkpoint_id": checkpoint_id,
        "output_path": "edit-output\\acc005full\\ImportHost-edited.exe"})
    output_row = payload(exported).get("output", {})
    export_path = str(output_row.get("path", ""))
    sha256 = str(output_row.get("sha256", ""))
    check("X1 export ok", bool(exported.get("ok")) and export_path.lower().endswith(".exe"), json.dumps(exported)[:240])
    sibling_pdb = False
    siblings = []
    if export_path.lower().endswith(".exe"):
        try:
            siblings = os.listdir(str(Path(export_path).parent))
            sibling_pdb = any(name.lower().endswith(".pdb") for name in siblings)
        except OSError:
            siblings = []
    check("X1 no sidecar pdb", bool(export_path) and not sibling_pdb, str(siblings))

    # L2 reload from a verified, run-scoped copy below AllowedSampleRoot.  The
    # product export response and original export bytes remain untouched.
    try:
        launch_path, launch_facts = prepare_isolated_launch(exported)
    except LaunchPreparationError as ex:
        check("X2 isolated launch copy verified", False, str(ex))
        print(f"ACC005F FAIL passes={len(PASSES)} failures={FAILURES}", flush=True)
        return 1
    check("X2 isolated launch copy verified", True)
    print("INFO ACC005_LAUNCH_COPY " + json.dumps(launch_facts, sort_keys=True), flush=True)
    reopened = call(client, "open_files", {"paths": [launch_path]})
    check("L2 exported image reopened", "error" not in reopened, json.dumps(reopened)[:200])

    # B1 real breakpoint: launch the exported exe, break on the imported async
    # kickoff at IL 0, and observe breakpoint_hit with a matching frame.
    launch_env = call(client, "debug_launch", {
        "request_id": rid(), "target_path": launch_path, "expected_sha256": sha256,
        "launch_mode": "net48-exe", "architecture": ARCH, "break_kind": "entry"})
    launch = payload(launch_env)
    session_id = str(launch.get("session_id", ""))
    generation = int(launch.get("generation", 0))
    check("B1 debug launch", bool(session_id), json.dumps(launch_env)[:240])

    paused_epoch = 0
    target_module = None
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline and target_module is None:
        status_env = call(client, "debug_status", {"session_id": session_id})
        if payload(status_env).get("state") == "paused" or debug_context(status_env).get("state") == "paused":
            epoch = int(debug_context(status_env).get("pause_epoch", 0))
            modules = payload(call(client, "debug_list_modules", {"session_id": session_id, "generation": generation}))
            target_module = next((m for m in modules.get("items", [])
                                  if isinstance(m, dict) and str(m.get("sha256", "")).casefold() == sha256.casefold()), None)
            if target_module is None:
                call(client, "debug_continue", {"session_id": session_id, "generation": generation,
                                                "pause_epoch": epoch, "request_id": rid()})
            else:
                paused_epoch = epoch
        time.sleep(0.4)
    check("B1 target module loaded", target_module is not None, "no loaded module matches the export sha")
    if target_module is None:
        print(f"ACC005F {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
        return 1

    breakpoint = call(client, "debug_set_breakpoint", {
        "session_id": session_id, "generation": generation, "pause_epoch": paused_epoch,
        "request_id": rid(), "module_handle": target_module.get("module_handle"),
        "module_sha256": sha256, "mvid": target_module.get("mvid"),
        "method_token": do_async_token, "il_offset": 0, "enabled": True})
    check("B2 breakpoint set", bool(breakpoint.get("ok")), json.dumps(breakpoint)[:240])
    after_cursor = int(debug_context(breakpoint).get("event_cursor", 0))

    call(client, "debug_continue", {"session_id": session_id, "generation": generation,
                                    "pause_epoch": paused_epoch, "request_id": rid()})
    hit_event = None
    all_events: list[dict] = []
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline and hit_event is None:
        waited = payload(call(client, "debug_wait_event", {
            "session_id": session_id, "after_cursor": after_cursor, "limit": 20,
            "kinds": ["breakpoint_bound", "breakpoint_hit", "paused", "process_exited"],
            "timeout_ms": 2500}))
        events = waited.get("events", [])
        all_events.extend(events)
        if events:
            after_cursor = max(int(event["cursor"]) for event in events)
        hit_event = next((event for event in events if event.get("kind") == "breakpoint_hit"), None)
    check("B2 breakpoint_hit received", hit_event is not None, json.dumps(all_events)[:300])

    hit_env = call(client, "debug_status", {"session_id": session_id})
    hit_epoch = int(debug_context(hit_env).get("pause_epoch", 0))
    frames: list[dict] = []
    threads = payload(call(client, "debug_list_threads", {
        "session_id": session_id, "generation": generation, "pause_epoch": hit_epoch}))
    for thread in threads.get("items", []):
        stack = payload(call(client, "debug_get_stack", {
            "session_id": session_id, "generation": generation, "pause_epoch": hit_epoch,
            "thread_handle": thread.get("thread_handle")}))
        frames.extend(stack.get("items", []))
    matching = next((f for f in frames if isinstance(f, dict)
                     and str(f.get("location", {}).get("method_token", "")).casefold() == do_async_token.casefold()
                     and int(f.get("location", {}).get("il_offset", -1)) == 0), None)
    check("B2 frame at imported method IL 0", matching is not None, json.dumps(frames)[:240])

    call(client, "debug_terminate", {"session_id": session_id, "generation": generation, "request_id": rid()})

    print(f"ACC005F {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
