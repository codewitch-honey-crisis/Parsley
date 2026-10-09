using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Parsley.Tests
{
	/// <summary>
	/// Compiles a synthesized parser together with the runtime file, in memory and on its own
	/// (no reference to Parsley.dll), then runs it through reflection.
	/// </summary>
	sealed class CompiledParser
	{
		readonly Type _parserType;
		readonly Type _tokenType;
		public IReadOnlyList<Diagnostic> Diagnostics { get; }

		CompiledParser(Type parserType, Type tokenType, IReadOnlyList<Diagnostic> diagnostics)
		{
			_parserType = parserType;
			_tokenType = tokenType;
			Diagnostics = diagnostics;
		}

		static List<MetadataReference>? _refs;
		static List<MetadataReference> _References()
		{
			if (null != _refs) return _refs;
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			_refs = tpa.Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) == "netstandard.dll" || Path.GetFileName(p) == "mscorlib.dll")
				.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
			return _refs;
		}

		public static CompiledParser Compile(string source, string className)
		{
			var opts = new CSharpParseOptions(LanguageVersion.CSharp12);
			var trees = new[]
			{
				CSharpSyntaxTree.ParseText(source, opts, "Parser.cs"),
				CSharpSyntaxTree.ParseText(CSharpSynthesizer.RuntimeSource, opts, "ParsleyRuntime.cs")
			};
			var comp = CSharpCompilation.Create("Synth" + Guid.NewGuid().ToString("N"), trees, _References(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, warningLevel: 4));
			using var ms = new MemoryStream();
			var result = comp.Emit(ms);
			var diags = result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
			if (!result.Success)
				throw new InvalidOperationException("Synthesized code did not compile:\n" + string.Join("\n", diags));
			ms.Position = 0;
			var asm = new AssemblyLoadContext(null, true).LoadFromStream(ms);
			var parserType = asm.GetTypes().First(t => t.Name == className);
			var tokenType = asm.GetType("Parsley.Runtime.Token")!;
			return new CompiledParser(parserType, tokenType, diags);
		}

		/// <summary>
		/// Parses and returns the tree dump and the error messages
		/// </summary>
		public (string Tree, List<string> Errors) Parse(IEnumerable<Parsley.Runtime.Token> tokens)
		{
			var list = tokens.ToList();
			var arr = Array.CreateInstance(_tokenType, list.Count);
			for (var i = 0; i < list.Count; ++i)
			{
				var t = list[i];
				arr.SetValue(Activator.CreateInstance(_tokenType, t.SymbolId, t.Value, t.Line, t.Column, t.Position), i);
			}
			var parser = Activator.CreateInstance(_parserType, arr)!;
			var root = _parserType.GetMethod("Parse")!.Invoke(parser, null)!;
			var errors = ((IEnumerable)_parserType.GetProperty("Errors")!.GetValue(parser)!).Cast<object>().Select(e => e.ToString()!).ToList();
			return (root.ToString()!, errors);
		}
	}

	static class Interp
	{
		public static (string Tree, List<string> Errors) Parse(GrammarAnalysis a, IEnumerable<Parsley.Runtime.Token> tokens)
		{
			var p = new GrammarInterpreter(a, tokens);
			var root = p.Parse();
			return (root.ToString(), p.Errors.Select(e => e.ToString()).ToList());
		}
	}
}
