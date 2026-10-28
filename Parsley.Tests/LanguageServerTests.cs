using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Parsley.LanguageServer;

namespace Parsley.Tests
{
	/// <summary>
	/// The language server, talked to in memory as an editor would: where EvaluateXxx in an action goes, and what
	/// a rename does to it, which has to agree with the names the synthesizer gives the methods
	/// </summary>
	public static class LanguageServerTests
	{
		// foo_bar and fooBar meet as FooBar, so fooBar's methods are named for FooBar2
		const string Meet =
			"S<start, type=\"int\">= foo_bar | fooBar | Expr => { return EvaluateFooBar(node.Children[0]) + EvaluateFooBar2(node.Children[0]) + EvaluateExpr(node.Children[0]); };\n" +
			"foo_bar<type=\"int\">= a => { return 1; };\n" +
			"fooBar<type=\"int\">= b => { return 2; };\n" +
			"Expr<type=\"int\">= c => { return 3; };\n" +
			"a=\"a\"; b=\"b\"; c=\"c\";\n";

		static string _Path(string name) => Path.Combine(Path.GetTempPath(), "parsley-lsp", name);
		static string _Uri(string path) => new Uri(path).AbsoluteUri;

		static void _Frame(Stream s, JsonObject message)
		{
			message["jsonrpc"] = "2.0";
			var body = Encoding.UTF8.GetBytes(message.ToJsonString());
			var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
			s.Write(header);
			s.Write(body);
		}

		// opens the grammar, sends the requests, numbered from 1, and returns the response to each, by number
		static Dictionary<int, JsonObject> _Talk(string path, string text, params (string Method, JsonObject Params)[] requests)
		{
			var input = new MemoryStream();
			_Frame(input, new JsonObject
			{
				["id"] = 0, ["method"] = "initialize",
				["params"] = new JsonObject { ["capabilities"] = new JsonObject { ["textDocument"] = new JsonObject { ["rename"] = new JsonObject { ["prepareSupport"] = true } } } },
			});
			_Frame(input, new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() });
			_Frame(input, new JsonObject
			{
				["method"] = "textDocument/didOpen",
				["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = _Uri(path), ["languageId"] = "xbnf", ["version"] = 1, ["text"] = text } },
			});
			for (var i = 0; i < requests.Length; ++i)
				_Frame(input, new JsonObject { ["id"] = i + 1, ["method"] = requests[i].Method, ["params"] = requests[i].Params });
			_Frame(input, new JsonObject { ["id"] = requests.Length + 1, ["method"] = "shutdown" });
			_Frame(input, new JsonObject { ["method"] = "exit" });
			input.Position = 0;
			var output = new MemoryStream();
			new XbnfLanguageServer(new JsonRpc(input, output)).RunAsync().GetAwaiter().GetResult();
			// the server may still be checking the grammar in the background, and write diagnostics after this
			var bytes = output.ToArray();
			var result = new Dictionary<int, JsonObject>();
			var replies = new JsonRpc(new MemoryStream(bytes), Stream.Null);
			while (replies.ReadAsync(default).GetAwaiter().GetResult() is JsonObject m)
				if (m["id"] is JsonValue id && id.TryGetValue<int>(out var n) && n > 0 && n <= requests.Length) result[n] = m;
			return result;
		}

		static (int Line, int Character) _Position(string text, int offset)
		{
			var line = 0;
			var start = 0;
			for (var i = 0; i < offset; ++i)
				if (text[i] == '\n') { ++line; start = i + 1; }
			return (line, offset - start);
		}
		static int _Offset(string text, JsonNode position)
		{
			var line = (int)position["line"]!;
			var offset = 0;
			for (var l = 0; l < line; ++l) offset = text.IndexOf('\n', offset) + 1;
			return offset + (int)position["character"]!;
		}
		// the parameters for a request at a place in the text: just inside the nth occurrence of what's given
		static JsonObject _At(string path, string text, string what, int nth = 0, int into = 0)
		{
			var offset = -1;
			for (var i = 0; i <= nth; ++i) offset = text.IndexOf(what, offset + 1, StringComparison.Ordinal);
			if (offset < 0) throw new ArgumentException($"\"{what}\" isn't in the text");
			var (line, character) = _Position(text, offset + into);
			return new JsonObject
			{
				["textDocument"] = new JsonObject { ["uri"] = _Uri(path) },
				["position"] = new JsonObject { ["line"] = line, ["character"] = character },
			};
		}
		static JsonObject _With(JsonObject p, string key, JsonNode value) { p[key] = value; return p; }

		// the text a range covers
		static string _Covered(string text, JsonNode range) => text.Substring(_Offset(text, range["start"]!), _Offset(text, range["end"]!) - _Offset(text, range["start"]!));

		// a rename's edits applied to the text
		static string _Apply(string text, JsonObject? response, string path)
		{
			var edits = response?["result"]?["changes"]?[_Uri(path)] as JsonArray;
			if (null == edits) return text;
			var spans = edits.Select(e => (Start: _Offset(text, e!["range"]!["start"]!), End: _Offset(text, e!["range"]!["end"]!), Text: (string)e!["newText"]!))
				.OrderByDescending(e => e.Start).ToList();
			var sb = new StringBuilder(text);
			foreach (var (start, end, newText) in spans) sb.Remove(start, end - start).Insert(start, newText);
			return sb.ToString();
		}

		// the grammar synthesizes and its evaluator compiles, so every EvaluateXxx in it names a method that's there
		static void _Compiles(string text, string what)
		{
			var g = Grammar.Parse(text, "lspcheck.xbnf");
			var a = GrammarAnalysis.Analyze(g);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			try
			{
				var p = CompiledParser.Compile(src, g.ClassName ?? (g.StartSymbol + "Parser"));
				T.Check(true, what);
			}
			catch (InvalidOperationException ex) { T.Check(false, what + ": " + ex.Message); }
		}

		public static void EvaluateCallsGoToTheRuleTheirMethodIsFor()
		{
			var path = _Path("meet.xbnf");
			var r = _Talk(path, Meet,
				("textDocument/definition", _At(path, Meet, "EvaluateFooBar(", into: 9)),
				("textDocument/definition", _At(path, Meet, "EvaluateFooBar2(", into: 9)),
				("textDocument/definition", _At(path, Meet, "EvaluateExpr(", into: 9)),
				("textDocument/hover", _At(path, Meet, "EvaluateFooBar2(", into: 9)));
			string target(int n) => r.TryGetValue(n, out var m) && m["result"] is JsonArray a && 1 == a.Count ? _Covered(Meet, a[0]!["range"]!) : "(none)";
			T.Equal("foo_bar", target(1), "EvaluateFooBar is foo_bar's");
			T.Equal("fooBar", target(2), "EvaluateFooBar2 is fooBar's, as the synthesizer names it");
			T.Equal("Expr", target(3), "EvaluateExpr is Expr's");
			var hover = r.TryGetValue(4, out var h) ? (string?)h["result"]?["contents"]?["value"] ?? "" : "";
			T.Check(hover.Contains("`fooBar`"), "the hover on EvaluateFooBar2 is about fooBar: " + hover);
			_Compiles(Meet, "the grammar's actions compile as written");
		}

		public static void ReferencesFindTheRightEvaluateCalls()
		{
			var path = _Path("refs.xbnf");
			var r = _Talk(path, Meet,
				("textDocument/references", _With(_At(path, Meet, "fooBar<"), "context", new JsonObject { ["includeDeclaration"] = true })),
				("textDocument/references", _With(_At(path, Meet, "foo_bar<"), "context", new JsonObject { ["includeDeclaration"] = true })));
			List<string> covered(int n) => (r.TryGetValue(n, out var m) ? m["result"] as JsonArray : null)?.Select(l => _Covered(Meet, l!["range"]!)).ToList() ?? new List<string>();
			var fooBar = covered(1);
			T.Check(fooBar.Contains("EvaluateFooBar2") && !fooBar.Contains("EvaluateFooBar"), "fooBar is evaluated by EvaluateFooBar2 alone: " + string.Join(", ", fooBar));
			var foo_bar = covered(2);
			T.Check(foo_bar.Contains("EvaluateFooBar") && !foo_bar.Contains("EvaluateFooBar2"), "foo_bar is evaluated by EvaluateFooBar alone: " + string.Join(", ", foo_bar));
		}

		public static void RenamingARuleRenamesItsEvaluateCallsAsTheSynthesizerWould()
		{
			var path = _Path("rename.xbnf");
			var r = _Talk(path, Meet,
				// a name that isn't an identifier as written is made one: EvaluateMyExpr, not Evaluatemy_expr
				("textDocument/rename", _With(_At(path, Meet, "Expr<"), "newName", "my_expr")),
				// from the call: fooBar to baz_qux, which is EvaluateBazQux
				("textDocument/rename", _With(_At(path, Meet, "EvaluateFooBar2(", into: 9), "newName", "baz_qux")),
				// foo_bar out of the way: fooBar's methods get their own name back
				("textDocument/rename", _With(_At(path, Meet, "foo_bar<"), "newName", "first")),
				// fooBar to FooBar: foo_bar keeps FooBar and the new FooBar is still FooBar2
				("textDocument/rename", _With(_At(path, Meet, "fooBar<"), "newName", "FooBar")),
				("textDocument/prepareRename", _At(path, Meet, "EvaluateFooBar2(", into: 9)));
			string renamed(int n) => _Apply(Meet, r.TryGetValue(n, out var m) ? m : null, path);

			var one = renamed(1);
			T.Check(one.Contains("my_expr<") && one.Contains("| my_expr =>") && one.Contains("EvaluateMyExpr(") && !one.Contains("Evaluatemy_expr"), "Expr to my_expr: " + one);
			_Compiles(one, "the actions compile after Expr is renamed my_expr");

			var two = renamed(2);
			T.Check(two.Contains("baz_qux<") && two.Contains("EvaluateBazQux(") && two.Contains("EvaluateFooBar(") && !two.Contains("EvaluateFooBar2"), "fooBar to baz_qux: " + two);
			_Compiles(two, "the actions compile after fooBar is renamed baz_qux");

			var three = renamed(3);
			T.Check(three.Contains("first<") && three.Contains("EvaluateFirst(") && three.Contains("EvaluateFooBar(") && !three.Contains("EvaluateFooBar2"), "foo_bar to first, and fooBar's EvaluateFooBar2 is EvaluateFooBar: " + three);
			_Compiles(three, "the actions compile after foo_bar is renamed first");

			var four = renamed(4);
			T.Check(four.Contains("FooBar<") && four.Contains("EvaluateFooBar(") && four.Contains("EvaluateFooBar2("), "fooBar to FooBar changes no call: " + four);
			_Compiles(four, "the actions compile after fooBar is renamed FooBar");

			var prepare = r.TryGetValue(5, out var p) ? p["result"] : null;
			T.Equal("fooBar", (string?)prepare?["placeholder"], "a rename at EvaluateFooBar2 offers the rule's name");
			T.Equal("FooBar2", null == prepare ? "" : _Covered(Meet, prepare["range"]!), "and covers the name in the call");
		}

		public static void EvaluateCallsFindImportedRules()
		{
			// the imported rules come where the @import is, after foo_bar, so the imported fooBar is FooBar2
			const string parts = "fooBar<type=\"int\">= b => { return 2; };\nb=\"b\";\n";
			const string text =
				"S<start, type=\"int\">= foo_bar | fooBar => { return EvaluateFooBar(node.Children[0]) + EvaluateFooBar2(node.Children[0]); };\n" +
				"foo_bar<type=\"int\">= a => { return 1; };\n" +
				"@import \"parts.xbnf\";\n" +
				"a=\"a\";\n";
			var path = _Path("importer.xbnf");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var partsPath = _Path("parts.xbnf");
			File.WriteAllText(partsPath, parts);
			var r = _Talk(path, text,
				("textDocument/definition", _At(path, text, "EvaluateFooBar2(", into: 9)),
				("textDocument/definition", _At(path, text, "EvaluateFooBar(", into: 9)));
			JsonObject? location(int n) => r.TryGetValue(n, out var m) && m["result"] is JsonArray a && 1 == a.Count ? a[0] as JsonObject : null;
			var two = location(1);
			T.Equal(_Uri(partsPath), (string?)two?["uri"], "EvaluateFooBar2 goes to the imported file");
			T.Equal("fooBar", null == two ? "" : _Covered(parts, two["range"]!), "and to fooBar there");
			var one = location(2);
			T.Equal(_Uri(path), (string?)one?["uri"], "EvaluateFooBar stays in the importer");
			T.Equal("foo_bar", null == one ? "" : _Covered(text, one["range"]!), "at foo_bar");
		}

		public static void MethodNamesRequestGivesTheSynthesizersNames()
		{
			// what the Visual Studio extension asks for, to find an action's EvaluateXxx in the generated code
			const string text =
				"S<start, type=\"int\">= foo_bar | fooBar => { return 0; };\n" +
				"foo_bar<shared, type=\"int\">= a => { return 1; };\n" +
				"fooBar<type=\"int\">= b => { return 2; };\n" +
				"a=\"a\"; b=\"b\";\n";
			var path = _Path("methods.xbnf");
			var r = _Talk(path, text, ("parsley/methodNames", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = _Uri(path) } }));
			var rules = r.TryGetValue(1, out var m) ? m["result"]?["rules"] as JsonObject : null;
			T.Check(null != rules, "the server answers: " + m?.ToJsonString());
			if (null == rules) return;
			T.Equal("EvaluateFooBar", (string?)rules["foo_bar"]?["evaluate"], "foo_bar's EvaluateXxx");
			T.Equal("EvaluateFooBar2", (string?)rules["fooBar"]?["evaluate"], "fooBar's is numbered");
			T.Equal("ParseFooBar2", (string?)rules["fooBar"]?["parse"], "and its ParseXxx");
			T.Equal("TryParseFooBar", (string?)rules["foo_bar"]?["tryParse"], "a shared rule's TryParseXxx");
			T.Check(null == rules["fooBar"]?["tryParse"], "and none for a rule that isn't shared");
			T.Check(null == rules["a"], "a terminal has no methods");
			// and the synthesized code has every method the server names
			var g = Grammar.Parse(text, "methods.xbnf");
			var src = CSharpSynthesizer.Synthesize(g);
			foreach (var kv in rules)
				foreach (var method in ((JsonObject)kv.Value!).Select(x => (string)x.Value!))
					T.Check(src.Contains(" " + method + "("), $"{kv.Key}'s {method} is in the synthesized code");
		}

		public static void AnEvaluateCallNoMethodHasIsntARule()
		{
			// EvaluateFoo_bar and Evaluatefoo_bar aren't methods the synthesizer writes, whatever the rules are called
			const string text =
				"S<start, type=\"int\">= foo_bar => { return EvaluateFoo_bar(node.Children[0]) + EvaluateFooBar3(node.Children[0]); };\n" +
				"foo_bar<type=\"int\">= a => { return 1; };\n" +
				"a=\"a\";\n";
			var path = _Path("none.xbnf");
			var r = _Talk(path, text,
				("textDocument/definition", _At(path, text, "EvaluateFoo_bar(", into: 9)),
				("textDocument/definition", _At(path, text, "EvaluateFooBar3(", into: 9)));
			bool none(int n) => !r.TryGetValue(n, out var m) || null == m["result"] || (m["result"] is JsonArray a && 0 == a.Count);
			T.Check(none(1), "EvaluateFoo_bar goes nowhere");
			T.Check(none(2), "EvaluateFooBar3 goes nowhere");
		}
	}
}
