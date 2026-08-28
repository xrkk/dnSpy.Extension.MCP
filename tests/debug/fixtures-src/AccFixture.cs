using System;
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

    private static int Main(string[] args) {
        var i = 0;
        while (true) {
            Hot();
            i++;
        }
    }
}
