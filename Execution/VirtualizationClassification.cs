using System;
using System.Collections.Generic;
using System.Linq;

namespace dnSpy.Extension.MCP.Execution;

public static class VirtualizationClassifications
{
    public const string VMware = "vmware";
    public const string VirtualBox = "virtualbox";
    public const string Physical = "physical";
    public const string Unknown = "unknown";
}

public sealed class VirtualizationEnvironmentSnapshot
{
    public string Classification { get; }
    public bool ExecutionAllowed { get; }
    public bool LocalProcessOverrideActive { get; }
    public string DetectionSource => "windows_bios_registry_v1";
    public IReadOnlyList<string> MarkerTags { get; }

    internal VirtualizationEnvironmentSnapshot(string classification, bool executionAllowed,
        bool localProcessOverrideActive, IReadOnlyList<string> markerTags)
    {
        Classification = classification;
        ExecutionAllowed = executionAllowed;
        LocalProcessOverrideActive = localProcessOverrideActive;
        MarkerTags = markerTags;
    }
}

internal static class VirtualizationClassifier
{
    internal sealed class BiosSignals
    {
        public string? Manufacturer { get; }
        public string? ProductName { get; }
        public string? BiosVendor { get; }
        public bool ReadSucceeded { get; }

        public BiosSignals(string? manufacturer, string? productName, string? biosVendor, bool readSucceeded)
        {
            Manufacturer = manufacturer;
            ProductName = productName;
            BiosVendor = biosVendor;
            ReadSucceeded = readSucceeded;
        }
    }

    internal static VirtualizationEnvironmentSnapshot Classify(BiosSignals signals, bool localOverrideActive)
    {
        var manufacturer = Normalize(signals.Manufacturer);
        var product = Normalize(signals.ProductName);
        var vendor = Normalize(signals.BiosVendor);
        var values = new[] { manufacturer, product, vendor };
        var tags = new List<string>();
        string classification;

        if (values.Any(v => v.Contains("vmware"))) {
            classification = VirtualizationClassifications.VMware;
            tags.Add("vmware");
        }
        else {
            bool productVirtualBox = product.Contains("virtualbox");
            var innotekIndexes = Enumerable.Range(0, values.Length)
                .Where(i => values[i].Contains("innotek gmbh")).ToArray();
            bool hasInnotekPair = innotekIndexes.Any(i => Enumerable.Range(0, values.Length)
                .Any(j => j != i && (values[j].Contains("virtualbox") || values[j].Contains("innotek"))));
            if (productVirtualBox || hasInnotekPair) {
                classification = VirtualizationClassifications.VirtualBox;
                if (productVirtualBox) tags.Add("virtualbox");
                if (innotekIndexes.Length != 0) tags.Add("innotek_gmbh");
            }
            else if (!signals.ReadSucceeded || values.All(string.IsNullOrEmpty))
                classification = VirtualizationClassifications.Unknown;
            else
                classification = VirtualizationClassifications.Physical;
        }

        bool intrinsicallyAllowed = classification == VirtualizationClassifications.VMware
            || classification == VirtualizationClassifications.VirtualBox;
        return new VirtualizationEnvironmentSnapshot(classification,
            intrinsicallyAllowed || localOverrideActive, localOverrideActive, tags);
    }

    static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
