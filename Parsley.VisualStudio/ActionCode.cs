#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

using Parsley.LanguageServer;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// A semantic action in a grammar's text: the rule it belongs to, and the code between its braces
    /// </summary>
    internal sealed class ActionSite
    {
        public ActionSite(string rule, int bodyStart, int bodyEnd, string body)
        {
            Rule = rule; BodyStart = bodyStart; BodyEnd = bodyEnd; Body = body;
        }
        public string Rule { get; }
        /// <summary>
        /// The offset just after the opening brace
        /// </summary>
        public int BodyStart { get; }
        /// <summary>
        /// The offset of the closing brace, or the end of the text when there isn't one yet
        /// </summary>
        public int BodyEnd { get; }
        public string Body { get; }

        /// <summary>
        /// The action whose braces the offset is between, or null
        /// </summary>
        public static ActionSite? At(string text, int offset)
        {
            var scan = XbnfScan.Scan(text);
            foreach (var t in scan.Tokens)
            {
                if (t.Kind != TokenKind.Action) continue;
                var open = text.IndexOf('{', t.Start);
                if (open < 0 || open >= t.End) continue;
                var closed = t.End > open + 1 && text[t.End - 1] == '}';
                var bodyEnd = closed ? t.End - 1 : t.End;
                if (offset <= open || offset > bodyEnd) continue;
                // the rule it ends: the last definition before it
                var rule = scan.Definitions.LastOrDefault(d => d.Start < t.Start);
                if (null == rule) return null;
                return new ActionSite(rule.Text, open + 1, bodyEnd, text.Substring(open + 1, bodyEnd - open - 1));
            }
            return null;
        }

        /// <summary>
        /// The grammar with every action's code taken out: it changes when the rules do, and not as you type in an
        /// action, so the generated code that depends on it only has to be found again when it changes
        /// </summary>
        public static string Structure(string text)
        {
            var sb = new StringBuilder(text.Length);
            var pos = 0;
            foreach (var t in XbnfScan.Scan(text).Tokens)
            {
                if (t.Kind != TokenKind.Action) continue;
                sb.Append(text, pos, t.Start - pos).Append("=>{}");
                pos = t.End;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }
    }

    /// <summary>
    /// A C# document holding an action's current code where the parser's evaluator would have it, so Roslyn can
    /// answer questions about it: offsets in the action map to offsets in the document by a fixed difference
    /// </summary>
    internal sealed class ActionDocument
    {
        public ActionDocument(Document document, int generatedBodyStart, ActionSite site)
        {
            Document = document; GeneratedBodyStart = generatedBodyStart; Site = site;
        }
        public Document Document { get; }
        public int GeneratedBodyStart { get; }
        public ActionSite Site { get; }

        public int ToGenerated(int grammarOffset) => GeneratedBodyStart + (grammarOffset - Site.BodyStart);

        /// <summary>
        /// The offset in the grammar, or -1 when it's not inside the action
        /// </summary>
        public int ToGrammar(int generatedOffset)
        {
            var o = Site.BodyStart + (generatedOffset - GeneratedBodyStart);
            return o >= Site.BodyStart && o <= Site.BodyEnd ? o : -1;
        }
    }

    /// <summary>
    /// Finds the C# code a grammar's actions were generated into, whether the source generator made it or the
    /// command line tool wrote it into a file in the project, by the #line directives that point back at the
    /// grammar. It copies that code with the action's current text in place of what was generated, so it
    /// doesn't need the grammar to be regenerated as you type. The copy, and with the source generator a copy of
    /// the project without it, is kept until the grammar's rules or the project's other files change.
    /// </summary>
    internal sealed class ActionWorkspace
    {
        const string GeneratorTypeName = "Parsley.Generator.ParsleyGenerator";
        const int MaxCached = 8;

        sealed class Entry
        {
            public string Key = "";
            public Solution Solution = null!;
            public DocumentId Host = null!;
            // the generated text around the host method's body, braces included
            public string Before = "";
            public string After = "";
        }

        readonly object _lock = new object();
        readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A document with the action's code in it, or null when nothing generated from the grammar is in the
        /// solution: it has no actions yet, or the code generated from it isn't in any project
        /// </summary>
        public async Task<ActionDocument?> GetAsync(Solution solution, string grammarPath, string grammarText, ActionSite site, CancellationToken ct)
        {
            grammarPath = _Full(grammarPath) ?? grammarPath;
            var structure = ActionSite.Structure(grammarText);
            Entry? entry;
            lock (_lock) _cache.TryGetValue(grammarPath, out entry);
            if (null != entry)
            {
                var project = entry.Solution.GetDocument(entry.Host)?.Project;
                var live = null != project ? solution.GetProject(project.Id) : null;
                if (null == live || entry.Key != await _KeyAsync(live, structure, ct).ConfigureAwait(false)) entry = null;
            }
            if (null == entry)
            {
                entry = await _BuildAsync(solution, grammarPath, structure, site, ct).ConfigureAwait(false);
                if (null == entry) return null;
                lock (_lock)
                {
                    if (_cache.Count >= MaxCached) _cache.Clear();
                    _cache[grammarPath] = entry;
                }
            }
            var text = entry.Before + site.Body + entry.After;
            var forked = entry.Solution.WithDocumentText(entry.Host, SourceText.From(text, Encoding.UTF8));
            var document = forked.GetDocument(entry.Host);
            return null == document ? null : new ActionDocument(document, entry.Before.Length, site);
        }

        // what the copy depends on: the grammar's rules, and the project's documents and references. Not the
        // grammar's actions, and not the grammar itself as an AdditionalFile, which changes as you type
        static async Task<string> _KeyAsync(Project project, string structure, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append(project.Id.Id).Append('|').Append(structure.GetHashCode()).Append('|').Append(structure.Length);
            foreach (var d in project.Documents)
            {
                var v = await d.GetTextVersionAsync(ct).ConfigureAwait(false);
                sb.Append('|').Append(d.Id.Id).Append(':').Append(v);
            }
            sb.Append("|m").Append(project.MetadataReferences.Count).Append("|p").Append(project.ProjectReferences.Count()).Append("|a").Append(project.AnalyzerReferences.Count);
            return sb.ToString();
        }

        async Task<Entry?> _BuildAsync(Solution solution, string grammarPath, string structure, ActionSite site, CancellationToken ct)
        {
            // projects in or above the grammar's folder first: that's where the code generated from it usually is
            var dir = Path.GetDirectoryName(grammarPath) ?? "";
            var projects = solution.Projects
                .Where(p => p.Language == LanguageNames.CSharp)
                .OrderBy(p => _IsUnder(dir, Path.GetDirectoryName(p.FilePath ?? "") ?? "") ? 0 : 1)
                .ToList();
            var name = Path.GetFileName(grammarPath);
            // the rule's EvaluateXxx, as the language server says the synthesizer names it; null while it can't say,
            // and then any of the grammar's Evaluate methods will do
            var evaluate = await MethodNameService.GetEvaluateNameAsync(grammarPath, site.Rule, ct).ConfigureAwait(false);
            foreach (var project in projects)
            {
                ct.ThrowIfCancellationRequested();
                // a file the command line tool wrote, compiled with the rest of the project
                foreach (var d in project.Documents)
                {
                    var text = await d.GetTextAsync(ct).ConfigureAwait(false);
                    if (!_Mentions(text, name)) continue;
                    var root = await d.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    var host = null == root ? null : _FindHost(root, grammarPath, Path.GetDirectoryName(d.FilePath ?? "") ?? "", evaluate);
                    if (null == host) continue;
                    return await _EntryAsync(solution, project, d.Id, text.ToString(), host.Body!, structure, ct).ConfigureAwait(false);
                }
                // the source generator's, for a grammar that's one of the project's AdditionalFiles
                if (!project.AdditionalDocuments.Any(a => _SamePath(a.FilePath, grammarPath))) continue;
                var generated = await project.GetSourceGeneratedDocumentsAsync(ct).ConfigureAwait(false);
                foreach (var g in generated)
                {
                    var text = await g.GetTextAsync(ct).ConfigureAwait(false);
                    if (!_Mentions(text, name)) continue;
                    var root = await g.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    var host = null == root ? null : _FindHost(root, grammarPath, "", evaluate);
                    if (null == host) continue;
                    return await _FromGeneratorAsync(solution, project, generated, g, text.ToString(), host.Body!, structure, ct).ConfigureAwait(false);
                }
            }
            return null;
        }

        static async Task<Entry> _EntryAsync(Solution solution, Project project, DocumentId host, string text, BlockSyntax body, string structure, CancellationToken ct)
        {
            var live = solution.GetProject(project.Id)!;
            return new Entry
            {
                Key = await _KeyAsync(live, structure, ct).ConfigureAwait(false),
                Solution = solution,
                Host = host,
                Before = text.Substring(0, body.SpanStart + 1),
                After = text.Substring(body.Span.End - 1),
            };
        }

        // generated code can't be edited, so the copy is of the project without the generator, with what it
        // generated as ordinary documents
        static async Task<Entry?> _FromGeneratorAsync(Solution solution, Project project, IEnumerable<SourceGeneratedDocument> generated,
            SourceGeneratedDocument hostDoc, string hostText, BlockSyntax body, string structure, CancellationToken ct)
        {
            var ours = project.AnalyzerReferences.Where(_IsParsleyGenerator).ToList();
            if (0 == ours.Count) return null;
            var key = await _KeyAsync(project, structure, ct).ConfigureAwait(false);
            var p = project;
            foreach (var r in ours) p = p.RemoveAnalyzerReference(r);
            DocumentId? host = null;
            foreach (var g in generated)
            {
                if (!_IsParsleyOutput(g.HintName)) continue;
                var text = g == hostDoc ? hostText : (await g.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                var d = p.AddDocument(g.HintName, SourceText.From(text, Encoding.UTF8), folders: new[] { "Parsley.Generator" });
                if (g == hostDoc) host = d.Id;
                p = d.Project;
            }
            if (null == host) return null;
            return new Entry
            {
                Key = key,
                Solution = p.Solution,
                Host = host,
                Before = hostText.Substring(0, body.SpanStart + 1),
                After = hostText.Substring(body.Span.End - 1),
            };
        }

        static bool _IsParsleyGenerator(AnalyzerReference r)
        {
            try
            {
                return r.GetGeneratorsForAllLanguages().Any(g => g.GetGeneratorType().FullName == GeneratorTypeName);
            }
            catch (Exception) { return false; }
        }

        // the names the source generator gives what it adds
        static bool _IsParsleyOutput(string hint) =>
            hint.EndsWith(".Parsley.g.cs", StringComparison.Ordinal)
            || hint.StartsWith("Parsley.Runtime.", StringComparison.Ordinal)
            || hint == "ParserAttribute.g.cs";

        static bool _Mentions(SourceText text, string fileName)
        {
            var s = text.ToString();
            return s.IndexOf("#line", StringComparison.Ordinal) >= 0 && s.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The method whose body the action goes in: the rule's Evaluate method, named <paramref name="name"/>, in a
        /// class with code mapped to the grammar, or, for a rule that had no action when the code was generated or
        /// when the name isn't known, another method of that class, so the action still sees the evaluator's members
        /// </summary>
        static MethodDeclarationSyntax? _FindHost(SyntaxNode root, string grammarPath, string docDir, string? name)
        {
            MethodDeclarationSyntax? fallback = null;
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var methods = type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => null != m.Body && 1 == m.ParameterList.Parameters.Count
                        && m.ParameterList.Parameters[0].Type?.ToString().EndsWith("ParseNode", StringComparison.Ordinal) == true)
                    .ToList();
                if (0 == methods.Count) continue;
                var mapped = methods.Where(m => _MapsTo(m, grammarPath, docDir)).ToList();
                if (0 == mapped.Count) continue;
                var exact = null == name ? null : methods.FirstOrDefault(m => m.Identifier.ValueText == name);
                if (null != exact) return exact;
                fallback ??= mapped.FirstOrDefault(m => m.Identifier.ValueText.StartsWith("Evaluate", StringComparison.Ordinal) && m.Identifier.ValueText != "Evaluate");
            }
            return fallback;
        }

        // whether a #line directive in or just before the method names the grammar
        static bool _MapsTo(MethodDeclarationSyntax m, string grammarPath, string docDir)
        {
            foreach (var trivia in m.DescendantTrivia(descendIntoTrivia: true).Concat(m.GetLeadingTrivia()))
            {
                if (!(trivia.GetStructure() is LineDirectiveTriviaSyntax line) || line.File.IsKind(SyntaxKind.None)) continue;
                var file = line.File.ValueText;
                if (string.IsNullOrEmpty(file)) continue;
                var full = Path.IsPathRooted(file) ? _Full(file) : _Full(Path.Combine(docDir, file));
                if (_SamePath(full, grammarPath)) return true;
            }
            return false;
        }

        static string? _Full(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (Exception) { return null; }
        }

        static bool _SamePath(string? a, string? b)
        {
            a = _Full(a);
            b = _Full(b);
            return null != a && null != b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static bool _IsUnder(string dir, string projectDir)
        {
            if (string.IsNullOrEmpty(projectDir)) return false;
            var p = _Full(projectDir)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var d = _Full(dir);
            return null != p && null != d && (string.Equals(p, d, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }
}
