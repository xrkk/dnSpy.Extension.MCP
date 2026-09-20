# Recommended tool workflows

Inspect live schemas with `tools/list`; the sequences below describe intent, not hand-built packets.

## Locate behavior from a visible string

`search_string_literals` → `decompile_by_token` → `find_callers`/`find_callees`

## Understand a Unity type

`search_types` → `get_type_info` → `find_unity_messages` → `decompile_method`

## Follow polymorphic behavior

`search_members` → `find_overrides` → `decompile_by_token`

## Patch and persist a method

Read `dnspy://docs/il-editing`, then:
`get_method_il` → `patch_method_il`/`force_return`/`nop_method` → `get_method_il` →
`decompile_by_token` → `save_assembly`. Use `revert_method_il` before saving if validation fails.

## Build, review and optionally commit a structured edit

Read `dnspy://docs/il-editing`, then:
`edit_begin` → one or more revision-checked `edit_apply` calls → `edit_review` → inspect diffs,
fingerprints and required risk IDs → `edit_commit`, or `edit_rollback` to discard it.

A commit updates the live module and creates a persistent checkpoint; use `edit_history`,
`edit_undo`, `edit_redo`, `edit_restore` and `edit_export` for lineage navigation/export. Use returned
object IDs to link operations created within the same transaction and exact metadata tokens for
existing objects. If a revision, owner, live fingerprint or review becomes stale, query
`edit_status` and either retry with a new request ID or explicitly roll back—never scan endpoints or
replay mutations automatically.

## Generate a runtime patch

`list_methods`/`search_members` → `generate_harmony_patch`, or use
`generate_bepinex_plugin` when a complete plugin shell and multiple hooks are needed.

## Debug a launched program

Read `dnspy://docs/dynamic-debugging`, then:
`debug_capabilities` → `debug_launch` → `debug_wait_event` → `debug_list_threads` →
`debug_get_stack` → `debug_get_locals`/`debug_expand_value` → `debug_continue` or `debug_step` →
`debug_terminate`.

## Dump a live module

`debug_capabilities` → `debug_launch` → `debug_list_modules` → `debug_dump_module` →
`debug_terminate`. Treat the artifact as untrusted and respect the artifact-store retention policy.
