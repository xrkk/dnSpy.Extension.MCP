using System;
using dnSpy.Contracts.Settings;

namespace dnSpy.Extension.MCP {
/// <summary>Raw two-key attribute IO seam over the extension's fixed settings section GUID.</summary>
public interface ISettingsSnapshotIO {
	/// <summary>Reads a raw stored attribute value (null when absent).</summary>
	string? Read(string key);
	/// <summary>Writes or overwrites an attribute value (null clears it). Throws on IO failure.</summary>
	void Write(string key, string? value);
}

/// <summary>Production adapter over <see cref="ISettingsSection"/>. Writes mutate the in-memory
/// section; dnSpy persists the whole settings file on graceful exit, so a crash never leaves a
/// half-applied candidate on disk (the CON-DYN-014 crash invariant holds by construction).</summary>
public sealed class SettingsSectionSnapshotIO : ISettingsSnapshotIO {
	readonly ISettingsService settingsService;
	readonly Guid sectionGuid;

	public SettingsSectionSnapshotIO(ISettingsService settingsService, Guid sectionGuid) {
		this.settingsService = settingsService;
		this.sectionGuid = sectionGuid;
	}

	public string? Read(string key) => settingsService.GetOrCreateSection(sectionGuid).Attribute<string>(key);

	public void Write(string key, string? value) {
		var section = settingsService.GetOrCreateSection(sectionGuid);
		if (value is null)
			section.RemoveAttribute(key);
		else
			section.Attribute(key, value);
	}
}

/// <summary>
/// Authoritative settings store: startup recovery plus the CON-DYN-014 ApplySnapshot five-step
/// transaction. All mutations go through Apply; there is no per-setter persistence path and no
/// rollback by rewriting old JSON.
/// </summary>
public sealed class McpSettingsStore {
	public McpSettingsSnapshot Current { get; private set; }
	/// <summary>Fixed startup warning from recovery, or null. Consumed before server Start.</summary>
	public string? StartupWarning { get; }
	public event Action<McpSettingsSnapshot>? SnapshotChanged;

	readonly ISettingsSnapshotIO io;

	/// <summary>Injectable read of the legacy per-property attributes, used only when both new keys are absent.</summary>
	public delegate (bool? enableServer, string? host, int? port)? LegacyRead();

	public McpSettingsStore(ISettingsSnapshotIO io, LegacyRead? legacyReader,
		Func<McpSettingsSnapshot, bool>? runtimeValidator = null) {
		this.io = io;
		string? committed, pending;
		try { committed = io.Read(McpSettingsPersistence.CommittedKey); }
		catch { committed = null; }
		try { pending = io.Read(McpSettingsPersistence.PendingKey); }
		catch { pending = null; }
		(bool?, string?, int?)? legacy = null;
		if (string.IsNullOrEmpty(committed) && string.IsNullOrEmpty(pending) && legacyReader != null)
			legacy = legacyReader();
		var recovery = McpSettingsPersistence.Recover(committed, pending, legacy);
		if (recovery.Snapshot.DebugToolsEnabled && runtimeValidator != null
			&& !runtimeValidator(recovery.Snapshot)) {
			Current = McpSettingsSnapshot.SafeDefaults();
			StartupWarning = McpSettingsPersistence.InvalidStoredWarning;
		}
		else {
			Current = recovery.Snapshot;
			StartupWarning = recovery.Warning;
		}
		if (recovery.TryClearPending)
			TryBestEffortClearPending();
	}

	void TryBestEffortClearPending() {
		try { io.Write(McpSettingsPersistence.PendingKey, null); }
		catch { /* best effort: fixed warning is surfaced by the caller via StartupWarning semantics */ }
	}

	/// <summary>Which step of the Apply sequence failed.</summary>
	public enum ApplyStep { None, PendingWrite, ServerTransition, CommittedWrite }

	public sealed class ApplyResult {
		public bool Success { get; }
		/// <summary>Failed step; None on success or gate rejection.</summary>
		public ApplyStep FailedStep { get; }
		/// <summary>True when the operation was rejected by the active-session gate.</summary>
		public bool RejectedByActiveSession { get; }
		/// <summary>Success-with-warning (pending could not be cleared) or gate rejection body.</summary>
		public string? FixedMessage { get; }
		ApplyResult(bool success, ApplyStep failedStep, bool rejected, string? message) {
			Success = success; FailedStep = failedStep; RejectedByActiveSession = rejected; FixedMessage = message;
		}
		public static ApplyResult Ok(string? warning) => new(true, ApplyStep.None, false, warning);
		public static ApplyResult Fail(ApplyStep step) => new(false, step, false, null);
		public static ApplyResult GateRejected() => new(false, ApplyStep.None, true, McpSettingsPersistence.ApplyActiveBody);
	}

	/// <summary>
	/// Whether an active MCP debug session must block this candidate. Wired to the coordinator in
	/// IMP-004; until then no coordinator exists, so no session can be active.
	/// </summary>
	public delegate bool ActiveSessionProbe();

	/// <summary>
	/// Applies a candidate with the fixed sequence: ① write pending ② server transition
	/// ③ write committed ④ memory swap + SnapshotChanged ⑤ best-effort clear pending.
	/// Transition failures restore the old server (or force stop); committed/memory stay old and
	/// pending is cleared best-effort. Old JSON is never written back as a rollback.
	/// </summary>
	public ApplyResult Apply(McpSettingsSnapshot candidate, Func<McpSettingsSnapshot, bool> serverTransition,
		Action? forceStopServer, ActiveSessionProbe? activeSession = null) {
		var old = Current;
		bool gateRelevant = candidate.DebugToolsEnabled != old.DebugToolsEnabled
			|| candidate.DedicatedDebugInstanceAcknowledged != old.DedicatedDebugInstanceAcknowledged
			|| !string.Equals(candidate.AllowedSampleRoot, old.AllowedSampleRoot, StringComparison.Ordinal)
			|| !string.Equals(candidate.ArtifactRoot, old.ArtifactRoot, StringComparison.Ordinal);
		if (gateRelevant && activeSession != null && activeSession())
			return ApplyResult.GateRejected();

		// ① pending write: any failure leaves committed/memory/server untouched.
		try { io.Write(McpSettingsPersistence.PendingKey, candidate.ToCanonicalJson()); }
		catch { return ApplyResult.Fail(ApplyStep.PendingWrite); }

		// ② server transition; on failure restore old server (callback contract) and clear pending.
		bool transitioned;
		try { transitioned = serverTransition(candidate); }
		catch { transitioned = false; }
		if (!transitioned) {
			try { if (!serverTransition(old)) forceStopServer?.Invoke(); }
			catch { forceStopServer?.Invoke(); }
			TryBestEffortClearPending();
			return ApplyResult.Fail(ApplyStep.ServerTransition);
		}

		// ③ committed write; on failure restore old server or force stop, keep committed/memory old.
		try { io.Write(McpSettingsPersistence.CommittedKey, candidate.ToCanonicalJson()); }
		catch {
			try { if (!serverTransition(old)) forceStopServer?.Invoke(); }
			catch { forceStopServer?.Invoke(); }
			TryBestEffortClearPending();
			return ApplyResult.Fail(ApplyStep.CommittedWrite);
		}

		// ④ authoritative memory swap + exactly one change event.
		Current = candidate;
		SnapshotChanged?.Invoke(candidate);

		// ⑤ best-effort pending clear; failure does not undo the commit.
		bool clearFailed = false;
		try { io.Write(McpSettingsPersistence.PendingKey, null); }
		catch { clearFailed = true; }
		return ApplyResult.Ok(clearFailed ? McpSettingsPersistence.ApplyPendingClearFailedBody : null);
	}
}

/// <summary>
/// The frozen per-process debug gate (CON-DYN-014): effective_debug_launch =
/// DebugToolsEnabled && DedicatedDebugInstanceAcknowledged && StartupDbgWasIdle.
/// StartupDbgWasIdle is sampled once via DbgManager.Dispatcher before the server starts; until
/// the debugger contract references land (IMP-005) sampling is unavailable and the gate stays
/// false — an unsampleable gate must never enable debug tools.
/// </summary>
public static class DebugFeatureGate {
	public sealed class FrozenGate {
		public bool EffectiveDebugLaunch { get; }
		public bool DebugToolsEnabled { get; }
		public bool DedicatedInstanceAcknowledged { get; }
		public bool StartupDbgWasIdle { get; }
		public bool StartupSampled { get; }
		public FrozenGate(McpSettingsSnapshot snapshot, bool? startupDbgWasIdle) {
			DebugToolsEnabled = snapshot.DebugToolsEnabled;
			DedicatedInstanceAcknowledged = snapshot.DedicatedDebugInstanceAcknowledged;
			StartupSampled = startupDbgWasIdle.HasValue;
			StartupDbgWasIdle = startupDbgWasIdle == true;
			EffectiveDebugLaunch = DebugToolsEnabled && DedicatedInstanceAcknowledged && StartupDbgWasIdle;
		}
	}

	public static FrozenGate Freeze(McpSettingsSnapshot snapshot, bool? startupDbgWasIdle) =>
		new(snapshot, startupDbgWasIdle);
}
}
