# XBNF in Parsley

XBNF is the grammar format Parsley reads. One file holds both halves of a language: the **terminals**, which go to a lexer generator, and the **non-terminals**, which Parsley turns into a parser. This document covers the XBNF that Parsley's reader (`XbnfReader`, used by `Grammar.Parse` and `Grammar.ReadFrom`) accepts.

```xbnf
// based on the spec at json.org
Json<start>= Object | Array;
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
Name= expression;
Name<attribute, attribute=value>= expression;
```

- **Comments** are `// to end of line` or `/* block */`. They can go anywhere whitespace can.
- **Whitespace and line breaks** are insignificant. A rule can span lines.
- **Names** start with a letter or `_`, then letters, digits or `_`. They are case-sensitive.
- **Rule order** matters in three places: the first non-terminal is the start symbol unless one is marked `start`; terminals are numbered in the order they're declared; and the lexer usually breaks ties between equal-length matches by declaration order.

The syntax of XBNF itself, written in XBNF:

```xbnf
File<start>= { Directive | Rule };
Directive= "@" name literal ";";
Rule= name [ Attributes ] "=" Expression ";";
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

- **By default, a rule is a terminal only if it references no symbols.** Its right side is made only of literals and patterns, possibly combined with operators: `true= "true";`, `sign= "+" | "-";`, `ws= ' +';`.
- **Any rule that references another rule is a non-terminal**, even if everything it references is a terminal. So `Boolean= true | false;` is a non-terminal.
- **`<terminal>` forces a terminal.** Such a rule may reference other terminals, and their regexes are inlined. It's an error if it references a non-terminal or itself.
- **`<terminal=false>` forces a non-terminal**, even for a rule with no references.
- **A rule marked `start` is never made a terminal automatically**, only with `<terminal>`. If every rule in the file would be a terminal, the first one is kept as a non-terminal so the grammar has something to parse.

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

### Literals used inline

A literal or pattern written directly inside a non-terminal, like `"{"` in `Object= "{" … "}";`, refers to a terminal:

- **If a terminal with the identical literal or pattern is declared**, the inline use refers to it. In JSON, `"{"` refers to `lbrace`.
- **Otherwise Parsley declares an implicit terminal** for it, after all the declared ones. Its name comes from the text:
  - words become `<word>Keyword`: `"if"` gives `ifKeyword`
  - punctuation is spelled out and joined with `_`: `"("` gives `lparen`, `"=="` gives `eq_eq`
  - anything else becomes `implicit`, `implicit2` and so on

Implicit terminals come last, so they lose ties in a lexer that prefers earlier declarations. To control a literal's priority, declare it explicitly where you want it, for example `ifKeyword= "if";` above your identifier pattern. Inline uses then refer to that declaration.

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

### On terminal rules

| Attribute | Value | Effect |
| --- | --- | --- |
| `hidden` | flag | The lexer matches it but never reports it, so the parser never sees it (whitespace, comments). A hidden terminal can't be referenced from a non-terminal. |
| `collapsed` | flag | The parser checks it as usual but leaves it out of the parse tree (punctuation). |
| `sync` | flag | A strong restart point for error recovery anywhere in the grammar, typically `;` or `}`. |
| `terminal` | `true` / `false` | Forces terminal or non-terminal (see above). |
| `expected`, `blockEnd` | string | Accepted for compatibility with older grammars. They currently have no effect. |

### On groups and expressions

Any primary expression can take attributes, which apply to that one occurrence only. They matter on a decision point: an alternation, `[ … ]` or `{ … }`.

| Attribute | Value | Effect |
| --- | --- | --- |
| `policy` | `greedy`, `lazy`, `first` | Settles this decision if it's ambiguous. |
| `expected` | string | Names this alternation in error messages. |
| `sync` | string | Like the rule attribute: adds terminals where recovery may stop, for the enclosing rule. |

```xbnf
S= ( A | B )<policy=first>;
Stmt= "if" Expr "then" Stmt [ "else" Stmt ]<policy=greedy> | other;
Block= "{" { Stmt }<sync="semi"> "}";
```

## Directives

| Directive | Effect |
| --- | --- |
| `@import "file.xbnf";` | Reads another file's rules in at this point, with the path relative to the importing file. A file is imported only once, so import cycles are harmless. Imported rules keep their own file name in messages. An imported file's directives don't override the importer's. |
| `@namespace "My.Parsers";` | The namespace of the synthesized parser. Without it, the parser has no namespace. |
| `@class "MyParser";` | The class name of the synthesized parser. By default it's the file name in PascalCase plus `Parser`, so `json.xbnf` gives `JsonParser`. |

Other directives are kept, with a warning that they're unknown.

## How rules become a parser

None of this needs to be written in the grammar, but it explains the messages Parsley gives and the code it produces.

### Lookahead

Each decision point (each `|`, `[ ]` and `{ }`) uses the fewest tokens of lookahead that tell its branches apart, up to 4 by default. Most decisions need one token. Ones that need more are noted in the synthesized code.

### Ambiguity

A decision whose branches still overlap after 4 tokens is ambiguous. It's settled by `policy`:

- **`greedy`:** a loop keeps going and an optional is taken. This is the default for `[ ]` and `{ }`, with a warning, and it's what makes `else` bind to the nearest `if`.
- **`lazy`:** a loop stops and an optional is skipped.
- **`first`:** the earliest alternative wins. On a loop or optional it acts like `greedy`.

An ambiguous alternation with no `policy` is an error. Add `<policy=first>` if the earliest alternative should win.

### Left recursion

Left recursion is rewritten automatically:

- **Direct left recursion becomes a loop.** `E= E "+" T | T;` is parsed as `T { "+" T }`. The tree still comes out left-associative: `1 - 2 - 3` gives `E(E(1 - 2) - 3)`.
- **Indirect left recursion** (`A= B x; B= A y | z;`) is made direct by inlining, with a warning that the inlined rule loses its own node on that path.
- **Left recursion through a nullable prefix** (`A= N A x;` where `N` can match nothing) is an error.
- **A rule that derives exactly itself** (`A= A;`) is an error.

### Errors in other places

| Problem | Level |
| --- | --- |
| A name defined twice | Error |
| A reference to an undefined name | Error |
| A hidden terminal referenced from a non-terminal | Error |
| A loop whose body can match nothing (`{ [ a ] }`) | Error |
| A rule with a code body (`Name<virtual> { … }`) | Error: virtual productions aren't supported |
| A non-terminal unreachable from the start symbol | Warning, unless `nowarn` |

Messages carry the file, line and column of the rule or expression they're about.

## Symbol ids

The synthesized parser numbers symbols in this order:

1. non-terminals, in rule order
2. terminals, in declaration order, including hidden ones, followed by implicit terminals
3. `#EOS` (end of input) and `#ERROR` (unrecognized input)

A lexer reports terminals numbered from 0 in that same terminal order. The parser adds the number of non-terminals as each token arrives. A negative id, or one past the last terminal, is treated as `#ERROR`.