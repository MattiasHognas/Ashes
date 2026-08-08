using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal interface IExpressionGenerationRule
{
    string Id { get; }
    int Weight { get; }
    IReadOnlyList<AshesType> AdvertisedTypes { get; }
    bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget);
    GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random);
}

internal sealed class GeneratorRegistry
{
    private readonly SortedDictionary<string, IExpressionGenerationRule> _rules = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<IExpressionGenerationRule> Rules => _rules.Values;

    internal void Register(IExpressionGenerationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Weight <= 0 || rule.AdvertisedTypes.Count == 0 || !_rules.TryAdd(rule.Id, rule))
        {
            throw new ArgumentException($"Duplicate or invalid expression generation rule '{rule.Id}'.");
        }

        GenerationBudget validationBudget = GenerationBudget.Create(80);
        foreach (AshesType advertisedType in rule.AdvertisedTypes)
        {
            GenerationContext validationContext = GenerationContext.Empty.WithBinding(
                new GeneratedBinding("advertisedValue", advertisedType));
            if (!rule.CanGenerate(advertisedType, validationContext, validationBudget))
            {
                _rules.Remove(rule.Id);
                throw new ArgumentException($"Expression generation rule '{rule.Id}' advertises unsupported type '{advertisedType}'.");
            }
        }
    }

    internal static GeneratorRegistry CreateDefault()
    {
        GeneratorRegistry registry = new();
        registry.Register(new Expressions.PrimitiveGenerationRule());
        registry.Register(new Expressions.VariableGenerationRule());
        registry.Register(new Expressions.ArithmeticGenerationRule());
        registry.Register(new Expressions.ComparisonGenerationRule());
        registry.Register(new Expressions.LetGenerationRule());
        registry.Register(new Expressions.IfGenerationRule());
        registry.Register(new Expressions.TupleGenerationRule());
        registry.Register(new Expressions.ListGenerationRule());
        registry.Register(new Expressions.LambdaGenerationRule());
        registry.Register(new Expressions.CallGenerationRule());
        registry.Register(new Expressions.RecordGenerationRule());
        registry.Register(new Expressions.ResultGenerationRule());
        registry.Register(new Expressions.AdtGenerationRule());
        registry.Register(new Expressions.TaskGenerationRule());
        registry.Register(new Expressions.ConsGenerationRule());
        registry.Register(new Expressions.ListMatchGenerationRule());
        registry.Register(new Expressions.TupleMatchGenerationRule());
        registry.Register(new Expressions.ResultMatchGenerationRule());
        registry.Register(new Expressions.ResultPipeGenerationRule());
        registry.Register(new Expressions.ResultMapErrorGenerationRule());
        registry.Register(new Expressions.RecordUpdateGenerationRule());
        registry.Register(new Expressions.BitwiseGenerationRule());
        return registry;
    }
}

internal static class AdvertisedGenerationTypes
{
    internal static IReadOnlyList<AshesType> Generic { get; } = [AshesType.Int, AshesType.Str];
    internal static IReadOnlyList<AshesType> Primitive { get; } = [AshesType.Int, AshesType.Bool, AshesType.Str, AshesType.Float, AshesType.BigInt, new AshesType.UInt(8)];
    internal static IReadOnlyList<AshesType> Numeric { get; } = [AshesType.Int, AshesType.Float, AshesType.BigInt, new AshesType.UInt(8)];
    internal static IReadOnlyList<AshesType> Bool { get; } = [AshesType.Bool];
    internal static IReadOnlyList<AshesType> Tuple { get; } = [new AshesType.Tuple([AshesType.Int, AshesType.Bool])];
    internal static IReadOnlyList<AshesType> List { get; } = [new AshesType.List(AshesType.Int)];
    internal static IReadOnlyList<AshesType> Function { get; } = [new AshesType.Function(AshesType.Int, AshesType.Str)];
    internal static IReadOnlyList<AshesType> Record { get; } = [new AshesType.Record("FuzzRecord")];
    internal static IReadOnlyList<AshesType> Result { get; } = [new AshesType.Result(AshesType.Str, AshesType.Int)];
    internal static IReadOnlyList<AshesType> Adt { get; } = [new AshesType.Adt("FuzzTree", [AshesType.Int])];
    internal static IReadOnlyList<AshesType> Task { get; } = [new AshesType.Task(AshesType.Str, AshesType.Int)];
    internal static IReadOnlyList<AshesType> UInt { get; } = [new AshesType.UInt(8), new AshesType.UInt(64)];
}
