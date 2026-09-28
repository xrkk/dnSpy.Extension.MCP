using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using dnlib.DotNet;

namespace dnSpy.Extension.MCP.Editing;

// Private candidate: record a validated reference-source row view for
// checkpoint serialization of this live module. The writer receives a copy
// through MetadataOptions; source metadata tables are never mutated.
internal static class EditReferenceImageProjection {
    sealed class Projection { public uint[] Rows { get; } public Projection(uint[] rows) { Rows = (uint[])rows.Clone(); } }
    sealed class MethodSpecRebind {
        public MethodSpec Row { get; }
        public IMethodDefOrRef Original { get; }
        public MethodSpecRebind(MethodSpec row, IMethodDefOrRef original) { Row = row; Original = original; }
    }
    static readonly ConditionalWeakTable<ModuleDef, Projection> projections = new();
    static readonly ConditionalWeakTable<ModuleDef, List<MethodSpecRebind>> methodRebinds = new();
    static readonly object gate = new();

    internal static uint[]? Get(ModuleDef module) {
        lock (gate) return projections.TryGetValue(module, out var row) ? (uint[])row.Rows.Clone() : null;
    }

    internal static void RebindSourceMethodSpec(ModuleDef module, MethodSpec row, IMethodDefOrRef method) {
        lock (gate) {
            var rows = methodRebinds.GetOrCreateValue(module);
            if (!rows.Exists(x => ReferenceEquals(x.Row, row)))
                rows.Add(new MethodSpecRebind(row, row.Method));
            row.Method = method;
        }
    }

    internal static Action Set(ModuleDef module, uint[]? rows) {
        uint[]? previous;
        List<(MethodSpec Row, IMethodDefOrRef Method)> previousMethods = new();
        lock (gate) {
            previous = projections.TryGetValue(module, out var existing) ? (uint[])existing.Rows.Clone() : null;
            if (methodRebinds.TryGetValue(module, out var rebinds)) {
                foreach (var rebind in rebinds) {
                    previousMethods.Add((rebind.Row, rebind.Row.Method));
                    if (rows != null && rows.Length == 5 && rebind.Row.Rid > rows[4])
                        rebind.Row.Method = rebind.Original;
                }
            }
            projections.Remove(module);
            if (rows != null) projections.Add(module, new Projection(rows));
        }
        return () => {
            lock (gate) {
                projections.Remove(module);
                if (previous != null) projections.Add(module, new Projection(previous));
                foreach (var (row, method) in previousMethods) row.Method = method;
            }
        };
    }

}
