using System.ComponentModel.Composition;
using dnSpy.Contracts.Menus;

namespace dnSpy.Extension.MCP {
	/// <summary>Live MCP server toggle shown under dnSpy's Edit menu.</summary>
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "启动 MCP 服务器",
		Group = "1500,5BBDE70E-5A7F-44CE-8A69-633CA5B6B28F", Order = 0)]
	sealed class ToggleMcpServerMenuItem : MenuItemBase {
		readonly McpSettings settings;

		[ImportingConstructor]
		ToggleMcpServerMenuItem(McpSettings settings) => this.settings = settings;

		public override string? GetHeader(IMenuItemContext context) =>
			settings.IsServerRunning ? "停止 MCP 服务器" : "启动 MCP 服务器";

		public override void Execute(IMenuItemContext context) {
			settings.SetServerEnabled(!settings.IsServerRunning);
			var token = settings.ConsumeOneTimeRemoteToken();
			if (token != null)
				McpSettingsControl.ShowOneTimeRemoteToken(System.Windows.Application.Current?.MainWindow, token);
		}
	}
}
