using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

internal static class SerializedTokenBindingProbe {
	static void Check(bool ok, string label) {
		if (!ok) throw new InvalidOperationException("FAILED: " + label);
		Console.WriteLine("PASS " + label);
	}
	static IMDTokenProvider Resolve(ModuleDef module, uint token) {
		var method = typeof(EditOperationRegistry).GetMethod("ResolveToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
		try { return (IMDTokenProvider)method.Invoke(null, new object[] { module, token })!; }
		catch (System.Reflection.TargetInvocationException error) { throw error.InnerException!; }
	}
	static bool Rejected(ModuleDef module, uint token) {
		try { Resolve(module, token); return false; }
		catch (EditDomainException) { return true; }
	}
	static bool WrongKindRejected(ModuleDef module, uint token) {
		var method = typeof(EditOperationRegistry).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			.Single(m => m.Name == "Ref" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(MethodDef));
		using var document = System.Text.Json.JsonDocument.Parse("{\"token\":\"0x" + token.ToString("x8") + "\"}");
		try {
			method.Invoke(null, new object[] { module, document.RootElement, new Dictionary<string, IMDTokenProvider>() });
			return false;
		} catch (System.Reflection.TargetInvocationException error) when (error.InnerException is EditDomainException domain) {
			return domain.Code == "EDIT_VALIDATION_FAILED";
		}
	}
	static IEnumerable<IMDTokenProvider> Definitions(ModuleDef module) {
		foreach (var type in module.GetTypes()) {
			yield return type;
			foreach (var generic in type.GenericParameters) yield return generic;
			foreach (var method in type.Methods) {
				yield return method;
				foreach (var parameter in method.ParamDefs) yield return parameter;
				foreach (var generic in method.GenericParameters) yield return generic;
			}
			foreach (var field in type.Fields) yield return field;
			foreach (var property in type.Properties) yield return property;
			foreach (var eventDef in type.Events) yield return eventDef;
		}
	}
	public static void Run(string fixture) {
		using var live = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture));
		using var other = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture));
		var owner = live.GetTypes().Single(t => t.FullName == "TestIL.Simple");
		var added = new MethodDefUser("R059Bound", MethodSig.CreateStatic(live.CorLibTypes.Int32),
			MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
		added.Body = new CilBody();
		added.Body.Instructions.Add(Instruction.CreateLdcI4(47));
		added.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		owner.Methods.Add(added);
		var overload = new MethodDefUser("R059Bound", MethodSig.CreateStatic(live.CorLibTypes.Int32, live.CorLibTypes.Int32),
			MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
		overload.Body = new CilBody();
		overload.Body.Instructions.Add(Instruction.CreateLdcI4(49));
		overload.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		owner.Methods.Add(overload);
		var addedField = new FieldDefUser("R059BoundField", new FieldSig(live.CorLibTypes.Int32), FieldAttributes.Public);
		owner.Fields.Add(addedField);
		var addedType = new TypeDefUser("TestIL", "R059BoundOwner", live.CorLibTypes.Object.TypeDefOrRef);
		live.Types.Add(addedType);
		Check(added.MDToken.Rid == 0, "live addition has unassigned RID");
		using var source = ModuleDefMD.Load(EditWorkspace.WriteCheckpointImage(live));
		var serializedOwner = source.GetTypes().Single(t => t.FullName == owner.FullName);
		var serialized = serializedOwner.Methods.Single(m => m.Name == added.Name && m.MethodSig.Params.Count == 0);
		var serializedOverload = serializedOwner.Methods.Single(m => m.Name == overload.Name && m.MethodSig.Params.Count == 1);
		var serializedField = serializedOwner.Fields.Single(f => f.Name == addedField.Name);
		var serializedType = source.GetTypes().Single(t => t.FullName == addedType.FullName);
		Check(serialized.MDToken.Rid != 0 && live.ResolveToken(serialized.MDToken.Raw) == null,
			"written addition has nonzero token absent from live graph");
		Check(new IMDTokenProvider[] { serializedOverload, serializedField, serializedType }
			.All(row => row.MDToken.Rid != 0 && live.ResolveToken(row.MDToken.Raw) == null),
			"overload field and type tokens absent from live graph");
		var checkedRows = new Dictionary<string, int>();
		foreach (var row in Definitions(source)) {
			if (row.MDToken.Rid == 0) continue;
			var direct = live.ResolveToken(row.MDToken.Raw);
			if (direct == null) continue;
			var address = EditDefinitionAddress.Capture(source, row);
			var expected = EditDefinitionAddress.Resolve(live, address);
			Check(ReferenceEquals(direct, expected), "preserved RID identity " + address);
			var kind = row.MDToken.Table.ToString();
			checkedRows[kind] = checkedRows.GetValueOrDefault(kind) + 1;
		}
		Console.WriteLine("RID_IDENTITY_COUNTS " + System.Text.Json.JsonSerializer.Serialize(checkedRows));
		Check(checkedRows.Count >= 4, "multiple metadata definition kinds inspected");
		var beforeWrongKind = EditFingerprint.Compute(live);
		Check(WrongKindRejected(live, owner.MDToken.Raw) && EditFingerprint.Compute(live) == beforeWrongKind,
			"wrong metadata kind rejected without graph mutation");
		Check(Rejected(live, serialized.MDToken.Raw), "unbound serialized token rejected");
		using (EditOperationRegistry.BindSerializedTokens(source, live)) {
			Check(ReferenceEquals(Resolve(live, serialized.MDToken.Raw), added), "bound token resolves exact live addition");
			Check(ReferenceEquals(Resolve(live, serializedOverload.MDToken.Raw), overload)
				&& ReferenceEquals(Resolve(live, serializedField.MDToken.Raw), addedField)
				&& ReferenceEquals(Resolve(live, serializedType.MDToken.Raw), addedType),
				"overload field and type bind to distinct exact definitions");
			Check(Rejected(other, serialized.MDToken.Raw), "scope cannot bind another module");
			using (EditOperationRegistry.BindSerializedTokens(other, other)) {
				Check(Rejected(other, serialized.MDToken.Raw), "different module cannot inherit outer binding");
				Check(Rejected(live, serialized.MDToken.Raw), "inner scope masks outer module");
			}
			Check(ReferenceEquals(Resolve(live, serialized.MDToken.Raw), added), "nested exit restores outer target");
			try { using var invalid = EditOperationRegistry.BindSerializedTokens(source, other); throw new InvalidOperationException("invalid graph accepted"); }
			catch (EditDomainException error) { Check(error.Code == "EDIT_HISTORY_CONFLICT", "wrong owner or missing address rejected"); }
			Check(ReferenceEquals(Resolve(live, serialized.MDToken.Raw), added), "binding construction failure preserves outer scope");
			try {
				using (EditOperationRegistry.BindSerializedTokens(source, live)) throw new InvalidOperationException("probe unwind");
			} catch (InvalidOperationException error) when (error.Message == "probe unwind") { }
			Check(ReferenceEquals(Resolve(live, serialized.MDToken.Raw), added), "exception unwind restores outer scope");
		}
		Check(Rejected(live, serialized.MDToken.Raw), "disposed outer scope cannot resolve binding");
		Console.WriteLine("PASS serialized-token-binding");
	}
}
