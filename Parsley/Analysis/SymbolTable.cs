using System;
using System.Collections.Generic;

namespace Parsley
{
	/// <summary>
	/// Integer ids for every symbol: non-terminals first (in production order), then terminals
	/// (in declaration order, including hidden and implicit ones), then #EOS and #ERROR.
	/// Synthesized parsers use these ids as their constants; lexers must report the same ids.
	/// </summary>
	public sealed class SymbolTable
	{
		readonly List<string> _names = new List<string>();
		readonly Dictionary<string, int> _ids = new Dictionary<string, int>();
		readonly List<Production?> _productions = new List<Production?>();
		readonly List<TerminalDeclaration?> _terminals = new List<TerminalDeclaration?>();

		public const string EndOfInputName = "#EOS";
		public const string ErrorName = "#ERROR";

		internal SymbolTable(Grammar g)
		{
			foreach (var p in g.Productions)
				_Add(p.Name, p, null);
			NonTerminalCount = _names.Count;
			foreach (var t in g.Terminals)
				_Add(t.Name, null, t);
			EndOfInput = _Add(EndOfInputName, null, null);
			Error = _Add(ErrorName, null, null);
		}
		int _Add(string name, Production? p, TerminalDeclaration? t)
		{
			var id = _names.Count;
			_names.Add(name);
			_ids[name] = id;
			_productions.Add(p);
			_terminals.Add(t);
			return id;
		}

		public int Count => _names.Count;
		/// <summary>
		/// Non-terminals have ids 0 to NonTerminalCount - 1
		/// </summary>
		public int NonTerminalCount { get; }
		public int EndOfInput { get; }
		public int Error { get; }
		public string GetName(int id) => _names[id];
		public int GetId(string name) => _ids.TryGetValue(name, out var id) ? id : -1;
		public bool IsNonTerminal(int id) => id < NonTerminalCount;
		public bool IsTerminal(int id) => id >= NonTerminalCount;
		public Production? GetProduction(int id) => _productions[id];
		public TerminalDeclaration? GetTerminal(int id) => _terminals[id];
		public IReadOnlyList<string> Names => _names;
		/// <summary>
		/// How a symbol reads in an error message: a literal terminal shows its text in quotes
		/// </summary>
		public string Display(int id)
		{
			if (id == EndOfInput) return "end of input";
			if (id == Error) return "an unrecognized token";
			var t = _terminals[id];
			if (null != t && null != t.Literal) return string.Concat("\"", t.Literal, "\"");
			return _names[id];
		}
		/// <summary>
		/// True for terminals that are checked but left out of the parse tree
		/// </summary>
		public bool IsCollapsedTerminal(int id) => _terminals[id]?.IsCollapsed ?? false;
	}
}
