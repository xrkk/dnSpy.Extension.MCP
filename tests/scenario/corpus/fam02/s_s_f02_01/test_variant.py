"""模组开发链: bepinex 插件 + 编辑事务(rollback)"""

SCENARIO_ID = 'S-F02-01'
DECLARED_TOOLS = ["open_files", "get_type_info", "list_methods", "find_unity_messages", "decompile_method", "edit_begin", "edit_compile", "edit_import", "edit_apply", "edit_impact_scan", "edit_review", "edit_commit", "edit_rollback", "edit_status", "generate_harmony_patch", "generate_bepinex_plugin", "search_types"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f02_01(scenario_env):
    """模组开发链: bepinex 插件 + 编辑事务(rollback)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F02-01')
