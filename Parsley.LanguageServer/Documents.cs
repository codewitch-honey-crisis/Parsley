using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// A grammar's text, with its scan and line starts, for positions in both directions
    /// </summary>
    sealed class Document
    {
        int[]? _lineStarts;
        XbnfScan? _scan;

        public Document(string path, string text, int version)
        {
            Path = path;
            Text = text;
            Version = version;
        }
        public string Path { get; }
        public string Text { get; }
        public int Version { get; }
        public XbnfScan Scan => _scan ??= XbnfScan.Scan(Text);
        /// <summary>
        /// The grammar as the reader reads it, for what only it knows, such as which rules are terminals.
        /// Read once per version, when first asked for; null if it couldn't be read.
        /// </summary>
        public Parsley.Grammar? ReadGrammar(Func<string, string> readFile)
        {
            if (!_grammarRead)
            {
                _grammarRead = true;
                try { XbnfParser.TryParse(Text, Path, readFile, out _grammar); }
                catch (Exception) { _grammar = null; }
            }
            return _grammar;
        }
        Parsley.Grammar? _grammar;
        bool _grammarRead;

        /// <summary>
        /// The productions' names, in the order the reader has them, with what imports add where it adds it, which
        /// decides the names their methods get. Null if the grammar couldn't be read.
        /// </summary>
        public IReadOnlyList<string>? ProductionNames(Func<string, string> readFile)
        {
            if (null == _productionNames)
            {
                var g = ReadGrammar(readFile);
                if (null == g) return null;
                var names = new List<string>();
                foreach (var p in g.Productions) names.Add(p.Name);
                _productionNames = names;
            }
            return _productionNames;
        }
        List<string>? _productionNames;

        /// <summary>
        /// The names of the productions' methods, as in EvaluateXxx, as the synthesizer names them. Built once per
        /// version, when first asked for; null if the grammar couldn't be read.
        /// </summary>
        public Parsley.MethodNames? MethodNames(Func<string, string> readFile)
        {
            if (null == _methodNames)
            {
                var names = ProductionNames(readFile);
                if (null == names) return null;
                _methodNames = Parsley.MethodNames.For(names);
            }
            return _methodNames;
        }
        Parsley.MethodNames? _methodNames;

        /// <summary>
        /// The terminal each "literal" or 'pattern' used in a rule of this document stands for, by the offset of its
        /// token: the declared terminal it resolves to, as the reader resolves it, or null when it has no declaration
        /// and so is an implicit terminal of its own. Built once per version, when first asked for; empty if the
        /// grammar couldn't be read.
        /// </summary>
        public IReadOnlyDictionary<int, string?> LiteralTargets(Func<string, string> readFile)
        {
            if (null != _literalTargets) return _literalTargets;
            var result = new Dictionary<int, string?>();
            var g = ReadGrammar(readFile);
            if (null != g)
            {
                try
                {
                    // resolve on a copy, the way the analysis does, so implicit terminals are told from declared ones
                    var r = g.Clone();
                    r.ResolveLiterals();
                    foreach (var p in r.Productions)
                        foreach (var e in p.Expression.Descendants())
                        {
                            if (!(e is Parsley.SymbolRef sr) || (null == sr.Literal && null == sr.Pattern) || null == sr.Name) continue;
                            // a literal from an @imported file is that file's, at an offset in its own text
                            if (null != sr.Filename && !Documents.PathComparer.Equals(System.IO.Path.GetFullPath(sr.Filename), Path)) continue;
                            if (0 >= sr.Line && 0 > sr.Position) continue;
                            var t = r.GetTerminal(sr.Name);
                            result[ToOffset(sr)] = null != t && !t.IsImplicit ? t.Name : null;
                        }
                }
                catch (Exception) { result.Clear(); }
            }
            return _literalTargets = result;
        }
        Dictionary<int, string?>? _literalTargets;

        int[] LineStarts
        {
            get
            {
                if (null != _lineStarts) return _lineStarts;
                var starts = new List<int> { 0 };
                for (var i = 0; i < Text.Length; ++i)
                    if (Text[i] == '\n') starts.Add(i + 1);
                return _lineStarts = starts.ToArray();
            }
        }

        /// <summary>
        /// An LSP position (zero-based line, UTF-16 character) as an offset
        /// </summary>
        public int ToOffset(int line, int character)
        {
            var starts = LineStarts;
            if (line < 0) return 0;
            if (line >= starts.Length) return Text.Length;
            return Math.Min(starts[line] + Math.Max(0, character), line + 1 < starts.Length ? starts[line + 1] : Text.Length);
        }

        /// <summary>
        /// Where something the reader read is: its Position, the offset it read it at, when it has one. Its line and
        /// column otherwise, which aren't exact on a line with a tab before it, since the reader counts a tab as
        /// moving to the next multiple of 8 columns.
        /// </summary>
        public int ToOffset(Parsley.GrammarNode node) => ToOffset(node.Position, node.Line, node.Column);
        public int ToOffset(Parsley.GrammarMessage m) => ToOffset(m.Position, m.Line, m.Column);
        int ToOffset(long position, int line, int column) =>
            0 <= position && position <= Text.Length ? (int)position : ToOffset(line - 1, Math.Max(0, column - 1));

        public (int Line, int Character) ToPosition(int offset)
        {
            var starts = LineStarts;
            offset = Math.Clamp(offset, 0, Text.Length);
            var line = Array.BinarySearch(starts, offset);
            if (line < 0) line = ~line - 1;
            return (line, offset - starts[line]);
        }

        public JsonObject Range(int start, int end)
        {
            var (sl, sc) = ToPosition(start);
            var (el, ec) = ToPosition(end);
            return new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
                ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
            };
        }
    }

    /// <summary>
    /// Open documents, and grammars read from disk because an open one @imports them
    /// </summary>
    sealed class Documents
    {
        static readonly StringComparer s_paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        readonly Dictionary<string, Document> _open = new Dictionary<string, Document>(s_paths);
        readonly Dictionary<string, (DateTime Stamp, Document Doc)> _disk = new Dictionary<string, (DateTime, Document)>(s_paths);
        readonly object _lock = new object();

        public static StringComparer PathComparer => s_paths;

        // the URI each path came in as, so what goes back to the client is in the form it uses
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_uris = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(s_paths);

        /// <summary>
        /// A file: URI as a full path, or any other URI as it is. Clients write them differently: VS Code escapes the
        /// drive's colon (file:///c%3A/Users/...), which System.Uri doesn't read as a drive, and Visual Studio
        /// doesn't (file:///C:/Users/...), so they're taken apart here rather than by Uri
        /// </summary>
        public static string ToPath(string uri)
        {
            var local = FileUriToPath(uri, OperatingSystem.IsWindows());
            if (null == local) return uri;
            var path = System.IO.Path.GetFullPath(local);
            s_uris[path] = uri;
            return path;
        }

        /// <summary>
        /// The path in a file: URI, unescaped, in the platform's form, or null for another scheme
        /// </summary>
        public static string? FileUriToPath(string uri, bool windows)
        {
            if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            var rest = uri.Substring(5);
            // drop a query or fragment, which a file path doesn't have
            var cut = rest.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) rest = rest.Substring(0, cut);
            string host = "";
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                rest = rest.Substring(2);
                var slash = rest.IndexOf('/');
                host = Uri.UnescapeDataString(slash < 0 ? rest : rest.Substring(0, slash));
                rest = slash < 0 ? "/" : rest.Substring(slash);
            }
            var path = Uri.UnescapeDataString(rest);
            if (!windows) return path;
            // /c:/Users/... is c:/Users/...
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && (path[2] == ':' || path[2] == '|'))
                path = path[1] + ":" + path.Substring(3);
            path = path.Replace('/', '\\');
            // file://server/share/... is a UNC path
            if (0 < host.Length && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + host + path;
            return path;
        }

        /// <summary>
        /// The URI for a path: the one the client gave for it, or a file: URI made from it
        /// </summary>
        public static string ToUri(string path) => s_uris.TryGetValue(path, out var uri) ? uri : new Uri(path).AbsoluteUri;

        public void Open(string path, string text, int version) { lock (_lock) _open[path] = new Document(path, text, version); }
        public void Close(string path) { lock (_lock) _open.Remove(path); }

        public Document? GetOpen(string path) { lock (_lock) return _open.TryGetValue(path, out var d) ? d : null; }

        /// <summary>
        /// The open document, or the file on disk, or null if neither
        /// </summary>
        public Document? Get(string path)
        {
            lock (_lock)
            {
                if (_open.TryGetValue(path, out var d)) return d;
                try
                {
                    if (!File.Exists(path)) return null;
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (_disk.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Doc;
                    var doc = new Document(path, File.ReadAllText(path), 0);
                    _disk[path] = (stamp, doc);
                    return doc;
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        /// <summary>
        /// For XbnfReader's @import: the open text if the file is open, else what's on disk
        /// </summary>
        public string ReadForImport(string path) => Get(System.IO.Path.GetFullPath(path))?.Text ?? throw new FileNotFoundException($"Could not find {path}", path);

        /// <summary>
        /// The document and everything it @imports, transitively, each once
        /// </summary>
        public List<Document> WithImports(Document root)
        {
            var result = new List<Document>();
            var seen = new HashSet<string>(s_paths);
            var stack = new Stack<Document>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d.Path)) continue;
                result.Add(d);
                var dir = System.IO.Path.GetDirectoryName(d.Path) ?? "";
                foreach (var (p, _) in d.Scan.Imports)
                {
                    string full;
                    try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p)); }
                    catch (ArgumentException) { continue; }
                    var imported = Get(full);
                    if (null != imported) stack.Push(imported);
                }
            }
            return result;
        }

        /// <summary>
        /// The open documents that are the document or @import it, with their imports: every grammar a rename
        /// or a reference search in this one could touch
        /// </summary>
        public List<Document> Related(Document doc)
        {
            var result = new List<Document>();
            var seen = new HashSet<string>(s_paths);
            void add(Document d) { if (seen.Add(d.Path)) result.Add(d); }
            foreach (var d in WithImports(doc)) add(d);
            List<Document> open;
            lock (_lock) open = new List<Document>(_open.Values);
            foreach (var o in open)
            {
                var closure = WithImports(o);
                if (closure.Exists(d => s_paths.Equals(d.Path, doc.Path)))
                    foreach (var d in closure) add(d);
            }
            return result;
        }
    }
}
