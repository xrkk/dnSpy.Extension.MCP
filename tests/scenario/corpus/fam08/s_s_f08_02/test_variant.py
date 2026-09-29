"""跨集影响分析(变体 02)"""

SCENARIO_ID = 'S-F08-02'
DECLARED_TOOLS = ["open_files", "list_assemblies", "find_overrides", "find_callers", "find_callees", "find_references", "find_path_to_type", "decompile_method", "search_members", "get_type_info", "list_methods", "search_types", "edit_status"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f08_02(scenario_env):
    """跨集影响分析(变体 02)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F08-02')
