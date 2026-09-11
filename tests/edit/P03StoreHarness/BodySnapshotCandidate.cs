using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Extension.MCP.Editing;

// Test-only IL graph codec. Unsupported PDB scopes are rejected, not discarded.
internal static class BodySnapshotCandidate {
	public sealed class OperandNode {
		public string Kind { get; set; } = "null";
		public int Index { get; set; }
		public int[]? Targets { get; set; }
		public string? Reference { get; set; }
		public EditStructuredSignatureCodec.CallNode? Signature { get; set; }
		public AttributeSnapshotCandidate.ValueNode? Value { get; set; }
	}
	public sealed class InstructionNode {
		public ushort OpCode { get; set; }
		public uint Offset { get; set; }
		public OperandNode Operand { get; set; } = new();
	}
	public sealed class HandlerNode {
		public int Kind { get; set; }
		public int TryStart { get; set; }
		public int TryEnd { get; set; }
		public int HandlerStart { get; set; }
		public int HandlerEnd { get; set; }
		public int FilterStart { get; set; }
		public string? CatchType { get; set; }
	}
	public sealed class Node {
		public bool InitLocals { get; set; }
		public bool KeepOldMaxStack { get; set; }
		public byte HeaderSize { get; set; }
		public ushort MaxStack { get; set; }
		public uint LocalVarSigToken { get; set; }
		public EditStructuredSignatureCodec.TypeNode[] Locals { get; set; } = Array.Empty<EditStructuredSignatureCodec.TypeNode>();
		public InstructionNode[] Instructions { get; set; } = Array.Empty<InstructionNode>();
		public HandlerNode[] Handlers { get; set; } = Array.Empty<HandlerNode>();
		public SequencePointSnapshotCandidate.Node SequencePoints { get; set; } = new();
		public bool HasPdbMethod { get; set; }
		public PdbScopeSnapshotCandidate.Node? Scope { get; set; }
		public Dictionary<string, PdbImportSnapshotCandidate.ScopeNode> Imports { get; set; } = new();
	}
	public static Node Capture(MethodDef method, Func<IMDTokenProvider, string> bind) {
		var body = method.Body ?? throw new InvalidDataException("Missing IL body");
		int Target(Instruction? i) { if (i == null) return -1; var index = body.Instructions.IndexOf(i); if (index < 0) throw new InvalidDataException("Detached instruction"); return index; }
		OperandNode Operand(object? value) {
			switch (value) {
			case null: return new();
			case Instruction i: return new() { Kind = "branch", Index = Target(i) };
			case IList<Instruction> targets: return new() { Kind = "switch", Targets = targets.Select(Target).ToArray() };
			case Local local: {
				var index = body.Variables.IndexOf(local); if (index < 0) throw new InvalidDataException("Detached local"); return new() { Kind = "local", Index = index };
			}
			case Parameter parameter: {
				var index = method.Parameters.IndexOf(parameter); if (index < 0) throw new InvalidDataException("Detached parameter"); return new() { Kind = "parameter", Index = index };
			}
			case CallingConventionSig signature: return new() { Kind = "signature", Signature = EditStructuredSignatureCodec.Capture(signature, bind) };
			case IMDTokenProvider reference: return new() { Kind = "reference", Reference = bind(reference) };
			default: return new() { Kind = "scalar", Value = AttributeSnapshotCandidate.CaptureValue(value, bind) };
			}
		}
		var imports = new PdbImportSnapshotCandidate(bind);
		var scope = PdbScopeSnapshotCandidate.Capture(body, bind, imports.Bind);
		return new Node { InitLocals = body.InitLocals, KeepOldMaxStack = body.KeepOldMaxStack, HeaderSize = body.HeaderSize, MaxStack = body.MaxStack,
			SequencePoints = SequencePointSnapshotCandidate.Capture(body),
			HasPdbMethod = body.PdbMethod != null, Scope = scope, Imports = imports.Nodes,
			LocalVarSigToken = body.LocalVarSigTok, Locals = body.Variables.Select(l => EditStructuredSignatureCodec.Capture(l.Type, bind)).ToArray(),
			Instructions = body.Instructions.Select(i => new InstructionNode { OpCode = unchecked((ushort)i.OpCode.Value), Offset = i.Offset, Operand = Operand(i.Operand) }).ToArray(),
			Handlers = body.ExceptionHandlers.Select(h => new HandlerNode { Kind = (int)h.HandlerType, TryStart = Target(h.TryStart), TryEnd = Target(h.TryEnd),
				HandlerStart = Target(h.HandlerStart), HandlerEnd = Target(h.HandlerEnd), FilterStart = Target(h.FilterStart), CatchType = h.CatchType == null ? null : bind(h.CatchType) }).ToArray() };
	}
	public static CilBody Restore(Node node, MethodDef method, Func<string, IMDTokenProvider> resolve) {
		var body = new CilBody { InitLocals = node.InitLocals, KeepOldMaxStack = node.KeepOldMaxStack, HeaderSize = node.HeaderSize, MaxStack = node.MaxStack, LocalVarSigTok = node.LocalVarSigToken };
		foreach (var local in node.Locals) body.Variables.Add(new Local(EditStructuredSignatureCodec.Restore(local, resolve)));
		foreach (var instruction in node.Instructions) {
			var code = instruction.OpCode;
			var opcode = code < 256 ? OpCodes.OneByteOpCodes[code] : (code & 0xff00) == 0xfe00 ? OpCodes.TwoByteOpCodes[code & 255] : throw new InvalidDataException("Invalid opcode");
			if (opcode == null || unchecked((ushort)opcode.Value) != code) throw new InvalidDataException("Unknown opcode");
			body.Instructions.Add(new Instruction(opcode) { Offset = instruction.Offset });
		}
		Instruction? Target(int index) => index == -1 ? null : index >= 0 && index < body.Instructions.Count ? body.Instructions[index] : throw new InvalidDataException("Invalid instruction index");
		for (var i = 0; i < node.Instructions.Length; i++) {
			var n = node.Instructions[i].Operand;
			body.Instructions[i].Operand = n.Kind switch {
				"null" => null, "branch" => Target(n.Index) ?? throw new InvalidDataException("Null branch target"),
				"switch" => n.Targets!.Select(index => Target(index) ?? throw new InvalidDataException("Null switch target")).ToArray(),
				"local" => n.Index >= 0 && n.Index < body.Variables.Count ? body.Variables[n.Index] : throw new InvalidDataException("Invalid local index"),
				"parameter" => n.Index >= 0 && n.Index < method.Parameters.Count ? method.Parameters[n.Index] : throw new InvalidDataException("Invalid parameter index"),
				"signature" => EditStructuredSignatureCodec.Restore(n.Signature!, resolve),
				"reference" => resolve(n.Reference!), "scalar" => AttributeSnapshotCandidate.RestoreValue(n.Value!, resolve),
				_ => throw new InvalidDataException("Unknown operand kind"),
			};
		}
		foreach (var h in node.Handlers) body.ExceptionHandlers.Add(new ExceptionHandler((ExceptionHandlerType)h.Kind) { TryStart = Target(h.TryStart), TryEnd = Target(h.TryEnd),
			HandlerStart = Target(h.HandlerStart), HandlerEnd = Target(h.HandlerEnd), FilterStart = Target(h.FilterStart), CatchType = h.CatchType == null ? null : (ITypeDefOrRef)resolve(h.CatchType) });
		SequencePointSnapshotCandidate.Restore(node.SequencePoints, body);
		var resolveImport = PdbImportSnapshotCandidate.Restore(node.Imports, resolve);
		if (node.HasPdbMethod) body.PdbMethod = new PdbMethod { Scope = PdbScopeSnapshotCandidate.Restore(node.Scope, body, resolve, resolveImport) };
		return body;
	}
}
