using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Internal representation experiment only. No public raw-edit input is added.
internal static class ConstantSnapshotProbe {
	public sealed class Snapshot {
		public string Kind { get; set; } = "";
		public string Bits { get; set; } = "";
	}
	public static void Run(string fixture) {
		var singles = new uint[] { 0x80000000, 0x7f800000, 0xff800000, 0x7fc12345, 0xffc54321, 0x00000001 };
		var doubles = new ulong[] { 0x8000000000000000, 0x7ff0000000000000, 0xfff0000000000000, 0x7ff8123456789abc, 0xfff854321abcdef0, 1 };
		var verified = 0;
		foreach (var bits in singles.Select(b => new Snapshot { Kind = "r4", Bits = b.ToString("x8") })
			.Concat(doubles.Select(b => new Snapshot { Kind = "r8", Bits = b.ToString("x16") }))) {
			object Decode(Snapshot value) => value.Kind switch {
				"r4" => (object)BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(value.Bits, 16))),
				"r8" => BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(value.Bits, 16))),
				_ => throw new InvalidDataException("Unknown constant kind"),
			};
			using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
			var owner = module.GetTypes().Single(t => t.FullName == "TestIL.Members");
			var field = new FieldDefUser("P03ConstantSnapshot", new FieldSig(bits.Kind == "r4" ? module.CorLibTypes.Single : module.CorLibTypes.Double),
				FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault) { Constant = new ConstantUser(Decode(bits)) };
			owner.Fields.Add(field);
			var before = EditWorkspace.WriteCanonical(module);
			var json = JsonSerializer.Serialize(bits);
			owner.Fields.Remove(field);
			using var reloaded = ModuleDefMD.Load(EditWorkspace.WriteCanonical(module));
			var restored = JsonSerializer.Deserialize<Snapshot>(json)!;
			var restoredField = new FieldDefUser(field.Name, new FieldSig(bits.Kind == "r4" ? reloaded.CorLibTypes.Single : reloaded.CorLibTypes.Double), field.Attributes) {
				Constant = new ConstantUser(Decode(restored)),
			};
			reloaded.GetTypes().Single(t => t.FullName == "TestIL.Members").Fields.Add(restoredField);
			if (!before.SequenceEqual(EditWorkspace.WriteCanonical(reloaded))) throw new InvalidOperationException("Constant image changed: " + bits.Kind + ":" + bits.Bits);
			var recovered = restoredField.Constant.Value;
			var actual = recovered is float single ? unchecked((uint)BitConverter.SingleToInt32Bits(single)).ToString("x8")
				: unchecked((ulong)BitConverter.DoubleToInt64Bits((double)recovered)).ToString("x16");
			if (actual != bits.Bits) throw new InvalidOperationException("Constant bits changed");
			verified++;
		}
		Console.WriteLine($"SPIKE float-constant-snapshot cases={verified} negative-zero+infinity+nan-payload+subnormal=True json-roundtrip=True delete-reload-restore-exact=True");
	}
}
