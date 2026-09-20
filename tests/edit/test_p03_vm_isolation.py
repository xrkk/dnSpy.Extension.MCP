#!/usr/bin/env python3
"""Local-only checks for P03's explicit VM evidence isolation entrypoint."""

from __future__ import annotations

import json
import ast
import hashlib
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

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
                              requires_ui=case in ("EDIT-ACC-018", "EDIT-ACC-025"))
                 for case in sorted(CASE_MODULES | HARNESS_CASES)]
        self.assertEqual(30, len(plans))
        self.assertTrue(all(plan["mcp_url"] == "http://127.0.0.1:15400/mcp" for plan in plans))
        self.assertTrue(all(str(plan["artifact_root"]).startswith(str(self.root)) for plan in plans))
        launch_plans = {plan["case_id"]: plan for plan in plans
                        if plan["case_id"] in ("EDIT-ACC-005", "EDIT-ACC-006", "EDIT-ACC-016-CAUSAL")}
        for case_id, folder in (("EDIT-ACC-005", ".acc005-launch"),
                                ("EDIT-ACC-006", ".acc006-launch"),
                                ("EDIT-ACC-016-CAUSAL", ".acc016causal-launch")):
            launch_root = str(self.root / "fixtures" / folder / "local-p03" / "x64")
            self.assertIn(launch_root, launch_plans[case_id]["writes"])
            self.assertIn(launch_root, launch_plans[case_id]["cleanup"])
            self.assertTrue(all(launch_root not in plan["writes"]
                                for plan in plans if plan is not launch_plans[case_id]))
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
        for case_id in ("EDIT-ACC-018", "EDIT-ACC-025"):
            with self.subTest(case_id=case_id):
                loaded = []
                status, summary = run_case(
                    case_id, "local-p03", self.root / "artifacts", isolation=self.context(ui=False),
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

    def test_acc006_load_stage_preserves_original_observation_order(self):
        import p03_vm_acc006 as driver
        responses = iter([
            {"loaded_count": 1, "already_loaded_count": 0, "failed_count": 0,
             "loaded": [{"name": "ImportHost", "path": "X:/ImportHost.exe", "already_loaded": False}], "failed": []},
            {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": "Assembly not found: ImportHost"}},
            {"items": [{"Name": "Main", "Token": 0x06000004}]},
            {"assemblies": [{"Name": "ImportHost"}]},
        ])
        events, sleeps = [], []
        original_call = driver.call
        try:
            driver.call = lambda client, tool, args: (events.append(tool), next(responses))[1]
            entry, wire, probe = driver.observe_loaded_entry(
                object(), "X:/ImportHost.exe",
                lambda: (events.append("tools/list"), "assembly_update entry_point_set")[1],
                delays=(0.25,), sleeper=sleeps.append, monotonic=lambda: 10.0)
        finally:
            driver.call = original_call
        self.assertIn("assembly_update", wire)
        self.assertFalse(probe["immediate_entry_found"])
        self.assertEqual(1, probe["recovered_after_attempt"])
        self.assertEqual(0x06000004, entry["Token"])
        self.assertEqual([0.25], sleeps)
        self.assertEqual(
            ["open_files", "tools/list", "list_methods", "list_methods", "list_assemblies"], events)
        self.assertEqual(250, probe["retry_attempts"][0]["delay_ms"])
        self.assertEqual(250, probe["retry_attempts"][0]["cumulative_delay_ms"])

    def test_acc006_load_stage_does_not_retry_an_immediate_hit(self):
        import p03_vm_acc006 as driver
        responses = iter([
            {"loaded_count": 1, "already_loaded_count": 0, "failed_count": 0,
             "loaded": [{"name": "ImportHost"}], "failed": []},
            {"items": [{"name": "Main", "token": 0x06000004}]},
            {"assemblies": [{"Name": "ImportHost"}]},
        ])
        events, sleeps = [], []
        original_call = driver.call
        try:
            driver.call = lambda client, tool, args: (events.append(tool), next(responses))[1]
            entry, _, probe = driver.observe_loaded_entry(
                object(), "X:/ImportHost.exe", lambda: events.append("tools/list") or "wire",
                sleeper=sleeps.append, monotonic=lambda: 10.0)
        finally:
            driver.call = original_call
        self.assertTrue(probe["immediate_entry_found"])
        self.assertEqual(0, probe["recovered_after_attempt"])
        self.assertEqual(0x06000004, entry["token"])
        self.assertEqual([], sleeps)
        self.assertEqual(["open_files", "tools/list", "list_methods", "list_assemblies"], events)

    def test_acc006_delayed_recovery_keeps_the_case_failed(self):
        import p03_vm_acc006 as driver
        responses = iter([
            {"loaded_count": 1, "already_loaded_count": 0, "failed_count": 0},
            {"ok": False, "error": {"code": "DRIVER_TRANSPORT"}},
            {"items": [{"Name": "Main", "Token": 0x06000004}]},
            {"assemblies": [{"Name": "ImportHost"}]},
        ])
        original_call = driver.call
        original_failures, original_passes = driver.FAILURES[:], driver.PASSES[:]
        try:
            driver.FAILURES.clear()
            driver.PASSES.clear()
            driver.call = lambda client, tool, args: next(responses)
            entry, _, _ = driver.run_load_stage(
                object(), "X:/ImportHost.exe", lambda: '\"assembly_update\" \"entry_point_set\"',
                delays=(0.1,), sleeper=lambda _: None, monotonic=lambda: 10.0)
            self.assertIsNotNone(entry)
            self.assertIn("L1 host entry listed immediately", driver.FAILURES)
            self.assertEqual(1, 0 if not driver.FAILURES else 1)
        finally:
            driver.call = original_call
            driver.FAILURES[:] = original_failures
            driver.PASSES[:] = original_passes

    def test_acc006_launch_copy_preserves_export_response_and_fixture(self):
        import p03_vm_acc006 as driver
        context = self.context()
        fixture = Path(context.fixture("ImportHost/ImportHost.exe"))
        fixture.parent.mkdir(parents=True)
        fixture.write_bytes(b"original fixture")
        export = self.root / "artifacts" / "edit-output" / "acc006" / "ImportHost-p07.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"exported identity bytes")
        digest = hashlib.sha256(export.read_bytes()).hexdigest()
        response = {"ok": True, "result": {"output": {"path": str(export), "sha256": digest}}}
        original = json.loads(json.dumps(response))

        driver.configure_isolation(context)
        launch_path, facts = driver.prepare_isolated_launch(response)

        self.assertEqual(original, response, "the product export response must remain unchanged")
        self.assertEqual(b"original fixture", fixture.read_bytes())
        self.assertEqual(export.read_bytes(), Path(launch_path).read_bytes())
        self.assertEqual(digest, facts["advertised_sha256"])
        self.assertEqual(digest, facts["source_sha256_before"])
        self.assertEqual(digest, facts["source_sha256_after"])
        self.assertEqual(digest, facts["launch_sha256"])
        self.assertTrue(Path(launch_path).is_relative_to(Path(context.fixture_root)))
        self.assertIn(context.run_id, Path(launch_path).parts)

    def test_acc005_launch_copy_preserves_export_response_and_fixture(self):
        import p03_vm_acc005full as driver
        context = self.context()
        fixture = Path(context.fixture("ImportHost/ImportHost.exe"))
        fixture.parent.mkdir(parents=True)
        fixture.write_bytes(b"original fixture")
        export = self.root / "artifacts" / "edit-output" / "acc005full" / "ImportHost-edited.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"exported method bytes")
        digest = hashlib.sha256(export.read_bytes()).hexdigest()
        response = {"ok": True, "result": {"output": {"path": str(export), "sha256": digest}}}
        original = json.loads(json.dumps(response))

        driver.configure_isolation(context)
        launch_path, facts = driver.prepare_isolated_launch(response)

        self.assertEqual(original, response)
        self.assertEqual(b"original fixture", fixture.read_bytes())
        self.assertEqual(export.read_bytes(), Path(launch_path).read_bytes())
        self.assertEqual(digest, facts["launch_sha256"])
        self.assertTrue(Path(launch_path).is_relative_to(Path(context.fixture_root)))
        self.assertIn(context.run_id, Path(launch_path).parts)

    def test_acc006_rejects_writable_launch_root_outside_isolation(self):
        import p03_vm_acc006 as driver
        context = self.context()
        object.__setattr__(context, "fixture_root", str(self.root.parent / "external-fixtures"))
        context.validate()  # Read-only fixture use by other cases remains compatible.

        with self.assertRaisesRegex(IsolationError, "fixture_root must be below isolation_root"):
            driver.configure_isolation(context)

    def test_acc006_windows_launch_root_is_a_run_scoped_fixture_child(self):
        context = IsolationContext(
            run_id="win-run", architecture="x86", mcp_url="http://127.0.0.1:15401/mcp",
            isolation_root=r"D:\T013\run", fixture_root=r"D:\T013\run\fixtures",
            artifact_root=r"D:\T013\run\artifacts", checkpoint_store=r"D:\T013\run\checkpoints",
            work_root=r"D:\T013\run\work", harness_dir=r"D:\T013\run\harness",
            dotnet_host=r"D:\dotnet-x86\dotnet.exe")

        self.assertEqual(
            r"D:\T013\run\fixtures\.acc006-launch\win-run\x86",
            context.fixture_output(".acc006-launch/win-run/x86"))

    def test_acc006_launch_copy_rejects_advertised_mismatch_without_copy(self):
        import p03_vm_acc006 as driver
        context = self.context()
        export = self.root / "artifacts" / "ImportHost-p07.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"exported identity bytes")
        response = {"ok": True, "result": {"output": {"path": str(export), "sha256": "0" * 64}}}
        driver.configure_isolation(context)

        with self.assertRaisesRegex(driver.LaunchPreparationError, "advertised SHA"):
            driver.prepare_isolated_launch(response)

        self.assertFalse((Path(context.fixture_root) / ".acc006-launch").exists())

    def test_acc006_launch_copy_rejects_corrupt_copy(self):
        import p03_vm_acc006 as driver
        context = self.context()
        export = self.root / "artifacts" / "ImportHost-p07.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"exported identity bytes")
        digest = hashlib.sha256(export.read_bytes()).hexdigest()
        response = {"ok": True, "result": {"output": {"path": str(export), "sha256": digest}}}
        driver.configure_isolation(context)

        def corrupt_copy(_source, destination):
            Path(destination).write_bytes(b"corrupt")

        with patch.object(driver.shutil, "copyfile", side_effect=corrupt_copy), \
                self.assertRaisesRegex(driver.LaunchPreparationError, "copied SHA"):
            driver.prepare_isolated_launch(response)

    def test_acc006_launch_copy_does_not_overwrite_a_prior_run(self):
        import p03_vm_acc006 as driver
        context = self.context()
        export = self.root / "artifacts" / "ImportHost-p07.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"first export")
        digest = hashlib.sha256(export.read_bytes()).hexdigest()
        response = {"ok": True, "result": {"output": {"path": str(export), "sha256": digest}}}
        driver.configure_isolation(context)
        launch_path, _ = driver.prepare_isolated_launch(response)
        first = Path(launch_path).read_bytes()

        with self.assertRaisesRegex(driver.LaunchPreparationError, "already exists"):
            driver.prepare_isolated_launch(response)

        self.assertEqual(first, Path(launch_path).read_bytes())

    def test_acc006_identity_mismatch_stops_before_open_or_debug_launch(self):
        import p03_vm_acc006 as driver
        context = self.context()
        fixture = Path(context.fixture("ImportHost/ImportHost.exe"))
        fixture.parent.mkdir(parents=True)
        fixture.write_bytes(b"fixture")
        export = self.root / "artifacts" / "ImportHost-p07.exe"
        export.parent.mkdir(parents=True)
        export.write_bytes(b"export")
        calls = []

        class FakeClient:
            session_id = "test-session"

            def __init__(self, *args, **kwargs):
                pass

            def initialize(self):
                calls.append("initialize")

        def fake_call(client, tool, args):
            calls.append(tool)
            if tool == "open_files":
                return {"loaded_count": 1, "already_loaded_count": 0, "failed_count": 0}
            if tool == "list_methods":
                return {"items": [{"name": "Main", "token": 0x06000004}]}
            if tool == "list_assemblies":
                return {"assemblies": [{"Name": "ImportHost"}]}
            if tool == "edit_begin":
                return {"ok": True, "result": {"transaction": {"transaction_id": "tx", "work_revision": 0}}}
            if tool == "edit_apply":
                if args["operation"].get("version") == "not-a-version":
                    return {"ok": False, "error": {"code": "EDIT_VALIDATION_FAILED"}}
                return {"ok": True, "result": {"transaction": {"work_revision": args["expected_revision"] + 1}}}
            if tool == "edit_status":
                return {"ok": True, "result": {"fingerprints": {"private": "same"}}}
            if tool == "edit_review":
                return {"ok": True, "result": {"review": {"review_id": "review", "required_confirmation_ids": []}}}
            if tool == "edit_commit":
                return {"ok": True, "result": {"history": {"lineage_id": "lineage"},
                                                 "checkpoint": {"checkpoint_id": "checkpoint"}}}
            if tool == "edit_export":
                return {"ok": True, "result": {"output": {"path": str(export), "sha256": "0" * 64}}}
            if tool == "debug_launch":
                self.fail("debug_launch must not run after identity verification fails")
            raise AssertionError(f"unexpected tool after export identity failure: {tool}")

        original_client = driver.DnSpyClient
        original_call = driver.call
        original_wire = driver._tools_list_wire
        original_failures, original_passes = driver.FAILURES[:], driver.PASSES[:]
        try:
            driver.FAILURES.clear()
            driver.PASSES.clear()
            driver.configure_isolation(context)
            driver.DnSpyClient = FakeClient
            driver.call = fake_call
            driver._tools_list_wire = lambda client: '"assembly_update" "entry_point_set"'
            self.assertEqual(1, driver.main())
        finally:
            driver.DnSpyClient = original_client
            driver.call = original_call
            driver._tools_list_wire = original_wire
            driver.FAILURES[:] = original_failures
            driver.PASSES[:] = original_passes
        self.assertNotIn("debug_launch", calls)
        self.assertEqual(1, calls.count("open_files"), "only the original fixture may be opened")

    def test_acc006_nonisolated_entry_fails_before_client_or_rpc(self):
        import p03_vm_acc006 as driver

        class UnexpectedClient:
            def __init__(self, *args, **kwargs):
                self.fail("client must not be created without an isolation context")

        original_root = driver.LAUNCH_ROOT
        original_client = driver.DnSpyClient
        original_failures, original_passes = driver.FAILURES[:], driver.PASSES[:]
        try:
            driver.LAUNCH_ROOT = None
            driver.DnSpyClient = UnexpectedClient
            driver.FAILURES.clear()
            driver.PASSES.clear()
            self.assertEqual(1, driver.main())
            self.assertEqual(["C0 isolated launch root configured"], driver.FAILURES)
        finally:
            driver.LAUNCH_ROOT = original_root
            driver.DnSpyClient = original_client
            driver.FAILURES[:] = original_failures
            driver.PASSES[:] = original_passes

    def test_acc006_main_order_and_no_recovery_stop_before_edit_side_effects(self):
        import p03_vm_acc006 as driver

        calls = []

        class FakeClient:
            session_id = "test-session"

            def __init__(self, *args, **kwargs):
                pass

            def initialize(self):
                calls.append("initialize")

        original_client = driver.DnSpyClient
        original_call = driver.call
        original_wire = driver._tools_list_wire
        original_failures, original_passes = driver.FAILURES[:], driver.PASSES[:]
        try:
            driver.FAILURES.clear()
            driver.PASSES.clear()
            driver.configure_isolation(self.context())
            driver.DnSpyClient = FakeClient
            driver._tools_list_wire = lambda client: calls.append("tools/list") or '\"assembly_update\" \"entry_point_set\"'

            def fake_call(client, tool, args):
                calls.append(tool)
                if tool == "open_files":
                    return {"loaded_count": 1, "already_loaded_count": 0, "failed_count": 0}
                if tool == "list_assemblies":
                    return {"assemblies": []}
                return {"ok": False, "error": {"code": "DRIVER_TRANSPORT"}}

            driver.call = fake_call
            self.assertEqual(1, driver.main())
        finally:
            driver.DnSpyClient = original_client
            driver.call = original_call
            driver._tools_list_wire = original_wire
            driver.FAILURES[:] = original_failures
            driver.PASSES[:] = original_passes
        self.assertEqual("open_files", calls[1])
        self.assertEqual("tools/list", calls[2])
        self.assertEqual("list_methods", calls[3])
        self.assertNotIn("edit_begin", calls)
        self.assertNotIn("edit_apply", calls)


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
