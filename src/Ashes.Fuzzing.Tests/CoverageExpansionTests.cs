using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Execution;
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
    public void ResultErrorMappingRuleProducesTypedSemanticProgramsAcrossTypes()
    {
        var fixture = TestFixture.Create();
        AshesType.Result[] resultTypes =
        [
            new(AshesType.Str, AshesType.Int),
            new(AshesType.Bool, AshesType.Str),
            new(AshesType.Int, new AshesType.Tuple([AshesType.Bool, AshesType.Str])),
        ];

        for (int caseIndex = 0; caseIndex < resultTypes.Length; caseIndex++)
        {
            AshesType.Result resultType = resultTypes[caseIndex];
            FuzzProfile profile = new(
                "test-result-map-error",
                new HashSet<string>(StringComparer.Ordinal) { "result-map-error" },
                new HashSet<string>(StringComparer.Ordinal),
                ["parse", "format", "semantic"],
                [resultType],
                0);
            GeneratedFuzzCase? generated = null;
            for (int attempt = 0; attempt < 50 && generated is null; attempt++)
            {
                GeneratedFuzzCase candidate = fixture.Generator.Generate(6060 + (ulong)caseIndex, attempt, profile, 80);
                if (candidate.Trace.Entries.Contains("rule:result-map-error", StringComparer.Ordinal))
                {
                    generated = candidate;
                }
            }

            generated.ShouldNotBeNull($"Result error mapping was not selected for '{resultType}'.");
            generated.Type.ShouldBe(resultType);
            generated.Features.Contains(GeneratedFeature.ResultErrorMapping).ShouldBeTrue();
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);
            diagnostics.Errors.ShouldBeEmpty(generated.Source);
        }
    }

    [Test]
    public void ResultBindingRuleProducesTypedSemanticPrograms()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = new(
            "test-result-bind",
            new HashSet<string>(StringComparer.Ordinal) { "result-bind" },
            new HashSet<string>(StringComparer.Ordinal),
            ["parse", "format", "semantic"],
            [new AshesType.Result(AshesType.Str, AshesType.Int)],
            0);

        GeneratedFuzzCase generated = fixture.Generator.Generate(7070, 0, profile, 80);

        generated.Trace.Entries.ShouldContain("rule:result-bind");
        generated.Features.Contains(GeneratedFeature.ResultBinding).ShouldBeTrue();
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
        _ = new Lowering(diagnostics).Lower(parsed);
        diagnostics.Errors.ShouldBeEmpty(generated.Source);
    }

    [Test]
    public void EveryCombinationTemplateIsGeneratedAndSemanticallyValid()
    {
        var fixture = TestFixture.Create();
        AshesType[] candidateTypes = StableTypes();
        int templateIndex = 0;
        foreach (ICombinationTemplate template in fixture.Combinations.Templates.OrderBy(template => template.Id, StringComparer.Ordinal))
        {
            GenerationContext templateContext = TestFixture.ContextFor(template);
            AshesType compatibleType = candidateTypes.First(type => template.CanApply(type, templateContext, GenerationBudget.Create(120)));
            FuzzProfile profile = new(
                "test-" + template.Id,
                fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal) { template.Id },
                ["parse", "semantic"],
                [compatibleType],
                0,
                ContextFlags: GenerationFlags.RecursionAllowed | GenerationFlags.SuspensionAllowed | GenerationFlags.ResourcesAllowed,
                ResourceTypes: GenerationContext.Empty.ResourceTypes,
                GenerateTraits: template.Id.StartsWith("trait.", StringComparison.Ordinal));
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
            FuzzSemanticCompilation compilation = FuzzSemanticCompiler.Lower(exercised.Source);
            compilation.Diagnostics.Errors.ShouldBeEmpty($"Template '{template.Id}':\n{exercised.Source}");
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
    public void NativeProfilesOnlyScheduleCombinationsWithObservableResultTypes()
    {
        var fixture = TestFixture.Create();
        foreach (string profileId in new[] { "compile", "differential", "memory-growth", "cross-target" })
        {
            FuzzProfile profile = fixture.Profiles.Get(profileId);
            string[] enabled = profile.EnabledCombinations.Order(StringComparer.Ordinal).ToArray();
            for (int caseIndex = 0; caseIndex < enabled.Length; caseIndex++)
            {
                ICombinationTemplate template = fixture.Combinations.Get(enabled[caseIndex]);
                profile.Types.Any(type => template.CanApply(
                    type,
                    GenerationContext.Empty,
                    GenerationBudget.Create(120))).ShouldBeTrue();

                GeneratedFuzzCase generated = fixture.Generator.Generate(910000, caseIndex, profile, 120);
                generated.Trace.Entries.ShouldContain("combination:" + enabled[caseIndex]);
            }
        }
    }

    [Test]
    public void AllProfileCoversEveryStableProfileWithBoundedNativeWork()
    {
        var fixture = TestFixture.Create();
        FuzzProfile all = fixture.Profiles.Get("all");
        FuzzCampaign campaign = new(fixture.Generator, fixture.Profiles, FuzzOracleRegistry.CreateDefault());
        FuzzProfile[] cycle = Enumerable.Range(0, 50)
            .Select(caseIndex => campaign.ResolveProfile(all, caseIndex))
            .ToArray();
        string[] expected =
        [
            "async",
            "capabilities",
            "combinations",
            "compile",
            "cross-target",
            "differential",
            "invalid-semantics",
            "invalid-source",
            "memory-growth",
            "perceus",
            "resources",
            "semantics",
            "syntax",
            "traits",
            "traits-differential",
        ];

        cycle.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ShouldBe(expected);
        cycle.Count(profile => profile.Native).ShouldBe(5);
        all.EnabledCombinations.ShouldContain("resource.deterministic-file-handle");
        all.EffectiveResourceTypes.ShouldBe([AshesType.FileHandle]);
        campaign.ResolveProfile(all, 50).Id.ShouldBe(campaign.ResolveProfile(all, 0).Id);
    }

    [Test]
    public void MemoryGrowthProfileExcludesConcurrentSuspensionShapes()
    {
        var fixture = TestFixture.Create();
        FuzzProfile memoryGrowth = fixture.Profiles.Get("memory-growth");

        memoryGrowth.ContextFlags.ShouldBe(GenerationFlags.RecursionAllowed);
        memoryGrowth.EnabledCombinations.ShouldAllBe(id =>
            !id.StartsWith("async.", StringComparison.Ordinal));
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
    public void IrVerifierRejectsUseAfterOwnershipConsumption()
    {
        IrFunction entry = new(
            "_start_main",
            [
                new IrInst.LoadConstStr(0, "owned"),
                new IrInst.RcDrop(0, "Str", RuntimeManaged: true),
                new IrInst.LoadConstStr(1, "suffix"),
                new IrInst.ConcatStr(2, 0, 1),
                new IrInst.Return(2),
            ],
            LocalCount: 0,
            TempCount: 3,
            HasEnvAndArgParams: false);
        IrProgram program = new(entry, [], [], false, false, false, false, false, false);

        IReadOnlyList<string> errors = new IrInvariantVerifier().Verify(program);

        errors.ShouldContain(error => error.Contains("uses consumed temp %0", StringComparison.Ordinal));
    }

    [Test]
    public void IrVerifierRejectsInvalidInstructionContracts()
    {
        IrFunction entry = new(
            "_start_main",
            [
                new IrInst.LoadConstStr(0, "missing-string"),
                new IrInst.LoadLocal(1, 2),
                new IrInst.CallExternal(
                    2,
                    "missing_external",
                    null,
                    [0],
                    [new FfiType.Str(), new FfiType.Int()],
                    new FfiType.Int()),
                new IrInst.AllocAdt(3, 0, -1),
                new IrInst.SetAdtField(3, -1, 0),
                new IrInst.Return(2),
            ],
            LocalCount: 1,
            TempCount: 4,
            HasEnvAndArgParams: false);
        IrProgram program = new(
            entry,
            [],
            [new IrStringLiteral("duplicate", "one"), new IrStringLiteral("duplicate", "two")],
            false,
            false,
            false,
            false,
            false,
            false);

        IReadOnlyList<string> errors = new IrInvariantVerifier().Verify(program);

        errors.ShouldContain(error => error.Contains("string literal label", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("missing string literal", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("invalid Slot local slot", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("arguments but 2 parameter types", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("undeclared external symbol", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("negative field count", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("negative ADT field index", StringComparison.Ordinal));
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
            coverage.Record(generated);
            coverage.RecordOracleExecution("parse");
            coverage.RecordOracleExecution("semantic");
            coverage.RecordOracleExecution("ir");
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
    public void CoverageCountsEveryOracleExecutionIndependentlyOfCaseCompletion()
    {
        FuzzCoverage coverage = new();

        coverage.RecordOracleExecution("parse");
        coverage.RecordOracleExecution("parse");
        coverage.RecordOracleExecution("semantic");

        coverage.OracleExecutionCount("parse").ShouldBe(2);
        coverage.OracleExecutionCount("semantic").ShouldBe(1);
        coverage.Summary().ShouldContain("oracles=2, oracle-runs=3");
        Should.Throw<ArgumentException>(() => coverage.RecordOracleExecution(""));
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
        const string source = "let value = [1, 2, 3] in value";
        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (InvalidSourceMutation mutation in Enum.GetValues<InvalidSourceMutation>())
        {
            string first = mutator.Mutate(source, 2718, mutation);
            first.ShouldBe(mutator.Mutate(source, 2718, mutation));
            results.Add(first);
        }
        results.Count.ShouldBe(InvalidSourceMutator.MutationCount);
    }

    [Test]
    public async Task InvalidSourceSeedsRotateBetweenGeneratedAndCheckedInFamilies()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(51, 0, fixture.Profiles.Get("invalid-source"), 80);
        string root = Directory.CreateTempSubdirectory("ashes-fuzz-seeds-").FullName;
        try
        {
            string corpus = Path.Combine(root, "tests", "fuzz", "corpus");
            Directory.CreateDirectory(corpus);
            string checkedIn = Path.Combine(corpus, "regression.ash");
            await File.WriteAllTextAsync(checkedIn, "42");
            string traitSeed = Path.Combine(root, "tests", "trait_generated.ash");
            await File.WriteAllTextAsync(traitSeed, "trait Render(a) = | render : a -> Str");

            GeneratedFuzzCase generatedSeed = InvalidSourceSeedSelector.Select(generated, root, 0);
            GeneratedFuzzCase corpusSeed = InvalidSourceSeedSelector.Select(generated, root, 1);
            GeneratedFuzzCase traitSeedCase = InvalidSourceSeedSelector.Select(generated, root, 2);

            generatedSeed.Source.ShouldBe(generated.Source);
            generatedSeed.Trace.Entries.ShouldContain("invalid-seed:generated");
            corpusSeed.Source.ShouldBe("42");
            corpusSeed.Trace.Entries.ShouldContain(entry => entry.StartsWith("invalid-seed:corpus:", StringComparison.Ordinal));
            traitSeedCase.Source.ShouldStartWith("trait Render");
            traitSeedCase.Trace.Entries.ShouldContain(entry =>
                entry.StartsWith("invalid-seed:trait-tests:", StringComparison.Ordinal));
            InvalidSourceSeedSelector.Select(generated, root, 1).Source.ShouldBe(corpusSeed.Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        new AshesType.Adt("FuzzMaybe", [AshesType.Str]),
        new AshesType.Task(AshesType.Str, AshesType.Int),
    ];
}
