"""Guards for the isolated live listener and UI target entry points."""

import unittest
import tempfile
from pathlib import Path
from unittest.mock import patch

import ui_apply_settings as ui
import run_p02_listener_tests as listener


class ListenerIsolationTests(unittest.TestCase):
    def test_cli_rejects_shared_port_and_path_escape_before_writing(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "owned"
            root.mkdir()
            output = root / "report.json"
            stdout = root / "stdout.log"
            stderr = root / "stderr.log"
            base = ["listener", "--output", str(output), "--ui-signal-dir", str(root / "signals"),
                    "--old-port", "16441", "--new-port", "16442", "--isolation-root", str(root),
                    "--fixture", str(root / "TestIL.dll"), "--stdout-log", str(stdout),
                    "--stderr-log", str(stderr)]
            for argv in (base[:base.index("16441")] + ["15378"] + base[base.index("16441")+1:],
                         base[:base.index(str(output))] + [str(Path(temp) / "outside.json")] + base[base.index(str(output))+1:]):
                with self.subTest(argv=argv), patch.object(listener.sys, "argv", argv):
                    with self.assertRaises(SystemExit):
                        listener.main()
                self.assertFalse(stdout.exists())
                self.assertFalse(stderr.exists())

    def test_ui_guard_rechecks_creation_ticks_before_control_access(self):
        target = {"pid": 3141, "path": r"E:\owned\dnSpy.exe", "ticks": 639257666931766730}
        script = ui._uia_prelude(target)
        self.assertIn("ProcessId=' + $p.Id", script)
        self.assertIn("639257666931766730", script)
        self.assertLess(script.index("target creation ticks mismatch"), script.index("FromHandle"))

    def test_explicit_target_ticks_are_checked_in_resolution(self):
        scripts = []
        def fake(_client, script):
            scripts.append(script)
            return {"ok": False, "reason": "identity-mismatch", "path": r"E:\owned\dnSpy.exe"}
        with patch.object(ui, "_ps_json", side_effect=fake):
            with self.assertRaisesRegex(RuntimeError, "identity-mismatch"):
                ui.resolve_target(None, 3141, r"E:\owned\dnSpy.exe", 123)
        self.assertIn("$ticks -eq 123", scripts[0])

    def test_direct_ui_fallback_refuses_without_signal(self):
        previous = listener.UI_SIGNAL_DIR
        try:
            listener.UI_SIGNAL_DIR = None
            with self.assertRaisesRegex(RuntimeError, "signal directory is required"):
                listener.ui_apply(16441)
        finally:
            listener.UI_SIGNAL_DIR = previous


if __name__ == "__main__":
    unittest.main()
