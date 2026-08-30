using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// User control for MCP server settings UI.
	/// </summary>
	public partial class McpSettingsControl : UserControl {
		/// <summary>
		/// Initializes the settings control.
		/// </summary>
		public McpSettingsControl() => InitializeComponent();

		void RotateRemoteTokenButton_Click(object sender, RoutedEventArgs e) {
			if (DataContext is not SettingsViewModel viewModel)
				return;
			viewModel.RequestRemoteTokenRotation();
			MessageBox.Show("成功应用这些设置时，将生成新的 Token，并且只显示一次。",
				"MCP 远程 Token", MessageBoxButton.OK, MessageBoxImage.Information);
		}

		void ToggleServerButton_Click(object sender, RoutedEventArgs e) {
			if (DataContext is not SettingsViewModel viewModel)
				return;
			var token = viewModel.ToggleServer();
			if (token != null)
				ShowOneTimeRemoteToken(Window.GetWindow(this), token);
		}

		void BrowseArtifactRootButton_Click(object sender, RoutedEventArgs e) {
			if (DataContext is SettingsViewModel viewModel)
				viewModel.BrowseArtifactRoot();
		}

		/// <summary>Shows and copies a raw remote token without ever logging or persisting it.</summary>
		internal static void ShowOneTimeRemoteToken(Window? owner, string token) {
			bool copied = TryCopyToClipboard(token);
			var window = new Window {
				Title = "MCP 远程 Token（仅显示一次）",
				Width = 680,
				Height = 245,
				WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
				ResizeMode = ResizeMode.NoResize,
			};
			// AppSettingsPage.OnApply can run after dnSpy has already closed its settings window.
			// Assigning that closed Window as Owner throws and would hide the one-time credential.
			if (owner != null && owner.IsVisible) {
				try { window.Owner = owner; }
				catch (InvalidOperationException) { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
			}
			else
				window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
			var panel = new StackPanel { Margin = new Thickness(14) };
			panel.Children.Add(new TextBlock {
				Text = "这是提供给 MCP 客户端的 DNSPY_MCP_TOKEN。原始 Token 不会保存，并且关闭后无法再次查看。",
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 10),
			});
			var tokenBox = new TextBox {
				Text = token,
				IsReadOnly = true,
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				Margin = new Thickness(0, 0, 0, 8),
			};
			panel.Children.Add(tokenBox);
			var status = new TextBlock {
				Text = copied ? "Token 已复制到剪贴板。请立即粘贴到 ZCode/Codex 的 DNSPY_MCP_TOKEN 环境变量。"
					: "自动复制失败。请在上方文本框中按 Ctrl+C 复制。",
				Foreground = copied ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkOrange,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 10),
			};
			panel.Children.Add(status);
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			var copy = new Button { Content = "复制 Token", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
			copy.Click += (s, e) => {
				if (TryCopyToClipboard(token)) {
					status.Text = "Token 已复制到剪贴板。";
					status.Foreground = System.Windows.Media.Brushes.DarkGreen;
				}
			};
			buttons.Children.Add(copy);
			var close = new Button { Content = "关闭", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
			close.Click += (s, e) => window.Close();
			buttons.Children.Add(close);
			panel.Children.Add(buttons);
			window.Content = panel;
			window.Loaded += (s, e) => { tokenBox.SelectAll(); tokenBox.Focus(); };
			window.ShowDialog();
		}

		/// <summary>
		/// Handles the copy logs button click, copying all logs to clipboard with retry logic.
		/// </summary>
		void CopyLogsButton_Click(object sender, RoutedEventArgs e) {
			try {
				if (DataContext is SettingsViewModel viewModel && viewModel.LogMessages != null) {
					var allLogs = string.Join(Environment.NewLine, viewModel.LogMessages);
					if (string.IsNullOrEmpty(allLogs)) {
						MessageBox.Show("没有可复制的日志。",
							"无日志",
							MessageBoxButton.OK,
							MessageBoxImage.Information);
						return;
					}

					// Try to copy to clipboard with retries
					bool copied = TryCopyToClipboard(allLogs);

					if (copied) {
						MessageBox.Show($"已将 {viewModel.LogMessages.Count} 条日志复制到剪贴板。",
							"日志已复制",
							MessageBoxButton.OK,
							MessageBoxImage.Information);
					} else {
						// Fallback: Show logs in a window
						ShowLogsWindow(allLogs, viewModel.LogMessages.Count);
					}
				} else {
					MessageBox.Show("无法访问日志消息。",
						"错误",
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}
			}
			catch (Exception ex) {
				MessageBox.Show($"访问日志失败：{ex.Message}",
					"错误",
					MessageBoxButton.OK,
					MessageBoxImage.Error);
			}
		}

		static bool TryCopyToClipboard(string text) {
			// Try multiple times with delays
			for (int i = 0; i < 5; i++) {
				try {
					Clipboard.SetDataObject(text, true);
					return true;
				}
				catch (System.Runtime.InteropServices.COMException) {
					// Clipboard is locked, wait and retry
					Thread.Sleep(100);
				}
				catch {
					// Other error, give up
					return false;
				}
			}
			return false;
		}

		void ShowLogsWindow(string logs, int count) {
			var window = new Window {
				Title = $"MCP 服务器日志（{count} 条）",
				Width = 800,
				Height = 600,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Owner = Window.GetWindow(this)
			};

			var grid = new Grid();
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			var textBox = new TextBox {
				Text = logs,
				IsReadOnly = true,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
				FontFamily = new System.Windows.Media.FontFamily("Consolas"),
				FontSize = 11,
				TextWrapping = TextWrapping.NoWrap,
				Margin = new Thickness(10)
			};
			Grid.SetRow(textBox, 0);
			grid.Children.Add(textBox);

			var buttonPanel = new StackPanel {
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(10)
			};
			Grid.SetRow(buttonPanel, 1);

			var copyButton = new Button {
				Content = "再次尝试复制",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			copyButton.Click += (s, e) => {
				if (TryCopyToClipboard(logs)) {
					MessageBox.Show("日志已成功复制到剪贴板。",
						"成功",
						MessageBoxButton.OK,
						MessageBoxImage.Information);
					window.Close();
				} else {
					MessageBox.Show("剪贴板仍被占用。请尝试关闭可能正在使用剪贴板的其他应用程序。",
						"剪贴板被占用",
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}
			};
			buttonPanel.Children.Add(copyButton);

			var selectAllButton = new Button {
				Content = "全选（按 Ctrl+C 复制）",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			selectAllButton.Click += (s, e) => {
				textBox.SelectAll();
				textBox.Focus();
			};
			buttonPanel.Children.Add(selectAllButton);

			var closeButton = new Button {
				Content = "关闭",
				Padding = new Thickness(20, 5, 20, 5),
				Margin = new Thickness(5)
			};
			closeButton.Click += (s, e) => window.Close();
			buttonPanel.Children.Add(closeButton);

			grid.Children.Add(buttonPanel);
			window.Content = grid;

			window.ShowDialog();
		}
	}
}
