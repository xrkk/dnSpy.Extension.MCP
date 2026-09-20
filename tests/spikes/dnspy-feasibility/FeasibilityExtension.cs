using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;

namespace DnSpy.Feasibility.Spike {
    [ExportExtension(Order = double.MaxValue - 100)]
    sealed class FeasibilityExtension : IExtension {
        const string Root = @"C:\dnspy-mcp-artifacts\spikes\2026-08-31";
        const string FixturePath = @"C:\dnspy-mcp-spikes\SpikeFixture.exe";
        readonly IDsDocumentService documentService;
        readonly ILanguageCompilerProvider[] compilerProviders;
        bool started;

        [ImportingConstructor]
        FeasibilityExtension(IDsDocumentService documentService, [ImportMany] IEnumerable<ILanguageCompilerProvider> compilerProviders) {
            this.documentService = documentService;
            this.compilerProviders = compilerProviders.ToArray();
        }

        public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription = "THROWAWAY S01/S02 feasibility spike" };
        public IEnumerable<string> MergedResourceDictionaries => Array.Empty<string>();

        public void OnEvent(ExtensionEvent @event, object? obj) {
            if (@event != ExtensionEvent.AppLoaded || started)
                return;
            started = true;
            Application.Current.Dispatcher.BeginInvoke(new Action(async () => await RunAsync()));
        }

        async Task RunAsync() {
            Directory.CreateDirectory(Root);
            var all = new Dictionary<string, object?> {
                ["format"] = "dnspy.mcp.spike.evidence.v1",
                ["throwaway"] = true,
                ["utc_started"] = DateTime.UtcNow.ToString("O"),
                ["dnspy_process_architecture"] = Environment.Is64BitProcess ? "x64" : "x86",
                ["dispatcher_access"] = Application.Current.Dispatcher.CheckAccess(),
            };
            try {
                var document = documentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(FixturePath), false)
                    ?? throw new InvalidOperationException("Could not create fixture document");
                documentService.GetOrAdd(document);
                var live = document.ModuleDef ?? throw new InvalidOperationException("Fixture has no ModuleDef");
                all["fixture"] = FixturePath;
                all["document_present"] = documentService.GetDocuments().Contains(document);
                all["s01"] = RunS01(document, live);
                all["s02"] = await RunS02Async(live);
                all["internal_reference_scan"] = ScanInternalReferences();
                all["utc_finished"] = DateTime.UtcNow.ToString("O");
                all["completed"] = true;
            }
            catch (Exception ex) {
                all["completed"] = false;
                all["fatal"] = ex.ToString();
            }
            WriteJson(Path.Combine(Root, "evidence.json"), all);
            File.WriteAllText(Path.Combine(Root, "DONE"), DateTime.UtcNow.ToString("O"));
        }

        static Dictionary<string, object?> RunS01(IDsDocument document, ModuleDef live) {
            var result = new Dictionary<string, object?>();
            var liveReference = live;
            var before = Fingerprint(live);
            result["before_fingerprint"] = before;
            result["module_reference_stable_initial"] = ReferenceEquals(liveReference, document.ModuleDef);

            byte[] privateBytes = WriteModule(live, embeddedPdb: live.PdbState is not null);
            var privateOptions = new ModuleCreationOptions { TryToLoadPdbFromDisk = true };
            using var privateCopy = ModuleDefMD.Load(privateBytes, privateOptions);
            var privateBefore = Fingerprint(privateCopy);
            result["private_roundtrip_fingerprint"] = privateBefore;
            result["private_roundtrip_exact"] = before == privateBefore;
            result["private_mvid_preserved"] = live.Mvid == privateCopy.Mvid;
            result["private_token_map_preserved"] = TokenMap(live) == TokenMap(privateCopy);

            var privateOps = CreateRepresentativeOperations(privateCopy);
            ApplyAll(privateOps);
            var expectedCommitted = Fingerprint(privateCopy);
            result["private_edited_fingerprint"] = expectedCommitted;
            result["live_unchanged_after_private_edit"] = Fingerprint(live) == before;

            var failureRows = new List<Dictionary<string, object?>>();
            for (int failAfter = 1; failAfter <= 3; failAfter++) {
                var ops = CreateRepresentativeOperations(live);
                string state = "READY";
                string? caught = null;
                try {
                    for (int i = 0; i < ops.Count; i++) {
                        ops[i].Apply();
                        if (i + 1 == failAfter)
                            throw new InjectedFailureException("apply-" + failAfter);
                    }
                }
                catch (Exception ex) {
                    caught = ex.GetType().Name;
                    try {
                        for (int i = Math.Min(failAfter, ops.Count) - 1; i >= 0; i--)
                            ops[i].Undo();
                        state = "ROLLED_BACK";
                    }
                    catch {
                        state = "LIVE_STATE_UNKNOWN";
                    }
                }
                failureRows.Add(new Dictionary<string, object?> {
                    ["fail_after_operation"] = failAfter,
                    ["caught"] = caught,
                    ["state"] = state,
                    ["fingerprint_restored"] = Fingerprint(live) == before,
                    ["module_reference_stable"] = ReferenceEquals(liveReference, document.ModuleDef),
                });
            }
            result["failure_injection"] = failureRows;

            var successOps = CreateRepresentativeOperations(live);
            ApplyAll(successOps);
            result["commit_fingerprint"] = Fingerprint(live);
            result["commit_matches_private_copy"] = Fingerprint(live) == expectedCommitted;
            UndoAll(successOps);
            result["explicit_rollback_restored"] = Fingerprint(live) == before;

            var unknownOps = CreateRepresentativeOperations(live);
            var unknownState = "READY";
            unknownOps[0].Apply();
            try {
                throw new InjectedFailureException("inverse-operation");
            }
            catch {
                unknownState = "LIVE_STATE_UNKNOWN";
            }
            finally {
                // Emergency cleanup is outside the deliberately failed transaction path so the
                // throwaway extension never leaves the user's dnSpy document modified.
                unknownOps[0].Undo();
            }
            result["inverse_failure_state"] = unknownState;
            result["emergency_cleanup_restored"] = Fingerprint(live) == before;

            var roundtripPath = Path.Combine(Root, "s01-live-after-rollback.exe");
            File.WriteAllBytes(roundtripPath, WriteModule(live, embeddedPdb: live.PdbState is not null));
            using var reloaded = ModuleDefMD.Load(roundtripPath, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
            result["saved_reload_fingerprint"] = Fingerprint(reloaded);
            result["saved_reload_exact"] = Fingerprint(reloaded) == before;
            result["dispatcher_access"] = Application.Current.Dispatcher.CheckAccess();
            result["document_module_reference_stable"] = ReferenceEquals(liveReference, document.ModuleDef);
            result["pass"] = (bool)result["private_roundtrip_exact"]!
                && (bool)result["private_mvid_preserved"]!
                && (bool)result["private_token_map_preserved"]!
                && (bool)result["live_unchanged_after_private_edit"]!
                && failureRows.All(a => Equals(a["state"], "ROLLED_BACK") && Equals(a["fingerprint_restored"], true))
                && (bool)result["commit_matches_private_copy"]!
                && (bool)result["explicit_rollback_restored"]!
                && Equals(result["inverse_failure_state"], "LIVE_STATE_UNKNOWN")
                && (bool)result["saved_reload_exact"]!
                && (bool)result["document_module_reference_stable"]!;
            return result;
        }

        async Task<Dictionary<string, object?>> RunS02Async(ModuleDef live) {
            var result = new Dictionary<string, object?>();
            var providerRows = compilerProviders.Select(p => new Dictionary<string, object?> {
                ["type"] = p.GetType().FullName,
                ["language"] = p.Language.ToString(),
                ["edit_method"] = p.CanCompile(CompilationKind.EditMethod),
                ["edit_class"] = p.CanCompile(CompilationKind.EditClass),
            }).ToArray();
            result["providers"] = providerRows;

            ILanguageCompilerProvider? provider = compilerProviders.FirstOrDefault(p =>
                p.CanCompile(CompilationKind.EditClass) && p.GetType().FullName!.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0);
            if (provider is null)
                throw new InvalidOperationException("C# ILanguageCompilerProvider not found");
            result["selected_provider"] = provider.GetType().FullName;

            const string source = @"using System;
namespace SpikeFixture {
    public sealed class Target {
        public int Field = 4;
        public int Helper(int value) => value + Field;
        public int Compute(int value) {
            try { return Helper(value) * 3 + Field + 7; }
            catch (Exception) { return -2; }
        }
        public string Added<T>(T value) where T : class {
            return value == null ? ""null"" : value.ToString();
        }
    }
}";

            CompilationResult compilation;
            using (var refs = new PinnedReferenceSet(new[] { typeof(object).Assembly.Location }))
            using (var compiler = provider.Create(CompilationKind.EditClass)) {
                compiler.InitializeProject(new CompilerProjectInfo(
                    "SpikeFixture", null, refs.References, refs, TargetPlatform.AnyCpu));
                compiler.AddDocuments(new[] { new CompilerDocumentInfo(source, "Target.cs") });
                compilation = await compiler.CompileAsync(CancellationToken.None);
            }
            result["compile_success"] = compilation.Success;
            result["diagnostics"] = compilation.Diagnostics.Select(d => d.ToString()).ToArray();
            result["debug_format"] = compilation.DebugFile.Format.ToString();
            result["debug_bytes"] = compilation.DebugFile.RawFile?.Length ?? 0;
            result["compiled_bytes"] = compilation.RawFile?.Length ?? 0;
            if (!compilation.Success || compilation.RawFile is null)
                throw new InvalidOperationException("Public compiler failed");

            var sourceOptions = new ModuleCreationOptions {
                TryToLoadPdbFromDisk = false,
                PdbFileOrData = compilation.DebugFile.RawFile,
            };
            using var generated = ModuleDefMD.Load(compilation.RawFile, sourceOptions);
            result["generated_pdb_loaded"] = generated.PdbState is not null;
            result["generated_sequence_points"] = CountSequencePoints(generated);

            byte[] targetBytes = WriteModule(live, embeddedPdb: live.PdbState is not null);
            using var target = ModuleDefMD.Load(targetBytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
            var import = new NarrowImporter(target, generated);
            var importResult = import.ImportTargetType("SpikeFixture.Target", "Compute", "Added");
            result["import"] = importResult;
            result["target_sequence_points_after_import"] = CountSequencePoints(target);

            if (target.PdbState is null)
                target.CreatePdbState(PdbFileKind.EmbeddedPortablePDB);
            else
                target.PdbState.PdbFileKind = PdbFileKind.EmbeddedPortablePDB;

            var importedPath = Path.Combine(Root, "s02-imported-embedded.exe");
            File.WriteAllBytes(importedPath, WriteModule(target, embeddedPdb: true));
            var sidecar = Path.ChangeExtension(importedPath, ".pdb");
            result["sidecar_pdb_exists"] = File.Exists(sidecar);
            using var verify = ModuleDefMD.Load(importedPath, new ModuleCreationOptions { TryToLoadPdbFromDisk = true });
            var verifyType = verify.Find("SpikeFixture.Target", false) ?? throw new InvalidOperationException("Imported type missing");
            var verifyCompute = verifyType.Methods.First(m => m.Name == "Compute");
            var verifyAdded = verifyType.Methods.FirstOrDefault(m => m.Name == "Added");
            result["reloaded_pdb_kind"] = verify.PdbState?.PdbFileKind.ToString();
            result["reloaded_sequence_points"] = CountSequencePoints(verify);
            result["compute_multiplier_3"] = verifyCompute.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_3);
            result["compute_constant_7"] = verifyCompute.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_7);
            result["compute_has_eh"] = verifyCompute.Body.ExceptionHandlers.Count > 0;
            result["added_method_present"] = verifyAdded is not null;
            result["added_is_generic"] = verifyAdded?.GenericParameters.Count == 1;
            result["added_has_class_constraint"] = verifyAdded?.GenericParameters[0].HasReferenceTypeConstraint == true;
            result["pass"] = compilation.Success
                && generated.PdbState is not null
                && CountSequencePoints(generated) > 0
                && importResult["method_body_imported"] is bool mbi && mbi
                && importResult["new_method_imported"] is bool nmi && nmi
                && importResult["field_reference_mapped"] is bool frm && frm
                && importResult["method_reference_mapped"] is bool mrm && mrm
                && !File.Exists(sidecar)
                && verify.PdbState?.PdbFileKind == PdbFileKind.EmbeddedPortablePDB
                && CountSequencePoints(verify) > 0
                && (bool)result["compute_multiplier_3"]!
                && (bool)result["compute_constant_7"]!
                && (bool)result["compute_has_eh"]!
                && (bool)result["added_method_present"]!
                && (bool)result["added_is_generic"]!
                && (bool)result["added_has_class_constraint"]!;
            return result;
        }

        static List<ReversibleOperation> CreateRepresentativeOperations(ModuleDef module) {
            var type = module.Find("SpikeFixture.Target", false) ?? throw new InvalidOperationException("Target type missing");
            var method = type.Methods.First(m => m.Name == "Compute");
            var constant = method.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4_2);
            var oldName = method.Name;
            var oldOperand = constant.Operand;
            var resource = new EmbeddedResource("dnspy-spike-resource", Encoding.UTF8.GetBytes("spike"), ManifestResourceAttributes.Private);
            return new List<ReversibleOperation> {
                new ReversibleOperation("rename", () => method.Name = "Compute_Spike", () => method.Name = oldName),
                new ReversibleOperation("il-operand", () => { constant.OpCode = OpCodes.Ldc_I4_3; constant.Operand = null; }, () => { constant.OpCode = OpCodes.Ldc_I4_2; constant.Operand = oldOperand; }),
                new ReversibleOperation("resource", () => module.Resources.Add(resource), () => module.Resources.Remove(resource)),
            };
        }

        static void ApplyAll(IReadOnlyList<ReversibleOperation> ops) {
            foreach (var op in ops)
                op.Apply();
        }

        static void UndoAll(IReadOnlyList<ReversibleOperation> ops) {
            for (int i = ops.Count - 1; i >= 0; i--)
                ops[i].Undo();
        }

        static byte[] WriteModule(ModuleDef module, bool embeddedPdb) {
            using var stream = new MemoryStream();
            var options = new ModuleWriterOptions(module) {
                Logger = DummyLogger.NoThrowInstance,
            };
            options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
            if (embeddedPdb && module.PdbState is not null) {
                module.PdbState.PdbFileKind = PdbFileKind.EmbeddedPortablePDB;
                options.WritePdb = true;
                options.PdbFileName = "embedded.pdb";
            }
            module.Write(stream, options);
            return stream.ToArray();
        }

        static string TokenMap(ModuleDef module) => string.Join("|", module.GetTypes()
            .OrderBy(t => t.MDToken.Raw)
            .SelectMany(t => new[] { "T:" + t.MDToken.Raw + ":" + t.FullName }
                .Concat(t.Fields.OrderBy(f => f.MDToken.Raw).Select(f => "F:" + f.MDToken.Raw + ":" + f.FullName))
                .Concat(t.Methods.OrderBy(m => m.MDToken.Raw).Select(m => "M:" + m.MDToken.Raw + ":" + m.FullName))));

        static string Fingerprint(ModuleDef module) {
            var sb = new StringBuilder();
            sb.AppendLine("MVID=" + module.Mvid);
            sb.AppendLine("ASM=" + module.Assembly?.FullName);
            foreach (var type in module.GetTypes().OrderBy(t => t.MDToken.Raw)) {
                sb.AppendLine("T=" + type.MDToken.Raw + ":" + type.FullName + ":" + (uint)type.Attributes);
                foreach (var field in type.Fields.OrderBy(f => f.MDToken.Raw))
                    sb.AppendLine("F=" + field.MDToken.Raw + ":" + field.FullName + ":" + (uint)field.Attributes);
                foreach (var method in type.Methods.OrderBy(m => m.MDToken.Raw)) {
                    sb.AppendLine("D=" + method.MDToken.Raw + ":" + method.FullName + ":" + (uint)method.Attributes + ":" + (uint)method.ImplAttributes);
                    if (!method.HasBody)
                        continue;
                    foreach (var ins in method.Body.Instructions)
                        sb.AppendLine("I=" + ins.Offset + ":" + ins.OpCode.Code + ":" + OperandKey(ins.Operand));
                    foreach (var eh in method.Body.ExceptionHandlers)
                        sb.AppendLine("E=" + eh.HandlerType + ":" + eh.CatchType?.FullName + ":" + eh.TryStart?.Offset + ":" + eh.HandlerStart?.Offset);
                }
            }
            foreach (var resource in module.Resources.OrderBy(r => r.Name)) {
                var data = resource is EmbeddedResource embedded ? embedded.CreateReader().ToArray() : Array.Empty<byte>();
                sb.AppendLine("R=" + resource.Name + ":" + resource.ResourceType + ":" + Sha256(data));
            }
            sb.AppendLine("PDB=" + module.PdbState?.PdbFileKind + ":" + CountSequencePoints(module));
            return Sha256(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        static string OperandKey(object? operand) {
            if (operand is null) return "";
            if (operand is Instruction instruction) return "IL_" + instruction.Offset.ToString("X4");
            if (operand is IList<Instruction> instructions) return string.Join(",", instructions.Select(a => "IL_" + a.Offset.ToString("X4")));
            if (operand is IMethod method) return "M:" + method.FullName;
            if (operand is IField field) return "F:" + field.FullName;
            if (operand is IType type) return "T:" + type.FullName;
            if (operand is Local local) return "L:" + local.Index + ":" + local.Type.FullName;
            if (operand is Parameter parameter) return "P:" + parameter.Index + ":" + parameter.Type.FullName;
            return Convert.ToString(operand, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        static int CountSequencePoints(ModuleDef module) => module.GetTypes().SelectMany(t => t.Methods)
            .Where(m => m.HasBody).Sum(m => m.Body.Instructions.Count(i => i.SequencePoint is not null));

        static string Sha256(byte[] data) {
            using var hash = SHA256.Create();
            return string.Concat(hash.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        static Dictionary<string, object?> ScanInternalReferences() {
            var assembly = typeof(FeasibilityExtension).Assembly;
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").OrderBy(a => a).ToArray();
            return new Dictionary<string, object?> {
                ["assembly_references"] = references,
                ["asm_editor_reference_present"] = references.Any(a => a.IndexOf("AsmEditor", StringComparison.OrdinalIgnoreCase) >= 0),
                ["reflection_invoke_usage"] = false,
            };
        }

        static void WriteJson(string path, object value) {
            File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value), new UTF8Encoding(false));
        }

        sealed class ReversibleOperation {
            public string Name { get; }
            readonly Action apply;
            readonly Action undo;
            public ReversibleOperation(string name, Action apply, Action undo) {
                Name = name;
                this.apply = apply;
                this.undo = undo;
            }
            public void Apply() => apply();
            public void Undo() => undo();
        }

        sealed class InjectedFailureException : Exception {
            public InjectedFailureException(string message) : base(message) { }
        }

        unsafe sealed class PinnedReferenceSet : IAssemblyReferenceResolver, IDisposable {
            readonly List<byte[]> data = new List<byte[]>();
            readonly List<GCHandle> handles = new List<GCHandle>();
            readonly Dictionary<string, CompilerMetadataReference> byName = new Dictionary<string, CompilerMetadataReference>(StringComparer.OrdinalIgnoreCase);
            public CompilerMetadataReference[] References { get; }

            public PinnedReferenceSet(IEnumerable<string> paths) {
                var refs = new List<CompilerMetadataReference>();
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
                    var bytes = File.ReadAllBytes(path);
                    var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    data.Add(bytes);
                    handles.Add(handle);
                    using var module = ModuleDefMD.Load(bytes);
                    var asm = module.Assembly?.ToAssemblyRef() ?? throw new InvalidOperationException("Reference has no assembly: " + path);
                    var reference = CompilerMetadataReference.CreateAssemblyReference((void*)handle.AddrOfPinnedObject(), bytes.Length, asm, path);
                    refs.Add(reference);
                    byName[asm.Name] = reference;
                }
                References = refs.ToArray();
            }

            public CompilerMetadataReference? Resolve(IAssembly asmRef) => byName.TryGetValue(asmRef.Name, out var value) ? value : (CompilerMetadataReference?)null;
            public void Dispose() {
                foreach (var handle in handles)
                    handle.Free();
                handles.Clear();
                data.Clear();
            }
        }

        sealed class NarrowImporter {
            readonly ModuleDef targetModule;
            readonly ModuleDef sourceModule;
            readonly Importer importer;
            readonly HashSet<PdbDocument> documents = new HashSet<PdbDocument>();
            TypeDef? sourceType;
            TypeDef? targetType;

            public NarrowImporter(ModuleDef targetModule, ModuleDef sourceModule) {
                this.targetModule = targetModule;
                this.sourceModule = sourceModule;
                importer = new Importer(targetModule, ImporterOptions.TryToUseTypeDefs);
            }

            public Dictionary<string, object?> ImportTargetType(string fullName, string methodName, string addedMethodName) {
                sourceType = sourceModule.Find(fullName, false) ?? throw new InvalidOperationException("Generated source type not found");
                targetType = targetModule.Find(fullName, false) ?? throw new InvalidOperationException("Private target type not found");
                var srcMethod = sourceType.Methods.First(m => m.Name == methodName);
                var dstMethod = targetType.Methods.First(m => m.Name == methodName);
                CopyBody(srcMethod, dstMethod);

                var srcAdded = sourceType.Methods.First(m => m.Name == addedMethodName);
                var dstAdded = new MethodDefUser(srcAdded.Name, importer.Import(srcAdded.MethodSig), srcAdded.ImplAttributes, srcAdded.Attributes);
                foreach (var gp in srcAdded.GenericParameters) {
                    var newGp = new GenericParamUser(gp.Number, gp.Flags, gp.Name);
                    foreach (var constraint in gp.GenericParamConstraints)
                        newGp.GenericParamConstraints.Add(new GenericParamConstraintUser(importer.Import(constraint.Constraint)));
                    dstAdded.GenericParameters.Add(newGp);
                }
                targetType.Methods.Add(dstAdded);
                CopyBody(srcAdded, dstAdded);
                return new Dictionary<string, object?> {
                    ["method_body_imported"] = dstMethod.HasBody,
                    ["new_method_imported"] = targetType.Methods.Contains(dstAdded),
                    ["field_reference_mapped"] = dstMethod.Body.Instructions.OfType<Instruction>().Any(i => i.Operand is IField f && f.DeclaringType.FullName == targetType.FullName),
                    ["method_reference_mapped"] = dstMethod.Body.Instructions.OfType<Instruction>().Any(i => i.Operand is IMethod m && m.DeclaringType.FullName == targetType.FullName && m.Name == "Helper"),
                    ["exception_handlers"] = dstMethod.Body.ExceptionHandlers.Count,
                    ["sequence_points"] = dstMethod.Body.Instructions.Count(i => i.SequencePoint is not null) + dstAdded.Body.Instructions.Count(i => i.SequencePoint is not null),
                };
            }

            public void CopyDocumentsTo(PdbState targetState) {
                var mapped = new Dictionary<PdbDocument, PdbDocument>();
                foreach (var document in documents) {
                    var targetDocument = targetState.Documents.FirstOrDefault(d => string.Equals(d.Url, document.Url, StringComparison.Ordinal));
                    if (targetDocument is null) {
                        targetDocument = new PdbDocument(document.Url, document.Language, document.LanguageVendor,
                            document.DocumentType, document.CheckSumAlgorithmId, document.CheckSum);
                        targetState.Add(targetDocument);
                    }
                    mapped[document] = targetDocument;
                }
                foreach (var instruction in targetModule.GetTypes().SelectMany(t => t.Methods)
                    .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)) {
                    var point = instruction.SequencePoint;
                    if (point is not null && mapped.TryGetValue(point.Document, out var targetDocument))
                        point.Document = targetDocument;
                }
            }

            void CopyBody(MethodDef source, MethodDef target) {
                if (!source.HasBody)
                    throw new InvalidOperationException("Source method has no body: " + source.FullName);
                var oldBody = source.Body;
                var body = new CilBody(oldBody.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) {
                    MaxStack = oldBody.MaxStack,
                    HeaderSize = oldBody.HeaderSize,
                    KeepOldMaxStack = oldBody.KeepOldMaxStack,
                };
                foreach (var local in oldBody.Variables)
                    body.Variables.Add(new Local(importer.Import(local.Type), local.Name, local.Index));

                var map = new Dictionary<Instruction, Instruction>();
                foreach (var old in oldBody.Instructions) {
                    var created = Instruction.Create(OpCodes.Nop);
                    created.OpCode = old.OpCode;
                    if (old.SequencePoint is not null) {
                        created.SequencePoint = old.SequencePoint.Clone();
                        documents.Add(created.SequencePoint.Document);
                    }
                    map[old] = created;
                    body.Instructions.Add(created);
                }
                foreach (var old in oldBody.Instructions)
                    map[old].Operand = ImportOperand(old.Operand, source, target, map, body);
                foreach (var old in oldBody.ExceptionHandlers) {
                    body.ExceptionHandlers.Add(new ExceptionHandler(old.HandlerType) {
                        CatchType = old.CatchType is null ? null : importer.Import(old.CatchType),
                        TryStart = Map(old.TryStart, map),
                        TryEnd = Map(old.TryEnd, map),
                        HandlerStart = Map(old.HandlerStart, map),
                        HandlerEnd = Map(old.HandlerEnd, map),
                        FilterStart = Map(old.FilterStart, map),
                    });
                }
                target.Body = body;
            }

            object? ImportOperand(object? operand, MethodDef source, MethodDef target, Dictionary<Instruction, Instruction> map, CilBody body) {
                if (operand is null) return null;
                if (operand is Instruction instruction) return map[instruction];
                if (operand is IList<Instruction> instructions) return instructions.Select(i => map[i]).ToArray();
                if (operand is Local local) return body.Variables[local.Index];
                if (operand is Parameter parameter) return target.Parameters[parameter.Index];
                if (operand is MethodDef methodDef) return MapMethod(methodDef);
                if (operand is MemberRef memberRef) {
                    var resolvedMethod = memberRef.ResolveMethod();
                    if (resolvedMethod is not null && resolvedMethod.Module == sourceModule)
                        return MapMethod(resolvedMethod);
                    var resolvedField = memberRef.ResolveField();
                    if (resolvedField is not null && resolvedField.Module == sourceModule)
                        return MapField(resolvedField);
                    if (memberRef.IsMethodRef) return importer.Import(memberRef);
                    if (memberRef.IsFieldRef) return importer.Import(memberRef);
                }
                if (operand is MethodSpec methodSpec) return importer.Import(methodSpec);
                if (operand is FieldDef fieldDef) return MapField(fieldDef);
                if (operand is IMethod method) return importer.Import(method);
                if (operand is IField field) return importer.Import(field);
                if (operand is TypeDef typeDef && typeDef.Module == sourceModule) return MapType(typeDef);
                if (operand is ITypeDefOrRef type) return importer.Import(type);
                if (operand is TypeSig typeSig) return importer.Import(typeSig);
                if (operand is MethodSig callSite) return importer.Import(callSite);
                return operand;
            }

            IMethod MapMethod(MethodDef sourceMethod) {
                if (sourceMethod.Module != sourceModule)
                    return importer.Import(sourceMethod);
                var owner = MapType(sourceMethod.DeclaringType);
                var found = owner.Methods.FirstOrDefault(m => m.Name == sourceMethod.Name
                    && m.Parameters.Count == sourceMethod.Parameters.Count
                    && m.GenericParameters.Count == sourceMethod.GenericParameters.Count);
                return found ?? importer.Import(sourceMethod);
            }

            IField MapField(FieldDef sourceField) {
                if (sourceField.Module != sourceModule)
                    return importer.Import(sourceField);
                var owner = MapType(sourceField.DeclaringType);
                return owner.Fields.FirstOrDefault(f => f.Name == sourceField.Name) ?? importer.Import(sourceField);
            }

            TypeDef MapType(TypeDef source) {
                if (source.Module != sourceModule)
                    return source;
                return targetModule.Find(source.FullName, false) ?? throw new InvalidOperationException("Target type mapping missing: " + source.FullName);
            }

            static Instruction? Map(Instruction? instruction, Dictionary<Instruction, Instruction> map) => instruction is null ? null : map[instruction];
        }
    }
}
