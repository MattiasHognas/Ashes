using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostIrParityTests
{
    private static readonly ExplainKind[] SharedExplainKinds =
        [ExplainKind.Ownership, ExplainKind.Rc, ExplainKind.Reuse, ExplainKind.Memory];

    [Test]
    [Arguments("simple_arith")]
    [Arguments("let_bindings")]
    [Arguments("nested_let_scopes")]
    [Arguments("scalar_match")]
    [Arguments("ownerless_match")]
    [Arguments("closure_capture")]
    [Arguments("pattern_match")]
    [Arguments("mutual_recursion")]
    public async Task Stage_zero_lowering_matches_shared_lowered_ir_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostIrParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".ir")).ConfigureAwait(false);

        (_, IrProgram ir) = LowerFixture(fixtureName, source);

        IReadOnlyList<string> lines = IrTextFormatter.Format(ir, IrDumpStage.Lowered, filter: null);
        string actual = string.Join('\n', lines) + '\n';

        if (UpdateFixtures)
        {
            string outPath = Path.Combine(fixtureDirectory, fixtureName + ".ir");
            await File.WriteAllTextAsync(outPath, actual).ConfigureAwait(false);

            string repoFixtureDir = RepoParityDirectory("lowered-ir");
            if (Directory.Exists(repoFixtureDir))
            {
                await File.WriteAllTextAsync(Path.Combine(repoFixtureDir, fixtureName + ".ir"), actual).ConfigureAwait(false);
            }

            expected = actual;
        }

        actual.ShouldBe(expected);
    }

    // The explain fixtures are the same reporter and formatter the CLI's `--explain` prints, run on
    // the un-stitched lowering the lowered-ir fixtures come from, so the self-hosted explain parity
    // test compares against the report of the program it actually lowers rather than one with the
    // shipped standard library stitched in.
    [Test]
    [Arguments("simple_arith")]
    [Arguments("let_bindings")]
    [Arguments("nested_let_scopes")]
    [Arguments("scalar_match")]
    [Arguments("ownerless_match")]
    [Arguments("closure_capture")]
    [Arguments("pattern_match")]
    [Arguments("mutual_recursion")]
    public async Task Stage_zero_explain_reports_match_shared_explain_fixtures(string fixtureName)
    {
        string sourceDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostIrParity");
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostExplainParity");
        string source = await File.ReadAllTextAsync(Path.Combine(sourceDirectory, fixtureName + ".source")).ConfigureAwait(false);

        (Lowering lowering, IrProgram ir) = LowerFixture(fixtureName, source);
        IrProgram finalIr = IrOptimizer.Optimize(ir);
        CompilationDecisionSnapshot snapshot = lowering.GetDecisionSnapshot();

        foreach (ExplainKind kind in SharedExplainKinds)
        {
            var request = new ExplainRequest(new HashSet<ExplainKind> { kind });
            CompilationExplainReport report = IrExplainReporter.Build(snapshot, finalIr, request);
            string actual = string.Join('\n', ExplainReportFormatter.Format(report, request)) + '\n';

            string fixtureFile = $"{fixtureName}.{kind.ToString().ToLowerInvariant()}.txt";
            string expected;
            if (UpdateFixtures)
            {
                Directory.CreateDirectory(fixtureDirectory);
                await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, fixtureFile), actual).ConfigureAwait(false);

                string repoFixtureDir = RepoParityDirectory("explain");
                Directory.CreateDirectory(repoFixtureDir);
                await File.WriteAllTextAsync(Path.Combine(repoFixtureDir, fixtureFile), actual).ConfigureAwait(false);

                expected = actual;
            }
            else
            {
                expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureFile)).ConfigureAwait(false);
            }

            actual.ShouldBe(expected, fixtureFile);
        }
    }

    private static bool UpdateFixtures
        => string.Equals(Environment.GetEnvironmentVariable("ASHES_UPDATE_PARITY_FIXTURES"), "1", StringComparison.Ordinal);

    private static string RepoParityDirectory(string leaf)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "selfhost", "parity", "semantics", leaf));

    private static (Lowering Lowering, IrProgram Ir) LowerFixture(string fixtureName, string source)
    {
        var diagnostics = new Diagnostics();
        var parser = new Parser(source, diagnostics);
        var program = parser.ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();

        var lowering = new Lowering(diagnostics);
        lowering.SetSourceContext(fixtureName + ".ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.StructuredErrors.ShouldBeEmpty();
        return (lowering, ir);
    }
}
