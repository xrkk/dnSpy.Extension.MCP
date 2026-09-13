"""Resource path contract: validate real requests against the published schemas."""
import json
from pathlib import Path
import unittest
import jsonschema

CONTRACT = json.loads((Path(__file__).resolve().parents[2] / 'Editing/Contracts/p03-tool-schemas.json').read_text())

class ResourceContractTests(unittest.TestCase):
    def validate(self, tool, request):
        jsonschema.Draft202012Validator(CONTRACT[tool]['inputSchema']).validate(request)

    def test_linked_import_is_supported(self):
        self.validate('edit_resource_import', dict(request_id='r', transaction_id='t', expected_revision=0,
            vm_path=r'C:\samples\payload.bin', resource_name='payload', resource_type='linked'))

    def test_win32_export_numeric_identity(self):
        self.validate('edit_resource_export', dict(request_id='r', assembly_name='sample', resource_name='payload',
            output_path='resources/payload.bin', resource_type='win32', type_id=10, name_id=7, lang_id=1033))

    def test_legacy_embedded_export_remains_valid(self):
        self.validate('edit_resource_export', dict(request_id='r', assembly_name='sample', resource_name='payload',
            output_path='resources/payload.bin'))

    def test_export_result_matches_actual_response_shape(self):
        result = {'export': dict(path=r'C:\artifacts\payload.bin', file_id='0' * 32,
            length=4, sha256='a' * 64)}
        schema = CONTRACT['edit_resource_export']['outputSchema']['oneOf'][0]['properties']['result']
        jsonschema.Draft202012Validator(schema).validate(result)

    def test_import_identity_is_declared(self):
        schema = CONTRACT['edit_resource_import']['outputSchema']['oneOf'][0]['properties']['result']
        self.assertIn('import', schema['properties'])
        self.assertIn('import', schema['required'])
        identity = dict(vm_path=r'C:\samples\payload.bin', resource_name='payload',
            resource_type='linked', file_id='0' * 32, length=4, sha256='a' * 64)
        validator = jsonschema.Draft202012Validator(schema['properties']['import'])
        validator.validate(identity)
        with self.assertRaises(jsonschema.ValidationError):
            validator.validate({**identity, 'file_id': 'file-random-id'})

    def test_ambiguous_type_identity_is_rejected(self):
        with self.assertRaises(jsonschema.ValidationError):
            self.validate('edit_resource_export', dict(request_id='r', assembly_name='sample', resource_name='payload',
                output_path='resources/payload.bin', resource_type='win32', type_id=10, type_name='RCDATA'))

    def test_language_overflow_is_rejected(self):
        with self.assertRaises(jsonschema.ValidationError):
            self.validate('edit_resource_export', dict(request_id='r', assembly_name='sample', resource_name='payload',
                output_path='resources/payload.bin', resource_type='win32', lang_id=65536))

if __name__ == '__main__':
    unittest.main()
