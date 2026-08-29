using System;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Debugger;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// Per-process freeze of the debug feature gate (CON-DYN-014). The startup snapshot is captured
/// once, and exactly one IsDebugging sample is posted through DbgManager.Dispatcher — before the
/// server starts (the caller captures at Loaded, prior to Start). The contract dispatcher only
/// exposes BeginInvoke, so the sampled value materializes when the dispatcher thread runs the
/// callback; until then Current reports the unsampleable gate (always false). An unsampleable
/// gate must never enable debug tools, and once sampled the frozen value is immutable.
/// </summary>
[Export(typeof(DebugGateService))]
public sealed class DebugGateService {
	readonly object gateLock = new object();
	readonly Action<Action>? postToDispatcher;
	readonly Func<bool>? isDebugging;
	McpSettingsSnapshot? startupSnapshot;
	DebugFeatureGate.FrozenGate? frozen;
	bool samplePosted;

	[ImportingConstructor]
	public DebugGateService([Import(AllowDefault = true)] DbgManager? dbgManager)
		: this(dbgManager is null ? null : callback => dbgManager.Dispatcher.BeginInvoke(callback),
			dbgManager is null ? null : () => dbgManager.IsDebugging) {
	}

	/// <summary>Test seam: inject the dispatcher posting and the IsDebugging read directly.</summary>
	public DebugGateService(Action<Action>? postToDispatcher, Func<bool>? isDebugging) {
		this.postToDispatcher = postToDispatcher;
		this.isDebugging = isDebugging;
	}

	/// <summary>
	/// Captures the startup snapshot (first call wins) and posts the single dispatcher sample.
	/// Call once from the extension Loaded handler, before the server starts.
	/// </summary>
	public void CaptureStartup(McpSettingsSnapshot snapshot) {
		lock (gateLock) {
			startupSnapshot ??= snapshot;
			if (samplePosted || postToDispatcher is null || isDebugging is null)
				return;
			samplePosted = true;
			var captured = startupSnapshot;
			postToDispatcher(() => {
				// DNMCP_TEST seam: simulate a debug session being active at extension startup
				// (combo-D) — the sampled gate freezes closed, exactly like a real one.
				bool busyAtStartup = (DebugSessionService.TestModeEnabled
						&& Environment.GetEnvironmentVariable("DNMCP_TEST_STARTUP_DEBUGGING") == "1")
					|| (isDebugging?.Invoke() ?? true);
				lock (gateLock)
					frozen ??= DebugFeatureGate.Freeze(captured, !busyAtStartup);
			});
		}
	}

	/// <summary>
	/// The frozen gate: the dispatcher-sampled value once it lands, otherwise the unsampleable
	/// gate over the captured startup snapshot (effective_debug_launch = false).
	/// </summary>
	public DebugFeatureGate.FrozenGate Current {
		get {
			lock (gateLock) {
				if (frozen != null)
					return frozen;
				var snapshot = startupSnapshot ?? McpSettingsSnapshot.SafeDefaults();
				return DebugFeatureGate.Freeze(snapshot, startupDbgWasIdle: null);
			}
		}
	}
}
