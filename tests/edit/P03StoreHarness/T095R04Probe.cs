using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class T095R04Probe {
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static byte[] Image(ModuleDef module) => EditWorkspace.WriteCheckpointImage(module);
    static void Check(bool okay, string name) { if (!okay) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Empty(string path) {
        using var module=ModuleDefMD.Load(File.ReadAllBytes(path));
        var before=Image(module);var semantic=EditFingerprint.ComputeRoundtripStrongV3(module);
        var userNames=module.GetTypes().Select(t=>t.FullName).ToArray();
        var owner=EditDeletedRowsTombstone.GetOrCreate(module);
        Check(owner.Fields.Count==0 && owner.Methods.Count==0 && semantic!=EditFingerprint.ComputeRoundtripStrongV3(module),"empty in-flight owner is visible to v3 digest");
        EditDeletedRowsTombstone.RemoveIfEmpty(owner);
        Check(before.SequenceEqual(Image(module)) && userNames.SequenceEqual(module.GetTypes().Select(t=>t.FullName)),"empty in-flight cleanup preserves image and user rows");
    }

    public static void Cache(string path) {
        using var live=ModuleDefMD.Load(File.ReadAllBytes(path));
        using var workspace=EditWorkspace.CreateForTesting(live);
        var v2=workspace.BaselineSemanticFingerprintFor(EditHistoryModule.PackageFormatV2);
        var v3=workspace.BaselineSemanticFingerprintFor(EditHistoryModule.PackageFormatV3);
        Check(v2!=v3,"v2/v3 baseline digests differ on legitimate same-shape user rows");
        using var baseline=ModuleDefMD.Load(workspace.BaselineBytes);
        Check(v2==EditHistoryModule.SemanticDigest(EditHistoryModule.PackageFormatV2,baseline) && v3==EditHistoryModule.SemanticDigest(EditHistoryModule.PackageFormatV3,baseline),"version caches remain isolated");
    }

    public static void Inspect(string path) {
        using var m=ModuleDefMD.Load(File.ReadAllBytes(path));
        Console.WriteLine($"IMAGE {Sha(File.ReadAllBytes(path))} fields={m.TablesStream.FieldTable.Rows} typedef={m.TablesStream.TypeDefTable.Rows} typeref={m.TablesStream.TypeRefTable.Rows}");
        foreach(var t in m.GetTypes()) Console.WriteLine($"TYPE {t.MDToken.Raw:x8} {t.FullName} fields={string.Join(",",t.Fields.Select(f=>$"{f.MDToken.Raw:x8}:{f.Name}"))}");
    }

    public static void MakeCollisions(string sourcePath, string outputPath) {
        using var module = ModuleDefMD.Load(Path.GetFullPath(sourcePath));
        var initial = EditFingerprint.ComputeRoundtripStrongV3(module);
        for (var i = 0; i < 2; i++) {
            var name = EditDeletedRowsTombstone.InterfaceName + (i == 0 ? "" : "-1");
            var user = new TypeDefUser(EditDeletedRowsTombstone.Namespace, name, null) { Attributes = EditDeletedRowsTombstone.InterfaceAttributes };
            var data = new MethodDefUser("UserData" + i, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            data.Body = new CilBody(); data.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 7 + i)); data.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            user.Methods.Add(data); module.Types.Add(user);
        }
        var global = new MethodDefUser(EditDeletedRowsTombstone.GlobalHostPrefix + "d000001", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static);
        global.Body = new CilBody(); global.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7)); global.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        module.GlobalType.Methods.Add(global);
        var target = module.GetTypes().SingleOrDefault(t => t.Name == "Nested");
        var globalStem = EditDeletedRowsTombstone.GlobalTypePrefix + (target?.MDToken.Rid ?? 2).ToString("x6");
        var nested = new TypeDefUser("", globalStem, null) { Attributes = TypeAttributes.NestedPrivate | TypeAttributes.Interface | TypeAttributes.Abstract };
        var nested2 = new TypeDefUser("", globalStem + "-1", null) { Attributes = TypeAttributes.NestedPrivate | TypeAttributes.Interface | TypeAttributes.Abstract };
        module.GlobalType.NestedTypes.Add(nested); module.GlobalType.NestedTypes.Add(nested2);
        var dummy = new TypeDefUser("dummy_ptr", "11111111-2222-3333-4444-555555555555", null) {
            Attributes = TypeAttributes.NotPublic | TypeAttributes.Interface | TypeAttributes.Abstract };
        var dummyData = new MethodDefUser("UserDummyData", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static);
        dummyData.Body = new CilBody(); dummyData.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_7)); dummyData.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        dummy.Methods.Add(dummyData); module.Types.Add(dummy);
        var bytes = Image(module); File.WriteAllBytes(outputPath, bytes);
        using var reload = ModuleDefMD.Load(bytes);
        Check(initial != EditFingerprint.ComputeRoundtripStrongV3(reload), "user collisions affect v3 digest");
        Check(EditDeletedRowsTombstone.NextMarkerName(reload) == EditDeletedRowsTombstone.InterfaceName + "-2", "collision allocation is deterministic and leaves user rows");
        var reloadTarget = reload.GetTypes().SingleOrDefault(t => t.Name == "Nested");
        if (reloadTarget != null) Check(EditDeletedRowsTombstone.NextGlobalTypeName(reload, reloadTarget) == globalStem + "-2", "global nested type collision allocation is deterministic");
        var beforeDummy=EditFingerprint.ComputeRoundtripStrongV3(reload);
        var dummyMethod=reload.GetTypes().SelectMany(t=>t.Methods).Single(m=>m.Name=="UserDummyData");
        dummyMethod.Body!.Instructions[0].OpCode=OpCodes.Ldc_I4_8;
        dummyMethod.Body.Instructions[0].Operand=null;
        Check(beforeDummy!=EditFingerprint.ComputeRoundtripStrongV3(reload),"same-shape dummy_ptr user data affects v3 digest");
        Console.WriteLine($"COLLISION image={Sha(bytes)} user_methods={reload.GetTypes().Count(t => t.Name.String?.StartsWith(EditDeletedRowsTombstone.InterfaceName, StringComparison.Ordinal) == true)} global={reload.GlobalType.Methods.Any(m => m.Name == global.Name)} nested={reload.GlobalType.NestedTypes.Any(t => t.Name == nested.Name)} dummy={reload.GetTypes().Any(t => t.Name == dummy.Name)}");
    }

    public static void Negative(string packagePath, string deletedPath, string foreignPath) {
        var package = File.ReadAllBytes(packagePath); var source = File.ReadAllBytes(deletedPath);
        using var catalog = new EditSchemaCatalog();
        using var store = new InMemoryEditCheckpointStore();
        using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
        EditLoadedLineage Load() => history.ValidatePackageForTesting(package);
        var clean = Load(); var head = clean.Manifest.HeadCheckpointId;
        var root = clean.Manifest.Checkpoints.Single(n => n.ParentCheckpointId == null).CheckpointId;
        var goodPlan = history.PlanNavigation(clean, head, root);
        using (var wrong = ModuleDefMD.Load(File.ReadAllBytes(foreignPath))) {
            var before = Image(wrong);
            try { goodPlan.Apply(wrong); throw new Exception("cross module plan accepted"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(wrong)), "cross module rejected before mutation"); }
        }
        using (var changedMarker = ModuleDefMD.Load(source)) {
            var inverse = clean.Operations[head].Operations[0].Inverse["state"];
            using var markerJson = JsonDocument.Parse(JsonSerializer.Serialize(inverse, EditWire.JsonOptions));
            var markerName = markerJson.RootElement.GetProperty("member_restore").GetProperty("v3_marker_name").GetString();
            var marker = changedMarker.Types.Single(t => t.Name == markerName);
            marker.Name = markerName + "-tampered";
            var before = Image(changedMarker);
            try { goodPlan.Apply(changedMarker); throw new Exception("changed marker accepted"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(changedMarker)), "changed marker rejects before mutation"); }
        }
        using (var changedOwner = ModuleDefMD.Load(source)) {
            var inverse = clean.Operations[head].Operations[0].Inverse["state"];
            using var markerJson = JsonDocument.Parse(JsonSerializer.Serialize(inverse, EditWire.JsonOptions));
            var markerName = markerJson.RootElement.GetProperty("member_restore").GetProperty("v3_marker_name").GetString();
            var marker = changedOwner.Types.Single(t => t.Name == markerName);
            var field = marker.Fields.Single(); marker.Fields.Remove(field);
            changedOwner.Types.Single(t => t.Name == EditDeletedRowsTombstone.InterfaceName).Fields.Add(field);
            var before = Image(changedOwner);
            try { goodPlan.Apply(changedOwner); throw new Exception("changed row owner accepted"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(changedOwner)), "changed row owner rejects before mutation"); }
        }
        using (var changedUser = ModuleDefMD.Load(source)) {
            var user = changedUser.GetTypes().SelectMany(t => t.Methods).FirstOrDefault(m => m.Name == "UserData0");
            if (user != null) {
                user.Body!.Instructions[0].OpCode = OpCodes.Ldc_I4_8;
                user.Body.Instructions[0].Operand = null;
                var before = Image(changedUser);
                try { goodPlan.Apply(changedUser); throw new Exception("changed user data accepted"); }
                catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(changedUser)), "changed user data rejects before mutation"); }
            }
        }
        using (var stale = ModuleDefMD.Load(clean.BaselineBytes)) {
            var before = Image(stale);
            try { goodPlan.Apply(stale); throw new Exception("stale checkpoint plan accepted"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(stale)), "stale checkpoint rejected before mutation"); }
        }
        using (var bare = ModuleDefMD.Load(source)) {
            var before = Image(bare);
            var state = clean.Operations[head].Operations[0].Inverse["state"];
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(state, EditWire.JsonOptions));
            try { EditOperationRegistry.ApplyCompiledInverse(bare, json.RootElement, new Dictionary<string, IMDTokenProvider>(), 0); throw new Exception("bare inverse accepted"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(bare)), "bare inverse has no source authority"); }
        }
        foreach (var variant in new[] { "missing_marker", "foreign_marker", "row", "owner" }) {
            var lineage = Load();
            var operation = lineage.Operations[head].Operations[0];
            var state = JsonNode.Parse(JsonSerializer.Serialize(operation.Inverse["state"], EditWire.JsonOptions))!.AsObject();
            var restore = state["member_restore"]!.AsObject();
            if (variant == "missing_marker") restore.Remove("v3_marker_name");
            if (variant == "foreign_marker") restore["v3_marker_name"] = "CopiedFromAnotherModule";
            if (variant == "row") restore["row"] = new JsonObject { ["token"] = "0x0400ffff" };
            if (variant == "owner") restore["owner"] = new JsonObject { ["token"] = "0x0200ffff" };
            using var stateJson = JsonDocument.Parse(state.ToJsonString());
            operation.Inverse["state"] = stateJson.RootElement.Clone();
            using var untouched = ModuleDefMD.Load(source); var before = Image(untouched);
            try { history.PlanNavigation(lineage, head, root); throw new Exception("tampered state accepted: " + variant); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_CHECKPOINT_INVALID" && before.SequenceEqual(Image(untouched)) && Sha(package) == Sha(File.ReadAllBytes(packagePath)), "tampered " + variant + " rejected before live mutation"); }
        }
        using (var live = ModuleDefMD.Load(source)) {
            var before = Image(live);
            var wrong = ModuleDefMD.Load(File.ReadAllBytes(foreignPath));
            using (EditDeletedRowsTombstone.UseVerifiedV3(live)) {
                try { using (EditDeletedRowsTombstone.UseVerifiedV3(wrong)) { EditDeletedRowsTombstone.RequireVerifiedV3(live); throw new Exception("nested authority leaked"); } }
                catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT", "nested different module isolates authority"); }
                EditDeletedRowsTombstone.RequireVerifiedV3(live);
                try { Task.Run(() => EditDeletedRowsTombstone.RequireVerifiedV3(live)).GetAwaiter().GetResult(); throw new Exception("async authority leaked"); }
                catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT", "async worker has no authority"); }
            }
            try { using (EditDeletedRowsTombstone.UseVerifiedV3(live)) throw new InvalidOperationException("injected"); }
            catch (InvalidOperationException) { }
            try { EditDeletedRowsTombstone.RequireVerifiedV3(live); throw new Exception("scope leaked after exit"); }
            catch (EditDomainException ex) { Check(ex.Code == "EDIT_HISTORY_CONFLICT" && before.SequenceEqual(Image(live)), "authority restored after exit"); }
            wrong.Dispose();
        }
        Console.WriteLine($"NEGATIVE package={Sha(package)} deleted={Sha(source)} unchanged={Sha(package)==Sha(File.ReadAllBytes(packagePath)) && Sha(source)==Sha(File.ReadAllBytes(deletedPath))}");
    }
}
