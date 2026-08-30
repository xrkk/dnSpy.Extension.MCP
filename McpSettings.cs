using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.Linq;
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
		/// Gets or sets the server host (default: the configured Win10VM host-only address).
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
		string host = "192.168.204.149";

		/// <summary>
		/// Gets or sets the server port (default: 15378).
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
		int port = 15378;

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
		string artifactRoot = McpSettingsSnapshot.DefaultArtifactRoot();

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

		/// <summary>Canonical CIDRs, one per line, used when Host is non-loopback.</summary>
		public string RemoteAllowedCidrsText {
			get => remoteAllowedCidrsText;
			set {
				if (remoteAllowedCidrsText != value) {
					remoteAllowedCidrsText = value ?? string.Empty;
					OnPropertyChanged(nameof(RemoteAllowedCidrsText));
				}
			}
		}
		string remoteAllowedCidrsText = McpSettingsSnapshot.TrustedHostOnlyPeerCidr;

		/// <summary>The persisted SHA-256 verifier. The raw bearer token is never stored here.</summary>
		public string? RemoteTokenVerifier {
			get => remoteTokenVerifier;
			set {
				if (remoteTokenVerifier != value) {
					remoteTokenVerifier = value;
					OnPropertyChanged(nameof(RemoteTokenVerifier));
				}
			}
		}
		string? remoteTokenVerifier;

		/// <summary>UI authentication mode. Persistence remains the fixed v1 snapshot shape:
		/// a non-null verifier means token mode; null means the exact trusted-peer tokenless mode.</summary>
		public bool RemoteTokenRequired {
			get => remoteTokenRequired;
			set {
				if (remoteTokenRequired != value) {
					remoteTokenRequired = value;
					OnPropertyChanged(nameof(RemoteTokenRequired));
				}
			}
		}
		bool remoteTokenRequired;

		/// <summary>Operator acknowledgment that a non-loopback listener is host-only isolated.</summary>
		public bool RemoteHostOnlyAcknowledged {
			get => remoteHostOnlyAcknowledged;
			set {
				if (remoteHostOnlyAcknowledged != value) {
					remoteHostOnlyAcknowledged = value;
					OnPropertyChanged(nameof(RemoteHostOnlyAcknowledged));
				}
			}
		}
		bool remoteHostOnlyAcknowledged = true;

		/// <summary>Gets whether the listener is currently active (independent of unsaved UI edits).</summary>
		public bool IsServerRunning { get; private set; }

		/// <summary>Updates the live-state indicator from <see cref="McpServer"/>.</summary>
		internal void SetServerRunning(bool value) {
			if (IsServerRunning == value)
				return;
			IsServerRunning = value;
			OnPropertyChanged(nameof(IsServerRunning));
		}

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
			other.RemoteAllowedCidrsText = RemoteAllowedCidrsText;
			other.RemoteTokenVerifier = RemoteTokenVerifier;
			other.RemoteTokenRequired = RemoteTokenRequired;
			other.RemoteHostOnlyAcknowledged = RemoteHostOnlyAcknowledged;
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
			RemoteAllowedCidrsText = edited.RemoteAllowedCidrsText;
			RemoteTokenVerifier = edited.RemoteTokenVerifier;
			RemoteTokenRequired = edited.RemoteTokenRequired;
			RemoteHostOnlyAcknowledged = edited.RemoteHostOnlyAcknowledged;
		}

		/// <summary>Persists and immediately applies an explicit server start/stop request.</summary>
		public virtual void SetServerEnabled(bool enabled) {
			var edited = Clone();
			edited.EnableServer = enabled;
			ApplyEdited(edited);
		}

		/// <summary>Enables token mode and clears the verifier so the next successful remote Apply rotates it.</summary>
		public virtual void RequestRemoteTokenRotation() {
			RemoteTokenRequired = true;
			RemoteTokenVerifier = null;
		}

		/// <summary>Returns a newly generated raw token once, then irreversibly clears it.</summary>
		public virtual string? ConsumeOneTimeRemoteToken() => null;

		/// <summary>Wires the live MCP-session save gate after the debug service is composed.</summary>
		internal virtual void SetActiveSessionProbe(McpSettingsStore.ActiveSessionProbe probe) { }
	}

	/// <summary>
	/// Implementation of MCP settings with persistence support.
	/// </summary>
	[Export(typeof(McpSettings))]
	sealed class McpSettingsImpl : McpSettings {
		static readonly Guid SETTINGS_GUID = new Guid("352907A0-9DF5-4B2B-B47B-95E504CAC301");

		readonly McpSettingsStore store;
		McpServer? mcpServer;
		string? oneTimeRemoteToken;
		McpSettingsStore.ActiveSessionProbe? activeSessionProbe;
		Debugger.SettingsRootLease? activeRootLease;

		[ImportingConstructor]
		McpSettingsImpl(dnSpy.Contracts.Settings.ISettingsService settingsService) {
			// Authoritative load: two-key staged/committed recovery with the one-shot legacy
			// fallback (CON-DYN-014). UI fields mirror the snapshot without persistence events.
			Debugger.SettingsRootLease? startupLease = null;
			store = new McpSettingsStore(
				new SettingsSectionSnapshotIO(settingsService, SETTINGS_GUID),
				() => {
					var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
					return (sect.Attribute<bool?>(nameof(EnableServer)),
						sect.Attribute<string>(nameof(Host)),
						sect.Attribute<int?>(nameof(Port)));
				}, snapshot => Debugger.SettingsRootLease.TryAcquire(snapshot, out startupLease, out _));
			activeRootLease = startupLease;
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
			RemoteAllowedCidrsText = string.Join(Environment.NewLine, s.RemoteAllowedCidrs);
			RemoteTokenVerifier = s.RemoteTokenVerifier;
			RemoteTokenRequired = s.RequiresRemoteToken;
			RemoteHostOnlyAcknowledged = s.RemoteHostOnlyAcknowledged;
			OnPropertyChanged(nameof(EnableServer));
			OnPropertyChanged(nameof(Host));
			OnPropertyChanged(nameof(Port));
			OnPropertyChanged(nameof(DebugToolsEnabled));
			OnPropertyChanged(nameof(DedicatedDebugInstanceAcknowledged));
			OnPropertyChanged(nameof(ArtifactRoot));
			OnPropertyChanged(nameof(AllowedSampleRoot));
			OnPropertyChanged(nameof(RemoteAllowedCidrsText));
			OnPropertyChanged(nameof(RemoteTokenVerifier));
			OnPropertyChanged(nameof(RemoteTokenRequired));
			OnPropertyChanged(nameof(RemoteHostOnlyAcknowledged));
		}

		public override string? ConsumeOneTimeRemoteToken() {
			var token = oneTimeRemoteToken;
			oneTimeRemoteToken = null;
			return token;
		}

		static List<string> ParseCidrs(string text) => (text ?? string.Empty)
			.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(s => s.Trim()).Where(s => s.Length != 0).ToList();

		/// <summary>
		/// Sets the server instance for dynamic control.
		/// </summary>
		public void SetServer(McpServer server) {
			mcpServer = server;
		}

		internal override void SetActiveSessionProbe(McpSettingsStore.ActiveSessionProbe probe) =>
			activeSessionProbe = probe;

		/// <summary>
		/// Single Apply entry point: builds a candidate from the edited legacy fields plus the
		/// eight non-UI snapshot fields, then runs the five-step ApplySnapshot transaction.
		/// Failure and gate rejection keep the authoritative snapshot and re-sync the UI fields.
		/// </summary>
		public override void ApplyEdited(McpSettings edited) {
			oneTimeRemoteToken = null;
			var loopback = string.Equals(edited.Host, "localhost", StringComparison.Ordinal);
			var cidrs = loopback ? new List<string>() : ParseCidrs(edited.RemoteAllowedCidrsText);
			var verifier = loopback || !edited.RemoteTokenRequired ? null : edited.RemoteTokenVerifier;
			var remoteAck = loopback ? false : edited.RemoteHostOnlyAcknowledged;
			string? generatedToken = null;
			if (!loopback && edited.RemoteTokenRequired && verifier == null) {
				// Validate all non-secret fields before minting a credential. A failed Apply never
				// displays or persists a token that cannot subsequently authenticate.
				var probe = McpSettingsSnapshot.TryCreate(
					edited.EnableServer, edited.Host, edited.Port,
					edited.DebugToolsEnabled, edited.DedicatedDebugInstanceAcknowledged,
					edited.AllowedSampleRoot, edited.ArtifactRoot, cidrs, new string('0', 64),
					remoteAck, out var probeError);
				if (probe == null) {
					Log($"Settings rejected: {probeError}");
					LoadFieldsFromSnapshot();
					return;
				}
				(generatedToken, verifier) = McpSettingsSnapshot.GenerateRemoteToken();
			}
			var candidate = McpSettingsSnapshot.TryCreate(
				edited.EnableServer, edited.Host, edited.Port,
				edited.DebugToolsEnabled, edited.DedicatedDebugInstanceAcknowledged,
				edited.AllowedSampleRoot, edited.ArtifactRoot,
				cidrs, verifier, remoteAck, out var error);
			if (candidate == null) {
				Log($"Settings rejected: {error}");
				LoadFieldsFromSnapshot();
				return;
			}
			Debugger.SettingsRootLease? candidateLease = null;
			bool needsRuntimeLease = candidate.DebugToolsEnabled
				|| (store.Current.DebugToolsEnabled && store.Current.DedicatedDebugInstanceAcknowledged);
			if (needsRuntimeLease && !Debugger.SettingsRootLease.TryAcquire(candidate, out candidateLease, out var leaseError,
				force: true)) {
				Log($"Settings rejected: {leaseError}");
				LoadFieldsFromSnapshot();
				return;
			}
			var result = store.Apply(candidate,
				s => mcpServer != null && mcpServer.ApplySnapshot(s),
				() => mcpServer?.Stop(), activeSessionProbe);
			if (result.RejectedByActiveSession || !result.Success) {
				candidateLease?.Dispose();
				Log(result.FixedMessage ?? McpSettingsPersistence.ApplyErrorBody);
				LoadFieldsFromSnapshot();
				return;
			}
			var oldLease = activeRootLease;
			activeRootLease = candidateLease;
			oldLease?.Dispose();
			if (result.FixedMessage != null)
				Log(result.FixedMessage);
			oneTimeRemoteToken = generatedToken;
			LoadFieldsFromSnapshot();
		}
	}
}
