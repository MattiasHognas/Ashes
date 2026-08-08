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
            GenerationBudgetValidator.Validate(generated.Program, generated.Trace, generated.Source.Length, generated.Budget).ShouldBeEmpty();
            new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
        }
    }

    [Test]
    public void BudgetValidatorRejectsEveryBoundedDimension()
    {
        TypeDecl type = new("BudgetType", [], [new TypeConstructor("BudgetValue", [])]);
        Expr recursive = new Expr.LetRecursive(
            "loop",
            new Expr.Lambda(
                "value",
                new Expr.Match(
                    new Expr.ListLit([new Expr.IntLit(1), new Expr.IntLit(2)]),
                    [
                        new MatchCase(new Pattern.EmptyList(), new Expr.IntLit(0)),
                        new MatchCase(new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Wildcard()), new Expr.IntLit(1)),
                    ])),
            new Expr.IntLit(0));
        Ashes.Frontend.Program program = new([new TopLevelItem.Type(type)], recursive);
        GenerationBudget budget = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        GenerationTrace trace = new(["combination:test"]);

        IReadOnlyList<string> errors = GenerationBudgetValidator.Validate(program, trace, 1, budget);

        foreach (string dimension in new[]
        {
            "nodes", "depth", "declarations", "functions", "ADTs", "match cases",
            "collection length", "recursion complexity", "combinations", "source length",
        })
        {
            errors.ShouldContain(error => error.Contains(dimension, StringComparison.Ordinal));
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

    [Test]
    public void CompleteProgramsExerciseVariedTopLevelDeclarations()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase adtCase = fixture.Generator.Generate(84, 0, fixture.Profiles.Get("semantics"), 80);
        GeneratedFuzzCase recordCase = fixture.Generator.Generate(84, 1, fixture.Profiles.Get("semantics"), 80);
        GeneratedFuzzCase recursiveCase = fixture.Generator.Generate(84, 2, fixture.Profiles.Get("semantics"), 80);

        adtCase.Program.Items.OfType<TopLevelItem.Type>()
            .ShouldContain(item => item.Decl.Name == "FuzzChoice0" && item.Decl.TypeParameters.Count == 2);
        recordCase.Program.Items.OfType<TopLevelItem.Type>()
            .ShouldContain(item => item.Decl.Name == "FuzzRecordShape1" && item.Decl.IsRecord);
        recursiveCase.Program.Items.OfType<TopLevelItem.RecursiveGroup>().Single().Bindings.Count.ShouldBe(2);

        adtCase.Features.Contains(GeneratedFeature.TopLevelFunction).ShouldBeTrue();
        recursiveCase.Features.Contains(GeneratedFeature.MutualRecursion).ShouldBeTrue();
        new AstInvariantValidator().ValidateScope(adtCase.Program).ShouldBeEmpty();
        new AstInvariantValidator().ValidateScope(recordCase.Program).ShouldBeEmpty();
        new AstInvariantValidator().ValidateScope(recursiveCase.Program).ShouldBeEmpty();
    }

    [Test]
    public void CompleteProgramsExerciseDeterministicCapabilityProviders()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(84, 7, fixture.Profiles.Get("semantics"), 80);

        generated.Program.Items.OfType<TopLevelItem.Capability>()
            .ShouldContain(item => item.Decl.Name == "FuzzProvided7");
        generated.Program.Items.OfType<TopLevelItem.Provide>()
            .ShouldContain(item => item.Decl.CapabilityName == "FuzzProvided7");
        generated.Features.Contains(GeneratedFeature.Provider).ShouldBeTrue();
        new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
    }
}
