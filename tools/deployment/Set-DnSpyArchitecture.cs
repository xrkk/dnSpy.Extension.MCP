// Build on the host with dnlib.dll. Keep the signing key local and transfer only candidates.
// This specializes an existing IL-only net48 executable; it does not rebuild its source.
using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using dnlib.PE;

static class SetDnSpyArchitecture {
    static string Fingerprint(ModuleDef module) {
        return module.Assembly.FullName + "|" + module.EntryPoint.FullName + "|" +
            string.Join(";", module.GetTypes().Select(t => t.FullName + ":" +
                string.Join(",", t.Methods.Select(m => m.FullName)))) + "|" +
            string.Join(";", module.GetAssemblyRefs().Select(a => a.FullName)) + "|" +
            string.Join(";", module.Resources.Select(r => r.Name.String));
    }

    static int Main(string[] args) {
        if (args.Length != 4 || (args[3] != "x86" && args[3] != "x64"))
            throw new ArgumentException("source.exe candidate.exe signing-key.snk x86|x64");
        if (File.Exists(args[1])) throw new IOException("Candidate already exists");
        bool x86 = args[3] == "x86";
        using (var module = ModuleDefMD.Load(args[0])) {
            if (!module.IsILOnly || module.EntryPoint == null)
                throw new InvalidOperationException("Only an IL-only managed executable is supported");
            string before = Fingerprint(module);
            module.Machine = x86 ? Machine.I386 : Machine.AMD64;
            module.Is32BitRequired = x86;
            module.Is32BitPreferred = false;
            if (x86) module.Characteristics |= Characteristics.Bit32Machine;
            else module.Characteristics &= ~Characteristics.Bit32Machine;
            var options = new ModuleWriterOptions(module);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            options.InitializeStrongNameSigning(module, new StrongNameKey(args[2]));
            module.Write(args[1], options);
            using (var result = ModuleDefMD.Load(args[1])) {
                if (before != Fingerprint(result) || result.Machine != module.Machine ||
                    result.Is32BitRequired != x86 || result.Is32BitPreferred ||
                    !result.IsStrongNameSigned)
                    throw new InvalidOperationException("Architecture/assembly identity validation failed");
            }
        }
        Console.WriteLine(args[3] + " candidate verified: " + args[1]);
        return 0;
    }
}
