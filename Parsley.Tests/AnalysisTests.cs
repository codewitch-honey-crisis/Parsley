using System;
using System.IO;
using System.Linq;
using System.Text;
namespace Parsley.Tests
{
	public static class AnalysisTests
	{
		static GrammarAnalysis A(string xbnf, int maxK = 4) => GrammarAnalysis.Analyze(Grammar.Parse(xbnf));
		static string Errors(GrammarAnalysis a) => string.Join("; ", a.Messages.Where(m => m.Level == ErrorLevel.Error).Select(m => m.Message));

		public static void JsonIsAllOneToken()
		{
			var a = GrammarAnalysis.Analyze(Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf")));
			T.Equal("", Errors(a), "no errors");
			T.Equal(1, a.Depth, "json needs one token everywhere");
			var obj = a.Grammar.GetProduction("Object")!;
			var opt = (OptionalExpression)((Sequence)obj.Expression).Items[1];
			var d = a.GetDecision(opt)!;
			T.Equal(DecisionKind.Optional, d.Kind, "optional decision");
			T.Equal("string", string.Join(",", d.TokensOf(0).Select(a.Symbols.GetName)), "optional starts with string");
			T.Equal(1, d.DefaultBranch, "tests the take side: 1 token vs 2");
			var loop = a.Decisions.First(x => x.Production.Name == "Object" && x.Kind == DecisionKind.Loop);
			T.Equal("comma", string.Join(",", loop.TokensOf(0).Select(a.Symbols.GetName)), "loop continues on comma");
			T.Equal(1, loop.DefaultBranch, "comma loop: tie, but } is checked right after, so test the comma");
			var value = a.Decisions.First(x => x.Production.Name == "Value");
			T.Equal(-1, value.DefaultBranch, "6 options: switch with an error default");
		}
		public static void FixedLookahead()
		{
			var a = A("P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";");
			T.Equal("", Errors(a), "no errors");
			T.Equal(1, a.Decisions.First(d => d.Production.Name == "P").Depth, "P needs 1");
			T.Equal(3, a.Decisions.First(d => d.Production.Name == "S").Depth, "S needs 3");
		}
		public static void UsesFollowContext()
		{
			// the optional's exit depends on what follows it: [a #EOS] skips, [a a] takes
			var a = A("S= A a; A= b [ a ]; a=\"a\"; b=\"b\";");
			var d = a.Decisions.Single();
			T.Equal("", Errors(a), "no errors");
			T.Equal(2, d.Depth, "two tokens separate take from skip");
			T.Equal(null, d.AppliedPolicy, "no policy needed");
		}
		public static void LoopVersusExitNeedsTwoTokens()
		{
			// { a b } a c : continue on [a b], exit on [a c]
			var a = A("S= { a b } a c; a=\"a\"; b=\"b\"; c=\"c\";");
			T.Equal("", Errors(a), "no errors");
			var d = a.Decisions.Single();
			T.Equal(2, d.Depth, "loop needs 2");
		}
		public static void InversionPrefersSmallerSide()
		{
			// a block of statements: many ways to continue, one way out
			var a = A("Block= \"{\" { Stmt } \"}\"; Stmt= a \";\" | b \";\" | c \";\" | d \";\"; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\";");
			var loop = a.Decisions.First(x => x.Kind == DecisionKind.Loop);
			T.Equal(0, loop.DefaultBranch, "4 to continue vs } and EOS to exit: test the exit");
		}
		public static void AlternationConflictIsAnError()
		{
			var a = A("S= A | B; A= { a } x; B= { a } y; a=\"a\"; x=\"x\"; y=\"y\";", 3);
			T.Check(Errors(a).Contains("can't be told apart"), "unresolved alternation reported");
			var b = A("S= ( A | B )<policy=first>; A= { a } x; B= { a } y; a=\"a\"; x=\"x\"; y=\"y\";", 3);
			T.Equal("", Errors(b), "explicit policy accepted");
		}
		public static void DanglingElseIsGreedy()
		{
			var a = A("S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";");
			T.Equal("", Errors(a), "no errors");
			var opt = a.Decisions.First(d => d.Kind == DecisionKind.Optional);
			T.Equal("greedy", opt.AppliedPolicy, "greedy");
			T.Equal(1, opt.Depth, "depth 1");
			T.Equal("\"else\"", string.Join(",", opt.TokensOf(0).Select(a.Symbols.Display)), "else always taken");
			var lazy = A("S= \"if\" e \"then\" S [ \"else\" S ]<policy=lazy> | s; e=\"e\"; s=\"s\";");
			T.Equal(0, lazy.Decisions.First(d => d.Kind == DecisionKind.Optional).Branches[0].Count, "lazy never takes else");
			T.Check(!lazy.Messages.Any(m => m.Level == ErrorLevel.Warning && m.Message.Contains("ambiguous")), "explicit policy is quiet");
		}
		public static void DirectLeftRecursionBecomesAFoldingLoop()
		{
			var a = A("E= E \"+\" T | E \"-\" T | T; T= T \"*\" F | F; F= n | \"(\" E \")\"; n='[0-9]+';");
			T.Equal("", Errors(a), "no errors");
			var e = a.Grammar.GetProduction("E")!;
			T.Equal("T { \"+\" T | \"-\" T }", e.Expression.ToString(), "E rewritten");
			T.Check(((Repeat)((Sequence)e.Expression).Items[1]).LeftFold, "loop folds left");
			T.Equal(1, a.Depth, "expression grammar is one token after rewriting");
		}
		public static void IndirectLeftRecursion()
		{
			var a = A("A= B x | y; B= A z | w; x=\"x\"; y=\"y\"; z=\"z\"; w=\"w\";");
			T.Equal("", Errors(a), "no errors");
			T.Check(a.Messages.Any(m => m.Message.Contains("Inlined \"B\" into \"A\"")), "inlining reported");
			T.Equal("( w x | y ) { z x }", a.Grammar.GetProduction("A")!.Expression.ToString(), "A rewritten");
		}
		public static void HiddenLeftRecursionIsReported()
		{
			var a = A("A= N A x | y; N= [ n ]; n=\"n\"; x=\"x\"; y=\"y\";");
			T.Check(Errors(a).Contains("nullable prefix"), "hidden left recursion reported");
		}
		public static void NullableLoopBodyIsAnError()
		{
			var a = A("S= { [ a ] } b; a=\"a\"; b=\"b\";");
			T.Check(Errors(a).Contains("can match nothing"), "nullable loop body");
		}
		public static void InputGrammarIsUnchanged()
		{
			var g = Grammar.Parse("E= E \"+\" n | n; n='[0-9]+';");
			var before = g.ToString();
			GrammarAnalysis.Analyze(g);
			T.Equal(before, g.ToString(), "analysis works on a copy");
		}
	}
}
