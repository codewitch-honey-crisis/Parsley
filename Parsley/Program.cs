using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Parsley
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Options? toDispose = null;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;

                if (!Options.TryCreate(args, out var options, out var error))
                {
                    if (!error!.IsHelpRequest)
                    {
                        Console.Error.WriteLine("Error: " + error.Message);
                        Console.Error.WriteLine();
                    }
                    Options.PrintUsage();
                    return;
                }
                toDispose = options;
                var grammar = Grammar.ReadFrom(options.Input, CliUtility.GetFilename(options.Input));
                var analysis = GrammarAnalysis.Analyze(grammar);
                foreach (var m in analysis.Messages) Console.Error.WriteLine(m);
                options.Output.Write(CSharpSynthesizer.Synthesize(analysis));
                if(options.Shared)
                {
                    var dir = "";
                    var fn = CliUtility.GetFilename(options.Input);
                    if(fn!=null)
                    {
                        dir = Path.GetDirectoryName(fn) ?? dir;
                    }
                    var sharedPath = dir.Length>0?Path.Combine(dir, "ParsleyRuntime.cs"):"ParsleyRuntime.cs";
                    File.WriteAllText(sharedPath, CSharpSynthesizer.RuntimeSource);
                }
                
                if (options.Lexer != null)
                {
                    for (var i = 0; i < grammar.Terminals.Count; i++)
                    {
                        var term = grammar.Terminals[i];
                        var isHidden = (bool)(term.Attributes.FirstOrDefault(p => p.Name.Equals("hidden", StringComparison.Ordinal) && p.Value is bool b && b)?.Value ?? false);
                        if (isHidden) options.Lexer.Write('.');
                        options.Lexer.Write(term.Name);
                        options.Lexer.Write(' ');
                        options.Lexer.WriteLine(Grammar.EscapeLiteralOrPattern(term.Literal, term.Pattern));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return;
            }
            finally
            {
                toDispose?.Dispose();
            }
        }
    }
}
