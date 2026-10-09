using System;
using System.IO;
using System.Linq;
using static Parsley.Ebnf;

namespace Parsley.Tests
{
	public static class GrammarTests
	{
		public static void ReadsJsonXbnf()
		{
			var g = Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf"));
			T.Equal("Json", g.StartSymbol, "start symbol");
			T.Equal(6, g.Productions.Count, "production count");
			T.Check(g.GetTerminal("number")?.Pattern?.StartsWith("\\-?") == true, "number is a pattern terminal");
			T.Check(g.GetTerminal("whitespace")!.IsHidden, "whitespace hidden");
			T.Check(g.GetTerminal("lbrace")!.IsCollapsed, "lbrace collapsed");
			T.Check(g.GetProduction("Value")!.IsCollapsed, "Value collapsed");
			var obj = g.GetProduction("Object")!;
			T.Equal("\"{\" [ Field { \",\" Field } ] \"}\"", obj.Expression.ToString(), "Object expression round trips");
			T.Equal("JsonParser", g.ClassName, "class name from file");
		}
		public static void ResolvesLiteralsToDeclaredTerminals()
		{
			var g = Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf"));
			g.ResolveLiterals();
			var names = g.GetProduction("Object")!.Expression.Descendants().OfType<SymbolRef>().Select(s => s.Name).ToList();
			T.Equal("lbrace,Field,comma,Field,rbrace", string.Join(",", names), "literals resolve to declared terminals");
			T.Check(!g.Terminals.Any(t => t.IsImplicit), "no implicit terminals for json");
			T.Equal(0, g.Validate().Count(m => m.Level != ErrorLevel.Message), "json validates cleanly");
		}
		public static void ImplicitTerminalsGetReadableNames()
		{
			var g = Grammar.Parse("S= \"if\" \"(\" x \")\" \"==\" ; x= 'x';");
			g.ResolveLiterals();
			var names = g.Terminals.Where(t => t.IsImplicit).Select(t => t.Name).ToList();
			T.Equal("ifKeyword,lparen,rparen,eq_eq", string.Join(",", names), "implicit names");
		}
		public static void AttributedGroupsAndDirectives()
		{
			var g = Grammar.Parse(@"
				@namespace ""My.Parsers"";
				@class ""Thing"";
				// a comment
				S<start, expected=""a thing"">= ( a b )<policy=first> | { c }+<sync=""semi""> | [ a ]<policy=lazy>; /* block */
				a= ""a""; b= ""b""; c= ""c""; semi<sync>= "";"";");
			T.Equal("My.Parsers", g.Namespace, "namespace directive");
			T.Equal("Thing", g.ClassName, "class directive");
			var alt = (Alternation)g.GetProduction("S")!.Expression;
			T.Equal("first", alt.Options[0].Attributes.GetString("policy"), "group attribute");
			T.Equal(1, ((Repeat)alt.Options[1]).Min, "one-or-more");
			T.Equal("semi", alt.Options[1].Attributes.GetString("sync"), "loop attribute");
			T.Equal("lazy", alt.Options[2].Attributes.GetString("policy"), "optional attribute");
			T.Equal("a thing", g.GetProduction("S")!.Attributes.GetString("expected"), "production attribute");
			T.Check(g.GetTerminal("semi")!.IsSync, "sync terminal");
			T.Equal("S<start, expected=\"a thing\">= ( a b )<policy=\"first\"> | { c }+<sync=\"semi\"> | [ a ]<policy=\"lazy\">;", g.GetProduction("S")!.ToString(), "round trip");
		}
		public static void FalseIsFalse()
		{
			var g = Grammar.Parse("S<collapsed=false>= a; a= \"a\";");
			T.Check(g.GetProduction("S")!.Attributes.Get("collapsed") is false, "false parses as false");
		}
		public static void SyntaxErrorsAreReportedAndReadingContinues()
		{
			var msgs = XbnfReader.TryParse("S= a b\nT= ( a ;\nU= a; a= \"a\"; b=\"b\";", "x.xbnf", out var g);
			var errors = msgs.Where(m => m.Level == ErrorLevel.Error).ToList();
			T.Check(1 <= errors.Count, "error reported");
			T.Equal(2, errors[0].Line, "error line");
			T.Check(null != g!.GetProduction("U"), "reading continued after the error");
		}
		public static void VirtualProductionsAreRejected()
		{
			var msgs = XbnfReader.TryParse("S<start,virtual> { return x; }", null, out _);
			T.Check(msgs.Any(m => m.Level == ErrorLevel.Error && m.Message.Contains("not supported")), "virtual rejected");
		}
		public static void Imports()
		{
			var dir = Path.Combine(Path.GetTempPath(), "parsley-import-test");
			Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "sub.xbnf"), "@import \"main.xbnf\";\nA= \"a\" B;\nB= \"b\";");
            File.WriteAllText(Path.Combine(dir, "main.xbnf"), "@import \"sub.xbnf\";\nS= A x;\nx= \"x\";");
			var g = Grammar.ReadFrom(Path.Combine(dir, "main.xbnf"));
			T.Check(null != g.GetProduction("A"), "imported production present");
			T.Check(g.GetProduction("A")!.Filename!.EndsWith("sub.xbnf"), "imported production keeps its file name");
			// imported productions come first; the importer's own start wins only if marked, so the first production is A
			T.Equal("A", g.StartSymbol, "first production is the start without a start attribute");
		}
		public static void BuilderApi()
		{
			var g = new Grammar();
			g.Terminal("lbrace", "{"); g.Terminal("rbrace", "}"); g.Terminal("comma", ",");
			g.Terminal("str", "s");
			g.Production("Object", Seq("lbrace", Opt("Field", Rep("comma", "Field")), "rbrace"));
			g.Production("Field", Sym("str"));
			T.Equal("Object= lbrace [ Field { comma Field } ] rbrace;", g.Productions[0].ToString(), "builder output");
			T.Equal(0, g.Validate().Count, "builder grammar validates");
		}
		public static void ValidationCatchesProblems()
		{
			var g = Grammar.Parse("S<collapsed>= a Undefined ws; a= \"a\"; ws<hidden>= ' '; Orphan= a;");
			g.ResolveLiterals();
			var msgs = g.Validate();
			T.Check(msgs.Any(m => m.Message.Contains("start production cannot be collapsed")), "collapsed start");
			T.Check(msgs.Any(m => m.Message.Contains("\"Undefined\" is not defined")), "undefined symbol");
			T.Check(msgs.Any(m => m.Message.Contains("hidden")), "hidden terminal used");
			T.Check(msgs.Any(m => m.Level == ErrorLevel.Warning && m.Message.Contains("Orphan")), "unreachable");
		}
	}
}
