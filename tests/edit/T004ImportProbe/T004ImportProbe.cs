using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

// T004 formal in-repo probe: real Roslyn compile artifacts (built from the
// checked-in fixture sources by the plain dotnet SDK) driven through the real
// P06 importer, the real P02/P03 operation registry and the real checkpoint
// history.  Positive cases verify the full chain compile -> apply -> structural
// validation -> export -> reload -> runtime behaviour (await suspension,
// iterator MoveNext sequences, generic instantiations); negative cases verify
// rejections land before any private mutation and that mid-sequence failures
// roll back without residual synthesized rows.
internal static class T004ImportProbe {
	static int failures;

	static int Main(string[] args) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		if (args.Length == 2 && args[0] == "dump") { Dump(args[1]); return 0; }
		if (args.Length == 2 && args[0] == "repro") { Repro(args[1]); return 0; }
		if (args.Length != 3 || args[0] != "run") throw new ArgumentException("usage: T004ImportProbe run <fixturesDir> <productDir> | dump <artifact>");
		try {
			Run(args[1], args[2]);
			Console.WriteLine(failures == 0 ? "PROBE PASS" : "PROBE FAIL failures=" + failures);
			return failures == 0 ? 0 : 1;
		}
		catch (Exception ex) {
			Console.Error.WriteLine(ex);
			if (ex is EditDomainException domain) Console.WriteLine("DOMAIN-DETAIL " + EditWire.CanonicalPayload(domain.Details));
			Console.WriteLine("PROBE FAIL failures=" + (failures + 1));
			return 1;
		}
	}

	static void Repro(string opsPath) {
		// minimal reproduction of the type_spec reuse mismatch: apply the
		// reference rows of the iter plan one by one and print the rows
		var lines = File.ReadAllLines(opsPath).Where(l => l.StartsWith("PLAN[")).ToList();
		using var module = ModuleDefMD.Load(Path.GetFullPath("Fixtures/bin/T004Plain.dll"));
		var map = new Dictionary<string, IMDTokenProvider>();
		for (var index = 0; index < lines.Count; index++) {
			var payload = lines[index].Substring(lines[index].IndexOf(' ') + 1);
			using var document = JsonDocument.Parse(payload);
			var outcome = EditOperationRegistry.ApplyPersisted(module, document.RootElement, map, index);
			if (outcome.Kind == "reference_add") {
				var row = map["obj-" + index.ToString("D3") + "-00"];
				Console.WriteLine("REPRO op" + index + " -> " + row.GetType().Name + " " + ((dnlib.DotNet.IFullName)row).FullName
					+ " form=" + document.RootElement.GetProperty("reference").GetProperty("form").GetString()
					+ " node=" + (document.RootElement.GetProperty("reference").TryGetProperty("signature", out var sig) ? sig.GetRawText() : "-"));
				if (index == 37) {
					var restored = EditOperationRegistry.RestoreTypeNode(document.RootElement.GetProperty("reference").GetProperty("signature"), module, map);
					Console.WriteLine("REPRO restored=" + restored.FullName);
					string Bind(IMDTokenProvider r) => r.MDToken.Raw == 0 ? "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r) : "0x" + r.MDToken.Raw.ToString("x8");
					string Text(TypeSig s) => JsonSerializer.Serialize(EditStructuredSignatureCodec.Capture(s, Bind), EditWire.JsonOptions);
					var earlier = ((TypeSpec)map["obj-027-00"]).TypeSig;
					Console.WriteLine("REPRO text27=" + Text(earlier));
					Console.WriteLine("REPRO text37=" + Text(restored));
				}
			}
		}
	}

	static void Dump(string artifactPath) {
		using var artifact = Load(artifactPath);
		foreach (var host in new[] { "T004Plain.Simple", "TestIL.Simple", "TestIL.NewHost", "TestIL.H`1", "TestIL.Machines", "TestIL.Impl" }) {
			var type = artifact.GetTypes().FirstOrDefault(t => t.FullName == host);
			if (type == null) continue;
			Console.WriteLine("HOST " + host);
			foreach (var method in type.Methods.Where(m => m.HasBody && m.Name.String.Contains("Zero", StringComparison.Ordinal) || m.Name.String.Contains("Iter", StringComparison.Ordinal) || m.Name.String.Contains("Async", StringComparison.Ordinal) || m.Name.String.Contains("Coroutine", StringComparison.Ordinal))) {
				Console.WriteLine("METHOD " + method.FullName);
				foreach (var instruction in method.Body!.Instructions)
					Console.WriteLine("  " + instruction.OpCode.Name + " " + (instruction.Operand switch {
						null => string.Empty,
						IMDTokenProvider token => token.GetType().Name + ":" + ((dnlib.DotNet.IFullName)token).FullName + ":" + ("0x" + token.MDToken.Raw.ToString("x8")),
						Instruction target => "IL_" + method.Body.Instructions.IndexOf(target),
						_ => instruction.Operand.ToString()!,
					}));
			}
			foreach (var nested in type.NestedTypes) {
				Console.WriteLine("NESTED " + nested.FullName + " interfaces=[" + string.Join(",", nested.Interfaces.Select(i => i.Interface?.FullName)) + "]");
				foreach (var method in nested.Methods) {
					Console.WriteLine("NESTED-METHOD " + method.FullName + " overrides=[" + string.Join(",", method.Overrides.Select(o => o.MethodDeclaration?.FullName)) + "]");
					if (!method.HasBody) continue;
					foreach (var instruction in method.Body.Instructions)
						Console.WriteLine("  " + instruction.OpCode.Name + " " + (instruction.Operand switch {
							null => string.Empty,
							IMDTokenProvider token => token.GetType().Name + ":" + ((dnlib.DotNet.IFullName)token).FullName,
							Instruction target => "IL_" + method.Body.Instructions.IndexOf(target),
							_ => instruction.Operand.ToString()!,
						}));
				}
			}
		}
	}

	// ------------------------------------------------------------------ setup

	static IReadOnlyList<EditCSharpImporter.PlanRow> lastPlan = Array.Empty<EditCSharpImporter.PlanRow>();
	static Dictionary<string, IMDTokenProvider> lastMap = new();

	static void Run(string fixturesDir, string productDir) {
		fixturesDir = Path.GetFullPath(fixturesDir);
		var bin = Path.Combine(fixturesDir, "bin");
		BuildFixture(Path.Combine(fixturesDir, "T004Target.csproj"), bin);
		BuildFixture(Path.Combine(fixturesDir, "T004Plain.csproj"), bin);
		BuildFixture(Path.Combine(fixturesDir, "T004Artifact.csproj"), bin);
		var target = Path.Combine(bin, "T004Target.dll");
		var plain = Path.Combine(bin, "T004Plain.dll");
		var artifact = Path.Combine(bin, "T004Artifact.dll");

		// positives: first async/iterator adds (zero-surface host included),
		// new generic types, explicit overrides, existing replacements
		ZeroSurface(plain, artifact);
		AddOnExistingHost(target, artifact);
		NewTypeAdds(target, artifact);
		ExistingReplacements(target, artifact);

		// negatives: rejections before mutation + no side effects
		Negatives(plain, target, artifact);

		// A4: private apply -> commit -> persistent replay -> undo -> redo ->
		// export, plus mid-sequence fault compensation
		HistoryChain(plain, artifact);
	}

	static void BuildFixture(string project, string bin) {
		var info = new System.Diagnostics.ProcessStartInfo("dotnet", "build \"" + project + "\" -c Release -o \"" + bin + "\" --nologo -v:q") {
			RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
		};
		using var process = System.Diagnostics.Process.Start(info)!;
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) throw new InvalidOperationException("fixture build failed: " + project + "\n" + output);
	}

	static ModuleDef Load(string path) =>
		ModuleDefMD.Load(Path.GetFullPath(path), new ModuleCreationOptions { TryToLoadPdbFromDisk = true });

	static JsonElement Targets(params (string compiled, string action)[] rows) =>
		JsonDocument.Parse(JsonSerializer.Serialize(rows.Select(row => new Dictionary<string, object?> {
			["compiled"] = row.compiled, ["action"] = row.action,
		}).ToArray())).RootElement.Clone();

	// ---------------------------------------------------------------- helpers

	sealed class AppliedPlan {
		public IReadOnlyList<EditCSharpImporter.PlanRow> Plan = Array.Empty<EditCSharpImporter.PlanRow>();
		public List<string> Kinds = new();
	}

	static AppliedPlan CompileAndApply(string name, ModuleDef target, string artifactPath, (string compiled, string action)[] rows) {
		var plan = Compile(name, target, artifactPath, rows);
		var kinds = new List<string>();
		var objects = new Dictionary<string, IMDTokenProvider>();
		for (var index = 0; index < plan.Count; index++) {
			try {
				using var document = JsonDocument.Parse(EditWire.CanonicalPayload(plan[index].Operation));
				var outcome = EditOperationRegistry.ApplyPersisted(target, document.RootElement, objects, index);
				kinds.Add(outcome.Kind);
			}
			catch (Exception ex) {
				Console.WriteLine("APPLY-FAIL index=" + index + " kind=" + plan[index].Kind + " op=" + EditWire.CanonicalPayload(plan[index].Operation));
				Console.WriteLine("APPLY-FAIL map-keys=" + string.Join(",", objects.Keys));
				throw;
			}
		}
		EditStructuralValidator.Validate(target);
		lastPlan = plan;
		lastMap = objects;
		return new AppliedPlan { Plan = plan, Kinds = kinds };
	}

	static IReadOnlyList<EditCSharpImporter.PlanRow> Compile(string name, ModuleDef target, string artifactPath, (string compiled, string action)[] rows) {
		using var artifact = Load(artifactPath);
		using var importer = new EditCSharpImporter(artifact, target, new Dictionary<string, IMDTokenProvider>(), 0);
		var plan = importer.Compile(Targets(rows));
		Console.WriteLine("CASE " + name + " accepted rows=" + plan.Count + " kinds=" + string.Join(",", plan.Select(row => row.Kind).Distinct()));
		return plan;
	}

	static string ExportAndVerifyAssembly(ModuleDef module, string name, Func<Assembly, string> verify) {
		var path = Path.Combine(Path.GetTempPath(), "t004-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dll");
		module.Write(path);
		// A fresh collectible context per export: the default context dedupes
		// by assembly identity, which would shadow later exports of the same
		// target assembly with an earlier edited copy.
		var context = new AssemblyLoadContext("t004-" + name, isCollectible: true);
		try {
			var assembly = context.LoadFromAssemblyPath(path);
			var detail = verify(assembly);
			Console.WriteLine("CASE " + name + " runtime " + detail);
		}
		catch (Exception) {
			DumpGeneratedTree(module);
			for (var i = 0; i < lastPlan.Count; i++)
				Console.WriteLine("PLAN[" + i + "] " + EditWire.CanonicalPayload(lastPlan[i].Operation));
			foreach (var pair in lastMap.Where(p => p.Key is "obj-027-00" or "obj-028-00" or "obj-037-00" or "obj-038-00"))
				Console.WriteLine("MAP[" + pair.Key + "] " + pair.Value.GetType().Name + " " + ((dnlib.DotNet.IFullName)pair.Value).FullName);
			throw;
		}
		finally {
			context.Unload();
		}
		return path;
	}

	static void DumpGeneratedTree(ModuleDef module) {
		foreach (var type in module.GetTypes().Where(t => t.Name.String.StartsWith("<", StringComparison.Ordinal))) {
			Console.WriteLine("GEN " + type.FullName + " interfaces=[" + string.Join(",", type.Interfaces.Select(i => i.Interface?.FullName)) + "]");
			foreach (var method in type.Methods)
				Console.WriteLine("GEN-METHOD " + method.FullName + " overrides=[" + string.Join(" ; ", method.Overrides.Select(o => {
					var declaration = o.MethodDeclaration;
					return declaration.GetType().Name + "|" + ((dnlib.DotNet.IFullName)declaration).FullName
						+ "|owner=" + declaration.DeclaringType?.GetType().Name + ":" + declaration.DeclaringType?.FullName;
				})) + "]");
		}
	}

	static int TaskInt(Assembly assembly, string typeName, string methodName, object?[] args) {
		var instance = assembly.CreateInstance(typeName)!;
		var method = instance.GetType().GetMethod(methodName)!;
		var task = (Task<int>)method.Invoke(instance, args)!;
		return task.GetAwaiter().GetResult();
	}

	static int GenericTaskInt(Assembly assembly, string typeName, string methodName, object?[] args) {
		var instance = assembly.CreateInstance(typeName)!;
		var method = instance.GetType().GetMethods().Single(m => m.Name == methodName && m.IsGenericMethodDefinition);
		var task = (Task<int>)method.MakeGenericMethod(typeof(int)).Invoke(instance, args)!;
		return task.GetAwaiter().GetResult();
	}

	static List<int> IterInt(Assembly assembly, string typeName, string methodName, object?[] args) {
		var instance = assembly.CreateInstance(typeName)!;
		var method = instance.GetType().GetMethod(methodName)
			?? throw new InvalidOperationException("method missing: " + methodName + " on " + instance.GetType().FullName);
		var raw = method.Invoke(instance, args);
		Console.WriteLine("DEBUG iter " + typeName + "::" + methodName + " raw=" + (raw?.GetType().FullName ?? "null"));
		var result = (IEnumerable<int>)raw!;
		var values = new List<int>();
		foreach (var value in result) values.Add(value);
		return values;
	}

	static List<int> AsyncStreamInt(Assembly assembly, string typeName, string methodName) {
		var instance = assembly.CreateInstance(typeName)!;
		var stream = (IAsyncEnumerable<int>)instance.GetType().GetMethod(methodName)!.Invoke(instance, null)!;
		return Task.Run(async () => {
			var values = new List<int>();
			await foreach (var value in stream) values.Add(value);
			return values;
		}).GetAwaiter().GetResult();
	}

	static object StaticField(Assembly assembly, string typeName, string fieldName) =>
		assembly.GetType(typeName)!.GetField(fieldName)!.GetValue(null)!;

	static string Sequence<T>(IEnumerable<T> values) => "[" + string.Join(",", values) + "]";

	static void Check(bool condition, string label) {
		if (condition) return;
		failures++;
		Console.WriteLine("FAIL " + label);
	}

	// -------------------------------------------------------------- positives

	static void ZeroSurface(string plainPath, string artifactPath) {
		using (var plain = Load(plainPath)) {
			var before = EditFingerprint.Compute(plain);
			var applied = CompileAndApply("zero-surface-async-add", plain, artifactPath,
				new[] { ("T004Plain.Simple::ZeroSurfaceAsync(System.Int32)", "add") });
			Check(applied.Plan.Any(row => row.Kind == "reference_add"), "zero-surface synthesized reference rows");
			Check(applied.Plan.Any(row => row.Kind == "interface_add"), "zero-surface state machine interface row");
			Check(applied.Plan.Any(row => row.Kind == "attribute_add"), "zero-surface state machine attribute rows");
			ExportAndVerifyAssembly(plain, "zero-surface-async-add", assembly => {
				var value = TaskInt(assembly, "T004Plain.Simple", "ZeroSurfaceAsync", new object[] { 21 });
				Check(value == 1021, "zero-surface async runtime value");
				return "value=" + value;
			});
			Check(EditFingerprint.Compute(plain) != before, "zero-surface async changed the module");
		}
		using (var plain = Load(plainPath)) {
			CompileAndApply("zero-surface-iter-add", plain, artifactPath,
				new[] { ("T004Plain.Simple::ZeroSurfaceIter(System.Int32)", "add") });
			ExportAndVerifyAssembly(plain, "zero-surface-iter-add", assembly => {
				var values = IterInt(assembly, "T004Plain.Simple", "ZeroSurfaceIter", new object[] { 3 });
				Check(Sequence(values) == "[100,101,102]", "zero-surface iterator runtime sequence (multi MoveNext)");
				return "values=" + Sequence(values);
			});
		}
	}

	static void AddOnExistingHost(string targetPath, string artifactPath) {
		using (var target = Load(targetPath)) {
			CompileAndApply("add-async-existing-host", target, artifactPath,
				new[] { ("TestIL.Simple::NewAsync(System.Int32)", "add") });
			ExportAndVerifyAssembly(target, "add-async-existing-host", assembly => {
				var value = TaskInt(assembly, "TestIL.Simple", "NewAsync", new object[] { 21 });
				Check(value == 43, "existing-host async runtime value (await suspension)");
				return "value=" + value;
			});
		}
		using (var target = Load(targetPath)) {
			CompileAndApply("add-iterator-existing-host", target, artifactPath,
				new[] { ("TestIL.Simple::NewIter(System.Int32)", "add") });
			ExportAndVerifyAssembly(target, "add-iterator-existing-host", assembly => {
				var values = IterInt(assembly, "TestIL.Simple", "NewIter", new object[] { 4 });
				Check(Sequence(values) == "[0,1,4,9]", "existing-host iterator runtime sequence");
				return "values=" + Sequence(values);
			});
		}
		using (var target = Load(targetPath)) {
			CompileAndApply("add-generic-method-async", target, artifactPath,
				new[] { ("TestIL.Simple::GMAsync`1(!!0)", "add") });
			ExportAndVerifyAssembly(target, "add-generic-method-async", assembly => {
				var value = GenericTaskInt(assembly, "TestIL.Simple", "GMAsync", new object[] { 41 });
				Check(value == 41, "generic method async runtime value");
				return "value=" + value;
			});
		}
	}

	static void NewTypeAdds(string targetPath, string artifactPath) {
		using (var target = Load(targetPath)) {
			CompileAndApply("add-type-with-async-and-stream", target, artifactPath, new[] { ("TestIL.NewHost", "add") });
			ExportAndVerifyAssembly(target, "add-type-with-async-and-stream", assembly => {
				var value = TaskInt(assembly, "TestIL.NewHost", "HostAsync", new object[] { 1 });
				var iter = IterInt(assembly, "TestIL.NewHost", "HostIter", Array.Empty<object>());
				var stream = AsyncStreamInt(assembly, "TestIL.NewHost", "HostStream");
				Check(value == 8, "new host async runtime value");
				Check(Sequence(iter) == "[3,4,5]", "new host iterator runtime sequence");
				Check(Sequence(stream) == "[10,20]", "new host async stream runtime sequence");
				return "value=" + value + " iter=" + Sequence(iter) + " stream=" + Sequence(stream);
			});
		}
		using (var target = Load(targetPath)) {
			CompileAndApply("add-generic-type", target, artifactPath, new[] { ("TestIL.H`1", "add") });
			ExportAndVerifyAssembly(target, "add-generic-type", assembly => {
				var hostType = assembly.GetType("TestIL.H`1")!.MakeGenericType(typeof(string));
				var host = Activator.CreateInstance(hostType)!;
				var asyncValue = ((Task<string>)hostType.GetMethod("HAsync")!.Invoke(host, new object[] { "ok" })!).GetAwaiter().GetResult();
				var iterValue = (IEnumerable<string>)hostType.GetMethod("HIter")!.Invoke(host, new object[] { "v" })!;
				var innerType = assembly.GetType("TestIL.H`1+Inner`1")!.MakeGenericType(typeof(string), typeof(int));
				var inner = Activator.CreateInstance(innerType)!;
				var pair = ((Task<KeyValuePair<string, int>>)innerType.GetMethod("PairAsync")!.Invoke(inner, new object[] { "k", 3 })!).GetAwaiter().GetResult();
				Check(asyncValue == "ok", "generic host async runtime value");
				Check(iterValue.Single() == "v", "generic host iterator runtime value");
				Check(pair.Key == "k" && pair.Value == 3, "nested generic pair runtime value");
				return "async=" + asyncValue + " iter=" + iterValue.Single() + " pair=(" + pair.Key + "," + pair.Value + ")";
			});
		}
		using (var target = Load(targetPath)) {
			CompileAndApply("add-explicit-override-type", target, artifactPath, new[] { ("TestIL.Impl", "add") });
			var overrides = target.GetTypes().First(t => t.FullName == "TestIL.Impl").Methods
				.SelectMany(m => m.Overrides).ToArray();
			Check(overrides.Length == 1 && overrides[0].MethodDeclaration.DeclaringType?.FullName == "TestIL.IDamageable",
				"explicit override declaration landed");
			ExportAndVerifyAssembly(target, "add-explicit-override-type", assembly => {
				var instance = assembly.CreateInstance("TestIL.Impl")!;
				var interfaceType = assembly.GetType("TestIL.IDamageable")!;
				var damage = (int)interfaceType.GetMethod("TakeDamage")!.Invoke(instance, new object[] { 3 })!;
				Check(damage == 9, "explicit override runtime dispatch");
				return "damage=" + damage;
			});
		}
	}

	static void ExistingReplacements(string targetPath, string artifactPath) {
		using (var target = Load(targetPath)) {
			CompileAndApply("replace-existing-async", target, artifactPath, new[] { ("TestIL.Machines::DoAsync()", "replace_body") });
			ExportAndVerifyAssembly(target, "replace-existing-async", assembly => {
				StaticField(assembly, "TestIL.Machines", "Counter");
				var value = TaskInt(assembly, "TestIL.Machines", "DoAsync", Array.Empty<object>());
				var counter = (int)StaticField(assembly, "TestIL.Machines", "Counter");
				Check(value == 500 && counter == 500, "existing async replacement runtime effect");
				return "value=" + value + " counter=" + counter;
			});
		}
		using (var target = Load(targetPath)) {
			CompileAndApply("replace-existing-iterator", target, artifactPath, new[] { ("TestIL.Machines::DoCoroutine()", "replace_body") });
			ExportAndVerifyAssembly(target, "replace-existing-iterator", assembly => {
				var instance = assembly.CreateInstance("TestIL.Machines")!;
				var enumerator = (System.Collections.IEnumerator)instance.GetType().GetMethod("DoCoroutine")!.Invoke(instance, null)!;
				var steps = new List<int>();
				while (enumerator.MoveNext()) steps.Add((int)StaticField(assembly, "TestIL.Machines", "Counter"));
				var counter = (int)StaticField(assembly, "TestIL.Machines", "Counter");
				Check(Sequence(steps) == "[2,22]", "existing iterator replacement runtime sequence (two yields, then exhaustion)");
				Check(counter == 222, "existing iterator replacement runtime counter");
				return "steps=" + Sequence(steps) + " counter=" + counter;
			});
		}
	}

	// -------------------------------------------------------------- negatives

	static void Negatives(string plainPath, string targetPath, string artifactPath) {
		// type_add must reject an `interfaces` field instead of dropping rows.
		using (var plain = Load(plainPath)) {
			var before = EditFingerprint.Compute(plain);
			Rejects("type-add-interfaces-field", plain, "{\"kind\":\"type_add\",\"name\":\"T\",\"namespace\":\"T004Plain\",\"attributes\":0,"
				+ "\"interfaces\":[{\"type\":\"System.Runtime.CompilerServices.IAsyncStateMachine\"}]}");
			Check(EditFingerprint.Compute(plain) == before, "type-add-interfaces-field left no side effect");
		}
		// duplicate interface_add (owner already implements) rejects.
		using (var target = Load(targetPath)) {
			var before = EditFingerprint.Compute(target);
			var machines = target.GetTypes().First(t => t.FullName == "TestIL.Machines");
			var owner = machines.NestedTypes.FirstOrDefault(t => t.Name.String.StartsWith("<DoCoroutine>", StringComparison.Ordinal)) ?? machines;
			var token = "0x" + owner.MDToken.Raw.ToString("x8");
			var interfaceToken = "0x" + (owner.Interfaces.First().Interface?.MDToken.Raw ?? 0).ToString("x8");
			Rejects("duplicate-interface-add", target, "{\"kind\":\"interface_add\",\"owner_type\":{\"token\":\"" + token
				+ "\"},\"interface\":{\"reference\":{\"token\":\"" + interfaceToken + "\"}}}");
			Check(EditFingerprint.Compute(target) == before, "duplicate-interface-add left no side effect");
		}
		// duplicate member add rejects with a history conflict.
		using (var target = Load(targetPath)) {
			var before = EditFingerprint.Compute(target);
			try {
				using var artifact = Load(artifactPath);
				using var importer = new EditCSharpImporter(artifact, target, new Dictionary<string, IMDTokenProvider>(), 0);
				importer.Compile(Targets(("TestIL.Simple::ProbePlain(System.Int32,System.Int32)", "add")));
				failures++;
				Console.WriteLine("FAIL duplicate-member-add was accepted");
			}
			catch (EditDomainException ex) {
				Console.WriteLine("CASE duplicate-member-add rejected code=" + ex.Code);
				Check(ex.Code == "EDIT_HISTORY_CONFLICT", "duplicate member add conflict code");
			}
			Check(EditFingerprint.Compute(target) == before, "duplicate-member-add left no side effect");
		}
		// version gates: new kinds stay v1-only, unknown v2 domains reject,
		// v1 rows cannot carry the structured domain.
		using (var document = JsonDocument.Parse("{\"kind\":\"interface_add\",\"owner_type\":{\"token\":\"0x02000002\"},\"interface\":{\"type\":{\"Kind\":\"ClassSig\"}}}")) {
			try { EditOperationVersions.Validate("interface_add", 2, document.RootElement); failures++; Console.WriteLine("FAIL interface_add v2 accepted"); }
			catch (EditDomainException ex) { Check(ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED", "interface_add v2 unsupported"); }
		}
		using (var document = JsonDocument.Parse("{\"kind\":\"managed_resource_add\",\"name\":\"r\"}")) {
			try { EditOperationVersions.Validate("managed_resource_add", 2, document.RootElement); failures++; Console.WriteLine("FAIL resource v2 accepted"); }
			catch (EditDomainException ex) { Check(ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED", "non-v2 kind version unsupported"); }
		}
		using (var document = JsonDocument.Parse("{\"kind\":\"type_add\",\"name\":\"T\",\"base_type\":{\"kind\":\"type\",\"type\":{\"Kind\":\"ClassSig\"}}}")) {
			try { EditOperationVersions.Validate("type_add", 1, document.RootElement); failures++; Console.WriteLine("FAIL structured base_type on v1 accepted"); }
			catch (EditDomainException ex) { Check(ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED", "structured domain requires v2"); }
			Check(EditOperationVersions.RequiredVersion("type_add", document.RootElement) == 2, "structured base_type requires version 2");
		}
		Console.WriteLine("CASE version-gates checked");
	}

	static void Rejects(string name, ModuleDef target, string operationJson) {
		try {
			using var document = JsonDocument.Parse(operationJson);
			EditOperationRegistry.ApplyPersisted(target, document.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
			failures++;
			Console.WriteLine("FAIL " + name + " was accepted");
		}
		catch (Exception ex) {
			var code = ex is EditDomainException domain ? domain.Code : ex.GetType().Name;
			Console.WriteLine("CASE " + name + " rejected code=" + code);
		}
	}

	// ---------------------------------------------------------- history chain

	static void HistoryChain(string plainPath, string artifactPath) {
		using var catalog = new EditSchemaCatalog();
		var store = new InMemoryEditCheckpointStore(Path.Combine(Path.GetTempPath(), "t004-artifacts"));
		using var history = new EditHistoryModule(store, catalog.CheckpointPackage);
		using var live = Load(plainPath);
		using var workspace = EditWorkspace.CreateForTesting(live);
		var original = EditFingerprint.Compute(live);

		// compile against the private copy, then stage every operation on it
		using (var artifact = Load(artifactPath)) {
			using var importer = new EditCSharpImporter(artifact, workspace.PrivateModule, workspace.ObjectIds, 0);
			var plan = importer.Compile(Targets(("T004Plain.Simple::ZeroSurfaceAsync(System.Int32)", "add")));
			var staged = 0;
			foreach (var row in plan) {
				var normalized = EditWire.CanonicalPayload(row.Operation);
				using var document = JsonDocument.Parse(normalized);
				try {
					EditOperationRegistry.Apply(workspace.PrivateModule, document.RootElement, workspace.ObjectIds, staged);
				}
				catch (Exception) {
					Console.WriteLine("STAGE-FAIL index=" + (workspace.NormalizedOperations.Count + staged) + " kind=" + row.Kind + " op=" + normalized);
					Console.WriteLine("STAGE-FAIL map-keys=" + string.Join(",", workspace.ObjectIds.Keys));
					throw;
				}
				EditStructuralValidator.Validate(workspace.PrivateModule);
				workspace.NormalizedOperations.Add(normalized);
				staged++;
			}
			Console.WriteLine("CASE history-chain staged operations=" + staged);
		}

		var binding = history.ResolveBegin(workspace, null);
		var prepared = history.PrepareCommit(workspace, binding, workspace.NormalizedOperations, "review-t004", 1, Array.Empty<string>());
		// One shared object map across the sequence: later operations reference
		// the object IDs earlier operations create.
		var replayMap = new Dictionary<string, IMDTokenProvider>();
		for (var index = 0; index < workspace.NormalizedOperations.Count; index++) {
			using var document = JsonDocument.Parse(workspace.NormalizedOperations[index]);
			EditOperationRegistry.ApplyPersisted(live, document.RootElement, replayMap, index);
		}
		history.Finalize(prepared, live);
		var committed = EditFingerprint.Compute(live);
		Check(committed != original, "commit changed the live module");

		var lineage = history.Load(prepared.Lineage.Manifest.LineageId);
		var assessment = history.Assess(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, EditFingerprint.Compute(live));
		Check(assessment.Classification == "exact", "persistent exact replay");

		// undo to the root through compiled inverses (new kinds included)
		var root = lineage.Manifest.Checkpoints.Single(x => x.ParentCheckpointId == null);
		var undoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, lineage.Manifest.HeadCheckpointId, root.CheckpointId, "undo");
		var undoPlan = history.PlanNavigation(lineage, lineage.Manifest.HeadCheckpointId, root.CheckpointId);
		undoPlan.Apply(live);
		history.Finalize(undoWrite, live);
		Check(EditFingerprint.Compute(live) == original, "undo restored the original fingerprint");

		// redo forward, then export both the package and the live assembly
		var redoWrite = history.PrepareHeadMove(lineage.Manifest.LineageId, root.CheckpointId, prepared.PostHeadCheckpointId, "redo");
		var redoPlan = history.PlanNavigation(lineage, root.CheckpointId, prepared.PostHeadCheckpointId);
		redoPlan.Apply(live);
		history.Finalize(redoWrite, live);
		Check(EditFingerprint.Compute(live) == committed, "redo restored the committed fingerprint");

		var package = store.FinalBytes(lineage.Manifest.LineageId);
		var packagePath = Path.Combine(Path.GetTempPath(), "t004-export-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
		File.WriteAllBytes(packagePath, package);
		using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(package), System.IO.Compression.ZipArchiveMode.Read))
			foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("operations/", StringComparison.Ordinal)).ToArray())
			using (var reader = JsonDocument.Parse(entry.Open())) {
				var envelopes = reader.RootElement.GetProperty("operations");
				foreach (var envelope in envelopes.EnumerateArray()) {
					var kind = envelope.GetProperty("kind").GetString();
					var version = envelope.GetProperty("kind_version").GetInt32();
					var inverse = envelope.GetProperty("inverse");
					Check(inverse.GetProperty("strategy").GetString() == "compiled_state", "envelope carries compiled_state inverse for " + kind);
					Check(version is 1 or 2, "persisted kind_version in range for " + kind);
				}
			}
		ExportAndVerifyAssembly(live, "history-chain", assembly => {
			var value = TaskInt(assembly, "T004Plain.Simple", "ZeroSurfaceAsync", new object[] { 21 });
			Check(value == 1021, "history-chain exported runtime value");
			return "value=" + value + " package=" + packagePath;
		});

		FaultCompensation(plainPath, artifactPath);
	}

	// A reference-synthesis failure mid-sequence must leave no residual rows:
	// stage the compiled plan, inject a failing operation in the middle, roll
	// every earlier undo back in reverse and compare fingerprints.
	static void FaultCompensation(string plainPath, string artifactPath) {
		using var target = Load(plainPath);
		var before = EditFingerprint.Compute(target);
		List<EditCSharpImporter.PlanRow> plan;
		using (var artifact = Load(artifactPath)) {
			using var importer = new EditCSharpImporter(artifact, target, new Dictionary<string, IMDTokenProvider>(), 0);
			plan = importer.Compile(Targets(("T004Plain.Simple::ZeroSurfaceIter(System.Int32)", "add"))).ToList();
		}
		var failing = JsonDocument.Parse("{\"kind\":\"reference_add\",\"reference\":{\"form\":\"matrix_ref\"}}").RootElement.Clone();
		var objects = new Dictionary<string, IMDTokenProvider>();
		var undos = new List<Action>();
		var failed = false;
		var spliced = 0;
		for (var index = 0; index <= plan.Count; index++) {
			JsonElement operation;
			if (index == plan.Count / 2) {
				operation = failing;
				spliced = index;
			}
			else {
				var offset = index < plan.Count / 2 ? index : index - 1;
				operation = JsonDocument.Parse(EditWire.CanonicalPayload(plan[offset].Operation)).RootElement.Clone();
			}
			try {
				var outcome = EditOperationRegistry.Apply(target, operation, objects, index);
				undos.Add(outcome.Undo);
			}
			catch (Exception ex) {
				failed = true;
				Console.WriteLine("CASE fault-compensation failure-at=" + spliced + " code="
					+ (ex is EditDomainException domain ? domain.Code : ex.GetType().Name));
				break;
			}
		}
		Check(failed, "fault-compensation injected failure observed");
		for (var index = undos.Count - 1; index >= 0; index--) undos[index]();
		var restored = EditFingerprint.Compute(target);
		Check(restored == before, "fault-compensation rollback restored the original fingerprint (no residual synthesized rows)");
	}
}
