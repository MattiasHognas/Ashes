using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class CapabilityHandlerTemplate : ICombinationTemplate
{
    public string Id => "capability.deterministic-handler";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Capability, GeneratedFeature.Handler };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 8;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GeneratedCapability capability = new(
            "FuzzCapability",
            [new GeneratedCapabilityOperation("get", AshesType.Unit, resultType)]);
        GenerationContext handlerContext = context.WithCapability(capability)
            .WithActiveHandler(capability.Name)
            .WithFeature(GeneratedFeature.Capability)
            .WithFeature(GeneratedFeature.Handler);
        GenerationResult<Expr> supplied = expressions.Generate(resultType, handlerContext, budget.Descend(5), random);
        Expr operation = new Expr.Perform(new Expr.Call(new Expr.QualifiedVar("FuzzCapability", "get"), new Expr.Var("Unit")));
        Expr resume = new Expr.Call(new Expr.Var("resume"), supplied.Value);
        string returned = "handled" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        HandlerArm operationArm = new("FuzzCapability", "get", [new Pattern.Wildcard()], resume);
        HandlerArm returnArm = new(null, "return", [new Pattern.Var(returned)], new Expr.Var(returned));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(supplied.Features);
        return new GenerationResult<Expr>(new Expr.Handle(operation, [operationArm, returnArm]), resultType, features, GenerationTrace.Merge("capability:handler", supplied.Trace), supplied.NodeCount + 7);
    }
}

internal sealed class AsyncCaptureTemplate : ICombinationTemplate
{
    public string Id => "async.capture-across-await";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature> { GeneratedFeature.Await, GeneratedFeature.Match, GeneratedFeature.SharedValue };
    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.SuspensionAllowed) && budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        string captured = "awaitedCapture" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> value = expressions.Generate(resultType, context, budget.Descend(9), random);
        Expr completedTask = new Expr.Call(new Expr.QualifiedVar("Ashes.Task", "task"), new Expr.Var(captured));
        Expr awaited = new Expr.Await(completedTask);
        Expr awaitMatch = new Expr.Match(awaited,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var("awaitedValue")]), new Expr.Var("awaitedValue")),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(captured)),
        ]);
        Expr task = new Expr.Call(new Expr.Var("async"), awaitMatch);
        Expr run = new Expr.Call(new Expr.QualifiedVar("Ashes.Task", "run"), task);
        Expr runMatch = new Expr.Match(run,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var("taskValue")]), new Expr.Var("taskValue")),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(captured)),
        ]);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(value.Features);
        return new GenerationResult<Expr>(new Expr.Let(captured, value.Value, runMatch), resultType, features, GenerationTrace.Merge("async:capture", value.Trace), value.NodeCount + 14);
    }
}

internal sealed class AsyncSpawnSharedValueTemplate : ICombinationTemplate
{
    public string Id => "async.spawn-shared-value";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.Variable,
        GeneratedFeature.Spawn,
        GeneratedFeature.SharedValue,
        GeneratedFeature.ResultAliasesInput,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.SuspensionAllowed) && budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> shared = expressions.Generate(resultType, context, budget.Descend(6), random);
        string sharedName = Name("spawnShared", random);
        string spawnName = Name("spawnHandle", random);
        Expr completedTask = new Expr.Call(new Expr.Var("async"), new Expr.Var(sharedName));
        Expr spawn = new Expr.Call(new Expr.QualifiedVar("Ashes.Task", "spawn"), completedTask);
        Expr value = new Expr.Let(
            sharedName,
            shared.Value,
            new Expr.Let(spawnName, spawn, new Expr.Var(sharedName)));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(shared.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge($"async:spawn-shared:{resultType}", shared.Trace),
            shared.NodeCount + 8);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
