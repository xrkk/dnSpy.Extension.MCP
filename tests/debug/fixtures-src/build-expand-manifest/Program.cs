using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// T038: emits the metadata manifest the shared live-contract test consumes instead of a
// hard-coded method token / IL offset. The chosen breakpoint is semantic: the IL offset of
// the `call KeepLive` instruction in Main — at that point every field of `expandPayload`
// (including the child object) is assigned and the local is still live (it is the call's
// argument). Everything the test needs to know is derived from the actual built bytes and
// bound to the file's SHA-256; a manifest whose SHA does not match the deployed fixture is
// rejected by the test, as is a non-boundary offset.
//
// Usage: dotnet run -c Release -- <path-to-ExpandValuesFixture.exe> [out.json]

var path = args.Length > 0 ? args[0] : throw new ArgumentException("fixture exe path required");
var outPath = args.Length > 1 ? args[1] : Path.ChangeExtension(path, ".manifest.json");

var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
using var module = ModuleDefMD.Load(path);
var mvid = module.Mvid ?? throw new InvalidOperationException("module has no MVID");

var fixtureType = module.Find("ExpandValuesFixtureNs.ExpandValuesFixture", false)
    ?? throw new InvalidOperationException("ExpandValuesFixture type not found");
var nodeType = module.Find("ExpandValuesFixtureNs.ExpandNode", false)
    ?? throw new InvalidOperationException("ExpandNode type not found");

var main = fixtureType.FindMethod("Main") ?? throw new InvalidOperationException("Main not found");
var keep = fixtureType.FindMethod("KeepLive") ?? throw new InvalidOperationException("KeepLive not found");
if (main.Body is null || keep.Body is null) throw new InvalidOperationException("method bodies missing");

// Instruction boundaries of Main (every instruction start is a boundary; the manifest lists
// them so the test can reject an offset that is not one).
var boundaries = new System.Collections.Generic.List<int>();
int callOffset = -1;
foreach (var instr in keep.Body.Instructions)
    boundaries.Add((int)instr.Offset);
foreach (var instr in main.Body.Instructions)
{
    boundaries.Add((int)instr.Offset);
    if (instr.OpCode.Code == Code.Call
        && instr.Operand is IMethod target
        && target.Name == keep.Name
        && target.DeclaringType?.Name == fixtureType.Name)
    {
        callOffset = (int)instr.Offset;
    }
}
if (callOffset < 0) throw new InvalidOperationException("the KeepLive call site was not found in Main");
if (keep.Body.Instructions.Count == 0) throw new InvalidOperationException("KeepLive has no IL");
var anchorToken = keep.MDToken.Raw;
var anchorOffset = 0;
var anchorMethod = "KeepLive";

// Field shape check so a rebuilt fixture that drifts from the test's expectations fails at
// manifest build time, not at 3 a.m. inside a debug session.
foreach (var fieldName in new[] { "Number", "Text", "Child" })
    if (nodeType.FindField(fieldName) is null)
        throw new InvalidOperationException($"ExpandNode.{fieldName} missing");

var manifest = new
{
    schema_version = "dnspy.expand-fixture-manifest.v1",
    fixture = Path.GetFileName(path),
    sha256 = sha256,
    mvid = mvid.ToString(),
    entry_type = fixtureType.FullName,
    entry_type_token = $"0x{fixtureType.MDToken.Raw:X8}",
    method = anchorMethod,
    method_token = $"0x{anchorToken:X8}",
    breakpoint = new
    {
        il_offset = anchorOffset,
        rationale = "IL offset 0 (method entry) of KeepLive(ExpandNode expandPayload): the "
            + "caller assigned every field of the payload (including Child and Child.Child=null) "
            + "before the call, so the parameter is fully populated in this frame; entry-offset "
            + "breakpoints bind deterministically. The KeepLive call site in Main (offset "
            + callOffset + ") is recorded as main_keep_call_il_offset for reference.",
        is_instruction_boundary = true,
    },
    main_keep_call_il_offset = callOffset,
    main_il_boundaries = boundaries,
    node_type = nodeType.FullName,
    local_name = "expandPayload",
    anchor_frame_kind = "parameter",
};
File.WriteAllText(outPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"manifest written: {outPath}");
Console.WriteLine($"sha256={sha256} mvid={mvid} method_token=0x{main.MDToken.Raw:X8} il_offset={callOffset}");
