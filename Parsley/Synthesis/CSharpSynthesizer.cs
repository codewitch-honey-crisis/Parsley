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
		/// The namespace, overriding the one the grammar's @class gives, as in <c>@class "My.Parsers.MyParser";</c>.
		/// Null uses the grammar's, or leaves the class in the global namespace if it gives none.
		/// </summary>
		public string? Namespace { get; set; }
		/// <summary>
		/// The class name, overriding the grammar's @class (its last part) and the default (file name + "Parser")
		/// </summary>
		public string? ClassName { get; set; }
		/// <summary>
		/// The evaluator class name, overriding the grammar's @evaluator and the default (file name + "Evaluator")
		/// </summary>
		public string? EvaluatorClassName { get; set; }
		/// <summary>
		/// Whether the class is public (else internal)
		/// </summary>
		public bool Public { get; set; } = true;
		/// <summary>
		/// Write only the parser and evaluator classes, private, for nesting in another type: no header, usings or
		/// namespace. <see cref="SynthesizedParser.Usings"/> has the usings the code needs.
		/// </summary>
		public bool Nested { get; set; }
		/// <summary>
		/// The indent, in tabs, of the classes' declarations, for nesting
		/// </summary>
		public int Indent { get; set; }
		/// <summary>
		/// Names the symbol constants must not take, such as the members of a type the classes are nested in
		/// </summary>
		public ICollection<string>? ReservedNames { get; set; }
		/// <summary>
		/// The access of the classes when <see cref="Nested"/>: "private" unless set, or "internal" for classes
		/// another nested class has to reach. "" writes none, so the classes are private unless another part of them,
		/// as they're partial, gives an access of its own.
		/// </summary>
		public string? NestedAccess { get; set; }
		/// <summary>
		/// The type whose static Tokenize the code calls for a lexer: the grammar's own (null), an @external
		/// grammar's alias, or a lexer terminals name with lexer=. Null uses <see cref="CSharpSynthesizer.DefaultLexerType"/>.
		/// </summary>
		public Func<string?, string>? LexerType { get; set; }
		/// <summary>
		/// The parser class of an @external grammar, by alias. Null uses <see cref="CSharpSynthesizer.DefaultExternalParser"/>.
		/// </summary>
		public Func<string, string>? ExternalParser { get; set; }
		/// <summary>
		/// The path written in the #line directives that map code copied from the grammar (semantic actions,
		/// <c>type</c> and <c>state</c> attributes and <c>@using</c>s) back to the grammar file it came from, given
		/// that file's path as the grammar recorded it. Null writes that path as is. Code from a grammar with no file
		/// gets no directives.
		/// </summary>
		public Func<string, string>? LinePath { get; set; }
	}

	/// <summary>
	/// A symbol constant of a synthesized parser
	/// </summary>
	public sealed class SynthesizedConstant
	{
		public SynthesizedConstant(string name, int id, string? comment) { Name = name; Id = id; Comment = comment; }
		public string Name { get; }
		public int Id { get; }
		/// <summary>
		/// How a terminal with a literal reads, such as "+", or null
		/// </summary>
		public string? Comment { get; }
	}

	/// <summary>
	/// The code for a parser, and evaluator if there is one, with what's needed to place it and call it
	/// </summary>
	public sealed class SynthesizedParser
	{
		internal SynthesizedParser(string code, IReadOnlyList<string> usings, string className, string? evaluatorClassName,
			string? evaluateReturnType, string? evaluateStateParameter, IReadOnlyList<SynthesizedConstant> constants, ParserEngine engine,
			bool isSubordinate, bool isCompound, bool hasLexerMode, IReadOnlyList<string> sharedEntries, IReadOnlyList<GrammarMessage> messages,
			IReadOnlyList<string> usingLines, string? evaluateLineDirective)
		{
			Messages = messages;
			UsingLines = usingLines; EvaluateLineDirective = evaluateLineDirective;
			Code = code; Usings = usings; ClassName = className; EvaluatorClassName = evaluatorClassName;
			EvaluateReturnType = evaluateReturnType; EvaluateStateParameter = evaluateStateParameter; Constants = constants; Engine = engine;
			IsSubordinate = isSubordinate; IsCompound = isCompound; HasLexerMode = hasLexerMode; SharedEntries = sharedEntries;
		}
		/// <summary>
		/// The grammar is only parsed through its shared productions, so the parser has no TryParse or Parse
		/// </summary>
		public bool IsSubordinate { get; }
		/// <summary>
		/// The grammar has islands or terminals from other lexers, so its tokens come from a LexerController
		/// </summary>
		public bool IsCompound { get; }
		/// <summary>
		/// The parser has a static CreateLexerMode(TokenizeFunc), which a LexerController lexes the grammar with
		/// </summary>
		public bool HasLexerMode { get; }
		/// <summary>
		/// The parser's static entry points for the shared productions, such as TryParseObject
		/// </summary>
		public IReadOnlyList<string> SharedEntries { get; }
		public string Code { get; }
		/// <summary>
		/// The namespaces the code imports. Written at the top of the code unless <see cref="SynthesisOptions.Nested"/>.
		/// </summary>
		public IReadOnlyList<string> Usings { get; }
		/// <summary>
		/// The lines that write <see cref="Usings"/>, with #line directives around those from the grammar's <c>@using</c>s,
		/// which is how they're written at the top of the code unless <see cref="SynthesisOptions.Nested"/>
		/// </summary>
		public IReadOnlyList<string> UsingLines { get; }
		/// <summary>
		/// The #line directive that maps a declaration naming <see cref="EvaluateReturnType"/> and
		/// <see cref="EvaluateStateParameter"/> to the start rule's <c>type</c> or <c>state</c> attribute, to go on the line
		/// before it, with <c>#line default</c> on the line after. Null when there's none.
		/// </summary>
		public string? EvaluateLineDirective { get; }
		public string ClassName { get; }
		/// <summary>
		/// The evaluator class, or null when the grammar has no semantic actions
		/// </summary>
		public string? EvaluatorClassName { get; }
		/// <summary>
		/// The type the evaluator's static Evaluate returns, or null without an evaluator
		/// </summary>
		public string? EvaluateReturnType { get; }
		/// <summary>
		/// The declaration of the evaluator's static Evaluate's state parameter, such as "object? state = null", or null without an evaluator
		/// </summary>
		public string? EvaluateStateParameter { get; }
		/// <summary>
		/// The symbol constants, by id
		/// </summary>
		public IReadOnlyList<SynthesizedConstant> Constants { get; }
		/// <summary>
		/// The engine the parser was synthesized for, which says which base class source it needs
		/// </summary>
		public ParserEngine Engine { get; }
		/// <summary>
		/// Warnings from synthesis, such as a symbol constant renamed because its symbol's name was taken. The same as
		/// <see cref="CSharpSynthesizer.CheckNames"/> gives.
		/// </summary>
		public IReadOnlyList<GrammarMessage> Messages { get; }
	}

	/// <summary>
	/// Turns an analyzed grammar into C# recursive descent that reads as if written by hand:
	/// one method per production, each decision written with the cheapest test that decides it,
	/// and a comment quoting the grammar above each piece. When the start production has a semantic
	/// action, an evaluator class follows the parser, with one method per production that runs its action
	/// over a parse node.
	/// </summary>
	public static class CSharpSynthesizer
	{
		/// <summary>
		/// The file name of the shared runtime source
		/// </summary>
		public const string RuntimeFileName = "ParsleyRuntime.cs";
		/// <summary>
		/// The shared runtime support (Token, LookAheadEnumerator, ParseNode, ParseError, ParserTables and the
		/// abstract ParserBase) that every synthesized parser needs alongside it, whatever its engine
		/// </summary>
		public static string RuntimeSource => _Resource(RuntimeFileName);
		/// <summary>
		/// The file name of an engine's base class source, such as LL1ParserBase.cs
		/// </summary>
		public static string BaseFileName(ParserEngine engine) => engine.BaseClassName() + ".cs";
		/// <summary>
		/// The source of an engine's base class (LL1ParserBase, LLkParserBase or AllStarParserBase), which
		/// a parser synthesized for that engine derives from. It needs <see cref="RuntimeSource"/>.
		/// </summary>
		public static string BaseSource(ParserEngine engine) => _Resource(BaseFileName(engine));

		/// <summary>
		/// The type a grammar uses for a lexer when nothing else is said: what @lexer names for it, in the namespace
		/// it gives (<c>@lexer "My.Lexers.MyLexer";</c>); for an @external grammar, what that grammar's own @lexer
		/// names, else the alias plus "Lexer", in that grammar's namespace unless the name gives one; for the
		/// grammar's own lexer, its file name plus "Lexer"; for one terminals name with lexer=, the name plus "Lexer".
		/// </summary>
		/// <param name="alias">An @external alias, a lexer=, or null for the grammar's own lexer</param>
		public static string DefaultLexerType(Grammar g, string? alias)
		{
			var named = g.GetLexerType(alias);
			if (null != named) return _Qualify(named, null);
			if (null == alias) return g.NameFromFile("Lexer") ?? (g.StartSymbol + "Lexer");
			var x = g.GetExternal(alias);
			if (null == x?.Grammar) return alias + "Lexer";
			return _Qualify(x.Grammar.GetLexerType(null) ?? alias + "Lexer", x.Grammar.Namespace);
		}
		/// <summary>
		/// The parser class of an @external grammar: its @class, else its file name plus "Parser", in the namespace its
		/// @class gives
		/// </summary>
		public static string DefaultExternalParser(Grammar g, string alias)
		{
			var x = g.GetExternal(alias)?.Grammar;
			if (null == x) return alias + "Parser";
			return _Qualify(x.ClassName ?? (x.StartSymbol + "Parser"), x.Namespace);
		}
		// a name in another grammar's namespace, unless it gives its own. A name with a namespace is from the global
		// namespace, so it means the same wherever the parser is.
		static string _Qualify(string name, string? ns)
		{
			if (name.StartsWith("global::", StringComparison.Ordinal)) return name;
			if (name.Contains(".")) return "global::" + name;
			return null == ns ? name : $"global::{ns}.{name}";
		}
		static string _Resource(string name)
		{
			using var s = typeof(CSharpSynthesizer).Assembly.GetManifestResourceStream("Parsley.Runtime." + name)
				?? throw new InvalidOperationException($"The runtime source {name} is missing from the assembly");
			using var r = new StreamReader(s);
			return r.ReadToEnd();
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

		/// <summary>
		/// The warnings synthesizing with these options would give, without synthesizing: a symbol constant that
		/// can't have its symbol's name, because a member, a type or a local of the synthesized code has it, and
		/// gets a number after it instead. Throws if the analysis has errors.
		/// </summary>
		public static IReadOnlyList<GrammarMessage> CheckNames(GrammarAnalysis analysis, SynthesisOptions? options = null)
		{
			if (analysis.HasErrors)
				throw new GrammarException(analysis.Messages);
			return new _Emitter(analysis, options ?? new SynthesisOptions()).Messages;
		}

		/// <summary>
		/// Synthesizes, and returns the code with what's needed to place it and call it. Throws if the analysis has errors.
		/// </summary>
		public static SynthesizedParser SynthesizeParser(GrammarAnalysis analysis, SynthesisOptions? options = null)
		{
			if (analysis.HasErrors)
				throw new GrammarException(analysis.Messages);
			var e = new _Emitter(analysis, options ?? new SynthesisOptions());
			var code = e.Emit();
			return e.Result(code);
		}

		sealed class _Emitter
		{
			readonly GrammarAnalysis _a;
			readonly SymbolTable _s;
			readonly SynthesisOptions _o;
			readonly string _className;
			readonly string? _evaluatorName; // null when there's no evaluator to synthesize
			readonly string[] _const;
			readonly HashSet<string> _constNames; // without any @
			readonly MethodNames _names; // ParseXxx, EvaluateXxx, TryParseXxx and PredictXxxN
			StringBuilder _sb = new();
			int _indent;
			readonly Dictionary<string, string> _sets = new(); // key: sorted ids
			readonly List<(string Name, int[] Ids, string Comment)> _setList = new();
			readonly List<Decision> _predictors = new();
			readonly ParserEngine _engine;
			readonly bool _allStar; // productions carry the ATN state they return to, for adaptive prediction
			bool _nameConsumes; // inside a multi-token decision, where no case label shows the token

			public _Emitter(GrammarAnalysis a, SynthesisOptions o)
			{
				_a = a;
				_s = a.Symbols;
				_o = o;
				_engine = a.Engine;
				_allStar = ParserEngine.AllStar == _engine;
				_className = _Ident(o.ClassName ?? a.Grammar.ClassName ?? (a.Grammar.StartSymbol + "Parser"));
				// only an action on the start production makes an evaluator; validation has already
				// rejected actions elsewhere without one there
				if (null != a.Grammar.GetProduction(a.Grammar.StartSymbol!)?.Action)
				{
					_evaluatorName = _VerbatimIdent(o.EvaluatorClassName ?? a.Grammar.EvaluatorClassName ?? (a.Grammar.StartSymbol + "Evaluator"));
					if (_evaluatorName == _className)
						throw new GrammarException($"The evaluator and the parser are both named \"{_className}\". Use @evaluator or @class to name them apart");
				}
				_names = MethodNames.For(a.Grammar);
				foreach (var p in a.Grammar.Productions)
					if (_names.IsRenumbered(p.Name))
						Messages.Add(new GrammarMessage(ErrorLevel.Warning,
							$"The methods for {p.Name} are named for {_names.Stem(p.Name)}, since another production's are named for {MethodNames.Identifier(p.Name)}. Rename the production to keep its name", p));
				_const = _MakeConstantNames();
				_constNames = new HashSet<string>(_const.Select(c => c.TrimStart('@')));
			}

			#region Naming
			static readonly HashSet<string> _keywords = new HashSet<string>(
				("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double " +
				"else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is " +
				"lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
				"sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe " +
				"ushort using virtual void volatile while").Split(' '));
			// a name made into a PascalCase identifier, for the names built from a symbol's: ParseXxx, EvaluateXxx and the like
			static string _Ident(string name)
			{
				var s = MethodNames.Identifier(name);
				return _keywords.Contains(s) ? "@" + s : s;
			}
			// a name used as written when it's a valid identifier, with @ in front of a keyword; otherwise made into one
			static string _VerbatimIdent(string name)
			{
				var valid = 0 < name.Length && (char.IsLetter(name[0]) || '_' == name[0]) && name.All(ch => char.IsLetterOrDigit(ch) || '_' == ch);
				if (!valid) return _Ident(name);
				return _keywords.Contains(name) ? "@" + name : name;
			}

			// Symbol constants keep the symbol's name as written, so a grammar's typedef_name is typedef_name in C#.
			// Words that aren't keywords but that C# reads as something else where the constants are used, in
			// patterns such as "is (a or b)", in case labels and in declarations, also get an @ in front:
			// "not" and "_" are pattern syntax, nint and nuint are types, and partial is a modifier
			static readonly HashSet<string> _contextualKeywords = new HashSet<string>(new[] { "not", "_", "nint", "nuint", "partial" });
			// the name a symbol constant starts from: the symbol's own, with anything that can't be in an identifier
			// (only possible for a grammar built in code) made a _, and no @. Not recased.
			static string _ConstBase(string name)
			{
				var sb = new StringBuilder(name.Length + 1);
				foreach (var ch in name)
				{
					if (0 == sb.Length && char.IsDigit(ch)) sb.Append('_');
					sb.Append(char.IsLetterOrDigit(ch) || '_' == ch ? ch : '_');
				}
				return 0 == sb.Length ? "symbol" : sb.ToString();
			}
			static string _ConstIdent(string name) => _keywords.Contains(name) || _contextualKeywords.Contains(name) ? "@" + name : name;

			// the messages naming the symbol constants raised, such as a constant renamed to stay clear of a member
			public List<GrammarMessage> Messages { get; } = new();
			string[] _MakeConstantNames()
			{
				// each name a constant can't take, with what has it, for the warning when one is renamed
				var reserved = new Dictionary<string, string>();
				void reserve(string what, IEnumerable<string> names)
				{
					foreach (var n in names)
					{
						var bare = n.StartsWith("@", StringComparison.Ordinal) ? n.Substring(1) : n;
						if (!reserved.ContainsKey(bare)) reserved.Add(bare, what);
					}
				}
				reserve("the parser class", new[] { _className });
				reserve("a member of the parser", new[]
				{
					"Parse", "Current", "Errors", "Peek", "Advance", "Consume", "Expect", "Error", "NewNode", "Fold",
					"Enter", "Leave", "ExpectEnd", "Run", "Tables", "SymbolCount", "Start", "ReadToken", "FromLexer", "EndToken",
					"AdaptivePredict", "Atn", "Classify", "Classified", "OnMatched", "CurrentUnclassified", "Previous", "Wrap",
					"Adopt", "EnterInlined", "LeaveInlined",
					"CreateLexerMode", "Tokenize", "TryParse", "s_tables", "s_atn",
					"GetHashCode", "ToString", "Equals", "GetType", "MemberwiseClone", "ReferenceEquals", "Finalize"
				});
				reserve("a member of the parser", _a.Grammar.SharedProductions.Select(p => _TryParseName(p.Name)));
				reserve("a member of the parser", _a.Grammar.Productions.Select(p => _MethodName(p.Name)));
				reserve("a member of the parser", _a.Decisions.Select(_PredictName));
				// the methods hooks call are members too
				reserve("a hook method", _Hooks().Select(h => h.Name));
				// the evaluator uses the same constant names, so they mustn't clash with its members either
				if (null != _evaluatorName)
				{
					reserve("the evaluator class", new[] { _evaluatorName });
					reserve("a member of the evaluator", new[] { "Evaluate", "State", "_PassThrough" });
					reserve("a member of the evaluator", _a.Grammar.Productions.Select(p => _EvaluateName(p.Name)));
				}
				// parameters and locals the code declares where it uses the constants, or that hooks and semantic actions
				// are given, would hide a constant of the same name
				reserve("a parameter or local in the synthesized code", new[]
				{
					"node", "parent", "returnState", "minLevel", "token", "previous", "tokens", "result", "errors", "parser",
					"state", "evaluator", "only", "child", "tokenize", "reader", "text", "position", "line", "column", "tabWidth"
				});
				// types the code names without qualifying them, which a constant of the same name would hide. dynamic
				// is here because C# reads it as the type in a pattern even written @dynamic
				reserve("a type the synthesized code uses", new[]
				{
					"System", "Parsley", "Runtime", "Token", "ParseNode", "ParseError", "ParseException", "ParserTables", "ParserBase",
					"LL1ParserBase", "LLkParserBase", "AllStarParserBase", "LexerMode", "LexerController", "LexerSwitch", "IslandRule",
					"TokenizeFunc", "IEnumerable", "IReadOnlyList", "IDictionary", "InvalidOperationException", "dynamic"
				});
				if (null != _o.ReservedNames) reserve("taken in the type the parser is nested in", _o.ReservedNames);
				var used = new Dictionary<string, string>(reserved);
				var result = new string[_s.Count];
				for (var i = 0; i < _s.Count; ++i)
				{
					if (i == _s.EndOfInput || i == _s.Error)
					{
						var fixedName = i == _s.EndOfInput ? "EndOfInput" : "ErrorSymbol";
						used[fixedName] = "the special symbol constant " + fixedName;
						result[i] = fixedName;
					}
				}
				for (var i = 0; i < _s.Count; ++i)
				{
					if (null != result[i]) continue;
					var symbol = _s.GetName(i);
					var baseName = _ConstBase(symbol);
					var name = baseName;
					var n = 2;
					while (used.ContainsKey(name)) name = baseName + (n++).ToString();
					used.Add(name, $"the constant for {symbol}");
					result[i] = _ConstIdent(name);
					if (name != baseName)
					{
						GrammarNode? at = _s.IsNonTerminal(i) ? _s.GetProduction(i) : _s.GetTerminal(i);
						Messages.Add(new GrammarMessage(ErrorLevel.Warning,
							$"The symbol constant for {symbol} is named {name}, since {baseName} is {used[baseName]}. Rename the symbol to keep its name", at));
					}
				}
				return result;
			}
			string _MethodName(string production) => _names.Parse(production);

			// every hook method the grammar names, with the declaration it gets: the parse-time interface the
			// other half of the partial class implements
			List<(string Name, string Declaration)> _Hooks()
			{
				var result = new List<(string, string)>();
				void add(string name, string decl)
				{
					if (!result.Contains((name, decl))) result.Add((name, decl));
				}
				foreach (var t in _a.Grammar.Terminals)
				{
					// a contextual terminal's when is asked by the parser; one with lexer= by the lexer controller, which has no parser
					if (null != t.When) add(t.When, null != t.Lexer ? $"private static partial bool {t.When}(Token token, Token previous);" : $"private partial bool {t.When}(Token token, Token previous);");
					if (null != t.Matched) add(t.Matched, $"partial void {t.Matched}(Token token);");
				}
				foreach (var p in _a.Grammar.Productions)
				{
					if (null != p.Entering) add(p.Entering, $"partial void {p.Entering}();");
					if (null != p.Parsed) add(p.Parsed, $"partial void {p.Parsed}(ParseNode node);");
				}
				return result;
			}
			string _EvaluateName(string production) => _names.Evaluate(production);
			string _TryParseName(string production) => _names.TryParse(production);
			string _PredictName(Decision d) => _names.Predict(d.Production.Name, d.Index);
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
			// C# ends a line, and so a comment, at any of these
			static string _Comment(string text) => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
				.Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');
			// how a symbol reads in a comment: as in an error message, but a literal escaped, so "\n" doesn't read as a space
			string _Shown(int id) => _s.GetTerminal(id)?.Literal is string literal ? GrammarAttribute.FormatValue(literal) : _s.Display(id);

			// a #line directive that maps the lines after it to a line of the grammar file node came from, so the
			// compiler's errors and the debugger point at the grammar. Null for a grammar with no file, or a node
			// with no location. Directives are written unindented.
			string? _LineDirective(GrammarNode? node) => _LineDirective(node, node?.Line ?? 0);
			string? _LineDirective(GrammarNode? node, int line)
			{
				if (null == node || 0 >= line || string.IsNullOrEmpty(node.Filename)) return null;
				var path = null != _o.LinePath ? _o.LinePath(node.Filename!) : node.Filename!;
				// a #line file name is read up to the next quote, with no escapes, so a path with a quote or a
				// line break can't be written
				if (string.IsNullOrEmpty(path) || -1 < path.IndexOfAny(new[] { '"', '\r', '\n' })) return null;
				return $"#line {line} \"{path}\"";
			}
			// writes a line copied in part from the grammar, mapped to where node is in it
			void _MappedLine(GrammarNode? node, string text)
			{
				var directive = _LineDirective(node);
				if (null != directive) _sb.AppendLine(directive);
				_Line(text);
				if (null != directive) _sb.AppendLine("#line default");
			}
			void _MappedOpen(GrammarNode? node, string text)
			{
				_MappedLine(node, text);
				_Line("{");
				++_indent;
			}
			// where a declaration naming the start production's type and state type comes from: its type attribute,
			// or its state attribute when it has no type
			static GrammarNode? _TypeSource(Production p) => p.Attributes.Find("type") ?? p.Attributes.Find("state");
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
				// a symbol constant can be named like one of these too, now that constants keep their symbols' names
				while (_sets.ContainsValue(name) || "s_set" == name || _constNames.Contains(name)) name = baseName + (n++).ToString();
				_sets.Add(key, name);
				_setList.Add((name, sorted, string.Join(", ", sorted.Select(_Shown))));
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
				_indent = _o.Indent;
				if (!_o.Nested) _Header(source);
				_Line("/// <summary>");
				_Line($"/// Parses {g.StartSymbol} with {_EngineDescription()}. Synthesized from {source}.");
				_Line("/// </summary>");
				_Open($"{_AccessPrefix}partial class {_className} : {_engine.BaseClassName()}");
				_ParserBody(source);
				return _sb.ToString();
			}

			// a nested class is private; the outer type exposes what it needs to
			string _Access => _o.Nested ? _o.NestedAccess ?? "private" : _o.Public ? "public" : "internal";
			// the access and a space, or nothing when the classes leave it to another part
			string _AccessPrefix => 0 == _Access.Length ? "" : _Access + " ";
			// the grammar's tokens come from a lexer controller, which needs to know about its islands and other lexers
			bool _HasLexerMode => _a.Grammar.IsCompound || _a.Grammar.SharedProductions.Any();

			List<string> _Usings() => _UsingSources().Select(u => u.Namespace).ToList();
			// the usings, each with the @using it comes from, if any
			List<(string Namespace, UsingDirective? Source)> _UsingSources()
			{
				var g = _a.Grammar;
				var usings = new List<(string Namespace, UsingDirective? Source)>();
				if (null != _evaluatorName) usings.Add(("System", null));
				usings.Add(("System.Collections.Generic", null));
				usings.Add(("Parsley.Runtime", null));
				// @using is for semantic actions, so it only applies when there's an evaluator; validation warns
				// otherwise. One the file already has is skipped.
				if (null != _evaluatorName)
					foreach (var u in g.Usings)
						if (!usings.Any(x => x.Namespace == u.Namespace))
							usings.Add((u.Namespace, u));
				return usings;
			}
			// the lines that write the usings, each from the grammar mapped to its @using
			List<string> _UsingLines() => _Capture(() =>
			{
				var saved = _indent;
				_indent = 0;
				foreach (var (ns, source) in _UsingSources())
					_MappedLine(source, $"using {ns};");
				_indent = saved;
			}).Replace("\r\n", "\n").Split('\n').Where(l => 0 < l.Length).ToList();

			public SynthesizedParser Result(string code)
			{
				var start = _a.Grammar.GetProduction(_a.Grammar.StartSymbol!)!;
				var constants = new List<SynthesizedConstant>();
				for (var i = 0; i < _s.Count; ++i)
					constants.Add(new SynthesizedConstant(_const[i], i, _s.IsNonTerminal(i) ? null : null != _s.GetTerminal(i)?.Literal ? _Shown(i) : null));
				return new SynthesizedParser(code, _Usings(), _className, _evaluatorName,
					null != _evaluatorName ? start.Type ?? "object" : null,
					null != _evaluatorName ? (null == start.StateType ? "object? state = null" : $"{start.StateType} state") : null,
					constants, _engine, _a.Grammar.IsSubordinate, _a.Grammar.IsCompound, _HasLexerMode,
					_a.Grammar.SharedProductions.Select(p => _TryParseName(p.Name)).ToList(), Messages,
					_UsingLines(), null != _evaluatorName ? _LineDirective(_TypeSource(start)) : null);
			}

			void _Header(string source)
			{
				var g = _a.Grammar;
				_Line("// <auto-generated>");
				_Line($"// Synthesized by Parsley from {source}. Changes are lost when it is synthesized again;");
				_Line("// add members in another part of this partial class instead.");
				_Line("// </auto-generated>");
				_Line("#nullable enable");
				foreach (var u in _UsingLines())
					_Line(u);
				_Line();
				var ns = _o.Namespace ?? g.Namespace;
				if (null != ns)
				{
					_Line($"namespace {ns};");
					_Line();
				}
			}

			void _ParserBody(string source)
			{
				var g = _a.Grammar;
				_Line("#region Symbols");
				for (var i = 0; i < _s.Count; ++i)
				{
					var comment = _s.IsNonTerminal(i) ? "" : null != _s.GetTerminal(i)?.Literal ? $" // {_Shown(i)}" : "";
					_Line($"public const int {_const[i]} = {i};{comment}");
				}
				_Line($"public const int SymbolCount = {_s.Count};");
				_Line("#endregion");
				_Line();
				_Line($"public {_className}(IEnumerable<Token> tokens) : base(tokens, s_tables{(_allStar ? ", s_atn" : "")}) {{ }}");
				// a subordinate grammar is only parsed through its shared productions
				if (!g.IsSubordinate)
				{
					_Line();
					_Line("/// <summary>");
					_Line($"/// Parses a complete {g.StartSymbol} and returns its tree. Problems are reported in <see cref=\"ParserBase.Errors\"/>.");
					_Line("/// </summary>");
					_Open("public static bool TryParse(IEnumerable<Token> tokens,out ParseNode result, out IReadOnlyList<ParseError> errors)");
					_Line($"var parser = new {_className}(tokens);");
					// an operator rule's method takes the level it starts at, so it's called rather than passed
					var startIsOperators = null != g.GetProduction(g.StartSymbol!)!.Operators;
					_Line(_allStar
						? $"result = parser.Run(() => parser.{_MethodName(g.StartSymbol!)}(-1));"
						: startIsOperators ? $"result = parser.Run(() => parser.{_MethodName(g.StartSymbol!)}());"
						: $"result = parser.Run(parser.{_MethodName(g.StartSymbol!)});");
					_Line($"errors = parser.Errors;");
					_Line($"return parser.Errors.Count == 0;");
					_Close();

					_Open("public static ParseNode Parse(IEnumerable<Token> tokens)");
					_Open($"if(!TryParse(tokens, out var result, out var errors))");
					_Line("throw new ParseException(errors);");
					_Close();
					_Line("return result;");
					_Close();
				}
				_SharedEntries();
				if (_HasLexerMode)
				{
					_Line();
					_LexerMode();
				}
				// with no class around it, the parser lexes through a controller itself
				if (g.IsCompound && !_o.Nested && !g.IsSubordinate)
				{
					_Line();
					_CompoundEntries();
				}

                foreach (var p in g.Productions)
				{
					// an operator form's own production is parsed by its operator rule
					if (null != p.OperatorOwner) continue;
					_Line();
					if (null != p.Operators) _OperatorProduction(p);
					else _Production(p);
				}
				foreach (var d in _predictors.ToList())
				{
					_Line();
					_Predictor(d);
				}
				_ParseHooks();
				_Line();
				_Tables();
				_Close();
				if (null != _evaluatorName)
				{
					_Line();
					_Evaluator(source);
				}
			}

			// for each shared production, a static entry point that parses exactly it: what an island's controller calls
			void _SharedEntries()
			{
				foreach (var p in _a.Grammar.SharedProductions)
				{
					_Line();
					_Line("/// <summary>");
					_Line($"/// Parses a complete {p.Name}, as an island of another grammar, and returns its tree. Problems are reported in <paramref name=\"errors\"/>.");
					_Line("/// </summary>");
					_Open($"public static bool {_TryParseName(p.Name)}(IEnumerable<Token> tokens, out ParseNode result, out IReadOnlyList<ParseError> errors)");
					_Line($"var parser = new {_className}(tokens);");
					_Line(_allStar
						? $"result = parser.Run(() => parser.{_MethodName(p.Name)}(-1));"
						: $"result = parser.Run(() => parser.{_MethodName(p.Name)}());");
					_Line("errors = parser.Errors;");
					_Line("return parser.Errors.Count == 0;");
					_Close();
				}
			}

			// the lexer ids of the grammar's own terminals: their parser ids less the first terminal's
			string _LexerId(string terminal)
			{
				var id = _s.GetId(terminal);
				return $"{_const[id]} - {_const[_s.NonTerminalCount]}";
			}
			readonly Dictionary<string, GrammarAnalysis> _externals = new();
			GrammarAnalysis _External(ExternalGrammar x)
			{
				if (!_externals.TryGetValue(x.Alias, out var a))
				{
					a = GrammarAnalysis.Analyze(x.Grammar!);
					_externals.Add(x.Alias, a);
				}
				return a;
			}
			string _LexerType(string? alias) => null != _o.LexerType ? _o.LexerType(alias) : DefaultLexerType(_a.Grammar, alias);
			string _ExternalParser(string alias) => null != _o.ExternalParser ? _o.ExternalParser(alias) : DefaultExternalParser(_a.Grammar, alias);

			// what a lexer controller needs to lex this grammar with the lexer it's given: where islands start and
			// end, and which terminals another lexer produces
			void _LexerMode()
			{
				var g = _a.Grammar;
				var islands = g.Terminals.Where(t => t.IsIsland).ToList();
				var switches = g.Terminals.Where(t => null != t.Lexer && !t.IsIsland).ToList();
				_Line("/// <summary>");
				_Line("/// How a LexerController lexes this grammar with the given lexer: its islands and the terminals other lexers produce");
				_Line("/// </summary>");
				_Open("public static LexerMode CreateLexerMode(TokenizeFunc tokenize)");
				if (0 == islands.Count && 0 == switches.Count)
				{
					_Line("return new LexerMode(tokenize);");
					_Close();
					return;
				}
				_Line("return new LexerMode(tokenize,");
				++_indent;
				if (0 < islands.Count)
				{
					_Line("islands:");
					_Line("[");
					++_indent;
					for (var i = 0; i < islands.Count; ++i)
					{
						var t = islands[i];
						var (x, p) = g.ResolveEntry(t);
						var inner = _External(x!);
						var parser = _ExternalParser(x!.Alias);
						string innerId(string attr)
						{
							var name = t.Attributes.GetString(attr);
							if (null == name) return "-1";
							var term = Grammar.ResolveExternalTerminal(x, name.Trim())!;
							var id = inner.Symbols.GetId(term.Name) - inner.Symbols.NonTerminalCount;
							return $"{id} /* {x.Alias}.{term.Name} */";
						}
						_Line($"// {_Comment(XbnfWriter.Write(t))}");
						_Line("new IslandRule(");
						++_indent;
						_Line($"terminal: {_LexerId(t.Name)},");
						_Line($"after: [{string.Join(", ", t.GetNames("after").Select(n => _LexerId(g.FindTerminal(n)!.Name)))}],");
						_Line($"inner: {parser}.CreateLexerMode({_LexerType(x.Alias)}.Tokenize),");
						_Line($"parse: {parser}.{MethodNames.For(inner.Grammar).TryParse(p!.Name)},");
						_Line($"nest: {innerId("nest")},");
						_Line($"pop: {innerId("pop")},");
						var startsWith = t.GetNames("startsWith");
						if (0 < startsWith.Count)
						{
							_Line($"popOnError: {(t.PopOnError ? "true" : "false")},");
							_Line($"startsWith: [{string.Join(", ", startsWith.Select(n => { var term = Grammar.ResolveExternalTerminal(x, n)!; return $"{inner.Symbols.GetId(term.Name) - inner.Symbols.NonTerminalCount} /* {x.Alias}.{term.Name} */"; }))}])" + (i < islands.Count - 1 ? "," : ""));
						}
						else
							_Line($"popOnError: {(t.PopOnError ? "true" : "false")})" + (i < islands.Count - 1 ? "," : ""));
						--_indent;
					}
					--_indent;
					_Line("]" + (0 < switches.Count ? "," : ");"));
				}
				if (0 < switches.Count)
				{
					_Line("switches:");
					_Line("[");
					++_indent;
					for (var i = 0; i < switches.Count; ++i)
					{
						var t = switches[i];
						// its id in its own lexer: its place among the terminals that lexer produces
						var lexedAs = g.Terminals.Where(u => u.Lexer == t.Lexer && !u.IsIsland).ToList().IndexOf(t);
						_Line($"// {_Comment(XbnfWriter.Write(t))}");
						_Line("new LexerSwitch(");
						++_indent;
						_Line($"terminal: {_LexerId(t.Name)},");
						_Line($"on: [{string.Join(", ", t.GetNames("on").Select(n => _LexerId(g.FindTerminal(n)!.Name)))}],");
						_Line($"tokenize: {_LexerType(t.Lexer)}.Tokenize,");
						_Line($"lexedAs: {lexedAs}" + (null != t.When ? "," : ")" + (i < switches.Count - 1 ? "," : "")));
						if (null != t.When)
							_Line($"when: {t.When})" + (i < switches.Count - 1 ? "," : ""));
						--_indent;
					}
					--_indent;
					_Line("]);");
				}
				--_indent;
				_Close();
			}

			// a compound grammar's parser lexes through a controller, so it has the entry points that take text
			void _CompoundEntries()
			{
				var lexer = _LexerType(null);
				const string args = "long position = 0, int line = 1, int column = 1, int tabWidth = 4";
				_Line("/// <summary>");
				_Line($"/// Lexes text with {lexer}, and with the lexers of its islands and other terminals where they take over");
				_Line("/// </summary>");
				_Line($"public static IEnumerable<Token> Tokenize(global::System.IO.TextReader reader, {args}) => new LexerController(CreateLexerMode({lexer}.Tokenize), reader, position, line, column, tabWidth);");
				_Line("/// <summary>");
				_Line($"/// Lexes text with {lexer}, and with the lexers of its islands and other terminals where they take over");
				_Line("/// </summary>");
				_Line($"public static IEnumerable<Token> Tokenize(string text, {args}) => new LexerController(CreateLexerMode({lexer}.Tokenize), text, position, line, column, tabWidth);");
				if (_a.Grammar.IsSubordinate) return;
				_Line("/// <summary>");
				_Line("/// Lexes and parses what a reader reads, and returns the tree. Throws a ParseException with every problem found if there were any.");
				_Line("/// </summary>");
				_Line("public static ParseNode Parse(global::System.IO.TextReader reader) => Parse(Tokenize(reader));");
				_Line("/// <summary>");
				_Line("/// Lexes and parses text, and returns the tree. Throws a ParseException with every problem found if there were any.");
				_Line("/// </summary>");
				_Line("public static ParseNode Parse(string text) => Parse(Tokenize(text));");
			}

			string _EngineDescription()
			{
				switch (_engine)
				{
					case ParserEngine.LL1: return "one token of lookahead (LL(1))";
					case ParserEngine.LLk: return 1 < _a.Depth ? $"up to {_a.Depth} tokens of lookahead (LL({_a.Depth}))" : "one token of lookahead, on the LL(k) base";
					default:
						var n = _a.AdaptiveDecisions.Count;
						return $"adaptive prediction (ALL(*)) for {n} decision{(1 == n ? "" : "s")}";
				}
			}

			#region Evaluator
			// the evaluator walks a finished parse tree: one EvaluateXxx per production that has a node,
			// running its semantic action, or passing through to its one non-terminal child when it has none
			void _Evaluator(string source)
			{
				var g = _a.Grammar;
				var start = g.GetProduction(g.StartSymbol!)!;
				var evaluated = g.Productions.Where(p => !p.IsCollapsed).ToList();
				var startType = start.Type ?? "object";
				// a typed state has to be passed: null isn't a valid default for every type
				var stateType = start.StateType ?? "object?";
				var stateParam = null == start.StateType ? "object? state = null" : $"{stateType} state";
				_Line("/// <summary>");
				_Line($"/// Evaluates {g.StartSymbol} parse trees from <see cref=\"{_className}\"/> with the semantic actions in {source}.");
				_Line("/// </summary>");
				_Open($"{_AccessPrefix}partial class {_evaluatorName}");
				_Line("#region Symbols");
				for (var i = 0; i < _s.Count; ++i)
				{
					var comment = _s.IsNonTerminal(i) ? "" : null != _s.GetTerminal(i)?.Literal ? $" // {_Shown(i)}" : "";
					_Line($"protected const int {_const[i]} = {i};{comment}");
				}
				_Line("#endregion");
				_Line();
				_Line("/// <summary>");
				_Line("/// The state passed to Evaluate, for the semantic actions to use");
				_Line("/// </summary>");
				var stateSource = start.Attributes.Find("state");
				_MappedLine(stateSource, $"protected {stateType} State {{ get; }}");
				_Line();
				_MappedLine(stateSource, $"protected {_evaluatorName}({stateType} state) {{ State = state; }}");
				_Line();
				_Line("/// <summary>");
				_Line($"/// Evaluates a parse tree from <see cref=\"{_className}\"/>, starting at its {g.StartSymbol} root");
				_Line("/// </summary>");
				_Line("/// <param name=\"node\">The root of the tree</param>");
				_Line("/// <param name=\"state\">Anything the semantic actions need, available to them as State</param>");
				_MappedOpen(_TypeSource(start), $"public static {startType} Evaluate(ParseNode node, {stateParam})");
				if (g.SharedProductions.Any(p => p != start))
				{
					// a tree can also be an island's, rooted at one of the shared productions
					_Line($"var evaluator = new {_evaluatorName}(state);");
					_Line($"if ({_const[_s.GetId(start.Name)]} == node.SymbolId) return evaluator.{_EvaluateName(start.Name)}(node);");
					_Line($"return {("object" == startType ? "" : $"({startType})")}evaluator.Evaluate(node);");
				}
				else
					_Line($"return new {_evaluatorName}(state).{_EvaluateName(start.Name)}(node);");
				_Close();

				_Line();
				_Line("/// <summary>");
				_Line("/// Evaluates a node of any non-terminal, for when a child can be one of several");
				_Line("/// </summary>");
				_Open("protected object Evaluate(ParseNode node)");
				_Open("switch (node.SymbolId)");
				foreach (var p in evaluated)
				{
					_Line($"case {_const[_s.GetId(p.Name)]}:");
					++_indent;
					// a nullable type can return null, which object can't say
					_Line($"return {_EvaluateName(p.Name)}(node){(null != p.Type && p.Type.TrimEnd().EndsWith("?") ? "!" : "")};");
					--_indent;
				}
				_Close();
				_Line("throw new InvalidOperationException($\"{node.Symbol} can't be evaluated\");");
				_Close();

				if (evaluated.Any(p => null == p.Action))
				{
					_Line();
					_Line("// for a production with no semantic action: its node evaluates to its one non-terminal child, terminals aside");
					_Open("object _PassThrough(ParseNode node)");
					_Line("ParseNode? only = null;");
					_Open("foreach (var child in node.Children)");
					_Line("if (child.IsTerminal) continue;");
					_Line("if (null != only) throw new InvalidOperationException($\"{node.Symbol} has no semantic action and more than one non-terminal child, so it can't be evaluated\");");
					_Line("only = child;");
					_Close();
					_Line("if (null == only) throw new InvalidOperationException($\"{node.Symbol} has no semantic action and no non-terminal child, so it can't be evaluated\");");
					_Line("return Evaluate(only);");
					_Close();
				}

				foreach (var p in evaluated)
				{
					var type = p.Type ?? "object";
					_Line();
					var written = p.Operators?.Text ?? (null != p.OperatorOwner ? p.OperatorOwner.Forms.First(f => f.Production == p).Text + ";" : null) ?? XbnfWriter.Write(p, false);
					_Line($"// {_Comment(written)}");
					_MappedOpen(p.Attributes.Find("type"), $"protected {type} {_EvaluateName(p.Name)}(ParseNode node)");
					if (null != p.Operators)
					{
						// nodes of an operator form with an action of its own go to it; the rest are evaluated here
						var own = p.Operators.Forms.Where(f => null != f.Production?.Action).Select(f => f.Production!).Distinct().ToList();
						if (0 < own.Count)
						{
							_Open("switch (node.SymbolId)");
							foreach (var q in own)
							{
								_Line($"case {_const[_s.GetId(q.Name)]}:");
								++_indent;
								_Line($"return {_Cast(q.Type, p.Type)}{_EvaluateName(q.Name)}(node);");
								--_indent;
							}
							_Close();
						}
					}
					if (null != p.OperatorOwner && null == p.Action)
					{
						// no action of its own: the operator rule's action evaluates it
						var rule = p.OperatorOwner.Production;
						_Line($"return {_Cast(rule.Type, p.Type)}{_EvaluateName(rule.Name)}(node);");
					}
					else if (null == p.Action)
						_Line(null == p.Type ? "return _PassThrough(node);" : $"return ({type})_PassThrough(node);");
					else
						_ActionBody(p.Action);
					_Close();
				}
				_Close();
			}

			// a cast from one evaluate method's type to another's, when they're written differently. Parsley doesn't
			// know C#'s conversions, so it casts and lets the compiler say whether that's allowed
			static string _Cast(string? from, string? to)
			{
				static string norm(string? t) => string.Concat((t ?? "object").Where(c => !char.IsWhiteSpace(c)));
				return norm(from) == norm(to) ? "" : $"({to ?? "object"})";
			}

			// the action's code, re-indented to sit in its method. code with a string literal that spans
			// lines goes in exactly as written, because re-indenting it would change the string. Either way it's
			// mapped to its lines in the grammar with #line, which counts from the line of the opening brace, and
			// every line of the action is written, blank ones included, so they stay in step
			void _ActionBody(SemanticAction action)
			{
				if (action.HasMultilineLiteral)
				{
					// a blank line right after the opening brace or right before the closing one can't be
					// inside a string, so those can go
					var code = action.Code;
					var line = action.Line;
					var nl = code.IndexOf('\n');
					if (-1 < nl && 0 == code.Substring(0, nl).Trim().Length) { code = code.Substring(nl + 1); ++line; }
					nl = code.LastIndexOf('\n');
					if (-1 < nl && 0 == code.Substring(nl + 1).Trim().Length) code = code.Substring(0, nl + 1);
					var mapped = _LineDirective(action, line);
					if (null != mapped) _sb.AppendLine(mapped);
					_sb.Append(code);
					if (!code.EndsWith("\n")) _sb.AppendLine();
					if (null != mapped) _sb.AppendLine("#line default");
					return;
				}
				var lines = action.Code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
				// anything on the same line as the opening brace has no indentation to go by
				var first = lines[0].Trim();
				lines.RemoveAt(0);
				while (0 < lines.Count && 0 == lines[lines.Count - 1].Trim().Length)
					lines.RemoveAt(lines.Count - 1);
				var directive = _LineDirective(action, 0 < first.Length ? action.Line : action.Line + 1);
				if (null != directive) _sb.AppendLine(directive);
				if (0 < first.Length) _Line(first);
				string? common = null;
				foreach (var line in lines)
				{
					if (0 == line.Trim().Length) continue;
					var lead = line.Substring(0, line.Length - line.TrimStart().Length);
					if (null == common) { common = lead; continue; }
					var n = 0;
					while (n < common.Length && n < lead.Length && common[n] == lead[n]) ++n;
					common = common.Substring(0, n);
				}
				foreach (var line in lines)
					_Line(0 == line.Trim().Length ? "" : line.Substring(common!.Length).TrimEnd());
				if (null != directive) _sb.AppendLine("#line default");
			}
			#endregion

			void _Production(Production p)
			{
				var id = _s.GetId(p.Name);
				var rewritten = p.Expression.Descendants().OfType<Repeat>().Any(r => r.LeftFold);
				_Line($"// {_Comment(XbnfWriter.Write(p, false))}" + (rewritten ? " (left recursion rewritten as a loop)" : ""));
				var enter = _allStar ? $"Enter({_const[id]}, returnState);" : $"Enter({_const[id]});";
				var retParam = _allStar ? "int returnState" : "";
				if (p.IsCollapsed)
				{
					_Open($"void {_MethodName(p.Name)}(ParseNode parent{(_allStar ? ", " : "")}{retParam})");
					_Line(enter);
					if (null != p.Entering) _Line($"{p.Entering}();");
					_Expr(p.Expression, "parent", null, p, true);
					if (null != p.Parsed) _Line($"{p.Parsed}(parent);");
					_Line("Leave();");
					_Close();
				}
				else
				{
					_Open($"ParseNode {_MethodName(p.Name)}({retParam})");
					_Line(enter);
					_Line($"var node = NewNode({_const[id]});");
					if (null != p.Entering) _Line($"{p.Entering}();");
					_Expr(p.Expression, "node", null, p, true);
					if (null != p.Parsed) _Line($"{p.Parsed}(node);");
					_Line("Leave();");
					_Line("return node;");
					_Close();
				}
			}

			// an operator rule, parsed by precedence climbing: an operand (an atom, or a prefix form and its operand),
			// then a loop that applies infix and postfix forms for as long as they bind at least as tightly as
			// minLevel. A form's operand is parsed by a call that starts at the form's level, so it takes the
			// operators that bind tighter and leaves the rest to this loop.
			void _OperatorProduction(Production p)
			{
				var rule = p.Operators!;
				var id = _s.GetId(p.Name);
				_Line($"// {_Comment(rule.Text)}");
				_Line("// an operator rule, parsed by precedence climbing. minLevel is the loosest operator this call may apply:");
				_Line("// a looser one is left for the call that's waiting for this operand");
				var enter = _allStar ? $"Enter({_const[id]}, returnState);" : $"Enter({_const[id]});";
				_Open($"ParseNode {_MethodName(p.Name)}({(_allStar ? "int returnState, " : "")}int minLevel = 0)");
				_Line(enter);
				_Line($"var node = NewNode({_const[id]});");
				if (null != p.Entering) _Line($"{p.Entering}();");
				_Expr(rule.Primary, "node", null, p, true);
				if (null != rule.Loop)
				{
					_Expr(rule.Loop, "node", null, p, false);
					--_indent;
					_Line("done:");
					++_indent;
				}
				if (null != p.Parsed) _Line($"{p.Parsed}(node);");
				_Line("Leave();");
				_Line("return node;");
				_Close();
			}

			static string _Describe(OperatorForm f) => f.Kind switch
			{
				OperatorKind.Atom => "an atom",
				OperatorKind.Prefix => $"prefix, level {f.Level}",
				OperatorKind.Postfix => $"postfix, level {f.Level}",
				_ => $"infix, level {f.Level}, {(f.IsRightAssociative ? "right" : "left")} associative"
			};

			// one operator form, chosen by the decision it's an alternative of
			void _OperatorForm(OperatorForm f, string node, IReadOnlyList<int>? known, Production p)
			{
				_Line($"// {_Comment(f.Text)} ({_Describe(f)})");
				var symbol = _const[_s.GetId(f.Symbol)];
				switch (f.Kind)
				{
					case OperatorKind.Atom:
						_Expr(f.Option, node, known, p, false, true);
						return;
					case OperatorKind.Prefix:
						// node is new, made for the operand; a form with a production of its own makes its own instead
						if (null != f.Production) _Line($"{node} = NewNode({symbol});");
						break;
					default:
						_Line($"if ({f.Level} < minLevel) goto done;");
						_Line($"{node} = Wrap({symbol}, {node});");
						break;
				}
				foreach (var item in f.Operator)
				{
					_Expr(item, node, known, p, false);
					if (null != known && 0 < known.Count && item is SymbolRef sr && _s.GetId(sr.Name!) == known[0])
						known = known.Skip(1).ToList();
					else
						known = null;
				}
				if (null == f.Operand) return;
				// left associative: the operand stops at this level's own operators, so they apply to this node next
				var level = OperatorKind.Infix == f.Kind && !f.IsRightAssociative ? f.Level + 1 : f.Level;
				var ret = _allStar ? _a.Atn.GetReturnState(f.Operand).ToString() + ", " : "";
				_Line($"{node}.Add({_MethodName(p.Name)}({ret}{level}));");
			}

			// emits the code for an expression. known: the token already established as Current, if any
			void _Expr(Expression e, string node, IReadOnlyList<int>? known, Production p, bool isRoot, bool asWritten = false)
			{
				if (!asWritten && _a.GetOperatorForm(e) is OperatorForm form)
				{
					_OperatorForm(form, node, known, p);
					return;
				}
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
									_Line(_nameConsumes ? $"Consume({node}); // {_Shown(id)}" : $"Consume({node});");
								else
									_Line($"Expect({_const[id]}, {node});");
							}
							else
							{
								// an ALL(*) parser tells each production where its caller continues
								var ret = _allStar ? _a.Atn.GetReturnState(sr).ToString() : "";
								if (_s.GetProduction(id)!.IsCollapsed)
									_Line($"{_MethodName(sr.Name!)}({node}{(_allStar ? ", " : "")}{ret});");
								else
									_Line($"{node}.Add({_MethodName(sr.Name!)}({ret}));");
							}
							break;
						}
					case InlinedProduction ip:
						_Inlined(ip, node, known);
						break;
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

			// a production inlined by left factoring, parsed in place as its method would parse it: its node (which
			// takes over the tokens factoring moved in front of the decision), its hooks, and its place in recovery
			void _Inlined(InlinedProduction ip, string node, IReadOnlyList<int>? known)
			{
				var q = _a.Grammar.GetProduction(ip.Production)!;
				var id = _const[_s.GetId(q.Name)];
				_Line($"// {q.Name}, inlined" + (0 < ip.Back ? $" after its first {(1 == ip.Back ? "token" : ip.Back.ToString() + " tokens")}" : ""));
				if (q.IsCollapsed)
				{
					_Line($"EnterInlined({id});");
					_Expr(ip.Body, node, known, q, ip.Whole);
					if (null != q.Parsed) _Line($"{q.Parsed}({node});");
					_Line("LeaveInlined();");
					return;
				}
				var inner = $"__inlined{++_inlinedCount}";
				_Line("{");
				++_indent;
				_Line($"var {inner} = Adopt({id}, {node}, {ip.Back}, {ip.Adopt});");
				_Line($"EnterInlined({id});");
				_Expr(ip.Body, inner, known, q, ip.Whole);
				if (null != q.Parsed) _Line($"{q.Parsed}({inner});");
				_Line("LeaveInlined();");
				_Line($"{node}.Add({inner});");
				_Close();
			}
			int _inlinedCount;

			static IReadOnlyList<int>? _Single(IReadOnlyList<int> tokens) => 1 == tokens.Count ? tokens : null;

			void _Decision(Decision d, string node, Production p, bool isRoot)
			{
				// a decision that is the whole body of a loop or optional is already described by its parent's comment
				var parent = _a.GetParent(d.Node);
				var operatorLoop = _a.IsOperatorLoop(d.Node);
				if (operatorLoop)
					_Line("// apply infix and postfix operators while the next token starts one");
				else if (!isRoot && !(parent is Repeat || parent is OptionalExpression) && !(null != _a.GetOperatorForm(d.Node)))
					_Line($"// {_Comment(d.Node.ToString())}");
				if (null != d.AppliedPolicy && !operatorLoop)
					_Line($"// ambiguous within {d.MaxK} tokens; resolved {(d.Kind == DecisionKind.Alternation ? "by taking the first alternative" : "greedy" == d.AppliedPolicy ? "greedily" : "lazily")}");
				if (d.IsAdaptive)
				{
					if (0 == d.Depth)
						_Line($"// no fixed lookahead decides this; ALL(*) looks as far as it has to");
					// a prediction has matched at least the first token, so a branch that can only start one way has it.
					// The default branch is where input no branch matches goes, unless one got further into it
					var args = 0 <= d.DefaultBranch ? $"{d.AdaptiveIndex}, {d.DefaultBranch}" : d.AdaptiveIndex.ToString();
					_DeepDecision(d, node, p, isRoot, $"AdaptivePredict({args})", i => _Single(d.TokensOf(i)));
					return;
				}
				if (1 < d.Depth)
				{
					_predictors.Add(d);
					// as with adaptive prediction, only the first token is certain: on input no branch matches all of,
					// the predictor returns the branch that matched the most, which checks the rest itself
					_DeepDecision(d, node, p, isRoot, _PredictName(d) + "()", i => _Single(d.TokensOf(i)));
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
				// a rule that can't match any finite input has no tokens to name
				return 0 == tokens.Count ? $"Error({node}, {desc});" : $"Error({node}, {desc}, {string.Join(", ", tokens.Select(t => _const[t]))});";
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

			// a decision made by a method that returns the branch: a synthesized predictor or adaptive prediction.
			// known: the tokens a branch is certain to start with once chosen
			void _DeepDecision(Decision d, string node, Production p, bool isRoot, string call, Func<int, IReadOnlyList<int>?> known)
			{
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
							_Expr(a.Options[i], node, known(i), p, false);
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
						_Expr(o.Body, node, known(0), p, false);
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

			// the overrides that connect contextual terminals and matched hooks, and the partial methods they call
			void _ParseHooks()
			{
				var hooks = _Hooks();
				if (0 == hooks.Count) return;
				var contextual = _a.Grammar.Terminals.Where(t => t.IsContextual).ToList();
				if (0 < contextual.Count)
				{
					_Line();
					_Line("// contextual terminals: what a token is depends on what has been parsed, so it's decided as the parser looks at it");
					_Open("protected override int Classify(Token token, Token previous)");
					_Open("switch (token.SymbolId)");
					foreach (var g in contextual.GroupBy(t => t.ContextualBase!))
					{
						_Line($"case {_const[_s.GetId(g.Key)]}:");
						++_indent;
						foreach (var t in g)
							_Line($"if ({t.When}(token, previous)) return {_const[_s.GetId(t.Name)]};");
						_Line("break;");
						--_indent;
					}
					_Close();
					_Line("return token.SymbolId;");
					_Close();
				}
				var matched = _a.Grammar.Terminals.Where(t => null != t.Matched).ToList();
				if (0 < matched.Count)
				{
					_Line();
					_Open("protected override void OnMatched(Token token)");
					_Open("switch (token.SymbolId)");
					foreach (var t in matched)
					{
						_Line($"case {_const[_s.GetId(t.Name)]}:");
						++_indent;
						_Line($"{t.Matched}(token);");
						_Line("break;");
						--_indent;
					}
					_Close();
					_Close();
				}
				_Line();
				_Line("// implemented in another part of this partial class: the parse-time hooks the grammar names");
				foreach (var (_, decl) in hooks) _Line(decl);
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
				_Line($"// looks {d.Depth} tokens ahead and returns {what}. On input none matches, the one that matched the most,");
				_Line("// so it reports the error where the input goes wrong, or -1 if none matches the first token");
				_Open($"int {_PredictName(d)}()");
				var strings = new List<(LookaheadString S, int Branch)>();
				for (var i = 0; i < d.Branches.Count; ++i)
					foreach (var x in d.Branches[i])
						strings.Add((x, i));
				_Trie(strings, 0, d.DefaultBranch);
				_Line("return -1;");
				_Close();
			}

			// a switch on the token at the level, for lookahead strings that agree up to it. Below the first level,
			// one that matches nothing returns the default branch if it's among those that got this far, else the
			// earliest of them, as adaptive prediction does
			void _Trie(List<(LookaheadString S, int Branch)> strings, int level, int defaultBranch)
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
					// it returns whatever the token after this is
					_Trie(gr.ToList(), level + 1, defaultBranch);
					--_indent;
				}
				_Close();
				if (0 < level)
				{
					var alive = strings.Select(x => x.Branch).Distinct().ToList();
					_Line($"return {(alive.Contains(defaultBranch) ? defaultBranch : alive.Min())};");
				}
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
				var contextualIds = Enumerable.Range(0, _s.Count).Where(i => _s.GetTerminal(i)?.IsContextual ?? false).Select(i => _const[i]).ToList();
				if (0 < contextualIds.Count)
				{
					_Line("error: ErrorSymbol,");
					_Line($"contextualTerminals: [{string.Join(", ", contextualIds)}]);");
				}
				else
					_Line("error: ErrorSymbol);");
				--_indent;
				if (_allStar) _Atn();
				_Line("#endregion");
			}

			void _Atn()
			{
				var atn = _a.Atn;
				_Line("// the grammar as a transition network, for adaptive prediction. rules: each non-terminal's start and");
				_Line("// stop state. states: each state's transition count, then for each transition a kind (0: no input,");
				_Line("// to a; 1: match terminal a, to b; 2: call non-terminal a, returning to b) and its a and b.");
				_Line("// decisions: the state where each adaptive decision chooses its branch.");
				_Line("static readonly Atn s_atn = new(");
				++_indent;
				_Line($"startRule: {_const[atn.StartRule]},");
				_Line("endOfInput: EndOfInput,");
				_Numbers("rules", atn.Rules, ",");
				_Numbers("states", atn.SerializeStates(), ",");
				if (0 < atn.EntryRules.Length)
				{
					_Numbers("decisions", _a.AdaptiveDecisions.Select(d => atn.GetDecisionState(d.Node)).ToList(), ",");
					_Line($"// the shared productions: parses of islands start with them, so the input can end after them");
					_Line($"entryRules: [{string.Join(", ", atn.EntryRules.Select(r => _const[r]))}]);");
				}
				else
					_Numbers("decisions", _a.AdaptiveDecisions.Select(d => atn.GetDecisionState(d.Node)).ToList(), ");");
				--_indent;
			}

			// a long list of numbers, wrapped
			void _Numbers(string name, IReadOnlyList<int> values, string end)
			{
				if (24 >= values.Count)
				{
					_Line($"{name}: [{string.Join(", ", values)}]{end}");
					return;
				}
				_Line($"{name}:");
				_Line("[");
				++_indent;
				for (var i = 0; i < values.Count; i += 24)
				{
					var last = i + 24 >= values.Count;
					_Line(string.Join(", ", values.Skip(i).Take(24)) + (last ? "" : ","));
				}
				--_indent;
				_Line("]" + end);
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
				foreach (var e in p.Expression.OwnDescendants())
					addSync(e.Attributes.Get("sync"));
				return result;
			}
		}
	}
}
