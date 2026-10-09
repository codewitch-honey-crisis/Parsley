# Parsley parser synthesis

Turns an XBNF grammar into C# recursive descent that reads as if written by hand.

## Use

```
parsley

Usage: parsley [<grammar-file>] [--lexer <lexer-file>] [--output <code-file>] [--shared]

    <grammar-file>            The path to the XBNF grammar input file. Defaults to <stdin>.
    -l, --lexer <lexer-file>  The lexer output file to generate.
    -o, --output <code-file>  The path to the ouput code file. Defaults to <stdout>.
    -s, --shared              Generate the ParsleyRuntime.cs shared runtime file.
```

- The grammar file is in XBNF, a superset of EBNF with some extra features for parser synthesis. 
- The lexer file is generated if specified and contains 1 rule per line, `<name>` `<pattern>` (separated by a space) and hidden terminal names are preceded with `.` as in `.whitespace`,
- The output code file is a C# class that implements `ParserBase` and has a `Parse()` method returning a `ParseNode`. The shared runtime file is `ParsleyRuntime.cs`, which contains the definitions of `Token`, `LookAheadEnumerator`, `ParseNode`, and `ParserBase`. 
- If shared is indicated, `ParsleyRuntime.cs` is generated in the same directory as the output code file, or the current working directory if no output code file is specified.

XBNF is a loose superset of EBNF. The documentation is [here](XBNF.md).

The synthesized class plus `ParsleyRuntime.cs` compile on their own (C# 12, .NET 8), with no reference to Parsley:

```csharp
var parser = new JsonParser(tokens);   // IEnumerable<Token> straight from the lexer
ParseNode tree = parser.Parse();
foreach (var e in parser.Errors) Console.WriteLine(e);
```

Token ids: the lexer numbers terminals from 0 in the grammar's terminal order (hidden and implicit ones
included, implicit ones last), and the parser adds `ParserTables.TerminalStart` (the number of non-terminals)
as each token arrives. A negative id, or one past the last terminal, is reported as unrecognized input.

Terminals: a rule that references no symbols (only `"literals"` and `'patterns'`) is a terminal; `<terminal>`
forces one (it may then reference other terminals, which are inlined) and `<terminal=false>` forces a
non-terminal. Each `TerminalDeclaration` keeps its `Definition` expression and a `Regex` for your lexer
generator: patterns pass through unvalidated with the quotes removed (`\'` becomes `'`), literals are escaped,
and other definitions are built from their constructs.

## Build and test

Open `Parsley.sln` in Visual Studio, or run `dotnet build Parsley.sln`.
