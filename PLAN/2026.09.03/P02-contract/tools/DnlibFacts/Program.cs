using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Program {
  static ulong EnumMask<T>() where T : struct, Enum =>
    Enum.GetValues(typeof(T)).Cast<T>().Aggregate(0UL, (mask, value) => mask | Convert.ToUInt64(value));

  // This direct member access is intentionally compiled against the pinned
  // dnlib assembly. It is evidence that the P02 EmbeddedPdb mutation target is
  // a real dnlib 4.5.0 API path rather than a documentation-only string.
  static string ReadSequencePointDocumentUrl(MethodDef method, int instructionIndex) =>
    method.Body.Instructions[instructionIndex].SequencePoint!.Document.Url;

  static int Main(string[] args) {
    if (args.Length != 1) {
      Console.Error.WriteLine("usage: DnlibFacts OUTPUT.json");
      return 2;
    }
    var assembly = typeof(OpCode).Assembly;
    var path = assembly.Location;
    var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
      .Where(field => field.FieldType == typeof(OpCode))
      .Select(field => (OpCode)field.GetValue(null)!)
      .OrderBy(opcode => opcode.Name, StringComparer.Ordinal)
      .ToDictionary(opcode => opcode.Name, opcode => opcode.OperandType.ToString(), StringComparer.Ordinal);
    var masks = new SortedDictionary<string, ulong>(StringComparer.Ordinal) {
      [nameof(dnlib.DotNet.TypeAttributes)] = EnumMask<dnlib.DotNet.TypeAttributes>(),
      [nameof(dnlib.DotNet.MethodAttributes)] = EnumMask<dnlib.DotNet.MethodAttributes>(),
      [nameof(dnlib.DotNet.MethodImplAttributes)] = EnumMask<dnlib.DotNet.MethodImplAttributes>(),
      [nameof(dnlib.DotNet.FieldAttributes)] = EnumMask<dnlib.DotNet.FieldAttributes>(),
      [nameof(dnlib.DotNet.PropertyAttributes)] = EnumMask<dnlib.DotNet.PropertyAttributes>(),
      [nameof(dnlib.DotNet.EventAttributes)] = EnumMask<dnlib.DotNet.EventAttributes>(),
      [nameof(dnlib.DotNet.ParamAttributes)] = EnumMask<dnlib.DotNet.ParamAttributes>(),
      [nameof(dnlib.DotNet.GenericParamAttributes)] = EnumMask<dnlib.DotNet.GenericParamAttributes>(),
    };
    var output = new SortedDictionary<string, object?>(StringComparer.Ordinal) {
      ["assembly_file_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
      ["assembly_name"] = assembly.GetName().Name,
      ["assembly_version"] = assembly.GetName().Version?.ToString(),
      ["attribute_masks"] = masks,
      ["format"] = "dnspy.p02.dnlib-facts.v1",
      ["opcodes"] = opcodes,
      ["public_api_paths"] = new SortedDictionary<string, string>(StringComparer.Ordinal) {
        ["sequence_point_document_url"] = "MethodDef.Body.Instructions[first SequencePoint != null].SequencePoint.Document.Url",
      },
    };
    File.WriteAllText(args[0], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = false }) + "\n");
    return 0;
  }
}
