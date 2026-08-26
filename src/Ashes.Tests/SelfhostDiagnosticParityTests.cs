using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostDiagnosticParityTests
{
    [Test]
    [Arguments("lexer_unterminated_string")]
    [Arguments("lexer_invalid_float")]
    [Arguments("lexer_unsigned_out_of_range")]
    [Arguments("lexer_unexpected_character")]
    [Arguments("parser_expected_expression_recovery")]
    [Arguments("parser_expected_pattern")]
    [Arguments("parser_unexpected_token_after_program")]
    [Arguments("parser_refutable_pattern_in_let")]
    [Arguments("parser_and_without_let_recursive")]
    [Arguments("parser_type_needs_constructor")]
    public async Task Stage_zero_frontend_matches_shared_diagnostic_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostDiagnosticParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".diagnostics")).ConfigureAwait(false);
        var diagnostics = new Diagnostics();
        var parser = new Parser(source, diagnostics);
        Program program = parser.ParseProgram();

        DiagnosticSerialization.Serialize(program.Items.Count, diagnostics.StructuredErrors).ShouldBe(expected);
    }
}
