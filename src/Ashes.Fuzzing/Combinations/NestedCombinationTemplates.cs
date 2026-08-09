using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class NestedAdtMatchTemplate : ICombinationTemplate
{
    public string Id => "match.nested-adt";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Adt,
        GeneratedFeature.Match,
        GeneratedFeature.NestedMatch,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingNodes >= 18;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> first = expressions.Generate(resultType, context, budget.Descend(9), random);
        GenerationResult<Expr> second = expressions.Generate(resultType, context, budget.Descend(9), random);
        GenerationResult<Expr> fallback = expressions.Generate(resultType, context, budget.Descend(9), random);
        string left = Name("nestedLeft", random);
        string value = Name("nestedValue", random);
        Expr firstLeaf = new Expr.Call(new Expr.Var("FuzzLeaf"), first.Value);
        Expr secondLeaf = new Expr.Call(new Expr.Var("FuzzLeaf"), second.Value);
        Expr tree = new Expr.Call(new Expr.Call(new Expr.Var("FuzzBranch"), firstLeaf), secondLeaf);
        Expr inner = new Expr.Match(new Expr.Var(left),
        [
            new MatchCase(new Pattern.Constructor("FuzzEmpty", []), fallback.Value),
            new MatchCase(new Pattern.Constructor("FuzzLeaf", [new Pattern.Var(value)]), new Expr.Var(value)),
            new MatchCase(new Pattern.Constructor("FuzzBranch", [new Pattern.Wildcard(), new Pattern.Wildcard()]), fallback.Value),
        ]);
        Expr outer = new Expr.Match(tree,
        [
            new MatchCase(new Pattern.Constructor("FuzzEmpty", []), fallback.Value),
            new MatchCase(new Pattern.Constructor("FuzzLeaf", [new Pattern.Var(value)]), new Expr.Var(value)),
            new MatchCase(new Pattern.Constructor("FuzzBranch", [new Pattern.Var(left), new Pattern.Wildcard()]), inner),
        ]);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(first.Features);
        features.UnionWith(second.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(
            outer,
            resultType,
            features,
            GenerationTrace.Merge($"match:nested-adt:{resultType}", first.Trace, second.Trace, fallback.Trace),
            first.NodeCount + second.NodeCount + fallback.NodeCount + 13);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class LoopCarriedAdtTemplate : ICombinationTemplate
{
    public string Id => "recursion.loop-carried-adt";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Adt,
        GeneratedFeature.If,
        GeneratedFeature.RecursiveFunction,
        GeneratedFeature.TailCall,
        GeneratedFeature.LoopCarriedAdt,
        GeneratedFeature.RecursiveReconstruction,
        GeneratedFeature.ReuseCandidate,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.RecursionAllowed) &&
        budget.RemainingRecursion > 0 &&
        resultType is AshesType.Adt { Name: "FuzzTree", Arguments.Count: 1 } &&
        budget.RemainingNodes >= 24;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> seed = expressions.Generate(
            resultType,
            context,
            budget.Descend(12) with { RemainingRecursion = budget.RemainingRecursion - 1 },
            random);
        string loop = Name("adtLoop", random);
        string remaining = Name("adtRemaining", random);
        string carried = Name("adtCarried", random);
        Expr condition = new Expr.LessOrEqual(new Expr.Var(remaining), new Expr.IntLit(0));
        Expr reconstructed = new Expr.Call(
            new Expr.Call(new Expr.Var("FuzzBranch"), new Expr.Var(carried)),
            new Expr.Var("FuzzEmpty"));
        Expr decrement = new Expr.Subtract(new Expr.Var(remaining), new Expr.IntLit(1));
        Expr recursiveCall = new Expr.Call(
            new Expr.Call(new Expr.Var(loop), decrement),
            reconstructed);
        Expr body = new Expr.If(condition, new Expr.Var(carried), recursiveCall);
        Expr lambda = new Expr.Lambda(
            remaining,
            new Expr.Lambda(carried, body) { ParamAnnotation = resultType.ToSyntax() })
        {
            ParamAnnotation = AshesType.Int.ToSyntax(),
        };
        AshesType functionType = new AshesType.Function(
            AshesType.Int,
            new AshesType.Function(resultType, resultType));
        Expr initialCall = new Expr.Call(
            new Expr.Call(new Expr.Var(loop), new Expr.IntLit(2)),
            seed.Value);
        Expr value = new Expr.LetRecursive(loop, lambda, initialCall)
        {
            TypeAnnotation = functionType.ToSyntax(),
        };
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(seed.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge($"recursion:loop-carried-adt:{resultType}", seed.Trace),
            seed.NodeCount + 23);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
