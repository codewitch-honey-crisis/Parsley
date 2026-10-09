using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	/// <summary>
	/// A string of at most k terminal ids. It's only shorter than k when it ends in #EOS,
	/// or while it's still being built.
	/// </summary>
	public sealed class LookaheadString : IEquatable<LookaheadString>, IReadOnlyList<int>
	{
		readonly int[] _ids;
		readonly int _hash;
		public static readonly LookaheadString Empty = new LookaheadString(Array.Empty<int>());

		public LookaheadString(params int[] ids)
		{
			_ids = ids;
			var h = 17;
			foreach (var i in ids) h = unchecked(h * 31 + i);
			_hash = h;
		}
		public int Count => _ids.Length;
		public int this[int index] => _ids[index];
		public bool IsComplete(int k, int eos) => _ids.Length >= k || (0 < _ids.Length && eos == _ids[_ids.Length - 1]);
		public LookaheadString Truncate(int k)
		{
			if (k >= _ids.Length) return this;
			var a = new int[k];
			Array.Copy(_ids, a, k);
			return new LookaheadString(a);
		}
		public LookaheadString Concat(LookaheadString rhs, int k, int eos)
		{
			if (IsComplete(k, eos)) return Truncate(k);
			if (0 == rhs._ids.Length) return this;
			var n = Math.Min(k, _ids.Length + rhs._ids.Length);
			var a = new int[n];
			Array.Copy(_ids, a, _ids.Length);
			Array.Copy(rhs._ids, 0, a, _ids.Length, n - _ids.Length);
			return new LookaheadString(a);
		}
		public bool StartsWith(LookaheadString prefix)
		{
			if (prefix._ids.Length > _ids.Length) return false;
			for (var i = 0; i < prefix._ids.Length; ++i)
				if (_ids[i] != prefix._ids[i]) return false;
			return true;
		}
		public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_ids).GetEnumerator();
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _ids.GetEnumerator();
		public bool Equals(LookaheadString? rhs)
		{
			if (ReferenceEquals(this, rhs)) return true;
			if (null == rhs || rhs._hash != _hash || rhs._ids.Length != _ids.Length) return false;
			for (var i = 0; i < _ids.Length; ++i)
				if (_ids[i] != rhs._ids[i]) return false;
			return true;
		}
		public override bool Equals(object? obj) => Equals(obj as LookaheadString);
		public override int GetHashCode() => _hash;
		public string ToString(SymbolTable symbols) => 0 == _ids.Length ? "ε" : string.Join(" ", _ids.Select(symbols.Display));
		public override string ToString() => 0 == _ids.Length ? "ε" : string.Join(" ", _ids);
	}

	/// <summary>
	/// Set operations on lookahead strings under a fixed k
	/// </summary>
	internal readonly struct LookaheadAlgebra
	{
		public readonly int K;
		public readonly int Eos;
		public LookaheadAlgebra(int k, int eos) { K = k; Eos = eos; }

		public static HashSet<LookaheadString> EmptyOnly() => new HashSet<LookaheadString> { LookaheadString.Empty };
		public HashSet<LookaheadString> Concat(HashSet<LookaheadString> lhs, HashSet<LookaheadString> rhs)
		{
			var result = new HashSet<LookaheadString>();
			foreach (var x in lhs)
			{
				if (x.IsComplete(K, Eos))
					result.Add(x.Truncate(K));
				else
					foreach (var y in rhs)
						result.Add(x.Concat(y, K, Eos));
			}
			return result;
		}
		public bool AllComplete(HashSet<LookaheadString> set)
		{
			foreach (var x in set)
				if (!x.IsComplete(K, Eos)) return false;
			return true;
		}
	}
}
