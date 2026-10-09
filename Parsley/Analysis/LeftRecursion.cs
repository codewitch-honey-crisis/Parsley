using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	/// <summary>
	/// Rewrites left recursion as loops. <c>E= E "+" T | T</c> becomes <c>E= T { "+" T }</c> with the
	/// loop marked LeftFold, so the tree still comes out left-associative. Indirect left recursion is
	/// first made direct by inlining the production that leads back.
	/// </summary>
	internal static class LeftRecursion
	{
		internal static List<Expression> Options(Expression e) => e is Alternation a ? a.Options : new List<Expression> { e };
		internal static List<Expression> Items(Expression e) => e switch
		{
			Sequence s => s.Items,
			EmptyExpression => new List<Expression>(),
			_ => new List<Expression> { e }
		};
		internal static Expression MakeSeq(List<Expression> items, GrammarNode at)
		{
			Expression result = 0 == items.Count ? new EmptyExpression() : 1 == items.Count ? items[0] : new Sequence(items);
			if (1 != items.Count) result.CopyLocation(at);
			return result;
		}
		internal static Expression MakeAlt(List<Expression> options, GrammarNode at)
		{
			if (1 == options.Count) return options[0];
			var result = new Alternation(options);
			result.CopyLocation(at);
			return result;
		}

		internal static HashSet<string> ComputeNullable(Grammar g)
		{
			var result = new HashSet<string>();
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in g.Productions)
					if (!result.Contains(p.Name) && IsNullable(p.Expression, result))
					{
						result.Add(p.Name);
						changed = true;
					}
			}
			return result;
		}
		internal static bool IsNullable(Expression e, HashSet<string> nullable) => e switch
		{
			EmptyExpression => true,
			OptionalExpression => true,
			Repeat r => 0 == r.Min || IsNullable(r.Body, nullable),
			Alternation a => a.Options.Any(o => IsNullable(o, nullable)),
			Sequence s => s.Items.All(i => IsNullable(i, nullable)),
			SymbolRef sr => null != sr.Name && nullable.Contains(sr.Name),
			_ => false
		};
		static void _LeftCorners(Expression e, HashSet<string> nullable, HashSet<string> productions, HashSet<string> result)
		{
			switch (e)
			{
				case SymbolRef sr:
					if (null != sr.Name && productions.Contains(sr.Name)) result.Add(sr.Name);
					break;
				case Sequence s:
					foreach (var item in s.Items)
					{
						_LeftCorners(item, nullable, productions, result);
						if (!IsNullable(item, nullable)) break;
					}
					break;
				case Alternation a:
					foreach (var o in a.Options) _LeftCorners(o, nullable, productions, result);
					break;
				case OptionalExpression o:
					_LeftCorners(o.Body, nullable, productions, result);
					break;
				case Repeat r:
					_LeftCorners(r.Body, nullable, productions, result);
					break;
			}
		}

		public static void Eliminate(Grammar g, IList<GrammarMessage> messages)
		{
			var names = new HashSet<string>(g.Productions.Select(p => p.Name));
			for (var guard = 0; guard < 200; ++guard)
			{
				var nullable = ComputeNullable(g);
				var corners = new Dictionary<string, HashSet<string>>();
				foreach (var p in g.Productions)
				{
					var set = new HashSet<string>();
					_LeftCorners(p.Expression, nullable, names, set);
					corners[p.Name] = set;
				}
				bool reaches(string from, string to)
				{
					var seen = new HashSet<string>();
					var stack = new Stack<string>(corners[from]);
					while (0 < stack.Count)
					{
						var n = stack.Pop();
						if (n == to) return true;
						if (seen.Add(n) && corners.TryGetValue(n, out var next))
							foreach (var x in next) stack.Push(x);
					}
					return false;
				}
				var p0 = g.Productions.FirstOrDefault(p => reaches(p.Name, p.Name));
				if (null == p0)
					return;
				var options = Options(p0.Expression);
				// direct: some option starts with p0 itself
				if (options.Any(o => Items(o).FirstOrDefault() is SymbolRef sr && sr.Name == p0.Name))
				{
					if (!_RewriteDirect(p0, messages))
						return;
					continue;
				}
				// indirect: inline the production at the front of an option that leads back to p0
				var inlined = false;
				for (var i = 0; i < options.Count; ++i)
				{
					var items = Items(options[i]);
					if (0 < items.Count && items[0] is SymbolRef q && null != q.Name && q.Name != p0.Name && names.Contains(q.Name) && reaches(q.Name, p0.Name))
					{
						var qp = g.GetProduction(q.Name)!;
						var newOptions = new List<Expression>();
						foreach (var qo in Options(qp.Expression))
						{
							var newItems = Items(qo.Clone()).ToList();
							newItems.AddRange(items.Skip(1).Select(x => x.Clone()));
							newOptions.Add(MakeSeq(newItems, options[i]));
						}
						var all = new List<Expression>(options);
						all.RemoveAt(i);
						all.InsertRange(i, newOptions);
						p0.Expression = MakeAlt(all, p0.Expression);
						messages.Add(new GrammarMessage(ErrorLevel.Warning, $"Inlined \"{q.Name}\" into \"{p0.Name}\" to remove indirect left recursion; \"{q.Name}\" won't get its own node on that path", q));
						inlined = true;
						break;
					}
				}
				if (!inlined)
				{
					messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p0.Name}\" is left recursive through a nullable prefix or a nested group, which can't be rewritten automatically. Move the recursion to the front of a top-level alternative.", p0));
					return;
				}
			}
			messages.Add(new GrammarMessage(ErrorLevel.Error, "Gave up removing left recursion after too many rewrites", 0, 0, -1, g.Filename));
		}

		static bool _RewriteDirect(Production p, IList<GrammarMessage> messages)
		{
			var bases = new List<Expression>();
			var tails = new List<Expression>();
			foreach (var o in Options(p.Expression))
			{
				var items = Items(o);
				if (0 < items.Count && items[0] is SymbolRef sr && sr.Name == p.Name)
				{
					if (1 == items.Count)
					{
						messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" derives itself directly ({p.Name}= {p.Name}), which makes the grammar ambiguous", o));
						return false;
					}
					tails.Add(MakeSeq(items.Skip(1).ToList(), o));
				}
				else
					bases.Add(o);
			}
			if (0 == bases.Count)
			{
				messages.Add(new GrammarMessage(ErrorLevel.Error, $"Every alternative of \"{p.Name}\" starts with \"{p.Name}\", so it can never finish", p));
				return false;
			}
			var loop = new Repeat(MakeAlt(tails, p.Expression), 0) { LeftFold = true };
			loop.CopyLocation(p.Expression);
			var seq = new Sequence(MakeAlt(bases, p.Expression), loop);
			seq.CopyLocation(p.Expression);
			foreach (var a in p.Expression.Attributes)
				seq.Attributes.Add(a.Clone());
			p.Expression = seq;
			messages.Add(new GrammarMessage(ErrorLevel.Message, $"Rewrote left recursion in \"{p.Name}\" as a loop: {seq}", p));
			return true;
		}
	}
}
