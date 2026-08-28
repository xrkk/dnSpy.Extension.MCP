using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.Extension.MCP {
	/// <summary>
	/// Settings for the MCP server extension, including server configuration and logging.
	/// </summary>
	public class McpSettings : ViewModelBase {
		/// <summary>
		/// Gets or sets whether the MCP server is enabled.
		/// </summary>
		public bool EnableServer {
			get => enableServer;
			set {
				if (enableServer != value) {
					enableServer = value;
					OnPropertyChanged(nameof(EnableServer));
				}
			}
		}
		bool enableServer = false;

		/// <summary>
		/// Gets or sets the server host (default: localhost).
		/// </summary>
		public string Host {
			get => host;
			set {
				if (host != value) {
					host = value;
					OnPropertyChanged(nameof(Host));
				}
			}
		}
		string host = "localhost";

		/// <summary>
		/// Gets or sets the server port (default: 3000).
		/// </summary>
		public int Port {
			get => port;
			set {
				if (port != value) {
					port = value;
					OnPropertyChanged(nameof(Port));
				}
			}
		}
		int port = 3000;

		/// <summary>
		/// Gets or sets whether the dynamic-debugging tools are enabled. The per-process gate
		/// freezes this value at startup; changes apply after a restart (CON-DYN-014).
		/// </summary>
		public bool DebugToolsEnabled {
			get => debugToolsEnabled;
			set {
				if (debugToolsEnabled != value) {
					debugToolsEnabled = value;
					OnPropertyChanged(nameof(DebugToolsEnabled));
				}
			}
		}
		bool debugToolsEnabled;

		/// <summary>
		/// Gets or sets the dedicated-instance acknowledgment for this installation. The operator
		/// confirms this dnSpy instance follows the dedicated-instance runbook; the gate freezes
		/// the value at startup.
		/// </summary>
		public bool DedicatedDebugInstanceAcknowledged {
			get => dedicatedDebugInstanceAcknowledged;
			set {
				if (dedicatedDebugInstanceAcknowledged != value) {
					dedicatedDebugInstanceAcknowledged = value;
					OnPropertyChanged(nameof(DedicatedDebugInstanceAcknowledged));
				}
			}
		}
		bool dedicatedDebugInstanceAcknowledged;

		/// <summary>
		/// Gets or sets the artifact root for debug dumps (absolute path, exclusive of the
		/// extension directory and the sample root).
		/// </summary>
		public string ArtifactRoot {
			get => artifactRoot;
			set {
				if (artifactRoot != value) {
					artifactRoot = value;
					OnPropertyChanged(nameof(ArtifactRoot));
				}
			}
		}
		string artifactRoot = string.Empty;

		/// <summary>
		/// Gets or sets the allowed sample root for trusted sample data (may be empty).
		/// </summary>
		public string AllowedSampleRoot {
			get => allowedSampleRoot;
			set {
				if (allowedSampleRoot != value) {
					allowedSampleRoot = value;
					OnPropertyChanged(nameof(AllowedSampleRoot));
				}
			}
		}
		string allowedSampleRoot = string.Empty;

		/// <summary>
		/// Gets the collection of log messages (limited to last 100 messages).
		/// </summary>
		public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

		/// <summary>
		/// Gets or sets the combined log text for easy copying.
		/// </summary>
		string logText = string.Empty;
		public string LogText {
			get => logText;
			set {
				if (logText != value) {
					logText = value;
					OnPropertyChanged(nameof(LogText));
				}
			}
		}

		/// <summary>
		/// Path of the on-disk fallback log used only in debug builds. Writes here always succeed
		/// and do not depend on the WPF dispatcher being alive or on a settings dialog being open,
		/// which makes this the authoritative record of what the extension did at startup during
		/// development. Release builds do not write to disk.
		/// </summary>
#if DEBUG
		public static readonly string LogFilePath = @"E:\dnspy-mcp.log";

		static readonly object logFileLock = new object();
#endif

		/// <summary>
		/// Adds a log message with timestamp to the log collection. In DEBUG builds the entry is
		/// also mirrored to an on-disk log file so that startup problems are captured even when the
		/// WPF dispatcher or settings dialog is unavailable. Release builds only keep the in-memory
		/// collection for the settings UI.
		/// </summary>
		/// <param name="message">The log message to add.</param>
		public virtual void Log(string message) {
			var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
			var logEntry = $"[{timestamp}] {message}";

#if DEBUG
			// Debug-only: mirror to disk. UI writes can fail silently if the dispatcher is unavailable,
			// so the on-disk log is the authoritative record during development.
			try {
				lock (logFileLock)
					System.IO.File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
			}
			catch {
				// If we can't write the log file, there is nowhere sensible to report that failure.
			}
#endif

			void addToCollection() {
				LogMessages.Add(logEntry);
				while (LogMessages.Count > 100)
					LogMessages.RemoveAt(0);
				LogText = string.Join(Environment.NewLine, LogMessages);
			}

			// Add to collection on UI thread if available
			var app = System.Windows.Application.Current;
			if (app?.Dispatcher != null && !app.Dispatcher.HasShutdownStarted) {
				try {
					app.Dispatcher.Invoke(addToCollection);
				}
				catch {
					// Dispatcher may be busy or unavailable during startup; file log still has the entry.
				}
			}
			else {
				addToCollection();
			}
		}

		/// <summary>
		/// Creates a copy of these settings.
		/// </summary>
		public McpSettings Clone() => CopyTo(new McpSettings());

		/// <summary>
		/// Copies these settings to another instance.
		/// </summary>
		public McpSettings CopyTo(McpSettings other) {
			other.EnableServer = EnableServer;
			other.Host = Host;
			other.Port = Port;
			other.DebugToolsEnabled = DebugToolsEnabled;
			other.DedicatedDebugInstanceAcknowledged = DedicatedDebugInstanceAcknowledged;
			other.ArtifactRoot = ArtifactRoot;
			other.AllowedSampleRoot = AllowedSampleRoot;
			return other;
		}

		/// <summary>
		/// The authoritative settings snapshot backing this instance, or null before the store
		/// is wired. The server reads this instead of the mutable UI properties.
		/// </summary>
		public virtual McpSettingsSnapshot? CurrentSnapshot => null;

		/// <summary>
		/// Applies an edited clone. The store-backed implementation routes this through the
		/// CON-DYN-014 ApplySnapshot transaction (staged/committed persistence plus one server
		/// transition); the base fallback just copies the legacy properties.
		/// </summary>
		public virtual void ApplyEdited(McpSettings edited) {
			EnableServer = edited.EnableServer;
			Host = edited.Host;
			Port = edited.Port;
			DebugToolsEnabled = edited.DebugToolsEnabled;
			DedicatedDebugInstanceAcknowledged = edited.DedicatedDebugInstanceAcknowledged;
			ArtifactRoot = edited.ArtifactRoot;
			AllowedSampleRoot = edited.AllowedSampleRoot;
		}
	}

	/// <summary>
	/// Implementation of MCP settings with persistence support.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("352907A0-9DF5-4B2B-B47B-95E504CAC301");

		readonly McpSettingsStore store;
		McpServer? mcpServer;

		[ImportingConstructor]
		McpSettingsImpl(dnSpy.Contracts.Settings.ISettingsService settingsService) {
			// Authoritative load: two-key staged/committed recovery with the one-shot legacy
			// fallback (CON-DYN-014). UI fields mirror the snapshot without persistence events.
			store = new McpSettingsStore(
				new SettingsSectionSnapshotIO(settingsService, SETTINGS_GUID),
				() => {
					var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
					return (sect.Attribute<bool?>(nameof(EnableServer)),
						sect.Attribute<string>(nameof(Host)),
						sect.Attribute<int?>(nameof(Port)));
				});
			LoadFieldsFromSnapshot();
			if (store.StartupWarning != null)
				Log(store.StartupWarning);
		}

		public override McpSettingsSnapshot? CurrentSnapshot => store.Current;

		void LoadFieldsFromSnapshot() {
			var s = store.Current;
			// Property setters raise PropertyChanged for the UI; there is no persistence
			// subscriber anymore — all writes go through ApplyEdited's transaction.
			EnableServer = s.EnableServer;
			Host = s.Host;
			Port = s.Port;
			DebugToolsEnabled = s.DebugToolsEnabled;
			DedicatedDebugInstanceAcknowledged = s.DedicatedDebugInstanceAcknowledged;
			ArtifactRoot = s.ArtifactRoot;
			AllowedSampleRoot = s.AllowedSampleRoot;
			OnPropertyChanged(nameof(EnableServer));
			OnPropertyChanged(nameof(Host));
			OnPropertyChanged(nameof(Port));
			OnPropertyChanged(nameof(DebugToolsEnabled));
			OnPropertyChanged(nameof(DedicatedDebugInstanceAcknowledged));
			OnPropertyChanged(nameof(ArtifactRoot));
			OnPropertyChanged(nameof(AllowedSampleRoot));
		}

		/// <summary>
		/// Sets the server instance for dynamic control.
		/// </summary>
		public void SetServer(McpServer server) {
			mcpServer = server;
		}

		/// <summary>
		/// Single Apply entry point: builds a candidate from the edited legacy fields plus the
		/// eight non-UI snapshot fields, then runs the five-step ApplySnapshot transaction.
		/// Failure and gate rejection keep the authoritative snapshot and re-sync the UI fields.
		/// </summary>
		public override void ApplyEdited(McpSettings edited) {
			var current = store.Current;
			var candidate = McpSettingsSnapshot.TryCreate(
				edited.EnableServer, edited.Host, edited.Port,
				edited.DebugToolsEnabled, edited.DedicatedDebugInstanceAcknowledged,
				edited.AllowedSampleRoot, edited.ArtifactRoot,
				current.RemoteAllowedCidrs, current.RemoteTokenVerifier,
				current.RemoteHostOnlyAcknowledged, out var error);
			if (candidate == null) {
				Log($"Settings rejected: {error}");
				LoadFieldsFromSnapshot();
				return;
			}
			var result = store.Apply(candidate,
				s => mcpServer != null && mcpServer.ApplySnapshot(s),
				() => mcpServer?.Stop());
			if (result.RejectedByActiveSession || !result.Success) {
				Log(result.FixedMessage ?? McpSettingsPersistence.ApplyErrorBody);
				LoadFieldsFromSnapshot();
				return;
			}
			if (result.FixedMessage != null)
				Log(result.FixedMessage);
			LoadFieldsFromSnapshot();
		}
	}
}
