using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Functions that may run inside a coroutine: those written inside an `async` body, those it
    // calls, and — where the async body makes a call this analysis cannot resolve — every escaped
    // function. A task creator that is not itself one of these keeps ordinary placement even though
    // the program uses async elsewhere.
    private const string AsyncBindingName = "async";

    private readonly HashSet<FuncKey> _maFunctionsMayExecuteInsideCoroutine = [];

    private void ClearCoroutineEffectAnalysis()
    {
        _maFunctionsMayExecuteInsideCoroutine.Clear();
    }

    private void ComputeCoroutineEffects(
        IReadOnlyDictionary<FuncKey, HashSet<FuncKey>> calleesByCaller,
        IReadOnlySet<FuncKey> unknownDynamicCallers)
    {
        SeedCoroutineEffects();
        PropagateCoroutineEffects(calleesByCaller, unknownDynamicCallers);
    }

    private void SeedCoroutineEffects()
    {
        foreach ((FuncKey enclosing, (_, Expr body)) in _maFuncs)
        {
            SeedCoroutineEffectsFromAsyncBodies(body, enclosing);
        }

        if (_maBody is { } entryBody)
        {
            SeedCoroutineEffectsFromAsyncBodies(entryBody, owner: null);
        }
    }

    private void SeedCoroutineEffectsFromAsyncBodies(object? node, FuncKey? owner)
    {
        if (node is null or string)
        {
            return;
        }

        if (node is Expr.Call { Func: Expr.Var { Name: AsyncBindingName } } asyncCall)
        {
            MarkFunctionsReachableFrom(asyncCall.Arg, owner, _maFunctionsMayExecuteInsideCoroutine);
        }

        foreach (object? child in HandlerEffectChildren(node))
        {
            SeedCoroutineEffectsFromAsyncBodies(child, owner);
        }
    }

    /// <summary>
    /// Marks everything an expression subtree can run into <paramref name="target"/>: a lambda
    /// written inside it, and a name it references that resolves to a function in the enclosing
    /// lexical scope. Referencing a function is treated as running it, which over-approximates rather
    /// than missing an indirect call. Shared by every whole-program reachability seed (async bodies,
    /// structured-parallelism worker callbacks — see Lowering.ParallelWorkerEffects.cs) that needs
    /// "what functions can this subtree cause to run."
    /// </summary>
    private void MarkFunctionsReachableFrom(object? node, FuncKey? owner, HashSet<FuncKey> target)
    {
        if (node is null or string)
        {
            return;
        }

        if (node is Expr.Lambda lambda && _maFunctionKeyByLambda.TryGetValue(lambda, out FuncKey nested))
        {
            target.Add(nested);
        }
        else if (node is Expr.Var variable && ResolveLexicalFunctionReference(variable.Name, owner) is { } referenced)
        {
            target.Add(referenced);
        }

        foreach (object? child in HandlerEffectChildren(node))
        {
            MarkFunctionsReachableFrom(child, owner, target);
        }
    }

    private FuncKey? ResolveLexicalFunctionReference(string name, FuncKey? owner)
    {
        if (owner is { } enclosing
            && _maFunctionScopes.TryGetValue(enclosing, out IReadOnlyDictionary<string, FuncKey>? scope)
            && scope.TryGetValue(name, out FuncKey lexical))
        {
            return lexical;
        }

        // A reference written in the entry expression has no enclosing function scope; the
        // globally-unambiguous name index is the available resolution there.
        return _maNameIndex.TryGetValue(name, out FuncKey topLevel) ? topLevel : null;
    }

    private void PropagateCoroutineEffects(
        IReadOnlyDictionary<FuncKey, HashSet<FuncKey>> calleesByCaller,
        IReadOnlySet<FuncKey> unknownDynamicCallers)
    {
        // Least fixpoint (via WholeProgramFixpoint): grow _maFunctionsMayExecuteInsideCoroutine
        // until a full pass adds nothing further — the same shape as PropagateLiveHandlerEffects.
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (FuncKey caller in _maFunctionsMayExecuteInsideCoroutine.ToArray())
            {
                if (calleesByCaller.TryGetValue(caller, out HashSet<FuncKey>? callees))
                {
                    changed |= AddFunctions(_maFunctionsMayExecuteInsideCoroutine, callees);
                }

                if (unknownDynamicCallers.Contains(caller))
                {
                    changed |= AddFunctions(_maFunctionsMayExecuteInsideCoroutine, _maEscaped);
                }
            }

            return changed;
        });
    }
}
