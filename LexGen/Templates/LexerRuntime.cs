using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

// A source of code units for the matcher. Implemented by small structs so the JIT
// specializes Match<T> for each input kind (no interface dispatch in the inner loop).
internal interface ILexerUnitSource
{
    /// <summary>Code unit at offset from the start of the current token, or -1 at end of input.</summary>
    int Peek(int offset);
}

internal static class LexerRuntime
{
    internal const int DefaultTabWidth = 4;

    // Flat table layout (see Luthor's Compiler.cs):
    //   dfa[0]  newline code unit (for ^ and $), then per state:
    //   accept, bol, eol, n, n x (min, max, target)

    /// <summary>Longest match at the start of <paramref name="text"/>. Returns (symbol or -1, length in units).</summary>
    internal static (int Token, int Length) Match<T>(int[] dfa, ref T text, bool atLineStart) where T : struct, ILexerUnitSource
    {
        int state = 1, accept = -1, length = 0, i = 0;
        bool bol = atLineStart;
        while (true)
        {
            int c = text.Peek(i);                                                          // -1 at end of input
            if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                       // ^
            if ((c == -1 || c == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; // $
            if (dfa[state] != -1) { accept = dfa[state]; length = i; }
            if (c == -1) break;
            int next = -1, r = state + 4;                                                  // (min, max, target) triples, sorted
            for (int k = 0; k < dfa[state + 3] && c >= dfa[r]; k++, r += 3)
                if (c <= dfa[r + 1]) { next = dfa[r + 2]; break; }
            if (next == -1) break;
            state = next;
            bol = c == dfa[0];
            i++;
        }
        return (accept, length);
    }

    static void CheckArgs(int tabWidth, long position, int line, int column)
    {
        if (tabWidth < 1) throw new ArgumentOutOfRangeException(nameof(tabWidth), tabWidth, "Tab width must be at least 1.");
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position), position, "Position must be zero or greater.");
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line), line, "Line must be at least 1.");
        if (column < 1) throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be at least 1.");
    }

    // ---------------------------------------------------------------- string

    /// <summary>
    /// Tokenizes <paramref name="text"/>. Position is the zero-based code-unit offset of the token start;
    /// Line and Column are one-based and also refer to the token start.
    /// </summary>
    /// <param name="position">Position reported for the first unit of <paramref name="text"/>.</param>
    /// <param name="line">Line reported for the first unit of <paramref name="text"/>.</param>
    /// <param name="column">Column reported for the first unit of <paramref name="text"/>. The input is treated
    /// as starting at the beginning of a line (for ^) only when this is 1.</param>
    internal static IEnumerable<(long Position, int Line, int Column, int Symbol, string Text)> Tokenize(
        int[] dfa, string text, int tabWidth = DefaultTabWidth, long position = 0, int line = 1, int column = 1)
    {
        if (dfa is null) throw new ArgumentNullException(nameof(dfa));
        if (text is null) throw new ArgumentNullException(nameof(text));
        CheckArgs(tabWidth, position, line, column);
        return TokenizeString(dfa, text, tabWidth, position, line, column);
    }

    static IEnumerable<(long Position, int Line, int Column, int Symbol, string Text)> TokenizeString(
        int[] dfa, string text, int tabWidth, long position, int line, int column)
    {
        var lc = new LineColumnTracker(dfa[0], tabWidth, line, column);
        for (int i = 0; i < text.Length;)
        {
            var src = new StringSource(text, i);
            bool atLineStart = i == 0 ? column == 1 : text[i - 1] == dfa[0];
            var (tok, len) = Match(dfa, ref src, atLineStart);
            if (len == 0) { tok = -1; len = 1; }  // nothing matched (or only an empty match): skip one unit
            int tokLine = lc.Line, tokColumn = lc.Column;
            lc.Advance(text, i, len);
            yield return (position + i, tokLine, tokColumn, tok, text.Substring(i, len));
            i += len;
        }
    }

    readonly struct StringSource : ILexerUnitSource
    {
        readonly string _text; readonly int _pos;
        public StringSource(string text, int pos) { _text = text; _pos = pos; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : -1;
    }

    // ---------------------------------------------------------------- TextReader

    /// <summary>
    /// Tokenizes the contents of <paramref name="reader"/>. Position is the zero-based code-unit offset of the
    /// token start; Line and Column are one-based and also refer to the token start.
    /// </summary>
    /// <param name="position">Position reported for the first unit read from <paramref name="reader"/>.</param>
    /// <param name="line">Line reported for the first unit read from <paramref name="reader"/>.</param>
    /// <param name="column">Column reported for the first unit read from <paramref name="reader"/>. The input is
    /// treated as starting at the beginning of a line (for ^) only when this is 1.</param>
    internal static IEnumerable<(long Position, int Line, int Column, int Symbol, string Text)> Tokenize(
        int[] dfa, TextReader reader, int tabWidth = DefaultTabWidth, long position = 0, int line = 1, int column = 1)
    {
        if (dfa is null) throw new ArgumentNullException(nameof(dfa));
        if (reader is null) throw new ArgumentNullException(nameof(reader));
        CheckArgs(tabWidth, position, line, column);
        return TokenizeReader(dfa, reader, tabWidth, position, line, column);
    }

    static IEnumerable<(long Position, int Line, int Column, int Symbol, string Text)> TokenizeReader(
        int[] dfa, TextReader reader, int tabWidth, long position, int line, int column)
    {
        var window = new LexerCharWindow(reader);
        var lc = new LineColumnTracker(dfa[0], tabWidth, line, column);
        long pos = position;
        bool atLineStart = column == 1;
        while (window.Peek(0) != -1)
        {
            var src = new CharWindowSource(window);
            var (tok, len) = Match(dfa, ref src, atLineStart);
            if (len == 0) { tok = -1; len = 1; }
            atLineStart = window.Peek(len - 1) == dfa[0];
            string s = window.Take(len);
            int tokLine = lc.Line, tokColumn = lc.Column;
            lc.Advance(s, 0, s.Length);
            yield return (pos, tokLine, tokColumn, tok, s);
            pos += len;
        }
    }

    readonly struct CharWindowSource : ILexerUnitSource
    {
        readonly LexerCharWindow _w;
        public CharWindowSource(LexerCharWindow w) => _w = w;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Peek(int offset) => _w.Peek(offset);
    }
}

// Tracks one-based line and column across consecutive tokens.
//  - The DFA's newline unit (dfa[0]) ends a line, so line counting agrees with ^ and $.
//  - '\t' advances to the next tab stop (with width 4: columns 1..4 -> 5, 5..8 -> 9).
//  - Other control characters (below U+0020) don't advance the column.
//  - Columns count code points: the low half of a surrogate pair doesn't advance the column,
//    even when the pair is split across two tokens.
internal struct LineColumnTracker
{
    readonly int _newline;
    readonly int _tabWidth;
    bool _afterHighSurrogate;

    public int Line { get; private set; }
    public int Column { get; private set; }

    public LineColumnTracker(int newline, int tabWidth, int line = 1, int column = 1)
    {
        _newline = newline;
        _tabWidth = tabWidth;
        _afterHighSurrogate = false;
        Line = line;
        Column = column;
    }

    public void Advance(string text, int start, int length)
    {
        int line = Line, column = Column;
        bool afterHigh = _afterHighSurrogate;
        for (int i = start, end = start + length; i < end; i++)
        {
            char c = text[i];
            if (c == _newline)
            {
                line++;
                column = 1;
                afterHigh = false;
            }
            else if (c == '\t')
            {
                column = ((column - 1) / _tabWidth + 1) * _tabWidth + 1;
                afterHigh = false;
            }
            else if (c < ' ')
            {
                afterHigh = false;   // other control characters take no width
            }
            else if (char.IsLowSurrogate(c) && afterHigh)
            {
                afterHigh = false;   // second half of a pair: already counted
            }
            else
            {
                column++;
                afterHigh = char.IsHighSurrogate(c);
            }
        }
        Line = line;
        Column = column;
        _afterHighSurrogate = afterHigh;
    }
}

// A growable window over a TextReader. Index 0 is the start of the current token.
// Chars are read on demand; Take() consumes from the front. The buffer only grows
// when a single token (plus its lookahead/overrun) doesn't fit.
internal sealed class LexerCharWindow
{
    readonly TextReader _reader;
    char[] _buf;
    int _start, _end;
    bool _eof;

    public LexerCharWindow(TextReader reader, int capacity = 4096)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _buf = new char[capacity];
    }

    /// <summary>Char at offset from the token start, or -1 at end of input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek(int offset)
        => _start + offset < _end ? _buf[_start + offset] : PeekSlow(offset);

    [MethodImpl(MethodImplOptions.NoInlining)]
    int PeekSlow(int offset)
    {
        while (_start + offset >= _end)
        {
            if (_eof) return -1;
            Fill();
        }
        return _buf[_start + offset];
    }

    void Fill()
    {
        if (_end == _buf.Length)
        {
            if (_start == 0)
            {
                Array.Resize(ref _buf, _buf.Length * 2);
            }
            else
            {
                int count = _end - _start;
                Array.Copy(_buf, _start, _buf, 0, count);   // overlap-safe
                _start = 0;
                _end = count;
            }
        }
        int n = _reader.Read(_buf, _end, _buf.Length - _end);
        if (n == 0) _eof = true;
        else _end += n;
    }

    public string Take(int length)
    {
        string s = new string(_buf, _start, length);
        _start += length;
        return s;
    }
}