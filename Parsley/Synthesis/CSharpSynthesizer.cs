using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	public sealed class SynthesisOptions
	{
		/// <summary>
		/// The namespace, overriding the grammar's @namespace. Null leaves the class in the global namespace.
		/// </summary>
		public string? Namespace { get; set; }
		/// <summary>
		/// The class name, overriding the grammar's @class and the default (file name + "Parser")
		/// </summary>
		public string? ClassName { get; set; }
		/// <summary>
		/// Whether the class is public (else internal)
		/// </summary>
		public bool Public { get; set; } = true;
	}

	/// <summary>
	/// Turns an analyzed grammar into C# recursive descent that reads as if written by hand:
	/// one method per production, each decision written with the cheapest test that decides it,
	/// and a comment quoting the grammar above each piece.
	/// </summary>
	public static class CSharpSynthesizer
	{
		/// <summary>
		/// The runtime support file (Token, LookAheadEnumerator, ParseNode, ParserBase) that every
		/// synthesized parser needs alongside it
		/// </summary>
		public static string RuntimeSource
		{
			get
			{
				using var s = typeof(CSharpSynthesizer).Assembly.GetManifestResourceStream("Parsley.Runtime.ParsleyRuntime.cs")
					?? throw new InvalidOperationException("The runtime source is missing from the assembly");
				using var r = new StreamReader(s);
				return r.ReadToEnd();
			}
		}

		/// <summary>
		/// Analyzes and synthesizes in one step. Throws if the grammar has errors.
		/// </summary>
		public static string Synthesize(Grammar grammar, SynthesisOptions? options = null)
		{
			var a = GrammarAnalysis.Analyze(grammar);
			GrammarException.ThrowIfErrors(a.Messages);
			return Synthesize(a, options);
		}

		public static string Synthesize(GrammarAnalysis analysis, SynthesisOptions? options = null)
		{
			if (analysis.HasErrors)
				throw new GrammarException(analysis.Messages);
			return new _Emitter(analysis, options ?? new SynthesisOptions()).Emit();
		}

		sealed class _Emitter
		{
			readonly GrammarAnalysis _a;
			readonly SymbolTable _s;
			readonly SynthesisOptions _o;
			readonly string _className;
			readonly string[] _const;
			StringBuilder _sb = new();
			int _indent;
			readonly Dictionary<string, string> _sets = new(); // key: sorted ids
			readonly List<(string Name, int[] Ids, string Comment)> _setList = new();
			readonly List<Decision> _predictors = new();
			bool _nameConsumes; // inside a multi-token decision, where no case label shows the token

			public _Emitter(GrammarAnalysis a, SynthesisOptions o)
			{
				_a = a;
				_s = a.Symbols;
				_o = o;
				_className = _Ident(o.ClassName ?? a.Grammar.ClassName ?? (a.Grammar.StartSymbol + "Parser"));
				_const = _MakeConstantNames();
			}

			#region Naming
			static readonly HashSet<string> _keywords = new HashSet<string>(
				("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double " +
				"else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is " +
				"lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
				"sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe " +
				"ushort using virtual void volatile while").Split(' '));
			static string _Ident(string name)
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
				var s = 0 == sb.Length ? "Symbol" : sb.ToString();
				return _keywords.Contains(s) ? "@" + s : s;
			}
			string[] _MakeConstantNames()
			{
				var reserved = new HashSet<string>
				{
					_className, "Parse", "Current", "Errors", "Peek", "Advance", "Consume", "Expect", "Error", "NewNode", "Fold",
					"Enter", "Leave", "ExpectEnd", "Run", "Tables", "SymbolCount", "GetHashCode", "ToString", "Equals", "GetType"
				};
				foreach (var p in _a.Grammar.Productions) reserved.Add("Parse" + _Ident(p.Name));
				var used = new HashSet<string>(reserved);
				var result = new string[_s.Count];
				for (var i = 0; i < _s.Count; ++i)
				{
					string baseName = i == _s.EndOfInput ? "EndOfInput" : i == _s.Error ? "ErrorSymbol" : _Ident(_s.GetName(i));
					var name = baseName;
					var n = 2;
					while (!used.Add(name)) name = baseName + (n++).ToString();
					result[i] = name;
				}
				return result;
			}
			string _MethodName(string production) => "Parse" + _Ident(production);
			string _PredictName(Decision d) => "Predict" + _Ident(d.Production.Name) + d.Index.ToString();
			#endregion

			#region Writing
			void _Line(string text = "")
			{
				if (0 < text.Length) _sb.Append('\t', _indent);
				_sb.AppendLine(text);
			}
			void _Open(string text)
			{
				_Line(text);
				_Line("{");
				++_indent;
			}
			void _Close(string text = "}")
			{
				--_indent;
				_Line(text);
			}
			string _Capture(Action write)
			{
				var saved = _sb;
				_sb = new StringBuilder();
				write();
				var result = _sb.ToString();
				_sb = saved;
				return result;
			}
			static string _Comment(string text) => text.Replace("\r", "").Replace("\n", " ");
			#endregion

			#region Tests on the current token
			string _SetName(ICollection<int> ids)
			{
				var sorted = ids.OrderBy(x => x).ToArray();
				var key = string.Join(",", sorted);
				if (_sets.TryGetValue(key, out var name)) return name;
				// name it after a production when it is exactly what that production starts with
				string? baseName = null;
				foreach (var p in _a.Grammar.Productions)
				{
					var first = _a.GetFirst(p.Expression, 1);
					if (first.Any(x => 0 == x.Count)) continue;
					if (first.Select(x => x[0]).Distinct().OrderBy(x => x).SequenceEqual(sorted))
					{
						baseName = "s_starts" + _Ident(p.Name);
						break;
					}
				}
				baseName ??= "s_set";
				name = baseName;
				var n = 2;
				while (_sets.ContainsValue(name) || "s_set" == name) name = baseName + (n++).ToString();
				_sets.Add(key, name);
				_setList.Add((name, sorted, string.Join(", ", sorted.Select(_s.Display))));
				return name;
			}
			string _Test(ICollection<int> ids, bool negate, string token = "Current")
			{
				var sorted = ids.OrderBy(x => x).ToList();
				if (1 == sorted.Count)
					return $"{token}.SymbolId {(negate ? "!=" : "==")} {_const[sorted[0]]}";
				if (4 >= sorted.Count)
				{
					var or = string.Join(" or ", sorted.Select(i => _const[i]));
					return negate ? $"{token}.SymbolId is not ({or})" : $"{token}.SymbolId is {or}";
				}
				var set = _SetName(sorted);
				return $"{(negate ? "!" : "")}{set}[{token}.SymbolId]";
			}
			#endregion

			public string Emit()
			{
				var g = _a.Grammar;
				var source = null != g.Filename ? Path.GetFileName(g.Filename) : "an in-memory grammar";
				_Line("// <auto-generated>");
				_Line($"// Synthesized by Parsley from {source}. Changes are lost when it is synthesized again;");
				_Line("// add members in another part of this partial class instead.");
				_Line("// </auto-generated>");
				_Line("#nullable enable");
				_Line("using System.Collections.Generic;");
				_Line("using Parsley.Runtime;");
				_Line();
				var ns = _o.Namespace ?? g.Namespace;
				if (null != ns)
				{
					_Line($"namespace {ns};");
					_Line();
				}
				_Line("/// <summary>");
				_Line($"/// Parses {g.StartSymbol}. Synthesized from {source}.");
				_Line("/// </summary>");
				_Open($"{(_o.Public ? "public" : "internal")} partial class {_className} : ParserBase");

				_Line("#region Symbols");
				for (var i = 0; i < _s.Count; ++i)
				{
					var comment = _s.IsNonTerminal(i) ? "" : null != _s.GetTerminal(i)?.Literal ? $" // {_s.Display(i)}" : "";
					_Line($"public const int {_const[i]} = {i};{comment}");
				}
				_Line($"public const int SymbolCount = {_s.Count};");
				_Line("#endregion");
				_Line();
				_Line($"public {_className}(IEnumerable<Token> tokens) : base(tokens, s_tables) {{ }}");
				_Line();
				_Line("/// <summary>");
				_Line($"/// Parses a complete {g.StartSymbol} and returns its tree. Problems are reported in <see cref=\"ParserBase.Errors\"/>.");
				_Line("/// </summary>");
				_Line($"public ParseNode Parse() => Run({_MethodName(g.StartSymbol!)});");

				foreach (var p in g.Productions)
				{
					_Line();
					_Production(p);
				}
				foreach (var d in _predictors.ToList())
				{
					_Line();
					_Predictor(d);
				}
				_Line();
				_Tables();
				_Close();
				return _sb.ToString();
			}

			void _Production(Production p)
			{
				var id = _s.GetId(p.Name);
				var rewritten = p.Expression.Descendants().OfType<Repeat>().Any(r => r.LeftFold);
				_Line($"// {_Comment(p.ToString())}" + (rewritten ? " (left recursion rewritten as a loop)" : ""));
				if (p.IsCollapsed)
				{
					_Open($"void {_MethodName(p.Name)}(ParseNode parent)");
					_Line($"Enter({_const[id]});");
					_Expr(p.Expression, "parent", null, p, true);
					_Line("Leave();");
					_Close();
				}
				else
				{
					_Open($"ParseNode {_MethodName(p.Name)}()");
					_Line($"Enter({_const[id]});");
					_Line($"var node = NewNode({_const[id]});");
					_Expr(p.Expression, "node", null, p, true);
					_Line("Leave();");
					_Line("return node;");
					_Close();
				}
			}

			// emits the code for an expression. known: the token already established as Current, if any
			void _Expr(Expression e, string node, IReadOnlyList<int>? known, Production p, bool isRoot)
			{
				switch (e)
				{
					case EmptyExpression:
						break;
					case SymbolRef sr:
						{
							var id = _s.GetId(sr.Name!);
							if (_s.IsTerminal(id))
							{
								if (null != known && 0 < known.Count && known[0] == id)
									_Line(_nameConsumes ? $"Consume({node}); // {_Comment(_s.Display(id))}" : $"Consume({node});");
								else
									_Line($"Expect({_const[id]}, {node});");
							}
							else if (_s.GetProduction(id)!.IsCollapsed)
								_Line($"{_MethodName(sr.Name!)}({node});");
							else
								_Line($"{node}.Add({_MethodName(sr.Name!)}());");
							break;
						}
					case Sequence seq:
						// tokens the decision already verified carry on to the terminals that start the sequence
						for (var i = 0; i < seq.Items.Count; ++i)
						{
							_Expr(seq.Items[i], node, known, p, false);
							if (null != known && 0 < known.Count && seq.Items[i] is SymbolRef sr && _s.GetId(sr.Name!) == known[0])
								known = known.Skip(1).ToList();
							else
								known = null;
						}
						break;
					default:
						_Decision(_a.GetDecision(e)!, node, p, isRoot);
						break;
				}
			}

			static IReadOnlyList<int>? _Single(IReadOnlyList<int> tokens) => 1 == tokens.Count ? tokens : null;

			void _Decision(Decision d, string node, Production p, bool isRoot)
			{
				// a decision that is the whole body of a loop or optional is already described by its parent's comment
				var parent = _a.GetParent(d.Node);
				if (!isRoot && !(parent is Repeat || parent is OptionalExpression))
					_Line($"// {_Comment(d.Node.ToString())}");
				if (null != d.AppliedPolicy)
					_Line($"// ambiguous within {_a.MaxK} tokens; resolved {(d.Kind == DecisionKind.Alternation ? "by taking the first alternative" : "greedy" == d.AppliedPolicy ? "greedily" : "lazily")}");
				if (1 < d.Depth)
				{
					_DeepDecision(d, node, p, isRoot);
					return;
				}
				switch (d.Node)
				{
					case Alternation a:
						_Alternation(d, a, node, p, isRoot);
						break;
					case OptionalExpression o:
						if (1 == d.DefaultBranch)
						{
							var take = d.TokensOf(0);
							_Open($"if ({_Test(take.ToList(), false)})");
							_Expr(o.Body, node, _Single(take), p, false);
							_Close();
						}
						else
						{
							_Open($"if ({_Test(_ExitTokens(d), true)})");
							_Expr(o.Body, node, null, p, false);
							_Close();
						}
						break;
					case Repeat r:
						{
							var useContinue = 1 == d.DefaultBranch;
							var cont = d.TokensOf(0);
							var test = useContinue ? _Test(cont.ToList(), false) : _Test(_ExitTokens(d), true);
							var fold = r.LeftFold && !p.IsCollapsed;
							if (1 == r.Min)
							{
								_Open("do");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close($"}} while ({test});");
							}
							else
							{
								_Open($"while ({test})");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, useContinue ? _Single(cont) : null, p, false);
								_Close();
							}
							break;
						}
				}
			}

			List<int> _ExitTokens(Decision d)
			{
				var result = d.TokensOf(1).ToList();
				if (!result.Contains(_s.EndOfInput)) result.Add(_s.EndOfInput);
				return result;
			}

			string _ErrorCall(Decision d, string node, Production p, bool isRoot)
			{
				var tokens = new SortedSet<int>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						tokens.Add(x[0]);
				string? expected = null;
				if (isRoot) expected = p.Attributes.GetString("expected");
				expected ??= d.Node.Attributes.GetString("expected");
				var desc = null == expected ? "null" : GrammarAttribute.FormatValue(expected);
				return $"Error({node}, {desc}, {string.Join(", ", tokens.Select(t => _const[t]))});";
			}

			void _Alternation(Decision d, Alternation a, string node, Production p, bool isRoot)
			{
				var n = a.Options.Count;
				if (2 == n && 0 <= d.DefaultBranch)
				{
					// if/else: test the other branch, the default is the else
					var other = 1 - d.DefaultBranch;
					var tokens = d.TokensOf(other);
					if (0 == tokens.Count)
					{
						_Expr(a.Options[d.DefaultBranch], node, null, p, false);
						return;
					}
					_Open($"if ({_Test(tokens.ToList(), false)})");
					_Expr(a.Options[other], node, _Single(tokens), p, false);
					_Close();
					var elseBody = a.Options[d.DefaultBranch];
					if (!(elseBody is EmptyExpression))
					{
						_Open("else");
						_Expr(elseBody, node, null, p, false);
						_Close();
					}
					return;
				}
				_Open("switch (Current.SymbolId)");
				// alternatives whose code comes out the same share one set of case labels
				var cases = new List<(List<int> Tokens, string Body)>();
				for (var i = 0; i < n; ++i)
				{
					if (i == d.DefaultBranch) continue;
					var tokens = d.TokensOf(i);
					if (0 == tokens.Count) continue;
					++_indent;
					var body = _Capture(() =>
					{
						_Expr(a.Options[i], node, _Single(tokens), p, false);
						_Line("break;");
					});
					--_indent;
					var same = cases.FindIndex(c => c.Body == body);
					if (-1 < same) cases[same].Tokens.AddRange(tokens);
					else cases.Add((tokens.ToList(), body));
				}
				foreach (var (tokens, body) in cases)
				{
					foreach (var t in tokens)
						_Line($"case {_const[t]}:");
					_sb.Append(body);
				}
				_Line("default:");
				++_indent;
				if (0 <= d.DefaultBranch)
					_Expr(a.Options[d.DefaultBranch], node, null, p, false);
				else
					_Line(_ErrorCall(d, node, p, isRoot));
				_Line("break;");
				--_indent;
				_Close();
			}

			void _DeepDecision(Decision d, string node, Production p, bool isRoot)
			{
				_predictors.Add(d);
				var call = _PredictName(d) + "()";
				switch (d.Node)
				{
					case Alternation a:
						_Open($"switch ({call})");
						for (var i = 0; i < a.Options.Count; ++i)
						{
							if (i == d.DefaultBranch || 0 == d.Branches[i].Count) continue;
							_Line($"case {i}:");
							++_indent;
							_nameConsumes = true;
							_Expr(a.Options[i], node, _CommonPrefix(d.Branches[i]), p, false);
							_nameConsumes = false;
							_Line("break;");
							--_indent;
						}
						_Line("default:");
						++_indent;
						if (0 <= d.DefaultBranch)
							_Expr(a.Options[d.DefaultBranch], node, null, p, false);
						else
							_Line(_ErrorCall(d, node, p, isRoot));
						_Line("break;");
						--_indent;
						_Close();
						break;
					case OptionalExpression o:
						_Open($"if (0 == {call})");
						_nameConsumes = true;
						_Expr(o.Body, node, _CommonPrefix(d.Branches[0]), p, false);
						_nameConsumes = false;
						_Close();
						break;
					case Repeat r:
						{
							var fold = r.LeftFold && !p.IsCollapsed;
							if (1 == r.Min)
							{
								_Open("do");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close($"}} while (0 == {call});");
							}
							else
							{
								_Open($"while (0 == {call})");
								if (fold) _Line($"{node} = Fold({node});");
								_Expr(r.Body, node, null, p, false);
								_Close();
							}
							break;
						}
				}
			}

			// the tokens every lookahead string of a branch starts with: the predictor has checked them
			static IReadOnlyList<int>? _CommonPrefix(IReadOnlyCollection<LookaheadString> strings)
			{
				if (0 == strings.Count) return null;
				var first = strings.First();
				var n = first.Count;
				foreach (var x in strings)
				{
					var i = 0;
					while (i < n && i < x.Count && x[i] == first[i]) ++i;
					n = i;
				}
				return 0 == n ? null : first.Take(n).ToList();
			}

			void _Predictor(Decision d)
			{
				var what = d.Kind switch
				{
					DecisionKind.Alternation => "which alternative",
					DecisionKind.Optional => "0 to take it, 1 to skip it",
					_ => "0 to go around again, 1 to stop"
				};
				_Line($"// {_Comment(d.Production.Name)}: {_Comment(d.Node.ToString())}");
				_Line($"// looks {d.Depth} tokens ahead and returns {what}, or -1 if nothing matches");
				_Open($"int {_PredictName(d)}()");
				var strings = new List<(LookaheadString S, int Branch)>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						strings.Add((x, i));
				_Trie(strings, 0);
				_Line("return -1;");
				_Close();
			}

			void _Trie(List<(LookaheadString S, int Branch)> strings, int level)
			{
				var token = 0 == level ? "Current" : $"Peek({level})";
				_Open($"switch ({token}.SymbolId)");
				var groups = strings.Where(x => x.S.Count > level).GroupBy(x => x.S[level]).OrderBy(gr => gr.Key).ToList();
				// leaves that return the same branch share one set of case labels
				var leaves = groups.Where(gr => 1 == gr.Select(x => x.Branch).Distinct().Count()).GroupBy(gr => gr.First().Branch).OrderBy(x => x.Key);
				foreach (var leaf in leaves)
				{
					foreach (var gr in leaf)
						_Line($"case {_const[gr.Key]}:");
					++_indent;
					_Line($"return {leaf.Key};");
					--_indent;
				}
				foreach (var gr in groups.Where(gr => 1 < gr.Select(x => x.Branch).Distinct().Count()))
				{
					_Line($"case {_const[gr.Key]}:");
					++_indent;
					_Trie(gr.ToList(), level + 1);
					_Line("break;");
					--_indent;
				}
				_Close();
			}

			void _Tables()
			{
				_Line("#region Tables");
				foreach (var (name, ids, comment) in _setList)
				{
					_Line($"// {_Comment(comment)}");
					_Line($"static readonly bool[] {name} = ParserTables.Set(SymbolCount, {string.Join(", ", ids.Select(i => _const[i]))});");
				}
				_Line("static readonly ParserTables s_tables = new(");
				++_indent;
				_Line($"terminalStart: {_const[_s.NonTerminalCount]}, // lexer ids count terminals from 0; the parser adds this");
				_Line($"symbolNames: [{string.Join(", ", _s.Names.Select(GrammarAttribute.FormatValue))}],");
				_Line($"displayNames: [{string.Join(", ", Enumerable.Range(0, _s.Count).Select(i => GrammarAttribute.FormatValue(_s.Display(i))))}],");
				var collapsed = Enumerable.Range(0, _s.Count).Where(_s.IsCollapsedTerminal).Select(i => _const[i]);
				_Line($"collapsedTerminals: [{string.Join(", ", collapsed)}],");
				_Line("// for each non-terminal, where error recovery can stop while parsing it: what can follow it, plus its sync terminals");
				_Line("recoverySets:");
				_Line("[");
				++_indent;
				var nts = _s.NonTerminalCount;
				for (var i = 0; i < nts; ++i)
				{
					var set = _RecoverySet(_s.GetProduction(i)!);
					_Line($"[{string.Join(", ", set.Select(x => _const[x]))}]{(i < nts - 1 ? "," : "")} // {_s.GetName(i)}");
				}
				--_indent;
				_Line("],");
				var sync = Enumerable.Range(0, _s.Count).Where(i => _s.GetTerminal(i)?.IsSync ?? false).Select(i => _const[i]);
				_Line($"syncTerminals: [{string.Join(", ", sync)}],");
				_Line("endOfInput: EndOfInput,");
				_Line("error: ErrorSymbol);");
				--_indent;
				_Line("#endregion");
			}

			SortedSet<int> _RecoverySet(Production p)
			{
				var result = new SortedSet<int>(_a.GetFollowTokens(p.Name));
				void addSync(object? value)
				{
					if (!(value is string s)) return;
					foreach (var part in s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
					{
						var id = _s.GetId(part);
						if (-1 == id)
						{
							var t = _a.Grammar.Terminals.FirstOrDefault(x => x.Literal == part);
							if (null != t) id = _s.GetId(t.Name);
						}
						if (-1 != id && _s.IsTerminal(id)) result.Add(id);
					}
				}
				addSync(p.Attributes.Get("sync"));
				foreach (var e in p.Expression.Descendants())
					addSync(e.Attributes.Get("sync"));
				return result;
			}
		}
	}
}
