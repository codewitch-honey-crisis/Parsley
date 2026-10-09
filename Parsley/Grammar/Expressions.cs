using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// The right hand side of a production: an EBNF expression tree.
	/// Any node can carry attributes, written after a group as <c>( … )&lt;attrs&gt;</c>.
	/// </summary>
	public abstract class Expression : GrammarNode
	{
		public AttributeList Attributes { get; } = new AttributeList();

		/// <summary>
		/// A deep copy, including attributes and locations
		/// </summary>
		public abstract Expression Clone();
		protected T CloneBase<T>(T result) where T : Expression
		{
			result.CopyLocation(this);
			foreach (var a in Attributes)
				result.Attributes.Add(a.Clone());
			return result;
		}
		/// <summary>
		/// The direct children of this node
		/// </summary>
		public virtual IEnumerable<Expression> Children => Enumerable.Empty<Expression>();
		/// <summary>
		/// This node and everything under it, depth first
		/// </summary>
		public IEnumerable<Expression> Descendants()
		{
			yield return this;
			foreach (var c in Children)
				foreach (var d in c.Descendants())
					yield return d;
		}
		/// <summary>
		/// The expression in XBNF syntax
		/// </summary>
		public override string ToString() => XbnfWriter.Write(this);
	}

	/// <summary>
	/// A reference to a terminal or non-terminal. Written as a name, a "literal" or a 'pattern'.
	/// Literals and patterns are resolved to terminal names before analysis.
	/// </summary>
	public sealed class SymbolRef : Expression
	{
		public SymbolRef(string name) { Name = name; }
		SymbolRef() { }
		public static SymbolRef FromLiteral(string literal) => new SymbolRef { Literal = literal };
		public static SymbolRef FromPattern(string pattern) => new SymbolRef { Pattern = pattern };
		/// <summary>
		/// The symbol name. Null for a literal or pattern that hasn't been resolved yet.
		/// </summary>
		public string? Name { get; set; }
		/// <summary>
		/// Set when written as "literal"
		/// </summary>
		public string? Literal { get; set; }
		/// <summary>
		/// Set when written as 'pattern'
		/// </summary>
		public string? Pattern { get; set; }
		public override Expression Clone() => CloneBase(new SymbolRef { Name = Name, Literal = Literal, Pattern = Pattern });
	}

	public sealed class Sequence : Expression
	{
		public Sequence(IEnumerable<Expression> items) { Items.AddRange(items); }
		public Sequence(params Expression[] items) : this((IEnumerable<Expression>)items) { }
		public List<Expression> Items { get; } = new List<Expression>();
		public override IEnumerable<Expression> Children => Items;
		public override Expression Clone() => CloneBase(new Sequence(Items.Select(i => i.Clone())));
	}

	public sealed class Alternation : Expression
	{
		public Alternation(IEnumerable<Expression> options) { Options.AddRange(options); }
		public Alternation(params Expression[] options) : this((IEnumerable<Expression>)options) { }
		public List<Expression> Options { get; } = new List<Expression>();
		public override IEnumerable<Expression> Children => Options;
		public override Expression Clone() => CloneBase(new Alternation(Options.Select(o => o.Clone())));
	}

	/// <summary>
	/// <c>[ body ]</c>: zero or one
	/// </summary>
	public sealed class OptionalExpression : Expression
	{
		public OptionalExpression(Expression body) { Body = body ?? throw new ArgumentNullException(nameof(body)); }
		public Expression Body { get; set; }
		public override IEnumerable<Expression> Children { get { yield return Body; } }
		public override Expression Clone() => CloneBase(new OptionalExpression(Body.Clone()));
	}

	/// <summary>
	/// <c>{ body }</c>: zero or more, or <c>{ body }+</c>: one or more
	/// </summary>
	public sealed class Repeat : Expression
	{
		public Repeat(Expression body, int min = 0)
		{
			Body = body ?? throw new ArgumentNullException(nameof(body));
			if (0 > min || 1 < min) throw new ArgumentOutOfRangeException(nameof(min), "min must be 0 or 1");
			Min = min;
		}
		public Expression Body { get; set; }
		public int Min { get; }
		/// <summary>
		/// Set by left recursion elimination: each pass of the loop wraps what was parsed so far
		/// in a new node, so the tree comes out left-associative.
		/// </summary>
		public bool LeftFold { get; set; }
		public override IEnumerable<Expression> Children { get { yield return Body; } }
		public override Expression Clone() => CloneBase(new Repeat(Body.Clone(), Min) { LeftFold = LeftFold });
	}

	/// <summary>
	/// Matches nothing. Written as an empty alternative.
	/// </summary>
	public sealed class EmptyExpression : Expression
	{
		public override Expression Clone() => CloneBase(new EmptyExpression());
	}

	/// <summary>
	/// Builder helpers for constructing grammars in code. Strings become symbol references.
	/// Use <c>using static Parsley.Ebnf;</c>
	/// </summary>
	public static class Ebnf
	{
		static Expression _X(object item)
		{
			switch (item)
			{
				case Expression e: return e;
				case string s: return new SymbolRef(s);
				case null: throw new ArgumentNullException(nameof(item));
				default: throw new ArgumentException($"Expected an Expression or a symbol name, got {item.GetType().Name}");
			}
		}
		public static SymbolRef Sym(string name) => new SymbolRef(name);
		/// <summary>
		/// A literal terminal, resolved to the terminal declared with that text (or an implicit one)
		/// </summary>
		public static SymbolRef Lit(string literal) => SymbolRef.FromLiteral(literal);
		public static Expression Seq(params object[] items) => 1 == items.Length ? _X(items[0]) : new Sequence(items.Select(_X));
		public static Expression Alt(params object[] options) => 1 == options.Length ? _X(options[0]) : new Alternation(options.Select(_X));
		public static OptionalExpression Opt(params object[] items) => new OptionalExpression(Seq(items));
		public static Repeat Rep(params object[] items) => new Repeat(Seq(items), 0);
		public static Repeat Rep1(params object[] items) => new Repeat(Seq(items), 1);
		public static EmptyExpression Empty() => new EmptyExpression();
		/// <summary>
		/// Attaches an attribute to an expression and returns it
		/// </summary>
		public static T With<T>(this T expression, string name, object? value = null) where T : Expression
		{
			expression.Attributes.Set(name, value);
			return expression;
		}
	}

	/// <summary>
	/// Writes expressions and grammars back out as XBNF
	/// </summary>
	public static class XbnfWriter
	{
		public static string Write(Expression e)
		{
			var sb = new StringBuilder();
			_Write(e, sb, 0);
			return sb.ToString();
		}
		// precedence: 0 alternation, 1 sequence, 2 atom
		static void _Write(Expression e, StringBuilder sb, int context)
		{
			var hasAttrs = 0 < e.Attributes.Count;
			switch (e)
			{
				case OptionalExpression o:
					sb.Append("[ ");
					_Write(o.Body, sb, 0);
					sb.Append(" ]");
					break;
				case Repeat r:
					sb.Append("{ ");
					_Write(r.Body, sb, 0);
					sb.Append(" }");
					if (1 == r.Min) sb.Append('+');
					break;
				default:
					var prec = e switch { Alternation => 0, Sequence => 1, _ => 2 };
					var paren = hasAttrs || prec < context;
					if (paren) sb.Append("( ");
					switch (e)
					{
						case Alternation a:
							for (var i = 0; i < a.Options.Count; ++i)
							{
								if (0 < i) sb.Append(" | ");
								_Write(a.Options[i], sb, 1);
							}
							break;
						case Sequence s:
							for (var i = 0; i < s.Items.Count; ++i)
							{
								if (0 < i) sb.Append(' ');
								_Write(s.Items[i], sb, 2);
							}
							break;
						case SymbolRef sr:
							if (null != sr.Literal)
								sb.Append(GrammarAttribute.FormatValue(sr.Literal));
							else if (null != sr.Pattern)
								sb.Append('\'').Append(sr.Pattern.Replace("'", "\\'")).Append('\'');
							else
								sb.Append(sr.Name);
							break;
						case EmptyExpression:
							break;
					}
					if (paren) sb.Append(" )");
					break;
			}
			if (hasAttrs)
				sb.Append(e.Attributes.ToString());
		}
		public static string Write(Production p) => string.Concat(p.Name, p.Attributes.ToString(), "= ", Write(p.Expression), ";");
		public static string Write(TerminalDeclaration t) => string.Concat(t.Name, t.Attributes.ToString(), "= ", Write(t.Definition), ";");
		public static string Write(Grammar g)
		{
			var sb = new StringBuilder();
			foreach (var d in g.Directives)
				sb.AppendLine($"@{d.Key} {GrammarAttribute.FormatValue(d.Value)};");
			if (0 < g.Directives.Count) sb.AppendLine();
			foreach (var p in g.Productions)
				sb.AppendLine(Write(p));
			foreach (var t in g.Terminals)
				if (!t.IsImplicit)
					sb.AppendLine(Write(t));
			return sb.ToString();
		}
	}
}
