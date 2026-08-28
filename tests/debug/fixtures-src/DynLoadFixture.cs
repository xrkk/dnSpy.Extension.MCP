using System;
using System.IO;
using System.Reflection;
using System.Threading;

// ACC-017 fixture: loads the satellite library from disk after start (a dynamic module in the
// debugger's module table), then spins calling into it.
internal static class DynLoadFixture {
    private static int Main(string[] args) {
        Thread.Sleep(1500);
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SatelliteLib.dll");
        var asm = Assembly.LoadFrom(path);
        var mi = asm.GetType("Satellite.Satellite").GetMethod("Answer");
        while (true) {
            var v = (int)mi.Invoke(null, null);
            if (v == -1) Console.WriteLine("never");
            Thread.Sleep(30);
        }
    }
}
