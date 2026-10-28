using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley.Tests
{
	/// <summary>
	/// MethodNames, the one place ParseXxx, EvaluateXxx, TryParseXxx and PredictXxxN are named, and that the
	/// synthesized code has the methods it names
	/// </summary>
	public static class MethodNamesTests
	{
		public static void IdentifiersCapitalizeEachRun()
		{
			T.Equal("FooBar", MethodNames.Identifier("foo_bar"), "foo_bar");
			T.Equal("FooBar", MethodNames.Identifier("fooBar"), "fooBar");
			T.Equal("Expr", MethodNames.Identifier("Expr"), "Expr");
			T.Equal("_1a", MethodNames.Identifier("1a"), "a leading digit gets a _");
			T.Equal("Symbol", MethodNames.Identifier("_"), "nothing left");
		}

		public static void NamesThatMeetAreNumberedInOrder()
		{
			var n = MethodNames.For(new[] { "S", "foo_bar", "fooBar", "FOO_BAR", "Expr" });
			T.Equal("FooBar", n.Stem("foo_bar"), "the first keeps its name");
			T.Equal("FooBar2", n.Stem("fooBar"), "the second is numbered");
			T.Equal("FOOBAR", n.Stem("FOO_BAR"), "a name that differs in case doesn't meet");
			T.Equal("Expr", n.Stem("Expr"), "the rest keep theirs");
			T.Check(!n.IsRenumbered("foo_bar") && n.IsRenumbered("fooBar"), "only fooBar is renumbered");
			T.Equal("ParseFooBar2", n.Parse("fooBar"), "Parse");
			T.Equal("EvaluateFooBar2", n.Evaluate("fooBar"), "Evaluate");
			T.Equal("TryParseFooBar2", n.TryParse("fooBar"), "TryParse");
			T.Equal("fooBar", n.ProductionFor("FooBar2"), "back from the name");
			T.Equal(null, n.ProductionFor("FooBar3"), "a name no production has");
		}

		public static void ANumberNeverTakesAnotherProductionsName()
		{
			var n = MethodNames.For(new[] { "foo_bar", "fooBar", "FooBar2" });
			T.Equal("FooBar3", n.Stem("fooBar"), "fooBar skips FooBar2, which is another production's own");
			T.Equal("FooBar2", n.Stem("FooBar2"), "FooBar2 keeps its name");
			var d = MethodNames.For(new[] { "A1", "A_1", "A12" });
			T.Equal("A1_2", d.Stem("A_1"), "a number after a digit is set apart");
			T.Equal("A12", d.Stem("A12"), "A12 keeps its name");
		}

		public static void PredictorsAfterADigitAreSetApart()
		{
			var n = MethodNames.For(new[] { "A", "A1" });
			T.Equal("PredictA11", n.Predict("A", 11), "A's 11th");
			T.Equal("PredictA1_1", n.Predict("A1", 1), "A1's 1st");
		}

		public static void ADuplicateNameCountsOnce()
		{
			// a grammar the analysis will reject still gets names, for the language server
			var n = MethodNames.For(new[] { "foo", "foo", "Foo" });
			T.Equal("Foo", n.Stem("foo"), "the first foo");
			T.Equal("Foo2", n.Stem("Foo"), "Foo after it");
		}

		public static void TheSynthesizedCodeHasTheMethodsItNames()
		{
			const string xbnf =
				"S<start, type=\"int\">= foo_bar | fooBar | A_1 => { return (int)Evaluate(node.Children[0]); };" +
				"foo_bar<shared, type=\"int\">= a [k] ( u v | u w ) => { return 1; };" +
				"fooBar<shared, type=\"int\">= b [k] ( u v | u w ) => { return 2; };" +
				"A_1<shared, type=\"int\">= c [k] ( u v | u w ) => { return 3; };" +
				"a=\"a\"; b=\"b\"; c=\"c\"; k=\"k\"; u=\"u\"; v=\"v\"; w=\"w\"; ws<hidden>= '[ \\t\\r\\n]+';";
			var g = Grammar.Parse(xbnf, "named.xbnf");
			var a = GrammarAnalysis.Analyze(g, ParserEngine.LLk);
			GrammarException.ThrowIfErrors(a.Messages);
			var src = CSharpSynthesizer.Synthesize(a);
			var n = MethodNames.For(a.Grammar);
			foreach (var p in a.Grammar.Productions)
			{
				T.Check(src.Contains(" " + n.Parse(p.Name) + "("), p.Name + "'s " + n.Parse(p.Name) + " is there");
				T.Check(src.Contains(" " + n.Evaluate(p.Name) + "("), p.Name + "'s " + n.Evaluate(p.Name) + " is there");
			}
			foreach (var p in a.Grammar.SharedProductions)
				T.Check(src.Contains(" " + n.TryParse(p.Name) + "("), p.Name + "'s " + n.TryParse(p.Name) + " is there");
			T.Check(a.Decisions.Any(d => src.Contains("int " + n.Predict(d.Production.Name, d.Index) + "()")), "the predictors are named by it");
			T.Equal(MethodNames.For(g).Stems.Count, n.Stems.Count, "the analysis doesn't change the productions the names come from");
			CompiledParser.Compile(src, g.ClassName ?? "SParser");
		}
	}
}
