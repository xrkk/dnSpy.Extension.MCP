using System;
using System.IO;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Extension.MCP.Editing;
internal static class Program {
 static int failures;
 public static void Check(bool condition,string name){Console.WriteLine((condition?"PASS ":"FAIL ")+name);if(!condition)failures++;}
 public static ModuleDef MakeModule(){
  var module=new ModuleDefUser("guard.exe"){Kind=ModuleKind.Console,Mvid=Guid.Parse("11111111-1111-1111-1111-111111111111")};
  new AssemblyDefUser("guard",new Version(1,0)).Modules.Add(module);
  var type=new TypeDefUser("Example","Program",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
  foreach(var name in new[]{"A","B"}){var method=new MethodDefUser(name,MethodSig.CreateStatic(module.CorLibTypes.Void),MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static){Body=new CilBody()};method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));type.Methods.Add(method);}
  module.ManagedEntryPoint=type.Methods[0];return module;
 }
 static void AttributeProbe(bool assembly){
  using var module=MakeModule();
  var ctor=new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.String),new TypeRefUser(module,"Example","MarkerAttribute",module.CorLibTypes.AssemblyRef));
  var attribute=new CustomAttribute(ctor);attribute.ConstructorArguments.Add(new CAArgument(module.CorLibTypes.String,"before"));
  (assembly?module.Assembly.CustomAttributes:module.CustomAttributes).Add(attribute);
  var frozen=EditFingerprint.Compute(module);var before=EditFingerprint.ComputeExternalGuard(module);
  attribute.ConstructorArguments[0]=new CAArgument(module.CorLibTypes.String,"after");
  using var stream=new MemoryStream();module.Write(stream);using var reloaded=ModuleDefMD.Load(stream.ToArray());
  var readback=(assembly?reloaded.Assembly.CustomAttributes:reloaded.CustomAttributes)[0].ConstructorArguments[0].Value.ToString();
  Check(readback=="after","attribute actual write/readback "+assembly);
  Check(before!=EditFingerprint.ComputeExternalGuard(module),"attribute argument drift "+assembly);
  Check(frozen==EditFingerprint.Compute(module),"frozen fingerprint compatibility "+assembly);
 }
 static void AttributeArrayProbe(){
  using var module=MakeModule();var signature=new SZArraySig(module.CorLibTypes.String);
  var ctor=new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void,signature),new TypeRefUser(module,"Example","ArrayAttribute",module.CorLibTypes.AssemblyRef));
  var attribute=new CustomAttribute(ctor);var values=new List<CAArgument>();for(var i=0;i<65;i++)values.Add(new CAArgument(module.CorLibTypes.String,"before"));
  attribute.ConstructorArguments.Add(new CAArgument(signature,values));module.Assembly.CustomAttributes.Add(attribute);
  var before=EditFingerprint.ComputeExternalGuard(module);values[64]=new CAArgument(module.CorLibTypes.String,"after");
  Check(before!=EditFingerprint.ComputeExternalGuard(module),"attribute array element 65 drift");
  var named=new CANamedArgument(false,module.CorLibTypes.String,"Label",new CAArgument(module.CorLibTypes.String,"before"));attribute.NamedArguments.Add(named);
  before=EditFingerprint.ComputeExternalGuard(module);named.Argument=new CAArgument(module.CorLibTypes.String,"after");
  Check(before!=EditFingerprint.ComputeExternalGuard(module),"attribute named argument drift");
 }
 static void SecurityProbe(){
  using var module=MakeModule();var attribute=new SecurityAttribute(new TypeRefUser(module,"Example","PermissionAttribute",module.CorLibTypes.AssemblyRef));
  var named=new CANamedArgument(false,module.CorLibTypes.String,"Permission",new CAArgument(module.CorLibTypes.String,"before"));attribute.NamedArguments.Add(named);
  module.Assembly.DeclSecurities.Add(new DeclSecurityUser(SecurityAction.RequestMinimum,new List<SecurityAttribute>{attribute}));
  var before=EditFingerprint.ComputeExternalGuard(module);named.Argument=new CAArgument(module.CorLibTypes.String,"after");
  Check(before!=EditFingerprint.ComputeExternalGuard(module),"declared security argument drift");
 }
 static int Main(){AttributeProbe(true);AttributeProbe(false);GateProbe.Run();ExpiryProbe.Run();SecurityProbe();AttributeArrayProbe();ResourceProbe.Run();return failures==0?0:1;}
}
