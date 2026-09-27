using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

internal static class T091ResourceTypesProbe {
	const string ResourceName = "T091.Values.resources";

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var objects = new Dictionary<string, IMDTokenProvider>();
		var original = ResourceBytes(module);
		var excluded = new[] { "char", "date", "span", "decimal", "null", "custom" }
			.ToDictionary(name => name, name => Raw(original, name), StringComparer.Ordinal);
		var edits = new (string Name, string Kind, object Value, object Expected)[] {
			("text", "string", "edited", "edited"), ("boolean", "boolean", false, false),
			("i1", "i1", -8, (sbyte)-8), ("u1", "u1", 251, (byte)251),
			("i2", "i2", -30001, (short)-30001), ("u2", "u2", 60001, (ushort)60001),
			("i4", "i4", -2000000001, -2000000001), ("u4", "u4", 4000000001U, 4000000001U),
			("i8", "i8", -9007199254740995L, -9007199254740995L),
			("u8", "u8", 18446744073709551611UL, 18446744073709551611UL),
			("r4", "r4", 2.5f, 2.5f), ("r8", "r8", -3.25d, -3.25d),
			("bytes", "bytes", Convert.ToBase64String(new byte[] { 9, 8, 7, 6 }), new byte[] { 9, 8, 7, 6 }),
			("stream", "bytes", Convert.ToBase64String(new byte[] { 10, 11, 12 }), new byte[] { 10, 11, 12 }),
		};
		var expected = new Dictionary<string, object>(StringComparer.Ordinal);
		var index = 0;
		foreach (var edit in edits) {
			var operation = Operation(edit.Name, edit.Kind, edit.Value);
			using var json = JsonDocument.Parse(operation);
			EditOperationRegistry.Apply(module, json.RootElement, objects, index++);
			EditStructuralValidator.Validate(module);
			expected.Add(edit.Name, edit.Expected);
			var bytes = EditWorkspace.WriteCanonical(module);
			var output = Path.Combine(Path.GetTempPath(), "t091-types-" + Guid.NewGuid().ToString("N") + ".dll");
			try {
				File.WriteAllBytes(output, bytes);
				using var reloaded = ModuleDefMD.Load(output);
				var blob = ResourceBytes(reloaded);
				foreach (var item in expected) AssertValue(blob, item.Key, item.Value);
				foreach (var name in excluded) {
					var after = Raw(blob, name.Key);
					if (after.Type != name.Value.Type || !after.Bytes.SequenceEqual(name.Value.Bytes))
						throw new InvalidOperationException("excluded resource changed: " + name.Key);
				}
			}
			finally { File.Delete(output); }
		}
		var beforeReject = EditWorkspace.WriteCanonical(module);
		Reject(Operation("i4", "u4", 1), "EDIT_VALIDATION_FAILED");
		Reject(Operation("char", "char", "R"), "EDIT_VALIDATION_FAILED");
		if (!EditWorkspace.WriteCanonical(module).SequenceEqual(beforeReject))
			throw new InvalidOperationException("rejected operation changed image");
		Console.WriteLine("PASS t091-resource-types kinds=13 storage=14 BCL-type-and-value=True excluded-raw=6 rejects-unchanged=True");

		void Reject(string operation, string code) {
			using var json = JsonDocument.Parse(operation);
			try {
				EditOperationRegistry.Apply(module, json.RootElement, objects, index++);
				throw new InvalidOperationException("invalid resource edit accepted: " + operation);
			}
			catch (EditDomainException ex) when (ex.Code == code) { }
		}
	}

	static string Operation(string name, string kind, object value) => JsonSerializer.Serialize(new {
		kind = "managed_resource_update", target = new { name = ResourceName },
		entry = new { name, value_kind = kind, value },
	});

	static byte[] ResourceBytes(ModuleDef module) =>
		(module.Resources.OfType<EmbeddedResource>().Single(r => r.Name == ResourceName)).CreateReader().ToArray();

	static (string Type, byte[] Bytes) Raw(byte[] blob, string name) {
		using var reader = new ResourceReader(new MemoryStream(blob));
		reader.GetResourceData(name, out var type, out var bytes);
		return (type, bytes);
	}

	static void AssertValue(byte[] blob, string name, object expected) {
		using var reader = new ResourceReader(new MemoryStream(blob));
		var items = reader.GetEnumerator();
		object? actual = null;
		var found = false;
		while (items.MoveNext()) {
			if ((string)items.Key != name) continue;
			actual = items.Value;
			found = true;
			break;
		}
		if (!found) throw new InvalidOperationException("resource entry missing: " + name);
		if (name == "stream") {
			if (actual is not Stream stream) throw new InvalidOperationException("stream CLR type changed");
			using var copy = new MemoryStream();
			stream.CopyTo(copy);
			if (!copy.ToArray().SequenceEqual((byte[])expected)) throw new InvalidOperationException("stream bytes changed");
		}
		else if (expected is byte[] bytes) {
			if (actual is not byte[] array || !array.SequenceEqual(bytes)) throw new InvalidOperationException("byte array type/value changed");
		}
		else if (actual?.GetType() != expected.GetType() || !Equals(actual, expected))
			throw new InvalidOperationException($"resource type/value changed: {name}, actual={actual?.GetType().FullName}:{actual}");
	}
}
