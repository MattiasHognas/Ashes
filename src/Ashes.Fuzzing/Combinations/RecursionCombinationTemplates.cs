using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class BoundedRecursionTemplate : ICombinationTemplate
{
    public string Id => "recursion.bounded-capture";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.If,
        GeneratedFeature.Let,
        GeneratedFeature.RecursionWithSharing,
        GeneratedFeature.RecursiveFunction,
        GeneratedFeature.SharedValue,
        GeneratedFeature.TailCall,
        GeneratedFeature.Variable,
    };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.RecursionAllowed) && budget.RemainingRecursion > 0 && budget.RemainingNodes >= 14;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        string captured = "recursiveResult" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string loop = "loop" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string count = "count" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string completed = "recursiveCompleted" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> result = expressions.Generate(resultType, context, budget.Descend(10) with { RemainingRecursion = budget.RemainingRecursion - 1 }, random);
        Expr condition = new Expr.LessOrEqual(new Expr.Var(count), new Expr.IntLit(0));
        Expr recurse = new Expr.Call(new Expr.Var(loop), new Expr.Subtract(new Expr.Var(count), new Expr.IntLit(1)));
        Expr lambda = new Expr.Lambda(count, new Expr.If(condition, new Expr.Var(captured), recurse)) { ParamAnnotation = AshesType.Int.ToSyntax() };
        Expr chooseAlias = new Expr.Let(
            completed,
            new Expr.Call(new Expr.Var(loop), new Expr.IntLit(2)),
            new Expr.If(new Expr.BoolLit(random.NextBool()), new Expr.Var(completed), new Expr.Var(captured)));
        Expr recursive = new Expr.LetRecursive(loop, lambda, chooseAlias) { TypeAnnotation = new AshesType.Function(AshesType.Int, resultType).ToSyntax() };
        Expr value = new Expr.Let(captured, result.Value, recursive);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(result.Features);
        if (resultType is AshesType.Result)
        {
            features.Add(GeneratedFeature.RecursiveResult);
        }
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("recursion:bounded-shared", result.Trace), result.NodeCount + 14);
    }
}
