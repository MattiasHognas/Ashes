using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
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
            foreach (AshesType type in types)
            {
                GenerationBudget budget = GenerationBudget.Create(80);
                if (!template.CanApply(type, GenerationContext.Empty, budget)) continue;
                string[] ruleIds = fixture.Rules.Rules.Select(rule => rule.Id).ToArray();
                GenerationCoverageGuidance coverage = new(ruleIds, []);
                ExpressionGenerator expressions = new(fixture.Rules, ruleIds.ToHashSet(StringComparer.Ordinal), coverage);
                GenerationResult<Ashes.Frontend.Expr> result = template.Generate(type, GenerationContext.Empty.WithTemplate(template.Id), budget, expressions, new FuzzRandom(42));
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
}
