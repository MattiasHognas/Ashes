using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostTokenParityTests
{
    [Test]
    [Arguments("keywords")]
    [Arguments("literals")]
    [Arguments("operators-unicode")]
    public async Task Stage_zero_lexer_matches_shared_token_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostTokenParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".tokens")).ConfigureAwait(false);
        var diagnostics = new Diagnostics();
        var lexer = new Lexer(source, diagnostics);
        var tokens = new List<Token>();

        while (true)
        {
            Token token = lexer.Next();
            tokens.Add(token);
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }
        }

        diagnostics.StructuredErrors.ShouldBeEmpty();
        TokenSerialization.Serialize(tokens).ShouldBe(expected);
    }
}
