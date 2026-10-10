using Json;

using Parsley.Runtime;
namespace JsonTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var lexer =  JsonLexer.Tokenize("{\"name\": \"John\", \"age\": 30, \"isStudent\": false, \"emails\": [ \"john@mydomain.com\", \"support@widgetco.com\" ] }");
            try
            {
                Console.WriteLine(JsonParser.Parse(lexer));
            }
            catch (ParseException e)
            {
                foreach(var error in e.Errors) { Console.WriteLine($"ERROR: {error}"); }
            }
        }
    }
}
