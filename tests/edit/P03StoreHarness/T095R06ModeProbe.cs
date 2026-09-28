using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class T095R06ModeProbe {
    static void Check(bool yes, string label) {
        if (!yes) throw new Exception("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }
    static byte[] Image(ModuleDef module) => EditWorkspace.WriteCheckpointImage(module);
    static string Operation(object value) => JsonSerializer.Serialize(value, EditWire.JsonOptions);
    static EditPreparedHistoryWrite Commit(EditHistoryModule history, ModuleDef live, EditHistoryBinding binding, params string[] operations) {
        using var workspace = EditWorkspace.CreateForTesting(live);
        workspace.HistoryFormat = history.FormatForBinding(workspace, binding);
        using var mode = workspace.UseHistoryMode();
        var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
        foreach (var row in operations) {
            using var operation = JsonDocument.Parse(row);
            EditOperationRegistry.ApplyPersisted(workspace.PrivateModule, operation.RootElement, workspace.ObjectIds, workspace.NormalizedOperations.Count);
            workspace.NormalizedOperations.Add(row);
        }
        var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-r06", 1, Array.Empty<string>());
        for (var i = 0; i < operations.Length; i++) {
            using var operation = JsonDocument.Parse(operations[i]);
            EditOperationRegistry.ApplyPersisted(live, operation.RootElement, map, i);
        }
        history.Finalize(prepared, live);
        return prepared;
    }
    static EditHistoryBinding At(EditLoadedLineage lineage) => new() {
        FamilyId = lineage.Manifest.FamilyId, LineageId = lineage.Manifest.LineageId,
        BaseCheckpointId = lineage.Manifest.HeadCheckpointId,
    };
    public static void CreateUnusedReferenceFixture(string source, string destination) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var scope = module.GetAssemblyRefs().Single(x => x.Name == "System.Runtime");
        var unused = new TypeRefUser(module, "System", "String", scope);
        var used = new TypeRefUser(module, "System", "IDisposable", scope);
        owner.Fields.Add(new FieldDefUser("TemporaryString", new FieldSig(new ClassSig(unused)), FieldAttributes.Public));
        owner.Fields.Add(new FieldDefUser("KeptDisposable", new FieldSig(new ClassSig(used)), FieldAttributes.Public));
        var first = Image(module);
        using var persisted = ModuleDefMD.Load(first);
        var reloadedOwner = persisted.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        reloadedOwner.Fields.Single(f => f.Name == "TemporaryString").FieldSig = new FieldSig(new ClassSig(persisted.GetTypeRefs().Single(x => x.FullName == "System.IDisposable")));
        var second = Image(persisted);
        using var final = ModuleDefMD.Load(second);
        Console.WriteLine($"UNUSED_FIXTURE refs={final.TablesStream.TypeRefTable.Rows} string={final.GetTypeRefs().Single(x => x.FullName == "System.String").Rid} disposable={final.GetTypeRefs().Single(x => x.FullName == "System.IDisposable").Rid}");
        File.WriteAllBytes(destination, second);
    }

    public static void CreateDuplicateScopeFixture(string source, string destination) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var a = new AssemblyRefUser(new AssemblyNameInfo("R07ScopeA, Version=1.0.0.0"));
        var b = new AssemblyRefUser(new AssemblyNameInfo("R07ScopeB, Version=1.0.0.0"));
        owner.Fields.Add(new FieldDefUser("SameA", new FieldSig(new ClassSig(new TypeRefUser(module, "Demo", "Same", a))), FieldAttributes.Public));
        owner.Fields.Add(new FieldDefUser("SameB", new FieldSig(new ClassSig(new TypeRefUser(module, "Demo", "Same", b))), FieldAttributes.Public));
        var image = Image(module);
        using var final = ModuleDefMD.Load(image);
        var duplicates = final.GetTypeRefs().Where(x => x.FullName == "Demo.Same").ToArray();
        Check(duplicates.Length == 2 && duplicates[0].ResolutionScope?.ToString() != duplicates[1].ResolutionScope?.ToString(),
            "fixture has same-name TypeRefs in distinct assembly scopes");
        File.WriteAllBytes(destination, image);
    }

    public static void V3FreshProcessExport(string fixture, string directory) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore(directory);
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var method = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods.Single(m => m.Name == "RemoveMe");
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{method.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=true,
                locals=new object[] { new { type="System.Int32", name="restartLocal" } },
                instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, body);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "package.zip"), store.FinalBytes(first.Lineage.Manifest.LineageId));
        File.WriteAllBytes(Path.Combine(directory, "head.dll"), Image(live));
        Console.WriteLine("EXPORTED fresh-process package/head");
    }
    public static void V3FreshProcessReopen(string directory) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore(directory);
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var bytes = File.ReadAllBytes(Path.Combine(directory, "package.zip"));
        var manifest = history.ValidatePackageForTesting(bytes).Manifest;
        var temp = store.CreateTemp(manifest.LineageId, bytes);
        store.FinalizeTemp(temp, replaceExisting:false);
        var lineage = history.Load(manifest.LineageId);
        using var live = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(directory, "head.dll")));
        var head = lineage.Manifest.HeadCheckpointId;
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        var headImage = Image(live);
        history.PlanNavigation(lineage, head, root).Apply(live);
        Check(Image(live).SequenceEqual(lineage.BaselineBytes), "fresh-process Undo exact root without projection cache");
        history.PlanNavigation(lineage, root, head).Apply(live);
        Check(Image(live).SequenceEqual(headImage), "fresh-process Redo exact head without projection cache");
    }

    public static void CreateLargeFixture(string source, string destination, int size) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var bytes = new byte[size];
        new Random(910).NextBytes(bytes);
        module.Resources.Add(new EmbeddedResource("R10LargeResource", bytes, ManifestResourceAttributes.Private));
        var image = Image(module);
        File.WriteAllBytes(destination, image);
        Console.WriteLine($"LARGE_FIXTURE resource={size} image={image.Length} sha={EditWire.Sha256(image)}");
    }

    public static void V3SpillBound(string package, long cap, int cancelAfterMs) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        Environment.SetEnvironmentVariable("T095_R07_TRACE_COST", "1");
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        using var cancellation = new System.Threading.CancellationTokenSource();
        if (cancelAfterMs > 0) cancellation.CancelAfter(cancelAfterMs);
        try {
            var loaded = history.ValidatePackageForTesting(File.ReadAllBytes(package), cap, cancellation.Token);
            Check(cancelAfterMs == 0 && loaded.Manifest.Checkpoints.Count > 0, "bounded spill package accepted");
        }
        catch (OperationCanceledException) when (cancelAfterMs > 0) {
            Console.WriteLine("CANCELED bounded spill parse");
        }
        catch (EditDomainException ex) when (package.Contains("tampered", StringComparison.Ordinal) && ex.Code == "EDIT_CHECKPOINT_INVALID") {
            Console.WriteLine("REJECT tampered package and spill cleaned");
        }
    }

    static void CheckReferenceBranchAndCompensation(EditHistoryModule history, EditLoadedLineage lineage,
        EditPreparedHistoryWrite first, ModuleDefMD live, string root, byte[] rootImage, string label) {
        var head = first.PostHeadCheckpointId;
        var headImage = Image(live);
        var compensate = history.PlanNavigation(lineage, head, root).Apply(live);
        Check(Image(live).SequenceEqual(rootImage), label + " compensation setup exact root");
        compensate();
        Check(Image(live).SequenceEqual(headImage), label + " compensation restores exact head");
        history.PlanNavigation(lineage, head, root).Apply(live);
        history.Finalize(history.PrepareHeadMove(lineage.Manifest.LineageId, head, root, "r10-branch-root"), live);
        lineage = history.Load(lineage.Manifest.LineageId);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var branchOp = label == "MultiMethod"
            ? Operation(new { kind="method_add", owner_type=new { token=$"0x{owner.MDToken.Raw:x8}" },
                name="R12BranchMethod", signature=new { return_type="System.Int32", has_this=false,
                    generic_parameters=Array.Empty<object>(), parameters=Array.Empty<object>() },
                attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static),
                body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                    instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } },
                    exception_handlers=Array.Empty<object>() } })
            : Operation(new { kind="field_add", owner_type=new { token=$"0x{owner.MDToken.Raw:x8}" },
                name="R10Branch" + label, field_type="System.Int32", attributes=(uint)FieldAttributes.Public });
        var branch = Commit(history, live, At(lineage), branchOp);
        lineage = history.Load(branch.Lineage.Manifest.LineageId);
        var branchImage = Image(live);
        history.PlanNavigation(lineage, branch.PostHeadCheckpointId, head).Apply(live);
        Check(Image(live).SequenceEqual(headImage), label + " branch to original head exact");
        history.PlanNavigation(lineage, head, branch.PostHeadCheckpointId).Apply(live);
        Check(Image(live).SequenceEqual(branchImage), label + " branch return exact");
    }

    public static void V3MethodSpec(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var oldRows = live.TablesStream.MethodSpecTable.Rows;
        var type = live.GetTypeRefs().First();
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var method = owner.Methods.Single(m => m.Name == "RemoveMe");
        var generic = Operation(new { kind="method_add", owner_type=new { token=$"0x{owner.MDToken.Raw:x8}" }, name="R10Generic",
            signature=new { return_type="System.Int32", has_this=false, generic_parameters=new object[] { new { name="T" } }, parameters=Array.Empty<object>() },
            attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static),
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(), instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var reference = Operation(new { kind="reference_add", reference=new { form="method_spec", method=new { object_id="obj-000-00" },
            arguments=new object[] { new { Kind="ClassSig", Reference=$"0x{type.MDToken.Raw:x8}" } } } });
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{method.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="call", operand=new { kind="object", object_id="obj-001-00" } },
                    new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, generic, reference, body);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.TablesStream.MethodSpecTable.Rows > oldRows, "MethodSpec legal generic call adds physical row");
        var methodOperand = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
            .Methods.Single(m => m.Name == "RemoveMe").Body.Instructions.First().Operand as MethodSpec;
        Check(methodOperand != null && methodOperand.Rid > oldRows && methodOperand.Method?.Name == "R10Generic"
            && methodOperand.GenericInstMethodSig?.GenericArguments.Count == 1,
            "MethodSpec token, generic argument and instruction owner survive disk reload");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "MethodSpec Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256, "MethodSpec Redo exact head cycle=" + cycle);
        }
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "MethodSpec");
    }

    public static void FixtureTokens(string source, string destination) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var method = owner.Methods.Single(m => m.Name == "RemoveMe");
        var type = module.GetTypeRefs().First();
        Check(!module.GetTypeRefs().Any(t => t.FullName == "System.Object"), "VM fixture has no Object TypeRef");
        var result = new { source_sha256=EditWire.Sha256(File.ReadAllBytes(source)),
            assembly_name=module.Assembly?.Name?.String, owner_type=$"0x{owner.MDToken.Raw:x8}",
            other_owner_type=module.GetTypes().FirstOrDefault(t => t.FullName == "NoObjectFixture.OtherGenericOwner") is TypeDef other
                ? $"0x{other.MDToken.Raw:x8}" : null,
            remove_me=$"0x{method.MDToken.Raw:x8}", first_type_ref=$"0x{type.MDToken.Raw:x8}",
            type_ref_rows=module.TablesStream.TypeRefTable.Rows,
            member_ref_rows=module.TablesStream.MemberRefTable.Rows,
            standalone_sig_rows=module.TablesStream.StandAloneSigTable.Rows,
            type_spec_rows=module.TablesStream.TypeSpecTable.Rows,
            method_spec_rows=module.TablesStream.MethodSpecTable.Rows };
        File.WriteAllText(destination, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    public static void ReadbackFour(string label, string baseline, string head) {
        using var root = ModuleDefMD.Load(File.ReadAllBytes(baseline));
        using var module = ModuleDefMD.Load(File.ReadAllBytes(head));
        var method = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
            .Methods.Single(m => m.Name == "RemoveMe");
        switch (label) {
        case "multi-method":
            var members = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods
                .Where(m => m.Name == "R12Generic").ToArray();
            var other = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner").Methods
                .Where(m => m.Name == "R12Generic").ToArray();
            Check(module.TablesStream.MethodTable.Rows == root.TablesStream.MethodTable.Rows + 3
                && members.Length == 2 && other.Length == 1
                && members.Any(m => m.MethodSig.Params.Count == 0)
                && members.Any(m => m.MethodSig.Params.Count == 1)
                && other[0].MethodSig.Params.Count == 0,
                "three added MethodDef physical rows retain owner and overload identity"); break;
        case "standalone":
            Check(module.TablesStream.StandAloneSigTable.Rows > root.TablesStream.StandAloneSigTable.Rows
                && method.Body.Variables.Count == 1, "StandAloneSig physical row and local readback"); break;
        case "typespec":
            Check(module.TablesStream.TypeSpecTable.Rows > root.TablesStream.TypeSpecTable.Rows
                && method.Body.Instructions.Any(i => i.Operand is TypeSpec), "TypeSpec physical row and IL operand readback"); break;
        case "memberref":
            Check(module.TablesStream.MemberRefTable.Rows > root.TablesStream.MemberRefTable.Rows
                && method.Body.Instructions.Any(i => i.Operand is MemberRef), "MemberRef physical row and IL operand readback"); break;
        case "methodspec":
            Check(module.TablesStream.MethodSpecTable.Rows > root.TablesStream.MethodSpecTable.Rows
                && method.Body.Instructions.Any(i => i.Operand is MethodSpec spec
                    && spec.Method?.Name == "R10Generic" && spec.GenericInstMethodSig?.GenericArguments.Count == 1),
                "MethodSpec physical row and generic IL operand readback"); break;
        case "assemblyref":
            var fields = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
                .Fields.Where(f => f.Name.String?.StartsWith("R11External", StringComparison.Ordinal) == true).ToArray();
            Check(module.TablesStream.AssemblyRefTable.Rows > root.TablesStream.AssemblyRefTable.Rows
                && module.TablesStream.TypeRefTable.Rows > root.TablesStream.TypeRefTable.Rows
                && fields.Length == 2 && fields[0].FieldType.ToTypeDefOrRef()?.MDToken == fields[1].FieldType.ToTypeDefOrRef()?.MDToken,
                "shared AssemblyRef/TypeRef physical rows and field bindings readback"); break;
        default: throw new ArgumentException("invalid four-table label");
        }
        Console.WriteLine($"READBACK {label} root={EditWire.Sha256(File.ReadAllBytes(baseline))} head={EditWire.Sha256(File.ReadAllBytes(head))}");
    }

    public static void CreateMethodPtrFixture(string source, string destination) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var first = owner.Methods[0];
        owner.Methods.RemoveAt(0);
        owner.Methods.Add(first);
        var bytes = Image(module);
        using var reloaded = ModuleDefMD.Load(bytes);
        Console.WriteLine($"METHODPTR rows={reloaded.TablesStream.MethodPtrTable.Rows} methods={reloaded.TablesStream.MethodTable.Rows}");
        Check(reloaded.TablesStream.MethodPtrTable.Rows > 0, "method reorder produces physical MethodPtr table");
        File.WriteAllBytes(destination, bytes);
    }

    public static void CreateMultiMethodFixture(string source, string destination) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(source));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        module.Types.Add(new TypeDefUser("NoObjectFixture", "OtherGenericOwner", owner.BaseType)
            { Attributes=owner.Attributes });
        File.WriteAllBytes(destination, Image(module));
        Console.WriteLine("MULTI_FIXTURE " + EditWire.Sha256(File.ReadAllBytes(destination)));
    }

    public static void InspectMethodRows(string path) {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(path));
        Console.WriteLine($"IMAGE {EditWire.Sha256(File.ReadAllBytes(path))} methods={module.TablesStream.MethodTable.Rows} ptr={module.TablesStream.MethodPtrTable.Rows}");
        foreach (var type in module.GetTypes()) {
            Console.WriteLine($"TYPE {type.MDToken.Raw:x8} {type.FullName}");
            foreach (var method in type.Methods) Console.WriteLine($"METHOD {method.MDToken.Raw:x8} {method.Name} {method.MethodSig}");
        }
        for (uint rid=1; rid<=module.TablesStream.MethodPtrTable.Rows; rid++) {
            module.TablesStream.TryReadMethodPtrRow(rid, out var row);
            Console.WriteLine($"PTR {rid} -> {row.Method}");
        }
    }

    public static void OrdinaryMethodDeletePreservesRow(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var module = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        var owner = module.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var method = owner.Methods.Single(m => m.Name == "RemoveMe");
        var originalRid = method.Rid;
        using var operation = JsonDocument.Parse(Operation(new { kind="method_remove",
            target=new { token=$"0x{method.MDToken.Raw:x8}" }, remove_mode="reject_if_referenced" }));
        using var mode = EditDeletedRowsTombstone.UseLegacy(false);
        using var verified = EditDeletedRowsTombstone.UseVerifiedV3(module);
        var undo = EditOperationRegistry.ApplyPersisted(module, operation.RootElement,
            new Dictionary<string, IMDTokenProvider>(), 0);
        var deleted = Image(module);
        using var persisted = ModuleDefMD.Load(deleted);
        using (EditDeletedRowsTombstone.UseVerifiedV3(persisted)) {
            var tombstone = persisted.GetTypes().Single(EditDeletedRowsTombstone.IsTombstone);
            Check(tombstone.Methods.Count == 1 && tombstone.Methods[0].Rid == originalRid
                && tombstone.Methods[0].Name == "RemoveMe", "ordinary method_remove retains original row and user method content");
        }
        undo.Undo();
        Check(owner.Methods.Any(m => ReferenceEquals(m, method)), "ordinary method_remove compensation restores same live method");
    }

    public static void V3VerifierLifecycle(string package) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        Environment.SetEnvironmentVariable("T095_R07_TRACE_COST", "1");
        var bytes = File.ReadAllBytes(package);
        using var catalog = new EditSchemaCatalog();
        using var history = new EditHistoryModule(new InMemoryEditCheckpointStore(), catalog.CheckpointPackage);
        using (var cancellation = new System.Threading.CancellationTokenSource()) {
            cancellation.CancelAfter(100);
            using var scope = history.UseVerificationCancellation(cancellation.Token);
            try { history.ValidatePackageForTesting(bytes, 8192); throw new Exception("FAIL ambient cancellation ignored"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS ambient verifier cancellation interrupts replay"); }
        }
        var directory = Path.GetTempPath();
        Check(!Directory.EnumerateFiles(directory, "dnspy-v3-replay-*.tmp").Any(),
            "ambient cancellation leaves no verifier temp");
        var first = System.Threading.Tasks.Task.Run(() => {
            using var copy = new EditHistoryModule(new InMemoryEditCheckpointStore(), catalog.CheckpointPackage);
            return copy.ValidatePackageForTesting(bytes, 8192).Manifest.Checkpoints.Count;
        });
        var second = System.Threading.Tasks.Task.Run(() => {
            using var copy = new EditHistoryModule(new InMemoryEditCheckpointStore(), catalog.CheckpointPackage);
            return copy.ValidatePackageForTesting(bytes, 8192).Manifest.Checkpoints.Count;
        });
        System.Threading.Tasks.Task.WaitAll(first, second);
        Check(first.Result == second.Result && first.Result > 0,
            "two concurrent verifier calls both accept under shared process lease");
        Check(!Directory.EnumerateFiles(directory, "dnspy-v3-replay-*.tmp").Any(),
            "parallel verifier calls leave no temp");
    }

    public static void V3MethodSpecCollisions(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var seed = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        var seedOwner = seed.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        if (!seed.GetTypes().Any(t => t.FullName == "NoObjectFixture.OtherGenericOwner"))
            seed.Types.Add(new TypeDefUser("NoObjectFixture", "OtherGenericOwner", seedOwner.BaseType)
                { Attributes = seedOwner.Attributes });
        using var typedSeed = ModuleDefMD.Load(Image(seed));
        var typedOwner = typedSeed.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var otherSeed = typedSeed.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner");
        string Generic(TypeDef targetOwner, object[] parameters) => Operation(new {
            kind="method_add", owner_type=new { token=$"0x{targetOwner.MDToken.Raw:x8}" }, name="R11Generic",
            signature=new { return_type="System.Int32", has_this=false,
                generic_parameters=new object[] { new { name="T" } }, parameters },
            attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static),
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } },
                exception_handlers=Array.Empty<object>() } });
        var seedMap = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
        var seedOps = new[] { Generic(typedOwner, Array.Empty<object>()), Generic(otherSeed, Array.Empty<object>()),
            Generic(typedOwner, new object[] { new { type="System.Int32" } }) };
        for (var i=0; i<seedOps.Length; i++) {
            using var json = JsonDocument.Parse(seedOps[i]);
            EditOperationRegistry.ApplyPersisted(typedSeed, json.RootElement, seedMap, i);
        }
        using var live = ModuleDefMD.Load(Image(typedSeed));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var initialRows = live.TablesStream.MethodSpecTable.Rows;
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var other = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner");
        var target = owner.Methods.Single(m => m.Name == "RemoveMe");
        var a = owner.Methods.Single(m => m.Name == "R11Generic" && m.MethodSig.Params.Count == 0);
        var b = other.Methods.Single(m => m.Name == "R11Generic");
        var overload = owner.Methods.Single(m => m.Name == "R11Generic" && m.MethodSig.Params.Count == 1);
        var type = live.GetTypeRefs().First();
        var classArg = new { Kind="ClassSig", Reference=$"0x{type.MDToken.Raw:x8}" };
        var arrayArg = new { Kind="SZArraySig", Children=new object[] { classArg } };
        string Spec(MethodDef method, object argument) => Operation(new { kind="reference_add",
            reference=new { form="method_spec", method=new { token=$"0x{method.MDToken.Raw:x8}" }, arguments=new[] { argument } } });
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{target.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] {
                    new { opcode="call", operand=new { kind="object", object_id="obj-000-00" } }, new { opcode="pop" },
                    new { opcode="call", operand=new { kind="object", object_id="obj-001-00" } }, new { opcode="pop" },
                    new { opcode="ldc.i4.0" }, new { opcode="call", operand=new { kind="object", object_id="obj-002-00" } }, new { opcode="pop" },
                    new { opcode="call", operand=new { kind="object", object_id="obj-003-00" } }, new { opcode="pop" },
                    new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") },
            Spec(a, classArg), Spec(b, classArg), Spec(overload, classArg), Spec(a, arrayArg), body);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        var instructions = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
            .Methods.Single(m => m.Name == "RemoveMe").Body.Instructions;
        var calls = instructions.Where(i => i.OpCode.Code == dnlib.DotNet.Emit.Code.Call)
            .Select(i => i.Operand as MethodSpec).ToArray();
        Check(calls.Length == 4 && calls.All(x => x != null) && reloaded.TablesStream.MethodSpecTable.Rows >= initialRows + 4,
            "four physical MethodSpec rows are read from disk");
        Check(calls[0]!.Method?.DeclaringType?.FullName == "NoObjectFixture.Members"
            && calls[1]!.Method?.DeclaringType?.FullName == "NoObjectFixture.OtherGenericOwner"
            && calls[2]!.Method?.MethodSig?.Params.Count == 1
            && calls[3]!.GenericInstMethodSig?.GenericArguments.Single().ElementType == ElementType.SZArray,
            "owner, overload signature and generic arguments remain distinct on disk");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "collision Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256,
                "collision Redo exact head cycle=" + cycle);
        }
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "MethodSpecCollision");
    }

    public static void V3MultiMethodAdd(string fixture, bool withReferences = false, string? outputPath = null) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var seed = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        var seedOwner = seed.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        if (!seed.GetTypes().Any(t => t.FullName == "NoObjectFixture.OtherGenericOwner"))
            seed.Types.Add(new TypeDefUser("NoObjectFixture", "OtherGenericOwner", seedOwner.BaseType)
                { Attributes = seedOwner.Attributes });
        using var live = ModuleDefMD.Load(Image(seed));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var other = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner");
        string Generic(TypeDef targetOwner, object[] parameters) => Operation(new {
            kind="method_add", owner_type=new { token=$"0x{targetOwner.MDToken.Raw:x8}" }, name="R12Generic",
            signature=new { return_type="System.Int32", has_this=false,
                generic_parameters=new object[] { new { name="T" } }, parameters },
            attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static),
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } },
                exception_handlers=Array.Empty<object>() } });
        var operations = new List<string> { Generic(owner, Array.Empty<object>()), Generic(other, Array.Empty<object>()),
            Generic(owner, new object[] { new { type="System.Int32" } }) };
        if (withReferences) {
            var type = live.GetTypeRefs().First();
            var argument = new { Kind="ClassSig", Reference=$"0x{type.MDToken.Raw:x8}" };
            for (var i=0; i<3; i++) operations.Add(Operation(new { kind="reference_add",
                reference=new { form="method_spec", method=new { object_id=$"obj-{i:D3}-00" },
                    arguments=new[] { argument } } }));
            var target = owner.Methods.Single(m => m.Name == "RemoveMe");
            operations.Add(Operation(new { kind="method_body_replace", target=new { token=$"0x{target.MDToken.Raw:x8}" },
                body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                    instructions=new object[] { new { opcode="call", operand=new { kind="object", object_id="obj-003-00" } },
                        new { opcode="pop" }, new { opcode="call", operand=new { kind="object", object_id="obj-004-00" } },
                        new { opcode="pop" }, new { opcode="ldc.i4.0" },
                        new { opcode="call", operand=new { kind="object", object_id="obj-005-00" } },
                        new { opcode="pop" }, new { opcode="ldc.i4.0" }, new { opcode="ret" } },
                    exception_handlers=Array.Empty<object>() } }));
        }
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, operations.ToArray());
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        var headImage = Image(reloaded);
        if (outputPath != null) File.WriteAllBytes(outputPath, headImage);
        if (withReferences) {
            var calls = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
                .Methods.Single(m => m.Name == "RemoveMe").Body.Instructions
                .Where(i => i.OpCode.Code == dnlib.DotNet.Emit.Code.Call).Select(i => i.Operand as MethodSpec).ToArray();
            Check(calls.Length == 3 && calls.All(x => x != null)
                && calls[0]!.Method?.DeclaringType?.FullName == "NoObjectFixture.Members"
                && calls[1]!.Method?.DeclaringType?.FullName == "NoObjectFixture.OtherGenericOwner"
                && calls[2]!.Method?.MethodSig?.Params.Count == 1,
                "new method identities remain distinct through shared MethodSpec references");
        }
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "multi method Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(headImage), "multi method Redo exact head cycle=" + cycle);
        }
        var beforePackage = store.FinalBytes(lineage.Manifest.LineageId);
        var injected = history.PlanNavigation(lineage, first.PostHeadCheckpointId, root);
        typeof(EditHistoryNavigationPlan).GetField("afterImageSha256", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(injected, new string('0', 64));
        try { injected.Apply(reloaded); throw new Exception("FAIL injected navigation mismatch accepted"); }
        catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
        Check(Image(reloaded).SequenceEqual(headImage) && store.FinalBytes(lineage.Manifest.LineageId).SequenceEqual(beforePackage)
            && history.Load(lineage.Manifest.LineageId).Manifest.HeadCheckpointId == first.PostHeadCheckpointId,
            "injected post-operation image failure restores live, package and head");
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "MultiMethod");
    }

    public static void V3MultiCheckpointMethodAdd(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var seed = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        var seedOwner = seed.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        seed.Types.Add(new TypeDefUser("NoObjectFixture", "OtherGenericOwner", seedOwner.BaseType)
            { Attributes = seedOwner.Attributes });
        var live = ModuleDefMD.Load(Image(seed));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var other = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner");
        string Add(TypeDef target, int parameters) => Operation(new { kind="method_add",
            owner_type=new { token=$"0x{target.MDToken.Raw:x8}" }, name="R12Across",
            signature=new { return_type="System.Int32", has_this=false,
                generic_parameters=new object[] { new { name="T" } },
                parameters=parameters == 0 ? Array.Empty<object>() : new object[] { new { type="System.Int32" } } },
            attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static),
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } },
                exception_handlers=Array.Empty<object>() } });
        var images = new List<byte[]> { Image(live) };
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, Add(owner,0));
        images.Add(Image(live));
        live.Dispose(); live = ModuleDefMD.Load(images[1]);
        other = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.OtherGenericOwner");
        var second = Commit(history, live, At(history.Load(first.Lineage.Manifest.LineageId)), Add(other,0));
        images.Add(Image(live));
        live.Dispose(); live = ModuleDefMD.Load(images[2]);
        owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var third = Commit(history, live, At(history.Load(second.Lineage.Manifest.LineageId)), Add(owner,1));
        images.Add(Image(live));
        live.Dispose();
        var lineage = history.Load(third.Lineage.Manifest.LineageId);
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        var checkpoints = new[] { root, first.PostHeadCheckpointId, second.PostHeadCheckpointId, third.PostHeadCheckpointId };
        using var reloaded = ModuleDefMD.Load(images[3]);
        for (var cycle=0; cycle<2; cycle++) {
            for (var i=3; i>0; i--) {
                history.PlanNavigation(lineage, checkpoints[i], checkpoints[i-1]).Apply(reloaded);
                Check(Image(reloaded).SequenceEqual(images[i-1]), "multi checkpoint Undo exact level=" + i + " cycle=" + cycle);
            }
            for (var i=1; i<4; i++) {
                history.PlanNavigation(lineage, checkpoints[i-1], checkpoints[i]).Apply(reloaded);
                Check(Image(reloaded).SequenceEqual(images[i]), "multi checkpoint Redo exact level=" + i + " cycle=" + cycle);
            }
        }
    }

    public static void V3MemberRef(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var oldRows = live.TablesStream.MemberRefTable.Rows;
        var type = live.GetTypeRefs().First();
        var method = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods.Single(m => m.Name == "RemoveMe");
        var reference = Operation(new { kind="reference_add", reference=new { form="member_ref", member_kind="field",
            owner=new { token=$"0x{type.MDToken.Raw:x8}" }, name="R10ExternalField",
            signature=new { Kind="FieldSig", Convention=(byte)6, Result=new { Kind="ClassSig", Reference=$"0x{type.MDToken.Raw:x8}" } } } });
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{method.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="ldsfld", operand=new { kind="object", object_id="obj-000-00" } },
                    new { opcode="pop" }, new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, reference, body);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.TablesStream.MemberRefTable.Rows > oldRows, "MemberRef legal ldsfld adds physical row");
        var memberOperand = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
            .Methods.Single(m => m.Name == "RemoveMe").Body.Instructions.First().Operand as MemberRef;
        Check(memberOperand != null && memberOperand.Rid > oldRows && memberOperand.Name == "R10ExternalField"
            && memberOperand.DeclaringType?.FullName == type.FullName,
            "MemberRef token, scope and instruction owner survive disk reload");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "MemberRef Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256, "MemberRef Redo exact head cycle=" + cycle);
        }
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "MemberRef");
    }

    public static void V3TypeSpec(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var oldRows = live.TablesStream.TypeSpecTable.Rows;
        var type = live.GetTypeRefs().First();
        var method = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods.Single(m => m.Name == "RemoveMe");
        var reference = Operation(new { kind="reference_add", reference=new { form="type_spec",
            signature=new { Kind="SZArraySig", Children=new object[] { new { Kind="ClassSig", Reference=$"0x{type.MDToken.Raw:x8}" } } } } });
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{method.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=false, locals=Array.Empty<object>(),
                instructions=new object[] { new { opcode="ldtoken", operand=new { kind="object", object_id="obj-000-00" } },
                    new { opcode="pop" }, new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, reference, body);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.TablesStream.TypeSpecTable.Rows > oldRows, "TypeSpec legal ldtoken adds physical row");
        var typeOperand = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members")
            .Methods.Single(m => m.Name == "RemoveMe").Body.Instructions.First().Operand as TypeSpec;
        Check(typeOperand != null && typeOperand.Rid > oldRows && typeOperand.TypeSig is SZArraySig,
            "TypeSpec token, array signature and instruction owner survive disk reload");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "TypeSpec Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256, "TypeSpec Redo exact head cycle=" + cycle);
        }
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "TypeSpec");
    }

    public static void V3StandAloneSig(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var oldRows = live.TablesStream.StandAloneSigTable.Rows;
        var method = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods.Single(m => m.Name == "RemoveMe");
        var body = Operation(new { kind="method_body_replace", target=new { token=$"0x{method.MDToken.Raw:x8}" },
            body=new { max_stack=1, init_locals=true,
                locals=new object[] { new { type="System.Int32", name="r10Local" } },
                instructions=new object[] { new { opcode="ldc.i4.0" }, new { opcode="ret" } }, exception_handlers=Array.Empty<object>() } });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, body);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.TablesStream.StandAloneSigTable.Rows > oldRows, "StandAloneSig legal body adds physical row");
        var reloadedMethod = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Methods.Single(m => m.Name == "RemoveMe");
        var localToken = reloadedMethod.Body.LocalVarSigTok;
        Check((localToken >> 24) == 0x11 && reloaded.ResolveToken(localToken) is StandAloneSig row
            && row.LocalSig?.Locals.Count == 1 && new SigComparer().Equals(row.LocalSig.Locals[0], reloadedMethod.Body.Variables[0].Type),
            "StandAloneSig token and local owner survive disk reload");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "StandAloneSig Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256, "StandAloneSig Redo exact head cycle=" + cycle);
        }
        CheckReferenceBranchAndCompensation(history, lineage, first, reloaded, root, rootImage, "StandAloneSig");
    }

    public static void V3AssemblyScope(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var corlib = live.GetAssemblyRefs().Single(x => x.Name == "System.Runtime");
        var reference = Operation(new { kind="reference_add", reference=new { form="type_ref", @namespace="System", name="IDisposable", scope=new {token=$"0x{corlib.MDToken.Raw:x8}"} } });
        var iface = Operation(new { kind="interface_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"}, @interface=new {reference=new {object_id="obj-000-00"}} });
        var first = Commit(history, live, new EditHistoryBinding{FamilyId=EditWire.NewId("family")}, reference, iface);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV3, "explicit-scope reference additions enter v3 history");
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.GetTypeRefs().Any(x => x.FullName == "System.IDisposable"),
            "new explicit-scope TypeRef is physically present after reload");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
        Check(Image(reloaded).SequenceEqual(rootImage), "reference_add/interface_add introduction Undo restores exact root");
        history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
        Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256,
            "reference_add/interface_add introduction Redo restores exact head");
    }

    public static void V3AssemblyBadScope(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var root = Image(live);
        var assembly = Operation(new { kind="reference_add", reference=new { form="assembly_ref", name="R10BadScope", version="1.0.0.0" } });
        var bad = Operation(new { kind="reference_add", reference=new { form="type_ref", @namespace="Demo", name="Bad",
            scope=new { object_id="obj-999-00" } } });
        try { Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, assembly, bad);
            throw new Exception("FAIL forged scope accepted"); }
        catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") {
            Check(Image(live).SequenceEqual(root) && store.EnumerateCheckpointObjects().Count == 0,
                "wrong scope object ID rejected before live/store mutation");
        }
    }

    public static void V3NewAssemblyScope(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var rootImage = Image(live);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var corlib = live.GetAssemblyRefs().First();
        var assembly = Operation(new { kind="reference_add", reference=new { form="assembly_ref", name="R09External", version="1.0.0.0" } });
        var reference = Operation(new { kind="reference_add", reference=new { form="type_ref", @namespace="Demo", name="IExternal", scope=new {object_id="obj-000-00"} } });
        var iface = Operation(new { kind="field_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"}, name="ExternalRef", field_type=new { kind="type", type=new { Kind="ClassSig", Reference="obj-001-00" } }, attributes=(uint)FieldAttributes.Public });
        var shared = Operation(new { kind="field_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"}, name="ExternalRefShared", field_type=new { kind="type", type=new { Kind="ClassSig", Reference="obj-001-00" } }, attributes=(uint)FieldAttributes.Public });
        var unused = Operation(new { kind="reference_add", reference=new { form="assembly_ref", name="R10Unused", version="1.0.0.0" } });
        var first = Commit(history, live, new EditHistoryBinding{FamilyId=EditWire.NewId("family")}, assembly, reference, iface, shared, unused);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        if (Environment.GetEnvironmentVariable("T095_R10_ASSEMBLY_PACKAGE") is string output) File.WriteAllBytes(output, store.FinalBytes(lineage.Manifest.LineageId));
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV3, "explicit-scope reference additions enter v3 history");
        using var reloaded = ModuleDefMD.Load(Image(live));
        Check(reloaded.GetAssemblyRefs().Any(x => x.Name == "R09External") && reloaded.GetTypeRefs().Any(x => x.FullName == "Demo.IExternal"),
            "new AssemblyRef and TypeRef physically present after reload");
        Check(!reloaded.GetAssemblyRefs().Any(x => x.Name == "R10Unused"), "unreferenced AssemblyRef remains a graph-only no-op");
        var sharedFields = reloaded.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Fields.Where(f => f.Name.String?.StartsWith("ExternalRef", StringComparison.Ordinal) == true).ToArray();
        Check(sharedFields.Length == 2 && sharedFields[0].FieldType.ToTypeDefOrRef()?.MDToken == sharedFields[1].FieldType.ToTypeDefOrRef()?.MDToken,
            "shared AssemblyRef/TypeRef field signatures bind one persisted row");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        for (var cycle=0; cycle<2; cycle++) {
            history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(reloaded);
            Check(Image(reloaded).SequenceEqual(rootImage), "AssemblyRef shared Undo exact root cycle=" + cycle);
            history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(reloaded);
            Check(EditWire.Sha256(Image(reloaded)) == first.Lineage.Head.ResultImageSha256,
                "AssemblyRef shared Redo exact head cycle=" + cycle);
        }
    }

    public static void V3ObjectAndBranch(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var baseline = Image(live);
        var diagnostic = Environment.GetEnvironmentVariable("T095_R07_DIAG_DIR");
        if (!string.IsNullOrEmpty(diagnostic)) File.WriteAllBytes(Path.Combine(diagnostic, "root.bin"), baseline);
        if (!string.IsNullOrEmpty(diagnostic) && File.Exists(Path.Combine(diagnostic, "inverse-rebuild-typeref.bin"))) {
            foreach (var file in new[] { "root.bin", "inverse-preserve.bin", "inverse-rebuild-typeref.bin" }) {
                using var m = ModuleDefMD.Load(Path.Combine(diagnostic, file));
                Console.WriteLine($"DIAG {file} typeRef={m.TablesStream.TypeRefTable.Rows} memberRef={m.TablesStream.MemberRefTable.Rows} typeSpec={m.TablesStream.TypeSpecTable.Rows} assemblyRef={m.TablesStream.AssemblyRefTable.Rows} standAloneSig={m.TablesStream.StandAloneSigTable.Rows} methodSpec={m.TablesStream.MethodSpecTable.Rows}");
                foreach (var t in m.GetTypeRefs()) Console.WriteLine($"DIAG_ROW {file} {t.MDToken.Raw:x8} {t.FullName} scope={t.ResolutionScope}");
            }
        }
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        if (Environment.GetEnvironmentVariable("T095_R07_DIAG_DIR") != null) {
            var table = live.TablesStream.TypeRefTable;
            Console.WriteLine("TABLE_TYPE " + table.GetType().FullName);
            foreach (var f in table.GetType().GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))
                Console.WriteLine($"TABLE_FIELD {f.Name} {f.FieldType.FullName} {f.GetValue(table)}");
            foreach (var q in table.GetType().GetProperties(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))
                Console.WriteLine($"TABLE_PROPERTY {q.Name} {q.PropertyType.FullName} setter={q.SetMethod != null}");
        }
        Check(!live.GetTypeRefs().Any(t => t.FullName == "System.Object"), "v3 baseline has no Object TypeRef");
        var fieldAdd = Operation(new { kind="field_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"},
            name="ObjectRef", field_type="System.Object", attributes=(uint)FieldAttributes.Public });
        var stringAdd = Operation(new { kind="field_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"},
            name="ExceptionRef", field_type="System.Exception", attributes=(uint)FieldAttributes.Public });
        var typeAdd = Operation(new { kind="type_add", @namespace="NoObjectFixture", name="IntroducedObject",
            base_type="System.Object", attributes=(uint)(TypeAttributes.Public | TypeAttributes.BeforeFieldInit) });
        var first = Commit(history, live, new EditHistoryBinding { FamilyId=EditWire.NewId("family") }, fieldAdd, stringAdd, typeAdd);
        var lineage = history.Load(first.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV3, "new no-Object lineage is v3");
        using var continued = ModuleDefMD.Load(Image(live));
        Check(continued.GetTypeRefs().Any(t => t.FullName == "System.Object"), "legal additions introduce an on-disk Object TypeRef");
        var stillUsedImage = Image(continued);
        var stillUsedRows = continued.TablesStream.TypeRefTable.Rows;
        using (var rootModule = ModuleDefMD.Load(baseline)) {
            var refs = rootModule.GetTypeRefs().OrderBy(x => x.Rid).Select(x => x.FullName + "|" + x.ResolutionScope).ToArray();
            var rowLimits = new[] { rootModule.TablesStream.TypeRefTable.Rows, rootModule.TablesStream.MemberRefTable.Rows,
                rootModule.TablesStream.StandAloneSigTable.Rows, rootModule.TablesStream.TypeSpecTable.Rows,
                rootModule.TablesStream.MethodSpecTable.Rows };
            var currentSemantic = EditHistoryModule.SemanticDigest(EditHistoryModule.PackageFormatV3, continued);
            var impossible = new EditHistoryNavigationPlan(EditHistoryModule.PackageFormatV3,
                Array.Empty<EditHistoryNavigationPlan.Step>(), currentSemantic, currentSemantic,
                EditWire.Sha256(stillUsedImage), false, EditWire.Sha256(baseline), refs, rowLimits);
            try { impossible.Apply(continued); throw new Exception("FAIL in-use reference row removed"); }
            catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") {
                Check(continued.TablesStream.TypeRefTable.Rows == stillUsedRows && Image(continued).SequenceEqual(stillUsedImage),
                    "in-use reference tail refuses shortening and preserves live image");
            }
            if (refs.Length != 0) refs[0] = "wrong-scope";
            var wrongScope = new EditHistoryNavigationPlan(EditHistoryModule.PackageFormatV3,
                Array.Empty<EditHistoryNavigationPlan.Step>(), currentSemantic, currentSemantic,
                EditWire.Sha256(stillUsedImage), false, EditWire.Sha256(baseline), refs, rowLimits);
            try { wrongScope.Apply(continued); throw new Exception("FAIL changed retained reference scope accepted"); }
            catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") {
                Check(continued.TablesStream.TypeRefTable.Rows == stillUsedRows && Image(continued).SequenceEqual(stillUsedImage),
                    "changed retained reference prefix refuses shortening without side effect");
            }
        }

        var continuedOwner = continued.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var removed = continuedOwner.Fields.Single(f => f.Name == "A");
        var fieldRemove = Operation(new { kind="field_remove", target=new {token=$"0x{removed.MDToken.Raw:x8}"}, remove_mode="reject_if_referenced" });
        var nested = continuedOwner.NestedTypes.Single(t => t.Name == "Nested");
        var typeRemove = Operation(new { kind="type_remove", target=new {token=$"0x{nested.MDToken.Raw:x8}"}, remove_mode="reject_if_referenced" });
        var second = Commit(history, continued, At(lineage), fieldRemove, typeRemove);
        lineage = history.Load(second.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV3, "lineage stays v3 after Object reference");
        Check(continued.GetTypes().Any(t => t.Name.String?.StartsWith(EditDeletedRowsTombstone.ValueName, StringComparison.Ordinal) == true),
            "post-Object deletion stays in v3 ValueType representation");
        Check(continued.GlobalType.NestedTypes.Any(t => t.Name.String?.StartsWith(EditDeletedRowsTombstone.GlobalTypePrefix, StringComparison.Ordinal) == true),
            "post-Object type deletion stays in v3 global tombstone representation");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        var current = lineage.Manifest.HeadCheckpointId;
        history.PlanNavigation(lineage, current, first.PostHeadCheckpointId).Apply(continued);
        Check(EditWire.Sha256(Image(continued)) == first.Lineage.Head.ResultImageSha256,
            "post-Object deletion Undo restores exact first checkpoint");
        history.PlanNavigation(lineage, first.PostHeadCheckpointId, current).Apply(continued);
        Check(EditWire.Sha256(Image(continued)) == second.Lineage.Head.ResultImageSha256,
            "post-Object deletion Redo returns exact head image");
        history.PlanNavigation(lineage, current, first.PostHeadCheckpointId).Apply(continued);
        history.Finalize(history.PrepareHeadMove(lineage.Manifest.LineageId, current, first.PostHeadCheckpointId, "branch-undo"), continued);
        lineage = history.Load(lineage.Manifest.LineageId);
        var fieldB = continuedOwner.Fields.Single(f => f.Name == "B");
        var branchOp = Operation(new { kind="field_remove", target=new {token=$"0x{fieldB.MDToken.Raw:x8}"}, remove_mode="reject_if_referenced" });
        var branch = Commit(history, continued, At(lineage), branchOp);
        lineage = history.Load(branch.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Checkpoints.Count == 4 && lineage.Manifest.Checkpoints.Count(x => x.ParentCheckpointId == first.PostHeadCheckpointId) == 2,
            "v3 multi-checkpoint branch remains replayable");
        history.PlanNavigation(lineage, branch.PostHeadCheckpointId, first.PostHeadCheckpointId).Apply(continued);
        Check(EditWire.Sha256(Image(continued)) == first.Lineage.Head.ResultImageSha256,
            "branch Undo restores exact common checkpoint");
        var beforeRootNavigation = Image(continued);
        var beforeRootTypeRefs = continued.TablesStream.TypeRefTable.Rows;
        var rootRollback = history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(continued);
        Check(Image(continued).SequenceEqual(baseline), "v3 Object-introducing add Undo restores exact root on same live after reload");
        rootRollback();
        Check(EditWire.Sha256(Image(continued)) == first.Lineage.Head.ResultImageSha256,
            "v3 root-navigation compensation restores exact first image and reference view");
        history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(continued);
        history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(continued);
        Check(EditWire.Sha256(Image(continued)) == first.Lineage.Head.ResultImageSha256,
            "v3 Object-introducing add Redo returns exact first image");
        history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(continued);
        Check(Image(continued).SequenceEqual(baseline), "v3 Object-introducing add second Undo restores exact root");
        var forget = EditReferenceImageProjection.Set(continued, null);
        Check(EditWire.Sha256(Image(continued)) != EditWire.Sha256(baseline), "lost projection changes exact live image");
        history.PlanNavigation(lineage, root, first.PostHeadCheckpointId).Apply(continued);
        Check(EditWire.Sha256(Image(continued)) == first.Lineage.Head.ResultImageSha256,
            "verified source checkpoint restores lost projection before navigation");
        history.PlanNavigation(lineage, first.PostHeadCheckpointId, root).Apply(continued);
        Check(Image(continued).SequenceEqual(baseline), "recovered projection supports exact second root");

        history.Finalize(history.PrepareHeadMove(lineage.Manifest.LineageId, branch.PostHeadCheckpointId, root, "root-undo"), continued);
        Check(history.Load(lineage.Manifest.LineageId).Manifest.HeadCheckpointId == root
            && history.Assess(lineage.Manifest.LineageId, root, EditFingerprint.Compute(continued)).Classification == "exact",
            "v3 root head move persists and reloads exact");
        lineage = history.Load(lineage.Manifest.LineageId);
        var rootField = continued.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Fields.Single(f => f.Name == "A");
        var rootEdit = Operation(new { kind="field_update", target=new { token=$"0x{rootField.MDToken.Raw:x8}" }, name="AfterRootUndo" });
        var afterRoot = Commit(history, continued, At(lineage), rootEdit);
        Check(history.Assess(afterRoot.Lineage.Manifest.LineageId, afterRoot.PostHeadCheckpointId,
            EditFingerprint.Compute(continued)).Classification == "exact",
            "v3 projected root accepts subsequent legal commit and replays exact");
        using (var acceptedWorkspace = EditWorkspace.CreateForTesting(continued)) {
            var accepted = history.PrepareAcceptedBaseline(acceptedWorkspace, afterRoot.Lineage.Manifest.FamilyId,
                afterRoot.Lineage.Manifest.LineageId);
            history.Finalize(accepted, continued);
            Check(history.Load(accepted.Lineage.Manifest.LineageId).Manifest.Format == EditHistoryModule.PackageFormatV2,
                "accept_live from projected v3 live creates frozen v2 lineage");
        }
        Console.WriteLine($"REFERENCE root={EditWire.Sha256(baseline)} before={EditWire.Sha256(beforeRootNavigation)} typeref={beforeRootTypeRefs}->{continued.TablesStream.TypeRefTable.Rows} typeref_after_undo={continued.TablesStream.TypeRefTable.Rows}");

    }

    public static void V3BranchCost(string fixture, int depth, int width, string output) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var field = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members").Fields.Single(f => f.Name == "A");
        var token = $"0x{field.MDToken.Raw:x8}";
        var family = EditWire.NewId("family");
        string Op(string name) => Operation(new { kind="field_update", target=new {token}, name });
        string? shared = null;
        string? lineageId = null;
        for (var i = 1; i <= depth; i++) {
            var binding = i == 1 ? new EditHistoryBinding { FamilyId=family } : At(history.Load(lineageId!));
            var prepared = Commit(history, live, binding, Op("Cost" + i.ToString("D4")));
            lineageId = prepared.Lineage.Manifest.LineageId;
            if (i == depth / 2) shared = prepared.PostHeadCheckpointId;
        }
        var lineage = history.Load(lineageId!);
        var sharedId = shared ?? lineage.Manifest.HeadCheckpointId;
        for (var j = 1; j <= width; j++) {
            var from = lineage.Manifest.HeadCheckpointId;
            if (from != sharedId) {
                history.PlanNavigation(lineage, from, sharedId).Apply(live);
                history.Finalize(history.PrepareHeadMove(lineage.Manifest.LineageId, from, sharedId, "cost-branch"), live);
                lineage = history.Load(lineage.Manifest.LineageId);
            }
            var prepared = Commit(history, live, At(lineage), Op("Leaf" + j.ToString("D4")));
            lineage = history.Load(prepared.Lineage.Manifest.LineageId);
        }
        var bytes = store.FinalBytes(lineage.Manifest.LineageId);
        File.WriteAllBytes(output, bytes);
        var before = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var verified = history.ValidatePackageForTesting(bytes);
        watch.Stop();
        var after = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
        Check(verified.Manifest.Checkpoints.Count == depth + width + 1, "deep shared-prefix branch package validates every node");
        Console.WriteLine($"COST depth={depth} width={width} checkpoints={verified.Manifest.Checkpoints.Count} package_bytes={bytes.Length} parse_ms={watch.ElapsedMilliseconds} rss_before={before} peak_rss={after}");
        var rootId=verified.Manifest.Checkpoints.Single(x=>x.ParentCheckpointId==null).CheckpointId;
        var headId=verified.Manifest.HeadCheckpointId;
        var beforeNavigation=Image(live);
        var planClock=System.Diagnostics.Stopwatch.StartNew();
        var plan=history.PlanNavigation(verified,headId,rootId);
        planClock.Stop();
        var applyClock=System.Diagnostics.Stopwatch.StartNew();
        var compensation=plan.Apply(live);
        applyClock.Stop();
        Check(EditWire.Sha256(Image(live))==verified.Checkpoint(rootId).ResultImageSha256,
            "deep navigation exact root");
        compensation();
        Check(beforeNavigation.SequenceEqual(Image(live)),"deep navigation same-live compensation");
        Console.WriteLine($"COST_NAV depth={depth} width={width} plan_ms={planClock.ElapsedMilliseconds} apply_ms={applyClock.ElapsedMilliseconds} head_restored=True");
    }

    public static void ExistingV2BaselineAppend(string sourcePackage) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var oldBytes = File.ReadAllBytes(sourcePackage);
        var oldManifest = history.ValidatePackageForTesting(oldBytes).Manifest;
        var temp = store.CreateTemp(oldManifest.LineageId, oldBytes);
        store.FinalizeTemp(temp, replaceExisting:false);
        var old = history.Load(oldManifest.LineageId);
        using var live = ModuleDefMD.Load(old.BaselineBytes);
        Check(!live.GetTypeRefs().Any(t => t.FullName == "System.Object"), "existing v2 baseline has no Object TypeRef");
        using var acceptedWorkspace = EditWorkspace.CreateForTesting(live);
        var accepted = history.PrepareAcceptedBaseline(acceptedWorkspace, old.Manifest.FamilyId, old.Manifest.LineageId);
        history.Finalize(accepted, live);
        var lineage = history.Load(accepted.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV2 && lineage.Manifest.Checkpoints.Count == 1,
            "accept_live v2 baseline remains at a clean head");
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.I");
        var method = owner.Methods.Single(m => m.Name == "Tail");
        var remove = Operation(new {kind="method_remove",target=new {token=$"0x{method.MDToken.Raw:x8}"},remove_mode="reject_if_referenced"});
        var appended = Commit(history, live, At(lineage), remove);
        var loaded = history.Load(appended.Lineage.Manifest.LineageId);
        Check(loaded.Manifest.Format == EditHistoryModule.PackageFormatV2 && loaded.Manifest.Checkpoints.Count == 2,
            "bound no-Object v2 legal method removal commits without v3 representation");
        Check(live.GetTypes().Any(EditDeletedRowsTombstone.IsLegacyTombstone), "bound v2 deletion uses frozen Object marker");
        Check(history.Assess(loaded.Manifest.LineageId, loaded.Manifest.HeadCheckpointId, EditFingerprint.Compute(live)).Classification == "exact",
            "bound v2 appended head replays exact");
    }

    public static void V3PdbPayload(string fixture) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        using var live = ModuleDefMD.Load(File.ReadAllBytes(fixture));
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        var before = Image(live);
        var owner = live.GetTypes().Single(t => t.FullName == "NoObjectFixture.Members");
        var op = Operation(new {
            kind="method_add", owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"}, name="R06Pdb",
            attributes=(uint)(MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig),
            signature=new {return_type="System.Void",has_this=false,parameters=Array.Empty<object>(),generic_parameters=Array.Empty<object>()},
            body=new {init_locals=false,max_stack=1,locals=Array.Empty<object>(),exception_handlers=Array.Empty<object>(),
                instructions=new[] {new {opcode="ret"}}, sequence_points=new[] {new {
                    document=new {name="R06Pdb.cs", language="3f5162f8-07c6-11d3-9053-00c04fa302a1",
                        vendor="994b45c4-e6e9-11d2-903f-00c04fa302a1", hash="AQID",
                        type="5a869d0b-6611-11d3-bd2a-0000f80849bd", hashAlgorithm="ff1816ec-aa5e-4d10-87f7-6f4963833460"},
                    start=new {il=0,line=1,column=1}, end=new {il=0,line=1,column=2},
                }},
            },
        });
        var prepared = Commit(history, live, new EditHistoryBinding {FamilyId=EditWire.NewId("family")}, op);
        var lineage = history.Load(prepared.Lineage.Manifest.LineageId);
        Check(lineage.Manifest.Format == EditHistoryModule.PackageFormatV3 && lineage.Manifest.Payloads.Count == 1,
            "v3 PDB method body is a validated external payload");
        Check(live.PdbState?.Documents.Any(d => d.Url == "R06Pdb.cs") == true,
            "v3 PDB document materialized on live graph");
        var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
        history.PlanNavigation(lineage, prepared.PostHeadCheckpointId, root).Apply(live);
        Check(Image(live).SequenceEqual(before), "v3 PDB method Undo restores exact root image");
    }
}
