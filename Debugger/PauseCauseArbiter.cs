using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// One entry of an owned runtime's BreakInfos snapshot, already classified upstream (§3.2).
/// Ordinal is the position in the original collection and must never be reordered by arrival.
/// </summary>
public sealed class BreakInfoObservation {
	/// <summary>Classified kind: exception, breakpoint, step, entry, process, break, other.</summary>
	public string Kind { get; }
	/// <summary>Original collection ordinal (lower wins within the same priority).</summary>
	public int Ordinal { get; }
	/// <summary>Owned breakpoint id when a BoundBreakpoint maps to an MCP-created breakpoint.</summary>
	public string? OwnedBreakpointId { get; }
	/// <summary>Current MCP step id when a StepComplete matches the outstanding step.</summary>
	public string? StepId { get; }
	/// <summary>True when the session exception policy requests a pause for this exception.</summary>
	public bool PolicyRequestedPause { get; }

	public BreakInfoObservation(string kind, int ordinal, string? ownedBreakpointId = null,
		string? stepId = null, bool policyRequestedPause = false) {
		Kind = kind;
		Ordinal = ordinal;
		OwnedBreakpointId = ownedBreakpointId;
		StepId = stepId;
		PolicyRequestedPause = policyRequestedPause;
	}
}

/// <summary>
/// Closed-priority primary-cause arbitration for a running→paused observation (§3.2, AUD-027).
/// Priority is fixed: exception &gt; breakpoint &gt; step &gt; entry &gt; process &gt; manual &gt; unknown,
/// ties broken by the smallest original ordinal — never by arrival order. A Break message is a
/// manual-pause candidate ONLY while the session's single pause record is still issued and
/// unsettled; stock dnSpy v6.6.0 carries no request-correlation token, so an issued pause that
/// coincides with a Break observation still settles as request_effect=state_satisfied and a
/// late Break from an earlier request can never upgrade to a causal claim.
/// </summary>
public static class PauseCauseArbiter {
	public const string Exception = "exception";
	public const string Breakpoint = "breakpoint";
	public const string Step = "step";
	public const string Entry = "entry";
	public const string Process = "process";
	public const string Manual = "manual";
	public const string Unknown = "unknown";

	/// <summary>Priority rank (lower wins); unknown ranks last.</summary>
	static int Rank(string cause) => cause switch {
		Exception => 0,
		Breakpoint => 1,
		Step => 2,
		Entry => 3,
		Process => 4,
		Manual => 5,
		_ => 6,
	};

	/// <summary>
	/// Selects the unique primary cause. Candidates: policy-qualified exceptions, bound
	/// breakpoints, current-step completions, entry-point breaks, program breaks, and Break
	/// messages only while an issued pause record is unsettled. Everything else — including an
	/// empty snapshot, unknown kinds, unmatched steps and Break with no issued pause — falls to
	/// unknown.
	/// </summary>
	public static string SelectPrimaryCause(IReadOnlyList<BreakInfoObservation> infos, bool issuedPauseRecordUnsettled) {
		string? best = null;
		int bestOrdinal = int.MaxValue;
		foreach (var info in infos) {
			string? candidate = info.Kind switch {
				"exception" when info.PolicyRequestedPause => Exception,
				"breakpoint" => Breakpoint,
				"step" when info.StepId != null => Step,
				"entry" => Entry,
				"process" => Process,
				"break" when issuedPauseRecordUnsettled => Manual,
				_ => null,
			};
			if (candidate is null)
				continue;
			if (best is null || Rank(candidate) < Rank(best) || (Rank(candidate) == Rank(best) && info.Ordinal < bestOrdinal)) {
				best = candidate;
				bestOrdinal = info.Ordinal;
			}
		}
		return best ?? Unknown;
	}

	/// <summary>
	/// The detail events constructible for one stop, in the fixed order exception, breakpoint,
	/// step with the original ordinals preserved (EVT-DYN-014, owned-only EVT-DYN-013 and
	/// current-step EVT-DYN-015 are written strictly after EVT-DYN-010).
	/// </summary>
	public static IReadOnlyList<BreakInfoObservation> DetailOrder(IReadOnlyList<BreakInfoObservation> infos)
		=> infos
			.Where(i => i.Kind is Exception or Breakpoint or Step)
			.OrderBy(i => Rank(i.Kind))
			.ThenBy(i => i.Ordinal)
			.ToList();
}
