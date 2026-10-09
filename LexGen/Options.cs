using Cli;
namespace LexGen;
[CliArgs]
internal partial class Options
{
    [CmdArg(0, Description = "The path to the lexer input file.",ValueName ="lexer-file",Required =false)]
    public TextReader Input { get; set; } = Console.In;
    [CmdArg(ShortName ='o', Description = "The path to the ouput code file.", ValueName = "code-file",Required =false)]
    public TextWriter Output { get; set; } = Console.Out;
    [CmdArg(ShortName ='u', ValueName = "unicode", Description = "Use Unicode character classes.",Required =false)]
    public bool Unicode = false;
    [CmdArg(ShortName = 'n', ValueName = "namespace", Description = "Use the given namespace. Defaults to no namespace.", Required = false)]
    public string? Namespace = null;
    [CmdArg(ShortName = 'c', ValueName = "class", Description = "Use the given class name. Defaults to the input filename if available, or Lexer.", Required = false)]
    public string? Class = null;
    [CmdArg(ShortName = 'p', ValueName = "public", Description = "Make the class public", Required = false)]
    public bool Public = false;

}
