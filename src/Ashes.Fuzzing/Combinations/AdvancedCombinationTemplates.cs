using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class EscapingClosureTemplate : ICombinationTemplate
{
    public string Id => "closure.escape-captured-value";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.Lambda,
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.EscapingClosure,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType is AshesType.Function && budget.RemainingNodes >= 6;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Function function = (AshesType.Function)resultType;
        GenerationResult<Expr> capture = expressions.Generate(function.Return, context, budget.Descend(4), random);
        string captured = Name("escapingCapture", random);
        string parameter = Name("escapingParameter", random);
        Expr lambda = new Expr.Lambda(parameter, new Expr.Var(captured)) { ParamAnnotation = function.Parameter.ToSyntax() };
        Expr value = new Expr.Let(captured, capture.Value, lambda);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(capture.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge($"closure:escape:{function.Return}", capture.Trace), capture.NodeCount + 3);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class MultipleClosuresTemplate : ICombinationTemplate
{
    public string Id => "closure.multiple-shared-capture";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.Tuple,
        GeneratedFeature.Lambda,
        GeneratedFeature.ClosureCapture,
        GeneratedFeature.MultipleClosures,
        GeneratedFeature.SharedValue,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType is AshesType.Tuple { Elements.Count: 2 } tuple &&
        tuple.Elements[0] is AshesType.Function &&
        tuple.Elements[0] == tuple.Elements[1] &&
        budget.RemainingNodes >= 9;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Tuple tuple = (AshesType.Tuple)resultType;
        AshesType.Function function = (AshesType.Function)tuple.Elements[0];
        GenerationResult<Expr> capture = expressions.Generate(function.Return, context, budget.Descend(5), random);
        string captured = Name("sharedClosureCapture", random);
        Expr first = new Expr.Lambda(Name("firstParameter", random), new Expr.Var(captured)) { ParamAnnotation = function.Parameter.ToSyntax() };
        Expr second = new Expr.Lambda(Name("secondParameter", random), new Expr.Var(captured)) { ParamAnnotation = function.Parameter.ToSyntax() };
        Expr value = new Expr.Let(captured, capture.Value, new Expr.TupleLit([first, second]));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(capture.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge($"closure:multiple:{function.Return}", capture.Trace), capture.NodeCount + 6);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ListHeadSharingTemplate : ICombinationTemplate
{
    public string Id => "sharing.list-head-and-structure";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.List,
        GeneratedFeature.Tuple,
        GeneratedFeature.SharedValue,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.FreshResultInternalSharing,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType is AshesType.Tuple { Elements.Count: 2 } tuple &&
        tuple.Elements[1] is AshesType.List list &&
        tuple.Elements[0] == list.Element &&
        budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Tuple tuple = (AshesType.Tuple)resultType;
        AshesType.List listType = (AshesType.List)tuple.Elements[1];
        GenerationResult<Expr> head = expressions.Generate(listType.Element, context, budget.Descend(5), random);
        GenerationResult<Expr> tail = expressions.Generate(listType, context, budget.Descend(5), random);
        string headName = Name("sharedHead", random);
        string tailName = Name("sharedTail", random);
        string listName = Name("sharedList", random);
        Expr value = new Expr.Let(
            headName,
            head.Value,
            new Expr.Let(
                tailName,
                tail.Value,
                new Expr.Let(
                    listName,
                    new Expr.Cons(new Expr.Var(headName), new Expr.Var(tailName)),
                    new Expr.TupleLit([new Expr.Var(headName), new Expr.Var(listName)]))));
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(head.Features);
        features.UnionWith(tail.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge($"sharing:list-head:{listType.Element}", head.Trace, tail.Trace), head.NodeCount + tail.NodeCount + 9);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ListReconstructionTemplate : ICombinationTemplate
{
    public string Id => "perceus.list-reconstruction";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.List,
        GeneratedFeature.Match,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.LayoutCompatibleReuse,
        GeneratedFeature.RuntimeUniquenessCheck,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => resultType is AshesType.List && budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> list = expressions.Generate(resultType, context, budget.Descend(6), random);
        string listName = Name("reuseList", random);
        string headName = Name("reuseHead", random);
        string tailName = Name("reuseTail", random);
        Expr match = new Expr.Match(new Expr.Var(listName),
        [
            new MatchCase(new Pattern.EmptyList(), new Expr.ListLit([])),
            new MatchCase(
                new Pattern.Cons(new Pattern.Var(headName), new Pattern.Var(tailName)),
                new Expr.Cons(new Expr.Var(headName), new Expr.Var(tailName))),
        ]);
        Expr value = new Expr.Let(listName, list.Value, match);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(list.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("perceus:list-reconstruct", list.Trace), list.NodeCount + 8);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class TreeLayoutFallbackTemplate : ICombinationTemplate
{
    public string Id => "perceus.tree-layout-fallback";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.Adt,
        GeneratedFeature.Match,
        GeneratedFeature.ConstructorReconstruction,
        GeneratedFeature.ReuseCandidate,
        GeneratedFeature.LayoutCompatibleReuse,
        GeneratedFeature.LayoutIncompatibleFallback,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => resultType is AshesType.Adt { Name: "FuzzTree" } && budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> tree = expressions.Generate(resultType, context, budget.Descend(8), random);
        string treeName = Name("reuseTree", random);
        string payloadName = Name("treePayload", random);
        string leftName = Name("treeLeft", random);
        string rightName = Name("treeRight", random);
        Expr leaf = new Expr.Call(new Expr.Var("FuzzLeaf"), new Expr.Var(payloadName));
        Expr incompatible = new Expr.Call(new Expr.Call(new Expr.Var("FuzzBranch"), leaf), new Expr.Var("FuzzEmpty"));
        Expr compatible = new Expr.Call(new Expr.Call(new Expr.Var("FuzzBranch"), new Expr.Var(leftName)), new Expr.Var(rightName));
        Expr match = new Expr.Match(new Expr.Var(treeName),
        [
            new MatchCase(new Pattern.Constructor("FuzzEmpty", []), new Expr.Var("FuzzEmpty")),
            new MatchCase(new Pattern.Constructor("FuzzLeaf", [new Pattern.Var(payloadName)]), incompatible),
            new MatchCase(new Pattern.Constructor("FuzzBranch", [new Pattern.Var(leftName), new Pattern.Var(rightName)]), compatible),
        ]);
        Expr value = new Expr.Let(treeName, tree.Value, match);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(tree.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("perceus:tree-layout-fallback", tree.Trace), tree.NodeCount + 13);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class GuardedMatchTemplate : ICombinationTemplate
{
    public string Id => "match.guarded-generic";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Match,
        GeneratedFeature.GuardedMatch,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 10;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> scrutinee = expressions.Generate(AshesType.Bool, context, budget.Descend(5), random);
        GenerationResult<Expr> guard = expressions.Generate(AshesType.Bool, context, budget.Descend(5), random);
        GenerationResult<Expr> guarded = expressions.Generate(resultType, context, budget.Descend(5), random);
        GenerationResult<Expr> fallback = expressions.Generate(resultType, context, budget.Descend(5), random);
        Expr match = new Expr.Match(scrutinee.Value,
        [
            new MatchCase(new Pattern.BoolLit(true), guarded.Value, guard.Value),
            new MatchCase(new Pattern.Wildcard(), fallback.Value),
        ]);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(scrutinee.Features);
        features.UnionWith(guard.Features);
        features.UnionWith(guarded.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(match, resultType, features, GenerationTrace.Merge("match:guarded", scrutinee.Trace, guard.Trace, guarded.Trace, fallback.Trace), scrutinee.NodeCount + guard.NodeCount + guarded.NodeCount + fallback.NodeCount + 3);
    }
}

internal sealed class NestedCapabilityHandlersTemplate : ICombinationTemplate
{
    public string Id => "capability.nested-handlers";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Capability,
        GeneratedFeature.Handler,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 16;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GeneratedCapability capability = new(
            "FuzzCapability",
            [new GeneratedCapabilityOperation("get", AshesType.Unit, resultType)]);
        GenerationContext handlerContext = context.WithCapability(capability)
            .WithActiveHandler(capability.Name)
            .WithFeature(GeneratedFeature.Capability)
            .WithFeature(GeneratedFeature.Handler);
        GenerationResult<Expr> supplied = expressions.Generate(resultType, handlerContext, budget.Descend(9), random);
        Expr operation = new Expr.Perform(new Expr.Call(new Expr.QualifiedVar("FuzzCapability", "get"), new Expr.Var("Unit")));
        HandlerArm innerOperation = new("FuzzCapability", "get", [new Pattern.Wildcard()], new Expr.Call(new Expr.Var("resume"), supplied.Value));
        string innerReturnName = Name("innerHandled", random);
        HandlerArm innerReturn = new(null, "return", [new Pattern.Var(innerReturnName)], new Expr.Var(innerReturnName));
        Expr inner = new Expr.Handle(operation, [innerOperation, innerReturn]);

        HandlerArm outerOperation = new("FuzzCapability", "get", [new Pattern.Wildcard()], new Expr.Call(new Expr.Var("resume"), supplied.Value));
        string outerReturnName = Name("outerHandled", random);
        HandlerArm outerReturn = new(null, "return", [new Pattern.Var(outerReturnName)], new Expr.Var(outerReturnName));
        Expr outer = new Expr.Handle(inner, [outerOperation, outerReturn]);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(supplied.Features);
        return new GenerationResult<Expr>(outer, resultType, features, GenerationTrace.Merge("capability:nested", supplied.Trace), supplied.NodeCount + 15);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class DeterministicResourceTemplate : ICombinationTemplate
{
    public string Id => "resource.deterministic-file-handle";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Resource,
        GeneratedFeature.Match,
        GeneratedFeature.ResultShortCircuit,
        GeneratedFeature.SharedValue,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.ResourcesAllowed) &&
        context.ResourceTypes.Contains(AshesType.FileHandle) &&
        budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> fallback = expressions.Generate(resultType, context, budget.Descend(8), random);
        string resultName = Name("resourceResult", random);
        string handleName = Name("fileHandle", random);
        Expr open = new Expr.Call(new Expr.QualifiedVar("Ashes.IO.File", "open"), new Expr.StrLit("__ashes_fuzz_missing_file__"));
        Expr close = new Expr.Call(new Expr.QualifiedVar("Ashes.IO.File", "close"), new Expr.Var(handleName));
        Expr closeMatch = new Expr.Match(close,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
        ]);
        Expr openMatch = new Expr.Match(open,
        [
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var(handleName)]), closeMatch),
        ]);
        Expr value = new Expr.Let(resultName, fallback.Value, openMatch);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("resource:file-open-close", fallback.Trace), fallback.NodeCount + 13);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class DeterministicEnvironmentTemplate : ICombinationTemplate
{
    public string Id => "ambient.environment-missing";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.AmbientAuthority,
        GeneratedFeature.Match,
        GeneratedFeature.ResultShortCircuit,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        budget.RemainingNodes >= 12;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> fallback = expressions.Generate(resultType, context, budget.Descend(7), random);
        string resultName = Name("environmentFallback", random);
        Expr get = new Expr.Call(
            new Expr.QualifiedVar("Ashes.IO.Environment", "get"),
            new Expr.StrLit("ASHES_FUZZ_VARIABLE_THAT_MUST_NOT_EXIST_7A8EC7E8"));
        Expr matched = new Expr.Match(get,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Constructor("None", [])]), new Expr.Var(resultName)),
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Constructor("Some", [new Pattern.Wildcard()])]), new Expr.Var(resultName)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
        ]);
        Expr value = new Expr.Let(resultName, fallback.Value, matched);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("ambient:environment-missing", fallback.Trace), fallback.NodeCount + 11);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class DeterministicDirectoryTemplate : ICombinationTemplate
{
    public string Id => "resource.directory-operations";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.AmbientAuthority,
        GeneratedFeature.Match,
        GeneratedFeature.ResultShortCircuit,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        context.Allows(GenerationFlags.ResourcesAllowed) && budget.RemainingNodes >= 14;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> fallback = expressions.Generate(resultType, context, budget.Descend(8), random);
        string resultName = Name("directoryFallback", random);
        Expr remove = new Expr.Call(new Expr.QualifiedVar("Ashes.IO.Directory", "removeTree"), new Expr.StrLit("__ashes_fuzz_missing_directory__"));
        Expr matched = new Expr.Match(remove,
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), new Expr.Var(resultName)),
        ]);
        Expr value = new Expr.Let(resultName, fallback.Value, matched);
        GeneratedFeatureSet features = new(AdvertisedFeatures);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(value, resultType, features, GenerationTrace.Merge("resource:directory-operations", fallback.Trace), fallback.NodeCount + 10);
    }

    private static string Name(string prefix, FuzzRandom random) => prefix + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
