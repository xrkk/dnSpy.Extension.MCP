using System;
using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace T024Sentinel {
	static class Marker {
		internal const string Root = @"__MARKER_ROOT__";
		internal static void Write(string name) {
			Directory.CreateDirectory(Root);
			File.WriteAllText(Path.Combine(Root, name),
				"pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + ";utc=" + DateTime.UtcNow.ToString("O"));
		}
	}

	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class HarmlessAnalyzer : DiagnosticAnalyzer {
		public HarmlessAnalyzer() => Marker.Write("analyzer.marker");
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
		public override void Initialize(AnalysisContext context) {
			Marker.Write("analyzer.marker");
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.EnableConcurrentExecution();
		}
	}

	[Generator]
	public sealed class HarmlessGenerator : ISourceGenerator {
		public HarmlessGenerator() => Marker.Write("generator.marker");
		public void Initialize(GeneratorInitializationContext context) => Marker.Write("generator.marker");
		public void Execute(GeneratorExecutionContext context) => Marker.Write("generator.marker");
	}
}
