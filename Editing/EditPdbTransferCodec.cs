using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>
/// P06 production codec for the optional symbol payload rows of the frozen
/// operation language: sequence points, PdbScope trees and method custom debug
/// info.  Rows are plain JSON with the operation reference grammar (tokens /
/// object IDs) so the private copy, the live module and the checkpoint replay
/// all materialize the same symbol state from the same operation (S02
/// boundaries 1-3: Portable PDB payloads stay in memory, points travel with
/// the body, documents are never hand-inserted into PdbState.Documents —
/// application reuses existing document rows through PdbState and only adds
/// rows the writer will actually reference).
/// </summary>
internal static class EditPdbTransferCodec {
	sealed class ReferenceComparer<T> : IEqualityComparer<T> {
		public static readonly ReferenceComparer<T> Instance = new();
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
		public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj!);
	}

	// C# Roslyn portable PDBs use the Text document type and SHA-256 checksums;
	// rows outside that identity domain are rejected rather than rewritten.
	public static readonly Guid TextDocumentType = new("5a869d0b-6611-11d3-bd2a-0000f80849bd");
	public static readonly Guid Sha256ChecksumAlgorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");
	static readonly Guid CSharpLanguage = new("3f5162f8-07c6-11d3-9053-00c04fa302a1");
	static readonly Guid MicrosoftVendor = new("994b45c4-e6e9-11d2-903f-00c04fa302a4");

	public sealed class DocumentRow {
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;
		[JsonPropertyName("language")]
		public string? Language { get; set; }
		[JsonPropertyName("vendor")]
		public string? Vendor { get; set; }
		[JsonPropertyName("hash")]
		public string? Hash { get; set; }
		[JsonPropertyName("type")]
		public string? Type { get; set; }
		[JsonPropertyName("hashAlgorithm")]
		public string? HashAlgorithm { get; set; }
		[JsonPropertyName("hash_algorithm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? HashAlgorithmLegacyInput { get => null; set { if (value != null) HashAlgorithm = value; } }
	}
	public sealed class Location {
		[JsonPropertyName("il")]
		public int Il { get; set; }
		[JsonPropertyName("line")]
		public int Line { get; set; }
		[JsonPropertyName("column")]
		public int Column { get; set; }
	}
	public sealed class PointRow {
		[JsonPropertyName("document")]
		public DocumentRow Document { get; set; } = new();
		[JsonPropertyName("start")]
		public Location Start { get; set; } = new();
		[JsonPropertyName("end")]
		public Location End { get; set; } = new();
	}
	public sealed class LocalRow {
		[JsonPropertyName("index")]
		public int Index { get; set; }
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;
		[JsonPropertyName("attributes")]
		public int Attributes { get; set; }
	}
	public sealed class ConstantRow {
		[JsonPropertyName("name")]
		public string Name { get; set; } = string.Empty;
		[JsonPropertyName("type")]
		public string Type { get; set; } = string.Empty;
		[JsonPropertyName("valueKind")]
		public string ValueKind { get; set; } = string.Empty;
		[JsonPropertyName("value_kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? ValueKindLegacyInput { get => null; set { if (value != null) ValueKind = value; } }
		[JsonPropertyName("value")]
		public JsonElement Value { get; set; }
	}
	public sealed class ScopeRow {
		[JsonPropertyName("startIl")]
		public int StartIl { get; set; }
		[JsonPropertyName("start_il"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? StartIlLegacyInput { get => null; set { if (value != null) StartIl = value.Value; } }
		[JsonPropertyName("endIl")]
		public int EndIl { get; set; }
		[JsonPropertyName("end_il"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public int? EndIlLegacyInput { get => null; set { if (value != null) EndIl = value.Value; } }
		[JsonPropertyName("locals")]
		public LocalRow[] Locals { get; set; } = Array.Empty<LocalRow>();
		[JsonPropertyName("constants")]
		public ConstantRow[] Constants { get; set; } = Array.Empty<ConstantRow>();
		[JsonPropertyName("namespaces")]
		public string[] Namespaces { get; set; } = Array.Empty<string>();
		[JsonPropertyName("importScope")]
		public string? ImportScope { get; set; }
		[JsonPropertyName("import_scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? ImportScopeLegacyInput { get => null; set { if (value != null) ImportScope = value; } }
		[JsonPropertyName("scopes")]
		public ScopeRow[] Scopes { get; set; } = Array.Empty<ScopeRow>();
	}
	public sealed class ImportScopeRow {
		[JsonPropertyName("parent")]
		public string? Parent { get; set; }
		[JsonPropertyName("imports")]
		public ImportRow[] Imports { get; set; } = Array.Empty<ImportRow>();
	}
	/// <summary>Wire shape of the import-scope map: the closed-schema contract
	/// carries the importer-assigned ids as an array of rows.</summary>
	public sealed class ImportScopeWireRow {
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;
		[JsonPropertyName("parent")]
		public string? Parent { get; set; }
		[JsonPropertyName("imports")]
		public ImportRow[] Imports { get; set; } = Array.Empty<ImportRow>();
	}

	public static List<ImportScopeWireRow> ToWireRows(IReadOnlyDictionary<string, ImportScopeRow> scopes) =>
		scopes.Select(pair => new ImportScopeWireRow { Id = pair.Key, Parent = pair.Value.Parent, Imports = pair.Value.Imports }).ToList();

	public static Dictionary<string, ImportScopeRow> FromWireRows(IEnumerable<ImportScopeWireRow>? rows) {
		var map = new Dictionary<string, ImportScopeRow>(StringComparer.Ordinal);
		foreach (var row in rows ?? Array.Empty<ImportScopeWireRow>())
			map[row.Id] = new ImportScopeRow { Parent = row.Parent, Imports = row.Imports };
		return map;
	}
	public sealed class ImportRow {
		[JsonPropertyName("kind")]
		public string Kind { get; set; } = string.Empty;
		[JsonPropertyName("alias")]
		public string? Alias { get; set; }
		[JsonPropertyName("namespace")]
		public string? Namespace { get; set; }
		[JsonPropertyName("assemblyName")]
		public string? AssemblyName { get; set; }
		[JsonPropertyName("assembly_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? AssemblyNameLegacyInput { get => null; set { if (value != null) AssemblyName = value; } }
		[JsonPropertyName("type")]
		public string? Type { get; set; }
	}
	public sealed class InstructionRow {
		[JsonPropertyName("method")]
		public string Method { get; set; } = string.Empty;
		[JsonPropertyName("index")]
		public int Index { get; set; } = -1;
	}
	public sealed class AsyncStepRow {
		[JsonPropertyName("yield")]
		public InstructionRow Yield { get; set; } = new();
		[JsonPropertyName("breakpoint")]
		public InstructionRow Breakpoint { get; set; } = new();
	}
	public sealed class StateMapRow {
		[JsonPropertyName("syntax_offset")] public int SyntaxOffset { get; set; }
		[JsonPropertyName("state")] public int State { get; set; }
	}
	public sealed class CdiRow {
		[JsonPropertyName("kind")]
		public string Kind { get; set; } = string.Empty;
		[JsonPropertyName("reference")]
		public string? Reference { get; set; }
		[JsonPropertyName("instruction")]
		public InstructionRow? Instruction { get; set; }
		[JsonPropertyName("steps")]
		public AsyncStepRow[]? Steps { get; set; }
		[JsonPropertyName("ranges")]
		public int[][]? Ranges { get; set; }
		[JsonPropertyName("states")]
		public StateMapRow[]? States { get; set; }
		[JsonPropertyName("type")]
		public string? Type { get; set; }
		[JsonPropertyName("documents")]
		public DocumentRow[]? Documents { get; set; }
		[JsonPropertyName("text")]
		public string? Text { get; set; }
		[JsonPropertyName("texts")]
		public string[]? Texts { get; set; }
		[JsonPropertyName("flags")]
		public bool[]? Flags { get; set; }
		[JsonPropertyName("base64")]
		public string? Base64 { get; set; }
	}

	sealed class RowDocument : dnlib.DotNet.Pdb.Symbols.SymbolDocument {
		readonly DocumentRow row;
		public RowDocument(DocumentRow row) => this.row = row;
		public override string URL => row.Name;
		public override Guid Language => ParseGuid(row.Language) ?? CSharpLanguage;
		public override Guid LanguageVendor => ParseGuid(row.Vendor) ?? MicrosoftVendor;
		public override Guid DocumentType => ParseGuid(row.Type) ?? TextDocumentType;
		public override Guid CheckSumAlgorithmId => ParseGuid(row.HashAlgorithm) ?? Sha256ChecksumAlgorithm;
		public override byte[] CheckSum => row.Hash == null ? null! : Convert.FromBase64String(row.Hash);
		public override PdbCustomDebugInfo[] CustomDebugInfos => Array.Empty<PdbCustomDebugInfo>();
		public override MDToken? MDToken => null;
	}

	/// <summary>Reuse the target module's document rows through PdbState; only
	/// add rows no existing document equals (S02 boundary 3).  Construction goes
	/// through the SymbolDocument ctor — the direct PdbDocument ctor leaves the
	/// writer-required CustomDebugInfos list null (S02 spike fact).</summary>
	public static PdbDocument ResolveDocument(ModuleDef module, DocumentRow row) {
		ValidateDocumentRows(module, new[] { row });
		var candidate = Candidate(row);
		if (module.PdbState == null) module.SetPdbState(new PdbState(module, PdbFileKind.EmbeddedPortablePDB));
		var state = module.PdbState;
		var existing = state.GetExisting(candidate);
		if (existing != null) return existing;
		state.Add(candidate);
		return candidate;
	}

	/// <summary>dnlib's PdbState matches URLs without considering symbol metadata.
	/// Check the complete normalized document key before any body/target state is
	/// changed, including collisions between two rows in the same operation or
	/// import plan.  URL casing follows dnlib's existing equivalence rule.</summary>
	public static void ValidateDocumentRows(ModuleDef module, IEnumerable<DocumentRow> rows) {
		var byUrl = new Dictionary<string, PdbDocument>(StringComparer.OrdinalIgnoreCase);
		if (module.PdbState != null)
			foreach (var existing in module.PdbState.Documents)
				Remember(existing);
		foreach (var row in rows)
			Remember(Candidate(row));

		void Remember(PdbDocument document) {
			if (byUrl.TryGetValue(document.Url, out var prior)) {
				if (!SameIdentity(prior, document))
					throw Reject("document URL has a different language, vendor, type, checksum algorithm, or checksum: " + document.Url);
			}
			else byUrl.Add(document.Url, document);
		}
	}

	static PdbDocument Candidate(DocumentRow row) {
		try { return new PdbDocument(new RowDocument(row)); }
		catch (FormatException) { throw Reject("document checksum is not valid base64: " + row.Name); }
	}

	static bool SameIdentity(PdbDocument left, PdbDocument right) =>
		string.Equals(left.Url, right.Url, StringComparison.OrdinalIgnoreCase)
		&& left.Language == right.Language && left.LanguageVendor == right.LanguageVendor
		&& left.DocumentType == right.DocumentType && left.CheckSumAlgorithmId == right.CheckSumAlgorithmId
		&& (left.CheckSum == null ? right.CheckSum == null
			: right.CheckSum != null && left.CheckSum.SequenceEqual(right.CheckSum));

	static Guid? ParseGuid(string? text) =>
		text != null && Guid.TryParseExact(text, "D", out var value) ? value : null;
	static string GuidText(Guid value) => value.ToString("D");

	static DocumentRow ToRow(PdbDocument document) => new() {
		Name = document.Url,
		Language = GuidText(document.Language),
		Vendor = GuidText(document.LanguageVendor),
		Hash = document.CheckSum == null ? null : Convert.ToBase64String(document.CheckSum),
		Type = GuidText(document.DocumentType),
		HashAlgorithm = GuidText(document.CheckSumAlgorithmId),
	};

	public static List<PointRow> CapturePoints(CilBody body) {
		var rows = new List<PointRow>();
		for (var index = 0; index < body.Instructions.Count; index++) {
			var point = body.Instructions[index].SequencePoint;
			if (point?.Document == null) continue;
			if ((point.Document.CustomDebugInfos?.Count ?? 0) != 0)
				throw Reject("document custom debug data is outside the P06 symbol domain: " + point.Document.Url);
			rows.Add(new PointRow {
				Document = ToRow(point.Document),
				Start = new Location { Il = index, Line = point.StartLine, Column = point.StartColumn },
				End = new Location { Line = point.EndLine, Column = point.EndColumn },
			});
		}
		return rows;
	}

	public static void ApplyPoints(ModuleDef module, CilBody body, IEnumerable<PointRow> rows) {
		var pending = rows.ToArray();
		var seen = new HashSet<int>();
		foreach (var row in pending) {
			if (row.Start.Il < 0 || row.Start.Il >= body.Instructions.Count || !seen.Add(row.Start.Il))
				throw Reject("sequence point location is outside the body");
		}
		ValidateDocumentRows(module, pending.Select(row => row.Document));
		foreach (var row in pending) {
			var document = ResolveDocument(module, row.Document);
			body.Instructions[row.Start.Il].SequencePoint = new SequencePoint {
				Document = document,
				StartLine = row.Start.Line, StartColumn = row.Start.Column,
				EndLine = row.End.Line, EndColumn = row.End.Column,
			};
		}
	}

	/// <summary>Capture the body's root scope as a payload row.  References in
	/// scopes are plain names (locals are body slots; constants carry signature
	/// text); import scopes are collected into <paramref name="importScopes"/>
	/// because parents may be shared between scopes.</summary>
	public static ScopeRow? CaptureScope(CilBody body, Dictionary<string, ImportScopeRow> importScopes) {
		var root = body.PdbMethod?.Scope;
		if (root == null) return null;
		var identities = new Dictionary<PdbImportScope, string>(ReferenceComparer<PdbImportScope>.Instance);
		return Visit(root);

		ScopeRow Visit(PdbScope scope) {
			if (scope.CustomDebugInfos.Count != 0 || scope.Variables.Any(v => v.CustomDebugInfos.Count != 0) ||
				scope.Constants.Any(c => c.CustomDebugInfos.Count != 0))
				throw Reject("PDB custom debug data on a scope is outside the P06 symbol domain");
			return new ScopeRow {
				StartIl = Boundary(body, scope.Start),
				EndIl = Boundary(body, scope.End),
				Locals = scope.Variables.Select(v => new LocalRow {
					Index = Slot(body, v.Local),
					Name = v.Name,
					Attributes = (int)v.Attributes,
				}).ToArray(),
				Constants = scope.Constants.Select(c => new ConstantRow {
					Name = c.Name,
					Type = SigText(c.Type),
					ValueKind = c.Value == null ? "null" : c.Value.GetType().Name,
					Value = JsonSerializer.SerializeToElement(ConstantValue(c.Value)),
				}).ToArray(),
				Namespaces = scope.Namespaces.ToArray(),
				ImportScope = scope.ImportScope == null ? null : BindImportScope(scope.ImportScope),
				Scopes = scope.Scopes.Select(Visit).ToArray(),
			};
		}

		string BindImportScope(PdbImportScope scope) {
			if (identities.TryGetValue(scope, out var existing)) return existing;
			if (scope.CustomDebugInfos.Count != 0) throw Reject("PDB import custom debug data is outside the P06 symbol domain");
			var id = "i" + identities.Count.ToString(CultureInfo.InvariantCulture);
			identities.Add(scope, id);
			var row = new ImportScopeRow {
				Parent = scope.Parent == null ? null : BindImportScope(scope.Parent),
				Imports = scope.Imports.Select(import => import switch {
					PdbImportNamespace ns => new ImportRow { Kind = "namespace", Namespace = ns.TargetNamespace },
					PdbImportAssemblyNamespace assemblyNs => new ImportRow { Kind = "assembly_namespace", AssemblyName = assemblyNs.TargetAssembly?.Name?.String, Namespace = assemblyNs.TargetNamespace },
					PdbImportType type => new ImportRow { Kind = "type", Type = type.TargetType?.FullName },
					PdbImportXmlNamespace xml => new ImportRow { Kind = "xml", Alias = xml.Alias, Namespace = xml.TargetNamespace },
					PdbImportAssemblyReferenceAlias alias => new ImportRow { Kind = "assembly_reference_alias", Alias = alias.Alias },
					PdbAliasAssemblyReference aliasAssembly => new ImportRow { Kind = "alias_assembly", Alias = aliasAssembly.Alias, AssemblyName = aliasAssembly.TargetAssembly?.Name?.String },
					PdbAliasNamespace aliasNs => new ImportRow { Kind = "alias_namespace", Alias = aliasNs.Alias, Namespace = aliasNs.TargetNamespace },
					PdbAliasAssemblyNamespace aliasAssemblyNs => new ImportRow { Kind = "alias_assembly_namespace", Alias = aliasAssemblyNs.Alias, AssemblyName = aliasAssemblyNs.TargetAssembly?.Name?.String, Namespace = aliasAssemblyNs.TargetNamespace },
					PdbAliasType aliasType => new ImportRow { Kind = "alias_type", Alias = aliasType.Alias, Type = aliasType.TargetType?.FullName },
					_ => throw Reject("PDB import kind is outside the P06 symbol domain"),
				}).ToArray(),
			};
			importScopes[id] = row;
			return id;
		}
	}

	static int Boundary(CilBody body, Instruction? instruction) =>
		instruction == null ? -1 : body.Instructions.IndexOf(instruction) is var index && index >= 0
			? index : throw Reject("a PDB scope boundary is detached from the body");
	static int Slot(CilBody body, Local local) =>
		body.Variables.IndexOf(local) is var index && index >= 0 ? index : throw Reject("a PDB local is detached from the body");

	static object ConstantValue(object? value) => value switch {
		bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or string or char => value!,
		_ => throw Reject("a PDB constant value is outside the P06 symbol domain"),
	};

	public static PdbScope? RestoreScope(ScopeRow? row, CilBody body, ModuleDef module, IReadOnlyDictionary<string, ImportScopeRow> importRows) {
		if (row == null) return null;
		var scopes = importRows.ToDictionary(pair => pair.Key, _ => new PdbImportScope(), StringComparer.Ordinal);
		foreach (var pair in importRows) {
			var scope = scopes[pair.Key];
			if (pair.Value.Parent != null) {
				if (!scopes.TryGetValue(pair.Value.Parent, out var parent)) throw Reject("unknown PDB import parent");
				scope.Parent = parent;
			}
			foreach (var import in pair.Value.Imports) {
				scope.Imports.Add(import.Kind switch {
					"namespace" => new PdbImportNamespace(import.Namespace),
					"assembly_namespace" => new PdbImportAssemblyNamespace(ResolveAssembly(module, import.AssemblyName), import.Namespace),
					"type" => new PdbImportType(ParseType(module, import.Type)),
					"xml" => new PdbImportXmlNamespace(import.Alias, import.Namespace),
					"assembly_reference_alias" => new PdbImportAssemblyReferenceAlias(import.Alias),
					"alias_assembly" => new PdbAliasAssemblyReference(import.Alias, ResolveAssembly(module, import.AssemblyName)),
					"alias_namespace" => new PdbAliasNamespace(import.Alias!, import.Namespace),
					"alias_assembly_namespace" => new PdbAliasAssemblyNamespace(import.Alias!, ResolveAssembly(module, import.AssemblyName), import.Namespace),
					"alias_type" => new PdbAliasType(import.Alias!, ParseType(module, import.Type)),
					_ => throw Reject("unknown PDB import kind"),
				});
			}
		}
		foreach (var pair in scopes) {
			var seen = new HashSet<PdbImportScope>(ReferenceComparer<PdbImportScope>.Instance);
			for (var current = pair.Value; current != null; current = current.Parent)
				if (!seen.Add(current)) throw Reject("cyclic PDB import parent");
		}
		return Visit(row);

		PdbScope Visit(ScopeRow node) {
			var scope = new PdbScope { Start = InstructionAt(body, node.StartIl), End = InstructionAt(body, node.EndIl) };
			scope.ImportScope = node.ImportScope == null ? null
				: scopes.TryGetValue(node.ImportScope, out var import) ? import : throw Reject("unknown PDB import scope");
			foreach (var local in node.Locals) {
				if (local.Index < 0 || local.Index >= body.Variables.Count) throw Reject("a PDB local is outside the body");
				scope.Variables.Add(new PdbLocal(body.Variables[local.Index], local.Name, (PdbLocalAttributes)local.Attributes));
			}
			foreach (var constant in node.Constants)
				scope.Constants.Add(new PdbConstant(constant.Name, ParseConstantType(module, constant.Type), RestoreConstantValue(constant)));
			foreach (var ns in node.Namespaces) scope.Namespaces.Add(ns);
			foreach (var child in node.Scopes) scope.Scopes.Add(Visit(child));
			return scope;
		}
	}

	static TypeSig ParseConstantType(ModuleDef module, string text) {
		try { return new EditTypeSigParser(module).Parse(text); }
		catch (Exception ex) when (ex is ArgumentException or EditDomainException) { throw Reject("a PDB constant type did not resolve: " + text); }
	}

	static ITypeDefOrRef ParseType(ModuleDef module, string? text) =>
		text == null ? throw Reject("a PDB type reference is missing")
		: new EditTypeSigParser(module).Parse(text).ToTypeDefOrRef() ?? throw Reject("a PDB type reference did not resolve");

	static AssemblyRef ResolveAssembly(ModuleDef module, string? name) =>
		module.GetAssemblyRefs().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
		?? throw Reject("a PDB import references an assembly the target does not reference: " + name);

	static object? RestoreConstantValue(ConstantRow row) => row.ValueKind switch {
		"null" => null,
		"Boolean" => row.Value.GetBoolean(),
		"String" => row.Value.GetString(),
		"Char" => (char)row.Value.GetUInt16(),
		"SByte" => row.Value.GetSByte(), "Byte" => row.Value.GetByte(),
		"Int16" => row.Value.GetInt16(), "UInt16" => row.Value.GetUInt16(),
		"Int32" => row.Value.GetInt32(), "UInt32" => row.Value.GetUInt32(),
		"Int64" => row.Value.GetInt64(), "UInt64" => row.Value.GetUInt64(),
		"Single" => row.Value.GetSingle(), "Double" => row.Value.GetDouble(),
		_ => throw Reject("a PDB constant value is outside the P06 symbol domain"),
	};

	static Instruction? InstructionAt(CilBody body, int index) =>
		index == -1 ? null : index >= 0 && index < body.Instructions.Count
			? body.Instructions[index] : throw Reject("a PDB scope boundary is outside the body");

	/// <summary>Method custom debug info rows.  The instruction rows reference
	/// methods by operation reference grammar strings produced by
	/// <paramref name="bindReference"/> (token or object ID text).</summary>
	public static List<CdiRow> CaptureMethodDebugInfo(MethodDef method, Func<IMDTokenProvider, string> bindReference) {
		var rows = new List<CdiRow>();
		foreach (var info in method.CustomDebugInfos) rows.Add(Capture(info, method, bindReference));
		return rows;
	}

	static CdiRow Capture(PdbCustomDebugInfo info, MethodDef owner, Func<IMDTokenProvider, string> bind) {
		InstructionRow At(MethodDef method, Instruction? instruction) => instruction == null || !method.HasBody
			? new InstructionRow()
			: new InstructionRow { Method = bind(method), Index = method.Body.Instructions.IndexOf(instruction) };
		switch (info) {
		case PdbStateMachineHoistedLocalScopesCustomDebugInfo hoisted:
			return new CdiRow {
				Kind = "hoisted",
				Ranges = hoisted.Scopes.Select(scope => new[] {
					owner.HasBody ? owner.Body.Instructions.IndexOf(scope.Start) : -1,
					owner.HasBody ? owner.Body.Instructions.IndexOf(scope.End) : -1,
				}).ToArray(),
			};
		case PdbAsyncMethodCustomDebugInfo async:
			return new CdiRow {
				Kind = "async",
				Reference = bind(async.KickoffMethod),
				Instruction = At(owner, async.CatchHandlerInstruction),
				Steps = async.StepInfos.Select(step => new AsyncStepRow {
					Yield = At(owner, step.YieldInstruction),
					Breakpoint = At(step.BreakpointMethod, step.BreakpointInstruction),
				}).ToArray(),
			};
		case PdbIteratorMethodCustomDebugInfo iterator:
			return new CdiRow { Kind = "iterator", Reference = bind(iterator.KickoffMethod) };
		case PdbStateMachineTypeNameCustomDebugInfo typeName:
			return new CdiRow { Kind = "state_machine_type_name", Type = bind(typeName.Type) };
		case PdbTypeDefinitionDocumentsDebugInfo documents:
			return new CdiRow { Kind = "type_documents", Documents = documents.Documents.Select(ToRow).ToArray() };
		case PdbDefaultNamespaceCustomDebugInfo ns:
			return new CdiRow { Kind = "default_namespace", Text = ns.Namespace };
		case PortablePdbTupleElementNamesCustomDebugInfo tuple:
			return new CdiRow { Kind = "tuple", Texts = tuple.Names.ToArray() };
		case PdbDynamicLocalVariablesCustomDebugInfo dynamic:
			return new CdiRow { Kind = "dynamic", Flags = (bool[])dynamic.Flags.Clone() };
		case PdbEmbeddedSourceCustomDebugInfo source:
			return new CdiRow { Kind = "embedded_source", Base64 = Convert.ToBase64String(source.SourceCodeBlob) };
		case PdbSourceLinkCustomDebugInfo link:
			return new CdiRow { Kind = "source_link", Base64 = Convert.ToBase64String(link.FileBlob) };
		case PdbEditAndContinueStateMachineStateMapDebugInfo stateMap:
			return new CdiRow {
				Kind = "enc_state_map",
				States = stateMap.StateMachineStates.Select(entry => new StateMapRow {
					SyntaxOffset = entry.SyntaxOffset,
					State = (int)entry.State,
				}).ToArray(),
			};
		case PdbEditAndContinueLocalSlotMapCustomDebugInfo encLocal:
			return new CdiRow { Kind = "enc_local", Base64 = Convert.ToBase64String(encLocal.Data) };
		case PdbEditAndContinueLambdaMapCustomDebugInfo encLambda:
			return new CdiRow { Kind = "enc_lambda", Base64 = Convert.ToBase64String(encLambda.Data) };
		case PdbUnknownCustomDebugInfo unknown:
			return new CdiRow { Kind = "unknown", Text = GuidText(unknown.Guid), Base64 = Convert.ToBase64String(unknown.Data ?? Array.Empty<byte>()) };
		default:
			throw Reject("method custom debug info is outside the P06 symbol domain: " + info.GetType().Name);
		}
	}

	public static void ApplyMethodDebugInfo(ModuleDef module, MethodDef method, IEnumerable<CdiRow> rows, Func<string, IMDTokenProvider> resolveReference) {
		var restored = new List<PdbCustomDebugInfo>();
		foreach (var row in rows) restored.Add(Restore(row, module, method, resolveReference));
		method.CustomDebugInfos.Clear();
		foreach (var info in restored) method.CustomDebugInfos.Add(info);
	}

	static PdbCustomDebugInfo Restore(CdiRow row, ModuleDef module, MethodDef owner, Func<string, IMDTokenProvider> resolve) {
		Instruction? At(InstructionRow value) {
			if (value.Index == -1 && value.Method.Length == 0) return null;
			var method = value.Method.Length == 0 ? owner : resolve(value.Method) as MethodDef ?? throw Reject("a custom debug info method reference is invalid");
			if (value.Index == -1) return null;
			return method.HasBody && value.Index >= 0 && value.Index < method.Body.Instructions.Count
				? method.Body.Instructions[value.Index] : throw Reject("a custom debug info instruction is outside its method");
		}
		switch (row.Kind) {
		case "hoisted": {
			var value = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			foreach (var range in row.Ranges ?? Array.Empty<int[]>()) {
				if (range.Length != 2) throw Reject("a hoisted range is malformed");
				Instruction? Boundary(int index) => index == -1 ? null :
					owner.HasBody && index >= 0 && index < owner.Body.Instructions.Count
						? owner.Body.Instructions[index] : throw Reject("a hoisted range is outside the body");
				value.Scopes.Add(new StateMachineHoistedLocalScope(Boundary(range[0]), Boundary(range[1])));
			}
			return value;
		}
		case "async": {
			var value = new PdbAsyncMethodCustomDebugInfo {
				KickoffMethod = resolve(row.Reference!) as MethodDef ?? throw Reject("the async kickoff method reference is invalid"),
				CatchHandlerInstruction = At(row.Instruction!),
			};
			foreach (var step in row.Steps ?? Array.Empty<AsyncStepRow>()) {
				var yield = At(step.Yield) ?? throw Reject("the async yield instruction is missing");
				var breakpointMethod = resolve(step.Breakpoint.Method) as MethodDef ?? throw Reject("the async breakpoint method reference is invalid");
				var breakpoint = At(step.Breakpoint) ?? throw Reject("the async breakpoint instruction is missing");
				value.StepInfos.Add(new PdbAsyncStepInfo(yield, breakpointMethod, breakpoint));
			}
			return value;
		}
		case "iterator":
			return new PdbIteratorMethodCustomDebugInfo(resolve(row.Reference!) as MethodDef ?? throw Reject("the iterator kickoff method reference is invalid"));
		case "state_machine_type_name":
			return new PdbStateMachineTypeNameCustomDebugInfo(resolve(row.Type!) as TypeDef ?? throw Reject("the state machine type reference is invalid"));
		case "type_documents": {
			var value = new PdbTypeDefinitionDocumentsDebugInfo();
			foreach (var document in row.Documents ?? Array.Empty<DocumentRow>())
				value.Documents.Add(ResolveDocument(module, document));
			return value;
		}
		case "default_namespace": return new PdbDefaultNamespaceCustomDebugInfo(row.Text!);
		case "tuple": {
			var value = new PortablePdbTupleElementNamesCustomDebugInfo();
			foreach (var name in row.Texts ?? Array.Empty<string>()) value.Names.Add(name);
			return value;
		}
		case "dynamic": return new PdbDynamicLocalVariablesCustomDebugInfo((bool[])(row.Flags ?? Array.Empty<bool>()).Clone());
		case "embedded_source": return new PdbEmbeddedSourceCustomDebugInfo(Convert.FromBase64String(row.Base64 ?? string.Empty));
		case "source_link": return new PdbSourceLinkCustomDebugInfo(Convert.FromBase64String(row.Base64 ?? string.Empty));
		case "enc_local": return new PdbEditAndContinueLocalSlotMapCustomDebugInfo(Convert.FromBase64String(row.Base64 ?? string.Empty));
		case "enc_state_map":
			var stateMap = new PdbEditAndContinueStateMachineStateMapDebugInfo();
			stateMap.StateMachineStates.AddRange((row.States ?? Array.Empty<StateMapRow>())
				.Select(entry => new StateMachineStateInfo(entry.SyntaxOffset, (StateMachineState)entry.State)));
			return stateMap;
		case "enc_lambda": return new PdbEditAndContinueLambdaMapCustomDebugInfo(Convert.FromBase64String(row.Base64 ?? string.Empty));
		case "unknown": return new PdbUnknownCustomDebugInfo(Guid.Parse(row.Text!), Convert.FromBase64String(row.Base64 ?? string.Empty));
		default: throw Reject("unknown custom debug info kind: " + row.Kind);
		}
	}

	/// <summary>Canonical type-signature text for the frozen operation language
	/// (inverse of EditTypeSigParser for primitives, full names, generic
	/// variables/instances, arrays, byref and pointers).</summary>
	public static string SigText(TypeSig type, Func<TypeDef, TypeDef?>? rebind = null) => type switch {
		null => throw Reject("a type signature is missing"),
		CorLibTypeSig core => core.TypeDefOrRef?.FullName ?? core.FullName,
		GenericVar typeVar => "!" + typeVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericMVar methodVar => "!!" + methodVar.Number.ToString(CultureInfo.InvariantCulture),
		GenericInstSig instance => SigName(instance.GenericType.TypeDefOrRef, rebind, instance.GenericType.ElementType == ElementType.ValueType)
			+ "<" + string.Join(",", instance.GenericArguments.Select(argument => SigText(argument, rebind))) + ">",
		SZArraySig array => SigText(array.Next, rebind) + "[]",
		ArraySig array => SigText(array.Next, rebind) + "[" + new string(',', Math.Max(0, (int)array.Rank - 1)) + "]",
		ByRefSig byRef => SigText(byRef.Next, rebind) + "&",
		PtrSig pointer => SigText(pointer.Next, rebind) + "*",
		TypeDefOrRefSig reference => SigName(reference.TypeDefOrRef, rebind, reference.ElementType == ElementType.ValueType),
		_ => throw Reject("a type signature is outside the P06 signature text domain: " + type.GetType().Name),
	};

	static string SigName(ITypeDefOrRef? row, Func<TypeDef, TypeDef?>? rebind, bool valueType) {
		var name = row is TypeDef definition && rebind != null && rebind(definition) is { } counterpart
			? counterpart.FullName
			: row?.FullName ?? throw Reject("a type signature has no named reference");
		// The forced prefix pins the element kind: the class/value decision must
		// not depend on the target module's resolver, which differs between the
		// private copy, the live module and the checkpoint replay module.
		return valueType && !(row is CorLibTypeSig) ? "valuetype:" + name : name;
	}

	static EditDomainException Reject(string reason) =>
		new("EDIT_VALIDATION_FAILED", EditWorkspace.ValidationDetails("pdb_transfer", reason));
}
