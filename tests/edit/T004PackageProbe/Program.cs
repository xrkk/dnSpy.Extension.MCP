using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class Program {
    static int Main(string[] args) {
        try {
            if (args.Length != 2) throw new ArgumentException("usage: P03StoreHarness <one-commit-package> <original-module>");
            using var catalog = new EditSchemaCatalog();
            var store = new InMemoryEditCheckpointStore();
            var id = Path.GetFileNameWithoutExtension(args[0]);
            store.FinalizeTemp(store.CreateTemp(id, File.ReadAllBytes(args[0])), false);
            using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
            var lineage = history.Load(id);
            var head = lineage.Manifest.HeadCheckpointId;
            var root = lineage.Manifest.Checkpoints.Single(c => c.ParentCheckpointId == null).CheckpointId;
            if (lineage.Manifest.Checkpoints.Single(c => c.CheckpointId == head).ParentCheckpointId != root)
                throw new ArgumentException("requires one commit");
            using var live = ModuleDefMD.Load(args[1]);
            using var corlib = ModuleDefMD.Load(typeof(object).Assembly.Location);
            var resolver = new AssemblyResolver();
            resolver.AddToCache(corlib.Assembly);
            live.Context = new ModuleContext(resolver);
            var map = new Dictionary<string, IMDTokenProvider>();
            var operations = lineage.Operations[head].Operations;
            for (var i = 0; i < operations.Count; i++) {
                using var forward = EditHistoryModule.ExpandedForward(lineage, operations[i]);
                EditOperationRegistry.ApplyPersisted(live, forward.RootElement, map, i);
            }
            Console.WriteLine("REPLAY complete");
            var headAssessment = history.Assess(id, head, "");
            if (EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)) != headAssessment.ImageSha256)
                throw new Exception("resolver-backed live image mismatch");
            var undo = history.PlanNavigation(lineage, head, root);
            Console.WriteLine("PLAN complete");
            undo.Apply(live);
            var target = history.Assess(id, root, "");
            if (EditWire.Sha256(EditWorkspace.WriteCheckpointImage(live)) != target.ImageSha256)
                throw new Exception("undo image mismatch");
            Console.WriteLine("PASS CLR48 package undo image");
            return 0;
        }
        catch (Exception error) {
            Console.WriteLine(error);
            if (error is EditDomainException domain) Console.WriteLine(JsonSerializer.Serialize(domain.Details, EditWire.JsonOptions));
            return 1;
        }
    }
}
