"""Acceptance guards for ambiguous dialogs and coordinate fallback ordering."""
import unittest
from unittest.mock import patch
import ui_apply_settings as ui


class SettingsGuardTests(unittest.TestCase):
    def test_only_explicit_zero_means_dialog_closed(self):
        for count in (2, -1, None, False, "0"):
            with self.subTest(count=count), patch.object(ui, "_ps_json", return_value={"n": count}):
                with self.assertRaises(RuntimeError):
                    ui._options_window_present(None, {"pid": 123, "path": "C:/owned.exe"})
        for count in (0, 1):
            with patch.object(ui, "_ps_json", return_value={"n": count}):
                self.assertEqual(bool(count), ui._options_window_present(None, {"pid": 123, "path": "C:/owned.exe"}))

    def test_hit_identity_is_checked_before_mouse_down(self):
        scripts = []
        def invoke(client, script):
            scripts.append(script)
            if "@{count=$items.Count;ids=$ids}" in script:
                return {"count": 1, "ids": ["1.2"]}
            if "@{selected=$true" in script:
                # The generated fallback must refuse a foreign hit before injecting input.
                self.assertLess(script.index("::FromPoint("), script.index("::mouse_event(2"))
                self.assertLess(script.index("throw ('FromPoint hit"), script.index("::mouse_event(2"))
                return {"selected": True}
            return {"n": 1}
        with patch.object(ui, "_ps_json", side_effect=invoke), patch.object(ui.time, "sleep"):
            ui.select_mcp_page(None, {"pid": 123, "path": "C:/owned.exe"})
        self.assertEqual(3, len(scripts))


if __name__ == "__main__":
    unittest.main()
