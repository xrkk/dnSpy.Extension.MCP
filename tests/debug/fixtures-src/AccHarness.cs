using System;
using System.IO;
using System.Reflection;
using System.Threading;

// ACC-030 fixture: a harness EXE that loads the target assembly named by its FIRST
// argument (the 3.5 launch contract) and then spins so the debug session stays alive.
// Remaining arguments are recorded verbatim to a transcript file for harness_argv checks.
internal static class AccHarness {
    private static int Main(string[] args) {
        var transcript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harness-transcript.txt");
        var lines = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
            lines[i] = args[i].Length + ":" + args[i];
        try { File.WriteAllLines(transcript, lines); } catch { }
        if (args.Length > 0 && File.Exists(args[0])) {
            try { Assembly.LoadFrom(args[0]); } catch { }
        }
        while (true) { Thread.Sleep(50); }
    }
}
