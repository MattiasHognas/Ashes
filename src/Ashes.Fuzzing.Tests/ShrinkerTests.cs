using Ashes.Frontend;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Shrinking;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class ShrinkerTests
{
    [Test]
    public async Task ShrinkingPreservesSimulatedFailureAndReducesMetric()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(700, 2, fixture.Profiles.Get("combinations"), 80);
        ShrinkResult result = await new FuzzShrinker().ShrinkAsync(original, (_, _) => ValueTask.FromResult(true), 50, TimeSpan.FromSeconds(2), CancellationToken.None);
        result.Attempts.ShouldBeLessThanOrEqualTo(50);
        result.Accepted.ShouldBeGreaterThan(0);
        StableSizeMetric.Measure(result.Case).ShouldBeLessThan(StableSizeMetric.Measure(original));
    }

    [Test]
    public void EveryCandidateHasStrictlySmallerStableMetric()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(91, 5, fixture.Profiles.Get("combinations"), 80);
        foreach (GeneratedFuzzCase candidate in new FuzzShrinker().Candidates(original)) StableSizeMetric.Measure(candidate).ShouldBeLessThan(StableSizeMetric.Measure(original));
    }

    [Test]
    public void WholeProgramCandidatesRemoveUnusedDeclarationsAndKeepAccurateMetrics()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(91, 1, fixture.Profiles.Get("semantics"), 80);

        GeneratedFuzzCase[] candidates = new FuzzShrinker().Candidates(original).ToArray();

        candidates.ShouldContain(candidate => candidate.Program.Items.Count < original.Program.Items.Count);
        foreach (GeneratedFuzzCase candidate in candidates)
        {
            candidate.NodeCount.ShouldBe(AstCoverageMetrics.Measure(candidate.Program).Nodes);
        }
    }

    [Test]
    public void GeneratedShrinkCandidatesRemainParseableAndTypeCorrect()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase original = fixture.Generator.Generate(700, 2, fixture.Profiles.Get("combinations"), 80);

        foreach (GeneratedFuzzCase candidate in new FuzzShrinker().Candidates(original).Take(100))
        {
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(candidate.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty(candidate.Source);
        }
    }

    [Test]
    public void ShrinkingRemovesUnusedAdtConstructorsAndRedundantMatchCases()
    {
        GeneratedFuzzCase original = Case(
            """
            type Choice =
                | Keep(Int)
                | Unused(Str)

            let choice : Choice = Keep(3)
            match true with
                | true -> match choice with
                    | Keep(value) -> value
                    | _ -> 0
                | _ -> match choice with
                    | Keep(value) -> value
                    | _ -> 0
            """);

        GeneratedFuzzCase[] candidates = new FuzzShrinker().Candidates(original).ToArray();

        candidates.ShouldContain(candidate => candidate.Program.Items.OfType<TopLevelItem.Type>()
            .Any(type => type.Decl.Constructors.Count == 1 && type.Decl.Constructors[0].Name == "Keep"));
        candidates.Any(candidate => candidate.Program.Body is Expr.Match { Cases.Count: 1 }).ShouldBeTrue();
        foreach (GeneratedFuzzCase candidate in candidates)
        {
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(candidate.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty(candidate.Source);
        }
    }

    [Test]
    public void ShrinkingReducesConsInputsAndSafeArithmeticChildren()
    {
        GeneratedFuzzCase list = Case("1 :: 2 :: []", new AshesType.List(AshesType.Int));
        new FuzzShrinker().Candidates(list).ShouldContain(candidate => candidate.Source.Contains("2 :: []", StringComparison.Ordinal));

        GeneratedFuzzCase arithmetic = Case("100 + 20");
        new FuzzShrinker().Candidates(arithmetic).ShouldContain(candidate => candidate.Source.Contains("50 + 20", StringComparison.Ordinal));
    }

    [Test]
    public void MaybeLikeAdtsShrinkToTheirCompatibleEmptyConstructor()
    {
        GeneratedFuzzCase original = Case(
            """
            type FuzzMaybe(a) =
                | FuzzNone
                | FuzzSome(a)

            FuzzSome("payload")
            """,
            new AshesType.Adt("FuzzMaybe", [AshesType.Str]));

        GeneratedFuzzCase candidate = new FuzzShrinker().Candidates(original)
            .First(candidate => candidate.Program.Body is Expr.Var { Name: "FuzzNone" });
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(candidate.Source, diagnostics).ParseProgram();
        _ = new Lowering(diagnostics).Lower(parsed);

        diagnostics.Errors.ShouldBeEmpty(candidate.Source);
    }

    [Test]
    public void LetRemovalTracksReferencesInsideAggregateChildren()
    {
        GeneratedFuzzCase original = Case(
            """
            type FuzzRecord =
                | first: Int
                | second: Bool

            let captured = 7
            in FuzzRecord(first = captured, second = true)
            """,
            new AshesType.Record("FuzzRecord"));

        foreach (GeneratedFuzzCase candidate in new FuzzShrinker().Candidates(original))
        {
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(candidate.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty(candidate.Source);
        }
    }

    [Test]
    public void ResultBindingsAndPipelinesShrinkTheirTypedChildren()
    {
        AshesType.Result resultType = new(AshesType.Str, AshesType.Int);
        GeneratedFuzzCase binding = Case("let? value = Ok(128) in Ok(value + 64)", resultType);
        GeneratedFuzzCase pipe = Case(
            "Ok(128) |?> (given (value: Int) -> Ok(value + 64))",
            resultType);
        GeneratedFuzzCase mapError = Case(
            "Error(128) |!> (given (error: Int) -> \"failure\")",
            resultType);

        GeneratedFuzzCase[] bindingCandidates = new FuzzShrinker().Candidates(binding).ToArray();
        GeneratedFuzzCase[] pipeCandidates = new FuzzShrinker().Candidates(pipe).ToArray();
        GeneratedFuzzCase[] mapCandidates = new FuzzShrinker().Candidates(mapError).ToArray();

        bindingCandidates.Any(candidate => candidate.Program.Body is Expr.LetResult
        {
            Value: Expr.Call { Func: Expr.Var { Name: "Ok" }, Arg: Expr.IntLit { Value: 0 } },
        }).ShouldBeTrue();
        pipeCandidates.Any(candidate => candidate.Program.Body is Expr.ResultPipe
        {
            Left: Expr.Call { Func: Expr.Var { Name: "Ok" }, Arg: Expr.IntLit { Value: 0 } },
        }).ShouldBeTrue();
        mapCandidates.Any(candidate => candidate.Program.Body is Expr.ResultMapErrorPipe
        {
            Right: Expr.Lambda { Body: Expr.StrLit { Value.Length: 3 } },
        }).ShouldBeTrue();

        foreach (GeneratedFuzzCase candidate in bindingCandidates.Concat(pipeCandidates).Concat(mapCandidates))
        {
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(candidate.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty(candidate.Source);
        }
    }

    private static GeneratedFuzzCase Case(string source, AshesType? type = null)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.Errors.ShouldBeEmpty();
        string formatted = Ashes.Formatter.Formatter.Format(program);
        return new GeneratedFuzzCase(
            1,
            1,
            0,
            "test",
            type ?? AshesType.Int,
            program,
            formatted,
            new GeneratedFeatureSet(),
            GenerationTrace.Empty,
            AstCoverageMetrics.Measure(program).Nodes,
            GenerationBudget.Create(120));
    }
}
