using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class SelfhostIrParityTests
{
    [Test]
    [Arguments("simple_arith")]
    [Arguments("let_bindings")]
    [Arguments("closure_capture")]
    [Arguments("pattern_match")]
    [Arguments("mutual_recursion")]
    public async Task Stage_zero_lowering_matches_shared_lowered_ir_fixture(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfhostIrParity");
        string source = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".source")).ConfigureAwait(false);
        string expected = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fixtureName + ".ir")).ConfigureAwait(false);

        var diagnostics = new Diagnostics();
        var parser = new Parser(source, diagnostics);
        var program = parser.ParseProgram();
        diagnostics.StructuredErrors.ShouldBeEmpty();

        var lowering = new Lowering(diagnostics);
        lowering.SetSourceContext(fixtureName + ".ash", source);
        IrProgram ir = lowering.Lower(program);
        diagnostics.StructuredErrors.ShouldBeEmpty();

        IReadOnlyList<string> lines = IrTextFormatter.Format(ir, IrDumpStage.Lowered, filter: null);
        string actual = string.Join('\n', lines) + '\n';

        actual.ShouldBe(expected);
    }
}
