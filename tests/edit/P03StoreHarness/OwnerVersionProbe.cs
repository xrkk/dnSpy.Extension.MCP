using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

// T003-R03 formal owner/version regression.  Self-contained on a clean checkout:
// the fixed v1 packages live in fixtures/legacy-v1 next to this assembly, the
// historical vectors are recomputed from the deterministic OwnerVersionCanonical
// module, and every assertion drives the real product entry points.  Malformed
// cyclic/over-deep metadata runs in isolated child processes so a regression
// cannot take the harness down.
internal static class OwnerVersionProbe {
	static int failures;
	static int checks;
	static readonly List<string> failed = new();

	const string OldCompute = "b8d33eec40cc1f666b6847284640252cd06d8982e62913b665e5f1862083f264";
	const string OldGuard = "3ea5fd60eba04af0db389a02dc972c10c2f1bc24596b804f5625ca9f1a4e33d5";
	const string OldRoundtrip = "3f7bbaf20a7553c2f8072c266b8040f5566f77544add5ffa0c4baac357afe2b6";
	const string V1BaselineSemantic = "806506595d46ffcbb130cfec54c3f61b3533491adad79a8a3688f0e3a2884227";
	const string V1FixedSha = "3db186f57b86b5b6f9f4774529333483853cadc5b080bff0e1dc811e57a9da26";
	const string V1SwappedSha = "ac4d4b48a9014da768ebbb9e11bdcf7ddf0ab4acee54389fe85d3198ef5cc7e6";

	public static void Run(string? fixturePath) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		var fixtureDir = ResolveFixtureDir();
		HistoricalVectors();
		StrongOwnership();
		DuplicateOwnerFailClosed();
		NestedScopeControl();
		GenericCallControl();
		CycleAndDepth(fixturePath);
		PositiveControls();
		Compatibility(fixtureDir);
		Console.WriteLine("SUMMARY checks=" + checks + " failures=" + failures);
		if (failures != 0) Console.WriteLine("FAILED " + string.Join("; ", failed));
		if (failures != 0) throw new InvalidOperationException("owner-version probe failed with " + failures + " failure(s)");
	}

	static void GenericCallControl() {
		using var module = NewChildModule("GenericCall");
		var type = module.Types[1];
		var generic = new MethodDefUser("G", MethodSig.CreateStatic(module.CorLibTypes.Void)) { Body = new CilBody() };
		generic.MethodSig.GenParamCount = 1;
		generic.MethodSig.Generic = true;
		generic.GenericParameters.Add(new GenericParamUser(0));
		generic.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		type.Methods.Add(generic);
		var caller = new MethodDefUser("Caller", MethodSig.CreateStatic(module.CorLibTypes.Void)) { Body = new CilBody() };
		type.Methods.Add(caller);
		var instance = new MethodSpecUser(generic, new GenericInstMethodSig(module.CorLibTypes.Int32));
		caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, instance));
		caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		var before = EditFingerprint.ComputeRoundtripStrong(module);
		instance.GenericInstMethodSig.GenericArguments[0] = module.CorLibTypes.String;
		Check(before != EditFingerprint.ComputeRoundtripStrong(module), "generic call instantiation argument is observed");
		caller.Body.Instructions[0] = Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(module.CorLibTypes.Void));
		Check(EditFingerprint.ComputeRoundtripStrong(module) == EditFingerprint.ComputeRoundtripStrong(module), "calli signature operand is supported and stable");
	}

	static void NestedScopeControl() {
		using var module = NewChildModule("NestedScope");
		var outer = new TypeRefUser(module, "N", "Outer", new AssemblyRefUser(new AssemblyNameInfo("External, Version=1.0.0.0")));
		var inner = new TypeRefUser(module, "", "Inner", outer);
		module.Types[1].Fields.Add(new FieldDefUser("nested", new FieldSig(new ClassSig(inner))));
		var before = EditFingerprint.ComputeRoundtripStrong(module);
		Check(before == EditFingerprint.ComputeRoundtripStrong(module), "legal nested TypeRef scope is stable, not a cycle");
		outer.Name = "OtherOuter";
		Check(before != EditFingerprint.ComputeRoundtripStrong(module), "nested scope owner change is observed");
	}

	// The committed v1 bytes were produced on Linux. Adapt only their output
	// path separator for the platform-specific historical path contract, after
	// verifying original hashes. Baseline, nodes, operations and hashes stay intact.
	static byte[] PlatformFixture(byte[] package) {
		var entries = ReadEntries(package);
		var manifest = ManifestOf(package);
		var path = manifest.DefaultOutput.RelativePath.Replace('/', Path.DirectorySeparatorChar);
		if (path == manifest.DefaultOutput.RelativePath) return package;
		manifest.DefaultOutput.RelativePath = path;
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static string ResolveFixtureDir() {
		var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy-v1");
		if (!File.Exists(Path.Combine(dir, "v1-fixed-package.dnspy-mcp-checkpoints")))
			throw new InvalidOperationException("missing legacy v1 fixtures under " + dir);
		return dir;
	}

	static void Check(bool condition, string name) {
		checks++;
		if (condition) Console.WriteLine("PASS " + name);
		else { failures++; failed.Add(name); Console.WriteLine("FAIL " + name); }
	}

	static string Rename(string name) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["kind"] = "module_update", ["name"] = name }, EditWire.JsonOptions);

	static string Image(ModuleDef module) => EditWire.Sha256(EditWorkspace.WriteCheckpointImage(module));

	// ------------------------------------------------------------ historical vectors

	static void HistoricalVectors() {
		using var module = OwnerVersionCanonical.Build();
		Check(EditFingerprint.Compute(module) == OldCompute, "historical Compute fixed vector unchanged");
		Check(EditFingerprint.ComputeExternalGuard(module) == OldGuard, "historical external guard fixed vector unchanged");
		Check(EditFingerprint.ComputeRoundtrip(module) == OldRoundtrip, "historical ComputeRoundtrip fixed vector unchanged");
	}

	// ------------------------------------------------------------ strong ownership

	static MethodDef Find(ModuleDef module, string typeName, string name, int paramCount) =>
		module.GetTypes().Single(t => t.Name == typeName).Methods.Single(m => m.Name == name && m.MethodSig.Params.Count == paramCount);

	static MethodDef Find(ModuleDef module, string typeName, string name, int paramCount, int occurrence) =>
		module.GetTypes().Single(t => t.Name == typeName).Methods.Where(m => m.Name == name && m.MethodSig.Params.Count == paramCount).Skip(occurrence).First();

	static void SwapBodies(MethodDef a, MethodDef b) => (a.Body, b.Body) = (b.Body, a.Body);

	static void ExpectStrongOnly(string name, Func<ModuleDef> build, Action<ModuleDef> mutate) {
		using var module = build();
		var weak = EditFingerprint.ComputeRoundtrip(module);
		var strong = EditFingerprint.ComputeRoundtripStrong(module);
		mutate(module);
		Check(EditFingerprint.ComputeRoundtripStrong(module) != strong, name + ": strong projection changes");
		Check(EditFingerprint.ComputeRoundtrip(module) == weak, name + ": historical projection stays blind (control)");
	}

	static ModuleDef TwoValueMethods(int first, int second, TypeSig? localFirst, TypeSig? localSecond) {
		var module = new ModuleDefUser("Own.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("bbbbbbbb-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Own", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		var t2 = new TypeDefUser("N", "T2", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		module.Types.Add(t2);
		Value(t, "One", first, localFirst);
		Value(t, "Two", second, localSecond);
		Value(t2, "One", second, localSecond);
		return module;
	}

	static MethodDef Value(TypeDef type, string name, int value, TypeSig? localType) {
		var module = type.Module;
		var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		if (localType != null) method.Body.Variables.Add(new Local(localType));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}

	static void StrongOwnership() {
		ExpectStrongOnly("body swap One/Two", () => TwoValueMethods(1, 2, null, null),
			m => SwapBodies(Find(m, "T", "One", 0), Find(m, "T", "Two", 0)));
		ExpectStrongOnly("same name different type", () => TwoValueMethods(1, 2, null, null),
			m => SwapBodies(Find(m, "T", "One", 0), Find(m, "T2", "One", 0)));
		ExpectStrongOnly("same name overload", () => {
			var m = TwoValueMethods(1, 2, null, null);
			Value(m.GetTypes().Single(t => t.Name == "T"), "One", 5, null).MethodSig.Params.Add(m.CorLibTypes.Int32);
			return m;
		}, m => SwapBodies(Find(m, "T", "One", 0), Find(m, "T", "One", 1)));
		ExpectStrongOnly("local type ownership", () => {
			var m = TwoValueMethods(1, 2, null, null);
			Find(m, "T", "One", 0).Body!.Variables.Add(new Local(m.CorLibTypes.Int32));
			Find(m, "T", "Two", 0).Body!.Variables.Add(new Local(m.CorLibTypes.String));
			return m;
		}, m => SwapBodies(Find(m, "T", "One", 0), Find(m, "T", "Two", 0)));
		ExpectStrongOnly("exception handler ownership", () => {
			var m = TwoValueMethods(1, 2, null, null);
			AddEh(Find(m, "T", "One", 0), m.CorLibTypes.Object.TypeDefOrRef);
			AddEh(Find(m, "T", "Two", 0), m.GetTypes().Single(t => t.Name == "T2"));
			return m;
		}, m => SwapBodies(Find(m, "T", "One", 0), Find(m, "T", "Two", 0)));
		ExpectStrongOnly("generic parameter ownership", () => TwoGenericMethods("G1", "TP1", "G2", "TP2"),
			m => {
				var g1 = Find(m, "T", "G1", 0).GenericParameters[0];
				var g2 = Find(m, "T", "G2", 0).GenericParameters[0];
				var name = g1.Name; g1.Name = g2.Name; g2.Name = name;
			});
		ExpectStrongOnly("parameter ownership", () => TwoParameterMethods("P1", "x", "P2", "y"),
			m => {
				var p1 = Find(m, "T", "P1", 1).ParamDefs.Single(p => p.Sequence == 1);
				var p2 = Find(m, "T", "P2", 1).ParamDefs.Single(p => p.Sequence == 1);
				var name = p1.Name; p1.Name = p2.Name; p2.Name = name;
			});
		ExpectStrongOnly("sequence point instruction position", SequencePointModule,
			m => {
				var body = Find(m, "T", "Sp", 0).Body!;
				var first = body.Instructions[0].SequencePoint;
				var second = body.Instructions[1].SequencePoint;
				body.Instructions[0].SequencePoint = second;
				body.Instructions[1].SequencePoint = first;
			});
		ExpectStrongOnly("type generic parameter ownership", TypeGenericModule,
			m => {
				var p1 = m.GetTypes().Single(t => t.Name == "T1").GenericParameters[0];
				var p2 = m.GetTypes().Single(t => t.Name == "T2").GenericParameters[0];
				var name = p1.Name; p1.Name = p2.Name; p2.Name = name;
			});
		ExpectStrongOnly("interface ownership", InterfaceModule,
			m => {
				var t1 = m.GetTypes().Single(t => t.Name == "T1");
				var t2 = m.GetTypes().Single(t => t.Name == "T2");
				var first = t1.Interfaces[0]; t1.Interfaces[0] = t2.Interfaces[0]; t2.Interfaces[0] = first;
			});
		ExpectStrongOnly("modreq overload ownership", ModifierOverloadModule,
			m => SwapBodies(Find(m, "T", "M", 1, 0), Find(m, "T", "M", 1, 1)));
		ExpectStrongOnly("assembly scope overload ownership", ScopeOverloadModule,
			m => SwapBodies(Find(m, "T", "M", 1, 0), Find(m, "T", "M", 1, 1)));
	}

	// F1/T003-R03: the R02 counterexample.  Two type rows with the same structured
	// owner (and two same-name/same-signature methods) are ambiguous input; the
	// projection must fail closed instead of producing a colliding hash.
	static void DuplicateOwnerFailClosed() {
		using (var module = new ModuleDefUser("DupType.dll") { Kind = ModuleKind.Dll }) {
			new AssemblyDefUser("DupType", new Version(1, 0)).Modules.Add(module);
			var first = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
			var second = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(first);
			module.Types.Add(second);
			Value(first, "One", 1, null);
			Value(second, "Two", 2, null);
			var historicalBefore = EditFingerprint.ComputeRoundtrip(module);
			Check(ThrowsCapability(() => EditFingerprint.ComputeRoundtripStrong(module)), "duplicate type owner fails with EDIT_CAPABILITY_UNAVAILABLE");
			Check(EditFingerprint.ComputeRoundtrip(module) == historicalBefore && module.GetTypes().Count(t => t.Name == "T") == 2,
				"duplicate type owner rejection leaves the input unchanged");
		}
		using (var module = new ModuleDefUser("DupMethod.dll") { Kind = ModuleKind.Dll }) {
			new AssemblyDefUser("DupMethod", new Version(1, 0)).Modules.Add(module);
			var type = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			var body1 = new CilBody();
			body1.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
			body1.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var body2 = new CilBody();
			body2.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_2));
			body2.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var first = new MethodDefUser("F", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = body1 };
			var second = new MethodDefUser("F", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = body2 };
			type.Methods.Add(first);
			type.Methods.Add(second);
			var historicalBefore = EditFingerprint.ComputeRoundtrip(module);
			Check(ThrowsCapability(() => EditFingerprint.ComputeRoundtripStrong(module)), "duplicate method owner fails with EDIT_CAPABILITY_UNAVAILABLE");
			Check(EditFingerprint.ComputeRoundtrip(module) == historicalBefore && type.Methods.Count == 2,
				"duplicate method owner rejection leaves the input unchanged");
		}
	}

	static bool ThrowsCapability(Action action) {
		try { action(); return false; }
		catch (EditDomainException ex) when (ex.Code == "EDIT_CAPABILITY_UNAVAILABLE") { return true; }
	}

	// ------------------------------------------------------------ cycles and depth

	static void CycleAndDepth(string? fixturePath) {
		foreach (var name in new[] { "type-ref-scope-cycle", "fnptr-methodsig-cycle", "fnptr-non-methodsig", "type-declaring-cycle", "typespec-cycle" }) {
			var output = RunChild(fixturePath, name);
			Check(output.Contains("CASE " + name + " code=EDIT_CAPABILITY_UNAVAILABLE", StringComparison.Ordinal),
				"malformed " + name + " fails with EDIT_CAPABILITY_UNAVAILABLE (isolated child)");
		}
		foreach (var depth in new[] { 40, 1000, 2000 }) {
			var output = RunChild(fixturePath, "deep-ptr-" + depth);
			Check(output.Contains("CASE deep-ptr-" + depth + " strong=", StringComparison.Ordinal),
				"legal " + depth + "-deep signature computes (no truncation)");
		}
		var overDeep = RunChild(fixturePath, "deep-ptr-2049");
		Check(overDeep.Contains("CASE deep-ptr-2049 code=EDIT_CAPABILITY_UNAVAILABLE", StringComparison.Ordinal),
			"over-deep signature fails loudly at the documented graph bound");
	}

	static string RunChild(string? fixturePath, string name) {
		var executable = Environment.ProcessPath ?? throw new InvalidOperationException("no process path available");
		var arguments = new List<string>();
		if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
			arguments.Add(typeof(OwnerVersionProbe).Assembly.Location);
		arguments.Add(fixturePath ?? AppContext.BaseDirectory);
		arguments.Add("--owner-version-child");
		arguments.Add(name);
		var start = new ProcessStartInfo {
			FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
		};
		foreach (var argument in arguments) start.ArgumentList.Add(argument);
		using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start child probe");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(60000)) {
			try { process.Kill(entireProcessTree: true); } catch { }
			return "TIMEOUT";
		}
		return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
	}

	// Child entry point: runs one malformed/deep case and reports the outcome.
	// Kept public so the harness dispatch can route it without reflection.
	public static void RunChildCase(string name) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		try {
			switch (name) {
			case "type-ref-scope-cycle": TypeRefScopeCycle(); break;
			case "fnptr-methodsig-cycle": FnPtrMethodSigCycle(); break;
			case "fnptr-non-methodsig": FnPtrNonMethodSig(); break;
			case "type-declaring-cycle": TypeDeclaringCycle(); break;
			case "typespec-cycle": TypeSpecCycle(); break;
			case "deep-ptr-40": Deep(40); break;
			case "deep-ptr-1000": Deep(1000); break;
			case "deep-ptr-2000": Deep(2000); break;
			case "deep-ptr-2049": Deep(2049); break;
			default: throw new ArgumentException("unknown child case " + name);
			}
		}
		catch (EditDomainException ex) {
			Console.WriteLine("CASE " + name + " code=" + ex.Code);
		}
		catch (Exception ex) {
			Console.WriteLine("CASE " + name + " unexpected=" + ex.GetType().FullName + ":" + ex.Message);
		}
	}

	static ModuleDef NewChildModule(string name) {
		var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("12345678-2222-3333-4444-555555555555") };
		new AssemblyDefUser(name, new Version(1, 0)).Modules.Add(module);
		var type = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(type);
		return module;
	}

	static void ChildHash(ModuleDef module, string name) =>
		Console.WriteLine("CASE " + name + " strong=" + EditFingerprint.ComputeRoundtripStrong(module));

	static void TypeRefScopeCycle() {
		using var module = NewChildModule("ScopeCycle");
		var outer = new TypeRefUser(module, "N", "A", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")));
		var inner = new TypeRefUser(module, "N", "B", outer);
		outer.ResolutionScope = inner;
		module.Types[0].Fields.Add(new FieldDefUser("f", new FieldSig(new ClassSig(outer))));
		ChildHash(module, "type-ref-scope-cycle");
	}

	static void FnPtrMethodSigCycle() {
		using var module = NewChildModule("FnPtrCycle");
		var signature = MethodSig.CreateStatic(module.CorLibTypes.Void);
		var fnptr = new FnPtrSig(signature);
		signature.Params.Add(fnptr);
		module.Types[0].Fields.Add(new FieldDefUser("f", new FieldSig(fnptr)));
		ChildHash(module, "fnptr-methodsig-cycle");
	}

	static void FnPtrNonMethodSig() {
		using var module = NewChildModule("FnPtrNonMethod");
		module.Types[0].Fields.Add(new FieldDefUser("f", new FieldSig(new FnPtrSig(new LocalSig()))));
		ChildHash(module, "fnptr-non-methodsig");
	}

	static void TypeDeclaringCycle() {
		using var module = NewChildModule("DeclCycle");
		var outer = module.Types[0];
		var inner = new TypeDefUser("N", "I", module.CorLibTypes.Object.TypeDefOrRef);
		outer.NestedTypes.Add(inner);
		inner.DeclaringType = inner;
		outer.Fields.Add(new FieldDefUser("f", new FieldSig(new ClassSig(inner))));
		ChildHash(module, "type-declaring-cycle");
	}

	static void TypeSpecCycle() {
		using var module = NewChildModule("SpecCycle");
		var spec = new TypeSpecUser(module.CorLibTypes.Object);
		var instance = new GenericInstSig(new ClassSig(spec), new TypeSig[0]);
		spec.TypeSig = instance;
		instance.GenericArguments.Add(new ClassSig(spec));
		module.Types[0].Fields.Add(new FieldDefUser("f", new FieldSig(new ClassSig(spec))));
		ChildHash(module, "typespec-cycle");
	}

	static void Deep(int depth) {
		using var module = NewChildModule("Deep" + depth);
		TypeSig signature = module.CorLibTypes.Int32;
		for (var i = 0; i < depth; i++) signature = new PtrSig(signature);
		module.Types[0].Fields.Add(new FieldDefUser("f", new FieldSig(signature)));
		ChildHash(module, "deep-ptr-" + depth);
	}

	// ------------------------------------------------------------ positive controls

	static void PositiveControls() {
		using (var module = TwoValueMethods(1, 2, null, null)) {
			var first = EditFingerprint.ComputeRoundtripStrong(module);
			Check(EditFingerprint.ComputeRoundtripStrong(module) == first, "strong repeat stable");
			var types = module.Types.ToArray();
			module.Types.Clear();
			foreach (var type in types.Reverse()) module.Types.Add(type);
			Check(EditFingerprint.ComputeRoundtripStrong(module) == first, "strong stable under enumeration order");
			using (var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(module))) {
				Check(EditFingerprint.ComputeRoundtripStrong(reloaded) == first, "strong stable across writer roundtrip");
				Check(EditFingerprint.ComputeRoundtrip(reloaded) == EditFingerprint.ComputeRoundtrip(module), "historical projection stable across writer roundtrip");
			}
			module.Types.Add(new TypeDefUser("dummy", Guid.NewGuid().ToString("D")));
			Check(EditFingerprint.ComputeRoundtripStrong(module) == first, "writer tombstone type excluded from strong projection");
			Check(EditFingerprint.ComputeRoundtrip(module) == EditFingerprint.ComputeRoundtrip(module), "historical tombstone control");
		}
	}

	// ------------------------------------------------------------ package compatibility

	static Dictionary<string, byte[]> ReadEntries(byte[] package) {
		using var input = new MemoryStream(package, writable: false);
		using var archive = new ZipArchive(input, ZipArchiveMode.Read);
		return archive.Entries.ToDictionary(x => x.FullName, x => { using var s = x.Open(); using var c = new MemoryStream(); s.CopyTo(c); return c.ToArray(); }, StringComparer.Ordinal);
	}

	static byte[] BuildZip(Dictionary<string, byte[]> entries) {
		using var output = new MemoryStream();
		using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
			foreach (var row in entries) { var e = archive.CreateEntry(row.Key, CompressionLevel.Optimal); using var s = e.Open(); s.Write(row.Value, 0, row.Value.Length); }
		return output.ToArray();
	}

	static EditCheckpointManifest ManifestOf(byte[] package) {
		var entries = ReadEntries(package);
		return JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
	}

	static void Inject(InMemoryEditCheckpointStore store, byte[] package, string lineageId) {
		var temp = store.CreateTemp(lineageId, package);
		store.FinalizeTemp(temp, replaceExisting: false);
	}

	static string Commit(EditHistoryModule history, EditWorkspace workspace, ModuleDef live, string familyId, string? lineageId, string? baseHead, string review, params string[] operations) {
		for (var index = 0; index < operations.Length; index++) {
			using var json = JsonDocument.Parse(operations[index]);
			EditOperationRegistry.Apply(workspace.PrivateModule, json.RootElement, workspace.ObjectIds, 0);
			workspace.NormalizedOperations.Add(operations[index]);
		}
		var binding = lineageId == null
			? new EditHistoryBinding { FamilyId = familyId }
			: new EditHistoryBinding { FamilyId = familyId, LineageId = lineageId, BaseCheckpointId = baseHead };
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, review, 1, Array.Empty<string>());
		var map = new Dictionary<string, IMDTokenProvider>(StringComparer.Ordinal);
		foreach (var operation in operations) {
			using var json = JsonDocument.Parse(operation);
			EditOperationRegistry.ApplyPersisted(live, json.RootElement, map, 0);
		}
		history.Finalize(prepared, live);
		workspace.NormalizedOperations.Clear();
		return prepared.Lineage.Manifest.LineageId + "|" + prepared.PostHeadCheckpointId;
	}

	static byte[] RewriteFormat(byte[] package, string format) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		manifest.Format = format;
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static byte[] RewriteBaseline(byte[] package, byte[] baseline, string lineageId) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		entries["baseline/module.bin"] = baseline;
		manifest.Baseline.Length = baseline.LongLength;
		manifest.Baseline.Sha256 = EditWire.Sha256(baseline);
		manifest.SourceIdentity.BaselineImageSha256 = manifest.Baseline.Sha256;
		manifest.LineageId = lineageId;
		manifest.FamilyId = "family-" + Guid.NewGuid().ToString("N");
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static byte[] RewriteHeadImage(byte[] package, string imageSha, string lineageId) {
		var entries = ReadEntries(package);
		var manifest = JsonSerializer.Deserialize<EditCheckpointManifest>(entries["manifest.json"], EditWire.JsonOptions)!;
		manifest.Checkpoints.Single(x => x.CheckpointId == manifest.HeadCheckpointId).ResultImageSha256 = imageSha;
		manifest.LineageId = lineageId;
		manifest.FamilyId = "family-" + Guid.NewGuid().ToString("N");
		entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, EditWire.JsonOptions);
		return BuildZip(entries);
	}

	static byte[] SwapBodiesInImage(byte[] imageBytes, string typeName, string first, string second) {
		using var module = ModuleDefMD.Load(imageBytes);
		var type = module.GetTypes().Single(t => t.Name == typeName);
		var a = type.Methods.Single(m => m.Name == first);
		var b = type.Methods.Single(m => m.Name == second);
		SwapBodies(a, b);
		return EditWorkspace.WriteCheckpointImage(module);
	}

	static bool OldEntriesUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, EditCheckpointManifest manifest) =>
		manifest.Checkpoints.All(node => before.TryGetValue(node.OperationEntry, out var left)
			&& after.TryGetValue(node.OperationEntry, out var right) && left.SequenceEqual(right));

	// A writer-managed tombstone type changes the image but is normalized away by
	// both the historical (excludeWriterTombstones) and strong projections.
	static byte[] TombstoneDriftImage(byte[] imageBytes) {
		using var module = ModuleDefMD.Load(imageBytes);
		module.Types.Add(new TypeDefUser("dummy", Guid.NewGuid().ToString("D")));
		return EditWorkspace.WriteCheckpointImage(module);
	}

	static ModuleDef v2LiveModule() => OwnerVersionCanonical.Build();

	static void Compatibility(string fixtureDir) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "p03-owner-version"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		const string V1 = EditHistoryModule.PackageFormatV1;
		const string V2 = EditHistoryModule.PackageFormatV2;

		// ---- fixed v1 fixture: identity, historical baseline, exact load
		var v1Package = File.ReadAllBytes(Path.Combine(fixtureDir, "v1-fixed-package.dnspy-mcp-checkpoints"));
		var v1SwappedPackage = File.ReadAllBytes(Path.Combine(fixtureDir, "v1-swapped-baseline-package.dnspy-mcp-checkpoints"));
		Check(EditWire.Sha256(v1Package) == V1FixedSha, "v1 fixed fixture matches its recorded SHA");
		Check(EditWire.Sha256(v1SwappedPackage) == V1SwappedSha, "v1 swapped fixture matches its recorded SHA");
		v1Package = PlatformFixture(v1Package);
		v1SwappedPackage = PlatformFixture(v1SwappedPackage);
		var v1Manifest = ManifestOf(v1Package);
		Check(v1Manifest.Format == V1, "v1 fixture is format v1");
		Check(v1Manifest.SourceIdentity.BaselineSemanticFingerprint == V1BaselineSemantic,
			"v1 fixture records the historical baseline semantic");
		using (var fixtureBaseline = ModuleDefMD.Load(ReadEntries(v1Package)["baseline/module.bin"]))
			Check(EditFingerprint.ComputeRoundtrip(fixtureBaseline) == V1BaselineSemantic,
				"v1 fixture baseline semantic is reproducible with the frozen algorithm");
		var v1Id = v1Manifest.LineageId;
		var v1Head = v1Manifest.HeadCheckpointId;
		var v1Root = v1Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null).CheckpointId;
		Inject(store, v1Package, v1Id);
		var v1HeadAssessment = history.Assess(v1Id, v1Head, "");
		Check(v1HeadAssessment.Classification == "exact", "v1 fixed fixture loads and classifies exact");
		using (var v1Live = ModuleDefMD.Load(v1HeadAssessment.Bytes))
		using (var v1Workspace = EditWorkspace.CreateForTesting(v1Live)) {
			var beforeEntries = ReadEntries(store.FinalBytes(v1Id));
			var appended = Commit(history, v1Workspace, v1Live, v1Manifest.FamilyId, v1Id, v1Head, "review-v1-append", Rename("V1Appended"));
			var appendedHead = appended.Split('|')[1];
			var afterEntries = ReadEntries(store.FinalBytes(v1Id));
			Check(OldEntriesUnchanged(beforeEntries, afterEntries, v1Manifest), "v1 append keeps every old operation entry byte-identical");
			var appendedLineage = history.Load(v1Id);
			Check(appendedLineage.Manifest.Format == V1, "v1 append keeps format v1");
			var appendedAssessment = history.Assess(v1Id, appendedHead, "");
			Check(appendedAssessment.Classification == "exact", "v1 appended node replays exact");
			using (var appendedModule = ModuleDefMD.Load(appendedAssessment.Bytes))
				Check(appendedAssessment.SemanticFingerprint == EditHistoryModule.SemanticDigest(V1, appendedModule), "v1 appended node uses the historical algorithm");
			// v1 plan undo/redo with returned Action inverse control
			var appendedImage = appendedAssessment.ImageSha256;
			var undoPlan = history.PlanNavigation(appendedLineage, appendedHead, v1Head);
			var undoAction = undoPlan.Apply(v1Live);
			Check(Image(v1Live) == v1HeadAssessment.ImageSha256, "v1 undo plan reaches the parent image");
			var redo = history.PlanNavigation(history.Load(v1Id), v1Head, appendedHead);
			var redoAction = redo.Apply(v1Live);
			Check(Image(v1Live) == appendedImage, "v1 redo plan reaches the appended image");
			redoAction();
			Check(Image(v1Live) == v1HeadAssessment.ImageSha256, "v1 redo plan returned Action restores the parent image");
			undoAction();
			Check(Image(v1Live) == appendedImage, "v1 undo plan returned Action restores the appended image");
			var exportPath = "export/v1-" + Guid.NewGuid().ToString("N") + ".dll";
			var exported = history.Export(history.Assess(v1Id, appendedHead, ""), exportPath, "ignored-source.dll");
			Check(store.OutputBytes(exportPath, out var exportedBytes) && exportedBytes.SequenceEqual(appendedAssessment.Bytes), "v1 export writes the replayed image");
			// branch from the root, then cross-sibling navigation back to the appended head
			var headNow = history.Load(v1Id).Manifest.HeadCheckpointId;
			var headMove = history.PrepareHeadMove(v1Id, headNow, v1Root, "undo");
			history.PlanNavigation(history.Load(v1Id), headNow, v1Root).Apply(v1Live);
			history.Finalize(headMove, v1Live);
			using var branchWorkspace = EditWorkspace.CreateForTesting(v1Live);
			var branch = Commit(history, branchWorkspace, v1Live, v1Manifest.FamilyId, v1Id, v1Root, "review-v1-branch", Rename("V1Branch"));
			var branchHead = branch.Split('|')[1];
			var branched = history.Load(v1Id);
			Check(branched.Checkpoint(branchHead).ParentCheckpointId == v1Root, "v1 branch keeps the selected parent");
			Check(branched.Manifest.Format == V1, "v1 branch stays format v1");
			// head currently branch; adjust head back to the appended child for a cross-sibling plan
			var trimmedHeadMove = history.PrepareHeadMove(v1Id, branchHead, appendedHead, "redo");
			history.PlanNavigation(history.Load(v1Id), branchHead, appendedHead).Apply(v1Live);
			history.Finalize(trimmedHeadMove, v1Live);
			var crossLineage = history.Load(v1Id);
			var crossPlan = history.PlanNavigation(crossLineage, appendedHead, branchHead);
			var crossAction = crossPlan.Apply(v1Live);
			Check(Image(v1Live) == crossLineage.Checkpoint(branchHead).ResultImageSha256, "v1 cross-sibling plan reaches the branch image");
			crossAction();
			Check(Image(v1Live) == appendedImage, "v1 cross-sibling returned Action restores the appended image");
		}

		// ---- v1 image drift is unverified and never migratable
		var v1DriftSources = new List<(byte[] Package, string Label)> { (v1SwappedPackage, "swapped baseline") };
		{
			var tombstoneId = "lineage-" + Guid.NewGuid().ToString("N");
			v1DriftSources.Add((RewriteHeadImage(store.FinalBytes(v1Id), EditWire.Sha256(TombstoneDriftImage(v1HeadAssessment.Bytes)), tombstoneId), "writer tombstone drift"));
		}
		foreach (var (package, label) in v1DriftSources) {
			var manifest = ManifestOf(package);
			var id = manifest.LineageId;
			Inject(store, package, id);
			var assessment = history.Assess(id, manifest.HeadCheckpointId, "");
			Check(assessment.ImageSha256 != assessment.Checkpoint.ResultImageSha256, "v1 " + label + " really drifts the image");
			Check(assessment.Classification == "unverified_drift", "v1 " + label + " classifies unverified_drift");
			var finalsBefore = store.ListFinalIds().Count;
			var rejected = false;
			try { history.PrepareMigration(assessment, assessment.Bytes, "review-migration"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_REPLAY_UNVERIFIED") { rejected = true; }
			Check(rejected, "v1 " + label + " migration is refused with EDIT_REPLAY_UNVERIFIED");
			Check(store.ListFinalIds().Count == finalsBefore, "v1 " + label + " refusal has no store side effect");
		}

		// ---- v2 new lineage: strong semantics, historical source_identity, drift classes
		using (var module = TwoValueMethods(1, 2, null, null))
		using (var v2Workspace = EditWorkspace.CreateForTesting(module)) {
			var family = "family-" + Guid.NewGuid().ToString("N");
			var created = Commit(history, v2Workspace, module, family, null, null, "review-v2-new", Rename("V2NewLineage"));
			var v2Id = created.Split('|')[0];
			var v2Head = created.Split('|')[1];
			var v2Lineage = history.Load(v2Id);
			Check(v2Lineage.Manifest.Format == V2, "new lineage is format v2");
			using (var baseline = ModuleDefMD.Load(v2Lineage.BaselineBytes)) {
				var historical = EditFingerprint.ComputeRoundtrip(baseline);
				var strong = EditFingerprint.ComputeRoundtripStrong(baseline);
				var root = v2Lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
				Check(v2Lineage.Manifest.SourceIdentity.BaselineSemanticFingerprint == historical, "source_identity keeps the historical baseline semantic");
				Check(root.ResultSemanticFingerprint == strong, "v2 baseline root records the strong digest");
				Check(strong != historical, "strong and historical baseline digests differ (no silent reuse)");
			}
			var v2Assessment = history.Assess(v2Id, v2Head, "");
			Check(v2Assessment.Classification == "exact", "v2 new lineage classifies exact");
			using (var replayed = ModuleDefMD.Load(v2Assessment.Bytes))
				Check(v2Assessment.SemanticFingerprint == EditFingerprint.ComputeRoundtripStrong(replayed), "v2 exact uses the strong digest");
			// same-module writer-managed drift (tombstone normalized away): validated + confirmed migration
			var writerId = "lineage-" + Guid.NewGuid().ToString("N");
			Inject(store, RewriteHeadImage(store.FinalBytes(v2Id), EditWire.Sha256(TombstoneDriftImage(v2Assessment.Bytes)), writerId), writerId);
			var writerManifest = ManifestOf(store.FinalBytes(writerId));
			var writerAssessment = history.Assess(writerId, writerManifest.HeadCheckpointId, "");
			Check(writerAssessment.ImageSha256 != writerAssessment.Checkpoint.ResultImageSha256, "v2 writer drift really drifts the image");
			Check(writerAssessment.Classification == "validated_drift", "v2 writer drift classifies validated_drift");
			Check(writerAssessment.SemanticFingerprint == writerAssessment.Checkpoint.ResultSemanticFingerprint, "v2 writer drift keeps the strong semantic");
			var migration = history.PrepareMigration(writerAssessment, writerAssessment.Bytes, "review-v2-migration");
			history.Finalize(migration, v2LiveModule());
			Check(migration.Lineage.Checkpoint(migration.PostHeadCheckpointId).Kind == "migration", "v2 confirmed migration child created");
			// swapped baseline: unverified, migration refused
			var swappedBaseline = SwapBodiesInImage(ReadEntries(store.FinalBytes(v2Id))["baseline/module.bin"], "T", "One", "Two");
			var swappedId = "lineage-" + Guid.NewGuid().ToString("N");
			Inject(store, RewriteBaseline(store.FinalBytes(v2Id), swappedBaseline, swappedId), swappedId);
			var swappedManifest = ManifestOf(store.FinalBytes(swappedId));
			var swappedAssessment = history.Assess(swappedId, swappedManifest.HeadCheckpointId, "");
			Check(swappedAssessment.Classification == "unverified_drift", "v2 swapped baseline classifies unverified_drift");
			var refused = false;
			try { history.PrepareMigration(swappedAssessment, swappedAssessment.Bytes, "review-v2-refused"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_REPLAY_CONFIRMATION_REQUIRED") { refused = true; }
			Check(refused, "v2 non-validated migration is refused");
			var artifactDir = Path.Combine(Path.GetTempPath(), "p03-owner-version-artifacts");
			Directory.CreateDirectory(artifactDir);
			File.WriteAllBytes(Path.Combine(artifactDir, "v2-fixed-package.zip"), store.FinalBytes(v2Id));
		}

		// ---- v2 plan navigation: undo/redo, cross-sibling, Action inverse, ownership-change reject
		using (var module = TwoValueMethods(1, 2, null, null))
		using (var navWorkspace = EditWorkspace.CreateForTesting(module)) {
			var family = "family-" + Guid.NewGuid().ToString("N");
			var first = Commit(history, navWorkspace, module, family, null, null, "review-v2-nav-1", Rename("V2NavOne"));
			var navId = first.Split('|')[0];
			var head1 = first.Split('|')[1];
			var second = Commit(history, navWorkspace, module, family, navId, head1, "review-v2-nav-2", Rename("V2NavTwo"));
			var head2 = second.Split('|')[1];
			var lineage = history.Load(navId);
			// The live module is a User graph, so navigation and its returned
			// inverse Action are asserted on the lineage's own semantic digest
			// (the plan's before/after contract); the byte-level replay image gate
			// is enforced inside PlanNavigation and by the exact assessments above.
			var head1Semantic = lineage.Checkpoint(head1).ResultSemanticFingerprint;
			var head2Semantic = lineage.Checkpoint(head2).ResultSemanticFingerprint;
			var undoPlan = history.PlanNavigation(lineage, head2, head1);
			var undoAction = undoPlan.Apply(module);
			Check(EditHistoryModule.SemanticDigest(V2, module) == head1Semantic, "v2 undo plan reaches the parent semantic");
			var redoPlan = history.PlanNavigation(history.Load(navId), head1, head2);
			var redoAction = redoPlan.Apply(module);
			Check(EditHistoryModule.SemanticDigest(V2, module) == head2Semantic, "v2 redo plan reaches the head semantic");
			redoAction();
			Check(EditHistoryModule.SemanticDigest(V2, module) == head1Semantic, "v2 redo plan returned Action restores the parent semantic");
			undoAction();
			Check(EditHistoryModule.SemanticDigest(V2, module) == head2Semantic, "v2 undo plan returned Action restores the head semantic");
			// branch from head1, then cross-sibling navigate to head2
			var move = history.PrepareHeadMove(navId, head2, head1, "undo");
			history.PlanNavigation(history.Load(navId), head2, head1).Apply(module);
			history.Finalize(move, module);
			var third = Commit(history, navWorkspace, module, family, navId, head1, "review-v2-nav-3", Rename("V2NavThree"));
			var head3 = third.Split('|')[1];
			var branchLineage = history.Load(navId);
			Check(branchLineage.Checkpoint(head3).ParentCheckpointId == head1, "v2 branch keeps the selected parent");
			var head3Semantic = branchLineage.Checkpoint(head3).ResultSemanticFingerprint;
			var crossPlan = history.PlanNavigation(branchLineage, head3, head2);
			var crossAction = crossPlan.Apply(module);
			Check(EditHistoryModule.SemanticDigest(V2, module) == head2Semantic, "v2 cross-sibling plan reaches the other branch semantic");
			crossAction();
			Check(EditHistoryModule.SemanticDigest(V2, module) == head3Semantic, "v2 cross-sibling returned Action restores the branch semantic");
			// ownership change with an unchanged weak hash must be refused at plan start
			var weakBefore = EditFingerprint.ComputeRoundtrip(module);
			SwapBodies(Find(module, "T", "One", 0), Find(module, "T", "Two", 0));
			Check(EditFingerprint.ComputeRoundtrip(module) == weakBefore, "ownership-change fixture keeps the weak hash (control)");
			var swappedImage = Image(module);
			var finalsBefore = store.ListFinalIds().Count;
			var rejected = false;
			try { history.PlanNavigation(history.Load(navId), head3, head2).Apply(module); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_HISTORY_CONFLICT") { rejected = true; }
			Check(rejected, "v2 plan rejects a same-weak-hash ownership change before any operation");
			Check(Image(module) == swappedImage, "v2 plan ownership rejection leaves the live module unchanged");
			Check(store.ListFinalIds().Count == finalsBefore, "v2 plan ownership rejection has no store side effect");
		}

		// ---- accept_live creates a v2 lineage and keeps the v1 package
		{
			var oldManifest = ManifestOf(v1SwappedPackage);
			var oldId = oldManifest.LineageId;
			using var acceptedModule = ModuleDefMD.Load(SimpleImage());
			using var acceptedWorkspace = EditWorkspace.CreateForTesting(acceptedModule);
			var accepted = history.PrepareAcceptedBaseline(acceptedWorkspace, oldManifest.FamilyId, oldId);
			history.Finalize(accepted, acceptedModule);
			var acceptedLineage = history.Load(accepted.Lineage.Manifest.LineageId);
			Check(acceptedLineage.Manifest.Format == V2, "accept_live creates a v2 lineage");
			Check(acceptedLineage.Manifest.SupersededLineageId == oldId, "accept_live keeps the superseded v1 lineage link");
			Check(history.Load(oldId).Manifest.Format == V1, "the old v1 package is still readable and unchanged in format");
			using var acceptedBaseline = ModuleDefMD.Load(acceptedLineage.BaselineBytes);
			Check(acceptedLineage.Head.ResultSemanticFingerprint == EditFingerprint.ComputeRoundtripStrong(acceptedBaseline), "accepted root records the strong digest");
		}

		// ---- unknown version hard reject, including internal digest entry points
		var unknown = RewriteFormat(v1Package, "dnspy.edit.checkpoints.v3");
		var unknownRejected = false;
		try { history.ValidatePackageForTesting(unknown); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { unknownRejected = true; }
		Check(unknownRejected, "unknown package format is rejected with EDIT_OPERATION_VERSION_UNSUPPORTED");
		var digestRejected = false;
		using (var module = TwoValueMethods(1, 2, null, null)) {
			try { EditHistoryModule.SemanticDigest("dnspy.edit.checkpoints.v3", module); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { digestRejected = true; }
		}
		Check(digestRejected, "SemanticDigest hard-rejects an unknown format instead of treating it as v1");
	}

	static byte[] SimpleImage() { using var module = TwoValueMethods(1, 2, null, null); return EditWorkspace.WriteCheckpointImage(module); }

	// ------------------------------------------------------------ module shapes

	static void AddEh(MethodDef method, ITypeDefOrRef catchType) {
		var body = method.Body!;
		body.MaxStack = 8;
		var ret = Instruction.Create(OpCodes.Ret);
		var start = Instruction.Create(OpCodes.Nop);
		var leave = Instruction.Create(OpCodes.Leave_S, ret);
		var handlerStart = Instruction.Create(OpCodes.Pop);
		var handlerEnd = Instruction.Create(OpCodes.Ldc_I4_M1);
		body.Instructions.Clear();
		body.Instructions.Add(start);
		body.Instructions.Add(leave);
		body.Instructions.Add(handlerStart);
		body.Instructions.Add(handlerEnd);
		body.Instructions.Add(ret);
		body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
			CatchType = catchType, TryStart = start, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = ret,
		});
	}

	static ModuleDef TwoGenericMethods(string name1, string generic1, string name2, string generic2) {
		var module = new ModuleDefUser("Gen.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("cccccccc-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Gen", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		Generic(t, name1, generic1);
		Generic(t, name2, generic2);
		return module;
	}

	static MethodDef Generic(TypeDef type, string name, string genericName) {
		var module = type.Module;
		var sig = MethodSig.CreateStatic(module.CorLibTypes.Void); sig.GenParamCount = 1;
		var method = new MethodDefUser(name, sig, MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		method.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, genericName));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}

	static ModuleDef TwoParameterMethods(string name1, string param1, string name2, string param2) {
		var module = new ModuleDefUser("Par.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("dddddddd-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Par", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		Parameter(t, name1, param1);
		Parameter(t, name2, param2);
		return module;
	}

	static MethodDef Parameter(TypeDef type, string name, string parameterName) {
		var module = type.Module;
		var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		method.ParamDefs.Add(new ParamDefUser(parameterName, 1));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}

	static ModuleDef SequencePointModule() {
		var module = new ModuleDefUser("Sp.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("eeeeeeee-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Sp", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		var method = new MethodDefUser("Sp", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		t.Methods.Add(method);
		var first = Instruction.Create(OpCodes.Ldc_I4_1);
		var second = Instruction.Create(OpCodes.Ldc_I4_2);
		method.Body.Instructions.Add(first);
		method.Body.Instructions.Add(second);
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		first.SequencePoint = new dnlib.DotNet.Pdb.SequencePoint {
			Document = new dnlib.DotNet.Pdb.PdbDocument("doc://a.cs", Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new byte[] { 1 }),
			StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2,
		};
		second.SequencePoint = new dnlib.DotNet.Pdb.SequencePoint {
			Document = new dnlib.DotNet.Pdb.PdbDocument("doc://b.cs", Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new byte[] { 2 }),
			StartLine = 2, StartColumn = 1, EndLine = 2, EndColumn = 2,
		};
		return module;
	}

	static ModuleDef TypeGenericModule() {
		var module = new ModuleDefUser("Tg.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("ffffffff-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Tg", new Version(1, 0)).Modules.Add(module);
		var t1 = new TypeDefUser("N", "T1", module.CorLibTypes.Object.TypeDefOrRef);
		var t2 = new TypeDefUser("N", "T2", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t1);
		module.Types.Add(t2);
		t1.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "TP1"));
		t2.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "TP2"));
		return module;
	}

	static ModuleDef InterfaceModule() {
		var module = new ModuleDefUser("If.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("aaaa1111-2222-3333-4444-555555555555") };
		new AssemblyDefUser("If", new Version(1, 0)).Modules.Add(module);
		var t1 = new TypeDefUser("N", "T1", module.CorLibTypes.Object.TypeDefOrRef);
		var t2 = new TypeDefUser("N", "T2", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t1);
		module.Types.Add(t2);
		t1.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(module, "N", "IF1", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))));
		t2.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(module, "N", "IF2", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")))));
		return module;
	}

	static ModuleDef ModifierOverloadModule() {
		var module = new ModuleDefUser("Mo.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("bbbb1111-2222-3333-4444-555555555555") };
		new AssemblyDefUser("Mo", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		var refA = new TypeRefUser(module, "N", "ModA", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")));
		var refB = new TypeRefUser(module, "N", "ModB", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")));
		Overload(t, new CModReqdSig(refA, module.CorLibTypes.Int32), false);
		Overload(t, new CModReqdSig(refB, module.CorLibTypes.Int32), true);
		return module;
	}

	static ModuleDef ScopeOverloadModule() {
		var module = new ModuleDefUser("So.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("cccc1111-2222-3333-4444-555555555555") };
		new AssemblyDefUser("So", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		var refA = new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0"));
		var refB = new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0"));
		Overload(t, new ClassSig(new TypeRefUser(module, "N", "T", refA)), false);
		Overload(t, new ClassSig(new TypeRefUser(module, "N", "T", refB)), true);
		return module;
	}

	static MethodDef Overload(TypeDef type, TypeSig parameter, bool doubled) {
		var module = type.Module;
		var method = new MethodDefUser("M", MethodSig.CreateStatic(module.CorLibTypes.Void, parameter), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
		if (doubled) method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}
}
