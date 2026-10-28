using System.Collections.Generic;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// The attributes and directives XBNF understands, for completion and hover. Summarized from XBNF.md.
    /// </summary>
    static class XbnfKnowledge
    {
        public static readonly Dictionary<string, string> Attributes = new Dictionary<string, string>
        {
            ["start"] = "Flag. This rule is the start symbol; otherwise the first non-terminal is.",
            ["collapsed"] = "Flag. The rule (or terminal) gets no node in the parse tree; its children go into its parent's.",
            ["hidden"] = "Flag, on a terminal. The lexer matches it but never reports it (whitespace, comments).",
            ["ignoreCase"] = "`true` or `false`, on a terminal. Whether it matches letters in either case, whatever `@ignoreCase` says. Defaults to false. An error on a non-terminal.",
            ["terminal"] = "`true` or `false`. Forces the rule to be a terminal or a non-terminal. Without it, only a lone literal or pattern is a terminal; `<terminal>` lets a terminal combine them and inline other terminals.",
            ["nowarn"] = "Flag. Suppresses the \"unreachable from the start symbol\" warning.",
            ["expected"] = "String. How the rule or alternation is named in error messages: `expected=\"a statement\"`.",
            ["policy"] = "`greedy`, `lazy` or `first`. Settles ambiguous decisions in this rule or group.",
            ["sync"] = "On a rule: terminals where error recovery may stop, as a string. On a terminal: a flag making it a strong restart point.",
            ["type"] = "String. The C# type the rule's `EvaluateXxx` returns: `type=\"double\"`.",
            ["state"] = "String, on the start rule. The C# type of the evaluator's `State` and `Evaluate`'s `state` argument.",
            ["entering"] = "Method name. Called with no arguments as the parser starts this rule.",
            ["parsed"] = "Method name. Called with the finished parse node as this rule ends.",
            ["operators"] = "Flag. The alternatives are operators, tightest first, parsed by precedence climbing.",
            ["assoc"] = "`left` or `right`. How an operator form associates.",
            ["when"] = "Method name, on a terminal. Makes a contextual terminal, read by the parser from another terminal's tokens.",
            ["matched"] = "Method name, on a terminal. Called with each token of this terminal the parser matches.",
            ["shared"] = "Flag. Other grammars can parse islands with this rule, through `@external` and `entry=`.",
            ["entry"] = "String, on an island (a rule with no body). The `@external` grammar's shared rule that parses it: `entry=\"Json.Object\"`.",
            ["after"] = "String, on an island. The terminals the island starts right after: `after=\"addHeadersKeyword\"`.",
            ["nest"] = "String, on an island. The external grammar's terminal that opens a nested pair, counted against `pop`. It can be the same terminal as `pop`, for a delimiter like a quote: the first opens and the next ends the island.",
            ["pop"] = "String, on an island. The external grammar's terminal that ends the island once it balances every `nest`. When it's also `nest`, the second one ends the island.",
            ["startsWith"] = "String, on an island. The external grammar's terminals the island must start with: where its lexer reads none of them first, the next island after the same token is tried. `startsWith=\"XPath.xpathOpen\"`.",
            ["popOnError"] = "`true` or `false`, on an island. Whether an error from the external grammar's lexer ends the island. Defaults to true.",
            ["factor"] = "`true` or `false`, on a rule or a group. Whether to left factor its alternations, whatever `@factor` says. `factor=false` also keeps a rule from being inlined to factor others.",
            ["maxk"] = "Number, on a rule or a group. The most tokens its decisions may look at, instead of the `@maxk` limit: `maxk=1`.",
            ["lexer"] = "Name, on a terminal. Another lexer produces it instead of the grammar's own, where `on` and `when` say: `lexer=Regex`.",
            ["on"] = "String, on a terminal with `lexer`. The terminals of the grammar's own lexer where the other lexer may take over.",
        };

        public static readonly Dictionary<string, string[]> AttributeValues = new Dictionary<string, string[]>
        {
            ["policy"] = new[] { "greedy", "lazy", "first" },
            ["assoc"] = new[] { "left", "right" },
            ["terminal"] = new[] { "true", "false" },
            ["start"] = new[] { "true", "false" },
            ["collapsed"] = new[] { "true", "false" },
            ["hidden"] = new[] { "true", "false" },
            ["ignoreCase"] = new[] { "true", "false" },
            ["nowarn"] = new[] { "true", "false" },
            ["operators"] = new[] { "true", "false" },
            ["shared"] = new[] { "true", "false" },
            ["popOnError"] = new[] { "true", "false" },
            ["factor"] = new[] { "true", "false" },
        };

        public static readonly Dictionary<string, string> Directives = new Dictionary<string, string>
        {
            ["import"] = "`@import \"file.xbnf\";` Reads another file's rules in, relative to this file.",
            ["class"] = "`@class \"MyParser\";` or `@class \"My.Parsers.MyParser\";` The synthesized parser's class name, and its namespace before the last dot. Ignored, with a warning, by Parsley.Generator.",
            ["evaluator"] = "`@evaluator \"MyEvaluator\";` The synthesized evaluator's class name, in the parser's namespace. Ignored, with a warning, by Parsley.Generator.",
            ["using"] = "`@using \"System.Text\";` A namespace for semantic actions to use.",
            ["maxk"] = "`@maxk 2;` The most tokens a fixed-lookahead decision looks at; 4 if not given.",
            ["factor"] = "`@factor true;` Left factors alternations whose alternatives start alike, so one token decides. The tree doesn't change.",
            ["ignoreCase"] = "`@ignoreCase true;` Makes every terminal match letters in either case, unless its own `ignoreCase` says otherwise. Defaults to false. Doesn't reach `@external` grammars.",
            ["external"] = "`@external Json \"json.xbnf\";` Another grammar, by an alias, whose shared rules parse islands in this one.",
            ["lexer"] = "`@lexer \"My.Lexers.MyLexer\";` or `@lexer Json \"My.JsonLexer\";` The type whose static Tokenize lexes this grammar, or an alias's, with its namespace before the last dot. Ignored, with a warning, by Parsley.Generator, which generates its lexers.",
        };
    }
}
