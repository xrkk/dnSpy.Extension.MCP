"""The evidence architecture must reach the UI driver, without leaking process state."""
import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('ui_evidence_runner', Path(__file__).with_name('p03_vm_edit_acc_evidence.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class UiEvidenceArchitectureTests(unittest.TestCase):
    def test_requested_architecture_overrides_and_restores_environment(self):
        for arch in ('x64', 'x86'):
            for fails in (False, True):
                with self.subTest(arch=arch, fails=fails), tempfile.TemporaryDirectory() as temporary:
                    seen = []

                    def main():
                        seen.append(os.environ.get('DNMCP_UI_ARCH'))
                        if fails:
                            raise RuntimeError('driver failure')
                        print('PASS exercised requested architecture')
                        return 0

                    module = types.SimpleNamespace(call=lambda *_: {}, DnSpyClient=type('Client', (), {}), main=main)
                    with patch.dict(os.environ, {'DNMCP_UI_ARCH': 'previous-value'}), patch.object(runner.importlib, 'import_module', return_value=module):
                        status, _ = runner.run_case('EDIT-ACC-018', 'architecture-regression', Path(temporary), arch)
                        self.assertEqual(seen, [arch])
                        self.assertEqual(os.environ['DNMCP_UI_ARCH'], 'previous-value')
                        self.assertEqual(status, 'fail' if fails else 'pass')

    def test_missing_environment_is_not_created_permanently(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            module = types.SimpleNamespace(call=lambda *_: {}, DnSpyClient=type('Client', (), {}), main=lambda: 1)
            with patch.object(runner.importlib, 'import_module', return_value=module):
                runner.run_case('EDIT-ACC-018', 'architecture-regression', Path(temporary), 'x86')
            self.assertNotIn('DNMCP_UI_ARCH', os.environ)


if __name__ == '__main__':
    unittest.main()
