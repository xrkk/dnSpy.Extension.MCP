// Isolated VM test fixture only. Never install in a normal dnSpy profile.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ComponentModel.Composition;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Documents.TreeView;

[ExportAutoLoaded]
sealed class CdiCoordinatorFixture : IAutoLoaded {
    readonly IDocumentTreeView tree;
    readonly string root;
    readonly DispatcherTimer timer;
    ModuleDefMD module;
    Action undo;
    [ImportingConstructor]
    public CdiCoordinatorFixture(IDocumentTreeView tree) {
        this.tree=tree; root=Environment.GetEnvironmentVariable("DNMCP_CDI_FIXTURE_ROOT");
        if(Environment.GetEnvironmentVariable("DNMCP_TEST")!="1" || string.IsNullOrEmpty(root)) return;
        timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
        timer.Tick+=(s,e)=>Tick(); timer.Start();
    }
    string Guard() {
        var a=AppDomain.CurrentDomain.GetAssemblies().Single(x=>x.GetName().Name=="dnSpy.Extension.MCP.x");
        return (string)a.GetType("dnSpy.Extension.MCP.Editing.EditFingerprint").GetMethod("ComputeExternalGuard",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{module});
    }
    void Publish(string name,string text) { var path=Path.Combine(root,name);File.WriteAllText(path+".tmp",text);if(File.Exists(path))File.Delete(path);File.Move(path+".tmp",path); }
    void Tick() {
        try {
            if(module==null) {
                module=tree.GetAllModuleNodes().Select(x=>x.Document.ModuleDef).OfType<ModuleDefMD>().FirstOrDefault(x=>string.Equals(x.Location,Path.Combine(root,"fixtures","CdiHost.dll"),StringComparison.OrdinalIgnoreCase));
                if(module==null) return;
                using(var source=ModuleDefMD.Load(Path.Combine(root,"fixtures","CdiHost.dll"),new ModuleCreationOptions { TryToLoadPdbFromDisk=true })) {
                    foreach(var original in source.GetTypes().SelectMany(x=>x.Methods)) {
                        var originalBody=original.Body;
                        foreach(var info in original.CustomDebugInfos.OfType<PdbStateMachineHoistedLocalScopesCustomDebugInfo>()) {
                            var target=(MethodDef)module.ResolveToken(original.MDToken);
                            var copy=new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
                            foreach(var scope in info.Scopes) copy.Scopes.Add(new StateMachineHoistedLocalScope(
                                scope.Start==null?null:target.Body.Instructions[originalBody.Instructions.IndexOf(scope.Start)],
                                scope.End==null?null:target.Body.Instructions[originalBody.Instructions.IndexOf(scope.End)]));
                            target.CustomDebugInfos.Add(copy);
                        }
                    }
                }
                foreach(var m in module.GetTypes().SelectMany(x=>x.Methods)) { var body=m.Body; }
                module.CustomDebugInfos.Add(new PdbDynamicLocalVariablesCustomDebugInfo { Flags=new bool[65] });
                Publish("fixture-ready.txt",Guard());
            }
            var path=Path.Combine(root,"fixture-command.txt"); if(!File.Exists(path)) return;
            var command=File.ReadAllText(path).Trim(); File.Delete(path); var before=Guard();
            if(command=="restore") { if(undo==null) throw new Exception("no mutation"); undo(); undo=null; }
            else {
                if(undo!=null) throw new Exception("mutation pending");
                if(command=="flags") { var row=module.CustomDebugInfos.OfType<PdbDynamicLocalVariablesCustomDebugInfo>().Single();var old=row.Flags[64];row.Flags[64]=!old;undo=()=>row.Flags[64]=old; }
                else if(command=="hoisted") {
                    var method=module.GetTypes().SelectMany(x=>x.Methods).First(x=>x.CustomDebugInfos.OfType<PdbStateMachineHoistedLocalScopesCustomDebugInfo>().Any(y=>y.Scopes.Count>0));
                    var cdi=method.CustomDebugInfos.OfType<PdbStateMachineHoistedLocalScopesCustomDebugInfo>().First(x=>x.Scopes.Count>0);var old=cdi.Scopes[0];var next=old;next.End=method.Body.Instructions.First(x=>!ReferenceEquals(x,old.End));cdi.Scopes[0]=next;undo=()=>cdi.Scopes[0]=old;
                } else throw new Exception("unknown command");
            }
            Publish("fixture-result.txt","OK\n"+before+"\n"+Guard());
        } catch(Exception e) { Publish("fixture-error.txt",e.ToString()); timer.Stop(); }
    }
}
