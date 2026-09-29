# Edit operation catalog (edit_apply)

`edit_apply` takes one structured operation per call. The advertised schema is a compact
discriminating union: `kind` selects the operation and the remaining fields are validated
server-side against this catalog. Unknown kinds, fields outside the operation's allowed
set, and missing required fields are rejected with `EDIT_VALIDATION_FAILED` (the operation
is never staged into the transaction or its checkpoint history).

All 39 operations: required fields, optional fields, and one-line semantics.

| kind | required | optional | semantics |
| --- | --- | --- | --- |
| `type_add` | name | attributes, base_type, layout, namespace, owner_type | Append a new TypeDef row to the module (or nest it under owner_type). |
| `type_update` | target | attributes, base_type, layout, name, namespace | Update an existing type in place: name, namespace, attributes, base_type and/or whole-row layout. |
| `type_remove` | remove_mode, target | — | Remove a type row; rejects when the type is referenced or attached (remove_mode=reject_if_referenced). |
| `method_add` | name, owner_type, signature | attributes, body, custom_debug_infos, impl_attributes, overrides, pinvoke | Add a method to owner_type with a full signature (return/params/generics), optional body, overrides, PInvoke and PDB debug info. |
| `method_update` | target | attributes, has_this, impl_attributes, name, overrides, pinvoke, return_type | Update an existing method in place: name, attributes, impl_attributes, return_type, has_this, overrides, PInvoke. |
| `method_remove` | remove_mode, target | — | Remove a method row; rejects when the method is referenced or attached. |
| `field_add` | field_type, name, owner_type | attributes, constant, field_offset, initial_data, marshal | Add a field to owner_type, optionally with a constant, RVA initial data, marshal spec or explicit offset. |
| `field_update` | target | attributes, clear_constant, constant, field_offset, field_type, initial_data, marshal, name | Update an existing field in place: name, field_type, attributes, constant, initial_data, marshal, field_offset. |
| `field_remove` | remove_mode, target | — | Remove a field row; rejects when the field is referenced or attached. |
| `property_add` | name, owner_type, property_type | attributes, getter, index_parameter_types, setter | Add a property to owner_type, binding existing methods as getter/setter (at least one accessor). |
| `property_update` | target | attributes, getter, index_parameter_types, name, property_type, setter | Update an existing property in place: name, property_type, index_parameter_types, getter, setter, attributes. |
| `property_remove` | remove_mode, target | — | Remove a property row; its accessor semantics rows travel with it into the deleted-state tombstone. |
| `event_add` | add_method, event_type, name, owner_type, remove_method | attributes, raise_method | Add an event to owner_type; add_method and remove_method accessors are required, raise_method optional. |
| `event_update` | target | add_method, attributes, event_type, name, raise_method, remove_method | Update an existing event in place: name, event_type, add/remove/raise accessors, attributes. |
| `event_remove` | remove_mode, target | — | Remove an event row; its accessor semantics rows travel with it into the deleted-state tombstone. |
| `parameter_add` | name, owner_method, parameter_index, parameter_type | attributes, marshal | Append one parameter at the tail of a method (parameter_index must equal the current parameter count). |
| `parameter_update` | parameter_target | attributes, marshal, name, parameter_type | Update one parameter of a method in place: name, parameter_type, attributes, marshal. |
| `parameter_remove` | parameter_target, remove_mode | — | Remove the tail parameter of a method (only the last parameter can be removed). |
| `generic_parameter_add` | generic_index, name, owner | attributes, constraints | Append one generic parameter at the tail of a type or method (generic_index must equal the current count), optionally with constraints. |
| `generic_parameter_update` | target | attributes, constraints, name | Update a generic parameter in place: name, attributes, constraints (whole-list replacement). |
| `generic_parameter_remove` | remove_mode, target | — | Remove the tail generic parameter; rejects when it is used or attached. |
| `method_body_replace` | body, target | custom_debug_infos | Replace a method's whole CIL body: instructions, locals, exception handlers and custom debug info. |
| `attribute_add` | constructor, target | fixed_arguments, named_arguments | Add a custom attribute row to a target (constructor plus fixed/named arguments). |
| `attribute_remove` | match, target | — | Remove custom attribute rows matching a target plus constructor signature (match=all|first). |
| `security_add` | action, parent, xml | — | Add a declarative security row (action + permission-set XML) to the assembly, a type or a method. |
| `security_remove` | action, parent | index | Remove a declarative security row by parent, action and optional index. |
| `assembly_update` | — | culture, name, version | Update the assembly identity row: at least one of name, version, culture. |
| `module_update` | name | — | Rename the module row. |
| `assembly_ref_update` | target | culture, name, version | Update an AssemblyRef row: at least one of name, version, culture. |
| `entry_point_set` | — | entry_point | Set (or explicitly clear) the assembly entry point. |
| `managed_resource_add` | data_base64, name | attributes | Add an embedded managed resource (name plus inline data_base64 payload). |
| `managed_resource_update` | target | data_base64, entry | Update an embedded managed resource: whole-blob data_base64, or one resource-manager entry by name and value kind. |
| `managed_resource_remove` | remove_mode, target | — | Remove an embedded managed resource by name. |
| `win32_resource_add` | data_base64 | lang_id, name_id, name_string, type_id, type_name | Add a Win32 resource row (type/name/lang identity plus inline data_base64 payload). |
| `win32_resource_update` | data_base64 | lang_id, name_id, name_string, type_id, type_name | Replace the bytes of one Win32 resource row (type/name/lang identity). |
| `win32_resource_remove` | remove_mode | lang_id, name_id, name_string, type_id, type_name | Remove a Win32 resource row (type/name/lang identity). |
| `strong_name_remove` | dynamic_failure | — | Remove the strong-name public key; requires dynamic-failure evidence (currently returns EDIT_CAPABILITY_UNAVAILABLE). |
| `interface_add` | interface, owner_type | — | Add an InterfaceImpl row (one implemented interface) to a type. |
| `reference_add` | reference | — | Add an AssemblyRef row so the module references another assembly. |

Field-level constraints (attribute bit masks, type-signature grammar, payload limits) stay
authoritative in the server-side validation; this catalog is the navigable directory.
