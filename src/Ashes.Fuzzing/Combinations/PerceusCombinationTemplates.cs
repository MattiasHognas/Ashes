using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class SharedReconstructionFallbackTemplate : ICombinationTemplate
{
    public string Id => "perceus.shared-reconstruction-fallback";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Adt,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
        GeneratedFeature.SharedValue,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.SharedReuseFallback,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> payload = expressions.Generate(resultType, context, budget.Descend(8), random);
        string box = Name("sharedReuseBox", random);
        string alias = Name("sharedReuseAlias", random);
        string item = Name("sharedReuseItem", random);
        string rebuiltItem = Name("sharedRebuiltItem", random);
        Expr rebuilt = new Expr.Call(new Expr.Var("FuzzBox"), new Expr.Var(item));
        Expr extractRebuilt = new Expr.Match(
            rebuilt,
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(rebuiltItem)]), new Expr.Var(rebuiltItem))]);
        Expr consumeAlias = new Expr.Match(
            new Expr.Var(alias),
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Wildcard()]), extractRebuilt)]);
        Expr inspectOriginal = new Expr.Match(
            new Expr.Var(box),
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(item)]), consumeAlias)]);
        Expr value = new Expr.Let(
            box,
            new Expr.Call(new Expr.Var("FuzzBox"), payload.Value),
            new Expr.Let(alias, new Expr.Var(box), inspectOriginal));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(payload.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("perceus:shared-reconstruction-fallback", payload.Trace),
            payload.NodeCount + 14);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class BranchSelectiveReuseTemplate : ICombinationTemplate
{
    public string Id => "perceus.branch-selective-reuse";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Adt,
        GeneratedFeature.If,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.BranchSelectiveReuse,
        GeneratedFeature.CrossBranchAlias,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> payload = expressions.Generate(resultType, context, budget.Descend(8), random);
        GenerationResult<Expr> condition = expressions.Generate(AshesType.Bool, context, budget.Descend(8), random);
        string box = Name("branchReuseBox", random);
        string item = Name("branchReuseItem", random);
        string rebuiltItem = Name("branchRebuiltItem", random);
        Expr rebuilt = new Expr.Call(new Expr.Var("FuzzBox"), new Expr.Var(item));
        Expr reconstruct = new Expr.Match(
            rebuilt,
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(rebuiltItem)]), new Expr.Var(rebuiltItem))]);
        Expr branch = new Expr.If(condition.Value, reconstruct, new Expr.Var(item));
        Expr match = new Expr.Match(
            new Expr.Var(box),
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(item)]), branch)]);
        Expr value = new Expr.Let(box, new Expr.Call(new Expr.Var("FuzzBox"), payload.Value), match);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(payload.Features);
        features.UnionWith(condition.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("perceus:branch-selective-reuse", payload.Trace, condition.Trace),
            payload.NodeCount + condition.NodeCount + 13);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class UniqueRecordUpdateTemplate : ICombinationTemplate
{
    public string Id => "perceus.unique-record-update";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Record,
        GeneratedFeature.Let,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.RuntimeUniquenessCheck,
        GeneratedFeature.UniqueConstructorUpdate,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType is AshesType.Record { Name: "FuzzRecord" } && budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> first = expressions.Generate(AshesType.Int, context, budget.Descend(6), random);
        GenerationResult<Expr> second = expressions.Generate(AshesType.Bool, context, budget.Descend(6), random);
        GenerationResult<Expr> replacement = expressions.Generate(AshesType.Int, context, budget.Descend(6), random);
        string record = Name("uniqueRecord", random);
        Expr original = new Expr.RecordLit("FuzzRecord", [("first", first.Value), ("second", second.Value)]);
        Expr update = new Expr.RecordUpdate(new Expr.Var(record), [("first", replacement.Value)]);
        Expr value = new Expr.Let(record, original, update);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(first.Features);
        features.UnionWith(second.Features);
        features.UnionWith(replacement.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("perceus:unique-record-update", first.Trace, second.Trace, replacement.Trace),
            first.NodeCount + second.NodeCount + replacement.NodeCount + 5);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class BoundedListTraversalTemplate : ICombinationTemplate
{
    public string Id => "recursion.bounded-list-traversal";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.List,
        GeneratedFeature.Match,
        GeneratedFeature.RecursiveFunction,
        GeneratedFeature.TailCall,
        GeneratedFeature.ClosureCapture,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingRecursion > 0 && budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> result = expressions.Generate(
            resultType,
            context,
            budget.Descend(8) with { RemainingRecursion = budget.RemainingRecursion - 1 },
            random);
        AshesType.List listType = new(AshesType.Int);
        string captured = Name("listTraversalResult", random);
        string loop = Name("listTraversalLoop", random);
        string items = Name("listTraversalItems", random);
        string tail = Name("listTraversalTail", random);
        Expr body = new Expr.Match(
            new Expr.Var(items),
            [
                new MatchCase(new Pattern.EmptyList(), new Expr.Var(captured)),
                new MatchCase(
                    new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Var(tail)),
                    new Expr.Call(new Expr.Var(loop), new Expr.Var(tail))),
            ]);
        Expr lambda = new Expr.Lambda(items, body) { ParamAnnotation = listType.ToSyntax() };
        Expr input = new Expr.ListLit([new Expr.IntLit(1), new Expr.IntLit(2), new Expr.IntLit(3)]);
        Expr traverse = new Expr.LetRecursive(loop, lambda, new Expr.Call(new Expr.Var(loop), input))
        {
            TypeAnnotation = new AshesType.Function(listType, resultType).ToSyntax(),
        };
        Expr value = new Expr.Let(captured, result.Value, traverse);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(result.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("recursion:bounded-list-traversal", result.Trace),
            result.NodeCount + 14);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
