using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P08 managed-resource codec for the standard .resources container.  Parsing
/// walks the documented binary format directly (ECMA-335 II.24.2.4) and never
/// deserializes anything — custom serialized objects are surfaced as metadata
/// (type hint + payload length + SHA) only (CON-019).  Entry edits rebuild the
/// blob by copying every untouched entry's raw bytes verbatim and re-encoding
/// exactly the edited entry, so private copy, live module and checkpoint
/// replay produce byte-identical output (adjudicated AUD-002/003; the format
/// is positional and every byte is payload-driven).
/// </summary>
internal static class EditResourceCodec {
	/// <summary>Read the selected resource without executing or deserializing it.
	/// Path imports labelled linked have already been normalized to embedded bytes.</summary>
	public static byte[] ReadExportBytes(ModuleDef module, string resourceType, string name,
		int? typeId, string typeName, int? nameId, int language) {
		if (resourceType is "embedded" or "linked") {
			var matches = module.Resources.OfType<EmbeddedResource>().Where(r => r.Name == name).ToArray();
			if (matches.Length == 1) return matches[0].CreateReader().ToArray();
		}
		else if (resourceType == "win32") {
			var type = typeId.HasValue ? new dnlib.W32Resources.ResourceName(typeId.Value) : new dnlib.W32Resources.ResourceName(typeName);
			var row = nameId.HasValue ? new dnlib.W32Resources.ResourceName(nameId.Value) : new dnlib.W32Resources.ResourceName(name);
			var types = module.Win32Resources?.Root.Directories.Where(x => x.Name.Equals(type)).ToArray();
			var names = types?.Length == 1 ? types[0].Directories.Where(x => x.Name.Equals(row)).ToArray() : null;
			var data = names?.Length == 1 ? names[0].Data.Where(x => x.Name.Equals(new dnlib.W32Resources.ResourceName(language))).ToArray() : null;
			if (data?.Length == 1) return data[0].CreateReader().ToArray();
		}
		throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> {
			["kind"] = "capability", ["capability"] = "resource_row", ["reason"] = "resource identity is absent, ambiguous, or unsupported" });
	}

	// ResourceTypeCode enum (negative type codes inline; >= 0 indexes the types array)
	public const int CodeNull = 0, CodeString = 1, CodeBoolean = 2, CodeChar = 3, CodeByte = 4,
		CodeSByte = 5, CodeInt16 = 6, CodeUInt16 = 7, CodeInt32 = 8, CodeUInt32 = 9,
		CodeInt64 = 10, CodeUInt64 = 11, CodeSingle = 12, CodeDouble = 13, CodeDecimal = 14,
		CodeDateTime = 15, CodeTimeSpan = 16, CodeByteArray = 32, CodeStream = 33,
		CodeStartOfUserTypes = 64;

	// The frozen P08 standard edit domain (adjudicated AUD-006): strings, bool,
	// the nine numeric scalars and byte arrays (stream rows read/write as bytes).
	static readonly Dictionary<int, string> StandardKinds = new() {
		[CodeString] = "string", [CodeBoolean] = "boolean", [CodeByte] = "u1", [CodeSByte] = "i1",
		[CodeInt16] = "i2", [CodeUInt16] = "u2", [CodeInt32] = "i4", [CodeUInt32] = "u4",
		[CodeInt64] = "i8", [CodeUInt64] = "u8", [CodeSingle] = "r4", [CodeDouble] = "r8",
		[CodeByteArray] = "bytes", [CodeStream] = "bytes",
	};
	public static readonly string[] EditableKinds = StandardKinds.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

	public sealed class ResourceEntry {
		public string Name = string.Empty;
		public int TypeCode;
		public string Kind = string.Empty;       // "custom"/"decimal"/"char"/... for non-editable rows
		public byte[] Raw = Array.Empty<byte>(); // value bytes exactly as stored (type code excluded)
		public object? Decoded;                  // safe scalar decode (never a deserialized object)
	}

	public sealed class ParsedResource {
		public byte[] Header = Array.Empty<byte>();   // magic..reader strings (verbatim)
		public int SetVersion;
		public byte[][] TypeNames = Array.Empty<byte[]>();  // verbatim 7-bit-prefixed ASCII user-type names
		public uint[] Hashes = Array.Empty<uint>();       // hash-table order (verbatim)
		public int[] NamePositions = Array.Empty<int>();  // hash-table order (verbatim)
		public long NameSectionStart;
		public List<ResourceEntry> Entries = new();       // data/layout order
	}

	public static bool IsStandardKind(int typeCode) => StandardKinds.ContainsKey(typeCode);

	public static string KindOf(int typeCode) =>
		StandardKinds.TryGetValue(typeCode, out var kind) ? kind
		: typeCode switch {
			CodeNull => "null", CodeChar => "char", CodeDecimal => "decimal",
			CodeDateTime => "datetime", CodeTimeSpan => "timespan",
			_ => "custom",
		};

	public static ParsedResource Parse(byte[] blob) {
		var stream = new SpanReader(blob);
		if (stream.ReadUInt32() != 0xBEEFCACE) throw Reject("the resource blob magic is not a .resources container");
		if (stream.ReadInt32() != 1) throw Reject("unsupported .resources header version");
		var headerSize = stream.ReadInt32();
		if (headerSize < 0 || headerSize > blob.Length) throw Reject("the .resources header size is corrupt");
		var headerEnd = stream.Position + headerSize;
		var readerTypeLength = (int)stream.ReadSevenBit();
		stream.Skip(readerTypeLength);           // reader type string payload (UTF8)
		var setTypeLength = (int)stream.ReadSevenBit();
		stream.Skip(setTypeLength);              // resource set type string payload
		// capture the full verbatim header (magic through the reader strings)
		var header = new byte[headerEnd];
		Buffer.BlockCopy(blob, 0, header, 0, headerEnd);
		stream.Seek(headerEnd);
		var setVersion = stream.ReadInt32();
		if (setVersion is not (1 or 2)) throw Reject("unsupported .resources set version");
		var numResources = stream.ReadInt32();
		var numTypes = stream.ReadInt32();
		if (numResources < 0 || numTypes < 0 || numResources > 1_000_000) throw Reject("the .resources counts are corrupt");
		// CHK-005: the user-type table is a run of 7-bit-length-prefixed ASCII
		// assembly-qualified type names (ResourceWriter.Generate), not int32
		// codes.  Preserved verbatim; user-typed rows never enter the edit domain.
		var typeNames = new byte[numTypes][];
		for (var index = 0; index < numTypes; index++) {
			var nameLength = (int)stream.ReadSevenBit();
			if (nameLength < 0 || nameLength > blob.Length - stream.Position) throw Reject("a .resources type name is corrupt");
			typeNames[index] = new byte[nameLength];
			for (var b = 0; b < nameLength; b++) typeNames[index][b] = stream.ReadByte();
		}
		// the name hash array starts on an 8-byte boundary from the stream start
		stream.Align(8);
		var hashes = new uint[numResources];
		for (var index = 0; index < numResources; index++) hashes[index] = stream.ReadUInt32();
		var namePositions = new int[numResources];
		for (var index = 0; index < numResources; index++) namePositions[index] = stream.ReadInt32();
		var dataOffset = stream.ReadInt32();      // absolute stream position of the data section
		var nameSectionStart = stream.Position;
		// CHK-005: the name section is a sequence of [7-bit BYTE count][UTF-16LE
		// name][INT32 relative data offset] rows, in the writer's own row order;
		// namePositions[i] is that row's byte offset and stays verbatim on
		// rebuild because edits never rename or reorder rows.
		var nameRows = new (int position, string name, int dataRelative)[numResources];
		for (var index = 0; index < numResources; index++) {
			var target = nameSectionStart + namePositions[index];
			if (target < 0 || target >= blob.Length) throw Reject("a .resources name offset is corrupt");
			var nameReader = new SpanReader(blob, target);
			var name = nameReader.ReadNameString();
			var dataRelative = new SpanReader(blob, (int)nameReader.Position).ReadInt32();
			if (dataRelative < 0 || dataOffset + dataRelative >= blob.Length) throw Reject("a .resources name data offset is corrupt");
			nameRows[index] = (namePositions[index], name, dataRelative);
		}
		// entries follow the name-section file order (ascending row offset): the
		// rebuild rewrites names in this same order so the verbatim
		// namePositions table stays valid without renaming or reordering rows
		Array.Sort(nameRows, (left, right) => left.position.CompareTo(right.position));
		var parsed = new ParsedResource {
			Header = header, SetVersion = setVersion, TypeNames = typeNames,
			Hashes = hashes, NamePositions = namePositions, NameSectionStart = nameSectionStart,
		};
		var offsets = nameRows.Select(r => r.dataRelative).Distinct().OrderBy(x => x).ToArray();
		foreach (var row in nameRows) {
			// each entry is located by its own inline data offset; the data
			// section itself is written in the writer's add order and must not
			// be walked sequentially
			var dataReader = new SpanReader(blob, dataOffset + row.dataRelative);
			var typeCode = (int)dataReader.ReadSevenBit();
			// CHK-005: a user-typed row (code >= 0x40) carries no length prefix
			// (ResourceWriter.AddResourceData writes the payload verbatim; the
			// reader bounds it by the next entry's data offset).  Preserved
			// opaquely; the edit layer refuses value edits on it by name.
			int rawLength;
			if (typeCode >= CodeStartOfUserTypes) {
				var following = offsets.FirstOrDefault(next => next > row.dataRelative);
				var end = following > row.dataRelative ? dataOffset + following : blob.Length;
				rawLength = end - (int)dataReader.Position;
			}
			else
				rawLength = ValueSize(blob, dataReader.Position, typeCode);
			if (rawLength < 0 || dataReader.Position + rawLength > blob.Length) throw Reject("a .resources value runs past the blob");
			var raw = new byte[rawLength];
			Buffer.BlockCopy(blob, dataReader.Position, raw, 0, rawLength);
			parsed.Entries.Add(new ResourceEntry {
				Name = row.name, TypeCode = typeCode, Kind = KindOf(typeCode), Raw = raw,
				Decoded = IsStandardKind(typeCode) ? DecodeValue(typeCode, raw) : null,
			});
		}
		return parsed;
	}

	/// <summary>Rebuild the blob: header, hash table and name positions are
	/// kept verbatim (edits never rename); the name section and the data
	/// section are rewritten in the same layout order, with every entry's raw
	/// bytes copied verbatim except re-encoded edited entries (by name).</summary>
	public static byte[] Rebuild(ParsedResource parsed, IReadOnlyDictionary<string, ResourceEntry> edited) {
		var entries = parsed.Entries;
		using var output = new MemoryStream();
		output.Write(parsed.Header, 0, parsed.Header.Length);
		WriteInt32(output, parsed.SetVersion);
		WriteInt32(output, entries.Count);
		WriteInt32(output, parsed.TypeNames.Length);
		foreach (var typeName in parsed.TypeNames) {
			WriteSevenBit(output, typeName.Length);
			output.Write(typeName, 0, typeName.Length);
		}
		Align(output, 8);
		foreach (var hash in parsed.Hashes) WriteUInt32(output, hash);
		foreach (var position in parsed.NamePositions) WriteInt32(output, position);
		var dataOffsetField = (int)output.Position;
		WriteInt32(output, 0);                       // patched below
		// name entries live in layout order, each carrying the relative data
		// offset of its entry; entry sizes are constant ([7-bit len][utf16 name]
		// [int32 offset]) so the verbatim namePositions stay valid.
		var dataRelative = new int[entries.Count];
		var running = 0;
		for (var index = 0; index < entries.Count; index++) {
			var entry = edited.TryGetValue(entries[index].Name, out var replacement) ? replacement : entries[index];
			dataRelative[index] = running;
			running += GetSevenBitLength(unchecked((int)entry.TypeCode)) + entry.Raw.Length;
		}
		for (var index = 0; index < entries.Count; index++) {
			WriteName(output, entries[index].Name);
			WriteInt32(output, dataRelative[index]);
		}
		var dataOffset = (int)output.Position;
		foreach (var entry in entries) {
			var value = edited.TryGetValue(entry.Name, out var replacement) ? replacement : entry;
			WriteSevenBit(output, unchecked((int)value.TypeCode));
			output.Write(value.Raw, 0, value.Raw.Length);
		}
		var result = output.ToArray();
		Buffer.BlockCopy(BitConverter.GetBytes(dataOffset), 0, result, dataOffsetField, 4);
		return result;
	}

	public static byte[] EncodeValue(int typeCode, JsonElement value) {
		switch (typeCode) {
		case CodeString: {
			var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Reject("a string resource value must be a string");
			// the data section stores strings in BinaryWriter form: 7-bit byte
			// count followed by UTF-8 bytes (no terminator)
			return SevenBitPrefixed(Encoding.UTF8.GetBytes(text));
		}
		case CodeBoolean: return new[] { value.GetBoolean() ? (byte)1 : (byte)0 };
		case CodeByte: return new[] { value.GetByte() };
		case CodeSByte: return new[] { unchecked((byte)value.GetSByte()) };
		case CodeInt16: return BitConverter.GetBytes(value.GetInt16());
		case CodeUInt16: return BitConverter.GetBytes(value.GetUInt16());
		case CodeInt32: return BitConverter.GetBytes(value.GetInt32());
		case CodeUInt32: return BitConverter.GetBytes(value.GetUInt32());
		case CodeInt64: return BitConverter.GetBytes(value.GetInt64());
		case CodeUInt64: return BitConverter.GetBytes(value.GetUInt64());
		case CodeSingle: return BitConverter.GetBytes(value.GetSingle());
		case CodeDouble: return BitConverter.GetBytes(value.GetDouble());
		case CodeByteArray:
		case CodeStream: {
			// arrays carry an INT32 length prefix (verified format), unlike
			// strings which use a 7-bit prefix
			var bytes = value.GetBytesFromBase64();
			using var buffer = new MemoryStream();
			buffer.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
			buffer.Write(bytes, 0, bytes.Length);
			return buffer.ToArray();
		}
		default: throw Reject("the resource value kind is outside the P08 edit domain");
		}
	}

	static byte[] SevenBitPrefixed(byte[] payload) {
		using var buffer = new MemoryStream();
		WriteSevenBit(buffer, payload.Length);
		buffer.Write(payload, 0, payload.Length);
		return buffer.ToArray();
	}

	static object? DecodeValue(int typeCode, byte[] raw) {
		switch (typeCode) {
		case CodeString: return DecodeString(raw);
		case CodeBoolean: return raw.Length == 1 && raw[0] != 0;
		case CodeByte: return raw[0];
		case CodeSByte: return unchecked((sbyte)raw[0]);
		case CodeInt16: return BitConverter.ToInt16(raw, 0);
		case CodeUInt16: return BitConverter.ToUInt16(raw, 0);
		case CodeInt32: return BitConverter.ToInt32(raw, 0);
		case CodeUInt32: return BitConverter.ToUInt32(raw, 0);
		case CodeInt64: return BitConverter.ToInt64(raw, 0);
		case CodeUInt64: return BitConverter.ToUInt64(raw, 0);
		case CodeSingle: return BitConverter.ToSingle(raw, 0);
		case CodeDouble: return BitConverter.ToDouble(raw, 0);
		case CodeByteArray:
		case CodeStream: {
			// the raw bytes carry an INT32 length prefix; decode returns the payload
			if (raw.Length < 4) throw Reject("a .resources array payload is corrupt");
			return raw.Skip(4).ToArray();
		}
		default: return null;
		}
	}

	static string DecodeString(byte[] raw) {
		var reader = new SpanReader(raw);
		var byteCount = (int)reader.ReadSevenBit();
		if (reader.Position + byteCount > raw.Length) throw Reject("a .resources string value is corrupt");
		return Encoding.UTF8.GetString(raw, reader.Position, byteCount);
	}

	static int ValueSize(byte[] blob, int position, int typeCode) {
		var reader = new SpanReader(blob, position);
		switch (typeCode) {
		case CodeNull: return 0;
		case CodeBoolean: case CodeByte: case CodeSByte: return 1;
		case CodeChar: case CodeInt16: case CodeUInt16: return 2;
		case CodeInt32: case CodeUInt32: case CodeSingle: return 4;
		case CodeInt64: case CodeUInt64: case CodeDouble:
		case CodeDateTime: case CodeTimeSpan: return 8;
		case CodeDecimal: return 16;
		case CodeString: {
			var byteCount = (int)reader.ReadSevenBit();
			if (byteCount < 0 || reader.Position + byteCount > blob.Length) throw Reject("a .resources string length is corrupt");
			return (reader.Position - position) + byteCount;
		}
		case CodeByteArray:
		case CodeStream: {
			if (position + 4 > blob.Length) throw Reject("a .resources array length is corrupt");
			var byteCount = BitConverter.ToInt32(blob, position);
			if (byteCount < 0 || position + 4 + byteCount > blob.Length) throw Reject("a .resources array payload is corrupt");
			return 4 + byteCount;
		}
		default:
			// user-typed rows (code >= 0x40) carry no length prefix and are
			// bounded by the next entry's data offset in Parse; they never
			// reach this sizing path
			throw Reject("a resource value type is outside the P08 entry walk domain");
		}
	}

	static void WriteInt32(Stream stream, int value) => stream.Write(BitConverter.GetBytes(value), 0, 4);
	static void WriteUInt32(Stream stream, uint value) => stream.Write(BitConverter.GetBytes(value), 0, 4);
	// ResourceWriter pads with the literal bytes "PAD" (P,A,D cycling)
	static void Align(Stream stream, int boundary) {
		var cycle = 0;
		while (stream.Position % boundary != 0) {
			stream.WriteByte(cycle % 3 == 0 ? (byte)0x50 : cycle % 3 == 1 ? (byte)0x41 : (byte)0x44);
			cycle++;
		}
	}

	static int GetSevenBitLength(int value) {
		var length = 1;
		while (value >= 0x80) { value >>= 7; length++; }
		return length;
	}

	static void WriteSevenBit(Stream stream, int value) {
		uint current = unchecked((uint)value);
		while (current >= 0x80) {
			stream.WriteByte((byte)(current | 0x80));
			current >>= 7;
		}
		stream.WriteByte((byte)current);
	}

	static void WriteName(Stream stream, string name) {
		var payload = Encoding.Unicode.GetBytes(name);
		WriteSevenBit(stream, payload.Length);
		stream.Write(payload, 0, payload.Length);
	}

	sealed class SpanReader {
		readonly byte[] blob;
		int position;
		public SpanReader(byte[] data, int start = 0) { blob = data; position = start; }
		public int Position => position;
		public void Seek(int target) => position = target;
		public void Skip(int count) => position += count;
		public int ReadInt32() { var value = BitConverter.ToInt32(blob, position); position += 4; return value; }
		public byte ReadByte() => blob[position++];
		public uint ReadUInt32() { var value = BitConverter.ToUInt32(blob, position); position += 4; return value; }
		public uint ReadSevenBit() {
			uint result = 0, shift = 0;
			while (true) {
				var b = blob[position++];
				result |= (uint)(b & 0x7F) << (int)shift;
				if ((b & 0x80) == 0) return result;
				shift += 7;
				if (shift > 35 || position > blob.Length) throw Reject("a 7-bit encoded length is corrupt");
			}
		}
		public string ReadNameString() {
			var byteCount = (int)ReadSevenBit();
			if ((byteCount & 1) != 0 || position + byteCount > blob.Length) throw Reject("a .resources name is corrupt");
			var text = byteCount == 0 ? string.Empty : Encoding.Unicode.GetString(blob, position, byteCount);
			position += byteCount;
			return text;
		}
		public void SkipString7BitChars() {
			var byteCount = (int)ReadSevenBit();
			position += byteCount;
		}
		public void Align(int boundary) { while (position % boundary != 0) position++; }
	}

	/// <summary>Decode an edited entry from the operation payload; the raw bytes
	/// are re-encoded for the target type code.</summary>
	public static ResourceEntry EncodeEntry(string name, string kind, JsonElement value) => EncodeEntry(name, kind, value, null);

	/// <summary>Encode an edited entry; <paramref name="preservedTypeCode"/> carries the
	/// stored code so byte-array edits keep their Stream/ByteArray distinction.</summary>
	public static ResourceEntry EncodeEntry(string name, string kind, JsonElement value, int? preservedTypeCode) {
		var typeCode = kind switch {
			"string" => CodeString, "boolean" => CodeBoolean,
			"u1" => CodeByte, "i1" => CodeSByte, "i2" => CodeInt16, "u2" => CodeUInt16,
			"i4" => CodeInt32, "u4" => CodeUInt32, "i8" => CodeInt64, "u8" => CodeUInt64,
			"r4" => CodeSingle, "r8" => CodeDouble,
			"bytes" => preservedTypeCode is CodeByteArray or CodeStream ? preservedTypeCode.Value : CodeByteArray,
			_ => throw Reject("the resource value kind is outside the P08 edit domain: " + kind),
		};
		return new ResourceEntry { Name = name, TypeCode = typeCode, Kind = kind, Raw = EncodeValue(typeCode, value) };
	}

	static EditDomainException Reject(string reason) => new("EDIT_VALIDATION_FAILED",
		EditWorkspace.ValidationDetails("resource_codec", reason));
}
