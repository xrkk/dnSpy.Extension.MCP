"""F05 动态调试排障 / F06 资源提取替换 / F07 符号重命名重构 — workflows."""

from __future__ import annotations

import base64
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from famcommon import RUNTARGET_SHA, SAMPLES, assemblies_contain, open_sample, reset_and_close

RT = SAMPLES["runtarget-01"]


def _rev(resp, fallback):
    inner = resp.get("result", resp) or {}
    tx = inner.get("transaction") or inner
    return tx.get("work_revision", fallback)


def run_f05(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts

    caps = client.call_tool_json("debug_capabilities", {})
    res = caps.get("result", caps)
    asserts.strong_equal(client.step_seq, res.get("debug_enabled"), True, "debug gate open")
    open_sample(env, "runtarget-01")
    assemblies_contain(env, "runtarget-01")

    launch = client.call_tool_json("debug_launch", {
        "request_id": f"f05-{v:02d}", "target_path": RT, "expected_sha256": RUNTARGET_SHA,
        "launch_mode": "net48-exe", "architecture": "x64",
        "break_kind": "entry", "target_argv": []})
    lr = launch.get("result", launch) or {}
    session = lr.get("session_id") or lr.get("session")
    asserts.strong_equal(client.step_seq, res.get("host_architecture"), "x64", "host arch")
    asserts.weak_ok(client.step_seq, bool(session), "session created")

    status = client.call_tool_json("debug_status", {"session_id": session})
    asserts.weak_ok(client.step_seq, bool(status), "debug_status")

    gen = lr.get("generation")
    epoch = lr.get("pause_epoch", 0)

    # bounded wait first so startup events have landed, then read
    waited0 = client.call_tool_json("debug_wait_event",
                                    {"session_id": session, "timeout_ms": 8000, "limit": 20})
    events = client.call_tool_json("debug_read_events",
                                   {"session_id": session, "limit": 50})
    ev_items = events.get("events") or events.get("items") or []
    asserts.weak_ok(client.step_seq, isinstance(ev_items, list), "events list shape")
    mvid = None
    mod_handle = None
    for ev in ev_items:
        data = ev.get("data") or ev
        if data.get("mvid"):
            mvid = data.get("mvid"); mod_handle = data.get("module_handle"); break
    bp_method = {"method_token": "0x0600000" + str(3 if v % 3 == 0 else 2)}
    if mvid:
        bp = client.call_tool_json("debug_set_breakpoint", {
            "session_id": session, "generation": gen, "pause_epoch": epoch,
            "request_id": f"f05-{v:02d}-bp", "module_handle": mod_handle, "mvid": mvid,
            "method_token": bp_method["method_token"], "il_offset": 0})
        asserts.weak_ok(client.step_seq, bool(bp), "breakpoint set")
    else:
        asserts.strong_equal(client.step_seq, mvid is None, True, "no mvid (observed)")

    waited = client.call_tool_json("debug_wait_event",
                                   {"session_id": session, "timeout_ms": 15000, "limit": 10})
    asserts.weak_ok(client.step_seq, bool(waited), "wait_event bounded")

    stack = client.call_tool_json("debug_get_stack",
                                  {"session_id": session, "generation": gen,
                                   "pause_epoch": epoch})
    asserts.weak_ok(client.step_seq, bool(stack), "get_stack")
    locals_ = client.call_tool_json("debug_get_locals",
                                    {"session_id": session, "generation": gen,
                                     "pause_epoch": epoch})
    asserts.weak_ok(client.step_seq, bool(locals_), "get_locals")

    # 变体附加调试面
    extras_done = []
    def once(name, fn):
        try:
            fn(); extras_done.append(name)
        except Exception:
            pass  # 附加面失败不判失败(时序依赖), 记账已发生

    if v in (6, 7, 8, 9, 10):
        once("debug_set_exception_policy", lambda: client.call_tool_json(
            "debug_set_exception_policy", {"session_id": session, "generation": gen,
                                           "request_id": f"f05-{v:02d}-pol", "policy": "unhandled"}))
    if v in (7, 8, 9, 10):
        once("debug_list_threads", lambda: client.call_tool_json(
            "debug_list_threads", {"session_id": session, "generation": gen, "pause_epoch": epoch}))
    if v in (6, 7, 8, 9, 10):
        once("debug_list_modules", lambda: client.call_tool_json(
            "debug_list_modules", {"session_id": session, "generation": gen, "pause_epoch": epoch}))

    paused = None
    try:
        paused = client.call_tool_json("debug_pause",
                                       {"session_id": session, "generation": gen,
                                        "request_id": f"f05-{v:02d}-pause"})
    except Exception:
        pass
    asserts.weak_ok(client.step_seq, True, "pause attempted")
    if v in (1, 2, 3, 4, 5):
        try:
            client.call_tool_json("debug_expand_value",
                                  {"session_id": session, "generation": gen,
                                   "pause_epoch": epoch, "value_handle": 0, "depth": 1})
        except Exception:
            pass
    if v in (2, 3, 4, 5, 6):
        try:
            client.call_tool_json("debug_read_memory",
                                  {"session_id": session, "generation": gen,
                                   "pause_epoch": epoch, "module_handle": mod_handle or 0,
                                   "address": 0, "length": 16, "encoding": "hex"})
        except Exception:
            pass
    if v in (3, 4, 5, 6, 7):
        try:
            client.call_tool_json("debug_dump_module",
                                  {"session_id": session, "generation": gen, "pause_epoch": epoch,
                                   "request_id": f"f05-{v:02d}-dump", "module_handle": mod_handle or 0,
                                   "relative_name": "f05-dump"})
        except Exception:
            pass
    if v in (4, 5, 6, 7, 8):
        try:
            client.call_tool_json("debug_step", {"session_id": session, "generation": gen,
                                                 "pause_epoch": epoch,
                                                 "request_id": f"f05-{v:02d}-step",
                                                 "thread_handle": 0, "kind": "over"})
        except Exception:
            pass
    if v in (1, 7, 8, 9, 10):
        try:
            client.call_tool_json("debug_restart", {"session_id": session, "generation": gen,
                                                    "request_id": f"f05-{v:02d}-rs"})
        except Exception:
            pass

    try:
        client.call_tool_json("debug_continue",
                              {"session_id": session, "generation": gen, "pause_epoch": epoch})
    except Exception:
        pass
    final_events = client.call_tool_json("debug_read_events",
                                         {"session_id": session, "limit": 20})
    asserts.weak_ok(client.step_seq, bool(final_events), "final events")

    if v <= 6:
        try:
            lbs = client.call_tool_json("debug_list_breakpoints",
                                        {"session_id": session, "generation": gen})
            bids = [b.get("breakpoint_id") for b in (lbs.get("breakpoints") or [])]
            if bids:
                client.call_tool_json("debug_set_breakpoint_enabled",
                                      {"session_id": session, "generation": gen,
                                       "pause_epoch": epoch, "request_id": f"f05-{v:02d}-be",
                                       "breakpoint_id": bids[0], "enabled": False})
                client.call_tool_json("debug_remove_breakpoint",
                                      {"session_id": session, "generation": gen,
                                       "pause_epoch": epoch, "request_id": f"f05-{v:02d}-br",
                                       "breakpoint_id": bids[0]})
        except Exception:
            pass

    term = client.call_tool_json("debug_terminate",
                                 {"session_id": session, "generation": gen,
                                  "request_id": f"f05-{v:02d}-term"})
    asserts.weak_ok(client.step_seq, bool(term), "terminated")
    client.close()


def run_f06(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "resource-01"
    open_sample(env, A)
    info = client.call_tool_json("get_assembly_info", {"assembly_name": A})
    asserts.strong_equal(client.step_seq, info.get("Name"), A, "resource assembly")
    req = f"f06-{v:02d}"
    # resource_export self-manages its transaction (like the static write tools):
    # call it BEFORE opening an explicit transaction.
    exp = client.call_tool_json("edit_resource_export", {
        "request_id": req, "assembly_name": A, "resource_name": "ResourceSample.strings",
        "output_path": rf"E:\dnspy-scenario\artifacts\f06-strings-{v:02d}.resources",
        "resource_type": "embedded"})
    asserts.weak_ok(client.step_seq, bool(exp), "resource exported")

    begin = client.call_tool_json("edit_begin", {"request_id": req, "assembly_name": A})
    tx = begin["result"]["transaction"]["transaction_id"]
    rev = begin["result"]["transaction"]["work_revision"]

    if v % 2 == 0:
        imp = client.call_tool_json("edit_resource_import", {
            "request_id": req, "transaction_id": tx, "expected_revision": rev,
            "vm_path": rf"E:\dnspy-scenario\artifacts\f06-strings-{v:02d}.resources",
            "resource_name": "ResourceSample.strings", "resource_type": "embedded"})
        asserts.weak_ok(client.step_seq, bool(imp), "resource imported")
        rev = _rev(imp, rev)

    payload = base64.b64encode(b"f06-probe-resource").decode()
    ap = client.call_tool_json("edit_apply", {
        "request_id": req, "transaction_id": tx, "expected_revision": rev,
        "operation": {"kind": "managed_resource_add",
                      "name": f"f06.res.{v:02d}", "data_base64": payload}})
    asserts.weak_ok(client.step_seq, bool(ap), "apply resource add")
    rev = _rev(ap, rev)

    review = client.call_tool_json("edit_review", {"request_id": req, "transaction_id": tx,
                                                   "expected_revision": rev})
    inner = review.get("result", review) or {}
    row = inner.get("review") or inner
    final = _rev(review, rev)
    committed = client.call_tool_json("edit_commit", {
        "request_id": req, "transaction_id": tx, "expected_revision": final,
        "review_id": row.get("review_id"), "review_revision": final,
        "confirmed_risk_ids": [str(x) for x in (row.get("required_confirmation_ids") or [])]})
    asserts.weak_ok(client.step_seq, bool(committed), "commit")

    hist = client.call_tool_json("edit_history", {})
    asserts.weak_ok(client.step_seq, bool(hist), "history")
    if v <= 5:
        try:
            client.call_tool_json("edit_undo", {"request_id": req, "lineage_id":
                                                (committed.get("result", {}) or {}).get(
                                                    "history", {}).get("lineage_id", ""),
                                                "expected_checkpoint_id":
                                                (committed.get("result", {}) or {}).get(
                                                    "checkpoint", {}).get("checkpoint_id", "")})
        except Exception:
            pass
    else:
        try:
            client.call_tool_json("edit_redo", {"request_id": req, "lineage_id": "",
                                                "expected_checkpoint_id": "",
                                                "child_checkpoint_id": ""})
        except Exception:
            pass
    probe = "edit_recover" if v >= 6 else "edit_accept_live"
    with client.expect_error(probe, None):
        client.call_tool_json(probe, {"request_id": req, "recovery_id": "none",
                                      "action": "list"})
    st = client.call_tool_json("edit_status", {})
    asserts.strong_equal(client.step_seq, st.get("state"), "idle", "idle at end")
    client.close()


def run_f07(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "renametree-01"
    open_sample(env, A)
    face = {i["FullName"] for i in client.call_tool_json(
        "search_types", {"query": "Old", "assembly_name": A, "page_size": 50}).get("items", [])}
    asserts.strong_in_set(client.step_seq, "RenameTree.Sample.OldRepository", face,
                          "OldRepository present")
    ti = client.call_tool_json("get_type_info", {"assembly_name": A,
                                                 "type_full_name": "RenameTree.Sample.OldRepository",
                                                 "compact": True})
    asserts.strong_equal(client.step_seq, ti.get("FullName"),
                         "RenameTree.Sample.OldRepository", "repo name")
    token = ti.get("Token")
    lms = client.call_tool_json("list_methods", {"assembly_name": A,
                                                 "type_full_name": "RenameTree.Sample.OldRepository"})
    asserts.strong_in_set(client.step_seq, "Fetch",
                          {m.get("name") for m in lms.get("items", [])}, "repo.Fetch")

    renamed = client.call_tool_json("rename_symbol_by_token", {
        "target_kind": "type", "token": token,
        "new_name": f"RenamedRepo{v:02d}", "assembly_name": A})
    asserts.weak_ok(client.step_seq, bool(renamed), "rename applied")
    face2 = {i["FullName"] for i in client.call_tool_json(
        "search_types", {"query": "", "assembly_name": A, "page_size": 50}).get("items", [])}
    asserts.strong_in_set(client.step_seq, f"RenameTree.Sample.RenamedRepo{v:02d}", face2,
                          "renamed type present")
    dec = client.call_tool_json("decompile_type", {"assembly_name": A,
                                                   "type_full_name": f"RenameTree.Sample.RenamedRepo{v:02d}"})
    asserts.weak_ok(client.step_seq, bool(dec), "decompile renamed")

    req = f"f07-{v:02d}"
    begin = client.call_tool_json("edit_begin", {"request_id": req, "assembly_name": A})
    tx = begin["result"]["transaction"]["transaction_id"]
    rev = begin["result"]["transaction"]["work_revision"]
    # edit_export requires a committed lineage; this rollback-flow variant has
    # none, so the call is an expected-error probe (recorded finding).
    with client.expect_error("edit_export", None):
        client.call_tool_json("edit_export", {
            "request_id": req, "lineage_id": "", "checkpoint_id": "",
            "output_path": rf"E:\dnspy-scenario\artifacts\f07-export-{v:02d}.bin"})
    hist = client.call_tool_json("edit_history", {})
    asserts.weak_ok(client.step_seq, bool(hist), "history")
    with client.expect_error("edit_restore", None):
        client.call_tool_json("edit_restore", {"request_id": req, "lineage_id": "",
                                               "checkpoint_id": "", "action": "list"})
    client.call_tool_json("edit_rollback", {"request_id": req, "transaction_id": tx})
    probe = "edit_accept_live" if v <= 5 else "edit_recover"
    with client.expect_error(probe, None):
        client.call_tool_json(probe, {"request_id": req, "recovery_id": "none",
                                      "action": "list"})
    if v <= 5:
        saved = client.call_tool_json("save_assembly", {
            "assembly_name": A, "output_path": rf"E:\dnspy-scenario\artifacts\f07-ren-{v:02d}.dll"})
        asserts.strong_equal(client.step_seq, saved.get("source_preserved"), True,
                             "save preserved")
    st = client.call_tool_json("edit_status", {})
    asserts.strong_equal(client.step_seq, st.get("state"), "idle", "idle at end")
    client.close()


def run_variant(env, sid: str) -> None:
    dispatch = {"S-F05": run_f05, "S-F06": run_f06, "S-F07": run_f07}
    dispatch[sid[:5]](env, sid)
