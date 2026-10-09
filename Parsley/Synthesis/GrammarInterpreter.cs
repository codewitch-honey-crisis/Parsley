using System;
using System.Collections.Generic;
using System.Linq;
using Parsley.Runtime;

namespace Parsley
{
	/// <summary>
	/// Parses directly from an analyzed grammar, making exactly the choices synthesized code makes.
	/// Useful for prototyping without compiling, and as an oracle for testing synthesized parsers.
	/// </summary>
	public sealed class GrammarInterpreter : ParserBase
	{
		readonly GrammarAnalysis _a;
		readonly SymbolTable _s;

		public GrammarInterpreter(GrammarAnalysis analysis, IEnumerable<Token> tokens) : base(tokens, CreateTables(analysis))
		{
			if (analysis.HasErrors) throw new GrammarException(analysis.Messages);
			_a = analysis;
			_s = analysis.Symbols;
		}

		/// <summary>
		/// The runtime tables for a grammar, the same ones a synthesized parser embeds
		/// </summary>
		public static ParserTables CreateTables(GrammarAnalysis a)
		{
			var s = a.Symbols;
			var recovery = new int[s.NonTerminalCount][];
			for (var i = 0; i < s.NonTerminalCount; ++i)
			{
				var p = s.GetProduction(i)!;
				var set = new SortedSet<int>(a.GetFollowTokens(p.Name));
				void addSync(object? value)
				{
					if (!(value is string str)) return;
					foreach (var part in str.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
					{
						var id = s.GetId(part);
						if (-1 == id)
						{
							var t = a.Grammar.Terminals.FirstOrDefault(x => x.Literal == part);
							if (null != t) id = s.GetId(t.Name);
						}
						if (-1 != id && s.IsTerminal(id)) set.Add(id);
					}
				}
				addSync(p.Attributes.Get("sync"));
				foreach (var e in p.Expression.Descendants())
					addSync(e.Attributes.Get("sync"));
				recovery[i] = set.ToArray();
			}
			return new ParserTables(
				s.NonTerminalCount,
				s.Names.ToArray(),
				Enumerable.Range(0, s.Count).Select(s.Display).ToArray(),
				Enumerable.Range(0, s.Count).Where(s.IsCollapsedTerminal).ToArray(),
				recovery,
				Enumerable.Range(0, s.Count).Where(i => s.GetTerminal(i)?.IsSync ?? false).ToArray(),
				s.EndOfInput,
				s.Error);
		}

		public ParseNode Parse()
		{
			return Run(() => _Production(_a.Grammar.GetProduction(_a.Grammar.StartSymbol!)!, null)!);
		}

		ParseNode? _Production(Production p, ParseNode? parent)
		{
			var id = _s.GetId(p.Name);
			Enter(id);
			ParseNode result;
			if (p.IsCollapsed)
			{
				result = parent!;
				_Expr(p.Expression, ref result, p, true);
				Leave();
				return null;
			}
			result = NewNode(id);
			_Expr(p.Expression, ref result, p, true);
			Leave();
			return result;
		}

		void _Expr(Expression e, ref ParseNode node, Production p, bool isRoot)
		{
			switch (e)
			{
				case EmptyExpression:
					return;
				case SymbolRef sr:
					{
						var id = _s.GetId(sr.Name!);
						if (_s.IsTerminal(id))
							Expect(id, node);
						else
						{
							var callee = _s.GetProduction(id)!;
							var child = _Production(callee, node);
							if (null != child) node.Add(child);
						}
						return;
					}
				case Sequence seq:
					foreach (var item in seq.Items)
						_Expr(item, ref node, p, false);
					return;
			}
			var d = _a.GetDecision(e)!;
			switch (e)
			{
				case Alternation a:
					{
						var b = _Choose(d);
						if (0 <= b)
							_Expr(a.Options[b], ref node, p, false);
						else
						{
							var tokens = d.Branches.SelectMany(x => x).Select(x => x[0]).Distinct().OrderBy(x => x).ToArray();
							var expected = isRoot ? p.Attributes.GetString("expected") : null;
							Error(node, expected ?? d.Node.Attributes.GetString("expected"), tokens);
						}
						return;
					}
				case OptionalExpression o:
					if (0 == _Choose(d))
						_Expr(o.Body, ref node, p, false);
					return;
				case Repeat r:
					{
						var fold = r.LeftFold && !p.IsCollapsed;
						if (1 == r.Min)
						{
							do
							{
								if (fold) node = Fold(node);
								_Expr(r.Body, ref node, p, false);
							} while (0 == _Choose(d));
						}
						else
						{
							while (0 == _Choose(d))
							{
								if (fold) node = Fold(node);
								_Expr(r.Body, ref node, p, false);
							}
						}
						return;
					}
			}
		}

		// the same choice the synthesized code makes, including what happens on input that matches nothing
		int _Choose(Decision d)
		{
			if (1 == d.Depth)
			{
				var t = Current.SymbolId;
				// an inverted loop or optional test also stops at end of input
				if (d.Kind != DecisionKind.Alternation && 0 == d.DefaultBranch && t == _s.EndOfInput)
					return 1;
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						if (x[0] == t) return i;
				return d.DefaultBranch;
			}
			// walk the lookahead one token at a time, as the synthesized trie does
			var prefix = new List<int>();
			for (var level = 0; level < d.Depth; ++level)
			{
				var tok = 0 == level ? Current : Peek(level);
				prefix.Add(tok.SymbolId);
				var matches = new HashSet<int>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						if (x.Count > level && _Prefix(x, prefix)) matches.Add(i);
				if (0 == matches.Count) return d.DefaultBranch;
				if (1 == matches.Count) return matches.First();
			}
			return d.DefaultBranch;
		}
		static bool _Prefix(LookaheadString s, List<int> prefix)
		{
			for (var i = 0; i < prefix.Count; ++i)
				if (s[i] != prefix[i]) return false;
			return true;
		}
	}
}
