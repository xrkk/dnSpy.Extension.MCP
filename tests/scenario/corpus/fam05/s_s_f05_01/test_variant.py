"""动态调试排障: runtarget 全链(变体 01: 附加 debug_expand_value+debug_restart)"""

SCENARIO_ID = 'S-F05-01'
DECLARED_TOOLS = ["debug_capabilities", "open_files", "list_assemblies", "debug_launch", "debug_status", "debug_read_events", "debug_list_modules", "debug_set_breakpoint", "debug_wait_event", "debug_get_stack", "debug_get_locals", "debug_continue", "debug_pause", "debug_terminate", "debug_expand_value", "debug_restart", "debug_list_breakpoints", "debug_set_breakpoint_enabled", "debug_remove_breakpoint"]

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

def test_s_f05_01(scenario_env):
    """动态调试排障: runtarget 全链(变体 01: 附加 debug_expand_value+debug_restart)"""
    from workflow import run_variant
    run_variant(scenario_env, 'S-F05-01')
