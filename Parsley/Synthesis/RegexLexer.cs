using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Parsley.Runtime;

namespace Parsley
{
	/// <summary>
	/// A simple stand-in lexer built from the terminals' regular expressions, for tests and prototyping.
	/// Longest match wins; on a tie the earlier declaration wins, as in flex. Hidden terminals are dropped.
	/// Token ids count terminals from 0 in declaration order, as a generated lexer's would; unmatched
	/// characters come out with id -1. Uses .NET regex syntax, so patterns written for another engine
	/// may not work here. Not meant for production use.
	/// </summary>
	public sealed class RegexLexer
	{
		readonly List<(int Id, Regex Regex, bool Hidden)> _rules = new();
		readonly int _error;

		public RegexLexer(GrammarAnalysis analysis)
		{
			var s = analysis.Symbols;
			_error = -1;
			foreach (var t in analysis.Grammar.Terminals)
			{
				if (null == t.Regex) continue;
				Regex rx;
				try
				{
					rx = new Regex(@"\G(?:" + t.Regex + ")", RegexOptions.CultureInvariant);
				}
				catch (ArgumentException ex)
				{
					throw new ArgumentException($"Terminal \"{t.Name}\": {t.Regex} isn't a valid .NET regex: {ex.Message}", ex);
				}
				// ids count terminals from 0, as a lexer generator would number them
				_rules.Add((s.GetId(t.Name) - s.NonTerminalCount, rx, t.IsHidden));
			}
		}

		public IEnumerable<Token> Tokenize(string text)
		{
			int pos = 0, line = 1, col = 1;
			while (pos < text.Length)
			{
				int bestLen = 0, bestId = -1;
				bool bestHidden = false;
				foreach (var (id, regex, hidden) in _rules)
				{
					var m = regex.Match(text, pos);
					if (!m.Success || m.Length <= bestLen) continue;
					bestLen = m.Length;
					bestId = id;
					bestHidden = hidden;
				}
				if (0 == bestLen)
				{
					bestLen = 1;
					bestId = _error;
					bestHidden = false;
				}
				var value = text.Substring(pos, bestLen);
				if (!bestHidden)
					yield return new Token(bestId, value, line, col, pos);
				foreach (var ch in value)
				{
					if ('\n' == ch) { ++line; col = 1; }
					else ++col;
				}
				pos += bestLen;
			}
		}
	}
}
