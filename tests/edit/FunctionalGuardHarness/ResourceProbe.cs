using System;
using System.IO;
using dnlib.DotNet;
using dnlib.W32Resources;
using dnSpy.Extension.MCP.Editing;
internal static class ResourceProbe {
 public static void Run(){
  using var module=Program.MakeModule();var bytes=new byte[]{1,2,3,4};module.Resources.Add(new EmbeddedResource("payload",bytes,ManifestResourceAttributes.Private));
  foreach(var kind in new[]{"embedded","linked"})Program.Check(Convert.ToHexString(EditResourceCodec.ReadExportBytes(module,kind,"payload",null,"RCDATA",null,0))=="01020304","resource export "+kind);
  module.Win32Resources=new Win32ResourcesUser();
  foreach(var numeric in new[]{false,true}){
   var type=new ResourceDirectoryUser(numeric?new ResourceName(10):new ResourceName("RCDATA"));
   var name=new ResourceDirectoryUser(numeric?new ResourceName(7):new ResourceName("payload"));
   type.Directories.Add(name);module.Win32Resources.Root.Directories.Add(type);
   name.Data.Add(new ResourceData(new ResourceName(1033),dnlib.IO.ByteArrayDataReaderFactory.Create(bytes,null),0,(uint)bytes.Length));
   Program.Check(Convert.ToHexString(EditResourceCodec.ReadExportBytes(module,"win32","payload",numeric?10:null,"RCDATA",numeric?7:null,1033))=="01020304","Win32 identity export numeric="+numeric);
  }
  try{EditResourceCodec.ReadExportBytes(module,"win32","payload",null,"RCDATA",null,1041);Program.Check(false,"Win32 absent language rejected");}
  catch(EditDomainException ex){Program.Check(ex.Code=="EDIT_CAPABILITY_UNAVAILABLE","Win32 absent language rejected");}
  var root=Path.Combine(Path.GetTempPath(),"dnspy-resource-probe-"+Guid.NewGuid().ToString("N"));
  try{EditSourceFileIdentity.ReadAllowedResource(Path.Combine(root+"-outside","file"),root,1024);Program.Check(false,"resource outside root rejected");}
  catch(IOException){Program.Check(true,"resource outside root rejected");}
  if(!OperatingSystem.IsWindows()){Console.WriteLine("SKIP resource handle/file-id tests: Windows required");return;}
  Directory.CreateDirectory(root);
  try{
   var path=Path.Combine(root,"input.bin");File.WriteAllBytes(path,bytes);
   var one=EditSourceFileIdentity.ReadAllowedResource(path,root,1024);var two=EditSourceFileIdentity.ReadAllowedResource(path,root,1024);
   Program.Check(Convert.ToHexString(one.Bytes)=="01020304" && one.Identity.FileId==two.Identity.FileId && one.Identity.FileId==EditSourceFileIdentity.Observe(path)!.FileId,"resource bytes and real stable file identity");
   try{EditSourceFileIdentity.ReadAllowedResource(path,root,2);Program.Check(false,"resource capacity rejected before allocation");}
   catch(EditDomainException ex){Program.Check(ex.Code=="EDIT_CAPACITY_EXCEEDED","resource capacity rejected before allocation");}
   try{EditSourceFileIdentity.ReadAllowedResource(root,Path.GetDirectoryName(root),1024);Program.Check(false,"resource directory rejected");}
   catch(IOException){Program.Check(true,"resource directory rejected");}
  }finally{Directory.Delete(root,true);}
 }
}
