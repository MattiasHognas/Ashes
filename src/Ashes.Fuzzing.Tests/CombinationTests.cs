using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class CombinationTests
{
    [Test]
    public void TemplatesRecordAdvertisedFeaturesAcrossTypes()
    {
        var fixture = TestFixture.Create();
        AshesType[] types = [AshesType.Int, AshesType.Str, new AshesType.List(AshesType.Bool), new AshesType.Tuple([AshesType.Str, AshesType.Bool])];
        foreach (ICombinationTemplate template in fixture.Combinations.Templates)
        {
            GenerationContext templateContext = TestFixture.ContextFor(template);
            foreach (AshesType type in types)
            {
                GenerationBudget budget = GenerationBudget.Create(80);
                if (!template.CanApply(type, templateContext, budget)) continue;
                string[] ruleIds = fixture.Rules.Rules.Select(rule => rule.Id).ToArray();
                GenerationCoverageGuidance coverage = new(ruleIds, []);
                ExpressionGenerator expressions = new(fixture.Rules, ruleIds.ToHashSet(StringComparer.Ordinal), coverage);
                GenerationResult<Ashes.Frontend.Expr> result = template.Generate(type, templateContext.WithTemplate(template.Id), budget, expressions, new FuzzRandom(42));
                foreach (GeneratedFeature feature in template.AdvertisedFeatures) result.Features.Contains(feature).ShouldBeTrue($"{template.Id} omitted {feature}");
            }
        }
    }

    [Test]
    public void CombinationProfileGeneratesNonIntegerCasesAndRespectsBudget()
    {
        var fixture = TestFixture.Create();
        bool sawNonInteger = false;
        for (int index = 0; index < 100; index++)
        {
            GeneratedFuzzCase generated = fixture.Generator.Generate(8181, index, fixture.Profiles.Get("combinations"), 60);
            sawNonInteger |= generated.Type != AshesType.Int;
            generated.NodeCount.ShouldBeLessThanOrEqualTo(60);
            generated.Features.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
        sawNonInteger.ShouldBeTrue();
    }

    [Test]
    public void RegistriesRejectDuplicates()
    {
        GeneratorRegistry rules = new();
        PrimitiveDuplicate rule = new();
        rules.Register(rule);
        Should.Throw<ArgumentException>(() => rules.Register(rule));
        CombinationRegistry combinations = new();
        CombinationDuplicate combination = new();
        combinations.Register(combination);
        Should.Throw<ArgumentException>(() => combinations.Register(combination));

        FuzzProfileRegistry profiles = new();
        FuzzProfile profile = new(
            "duplicate",
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["parse"],
            [AshesType.Int],
            0);
        profiles.Register(profile);
        Should.Throw<ArgumentException>(() => profiles.Register(profile));
    }

    [Test]
    public void RegistryIterationIsOrdinalAndRepeatable()
    {
        var first = TestFixture.Create();
        var second = TestFixture.Create();
        string[] firstRules = first.Rules.Rules.Select(rule => rule.Id).ToArray();
        string[] firstCombinations = first.Combinations.Templates.Select(template => template.Id).ToArray();

        firstRules.ShouldBe(firstRules.Order(StringComparer.Ordinal));
        firstCombinations.ShouldBe(firstCombinations.Order(StringComparer.Ordinal));
        second.Rules.Rules.Select(rule => rule.Id).ShouldBe(firstRules);
        second.Combinations.Templates.Select(template => template.Id).ShouldBe(firstCombinations);
    }

    [Test]
    public void ProfilesRejectUnknownRulesAndCombinations()
    {
        var fixture = TestFixture.Create();
        FuzzProfileRegistry unknownRule = new();
        unknownRule.Register(new FuzzProfile(
            "unknown-rule",
            new HashSet<string>(StringComparer.Ordinal) { "missing-rule" },
            new HashSet<string>(StringComparer.Ordinal),
            ["parse"],
            [AshesType.Int],
            0));
        Should.Throw<ArgumentException>(() => unknownRule.Validate(fixture.Rules, fixture.Combinations))
            .Message.ShouldContain("missing-rule");

        FuzzProfileRegistry unknownCombination = new();
        unknownCombination.Register(new FuzzProfile(
            "unknown-combination",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "missing-combination" },
            ["parse"],
            [AshesType.Int],
            0));
        Should.Throw<ArgumentException>(() => unknownCombination.Validate(fixture.Rules, fixture.Combinations))
            .Message.ShouldContain("missing-combination");
    }

    [Test]
    public void ProfilesRejectUnknownOraclesAndInvalidCampaignInputs()
    {
        var fixture = TestFixture.Create();
        FuzzOracleRegistry oracles = FuzzOracleRegistry.CreateDefault();
        FuzzProfileRegistry unknownOracle = new();
        unknownOracle.Register(new FuzzProfile(
            "unknown-oracle",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["missing-oracle"],
            [AshesType.Int],
            0));

        Should.Throw<ArgumentException>(() => unknownOracle.ValidateOracles(oracles))
            .Message.ShouldContain("missing-oracle");

        FuzzProfileRegistry invalidTarget = new();
        invalidTarget.Register(new FuzzProfile(
            "invalid-target",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["parse"],
            [AshesType.Int],
            0,
            Defaults: new FuzzProfileDefaults(1, 10, 1, 1, ["unknown-target"])));

        Should.Throw<ArgumentException>(() => invalidTarget.Validate(fixture.Rules, fixture.Combinations))
            .Message.ShouldContain("invalid campaign defaults");
    }

    [Test]
    public void CombinationGenerationRejectsMissingAdvertisedFeatures()
    {
        var fixture = TestFixture.Create();
        MissingAdvertisedFeature invalid = new();
        CombinationRegistry combinations = new();
        combinations.Register(invalid);
        string[] ruleIds = fixture.Rules.Rules.Select(rule => rule.Id).ToArray();
        GenerationCoverageGuidance coverage = new(ruleIds, [invalid.Id]);
        CombinationGenerator combinationGenerator = new(
            combinations,
            new HashSet<string>(StringComparer.Ordinal) { invalid.Id },
            coverage,
            invalid.Id);
        ExpressionGenerator expressions = new(
            fixture.Rules,
            ruleIds.ToHashSet(StringComparer.Ordinal),
            coverage,
            combinationGenerator,
            forcePreferredCombination: true);

        Should.Throw<InvalidOperationException>(() => expressions.Generate(
            AshesType.Int,
            GenerationContext.Empty,
            GenerationBudget.Create(80),
            new FuzzRandom(1))).Message.ShouldContain("advertised but did not record");
    }

    [Test]
    public void RegistryRejectsRulesThatCannotGenerateAdvertisedTypes()
    {
        GeneratorRegistry rules = new();
        ArgumentException exception = Should.Throw<ArgumentException>(() => rules.Register(new InvalidAdvertisement()));
        exception.Message.ShouldContain("advertises unsupported type");
        rules.Rules.ShouldBeEmpty();
    }

    [Test]
    public void EveryRegisteredRuleCanGenerateInACompatibleContext()
    {
        var fixture = TestFixture.Create();
        GenerationContext context = GenerationContext.Empty.WithBinding(new GeneratedBinding("available", AshesType.Int));
        foreach (IExpressionGenerationRule rule in fixture.Rules.Rules)
        {
            rule.AdvertisedTypes.ShouldNotBeEmpty();
            foreach (AshesType advertisedType in rule.AdvertisedTypes)
            {
                GenerationContext compatibleContext = context.WithBinding(new GeneratedBinding("advertised", advertisedType));
                rule.CanGenerate(advertisedType, compatibleContext, GenerationBudget.Create(80))
                    .ShouldBeTrue($"Rule '{rule.Id}' cannot generate advertised type '{advertisedType}'.");
            }
        }
    }

    [Test]
    public void CoverageGuidanceRaisesWeightsForUnderrepresentedRulesAndCombinations()
    {
        GenerationCoverageGuidance coverage = new(["common-rule", "rare-rule"], ["common-template", "rare-template"]);
        coverage.RecordRule("common-rule");
        coverage.RecordRule("common-rule");
        coverage.RecordCombination("common-template");

        coverage.RuleWeight("rare-rule", 2, false).ShouldBeGreaterThan(coverage.RuleWeight("common-rule", 2, false));
        coverage.CombinationWeight("rare-template", false).ShouldBeGreaterThan(coverage.CombinationWeight("common-template", false));
        Should.Throw<InvalidOperationException>(() => coverage.RecordRule("unknown"));
    }

    [Test]
    public void ContextTracksEffectsOwnershipAndActiveHandlersForTemplatePreconditions()
    {
        GeneratedCapability capability = new(
            "GeneratedEffect",
            [new GeneratedCapabilityOperation("get", AshesType.Unit, AshesType.Str)]);
        GenerationContext context = GenerationContext.Empty
            .WithFlags(GenerationFlags.None)
            .WithOwnershipInterests([])
            .WithCapability(capability)
            .WithActiveHandler(capability.Name)
            .WithFeature(GeneratedFeature.Handler);

        context.Capabilities.ShouldContain(capability);
        context.ActiveHandlers.ShouldContain(capability.Name);
        context.CurrentFeatures.ShouldContain(GeneratedFeature.Handler);
        context.Allows(GenerationFlags.SuspensionAllowed).ShouldBeFalse();
        context.IsInterestedIn(OwnershipInterest.Reuse).ShouldBeFalse();
        GenerationBudget budget = GenerationBudget.Create(120);
        new AsyncCaptureTemplate().CanApply(AshesType.Str, context, budget).ShouldBeFalse();
        new DeterministicResourceTemplate().CanApply(AshesType.Str, context, budget).ShouldBeFalse();
        new BoundedRecursionTemplate().CanApply(AshesType.Str, context, budget).ShouldBeFalse();
        new SharedReconstructionFallbackTemplate().CanApply(AshesType.Str, context, budget).ShouldBeFalse();
    }

    [Test]
    public void ResourceTypesAreExplicitAndRestrictedToResourceCapableProfiles()
    {
        var fixture = TestFixture.Create();
        FuzzProfile resources = fixture.Profiles.Get("resources");

        resources.EffectiveResourceTypes.ShouldBe([AshesType.FileHandle]);
        AshesType.FileHandle.ToSyntax().ShouldBe(new Ashes.Frontend.TypeExpr.Named("FileHandle"));
        fixture.Profiles.Profiles
            .Where(profile => profile.Id is not ("resources" or "all"))
            .ShouldAllBe(profile => profile.EffectiveResourceTypes.Count == 0);

        FuzzProfileRegistry invalid = new();
        invalid.Register(new FuzzProfile(
            "invalid-resource",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["parse"],
            [AshesType.Int],
            0,
            ResourceTypes: [AshesType.FileHandle]));

        Should.Throw<ArgumentException>(() => invalid.Validate(fixture.Rules, fixture.Combinations))
            .Message.ShouldContain("without resource generation");

        FuzzProfileRegistry unknown = new();
        unknown.Register(new FuzzProfile(
            "unknown-resource",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            ["parse"],
            [AshesType.Int],
            0,
            ContextFlags: GenerationFlags.ResourcesAllowed,
            ResourceTypes: [new AshesType.Resource("UnknownResource")]));

        Should.Throw<ArgumentException>(() => unknown.Validate(fixture.Rules, fixture.Combinations))
            .Message.ShouldContain("unknown resource type");
    }

    [Test]
    public void OwnershipTemplatesAdvertiseAliasFreshnessAndStaticUniquenessOutcomes()
    {
        var fixture = TestFixture.Create();

        fixture.Combinations.Get("sharing.branch-alias").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.ResultAliasesInput);
        fixture.Combinations.Get("sharing.tuple-fields").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.FreshResultInternalSharing);
        fixture.Combinations.Get("perceus.unique-record-update").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.StaticallyUniquePath);
        fixture.Combinations.Get("perceus.captured-reuse-candidate").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.CapturedReuseCandidate);
        fixture.Combinations.Get("async.task-result-reuse").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.TaskResultReuse);
        fixture.Combinations.Get("async.structured-fork-join").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.StructuredScope);
        fixture.Combinations.Get("async.structured-fork-join").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.Fork);
        fixture.Combinations.Get("async.structured-fork-join").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.Join);
        fixture.Combinations.Get("perceus.shared-reconstruction-fallback").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.AliasedResultPreventsReuse);
        fixture.Combinations.Get("perceus.unique-record-update").AdvertisedFeatures
            .ShouldContain(GeneratedFeature.FreshResultAllowsReuse);
    }

    [Test]
    public void CompletedTaskResultsFlowThroughReuseSensitiveRecordUpdates()
    {
        var fixture = TestFixture.Create();
        FuzzProfile profile = new(
            "task-result-reuse",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "async.task-result-reuse" },
            ["parse", "format", "semantic", "ir"],
            [new AshesType.Record("FuzzRecord")],
            2,
            OwnershipInterests: Enum.GetValues<OwnershipInterest>().ToHashSet());

        GeneratedFuzzCase generated = fixture.Generator.Generate(20260809, 1, profile, 120);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
        _ = new Lowering(diagnostics).Lower(parsed);

        generated.Features.Contains(GeneratedFeature.TaskResultReuse).ShouldBeTrue();
        generated.Source.ShouldContain("Ashes.Task.run(async(");
        generated.Source.ShouldContain(" with first = ");
        diagnostics.Errors.ShouldBeEmpty(generated.Source);
    }

    [Test]
    public void ClosuresAndMatchesCrossAwaitForMultipleResultTypes()
    {
        var fixture = TestFixture.Create();
        AshesType[] resultTypes =
        [
            AshesType.Int,
            AshesType.Str,
            new AshesType.Adt("FuzzTree", [AshesType.Bool]),
        ];

        for (int index = 0; index < resultTypes.Length; index++)
        {
            FuzzProfile profile = new(
                "closure-match-across-await",
                fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal) { "async.closure-match-across-await" },
                ["parse", "format", "semantic", "ir"],
                [resultTypes[index]],
                2);
            GeneratedFuzzCase generated = fixture.Generator.Generate(20260810, index, profile, 140);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);

            generated.Features.Contains(GeneratedFeature.ClosureAcrossAwait).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.MatchAcrossAwait).ShouldBeTrue();
            generated.Trace.Entries.ShouldContain(entry =>
                entry.StartsWith("async:closure-match-across-await", StringComparison.Ordinal));
            new AstInvariantValidator().ValidateScope(generated.Program).ShouldBeEmpty();
            diagnostics.Errors.ShouldBeEmpty(generated.Source);
        }
    }

    [Test]
    public void BoundedRecursionCanReturnResultValues()
    {
        var fixture = TestFixture.Create();
        AshesType.Result resultType = new(AshesType.Str, new AshesType.List(AshesType.Bool));
        FuzzProfile profile = new(
            "recursive-result",
            fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "recursion.bounded-capture" },
            ["parse", "format", "semantic", "ir"],
            [resultType],
            2);

        GeneratedFuzzCase generated = fixture.Generator.Generate(20260811, 0, profile, 140);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        _ = lowering.Lower(parsed);

        generated.Features.Contains(GeneratedFeature.RecursiveFunction).ShouldBeTrue();
        generated.Features.Contains(GeneratedFeature.RecursiveResult).ShouldBeTrue();
        generated.Features.Contains(GeneratedFeature.RecursionWithSharing).ShouldBeTrue();
        generated.Features.Contains(GeneratedFeature.SharedValue).ShouldBeTrue();
        generated.Source.ShouldContain("Result(Str, List(Bool))");
        diagnostics.Errors.ShouldBeEmpty(generated.Source);
        lowering.LastLoweredType.ShouldNotBeNull();
        lowering.FormatType(lowering.LastLoweredType).ShouldBe("Result<Str, List<Bool>>");
    }

    [Test]
    public void NestedReusableConstructorsSupportMultiplePayloadTypes()
    {
        var fixture = TestFixture.Create();
        AshesType[] payloadTypes = [AshesType.Int, AshesType.Str];
        for (int index = 0; index < payloadTypes.Length; index++)
        {
            AshesType.Adt resultType = new("FuzzTree", [payloadTypes[index]]);
            FuzzProfile profile = new(
                "nested-reusable-constructors",
                fixture.Rules.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal) { "perceus.nested-reusable-constructors" },
                ["parse", "format", "semantic", "ir"],
                [resultType],
                2,
                OwnershipInterests: Enum.GetValues<OwnershipInterest>().ToHashSet());

            GeneratedFuzzCase generated = fixture.Generator.Generate(20260813, index, profile, 160);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(generated.Source, diagnostics).ParseProgram();
            Lowering lowering = new(diagnostics);
            _ = lowering.Lower(parsed);

            generated.Features.Contains(GeneratedFeature.NestedReusableConstructors).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.NestedMatch).ShouldBeTrue();
            generated.Features.Contains(GeneratedFeature.LayoutCompatibleReuse).ShouldBeTrue();
            diagnostics.Errors.ShouldBeEmpty(generated.Source);
            lowering.LastLoweredType.ShouldNotBeNull();
            lowering.FormatType(lowering.LastLoweredType)
                .ShouldBe($"FuzzTree<{payloadTypes[index]}>");
        }
    }

    [Test]
    public void ImmediateClosureTemplateAlwaysReferencesItsCaptureAndParameter()
    {
        var fixture = TestFixture.Create();
        string[] ruleIds = fixture.Rules.Rules.Select(rule => rule.Id).ToArray();
        GenerationCoverageGuidance coverage = new(ruleIds, []);
        ExpressionGenerator expressions = new(
            fixture.Rules,
            ruleIds.ToHashSet(StringComparer.Ordinal),
            coverage);
        ClosureCaptureTemplate template = new();

        for (ulong seed = 0; seed < 20; seed++)
        {
            GenerationResult<Ashes.Frontend.Expr> generated = template.Generate(
                new AshesType.Tuple([AshesType.Str, AshesType.Bool]),
                GenerationContext.Empty.WithTemplate(template.Id),
                GenerationBudget.Create(100),
                expressions,
                new FuzzRandom(seed));

            Ashes.Frontend.Expr.Let capture = generated.Value.ShouldBeOfType<Ashes.Frontend.Expr.Let>();
            Ashes.Frontend.Expr.Let function = capture.Body.ShouldBeOfType<Ashes.Frontend.Expr.Let>();
            Ashes.Frontend.Expr.Lambda lambda = function.Value.ShouldBeOfType<Ashes.Frontend.Expr.Lambda>();
            Ashes.Frontend.Expr.Let captureUse = lambda.Body.ShouldBeOfType<Ashes.Frontend.Expr.Let>();
            captureUse.Value.ShouldBe(new Ashes.Frontend.Expr.Var(capture.Name));
            Ashes.Frontend.Expr.Let parameterUse = captureUse.Body.ShouldBeOfType<Ashes.Frontend.Expr.Let>();
            parameterUse.Value.ShouldBe(new Ashes.Frontend.Expr.Var(lambda.ParamName));
        }
    }

    private sealed class PrimitiveDuplicate : IExpressionGenerationRule
    {
        public string Id => "duplicate";
        public int Weight => 1;
        public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Generic;
        public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => true;
        public GenerationResult<Ashes.Frontend.Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random) => ExpressionGenerator.GenerateLeaf(requiredType, context, budget, random);
    }

    private sealed class InvalidAdvertisement : IExpressionGenerationRule
    {
        public string Id => "invalid-advertisement";
        public int Weight => 1;
        public IReadOnlyList<AshesType> AdvertisedTypes => [new AshesType.List(AshesType.Int)];
        public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType == AshesType.Int;
        public GenerationResult<Ashes.Frontend.Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random) => ExpressionGenerator.GenerateLeaf(requiredType, context, budget, random);
    }

    private sealed class CombinationDuplicate : ICombinationTemplate
    {
        public string Id => "duplicate";
        public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new HashSet<GeneratedFeature> { GeneratedFeature.Literal };
        public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => true;
        public GenerationResult<Ashes.Frontend.Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random) => ExpressionGenerator.GenerateLeaf(resultType, context, budget, random);
    }

    private sealed class MissingAdvertisedFeature : ICombinationTemplate
    {
        public string Id => "missing-advertised-feature";
        public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new HashSet<GeneratedFeature>
        {
            GeneratedFeature.Match,
        };
        public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => true;
        public GenerationResult<Ashes.Frontend.Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random) =>
            ExpressionGenerator.GenerateLeaf(resultType, context, budget, random);
    }
}
