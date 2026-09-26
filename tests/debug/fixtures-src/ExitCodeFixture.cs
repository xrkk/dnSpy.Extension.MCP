using System;
using System.IO;
using System.Text;

internal static class ExitHost {
    static int Main() {
        string caseName = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory).Name;
        int exitCode;
        switch (caseName) {
            case "zero": exitCode = 0; break;
            case "ten": exitCode = 10; break;
            case "negative": exitCode = -7; break;
            default: return 99;
        }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Host.result.txt"),
            "CASE " + caseName + " INTENDED_EXIT " + exitCode + Environment.NewLine, new UTF8Encoding(false));
        return exitCode;
    }
}
