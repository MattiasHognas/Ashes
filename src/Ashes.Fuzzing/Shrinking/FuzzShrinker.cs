using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Shrinking;

using AshesFormatter = Ashes.Formatter.Formatter;
using FrontendProgram = Ashes.Frontend.Program;

internal sealed record ShrinkResult(GeneratedFuzzCase Case, int Attempts, int Accepted, TimeSpan Duration);

internal sealed class FuzzShrinker
{
    internal async Task<ShrinkResult> ShrinkAsync(GeneratedFuzzCase original, Func<GeneratedFuzzCase, CancellationToken, ValueTask<bool>> stillFails, int maximumAttempts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        GeneratedFuzzCase current = original;
        int attempts = 0;
        int accepted = 0;
        while (attempts < maximumAttempts && DateTimeOffset.UtcNow - started < timeout)
        {
            bool changed = false;
            foreach (GeneratedFuzzCase candidate in Candidates(current))
            {
                attempts++;
                if (StableSizeMetric.Measure(candidate) >= StableSizeMetric.Measure(current))
                {
                    continue;
                }
                if (await stillFails(candidate, cancellationToken).ConfigureAwait(false))
                {
                    current = candidate;
                    accepted++;
                    changed = true;
                    break;
                }
                if (attempts >= maximumAttempts) break;
            }
            if (!changed) break;
        }
        return new ShrinkResult(current, attempts, accepted, DateTimeOffset.UtcNow - started);
    }

    internal IEnumerable<GeneratedFuzzCase> Candidates(GeneratedFuzzCase source)
    {
        foreach ((Expr expression, int nodes) in ExpressionCandidates(source.Program.Body, source.Type))
        {
            FrontendProgram program = source.Program with { Body = expression };
            string formatted = AshesFormatter.Format(program);
            GeneratedFuzzCase candidate = source with { Program = program, Source = formatted, NodeCount = nodes, Trace = source.Trace.Append("shrink") };
            if (StableSizeMetric.Measure(candidate) < StableSizeMetric.Measure(source))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<(Expr Expr, int Nodes)> ExpressionCandidates(Expr expression, AshesType type)
    {
        Expr literal = Leaf(type);
        if (literal != expression) yield return (literal, Count(literal));
        switch (expression)
        {
            case Expr.If conditional:
                yield return (conditional.Then, Count(conditional.Then));
                yield return (conditional.Else, Count(conditional.Else));
                foreach ((Expr child, int _) in ExpressionCandidates(conditional.Then, type))
                {
                    Expr candidate = conditional with { Then = child };
                    yield return (candidate, Count(candidate));
                }
                foreach ((Expr child, int _) in ExpressionCandidates(conditional.Else, type))
                {
                    Expr candidate = conditional with { Else = child };
                    yield return (candidate, Count(candidate));
                }
                break;
            case Expr.Let let:
                if (!ContainsVariable(let.Body, let.Name))
                {
                    yield return (let.Body, Count(let.Body));
                }
                foreach ((Expr child, int _) in ExpressionCandidates(let.Body, type))
                {
                    Expr candidate = let with { Body = child };
                    yield return (candidate, Count(candidate));
                }
                break;
            case Expr.TupleLit tuple when type is AshesType.Tuple tupleType:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    foreach ((Expr child, int _) in ExpressionCandidates(tuple.Elements[index], tupleType.Elements[index]))
                    {
                        Expr[] elements = [.. tuple.Elements];
                        elements[index] = child;
                        Expr candidate = new Expr.TupleLit(elements);
                        yield return (candidate, Count(candidate));
                    }
                }
                break;
            case Expr.ListLit list when type is AshesType.List listType:
                if (list.Elements.Count > 0)
                {
                    Expr shorter = new Expr.ListLit(list.Elements.Take(list.Elements.Count - 1).ToArray());
                    yield return (shorter, Count(shorter));
                }
                for (int index = 0; index < list.Elements.Count; index++)
                {
                    foreach ((Expr child, int _) in ExpressionCandidates(list.Elements[index], listType.Element))
                    {
                        Expr[] elements = [.. list.Elements];
                        elements[index] = child;
                        Expr candidate = new Expr.ListLit(elements);
                        yield return (candidate, Count(candidate));
                    }
                }
                break;
            case Expr.RecordLit record when type is AshesType.Record { Name: "FuzzRecord" }:
                AshesType[] fieldTypes = [AshesType.Int, AshesType.Bool];
                for (int index = 0; index < record.Fields.Count; index++)
                {
                    foreach ((Expr child, int _) in ExpressionCandidates(record.Fields[index].Value, fieldTypes[index]))
                    {
                        (string Name, Expr Value)[] fields = [.. record.Fields];
                        fields[index] = (fields[index].Name, child);
                        Expr candidate = new Expr.RecordLit(record.TypeName, fields);
                        yield return (candidate, Count(candidate));
                    }
                }
                break;
            case Expr.IntLit integer when integer.Value != 0:
                yield return (new Expr.IntLit(integer.Value / 2), 1);
                break;
            case Expr.BigIntLit integer when !string.Equals(integer.Digits, "0", StringComparison.Ordinal):
                yield return (new Expr.BigIntLit("0"), 1);
                break;
            case Expr.UIntLit integer when integer.Value != 0:
                yield return (integer with { Value = integer.Value / 2 }, 1);
                break;
            case Expr.StrLit text when text.Value.Length > 0:
                yield return (new Expr.StrLit(text.Value[..(text.Value.Length / 2)]), 1);
                break;
        }
    }

    private static bool ContainsVariable(Expr expression, string name) => expression switch
    {
        Expr.Var variable => string.Equals(variable.Name, name, StringComparison.Ordinal),
        Expr.Let let => ContainsVariable(let.Value, name) || (!string.Equals(let.Name, name, StringComparison.Ordinal) && ContainsVariable(let.Body, name)),
        Expr.LetRecursive recursive => !string.Equals(recursive.Name, name, StringComparison.Ordinal) && (ContainsVariable(recursive.Value, name) || ContainsVariable(recursive.Body, name)),
        Expr.Lambda lambda => !string.Equals(lambda.ParamName, name, StringComparison.Ordinal) && ContainsVariable(lambda.Body, name),
        Expr.If conditional => ContainsVariable(conditional.Cond, name) || ContainsVariable(conditional.Then, name) || ContainsVariable(conditional.Else, name),
        Expr.Call call => ContainsVariable(call.Func, name) || ContainsVariable(call.Arg, name),
        Expr.TupleLit tuple => tuple.Elements.Any(element => ContainsVariable(element, name)),
        Expr.ListLit list => list.Elements.Any(element => ContainsVariable(element, name)),
        Expr.Cons cons => ContainsVariable(cons.Head, name) || ContainsVariable(cons.Tail, name),
        Expr.Match match => ContainsVariable(match.Value, name) || match.Cases.Any(matchCase => ContainsVariable(matchCase.Body, name) || (matchCase.Guard is not null && ContainsVariable(matchCase.Guard, name))),
        Expr.RecordLit record => record.Fields.Any(field => ContainsVariable(field.Value, name)),
        Expr.RecordUpdate update => ContainsVariable(update.Target, name) || update.Updates.Any(field => ContainsVariable(field.Value, name)),
        _ => false,
    };

    private static Expr Leaf(AshesType type) => type switch
    {
        AshesType.Primitive { Name: "Int" } => new Expr.IntLit(0),
        AshesType.Primitive { Name: "Bool" } => new Expr.BoolLit(false),
        AshesType.Primitive { Name: "Str" } => new Expr.StrLit(""),
        AshesType.Primitive { Name: "Float" } => new Expr.FloatLit(0.0),
        AshesType.Primitive { Name: "BigInt" } => new Expr.BigIntLit("0"),
        AshesType.UInt unsigned => new Expr.UIntLit(0, unsigned.Bits),
        AshesType.List => new Expr.ListLit([]),
        AshesType.Tuple tuple => new Expr.TupleLit(tuple.Elements.Select(Leaf).ToArray()),
        AshesType.Function function => new Expr.Lambda("shrunk", Leaf(function.Return)) { ParamAnnotation = function.Parameter.ToSyntax() },
        AshesType.Record { Name: "FuzzRecord" } => new Expr.RecordLit("FuzzRecord", [("first", new Expr.IntLit(0)), ("second", new Expr.BoolLit(false))]),
        AshesType.Result result => new Expr.Call(new Expr.Var("Ok"), Leaf(result.Value)),
        AshesType.Adt { Name: "FuzzTree" } => new Expr.Var("FuzzEmpty"),
        AshesType.Task { Error: AshesType.Primitive { Name: "Str" } } task => new Expr.Call(new Expr.Var("async"), Leaf(task.Value)),
        _ => throw new InvalidOperationException($"Cannot shrink '{type}' to a leaf."),
    };

    private static int Count(Expr expression) => expression switch
    {
        Expr.Let let => 1 + Count(let.Value) + Count(let.Body),
        Expr.LetRecursive recursive => 1 + Count(recursive.Value) + Count(recursive.Body),
        Expr.If conditional => 1 + Count(conditional.Cond) + Count(conditional.Then) + Count(conditional.Else),
        Expr.Lambda lambda => 1 + Count(lambda.Body),
        Expr.Call call => 1 + Count(call.Func) + Count(call.Arg),
        Expr.TupleLit tuple => 1 + tuple.Elements.Sum(Count),
        Expr.ListLit list => 1 + list.Elements.Sum(Count),
        Expr.Cons cons => 1 + Count(cons.Head) + Count(cons.Tail),
        Expr.Match match => 1 + Count(match.Value) + match.Cases.Sum(matchCase => Count(matchCase.Body)),
        Expr.Add binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.Subtract binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.Multiply binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.Divide binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.Modulo binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.BitwiseAnd binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.BitwiseOr binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.BitwiseXor binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.ShiftLeft binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.ShiftRight binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.Equal binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.NotEqual binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.LessThan binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.LessOrEqual binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.GreaterThan binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.GreaterOrEqual binary => 1 + Count(binary.Left) + Count(binary.Right),
        Expr.RecordLit record => 1 + record.Fields.Sum(field => Count(field.Value)),
        Expr.RecordUpdate update => 1 + Count(update.Target) + update.Updates.Sum(field => Count(field.Value)),
        Expr.Await awaitExpression => 1 + Count(awaitExpression.Task),
        _ => 1,
    };
}
