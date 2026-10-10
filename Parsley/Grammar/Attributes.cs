using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsley
{
	/// <summary>
	/// A name with an optional value, written <c>&lt;name&gt;</c> or <c>&lt;name=value&gt;</c>.
	/// A bare name has the value <c>true</c>.
	/// </summary>
	public sealed class GrammarAttribute : GrammarNode
	{
		public GrammarAttribute(string name, object? value = null)
		{
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Value = value ?? true;
		}
		public string Name { get; }
		/// <summary>
		/// bool, string, double or long
		/// </summary>
		public object Value { get; set; }

		public GrammarAttribute Clone()
		{
			var result = new GrammarAttribute(Name, Value);
			result.CopyLocation(this);
			return result;
		}
		public override string ToString()
		{
			if (Value is bool b && b)
				return Name;
			return string.Concat(Name, "=", FormatValue(Value));
		}
		internal static string FormatValue(object value)
		{
			switch (value)
			{
				case bool b:
					return b ? "true" : "false";
				case string s:
					var sb = new StringBuilder();
					sb.Append('"');
					foreach (var ch in s)
					{
						switch (ch)
						{
							case '"': sb.Append("\\\""); break;
							case '\\': sb.Append("\\\\"); break;
							case '\n': sb.Append("\\n"); break;
							case '\r': sb.Append("\\r"); break;
							case '\t': sb.Append("\\t"); break;
							default: sb.Append(ch); break;
						}
					}
					sb.Append('"');
					return sb.ToString();
				case IFormattable f:
					return f.ToString(null, CultureInfo.InvariantCulture);
				default:
					return value?.ToString() ?? "null";
			}
		}
	}

	/// <summary>
	/// An ordered list of attributes, looked up by name
	/// </summary>
	public sealed class AttributeList : List<GrammarAttribute>
	{
		public int IndexOf(string name)
		{
			for (var i = 0; i < Count; ++i)
				if (this[i].Name == name)
					return i;
			return -1;
		}
		public bool Contains(string name) => -1 < IndexOf(name);
		public object? Get(string name)
		{
			var i = IndexOf(name);
			return -1 < i ? this[i].Value : null;
		}
		public GrammarAttribute? Find(string name)
		{
			var i = IndexOf(name);
			return -1 < i ? this[i] : null;
		}
		public bool TryGetValue(string name, out object? value)
		{
			var attr = Find(name);
			if (attr == null) { value = null; return false; }
			value = attr.Value;
			return true;
		}
		/// <summary>
		/// True when the attribute is present as a bare flag or set to true
		/// </summary>
		public bool IsSet(string name) => Get(name) is bool b && b;
		public string? GetString(string name) => Get(name) as string;
		public void Set(string name, object? value = null)
		{
			var i = IndexOf(name);
			if (-1 < i)
				this[i].Value = value ?? true;
			else
				Add(new GrammarAttribute(name, value));
		}
		public bool Remove(string name)
		{
			var i = IndexOf(name);
			if (0 > i) return false;
			RemoveAt(i);
			return true;
		}
		public AttributeList Clone()
		{
			var result = new AttributeList();
			foreach (var a in this)
				result.Add(a.Clone());
			return result;
		}
		public override string ToString() => 0 == Count ? "" : string.Concat("<", string.Join(", ", this), ">");
	}
}
