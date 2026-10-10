// Runtime support for parsers synthesized by Parsley. Include this file alongside the synthesized
// parser; it has no other dependencies. Requires C# 12.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Parsley.Runtime
{
    /// <summary>
    /// A token from whatever lexer feeds the parser. Lexers drop hidden terminals (whitespace, comments)
    /// before the parser sees them.
    /// </summary>
    /// <remarks>
    /// Coming from the lexer, SymbolId counts terminals from 0 in the grammar's terminal order, hidden and
    /// implicit ones included; a negative id means the lexer couldn't match the input. The parser adds
    /// <see cref="ParserTables.TerminalStart"/> as each token arrives, so inside the parser (Current, Peek,
    /// ParseNode.SymbolId) ids match the synthesized parser's constants.
    /// </remarks>
    public readonly record struct Token(int SymbolId, string Value, int Line, int Column, long Position);

    /// <summary>
    /// Wraps an enumerator so code can look ahead of the current item without advancing.
    /// </summary>
    public sealed class LookAheadEnumerator<T> : IEnumerator<T>
    {
        readonly IEnumerator<T> _inner;
        readonly List<T> _buffer = new();
        int _head; // index of the current item in _buffer
        bool _started;
        bool _innerDone;

        public LookAheadEnumerator(IEnumerable<T> items)
        {
            _inner = (items ?? throw new ArgumentNullException(nameof(items))).GetEnumerator();
        }
        public T Current
        {
            get
            {
                if (!_started || _head >= _buffer.Count)
                    throw new InvalidOperationException("The enumerator is not positioned on an item");
                return _buffer[_head];
            }
        }
        object? IEnumerator.Current => Current;
        bool _Fill(int count)
        {
            // make sure there are count items from _head on
            while (_buffer.Count - _head < count)
            {
                if (_innerDone || !_inner.MoveNext())
                {
                    _innerDone = true;
                    return false;
                }
                _buffer.Add(_inner.Current);
            }
            return true;
        }
        public bool MoveNext()
        {
            if (_started)
                ++_head;
            _started = true;
            // drop consumed items now and then so the buffer doesn't grow with the input
            if (_head > 64 && _head * 2 > _buffer.Count)
            {
                _buffer.RemoveRange(0, _head);
                _head = 0;
            }
            return _Fill(1);
        }
        /// <summary>
        /// Looks n items past the current one (n = 1 is the next item) without advancing
        /// </summary>
        public bool TryPeek(int n, out T item)
        {
            if (0 > n) throw new ArgumentOutOfRangeException(nameof(n));
            if (!_started) throw new InvalidOperationException("Call MoveNext first");
            if (_Fill(n + 1))
            {
                item = _buffer[_head + n];
                return true;
            }
            item = default!;
            return false;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// A node in the parse tree: a terminal (with its text), a non-terminal, or an error node holding skipped tokens
    /// </summary>
    public sealed class ParseNode
    {
        public ParseNode(int symbolId, string symbol, string? value, bool isTerminal, int line, int column, long position)
        {
            SymbolId = symbolId;
            Symbol = symbol;
            Value = value;
            IsTerminal = isTerminal;
            Line = line;
            Column = column;
            Position = position;
        }
        public int SymbolId { get; }
        public string Symbol { get; }
        /// <summary>
        /// The matched text, for terminals
        /// </summary>
        public string? Value { get; }
        public bool IsTerminal { get; }
        /// <summary>
        /// True for a node holding tokens skipped during error recovery
        /// </summary>
        public bool IsError { get; init; }
        public int Line { get; }
        public int Column { get; }
        public long Position { get; }
        public List<ParseNode> Children { get; } = new();
        public void Add(ParseNode child) => Children.Add(child);
        /// <summary>
        /// The terminal text under this node, joined with spaces
        /// </summary>
        public string Text
        {
            get
            {
                if (IsTerminal) return Value ?? "";
                var sb = new StringBuilder();
                foreach (var c in Children)
                {
                    var t = c.Text;
                    if (0 == t.Length) continue;
                    if (0 < sb.Length) sb.Append(' ');
                    sb.Append(t);
                }
                return sb.ToString();
            }
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            _Write(sb, 0);
            return sb.ToString();
        }
        void _Write(StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2);
            sb.Append(Symbol);
            if (IsTerminal)
            {
                sb.Append(' ');
                sb.Append('"');
                sb.Append(Value);
                sb.Append('"');
            }
            sb.AppendLine();
            foreach (var c in Children)
                c._Write(sb, depth + 1);
        }
    }
    public sealed class ParseException : Exception
    {
        public IReadOnlyCollection<ParseError> Errors { get; }
        public ParseException(IReadOnlyCollection<ParseError> errors) : base("One or more parse errors occured.") { Errors = errors; }

    }
    public sealed record ParseError(string Message, int Line, int Column, long Position)
    {
        public override string ToString() => $"{Message} at line {Line}, column {Column}";
    }

    /// <summary>
    /// What a synthesized parser tells the runtime about its symbols
    /// </summary>
    public sealed class ParserTables
    {
        public ParserTables(int terminalStart, string[] symbolNames, string[] displayNames, int[] collapsedTerminals, int[][] recoverySets, int[] syncTerminals, int endOfInput, int error)
        {
            TerminalStart = terminalStart;
            SymbolNames = symbolNames;
            DisplayNames = displayNames;
            RecoverySets = recoverySets;
            EndOfInput = endOfInput;
            Error = error;
            Collapsed = Set(symbolNames.Length, collapsedTerminals);
            Sync = Set(symbolNames.Length, syncTerminals);
            Recovery = new bool[recoverySets.Length][];
            for (var i = 0; i < recoverySets.Length; ++i)
                Recovery[i] = Set(symbolNames.Length, recoverySets[i]);
        }
        public string[] SymbolNames { get; }
        /// <summary>
        /// How each symbol reads in an error message
        /// </summary>
        public string[] DisplayNames { get; }
        /// <summary>
        /// For each non-terminal, the terminals where error recovery may stop while it is being parsed
        /// </summary>
        public int[][] RecoverySets { get; }
        public int EndOfInput { get; }
        public int Error { get; }
        internal bool[] Collapsed { get; }
        internal bool[] Sync { get; }
        internal bool[][] Recovery { get; }
        /// <summary>
        /// The parser id of the first terminal (the number of non-terminals). Lexer ids are offset by this.
        /// </summary>
        public int TerminalStart { get; }

        /// <summary>
        /// A lookup table with true at each of the given ids
        /// </summary>
        public static bool[] Set(int count, params int[] ids)
        {
            var result = new bool[count];
            foreach (var id in ids)
                result[id] = true;
            return result;
        }
    }

    /// <summary>
    /// The base of every synthesized parser: token access, tree building and error recovery.
    /// Errors are recorded in <see cref="Errors"/> and parsing continues.
    /// </summary>
    public abstract class ParserBase
    {
        readonly LookAheadEnumerator<Token> _tokens;
        readonly ParserTables _tables;
        readonly List<int> _active = new();
        readonly List<ParseError> _errors = new();
        Token _current;
        long _index;
        long _lastErrorIndex = -1;
        int _errorsAtIndex;
        bool _quiet;

        protected ParserBase(IEnumerable<Token> tokens, ParserTables tables)
        {
            _tables = tables ?? throw new ArgumentNullException(nameof(tables));
            _tokens = new LookAheadEnumerator<Token>(_FromLexer(tokens ?? throw new ArgumentNullException(nameof(tokens))));
            _current = _tokens.MoveNext() ? _tokens.Current : _EndToken(default);
        }

        /// <summary>
        /// Problems found while parsing, in order
        /// </summary>
        public IReadOnlyList<ParseError> Errors => _errors;
        protected ParserTables Tables => _tables;

        // converts lexer ids (terminals counted from 0) to parser ids, once, as tokens arrive. Anything the
        // lexer couldn't match, or an id past the last terminal, becomes #ERROR so the parser reports it.
        IEnumerable<Token> _FromLexer(IEnumerable<Token> tokens)
        {
            foreach (var t in tokens)
            {
                var id = t.SymbolId + _tables.TerminalStart;
                if (0 > t.SymbolId || id >= _tables.EndOfInput)
                    id = _tables.Error;
                yield return t with { SymbolId = id };
            }
        }

        Token _EndToken(Token last) => new Token(_tables.EndOfInput, "", last.Line, last.Column + (last.Value?.Length ?? 0), last.Position + (last.Value?.Length ?? 0));

        /// <summary>
        /// The token being looked at
        /// </summary>
        protected Token Current => _current;
        /// <summary>
        /// The token n places after Current, or end of input
        /// </summary>
        protected Token Peek(int n) => _tokens.TryPeek(n, out var t) ? t : _EndToken(_current);
        protected void Advance()
        {
            if (_current.SymbolId == _tables.EndOfInput) return;
            var last = _current;
            _current = _tokens.MoveNext() ? _tokens.Current : _EndToken(last);
            ++_index;
        }
        ParseNode _Leaf(Token t) => new ParseNode(t.SymbolId, _tables.SymbolNames[t.SymbolId], t.Value, true, t.Line, t.Column, t.Position);
        /// <summary>
        /// Adds Current to the tree (unless its terminal is collapsed) and moves past it
        /// </summary>
        protected void Consume(ParseNode parent)
        {
            if (!_tables.Collapsed[_current.SymbolId])
                parent.Add(_Leaf(_current));
            Advance();
            _quiet = false; // a matched token ends the quiet period after an error
        }
        /// <summary>
        /// Consumes the given terminal, or reports it missing and recovers
        /// </summary>
        protected void Expect(int symbolId, ParseNode parent)
        {
            if (_current.SymbolId == symbolId)
            {
                Consume(parent);
                return;
            }
            _Report(parent, $"Expected {_tables.DisplayNames[symbolId]}");
            _Recover(parent, symbolId);
            if (_current.SymbolId == symbolId)
                Consume(parent);
        }
        /// <summary>
        /// A new node for a non-terminal, located at Current
        /// </summary>
        protected ParseNode NewNode(int symbolId)
        {
            var result = new ParseNode(symbolId, _tables.SymbolNames[symbolId], null, false, _current.Line, _current.Column, _current.Position);
            _root ??= result;
            return result;
        }
        /// <summary>
        /// For a left-associative loop: wraps what was parsed so far in a new node of the same symbol
        /// </summary>
        protected ParseNode Fold(ParseNode node)
        {
            var result = new ParseNode(node.SymbolId, node.Symbol, null, false, node.Line, node.Column, node.Position);
            result.Add(node);
            return result;
        }
        /// <summary>
        /// Reports that none of the expected symbols was found, then recovers
        /// </summary>
        protected void Error(ParseNode parent, string? expected, params int[] symbols)
        {
            if (null == expected)
            {
                var names = new List<string>();
                foreach (var s in symbols)
                    if (!names.Contains(_tables.DisplayNames[s]))
                        names.Add(_tables.DisplayNames[s]);
                expected = 1 == names.Count ? names[0]
                    : 2 == names.Count ? $"{names[0]} or {names[1]}"
                    : string.Join(", ", names.GetRange(0, names.Count - 1)) + ", or " + names[names.Count - 1];
            }
            _Report(parent, $"Expected {expected}");
            _Recover(parent, -1);
        }
        void _Report(ParseNode parent, string message)
        {
            if (_lastErrorIndex == _index)
            {
                // several failures at one token mean recovery stopped there and the parse isn't moving:
                // on the third, skip the token so a loop can't spin forever
                if (3 <= ++_errorsAtIndex)
                {
                    // at end of input there's nothing to skip, so a recursive rule could descend forever: give up
                    if (_current.SymbolId == _tables.EndOfInput)
                        throw new _Abandon();
                    var skipped = new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                    skipped.Add(_Leaf(_current));
                    parent.Add(skipped);
                    Advance();
                }
                return;
            }
            _lastErrorIndex = _index;
            _errorsAtIndex = 1;
            // follow-on errors before anything has matched again are usually noise from the first one
            if (_quiet) return;
            _quiet = true;
            // a literal terminal's display name is already its text in quotes; others show the text after the name
            var display = _tables.DisplayNames[_current.SymbolId];
            var found = _current.SymbolId == _tables.EndOfInput ? "end of input"
                : _current.SymbolId == _tables.Error ? $"unrecognized input \"{_current.Value}\""
                : display.StartsWith('"') ? display
                : $"{display} {_current.Value}";
            _errors.Add(new ParseError($"{message} but found {found}", _current.Line, _current.Column, _current.Position));
        }
        bool _IsRecoveryPoint(int id)
        {
            if (id == _tables.EndOfInput || _tables.Sync[id]) return true;
            for (var i = _active.Count - 1; i >= 0; --i)
                if (_tables.Recovery[_active[i]][id]) return true;
            return false;
        }
        // skips tokens until one that the current construct or something enclosing it can continue with
        void _Recover(ParseNode parent, int stopAt)
        {
            ParseNode? err = null;
            while (_current.SymbolId != stopAt && !_IsRecoveryPoint(_current.SymbolId))
            {
                err ??= new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                err.Add(_Leaf(_current));
                Advance();
            }
            if (null != err)
                parent.Add(err);
        }
        /// <summary>
        /// Marks the start of a non-terminal, for error recovery
        /// </summary>
        protected void Enter(int nonTerminal) => _active.Add(nonTerminal);
        protected void Leave() => _active.RemoveAt(_active.Count - 1);
        sealed class _Abandon : Exception { }
        ParseNode? _root;

        /// <summary>
        /// Runs the start production and reports anything left over after it. If the input ends in a way
        /// recovery can't get past, returns the tree built so far.
        /// </summary>
        protected ParseNode Run(Func<ParseNode> start)
        {
            try
            {
                var root = start();
                ExpectEnd(root);
                return root;
            }
            catch (_Abandon)
            {
                _active.Clear();
                return _root!;
            }
        }
        /// <summary>
        /// Reports anything left over after the start symbol
        /// </summary>
        protected void ExpectEnd(ParseNode root)
        {
            if (_current.SymbolId == _tables.EndOfInput) return;
            _Report(root, "Expected end of input");
            ParseNode? err = null;
            while (_current.SymbolId != _tables.EndOfInput)
            {
                err ??= new ParseNode(_tables.Error, _tables.SymbolNames[_tables.Error], null, false, _current.Line, _current.Column, _current.Position) { IsError = true };
                err.Add(_Leaf(_current));
                Advance();
            }
            if (null != err) root.Add(err);
        }
    }
}