"""Unity 消息面分析 + 插件生成(变体 04)"""

SCENARIO_ID = 'S-F10-04'
DECLARED_TOOLS = ["open_files", "find_unity_messages", "get_type_info", "list_methods", "decompile_method", "generate_bepinex_plugin", "search_types", "get_method_il", "find_callers", "search_members", "edit_status"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f10_04(scenario_env):
    """Unity 消息面分析 + 插件生成(变体 04)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F10-04')
