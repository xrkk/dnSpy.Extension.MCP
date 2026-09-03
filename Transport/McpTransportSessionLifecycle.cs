using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;

namespace dnSpy.Extension.MCP.Transport;

internal interface IMcpTransportSessionObserver
{
    void OnSessionClosed(McpTransportSessionClosed closedSession);
}

internal sealed class McpTransportSessionClosed
{
    public McpTransportKind TransportKind { get; }
    public string SessionId { get; }
    public string Reason { get; }
    public int ActiveSessionCountAfterRemoval { get; }

    public McpTransportSessionClosed(
        McpTransportKind transportKind,
        string sessionId,
        string reason,
        int activeSessionCountAfterRemoval)
    {
        TransportKind = transportKind;
        SessionId = sessionId;
        Reason = reason;
        ActiveSessionCountAfterRemoval = activeSessionCountAfterRemoval;
    }
}

internal static class McpTransportCloseReasons
{
    public const string ClientDelete = "client_delete";
    public const string LegacyDisconnect = "legacy_disconnect";
    public const string ListenerStop = "listener_stop";
}

/// <summary>
/// Removes a transport session and publishes its single authoritative close notification. The
/// dictionary removal is the idempotence boundary: only the caller that removed the live entry
/// can notify observers or consume a test fault.
/// </summary>
[Export(typeof(McpTransportSessionLifecycle))]
internal sealed class McpTransportSessionLifecycle
{
    const int MaxTestEvents = 256;
    readonly McpSettings settings;
    readonly IMcpTransportSessionObserver[] observers;
    readonly ConcurrentQueue<McpTransportSessionClosed> testEvents = new();
    int testEventCount;
    int faultNextObserver;
    int isolatedObserverFaults;
    static McpTransportSessionLifecycle? testInstance;

    [ImportingConstructor]
    public McpTransportSessionLifecycle(
        McpSettings settings,
        [ImportMany] IEnumerable<IMcpTransportSessionObserver> observers)
    {
        this.settings = settings;
        this.observers = observers.ToArray();
        if (TestModeEnabled)
            Interlocked.Exchange(ref testInstance, this);
    }

    public bool RemoveAndNotify<TSession>(
        ConcurrentDictionary<string, TSession> sessions,
        McpTransportKind transportKind,
        string sessionId,
        string reason,
        Func<int> activeSessionCount)
    {
        if (!sessions.TryRemove(sessionId, out _))
            return false;

        var closed = new McpTransportSessionClosed(
            transportKind,
            sessionId,
            reason,
            activeSessionCount());

        if (TestModeEnabled && Interlocked.Exchange(ref faultNextObserver, 0) == 1)
        {
            try
            {
                throw new InvalidOperationException("DNMCP_TEST observer fault");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref isolatedObserverFaults);
                settings.Log($"MCP transport session observer failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnSessionClosed(closed);
            }
            catch (Exception ex)
            {
                settings.Log($"MCP transport session observer failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (TestModeEnabled)
            RecordTestEvent(closed);
        return true;
    }

    void RecordTestEvent(McpTransportSessionClosed closed)
    {
        testEvents.Enqueue(closed);
        var count = Interlocked.Increment(ref testEventCount);
        while (count > MaxTestEvents && testEvents.TryDequeue(out _))
            count = Interlocked.Decrement(ref testEventCount);
    }

    void ArmTestObserverFaultCore()
    {
        if (!TestModeEnabled)
            throw new InvalidOperationException("test diagnostics require DNMCP_TEST=1");
        Interlocked.Exchange(ref faultNextObserver, 1);
    }

    void ResetTestSnapshotCore()
    {
        if (!TestModeEnabled)
            throw new InvalidOperationException("test diagnostics require DNMCP_TEST=1");
        while (testEvents.TryDequeue(out _)) { }
        Interlocked.Exchange(ref testEventCount, 0);
        Interlocked.Exchange(ref faultNextObserver, 0);
        Interlocked.Exchange(ref isolatedObserverFaults, 0);
    }

    static McpTransportSessionLifecycle TestInstance() =>
        Volatile.Read(ref testInstance) ?? throw new InvalidOperationException("test transport lifecycle is unavailable");

    internal static void ArmTestObserverFault() => TestInstance().ArmTestObserverFaultCore();
    internal static void ResetTestDiagnostics() => TestInstance().ResetTestSnapshotCore();

    internal static Dictionary<string, object?> GetTestDiagnostics()
    {
        var instance = TestInstance();
        return new Dictionary<string, object?> {
            ["isolated_observer_faults"] = Volatile.Read(ref instance.isolatedObserverFaults),
            ["events"] = instance.testEvents.Select(e => new Dictionary<string, object?> {
                ["transport_kind"] = e.TransportKind.ToWireName(),
                ["session_id"] = e.SessionId,
                ["reason"] = e.Reason,
                ["active_session_count_after_removal"] = e.ActiveSessionCountAfterRemoval,
            }).ToArray(),
        };
    }

    static bool TestModeEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("DNMCP_TEST"), "1", StringComparison.Ordinal);
}
