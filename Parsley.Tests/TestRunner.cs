using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Parsley.Tests
{
	static class T
	{
		public static int Fails;
		public static int Passes;
		public static void Check(bool cond, string what)
		{
			if (cond) ++Passes;
			else
			{
				++Fails;
				Console.WriteLine("  FAIL " + what);
			}
		}
		public static void Equal<TV>(TV expected, TV actual, string what)
		{
			var ok = EqualityComparer<TV>.Default.Equals(expected, actual);
			if (ok) ++Passes;
			else
			{
				++Fails;
				Console.WriteLine($"  FAIL {what}\n       expected: {expected}\n       actual:   {actual}");
			}
		}
		public static void Throws<TE>(Action a, string what) where TE : Exception
		{
			try { a(); }
			catch (TE) { ++Passes; return; }
			catch (Exception ex) { ++Fails; Console.WriteLine($"  FAIL {what}: threw {ex.GetType().Name}: {ex.Message}"); return; }
			++Fails;
			Console.WriteLine($"  FAIL {what}: did not throw");
		}
		// the test grammars are copied next to the test assembly
		public static string Dir => AppContext.BaseDirectory;
	}

	static class TestRunner
	{
		static int Main(string[] args)
		{
			var filter = 0 < args.Length ? args[0] : null;
			var types = typeof(TestRunner).Assembly.GetTypes().Where(t => t.Name.EndsWith("Tests") && t.IsClass).OrderBy(t => t.Name);
			foreach (var type in types)
			{
				foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => 0 == m.GetParameters().Length && !m.IsSpecialName))
				{
					var name = $"{type.Name}.{m.Name}";
					if (null != filter && !name.Contains(filter)) continue;
					var before = T.Fails;
					try
					{
						m.Invoke(null, null);
					}
					catch (TargetInvocationException ex)
					{
						++T.Fails;
						Console.WriteLine($"  FAIL {name} threw {ex.InnerException}");
					}
					Console.WriteLine((before == T.Fails ? "ok   " : "FAIL ") + name);
				}
			}
			Console.WriteLine($"{T.Passes} checks passed, {T.Fails} failed");
			return 0 == T.Fails ? 0 : 1;
		}
	}
}
