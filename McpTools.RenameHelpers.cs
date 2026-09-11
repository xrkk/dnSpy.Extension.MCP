using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP {
	sealed partial class McpTools {
		const SigComparerOptions RenameTypeComparerOptions =
			SigComparerOptions.CompareDeclaringTypes |
			SigComparerOptions.CompareAssemblyPublicKeyToken |
			SigComparerOptions.TypeRefCanReferenceGlobalType |
			SigComparerOptions.PrivateScopeIsComparable |
			SigComparerOptions.DontProjectWinMDRefs;
		const SigComparerOptions RenameMemberComparerOptions =
			SigComparerOptions.CompareAssemblyPublicKeyToken |
			SigComparerOptions.TypeRefCanReferenceGlobalType |
			SigComparerOptions.PrivateScopeIsComparable |
			SigComparerOptions.DontProjectWinMDRefs;

		sealed class EnumMemberRenameRequest {
			public string Name { get; }
			public decimal Value { get; }
			public EnumMemberRenameRequest(string name, decimal value) { Name = name; Value = value; }
		}

		static string ReadRenameSymbolName(Dictionary<string, object> arguments) {
			var newName = ReadOptionalString(arguments, "new_name")
				?? throw new ArgumentException("new_name is required and cannot be empty");
			newName = newName.Trim();
			if (newName.Length == 0) throw new ArgumentException("new_name is required and cannot be empty");
			if (newName.IndexOf('\0') >= 0) throw new ArgumentException("new_name cannot contain a NUL character");
			return newName;
		}

		static string GetTypeSymbolKind(TypeDef type) {
			if (type.IsEnum) return "enum";
			if (type.IsInterface) return "interface";
			if (type.IsValueType) return "struct";
			var baseType = type.BaseType?.FullName;
			return baseType == "System.MulticastDelegate" || baseType == "System.Delegate" ? "delegate" : "class";
		}

		static List<EnumMemberRenameRequest> ParseEnumMemberRenameRequests(Dictionary<string, object> arguments) {
			if (!arguments.TryGetValue("members", out var raw) || raw == null)
				throw new ArgumentException("members is required and must be a non-empty array");
			var requests = new List<EnumMemberRenameRequest>();
			if (raw is JsonElement element) {
				if (element.ValueKind != JsonValueKind.Array) throw new ArgumentException("members must be an array");
				foreach (var item in element.EnumerateArray()) {
					if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var nameElement)
						|| nameElement.ValueKind != JsonValueKind.String || !item.TryGetProperty("value", out var valueElement))
						throw new ArgumentException("each members item requires a string name and integral value");
					requests.Add(new EnumMemberRenameRequest(ValidateEnumMemberName(nameElement.GetString()), ParseIntegralEnumValue(valueElement)));
				}
			}
			else if (raw is IEnumerable<object> sequence) {
				foreach (var item in sequence) {
					if (item is not Dictionary<string, object> dictionary || !dictionary.TryGetValue("value", out var value) || value == null)
						throw new ArgumentException("each members item requires a string name and integral value");
					requests.Add(new EnumMemberRenameRequest(ValidateEnumMemberName(ReadOptionalString(dictionary, "name")), ParseIntegralEnumValue(value)));
				}
			}
			else throw new ArgumentException("members must be an array");
			if (requests.Count == 0) throw new ArgumentException("members must contain at least one item");
			if (requests.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != requests.Count)
				throw new ArgumentException("members contains duplicate names");
			if (requests.Select(x => x.Value).Distinct().Count() != requests.Count)
				throw new ArgumentException("members contains duplicate values; aliases cannot be mapped by value alone");
			return requests;
		}

		static string ValidateEnumMemberName(string? name) {
			name = name?.Trim();
			if (name == null || name.Length == 0) throw new ArgumentException("enum member name cannot be empty");
			if (name.IndexOf('\0') >= 0) throw new ArgumentException("enum member name cannot contain a NUL character");
			if (name == "value__") throw new ArgumentException("value__ is reserved for the enum backing field");
			return name;
		}

		static decimal ParseIntegralEnumValue(object raw) {
			decimal value;
			if (raw is JsonElement element) {
				if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value) && decimal.Truncate(value) == value) return value;
				if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
				throw new ArgumentException("enum member value must be a decimal integer or numeric string");
			}
			if (raw is string text && decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
			try { value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); }
			catch { throw new ArgumentException("enum member value must be an integer"); }
			if (decimal.Truncate(value) != value) throw new ArgumentException("enum member value must be an integer");
			return value;
		}

		static decimal ReadEnumConstant(FieldDef field) {
			try { return Convert.ToDecimal(field.Constant!.Value, CultureInfo.InvariantCulture); }
			catch { throw new ArgumentException("Enum field " + field.FullName + " has an unsupported constant value"); }
		}

		static bool MemberRefTargetsField(MemberRef memberRef, FieldDef field) {
			if (!memberRef.IsFieldRef) return false;
			try { if (ReferenceEquals(memberRef.ResolveField(), field)) return true; } catch { }
			return new SigComparer(RenameMemberComparerOptions).Equals(memberRef, field)
				&& MemberRefParentMatchesType(memberRef.Class, field.DeclaringType);
		}

		static bool MemberRefTargetsMethod(MemberRef memberRef, MethodDef method) {
			if (!memberRef.IsMethodRef) return false;
			try { if (ReferenceEquals(memberRef.ResolveMethod(), method)) return true; } catch { }
			return new SigComparer(RenameMemberComparerOptions).Equals(memberRef, method)
				&& MemberRefParentMatchesType(memberRef.Class, method.DeclaringType);
		}

		static IEnumerable<MemberRef> EnumerateMethodMemberRefs(ModuleDef module) {
			foreach (var memberRef in module.GetMemberRefs()) yield return memberRef;
			foreach (var owner in module.GetTypes().SelectMany(x => x.Methods).Where(x => x.HasBody))
				foreach (var instruction in owner.Body.Instructions) {
					if (instruction.Operand is MemberRef memberRef) yield return memberRef;
					else if (instruction.Operand is MethodSpec spec && spec.Method is MemberRef methodRef) yield return methodRef;
				}
		}

		static IEnumerable<MemberRef> EnumerateFieldMemberRefs(ModuleDef module) {
			foreach (var memberRef in module.GetMemberRefs().Where(x => x.IsFieldRef)) yield return memberRef;
			foreach (var owner in module.GetTypes().SelectMany(x => x.Methods).Where(x => x.HasBody))
				foreach (var instruction in owner.Body.Instructions)
					if (instruction.Operand is MemberRef memberRef && memberRef.IsFieldRef) yield return memberRef;
		}

		static bool MemberRefParentMatchesType(IMemberRefParent parent, TypeDef type) {
			var comparer = new TypeEqualityComparer(RenameTypeComparerOptions);
			if (parent is TypeDef typeDef) return comparer.Equals(typeDef, type);
			if (parent is TypeRef typeRef) return comparer.Equals(typeRef, type);
			if (parent is TypeSpec typeSpec) {
				var signature = typeSpec.TypeSig.RemovePinnedAndModifiers();
				var reference = signature as TypeDefOrRefSig;
				if (reference == null && signature is GenericInstSig generic) reference = generic.GenericType;
				return reference != null && comparer.Equals(reference.TypeDefOrRef, type);
			}
			if (parent is MethodDef method) return comparer.Equals(method.DeclaringType, type);
			return parent is ModuleRef moduleRef && type.IsGlobalModuleType
				&& StringComparer.OrdinalIgnoreCase.Equals(moduleRef.Name, type.Module.Name);
		}
	}
}
