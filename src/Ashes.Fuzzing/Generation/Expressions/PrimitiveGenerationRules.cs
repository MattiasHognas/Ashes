using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation.Expressions;

internal sealed class PrimitiveGenerationRule : IExpressionGenerationRule
{
    public string Id => "primitive";
    public int Weight => 6;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Primitive;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType is AshesType.Primitive or AshesType.UInt;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
        => ExpressionGenerator.GenerateLeaf(requiredType, context, budget, random);
}

internal sealed class VariableGenerationRule : IExpressionGenerationRule
{
    public string Id => "variable";
    public int Weight => 4;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Generic;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => context.Bindings.Any(binding => binding.Type == requiredType);
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GeneratedBinding[] choices = context.Bindings.Where(binding => binding.Type == requiredType).OrderBy(binding => binding.Name, StringComparer.Ordinal).ToArray();
        GeneratedBinding choice = choices[random.Next(choices.Length)];
        return ExpressionGenerator.Result(new Expr.Var(choice.Name), requiredType, GeneratedFeature.Variable, $"variable:{choice.Name}", 1);
    }
}

internal sealed class ArithmeticGenerationRule : IExpressionGenerationRule
{
    public string Id => "arithmetic";
    public int Weight => 3;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Numeric;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => IsNumeric(requiredType) && budget.RemainingNodes >= 3;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GenerationResult<Expr> left = expressions.Generate(requiredType, context, budget.Descend(2), random);
        int operationIndex = requiredType == AshesType.Float ? random.Next(4) : random.Next(5);
        GenerationResult<Expr> right = operationIndex >= 3
            ? NonZeroRight(requiredType)
            : expressions.Generate(requiredType, context, budget.Descend(2), random);
        string leftName = "leftOperand" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rightName = "rightOperand" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr operation = operationIndex switch
        {
            0 => new Expr.Add(new Expr.Var(leftName), new Expr.Var(rightName)),
            1 => new Expr.Subtract(new Expr.Var(leftName), new Expr.Var(rightName)),
            2 => new Expr.Multiply(new Expr.Var(leftName), new Expr.Var(rightName)),
            3 => new Expr.Divide(new Expr.Var(leftName), new Expr.Var(rightName)),
            _ => new Expr.Modulo(new Expr.Var(leftName), new Expr.Var(rightName)),
        };
        Expr value = new Expr.Let(leftName, left.Value, new Expr.Let(rightName, right.Value, operation));
        GeneratedFeatureSet features = new([GeneratedFeature.Arithmetic, GeneratedFeature.Let, GeneratedFeature.Variable]);
        features.UnionWith(left.Features); features.UnionWith(right.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("arithmetic", left.Trace, right.Trace), 5 + left.NodeCount + right.NodeCount);
    }

    private static bool IsNumeric(AshesType type) => type is AshesType.UInt or AshesType.Primitive { Name: "Int" or "Float" or "BigInt" };

    private static GenerationResult<Expr> NonZeroRight(AshesType type) => type switch
    {
        AshesType.Primitive { Name: "Int" } => ExpressionGenerator.Result(new Expr.IntLit(1), type, GeneratedFeature.Literal, "literal:nonzero-int", 1),
        AshesType.Primitive { Name: "Float" } => ExpressionGenerator.Result(new Expr.FloatLit(1.0), type, GeneratedFeature.Literal, "literal:nonzero-float", 1),
        AshesType.Primitive { Name: "BigInt" } => ExpressionGenerator.Result(new Expr.BigIntLit("1"), type, GeneratedFeature.Literal, "literal:nonzero-bigint", 1),
        AshesType.UInt unsigned => ExpressionGenerator.Result(new Expr.UIntLit(1, unsigned.Bits), type, GeneratedFeature.Literal, "literal:nonzero-uint", 1),
        _ => throw new InvalidOperationException($"'{type}' is not a numeric fuzz type."),
    };
}

internal sealed class ComparisonGenerationRule : IExpressionGenerationRule
{
    public string Id => "comparison";
    public int Weight => 2;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Bool;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) => requiredType == AshesType.Bool && budget.RemainingNodes >= 3;
    public GenerationResult<Expr> Generate(AshesType requiredType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        AshesType[] operandTypes =
        [
            AshesType.Int,
            AshesType.Bool,
            AshesType.Str,
            AshesType.Float,
            AshesType.BigInt,
            new AshesType.UInt(8),
            new AshesType.UInt(16),
            new AshesType.UInt(32),
            new AshesType.UInt(64),
        ];
        AshesType operandType = operandTypes[random.Next(operandTypes.Length)];
        GenerationResult<Expr> left = expressions.Generate(operandType, context, budget.Descend(2), random);
        GenerationResult<Expr> right = expressions.Generate(operandType, context, budget.Descend(2), random);
        string leftName = "comparedLeft" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rightName = "comparedRight" + random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool supportsOrdering = operandType is AshesType.UInt or AshesType.Primitive { Name: "Int" or "Float" or "BigInt" };
        int operationIndex = supportsOrdering ? random.Next(6) : random.Next(2);
        Expr operation = operationIndex switch
        {
            0 => new Expr.Equal(new Expr.Var(leftName), new Expr.Var(rightName)),
            1 => new Expr.NotEqual(new Expr.Var(leftName), new Expr.Var(rightName)),
            2 => new Expr.LessThan(new Expr.Var(leftName), new Expr.Var(rightName)),
            3 => new Expr.LessOrEqual(new Expr.Var(leftName), new Expr.Var(rightName)),
            4 => new Expr.GreaterThan(new Expr.Var(leftName), new Expr.Var(rightName)),
            _ => new Expr.GreaterOrEqual(new Expr.Var(leftName), new Expr.Var(rightName)),
        };
        Expr value = new Expr.Let(leftName, left.Value, new Expr.Let(rightName, right.Value, operation));
        GeneratedFeatureSet features = new([GeneratedFeature.Comparison, GeneratedFeature.Let, GeneratedFeature.Variable]); features.UnionWith(left.Features); features.UnionWith(right.Features);
        return new GenerationResult<Expr>(value, requiredType, features, GenerationTrace.Merge("comparison", left.Trace, right.Trace), 5 + left.NodeCount + right.NodeCount);
    }
}

internal sealed class LogicalNotGenerationRule : IExpressionGenerationRule
{
    public string Id => "logical-not";
    public int Weight => 2;
    public IReadOnlyList<AshesType> AdvertisedTypes => AdvertisedGenerationTypes.Bool;
    public bool CanGenerate(AshesType requiredType, GenerationContext context, GenerationBudget budget) =>
        requiredType == AshesType.Bool && budget.RemainingNodes >= 2;

    public GenerationResult<Expr> Generate(
        AshesType requiredType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        GenerationResult<Expr> operand = expressions.Generate(
            AshesType.Bool,
            context,
            budget.Descend(),
            random);
        GeneratedFeatureSet features = new([GeneratedFeature.LogicalNot]);
        features.UnionWith(operand.Features);
        return new GenerationResult<Expr>(
            new Expr.LogicalNot(operand.Value),
            requiredType,
            features,
            GenerationTrace.Merge("logical-not", operand.Trace),
            operand.NodeCount + 1);
    }
}
