using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsley
{
	public enum ErrorLevel
	{
		Message = 0,
		Warning = 1,
		Error = 2
	}

	/// <summary>
	/// Something in a grammar that has a source location
	/// </summary>
	public abstract class GrammarNode
	{
		public int Line { get; set; }
		public int Column { get; set; }
		public long Position { get; set; } = -1;
		public string? Filename { get; set; }

		public void SetLocation(int line, int column, long position, string? filename)
		{
			Line = line;
			Column = column;
			Position = position;
			Filename = filename;
		}
		internal void CopyLocation(GrammarNode other)
		{
			Line = other.Line;
			Column = other.Column;
			Position = other.Position;
			Filename = other.Filename;
		}
	}

	/// <summary>
	/// A diagnostic about a grammar, with the location it applies to
	/// </summary>
	public sealed class GrammarMessage
	{
		public GrammarMessage(ErrorLevel level, string message, GrammarNode? at = null)
			: this(level, message, at?.Line ?? 0, at?.Column ?? 0, at?.Position ?? -1, at?.Filename) { }
		public GrammarMessage(ErrorLevel level, string message, int line, int column, long position, string? filename)
		{
			Level = level;
			Message = message;
			Line = line;
			Column = column;
			Position = position;
			Filename = filename;
		}
		public ErrorLevel Level { get; }
		public string Message { get; }
		public int Line { get; }
		public int Column { get; }
		public long Position { get; }
		public string? Filename { get; }

		public override string ToString()
		{
			if (0 >= Line)
				return string.IsNullOrEmpty(Filename) ? $"{Level}: {Message}" : $"{Level}: {Message} in {Filename}";
			return $"{Level}: {Message} at line {Line}, column {Column} in {(string.IsNullOrEmpty(Filename) ? "in-memory grammar" : Filename)}";
		}
	}

	public sealed class GrammarException : Exception
	{
		public GrammarException(IEnumerable<GrammarMessage> messages) : base(_Summary(messages))
		{
			Messages = messages.ToList();
		}
		public GrammarException(string message, GrammarNode? at = null)
			: this(new[] { new GrammarMessage(ErrorLevel.Error, message, at) }) { }
		public IList<GrammarMessage> Messages { get; }

		static string _Summary(IEnumerable<GrammarMessage> messages)
		{
			var errors = messages.Where(m => m.Level == ErrorLevel.Error).ToList();
			if (0 == errors.Count)
				return messages.FirstOrDefault()?.ToString() ?? "";
			if (1 == errors.Count)
				return errors[0].ToString();
			return $"{errors[0]} (and {errors.Count - 1} more errors)";
		}
		public static void ThrowIfErrors(IEnumerable<GrammarMessage> messages)
		{
			if (messages.Any(m => m.Level == ErrorLevel.Error))
				throw new GrammarException(messages);
		}
	}
}
