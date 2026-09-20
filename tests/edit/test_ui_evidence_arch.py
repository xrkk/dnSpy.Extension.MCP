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
                    for case_id in ('EDIT-ACC-018', 'EDIT-ACC-025'):
                        with self.subTest(case_id=case_id), patch.dict(os.environ, {'DNMCP_UI_ARCH': 'previous-value', 'DNMCP_UI_DEPLOYMENT_ROOT': 'test-ui-root'}):
                            status, _ = runner.run_case(case_id, 'architecture-regression-'+case_id, Path(temporary), arch, module_loader=lambda _: module)
                            self.assertEqual(seen[-1:], [arch])
                            self.assertEqual(os.environ['DNMCP_UI_ARCH'], 'previous-value')
                            self.assertEqual(status, 'fail' if fails else 'pass')

    def test_missing_environment_is_not_created_permanently(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {}, clear=True):
            module = types.SimpleNamespace(call=lambda *_: {}, DnSpyClient=type('Client', (), {}), main=lambda: 1)
            runner.run_case('EDIT-ACC-025', 'architecture-regression', Path(temporary), 'x86', module_loader=lambda _: module)
            self.assertNotIn('DNMCP_UI_ARCH', os.environ)


if __name__ == '__main__':
    unittest.main()
