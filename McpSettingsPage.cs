using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Settings.Dialog;
using System.Windows;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Provider for the MCP server settings page in dnSpy settings dialog.
	/// </summary>
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class McpAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly McpSettings mcpSettings;

		/// <summary>
		/// Initializes the settings page provider.
		/// </summary>
		[ImportingConstructor]
		McpAppSettingsPageProvider(McpSettings mcpSettings) => this.mcpSettings = mcpSettings;

		/// <summary>
		/// Creates the settings page.
		/// </summary>
		public IEnumerable<AppSettingsPage> Create() {
			yield return new McpAppSettingsPage(mcpSettings);
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
		public override string Title => "MCP Server";

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
					uiObject.DataContext = new SettingsViewModel(newSettings, globalSettings);
				}
				return uiObject;
			}
		}
		McpSettingsControl? uiObject;

		readonly McpSettings globalSettings;
		readonly McpSettings newSettings;

		/// <summary>
		/// Initializes the settings page with the given settings instance.
		/// </summary>
		public McpAppSettingsPage(McpSettings mcpSettings) {
			globalSettings = mcpSettings;
			newSettings = mcpSettings.Clone();
		}

		/// <summary>
		/// Applies the settings when user clicks OK: one snapshot transaction, never per-setter writes.
		/// </summary>
		public override void OnApply() {
			globalSettings.ApplyEdited(newSettings);
			var token = globalSettings.ConsumeOneTimeRemoteToken();
			if (token != null) {
				MessageBox.Show(
					"Copy this bearer token now. It will not be stored or shown again:\n\n" + token,
					"MCP Remote Token — One-time Display",
					MessageBoxButton.OK, MessageBoxImage.Warning);
			}
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

		/// <summary>
		/// Initializes the view model with editable and global settings instances.
		/// </summary>
		public SettingsViewModel(McpSettings editable, McpSettings global) {
			editableSettings = editable;
			globalSettings = global;

			// Forward property change notifications from editable settings
			editableSettings.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName ?? string.Empty);

			// Forward property change notifications from global settings (for live logs)
			globalSettings.PropertyChanged += (s, e) => {
				if (e.PropertyName == nameof(LogText) || e.PropertyName == nameof(LogMessages)) {
					OnPropertyChanged(e.PropertyName);
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

		public string RemoteTokenVerifier => editableSettings.RemoteTokenVerifier ?? "(not configured — generated on Apply)";

		public bool RemoteHostOnlyAcknowledged {
			get => editableSettings.RemoteHostOnlyAcknowledged;
			set => editableSettings.RemoteHostOnlyAcknowledged = value;
		}

		public void RequestRemoteTokenRotation() {
			editableSettings.RequestRemoteTokenRotation();
			OnPropertyChanged(nameof(RemoteTokenVerifier));
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
