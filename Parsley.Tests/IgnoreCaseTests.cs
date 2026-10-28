using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// The ignoreCase attribute on terminals and the @ignoreCase directive: (?i) in front of the regexes the lexer
	/// gets, overriding, inlining, lexer priority, and where it's an error
	/// </summary>
	public static class IgnoreCaseTests
	{
		static string Rx(Grammar g, string name) => g.GetTerminal(name)?.Regex ?? "(not a terminal)";
		static List<GrammarMessage> Messages(string xbnf)
		{
			var msgs = XbnfParser.TryParse(xbnf, null, out var g);
			if (msgs.Any(m => ErrorLevel.Error == m.Level)) return msgs.ToList();
			return GrammarAnalysis.Analyze(g!).Messages.ToList();
		}
		static IEnumerable<GrammarMessage> At(IEnumerable<GrammarMessage> msgs, ErrorLevel level, string contains)
			=> msgs.Where(m => level == m.Level && m.Message.Contains(contains));
		// the terminals the lexer reads from the text, by name
		static string Lex(GrammarAnalysis a, string text)
			=> string.Join(" ", new RegexLexer(a).Tokenize(text).Select(t => -1 == t.SymbolId ? "#error" : a.Symbols.GetName(t.SymbolId + a.Symbols.NonTerminalCount)));

		public static void OffUnlessAsked()
		{
			var g = Grammar.Parse("S= kw id; kw= \"select\"; id= '[a-z]+';");
			T.Check(!g.IgnoreCase, "@ignoreCase defaults to false");
			T.Equal("select", Rx(g, "kw"), "no attribute, no directive: the literal as is");
			T.Equal("[a-z]+", Rx(g, "id"), "and the pattern as is");
			T.Equal(null, g.GetTerminal("kw")!.IgnoreCase, "with no attribute, the terminal's own setting is null");
		}

		public static void AttributeOnATerminal()
		{
			var g = Grammar.Parse("S= kw id; kw<ignoreCase>= \"select\"; id= '[a-z]+';");
			T.Equal("(?i)select", Rx(g, "kw"), "<ignoreCase> puts (?i) in front");
			T.Equal("[a-z]+", Rx(g, "id"), "other terminals are left alone");
			T.Equal(true, g.GetTerminal("kw")!.IgnoreCase, "the attribute's value");
			var off = Grammar.Parse("S= kw; kw<ignoreCase=false>= \"select\";");
			T.Equal("select", Rx(off, "kw"), "ignoreCase=false leaves it as is");
			// a literal's metacharacters are escaped before (?i) goes on
			var esc = Grammar.Parse("S= kw; kw<ignoreCase>= \"a.b\";");
			T.Equal("(?i)a\\.b", Rx(esc, "kw"), "escaped literal");
		}

		public static void DirectiveSetsTheDefault()
		{
			var g = Grammar.Parse("@ignoreCase true;\nS= kw id \"from\"; kw= \"select\"; id= '[a-z]+'; cs<ignoreCase=false>= \"Exact\"; ws<hidden>= ' +';");
			T.Check(g.IgnoreCase, "@ignoreCase true");
			T.Equal("(?i)select", Rx(g, "kw"), "a declared literal");
			T.Equal("(?i)[a-z]+", Rx(g, "id"), "a pattern");
			T.Equal("(?i) +", Rx(g, "ws"), "a hidden terminal is lexed too");
			T.Equal("Exact", Rx(g, "cs"), "the attribute overrides the directive");
			var a = GrammarAnalysis.Analyze(g);
			T.Equal("(?i)from", a.Grammar.GetTerminal("fromKeyword")!.Regex, "an implicit terminal, from a literal used in a rule, follows the directive");
			var f = Grammar.Parse("@ignoreCase false;\nS= kw; kw= \"select\"; kw2<ignoreCase>= \"x\";");
			T.Check(!f.IgnoreCase, "@ignoreCase false");
			T.Equal("select", Rx(f, "kw"), "with the directive false, as is");
			T.Equal("(?i)x", Rx(f, "kw2"), "the attribute still turns it on");
		}

		public static void DirectiveRoundTrips()
		{
			var g = Grammar.Parse("@ignoreCase true;\nS= kw; kw<ignoreCase=false>= \"select\";");
			var again = Grammar.Parse(g.ToString());
			T.Check(again.IgnoreCase, "the directive is written back out");
			T.Equal(false, again.GetTerminal("kw")!.IgnoreCase, "and the attribute");
			T.Equal("select", Rx(again, "kw"), "with the same result");
		}

		public static void ExistingFlagsAreRecognized()
		{
			// already case-insensitive from the start: nothing to add
			T.Equal("(?i)abc", Grammar.WithIgnoreCase("(?i)abc"), "(?i)");
			T.Equal("(?si)a.b", Grammar.WithIgnoreCase("(?si)a.b"), "i among other flags");
			T.Equal("(?im)abc", Grammar.WithIgnoreCase("(?im)abc"), "i before m");
			T.Equal("(?is-x)abc", Grammar.WithIgnoreCase("(?is-x)abc"), "i with flags turned off after it");
			T.Equal("(?s)(?i)a.b", Grammar.WithIgnoreCase("(?s)(?i)a.b"), "i in a later leading group");
			T.Equal("(?x) # words\n (?i) a b", Grammar.WithIgnoreCase("(?x) # words\n (?i) a b"), "under (?x), space and comments between the groups");
			T.Equal("(?)(?i)abc", Grammar.WithIgnoreCase("(?)(?i)abc"), "an empty modifier group before it");
			// not case-insensitive for all of it: (?i) goes in front
			T.Equal("(?i)abc", Grammar.WithIgnoreCase("abc"), "no flags");
			T.Equal("(?i)(?s)a.b", Grammar.WithIgnoreCase("(?s)a.b"), "flags without i");
			T.Equal("(?i)(?i:a)bc", Grammar.WithIgnoreCase("(?i:a)bc"), "a scoped (?i:…) covers only its group");
			T.Equal("(?i)(?si:a)bc", Grammar.WithIgnoreCase("(?si:a)bc"), "nor does a scoped one with other flags");
			T.Equal("(?i)(?i)(?-i)abc", Grammar.WithIgnoreCase("(?i)(?-i)abc"), "i turned on and back off");
			T.Equal("(?i)(?-i)abc", Grammar.WithIgnoreCase("(?-i)abc"), "(?-i) turns it off, so the pattern stays case-sensitive");
			T.Equal("(?i)(?s-i)abc", Grammar.WithIgnoreCase("(?s-i)abc"), "i in the off part");
			T.Equal("(?i)a(?i)bc", Grammar.WithIgnoreCase("a(?i)bc"), "(?i) after the start");
			T.Equal("(?i)(?x)  (?i:a)", Grammar.WithIgnoreCase("(?x)  (?i:a)"), "a scoped group after (?x)");
			T.Equal("(?i) (?i)a", Grammar.WithIgnoreCase(" (?i)a"), "space before (?i) without (?x) is text");
			T.Equal("(?i)(?=a)a", Grammar.WithIgnoreCase("(?=a)a"), "a group that isn't modifiers");
			T.Equal("(?i)(?i", Grammar.WithIgnoreCase("(?i"), "an unterminated group");
			T.Equal("(?i)(?--i)a", Grammar.WithIgnoreCase("(?--i)a"), "two dashes is not a modifier group");
			T.Equal("(?i)", Grammar.WithIgnoreCase(""), "empty");
			// through a grammar: a pattern that already ignores case isn't given a second (?i)
			var g = Grammar.Parse("@ignoreCase true;\nS= a b; a= '(?i)abc'; b= '(?s)x.y';");
			T.Equal("(?i)abc", Rx(g, "a"), "no second (?i)");
			T.Equal("(?i)(?s)x.y", Rx(g, "b"), "(?i) ahead of the pattern's own flags");
		}

		public static void InlinedTerminalsKeepTheirOwnCase()
		{
			// the outer terminal ignores case, but letter is case-sensitive
			var g = Grammar.Parse("@ignoreCase true;\nS= Ident; Ident<terminal>= letter { letter | digit | \"_\" }; letter<ignoreCase=false>= '[a-z]'; digit= '[0-9]';");
			T.Equal("(?i)(?-i:[a-z])((?-i:[a-z])|[0-9]|_)*", Rx(g, "Ident"), "a case-sensitive terminal inlined into one that ignores case is scoped with (?-i:…)");
			T.Equal("[a-z]", Rx(g, "letter"), "and keeps its own regex as is");
			T.Equal("(?i)[0-9]", Rx(g, "digit"), "the others follow the directive");
			// the reverse: an insensitive terminal inlined into a sensitive one
			var r = Grammar.Parse("S= Kw; Kw<terminal>= pre \"Name\"; pre<ignoreCase>= \"get\";");
			T.Equal("(?i:get)Name", Rx(r, "Kw"), "an insensitive terminal inlined into a case-sensitive one is scoped with (?i:…)");
			T.Equal("(?i)get", Rx(r, "pre"), "and has (?i) on its own");
			// the same on both sides: inlined as is, under the outer (?i)
			var s = Grammar.Parse("@ignoreCase true;\nS= Kw; Kw<terminal>= pre \"name\"; pre= \"get\";");
			T.Equal("(?i)getname", Rx(s, "Kw"), "no scoping when they agree");
			// and they lex that way
			var a = GrammarAnalysis.Analyze(Grammar.Parse("S= { Kw | other }; Kw<terminal>= pre \"Name\"; pre<ignoreCase>= \"get\"; other= '[A-Za-z]+'; ws<hidden>= ' +';"));
			GrammarException.ThrowIfErrors(a.Messages);
			T.Equal("Kw Kw other", Lex(a, "getName GETName GETNAME"), "only the inlined part ignores case");
		}

		public static void LexesEitherCase()
		{
			// the keyword is declared ahead of id, so it wins the tie, as without ignoreCase
			var a = GrammarAnalysis.Analyze(Grammar.Parse("@ignoreCase true;\nS= { SELECT id }; SELECT= \"SELECT\"; id= '[a-z]+'; ws<hidden>= ' +';"));
			GrammarException.ThrowIfErrors(a.Messages);
			T.Equal("SELECT id SELECT id SELECT id", Lex(a, "select a SELECT B SeLeCt Cd"), "keywords and identifiers in any case");
			var b = GrammarAnalysis.Analyze(Grammar.Parse("S= { SELECT id }; SELECT= \"SELECT\"; id= '[a-z]+'; ws<hidden>= ' +';"));
			T.Equal("id id SELECT #error", Lex(b, "select a SELECT B"), "without it, case matters");
		}

		public static void SensitiveFirstMatchesBoth()
		{
			// the lexer gives a tie to the terminal declared first. With the case-sensitive one first, it takes the
			// exact text and the insensitive one takes every other spelling
			var a = GrammarAnalysis.Analyze(Grammar.Parse("S= { exact | any }; exact= \"Select\"; any<ignoreCase>= \"select\"; ws<hidden>= ' +';"));
			GrammarException.ThrowIfErrors(a.Messages);
			T.Equal("exact any any any", Lex(a, "Select select SELECT sElEcT"), "both are lexed");
			T.Equal(0, At(a.Messages, ErrorLevel.Warning, "never lexed").Count(), "no warning in that order");
			// the other way around, the case-sensitive one is never lexed
			var msgs = Messages("S= { any | exact }; any<ignoreCase>= \"select\"; exact= \"Select\";");
			var w = At(msgs, ErrorLevel.Warning, "\"exact\" is never lexed").ToList();
			T.Equal(1, w.Count, "an insensitive literal ahead of one that differs only in case shadows it");
			T.Check(0 < w.Count && w[0].Message.Contains("Declare \"exact\" before \"any\""), "the warning says how to fix it");
			// two insensitive spellings of the same word: the second is never lexed
			var dup = Messages("@ignoreCase true;\nS= { a | b }; a= \"select\"; b= \"SELECT\";");
			var d = At(dup, ErrorLevel.Warning, "\"b\" is never lexed").ToList();
			T.Equal(1, d.Count, "two insensitive spellings");
			T.Check(0 < d.Count && d[0].Message.Contains("Use \"a\" instead"), "the warning says to use the first");
			// a literal used in a rule makes an implicit terminal, which goes last, after the declared one
			var imp = Messages("@ignoreCase true;\nS= SELECT \"select\"; SELECT= \"SELECT\";");
			T.Equal(1, At(imp, ErrorLevel.Warning, "\"selectKeyword\" is never lexed").Count(), "an implicit terminal shadowed by a declared one");
			// only literals that differ in case: the same literal twice isn't this feature's business
			T.Equal(0, At(Messages("S= { a | b }; a<ignoreCase>= \"+\"; b= \"x\";"), ErrorLevel.Warning, "never lexed").Count(), "no letters, nothing to fold");
			// terminals for another lexer don't compete with the grammar's own
			T.Equal(0, At(Messages("@lexer Other \"OtherLexer\";\nS= { a | b }; a<ignoreCase>= \"select\"; b<lexer=Other, on=\"a\">= \"Select\";"), ErrorLevel.Warning, "never lexed").Count(), "different lexers");
		}

		public static void ErrorsOnNonTerminals()
		{
			var p = Messages("S= A; A<ignoreCase>= \"a\" \"b\";");
			T.Equal(1, At(p, ErrorLevel.Error, "ignoreCase goes on a terminal; this rule is a non-terminal").Count(), "on a non-terminal rule");
			var f = Messages("S= A; A<terminal=false, ignoreCase>= \"a\";");
			T.Equal(1, At(f, ErrorLevel.Error, "this rule is a non-terminal").Count(), "on a rule forced to be a non-terminal");
			var s = Messages("S<ignoreCase>= \"a\";");
			T.Equal(1, At(s, ErrorLevel.Error, "this rule is a non-terminal").Count(), "on the start rule, which is never a terminal by default");
			var grp = Messages("S= ( \"a\" \"b\" )<ignoreCase>;");
			T.Equal(1, At(grp, ErrorLevel.Error, "not on a group in a non-terminal").Count(), "on a group in a rule");
			var lit = Messages("S= \"a\"<ignoreCase> \"b\";");
			T.Equal(1, At(lit, ErrorLevel.Error, "ignoreCase goes on a terminal's declaration").Count(), "on a literal used in a rule");
			var inside = Messages("S= A; A<terminal>= \"a\"<ignoreCase> \"b\";");
			T.Equal(1, At(inside, ErrorLevel.Error, "ignoreCase is inside \"A\"'s definition").Count(), "inside a terminal's definition");
			// marking it <terminal> makes it fine
			T.Equal(0, Messages("S= A; A<terminal, ignoreCase>= \"a\" \"b\";").Count(m => ErrorLevel.Error == m.Level), "on a rule marked <terminal>");
		}

		public static void BadValues()
		{
			var t = Messages("S= a; a<ignoreCase=\"yes\">= \"a\";");
			T.Equal(1, At(t, ErrorLevel.Error, "ignoreCase expects true or false").Count(), "a string value");
			var d = XbnfParser.TryParse("@ignoreCase yes;\nS= \"a\";", null, out _);
			T.Equal(1, At(d, ErrorLevel.Error, "@ignoreCase expects true or false").Count(), "the directive takes true or false");
		}

		public static void NoEffectWithoutARegex()
		{
			var c = Messages("S= { id | tn }; id= '[a-z]+'; tn<when=\"IsTypeName\", ignoreCase>= id;");
			T.Equal(1, At(c, ErrorLevel.Warning, "ignoreCase has no effect, since a contextual terminal").Count(), "on a contextual terminal");
			T.Equal(0, At(c, ErrorLevel.Error, "ignoreCase").Count(), "a warning, not an error");
			var dir = Path.Combine(Path.GetTempPath(), "parsley-ignorecase-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				File.WriteAllText(Path.Combine(dir, "inner.xbnf"), "Obj<shared>= \"{\" \"}\";");
				File.WriteAllText(Path.Combine(dir, "outer.xbnf"), "@external J \"inner.xbnf\";\nS<start>= \"set\" Data;\nData<entry=\"J.Obj\", after=\"setKeyword\", ignoreCase>;\nsetKeyword= \"set\";");
				var read = XbnfParser.TryReadFrom(Path.Combine(dir, "outer.xbnf"), out var g);
				var msgs = read.Concat(GrammarAnalysis.Analyze(g!).Messages).ToList();
				T.Equal(1, At(msgs, ErrorLevel.Warning, "ignoreCase has no effect, since an island").Count(), "on an island");
			}
			finally { Directory.Delete(dir, true); }
		}

		public static void ImportsAndIslands()
		{
			var dir = Path.Combine(Path.GetTempPath(), "parsley-ignorecase-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			try
			{
				// like other directives, an import's @ignoreCase applies when the importer doesn't give one
				File.WriteAllText(Path.Combine(dir, "keywords.xbnf"), "@ignoreCase true;\nkw= \"select\";");
				File.WriteAllText(Path.Combine(dir, "uses.xbnf"), "@import \"keywords.xbnf\";\nS<start>= kw id;\nid= '[a-z]+';");
				var g = Grammar.ReadFrom(Path.Combine(dir, "uses.xbnf"));
				T.Check(g.IgnoreCase, "taken from the import");
				T.Equal("(?i)select", Rx(g, "kw"), "the imported terminal");
				T.Equal("(?i)[a-z]+", Rx(g, "id"), "and the importer's own");
				// and the importer's wins when it gives one
				File.WriteAllText(Path.Combine(dir, "overrides.xbnf"), "@ignoreCase false;\n@import \"keywords.xbnf\";\nS<start>= kw;");
				var o = Grammar.ReadFrom(Path.Combine(dir, "overrides.xbnf"));
				T.Check(!o.IgnoreCase, "the importer's own @ignoreCase wins");
				T.Equal("select", Rx(o, "kw"), "so the imported terminal is case-sensitive");
				// an @external grammar is its own: the host's @ignoreCase doesn't reach it
				File.WriteAllText(Path.Combine(dir, "inner.xbnf"), "Obj<shared>= \"{\" word \"}\";\nword= '[a-z]+';");
				File.WriteAllText(Path.Combine(dir, "outer.xbnf"), "@ignoreCase true;\n@external J \"inner.xbnf\";\nS<start>= \"set\" Data;\nData<entry=\"J.Obj\", after=\"setKeyword\">;\nsetKeyword= \"set\";");
				var outer = Grammar.ReadFrom(Path.Combine(dir, "outer.xbnf"));
				var host = GrammarAnalysis.Analyze(outer);
				GrammarException.ThrowIfErrors(host.Messages);
				T.Equal("(?i)set", host.Grammar.GetTerminal("setKeyword")!.Regex, "the host's terminals ignore case");
				var inner = GrammarAnalysis.Analyze(outer.Externals[0].Grammar!);
				T.Check(!inner.Grammar.IgnoreCase, "the external grammar has no @ignoreCase of its own");
				T.Equal("[a-z]+", inner.Grammar.GetTerminal("word")!.Regex, "so its terminals are case-sensitive");
				T.Equal("\\{", inner.Grammar.GetTerminal("lbrace")!.Regex, "including its implicit ones");
			}
			finally { Directory.Delete(dir, true); }
		}
	}
}
