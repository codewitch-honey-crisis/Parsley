using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// The names of the methods synthesized for a grammar's productions: ParseXxx, EvaluateXxx, TryParseXxx and
	/// PredictXxxN. This is the one place they're decided, so the synthesizer, the source generator, the language
	/// server, a grammar that parses islands of this one, and editors that ask the language server all agree.
	/// </summary>
	/// <remarks>
	/// Each production's methods are named for its name made into an identifier: each run of letters and digits
	/// capitalized, everything else dropped, so foo_bar is FooBar. Names that come out the same, such as foo_bar and
	/// fooBar, are numbered in the order the grammar has them, the first keeping its name. A number never takes a
	/// name another production has as its own, so a production whose name meets no other's is never renamed, and a
	/// name that ends in a digit gets a _ before its number, so A_1 is A1_2 rather than A12. The names depend only on
	/// the productions' names, in order, and not on the analysis.
	/// </remarks>
	public sealed class MethodNames
	{
		public const string ParsePrefix = "Parse";
		public const string EvaluatePrefix = "Evaluate";
		public const string TryParsePrefix = "TryParse";
		public const string PredictPrefix = "Predict";

		readonly Dictionary<string, string> _stems = new Dictionary<string, string>();
		readonly Dictionary<string, string> _rules = new Dictionary<string, string>();

		MethodNames(IEnumerable<string> productions)
		{
			var names = productions.ToList();
			var own = new HashSet<string>(names.Select(Identifier));
			var used = new HashSet<string>();
			foreach (var production in names)
			{
				// a name defined twice is an error the analysis reports; the first is the one that counts
				if (_stems.ContainsKey(production)) continue;
				var baseName = Identifier(production);
				var name = baseName;
				if (!used.Add(name))
				{
					var sep = char.IsDigit(baseName[baseName.Length - 1]) ? "_" : "";
					var n = 2;
					do name = baseName + sep + (n++).ToString();
					while (own.Contains(name) || !used.Add(name));
				}
				_stems.Add(production, name);
				_rules.Add(name, production);
			}
		}

		/// <summary>
		/// The names for a grammar's productions
		/// </summary>
		public static MethodNames For(Grammar grammar) => new MethodNames(grammar.Productions.Select(p => p.Name));
		/// <summary>
		/// The names for productions with these names, in the order the grammar has them
		/// </summary>
		public static MethodNames For(IEnumerable<string> productionNames) => new MethodNames(productionNames);

		/// <summary>
		/// A name made into an identifier: each run of letters and digits capitalized, everything else dropped,
		/// with a _ in front if it would start with a digit, and Symbol if nothing's left
		/// </summary>
		public static string Identifier(string name)
		{
			var s = IdentifierOrEmpty(name);
			return 0 == s.Length ? "Symbol" : s;
		}
		// the same, but empty when nothing's left, for a name with something added after it, such as a class name
		// made from a file name
		internal static string IdentifierOrEmpty(string name)
		{
			var sb = new StringBuilder();
			var upper = true;
			foreach (var ch in name)
			{
				if (!char.IsLetterOrDigit(ch)) { upper = true; continue; }
				if (0 == sb.Length && char.IsDigit(ch)) sb.Append('_');
				sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
				upper = false;
			}
			return sb.ToString();
		}

		/// <summary>
		/// Each production's name, and the name its methods are named for
		/// </summary>
		public IReadOnlyDictionary<string, string> Stems => _stems;
		/// <summary>
		/// The name a production's methods are named for: FooBar for foo_bar
		/// </summary>
		public string Stem(string production) => _stems.TryGetValue(production, out var s) ? s : throw new ArgumentException($"\"{production}\" isn't a production", nameof(production));
		/// <summary>
		/// The production whose methods are named for this, as the Xxx of EvaluateXxx, or null when none is
		/// </summary>
		public string? ProductionFor(string stem) => _rules.TryGetValue(stem, out var p) ? p : null;
		/// <summary>
		/// True when the production's methods aren't named for its own name as an identifier, because an earlier
		/// production's are
		/// </summary>
		public bool IsRenumbered(string production) => Stem(production) != Identifier(production);

		public string Parse(string production) => ParsePrefix + Stem(production);
		public string Evaluate(string production) => EvaluatePrefix + Stem(production);
		/// <summary>
		/// The static method a shared production gets, which a grammar with an island of this one calls
		/// </summary>
		public string TryParse(string production) => TryParsePrefix + Stem(production);
		/// <summary>
		/// The predictor for one of a production's decisions. A's 11th decision and A1's 1st would both be A11, so a
		/// name that ends in a digit gets a _ before the index.
		/// </summary>
		public string Predict(string production, int decision)
		{
			var stem = Stem(production);
			return PredictPrefix + stem + (char.IsDigit(stem[stem.Length - 1]) ? "_" : "") + decision.ToString();
		}
	}
}
