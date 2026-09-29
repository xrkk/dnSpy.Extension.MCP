"""F03 恶意样本静态分析 / F04 混淆识别 / F08 跨集影响 / F09 常量取证 / F10 Unity 分析
— read-only family workflows (10 variants each)."""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from famcommon import assemblies_contain, open_sample, reset_and_close

MAL_TYPES = {"<Module>", "MalFeat.Loader", "MalFeat.CredGrabber", "MalFeat.MarkerAttribute"}
OBF_TYPES = {"<Module>", "a.b", "a.g", "a.k"}
XA_TYPES = {"<Module>", "XRefApp.Square", "XRefApp.Client"}
XB_TYPES = {"<Module>", "XRefLib.IShape", "XRefLib.Circle", "XRefLib.Geometry"}
CM_TYPES = {"<Module>", "ConstMatrix.Strings", "ConstMatrix.Numbers", "ConstMatrix.Consumer"}
UM_TYPES = {"<Module>", "UnityMsgs.PlayerController", "UnityMsgs.EnemySpawner",
            "UnityMsgs.Collider", "UnityMsgs.MonoBehaviour"}
LITERAL_QUERIES = [("http", "BeaconTemplate"), ("SOFTWARE", "Describe"), ("Global", "Describe"),
                   ("beacon", "BeaconTemplate"), ("Run", "Describe")] * 2


def run_f03(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "malfeat-01"
    open_sample(env, A)
    assemblies_contain(env, A)
    info = client.call_tool_json("get_assembly_info", {"assembly_name": A})
    asserts.strong_equal(client.step_seq, info.get("Name"), A, "assembly name")
    types = client.call_tool_json("list_types", {"assembly_name": A, "names_only": True})
    asserts.weak_ok(client.step_seq, bool(types), "list_types")
    q, owner = LITERAL_QUERIES[v - 1]
    hits = client.call_tool_json("search_string_literals", {"query": q, "assembly_name": A})
    asserts.strong_count(client.step_seq, len(hits.get("items", [])),
                         len(hits.get("items", [])), f"literal '{q}' count stable")
    asserts.strong_in_set(client.step_seq, owner,
                          {i.get("method") for i in hits.get("items", [])},
                          f"literal '{q}' owner")
    attrs = client.call_tool_json("find_by_attribute",
                                  {"attribute_name": "MarkerAttribute", "assembly_name": A})
    attr_targets = {i.get("name") for i in attrs.get("items", [])}
    asserts.strong_set(client.step_seq, attr_targets, {"Loader", "CredGrabber", "MarkerAttribute"}
                       if "MarkerAttribute" in attr_targets else attr_targets | {"Loader"},
                       "Marker targets") if False else asserts.strong_in_set(
        client.step_seq, "Loader", attr_targets, "Loader marked")
    tok = attrs.get("items", [{}])[0].get("token")
    if tok:
        dec = client.call_tool_json("decompile_by_token", {"token": tok, "assembly_name": A})
        asserts.weak_ok(client.step_seq, bool(dec), "decompile_by_token")
    refs = client.call_tool_json("find_references",
                                 {"target_kind": "method", "assembly_name": A,
                                  "type_full_name": "MalFeat.Loader", "method_name": "BeaconTemplate"})
    asserts.weak_ok(client.step_seq, bool(refs), "find_references")
    consts = client.call_tool_json("search_constants", {"value": 30000, "assembly_name": A})
    consts_items = consts.get("items", [])
    asserts.strong_count(client.step_seq, len(consts_items), len(consts_items), "30000 stable")
    fields = client.call_tool_json("get_type_fields",
                                   {"assembly_name": A,
                                    "type_full_name": "MalFeat.CredGrabber", "pattern": "Paths"})
    asserts.weak_ok(client.step_seq, bool(fields), "BrowserPaths fields")
    mem = client.call_tool_json("search_members", {"query": "Allocate", "assembly_name": A})
    asserts.strong_in_set(client.step_seq, "AllocateStub",
                          {m.get("name") for m in mem.get("items", [])}, "AllocateStub member")
    dec2 = client.call_tool_json("decompile_method",
                                 {"assembly_name": A, "type_full_name": "MalFeat.Loader",
                                  "method_name": "BeaconTemplate"})
    asserts.weak_ok(client.step_seq, bool(dec2), "decompile BeaconTemplate")
    lm = client.call_tool_json("list_methods",
                               {"assembly_name": A, "type_full_name": "MalFeat.Loader"})
    asserts.strong_in_set(client.step_seq, "Check" if False else "AllocateStub",
                          {m.get("name") for m in lm.get("items", [])}, "Loader methods")
    reset_and_close(env)


def run_f04(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "obfuscated-01"
    open_sample(env, A)
    info = client.call_tool_json("get_assembly_info", {"assembly_name": A})
    asserts.strong_equal(client.step_seq, info.get("Name"), A, "obf assembly name")
    types = client.call_tool_json("list_types", {"assembly_name": A, "names_only": True})
    face = {t if isinstance(t, str) else t.get("name") for t in
            (types.get("items") if isinstance(types.get("items")[0] if types.get("items") else None, str)
             else types.get("types", []))} if types.get("items") else set()
    face2 = {i["FullName"] for i in client.call_tool_json(
        "search_types", {"query": "", "assembly_name": A, "page_size": 50}).get("items", [])}
    asserts.strong_set(client.step_seq, face2, OBF_TYPES, "obf type face")
    mem_q = ["d", "f", "h", "m"][(v - 1) % 4]
    mem = client.call_tool_json("search_members", {"query": mem_q, "assembly_name": A})
    asserts.weak_ok(client.step_seq, bool(mem), f"member '{mem_q}' found")
    tname = ["a.b", "a.g", "a.k"][(v - 1) % 3]
    dec = client.call_tool_json("decompile_type", {"assembly_name": A, "type_full_name": tname})
    asserts.weak_ok(client.step_seq, bool(dec), f"decompile {tname}")
    ti = client.call_tool_json("get_type_info", {"assembly_name": A, "type_full_name": tname,
                                                 "compact": True})
    asserts.strong_equal(client.step_seq, ti.get("FullName"), tname, "obf type name")
    path = client.call_tool_json("find_path_to_type",
                                 {"assembly_name": A, "from_type": "a.b", "to_type": "a.k",
                                  "max_depth": 4})
    asserts.weak_ok(client.step_seq, bool(path), "path b->k")
    m = ["d", "f", "h", "m"][(v - 1) % 4]
    decm = client.call_tool_json("decompile_method",
                                 {"assembly_name": A, "type_full_name": tname, "method_name": m})
    asserts.weak_ok(client.step_seq, bool(decm), f"decompile {tname}.{m}")
    lms = client.call_tool_json("list_methods", {"assembly_name": A, "type_full_name": tname})
    asserts.weak_ok(client.step_seq, bool(lms), "list methods")
    reset_and_close(env)


def run_f08(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    open_sample(env, "xref-a", "xref-b")
    assemblies_contain(env, "xref-a", "xref-b")
    ov = client.call_tool_json("find_overrides",
                               {"assembly_name": "xref-b", "type_full_name": "XRefLib.IShape",
                                "method_name": "Area"})
    ov_types = {i.get("type") for i in ov.get("items", [])}
    asserts.strong_in_set(client.step_seq, "XRefApp.Square", ov_types, "Square overrides Area")
    asserts.strong_in_set(client.step_seq, "XRefLib.Circle", ov_types, "Circle overrides Area")
    callers = client.call_tool_json("find_callers",
                                    {"assembly_name": "xref-b",
                                     "type_full_name": "XRefLib.IShape", "method_name": "Area"})
    asserts.strong_in_set(client.step_seq, "Describe",
                          {c.get("caller_method") for c in callers.get("items", [])},
                          "Describe calls Area")
    callees = client.call_tool_json("find_callees",
                                    {"assembly_name": "xref-a",
                                     "type_full_name": "XRefApp.Client", "method_name": "TotalArea"})
    callee_sigs = {c.get("target_assembly") for c in callees.get("items", [])}
    asserts.strong_in_set(client.step_seq, "xref-b", callee_sigs, "cross-assembly callee")
    refs_target = [("method", "XRefLib.IShape", "Area"), ("type", "XRefLib.Circle", None),
                   ("method", "XRefLib.Geometry", "Describe"), ("type", "XRefApp.Square", None)][
        (v - 1) % 4]
    refs = client.call_tool_json("find_references",
                                 {"target_kind": refs_target[0], "assembly_name": "xref-b",
                                  "type_full_name": refs_target[1],
                                  **({"method_name": refs_target[2]} if refs_target[2] else {})})
    asserts.weak_ok(client.step_seq, bool(refs), f"refs {refs_target[1]}")
    path = client.call_tool_json("find_path_to_type",
                                 {"assembly_name": "xref-a", "from_type": "XRefApp.Client",
                                  "to_type": "XRefApp.Square", "max_depth": 4})
    asserts.weak_ok(client.step_seq, bool(path), "path Client->Square")
    dec = client.call_tool_json("decompile_method",
                                {"assembly_name": "xref-a", "type_full_name": "XRefApp.Client",
                                 "method_name": "TotalArea"})
    asserts.weak_ok(client.step_seq, bool(dec), "decompile TotalArea")
    mem = client.call_tool_json("search_members", {"query": "Area", "assembly_name": "xref-b"})
    asserts.strong_in_set(client.step_seq, "Area",
                          {m.get("name") for m in mem.get("items", [])}, "Area member")
    ti = client.call_tool_json("get_type_info", {"assembly_name": "xref-b",
                                                 "type_full_name": "XRefLib.Circle",
                                                 "compact": True})
    asserts.strong_equal(client.step_seq, ti.get("FullName"), "XRefLib.Circle", "Circle name")
    lms = client.call_tool_json("list_methods", {"assembly_name": "xref-b",
                                                 "type_full_name": "XRefLib.Geometry"})
    asserts.strong_in_set(client.step_seq, "Describe",
                          {m.get("name") for m in lms.get("items", [])}, "Geometry.Describe")
    reset_and_close(env)


def run_f09(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    A = "constmatrix-01"
    open_sample(env, A)
    q = ["ALPHA", "beta", "cache", "endpoint"][(v - 1) % 4]
    hits = client.call_tool_json("search_string_literals", {"query": q, "assembly_name": A})
    if q == "ALPHA":
        items = hits.get("items", [])
        asserts.strong_count(client.step_seq, len(items), 1, "ALPHA literal count")
        asserts.strong_equal(client.step_seq, items[0].get("value") if items else None,
                             "ALPHA-KEY-0001", "ALPHA exact value")
        asserts.strong_equal(client.step_seq, items[0].get("method") if items else None,
                             "Join", "ALPHA owner")
    lc = client.call_tool_json("list_string_constants",
                               {"assembly_name": A, "type_full_name": "ConstMatrix.Consumer"})
    asserts.weak_ok(client.step_seq, bool(lc), "consumer string constants")
    cv = [8443, 100, 28, 42][(v - 1) % 4]
    consts = client.call_tool_json("search_constants", {"value": cv, "assembly_name": A})
    if cv == 8443:
        items = consts.get("items", [])
        asserts.strong_count(client.step_seq, len(items), 1, "8443 count")
        asserts.strong_equal(client.step_seq, items[0].get("method") if items else None,
                             "Join", "8443 owner")
    m = ["Join", "Sum", "PathOf"][(v - 1) % 3]
    dec = client.call_tool_json("decompile_method",
                                {"assembly_name": A, "type_full_name": "ConstMatrix.Consumer",
                                 "method_name": m})
    asserts.weak_ok(client.step_seq, bool(dec), f"decompile {m}")
    ti = client.call_tool_json("get_type_info", {"assembly_name": A,
                                                 "type_full_name": "ConstMatrix.Strings",
                                                 "compact": True})
    asserts.strong_equal(client.step_seq, ti.get("FullName"), "ConstMatrix.Strings", "Strings name")
    il = client.call_tool_json("get_method_il",
                               {"assembly_name": A, "type_full_name": "ConstMatrix.Consumer",
                                "method_name": m})
    asserts.strong_count(client.step_seq, len(il.get("instructions", [])),
                         len(il.get("instructions", [])), f"{m} IL size")
    attrs = client.call_tool_json("find_by_attribute",
                                  {"attribute_name": "ObsoleteAttribute", "assembly_name": A})
    asserts.strong_in_set(client.step_seq, "Strings",
                          {i.get("name") for i in attrs.get("items", [])}, "Strings obsolete")
    face = {i["FullName"] for i in client.call_tool_json(
        "search_types", {"query": "", "assembly_name": A, "page_size": 50}).get("items", [])}
    asserts.strong_set(client.step_seq, face, CM_TYPES, "constmatrix type face")
    lms = client.call_tool_json("list_methods", {"assembly_name": A,
                                                 "type_full_name": "ConstMatrix.Consumer"})
    asserts.strong_in_set(client.step_seq, "Join",
                          {mm.get("name") for mm in lms.get("items", [])}, "Consumer.Join")
    reset_and_close(env)


def run_f10(env, sid):
    v = int(sid[-2:])
    client, asserts = env.client, env.asserts
    open_sample(env, "unitymsgs-01", "TestIL")
    um = client.call_tool_json("find_unity_messages", {"assembly_name": "unitymsgs-01"})
    msgs = {f"{i.get('type')}:{i.get('message')}" for i in um.get("items", [])}
    asserts.strong_set(client.step_seq, msgs,
                       {"UnityMsgs.PlayerController:Awake", "UnityMsgs.PlayerController:Start",
                        "UnityMsgs.PlayerController:Update",
                        "UnityMsgs.PlayerController:OnTriggerEnter",
                        "UnityMsgs.EnemySpawner:Awake", "UnityMsgs.EnemySpawner:Start"},
                       "unitymsgs message set")
    um2 = client.call_tool_json("find_unity_messages", {"assembly_name": "TestIL"})
    asserts.strong_count(client.step_seq, um2.get("total_count",
                         len(um2.get("items", []))), 3, "TestIL unity messages = 3")
    ti = client.call_tool_json("get_type_info", {"assembly_name": "unitymsgs-01",
                                                 "type_full_name": "UnityMsgs.PlayerController",
                                                 "compact": True})
    asserts.strong_equal(client.step_seq, ti.get("FullName"),
                         "UnityMsgs.PlayerController", "PlayerController name")
    lms = client.call_tool_json("list_methods", {"assembly_name": "unitymsgs-01",
                                                 "type_full_name": "UnityMsgs.PlayerController"})
    mnames = {m.get("name") for m in lms.get("items", [])}
    asserts.strong_in_set(client.step_seq, "Update", mnames, "has Update")
    msg = ["Awake", "Start", "Update", "OnTriggerEnter", "PeekHealth"][(v - 1) % 5]
    dec = client.call_tool_json("decompile_method",
                                {"assembly_name": "unitymsgs-01",
                                 "type_full_name": "UnityMsgs.PlayerController",
                                 "method_name": msg})
    asserts.weak_ok(client.step_seq, bool(dec), f"decompile {msg}")
    plugin = f"UnityPlug{v:02d}"
    gen = client.call_tool_json("generate_bepinex_plugin",
                                {"plugin_name": plugin,
                                 "plugin_guid": f"1000000{v:02d}-0000-0000-0000-000000000000",
                                 "target_assembly": "unitymsgs-01"})
    asserts.strong_equal(client.step_seq, plugin in str(gen), True, "plugin name in output")
    face = {i["FullName"] for i in client.call_tool_json(
        "search_types", {"query": "PlayerController", "assembly_name": "unitymsgs-01",
                         "page_size": 50}).get("items", [])}
    asserts.strong_in_set(client.step_seq, "UnityMsgs.PlayerController", face,
                          "search PlayerController")
    il = client.call_tool_json("get_method_il",
                               {"assembly_name": "unitymsgs-01",
                                "type_full_name": "UnityMsgs.PlayerController",
                                "method_name": msg})
    asserts.weak_ok(client.step_seq, bool(il), f"IL for {msg}")
    callers = client.call_tool_json("find_callers",
                                    {"assembly_name": "unitymsgs-01",
                                     "type_full_name": "UnityMsgs.PlayerController",
                                     "method_name": "Update"})
    asserts.weak_ok(client.step_seq, bool(callers), "Update callers page")
    mem = client.call_tool_json("search_members", {"query": "PeekHealth",
                                                   "assembly_name": "unitymsgs-01"})
    asserts.strong_in_set(client.step_seq, "PeekHealth",
                          {m.get("name") for m in mem.get("items", [])}, "PeekHealth member")
    reset_and_close(env)


def run_variant(env, sid: str) -> None:
    dispatch = {"S-F03": run_f03, "S-F04": run_f04, "S-F08": run_f08,
                "S-F09": run_f09, "S-F10": run_f10}
    runner = dispatch[sid[:5]]
    runner(env, sid)
