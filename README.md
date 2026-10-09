# Parsley parser synthesis

Turns an XBNF grammar into C# recursive descent that reads as if written by hand.

## Use

```csharp
var grammar = Grammar.ReadFrom("json.xbnf");          // or build one with the Ebnf helpers
var analysis = GrammarAnalysis.Analyze(grammar);       // problems are in analysis.Messages
string code = CSharpSynthesizer.Synthesize(analysis);  // the parser class
string runtime = CSharpSynthesizer.RuntimeSource;      // ParserRuntime.cs, needed alongside it
```

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

For prototyping without compiling, `new GrammarInterpreter(analysis, tokens).Parse()` makes exactly the same
choices. `RegexLexer` is a stand-in lexer built from the terminal declarations, for tests only.

## Layout

| Folder | What's in it |
| --- | --- |
| `src/Parsley/Grammar` | Grammar model, `Ebnf` builder helpers, XBNF reader and writer, validation |
| `src/Parsley/Analysis` | Symbol ids, left recursion to loops, FIRST/FOLLOW per node, decision classification |
| `src/Parsley/Synthesis` | C# synthesizer, grammar interpreter, stand-in regex lexer |
| `src/Parsley/Runtime` | `ParsleyRuntime.cs`: Token, LookAheadEnumerator, ParseNode, ParserBase (also embedded in the dll) |
| `src/Parsley/ParseContext` | Your ParseContext, with two fixes (see below) and `#nullable disable` at the top |
| `tests` | Test runner and tests; `json.xbnf` with its `\u` pattern fixed |

## Build and test

Open `Parsley.sln` in Visual Studio, or run `dotnet build Parsley.sln`. It has two projects:
