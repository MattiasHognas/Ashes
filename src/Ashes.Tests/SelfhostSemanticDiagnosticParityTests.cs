using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostSemanticDiagnosticParityTests
{
    [Test]
    [Arguments("call_argument_mismatch")]
    [Arguments("tail_self_call_argument_mismatch")]
    public async Task Stage_zero_lowering_matches_shared_diagnostic_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostSemanticDiagnosticParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".diagnostics")).ConfigureAwait(false);

        var diagnostics = new Diagnostics();
        var parser = new Parser(source, diagnostics);
        Program program = parser.ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();

        var lowering = new Lowering(diagnostics);
        lowering.SetSourceContext(fixtureName + ".ash", source);
        lowering.Lower(program);

        string actual = DiagnosticSerialization.Serialize(program.Items.Count, diagnostics.StructuredErrors);

        if (UpdateFixtures)
        {
            await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".diagnostics"), actual).ConfigureAwait(false);

            string repoFixtureDirectory = RepoParityDirectory();
            if (Directory.Exists(repoFixtureDirectory))
            {
                await File.WriteAllTextAsync(Path.Combine(repoFixtureDirectory, fixtureName + ".diagnostics"), actual).ConfigureAwait(false);
            }

            expected = actual;
        }

        actual.ShouldBe(expected);
    }

    private static bool UpdateFixtures
        => string.Equals(Environment.GetEnvironmentVariable("ASHES_UPDATE_PARITY_FIXTURES"), "1", StringComparison.Ordinal);

    private static string RepoParityDirectory()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "selfhost", "parity", "semantics", "diagnostics"));
}
