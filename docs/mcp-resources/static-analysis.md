# Static analysis and navigation

## Load and inventory

- `open_files` loads files or directories into dnSpy without executing them.
- `list_assemblies`, `get_assembly_info`, `list_types`, `get_type_info` and `list_methods` inventory
  metadata. Prefer compact/name-only modes and pagination for large Unity installations.

## Decompilation

- `decompile_method` resolves a method by type/name and optional parameter types or MethodDef token.
- `decompile_type` returns the full decompiled type.
- `decompile_by_token` is the preferred continuation when another tool already returned a token.
- Nested/compiler-generated types are addressable. Async/iterator kickoff methods can include the
  underlying state-machine `MoveNext` when high-level reconstruction is incomplete.

## Cross-references and discovery

- `find_callers` finds call sites; `find_callees` lists members used by one method.
- `find_references` finds method, field, type or string references.
- `find_overrides` follows virtual overrides and interface implementations.
- `search_types` and `search_members` locate metadata by name.
- `find_unity_messages` locates Unity lifecycle/message methods that have no IL call site.
- `find_by_attribute` locates types/members by custom attribute.
- `search_string_literals`, `list_string_constants` and `search_constants` connect observable
  strings or magic numbers to MethodDef tokens and IL locations.

## Code generation

- `generate_harmony_patch` resolves a real method and emits a signature-aware prefix, postfix or
  transpiler class.
- `generate_bepinex_plugin` emits the plugin shell plus signature-aware hooks.

Tokens are module-local. When a workflow crosses modules, retain the assembly/module identity along
with the token. Prefer returned tokens over reconstructing method names or signatures manually.
