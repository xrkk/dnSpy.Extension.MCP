using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace dnSpy.Extension.MCP.Editing;

internal sealed class EditSchemaCatalog : IDisposable {
	readonly JsonDocument schemas;
	readonly JsonDocument lowering;
	readonly JsonDocument faults;
	readonly JsonDocument mutations;

	public EditSchemaCatalog() {
		schemas = Load("dnspy.edit.expanded-tool-schemas.json");
		lowering = Load("dnspy.edit.operation-lowering.json");
		faults = Load("dnspy.edit.fault-golden.json");
		mutations = Load("dnspy.edit.mutation-corpus.json");
	}

	public Dictionary<string, object> InputSchema(string tool) => ConvertObject(schemas.RootElement.GetProperty(tool).GetProperty("inputSchema"));
	public Dictionary<string, object> OutputSchema(string tool) => ConvertObject(schemas.RootElement.GetProperty(tool).GetProperty("outputSchema"));
	public JsonElement InputElement(string tool) => schemas.RootElement.GetProperty(tool).GetProperty("inputSchema");
	public JsonElement Lowering => lowering.RootElement;
	public JsonElement Faults => faults.RootElement;
	public JsonElement Mutations => mutations.RootElement;

	static JsonDocument Load(string suffix) {
		var assembly = typeof(EditSchemaCatalog).Assembly;
		foreach (var name in assembly.GetManifestResourceNames()) {
			if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
			using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Embedded resource is empty: " + suffix);
			return JsonDocument.Parse(stream);
		}
		throw new InvalidOperationException("Embedded edit contract is missing: " + suffix);
	}

	static Dictionary<string, object> ConvertObject(JsonElement value) => (Dictionary<string, object>)Convert(value)!;
	static object? Convert(JsonElement value) {
		switch (value.ValueKind) {
		case JsonValueKind.Object:
			var map = new Dictionary<string, object>(StringComparer.Ordinal);
			foreach (var p in value.EnumerateObject()) map[p.Name] = Convert(p.Value)!;
			return map;
		case JsonValueKind.Array:
			var list = new List<object>(); foreach (var item in value.EnumerateArray()) list.Add(Convert(item)!); return list;
		case JsonValueKind.String: return value.GetString() ?? string.Empty;
		case JsonValueKind.Number: if (value.TryGetInt32(out var i)) return i; if (value.TryGetInt64(out var l)) return l; return value.GetDouble();
		case JsonValueKind.True: return true;
		case JsonValueKind.False: return false;
		default: return null;
		}
	}

	public void Dispose() { schemas.Dispose(); lowering.Dispose(); faults.Dispose(); mutations.Dispose(); }
}
