using System;
using System.Collections.Generic;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>Protects transports participating in the current debug session, including reconnects.
/// The debugger's session identity is independent of transport identity and survives DELETE.</summary>
internal sealed class DebugTransportSessionLeases : IMcpTransportSessionLeaseGuard, IMcpTransportSessionObserver {
	readonly object gate = new();
	readonly Dictionary<string, string> leases = new(StringComparer.Ordinal);
	readonly Func<string?> activeDebugSession;

	public DebugTransportSessionLeases(Func<string?> activeDebugSession) => this.activeDebugSession = activeDebugSession;
	static string Key(McpTransportKind kind, string id) => kind.ToWireName() + ":" + id;

	public bool HasSessionLease(McpTransportKind kind, string sessionId) { lock (gate) {
		var key = Key(kind, sessionId);
		if (!leases.TryGetValue(key, out var debugSession)) return false;
		if (debugSession == activeDebugSession()) return true;
		leases.Remove(key);
		return false;
	} }

	public void OnSessionClosed(McpTransportSessionClosed closed) { lock (gate) {
		leases.Remove(Key(closed.TransportKind, closed.SessionId));
	} }

	public void ObserveCall(McpCallContext context, bool accepted) {
		if (context.TransportKind != McpTransportKind.StreamableHttp || context.AuthoritativeSessionId == null) return;
		lock (gate) {
			var key = Key(context.TransportKind, context.AuthoritativeSessionId);
			var debugSession = activeDebugSession();
			// Closure is published before observers run, so a request completing after DELETE
			// cannot reinstall a lease or leave an entry behind for a dead transport.
			if (debugSession == null || context.IsSessionClosed) leases.Remove(key);
			else if (accepted) leases[key] = debugSession;
		}
	}
}
