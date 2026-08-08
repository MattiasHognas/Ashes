using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class SharingBranchTemplate : ICombinationTemplate
{
    public string Id => "sharing.branch-alias";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Let, GeneratedFeature.If, GeneratedFeature.SharedValue, GeneratedFeature.CrossBranchAlias, GeneratedFeature.ResultAliasesInput };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 6;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        string name = "shared" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> value = expressions.Generate(resultType, context, budget.Descend(4), random);
        GenerationResult<Expr> condition = expressions.Generate(AshesType.Bool, context, budget.Descend(4), random);
        Expr body = new Expr.If(condition.Value, new Expr.Var(name), new Expr.Var(name));
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(value.Features); features.UnionWith(condition.Features);
        return new GenerationResult<Expr>(new Expr.Let(name, value.Value, body), resultType, features, GenerationTrace.Merge("sharing:branch", value.Trace, condition.Trace), value.NodeCount + condition.NodeCount + 4);
    }
}

internal sealed class SharedTupleTemplate : ICombinationTemplate
{
    public string Id => "sharing.tuple-fields";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Let, GeneratedFeature.Tuple, GeneratedFeature.SharedValue, GeneratedFeature.FreshResultInternalSharing };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => resultType is AshesType.Tuple { Elements.Count: 2 } tuple && tuple.Elements[0] == tuple.Elements[1] && budget.RemainingNodes >= 5;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Tuple tuple = (AshesType.Tuple)resultType;
        string name = "shared" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> value = expressions.Generate(tuple.Elements[0], context, budget.Descend(3), random);
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(value.Features);
        Expr result = new Expr.Let(name, value.Value, new Expr.TupleLit([new Expr.Var(name), new Expr.Var(name)]));
        return new GenerationResult<Expr>(result, resultType, features, GenerationTrace.Merge("sharing:tuple", value.Trace), value.NodeCount + 4);
    }
}

internal sealed class NestedAliasTemplate : ICombinationTemplate
{
    public string Id => "sharing.nested-alias";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Let, GeneratedFeature.SharedValue, GeneratedFeature.ResultAliasesInput };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 5;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> value = expressions.Generate(resultType, context, budget.Descend(3), random);
        string first = "aliasA" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string second = "aliasB" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr result = new Expr.Let(first, value.Value, new Expr.Let(second, new Expr.Var(first), new Expr.Var(second)));
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(value.Features);
        return new GenerationResult<Expr>(result, resultType, features, GenerationTrace.Merge("sharing:nested-alias", value.Trace), value.NodeCount + 4);
    }
}
