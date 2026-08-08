using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation.Expressions;

internal sealed class LetGenerationRule : IExpressionGenerationRule
{
    public string Id => "let";
    public int Weight => 4;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 4;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType boundType = ChooseType(requiredType, random);
        string name = "v" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> value = expressions.Generate(boundType, context, budget.Descend(2), random);
        GenerationResult<Expr> body = expressions.Generate(requiredType, context.WithBinding(new GeneratedBinding(name, boundType)), budget.Descend(2), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Let]); features.UnionWith(value.Features); features.UnionWith(body.Features);
        return new GenerationResult<Expr>(new Expr.Let(name, value.Value, body.Value), requiredType, features, GenerationTrace.Merge($"let:{name}:{boundType}", value.Trace, body.Trace), 1 + value.NodeCount + body.NodeCount);
    }

    internal static AshesType ChooseType(AshesType fallback, FuzzRandom random) => random.Next(12) switch
    {
        0 => AshesType.Int,
        1 => AshesType.Bool,
        2 => AshesType.Str,
        3 => AshesType.Float,
        4 => AshesType.BigInt,
        5 => new AshesType.UInt(8),
        6 => new AshesType.UInt(32),
        7 => new AshesType.List(AshesType.Int),
        8 => new AshesType.List(AshesType.Str),
        9 => new AshesType.Tuple([AshesType.Int, AshesType.Bool]),
        10 => new AshesType.Result(AshesType.Str, AshesType.Int),
        _ => fallback,
    };
}

internal sealed class IfGenerationRule : IExpressionGenerationRule
{
    public string Id => "if";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 4;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> condition = expressions.Generate(AshesType.Bool, context, budget.Descend(3), random);
        GenerationResult<Expr> thenValue = expressions.Generate(requiredType, context, budget.Descend(3), random);
        GenerationResult<Expr> elseValue = expressions.Generate(requiredType, context, budget.Descend(3), random);
        GeneratedFeatureSet features = new([GeneratedFeature.If]); features.UnionWith(condition.Features); features.UnionWith(thenValue.Features); features.UnionWith(elseValue.Features);
        return new GenerationResult<Expr>(new Expr.If(condition.Value, thenValue.Value, elseValue.Value), requiredType, features, GenerationTrace.Merge("if", condition.Trace, thenValue.Trace, elseValue.Trace), 1 + condition.NodeCount + thenValue.NodeCount + elseValue.NodeCount);
    }
}

internal sealed class TupleGenerationRule : IExpressionGenerationRule
{
    public string Id => "tuple";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Tuple && budget.RemainingNodes >= 2;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Tuple tuple = (AshesType.Tuple)requiredType;
        List<(string Name, Expr Value)> bindings = []; GeneratedFeatureSet features = new([GeneratedFeature.Tuple, GeneratedFeature.Let, GeneratedFeature.Variable]); List<string> trace = ["tuple"]; int nodes = 1;
        foreach (AshesType elementType in tuple.Elements)
        {
            GenerationResult<Expr> element = expressions.Generate(elementType, context, budget.Descend(tuple.Elements.Count), random);
            string name = "tupleElement" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add((name, element.Value)); features.UnionWith(element.Features); trace.AddRange(element.Trace.Entries); nodes += element.NodeCount + 2;
        }
        Expr value = new Expr.TupleLit(bindings.Select(binding => (Expr)new Expr.Var(binding.Name)).ToArray());
        for (int i = bindings.Count - 1; i >= 0; i--) value = new Expr.Let(bindings[i].Name, bindings[i].Value, value);
        return new GenerationResult<Expr>(value, requiredType, features, new GenerationTrace(trace), nodes);
    }
}

internal sealed class ListGenerationRule : IExpressionGenerationRule
{
    public string Id => "list";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.List && budget.RemainingNodes >= 2;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.List list = (AshesType.List)requiredType;
        int count = random.Next(Math.Min(3, budget.MaximumCollectionLength) + 1);
        List<(string Name, Expr Value)> bindings = []; GeneratedFeatureSet features = new([GeneratedFeature.List]); List<string> trace = ["list"]; int nodes = 1;
        for (int i = 0; i < count; i++)
        {
            GenerationResult<Expr> element = expressions.Generate(list.Element, context, budget.Descend(Math.Max(1, count)), random);
            string name = "listElement" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add((name, element.Value)); features.UnionWith(element.Features); trace.AddRange(element.Trace.Entries); nodes += element.NodeCount + 2;
        }
        Expr value = new Expr.ListLit(bindings.Select(binding => (Expr)new Expr.Var(binding.Name)).ToArray());
        for (int i = bindings.Count - 1; i >= 0; i--) value = new Expr.Let(bindings[i].Name, bindings[i].Value, value);
        if (bindings.Count > 0)
        {
            features.Add(GeneratedFeature.Let);
            features.Add(GeneratedFeature.Variable);
        }
        return new GenerationResult<Expr>(value, requiredType, features, new GenerationTrace(trace), nodes);
    }
}

internal sealed class LambdaGenerationRule : IExpressionGenerationRule
{
    public string Id => "lambda";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Function && budget.RemainingNodes >= 2;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Function function = (AshesType.Function)requiredType;
        string parameter = "arg" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> body = expressions.Generate(function.Return, context.WithBinding(new GeneratedBinding(parameter, function.Parameter)), budget.Descend(), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Lambda]); features.UnionWith(body.Features);
        Expr.Lambda lambda = new(parameter, body.Value) { ParamAnnotation = function.Parameter.ToSyntax() };
        return new GenerationResult<Expr>(lambda, requiredType, features, GenerationTrace.Merge($"lambda:{parameter}:{function.Parameter}", body.Trace), body.NodeCount + 1);
    }
}

internal sealed class CallGenerationRule : IExpressionGenerationRule
{
    public string Id => "call";
    public int Weight => 2;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => budget.RemainingNodes >= 4;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType parameterType = LetGenerationRule.ChooseType(requiredType, random);
        string parameter = "callArg" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> body = expressions.Generate(requiredType, context.WithBinding(new GeneratedBinding(parameter, parameterType)), budget.Descend(2), random);
        GenerationResult<Expr> argument = expressions.Generate(parameterType, context, budget.Descend(2), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Call, GeneratedFeature.Lambda]);
        features.UnionWith(body.Features);
        features.UnionWith(argument.Features);
        Expr function = new Expr.Lambda(parameter, body.Value) { ParamAnnotation = parameterType.ToSyntax() };
        string functionName = "localFunction" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr call = new Expr.Let(functionName, function, new Expr.Call(new Expr.Var(functionName), argument.Value));
        features.Add(GeneratedFeature.Let);
        return new GenerationResult<Expr>(call, requiredType, features, GenerationTrace.Merge($"call:{parameterType}", body.Trace, argument.Trace), 4 + body.NodeCount + argument.NodeCount);
    }
}

internal sealed class RecordGenerationRule : IExpressionGenerationRule
{
    public string Id => "record";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Record { Name: "FuzzRecord" };
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> first = expressions.Generate(AshesType.Int, context, budget.Descend(2), random);
        GenerationResult<Expr> second = expressions.Generate(AshesType.Bool, context, budget.Descend(2), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Record]);
        features.UnionWith(first.Features);
        features.UnionWith(second.Features);
        Expr value = new Expr.RecordLit("FuzzRecord", [("first", first.Value), ("second", second.Value)]);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("record", first.Trace, second.Trace), first.NodeCount + second.NodeCount + 1);
    }
}

internal sealed class ResultGenerationRule : IExpressionGenerationRule
{
    public string Id => "result";
    public int Weight => 3;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Result;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType.Result result = (AshesType.Result)requiredType;
        bool success = random.NextBool();
        AshesType payloadType = success ? result.Value : result.Error;
        GenerationResult<Expr> payload = expressions.Generate(payloadType, context, budget.Descend(2), random);
        GeneratedFeatureSet features = new([GeneratedFeature.ResultShortCircuit]);
        features.UnionWith(payload.Features);
        string constructor = success ? "Ok" : "Error";
        return new GenerationResult<Expr>(new Expr.Call(new Expr.Var(constructor), payload.Value), requiredType, features, GenerationTrace.Merge($"result:{constructor}", payload.Trace), payload.NodeCount + 2);
    }
}
