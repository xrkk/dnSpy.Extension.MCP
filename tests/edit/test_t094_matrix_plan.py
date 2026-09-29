#!/usr/bin/env python3
"""Check the public matrix vectors against the published resource schema."""
import json
import sys
from pathlib import Path
from jsonschema import Draft202012Validator

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "tests/edit"))
from t094_vm_resource_matrix import VECTORS, DATE_VECTORS  # noqa: E402

# IMP-502: the advertised edit_apply operation schema is now a compact kind
# discriminator; the frozen v1 managed_resource_update branch (the resource
# value union this check pins) is preserved verbatim as this fixture, extracted
# from the pre-2026-09-29 p03-tool-schemas.json oneOf.
branch = json.loads((root / "tests/edit/t094-managed-resource-branch.json").read_text())
assert branch["properties"]["kind"]["const"] == "managed_resource_update"
validator = Draft202012Validator(branch)
kind = {"char": "char", "decimal": "decimal", "span": "timespan", "date": "datetime"}
for vector in VECTORS:
    assert set(vector) == set(kind)
    for name, value in vector.items():
        operation = {"kind": "managed_resource_update", "target": {"name": "T091.Values.resources"},
                     "entry": {"name": name, "value_kind": kind[name],
                               "value": {k: v for k, v in value.items() if k != "kind"}}}
        assert validator.is_valid(operation), (name, value)
assert [x["char"]["code_unit"] for x in VECTORS[:4]] == [0, 65535, 55296, 56320]
assert {x["span"]["ticks"] for x in VECTORS} >= {"0", "-1", "-9223372036854775808", "9223372036854775807"}
assert {x["decimal"]["negative"] for x in VECTORS if x["decimal"]["lo"] == 0} == {True, False}
assert {x["decimal"]["scale"] for x in VECTORS if x["decimal"]["lo"] == 0} == {0, 28}
assert all(VECTORS[2]["decimal"][part] == 4294967295 for part in ("lo", "mid", "hi"))
assert DATE_VECTORS == [
    ("0", "Unspecified"), ("3155378975999999999", "Unspecified"),
    ("638661942000000000", "Unspecified"), ("5250348104427387904", "Utc"),
    ("-8584709950854775808", "Local"), ("-8584709914854775808", "Local"),
]
print(json.dumps({"status": "PASS", "checkpoints": 7, "old_v1_rows": 1,
                  "new_v2_rows": len(VECTORS) * 4, "boundary_values": len(VECTORS) * 4}))
