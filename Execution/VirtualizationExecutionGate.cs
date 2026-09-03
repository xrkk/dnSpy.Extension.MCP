using System;
using System.ComponentModel.Composition;
using Microsoft.Win32;

namespace dnSpy.Extension.MCP.Execution;

public static class VirtualizationExecutionEntryPoints
{
    public const string DebugLaunch = "debug_launch";
    public const string DebugRestart = "debug_restart";
    public const string EditDynamicValidation = "edit_dynamic_validation";

    public static bool IsKnown(string value) => value == DebugLaunch
        || value == DebugRestart || value == EditDynamicValidation;
}

public sealed class VirtualizationExecutionDecision
{
    public string EntryPoint { get; }
    public VirtualizationEnvironmentSnapshot Environment { get; }
    public bool Allowed => Environment.ExecutionAllowed;

    internal VirtualizationExecutionDecision(string entryPoint, VirtualizationEnvironmentSnapshot environment)
    {
        EntryPoint = entryPoint;
        Environment = environment;
    }
}

public interface IVirtualizationExecutionGate
{
    VirtualizationEnvironmentSnapshot Current { get; }
    VirtualizationExecutionDecision Evaluate(string entryPoint);
}

/// <summary>Process-local, UI-owned override. It is deliberately absent from McpSettings and
/// every persistence/transport contract, so closing dnSpy is the unconditional reset.</summary>
[Export(typeof(LocalVirtualizationExecutionOverride))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class LocalVirtualizationExecutionOverride
{
    readonly object sync = new();
    bool active;

    public bool Active { get { lock (sync) return active; } }
    public void ApplyFromLocalSettingsPage(bool requested) { lock (sync) active = requested; }
}

/// <summary>Fixed BIOS-registry VM classifier and shared execution gate for all sample execution.</summary>
[Export(typeof(IVirtualizationExecutionGate))]
[Export(typeof(VirtualizationExecutionGate))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class VirtualizationExecutionGate : IVirtualizationExecutionGate
{
    readonly LocalVirtualizationExecutionOverride localOverride;
    readonly object testSync = new();
    VirtualizationClassifier.BiosSignals? testSignals;

    [ImportingConstructor]
    public VirtualizationExecutionGate(LocalVirtualizationExecutionOverride localOverride) =>
        this.localOverride = localOverride;

    public VirtualizationEnvironmentSnapshot Current => VirtualizationClassifier.Classify(CurrentSignals(), localOverride.Active);
    public LocalVirtualizationExecutionOverride LocalOverride => localOverride;

    public VirtualizationExecutionDecision Evaluate(string entryPoint)
    {
        if (!VirtualizationExecutionEntryPoints.IsKnown(entryPoint))
            throw new ArgumentException("unknown execution entry point", nameof(entryPoint));
        return new VirtualizationExecutionDecision(entryPoint, Current);
    }

    VirtualizationClassifier.BiosSignals CurrentSignals()
    {
        if (TestModeEnabled) {
            lock (testSync) {
                if (testSignals != null)
                    return testSignals;
            }
        }
        return ReadBiosSignals();
    }

    internal void SetTestSignals(string? manufacturer, string? productName, string? biosVendor)
    {
        EnsureTestMode();
        lock (testSync)
            testSignals = new VirtualizationClassifier.BiosSignals(manufacturer, productName, biosVendor, readSucceeded: true);
    }

    internal void SetTestReadFailure()
    {
        EnsureTestMode();
        lock (testSync)
            testSignals = new VirtualizationClassifier.BiosSignals(null, null, null, readSucceeded: false);
    }

    internal void ClearTestSignals()
    {
        EnsureTestMode();
        lock (testSync)
            testSignals = null;
    }

    static void EnsureTestMode()
    {
        if (!TestModeEnabled)
            throw new InvalidOperationException("test diagnostics require DNMCP_TEST=1");
    }

    static bool TestModeEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("DNMCP_TEST"), "1", StringComparison.Ordinal);

    static VirtualizationClassifier.BiosSignals ReadBiosSignals()
    {
        try {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
            if (key == null)
                return new VirtualizationClassifier.BiosSignals(null, null, null, readSucceeded: false);
            return new VirtualizationClassifier.BiosSignals(
                key.GetValue("SystemManufacturer") as string,
                key.GetValue("SystemProductName") as string,
                key.GetValue("BIOSVendor") as string,
                readSucceeded: true);
        }
        catch {
            return new VirtualizationClassifier.BiosSignals(null, null, null, readSucceeded: false);
        }
    }
}

public sealed class EditDynamicValidationDecision
{
    public bool Allowed { get; }
    public string? ErrorCode { get; }
    public string State { get; }
    public VirtualizationEnvironmentSnapshot Environment { get; }

    public EditDynamicValidationDecision(bool allowed, string? errorCode, string state,
        VirtualizationEnvironmentSnapshot environment)
    {
        Allowed = allowed;
        ErrorCode = errorCode;
        State = state;
        Environment = environment;
    }
}

public interface IEditDynamicValidationGate
{
    EditDynamicValidationDecision EvaluateEditDynamicValidation();
}
