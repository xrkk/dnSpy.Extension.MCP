using System;
using System.IO;
using System.Reflection;
using System.Threading;

// ACC-032/ACC-035 fixture: loads the SAME satellite bytes TWICE via Assembly.Load(byte[]) —
// two in-memory (runtime_weak) modules with identical MVID and method tokens but distinct
// runtime identities, whose target methods are invoked alternately. A token manifest is
// written at startup so the driver can address Satellite.Answer by its MethodDef token and
// verify both loads really produced distinct module objects.
internal static class DualDynFixture {
    private static void Main() {
        Thread.Sleep(1500);
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var bytes = File.ReadAllBytes(Path.Combine(dir, "SatelliteLib.dll"));
        var a1 = Assembly.Load(bytes);
        var a2 = Assembly.Load(bytes);
        var t1 = a1.GetType("Satellite.Satellite");
        var t2 = a2.GetType("Satellite.Satellite");
        var mi1 = t1.GetMethod("Answer");
        var mi2 = t2.GetMethod("Answer");
        File.WriteAllText(Path.Combine(dir, "dualdyn-manifest.txt"),
            "mvid=" + t1.Module.ModuleVersionId + "\r\n" +
            "token1=0x" + mi1.MetadataToken.ToString("x8") + "\r\n" +
            "token2=0x" + mi2.MetadataToken.ToString("x8") + "\r\n" +
            "mvid2=" + t2.Module.ModuleVersionId + "\r\n" +
            "distinct=" + (!ReferenceEquals(a1, a2)) + "\r\n");
        while (true) {
            mi1.Invoke(null, null);
            mi2.Invoke(null, null);
            Thread.Sleep(30);
        }
    }
}
