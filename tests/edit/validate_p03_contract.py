#!/usr/bin/env python3
"""Independent cross-artifact checks for the P03 declarative contract."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = ROOT / "Editing/Contracts/p03-tool-schemas.json"
PACKAGE = ROOT / "Editing/Contracts/checkpoint-package.schema.json"
ACCEPTANCE = ROOT / "Editing/Contracts/p03-acceptance.json"
PROVIDER = ROOT / "Editing/EditToolProvider.cs"
CATALOG = ROOT / "Editing/EditSchemaCatalog.cs"
PROJECT = ROOT / "dnSpy.Extension.MCP.csproj"

PRODUCT = [
    "edit_begin", "edit_status", "edit_apply", "edit_import", "edit_impact_scan", "edit_resource_import", "edit_resource_export", "edit_review", "edit_rollback",
    "edit_commit", "edit_history", "edit_undo", "edit_redo", "edit_restore",
    "edit_export", "edit_recover", "edit_accept_live",
]
TEST = [
    "edit_test_clock", "edit_test_barrier", "edit_test_external_mutation",
    "edit_test_live_mutation", "edit_test_fault", "edit_test_apply_and_restore",
    "edit_test_storage_fault", "edit_test_lineage_mutation",
]
OPERATIONS = [
    "type_add", "type_update", "type_remove", "method_add", "method_update",
    "method_remove", "field_add", "field_update", "field_remove", "property_add",
    "property_update", "property_remove", "event_add", "event_update", "event_remove",
    "parameter_add", "parameter_update", "parameter_remove", "generic_parameter_add",
    "generic_parameter_update", "generic_parameter_remove", "method_body_replace",
    "attribute_add", "attribute_remove", "security_add", "security_remove",
    "assembly_update", "module_update", "assembly_ref_update", "entry_point_set",
    "managed_resource_add", "managed_resource_update", "managed_resource_remove",
    "win32_resource_add", "win32_resource_update", "win32_resource_remove",
    "strong_name_remove",
]
ACCS = ["ACC-011", "ACC-012", "ACC-013", "ACC-014", "ACC-019", "ACC-020", "ACC-024", "ACC-025", "ACC-029", "ACC-031", "ACC-006", "ACC-015", "ACC-032", "ACC-007", "ACC-008", "ACC-016", "ACC-033", "ACC-018", "ACC-021", "ACC-023"]
BARRIERS = [
    "begin_after_copy", "apply_before_mutation", "review_before_validation",
    "commit_after_guard_before_temp", "commit_after_temp_validate",
    "commit_dispatcher_queued", "commit_after_live_first_mutation",
    "commit_after_live_complete", "commit_after_package_switch_before_response",
]


def walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def listed(source: str, field: str) -> list[str]:
    match = re.search(rf"{field}\s*=\s*\{{(?P<body>.*?)\}};", source, re.S)
    if not match:
        raise AssertionError(f"provider:{field}:missing")
    return re.findall(r'"([a-z0-9_]+)"', match.group("body"))


def check(condition: bool, name: str, failures: list[str]) -> None:
    if not condition:
        failures.append(name)


def main() -> int:
    failures: list[str] = []
    schemas = json.loads(SCHEMAS.read_text(encoding="utf-8"))
    package = json.loads(PACKAGE.read_text(encoding="utf-8"))
    acceptance = json.loads(ACCEPTANCE.read_text(encoding="utf-8"))
    provider = PROVIDER.read_text(encoding="utf-8")
    catalog = CATALOG.read_text(encoding="utf-8")
    project = PROJECT.read_text(encoding="utf-8")

    check(list(schemas) == PRODUCT[:5] + TEST[:6] + PRODUCT[5:] + TEST[6:] or set(schemas) == set(PRODUCT + TEST), "schemas:tool-set", failures)
    check(len(schemas) == 25, "schemas:tool-count", failures)
    check(all("$ref" not in node for node in walk(schemas) if isinstance(node, dict)), "schemas:no-ref", failures)
    check(all(isinstance(schemas[name].get("inputSchema"), dict) and isinstance(schemas[name].get("outputSchema"), dict) for name in schemas), "schemas:object-roots", failures)
    for name in PRODUCT:
        output = schemas[name]["outputSchema"]
        expected_branches = 3 if name == "edit_history" else 2
        check(isinstance(output.get("oneOf"), list) and len(output["oneOf"]) == expected_branches, f"{name}:success-failure", failures)
        branches = output.get("oneOf", [])
        check(all(row.get("type") == "object" and row.get("additionalProperties") is False for row in branches), f"{name}:strict-envelope", failures)
        check(len(branches) == expected_branches and set(branches[0].get("required", [])) == {"schema_version", "ok", "state", "warnings", "untrusted_sample_data", "result"}, f"{name}:success-required", failures)
        check(len(branches) == expected_branches and set(branches[-1].get("required", [])) == {"schema_version", "ok", "state", "warnings", "untrusted_sample_data", "error"}, f"{name}:failure-required", failures)

    operation_branches = schemas["edit_apply"]["inputSchema"]["properties"]["operation"]["oneOf"]
    actual_operations = [row["properties"]["kind"]["const"] for row in operation_branches]
    check(actual_operations == OPERATIONS, "edit_apply:26-operation-order", failures)
    barrier_names = schemas["edit_test_barrier"]["inputSchema"]["oneOf"][0]["properties"]["name"]["enum"]
    check(barrier_names == BARRIERS, "barrier:nine-names", failures)
    check(schemas["edit_begin"]["inputSchema"]["properties"].get("source_family_id", {}).get("pattern") == r"^family-[0-9a-f]{32}$", "begin:family-id", failures)

    check(package.get("schema_version") == "dnspy.edit.checkpoint-contract.v1", "package:version", failures)
    manifest = package["manifestSchema"]
    operation = package["operationSchema"]
    check(manifest.get("additionalProperties") is False and operation.get("additionalProperties") is False, "package:strict-roots", failures)
    inverse = operation["properties"]["operations"]["items"]["properties"]["inverse"]
    check(inverse.get("additionalProperties") is False, "package:strict-inverse", failures)
    check(inverse.get("properties", {}).get("strategy", {}).get("enum") == ["compiled_state"], "package:executable-inverse", failures)
    check("state" in inverse.get("properties", {}) and "state" in inverse.get("required", []), "package:compiled-inverse-state", failures)
    check("path_hash" in manifest["properties"]["default_output"]["required"], "package:default-output-alias", failures)

    check(acceptance.get("schema_version") == "dnspy.edit.p03.acceptance.v1", "acceptance:version", failures)
    check(acceptance.get("architectures") == ["x86", "x64"], "acceptance:architectures", failures)
    check([row.get("id") for row in acceptance.get("cases", [])] == ACCS, "acceptance:cases", failures)
    check(listed(provider, "ProductTools") == PRODUCT, "provider:product-tools", failures)
    check(listed(provider, "TestTools") == TEST, "provider:test-tools", failures)
    for logical in ("p03-tool-schemas.json", "checkpoint-package.schema.json", "p03-acceptance.json"):
        check(logical in catalog and logical in project, f"embed:{logical}", failures)

    inputs = [SCHEMAS, PACKAGE, ACCEPTANCE, PROVIDER, CATALOG, PROJECT]
    evidence = {
        "schema_version": "dnspy.edit.p03.contract-validation.v1",
        "checks_failed": failures,
        "result": "PASS" if not failures else "FAIL",
        "inputs": {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs},
        "counts": {"tools": len(schemas), "product_tools": len(PRODUCT), "test_tools": len(TEST), "operations": len(actual_operations), "acceptance_cases": len(acceptance.get("cases", []))},
    }
    print(json.dumps(evidence, ensure_ascii=False, indent=2, sort_keys=True))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
