"""恶意静态分析: 特征串与标注面取证(变体 10)"""

SCENARIO_ID = 'S-F03-10'
DECLARED_TOOLS = ["open_files", "get_assembly_info", "list_types", "search_string_literals", "find_by_attribute", "decompile_by_token", "find_references", "search_constants", "get_type_fields", "search_members", "decompile_method", "list_methods"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f03_10(scenario_env):
    """恶意静态分析: 特征串与标注面取证(变体 10)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F03-10')
