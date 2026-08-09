using Ashes.Frontend;

namespace Ashes.Fuzzing.Coverage;

internal readonly record struct AstCoverageMetrics(int Nodes, int Depth)
{
    internal static AstCoverageMetrics Measure(Ashes.Frontend.Program program)
    {
        List<Expr> roots = [program.Body];
        foreach (TopLevelItem item in program.Items)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl binding:
                    roots.Add(binding.Value);
                    break;
                case TopLevelItem.RecursiveGroup group:
                    roots.AddRange(group.Bindings.Select(binding => binding.Value));
                    break;
                case TopLevelItem.Provide provide:
                    roots.AddRange(provide.Decl.Bindings.Select(binding => binding.Implementation));
                    break;
            }
        }
        AstCoverageMetrics[] metrics = roots.Select(Measure).ToArray();
        return new AstCoverageMetrics(metrics.Sum(metric => metric.Nodes), metrics.Max(metric => metric.Depth));
    }

    private static AstCoverageMetrics Measure(Expr expression)
    {
        AstCoverageMetrics[] children = Children(expression).Select(Measure).ToArray();
        return new AstCoverageMetrics(
            1 + children.Sum(child => child.Nodes),
            1 + (children.Length == 0 ? 0 : children.Max(child => child.Depth)));
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
        Expr.ResultPipe binary => [binary.Left, binary.Right],
        Expr.ResultMapErrorPipe binary => [binary.Left, binary.Right],
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
