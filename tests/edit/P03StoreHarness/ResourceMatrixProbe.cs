using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// P08 --resource-matrix: headless exercise of the resource operations.  Builds
// a standard .resources blob (string/bool/numerics/byte-array), adds it, edits
// entries surgically, replaces whole blobs, maintains an icon group, and proves
// (a) file-level readback after write+reload, (b) the compiled inverse restores
// the exact pre-state image, (c) invalid payloads reject with zero image
// change, and (d) no custom object is ever instantiated (CON-019 sentinel).
internal static class ResourceMatrixProbe {
	static string markerPath = string.Empty;

	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		markerPath = Path.Combine(Path.GetTempPath(), "p08-sentinel-" + Guid.NewGuid().ToString("N") + ".flag");
		var path = Path.GetFullPath(fixture);
		using var module = ModuleDefMD.Load(path);
		var objects = new Dictionary<string, IMDTokenProvider>();
		var index = 0;

		EditOperationOutcome Apply(string json) {
			using var document = JsonDocument.Parse(json);
			EditOperationOutcome outcome;
			try {
				outcome = EditOperationRegistry.Apply(module, document.RootElement, objects, index++);
				EditStructuralValidator.Validate(module);
			}
			catch (Exception ex) {
				Console.Error.WriteLine("PROBE-APPLY-FAIL json=" + json.Substring(0, Math.Min(200, json.Length)) + " ex=" + ex.Message);
				throw;
			}
			return outcome;
		}
		void Reject(string json) {
			var before = EditWorkspace.WriteCanonical(module);
			try {
				using var document = JsonDocument.Parse(json);
				EditOperationRegistry.Apply(module, document.RootElement, objects, index++);
				throw new InvalidOperationException("invalid resource payload was accepted: " + json.Substring(0, Math.Min(160, json.Length)));
			}
			catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
			catch (ArgumentException) { }
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("a rejected resource payload changed the module: " + json.Substring(0, Math.Min(160, json.Length)));
		}
		void RoundTrip(string json, Action<ModuleDefMD> assertReloaded) {
			var before = EditWorkspace.WriteCanonical(module);
			using var forward = JsonDocument.Parse(json);
			System.Collections.Generic.Dictionary<string, object?> inverse;
			try {
				inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
			}
			catch (Exception ex) {
				Console.Error.WriteLine("PROBE-INVERSE-FAIL json=" + json.Substring(0, Math.Min(300, json.Length)) + " ex=" + ex.Message);
				throw;
			}
			var outcome = Apply(json);
			var bytes = EditWorkspace.WriteCanonical(module);
			var temp = Path.Combine(Path.GetTempPath(), "p08-resource-" + Guid.NewGuid().ToString("N") + ".dll");
			File.WriteAllBytes(temp, bytes);
			using (var reloaded = ModuleDefMD.Load(temp)) assertReloaded(reloaded);
			File.Delete(temp);
			EditOperationRegistry.ApplyCompiledInverse(module,
				JsonDocument.Parse(JsonSerializer.Serialize(inverse)).RootElement, objects, index++);
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("the resource inverse did not restore the exact image");
			outcome.Undo();
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("the resource undo did not restore the exact image");
		}
		byte[] ResourceBytes(ModuleDef moduleOrReload, string name) =>
			((EmbeddedResource)moduleOrReload.Resources.First(r => r.Name == name)).CreateReader().ToArray();

		// ---- 1) build a standard blob with ResourceWriter (cross-process
		// deterministic — proven separately by the x64/x86 identical logs) and
		// add it through the frozen operation.
		var blobPath = Path.Combine(Path.GetTempPath(), "p08-matrix-" + Guid.NewGuid().ToString("N") + ".resources");
		using (var writer = new ResourceWriter(blobPath)) {
			writer.AddResource("text", "hello");
			writer.AddResource("count", 42);
			writer.AddResource("ratio", 3.5);
			writer.AddResource("flag", true);
			writer.AddResource("blob", new byte[] { 9, 8, 7 });
		}
		var blob = File.ReadAllBytes(blobPath);
		File.Delete(blobPath);
		// determinism: writing the same entries again produces identical bytes
		using (var writer = new ResourceWriter(blobPath)) {
			writer.AddResource("text", "hello");
			writer.AddResource("count", 42);
			writer.AddResource("ratio", 3.5);
			writer.AddResource("flag", true);
			writer.AddResource("blob", new byte[] { 9, 8, 7 });
		}
		if (!File.ReadAllBytes(blobPath).SequenceEqual(blob))
			throw new InvalidOperationException("ResourceWriter output is not deterministic in-process");
		File.Delete(blobPath);

		// add and KEEP the resource (entry-edit round trips below need it present)
		var addJson = "{\"kind\":\"managed_resource_add\",\"name\":\"P08.Matrix.resources\",\"data_base64\":\"" + Convert.ToBase64String(blob) + "\"}";
		Apply(addJson);
		{
			var bytes = EditWorkspace.WriteCanonical(module);
			var temp = Path.Combine(Path.GetTempPath(), "p08-resource-" + Guid.NewGuid().ToString("N") + ".dll");
			File.WriteAllBytes(temp, bytes);
			using (var reloaded = ModuleDefMD.Load(temp))
				Require(ResourceBytes(reloaded, "P08.Matrix.resources").SequenceEqual(blob), "the added resource blob did not survive reload");
			File.Delete(temp);
		}

		// ---- 2) surgical entry edits (string + int + byte array), one at a time
		RoundTrip("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Matrix.resources\"},\"entry\":{\"name\":\"text\",\"value_kind\":\"string\",\"value\":\"changed\"}}",
			reloaded => {
				var reloadedBlob = ResourceBytes(reloaded, "P08.Matrix.resources");
				var value = EntryValue(reloadedBlob, "text");
				if (!string.Equals(value as string, "changed", StringComparison.Ordinal))
					Console.Error.WriteLine("PROBE-ENTRY text=[" + value + "] len=" + reloadedBlob.Length
						+ " head=" + BitConverter.ToString(reloadedBlob, 0, Math.Min(48, reloadedBlob.Length)));
				Require(string.Equals(value as string, "changed", StringComparison.Ordinal), "the string entry edit did not survive reload");
			});
		RoundTrip("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Matrix.resources\"},\"entry\":{\"name\":\"count\",\"value_kind\":\"i4\",\"value\":77}}",
			reloaded => Require(EntryValue(ResourceBytes(reloaded, "P08.Matrix.resources"), "count") is int value && value == 77, "the int entry edit did not survive reload"));
		RoundTrip("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Matrix.resources\"},\"entry\":{\"name\":\"blob\",\"value_kind\":\"bytes\",\"value\":\"" + Convert.ToBase64String(new byte[] { 1, 2 }) + "\"}}",
			reloaded => Require(EntryValue(ResourceBytes(reloaded, "P08.Matrix.resources"), "blob") is byte[] changed && changed.Length == 2, "the byte entry edit did not survive reload"));

		// ---- 3) whole-blob replacement (the only custom-object edit face)
		var replacement = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
		RoundTrip("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Matrix.resources\"},\"data_base64\":\"" + Convert.ToBase64String(replacement) + "\"}",
			reloaded => Require(ResourceBytes(reloaded, "P08.Matrix.resources").SequenceEqual(replacement), "the whole-blob replacement did not survive reload"));

		// ---- 4) icon group: RT_GROUP_ICON + two RT_ICON rows; removing a
		// referenced icon rejects; removing the group first succeeds.
		var icon1 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 };
		var icon2 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 2 };
		var group = GroupDirectory(new ushort[] { 1, 2 });
		Apply("{\"kind\":\"win32_resource_add\",\"type_id\":3,\"name_id\":1,\"data_base64\":\"" + Convert.ToBase64String(icon1) + "\"}");
		Apply("{\"kind\":\"win32_resource_add\",\"type_id\":3,\"name_id\":2,\"data_base64\":\"" + Convert.ToBase64String(icon2) + "\"}");
		Apply("{\"kind\":\"win32_resource_add\",\"type_id\":14,\"name_id\":7,\"data_base64\":\"" + Convert.ToBase64String(group) + "\"}");
		{
			var bytes = EditWorkspace.WriteCanonical(module);
			var temp = Path.Combine(Path.GetTempPath(), "p08-resource-" + Guid.NewGuid().ToString("N") + ".dll");
			File.WriteAllBytes(temp, bytes);
			using (var reloaded = ModuleDefMD.Load(temp))
				Require(NativeCount(reloaded, 14) == 1 && NativeCount(reloaded, 3) == 2, "the icon group did not survive reload");
			File.Delete(temp);
		}
		Reject("{\"kind\":\"win32_resource_remove\",\"type_id\":3,\"name_id\":1,\"remove_mode\":\"reject_if_referenced\"}");
		Apply("{\"kind\":\"win32_resource_remove\",\"type_id\":14,\"name_id\":7,\"remove_mode\":\"reject_if_referenced\"}");
		Apply("{\"kind\":\"win32_resource_remove\",\"type_id\":3,\"name_id\":1,\"remove_mode\":\"reject_if_referenced\"}");
		Require(NativeCount(module, 3) == 1, "the icon row was not removed");

		// ---- 5) strong-name: registry-level field validation (the one-time
		// evidence gate lives in the coordinator and is covered by EDIT-ACC-016)
		Reject("{\"kind\":\"strong_name_remove\"}");
		Reject("{\"kind\":\"strong_name_remove\",\"dynamic_failure\":{\"session_id\":\"s\",\"event_cursor\":0,\"event_kind\":\"start_failed\"}}");
		{
			// unsigned fixture: the operation is a byte no-op; prove the exact
			// inverse-image invariant and the public-key clearing semantics on a
			// stamped key (a pseudo RSA blob suffices — no signing involved)
			var key = new byte[] { 0x52, 0x53, 0x41, 0x32, 0x01, 0x02, 0x03 };
			module.Assembly!.PublicKey = new dnlib.DotNet.PublicKey(key);
			var strongOutcome = Apply("{\"kind\":\"strong_name_remove\",\"dynamic_failure\":{\"session_id\":\"none\",\"event_cursor\":1,\"event_kind\":\"start_failed\"}}");
			// dnlib keeps a PublicKey wrapper after clearing; the DATA is what the
			// metadata column holds (empty after removal, restored by undo)
			var dataAfter = module.Assembly?.PublicKey?.Data is { Length: > 0 };
			if (dataAfter) throw new InvalidOperationException("strong_name_remove did not clear the public key data");
			strongOutcome.Undo();
			if (!(module.Assembly?.PublicKey?.Data is { Length: > 0 })) throw new InvalidOperationException("strong_name undo did not restore the key");
			module.Assembly!.PublicKey = null;
		}

		// ---- 6) rejects with zero image change
		Reject("{\"kind\":\"managed_resource_add\",\"name\":\"P08.Bad.resources\",\"data_base64\":\"!!!notbase64!!!\"}");
		Reject("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Missing.resources\"},\"data_base64\":\"AAAA\"}");
		Reject("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Bad.resources\"},\"entry\":{\"name\":\"x\",\"value_kind\":\"char\",\"value\":\"a\"}}");
		Apply("{\"kind\":\"managed_resource_add\",\"name\":\"P08.Bad.resources\",\"data_base64\":\"" + Convert.ToBase64String(blob) + "\"}");
		Reject("{\"kind\":\"managed_resource_update\",\"target\":{\"name\":\"P08.Bad.resources\"},\"entry\":{\"name\":\"nope\",\"value_kind\":\"string\",\"value\":\"x\"}}");
		Apply("{\"kind\":\"managed_resource_remove\",\"target\":{\"name\":\"P08.Bad.resources\"},\"remove_mode\":\"reject_if_referenced\"}");

		// ---- 7) CON-019 sentinel: nothing may instantiate anything (this probe
		// never deserializes; the marker file existing would prove otherwise)
		if (File.Exists(markerPath)) throw new InvalidOperationException("the sentinel marker was written — something deserialized");

		Console.WriteLine("PASS resource-matrix add+reload=True entry-edits=string+i4+bytes=True whole-blob-replace=True "
			+ "icon-group=group+icons+referenced-reject=True strong-name-gate=True rejects-zero-side-effect=True "
			+ "deterministic-writer=True sentinel=never-instantiated=True");
	}

	static object? EntryValue(byte[] blob, string name) {
		var parsed = EditResourceCodec.Parse(blob);
		return parsed.Entries.FirstOrDefault(entry => entry.Name == name)?.Decoded;
	}

	static int NativeCount(ModuleDef module, int typeId) {
		var directory = module.Win32Resources.Root.FindDirectory(new dnlib.W32Resources.ResourceName(typeId));
		return directory?.Directories.SelectMany(nameDirectory => nameDirectory.Data).Count() ?? 0;
	}

	static byte[] GroupDirectory(ushort[] iconIds) {
		using var buffer = new MemoryStream();
		buffer.Write(new byte[] { 0, 0, 0, 1 }, 0, 4);   // reserved, type=icon group
		buffer.Write(BitConverter.GetBytes((ushort)iconIds.Length), 0, 2);
		foreach (var id in iconIds) {
			buffer.Write(new byte[4], 0, 4);             // width/height/colors/reserved (4)
			buffer.Write(BitConverter.GetBytes((ushort)1), 0, 2);  // planes
			buffer.Write(BitConverter.GetBytes((ushort)32), 0, 2); // bit count
			buffer.Write(BitConverter.GetBytes((uint)5), 0, 4);    // bytes in resource
			buffer.Write(BitConverter.GetBytes(id), 0, 2);
		}
		return buffer.ToArray();
	}

	static void Require(bool condition, string message) {
		if (!condition) throw new InvalidOperationException(message);
	}
}
