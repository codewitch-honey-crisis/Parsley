using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Parsley
{
	partial class Grammar
	{
		/// <summary>
		/// Turns rules into terminals, as XBNF does: by default a rule is a terminal only when its right side is a
		/// single "literal" or a single 'pattern'. <c>&lt;terminal&gt;</c> forces a terminal, which may then combine
		/// literals and patterns and reference other terminals (their regexes are inlined);
		/// <c>&lt;terminal=false&gt;</c> forces a non-terminal. The start rule and shared rules are never made
		/// terminals unless forced, and islands and contextual terminals are terminals by their attributes.
		/// Changes this grammar.
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
					// an island is a terminal the lexer controller produces; it has no body
					if (p.Attributes.Contains("entry"))
					{
						terminalNames.Add(p.Name);
						changed = true;
						continue;
					}
					// a contextual terminal is one terminal reference, read differently by the parser. With
					// lexer=, when is the lexer controller's instead, and the rule is an ordinary terminal
					if (null != p.Attributes.Get("when") && !p.Attributes.Contains("lexer"))
					{
						if (p.Expression is SymbolRef only && null != only.Name && only.Name != p.Name && terminalNames.Contains(only.Name))
						{
							terminalNames.Add(p.Name);
							changed = true;
						}
						continue;
					}
					var f = forced(p);
					// like the start rule, a shared rule is never made a terminal automatically: it's an entry point
					if (false == f || ((p.Name == start || p.Attributes.IsSet("shared")) && true != f)) continue;
					var refs = p.Expression.Descendants().OfType<SymbolRef>().Where(r => null != r.Name).Select(r => r.Name!).ToList();
					// by default only a lone literal or pattern; a forced terminal may combine them and reference other terminals
					if (true == f ? refs.All(r => r != p.Name && terminalNames.Contains(r)) : _IsLoneLiteralOrPattern(p.Expression))
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
					if (null != p.Action)
						messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" is a terminal, and semantic actions can't be specified on terminals", p.Action));
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

		// a single "literal" or 'pattern', the only rule that's a terminal by default
		static bool _IsLoneLiteralOrPattern(Expression e) => e is SymbolRef sr && null == sr.Name && (null != sr.Literal || null != sr.Pattern);

		/// <summary>
		/// Builds <see cref="TerminalDeclaration.Regex"/> for every terminal. Patterns pass through as written;
		/// literals are escaped; other definitions are built from their EBNF constructs with referenced
		/// terminals inlined. A terminal that ignores case (its <c>ignoreCase</c> attribute, or else @ignoreCase)
		/// gets <c>(?i)</c> in front, and an inlined terminal keeps its own case sensitivity. Patterns are not
		/// validated: that's left to the lexer generator.
		/// </summary>
		public IList<GrammarMessage> ResolveTerminals()
		{
			var messages = new List<GrammarMessage>();
			// each terminal's regex as built, before (?i) is added: inlining wraps it to suit
			var done = new Dictionary<string, (string Text, int Prec)?>();
			foreach (var t in Terminals)
			{
				// the lexer never produces a contextual terminal or an island, so they have no regex
				if (t.IsContextual || t.IsIsland)
				{
					t.Regex = null;
					continue;
				}
				var rx = _TerminalRegex(t, done, new HashSet<string>(), messages);
				t.Regex = null == rx ? null : IgnoresCase(t) ? WithIgnoreCase(rx.Value.Text) : rx.Value.Text;
			}
			_CheckCaseShadowing(messages);
			return messages;
		}

		/// <summary>
		/// Whether the terminal matches letters in either case: its <c>ignoreCase</c> attribute if it has one,
		/// otherwise the grammar's @ignoreCase
		/// </summary>
		public bool IgnoresCase(TerminalDeclaration terminal) => terminal.IgnoreCase ?? IgnoreCase;

		/// <summary>
		/// Puts <c>(?i)</c> in front of a regular expression, unless the inline modifiers it starts with already
		/// turn case-insensitivity on for all of it, as <c>(?i)</c>, <c>(?si)</c> or <c>(?x)(?i)</c> do.
		/// A scoped modifier such as <c>(?i:abc)</c> covers only its group, so it doesn't count.
		/// </summary>
		public static string WithIgnoreCase(string regex) => _StartsIgnoringCase(regex) ? regex : "(?i)" + regex;

		// reads the inline modifier groups at the start, (?imsxqu-imsxqu) as LexGen takes them, and says whether
		// they leave i on for the rest of the expression. Stops at anything else: a scoped (?flags:…), a group
		// that isn't modifiers, such as (?=, or the first thing that isn't a group.
		static bool _StartsIgnoringCase(string rx)
		{
			bool ignoreCase = false, freeSpacing = false;
			var i = 0;
			while (true)
			{
				// under (?x), whitespace and # comments can come between the groups
				if (freeSpacing)
				{
					while (i < rx.Length)
					{
						if (char.IsWhiteSpace(rx[i])) ++i;
						else if ('#' == rx[i]) { while (i < rx.Length && '\n' != rx[i]) ++i; }
						else break;
					}
				}
				if (i + 1 >= rx.Length || '(' != rx[i] || '?' != rx[i + 1]) return ignoreCase;
				bool off = false, ic = ignoreCase, fs = freeSpacing;
				var j = i + 2;
				for (; j < rx.Length; ++j)
				{
					var c = rx[j];
					if (')' == c || ':' == c) break;
					if ('-' == c)
					{
						if (off) return ignoreCase;
						off = true;
						continue;
					}
					switch (c)
					{
						case 'i': ic = !off; break;
						case 'x': fs = !off; break;
						case 'm': case 's': case 'q': case 'u': break;
						default: return ignoreCase;
					}
				}
				// unterminated, or scoped: either way it doesn't set the mode for the rest
				if (j >= rx.Length || ':' == rx[j]) return ignoreCase;
				ignoreCase = ic;
				freeSpacing = fs;
				i = j + 1;
			}
		}

		// a literal that ignores case also matches every literal after it that differs from it only in case, and the
		// lexer gives a tie to the terminal declared first, so those are never produced. The case-sensitive one has
		// to come first; then it takes the exact text and the other takes the rest. Patterns can't be compared this
		// way, so only literals are checked. Each lexer is checked on its own.
		void _CheckCaseShadowing(List<GrammarMessage> messages)
		{
			var earlier = new Dictionary<string, List<TerminalDeclaration>>();
			foreach (var t in Terminals)
			{
				if (null == t.Regex || null == t.Literal) continue;
				var key = t.Lexer ?? "";
				if (!earlier.TryGetValue(key, out var list))
					earlier.Add(key, list = new List<TerminalDeclaration>());
				var x = list.FirstOrDefault(e => e.Literal != t.Literal && _AsciiEqualsIgnoreCase(e.Literal!, t.Literal));
				if (null != x)
				{
					var text = IgnoresCase(t)
						? $"\"{t.Name}\" is never lexed: \"{x.Name}\" comes before it and ignores case, so it matches the same text. Use \"{x.Name}\" instead"
						: $"\"{t.Name}\" is never lexed: \"{x.Name}\" comes before it and ignores case, so it matches \"{t.Literal}\" too. Declare \"{t.Name}\" before \"{x.Name}\" so it takes the exact text and \"{x.Name}\" the rest";
					messages.Add(new GrammarMessage(ErrorLevel.Warning, text, t));
				}
				if (IgnoresCase(t)) list.Add(t);
			}
		}

		// LexGen folds only ASCII letters unless it's in Unicode mode, so compare the way it always folds
		static bool _AsciiEqualsIgnoreCase(string a, string b)
		{
			if (a.Length != b.Length) return false;
			for (var i = 0; i < a.Length; ++i)
			{
				char x = a[i], y = b[i];
				if (x == y) continue;
				if ('A' <= x && 'Z' >= x) x = (char)(x + 32);
				if ('A' <= y && 'Z' >= y) y = (char)(y + 32);
				if (x != y || 'a' > x || 'z' < x) return false;
			}
			return true;
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
						if (target.IsContextual || target.IsIsland)
						{
							messages.Add(new GrammarMessage(ErrorLevel.Error, $"Terminal \"{owner.Name}\" references \"{sr.Name}\", which is {(target.IsIsland ? "an island" : "contextual")} and has no regex", sr));
							return null;
						}
						var inlined = _TerminalRegex(target, done, visiting, messages);
						// an inlined terminal keeps its own case sensitivity: where it differs from the terminal
						// it's inlined into, it's scoped to that part alone
						var ignoreCase = IgnoresCase(target);
						if (null == inlined || ignoreCase == IgnoresCase(owner)) return inlined;
						return (string.Concat(ignoreCase ? "(?i:" : "(?-i:", inlined.Value.Text, ")"), RxAtom);
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
