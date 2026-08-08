using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation.Expressions;

internal sealed class AdtGenerationRule : IExpressionGenerationRule
{
    public string Id => "adt";
    public int Weight => 4;

    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.Adt { Name: "FuzzTree", Arguments.Count: 1 };

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Adt tree = (AshesType.Adt)requiredType;
        if (budget.IsLeaf || random.Next(3) != 0)
        {
            GenerationResult<Expr> payload = expressions.Generate(tree.Arguments[0], context, budget.Descend(2), random);
            GeneratedFeatureSet leafFeatures = payload.Features.Copy();
            leafFeatures.Add(GeneratedFeature.Adt);
            return new GenerationResult<Expr>(
                new Expr.Call(new Expr.Var("FuzzLeaf"), payload.Value),
                requiredType,
                leafFeatures,
                GenerationTrace.Merge("adt:FuzzLeaf", payload.Trace),
                payload.NodeCount + 2);
        }

        GenerationResult<Expr> left = expressions.Generate(requiredType, context, budget.Descend(3), random);
        GenerationResult<Expr> right = expressions.Generate(requiredType, context, budget.Descend(3), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Adt, GeneratedFeature.ConstructorReconstruction]);
        features.UnionWith(left.Features);
        features.UnionWith(right.Features);
        Expr branch = new Expr.Call(new Expr.Call(new Expr.Var("FuzzBranch"), left.Value), right.Value);
        return new GenerationResult<Expr>(branch, requiredType, features, GenerationTrace.Merge("adt:FuzzBranch", left.Trace, right.Trace), left.NodeCount + right.NodeCount + 3);
    }
}

internal sealed class TaskGenerationRule : IExpressionGenerationRule
{
    public string Id => "task";
    public int Weight => 3;

    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.Task { Error: AshesType.Primitive { Name: "Str" } };

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Task task = (AshesType.Task)requiredType;
        GenerationResult<Expr> payload = expressions.Generate(task.Value, context, budget.Descend(2), random);
        GeneratedFeatureSet features = payload.Features.Copy();
        features.Add(GeneratedFeature.Await);
        return new GenerationResult<Expr>(
            new Expr.Call(new Expr.Var("async"), payload.Value),
            requiredType,
            features,
            GenerationTrace.Merge("task:completed", payload.Trace),
            payload.NodeCount + 2);
    }
}

internal sealed class ConsGenerationRule : IExpressionGenerationRule
{
    public string Id => "cons";
    public int Weight => 3;

    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.List && budget.RemainingNodes >= 6;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.List list = (AshesType.List)requiredType;
        GenerationResult<Expr> head = expressions.Generate(list.Element, context, budget.Descend(3), random);
        GenerationResult<Expr> tail = expressions.Generate(requiredType, context, budget.Descend(3), random);
        string headName = Name("consHead", random);
        string tailName = Name("consTail", random);
        Expr value = new Expr.Let(
            headName,
            head.Value,
            new Expr.Let(tailName, tail.Value, new Expr.Cons(new Expr.Var(headName), new Expr.Var(tailName))));
        GeneratedFeatureSet features = new([GeneratedFeature.List, GeneratedFeature.Let, GeneratedFeature.Variable, GeneratedFeature.ConstructorReconstruction]);
        features.UnionWith(head.Features);
        features.UnionWith(tail.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("list:cons", head.Trace, tail.Trace), head.NodeCount + tail.NodeCount + 5);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ListMatchGenerationRule : IExpressionGenerationRule
{
    public string Id => "list-match";
    public int Weight => 2;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 8;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.List listType = new(requiredType);
        GenerationResult<Expr> head = expressions.Generate(requiredType, context, budget.Descend(4), random);
        GenerationResult<Expr> fallback = expressions.Generate(requiredType, context, budget.Descend(4), random);
        string listName = Name("matchedList", random);
        string headName = Name("matchedHead", random);
        Expr list = new Expr.ListLit([head.Value]);
        Expr match = new Expr.Match(new Expr.Var(listName),
        [
            new MatchCase(new Pattern.EmptyList(), fallback.Value),
            new MatchCase(new Pattern.Cons(new Pattern.Var(headName), new Pattern.Wildcard()), new Expr.Var(headName)),
        ]);
        Expr value = new Expr.Let(listName, list, match);
        GeneratedFeatureSet features = new([GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Let, GeneratedFeature.Variable, GeneratedFeature.SharedValue]);
        features.UnionWith(head.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge($"match:list:{listType}", head.Trace, fallback.Trace), head.NodeCount + fallback.NodeCount + 7);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class TupleMatchGenerationRule : IExpressionGenerationRule
{
    public string Id => "tuple-match";
    public int Weight => 2;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 8;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> selected = expressions.Generate(requiredType, context, budget.Descend(4), random);
        GenerationResult<Expr> auxiliary = expressions.Generate(AshesType.Bool, context, budget.Descend(4), random);
        string tupleName = Name("matchedTuple", random);
        string selectedName = Name("selected", random);
        Expr tuple = new Expr.TupleLit([selected.Value, auxiliary.Value]);
        Expr match = new Expr.Match(
            new Expr.Var(tupleName),
            [new MatchCase(new Pattern.Tuple([new Pattern.Var(selectedName), new Pattern.Wildcard()]), new Expr.Var(selectedName))]);
        Expr value = new Expr.Let(tupleName, tuple, match);
        GeneratedFeatureSet features = new([GeneratedFeature.Match, GeneratedFeature.Tuple, GeneratedFeature.Let, GeneratedFeature.Variable, GeneratedFeature.SharedValue]);
        features.UnionWith(selected.Features);
        features.UnionWith(auxiliary.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("match:tuple", selected.Trace, auxiliary.Trace), selected.NodeCount + auxiliary.NodeCount + 7);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ResultMatchGenerationRule : IExpressionGenerationRule
{
    public string Id => "result-match";
    public int Weight => 2;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 9;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Result resultType = new(AshesType.Str, requiredType);
        GenerationResult<Expr> success = expressions.Generate(requiredType, context, budget.Descend(4), random);
        GenerationResult<Expr> fallback = expressions.Generate(requiredType, context, budget.Descend(4), random);
        string resultName = Name("matchedResult", random);
        string valueName = Name("successValue", random);
        Expr result = new Expr.Call(new Expr.Var("Ok"), success.Value);
        Expr match = new Expr.Match(new Expr.Var(resultName),
        [
            new MatchCase(new Pattern.Constructor("Ok", [new Pattern.Var(valueName)]), new Expr.Var(valueName)),
            new MatchCase(new Pattern.Constructor("Error", [new Pattern.Wildcard()]), fallback.Value),
        ]);
        Expr value = new Expr.Let(resultName, result, match);
        GeneratedFeatureSet features = new([GeneratedFeature.Match, GeneratedFeature.ResultShortCircuit, GeneratedFeature.Let, GeneratedFeature.Variable]);
        features.UnionWith(success.Features);
        features.UnionWith(fallback.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge($"match:{resultType}", success.Trace, fallback.Trace), success.NodeCount + fallback.NodeCount + 8);
    }

    private static string Name(string prefix, FuzzRandom random) =>
        prefix + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ResultPipeGenerationRule : IExpressionGenerationRule
{
    public string Id => "result-pipe";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Result && budget.RemainingNodes >= 9;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Result result = (AshesType.Result)requiredType;
        AshesType inputType = LetGenerationRule.ChooseType(result.Value, random);
        AshesType.Result inputResultType = new(result.Error, inputType);
        GenerationResult<Expr> input = expressions.Generate(inputResultType, context, budget.Descend(4), random);
        string parameter = "pipeValue" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationContext bodyContext = context.WithBinding(new GeneratedBinding(parameter, inputType));
        GenerationResult<Expr> body = expressions.Generate(requiredType, bodyContext, budget.Descend(4), random);
        Expr function = new Expr.Lambda(parameter, body.Value) { ParamAnnotation = inputType.ToSyntax() };
        Expr value = new Expr.ResultPipe(input.Value, function);
        GeneratedFeatureSet features = new([GeneratedFeature.ResultShortCircuit, GeneratedFeature.Lambda, GeneratedFeature.Call]);
        features.UnionWith(input.Features);
        features.UnionWith(body.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge($"result:pipe:{inputType}", input.Trace, body.Trace), input.NodeCount + body.NodeCount + 3);
    }
}

internal sealed class RecordUpdateGenerationRule : IExpressionGenerationRule
{
    public string Id => "record-update";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Record { Name: "FuzzRecord" } && budget.RemainingNodes >= 7;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> original = expressions.Generate(requiredType, context, budget.Descend(3), random);
        bool updateFirst = random.NextBool();
        AshesType fieldType = updateFirst ? AshesType.Int : AshesType.Bool;
        GenerationResult<Expr> replacement = expressions.Generate(fieldType, context, budget.Descend(3), random);
        string recordName = "updatedRecord" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr update = new Expr.RecordUpdate(new Expr.Var(recordName), [(updateFirst ? "first" : "second", replacement.Value)]);
        Expr value = new Expr.Let(recordName, original.Value, update);
        GeneratedFeatureSet features = new([GeneratedFeature.Record, GeneratedFeature.Let, GeneratedFeature.Variable, GeneratedFeature.ReuseCandidate, GeneratedFeature.ConstructorReconstruction]);
        features.UnionWith(original.Features);
        features.UnionWith(replacement.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("record:update", original.Trace, replacement.Trace), original.NodeCount + replacement.NodeCount + 4);
    }
}

internal sealed class BitwiseGenerationRule : IExpressionGenerationRule
{
    public string Id => "bitwise";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.UInt && budget.RemainingNodes >= 5;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.UInt unsigned = (AshesType.UInt)requiredType;
        GenerationResult<Expr> left = expressions.Generate(requiredType, context, budget.Descend(3), random);
        GenerationResult<Expr> right = random.Next(5) >= 3
            ? ExpressionGenerator.Result(new Expr.UIntLit((ulong)random.Next(unsigned.Bits), unsigned.Bits), requiredType, GeneratedFeature.Literal, "literal:shift", 1)
            : expressions.Generate(requiredType, context, budget.Descend(3), random);
        string leftName = "bitLeft" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rightName = "bitRight" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr operation = random.Next(5) switch
        {
            0 => new Expr.BitwiseAnd(new Expr.Var(leftName), new Expr.Var(rightName)),
            1 => new Expr.BitwiseOr(new Expr.Var(leftName), new Expr.Var(rightName)),
            2 => new Expr.BitwiseXor(new Expr.Var(leftName), new Expr.Var(rightName)),
            3 => new Expr.ShiftLeft(new Expr.Var(leftName), new Expr.Var(rightName)),
            _ => new Expr.ShiftRight(new Expr.Var(leftName), new Expr.Var(rightName)),
        };
        Expr value = new Expr.Let(leftName, left.Value, new Expr.Let(rightName, right.Value, operation));
        GeneratedFeatureSet features = new([GeneratedFeature.Arithmetic, GeneratedFeature.Let, GeneratedFeature.Variable]);
        features.UnionWith(left.Features);
        features.UnionWith(right.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("bitwise", left.Trace, right.Trace), left.NodeCount + right.NodeCount + 5);
    }
}
