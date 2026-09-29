"""混淆识别: 单字符符号面遍历(变体 03)"""

SCENARIO_ID = 'S-F04-03'
DECLARED_TOOLS = ["open_files", "get_assembly_info", "list_types", "search_members", "decompile_type", "get_type_info", "find_path_to_type", "decompile_method", "list_methods", "search_types", "edit_status"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f04_03(scenario_env):
    """混淆识别: 单字符符号面遍历(变体 03)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F04-03')
