from __future__ import annotations

import unittest
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))

import p03_vm_acc014_formal as acc014
import p03_vm_acc024_formal as acc024
import p03_vm_edit_acc_evidence as evidence
from p03_vm_isolation import IsolationContext


class T014FormalDriverTests(unittest.TestCase):
    def test_old_and_formal_cases_have_independent_driver_mappings(self):
        self.assertEqual("p03_vm_acc014b", evidence.CASE_MODULES["EDIT-ACC-014"])
        self.assertEqual("p03_vm_acc024c", evidence.CASE_MODULES["EDIT-ACC-024"])
        self.assertEqual("p03_vm_acc014_formal", evidence.CASE_MODULES["EDIT-ACC-014-FORMAL"])
        self.assertEqual("p03_vm_acc024_formal", evidence.CASE_MODULES["EDIT-ACC-024-FORMAL"])

    def test_old_and_formal_cases_execute_through_independent_runner_entries(self):
        expected = {
            "EDIT-ACC-014": "p03_vm_acc014b",
            "EDIT-ACC-014-FORMAL": "p03_vm_acc014_formal",
            "EDIT-ACC-024": "p03_vm_acc024c",
            "EDIT-ACC-024-FORMAL": "p03_vm_acc024_formal",
        }
        loaded = []

        class Client:
            def __init__(self, *args, **kwargs): pass
            def close(self): pass

        def load(name):
            loaded.append(name)
            return SimpleNamespace(
                call=lambda client, tool, args: {}, DnSpyClient=Client,
                configure_isolation=lambda context: None,
                main=lambda: (print("PASS routed driver"), 0)[1])

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "isolated"
            for index, (case_id, module) in enumerate(expected.items()):
                run_id = f"t014-route-{index}"
                context = IsolationContext(
                    run_id=run_id, architecture="x64", mcp_url="http://127.0.0.1:15400/mcp",
                    isolation_root=str(root), fixture_root=str(root / "fixtures"),
                    artifact_root=str(root / "artifacts"), checkpoint_store=str(root / "checkpoints"),
                    work_root=str(root / "work"), harness_dir=str(root / "harness"),
                    dotnet_host=str(root / "dotnet.exe"))
                status, summary = evidence.run_case(
                    case_id, run_id, root / "artifacts", isolation=context, module_loader=load)
                self.assertEqual("pass", status)
                self.assertEqual(module + ".py", summary["driver"])
        self.assertEqual(list(expected.values()), loaded)

    def test_export_variants_cover_default_explicit_and_legacy(self):
        lineage = "lineage-" + "1" * 32
        checkpoint = "checkpoint-" + "2" * 32
        explicit = r"C:\isolated\artifact\explicit.dll"
        self.assertEqual([
            ("default", "edit_export", {"lineage_id": lineage, "checkpoint_id": checkpoint}),
            ("explicit", "edit_export", {"lineage_id": lineage, "checkpoint_id": checkpoint,
                                           "output_path": explicit}),
            ("legacy-default", "save_assembly", {"assembly_name": "TestIL"}),
            ("legacy-explicit", "save_assembly", {"assembly_name": "TestIL", "output_path": explicit}),
        ], acc024.export_variants(lineage, checkpoint, explicit))

    def test_formal_export_gate_never_accepts_neighboring_error(self):
        self.assertTrue(acc014.is_formal_export_block({"ok": False, "error": {"code": "EDIT_EXPORT_BLOCKED"}}))
        self.assertFalse(acc014.is_formal_export_block({"ok": False, "error": {"code": "EDIT_CHECKPOINT_COMMIT_FAILED"}}))
        self.assertFalse(acc014.is_formal_export_block({"ok": False, "error": {"code": "EDIT_LIVE_STATE_UNKNOWN"}}))

    def test_windows_identity_probe_compares_device_and_file_index(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "output.dll"
            path.write_bytes(b"old")
            original = acc014.identity(path)
            path.write_bytes(b"new")
            self.assertEqual(original, acc014.identity(path))
            replacement = Path(folder) / "replacement.dll"
            replacement.write_bytes(b"replacement")
            replacement.replace(path)
            self.assertNotEqual(original, acc014.identity(path))

    def test_idempotence_comparison_requires_all_observable_state_to_match(self):
        first = {"nodes": ("root", "head"), "head": "head", "package_sha256": "a" * 64,
                 "live_fingerprint": "b" * 64, "bound_head": "head", "probe_ok": True}
        self.assertTrue(acc024.idempotence_observables_match(first, dict(first), "head", "b" * 64))
        for field, value in (("nodes", ("root", "extra", "head")), ("head", "extra"),
                             ("package_sha256", "c" * 64), ("live_fingerprint", "d" * 64),
                             ("bound_head", "extra"), ("probe_ok", False)):
            changed = dict(first)
            changed[field] = value
            self.assertFalse(acc024.idempotence_observables_match(first, changed, "head", "b" * 64), field)


if __name__ == "__main__":
    unittest.main()
