using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

internal static class MarshalSnapshotProbe {
	sealed class WriterError : IWriterError { public void Error(string message) => throw new InvalidDataException(message); }
	public static void Run(string fixture) {
		using var module = ModuleDefMD.Load(Path.GetFullPath(fixture));
		var type = new TypeRefUser(module, "Example", "Marshaler", module.CorLibTypes.AssemblyRef);
		var refs = new Dictionary<string, IMDTokenProvider>();
		string Bind(IMDTokenProvider v) { if (!ReferenceEquals(v, type)) throw new InvalidDataException("Unexpected reference"); refs["type"] = v; return "type"; }
		var samples = new MarshalType[] {
			new MarshalType(NativeType.I4), new RawMarshalType(new byte[] { 0xff, 0x42 }),
			new FixedSysStringMarshalType(), new FixedSysStringMarshalType(16),
			new SafeArrayMarshalType(), new SafeArrayMarshalType((VariantType)36, type),
			new FixedArrayMarshalType(), new FixedArrayMarshalType(4, NativeType.I4),
			new ArrayMarshalType(), new ArrayMarshalType(NativeType.I4, 1, 3, 1),
			new CustomMarshalType(), new CustomMarshalType("guid", "native", type, new UTF8String(new byte[] { 65, 0xff })),
			new InterfaceMarshalType(NativeType.IUnknown), new InterfaceMarshalType(NativeType.IDispatch, 2),
		};
		foreach (var original in samples) {
			var json = JsonSerializer.Serialize(MarshalSnapshotCandidate.Capture(original, Bind));
			var copy = MarshalSnapshotCandidate.Restore(JsonSerializer.Deserialize<MarshalSnapshotCandidate.Node>(json)!, id => refs[id]);
			if (copy.GetType() != original.GetType() || json != JsonSerializer.Serialize(MarshalSnapshotCandidate.Capture(copy, Bind))) throw new InvalidOperationException("Marshal shape lost");
			if (ReferenceEquals(original, samples[10])) {
				// Default CustomMarshalType has null required UTF8 fields. Keep it
				// as a negative sample; neither original nor restored may write.
				foreach (var invalid in new[] { original, copy }) {
					try { MarshalBlobWriter.Write(module, invalid, new WriterError()); }
					catch (InvalidDataException e) when (e.Message == "UTF8String is null") { continue; }
					throw new InvalidOperationException("Invalid custom marshal unexpectedly wrote");
				}
				continue;
			}
			if (!MarshalBlobWriter.Write(module, original, new WriterError()).SequenceEqual(MarshalBlobWriter.Write(module, copy, new WriterError()))) throw new InvalidOperationException("Marshal bytes lost");
		}
		Console.WriteLine("SPIKE marshal-snapshot samples=14 classes=8 parsed+raw+sentinels+reference+utf8=True independent-dnlib-bytes=13 invalid-default-custom-rejected=1");
	}
}
