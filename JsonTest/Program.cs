using Json;
namespace JsonTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var lexer =  JsonLexer.Tokenize("{\"name\": \"John\", \"age\": 30, \"isStudent\": false}");
            var parser = new JsonParser(lexer);
            Console.WriteLine(parser.Parse().ToString());
        }
    }
}
