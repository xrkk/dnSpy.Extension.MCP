using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnSpy.Contracts.Debugger;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// CommandLineToArgvW-reverse-compatible per-element encoder (§3.5 WindowsEncode): the empty
/// string becomes ""; an argument containing whitespace or a quote is wrapped in double quotes;
/// a run of n backslashes before a quote is written as 2n+1 backslashes followed by the quote;
/// a run of n backslashes before the closing quote is written as 2n; every other character is
/// verbatim. No shell is ever involved.
/// </summary>
public static class WindowsArgumentEncoder {
	public static string EncodeElement(string argument) {
		bool quote = argument.Length == 0
			|| argument.Any(c => char.IsWhiteSpace(c) || c == '"');
		var sb = new StringBuilder();
		if (quote)
			sb.Append('"');
		int i = 0;
		while (i < argument.Length) {
			if (argument[i] == '\\') {
				int run = 0;
				while (i + run < argument.Length && argument[i + run] == '\\')
					run++;
				bool atEnd = i + run == argument.Length;
				bool beforeQuote = !atEnd && argument[i + run] == '"';
				if (beforeQuote) {
					sb.Append('\\', 2 * run + 1);
					sb.Append('"');
					i += run + 1;
				}
				else {
					// Runs not followed by a quote are verbatim, EXCEPT the trailing run of a
					// quoted element (it sits right before the closing quote): 2n.
					sb.Append('\\', atEnd && quote ? 2 * run : run);
					i += run;
				}
			}
			else if (argument[i] == '"') {
				// A quote with no preceding backslashes: 2*0+1 backslash then the quote.
				sb.Append("\\\"");
				i++;
			}
			else {
				sb.Append(argument[i]);
				i++;
			}
		}
		if (quote)
			sb.Append('"');
		return sb.ToString();
	}

	/// <summary>Joins encoded elements with single spaces — the process command line string.</summary>
	public static string EncodeCommandLine(IReadOnlyList<string> arguments)
		=> string.Join(" ", arguments.Select(EncodeElement));
}

/// <summary>The fully validated, mode-resolved launch plan (pure data; §3.5 launch-mode table).</summary>
public sealed class LaunchPlan {
	public string LaunchMode { get; init; } = string.Empty;
	public string RuntimeFamily { get; init; } = string.Empty;
	/// <summary>Filename handed to the dnSpy start options (target, or host/harness where the mode dictates).</summary>
	public string Filename { get; init; } = string.Empty;
	/// <summary>Encoded CommandLine handed to the dnSpy start options.</summary>
	public string CommandLine { get; init; } = string.Empty;
	public string WorkingDirectory { get; init; } = string.Empty;
	public bool UseHost { get; init; }
	public string? Host { get; init; }
	public string HostArguments { get; init; } = string.Empty;
	public string BreakKind { get; init; } = string.Empty;
	/// <summary>The exact upstream StartDebuggingOptions.BreakKind constant name.</summary>
	public string UpstreamBreakKind { get; init; } = string.Empty;
}

/// <summary>
/// Maps a validated debug_launch request onto the dnSpy start-option fields per the §3.5 table.
/// Pure logic: file reading (auto/harness PE family detection) and the dnSpy option objects
/// stay on the caller; the caller passes the detected runtime family for auto/harness modes.
/// </summary>
public static class LaunchPlanner {
	/// <summary>Windows-semantics parent directory (both separators accepted); the pure planner
	/// must not depend on the host OS path rules.</summary>
	static string WindowsParentDirectory(string path) {
		int idx = path.LastIndexOfAny(new[] { '\\', '/' });
		return idx <= 0 ? string.Empty : path.Substring(0, idx);
	}


	public sealed class LaunchRequest {
		public string TargetPath { get; init; } = string.Empty;
		public string LaunchMode { get; init; } = string.Empty;
		public IReadOnlyList<string> TargetArgv { get; init; } = Array.Empty<string>();
		public string? WorkingDirectory { get; init; }
		public string BreakKind { get; init; } = string.Empty;
		public string? HostPath { get; init; }
		public IReadOnlyList<string> HostArgv { get; init; } = Array.Empty<string>();
		public string? HarnessPath { get; init; }
		public IReadOnlyList<string> HarnessArgv { get; init; } = Array.Empty<string>();
		/// <summary>Runtime family detected from PE/CLR headers for auto/harness (null when detection failed).</summary>
		public string? DetectedRuntimeFamily { get; init; }
	}

	/// <summary>Fixed MCP break_kind → PredefinedBreakKinds mapping (EVD-API-008): literal or null is never passed upstream.</summary>
	public static string UpstreamBreakKind(string breakKind) => breakKind switch {
		BreakKinds.None => PredefinedBreakKinds.DontBreak,
		BreakKinds.Process => PredefinedBreakKinds.CreateProcess,
		BreakKinds.ModuleCctorOrEntryPoint => PredefinedBreakKinds.ModuleCctorOrEntryPoint,
		BreakKinds.EntryPoint => PredefinedBreakKinds.EntryPoint,
		_ => throw new ArgumentOutOfRangeException(nameof(breakKind)),
	};

	/// <summary>
	/// Resolves the concrete exe mode for auto: net48-family headers → net48-exe, coreclr →
	/// coreclr-apphost; anything else cannot be uniquely mapped and the launch must fail with
	/// CAPABILITY_UNAVAILABLE before Start.
	/// </summary>
	public static string? ResolveAutoMode(string? detectedRuntimeFamily) => detectedRuntimeFamily switch {
		RuntimeFamilies.Net48 => LaunchModes.Net48Exe,
		RuntimeFamilies.CoreClr => LaunchModes.CoreClrAppHost,
		_ => null,
	};

	/// <summary>Builds the plan; returns null with an error when the mode cannot be resolved.</summary>
	public static LaunchPlan? Plan(LaunchRequest request, out string? error) {
		error = null;
		string mode = request.LaunchMode;
		string family;		switch (mode) {
			case LaunchModes.Auto: {
				var resolved = ResolveAutoMode(request.DetectedRuntimeFamily);
				if (resolved is null) {
					error = "auto: target runtime family could not be uniquely determined";
					return null;
				}
				mode = resolved;
				family = request.DetectedRuntimeFamily!;
				break;
			}
			case LaunchModes.Net48Exe:
				family = RuntimeFamilies.Net48;
				break;
			case LaunchModes.CoreClrAppHost:
				family = RuntimeFamilies.CoreClr;
				break;
			case LaunchModes.CoreClrDotnet:
				family = RuntimeFamilies.CoreClr;
				break;
			case LaunchModes.Harness: {
				// The harness PE's own family must be identifiable (net48 or coreclr); a failure
				// is CAPABILITY_UNAVAILABLE. The mode stays "harness": the plan switch's
				// harness branch launches the HARNESS exe with target_path as first argument —
				// overwriting mode here would build a plan that starts the target DLL instead.
				var resolved = request.DetectedRuntimeFamily switch {
					RuntimeFamilies.Net48 => LaunchModes.Net48Exe,
					RuntimeFamilies.CoreClr => LaunchModes.CoreClrAppHost,
					_ => null,
				};
				if (resolved is null) {
					error = "harness: harness runtime family could not be identified";
					return null;
				}
				family = request.DetectedRuntimeFamily!;
				break;
			}
			default:
				error = $"unknown launch_mode: {mode}";
				return null;
		}

		switch (mode) {
			case LaunchModes.Net48Exe:
			case LaunchModes.CoreClrAppHost:
				return new LaunchPlan {
					LaunchMode = mode, RuntimeFamily = family,
					Filename = request.TargetPath,
					CommandLine = WindowsArgumentEncoder.EncodeCommandLine(request.TargetArgv),
					WorkingDirectory = request.WorkingDirectory ?? WindowsParentDirectory(request.TargetPath),
					UseHost = false, Host = null, HostArguments = string.Empty,
					BreakKind = request.BreakKind, UpstreamBreakKind = UpstreamBreakKind(request.BreakKind),
				};
			case LaunchModes.CoreClrDotnet:
				if (string.IsNullOrEmpty(request.HostPath)) {
					// ACC-008: a framework-dependent DLL without an explicit host can never
					// start; reject before any lease or Start (CAPABILITY_UNAVAILABLE).
					error = "coreclr-dotnet requires an explicit host_path";
					return null;
				}
				return new LaunchPlan {
					LaunchMode = mode, RuntimeFamily = family,
					Filename = request.TargetPath,
					CommandLine = WindowsArgumentEncoder.EncodeCommandLine(request.TargetArgv),
					WorkingDirectory = request.WorkingDirectory ?? WindowsParentDirectory(request.TargetPath),
					UseHost = true,
					Host = request.HostPath,
					// dnSpy's default HostArguments is ["exec"] when the caller supplied none.
					HostArguments = WindowsArgumentEncoder.EncodeCommandLine(
						request.HostArgv.Count > 0 ? request.HostArgv : new[] { "exec" }),
					BreakKind = request.BreakKind, UpstreamBreakKind = UpstreamBreakKind(request.BreakKind),
				};
			default:
				// Harness resolved to an exe mode: Filename is the harness; the contract is the
				// first argument receiving the absolute target path, remaining args verbatim.
				var harnessArgs = new List<string> { request.TargetPath };
				harnessArgs.AddRange(request.HarnessArgv);
				return new LaunchPlan {
					LaunchMode = mode, RuntimeFamily = family,
					Filename = request.HarnessPath ?? string.Empty,
					CommandLine = WindowsArgumentEncoder.EncodeCommandLine(harnessArgs),
					WorkingDirectory = request.WorkingDirectory ?? string.Empty,
					UseHost = false, Host = null, HostArguments = string.Empty,
					BreakKind = request.BreakKind, UpstreamBreakKind = UpstreamBreakKind(request.BreakKind),
				};
		}
	}
}
