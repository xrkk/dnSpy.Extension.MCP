using System;
using System.Text.Json;
using System.Security.Cryptography;
using dnlib.DotNet;
namespace dnSpy.Extension.MCP.Editing {
internal sealed class EditDomainException : Exception {
 public string Code { get; }
 public EditDomainException(string code, object? details=null):base(code){Code=code;}
}
internal static class EditWire {
 public const long IdleTimeoutMs=600000;
 public static string Sha256(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
 public static JsonSerializerOptions JsonOptions=new();
}
internal static class EditDeletedRowsTombstone { public static bool IsTombstone(TypeDef type)=>false; }
internal static class EditMarshalCodec { public static object Capture(MarshalType value,Func<TypeSig,object> encode)=>throw new InvalidOperationException("No marshal fixture permitted"); }
internal static class EditWorkspace { public static object ValidationDetails(params object[] values)=>values; }
}
