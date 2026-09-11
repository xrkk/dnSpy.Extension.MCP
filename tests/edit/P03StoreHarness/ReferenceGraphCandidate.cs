using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;

// Test-only reference graph for the cross-reload experiment. No product format.
// Reference attributes are attached only after structural references exist.
internal sealed class ReferenceGraphCandidate {
	public sealed class Node {
		public string Kind { get; set; } = "";
		public uint SourceToken { get; set; }
		public byte[]? Name { get; set; }
		public byte[]? Namespace { get; set; }
		public string? Link { get; set; }
		public string? Address { get; set; }
		public string? Version { get; set; }
		public uint Attributes { get; set; }
		public byte[]? Culture { get; set; }
		public byte[]? Hash { get; set; }
		public byte[]? Key { get; set; }
		public bool FullKey { get; set; }
		public EditStructuredSignatureCodec.TypeNode? Type { get; set; }
		public EditStructuredSignatureCodec.CallNode? Signature { get; set; }
		public byte[]? Extra { get; set; }
		public AttributeSnapshotCandidate.AttributeNode[] CustomAttributes { get; set; } = Array.Empty<AttributeSnapshotCandidate.AttributeNode>();
	}
	public Dictionary<string, Node> Nodes { get; } = new();
	readonly Dictionary<object, string> identities = new(ReferenceEqualityComparer.Instance);
	readonly ModuleDef module;
	public ReferenceGraphCandidate(ModuleDef module) => this.module = module;
	static byte[]? Bytes(UTF8String? value) => value?.Data == null ? null : (byte[])value.Data.Clone();
	public string Bind(IMDTokenProvider value) {
		if (identities.TryGetValue(value, out var known) && Nodes.ContainsKey(known)) return known;
		var id = known ?? "r" + identities.Count;
		if (known == null) identities.Add(value, id);
		var node = new Node { SourceToken = value.MDToken.Raw };
		Nodes.Add(id, node);
		switch (value) {
		case ModuleDef current when ReferenceEquals(current, module): node.Kind = "module"; break;
		case TypeDef or MethodDef or FieldDef: node.Kind = "definition"; node.Address = EditDefinitionAddress.Capture(module, value); break;
		case AssemblyRef assembly:
			node.Kind = "assembly"; node.Name = Bytes(assembly.Name); node.Culture = Bytes(assembly.Culture);
			node.Version = assembly.Version.ToString(); node.Attributes = (uint)assembly.Attributes;
			node.Hash = assembly.Hash == null ? null : (byte[])assembly.Hash.Clone();
			node.Key = assembly.PublicKeyOrToken?.Data == null ? null : (byte[])assembly.PublicKeyOrToken.Data.Clone();
			node.FullKey = assembly.PublicKeyOrToken is PublicKey; break;
		case ModuleRef reference: node.Kind = "module_ref"; node.Name = Bytes(reference.Name); break;
		case TypeRef type:
			node.Kind = "type_ref"; node.Name = Bytes(type.Name); node.Namespace = Bytes(type.Namespace);
			node.Link = type.ResolutionScope == null ? null : Bind(type.ResolutionScope); break;
		case TypeSpec type:
			node.Kind = "type_spec"; node.Type = EditStructuredSignatureCodec.Capture(type.TypeSig, Bind);
			node.Extra = type.ExtraData == null ? null : (byte[])type.ExtraData.Clone(); break;
		case MemberRef member:
			node.Kind = "member_ref"; node.Name = Bytes(member.Name); node.Link = Bind(member.Class);
			node.Signature = EditStructuredSignatureCodec.Capture(member.Signature, Bind); break;
		case MethodSpec method:
			node.Kind = "method_spec"; node.Link = Bind(method.Method); node.Signature = EditStructuredSignatureCodec.Capture(method.Instantiation, Bind); break;
		default: throw new InvalidDataException("Reference outside candidate: " + value.GetType().Name);
		}
		// Definition/module nodes are external anchors, not deletion snapshots.
		if (node.Kind != "module" && node.Kind != "definition" && value is IHasCustomAttribute attributes)
			node.CustomAttributes = attributes.CustomAttributes.Select(a => AttributeSnapshotCandidate.Capture(a, Bind)).ToArray();
		return id;
	}
	public static Func<string, IMDTokenProvider> Restore(Dictionary<string, Node> nodes, ModuleDef target,
		IReadOnlyDictionary<string, IMDTokenProvider>? surviving = null) {
		var restored = new Dictionary<string, IMDTokenProvider>();
		if (surviving != null) foreach (var pair in surviving) {
			if (!nodes.ContainsKey(pair.Key)) throw new InvalidDataException("Unknown surviving binding");
			restored.Add(pair.Key, pair.Value);
		}
		var active = new HashSet<string>();
		UTF8String? Text(byte[]? bytes) => bytes == null ? null : new UTF8String((byte[])bytes.Clone());
		IMDTokenProvider Resolve(string id) {
			if (restored.TryGetValue(id, out var cached)) return cached;
			if (!active.Add(id)) throw new InvalidDataException("Cyclic reference graph");
			try {
				if (!nodes.TryGetValue(id, out var node)) throw new InvalidDataException("Missing reference node");
				IMDTokenProvider value;
				switch (node.Kind) {
				case "module": value = target; break;
				case "definition": value = EditDefinitionAddress.Resolve(target, node.Address!); break;
				case "assembly": value = new AssemblyRefUser(Text(node.Name), System.Version.Parse(node.Version!),
					node.FullKey ? new PublicKey(node.Key) : new PublicKeyToken(node.Key), Text(node.Culture)) {
					Attributes = (AssemblyAttributes)node.Attributes, Hash = node.Hash == null ? null : (byte[])node.Hash.Clone(),
				}; break;
				case "module_ref": value = new ModuleRefUser(target, Text(node.Name)); break;
				case "type_ref": value = new TypeRefUser(target, Text(node.Namespace), Text(node.Name), node.Link == null ? null : (IResolutionScope)Resolve(node.Link)); break;
				case "type_spec": value = new TypeSpecUser(EditStructuredSignatureCodec.Restore(node.Type!, Resolve)) { ExtraData = node.Extra }; break;
				case "member_ref": value = new MemberRefUser(target) { Name = Text(node.Name), Signature = EditStructuredSignatureCodec.Restore(node.Signature!, Resolve), Class = (IMemberRefParent)Resolve(node.Link!) }; break;
				case "method_spec": value = new MethodSpecUser((IMethodDefOrRef)Resolve(node.Link!), (GenericInstMethodSig)EditStructuredSignatureCodec.Restore(node.Signature!, Resolve)); break;
				default: throw new InvalidDataException("Unknown reference kind");
				}
				restored.Add(id, value); return value;
			}
			finally { active.Remove(id); }
		}
		// Resolve and validate all descriptors before returning any candidate to
		// a mutating caller. This doesn't choose anchors; the caller must supply
		// operation-prestate bindings, including each surviving scope edge.
		foreach (var id in nodes.Keys) Resolve(id);
		foreach (var pair in nodes) {
			if (pair.Value.Kind == "module" || pair.Value.Kind == "definition") {
				if (pair.Value.CustomAttributes.Length != 0) throw new InvalidDataException("Attributes cannot mutate an external anchor");
				continue;
			}
			if (surviving != null && surviving.ContainsKey(pair.Key)) continue;
			var attributes = restored[pair.Key] as IHasCustomAttribute ?? throw new InvalidDataException("No attribute attachment");
			foreach (var attribute in pair.Value.CustomAttributes)
				attributes.CustomAttributes.Add(AttributeSnapshotCandidate.Restore(attribute, Resolve));
		}
		var verify = new ReferenceGraphCandidate(target);
		foreach (var pair in restored) {
			if (!verify.identities.TryAdd(pair.Value, pair.Key)) throw new InvalidDataException("Distinct reference nodes collapsed");
		}
		foreach (var pair in restored) verify.Bind(pair.Value);
		if (verify.Nodes.Count != nodes.Count) throw new InvalidDataException("Unbound surviving reference edge");
		foreach (var pair in nodes) {
			var actual = verify.Nodes[pair.Key];
			actual.SourceToken = pair.Value.SourceToken; // Provenance is not a reloaded identity.
			if (actual.CustomAttributes.Length == pair.Value.CustomAttributes.Length)
				for (var i = 0; i < actual.CustomAttributes.Length; i++) actual.CustomAttributes[i].BlobOffset = pair.Value.CustomAttributes[i].BlobOffset;
			if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(pair.Value))
				throw new InvalidDataException("Surviving/reference descriptor mismatch: " + pair.Key);
		}
		return Resolve;
	}
}
