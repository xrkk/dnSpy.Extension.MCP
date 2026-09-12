using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// P07 --identity-matrix: headless exercise of the four identity operations.
// Every field edit applies, writes, reloads and reads back at file level (the
// adjudicated authoritative check — AUD-002), the compiled inverse restores the
// exact pre-state image, and invalid payloads reject without side effects.
internal static class IdentityMatrixProbe {
	public static void Run(string fixture) {
		Environment.SetEnvironmentVariable("DNMCP_TEST", "1");
		var path = Path.GetFullPath(fixture);
		using var module = ModuleDefMD.Load(path);
		var baseline = EditWorkspace.WriteCanonical(module);
		var objects = new Dictionary<string, IMDTokenProvider>();
		var index = 0;

		EditOperationOutcome Apply(string json) {
			using var document = JsonDocument.Parse(json);
			var outcome = EditOperationRegistry.Apply(module, document.RootElement, objects, index++);
			EditStructuralValidator.Validate(module);
			return outcome;
		}
		EditOperationOutcome Reject(string json) {
			var before = EditWorkspace.WriteCanonical(module);
			try {
				using var document = JsonDocument.Parse(json);
				EditOperationRegistry.Apply(module, document.RootElement, objects, index++);
				throw new InvalidOperationException("invalid identity payload was accepted: " + json);
			}
			catch (EditDomainException ex) when (ex.Code == "EDIT_VALIDATION_FAILED") { }
			catch (ArgumentException) { }
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("a rejected identity payload changed the module: " + json);
			return null!;
		}
		void RoundTrip(string json, Action<ModuleDefMD> assertReloaded, Action<ModuleDef> assertApplied, bool allowNoChange = false) {
			var before = EditWorkspace.WriteCanonical(module);
			using var forward = JsonDocument.Parse(json);
			var inverse = EditOperationRegistry.CompileInverse(module, forward.RootElement, objects);
			var outcome = Apply(json);
			assertApplied(module);
			if (!allowNoChange && EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("identity edit produced no image change: " + json);
			// compiled inverse restores the exact pre-state image (checkpoint path)
			EditOperationRegistry.ApplyCompiledInverse(module,
				JsonDocument.Parse(JsonSerializer.Serialize(inverse)).RootElement, objects, index++);
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("identity inverse did not restore the exact image: " + json);
			// live undo path then the reload readback on a fresh application
			Apply(json);
			var bytes = EditWorkspace.WriteCanonical(module);
			var temp = Path.Combine(Path.GetTempPath(), "p07-identity-" + Guid.NewGuid().ToString("N") + ".dll");
			File.WriteAllBytes(temp, bytes);
			using (var reloaded = ModuleDefMD.Load(temp)) assertReloaded(reloaded);
			File.Delete(temp);
			outcome.Undo();
			if (!EditWorkspace.WriteCanonical(module).SequenceEqual(before))
				throw new InvalidOperationException("identity undo did not restore the exact image: " + json);
		}

		var originalName = module.Assembly!.Name.String;

		RoundTrip("{\"kind\":\"assembly_update\",\"name\":\"P07Renamed\"}",
			reloaded => Require(reloaded.Assembly?.Name.String == "P07Renamed", "assembly name did not survive reload"),
			applied => Require(applied.Assembly!.Name.String == "P07Renamed", "assembly name not applied"));
		RoundTrip("{\"kind\":\"assembly_update\",\"version\":\"7.8.9.10\"}",
			reloaded => Require(reloaded.Assembly?.Version?.ToString() == "7.8.9.10", "assembly version did not survive reload"),
			applied => Require(applied.Assembly!.Version?.ToString() == "7.8.9.10", "assembly version not applied"));
		RoundTrip("{\"kind\":\"assembly_update\",\"version\":\"2.3\"}",
			reloaded => Require(reloaded.Assembly?.Version?.ToString() == "2.3.0.0", "partial version not normalized"),
			applied => Require(applied.Assembly!.Version?.ToString() == "2.3.0.0", "partial version not applied"));
		RoundTrip("{\"kind\":\"assembly_update\",\"culture\":\"zh-CN\"}",
			reloaded => Require(reloaded.Assembly?.Culture.String == "zh-CN", "culture did not survive reload"),
			applied => Require(applied.Assembly!.Culture.String == "zh-CN", "culture not applied"));
		RoundTrip("{\"kind\":\"module_update\",\"name\":\"P07Module\"}",
			reloaded => Require(reloaded.Name.String == "P07Module", "module name did not survive reload"),
			applied => Require(applied.Name.String == "P07Module", "module name not applied"));

		var reference = module.GetAssemblyRefs().First();
		RoundTrip("{\"kind\":\"assembly_ref_update\",\"target\":{\"token\":\"0x" + reference.MDToken.Raw.ToString("x8") + "\"},\"version\":\"3.4.5.6\"}",
			reloaded => Require(reloaded.GetAssemblyRefs().First().Version?.ToString() == "3.4.5.6", "assembly ref version did not survive reload"),
			applied => Require(applied.GetAssemblyRefs().First().Version?.ToString() == "3.4.5.6", "assembly ref version not applied"));

		var entry = module.GetTypes().First(t => t.Methods.Any(m => m.IsStatic && m.HasBody)).Methods.First(m => m.IsStatic && m.HasBody);
		var entryToken = "0x" + entry.MDToken.Raw.ToString("x8");
		RoundTrip("{\"kind\":\"entry_point_set\",\"entry_point\":{\"token\":\"" + entryToken + "\"}}",
			reloaded => Require(reloaded.ManagedEntryPoint?.MDToken.Raw == entry.MDToken.Raw, "entry point did not survive reload"),
			applied => Require(applied.ManagedEntryPoint?.MDToken.Raw == entry.MDToken.Raw, "entry point not applied"));
		// a library fixture carries no entry point, so clearing is a no-op image-wise
		RoundTrip("{\"kind\":\"entry_point_set\",\"entry_point\":null}",
			reloaded => Require(reloaded.ManagedEntryPoint == null, "entry point clear did not survive reload"),
			applied => Require(applied.ManagedEntryPoint == null, "entry point not cleared"), allowNoChange: true);

		// invalid payloads: zero side effects
		Reject("{\"kind\":\"assembly_update\"}");
		Reject("{\"kind\":\"assembly_update\",\"version\":\"1.x\"}");
		Reject("{\"kind\":\"assembly_update\",\"name\":\"\"}");
		Reject("{\"kind\":\"module_update\",\"name\":\"\"}");
		Reject("{\"kind\":\"assembly_ref_update\",\"target\":{\"token\":\"" + TypeToken(module) + "\"},\"name\":\"Nope\"}");
		Reject("{\"kind\":\"entry_point_set\",\"entry_point\":{\"token\":\"" + FieldToken(module) + "\"}}");

		// combined sequence image-level readback (AUD-002: replay consistency rides the image)
		Apply("{\"kind\":\"assembly_update\",\"name\":\"P07Combined\",\"version\":\"5.6.7.8\",\"culture\":\"neutral\"}");
		Apply("{\"kind\":\"module_update\",\"name\":\"P07CombinedModule\"}");
		Apply("{\"kind\":\"entry_point_set\",\"entry_point\":{\"token\":\"" + entryToken + "\"}}");
		var combined = EditWorkspace.WriteCanonical(module);
		var combinedPath = Path.Combine(Path.GetTempPath(), "p07-combined-" + Guid.NewGuid().ToString("N") + ".dll");
		File.WriteAllBytes(combinedPath, combined);
		using (var combinedReload = ModuleDefMD.Load(combinedPath)) {
			Require(combinedReload.Assembly?.Name.String == "P07Combined"
				&& combinedReload.Assembly.Version?.ToString() == "5.6.7.8"
				&& combinedReload.Name.String == "P07CombinedModule"
				&& combinedReload.ManagedEntryPoint?.MDToken.Raw == entry.MDToken.Raw,
				"combined identity sequence did not survive reload");
		}
		File.Delete(combinedPath);

		Console.WriteLine("PASS identity-matrix assembly=name+version+partial+culture=True module=name=True "
			+ "assembly_ref=version=True entry=set+clear=True inverse=exact-image=True "
			+ "rejects=6-zero-side-effect=True combined-image-readback=True");
	}

	static string TypeToken(ModuleDef module) {
		var type = module.GetTypes().First(t => t.MDToken.Raw != 0x02000001);
		return "0x" + type.MDToken.Raw.ToString("x8");
	}
	static string FieldToken(ModuleDef module) {
		var field = module.GetTypes().SelectMany(t => t.Fields).First();
		return "0x" + field.MDToken.Raw.ToString("x8");
	}
	static void Require(bool condition, string message) {
		if (!condition) throw new InvalidOperationException(message);
	}
}
