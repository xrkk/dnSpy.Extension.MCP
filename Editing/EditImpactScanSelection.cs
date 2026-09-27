using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Editing;

internal static class EditImpactScanSelection {
	// A second loaded module can have the same MVID as the edited module.
	// Only the exact live ModuleDef object is the scan target itself.
	internal static IEnumerable<T> OtherLoadedModules<T>(IEnumerable<T> modules, T edited) where T : class =>
		modules.Where(module => !ReferenceEquals(module, edited));
}
