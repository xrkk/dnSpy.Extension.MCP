using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

// Pure selector shared by the live tree path and the isolated contract probe.
internal static class EditTargetResolver {
	internal static ModuleDef? AssemblyModule(IEnumerable<ModuleDef> modules, string assemblyName, string? requestedMvid) {
		var matches = modules.Where(m => string.Equals(m.Assembly?.Name, assemblyName, StringComparison.OrdinalIgnoreCase));
		if (requestedMvid != null)
			matches = matches.Where(m => string.Equals(m.Mvid?.ToString("D"), requestedMvid, StringComparison.OrdinalIgnoreCase));
		var atMostTwo = matches.Take(2).ToArray();
		return atMostTwo.Length == 1 ? atMostTwo[0] : null;
	}

	internal static Guid DiagnosticMvid(string value) {
		if (value.Length != 36 || !Guid.TryParseExact(value, "D", out var mvid) || mvid == Guid.Empty)
			throw new ArgumentException("module_mvid must be a non-zero D-form GUID", nameof(value));
		return mvid;
	}

	internal static int StandaloneMatchCount(IEnumerable<ModuleDef> modules, string netmoduleName, Guid requestedMvid) {
		if (string.IsNullOrWhiteSpace(netmoduleName) || netmoduleName.Length > 512)
			throw new ArgumentException("netmodule_name must be non-blank and at most 512 characters", nameof(netmoduleName));
		if (requestedMvid == Guid.Empty)
			throw new ArgumentException("module_mvid must be non-zero", nameof(requestedMvid));
		return modules.Where(m => m.Assembly == null
			&& string.Equals(m.Name.String, netmoduleName, StringComparison.OrdinalIgnoreCase)
			&& m.Mvid == requestedMvid).Take(2).Count();
	}
}
