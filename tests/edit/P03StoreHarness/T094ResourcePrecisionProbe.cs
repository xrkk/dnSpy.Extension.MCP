using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class T094ResourcePrecisionProbe {
	const string ResourceName = "T091.Values.resources";

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var objects = new Dictionary<string, IMDTokenProvider>();
		var baseline = ResourceBytes(module);
		var original = EditResourceCodec.Parse(baseline).Entries.ToDictionary(x => x.Name,
			x => (x.TypeCode, x.Raw), StringComparer.Ordinal);
		var counter = 0;
		var dates = new [] {
			DateTime.MinValue, DateTime.MaxValue,
			new DateTime(2024, 11, 3, 5, 30, 0, DateTimeKind.Utc),
			new DateTime(2024, 11, 3, 5, 30, 0, DateTimeKind.Utc).ToLocalTime(),
			new DateTime(2024, 11, 3, 6, 30, 0, DateTimeKind.Utc).ToLocalTime(),
		};
		foreach (var unit in new[] { 0, 0xffff, 0xd800, 0xdc00 })
			Apply("char", "char", new { code_unit = unit }, EditResourceCodec.CodeChar,
				BitConverter.GetBytes((ushort)unit), value => value is char c && c == unit);
		foreach (var value in new [] {
			new { lo = 0u, mid = 0u, hi = 0u, negative = true, scale = 28 },
			new { lo = uint.MaxValue, mid = uint.MaxValue, hi = uint.MaxValue, negative = false, scale = 0 },
			new { lo = 25u, mid = 0u, hi = 0u, negative = true, scale = 1 },
		}) {
			var expected = new decimal(unchecked((int)value.lo), unchecked((int)value.mid), unchecked((int)value.hi), value.negative, (byte)value.scale);
			var bits = decimal.GetBits(expected);
			var raw = bits.SelectMany(BitConverter.GetBytes).ToArray();
			Apply("decimal", "decimal", value, EditResourceCodec.CodeDecimal, raw,
				actual => actual is decimal got && decimal.GetBits(got).SequenceEqual(bits));
		}
		foreach (var ticks in new[] { long.MinValue, -1L, 0L, long.MaxValue })
			Apply("span", "timespan", new { ticks = ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) },
				EditResourceCodec.CodeTimeSpan, BitConverter.GetBytes(ticks),
				value => value is TimeSpan span && span.Ticks == ticks);
		foreach (var date in dates) {
			var binary = date.ToBinary();
			Apply("date", "datetime", new { binary = binary.ToString(System.Globalization.CultureInfo.InvariantCulture) },
				EditResourceCodec.CodeDateTime, BitConverter.GetBytes(binary),
				value => value is DateTime actual && actual.ToBinary() == binary);
		}
		var prior = EditWorkspace.WriteCanonical(module);
		using (var newRow = JsonDocument.Parse(Operation("char", "char", new { code_unit = 65 }))) {
			if (EditOperationVersions.RequiredVersion("managed_resource_update", newRow.RootElement) != 2 ||
				!EditOperationVersions.IsSupported("managed_resource_update", 2)) throw new InvalidOperationException("new kind version was not derived");
			try { EditOperationVersions.Validate("managed_resource_update", 1, newRow.RootElement); throw new InvalidOperationException("v1 accepted new resource domain"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { }
			EditOperationVersions.Validate("managed_resource_update", 2, newRow.RootElement);
		}
		using (var oldRow = JsonDocument.Parse(Operation("text", "string", "old"))) {
			if (EditOperationVersions.RequiredVersion("managed_resource_update", oldRow.RootElement) != 1) throw new InvalidOperationException("old resource domain changed version");
			EditOperationVersions.Validate("managed_resource_update", 1, oldRow.RootElement);
			try { EditOperationVersions.Validate("managed_resource_update", 2, oldRow.RootElement); throw new InvalidOperationException("v2 accepted old-only resource domain"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { }
			try { EditOperationVersions.Validate("managed_resource_update", 3, oldRow.RootElement); throw new InvalidOperationException("unknown version accepted"); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_OPERATION_VERSION_UNSUPPORTED") { }
		}
		foreach (var bad in new[] {
			Operation("char", "char", new { code_unit = -1 }),
			Operation("char", "char", new { code_unit = 65536 }),
			Operation("char", "char", new { code_unit = 0.5 }),
			Operation("decimal", "decimal", new { lo = 1u, mid = 0u, hi = 0u, negative = false, scale = 29 }),
			Operation("span", "timespan", new { ticks = "-0" }),
			Operation("span", "timespan", new { ticks = "9223372036854775808" }),
			Operation("date", "datetime", new { binary = "9223372036854775807" }),
			Operation("i4", "char", new { code_unit = 65 }),
		}) {
			using var json = JsonDocument.Parse(bad);
			try { EditOperationRegistry.Apply(module, json.RootElement, objects, counter++); throw new InvalidOperationException("bad edit accepted: " + bad); }
			catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(prior)) throw new InvalidOperationException("rejected edit changed image");
		}
		Console.WriteLine("PASS t094-resource-precision typed_roundtrips=" + (counter - 8) + " raw_preserved=true negatives=8");

		void Apply(string name, string kind, object value, int typeCode, byte[] raw, Func<object?, bool> match) {
			using var json = JsonDocument.Parse(Operation(name, kind, value));
			EditOperationRegistry.Apply(module, json.RootElement, objects, counter++);
			EditStructuralValidator.Validate(module);
			var image = EditWorkspace.WriteCanonical(module);
			var file = Path.Combine(Path.GetTempPath(), "t094-resource-" + Guid.NewGuid().ToString("N") + ".dll");
			try {
				File.WriteAllBytes(file, image);
				using var reloaded = ModuleDefMD.Load(file);
				var blob = ResourceBytes(reloaded);
				var parsed = EditResourceCodec.Parse(blob);
				var selected = parsed.Entries.Single(x => x.Name == name);
				if (selected.TypeCode != typeCode || !selected.Raw.SequenceEqual(raw)) throw new InvalidOperationException("raw/type changed: " + name);
				foreach (var untouched in original.Where(x => x.Key != name)) {
					var item = parsed.Entries.Single(x => x.Name == untouched.Key);
					if (item.TypeCode != untouched.Value.TypeCode || !item.Raw.SequenceEqual(untouched.Value.Raw))
						throw new InvalidOperationException("untouched entry changed: " + untouched.Key);
				}
				using var reader = new ResourceReader(new MemoryStream(blob));
				var enumerator = reader.GetEnumerator(); object? read = null;
				while (enumerator.MoveNext()) if ((string)enumerator.Key == name) { read = enumerator.Value; break; }
				if (!match(read)) throw new InvalidOperationException("BCL value changed: " + name);
				original[name] = (typeCode, raw);
			}
			finally { File.Delete(file); }
		}
	}

	static string Operation(string name, string kind, object value) => JsonSerializer.Serialize(new {
		kind = "managed_resource_update", target = new { name = ResourceName },
		entry = new { name, value_kind = kind, value },
	});
	static byte[] ResourceBytes(ModuleDef module) =>
		module.Resources.OfType<EmbeddedResource>().Single(r => r.Name == ResourceName).CreateReader().ToArray();
}
