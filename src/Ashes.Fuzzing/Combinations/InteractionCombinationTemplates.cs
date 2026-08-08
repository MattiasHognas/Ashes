using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class CapturedAdtMatchClosureTemplate : ICombinationTemplate
{
    public string Id => "closure.captured-adt-match";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Adt,
        GeneratedFeature.Call,
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.Lambda,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.IsInterestedIn(OwnershipInterest.ClosureCapture) && budget.RemainingNodes >= 12;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> payload = expressions.Generate(resultType, context, budget.Descend(7), random);
        string box = Name("capturedAdt", random);
        string item = Name("capturedAdtItem", random);
        string parameter = Name("capturedAdtArgument", random);
        string closure = Name("capturedAdtClosure", random);
        Expr body = new Expr.Match(
            new Expr.Var(box),
            [new MatchCase(new Pattern.Constructor("FuzzBox", [new Pattern.Var(item)]), new Expr.Var(item))]);
        Expr lambda = new Expr.Lambda(parameter, body) { ParamAnnotation = AshesType.Unit.ToSyntax() };
        Expr value = new Expr.Let(
            box,
            new Expr.Call(new Expr.Var("FuzzBox"), payload.Value),
            new Expr.Let(closure, lambda, new Expr.Call(new Expr.Var(closure), new Expr.Var("Unit"))));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(payload.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("closure:captured-adt-match", payload.Trace),
            payload.NodeCount + 11);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ClosureInMatchBranchTemplate : ICombinationTemplate
{
    public string Id => "match.closure-branch";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Call,
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.ClosureInMatch,
        GeneratedFeature.Lambda,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
        GeneratedFeature.SharedValue,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.IsInterestedIn(OwnershipInterest.ClosureCapture) && budget.RemainingNodes >= 12;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> capturedValue = expressions.Generate(resultType, context, budget.Descend(7), random);
        GenerationResult<Expr> scrutinee = expressions.Generate(AshesType.Bool, context, budget.Descend(7), random);
        string captured = Name("branchClosureCapture", random);
        string parameter = Name("branchClosureArgument", random);
        Expr closureCall = new Expr.Call(
            new Expr.Lambda(parameter, new Expr.Var(captured)) { ParamAnnotation = AshesType.Unit.ToSyntax() },
            new Expr.Var("Unit"));
        Expr match = new Expr.Match(scrutinee.Value,
        [
            new MatchCase(new Pattern.BoolLit(true), closureCall),
            new MatchCase(new Pattern.BoolLit(false), new Expr.Var(captured)),
        ]);
        Expr value = new Expr.Let(captured, capturedValue.Value, match);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(capturedValue.Features);
        features.UnionWith(scrutinee.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("match:closure-branch", capturedValue.Trace, scrutinee.Trace),
            capturedValue.NodeCount + scrutinee.NodeCount + 9);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class RecursiveListReconstructionTemplate : ICombinationTemplate
{
    public string Id => "recursion.list-reconstruction";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.List,
        GeneratedFeature.Match,
        GeneratedFeature.RecursiveFunction,
        GeneratedFeature.RecursiveReconstruction,
        GeneratedFeature.ReuseCandidate,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.RecursionAllowed) &&
        resultType is AshesType.List &&
        budget.RemainingRecursion > 0 &&
        budget.RemainingNodes >= 16;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.List listType = (AshesType.List)resultType;
        GenerationBudget childBudget = budget.Descend(9).LimitNodes(3).LimitDepth(1) with
        {
            RemainingRecursion = budget.RemainingRecursion - 1,
        };
        GenerationResult<Expr> first = expressions.Generate(listType.Element, context, childBudget, random);
        GenerationResult<Expr> second = expressions.Generate(listType.Element, context, childBudget, random);
        string loop = Name("reconstructList", random);
        string items = Name("reconstructItems", random);
        string head = Name("reconstructHead", random);
        string tail = Name("reconstructTail", random);
        Expr body = new Expr.Match(new Expr.Var(items),
        [
            new MatchCase(new Pattern.EmptyList(), new Expr.ListLit([])),
            new MatchCase(
                new Pattern.Cons(new Pattern.Var(head), new Pattern.Var(tail)),
                new Expr.Cons(new Expr.Var(head), new Expr.Call(new Expr.Var(loop), new Expr.Var(tail)))),
        ]);
        Expr lambda = new Expr.Lambda(items, body) { ParamAnnotation = listType.ToSyntax() };
        Expr input = new Expr.ListLit([first.Value, second.Value]);
        Expr value = new Expr.LetRecursive(loop, lambda, new Expr.Call(new Expr.Var(loop), input))
        {
            TypeAnnotation = new AshesType.Function(listType, listType).ToSyntax(),
        };
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(first.Features);
        features.UnionWith(second.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("recursion:list-reconstruction", first.Trace, second.Trace),
            first.NodeCount + second.NodeCount + 15);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ResultClosurePipeTemplate : ICombinationTemplate
{
    public string Id => "result.closure-pipe";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Call,
        GeneratedFeature.Lambda,
        GeneratedFeature.ResultClosure,
        GeneratedFeature.ResultShortCircuit,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType is AshesType.Result && budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Result result = (AshesType.Result)resultType;
        GenerationResult<Expr> input = expressions.Generate(resultType, context, budget.Descend(5), random);
        string parameter = Name("resultClosureValue", random);
        GenerationContext bodyContext = context.WithBinding(new GeneratedBinding(parameter, result.Value))
            .WithFlag(GenerationFlags.TailPosition)
            .WithFeature(GeneratedFeature.ResultShortCircuit);
        GenerationResult<Expr> body = expressions.Generate(resultType, bodyContext, budget.Descend(5), random);
        Expr lambda = new Expr.Lambda(parameter, body.Value) { ParamAnnotation = result.Value.ToSyntax() };
        Expr value = new Expr.ResultPipe(input.Value, lambda);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(input.Features);
        features.UnionWith(body.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("result:closure-pipe", input.Trace, body.Trace),
            input.NodeCount + body.NodeCount + 3);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class CapabilityClosureMatchTemplate : ICombinationTemplate
{
    public string Id => "capability.closure-match";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Call,
        GeneratedFeature.Capability,
        GeneratedFeature.CapabilityInClosure,
        GeneratedFeature.Handler,
        GeneratedFeature.Lambda,
        GeneratedFeature.Let,
        GeneratedFeature.Match,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingNodes >= 17;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GeneratedCapability capability = new(
            "FuzzCapability",
            [new GeneratedCapabilityOperation("get", AshesType.Unit, resultType)]);
        GenerationContext handlerContext = context.WithCapability(capability)
            .WithActiveHandler(capability.Name)
            .WithFeature(GeneratedFeature.Capability)
            .WithFeature(GeneratedFeature.Handler);
        GenerationResult<Expr> supplied = expressions.Generate(resultType, handlerContext, budget.Descend(10), random);
        GenerationResult<Expr> condition = expressions.Generate(AshesType.Bool, context, budget.Descend(10), random);
        string suppliedName = Name("capabilityClosureResult", random);
        Expr operation = new Expr.Perform(new Expr.Call(new Expr.QualifiedVar("FuzzCapability", "get"), new Expr.Var("Unit")));
        string returned = Name("closureHandled", random);
        Expr handled = new Expr.Handle(operation,
        [
            new HandlerArm("FuzzCapability", "get", [new Pattern.Wildcard()], new Expr.Call(new Expr.Var("resume"), new Expr.Var(suppliedName))),
            new HandlerArm(null, "return", [new Pattern.Var(returned)], new Expr.Var(returned)),
        ]);
        string parameter = Name("capabilityClosureArgument", random);
        Expr closure = new Expr.Lambda(parameter, handled) { ParamAnnotation = AshesType.Unit.ToSyntax() };
        Expr call = new Expr.Call(closure, new Expr.Var("Unit"));
        Expr match = new Expr.Match(condition.Value,
        [
            new MatchCase(new Pattern.BoolLit(true), call),
            new MatchCase(new Pattern.BoolLit(false), new Expr.Var(suppliedName)),
        ]);
        Expr value = new Expr.Let(suppliedName, supplied.Value, match);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(supplied.Features);
        features.UnionWith(condition.Features);
        return new GenerationResult<Expr>(
            value,
            resultType,
            features,
            GenerationTrace.Merge("capability:closure-match", supplied.Trace, condition.Trace),
            supplied.NodeCount + condition.NodeCount + 16);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
