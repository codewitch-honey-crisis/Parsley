using Cli;

using System;
using System.IO;
namespace Parsley;

[CliArgs]
internal partial class Options
{
    [CmdArg(0, Description = "The path to the XBNF grammar input file.", ValueName = "grammar-file", Required = false)]
    public TextReader Input { get; set; } = Console.In;
    [CmdArg(ShortName = 'l', Description = "The lexer output file to generate.", ValueName = "lexer", Required = false)]
    public TextWriter? Lexer = null;
    [CmdArg(ShortName = 'o', Description = "The path to the ouput code file.", ValueName = "code-file", Required = false)]
    public TextWriter Output { get; set; } = Console.Out;
    [CmdArg(ShortName = 's', Description = "Generate the shared runtime file.", ValueName = "shared", Required = false)]
    public bool Shared = false;

}
