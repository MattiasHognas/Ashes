using Ashes.Frontend;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal interface ICombinationTemplate
{
    string Id { get; }
    IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; }
    bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget);
    GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random);
}

internal sealed class CombinationRegistry
{
    private readonly SortedDictionary<string, ICombinationTemplate> _templates = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<ICombinationTemplate> Templates => _templates.Values;

    internal ICombinationTemplate Get(string id) => _templates.TryGetValue(id, out ICombinationTemplate? template)
        ? template
        : throw new ArgumentException($"Unknown combination template '{id}'.");

    internal void Register(ICombinationTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.Id) || template.AdvertisedFeatures.Count == 0 || !_templates.TryAdd(template.Id, template))
        {
            throw new ArgumentException($"Duplicate or invalid combination template '{template.Id}'.");
        }
    }

    internal static CombinationRegistry CreateDefault()
    {
        CombinationRegistry registry = new();
        registry.Register(new SharingBranchTemplate());
        registry.Register(new SharedTupleTemplate());
        registry.Register(new NestedAliasTemplate());
        registry.Register(new ClosureCaptureTemplate());
        registry.Register(new AdtMatchTemplate());
        registry.Register(new ConstructorReconstructionTemplate());
        registry.Register(new BoundedRecursionTemplate());
        registry.Register(new CapabilityHandlerTemplate());
        registry.Register(new AsyncCaptureTemplate());
        registry.Register(new AsyncClosureMatchAcrossAwaitTemplate());
        registry.Register(new AsyncSpawnSharedValueTemplate());
        registry.Register(new AsyncTaskResultReuseTemplate());
        registry.Register(new StructuredConcurrencyTemplate());
        registry.Register(new EscapingClosureTemplate());
        registry.Register(new MultipleClosuresTemplate());
        registry.Register(new ListHeadSharingTemplate());
        registry.Register(new ListReconstructionTemplate());
        registry.Register(new TreeLayoutFallbackTemplate());
        registry.Register(new GuardedMatchTemplate());
        registry.Register(new NestedCapabilityHandlersTemplate());
        registry.Register(new DeterministicResourceTemplate());
        registry.Register(new SharedReconstructionFallbackTemplate());
        registry.Register(new BranchSelectiveReuseTemplate());
        registry.Register(new UniqueRecordUpdateTemplate());
        registry.Register(new NestedReusableConstructorsTemplate());
        registry.Register(new CapturedReuseCandidateTemplate());
        registry.Register(new BoundedListTraversalTemplate());
        registry.Register(new CapturedAdtMatchClosureTemplate());
        registry.Register(new ClosureInMatchBranchTemplate());
        registry.Register(new RecursiveListReconstructionTemplate());
        registry.Register(new ResultClosurePipeTemplate());
        registry.Register(new CapabilityClosureMatchTemplate());
        registry.Register(new CapabilityResultTemplate());
        registry.Register(new CapabilityRecursiveListTemplate());
        registry.Register(new NestedAdtMatchTemplate());
        registry.Register(new LoopCarriedAdtTemplate());
        registry.Register(new TraitConstrainedClosureTemplate());
        registry.Register(new TraitDerivedOperatorSharingTemplate());
        registry.Register(new RuneRoundTripTemplate());
        return registry;
    }
}

internal sealed class CombinationGenerator
{
    private readonly CombinationRegistry _registry;
    private readonly IReadOnlySet<string> _enabled;
    private readonly string? _preferredTemplate;
    private readonly GenerationCoverageGuidance _coverage;

    internal CombinationGenerator(
        CombinationRegistry registry,
        IReadOnlySet<string> enabled,
        GenerationCoverageGuidance coverage,
        string? preferredTemplate = null)
    {
        _registry = registry;
        _enabled = enabled;
        _preferredTemplate = preferredTemplate;
        _coverage = coverage;
    }

    internal GenerationResult<Expr>? TryGenerate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        ICombinationTemplate[] candidates = _registry.Templates
            .Where(template => _enabled.Contains(template.Id) && !context.ActiveTemplates.Contains(template.Id) && template.CanApply(resultType, context, budget))
            .OrderBy(template => template.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        ICombinationTemplate? preferred = candidates.FirstOrDefault(template =>
            string.Equals(template.Id, _preferredTemplate, StringComparison.Ordinal));
        ICombinationTemplate selected;
        if (preferred is not null)
        {
            selected = preferred;
        }
        else
        {
            int totalWeight = candidates.Sum(template => EffectiveWeight(template.Id));
            int choice = random.Next(totalWeight);
            selected = candidates[0];
            foreach (ICombinationTemplate template in candidates)
            {
                int weight = EffectiveWeight(template.Id);
                if (choice < weight)
                {
                    selected = template;
                    break;
                }
                choice -= weight;
            }
        }
        _coverage.RecordCombination(selected.Id);
        GenerationBudget templateBudget = budget.UseCombination()
            .LimitNodes(Math.Max(2, budget.RemainingNodes / 4))
            .LimitDepth(3);
        GenerationResult<Expr> result = selected.Generate(
            resultType,
            context.WithTemplate(selected.Id),
            templateBudget,
            expressions,
            random);
        foreach (GeneratedFeature feature in selected.AdvertisedFeatures)
        {
            if (!result.Features.Contains(feature))
            {
                throw new InvalidOperationException($"Combination '{selected.Id}' advertised but did not record '{feature}'.");
            }
        }
        if (result.Trace.Entries.Skip(1).Any(entry => entry.StartsWith("combination:", StringComparison.Ordinal)))
        {
            result.Features.Add(GeneratedFeature.NestedCombination);
        }
        return result with { Trace = new GenerationTrace([$"combination:{selected.Id}", .. result.Trace.Entries]) };
    }

    private int EffectiveWeight(string id) => _coverage.CombinationWeight(
        id,
        string.Equals(id, _preferredTemplate, StringComparison.Ordinal));
}
