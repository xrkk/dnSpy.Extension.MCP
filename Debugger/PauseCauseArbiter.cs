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
	/// <summary>The registered step kind (into/over/out) reported on StepComplete; null otherwise.</summary>
	public string? StepKind { get; }
	/// <summary>True when the session exception policy requests a pause for this exception.</summary>
	public bool PolicyRequestedPause { get; }
	/// <summary>Pause-epoch-scoped thread handle, when the upstream event identifies a thread.</summary>
	public string? ThreadHandle { get; }
	/// <summary>Registered live module handle for the event location, when it can be resolved.</summary>
	public string? ModuleHandle { get; }
	/// <summary>Internal exception facts retained from the debugger object. They are not
	/// caller-supplied and support narrow server-side evidence gates.</summary>
	internal string? ExceptionType { get; }
	internal string? ExceptionMessage { get; }
	internal int? ExceptionHResult { get; }
	internal bool ExceptionFirstChance { get; }
	internal bool ExceptionUnhandled { get; }
	/// <summary>Loader-authored strong-name rejection facts. Set only by the session service
	/// when the exception is ICorDebug-attributed to a framework loader module, carries the
	/// strong-name failure HRESULT, and the loader message names the rejected assembly
	/// identity. Never caller-suppliable.</summary>
	internal StrongNameRejectionFacts? StrongNameRejection { get; set; }
	/// <summary>Diagnostic note from the classifier (DNMCP_TEST-era probes); not gate input.</summary>
	internal string? StrongNameGateNote { get; set; }

	public BreakInfoObservation(string kind, int ordinal, string? ownedBreakpointId = null,
		string? stepId = null, bool policyRequestedPause = false, string? stepKind = null,
		string? threadHandle = null, string? moduleHandle = null) {
		Kind = kind;
		Ordinal = ordinal;
		OwnedBreakpointId = ownedBreakpointId;
		StepId = stepId;
		StepKind = stepKind;
		PolicyRequestedPause = policyRequestedPause;
		ThreadHandle = threadHandle;
		ModuleHandle = moduleHandle;
	}

	internal BreakInfoObservation(string kind, int ordinal, string? ownedBreakpointId,
		string? stepId, bool policyRequestedPause, string? stepKind, string? threadHandle,
		string? moduleHandle, string? exceptionType, string? exceptionMessage,
		int? exceptionHResult, bool exceptionFirstChance, bool exceptionUnhandled)
		: this(kind, ordinal, ownedBreakpointId, stepId, policyRequestedPause, stepKind,
			threadHandle, moduleHandle) {
		ExceptionType = exceptionType;
		ExceptionMessage = exceptionMessage;
		ExceptionHResult = exceptionHResult;
		ExceptionFirstChance = exceptionFirstChance;
		ExceptionUnhandled = exceptionUnhandled;
	}
}

/// <summary>Facts of a CLR loader strong-name rejection, extracted from the loader-authored
/// exception message of an exception attributed (via ICorDebug module mapping) to a framework
/// loader module. Sample code cannot author an exception that carries this attribution.</summary>
public sealed class StrongNameRejectionFacts {
	public string LoaderModule { get; init; }
	public int HResult { get; init; }
	public string AssemblyName { get; init; }
	public string AssemblyVersion { get; init; }
	public string PublicKeyToken { get; init; }
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
