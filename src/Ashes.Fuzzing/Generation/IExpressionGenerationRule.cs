using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal interface IExpressionGenerationRule
{
    string Id { get; }
    int Weight { get; }
    bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget);
    GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random);
}

internal sealed class GeneratorRegistry
{
    private readonly SortedDictionary<string, IExpressionGenerationRule> _rules = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<IExpressionGenerationRule> Rules => _rules.Values;

    internal void Register(IExpressionGenerationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Weight <= 0 || !_rules.TryAdd(rule.Id, rule))
        {
            throw new ArgumentException($"Duplicate or invalid expression generation rule '{rule.Id}'.");
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
        registry.Register(new Expressions.RecordUpdateGenerationRule());
        registry.Register(new Expressions.BitwiseGenerationRule());
        return registry;
    }
}
