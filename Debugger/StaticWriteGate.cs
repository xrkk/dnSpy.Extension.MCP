using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.Debugger;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// IMP-010 gate for the six static write tools. The blocking predicate is evaluated inside the
/// SAME WPF Dispatcher callback as the mutation: blocked when the MCP coordinator is not idle OR
/// any debugging is active in this dnSpy process (DbgManager.IsDebugging covers human UI
/// debugging and other extensions' sessions — not just MCP-owned ones). Blocked calls return
/// INVALID_STATE with zero side effects.
/// </summary>
[Export(typeof(StaticWriteGate))]
public sealed class StaticWriteGate {
	/// <summary>The six gated static write tools (fixed set; reads and codegen are unaffected).</summary>
	public static readonly IReadOnlyList<string> GatedTools = new[] {
		"patch_method_il", "force_return", "nop_method", "revert_method_il",
		"rename_symbol_by_token", "save_assembly",
	};

	public static bool IsGatedTool(string name) => GatedTools.Contains(name);

	readonly Func<bool>? isDebugging;
	/// <summary>
	/// Coordinator state provider, wired when the coordinator service is assembled; until then
	/// no MCP debug session can exist and the state reads as idle — the DbgManager side of the
	/// OR still guards human/other-extension debugging.
	/// </summary>
	public Func<string>? CoordinatorStateProvider { get; set; }

	[ImportingConstructor]
	public StaticWriteGate([Import(AllowDefault = true)] DbgManager? dbgManager)
		: this(() => dbgManager is not null && dbgManager.IsDebugging) {
	}

	/// <summary>Test seam.</summary>
	public StaticWriteGate(Func<bool>? isDebugging) {
		this.isDebugging = isDebugging;
	}

	public string CurrentCoordinatorState => CoordinatorStateProvider?.Invoke() ?? DebugStates.Idle;

	/// <summary>(coordinator state != idle) OR DbgManager.IsDebugging.</summary>
	public bool IsBlocked => CurrentCoordinatorState != DebugStates.Idle || (isDebugging?.Invoke() ?? false);
}
