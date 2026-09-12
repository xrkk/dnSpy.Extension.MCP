# dnSpy MCP — Complete Tool Reference (EN)

Machine-checked against the live `tools/list` registry (78 advertised tools; 8 additional `edit_test_*` seams are schema'd but unadvertised and gated behind `DNMCP_TEST=1`). Counts in this document come from `tools/export_tool_registry.py`, never from hand edits.

See also: [README.md](../README.md) · [中文完整说明](MCP-TOOLS.zh-CN.md)

## 1. Wire protocol

- Transport: Streamable HTTP (also legacy SSE) at `http://localhost:<port>/mcp` (default 15378; the Options page shows the actual port).
- Envelope: every tool returns `{"schema_version": "...", "ok": bool, "state": "...", ...}`; failures carry `error.code`, `error.message`, `error.current_state`, `error.recovery`.
- Sessions: edit/debug transactions are owned by one initialized MCP session; `request_id` gives idempotent retries.

## 2. Static analysis & codegen (32 tools)

open_files · list_assemblies · get_assembly_info · list_types · get_type_info · list_methods · get_type_fields · get_type_property · decompile_method · decompile_type · decompile_member · search_string_constants · search_constants · search_arrays · find_references · find_overrides · find_derived_types · navigate_type relationship tools · rename_symbol_by_token · save_assembly · patch_method_il · force_return · nop_method · revert_method · generate_bepinex_plugin · generate_harmony_patch · search_structures · find_unity_scenes · find_unity_methods · get_method_il · find_type_relationship · get_type_layout — see README §Features for per-tool parameters.

## 3. Launch-only dynamic debugging (28 tools)

debug_capabilities · debug_status · debug_launch · debug_pause · debug_continue · debug_restart · debug_terminate · debug_read_events · debug_wait_event · debug_set_breakpoint · debug_list_breakpoints · debug_set_breakpoint_enabled · debug_remove_breakpoint · debug_list_threads · debug_get_stack · debug_step · debug_get_locals · debug_expand_value · debug_list_modules · debug_read_memory · debug_dump_module · debug_set_exception_policy — plus the frozen test seams (debug_test_*) gated by `DNMCP_TEST=1`. Launch is the only execution gate: static tools never run sample code.

## 4. Transactional structured editing (18 advertised)

### 4.1 Lifecycle

| Tool | Purpose |
| --- | --- |
| `edit_begin` | Acquire the process-wide edit lease for one loaded pure-managed single-module assembly; create the private copy |
| `edit_status` | State/revision/fingerprints/capacity/risks without mutation |
| `edit_apply` | Apply one of **37** operation kinds to the private copy (`request_id` + `expected_revision` required) |
| `edit_review` | Validate the fixed revision; canonical diffs + required risk confirmations |
| `edit_commit` | Linearize to the live module + persist the checkpoint (one recoverable step) |
| `edit_rollback` | Discard the private copy; release the lease |
| `edit_history` / `edit_undo` / `edit_redo` / `edit_restore` | Browse/navigate the persistent checkpoint lineage |
| `edit_export` | Export an exact checkpoint below ArtifactRoot |
| `edit_recover` / `edit_accept_live` | Resolve partial-commit recovery; accept a UI-diverged module as a new baseline |

### 4.2 Compile → import

| Tool | Purpose |
| --- | --- |
| `edit_compile` | Compile C# through dnSpy's public Roslyn compiler (assembly + Portable PDB stay in memory; no analyzer/generator/script surface) |
| `edit_import` | Import compiled members into the private copy as frozen operations — structured-signature matching, generated-subtree handling, all-or-nothing rejection; symbol rows transfer; saved images keep an embedded-only PDB |
| `edit_impact_scan` | Cross-assembly impact over the loaded modules (`scope=loaded_modules`); inbound references become confirmation-required risks |

### 4.3 Identity / resources (edit_apply kinds)

- `assembly_update`, `module_update`, `assembly_ref_update`, `entry_point_set`
- `managed_resource_add/update/remove`, `win32_resource_add/update/remove`
- `strong_name_remove` (evidence-gated)

### 4.4 The 37 operation kinds

type_add/update/remove · method_add/update/remove · field_add/update/remove · property_add/update/remove · event_add/update/remove · parameter_add/update/remove · generic_parameter_add/update/remove · method_body_replace · attribute_add/remove · security_add/remove · assembly_update · module_update · assembly_ref_update · entry_point_set · managed_resource_add/update/remove · win32_resource_add/update/remove · strong_name_remove

## 5. Error codes and recovery (frozen)

`EditWire.Message` / `EditWire.Recovery` are the source of truth; the stable set includes EDIT_TRANSACTION_BUSY, EDIT_TRANSACTION_NOT_FOUND, EDIT_OWNER_REQUIRED/MISMATCH, EDIT_REVISION_CONFLICT, EDIT_LIVE_MODULE_CONFLICT, EDIT_REVIEW_STALE, EDIT_VALIDATION_FAILED, EDIT_RISK_CONFIRMATION_REQUIRED, EDIT_CAPABILITY_UNAVAILABLE, EDIT_CAPACITY_EXCEEDED, EDIT_DEBUG_NOT_IDLE, EDIT_LIVE_STATE_UNKNOWN, EDIT_CHECKPOINT_INVALID/COMMIT_FAILED/CLEANUP_FAILED, EDIT_EXPORT_BLOCKED, EDIT_REPLAY_CONFIRMATION_REQUIRED/UNVERIFIED, EDIT_OPERATION_VERSION_UNSUPPORTED, EDIT_HISTORY_CONFLICT, EDIT_BRANCH_SELECTION_REQUIRED, EDIT_LINEAGE_DIVERGED, EDIT_SOURCE_IDENTITY_CONFLICT, EDIT_RECOVERY_NOT_FOUND, REQUEST_ID_REUSE.

## 6. Resources

The MCP resources face exposes 14 concrete resources (assembly list, type index, edit status, debug events, …); `resources/templates/list` is intentionally empty. The tools/list and resources faces are the two machine-readable registries.
