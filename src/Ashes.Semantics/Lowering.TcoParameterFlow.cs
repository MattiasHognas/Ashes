using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// For a recursive function, the source parameters whose value, or a part of it, each parameter
    /// position of a self-call may receive: a parameter's parts are the names a match on it (or on
    /// a name derived from it) binds and the lets bound to them. Null when some reference to the
    /// function is not a full self-call this walk recognizes, or the body holds an expression it
    /// does not know, so nothing is claimed about the flow.
    /// </summary>
    private static Dictionary<int, HashSet<int>>? CollectSelfCallParameterFlow(string selfName, List<string> parameters, Expr body)
    {
        var derived = parameters.Select(parameter => new HashSet<string>(StringComparer.Ordinal) { parameter }).ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Expr? node in EnumerateFlowNodes(body))
            {
                changed |= node is not null && AddDerivedBinders(node, derived);
            }
        }

        var flow = new Dictionary<int, HashSet<int>>();
        int selfReferences = 0;
        int recognizedCalls = 0;
        foreach (Expr? node in EnumerateFlowNodes(body))
        {
            if (node is null)
            {
                return null;
            }

            if (node is Expr.Var { Name: var name } && string.Equals(name, selfName, StringComparison.Ordinal))
            {
                selfReferences++;
            }

            if (TryUnwindSelfCall(node, selfName, parameters.Count, out List<Expr> arguments))
            {
                recognizedCalls++;
                RecordSelfCallFlow(arguments, derived, flow);
            }
        }

        return selfReferences == recognizedCalls ? flow : null;
    }

    private static bool AddDerivedBinders(Expr node, List<HashSet<string>> derived)
    {
        bool changed = false;
        foreach (HashSet<string> names in derived)
        {
            switch (node)
            {
                case Expr.Match match when names.Any(name => ExprMentionsName(match.Value, name, 0)):
                    foreach (MatchCase matchCase in match.Cases)
                    {
                        HashSet<string> binders = new(StringComparer.Ordinal);
                        CollectPatternBinders(matchCase.Pattern, binders);
                        foreach (string binder in binders)
                        {
                            changed |= names.Add(binder);
                        }
                    }

                    break;
                case Expr.Let let when names.Any(name => ExprMentionsName(let.Value, name, 0)):
                    changed |= names.Add(let.Name);
                    break;
            }
        }

        return changed;
    }

    private static void RecordSelfCallFlow(List<Expr> arguments, List<HashSet<string>> derived, Dictionary<int, HashSet<int>> flow)
    {
        for (int position = 0; position < arguments.Count; position++)
        {
            for (int source = 0; source < derived.Count; source++)
            {
                if (derived[source].Any(name => ExprMentionsName(arguments[position], name, 0)))
                {
                    if (!flow.TryGetValue(position, out HashSet<int>? sources))
                    {
                        sources = [];
                        flow[position] = sources;
                    }

                    sources.Add(source);
                }
            }
        }
    }

    // A call applying the function to exactly its parameter count, one argument at a time.
    private static bool TryUnwindSelfCall(Expr node, string selfName, int parameterCount, out List<Expr> arguments)
    {
        arguments = [];
        Expr current = node;
        while (current is Expr.Call call)
        {
            arguments.Insert(0, call.Arg);
            current = call.Func;
        }

        return arguments.Count == parameterCount
            && current is Expr.Var { Name: var name }
            && string.Equals(name, selfName, StringComparison.Ordinal);
    }

    // Every expression in the body, outermost first; a null entry marks an expression the walk
    // does not know. A partial application of the function counts only its full call.
    private static IEnumerable<Expr?> EnumerateFlowNodes(Expr root)
    {
        var pending = new Stack<Expr>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Expr node = pending.Pop();
            yield return node;
            if (node is Expr.Call)
            {
                // Only the outermost call of an application chain is a node; its callee chain's
                // arguments are walked, its partial applications are not.
                Expr current = node;
                while (current is Expr.Call call)
                {
                    pending.Push(call.Arg);
                    current = call.Func;
                }

                pending.Push(current);
                continue;
            }

            IEnumerable<Expr>? children = FlowChildren(node);
            if (children is null)
            {
                yield return null;
                yield break;
            }

            foreach (Expr child in children)
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<Expr>? FlowChildren(Expr node) => node switch
    {
        Expr.Let let => [let.Value, let.Body],
        Expr.LetResult let => [let.Value, let.Body],
        Expr.LetRecursive let => [let.Value, let.Body],
        Expr.If conditional => [conditional.Cond, conditional.Then, conditional.Else],
        Expr.Lambda lambda => [lambda.Body],
        Expr.Match match => MatchFlowChildren(match),
        Expr.Var or Expr.QualifiedVar or Expr.IntLit or Expr.UIntLit or Expr.BigIntLit or Expr.FloatLit
            or Expr.StrLit or Expr.RuneLit or Expr.BoolLit => [],
        Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
            or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor or Expr.ShiftLeft
            or Expr.ShiftRight or Expr.BitwiseNot or Expr.LogicalNot or Expr.LogicalAnd or Expr.LogicalOr
            or Expr.GreaterThan or Expr.LessThan or Expr.GreaterOrEqual or Expr.LessOrEqual
            or Expr.Equal or Expr.NotEqual or Expr.Cons or Expr.ResultPipe or Expr.ResultMapErrorPipe or Expr.Await
            or Expr.TupleLit or Expr.ListLit or Expr.RecordLit or Expr.RecordUpdate => EnumerateChildren(node),
        _ => null,
    };

    private static List<Expr> MatchFlowChildren(Expr.Match match)
    {
        List<Expr> children = [match.Value];
        foreach (MatchCase matchCase in match.Cases)
        {
            children.Add(matchCase.Body);
            if (matchCase.Guard is not null)
            {
                children.Add(matchCase.Guard);
            }
        }

        return children;
    }
}
