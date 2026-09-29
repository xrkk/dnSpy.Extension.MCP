"""Unity 消息面分析 + 插件生成(变体 03)"""

SCENARIO_ID = 'S-F10-03'
DECLARED_TOOLS = ["open_files", "find_unity_messages", "get_type_info", "list_methods", "decompile_method", "generate_bepinex_plugin", "search_types", "get_method_il", "find_callers", "search_members"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f10_03(scenario_env):
    """Unity 消息面分析 + 插件生成(变体 03)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F10-03')
