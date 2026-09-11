using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// Addresses an attached definition in the graph immediately before an operation.
/// The caller must bind the address to that graph's expected fingerprint. It is
/// not a permanent object ID, a public metadata token, or a name-based fallback.
/// </summary>
internal static class EditDefinitionAddress {
	public static string Capture(ModuleDef module, IMDTokenProvider definition) {
		string address;
		switch (definition) {
		case TypeDef type: address = TypePath(module, type); break;
		case MethodDef method: address = MethodPath(module, method); break;
		case FieldDef field:
			address = TypePath(module, field.DeclaringType) + "/f/" + Index(field.DeclaringType.Fields, field); break;
		case PropertyDef property:
			address = TypePath(module, property.DeclaringType) + "/p/" + Index(property.DeclaringType.Properties, property); break;
		case EventDef eventDef:
			address = TypePath(module, eventDef.DeclaringType) + "/e/" + Index(eventDef.DeclaringType.Events, eventDef); break;
		case ParamDef parameter:
			address = MethodPath(module, parameter.DeclaringMethod) + "/a/" + Number(parameter.Sequence); break;
		case GenericParam generic:
			address = generic.Owner switch {
				TypeDef owner => TypePath(module, owner),
				MethodDef owner => MethodPath(module, owner),
				_ => throw Conflict(),
			};
			address += "/g/" + Number(generic.Number); break;
		default: throw Conflict();
		}
		if (!ReferenceEquals(Resolve(module, address), definition)) throw Conflict();
		return address;
	}

	public static IMDTokenProvider Resolve(ModuleDef module, string address) {
		if (address == null) throw Conflict();
		var parts = address.Split('/');
		if (parts.Length < 2 || parts.Length % 2 != 0 || parts[0] != "t") throw Conflict();
		IMDTokenProvider current = At(module.Types, Parse(parts[1]));
		for (var i = 2; i < parts.Length; i += 2) {
			var index = Parse(parts[i + 1]);
			current = current switch {
				TypeDef type => parts[i] switch {
					"t" => At(type.NestedTypes, index),
					"m" => At(type.Methods, index),
					"f" => At(type.Fields, index),
					"p" => At(type.Properties, index),
					"e" => At(type.Events, index),
					"g" => Unique(type.GenericParameters.Where(g => g.Number == index)),
					_ => throw Conflict(),
				},
				MethodDef method => parts[i] switch {
					"a" => Unique(method.ParamDefs.Where(p => p.Sequence == index)),
					"g" => Unique(method.GenericParameters.Where(g => g.Number == index)),
					_ => throw Conflict(),
				},
				_ => throw Conflict(),
			};
		}
		return current;
	}

	static string TypePath(ModuleDef module, TypeDef? type) {
		if (type == null) throw Conflict();
		var path = new List<string>();
		var seen = new HashSet<TypeDef>();
		while (true) {
			if (!seen.Add(type)) throw Conflict();
			var owner = type.DeclaringType;
			path.Add("t/" + Index(owner == null ? module.Types : owner.NestedTypes, type));
			if (owner == null) break;
			type = owner;
		}
		path.Reverse();
		return string.Join("/", path);
	}
	static string MethodPath(ModuleDef module, MethodDef? method) {
		if (method?.DeclaringType == null) throw Conflict();
		return TypePath(module, method.DeclaringType) + "/m/" + Index(method.DeclaringType.Methods, method);
	}
	static string Index<T>(IList<T> values, T value) {
		var index = values.IndexOf(value);
		if (index < 0) throw Conflict();
		return Number(index);
	}
	static T At<T>(IList<T> values, int index) {
		if (index < 0 || index >= values.Count) throw Conflict();
		return values[index];
	}
	static T Unique<T>(IEnumerable<T> values) {
		var matches = values.Take(2).ToArray();
		if (matches.Length != 1) throw Conflict();
		return matches[0];
	}
	static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
	static int Parse(string value) {
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
			|| number < 0 || Number(number) != value) throw Conflict();
		return number;
	}
	static EditDomainException Conflict() => new("EDIT_HISTORY_CONFLICT");
}
