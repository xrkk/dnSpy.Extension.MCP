"""模组开发链: harmony 补丁 + 编辑事务(commit)"""

SCENARIO_ID = 'S-F02-09'
DECLARED_TOOLS = ["open_files", "list_assemblies", "get_type_info", "list_methods", "find_unity_messages", "decompile_method", "edit_begin", "edit_apply", "edit_impact_scan", "edit_status", "generate_harmony_patch", "edit_review", "edit_commit"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f02_09(scenario_env):
    """模组开发链: harmony 补丁 + 编辑事务(commit)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F02-09')
