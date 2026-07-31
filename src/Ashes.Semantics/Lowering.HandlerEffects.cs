using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly record struct OwnershipPlacementContext(
        bool MayExecuteUnderLiveHandlerPost)
    {
        public bool AllowsOrdinaryRc => !MayExecuteUnderLiveHandlerPost;
    }

    private readonly HashSet<FuncKey> _maFunctionsMayExecuteUnderLiveHandlerPost = [];
    private readonly Dictionary<Expr.Lambda, FuncKey> _maFunctionKeyByLambda =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SourceFunctionOrigin, OwnershipPlacementContext>
        _ownershipPlacementBySource = [];
    private readonly Dictionary<string, OwnershipPlacementContext>
        _ownershipPlacementByFunctionLabel = new(StringComparer.Ordinal);
    private bool _maEntryMayExecuteUnderLiveHandlerPost;
    private OwnershipPlacementContext _ownershipPlacementContext;

    private bool AllowsOrdinaryRcPlacement => _ownershipPlacementContext.AllowsOrdinaryRc;

    private void ClearHandlerEffectAnalysis()
    {
        _maFunctionsMayExecuteUnderLiveHandlerPost.Clear();
        _maFunctionKeyByLambda.Clear();
        _ownershipPlacementBySource.Clear();
        _ownershipPlacementByFunctionLabel.Clear();
        _maEntryMayExecuteUnderLiveHandlerPost = false;
        _ownershipPlacementContext = default;
    }

    private void ComputeLiveHandlerEffects()
    {
        Dictionary<FuncKey, HashSet<FuncKey>> calleesByCaller = BuildHandlerEffectCallGraph(
            out HashSet<FuncKey> entryCallees);
        HashSet<FuncKey> unknownDynamicCallers = SeedLiveHandlerEffects(
            out bool entryHasUnknownDynamicCall);
        PropagateLiveHandlerEffects(
            calleesByCaller,
            entryCallees,
            unknownDynamicCallers,
            entryHasUnknownDynamicCall);

        foreach ((FuncKey function, SourceFunctionOrigin source) in _maFunctionOrigins)
        {
            _ownershipPlacementBySource[source] = new OwnershipPlacementContext(
                _maFunctionsMayExecuteUnderLiveHandlerPost.Contains(function));
        }
    }

    private Dictionary<FuncKey, HashSet<FuncKey>> BuildHandlerEffectCallGraph(
        out HashSet<FuncKey> entryCallees)
    {
        Dictionary<FuncKey, HashSet<FuncKey>> calleesByCaller = _maFuncs.Keys.ToDictionary(
            function => function,
            _ => new HashSet<FuncKey>());
        entryCallees = [];
        foreach ((FuncKey callee, List<MoveCallSite> sites) in _maCallSites)
        {
            foreach (MoveCallSite site in sites)
            {
                if (site.Enclosing is { } caller)
                {
                    calleesByCaller.GetValueOrDefault(caller)?.Add(callee);
                }
                else
                {
                    entryCallees.Add(callee);
                }
            }
        }

        return calleesByCaller;
    }

    private HashSet<FuncKey> SeedLiveHandlerEffects(
        out bool entryHasUnknownDynamicCall)
    {
        var unknownDynamicCallers = new HashSet<FuncKey>();
        foreach ((FuncKey function, (List<string> parameters, Expr functionBody)) in _maFuncs)
        {
            if (ExpressionContainsHandleForOwner(functionBody, function))
            {
                _maFunctionsMayExecuteUnderLiveHandlerPost.Add(function);
            }

            if (ExpressionHasPotentialDynamicCall(functionBody, parameters, function))
            {
                unknownDynamicCallers.Add(function);
            }
        }

        _maEntryMayExecuteUnderLiveHandlerPost = _maBody is { } entryBody
            && ExpressionContainsHandleForOwner(entryBody, owner: null);
        entryHasUnknownDynamicCall = _maBody is { } entryExpression
            && ExpressionHasPotentialDynamicCall(entryExpression, [], owner: null);
        return unknownDynamicCallers;
    }

    private void PropagateLiveHandlerEffects(
        IReadOnlyDictionary<FuncKey, HashSet<FuncKey>> calleesByCaller,
        IReadOnlySet<FuncKey> entryCallees,
        IReadOnlySet<FuncKey> unknownDynamicCallers,
        bool entryHasUnknownDynamicCall)
    {
        bool changed;
        do
        {
            changed = false;
            if (_maEntryMayExecuteUnderLiveHandlerPost)
            {
                changed |= AddFunctions(
                    _maFunctionsMayExecuteUnderLiveHandlerPost,
                    entryCallees);
                if (entryHasUnknownDynamicCall)
                {
                    changed |= AddFunctions(
                        _maFunctionsMayExecuteUnderLiveHandlerPost,
                        _maEscaped);
                }
            }

            foreach (FuncKey caller in _maFunctionsMayExecuteUnderLiveHandlerPost.ToArray())
            {
                if (calleesByCaller.TryGetValue(caller, out HashSet<FuncKey>? callees))
                {
                    changed |= AddFunctions(
                        _maFunctionsMayExecuteUnderLiveHandlerPost,
                        callees);
                }

                if (unknownDynamicCallers.Contains(caller))
                {
                    changed |= AddFunctions(
                        _maFunctionsMayExecuteUnderLiveHandlerPost,
                        _maEscaped);
                }
            }
        }
        while (changed);
    }

    private static bool AddFunctions(HashSet<FuncKey> target, IEnumerable<FuncKey> functions)
    {
        bool changed = false;
        foreach (FuncKey function in functions)
        {
            changed |= target.Add(function);
        }

        return changed;
    }

    private OwnershipPlacementContext EnterFunctionOwnershipPlacement(
        IrFunctionOrigin origin,
        string label,
        OwnershipPlacementContext enclosing)
    {
        OwnershipPlacementContext context = origin.Source is { } source
            && _ownershipPlacementBySource.TryGetValue(source, out OwnershipPlacementContext exact)
                ? exact
                : origin.ParentGeneratedLabel is { } parent
                    && _ownershipPlacementByFunctionLabel.TryGetValue(
                        parent,
                        out OwnershipPlacementContext generatedParent)
                        ? generatedParent
                        : enclosing;
        _ownershipPlacementByFunctionLabel[label] = context;
        return context;
    }

    private bool ExpressionContainsHandleForOwner(object? node, FuncKey? owner)
    {
        if (node is null or string)
        {
            return false;
        }

        if (node is Expr.Handle)
        {
            return true;
        }

        if (node is Expr.Lambda lambda
            && _maFunctionKeyByLambda.TryGetValue(lambda, out FuncKey nested)
            && (!owner.HasValue || !nested.Equals(owner.Value)))
        {
            return false;
        }

        return HandlerEffectChildren(node).Any(child =>
            ExpressionContainsHandleForOwner(child, owner));
    }

    private bool ExpressionHasPotentialDynamicCall(
        Expr expression,
        IReadOnlyList<string> parameters,
        FuncKey? owner)
    {
        var possibleCallees = new HashSet<string>(parameters, StringComparer.Ordinal);
        CollectPotentialDynamicCalleeNames(expression, possibleCallees, owner);
        return CallsPotentialDynamicCallee(expression, possibleCallees, owner);
    }

    private void CollectPotentialDynamicCalleeNames(
        object? node,
        HashSet<string> names,
        FuncKey? owner)
    {
        if (node is null or string)
        {
            return;
        }

        if (node is Expr.Lambda lambda)
        {
            if (_maFunctionKeyByLambda.TryGetValue(lambda, out FuncKey nested)
                && (!owner.HasValue || !nested.Equals(owner.Value)))
            {
                return;
            }

            names.Add(lambda.ParamName);
        }
        else if (node is Expr.Let let
            && FindInnermostLambdaUnderLets(let.Value) is null)
        {
            names.Add(let.Name);
        }
        else if (node is Expr.LetRecursive recursive
            && FindInnermostLambdaUnderLets(recursive.Value) is null)
        {
            names.Add(recursive.Name);
        }
        else if (node is Expr.LetResult result
            && FindInnermostLambdaUnderLets(result.Value) is null)
        {
            names.Add(result.Name);
        }
        else if (node is Pattern.Var variable)
        {
            names.Add(variable.Name);
        }

        foreach (object? child in HandlerEffectChildren(node))
        {
            CollectPotentialDynamicCalleeNames(child, names, owner);
        }
    }

    private bool CallsPotentialDynamicCallee(
        object? node,
        IReadOnlySet<string> names,
        FuncKey? owner)
    {
        if (node is null or string)
        {
            return false;
        }

        if (node is Expr.Lambda lambda
            && _maFunctionKeyByLambda.TryGetValue(lambda, out FuncKey nested)
            && (!owner.HasValue || !nested.Equals(owner.Value)))
        {
            return false;
        }

        if (node is Expr.Call call)
        {
            var arguments = new List<Expr>();
            Expr root = CollectCallArgs(call, arguments);
            if (root is Expr.Var variable && names.Contains(variable.Name))
            {
                return true;
            }
        }

        return HandlerEffectChildren(node).Any(child =>
            CallsPotentialDynamicCallee(child, names, owner));
    }

    private static IEnumerable<object?> HandlerEffectChildren(object node)
    {
        if (node is System.Runtime.CompilerServices.ITuple tuple)
        {
            for (int index = 0; index < tuple.Length; index++)
            {
                yield return tuple[index];
            }

            yield break;
        }

        if (node is System.Collections.IEnumerable sequence && node is not string)
        {
            foreach (object? item in sequence)
            {
                yield return item;
            }

            yield break;
        }

        if (node is not (Expr or Pattern or MatchCase or HandlerArm))
        {
            yield break;
        }

        foreach (System.Reflection.PropertyInfo property in node.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            Type type = property.PropertyType;
            if (typeof(Expr).IsAssignableFrom(type)
                || typeof(Pattern).IsAssignableFrom(type)
                || typeof(MatchCase).IsAssignableFrom(type)
                || typeof(HandlerArm).IsAssignableFrom(type)
                || typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                    && type != typeof(string))
            {
                yield return property.GetValue(node);
            }
        }
    }
}
