using System;
using System.IO;
using System.Linq;
using static Parsley.Ebnf;

namespace Parsley.Tests
{
	public static class TerminalTests
	{
		static Grammar G(string xbnf) => Grammar.Parse(xbnf);
		static string Rx(Grammar g, string name) => g.GetTerminal(name)?.Regex ?? "(not a terminal)";

		public static void OnlyRulesWithoutReferencesAreTerminalsByDefault()
		{
			var g = Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf"));
			T.Check(null != g.GetTerminal("true") && null != g.GetTerminal("lbracket"), "literal rules are terminals");
			T.Check(null != g.GetProduction("Boolean"), "Boolean references symbols, so it stays a non-terminal");
			T.Equal("\\[", Rx(g, "lbracket"), "literal escaped");
			T.Equal("\"([^\\n\"\\\\]|\\\\([btrnf\"\\\\/]|(u[0-9A-Fa-f]{4})))*\"", Rx(g, "string"), "pattern passed through without its quotes");
		}

		public static void ForcedTerminalsInlineOtherTerminals()
		{
			var g = G("S= Ident; Ident<terminal>= letter { letter | digit | \"_\" }; letter='[a-z]'; digit='[0-9]';");
			T.Equal("[a-z]([a-z]|[0-9]|_)*", Rx(g, "Ident"), "built from constructs, single-class patterns need no parentheses");
			T.Check(null == g.GetProduction("Ident"), "Ident moved to terminals");
			T.Check(!g.GetTerminal("Ident")!.Attributes.Contains("terminal"), "the terminal attribute is consumed");
		}

		public static void RegexConstruction()
		{
			var g = G("S= A B C D E F; A<terminal>= \"a.b\" | \"(\"; B<terminal>= \"x\" ( \"y\" | ); C<terminal>= [ \"-\" ] d; d='[0-9]+'; E<terminal>= { \"ab\" }+; F<terminal>= d \"/\" d;");
			T.Equal("a\\.b|\\(", Rx(g, "A"), "alternation of escaped literals");
			T.Equal("x(y)?", Rx(g, "B"), "an empty alternative becomes optional");
			T.Equal("-?([0-9]+)", Rx(g, "C"), "optional atom, opaque pattern wrapped");
			T.Equal("(ab)+", Rx(g, "E"), "one or more");
			T.Equal("([0-9]+)\\/([0-9]+)", Rx(g, "F"), "slash escaped (flex trailing context)");
		}

		public static void TerminalFalseForcesANonTerminal()
		{
			var g = G("S= K x; K<terminal=false>= \"k\"; x=\"x\";");
			T.Check(null != g.GetProduction("K"), "K stays a production");
			T.Check(!g.GetProduction("K")!.Attributes.Contains("terminal"), "attribute consumed");
		}

		public static void StartRuleIsNeverAutomaticallyATerminal()
		{
			var g = G("S= \"a\";");
			T.Check(null != g.GetProduction("S"), "lone start rule stays a production");
		}

		public static void ForcedTerminalErrors()
		{
			var msgs = XbnfReader.TryParse("S= T; T<terminal>= \"a\" S;", null, out _);
			T.Check(msgs.Any(m => m.Level == ErrorLevel.Error && m.Message.Contains("references \"S\", which is not a terminal")), "terminal referencing a non-terminal");
			msgs = XbnfReader.TryParse("S= T; T<terminal>= \"a\" [ T ];", null, out _);
			T.Check(msgs.Any(m => m.Level == ErrorLevel.Error && m.Message.Contains("refers to itself")), "recursive terminal");
		}

		public static void QuotesInPatterns()
		{
			var g = G("S= q; q= 'it\\'s\\\\';");
			T.Equal("it's\\\\", Rx(g, "q"), "\\' becomes ' while \\\\ stays");
			T.Equal("q= 'it\\'s\\\\';", g.GetTerminal("q")!.ToString(), "written back with the quote escaped");
		}

		public static void BuilderTerminals()
		{
			var g = new Grammar();
			g.Terminal("letter", Ebnf.Lit("x"));
			g.Terminal("Word", Rep1("letter"));
			g.Production("S", Sym("Word"));
			GrammarException.ThrowIfErrors(g.Validate());
			T.Equal("x+", Rx(g, "Word"), "builder terminal built from another terminal");
		}

		public static void ExplicitDeclarationsSetLexerPriority()
		{
			// inline "if" resolves to the explicit declaration, which comes before id, so it wins the tie
			var a = GrammarAnalysis.Analyze(G("S= \"if\" id; ifKeyword= \"if\"; id='[a-z]+'; ws<hidden>= ' +';"));
			var tokens = new RegexLexer(a).Tokenize("if x").ToList();
			T.Equal("ifKeyword id", string.Join(" ", tokens.Select(t => a.Symbols.GetName(t.SymbolId + a.Symbols.NonTerminalCount))), "declared keyword wins the tie");
			T.Check(!a.Grammar.Terminals.Any(t => t.IsImplicit), "no implicit terminal when one is declared");
			// without a declaration the implicit terminal goes last
			var b = GrammarAnalysis.Analyze(G("S= \"if\" id; id='[a-z]+';"));
			T.Equal("ifKeyword", b.Grammar.Terminals.Last().Name, "implicit terminal appended after declared ones");
		}
	}
}
