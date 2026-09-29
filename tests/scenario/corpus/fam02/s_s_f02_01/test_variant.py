"""模组开发链: bepinex 插件 + 编辑事务(rollback)"""

SCENARIO_ID = 'S-F02-01'
DECLARED_TOOLS = ["open_files", "list_assemblies", "get_type_info", "list_methods", "find_unity_messages", "decompile_method", "edit_begin", "edit_apply", "edit_impact_scan", "edit_status", "generate_bepinex_plugin", "edit_compile", "edit_import", "edit_rollback"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f02_01(scenario_env):
    """模组开发链: bepinex 插件 + 编辑事务(rollback)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F02-01')
