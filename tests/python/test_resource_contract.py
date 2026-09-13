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
