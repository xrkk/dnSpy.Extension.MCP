#!/usr/bin/env python3
"""Local-only checks for P03's explicit VM evidence isolation entrypoint."""

from __future__ import annotations

import json
import ast
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

from p03_vm_edit_acc_evidence import CASE_MODULES, HARNESS_CASES, run_case, run_harness_case
from p03_vm_isolation import IsolationContext, IsolationError


class P03IsolationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tempdir = tempfile.TemporaryDirectory()
        self.root = Path(self.tempdir.name) / "isolated-run"

    def tearDown(self) -> None:
        self.tempdir.cleanup()

    def context(self, *, url: str = "http://127.0.0.1:15400/mcp", ui: bool = True) -> IsolationContext:
        return IsolationContext(
            run_id="local-p03", architecture="x64", mcp_url=url,
            isolation_root=str(self.root), fixture_root=str(self.root / "fixtures"),
            artifact_root=str(self.root / "artifacts"), checkpoint_store=str(self.root / "checkpoints"),
            work_root=str(self.root / "work"), harness_dir=str(self.root / "harness"),
            dotnet_host=str(self.root / "dotnet" / "dotnet.exe"),
            ui_deployment_root=str(self.root / "ui") if ui else None)

    def test_every_case_has_a_nonexecuting_plan(self) -> None:
        context = self.context()
        plans = [context.plan(case, harness=case in HARNESS_CASES,
                              requires_ui=case == "EDIT-ACC-018")
                 for case in sorted(CASE_MODULES | HARNESS_CASES)]
        self.assertEqual(20, len(plans))
        self.assertTrue(all(plan["mcp_url"] == "http://127.0.0.1:15400/mcp" for plan in plans))
        self.assertTrue(all(str(plan["artifact_root"]).startswith(str(self.root)) for plan in plans))
        self.assertFalse(self.root.exists(), "planning must not create a run directory")

    def test_every_supported_integration_driver_declares_the_context_hook(self) -> None:
        driver_dir = Path(__file__).resolve().parent
        for module_name in CASE_MODULES.values():
            tree = ast.parse((driver_dir / f"{module_name}.py").read_text(encoding="utf-8"))
            self.assertIn("configure_isolation", {node.name for node in tree.body if isinstance(node, ast.FunctionDef)})

    def test_shared_port_and_paths_are_rejected_before_execution(self) -> None:
        with self.assertRaisesRegex(IsolationError, "shared default port"):
            self.context(url="http://127.0.0.1:15378/mcp").validate()
        bad = self.context()
        object.__setattr__(bad, "artifact_root", r"C:\Tools\dnspy-mcp-edit-tests\artifacts")
        with self.assertRaisesRegex(IsolationError, "shared C"):
            bad.validate()

    def test_missing_ui_root_blocks_before_driver_load_or_write(self) -> None:
        loaded = []
        status, summary = run_case(
            "EDIT-ACC-018", "local-p03", self.root / "artifacts", isolation=self.context(ui=False),
            module_loader=lambda name: loaded.append(name))
        self.assertEqual("blocked", status)
        self.assertIn("ui_deployment_root", summary["reason"])
        self.assertEqual([], loaded)
        self.assertFalse((self.root / "artifacts").exists())

    def test_parent_traversal_and_run_id_escape_are_rejected(self):
        for field, value in (("artifact_root", str(self.root / "artifacts" / ".." / ".." / "outside")),
                             ("run_id", "../../outside"), ("isolation_root", "relative")):
            context = self.context()
            object.__setattr__(context, field, value)
            with self.assertRaises(IsolationError):
                context.validate()
        self.assertFalse(self.root.exists())

    def test_existing_link_cannot_redirect_evidence(self):
        self.root.mkdir()
        (self.root / "artifacts").symlink_to(self.root.parent, target_is_directory=True)
        with self.assertRaisesRegex(IsolationError, "outside"):
            self.context().validate()

    def test_runner_cannot_override_validated_output_or_architecture(self):
        loaded = []
        status, _ = run_case("EDIT-ACC-004", "../../escape", self.root.parent,
                             isolation=self.context(), module_loader=lambda name: loaded.append(name))
        self.assertEqual("blocked", status)
        self.assertEqual([], loaded)
        self.assertFalse(self.root.exists())

    def test_real_acc023_context_rebinds_source_and_dynamic_fixture(self):
        import p03_vm_acc023 as driver
        context = self.context()
        for arch in ("x64", "x86"):
            object.__setattr__(context, "architecture", arch)
            driver.configure_isolation(context)
            self.assertIn(context.work_file("p09-sentinel.flag"), driver.MALICIOUS_SOURCE)
            self.assertNotIn(driver.LEGACY_SENTINEL, driver.MALICIOUS_SOURCE)
            folder = "ImportHost" if arch == "x64" else "ImportHost-x86"
            self.assertEqual(context.fixture(folder + "/ImportHost.exe"), driver.ISOLATED_LAUNCH)
            self.assertEqual(arch, driver.ARCH)


    def test_mixed_pass_and_blocked_cli_is_not_success(self):
        from unittest.mock import patch
        import p03_vm_edit_acc_evidence as runner
        with patch("sys.argv", ["runner", "--case", "EDIT-ACC-004", "--case", "EDIT-ACC-005"]), \
                patch.object(runner, "run_case", side_effect=[("pass", {}), ("blocked", {})]):
            self.assertEqual(2, runner.main())

    def test_windows_parent_traversal_is_rejected_on_linux(self):
        context = self.context()
        object.__setattr__(context, "isolation_root", r"D:\T006\run")
        object.__setattr__(context, "artifact_root", r"D:\T006\run\..\outside")
        with self.assertRaises(IsolationError):
            context.validate()


    def test_fake_rpc_driver_receives_selected_context(self) -> None:
        observed = []

        class FakeClient:
            def close(self):
                pass

        module = SimpleNamespace()
        module.DnSpyClient = FakeClient
        module.call = lambda client, tool, args: {"ok": True, "result": {"tool": tool}}
        module.configure_isolation = lambda context: observed.append(context)

        def fake_main():
            module.call(module.DnSpyClient(), "edit_status", {})
            print("PASS fake RPC")
            return 0

        module.main = fake_main
        status, summary = run_case(
            "EDIT-ACC-004", "local-p03", self.root / "artifacts", isolation=self.context(),
            module_loader=lambda name: module)
        self.assertEqual("pass", status)
        self.assertEqual(str(self.root / "artifacts"), observed[0].artifact_root)
        actions = self.root / "artifacts" / "edit-tests" / "local-p03" / "EDIT-ACC-004" / "actions.jsonl"
        self.assertEqual("edit_status", json.loads(actions.read_text(encoding="utf-8"))["tool"])
        self.assertEqual(1, summary["artifacts"]["actions"]["calls"])

    def test_fake_harness_uses_selected_host_fixture_and_directory(self) -> None:
        observed = {}

        def fake_subprocess(command, **kwargs):
            observed["command"] = command
            observed["cwd"] = kwargs["cwd"]
            return SimpleNamespace(stdout="PASS fake harness\n", stderr="", returncode=0)

        status, _ = run_harness_case(
            "EDIT-ACC-029", "local-p03", self.root / "artifacts", "x64", self.context(),
            subprocess_runner=fake_subprocess)
        self.assertEqual("pass", status)
        self.assertEqual(str(self.root / "dotnet" / "dotnet.exe"), observed["command"][0])
        self.assertEqual(str(self.root / "fixtures" / "TestIL.dll"), observed["command"][2])
        self.assertEqual(str(self.root / "harness"), observed["cwd"])


if __name__ == "__main__":
    unittest.main()
