# dnSpy MCP — Complete Tool Reference (EN)

Machine-checked against `tools/list`: the production surface is 72 tools when both feature gates are enabled (32 static + 22 debug + 18 edit). A `DNMCP_TEST=1` acceptance process additionally advertises 6 `debug_test_*` probes, producing the 78-tool snapshot; 9 callable `edit_test_*` seams remain unadvertised. With the debug gate closed, the corresponding totals are 51 production / 57 acceptance (only `debug_capabilities` remains from the debug family). Use `debug_capabilities` and the live registry to identify the active profile. Counts come from `tools/export_tool_registry.py` with its explicit profile and debug-gate arguments.

See also: [README.md](../README.md) · [中文完整说明](MCP-TOOLS.zh-CN.md)

## 1. Wire protocol

- Transport: Streamable HTTP (also legacy SSE) at `http://localhost:<port>/mcp` (default 15378; the Options page shows the actual port).
- Envelope: transactional edit and debug tools return a structured `schema_version`/`ok`/`state` result; edit failures carry `error.code`, `error.message`, `error.current_state`, and `error.recovery`. Static analysis/codegen tools commonly return text; consult each live `outputSchema` when one is advertised, rather than assuming a universal envelope.
- Sessions: edit/debug transactions are owned by one initialized MCP session; `request_id` gives idempotent retries.

## 2. Static analysis & codegen (32 tools)

open_files · list_assemblies · get_assembly_info · list_types · search_types · get_type_info · list_methods · search_members · get_method_il · get_type_fields · get_type_property · list_string_constants · search_string_literals · search_constants · decompile_by_token · decompile_method · decompile_type · find_by_attribute · find_callees · find_callers · find_overrides · find_path_to_type · find_references · find_unity_messages · generate_harmony_patch · generate_bepinex_plugin · force_return · nop_method · patch_method_il · revert_method_il · rename_symbol_by_token · save_assembly — see the [single-file AI tool reference](AI-TOOL-REFERENCE.zh-CN.md) for per-tool arguments and return structures.

## 3. Launch-only dynamic debugging (22 production tools; 28 in acceptance mode)

debug_capabilities · debug_status · debug_launch · debug_pause · debug_continue · debug_restart · debug_terminate · debug_read_events · debug_wait_event · debug_set_breakpoint · debug_list_breakpoints · debug_set_breakpoint_enabled · debug_remove_breakpoint · debug_list_threads · debug_get_stack · debug_step · debug_get_locals · debug_expand_value · debug_list_modules · debug_read_memory · debug_dump_module · debug_set_exception_policy. Acceptance mode additionally advertises `debug_test_spy` · `debug_test_flood` · `debug_test_start` · `debug_test_dump` · `debug_test_clock` · `debug_test_adapter`. Launch is the only execution gate: static tools never run sample code.

## 4. Transactional structured editing (18 advertised)

### 4.1 Lifecycle

| Tool | Purpose |
| --- | --- |
| `edit_begin` | Acquire the process-wide edit lease for one loaded pure-managed single-module assembly; create the private copy |
| `edit_status` | State/revision/fingerprints/capacity/risks without mutation |
| `edit_apply` | Apply one of **39** operation kinds to the private copy (`request_id` + `expected_revision` required) |
| `edit_review` | Validate the fixed revision; canonical diffs + required risk confirmations |
| `edit_commit` | Linearize to the live module + persist the checkpoint (one recoverable step) |
| `edit_rollback` | Discard the private copy; release the lease |
| `edit_history` / `edit_undo` / `edit_redo` / `edit_restore` | Browse/navigate the persistent checkpoint lineage |
| `edit_export` | Export an exact checkpoint below ArtifactRoot |
| `edit_recover` / `edit_accept_live` | Resolve partial-commit recovery; accept a UI-diverged module as a new baseline |

### 4.2 Compile → import

| Tool | Purpose |
| --- | --- |
| `edit_compile` | Compile C# through dnSpy's public Roslyn compiler; each `documents` item is closed to `path` and `content` (assembly + Portable PDB stay in memory; no analyzer/generator/script surface) |
| `edit_import` | Import compiled members into the private copy as frozen operations — structured-signature matching, generated-subtree handling, all-or-nothing rejection; symbol rows transfer; saved images keep an embedded-only PDB |
| `edit_impact_scan` | Cross-assembly impact over the loaded modules (`scope=loaded_modules`); inbound references become confirmation-required risks |

### 4.3 Identity / resources (edit_apply kinds)

- `assembly_update`, `module_update`, `assembly_ref_update`, `entry_point_set`
- `managed_resource_add/update/remove`, `win32_resource_add/update/remove`
- `strong_name_remove` (currently always rejected: no trusted target-bound causal evidence source; ACC016 is blocked)

### 4.4 The 39 operation kinds

`type_add` · `type_update` · `type_remove` · `method_add` · `method_update` · `method_remove` · `field_add` · `field_update` · `field_remove` · `property_add` · `property_update` · `property_remove` · `event_add` · `event_update` · `event_remove` · `parameter_add` · `parameter_update` · `parameter_remove` · `generic_parameter_add` · `generic_parameter_update` · `generic_parameter_remove` · `method_body_replace` · `attribute_add` · `attribute_remove` · `security_add` · `security_remove` · `assembly_update` · `module_update` · `assembly_ref_update` · `entry_point_set` · `managed_resource_add` · `managed_resource_update` · `managed_resource_remove` · `win32_resource_add` · `win32_resource_update` · `win32_resource_remove` · `strong_name_remove` · `interface_add` · `reference_add`

The operation schema and `EditWire.OperationKinds` contain 39 entries. The current `edit_apply` registry description still says “37”; that text is stale and is not the operation allow-list. Check the live input schema for accepted kinds. This source-description discrepancy is not a claim that the product defect is fixed.

## 5. Error codes and recovery (frozen)

`EditWire.Message` / `EditWire.Recovery` are the source of truth; the stable set includes EDIT_TRANSACTION_BUSY, EDIT_TRANSACTION_NOT_FOUND, EDIT_OWNER_REQUIRED/MISMATCH, EDIT_REVISION_CONFLICT, EDIT_LIVE_MODULE_CONFLICT, EDIT_REVIEW_STALE, EDIT_VALIDATION_FAILED, EDIT_RISK_CONFIRMATION_REQUIRED, EDIT_CAPABILITY_UNAVAILABLE, EDIT_CAPACITY_EXCEEDED, EDIT_DEBUG_NOT_IDLE, EDIT_LIVE_STATE_UNKNOWN, EDIT_CHECKPOINT_INVALID/COMMIT_FAILED/CLEANUP_FAILED, EDIT_EXPORT_BLOCKED, EDIT_REPLAY_CONFIRMATION_REQUIRED/UNVERIFIED, EDIT_OPERATION_VERSION_UNSUPPORTED, EDIT_HISTORY_CONFLICT, EDIT_BRANCH_SELECTION_REQUIRED, EDIT_LINEAGE_DIVERGED, EDIT_SOURCE_IDENTITY_CONFLICT, EDIT_RECOVERY_NOT_FOUND, REQUEST_ID_REUSE.

A syntactically valid operation with an unknown version returns `EDIT_OPERATION_VERSION_UNSUPPORTED`; malformed input remains protocol/schema invalid. Checkpoint v1 is exact-only. A drifted live module must be explicitly accepted into a new v2 lineage; v2 distinguishes `exact`, `validated_drift`, and `unverified_drift`, and migration still requires explicit confirmation.

## 6. Resources

The MCP resources face exposes 14 concrete resources (assembly list, type index, edit status, debug events, …); `resources/templates/list` is intentionally empty. The tools/list and resources faces are the two machine-readable registries.

### Resource path import and export

`edit_resource_import` reads only ordinary, non-reparse files below `AllowedSampleRoot`, checking the size before allocation. Returned `file_id`, length and SHA-256 describe the same opened Windows file handle. `resource_type` accepts `embedded`, `linked`, or `win32`; linked imports normalize to embedded bytes and retain no external file dependency.

`edit_resource_export` defaults to `embedded`. For Win32 bytes, specify `resource_type=win32`, `type_id` or `type_name` (default `RCDATA`), `name_id` or `resource_name`, and `lang_id` (default 0). The type selectors are mutually exclusive; `name_id` takes precedence over `resource_name`. Exports reuse the checkpoint store's atomic output and actual file identity, remain below `ArtifactRoot`, and cannot overwrite the source sample.

Response contracts distinguish operation apply from batch import and impact scanning: `edit_import.result` carries `import` and `operation_count`; `edit_impact_scan.result` carries `transaction` and `impact`; resource tools carry their `import`/`export` file identity; `edit_commit.result.live_recovery` binds the precompiled inverse plan to its checkpoint. These fields are declared by each tool's outputSchema.
