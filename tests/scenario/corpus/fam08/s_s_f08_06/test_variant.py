"""跨集影响分析(变体 06)"""

SCENARIO_ID = 'S-F08-06'
DECLARED_TOOLS = ["open_files", "find_references", "find_callers", "find_callees", "find_overrides", "find_path_to_type", "decompile_method", "search_members", "get_type_info", "list_methods", "search_types"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f08_06(scenario_env):
    """跨集影响分析(变体 06)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F08-06')
