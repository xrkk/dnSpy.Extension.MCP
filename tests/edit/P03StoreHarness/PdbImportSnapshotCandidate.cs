using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

internal sealed class PdbImportSnapshotCandidate {
	public sealed class ImportNode {
		public string Kind { get; set; } = "";
		public int[]? Alias { get; set; }
		public int[]? Namespace { get; set; }
		public string? Reference { get; set; }
	}
	public sealed class ScopeNode {
		public string? Parent { get; set; }
		public ImportNode[] Imports { get; set; } = Array.Empty<ImportNode>();
	}
	public Dictionary<string, ScopeNode> Nodes { get; } = new();
	readonly Dictionary<PdbImportScope, string> identities = new(ReferenceEqualityComparer.Instance);
	readonly Func<IMDTokenProvider, string> bindReference;
	public PdbImportSnapshotCandidate(Func<IMDTokenProvider, string> bindReference) => this.bindReference = bindReference;
	static int[]? Text(string? value) => value?.Select(c => (int)c).ToArray();
	public string Bind(PdbImportScope scope) {
		if (identities.TryGetValue(scope, out var existing)) return existing;
		if (scope.CustomDebugInfos.Count != 0) throw new InvalidDataException("Import custom debug data outside candidate");
		var id = "i" + identities.Count; identities.Add(scope, id); Nodes.Add(id, new ScopeNode());
		var node = Nodes[id];
		node.Parent = scope.Parent == null ? null : Bind(scope.Parent);
		node.Imports = scope.Imports.Select(import => import switch {
			PdbImportNamespace x => new ImportNode { Kind = "namespace", Namespace = Text(x.TargetNamespace) },
			PdbImportAssemblyNamespace x => new ImportNode { Kind = "assembly_namespace", Reference = bindReference(x.TargetAssembly), Namespace = Text(x.TargetNamespace) },
			PdbImportType x => new ImportNode { Kind = "type", Reference = bindReference(x.TargetType) },
			PdbImportXmlNamespace x => new ImportNode { Kind = "xml", Alias = Text(x.Alias), Namespace = Text(x.TargetNamespace) },
			PdbImportAssemblyReferenceAlias x => new ImportNode { Kind = "assembly_reference_alias", Alias = Text(x.Alias) },
			PdbAliasAssemblyReference x => new ImportNode { Kind = "alias_assembly", Alias = Text(x.Alias), Reference = bindReference(x.TargetAssembly) },
			PdbAliasNamespace x => new ImportNode { Kind = "alias_namespace", Alias = Text(x.Alias), Namespace = Text(x.TargetNamespace) },
			PdbAliasAssemblyNamespace x => new ImportNode { Kind = "alias_assembly_namespace", Alias = Text(x.Alias), Reference = bindReference(x.TargetAssembly), Namespace = Text(x.TargetNamespace) },
			PdbAliasType x => new ImportNode { Kind = "alias_type", Alias = Text(x.Alias), Reference = bindReference(x.TargetType) },
			_ => throw new InvalidDataException("Unknown PDB import kind"),
		}).ToArray();
		return id;
	}
	public static Func<string, PdbImportScope> Restore(Dictionary<string, ScopeNode> nodes, Func<string, IMDTokenProvider> resolveReference) {
		var scopes = nodes.ToDictionary(pair => pair.Key, _ => new PdbImportScope(), StringComparer.Ordinal);
		string? Text(int[]? chars) => chars == null ? null : new string(chars.Select(c => checked((char)c)).ToArray());
		foreach (var pair in nodes) {
			var scope = scopes[pair.Key]; var node = pair.Value;
			if (node.Parent != null) scope.Parent = scopes.TryGetValue(node.Parent, out var parent) ? parent : throw new InvalidDataException("Unknown PDB import parent");
			foreach (var item in node.Imports) {
				AssemblyRef Assembly() => resolveReference(item.Reference!) as AssemblyRef ?? throw new InvalidDataException("Invalid PDB assembly reference");
				ITypeDefOrRef Type() => resolveReference(item.Reference!) as ITypeDefOrRef ?? throw new InvalidDataException("Invalid PDB type reference");
				scope.Imports.Add(item.Kind switch {
					"namespace" => new PdbImportNamespace(Text(item.Namespace)),
					"assembly_namespace" => new PdbImportAssemblyNamespace(Assembly(), Text(item.Namespace)),
					"type" => new PdbImportType(Type()),
					"xml" => new PdbImportXmlNamespace(Text(item.Alias), Text(item.Namespace)),
					"assembly_reference_alias" => new PdbImportAssemblyReferenceAlias(Text(item.Alias)),
					"alias_assembly" => new PdbAliasAssemblyReference(Text(item.Alias), Assembly()),
					"alias_namespace" => new PdbAliasNamespace(Text(item.Alias), Text(item.Namespace)),
					"alias_assembly_namespace" => new PdbAliasAssemblyNamespace(Text(item.Alias), Assembly(), Text(item.Namespace)),
					"alias_type" => new PdbAliasType(Text(item.Alias), Type()),
					_ => throw new InvalidDataException("Unknown PDB import node"),
				});
			}
		}
		foreach (var pair in scopes) {
			var seen = new HashSet<PdbImportScope>(ReferenceEqualityComparer.Instance); var current = pair.Value;
			while (current != null) { if (!seen.Add(current)) throw new InvalidDataException("Cyclic PDB import parent"); current = current.Parent; }
		}
		return id => scopes.TryGetValue(id, out var scope) ? scope : throw new InvalidDataException("Unknown PDB import scope");
	}
}
