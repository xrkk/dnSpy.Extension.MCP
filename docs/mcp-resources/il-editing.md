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
