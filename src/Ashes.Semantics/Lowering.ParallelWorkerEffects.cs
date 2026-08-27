using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Functions that may run as a structured-parallelism worker: the callback arguments passed to
    // Ashes.Task.Parallel.{map,reduce,mapGrained,reduceGrained} — the map function and, for reduce,
    // also the combine function (both the per-element worker step and the pairwise merge run on
    // worker threads; see LowerParallelReduceQueuedEmit/TryLowerParallelSpecializedCall in
    // Lowering.Reuse.cs) — those they call, and every escaped function where a callback's own body
    // makes a call this analysis cannot resolve. A callback is marked even at a call site that later
    // falls back to sequential execution (e.g. CanRunRightOnWorker rejects the concrete result type):
    // this whole-program pre-pass runs before that lowering-time decision is made, and over-
    // approximating is safe (it only forgoes an optimization), while under-approximating is not (see
    // the crash this exclusion exists to prevent, in project_ownership_provenance_arena_corruption_bug
    // memory: RcDup on a value from an already-torn-down worker arena).
    private static string[] ParallelWorkerCalleeNames => [
        ParallelMapName, ParallelReduceName, ParallelMapGrainedName, ParallelReduceGrainedName,
    ];

    private readonly HashSet<FuncKey> _maFunctionsMayExecuteAsParallelWorker = [];

    private void ClearParallelWorkerEffectAnalysis()
    {
        _maFunctionsMayExecuteAsParallelWorker.Clear();
    }

    private void ComputeParallelWorkerEffects(
        IReadOnlyDictionary<FuncKey, HashSet<FuncKey>> calleesByCaller,
        IReadOnlySet<FuncKey> unknownDynamicCallers)
    {
        SeedParallelWorkerEffects();
        PropagateParallelWorkerEffects(calleesByCaller, unknownDynamicCallers);
    }

    private void SeedParallelWorkerEffects()
    {
        foreach ((FuncKey enclosing, (_, Expr body)) in _maFuncs)
        {
            SeedParallelWorkerEffectsFromCalls(body, enclosing);
        }

        if (_maBody is { } entryBody)
        {
            SeedParallelWorkerEffectsFromCalls(entryBody, owner: null);
        }
    }

    private void SeedParallelWorkerEffectsFromCalls(object? node, FuncKey? owner)
    {
        if (node is null or string)
        {
            return;
        }

        if (node is Expr.Call call)
        {
            var arguments = new List<Expr>();
            Expr root = CollectCallArgs(call, arguments);
            if (ResolveSpecializableCalleeName(root) is { } calleeName
                && Array.IndexOf(ParallelWorkerCalleeNames, calleeName) >= 0)
            {
                foreach (Expr argument in arguments)
                {
                    MarkFunctionsReachableFrom(argument, owner, _maFunctionsMayExecuteAsParallelWorker);
                }
            }
        }

        foreach (object? child in HandlerEffectChildren(node))
        {
            SeedParallelWorkerEffectsFromCalls(child, owner);
        }
    }

    private void PropagateParallelWorkerEffects(
        IReadOnlyDictionary<FuncKey, HashSet<FuncKey>> calleesByCaller,
        IReadOnlySet<FuncKey> unknownDynamicCallers)
    {
        // Least fixpoint (via WholeProgramFixpoint): grow _maFunctionsMayExecuteAsParallelWorker
        // until a full pass adds nothing further — the same shape as PropagateCoroutineEffects.
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (FuncKey caller in _maFunctionsMayExecuteAsParallelWorker.ToArray())
            {
                if (calleesByCaller.TryGetValue(caller, out HashSet<FuncKey>? callees))
                {
                    changed |= AddFunctions(_maFunctionsMayExecuteAsParallelWorker, callees);
                }

                if (unknownDynamicCallers.Contains(caller))
                {
                    changed |= AddFunctions(_maFunctionsMayExecuteAsParallelWorker, _maEscaped);
                }
            }

            return changed;
        });
    }
}
