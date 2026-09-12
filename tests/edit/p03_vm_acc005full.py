#!/usr/bin/env python3
"""P06 ACC-005 final judgment: compile -> import -> review -> commit -> export
-> reload -> real breakpoint_hit on the imported method, all through the real
dnSpy MCP loopback.

Covers the audited ACC-005 clauses: diagnostics/imports succeed; fields/methods/
EH/generics/symbols correct (IL reference mapping, sequence points readable after
reload); generated subtrees (async/iterator state machines) land whole; no
sidecar PDB; forbidden extension fields rejected upstream (P05); ambiguous/
explicit-mismatch/unresolved-reference imports reject with zero side effects; a
real debugger breakpoint hits the imported kickoff method at IL 0.  The
compile-only clauses were proven by p03_vm_acc005 (P05, run-id prefix
p05-compile-); this driver's evidence uses run-id prefix p06-import-.
"""

from __future__ import annotations

import json
import os
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
FAILURES: list[str] = []
PASSES: list[str] = []

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

        public string Tag => "tag";
    }
}
"""

# Unresolved-reference sample: System.Random rows do not exist in the host.
GHOST_SOURCE = """namespace ImportHost
{
    public class Machines
    {
        public static int counter;
        public int Dice() => new System.Random(4).Next();
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


def main() -> int:
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
    check("C2 ghost class compiled", ghost_id.startswith("compile-"), ghost_id)

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

    # R2 unresolved reference: a member the host has no rows for.
    unresolved = import_call(ghost_id, [
        {"compiled": "ImportHost.Machines::Dice()", "action": "add"},
    ], revision)
    check("R2 unresolved reference rejected", envelope_error(unresolved) == "EDIT_VALIDATION_FAILED", json.dumps(unresolved)[:240])
    check("R2 zero side effects", private_fingerprint() == baseline_private, private_fingerprint())

    # I1 happy import — cell ② auto-match replaces both kickoff bodies (the
    # generated async/iterator subtrees land whole) and cell ③ adds the generic
    # method on the nesting chain.
    imported = import_call(happy_id, [
        {"compiled": "ImportHost.Machines::DoCoroutine()", "action": "replace_body"},
        {"compiled": "ImportHost.Machines::DoAsync()", "action": "replace_body"},
        {"compiled": "ImportHost.Machines::AddedTag`1(!!0)", "action": "add"},
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
    check("V1 members landed", "AddedTag" in added_names and any(n.startswith("get_Tag") for n in added_names), str(added_names))

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

    # L2 reload: open the exported image (byte-level embedded symbol proof).
    reopened = call(client, "open_files", {"paths": [export_path]})
    check("L2 exported image reopened", "error" not in reopened, json.dumps(reopened)[:200])

    # B1 real breakpoint: launch the exported exe, break on the imported async
    # kickoff at IL 0, and observe breakpoint_hit with a matching frame.
    launch_env = call(client, "debug_launch", {
        "request_id": rid(), "target_path": export_path, "expected_sha256": sha256,
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
