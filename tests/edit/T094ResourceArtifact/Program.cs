using System.Collections;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;

internal static class Program {
    internal const string ResourceName = "T091.Values.resources";
    static readonly byte[] CustomBytes = { 0, 255, 17 };
    static readonly byte[] OriginalBytes = { 1, 2, 3, 4 };
    static readonly byte[] OriginalStream = { 5, 6, 7 };
    static readonly byte[] EditedBytes = { 9, 8, 7, 6 };
    static readonly byte[] EditedStream = { 10, 11, 12 };

    static int Main(string[] args) {
        try {
            if (args.Length == 4 && args[0] == "verify-matrix") {
                T094MatrixReader.Verify(args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length != 3 || args[0] is not ("make" or "verify" or "verify-blob" or "extract"))
                throw new ArgumentException("usage: T094ResourceArtifact make <source.dll> <fixture.dll> | verify <image.dll> <baseline|first|second> | verify-blob <file.resources> <baseline|first|second> | extract <image.dll> <file.resources>");
            if (args[0] == "make") Make(args[1], args[2]);
            else if (args[0] == "extract") {
                if (File.Exists(args[2])) throw new InvalidOperationException("extract output already exists");
                File.WriteAllBytes(args[2], ResourceBytes(args[1]));
            }
            else if (args[0] == "verify-blob") VerifyBlob(File.ReadAllBytes(args[1]), args[2], args[1], null);
            else Verify(args[1], args[2]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void Make(string source, string output) {
        if (!File.Exists(source) || File.Exists(output)) throw new InvalidOperationException("source absent or output already exists");
        using var resource = new MemoryStream();
        using (var writer = new ResourceWriter(resource)) {
            writer.AddResource("text", "original");
            writer.AddResource("boolean", true);
            writer.AddResource("i1", (object)(sbyte)-7);
            writer.AddResource("u1", (object)(byte)250);
            writer.AddResource("i2", (object)(short)-30000);
            writer.AddResource("u2", (object)(ushort)60000);
            writer.AddResource("i4", (object)-2000000000);
            writer.AddResource("u4", (object)4000000000U);
            writer.AddResource("i8", (object)-9007199254740993L);
            writer.AddResource("u8", (object)18446744073709551610UL);
            writer.AddResource("r4", (object)1.5f);
            writer.AddResource("r8", (object)-2.25d);
            writer.AddResource("bytes", OriginalBytes);
            writer.AddResource("stream", new MemoryStream(OriginalStream));
            writer.AddResource("char", (object)'Q');
            writer.AddResource("date", (object)new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            writer.AddResource("span", (object)TimeSpan.FromMinutes(5));
            writer.AddResource("decimal", (object)12.5m);
            writer.AddResource("null", (object?)null);
            writer.AddResourceData("custom", "T091.Ghost, T091", CustomBytes);
        }
        using var module = ModuleDefMD.Load(Path.GetFullPath(source));
        if (module.Resources.Any(r => r.Name == ResourceName)) throw new InvalidOperationException("fixture resource already exists");
        module.Resources.Add(new EmbeddedResource(ResourceName, resource.ToArray(), ManifestResourceAttributes.Private));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        module.Write(output);
        Verify(output, "baseline");
    }

    static void Verify(string image, string profile) {
        if (profile is not ("baseline" or "first" or "second")) throw new ArgumentException("unknown profile");
        var blob = ResourceBytes(image);
        VerifyBlob(blob, profile, image, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant());
    }

    static byte[] ResourceBytes(string image) {
        using var module = ModuleDefMD.Load(Path.GetFullPath(image));
        var matches = module.Resources.OfType<EmbeddedResource>().Where(r => r.Name == ResourceName).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("resource identity missing or ambiguous");
        return matches[0].CreateReader().ToArray();
    }

    static void VerifyBlob(byte[] blob, string profile, string image, string? imageHash) {
        if (profile is not ("baseline" or "first" or "second")) throw new ArgumentException("unknown profile");
        using var reader = new ResourceReader(new MemoryStream(blob));
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var iterator = reader.GetEnumerator();
        while (iterator.MoveNext()) {
            var key = (string)iterator.Key;
            if (key != "custom") values.Add(key, iterator.Value); // never deserialize the user type
        }
        if (values.Count != 19) throw new InvalidOperationException("safe resource row count differs: " + values.Count);
        var edited = profile != "baseline";
        var expected = new Dictionary<string, object?> {
            ["text"] = "original",
            ["boolean"] = true,
            ["i1"] = (sbyte)-7,
            ["u1"] = (byte)250,
            ["i2"] = (short)-30000,
            ["u2"] = (ushort)60000,
            ["i4"] = -2000000000,
            ["u4"] = 4000000000U,
            ["i8"] = -9007199254740993L,
            ["u8"] = 18446744073709551610UL,
            ["r4"] = 1.5f,
            ["r8"] = -2.25d,
            ["bytes"] = OriginalBytes,
            ["stream"] = OriginalStream,
            ["char"] = !edited ? 'Q' : profile == "second" ? 'Y' : (char)0xD800,
            ["date"] = edited ? new DateTime(2024, 11, 3, 5, 30, 0, DateTimeKind.Utc) : new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ["span"] = edited ? TimeSpan.MinValue : TimeSpan.FromMinutes(5),
            ["decimal"] = edited ? new decimal(0, 0, 0, true, 28) : 12.5m,
            ["null"] = null,
        };
        foreach (var (name, target) in expected) {
            if (!values.TryGetValue(name, out var actual)) throw new InvalidOperationException("missing entry: " + name);
            if (name == "stream") {
                if (actual is not Stream stream || !ReadAll(stream).SequenceEqual((byte[])target!))
                    throw new InvalidOperationException("Stream type or bytes differ");
            }
            else if (target is byte[] bytes) {
                if (actual is not byte[] actualBytes || !actualBytes.SequenceEqual(bytes))
                    throw new InvalidOperationException("ByteArray type or bytes differ: " + name);
            }
            else if (name == "decimal" && (actual is not decimal actualDecimal || !decimal.GetBits(actualDecimal).SequenceEqual(decimal.GetBits((decimal)target!))))
                throw new InvalidOperationException("decimal bits differ");
            else if (name == "date" && (actual is not DateTime actualDate || actualDate.ToBinary() != ((DateTime)target!).ToBinary()))
                throw new InvalidOperationException("DateTime binary differs");
            else if (actual?.GetType() != target?.GetType() || !Equals(actual, target))
                throw new InvalidOperationException($"type/value differ: {name}: {actual?.GetType().FullName} {actual} vs {target}");
        }
        if (edited) {
            var exact = new Dictionary<string, (string Code, byte[] Raw)> {
                ["char"] = ("ResourceTypeCode.Char", BitConverter.GetBytes((ushort)(profile == "second" ? 'Y' : 0xD800))),
                ["decimal"] = ("ResourceTypeCode.Decimal", decimal.GetBits(new decimal(0, 0, 0, true, 28)).SelectMany(BitConverter.GetBytes).ToArray()),
                ["span"] = ("ResourceTypeCode.TimeSpan", BitConverter.GetBytes(long.MinValue)),
                ["date"] = ("ResourceTypeCode.DateTime", BitConverter.GetBytes(new DateTime(2024, 11, 3, 5, 30, 0, DateTimeKind.Utc).ToBinary())),
            };
            foreach (var (name, target) in exact) {
                reader.GetResourceData(name, out var code, out var rawBytes);
                if (code != target.Code || !rawBytes.SequenceEqual(target.Raw))
                    throw new InvalidOperationException("type code or raw bytes differ: " + name);
            }
        }
        reader.GetResourceData("custom", out var typeName, out var raw);
        if (typeName != "T091.Ghost, T091" || !raw.SequenceEqual(CustomBytes))
            throw new InvalidOperationException("custom user-type name or raw bytes changed");
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "PASS", profile, image = Path.GetFullPath(image),
            image_sha256 = imageHash,
            resource_sha256 = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant(),
            value_kinds = 17, storage_codes = 18, preserved_excluded = 2, custom_raw = true,
            stream_clr_type = values["stream"]?.GetType().FullName,
            i8 = values["i8"], u8 = values["u8"],
        }));
    }

    static byte[] ReadAll(Stream stream) { using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
}
