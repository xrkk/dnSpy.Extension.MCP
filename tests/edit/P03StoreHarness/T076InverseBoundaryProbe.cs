using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Component-level regression for the generic inverse branch added in T076 R02.
internal static class T076InverseBoundaryProbe {
	static void Check(bool value, string label) {
		if (!value) throw new InvalidOperationException("T076_BOUNDARY: " + label);
	}

	static string AddJson(TypeDef owner, string name, string? document = null) => JsonSerializer.Serialize(new {
		kind = "method_add", owner_type = new { token = "0x" + owner.MDToken.Raw.ToString("x8") }, name,
		attributes = 150,
		signature = new { return_type = "System.Void", has_this = false, parameters = Array.Empty<object>(), generic_parameters = Array.Empty<object>() },
		body = new {
			init_locals = false, max_stack = 1, locals = Array.Empty<object>(), exception_handlers = Array.Empty<object>(),
			instructions = new[] { new { opcode = "ret" } },
			sequence_points = document == null ? Array.Empty<object>() : new object[] { new {
				document = new { name = document, language = "3f5162f8-07c6-11d3-9053-00c04fa302a1",
					vendor = "994b45c4-e6e9-11d2-903f-00c04fa302a1", hash = "AQID",
					type = "5a869d0b-6611-11d3-bd2a-0000f80849bd", hashAlgorithm = "ff1816ec-aa5e-4d10-87f7-6f4963833460" },
				start = new { il = 0, line = 1, column = 1 }, end = new { il = 0, line = 1, column = 2 },
			} },
		},
	});

	static string Add(ModuleDef module, TypeDef owner, string name, string? document = null) {
		using var op = JsonDocument.Parse(AddJson(owner, name, document));
		var map = new Dictionary<string, IMDTokenProvider>();
		var inverse = EditOperationRegistry.CompileInverse(module, op.RootElement, map);
		EditOperationRegistry.Apply(module, op.RootElement, map, 0);
		return JsonSerializer.Serialize(inverse, EditWire.JsonOptions);
	}

	static MethodDef Method(ModuleDef module, string name) => module.GetTypes().SelectMany(type => type.Methods)
		.Single(method => method.Name.String == name);

	static int Tombstones(ModuleDef module) => module.GetTypes().Count(EditDeletedRowsTombstone.IsTombstone);
	static byte[] Image(ModuleDef module) => EditWorkspace.WriteCheckpointImage(module);
	static uint MaxRid(ModuleDef module) => module.GetTypes().SelectMany(type => type.Methods).Max(method => method.MDToken.Rid);

	static void Inverse(ModuleDef module, string serialized, string targetName, bool expectTombstone,
		byte[]? exactAncestor, string? liveName = null, string? document = null) {
		var target = Method(module, targetName);
		var targetOwner = target.DeclaringType;
		var targetRid = target.MDToken.Raw;
		var live = liveName == null ? null : Method(module, liveName);
		var liveToken = live?.MDToken.Raw;
		var before = Image(module);
		var beforeTombstones = Tombstones(module);
		using var inverse = JsonDocument.Parse(serialized);
		var undo = EditOperationRegistry.ApplyCompiledInverse(module, inverse.RootElement,
			new Dictionary<string, IMDTokenProvider>(), 0).Undo;
		Check(!targetOwner.Methods.Contains(target), targetName + " removed from owner");
		Check(Tombstones(module) == beforeTombstones + (expectTombstone ? 1 : 0), targetName + " tombstone count");
		if (expectTombstone) Check(module.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone)
			.SelectMany(type => type.Methods).Any(method => ReferenceEquals(method, target)), targetName + " row retained");
		if (live != null) Check(ReferenceEquals(Method(module, liveName!), live) && live.MDToken.Raw == liveToken,
			liveName! + " live method object and token survive");
		if (document != null) Check(module.PdbState?.Documents.Count(d => d.Url == document) == 1
			&& ReferenceEquals(Method(module, liveName!).Body.Instructions[0].SequencePoint?.Document,
				module.PdbState.Documents.Single(d => d.Url == document)), "live shared document retained");
		var removed = Image(module);
		using (var reloaded = ModuleDefMD.Load(removed)) {
			Check(Tombstones(reloaded) == Tombstones(module), "export/reload tombstone structure");
			if (live != null) Check(Method(reloaded, liveName!).MDToken.Raw == liveToken, "export/reload surviving token");
			if (document != null) Check(reloaded.PdbState?.Documents.Count(d => d.Url == document) == 1
				&& ReferenceEquals(Method(reloaded, liveName!).Body.Instructions[0].SequencePoint?.Document,
					reloaded.PdbState.Documents.Single(d => d.Url == document)),
				"export/reload live shared document reference");
		}
		if (exactAncestor != null) Check(Image(module).SequenceEqual(exactAncestor), targetName + " exact ancestor image");
		undo();
		Check(ReferenceEquals(Method(module, targetName), target) && target.MDToken.Raw == targetRid,
			targetName + " compensation same object and token");
		Check(Image(module).SequenceEqual(before), targetName + " compensation exact image");
		Console.WriteLine($"T076_CASE {targetName} rid={targetRid:x8} max={MaxRid(module)} tombstone={expectTombstone} ancestor_exact={exactAncestor != null} compensation_exact=true");
	}

	public static void Run(string fixture) {
		// The probe asserts the historical v2 tombstone representation
		// (Object-marker re-owning); the formal v3 encoding only recognizes
		// tombstones through the verified-module binding, so run the whole
		// boundary cycle inside the legacy representation scope.
		using var legacyScope = EditDeletedRowsTombstone.UseLegacy(true);
		using var seed = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture));
		var owners = seed.GetTypes().Where(type => type.MDToken.Rid != 0 && !EditDeletedRowsTombstone.IsTombstone(type)).ToArray();
		var earlier = owners.Single(type => type.FullName == "TestIL.Members");
		var later = owners.Last(type => type.MDToken.Rid > earlier.MDToken.Rid && type.FullName != "TestIL.Members");
		var baseline = Image(seed);
		var survivor = seed.GetTypes().SelectMany(type => type.Methods)
			.GroupBy(method => method.Name.String).First(group => group.Count() == 1).Single();
		var survivorToken = survivor.MDToken.Raw;
		var ridZeroInverse = Add(seed, earlier, "T076RidZero");
		Check(Method(seed, "T076RidZero").MDToken.Rid == 0, "new method has RID0 before write");
		Inverse(seed, ridZeroInverse, "T076RidZero", false, baseline, survivor.Name.String);
		Check(ReferenceEquals(Method(seed, survivor.Name.String), survivor) && survivor.MDToken.Raw == survivorToken,
			"RID0 preserved existing method object/token");

		using (var maxSource = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture))) {
			var ancestor = Image(maxSource);
			var inverse = Add(maxSource, maxSource.GetTypes().Single(type => type.FullName == "TestIL.Members"), "T076Max");
			using var landed = ModuleDefMD.Load(Image(maxSource));
			Check(Method(landed, "T076Max").MDToken.Rid == MaxRid(landed), "persisted new max RID");
			Inverse(landed, inverse, "T076Max", false, ancestor, survivor.Name.String);
		}

		using (var nonMaxSource = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture))) {
			var firstOwner = nonMaxSource.GetTypes().Single(type => type.FullName == earlier.FullName);
			var secondOwner = nonMaxSource.GetTypes().Single(type => type.FullName == later.FullName);
			var inverse = Add(nonMaxSource, firstOwner, "T076NonMax", "T076Shared.cs");
			Add(nonMaxSource, secondOwner, "T076Live", "T076Shared.cs");
			using var landed = ModuleDefMD.Load(Image(nonMaxSource));
			Check(Method(landed, "T076NonMax").MDToken.Rid < MaxRid(landed), "persisted new nonmax RID");
			Inverse(landed, inverse, "T076NonMax", true, null, "T076Live", "T076Shared.cs");
		}

		using (var removeSource = ModuleDefMD.Load(System.IO.Path.GetFullPath(fixture))) {
			Add(removeSource, removeSource.GetTypes().Single(type => type.FullName == "TestIL.Members"), "T076PublicMax");
			using var landed = ModuleDefMD.Load(Image(removeSource));
			var target = Method(landed, "T076PublicMax");
			Check(target.MDToken.Rid == MaxRid(landed), "public remove target is persisted max RID");
			var before = Image(landed);
			using var operation = JsonDocument.Parse(JsonSerializer.Serialize(new {
				kind = "method_remove", target = new { token = "0x" + target.MDToken.Raw.ToString("x8") },
				remove_mode = "reject_if_referenced",
			}));
			var inverse = EditOperationRegistry.CompileInverse(landed, operation.RootElement,
				new Dictionary<string, IMDTokenProvider>());
			var undo = EditOperationRegistry.Apply(landed, operation.RootElement,
				new Dictionary<string, IMDTokenProvider>(), 0).Undo;
			Check(Tombstones(landed) == 1 && landed.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone)
				.SelectMany(type => type.Methods).Any(method => ReferenceEquals(method, target)), "public max row retained");
			using (var reloaded = ModuleDefMD.Load(Image(landed))) {
				Check(Tombstones(reloaded) == 1 && reloaded.GetTypes().Where(EditDeletedRowsTombstone.IsTombstone)
					.SelectMany(type => type.Methods).Any(method => method.MDToken.Raw == target.MDToken.Raw),
					"public max row retained after export/reload");
				using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(inverse, EditWire.JsonOptions));
				EditOperationRegistry.ApplyCompiledInverse(reloaded, serialized.RootElement,
					new Dictionary<string, IMDTokenProvider>(), 0);
				Check(Image(reloaded).SequenceEqual(before), "public max row reloaded inverse restores exact image");
			}
			undo();
			Check(ReferenceEquals(Method(landed, "T076PublicMax"), target) && Image(landed).SequenceEqual(before),
				"public remove compensation same object and exact image");
			Console.WriteLine($"T076_CASE public-remove rid={target.MDToken.Raw:x8} tombstone=true compensation_exact=true inverse={JsonSerializer.Serialize(inverse).Length}");
		}
		Console.WriteLine("PASS t076-inverse-boundary RID0+persisted-max+persisted-nonmax+public-max");
	}
}
