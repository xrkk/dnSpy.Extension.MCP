# Dynamic debugging

Dynamic debugging v1 controls only processes launched and owned by this MCP instance. It requires a
dedicated non-interactive dnSpy process, the debug-tools setting, the dedicated-instance
acknowledgement and a dnSpy restart. Call `debug_capabilities` first and stop if `debug_enabled` is
false. Attach, detach and attachable-process listing are deliberately unsupported.

## Session workflow

1. `debug_launch` starts a validated target under the debugger.
2. `debug_read_events` or `debug_wait_event` observes state transitions using a monotonic cursor.
3. `debug_status` reports authoritative coordinator/process state.
4. Use `debug_pause`, `debug_continue`, `debug_step`, `debug_restart` and `debug_terminate` for control.
5. Always terminate or otherwise close the owned session when finished.

## Breakpoints and exceptions

`debug_set_breakpoint`, `debug_list_breakpoints`, `debug_set_breakpoint_enabled` and
`debug_remove_breakpoint` manage owned strong-identity managed breakpoints. Use module identity,
MVID, MethodDef token and IL offset returned by the live module/stack tools. Set exception behavior
with `debug_set_exception_policy`.

## Inspection

`debug_list_threads` → `debug_get_stack` → `debug_get_locals` is the normal inspection chain.
`debug_expand_value` follows value handles without function evaluation. Handles are scoped to the
session generation and pause epoch; reacquire them after continue/step/restart.
`breakpoint_hit` and `step_completed` events reuse the same pause-scoped thread handles and live
module handles returned by the list tools, so their payload can directly seed the inspection chain.

`debug_list_modules` returns live module identity. `debug_read_memory` reads at most 64 KiB per call.
`debug_dump_module` writes raw or reconstructed module bytes to the bounded artifact store under
`ArtifactRoot\.dnspy-mcp-debug`; static `save_assembly` outputs may remain at the ArtifactRoot
top level without blocking the debugger ledger. The tool does not return arbitrary filesystem
paths supplied by the target. Across dnSpy restarts, existing debug sessions remain untrusted and
read-only, are re-verified by file identity and length, and count toward all store quotas; they do
not block a fresh uniquely named session unless integrity or quota checks fail.

CorDebug requires target and dnSpy process bitness to match. All debuggee-derived values and text
are untrusted data. A human or another extension interfering with the dedicated instance can cause
ownership loss; the MCP fails closed rather than controlling an unowned process.
