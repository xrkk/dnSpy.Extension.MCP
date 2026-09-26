using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

static class Program {
	static int checks;
	static void Check(bool condition, string name) {
		if (!condition) throw new Exception("FAIL " + name);
		checks++;
	}
	static void Invalid(Action action, string name) {
		try { action(); }
		catch (ArgumentException) { checks++; return; }
		throw new Exception("FAIL accepted " + name);
	}
	static void Main(string[] args) {
		if (args.Length != 1) throw new ArgumentException("schema path required");
		using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
		var schema = document.RootElement.GetProperty("edit_begin").GetProperty("inputSchema");
		void Valid(string json, string name) {
			using var request = JsonDocument.Parse(json);
			EditJsonSchemaValidator.ValidateValue(schema, request.RootElement, name);
			checks++;
		}
		void Bad(string json, string name) {
			using var request = JsonDocument.Parse(json);
			Invalid(() => EditJsonSchemaValidator.ValidateValue(schema, request.RootElement, name), name);
		}
		const string mvid = "cec6a9a1-2d28-4010-88ed-b469551880f4";
		Valid("{\"request_id\":\"r\",\"assembly_name\":\"Pure\"}", "old no MVID");
		Valid("{\"request_id\":\"r\",\"assembly_name\":\"Pure\",\"module_mvid\":\"not-a-guid\",\"source_family_id\":\"family-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "old permissive MVID");
		Valid("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"module_mvid\":\""+mvid+"\"}", "new exact");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\"}", "new missing MVID");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"module_mvid\":\"\"}", "new empty MVID");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"module_mvid\":\"garbage\"}", "new malformed MVID");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"module_mvid\":\"00000000-0000-0000-0000-000000000000\"}", "new zero MVID");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"\",\"module_mvid\":\""+mvid+"\"}", "empty name");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"   \",\"module_mvid\":\""+mvid+"\"}", "blank name");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"assembly_name\":\"Pure\",\"module_mvid\":\""+mvid+"\"}", "mixed selectors");
		Bad("{\"request_id\":\"r\",\"netmodule_name\":\"BoundaryModule.netmodule\",\"source_family_id\":\"family-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"module_mvid\":\""+mvid+"\"}", "new family forbidden");
		Bad("{\"request_id\":\"r\",\"assembly_name\":\"\"}", "old empty assembly");
		Bad("{\"request_id\":\"r\"}", "missing selector");

		var id = EditTargetResolver.DiagnosticMvid(mvid);
		Check(id == Guid.Parse(mvid), "parse D GUID");
		Invalid(() => EditTargetResolver.DiagnosticMvid("{cec6a9a1-2d28-4010-88ed-b469551880f4}"), "braced GUID");
		Invalid(() => EditTargetResolver.DiagnosticMvid(Guid.Empty.ToString("D")), "zero GUID runtime");
		var standalone = new ModuleDefUser("BoundaryModule.netmodule") { Mvid = id };
		var pure = new ModuleDefUser("Pure.dll") { Kind = ModuleKind.Dll, Mvid = id };
		new AssemblyDefUser("Pure", new Version(1,0)).Modules.Add(pure);
		var attached = new ModuleDefUser("BoundaryModule.netmodule") { Mvid = id };
		var manifest = new ModuleDefUser("Manifest.dll") { Kind = ModuleKind.Dll, Mvid = Guid.NewGuid() };
		var multifile = new AssemblyDefUser("Manifest", new Version(1,0));
		multifile.Modules.Add(manifest); multifile.Modules.Add(attached);
		Check(EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {standalone,pure,attached}, "boundarymodule.NETMODULE", id) == 1, "exact case insensitive, ignores attached");
		Check(EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {standalone}, "wrong", id) == 0, "wrong name");
		Check(EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {standalone}, "BoundaryModule.netmodule", Guid.NewGuid()) == 0, "wrong MVID");
		Check(EditTargetResolver.StandaloneMatchCount(Array.Empty<ModuleDef>(), "BoundaryModule.netmodule", id) == 0, "unloaded");
		Check(EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {attached}, "BoundaryModule.netmodule", id) == 0, "attached only");
		var duplicate = new ModuleDefUser("boundaryMODULE.netmodule") { Mvid = id };
		Check(EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {standalone,duplicate}, "BoundaryModule.netmodule", id) == 2, "duplicate ambiguity");
		Invalid(() => EditTargetResolver.StandaloneMatchCount(new ModuleDef[] {standalone}, "  ", id), "blank runtime name");
		Check(ReferenceEquals(EditTargetResolver.AssemblyModule(new ModuleDef[] {pure,standalone}, "PURE", null),pure), "old assembly positive");
		Check(ReferenceEquals(EditTargetResolver.AssemblyModule(new ModuleDef[] {pure}, "Pure", mvid),pure), "old MVID positive");
		Check(EditTargetResolver.AssemblyModule(new ModuleDef[] {pure}, "Pure", "not-a-guid") == null, "old MVID still plain string");
		var same = new ModuleDefUser("Pure2.dll") { Mvid = id };
		new AssemblyDefUser("Pure", new Version(1,0)).Modules.Add(same);
		Check(EditTargetResolver.AssemblyModule(new ModuleDef[] {pure,same}, "Pure", null) == null, "old same-name ambiguous");
		Check(EditTargetResolver.AssemblyModule(new ModuleDef[] {pure,same}, "Pure", mvid) == null, "old same-name same-MVID ambiguous");
		Check(EditTargetResolver.AssemblyModule(new ModuleDef[] {manifest,attached}, "Manifest", manifest.Mvid?.ToString("D")) == manifest, "old multifile manifest address");
		Console.WriteLine("PASS T078 resolver/schema " + checks + " checks");
	}
}
