"""动态调试排障: runtarget 全链(变体 10: 附加 debug_list_modules+debug_restart+debug_set_exception_policy)"""

SCENARIO_ID = 'S-F05-10'
DECLARED_TOOLS = ["debug_capabilities", "open_files", "list_assemblies", "debug_launch", "debug_status", "debug_set_breakpoint", "debug_wait_event", "debug_get_stack", "debug_get_locals", "debug_pause", "debug_continue", "debug_read_events", "debug_terminate", "debug_list_modules", "debug_restart", "debug_set_exception_policy"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f05_10(scenario_env):
    """动态调试排障: runtarget 全链(变体 10: 附加 debug_list_modules+debug_restart+debug_set_exception_policy)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F05-10')
