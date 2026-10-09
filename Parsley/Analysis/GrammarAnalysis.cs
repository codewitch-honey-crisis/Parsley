using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	public enum DecisionKind
	{
		/// <summary>
		/// <c>a | b | c</c>: branch i is option i
		/// </summary>
		Alternation,
		/// <summary>
		/// <c>[ a ]</c>: branch 0 takes it, branch 1 skips it
		/// </summary>
		Optional,
		/// <summary>
		/// <c>{ a }</c>: branch 0 goes around again, branch 1 exits
		/// </summary>
		Loop
	}

	/// <summary>
	/// One choice point and how to make it: the lookahead depth, which lookahead strings pick which
	/// branch, and what to do with input that matches none of them.
	/// </summary>
	public sealed class Decision
	{
		internal Decision(Production production, Expression node, DecisionKind kind, int index)
		{
			Production = production;
			Node = node;
			Kind = kind;
			Index = index;
		}
		public Production Production { get; }
		public Expression Node { get; }
		public DecisionKind Kind { get; }
		/// <summary>
		/// The decision's position among its production's decisions, in source order, from 1
		/// </summary>
		public int Index { get; }
		/// <summary>
		/// How many tokens it needs to look at
		/// </summary>
		public int Depth { get; internal set; }
		/// <summary>
		/// For each branch, the lookahead strings (at most Depth long) that choose it. The sets don't overlap.
		/// </summary>
		public IReadOnlyList<IReadOnlyCollection<LookaheadString>> Branches { get; internal set; } = Array.Empty<IReadOnlyCollection<LookaheadString>>();
		/// <summary>
		/// The branch taken when the input matches none of the strings, or -1 to report an error.
		/// For loops and optionals this decides whether code tests the continue set or the inverse of the exit set.
		/// </summary>
		public int DefaultBranch { get; internal set; } = -1;
		/// <summary>
		/// The policy used to settle input that matched more than one branch, if any was needed
		/// </summary>
		public string? AppliedPolicy { get; internal set; }
		/// <summary>
		/// Lookahead strings that matched more than one branch before the policy settled them
		/// </summary>
		public IReadOnlyList<LookaheadString> Ambiguities { get; internal set; } = Array.Empty<LookaheadString>();
		/// <summary>
		/// The single tokens that start each branch, when Depth is 1
		/// </summary>
		public IReadOnlyList<int> TokensOf(int branch) => Branches[branch].Select(s => s[0]).Distinct().OrderBy(x => x).ToList();
		public override string ToString() => $"{Production.Name} #{Index} {Kind} depth {Depth}: {Node}";
	}

	/// <summary>
	/// Analyzes a grammar for synthesis: removes left recursion, computes FIRST and FOLLOW for
	/// every expression node, and classifies every decision by the lookahead it needs.
	/// </summary>
	public sealed class GrammarAnalysis
	{
		sealed class KSets
		{
			public int K;
			public Dictionary<string, HashSet<LookaheadString>> ProductionFirst = new();
			public Dictionary<Expression, HashSet<LookaheadString>> ExprFirst = new(ReferenceEqualityComparer.Instance);
			public Dictionary<string, HashSet<LookaheadString>> ProductionFollow = new();
			public Dictionary<Expression, HashSet<LookaheadString>> NodeFollow = new(ReferenceEqualityComparer.Instance);
			public bool Converged;
		}

		readonly Dictionary<int, KSets> _sets = new();
		readonly Dictionary<Expression, Decision> _decisions = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Expression, Expression?> _parents = new(ReferenceEqualityComparer.Instance);
		readonly Dictionary<Expression, Production> _owners = new(ReferenceEqualityComparer.Instance);
		readonly List<GrammarMessage> _messages = new();
		HashSet<string> _nullable = new();

		GrammarAnalysis(Grammar g, int maxK)
		{
			Grammar = g;
			MaxK = maxK;
		}

		/// <summary>
		/// The grammar as analyzed: literals resolved and left recursion rewritten. The input grammar is not changed.
		/// </summary>
		public Grammar Grammar { get; }
		public int MaxK { get; }
		public SymbolTable Symbols { get; private set; } = null!;
		public IReadOnlyList<GrammarMessage> Messages => _messages;
		public bool HasErrors => _messages.Any(m => m.Level == ErrorLevel.Error);
		public IReadOnlyList<Decision> Decisions => _decisions.Values.OrderBy(d => Grammar.Productions.IndexOf(d.Production)).ThenBy(d => d.Index).ToList();
		public Decision? GetDecision(Expression node) => _decisions.TryGetValue(node, out var d) ? d : null;
		public Expression? GetParent(Expression node) => _parents.TryGetValue(node, out var p) ? p : null;
		public Production GetOwner(Expression node) => _owners[node];
		public bool IsNullable(Expression e) => LeftRecursion.IsNullable(e, _nullable);
		/// <summary>
		/// The deepest lookahead any decision needs
		/// </summary>
		public int Depth => 0 == _decisions.Count ? 1 : _decisions.Values.Max(d => d.Depth);

		/// <summary>
		/// Analyzes the grammar. Problems are reported in Messages rather than thrown.
		/// </summary>
		public static GrammarAnalysis Analyze(Grammar grammar, int maxK = 4)
		{
			if (1 > maxK) throw new ArgumentOutOfRangeException(nameof(maxK));
			var g = grammar.Clone();
			g.ResolveLiterals();
			var a = new GrammarAnalysis(g, maxK);
			a._Run();
			return a;
		}

		void _Run()
		{
			_messages.AddRange(Grammar.Validate());
			if (HasErrors) return;
			LeftRecursion.Eliminate(Grammar, _messages);
			if (HasErrors) return;
			Symbols = new SymbolTable(Grammar);
			_nullable = LeftRecursion.ComputeNullable(Grammar);
			foreach (var p in Grammar.Productions)
				_Index(p, p.Expression, null);
			foreach (var p in Grammar.Productions)
			{
				var index = 0;
				_CollectDecisions(p, p.Expression, ref index);
			}
			foreach (var d in _decisions.Values.OrderBy(d => Grammar.Productions.IndexOf(d.Production)).ThenBy(d => d.Index))
				_Resolve(d);
		}

		void _Index(Production p, Expression e, Expression? parent)
		{
			_parents[e] = parent;
			_owners[e] = p;
			foreach (var c in e.Children)
				_Index(p, c, e);
		}

		void _CollectDecisions(Production p, Expression e, ref int index)
		{
			switch (e)
			{
				case Alternation:
					_decisions[e] = new Decision(p, e, DecisionKind.Alternation, ++index);
					break;
				case OptionalExpression:
					_decisions[e] = new Decision(p, e, DecisionKind.Optional, ++index);
					break;
				case Repeat r:
					_decisions[e] = new Decision(p, e, DecisionKind.Loop, ++index);
					if (IsNullable(r.Body))
						_messages.Add(new GrammarMessage(ErrorLevel.Error, $"The loop body {r.Body} can match nothing, so the loop could repeat forever", r));
					break;
			}
			foreach (var c in e.Children)
				_CollectDecisions(p, c, ref index);
		}

		#region FIRST and FOLLOW
		KSets _Sets(int k)
		{
			if (_sets.TryGetValue(k, out var s) && s.Converged) return s;
			s = new KSets { K = k };
			_sets[k] = s;
			var alg = new LookaheadAlgebra(k, Symbols.EndOfInput);
			foreach (var p in Grammar.Productions)
				s.ProductionFirst[p.Name] = new HashSet<LookaheadString>();
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in Grammar.Productions)
				{
					var f = _First(p.Expression, s, alg, false);
					var target = s.ProductionFirst[p.Name];
					foreach (var x in f)
						if (target.Add(x)) changed = true;
				}
			}
			// follows
			foreach (var p in Grammar.Productions)
				s.ProductionFollow[p.Name] = new HashSet<LookaheadString>();
			s.ProductionFollow[Grammar.StartSymbol!].Add(new LookaheadString(Symbols.EndOfInput));
			changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in Grammar.Productions)
					if (_WalkFollow(p.Expression, s.ProductionFollow[p.Name], s, alg))
						changed = true;
			}
			s.Converged = true;
			return s;
		}

		HashSet<LookaheadString> _First(Expression e, KSets s, LookaheadAlgebra alg, bool memo)
		{
			if (memo && s.ExprFirst.TryGetValue(e, out var cached)) return cached;
			HashSet<LookaheadString> result;
			switch (e)
			{
				case EmptyExpression:
					result = LookaheadAlgebra.EmptyOnly();
					break;
				case SymbolRef sr:
					if (s.ProductionFirst.TryGetValue(sr.Name!, out var pf))
						result = new HashSet<LookaheadString>(pf);
					else
						result = new HashSet<LookaheadString> { new LookaheadString(Symbols.GetId(sr.Name!)) };
					break;
				case Sequence seq:
					result = LookaheadAlgebra.EmptyOnly();
					foreach (var item in seq.Items)
					{
						result = alg.Concat(result, _First(item, s, alg, memo));
						if (0 == result.Count || alg.AllComplete(result)) break;
					}
					break;
				case Alternation a:
					result = new HashSet<LookaheadString>();
					foreach (var o in a.Options)
						result.UnionWith(_First(o, s, alg, memo));
					break;
				case OptionalExpression o:
					result = new HashSet<LookaheadString>(_First(o.Body, s, alg, memo)) { LookaheadString.Empty };
					break;
				case Repeat r:
					{
						var body = _First(r.Body, s, alg, memo);
						// star = {ε} ∪ body·star, to a fixed point
						var star = LookaheadAlgebra.EmptyOnly();
						while (true)
						{
							var next = alg.Concat(body, star);
							var before = star.Count;
							star.UnionWith(next);
							if (star.Count == before) break;
						}
						result = 1 == r.Min ? alg.Concat(body, star) : star;
						break;
					}
				default:
					throw new NotSupportedException(e.GetType().Name);
			}
			if (memo) s.ExprFirst[e] = result;
			return result;
		}

		// walks an expression with the set of strings that can follow it; returns true if any production follow grew
		bool _WalkFollow(Expression e, HashSet<LookaheadString> follow, KSets s, LookaheadAlgebra alg)
		{
			s.NodeFollow[e] = follow;
			var changed = false;
			switch (e)
			{
				case SymbolRef sr:
					if (s.ProductionFollow.TryGetValue(sr.Name!, out var pf))
						foreach (var x in follow)
							if (pf.Add(x)) changed = true;
					break;
				case Sequence seq:
					{
						var f = follow;
						for (var i = seq.Items.Count - 1; i >= 0; --i)
						{
							if (_WalkFollow(seq.Items[i], f, s, alg)) changed = true;
							f = alg.Concat(_First(seq.Items[i], s, alg, true), f);
						}
						break;
					}
				case Alternation a:
					foreach (var o in a.Options)
						if (_WalkFollow(o, follow, s, alg)) changed = true;
					break;
				case OptionalExpression o:
					if (_WalkFollow(o.Body, follow, s, alg)) changed = true;
					break;
				case Repeat r:
					{
						// after one pass of the body comes more passes or the exit
						var again = alg.Concat(_First(new Repeat(r.Body, 0), s, alg, false), follow);
						if (_WalkFollow(r.Body, again, s, alg)) changed = true;
						break;
					}
			}
			return changed;
		}

		/// <summary>
		/// FIRST_k of an expression, with ε as the empty string
		/// </summary>
		public IReadOnlyCollection<LookaheadString> GetFirst(Expression e, int k = 1)
		{
			var s = _Sets(k);
			return _First(e, s, new LookaheadAlgebra(k, Symbols.EndOfInput), true);
		}
		/// <summary>
		/// What can follow an expression node, k tokens deep
		/// </summary>
		public IReadOnlyCollection<LookaheadString> GetFollow(Expression e, int k = 1) => _Sets(k).NodeFollow[e];
		public IReadOnlyCollection<LookaheadString> GetProductionFollow(string production, int k = 1) => _Sets(k).ProductionFollow[production];
		public IReadOnlyCollection<LookaheadString> GetProductionFirst(string production, int k = 1) => _Sets(k).ProductionFirst[production];
		/// <summary>
		/// The terminals that can start an expression (ε excluded)
		/// </summary>
		public ISet<int> GetFirstTokens(Expression e) => new HashSet<int>(GetFirst(e, 1).Where(x => 0 < x.Count).Select(x => x[0]));
		public ISet<int> GetFollowTokens(string production) => new HashSet<int>(GetProductionFollow(production, 1).Select(x => x[0]));
		#endregion

		#region Decisions
		List<HashSet<LookaheadString>> _BranchSets(Decision d, int k)
		{
			var s = _Sets(k);
			var alg = new LookaheadAlgebra(k, Symbols.EndOfInput);
			var follow = s.NodeFollow[d.Node];
			var result = new List<HashSet<LookaheadString>>();
			switch (d.Node)
			{
				case Alternation a:
					foreach (var o in a.Options)
						result.Add(alg.Concat(_First(o, s, alg, true), follow));
					break;
				case OptionalExpression o:
					result.Add(alg.Concat(_First(o.Body, s, alg, true), follow));
					result.Add(new HashSet<LookaheadString>(follow));
					break;
				case Repeat r:
					result.Add(alg.Concat(_First(r.Body, s, alg, true), s.NodeFollow[r.Body]));
					result.Add(new HashSet<LookaheadString>(follow));
					break;
			}
			return result;
		}

		static List<LookaheadString> _Conflicts(List<HashSet<LookaheadString>> branches)
		{
			var owner = new Dictionary<LookaheadString, int>();
			var result = new List<LookaheadString>();
			for (var i = 0; i < branches.Count; ++i)
				foreach (var x in branches[i])
				{
					if (owner.TryGetValue(x, out var j) && j != i)
					{
						if (!result.Contains(x)) result.Add(x);
					}
					else
						owner[x] = i;
				}
			return result;
		}

		string? _Policy(Decision d)
		{
			// the decision's own attribute wins, then its production's
			return d.Node.Attributes.GetString("policy") ?? d.Production.Attributes.GetString("policy");
		}

		void _Resolve(Decision d)
		{
			List<HashSet<LookaheadString>> branches = null!;
			for (var k = 1; k <= MaxK; ++k)
			{
				branches = _BranchSets(d, k);
				if (0 == _Conflicts(branches).Count)
				{
					d.Depth = k;
					d.Branches = branches;
					_ChooseDefault(d);
					return;
				}
			}
			// still conflicting at MaxK: settle with a policy
			var conflicts = _Conflicts(branches);
			var policy = _Policy(d);
			var explicitPolicy = null != policy;
			if (null == policy)
				policy = d.Kind == DecisionKind.Alternation ? "first" : "greedy";
			var conflictText = string.Join(", ", conflicts.Take(4).Select(c => $"[{c.ToString(Symbols)}]")) + (4 < conflicts.Count ? $" and {conflicts.Count - 4} more" : "");
			if (d.Kind == DecisionKind.Alternation && !explicitPolicy)
				_messages.Add(new GrammarMessage(ErrorLevel.Error, $"In \"{d.Production.Name}\", the alternatives of {d.Node} can't be told apart within {MaxK} tokens on {conflictText}. Add <policy=\"first\"> if the earliest alternative should win.", d.Node));
			else if (!explicitPolicy)
				_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"In \"{d.Production.Name}\", {d.Node} is ambiguous on {conflictText}; resolved greedily (it keeps matching). Add <policy=\"greedy\"> or <policy=\"lazy\"> to choose explicitly.", d.Node));
			else if (d.Kind == DecisionKind.Alternation && "first" != policy)
				_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"policy=\"{policy}\" on an alternation acts like \"first\"", d.Node));

			// assign each conflicting string to one branch
			var assigned = new List<HashSet<LookaheadString>>();
			for (var i = 0; i < branches.Count; ++i) assigned.Add(new HashSet<LookaheadString>());
			var conflictSet = new HashSet<LookaheadString>(conflicts);
			for (var i = 0; i < branches.Count; ++i)
				foreach (var x in branches[i])
				{
					if (!conflictSet.Contains(x))
						assigned[i].Add(x);
				}
			foreach (var x in conflicts)
			{
				var candidates = Enumerable.Range(0, branches.Count).Where(i => branches[i].Contains(x)).ToList();
				int winner;
				if (d.Kind == DecisionKind.Alternation) winner = candidates.Min();
				else winner = "lazy" == policy ? 1 : 0;
				if (!candidates.Contains(winner)) winner = candidates.Min();
				assigned[winner].Add(x);
			}
			// the shallowest depth at which every prefix still picks one branch
			for (var depth = 1; depth <= MaxK; ++depth)
			{
				var owner = new Dictionary<LookaheadString, int>();
				var ok = true;
				for (var i = 0; i < assigned.Count && ok; ++i)
					foreach (var x in assigned[i])
					{
						var t = x.Truncate(depth);
						if (owner.TryGetValue(t, out var j) && j != i) { ok = false; break; }
						owner[t] = i;
					}
				if (ok)
				{
					d.Depth = depth;
					d.Branches = assigned.Select(set => (IReadOnlyCollection<LookaheadString>)new HashSet<LookaheadString>(set.Select(x => x.Truncate(depth)))).ToList();
					break;
				}
			}
			d.AppliedPolicy = policy;
			d.Ambiguities = conflicts;
			_ChooseDefault(d);
		}

		// picks what unmatched input does, which also decides the shape of the emitted test
		void _ChooseDefault(Decision d)
		{
			switch (d.Kind)
			{
				case DecisionKind.Alternation:
					{
						var a = (Alternation)d.Node;
						var nullable = Enumerable.Range(0, a.Options.Count).Where(i => IsNullable(a.Options[i])).ToList();
						if (1 == nullable.Count)
							d.DefaultBranch = nullable[0];
						else if (2 == a.Options.Count && 1 == d.Depth)
						{
							// if/else: test the branch with fewer tokens, the other is the else
							d.DefaultBranch = d.TokensOf(0).Count <= d.TokensOf(1).Count ? 1 : 0;
						}
						else
							d.DefaultBranch = -1;
						break;
					}
				default:
					if (1 != d.Depth)
					{
						d.DefaultBranch = 1;
						break;
					}
					var c = d.TokensOf(0).Count;
					var x = d.TokensOf(1).Union(new[] { Symbols.EndOfInput }).Count();
					// test the inverse of the exit set only when it is strictly smaller; ties test the continue set,
					// which reads more naturally and doesn't depend on what follows the construct
					d.DefaultBranch = x < c ? 0 : 1;
					break;
			}
		}

		#endregion
	}
}
