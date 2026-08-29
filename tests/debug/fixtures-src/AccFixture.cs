using System;
using System.IO;
using System.Reflection;
using System.Threading;

// ACC fixture target: infinite Hot() loop so pause/continue/restart cycles and repeated
// breakpoint hits always have live code to land in. Compiled on the VM with the framework
// csc (net48, x64-preferred AnyCPU).
internal static class AccFixture {
    private static void Hot() {
        var marker = "acc-fixture";
        Thread.Sleep(20);
        if (marker.Length == 0) Console.WriteLine(marker);
    }

    // Runtime token manifest (ACC-010): stepping-based discovery is flaky when the JIT
    // inlines Hot into Main (no Hot frame on the stack). The manifest publishes the real
    // MethodDef tokens once at startup so the driver can address Hot deterministically.
    private static void WriteTokenManifest() {
        try {
            var hot = typeof(AccFixture).GetMethod("Hot", BindingFlags.NonPublic | BindingFlags.Static);
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "accfix-manifest.txt");
            File.WriteAllText(path,
                "hot=0x" + hot.MetadataToken.ToString("x8") + "\r\n" +
                "main=0x" + MethodBase.GetCurrentMethod().DeclaringType.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static).MetadataToken.ToString("x8") + "\r\n" +
                "mvid=" + typeof(AccFixture).Module.ModuleVersionId + "\r\n");
        }
        catch {
        }
    }

    private static int Main(string[] args) {
        WriteTokenManifest();
        var i = 0;
        while (true) {
            Hot();
            i++;
        }
    }
}
