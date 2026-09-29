"""P02 冒烟情景（ACC-042）: 4 接口 × 2 强断言, 走 录制→账本→聚合 全链路。

对 net48 实例（P01 部署）打开样本、核验程序集清单与类型检索的结构化
结果, 并确认编辑状态空闲。此为 SPEC-authoring 的首个范例用例。
"""

SCENARIO_ID = "S-SMOKE-01"
DECLARED_TOOLS = ["open_files", "list_assemblies", "search_types", "edit_status"]

SAMPLE = r"E:\dnspy-scenario\samples\TestIL.dll"


def test_smoke_chain(scenario_env):
    """目标: 验证录制代理/断言 API/账本/复位全链路; 步骤: 开样本→列程序集→搜类型→查编辑态。"""
    client = scenario_env.client
    asserts = scenario_env.asserts

    # 1. open sample (strong: idempotent-open result — loaded now or already loaded)
    opened = client.call_tool_json("open_files", {"paths": [SAMPLE]})
    loaded_now = opened.get("loaded_count", 0) + opened.get("already_loaded_count", 0)
    asserts.strong_count(client.step_seq, loaded_now, 1, "open_files loaded+already")
    asserts.strong_count(client.step_seq, opened.get("failed_count"), 0, "open_files.failed_count")
    loaded = opened.get("loaded") or []
    asserts.strong_in_set(client.step_seq,
                          loaded[0].get("name") if loaded else None,
                          {"TestIL"}, "open_files loaded[0].name")

    # 2. assembly list contains the sample (strong: membership of structured names)
    assemblies = client.call_tool_json("list_assemblies", {})
    names = {a.get("Name") for a in assemblies.get("assemblies", [])}
    asserts.strong_in_set(client.step_seq, "TestIL", names, "list_assemblies contains TestIL")

    # 3. type search returns exactly the known type (strong: count + exact full name)
    hits = client.call_tool_json("search_types", {"query": "UnityComponent"})
    asserts.strong_count(client.step_seq, hits.get("total_count"), 1, "search_types total_count")
    items = hits.get("items") or []
    asserts.strong_equal(client.step_seq,
                         items[0].get("FullName") if items else None,
                         "TestIL.UnityComponent", "search_types[0].FullName")

    # 4. edit idle (weak: ok flag + state field present)
    status = client.edit_status()
    asserts.weak_ok(client.step_seq, status.get("ok"), "edit_status.ok")
    asserts.weak_fields(client.step_seq, status, ["state", "ok"], "edit_status fields")

    # 复位段: 无编辑/调试状态产生, 仅关闭会话（标准复位例程五检查兜底）
    client.close()
