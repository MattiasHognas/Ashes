using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class TraitConstrainedClosureTemplate : ICombinationTemplate
{
    public string Id => "trait.constrained-closure";

    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } =
        new SortedSet<GeneratedFeature>
        {
            GeneratedFeature.Call,
            GeneratedFeature.ClosureCapture,
            GeneratedFeature.Lambda,
            GeneratedFeature.Let,
            GeneratedFeature.TraitConstraint,
            GeneratedFeature.TraitMethodCall,
            GeneratedFeature.TraitResolution,
        };

    public bool CanApply(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget) =>
        budget.RemainingNodes >= 10
        && context.Traits.Any(trait => trait.ImplementedTypes.Contains(resultType));

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GeneratedTrait trait = context.Traits
            .Where(candidate => candidate.ImplementedTypes.Contains(resultType))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .First();
        GenerationResult<Expr> left = expressions.Generate(
            resultType,
            context,
            budget.Descend(6).LimitDepth(2),
            random);
        GenerationResult<Expr> right = expressions.Generate(
            resultType,
            context,
            budget.Descend(6).LimitDepth(2),
            random);
        string captured = Name("traitCaptured", random);
        string argument = Name("traitArgument", random);
        Expr selected = CallTwice(
            new Expr.Var(trait.ConstrainedFunction),
            new Expr.Var(captured),
            new Expr.Var(argument));
        Expr closure = new Expr.Lambda(argument, selected)
        {
            ParamAnnotation = resultType.ToSyntax(),
        };
        Expr value = new Expr.Let(
            captured,
            left.Value,
            new Expr.Call(closure, right.Value));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(left.Features);
        features.UnionWith(right.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge(
                "trait:constrained-closure:" + trait.Name,
                left.Trace,
                right.Trace),
            left.NodeCount + right.NodeCount + 7);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Expr CallTwice(Expr function, Expr first, Expr second) =>
        new Expr.Call(new Expr.Call(function, first), second);
}

internal sealed class TraitDerivedOperatorSharingTemplate : ICombinationTemplate
{
    public string Id => "trait.derived-operator-sharing";

    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } =
        new SortedSet<GeneratedFeature>
        {
            GeneratedFeature.Adt,
            GeneratedFeature.Comparison,
            GeneratedFeature.DerivedImplementation,
            GeneratedFeature.If,
            GeneratedFeature.Let,
            GeneratedFeature.SharedValue,
            GeneratedFeature.TraitOperator,
            GeneratedFeature.TraitResolution,
        };

    public bool CanApply(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget) =>
        resultType == AshesType.Bool
        && budget.RemainingNodes >= 10
        && context.Traits.Any(trait => trait.DerivedBoxType is not null);

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GeneratedTrait trait = context.Traits
            .Where(candidate => candidate.DerivedBoxType is not null)
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .First();
        AshesType payloadType = trait.ImplementedTypes[random.Next(trait.ImplementedTypes.Count)];
        GenerationResult<Expr> payload = expressions.Generate(
            payloadType,
            context,
            budget.Descend(7).LimitDepth(2),
            random);
        string shared = Name("traitSharedBox", random);
        Expr box = new Expr.Call(new Expr.Var(trait.DerivedBoxConstructor!), payload.Value);
        Expr equality = new Expr.Equal(new Expr.Var(shared), new Expr.Var(shared));
        Expr value = new Expr.Let(
            shared,
            box,
            new Expr.If(equality, new Expr.BoolLit(true), new Expr.BoolLit(false)));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(payload.Features);
        return new GenerationResult<Expr>(
            value,
            AshesType.Bool,
            features,
            GenerationTrace.Merge("trait:derived-operator-sharing", payload.Trace),
            payload.NodeCount + 8);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
