using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// For each full application in a loop body, the arguments whose value the call's result may
    /// hold: the flow walk follows only those into the result. A call with no entry passes every
    /// argument on, which is what every walk assumed before callees were consulted.
    /// </summary>
    private sealed class FlowCallPolicy(Dictionary<Expr, bool[]>? flowing)
    {
        public static readonly FlowCallPolicy All = new(null);

        public bool[]? FlowingArguments(Expr.Call call)
            => flowing is not null && flowing.TryGetValue(call, out bool[]? arguments) ? arguments : null;
    }

    /// <summary>
    /// For a recursive function, the source parameters whose value, or a part of it, each parameter
    /// position of a self-call may receive: a parameter's parts are the names a match on it (or on
    /// a name derived from it) binds and the lets bound to them. Null when some reference to the
    /// function is not a full self-call this walk recognizes, or the body holds an expression it
    /// does not know, so nothing is claimed about the flow.
    /// </summary>
    private static Dictionary<int, HashSet<int>>? CollectSelfCallParameterFlow(string selfName, List<string> parameters, Expr body, FlowCallPolicy policy)
    {
        var derived = parameters.Select(parameter => new HashSet<string>(StringComparer.Ordinal) { parameter }).ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Expr? node in EnumerateFlowNodes(body))
            {
                changed |= node is not null && AddDerivedBinders(node, derived, parameters, policy);
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
                RecordSelfCallFlow(arguments, derived, parameters, policy, flow);
            }
        }

        return selfReferences == recognizedCalls ? flow : null;
    }

    private static bool AddDerivedBinders(Expr node, List<HashSet<string>> derived, List<string> parameters, FlowCallPolicy policy)
    {
        bool changed = false;
        foreach (HashSet<string> names in derived)
        {
            switch (node)
            {
                case Expr.Match match when names.Any(name => FlowMentionsName(match.Value, name, parameters, policy, 0)):
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
                case Expr.Let let when names.Any(name => FlowMentionsName(let.Value, name, parameters, policy, 0)):
                    changed |= names.Add(let.Name);
                    break;
                case Expr.LetResult letResult when names.Any(name => FlowMentionsName(letResult.Value, name, parameters, policy, 0)):
                    changed |= names.Add(letResult.Name);
                    break;
                case Expr.LetRecursive letRecursive when names.Any(name => FlowMentionsName(letRecursive.Value, name, parameters, policy, 0)):
                    changed |= names.Add(letRecursive.Name);
                    break;
                case Expr.Call call:
                    changed |= AddLambdaApplicationBinders(call, names, parameters, policy);
                    break;
            }
        }

        return changed;
    }

    // A lambda applied where it is written binds each of its parameters to the argument at that
    // position: a parameter whose argument reads one of the names is derived from it.
    private static bool AddLambdaApplicationBinders(Expr.Call call, HashSet<string> names, List<string> parameters, FlowCallPolicy policy)
    {
        List<Expr> arguments = [];
        Expr current = call;
        while (current is Expr.Call inner)
        {
            arguments.Insert(0, inner.Arg);
            current = inner.Func;
        }

        bool changed = false;
        int position = 0;
        while (current is Expr.Lambda lambda && position < arguments.Count)
        {
            if (names.Any(name => FlowMentionsName(arguments[position], name, parameters, policy, 0)))
            {
                changed |= names.Add(lambda.ParamName);
            }

            current = lambda.Body;
            position++;
        }

        return changed;
    }

    // The names bound, anywhere in the body, from a value that reads one of the seed names or a
    // name derived from one, to a fixed point, the seeds included.
    private static HashSet<string> CollectDerivedNames(Expr body, IEnumerable<string> seeds, List<string> parameters, FlowCallPolicy policy)
    {
        HashSet<string> names = new(seeds, StringComparer.Ordinal);
        List<HashSet<string>> derived = [names];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Expr? node in EnumerateFlowNodes(body))
            {
                changed |= node is not null && AddDerivedBinders(node, derived, parameters, policy);
            }
        }

        return names;
    }

    // The parameter positions at which some full self call in the body passes a value reading one
    // of the names.
    private static HashSet<int> CollectSelfCallPositionsMentioning(string selfName, List<string> parameters, Expr body, HashSet<string> names, FlowCallPolicy policy)
    {
        HashSet<int> positions = [];
        foreach (Expr? node in EnumerateFlowNodes(body))
        {
            if (node is not null && TryUnwindSelfCall(node, selfName, parameters.Count, out List<Expr> arguments))
            {
                for (int position = 0; position < arguments.Count; position++)
                {
                    if (names.Any(name => FlowMentionsName(arguments[position], name, parameters, policy, 0)))
                    {
                        positions.Add(position);
                    }
                }
            }
        }

        return positions;
    }

    private static void RecordSelfCallFlow(List<Expr> arguments, List<HashSet<string>> derived, List<string> parameters, FlowCallPolicy policy, Dictionary<int, HashSet<int>> flow)
    {
        for (int position = 0; position < arguments.Count; position++)
        {
            for (int source = 0; source < derived.Count; source++)
            {
                if (derived[source].Any(name => FlowMentionsName(arguments[position], name, parameters, policy, 0)))
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

    // Whether a value computed by `expression` may hold `name`'s value or a part of it. An operator's
    // result holds nothing of its operands (a number, a truth value, a fresh string), except that a
    // string append grows a loop parameter on its left in place, so such an append is followed.
    private static bool FlowMentionsName(Expr expression, string name, List<string> parameters, FlowCallPolicy policy, int depth)
        => Environment.GetEnvironmentVariable("GRC_NO_OPERATORFLOW") is not null
            ? ExprMentionsName(expression, name, depth)
            : depth > 256 || expression switch
            {
                Expr.Add { Left: Expr.Var left } => parameters.Contains(left.Name, StringComparer.Ordinal) && string.Equals(left.Name, name, StringComparison.Ordinal),
                Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
                    or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor or Expr.ShiftLeft
                    or Expr.ShiftRight or Expr.BitwiseNot or Expr.LogicalNot or Expr.LogicalAnd or Expr.LogicalOr
                    or Expr.GreaterThan or Expr.LessThan or Expr.GreaterOrEqual or Expr.LessOrEqual
                    or Expr.Equal or Expr.NotEqual => false,
                Expr.Call call => CallFlowMentionsName(call, name, parameters, policy, depth),
                Expr.Cons or Expr.TupleLit or Expr.ListLit => EnumerateChildren(expression).Any(child => FlowMentionsName(child, name, parameters, policy, depth + 1)),
                _ => ExprMentionsName(expression, name, depth),
            };

    // A call's result holds what its callee and the arguments the policy admits hold; a call the
    // policy knows nothing about holds whatever any part of the application holds.
    private static bool CallFlowMentionsName(Expr.Call call, string name, List<string> parameters, FlowCallPolicy policy, int depth)
    {
        bool[]? flowing = policy.FlowingArguments(call);
        if (flowing is null)
        {
            return FlowMentionsName(call.Func, name, parameters, policy, depth + 1)
                || FlowMentionsName(call.Arg, name, parameters, policy, depth + 1);
        }

        List<Expr> arguments = [];
        Expr root = call;
        while (root is Expr.Call inner)
        {
            arguments.Insert(0, inner.Arg);
            root = inner.Func;
        }

        if (FlowMentionsName(root, name, parameters, policy, depth + 1))
        {
            return true;
        }

        for (int position = 0; position < arguments.Count && position < flowing.Length; position++)
        {
            if (flowing[position] && FlowMentionsName(arguments[position], name, parameters, policy, depth + 1))
            {
                return true;
            }
        }

        return false;
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

    // Computes the loop's self-call parameter flow: with every argument flowing at the loop's
    // creation, and under the policy its body admits once the specialization scan has run
    // (`routed`), which the placements after the body's lowering and the back-edge rules read.
    private void RecomputeTcoParameterFlow(TcoContext tco, bool routed)
    {
        if (tco.Body is not { } body)
        {
            return;
        }

        tco.FlowPolicy = routed ? CreateFlowCallPolicy(body) : FlowCallPolicy.All;
        tco.SelfCallParameterFlow = CollectSelfCallParameterFlow(tco.SelfName, tco.ParamNames, body, tco.FlowPolicy);
    }

    // The policy for a loop body: every full application whose callee's ownership summary is known
    // and unpoisoned flows only the arguments whose parameter its result may reach, and a call
    // routed to a reuse specialization flows only its accumulator, the keys and values it stores
    // being copied into the specialization's own region.
    private FlowCallPolicy CreateFlowCallPolicy(Expr body)
    {
        var flowing = new Dictionary<Expr, bool[]>(ReferenceEqualityComparer.Instance);
        foreach (Expr? node in EnumerateFlowNodes(body))
        {
            if (node is null)
            {
                break;
            }

            if (node is Expr.Call call && FlowingCallArguments(call) is { } arguments)
            {
                flowing[call] = arguments;
            }
        }

        return new FlowCallPolicy(flowing);
    }

    private bool[]? FlowingCallArguments(Expr.Call call)
    {
        List<Expr> arguments = [];
        Expr root = call;
        while (root is Expr.Call inner)
        {
            arguments.Insert(0, inner.Arg);
            root = inner.Func;
        }

        return SummaryFlowingArguments(root, arguments);
    }

    // The arguments a callee's result may reach, by its ownership summary: the trait evidence a
    // call passes ahead of the source arguments is always followed.
    private bool[]? SummaryFlowingArguments(Expr root, IReadOnlyList<Expr> arguments)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_CALLREACHFLOW") is not null
            || GetOwnershipSummaryForCallRoot(root) is not { ResultPoisoned: false } summary)
        {
            return null;
        }

        int dictionaries = 0;
        while (dictionaries < arguments.Count
            && arguments[dictionaries] is Expr.Var evidence
            && evidence.Name.StartsWith("__trait_evidence_", StringComparison.Ordinal))
        {
            dictionaries++;
        }

        if (arguments.Count - dictionaries != summary.Parameters.Count)
        {
            return null;
        }

        bool[] flowing = new bool[arguments.Count];
        for (int position = 0; position < arguments.Count; position++)
        {
            flowing[position] = position < dictionaries || summary.ResultReaches(summary.Parameters[position - dictionaries]);
        }

        return flowing;
    }
}
