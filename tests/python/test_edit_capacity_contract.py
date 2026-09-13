"""Replay complete VM success responses: capacity is not a hand-written fixture."""
import copy
import json
from pathlib import Path
import unittest
import jsonschema

ROOT = Path(__file__).resolve().parents[2]
CONTRACT = json.loads((ROOT / 'Editing/Contracts/p03-tool-schemas.json').read_text(encoding='utf-8'))
CAPTURE = json.loads((ROOT / 'tests/edit/fixtures/chk018-live-capacity.json').read_text(encoding='utf-8'))

class EditCapacityContractTests(unittest.TestCase):
    def test_complete_captured_success_responses(self):
        for capture in CAPTURE['responses']:
            with self.subTest(tool=capture['tool']):
                jsonschema.Draft202012Validator(CONTRACT[capture['tool']]['outputSchema']).validate(capture['response'])

    def test_all_capacity_consumers_accept_actual_meters_and_reject_unknown(self):
        actual = CAPTURE['responses'][0]['response']['result']['capacity']
        for tool in ('edit_begin', 'edit_apply', 'edit_import', 'edit_resource_import'):
            with self.subTest(tool=tool):
                schema = CONTRACT[tool]['outputSchema']['oneOf'][0]['properties']['result']['properties']['capacity']
                validator = jsonschema.Draft202012Validator(schema)
                validator.validate(actual)
                self.assertEqual(set(schema['required']), set(actual))
                with self.assertRaises(jsonschema.ValidationError):
                    validator.validate({**actual, 'undeclared_meter': {'current': 0, 'maximum': 1}})
                invalid = copy.deepcopy(actual)
                invalid['review_tombstone_entries']['current'] = -1
                with self.assertRaises(jsonschema.ValidationError):
                    validator.validate(invalid)
