using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Parsley.Tests
{
	/// <summary>
	/// Islands (@external, shared, entry=), terminals from other lexers (lexer=), and the LexerController that
	/// lexes them
	/// </summary>
	public static class IslandTests
	{
		// the island grammar: a scrap of JSON, with two shared productions
		const string Inner = @"
			@class ""InnerParser"";
			Doc<start>= Obj | Arr;
			Obj<shared>= ""{"" [ Pair { "","" Pair } ] ""}"";
			Pair= str "":"" Val;
			Val<collapsed>= str | num | Obj | Arr;
			Arr<shared>= ""["" [ Val { "","" Val } ] ""]"";
			str= '""[^""\n]*""';
			num= '[0-9]+';
			ws<hidden>= '[ \t\r\n]+';";

		// statements, some of which hold an island. Data nests and ends at the } that balances its first {.
		// Items ends where the island's lexer can't go on, such as at the ;
		const string Outer = @"
			@class ""OuterParser"";
			@external J ""inner.xbnf"";
			@lexer ""OuterLexer"";
			@lexer J ""InnerLexer"";
			Script<start>= { Stmt };
			Stmt<collapsed>= Set | List | Echo;
			Set= ""set"" name ""="" Data "";"";
			List= ""list"" Items "";"";
			Echo= ""echo"" text "";"";
			Data<entry=""J.Obj"", after=""eq"", nest=""J.lbrace"", pop=""J.rbrace"">;
			Items<entry=""J.Arr"", after=""listKeyword"">;
			setKeyword= ""set"";
			listKeyword= ""list"";
			echoKeyword= ""echo"";
			eq= ""="";
			name= '[a-z]+';
			text= '""[^""\n]*""';
			ws<hidden>= '[ \t\r\n]+';";

		// a quoted island: one terminal both opens and ends it (nest and pop are the same), so the island is the
		// whole string, quotes included, and its own grammar lexes what's between them
		const string QuotedInner = @"
			@class ""QuotedParser"";
			Phrase<shared>= quote { word } quote;
			quote= '""';
			word= '[a-z]+';
			ws<hidden>= '[ \t]+';";
		const string QuotedOuter = @"
			@class ""OuterParser"";
			@external Q ""quoted.xbnf"";
			@lexer ""OuterLexer"";
			@lexer Q ""QuotedLexer"";
			Script<start>= { Say };
			Say= ""say"" Words text "";"";
			Words<entry=""Q.Phrase"", after=""sayKeyword"", nest=""Q.quote"", pop=""Q.quote"">;
			sayKeyword= ""say"";
			semi= "";"";
			text= '""[^""\n]*""';
			ws<hidden>= '[ \t\r\n]+';";

		// islands that share a trigger, told apart by startsWith: an object or an array after =, or neither, and
		// then the outer grammar's own text
		const string DispatchOuter = @"
			@class ""OuterParser"";
			@external J ""inner.xbnf"";
			@lexer ""OuterLexer"";
			@lexer J ""InnerLexer"";
			Script<start>= { Set };
			Set= ""set"" name ""="" ( Data | Items | text ) "";"";
			Data<entry=""J.Obj"", after=""eq"", startsWith=""J.lbrace"", nest=""J.lbrace"", pop=""J.rbrace"">;
			Items<entry=""J.Arr"", after=""eq"", startsWith=""J.lbracket"">;
			setKeyword= ""set"";
			eq= ""="";
			name= '[a-z]+';
			text= '""[^""\n]*""';
			ws<hidden>= '[ \t\r\n]+';";

		// a quoted island whose shared productions' names meet as identifiers: phrase_x's entry is TryParsePhraseX2
		const string MeetInner = @"
			@class ""QuotedParser"";
			phraseX<shared>= quote quote;
			phrase_x<shared>= quote { word } quote;
			quote= '""';
			word= '[a-z]+';
			ws<hidden>= '[ \t]+';";
		const string MeetOuter = @"
			@class ""OuterParser"";
			@external Q ""meet.xbnf"";
			@lexer ""OuterLexer"";
			@lexer Q ""QuotedLexer"";
			Script<start>= { Say };
			Say= ""say"" Words text "";"";
			Words<entry=""Q.phrase_x"", after=""sayKeyword"", nest=""Q.quote"", pop=""Q.quote"">;
			sayKeyword= ""say"";
			semi= "";"";
			text= '""[^""\n]*""';
			ws<hidden>= '[ \t\r\n]+';";

		static readonly Dictionary<string, string> s_files = new() { ["inner.xbnf"] = Inner, ["outer.xbnf"] = Outer, ["quoted.xbnf"] = QuotedInner, ["quoted-outer.xbnf"] = QuotedOuter, ["dispatch-outer.xbnf"] = DispatchOuter, ["meet.xbnf"] = MeetInner, ["meet-outer.xbnf"] = MeetOuter };
		static string _Read(string path) => s_files.TryGetValue(Path.GetFileName(path), out var t) ? t : throw new FileNotFoundException(path);
		static string _Path(string name) => Path.Combine(Path.GetTempPath(), "islands", name);

		static Grammar _Grammar(string name, string? text = null)
		{
			var msgs = XbnfParser.TryParse(text ?? s_files[name], _Path(name), _Read, out var g);
			GrammarException.ThrowIfErrors(msgs);
			return g!;
		}
		static IList<GrammarMessage> _Messages(string text, Dictionary<string, string>? files = null)
		{
			var all = new Dictionary<string, string>(s_files);
			if (null != files) foreach (var f in files) all[f.Key] = f.Value;
			var msgs = XbnfParser.TryParse(text, _Path("test.xbnf"), p => all.TryGetValue(Path.GetFileName(p), out var t) ? t : throw new FileNotFoundException(p), out var g);
			if (msgs.Any(m => ErrorLevel.Error == m.Level)) return msgs;
			return GrammarAnalysis.Analyze(g!).Messages.ToList();
		}
		static bool _Has(IEnumerable<GrammarMessage> msgs, ErrorLevel level, string text) => msgs.Any(m => m.Level == level && m.Message.Contains(text));

		// a lexer for the tests: regexes tried at each position, longest match first, reading the text a line at a
		// time so the controller's buffer is read as it goes. Reports text it can't match as -1, as any lexer must.
		const string Lexing = @"
			using System.Collections.Generic;
			using System.IO;
			using System.Linq;
			using System.Text;
			using System.Text.RegularExpressions;
			using Parsley.Runtime;
			public static class TestLexing
			{
				public static IEnumerable<Token> Tokenize(string[] patterns, bool[] hidden, TextReader reader, long position, int line, int column, int tabWidth)
				{
					var rx = patterns.Select(p => new Regex(""\\G(?:"" + p + "")"")).ToArray();
					var sb = new StringBuilder();
					while (true)
					{
						sb.Clear();
						int c;
						while (-1 != (c = reader.Read()))
						{
							sb.Append((char)c);
							if ('\n' == c) break;
						}
						if (0 == sb.Length) yield break;
						var s = sb.ToString();
						for (var i = 0; i < s.Length;)
						{
							int best = -1, len = 0;
							for (var k = 0; k < rx.Length; ++k)
							{
								var m = rx[k].Match(s, i);
								if (m.Success && m.Length > len) { best = k; len = m.Length; }
							}
							if (0 == len) { best = -1; len = 1; }
							var text = s.Substring(i, len);
							var t = new Token(best, text, line, column, position);
							LexerController.Advance(text, tabWidth, ref line, ref column);
							position += len;
							i += len;
							if (0 <= best && hidden[best]) continue;
							yield return t;
						}
					}
				}
			}";

		// a test lexer for the terminals, as a class with the static Tokenize the controller calls
		static string _Lexer(string className, IEnumerable<TerminalDeclaration> terminals)
		{
			var list = terminals.ToList();
			string str(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";
			return $@"
				using System.Collections.Generic;
				using System.IO;
				using Parsley.Runtime;
				public static class {className}
				{{
					static readonly string[] s_patterns = {{ {string.Join(", ", list.Select(t => str(t.Regex!)))} }};
					static readonly bool[] s_hidden = {{ {string.Join(", ", list.Select(t => t.IsHidden ? "true" : "false"))} }};
					public static IEnumerable<Token> Tokenize(TextReader reader, long position = 0, int line = 1, int column = 1, int tabWidth = 4)
						=> TestLexing.Tokenize(s_patterns, s_hidden, reader, position, line, column, tabWidth);
				}}";
		}

		// runs text through a compiled grammar's Tokenize and TryParse, returning the tree and the errors
		const string Driver = @"
			using System.Collections.Generic;
			using System.IO;
			using System.Linq;
			public static class Driver
			{
				public static string Run(string text, List<string> errors)
				{
					OuterParser.TryParse(OuterParser.Tokenize(text), out var tree, out var errs);
					errors.AddRange(errs.Select(e => e.ToString()));
					return tree.ToString();
				}
				public static string RunReader(TextReader reader, List<string> errors)
				{
					OuterParser.TryParse(OuterParser.Tokenize(reader), out var tree, out var errs);
					errors.AddRange(errs.Select(e => e.ToString()));
					return tree.ToString();
				}
				public static string Tokens(string text) => string.Join(""\n"", OuterParser.Tokenize(text).Select(t => $""{t.SymbolId} {t.Line}:{t.Column}@{t.Position} {t.Value.Replace(""\n"", ""\\n"")}""));
			}";

		static CompiledParser? s_compiled;
		static CompiledParser _Compiled(ParserEngine innerEngine = ParserEngine.Auto)
		{
			if (ParserEngine.Auto == innerEngine && null != s_compiled) return s_compiled;
			var outer = GrammarAnalysis.Analyze(_Grammar("outer.xbnf"));
			GrammarException.ThrowIfErrors(outer.Messages);
			var inner = GrammarAnalysis.Analyze(_Grammar("inner.xbnf"), innerEngine);
			GrammarException.ThrowIfErrors(inner.Messages);
			var result = CompiledParser.Compile(CSharpSynthesizer.Synthesize(outer), "OuterParser",
				CSharpSynthesizer.Synthesize(inner),
				Lexing,
				_Lexer("OuterLexer", outer.Grammar.Terminals.Where(t => t.IsLexed)),
				_Lexer("InnerLexer", inner.Grammar.Terminals.Where(t => t.IsLexed)),
				Driver);
			if (ParserEngine.Auto == innerEngine) s_compiled = result;
			return result;
		}
		static string _Run(CompiledParser p, string text, out List<string> errors)
		{
			errors = new List<string>();
			var m = p.Assembly.GetType("Driver")!.GetMethod("Run")!;
			try { return (string)m.Invoke(null, new object[] { text, errors })!; }
			catch (TargetInvocationException ex) { throw ex.InnerException!; }
		}

		public static void ReadsExternalsLexersAndIslands()
		{
			var g = _Grammar("outer.xbnf");
			T.Equal(1, g.Externals.Count, "one @external");
			T.Equal("J", g.Externals[0].Alias, "its alias");
			T.Check(null != g.Externals[0].Grammar?.GetProduction("Obj"), "the external grammar is read, apart from this one");
			T.Check(null == g.GetProduction("Obj") && null == g.GetTerminal("str"), "and none of its rules are merged in");
			T.Equal("OuterLexer", g.GetLexerType(null), "@lexer for the grammar itself");
			T.Equal("InnerLexer", g.GetLexerType("J"), "@lexer for an alias");
			var data = g.GetTerminal("Data")!;
			T.Check(data.IsIsland && !data.IsContextual && !data.IsLexed && null == data.Regex, "a rule with entry and no body is an island: a terminal no lexer produces");
			T.Equal("J.Obj", data.Entry, "its entry");
			var text = g.ToString();
			T.Check(text.Contains("@external J \"inner.xbnf\";") && text.Contains("@lexer J \"InnerLexer\";") && text.Contains("Data<entry=\"J.Obj\""), "written back out");
			var g2 = XbnfParser.TryParse(text, _Path("outer.xbnf"), _Read, out var back);
			T.Check(!g2.Any(m => ErrorLevel.Error == m.Level) && (back?.GetTerminal("Data")?.IsIsland ?? false), "and read back");
		}

		public static void IslandsAreOrderedAfterLexedTerminals()
		{
			var a = GrammarAnalysis.Analyze(_Grammar("outer.xbnf"));
			GrammarException.ThrowIfErrors(a.Messages);
			var terms = a.Grammar.Terminals;
			var firstIsland = terms.FindIndex(t => t.IsIsland);
			T.Check(terms.Take(firstIsland).All(t => t.IsLexed) && terms.Skip(firstIsland).All(t => t.IsIsland), "lexed terminals first, so the lexer's ids match the parser's; islands after them");
			var inner = GrammarAnalysis.Analyze(_Grammar("inner.xbnf"));
			T.Check(inner.GetFollowTokens("Arr").Contains(inner.Symbols.EndOfInput), "a shared production can end the input, as an island");
		}

		public static void ValidatesIslands()
		{
			string outer(string island) => $@"@external J ""inner.xbnf""; S= a X; a= ""a""; {island}";
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Pair"", after=""a"">;")), ErrorLevel.Error, "isn't marked shared"), "entry must be shared");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Nope"", after=""a"">;")), ErrorLevel.Error, "has no production"), "entry must exist");
			T.Check(_Has(_Messages(outer(@"X<entry=""K.Obj"", after=""a"">;")), ErrorLevel.Error, "isn't declared with @external"), "the alias must be declared");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"">;")), ErrorLevel.Error, "needs after="), "after is required");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""zz"">;")), ErrorLevel.Error, "isn't a terminal"), "after names terminals");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""a"", pop=""J.nope"">;")), ErrorLevel.Error, "isn't one of J's terminals"), "pop names the external grammar's terminals");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""a"", pop=""J.ws"">;")), ErrorLevel.Error, "never reports"), "pop can't be hidden");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""a"", nest=""J.lbrace"">;")), ErrorLevel.Warning, "nest does nothing without pop"), "nest needs pop");
			T.Check(_Has(_Messages(@"S= a X; a= ""a""; X;"), ErrorLevel.Error, "only an island"), "only an island has no body");
			T.Check(_Has(_Messages(@"@external J ""missing.xbnf""; S= a;"), ErrorLevel.Error, "Could not read @external"), "a missing file");
			T.Check(_Has(_Messages(@"@external J ""loop.xbnf""; S= ""a"";", new() { ["loop.xbnf"] = @"@external K ""test.xbnf""; L= ""b"";" }), ErrorLevel.Error, "leads back"), "a grammar can't embed itself");
			// the island starts after every a, so wherever something else follows an a, that can never parse
			T.Check(_Has(_Messages(@"@external J ""inner.xbnf""; S= a X | a b; a= ""a""; b= ""b""; X<entry=""J.Obj"", after=""a"">;"), ErrorLevel.Warning, "can follow \"a\" too"), "a trigger something else can follow");
			T.Check(!_Messages(outer(@"X<entry=""J.Obj"", after=""a"", nest=""J.lbrace"", pop=""J.rbrace"">;")).Any(m => ErrorLevel.Message != m.Level), "a well-formed island");
			T.Check(!_Messages(outer(@"X<entry=""J.Obj"", after=""a"", nest=""J.str"", pop=""J.str"">;")).Any(m => ErrorLevel.Message != m.Level), "nest and pop can be the same terminal");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""a"", startsWith=""J.nope"">;")), ErrorLevel.Error, "isn't one of J's terminals"), "startsWith names the external grammar's terminals");
			T.Check(_Has(_Messages(outer(@"X<entry=""J.Obj"", after=""a"", startsWith=""J.ws"">;")), ErrorLevel.Error, "never reports"), "startsWith can't be hidden");
			T.Check(_Has(_Messages(@"@external J ""inner.xbnf""; a= ""a""; b= ""b""; S= a b;  X= ""x""; Y= ""y""; Z<entry=""J.Obj"", startsWith=""J.lbrace"">; "), ErrorLevel.Error, "needs after="), "startsWith still needs after");
			string two(string first, string second) => $@"@external J ""inner.xbnf""; S= a ( X | Y ); a= ""a""; {first} {second}";
			T.Check(!_Messages(two(@"X<entry=""J.Obj"", after=""a"", startsWith=""J.lbrace"">;", @"Y<entry=""J.Arr"", after=""a"">;")).Any(m => ErrorLevel.Message != m.Level), "islands sharing a trigger, the first with startsWith");
			T.Check(!_Messages(two(@"X<entry=""J.Obj"", after=""a"", startsWith=""J.lbrace"">;", @"Y<entry=""J.Arr"", after=""a"", startsWith=""J.lbracket"">;")).Any(m => ErrorLevel.Message != m.Level), "islands sharing a trigger, both with startsWith");
			T.Check(_Has(_Messages(two(@"X<entry=""J.Obj"", after=""a"">;", @"Y<entry=""J.Arr"", after=""a"">;")), ErrorLevel.Warning, "never starts after"), "an island after one without startsWith never starts");
			T.Check(_Has(_Messages(@"@external J ""inner.xbnf""; S= a ( X | b ); a= ""a""; b= ""b""; X<entry=""J.Obj"", after=""a"", startsWith=""J.lbrace"">; Y<entry=""J.Arr"", after=""a"">; T= a Y;"), ErrorLevel.Warning, "can follow"), "the fallback island still warns about what else can follow");
		}

		public static void SubordinateGrammars()
		{
			// shared productions and no start: only parsed as islands, every shared production a root
			const string sub = @"Obj<shared>= ""{"" [ Pair ] ""}""; Pair= str "":"" str; Arr<shared>= ""["" [ str ] ""]""; str= '""[^""]*""'; ws<hidden>= ' +';";
			var g = Grammar.Parse(sub, "sub.xbnf");
			T.Check(g.IsSubordinate, "is subordinate");
			var a = GrammarAnalysis.Analyze(g);
			T.Check(!a.Messages.Any(m => m.Message.Contains("unreachable")), "no unreachable warnings: every shared production is a root");
			var code = CSharpSynthesizer.Synthesize(a);
			T.Check(!code.Contains("public static bool TryParse(") && code.Contains("public static bool TryParseObj(") && code.Contains("public static bool TryParseArr("), "no TryParse of its own, an entry point for each shared production");
			T.Check(code.Contains("public static LexerMode CreateLexerMode(TokenizeFunc tokenize)"), "and a lexer mode for embedders");
			var withStart = GrammarAnalysis.Analyze(_Grammar("inner.xbnf"));
			var code2 = CSharpSynthesizer.Synthesize(withStart);
			T.Check(code2.Contains("public static bool TryParse(") && code2.Contains("TryParseObj("), "with a start too, it's standalone and embeddable");
		}

		public static void ParsesIslandsThroughTheController()
		{
			var p = _Compiled();
			var tree = _Run(p, "set a = {\"x\": {\"y\": [1, 2]},\n  \"z\": 3};\nlist [1, {\"k\": 2}] ;\necho \"done\";", out var errors);
			T.Equal(0, errors.Count, "no errors: " + string.Join("; ", errors));
			T.Check(tree.Contains("  Set\n") || tree.Contains("  Set\r\n"), "the statements parse");
			var lines = tree.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
			T.Check(lines.Contains("Data") && lines.Contains("Obj") && lines.Contains("Pair"), "an island's node holds the external grammar's tree");
			T.Check(lines.Contains("Items") && lines.Contains("Arr"), "an island that ends at the lexer error before ;");
			T.Check(lines.Contains("text \"\"done\"\""), "the outer grammar goes on after the islands");
			var tokens = (string)p.Assembly.GetType("Driver")!.GetMethod("Tokens")!.Invoke(null, new object[] { "set a = {\"x\":\n 1};echo \"e\";" })!;
			var echo = tokens.Split('\n').First(l => l.EndsWith(" \"e\""));
			T.Equal("2:10@23", echo.Split(' ')[1], "positions, lines and columns after an island are where the text is");
			var island = tokens.Split('\n').First(l => l.Contains("{\"x\""));
			T.Check(island.Contains("1:9@8") && island.EndsWith("{\"x\":\\n 1}"), "an island's token is its text, from its first token to its last");
		}

		public static void QuotedIslands()
		{
			var outer = GrammarAnalysis.Analyze(_Grammar("quoted-outer.xbnf"));
			GrammarException.ThrowIfErrors(outer.Messages);
			var inner = GrammarAnalysis.Analyze(_Grammar("quoted.xbnf"));
			GrammarException.ThrowIfErrors(inner.Messages);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(outer), "OuterParser",
				CSharpSynthesizer.Synthesize(inner),
				Lexing,
				_Lexer("OuterLexer", outer.Grammar.Terminals.Where(t => t.IsLexed)),
				_Lexer("QuotedLexer", inner.Grammar.Terminals.Where(t => t.IsLexed)),
				Driver);
			// the island's lexer could read on through "x", so only the second quote can end the island there
			var tree = _Run(p, "say \"a b c\" \"x\";\nsay \"d\" \"e\";", out var errors);
			T.Equal(0, errors.Count, "no errors: " + string.Join("; ", errors));
			var lines = tree.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
			T.Equal(2, lines.Count(l => "Words" == l), "an island per say");
			T.Equal(4, lines.Count(l => l.StartsWith("word ")), "each island holds just the words between its quotes");
			T.Check(lines.Contains("text \"\"x\"\"") && lines.Contains("text \"\"e\"\""), "the outer grammar goes on after the closing quote");
			var tokens = (string)p.Assembly.GetType("Driver")!.GetMethod("Tokens")!.Invoke(null, new object[] { "say \"a b\" \"x\";" })!;
			T.Check(tokens.Split('\n').Any(l => l.EndsWith(" \"a b\"")), "the island's token is the whole quoted string");
			// a missing closing quote: the island's lexer can't read the ;, so the island ends there
			_Run(p, "say \"a b ; say \"c\" \"d\";", out errors);
			T.Check(0 < errors.Count, "an unterminated quote is an error");
		}

		public static void IslandEntriesWhoseNamesMeet()
		{
			var outer = GrammarAnalysis.Analyze(_Grammar("meet-outer.xbnf"));
			GrammarException.ThrowIfErrors(outer.Messages);
			var inner = GrammarAnalysis.Analyze(_Grammar("meet.xbnf"));
			GrammarException.ThrowIfErrors(inner.Messages);
			var p = CompiledParser.Compile(CSharpSynthesizer.Synthesize(outer), "OuterParser",
				CSharpSynthesizer.Synthesize(inner),
				Lexing,
				_Lexer("OuterLexer", outer.Grammar.Terminals.Where(t => t.IsLexed)),
				_Lexer("QuotedLexer", inner.Grammar.Terminals.Where(t => t.IsLexed)),
				Driver);
			// phraseX takes no words, so words between the quotes parse only if the island is parsed as phrase_x
			var tree = _Run(p, "say \"a b\" \"x\";", out var errors);
			T.Equal(0, errors.Count, "the island is parsed by phrase_x: " + string.Join("; ", errors));
			T.Equal(2, tree.Replace("\r\n", "\n").Split('\n').Count(l => l.Trim().StartsWith("word ")), "and holds its words");
		}

		public static void IslandsStartWith()
		{
			var outer = GrammarAnalysis.Analyze(_Grammar("dispatch-outer.xbnf"));
			GrammarException.ThrowIfErrors(outer.Messages);
			T.Check(!outer.Messages.Any(m => ErrorLevel.Message != m.Level), "no warnings: " + string.Join("; ", outer.Messages));
			var inner = GrammarAnalysis.Analyze(_Grammar("inner.xbnf"));
			var code = CSharpSynthesizer.Synthesize(outer);
			T.Check(code.Contains("startsWith: ["), "the island rule carries startsWith");
			var p = CompiledParser.Compile(code, "OuterParser",
				CSharpSynthesizer.Synthesize(inner),
				Lexing,
				_Lexer("OuterLexer", outer.Grammar.Terminals.Where(t => t.IsLexed)),
				_Lexer("InnerLexer", inner.Grammar.Terminals.Where(t => t.IsLexed)),
				Driver);
			var tree = _Run(p, "set a = {\"x\": [1]};\nset b = [1, {\"y\": 2}] ;\nset c = \"plain\";", out var errors);
			T.Equal(0, errors.Count, "no errors: " + string.Join("; ", errors));
			var lines = tree.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
			T.Check(lines.Contains("Data") && lines.Contains("Obj"), "a { starts the object island");
			T.Check(lines.Contains("Items") && lines.Contains("Arr"), "a [ starts the array island, the second after the same =");
			T.Check(lines.Contains("text \"\"plain\"\""), "where neither starts, the outer grammar's lexer goes on");
			T.Equal(1, lines.Count(l => "Data" == l), "one object island");
			T.Equal(1, lines.Count(l => "Items" == l), "one array island");
		}

		public static void IslandErrorsAreLocal()
		{
			var p = _Compiled();
			// inside the island: the island's parser reports it and recovers; the outer grammar never sees it
			_Run(p, "set a = {\"x\" 1}; echo \"ok\";", out var errors);
			T.Equal(1, errors.Count, "one error, from the island: " + string.Join("; ", errors));
			T.Check(errors.Count > 0 && errors[0].Contains("\":\"") && errors[0].Contains("column 14"), "where it is, in the whole text");
			// the island's lexer can't read ;, so the unterminated object ends there, and the ; is the outer grammar's
			_Run(p, "set a = {\"x\": 1 ; echo \"ok\";", out errors);
			T.Equal(1, errors.Count, "one error for the unterminated island: " + string.Join("; ", errors));
			T.Check(errors.Count > 0 && errors[0].Contains("end of input"), "the island ended");
		}

		public static void ReadsLargeInputThroughABuffer()
		{
			var p = _Compiled();
			var sb = new StringBuilder();
			for (var i = 0; i < 5000; ++i)
				sb.Append("set v = {\"n\": ").Append(i).Append(", \"a\": [").Append(i).Append(", {\"q\": \"").Append(new string('x', i % 50)).Append("\"}]};\necho \"line\";\n");
			var errors = new List<string>();
			var m = p.Assembly.GetType("Driver")!.GetMethod("RunReader")!;
			var tree = (string)m.Invoke(null, new object[] { new StringReader(sb.ToString()), errors })!;
			T.Equal(0, errors.Count, "a long input from a reader: " + string.Join("; ", errors.Take(3)));
			T.Equal(5000, tree.Replace("\r\n", "\n").Split('\n').Count(l => "Data" == l.Trim()), "every island");
		}

		public static void SharedEntriesWithAdaptivePrediction()
		{
			// the island grammar on ALL(*): a parse that starts at a shared production other than the start ends there
			var p = _Compiled(ParserEngine.AllStar);
			_Run(p, "set a = {\"x\": [1, {\"y\": 2}]}; list [3, [4]];", out var errors);
			T.Equal(0, errors.Count, "no errors: " + string.Join("; ", errors));
		}

		// a terminal another lexer produces: / is division after an operand, and the start of a regex otherwise
		const string Slashes = @"
			@class ""SlashParser"";
			@lexer ""SlashLexer"";
			@lexer Rx ""RxLexer"";
			Prog<start>= { Expr "";"" };
			Expr= Term { slash Term };
			Term<collapsed>= name | num | regex | ""("" Expr "")"";
			regex<lexer=Rx, on=""slash"", when=""RegexCanStart"">= '/[^/\n]+/';
			slash= ""/"";
			name= '[a-z]+';
			num= '[0-9]+';
			ws<hidden>= '[ \t\r\n]+';";
		const string SlashHooks = @"
			using Parsley.Runtime;
			public partial class SlashParser
			{
				// after a name, number or ), / divides
				private static partial bool RegexCanStart(Token token, Token previous)
					=> !(0 < previous.Value.Length && (char.IsLetterOrDigit(previous.Value[0]) || "")"" == previous.Value));
			}
			public static class SlashDriver
			{
				public static string Run(string text) => SlashParser.Parse(text).ToString();
			}";

		public static void OtherLexersProduceTerminals()
		{
			var a = GrammarAnalysis.Analyze(Grammar.Parse(Slashes, "slashes.xbnf"));
			GrammarException.ThrowIfErrors(a.Messages);
			var regex = a.Grammar.GetTerminal("regex")!;
			T.Check(!regex.IsLexed && !regex.IsContextual && "Rx" == regex.Lexer, "a terminal from another lexer");
			T.Equal(a.Grammar.Terminals.Count - 1, a.Grammar.Terminals.IndexOf(regex), "after the grammar's own lexer's terminals");
			var code = CSharpSynthesizer.Synthesize(a);
			T.Check(code.Contains("private static partial bool RegexCanStart(Token token, Token previous);"), "its when is the controller's, so it's static");
			var p = CompiledParser.Compile(code, "SlashParser", SlashHooks, Lexing,
				_Lexer("SlashLexer", a.Grammar.Terminals.Where(t => t.IsLexed)),
				_Lexer("RxLexer", a.Grammar.Terminals.Where(t => "Rx" == t.Lexer)));
			var tree = (string)p.Assembly.GetType("SlashDriver")!.GetMethod("Run")!.Invoke(null, new object[] { "a / b;\n/x y/;\n(c) / /re/;" })!;
			var lines = tree.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
			T.Equal(2, lines.Count(l => l.StartsWith("slash ")), "/ after an operand divides");
			T.Check(lines.Contains("regex \"/x y/\"") && lines.Contains("regex \"/re/\""), "/ elsewhere starts a regex, lexed by the other lexer");
		}

		public static void ValidatesOtherLexers()
		{
			T.Check(_Has(_Messages(@"S= r; r<lexer=Rx>= '/x/';"), ErrorLevel.Error, "needs on="), "on is required");
			T.Check(_Has(_Messages(@"S= r s; s= ""/""; r<lexer=Rx, on=""nope"">= '/x/';"), ErrorLevel.Error, "isn't a terminal"), "on names terminals");
			T.Check(_Has(_Messages(@"@external J ""inner.xbnf""; S= r s; s= ""/""; r<lexer=J, on=""s"">= '/x/';"), ErrorLevel.Error, "is an @external grammar"), "an external grammar isn't a lexer=");
			T.Check(_Has(_Messages(@"@lexer Zz ""ZLexer""; S= ""a"";"), ErrorLevel.Warning, "names neither"), "an unused @lexer alias");
		}

		public static void NamesTypesForExternals()
		{
			var g = _Grammar("outer.xbnf");
			T.Equal("InnerLexer", CSharpSynthesizer.DefaultLexerType(g, "J"), "@lexer names it");
			T.Equal("InnerParser", CSharpSynthesizer.DefaultExternalParser(g, "J"), "the external grammar's @class");
			// without @lexer: the external grammar's own @lexer, else the alias plus Lexer, in its namespace
			var files = new Dictionary<string, string>
			{
				["a.xbnf"] = @"@class ""My.A.AParser""; A<shared>= ""a"";",
				["b.xbnf"] = @"@class ""My.B.BParser""; @lexer ""BLex""; B<shared>= ""b"";",
				["c.xbnf"] = @"@class ""My.C.CParser""; @lexer ""Other.Lexers.CLex""; C<shared>= ""c"";",
				["d.xbnf"] = @"@class ""DParser""; D<shared>= ""d"";",
			};
			var msgs = XbnfParser.TryParse(@"@external Aa ""a.xbnf""; @external Bb ""b.xbnf""; @external Cc ""c.xbnf""; @external Dd ""d.xbnf""; S= ""x"";", _Path("names.xbnf"), p => files[Path.GetFileName(p)], out var n);
			T.Equal(0, msgs.Count(m => m.Level == ErrorLevel.Error), "the grammars read");
			T.Equal("global::My.A.AaLexer", CSharpSynthesizer.DefaultLexerType(n!, "Aa"), "the alias plus Lexer, in the external grammar's namespace");
			T.Equal("global::My.B.BLex", CSharpSynthesizer.DefaultLexerType(n!, "Bb"), "the external grammar's own @lexer, in its namespace");
			T.Equal("global::My.A.AParser", CSharpSynthesizer.DefaultExternalParser(n!, "Aa"), "its parser, from its @class");
			T.Equal("global::Other.Lexers.CLex", CSharpSynthesizer.DefaultLexerType(n!, "Cc"), "the external grammar's @lexer, in the namespace it gives");
			T.Equal("global::My.C.CParser", CSharpSynthesizer.DefaultExternalParser(n!, "Cc"), "its parser, in the namespace its @class gives");
			T.Equal("DParser", CSharpSynthesizer.DefaultExternalParser(n!, "Dd"), "a parser in the global namespace");
			T.Equal("DdLexer", CSharpSynthesizer.DefaultLexerType(n!, "Dd"), "and its lexer");
			var own = Grammar.Parse(@"@class ""My.P.Parser""; @lexer ""My.L.Lexer""; @lexer Rx ""Rx.RxLexer""; S= a r; a= ""a""; r<lexer=Rx, on=""a"">= 'r';");
			T.Equal("global::My.L.Lexer", CSharpSynthesizer.DefaultLexerType(own, null), "the grammar's @lexer, in the namespace it gives");
			T.Equal("global::Rx.RxLexer", CSharpSynthesizer.DefaultLexerType(own, "Rx"), "a lexer= alias's @lexer, in the namespace it gives");
			T.Equal("NamesLexer", CSharpSynthesizer.DefaultLexerType(n!, null), "the grammar's own: its file name plus Lexer");
		}
	}
}
