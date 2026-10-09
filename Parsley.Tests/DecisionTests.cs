using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// One grammar per decision kind or feature: synthesize, compile, parse, and check against the interpreter
	/// </summary>
	public static class DecisionTests
	{
		sealed class Harness
		{
			public GrammarAnalysis Analysis = null!;
			public CompiledParser Parser = null!;
			public RegexLexer Lexer = null!;
			public string Source = "";
			public (string Tree, List<string> Errors) Parse(string text)
			{
				var tokens = Lexer.Tokenize(text).ToList();
				var s = Parser.Parse(tokens);
				var i = Interp.Parse(Analysis, tokens);
				T.Equal(i.Tree, s.Tree, $"interpreter and synthesized trees agree on \"{text}\"");
				T.Equal(string.Join("; ", i.Errors), string.Join("; ", s.Errors), $"interpreter and synthesized errors agree on \"{text}\"");
				return s;
			}
			public bool Accepts(string text) => 0 == Parse(text).Errors.Count;
		}
		static Harness _Build(string name, string xbnf)
		{
			var g = Grammar.Parse(xbnf, name + ".xbnf");
			var a = GrammarAnalysis.Analyze(g);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			File.WriteAllText(Path.Combine(SynthesisTests.OutDir, g.ClassName + ".cs"), src);
			var h = new Harness { Analysis = a, Source = src, Parser = CompiledParser.Compile(src, g.ClassName!), Lexer = new RegexLexer(a) };
			T.Equal("", string.Join("\n", h.Parser.Diagnostics), name + " compiles without warnings");
			return h;
		}
		const string Ws = " ws<hidden>= '[ \\t\\r\\n]+';";

		public static void FixedLookaheadUsesAPredictor()
		{
			var h = _Build("fixed", "P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";" + Ws);
			T.Check(h.Source.Contains("int PredictS1()"), "predictor method emitted");
			T.Check(h.Source.Contains("switch (Peek(2).SymbolId)"), "looks three tokens deep");
			T.Check(h.Accepts("a b c x") && h.Accepts("a b d x") && h.Accepts("y"), "accepts");
			T.Check(!h.Accepts("a b x") && !h.Accepts("a b c"), "rejects");
			T.Check(h.Parse("a b d x").Tree.Contains("d \"d\""), "picked the second alternative");
		}

		public static void LoopNeedingTwoTokens()
		{
			var h = _Build("loop2", "S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";" + Ws);
			T.Check(h.Source.Contains("while (0 == PredictS1())"), "loop driven by a predictor");
			T.Check(h.Accepts("a c") && h.Accepts("a b a c") && h.Accepts("a b a b a c"), "accepts");
			T.Check(!h.Accepts("a b") && !h.Accepts("a b c"), "rejects");
		}

		public static void DanglingElseBindsToTheNearestIf()
		{
			var h = _Build("ifelse", "S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";" + Ws);
			T.Check(h.Source.Contains("resolved greedily"), "the policy is explained in a comment");
			var tree = h.Parse("if e then if e then s else s").Tree;
			// the else belongs to the inner if: the outer S has 4 children, the inner one 6
			var lines = tree.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
			var elseLine = lines.First(l => l.Trim().StartsWith("elseKeyword"));
			T.Equal(4, elseLine.Length - elseLine.TrimStart().Length, "else is a child of the inner S (indent 4)");
		}

		public static void LeftRecursionFoldsLeft()
		{
			var h = _Build("expr", "E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';" + Ws);
			T.Check(h.Source.Contains("node = Fold(node);"), "fold emitted");
			T.Check(h.Source.Contains("(left recursion rewritten as a loop)"), "rewrite noted in the comment");
			var tree = h.Parse("1 - 2 - 3").Tree;
			var expected = string.Join(Environment.NewLine,
				"E", "  E", "    E", "      T", "        F", "          n \"1\"",
				"    minus \"-\"", "    T", "      F", "        n \"2\"",
				"  minus \"-\"", "  T", "    F", "      n \"3\"", "");
			T.Equal(expected, tree, "(1 - 2) - 3");
			T.Check(h.Accepts("(1 + 2) * 3 - 4 * (5)"), "precedence grammar accepts");
			T.Check(!h.Accepts("1 + * 2"), "rejects");
		}

		public static void InvertedLoopTestAndSyncRecovery()
		{
			var h = _Build("block",
				"Block= \"{\" { Stmt }<sync=\"semi\"> \"}\";" +
				"Stmt<expected=\"a statement\">= a \"=\" n semi | b \"(\" \")\" semi | c semi | d semi;" +
				"a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; n='[0-9]+'; semi= \";\";" + Ws);
			T.Check(h.Source.Contains("while (Current.SymbolId is not (Rbrace or EndOfInput))"), "tests the small exit side");
			T.Check(h.Accepts("{ a = 1; b(); c; }"), "accepts");
			var (tree, errors) = h.Parse("{ a = ; c; 7 7; d; }");
			T.Equal(2, errors.Count, "two separate errors: " + string.Join(" / ", errors));
			T.Check(errors[1].StartsWith("Expected a statement but found n 7"), "expected attribute names the production: " + errors[1]);
			T.Check(tree.Contains("d \"d\""), "parsing resumed at the statement after the bad ones");
		}

		public static void OneOrMoreAndCollapsed()
		{
			var h = _Build("list", "L= { Item }+; Item<collapsed>= Word | n; Word= w; w='[a-z]+'; n='[0-9]+';" + Ws);
			T.Check(h.Source.Contains("do\n") || h.Source.Contains("do\r\n"), "do/while for one-or-more");
			T.Check(h.Accepts("x") && h.Accepts("x 1 y"), "accepts");
			T.Check(!h.Accepts(""), "needs at least one");
			T.Check(!h.Parse("x 1").Tree.Contains("Item"), "collapsed Item leaves no node");
		}

		public static void AwkwardNamesStillCompile()
		{
			var h = _Build("names",
				"Parse= error class \"+=\" Current \"while\" Errors; Current= \"x\"; Errors= int; error=\"e\"; class=\"c\"; int='[0-9]+';" + Ws);
			T.Check(h.Accepts("e c += x while 5"), "accepts");
		}

		public static void OptionalNeedingFollowContext()
		{
			var h = _Build("follow", "S= A a; A= b [ a ]; a=\"a\"; b=\"b\";" + Ws);
			T.Check(h.Accepts("b a") && h.Accepts("b a a"), "two tokens tell take from skip");
			T.Check(!h.Accepts("b a a a"), "rejects");
		}

		public static void FuzzAlwaysTerminatesAndAgrees()
		{
			var grammars = new (string Name, string Xbnf, string[] Words)[]
			{
				("fz1", "E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';", new[] { "1", "+", "-", "*", "(", ")" }),
				("fz2", "Block= \"{\" { Stmt }<sync=\"semi\"> \"}\"; Stmt= a \"=\" n semi | b \"(\" \")\" semi | c semi; a=\"a\"; b=\"b\"; c=\"c\"; n='[0-9]+'; semi= \";\";", new[] { "{", "}", "a", "b", "c", "=", "1", "(", ")", ";" }),
				("fz3", "S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";", new[] { "if", "e", "then", "else", "s" }),
				("fz4", "P= S x | y; S= a b c | a b d | { a } e; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; e=\"e\"; x=\"x\"; y=\"y\";", new[] { "a", "b", "c", "d", "e", "x", "y" }),
			};
			var r = new Random(3);
			foreach (var (name, xbnf, words) in grammars)
			{
				var h = _Build(name, xbnf + Ws);
				var before = T.Fails;
				for (var i = 0; i < 300 && T.Fails == before; ++i)
				{
					var n = r.Next(14);
					var text = string.Join(" ", Enumerable.Range(0, n).Select(_ => words[r.Next(words.Length)]));
					h.Parse(text);
				}
			}
		}

		public static void SlangImportsAreReported()
		{
			// Slang.xbnf imports files we don't have and declares virtual productions
			var msgs = XbnfReader.TryReadFrom(Path.Combine(T.Dir, "Slang.xbnf"), out _);
			T.Check(msgs.Any(m => m.Message.Contains("Could not import")), "missing imports reported");
			T.Check(msgs.Any(m => m.Message.Contains("Virtual productions are not supported")), "virtual productions reported");
		}
	}
}
