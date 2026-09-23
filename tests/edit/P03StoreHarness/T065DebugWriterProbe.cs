using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnlib.PE;
using dnSpy.Extension.MCP.Editing;

internal static class T065DebugWriterProbe {
    static void Check(bool condition, string name) {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    internal static void Run(string fixture) {
        var bytes = File.ReadAllBytes(fixture);
        using var live = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
        Check(live.PdbState == null, "fixture without sidecar has no PDB state");
        Check(live.Metadata.PEImage.ImageDebugDirectories.Any(d => d.Type == ImageDebugType.Reproducible),
            "source has legitimate reproducible debug entry");
        using var copy = ModuleDefMD.Load(EditWorkspace.Write(live));
        Check(copy.PdbState == null && !copy.Metadata.PEImage.ImageDebugDirectories.Any(d => d.Type == ImageDebugType.Reproducible),
            "no-PDB private copy lacks inherited reproducible writer policy");
        live.SetPdbState(new PdbState(live, PdbFileKind.EmbeddedPortablePDB));
        copy.SetPdbState(new PdbState(copy, PdbFileKind.EmbeddedPortablePDB));
        var liveImage = EditWorkspace.WriteCheckpointImage(live);
        var copyImage = EditWorkspace.WriteCheckpointImage(copy);
        Check(liveImage.SequenceEqual(copyImage), "live and private embedded images are byte-identical");
        Check(liveImage.SequenceEqual(EditWorkspace.WriteCheckpointImage(live))
            && copyImage.SequenceEqual(EditWorkspace.WriteCheckpointImage(copy)), "repeated checkpoint writes are stable");
        using var materialized = ModuleDefMD.Load(liveImage);
        var debug = materialized.Metadata.PEImage.ImageDebugDirectories.Select(d => d.Type).ToArray();
        Check(debug.SequenceEqual(new[] { ImageDebugType.CodeView, ImageDebugType.PdbChecksum,
            ImageDebugType.Reproducible, ImageDebugType.EmbeddedPortablePdb }),
            "CodeView/checksum/reproducible/embedded entries remain nonempty where required");
        Check(materialized.PdbState?.PdbFileKind == PdbFileKind.EmbeddedPortablePDB,
            "emitted PDB reloads from image without sidecar");
    }
}
