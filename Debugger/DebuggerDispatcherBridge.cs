using System;

namespace dnSpy.Extension.MCP.Debugger;

/// <summary>
/// CON-DYN-003's single WPF special case: the serialized Start-attempt window for initial
/// launch and restart. Only one Start callback may run at a time; the callback may only
/// establish its reservation/start claim, read the pre-Start no-debugging precondition and
/// invoke the Start call, and must return immediately afterwards. It must never read or retain
/// any DbgProcess/Runtime/Thread/Frame/Value — those resolve on DbgManager.Dispatcher, which
/// IMP-004/IMP-005 wire up together with the debugger contract references.
/// </summary>
internal sealed class DebuggerDispatcherBridge
{
    readonly object gate = new object();
    bool startCallbackRunning;

    /// <summary>
    /// Claims the single serialized Start-callback slot. Returns false when another Start
    /// attempt is still inside its WPF window, in which case the caller must fail without side
    /// effects instead of queueing behind it.
    /// </summary>
    public bool TryBeginStartCallback()
    {
        lock (gate)
        {
            if (startCallbackRunning)
                return false;
            startCallbackRunning = true;
            return true;
        }
    }

    /// <summary>Releases the serialized slot; must run in a finally block of the callback.</summary>
    public void EndStartCallback()
    {
        lock (gate)
            startCallbackRunning = false;
    }
}
