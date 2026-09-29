"""字符串/常量取证(变体 09)"""

SCENARIO_ID = 'S-F09-09'
DECLARED_TOOLS = ["open_files", "search_string_literals", "list_string_constants", "search_constants", "decompile_method", "get_type_info", "get_method_il", "find_by_attribute", "search_types", "list_methods", "edit_status"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f09_09(scenario_env):
    """字符串/常量取证(变体 09)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F09-09')
