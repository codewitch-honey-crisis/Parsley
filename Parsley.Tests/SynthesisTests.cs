using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Parsley.Tests
{
	public static class SynthesisTests
	{
		public static string OutDir
		{
			get
			{
				var d = Path.Combine(T.Dir, "out");
				Directory.CreateDirectory(d);
				return d;
			}
		}
		static GrammarAnalysis _json = null!;
		static CompiledParser _jsonParser = null!;
		static RegexLexer _jsonLexer = null!;
		static void _LoadJson()
		{
			if (null != _jsonParser) return;
			var g = Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf"));
			_json = GrammarAnalysis.Analyze(g);
			var src = CSharpSynthesizer.Synthesize(_json);
			File.WriteAllText(Path.Combine(OutDir, "JsonParser.cs"), src);
			File.WriteAllText(Path.Combine(OutDir, "ParsleyRuntime.cs"), CSharpSynthesizer.RuntimeSource);
			_jsonParser = CompiledParser.Compile(src, "JsonParser");
			_jsonLexer = new RegexLexer(_json);
		}

		public static void JsonCompilesCleanly()
		{
			_LoadJson();
			T.Equal("", string.Join("\n", _jsonParser.Diagnostics), "no warnings");
		}

		public static void JsonParsesAndBuildsTheTree()
		{
			_LoadJson();
			var (tree, errors) = _jsonParser.Parse(_jsonLexer.Tokenize("{ \"a\": [1, true, null], \"b\": {} }"));
			T.Equal(0, errors.Count, "no errors: " + string.Join("; ", errors));
			var expected = string.Join(Environment.NewLine,
				"Json",
				"  Object",
				"    Field",
				"      string \"\"a\"\"",
				"      Array",
				"        number \"1\"",
				"        Boolean",
				"          true \"true\"",
				"        null \"null\"",
				"    Field",
				"      string \"\"b\"\"",
				"      Object",
				"");
			T.Equal(expected, tree, "tree: Value collapsed, punctuation collapsed");
		}

		static readonly string[] _samples =
		{
			"{}", "[]", "[1]", "[1,2,3]", "{\"a\":1}", "{\"a\":1,\"b\":[true,false,null]}", "[[[]]]", "[{},{}]",
			"{\"k\":\"v\\\"x\\u00e9\"}", "[-0.5e10, 0, 12.25]",
			// broken
			"{", "}", "[1,]", "[,1]", "{\"a\" 1}", "{\"a\":}", "{\"a\":1 \"b\":2}", "[1 2]", "{\"a\":1}}", "[tru]", "[01]",
			"{\"a\":[1,{\"b\":}],\"c\":3}", "", "1", "[\"unterminated]", "{:}", "[[[", "]]]", "{\"a\":1,}", "[1,,2]"
		};

		public static void SynthesizedMatchesInterpreter()
		{
			_LoadJson();
			var mismatches = new List<string>();
			foreach (var text in _samples.Concat(_RandomDocuments(400, 7)))
			{
				var tokens = _jsonLexer.Tokenize(text).ToList();
				var s = _jsonParser.Parse(tokens);
				var i = Interp.Parse(_json, tokens);
				if (s.Tree != i.Tree || !s.Errors.SequenceEqual(i.Errors))
					mismatches.Add(text);
			}
			T.Equal("", string.Join(" | ", mismatches.Take(5)), "synthesized and interpreted parsers agree on trees and errors");
		}

		public static void AcceptsExactlyWhatSystemTextJsonAccepts()
		{
			_LoadJson();
			var disagreements = new List<string>();
			var count = 0;
			foreach (var text in _samples.Concat(_RandomDocuments(1500, 11)))
			{
				// json.xbnf only allows an object or array at the top
				var trimmed = text.TrimStart();
				if (0 == trimmed.Length || ('{' != trimmed[0] && '[' != trimmed[0])) continue;
				bool stj;
				try { using var _ = JsonDocument.Parse(text); stj = true; }
				catch (JsonException) { stj = false; }
				var ours = 0 == _jsonParser.Parse(_jsonLexer.Tokenize(text)).Errors.Count;
				++count;
				if (stj != ours) disagreements.Add($"{(stj ? "valid" : "invalid")}: {text}");
			}
			T.Check(1000 < count, "enough documents compared");
			T.Equal("", string.Join("\n", disagreements.Take(5)), "acceptance matches System.Text.Json");
		}

		public static void RecoversAndKeepsGoing()
		{
			_LoadJson();
			var (tree, errors) = _jsonParser.Parse(_jsonLexer.Tokenize("{\"a\":1 \"b\":2, \"c\":3}"));
			T.Equal(1, errors.Count, "one error for a missing comma");
			T.Equal("Expected \"}\" but found string \"b\" at line 1, column 8", errors[0], "message names what was expected and found");
			T.Check(tree.Contains("#ERROR"), "skipped tokens are kept in an error node");

			(tree, errors) = _jsonParser.Parse(_jsonLexer.Tokenize("[1,,2,3]"));
			T.Equal(1, errors.Count, "one error for a missing value");
			T.Check(errors[0].StartsWith("Expected number, string,") && errors[0].Contains("but found \",\" at line 1, column 4"), "value error: " + errors[0]);
			T.Check(tree.Contains("number \"3\""), "parsing continued after the error");

			(tree, errors) = _jsonParser.Parse(_jsonLexer.Tokenize("{\"a\" }"));
			T.Equal(1, errors.Count, "a missing colon and value is reported once");
			T.Check(!tree.Contains("#ERROR"), "the closing brace isn't swallowed");

			(_, errors) = _jsonParser.Parse(_jsonLexer.Tokenize("[[[["));
			T.Check(1 <= errors.Count && errors[0].Contains("end of input"), "end of input reported");
		}

		public static void LexerIdsAreOffsetByTheParser()
		{
			_LoadJson();
			var s = _json.Symbols;
			int lex(string name) => s.GetId(name) - s.NonTerminalCount;
			// [ true ] as a lexer reports it: terminals counted from 0
			var tokens = new[]
			{
				new Parsley.Runtime.Token(lex("lbracket"), "[", 1, 1, 0),
				new Parsley.Runtime.Token(lex("true"), "true", 1, 2, 1),
				new Parsley.Runtime.Token(lex("rbracket"), "]", 1, 6, 5),
			};
			var (tree, errors) = _jsonParser.Parse(tokens);
			T.Equal(0, errors.Count, "lexer ids accepted: " + string.Join("; ", errors));
			T.Check(tree.Contains("true \"true\""), "terminal named correctly in the tree");
			// a negative id, or one past the last terminal, is reported as unrecognized input
			foreach (var bad in new[] { -1, s.EndOfInput - s.NonTerminalCount, 999 })
			{
				(_, errors) = _jsonParser.Parse(new[] { tokens[0], new Parsley.Runtime.Token(bad, "?", 1, 2, 1), tokens[2] });
				T.Check(1 == errors.Count && errors[0].Contains("unrecognized input \"?\""), $"id {bad} reported as unrecognized: " + string.Join("; ", errors));
			}
		}

		// random JSON plus random single-character damage
		static IEnumerable<string> _RandomDocuments(int count, int seed)
		{
			var r = new Random(seed);
			const string damage = "{}[],:\"0123456789-+.eEtrufalsn \\ab";
			for (var i = 0; i < count; ++i)
			{
				var sb = new StringBuilder();
				_Value(sb, r, 0, true);
				var s = sb.ToString();
				if (0 < i % 2)
				{
					var edits = 1 + r.Next(2);
					for (var e = 0; e < edits && 0 < s.Length; ++e)
					{
						var pos = r.Next(s.Length);
						switch (r.Next(3))
						{
							case 0: s = s.Remove(pos, 1); break;
							case 1: s = s.Insert(pos, damage[r.Next(damage.Length)].ToString()); break;
							default: s = s.Remove(pos, 1).Insert(pos, damage[r.Next(damage.Length)].ToString()); break;
						}
					}
				}
				yield return s;
			}
		}
		static void _Value(StringBuilder sb, Random r, int depth, bool container)
		{
			var kind = container || depth < 3 && 0 == r.Next(3) ? r.Next(2) : 2 + r.Next(5);
			switch (kind)
			{
				case 0:
					sb.Append('{');
					var n = r.Next(4);
					for (var i = 0; i < n; ++i)
					{
						if (0 < i) sb.Append(',');
						if (0 == r.Next(3)) sb.Append(' ');
						sb.Append('"').Append((char)('a' + r.Next(26))).Append("\":");
						_Value(sb, r, depth + 1, false);
					}
					sb.Append('}');
					break;
				case 1:
					sb.Append('[');
					n = r.Next(4);
					for (var i = 0; i < n; ++i)
					{
						if (0 < i) sb.Append(", ");
						_Value(sb, r, depth + 1, false);
					}
					sb.Append(']');
					break;
				case 2: sb.Append(r.Next(-50, 1000)); if (0 == r.Next(4)) sb.Append(".5"); break;
				case 3: sb.Append("\"s").Append(r.Next(10)).Append('"'); break;
				case 4: sb.Append("true"); break;
				case 5: sb.Append("false"); break;
				default: sb.Append("null"); break;
			}
		}
	}
}
