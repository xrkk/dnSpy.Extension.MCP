using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;

// Independent BCL readback of each public checkpoint.  The expected JSON is
// produced by the public driver from its frozen vector table, while the image
// and baseline are read here in a separate process.
internal static class T094MatrixReader {
    const string ResourceName = "T091.Values.resources";
    static readonly string[] NewNames = { "char", "decimal", "span", "date" };

    public static void Verify(string imageOrBlob, string baselineImage, string expectedFile) {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(expectedFile));
        var profile = expected.RootElement.GetProperty("profile").GetString()!;
        var changes = expected.RootElement.GetProperty("changes");
        var actualBytes = imageOrBlob.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(imageOrBlob) : ResourceBytes(imageOrBlob);
        var baselineBytes = ResourceBytes(baselineImage);
        using var actual = new ResourceReader(new MemoryStream(actualBytes));
        using var baseline = new ResourceReader(new MemoryStream(baselineBytes));
        var actualValues = Values(actual);
        var baselineValues = Values(baseline);
        if (actualValues.Count != 19 || baselineValues.Count != 19 ||
            !actualValues.Keys.OrderBy(x => x).SequenceEqual(baselineValues.Keys.OrderBy(x => x)))
            throw new InvalidOperationException("resource keys/count differ");
        var preserved = 0;
        foreach (var (name, before) in baselineValues) {
            actual.GetResourceData(name, out var code, out var raw);
            baseline.GetResourceData(name, out var oldCode, out var oldRaw);
            if (name == "text") {
                var want = expected.RootElement.GetProperty("text").GetString();
                if (code != "ResourceTypeCode.String" || actualValues[name] is not string got || got != want)
                    throw new InvalidOperationException("old v1 string value differs");
                continue;
            }
            if (changes.TryGetProperty(name, out var row)) {
                CheckChanged(name, row, actualValues[name], code, raw);
                continue;
            }
            if (code != oldCode || !raw.SequenceEqual(oldRaw) || !ValueEqual(actualValues[name], before))
                throw new InvalidOperationException("untouched type/raw/BCL value differs: " + name);
            preserved++;
        }
        actual.GetResourceData("custom", out var customCode, out var customRaw);
        baseline.GetResourceData("custom", out var baselineCode, out var baselineRaw);
        if (customCode != baselineCode || !customRaw.SequenceEqual(baselineRaw))
            throw new InvalidOperationException("custom raw changed");
        preserved++;
        if (NewNames.Any(name => !actualValues.ContainsKey(name))) throw new InvalidOperationException("new-kind row absent");
        Console.WriteLine(JsonSerializer.Serialize(new {
            status = "PASS", profile, image = Path.GetFullPath(imageOrBlob),
            image_sha256 = Hex(File.ReadAllBytes(imageOrBlob)), resource_sha256 = Hex(actualBytes),
            timezone = TimeZoneInfo.Local.Id, edited = changes.EnumerateObject().Select(x => x.Name).ToArray(),
            preserved_raw_rows = preserved, value_kinds = 17, storage_codes = 18,
            custom_raw = true,
        }));
    }

    static void CheckChanged(string name, JsonElement row, object? value, string code, byte[] raw) {
        byte[] wanted;
        string wantedCode;
        switch (name) {
        case "char":
            var unit = row.GetProperty("code_unit").GetInt32();
            wantedCode = "ResourceTypeCode.Char";
            wanted = BitConverter.GetBytes((ushort)unit);
            if (value is not char c || c != unit) throw new InvalidOperationException("char BCL code unit differs");
            break;
        case "decimal":
            var dec = new decimal(unchecked((int)row.GetProperty("lo").GetUInt32()),
                unchecked((int)row.GetProperty("mid").GetUInt32()),
                unchecked((int)row.GetProperty("hi").GetUInt32()),
                row.GetProperty("negative").GetBoolean(), (byte)row.GetProperty("scale").GetInt32());
            var bits = decimal.GetBits(dec);
            wantedCode = "ResourceTypeCode.Decimal";
            wanted = bits.SelectMany(BitConverter.GetBytes).ToArray();
            if (value is not decimal d || !decimal.GetBits(d).SequenceEqual(bits))
                throw new InvalidOperationException("decimal BCL bits differ");
            break;
        case "span":
            var ticks = long.Parse(row.GetProperty("ticks").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            wantedCode = "ResourceTypeCode.TimeSpan";
            wanted = BitConverter.GetBytes(ticks);
            if (value is not TimeSpan span || span.Ticks != ticks)
                throw new InvalidOperationException("timespan BCL ticks differ");
            break;
        case "date":
            var binary = long.Parse(row.GetProperty("binary").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            wantedCode = "ResourceTypeCode.DateTime";
            wanted = BitConverter.GetBytes(binary);
            if (value is not DateTime date || date.ToBinary() != binary ||
                date.Kind.ToString() != row.GetProperty("kind").GetString())
                throw new InvalidOperationException("datetime BCL binary/kind differ");
            break;
        default: throw new InvalidOperationException("unknown changed row: " + name);
        }
        if (code != wantedCode || !raw.SequenceEqual(wanted))
            throw new InvalidOperationException("changed type/raw differs: " + name);
    }

    static Dictionary<string, object?> Values(ResourceReader reader) {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var cursor = reader.GetEnumerator();
        while (cursor.MoveNext()) {
            var name = (string)cursor.Key;
            if (name != "custom") result.Add(name, cursor.Value); // never instantiate user type
        }
        return result;
    }

    static bool ValueEqual(object? x, object? y) {
        if (x is Stream xs && y is Stream ys) return Bytes(xs).SequenceEqual(Bytes(ys));
        if (x is byte[] xb && y is byte[] yb) return xb.SequenceEqual(yb);
        if (x is decimal xd && y is decimal yd) return decimal.GetBits(xd).SequenceEqual(decimal.GetBits(yd));
        if (x is DateTime xt && y is DateTime yt) return xt.ToBinary() == yt.ToBinary();
        return x?.GetType() == y?.GetType() && Equals(x, y);
    }

    static byte[] ResourceBytes(string image) {
        using var module = ModuleDefMD.Load(Path.GetFullPath(image));
        return module.Resources.OfType<EmbeddedResource>().Single(r => r.Name == ResourceName).CreateReader().ToArray();
    }
    static byte[] Bytes(Stream stream) { using var copy = new MemoryStream(); stream.Position = 0; stream.CopyTo(copy); return copy.ToArray(); }
    static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
