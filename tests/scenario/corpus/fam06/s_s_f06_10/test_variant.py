"""资源提取替换链 + 恢复面探针(变体 10)"""

SCENARIO_ID = 'S-F06-10'
DECLARED_TOOLS = ["open_files", "get_assembly_info", "edit_resource_export", "edit_begin", "edit_apply", "edit_review", "edit_commit", "edit_history", "edit_status", "edit_resource_import", "edit_undo", "edit_redo", "edit_recover"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f06_10(scenario_env):
    """资源提取替换链 + 恢复面探针(变体 10)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F06-10')
