using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;

// T003 shared canonical fixture: rich enough to exercise every method-owned
// row kind (method/mgp/param/body/local/il/sp/eh), type-owned rows (tgp/interface)
// and legal overload variants, while staying deterministic for old/new vector
// comparison.  The fixed MVID and source shape are what make the historical
// fingerprint vectors in OwnerVersionProbe.cs reproducible on a clean checkout.
internal static class OwnerVersionCanonical {
	public static ModuleDef Build() {
		var module = new ModuleDefUser("T003Canonical.dll") { Kind = ModuleKind.Dll, Mvid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };
		new AssemblyDefUser("T003Canonical", new Version(1, 0)).Modules.Add(module);
		var t = new TypeDefUser("N", "T", module.CorLibTypes.Object.TypeDefOrRef);
		var t2 = new TypeDefUser("N", "T2", module.CorLibTypes.Object.TypeDefOrRef);
		module.Types.Add(t);
		module.Types.Add(t2);
		AddValue(t, "One", 1, module.CorLibTypes.Int32, null);
		AddValue(t, "Two", 2, module.CorLibTypes.Int32, null);
		AddValue(t, "One", 5, null, module.CorLibTypes.Int32);
		AddValue(t2, "One", 7, module.CorLibTypes.String, null);
		AddGeneric(t, "G1", "TP1");
		AddGeneric(t, "G2", "TP2");
		AddSequencePoints(t, "WithSp");
		t.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "TP1"));
		t2.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, "TP2"));
		t.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(module, "N", "IF1", new AssemblyRefUser(new AssemblyNameInfo("A, Version=1.0.0.0")))));
		t2.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(module, "N", "IF2", new AssemblyRefUser(new AssemblyNameInfo("B, Version=1.0.0.0")))));
		return module;
	}

	static MethodDef AddValue(TypeDef type, string name, int value, TypeSig? localType, TypeSig? extraParam) {
		var module = type.Module;
		var sig = extraParam == null ? MethodSig.CreateStatic(module.CorLibTypes.Int32) : MethodSig.CreateStatic(module.CorLibTypes.Int32, extraParam);
		var method = new MethodDefUser(name, sig, MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		if (localType != null) method.Body.Variables.Add(new Local(localType));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, value));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}

	static MethodDef AddGeneric(TypeDef type, string name, string genericName) {
		var module = type.Module;
		var sig = MethodSig.CreateStatic(module.CorLibTypes.Void);
		sig.GenParamCount = 1;
		var method = new MethodDefUser(name, sig, MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		method.GenericParameters.Add(new GenericParamUser(0, (GenericParamAttributes)0, genericName));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Nop));
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		return method;
	}

	static MethodDef AddSequencePoints(TypeDef type, string name) {
		var module = type.Module;
		var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody { MaxStack = 8 } };
		type.Methods.Add(method);
		var first = Instruction.Create(OpCodes.Ldc_I4_1);
		var second = Instruction.Create(OpCodes.Ldc_I4_2);
		method.Body.Instructions.Add(first);
		method.Body.Instructions.Add(second);
		method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
		first.SequencePoint = new SequencePoint {
			Document = new PdbDocument("doc://a.cs", Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new byte[] { 1 }),
			StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2,
		};
		second.SequencePoint = new SequencePoint {
			Document = new PdbDocument("doc://b.cs", Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, new byte[] { 2 }),
			StartLine = 2, StartColumn = 1, EndLine = 2, EndColumn = 2,
		};
		return method;
	}
}
