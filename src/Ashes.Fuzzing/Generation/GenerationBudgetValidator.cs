using Ashes.Frontend;
using Ashes.Fuzzing.Coverage;

namespace Ashes.Fuzzing.Generation;

internal readonly record struct GenerationBudgetUsage(
    int Nodes,
    int Depth,
    int Declarations,
    int Functions,
    int Adts,
    int MaximumMatchCases,
    int MaximumCollectionLength,
    int RecursionComplexity,
    int Combinations,
    int SourceLength);

internal static class GenerationBudgetValidator
{
    internal static GenerationBudgetUsage Measure(
        Ashes.Frontend.Program program,
        GenerationTrace trace,
        int sourceLength)
    {
        AstCoverageMetrics ast = AstCoverageMetrics.Measure(program);
        int functions = 0;
        int maximumMatchCases = 0;
        int maximumCollectionLength = 0;
        int recursionComplexity = 0;
        foreach ((Expr expression, int recursionBase) in Roots(program))
        {
            Visit(
                expression,
                recursionBase,
                ref functions,
                ref maximumMatchCases,
                ref maximumCollectionLength,
                ref recursionComplexity);
        }
        int combinations = trace.Entries.Count(entry => entry.StartsWith("combination:", StringComparison.Ordinal));
        return new GenerationBudgetUsage(
            ast.Nodes,
            ast.Depth,
            program.Items.Count,
            functions,
            program.Items.OfType<TopLevelItem.Type>().Count(item => !item.Decl.IsRecord),
            maximumMatchCases,
            maximumCollectionLength,
            recursionComplexity,
            combinations,
            sourceLength);
    }

    internal static IReadOnlyList<string> Validate(
        Ashes.Frontend.Program program,
        GenerationTrace trace,
        int sourceLength,
        GenerationBudget budget)
    {
        GenerationBudgetUsage usage = Measure(program, trace, sourceLength);
        List<string> errors = [];
        Check("nodes", usage.Nodes, budget.RemainingNodes, errors);
        Check("depth", usage.Depth, budget.RemainingDepth, errors);
        Check("declarations", usage.Declarations, budget.RemainingDeclarations, errors);
        Check("functions", usage.Functions, budget.RemainingFunctions, errors);
        Check("ADTs", usage.Adts, budget.RemainingAdts, errors);
        Check("match cases", usage.MaximumMatchCases, budget.MaximumMatchCases, errors);
        Check("collection length", usage.MaximumCollectionLength, budget.MaximumCollectionLength, errors);
        Check("recursion complexity", usage.RecursionComplexity, budget.RemainingRecursion, errors);
        Check("combinations", usage.Combinations, budget.RemainingCombinations, errors);
        Check("source length", usage.SourceLength, budget.MaximumSourceLength, errors);
        return errors;
    }

    private static void Check(string dimension, int actual, int maximum, ICollection<string> errors)
    {
        if (actual > maximum)
        {
            errors.Add($"Generation exceeded {dimension} budget: {actual} > {maximum}.");
        }
    }

    private static IEnumerable<(Expr Expression, int RecursionBase)> Roots(Ashes.Frontend.Program program)
    {
        yield return (program.Body, 0);
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl binding:
                    yield return (binding.Value, binding.IsRecursive ? 1 : 0);
                    break;
                case TopLevelItem.RecursiveGroup group:
                    foreach ((string _, Expr value) in group.Bindings)
                    {
                        yield return (value, 1);
                    }
                    break;
                case TopLevelItem.Provide provide:
                    foreach (ProvideBinding binding in provide.Decl.Bindings)
                    {
                        yield return (binding.Implementation, 0);
                    }
                    break;
                case TopLevelItem.Trait trait:
                    foreach (TraitMethodDecl method in trait.Decl.Methods)
                    {
                        if (method.DefaultImplementation is not null)
                        {
                            yield return (method.DefaultImplementation, 0);
                        }
                    }
                    break;
                case TopLevelItem.Implementation instance:
                    foreach (TraitImplementationMethodBinding binding in instance.Decl.Bindings)
                    {
                        yield return (binding.Implementation, 0);
                    }
                    break;
            }
        }
    }

    private static void Visit(
        Expr expression,
        int recursionDepth,
        ref int functions,
        ref int maximumMatchCases,
        ref int maximumCollectionLength,
        ref int recursionComplexity)
    {
        if (expression is Expr.Lambda)
        {
            functions++;
        }
        if (expression is Expr.Match match)
        {
            maximumMatchCases = Math.Max(maximumMatchCases, match.Cases.Count);
        }
        if (expression is Expr.ListLit list)
        {
            maximumCollectionLength = Math.Max(maximumCollectionLength, list.Elements.Count);
        }
        int childRecursionDepth = expression is Expr.LetRecursive ? recursionDepth + 1 : recursionDepth;
        recursionComplexity = Math.Max(recursionComplexity, childRecursionDepth);
        foreach (Expr child in Children(expression))
        {
            Visit(
                child,
                childRecursionDepth,
                ref functions,
                ref maximumMatchCases,
                ref maximumCollectionLength,
                ref recursionComplexity);
        }
    }

    private static IEnumerable<Expr> Children(Expr expression) => expression switch
    {
        Expr.Add binary => [binary.Left, binary.Right],
        Expr.Subtract binary => [binary.Left, binary.Right],
        Expr.Multiply binary => [binary.Left, binary.Right],
        Expr.Divide binary => [binary.Left, binary.Right],
        Expr.Modulo binary => [binary.Left, binary.Right],
        Expr.BitwiseAnd binary => [binary.Left, binary.Right],
        Expr.BitwiseOr binary => [binary.Left, binary.Right],
        Expr.BitwiseXor binary => [binary.Left, binary.Right],
        Expr.ShiftLeft binary => [binary.Left, binary.Right],
        Expr.ShiftRight binary => [binary.Left, binary.Right],
        Expr.BitwiseNot unary => [unary.Operand],
        Expr.LogicalNot unary => [unary.Operand],
        Expr.GreaterThan binary => [binary.Left, binary.Right],
        Expr.LessThan binary => [binary.Left, binary.Right],
        Expr.GreaterOrEqual binary => [binary.Left, binary.Right],
        Expr.LessOrEqual binary => [binary.Left, binary.Right],
        Expr.Equal binary => [binary.Left, binary.Right],
        Expr.NotEqual binary => [binary.Left, binary.Right],
        Expr.ResultPipe pipe => [pipe.Left, pipe.Right],
        Expr.ResultMapErrorPipe pipe => [pipe.Left, pipe.Right],
        Expr.Let let => [let.Value, let.Body],
        Expr.LetResult let => [let.Value, let.Body],
        Expr.LetRecursive let => [let.Value, let.Body],
        Expr.If conditional => [conditional.Cond, conditional.Then, conditional.Else],
        Expr.Lambda lambda => [lambda.Body],
        Expr.Call call => [call.Func, call.Arg],
        Expr.TupleLit tuple => tuple.Elements,
        Expr.ListLit list => list.Elements,
        Expr.Cons cons => [cons.Head, cons.Tail],
        Expr.Match match =>
        [
            match.Value,
            .. match.Cases.SelectMany(matchCase => matchCase.Guard is null
                ? new[] { matchCase.Body }
                : new[] { matchCase.Body, matchCase.Guard }),
        ],
        Expr.Await awaitExpression => [awaitExpression.Task],
        Expr.RecordLit record => record.Fields.Select(field => field.Value),
        Expr.RecordUpdate update => [update.Target, .. update.Updates.Select(field => field.Value)],
        Expr.Perform perform => [perform.Operation],
        Expr.Handle handle => [handle.Body, .. handle.Arms.Select(arm => arm.Body)],
        _ => [],
    };
}
