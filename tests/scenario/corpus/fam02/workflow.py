"""F02 BepInEx/Harmony 模组开发 — family workflow (10 variants)."""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from famcommon import assemblies_contain, open_sample, reset_and_close

HOOK_TYPES = {"<Module>", "HookTarget.Combatant", "HookTarget.Tank", "HookTarget.Scout",
              "HookTarget.Battle"}
DOC_METHOD = {1: "Damage", 2: "Damage", 3: "Attack", 4: "Damage", 5: "Attack",
              6: "Damage", 7: "Damage", 8: "Attack", 9: "Damage", 10: "Attack"}
BODY = {"Damage": "return 99;", "Attack": "return Damage() + 1;"}


def run_variant(env, sid: str) -> None:
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "hooktarget-01"
    open_sample(env, A)
    assemblies_contain(env, A)

    info = client.call_tool_json("get_type_info", {"assembly_name": A,
                                                   "type_full_name": "HookTarget.Combatant",
                                                   "compact": True})
    asserts.strong_equal(client.step_seq, info.get("FullName"), "HookTarget.Combatant",
                         "Combatant full name")

    methods = client.call_tool_json("list_methods", {"assembly_name": A,
                                                     "type_full_name": "HookTarget.Combatant"})
    names = {m.get("name") for m in methods.get("items", [])}
    asserts.strong_in_set(client.step_seq, "Damage", names, "Combatant has Damage")
    token_by_name = {m.get("name"): m.get("token") for m in methods.get("items", [])}

    um = client.call_tool_json("find_unity_messages", {"assembly_name": A})
    asserts.strong_count(client.step_seq, um.get("total_count", len(um.get("items", []))), 0,
                         "hooktarget unity messages = 0")

    hook_type = ["HookTarget.Tank", "HookTarget.Scout", "HookTarget.Combatant"][(v - 1) % 3]
    hook_method = "Damage" if hook_type != "HookTarget.Combatant" else "Attack"
    if v >= 6:
        gen = client.call_tool_json("generate_harmony_patch",
                                    {"assembly_name": A, "type_full_name": hook_type,
                                     "method_name": hook_method, "patch_type": "postfix"})
        asserts.strong_equal(client.step_seq, "Harmony postfix patch" in str(gen), True,
                             "harmony output marker")
    else:
        plugin = f"BepPlug{v:02d}"
        gen = client.call_tool_json("generate_bepinex_plugin",
                                    {"plugin_name": plugin,
                                     "plugin_guid": f"0000000{v:02d}-0000-0000-0000-000000000000",
                                     "target_assembly": A})
        asserts.weak_ok(client.step_seq, plugin in str(gen), "bepinex plugin name in output")

    dec = client.call_tool_json("decompile_method", {"assembly_name": A,
                                                     "type_full_name": hook_type,
                                                     "method_name": hook_method})
    asserts.weak_ok(client.step_seq, bool(dec), "decompile hook method")

    # edit transaction chain: begin → compile → import → apply → impact → review → commit|rollback
    import uuid
    req = f"f02-{v:02d}-{uuid.uuid4().hex[:8]}"
    begin = client.call_tool_json("edit_begin", {"request_id": req, "assembly_name": A})
    tx = begin["result"]["transaction"]["transaction_id"]
    rev = begin["result"]["transaction"]["work_revision"]

    m = DOC_METHOD[v]
    comp = client.call_tool_json("edit_compile", {
        "request_id": req, "assembly_name": A, "compilation_kind": "edit_method",
        "documents": [{"path": f"HookTarget/Combatant-v{v:02d}.cs",
                       "content": f"namespace HookTarget{{public partial class Combatant"
                                  f"{{public int {m}(){{{BODY[m]}}}}}}}"}]})
    inner = comp.get("result", {}).get("compile", {})
    asserts.strong_equal(client.step_seq, inner.get("success"), True, "edit_compile success")
    cid = inner.get("compile_id")

    def cur_rev(resp):
        inner = resp.get("result", resp)
        return (inner.get("transaction") or inner).get("work_revision", rev)

    imp = client.call_tool_json("edit_import", {
        "request_id": req, "transaction_id": tx, "expected_revision": rev,
        "compile_id": cid,
        "targets": [{"compiled": f"HookTarget.Combatant::{m}()", "action": "replace_body"}]})
    asserts.weak_ok(client.step_seq, bool(imp), "edit_import applied")
    rev = cur_rev(imp)

    applied = client.call_tool_json("edit_apply", {
        "request_id": req, "transaction_id": tx, "expected_revision": rev,
        "operation": {"kind": "type_add", "name": f"Hooked{v:02d}{req[-8:]}"}})
    asserts.weak_ok(client.step_seq, bool(applied), "edit_apply type_add")
    rev = cur_rev(applied)

    scan = client.call_tool_json("edit_impact_scan", {"request_id": req, "transaction_id": tx,
                                                      "expected_revision": rev})
    asserts.weak_ok(client.step_seq, bool(scan), "impact scan")
    rev = cur_rev(scan)

    if v >= 6:
        review = client.call_tool_json("edit_review", {"request_id": req, "transaction_id": tx,
                                                       "expected_revision": rev})
        r_inner = review.get("result", review) or {}
        review_row = (r_inner.get("review") or r_inner)
        rid = str(review_row.get("review_id", ""))
        required = [str(x) for x in (review_row.get("required_confirmation_ids") or [])]
        final_rev = cur_rev(review)
        committed = client.call_tool_json("edit_commit", {
            "request_id": req, "transaction_id": tx, "expected_revision": final_rev,
            "review_id": rid, "review_revision": final_rev, "confirmed_risk_ids": required})
        asserts.weak_ok(client.step_seq, bool(committed), "edit_commit")
    else:
        rolled = client.call_tool_json("edit_rollback", {"request_id": req, "transaction_id": tx})
        asserts.strong_equal(client.step_seq,
                             (rolled.get("result", {}) or {}).get("rolled_back"), True,
                             "edit_rollback")

    st = client.call_tool_json("edit_status", {})
    asserts.strong_equal(client.step_seq, st.get("state"), "idle", "edit idle at end")
    client.close()
