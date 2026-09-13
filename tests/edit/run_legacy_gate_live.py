#!/usr/bin/env python3
"""DNMCP_TEST VM regression: legacy adapters must honor the UI-debug gate first.
No target is supplied, so a regression cannot edit any assembly. Run VM-local.
"""
import json
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient

client = DnSpyClient(sys.argv[1], client_name='legacy-debug-gate-regression')
client.initialize()
failures = []
try:
    client.call_tool_json('debug_test_start', {'mode': 'ui_debugging'})
    for tool in ('patch_method_il', 'force_return', 'nop_method', 'revert_method_il', 'rename_symbol_by_token', 'save_assembly'):
        try:
            result = client.call_tool_json(tool, {'name': 'Never'})
        except Exception as ex:
            raw = str(ex)
            result = json.loads(raw[raw.index('{'):]) if '{' in raw else {'error': {'code': raw}}
        code = result.get('error', {}).get('code')
        print(tool, code, flush=True)
        if code != 'INVALID_STATE':
            failures.append((tool, code))
    # Reads remain available under the same process-wide debugging condition.
    client.call_tool_json('list_assemblies')
finally:
    client.call_tool_json('debug_test_start', {'mode': 'ui_debugging_off'})
    client.close()
assert not failures, failures
print('PASS six legacy routes reject before owner/argument handling; read remains available')
