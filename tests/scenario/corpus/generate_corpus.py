#!/usr/bin/env python3
"""P03 corpus generator: emits 10 family dirs × 10 variant modules + specs.

The family definitions below are the design artifact (reviewed via the P03
sub-plan §3 allocation); generated files are committed. Regeneration:
    python3 tests/scenario/corpus/generate_corpus.py

Expected values embedded in workflows are frozen from the P03 probes
(corpus/manifests/*.json, p03-evidence-archive/probe-notes-key.md).
"""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).parent
FAMS = ROOT

RUNTARGET_SHA = "8aac59523304a3ccf45c99dd6b2d3f5887593fb74eacf53966c61c819d38bced"

SAMPLES = {
    "TestIL": r"E:\dnspy-scenario\samples\TestIL.dll",
    "license-01": r"E:\dnspy-scenario\samples\scenario\license-01\license-01.dll",
    "hooktarget-01": r"E:\dnspy-scenario\samples\scenario\hooktarget-01\hooktarget-01.dll",
    "malfeat-01": r"E:\dnspy-scenario\samples\scenario\malfeat-01\malfeat-01.dll",
    "obfuscated-01": r"E:\dnspy-scenario\samples\scenario\obfuscated-01\obfuscated-01.dll",
    "renametree-01": r"E:\dnspy-scenario\samples\scenario\renametree-01\renametree-01.dll",
    "constmatrix-01": r"E:\dnspy-scenario\samples\scenario\constmatrix-01\constmatrix-01.dll",
    "unitymsgs-01": r"E:\dnspy-scenario\samples\scenario\unitymsgs-01\unitymsgs-01.dll",
    "resource-01": r"E:\dnspy-scenario\samples\scenario\resource-01\resource-01.dll",
    "xref-a": r"E:\dnspy-scenario\samples\scenario\xref-a\xref-a.dll",
    "xref-b": r"E:\dnspy-scenario\samples\scenario\xref-b\xref-b.dll",
    "runtarget-01": r"E:\dnspy-scenario\samples\scenario\runtarget-01\runtarget-01.exe",
}


def emit_variant(fam: Path, sid: str, tools: list[str], doc: str) -> None:
    d = fam / ("s_" + sid.lower().replace("-", "_"))
    d.mkdir(parents=True, exist_ok=True)
    (d / "__init__.py").write_text("", encoding="utf-8")
    (d / "test_variant.py").write_text(
        f'"""{doc}"""\n\n'
        f"SCENARIO_ID = {sid!r}\n"
        f"DECLARED_TOOLS = {json.dumps(tools)}\n\n"
        f"import sys\nfrom pathlib import Path\n"
        f"sys.path.insert(0, str(Path(__file__).resolve().parents[1]))\n\n"
        f"def test_{sid.lower().replace(chr(45), chr(95))}(scenario_env):\n"
        f'    """{doc}"""\n'
        f"    from workflow import run_variant\n"
        f"    run_variant(scenario_env, {sid!r})\n",
        encoding="utf-8")


# ---------------------------------------------------------------- family 01
F01_TOOLS = ["open_files", "list_assemblies", "search_types", "search_string_literals",
             "list_string_constants", "decompile_method", "find_callers",
             "get_method_il", "force_return", "nop_method", "patch_method_il",
             "revert_method_il", "save_assembly", "edit_status"]
F01_VARIANTS = [
    # (variant, write_tool, save_it) — targets/expecteds differ per variant below
    ("01", "force_return", False), ("02", "force_return", True),
    ("03", "nop_method", False), ("04", "nop_method", True),
    ("05", "patch_method_il", True), ("06", "patch_method_il", False),
    ("07", "force_return", True), ("08", "nop_method", False),
    ("09", "patch_method_il", True), ("10", "force_return", False),
]

# ---------------------------------------------------------------- family 05
F05_CORE = ["debug_capabilities", "open_files", "list_assemblies", "debug_launch", "debug_status",
            "debug_set_breakpoint", "debug_wait_event", "debug_get_stack",
            "debug_get_locals", "debug_pause", "debug_continue",
            "debug_read_events", "debug_terminate"]
F05_EXTRA = {
    "01": ["debug_expand_value", "debug_restart"],
    "02": ["debug_expand_value", "debug_read_memory"],
    "03": ["debug_expand_value", "debug_read_memory", "debug_dump_module"],
    "04": ["debug_expand_value", "debug_read_memory", "debug_dump_module", "debug_step"],
    "05": ["debug_expand_value", "debug_read_memory", "debug_dump_module", "debug_step", "debug_list_threads"],
    "06": ["debug_read_memory", "debug_dump_module", "debug_step", "debug_list_threads", "debug_list_modules", "debug_set_exception_policy"],
    "07": ["debug_dump_module", "debug_step", "debug_list_threads", "debug_list_modules", "debug_restart", "debug_set_exception_policy"],
    "08": ["debug_step", "debug_list_threads", "debug_list_modules", "debug_restart", "debug_set_exception_policy"],
    "09": ["debug_list_threads", "debug_list_modules", "debug_restart", "debug_set_exception_policy"],
    "10": ["debug_list_modules", "debug_restart", "debug_set_exception_policy"],
}
F05_BP_TOOLS = ["debug_list_breakpoints", "debug_set_breakpoint_enabled", "debug_remove_breakpoint"]

# ---------------------------------------------------------------- generator

def gen_f01(fam: Path) -> None:
    tools = F01_TOOLS
    spec = ["# F01 许可证/授权绕过 — 族级规格", "",
            "- 样本: license-01（LicenseSample: LicenseGate/AppMain/LicenseStrings, 4 类型）",
            "- 骨架: open → search_types(LicenseGate) → search_string_literals(INVALID) → list_string_constants(LicenseGate) → decompile_method(Check) → find_callers(Validate) → get_method_il(Check) → 变体写操作 → revert → (变体) save_assembly → edit_status → close",
            "- 强断言: 类型面集合相等(4 类型); INVALID 字面量恰 1 处(Validate); 常量含 29; IL 指令数与首指令 opcode 精确; 写后指令变化精确(ldc.i4.n/ret); 还原后 IL 与原一致; save 的 source_preserved=true + 字节数>0",
            "- 变体差异: 写工具(force_return@v1,2,7,10 / nop@v3,4,8 / patch replace@v5,6,9)、force_return 值(1/0/365)、是否 save、patch 目标方法(Check/DaysRemaining/Validate)",
            "", "| 变体 | 写工具 | 值/目标 | save |", "| --- | --- | --- | --- |"]
    for v, w, s in F01_VARIANTS:
        spec.append(f"| S-F01-{v} | {w} | 见 workflow.VARIANTS | {'是' if s else '否'} |")
    (fam / "SPEC-fam01.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for v, w, s in F01_VARIANTS:
        sid = f"S-F01-{v}"
        emit_variant(fam, sid, tools,
                     f"许可证绕过: 对 LicenseGate 实施 {w} 写操作并还原{'+保存产物' if s else ''}")


def gen_f02(fam: Path) -> None:
    tools = ["open_files", "get_type_info", "list_methods", "find_unity_messages",
             "decompile_method", "edit_begin", "edit_compile", "edit_import",
             "edit_apply", "edit_impact_scan", "edit_review", "edit_commit",
             "edit_rollback", "edit_status", "generate_harmony_patch",
             "generate_bepinex_plugin", "search_types"]
    spec = ["# F02 BepInEx/Harmony 模组开发 — 族级规格", "",
            "- 样本: hooktarget-01（Combatant/Tank/Scout/Battle, 5 类型）",
            "- 骨架: open → get_type_info(Combatant) → list_methods → find_unity_messages(0) → 生成器(变体五五分) → decompile_method(Damage) → edit_begin → edit_compile(方法体文档) → edit_import → edit_apply(type_add 最小操作) → edit_impact_scan → edit_review → v6-10 edit_commit / v1-5 edit_rollback → edit_status",
            "- 强断言: 类型面集合(5); 生成器输出含插件类名/补丁注释结构化子串; 事务 work_revision 递增; commit 后 lineage 计数; rollback 后 state=idle",
            "- 变体差异: 生成器与 hook 目标(Tank.Damage/Scout.Damage/Combatant.Attack 轮换)、compile 文档方法名、commit/rollback 分支", "",
            "| 变体 | 生成器 | hook 目标 | 收尾 |", "| --- | --- | --- | --- |"]
    for i in range(1, 11):
        gen = "generate_harmony_patch" if i >= 6 else "generate_bepinex_plugin"
        tgt = ["Tank.Damage", "Scout.Damage", "Combatant.Attack"][i % 3]
        closing = "edit_commit" if i >= 6 else "edit_rollback"
        spec.append(f"| S-F02-{i:02d} | {gen} | {tgt} | {closing} |")
    (fam / "SPEC-fam02.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F02-{i:02d}", tools,
                     f"模组开发链: { 'harmony 补丁' if i >= 6 else 'bepinex 插件' } + 编辑事务({'commit' if i >= 6 else 'rollback'})")


def gen_f03(fam: Path) -> None:
    tools = ["open_files", "get_assembly_info", "list_types", "search_string_literals",
             "find_by_attribute", "decompile_by_token", "find_references",
             "search_constants", "get_type_fields", "search_members",
             "decompile_method", "list_methods"]
    spec = ["# F03 恶意样本静态分析 — 族级规格", "",
            "- 样本: malfeat-01（Loader/CredGrabber/MarkerAttribute, 4 类型, 3 个 Marker 标注, http 特征串）",
            "- 骨架: open → get_assembly_info → list_types → 变体特征串检索(http/SOFTWARE/Global/beacon/Run) → find_by_attribute(Marker) → decompile_by_token(标注目标 token) → find_references(方法) → search_constants(30000) → get_type_fields(BrowserPaths) → search_members → decompile_method(BeaconTemplate) → list_methods",
            "- 强断言: 类型面集合; 特征串命中数与所属方法精确(BeaconTemplate); Marker 目标集合(Loader+2); 常量 30000 归属 SleepMs; 字段数=2",
            "- 变体差异: 特征查询串与期望命中、decompile 的 token 目标、references 目标方法", "",
            "| 变体 | 查询串 | 期望命中(方法) |", "| --- | --- | --- |"]
    queries = [("http", "BeaconTemplate"), ("SOFTWARE", "Describe"), ("Global", "Describe"),
               ("beacon", "BeaconTemplate"), ("Run", "Describe")] * 2
    for i in range(1, 11):
        q, m = queries[i - 1]
        spec.append(f"| S-F03-{i:02d} | {q} | {m} |")
    (fam / "SPEC-fam03.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F03-{i:02d}", tools, f"恶意静态分析: 特征串与标注面取证(变体 {i:02d})")


def gen_f04(fam: Path) -> None:
    tools = ["open_files", "get_assembly_info", "list_types", "search_members",
             "decompile_type", "get_type_info", "find_path_to_type",
             "decompile_method", "list_methods", "search_types"]
    spec = ["# F04 混淆与加壳识别 — 族级规格", "",
            "- 样本: obfuscated-01（命名空间 a, 类型 b/g/k, 单字符成员）",
            "- 骨架: open → get_assembly_info → list_types(names_only) → search_members(变体成员名) → decompile_type(b/g/k 轮换) → get_type_info → find_path_to_type(变体对) → decompile_method(d/f/h/m 轮换) → list_methods → search_types",
            "- 强断言: 混淆名类型集合精确(a.b/a.g/a.k+<>c); 成员命中集合; 方法 IL 存在; path 结果文本可判",
            "- 变体差异: 成员查询名、decompile 目标类型/方法、path 端点对", ""]
    for i in range(1, 11):
        spec.append(f"| S-F04-{i:02d} | 目标 {['a.b','a.g','a.k'][i%3]} / 方法 {['d','f','h','m'][i%4]} |")
    (fam / "SPEC-fam04.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F04-{i:02d}", tools, f"混淆识别: 单字符符号面遍历(变体 {i:02d})")


def gen_f05(fam: Path) -> None:
    for i in range(1, 11):
        v = f"{i:02d}"
        tools = F05_CORE + F05_EXTRA[v] + (F05_BP_TOOLS if i <= 6 else [])
        emit_variant(fam, f"S-F05-{v}", tools,
                     f"动态调试排障: runtarget 全链(变体 {v}: 附加 {'+'.join(F05_EXTRA[v])})")
    spec = ["# F05 动态调试排障 — 族级规格", "",
            f"- 样本: runtarget-01.exe（sha256={RUNTARGET_SHA}; Worker.Accumulate 确定性循环, TOTAL=70, 退出码 70）",
            "- 骨架: debug_capabilities(前置能力) → open_files → debug_launch(net48-exe/x64, break_kind=entry) → debug_status → 断点(Worker.Step) → wait_event → get_stack → get_locals → 变体附加工具 → pause/continue → read_events → (v≤6: 断点管理) → terminate",
            "- 强断言: capabilities launch=true(x64); launch 返回 session/generation; 断点命中事件 kind; 栈顶方法=Step; 局部变量 total/i 集合; 事件游标单调; terminate 后 idle",
            "- 变体差异: 附加调试面(expand/read_memory/dump/step/threads/modules/restart/exception_policy); 断点方法(Step/Accumulate/Report 轮换)", "",
            "| 变体 | 附加工具 | 断点方法 |", "| --- | --- | --- |"]
    for i in range(1, 11):
        v = f"{i:02d}"
        spec.append(f"| S-F05-{v} | {', '.join(F05_EXTRA[v])} | {['Step','Accumulate','Report'][i%3]} |")
    (fam / "SPEC-fam05.md").write_text("\n".join(spec) + "\n", encoding="utf-8")


def gen_f06(fam: Path) -> None:
    tools = ["open_files", "get_assembly_info", "edit_begin", "edit_resource_export",
             "edit_resource_import", "edit_apply", "edit_review", "edit_commit",
             "edit_history", "edit_undo", "edit_redo", "edit_recover",
             "edit_accept_live", "edit_status", "search_types"]
    spec = ["# F06 资源提取与替换 — 族级规格", "",
            "- 样本: resource-01（嵌入 ResourceSample.strings: greeting/payload_hint/count 三键）",
            "- 骨架: open → get_assembly_info → edit_begin → edit_resource_export(strings→ArtifactRoot) → 变体半数 edit_resource_import(回导) → edit_apply(managed_resource_add) → edit_review → commit → edit_history → v1-5 undo / v6-10 redo → 恢复面探针(v6-10 edit_recover / v1-5 edit_accept_live 预期错误) → edit_status",
            "- 强断言: export 产物路径+字节数; import/apply 后 revision 递增; commit 后 lineage; 探针错误码精确(NO_RECOVERY/无待恢复类域包)",
            "- 变体差异: import 与否、undo/redo 分支、探针工具", "",
            "| 变体 | import | undo/redo | 探针 |", "| --- | --- | --- | --- |"]
    for i in range(1, 11):
        spec.append(f"| S-F06-{i:02d} | {'是' if i % 2 == 0 else '否'} | {'undo' if i <= 5 else 'redo'} | {'accept_live' if i <= 5 else 'recover'} |")
    (fam / "SPEC-fam06.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F06-{i:02d}", tools,
                     f"资源提取替换链 + 恢复面探针(变体 {i:02d})")


def gen_f07(fam: Path) -> None:
    tools = ["open_files", "search_types", "get_type_info", "list_methods",
             "rename_symbol_by_token", "decompile_type", "get_type_property",
             "edit_begin", "edit_export", "edit_history", "edit_restore",
             "edit_rollback", "edit_recover", "edit_accept_live", "edit_status",
             "save_assembly"]
    spec = ["# F07 符号重命名重构 — 族级规格", "",
            "- 样本: renametree-01（IRepository/OldRepository/OldService/UglyName_*, 6 类型）",
            "- 骨架: open → search_types(Old) → get_type_info → list_methods → rename(变体目标: OldRepository/OldService/UglyName_1 轮换) → decompile_type 复核(新名出现) → get_type_property → edit_begin → edit_export(检查点) → edit_history → edit_restore(预期错误: 无 drift) → rollback → 探针(edit_recover v6-10 / edit_accept_live v1-5) → v1-5 save_assembly → edit_status",
            "- 强断言: rename 后类型面集合更新精确; export 产物路径; 探针错误码; save source_preserved",
            "- 变体差异: rename 目标与新名、探针工具、save 分支", ""]
    for i in range(1, 11):
        spec.append(f"| S-F07-{i:02d} | rename→{['OldRepository','OldService','UglyName_1'][i%3]}2 | 探针 {'accept_live' if i<=5 else 'recover'} |")
    (fam / "SPEC-fam07.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F07-{i:02d}", tools,
                     f"重命名重构链 + 血统导出/恢复面(变体 {i:02d})")


def gen_f08(fam: Path) -> None:
    tools = ["open_files", "find_references", "find_callers", "find_callees",
             "find_overrides", "find_path_to_type", "decompile_method",
             "search_members", "get_type_info", "list_methods", "search_types"]
    spec = ["# F08 跨程序集影响分析 — 族级规格", "",
            "- 样本对: xref-a（Square/Client）+ xref-b（IShape/Circle/Geometry）",
            "- 骨架: open(双集) → find_overrides(IShape.Area → Square+Circle) → find_callers(Area→Describe) → find_callees(Client.TotalArea→xref-b 方法集) → find_references(变体 target_kind) → find_path_to_type(变体对) → decompile_method(变体) → search_members(Area) → get_type_info → list_methods → search_types",
            "- 强断言: overrides 集合含 XRefApp.Square(is_interface_impl)与 Circle; callees 跨集目标集; 变体 kind 命中",
            "- 变体差异: references 目标与方法、path 端点、decompile 目标", ""]
    for i in range(1, 11):
        spec.append(f"| S-F08-{i:02d} | refs→{['IShape.Area','Circle','Geometry.Describe','Square.Area'][i%4]} |")
    (fam / "SPEC-fam08.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F08-{i:02d}", tools, f"跨集影响分析(变体 {i:02d})")


def gen_f09(fam: Path) -> None:
    tools = ["open_files", "search_string_literals", "list_string_constants",
             "search_constants", "decompile_method", "get_type_info",
             "get_method_il", "find_by_attribute", "search_types", "list_methods"]
    spec = ["# F09 字符串/常量取证 — 族级规格", "",
            "- 样本: constmatrix-01（Strings.* 4 常量 + Numbers.*; Join 内联 ALPHA-KEY-0001 与 8443）",
            "- 骨架: open → search_string_literals(变体查询: ALPHA/beta/cache.db/8443) → list_string_constants(Consumer) → search_constants(变体值: 8443/28/42/100) → decompile_method(Join/Sum/PathOf) → get_type_info → get_method_il(变体方法) → find_by_attribute(Obsolete→Strings) → search_types → list_methods",
            "- 强断言: ALPHA-KEY-0001 恰 1 处(Join); 8443 归属 Join; Obsolete 目标=ConstMatrix.Strings; IL 指令数精确",
            "- 变体差异: 查询串/常量值/方法目标", ""]
    for i in range(1, 11):
        spec.append(f"| S-F09-{i:02d} | q={ ['ALPHA','beta','cache.db','SELECT'][i%4] } v={ [8443,100,28,42][i%4] } m={ ['Join','Sum','PathOf'][i%3] } |")
    (fam / "SPEC-fam09.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F09-{i:02d}", tools, f"字符串/常量取证(变体 {i:02d})")


def gen_f10(fam: Path) -> None:
    tools = ["open_files", "find_unity_messages", "get_type_info", "list_methods",
             "decompile_method", "generate_bepinex_plugin", "search_types",
             "get_method_il", "find_callers", "search_members"]
    spec = ["# F10 Unity 游戏分析 — 族级规格", "",
            "- 样本: unitymsgs-01（PlayerController: Awake/Start/Update/OnTriggerEnter + EnemySpawner: Awake/Start; 共 6 消息）+ TestIL(UnityComponent 3 消息)",
            "- 骨架: open(unitymsgs+TestIL) → find_unity_messages(两集) → get_type_info(PlayerController) → list_methods → decompile_method(变体消息方法) → generate_bepinex_plugin(变体名/guid) → search_types(PlayerController) → get_method_il(变体) → find_callers(变体) → search_members",
            "- 强断言: unitymsgs 消息集恰 6 项且类型归属精确; TestIL UnityComponent 3 消息; 生成器输出含变体插件名; IL 存在",
            "- 变体差异: 消息方法(5 种)、插件名/GUID、caller 目标", ""]
    for i in range(1, 11):
        spec.append(f"| S-F10-{i:02d} | msg={ ['Awake','Start','Update','OnTriggerEnter','PeekHealth'][i%5] } plugin=UnityPlug{i:02d} |")
    (fam / "SPEC-fam10.md").write_text("\n".join(spec) + "\n", encoding="utf-8")
    for i in range(1, 11):
        emit_variant(fam, f"S-F10-{i:02d}", tools, f"Unity 消息面分析 + 插件生成(变体 {i:02d})")


GENS = {1: gen_f01, 2: gen_f02, 3: gen_f03, 4: gen_f04, 5: gen_f05,
        6: gen_f06, 7: gen_f07, 8: gen_f08, 9: gen_f09, 10: gen_f10}


def main() -> None:
    for n, gen in GENS.items():
        fam = FAMS / f"fam{n:02d}"
        fam.mkdir(parents=True, exist_ok=True)
        gen(fam)
        print(f"fam{n:02d}: generated")
    (ROOT / "manifests" / "samples-manifest.json").exists() or print("WARN: manifest missing")
    print("DONE: 10 families x 10 variants")


if __name__ == "__main__":
    main()
