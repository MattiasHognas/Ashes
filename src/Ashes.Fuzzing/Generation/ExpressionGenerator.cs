using Ashes.Frontend;
using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Coverage;

namespace Ashes.Fuzzing.Generation;

internal sealed class ExpressionGenerator
{
    private readonly GeneratorRegistry _rules;
    private readonly CombinationGenerator? _combinations;
    private readonly IReadOnlySet<string> _enabledRules;
    private readonly string? _preferredRule;
    private readonly GenerationCoverageGuidance _coverage;

    internal ExpressionGenerator(
        GeneratorRegistry rules,
        IReadOnlySet<string> enabledRules,
        GenerationCoverageGuidance coverage,
        CombinationGenerator? combinations = null,
        string? preferredRule = null)
    {
        _rules = rules;
        _enabledRules = enabledRules;
        _combinations = combinations;
        _preferredRule = preferredRule;
        _coverage = coverage;
    }

    internal GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        if (!budget.IsLeaf && _combinations is not null && budget.RemainingCombinations > 0 && random.Next(5) == 0)
        {
            GenerationResult<Expr>? combined = _combinations.TryGenerate(requiredType, context, budget, this, random);
            if (combined is not null && combined.NodeCount <= budget.RemainingNodes)
            {
                return combined;
            }
        }

        IExpressionGenerationRule[] applicable = _rules.Rules
            .Where(rule => _enabledRules.Contains(rule.Id) && (!budget.IsLeaf || rule.Id is "primitive" or "variable" or "record" or "result" or "adt" or "task") && rule.CanGenerate(requiredType, context, budget))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        if (applicable.Length == 0)
        {
            return GenerateLeaf(requiredType, context, budget, random);
        }

        int totalWeight = applicable.Sum(EffectiveWeight);
        int choice = random.Next(totalWeight);
        IExpressionGenerationRule selected = applicable[0];
        foreach (IExpressionGenerationRule rule in applicable)
        {
            int weight = EffectiveWeight(rule);
            if (choice < weight)
            {
                selected = rule;
                break;
            }
            choice -= weight;
        }

        _coverage.RecordRule(selected.Id);
        GenerationResult<Expr> result = selected.Generate(requiredType, context, budget.Descend(), this, random);
        return result.NodeCount <= budget.RemainingNodes
            ? result with { Trace = new GenerationTrace([$"rule:{selected.Id}", .. result.Trace.Entries]) }
            : GenerateLeaf(requiredType, context, budget, random);
    }

    private int EffectiveWeight(IExpressionGenerationRule rule) => _coverage.RuleWeight(
        rule.Id,
        rule.Weight,
        string.Equals(rule.Id, _preferredRule, StringComparison.Ordinal));

    internal static GenerationResult<Expr> GenerateLeaf(AshesType type, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        GeneratedBinding[] compatible = context.Bindings.Where(binding => binding.Type == type).OrderBy(binding => binding.Name, StringComparer.Ordinal).ToArray();
        if (compatible.Length > 0 && random.NextBool())
        {
            GeneratedBinding binding = compatible[random.Next(compatible.Length)];
            return Result(new Expr.Var(binding.Name), type, GeneratedFeature.Variable, "variable", 1);
        }

        return type switch
        {
            AshesType.Primitive { Name: "Int" } => Result(new Expr.IntLit((long)random.Next(21) - 10), type, GeneratedFeature.Literal, "literal:int", 1),
            AshesType.Primitive { Name: "Bool" } => Result(new Expr.BoolLit(random.NextBool()), type, GeneratedFeature.Literal, "literal:bool", 1),
            AshesType.Primitive { Name: "Str" } => Result(new Expr.StrLit(new[] { "", "ash", "owned", "λ" }[random.Next(4)]), type, GeneratedFeature.Literal, "literal:str", 1),
            AshesType.Primitive { Name: "Float" } => Result(new Expr.FloatLit(random.Next(9) + 0.5), type, GeneratedFeature.Literal, "literal:float", 1),
            AshesType.Primitive { Name: "BigInt" } => Result(new Expr.BigIntLit(random.Next(1000).ToString(System.Globalization.CultureInfo.InvariantCulture)), type, GeneratedFeature.Literal, "literal:bigint", 1),
            AshesType.UInt unsigned => Result(new Expr.UIntLit((ulong)random.Next(16), unsigned.Bits), type, GeneratedFeature.Literal, $"literal:uint{unsigned.Bits}", 1),
            AshesType.Tuple tuple => AggregateTuple(tuple, context, budget, random),
            AshesType.List => Result(new Expr.ListLit([]), type, GeneratedFeature.List, "leaf:list", 1),
            AshesType.Function function => LeafFunction(function, context, budget, random),
            AshesType.Record { Name: "FuzzRecord" } => LeafRecord(type, context, budget, random),
            AshesType.Result result => LeafResult(result, context, budget, random),
            AshesType.Adt { Name: "FuzzTree" } adt => LeafAdt(adt, context, budget, random),
            AshesType.Task { Error: AshesType.Primitive { Name: "Str" } } task => LeafTask(task, context, budget, random),
            _ => throw new InvalidOperationException($"No leaf generator is available for '{type}'."),
        };
    }

    private static GenerationResult<Expr> AggregateTuple(AshesType.Tuple tuple, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        List<Expr> values = [];
        GeneratedFeatureSet features = new([GeneratedFeature.Tuple]);
        List<string> trace = ["leaf:tuple"];
        int nodes = 1;
        foreach (AshesType element in tuple.Elements)
        {
            GenerationResult<Expr> child = GenerateLeaf(element, context, budget.Descend(), random);
            values.Add(child.Value);
            features.UnionWith(child.Features);
            trace.AddRange(child.Trace.Entries);
            nodes += child.NodeCount;
        }
        return new GenerationResult<Expr>(new Expr.TupleLit(values), tuple, features, new GenerationTrace(trace), nodes);
    }

    private static GenerationResult<Expr> LeafFunction(AshesType.Function function, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        string name = "p" + random.Next(1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        GenerationResult<Expr> body = GenerateLeaf(function.Return, context.WithBinding(new GeneratedBinding(name, function.Parameter)), budget.Descend(), random);
        GeneratedFeatureSet features = body.Features.Copy();
        features.Add(GeneratedFeature.Lambda);
        return new GenerationResult<Expr>(new Expr.Lambda(name, body.Value), function, features, GenerationTrace.Merge("leaf:lambda", body.Trace), body.NodeCount + 1);
    }

    private static GenerationResult<Expr> LeafRecord(AshesType type, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        GenerationResult<Expr> first = GenerateLeaf(AshesType.Int, context, budget.Descend(), random);
        GenerationResult<Expr> second = GenerateLeaf(AshesType.Bool, context, budget.Descend(), random);
        GeneratedFeatureSet features = new([GeneratedFeature.Record]);
        features.UnionWith(first.Features);
        features.UnionWith(second.Features);
        return new GenerationResult<Expr>(new Expr.RecordLit("FuzzRecord", [("first", first.Value), ("second", second.Value)]), type, features, GenerationTrace.Merge("leaf:record", first.Trace, second.Trace), first.NodeCount + second.NodeCount + 1);
    }

    private static GenerationResult<Expr> LeafResult(AshesType.Result result, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        bool success = random.NextBool();
        AshesType payloadType = success ? result.Value : result.Error;
        GenerationResult<Expr> payload = GenerateLeaf(payloadType, context, budget.Descend(), random);
        GeneratedFeatureSet features = payload.Features.Copy();
        features.Add(GeneratedFeature.ResultShortCircuit);
        string constructor = success ? "Ok" : "Error";
        return new GenerationResult<Expr>(new Expr.Call(new Expr.Var(constructor), payload.Value), result, features, GenerationTrace.Merge($"leaf:result:{constructor}", payload.Trace), payload.NodeCount + 2);
    }

    private static GenerationResult<Expr> LeafAdt(AshesType.Adt adt, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        AshesType payloadType = adt.Arguments[0];
        GenerationResult<Expr> payload = GenerateLeaf(payloadType, context, budget.Descend(), random);
        GeneratedFeatureSet features = payload.Features.Copy();
        features.Add(GeneratedFeature.Adt);
        Expr value = new Expr.Call(new Expr.Var("FuzzLeaf"), payload.Value);
        return new GenerationResult<Expr>(value, adt, features, GenerationTrace.Merge("leaf:adt", payload.Trace), payload.NodeCount + 2);
    }

    private static GenerationResult<Expr> LeafTask(AshesType.Task task, GenerationContext context, GenerationBudget budget, FuzzRandom random)
    {
        GenerationResult<Expr> payload = GenerateLeaf(task.Value, context, budget.Descend(), random);
        GeneratedFeatureSet features = payload.Features.Copy();
        features.Add(GeneratedFeature.Await);
        Expr value = new Expr.Call(new Expr.Var("async"), payload.Value);
        return new GenerationResult<Expr>(value, task, features, GenerationTrace.Merge("leaf:task", payload.Trace), payload.NodeCount + 2);
    }

    internal static GenerationResult<Expr> Result(Expr expression, AshesType type, GeneratedFeature feature, string trace, int nodes)
        => new(expression, type, new GeneratedFeatureSet([feature]), new GenerationTrace([trace]), nodes);
}
