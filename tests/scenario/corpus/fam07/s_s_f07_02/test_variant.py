"""重命名重构链 + 血统导出/恢复面(变体 02)"""

SCENARIO_ID = 'S-F07-02'
DECLARED_TOOLS = ["open_files", "search_types", "get_type_info", "list_methods", "rename_symbol_by_token", "decompile_type", "get_type_property", "edit_begin", "edit_export", "edit_history", "edit_restore", "edit_rollback", "edit_recover", "edit_accept_live", "edit_status", "save_assembly"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f07_02(scenario_env):
    """重命名重构链 + 血统导出/恢复面(变体 02)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F07-02')
