using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

// Test-only codec for portable CDI kinds without graph edges.
internal static class PortablePdbCdiCandidate {
	public sealed class PairNode { public int[] Key { get; set; } = Array.Empty<int>(); public int[] Value { get; set; } = Array.Empty<int>(); }
	public sealed class ReferenceNode {
		public int[] Name { get; set; } = Array.Empty<int>();
		public int[] Aliases { get; set; } = Array.Empty<int>();
		public int Flags { get; set; }
		public uint Timestamp { get; set; }
		public uint Size { get; set; }
		public Guid Mvid { get; set; }
	}
	public sealed class StateNode { public int Offset { get; set; } public int State { get; set; } }
	public sealed class InstructionNode { public string Method { get; set; } = ""; public int Index { get; set; } }
	public sealed class AsyncStepNode { public InstructionNode Yield { get; set; } = new(); public InstructionNode Breakpoint { get; set; } = new(); }
	public sealed class Node {
		public string Kind { get; set; } = "";
		public Guid Guid { get; set; }
		public byte[]? Data { get; set; }
		public int[]? Text { get; set; }
		public int[][]? Texts { get; set; }
		public bool[]? Flags { get; set; }
		public PairNode[]? Pairs { get; set; }
		public ReferenceNode[]? References { get; set; }
		public StateNode[]? States { get; set; }
		public string? Reference { get; set; }
		public InstructionNode? Instruction { get; set; }
		public AsyncStepNode[]? Steps { get; set; }
		public int[][]? Ranges { get; set; }
		public SequencePointSnapshotCandidate.DocumentNode[]? Documents { get; set; }
	}
	static byte[]? Bytes(byte[]? value) => value == null ? null : (byte[])value.Clone();
	static int[] Text(string value) => value.Select(c => (int)c).ToArray();
	public static Node Capture(PdbCustomDebugInfo value) => value switch {
		PdbUnknownCustomDebugInfo x => new() { Kind = "unknown", Guid = x.Guid, Data = Bytes(x.Data) },
		PdbEditAndContinueLocalSlotMapCustomDebugInfo x => new() { Kind = "enc_local", Data = Bytes(x.Data) },
		PdbEditAndContinueLambdaMapCustomDebugInfo x => new() { Kind = "enc_lambda", Data = Bytes(x.Data) },
		PortablePdbTupleElementNamesCustomDebugInfo x => new() { Kind = "tuple", Texts = x.Names.Select(Text).ToArray() },
		PdbDefaultNamespaceCustomDebugInfo x => new() { Kind = "default_namespace", Text = Text(x.Namespace) },
		PdbDynamicLocalVariablesCustomDebugInfo x => new() { Kind = "dynamic", Flags = (bool[])x.Flags.Clone() },
		PdbEmbeddedSourceCustomDebugInfo x => new() { Kind = "embedded_source", Data = Bytes(x.SourceCodeBlob) },
		PdbSourceLinkCustomDebugInfo x => new() { Kind = "source_link", Data = Bytes(x.FileBlob) },
		PdbCompilationMetadataReferencesCustomDebugInfo x => new() { Kind = "metadata_refs", References = x.References.Select(r => new ReferenceNode { Name = Text(r.Name), Aliases = Text(r.Aliases), Flags = (int)r.Flags, Timestamp = r.Timestamp, Size = r.SizeOfImage, Mvid = r.Mvid }).ToArray() },
		PdbCompilationOptionsCustomDebugInfo x => new() { Kind = "options", Pairs = x.Options.Select(p => new PairNode { Key = Text(p.Key), Value = Text(p.Value) }).ToArray() },
		PdbEditAndContinueStateMachineStateMapDebugInfo x => new() { Kind = "enc_states", States = x.StateMachineStates.Select(s => new StateNode { Offset = s.SyntaxOffset, State = (int)s.State }).ToArray() },
		PrimaryConstructorInformationBlobDebugInfo x => new() { Kind = "primary_ctor", Data = Bytes(x.Blob) },
		_ => throw new InvalidDataException("CDI graph kind outside scalar candidate: " + value.GetType().Name),
	};
	public static Node Capture(PdbCustomDebugInfo value, MethodDef owner, Func<IMDTokenProvider, string> bind) {
		int HoistedInstructionIndex(dnlib.DotNet.Emit.Instruction? instruction) {
			if (instruction == null) return -1;
			var index = owner.Body.Instructions.IndexOf(instruction);
			return index >= 0 ? index : throw new InvalidDataException("Detached hoisted CDI instruction");
		}
		InstructionNode Instruction(dnlib.DotNet.Emit.Instruction? instruction) {
			if (instruction == null) return new InstructionNode { Index = -1 };
			var method = owner.Module.GetTypes().SelectMany(t => t.Methods).FirstOrDefault(m => m.HasBody && m.Body.Instructions.Any(i => ReferenceEquals(i, instruction)))
				?? throw new InvalidDataException("Detached CDI instruction");
			return new InstructionNode { Method = bind(method), Index = method.Body.Instructions.IndexOf(instruction) };
		}
		return value switch {
			PdbStateMachineHoistedLocalScopesCustomDebugInfo x => new Node { Kind = "hoisted", Ranges = x.Scopes.Select(s => new[] { HoistedInstructionIndex(s.Start), HoistedInstructionIndex(s.End) }).ToArray() },
			PdbAsyncMethodCustomDebugInfo x => new Node { Kind = "async", Reference = bind(x.KickoffMethod), Instruction = Instruction(x.CatchHandlerInstruction),
				Steps = x.StepInfos.Select(s => new AsyncStepNode { Yield = Instruction(s.YieldInstruction), Breakpoint = Instruction(s.BreakpointInstruction) }).ToArray() },
			PdbIteratorMethodCustomDebugInfo x => new Node { Kind = "iterator", Reference = bind(x.KickoffMethod) },
			PdbTypeDefinitionDocumentsDebugInfo x => new Node { Kind = "type_documents", Documents = x.Documents.Select(SequencePointSnapshotCandidate.CaptureDocument).ToArray() },
			_ => Capture(value),
		};
	}
	public static PdbCustomDebugInfo Restore(Node node) {
		string Text(int[] value) => new(value.Select(c => checked((char)c)).ToArray());
		PdbCustomDebugInfo result;
		switch (node.Kind) {
		case "unknown": result = new PdbUnknownCustomDebugInfo(node.Guid, Bytes(node.Data)!); break;
		case "enc_local": result = new PdbEditAndContinueLocalSlotMapCustomDebugInfo(Bytes(node.Data)!); break;
		case "enc_lambda": result = new PdbEditAndContinueLambdaMapCustomDebugInfo(Bytes(node.Data)!); break;
		case "tuple": { var x = new PortablePdbTupleElementNamesCustomDebugInfo(); foreach (var s in node.Texts!) x.Names.Add(Text(s)); result = x; break; }
		case "default_namespace": result = new PdbDefaultNamespaceCustomDebugInfo(Text(node.Text!)); break;
		case "dynamic": result = new PdbDynamicLocalVariablesCustomDebugInfo((bool[])node.Flags!.Clone()); break;
		case "embedded_source": result = new PdbEmbeddedSourceCustomDebugInfo(Bytes(node.Data)!); break;
		case "source_link": result = new PdbSourceLinkCustomDebugInfo(Bytes(node.Data)!); break;
		case "metadata_refs": { var x = new PdbCompilationMetadataReferencesCustomDebugInfo(); foreach (var r in node.References!) x.References.Add(new PdbCompilationMetadataReference(Text(r.Name), Text(r.Aliases), (PdbCompilationMetadataReferenceFlags)r.Flags, r.Timestamp, r.Size, r.Mvid)); result = x; break; }
		case "options": { var x = new PdbCompilationOptionsCustomDebugInfo(); foreach (var p in node.Pairs!) x.Options.Add(new KeyValuePair<string, string>(Text(p.Key), Text(p.Value))); result = x; break; }
		case "enc_states": { var x = new PdbEditAndContinueStateMachineStateMapDebugInfo(); foreach (var s in node.States!) x.StateMachineStates.Add(new StateMachineStateInfo(s.Offset, (StateMachineState)s.State)); result = x; break; }
		case "primary_ctor": result = new PrimaryConstructorInformationBlobDebugInfo(Bytes(node.Data)!); break;
		default: throw new InvalidDataException("Unknown scalar CDI kind");
		}
		if (result.Guid != node.Guid && node.Kind == "unknown") throw new InvalidDataException("Unknown CDI guid mismatch");
		return result;
	}
	public static PdbCustomDebugInfo Restore(Node node, MethodDef owner, Func<string, IMDTokenProvider> resolve) {
		dnlib.DotNet.Emit.Instruction? Instruction(InstructionNode value) {
			if (value.Index == -1) return null;
			var method = resolve(value.Method) as MethodDef ?? throw new InvalidDataException("Invalid CDI method");
			return method.HasBody && value.Index >= 0 && value.Index < method.Body.Instructions.Count ? method.Body.Instructions[value.Index] : throw new InvalidDataException("Invalid CDI instruction");
		}
		switch (node.Kind) {
		case "hoisted": {
			var value = new PdbStateMachineHoistedLocalScopesCustomDebugInfo();
			foreach (var range in node.Ranges!) {
				if (range.Length != 2) throw new InvalidDataException("Invalid hoisted range");
				dnlib.DotNet.Emit.Instruction? At(int i) => i == -1 ? null : i >= 0 && i < owner.Body.Instructions.Count ? owner.Body.Instructions[i] : throw new InvalidDataException("Invalid hoisted instruction");
				value.Scopes.Add(new StateMachineHoistedLocalScope(At(range[0]), At(range[1])));
			}
			return value;
		}
		case "async": {
			var value = new PdbAsyncMethodCustomDebugInfo { KickoffMethod = resolve(node.Reference!) as MethodDef ?? throw new InvalidDataException("Invalid async kickoff"), CatchHandlerInstruction = Instruction(node.Instruction!) };
			foreach (var step in node.Steps!) {
				var yield = Instruction(step.Yield) ?? throw new InvalidDataException("Null async yield");
				var breakpoint = Instruction(step.Breakpoint) ?? throw new InvalidDataException("Null async breakpoint");
				value.StepInfos.Add(new PdbAsyncStepInfo(yield, resolve(step.Breakpoint.Method) as MethodDef ?? throw new InvalidDataException("Invalid async breakpoint method"), breakpoint));
			}
			return value;
		}
		case "iterator": return new PdbIteratorMethodCustomDebugInfo(resolve(node.Reference!) as MethodDef ?? throw new InvalidDataException("Invalid iterator kickoff"));
		case "type_documents": { var value = new PdbTypeDefinitionDocumentsDebugInfo(); foreach (var document in node.Documents!) value.Documents.Add(SequencePointSnapshotCandidate.CreateDocument(document)); return value; }
		default: return Restore(node);
		}
	}
}
