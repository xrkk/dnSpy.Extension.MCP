using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>Hard validation that runs before a private revision can be reviewed or replayed live.</summary>
internal static class EditStructuralValidator {
	public static int Validate(ModuleDef module) {
		var rules = 0;
		foreach (var type in module.GetTypes()) {
			// P03-CHANGE-002 v3 / AUD-106 registered exemption: the deleted-rows
			// tombstone intentionally hosts ParamDef rows behind a zero-parameter
			// void() signature while a deletion is active. It is writer bookkeeping
			// excluded from semantic fingerprints, not sample metadata to validate.
			if (EditDeletedRowsTombstone.IsTombstone(type)) continue;
			rules++;
			if (type.DeclaringType == null && !module.Types.Contains(type)) Fail("owner", type.FullName, "Top-level type is not owned by the module");
			if (type.DeclaringType != null && !type.DeclaringType.NestedTypes.Contains(type)) Fail("owner", type.FullName, "Nested type owner is inconsistent");
			ValidateGeneric(type.GenericParameters, "type:" + type.FullName, ref rules);
			foreach (var field in type.Fields) { rules++; if (!ReferenceEquals(field.DeclaringType, type)) Fail("owner", field.FullName, "Field owner is inconsistent"); }
			foreach (var method in type.Methods) {
				rules++;
				if (!ReferenceEquals(method.DeclaringType, type)) Fail("owner", method.FullName, "Method owner is inconsistent");
				ValidateMethod(method, ref rules);
			}
			foreach (var property in type.Properties) ValidateProperty(type, property, ref rules);
			foreach (var evt in type.Events) ValidateEvent(type, evt, ref rules);
		}
		return rules;
	}

	static void ValidateMethod(MethodDef method, ref int rules) {
		ValidateGeneric(method.GenericParameters, "method:" + method.FullName, ref rules);
		rules++;
		var signatureGeneric = (method.MethodSig.CallingConvention & CallingConvention.Generic) != 0;
		if (method.MethodSig.GenParamCount != method.GenericParameters.Count ||
			signatureGeneric != (method.GenericParameters.Count != 0))
			Fail("generic_signature", method.FullName, "Method generic parameters, signature count, and Generic calling-convention flag must agree");
		var seen = new HashSet<ushort>();
		foreach (var param in method.ParamDefs) {
			rules++;
			if (!seen.Add(param.Sequence)) Fail("param_sequence", method.FullName, "Duplicate ParamDef sequence");
			if (param.Sequence > method.MethodSig.Params.Count) Fail("param_sequence", method.FullName, "ParamDef sequence is outside MethodSig");
		}
		if (method.HasBody) ValidateBody(method, ref rules);
	}

	static void ValidateGeneric(IList<GenericParam> parameters, string location, ref int rules) {
		for (var i = 0; i < parameters.Count; i++) {
			rules++;
			if (parameters[i].Number != i) Fail("generic_number", location, "GenericParam numbers must be contiguous and owner ordered");
		}
	}

	static void ValidateProperty(TypeDef owner, PropertyDef property, ref int rules) {
		rules++;
		if (!ReferenceEquals(property.DeclaringType, owner)) Fail("owner", property.FullName, "Property owner is inconsistent");
		if (property.GetMethod != null) {
			if (!ReferenceEquals(property.GetMethod.DeclaringType, owner)) Fail("accessor_owner", property.FullName, "Getter belongs to another type");
			var sig = property.GetMethod.MethodSig;
			if (sig == null || sig.Params.Count != property.PropertySig.Params.Count || sig.RetType.FullName != property.PropertySig.RetType.FullName ||
				sig.Params.Where((p,i)=>p.FullName!=property.PropertySig.Params[i].FullName).Any())
				Fail("accessor_signature", property.FullName, "Getter signature does not match property signature");
		}
		if (property.SetMethod != null) {
			if (!ReferenceEquals(property.SetMethod.DeclaringType, owner)) Fail("accessor_owner", property.FullName, "Setter belongs to another type");
			var sig = property.SetMethod.MethodSig;
			if (sig == null || sig.RetType.ElementType != ElementType.Void || sig.Params.Count != property.PropertySig.Params.Count + 1 || sig.Params.Last().FullName != property.PropertySig.RetType.FullName ||
				sig.Params.Take(sig.Params.Count-1).Where((p,i)=>p.FullName!=property.PropertySig.Params[i].FullName).Any())
				Fail("accessor_signature", property.FullName, "Setter signature does not match property signature");
		}
	}

	static void ValidateEvent(TypeDef owner, EventDef evt, ref int rules) {
		rules++;
		if (!ReferenceEquals(evt.DeclaringType, owner)) Fail("owner", evt.FullName, "Event owner is inconsistent");
		if (evt.AddMethod == null || evt.RemoveMethod == null) Fail("event_semantics", evt.FullName, "Event requires add and remove methods");
		foreach (var method in new[] { evt.AddMethod, evt.RemoveMethod, evt.InvokeMethod }.Where(m => m != null)) {
			if (!ReferenceEquals(method!.DeclaringType, owner)) Fail("accessor_owner", evt.FullName, "Event accessor belongs to another type");
		}
		foreach(var method in new[]{evt.AddMethod,evt.RemoveMethod}.Where(m=>m!=null))if(method!.MethodSig==null||method.MethodSig.RetType.ElementType!=ElementType.Void||method.MethodSig.Params.Count!=1||method.MethodSig.Params[0].FullName!=evt.EventType.FullName)Fail("accessor_signature",evt.FullName,"Event add/remove signature does not match event type");
	}

	static void ValidateBody(MethodDef method, ref int rules) {
		var body = method.Body;
		if (body.Instructions.Count == 0) Fail("cfg", method.FullName, "Method body has no instructions");
		var set = new HashSet<Instruction>(body.Instructions);
		foreach (var instruction in body.Instructions) {
			rules++;
			if (instruction.Operand is Instruction target && !set.Contains(target)) Fail("branch_target", method.FullName, "Branch target is outside the body");
			if (instruction.Operand is IList<Instruction> targets && targets.Any(t => !set.Contains(t))) Fail("branch_target", method.FullName, "Switch target is outside the body");
		}
		ValidatePrefixes(method);
		ValidateExceptionHandlers(method, set);
		ValidateProtectedRegionBranches(method);
		ValidateStack(method);
	}

	static void ValidatePrefixes(MethodDef method) {
		var instructions = method.Body.Instructions;
		for (var i = 0; i < instructions.Count; i++) {
			var code = instructions[i].OpCode.Code;
			if (code is Code.Prefix1 or Code.Prefix2 or Code.Prefix3 or Code.Prefix4 or Code.Prefix5 or Code.Prefix6 or Code.Prefix7 or Code.Prefixref)
				Fail("prefix", method.FullName, "Reserved IL prefix is not legal");
			if (code is not (Code.Tailcall or Code.Constrained or Code.Readonly or Code.Volatile or Code.Unaligned or Code.No)) continue;
			if (i + 1 >= instructions.Count) Fail("prefix", method.FullName, "IL prefix cannot terminate a body");
			var next = instructions[i + 1].OpCode.Code;
			if (code == Code.Tailcall && next is not (Code.Call or Code.Calli or Code.Callvirt)) Fail("prefix", method.FullName, "tail. must precede call/calli/callvirt");
			if (code == Code.Constrained && next != Code.Callvirt) Fail("prefix", method.FullName, "constrained. must precede callvirt");
			if (code == Code.Readonly && next != Code.Ldelema) Fail("prefix", method.FullName, "readonly. must precede ldelema");
			if(code==Code.Unaligned){var alignment=Convert.ToInt32(instructions[i].Operand);if(alignment is not (1 or 2 or 4))Fail("prefix",method.FullName,"unaligned. requires alignment 1, 2, or 4");if(next is not (Code.Ldind_I1 or Code.Ldind_U1 or Code.Ldind_I2 or Code.Ldind_U2 or Code.Ldind_I4 or Code.Ldind_U4 or Code.Ldind_I8 or Code.Ldind_I or Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_Ref or Code.Stind_Ref or Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Ldfld or Code.Stfld or Code.Ldobj or Code.Stobj or Code.Initblk or Code.Cpblk))Fail("prefix",method.FullName,"unaligned. precedes only an indirect memory operation");}
			if(code==Code.Volatile&&next is not (Code.Ldind_I1 or Code.Ldind_U1 or Code.Ldind_I2 or Code.Ldind_U2 or Code.Ldind_I4 or Code.Ldind_U4 or Code.Ldind_I8 or Code.Ldind_I or Code.Ldind_R4 or Code.Ldind_R8 or Code.Ldind_Ref or Code.Stind_Ref or Code.Stind_I1 or Code.Stind_I2 or Code.Stind_I4 or Code.Stind_I8 or Code.Stind_R4 or Code.Stind_R8 or Code.Ldfld or Code.Stfld or Code.Ldsfld or Code.Stsfld or Code.Ldobj or Code.Stobj or Code.Initblk or Code.Cpblk))Fail("prefix",method.FullName,"volatile. precedes only a supported memory operation");
			if(code==Code.No){var checks=Convert.ToInt32(instructions[i].Operand);if(checks<1||(checks&~7)!=0)Fail("prefix",method.FullName,"no. requires a non-empty check mask");if(next is not (Code.Callvirt or Code.Ldelema or Code.Ldelem_I1 or Code.Ldelem_U1 or Code.Ldelem_I2 or Code.Ldelem_U2 or Code.Ldelem_I4 or Code.Ldelem_U4 or Code.Ldelem_I8 or Code.Ldelem_I or Code.Ldelem_R4 or Code.Ldelem_R8 or Code.Ldelem_Ref or Code.Ldelem or Code.Stelem_I or Code.Stelem_I1 or Code.Stelem_I2 or Code.Stelem_I4 or Code.Stelem_I8 or Code.Stelem_R4 or Code.Stelem_R8 or Code.Stelem_Ref or Code.Stelem))Fail("prefix",method.FullName,"no. precedes only callvirt or an array element operation");}
		}
	}

	static void ValidateExceptionHandlers(MethodDef method, HashSet<Instruction> instructions) {
		foreach (var eh in method.Body.ExceptionHandlers) {
			if (!instructions.Contains(eh.TryStart) || !instructions.Contains(eh.HandlerStart)) Fail("exception_handler", method.FullName, "EH start is outside the body");
			if (eh.TryEnd != null && !instructions.Contains(eh.TryEnd)) Fail("exception_handler", method.FullName, "EH try end is outside the body");
			if (eh.HandlerEnd != null && !instructions.Contains(eh.HandlerEnd)) Fail("exception_handler", method.FullName, "EH handler end is outside the body");
			if (eh.HandlerType == ExceptionHandlerType.Filter && (eh.FilterStart == null || !instructions.Contains(eh.FilterStart))) Fail("exception_handler", method.FullName, "Filter handler requires an in-body filter start");
			if (eh.HandlerType != ExceptionHandlerType.Filter && eh.FilterStart != null) Fail("exception_handler", method.FullName, "Only filter handlers can specify filter_start");
			if (eh.HandlerType == ExceptionHandlerType.Catch && eh.CatchType == null) Fail("exception_handler", method.FullName, "Catch handler requires catch_type");
			if (eh.HandlerType != ExceptionHandlerType.Catch && eh.CatchType != null) Fail("exception_handler", method.FullName, "Only catch handlers can specify catch_type");
			var ts=Index(method.Body,eh.TryStart);var te=Index(method.Body,eh.TryEnd);var hs=Index(method.Body,eh.HandlerStart);var he=Index(method.Body,eh.HandlerEnd);
			if(ts<0||te<=ts||hs<0||he<=hs)Fail("exception_handler",method.FullName,"Exception-handler ranges must be non-empty and ordered");
			if(eh.FilterStart!=null){var fs=Index(method.Body,eh.FilterStart);if(fs<0||fs>=hs)Fail("exception_handler",method.FullName,"Filter range must end at handler_start");}
		}
		var rows=method.Body.ExceptionHandlers.Select(e=>new{Handler=e,TryStart=Index(method.Body,e.TryStart),TryEnd=Index(method.Body,e.TryEnd),HandlerStart=Index(method.Body,e.HandlerStart),HandlerEnd=Index(method.Body,e.HandlerEnd)}).ToList();
		foreach(var a in rows)foreach(var b in rows)if(!ReferenceEquals(a,b)){
			if(Crosses(a.TryStart,a.TryEnd,b.TryStart,b.TryEnd)||Crosses(a.HandlerStart,a.HandlerEnd,b.HandlerStart,b.HandlerEnd))Fail("exception_handler",method.FullName,"Exception-handler regions must be disjoint or nested");
		}
	}

	static void ValidateProtectedRegionBranches(MethodDef method){var body=method.Body;foreach(var instruction in body.Instructions){var source=body.Instructions.IndexOf(instruction);IEnumerable<Instruction> targets=instruction.Operand is Instruction one?new[]{one}:instruction.Operand is IList<Instruction> many?many:Array.Empty<Instruction>();foreach(var target in targets){var destination=body.Instructions.IndexOf(target);foreach(var eh in body.ExceptionHandlers){var hs=Index(body,eh.HandlerStart);var he=Index(body,eh.HandlerEnd);if(destination>=hs&&destination<he&&!(source>=hs&&source<he))Fail("branch_handler",method.FullName,"Branch enters an exception handler from outside");if(eh.FilterStart!=null){var fs=Index(body,eh.FilterStart);if(destination>=fs&&destination<hs&&!(source>=fs&&source<hs))Fail("branch_filter",method.FullName,"Branch enters a filter from outside");}}}}}
	static int Index(CilBody body,Instruction? instruction)=>instruction==null?body.Instructions.Count:body.Instructions.IndexOf(instruction);
	static bool Crosses(int a0,int a1,int b0,int b1)=>a0<b0&&b0<a1&&a1<b1;

	static void ValidateStack(MethodDef method) {
		var body = method.Body;
		var depths = new Dictionary<Instruction, int>();
		var queue = new Queue<Instruction>();
		Seed(body.Instructions[0], 0);
		foreach (var eh in body.ExceptionHandlers) {
			Seed(eh.HandlerStart, eh.HandlerType == ExceptionHandlerType.Catch ? 1 : 0);
			if (eh.FilterStart != null) Seed(eh.FilterStart, 1);
		}
		while (queue.Count != 0) {
			var instruction = queue.Dequeue();
			var depth = depths[instruction];
			var pop = PopCount(method, instruction, depth);
			if (depth < pop) Fail("stack_underflow", method.FullName, "Evaluation stack underflow at " + instruction.Offset);
			var nextDepth = depth - pop + PushCount(instruction);
			if(instruction.OpCode.Code==Code.Ret&&nextDepth!=0)Fail("ret_stack",method.FullName,"ret must consume the complete evaluation stack");
			if(instruction.OpCode.Code==Code.Throw&&depth!=1)Fail("throw_stack",method.FullName,"throw requires exactly one stack value");
			if (nextDepth > body.MaxStack) Fail("max_stack", method.FullName, "Declared MaxStack is too small");
			foreach (var successor in Successors(body.Instructions, instruction)) Seed(successor, instruction.OpCode.Code == Code.Leave || instruction.OpCode.Code == Code.Leave_S ? 0 : nextDepth);
		}
		void Seed(Instruction instruction, int depth) {
			if (depths.TryGetValue(instruction, out var prior)) { if (prior != depth) Fail("stack_merge", method.FullName, "Control-flow stack depths disagree"); return; }
			depths[instruction] = depth; queue.Enqueue(instruction);
		}
	}

	static IEnumerable<Instruction> Successors(IList<Instruction> instructions, Instruction instruction) {
		var index = instructions.IndexOf(instruction);
		if (instruction.Operand is Instruction target) yield return target;
		if (instruction.Operand is IList<Instruction> targets) foreach (var item in targets) yield return item;
		if (instruction.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw or FlowControl.Branch) yield break;
		if (index + 1 < instructions.Count) yield return instructions[index + 1];
	}

	static int PopCount(MethodDef owner, Instruction instruction, int depth) => instruction.OpCode.StackBehaviourPop switch {
		StackBehaviour.Pop0 => 0,
		StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
		StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
		StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
		StackBehaviour.PopAll => depth,
		StackBehaviour.Varpop => VariablePop(owner, instruction),
		_ => 0,
	};

	static int VariablePop(MethodDef owner, Instruction instruction) {
		if (instruction.OpCode.Code == Code.Ret) return owner.ReturnType.ElementType == ElementType.Void ? 0 : 1;
		if (instruction.Operand is IMethod method && method.MethodSig != null) return method.MethodSig.Params.Count + ((method.MethodSig.HasThis && instruction.OpCode.Code != Code.Newobj) ? 1 : 0);
		return 0;
	}

	static int PushCount(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch {
		StackBehaviour.Push0 => 0,
		StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
		StackBehaviour.Push1_push1 => 2,
		StackBehaviour.Varpush => instruction.OpCode.Code == Code.Newobj ? 1 : instruction.Operand is IMethod method && method.MethodSig?.RetType.ElementType != ElementType.Void ? 1 : 0,
		_ => 0,
	};

	static void Fail(string rule, string location, string message) =>
		throw new EditDomainException("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails(rule, location, message));
}
