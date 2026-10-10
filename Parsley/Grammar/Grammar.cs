using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// A non-terminal: a name, an expression and attributes
	/// </summary>
	public sealed class Production : GrammarNode
	{
		public Production(string name, Expression expression)
		{
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Expression = expression ?? throw new ArgumentNullException(nameof(expression));
		}
		public string Name { get; set; }
		public Expression Expression { get; set; }
		public AttributeList Attributes { get; } = new AttributeList();
		public bool IsCollapsed => Attributes.IsSet("collapsed");
		public string Type => Attributes.TryGetValue("type", out var v) && v is string s ? s : "object";
        public string? Converter => Attributes.TryGetValue("converter", out var v) && v is string s ? s : null;
        public Production Clone()
		{
			var result = new Production(Name, Expression.Clone());
			result.CopyLocation(this);
			foreach (var a in Attributes)
				result.Attributes.Add(a.Clone());
			return result;
		}
		public override string ToString() => XbnfWriter.Write(this);
	}

	/// <summary>
	/// A terminal: a rule whose definition only uses literals, patterns and other terminals.
	/// Its definition and the regular expression built from it are for the lexer; the parser only
	/// uses its name and attributes.
	/// </summary>
	public sealed class TerminalDeclaration : GrammarNode
	{
		public TerminalDeclaration(string name, Expression definition)
		{
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Definition = definition ?? throw new ArgumentNullException(nameof(definition));
		}
		public TerminalDeclaration(string name, string? literal = null, string? pattern = null)
			: this(name, null != literal ? SymbolRef.FromLiteral(literal) : SymbolRef.FromPattern(pattern ?? throw new ArgumentNullException(nameof(pattern), "A terminal needs a literal or a pattern"))) { }
		public string Name { get; set; }
		/// <summary>
		/// The rule's right side as written: "literals", 'patterns', references to other terminals and EBNF operators
		/// </summary>
		public Expression Definition { get; set; }
		/// <summary>
		/// The exact text, when the definition is a single "literal"
		/// </summary>
		public string? Literal => Definition is SymbolRef sr && null == sr.Name ? sr.Literal : null;
		/// <summary>
		/// The regular expression as written, when the definition is a single 'pattern'
		/// </summary>
		public string? Pattern => Definition is SymbolRef sr && null == sr.Name ? sr.Pattern : null;
		/// <summary>
		/// The regular expression for the lexer: a pattern passed through as written, a literal with its
		/// metacharacters escaped, or one built from the definition with referenced terminals inlined.
		/// Set by <see cref="Grammar.ResolveTerminals"/>; patterns are not validated.
		/// </summary>
		public string? Regex { get; internal set; }
		public AttributeList Attributes { get; } = new AttributeList();
		/// <summary>
		/// The lexer never reports it
		/// </summary>
		public bool IsHidden => Attributes.IsSet("hidden");
		/// <summary>
		/// Reported and checked, but left out of the parse tree
		/// </summary>
		public bool IsCollapsed => Attributes.IsSet("collapsed");
		/// <summary>
		/// A strong restart point for error recovery
		/// </summary>
		public bool IsSync => Attributes.IsSet("sync");
		/// <summary>
		/// Created for a literal used inline in a production without a declaration of its own
		/// </summary>
		public bool IsImplicit { get; internal set; }
		public TerminalDeclaration Clone()
		{
			var result = new TerminalDeclaration(Name, Definition.Clone()) { IsImplicit = IsImplicit, Regex = Regex };
			result.CopyLocation(this);
			foreach (var a in Attributes)
				result.Attributes.Add(a.Clone());
			return result;
		}
		public override string ToString() => XbnfWriter.Write(this);
	}

	/// <summary>
	/// A grammar: productions (non-terminals) whose right sides are EBNF expressions, plus the
	/// terminals they use and any directives.
	/// </summary>
	public sealed partial class Grammar
	{
		public List<Production> Productions { get; } = new List<Production>();
		public List<TerminalDeclaration> Terminals { get; } = new List<TerminalDeclaration>();
		/// <summary>
		/// @namespace, @class and similar. Values are strings.
		/// </summary>
		public Dictionary<string, string> Directives { get; } = new Dictionary<string, string>();
		/// <summary>
		/// The file this grammar was read from, if any. Used for messages and default naming.
		/// </summary>
		public string? Filename { get; set; }

		#region Builder API
		/// <summary>
		/// Adds a production. The first one, or the one marked <c>start</c>, is the start symbol.
		/// </summary>
		public Production Production(string name, Expression expression, params string[] attributes)
		{
			var p = new Production(name, expression);
			foreach (var a in attributes)
				p.Attributes.Set(a);
			Productions.Add(p);
			return p;
		}
		/// <summary>
		/// Declares a terminal matched by exact text
		/// </summary>
		public TerminalDeclaration Terminal(string name, string literal, params string[] attributes)
		{
			var t = new TerminalDeclaration(name, literal: literal);
			foreach (var a in attributes)
				t.Attributes.Set(a);
			Terminals.Add(t);
			return t;
		}
		/// <summary>
		/// Declares a terminal from literals, patterns and other terminals: Terminal("Boolean", Alt("true", "false"))
		/// </summary>
		public TerminalDeclaration Terminal(string name, Expression definition, params string[] attributes)
		{
			var t = new TerminalDeclaration(name, definition);
			foreach (var a in attributes)
				t.Attributes.Set(a);
			Terminals.Add(t);
			return t;
		}
		/// <summary>
		/// Declares a terminal matched by a regular expression
		/// </summary>
		public TerminalDeclaration TerminalPattern(string name, string pattern, params string[] attributes)
		{
			var t = new TerminalDeclaration(name, pattern: pattern);
			foreach (var a in attributes)
				t.Attributes.Set(a);
			Terminals.Add(t);
			return t;
		}
		#endregion

		public Production? GetProduction(string name)
		{
			foreach (var p in Productions)
				if (p.Name == name)
					return p;
			return null;
		}
		public TerminalDeclaration? GetTerminal(string name)
		{
			foreach (var t in Terminals)
				if (t.Name == name)
					return t;
			return null;
		}
		/// <summary>
		/// The production marked <c>start</c>, else the first production
		/// </summary>
		public string? StartSymbol
		{
			get
			{
				foreach (var p in Productions)
					if (p.Attributes.IsSet("start"))
						return p.Name;
				return 0 < Productions.Count ? Productions[0].Name : null;
			}
		}
		/// <summary>
		/// The class name to synthesize: the @class directive, else the file name + "Parser"
		/// </summary>
		public string? ClassName
		{
			get
			{
				if (Directives.TryGetValue("class", out var c)) return c;
				if (string.IsNullOrEmpty(Filename)) return null;
				var sb = new StringBuilder();
				var upper = true;
				foreach (var ch in Path.GetFileNameWithoutExtension(Filename))
				{
					if (!char.IsLetterOrDigit(ch)) { upper = true; continue; }
					if (0 == sb.Length && char.IsDigit(ch)) sb.Append('_');
					sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
					upper = false;
				}
				return sb.Append("Parser").ToString();
			}
		}
		public string? Namespace => Directives.TryGetValue("namespace", out var n) ? n : null;

		public Grammar Clone()
		{
			var result = new Grammar { Filename = Filename };
			foreach (var p in Productions) result.Productions.Add(p.Clone());
			foreach (var t in Terminals) result.Terminals.Add(t.Clone());
			foreach (var d in Directives) result.Directives.Add(d.Key, d.Value);
			return result;
		}

		/// <summary>
		/// Resolves "literal" and 'pattern' references inside productions to terminal names,
		/// declaring implicit terminals for any that aren't declared. Changes this grammar.
		/// </summary>
		public void ResolveLiterals()
		{
			// implicit terminals go after the declared ones; to control a literal's lexer priority,
			// declare it explicitly where you want it and inline uses will resolve to that declaration
			foreach (var p in Productions)
			{
				foreach (var e in p.Expression.Descendants())
				{
					if (e is SymbolRef sr && null == sr.Name)
					{
						TerminalDeclaration? t = null;
						if (null != sr.Literal)
							t = Terminals.FirstOrDefault(x => x.Literal == sr.Literal);
						else if (null != sr.Pattern)
							t = Terminals.FirstOrDefault(x => x.Pattern == sr.Pattern);
						if (null == t)
						{
							t = new TerminalDeclaration(_ImplicitName(sr.Literal, sr.Pattern), sr.Literal, sr.Pattern) { IsImplicit = true };
							t.CopyLocation(sr);
							t.Regex = EscapeLiteralOrPattern(sr.Literal, sr.Pattern);
							Terminals.Add(t);
						}
						sr.Name = t.Name;
					}
				}
			}
		}

		static readonly Dictionary<char, string> _punctuationNames = new Dictionary<char, string>
		{
			['{'] = "lbrace", ['}'] = "rbrace", ['('] = "lparen", [')'] = "rparen", ['['] = "lbracket", [']'] = "rbracket",
			[';'] = "semi", [','] = "comma", ['.'] = "dot", [':'] = "colon", ['='] = "eq", ['+'] = "plus", ['-'] = "minus",
			['*'] = "star", ['/'] = "slash", ['<'] = "lt", ['>'] = "gt", ['!'] = "bang", ['&'] = "amp", ['|'] = "bar",
			['^'] = "caret", ['%'] = "percent", ['?'] = "question", ['~'] = "tilde", ['@'] = "at", ['#'] = "hash",
			['$'] = "dollar", ['"'] = "quote", ['\''] = "apos", ['\\'] = "backslash", ['`'] = "backtick"
		};
		string _ImplicitName(string? literal, string? pattern)
		{
			string baseName;
			if (null != literal && 0 < literal.Length && (char.IsLetter(literal[0]) || '_' == literal[0]) && literal.All(c => char.IsLetterOrDigit(c) || '_' == c))
				baseName = literal + "Keyword";
			else if (null != literal && 0 < literal.Length && literal.All(c => _punctuationNames.ContainsKey(c)))
				baseName = string.Join("_", literal.Select(c => _punctuationNames[c]));
			else
				baseName = "implicit";
			var name = baseName;
			var i = 2;
			while (null != GetTerminal(name) || null != GetProduction(name))
				name = baseName + (i++).ToString();
			return name;
		}

		/// <summary>
		/// Checks names and attributes. Literals should be resolved first.
		/// </summary>
		public IList<GrammarMessage> Validate()
		{
			var result = new List<GrammarMessage>();
			if (0 == Productions.Count)
			{
				result.Add(new GrammarMessage(ErrorLevel.Error, "The grammar has no productions", 0, 0, -1, Filename));
				return result;
			}
			result.AddRange(ResolveTerminals());
			var seen = new HashSet<string>();
			foreach (var p in Productions)
				if (!seen.Add(p.Name))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"Production \"{p.Name}\" is defined more than once", p));
			foreach (var t in Terminals)
				if (!seen.Add(t.Name))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" is defined more than once", t));
			var starts = Productions.Where(p => p.Attributes.IsSet("start")).ToList();
			if (1 < starts.Count)
				result.Add(new GrammarMessage(ErrorLevel.Warning, $"More than one production is marked start; \"{starts[0].Name}\" is used", starts[1]));
			var start = GetProduction(StartSymbol!)!;
			if (start.IsCollapsed)
				result.Add(new GrammarMessage(ErrorLevel.Error, "The start production cannot be collapsed", start));
			foreach (var p in Productions)
			{
				_ValidateAttributes(p.Attributes, p, true, result);
				foreach (var e in p.Expression.Descendants())
				{
					_ValidateAttributes(e.Attributes, e, false, result);
					if (e is SymbolRef sr)
					{
						if (null == sr.Name)
							result.Add(new GrammarMessage(ErrorLevel.Error, "Unresolved literal", sr));
						else if (null == GetProduction(sr.Name))
						{
							var t = GetTerminal(sr.Name);
							if (null == t)
								result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{sr.Name}\" is not defined", sr));
							else if (t.IsHidden)
								result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{sr.Name}\" is hidden, so the parser never sees it and it can't be used in a production", sr));
						}
					}
				}
			}
			foreach (var t in Terminals)
			{
				foreach (var a in t.Attributes)
				{
					switch (a.Name)
					{
						case "hidden": case "collapsed": case "sync": case "terminal":
							if (!(a.Value is bool))
								result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": {a.Name} expects true or false", a));
							break;
						case "type": case "expected":
							if (!(a.Value is string))
								result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": {a.Name} expects a string", a));
							break;
						default:
							result.Add(new GrammarMessage(ErrorLevel.Message, $"On \"{t.Name}\": unknown attribute \"{a.Name}\" will be ignored", a));
							break;
					}
				}
			}
			// reachability
			var reached = new HashSet<string>();
			var stack = new Stack<string>();
			stack.Push(start.Name);
			while (0 < stack.Count)
			{
				var n = stack.Pop();
				if (!reached.Add(n)) continue;
				var p = GetProduction(n);
				if (null == p) continue;
				foreach (var e in p.Expression.Descendants())
					if (e is SymbolRef sr && null != sr.Name)
						stack.Push(sr.Name);
			}
			foreach (var p in Productions)
				if (!reached.Contains(p.Name) && !p.Attributes.IsSet("nowarn"))
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"Production \"{p.Name}\" is unreachable from the start symbol", p));
			return result;
		}
		static readonly HashSet<string> _productionAttributes = new HashSet<string> { "start", "collapsed", "converter", "nowarn", "policy", "expected", "sync", "terminal", "type" };
		static readonly HashSet<string> _expressionAttributes = new HashSet<string> { "policy", "sync", "expected" };
		static void _ValidateAttributes(AttributeList attrs, GrammarNode owner, bool isProduction, IList<GrammarMessage> result)
		{
			foreach (var a in attrs)
			{
				var known = isProduction ? _productionAttributes : _expressionAttributes;
				if (!known.Contains(a.Name))
				{
					if ("virtual" == a.Name)
						result.Add(new GrammarMessage(ErrorLevel.Error, "Virtual productions are not supported", a));
					else
						result.Add(new GrammarMessage(ErrorLevel.Message, $"Unknown attribute \"{a.Name}\" will be ignored", a));
					continue;
				}
				switch (a.Name)
				{
					case "policy":
						if (!(a.Value is string s) || (s != "greedy" && s != "lazy" && s != "first"))
							result.Add(new GrammarMessage(ErrorLevel.Error, "policy expects \"greedy\", \"lazy\" or \"first\"", a));
						break;
					case "expected":
						if (!(a.Value is string))
							result.Add(new GrammarMessage(ErrorLevel.Warning, "expected expects a string", a));
						break;
					case "sync":
						if (!(a.Value is string) && !(a.Value is bool))
							result.Add(new GrammarMessage(ErrorLevel.Warning, "sync on an expression expects a string of terminal names or literals", a));
						break;
					case "type":
						if (!(a.Value is string))
							result.Add(new GrammarMessage(ErrorLevel.Warning, "type expects a string", a));
						break;
                    case "converter":
                        if (!(a.Value is string))
                            result.Add(new GrammarMessage(ErrorLevel.Warning, "converter expects a string", a));
                        break;
                    default:
						if (!(a.Value is bool))
							result.Add(new GrammarMessage(ErrorLevel.Warning, $"{a.Name} expects true or false", a));
						break;
				}
			}
		}

		public override string ToString() => XbnfWriter.Write(this);

		/// <summary>
		/// Reads a grammar from XBNF text. Imports are resolved relative to the current directory.
		/// </summary>
		public static Grammar Parse(string text, string? filename = null)
		{
			var msgs = XbnfReader.TryParse(text, filename, out var g);
			GrammarException.ThrowIfErrors(msgs);
			return g!;
		}
		/// <summary>
		/// Reads a grammar from an XBNF file, following @import directives
		/// </summary>
		public static Grammar ReadFrom(string filename)
		{
			var msgs = XbnfReader.TryReadFrom(filename, out var g);
			GrammarException.ThrowIfErrors(msgs);
			return g!;
		}
		public static Grammar ReadFrom(TextReader reader, string? filename)
        {
            var msgs = XbnfReader.TryReadFrom(reader,filename, out var g);
            GrammarException.ThrowIfErrors(msgs);
            return g!;
        }
    }
}
