using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;

if (args.Length != 2) throw new ArgumentException("usage: GenVariant <legacy-head.dll> <derived.dll>");
using var module = ModuleDefMD.Load(args[0]);
var selected = module.GetTypes().SelectMany(type => type.Methods)
	.First(method => method.IsPublic && method.IsStatic && !method.IsConstructor);
module.ManagedEntryPoint = selected;
var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
options.MetadataOptions.Flags = MetadataFlags.PreserveRids | MetadataFlags.PreserveExtraSignatureData | MetadataFlags.KeepOldMaxStack;
if (module.PdbState is { } state) {
	state.PdbFileKind = PdbFileKind.EmbeddedPortablePDB;
	options.WritePdb = true;
	options.PdbFileName = "embedded.pdb";
	options.PdbOptions |= PdbWriterOptions.Deterministic;
}
using var output = File.Create(args[1]);
module.Write(output, options);
Console.WriteLine($"entrypoint={selected.FullName} mvid={module.Mvid}");
