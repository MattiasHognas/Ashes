using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // For every registered function, the parameters its result reaches on every path that returns:
    // returned as they are, stored into the value built, or handed to a callee whose own result
    // reaches the matching parameter the same way. The dual of the may-reach summary, computed as a
    // greatest fixpoint: every parameter is assumed reached until some returning path is found that
    // reaches it through nothing, so a recursive or mutually recursive call keeps the property it
    // would have had the callee been written in place. A path that never returns keeps nothing and
    // so cannot take the property away.
    private readonly Dictionary<FuncKey, HashSet<string>> _maResultMustReach = new();

    private void ComputeResultMustReach()
    {
        _maResultMustReach.Clear();
        foreach ((FuncKey key, (List<string> parameters, _)) in _maFuncs)
        {
            _maResultMustReach[key] = new HashSet<string>(parameters, StringComparer.Ordinal);
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((FuncKey key, (List<string> parameters, Expr body)) in _maFuncs)
            {
                HashSet<string> reached = _maResultMustReach[key];
                IReadOnlyDictionary<string, FuncKey> scope = _maFunctionScopes.GetValueOrDefault(key)
                    ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
                foreach (string parameter in parameters)
                {
                    if (reached.Contains(parameter) && !MustReach(body, parameter, scope))
                    {
                        reached.Remove(parameter);
                        changed = true;
                    }
                }
            }
        }
    }

    private bool MustReach(Expr expression, string name, IReadOnlyDictionary<string, FuncKey> scope)
    {
        switch (expression)
        {
            case Expr.Var variable:
                return string.Equals(variable.Name, name, StringComparison.Ordinal);
            case Expr.Lambda nested:
                return !string.Equals(nested.ParamName, name, StringComparison.Ordinal)
                    && MustReach(nested.Body, name, scope);
            case Expr.Let let:
                return !string.Equals(let.Name, name, StringComparison.Ordinal)
                    && MustReach(let.Body, name, RemoveFuncNames(scope, [let.Name]));
            case Expr.If conditional:
                return MustReach(conditional.Then, name, scope) && MustReach(conditional.Else, name, scope);
            case Expr.Match match:
                return match.Cases.Count > 0
                    && match.Cases.All(matchCase =>
                        !PatternBinds(matchCase.Pattern, name) && MustReach(matchCase.Body, name, scope));
            case Expr.RecordLit record:
                return record.Fields.Any(field => MustReach(field.Value, name, scope));
            case Expr.RecordUpdate update:
                return MustReach(update.Target, name, scope)
                    || update.Updates.Any(field => MustReach(field.Value, name, scope));
            case Expr.TupleLit tuple:
                return tuple.Elements.Any(element => MustReach(element, name, scope));
            case Expr.ListLit list:
                return list.Elements.Any(element => MustReach(element, name, scope));
            case Expr.Cons cons:
                return MustReach(cons.Head, name, scope) || MustReach(cons.Tail, name, scope);
            case Expr.Call:
                return CallMustReach(expression, name, scope);
            default:
                return false;
        }
    }

    private bool CallMustReach(Expr expression, string name, IReadOnlyDictionary<string, FuncKey> scope)
    {
        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(expression, arguments);
        if (root is not Expr.Var callee)
        {
            return false;
        }

        if (_constructorSymbols.ContainsKey(callee.Name))
        {
            return arguments.Any(argument => MustReach(argument, name, scope));
        }

        if (TryResolveFunctionKey(root, callee.Name, scope) is not { } key
            || !_maFuncs.TryGetValue(key, out var info)
            || arguments.Count != info.Params.Count
            || !_maResultMustReach.TryGetValue(key, out HashSet<string>? reached))
        {
            return false;
        }

        for (int index = 0; index < arguments.Count; index++)
        {
            if (reached.Contains(info.Params[index]) && MustReach(arguments[index], name, scope))
            {
                return true;
            }
        }

        return false;
    }

    // The must-reach table's answer for a callee resolved at lowering time by its label.
    private bool CalleeResultMustReachParameter(string label, int parameterIndex)
        => _functionKeyByLabel.TryGetValue(label, out FuncKey function)
            && _maFuncs.TryGetValue(function, out var callee)
            && parameterIndex < callee.Params.Count
            && _maResultMustReach.TryGetValue(function, out HashSet<string>? reached)
            && reached.Contains(callee.Params[parameterIndex]);
}
