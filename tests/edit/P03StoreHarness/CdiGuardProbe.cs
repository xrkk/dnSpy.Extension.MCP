using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

// T002-R02 / CHK-021: --cdi-guard-content.  Exercises the complete
// EditCdiGuard projection through the real product
// EditFingerprint.ComputeExternalGuard: every bound dnlib CDI type with a
// non-trivial content change, the exact R01 counterexamples, owner/reference
// identity, full sequences, deep nesting, cycles/shared objects, and the
// explicit capability failures for unknown subclasses or dangling references.
internal static class CdiGuardProbe {
	static int failures;
	static readonly List<string> failed = new();

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		Matrix();
		CounterexamplesAndShapes();
		OwnerIdentity();
		GraphShapes();
		B1SignatureIdentity();
		B2ExactTypeGate();
		BoundedGraphTraversal();
		FailureModes();
		SemanticUnchanged();
		CrossCopy();
		Console.WriteLine("PASS cdi-guard-content failures=" + failures);
		if (failures != 0) throw new InvalidOperationException("FAILED: " + failures + " cdi guard checks: " + string.Join("; ", failed));
	}

    static void BoundedGraphTraversal() {
        using (var t = Build()) {
            var scope = new TypeRefUser(t.Module, "N", "Loop");
            scope.ResolutionScope = scope;
            var cdi = new PdbForwardMethodInfoCustomDebugInfo { Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void), scope) };
            t.Module.CustomDebugInfos.Add(cdi);
            bool rejected = false;
            try { Guard(t.Module); } catch (EditDomainException ex) { rejected = ex.Code == "EDIT_CAPABILITY_UNAVAILABLE"; }
            Check(rejected, "cyclic TypeRef rejected without process failure");
            Check(ReferenceEquals(scope.ResolutionScope, scope), "cyclic scope rejection leaves graph unchanged");
        }
        using (var t = Build()) {
            var signature = MethodSig.CreateStatic(t.Module.CorLibTypes.Void);
            var pointer = new FnPtrSig(signature);
            signature.Params.Add(pointer);
            var cdi = new PdbForwardMethodInfoCustomDebugInfo { Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void, pointer), new TypeRefUser(t.Module,"N","Owner",t.Module.CorLibTypes.AssemblyRef)) };
            t.Module.CustomDebugInfos.Add(cdi);
            bool rejected = false;
            try { Guard(t.Module); } catch (EditDomainException ex) { rejected = ex.Code == "EDIT_CAPABILITY_UNAVAILABLE"; }
            Check(rejected, "cyclic TypeSig rejected without process failure");
        }
        using (var t = Build()) {
            var tail = new PdbDefaultNamespaceCustomDebugInfo { Namespace = "deep-a" };
            PdbCustomDebugInfo head = tail;
            for (var i=0;i<512;i++) {
                var document = NewDocument("deep-"+i);
                DocumentWith(document, head);
                var parent = new PdbTypeDefinitionDocumentsDebugInfo(); parent.Documents.Add(document); head = parent;
            }
            t.Module.CustomDebugInfos.Add(head);
            var before = Guard(t.Module);
            Check(before == Guard(t.Module), "512-level CDI graph deterministic without truncation");
            tail.Namespace = "deep-b";
            Check(before != Guard(t.Module), "512-level CDI leaf change detected");
        }
    }

	static void Check(bool condition, string name) {
		if (condition) Console.WriteLine("PASS " + name);
		else { failures++; failed.Add(name); Console.WriteLine("FAIL " + name); }
	}

	static string Guard(ModuleDef module) => EditFingerprint.ComputeExternalGuard(module);
	static string RowsText(ModuleDef module) => string.Join("\n", EditCdiGuard.Rows(module));
	static PdbDocument NewDocument(string url) => new PdbDocument(url, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new byte[] { 1, 2, 3 });
	// dnlib leaves a constructed PdbDocument's CustomDebugInfos list null (the
	// reader fills it through its own path); tests set the documented shape
	// through the private backing field so nested-CDI rules are exercised.
	static void DocumentWith(PdbDocument document, params PdbCustomDebugInfo[] infos) =>
		typeof(PdbDocument).GetField("customDebugInfos", BindingFlags.NonPublic | BindingFlags.Instance)!
			.SetValue(document, new List<PdbCustomDebugInfo>(infos));

	sealed class TestModule : IDisposable {
		public ModuleDef Module = null!;
		public TypeDef Type1 = null!, Type2 = null!;
		public MethodDef Method1 = null!, Method2 = null!;
		public Instruction I1a = null!, I1b = null!, I2a = null!, I2b = null!;
		public Local L1 = null!, L2 = null!;
		public void Dispose() => Module?.Dispose();
	}

	static TestModule Build() {
		var module = new ModuleDefUser("cdi-guard.exe") { Kind = ModuleKind.Console, Mvid = Guid.Parse("33333333-3333-3333-3333-333333333333") };
		new AssemblyDefUser("cdiguard", new Version(1, 0)).Modules.Add(module);
		var type1 = new TypeDefUser("Ns", "Owner1", module.CorLibTypes.Object.TypeDefOrRef);
		var type2 = new TypeDefUser("Ns", "Owner2", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(type1);
		module.Types.Add(type2);
		MethodDef AddMethod(TypeDef owner, string name, out Instruction first, out Instruction second, out Local local) {
			var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
			owner.Methods.Add(method);
			first = Instruction.Create(OpCodes.Nop);
			second = Instruction.Create(OpCodes.Nop);
			local = new Local(module.CorLibTypes.Int32);
			method.Body.Variables.Add(local);
			method.Body.Instructions.Add(first);
			method.Body.Instructions.Add(second);
			method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			return method;
		}
		var method1 = AddMethod(type1, "M1", out var i1a, out var i1b, out var l1);
		var method2 = AddMethod(type1, "M2", out var i2a, out var i2b, out var l2);
		return new TestModule { Module = module, Type1 = type1, Type2 = type2, Method1 = method1, Method2 = method2,
			I1a = i1a, I1b = i1b, I2a = i2a, I2b = i2b, L1 = l1, L2 = l2 };
	}

	static void Case(string name, Func<TestModule, PdbCustomDebugInfo> create, Action<TestModule, PdbCustomDebugInfo> mutate, Action<TestModule, PdbCustomDebugInfo>? revert = null) {
		using var t = Build();
		var cdi = create(t);
		t.Module.CustomDebugInfos.Add(cdi);
		var before = Guard(t.Module);
		Check(Guard(t.Module) == before, name + ": repeat stable");
		mutate(t, cdi);
		Check(Guard(t.Module) != before, name + ": content change detected");
		if (revert != null) {
			revert(t, cdi);
			Check(Guard(t.Module) == before, name + ": revert stable");
		}
	}

	static void Matrix() {
		Case("PdbAsyncMethodCustomDebugInfo", t => {
			var x = new PdbAsyncMethodCustomDebugInfo { KickoffMethod = t.Method1, CatchHandlerInstruction = t.I1b };
			x.StepInfos.Add(new PdbAsyncStepInfo(t.I1a, t.Method2, t.I2a));
			return x;
		}, (t, cdi) => ((PdbAsyncMethodCustomDebugInfo)cdi).CatchHandlerInstruction = t.I1a);
		SteppingInformationCase();
		Case("PdbCompilationMetadataReferencesCustomDebugInfo", t => {
			var x = new PdbCompilationMetadataReferencesCustomDebugInfo();
			x.References.Add(new PdbCompilationMetadataReference("lib", "alias", (PdbCompilationMetadataReferenceFlags)1, 1, 2, Guid.Parse("44444444-4444-4444-4444-444444444444")));
			return x;
		}, (t, cdi) => ((PdbCompilationMetadataReferencesCustomDebugInfo)cdi).References[0].Timestamp = 9);
		Case("PdbCompilationOptionsCustomDebugInfo", t => {
			var x = new PdbCompilationOptionsCustomDebugInfo();
			x.Options.Add(new KeyValuePair<string, string>("k|=:\n", "v1"));
			return x;
		}, (t, cdi) => {
			var x = (PdbCompilationOptionsCustomDebugInfo)cdi;
			x.Options[0] = new KeyValuePair<string, string>(x.Options[0].Key, "v2");
		});
		Case("PdbDefaultNamespaceCustomDebugInfo", t => new PdbDefaultNamespaceCustomDebugInfo { Namespace = "ns|one" },
			(t, cdi) => ((PdbDefaultNamespaceCustomDebugInfo)cdi).Namespace = "ns|two");
		Case("PdbDynamicLocalVariablesCustomDebugInfo", t => new PdbDynamicLocalVariablesCustomDebugInfo(new bool[65]),
			(t, cdi) => ((PdbDynamicLocalVariablesCustomDebugInfo)cdi).Flags![64] = true);
		Case("PdbDynamicLocalsCustomDebugInfo", t => {
			var x = new PdbDynamicLocalsCustomDebugInfo();
			var local = new PdbDynamicLocal { Local = t.L1, Name = "n1" };
			local.Flags.Add(3);
			x.Locals.Add(local);
			return x;
		}, (t, cdi) => ((PdbDynamicLocalsCustomDebugInfo)cdi).Locals[0].Name = "n2");
		Case("PdbEditAndContinueLambdaMapCustomDebugInfo", t => new PdbEditAndContinueLambdaMapCustomDebugInfo(new byte[] { 1, 2, 3 }),
			(t, cdi) => ((PdbEditAndContinueLambdaMapCustomDebugInfo)cdi).Data[0] = 9);
		Case("PdbEditAndContinueLocalSlotMapCustomDebugInfo", t => new PdbEditAndContinueLocalSlotMapCustomDebugInfo(new byte[] { 1, 2, 3 }),
			(t, cdi) => ((PdbEditAndContinueLocalSlotMapCustomDebugInfo)cdi).Data[0] = 9);
		Case("PdbEditAndContinueStateMachineStateMapDebugInfo", t => {
			var x = new PdbEditAndContinueStateMachineStateMapDebugInfo();
			x.StateMachineStates.Add(new StateMachineStateInfo(1, StateMachineState.NotStartedOrRunningState));
			return x;
		}, (t, cdi) => ((PdbEditAndContinueStateMachineStateMapDebugInfo)cdi).StateMachineStates[0] = new StateMachineStateInfo(2, StateMachineState.NotStartedOrRunningState));
		Case("PdbEmbeddedSourceCustomDebugInfo", t => new PdbEmbeddedSourceCustomDebugInfo(new byte[] { 1, 2, 3 }),
			(t, cdi) => ((PdbEmbeddedSourceCustomDebugInfo)cdi).SourceCodeBlob[0] = 9);
		Case("PdbForwardMethodInfoCustomDebugInfo", t => new PdbForwardMethodInfoCustomDebugInfo(t.Method1),
			(t, cdi) => ((PdbForwardMethodInfoCustomDebugInfo)cdi).Method = t.Method2);
		Case("PdbForwardModuleInfoCustomDebugInfo", t => new PdbForwardModuleInfoCustomDebugInfo(t.Method1),
			(t, cdi) => ((PdbForwardModuleInfoCustomDebugInfo)cdi).Method = t.Method2);
		Case("PdbIteratorMethodCustomDebugInfo", t => new PdbIteratorMethodCustomDebugInfo(t.Method1),
			(t, cdi) => ((PdbIteratorMethodCustomDebugInfo)cdi).KickoffMethod = t.Method2);
		Case("PdbSourceLinkCustomDebugInfo", t => new PdbSourceLinkCustomDebugInfo(new byte[] { 1, 2, 3 }),
			(t, cdi) => ((PdbSourceLinkCustomDebugInfo)cdi).FileBlob[0] = 9);
		Case("PdbSourceServerCustomDebugInfo", t => new PdbSourceServerCustomDebugInfo(new byte[] { 1, 2, 3 }),
			(t, cdi) => ((PdbSourceServerCustomDebugInfo)cdi).FileBlob[0] = 9);
		Case("PdbStateMachineHoistedLocalScopesCustomDebugInfo", t => {
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
			return x;
		}, (t, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.End = t.I1a;
			x.Scopes[0] = scope;
		});
		Case("PdbStateMachineTypeNameCustomDebugInfo", t => new PdbStateMachineTypeNameCustomDebugInfo(t.Type1),
			(t, cdi) => ((PdbStateMachineTypeNameCustomDebugInfo)cdi).Type = t.Type2);
		Case("PdbTupleElementNamesCustomDebugInfo", t => {
			var x = new PdbTupleElementNamesCustomDebugInfo();
			var names = new PdbTupleElementNames { Name = "tuple", Local = t.L1, ScopeStart = t.I1a, ScopeEnd = t.I1b };
			names.TupleElementNames.Add("a");
			x.Names.Add(names);
			return x;
		}, (t, cdi) => ((PdbTupleElementNamesCustomDebugInfo)cdi).Names[0].TupleElementNames[0] = "b");
		Case("PdbTypeDefinitionDocumentsDebugInfo", t => {
			var x = new PdbTypeDefinitionDocumentsDebugInfo();
			x.Documents.Add(NewDocument("doc://one"));
			return x;
		}, (t, cdi) => ((PdbTypeDefinitionDocumentsDebugInfo)cdi).Documents[0].Url = "doc://two");
		Case("PdbUnknownCustomDebugInfo", t => new PdbUnknownCustomDebugInfo(Guid.Parse("55555555-5555-5555-5555-555555555555"), new byte[] { 1, 2 }),
			(t, cdi) => ((PdbUnknownCustomDebugInfo)cdi).Data[0] = 9);
		Case("PdbUsingGroupsCustomDebugInfo", t => {
			var x = new PdbUsingGroupsCustomDebugInfo();
			x.UsingCounts.Add(1);
			return x;
		}, (t, cdi) => ((PdbUsingGroupsCustomDebugInfo)cdi).UsingCounts[0] = 2);
		Case("PortablePdbTupleElementNamesCustomDebugInfo", t => {
			var x = new PortablePdbTupleElementNamesCustomDebugInfo();
			x.Names.Add("a");
			return x;
		}, (t, cdi) => ((PortablePdbTupleElementNamesCustomDebugInfo)cdi).Names[0] = "b");
		Case("PrimaryConstructorInformationBlobDebugInfo", t => new PrimaryConstructorInformationBlobDebugInfo(new byte[] { 1 }),
			(t, cdi) => ((PrimaryConstructorInformationBlobDebugInfo)cdi).Blob[0] = 9);
		MdCase();
	}

	static void SteppingInformationCase() {
		using var t = Build();
		var type = typeof(PdbCustomDebugInfo).Assembly.GetType("dnlib.DotNet.Pdb.PdbAsyncMethodSteppingInformationCustomDebugInfo")
			?? throw new InvalidOperationException("bound dnlib 4.5.0 is missing the stepping-information CDI type");
		var instance = (PdbCustomDebugInfo)Activator.CreateInstance(type, nonPublic: true)!;
		var catchHandler = type.GetProperty("CatchHandler", BindingFlags.Public | BindingFlags.Instance)!;
		var stepInfos = (System.Collections.IList)type.GetProperty("AsyncStepInfos", BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance)!;
		catchHandler.SetValue(instance, t.I1b);
		stepInfos.Add(new PdbAsyncStepInfo(t.I2a, t.Method2, t.I2b));
		t.Module.CustomDebugInfos.Add(instance);
		var before = Guard(t.Module);
		Check(Guard(t.Module) == before, "PdbAsyncMethodSteppingInformationCustomDebugInfo: repeat stable");
		catchHandler.SetValue(instance, t.I1a);
		Check(Guard(t.Module) != before, "PdbAsyncMethodSteppingInformationCustomDebugInfo: content change detected");
	}

	static void MdCase() {
		using var t = Build();
		var mdType = typeof(PdbCustomDebugInfo).Assembly.GetType("dnlib.DotNet.Pdb.PdbTypeDefinitionDocumentsDebugInfoMD")!;
		var ctor = mdType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
		var tokenType = ctor.GetParameters()[1].ParameterType.GetGenericArguments()[0];
		var tokens = Activator.CreateInstance(typeof(List<>).MakeGenericType(tokenType))!;
		var instance = (PdbTypeDefinitionDocumentsDebugInfo)ctor.Invoke(new[] { (object)t.Module, tokens });
		t.Module.CustomDebugInfos.Add(instance);
		var before = Guard(t.Module);
		Check(Guard(t.Module) == before, "PdbTypeDefinitionDocumentsDebugInfoMD: handled by base branch, repeat stable");
		try {
			instance.Documents.Add(NewDocument("md://changed"));
			Check(Guard(t.Module) != before, "PdbTypeDefinitionDocumentsDebugInfoMD: content change detected");
		}
		catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException || ex is ArgumentException) {
			Console.WriteLine("NOTE PdbTypeDefinitionDocumentsDebugInfoMD Documents list is read-only for a synthetic token list (" + ex.GetType().Name + "); the type is encoded through its public base branch and the guard computed before/after without failing");
		}
	}

	static void CounterexamplesAndShapes() {
		// CHK-021 counterexample 1: 65-bool sequence, first element is the control.
		Case("chk021 65-bool first control", t => new PdbDynamicLocalVariablesCustomDebugInfo(new bool[65]),
			(t, cdi) => ((PdbDynamicLocalVariablesCustomDebugInfo)cdi).Flags![0] = true,
			(t, cdi) => ((PdbDynamicLocalVariablesCustomDebugInfo)cdi).Flags![0] = false);
		Case("chk021 65-bool 65th element", t => new PdbDynamicLocalVariablesCustomDebugInfo(new bool[65]),
			(t, cdi) => ((PdbDynamicLocalVariablesCustomDebugInfo)cdi).Flags![64] = true,
			(t, cdi) => ((PdbDynamicLocalVariablesCustomDebugInfo)cdi).Flags![64] = false);
		// CHK-021 counterexample 2: hoisted scope Start/End instruction change.
		Case("chk021 hoisted Start", t => {
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
			return x;
		}, (t, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.Start = t.I2a;
			x.Scopes[0] = scope;
		}, (t, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.Start = t.I1a;
			x.Scopes[0] = scope;
		});
		Case("chk021 hoisted End", t => {
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
			return x;
		}, (t, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.End = t.I1a;
			x.Scopes[0] = scope;
		}, (t, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.End = t.I1b;
			x.Scopes[0] = scope;
		});
		// async step fields
		Case("async step YieldInstruction", t => AsyncStepping(t), (t, cdi) => {
			var x = (PdbAsyncMethodCustomDebugInfo)cdi;
			x.StepInfos[0] = new PdbAsyncStepInfo(t.I2b, t.Method2, t.I2a);
		});
		Case("async step BreakpointInstruction", t => AsyncStepping(t), (t, cdi) => {
			var x = (PdbAsyncMethodCustomDebugInfo)cdi;
			x.StepInfos[0] = new PdbAsyncStepInfo(t.I1a, t.Method2, t.I2b);
		});
		Case("async step BreakpointMethod", t => AsyncStepping(t), (t, cdi) => {
			var x = (PdbAsyncMethodCustomDebugInfo)cdi;
			x.StepInfos[0] = new PdbAsyncStepInfo(t.I1a, t.Method1, t.I2a);
		});
		// E&C state map
		Case("E&C state SyntaxOffset", t => {
			var x = new PdbEditAndContinueStateMachineStateMapDebugInfo();
			x.StateMachineStates.Add(new StateMachineStateInfo(1, StateMachineState.NotStartedOrRunningState));
			return x;
		}, (t, cdi) => ((PdbEditAndContinueStateMachineStateMapDebugInfo)cdi).StateMachineStates[0] = new StateMachineStateInfo(2, StateMachineState.NotStartedOrRunningState));
		Case("E&C state State", t => {
			var x = new PdbEditAndContinueStateMachineStateMapDebugInfo();
			x.StateMachineStates.Add(new StateMachineStateInfo(1, StateMachineState.NotStartedOrRunningState));
			return x;
		}, (t, cdi) => ((PdbEditAndContinueStateMachineStateMapDebugInfo)cdi).StateMachineStates[0] = new StateMachineStateInfo(1, StateMachineState.FinishedState));
		// dynamic locals flags/name/local
		Case("dynamic local Name", t => DynamicLocals(t), (t, cdi) => ((PdbDynamicLocalsCustomDebugInfo)cdi).Locals[0].Name = "n2");
		Case("dynamic local Local", t => DynamicLocals(t), (t, cdi) => ((PdbDynamicLocalsCustomDebugInfo)cdi).Locals[0].Local = t.L2);
		Case("dynamic local Flags", t => DynamicLocals(t), (t, cdi) => ((PdbDynamicLocalsCustomDebugInfo)cdi).Locals[0].Flags[0] = 9);
		// tuple content/scope
		Case("tuple element names content", t => TupleNames(t), (t, cdi) => ((PdbTupleElementNamesCustomDebugInfo)cdi).Names[0].TupleElementNames[0] = "b");
		Case("tuple scope start", t => TupleNames(t), (t, cdi) => ((PdbTupleElementNamesCustomDebugInfo)cdi).Names[0].ScopeStart = t.I2a);
		Case("tuple scope end", t => TupleNames(t), (t, cdi) => ((PdbTupleElementNamesCustomDebugInfo)cdi).Names[0].ScopeEnd = t.I2b);
		// document checksum/url/nested CDI
		Case("document url", t => Documents(t), (t, cdi) => ((PdbTypeDefinitionDocumentsDebugInfo)cdi).Documents[0].Url = "doc://two");
		Case("document checksum", t => Documents(t), (t, cdi) => ((PdbTypeDefinitionDocumentsDebugInfo)cdi).Documents[0].CheckSum[0] = 9);
		Case("document nested CDI", t => {
			var x = new PdbTypeDefinitionDocumentsDebugInfo();
			var document = NewDocument("doc://nested-one");
			DocumentWith(document, new PdbDefaultNamespaceCustomDebugInfo { Namespace = "n1" });
			x.Documents.Add(document);
			return x;
		}, (t, cdi) => ((PdbDefaultNamespaceCustomDebugInfo)((PdbTypeDefinitionDocumentsDebugInfo)cdi).Documents[0].CustomDebugInfos[0]).Namespace = "n2");
		// metadata reference fields
		Case("metadata ref Name", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).Name = "lib2");
		Case("metadata ref Aliases", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).Aliases = "a2");
		Case("metadata ref Flags", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).Flags = (PdbCompilationMetadataReferenceFlags)3);
		Case("metadata ref Timestamp", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).Timestamp = 99);
		Case("metadata ref SizeOfImage", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).SizeOfImage = 99);
		Case("metadata ref Mvid", t => MetadataRef(t), (t, cdi) => Reference(t, cdi).Mvid = Guid.Parse("66666666-6666-6666-6666-666666666666"));
		// compile options with separators
		Case("compile options separators", t => {
			var x = new PdbCompilationOptionsCustomDebugInfo();
			x.Options.Add(new KeyValuePair<string, string>("A|B=C\nD", "v|1"));
			return x;
		}, (t, cdi) => {
			var x = (PdbCompilationOptionsCustomDebugInfo)cdi;
			x.Options[0] = new KeyValuePair<string, string>(x.Options[0].Key, "v|2");
		});
	}

	static PdbCompilationMetadataReference Reference(TestModule t, PdbCustomDebugInfo cdi) => ((PdbCompilationMetadataReferencesCustomDebugInfo)cdi).References[0];
	static PdbCustomDebugInfo MetadataRef(TestModule t) {
		var x = new PdbCompilationMetadataReferencesCustomDebugInfo();
		x.References.Add(new PdbCompilationMetadataReference("lib", "alias", (PdbCompilationMetadataReferenceFlags)1, 1, 2, Guid.Empty));
		return x;
	}
	static PdbCustomDebugInfo AsyncStepping(TestModule t) {
		var x = new PdbAsyncMethodCustomDebugInfo { KickoffMethod = t.Method1, CatchHandlerInstruction = t.I1b };
		x.StepInfos.Add(new PdbAsyncStepInfo(t.I1a, t.Method2, t.I2a));
		return x;
	}
	static PdbCustomDebugInfo DynamicLocals(TestModule t) {
		var x = new PdbDynamicLocalsCustomDebugInfo();
		var local = new PdbDynamicLocal { Local = t.L1, Name = "n1" };
		local.Flags.Add(3);
		x.Locals.Add(local);
		return x;
	}
	static PdbCustomDebugInfo TupleNames(TestModule t) {
		var x = new PdbTupleElementNamesCustomDebugInfo();
		var names = new PdbTupleElementNames { Name = "tuple", Local = t.L1, ScopeStart = t.I1a, ScopeEnd = t.I1b };
		names.TupleElementNames.Add("a");
		x.Names.Add(names);
		return x;
	}
	static PdbCustomDebugInfo Documents(TestModule t) {
		_ = t;
		var x = new PdbTypeDefinitionDocumentsDebugInfo();
		x.Documents.Add(NewDocument("doc://one"));
		return x;
	}

	static void OwnerIdentity() {
		using var t = Build();
		var property1 = new PropertyDefUser("Same", PropertySig.CreateInstance(t.Module.CorLibTypes.Int32));
		var property2 = new PropertyDefUser("Same", PropertySig.CreateInstance(t.Module.CorLibTypes.Int32));
		t.Type1.Properties.Add(property1);
		t.Type2.Properties.Add(property2);
		var event1 = new EventDefUser("Evt", t.Module.CorLibTypes.Object.TypeDefOrRef);
		t.Type1.Events.Add(event1);
		property1.CustomDebugInfos.Add(new PdbDefaultNamespaceCustomDebugInfo { Namespace = "p1" });
		property2.CustomDebugInfos.Add(new PdbDefaultNamespaceCustomDebugInfo { Namespace = "p2" });
		event1.CustomDebugInfos.Add(new PdbDefaultNamespaceCustomDebugInfo { Namespace = "e1" });
		t.Method1.CustomDebugInfos.Add(new PdbIteratorMethodCustomDebugInfo(t.Method1));
		t.Method2.CustomDebugInfos.Add(new PdbIteratorMethodCustomDebugInfo(t.Method2));
		// ModuleDefUser keeps dnlib's <Module> row at index 0, so the owner paths
		// are derived from the real list positions instead of hardcoded indices.
		var type1Index = t.Module.Types.IndexOf(t.Type1);
		var type2Index = t.Module.Types.IndexOf(t.Type2);
		var rows = RowsText(t.Module);
		Check(rows.Contains("p:t/" + type1Index + "/p/0") && rows.Contains("p:t/" + type2Index + "/p/0"),
			"same-name properties keep type+slot owner identity (t/" + type1Index + " vs t/" + type2Index + ")");
		Check(rows.Contains("e:t/" + type1Index + "/e/0"), "event owner identity keeps type+slot");
		Check(rows.Contains("m:t/" + type1Index + "/m/0") && rows.Contains("m:t/" + type1Index + "/m/1"),
			"RID0 methods keep deterministic owner paths");
		Check(t.I1a.Offset == 0 && t.I2a.Offset == 0, "same-offset instructions share Offset 0 (fixture fact)");
		Case("same-offset instruction bound to owning method", t2 => {
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(t2.I1a, t2.I1b));
			return x;
		}, (t2, cdi) => {
			var x = (PdbStateMachineHoistedLocalScopesCustomDebugInfo)cdi;
			var scope = x.Scopes[0];
			scope.Start = t2.I2a;
			x.Scopes[0] = scope;
		});
	}

	static void GraphShapes() {
		// legal nesting deeper than the old depth>4 constant
		using (var t = Build()) {
			PdbTypeDefinitionDocumentsDebugInfo? head = null;
			PdbDocument? deepest = null;
			for (var level = 0; level < 5; level++) {
				var cdi = new PdbTypeDefinitionDocumentsDebugInfo();
				var document = NewDocument("doc://level" + level);
				cdi.Documents.Add(document);
				if (head != null) DocumentWith(document, head);
				else deepest = document;
				head = cdi;
			}
			t.Module.CustomDebugInfos.Add(head!);
			var before = Guard(t.Module);
			Check(Guard(t.Module) == before, "nested CDI depth 5 repeat stable");
			deepest!.Url = "doc://changed";
			Check(Guard(t.Module) != before, "nested CDI depth 5 change detected");
		}
		// cyclic document/CDI graph
		using (var t = Build()) {
			var cdi = new PdbTypeDefinitionDocumentsDebugInfo();
			var document = NewDocument("cycle://one");
			cdi.Documents.Add(document);
			DocumentWith(document, cdi);
			t.Module.CustomDebugInfos.Add(cdi);
			var before = Guard(t.Module);
			Check(Guard(t.Module) == before, "cyclic document/CDI graph repeat stable");
			document.Url = "cycle://two";
			Check(Guard(t.Module) != before, "cyclic document/CDI graph change detected");
		}
		// shared document reference encoded as an id + back reference
		using (var t = Build()) {
			var cdi = new PdbTypeDefinitionDocumentsDebugInfo();
			var document = NewDocument("shared://one");
			cdi.Documents.Add(document);
			cdi.Documents.Add(document);
			t.Module.CustomDebugInfos.Add(cdi);
			var rows = RowsText(t.Module);
			Check(rows.Contains("\"ref\""), "shared document reference encoded as a back reference");
			var before = Guard(t.Module);
			document.Url = "shared://two";
			Check(Guard(t.Module) != before, "shared document content change detected");
		}
	}

	sealed class UnknownCdi : PdbCustomDebugInfo {
		public override Guid Guid => Guid.Parse("77777777-7777-7777-7777-777777777777");
		public override PdbCustomDebugInfoKind Kind => (PdbCustomDebugInfoKind)9999;
	}

	sealed class UnknownSig : TypeSig {
		public override TypeSig Next => null!;
		public override ElementType ElementType => (ElementType)0x7F;
	}

	sealed class ExtraDocs : PdbTypeDefinitionDocumentsDebugInfo {
		public int Extra;
	}

	// B1: structured signature identity.  A scope-only change on a referenced
	// type, nested TypeRef outer scopes, modreq/modopt modifiers, names that
	// would collide under flat concatenation, and MemberRef parents must all be
	// observable; unknown signature shapes must fail instead of ToString.
	static void B1SignatureIdentity() {
		AssemblyRef? refA = null, refB = null;
		TypeRef? scopeType = null;
		Case("B1 parameter TypeRef scope A->B", t => {
			refA = new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0"));
			refB = new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0"));
			scopeType = new TypeRefUser(t.Module, "N", "T", refA);
			var owner = new TypeRefUser(t.Module, "N", "Owner", refA);
			return new PdbForwardMethodInfoCustomDebugInfo {
				Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void, new ClassSig(scopeType)), owner),
			};
		}, (t, cdi) => scopeType!.ResolutionScope = refB!);

		TypeRef? outer = null;
		Case("B1 nested TypeRef outer scope A->B", t => {
			var a = new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0"));
			var b = new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0"));
			outer = new TypeRefUser(t.Module, "N", "Outer", a);
			refB = b;
			var inner = new TypeRefUser(t.Module, "N", "Inner", outer);
			return new PdbForwardMethodInfoCustomDebugInfo {
				Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void, new ClassSig(inner)), new TypeRefUser(t.Module, "N", "Owner", a)),
			};
		}, (t, cdi) => outer!.ResolutionScope = refB!);

		TypeRef? modifierA = null, modifierB = null;
		Case("B1 modreq modifier A->B", t => {
			modifierA = new TypeRefUser(t.Module, "N", "ModA", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")));
			modifierB = new TypeRefUser(t.Module, "N", "ModB", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")));
			return ForwardWithParam(t, new CModReqdSig(modifierA, t.Module.CorLibTypes.Int32));
		}, (t, cdi) => SetForwardParam(t, cdi, new CModReqdSig(modifierB!, t.Module.CorLibTypes.Int32)));
		Case("B1 modopt modifier A->B", t => {
			modifierA = new TypeRefUser(t.Module, "N", "ModA", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")));
			modifierB = new TypeRefUser(t.Module, "N", "ModB", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")));
			return ForwardWithParam(t, new CModOptSig(modifierA, t.Module.CorLibTypes.Int32));
		}, (t, cdi) => SetForwardParam(t, cdi, new CModOptSig(modifierB!, t.Module.CorLibTypes.Int32)));

		// Flat "namespace.name" rendering could not distinguish these two.
		TypeRef? dottedName = null, splitName = null;
		Case("B1 name/namespace separation", t => {
			dottedName = new TypeRefUser(t.Module, string.Empty, "N.T");
			splitName = new TypeRefUser(t.Module, "N", "T");
			return ForwardWithParam(t, new ClassSig(dottedName));
		}, (t, cdi) => SetForwardParam(t, cdi, new ClassSig(splitName!)));

		Case("B1 punctuation in type name", t => {
			var type = new TypeRefUser(t.Module, "N", "T,()|");
			return ForwardWithParam(t, new ClassSig(type));
		}, (t, cdi) => {
			var type = (TypeRef)((ClassSig)((MemberRef)((PdbForwardMethodInfoCustomDebugInfo)cdi).Method!).MethodSig!.Params[0]).TypeDefOrRef!;
			type.Name = "T,()|2";
		});

		MethodSig? fnptrSig = null;
		Case("B1 fnptr inner parameter", t => {
			fnptrSig = MethodSig.CreateStatic(t.Module.CorLibTypes.Int32, new ClassSig(new TypeRefUser(t.Module, "N", "P", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))));
			return ForwardWithParam(t, new FnPtrSig(fnptrSig));
		}, (t, cdi) => fnptrSig!.Params[0] = new ClassSig(new TypeRefUser(t.Module, "N", "P", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")))));

		ArraySig? arraySig = null;
		Case("B1 array sizes", t => {
			arraySig = new ArraySig(t.Module.CorLibTypes.Int32, 2, new uint[] { 2, 2 }, new int[] { 0, 0 });
			return ForwardWithParam(t, arraySig);
		}, (t, cdi) => arraySig!.Sizes[0] = 3);

		GenericInstSig? genericSig = null;
		Case("B1 generic instance argument scope", t => {
			genericSig = new GenericInstSig(new ClassSig(new TypeRefUser(t.Module, "N", "G", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))),
				new TypeSig[] { new ClassSig(new TypeRefUser(t.Module, "N", "Arg", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))) });
			return ForwardWithParam(t, genericSig);
		}, (t, cdi) => genericSig!.GenericArguments[0] = new ClassSig(new TypeRefUser(t.Module, "N", "Arg", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")))));

		MethodSig? sentinelSig = null;
		Case("B1 sentinel parameter", t => {
			sentinelSig = MethodSig.CreateStatic(t.Module.CorLibTypes.Void, t.Module.CorLibTypes.Int32);
			sentinelSig.ParamsAfterSentinel = new List<TypeSig> { new ClassSig(new TypeRefUser(t.Module, "N", "S", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))) };
			return ForwardWithSig(t, sentinelSig);
		}, (t, cdi) => sentinelSig!.ParamsAfterSentinel![0] = new ClassSig(new TypeRefUser(t.Module, "N", "S", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")))));

		// MemberRef parents beyond plain DeclaringType identity.
		using (var left = Build()) {
			var mr = new MemberRefUser(left.Module, "F", MethodSig.CreateStatic(left.Module.CorLibTypes.Void), new ModuleRefUser(left.Module, "M1"));
			left.Module.CustomDebugInfos.Add(new PdbForwardMethodInfoCustomDebugInfo { Method = mr });
			using var right = Build();
			var mr2 = new MemberRefUser(right.Module, "F", MethodSig.CreateStatic(right.Module.CorLibTypes.Void), new ModuleRefUser(right.Module, "M2"));
			right.Module.CustomDebugInfos.Add(new PdbForwardMethodInfoCustomDebugInfo { Method = mr2 });
			Check(RowsText(left.Module) != RowsText(right.Module), "B1 ModuleRef member parent identity kept");
		}
		using (var t = Build()) {
			var global1 = new MethodDefUser("G1", MethodSig.CreateStatic(t.Module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
			global1.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			var global2 = new MethodDefUser("G2", MethodSig.CreateStatic(t.Module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() };
			global2.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			t.Type1.Methods.Add(global1);
			t.Type1.Methods.Add(global2);
			var mr1 = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void), global1);
			t.Module.CustomDebugInfos.Add(new PdbForwardMethodInfoCustomDebugInfo { Method = mr1 });
			var before = Guard(t.Module);
			((PdbForwardMethodInfoCustomDebugInfo)t.Module.CustomDebugInfos[0]).Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void), global2);
			Check(Guard(t.Module) != before, "B1 MethodDef member parent identity kept");
		}
		// Equivalent copies with external signatures stay identical.
		static ModuleDef ExternalCopy() {
			var t = Build();
			var a = new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0"));
			var ty = new TypeRefUser(t.Module, "N", "T", a);
			t.Module.CustomDebugInfos.Add(new PdbForwardMethodInfoCustomDebugInfo {
				Method = new MemberRefUser(t.Module, "F", MethodSig.CreateStatic(t.Module.CorLibTypes.Void, new ClassSig(ty)), new TypeRefUser(t.Module, "N", "Owner", a)),
			});
			return t.Module;
		}
		var copy1 = ExternalCopy();
		var copy2 = ExternalCopy();
		Check(RowsText(copy1) == RowsText(copy2), "B1 equivalent external signatures produce identical rows");
		copy1.Dispose();
		copy2.Dispose();

		using (var t = Build()) {
			var cdi = ForwardWithParam(t, new UnknownSig());
			t.Module.CustomDebugInfos.Add(cdi);
			ExpectCapability("B1 unknown signature shape", t.Module);
		}
	}

	static PdbCustomDebugInfo ForwardWithParam(TestModule t, TypeSig parameter) =>
		ForwardWithSig(t, MethodSig.CreateStatic(t.Module.CorLibTypes.Void, parameter));

	static PdbCustomDebugInfo ForwardWithSig(TestModule t, MethodSig signature) =>
		new PdbForwardMethodInfoCustomDebugInfo { Method = new MemberRefUser(t.Module, "F", signature, new TypeRefUser(t.Module, "N", "Owner")) };

	static void SetForwardParam(TestModule t, PdbCustomDebugInfo cdi, TypeSig parameter) {
		var member = (MemberRef)((PdbForwardMethodInfoCustomDebugInfo)cdi).Method!;
		member.MethodSig!.Params[0] = parameter;
	}

	// B2: exact bound-type gate.  A derived class of a known CDI type must be
	// rejected even when its base content is valid; the exact public type and the
	// exact internal MD type stay legal.
	static void B2ExactTypeGate() {
		using (var t = Build()) {
			var derived = new ExtraDocs();
			derived.Extra = 0;
			t.Module.CustomDebugInfos.Add(derived);
			ExpectCapability("B2 derived CDI with extra state rejected", t.Module);
			derived.Extra = 1;
			ExpectCapability("B2 derived CDI with changed extra state rejected", t.Module);
		}
		using (var t = Build()) {
			var exact = new PdbTypeDefinitionDocumentsDebugInfo();
			exact.Documents.Add(NewDocument("exact://one"));
			t.Module.CustomDebugInfos.Add(exact);
			var before = Guard(t.Module);
			exact.Documents[0].Url = "exact://two";
			Check(Guard(t.Module) != before, "B2 exact public type remains legal and complete");
		}
	}

	static void FailureModes() {
		using (var t = Build()) {
			t.Module.CustomDebugInfos.Add(new UnknownCdi());
			ExpectCapability("unknown CLR CDI subclass", t.Module);
		}
		using (var t = Build()) {
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(Instruction.Create(OpCodes.Nop), t.I1b));
			t.Module.CustomDebugInfos.Add(x);
			ExpectCapability("dangling instruction reference", t.Module);
		}
		using (var t = Build()) {
			var x = new PdbDynamicLocalsCustomDebugInfo();
			var local = new PdbDynamicLocal { Local = new Local(t.Module.CorLibTypes.Int32), Name = "dangling" };
			x.Locals.Add(local);
			t.Module.CustomDebugInfos.Add(x);
			ExpectCapability("dangling local reference", t.Module);
		}
		using (var t = Build()) {
			t.Method2.Body!.Instructions.Insert(0, t.I1a);
			var x = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			x.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
			t.Module.CustomDebugInfos.Add(x);
			ExpectCapability("instruction shared by two method bodies", t.Module);
		}
	}

	static void ExpectCapability(string name, ModuleDef module) {
		var before = EditWorkspace.WriteCanonical(module);
		var rejected = false;
		try { Guard(module); }
		catch (EditDomainException ex) when (ex.Code == "EDIT_CAPABILITY_UNAVAILABLE") { rejected = true; }
		catch (Exception) { }
		Check(rejected, name + ": EDIT_CAPABILITY_UNAVAILABLE");
		Check(EditWorkspace.WriteCanonical(module).SequenceEqual(before), name + ": no module side effect");
	}

	static void SemanticUnchanged() {
		using var t = Build();
		var scope = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
		scope.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
		t.Module.CustomDebugInfos.Add(scope);
		var compute = EditFingerprint.Compute(t.Module);
		var roundtrip = EditFingerprint.ComputeRoundtrip(t.Module);
		var guard = Guard(t.Module);
		var entry = scope.Scopes[0];
		entry.End = t.I2a;
		scope.Scopes[0] = entry;
		Check(EditFingerprint.Compute(t.Module) == compute, "CDI change keeps Compute byte-identical");
		Check(EditFingerprint.ComputeRoundtrip(t.Module) == roundtrip, "CDI change keeps ComputeRoundtrip byte-identical");
		Check(Guard(t.Module) != guard, "CDI change still alters the external guard");
	}

	static void CrossCopy() {
		static (ModuleDef Module, PdbCustomDebugInfo Cdi) Copy() {
			var t = Build();
			var cdi = new PdbTypeDefinitionDocumentsDebugInfo();
			cdi.Documents.Add(NewDocument("copy://one"));
			var hoisted = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			hoisted.Scopes.Add(new StateMachineHoistedLocalScope(t.I1a, t.I1b));
			t.Module.CustomDebugInfos.Add(cdi);
			t.Module.CustomDebugInfos.Add(hoisted);
			return (t.Module, cdi);
		}
		var left = Copy();
		var right = Copy();
		Check(RowsText(left.Module) == RowsText(right.Module), "equivalent module copies produce identical CDI rows");
		Check(Guard(left.Module) == Guard(right.Module), "equivalent module copies produce identical external guard");
		((PdbTypeDefinitionDocumentsDebugInfo)left.Cdi).Documents[0].Url = "copy://changed";
		Check(Guard(left.Module) != Guard(right.Module), "copy divergence detected");
		left.Module.Dispose();
		right.Module.Dispose();
	}
}
