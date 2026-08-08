using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class AdtMatchTemplate : ICombinationTemplate
{
    public string Id => "match.adt-extract";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.Let };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 6;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> payload = expressions.Generate(resultType, context, budget.Descend(4), random);
        string box = "box" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string item = "item" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr constructor = new Expr.Call(new Expr.Var("FuzzBox"), payload.Value);
        Expr match = new Expr.Match(new Expr.Var(box), [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(item)]), new Expr.Var(item))]);
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(payload.Features);
        return new GenerationResult<Expr>(new Expr.Let(box, constructor, match), resultType, features, GenerationTrace.Merge("match:adt", payload.Trace), payload.NodeCount + 5);
    }
}

internal sealed class ConstructorReconstructionTemplate : ICombinationTemplate
{
    public string Id => "perceus.constructor-reconstruction";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.ConstructorReconstruction, GeneratedFeature.ReuseCandidate, GeneratedFeature.LayoutCompatibleReuse };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 8;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> payload = expressions.Generate(resultType, context, budget.Descend(5), random);
        string box = "reuseBox" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string item = "reuseItem" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr construct = new Expr.Call(new Expr.Var("FuzzBox"), payload.Value);
        Expr reconstruct = new Expr.Call(new Expr.Var("FuzzBox"), new Expr.Var(item));
        Expr extractAgain = new Expr.Match(reconstruct, [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var("result")]), new Expr.Var("result"))]);
        Expr outerMatch = new Expr.Match(new Expr.Var(box), [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(item)]), extractAgain)]);
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(payload.Features);
        return new GenerationResult<Expr>(new Expr.Let(box, construct, outerMatch), resultType, features, GenerationTrace.Merge("perceus:reconstruct", payload.Trace), payload.NodeCount + 8);
    }
}
