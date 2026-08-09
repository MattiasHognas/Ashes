using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation.Expressions;

internal sealed class AdtGenerationRule : IExpressionGenerationRule
{
    public string Id => "adt";
    public int Weight => 4;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Adt;

    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.Adt adt &&
        TryFindAdt(adt, context, out GeneratedAdt declaration) &&
        declaration.Constructors.Count > 0 &&
        budget.RemainingNodes >= 1;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Adt adt = (AshesType.Adt)requiredType;
        if (!TryFindAdt(adt, context, out GeneratedAdt declaration))
        {
            throw new InvalidOperationException($"ADT generation type '{adt.Name}' is not declared in the current context.");
        }
        (string Name, IReadOnlyList<AshesType> Fields)[] constructors = declaration.Constructors
            .Where(constructor => !budget.IsLeaf || !constructor.Fields.Any(field => ContainsAdt(field, adt.Name)))
            .OrderBy(constructor => constructor.Name, StringComparer.Ordinal)
            .ToArray();
        if (constructors.Length == 0)
        {
            constructors = declaration.Constructors.OrderBy(constructor => constructor.Name, StringComparer.Ordinal).ToArray();
        }
        (string Name, IReadOnlyList<AshesType> Fields) selected = constructors[random.Next(constructors.Length)];
        Expr value = new Expr.Var(selected.Name);
        GeneratedFeatureSet features = new([GeneratedFeature.Adt]);
        List<GenerationTrace> traces = [];
        int nodes = 1;
        foreach (AshesType fieldTemplate in selected.Fields)
        {
            AshesType fieldType = Substitute(fieldTemplate, adt.Arguments);
            GenerationResult<Expr> field = expressions.Generate(
                fieldType,
                context.WithoutFlag(GenerationFlags.TailPosition),
                budget.Descend(Math.Max(2, selected.Fields.Count + 1)),
                random);
            value = new Expr.Call(value, field.Value);
            features.UnionWith(field.Features);
            traces.Add(field.Trace);
            nodes += field.NodeCount + 1;
        }
        if (selected.Fields.Any(field => ContainsAdt(field, adt.Name)))
        {
            features.Add(GeneratedFeature.ConstructorReconstruction);
        }
        return new GenerationResult<Expr>(
            value,
            requiredType,
            features,
            GenerationTrace.Merge($"adt:{selected.Name}", traces.ToArray()),
            nodes);
    }

    internal static bool TryFindAdt(AshesType.Adt type, GenerationContext context, out GeneratedAdt declaration)
    {
        GeneratedAdt? candidate = context.Adts.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, type.Name, StringComparison.Ordinal) && candidate.Arity == type.Arguments.Count);
        if (candidate is not null)
        {
            declaration = candidate;
            return true;
        }
        if (string.Equals(type.Name, "FuzzTree", StringComparison.Ordinal) && type.Arguments.Count == 1)
        {
            AshesType.GenericParameter parameter = new(0);
            declaration = new GeneratedAdt("FuzzTree", 1, [("FuzzEmpty", []), ("FuzzLeaf", [parameter]), ("FuzzBranch", [new AshesType.Adt("FuzzTree", [parameter]), new AshesType.Adt("FuzzTree", [parameter])])]);
            return true;
        }
        if (string.Equals(type.Name, "FuzzMaybe", StringComparison.Ordinal) && type.Arguments.Count == 1)
        {
            declaration = new GeneratedAdt("FuzzMaybe", 1, [("FuzzNone", []), ("FuzzSome", [new AshesType.GenericParameter(0)])]);
            return true;
        }
        declaration = null!;
        return false;
    }

    internal static AshesType Substitute(AshesType type, IReadOnlyList<AshesType> arguments) => type switch
    {
        AshesType.GenericParameter parameter when parameter.Index >= 0 && parameter.Index < arguments.Count => arguments[parameter.Index],
        AshesType.GenericParameter parameter => throw new InvalidOperationException($"ADT schema references missing type parameter {parameter.Index}."),
        AshesType.Tuple tuple => new AshesType.Tuple(tuple.Elements.Select(element => Substitute(element, arguments)).ToArray()),
        AshesType.List list => new AshesType.List(Substitute(list.Element, arguments)),
        AshesType.Function function => new AshesType.Function(Substitute(function.Parameter, arguments), Substitute(function.Return, arguments)),
        AshesType.Adt adt => new AshesType.Adt(adt.Name, adt.Arguments.Select(argument => Substitute(argument, arguments)).ToArray()),
        AshesType.Result result => new AshesType.Result(Substitute(result.Error, arguments), Substitute(result.Value, arguments)),
        AshesType.Task task => new AshesType.Task(Substitute(task.Error, arguments), Substitute(task.Value, arguments)),
        _ => type,
    };

    internal static bool ContainsAdt(AshesType type, string name) => type switch
    {
        AshesType.Adt adt => string.Equals(adt.Name, name, StringComparison.Ordinal) || adt.Arguments.Any(argument => ContainsAdt(argument, name)),
        AshesType.Tuple tuple => tuple.Elements.Any(element => ContainsAdt(element, name)),
        AshesType.List list => ContainsAdt(list.Element, name),
        AshesType.Function function => ContainsAdt(function.Parameter, name) || ContainsAdt(function.Return, name),
        AshesType.Result result => ContainsAdt(result.Error, name) || ContainsAdt(result.Value, name),
        AshesType.Task task => ContainsAdt(task.Error, name) || ContainsAdt(task.Value, name),
        _ => false,
    };
}

internal sealed class TaskGenerationRule : IExpressionGenerationRule
{
    public string Id => "task";
    public int Weight => 3;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Task;

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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.List;

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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Generic;
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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Generic;
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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Generic;
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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Result;
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
        GenerationContext bodyContext = context.WithBinding(new GeneratedBinding(parameter, inputType))
            .WithFlag(GenerationFlags.TailPosition);
        GenerationResult<Expr> body = expressions.Generate(requiredType, bodyContext, budget.Descend(4), random);
        Expr function = new Expr.Lambda(parameter, body.Value) { ParamAnnotation = inputType.ToSyntax() };
        Expr value = new Expr.ResultPipe(input.Value, function);
        GeneratedFeatureSet features = new([GeneratedFeature.ResultShortCircuit, GeneratedFeature.Lambda, GeneratedFeature.Call]);
        features.UnionWith(input.Features);
        features.UnionWith(body.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge($"result:pipe:{inputType}", input.Trace, body.Trace), input.NodeCount + body.NodeCount + 3);
    }
}

internal sealed class ResultMapErrorGenerationRule : IExpressionGenerationRule
{
    public string Id => "result-map-error";
    public int Weight => 3;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Result;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.Result && budget.RemainingNodes >= 9;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Result output = (AshesType.Result)requiredType;
        AshesType inputError = output.Error == AshesType.Str ? AshesType.Int : AshesType.Str;
        AshesType.Result inputType = new(inputError, output.Value);
        GenerationResult<Expr> input = expressions.Generate(inputType, context, budget.Descend(4), random);
        string parameter = "mappedError" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationContext mapperContext = context.WithBinding(new GeneratedBinding(parameter, inputError))
            .WithFlag(GenerationFlags.TailPosition);
        GenerationResult<Expr> mapped = expressions.Generate(output.Error, mapperContext, budget.Descend(4), random);
        Expr mapper = new Expr.Lambda(parameter, mapped.Value) { ParamAnnotation = inputError.ToSyntax() };
        Expr value = new Expr.ResultMapErrorPipe(input.Value, mapper);
        GeneratedFeatureSet features = new([
            GeneratedFeature.ResultShortCircuit,
            GeneratedFeature.ResultErrorMapping,
            GeneratedFeature.Lambda,
            GeneratedFeature.Call,
        ]);
        features.UnionWith(input.Features);
        features.UnionWith(mapped.Features);
        return new GenerationResult<Expr>(
            value,
            requiredType,
            features,
            GenerationTrace.Merge($"result:map-error:{inputError}->{output.Error}", input.Trace, mapped.Trace),
            input.NodeCount + mapped.NodeCount + 3);
    }
}

internal sealed class ResultBindGenerationRule : IExpressionGenerationRule
{
    public string Id => "result-bind";
    public int Weight => 3;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Result;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType is AshesType.Result && budget.RemainingNodes >= 8;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        AshesType.Result output = (AshesType.Result)requiredType;
        AshesType boundType = output.Value == AshesType.Int ? AshesType.Str : AshesType.Int;
        AshesType.Result inputType = new(output.Error, boundType);
        GenerationResult<Expr> input = expressions.Generate(
            inputType,
            context.WithoutFlag(GenerationFlags.TailPosition),
            budget.Descend(3),
            random);
        string name = "boundResult" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationContext bodyContext = context.WithBinding(new GeneratedBinding(name, boundType));
        GenerationResult<Expr> body = expressions.Generate(requiredType, bodyContext, budget.Descend(3), random);
        GeneratedFeatureSet features = new([
            GeneratedFeature.ResultShortCircuit,
            GeneratedFeature.ResultBinding,
            GeneratedFeature.Let,
        ]);
        features.UnionWith(input.Features);
        features.UnionWith(body.Features);
        return new GenerationResult<Expr>(
            new Expr.LetResult(name, input.Value, body.Value),
            requiredType,
            features,
            GenerationTrace.Merge($"result:bind:{name}:{boundType}", input.Trace, body.Trace),
            input.NodeCount + body.NodeCount + 1);
    }
}

internal sealed class RecordUpdateGenerationRule : IExpressionGenerationRule
{
    public string Id => "record-update";
    public int Weight => 3;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Record;
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
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.UInt;
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
