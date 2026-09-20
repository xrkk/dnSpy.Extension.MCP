import importlib
import sys
import unittest
from pathlib import Path
from pathlib import PureWindowsPath
from types import SimpleNamespace

EDIT_DIR = Path(__file__).resolve().parent
if str(EDIT_DIR) not in sys.path:
    sys.path.insert(0, str(EDIT_DIR))


class T016LifecycleDriverTests(unittest.TestCase):
    def test_independent_runner_route(self):
        runner = importlib.import_module("p03_vm_edit_acc_evidence")
        self.assertEqual("p03_vm_acc002_lifecycle",
                         runner.CASE_MODULES["EDIT-ACC-002-LIFECYCLE"])
        self.assertEqual("p03_vm_acc002_listener",
                         runner.CASE_MODULES["EDIT-ACC-002-LISTENER"])

    def test_isolation_configures_url_fixture_and_signal_root(self):
        driver = importlib.import_module("p03_vm_acc002_lifecycle")
        context = SimpleNamespace(
            mcp_url="http://127.0.0.1:15730/mcp",
            work_root=r"C:\Tools\dnspy-fix-20260919-t016\x64\work\acc002-lifecycle",
            fixture=lambda relative: str(PureWindowsPath(
                r"C:\Tools\dnspy-fix-20260919-t016\x64\fixtures") / relative),
        )
        driver.configure_isolation(context)
        self.assertEqual(context.mcp_url, driver.URL)
        self.assertEqual(context.work_root, str(driver.WORK_ROOT))
        self.assertTrue(driver.FIXTURE.endswith(r"fixtures\TestIL.dll"))

    def test_same_process_auto_recovery_rejects_replacement_or_manual_initialize(self):
        driver = importlib.import_module("p03_vm_acc002_lifecycle")
        status = {"ok": True, "result": {"state": "idle"}}
        self.assertTrue(driver.same_process_auto_recovery_pass(
            old_session="old", new_session="new", pid_before=41, pid_after=41,
            automatic_raw={"result": {}}, automatic_status=status,
            explicit_recovery_used=False))
        self.assertFalse(driver.same_process_auto_recovery_pass(
            old_session="old", new_session="new", pid_before=41, pid_after=42,
            automatic_raw={"result": {}}, automatic_status=status,
            explicit_recovery_used=False), "a replacement bridge process must not satisfy CON-007")
        self.assertFalse(driver.same_process_auto_recovery_pass(
            old_session="old", new_session="new", pid_before=41, pid_after=41,
            automatic_raw={"error": {}}, automatic_status=status,
            explicit_recovery_used=True), "manual initialize must not be reported as automatic recovery")

    def test_uncertain_request_requires_dropped_success_and_exactly_one_upstream_receive(self):
        driver = importlib.import_module("p03_vm_acc002_lifecycle")
        receipt = {"upstream_status": 200, "response_dropped": True}
        values = dict(
            ambiguous_raw={"error": {"code": -32000}}, receipts_before=[receipt],
            receipts_after=[receipt], old_url_raw={"error": {"code": -32000}},
            new_status={"ok": True, "result": {"state": "idle"}},
            search={"items": []}, new_url_mutations=[])
        self.assertTrue(driver.uncertain_request_pass(**values))
        self.assertFalse(driver.uncertain_request_pass(
            **{**values, "ambiguous_raw": {"result": {"confirmed": True}}}),
            "a confirmed response is not an uncertain request result")
        self.assertFalse(driver.uncertain_request_pass(
            **{**values, "receipts_after": [receipt, receipt]}),
            "an automatic replay must fail the acceptance check")
        self.assertFalse(driver.uncertain_request_pass(
            **{**values, "search": {"ok": True, "result": {"items": []}}}),
            "search_types is a direct result, not an edit envelope")


if __name__ == "__main__":
    unittest.main()
