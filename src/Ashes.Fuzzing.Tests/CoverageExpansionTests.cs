using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class CoverageExpansionTests
{
    [Test]
    public void GeneratedProgramsInferTheirRequestedTypesAcrossTheFullCatalog()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("semantics");
        for (int index = 0; index < 250; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(20260808, index, profile, 80);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(generated.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics);
            _ = lowering.Lower(program);

            diagnostics.Errors.ShouldBeEmpty($"case {index}:\n{generated.Source}");
            lowering.LastLoweredType.ShouldNotBeNull();
            lowering.FormatType(lowering.LastLoweredType).ShouldBe(ExpectedType(generated.Type));
        }
    }

    [Test]
    public void EveryCombinationTemplateIsGeneratedAndSemanticallyValid()
    {
        var fixture = TestFixture.Create();
        AshesType[] candidateTypes = StableTypes();
        int templateIndex = 0;
        foreach (ICombinationTemplate template in fixture.Combinations.Templates.OrderBy(template => template.Id, StringComparer.Ordinal))
        {
            AshesType compatibleType = candidateTypes.First(type => template.CanApply(type, GenerationContext.Empty, GenerationBudget.Create(120)));
            FuzzProfile profile = new(
                "test-" + template.Id,
                fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal) { template.Id },
                ["parse", "semantic"],
                [compatibleType],
                0);
            GeneratedFuzzCase? exercised = null;
            for (int caseIndex = 0; caseIndex < 50 && exercised is null; caseIndex++)
            {
                GeneratedFuzzCase candidate = fixture.Generator.Generate((ulong)(9000 + templateIndex), caseIndex, profile, 120);
                if (candidate.Trace.Entries.Contains("combination:" + template.Id, StringComparer.Ordinal))
                {
                    exercised = candidate;
                }
            }

            exercised.ShouldNotBeNull($"Template '{template.Id}' was not selected.");
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(exercised.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty($"Template '{template.Id}':\n{exercised.Source}");
            foreach (GeneratedFeature feature in template.AdvertisedFeatures)
            {
                exercised.Features.Contains(feature).ShouldBeTrue($"Template '{template.Id}' omitted '{feature}'.");
            }
            templateIndex++;
        }
    }

    [Test]
    public void PreferredCombinationRotationExercisesEveryStableTemplateInOneCycle()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = fixture.Profiles.Get("combinations");
        string[] enabled = profile.EnabledCombinations.Order(StringComparer.Ordinal).ToArray();

        for (int caseIndex = 0; caseIndex < enabled.Length; caseIndex++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(424242, caseIndex, profile, 120);

            generated.Trace.Entries.ShouldContain(
                "combination:" + enabled[caseIndex],
                $"case {caseIndex} did not exercise preferred template '{enabled[caseIndex]}'.");
        }
    }

    [Test]
    public void ReuseDisabledLoweringRemovesAllocReusingButKeepsProgramValid()
    {
        const string source = """
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """;
        IrProgram normal = Lower(source, LoweringConfiguration.Default);
        IrProgram disabled = Lower(source, new LoweringConfiguration(EnableReuse: false));

        AllInstructions(normal).Any(instruction => instruction is IrInst.AllocReusing).ShouldBeTrue();
        AllInstructions(disabled).Any(instruction => instruction is IrInst.AllocReusing).ShouldBeFalse();
        new IrInvariantVerifier().Verify(disabled).ShouldBeEmpty();
    }

    [Test]
    public void IrVerifierRejectsMissingLabelsInvalidTempsAndUnknownReuseTokens()
    {
        IrFunction entry = new(
            "_start_main",
            [new IrInst.LoadConstInt(0, 1), new IrInst.JumpIfFalse(4, "missing"), new IrInst.AllocReusing(0, 0, 1, 3), new IrInst.Return(0)],
            LocalCount: 0,
            TempCount: 1,
            HasEnvAndArgParams: false);
        IrProgram program = new(entry, [], [], false, false, false, false, false, false);

        IReadOnlyList<string> errors = new IrInvariantVerifier().Verify(program);

        errors.Count.ShouldBeGreaterThanOrEqualTo(3);
        errors.ShouldContain(error => error.Contains("invalid CondTemp", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("missing label", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("unknown reuse token", StringComparison.Ordinal));
    }

    [Test]
    public void IrVerifierRejectsInRangeTempsThatAreNotDefinedOnTheUsePath()
    {
        IrFunction entry = new(
            "_start_main",
            [new IrInst.LoadConstInt(0, 1), new IrInst.AddInt(1, 0, 2), new IrInst.Return(1)],
            LocalCount: 0,
            TempCount: 3,
            HasEnvAndArgParams: false);
        IrProgram program = new(entry, [], [], false, false, false, false, false, false);

        IReadOnlyList<string> errors = new IrInvariantVerifier().Verify(program);

        errors.ShouldContain(error => error.Contains("uses undefined temp %2", StringComparison.Ordinal));
    }

    [Test]
    public void CoverageReportsRulesCombinationsAndFeatureInteractions()
    {
        var fixture = TestFixture.Create();
        FuzzCoverage coverage = new(
            [.. fixture.Rules.Rules.Select(rule => rule.Id), "missing-rule"],
            [.. fixture.Combinations.Templates.Select(template => template.Id), "missing-template"]);
        for (int index = 0; index < 80; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(314159, index, fixture.Profiles.Get("combinations"), 80);
            coverage.Record(generated, ["parse", "semantic", "ir"]);
        }

        string summary = coverage.Summary();
        summary.ShouldContain("rules=");
        summary.ShouldContain("combinations=");
        summary.ShouldContain("pairs=");
        summary.ShouldContain("triples=");
        summary.ShouldContain("max-depth=");
        coverage.MaximumCombinationCount.ShouldBeGreaterThan(0);
        coverage.MaximumDepth.ShouldBeGreaterThan(0);
        coverage.CoveredRuleCount.ShouldBeLessThanOrEqualTo(fixture.Rules.Rules.Count);
        coverage.CoveredCombinationCount.ShouldBeLessThanOrEqualTo(fixture.Combinations.Templates.Count);
        coverage.MissingRules.ShouldContain("missing-rule");
        coverage.MissingCombinations.ShouldContain("missing-template");
    }

    [Test]
    public void AstCoverageMeasuresNodesAndNestingRatherThanTraceLength()
    {
        Expr expression = new Expr.Let(
            "value",
            new Expr.IntLit(1),
            new Expr.If(new Expr.BoolLit(true), new Expr.Var("value"), new Expr.IntLit(0)));
        Ashes.Frontend.Program program = new(Array.Empty<TopLevelItem>(), expression);

        AstCoverageMetrics metrics = AstCoverageMetrics.Measure(program);

        metrics.Nodes.ShouldBe(6);
        metrics.Depth.ShouldBe(3);
    }

    [Test]
    public void InvalidSourceMutationsCoverAllMutationFamiliesDeterministically()
    {
        InvalidSourceMutator mutator = new();
        HashSet<string> results = new(StringComparer.Ordinal);
        const string source = "let value = [1, 2, 3] in value";
        for (ulong seed = 0; seed < 128; seed++)
        {
            string first = mutator.Mutate(source, seed);
            first.ShouldBe(mutator.Mutate(source, seed));
            results.Add(first);
        }
        results.Count.ShouldBeGreaterThanOrEqualTo(7);
    }

    private static IrProgram Lower(string source, LoweringConfiguration configuration)
    {
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        IrProgram ir = new Lowering(diagnostics, configuration: configuration).Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        return ir;
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram program) =>
        program.Functions.Prepend(program.EntryFunction).SelectMany(function => function.Instructions);

    private static string ExpectedType(AshesType type) => type switch
    {
        AshesType.Primitive primitive => primitive.Name,
        AshesType.UInt unsigned => $"u{unsigned.Bits}",
        AshesType.List list => $"List<{ExpectedType(list.Element)}>",
        AshesType.Tuple tuple => $"({string.Join(", ", tuple.Elements.Select(ExpectedType))})",
        AshesType.Function function => $"{ExpectedType(function.Parameter)} -> {ExpectedType(function.Return)}",
        AshesType.Adt adt => $"{adt.Name}<{string.Join(", ", adt.Arguments.Select(ExpectedType))}>",
        AshesType.Record record => record.Name,
        AshesType.Result result => $"Result<{ExpectedType(result.Error)}, {ExpectedType(result.Value)}>",
        AshesType.Task task => $"Task<{ExpectedType(task.Error)}, {ExpectedType(task.Value)}>",
        _ => throw new InvalidOperationException($"No expected type renderer for '{type}'."),
    };

    private static AshesType[] StableTypes() =>
    [
        AshesType.Int,
        AshesType.Bool,
        AshesType.Str,
        AshesType.Float,
        AshesType.BigInt,
        new AshesType.UInt(8),
        new AshesType.List(AshesType.Int),
        new AshesType.List(AshesType.Str),
        new AshesType.Tuple([AshesType.Int, AshesType.Int]),
        new AshesType.Tuple([AshesType.Str, AshesType.Str]),
        new AshesType.Tuple([AshesType.Int, new AshesType.List(AshesType.Int)]),
        new AshesType.Tuple(
        [
            new AshesType.Function(AshesType.Int, AshesType.Str),
            new AshesType.Function(AshesType.Int, AshesType.Str),
        ]),
        new AshesType.Function(AshesType.Int, AshesType.Str),
        new AshesType.Record("FuzzRecord"),
        new AshesType.Result(AshesType.Str, AshesType.Int),
        new AshesType.Adt("FuzzTree", [AshesType.Int]),
        new AshesType.Task(AshesType.Str, AshesType.Int),
    ];
}
