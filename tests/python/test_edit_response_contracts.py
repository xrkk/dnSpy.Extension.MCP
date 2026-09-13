"""Public response shapes for importer, impact scan and commit recovery evidence."""
import json
from pathlib import Path
import unittest
import jsonschema

SCHEMAS = json.loads((Path(__file__).resolve().parents[2] / 'Editing/Contracts/p03-tool-schemas.json').read_text())
TRANSACTION = dict(transaction_id='tx', work_revision=0, review_revision=None,
    operation_count=0, started_at_monotonic_ms=0, last_activity_monotonic_ms=0)

def result_schema(tool):
    return SCHEMAS[tool]['outputSchema']['oneOf'][0]['properties']['result']

class EditResponseContractTests(unittest.TestCase):
    def test_impact_response_without_apply_fields(self):
        payload = dict(transaction=TRANSACTION, impact=dict(scope='loaded_modules', modules=[],
            inbound_references=[], risk_ids=[], identity_operations=[]))
        jsonschema.Draft202012Validator(result_schema('edit_impact_scan')).validate(payload)

    def test_import_response_declares_batch_fields(self):
        schema = result_schema('edit_import')
        self.assertIn('import', schema['properties'])
        self.assertIn('operation_count', schema['required'])
        self.assertNotIn('operation_index', schema['required'])
        jsonschema.Draft202012Validator(schema['properties']['import']).validate(dict(
            compile_id='compile-1', target_count=1, rows=[dict(kind='method_body_replace',
            artifact_member='Example.A()', target='0x06000001')], created_object_ids=[]))

    def test_commit_declares_precompiled_recovery_evidence(self):
        schema = result_schema('edit_commit')
        self.assertIn('live_recovery', schema['properties'])
        jsonschema.Draft202012Validator(schema['properties']['live_recovery']).validate(dict(
            inverse_plan='pregenerated_compiled_state', inverse_plan_operations=1,
            inverse_plan_bound_checkpoint_id='checkpoint-1', inverse_plan_complete_before_live_write=True))
