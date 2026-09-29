"""许可证绕过: 对 LicenseGate 实施 nop_method 写操作并还原"""

SCENARIO_ID = 'S-F01-08'
DECLARED_TOOLS = ["open_files", "list_assemblies", "search_types", "search_string_literals", "list_string_constants", "decompile_method", "find_callers", "get_method_il", "revert_method_il", "edit_status", "nop_method", "patch_method_il"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f01_08(scenario_env):
    """许可证绕过: 对 LicenseGate 实施 nop_method 写操作并还原"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F01-08')
