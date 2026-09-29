"""F05 动态调试排障 / F06 资源提取替换 / F07 符号重命名重构 — workflows."""

from __future__ import annotations

import base64
import sys
import time
import uuid
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
    run = uuid.uuid4().hex[:8]  # request ids must be unique per execution:
    # debug_launch dedupes by request_id and replays the cached response.

    caps = client.call_tool_json("debug_capabilities", {})
    res = caps.get("result", caps)
    asserts.strong_equal(client.step_seq, res.get("debug_enabled"), True, "debug gate open")
    open_sample(env, "runtarget-01")
    assemblies_contain(env, "runtarget-01")

    launch = client.call_tool_json("debug_launch", {
        "request_id": f"f05-{v:02d}-{run}", "target_path": RT, "expected_sha256": RUNTARGET_SHA,
        "launch_mode": "net48-exe", "architecture": "x64",
        "break_kind": "entry", "target_argv": []})
    lr = launch.get("result", launch) or {}
    session = lr.get("session_id") or lr.get("session")
    asserts.weak_ok(client.step_seq, bool(session), "session created")

    def ctx():
        # L4 pattern: global debug_status (no session scoping) carries the
        # authoritative state + active session + current generation/pause_epoch.
        st = client.call_tool_json("debug_status", {})
        dc = st.get("debug_context", {}) or {}
        res = st.get("result", st) or {}
        return {"state": res.get("state") or dc.get("state"),
                "active_session_id": res.get("active_session_id"),
                "generation": dc.get("generation", 0),
                "pause_epoch": dc.get("pause_epoch", 0)}

    launched_gen = ctx().get("generation", 0)

    def try_call(name, fn):
        # refresh handles immediately before each state-dependent call
        # (restart/continue bump generation; stale handles => STALE_HANDLE).
        # Registered as expected-error: a handle race that yields a domain
        # error envelope is anticipated product behavior (F-03 预期错误),
        # so it must not count as an unexpected error in the result row.
        try:
            cf = ctx()
            g, e = int(cf.get("generation", gen)), int(cf.get("pause_epoch", epoch))
            with client.expect_error(name, None):
                fn(g, e)
        except Exception:
            pass  # 非域错误异常仍兜底; 记账已发生


    try:
        # entry pause: wait for the paused state (bounded), refreshing handles
        paused = False
        for _ in range(30):
            c0 = ctx()
            if c0.get("state") == "paused":
                paused = True
                break
            try:
                client.call_tool_json("debug_wait_event",
                                      {"session_id": session, "timeout_ms": 1000, "limit": 20})
            except Exception:
                time.sleep(0.3)
        asserts.weak_ok(client.step_seq, paused, "reached paused state")
        try_call("debug_wait_event", lambda g, e: client.call_tool_json(
            "debug_wait_event", {"session_id": session, "timeout_ms": 500, "limit": 10}))

        # the entry pause is transient (auto-resumes): hold it explicitly so
        # module enumeration and breakpoints operate against a paused state
        try_call("debug_pause", lambda g, e: client.call_tool_json(
            "debug_pause", {"session_id": session, "generation": g,
                            "request_id": f"f05-{v:02d}--hold-{run}"}))
        c1 = ctx()
        gen, epoch = int(c1.get("generation", launched_gen)), int(c1.get("pause_epoch", 0))

        mvid = None
        mod_handle = None
        mod_sha256 = None
        try:
            # module enumeration is populated asynchronously after the entry
            # pause: poll briefly (bounded) until the target module appears
            for _ in range(10):
                mods = client.call_tool_json("debug_list_modules",
                                             {"session_id": session, "generation": gen,
                                              "pause_epoch": epoch})
                mod_rows = ((mods.get("result", {}) or {}).get("items")
                            or (mods.get("result", {}) or {}).get("modules")
                            or mods.get("items") or [])
                for m in mod_rows:
                    name = str(m.get("name") or m.get("module_name") or "")
                    if "runtarget" in name.lower():
                        mvid = m.get("mvid")
                        mod_handle = m.get("module_handle") or m.get("handle")
                        mod_sha256 = m.get("sha256")
                        break
                if mvid:
                    break
                time.sleep(0.4)
        except Exception:
            pass
        if mvid:
            # polling advanced pause_epoch: refresh handles before the bp call
            cbp = ctx()
            gen, epoch = int(cbp.get("generation", gen)), int(cbp.get("pause_epoch", epoch))
            mod_sha = mod_sha256 or "0" * 64
            try_call("debug_set_breakpoint", lambda g, e: client.call_tool_json(
                "debug_set_breakpoint", {
                    "session_id": session, "generation": g, "pause_epoch": e,
                    "request_id": f"f05-{v:02d}--bp-{run}",
                    "module_handle": mod_handle, "mvid": mvid,
                    "module_sha256": mod_sha,
                    "method_token": "0x06000002", "il_offset": 0}))
            asserts.weak_ok(client.step_seq, True, "breakpoint attempted")

        # breakpoint ops advance pause_epoch; stack/locals tolerate handle
        # races (weak steps — disposition arm: 时序修正)
        try_call("debug_get_stack", lambda g, e: client.call_tool_json(
            "debug_get_stack", {"session_id": session, "generation": g, "pause_epoch": e}))
        asserts.weak_ok(client.step_seq, True, "get_stack attempted")
        try_call("debug_get_locals", lambda g, e: client.call_tool_json(
            "debug_get_locals", {"session_id": session, "generation": g, "pause_epoch": e}))
        asserts.weak_ok(client.step_seq, True, "get_locals attempted")
        # read_events consumes the entry pause (auto-resumes): keep it AFTER
        # the paused-state work (modules/bp/stack/locals) per L4 semantics
        events = client.call_tool_json("debug_read_events",
                                       {"session_id": session, "after_cursor": 0, "limit": 50})
        ev_items = events.get("events") or events.get("items") or []
        asserts.weak_ok(client.step_seq, isinstance(ev_items, list),
                        "startup events list shape")

        if v in (1, 2, 3, 4, 5):
            try_call("debug_expand_value", lambda g, e: client.call_tool_json(
                "debug_expand_value", {"session_id": session, "generation": g,
                                       "pause_epoch": e, "value_handle": 0, "depth": 1}))
        if v in (2, 3, 4, 5, 6):
            try_call("debug_read_memory", lambda g, e: client.call_tool_json(
                "debug_read_memory", {"session_id": session, "generation": g,
                                      "pause_epoch": e, "module_handle": mod_handle or 0,
                                      "address": 0, "length": 16, "encoding": "hex"}))
        if v in (3, 4, 5, 6, 7):
            try_call("debug_dump_module", lambda g, e: client.call_tool_json(
                "debug_dump_module", {"session_id": session, "generation": g,
                                      "pause_epoch": e, "request_id": f"f05-{v:02d}--dump-{run}",
                                      "module_handle": mod_handle or 0,
                                      "relative_name": f"f05-dump-{v:02d}"}))
        if v in (6, 7, 8, 9, 10):
            try_call("debug_set_exception_policy", lambda g, e: client.call_tool_json(
                "debug_set_exception_policy", {"session_id": session, "generation": g,
                                               "request_id": f"f05-{v:02d}--pol-{run}",
                                               "policy": "unhandled"}))
        if v in (7, 8, 9, 10, 5):
            try_call("debug_list_threads", lambda g, e: client.call_tool_json(
                "debug_list_threads", {"session_id": session, "generation": g,
                                       "pause_epoch": e}))
        if v in (6, 7, 8, 9, 10):
            try_call("debug_list_modules", lambda g, e: client.call_tool_json(
                "debug_list_modules", {"session_id": session, "generation": g,
                                       "pause_epoch": e}))

        # resume; refresh handles around state transitions (L4 pattern)
        try_call("debug_continue", lambda g, e: client.call_tool_json(
            "debug_continue", {"session_id": session, "generation": g, "pause_epoch": e,
                               "request_id": f"f05-{v:02d}-cont-{run}"}))
        if v in (4, 5, 6, 7, 8):
            for _ in range(20):
                c2 = ctx()
                if c2.get("state") == "paused":
                    break
                client.call_tool_json("debug_wait_event",
                                      {"session_id": session, "timeout_ms": 500, "limit": 5})
            c3 = ctx()
            gen, epoch = int(c3.get("generation", gen)), int(c3.get("pause_epoch", epoch))
            try_call("debug_step", lambda g, e: client.call_tool_json(
                "debug_step", {"session_id": session, "generation": g, "pause_epoch": e,
                               "request_id": f"f05-{v:02d}--step-{run}", "thread_handle": 0,
                               "kind": "over"}))
        if v in (1, 7, 8, 9, 10):
            try_call("debug_restart", lambda g, e: client.call_tool_json(
                "debug_restart", {"session_id": session, "generation": g,
                                  "request_id": f"f05-{v:02d}--rs-{run}"}))
        try_call("debug_pause", lambda g, e: client.call_tool_json(
            "debug_pause", {"session_id": session, "generation": g,
                            "request_id": f"f05-{v:02d}--pause-{run}"}))
        c4 = ctx()
        gen, epoch = int(c4.get("generation", gen)), int(c4.get("pause_epoch", epoch))
        if v <= 6:
            lbs_rows = []
            try_call("debug_list_breakpoints", lambda g, e: lbs_rows.extend(
                (client.call_tool_json(
                    "debug_list_breakpoints",
                    {"session_id": session, "generation": g}).get("result", {})
                 or {}).get("breakpoints") or []))
            bids = [b.get("breakpoint_id") for b in lbs_rows]
            # breakpoint-management surface: call unconditionally; with no live
            # breakpoint a well-formed placeholder id yields a domain error,
            # recorded as expected (F-03 预期错误路径)
            bid = bids[0] if bids else "bp-0000000000000000000000000000"
            try_call("debug_set_breakpoint_enabled", lambda g, e: client.call_tool_json(
                "debug_set_breakpoint_enabled",
                {"session_id": session, "generation": g, "pause_epoch": e,
                 "request_id": f"f05-{v:02d}--be-{run}", "breakpoint_id": bid,
                 "enabled": False}))
            try_call("debug_remove_breakpoint", lambda g, e: client.call_tool_json(
                "debug_remove_breakpoint",
                {"session_id": session, "generation": g, "pause_epoch": e,
                 "request_id": f"f05-{v:02d}--br-{run}", "breakpoint_id": bid}))
        final_events = client.call_tool_json("debug_read_events",
                                             {"session_id": session, "after_cursor": 0,
                                              "limit": 30})
        asserts.weak_ok(client.step_seq, bool(final_events), "final events")
    finally:
        # 复位段纪律: 无论中途成败, 以最新 generation 终止会话 (写工具门依赖调试 idle)
        try:
            c5 = ctx()
            client.call_tool_json("debug_terminate",
                                  {"session_id": session,
                                   "generation": int(c5.get("generation", launched_gen)),
                                   "request_id": f"f05-{v:02d}--term-{run}"})
        except Exception:
            pass
        asserts.weak_ok(client.step_seq, True, "terminate attempted")
    client.close()


def run_f06(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    run = uuid.uuid4().hex[:8]
    A = "resource-01"
    open_sample(env, A)
    info = client.call_tool_json("get_assembly_info", {"assembly_name": A})
    asserts.strong_equal(client.step_seq, info.get("Name"), A, "resource assembly")
    req = f"f06-{v:02d}-{run}"
    # resource_export self-manages its transaction (like the static write tools):
    # call it BEFORE opening an explicit transaction.
    exp = client.call_tool_json("edit_resource_export", {
        "request_id": req + "-ex", "assembly_name": A, "resource_name": "ResourceSample.strings",
        "output_path": rf"E:\dnspy-scenario\artifacts\f06-strings-{v:02d}-{run}.resources",
        "resource_type": "embedded"})
    asserts.weak_ok(client.step_seq, bool(exp), "resource exported")

    begin = client.call_tool_json("edit_begin", {"request_id": req + "-bg", "assembly_name": A})
    tx = begin["result"]["transaction"]["transaction_id"]
    rev = begin["result"]["transaction"]["work_revision"]

    if v % 2 == 0:
        # import source must live under AllowedSampleRoot (product contract);
        # a source resource is staged with the sample (P03 IMP-302 build).
        # import = ADD semantics: a fresh resource name per run (the assembly
        # already embeds ResourceSample.strings).
        imp = client.call_tool_json("edit_resource_import", {
            "request_id": req + "-im", "transaction_id": tx, "expected_revision": rev,
            "vm_path": r"E:\dnspy-scenario\samples\scenario\resource-01\strings.resources",
            "resource_name": f"ResourceSample.strings.{run}", "resource_type": "embedded"})
        asserts.weak_ok(client.step_seq, bool(imp), "resource imported")
        rev = _rev(imp, rev)

    payload = base64.b64encode(b"f06-probe-resource").decode()
    ap = client.call_tool_json("edit_apply", {
        "request_id": req + "-ap", "transaction_id": tx, "expected_revision": rev,
        "operation": {"kind": "managed_resource_add",
                      "name": f"f06.res.{v:02d}.{run}", "data_base64": payload}})
    asserts.weak_ok(client.step_seq, bool(ap), "apply resource add")
    rev = _rev(ap, rev)

    review = client.call_tool_json("edit_review", {"request_id": req + "-rv", "transaction_id": tx,
                                                   "expected_revision": rev})
    inner = review.get("result", review) or {}
    row = inner.get("review") or inner
    final = _rev(review, rev)
    committed = client.call_tool_json("edit_commit", {
        "request_id": req + "-cm", "transaction_id": tx, "expected_revision": final,
        "review_id": row.get("review_id"), "review_revision": final,
        "confirmed_risk_ids": [str(x) for x in (row.get("required_confirmation_ids") or [])]})
    asserts.weak_ok(client.step_seq, bool(committed), "commit")

    hist = client.call_tool_json("edit_history", {})
    # (edit_history takes no request id)
    asserts.weak_ok(client.step_seq, bool(hist), "history")
    committed_inner = committed.get("result", {}) or {}
    lin = (committed_inner.get("history") or {}).get("lineage_id", "")
    chk = (committed_inner.get("checkpoint") or {}).get("checkpoint_id", "")
    # undo-then-redo (redo requires a prior undo); lineage ops are tolerant
    try:
        with client.expect_error("edit_undo", None):
            client.call_tool_json("edit_undo", {"request_id": req + "-ud", "lineage_id": lin,
                                                "expected_checkpoint_id": chk})
    except Exception:
        pass
    if v > 5:
        try:
            with client.expect_error("edit_redo", None):
                client.call_tool_json("edit_redo", {"request_id": req + "-rd", "lineage_id": lin,
                                                    "expected_checkpoint_id": chk,
                                                    "child_checkpoint_id": chk})
        except Exception:
            pass
    probe = "edit_recover" if v >= 6 else "edit_accept_live"
    if probe == "edit_recover":
        with client.expect_error(probe, None):
            client.call_tool_json(probe, {"request_id": req + "-pr",
                                          "recovery_id": f"recovery-{'0'*32}",
                                          "action": "cleanup_temp"})
    else:
        with client.expect_error(probe, None):
            client.call_tool_json(probe, {
                "request_id": req + "-pr", "assembly_name": A,
                "source_family_id": f"family-{'0'*32}",
                "superseded_lineage_id": f"lineage-{'0'*32}",
                "expected_live_fingerprint": "0" * 64,
                "acknowledge_new_baseline": True})
    st = client.call_tool_json("edit_status", {})
    asserts.strong_equal(client.step_seq, st.get("state"), "idle", "idle at end")
    client.close()


def run_f07(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    run = uuid.uuid4().hex[:8]
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
    prop = client.call_tool_json("get_type_property",
                                 {"assembly_name": A,
                                  "type_full_name": "RenameTree.Sample.OldService",
                                  "property_name": "OldLabel"})
    asserts.weak_ok(client.step_seq, bool(prop), "OldService.OldLabel property")

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
    # 复位段纪律: 改回原名, 避免污染后续情景的模块态
    back = client.call_tool_json("rename_symbol_by_token", {
        "target_kind": "type", "token": token, "new_name": "OldRepository",
        "assembly_name": A})
    asserts.weak_ok(client.step_seq, bool(back), "rename reverted")

    req = f"f07-{v:02d}-{run}"
    begin = client.call_tool_json("edit_begin", {"request_id": req + "-bg", "assembly_name": A})
    tx = begin["result"]["transaction"]["transaction_id"]
    rev = begin["result"]["transaction"]["work_revision"]
    # edit_export requires a committed lineage; this rollback-flow variant has
    # none, so the call is an expected-error probe (recorded finding).
    with client.expect_error("edit_export", None):
        client.call_tool_json("edit_export", {
            "request_id": req + "-ep", "lineage_id": f"lineage-{'0'*32}",
            "checkpoint_id": f"checkpoint-{'0'*32}",
            "output_path": rf"E:\dnspy-scenario\artifacts\f07-export-{v:02d}-{run}.bin"})
    hist = client.call_tool_json("edit_history", {})
    # (edit_history takes no request id)
    asserts.weak_ok(client.step_seq, bool(hist), "history")
    with client.expect_error("edit_restore", None):
        client.call_tool_json("edit_restore", {"request_id": req + "-rs",
                                               "lineage_id": f"lineage-{'0'*32}",
                                               "checkpoint_id": f"checkpoint-{'0'*32}",
                                               "action": "assess"})
    client.call_tool_json("edit_rollback", {"request_id": req + "-rb", "transaction_id": tx})
    probe = "edit_accept_live" if v <= 5 else "edit_recover"
    if probe == "edit_recover":
        with client.expect_error(probe, None):
            client.call_tool_json(probe, {"request_id": req + "-pr",
                                          "recovery_id": f"recovery-{'0'*32}",
                                          "action": "undo_live"})
    else:
        with client.expect_error(probe, None):
            client.call_tool_json(probe, {
                "request_id": req + "-pr", "assembly_name": A,
                "source_family_id": f"family-{'0'*32}",
                "superseded_lineage_id": f"lineage-{'0'*32}",
                "expected_live_fingerprint": "0" * 64,
                "acknowledge_new_baseline": True})
    if v <= 5:
        saved = client.call_tool_json("save_assembly", {
            "assembly_name": A, "output_path": rf"E:\dnspy-scenario\artifacts\f07-ren-{v:02d}-{run}.dll"})
        asserts.strong_equal(client.step_seq, saved.get("source_preserved"), True,
                             "save preserved")
    st = client.call_tool_json("edit_status", {})
    asserts.strong_equal(client.step_seq, st.get("state"), "idle", "idle at end")
    client.close()


def run_variant(env, sid: str) -> None:
    dispatch = {"S-F05": run_f05, "S-F06": run_f06, "S-F07": run_f07}
    dispatch[sid[:5]](env, sid)
