# IL editing and persistence

IL and metadata changes mutate dnSpy's in-memory module. Treat them as write operations and verify
the target module, method token and output path before calling them.

## Safe workflow

1. Locate the method with `list_methods`, search/xref tools or `decompile_by_token`.
2. Read the current body with `get_method_il` and retain the returned instruction indices.
3. Use `patch_method_il`, `force_return` or `nop_method`.
4. Re-read with `get_method_il` and decompile the method to validate intent.
5. Use `revert_method_il` if validation fails.
6. Call `save_assembly` only after the destination is explicit and approved.

`patch_method_il` supports ordered replace/insert/delete/init-locals edits and snapshots the original
body on the first mutation. `force_return` and `nop_method` are higher-level helpers using the same
rollback model. `revert_method_il` is session-local and is not dnSpy Ctrl+Z.

`rename_symbol_by_token` renames types, methods, fields, enum members, properties, events, parameters
and generic parameters and updates applicable references in the current module.

When saving over the source path, the server first creates a timestamped `.bak`. Saving to another
path does not modify the original and returns no backup path. GAC targets are rejected. No static
write tool (`patch_method_il`, `force_return`, `nop_method`, `revert_method_il`,
`rename_symbol_by_token`, `save_assembly`) may run while dynamic debugging is active.

## Transactional structured editing

Use the 18 advertised `edit_*` product tools when an edit spans metadata objects or needs a review,
commit, history or export boundary:

1. `edit_begin(request_id, assembly_name[, module_mvid])` acquires the single process-wide lease and
   creates a private copy. It accepts only an initialized Streamable HTTP or legacy SSE owner.
2. `edit_apply(request_id, transaction_id, expected_revision, operation)` applies one typed operation
   to that private copy. Carry forward the returned `work_revision`; never guess or auto-replay IDs.
3. `edit_review(request_id, transaction_id, expected_revision[, dynamic_validation])` validates the
   fixed revision, performs a write/reload check and returns canonical diffs and risk IDs.
4. `edit_commit` linearizes a reviewed revision to the live module and persists its checkpoint;
   `edit_rollback` discards an uncommitted private copy. Use `edit_status`/`edit_recover` for recovery.
5. `edit_history`, `edit_undo`, `edit_redo`, `edit_restore` and `edit_export` navigate or export the
   persistent lineage. `edit_accept_live` explicitly starts a new baseline after accepted UI drift.

The 39 operation kinds cover types, methods, fields, properties, events, parameters, generic
parameters, attributes, security, identities, entry point, resources, interface/reference additions,
strong-name removal and `method_body_replace`. A newly created object is addressed
by the returned transaction-scoped `object_id`; an existing object is addressed by its exact
metadata token. Removal is only `reject_if_referenced`. Unknown raw fields such as `raw_metadata`,
`pe_bytes`, `heap`, `rva` and `hex_patch` are rejected by the published schema.

`strong_name_remove` is presently always rejected: no trusted target-bound causal evidence source
is available, so ACC016 remains blocked. The old live write tools are blocked while a structured
transaction is active, so do not mix the two workflows.

A syntactically valid operation with an unknown version returns
`EDIT_OPERATION_VERSION_UNSUPPORTED`; malformed input remains schema/parameter invalid. v1
checkpoints are exact-only. Drift requires explicit `edit_accept_live` into a new v2 lineage; v2
distinguishes `exact`, `validated_drift` and `unverified_drift`, with explicit confirmation for
migration. `edit_compile.documents` entries accept only `path` and `content`.

Optional dynamic review is explicit. It is applicable only to an executable with an entry point,
requires the debugger to be idle and the VMware/VirtualBox execution gate to allow execution, writes
one temporary image below ArtifactRoot, pauses it at managed entry through dnSpy's debugger, then
terminates and removes the image. Inspect the returned `dynamic_validation` state; `blocked`,
`failed`, `not_applicable` and `not_requested` are not aliases for `passed`.
