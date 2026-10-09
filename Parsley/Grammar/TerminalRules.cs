using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Parsley
{
	partial class Grammar
	{
		/// <summary>
		/// Turns rules into terminals, as XBNF does: by default a rule is a terminal only when it references
		/// no symbols at all, just "literals" and 'patterns'. <c>&lt;terminal&gt;</c> forces a terminal, which
		/// may then reference other terminals (their regexes are inlined); <c>&lt;terminal=false&gt;</c> forces a
		/// non-terminal. The start rule is never made a terminal unless forced. Changes this grammar.
		/// </summary>
		public IList<GrammarMessage> ClassifyTerminals()
		{
			var messages = new List<GrammarMessage>();
			var rules = Productions.ToList();
			var start = StartSymbol;
			var terminalNames = new HashSet<string>(Terminals.Select(t => t.Name));
			bool? forced(Production p) => p.Attributes.Get("terminal") as bool?;
			var changed = true;
			while (changed)
			{
				changed = false;
				foreach (var p in rules)
				{
					if (terminalNames.Contains(p.Name)) continue;
					var f = forced(p);
					if (false == f || (p.Name == start && true != f)) continue;
					var refs = p.Expression.Descendants().OfType<SymbolRef>().Where(r => null != r.Name).Select(r => r.Name!).ToList();
					// by default only rules with no references; a forced terminal may reference other terminals
					if (0 == refs.Count || (true == f && refs.All(r => r != p.Name && terminalNames.Contains(r))))
					{
						terminalNames.Add(p.Name);
						changed = true;
					}
				}
			}
			foreach (var p in rules)
			{
				if (true == forced(p) && !terminalNames.Contains(p.Name))
				{
					var culprit = p.Expression.Descendants().OfType<SymbolRef>().FirstOrDefault(r => null != r.Name && !terminalNames.Contains(r.Name!));
					var why = null == culprit ? "it refers to itself"
						: culprit.Name == p.Name ? "it refers to itself"
						: $"it references \"{culprit.Name}\", which is not a terminal";
					messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" is marked terminal but {why}", p));
				}
			}
			// move the terminal rules over, keeping declaration order among terminals
			var moved = new List<TerminalDeclaration>();
			Productions.Clear();
			foreach (var p in rules)
			{
				p.Attributes.Remove("terminal");
				if (terminalNames.Contains(p.Name) && !Terminals.Any(t => t.Name == p.Name))
				{
					var t = new TerminalDeclaration(p.Name, p.Expression);
					t.CopyLocation(p);
					t.Attributes.AddRange(p.Attributes);
					moved.Add(t);
				}
				else
					Productions.Add(p);
			}
			Terminals.AddRange(moved);
			return messages;
		}

		/// <summary>
		/// Builds <see cref="TerminalDeclaration.Regex"/> for every terminal. Patterns pass through as written;
		/// literals are escaped; other definitions are built from their EBNF constructs with referenced
		/// terminals inlined. Patterns are not validated: that's left to the lexer generator.
		/// </summary>
		public IList<GrammarMessage> ResolveTerminals()
		{
			var messages = new List<GrammarMessage>();
			var done = new Dictionary<string, (string Text, int Prec)?>();
			foreach (var t in Terminals)
			{
				var rx = _TerminalRegex(t, done, new HashSet<string>(), messages);
				t.Regex = rx?.Text;
			}
			return messages;
		}

		// precedence of a regex fragment: how tightly it binds, so we know when to wrap it in parentheses
		const int RxAlt = 0, RxConcat = 1, RxQuantified = 2, RxAtom = 3;

		(string Text, int Prec)? _TerminalRegex(TerminalDeclaration t, Dictionary<string, (string Text, int Prec)?> done, HashSet<string> visiting, List<GrammarMessage> messages)
		{
			if (done.TryGetValue(t.Name, out var cached)) return cached;
			if (!visiting.Add(t.Name))
			{
				messages.Add(new GrammarMessage(ErrorLevel.Error, $"Terminal \"{t.Name}\" refers to itself, which a regular expression can't express", t));
				return null;
			}
			(string Text, int Prec)? result;
			// a lone pattern passes through untouched
			if (null != t.Pattern) result = (t.Pattern, _PatternPrec(t.Pattern));
			else result = _Rx(t.Definition, t, done, visiting, messages);
			visiting.Remove(t.Name);
			done[t.Name] = result;
			return result;
		}

		(string Text, int Prec)? _Rx(Expression e, TerminalDeclaration owner, Dictionary<string, (string Text, int Prec)?> done, HashSet<string> visiting, List<GrammarMessage> messages)
		{
			switch (e)
			{
				case EmptyExpression:
					return ("", RxAtom);
				case SymbolRef sr when null == sr.Name:
					if (null != sr.Literal)
					{
						var text = EscapeLiteral(sr.Literal);
						return (text, 1 == sr.Literal.Length ? RxAtom : RxConcat);
					}
					return (sr.Pattern!, _PatternPrec(sr.Pattern!));
				case SymbolRef sr:
					{
						var target = GetTerminal(sr.Name!);
						if (null == target)
						{
							messages.Add(new GrammarMessage(ErrorLevel.Error, null != GetProduction(sr.Name!)
								? $"Terminal \"{owner.Name}\" references \"{sr.Name}\", which is not a terminal"
								: $"Terminal \"{owner.Name}\" references \"{sr.Name}\", which is not defined", sr));
							return null;
						}
						return _TerminalRegex(target, done, visiting, messages);
					}
				case Sequence seq:
					{
						var sb = new StringBuilder();
						foreach (var item in seq.Items)
						{
							var x = _Rx(item, owner, done, visiting, messages);
							if (null == x) return null;
							sb.Append(_Wrap(x.Value, RxConcat));
						}
						return (sb.ToString(), 1 == seq.Items.Count ? RxConcat : RxConcat);
					}
				case Alternation a:
					{
						var parts = new List<string>();
						var optional = false;
						foreach (var o in a.Options)
						{
							if (o is EmptyExpression) { optional = true; continue; }
							var x = _Rx(o, owner, done, visiting, messages);
							if (null == x) return null;
							parts.Add(x.Value.Text);
						}
						var alt = (string.Join("|", parts), 1 == parts.Count ? RxConcat : RxAlt);
						// an empty alternative makes the rest optional
						return optional ? (_Wrap(alt, RxAtom) + "?", RxQuantified) : alt;
					}
				case OptionalExpression o:
					{
						var x = _Rx(o.Body, owner, done, visiting, messages);
						return null == x ? null : (_Wrap(x.Value, RxAtom) + "?", RxQuantified);
					}
				case Repeat r:
					{
						var x = _Rx(r.Body, owner, done, visiting, messages);
						return null == x ? null : (_Wrap(x.Value, RxAtom) + (1 == r.Min ? "+" : "*"), RxQuantified);
					}
			}
			throw new NotSupportedException(e.GetType().Name);
		}

		// patterns pass through unparsed, so only the obviously indivisible ones (one character, one escape,
		// one bracket class) are treated as atoms; anything else gets parentheses when combined
		static int _PatternPrec(string pattern)
		{
			if (1 == pattern.Length && -1 == "\\.*+?|()[]{}^$".IndexOf(pattern[0])) return RxAtom;
			if (2 == pattern.Length && '\\' == pattern[0]) return RxAtom;
			if (2 < pattern.Length && '[' == pattern[0] && ']' == pattern[pattern.Length - 1])
			{
				// a single class: no unescaped ] before the last character (a ] right after [ or [^ is literal)
				var i = '^' == pattern[1] ? 2 : 1;
				if (i < pattern.Length - 1 && ']' == pattern[i]) ++i;
				for (; i < pattern.Length - 1; ++i)
				{
					if ('\\' == pattern[i]) { ++i; continue; }
					if (']' == pattern[i] || '[' == pattern[i]) return RxAlt;
				}
				return RxAtom;
			}
			return RxAlt;
		}
		internal static string? EscapeLiteralOrPattern(string? literal, string? pattern) => null != literal ? EscapeLiteral(literal) : pattern;

		static string _Wrap((string Text, int Prec) x, int needed)
		{
			if (x.Prec >= needed || 0 == x.Text.Length) return x.Text;
			return string.Concat("(", x.Text, ")");
		}

		/// <summary>
		/// Escapes a literal for use in a regular expression: metacharacters get a backslash and control
		/// characters become \n, \t and the like or \xHH. Everything else passes through.
		/// </summary>
		public static string EscapeLiteral(string literal)
		{
			var sb = new StringBuilder();
			foreach (var ch in literal)
			{
				switch (ch)
				{
					case '\\': case '.': case '*': case '+': case '?': case '|': case '(': case ')':
					case '[': case ']': case '{': case '}': case '^': case '$': case '/': case '"':
						sb.Append('\\').Append(ch);
						break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					case '\f': sb.Append("\\f"); break;
					case '\v': sb.Append("\\v"); break;
					default:
						if (ch < ' ' || ch == '\x7f')
							sb.Append("\\x").Append(((int)ch).ToString("x2"));
						else
							sb.Append(ch);
						break;
				}
			}
			return sb.ToString();
		}
	}
}
