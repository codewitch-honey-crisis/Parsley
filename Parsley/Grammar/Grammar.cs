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
		/// <summary>
		/// The C# written after <c>=&gt;</c>, which evaluates this production's parse node, or null
		/// </summary>
		public SemanticAction? Action { get; set; }
		/// <summary>
		/// The <c>type</c> attribute: the return type of the production's evaluate method, written verbatim. Null means object.
		/// </summary>
		public string? Type => Attributes.Get("type") as string;
		/// <summary>
		/// The <c>state</c> attribute, which only the start production takes: the type of the evaluator's state, written verbatim. Null means object?.
		/// </summary>
		public string? StateType => Attributes.Get("state") as string;
		/// <summary>
		/// The <c>entering</c> attribute: a method the parser calls, with no arguments, as it starts this production
		/// </summary>
		public string? Entering => Attributes.Get("entering") as string;
		/// <summary>
		/// The <c>parsed</c> attribute: a method the parser calls with the finished node as this production ends
		/// (for a collapsed production, the node its children went into)
		/// </summary>
		public string? Parsed => Attributes.Get("parsed") as string;
		/// <summary>
		/// For a production marked <c>operators</c>, after analysis: its forms and how it's parsed. Null otherwise.
		/// </summary>
		public OperatorRule? Operators { get; internal set; }
		/// <summary>
		/// For a production that is an operator form of an operator rule (<c>Sum= Expr add Expr;</c>, used as an
		/// alternative of <c>Expr</c>), after analysis: that rule. The rule parses it and makes its nodes, so
		/// it has no parse method of its own. Null otherwise.
		/// </summary>
		public OperatorRule? OperatorOwner { get; internal set; }
		/// <summary>
		/// The expression as written, kept when analysis rewrites it for an operator rule, so a copy of an
		/// analyzed grammar can be analyzed again from the start
		/// </summary>
		internal Expression? Written { get; set; }
		/// <summary>
		/// A deep copy. A production rewritten as part of an operator rule is copied as it was written.
		/// </summary>
		public Production Clone()
		{
			var result = new Production(Name, (Written ?? Expression).Clone()) { Action = Action?.Clone() };
			result.CopyLocation(this);
			foreach (var a in Attributes)
				result.Attributes.Add(a.Clone());
			return result;
		}
		public override string ToString() => XbnfWriter.Write(this);
	}

	/// <summary>
	/// A semantic action: the body of a C# method that evaluates a production's parse node, which it
	/// receives as <c>node</c>. Written in XBNF as <c>Name= expression =&gt; { code };</c>
	/// </summary>
	public sealed class SemanticAction : GrammarNode
	{
		public SemanticAction(string code, bool hasMultilineLiteral = false)
		{
			Code = code ?? throw new ArgumentNullException(nameof(code));
			HasMultilineLiteral = hasMultilineLiteral;
		}
		/// <summary>
		/// The code between the braces, as written
		/// </summary>
		public string Code { get; }
		/// <summary>
		/// The code holds a string literal that spans lines, so it can't be re-indented without changing that string
		/// </summary>
		public bool HasMultilineLiteral { get; }
		public SemanticAction Clone()
		{
			var result = new SemanticAction(Code, HasMultilineLiteral);
			result.CopyLocation(this);
			return result;
		}
		public override string ToString() => "{" + Code + "}";
	}

	/// <summary>
	/// A namespace the synthesized code imports with a C# <c>using</c>, so semantic actions can name its
	/// types without qualifying them. Written in XBNF as <c>@using "System.Text";</c>
	/// </summary>
	public sealed class UsingDirective : GrammarNode
	{
		public UsingDirective(string @namespace)
		{
			Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
		}
		/// <summary>
		/// The namespace, as written, such as <c>System.Text</c>
		/// </summary>
		public string Namespace { get; }
		/// <summary>
		/// True if <see cref="Namespace"/> is a dotted C# name: identifiers separated by dots, each
		/// optionally prefixed with <c>@</c>
		/// </summary>
		public bool IsValid => IsValidNamespace(Namespace);
		public static bool IsValidNamespace(string? ns)
		{
			if (string.IsNullOrEmpty(ns)) return false;
			foreach (var part in ns!.Split('.'))
			{
				var i = 0 < part.Length && '@' == part[0] ? 1 : 0;
				if (i >= part.Length || !(char.IsLetter(part[i]) || '_' == part[i])) return false;
				for (++i; i < part.Length; ++i)
					if (!(char.IsLetterOrDigit(part[i]) || '_' == part[i])) return false;
			}
			return true;
		}
		public UsingDirective Clone()
		{
			var result = new UsingDirective(Namespace);
			result.CopyLocation(this);
			return result;
		}
		public override string ToString() => $"@using {GrammarAttribute.FormatValue(Namespace)};";
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
		/// The <c>ignoreCase</c> attribute: true or false when given, overriding the grammar's @ignoreCase, or null
		/// </summary>
		public bool? IgnoreCase => Attributes.Get("ignoreCase") as bool?;
		/// <summary>
		/// Created for a literal used inline in a production without a declaration of its own
		/// </summary>
		public bool IsImplicit { get; internal set; }
		/// <summary>
		/// The <c>when</c> attribute. On a contextual terminal: the method the parser asks, each time it looks at a
		/// token of <see cref="ContextualBase"/>, whether the token is this terminal instead. On a terminal with
		/// <see cref="Lexer"/>: the method the lexer controller asks whether to lex this terminal instead.
		/// </summary>
		public string? When => Attributes.Get("when") as string;
		/// <summary>
		/// A contextual terminal: never produced by the lexer, but read from another terminal's tokens by the parser
		/// </summary>
		public bool IsContextual => null != When && null == Lexer && !IsIsland;
		/// <summary>
		/// The <c>entry</c> attribute of an island: the shared production of an external grammar, as
		/// <c>Alias.Production</c>, that parses the island's text
		/// </summary>
		public string? Entry => Attributes.Get("entry") as string;
		/// <summary>
		/// An island: text in another language, lexed and parsed by an external grammar. The lexer controller
		/// produces it as one token, so the lexer never does.
		/// </summary>
		public bool IsIsland => null != Entry;
		/// <summary>
		/// The <c>lexer</c> attribute: the alias of the lexer that produces this terminal instead of the grammar's
		/// own, when the controller decides to switch (see <see cref="On"/> and <see cref="When"/>)
		/// </summary>
		public string? Lexer => Attributes.Get("lexer") as string;
		/// <summary>
		/// The terminals the attribute names, as written: names or literal texts separated by spaces or commas
		/// </summary>
		public IReadOnlyList<string> GetNames(string attribute)
			=> Attributes.Get(attribute) is string s ? s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
		/// <summary>
		/// The grammar's own lexer produces it: it isn't contextual, an island, or from another lexer
		/// </summary>
		public bool IsLexed => !IsContextual && !IsIsland && null == Lexer;
		/// <summary>
		/// An island ends when the external grammar's lexer produces an error, unless <c>popOnError=false</c>
		/// </summary>
		public bool PopOnError => !(Attributes.Get("popOnError") is bool b) || b;
		/// <summary>
		/// The terminal a contextual terminal's tokens arrive as: the one its definition names
		/// </summary>
		public string? ContextualBase => IsContextual && Definition is SymbolRef sr && null != sr.Name ? sr.Name : null;
		/// <summary>
		/// The <c>matched</c> attribute: a method the parser calls with each token of this terminal it matches
		/// </summary>
		public string? Matched => Attributes.Get("matched") as string;
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
		/// Where each directive in <see cref="Directives"/> was given, for directives read from XBNF
		/// </summary>
		public Dictionary<string, GrammarNode> DirectiveLocations { get; } = new Dictionary<string, GrammarNode>();
		/// <summary>
		/// The @using directives, in the order given. Unlike other directives, a grammar can have
		/// several, and an imported file's are added to the importer's.
		/// </summary>
		public List<UsingDirective> Usings { get; } = new List<UsingDirective>();
		/// <summary>
		/// The file this grammar was read from, if any. Used for messages and default naming.
		/// </summary>
		public string? Filename { get; set; }
		/// <summary>
		/// The grammars declared with @external, which islands are parsed with
		/// </summary>
		public List<ExternalGrammar> Externals { get; } = new List<ExternalGrammar>();
		/// <summary>
		/// The @lexer directives, which name the lexer used for this grammar or for one of its aliases
		/// </summary>
		public List<LexerDirective> Lexers { get; } = new List<LexerDirective>();

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
		/// <summary>
		/// Adds a namespace for the synthesized code to import with a C# <c>using</c>
		/// </summary>
		public UsingDirective Using(string @namespace)
		{
			var u = new UsingDirective(@namespace);
			Usings.Add(u);
			return u;
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
		/// The production marked <c>start</c>, else in a subordinate grammar the first shared production,
		/// else the first production. An island is never the start symbol.
		/// </summary>
		public string? StartSymbol
		{
			get
			{
				foreach (var p in Productions)
					if (p.Attributes.IsSet("start"))
						return p.Name;
				foreach (var p in Productions)
					if (p.Attributes.IsSet("shared") && !p.Attributes.Contains("entry"))
						return p.Name;
				foreach (var p in Productions)
					if (!p.Attributes.Contains("entry"))
						return p.Name;
				return null;
			}
		}
		/// <summary>
		/// A grammar with shared productions and none marked <c>start</c>: it's only parsed through its shared
		/// productions, as islands of other grammars, so it gets no Parse of its own
		/// </summary>
		public bool IsSubordinate => !Productions.Any(p => p.Attributes.IsSet("start")) && Productions.Any(p => p.Attributes.IsSet("shared"));
		/// <summary>
		/// The productions marked <c>shared</c>, which other grammars can parse islands with
		/// </summary>
		public IEnumerable<Production> SharedProductions => Productions.Where(p => p.Attributes.IsSet("shared"));
		/// <summary>
		/// The @class directive as written, such as <c>My.Parsers.MyParser</c>, or null if it isn't given
		/// </summary>
		public string? ClassDirective => Directives.TryGetValue("class", out var c) ? c : null;
		/// <summary>
		/// The class name to synthesize: the last part of the @class directive, else the file name + "Parser"
		/// </summary>
		public string? ClassName
		{
			get
			{
				var c = ClassDirective;
				if (null == c) return _NameFromFile("Parser");
				var dot = c.LastIndexOf('.');
				return 0 > dot ? c : c.Substring(dot + 1);
			}
		}
		/// <summary>
		/// The evaluator class name to synthesize: the @evaluator directive, else the file name + "Evaluator"
		/// </summary>
		public string? EvaluatorClassName => Directives.TryGetValue("evaluator", out var c) ? c : _NameFromFile("Evaluator");
		/// <summary>
		/// True if any production has a semantic action
		/// </summary>
		public bool HasActions => Productions.Any(p => null != p.Action);
		string? _NameFromFile(string suffix)
		{
			if (string.IsNullOrEmpty(Filename)) return null;
			// made an identifier the way the productions' method names are
			return MethodNames.IdentifierOrEmpty(Path.GetFileNameWithoutExtension(Filename)) + suffix;
		}
		/// <summary>
		/// The namespace to synthesize the parser in: what comes before the last dot of the @class directive, as in
		/// <c>@class "My.Parsers.MyParser";</c>, or null for the global namespace
		/// </summary>
		public string? Namespace
		{
			get
			{
				var c = ClassDirective;
				var dot = c?.LastIndexOf('.') ?? -1;
				return 0 < dot ? c!.Substring(0, dot) : null;
			}
		}
		/// <summary>
		/// The @maxk directive: the most tokens a decision with fixed lookahead looks at. Null if not given
		/// or not a whole number of at least 1 (which the reader reports).
		/// </summary>
		public int? MaxK => Directives.TryGetValue("maxk", out var k) && int.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && 0 < n ? n : null;
		/// <summary>
		/// The @factor directive: whether to left factor alternations whose alternatives start alike. Null if not
		/// given; a rule's or group's <c>factor</c> attribute overrides it.
		/// </summary>
		public bool? Factor => Directives.TryGetValue("factor", out var f) ? ("true" == f ? true : "false" == f ? false : null) : null;
		/// <summary>
		/// The @ignoreCase directive: whether this grammar's terminals match letters in either case unless their own
		/// <c>ignoreCase</c> attribute says otherwise. False if not given. It applies to this grammar and what it
		/// imports, not to @external grammars, which have their own.
		/// </summary>
		public bool IgnoreCase => Directives.TryGetValue("ignoreCase", out var i) && "true" == i;

		public Grammar Clone()
		{
			var result = new Grammar { Filename = Filename };
			foreach (var p in Productions) result.Productions.Add(p.Clone());
			foreach (var t in Terminals) result.Terminals.Add(t.Clone());
			foreach (var d in Directives) result.Directives.Add(d.Key, d.Value);
			foreach (var d in DirectiveLocations) result.DirectiveLocations.Add(d.Key, d.Value);
			foreach (var u in Usings) result.Usings.Add(u.Clone());
			foreach (var x in Externals) result.Externals.Add(x.Clone());
			foreach (var l in Lexers) result.Lexers.Add(l.Clone());
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

		GrammarMessage _DirectiveMessage(ErrorLevel level, string message, string directive)
			=> DirectiveLocations.TryGetValue(directive, out var at)
				? new GrammarMessage(level, message, at)
				: new GrammarMessage(level, message, 0, 0, -1, Filename);

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
			// semantic actions: the evaluator is entered through the start production, so it needs one
			if (null == start.Action)
			{
				var first = Productions.FirstOrDefault(p => null != p.Action);
				if (null != first)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{first.Name}\" has a semantic action, but the start production \"{start.Name}\" does not. Semantic actions require one on the start production", first.Action));
			}
			// the parser's name and namespace come from @class; the evaluator goes beside it
			if (Directives.TryGetValue("class", out var cls) && !_IsTypeName(cls))
				result.Add(_DirectiveMessage(ErrorLevel.Error, $"@class expects a C# class name, with its namespace if it has one, such as \"MyParser\" or \"My.Parsers.MyParser\", not \"{cls}\"", "class"));
			if (Directives.TryGetValue("evaluator", out var ev) && (!_IsTypeName(ev) || ev.Contains('.')))
				result.Add(_DirectiveMessage(ErrorLevel.Error, $"@evaluator expects a C# class name without a namespace, as the evaluator goes in the parser's namespace, not \"{ev}\"", "evaluator"));
			if (Directives.TryGetValue("namespace", out var ns))
				result.Add(_DirectiveMessage(ErrorLevel.Error, $"@namespace is no longer supported: give the namespace in @class, as in @class \"{ns}.{ClassName ?? "MyParser"}\"", "namespace"));
			foreach (var u in Usings)
				if (!u.IsValid)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"@using expects a namespace, such as \"System.Text\", not \"{u.Namespace}\"", u));
			// @evaluator and @using only affect the evaluator, which only semantic actions make
			if (!HasActions)
			{
				if (Directives.ContainsKey("evaluator"))
					result.Add(_DirectiveMessage(ErrorLevel.Warning, "@evaluator is ignored because the grammar has no semantic actions", "evaluator"));
				foreach (var u in Usings)
					if (u.IsValid)
						result.Add(new GrammarMessage(ErrorLevel.Warning, $"@using \"{u.Namespace}\" is ignored because the grammar has no semantic actions", u));
			}
			foreach (var p in Productions)
			{
				if (null != p.Action && p.IsCollapsed)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" is collapsed, so it has no parse node to evaluate and can't have a semantic action", p.Action));
				else if (null != p.Type && p.IsCollapsed)
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{p.Name}\" is collapsed, so it has no parse node to evaluate and its type is ignored", p.Attributes.First(a => "type" == a.Name)));
				if (null != p.Attributes.Get("state") && p != start)
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"\"{p.Name}\" is not the start production, so its state is ignored. Only the start production's state types the evaluator's state", p.Attributes.First(a => "state" == a.Name)));
			}
			foreach (var p in Productions)
			{
				_ValidateAttributes(p.Attributes, p, true, result);
				foreach (var e in p.Expression.Descendants())
				{
					if (e is SymbolRef lit && (null != lit.Literal || null != lit.Pattern))
						_ValidateInlineTerminalAttributes(lit, result);
					else
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
				_ValidateTerminalDefinitionAttributes(t, result);
				if (t.IsContextual)
				{
					var b = null == t.ContextualBase ? null : GetTerminal(t.ContextualBase);
					if (null == b)
						result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" has when, so it must be exactly one terminal, as in {t.Name}<when=\"IsSomething\">= identifier;", t));
					else if (b.IsHidden)
						result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" is read from \"{b.Name}\", which is hidden, so the parser never sees it", t));
					else if (b.IsContextual)
						result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" is read from \"{b.Name}\", which is contextual itself; read it from \"{b.ContextualBase}\" instead", t));
					if (t.IsHidden)
						result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" is contextual, so it can't be hidden: only the parser ever sees it", t));
				}
				foreach (var a in t.Attributes)
				{
					switch (a.Name)
					{
						case "when": case "matched":
							if (!(a.Value is string m) || !_IsMethodName(m))
								result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {a.Name} expects the name of a C# method, such as {a.Name}=\"{("when" == a.Name ? "IsTypeName" : "OnIdentifier")}\"", a));
							break;
						case "hidden": case "collapsed": case "sync": case "terminal":
							if (!(a.Value is bool))
								result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": {a.Name} expects true or false", a));
							break;
						case "ignoreCase":
							if (!(a.Value is bool))
								result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": ignoreCase expects true or false", a));
							else if (t.IsContextual || t.IsIsland)
								result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": ignoreCase has no effect, since {(t.IsIsland ? "an island's text is lexed by its own grammar" : "a contextual terminal is read from another terminal's tokens, not lexed")}", a));
							break;
						case "type": case "expected":
							if (!(a.Value is string))
								result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": {a.Name} expects a string", a));
							break;
						case "entry": case "lexer":
							if (!(a.Value is string))
								result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {a.Name} expects a name", a));
							break;
						case "after": case "on":
							if (!(a.Value is string))
								result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {a.Name} expects terminal names or literals, separated by spaces or commas", a));
							break;
						case "nest": case "pop": case "popOnError": case "startsWith": case "shared":
							// checked with the rest of the island and shared rules
							break;
						default:
							result.Add(new GrammarMessage(ErrorLevel.Message, $"On \"{t.Name}\": unknown attribute \"{a.Name}\" will be ignored", a));
							break;
					}
				}
			}
			_ValidateIslands(result);
			// reachability: from the start production, unless the grammar is only parsed through its shared
			// productions, and from every shared production
			var reached = new HashSet<string>();
			var stack = new Stack<string>();
			if (!IsSubordinate) stack.Push(start.Name);
			foreach (var sp in SharedProductions) stack.Push(sp.Name);
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
					result.Add(new GrammarMessage(ErrorLevel.Warning, IsSubordinate ? $"Production \"{p.Name}\" is unreachable from the shared productions" : $"Production \"{p.Name}\" is unreachable from the start symbol", p));
			return result;
		}
		static readonly HashSet<string> _productionAttributes = new HashSet<string> { "start", "shared", "collapsed", "nowarn", "policy", "expected", "sync", "terminal", "type", "state", "entering", "parsed", "when", "matched", "lexer", "on", "operators", "assoc", "factor", "maxk" };
		static bool _IsMethodName(string s) => 0 < s.Length && (char.IsLetter(s[0]) || '_' == s[0]) && s.All(ch => char.IsLetterOrDigit(ch) || '_' == ch);
		static readonly HashSet<string> _expressionAttributes = new HashSet<string> { "policy", "sync", "expected", "assoc", "factor", "maxk" };
		// a literal or pattern written in a rule: attributes that belong on a terminal's declaration don't apply to it.
		// It isn't a declaration, and every use of the same text is the same terminal, so one use can't make it hidden
		// or collapsed or anything else without making them all so. Those are errors, rather than ignored, so a
		// grammar doesn't silently lex differently from what it says.
		static void _ValidateInlineTerminalAttributes(SymbolRef sr, IList<GrammarMessage> result)
		{
			var text = null != sr.Literal ? "\"" + sr.Literal + "\"" : "'" + sr.Pattern + "'";
			var expressionOnly = new AttributeList();
			foreach (var a in sr.Attributes)
			{
				if (_expressionAttributes.Contains(a.Name) || !_terminalAttributes.Contains(a.Name))
				{
					expressionOnly.Add(a);
					continue;
				}
				result.Add(new GrammarMessage(ErrorLevel.Error, $"{a.Name} goes on a terminal's declaration, and {text} here is a use of it, not a declaration. Every {text} in the grammar is the same terminal, so declare it once with the attribute, as in name<{a}>= {text};, and use it by that name or text", a));
			}
			_ValidateAttributes(expressionOnly, sr, false, result);
		}
		// attributes written inside a terminal's definition, as in x= "a"<hidden>;. A terminal's definition only
		// says what text it matches, so they'd apply to nothing; one that belongs on the terminal is an error, so
		// it isn't mistaken for being on x, and the rest are ignored with a warning
		static void _ValidateTerminalDefinitionAttributes(TerminalDeclaration t, IList<GrammarMessage> result)
		{
			if (null == t.Definition) return;
			foreach (var e in t.Definition.Descendants())
				foreach (var a in e.Attributes)
				{
					if (_terminalAttributes.Contains(a.Name) && !_expressionAttributes.Contains(a.Name))
						result.Add(new GrammarMessage(ErrorLevel.Error, $"{a.Name} is inside \"{t.Name}\"'s definition, where it applies to nothing. Put it on the name instead, as in {t.Name}<{a}>= …;", a));
					else
						result.Add(new GrammarMessage(ErrorLevel.Warning, $"{a.Name} is inside the definition of the terminal \"{t.Name}\", which only says what text it matches, so it will be ignored", a));
				}
		}
		// the attributes a terminal's declaration takes
		static readonly HashSet<string> _terminalAttributes = new HashSet<string> { "hidden", "collapsed", "terminal", "type", "when", "matched", "lexer", "on", "after", "entry", "nest", "pop", "popOnError", "startsWith", "shared", "ignoreCase" };
		static void _ValidateAttributes(AttributeList attrs, GrammarNode owner, bool isProduction, IList<GrammarMessage> result)
		{
			foreach (var a in attrs)
			{
				// only the lexer matches text, so only a terminal can ignore case
				if ("ignoreCase" == a.Name)
				{
					result.Add(new GrammarMessage(ErrorLevel.Error, isProduction
						? "ignoreCase goes on a terminal; this rule is a non-terminal. Only a lone literal or pattern is a terminal by default: mark it <terminal> otherwise"
						: "ignoreCase goes on a terminal, not on a group in a non-terminal. Declare the text as a terminal with ignoreCase and use it by name", a));
					continue;
				}
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
					case "type":
					case "state":
						if (!(a.Value is string s2) || 0 == s2.Trim().Length)
							result.Add(new GrammarMessage(ErrorLevel.Error, $"{a.Name} expects a C# type name as a string, such as {a.Name}=\"double\"", a));
						break;
					case "sync":
						if (!(a.Value is string) && !(a.Value is bool))
							result.Add(new GrammarMessage(ErrorLevel.Warning, "sync on an expression expects a string of terminal names or literals", a));
						break;
					case "entering":
					case "parsed":
						if (!(a.Value is string m) || !_IsMethodName(m))
							result.Add(new GrammarMessage(ErrorLevel.Error, $"{a.Name} expects the name of a C# method, such as {a.Name}=\"{("entering" == a.Name ? "PushScope" : "OnDeclaration")}\"", a));
						break;
					case "maxk":
						if (!(a.Value is long k) || 1 > k || 1000 < k)
							result.Add(new GrammarMessage(ErrorLevel.Error, "maxk expects a whole number of at least 1, such as maxk=1", a));
						break;
					case "assoc":
						if (!(a.Value is string s3) || (s3 != "left" && s3 != "right"))
							result.Add(new GrammarMessage(ErrorLevel.Error, "assoc expects \"left\" or \"right\"", a));
						break;
					case "when":
						result.Add(new GrammarMessage(ErrorLevel.Error, "when makes a contextual terminal, so the rule must be exactly one terminal, as in typedef_name<when=\"IsTypedefName\">= identifier;", a));
						break;
					case "matched":
						result.Add(new GrammarMessage(ErrorLevel.Error, "matched goes on a terminal; this rule is a non-terminal", a));
						break;
					case "lexer": case "on":
						result.Add(new GrammarMessage(ErrorLevel.Error, $"{a.Name} goes on a terminal; this rule is a non-terminal. Only a lone literal or pattern is a terminal by default: mark it <terminal> otherwise", a));
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
			var msgs = XbnfParser.TryParse(text, filename, out var g);
			GrammarException.ThrowIfErrors(msgs);
			return g!;
		}
		/// <summary>
		/// Reads a grammar from an XBNF file, following @import directives
		/// </summary>
		public static Grammar ReadFrom(string filename)
		{
			var msgs = XbnfParser.TryReadFrom(filename, out var g);
			GrammarException.ThrowIfErrors(msgs);
			return g!;
		}
		public static Grammar ReadFrom(TextReader reader, string? filename)
        {
            var msgs = XbnfParser.TryReadFrom(reader,filename, out var g);
            GrammarException.ThrowIfErrors(msgs);
            return g!;
        }
    }
}
