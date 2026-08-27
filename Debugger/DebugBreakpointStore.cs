using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// A session module registered on the dispatcher side (fields the breakpoint rules need).
/// identity_strength is disk_strong (on-disk module with sha256) or runtime_weak (dynamic
/// module with runtime_handle and no sha).
/// </summary>
public sealed class RegisteredModule {
	public string ModuleHandle { get; init; } = string.Empty;
	public string? RuntimeHandle { get; init; }
	public string Mvid { get; init; } = string.Empty;
	public string IdentityStrength { get; init; } = string.Empty;
	public string? Sha256 { get; init; }
}

/// <summary>
/// One MCP-owned breakpoint (TYPE-DYN-009). Only breakpoints created through this store are
/// managed; user breakpoints are never touched, listed or cleared here.
/// </summary>
public sealed class BreakpointEntry {
	public string BreakpointId { get; }
	public bool Owned => true;
	public bool Enabled { get; internal set; }
	public bool Bound { get; internal set; }
	public RegisteredModule Module { get; }
	public string MethodToken { get; }
	public int IlOffset { get; }
	public string? LastError { get; internal set; }

	internal BreakpointEntry(string id, RegisteredModule module, string methodToken, int ilOffset, bool enabled) {
		BreakpointId = id; Module = module; MethodToken = methodToken; IlOffset = ilOffset; Enabled = enabled;
	}
}

/// <summary>
/// Pure breakpoint bookkeeping (IMP-006). Creation rules: a disk_strong module must match
/// module_handle + SHA-256 + MVID (the request must carry the module's exact sha); a
/// runtime_weak module must match module_handle + runtime_handle + MVID and the request must
/// OMIT sha256. Several runtime modules may share the same MVID/token/offset — only the
/// module_handle disambiguates. enabled=false keeps the breakpoint but it never resolves as a
/// hit; re-enabling restores hits. bound flips only on that module's bound event.
/// </summary>
public sealed class DebugBreakpointStore {
	readonly object gate = new();
	readonly Dictionary<string, RegisteredModule> modules = new(StringComparer.Ordinal);
	readonly Dictionary<string, BreakpointEntry> breakpoints = new(StringComparer.Ordinal);
	readonly Func<string> newId;
	int seq;

	public DebugBreakpointStore(Func<string>? newId = null) {
		this.newId = newId ?? DefaultNewId;
		static string DefaultNewId() {
			var bytes = System.Security.Cryptography.RandomNumberGeneratorShim.GetBytes(12);
			return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		}
	}

	/// <summary>Registers (or replaces) a session module visible to breakpoint creation.</summary>
	public void RegisterModule(RegisteredModule module) {
		lock (gate)
			modules[module.ModuleHandle] = module;
	}

	public enum CreateError { None, ModuleNotFound, MissingSha256, ShaRejected, MvidMismatch, ShaMismatch, MissingRuntimeHandle, DuplicateBreakpoint }

	/// <summary>
	/// Creates an owned breakpoint after the identity rules. sha256Request is the caller's
	/// module_sha256 argument: required-and-matching for disk_strong, forbidden for runtime_weak.
	/// </summary>
	public (BreakpointEntry? Entry, CreateError Error) TryCreate(string moduleHandle, string? sha256Request,
		string mvid, string methodToken, int ilOffset, bool enabled, string? expectedId = null) {
		lock (gate) {
			if (!modules.TryGetValue(moduleHandle, out var module))
				return (null, CreateError.ModuleNotFound);
			if (module.Mvid != mvid)
				return (null, CreateError.MvidMismatch);
			if (module.IdentityStrength == "disk_strong") {
				if (sha256Request is null)
					return (null, CreateError.MissingSha256);
				if (!string.Equals(sha256Request, module.Sha256, StringComparison.Ordinal))
					return (null, CreateError.ShaMismatch);
			}
			else if (module.IdentityStrength == "runtime_weak") {
				if (sha256Request != null)
					return (null, CreateError.ShaRejected);
				if (module.RuntimeHandle is null)
					return (null, CreateError.MissingRuntimeHandle);
			}
			else
				return (null, CreateError.ModuleNotFound);
			// Same module/token/offset twice: reject as a duplicate owned breakpoint.
			if (breakpoints.Values.Any(b => b.Module.ModuleHandle == moduleHandle
				&& b.MethodToken == methodToken && b.IlOffset == ilOffset))
				return (null, CreateError.DuplicateBreakpoint);
			var id = expectedId ?? newId() + "-" + System.Threading.Interlocked.Increment(ref seq);
			var entry = new BreakpointEntry(id, module, methodToken, ilOffset, enabled);
			breakpoints[id] = entry;
			return (entry, CreateError.None);
		}
	}

	/// <summary>Sets enabled on an owned breakpoint; disabled breakpoints never hit.</summary>
	public bool SetEnabled(string breakpointId, bool enabled) {
		lock (gate) {
			if (!breakpoints.TryGetValue(breakpointId, out var entry))
				return false;
			entry.Enabled = enabled;
			return true;
		}
	}

	/// <summary>Removes an owned breakpoint; user breakpoints are not modeled here at all.</summary>
	public bool Remove(string breakpointId) {
		lock (gate)
			return breakpoints.Remove(breakpointId);
	}

	/// <summary>Bound-state transition: flips only for the given owned breakpoint of that module.</summary>
	public bool MarkBound(string breakpointId, bool bound, string? error = null) {
		lock (gate) {
			if (!breakpoints.TryGetValue(breakpointId, out var entry))
				return false;
			entry.Bound = bound;
			entry.LastError = error;
			return true;
		}
	}

	/// <summary>
	/// Hit resolution: only ENABLED owned breakpoints on the exact module_handle + token +
	/// offset hit. Disabled breakpoints at the same location must NOT resolve; user breakpoints
	/// are not this store's concern.
	/// </summary>
	public BreakpointEntry? TryResolveHit(string moduleHandle, string methodToken, int ilOffset) {
		lock (gate)
			return breakpoints.Values.FirstOrDefault(b => b.Module.ModuleHandle == moduleHandle
				&& b.MethodToken == methodToken && b.IlOffset == ilOffset && b.Enabled);
	}

	public IReadOnlyList<BreakpointEntry> List() {
		lock (gate)
			return breakpoints.Values.ToList();
	}
}
