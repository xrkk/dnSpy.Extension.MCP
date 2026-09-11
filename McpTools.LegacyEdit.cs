using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;

namespace dnSpy.Extension.MCP {
	sealed partial class McpTools {
		internal uint ResolveLegacyMethodToken(Dictionary<string, object>? arguments, EditWorkspace workspace) {
			if (arguments == null) throw new ArgumentException("Arguments required");
			var typeName = ReadOptionalString(arguments, "type_full_name") ?? throw new ArgumentException("type_full_name is required");
			var methodName = ReadOptionalString(arguments, "method_name") ?? throw new ArgumentException("method_name is required");
			var type = workspace.PrivateModule.GetTypes().SingleOrDefault(x => x.FullName == typeName)
				?? throw new ArgumentException("Type not found: " + typeName);
			return FindMethod(type, methodName, ReadStringArray(arguments, "parameter_types"),
				ReadOptionalUInt(arguments, "method_token")).MDToken.Raw;
		}

		internal LegacyEditPlan BuildLegacyMethodPlan(string toolName, Dictionary<string, object>? arguments, EditWorkspace workspace) {
			if (arguments == null) throw new ArgumentException("Arguments required");
			var typeName = ReadOptionalString(arguments, "type_full_name") ?? throw new ArgumentException("type_full_name is required");
			var methodName = ReadOptionalString(arguments, "method_name") ?? throw new ArgumentException("method_name is required");
			var parameterTypes = ReadStringArray(arguments, "parameter_types");
			var methodToken = ReadOptionalUInt(arguments, "method_token");
			using var module = ModuleDefMD.Load(workspace.SnapshotPrivate());
			var type = module.GetTypes().SingleOrDefault(x => x.FullName == typeName) ?? throw new ArgumentException("Type not found: " + typeName);
			var method = FindMethod(type, methodName, parameterTypes, methodToken);
			if (!method.HasBody || method.Body == null) throw new ArgumentException("Method has no IL body");
			string? returnBehavior = null;
			if (toolName == "patch_method_il") ApplyLegacyPatch(arguments, module, method);
			else if (toolName is "force_return" or "nop_method") returnBehavior = ApplyLegacyReturn(arguments, method, toolName == "nop_method");
			else throw new ArgumentException("Unsupported legacy method edit: " + toolName);

			var emitted = EditWorkspace.Write(module);
			using var normalized = ModuleDefMD.Load(emitted);
			var normalizedMethod = normalized.ResolveToken(method.MDToken.Raw) as MethodDef ?? throw new ArgumentException("Method token was not preserved");
			var plan = new LegacyEditPlan();
			plan.Operations.Add(new Dictionary<string, object?> {
				["kind"] = "method_body_replace",
				["target"] = new Dictionary<string, object?> { ["token"] = Token(method.MDToken.Raw) },
				["body"] = StructuredBody(normalizedMethod),
			});
			foreach (var field in SerializeBody(normalizedMethod, normalizedMethod.Body)) plan.Projection[field.Key] = field.Value;
			plan.Projection[toolName == "patch_method_il" ? "edits_applied" : toolName == "nop_method" ? "nopped" : "forced_return"] =
				toolName == "patch_method_il" ? ParseEditsList(arguments["edits"]).Count : true;
			if (returnBehavior != null) plan.Projection["return_behavior"] = returnBehavior;
			plan.Projection["method_token"] = method.MDToken.Raw;
			plan.Projection["method_token_hex"] = Token(method.MDToken.Raw);
			// This projection is returned only after the coordinator commits this head.
			plan.Projection["has_pending_patch"] = true;
			return plan;
		}

		void ApplyLegacyPatch(Dictionary<string, object> arguments, ModuleDef module, MethodDef method) {
			if (!arguments.TryGetValue("edits", out var raw)) throw new ArgumentException("edits is required");
			var edits = ParseEditsList(raw); var body = method.Body; var original = body.Instructions.ToList();
			var importer = new Importer(module, ImporterOptions.TryToUseDefs);
			var resolved = edits.Select(x => ResolveEdit(x, original, body, method, importer)).ToList();
			foreach (var edit in resolved) switch (edit.Op) {
			case "replace": edit.Target!.OpCode = edit.NewOpCode!; edit.Target.Operand = edit.NewOperand; break;
			case "insert": var created = new Instruction(edit.NewOpCode!) { Operand = edit.NewOperand };
				if (edit.Target == null) body.Instructions.Add(created); else body.Instructions.Insert(body.Instructions.IndexOf(edit.Target), created); break;
			case "delete": if (edit.Target == null || !body.Instructions.Remove(edit.Target)) throw new ArgumentException("delete target is absent"); break;
			case "set_init_locals": body.InitLocals = edit.BoolValue!.Value; break;
			default: throw new ArgumentException("Unsupported edit");
			}
			if (ReadOptionalBool(arguments, "optimize_macros") ?? false) body.Instructions.OptimizeMacros();
			body.UpdateInstructionOffsets();
		}

		static string ApplyLegacyReturn(Dictionary<string, object> arguments, MethodDef method, bool nop) {
			object? raw = nop ? "default" : arguments.TryGetValue("value", out var value) ? value : null;
			var (instructions, local, description) = BuildReturnSequence(method, raw, nop);
			var body = method.Body; body.ExceptionHandlers.Clear(); body.Variables.Clear();
			if (local != null) { body.Variables.Add(local); body.InitLocals = true; }
			body.Instructions.Clear(); foreach (var instruction in instructions) body.Instructions.Add(instruction);
			body.KeepOldMaxStack = false; body.UpdateInstructionOffsets();
			return description;
		}

		internal LegacyEditPlan BuildLegacyRenamePlan(Dictionary<string, object>? arguments, EditWorkspace workspace) {
			if (arguments == null) throw new ArgumentException("Arguments required");
			var requested = (ReadOptionalString(arguments, "target_kind") ?? throw new ArgumentException("target_kind is required")).Trim().ToLowerInvariant();
			if (requested == "param") requested = "parameter";
			else if (requested == "generic_param") requested = "generic_parameter";
			var token = ReadOptionalUInt(arguments, "token") ?? throw new ArgumentException("token is required");
			var module = workspace.PrivateModule;
			var definition = module.ResolveToken(token) ?? throw new ArgumentException("Metadata token could not be resolved");
			if (requested == "enum_members") {
				if (definition is not TypeDef enumType || !enumType.IsEnum) throw new ArgumentException("token is not an enum TypeDef");
				return BuildLegacyEnumMembersRenamePlan(arguments, module, enumType);
			}
			var newName = ReadRenameSymbolName(arguments);
			var plan = new LegacyEditPlan();
			ValidateLegacyRenameTarget(definition, requested, newName);
			if (string.Equals(Name(definition), newName, StringComparison.Ordinal)) {
				plan.Projection["changed"] = false; plan.Projection["target_kind"] = requested;
				plan.Projection["token"] = token; plan.Projection["token_hex"] = Token(token);
				plan.Projection["old_name"] = Name(definition); plan.Projection["new_name"] = newName;
				plan.Projection[definition is TypeDef ? "updated_type_references" : "updated_member_references"] = 0;
				return plan;
			}
			if (definition is TypeDef type && requested is "type" or "class" or "enum" or "interface" or "struct" or "delegate")
				AddLegacyRename(plan, module, "type", type, newName, module.GetTypeRefs().Where(x => new TypeEqualityComparer(RenameTypeComparerOptions).Equals(x, type)).Cast<IMDTokenProvider>());
			else if (definition is MethodDef method && requested == "method")
				AddLegacyRename(plan, module, "method", method, newName, EnumerateMethodMemberRefs(module).Where(x => x.IsMethodRef && MemberRefTargetsMethod(x, method)).Cast<IMDTokenProvider>());
			else if (definition is FieldDef field && requested is "field" or "enum_member")
				AddLegacyRename(plan, module, "field", field, newName, EnumerateFieldMemberRefs(module).Where(x => MemberRefTargetsField(x, field)).Cast<IMDTokenProvider>());
			else if (definition is PropertyDef && requested == "property") AddSimpleUpdate(plan, "property_update", token, newName);
			else if (definition is EventDef && requested == "event") AddSimpleUpdate(plan, "event_update", token, newName);
			else if (definition is ParamDef && requested is "parameter" or "param") AddSimpleUpdate(plan, "parameter_update", token, newName, parameter: true);
			else if (definition is GenericParam && requested is "generic_parameter" or "generic_param") AddSimpleUpdate(plan, "generic_parameter_update", token, newName);
			else throw new ArgumentException("target_kind does not match token");
			plan.Projection["changed"] = plan.Changed; plan.Projection["target_kind"] = requested;
			plan.Projection["token"] = token; plan.Projection["token_hex"] = Token(token);
			plan.Projection["old_name"] = Name(definition); plan.Projection["new_name"] = newName;
			plan.Projection[definition is TypeDef ? "updated_type_references" : "updated_member_references"] =
				plan.Operations.Count == 0 ? 0 : ReferenceCount(plan.Operations[0]);
			return plan;
		}

		static void ValidateLegacyRenameTarget(IMDTokenProvider definition, string requested, string newName) {
			if (definition is TypeDef type && requested is "type" or "class" or "enum" or "interface" or "struct" or "delegate") {
				if (type.IsGlobalModuleType) throw new ArgumentException("The special <Module> type cannot be renamed");
				var actual = GetTypeSymbolKind(type);
				if (requested != "type" && requested != actual) throw new ArgumentException("target_kind does not match the TypeDef");
				return;
			}
			if (definition is MethodDef method && requested == "method") {
				if (method.IsConstructor) throw new ArgumentException("CLR constructors cannot be renamed");
				return;
			}
			if (definition is FieldDef field && requested is "field" or "enum_member") {
				var enumMember = field.DeclaringType.IsEnum && field.IsStatic && field.IsLiteral;
				if (requested == "enum_member" && !enumMember) throw new ArgumentException("target is not a literal enum member");
				if (field.DeclaringType.IsEnum && (field.Name == "value__" || newName == "value__"))
					throw new ArgumentException("value__ is reserved for the enum backing field");
				return;
			}
			if (definition is PropertyDef property && requested == "property") {
				if (property.DeclaringType.Properties.Any(x => x != property && x.Name == newName
					&& new SigComparer(RenameMemberComparerOptions).Equals(x.PropertySig, property.PropertySig)))
					throw new ArgumentException("a property with the same name and signature already exists");
				return;
			}
			if (definition is EventDef eventDef && requested == "event") {
				if (eventDef.DeclaringType.Events.Any(x => x != eventDef && x.Name == newName))
					throw new ArgumentException("an event with the same name already exists");
				return;
			}
			if (definition is ParamDef parameter && requested == "parameter") {
				if (parameter.Sequence == 0) throw new ArgumentException("return ParamDef cannot be renamed");
				if (parameter.DeclaringMethod.ParamDefs.Any(x => x != parameter && x.Sequence != 0 && x.Name == newName))
					throw new ArgumentException("a parameter with the same name already exists");
				return;
			}
			if (definition is GenericParam generic && requested == "generic_parameter") {
				var siblings = generic.Owner is TypeDef ownerType ? ownerType.GenericParameters
					: generic.Owner is MethodDef ownerMethod ? ownerMethod.GenericParameters : throw new ArgumentException("generic parameter has no owner");
				if (siblings.Any(x => x != generic && x.Name == newName)) throw new ArgumentException("a generic parameter with the same name already exists");
				return;
			}
			throw new ArgumentException("target_kind does not match token");
		}

		LegacyEditPlan BuildLegacyEnumMembersRenamePlan(Dictionary<string, object> arguments, ModuleDef module, TypeDef type) {
			var requests = ParseEnumMemberRenameRequests(arguments);
			var literalFields = type.Fields.Where(x => x.IsStatic && x.IsLiteral && x.HasConstant && x.Constant?.Value != null).ToList();
			if (literalFields.Count == 0 || literalFields.Count != requests.Count)
				throw new ArgumentException("members must describe every enum literal exactly once");
			var fieldsByValue = literalFields.Select(x => (Field: x, Value: ReadEnumConstant(x))).GroupBy(x => x.Value).ToArray();
			if (fieldsByValue.Any(x => x.Count() != 1)) throw new ArgumentException("enum aliases cannot be mapped by value alone");
			var lookup = fieldsByValue.ToDictionary(x => x.Key, x => x.Single().Field);
			if (!new HashSet<decimal>(lookup.Keys).SetEquals(requests.Select(x => x.Value)))
				throw new ArgumentException("members values must exactly match the enum's existing values");
			var targetFields = requests.Select(x => lookup[x.Value]).ToHashSet();
			var desiredNames = new HashSet<string>(requests.Select(x => x.Name), StringComparer.Ordinal);
			if (type.Fields.Any(x => !targetFields.Contains(x) && desiredNames.Contains(x.Name.String)))
				throw new ArgumentException("an enum member name collides with a non-target field");

			var allReferences = EnumerateFieldMemberRefs(module).ToArray();
			var rows = requests.Select(request => {
				var field = lookup[request.Value];
				var references = allReferences.Where(x => MemberRefTargetsField(x, field)).Cast<IMDTokenProvider>().ToArray();
				return (Field: field, Value: request.Value, OldName: field.Name.String, NewName: request.Name, References: references);
			}).ToArray();
			var changed = rows.Where(x => !string.Equals(x.OldName, x.NewName, StringComparison.Ordinal)).ToArray();
			var plan = new LegacyEditPlan();
			// A complete enum mapping can swap names. Move every changed definition/reference to a
			// collision-free temporary name first, then to its requested name. Both phases are
			// internal composite operations and therefore replay/invert atomically with the commit.
			for (var index = 0; index < changed.Length; index++)
				AddLegacyRename(plan, "field", changed[index].Field, changed[index].OldName,
					"__dnspy_mcp_enum_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N"), changed[index].References);
			for (var index = 0; index < changed.Length; index++) {
				var temporary = (string)((Dictionary<string, object?>)plan.Operations[index]["definition"]!)["new_name"]!;
				AddLegacyRename(plan, "field", changed[index].Field, temporary, changed[index].NewName, changed[index].References);
			}
			plan.Projection["changed"] = changed.Length != 0;
			plan.Projection["changed_count"] = changed.Length;
			plan.Projection["target_kind"] = "enum_members";
			plan.Projection["enum_token"] = type.MDToken.Raw;
			plan.Projection["enum_token_hex"] = Token(type.MDToken.Raw);
			plan.Projection["enum_full_name"] = type.FullName;
			plan.Projection["updated_member_references"] = changed.Sum(x => x.References.Length);
			plan.Projection["members"] = rows.Select(x => new Dictionary<string, object?> {
				["field_token"] = x.Field.MDToken.Raw, ["field_token_hex"] = Token(x.Field.MDToken.Raw), ["value"] = x.Value,
				["old_name"] = x.OldName, ["new_name"] = x.NewName, ["changed"] = x.OldName != x.NewName,
				["updated_member_references"] = x.References.Length,
			}).ToArray();
			return plan;
		}

		static void AddSimpleUpdate(LegacyEditPlan plan, string kind, uint token, string name, bool parameter = false) {
			var op = new Dictionary<string, object?> { ["kind"] = kind, ["name"] = name };
			op[parameter ? "parameter_target" : "target"] = new Dictionary<string, object?> { ["token"] = Token(token) };
			plan.Operations.Add(op);
		}

		static void AddLegacyRename(LegacyEditPlan plan, ModuleDef module, string kind, IMDTokenProvider definition, string newName,
			IEnumerable<IMDTokenProvider> references) {
			AddLegacyRename(plan, kind, definition, Name(definition), newName, references);
		}

		static void AddLegacyRename(LegacyEditPlan plan, string kind, IMDTokenProvider definition, string oldName, string newName,
			IEnumerable<IMDTokenProvider> references) {
			if (oldName == newName) return;
			var rows = references.Where(x => x.MDToken.Raw != 0).GroupBy(x => x.MDToken.Raw).OrderBy(x => x.Key).Select(group => new Dictionary<string, object?> {
				["table"] = kind == "type" ? "TypeRef" : "MemberRef", ["token"] = Token(group.Key),
				["old_name"] = oldName, ["new_name"] = newName,
			}).ToArray();
			plan.Operations.Add(new Dictionary<string, object?> {
				["kind"] = "legacy_symbol_rename", ["target_kind"] = kind,
				["definition"] = new Dictionary<string, object?> { ["token"] = Token(definition.MDToken.Raw), ["old_name"] = oldName, ["new_name"] = newName },
				["references"] = rows,
			});
		}

		static int ReferenceCount(Dictionary<string, object?> operation) => operation.TryGetValue("references", out var value) && value is Array array ? array.Length : 0;
		static string Name(IMDTokenProvider value) => value switch { TypeDef x => x.Name, MethodDef x => x.Name, FieldDef x => x.Name, PropertyDef x => x.Name, EventDef x => x.Name, ParamDef x => x.Name, GenericParam x => x.Name, _ => string.Empty };
		static string Token(uint value) => "0x" + value.ToString("x8");

		internal static Dictionary<string, object?> StructuredBody(MethodDef method) {
			var body = method.Body; var instructions = body.Instructions; var index = instructions.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i);
			return new Dictionary<string, object?> {
				["max_stack"] = (int)body.MaxStack, ["init_locals"] = body.InitLocals,
				["locals"] = body.Variables.Select(x => new Dictionary<string, object?> { ["type"] = x.Type.FullName, ["name"] = x.Name ?? string.Empty }).ToArray(),
				["instructions"] = instructions.Select(x => new Dictionary<string, object?> { ["opcode"] = x.OpCode.Name, ["operand"] = StructuredOperand(method, body, x.Operand, index) }).ToArray(),
				["exception_handlers"] = body.ExceptionHandlers.Select(x => new Dictionary<string, object?> {
					["kind"] = x.HandlerType switch { ExceptionHandlerType.Catch => "catch", ExceptionHandlerType.Finally => "finally", ExceptionHandlerType.Fault => "fault", ExceptionHandlerType.Filter => "filter", _ => throw new ArgumentException("unsupported exception handler") },
					["try_start"] = Position(x.TryStart, index, instructions.Count), ["try_end"] = Position(x.TryEnd, index, instructions.Count),
					["handler_start"] = Position(x.HandlerStart, index, instructions.Count), ["handler_end"] = Position(x.HandlerEnd, index, instructions.Count),
					["filter_start"] = x.FilterStart == null ? null : Position(x.FilterStart, index, instructions.Count), ["catch_type"] = x.CatchType?.FullName,
				}).ToArray(),
			};
		}

		static object? StructuredOperand(MethodDef method, CilBody body, object? value, IReadOnlyDictionary<Instruction, int> index) => value switch {
			null => null,
			sbyte x => Scalar("i32", (int)x), byte x => Scalar("i32", (int)x), short x => Scalar("i32", (int)x), ushort x => Scalar("i32", (int)x), int x => Scalar("i32", x),
			long x => Scalar("i64", x), float x => Scalar("f32", x), double x => Scalar("f64", x), string x => Scalar("string", x),
			Instruction x => Scalar("label", index[x], "instruction_index"),
			IList<Instruction> x => new Dictionary<string, object?> { ["kind"] = "switch", ["instruction_indices"] = x.Select(y => index[y]).ToArray() },
			Local x => Scalar("local", body.Variables.IndexOf(x), "local_index"),
			Parameter x => Scalar("arg", method.Parameters.IndexOf(x), "argument_index"),
			IMDTokenProvider x when x.MDToken.Raw != 0 => new Dictionary<string, object?> { ["kind"] = "token", ["token"] = Token(x.MDToken.Raw) },
			_ => throw new ArgumentException("unsupported IL operand: " + value.GetType().Name),
		};
		static Dictionary<string, object?> Scalar(string kind, object value, string name = "value") => new() { ["kind"] = kind, [name] = value };
		static int Position(Instruction? value, IReadOnlyDictionary<Instruction, int> index, int end) => value == null ? end : index.TryGetValue(value, out var result) ? result : end;
	}
}
