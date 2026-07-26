using System.Text.Json;
using Ashes.Frontend;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: lexdump <file.ash>");
    return 1;
}

string source;
try
{
    source = File.ReadAllText(args[0]);
}
catch (Exception ex)
{
    Console.WriteLine("ERROR:" + ex.Message);
    return 0;
}

var diagnostics = new Diagnostics();
var lexer = new Lexer(source, diagnostics);
var tokens = new List<object>();

while (true)
{
    var token = lexer.Next();
    tokens.Add(new
    {
        kind = token.Kind.ToString(),
        text = token.Text,
        intValue = token.IntValue,
        floatValue = token.FloatValue,
        position = token.Position,
        length = token.Length,
    });

    if (token.Kind == TokenKind.EOF)
    {
        break;
    }
}

Console.WriteLine(JsonSerializer.Serialize(tokens));
return 0;
