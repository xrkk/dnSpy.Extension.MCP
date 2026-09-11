using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

internal static class PdbScopeSnapshotCandidate {
	public sealed class LocalNode {
		public int Index { get; set; }
		public int[] Name { get; set; } = Array.Empty<int>();
		public int Attributes { get; set; }
	}
	public sealed class ConstantNode {
		public int[] Name { get; set; } = Array.Empty<int>();
		public EditStructuredSignatureCodec.TypeNode Type { get; set; } = null!;
		public AttributeSnapshotCandidate.ValueNode Value { get; set; } = null!;
	}
	public sealed class Node {
		public int Start { get; set; }
		public int End { get; set; }
		public LocalNode[] Locals { get; set; } = Array.Empty<LocalNode>();
		public ConstantNode[] Constants { get; set; } = Array.Empty<ConstantNode>();
		public int[][] Namespaces { get; set; } = Array.Empty<int[]>();
		public string? ImportScope { get; set; }
		public Node[] Children { get; set; } = Array.Empty<Node>();
	}
	public static Node? Capture(CilBody body, Func<IMDTokenProvider, string> bind, Func<PdbImportScope, string> bindImport) {
		var active = new HashSet<PdbScope>();
		int Target(Instruction? value) { if (value == null) return -1; var index = body.Instructions.IndexOf(value); if (index < 0) throw new InvalidDataException("Detached PDB scope boundary"); return index; }
		Node Visit(PdbScope scope) {
			if (!active.Add(scope)) throw new InvalidDataException("Cyclic PDB scope");
			try {
				if (scope.CustomDebugInfos.Count != 0 || scope.Variables.Any(v => v.CustomDebugInfos.Count != 0) || scope.Constants.Any(c => c.CustomDebugInfos.Count != 0)) throw new InvalidDataException("PDB custom info outside scope candidate");
				return new Node { Start = Target(scope.Start), End = Target(scope.End), Namespaces = scope.Namespaces.Select(s => s.Select(c => (int)c).ToArray()).ToArray(),
					ImportScope = scope.ImportScope == null ? null : bindImport(scope.ImportScope),
					Locals = scope.Variables.Select(v => { var index = body.Variables.IndexOf(v.Local); if (index < 0) throw new InvalidDataException("Detached PDB local"); return new LocalNode { Index = index, Name = v.Name.Select(c => (int)c).ToArray(), Attributes = (int)v.Attributes }; }).ToArray(),
					Constants = scope.Constants.Select(c => new ConstantNode { Name = c.Name.Select(v => (int)v).ToArray(), Type = EditStructuredSignatureCodec.Capture(c.Type, bind), Value = AttributeSnapshotCandidate.CaptureValue(c.Value, bind) }).ToArray(),
					Children = scope.Scopes.Select(Visit).ToArray() };
			}
			finally { active.Remove(scope); }
		}
		return body.PdbMethod?.Scope == null ? null : Visit(body.PdbMethod.Scope);
	}
	public static PdbScope? Restore(Node? node, CilBody body, Func<string, IMDTokenProvider> resolve, Func<string, PdbImportScope> resolveImport) {
		if (node == null) return null;
		Instruction? Target(int index) => index == -1 ? null : index >= 0 && index < body.Instructions.Count ? body.Instructions[index] : throw new InvalidDataException("Invalid PDB boundary");
		string Text(int[] chars) => new(chars.Select(c => checked((char)c)).ToArray());
		var scope = new PdbScope { Start = Target(node.Start), End = Target(node.End) };
		if (node.ImportScope != null) scope.ImportScope = resolveImport(node.ImportScope);
		foreach (var local in node.Locals) {
			if (local.Index < 0 || local.Index >= body.Variables.Count) throw new InvalidDataException("Invalid PDB local");
			scope.Variables.Add(new PdbLocal(body.Variables[local.Index], Text(local.Name), (PdbLocalAttributes)local.Attributes));
		}
		foreach (var constant in node.Constants) scope.Constants.Add(new PdbConstant(Text(constant.Name), EditStructuredSignatureCodec.Restore(constant.Type, resolve), AttributeSnapshotCandidate.RestoreValue(constant.Value, resolve)));
		foreach (var ns in node.Namespaces) scope.Namespaces.Add(Text(ns));
		foreach (var child in node.Children) scope.Scopes.Add(Restore(child, body, resolve, resolveImport)!);
		return scope;
	}
}
