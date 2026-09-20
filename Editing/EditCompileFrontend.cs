using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Extension.MCP.Tools;
using dnSpy.Extension.MCP.Transport;

namespace dnSpy.Extension.MCP.Editing;

/// <summary>P05 compile frontend: the dnSpy public C# Roslyn compiler exposed
/// as the edit_compile tool. Compilation only — the assembly and Portable PDB
/// payloads stay in memory and are registered for the P06 importer; nothing is
/// imported into any module here (OUT-005 boundary, S02 boundary 5).</summary>
[Export(typeof(IMcpToolProvider))]
[Export(typeof(EditCompileFrontend))]
internal sealed class EditCompileFrontend : IMcpToolProvider, IDisposable {
	const int MaxRegistrations = 8;
	const int MaxDocuments = 32;
	const int MaxDocumentChars = 512 * 1024;

	readonly IDocumentTreeView tree;
	readonly IEnumerable<ILanguageCompilerProvider> compilerProviders;
	readonly Dictionary<string, CompileArtifact> artifacts = new(StringComparer.Ordinal);
	readonly object gate = new();
	bool disposed;

	static readonly string[] ProductTools = { "edit_compile" };
	readonly IReadOnlyList<ToolInfo> tools;

	[ImportingConstructor]
	public EditCompileFrontend(IDocumentTreeView tree, [ImportMany] IEnumerable<ILanguageCompilerProvider> compilerProviders) {
		this.tree = tree;
		this.compilerProviders = compilerProviders;
		tools = new List<ToolInfo> { Tool() };
	}

	public string Name => "edit-compile";
	public IReadOnlyCollection<string> UnadvertisedTools { get; } = Array.Empty<string>();
	public IReadOnlyList<ToolInfo> GetTools() => tools;

	public CallToolResult? ExecuteTool(string toolName, Dictionary<string, object>? arguments, McpCallContext callContext) {
		if (toolName != "edit_compile") return null;
		try {
			return EditWire.Result(Compile(arguments ?? new(), callContext).GetAwaiter().GetResult());
		}
		catch (EditDomainException ex) {
			return EditWire.Result(EditWire.Failure("idle", ex.Code, ex.Details, ex.Message));
		}
		catch (ArgumentException) {
			throw;
		}
		catch (Exception ex) {
			return EditWire.Result(EditWire.Failure("idle", "EDIT_INTERNAL_ERROR", new Dictionary<string, object?> { ["kind"] = "internal", ["correlation_id"] = EditWire.NewId("incident"), ["reason"] = ex.GetType().Name + ": " + ex.Message }));
		}
	}

	async Task<Dictionary<string, object?>> Compile(Dictionary<string, object> args, McpCallContext context) {
		if (!context.IsInitializedSession) throw new EditDomainException("EDIT_OWNER_REQUIRED");
		// CON-022 closure: the compile contract has no analyzer/generator/script/
		// build-task surface — reject any field outside the frozen input schema
		// (additionalProperties=false enforced provider-side because non-edit
		// providers bypass the coordinator's schema validator).
		foreach (var key in args.Keys)
			if (key is not "request_id" and not "assembly_name" and not "compilation_kind" and not "documents" and not "target_platform" and not "references_override")
				throw new ArgumentException("Unknown edit_compile field (the contract has no analyzer/generator/script/build-task surface): " + key, key);
		var assemblyName = RequiredString(args, "assembly_name");
		var kindText = RequiredString(args, "compilation_kind");
		var kind = kindText switch {
			"edit_method" => CompilationKind.EditMethod,
			"edit_class" => CompilationKind.EditClass,
			_ => throw new ArgumentException("compilation_kind must be edit_method or edit_class", "compilation_kind"),
		};
		if (!args.TryGetValue("documents", out var rawDocuments) || rawDocuments is not JsonElement documentsElement || documentsElement.ValueKind != JsonValueKind.Array || documentsElement.GetArrayLength() == 0)
			throw new ArgumentException("documents is required", "documents");
		if (documentsElement.GetArrayLength() > MaxDocuments) throw new ArgumentException("documents exceeds " + MaxDocuments, "documents");
		var documents = new List<CompilerDocumentInfo>();
		foreach (var row in documentsElement.EnumerateArray()) {
			if (row.ValueKind != JsonValueKind.Object) throw new ArgumentException("documents entries must be objects", "documents");
			foreach (var property in row.EnumerateObject())
				if (property.Name is not "path" and not "content")
					throw new ArgumentException("Unknown edit_compile documents field: " + property.Name, "documents");
			var content = RequiredString(row, "content");
			if (content.Length > MaxDocumentChars) throw new ArgumentException("document content exceeds " + MaxDocumentChars, "documents");
			documents.Add(new CompilerDocumentInfo(content, RequiredString(row, "path")));
		}
		var platform = TargetPlatform.AnyCpu;
		if (args.TryGetValue("target_platform", out var platformValue) && platformValue is JsonElement platformElement && platformElement.ValueKind == JsonValueKind.String) {
			platform = platformElement.GetString() switch {
				"x86" => TargetPlatform.X86,
				"x64" => TargetPlatform.X64,
				"anycpu" => TargetPlatform.AnyCpu,
				_ => throw new ArgumentException("target_platform must be x86, x64 or anycpu", "target_platform"),
			};
		}
		var provider = SelectProvider(kind)
			?? throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "csharp_compiler", ["reason"] = "No C# ILanguageCompilerProvider can compile " + kindText });

		var referencePaths = ResolveReferencePaths(assemblyName, args);
		if (referencePaths.Count == 0) throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
			new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "reference_closure", ["reason"] = "No reference assemblies resolved for the target; provide references_override" });

		// The Roslyn provider is a dnSpy UI component: Create/Initialize/AddDocuments
		// touch UI-owned documents and must run on the dispatcher (S02 ran inside
		// the UI event handler). CompileAsync then runs on the request thread's
		// context — its continuations never touch UI objects.
		CompilationResult compilation;
		using (var pinned = new PinnedReferenceSet(referencePaths)) {
			ILanguageCompiler compiler = EditWorkspace.OnDispatcher(() => {
				var created = provider.Create(kind);
				created.InitializeProject(new CompilerProjectInfo(assemblyName, null, pinned.References, pinned, platform));
				created.AddDocuments(documents.ToArray());
				return created;
			});
			compilation = await compiler.CompileAsync(CancellationToken.None);
			EditWorkspace.OnDispatcher(() => { compiler.Dispose(); return 0; });
		}

		var diagnostics = (compilation.Diagnostics ?? Array.Empty<CompilerDiagnostic>())
			.Select(DiagnosticRow).ToArray();
		if (!compilation.Success || compilation.RawFile == null)
			return EditWire.Success("idle", new Dictionary<string, object?> {
				["compile"] = new Dictionary<string, object?> {
					["success"] = false,
					["diagnostics"] = diagnostics,
					["consumable_by_import"] = false,
				},
			});

		var artifact = new CompileArtifact(
			EditWire.NewId("compile"), assemblyName, kindText,
			(byte[])compilation.RawFile.Clone(),
			compilation.DebugFile.RawFile == null ? Array.Empty<byte>() : (byte[])compilation.DebugFile.RawFile.Clone());
		lock (gate) {
			ThrowIfDisposed();
			if (artifacts.Count >= MaxRegistrations && !artifacts.ContainsKey(artifact.CompileId))
				throw new EditDomainException("EDIT_CAPACITY_EXCEEDED", new Dictionary<string, object?> { ["kind"] = "capacity", ["limit"] = "compile_artifacts", ["current"] = artifacts.Count, ["maximum"] = MaxRegistrations });
			artifacts[artifact.CompileId] = artifact;
		}
		return EditWire.Success("idle", new Dictionary<string, object?> {
			["compile"] = new Dictionary<string, object?> {
				["compile_id"] = artifact.CompileId,
				["success"] = true,
				["assembly_name"] = assemblyName,
				["compilation_kind"] = kindText,
				["diagnostics"] = diagnostics,
				["assembly"] = new Dictionary<string, object?> { ["length"] = artifact.Assembly.Length, ["sha256"] = EditWire.Sha256(artifact.Assembly) },
				["portable_pdb"] = new Dictionary<string, object?> { ["length"] = artifact.Pdb.Length, ["sha256"] = artifact.Pdb.Length == 0 ? string.Empty : EditWire.Sha256(artifact.Pdb) },
				["target_platform"] = platform.ToString(),
				["consumable_by_import"] = true,
			},
		});
	}

	ILanguageCompilerProvider? SelectProvider(CompilationKind kind) {
		ILanguageCompilerProvider? byGuid = null, byName = null;
		foreach (var provider in compilerProviders) {
			if (!provider.CanCompile(kind)) continue;
			var typeName = provider.GetType().FullName ?? string.Empty;
			var isCSharp = typeName.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0;
			if (provider.Language == DecompilerConstants.LANGUAGE_CSHARP) {
				if (byGuid != null) return null;  // ambiguous C# providers
				byGuid = provider;
			}
			else if (isCSharp && byName == null) byName = provider;
		}
		return byGuid ?? byName;
	}

	List<string> ResolveReferencePaths(string assemblyName, Dictionary<string, object> args) {
		var paths = new List<string>();
		if (args.TryGetValue("references_override", out var rawOverride) && rawOverride is JsonElement overrideElement && overrideElement.ValueKind == JsonValueKind.Array) {
			foreach (var row in overrideElement.EnumerateArray()) {
				if (row.ValueKind != JsonValueKind.String) throw new ArgumentException("references_override entries must be absolute file paths", "references_override");
				var path = row.GetString()!;
				if (!Path.IsPathRooted(path) || !File.Exists(path))
					throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE", new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "reference_path", ["reason"] = "Reference path must be an existing absolute file: " + path });
				paths.Add(path);
			}
			return paths;
		}
		// The document tree and module graphs are UI-owned WPF objects: resolve
		// the target and its reference closure on the dnSpy dispatcher.
		var closure = EditWorkspace.OnDispatcher(() => {
			ModuleDef? module = null;
			foreach (var node in tree.GetAllModuleNodes()) {
				var candidate = node.Document?.ModuleDef;
				if (candidate?.Assembly?.Name is { } name && string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase)) {
					if (module != null) return (ModuleDef?)null;  // ambiguous target: no closure guess
					module = candidate;
				}
			}
			return module;
		});
		if (closure == null) throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
			new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "reference_closure", ["reason"] = "Target module not found or ambiguous (assembly_name=" + assemblyName + ")" });
		var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// The target itself is the primary reference: an EditClass compilation
		// references the target's own types.
		if (closure.Location is { Length: > 0 } ownLocation && File.Exists(ownLocation)) {
			modules.Add(ownLocation);
			paths.Add(ownLocation);
		}
		var references = EditWorkspace.OnDispatcher(() => closure.GetAssemblyRefs().Select(r => r.Name?.String ?? string.Empty).ToArray());
		foreach (var referenceName in references) {
			if (string.IsNullOrEmpty(referenceName)) continue;
			var loaded = EditWorkspace.OnDispatcher(() => tree.GetAllModuleNodes()
				.Select(n => n.Document?.ModuleDef)
				.Where(m => m != null && string.Equals(m.Assembly?.Name, referenceName, StringComparison.OrdinalIgnoreCase))
				.Cast<ModuleDef>()
				.FirstOrDefault());
			if (loaded?.Location is { Length: > 0 } location && modules.Add(location)) paths.Add(location);
		}
		var corlib = typeof(object).Assembly.Location;
		if (corlib is { Length: > 0 } && modules.Add(corlib)) paths.Add(corlib);
		if (paths.Count == 0) throw new EditDomainException("EDIT_CAPABILITY_UNAVAILABLE",
			new Dictionary<string, object?> { ["kind"] = "capability", ["capability"] = "reference_closure",
				["reason"] = "No reference assemblies resolved: closure=" + closure.Location + " refs=" + references.Length + " corlib=" + corlib });
		return paths;
	}

	static string? TryWriteToTemp(IAssembly assembly) {
		try {
			var path = Path.Combine(Path.GetTempPath(), "dnspy-mcp-ref-" + assembly.Name + ".dll");
			if (!File.Exists(path)) return null;  // in-memory assembly without bytes: skip
			return path;
		}
		catch { return null; }
	}

	static Dictionary<string, object?> DiagnosticRow(CompilerDiagnostic diagnostic) => new() {
		["severity"] = diagnostic.Severity.ToString(),
		["id"] = diagnostic.Id,
		["description"] = diagnostic.Description,
		["filename"] = diagnostic.Filename,
		["line"] = diagnostic.LineLocationSpan?.StartLinePosition.Line,
		["column"] = diagnostic.LineLocationSpan?.StartLinePosition.Character,
	};

	static string RequiredString(Dictionary<string, object> args, string name) {
		if (args.TryGetValue(name, out var raw) && raw is JsonElement element && element.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(element.GetString()))
			return element.GetString()!;
		throw new ArgumentException(name + " is required", name);
	}
	static string RequiredString(JsonElement element, string name) {
		if (element.TryGetProperty(name, out var raw) && raw.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(raw.GetString()))
			return raw.GetString()!;
		throw new ArgumentException(name + " is required", name);
	}
	static Dictionary<string, object?> DiagnosticRowTyped(CompilerDiagnostic diagnostic) => DiagnosticRow(diagnostic);
	static ToolInfo Tool() => new() {
		Name = "edit_compile",
		Description = "Compile C# source through the dnSpy public Roslyn compiler and register the in-memory assembly + Portable PDB payloads for the P06 importer. Compilation only: nothing is imported into any module (OUT-005).",
		InputSchema = CompileSchema(),
		OutputSchema = CompileOutputSchema(),
	};
	static Dictionary<string, object> CompileSchema() => new() {
		["type"] = "object", ["additionalProperties"] = false,
		["required"] = new[] { "request_id", "assembly_name", "compilation_kind", "documents" },
		["properties"] = new Dictionary<string, object> {
			["request_id"] = JsonDoc(new { type = "string", minLength = 1, maxLength = 128 }),
			["assembly_name"] = JsonDoc(new { type = "string", minLength = 1, maxLength = 512 }),
			["compilation_kind"] = JsonDoc(new { type = "string", @enum = new[] { "edit_method", "edit_class" } }),
			["documents"] = JsonDoc(new { type = "array", minItems = 1, maxItems = MaxDocuments, items = new Dictionary<string, object> {
				["type"] = "object", ["additionalProperties"] = false, ["required"] = new[] { "path", "content" },
				["properties"] = new Dictionary<string, object> {
					["path"] = JsonDoc(new { type = "string", minLength = 1, maxLength = 512 }),
					["content"] = JsonDoc(new { type = "string", minLength = 1, maxLength = MaxDocumentChars }),
				} } }),
			["target_platform"] = JsonDoc(new { type = "string", @enum = new[] { "anycpu", "x86", "x64" } }),
			["references_override"] = JsonDoc(new { type = "array", maxItems = 128, items = new { type = "string", minLength = 1, maxLength = 1024 } }),
		},
	};
	static Dictionary<string, object> CompileOutputSchema() => new() {
		["type"] = "object", ["additionalProperties"] = false, ["required"] = new[] { "schema_version", "ok", "state", "result", "warnings", "untrusted_sample_data" },
		["properties"] = new Dictionary<string, object> {
			["schema_version"] = JsonDoc(new { @const = "dnspy.edit.v1" }),
			["ok"] = JsonDoc(new { type = "boolean" }),
			["state"] = JsonDoc(new { type = "string", @enum = new[] { "idle" } }),
			["result"] = JsonDoc(new { type = "object" }),
			["warnings"] = JsonDoc(new { type = "array", items = new { type = "string" } }),
			["untrusted_sample_data"] = JsonDoc(new { type = "boolean" }),
		},
	};
	static Dictionary<string, object> JsonDoc(object anonymous) =>
		JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(anonymous, EditWire.JsonOptions), EditWire.JsonOptions) ?? new();

	void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(EditCompileFrontend)); }

	public void Dispose() {
		lock (gate) {
			disposed = true;
			artifacts.Clear();
		}
	}

	/// <summary>The registered in-memory compile artifact consumed by the P06
	/// importer (compile_id lookup). No other consumer exists by contract.</summary>
	internal CompileArtifact? Lookup(string compileId) {
		lock (gate) {
			return artifacts.TryGetValue(compileId, out var artifact) ? artifact : null;
		}
	}

	internal sealed class CompileArtifact {
		public string CompileId { get; }
		public string AssemblyName { get; }
		public string CompilationKind { get; }
		public byte[] Assembly { get; }
		public byte[] Pdb { get; }
		public CompileArtifact(string compileId, string assemblyName, string compilationKind, byte[] assembly, byte[] pdb) {
			CompileId = compileId; AssemblyName = assemblyName; CompilationKind = compilationKind;
			Assembly = assembly; Pdb = pdb;
		}
	}

	/// <summary>Production port of the S02 spike's pinned reference helper: pins
	/// reference assembly bytes and resolves CompilerMetadataReference by
	/// assembly simple name. The unsafe boundary is this class only.</summary>
	unsafe sealed class PinnedReferenceSet : IAssemblyReferenceResolver, IDisposable {
		readonly List<byte[]> data = new();
		readonly List<GCHandle> handles = new();
		readonly Dictionary<string, CompilerMetadataReference> byName = new(StringComparer.OrdinalIgnoreCase);
		public CompilerMetadataReference[] References { get; }

		public PinnedReferenceSet(IEnumerable<string> paths) {
			var refs = new List<CompilerMetadataReference>();
			foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
				var bytes = File.ReadAllBytes(path);
				var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
				data.Add(bytes);
				handles.Add(handle);
				using var module = ModuleDefMD.Load(bytes);
				var asm = module.Assembly?.ToAssemblyRef() ?? throw new InvalidOperationException("Reference has no assembly: " + path);
				var reference = CompilerMetadataReference.CreateAssemblyReference((void*)handle.AddrOfPinnedObject(), bytes.Length, asm, path);
				refs.Add(reference);
				byName[asm.Name] = reference;
			}
			References = refs.ToArray();
		}

		public CompilerMetadataReference? Resolve(IAssembly asmRef) => byName.TryGetValue(asmRef.Name, out var value) ? value : null;
		public void Dispose() {
			foreach (var handle in handles) handle.Free();
			handles.Clear();
			data.Clear();
		}
	}
}
