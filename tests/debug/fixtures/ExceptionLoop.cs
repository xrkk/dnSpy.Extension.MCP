using System;
using System.IO;
using System.Threading;

internal static class ExceptionLoop {
    private static void Main() {
        Thread.Sleep(3000); // Leave time for debug_launch and exception-policy setup.
        for (int i = 0; i < 1000; i++) {
            try { throw new InvalidOperationException("T051 handled event " + i); }
            catch (InvalidOperationException) {
                if (i == 0) File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExceptionLoop.ran"),
                    System.Diagnostics.Process.GetCurrentProcess().Id + ":" + DateTime.UtcNow.Ticks);
            }
            Thread.Sleep(50);
        }
    }
}
