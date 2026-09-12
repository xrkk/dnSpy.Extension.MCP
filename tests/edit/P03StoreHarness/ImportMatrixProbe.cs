using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

// P06 --import-matrix: headless end-to-end exercise of the stable-identity
// importer against a stub compile artifact (in-memory module plus Portable-PDB
// symbol rows).  Asserts the ACC-005 core clauses: reference mapping onto
// target tokens, sequence-point/scope/local/CDI transfer, generated-subtree
// sync, the add path (generic method, property accessor), all-or-nothing
// rejection for ambiguous targets and unmapped references, and embedded-only
// symbol output.
internal static class ImportMatrixProbe {
	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var target = ModuleDefMD.Load(Path.GetFullPath(fixture));

		// ---- plain-class replace path on TestIL.Simple (real rows everywhere)
		var simple = target.GetTypes().First(t => t.FullName == "TestIL.Simple");
		var addTwo = simple.Methods.First(m => m.Name == "Add" && m.MethodSig.Params.Count == 2);
		var inc = simple.Methods.First(m => m.Name == "Inc");

		var artifact = new ModuleDefUser("ImportMatrixArtifact");
		var targetAssemblyRef = new AssemblyRefUser(target.Assembly!.Name.String, target.Assembly.Version);
		var simpleRef = new TypeRefUser(artifact, simple.Namespace, simple.Name, targetAssemblyRef);
		var artifactSimple = new TypeDefUser(simple.Namespace, simple.Name, null);
		artifact.Types.Add(artifactSimple);

		var incRef = new MemberRefUser(artifact, inc.Name, CloneSig(inc.MethodSig), simpleRef);
		var externalCtor = target.GetMemberRefs().FirstOrDefault(m => m.IsMethodRef && m.Name == ".ctor" && m.DeclaringType?.FullName == "System.Object");
		var objectRef = new TypeRefUser(artifact, "System", "Object", artifact.CorLibTypes.AssemblyRef);
		MemberRefUser? mappedExternal = externalCtor == null ? null
			: new MemberRefUser(artifact, ".ctor", CloneSig(externalCtor.MethodSig), objectRef);
		var ghostType = new TypeRefUser(artifact, "System.Probe", "GhostType", artifact.CorLibTypes.AssemblyRef);
		var ghostRef = new MemberRefUser(artifact, "Vanish", MethodSig.CreateStatic(artifact.CorLibTypes.Void), ghostType);

		var edited = new MethodDefUser(addTwo.Name, CloneSig(addTwo.MethodSig), addTwo.ImplAttributes, addTwo.Attributes);
		artifactSimple.Methods.Add(edited);
		var body = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
		var local = new Local(artifact.CorLibTypes.Int32) { Name = "probeLocal" };
		body.Variables.Add(local);
		body.Instructions.Add(Instruction.CreateLdcI4(7));
		body.Instructions.Add(Instruction.Create(OpCodes.Stloc, local));
		body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
		body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
		body.Instructions.Add(Instruction.Create(OpCodes.Add));
		body.Instructions.Add(Instruction.Create(OpCodes.Call, incRef));
		body.Instructions.Add(Instruction.Create(OpCodes.Add));
		if (mappedExternal != null) {
			body.Instructions.Add(Instruction.Create(OpCodes.Newobj, mappedExternal));
			body.Instructions.Add(Instruction.Create(OpCodes.Pop));
		}
		body.Instructions.Add(Instruction.Create(OpCodes.Ldloc, local));
		body.Instructions.Add(Instruction.Create(OpCodes.Add));
		body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		edited.Body = body;

		artifact.CreatePdbState(PdbFileKind.PortablePDB);
		var document = new PdbDocument("probe-edited.cs",
			new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1"), new Guid("994b45c4-e6e9-11d2-903f-00c04fa302a4"),
			new Guid("5a869d0b-6611-11d3-bd2a-0000f80849bd"), new Guid("8829d00f-11b8-4213-878b-770e8597ac16"),
			new byte[] { 1, 2, 3, 4 });
		artifact.PdbState!.Add(document);
		body.Instructions[0].SequencePoint = new SequencePoint { Document = document, StartLine = 3, StartColumn = 4, EndLine = 3, EndColumn = 10 };
		body.Instructions[4].SequencePoint = new SequencePoint { Document = document, StartLine = 4, StartColumn = 4, EndLine = 4, EndColumn = 20 };
		var scope = new PdbScope { Start = body.Instructions[0], End = null };
		scope.Variables.Add(new PdbLocal(local, "probeLocal", PdbLocalAttributes.None));
		scope.Constants.Add(new PdbConstant("probeConstant", artifact.CorLibTypes.Int32, 37));
		body.PdbMethod = new PdbMethod { Scope = scope };
		var hoisted = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
		hoisted.Scopes.Add(new StateMachineHoistedLocalScope(body.Instructions[0], body.Instructions[4]));
		edited.CustomDebugInfos.Add(hoisted);
		edited.CustomDebugInfos.Add(new PdbIteratorMethodCustomDebugInfo(edited));

		// new members on the mirrored plain class: generic method + property getter
		var addedGeneric = new MethodDefUser("ProbeAdded",
			MethodSig.CreateStatic(artifact.CorLibTypes.String, new GenericMVar(0)), 0, (dnlib.DotNet.MethodAttributes)0x0096);
		addedGeneric.MethodSig.GenParamCount = 1;
		addedGeneric.MethodSig.CallingConvention |= CallingConvention.Generic;
		var genericParameter = new GenericParamUser(0, (dnlib.DotNet.GenericParamAttributes)0x0004, "T");
		genericParameter.GenericParamConstraints.Add(new GenericParamConstraintUser(artifact.CorLibTypes.Object.TypeDefOrRef));
		addedGeneric.GenericParameters.Add(genericParameter);
		var addedBody = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
		addedBody.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "added"));
		addedBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
		addedGeneric.Body = addedBody;
		artifactSimple.Methods.Add(addedGeneric);
		var getter = new MethodDefUser("get_ProbeValue", MethodSig.CreateInstance(artifact.CorLibTypes.Int32),
			0, (dnlib.DotNet.MethodAttributes)0x0086);
		var getterBody = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
		getterBody.Instructions.Add(Instruction.CreateLdcI4(4));
		getterBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
		getter.Body = getterBody;
		artifactSimple.Methods.Add(getter);
		artifactSimple.Properties.Add(new PropertyDefUser("ProbeValue", PropertySig.CreateInstance(artifact.CorLibTypes.Int32)) {
			GetMethod = getter,
		});

		// generated-subtree path on the real TestIL.Machines state machines: the
		// artifact kickoff carries the [IteratorStateMachine] pointer and an
		// edited MoveNext body plus one extra hoisted field.
		var machines = target.GetTypes().First(t => t.FullName == "TestIL.Machines");
		var doCoroutine = machines.Methods.First(m => m.Name == "DoCoroutine");
		var targetState = machines.NestedTypes.First(t => t.Name.String.StartsWith("<DoCoroutine>", StringComparison.Ordinal));
		var targetMoveNext = targetState.Methods.First(m => m.Name == "MoveNext");
		var stateFieldType = targetState.Fields.First(f => f.Name == "<>1__state").FieldType;

		var artifactMachines = new TypeDefUser(machines.Namespace, machines.Name, null);
		artifact.Types.Add(artifactMachines);
		var kickoffBodies = new List<MethodDefUser>();
		var artifactKickoff = new MethodDefUser(doCoroutine.Name, CloneSig(doCoroutine.MethodSig), doCoroutine.ImplAttributes, doCoroutine.Attributes);
		artifactMachines.Methods.Add(artifactKickoff);
		kickoffBodies.Add(artifactKickoff);
		var artifactState = new TypeDefUser(string.Empty, targetState.Name.String,
			new TypeRefUser(artifact, "System.Runtime.CompilerServices", "ValueType", artifact.CorLibTypes.AssemblyRef)) {
			Attributes = targetState.Attributes,
		};
		artifactMachines.NestedTypes.Add(artifactState);
		var stateFieldOld = new FieldDefUser("<>1__state", new FieldSig(stateFieldType), (dnlib.DotNet.FieldAttributes)0x0006);
		var stateFieldNew = new FieldDefUser("<>probeExtra", new FieldSig(stateFieldType), (dnlib.DotNet.FieldAttributes)0x0006);
		artifactState.Fields.Add(stateFieldOld);
		artifactState.Fields.Add(stateFieldNew);
		var artifactCtor = new MethodDefUser(".ctor", MethodSig.CreateInstance(artifact.CorLibTypes.Void, artifact.CorLibTypes.Int32),
			0, (dnlib.DotNet.MethodAttributes)0x1886);
		artifactState.Methods.Add(artifactCtor);
		var artifactMoveNext = new MethodDefUser("MoveNext", MethodSig.CreateInstance(targetMoveNext.MethodSig.RetType), 0, (dnlib.DotNet.MethodAttributes)0x0086);
		var moveNextBody = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
		moveNextBody.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
		moveNextBody.Instructions.Add(Instruction.CreateLdcI4(1));
		moveNextBody.Instructions.Add(Instruction.Create(OpCodes.Stfld, stateFieldNew));
		moveNextBody.Instructions.Add(Instruction.CreateLdcI4(0));
		moveNextBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
		artifactMoveNext.Body = moveNextBody;
		artifactState.Methods.Add(artifactMoveNext);
		foreach (var kickoff in kickoffBodies) {
			var kickoffBody = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
			kickoffBody.Instructions.Add(Instruction.CreateLdcI4(0));
			kickoffBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, artifactCtor));
			kickoffBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
			kickoff.Body = kickoffBody;
		}
		var machinesRef = new TypeRefUser(artifact, machines.Namespace, machines.Name, targetAssemblyRef);
		var attributeType = new TypeRefUser(artifact, "System.Runtime.CompilerServices", "IteratorStateMachineAttribute", artifact.CorLibTypes.AssemblyRef);
		var attributeCtor = new MemberRefUser(artifact, ".ctor",
			MethodSig.CreateInstance(artifact.CorLibTypes.Void, new ClassSig(attributeType)), attributeType);
		artifactKickoff.CustomAttributes.Add(new CustomAttribute(attributeCtor, new[] {
			new CAArgument(new ClassSig(attributeType), artifactState) }));
		// ghost member for the unresolved-reference rows
		var ghostBody = new CilBody(true, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>()) { MaxStack = 8 };
		ghostBody.Instructions.Add(Instruction.Create(OpCodes.Call, ghostRef));
		ghostBody.Instructions.Add(Instruction.Create(OpCodes.Ret));
		artifactSimple.Methods.Add(new MethodDefUser("ProbeGhost", MethodSig.CreateStatic(artifact.CorLibTypes.Void),
			0, (dnlib.DotNet.MethodAttributes)0x0096) { Body = ghostBody });

		var baselineFingerprint = EditFingerprint.Compute(target);

		// ---- 1) plain replace + generated subtree + adds in one import
		var replaceRow = "TestIL.Simple::Add(System.Int32,System.Int32)";
		var kickoffRow = "TestIL.Machines::DoCoroutine()";
		using (var importer = new EditCSharpImporter(artifact, target, new Dictionary<string, IMDTokenProvider>(), 0)) {
			var plan = importer.Compile(ParseTargets(
				(replaceRow, "replace_body"),
				(kickoffRow, "replace_body"),
				("TestIL.Simple::ProbeAdded`1(!!0)", "add"),
				("TestIL.Simple::get_ProbeValue()", "add")));
			Apply(target, plan);
			var kinds = string.Join(",", plan.Select(row => row.Kind));
			foreach (var expected in new[] { "method_body_replace", "field_add", "method_add", "property_add" })
				if (!kinds.Contains(expected))
					throw new InvalidOperationException("import matrix lost operation kind " + expected + ": " + kinds);
			var replaced = simple.Methods.First(m => m == addTwo);
			if (!replaced.Body.Instructions.Any(i => i.Operand is MethodDef called && called == inc))
				throw new InvalidOperationException("the target-assembly reference did not map to the target definition");
			if (mappedExternal != null && !replaced.Body.Instructions.Any(i => i.Operand is MemberRef referenced && referenced.Name == ".ctor"))
				throw new InvalidOperationException("the external member reference did not map to a target row");
			var points = replaced.Body.Instructions.Where(i => i.SequencePoint?.Document?.Url == "probe-edited.cs").ToArray();
			if (points.Length != 2 || !ReferenceEquals(points[0].SequencePoint.Document, points[1].SequencePoint.Document))
				throw new InvalidOperationException("sequence points did not land with a shared document");
			var pdbScope = replaced.Body.PdbMethod?.Scope;
			if (pdbScope == null || !pdbScope.Variables.Any(v => v.Name == "probeLocal")
				|| !pdbScope.Constants.Any(c => c.Name == "probeConstant" && Equals(c.Value, 37)))
				throw new InvalidOperationException("scope/local/constant rows did not land");
			if (!replaced.CustomDebugInfos.Any(c => c.Kind == PdbCustomDebugInfoKind.StateMachineHoistedLocalScopes)
				|| !replaced.CustomDebugInfos.Any(c => c.Kind == PdbCustomDebugInfoKind.IteratorMethod))
				throw new InvalidOperationException("method custom debug info did not land");
			if (!targetState.Fields.Any(f => f.Name == "<>probeExtra"))
				throw new InvalidOperationException("generated subtree field sync failed");
			if (targetMoveNext.Body!.Instructions.Count != 5)
				throw new InvalidOperationException("generated subtree body sync failed");
			var added = simple.Methods.FirstOrDefault(m => m.Name == "ProbeAdded");
			if (added == null || added.GenericParameters.Count != 1 || added.Body == null)
				throw new InvalidOperationException("generic method add failed");
			var addedProperty = simple.Properties.FirstOrDefault(p => p.Name == "ProbeValue");
			if (addedProperty == null || addedProperty.GetMethod == null || addedProperty.SetMethod != null)
				throw new InvalidOperationException("property accessor add failed");
		}
		if (EditFingerprint.Compute(target) == baselineFingerprint)
			throw new InvalidOperationException("import produced no semantic change");

		// ---- 2) embedded symbol write: reload keeps points/kind, no standalone pdb
		var bytes = EditWorkspace.WriteCanonical(target);
		var directory = Path.Combine(Path.GetTempPath(), "p06-import-matrix-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		File.WriteAllBytes(Path.Combine(directory, "probe.dll"), bytes);
		if (Directory.GetFiles(directory).Length != 1)
			throw new InvalidOperationException("import write produced files besides the module");
		using (var reloaded = ModuleDefMD.Load(Path.Combine(directory, "probe.dll"), new ModuleCreationOptions { TryToLoadPdbFromDisk = true })) {
			if (reloaded.PdbState?.PdbFileKind != PdbFileKind.EmbeddedPortablePDB)
				throw new InvalidOperationException("reloaded symbol kind is not embedded: " + reloaded.PdbState?.PdbFileKind);
			var reloadPoints = reloaded.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
				.SelectMany(m => m.Body.Instructions).Where(i => i.SequencePoint?.Document?.Url == "probe-edited.cs").ToArray();
			if (reloadPoints.Length != 2) throw new InvalidOperationException("embedded points lost after reload: " + reloadPoints.Length);
			var reloadMethod = reloaded.GetTypes().SelectMany(t => t.Methods)
				.FirstOrDefault(m => m.Body != null && m.Body.Instructions.Any(i => i.SequencePoint?.Document?.Url == "probe-edited.cs"));
			if (reloadMethod == null || !reloadMethod.CustomDebugInfos.Any(c => c.Kind == PdbCustomDebugInfoKind.StateMachineHoistedLocalScopes))
				throw new InvalidOperationException("embedded custom debug info lost after reload");
			var scopes = new List<PdbScope>();
			void Walk(PdbScope current) { scopes.Add(current); foreach (var child in current.Scopes) Walk(child); }
			if (reloadMethod.Body.PdbMethod?.Scope != null) Walk(reloadMethod.Body.PdbMethod.Scope);
			if (!scopes.SelectMany(s => s.Variables).Any(v => v.Name == "probeLocal")
				|| !scopes.SelectMany(s => s.Constants).Any(c => c.Name == "probeConstant" && Equals(c.Value, 37)))
				throw new InvalidOperationException("embedded scope rows lost after reload");
		}
		Directory.Delete(directory, true);

		// ---- 3) ambiguous target: duplicate shape -> zero side effects
		var twin = new MethodDefUser(addTwo.Name, CloneSig(addTwo.MethodSig), addTwo.ImplAttributes, addTwo.Attributes);
		simple.Methods.Add(twin);
		ExpectRejection(artifact, target, replaceRow, "the ambiguous target was accepted");
		simple.Methods.Remove(twin);

		// ---- 4) unmapped reference: all-or-nothing across rows (both orders)
		var rejectionFingerprint = EditFingerprint.Compute(target);
		ExpectRejection(artifact, target, replaceRow, "TestIL.Simple::ProbeGhost()",
			"the unmapped reference row was accepted");
		ExpectRejection(artifact, target, "TestIL.Simple::ProbeGhost()", replaceRow,
			"the unmapped reference row was accepted when it came first");
		if (EditFingerprint.Compute(target) != rejectionFingerprint)
			throw new InvalidOperationException("a rejected import changed the target");

		Console.WriteLine("PASS import-matrix target-assembly+external-refs=True sequence-points=2 shared-document=True "
			+ "scope+local+constant=True cdi=hoisted+iterator=True subtree-sync=field+body=True "
			+ "add=generic+constraint+property-getter=True embedded-reload=True standalone-pdb=False "
			+ "ambiguity-reject-zero-side-effect=True unresolved-reject-all-or-nothing=True");
	}

	static MethodSig CloneSig(MethodSig source) {
		var clone = source.HasThis
			? MethodSig.CreateInstance(source.RetType, source.Params.ToArray())
			: MethodSig.CreateStatic(source.RetType, source.Params.ToArray());
		clone.CallingConvention = source.CallingConvention;
		clone.GenParamCount = source.GenParamCount;
		return clone;
	}

	static JsonElement ParseTargets(params (string compiled, string action)[] rows) {
		var array = rows.Select(row => (object)new Dictionary<string, object?> {
			["compiled"] = row.compiled, ["action"] = row.action }).ToArray();
		return JsonDocument.Parse(JsonSerializer.Serialize(array)).RootElement.Clone();
	}

	static void Apply(ModuleDef target, IReadOnlyList<EditCSharpImporter.PlanRow> plan) {
		var objects = new Dictionary<string, IMDTokenProvider>();
		for (var index = 0; index < plan.Count; index++) {
			var json = EditWire.CanonicalPayload(plan[index].Operation);
			try {
				using var document = JsonDocument.Parse(json);
				EditOperationRegistry.ApplyPersisted(target, document.RootElement, objects, index);
				EditStructuralValidator.Validate(target);
			}
			catch (Exception ex) {
				var detail = ex is EditDomainException domain ? EditWire.CanonicalPayload(domain.Details) : ex.ToString();
				Console.Error.WriteLine("PROBE-OP-FAIL index=" + index + " detail=" + detail.Substring(0, Math.Min(500, detail.Length))
					+ " json=" + json.Substring(0, Math.Min(700, json.Length)));
				throw;
			}
		}
	}

	static void ExpectRejection(ModuleDef artifact, ModuleDef target, params string[] tail) {
		var message = tail[^1];
		var rows = tail[..^1].Select(compiled => (compiled, "replace_body")).ToArray();
		using var importer = new EditCSharpImporter(artifact, target, new Dictionary<string, IMDTokenProvider>(), 0);
		try {
			importer.Compile(ParseTargets(rows));
			throw new InvalidOperationException(message);
		}
		catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
	}
}
