using Expr;
namespace ExprTest;

internal class Program
{
    static void Main(string[] args)
    {
        var lexer = ExprLexer.Tokenize("1+(2^4)*3");
        Console.WriteLine(ExprParser.Parse(lexer));
    }
}
