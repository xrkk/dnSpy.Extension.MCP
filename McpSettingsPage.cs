using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Provider for the MCP server settings page in dnSpy settings dialog.
	/// </summary>
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class McpAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly McpSettings mcpSettings;
		readonly IPickDirectory pickDirectory;

		/// <summary>
		/// Initializes the settings page provider.
		/// </summary>
		[ImportingConstructor]
		McpAppSettingsPageProvider(McpSettings mcpSettings, IPickDirectory pickDirectory) {
			this.mcpSettings = mcpSettings;
			this.pickDirectory = pickDirectory;
		}

		/// <summary>
		/// Creates the settings page.
		/// </summary>
		public IEnumerable<AppSettingsPage> Create() {
			yield return new McpAppSettingsPage(mcpSettings, pickDirectory);
		}
	}

	/// <summary>
	/// Settings page for the MCP server in dnSpy settings dialog.
	/// </summary>
	sealed class McpAppSettingsPage : AppSettingsPage {
		static readonly Guid THE_GUID = new Guid("68F555EB-A951-49C1-9708-C8756A5FAC39");

		/// <summary>
		/// Gets the parent settings page GUID (none for top-level page).
		/// </summary>
		public override Guid ParentGuid => Guid.Empty;

		/// <summary>
		/// Gets the unique GUID for this settings page.
		/// </summary>
		public override Guid Guid => THE_GUID;

		/// <summary>
		/// Gets the display order in the settings tree.
		/// </summary>
		public override double Order => AppSettingsConstants.ORDER_DEBUGGER + 0.2;

		/// <summary>
		/// Gets the page title displayed in settings.
		/// </summary>
		public override string Title => "MCP 服务器";

		/// <summary>
		/// Gets the icon displayed next to the page title.
		/// </summary>
		public override ImageReference Icon => DsImages.MarkupTag;

		/// <summary>
		/// Gets the UI control for this settings page.
		/// </summary>
		public override object? UIObject {
			get {
				if (uiObject is null) {
					uiObject = new McpSettingsControl();
					// Use a wrapper that combines editable settings with live logs from global settings
					uiObject.DataContext = new SettingsViewModel(newSettings, globalSettings, pickDirectory);
				}
				return uiObject;
			}
		}
		McpSettingsControl? uiObject;

		readonly McpSettings globalSettings;
		readonly McpSettings newSettings;
		readonly IPickDirectory pickDirectory;

		/// <summary>
		/// Initializes the settings page with the given settings instance.
		/// </summary>
		public McpAppSettingsPage(McpSettings mcpSettings, IPickDirectory pickDirectory) {
			globalSettings = mcpSettings;
			this.pickDirectory = pickDirectory;
			newSettings = mcpSettings.Clone();
		}

		/// <summary>
		/// Applies the settings when user clicks OK: one snapshot transaction, never per-setter writes.
		/// </summary>
		public override void OnApply() {
			globalSettings.ApplyEdited(newSettings);
			var token = globalSettings.ConsumeOneTimeRemoteToken();
			if (token != null)
				McpSettingsControl.ShowOneTimeRemoteToken(uiObject == null ? null : System.Windows.Window.GetWindow(uiObject), token);
		}

		/// <summary>
		/// Called when the settings dialog is closed.
		/// </summary>
		public override void OnClosed() {
		}
	}

	/// <summary>
	/// View model for MCP settings that provides editable settings while showing live logs from global settings.
	/// This allows users to see real-time logs even before applying settings changes.
	/// </summary>
	public class SettingsViewModel : dnSpy.Contracts.MVVM.ViewModelBase {
		readonly McpSettings editableSettings;
		readonly McpSettings globalSettings;
		readonly IPickDirectory pickDirectory;

		/// <summary>
		/// Initializes the view model with editable and global settings instances.
		/// </summary>
		public SettingsViewModel(McpSettings editable, McpSettings global, IPickDirectory pickDirectory) {
			editableSettings = editable;
			globalSettings = global;
			this.pickDirectory = pickDirectory;

			// Forward property change notifications from editable settings
			editableSettings.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName ?? string.Empty);

			// Forward property change notifications from global settings (for live logs)
			globalSettings.PropertyChanged += (s, e) => {
				if (e.PropertyName == nameof(LogText) || e.PropertyName == nameof(LogMessages)) {
					OnPropertyChanged(e.PropertyName);
				}
				else if (e.PropertyName == nameof(McpSettings.IsServerRunning)) {
					OnPropertyChanged(nameof(IsServerRunning));
					OnPropertyChanged(nameof(ServerActionText));
					OnPropertyChanged(nameof(ServerStatusText));
				}
			};
		}

		/// <summary>
		/// Gets or sets whether the MCP server is enabled (editable).
		/// </summary>
		public bool EnableServer {
			get => editableSettings.EnableServer;
			set => editableSettings.EnableServer = value;
		}

		/// <summary>
		/// Gets or sets the server host (editable).
		/// </summary>
		public string Host {
			get => editableSettings.Host;
			set => editableSettings.Host = value;
		}

		/// <summary>
		/// Gets or sets the server port (editable).
		/// </summary>
		public int Port {
			get => editableSettings.Port;
			set => editableSettings.Port = value;
		}

		/// <summary>Gets or sets whether the dynamic-debugging tools are enabled (editable).</summary>
		public bool DebugToolsEnabled {
			get => editableSettings.DebugToolsEnabled;
			set => editableSettings.DebugToolsEnabled = value;
		}

		/// <summary>Gets or sets the dedicated-instance acknowledgment (editable).</summary>
		public bool DedicatedDebugInstanceAcknowledged {
			get => editableSettings.DedicatedDebugInstanceAcknowledged;
			set => editableSettings.DedicatedDebugInstanceAcknowledged = value;
		}

		/// <summary>Gets or sets the artifact root path (editable).</summary>
		public string ArtifactRoot {
			get => editableSettings.ArtifactRoot;
			set => editableSettings.ArtifactRoot = value;
		}

		/// <summary>Gets or sets the allowed sample root path (editable).</summary>
		public string AllowedSampleRoot {
			get => editableSettings.AllowedSampleRoot;
			set => editableSettings.AllowedSampleRoot = value;
		}

		public string RemoteAllowedCidrsText {
			get => editableSettings.RemoteAllowedCidrsText;
			set => editableSettings.RemoteAllowedCidrsText = value;
		}

		public bool RemoteTokenRequired {
			get => editableSettings.RemoteTokenRequired;
			set {
				editableSettings.RemoteTokenRequired = value;
				if (!value) {
					editableSettings.RemoteTokenVerifier = null;
					editableSettings.RemoteAllowedCidrsText = McpSettingsSnapshot.TrustedHostOnlyPeerCidr;
					OnPropertyChanged(nameof(RemoteAllowedCidrsText));
				}
				OnPropertyChanged(nameof(RemoteTokenVerifier));
			}
		}

		public string RemoteTokenVerifier => !editableSettings.RemoteTokenRequired
			? $"（免 Token——仅允许 {McpSettingsSnapshot.TrustedHostOnlyPeerCidr}）"
			: editableSettings.RemoteTokenVerifier ?? "（未配置——应用设置时生成）";

		public bool RemoteHostOnlyAcknowledged {
			get => editableSettings.RemoteHostOnlyAcknowledged;
			set => editableSettings.RemoteHostOnlyAcknowledged = value;
		}

		public void RequestRemoteTokenRotation() {
			editableSettings.RequestRemoteTokenRotation();
			OnPropertyChanged(nameof(RemoteTokenRequired));
			OnPropertyChanged(nameof(RemoteTokenVerifier));
		}

		/// <summary>Gets the actual listener state and its localized labels.</summary>
		public bool IsServerRunning => globalSettings.IsServerRunning;
		public string ServerActionText => IsServerRunning ? "停止" : "启动";
		public string ServerStatusText => IsServerRunning ? "服务器正在运行" : "服务器已停止";

		/// <summary>
		/// Applies the fields currently visible in the page and immediately toggles the listener.
		/// Returns a newly minted bearer token, if this transition configured remote auth.
		/// </summary>
		public string? ToggleServer() {
			editableSettings.EnableServer = !globalSettings.IsServerRunning;
			globalSettings.ApplyEdited(editableSettings);
			globalSettings.CopyTo(editableSettings);
			OnPropertyChanged(nameof(EnableServer));
			return globalSettings.ConsumeOneTimeRemoteToken();
		}

		/// <summary>Opens dnSpy's folder picker for the artifact directory.</summary>
		public void BrowseArtifactRoot() {
			var selected = pickDirectory.GetDirectory(string.IsNullOrWhiteSpace(ArtifactRoot) ? null : ArtifactRoot);
			if (selected != null)
				ArtifactRoot = selected;
		}

		/// <summary>
		/// Gets the live log messages from global settings.
		/// </summary>
		public System.Collections.ObjectModel.ObservableCollection<string> LogMessages => globalSettings.LogMessages;

		/// <summary>
		/// Gets the live combined log text from global settings.
		/// </summary>
		public string LogText => globalSettings.LogText;
	}
}
