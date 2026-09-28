using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;
using dnSpy.Extension.MCP;

internal static class T095NoObjectProbe {
    static byte[] Image(ModuleDef m) => EditWorkspace.WriteCheckpointImage(m);
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static void State(string label, ModuleDefMD m, byte[] bytes) {
        var refs = m.GetTypeRefs().Select(t => $"{t.MDToken.Rid}:{t.FullName}");
        var tomb = m.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone).ToArray();
        var method = m.GetTypes().SelectMany(t => t.Methods).SingleOrDefault(x => x.Name == "Tail");
        var param = method?.ParamDefs.SingleOrDefault();
        var hosted=tomb.SelectMany(t=>t.Methods).SelectMany(h=>h.ParamDefs.Select(p=>$"0x{p.MDToken.Raw:x8}/owner=0x{h.MDToken.Raw:x8}"));
        Console.WriteLine($"STATE {label} image={Sha(bytes)} semantic={EditFingerprint.ComputeRoundtrip(m)} strong={EditFingerprint.Compute(m)} typerefs={string.Join('|',refs)} tomb={tomb.Length} param={(param == null ? "absent" : $"0x{param.MDToken.Raw:x8}/owner=0x{method!.MDToken.Raw:x8}")} hosted={string.Join('|',hosted)} tables=TR{m.TablesStream.TypeRefTable.Rows}/P{m.TablesStream.ParamTable.Rows}");
    }
    public static void Run(string path) {
        Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
        var source = File.ReadAllBytes(path);
        using var m = ModuleDefMD.Load(source);
        if (m.Assembly == null) throw new Exception("not an assembly fixture");
        Console.WriteLine($"FIXTURE has_object={m.GetTypeRefs().Any(t=>t.FullName=="System.Object")}");
        State("source",m,source);
        var before=Image(m);
        var outDir=Environment.GetEnvironmentVariable("T095_OUT");
        void Save(string name, byte[] data) { if(outDir!=null) {Directory.CreateDirectory(outDir);File.WriteAllBytes(Path.Combine(outDir,name),data);} }
        Save("source.dll",source);Save("baseline.dll",before);
        using(var baseline=ModuleDefMD.Load(before)) State("baseline",baseline,before);
        var method=m.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail");
        var op=JsonSerializer.Serialize(new {kind="parameter_remove",parameter_target=new {owner_method=new {token=$"0x{method.MDToken.Raw:x8}"},parameter_index=0},remove_mode="reject_if_referenced"});
        using var json=JsonDocument.Parse(op);
        var map=new Dictionary<string,IMDTokenProvider>();
        var inverse=EditOperationRegistry.CompileInverse(m,json.RootElement,map);
        Save("inverse.json",System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inverse)));
        var applied=EditOperationRegistry.Apply(m,json.RootElement,map,0);
        var deleted=Image(m);
        Save("deleted.dll",deleted);
        using(var d=ModuleDefMD.Load(deleted)) State("delete",d,deleted);
        using var reload=ModuleDefMD.Load(deleted);
        State("reload",reload,deleted);
        using var inverseJson=JsonDocument.Parse(JsonSerializer.Serialize(inverse));
        EditOperationRegistry.ApplyCompiledInverse(reload,inverseJson.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
        var restored=Image(reload);
        Save("restored.dll",restored);
        using(var r=ModuleDefMD.Load(restored)) State("restore",r,restored);
        Console.WriteLine($"RESULT exact={before.SequenceEqual(restored)} source_unchanged={Sha(source)==Sha(File.ReadAllBytes(path))}");
        applied.Undo();
        Console.WriteLine($"RESULT same_live_undo_exact={before.SequenceEqual(Image(m))}");
        using(var second=ModuleDefMD.Load(restored)) {
            var secondMethod=second.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail");
            using var secondOp=JsonDocument.Parse(JsonSerializer.Serialize(new {kind="parameter_remove",parameter_target=new {owner_method=new {token=$"0x{secondMethod.MDToken.Raw:x8}"},parameter_index=0},remove_mode="reject_if_referenced"}));
            var secondMap=new Dictionary<string,IMDTokenProvider>();
            var secondInv=EditOperationRegistry.CompileInverse(second,secondOp.RootElement,secondMap);
            EditOperationRegistry.Apply(second,secondOp.RootElement,secondMap,0);
            var secondDeleted=Image(second);
            Save("second-deleted.dll",secondDeleted);
            using(var d=ModuleDefMD.Load(secondDeleted)) State("second-delete",d,secondDeleted);
            using var secondReload=ModuleDefMD.Load(secondDeleted);
            using var secondInvJson=JsonDocument.Parse(JsonSerializer.Serialize(secondInv));
            EditOperationRegistry.ApplyCompiledInverse(secondReload,secondInvJson.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
            var secondRestored=Image(secondReload);
            Save("second-restored.dll",secondRestored);
            using(var r=ModuleDefMD.Load(secondRestored)) State("second-restore",r,secondRestored);
            Console.WriteLine($"RESULT second_exact={secondRestored.SequenceEqual(restored)}");
        }
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(),"t095-no-object-"+Guid.NewGuid().ToString("N")));
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        using var live=ModuleDefMD.Load(source);
        using var workspace=EditWorkspace.CreateForTesting(live);
        var liveBase=Image(live);
        using(var privateOp=JsonDocument.Parse(op)) EditOperationRegistry.Apply(workspace.PrivateModule,privateOp.RootElement,workspace.ObjectIds,0);
        workspace.NormalizedOperations.Add(op);
        try {
            var bound=history.ResolveBegin(workspace,null);
            var prepared=history.PrepareCommit(workspace,bound,workspace.NormalizedOperations,"t095",1,Array.Empty<string>());
            Console.WriteLine($"HISTORY prepared temp={store.TempCount} final={store.FinalCount}");
            using(var liveOp=JsonDocument.Parse(op)) EditOperationRegistry.ApplyPersisted(live,liveOp.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
            history.Finalize(prepared,live);
            Console.WriteLine($"HISTORY committed temp={store.TempCount} final={store.FinalCount} live_sha={Sha(Image(live))}");
            var lineageId=prepared.Lineage.Manifest.LineageId;
            var headId=prepared.PostHeadCheckpointId;
            using var reopened=new EditHistoryModule(store,catalog.CheckpointPackage);
            var lineage=reopened.Load(lineageId);
            var rootId=lineage.Manifest.Checkpoints.Single(x=>x.ParentCheckpointId==null).CheckpointId;
            Save("checkpoint-package.zip",store.FinalBytes(lineageId));
            Console.WriteLine($"HISTORY lineage checkpoints={lineage.Manifest.Checkpoints.Count} package_sha={Sha(store.FinalBytes(lineageId))}");
            try {
                var navigation=reopened.PlanNavigation(lineage,headId,rootId);
                Console.WriteLine("HISTORY plan_back=ok");
                navigation.Apply(live);
                Console.WriteLine($"HISTORY apply_back=ok exact={Image(live).SequenceEqual(liveBase)}");
            } catch(Exception ex) { Console.WriteLine($"HISTORY back_fail={ex} live_sha={Sha(Image(live))} package_sha={Sha(store.FinalBytes(lineageId))}"); }
        } catch(Exception ex) { Console.WriteLine($"HISTORY commit_fail={ex} live_sha={Sha(Image(live))}"); }
    }
    public static void RunInverse(string deletedPath, string inversePath) {
        var bytes=File.ReadAllBytes(deletedPath);
        using var module=ModuleDefMD.Load(bytes);
        var before=Image(module);
        State("reader-before",module,bytes);
        try {
            using var json=JsonDocument.Parse(File.ReadAllText(inversePath));
            EditOperationRegistry.ApplyCompiledInverse(module,json.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
            var after=Image(module);
            State("reader-after",module,after);
            Console.WriteLine($"READER result=RESTORED before={Sha(before)} after={Sha(after)}");
        } catch(EditDomainException ex) {
            var after=Image(module);
            Console.WriteLine($"READER result=REJECT code={ex.Code} no_mutation={before.SequenceEqual(after)} before={Sha(before)} after={Sha(after)}");
        }
    }
    public static void MakeSpoof(string sourcePath, string outputPath) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(sourcePath));
        var valueRef=module.GetTypeRefs().Single(t=>t.FullName=="System.ValueType");
        var spoof=new TypeDefUser(EditDeletedRowsTombstone.Namespace,EditDeletedRowsTombstone.ValueName,valueRef) {
            Attributes=EditDeletedRowsTombstone.ValueAttributes,
        };
        var userMethod=new MethodDefUser("UserData",MethodSig.CreateStatic(module.CorLibTypes.Int32),
            MethodAttributes.Public|MethodAttributes.Static|MethodAttributes.HideBySig);
        userMethod.Body=new CilBody();
        userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
        userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        spoof.Methods.Add(userMethod);
        module.Types.Add(spoof);
        var image=Image(module);
        File.WriteAllBytes(outputPath,image);
        using var loaded=ModuleDefMD.Load(image);
        Console.WriteLine($"SPOOF marker={EditDeletedRowsTombstone.IsMarker(loaded.GetTypes().Single(t=>t.Name==EditDeletedRowsTombstone.ValueName))} tombstone={EditDeletedRowsTombstone.IsTombstone(loaded.GetTypes().Single(t=>t.Name==EditDeletedRowsTombstone.ValueName))} image={Sha(image)}");
    }
    public static void MakeGlobalConflict(string sourcePath, string outputPath) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(sourcePath));
        var userMethod=new MethodDefUser(EditDeletedRowsTombstone.GlobalHostPrefix+"d000001",
            MethodSig.CreateStatic(module.CorLibTypes.Int32),
            MethodAttributes.Private|MethodAttributes.Static|MethodAttributes.HideBySig);
        userMethod.Body=new CilBody();
        userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));
        userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.GlobalType.Methods.Add(userMethod);
        var image=Image(module);
        File.WriteAllBytes(outputPath,image);
        Console.WriteLine($"GLOBAL_CONFLICT image={Sha(image)} global_methods={module.GlobalType.Methods.Count}");
    }
    public static void TryRemove(string path) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(path));
        var before=Image(module);
        var method=module.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail");
        var op=JsonSerializer.Serialize(new {kind="parameter_remove",parameter_target=new {owner_method=new {token=$"0x{method.MDToken.Raw:x8}"},parameter_index=0},remove_mode="reject_if_referenced"});
        using var json=JsonDocument.Parse(op);
        try {EditOperationRegistry.Apply(module,json.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
            Console.WriteLine($"TRY_REMOVE result=APPLIED before={Sha(before)} after={Sha(Image(module))}");}
        catch(EditDomainException ex) {Console.WriteLine($"TRY_REMOVE result=REJECT code={ex.Code} before={Sha(before)} after={Sha(Image(module))} exact={before.SequenceEqual(Image(module))} param_count={method.ParamDefs.Count} sig_count={method.MethodSig.Params.Count}");}
    }
    public static void OldPackage(string packagePath, string deletedPath) {
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore();
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        var package=File.ReadAllBytes(packagePath);
        var lineage=history.ValidatePackageForTesting(package);
        var head=lineage.Manifest.HeadCheckpointId;
        var root=lineage.Manifest.Checkpoints.Single(c=>c.ParentCheckpointId==null).CheckpointId;
        Console.WriteLine($"OLD_PACKAGE loaded format={lineage.Manifest.Format} checkpoints={lineage.Manifest.Checkpoints.Count} package={Sha(package)} head={head} root={root}");
        var forward=history.PlanNavigation(lineage,root,head);
        using(var rootLive=ModuleDefMD.Load(lineage.BaselineBytes)) {
            var rootBefore=Image(rootLive);
            var forwardUndo=forward.Apply(rootLive);
            var forwardAfter=Image(rootLive);
            Console.WriteLine($"OLD_PACKAGE_FORWARD exact={Sha(forwardAfter)==lineage.Checkpoint(head).ResultImageSha256} image={Sha(forwardAfter)} target={lineage.Checkpoint(head).ResultImageSha256}");
            forwardUndo();
            Console.WriteLine($"OLD_PACKAGE_FORWARD compensation={rootBefore.SequenceEqual(Image(rootLive))}");
        }
        var plan=history.PlanNavigation(lineage,head,root);
        using var live=ModuleDefMD.Load(Path.GetFullPath(deletedPath));
        var before=Image(live);
        Console.WriteLine($"OLD_PACKAGE live_before={Sha(before)} expected_semantic={lineage.Checkpoint(head).ResultSemanticFingerprint} actual_semantic={EditHistoryModule.SemanticDigest(lineage.Manifest.Format, live)} old_strong={EditFingerprint.ComputeRoundtripStrong(live)}");
        var undo=plan.Apply(live);
        var after=Image(live);
        using(var rootImage=ModuleDefMD.Load(lineage.BaselineBytes))
            Console.WriteLine($"OLD_PACKAGE_TABLES baseline_tr={rootImage.TablesStream.TypeRefTable.Rows} deleted_tr={live.TablesStream.TypeRefTable.Rows} restored_tr={ModuleDefMD.Load(after).TablesStream.TypeRefTable.Rows}");
        Console.WriteLine($"OLD_PACKAGE applied image={Sha(after)} target={lineage.Manifest.Checkpoints.Single(c=>c.CheckpointId==root).ResultImageSha256} exact={Sha(after)==lineage.Manifest.Checkpoints.Single(c=>c.CheckpointId==root).ResultImageSha256} semantic={EditFingerprint.ComputeRoundtripStrong(live)}");
        var canonical=EditWorkspace.WriteCanonical(live);
        using(var canonicalModule=ModuleDefMD.Load(canonical)) Console.WriteLine($"OLD_PACKAGE canonical_image={Sha(canonical)} canonical_typerefs={canonicalModule.TablesStream.TypeRefTable.Rows} target_exact={Sha(canonical)==lineage.Manifest.Checkpoints.Single(c=>c.CheckpointId==root).ResultImageSha256}");
        undo();
        Console.WriteLine($"OLD_PACKAGE compensated image={Sha(Image(live))} exact={before.SequenceEqual(Image(live))} package_unchanged={Sha(package)==Sha(File.ReadAllBytes(packagePath))}");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static EditHistoryBinding ResolveWindowsBinding(EditHistoryModule history, EditWorkspace workspace) =>
        history.ResolveBegin(workspace,null);
    public static void RunHistory(string path) {
        Environment.SetEnvironmentVariable("DNMCP_TEST","1");
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(),"t095-history-"+Guid.NewGuid().ToString("N")));
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        using var live=ModuleDefMD.Load(Path.GetFullPath(path));
        using var workspace=EditWorkspace.CreateForTesting(live);
        using var formatBaseline=ModuleDefMD.Load(workspace.BaselineBytes);
        var format=formatBaseline.GetTypeRefs().Any(t=>t.FullName=="System.Object")
            ? EditHistoryModule.PackageFormatV2 : EditHistoryModule.PackageFormatV3;
        var binding=OperatingSystem.IsWindows() ? ResolveWindowsBinding(history,workspace)
            : new EditHistoryBinding{FamilyId=EditWire.NewId("family"),Format=format};
        workspace.HistoryFormat=history.FormatForBinding(workspace,binding);
        if(workspace.HistoryFormat!=format) throw new Exception("history format differs from baseline mode");
        using var historyMode=workspace.UseHistoryMode();
        var sourceBytes=File.ReadAllBytes(Path.GetFullPath(path));
        var baseline=Image(live);
        var method=live.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail");
        var op=JsonSerializer.Serialize(new {kind="parameter_remove",parameter_target=new {owner_method=new {token=$"0x{method.MDToken.Raw:x8}"},parameter_index=0},remove_mode="reject_if_referenced"});
        using(var json=JsonDocument.Parse(op)) EditOperationRegistry.Apply(workspace.PrivateModule,json.RootElement,workspace.ObjectIds,0);
        workspace.NormalizedOperations.Add(op);
        EditPreparedHistoryWrite prepared;
        try { prepared=history.PrepareCommit(workspace,binding,workspace.NormalizedOperations,"t095-isolated",1,Array.Empty<string>()); }
        catch(EditDomainException ex) {
            Console.WriteLine($"HISTORY prepare_failure code={ex.Code} details={JsonSerializer.Serialize(ex.Details)} private={EditHistoryModule.SemanticDigest(format,workspace.PrivateModule)} baseline={Sha(baseline)} package_count={store.FinalCount}");
            throw;
        }
        Console.WriteLine($"HISTORY prepared format={prepared.Lineage.Manifest.Format} checkpoints={prepared.Lineage.Manifest.Checkpoints.Count} temp={store.TempCount}");
        using(var json=JsonDocument.Parse(op)) EditOperationRegistry.ApplyPersisted(live,json.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
        history.Finalize(prepared,live);
        var lineageId=prepared.Lineage.Manifest.LineageId;
        var head=prepared.PostHeadCheckpointId;
        var package=store.FinalBytes(lineageId);
        var outDir=Environment.GetEnvironmentVariable("T095_OUT");
        if(outDir!=null){Directory.CreateDirectory(outDir);File.WriteAllBytes(Path.Combine(outDir,"baseline.dll"),baseline);File.WriteAllBytes(Path.Combine(outDir,"package.zip"),package);File.WriteAllBytes(Path.Combine(outDir,"deleted.dll"),Image(live));}
        Console.WriteLine($"HISTORY finalized package={Sha(package)} live_deleted={Sha(Image(live))}");
        using var reopened=new EditHistoryModule(store,catalog.CheckpointPackage);
        var lineage=reopened.Load(lineageId);
        var root=lineage.Manifest.Checkpoints.Single(c=>c.ParentCheckpointId==null).CheckpointId;
        var plan=reopened.PlanNavigation(lineage,head,root);
        Console.WriteLine("HISTORY plan=ok");
        var liveBefore=Image(live);
        var undo=plan.Apply(live);
        Console.WriteLine($"HISTORY same_live_restored={Sha(Image(live))} exact={baseline.SequenceEqual(Image(live))}");
        undo();
        Console.WriteLine($"HISTORY compensation={Sha(Image(live))} exact={liveBefore.SequenceEqual(Image(live))}");
        using var diskLive=ModuleDefMD.Load(liveBefore);
        var diskBefore=Image(diskLive);
        var diskUndo=plan.Apply(diskLive);
        var diskAfter=Image(diskLive);
        if(outDir!=null)File.WriteAllBytes(Path.Combine(outDir,"restored.dll"),diskAfter);
        Console.WriteLine($"HISTORY disk_live_restored={Sha(diskAfter)} exact={baseline.SequenceEqual(diskAfter)} token=0x{diskLive.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail").ParamDefs.Single().MDToken.Raw:x8}");
        diskUndo();
        Console.WriteLine($"HISTORY disk_compensation={Sha(Image(diskLive))} exact={diskBefore.SequenceEqual(Image(diskLive))} package_unchanged={Sha(package)==Sha(store.FinalBytes(lineageId))}");
        if (!baseline.SequenceEqual(diskAfter) || !diskBefore.SequenceEqual(Image(diskLive)) ||
            !sourceBytes.SequenceEqual(File.ReadAllBytes(Path.GetFullPath(path))) ||
            !package.SequenceEqual(store.FinalBytes(lineageId)))
            throw new Exception("WithObject history exact/source/package assertion failed");
        var steps=(IReadOnlyList<EditHistoryNavigationPlan.Step>)typeof(EditHistoryNavigationPlan)
            .GetField("steps",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(plan)!;
        using var failLive=ModuleDefMD.Load(liveBefore);
        var failBefore=Image(failLive);
        var methodBefore=failLive.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail");
        var rowBefore=failLive.ResolveToken(0x08000001);
        var failingPlan=new EditHistoryNavigationPlan(lineage.Manifest.Format,new[] {steps.Single(),new EditHistoryNavigationPlan.Step {
            CheckpointId=head,Index=1,IsInverse=false,Operation="{}"}},
            EditHistoryModule.SemanticDigest(lineage.Manifest.Format,failLive),
            lineage.Checkpoint(root).ResultSemanticFingerprint,Sha(failBefore),false);
        try {failingPlan.Apply(failLive);Console.WriteLine("HISTORY injected_failure=NOT_REJECTED");}
        catch(Exception ex) {Console.WriteLine($"HISTORY injected_failure={ex.GetType().Name} full_image_exact={failBefore.SequenceEqual(Image(failLive))} method_same={ReferenceEquals(methodBefore,failLive.GetTypes().SelectMany(t=>t.Methods).Single(t=>t.Name=="Tail"))} row_same={ReferenceEquals(rowBefore,failLive.ResolveToken(0x08000001))} package_unchanged={Sha(package)==Sha(store.FinalBytes(lineageId))}");}
    }
    public static void ValidateOnly(string path) {
        var package=File.ReadAllBytes(path);
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore();
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        try {var lineage=history.ValidatePackageForTesting(package);
            Console.WriteLine($"VALIDATE result=ACCEPT format={lineage.Manifest.Format} sha={Sha(package)}");}
        catch(EditDomainException ex) {Console.WriteLine($"VALIDATE result=REJECT code={ex.Code} sha={Sha(package)} unchanged={Sha(package)==Sha(File.ReadAllBytes(path))}");}
    }
    public static void PathContract() {
        var v3=EditHistoryModule.PackageFormatV3;
        var v2=EditHistoryModule.PackageFormatV2;
        foreach(var path in new[]{"edit-output/lineage-a/module.bin","edit-output\\lineage-a\\module.bin"}) {
            if(!EditHistoryModule.ValidDefaultOutputRelativePath(v3,path)) throw new Exception("v3 separator rejected: "+path);
        }
        foreach(var path in new[]{"../edit-output/x","/edit-output/x","\\edit-output\\x",
            "edit-output/../escape","edit-output\\..\\escape","edit-output2/x","C:\\absolute"}) {
            if(EditHistoryModule.ValidDefaultOutputRelativePath(v3,path)) throw new Exception("unsafe v3 path accepted: "+path);
        }
        if(!EditHistoryModule.ValidDefaultOutputRelativePath(v2,"edit-output"+Path.DirectorySeparatorChar+"x"))
            throw new Exception("native legacy path rejected");
        Console.WriteLine("PASS R13 v3 dual-separator and unsafe-path checks; v2 native check retained");
    }
    public static void ValidateWindowsStore(string packagePath, string newRoot, bool useExisting=false) {
        var source=Path.GetFullPath(packagePath);
        var root=Path.GetFullPath(newRoot);
        if (useExisting ? !Directory.Exists(root) : Directory.Exists(root) || File.Exists(root))
            throw new Exception(useExisting ? "existing diagnostic ArtifactRoot absent" : "fresh Windows diagnostic ArtifactRoot required");
        var bytes=File.ReadAllBytes(source);
        using var catalog=new EditSchemaCatalog();
        if (!useExisting) using (var inMemory=new EditHistoryModule(new InMemoryEditCheckpointStore(),catalog.CheckpointPackage)) {
            try {
                var local=inMemory.ValidatePackageForTesting(bytes);
                Console.WriteLine($"R13_MEMORY result=ACCEPT format={local.Manifest.Format} sha={Sha(bytes)}");
            } catch(EditDomainException ex) {
                Console.WriteLine($"R13_MEMORY result=REJECT code={ex.Code} details={JsonSerializer.Serialize(ex.Details)} sha={Sha(bytes)}");
                return;
            }
        }
        var snapshot=McpSettingsSnapshot.TryCreate(false,"localhost",16961,true,true,
            Path.GetDirectoryName(source)!,root,Array.Empty<string>(),null,false,out var settingsError)
            ?? throw new Exception("settings: "+settingsError);
        if (!useExisting) Directory.CreateDirectory(root);
        using var store=new WindowsEditCheckpointStore(snapshot);
        var id="lineage-50c5f977de76bcffe9afe7d976a63a6d";
        var final=Path.Combine(root,"edit-checkpoints",id+".dnspy-mcp-checkpoints");
        if (!useExisting) File.WriteAllBytes(final,bytes);
        else if (!bytes.SequenceEqual(File.ReadAllBytes(final))) throw new Exception("existing diagnostic package differs");
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        try {
            var loaded=history.Load(id);
            Console.WriteLine($"R13_WINDOWS_STORE result=ACCEPT format={loaded.Manifest.Format} checkpoints={loaded.Manifest.Checkpoints.Count} sha={Sha(File.ReadAllBytes(final))}");
        } catch(EditDomainException ex) {
            Console.WriteLine($"R13_WINDOWS_STORE result=REJECT code={ex.Code} details={JsonSerializer.Serialize(ex.Details)} sha={Sha(File.ReadAllBytes(final))}");
        }
        Console.WriteLine($"R13_WINDOWS_STORE source_unchanged={bytes.SequenceEqual(File.ReadAllBytes(source))} final_unchanged={bytes.SequenceEqual(File.ReadAllBytes(final))} temp_count={(Directory.Exists(Path.Combine(root,"edit-verifier-temp")) ? Directory.GetFileSystemEntries(Path.Combine(root,"edit-verifier-temp")).Length : 0)}");
    }
    public static void RunMember(string path, string kind) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(path));
        var owner=module.GetTypes().Single(t=>t.FullName=="NoObjectFixture.Members" || t.FullName=="NoObjectFixture.InterfaceMembers");
        IMDTokenProvider row=kind switch {
            "field_remove" => owner.Fields.FirstOrDefault(f=>f.Name=="A") ?? module.GlobalType.Fields.Single(f=>f.Name=="UserGlobal"),
            "method_remove" => owner.Methods.Single(m=>m.Name=="RemoveMe"),
            "property_remove" => owner.Properties.Single(p=>p.Name=="P"),
            "event_remove" => owner.Events.Single(e=>e.Name=="E"),
            "type_remove" => owner.NestedTypes.Single(t=>t.Name=="Nested"),
            _ => throw new Exception("invalid kind"),
        };
        var baseline=Image(module);
        var source=File.ReadAllBytes(path);
        Console.WriteLine($"MEMBER_PRE kind={kind} no_object={!module.GetTypeRefs().Any(t=>t.FullName=="System.Object")} has_value={module.GetTypeRefs().Any(t=>t.FullName=="System.ValueType")} row=0x{row.MDToken.Raw:x8} baseline={Sha(baseline)} source={Sha(source)}");
        var op=JsonSerializer.Serialize(new {kind,target=new {token=$"0x{row.MDToken.Raw:x8}"},remove_mode="reject_if_referenced"});
        using var json=JsonDocument.Parse(op);
        var map=new Dictionary<string,IMDTokenProvider>();
        object inverse;
        try { inverse=EditOperationRegistry.CompileInverse(module,json.RootElement,map); }
        catch(Exception ex) {Console.WriteLine($"MEMBER_COMPILE kind={kind} exception={ex.GetType().Name} code={(ex as EditDomainException)?.Code} message={ex.Message}");return;}
        Exception? applyException=null;
        try {EditOperationRegistry.Apply(module,json.RootElement,map,0);}
        catch(Exception ex) {applyException=ex;}
        var markers=module.GetTypes().Where(EditDeletedRowsTombstone.IsMarker).ToArray();
        var graph=$"owner_fields={owner.Fields.Count} owner_methods={owner.Methods.Count} owner_properties={owner.Properties.Count} owner_events={owner.Events.Count} owner_nested={owner.NestedTypes.Count} marker_count={markers.Length} " +
            $"marker_fields={markers.Sum(t=>t.Fields.Count)} marker_methods={markers.Sum(t=>t.Methods.Count)} marker_properties={markers.Sum(t=>t.Properties.Count)} marker_events={markers.Sum(t=>t.Events.Count)} marker_nested={markers.Sum(t=>t.NestedTypes.Count)}";
        byte[]? afterApply=null; Exception? imageException=null;
        try {afterApply=Image(module);} catch(Exception ex) {imageException=ex;}
        Console.WriteLine($"MEMBER_APPLY kind={kind} exception={applyException?.GetType().Name ?? "none"} code={(applyException as EditDomainException)?.Code ?? "none"} message={applyException?.Message ?? "none"} graph={graph}");
        Console.WriteLine($"MEMBER_IMAGE kind={kind} exception={imageException?.GetType().Name ?? "none"} message={imageException?.Message ?? "none"} exact={(afterApply!=null ? baseline.SequenceEqual(afterApply).ToString() : "unknown")} baseline={Sha(baseline)} after={(afterApply!=null ? Sha(afterApply) : "unavailable")}");
        if(applyException!=null || afterApply==null) return;
        try {
            var asm=System.Reflection.Assembly.Load(afterApply);
            var types=asm.GetTypes();
            var reflected=types.Sum(t=>t.GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static).Length
                +t.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static).Length);
            Console.WriteLine($"MEMBER_CLR kind={kind} ok=True types={types.Length} reflected={reflected}");
        } catch(Exception ex) {Console.WriteLine($"MEMBER_CLR kind={kind} ok=False error={ex.GetType().Name}:{ex.Message}");}
        using var reload=ModuleDefMD.Load(afterApply);
        var marker=reload.GetTypes().SingleOrDefault(EditDeletedRowsTombstone.IsMarker);
        Console.WriteLine($"MEMBER_DELETE kind={kind} marker={marker?.FullName} is_tombstone={(marker!=null&&EditDeletedRowsTombstone.IsTombstone(marker))} typerefs={reload.TablesStream.TypeRefTable.Rows} deleted={Sha(afterApply)}");
        try {using var inverseJson=JsonDocument.Parse(JsonSerializer.Serialize(inverse));
            var restored=EditOperationRegistry.ApplyCompiledInverse(reload,inverseJson.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
            byte[]? image=null; Exception? writeError=null;
            try {image=Image(reload);} catch(Exception ex) {writeError=ex;}
            Console.WriteLine($"MEMBER_RESTORE kind={kind} exception={writeError?.GetType().Name ?? "none"} exact={(image!=null ? baseline.SequenceEqual(image).ToString() : "unknown")} after={(image!=null ? Sha(image) : "unavailable")} source_unchanged={Sha(source)==Sha(File.ReadAllBytes(path))}");
            restored.Undo();
            if(image!=null) Console.WriteLine($"MEMBER_COMPENSATE kind={kind} exact={afterApply.SequenceEqual(Image(reload))}");
        }
        catch(Exception ex) {Console.WriteLine($"MEMBER_RESTORE kind={kind} exception={ex.GetType().Name} code={(ex as EditDomainException)?.Code} message={ex.Message}");}
    }
    public static void ProbeGlobal(string path, string kind) {
        var source=File.ReadAllBytes(path);
        using var module=ModuleDefMD.Load(source);
        var owner=module.GetTypes().Single(t=>t.FullName=="NoObjectFixture.InterfaceMembers");
        var global=module.GlobalType ?? throw new Exception("no global type");
        Console.WriteLine($"GLOBAL_PRE kind={kind} object={module.GetTypeRefs().Any(t=>t.FullName=="System.Object")} value={module.GetTypeRefs().Any(t=>t.FullName=="System.ValueType")} global_methods={global.Methods.Count} global_fields={global.Fields.Count} global_props={global.Properties.Count} global_events={global.Events.Count} global_nested={global.NestedTypes.Count}");
        switch(kind) {
        case "method": {
            var row=owner.Methods.Single(m=>m.Name=="RemoveMe");owner.Methods.Remove(row);global.Methods.Add(row);break;
        }
        case "property": {
            var row=owner.Properties.Single(p=>p.Name=="P");var accessors=new[]{row.GetMethod,row.SetMethod}.Where(m=>m!=null).ToArray();
            owner.Properties.Remove(row);global.Properties.Add(row);
            foreach(var accessor in accessors){owner.Methods.Remove(accessor);global.Methods.Add(accessor);}break;
        }
        case "event": {
            var row=owner.Events.Single(e=>e.Name=="E");var accessors=new[]{row.AddMethod,row.RemoveMethod}.Where(m=>m!=null).ToArray();
            owner.Events.Remove(row);global.Events.Add(row);
            foreach(var accessor in accessors){owner.Methods.Remove(accessor);global.Methods.Add(accessor);}break;
        }
        case "type": {
            var row=owner.NestedTypes.Single(t=>t.Name=="Nested");owner.NestedTypes.Remove(row);global.NestedTypes.Add(row);break;
        }
        default: throw new Exception("unknown kind");
        }
        byte[]? image=null;
        try {image=Image(module);Console.WriteLine($"GLOBAL_WRITE kind={kind} ok=True sha={Sha(image)} typerefs={module.TablesStream.TypeRefTable.Rows}");}
        catch(Exception ex){Console.WriteLine($"GLOBAL_WRITE kind={kind} ok=False error={ex.GetType().Name}:{ex.Message}");return;}
        try {var assembly=System.Reflection.Assembly.Load(image);var types=assembly.GetTypes();Console.WriteLine($"GLOBAL_CLR kind={kind} ok=True types={string.Join('|',types.Select(t=>t.FullName))}");}
        catch(Exception ex){Console.WriteLine($"GLOBAL_CLR kind={kind} ok=False error={ex.GetType().Name}:{ex.Message}");}
        using var reload=ModuleDefMD.Load(image);
        Console.WriteLine($"GLOBAL_READ kind={kind} typerefs={reload.TablesStream.TypeRefTable.Rows} global_methods={reload.GlobalType.Methods.Count} global_props={reload.GlobalType.Properties.Count} global_events={reload.GlobalType.Events.Count} global_nested={reload.GlobalType.NestedTypes.Count}");
    }
    public static void MakeGlobalField(string sourcePath, string outputPath) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(sourcePath));
        var field=new FieldDefUser("UserGlobal",new FieldSig(module.CorLibTypes.Int32),
            FieldAttributes.Private|FieldAttributes.Static);
        module.GlobalType.Fields.Add(field);
        var image=Image(module);File.WriteAllBytes(outputPath,image);
        using var reload=ModuleDefMD.Load(image);
        Console.WriteLine($"GLOBAL_FIELD image={Sha(image)} typerefs={reload.TablesStream.TypeRefTable.Rows} field_count={reload.GlobalType.Fields.Count} has_object={reload.GetTypeRefs().Any(t=>t.FullName=="System.Object")} has_value={reload.GetTypeRefs().Any(t=>t.FullName=="System.ValueType")}");
        try {var asm=System.Reflection.Assembly.Load(image);var fields=asm.ManifestModule.GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);Console.WriteLine($"GLOBAL_FIELD_CLR count={fields.Length} field={fields.Single().Name}");}
        catch(Exception ex){Console.WriteLine($"GLOBAL_FIELD_CLR error={ex.GetType().Name}:{ex.Message}");}
    }
    public static void ProbeGlobalFieldRename(string path) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(path));
        var baseline=Image(module);
        var field=module.GlobalType.Fields.Single(f=>f.Name=="UserGlobal");
        var token=field.MDToken.Raw;
        field.Name="dnspy.mcp.edit.DeletedGlobalField.d"+field.MDToken.Rid.ToString("x6");
        var deleted=Image(module);
        using var reload=ModuleDefMD.Load(deleted);
        var moved=(FieldDef)reload.ResolveToken(token);
        var load="unknown";
        try {var asm=System.Reflection.Assembly.Load(deleted);var fields=asm.ManifestModule.GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);load=string.Join('|',fields.Select(f=>f.Name));}
        catch(Exception ex){load=ex.GetType().Name+":"+ex.Message;}
        Console.WriteLine($"GLOBAL_RENAME before={Sha(baseline)} deleted={Sha(deleted)} token=0x{token:x8} runtime_field={load}");
        moved.Name="UserGlobal";
        var restored=Image(reload);
        Console.WriteLine($"GLOBAL_RENAME_RESTORE exact={baseline.SequenceEqual(restored)} after={Sha(restored)} token_same={moved.MDToken.Raw==token}");
    }
    public static void ProbeNullBase(string path) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(path));
        var type=new TypeDefUser("dnspy.mcp.edit","DeletedRowsNullBase",null) {
            Attributes=TypeAttributes.NotPublic|TypeAttributes.Abstract|TypeAttributes.Sealed,
        };
        var method=new MethodDefUser("Host",MethodSig.CreateStatic(module.CorLibTypes.Int32),
            MethodAttributes.Private|MethodAttributes.Static|MethodAttributes.HideBySig);
        method.Body=new CilBody();method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        type.Fields.Add(new FieldDefUser("HostField",new FieldSig(module.CorLibTypes.Int32),FieldAttributes.Private|FieldAttributes.Static));
        module.Types.Add(type);
        var image=Image(module);
        using var reload=ModuleDefMD.Load(image);
        Console.WriteLine($"NULL_BASE image={Sha(image)} typerefs={reload.TablesStream.TypeRefTable.Rows} base={reload.GetTypes().Single(t=>t.Name=="DeletedRowsNullBase").BaseType?.FullName ?? "null"}");
        try {var asm=System.Reflection.Assembly.Load(image);var found=asm.GetTypes().Single(t=>t.Name=="DeletedRowsNullBase");var fields=found.GetFields(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);Console.WriteLine($"NULL_BASE_CLR ok=True fields={fields.Length} base={found.BaseType?.FullName ?? "null"}");}
        catch(Exception ex){Console.WriteLine($"NULL_BASE_CLR ok=False error={ex.GetType().Name}:{ex.Message}");}
    }
    public static void ProbeInterfaceMarker(string path) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(path));
        var type=new TypeDefUser("dnspy.mcp.edit","DeletedRowsInterface",null) {
            Attributes=TypeAttributes.NotPublic|TypeAttributes.Interface|TypeAttributes.Abstract,
        };
        var method=new MethodDefUser("Host",MethodSig.CreateStatic(module.CorLibTypes.Int32),
            MethodAttributes.Public|MethodAttributes.Static|MethodAttributes.HideBySig);
        method.Body=new CilBody();method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        type.Fields.Add(new FieldDefUser("HostField",new FieldSig(module.CorLibTypes.Int32),FieldAttributes.Public|FieldAttributes.Static));
        module.Types.Add(type);
        var image=Image(module);
        using var reload=ModuleDefMD.Load(image);
        Console.WriteLine($"INTERFACE_MARKER image={Sha(image)} typerefs={reload.TablesStream.TypeRefTable.Rows} base={reload.GetTypes().Single(t=>t.Name=="DeletedRowsInterface").BaseType?.FullName ?? "null"}");
        try {var asm=System.Reflection.Assembly.Load(image);var found=asm.GetTypes().Single(t=>t.Name=="DeletedRowsInterface");var fields=found.GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);var result=found.GetMethod("Host")?.Invoke(null,null);Console.WriteLine($"INTERFACE_MARKER_CLR ok=True fields={fields.Length} result={result}");}
        catch(Exception ex){Console.WriteLine($"INTERFACE_MARKER_CLR ok=False error={ex.GetType().Name}:{ex.Message}");}
    }
    public static void RunHistoryMember(string path,string kind) {
        Environment.SetEnvironmentVariable("DNMCP_TEST","1");
        var source=File.ReadAllBytes(path);
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(),"t095-r03-"+Guid.NewGuid().ToString("N")));
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        using var live=ModuleDefMD.Load(source);
        using var workspace=EditWorkspace.CreateForTesting(live);
        var baseline=Image(live);
        var owner=live.GetTypes().Single(t=>t.FullName=="NoObjectFixture.Members" || t.FullName=="NoObjectFixture.InterfaceMembers");
        IMDTokenProvider row=kind switch {
            "field_remove" => owner.Fields.FirstOrDefault(f=>f.Name=="A") ?? live.GlobalType.Fields.Single(f=>f.Name=="UserGlobal"),
            "method_remove" => owner.Methods.Single(m=>m.Name=="RemoveMe"),
            "property_remove" => owner.Properties.Single(p=>p.Name=="P"),
            "event_remove" => owner.Events.Single(e=>e.Name=="E"),
            "type_remove" => owner.NestedTypes.Single(t=>t.Name=="Nested"),
            _ => throw new Exception("invalid kind"),
        };
        var token=row.MDToken.Raw;
        var op=JsonSerializer.Serialize(new {kind,target=new {token=$"0x{token:x8}"},remove_mode="reject_if_referenced"});
        using(var json=JsonDocument.Parse(op)) EditOperationRegistry.Apply(workspace.PrivateModule,json.RootElement,workspace.ObjectIds,0);
        workspace.NormalizedOperations.Add(op);
        var binding=new EditHistoryBinding{FamilyId=EditWire.NewId("family")};
        var prepared=history.PrepareCommit(workspace,binding,workspace.NormalizedOperations,"t095-r03",1,Array.Empty<string>());
        Console.WriteLine($"HM prepared kind={kind} format={prepared.Lineage.Manifest.Format} checkpoints={prepared.Lineage.Manifest.Checkpoints.Count}");
        using(var json=JsonDocument.Parse(op)) EditOperationRegistry.ApplyPersisted(live,json.RootElement,new Dictionary<string,IMDTokenProvider>(),0);
        history.Finalize(prepared,live);
        var deleted=Image(live);
        var lineageId=prepared.Lineage.Manifest.LineageId;
        var package=store.FinalBytes(lineageId);
        var outDir=Environment.GetEnvironmentVariable("T095_OUT");
        if(outDir!=null){Directory.CreateDirectory(outDir);File.WriteAllBytes(Path.Combine(outDir,"baseline.dll"),baseline);File.WriteAllBytes(Path.Combine(outDir,"package.zip"),package);File.WriteAllBytes(Path.Combine(outDir,"deleted.dll"),deleted);}
        using var reopened=new EditHistoryModule(store,catalog.CheckpointPackage);
        var lineage=reopened.Load(lineageId);
        var root=lineage.Manifest.Checkpoints.Single(c=>c.ParentCheckpointId==null).CheckpointId;
        var plan=reopened.PlanNavigation(lineage,prepared.PostHeadCheckpointId,root);
        Console.WriteLine($"HM plan kind={kind} ok=True package={Sha(package)} deleted={Sha(deleted)} baseline={Sha(baseline)}");
        var sameBefore=Image(live);
        var undo=plan.Apply(live);
        Console.WriteLine($"HM same_live kind={kind} exact={baseline.SequenceEqual(Image(live))} image={Sha(Image(live))}");
        undo();
        Console.WriteLine($"HM same_compensation kind={kind} exact={sameBefore.SequenceEqual(Image(live))}");
        using var disk=ModuleDefMD.Load(deleted);
        var diskBefore=Image(disk);
        var diskUndo=plan.Apply(disk);
        var restored=Image(disk);
        Console.WriteLine($"HM disk kind={kind} exact={baseline.SequenceEqual(restored)} image={Sha(restored)} token=0x{token:x8} owner={disk.ResolveToken(token)?.ToString()}");
        diskUndo();
        Console.WriteLine($"HM disk_compensation kind={kind} exact={diskBefore.SequenceEqual(Image(disk))} package_unchanged={Sha(package)==Sha(store.FinalBytes(lineageId))} source_unchanged={Sha(source)==Sha(File.ReadAllBytes(path))}");
    }
    public static void RunHistoryMany(string path) {
        Environment.SetEnvironmentVariable("DNMCP_TEST","1");
        var source=File.ReadAllBytes(path);
        using var catalog=new EditSchemaCatalog();
        using var store=new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(),"t095-r03-many-"+Guid.NewGuid().ToString("N")));
        using var history=new EditHistoryModule(store,catalog.CheckpointPackage);
        using var live=ModuleDefMD.Load(source);
        using var workspace=EditWorkspace.CreateForTesting(live);
        var baseline=Image(live);
        var owner=live.GetTypes().Single(t=>t.FullName=="NoObjectFixture.Members" || t.FullName=="NoObjectFixture.InterfaceMembers");
        var rows=new (string kind, IMDTokenProvider row)[] {
            ("field_remove",owner.Fields.FirstOrDefault(f=>f.Name=="A") ?? live.GlobalType.Fields.Single(f=>f.Name=="UserGlobal")),
            ("method_remove",owner.Methods.Single(m=>m.Name=="RemoveMe")),
            ("property_remove",owner.Properties.Single(p=>p.Name=="P")),
            ("event_remove",owner.Events.Single(e=>e.Name=="E")),
            ("type_remove",owner.NestedTypes.Single(t=>t.Name=="Nested")),
        };
        var ops=rows.Select(x=>JsonSerializer.Serialize(new {kind=x.kind,target=new {token=$"0x{x.row.MDToken.Raw:x8}"},remove_mode="reject_if_referenced"})).ToArray();
        if(Environment.GetEnvironmentVariable("T095_ADD_FIELD")=="1") {
            var add=JsonSerializer.Serialize(new {kind="field_add",owner_type=new {token=$"0x{owner.MDToken.Raw:x8}"},name="A",field_type="System.Int32",attributes=(uint)(FieldAttributes.Public|FieldAttributes.Static)});
            ops=ops.Take(1).Concat(new[]{add}).Concat(ops.Skip(1)).ToArray();
        }
        foreach(var op in ops){using var json=JsonDocument.Parse(op);EditOperationRegistry.Apply(workspace.PrivateModule,json.RootElement,workspace.ObjectIds,0);workspace.NormalizedOperations.Add(op);}
        var binding=new EditHistoryBinding{FamilyId=EditWire.NewId("family")};
        var prepared=history.PrepareCommit(workspace,binding,workspace.NormalizedOperations,"t095-r03-many",1,Array.Empty<string>());
        foreach(var op in ops){using var json=JsonDocument.Parse(op);EditOperationRegistry.ApplyPersisted(live,json.RootElement,new Dictionary<string,IMDTokenProvider>(),0);}
        history.Finalize(prepared,live);
        var deleted=Image(live);
        var lineageId=prepared.Lineage.Manifest.LineageId;
        var package=store.FinalBytes(lineageId);
        var outDir=Environment.GetEnvironmentVariable("T095_OUT");
        if(outDir!=null){Directory.CreateDirectory(outDir);File.WriteAllBytes(Path.Combine(outDir,"package.zip"),package);File.WriteAllBytes(Path.Combine(outDir,"deleted.dll"),deleted);}
        using var reopened=new EditHistoryModule(store,catalog.CheckpointPackage);
        var lineage=reopened.Load(lineageId);
        var root=lineage.Manifest.Checkpoints.Single(c=>c.ParentCheckpointId==null).CheckpointId;
        var plan=reopened.PlanNavigation(lineage,prepared.PostHeadCheckpointId,root);
        Console.WriteLine($"MANY plan=ok format={lineage.Manifest.Format} count={ops.Length} baseline={Sha(baseline)} deleted={Sha(deleted)} package={Sha(package)}");
        var sameBefore=Image(live);var sameUndo=plan.Apply(live);
        Console.WriteLine($"MANY same_live exact={baseline.SequenceEqual(Image(live))}");sameUndo();
        Console.WriteLine($"MANY same_compensation exact={sameBefore.SequenceEqual(Image(live))}");
        using var disk=ModuleDefMD.Load(deleted);
        var diskBefore=Image(disk);
        var beforeRows=rows.Select(x=>disk.ResolveToken(x.row.MDToken.Raw)).ToArray();
        Action diskUndo; try { diskUndo=plan.Apply(disk); } catch(EditDomainException ex) { Console.WriteLine("MANY disk_failure code="+ex.Code+" details="+JsonSerializer.Serialize(ex.Details)); throw; } var restored=Image(disk);
        if(outDir!=null) File.WriteAllBytes(Path.Combine(outDir,"restored.dll"),restored);
        Console.WriteLine($"MANY disk exact={baseline.SequenceEqual(restored)} row_same={rows.Select((x,i)=>ReferenceEquals(beforeRows[i],disk.ResolveToken(x.row.MDToken.Raw))).All(x=>x)} image={Sha(restored)}");
        diskUndo();
        Console.WriteLine($"MANY disk_compensation exact={diskBefore.SequenceEqual(Image(disk))} package_unchanged={Sha(package)==Sha(store.FinalBytes(lineageId))} source_unchanged={Sha(source)==Sha(File.ReadAllBytes(path))}");
        var steps=(IReadOnlyList<EditHistoryNavigationPlan.Step>)typeof(EditHistoryNavigationPlan).GetField("steps",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(plan)!;
        using var fail=ModuleDefMD.Load(deleted);
        var failImage=Image(fail);var failRows=rows.Select(x=>fail.ResolveToken(x.row.MDToken.Raw)).ToArray();
        var failing=new EditHistoryNavigationPlan(lineage.Manifest.Format,steps.Concat(new[] {new EditHistoryNavigationPlan.Step{CheckpointId=prepared.PostHeadCheckpointId,Index=ops.Length,IsInverse=false,Operation="{}"}}).ToArray(),
            EditHistoryModule.SemanticDigest(lineage.Manifest.Format,fail),lineage.Checkpoint(root).ResultSemanticFingerprint,Sha(failImage),false);
        try {failing.Apply(fail);Console.WriteLine("MANY injected=NOT_REJECTED");}
        catch(Exception ex){Console.WriteLine($"MANY injected={ex.GetType().Name} full_image_exact={failImage.SequenceEqual(Image(fail))} row_same={rows.Select((x,i)=>ReferenceEquals(failRows[i],fail.ResolveToken(x.row.MDToken.Raw))).All(x=>x)} package_unchanged={Sha(package)==Sha(store.FinalBytes(lineageId))}");}
    }
    public static void MakeSpoofInterface(string sourcePath,string outputPath) {
        using var module=ModuleDefMD.Load(Path.GetFullPath(sourcePath));
        var beforeV2=EditFingerprint.ComputeRoundtripStrong(module);
        var beforeV3=EditFingerprint.ComputeRoundtripStrongV3(module);
        var spoof=new TypeDefUser(EditDeletedRowsTombstone.Namespace,EditDeletedRowsTombstone.InterfaceName,null){Attributes=EditDeletedRowsTombstone.InterfaceAttributes};
        var userMethod=new MethodDefUser("UserData",MethodSig.CreateStatic(module.CorLibTypes.Int32),MethodAttributes.Public|MethodAttributes.Static|MethodAttributes.HideBySig);
        userMethod.Body=new CilBody();userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7));userMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        spoof.Methods.Add(userMethod);module.Types.Add(spoof);
        var image=Image(module);File.WriteAllBytes(outputPath,image);
        using var reload=ModuleDefMD.Load(image);
        Console.WriteLine($"SPOOF_INTERFACE image={Sha(image)} marker={EditDeletedRowsTombstone.IsTombstone(reload.GetTypes().Single(t=>t.Name==EditDeletedRowsTombstone.InterfaceName))} v2_same={beforeV2==EditFingerprint.ComputeRoundtripStrong(reload)} v3_same={beforeV3==EditFingerprint.ComputeRoundtripStrongV3(reload)}");
        try {var asm=System.Reflection.Assembly.Load(image);var type=asm.GetTypes().Single(t=>t.Name==EditDeletedRowsTombstone.InterfaceName);Console.WriteLine($"SPOOF_INTERFACE_CLR result={type.GetMethod("UserData")?.Invoke(null,null)}");}
        catch(Exception ex){Console.WriteLine($"SPOOF_INTERFACE_CLR error={ex.GetType().Name}:{ex.Message}");}
    }

}
