"""Static regression checks for WPF settings bindings."""

from pathlib import Path
import unittest
import xml.etree.ElementTree as ET


REPO_ROOT = Path(__file__).resolve().parents[2]
PRESENTATION_NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"


class SettingsUiBindingTests(unittest.TestCase):
    def test_read_only_token_verifier_binding_is_one_way(self) -> None:
        root = ET.parse(REPO_ROOT / "McpSettingsControl.xaml").getroot()
        text_boxes = root.findall(f".//{{{PRESENTATION_NS}}}TextBox")
        matches = [
            box
            for box in text_boxes
            if "RemoteTokenVerifier" in box.attrib.get("Text", "")
        ]

        self.assertEqual(1, len(matches))
        binding = matches[0].attrib["Text"]
        self.assertIn("Mode=OneWay", binding)
        self.assertEqual("True", matches[0].attrib.get("IsReadOnly"))

    def test_settings_page_uses_chinese_labels(self) -> None:
        root = ET.parse(REPO_ROOT / "McpSettingsControl.xaml").getroot()
        visible_text = {
            value
            for element in root.iter()
            for name, value in element.attrib.items()
            if name in {"Content", "Text", "Header"}
        }

        expected = {
            "启用 MCP 服务器",
            "MCP 服务器配置",
            "远程访问安全",
            "动态调试",
            "服务器日志",
            "复制所有日志到剪贴板",
        }
        self.assertTrue(expected.issubset(visible_text))

    def test_python_client_default_port_is_15378(self) -> None:
        paths = [
            REPO_ROOT / "dnspy_mcp" / "client.py",
            REPO_ROOT / "dnspy_mcp" / "cli.py",
            REPO_ROOT / "dnspy_mcp" / "stdio.py",
            REPO_ROOT / "dnspy_mcp" / "http_cli.py",
        ]
        for path in paths:
            with self.subTest(path=path.name):
                source = path.read_text(encoding="utf-8")
                self.assertIn("http://localhost:15378/", source)
                self.assertNotIn("http://localhost:3000/", source)

    def test_requested_defaults_and_controls_are_present(self) -> None:
        xaml = (REPO_ROOT / "McpSettingsControl.xaml").read_text(encoding="utf-8")
        settings = (REPO_ROOT / "McpSettings.cs").read_text(encoding="utf-8")
        snapshot = (REPO_ROOT / "McpSettingsSnapshot.cs").read_text(encoding="utf-8")
        menu = (REPO_ROOT / "McpServerMenuCommands.cs").read_text(encoding="utf-8")

        self.assertIn('string host = "192.168.204.149"', settings)
        self.assertIn('string remoteAllowedCidrsText = McpSettingsSnapshot.TrustedHostOnlyPeerCidr', settings)
        self.assertIn('"dnspy-mcp-artifacts"', snapshot)
        self.assertIn('Content="要求 Bearer Token"', xaml)
        self.assertIn('192.168.204.1/32', xaml)
        self.assertIn('Content="浏览目录…"', xaml)
        self.assertIn('Content="{Binding ServerActionText}"', xaml)
        self.assertIn('允许的样本目录留空表示不限制', xaml)
        self.assertIn('MenuConstants.APP_MENU_EDIT_GUID', menu)
        self.assertIn('settings.IsServerRunning ? "停止 MCP 服务器" : "启动 MCP 服务器"', menu)

    def test_token_dialog_explains_one_time_retrieval(self) -> None:
        source = (REPO_ROOT / "McpSettingsControl.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("MCP 远程 Token（仅显示一次）", source)
        self.assertIn("DNSPY_MCP_TOKEN", source)
        self.assertIn("Token 已复制到剪贴板", source)
        self.assertIn("owner != null && owner.IsVisible", source)

    def test_token_rotation_button_is_constrained_to_the_visible_grid(self) -> None:
        root = ET.parse(REPO_ROOT / "McpSettingsControl.xaml").getroot()
        buttons = root.findall(f".//{{{PRESENTATION_NS}}}Button")
        rotate = [button for button in buttons if button.attrib.get("Content") == "应用时轮换"]

        self.assertEqual(1, len(rotate))
        parent = next(element for element in root.iter() if rotate[0] in list(element))
        self.assertEqual(f"{{{PRESENTATION_NS}}}Grid", parent.tag)
        self.assertEqual("1", rotate[0].attrib.get("Grid.Column"))

    def test_tokenless_remote_keeps_remote_transport_guards(self) -> None:
        server = (REPO_ROOT / "McpServer.cs").read_text(encoding="utf-8")
        accept_loop = server[server.index("void AcceptLoop()") : server.index("void HandleHttpRequest")]

        self.assertIn("bool remote = snapshot.IsRemote;", server)
        self.assertLess(
            accept_loop.index("!CidrFilter.IsAllowed"),
            accept_loop.index("verifier != null && !RemoteTokenAuth.Verify"),
        )
        self.assertIn("if (activeSnapshot?.IsRemote != true)", server)


if __name__ == "__main__":
    unittest.main()
