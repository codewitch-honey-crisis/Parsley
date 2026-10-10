using Parsley.Runtime;
using Expr;
namespace ExprTest;

internal class Program
{
    static void Main(string[] args)
    {
        var lexer = ExprLexer.Tokenize("1+(2^4)*3");
        try
        {
            Console.WriteLine(ExprParser.Parse(lexer));
        }
        catch (ParseException e)
        {
            foreach (var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
        }
    }
}
