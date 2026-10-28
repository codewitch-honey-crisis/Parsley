# XBNF in Parsley

XBNF is the grammar format Parsley reads. One file holds both halves of a language: the **terminals**, which go to a lexer generator, and the **non-terminals**, which Parsley turns into a parser. A rule can also carry a **semantic action**, C# that evaluates its parse node, and Parsley then synthesizes an evaluator class alongside the parser (see [Semantic actions](#semantic-actions)). Operators can be written in one rule, tightest first, instead of one rule per level of precedence (see [Operator rules](#operator-rules)). Text in another language can be embedded as an **island**, lexed and parsed by another grammar, and a terminal can come from another lexer (see [Islands and other lexers](#islands-and-other-lexers)). This document covers the XBNF that Parsley's reader (`XbnfReader`, used by `Grammar.Parse` and `Grammar.ReadFrom`) accepts.

```xbnf
// based on the spec at json.org
Json<start>= Object | Array | Value;
Object= "{" [ Field { "," Field } ] "}";
Field= string ":" Value;
Array= "[" [ Value { "," Value } ] "]";
Value<collapsed>= string | number | Object | Array | Boolean | null;
Boolean= true | false;
number= '\-?(0|[1-9][0-9]*)(\.[0-9]+)?([Ee][\+\-]?[0-9]+)?';
string= '"([^\n"\\]|\\([btrnf"\\/]|(u[0-9A-Fa-f]{4})))*"';
true= "true";
false= "false";
null= "null";
lbracket<collapsed>= "[";
rbracket<collapsed>= "]";
lbrace<collapsed>= "{";
rbrace<collapsed>= "}";
colon<collapsed>= ":";
comma<collapsed>= ",";
whitespace<hidden>= '[\n\r\t ]+';
```

## File structure

A file is a sequence of **rules** and **directives**, in any order, each ending with `;`.

```xbnf
@directive "argument";
@directive Alias "argument";                  // @external and @lexer only
Name= expression;
Name<attribute, attribute=value>= expression;
Name<type="double">= expression => { /* C# */ };
Name<entry="Alias.Rule", after="t">;          // an island: no body
```

- **Comments** are `// to end of line` or `/* block */`. They can go anywhere whitespace can.
- **Whitespace and line breaks** are insignificant. A rule can span lines.
- **Names** start with a letter or `_`, then letters, digits or `_`. They are case-sensitive.
- **Rule order** matters in three places: the first non-terminal is the start symbol unless one is marked `start`; terminals are numbered in the order they're declared; and the lexer usually breaks ties between equal-length matches by declaration order.

The syntax of XBNF itself, written in XBNF:

```xbnf
File<start>= { Directive | Rule };
Directive= "@" name [ name ] ( literal | number ) ";";   // the second name only for @external and @lexer
Rule= name [ Attributes ] ( "=" Expression [ "=>" Action ] | ) ";";   // no body only for an island
Action= "{" … "}";                           // C#, read up to its matching brace
Expression= Sequence { "|" Sequence };
Sequence= { Primary [ Attributes ] };        // an empty sequence matches nothing
Primary= name | literal | pattern
       | "(" Expression ")"                  // group
       | "[" Expression "]"                  // optional
       | "{" Expression "}" [ "+" ];         // zero or more, or one or more
Attributes= "<" [ Attribute { "," Attribute } ] ">";
Attribute= name [ "=" Value ];
Value= literal | number | name;              // true, false, or a bare word read as a string
```

## Expressions

| Syntax | Meaning |
| --- | --- |
| `Name` | A reference to another rule, terminal or non-terminal |
| `"text"` | A literal: matches exactly this text |
| `'regex'` | A pattern: a regular expression for the lexer |
| `a b c` | Sequence: each in order |
| `a \| b \| c` | Alternation: any one of them |
| `( a b )` | Grouping |
| `[ a ]` | Optional: zero or one |
| `{ a }` | Repeat: zero or more |
| `{ a }+` | Repeat: one or more. The `+` must come right after the `}`, with no space. |
| `a \|` or `( )` | An empty alternative, which matches nothing |

Sequence binds tighter than alternation, so `a b | c` means `( a b ) | c`.

### Literals

`"…"` is literal text. These escapes are recognized: `\n` `\r` `\t` `\f` `\v` `\b` `\0` `\\` `\"` `\'` `\/` and `\uXXXX`. A literal can't span lines.

### Patterns

`'…'` is a regular expression, passed to the lexer generator as written. Parsley doesn't validate it, so any syntax your lexer generator supports works. The only change is that `\'` becomes `'`, because inside single quotes it can only mean a quote character. (Some engines read `\'` as an end-of-input anchor, so leaving it in could change the meaning.) Every other backslash is kept, so `\\'` is still an escaped backslash followed by the closing quote. A pattern can't span lines.

## Terminals and non-terminals

Every rule is one or the other, decided after the whole grammar, imports included, has been read:

- **By default, a rule is a terminal only if its right side is a single literal or a single pattern**: `true= "true";`, `ws= ' +';`. Attributes don't change that: `lbrace<collapsed>= "{";` is a terminal.
- **Anything else is a non-terminal** unless it's marked `<terminal>`: a rule that references another rule, even if everything it references is a terminal, so `Boolean= true | false;` is a non-terminal; and a rule that combines literals and patterns, so `sign= "+" | "-";` is a non-terminal whose `"+"` and `"-"` are implicit terminals.
- **`<terminal>` forces a terminal.** Such a rule may combine literals and patterns with any of the operators, and reference other terminals, whose regexes are inlined: `sign<terminal>= "+" | "-";`. It's an error if it references a non-terminal or itself.
- **`<terminal=false>` forces a non-terminal**, even for a lone literal or pattern.
- **A rule marked `start` or `shared` is never made a terminal automatically**, only with `<terminal>`. If every rule in the file would be a terminal, the first one is kept as a non-terminal so the grammar has something to parse.
- **Islands and contextual terminals are terminals by their attributes**: a rule with `entry` and no body, and a rule with `when` that is exactly one terminal.

### The regex each terminal gets

Each terminal carries its definition as written and a regular expression for the lexer generator (`TerminalDeclaration.Definition` and `.Regex`):

| Definition | Regex |
| --- | --- |
| A single pattern | Passed through as written, without the quotes |
| A single literal | The text with regex metacharacters escaped: ``\ . * + ? \| ( ) [ ] { } ^ $ / "``. Control characters become `\n`, `\t` and so on, or `\xHH`. |
| Anything else | Built from the constructs: sequence becomes concatenation, `\|` stays `\|`, `[ ]` becomes `?`, `{ }` becomes `*`, `{ }+` becomes `+`, and an empty alternative makes the rest optional. Referenced terminals are inlined. Parentheses are added where precedence needs them. |

For example, with `letter= '[a-z]';` and `digit= '[0-9]';`:

```xbnf
Ident<terminal>= letter { letter | digit | "_" };   // [a-z]([a-z]|[0-9]|_)*
```

### Case-insensitive terminals

A terminal that ignores case matches letters in either case. `ignoreCase` on a terminal says whether it does; `@ignoreCase true;` makes it the default for every terminal in the grammar, implicit ones included, and a terminal's own `ignoreCase` overrides the directive either way. Both default to `false`.

```xbnf
@ignoreCase true;

SELECT= "SELECT";                       // (?i)SELECT: matches select, Select, SELECT…
Name<ignoreCase=false>= '[A-Z][a-z]+';  // [A-Z][a-z]+: case-sensitive despite the directive
```

It works by putting `(?i)` in front of the terminal's regex, unless the inline modifiers it starts with already turn case-insensitivity on for all of it: `(?i)`, `(?si)` or `(?x)(?i)`, for instance. A scoped modifier such as `(?i:abc)` covers only its own group, so it doesn't count, and neither does `(?i)` anywhere but the start. A pattern that turns it back off with `(?-i)` keeps that part case-sensitive, so a pattern always has the last word. Writing `(?i)` at the start of a pattern yourself still works, and does the same thing for that one terminal.

A [`<terminal>`](#terminals-and-non-terminals) rule that inlines other terminals inlines each with its own case sensitivity. Where one differs from the rule it's inlined into, it's scoped to its part alone:

```xbnf
@ignoreCase true;

letter<ignoreCase=false>= '[a-z]';
digit= '[0-9]';
Ident<terminal>= letter { letter | digit };   // (?i)(?-i:[a-z])((?-i:[a-z])|[0-9])*
```

**Declaration order still decides ties.** The lexer gives text that two terminals match equally to the one declared first, and that includes a case-sensitive terminal and a case-insensitive one. To have both, declare the case-sensitive one first: it takes the exact text and the other takes every other spelling.

```xbnf
exact= "Select";                // only Select
any<ignoreCase>= "select";      // select, SELECT, sElEcT…: everything but Select
```

The other way around, `any` matches `Select` too and wins the tie, so `exact` is never lexed. Parsley warns when a literal that ignores case comes before one that differs from it only in case, which includes a literal used inline that becomes an implicit terminal after a declared one that already matches it: use the declared one by name. It can't tell for patterns, so for those keep the order in mind.

`ignoreCase` goes only on terminals, since only the lexer matches text. On a non-terminal, on a group inside one, or on a literal used inline, it's an error; on a contextual terminal or an island, neither of which this grammar's lexer produces, it has no effect and gets a warning. `@ignoreCase` applies to this grammar and what it `@import`s, following the same rule as other directives, but not to `@external` grammars: an island's grammar has its own `@ignoreCase`, or none.

### Literals used inline

A literal or pattern written directly inside a non-terminal, like `"{"` in `Object= "{" … "}";`, refers to a terminal:

- **If a terminal with the identical literal or pattern is declared**, the inline use refers to it. In JSON, `"{"` refers to `lbrace`.
- **Otherwise Parsley declares an implicit terminal** for it, after all the declared ones. Its name comes from the text:
  - words become `<word>Keyword`: `"if"` gives `ifKeyword`
  - punctuation is spelled out and joined with `_`: `"("` gives `lparen`, `"=="` gives `eq_eq`
  - anything else becomes `implicit`, `implicit2` and so on

Implicit terminals come last, so they lose ties in a lexer that prefers earlier declarations. To control a literal's priority, declare it explicitly where you want it, for example `ifKeyword= "if";` above your identifier pattern. Inline uses then refer to that declaration.

An inline use is a use, not a declaration, so it can't carry a terminal's attributes: every `"a"` in the grammar is the same terminal, and one use can't make it hidden without making them all so. `"a"<hidden>`, or any other attribute that belongs on a terminal's declaration (`collapsed`, `terminal`, `type`, `lexer`, `when` and the like), is an error. Declare the terminal with the attribute instead, as in `a<hidden>= "a";`. The attributes that go on an expression, such as `expected` and `policy`, are fine on an inline use.

The same goes for a terminal's own definition: in `x= "a"<hidden>;` the attribute is on the literal, not on `x`, so it's an error; write `x<hidden>= "a";`. Other attributes inside a terminal's definition do nothing there and are ignored with a warning.

## Attributes

Attributes go in `<…>` after a rule's name, or after any expression or group. They are separated by commas, and each is a bare name (meaning `true`) or `name=value`.

Values can be:
- a `"string"`
- a number
- `true` or `false`
- a bare word, read as a string: `policy=lazy` is the same as `policy="lazy"`

`null` isn't allowed. If an attribute appears twice, the last one wins, with a warning. Unknown attributes are kept but ignored, with a message.

### On non-terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `start` | flag | This rule is the start symbol. Otherwise the first non-terminal is. If several are marked, the first wins, with a warning. |
| `collapsed` | flag | The rule gets no node in the parse tree; its children go straight into its parent's node. The start rule can't be collapsed. |
| `terminal` | `true` / `false` | Forces the rule to be a terminal or a non-terminal (see above). |
| `nowarn` | flag | Suppresses the "unreachable from the start symbol" warning. |
| `expected` | string | How the rule is named in error messages when none of its alternatives match: `expected="a statement"` gives "Expected a statement but found …". By default the message lists every token the rule can start with. |
| `policy` | `greedy`, `lazy`, `first` | How to settle decisions in this rule that are ambiguous (see below). A group's own `policy` takes precedence. |
| `sync` | string | Extra terminals where error recovery may stop while this rule is being parsed. The value is terminal names or literal texts separated by spaces or commas: `sync="semi rbrace"`. |
| `type` | string | The C# type the rule's `EvaluateXxx` method returns, written verbatim: `type="double"`. Without it, the method returns `object`. Quote it, since a type like `List<int>` can't be a bare word. See [Semantic actions](#semantic-actions). |
| `state` | string | On the start rule only: the C# type of the evaluator's `State` and of the `state` argument to `Evaluate`, written verbatim: `state="MyContext"`. Without it, both are `object?`. On any other rule it's ignored, with a warning. |
| `entering` | method name | A method the parser calls, with no arguments, as it starts this rule. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `parsed` | method name | A method the parser calls with the finished parse node as this rule ends. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `operators` | flag | The rule's alternatives are operators, listed tightest first, and it's parsed by precedence climbing. See [Operator rules](#operator-rules). |
| `assoc` | `left`, `right` | On a production that is an operator form of an operator rule: how the form associates. See [Operator rules](#operator-rules). |
| `shared` | flag | Other grammars can parse islands with this rule: the parser gets a `TryParseXxx` entry point for it. A shared rule is never made a terminal automatically, and can't be collapsed. See [Islands and other lexers](#islands-and-other-lexers). |
| `factor` | `true` / `false` | Whether to [left factor](#left-factoring) this rule's alternations, whatever `@factor` says. `factor=false` also keeps the rule from being inlined to factor others. |
| `maxk` | number | The most tokens this rule's decisions may look at, instead of the `@maxk` limit. A group's own `maxk` takes precedence. |

### On terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `hidden` | flag | The lexer matches it but never reports it, so the parser never sees it (whitespace, comments). A hidden terminal can't be referenced from a non-terminal. |
| `collapsed` | flag | The parser checks it as usual but leaves it out of the parse tree (punctuation). |
| `sync` | flag | A strong restart point for error recovery anywhere in the grammar, typically `;` or `}`. |
| `terminal` | `true` / `false` | Forces terminal or non-terminal (see above). |
| `ignoreCase` | `true` / `false` | Whether the terminal matches letters in either case, whatever `@ignoreCase` says. `false` if neither is given. See [Case-insensitive terminals](#case-insensitive-terminals). |
| `when` | method name | Makes a contextual terminal, read by the parser from another terminal's tokens. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). With `lexer`, it's the lexer controller's instead: see [Terminals from other lexers](#terminals-from-other-lexers). |
| `matched` | method name | A method the parser calls with each token of this terminal it matches. See [Parse-time hooks](#parse-time-hooks-and-contextual-terminals). |
| `lexer` | name | Another lexer produces this terminal, where `on` and `when` say. See [Terminals from other lexers](#terminals-from-other-lexers). |
| `on` | string | With `lexer`: the terminals of the grammar's own lexer where the other lexer may take over. |
| `entry` | string | Makes an island, a rule with no body: the `@external` grammar's shared rule that parses it, as `Alias.Rule`. See [Islands](#islands). |
| `after` | string | On an island: the terminals it starts right after. |
| `nest`, `pop` | string | On an island: the external grammar's terminals that open a nested pair and end the island once balanced. They can be the same terminal, for a delimiter like a quote. |
| `popOnError` | `true` / `false` | On an island: whether an error from the external grammar's lexer ends it. `true` if not given. |
| `startsWith` | string | On an island: the external grammar's terminals it must start with. Where the first token its lexer reads is none of them, it doesn't start, and the next island after the same token is tried. See [Islands that share a trigger](#islands-that-share-a-trigger). |
| `expected`, `blockEnd` | string | Accepted for compatibility with older grammars. They currently have no effect. |

### On groups and expressions

Any primary expression can take attributes, which apply to that one occurrence only. They matter on a decision point: an alternation, `[ … ]` or `{ … }`.

| Attribute | Value | Effect |
| --- | --- | --- |
| `policy` | `greedy`, `lazy`, `first` | Settles this decision if it's ambiguous. |
| `expected` | string | Names this alternation in error messages. |
| `sync` | string | Like the rule attribute: adds terminals where recovery may stop, for the enclosing rule. |
| `assoc` | `left`, `right` | On any element of an infix form in an operator rule: how the form associates. See [Operator rules](#operator-rules). |
| `factor` | `true` / `false` | Whether to [left factor](#left-factoring) this alternation, and the ones inside it. |
| `maxk` | number | The most tokens this decision may look at, instead of the rule's or the grammar's limit. With a `policy`, `maxk=1` settles it on one token where the rest of the grammar may look further. |

```xbnf
S= ( A | B )<policy=first>;
Stmt= "if" Expr "then" Stmt [ "else" Stmt ]<policy=greedy> | other;
Block= "{" { Stmt }<sync="semi"> "}";
```

## Directives

| Directive | Effect |
| --- | --- |
| `@import "file.xbnf";` | Reads another file's rules in at this point, with the path relative to the importing file. A file is imported only once, so import cycles are harmless. Imported rules keep their own file name in messages. An imported file's directives don't override the importer's. |
| `@class "MyParser";`<br>`@class "My.Parsers.MyParser";` | The class name of the synthesized parser, with its namespace before the last dot. By default it's the file name in PascalCase plus `Parser`, so `json.xbnf` gives `JsonParser`, in no namespace. `@namespace`, which gave the namespace on its own, is no longer supported: it's an error that says what to write instead. |
| `@maxk 2;` | The most tokens a decision with fixed lookahead looks at; 4 if not given. A decision that needs more is predicted by ALL(\*) at parse time, or settled by its `policy`. A lower limit gives smaller predictors at the cost of more ALL(\*); a higher one, the reverse. It's part of the grammar so the same grammar always synthesizes the same parser. A `maxk` attribute on a rule or a decision sets its own limit. |
| `@factor true;` | [Left factors](#left-factoring) every alternation whose alternatives can start with the same token. `false`, or not given, leaves the grammar as written. A rule's or group's `factor` attribute overrides it. |
| `@ignoreCase true;` | Makes every terminal in the grammar match letters in either case, implicit terminals included. `false`, or not given, leaves case significant. A terminal's `ignoreCase` attribute overrides it. It doesn't reach `@external` grammars. See [Case-insensitive terminals](#case-insensitive-terminals). |
| `@evaluator "MyEvaluator";` | The class name of the synthesized evaluator, in the parser's namespace, so it can't give one of its own. It's used as written, with `@` added in front of a C# keyword. By default it's the file name in PascalCase plus `Evaluator`, so `json.xbnf` gives `JsonEvaluator`. Only matters when the grammar has [semantic actions](#semantic-actions); without any, it's ignored with a warning. |
| `@using "System.Text";` | Adds `using System.Text;` to the top of the synthesized file, so [semantic actions](#semantic-actions) can use the namespace's types without qualifying them. Give it once per namespace; it can appear any number of times. The argument must be a namespace name, such as `System.Text` or `My.@class`: not `static` or an alias. A namespace the file already imports, or one given twice, is written once, without a message. Usings from an `@import`ed file are added to the importer's. Only matters when the grammar has semantic actions; without any, it's ignored with a warning and the file is unchanged. |
| `@external Json "json.xbnf";` | Another grammar, known here by an alias, whose shared rules parse [islands](#islands) in this one. The path is relative to this file. The grammar is read on its own, as if it were being synthesized by itself: none of its rules or directives are merged into this one, and only its errors are reported here. A grammar can't embed itself, directly or through others. |
| `@lexer "BamcLexer";`<br>`@lexer Json "Foo.JsonLexer";` | The C# type whose static `Tokenize` lexes this grammar, or one of its aliases: an `@external` grammar, or a lexer that terminals name with `lexer=`, with its namespace before the last dot. See [Naming the lexers](#naming-the-lexers). |

Other directives are kept, with a warning that they're unknown. Every directive except `@using`, `@external` and `@lexer` takes one value, and if one is given more than once, the last wins. `@external` and `@lexer` take an alias first (optional for `@lexer`), and an `@import`ed file's are added unless the importer has one for that alias.

## Semantic actions

A non-terminal rule can end with `=>` and a block of C#, before the `;`. The block is a **semantic action**: the body of a method that turns that rule's node in the parse tree into a value.

```xbnf
@evaluator "ExprEvaluator";

Expr<start, type="double">= Expr add Term | Expr sub Term | Term => {
    if (1 == node.Children.Count)
        return EvaluateTerm(node.Children[0]);
    var left = EvaluateExpr(node.Children[0]);
    var right = EvaluateTerm(node.Children[2]);
    return add == node.Children[1].SymbolId ? left + right : left - right;
};
```

Actions run over the finished tree, not during the parse. The parser can look several tokens ahead before it commits to a branch, and it rebuilds nodes as it folds left recursion into loops, so the tree is the only reliable place to evaluate. Use the synthesized classes like this:

```csharp
var tree = ExprParser.Parse(tokens);
double value = ExprEvaluator.Evaluate(tree);
```

### The evaluator class

When the start rule has an action, Parsley synthesizes an evaluator class in the same file, after the parser. If the start rule has no action, no evaluator is synthesized, and an action on any other rule is an error. The class is `partial`, so you can add members, such as helpers your actions call, in another file. It has:

| Member | What it is |
| --- | --- |
| `public static T Evaluate(ParseNode node, S state)` | Evaluates a tree from the parser, starting at its root. It creates an evaluator and calls the start rule's method. `T` is the start rule's `type`, or `object`. `S` is the start rule's `state` type, and then `state` must be passed. Without a `state` type it's `object? state = null`. |
| `protected T EvaluateXxx(ParseNode node)` | One for each non-terminal that gets a node in the tree, so not for collapsed rules. `Xxx` is the rule name in PascalCase, and `T` is the rule's `type`, or `object`. The body is the rule's action. |
| `protected object Evaluate(ParseNode node)` | Calls the right `EvaluateXxx` for the node's symbol. Use it in an action when a child can be one of several non-terminals, as it can below a collapsed rule like `Value<collapsed>= Object \| Array;`. Inside the class, `Evaluate(child)` binds to this overload, not to the static one. |
| `protected S State` | The `state` passed to `Evaluate`, typed by the start rule's `state` attribute, or `object?`. |
| `protected const int xxx` | The same [symbol constants](#symbol-constants) the parser has, named as the symbols are written, so actions can test `node.Children[0].SymbolId == number`. |

### Writing an action

- The node is in `node`. Its `Children` are what the rule matched, in order. Terminals carry their text in `Value`, and non-terminals have their own children.
- Collapsed rules and collapsed terminals have no node, and their children sit directly in the parent. In `Power= Primary [ pow Unary ];` with `Primary<collapsed>= number | lparen Expr rparen;`, the first child of a `Power` node is either a `number` token or an `Expr` node, since `lparen` and `rparen` are collapsed too.
- Left recursion folds to the left. `E= E "+" T | T;` gives `E(E(T) "+" T)` for `1 + 2`. A node made by folding has three children here, and the innermost one has just the `T`.
- Return a value of the rule's `type`. The code is copied into the method as written, and Parsley doesn't insert casts, so each `return` needs a value C# converts to that type on its own. When you call `Evaluate(child)`, cast its result yourself.
- Call other rules' `EvaluateXxx` methods directly when you know which rule a child is.
- Anything the actions need from outside the tree, such as variables, options or a symbol table, goes in through `state` and is read back as `State`. Give the start rule a `state` type and `State` needs no cast:

  ```xbnf
  Calc<start, type="int", state="System.Collections.Generic.IDictionary<string, int>">= … => { … };
  Var<type="int">= name => { return State[node.Children[0].Value!]; };
  ```

  ```csharp
  int value = CalcEvaluator.Evaluate(tree, new Dictionary<string, int> { ["x"] = 5 });
  ```
- The generated file has `using System;`, `using System.Collections.Generic;` and `using Parsley.Runtime;`. For other namespaces, add an [`@using`](#directives) for each, such as `@using "System.Globalization";`, or write them out in full: `System.Globalization.CultureInfo.InvariantCulture`. A using in another file of your project doesn't help, since usings only apply to the file they're in (unless it's a `global using`).
- The action is read up to its matching `}`. Braces inside strings, character literals, comments and interpolation holes don't count, including verbatim, interpolated and raw strings. The code is re-indented to fit its method. The exception is a string literal that spans lines, since re-indenting would change it, so that code is copied exactly.
- The synthesized code maps what it copies from the grammar back to it with `#line` directives: each action's code, the declarations that name a rule's `type` or the start rule's `state`, and the usings from `@using`. So the compiler reports an error in an action at its line in the `.xbnf` file, and a debugger that follows `#line` can step through actions in it. Lines match exactly; columns can be off where an action was re-indented. `parsley` names the grammar file relative to the output file (or to the current directory when writing to stdout), and Parsley.Generator by its full path. `SynthesisOptions.LinePath` decides the path in code, and a grammar with no file gets no directives.
- An action can't be on a terminal or a collapsed rule, since neither has a node of its own.

### Rules without an action

A rule with no action still gets an `EvaluateXxx` method. Its node evaluates to its one non-terminal child, ignoring terminals, so chains like `Item= Wrapped | Word;` need no code. The exception is an operator form's production, which is evaluated by its operator rule's action (see [Evaluating operator rules](#evaluating-operator-rules)). A node with no non-terminal children, or with more than one, throws `InvalidOperationException` when it's evaluated. Watch for rules like `Unary= sub Unary | Power;`: without an action, the `-` would be ignored and the node would evaluate to its `Unary` child.

## Operator rules

Operators with precedence are usually written as a tower of rules, one per level, as in `Expr.xbnf`: `Expr` for `+` and `-`, `Term` for `*` and `/`, and so on down. A rule marked `operators` writes them all in one rule instead. Its alternatives are listed tightest first, and Parsley parses the rule by precedence climbing.

```xbnf
Expr<start, type="double", operators, expected="an expression">=
      Expr pow<assoc=right> Expr        // tightest
    | sub Expr                          // unary minus
    | Expr ( mul | div | mod ) Expr
    | Expr ( add | sub ) Expr           // loosest
    | number
    | lparen Expr rparen
    => { /* see ExprOperators.xbnf */ };
```

This is the same language as `Expr.xbnf`, with the same precedence: `-2^2` is `-(2^2)`, `2^-1` works, and `10 - 4 - 3` is `(10 - 4) - 3`. `ExprOperators.xbnf`, next to it, is the whole grammar with its action. The tower of rules still works as it always has; `operators` is opt-in, one rule at a time.

### Forms

Each alternative is a **form**, classified by whether it starts or ends with the rule's own name. Whatever lies between is its **operator**, and that can be any expression:

| Starts with the rule | Ends with it | Form | Examples |
| --- | --- | --- | --- |
| yes | yes | infix | `Expr add Expr`, `Expr question Expr colon Expr` |
| no | yes | prefix | `sub Expr`, `lparen name rparen Expr` (a cast) |
| yes | no | postfix | `Expr "++"`, `Expr lbracket Expr rbracket`, `Expr lparen [ Expr { comma Expr } ] rparen` |
| no | no | atom | `number`, `name`, `lparen Expr rparen` |

- **The rule inside an operator**, like the middle of a ternary or the index of `a[i]`, is an ordinary operand. It's parsed from the loosest level, as it is between parentheses, so `a ? b = c : d` works.
- **A group gives several operators one level:** `Expr ( add | sub ) Expr`.
- **An infix form with nothing between its operands**, `Expr Expr`, is juxtaposition, as in function application `f x`.
- **The rule needs at least one atom**, since that's what operators apply to.

### Precedence and associativity

- **Earlier alternatives bind tighter.** Atoms have no level, so they can go anywhere; listing them last reads best.
- **Infix forms are left associative** unless marked `assoc=right`: `a = b = c` is `a = (b = c)`. `assoc` goes on any element of the form, such as its operator (`Expr pow<assoc=right> Expr`) or its last operand (`Expr question Expr colon Expr<assoc=right>`), or on a [form's production](#forms-with-their-own-productions).
- **A prefix form's operand takes the operators that bind tighter than it.** With `^` above unary minus, `-2^2` is `-(2^2)`. With `*` below it, `-2 * 3` is `(-2) * 3`.
- **A prefix form can be any operand**, wherever it's listed: `2^-1` is `2^(-1)`, even though unary minus is looser than `^`.
- **A postfix form applies to everything to its left that binds tighter.** With indexing listed above `!`, `!a[0]` is `!(a[0])`.

### The tree

Every operator applied makes a node, with its children in the order they were read, and so does every atom. Operands are therefore always nodes:

| Form | Children |
| --- | --- |
| atom | what the atom matched |
| prefix | the operator, then the operand |
| infix | the left operand, the operator, then the right operand |
| postfix | the operand, then the operator |

As everywhere, collapsed terminals are left out. With the grammar above, where `lparen` and `rparen` are collapsed:

```
1 + 2 * 3      Expr(Expr(1) + Expr(Expr(2) * Expr(3)))
-2^2           Expr(- Expr(Expr(2) ^ Expr(2)))
(1 + 2) * 3    Expr(Expr(Expr(Expr(1) + Expr(2))) * Expr(3))
7              Expr(7)
```

So an action sees one of a few shapes, and can tell them apart by the number of children, or by the symbol of the operator.

### Forms with their own productions

An alternative can be a reference to a production whose right side is a single form. Its nodes are then that production's, and it can have an action of its own:

```xbnf
Expr<start, type="double", operators>=
      Power                    // tightest
    | Negate
    | Expr ( mul | div | mod ) Expr
    | Sum                      // loosest
    | number
    | lparen Expr rparen
    => { /* atoms, *, / and %, and Sum, which has no action */ };
Power<assoc=right>= Expr pow Expr => { return Math.Pow(EvaluateExpr(node.Children[0]), EvaluateExpr(node.Children[2])); };
Negate= sub Expr => { return -EvaluateExpr(node.Children[1]); };
Sum= Expr ( add | sub ) Expr;
```

- **Precedence still comes from where it's listed** in the operator rule.
- **`assoc` can go on the production** as well as on an element of its form.
- **The operator rule parses it.** It gets no parse method of its own, and its `entering` and `parsed` are ignored, with a warning; the operator rule's hooks run instead.
- **It can only be used as an alternative of its operator rule**, and only once.
- **It must be exactly one form.** To give several operators one level, group them: `Sum= Expr ( add | sub ) Expr;`, not `Sum= Expr add Expr | Expr sub Expr;`.
- **A reference to a production that isn't a form**, like `| Literal` with `Literal= number | string;`, is an atom like any other.
- **The root of the tree can be one of its nodes:** `1 + 2` gives a `Sum` node, not an `Expr` node.

### Evaluating operator rules

- **`EvaluateExpr` forwards** the nodes of forms whose productions have an action to their own `EvaluateXxx`, so `EvaluateExpr(child)` works on any operand, whatever form it is.
- **A form's production with no action is evaluated by the operator rule's action**, with a message saying so. In the grammar above, `Sum` nodes reach `Expr`'s action, which tells `+` from `-` by `node.Children[1].SymbolId`.
- **A form's production with no `type` returns `object`**, like any rule. Since every operand is evaluated through `EvaluateExpr`, the operator rule's `type` is the type of every operand's value.
- **When forms return different types**, such as arithmetic returning `int` and comparisons returning `bool`, leave the operator rule's `type` off (so `object`), or use a type of your own that holds either. Each form's production can still declare its exact type:

  ```xbnf
  E<start, operators>= Product | Less | number => { return int.Parse(node.Children[0].Value!); };
  Product<type="int">= E star E => { return (int)EvaluateE(node.Children[0]) * (int)EvaluateE(node.Children[2]); };
  Less<type="bool">= E lt E => { return (int)EvaluateE(node.Children[0]) < (int)EvaluateE(node.Children[2]); };
  ```
- **When the two types are written differently**, the forwarding casts, as in `return (double)EvaluateNegate(node);`, and the C# compiler decides whether that's allowed. `object` to `double` compiles and unboxes at run time, so it throws if the action actually returned something else, such as a boxed `int`. `bool` to `int` doesn't compile.

### How an operator rule is parsed

The rule gets one method, `ParseExpr(int minLevel = 0)`; in an ALL(\*) parser the return state comes first. `minLevel` is the loosest operator the call may apply:

1. It reads an operand: an atom, or a prefix form, whose own operand is read by calling `ParseExpr` with the form's level.
2. Then, while the next token starts an infix or postfix form: if the form's level is below `minLevel`, it returns, leaving the operator to the call waiting for this operand. Otherwise it makes the form's node around what it has so far and reads the operator. For an infix form, it reads the right operand by calling `ParseExpr` with the form's level plus one, or the form's level itself if the form is right associative.

Choosing among atoms and prefix forms, and among infix and postfix forms, are ordinary decisions. They use as much lookahead as they need, and ALL(\*) where no fixed amount will do: telling the cast `(a) b` from the parenthesized `(a) + b` takes four tokens.

Whether to apply another operator only ever looks at one token. Without precedence, the rule would be ambiguous there, since an operand's call could take the operator or leave it to its caller. Precedence is what settles that, at parse time, so there's no policy to give and no warning. A token that could start an operator is always taken as one, even where it could also follow the expression in the rest of the grammar.

Other things to know:

- **`entering` and `parsed` on the operator rule** run on every call, including the calls for operands, so `1 + 2` enters the rule twice: once for `2` and once for the whole.
- **`expected` on the operator rule** names it when no operand is found: "Expected an expression but found …".
- **Messages** say how many levels the rule has. `-r` shows it as `operators` with its levels, and shows each form's production as an operator form of it.

## Parse-time hooks and contextual terminals

Some languages can't be parsed from their tokens alone. In C, `T * x;` declares `x` if `T` names a type and multiplies otherwise, so the parser has to know which names are types, and that depends on the declarations it has already parsed. Two features handle this: hooks, which call your code as parsing goes, and contextual terminals, which let your code decide what a token is.

### Hooks

A hook names a method in the parser's partial class. Parsley declares it as a partial method, and you implement it in another part of the class:

| Attribute | On | Declared as | Called |
| --- | --- | --- | --- |
| `entering="M"` | non-terminal | `partial void M();` | as the rule starts, before any of it is read |
| `parsed="M"` | non-terminal | `partial void M(ParseNode node);` | as the rule ends, with its finished node. For a collapsed rule, the node its children went into. |
| `matched="M"` | terminal | `partial void M(Token token);` | each time the parser matches one of its tokens, after adding it to the tree |

A hook you don't implement does nothing, as C# partial methods do. Several rules can name the same method. Hooks run during parsing, so unlike [semantic actions](#semantic-actions), which evaluate a finished tree, they can affect the rest of the parse. If error recovery gives up partway through, some `parsed` hooks may not run for rules that were started.

### Contextual terminals

```xbnf
identifier= '[A-Za-z_][A-Za-z0-9_]*';
typedef_name<when="IsTypedefName">= identifier;
```

A rule with `when` whose whole definition is one terminal is a **contextual terminal**. The lexer never produces it: it isn't written to the lexer file, and it comes after every other terminal so lexer ids don't count it. Instead, each time the parser looks at an `identifier` token, it asks your method:

```csharp
private partial bool IsTypedefName(Token token, Token previous);
```

`previous` is the token before, as the lexer produced it (its SymbolId is -1 at the start of the input). When the method returns true, the parser treats the token as `typedef_name`. It asks every time it looks at the token, including when it looks ahead to choose between alternatives, so the answer reflects everything parsed up to that point. Unlike the other hooks, this method must be implemented, or the parser won't compile.

The parser may look several tokens ahead before it gets to them, and the hooks that change your state haven't run for those tokens yet. Basing the answer on the token and the one before it, rather than on where the parse has got to, keeps it the same at both times.

`c99.xbnf` and `C99Parser.Typedefs.cs` in `Parsley.Tests/c99` are a full example: they track C's scopes and typedef declarations to tell typedef names from identifiers.

The interpreter, `GrammarInterpreter`, takes a `ParseHooks` object with delegates in place of the methods.

## Islands and other lexers

Some text is in more than one language. A `.bamc` script's `add-headers` takes a JSON object, whose strings are lexed differently from bamc's and whose braces nest, which no single lexer pattern can match. A JavaScript `/` is division or the start of a regular expression, depending on the token before it. Both need the lexer to change partway through, and Parsley handles both the same way: a **lexer controller** in the runtime decides every switch as it lexes, from the tokens it has produced so far, and the parser just reads tokens.

- An **island** is text in another language, lexed and parsed by another grammar. The outer grammar sees it as one token.
- A **terminal from another lexer** is one token lexed by a different lexer, where the grammar's own lexer would get it wrong.

Because every switch is decided at lex time, a token is always lexed by the right lexer when it enters the parser's lookahead, however far the parser looks ahead, and nothing is ever lexed twice for the parser. The lexers themselves needn't know any of this.

### Islands

```xbnf
// bamc.xbnf
@external Json "json.xbnf";

AddHeaders= addHeadersKeyword Headers;
Headers<entry="Json.Object", after="addHeadersKeyword", nest="Json.lbrace", pop="Json.rbrace">;
```

```xbnf
// json.xbnf, changed for embedding: Object is shared, and whitespace leaves out line breaks
Json<start>= Object | Array;
Object<shared>= "{" [ Field { "," Field } ] "}";
...
whitespace<hidden>= '[\r\t ]+';
```

- **`@external Json "json.xbnf";`** declares the other grammar and gives it an alias. It's read on its own, as it would be synthesized by itself, and it knows nothing of being embedded: everything about the island is said in the outer grammar.
- **An island is a rule with no body**, marked with `entry`: the external grammar's alias and one of its productions, which must be marked `shared`. It's a terminal of the outer grammar, which no lexer produces: the controller does.
- **`after`** names the terminals the island starts right after, by name or literal text, separated by spaces or commas. It's required. The controller starts the island after every one of those tokens, wherever it appears, so a warning says so if anything else can follow one of them in the grammar. With `startsWith`, it only starts where the island's text begins with one of those terminals: see [Islands that share a trigger](#islands-that-share-a-trigger).
- **The island ends** at the first of these:
  - a `pop` token that balances every `nest` token before it. Both name the external grammar's terminals: `Json.rbrace`, or just `rbrace`, or a literal text. Without `nest`, the first `pop` ends it. Counting tokens rather than characters is exact: a `}` inside a JSON string is part of a string token, so it doesn't count.
  - When `nest` and `pop` name the same terminal, it's a delimiter that opens and closes, as a quote does, and can't nest: the first one opens, and the next one ends the island. Anything before the first one is part of the island, as it is with a separate `nest`.
  - a token the island's lexer can't match, unless `popOnError=false`. The island ends before it.
  - the end of the input.
- **When it ends**, the outer grammar's lexer carries on from the end of the island's last token. So text the island's lexer skipped as whitespace after that is lexed again by the outer lexer, and a line break that ends a JSON island is still bamc's `newline`.

A quoted island names one terminal for both. Here a selector island is the whole string, quotes included, so its own grammar can lex what's between them:

```xbnf
// bamc.xbnf
Selector<entry="Sel.QuotedSelector", after="clickKeyword", nest="Sel.quote", pop="Sel.quote">;
```

```xbnf
// selector.xbnf
QuotedSelector<shared>= quote SelectorList quote;
quote= '"';
```

#### Islands that share a trigger

Several islands can start after the same token, when what follows tells them apart. In bamc, a command's selector is CSS, `"div > p"`, or XPath with a prefix, `xpath "//div"` or `$"//div"`. Each island that can only start one way says so with `startsWith`, naming the external grammar's terminals its text must begin with:

```xbnf
// bamc.xbnf: tried in the order they're declared
XPath<entry="XPathEmbed.QuotedXPath", after="clickKeyword", startsWith="XPathEmbed.xpathOpen", nest="XPathEmbed.xpathOpen", pop="XPathEmbed.quote">;
Selector<entry="SelectorEmbed.QuotedSelector", after="clickKeyword", nest="SelectorEmbed.quote", pop="SelectorEmbed.quote">;
```

```xbnf
// xpath-embed.xbnf
QuotedXPath<shared>= xpathOpen Expr quote;
xpathOpen= '(xpath[ \t]+|\$)"';
quote= '"';
```

After a `click`, the controller tries the islands in the order they're declared:

- **An island with `startsWith`** is lexed one token ahead with its own lexer. If that token is one of the terminals it names, the island starts; otherwise the next island is tried. Hidden terminals don't count, so whitespace before the prefix is fine.
- **An island without `startsWith`** always starts. It's the fallback, so any island declared after it with the same trigger never starts there, and a warning says so.
- **If none starts**, there's no island: the outer grammar's own lexer goes on after the trigger, as it would have without one. So islands that all have `startsWith` can share a trigger with the outer grammar's own tokens.

Only the first token decides, and it's lexed by each candidate's own lexer, so the candidates' grammars needn't lex alike. Choosing never parses anything: an island that starts and then fails to parse reports its errors, and the islands after it aren't tried.

`popOnError` is on by default because it keeps a broken island from swallowing the rest of the input. In bamc, a line break is an error to JSON's lexer, so an unterminated object ends at the end of its line: the island reports the missing `}`, and bamc goes on with the next line. Turn it off for an island whose grammar recovers well on its own and whose outer grammar has few places to recover.

**The tree.** The island's token becomes a node of the island's symbol, here `Headers`, holding one child: the root of the tree the external grammar parsed, with the external grammar's symbol ids. Its problems are the outer parser's problems too, located in the whole text.

```
AddHeaders
  addHeadersKeyword "add-headers"
  Headers
    Object          <- json.xbnf's ids from here down
      Field
        string ""DNT""
        string ""1""
```

The token itself carries the island's text, from its first token to its last, and its `Tag` holds an `IslandParse` with the tree and the problems. A parser reading these tokens never sees inside an island, however far ahead it looks. In a semantic action, evaluate the island's tree with the external grammar's evaluator: its static `Evaluate` starts at whatever node it's given, a shared production's included.

### Shared productions and subordinate grammars

A production marked `shared` can parse an island for another grammar. The parser gets a static entry point for each, `TryParseObject(tokens, out result, out errors)`, which parses exactly that production and expects the end of the input after it, and its analysis treats the end of the input as able to follow it. A shared production is never made a terminal automatically, and can't be collapsed, since the island needs its node.

A grammar with shared productions and none marked `start` is **subordinate**: it's only parsed through its shared productions. It gets no `TryParse` or `Parse` of its own, every shared production is a root for the unreachable-production check, and none of the start rule's warnings apply. A grammar with a `start` and shared productions is both standalone and embeddable. Nothing marks a grammar as only subordinate.

### Terminals from other lexers

```xbnf
@lexer Regex "ES5RegexLexer";

regular_expression_literal<lexer=Regex, on="slash div_assign", when="RegexCanStart">= '/…/[a-z]*';
```

- **`lexer`** names another lexer, by an alias. That lexer produces this terminal instead of the grammar's own.
- **`on`** (required) names the terminals of the grammar's own lexer where the other lexer may take over.
- **`when`** (optional) names a static method the controller asks first:

  ```csharp
  private static partial bool RegexCanStart(Token token, Token previous);
  ```

  `token` is the `on` token as the grammar's lexer produced it, and `previous` the token before it, in the lexers' own ids. It's static because it runs as the text is lexed, before any parser has seen it, so it can't depend on parser state: deciding by the token before is what keeps the answer the same however far the parser looks ahead.

When the grammar's lexer produces an `on` token and `when` agrees, the controller lexes from that token's start with the other lexer. If what it lexes is this terminal, that's the token, and the grammar's lexer carries on after it; otherwise the original token stands.

When the rules for a switch are more than a token can tell, as JavaScript's are once `)` and `}` need to know what they closed, write your own lexer wrapper instead, as `Es5Test/ES5Tokenizer.cs` does, and name it with `@lexer`. The controller runs a wrapper like any other lexer.

### Naming the lexers

The controller calls lexers by a static method with this shape, the one Luthor lexers have:

```csharp
public static IEnumerable<Token> Tokenize(TextReader reader, long position, int line, int column, int tabWidth);
```

It reports each token's id counting the grammar's terminals from 0 in the order the parser numbers them, leaves out hidden terminals, and reports text it can't match as a token with id -1. A lexer from another generator needs only a small adapter with this shape.

`@lexer` names the type whose `Tokenize` lexes for the grammar itself, or for an alias, with its namespace before the last dot: `@lexer "My.Lexers.BamcLexer";`. A name with a namespace is used from the global namespace, so it means the same wherever the parser is; one without is used as written. Without one:

| Lexer for | Type used |
| --- | --- |
| the grammar itself | the file name plus `Lexer`: `bamc.xbnf` gives `BamcLexer` |
| an `@external` alias | the external grammar's own `@lexer`, else the alias plus `Lexer`, in the namespace the external grammar's `@class` gives unless the `@lexer` gives its own |
| a `lexer=` alias | the alias plus `Lexer` |

When the outer grammar and the external grammar both name a lexer for it, the outer grammar's wins: the external grammar stays unaware of being embedded. The external grammar's parser is its `@class`, or its file name plus `Parser`, in the namespace its `@class` gives.

The command line tool writes the lexers' `.lex` files, not their code, so the names it expects have to be given to `parsley-luthor` too: `@lexer "My.Lexers.BamcLexer";` goes with `parsley-luthor bamc.lex -c BamcLexer -n My.Lexers`.

The source generator works differently: see [With the source generator](#with-the-source-generator).

### What gets synthesized

A grammar with islands or terminals from other lexers is **compound**. Its parser has:

| Member | What it is |
| --- | --- |
| `static LexerMode CreateLexerMode(TokenizeFunc tokenize)` | What the controller needs to lex this grammar with the given lexer: its islands, with their external grammars' lexer modes, and its terminals from other lexers. Grammars with shared productions have one too, so other grammars can embed them. |
| `static IEnumerable<Token> Tokenize(TextReader reader, …)`<br>`static IEnumerable<Token> Tokenize(string text, …)` | The grammar's tokens, through a `LexerController` over its own lexer |
| `static ParseNode Parse(TextReader reader)`<br>`static ParseNode Parse(string text)` | Lexes and parses |

```csharp
var tree = BamcParser.Parse(reader);
```

With the command line tool, synthesize each external grammar too, as you would by itself: its parser has the shared productions' entry points and the `CreateLexerMode` the outer parser calls, and it needs its own engine's base class (`-b`) if that differs from the outer grammar's. The tool writes the grammar's own lexer's terminals with `-l`, leaving out islands and terminals from other lexers. Each `lexer=` alias gets a file of its own beside it: `-l es5.lex` also writes `es5.Regex.lex`. Lexer ids count each lexer's terminals from 0 in that file's order, which is the order the parser numbers them.

### With the source generator

One `[Parser("bamc.xbnf")]` class gets everything: bamc's parser and lexer, and for each `@external` grammar, a class nested in it named after the alias, holding that grammar's parser and lexer (and evaluator, if it has semantic actions), with its symbol constants. So `BamcParser.Json.Object` is json.xbnf's `Object` id, for looking at an island's tree. Every lexer is generated: a `lexer=` alias gets one from its terminals, named after it (`RegexLexer`). As always with the generator, `@class`, `@evaluator` and `@lexer` are ignored, the outer grammar's and the external grammars' alike, each with a warning where it's written. `Tokenize`, `Parse` and `TryParse` lex through the controller, and a grammar with shared productions has `TryParseXxx` and `CreateLexerMode` too. `BamcGenTest` is an example.

### The lexer controller

`LexerController` (in the runtime) is an `IEnumerable<Token>` over text in a `TextReader` or a string, given a `LexerMode`:

- **It owns the text.** It reads the reader into a buffer of its own, and gives each lexer a reader over that buffer from where it's to start, telling it the position, line and column there. It keeps only what a lexer might still restart from, and the text of an island being read, so a large document isn't held in memory.
- **It switches by restarting.** At every switch, the lexer that takes over starts fresh at the end of the last token produced, so it doesn't matter how far any lexer read ahead. An island's token is produced when the island's grammar has parsed it.
- **It finds errors by id.** A lexer reports text it can't match as a token with id -1, holding the text it skipped, as Luthor lexers do. That's what ends an island with `popOnError`.
- **It counts lines and columns** as Luthor lexers do: `\n` ends a line, a tab moves to the next tab stop, other control characters take no width, and a surrogate pair is one column.

It relies on lexers reporting each token's `Value` as exactly the text it matched. A token a wrapper makes up, with an empty value, is fine: it never moves the point a lexer restarts from back.

A lexer that keeps state between tokens, as `ES5Tokenizer` does with its brackets, starts fresh when it's restarted after an island. `Token.Tag` is there for such a lexer to carry its state on each token, `null` unless it does, and `LexerMode.Resume`, if given, restarts the lexer from the last token's `Tag`. Nothing in XBNF sets these up yet.

## How rules become a parser

None of this needs to be written in the grammar, but it explains the messages Parsley gives and the code it produces.

### Lookahead

Each decision point (each `|`, `[ ]` and `{ }`) uses the fewest tokens of lookahead that tell its branches apart, up to 4 by default, or a `maxk` on the decision or its rule. Most decisions need one token. Ones that need more are noted in the synthesized code.

### Ambiguity

A decision whose branches still overlap after 4 tokens (or the `@maxk` limit, or its own `maxk`) either needs more lookahead or is really ambiguous. With the engine chosen automatically (the default), Parsley leaves it to ALL(\*), which looks as far ahead as it takes at parse time, with a message saying so. If the input really is ambiguous there, ALL(\*) settles it the way the defaults below do: the first alternative wins, and a loop or optional keeps matching.

A `policy` settles the decision when the grammar is analyzed instead, on every engine, which keeps a grammar like the dangling `else` LL(1):

- **`greedy`:** a loop keeps going and an optional is taken. It's what makes `else` bind to the nearest `if`.
- **`lazy`:** a loop stops and an optional is skipped.
- **`first`:** the earliest alternative wins. On a loop or optional it acts like `greedy`.

With the LL(1) or LL(k) engine forced (`-e ll1`, `-e llk`), a loop or optional with no `policy` is settled greedily with a warning, and an alternation with no `policy` is an error.

A rule that can't match any finite input (every way through it leads back into itself, as in `A= a A;`) gets a warning, since parsing it can never succeed.

A policy settles what's still ambiguous after looking as far as the decision may. To settle it on one token where the rest of the grammar looks further, give the decision `maxk=1` too: in XPath, a `*` right after a lone `/` is a name test, not a multiplication, which is

```xbnf
AbsoluteLocationPath= slash [ RelativeLocationPath ]<policy=greedy, maxk=1> | dslash RelativeLocationPath;
```

### Left factoring

Alternatives that start with the same tokens need more than one token of lookahead to tell apart. Asked to, with `@factor true;` or `<factor>` on a rule or a group, Parsley factors those tokens out in front, so one token is enough: `A= x B | x C;` is parsed as `A= x ( B | C );`. It does this to every alternation it's asked to, and says which, and on what tokens, in a message.

The shared tokens are often inside the rules the alternatives start with, a rule or two down. In XPath, a step and a function call both start with a name:

```xbnf
@factor true;
PathExpr= LocationPath | FilterExpr [ ( slash | dslash ) RelativeLocationPath ];
LocationPath<collapsed>= RelativeLocationPath | AbsoluteLocationPath;
RelativeLocationPath= Step { ( slash | dslash ) Step };
Step= [ axis | at ] NodeTest { Predicate } | dot | dotdot;
FilterExpr= PrimaryExpr { Predicate };
PrimaryExpr= variable | lparen Expr rparen | Literal | number | FunctionCall;
FunctionCall= ident lparen [ Expr { comma Expr } ] rparen;
```

To bring the name out, Parsley inlines the rules along the way, and splits their alternations, optionals and loops into alternatives where it has to, until each alternative starts with a token. Then it factors the tokens they share, and does the same again with what each goes on with. `PathExpr` above takes two tokens without factoring, and one with it.

**It's all or nothing, one alternation at a time.** Factoring is kept only where it works: where the alternation, and every alternation factoring made of what its alternatives go on with, is decided on one token. Where it isn't, which is usual in a grammar that's ambiguous there, factoring would only have inlined rules and left more decisions for ALL(\*), so the alternation is left as written, with a message saying so, and decided by lookahead or ALL(\*) as it would have been.

**The tree doesn't change.** No rule or node is added: what factoring writes are groups, which have no nodes. An inlined rule still makes its own node, where a call to it would have. The node takes over the tokens factoring moved in front of the decision, and is located where the rule started. A collapsed rule still makes none. So `id('x')/a` gives the same tree, every node at the same line and column, factored or not, and so does all input in the language. A rule's `parsed` hook still runs, with its node, and the rule still has its place in error recovery.

**What's ambiguous stays settled the same way.** Where input can be parsed more than one way, the earliest alternative wins, factored or not: factoring never moves one alternative past another that could start the same way.

**Input that isn't in the language** is still an error, and the error is usually the same one, at the same token: a factored alternation still reports everything its alternatives can start with. Usually, not always: where an LL parser notices an error depends on where it commits to an alternative, which factoring moves, so on some input the first error comes a token or two later or earlier, or names different tokens. Recovery after it can differ too, since tokens moved in front of a decision were read before the rules they belong to were entered.

Factoring leaves an alternation as written, and lookahead or ALL(\*) decides it, where the shared tokens can only be brought out:
- through a rule with an `entering` hook, which would run after the tokens moved in front of it instead of before,
- through a rule marked `factor=false`,
- through an operator rule or one of its forms, which are parsed by precedence climbing,
- through a left-recursive loop, or a start that can match nothing in a way it can't split,
- or by growing the rule by more than 600 expression nodes, which it says in a message.

An alternation where an alternative can match nothing isn't factored either, since what follows the alternation decides that one.

It's off by default because it inlines: the synthesized parser has the inlined rules' code in more places, and a rule's `entering` hook keeps it from being inlined.

### Left recursion

Left recursion is rewritten automatically:

- **Direct left recursion becomes a loop.** `E= E "+" T | T;` is parsed as `T { "+" T }`. The tree still comes out left-associative: `1 - 2 - 3` gives `E(E(1 - 2) - 3)`.
- **Indirect left recursion** (`A= B x; B= A y | z;`) is made direct by inlining, with a warning that the inlined rule loses its own node on that path.
- **Left recursion through a nullable prefix** (`A= N A x;` where `N` can match nothing) is an error.
- **A rule that derives exactly itself** (`A= A;`) is an error.
- **An [operator rule](#operator-rules)** isn't rewritten as a loop. Its operator forms start or end with the rule on purpose, and precedence climbing parses them.

### Errors in other places

| Problem | Level |
| --- | --- |
| A name defined twice | Error |
| A reference to an undefined name | Error |
| A hidden terminal referenced from a non-terminal | Error |
| A loop whose body can match nothing (`{ [ a ] }`) | Error |
| A rule with a code body (`Name<virtual> { … }`) | Error: virtual productions aren't supported |
| A semantic action on a rule other than the start rule, when the start rule has none | Error |
| A semantic action on a terminal or a collapsed rule | Error |
| `type` on a collapsed rule | Warning: it has no node, so the type is ignored |
| `state` on a rule other than the start rule | Warning: it's ignored |
| `type` or `state` that isn't a string, such as `type=5` | Error |
| The evaluator and the parser named the same | Error when synthesizing |
| A symbol whose [constant](#symbol-constants) can't have its name, because the synthesized code already uses it | Warning when synthesizing: the constant gets a number after it |
| A non-terminal unreachable from the start symbol | Warning, unless `nowarn` |
| `lexer` or `on` on a rule that isn't a terminal | Error |
| `@factor` that isn't `true` or `false` | Error |
| `factor` that isn't `true` or `false` | Warning |
| `maxk` that isn't a whole number of at least 1 | Error |
| `@ignoreCase` or `ignoreCase` that isn't `true` or `false` | Error |
| `ignoreCase` on a non-terminal, on a group in one, on a literal used inline, or inside a terminal's definition | Error |
| `ignoreCase` on a contextual terminal or an island | Warning: it has no effect |
| A literal that's never lexed because a literal declared before it ignores case and differs from it only in case | Warning |
| An alternation left factored | Message: which, and on what tokens |
| An alternation factoring would grow too much, or couldn't make decidable on one token | Message: it's left as written |
| [Operator rules](#operator-rules): | |
| An operator rule that's collapsed | Error |
| An operator rule with no atom | Error |
| An atom, or the operator of a prefix or postfix form, that can match nothing | Error |
| An alternative that leads back to the operator rule before reading anything, other than by starting with it (`E<operators>= E "+" E \| P; P= E "." name \| name;`) | Error: write it as an operator form instead |
| A form's production used anywhere but as an alternative of its operator rule, or used twice | Error |
| A form's production with more than one alternative, all of them forms | Error: group the operators instead, as in `E ( a \| b ) E` |
| A form's production that's collapsed or is an operator rule itself | Error |
| `assoc` that isn't `left` or `right`, or both on one form | Error |
| `assoc` on a form that isn't infix, or outside an operator rule | Warning: it's ignored |
| `entering` or `parsed` on a form's production | Warning: it's ignored |
| An operator rule with no operator forms | Warning |
| A form's production with no action, when its operator rule has one | Message: the operator rule's action evaluates it |
| [Islands and other lexers](#islands-and-other-lexers): | |
| An `@external` file that can't be read, or that leads back to a grammar being read | Error |
| An island's `entry` that isn't an `@external` alias and one of its productions, or names a production that isn't `shared` | Error |
| An island with no `after`, or one naming a terminal that's hidden, contextual or an island | Error |
| `nest` or `pop` naming a terminal the external grammar's lexer never reports | Error |
| `nest` without `pop` | Warning |
| `startsWith` naming a terminal the external grammar's lexer never reports, or naming none | Error |
| An island declared after one without `startsWith` that shares its trigger | Warning: it never starts there |
| A rule with no body that isn't an island, or an island with a body | Error |
| An island no production uses, or an `@external` no island uses | Warning |
| A terminal `after` names that something other than the island can follow | Warning: there, the island starts anyway |
| `lexer` without `on`, `on` naming a terminal the grammar's lexer never reports, or `lexer` naming an `@external` alias | Error |
| A shared rule that's collapsed, or `shared` on a terminal | Error |
| `@lexer` with an alias nothing uses | Warning |

Messages carry the file, line and column of the rule or expression they're about.

## Symbol ids

The synthesized parser numbers symbols in this order:

1. non-terminals, in rule order
2. terminals, in declaration order, including hidden ones, followed by implicit terminals. Then terminals from other lexers, grouped by lexer; then islands; then contextual terminals. The grammar's own lexer produces only the first group, so its ids match.
3. `#EOS` (end of input) and `#ERROR` (unrecognized input)

A lexer reports terminals numbered from 0 in that same terminal order, and text it can't match as -1. The parser adds the number of non-terminals as each token arrives. A negative id is treated as `#ERROR`.

## Symbol constants

The synthesized parser has a public `const int` for each symbol's id, and the evaluator has the same ones, protected. Each is named exactly as the symbol is written in the grammar, without recasing: `typedef_name` is `typedef_name`, `number` is `number`, `Expr` is `Expr`, and an implicit terminal's is its generated name, such as `ifKeyword` or `lparen`. Then come `EndOfInput` and `ErrorSymbol`, for `#EOS` and `#ERROR`, and `SymbolCount`.

- **A C# keyword gets an `@` in front**, as C# requires: `struct` is `@struct`, `string` is `@string`. So do the words C# reads as something else where the constants are used, in patterns like `is (a or b)`, case labels and declarations: `not`, `_`, `nint`, `nuint` and `partial`. Write the `@` when you use them too, as in `previous.SymbolId is @struct or @union`. Other contextual words, such as `var`, `and`, `or` and `value`, are used as written.
- **A name the synthesized code already uses gets a number after it**, with a warning that says which symbol and why, so `node` becomes `node2`. Rename the symbol to keep its name. Those names are:
  - the parser's and evaluator's own names and members, such as `Parse`, `Current`, `Errors`, `Error`, `State`, the `ParseXxx`, `EvaluateXxx` and `TryParseXxx` methods built from the rule names, hook methods, and `s_tables`
  - the parameters and locals the synthesized methods declare around the constants, or that hooks and actions are given: `node`, `parent`, `token`, `previous`, `tokens`, `result`, `errors`, `parser`, `state`, `evaluator`, `only`, `child`, `returnState`, `minLevel`, `tokenize`, `reader`, `text`, `position`, `line`, `column` and `tabWidth`. A constant with one of those names would be hidden by it.
  - the types the code names without qualifying them, such as `Token`, `ParseNode`, `ParseError`, `IEnumerable` and `System`, and `dynamic`, which C# reads as the type in a pattern even written `@dynamic`
  - `EndOfInput`, `ErrorSymbol` and `SymbolCount`
  - with the source generator, the names of the class's own members and the classes it generates in it. See [the generator's readme](Parsley.Generator/README.md#whats-generated).
- **A grammar built in code can use names XBNF can't.** Anything that can't be in an identifier becomes `_`, and a name starting with a digit gets a `_` in front, so `x-y` is `x_y`. If two symbols end up with the same name, the second gets a number after it, with a warning.

Your own code beside the parser, such as a hook or an action, can hide a constant the same way: a parameter or local named `declarator` hides the `declarator` constant inside that method, so name it something else.