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

internal sealed class AsyncClosureMatchAcrossAwaitTemplate : ICombinationTemplate
{
    public string Id => "async.closure-match-across-await";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Await,
        GeneratedFeature.Call,
        GeneratedFeature.ClosureAcrossAwait,
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.Lambda,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
        GeneratedFeature.MatchAcrossAwait,
        GeneratedFeature.SharedValue,
        GeneratedFeature.Variable,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.SuspensionAllowed) && budget.RemainingNodes >= 24;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> capturedValue = expressions.Generate(
            resultType,
            context.WithoutFlag(GenerationFlags.TailPosition),
            budget.Descend(20),
            random);
        string captured = Name("awaitClosureCapture", random);
        string closure = Name("awaitClosure", random);
        string parameter = Name("awaitClosureArgument", random);
        string completed = Name("awaitClosureCompleted", random);
        string taskValue = Name("awaitClosureTaskValue", random);

        Expr beforeAwait = new Expr.Match(new Expr.BoolLit(random.NextBool()),
        [
            new MatchCase(new Pattern.BoolLit(true), new Expr.Var("Unit")),
            new MatchCase(new Pattern.BoolLit(false), new Expr.Var("Unit")),
        ]);
        Expr awaited = new Expr.Await(
            new Expr.Call(new Expr.QualifiedVar("Ashes.Task", "task"), beforeAwait));
        Expr afterAwait = new Expr.Match(awaited,
        [
            new MatchCase(
                new Pattern.Constructor("Ok", [new Pattern.Var(completed)]),
                new Expr.Call(new Expr.Var(closure), new Expr.Var(completed))),
            new MatchCase(
                new Pattern.Constructor("Error", [new Pattern.Wildcard()]),
                new Expr.Var(captured)),
        ]);
        Expr run = new Expr.Call(
            new Expr.QualifiedVar("Ashes.Task", "run"),
            new Expr.Call(new Expr.Var("async"), afterAwait));
        Expr observe = new Expr.Match(run,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var(taskValue)]), new Expr.Var(taskValue)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(captured)),
        ]);
        Expr value = new Expr.Let(
            captured,
            capturedValue.Value,
            new Expr.Let(closure, new Expr.Lambda(parameter, new Expr.Var(captured)), observe));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(capturedValue.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge($"async:closure-match-across-await:{resultType}", capturedValue.Trace),
            capturedValue.NodeCount + 20);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class AsyncTaskResultReuseTemplate : ICombinationTemplate
{
    public string Id => "async.task-result-reuse";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Await,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.Match,
        GeneratedFeature.Record,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.RuntimeUniquenessCheck,
        GeneratedFeature.TaskResultReuse,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.SuspensionAllowed) &&
        context.IsInterestedIn(OwnershipInterest.Reuse) &&
        resultType is AshesType.Record { Name: "FuzzRecord" } &&
        budget.RemainingNodes >= 16;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> taskValue = expressions.Generate(
            resultType,
            context.WithoutFlag(GenerationFlags.TailPosition),
            budget.Descend(8),
            random);
        GenerationResult<Expr> replacement = expressions.Generate(
            AshesType.Int,
            context.WithoutFlag(GenerationFlags.TailPosition),
            budget.Descend(8),
            random);
        GenerationResult<Expr> fallback = expressions.Generate(
            resultType,
            context.WithoutFlag(GenerationFlags.TailPosition),
            budget.Descend(8),
            random);
        string received = Name("taskReuseValue", random);
        Expr task = new Expr.Call(new Expr.Var("async"), taskValue.Value);
        Expr run = new Expr.Call(new Expr.QualifiedVar("Ashes.Task", "run"), task);
        Expr update = new Expr.RecordUpdate(new Expr.Var(received), [("first", replacement.Value)]);
        Expr value = new Expr.Match(run,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var(received)]), update),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), fallback.Value),
        ]);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(taskValue.Features);
        features.UnionWith(replacement.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("async:task-result-reuse", taskValue.Trace, replacement.Trace, fallback.Trace),
            taskValue.NodeCount + replacement.NodeCount + fallback.NodeCount + 8);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class CapabilityResultTemplate : ICombinationTemplate
{
    public string Id => "capability.result-operation";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Capability,
        GeneratedFeature.Handler,
        GeneratedFeature.Match,
        GeneratedFeature.ResultShortCircuit,
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
        AshesType.Result operationType = new(AshesType.Str, resultType);
        GeneratedCapability capability = Capability(operationType);
        GenerationContext handlerContext = HandlerContext(context, capability);
        GenerationResult<Expr> supplied = expressions.Generate(operationType, handlerContext, budget.Descend(9), random);
        GenerationResult<Expr> fallback = expressions.Generate(resultType, handlerContext, budget.Descend(9), random);
        string success = Name("capabilitySuccess", random);
        Expr operation = PerformGet();
        Expr body = new Expr.Match(operation,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var(success)]), new Expr.Var(success)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), fallback.Value),
        ]);
        Expr handled = Handle(body, supplied.Value, Name("capabilityResult", random));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(supplied.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(
            handled,
            resultType,
            features,
            GenerationTrace.Merge($"capability:result:{resultType}", supplied.Trace, fallback.Trace),
            supplied.NodeCount + fallback.NodeCount + 10);
    }

    internal static GeneratedCapability Capability(AshesType resultType) => new(
        "FuzzCapability",
        [new GeneratedCapabilityOperation("get", AshesType.Unit, resultType)]);

    internal static GenerationContext HandlerContext(GenerationContext context, GeneratedCapability capability) =>
        context.WithCapability(capability)
            .WithActiveHandler(capability.Name)
            .WithFeature(GeneratedFeature.Capability)
            .WithFeature(GeneratedFeature.Handler);

    internal static Expr PerformGet() =>
        new Expr.Perform(new Expr.Call(new Expr.QualifiedVar("FuzzCapability", "get"), new Expr.Var("Unit")));

    internal static Expr Handle(Expr body, Expr supplied, string returned)
    {
        HandlerArm operationArm = new(
            "FuzzCapability",
            "get",
            [new Pattern.Wildcard()],
            new Expr.Call(new Expr.Var("resume"), supplied));
        HandlerArm returnArm = new(null, "return", [new Pattern.Var(returned)], new Expr.Var(returned));
        return new Expr.Handle(body, [operationArm, returnArm]);
    }

    internal static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class CapabilityRecursiveListTemplate : ICombinationTemplate
{
    public string Id => "capability.recursive-list";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Capability,
        GeneratedFeature.Handler,
        GeneratedFeature.Match,
        GeneratedFeature.List,
        GeneratedFeature.RecursiveFunction,
        GeneratedFeature.TailCall,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.RecursionAllowed) && budget.RemainingNodes >= 22;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GeneratedCapability capability = CapabilityResultTemplate.Capability(resultType);
        GenerationContext handlerContext = CapabilityResultTemplate.HandlerContext(context, capability);
        GenerationResult<Expr> supplied = expressions.Generate(resultType, handlerContext, budget.Descend(12), random);
        string function = CapabilityResultTemplate.Name("capabilityWalk", random);
        string items = CapabilityResultTemplate.Name("capabilityItems", random);
        string tail = CapabilityResultTemplate.Name("capabilityTail", random);
        AshesType.List listType = new(AshesType.Int);
        Expr recursiveCall = new Expr.Call(new Expr.Var(function), new Expr.Var(tail));
        Expr match = new Expr.Match(new Expr.Var(items),
        [
            new MatchCase(new Pattern.EmptyList(), CapabilityResultTemplate.PerformGet()),
            new MatchCase(new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Var(tail)), recursiveCall),
        ]);
        Expr lambda = new Expr.Lambda(items, match) { ParamAnnotation = listType.ToSyntax() };
        Expr call = new Expr.Call(new Expr.Var(function), new Expr.ListLit([new Expr.IntLit(1), new Expr.IntLit(2)]));
        Expr handled = CapabilityResultTemplate.Handle(call, supplied.Value, CapabilityResultTemplate.Name("capabilityWalkResult", random));
        Expr value = new Expr.LetRecursive(function, lambda, handled);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(supplied.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge($"capability:recursive-list:{resultType}", supplied.Trace),
            supplied.NodeCount + 20);
    }
}
