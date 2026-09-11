using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet;
using dnlib.DotNet.Pdb.Symbols;

internal static class SequencePointSnapshotCandidate {
	sealed class DocumentAdapter : SymbolDocument {
		readonly DocumentNode node;
		public DocumentAdapter(DocumentNode node) => this.node = node;
		public override string URL => new string(node.Url.Select(c => checked((char)c)).ToArray());
		public override Guid Language => node.Language;
		public override Guid LanguageVendor => node.Vendor;
		public override Guid DocumentType => node.Type;
		public override Guid CheckSumAlgorithmId => node.ChecksumAlgorithm;
		public override byte[] CheckSum => node.Checksum == null ? null! : (byte[])node.Checksum.Clone();
		public override PdbCustomDebugInfo[] CustomDebugInfos => Array.Empty<PdbCustomDebugInfo>();
		public override MDToken? MDToken => null;
	}
	// dnlib's string constructor leaves CustomDebugInfos null; its public
	// SymbolDocument constructor initializes the collection required by writer.
	public static PdbDocument CreateDocument(DocumentNode node) => new(new DocumentAdapter(node));
	public static DocumentNode CaptureDocument(PdbDocument value) {
		if (value.CustomDebugInfos.Count != 0) throw new InvalidDataException("Document custom debug data outside candidate");
		return new DocumentNode { Url = value.Url.Select(c => (int)c).ToArray(), Language = value.Language, Vendor = value.LanguageVendor,
			Type = value.DocumentType, ChecksumAlgorithm = value.CheckSumAlgorithmId, Checksum = value.CheckSum == null ? null : (byte[])value.CheckSum.Clone() };
	}
	public sealed class DocumentNode {
		public int[] Url { get; set; } = Array.Empty<int>();
		public Guid Language { get; set; }
		public Guid Vendor { get; set; }
		public Guid Type { get; set; }
		public Guid ChecksumAlgorithm { get; set; }
		public byte[]? Checksum { get; set; }
	}
	public sealed class PointNode {
		public int Instruction { get; set; }
		public int Document { get; set; }
		public int StartLine { get; set; }
		public int StartColumn { get; set; }
		public int EndLine { get; set; }
		public int EndColumn { get; set; }
	}
	public sealed class Node {
		public DocumentNode[] Documents { get; set; } = Array.Empty<DocumentNode>();
		public PointNode[] Points { get; set; } = Array.Empty<PointNode>();
	}
	public static Node Capture(CilBody body) {
		var documents = new List<PdbDocument>(); var points = new List<PointNode>();
		for (var i = 0; i < body.Instructions.Count; i++) {
			var p = body.Instructions[i].SequencePoint; if (p == null) continue;
			if (p.Document == null || p.Document.CustomDebugInfos.Count != 0) throw new InvalidDataException("Document custom debug data outside candidate");
			var index = documents.FindIndex(d => ReferenceEquals(d, p.Document));
			if (index < 0) { index = documents.Count; documents.Add(p.Document); }
			points.Add(new PointNode { Instruction = i, Document = index, StartLine = p.StartLine, StartColumn = p.StartColumn, EndLine = p.EndLine, EndColumn = p.EndColumn });
		}
		return new Node { Points = points.ToArray(), Documents = documents.Select(CaptureDocument).ToArray() };
	}
	public static void Restore(Node node, CilBody body) {
		var documents = node.Documents.Select(CreateDocument).ToArray();
		var seen = new HashSet<int>();
		foreach (var p in node.Points) {
			if (p.Instruction < 0 || p.Instruction >= body.Instructions.Count || !seen.Add(p.Instruction) || p.Document < 0 || p.Document >= documents.Length) throw new InvalidDataException("Invalid sequence point location");
			body.Instructions[p.Instruction].SequencePoint = new SequencePoint { Document = documents[p.Document], StartLine = p.StartLine,
				StartColumn = p.StartColumn, EndLine = p.EndLine, EndColumn = p.EndColumn };
		}
	}
}
