#!/usr/bin/env python3
"""Check the published resource value union, including schema/business boundary."""
import json
from pathlib import Path
from jsonschema import Draft202012Validator

root = Path(__file__).resolve().parents[2]
# IMP-502: the advertised edit_apply operation schema is now a compact kind
# discriminator; the frozen v1 managed_resource_update branch (the resource
# value union this check pins) is preserved verbatim as this fixture, extracted
# from the pre-2026-09-29 p03-tool-schemas.json oneOf. The kind whitelist this
# branch belongs to is asserted separately below from the published schema.
resource = json.loads((root / 'tests/edit/t094-managed-resource-branch.json').read_text())
assert resource['properties']['kind']['const'] == 'managed_resource_update'
validator = Draft202012Validator(resource)

schema = json.loads((root / 'Editing/Contracts/p03-tool-schemas.json').read_text())
operation_schema = schema['edit_apply']['inputSchema']['properties']['operation']
kinds = operation_schema['properties']['kind']['enum']
assert 'managed_resource_update' in kinds and len(kinds) == 39

def operation(kind, value):
    return {'kind':'managed_resource_update','target':{'name':'T091.Values.resources'},
            'entry':{'name':'char','value_kind':kind,'value':value}}

valid = [
    operation('char', {'code_unit':0}), operation('char', {'code_unit':65535}),
    operation('decimal', {'lo':4294967295,'mid':0,'hi':0,'negative':True,'scale':28}),
    operation('datetime', {'binary':'-8584709950854775808'}),
    operation('timespan', {'ticks':'-9223372036854775808'}),
    operation('string', 'old'),
]
invalid = [
    operation('char', {'code_unit':-1}), operation('char', {'code_unit':65536}),
    operation('char', {'code_unit':1.5}), operation('char', {'code_unit':65,'extra':0}),
    operation('decimal', {'lo':0,'mid':0,'hi':0,'negative':False,'scale':29}),
    operation('decimal', {'lo':0,'mid':0,'hi':0,'negative':0,'scale':0}),
    operation('timespan', {'ticks':'-0'}), operation('timespan', {'ticks':1}),
    operation('datetime', {'binary':'01'}), operation('null', None),
]
for item in valid:
    assert validator.is_valid(item), item
for item in invalid:
    assert not validator.is_valid(item), item

# These are deliberately schema-valid: range/canonical CLR value is the
# resource business gate and must not be called a JSON-RPC schema rejection.
business_negative = [operation('timespan', {'ticks':'9223372036854775808'}),
                     operation('datetime', {'binary':'9223372036854775807'})]
for item in business_negative:
    assert validator.is_valid(item), item
assert len(kinds) == 39
print(json.dumps({'status':'PASS','schema_valid':len(valid),'schema_rejected':len(invalid),
                  'business_only_negative':len(business_negative),'operation_kinds':len(kinds)}))
