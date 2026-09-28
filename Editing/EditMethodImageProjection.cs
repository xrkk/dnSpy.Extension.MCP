using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

// An authenticated checkpoint's physical MethodDef rows, bound to existing live
// objects by their structural positions. This is a writer view, not a new live PE.
internal static class EditMethodImageProjection {
    internal sealed class Row {
        internal readonly string Type;
        internal readonly string Name;
        internal readonly string Signature;
        internal readonly uint Rid;
        internal Row(string type, string name, string signature, uint rid) {
            Type=type; Name=name; Signature=signature; Rid=rid;
        }
    }
    sealed class Projection {
        internal readonly IReadOnlyDictionary<MethodDef,uint> Rids;
        internal Projection(IReadOnlyDictionary<MethodDef,uint> rids) => Rids=rids;
    }
    static readonly ConditionalWeakTable<ModuleDef,Projection> projections=new();
    static readonly object gate=new();

    internal static Row[] Capture(ModuleDefMD module) => module.GetTypes()
        .SelectMany(type => type.Methods.Select(method => new Row(type.FullName, method.Name.String,
            method.MethodSig.ToString(), method.Rid))).ToArray();

    internal static IReadOnlyDictionary<MethodDef,uint>? Get(ModuleDef module) {
        lock (gate) return projections.TryGetValue(module,out var projection) ? projection.Rids : null;
    }

    internal static Action Clear(ModuleDef module) {
        Projection? previous;
        lock (gate) {
            projections.TryGetValue(module,out previous);
            projections.Remove(module);
        }
        return () => {
            lock (gate) {
                projections.Remove(module);
                if (previous!=null) projections.Add(module,previous);
            }
        };
    }

    internal static Action Set(ModuleDef module, Row[] expected) {
        var actual=module.GetTypes().SelectMany(type => type.Methods.Select(method => (Type:type.FullName, Method:method))).ToArray();
        if (actual.Length!=expected.Length) throw new EditDomainException("EDIT_HISTORY_CONFLICT");
        var map=new Dictionary<MethodDef,uint>();
        var seen=new HashSet<uint>();
        for (var i=0;i<actual.Length;i++) {
            var method=actual[i].Method;
            if (actual[i].Type!=expected[i].Type || method.Name.String!=expected[i].Name
                || method.MethodSig.ToString()!=expected[i].Signature
                || expected[i].Rid==0 || expected[i].Rid>actual.Length || !seen.Add(expected[i].Rid))
                throw new EditDomainException("EDIT_HISTORY_CONFLICT");
            map.Add(method,expected[i].Rid);
        }
        Projection? previous;
        lock (gate) {
            projections.TryGetValue(module,out previous);
            projections.Remove(module);
            projections.Add(module,new Projection(map));
        }
        return () => {
            lock (gate) {
                projections.Remove(module);
                if (previous!=null) projections.Add(module,previous);
            }
        };
    }
}
