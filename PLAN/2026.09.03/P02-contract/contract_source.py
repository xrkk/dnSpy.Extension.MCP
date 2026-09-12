#!/usr/bin/env python3
"""Normative, executable P02 contract source.

This file is the single source of truth for P02 wire schemas, operation lowering,
fault identities, cache/capacity rules and fingerprint mutation coverage.  Product
code does not import it; the implementation must embed the generated expanded
schemas and satisfy the generated golden data byte-for-byte.
"""

from __future__ import annotations

import copy
import hashlib
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent
GENERATED = ROOT / "generated"
DNILIB_FACTS_PATH = ROOT / "dependency" / "dnlib-4.5.0-facts.json"
DNILIB_FACTS = json.loads(DNILIB_FACTS_PATH.read_text(encoding="utf-8"))

STATES = ["idle", "editing", "reviewed", "applying", "live_state_unknown"]
PRODUCT_TOOLS = ["edit_begin", "edit_status", "edit_apply", "edit_review", "edit_rollback"]
TEST_TOOLS = ["edit_test_clock", "edit_test_barrier", "edit_test_external_mutation", "edit_test_live_mutation", "edit_test_fault", "edit_test_apply_and_restore"]
ERROR_CODES = {
    "EDIT_TRANSACTION_BUSY": "null",
    "EDIT_TRANSACTION_NOT_FOUND": "null",
    "EDIT_OWNER_REQUIRED": "null",
    "EDIT_OWNER_MISMATCH": "null",
    "EDIT_REVISION_CONFLICT": "revision_conflict",
    "EDIT_LIVE_MODULE_CONFLICT": "fingerprint_conflict",
    "EDIT_REVIEW_STALE": "null",
    "EDIT_VALIDATION_FAILED": "validation",
    "EDIT_RISK_CONFIRMATION_REQUIRED": "risk_confirmation",
    "EDIT_CAPABILITY_UNAVAILABLE": "capability",
    "EDIT_CAPACITY_EXCEEDED": "capacity",
    "EDIT_DEBUG_NOT_IDLE": "null",
    "EDIT_LIVE_STATE_UNKNOWN": "internal",
    "EDIT_CHECKPOINT_FAILED": "null",
    "EDIT_EXPORT_BLOCKED": "null",
    "EDIT_INTERNAL_ERROR": "internal",
    "REQUEST_ID_REUSE": "request_reuse",
}

LIMITS = {
    "max_operations": 256,
    "max_object_ids": 4096,
    "max_normalized_operation_bytes": 8 * 1024 * 1024,
    "max_diff_bytes": 512 * 1024,
    "max_body_instructions": 4096,
    "max_body_locals": 1024,
    "max_body_exception_handlers": 512,
    "max_live_steps": 256,
    "max_module_bytes": 16 * 1024 * 1024,
    "max_metadata_rows": 100_000,
    "max_resource_bytes": 8 * 1024 * 1024,
    "max_pdb_bytes": 8 * 1024 * 1024,
    "max_il_instructions": 250_000,
    "max_dispatcher_ms": 1000,
    "begin_cache_entries": 64,
    "begin_cache_bytes": 4 * 1024 * 1024,
    "begin_response_bytes": 1024 * 1024,
    "apply_cache_entries": 1024,
    "apply_cache_bytes": 32 * 1024 * 1024,
    "apply_response_bytes": 2 * 1024 * 1024,
    "review_slot_bytes": 5 * 1024 * 1024 // 2,
    "review_tombstone_entries": 64,
    "review_tombstone_bytes": 256 * 1024,
    "transport_response_bytes": 8 * 1024 * 1024,
    "transport_fixed_envelope_bytes": 65536,
    "transport_payload_expansion_factor": 3,
    "rollback_slot_bytes": 1024 * 1024,
}

TYPE_SIG_CONTRACT = {
    "version": "p02-typesig-v1",
    "ebnf": [
        "type = primary, { suffix } ;",
        "primary = primitive | type_ref | generic_var | generic_instance ;",
        "primitive = one of primitives ;",
        "type_ref = qualified_name, { '/', nested_name } ;",
        "generic_var = '!', uint | '!!', uint ;",
        "generic_name = type_ref ending in '`', positive_uint ;",
        "generic_instance = generic_name, '<', type, { ',', type }, '>' ; argument count must equal generic arity",
        "suffix = '[]' | '[', { ',' }, ']' | '*' | '&' ;",
    ],
    "primitives": [
        "System.Void", "System.Boolean", "System.Char", "System.SByte", "System.Byte",
        "System.Int16", "System.UInt16", "System.Int32", "System.UInt32", "System.Int64",
        "System.UInt64", "System.Single", "System.Double", "System.String", "System.Object",
        "System.IntPtr", "System.UIntPtr", "System.TypedReference",
    ],
    "qualified_name_rule": "dot-separated identifiers; nested identifiers use '/'; generic arity is a decimal suffix after backtick",
    "suffix_order": "parse left-to-right after primary; apply each suffix from inner to outer",
    "resolution": "TypeDefOrRef must resolve uniquely in the current module or an existing AssemblyRef; zero or multiple matches is EDIT_VALIDATION_FAILED",
    "context_rules": {"method_return": "naked System.Void allowed", "field_parameter_local_generic_argument": "System.Void forbidden",
                      "generic_index": "!n requires n < owner_type_arity; !!n requires n < owner_method_arity; both arities are explicit vector inputs",
                      "suffix": "zero or more array/pointer suffixes followed by at most one final byref; suffix after byref is invalid"},
    "forbidden": ["modreq", "modopt", "fnptr", "sentinel"],
    "forbidden_error": "EDIT_CAPABILITY_UNAVAILABLE",
    "valid_examples": [
        {"text": "System.Int32", "context": "field"}, {"text": "Example.Widget", "context": "field"},
        {"text": "Example.Outer/Inner", "context": "field"}, {"text": "!0", "context": "field", "owner_type_arity": 1, "owner_method_arity": 0},
        {"text": "!!12", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 13}, {"text": "System.Void", "context": "method_return"},
        {"text": "Example.Box`1<System.String>", "context": "field"},
        {"text": "Example.Map`2<System.String,System.Int32[]>", "context": "field"},
        {"text": "System.Int32[,,]", "context": "field"}, {"text": "Example.Widget*&", "context": "parameter"},
    ],
    "invalid_examples": [
        {"text": "", "reason": "empty"},
        {"text": "System.Int32 ", "reason": "whitespace"},
        {"text": "modreq(System.Int32)", "reason": "unsupported"},
        {"text": "fnptr<System.Void>", "reason": "unsupported"},
        {"text": "!", "reason": "generic-index"},
        {"text": "Example.Box`1<>", "reason": "generic-argument"},
        {"text": "Example.Box<System.String>", "reason": "missing-generic-arity"},
        {"text": "Example.Box`1<System.String,System.Int32>", "reason": "generic-arity-mismatch"},
        {"text": "System.Int32<System.String>", "reason": "primitive-generic"},
        {"text": "System.Void[]", "reason": "void-context", "context": "field"},
        {"text": "!65536", "reason": "generic-index-range", "context": "field"},
        {"text": "!0", "reason": "owner-type-arity", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0},
        {"text": "!1", "reason": "owner-type-boundary", "context": "field", "owner_type_arity": 1, "owner_method_arity": 0},
        {"text": "!!0", "reason": "owner-method-arity", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 0},
        {"text": "System.Int32&&", "reason": "byref-not-final", "context": "parameter"},
        {"text": "System.Int32&*", "reason": "suffix-after-byref", "context": "parameter"},
        {"text": "Example.Box`1<System.Void>", "reason": "void-generic-argument", "context": "field"},
        {"text": "System.Int32[,,", "reason": "unterminated-array"},
    ],
}

TYPE_SIG_REQUIRED_VECTORS = [
    {"vector_id": "type-var-zero-valid", "text": "!0", "context": "field", "owner_type_arity": 1, "owner_method_arity": 0, "expected": True},
    {"vector_id": "type-var-zero-invalid", "text": "!0", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "type-var-boundary-invalid", "text": "!1", "context": "field", "owner_type_arity": 1, "owner_method_arity": 0, "expected": False},
    {"vector_id": "method-var-zero-valid", "text": "!!0", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 1, "expected": True},
    {"vector_id": "method-var-zero-invalid", "text": "!!0", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "method-var-boundary-invalid", "text": "!!1", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 1, "expected": False},
    {"vector_id": "void-return-valid", "text": "System.Void", "context": "method_return", "owner_type_arity": 0, "owner_method_arity": 0, "expected": True},
    {"vector_id": "void-field-invalid", "text": "System.Void", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "void-generic-invalid", "text": "Example.Box`1<System.Void>", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "final-byref-valid", "text": "Example.Widget*&", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 0, "expected": True},
    {"vector_id": "double-byref-invalid", "text": "System.Int32&&", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "suffix-after-byref-invalid", "text": "System.Int32&*", "context": "parameter", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
    {"vector_id": "generic-arity-valid", "text": "Example.Map`2<System.String,System.Int32[]>", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0, "expected": True},
    {"vector_id": "generic-arity-mismatch", "text": "Example.Box`1<System.String,System.Int32>", "context": "field", "owner_type_arity": 0, "owner_method_arity": 0, "expected": False},
]
TYPE_SIG_CONTRACT["required_vectors"] = TYPE_SIG_REQUIRED_VECTORS

# Exact union of all values exposed by dnlib 4.5.0. Unknown bits are invalid.
ATTRIBUTE_DOMAINS = {
    "TypeAttributes": {"mask": 16219583, "add_default": 0},
    "MethodAttributes": {"mask": 65535, "add_default": 128},
    "MethodImplAttributes": {"mask": 6143, "add_default": 0},
    "FieldAttributes": {"mask": 47095, "add_default": 0},
    "PropertyAttributes": {"mask": 5632, "add_default": 0},
    "EventAttributes": {"mask": 1536, "add_default": 0},
    "ParamAttributes": {"mask": 12319, "add_default": 0},
    "GenericParamAttributes": {"mask": 63, "add_default": 0},
}
ATTRIBUTE_INVALID_VECTORS = {
    "TypeAttributes": [64], "MethodAttributes": [65536], "MethodImplAttributes": [2048],
    "FieldAttributes": [8], "PropertyAttributes": [1], "EventAttributes": [1],
    "ParamAttributes": [32], "GenericParamAttributes": [64],
}

CONSTANT_CONTRACT = {
    "char_pattern": r"^(?:[^\uD800-\uDFFF]|[\uD800-\uDBFF][\uDC00-\uDFFF])$",
    "float_policy": "finite IEEE-754 value only; NaN and infinities are invalid JSON input",
    "null_policy": "kind=null is permitted only for a compatible reference-type Constant; clear_constant=true removes the Constant row",
}

OPERATION_SEMANTICS = {
    "type_sig": TYPE_SIG_CONTRACT,
    "attribute_domains": ATTRIBUTE_DOMAINS,
    "attribute_invalid_vectors": ATTRIBUTE_INVALID_VECTORS,
    "constants": CONSTANT_CONTRACT,
    "add_nullability": {
        "property_add": {"getter": "omitted-or-ref; explicit-null-invalid", "setter": "omitted-or-ref; explicit-null-invalid"},
        "event_add": {"raise_method": "omitted-or-ref; explicit-null-invalid"},
    },
    "update_nullability": {
        "type_update": {"base_type": "omitted-keeps; null-clears"},
        "property_update": {"getter": "omitted-keeps; null-clears", "setter": "omitted-keeps; null-clears"},
        "event_update": {"raise_method": "omitted-keeps; null-clears"},
    },
    "accessor_rules": {
        "property": "accessors must have the same owner and compatible signature/has-this/static flags",
        "event": "add/remove are required void(T) methods with the same owner; raise is optional but must share owner and compatible flags",
    },
    "operand_dispatch": "resolve lowercase opcode through the dnlib 4.5.0 OpCodes table, then accept exactly the matching operand_kinds row; InlineNone requires omitted operand",
    "opcode_operand_table": DNILIB_FACTS["opcodes"],
    "dependency": {"name": "dnlib", "version": "4.5.0", "assembly_version": DNILIB_FACTS["assembly_version"],
                   "assembly_file_sha256": DNILIB_FACTS["assembly_file_sha256"]},
}

OPERAND_KINDS = {
    "InlineNone": {"operand": None},
    "ShortInlineI": {"kind": "i32", "minimum": -128, "maximum": 127},
    "InlineI": {"kind": "i32", "minimum": -(2**31), "maximum": 2**31 - 1},
    "InlineI8": {"kind": "i64", "minimum": -(2**63), "maximum": 2**63 - 1},
    "ShortInlineR": {"kind": "f32", "finite": True},
    "InlineR": {"kind": "f64", "finite": True},
    "InlineString": {"kind": "string", "maxLength": 65535},
    "InlineMethod": {"kind": ["token", "object"], "tables": ["MethodDef", "MemberRef", "MethodSpec"]},
    "InlineField": {"kind": ["token", "object"], "tables": ["Field", "MemberRef"]},
    "InlineType": {"kind": ["token", "object"], "tables": ["TypeDef", "TypeRef", "TypeSpec"]},
    "InlineTok": {"kind": ["token", "object"], "tables": ["TypeDef", "TypeRef", "TypeSpec", "Field", "MethodDef", "MemberRef", "MethodSpec"]},
    "ShortInlineBrTarget": {"kind": "label", "minimum": 0, "maximum": 4095},
    "InlineBrTarget": {"kind": "label", "minimum": 0, "maximum": 4095},
    "InlineSwitch": {"kind": "switch", "maxItems": 4096},
    "ShortInlineVar": {"kind": ["local", "arg"], "minimum": 0, "maximum": 255},
    "InlineVar": {"kind": ["local", "arg"], "minimum": 0, "maximum": 65535},
    "InlineSig": {"supported": False, "error": "EDIT_CAPABILITY_UNAVAILABLE"},
    "Phi": {"supported": False, "error": "EDIT_CAPABILITY_UNAVAILABLE"},
}


def op(kind: str, required: list[str], optional: list[str], private: list[str], forward: list[str], reverse: list[str], **extra: Any) -> dict[str, Any]:
    row = {"kind": kind, "required": required, "optional": optional, "private": private,
           "live_forward": forward, "live_reverse": reverse}
    row.update(extra)
    return row


OPERATIONS = [
    op("type_add", ["name"], ["namespace", "attributes", "base_type", "owner_type"],
       ["construct_type", "validate", "insert_private_type"], ["insert:type_collection"], ["remove:type_collection"]),
    op("type_update", ["target"], ["name", "namespace", "attributes", "base_type"],
       ["patch_private_type", "validate"], ["set:name:if_changed", "set:namespace:if_changed", "set:attributes:if_changed", "set:base_type:if_changed"],
       ["restore:base_type:if_changed", "restore:attributes:if_changed", "restore:namespace:if_changed", "restore:name:if_changed"]),
    op("type_remove", ["target", "remove_mode"], [], ["reject_if_referenced_or_attached", "remove_private_type", "validate"],
       ["remove:type_collection"], ["insert:type_collection"], delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("method_add", ["owner_type", "name", "signature"], ["attributes", "impl_attributes", "body"],
       ["construct_method_subgraph", "validate", "insert_private_method"], ["insert:method_collection"], ["remove:method_collection"]),
    op("method_update", ["target"], ["name", "attributes", "impl_attributes", "return_type", "has_this"],
       ["patch_private_method", "validate"], ["set:name:if_changed", "set:attributes:if_changed", "set:impl_attributes:if_changed", "set:return_type:if_changed", "set:has_this:if_changed"],
       ["restore:has_this:if_changed", "restore:return_type:if_changed", "restore:impl_attributes:if_changed", "restore:attributes:if_changed", "restore:name:if_changed"]),
    op("method_remove", ["target", "remove_mode"], [], ["reject_if_referenced_or_attached", "remove_private_method", "validate"],
       ["remove:method_collection"], ["insert:method_collection"], delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("field_add", ["owner_type", "name", "field_type"], ["attributes", "constant"],
       ["construct_field", "validate", "insert_private_field"], ["insert:field_collection"], ["remove:field_collection"]),
    op("field_update", ["target"], ["name", "field_type", "attributes", "constant", "clear_constant"],
       ["patch_private_field", "validate"], ["set:name:if_changed", "set:field_type:if_changed", "set:attributes:if_changed", "set:constant:if_changed"],
       ["restore:constant:if_changed", "restore:attributes:if_changed", "restore:field_type:if_changed", "restore:name:if_changed"]),
    op("field_remove", ["target", "remove_mode"], [], ["reject_if_referenced_or_attached", "remove_private_field", "validate"],
       ["remove:field_collection"], ["insert:field_collection"], delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("property_add", ["owner_type", "name", "property_type"], ["index_parameter_types", "attributes", "getter", "setter"],
       ["construct_property", "validate", "insert_private_property", "link_private_accessors"],
       ["insert:property_collection", "insert:getter_semantics:if_present", "insert:setter_semantics:if_present"],
       ["remove:setter_semantics:if_present", "remove:getter_semantics:if_present", "remove:property_collection"]),
    op("property_update", ["target"], ["name", "property_type", "index_parameter_types", "attributes", "getter", "setter"],
       ["patch_private_property", "validate"], ["set:name:if_changed", "set:property_type:if_changed", "set:index_signature:if_changed", "set:attributes:if_changed", "set:getter_semantics:if_changed", "set:setter_semantics:if_changed"],
       ["restore:setter_semantics:if_changed", "restore:getter_semantics:if_changed", "restore:attributes:if_changed", "restore:index_signature:if_changed", "restore:property_type:if_changed", "restore:name:if_changed"]),
    op("property_remove", ["target", "remove_mode"], [], ["reject_if_referenced_or_attached", "unlink_private_accessors", "remove_private_property", "validate"],
       ["remove:setter_semantics:if_present", "remove:getter_semantics:if_present", "remove:property_collection"],
       ["insert:property_collection", "insert:getter_semantics:if_present", "insert:setter_semantics:if_present"], delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("event_add", ["owner_type", "name", "event_type", "add_method", "remove_method"], ["attributes", "raise_method"],
       ["construct_event", "validate", "insert_private_event", "link_private_accessors"],
       ["insert:event_collection", "insert:add_semantics", "insert:remove_semantics", "insert:raise_semantics:if_present"],
       ["remove:raise_semantics:if_present", "remove:remove_semantics", "remove:add_semantics", "remove:event_collection"]),
    op("event_update", ["target"], ["name", "event_type", "attributes", "add_method", "remove_method", "raise_method"],
       ["patch_private_event", "validate"], ["set:name:if_changed", "set:event_type:if_changed", "set:attributes:if_changed", "set:add_semantics:if_changed", "set:remove_semantics:if_changed", "set:raise_semantics:if_changed"],
       ["restore:raise_semantics:if_changed", "restore:remove_semantics:if_changed", "restore:add_semantics:if_changed", "restore:attributes:if_changed", "restore:event_type:if_changed", "restore:name:if_changed"]),
    op("event_remove", ["target", "remove_mode"], [], ["reject_if_referenced_or_attached", "unlink_private_accessors", "remove_private_event", "validate"],
       ["remove:raise_semantics:if_present", "remove:remove_semantics", "remove:add_semantics", "remove:event_collection"],
       ["insert:event_collection", "insert:add_semantics", "insert:remove_semantics", "insert:raise_semantics:if_present"], delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("parameter_add", ["owner_method", "parameter_index", "name", "parameter_type"], ["attributes"],
       ["require_tail", "construct_paramdef", "append_private_signature_slot", "append_private_paramdef", "validate"],
       ["insert:signature_tail", "insert:paramdef_collection"], ["remove:paramdef_collection", "remove:signature_tail"]),
    op("parameter_update", ["parameter_target"], ["name", "attributes", "parameter_type"],
       ["reject_return_param", "materialize_private_paramdef_if_missing", "patch_private_parameter", "validate"],
       ["insert:paramdef_collection:if_missing", "set:name:if_changed", "set:attributes:if_changed", "set:signature_slot_type:if_changed"],
       ["restore:signature_slot_type:if_changed", "restore:attributes:if_changed", "restore:name:if_changed", "remove:paramdef_collection:if_materialized"]),
    op("parameter_remove", ["parameter_target", "remove_mode"], [],
       ["require_tail", "reject_return_param", "reject_if_referenced_or_attached", "remove_private_paramdef_if_present", "remove_private_signature_slot", "validate"],
       ["remove:paramdef_collection:if_present", "remove:signature_tail"], ["insert:signature_tail", "insert:paramdef_collection:if_was_present"],
       delete_policy="hard_fail_if_referenced;public_unreferenced_is_risk"),
    op("generic_parameter_add", ["owner", "generic_index", "name"], ["attributes"],
       ["require_tail", "construct_generic_param", "append_private_generic", "validate"],
       ["insert:generic_collection", "set:method_generic_arity:if_method"], ["restore:method_generic_arity:if_method", "remove:generic_collection"]),
    op("generic_parameter_update", ["target"], ["name", "attributes"], ["patch_private_generic", "validate"],
       ["set:name:if_changed", "set:attributes:if_changed"], ["restore:attributes:if_changed", "restore:name:if_changed"]),
    op("generic_parameter_remove", ["target", "remove_mode"], [], ["require_tail", "reject_if_used_or_attached", "remove_private_generic", "validate"],
       ["remove:generic_collection", "set:method_generic_arity:if_method"], ["restore:method_generic_arity:if_method", "insert:generic_collection"],
       delete_policy="hard_fail_if_used_or_attached;public_unreferenced_is_risk"),
    op("method_body_replace", ["target", "body"], [], ["construct_body", "validate", "swap_private_body"],
       ["set:body_reference"], ["restore:body_reference"]),
    # CHK-009 / P04+P07+P08 single-source migration: the 15 advanced operation
    # kinds previously appended to generated/operation-lowering.json by the
    # later phase sub-plans are ported into this generator so regeneration is
    # byte-faithful again.  The P02 machine fault suite and the P02 tool-schema
    # branches stay frozen at the first 22 kinds (MACHINE_SUITE_OPERATION_COUNT);
    # the advanced kinds are exercised by their own phase harnesses
    # (P03StoreHarness advanced-metadata / identity / resource matrices).
    op("attribute_add", ["target", "constructor"], ["fixed_arguments", "named_arguments"],
       ["resolve_constructor", "validate_allow_multiple", "insert_attribute"], ["insert:custom_attribute_collection"], ["remove:custom_attribute_by_constructor_index"]),
    op("attribute_remove", ["target", "match"], [],
       ["resolve_constructor", "capture_row"], ["remove:custom_attribute_by_constructor_index"], ["restore:captured_attribute"],
       delete_policy="hard_fail_if_row_not_found"),
    op("security_add", ["parent", "action", "xml"], [],
       ["xml_to_blob_spike_path", "insert_row"], ["insert:decl_security_collection"], ["remove:decl_security_by_action_index"]),
    op("security_remove", ["parent", "action"], ["index"],
       ["resolve_parent", "capture_row"], ["remove:decl_security_by_action_index"], ["restore:captured_decl_security_xml"],
       delete_policy="hard_fail_if_row_not_found"),
    op("assembly_update", [], ["name", "version", "culture"],
       ["resolve_assembly_row", "capture_old_values"], ["write:assembly_name_version_culture"], ["restore:captured_assembly_identity"]),
    op("module_update", ["name"], [],
       ["capture_old_module_name"], ["write:module_name"], ["restore:captured_module_name"]),
    op("assembly_ref_update", ["target"], ["name", "version", "culture"],
       ["resolve_assembly_ref_row", "capture_old_values"], ["write:assembly_ref_identity_fields"], ["restore:captured_assembly_ref_identity"]),
    op("entry_point_set", [], ["entry_point"],
       ["resolve_entry_method_in_module", "capture_old_entry"], ["write:managed_entry_point"], ["restore:captured_entry_point"]),
    op("managed_resource_add", ["name", "data_base64"], ["attributes"],
       ["decode_base64", "capacity_check"], ["add:embedded_resource_row"], ["remove:captured_row"]),
    op("managed_resource_update", ["target"], ["entry", "data_base64"],
       ["parse_resources_blob", "surgical_entry_rebuild"], ["write:resource_bytes"], ["restore:captured_bytes"]),
    op("managed_resource_remove", ["target", "remove_mode"], [],
       ["resolve_resource_row", "capture_bytes"], ["remove:resource_row"], ["restore:captured_row"],
       delete_policy="hard_fail_if_row_not_found"),
    op("win32_resource_add", ["data_base64"], ["type_id", "type_name", "name_id", "name_string", "lang_id"],
       ["decode_base64", "directory_tree_create"], ["add:win32_resource_row"], ["remove:row_and_prune"]),
    op("win32_resource_update", ["data_base64"], ["type_id", "type_name", "name_id", "name_string", "lang_id"],
       ["decode_base64", "icon_group_validate"], ["swap:win32_resource_row"], ["restore:captured_row"]),
    op("win32_resource_remove", ["remove_mode"], ["type_id", "type_name", "name_id", "name_string", "lang_id"],
       ["resolve_row", "icon_group_validate"], ["remove:win32_resource_row"], ["restore:captured_row"],
       delete_policy="hard_fail_if_dangling_icon_group"),
    op("strong_name_remove", ["dynamic_failure"], [],
       ["validate_one_time_evidence", "capture_public_key"], ["clear:public_key_and_flag"], ["restore:captured_key_and_attributes"],
       delete_policy="hard_fail_if_dynamic_evidence_not_consumed;removal_is_risk"),
]

# The frozen P02 machine suite: the P02 tool schemas, acceptance-case map and
# fault oracle matrix cover exactly the first 22 kinds; later kinds lower in
# operation-lowering.json (the single registry table) but are gated by their
# own phase harnesses (P03StoreHarness advanced-metadata / identity / resource
# matrices) and the P03 wire schema.
MACHINE_SUITE_OPERATION_COUNT = 22
SCHEMA_OPERATIONS = OPERATIONS[:MACHINE_SUITE_OPERATION_COUNT]

FINGERPRINT_CHANNELS = {
    "ModuleMetadata": {
        "category": "metadata", "fixture": "FingerprintFixture", "live_target": "ModuleDef.Name",
        "mutate_action": {"primitive": "set-dnlib-string-property", "property": "ModuleDef.Name", "value": "FingerprintFixture.changed.dll"},
        "readback": {"primitive": "read-dnlib-string-property", "property": "ModuleDef.Name"},
        "restore_action": {"primitive": "restore-captured-dnlib-property", "property": "ModuleDef.Name"}},
    "DnlibObjectGraph": {
        "category": "dnlib_object", "fixture": "FingerprintFixture", "live_target": "TypeDef.Name",
        "mutate_action": {"primitive": "set-dnlib-string-property", "selector": "type-token:0x02000002", "property": "TypeDef.Name", "value": "FingerprintTypeChanged"},
        "readback": {"primitive": "read-dnlib-string-property", "selector": "type-token:0x02000002", "property": "TypeDef.Name"},
        "restore_action": {"primitive": "restore-captured-dnlib-property", "selector": "type-token:0x02000002", "property": "TypeDef.Name"}},
    "MethodBodyIl": {
        "category": "il", "fixture": "FingerprintFixture", "live_target": "MethodDef.Body.Instructions[0].OpCode",
        "mutate_action": {"primitive": "set-dnlib-opcode", "selector": "method-token:0x06000001", "instruction_index": 0, "value": "nop"},
        "readback": {"primitive": "read-dnlib-opcode", "selector": "method-token:0x06000001", "instruction_index": 0},
        "restore_action": {"primitive": "restore-captured-dnlib-opcode", "selector": "method-token:0x06000001", "instruction_index": 0}},
    "ManagedResource": {
        "category": "resource", "fixture": "FingerprintFixture", "live_target": "ModuleDef.Resources",
        "mutate_action": {"primitive": "replace-dnlib-embedded-resource", "resource_name": "fingerprint.resource", "utf8_value": "changed"},
        "readback": {"primitive": "sha256-dnlib-embedded-resource", "resource_name": "fingerprint.resource"},
        "restore_action": {"primitive": "restore-captured-dnlib-resource", "resource_name": "fingerprint.resource"}},
    "EmbeddedPdb": {
        "category": "pdb", "fixture": "FingerprintFixtureEmbeddedPdb",
        "live_target": DNILIB_FACTS["public_api_paths"]["sequence_point_document_url"],
        "mutate_action": {"primitive": "set-dnlib-pdb-document-url", "selector": "method-token:0x06000001:first-instruction-with-sequence-point", "value": "fingerprint-changed.cs"},
        "readback": {"primitive": "read-dnlib-pdb-document-url", "selector": "method-token:0x06000001:first-instruction-with-sequence-point"},
        "restore_action": {"primitive": "restore-captured-dnlib-pdb-document-url", "selector": "method-token:0x06000001:first-instruction-with-sequence-point"}},
}

METADATA_COMPONENTS = list(FINGERPRINT_CHANNELS)


def component_recipe(component: str) -> dict[str, Any]:
    row = FINGERPRINT_CHANNELS[component]
    return {
        "recipe_id": f"fp-channel-{component.lower()}", "component": component, "category": row["category"],
        "fixture_precondition": {"fixture": row["fixture"], "live_target_resolves": True},
        "live_target": row["live_target"], "mutate_action": row["mutate_action"], "readback": row["readback"],
        "restore_action": {**row["restore_action"], "verify_full_fingerprint": True},
    }


COMPONENT_RECIPES = [component_recipe(name) for name in METADATA_COMPONENTS]
CANONICAL_ORDER_RECIPE = {
    "case_id": "fp-canonical-global-order:reorder", "mode": "reorder", "expected_fingerprint_change": False,
    "action": {"primitive": "reverse-global-fingerprint-channel-enumerator", "minimum_channels": 5},
    "recipe_id": "fp-canonical-global-order", "component": "GlobalCanonicalOrder", "category": "canonicalization",
    "fixture_precondition": {"fixture": "FingerprintFixtureEmbeddedPdb", "channel_count_minimum": 5},
    "live_target": "EditFingerprint.channel-enumerator", "mutate_action": {"primitive": "none"},
    "readback": {"primitive": "sha256-raw-and-canonical-channel-projection"},
    "restore_action": {"primitive": "restore-channel-enumerator", "verify_full_fingerprint": True},
    "reorder_action": {"primitive": "reverse-global-fingerprint-channel-enumerator", "minimum_channels": 5},
}

CACHE_TRANSITIONS = [
    {"from": "idle", "event": "begin(new-id)", "to": "editing", "cache": "session_begin_tombstone"},
    {"from": "editing|reviewed", "event": "apply(new-id,no-lease)", "to": "editing", "cache": "transaction_apply"},
    {"from": "editing|reviewed", "event": "apply(new-id,lease-held)", "to": "same", "error": "EDIT_TRANSACTION_BUSY"},
    {"from": "editing|reviewed", "event": "review(new-id,no-lease)", "to": "reviewed", "cache": "single_current_review"},
    {"from": "reviewed", "event": "apply-success", "to": "editing", "cache": "replace_full_review_with_hash_tombstone"},
    {"from": "editing|reviewed|applying", "event": "rollback(new-id)", "to": "idle", "cache": "session_terminal_slot", "action": "cancel_lease_then_wait_outside_monitor"},
    {"from": "any-active", "event": "close|timeout", "to": "idle", "cache": "release_transaction_cache"},
    {"from": "idle", "event": "old-begin-id", "to": "idle", "cache": "replay_original_begin_response"},
    {"from": "idle|new-transaction", "event": "old-transaction-id", "to": "same", "error": "EDIT_TRANSACTION_NOT_FOUND"},
]

REVIEW_LIFECYCLE = [
    {"transition_id": "review-first", "from": ["editing"], "event": "review", "request_class": "new",
     "guard": {"tombstones_less_than": LIMITS["review_tombstone_entries"]}, "to": "reviewed",
     "full_slots_after": 1, "tombstones_delta": 0, "result": "store-full-current-review"},
    {"transition_id": "review-new", "from": ["reviewed"], "event": "review", "request_class": "new",
     "guard": {"tombstones_less_than": LIMITS["review_tombstone_entries"]}, "to": "reviewed",
     "full_slots_after": 1, "tombstones_delta": 1,
     "result": "replace-full-current-review-and-store-prior-request-tombstone"},
    {"transition_id": "review-replay-old", "from": ["reviewed"], "event": "review", "request_class": "same-old-id-and-payload",
     "guard": {"tombstone_present": True}, "to": "reviewed", "full_slots_after": 1,
     "tombstones_delta": 0, "result": "reconstruct-byte-identical-response-from-current-revision-and-tombstone"},
    {"transition_id": "review-capacity", "from": ["reviewed"], "event": "review", "request_class": "new",
     "guard": {"tombstones_equal": LIMITS["review_tombstone_entries"]}, "to": "reviewed",
     "full_slots_after": 1, "tombstones_after": LIMITS["review_tombstone_entries"],
     "result": "EDIT_CAPACITY_EXCEEDED", "side_effect": "none"},
    {"transition_id": "review-apply-clear", "from": ["reviewed"], "event": "apply-success", "request_class": "terminal",
     "guard": {}, "to": "editing", "full_slots_after": 0, "tombstones_after": 0,
     "result": "clear-full-review-and-all-review-tombstones"},
    {"transition_id": "review-terminal-clear", "from": ["editing", "reviewed"], "event": "rollback|close|timeout",
     "request_class": "terminal", "guard": {}, "to": "idle", "full_slots_after": 0,
     "tombstones_after": 0, "result": "clear-review-capacity"},
]


def acceptance_case(case_id: str, action: str, expected: str, evidence: list[str]) -> dict[str, Any]:
    return {"case_id": case_id, "action": action, "expected": expected, "evidence": evidence}


ACCEPTANCE_CASES = {
    "RACC-001": [
        acceptance_case("private-live-isolation", "apply private operation", "live fingerprint unchanged", ["private_before_after", "live_before_after"]),
        acceptance_case("single-active-transaction", "second session begin", "EDIT_TRANSACTION_BUSY", ["two_session_transcript", "final_idle"]),
        acceptance_case("same-request-replay", "repeat same request ID and payload", "byte-identical response without second mutation", ["responses", "revision"]),
        acceptance_case("changed-request-reuse", "repeat request ID with changed payload", "REQUEST_ID_REUSE", ["responses", "fingerprints"]),
        acceptance_case("external-live-conflict", "mutate live after begin", "EDIT_LIVE_MODULE_CONFLICT and external edit preserved", ["component_readback", "fingerprints"]),
        acceptance_case("session-owner-enforced", "plain HTTP or other session operates transaction", "owner error and zero side effect", ["transport_kind", "responses", "fingerprints"]),
    ],
    "RACC-002": [
        acceptance_case("delete-session", "DELETE owning session", "transaction/cache released", ["session", "transaction", "cache", "idle"]),
        acceptance_case("timeout-599999", "advance test clock to 599999 ms", "transaction remains", ["clock", "status"]),
        acceptance_case("timeout-600000", "advance test clock to 600000 ms", "transaction rolled back", ["clock", "status", "idle"]),
        acceptance_case("listener-restart", "apply configuration and restart listener", "AI transaction rolled back and client reconnects explicitly", ["old_new_session", "idle"]),
        acceptance_case("stdio-same-url", "reconnect bridge to unchanged URL", "new authoritative session", ["old_new_session"]),
        acceptance_case("port-explicit-change", "change 15378 to 15379", "old client stops; only explicit new URL resumes", ["old_new_url", "transcript"]),
        acceptance_case("no-port-scan", "observe changed endpoint", "no scanning or guessing", ["network_transcript"]),
        acceptance_case("no-payload-replay", "reconnect after endpoint change", "no old mutation ID or payload replay", ["request_transcript"]),
        acceptance_case("waiter-lock-free", "close with follower on waiter", "continuation can status/begin/Dispatcher without inline reentry", ["timeline", "dispatcher_probe"]),
        acceptance_case("rollback-cancels-lease", "rollback races in-flight apply", "rollback wins and apply is not found", ["lease_timeline", "idle"]),
        acceptance_case("close-generation-isolation", "old generation close races new begin", "new transaction survives", ["generation_timeline"]),
        acceptance_case("capacity-release", "close/timeout/restart", "all session and cache slots released", ["capacity_before_after"]),
    ],
    "RACC-003": [
        *[acceptance_case(f"operation-{kind}", f"apply/review/test-restore {kind}", "positive and structural negative both match golden",
                          ["request_response", "private_reload", "live_restore"]) for kind in [row["kind"] for row in SCHEMA_OPERATIONS]],
        acceptance_case("typesig-vectors", "run all TypeSig contexts/vectors", "all accept/reject/error outcomes match", ["vector_results"]),
        acceptance_case("attribute-mask-vectors", "run valid boundaries and each sparse invalid bit", "invalid bit is -32602", ["vector_results"]),
        acceptance_case("constant-full-domain", "run integer/finite-float/char/null boundaries including UInt64 max", "all legal values accepted and illegal rejected", ["vector_results"]),
        acceptance_case("accessor-nullability", "run property/event add and update null cases", "add null rejected; update null clears only nullable accessor", ["responses", "reload"]),
        acceptance_case("opcode-operand-table", "run every pinned dnlib opcode row plus mismatched operand", "matching row accepted; mismatch -32602", ["opcode_matrix"]),
    ],
    "RACC-009": [
        acceptance_case("fault-suite", "one new transaction per 240 generated faults", "per-call prefix/single coverage and external union exactly satisfy suite contract", ["suite_index", "240_call_artifacts"]),
        acceptance_case("component-suite", "run five exact live dnlib fingerprint-channel mutations plus one global canonical-order perturbation", "each named live target changes its independent readback and full fingerprint then restores; global raw order changes while canonical fingerprint does not", ["six_evidence_artifacts"]),
        acceptance_case("review-wire-budget", "materialize every maximal output branch", "full JSON-RPC strict UTF-8 <= 8388608", ["payload_and_wire_bytes"]),
        acceptance_case("review-lifecycle", "first/new/old/apply-success review sequence", "one full slot and bounded tombstone behavior", ["capacity_timeline"]),
        acceptance_case("cache-boundaries", "boundary/over-one for begin/apply/review/rollback", "over-limit zero side effect and cleanup restores capacity", ["capacity_matrix"]),
        acceptance_case("reverse-failure", "inject reverse failure", "only live_state_unknown; all future edit paths blocked until test cleanup", ["fault_evidence", "state_timeline"]),
    ],
    "RACC-010": [
        acceptance_case("review-stale", "apply after review then use old review", "EDIT_REVIEW_STALE", ["responses", "revision"]),
        acceptance_case("review-live-conflict", "external live edit after review", "EDIT_LIVE_MODULE_CONFLICT", ["readback", "fingerprints"]),
        acceptance_case("review-debug-not-idle", "start debugger before test apply", "EDIT_DEBUG_NOT_IDLE", ["debug_events", "response"]),
        acceptance_case("review-risk-confirmation", "omit one required risk", "EDIT_RISK_CONFIRMATION_REQUIRED", ["risk_ids", "response"]),
        *[acceptance_case(f"dynamic-{state}", f"execute dynamic {state} fixture", f"only the {state} envelope branch validates",
                          ["events", "artifact", "cleanup", "response_schema"]) for state in ["not_requested", "not_applicable", "blocked", "passed", "failed"]],
    ],
    "RACC-026": [
        *[acceptance_case(f"unsupported-{kind}", f"analyze then begin {kind} fixture", "analysis succeeds; begin EDIT_CAPABILITY_UNAVAILABLE; zero side effect",
                          ["analysis_response", "begin_response", "fingerprints", "idle"]) for kind in ["mixed-mode", "netmodule", "multi-file"]],
        acceptance_case("raw-edit-fields", "send raw_metadata/pe_bytes/heap/rva/hex_patch", "-32602 and no raw-edit tool", ["tools_schema", "responses", "fingerprints"]),
    ],
}


def s_string(max_len: int = 4096, pattern: str | None = None, enum: list[str] | None = None) -> dict[str, Any]:
    out: dict[str, Any] = {"type": "string", "minLength": 1, "maxLength": max_len}
    if pattern: out["pattern"] = pattern
    if enum is not None: out["enum"] = enum
    return out


def obj(properties: dict[str, Any], required: list[str] | None = None, one_of: list[dict[str, Any]] | None = None) -> dict[str, Any]:
    out: dict[str, Any] = {"type": "object", "properties": properties, "required": required if required is not None else list(properties), "additionalProperties": False}
    if one_of: out["oneOf"] = one_of
    return out


def nullable(schema: dict[str, Any]) -> dict[str, Any]:
    return {"oneOf": [schema, {"type": "null"}]}


TOKEN = s_string(10, r"^0x[0-9a-fA-F]{8}$")
SHA = s_string(64, r"^[0-9a-f]{64}$")
ID = s_string(128)
UINT32 = {"type": "integer", "minimum": 0, "maximum": 2**32 - 1}
UINT64 = {"type": "integer", "minimum": 0, "maximum": 2**63 - 1}
REF = {"oneOf": [obj({"token": TOKEN}), obj({"object_id": ID})]}
PARAM_TARGET = {"oneOf": [REF, obj({"owner_method": REF, "parameter_index": UINT32})]}
REVIEW_TOMBSTONE = obj({"request_id_sha256": SHA, "request_payload_sha256": SHA,
                        "review_id": ID, "review_revision": UINT32})


def tagged_constant() -> dict[str, Any]:
    branches = []
    ranges = {"i1": (-128, 127), "u1": (0, 255), "i2": (-32768, 32767), "u2": (0, 65535),
              "i4": (-(2**31), 2**31-1), "u4": (0, 2**32-1), "i8": (-(2**63), 2**63-1), "u8": (0, 2**64-1)}
    for kind, (lo, hi) in ranges.items():
        branches.append(obj({"kind": {"const": kind}, "value": {"type": "integer", "minimum": lo, "maximum": hi}}))
    branches.append(obj({"kind": {"const": "r4"}, "value": {"type": "number", "minimum": -3.4028234663852886e38, "maximum": 3.4028234663852886e38}}))
    branches.append(obj({"kind": {"const": "r8"}, "value": {"type": "number", "minimum": -1.7976931348623157e308, "maximum": 1.7976931348623157e308}}))
    branches += [obj({"kind": {"const": "null"}, "value": {"type": "null"}}),
                 obj({"kind": {"const": "boolean"}, "value": {"type": "boolean"}}),
                 obj({"kind": {"const": "char"}, "value": {"type": "string", "minLength": 1, "maxLength": 2,
                                                                  "pattern": CONSTANT_CONTRACT["char_pattern"]}}),
                 obj({"kind": {"const": "string"}, "value": {"type": "string", "maxLength": 65535}})]
    return {"oneOf": branches}


def operand_schema() -> dict[str, Any]:
    return {"oneOf": [
        obj({"kind": {"const": "i32"}, "value": {"type": "integer", "minimum": -(2**31), "maximum": 2**31-1}}),
        obj({"kind": {"const": "i64"}, "value": {"type": "integer", "minimum": -(2**63), "maximum": 2**63-1}}),
        obj({"kind": {"const": "f32"}, "value": {"type": "number", "minimum": -3.4028234663852886e38, "maximum": 3.4028234663852886e38}}),
        obj({"kind": {"const": "f64"}, "value": {"type": "number", "minimum": -1.7976931348623157e308, "maximum": 1.7976931348623157e308}}),
        obj({"kind": {"const": "string"}, "value": {"type": "string", "maxLength": 65535}}),
        obj({"kind": {"const": "token"}, "token": TOKEN}), obj({"kind": {"const": "object"}, "object_id": ID}),
        obj({"kind": {"const": "label"}, "instruction_index": {"type": "integer", "minimum": 0, "maximum": 4095}}),
        obj({"kind": {"const": "switch"}, "instruction_indices": {"type": "array", "items": UINT32, "maxItems": 4096}}),
        obj({"kind": {"const": "local"}, "local_index": {"type": "integer", "minimum": 0, "maximum": 65535}}),
        obj({"kind": {"const": "arg"}, "argument_index": {"type": "integer", "minimum": 0, "maximum": 65535}}),
    ]}


TYPE_SIG = {**s_string(4096), "x-dnspy-contract": TYPE_SIG_CONTRACT["version"]}


def attribute_schema(domain: str, *, add: bool) -> dict[str, Any]:
    spec = ATTRIBUTE_DOMAINS[domain]
    schema: dict[str, Any] = {"type": "integer", "minimum": 0, "maximum": spec["mask"],
                              "x-dnspy-defined-bit-mask": spec["mask"], "x-dnspy-enum": domain}
    if add:
        schema["default"] = spec["add_default"]
    return schema


ATTRIBUTE_DOMAIN_BY_KIND = {
    "type": "TypeAttributes", "method": "MethodAttributes", "field": "FieldAttributes",
    "property": "PropertyAttributes", "event": "EventAttributes", "parameter": "ParamAttributes",
    "generic_parameter": "GenericParamAttributes",
}
BODY = obj({
    "init_locals": {"type": "boolean"}, "max_stack": {"type": "integer", "minimum": 0, "maximum": 65535},
    "instructions": {"type": "array", "maxItems": LIMITS["max_body_instructions"], "items": obj({"opcode": s_string(64), "operand": nullable(operand_schema())}, ["opcode"])},
    "locals": {"type": "array", "maxItems": LIMITS["max_body_locals"], "items": obj({"type": TYPE_SIG, "name": nullable(s_string(512))})},
    "exception_handlers": {"type": "array", "maxItems": LIMITS["max_body_exception_handlers"], "items": obj({
        "kind": {"enum": ["catch", "finally", "fault", "filter"]}, "try_start": UINT32, "try_end": UINT32,
        "handler_start": UINT32, "handler_end": UINT32, "filter_start": nullable(UINT32), "catch_type": nullable(TYPE_SIG)
    })}
})

FIELD_TYPES: dict[str, Any] = {
    "name": s_string(512), "namespace": {"type": "string", "maxLength": 512}, "attributes": UINT32,
    "impl_attributes": UINT32, "base_type": nullable(TYPE_SIG), "field_type": TYPE_SIG, "property_type": TYPE_SIG,
    "event_type": TYPE_SIG, "parameter_type": TYPE_SIG, "return_type": TYPE_SIG, "has_this": {"type": "boolean"},
    "target": REF, "owner": REF, "owner_type": REF, "owner_method": REF, "parameter_target": PARAM_TARGET,
    "parameter_index": UINT32, "generic_index": UINT32, "remove_mode": {"const": "reject_if_referenced"},
    "constant": tagged_constant(), "clear_constant": {"const": True}, "getter": nullable(REF), "setter": nullable(REF),
    "add_method": REF, "remove_method": REF, "raise_method": nullable(REF),
    "index_parameter_types": {"type": "array", "items": TYPE_SIG, "maxItems": 64}, "body": BODY,
    "signature": obj({
        "return_type": TYPE_SIG,
        "parameters": {"type": "array", "maxItems": 256, "items": obj({"type": TYPE_SIG, "name": nullable(s_string(512)),
                                                                           "attributes": attribute_schema("ParamAttributes", add=True)}, ["type"])},
        "has_this": {"type": "boolean"},
        "generic_parameters": {"type": "array", "maxItems": 64, "items": obj({"name": s_string(512),
                                                                                   "attributes": attribute_schema("GenericParamAttributes", add=True)}, ["name"])},
    }),
}


def operation_schema(row: dict[str, Any]) -> dict[str, Any]:
    props = {"kind": {"const": row["kind"]}}
    for name in row["required"] + row["optional"]:
        if name == "attributes":
            prefix = row["kind"].rsplit("_", 1)[0]
            props[name] = attribute_schema(ATTRIBUTE_DOMAIN_BY_KIND[prefix], add=row["kind"].endswith("_add"))
        elif name == "impl_attributes":
            props[name] = attribute_schema("MethodImplAttributes", add=row["kind"] == "method_add")
        else:
            props[name] = copy.deepcopy(FIELD_TYPES[name])
    if row["kind"] == "property_add":
        for name in ("getter", "setter"):
            if name in props:
                props[name] = copy.deepcopy(REF)
    if row["kind"] == "event_add" and "raise_method" in props:
        props["raise_method"] = copy.deepcopy(REF)
    required = ["kind"] + row["required"]
    schema = obj(props, required)
    if row["kind"].endswith("_update"):
        schema["minProperties"] = len(required) + 1
    if "constant" in props and "clear_constant" in props:
        schema["allOf"] = [{"not": {"required": ["constant", "clear_constant"]}}]
    return schema


OPERATION_SCHEMA = {"oneOf": [operation_schema(row) for row in SCHEMA_OPERATIONS]}


def validation_error_schema() -> dict[str, Any]:
    return obj({"rule_id": s_string(48), "object": s_string(48), "location": s_string(48), "message": s_string(192)})


DETAIL_SCHEMAS = {
    "validation": obj({"kind": {"const": "validation"}, "errors": {"type": "array", "items": validation_error_schema(), "maxItems": 64}}),
    "capacity": obj({"kind": {"const": "capacity"}, "limit": s_string(128), "current": UINT64, "maximum": UINT64}),
    "revision_conflict": obj({"kind": {"const": "revision_conflict"}, "expected": UINT32, "actual": UINT32}),
    "fingerprint_conflict": obj({"kind": {"const": "fingerprint_conflict"}, "expected": SHA, "actual": SHA}),
    "request_reuse": obj({"kind": {"const": "request_reuse"}, "request_id": ID}),
    "risk_confirmation": obj({"kind": {"const": "risk_confirmation"}, "missing_risk_ids": {"type": "array", "items": ID, "maxItems": 256}}),
    "capability": obj({"kind": {"const": "capability"}, "capability": s_string(128), "reason": s_string(1024)}),
    "internal": obj({"kind": {"const": "internal"}, "correlation_id": ID}),
}


def error_schema(code: str) -> dict[str, Any]:
    detail = ERROR_CODES[code]
    details = {"type": "null"} if detail == "null" else DETAIL_SCHEMAS[detail]
    return obj({"code": {"const": code}, "message": s_string(4096), "current_state": {"enum": STATES}, "recovery": s_string(4096), "details": details})


ERROR_UNION = {"oneOf": [error_schema(code) for code in ERROR_CODES]}


SOURCE = obj({"assembly_name": s_string(512), "module_name": s_string(512), "mvid": s_string(36, r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"),
              "file_path": nullable(s_string(32767)), "file_sha256": nullable(SHA), "live_fingerprint": SHA})
TRANSACTION = obj({"transaction_id": ID, "work_revision": UINT32, "review_revision": nullable(UINT32), "operation_count": UINT32,
                   "started_at_monotonic_ms": UINT64, "last_activity_monotonic_ms": UINT64})
FINGERPRINTS = obj({"baseline_live": SHA, "current_live": SHA, "private": SHA})
LIMITS_SCHEMA = obj({key: {"const": value} for key, value in LIMITS.items() if key.startswith("max_")})
COUNTER = obj({"current": UINT64, "maximum": UINT64})
CAPACITY = obj({name: COUNTER for name in ["operations", "object_ids", "normalized_operation_bytes", "diff_bytes", "apply_cache_entries", "apply_cache_bytes"]})
RISK_ID = s_string(48)
RISK = obj({"risk_id": RISK_ID, "kind": {"enum": ["public_delete", "signature_change", "visibility_change", "body_change", "eh_change"]},
            "object": s_string(32), "description": s_string(96), "confirmation_required": {"type": "boolean"}})
DIFF = obj({"operation_index": UINT32, "kind": {"enum": [row["kind"] for row in SCHEMA_OPERATIONS]}, "target": s_string(32), "path": s_string(48),
            "before": nullable(s_string(32)), "after": nullable(s_string(32)),
            "risk_ids": {"type": "array", "items": RISK_ID, "maxItems": 1}})
VALIDATION = obj({"state": {"enum": ["passed", "failed"]}, "rule_count": UINT32, "errors": {"type": "array", "items": validation_error_schema(), "maxItems": 64}})
REVIEW_SUMMARY = obj({"review_id": ID, "review_revision": UINT32, "required_confirmation_ids": {"type": "array", "items": RISK_ID, "maxItems": 256}})


ARTIFACT_PRESENT = obj({"created": {"const": True}, "path": s_string(4096), "sha256": SHA})
ARTIFACT_ABSENT = obj({"created": {"const": False}, "path": {"type": "null"}, "sha256": {"type": "null"}})
DYNAMIC_EVENT = obj({"sequence": UINT32, "kind": s_string(64), "summary": s_string(256)})
EMPTY_EVENTS = {"type": "array", "items": DYNAMIC_EVENT, "maxItems": 0}
NONEMPTY_EVENTS = {"type": "array", "items": DYNAMIC_EVENT, "minItems": 1, "maxItems": 64}
ANY_EVENTS = {"type": "array", "items": DYNAMIC_EVENT, "maxItems": 64}


def cleanup_schema(terminate: bool | None, debug_idle: bool, delete_attempted: bool,
                   deleted: bool, residual: dict[str, Any]) -> dict[str, Any]:
    terminate_schema = {"type": "boolean"} if terminate is None else {"const": terminate}
    return obj({"terminate_attempted": terminate_schema, "debug_idle": {"const": debug_idle},
                "temp_delete_attempted": {"const": delete_attempted}, "temp_deleted": {"const": deleted},
                "residual_path": residual})


CLEAN_NONE = cleanup_schema(False, True, False, True, {"type": "null"})
CLEAN_BLOCKED = cleanup_schema(False, True, False, True, {"type": "null"})
CLEAN_SUCCESS = cleanup_schema(True, True, True, True, {"type": "null"})
CLEAN_FAILED_REMOVED = cleanup_schema(None, True, True, True, {"type": "null"})
CLEAN_FAILED_RESIDUAL = cleanup_schema(None, True, True, False, s_string(4096))
def failure_schema(phases: list[str]) -> dict[str, Any]:
    phase = {"const": phases[0]} if len(phases) == 1 else {"enum": phases}
    return obj({"code": s_string(128), "message": s_string(512), "phase": phase})


def dynamic_branch(state: str, artifact: dict[str, Any], events: dict[str, Any], cleanup: dict[str, Any],
                   failure: dict[str, Any]) -> dict[str, Any]:
    requested = state != "not_requested"
    return obj({"state": {"const": state}, "requested": {"const": requested},
                "runtime_profile": s_string(128) if requested else {"type": "null"}, "artifact": artifact,
                "events": events, "cleanup": cleanup, "failure": failure})


DYNAMIC_SUCCESS = {"oneOf": [
    dynamic_branch("not_requested", ARTIFACT_ABSENT, EMPTY_EVENTS, CLEAN_NONE, {"type": "null"}),
    dynamic_branch("not_applicable", ARTIFACT_ABSENT, EMPTY_EVENTS, CLEAN_NONE, {"type": "null"}),
    dynamic_branch("passed", ARTIFACT_PRESENT, NONEMPTY_EVENTS, CLEAN_SUCCESS, {"type": "null"}),
]}
DYNAMIC_ERROR = {"oneOf": [
    dynamic_branch("blocked", ARTIFACT_ABSENT, EMPTY_EVENTS, CLEAN_BLOCKED, failure_schema(["precheck"])),
    dynamic_branch("failed", ARTIFACT_ABSENT, ANY_EVENTS, CLEAN_NONE, failure_schema(["precheck", "write"])),
    dynamic_branch("failed", ARTIFACT_PRESENT, NONEMPTY_EVENTS, CLEAN_FAILED_REMOVED, failure_schema(["launch", "run", "terminate"])),
    dynamic_branch("failed", ARTIFACT_PRESENT, NONEMPTY_EVENTS, CLEAN_FAILED_RESIDUAL, failure_schema(["delete"])),
]}

DYNAMIC_STATE_CONTRACT = {
    "not_requested": "no profile/artifact/events/attempts; debug idle; success review",
    "not_applicable": "profile present; no artifact/events/attempts; debug idle; success review",
    "blocked": "profile and failure present; no artifact/events/attempts; debug idle; no review",
    "passed": "artifact and events present; terminate/delete attempted; debug idle and deleted; success review",
    "failed": "failure phase present; pre-artifact or artifact cleaned/residual branch; debug idle; no review",
}
PRIMITIVE = obj({"fault_id": s_string(64, r"^fp-[0-9]+-(forward|reverse)-[0-9]+-[a-z0-9_]+-(before|after)$"),
                 "operation_index": UINT32, "step_index": UINT32, "operation_kind": {"enum": [r["kind"] for r in SCHEMA_OPERATIONS]},
                 "direction": {"enum": ["forward", "reverse"]}, "boundary": {"enum": ["before", "after"]},
                 "primitive_kind": s_string(16), "target": s_string(64), "step": s_string(96)})
FAULT_COUNT = 2 * sum(len(row["live_forward"]) + len(row["live_reverse"]) for row in SCHEMA_OPERATIONS)
MAX_ACTUAL_TRACE_ROWS = 2 * max(len(row["live_forward"]) + len(row["live_reverse"]) for row in SCHEMA_OPERATIONS)
FAULT_ARRAY = {"type": "array", "items": PRIMITIVE, "minItems": FAULT_COUNT, "maxItems": FAULT_COUNT, "uniqueItems": True}
EXECUTION_EVIDENCE = obj({"armed_fault": nullable(PRIMITIVE), "fault_manifest": FAULT_ARRAY,
                          "oracle_faults": copy.deepcopy(FAULT_ARRAY),
                          "actual_mutation_trace": {"type": "array", "items": copy.deepcopy(PRIMITIVE), "maxItems": MAX_ACTUAL_TRACE_ROWS},
                          "covered_faults": {"type": "array", "items": copy.deepcopy(PRIMITIVE), "maxItems": 1, "uniqueItems": True}})

FAULT_SUITE_CONTRACT = {
    "server_state": {"persistent_fields": ["armed_fault"], "reset_operation": "edit_test_fault:reset"},
    "per_call": {
        "fault_manifest": {"equals": "complete-generated-golden", "projection": "fault_projection", "cardinality": FAULT_COUNT, "unique_by": "fault_id"},
        "oracle_faults": {"equals": "complete-generated-golden", "projection": "fault_projection", "cardinality": FAULT_COUNT, "unique_by": "fault_id"},
        "armed_fault": {"when_unarmed": None, "when_armed": "selected-golden-row"},
        "actual_mutation_trace": {"ordering": "entire-call-forward-then-reverse-before-after-prefix", "maximum_rows": MAX_ACTUAL_TRACE_ROWS,
                                  "when_injected": {"terminal_equals": "armed_fault"}, "when_not_injected": {"terminal_equals": None}},
        "covered_faults": {"when_injected": ["armed_fault"], "when_not_injected": [], "maximum_rows": 1},
    },
    "runner": {
        "suite_id": "unique-per-run", "transaction_count": FAULT_COUNT, "transaction_policy": "one-new-transaction-per-golden-fault",
        "artifact_count": FAULT_COUNT, "artifact_path": "ArtifactRoot/fault-suite/<suite_id>/<fault_id>.json",
        "artifact_unique_by": "fault_id", "covered_union": "all-golden-fault-id-exactly-once",
        "reset": "before-and-after-every-call", "cleanup": "delete-temporary-fixtures-at-suite-end",
    },
}


def success_envelope(result: dict[str, Any]) -> dict[str, Any]:
    return obj({"schema_version": {"const": "dnspy.edit.v1"}, "ok": {"const": True}, "state": {"enum": STATES},
                "result": result, "warnings": {"type": "array", "items": s_string(256), "maxItems": 8}, "untrusted_sample_data": {"const": True}})


def error_envelope(*, validation_attempt: bool = False, execution_evidence: bool = False) -> dict[str, Any]:
    props = {"schema_version": {"const": "dnspy.edit.v1"}, "ok": {"const": False}, "state": {"enum": STATES}, "error": ERROR_UNION,
             "warnings": {"type": "array", "items": s_string(256), "maxItems": 8}, "untrusted_sample_data": {"const": True}}
    if validation_attempt: props["validation_attempt"] = DYNAMIC_ERROR
    if execution_evidence: props["execution_evidence"] = EXECUTION_EVIDENCE
    return obj(props)


BEGIN_RESULT = obj({"transaction": TRANSACTION, "source": SOURCE, "fingerprints": FINGERPRINTS, "limits": LIMITS_SCHEMA, "capacity": CAPACITY,
                    "capabilities": obj({"operation_kinds": {"type": "array", "items": {"enum": [r["kind"] for r in SCHEMA_OPERATIONS]}, "minItems": 22, "maxItems": 22, "uniqueItems": True},
                                         "dynamic_validation": {"type": "boolean"}, "test_apply_restore": {"type": "boolean"}})})
STATUS_RESULT = {"oneOf": [obj({"busy": {"const": False}, "state": {"const": "idle"}}),
                           obj({"busy": {"const": True}, "state": {"enum": STATES[1:]}, "owner_transport_kind": {"enum": ["legacy_sse", "streamable_http"]}}),
                           obj({"busy": {"const": True}, "state": {"enum": STATES[1:]}, "transaction": TRANSACTION, "fingerprints": FINGERPRINTS,
                                "review": nullable(REVIEW_SUMMARY), "capacity": CAPACITY, "risks": {"type": "array", "items": RISK, "maxItems": 256}})]}
APPLY_RESULT = obj({"transaction": TRANSACTION, "operation_index": UINT32, "kind": {"enum": [r["kind"] for r in SCHEMA_OPERATIONS]},
                    "created_object_ids": {"type": "array", "items": ID, "maxItems": 321}, "fingerprints": FINGERPRINTS,
                    "diffs": {"type": "array", "items": DIFF, "maxItems": 256}, "risks": {"type": "array", "items": RISK, "maxItems": 256},
                    "review_cleared": {"const": True}, "capacity": CAPACITY})
REVIEW_RESULT = obj({"transaction": TRANSACTION, "review": REVIEW_SUMMARY, "fingerprints": FINGERPRINTS,
                     "diffs": {"type": "array", "items": DIFF, "maxItems": 256}, "structural_validation": VALIDATION,
                     "roundtrip_validation": VALIDATION, "dynamic_validation": DYNAMIC_SUCCESS,
                     "risks": {"type": "array", "items": RISK, "maxItems": 256}, "limits": LIMITS_SCHEMA})
ROLLBACK_RESULT = obj({"rolled_back": {"const": True}, "end_reason": {"const": "client_rollback"}, "original_live_fingerprint": SHA,
                       "released": obj({"private_modules": UINT32, "validation_modules": UINT32, "apply_cache_entries": UINT32, "review_slots": UINT32})})


def product_schemas() -> dict[str, Any]:
    dynamic_input = obj({"mode": {"const": "run"}, "runtime_profile": s_string(128), "args": {"type": "array", "items": s_string(32767), "maxItems": 256},
                         "working_directory": s_string(32767), "timeout_ms": {"type": "integer", "minimum": 1000, "maximum": 120000}}, ["mode", "runtime_profile"])
    inputs = {
        "edit_begin": obj({"request_id": ID, "assembly_name": s_string(512), "module_mvid": s_string(36)}, ["request_id", "assembly_name"]),
        "edit_status": obj({}, []),
        "edit_apply": obj({"request_id": ID, "transaction_id": ID, "expected_revision": UINT32, "operation": OPERATION_SCHEMA}),
        "edit_review": obj({"request_id": ID, "transaction_id": ID, "expected_revision": UINT32, "dynamic_validation": dynamic_input}, ["request_id", "transaction_id", "expected_revision"]),
        "edit_rollback": obj({"request_id": ID, "transaction_id": ID}),
    }
    results = {"edit_begin": BEGIN_RESULT, "edit_status": STATUS_RESULT, "edit_apply": APPLY_RESULT, "edit_review": REVIEW_RESULT, "edit_rollback": ROLLBACK_RESULT}
    out = {}
    for name in PRODUCT_TOOLS:
        errors = [error_envelope()]
        if name == "edit_review": errors.append(error_envelope(validation_attempt=True))
        out[name] = {"inputSchema": inputs[name], "outputSchema": {"oneOf": [success_envelope(results[name]), *errors]}}
    return out


def test_schemas() -> dict[str, Any]:
    component = {"enum": [*METADATA_COMPONENTS, "GlobalCanonicalOrder"]}
    recipe_ids = [row["recipe_id"] for row in COMPONENT_RECIPES]
    case_ids = [f"{recipe_id}:mutate" for recipe_id in recipe_ids] + [CANONICAL_ORDER_RECIPE["case_id"]]
    clock_input = {"oneOf": [obj({"action": {"const": "read"}}), obj({"action": {"const": "reset"}}), obj({"action": {"const": "advance"}, "advance_ms": UINT64})]}
    barrier_names = ["begin_after_copy", "apply_before_mutation", "review_before_validation"]
    barrier_input = {"oneOf": [
        obj({"action": {"const": "arm"}, "name": {"enum": barrier_names}}),
        obj({"action": {"const": "snapshot"}}),
        obj({"action": {"const": "release"}}),
        obj({"action": {"const": "reset"}}),
    ]}
    external_input = obj({"transaction_id": ID, "case_id": {"enum": case_ids}})
    live_input = obj({"transaction_id": ID, "action": {"enum": ["mutate", "restore"]}})
    fault_input = {"oneOf": [obj({"action": {"const": "read"}}), obj({"action": {"const": "reset"}}), obj({"action": {"const": "arm"}, "fault_id": ID})]}
    apply_input = obj({"request_id": ID, "transaction_id": ID, "review_id": ID, "expected_revision": UINT32,
                       "confirmed_risk_ids": {"type": "array", "items": ID, "maxItems": 256}})
    clock_result = obj({"monotonic_ms": UINT64, "offset_ms": UINT64})
    barrier_result = obj({
        "armed": {"type": "boolean"}, "name": nullable({"enum": barrier_names}),
        "owner_session_id": nullable(ID), "entered": {"type": "boolean"},
        "released": {"type": "boolean"}, "operation_waiters": UINT32,
        "pending_request_key": nullable(s_string(512)),
        "active_generation": nullable(UINT64), "active_transaction_id": nullable(ID),
    })
    external_result = obj({"case_id": {"enum": case_ids},
                           "recipe_id": {"enum": [*recipe_ids, CANONICAL_ORDER_RECIPE["recipe_id"]]}, "component": component, "recipe_sha256": SHA,
                           "evidence_artifact": obj({"path": s_string(4096), "sha256": SHA}),
                           "located_slice_before": SHA, "located_slice_after": SHA,
                           "raw_order_before": SHA, "raw_order_after": SHA,
                           "canonical_readback_before": SHA, "canonical_readback_after": SHA,
                           "before_fingerprint": SHA, "after_fingerprint": SHA, "restored_fingerprint": SHA,
                           "changed": {"type": "boolean"}, "semantic_change": {"type": "boolean"},
                           "restored": {"const": True}})
    live_result = obj({"case_id": {"enum": ["live-conflict:mutate", "live-conflict:restore"]},
                       "recipe_id": {"const": "live-conflict"}, "component": {"const": "ModuleMetadata"},
                       "recipe_sha256": SHA, "evidence_artifact": obj({"path": s_string(4096), "sha256": SHA}),
                       "located_slice_before": SHA, "located_slice_after": SHA,
                       "raw_order_before": SHA, "raw_order_after": SHA,
                       "canonical_readback_before": SHA, "canonical_readback_after": SHA,
                       "before_fingerprint": SHA, "after_fingerprint": SHA, "restored_fingerprint": SHA,
                       "changed": {"type": "boolean"}, "semantic_change": {"const": True},
                       "restored": {"type": "boolean"}})
    fault_result = obj({"armed_fault_id": nullable(ID), "known_fault_ids": {"type": "array", "items": ID, "maxItems": 1024}, "emergency_cleanup": {"type": "boolean"}})
    apply_result = obj({"applied_and_restored": {"const": True}, "pre_live_fingerprint": SHA, "private_fingerprint": SHA,
                        "post_apply_fingerprint": SHA, "post_restore_fingerprint": SHA, "execution_evidence": EXECUTION_EVIDENCE,
                        "dispatcher_callbacks_ms": {"type": "array", "items": {"type": "number", "minimum": 0}, "maxItems": 64}})
    return {
        "edit_test_clock": {"inputSchema": clock_input, "outputSchema": {"oneOf": [success_envelope(clock_result), error_envelope()]}},
        "edit_test_barrier": {"inputSchema": barrier_input, "outputSchema": {"oneOf": [success_envelope(barrier_result), error_envelope()]}},
        "edit_test_external_mutation": {"inputSchema": external_input, "outputSchema": {"oneOf": [success_envelope(external_result), error_envelope()]}},
        "edit_test_live_mutation": {"inputSchema": live_input, "outputSchema": {"oneOf": [success_envelope(live_result), error_envelope()]}},
        "edit_test_fault": {"inputSchema": fault_input, "outputSchema": {"oneOf": [success_envelope(fault_result), error_envelope()]}},
        "edit_test_apply_and_restore": {"inputSchema": apply_input, "outputSchema": {"oneOf": [success_envelope(apply_result), error_envelope(execution_evidence=True)]}},
    }


def schema_max_json_bytes(schema: dict[str, Any]) -> int:
    """Conservative bound for System.Text.Json-compatible UTF-8 output.

    Twelve bytes per JSON-Schema code point covers a supplementary scalar escaped
    as two ``\\uXXXX`` sequences. Optional object properties are all counted.
    """
    if "oneOf" in schema:
        return max(schema_max_json_bytes(branch) for branch in schema["oneOf"])
    if "const" in schema:
        return len(json.dumps(schema["const"], ensure_ascii=True, separators=(",", ":")).encode("utf-8"))
    if "enum" in schema:
        return max(len(json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("utf-8")) for value in schema["enum"])
    kind = schema.get("type")
    if kind == "null":
        return 4
    if kind == "boolean":
        return 5
    if kind == "integer":
        candidates = [schema.get("minimum", -(2**63)), schema.get("maximum", 2**63 - 1)]
        return max(len(str(value)) for value in candidates)
    if kind == "number":
        return 32
    if kind == "string":
        return 2 + 12 * schema.get("maxLength", 4096)
    if kind == "array":
        count = schema.get("maxItems", 0)
        if count == 0:
            return 2
        item = schema_max_json_bytes(schema["items"])
        return 2 + count * item + count - 1
    if kind == "object":
        properties = schema.get("properties", {})
        if not properties:
            return 2
        total = 2 + len(properties) - 1
        for name, child in properties.items():
            total += len(json.dumps(name, ensure_ascii=True).encode("utf-8")) + 1 + schema_max_json_bytes(child)
        return total
    raise ValueError(f"unbounded or unsupported schema node: {schema}")


def build_contract() -> dict[str, Any]:
    schemas = {**product_schemas(), **test_schemas()}
    payload_bounds = {name: schema_max_json_bytes(pair["outputSchema"]) for name, pair in schemas.items()}
    wire_bounds = {name: LIMITS["transport_fixed_envelope_bytes"] +
                         LIMITS["transport_payload_expansion_factor"] * size
                   for name, size in payload_bounds.items()}
    review_max_bytes = payload_bounds["edit_review"]
    review_budget = {
        "calculation": "recursive JSON-Schema upper bound; all optional properties present; strings use 12 UTF-8 bytes per code point",
        "calculated_max_bytes": review_max_bytes,
        "slot_bytes": LIMITS["review_slot_bytes"],
        "headroom_bytes": LIMITS["review_slot_bytes"] - review_max_bytes,
        "full_jsonrpc_max_bytes": wire_bounds["edit_review"],
        "transport_response_bytes": LIMITS["transport_response_bytes"],
        "transport_headroom_bytes": LIMITS["transport_response_bytes"] - wire_bounds["edit_review"],
    }
    if review_budget["headroom_bytes"] < 0:
        raise ValueError(f"edit_review schema exceeds review slot: {review_budget}")
    oversized = {name: size for name, size in wire_bounds.items() if size > LIMITS["transport_response_bytes"]}
    if oversized:
        raise ValueError(f"tool schemas exceed complete JSON-RPC transport budget: {oversized}")
    cache_response_bounds = {
        "edit_begin": {"payload_max_bytes": payload_bounds["edit_begin"], "per_response_limit_bytes": LIMITS["begin_response_bytes"],
                       "entry_limit": LIMITS["begin_cache_entries"], "total_limit_bytes": LIMITS["begin_cache_bytes"]},
        "edit_apply": {"payload_max_bytes": payload_bounds["edit_apply"], "per_response_limit_bytes": LIMITS["apply_response_bytes"],
                       "entry_limit": LIMITS["apply_cache_entries"], "total_limit_bytes": LIMITS["apply_cache_bytes"]},
    }
    if any(row["payload_max_bytes"] > row["per_response_limit_bytes"] for row in cache_response_bounds.values()):
        raise ValueError(f"cacheable response exceeds per-response limit: {cache_response_bounds}")
    tombstone_item_bytes = schema_max_json_bytes(REVIEW_TOMBSTONE)
    review_tombstone_bound = {
        "schema": REVIEW_TOMBSTONE,
        "item_max_bytes": tombstone_item_bytes,
        "entry_limit": LIMITS["review_tombstone_entries"],
        "calculated_total_max_bytes": tombstone_item_bytes * LIMITS["review_tombstone_entries"],
        "configured_total_limit_bytes": LIMITS["review_tombstone_bytes"],
    }
    if review_tombstone_bound["calculated_total_max_bytes"] > review_tombstone_bound["configured_total_limit_bytes"]:
        raise ValueError(f"review tombstones exceed configured byte limit: {review_tombstone_bound}")
    rollback_response_bound = {
        "payload_max_bytes": payload_bounds["edit_rollback"],
        "configured_slot_bytes": LIMITS["rollback_slot_bytes"],
    }
    if rollback_response_bound["payload_max_bytes"] > rollback_response_bound["configured_slot_bytes"]:
        raise ValueError(f"rollback response exceeds terminal slot: {rollback_response_bound}")
    return {
        "format": "dnspy.p02.contract.v1", "schema_version": "dnspy.edit.v1",
        "requirements": ["RACC-001", "RACC-002", "RACC-003", "RACC-009", "RACC-010", "RACC-026"],
        "tools": schemas, "operations": OPERATIONS, "operand_kinds": OPERAND_KINDS,
        "error_codes": ERROR_CODES, "limits": LIMITS, "review_budget": review_budget,
        "tool_response_bounds": {name: {"payload_max_bytes": payload_bounds[name], "full_jsonrpc_max_bytes": wire_bounds[name]}
                                 for name in sorted(schemas)},
        "cache_response_bounds": cache_response_bounds,
        "review_tombstone_bound": review_tombstone_bound,
        "rollback_response_bound": rollback_response_bound,
        "cache_transitions": CACHE_TRANSITIONS,
        "review_lifecycle": REVIEW_LIFECYCLE,
        "operation_semantics": OPERATION_SEMANTICS,
        "fault_id_format": "fp-<operation-index>-<forward|reverse>-<step-index>-<primitive-kind>-<before|after>",
        "fingerprint_mutation_components": METADATA_COMPONENTS,
        "fingerprint_recipes": COMPONENT_RECIPES,
        "risk_policy": {"referenced_or_attached_delete": "EDIT_VALIDATION_FAILED", "unreferenced_public_delete": "risk_confirmation"},
        "fault_projection": ["fault_id", "operation_index", "step_index", "operation_kind", "direction", "boundary", "primitive_kind", "target", "step"],
        "fault_suite_contract": FAULT_SUITE_CONTRACT,
        "acceptance_cases": ACCEPTANCE_CASES,
        "dynamic_states": ["not_requested", "not_applicable", "blocked", "passed", "failed"],
        "dynamic_state_contract": DYNAMIC_STATE_CONTRACT,
    }


def canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def write_json(output_dir: Path, name: str, value: Any) -> str:
    data = canonical_bytes(value)
    (output_dir / name).write_bytes(data)
    return hashlib.sha256(data).hexdigest()


def main(output_dir: Path = GENERATED) -> int:
    output_dir.mkdir(parents=True, exist_ok=True)
    contract = build_contract()
    hashes: dict[str, str] = {}
    hashes["expanded-tool-schemas.json"] = write_json(output_dir, "expanded-tool-schemas.json", contract["tools"])
    hashes["operation-lowering.json"] = write_json(output_dir, "operation-lowering.json", {
        "operations": OPERATIONS, "operand_kinds": OPERAND_KINDS, "operation_semantics": OPERATION_SEMANTICS})
    fault_rows = []
    for op_index, row in enumerate(SCHEMA_OPERATIONS):
        for direction, steps in (("forward", row["live_forward"]), ("reverse", row["live_reverse"])):
            for step_index, step in enumerate(steps):
                parts = step.split(":")
                primitive = parts[0]
                target = ":".join(parts[1:])
                for boundary in ("before", "after"):
                    fault_rows.append({"operation_index": op_index, "operation_kind": row["kind"], "direction": direction,
                                       "step_index": step_index, "primitive_kind": primitive, "boundary": boundary,
                                       "target": target, "step": step,
                                       "fault_id": f"fp-{op_index}-{direction}-{step_index}-{primitive}-{boundary}"})
    hashes["fault-golden.json"] = write_json(output_dir, "fault-golden.json", {
        "projection": contract["fault_projection"], "suite_contract": contract["fault_suite_contract"],
        "faults": fault_rows})
    hashes["capacity-golden.json"] = write_json(output_dir, "capacity-golden.json", {
        "limits": LIMITS, "review_budget": contract["review_budget"], "cache_transitions": CACHE_TRANSITIONS,
        "review_lifecycle": REVIEW_LIFECYCLE, "tool_response_bounds": contract["tool_response_bounds"],
        "cache_response_bounds": contract["cache_response_bounds"],
        "review_tombstone_bound": contract["review_tombstone_bound"],
        "rollback_response_bound": contract["rollback_response_bound"]})
    corpus = []
    for recipe in COMPONENT_RECIPES:
        recipe_sha = hashlib.sha256(canonical_bytes(recipe)).hexdigest()
        corpus.append({"case_id": f"{recipe['recipe_id']}:mutate", "mode": "mutate",
                       "expected_fingerprint_change": True, "action": recipe["mutate_action"],
                       "recipe_sha256": recipe_sha, **recipe})
    order_recipe = copy.deepcopy(CANONICAL_ORDER_RECIPE)
    order_identity = {key: order_recipe[key] for key in ("recipe_id", "component", "category", "fixture_precondition",
                                                          "live_target", "mutate_action", "readback", "restore_action", "reorder_action")}
    order_recipe["recipe_sha256"] = hashlib.sha256(canonical_bytes(order_identity)).hexdigest()
    corpus.append(order_recipe)
    hashes["mutation-corpus.json"] = write_json(output_dir, "mutation-corpus.json", {
        "evidence_invariant": "case_id/recipe/target/action/readback/restore must match; mutation readback must change, reorder must execute and canonical readback must remain equal; restored fingerprint equals baseline",
        "cases": corpus})
    hashes["requirements-map.json"] = write_json(output_dir, "requirements-map.json", {
        "requirements": {racc: [row["case_id"] for row in rows] for racc, rows in ACCEPTANCE_CASES.items()},
        "cases": ACCEPTANCE_CASES})
    index = {"format": contract["format"], "source": "contract_source.py", "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
             "dependency": str(DNILIB_FACTS_PATH.relative_to(ROOT)),
             "dependency_sha256": hashlib.sha256(DNILIB_FACTS_PATH.read_bytes()).hexdigest(), "generated_sha256": hashes}
    write_json(output_dir, "contract-index.json", index)
    print(json.dumps(index, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    destination = GENERATED
    if len(sys.argv) == 3 and sys.argv[1] == "--output-dir":
        destination = Path(sys.argv[2]).resolve()
    elif len(sys.argv) != 1:
        raise SystemExit("usage: contract_source.py [--output-dir PATH]")
    raise SystemExit(main(destination))
