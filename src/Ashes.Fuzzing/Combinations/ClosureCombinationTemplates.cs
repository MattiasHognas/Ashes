using Ashes.Frontend;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Generation.Expressions;

namespace Ashes.Fuzzing.Combinations;

internal sealed class ClosureCaptureTemplate : ICombinationTemplate
{
    public string Id => "closure.capture-immediate-call";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Let, GeneratedFeature.Lambda, GeneratedFeature.Call, GeneratedFeature.ClosureCapture };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 7;
    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType captureType = LetGenerationRule.ChooseType(resultType, random);
        AshesType parameterType = LetGenerationRule.ChooseType(resultType, random);
        string captured = "captured" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string parameter = "parameter" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> capture = expressions.Generate(captureType, context, budget.Descend(5), random);
        GenerationContext bodyContext = context.WithBinding(new GeneratedBinding(captured, captureType)).WithBinding(new GeneratedBinding(parameter, parameterType));
        GenerationResult<Expr> body = expressions.Generate(resultType, bodyContext, budget.Descend(5), random);
        GenerationResult<Expr> argument = expressions.Generate(parameterType, context, budget.Descend(5), random);
        Expr lambda = new Expr.Lambda(parameter, body.Value) { ParamAnnotation = parameterType.ToSyntax() };
        string functionName = "capturingFunction" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr invoke = new Expr.Let(functionName, lambda, new Expr.Call(new Expr.Var(functionName), argument.Value));
        Expr result = new Expr.Let(captured, capture.Value, invoke);
        GeneratedFeatureSet features = new(AdvertisedFeatures); features.UnionWith(capture.Features); features.UnionWith(body.Features); features.UnionWith(argument.Features);
        return new GenerationResult<Expr>(result, resultType, features, GenerationTrace.Merge($"closure:{captureType}:{parameterType}", capture.Trace, body.Trace, argument.Trace), capture.NodeCount + body.NodeCount + argument.NodeCount + 5);
    }
}
