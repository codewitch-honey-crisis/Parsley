using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// Reads XBNF. A rule whose right side is a lone "literal" or 'pattern' declares a terminal, as does one marked
	/// &lt;terminal&gt;, an island (a rule with entry= and no body) or a contextual terminal; anything else declares a
	/// non-terminal. Supports @import, @class, @evaluator, @using, @maxk, @factor, @ignoreCase, @external and @lexer directives,
	/// attributed groups: <c>( … )&lt;attrs&gt;</c>, <c>[ … ]&lt;attrs&gt;</c>, <c>{ … }&lt;attrs&gt;</c>, and semantic actions:
	/// <c>Name= expression =&gt; { C# };</c>
	/// </summary>
	public sealed class XbnfParser
	{
		sealed class SyntaxError : Exception
		{
			public SyntaxError(string message, int line, int column, long position) : base(message)
			{
				Line = line; Column = column; Position = position;
			}
			public int Line { get; }
			public int Column { get; }
			public long Position { get; }
		}

		readonly ParseContext _pc;
		readonly string? _filename;
		readonly Grammar _grammar;
		readonly List<GrammarMessage> _messages;
		readonly HashSet<string> _imported;
		readonly Func<string, string> _readFile;
		// the grammars being read through @external, outermost first, to catch one that's its own external
		readonly IReadOnlyList<string> _externals;

		XbnfParser(ParseContext pc, string? filename, Grammar grammar, List<GrammarMessage> messages, HashSet<string> imported, Func<string, string> readFile, IReadOnlyList<string> externals)
		{
			_externals = externals;
			_readFile = readFile;
			_pc = pc;
			_filename = filename;
			_grammar = grammar;
			_messages = messages;
			_imported = imported;
		}

		public static IList<GrammarMessage> TryParse(string text, string? filename, out Grammar? grammar)
			=> TryParse(text, filename, File.ReadAllText, out grammar);
		/// <summary>
		/// Parses a grammar, reading the files it @imports with <paramref name="readFile"/>, which takes a full
		/// path and returns the text or throws
		/// </summary>
		public static IList<GrammarMessage> TryParse(string text, string? filename, Func<string, string> readFile, out Grammar? grammar)
		{
			var messages = new List<GrammarMessage>();
			var g = new Grammar { Filename = filename };
			var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (null != filename)
				imported.Add(Path.GetFullPath(filename));
			_Read(ParseContext.Create(text), filename, g, messages, imported, readFile);
			messages.AddRange(g.ClassifyTerminals());
			messages.AddRange(g.ResolveTerminals());
			grammar = g;
			return messages;
		}
        public static IList<GrammarMessage> TryReadFrom(TextReader reader, string? filename, out Grammar? grammar)
        {
            var messages = new List<GrammarMessage>();
            var g = new Grammar { Filename = filename };
            var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (null != filename)
                imported.Add(Path.GetFullPath(filename));
            _Read(ParseContext.CreateFrom(reader), filename, g, messages, imported, File.ReadAllText);
            messages.AddRange(g.ClassifyTerminals());
            messages.AddRange(g.ResolveTerminals());
            grammar = g;
            return messages;
        }
        public static IList<GrammarMessage> TryReadFrom(string filename, out Grammar? grammar)
		{
			using StreamReader sr = new StreamReader(filename,true);
            return TryReadFrom(sr,filename, out grammar);
		}
        public static IList<GrammarMessage> TryReadFrom(TextReader reader, out Grammar? grammar)
        {
            return TryReadFrom( reader, null, out grammar);
        }

        static void _Read(ParseContext pc, string? filename, Grammar g, List<GrammarMessage> messages, HashSet<string> imported, Func<string, string> readFile, IReadOnlyList<string>? externals = null)
		{
			externals ??= null != filename ? new[] { Path.GetFullPath(filename) } : Array.Empty<string>();
			var r = new XbnfParser(pc, filename, g, messages, imported, readFile, externals);
			r._ReadAll();
		}

		#region Lexical helpers
		int Cur => _pc.Current;
		void _Advance() => _pc.Advance();
		SyntaxError _Error(string message) => new SyntaxError(message, _pc.Line, _pc.Column, _pc.Position);
		void _SkipWs()
		{
			_pc.EnsureStarted();
			while (-1 != Cur)
			{
				if (char.IsWhiteSpace((char)Cur))
				{
					_Advance();
					continue;
				}
				if ('/' == Cur)
				{
					var la = _pc.Peek(1);
					if ('/' == la)
					{
						while (-1 != Cur && '\n' != Cur) _Advance();
						continue;
					}
					if ('*' == la)
					{
						_Advance(); _Advance();
						while (-1 != Cur && !('*' == Cur && '/' == _pc.Peek(1))) _Advance();
						if (-1 == Cur) throw _Error("Unterminated block comment");
						_Advance(); _Advance();
						continue;
					}
				}
				break;
			}
		}
		void _Expect(char ch)
		{
			_SkipWs();
			if (ch != Cur)
				throw _Error(-1 == Cur ? $"Unexpected end of input, expecting '{ch}'" : $"Unexpected '{(char)Cur}', expecting '{ch}'");
			_Advance();
		}
		static bool _IsIdStart(int ch) => -1 != ch && (char.IsLetter((char)ch) || '_' == ch);
		static bool _IsIdPart(int ch) => -1 != ch && (char.IsLetterOrDigit((char)ch) || '_' == ch);
		string _ReadIdentifier()
		{
			_SkipWs();
			if (!_IsIdStart(Cur))
				throw _Error(-1 == Cur ? "Unexpected end of input, expecting a name" : $"Unexpected '{(char)Cur}', expecting a name");
			var sb = new StringBuilder();
			while (_IsIdPart(Cur))
			{
				sb.Append((char)Cur);
				_Advance();
			}
			return sb.ToString();
		}
		string _ReadString()
		{
			// "..." with C-style escapes
			_Expect('"');
			var sb = new StringBuilder();
			while ('"' != Cur)
			{
				if (-1 == Cur || '\n' == Cur) throw _Error("Unterminated string");
				if ('\\' == Cur)
				{
					_Advance();
					switch (Cur)
					{
						case 'n': sb.Append('\n'); break;
						case 'r': sb.Append('\r'); break;
						case 't': sb.Append('\t'); break;
						case 'f': sb.Append('\f'); break;
						case 'v': sb.Append('\v'); break;
						case 'b': sb.Append('\b'); break;
						case '0': sb.Append('\0'); break;
						case '\\': sb.Append('\\'); break;
						case '"': sb.Append('"'); break;
						case '\'': sb.Append('\''); break;
						case '/': sb.Append('/'); break;
						case 'u':
							var hex = new StringBuilder();
							for (var i = 0; i < 4; ++i)
							{
								_Advance();
								if (-1 == Cur || !Uri.IsHexDigit((char)Cur)) throw _Error("Invalid \\u escape");
								hex.Append((char)Cur);
							}
							sb.Append((char)int.Parse(hex.ToString(), NumberStyles.HexNumber));
							break;
						default:
							throw _Error(-1 == Cur ? "Unterminated string" : $"Unknown escape \\{(char)Cur}");
					}
					_Advance();
					continue;
				}
				sb.Append((char)Cur);
				_Advance();
			}
			_Advance();
			return sb.ToString();
		}
		string _ReadPattern()
		{
			// '...' kept as written for the lexer; only \' is unescaped
			_Expect('\'');
			var sb = new StringBuilder();
			while ('\'' != Cur)
			{
				if (-1 == Cur || '\n' == Cur) throw _Error("Unterminated pattern");
				if ('\\' == Cur)
				{
					_Advance();
					if ('\'' == Cur)
						sb.Append('\'');
					else
					{
						sb.Append('\\');
						if (-1 == Cur) throw _Error("Unterminated pattern");
						sb.Append((char)Cur);
					}
					_Advance();
					continue;
				}
				sb.Append((char)Cur);
				_Advance();
			}
			_Advance();
			return sb.ToString();
		}
		object _ReadValue()
		{
			_SkipWs();
			if ('"' == Cur) return _ReadString();
			if ('-' == Cur || '.' == Cur || (-1 != Cur && char.IsDigit((char)Cur)))
			{
				var sb = new StringBuilder();
				while (-1 != Cur && ('-' == Cur || '+' == Cur || '.' == Cur || 'e' == Cur || 'E' == Cur || char.IsDigit((char)Cur)))
				{
					sb.Append((char)Cur);
					_Advance();
				}
				var s = sb.ToString();
				if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
				if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
				throw _Error($"Invalid number \"{s}\"");
			}
			if (_IsIdStart(Cur))
			{
				var id = _ReadIdentifier();
				switch (id)
				{
					case "true": return true;
					case "false": return false;
					case "null": throw _Error("null is not a valid attribute value");
					default: return id; // bare words are strings: policy=greedy
				}
			}
			throw _Error(-1 == Cur ? "Unexpected end of input, expecting a value" : $"Unexpected '{(char)Cur}', expecting a value");
		}
		void _ReadAttributes(AttributeList attrs)
		{
			_Expect('<');
			_SkipWs();
			if ('>' == Cur)
			{
				_Advance();
				return;
			}
			while (true)
			{
				_SkipWs();
				int line = _pc.Line, col = _pc.Column;
				long pos = _pc.Position;
				var name = _ReadIdentifier();
				_SkipWs();
				object value = true;
				if ('=' == Cur)
				{
					_Advance();
					value = _ReadValue();
				}
				var a = new GrammarAttribute(name, value);
				a.SetLocation(line, col, pos, _filename);
				if (attrs.Contains(name))
					_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"Attribute \"{name}\" is specified more than once; the last one wins", a));
				attrs.Remove(name);
				attrs.Add(a);
				_SkipWs();
				if (',' == Cur)
				{
					_Advance();
					continue;
				}
				_Expect('>');
				return;
			}
		}
		#endregion

		#region Semantic actions
		// a semantic action is C#, read only far enough to find its closing brace: braces inside
		// strings, character literals, comments and interpolation holes don't count
		bool _AtArrow() => '=' == Cur && '>' == _pc.Peek(1);
		bool _multilineLiteral;
		SemanticAction _ReadAction()
		{
			// Cur is the opening {
			_Advance();
			_multilineLiteral = false;
			var sb = new StringBuilder();
			_ScanCode(sb, 1, false);
			return new SemanticAction(sb.ToString(), _multilineLiteral);
		}
		void _Take(StringBuilder sb)
		{
			if (-1 == Cur) throw _Error("Unterminated semantic action");
			sb.Append((char)Cur);
			_Advance();
		}
		int _CountRun(char ch, int from = 0)
		{
			var n = 0;
			while (ch == _pc.Peek(from + n)) ++n;
			return n;
		}
		// copies code up to the } that closes the current block or interpolation hole. closers is how many
		// braces close it (more than one only for a hole in a $$"""raw""" string). They're consumed, and
		// copied for a hole but not for the action's own closing brace
		void _ScanCode(StringBuilder sb, int closers, bool isHole)
		{
			var depth = 0;
			while (true)
			{
				switch (Cur)
				{
					case -1:
						throw _Error("Unterminated semantic action");
					case '{':
						++depth;
						_Take(sb);
						break;
					case '}':
						if (0 < depth)
						{
							--depth;
							_Take(sb);
							break;
						}
						for (var i = 0; i < closers; ++i)
						{
							if ('}' != Cur) throw _Error("Unterminated interpolation in a semantic action");
							if (isHole) sb.Append('}');
							_Advance();
						}
						return;
					case '/':
						if ('/' == _pc.Peek(1))
						{
							while (-1 != Cur && '\n' != Cur) _Take(sb);
						}
						else if ('*' == _pc.Peek(1))
						{
							_Take(sb); _Take(sb);
							while (!('*' == Cur && '/' == _pc.Peek(1))) _Take(sb);
							_Take(sb); _Take(sb);
						}
						else
							_Take(sb);
						break;
					case '\'':
						_Take(sb);
						while ('\'' != Cur)
						{
							if ('\n' == Cur) throw _Error("Unterminated character literal in a semantic action");
							if ('\\' == Cur) _Take(sb);
							_Take(sb);
						}
						_Take(sb);
						break;
					case '"':
						_ScanString(sb, false, 0);
						break;
					case '@':
						if ('"' == _pc.Peek(1))
						{
							_Take(sb);
							_ScanString(sb, true, 0);
						}
						else if ('$' == _pc.Peek(1) && '"' == _pc.Peek(2))
						{
							_Take(sb); _Take(sb);
							_ScanString(sb, true, 1);
						}
						else
							_Take(sb);
						break;
					case '$':
						{
							var dollars = _CountRun('$');
							var verbatim = '@' == _pc.Peek(dollars);
							if ('"' == _pc.Peek(dollars + (verbatim ? 1 : 0)))
							{
								for (var i = 0; i < dollars; ++i) _Take(sb);
								if (verbatim) _Take(sb);
								_ScanString(sb, verbatim, dollars);
							}
							else
								_Take(sb);
							break;
						}
					default:
						_Take(sb);
						break;
				}
			}
		}
		// copies a string literal starting at its opening quote. interpolation is the number of $ before it
		void _ScanString(StringBuilder sb, bool verbatim, int interpolation)
		{
			var quotes = _CountRun('"');
			if (3 <= quotes)
			{
				// """raw""": ends at a run of as many quotes; a hole opens with as many braces as there were $
				for (var i = 0; i < quotes; ++i) _Take(sb);
				while (true)
				{
					if (-1 == Cur) throw _Error("Unterminated raw string in a semantic action");
					if ('"' == Cur)
					{
						var run = _CountRun('"');
						for (var i = 0; i < run; ++i) _Take(sb);
						if (quotes <= run) return;
						continue;
					}
					if (0 < interpolation && '{' == Cur)
					{
						// in a run longer than the $ count, the extra braces come first, as content
						var braces = _CountRun('{');
						for (var i = 0; i < braces; ++i) _Take(sb);
						if (braces >= interpolation)
							_ScanCode(sb, interpolation, true);
						continue;
					}
					if ('\n' == Cur) _multilineLiteral = true;
					_Take(sb);
				}
			}
			_Take(sb); // the opening "
			while (true)
			{
				switch (Cur)
				{
					case -1:
						throw _Error("Unterminated string in a semantic action");
					case '"':
						_Take(sb);
						if (verbatim && '"' == Cur)
						{
							_Take(sb); // "" in a verbatim string
							break;
						}
						return;
					case '\\':
						if (!verbatim) _Take(sb);
						_Take(sb);
						break;
					case '{':
						_Take(sb);
						if (0 < interpolation)
						{
							if ('{' == Cur)
								_Take(sb); // {{ is a literal brace
							else
								_ScanCode(sb, 1, true);
						}
						break;
					case '}':
						_Take(sb);
						if (0 < interpolation && '}' == Cur) _Take(sb); // }} is a literal brace
						break;
					case '\n':
						if (!verbatim) throw _Error("Newline in a string in a semantic action");
						_multilineLiteral = true;
						_Take(sb);
						break;
					default:
						_Take(sb);
						break;
				}
			}
		}
		#endregion

		void _ReadAll()
		{
			_SkipWs();
			while (-1 != Cur)
			{
				try
				{
					_ReadStatement();
				}
				catch (SyntaxError ex)
				{
					_messages.Add(new GrammarMessage(ErrorLevel.Error, ex.Message, ex.Line, ex.Column, ex.Position, _filename));
					// resynchronize at the next ;
					while (-1 != Cur && ';' != Cur) _Advance();
					if (-1 != Cur) _Advance();
				}
				_SkipWs();
			}
		}

		void _ReadStatement()
		{
			_SkipWs();
			int line = _pc.Line, col = _pc.Column;
			long pos = _pc.Position;
			if ('@' == Cur)
			{
				_Advance();
				var name = _ReadIdentifier();
				_SkipWs();
				// @external and @lexer take an alias before their argument: @external Json "json.xbnf";
				string? alias = null;
				if (("external" == name || "lexer" == name) && _IsIdStart(Cur))
				{
					alias = _ReadIdentifier();
					_SkipWs();
				}
				// a number or a word can be written bare, as in @maxk 2; or @factor true;
				string arg;
				if (-1 != Cur && char.IsDigit((char)Cur))
				{
					var sb = new System.Text.StringBuilder();
					while (-1 != Cur && char.IsDigit((char)Cur))
					{
						sb.Append((char)Cur);
						_Advance();
					}
					arg = sb.ToString();
				}
				else if (_IsIdStart(Cur))
					arg = _ReadIdentifier();
				else
					arg = _ReadString();
				_SkipWs();
				_Expect(';');
				var at = new GrammarAttribute(name);
				at.SetLocation(line, col, pos, _filename);
				switch (name)
				{
					case "import":
						_Import(arg, at);
						break;
					case "external":
						if (null == alias)
							_messages.Add(new GrammarMessage(ErrorLevel.Error, $"@external expects an alias and a file, as in @external Json \"json.xbnf\";", at));
						else
							_External(alias, arg, line, col, pos);
						break;
					case "lexer":
						{
							var l = new LexerDirective(alias, arg);
							l.SetLocation(line, col, pos, _filename);
							_grammar.Lexers.Add(l);
							break;
						}
					case "namespace":
						// the namespace is given with the class now: @class "My.Parsers.MyParser";
						_messages.Add(new GrammarMessage(ErrorLevel.Error, $"@namespace is no longer supported: give the namespace in @class, as in @class \"{arg}.MyParser\"", at));
						break;
					case "class":
					case "evaluator":
						_grammar.Directives[name] = arg;
						_grammar.DirectiveLocations[name] = at;
						break;
					case "using":
						// can be given more than once; the namespace is checked when the grammar is validated
						var u = new UsingDirective(arg);
						u.SetLocation(line, col, pos, _filename);
						_grammar.Usings.Add(u);
						break;
					case "factor":
					case "ignoreCase":
						if ("true" != arg && "false" != arg)
							_messages.Add(new GrammarMessage(ErrorLevel.Error, $"@{name} expects true or false, not \"{arg}\"", at));
						else
						{
							_grammar.Directives[name] = arg;
							_grammar.DirectiveLocations[name] = at;
						}
						break;
					case "maxk":
						if (!int.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k) || 1 > k)
							_messages.Add(new GrammarMessage(ErrorLevel.Error, $"@maxk expects a whole number of at least 1, not \"{arg}\"", at));
						else
						{
							_grammar.Directives[name] = arg;
							_grammar.DirectiveLocations[name] = at;
						}
						break;
					default:
						_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"Unknown directive @{name}", at));
						_grammar.Directives[name] = arg;
						_grammar.DirectiveLocations[name] = at;
						break;
				}
				return;
			}
			var id = _ReadIdentifier();
			var attrs = new AttributeList();
			_SkipWs();
			if ('<' == Cur)
				_ReadAttributes(attrs);
			_SkipWs();
			if ('{' == Cur)
				throw _Error($"\"{id}\" has a code body. Virtual productions are not supported");
			// an island has no body: Headers<entry="Json.Object", after="addHeadersKeyword">;
			if (';' == Cur)
			{
				_Advance();
				if (!attrs.Contains("entry"))
					_messages.Add(new GrammarMessage(ErrorLevel.Error, $"\"{id}\" has no body; only an island (a rule with entry=) is declared without one", line, col, pos, _filename));
				var island = new Production(id, _Locate(new EmptyExpression(), line, col, pos));
				island.SetLocation(line, col, pos, _filename);
				island.Attributes.AddRange(attrs);
				_grammar.Productions.Add(island);
				return;
			}
			_Expect('=');
			var expr = _ReadAlternation();
			_SkipWs();
			SemanticAction? action = null;
			if (_AtArrow())
			{
				_Advance(); _Advance();
				_SkipWs();
				int aline = _pc.Line, acol = _pc.Column;
				long apos = _pc.Position;
				if ('{' != Cur)
					throw _Error(-1 == Cur ? "Unexpected end of input, expecting '{' to start the semantic action" : $"Unexpected '{(char)Cur}', expecting '{{' to start the semantic action");
				action = _ReadAction();
				action.SetLocation(aline, acol, apos, _filename);
			}
			_Expect(';');

			// every rule starts as a production; which ones are terminals is decided once everything,
			// imports included, has been read
			var p = new Production(id, expr) { Action = action };
			p.SetLocation(line, col, pos, _filename);
			p.Attributes.AddRange(attrs);
			_grammar.Productions.Add(p);
		}

		// reads an @external grammar on its own, the way it would be read by itself: its own imports, rules and
		// directives, none of them merged into this grammar. Only its errors are reported here; reading it by
		// itself reports the rest.
		void _External(string alias, string path, int line, int col, long pos)
		{
			var baseDir = null != _filename ? Path.GetDirectoryName(Path.GetFullPath(_filename)) : Environment.CurrentDirectory;
			var full = Path.GetFullPath(Path.Combine(baseDir ?? "", path));
			GrammarMessage error(string message) => new GrammarMessage(ErrorLevel.Error, message, line, col, pos, _filename);
			if (_externals.Contains(full, StringComparer.OrdinalIgnoreCase))
			{
				_messages.Add(error($"@external {alias} \"{path}\" leads back to a grammar that's already being read, which would embed it in itself"));
				_grammar.Externals.Add(_Located(new ExternalGrammar(alias, path, null), line, col, pos));
				return;
			}
			string text;
			try
			{
				text = _readFile(full);
			}
			catch (Exception ex)
			{
				_messages.Add(error($"Could not read @external {alias} \"{path}\": {ex.Message}"));
				_grammar.Externals.Add(_Located(new ExternalGrammar(alias, path, null), line, col, pos));
				return;
			}
			var messages = new List<GrammarMessage>();
			var sub = new Grammar { Filename = full };
			var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full };
			_Read(ParseContext.Create(text), full, sub, messages, imported, _readFile, _externals.Concat(new[] { full }).ToList());
			messages.AddRange(sub.ClassifyTerminals());
			messages.AddRange(sub.ResolveTerminals());
			// literals used inline get their implicit terminals, named as analysis names them, so islands can
			// refer to them: nest="Json.lbrace" for an inline "{"
			sub.ResolveLiterals();
			_messages.AddRange(messages.Where(m => ErrorLevel.Error == m.Level));
			_grammar.Externals.Add(_Located(new ExternalGrammar(alias, path, sub), line, col, pos));
		}
		T _Located<T>(T node, int line, int col, long pos) where T : GrammarNode
		{
			node.SetLocation(line, col, pos, _filename);
			return node;
		}

		void _Import(string path, GrammarNode at)
		{
			var baseDir = null != _filename ? Path.GetDirectoryName(Path.GetFullPath(_filename)) : Environment.CurrentDirectory;
			var full = Path.GetFullPath(Path.Combine(baseDir ?? "", path));
			if (!_imported.Add(full))
				return; // already imported (or a cycle)
			string text;
			try
			{
				text = _readFile(full);
			}
			catch (Exception ex)
			{
				_messages.Add(new GrammarMessage(ErrorLevel.Error, $"Could not import \"{path}\": {ex.Message}", at));
				return;
			}
			// imported productions keep their own file name; directives in imports don't override the importer's,
			// except @using, which adds to them
			var sub = new Grammar { Filename = full };
			_Read(ParseContext.Create(text), full, sub, _messages, _imported, _readFile, _externals);
			_grammar.Productions.AddRange(sub.Productions);
			_grammar.Terminals.AddRange(sub.Terminals);
			// like other directives, an import's @external and @lexer don't override the importer's
			foreach (var x in sub.Externals)
				if (null == _grammar.GetExternal(x.Alias)) _grammar.Externals.Add(x);
			foreach (var l in sub.Lexers)
				if (!_grammar.Lexers.Any(m => m.Alias == l.Alias)) _grammar.Lexers.Add(l);
			foreach (var d in sub.Directives)
				if (!_grammar.Directives.ContainsKey(d.Key))
				{
					_grammar.Directives.Add(d.Key, d.Value);
					if (sub.DirectiveLocations.TryGetValue(d.Key, out var dat))
						_grammar.DirectiveLocations[d.Key] = dat;
				}
			_grammar.Usings.AddRange(sub.Usings);
		}

		T _Locate<T>(T e, int line, int col, long pos) where T : Expression
		{
			e.SetLocation(line, col, pos, _filename);
			return e;
		}

		Expression _ReadAlternation()
		{
			_SkipWs();
			int line = _pc.Line, col = _pc.Column;
			long pos = _pc.Position;
			var options = new List<Expression> { _ReadSequence() };
			_SkipWs();
			while ('|' == Cur)
			{
				_Advance();
				options.Add(_ReadSequence());
				_SkipWs();
			}
			return 1 == options.Count ? options[0] : _Locate(new Alternation(options), line, col, pos);
		}

		Expression _ReadSequence()
		{
			_SkipWs();
			int line = _pc.Line, col = _pc.Column;
			long pos = _pc.Position;
			var items = new List<Expression>();
			while (true)
			{
				_SkipWs();
				if (-1 == Cur || '|' == Cur || ')' == Cur || ']' == Cur || '}' == Cur || ';' == Cur || _AtArrow())
					break;
				items.Add(_ReadPostfix());
			}
			if (0 == items.Count) return _Locate(new EmptyExpression(), line, col, pos);
			return 1 == items.Count ? items[0] : _Locate(new Sequence(items), line, col, pos);
		}

		Expression _ReadPostfix()
		{
			var e = _ReadPrimary();
			_SkipWs();
			if ('<' == Cur)
				_ReadAttributes(e.Attributes);
			return e;
		}

		Expression _ReadPrimary()
		{
			_SkipWs();
			int line = _pc.Line, col = _pc.Column;
			long pos = _pc.Position;
			switch (Cur)
			{
				case '"':
					return _Locate(SymbolRef.FromLiteral(_ReadString()), line, col, pos);
				case '\'':
					return _Locate(SymbolRef.FromPattern(_ReadPattern()), line, col, pos);
				case '(':
					{
						_Advance();
						var inner = _ReadAlternation();
						_Expect(')');
						if (inner is EmptyExpression)
							_Locate(inner, line, col, pos);
						return inner;
					}
				case '[':
					{
						_Advance();
						var inner = _ReadAlternation();
						_Expect(']');
						return _Locate(new OptionalExpression(inner), line, col, pos);
					}
				case '{':
					{
						_Advance();
						var inner = _ReadAlternation();
						_Expect('}');
						var min = 0;
						if ('+' == Cur)
						{
							_Advance();
							min = 1;
						}
						return _Locate(new Repeat(inner, min), line, col, pos);
					}
			}
			if (_IsIdStart(Cur))
				return _Locate(new SymbolRef(_ReadIdentifier()), line, col, pos);
			throw _Error(-1 == Cur ? "Unexpected end of input in expression" : $"Unexpected '{(char)Cur}' in expression");
		}
	}
}
