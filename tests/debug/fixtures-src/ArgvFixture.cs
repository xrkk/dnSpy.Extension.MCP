using System;
using System.IO;
using System.Threading;

// ACC-026 argv matrix fixture: echoes every argument verbatim to a file (one per line with
// length-prefixed framing), then loops so the debug session stays alive.
internal static class ArgvFixture {
    private static int Main(string[] args) {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "argv-out.txt");
        var lines = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
            lines[i] = args[i].Length + ":" + args[i];
        File.WriteAllLines(path, lines);
        while (true) { Thread.Sleep(50); }
    }
}
