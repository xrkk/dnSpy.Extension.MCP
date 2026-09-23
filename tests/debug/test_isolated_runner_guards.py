"""Static safety audit for the small isolated ACC handler allowlist."""
import re
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name('run-debug-tests.ps1')
TEXT = SCRIPT.read_text(encoding='utf-8')
ALLOWED = (
    'ACC-005', 'ACC-006', 'ACC-007', 'ACC-008', 'ACC-009', 'ACC-010', 'ACC-011', 'ACC-012',
    'ACC-013', 'ACC-014', 'ACC-015', 'ACC-016', 'ACC-017', 'ACC-018', 'ACC-019',
    'ACC-020', 'ACC-021', 'ACC-022', 'ACC-024', 'ACC-025', 'ACC-026', 'ACC-027',
    'ACC-004', 'ACC-028', 'ACC-029', 'ACC-030', 'ACC-031', 'ACC-032', 'ACC-034', 'ACC-035', 'ACC-036',
)


class IsolatedRunnerGuards(unittest.TestCase):
    def test_results_are_never_recursively_overwritten(self):
        self.assertIn('result already exists; refusing overwrite', TEXT)
        self.assertIn('isolated result already exists', TEXT)
        self.assertNotRegex(TEXT, r'Remove-Item\s+-Recurse\s+-Force\s+\$script:OutDir')
        self.assertIn("[IO.File]::WriteAllText((Join-Path $script:OutDir 'evidence-gate.log')", TEXT)

    def test_isolated_parameters_are_fail_closed(self):
        self.assertIn('partial isolation parameters are forbidden', TEXT)
        self.assertIn('^[a-z0-9][a-z0-9-]{0,48}$', TEXT)
        self.assertIn('^[0-9a-f]{40}$', TEXT)
        self.assertIn('15378,15379', TEXT)
        self.assertTrue('dnspy-t072-r02-' in TEXT, 'private root guard missing')
        self.assertIn('case $Case has not passed the isolated handler safety audit', TEXT)
        self.assertIn('isolated runner must execute from its own private repo tree', TEXT)
        self.assertIn('private .NET 10 x64 host is required for isolated CoreCLR cases', TEXT)
        self.assertIn('private x86 runtime and dnSpy host are required for isolated ACC-029', TEXT)
        self.assertIn('isolated ACC-036 requires a free adjacent private secondary port', TEXT)

    def test_allowed_handlers_have_no_shared_or_global_writes(self):
        starts = [(m.start(), m.group(1)) for m in re.finditer(r'^function Run-(ACC\d{3}) \{', TEXT, re.M)]
        bodies = {}
        for i, (start, name) in enumerate(starts):
            end = starts[i+1][0] if i+1 < len(starts) else TEXT.index('# ---------------------------------------------------------------- dispatch + finalize')
            bodies[name.replace('ACC', 'ACC-')] = TEXT[start:end]
        allowlist = TEXT.split('if ($Case -notin @(')[1].split('))')[0]
        self.assertEqual(set(ALLOWED), set(re.findall(r"ACC-\d{3}", allowlist)))
        for case in ALLOWED:
            with self.subTest(case=case):
                self.assertIn(case, bodies)
                unsafe = re.search(r'C:\\Tools|\bnetsh\b|\bStop-Process\b|Get-Process\s+dnSpy|15378|15379', bodies[case])
                self.assertIsNone(unsafe, f'{case}: shared/global operation at {unsafe.group(0) if unsafe else ""}')
        self.assertRegex(bodies['ACC-015'], r"\$side = Join-Path \$m\.env\.sample_root 'side-effects\.txt'")
        self.assertRegex(bodies['ACC-026'], r"\$out = Join-Path \$m\.env\.sample_root 'argv-out\.txt'")


if __name__ == '__main__':
    unittest.main()
