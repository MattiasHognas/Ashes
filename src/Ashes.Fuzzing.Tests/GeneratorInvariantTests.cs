using Ashes.Frontend;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class GeneratorInvariantTests
{
    [Test]
    public void IdenticalSeedsGenerateIdenticalProgramsAndTraces()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase first = fixture.Generator.Generate(12345, 17, fixture.Profiles.Get("semantics"), 60);
        GeneratedFuzzCase second = fixture.Generator.Generate(12345, 17, fixture.Profiles.Get("semantics"), 60);
        first.Source.ShouldBe(second.Source);
        first.Trace.Entries.ShouldBe(second.Trace.Entries);
        first.CaseSeed.ShouldBe(second.CaseSeed);
    }

    [Test]
    public void GeneratedProgramsRespectBudgetAndScope()
    {
        var fixture = TestFixture.Create();
        for (int index = 0; index < 100; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(77, index, fixture.Profiles.Get("smoke"), 40);
            generated.NodeCount.ShouldBeLessThanOrEqualTo(40);
            generated.NodeCount.ShouldBe(AstCoverageMetrics.Measure(generated.Program).Nodes);
            generated.Source.Length.ShouldBeLessThanOrEqualTo(generated.Budget.MaximumSourceLength);
            new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
        }
    }

    [Test]
    public void GeneratedValidProgramsFormatAndParse()
    {
        var fixture = TestFixture.Create();
        for (int index = 0; index < 50; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(991, index, fixture.Profiles.Get("syntax"), 50);
            Diagnostics diagnostics = new();
            _ = new Parser(generated.Source, diagnostics).ParseProgram();
            diagnostics.Errors.ShouldBeEmpty();
        }
    }

    [Test]
    public void DifferentCaseIndexesAreIndependentAndDeterministic()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase zero = fixture.Generator.Generate(4, 0, fixture.Profiles.Get("syntax"), 40);
        GeneratedFuzzCase one = fixture.Generator.Generate(4, 1, fixture.Profiles.Get("syntax"), 40);
        zero.CaseSeed.ShouldNotBe(one.CaseSeed);
        fixture.Generator.Generate(4, 1, fixture.Profiles.Get("syntax"), 40).Source.ShouldBe(one.Source);
    }
}
