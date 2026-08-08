using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal sealed class AstInvariantValidator
{
    internal IReadOnlyList<string> ValidateScope(Ashes.Frontend.Program program)
    {
        List<string> errors = [];
        HashSet<string> topLevel = new(StringComparer.Ordinal) { "FuzzBox", "FuzzRecord", "Ok", "Error", "Unit", "resume", "async" };
        foreach (TopLevelItem item in program.Items)
        {
            if (item is TopLevelItem.Type type)
            {
                foreach (TypeConstructor constructor in type.Decl.Constructors)
                {
                    topLevel.Add(constructor.Name);
                }
            }
            if (item is TopLevelItem.LetDecl binding)
            {
                Validate(binding.Value, topLevel, errors);
                topLevel.Add(binding.Name);
            }
        }
        Validate(program.Body, topLevel, errors);
        return errors;
    }

    private static void Validate(Expr expression, IReadOnlySet<string> scope, List<string> errors)
    {
        switch (expression)
        {
            case Expr.Var variable:
                if (!scope.Contains(variable.Name)) errors.Add($"'{variable.Name}' is not in scope.");
                break;
            case Expr.Let let:
                Validate(let.Value, scope, errors);
                Validate(let.Body, Add(scope, let.Name), errors);
                break;
            case Expr.LetRecursive recursive:
                IReadOnlySet<string> recursiveScope = Add(scope, recursive.Name);
                Validate(recursive.Value, recursiveScope, errors); Validate(recursive.Body, recursiveScope, errors);
                break;
            case Expr.Lambda lambda: Validate(lambda.Body, Add(scope, lambda.ParamName), errors); break;
            case Expr.If conditional: Validate(conditional.Cond, scope, errors); Validate(conditional.Then, scope, errors); Validate(conditional.Else, scope, errors); break;
            case Expr.Call call: Validate(call.Func, scope, errors); Validate(call.Arg, scope, errors); break;
            case Expr.TupleLit tuple: foreach (Expr element in tuple.Elements) Validate(element, scope, errors); break;
            case Expr.ListLit list: foreach (Expr element in list.Elements) Validate(element, scope, errors); break;
            case Expr.Cons cons: Validate(cons.Head, scope, errors); Validate(cons.Tail, scope, errors); break;
            case Expr.Add binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.Subtract binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.Multiply binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.Divide binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.Modulo binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.BitwiseAnd binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.BitwiseOr binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.BitwiseXor binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.ShiftLeft binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.ShiftRight binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.BitwiseNot unary: Validate(unary.Operand, scope, errors); break;
            case Expr.Equal binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.NotEqual binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.GreaterThan binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.LessThan binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.GreaterOrEqual binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.LessOrEqual binary: Validate(binary.Left, scope, errors); Validate(binary.Right, scope, errors); break;
            case Expr.Match match:
                Validate(match.Value, scope, errors);
                foreach (MatchCase matchCase in match.Cases)
                {
                    HashSet<string> armScope = new(scope, StringComparer.Ordinal);
                    AddPatternBindings(matchCase.Pattern, armScope);
                    Validate(matchCase.Body, armScope, errors);
                    if (matchCase.Guard is not null) Validate(matchCase.Guard, armScope, errors);
                }
                break;
            case Expr.RecordLit record:
                foreach ((string _, Expr value) in record.Fields)
                {
                    Validate(value, scope, errors);
                }
                break;
            case Expr.RecordUpdate update:
                Validate(update.Target, scope, errors);
                foreach ((string _, Expr value) in update.Updates)
                {
                    Validate(value, scope, errors);
                }
                break;
            case Expr.Await awaitExpression:
                Validate(awaitExpression.Task, scope, errors);
                break;
            case Expr.Perform perform:
                Validate(perform.Operation, scope, errors);
                break;
            case Expr.Handle handle:
                Validate(handle.Body, scope, errors);
                foreach (HandlerArm arm in handle.Arms)
                {
                    HashSet<string> armScope = new(scope, StringComparer.Ordinal);
                    foreach (Pattern pattern in arm.Parameters)
                    {
                        AddPatternBindings(pattern, armScope);
                    }
                    Validate(arm.Body, armScope, errors);
                }
                break;
        }
    }

    private static IReadOnlySet<string> Add(IReadOnlySet<string> scope, string name) => new HashSet<string>(scope, StringComparer.Ordinal) { name };
    private static void AddPatternBindings(Pattern pattern, HashSet<string> scope)
    {
        switch (pattern)
        {
            case Pattern.Var variable: scope.Add(variable.Name); break;
            case Pattern.Constructor constructor: foreach (Pattern child in constructor.Patterns) AddPatternBindings(child, scope); break;
            case Pattern.Tuple tuple: foreach (Pattern child in tuple.Elements) AddPatternBindings(child, scope); break;
            case Pattern.Cons cons: AddPatternBindings(cons.Head, scope); AddPatternBindings(cons.Tail, scope); break;
        }
    }
}
