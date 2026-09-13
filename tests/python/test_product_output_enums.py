"""Product output vocabularies must cover actual producers, independently of P02."""
import json
from pathlib import Path
import re
import unittest
ROOT = Path(__file__).resolve().parents[2]

def nodes(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from nodes(child)
    elif isinstance(value, list):
        for child in value:
            yield from nodes(child)

class ProductOutputEnums(unittest.TestCase):
    def test_product_operation_and_risk_vocabularies(self):
        wire = (ROOT / 'Editing/EditContracts.cs').read_text()
        operations = set(re.findall(r'"([a-z0-9_]+)"', wire.split('string[] OperationKinds = {')[1].split('};')[0]))
        registry = (ROOT / 'Editing/EditOperationRegistry.cs').read_text()
        risks = set(re.findall(r'Risk\("([a-z0-9_]+)"', registry)) | {'cross_assembly_inbound'}
        contract = json.loads((ROOT / 'Editing/Contracts/p03-tool-schemas.json').read_text())
        op_count = risk_count = 0
        for tool, definition in contract.items():
            if tool.startswith('edit_test_'):
                continue
            for node in nodes(definition['outputSchema']):
                values = set(node.get('enum', []))
                if 'type_add' in values:
                    self.assertEqual(values, operations, tool)
                    op_count += 1
                if 'public_delete' in values:
                    self.assertEqual(values, risks, tool)
                    risk_count += 1
        self.assertEqual(op_count, 7)
        self.assertEqual(risk_count, 4)
