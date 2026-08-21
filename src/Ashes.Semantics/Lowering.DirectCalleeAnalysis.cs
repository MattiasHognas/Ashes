using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly Dictionary<Expr.Let, bool> _directCalleeOnlyByLet =
        new(ReferenceEqualityComparer.Instance);

    private void AnalyzeDirectCalleeOnlyUses(Expr expression)
    {
        var activeBindings = new Dictionary<string, Stack<Expr.Let?>>(StringComparer.Ordinal);
        VisitDirectCalleeUses(expression, activeBindings, allowDirectCallee: false);
    }

    private bool UsesLetNameOnlyAsDirectCallee(Expr.Let binding) =>
        _directCalleeOnlyByLet.GetValueOrDefault(binding);

    private void VisitDirectCalleeUses(
        Expr expression,
        Dictionary<string, Stack<Expr.Let?>> activeBindings,
        bool allowDirectCallee)
    {
        if (expression is Expr.Var variable)
        {
            MarkNonCalleeUse(variable.Name, activeBindings, allowDirectCallee);
            return;
        }

        if (expression is Expr.Call call)
        {
            VisitDirectCalleeUses(call.Func, activeBindings, allowDirectCallee: true);
            VisitDirectCalleeUses(call.Arg, activeBindings, allowDirectCallee: false);
            return;
        }

        if (VisitDirectCalleeBindingForm(expression, activeBindings))
        {
            return;
        }

        _ = MapChildExpressions(expression, child =>
        {
            VisitDirectCalleeUses(child, activeBindings, allowDirectCallee: false);
            return child;
        });
    }

    private void MarkNonCalleeUse(
        string name,
        Dictionary<string, Stack<Expr.Let?>> activeBindings,
        bool allowDirectCallee)
    {
        if (!allowDirectCallee
            && activeBindings.TryGetValue(name, out Stack<Expr.Let?>? bindings)
            && bindings.TryPeek(out Expr.Let? binding)
            && binding is not null)
        {
            _directCalleeOnlyByLet[binding] = false;
        }
    }

    private bool VisitDirectCalleeBindingForm(
        Expr expression,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        switch (expression)
        {
            case Expr.Let binding:
                VisitLetDirectCalleeUses(binding, activeBindings);
                return true;
            case Expr.LetResult binding:
                VisitDirectCalleeUses(binding.Value, activeBindings, allowDirectCallee: false);
                VisitShadowedBody(binding.Name, binding.Body, activeBindings);
                return true;
            case Expr.LetRecursive binding:
                VisitRecursiveDirectCalleeUses(binding, activeBindings);
                return true;
            case RecursiveGroupExpr group:
                VisitRecursiveGroupDirectCalleeUses(group, activeBindings);
                return true;
            case Expr.Lambda lambda:
                VisitShadowedBody(lambda.ParamName, lambda.Body, activeBindings);
                return true;
            case Expr.Match match:
                VisitMatchDirectCalleeUses(match, activeBindings);
                return true;
            case Expr.Handle handler:
                VisitHandlerDirectCalleeUses(handler, activeBindings);
                return true;
            default:
                return false;
        }
    }

    private void VisitLetDirectCalleeUses(
        Expr.Let binding,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        _directCalleeOnlyByLet[binding] = true;
        VisitDirectCalleeUses(binding.Value, activeBindings, allowDirectCallee: false);
        PushDirectCalleeBinding(binding.Name, binding, activeBindings);
        VisitDirectCalleeUses(binding.Body, activeBindings, allowDirectCallee: false);
        PopDirectCalleeBinding(binding.Name, activeBindings);
    }

    private void VisitRecursiveDirectCalleeUses(
        Expr.LetRecursive binding,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        PushDirectCalleeBinding(binding.Name, null, activeBindings);
        VisitDirectCalleeUses(binding.Value, activeBindings, allowDirectCallee: false);
        VisitDirectCalleeUses(binding.Body, activeBindings, allowDirectCallee: false);
        PopDirectCalleeBinding(binding.Name, activeBindings);
    }

    private void VisitRecursiveGroupDirectCalleeUses(
        RecursiveGroupExpr group,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        foreach ((string name, _) in group.Bindings)
        {
            PushDirectCalleeBinding(name, null, activeBindings);
        }

        foreach ((_, Expr value) in group.Bindings)
        {
            VisitDirectCalleeUses(value, activeBindings, allowDirectCallee: false);
        }

        VisitDirectCalleeUses(group.Body, activeBindings, allowDirectCallee: false);
        for (int index = group.Bindings.Count - 1; index >= 0; index--)
        {
            PopDirectCalleeBinding(group.Bindings[index].Name, activeBindings);
        }
    }

    private void VisitMatchDirectCalleeUses(
        Expr.Match match,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        VisitDirectCalleeUses(match.Value, activeBindings, allowDirectCallee: false);
        foreach (MatchCase matchCase in match.Cases)
        {
            IReadOnlyList<string> names = PatternBindings(matchCase.Pattern).ToArray();
            PushDirectCalleeBindings(names, activeBindings);
            if (matchCase.Guard is not null)
            {
                VisitDirectCalleeUses(matchCase.Guard, activeBindings, allowDirectCallee: false);
            }

            VisitDirectCalleeUses(matchCase.Body, activeBindings, allowDirectCallee: false);
            PopDirectCalleeBindings(names, activeBindings);
        }
    }

    private void VisitHandlerDirectCalleeUses(
        Expr.Handle handler,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        MarkActiveLetsAsNonCalleeOnly(activeBindings);
        VisitDirectCalleeUses(handler.Body, activeBindings, allowDirectCallee: false);
        foreach (HandlerArm arm in handler.Arms)
        {
            IReadOnlyList<string> names = arm.Parameters.SelectMany(PatternBindings).ToArray();
            PushDirectCalleeBindings(names, activeBindings);
            VisitDirectCalleeUses(arm.Body, activeBindings, allowDirectCallee: false);
            PopDirectCalleeBindings(names, activeBindings);
        }
    }

    private void MarkActiveLetsAsNonCalleeOnly(
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        foreach (Stack<Expr.Let?> bindings in activeBindings.Values)
        {
            foreach (Expr.Let? binding in bindings)
            {
                if (binding is not null)
                {
                    _directCalleeOnlyByLet[binding] = false;
                }
            }
        }
    }

    private void VisitShadowedBody(
        string name,
        Expr body,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        PushDirectCalleeBinding(name, null, activeBindings);
        VisitDirectCalleeUses(body, activeBindings, allowDirectCallee: false);
        PopDirectCalleeBinding(name, activeBindings);
    }

    private static void PushDirectCalleeBindings(
        IReadOnlyList<string> names,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        foreach (string name in names)
        {
            PushDirectCalleeBinding(name, null, activeBindings);
        }
    }

    private static void PopDirectCalleeBindings(
        IReadOnlyList<string> names,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        for (int index = names.Count - 1; index >= 0; index--)
        {
            PopDirectCalleeBinding(names[index], activeBindings);
        }
    }

    private static void PushDirectCalleeBinding(
        string name,
        Expr.Let? binding,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        if (!activeBindings.TryGetValue(name, out Stack<Expr.Let?>? bindings))
        {
            bindings = new Stack<Expr.Let?>();
            activeBindings.Add(name, bindings);
        }

        bindings.Push(binding);
    }

    private static void PopDirectCalleeBinding(
        string name,
        Dictionary<string, Stack<Expr.Let?>> activeBindings)
    {
        Stack<Expr.Let?> bindings = activeBindings[name];
        bindings.Pop();
        if (bindings.Count == 0)
        {
            activeBindings.Remove(name);
        }
    }
}
