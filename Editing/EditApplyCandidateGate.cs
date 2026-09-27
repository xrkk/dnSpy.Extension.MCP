using System;

namespace dnSpy.Extension.MCP.Editing;

// The caller publishes the accepted revision only after this returns. A writer
// can mutate tokens or PDB state before throwing, so rejected candidates are
// rebuilt even when their canonical fingerprint happens to be unchanged.
internal static class EditApplyCandidateGate {
	internal static T Validate<T>(Func<T> apply, Action structural, Action roundtrip, Action restore) {
		try {
			var outcome = apply();
			structural();
			roundtrip();
			return outcome;
		}
		catch {
			restore();
			throw;
		}
	}
}
