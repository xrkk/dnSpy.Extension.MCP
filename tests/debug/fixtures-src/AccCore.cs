using System;
using System.Threading;

// ACC-008 fixture: a CoreCLR (net10.0, x64) looping console app. Built as an apphost EXE
// (AccCore.exe) plus framework DLL (AccCore.dll) — the apphost exercises coreclr-apphost,
// the DLL with the .NET 10 host exercises coreclr-dotnet.
internal static class AccCore {
    private static void Hot() { Thread.Sleep(30); }
    private static int Main(string[] args) {
        while (true) { Hot(); }
    }
}
