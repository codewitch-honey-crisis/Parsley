using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// Reads XBNF. A rule that references no symbols (only "literals" and 'patterns') declares a terminal,
	/// as does one marked &lt;terminal&gt;; anything else declares a non-terminal. Supports @import, @namespace and @class directives and
	/// attributed groups: <c>( … )&lt;attrs&gt;</c>, <c>[ … ]&lt;attrs&gt;</c>, <c>{ … }&lt;attrs&gt;</c>.
	/// </summary>
	public sealed class XbnfReader
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

		XbnfReader(ParseContext pc, string? filename, Grammar grammar, List<GrammarMessage> messages, HashSet<string> imported)
		{
			_pc = pc;
			_filename = filename;
			_grammar = grammar;
			_messages = messages;
			_imported = imported;
		}

		public static IList<GrammarMessage> TryParse(string text, string? filename, out Grammar? grammar)
		{
			var messages = new List<GrammarMessage>();
			var g = new Grammar { Filename = filename };
			var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (null != filename)
				imported.Add(Path.GetFullPath(filename));
			_Read(ParseContext.Create(text), filename, g, messages, imported);
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
            _Read(ParseContext.CreateFrom(reader), filename, g, messages, imported);
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

        static void _Read(ParseContext pc, string? filename, Grammar g, List<GrammarMessage> messages, HashSet<string> imported)
		{
			var r = new XbnfReader(pc, filename, g, messages, imported);
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
				var arg = _ReadString();
				_Expect(';');
				var at = new GrammarAttribute(name);
				at.SetLocation(line, col, pos, _filename);
				switch (name)
				{
					case "import":
						_Import(arg, at);
						break;
					case "namespace":
					case "class":
						_grammar.Directives[name] = arg;
						break;
					default:
						_messages.Add(new GrammarMessage(ErrorLevel.Warning, $"Unknown directive @{name}", at));
						_grammar.Directives[name] = arg;
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
			_Expect('=');
			var expr = _ReadAlternation();
			_Expect(';');

			// every rule starts as a production; which ones are terminals is decided once everything,
			// imports included, has been read
			var p = new Production(id, expr);
			p.SetLocation(line, col, pos, _filename);
			p.Attributes.AddRange(attrs);
			_grammar.Productions.Add(p);
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
				text = File.ReadAllText(full);
			}
			catch (Exception ex)
			{
				_messages.Add(new GrammarMessage(ErrorLevel.Error, $"Could not import \"{path}\": {ex.Message}", at));
				return;
			}
			// imported productions keep their own file name; directives in imports don't override the importer's
			var sub = new Grammar { Filename = full };
			_Read(ParseContext.Create(text), full, sub, _messages, _imported);
			_grammar.Productions.AddRange(sub.Productions);
			_grammar.Terminals.AddRange(sub.Terminals);
			foreach (var d in sub.Directives)
				if (!_grammar.Directives.ContainsKey(d.Key))
					_grammar.Directives.Add(d.Key, d.Value);
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
				if (-1 == Cur || '|' == Cur || ')' == Cur || ']' == Cur || '}' == Cur || ';' == Cur)
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
