using dnlib.DotNet;
using dnlib.DotNet.Writer;

if (args.Length != 2)
	throw new ArgumentException("usage: P02FixtureMutator <input> <output>");

using var module = ModuleDefMD.Load(args[0]);
var target = module.GetTypes().SelectMany(type => type.Methods)
	.Single(method => method.Name == "ParameterUpdateTarget");
target.ParamDefs.Add(new ParamDefUser("duplicate", 1));

var assemblyName = Path.GetFileNameWithoutExtension(args[1]);
module.Assembly.Name = assemblyName;
module.Name = Path.GetFileName(args[1]);
var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
options.MetadataOptions.Flags = MetadataFlags.PreserveAll | MetadataFlags.KeepOldMaxStack;
module.Write(args[1], options);
