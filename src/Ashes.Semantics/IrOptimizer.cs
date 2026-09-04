namespace Ashes.Semantics;

/// <summary>
/// IR-level optimization pass pipeline.
/// Runs after semantic lowering, before the backend.
/// All optimizations are invisible to the user — observable behaviour is identical.
/// </summary>
public static partial class IrOptimizer
{
    /// <summary>
    /// Runs the full optimization pipeline on the given IR program.
    /// Returns a new IrProgram with optimized instructions.
    /// </summary>
    public static IrProgram Optimize(IrProgram program)
    {
        // Aggressive compile-time evaluation runs first: it reduces pure, constant-argument
        // calls to constants, after which the per-function passes below eliminate the now-dead
        // argument/closure construction and the redundant arena brackets around the removed call.
        program = IrCompileTimeEval.Evaluate(program);

        // Local CSE (below) needs to know which CallKnown targets are provably pure; reuse the
        // same whole-program purity oracle IrCompileTimeEval.Evaluate just computed for folding,
        // rather than building a second one.
        var functionsByLabel = new Dictionary<string, IrFunction>(StringComparer.Ordinal)
        {
            [program.EntryFunction.Label] = program.EntryFunction,
        };
        foreach (var f in program.Functions)
        {
            functionsByLabel[f.Label] = f;
        }
        var evaluableFunctions = IrCompileTimeEval.ComputeEvaluableFunctions(functionsByLabel);

        var optimizedEntry = OptimizeFunction(program.EntryFunction, evaluableFunctions);
        var optimizedFuncs = program.Functions.Select(f => OptimizeFunction(f, evaluableFunctions)).ToList();

        (optimizedEntry, optimizedFuncs) = RunInterproceduralClosurePasses(optimizedEntry, optimizedFuncs);

        // Interprocedural: strip arena save/restore/reclaim brackets that provably guard no
        // allocation. Runs after the per-function passes so devirtualized calls (CallKnown) and
        // dead MakeClosures are already resolved, and needs whole-program non-allocation
        // summaries for known callees.
        var nonAllocating = ComputeNonAllocatingFunctions(optimizedEntry, optimizedFuncs);
        optimizedEntry = StripRedundantArenaBrackets(optimizedEntry, nonAllocating);
        optimizedFuncs = optimizedFuncs.Select(f => StripRedundantArenaBrackets(f, nonAllocating)).ToList();

        // Last: folds a left-nested ConcatStr chain into one ConcatStrN. Placed after every pass
        // above (rather than in the per-function pipeline) so ComputeNonAllocatingFunctions/
        // StripRedundantArenaBrackets — and every other pass — only ever see plain ConcatStr, the
        // one instruction shape they already know how to reason about; only the backend needs to
        // learn ConcatStrN, not the rest of this pipeline.
        optimizedEntry = FoldConcatStrChains(optimizedEntry);
        optimizedFuncs = optimizedFuncs.Select(FoldConcatStrChains).ToList();

        return program with
        {
            EntryFunction = optimizedEntry,
            Functions = optimizedFuncs,
        };
    }

    // The whole-program closure passes, in dependency order.
    private static (IrFunction Entry, List<IrFunction> Functions) RunInterproceduralClosurePasses(
        IrFunction entry, List<IrFunction> functions)
    {
        // A call through a captured closure whose label every creation site of the enclosing
        // function agrees on becomes direct (a stitched module's alias bindings are the common
        // case), then a saturated chain of now-direct curried stages collapses into one call with a
        // caller-frame environment. Both run before scalarization, whose target shape (a stack
        // environment feeding a devirtualized CallKnown) the stage inlining produces.
        (entry, functions) = DevirtualizeCapturedClosureCalls(entry, functions);
        (entry, functions) = DevirtualizeReturnedClosureCalls(entry, functions);
        (entry, functions) = InlineCurryingStages(entry, functions);

        // Skip the environment allocation entirely for a single-scalar-capture stack closure whose
        // only use is already a devirtualized CallKnown. Runs after the per-function passes so
        // devirtualization has already resolved CallClosure -> CallKnown and dead-code elimination
        // has already swept the now-unused MakeClosureStack, leaving the residual
        // AllocStack/StoreMemOffset/CallKnown shape this pass looks for. May append newly generated
        // scalar-parameter callee variants to the function list, so it runs before the
        // non-allocation summary (a scalarized callee is strictly less allocating, never more).
        (entry, functions) = ScalarizeSingleCaptureStackClosures(entry, functions);

        // Devirtualize a CallClosure whose closure temp reaches a CallKnown to a function already
        // proven to always return one specific closure label (a curried call's second and later
        // applications — DevirtualizeKnownClosureCalls only looks at a MakeClosure definition, never
        // at what a called function is known to return). Runs again after scalarization (its own
        // scalarization target shape is unaffected by this) and before the non-allocation summary,
        // so a newly-direct call is visible to it.
        return DevirtualizeReturnedClosureCalls(entry, functions);
    }

    // String-concatenation chain folding
    // A left-nested chain of ConcatStr calls (`((a ++ b) ++ c) ++ d`) pays one allocation and one
    // growing copy per link — n-1 allocations and O(n^2) total bytes copied for n parts. When every
    // intermediate result is used exactly once, and that one use is as the Left operand of the next
    // link in the chain, the whole chain can be folded into a single ConcatStrN that allocates once
    // for the sum of every part's length and copies each part directly into its final position.
    private static IrFunction FoldConcatStrChains(IrFunction function)
    {
        List<IrInst>? rewritten = TryFoldConcatStrChains(function.Instructions);
        return rewritten is null ? function : function with { Instructions = rewritten };
    }

    private static List<IrInst>? TryFoldConcatStrChains(List<IrInst> instructions)
    {
        (var defCount, var defIndex, var useCount) = ComputeTempDefUseFacts(instructions);

        // A temp used exactly once as the Left operand of a ConcatStr is an inner link of some
        // chain, never a fold root on its own — it will be absorbed when its consumer is processed.
        var consumedAsConcatLeft = new HashSet<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.ConcatStr c && useCount.GetValueOrDefault(c.Left) == 1)
            {
                consumedAsConcatLeft.Add(c.Left);
            }
        }

        var toRemove = new HashSet<int>();
        var rewrites = new Dictionary<int, List<IrInst>>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.ConcatStr root || consumedAsConcatLeft.Contains(root.Target))
            {
                continue;
            }

            TryFoldConcatChainAt(instructions, defCount, defIndex, useCount, i, root, toRemove, rewrites);
        }

        if (rewrites.Count == 0)
        {
            return null;
        }

        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i))
            {
                continue;
            }
            if (rewrites.TryGetValue(i, out List<IrInst>? replacement))
            {
                result.AddRange(replacement);
            }
            else
            {
                result.Add(instructions[i]);
            }
        }
        return result;
    }

    // Attempts to fold the chain rooted at `root` (index `rootIndex`) into rewrites[rootIndex],
    // marking every absorbed inner link's index in toRemove. Leaves both untouched when the chain
    // is too short to be worth folding, or when the arena/control-flow safety check declines it.
    private static void TryFoldConcatChainAt(
        List<IrInst> instructions,
        Dictionary<int, int> defCount,
        Dictionary<int, int> defIndex,
        Dictionary<int, int> useCount,
        int rootIndex,
        IrInst.ConcatStr root,
        HashSet<int> toRemove,
        Dictionary<int, List<IrInst>> rewrites)
    {
        List<int> chainIndices = CollectConcatChainIndices(instructions, defCount, defIndex, useCount, rootIndex, root);
        if (chainIndices.Count < 2)
        {
            return;
        }

        int innermostLeft = ((IrInst.ConcatStr)instructions[chainIndices[^1]]).Left;
        int scanStart = defIndex.GetValueOrDefault(innermostLeft, chainIndices[^1]);
        if (RangeContainsArenaOrControlFlow(instructions, scanStart, rootIndex))
        {
            // Folding would delay reading an earlier part past a later part's arena
            // save/restore/reclaim bracket (each inlined helper call scopes its own), which can
            // reclaim and overwrite the earlier part's still-unread memory before this instruction
            // gets to read it — found only by running the compiled output of a multi-part chain
            // built from inlined helper calls, not by this pass's own hand-built raw-IR unit
            // tests. Decline rather than risk reading reclaimed memory.
            return;
        }

        List<int>? sunkReleases = CollectSinkableReleases(instructions, scanStart, rootIndex);
        if (sunkReleases is null)
        {
            return;
        }

        var parts = new List<int> { innermostLeft };
        for (int k = chainIndices.Count - 1; k >= 0; k--)
        {
            parts.Add(((IrInst.ConcatStr)instructions[chainIndices[k]]).Right);
        }
        for (int k = 1; k < chainIndices.Count; k++)
        {
            toRemove.Add(chainIndices[k]);
        }
        var replacement = new List<IrInst>(1 + sunkReleases.Count)
        {
            new IrInst.ConcatStrN(root.Target, parts, root.RuntimeManaged),
        };
        foreach (int releaseIndex in sunkReleases)
        {
            toRemove.Add(releaseIndex);
            replacement.Add(instructions[releaseIndex]);
        }
        rewrites[rootIndex] = replacement;
    }

    // The lifetime placement releases an owned part right after its last read, which in the
    // unfolded chain is the link that consumes it: an RcDrop between two links. The fold reads
    // every part at the root, so each such release moves to just after the folded instruction, in
    // its original order; left in place it would free the part before the fold reads it (an
    // unmapped block for any string past the RC cache size). A release-like instruction that
    // yields a value or closes a resource (DropReuse, CleanupResource) is not moved; its presence
    // declines the fold.
    private static List<int>? CollectSinkableReleases(List<IrInst> instructions, int fromIndex, int toIndex)
    {
        var releases = new List<int>();
        for (int i = fromIndex; i < toIndex; i++)
        {
            switch (instructions[i])
            {
                case IrInst.RcDrop:
                    releases.Add(i);
                    break;
                case IrInst.DropReuse or IrInst.CleanupResource:
                    return null;
            }
        }
        return releases;
    }

    // A part read this instruction's position (rather than immediately after it was computed, as
    // in the original unfolded chain) is unsafe if an arena bracket between the two could have
    // reclaimed the bump-allocator memory that part's string lives in, or if a branch/label makes
    // "between the two" not a genuine straight-line span. Conservative on purpose: this does not
    // attempt to prove a specific part is RC-managed (never arena-reclaimed) or that a specific
    // reclaim's range excludes a specific part's address — it declines the whole fold instead.
    private static bool RangeContainsArenaOrControlFlow(List<IrInst> instructions, int fromIndex, int toIndex)
    {
        for (int i = fromIndex; i <= toIndex; i++)
        {
            if (instructions[i] is IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag
                or IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks
                or IrInst.SaveStackPointer or IrInst.RestoreStackPointer)
            {
                return true;
            }
        }
        return false;
    }

    // Walks backward from a chain's outermost ConcatStr (`root`, at index `rootIndex`) via each
    // link's Left operand, for as long as that operand is itself defined by exactly one ConcatStr
    // with a single use (this same link) and a matching RuntimeManaged flag. Returns the visited
    // instruction indices in outermost-to-innermost order.
    private static List<int> CollectConcatChainIndices(
        List<IrInst> instructions,
        Dictionary<int, int> defCount,
        Dictionary<int, int> defIndex,
        Dictionary<int, int> useCount,
        int rootIndex,
        IrInst.ConcatStr root)
    {
        var chainIndices = new List<int>();
        int current = rootIndex;
        IrInst.ConcatStr link = root;
        while (true)
        {
            chainIndices.Add(current);
            if (defCount.GetValueOrDefault(link.Left) == 1
                && useCount.GetValueOrDefault(link.Left) == 1
                && defIndex.TryGetValue(link.Left, out int leftDefIndex)
                && instructions[leftDefIndex] is IrInst.ConcatStr innerLink
                && innerLink.RuntimeManaged == link.RuntimeManaged)
            {
                current = leftDefIndex;
                link = innerLink;
                continue;
            }
            return chainIndices;
        }
    }

    // Devirtualization past a single-hop reaching definition (single-agreeing-label case only —
    // a 2-4-label lambda-set-specialization dispatch is not implemented here).
    // DevirtualizeKnownClosureCalls above only recognizes a
    // MakeClosure/MakeClosureStack definition, so a curried call like `add(10)(32)` never
    // devirtualizes its second application: add(10)'s result temp is defined by a CallKnown (the
    // first application, already devirtualized above), not a MakeClosure. This computes, per
    // function and via a whole-program least fixpoint, whether every Return in a function's body is
    // provably the exact same closure label — directly from a heap MakeClosure (never
    // MakeClosureStack: a stack closure's environment lives in its defining function's own frame,
    // which is gone once that function returns, so treating one as a function's "known returned
    // label" would let a later caller read a dangling pointer), or transitively through a CallKnown
    // to another function already proven, earlier in the fixpoint, to always return that same label.
    // A CallClosure whose closure temp reaches such a CallKnown is then rewritten to an explicit
    // environment-field extraction (LoadMemOffset at the closure object's fixed env offset, matching
    // EmitCallClosure's own field layout) plus a direct CallKnown — safe regardless of the closure
    // object's own ownership/RC treatment, since extracting a field is a plain read that neither
    // consumes nor extends its lifetime, and it happens at the exact instruction position the
    // original (consuming) CallClosure occupied, so existing drop placement for the closure temp is
    // unaffected. Iterates each function to its own local fixpoint so a deeper curry (three or more
    // arguments) fully resolves, not just its first newly-direct hop.
    private static (IrFunction Entry, List<IrFunction> Functions) DevirtualizeReturnedClosureCalls(
        IrFunction entry, List<IrFunction> functions)
    {
        Dictionary<string, string> knownReturnedLabel = ComputeKnownReturnedClosureLabels(functions);
        if (knownReturnedLabel.Count == 0)
        {
            return (entry, functions);
        }

        return (
            DevirtualizeReturnedClosureCallsInFunction(entry, knownReturnedLabel),
            functions.Select(f => DevirtualizeReturnedClosureCallsInFunction(f, knownReturnedLabel)).ToList());
    }

    private static Dictionary<string, string> ComputeKnownReturnedClosureLabels(IReadOnlyList<IrFunction> functions)
    {
        var knownLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (IrFunction f in functions)
            {
                if (knownLabel.ContainsKey(f.Label))
                {
                    continue;
                }

                if (TryDetermineKnownReturnedClosureLabel(f, knownLabel) is { } label)
                {
                    knownLabel[f.Label] = label;
                    changed = true;
                }
            }

            return changed;
        });

        return knownLabel;
    }

    private static string? TryDetermineKnownReturnedClosureLabel(
        IrFunction function, Dictionary<string, string> knownLabel)
    {
        (var defCount, var defIndex, _) = ComputeTempDefUseFacts(function.Instructions);
        string? result = null;
        bool sawReturn = false;
        foreach (IrInst inst in function.Instructions)
        {
            if (inst is not IrInst.Return ret)
            {
                continue;
            }

            sawReturn = true;
            string? returnedLabel = TryGetKnownClosureLabel(ret.Source, function.Instructions, defCount, defIndex, knownLabel);
            if (returnedLabel is null)
            {
                return null;
            }

            if (result is not null && !string.Equals(result, returnedLabel, StringComparison.Ordinal))
            {
                return null;
            }

            result = returnedLabel;
        }

        return sawReturn ? result : null;
    }

    private static string? TryGetKnownClosureLabel(
        int sourceTemp,
        List<IrInst> instructions,
        Dictionary<int, int> defCount,
        Dictionary<int, int> defIndex,
        Dictionary<string, string> knownLabel)
    {
        if (defCount.GetValueOrDefault(sourceTemp) != 1 || !defIndex.TryGetValue(sourceTemp, out int index))
        {
            return null;
        }

        return instructions[index] switch
        {
            IrInst.MakeClosure mk => mk.FuncLabel,
            IrInst.CallKnown ck => knownLabel.GetValueOrDefault(ck.FuncLabel),
            _ => null,
        };
    }

    private static IrFunction DevirtualizeReturnedClosureCallsInFunction(
        IrFunction function, Dictionary<string, string> knownReturnedLabel)
    {
        bool changed;
        do
        {
            (function, changed) = DevirtualizeReturnedClosureCallsOnce(function, knownReturnedLabel);
        }
        while (changed);

        return function;
    }

    private static (IrFunction, bool) DevirtualizeReturnedClosureCallsOnce(
        IrFunction function, Dictionary<string, string> knownReturnedLabel)
    {
        (var defCount, var defIndex, _) = ComputeTempDefUseFacts(function.Instructions);
        List<IrInst> instructions = function.Instructions;
        var result = new List<IrInst>(instructions.Count);
        int nextTemp = function.TempCount;
        bool changed = false;

        foreach (IrInst inst in instructions)
        {
            if (inst is IrInst.CallClosure cc
                && defCount.GetValueOrDefault(cc.ClosureTemp) == 1
                && defIndex.TryGetValue(cc.ClosureTemp, out int defAt)
                && instructions[defAt] is IrInst.CallKnown known
                && knownReturnedLabel.TryGetValue(known.FuncLabel, out string? label))
            {
                int envTemp = nextTemp++;
                result.Add(new IrInst.LoadMemOffset(envTemp, cc.ClosureTemp, 8));
                result.Add(new IrInst.CallKnown(
                    cc.Target, label, envTemp, cc.ArgTemp, cc.RuntimeManagedArgumentFlagTemp,
                    EnvironmentIsStackAllocated: false)
                { Location = cc.Location });
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? (function with { Instructions = result, TempCount = nextTemp }, true) : (function, false);
    }

    // Closure environment scalarization
    // A stack-allocated closure with one or two 8-byte scalar captures, whose only use is already a
    // devirtualized CallKnown (EnvironmentIsStackAllocated: true), packs those values through an
    // AllocStack + StoreMemOffset + LoadMemOffset round trip even though they never need to leave a
    // register. When the callee only touches its environment through LoadEnv, the round trip is
    // unnecessary: the captured values are passed directly in the existing CallKnown ABI (env, arg,
    // and the runtime-managed-argument flag word) and the callee rewritten to read them directly.
    // The first capture travels in the "env" word; a second capture travels in the flag word, which
    // is free whenever the call passes no ownership flag and the callee never reads one
    // (LoadArgumentOwnership is a raw read of that same parameter, so the variant reads the second
    // capture through it).
    //
    // A new callee variant is generated per eligible target label and capture count (memoized
    // across call sites) rather than rewriting the original callee in place: the same label may
    // still be used elsewhere in a way that needs the pointer-based form (an escaping or
    // non-devirtualized use), and safety here does not depend on proving there is no such other
    // use — the original function is always left completely untouched.
    //
    // Scope: at most two scalar captures, because that is what the shared 3-word signature can
    // carry without an environment pointer. Three or more would need a direct-call-only calling
    // convention with a per-function parameter list, a materially larger change.
    private static (IrFunction Entry, List<IrFunction> Functions) ScalarizeSingleCaptureStackClosures(
        IrFunction entry, List<IrFunction> functions)
    {
        var functionsByLabel = new Dictionary<string, IrFunction>(StringComparer.Ordinal);
        foreach (IrFunction f in functions)
        {
            functionsByLabel[f.Label] = f;
        }

        var cloneLabelByOriginal = new Dictionary<string, string?>(StringComparer.Ordinal);
        var newFunctions = new List<IrFunction>();
        int cloneCounter = 0;

        IrFunction RewriteCaller(IrFunction caller)
        {
            IrFunction? rewritten = TryScalarizeCallSites(
                caller, functionsByLabel, cloneLabelByOriginal, newFunctions, ref cloneCounter);
            return rewritten ?? caller;
        }

        IrFunction newEntry = RewriteCaller(entry);
        var newFuncs = functions.Select(RewriteCaller).ToList();
        newFuncs.AddRange(newFunctions);

        return (newEntry, newFuncs);
    }

    private static IrFunction? TryScalarizeCallSites(
        IrFunction caller,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, string?> cloneLabelByOriginal,
        List<IrFunction> newFunctions,
        ref int cloneCounter)
    {
        List<IrInst> instructions = caller.Instructions;
        (HashSet<int> toRemove, Dictionary<int, IrInst> rewrites) = FindEligibleScalarEnvCallSites(
            instructions, functionsByLabel, cloneLabelByOriginal, newFunctions, ref cloneCounter);

        if (rewrites.Count == 0)
        {
            return null;
        }

        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i))
            {
                continue;
            }

            result.Add(rewrites.TryGetValue(i, out IrInst? replacement) ? replacement : instructions[i]);
        }

        return caller with { Instructions = result };
    }

    private static (HashSet<int> ToRemove, Dictionary<int, IrInst> Rewrites) FindEligibleScalarEnvCallSites(
        List<IrInst> instructions,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, string?> cloneLabelByOriginal,
        List<IrFunction> newFunctions,
        ref int cloneCounter)
    {
        (var defCount, var defIndex, var useCount) = ComputeTempDefUseFacts(instructions);

        var storeIndexByEnvPtrAndOffset = new Dictionary<(int BasePtr, int OffsetBytes), int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.StoreMemOffset store)
            {
                storeIndexByEnvPtrAndOffset[(store.BasePtr, store.OffsetBytes)] = i;
            }
        }

        var toRemove = new HashSet<int>();
        var rewrites = new Dictionary<int, IrInst>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.CallKnown { EnvironmentIsStackAllocated: true } call
                || !TryMatchScalarEnvCallSite(
                    instructions, call, defCount, defIndex, useCount, storeIndexByEnvPtrAndOffset,
                    out int allocIndex, out int[] storeIndices)
                || !functionsByLabel.TryGetValue(call.FuncLabel, out IrFunction? callee))
            {
                continue;
            }

            string? cloneLabel = GetOrCreateScalarEnvVariant(
                callee, storeIndices.Length, functionsByLabel, cloneLabelByOriginal, newFunctions, ref cloneCounter);
            if (cloneLabel is null)
            {
                continue;
            }

            toRemove.Add(allocIndex);
            foreach (int storeIndex in storeIndices)
            {
                toRemove.Add(storeIndex);
            }

            rewrites[i] = call with
            {
                FuncLabel = cloneLabel,
                EnvTemp = ((IrInst.StoreMemOffset)instructions[storeIndices[0]]).Source,
                RuntimeManagedArgumentFlagTemp = storeIndices.Length == 2
                    ? ((IrInst.StoreMemOffset)instructions[storeIndices[1]]).Source
                    : call.RuntimeManagedArgumentFlagTemp,
                EnvironmentIsStackAllocated = false,
            };
        }

        return (toRemove, rewrites);
    }

    // Matches the caller-side shape: the env pointer is defined once by an 8- or 16-byte AllocStack,
    // filled by exactly one store per 8-byte capture, and used nowhere else but those stores and
    // this call (so there is no other read and no escape). A second capture can only travel in the
    // flag word when this call does not already pass an ownership flag.
    private static bool TryMatchScalarEnvCallSite(
        List<IrInst> instructions,
        IrInst.CallKnown call,
        Dictionary<int, int> defCount,
        Dictionary<int, int> defIndex,
        Dictionary<int, int> useCount,
        Dictionary<(int BasePtr, int OffsetBytes), int> storeIndexByEnvPtrAndOffset,
        out int allocIndex,
        out int[] storeIndices)
    {
        storeIndices = [];
        if (defCount.GetValueOrDefault(call.EnvTemp) != 1
            || !defIndex.TryGetValue(call.EnvTemp, out allocIndex)
            || instructions[allocIndex] is not IrInst.AllocStack { SizeBytes: 8 or 16 } alloc
            || alloc.Target != call.EnvTemp)
        {
            allocIndex = -1;
            return false;
        }

        int captureCount = alloc.SizeBytes / 8;
        if (useCount.GetValueOrDefault(call.EnvTemp) != captureCount + 1
            || (captureCount == 2 && call.RuntimeManagedArgumentFlagTemp >= 0))
        {
            return false;
        }

        var stores = new int[captureCount];
        for (int capture = 0; capture < captureCount; capture++)
        {
            if (!storeIndexByEnvPtrAndOffset.TryGetValue((call.EnvTemp, capture * 8), out stores[capture]))
            {
                return false;
            }
        }

        storeIndices = stores;
        return true;
    }

    private static (Dictionary<int, int> DefCount, Dictionary<int, int> DefIndex, Dictionary<int, int> UseCount)
        ComputeTempDefUseFacts(List<IrInst> instructions)
    {
        var defCount = new Dictionary<int, int>();
        var defIndex = new Dictionary<int, int>();
        var useCount = new Dictionary<int, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            foreach (int d in StateMachineTransform.GetDefinedTemps(instructions[i]))
            {
                defCount[d] = defCount.GetValueOrDefault(d) + 1;
                defIndex[d] = i;
            }

            foreach (int u in StateMachineTransform.GetUsedTemps(instructions[i]))
            {
                useCount[u] = useCount.GetValueOrDefault(u) + 1;
            }
        }

        return (defCount, defIndex, useCount);
    }

    private static string? GetOrCreateScalarEnvVariant(
        IrFunction callee,
        int captureCount,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, string?> cloneLabelByOriginal,
        List<IrFunction> newFunctions,
        ref int cloneCounter)
    {
        string memoKey = $"{callee.Label}#{captureCount}";
        if (cloneLabelByOriginal.TryGetValue(memoKey, out string? cached))
        {
            return cached;
        }

        IrFunction? clone = TryBuildScalarEnvVariant(callee, captureCount, cloneCounter);
        string? cloneLabel = clone?.Label;
        cloneLabelByOriginal[memoKey] = cloneLabel;
        if (clone is not null)
        {
            cloneCounter++;
            newFunctions.Add(clone);
            functionsByLabel[clone.Label] = clone;
        }

        return cloneLabel;
    }

    // Builds a scalar-env variant of a devirtualizable callee, or returns null when the callee's
    // body does not match the narrow shape this pass recognizes: the function touches its
    // environment only through LoadEnv of capture indices below captureCount (no raw read of the
    // env pointer, no other field), and — when the second capture is to travel in the flag word —
    // never reads the ownership flag itself.
    private static IrFunction? TryBuildScalarEnvVariant(IrFunction callee, int captureCount, int cloneCounter)
    {
        // Real (non-coroutine) lowered closures read a capture via the dedicated LoadEnv
        // instruction, which implicitly dereferences local slot 0 — never via an explicit
        // LoadLocal(_, 0) of the env pointer. A coroutine's state-machine transform rewrites
        // LoadEnv into a LoadMemOffset against its own frame/state-struct temp instead (see
        // StateMachineTransform.AdjustLoadEnvForStateStruct), a materially different and riskier
        // shape this narrow pass does not attempt.
        if (!callee.HasEnvAndArgParams || callee.Coroutine is not null)
        {
            return null;
        }

        List<IrInst> body = callee.Instructions;
        if (body.Any(inst => inst is IrInst.LoadLocal { Slot: 0 }))
        {
            // The env pointer is read as a raw value somewhere outside of LoadEnv — it is treated
            // as a genuine pointer for some other purpose this pass does not attempt to reason about.
            return null;
        }

        if (captureCount == 2 && body.Any(inst => inst is IrInst.LoadArgumentOwnership))
        {
            // The flag word is already meaningful to this callee, so it cannot carry a capture.
            return null;
        }

        var loadEnvIndices = new List<int>();
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i] is not IrInst.LoadEnv loadEnv)
            {
                continue;
            }

            if (loadEnv.Index < 0 || loadEnv.Index >= captureCount)
            {
                // An index past the environment the caller-side gate measured means this callee's
                // shape is not what that gate assumed.
                return null;
            }

            loadEnvIndices.Add(i);
        }

        if (loadEnvIndices.Count == 0)
        {
            return null;
        }

        var newBody = new List<IrInst>(body);
        foreach (int i in loadEnvIndices)
        {
            IrInst.LoadEnv loadEnv = (IrInst.LoadEnv)body[i];
            newBody[i] = loadEnv.Index == 0
                ? new IrInst.LoadLocal(loadEnv.Target, 0)
                : new IrInst.LoadArgumentOwnership(loadEnv.Target);
        }

        return CloneAsScalarEnvVariant(callee, newBody, cloneCounter);
    }

    // The variant names itself and points at the callee it was cloned from, so a report selector
    // that matches the callee also finds the variant.
    private static IrFunction CloneAsScalarEnvVariant(IrFunction callee, List<IrInst> newBody, int cloneCounter)
    {
        string cloneLabel = $"{callee.Label}__scalarenv{cloneCounter}";
        return callee with
        {
            Label = cloneLabel,
            Instructions = newBody,
            Origin = callee.Origin is { } origin
                ? origin with { GeneratedLabel = cloneLabel, ParentGeneratedLabel = callee.Label }
                : null,
        };
    }

    private static IrFunction OptimizeFunction(IrFunction function, HashSet<string> evaluableFunctions)
    {
        var instructions = function.Instructions;

        // Pass ordering matters — each pass may enable further optimizations in subsequent passes.
        instructions = ElideTrivialOwnershipCopies(instructions);
        instructions = SinkRuntimeRcDupsIntoDiamonds(instructions);
        instructions = FuseAdjacentRuntimeRcPairs(instructions);
        instructions = DevirtualizeKnownClosureCalls(instructions);
        instructions = FoldConstants(instructions);
        instructions = ReduceIdentitiesAndStrength(instructions);

        // ReduceIdentitiesAndStrength rewrites an algebraic identity (x+0, x-0, ...) into a
        // Borrow copy rather than retargeting downstream uses directly, but it runs after
        // ElideTrivialOwnershipCopies (the pass that would otherwise erase such a copy), so
        // without this second call those copies would never be swept within this invocation.
        // ElideTrivialOwnershipCopies is a pure function of its input (it recomputes its
        // use-def facts fresh each call), so re-running it here is safe and cheap — a single
        // linear pass, not a fixpoint.
        instructions = ElideTrivialOwnershipCopies(instructions);

        // Runs after calls are already in canonical direct (CallKnown) form and constants are
        // folded, before ElideDeadCode sweeps the now-unused duplicate-call argument
        // construction it can expose. Emits Borrow copies for cache hits (same idiom as
        // ReduceIdentitiesAndStrength above), so re-run ElideTrivialOwnershipCopies once more to
        // forward/erase them.
        instructions = EliminateLocalRedundantComputation(instructions, evaluableFunctions, function.HasEnvAndArgParams);
        instructions = ElideTrivialOwnershipCopies(instructions);

        instructions = ElideUnreachableCode(instructions);

        // SimplifyControlFlow and ElideUnreachableCode can each expose new opportunities for the
        // other: redirecting a Jump through an empty-label chain can rewrite several distinct
        // instructions to the same final target, and once the now-unreferenced labels that used
        // to separate them are dropped, those become several unconditional Jumps stacked
        // back-to-back — every one after the first is unreachable code only ElideUnreachableCode
        // removes, which can in turn bring a surviving Jump directly adjacent to its own target
        // label for SimplifyControlFlow to elide next. Both are pure functions of their input, so
        // iterating them to a fixed point is safe; the instruction count strictly decreases each
        // iteration that changes anything (the one edit that doesn't remove an instruction —
        // redirecting a target — only ever fires once, since chains are already fully resolved to
        // their final destination on the first pass), so this always terminates.
        int simplifyPreviousCount;
        do
        {
            simplifyPreviousCount = instructions.Count;
            instructions = SimplifyControlFlow(instructions);
            instructions = ElideUnreachableCode(instructions);
        }
        while (instructions.Count != simplifyPreviousCount);

        instructions = ElideDeadCode(instructions);
        instructions = ElideErasedRcDrops(instructions);

        return function with
        {
            Instructions = instructions,
        };
    }

    private readonly record struct RuntimeRcDupSinkPlan(
        int DupIndex,
        int InsertIndex,
        int DeadDropIndex,
        IrInst.RcDup Dup);

    // Sink a runtime duplicate from immediately before a simple if/else diamond into the only
    // branch that meaningfully consumes it. The other branch must merely drop the duplicate and
    // must not use the source, since doing so could observe the reference-count change.
    private static List<IrInst> SinkRuntimeRcDupsIntoDiamonds(List<IrInst> instructions)
    {
        List<IrInst> current = instructions;
        while (TryFindRuntimeRcDupSink(current, out RuntimeRcDupSinkPlan plan))
        {
            List<IrInst> rewritten = new(current.Count - 1);
            for (int i = 0; i < current.Count; i++)
            {
                if (i == plan.InsertIndex)
                {
                    rewritten.Add(plan.Dup);
                }

                if (i != plan.DupIndex && i != plan.DeadDropIndex)
                {
                    rewritten.Add(current[i]);
                }
            }

            current = rewritten;
        }

        return current;
    }

    private static bool TryFindRuntimeRcDupSink(
        List<IrInst> instructions,
        out RuntimeRcDupSinkPlan plan)
    {
        for (int i = 0; i + 2 < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.RcDup { RuntimeManaged: true } dup
                || instructions[i + 1] is not IrInst.JumpIfFalse branch
                || !TryDescribeDiamond(instructions, i + 2, branch.Target, out int elseIndex, out int endIndex))
            {
                continue;
            }

            int thenStart = i + 2;
            int thenEnd = elseIndex - 1;
            int elseStart = elseIndex + 1;
            int elseEnd = endIndex;
            List<int> thenDrops = FindRuntimeRcDrops(instructions, thenStart, thenEnd, dup.Target);
            List<int> elseDrops = FindRuntimeRcDrops(instructions, elseStart, elseEnd, dup.Target);
            bool thenUses = HasMeaningfulTempUse(instructions, thenStart, thenEnd, dup.Target);
            bool elseUses = HasMeaningfulTempUse(instructions, elseStart, elseEnd, dup.Target);
            if (thenDrops.Count != 1 || elseDrops.Count != 1 || thenUses == elseUses
                || IsTempUsedInRange(instructions, endIndex + 1, instructions.Count, dup.Target))
            {
                continue;
            }

            int unusedStart = thenUses ? elseStart : thenStart;
            int unusedEnd = thenUses ? elseEnd : thenEnd;
            if (!IsRuntimeRcDropOnlyBranch(instructions, unusedStart, unusedEnd, dup.Target))
            {
                continue;
            }

            plan = thenUses
                ? new RuntimeRcDupSinkPlan(i, thenStart, elseDrops[0], dup)
                : new RuntimeRcDupSinkPlan(i, elseStart, thenDrops[0], dup);
            return true;
        }

        plan = default;
        return false;
    }

    private static bool IsRuntimeRcDropOnlyBranch(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is not IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                || source != temp)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDescribeDiamond(
        List<IrInst> instructions,
        int thenStart,
        string elseLabel,
        out int elseIndex,
        out int endIndex)
    {
        elseIndex = instructions.FindIndex(thenStart, inst => inst is IrInst.Label { Name: var name }
            && string.Equals(name, elseLabel, StringComparison.Ordinal));
        if (elseIndex <= thenStart
            || instructions[elseIndex - 1] is not IrInst.Jump endJump)
        {
            endIndex = -1;
            return false;
        }

        endIndex = instructions.FindIndex(elseIndex + 1, inst => inst is IrInst.Label { Name: var name }
            && string.Equals(name, endJump.Target, StringComparison.Ordinal));
        return endIndex > elseIndex;
    }

    private static List<int> FindRuntimeRcDrops(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        List<int> drops = [];
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                && source == temp)
            {
                drops.Add(i);
            }
        }

        return drops;
    }

    private static bool HasMeaningfulTempUse(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                && source == temp)
            {
                continue;
            }

            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTempUsedInRange(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = start; i < end; i++)
        {
            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    // Adjacent runtime dup/drop fusion. No instruction may occur between the pair because an
    // RcIsUnique or arbitrary call could observe the temporary increment. Dropping the duplicate
    // cancels the split outright; dropping the source transfers its ownership to the identity-
    // preserving duplicate, whose later uses can be remapped back to the source temp.
    private static List<IrInst> FuseAdjacentRuntimeRcPairs(List<IrInst> instructions)
    {
        List<IrInst> result = new(instructions.Count);
        Dictionary<int, int> remap = [];

        for (int i = 0; i < instructions.Count; i++)
        {
            IrInst instruction = RemapSourceTemps(instructions[i], remap);
            if (instruction is not IrInst.RcDup { RuntimeManaged: true } dup
                || i + 1 >= instructions.Count
                || RemapSourceTemps(instructions[i + 1], remap) is not IrInst.RcDrop { RuntimeManaged: true } drop)
            {
                result.Add(instruction);
                continue;
            }

            if (drop.SourceTemp == dup.Target && !IsTempUsedAfter(instructions, i + 2, dup.Target))
            {
                i++;
                continue;
            }

            if (drop.SourceTemp == dup.SourceTemp)
            {
                remap[dup.Target] = dup.SourceTemp;
                i++;
                continue;
            }

            result.Add(instruction);
        }

        return result;
    }

    private static bool IsTempUsedAfter(List<IrInst> instructions, int startIndex, int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = startIndex; i < instructions.Count; i++)
        {
            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    // Redundant arena-bracket elision
    // Lowering brackets every function body and every copy-type-returning helper call in
    // SaveArenaState / RestoreArenaState / ReclaimArenaChunks. For a region that provably
    // performs no arena allocation the bracket is pure overhead — worse, the reclaim's chunk
    // loop (a syscall loop with a dynamic stack slot) makes tiny accessors like Map.height
    // ineligible for LLVM inlining. Two eliminations, both conservative:
    //  (a) whole function: if every instruction of a function is non-allocating (direct calls
    //      only to non-allocating functions, via a whole-program fixpoint), every arena
    //      bracket instruction in it is a no-op and is removed;
    //  (b) straight-line caller regions: a Save…Restore(+Reclaim) triple with no label, jump,
    //      or potentially-allocating instruction between save and restore is removed.
    // Anything not on the explicit non-allocating whitelist (indirect calls, externals,
    // intrinsics that build values, copy-outs, allocs) keeps its brackets.

    private static bool IsNonAllocatingInst(IrInst inst, HashSet<string> nonAllocatingFns) => inst switch
    {
        IrInst.LoadConstInt or IrInst.LoadConstFloat or IrInst.LoadConstBool or IrInst.LoadConstStr
            or IrInst.LoadLocal or IrInst.StoreLocal or IrInst.LoadEnv or IrInst.LoadArgumentOwnership
            or IrInst.LoadMemOffset or IrInst.StoreMemOffset
            or IrInst.AddInt or IrInst.SubInt or IrInst.MulInt or IrInst.DivInt or IrInst.DivUInt
            or IrInst.AndInt or IrInst.OrInt or IrInst.XorInt or IrInst.ShlInt or IrInst.ShrInt
            or IrInst.AddFloat or IrInst.SubFloat or IrInst.MulFloat or IrInst.DivFloat
            or IrInst.IntToFloat or IrInst.FloatToInt or IrInst.FloatUnaryIntrinsic or IrInst.CallLibm
            or IrInst.CmpIntGt or IrInst.CmpIntGe or IrInst.CmpIntLt or IrInst.CmpIntLe
            or IrInst.CmpUIntGt or IrInst.CmpUIntGe or IrInst.CmpUIntLt or IrInst.CmpUIntLe
            or IrInst.CmpIntEq or IrInst.CmpIntNe
            or IrInst.CmpFloatGt or IrInst.CmpFloatGe or IrInst.CmpFloatLt or IrInst.CmpFloatLe
            or IrInst.CmpFloatEq or IrInst.CmpFloatNe
            or IrInst.CmpStrEq or IrInst.CmpStrNe
            or IrInst.LoadFuncAddr or IrInst.GetAdtTag or IrInst.GetAdtField or IrInst.SetAdtField
            or IrInst.Borrow or IrInst.DropReuse or IrInst.RcDup or IrInst.RcDrop or IrInst.RcIsUnique
            or IrInst.BytesLength or IrInst.BytesGet or IrInst.BytesCompare or IrInst.BytesIndexOf
            or IrInst.BytesHash or IrInst.BytesGetU16Le or IrInst.BytesGetU32Le or IrInst.BytesGetU64Le
            or IrInst.TextByteLength
            or IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks
            or IrInst.SaveStackPointer or IrInst.RestoreStackPointer
            or IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag or IrInst.Return
            => true,
        IrInst.CallKnown ck => nonAllocatingFns.Contains(ck.FuncLabel),
        _ => false,
    };

    private static HashSet<string> ComputeNonAllocatingFunctions(IrFunction entry, IReadOnlyList<IrFunction> functions)
    {
        // Least fixpoint (via WholeProgramFixpoint): start from "every function might be
        // non-allocating", knock out any whose body contains a non-whitelisted instruction or a
        // known call to a knocked-out callee, and iterate until stable. The entry function is
        // never a callee, so it is not in the candidate set.
        var candidates = new HashSet<string>(functions.Select(f => f.Label), StringComparer.Ordinal);
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (var f in functions)
            {
                if (!candidates.Contains(f.Label))
                {
                    continue;
                }

                foreach (var inst in f.Instructions)
                {
                    if (!IsNonAllocatingInst(inst, candidates))
                    {
                        candidates.Remove(f.Label);
                        changed = true;
                        break;
                    }
                }
            }

            return changed;
        });

        return candidates;
    }

    private static IrFunction StripRedundantArenaBrackets(IrFunction function, HashSet<string> nonAllocatingFns)
    {
        var instructions = function.Instructions;

        // (a) Whole-function elision: nothing in this function can move the arena cursor, so
        // every save/restore/reclaim in it observes and restores an unchanged arena.
        bool wholeFunction = nonAllocatingFns.Contains(function.Label);
        if (!wholeFunction)
        {
            // The entry function has no label in the candidate set; check it directly.
            wholeFunction = instructions.All(i => IsNonAllocatingInst(i, nonAllocatingFns));
        }

        if (wholeFunction)
        {
            if (!instructions.Any(i => i is IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks))
            {
                return function;
            }

            var stripped = instructions
                .Where(i => i is not (IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks))
                .ToList();
            return function with { Instructions = stripped };
        }

        // (b) Straight-line region elision within an allocating function.
        var toRemove = FindStraightLineBracketRemovals(instructions, nonAllocatingFns);

        if (toRemove.Count == 0)
        {
            return function;
        }

        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!toRemove.Contains(i))
            {
                result.Add(instructions[i]);
            }
        }

        return function with { Instructions = result };
    }

    /// <summary>
    /// Finds the instruction indices of Save…Restore(+Reclaim) arena-bracket triples with no
    /// label, jump, or potentially-allocating instruction between save and restore.
    /// </summary>
    private static HashSet<int> FindStraightLineBracketRemovals(List<IrInst> instructions, HashSet<string> nonAllocatingFns)
    {
        var toRemove = new HashSet<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.SaveArenaState save)
            {
                continue;
            }

            for (int j = i + 1; j < instructions.Count; j++)
            {
                var inst = instructions[j];
                if (inst is IrInst.RestoreArenaState restore
                    && restore.CursorLocalSlot == save.CursorLocalSlot
                    && restore.EndLocalSlot == save.EndLocalSlot)
                {
                    toRemove.Add(i);
                    toRemove.Add(j);
                    // The reclaim reads the slots the removed save and restore wrote, so it goes
                    // with them wherever the bracket's close placed it: right after the restore, or
                    // past the conditional copy-out block a call window puts between the two.
                    int reclaimIndex = FindBracketReclaim(instructions, j + 1, save.EndLocalSlot, restore.PreRestoreEndSlot);
                    if (reclaimIndex >= 0)
                    {
                        toRemove.Add(reclaimIndex);
                    }

                    break;
                }

                // Any control flow, allocation, or unknown instruction ends the attempt.
                if (inst is IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag
                    || !IsNonAllocatingInst(inst, nonAllocatingFns))
                {
                    break;
                }
            }
        }

        return toRemove;
    }

    private static int FindBracketReclaim(List<IrInst> instructions, int start, int savedEndSlot, int preRestoreEndSlot)
    {
        for (int k = start; k < instructions.Count; k++)
        {
            if (instructions[k] is IrInst.ReclaimArenaChunks reclaim
                && reclaim.SavedEndSlot == savedEndSlot
                && reclaim.PreRestoreEndSlot == preRestoreEndSlot)
            {
                return k;
            }
        }

        return -1;
    }

    // Known-closure devirtualization
    // A CallClosure whose closure temp is produced by MakeClosure/MakeClosureStack with a
    // statically-known function label becomes a direct CallKnown of that label with the
    // closure's captured env pointer. The indirect call through the closure struct's code
    // pointer is opaque to LLVM, so callees like tiny accessors could never be inlined; a
    // direct call inlines normally. Safety: only single-definition temps are rewritten (a
    // unique definition dominates every use in well-formed IR), and the env temp must itself
    // be single-definition so its value at the call site equals the value stored into the
    // closure at construction. Closures whose target is also stored/escapes elsewhere keep
    // their MakeClosure (dead-code elimination removes it only when no use remains).

    private static List<IrInst> DevirtualizeKnownClosureCalls(List<IrInst> instructions)
    {
        // Count definitions per temp; remember the defining instruction of single-def temps. Local
        // slots are tracked the same way: a slot written by exactly one StoreLocal in the whole
        // function holds that store's value at every load, since lowering only ever reads a
        // binding's slot inside the binding's own scope, after the store. This is what lets a
        // let-bound local helper (`let step = given x -> ... in step(1)`), whose call always goes
        // through a StoreLocal/LoadLocal round trip, resolve to its MakeClosure like an
        // immediately-applied lambda does.
        ClosureDefinitionFacts facts = ClosureDefinitionFacts.Collect(instructions);

        bool changed = false;
        var result = new List<IrInst>(instructions.Count);
        // A slot load whose only use was a call rewritten here is dead afterwards; dropping it lets
        // the slot's store and the closure construction die in the ordinary dead-code sweep.
        var deadSlotLoadTemps = new HashSet<int>();
        foreach (var inst in instructions)
        {
            // A let-bound closure's scope-exit resource cleanup is a runtime no-op when the closure
            // is a stack closure that never received a dropper; removing it (and its load) is what
            // lets the slot, the closure construction, and the environment die once every call is
            // devirtualized below.
            if (inst is IrInst.CleanupResource { TypeName: "Function", Destructor: null } cleanup
                && facts.IsDropperFreeStackClosureSlotLoad(cleanup.SourceTemp))
            {
                if (facts.UseCount.GetValueOrDefault(cleanup.SourceTemp) == 1)
                {
                    deadSlotLoadTemps.Add(cleanup.SourceTemp);
                }

                changed = true;
                continue;
            }

            if (inst is IrInst.CallClosure cc
                && facts.ResolveClosureDefinition(cc.ClosureTemp, out bool throughSlot) is { } def
                && TryBuildKnownCall(cc, def, facts) is { } known)
            {
                result.Add(known);
                if (throughSlot && facts.UseCount.GetValueOrDefault(cc.ClosureTemp) == 1)
                {
                    deadSlotLoadTemps.Add(cc.ClosureTemp);
                }

                changed = true;
                continue;
            }

            result.Add(inst);
        }

        if (deadSlotLoadTemps.Count > 0)
        {
            result.RemoveAll(inst => inst is IrInst.LoadLocal load && deadSlotLoadTemps.Contains(load.Target));
        }

        return changed ? result : instructions;
    }

    // The direct call for a closure call whose closure was produced by a known construction, or
    // null when the construction is not a closure or its environment pointer is not single-defined.
    private static IrInst.CallKnown? TryBuildKnownCall(IrInst.CallClosure call, IrInst definition, ClosureDefinitionFacts facts)
    {
        (string Label, int EnvTemp)? known = definition switch
        {
            IrInst.MakeClosure mk => (mk.FuncLabel, mk.EnvPtrTemp),
            IrInst.MakeClosureStack mks => (mks.FuncLabel, mks.EnvPtrTemp),
            _ => null,
        };
        if (known is not { } k || facts.DefCount.GetValueOrDefault(k.EnvTemp) != 1)
        {
            return null;
        }

        return new IrInst.CallKnown(
            call.Target,
            k.Label,
            k.EnvTemp,
            call.ArgTemp,
            call.RuntimeManagedArgumentFlagTemp,
            EnvironmentIsStackAllocated: definition is IrInst.MakeClosureStack { EnvSizeBytes: > 0 })
        { Location = call.Location };
    }

    /// <summary>
    /// Per-function definition facts for closure devirtualization: how often each temp is defined
    /// and by which instruction, and how often each local slot is stored and from which temp.
    /// </summary>
    private sealed class ClosureDefinitionFacts
    {
        public Dictionary<int, int> DefCount { get; } = new();
        public Dictionary<int, int> UseCount { get; } = new();
        private readonly Dictionary<int, IrInst> _singleDef = new();
        private readonly Dictionary<int, int> _storeCountBySlot = new();
        private readonly Dictionary<int, int> _singleStoreSourceBySlot = new();

        public static ClosureDefinitionFacts Collect(List<IrInst> instructions)
        {
            var facts = new ClosureDefinitionFacts();
            foreach (var inst in instructions)
            {
                foreach (var d in StateMachineTransform.GetDefinedTemps(inst))
                {
                    facts.DefCount[d] = facts.DefCount.GetValueOrDefault(d) + 1;
                    facts._singleDef[d] = inst;
                }

                foreach (var u in StateMachineTransform.GetUsedTemps(inst))
                {
                    facts.UseCount[u] = facts.UseCount.GetValueOrDefault(u) + 1;
                }

                if (inst is IrInst.StoreLocal store)
                {
                    facts._storeCountBySlot[store.Slot] = facts._storeCountBySlot.GetValueOrDefault(store.Slot) + 1;
                    facts._singleStoreSourceBySlot[store.Slot] = store.Source;
                }
                else if (inst is IrInst.StoreMemOffset { OffsetBytes: ClosureDropperOffsetBytes } dropperStore)
                {
                    facts._closureTempsWithDropper.Add(dropperStore.BasePtr);
                }
            }

            return facts;
        }

        // The closure object's resource-dropper word: {code, env, packed size/ownership, dropper}.
        private const int ClosureDropperOffsetBytes = 24;
        private readonly HashSet<int> _closureTempsWithDropper = new();

        /// <summary>
        /// True when <paramref name="temp"/> is a load of a single-store slot holding a stack closure
        /// that never had a resource dropper installed, so a resource cleanup of that value is a
        /// no-op at runtime (an ordinary closure's dropper word is zero).
        /// </summary>
        public bool IsDropperFreeStackClosureSlotLoad(int temp)
            => ResolveClosureDefinition(temp, out bool throughSlot) is IrInst.MakeClosureStack closure
                && throughSlot
                && !_closureTempsWithDropper.Contains(closure.Target);

        /// <summary>
        /// The instruction that produced the closure in <paramref name="closureTemp"/>, seen through
        /// a single-store local slot: a slot written by exactly one StoreLocal holds that store's
        /// value at every load, since lowering only reads a binding's slot inside the binding's own
        /// scope, after the store. Null when the temp has more than one definition.
        /// </summary>
        public IrInst? ResolveClosureDefinition(int closureTemp, out bool throughSlot)
        {
            throughSlot = false;
            if (DefCount.GetValueOrDefault(closureTemp) != 1 || !_singleDef.TryGetValue(closureTemp, out var def))
            {
                return null;
            }

            if (def is IrInst.LoadLocal load
                && _storeCountBySlot.GetValueOrDefault(load.Slot) == 1
                && _singleStoreSourceBySlot.TryGetValue(load.Slot, out int storedTemp)
                && DefCount.GetValueOrDefault(storedTemp) == 1
                && _singleDef.TryGetValue(storedTemp, out var storedDef))
            {
                throughSlot = true;
                return storedDef;
            }

            return def;
        }
    }

    // Trivial ownership-copy elision
    // Remove erased RcDup markers and eligible Borrow instructions, remapping all uses of their
    // targets back to the original source temp.
    //
    // Elidable borrows:
    // (a) Copy-type sources: when the source temp is produced by
    //     LoadConstInt / LoadConstFloat / LoadConstBool. Copy types have no
    //     ownership semantics, so the borrow is semantically a no-op.
    // (b) Single-use borrows: when the borrow target is used exactly once.
    //     The borrowed reference is consumed at a single point, so it is safe
    //     to substitute the original source directly.
    //
    // Chains of borrows (Borrow(t2, t1) where t1 itself was remapped) are
    // resolved transitively so that all uses point back to the original source.

    private static List<IrInst> ElideTrivialOwnershipCopies(List<IrInst> instructions)
    {
        // Build use-def information.
        var (copyTypeProducers, useCount) = CollectBorrowElisionInfo(instructions);

        // Identify elidable Borrows and build a remap table.
        var remap = new Dictionary<int, int>();

        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDup { RuntimeManaged: false } dup)
            {
                remap[dup.Target] = ResolveTemp(remap, dup.SourceTemp);
            }
            else if (inst is IrInst.Borrow b)
            {
                // Follow chains: if the source was already remapped, resolve transitively.
                int source = ResolveTemp(remap, b.SourceTemp);

                bool isCopyTypeSource = copyTypeProducers.Contains(source);
                bool isSingleUse = useCount.GetValueOrDefault(b.Target) <= 1;

                if (isCopyTypeSource || isSingleUse)
                {
                    remap[b.Target] = source;
                }
            }
        }

        if (remap.Count == 0)
        {
            return instructions;
        }

        // Rewrite the instruction list — remove elided Borrows and
        // remap all source-temp references to the original source.
        var result = new List<IrInst>(instructions.Count);

        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDup { RuntimeManaged: false } dup && remap.ContainsKey(dup.Target))
            {
                continue; // erased marker
            }

            if (inst is IrInst.Borrow b && remap.ContainsKey(b.Target))
            {
                continue; // elide this Borrow
            }

            result.Add(RemapSourceTemps(inst, remap));
        }

        return result;
    }

    /// <summary>
    /// Builds the use-def information for borrow elision: which temps are produced by
    /// copy-type constant instructions, and how many times each temp is read as a
    /// source operand.
    /// </summary>
    private static (HashSet<int> CopyTypeProducers, Dictionary<int, int> UseCount) CollectBorrowElisionInfo(List<IrInst> instructions)
    {
        // Track which temps are produced by copy-type constant instructions.
        var copyTypeProducers = new HashSet<int>();

        // Count how many times each temp is read as a source operand.
        var useCount = new Dictionary<int, int>();
        var tempBuf = new HashSet<int>();

        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci: copyTypeProducers.Add(lci.Target); break;
                case IrInst.LoadConstFloat lcf: copyTypeProducers.Add(lcf.Target); break;
                case IrInst.LoadConstBool lcb: copyTypeProducers.Add(lcb.Target); break;
            }

            tempBuf.Clear();
            CollectUsedTemps(inst, tempBuf);
            foreach (var t in tempBuf)
            {
                useCount[t] = useCount.GetValueOrDefault(t) + 1;
            }
        }

        return (copyTypeProducers, useCount);
    }

    /// <summary>
    /// Follows the remap chain for a temp index until a fixed point is reached.
    /// If <paramref name="temp"/> → a → b exists, returns b.
    /// Returns the original temp if it is not in the map.
    /// </summary>
    private static int ResolveTemp(Dictionary<int, int> remap, int temp)
    {
        while (remap.TryGetValue(temp, out int resolved))
        {
            temp = resolved;
        }

        return temp;
    }

    /// <summary>
    /// Returns a copy of <paramref name="inst"/> with all source (read) temps
    /// rewritten according to <paramref name="remap"/>. Target (write) temps are
    /// left unchanged. Instructions with no source temps are returned as-is.
    /// </summary>
    internal static IrInst RemapSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return RemapArithmeticSourceTemps(inst, remap)
            ?? RemapMemorySourceTemps(inst, remap)
            ?? RemapIoSourceTemps(inst, remap)
            ?? RemapBytesAndOwnershipSourceTemps(inst, remap)
            ?? RemapAsyncSourceTemps(inst, remap)
            ?? inst switch
            {
                // Capabilities.
                IrInst.StoreCapabilityHandler se => se with { Source = R(se.Source) },

                // Control flow.
                IrInst.PanicStr p => p with { Source = R(p.Source) },
                IrInst.JumpIfFalse j => j with { CondTemp = R(j.CondTemp) },
                IrInst.SwitchTag s => s with { TagTemp = R(s.TagTemp) },
                IrInst.Return r => r with { Source = R(r.Source) },

                // Instructions with no source temps — pass through unchanged.
                _ => inst,
            };
    }

    private static IrInst? RemapArithmeticSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Binary arithmetic / comparison — remap Left and Right.
            IrInst.AddInt a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.SubInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.MulInt m => m with { Left = R(m.Left), Right = R(m.Right) },
            IrInst.DivInt d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.DivUInt d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.AndInt a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.OrInt o => o with { Left = R(o.Left), Right = R(o.Right) },
            IrInst.XorInt x => x with { Left = R(x.Left), Right = R(x.Right) },
            IrInst.ShlInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.ShrInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.AddFloat a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.SubFloat s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.MulFloat m => m with { Left = R(m.Left), Right = R(m.Right) },
            IrInst.DivFloat d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.IntToFloat i => i with { ValueTemp = R(i.ValueTemp) },
            IrInst.FloatToInt f => f with { ValueTemp = R(f.ValueTemp) },
            IrInst.FloatUnaryIntrinsic u => u with { ValueTemp = R(u.ValueTemp) },
            IrInst.CallLibm c => c with { Args = c.Args.Select(R).ToList() },
            IrInst.RegexCompile c => c with { Pattern = R(c.Pattern) },
            IrInst.RegexCompileError c => c with { Pattern = R(c.Pattern) },
            IrInst.RegexFind c => c with { Code = R(c.Code), Subject = R(c.Subject), Start = R(c.Start) },
            IrInst.RegexCaptures c => c with { Code = R(c.Code), Subject = R(c.Subject), Start = R(c.Start) },
            IrInst.RegexSubstitute c => c with { Code = R(c.Code), Subject = R(c.Subject), Replacement = R(c.Replacement) },
            IrInst.CmpIntGt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntGe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntLt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntLe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpUIntGt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpUIntGe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpUIntLt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpUIntLe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatGt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatGe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatLt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatLe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpStrEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpStrNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.ConcatStr c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.ConcatStrTip c => c with { Left = R(c.Left), Right = R(c.Right) },

            _ => null,
        };
    }

    private static IrInst? RemapMemorySourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Memory operations.
            IrInst.StoreLocal s => s with { Source = R(s.Source) },
            IrInst.StoreMemOffset s => s with { BasePtr = R(s.BasePtr), Source = R(s.Source) },
            IrInst.LoadMemOffset l => l with { BasePtr = R(l.BasePtr) },

            // Closures.
            IrInst.MakeClosure mc => mc with { EnvPtrTemp = R(mc.EnvPtrTemp) },
            IrInst.MakeClosureStack mc => mc with { EnvPtrTemp = R(mc.EnvPtrTemp) },
            IrInst.CallClosure cc => cc with
            {
                ClosureTemp = R(cc.ClosureTemp),
                ArgTemp = R(cc.ArgTemp),
                RuntimeManagedArgumentFlagTemp = cc.RuntimeManagedArgumentFlagTemp < 0
                    ? -1
                    : R(cc.RuntimeManagedArgumentFlagTemp),
            },
            IrInst.CallKnown ck => ck with
            {
                EnvTemp = R(ck.EnvTemp),
                ArgTemp = R(ck.ArgTemp),
                RuntimeManagedArgumentFlagTemp = ck.RuntimeManagedArgumentFlagTemp < 0
                    ? -1
                    : R(ck.RuntimeManagedArgumentFlagTemp),
            },
            IrInst.ToCString c => c with { StrTemp = R(c.StrTemp) },
            IrInst.LoadFfiOut load => load with { SlotTemp = R(load.SlotTemp) },
            IrInst.CopyFfiString copy => copy with { PointerTemp = R(copy.PointerTemp) },
            IrInst.CopyFfiBytes copy => copy with { PointerTemp = R(copy.PointerTemp), LengthTemp = R(copy.LengthTemp) },
            IrInst.CallExternal c => c with { ArgTemps = c.ArgTemps.Select(R).ToList() },

            // ADTs.
            IrInst.SetAdtField sf => sf with { Ptr = R(sf.Ptr), Source = R(sf.Source) },
            IrInst.GetAdtTag gt => gt with { Ptr = R(gt.Ptr) },
            IrInst.GetAdtField gf => gf with { Ptr = R(gf.Ptr) },

            _ => null,
        };
    }

    private static IrInst? RemapIoSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // I/O — remap source temps.
            IrInst.PrintInt p => p with { Source = R(p.Source) },
            IrInst.PrintStr p => p with { Source = R(p.Source) },
            IrInst.PrintBool p => p with { Source = R(p.Source) },
            IrInst.WriteStr w => w with { Source = R(w.Source) },
            IrInst.WriteBufferedStr w => w with { Source = R(w.Source) },
            IrInst.FileReadText f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileWriteText f => f with { PathTemp = R(f.PathTemp), TextTemp = R(f.TextTemp) },
            IrInst.FileExists f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileReplace f => f with { SourceTemp = R(f.SourceTemp), DestinationTemp = R(f.DestinationTemp) },
            IrInst.FileMakeExecutable f => f with { PathTemp = R(f.PathTemp) },
            IrInst.EnvironmentGet e => e with { NameTemp = R(e.NameTemp) },
            IrInst.FileOpen f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileReadChunk f => f with { HandleTemp = R(f.HandleTemp), CountTemp = R(f.CountTemp) },
            IrInst.FileReadLine f => f with { HandleTemp = R(f.HandleTemp) },
            IrInst.FileClose f => f with { HandleTemp = R(f.HandleTemp) },
            IrInst.TextUncons t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextUnconsText t => t with { TextTemp = R(t.TextTemp) },
            IrInst.RuneToText t => t with { RuneTemp = R(t.RuneTemp) },
            IrInst.RuneFromInt t => t with { IntTemp = R(t.IntTemp) },
            IrInst.TextParseInt t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextParseFloat t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextFromInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextFromFloat t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextFormatFloat t => t with { ValueTemp = R(t.ValueTemp), DecimalsTemp = R(t.DecimalsTemp) },
            IrInst.BigIntFromInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntToString t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntToInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntFromString t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntBinary t => t with { Left = R(t.Left), Right = R(t.Right) },
            IrInst.BigIntCompare t => t with { Left = R(t.Left), Right = R(t.Right) },
            IrInst.TextToHex t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextAsciiCase t => t with { SourceTemp = R(t.SourceTemp) },
            IrInst.TextByteLength t => t with { TextTemp = R(t.TextTemp) },
            IrInst.ReadExact r => r with { CountTemp = R(r.CountTemp) },
            IrInst.ConsolePoll cp => cp with { TimeoutTemp = R(cp.TimeoutTemp) },
            IrInst.FileReadAllBytes f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileMmap f => f with { PathTemp = R(f.PathTemp) },
            IrInst.SpawnProcess s => s with { ExeTemp = R(s.ExeTemp), ArgsTemp = R(s.ArgsTemp) },
            IrInst.ProcessWriteStdin p => p with { ProcessTemp = R(p.ProcessTemp), TextTemp = R(p.TextTemp) },
            IrInst.ProcessReadStdoutLine p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessReadStderrLine p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessWaitForExit p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessKill p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.HttpGet h => h with { UrlTemp = R(h.UrlTemp) },
            IrInst.HttpPost h => h with { UrlTemp = R(h.UrlTemp), BodyTemp = R(h.BodyTemp) },
            IrInst.NetTcpConnect n => n with { HostTemp = R(n.HostTemp), PortTemp = R(n.PortTemp) },
            IrInst.NetTcpSend n => n with { SocketTemp = R(n.SocketTemp), TextTemp = R(n.TextTemp) },
            IrInst.NetTcpReceive n => n with { SocketTemp = R(n.SocketTemp), MaxBytesTemp = R(n.MaxBytesTemp) },
            IrInst.NetTcpClose n => n with { SocketTemp = R(n.SocketTemp) },
            IrInst.NetTcpListen n => n with { PortTemp = R(n.PortTemp) },
            IrInst.NetTcpAccept n => n with { SocketTemp = R(n.SocketTemp) },

            _ => RemapProcessOutputSourceTemps(inst, remap),
        };
    }

    private static IrInst? RemapProcessOutputSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;
        return inst switch
        {
            IrInst.WriteErrorStr write => write with { Source = R(write.Source) },
            IrInst.ExitProcess exit => exit with { Source = R(exit.Source) },
            _ => RemapDirectorySourceTemps(inst, remap),
        };
    }

    private static IrInst? RemapDirectorySourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;
        return inst switch
        {
            IrInst.DirectoryEntries d => d with { PathTemp = R(d.PathTemp) },
            IrInst.DirectoryCreateAll d => d with { PathTemp = R(d.PathTemp) },
            IrInst.DirectoryRemoveTree d => d with { PathTemp = R(d.PathTemp) },
            _ => null,
        };
    }

    private static IrInst? RemapBytesAndOwnershipSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            IrInst.BytesEmpty b => b,
            IrInst.BytesSingleton b => b with { ByteTemp = R(b.ByteTemp) },
            IrInst.BytesLength b => b with { BytesTemp = R(b.BytesTemp) },
            IrInst.BytesGet b => b with { BytesTemp = R(b.BytesTemp), IndexTemp = R(b.IndexTemp) },
            IrInst.BytesIndexOf b => b with { BytesTemp = R(b.BytesTemp), NeedleTemp = R(b.NeedleTemp), FromTemp = R(b.FromTemp) },
            IrInst.BytesCompare b => b with { LeftTemp = R(b.LeftTemp), RightTemp = R(b.RightTemp) },
            IrInst.BytesScanHash b => b with { BytesTemp = R(b.BytesTemp), NeedleTemp = R(b.NeedleTemp), FromTemp = R(b.FromTemp) },
            IrInst.BytesSubText b => b with { BytesTemp = R(b.BytesTemp), StartTemp = R(b.StartTemp), LenTemp = R(b.LenTemp) },
            IrInst.BytesSubView b => b with { BytesTemp = R(b.BytesTemp), StartTemp = R(b.StartTemp), LenTemp = R(b.LenTemp) },
            IrInst.BytesAppend b => b with { LeftTemp = R(b.LeftTemp), RightTemp = R(b.RightTemp) },
            IrInst.BytesAppendByte b => b with { BytesTemp = R(b.BytesTemp), ByteTemp = R(b.ByteTemp) },
            IrInst.BytesAllocate b => b with { LengthTemp = R(b.LengthTemp) },
            IrInst.BytesCopyRange b => b with
            {
                BytesTemp = R(b.BytesTemp),
                OffsetTemp = R(b.OffsetTemp),
                SourceTemp = R(b.SourceTemp),
                SourceOffsetTemp = R(b.SourceOffsetTemp),
                LengthTemp = R(b.LengthTemp),
            },
            IrInst.BytesSet b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp), ValueTemp = R(b.ValueTemp) },
            IrInst.BytesSetU16Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp), ValueTemp = R(b.ValueTemp) },
            IrInst.BytesSetU32Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp), ValueTemp = R(b.ValueTemp) },
            IrInst.BytesSetU64Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp), ValueTemp = R(b.ValueTemp) },
            IrInst.BytesFromList b => b with { ListTemp = R(b.ListTemp) },
            IrInst.BytesHash b => b with { BytesTemp = R(b.BytesTemp) },
            IrInst.BytesU16Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesU32Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesU64Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesGetU16Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.BytesGetU32Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.BytesGetU64Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.FileWriteBytes f => f with { PathTemp = R(f.PathTemp), BytesTemp = R(f.BytesTemp) },

            // Ownership.
            // NOTE: Keep these source-temp users in sync with CollectUsedTemps().
            IrInst.CleanupResource d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.DropReuse d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcDrop d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcDup d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcIsUnique u => u with { SourceTemp = R(u.SourceTemp) },
            IrInst.Borrow b => b with { SourceTemp = R(b.SourceTemp) },
            IrInst.CopyOutArena co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutArenaToSpace co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyFixedInto ci => ci with { DestTemp = R(ci.DestTemp), SrcTemp = R(ci.SrcTemp) },
            IrInst.CopyStringIntoOrFresh cs => cs with { OldBlobTemp = R(cs.OldBlobTemp), SrcTemp = R(cs.SrcTemp) },
            IrInst.CopyFixedIntoOrFresh cf => cf with { OldBlobTemp = R(cf.OldBlobTemp), SrcTemp = R(cf.SrcTemp) },
            IrInst.CopyOutList co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutClosure co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutTcoListCell co => co with { SrcTemp = R(co.SrcTemp) },

            _ => null,
        };
    }

    private static IrInst? RemapAsyncSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Async.
            IrInst.CreateTask ct => ct with { ClosureTemp = R(ct.ClosureTemp) },
            IrInst.CreateCompletedTask ct => ct with { ResultTemp = R(ct.ResultTemp) },
            IrInst.AwaitTask at => at with { TaskTemp = R(at.TaskTemp) },
            IrInst.RunTask rt => rt with { TaskTemp = R(rt.TaskTemp) },
            IrInst.SpawnTask st => st with { TaskTemp = R(st.TaskTemp) },
            IrInst.AllocReusing ar => ar with { TokenTemp = R(ar.TokenTemp) },
            IrInst.ParallelFork pf => pf with { RightClosureTemp = R(pf.RightClosureTemp) },
            IrInst.ParallelJoin pj => pj with { DescTemp = R(pj.DescTemp) },
            IrInst.ParallelCleanup pc => pc with { DescTemp = R(pc.DescTemp) },
            IrInst.StoreParallelWorkerOverride so => so with { Source = R(so.Source) },
            IrInst.ParallelQueueStart qs => qs with { FClosureTemp = R(qs.FClosureTemp), CombineClosureTemp = R(qs.CombineClosureTemp), ListTemp = R(qs.ListTemp) },
            IrInst.ParallelQueueAwait qa => qa with { DescTemp = R(qa.DescTemp) },
            IrInst.ParallelQueueCleanup qc => qc with { DescTemp = R(qc.DescTemp) },
            IrInst.AsyncSleep sl => sl with { MillisecondsTemp = R(sl.MillisecondsTemp) },
            IrInst.CreateTcpConnectTask t => t with { HostTemp = R(t.HostTemp), PortTemp = R(t.PortTemp) },
            IrInst.CreateTcpSendTask t => t with { SocketTemp = R(t.SocketTemp), TextTemp = R(t.TextTemp) },
            IrInst.CreateTcpReceiveTask t => t with { SocketTemp = R(t.SocketTemp), MaxBytesTemp = R(t.MaxBytesTemp) },
            IrInst.CreateTcpCloseTask t => t with { SocketTemp = R(t.SocketTemp) },
            IrInst.CreateTcpListenTask t => t with { PortTemp = R(t.PortTemp) },
            IrInst.CreateForkWorkersTask t => t with { PortTemp = R(t.PortTemp), CountTemp = R(t.CountTemp) },
            IrInst.SetDrainTimeout t => t with { MsTemp = R(t.MsTemp) },
            IrInst.CreateTcpAcceptTask t => t with { SocketTemp = R(t.SocketTemp) },
            IrInst.CreateHttpGetTask t => t with { UrlTemp = R(t.UrlTemp) },
            IrInst.CreateHttpPostTask t => t with { UrlTemp = R(t.UrlTemp), BodyTemp = R(t.BodyTemp) },
            IrInst.CreateTlsConnectTask t => t with { HostTemp = R(t.HostTemp), PortTemp = R(t.PortTemp) },
            IrInst.CreateTlsHandshakeTask t => t with { SocketTemp = R(t.SocketTemp), HostTemp = R(t.HostTemp) },
            IrInst.CreateTlsServerHandshakeTask t => t with { SocketTemp = R(t.SocketTemp), CertTemp = R(t.CertTemp), KeyTemp = R(t.KeyTemp) },
            IrInst.CreateTlsSendTask t => t with { SslTemp = R(t.SslTemp), TextTemp = R(t.TextTemp) },
            IrInst.CreateTlsReceiveTask t => t with { SslTemp = R(t.SslTemp), MaxBytesTemp = R(t.MaxBytesTemp) },
            IrInst.CreateTlsCloseTask t => t with { SslTemp = R(t.SslTemp) },
            IrInst.AsyncAll aa => aa with { TaskListTemp = R(aa.TaskListTemp) },
            IrInst.AsyncRace ar => ar with { TaskListTemp = R(ar.TaskListTemp) },
            IrInst.CreateScopedTask s => s with { ParentTaskTemp = R(s.ParentTaskTemp), ScopeTemp = R(s.ScopeTemp) },
            IrInst.ForkScopedTask f => f with { OwnerTaskTemp = R(f.OwnerTaskTemp), TaskTemp = R(f.TaskTemp) },
            IrInst.JoinScopedTask j => j with { HandleTemp = R(j.HandleTemp) },
            IrInst.Suspend s => s with
            {
                StateStructTemp = R(s.StateStructTemp),
                AwaitedTaskTemp = R(s.AwaitedTaskTemp),
                SaveVars = s.SaveVars.Select(v => (v.SlotOffset, R(v.SourceTemp))).ToList(),
            },
            IrInst.Resume r => r with { StateStructTemp = R(r.StateStructTemp) },

            _ => null,
        };
    }

    // Constant folding
    // Evaluate arithmetic on known constant operands at compile time.
    // At a label, the constant state entering it is the meet (intersection of
    // agreeing facts) over every predecessor edge already observed by this
    // single forward scan — a fact survives only if every incoming edge agrees
    // on it. A predecessor reached by a backward branch (e.g. a loop back-edge)
    // hasn't been visited yet when the label is first reached, so its edge is
    // unknown; a label with such an edge falls back to clearing all knowledge,
    // matching the historical conservative behavior for loop headers.
    //
    // Tracking is not limited to raw temps: every `let`-bound value and every
    // if/match join result in Ashes IR is lowered through a mutable local slot
    // (a StoreLocal in each producing arm, a LoadLocal at the point of use —
    // see Ir.cs), never through direct temp reuse across a label. Without also
    // tracking slot-level constant state, meet-over-paths at a label would never
    // observe anything, since the value read after a join is always a brand-new
    // temp produced by a LoadLocal the pass otherwise treats as opaque. A slot
    // holds at most one of Int/Float/Bool at a time (Ashes locals are statically
    // typed); a store of an unknown or non-scalar (e.g. heap pointer, closure)
    // value kills any stale knowledge for that slot, since a slot is ordinary
    // mutable storage, not single-assignment like a temp.

    /// <summary>
    /// Known-constant state tracked during the constant-folding pass, for both raw
    /// temps (produced by literal loads or folded arithmetic) and local slots (see the
    /// remarks on <see cref="FoldConstants"/>).
    /// </summary>
    private sealed class ConstantFoldingState
    {
        public Dictionary<int, long> Ints { get; } = new();
        public Dictionary<int, double> Floats { get; } = new();
        public Dictionary<int, bool> Bools { get; } = new();
        public Dictionary<int, long> LocalInts { get; } = new();
        public Dictionary<int, double> LocalFloats { get; } = new();
        public Dictionary<int, bool> LocalBools { get; } = new();

        public ConstantFoldingState Clone()
        {
            var clone = new ConstantFoldingState();
            clone.ReplaceWith(this);
            return clone;
        }

        public void Clear()
        {
            Ints.Clear();
            Floats.Clear();
            Bools.Clear();
            LocalInts.Clear();
            LocalFloats.Clear();
            LocalBools.Clear();
        }

        public void ReplaceWith(ConstantFoldingState other)
        {
            Ints.Clear();
            foreach (var kv in other.Ints) Ints[kv.Key] = kv.Value;
            Floats.Clear();
            foreach (var kv in other.Floats) Floats[kv.Key] = kv.Value;
            Bools.Clear();
            foreach (var kv in other.Bools) Bools[kv.Key] = kv.Value;
            LocalInts.Clear();
            foreach (var kv in other.LocalInts) LocalInts[kv.Key] = kv.Value;
            LocalFloats.Clear();
            foreach (var kv in other.LocalFloats) LocalFloats[kv.Key] = kv.Value;
            LocalBools.Clear();
            foreach (var kv in other.LocalBools) LocalBools[kv.Key] = kv.Value;
        }
    }

    private static List<IrInst> FoldConstants(List<IrInst> instructions)
    {
        var state = new ConstantFoldingState();

        // Pre-scan: count how many explicit branch edges (Jump/JumpIfFalse/SwitchTag
        // cases) target each label. Combined with fall-through analysis, this tells
        // us the total predecessor count at each label.
        var branchRefs = CountBranchRefsToLabels(instructions);

        // Snapshots of constant state saved at each branch/jump/switch-case site,
        // keyed by target label, one entry per predecessor edge observed so far.
        var savedStates = new Dictionary<string, List<ConstantFoldingState>>(StringComparer.Ordinal);

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false; // tracks whether the previous instruction was Jump/Return

        foreach (var inst in instructions)
        {
            if (TryRecordConstantLoad(inst, state.Ints, state.Floats, state.Bools, result))
            {
                prevIsTerminator = false;
                continue;
            }

            if (TryFoldIntArithmetic(inst, state.Ints, result, ref changed)
                || TryFoldIntBitwise(inst, state.Ints, result, ref changed)
                || TryFoldFloatArithmetic(inst, state.Floats, result, ref changed)
                || TryFoldIntEquality(inst, state.Ints, state.Bools, result, ref changed)
                || TryFoldIntOrdering(inst, state.Ints, state.Bools, result, ref changed))
            {
                // A folded instruction leaves the terminator flag unchanged, matching the
                // original single-switch form.
                continue;
            }

            prevIsTerminator = HandleConstantControlFlow(inst, prevIsTerminator, branchRefs, state, savedStates, result, ref changed);
        }

        return changed ? result : instructions;
    }

    private static bool TryRecordConstantLoad(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, double> knownFloats,
        Dictionary<int, bool> knownBools,
        List<IrInst> result)
    {
        switch (inst)
        {
            case IrInst.LoadConstInt lci:
                knownInts[lci.Target] = lci.Value;
                result.Add(inst);
                return true;

            case IrInst.LoadConstFloat lcf:
                knownFloats[lcf.Target] = lcf.Value;
                result.Add(inst);
                return true;

            case IrInst.LoadConstBool lcb:
                knownBools[lcb.Target] = lcb.Value;
                result.Add(inst);
                return true;

            default:
                return false;
        }
    }

    private static bool TryFoldIntArithmetic(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddInt add when knownInts.ContainsKey(add.Left) && knownInts.ContainsKey(add.Right):
                {
                    long folded = knownInts[add.Left] + knownInts[add.Right];
                    knownInts[add.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(add.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.SubInt sub when knownInts.ContainsKey(sub.Left) && knownInts.ContainsKey(sub.Right):
                {
                    long folded = knownInts[sub.Left] - knownInts[sub.Right];
                    knownInts[sub.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(sub.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.MulInt mul when knownInts.ContainsKey(mul.Left) && knownInts.ContainsKey(mul.Right):
                {
                    long folded = knownInts[mul.Left] * knownInts[mul.Right];
                    knownInts[mul.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(mul.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivInt div when knownInts.ContainsKey(div.Left) && knownInts.ContainsKey(div.Right)
                                       && knownInts[div.Right] != 0:
                {
                    long folded = knownInts[div.Left] / knownInts[div.Right];
                    knownInts[div.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(div.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivUInt divU when knownInts.ContainsKey(divU.Left) && knownInts.ContainsKey(divU.Right)
                                         && knownInts[divU.Right] != 0:
                {
                    long folded = (long)((ulong)knownInts[divU.Left] / (ulong)knownInts[divU.Right]);
                    knownInts[divU.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(divU.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntBitwise(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AndInt bitAnd when knownInts.ContainsKey(bitAnd.Left) && knownInts.ContainsKey(bitAnd.Right):
                {
                    long folded = knownInts[bitAnd.Left] & knownInts[bitAnd.Right];
                    knownInts[bitAnd.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitAnd.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.OrInt bitOr when knownInts.ContainsKey(bitOr.Left) && knownInts.ContainsKey(bitOr.Right):
                {
                    long folded = knownInts[bitOr.Left] | knownInts[bitOr.Right];
                    knownInts[bitOr.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitOr.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.XorInt bitXor when knownInts.ContainsKey(bitXor.Left) && knownInts.ContainsKey(bitXor.Right):
                {
                    long folded = knownInts[bitXor.Left] ^ knownInts[bitXor.Right];
                    knownInts[bitXor.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitXor.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.ShlInt shl when knownInts.ContainsKey(shl.Left) && knownInts.ContainsKey(shl.Right):
                {
                    long folded = knownInts[shl.Left] << (int)(knownInts[shl.Right] & 63);
                    knownInts[shl.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(shl.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.ShrInt shr when knownInts.ContainsKey(shr.Left) && knownInts.ContainsKey(shr.Right):
                {
                    // Match the language spec's logical right shift: reinterpret
                    // the signed Int bits as unsigned so the high bits are zero-filled.
                    long folded = (long)((ulong)knownInts[shr.Left] >> (int)(knownInts[shr.Right] & 63));
                    knownInts[shr.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(shr.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldFloatArithmetic(IrInst inst, Dictionary<int, double> knownFloats, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddFloat addF when knownFloats.ContainsKey(addF.Left) && knownFloats.ContainsKey(addF.Right):
                {
                    double folded = knownFloats[addF.Left] + knownFloats[addF.Right];
                    knownFloats[addF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(addF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.SubFloat subF when knownFloats.ContainsKey(subF.Left) && knownFloats.ContainsKey(subF.Right):
                {
                    double folded = knownFloats[subF.Left] - knownFloats[subF.Right];
                    knownFloats[subF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(subF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.MulFloat mulF when knownFloats.ContainsKey(mulF.Left) && knownFloats.ContainsKey(mulF.Right):
                {
                    double folded = knownFloats[mulF.Left] * knownFloats[mulF.Right];
                    knownFloats[mulF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(mulF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivFloat divF when knownFloats.ContainsKey(divF.Left) && knownFloats.ContainsKey(divF.Right):
                {
                    double folded = knownFloats[divF.Left] / knownFloats[divF.Right];
                    knownFloats[divF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(divF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntEquality(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, bool> knownBools,
        List<IrInst> result,
        ref bool changed)
    {
        switch (inst)
        {
            case IrInst.CmpIntEq cmpEq when knownInts.ContainsKey(cmpEq.Left) && knownInts.ContainsKey(cmpEq.Right):
                {
                    bool folded = knownInts[cmpEq.Left] == knownInts[cmpEq.Right];
                    knownBools[cmpEq.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpEq.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntNe cmpNe when knownInts.ContainsKey(cmpNe.Left) && knownInts.ContainsKey(cmpNe.Right):
                {
                    bool folded = knownInts[cmpNe.Left] != knownInts[cmpNe.Right];
                    knownBools[cmpNe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpNe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntOrdering(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, bool> knownBools,
        List<IrInst> result,
        ref bool changed)
    {
        switch (inst)
        {
            case IrInst.CmpIntGt cmpGt when knownInts.ContainsKey(cmpGt.Left) && knownInts.ContainsKey(cmpGt.Right):
                {
                    bool folded = knownInts[cmpGt.Left] > knownInts[cmpGt.Right];
                    knownBools[cmpGt.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpGt.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntGe cmpGe when knownInts.ContainsKey(cmpGe.Left) && knownInts.ContainsKey(cmpGe.Right):
                {
                    bool folded = knownInts[cmpGe.Left] >= knownInts[cmpGe.Right];
                    knownBools[cmpGe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpGe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntLt cmpLt when knownInts.ContainsKey(cmpLt.Left) && knownInts.ContainsKey(cmpLt.Right):
                {
                    bool folded = knownInts[cmpLt.Left] < knownInts[cmpLt.Right];
                    knownBools[cmpLt.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpLt.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntLe cmpLe when knownInts.ContainsKey(cmpLe.Left) && knownInts.ContainsKey(cmpLe.Right):
                {
                    bool folded = knownInts[cmpLe.Left] <= knownInts[cmpLe.Right];
                    knownBools[cmpLe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpLe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Folds a JumpIfFalse whose condition is statically known (via FoldConstants' meet-over-paths
    /// constant propagation, including through a folded LoadLocal): a known-true condition means
    /// the false-branch (this instruction's target) is never taken, so the instruction
    /// is dropped and execution falls through to the true-branch body unchanged; a
    /// known-false condition means the false-branch is always taken, so the instruction
    /// is rewritten to an unconditional jump — ElideUnreachableCode (later in the same
    /// per-function pass sequence) strips the now-dead true-branch body up to the next
    /// label. Returns the new "previous instruction was an unconditional terminator"
    /// flag, matching <see cref="HandleConstantControlFlow"/>'s other branches.
    /// </summary>
    private static bool HandleJumpIfFalse(
        IrInst.JumpIfFalse jif,
        SourceLocation? location,
        ConstantFoldingState state,
        Dictionary<string, List<ConstantFoldingState>> savedStates,
        List<IrInst> result,
        ref bool changed)
    {
        if (state.Bools.TryGetValue(jif.CondTemp, out bool condKnown))
        {
            changed = true;
            if (condKnown)
            {
                // Always true: the edge to jif.Target is genuinely eliminated (never
                // taken), so no edge state is saved for it — a real predecessor is
                // gone, which is exactly what lets ElideUnreachableCode's fresh
                // predecessor count later recognize an orphaned label as dead.
                return false;
            }

            // Always false: the edge to jif.Target still exists — only the branch's
            // conditionality is gone, not the edge itself — so it must still
            // contribute its state snapshot, exactly as the unconditional Jump case
            // below always does.
            SaveEdgeState(jif.Target, state, savedStates);
            result.Add(new IrInst.Jump(jif.Target) { Location = location });
            return true;
        }

        // Record this edge's state snapshot for the target label — one of potentially
        // several predecessor edges that will be met together.
        SaveEdgeState(jif.Target, state, savedStates);
        result.Add(jif);
        return false; // JumpIfFalse is conditional, not a terminator
    }

    private static bool HandleSwitchTag(
        IrInst.SwitchTag sw,
        SourceLocation? location,
        ConstantFoldingState state,
        Dictionary<string, List<ConstantFoldingState>> savedStates,
        List<IrInst> result,
        ref bool changed)
    {
        // A switch on a tag that is already a known constant (a single-constructor type's
        // tag is loaded as a literal, never read from the cell) takes exactly one edge: keep
        // only that edge's state and replace the dispatch with a jump, so every other case
        // becomes an orphaned label for ElideUnreachableCode.
        if (state.Ints.TryGetValue(sw.TagTemp, out long knownTag))
        {
            string taken = sw.DefaultLabel;
            foreach (var (caseTag, caseLabel) in sw.Cases)
            {
                if (caseTag == knownTag)
                {
                    taken = caseLabel;
                    break;
                }
            }

            changed = true;
            SaveEdgeState(taken, state, savedStates);
            result.Add(new IrInst.Jump(taken) { Location = location });
            return true;
        }

        // Every case (and the default) is reached by exactly one edge — the switch itself —
        // whose source state is simply the state right before dispatch (the tag test doesn't
        // invalidate any other known fact). Recording that single snapshot per target lets the
        // label handler's ordinary meet logic apply here too, rather than needing a special case.
        foreach (var (_, caseLabel) in sw.Cases)
        {
            SaveEdgeState(caseLabel, state, savedStates);
        }

        SaveEdgeState(sw.DefaultLabel, state, savedStates);
        result.Add(sw);
        return true; // Multi-way terminator
    }

    /// <summary>
    /// Handles the control-flow and local-slot instructions of the constant-folding
    /// pass (labels, jumps, switch, StoreLocal/LoadLocal, and every unhandled
    /// instruction), appending the resulting instruction to <paramref name="result"/>.
    /// Returns the new "previous instruction was an unconditional terminator" flag.
    /// </summary>
    private static bool HandleConstantControlFlow(
        IrInst inst,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        ConstantFoldingState state,
        Dictionary<string, List<ConstantFoldingState>> savedStates,
        List<IrInst> result,
        ref bool changed)
    {
        switch (inst)
        {
            // Labels restore the meet (intersection of agreeing facts) over every
            // predecessor edge observed so far; see ApplyLabelConstantState.
            case IrInst.Label lbl:
                ApplyLabelConstantState(lbl, prevIsTerminator, branchRefs, state, savedStates);
                result.Add(inst);
                return false;

            case IrInst.JumpIfFalse jif:
                return HandleJumpIfFalse(jif, inst.Location, state, savedStates, result, ref changed);

            case IrInst.Jump jmp:
                SaveEdgeState(jmp.Target, state, savedStates);
                result.Add(inst);
                return true; // Jump is an unconditional terminator

            case IrInst.SwitchTag sw:
                return HandleSwitchTag(sw, inst.Location, state, savedStates, result, ref changed);

            case IrInst.StoreLocal store:
                RecordLocalStore(store, state);
                result.Add(inst);
                return false;

            case IrInst.LoadLocal load:
                if (TryFoldLocalLoad(load, state, out var folded))
                {
                    result.Add(folded);
                    changed = true;
                }
                else
                {
                    result.Add(inst);
                }

                return false;

            default:
                result.Add(inst);
                return inst is IrInst.Return;
        }
    }

    /// <summary>
    /// Observes a StoreLocal: if the stored value is a known scalar constant, the
    /// slot's meet-tracked state records it; otherwise (a non-constant value, or a
    /// non-scalar value such as a heap pointer or closure — those never appear in
    /// <see cref="ConstantFoldingState.Ints"/>/<c>Floats</c>/<c>Bools</c> to begin with)
    /// any stale knowledge for that slot is killed, since a slot is ordinary mutable
    /// storage, not single-assignment like a temp.
    /// </summary>
    private static void RecordLocalStore(IrInst.StoreLocal store, ConstantFoldingState state)
    {
        state.LocalInts.Remove(store.Slot);
        state.LocalFloats.Remove(store.Slot);
        state.LocalBools.Remove(store.Slot);

        if (state.Ints.TryGetValue(store.Source, out long intValue))
        {
            state.LocalInts[store.Slot] = intValue;
        }
        else if (state.Floats.TryGetValue(store.Source, out double floatValue))
        {
            state.LocalFloats[store.Slot] = floatValue;
        }
        else if (state.Bools.TryGetValue(store.Source, out bool boolValue))
        {
            state.LocalBools[store.Slot] = boolValue;
        }
    }

    /// <summary>
    /// Folds a LoadLocal into a literal load when its slot is known (via
    /// <see cref="RecordLocalStore"/>, and meet-over-paths at labels for a slot written
    /// differently — or not at all — on different incoming paths) to hold a scalar
    /// constant. This is what makes the label meet in
    /// <see cref="ApplyLabelConstantState"/> observable in real compiled programs: every
    /// `let`-bound value and if/match join result is lowered through a StoreLocal/
    /// LoadLocal round trip (Ir.cs), never through direct temp reuse across a label.
    /// </summary>
    private static bool TryFoldLocalLoad(IrInst.LoadLocal load, ConstantFoldingState state, out IrInst folded)
    {
        if (state.LocalInts.TryGetValue(load.Slot, out long intValue))
        {
            state.Ints[load.Target] = intValue;
            folded = new IrInst.LoadConstInt(load.Target, intValue) { Location = load.Location };
            return true;
        }

        if (state.LocalFloats.TryGetValue(load.Slot, out double floatValue))
        {
            state.Floats[load.Target] = floatValue;
            folded = new IrInst.LoadConstFloat(load.Target, floatValue) { Location = load.Location };
            return true;
        }

        if (state.LocalBools.TryGetValue(load.Slot, out bool boolValue))
        {
            state.Bools[load.Target] = boolValue;
            folded = new IrInst.LoadConstBool(load.Target, boolValue) { Location = load.Location };
            return true;
        }

        folded = load;
        return false;
    }

    /// <summary>
    /// Appends a snapshot of the current constant state as one more predecessor edge
    /// for <paramref name="targetLabel"/>, to be combined via meet once every edge into
    /// that label has been observed (see <see cref="ApplyLabelConstantState"/>).
    /// </summary>
    private static void SaveEdgeState(
        string targetLabel,
        ConstantFoldingState state,
        Dictionary<string, List<ConstantFoldingState>> savedStates)
    {
        if (!savedStates.TryGetValue(targetLabel, out var list))
        {
            list = new List<ConstantFoldingState>();
            savedStates[targetLabel] = list;
        }

        list.Add(state.Clone());
    }

    /// <summary>
    /// Applies the constant-state transition for a label in the constant-folding pass.
    /// If every predecessor edge into this label has already been observed by this
    /// forward scan (all branch/switch-case sites plus fall-through, if any), the
    /// entering state is the meet over those edges — a fact survives only if every
    /// edge agrees on it. This subsumes the single-predecessor case (a meet of one
    /// snapshot is that snapshot) and the fall-through-only case (a meet of just the
    /// live state is the live state) as special cases of the same computation. If some
    /// predecessor hasn't been observed yet — a backward branch (loop back-edge) whose
    /// source appears later in the instruction stream — its state is unknowable here,
    /// so all constant knowledge is conservatively cleared, matching this label's only
    /// literal fall-through predecessor edge.
    /// </summary>
    private static void ApplyLabelConstantState(
        IrInst.Label lbl,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        ConstantFoldingState state,
        Dictionary<string, List<ConstantFoldingState>> savedStates)
    {
        bool hasFallthrough = !prevIsTerminator;
        int branchCount = branchRefs.GetValueOrDefault(lbl.Name);
        int totalPredecessors = branchCount + (hasFallthrough ? 1 : 0);

        savedStates.TryGetValue(lbl.Name, out var edgeSnapshots);
        int observedPredecessors = (edgeSnapshots?.Count ?? 0) + (hasFallthrough ? 1 : 0);

        if (totalPredecessors > 0 && observedPredecessors == totalPredecessors)
        {
            // Every predecessor edge has been observed — compute the true meet.
            var edges = new List<ConstantFoldingState>(edgeSnapshots ?? []);
            if (hasFallthrough)
            {
                edges.Add(state.Clone());
            }

            var meetInts = ComputeMeet(edges.Select(e => e.Ints).ToList());
            var meetFloats = ComputeMeet(edges.Select(e => e.Floats).ToList());
            var meetBools = ComputeMeet(edges.Select(e => e.Bools).ToList());
            var meetLocalInts = ComputeMeet(edges.Select(e => e.LocalInts).ToList());
            var meetLocalFloats = ComputeMeet(edges.Select(e => e.LocalFloats).ToList());
            var meetLocalBools = ComputeMeet(edges.Select(e => e.LocalBools).ToList());

            state.Ints.Clear();
            foreach (var kv in meetInts) state.Ints[kv.Key] = kv.Value;
            state.Floats.Clear();
            foreach (var kv in meetFloats) state.Floats[kv.Key] = kv.Value;
            state.Bools.Clear();
            foreach (var kv in meetBools) state.Bools[kv.Key] = kv.Value;
            state.LocalInts.Clear();
            foreach (var kv in meetLocalInts) state.LocalInts[kv.Key] = kv.Value;
            state.LocalFloats.Clear();
            foreach (var kv in meetLocalFloats) state.LocalFloats[kv.Key] = kv.Value;
            state.LocalBools.Clear();
            foreach (var kv in meetLocalBools) state.LocalBools[kv.Key] = kv.Value;
        }
        else
        {
            // Either unreachable (no predecessors at all) or a predecessor edge
            // (a backward branch) hasn't been observed yet — clear conservatively.
            state.Clear();
        }

        // Clean up any saved state for this label.
        savedStates.Remove(lbl.Name);
    }

    /// <summary>
    /// Computes the meet (intersection of agreeing facts) over a set of constant-state
    /// snapshots from different predecessor edges into the same label: a key survives
    /// only if it is present with the same value in every snapshot.
    /// </summary>
    private static Dictionary<int, TValue> ComputeMeet<TValue>(List<Dictionary<int, TValue>> edgeSnapshots)
    {
        if (edgeSnapshots.Count == 0)
        {
            return [];
        }

        var meet = new Dictionary<int, TValue>(edgeSnapshots[0]);
        for (int i = 1; i < edgeSnapshots.Count && meet.Count > 0; i++)
        {
            var other = edgeSnapshots[i];
            foreach (var key in meet.Keys.ToArray())
            {
                if (!other.TryGetValue(key, out var otherValue) || !EqualityComparer<TValue>.Default.Equals(meet[key], otherValue))
                {
                    meet.Remove(key);
                }
            }
        }

        return meet;
    }

    // Identity elimination and strength reduction
    // Simplify arithmetic with known identity values:
    //   x + 0 → x, 0 + x → x, x - 0 → x
    //   x * 1 → x, 1 * x → x, x * 0 → 0, 0 * x → 0
    //   x / 1 → x
    //   x * 2 → x + x (strength reduction)

    private static List<IrInst> ReduceIdentitiesAndStrength(List<IrInst> instructions)
    {
        var knownInts = new Dictionary<int, long>();
        var branchRefs = CountBranchRefsToLabels(instructions);
        var savedIntStates = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false;

        foreach (var inst in instructions)
        {
            if (inst is IrInst.LoadConstInt lci)
            {
                knownInts[lci.Target] = lci.Value;
                result.Add(inst);
                prevIsTerminator = false;
                continue;
            }

            if (TryReduceIntAddSub(inst, knownInts, result, ref changed)
                || TryReduceIntMul(inst, knownInts, result, ref changed)
                || TryReduceIntDiv(inst, knownInts, result, ref changed))
            {
                // A rewritten (or passed-through) arithmetic instruction leaves the
                // terminator flag unchanged, matching the original single-switch form.
                continue;
            }

            prevIsTerminator = HandleIdentityControlFlow(inst, prevIsTerminator, branchRefs, knownInts, savedIntStates, result);
        }

        return changed ? result : instructions;
    }

    private static bool TryReduceIntAddSub(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddInt add:
                {
                    bool leftZero = knownInts.TryGetValue(add.Left, out long lv) && lv == 0;
                    bool rightZero = knownInts.TryGetValue(add.Right, out long rv) && rv == 0;
                    if (leftZero)
                    {
                        // 0 + x → x: copy Right → Target
                        result.Add(new IrInst.Borrow(add.Target, add.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightZero)
                    {
                        // x + 0 → x: copy Left → Target
                        result.Add(new IrInst.Borrow(add.Target, add.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            case IrInst.SubInt sub:
                {
                    bool rightZero = knownInts.TryGetValue(sub.Right, out long rv) && rv == 0;
                    if (rightZero)
                    {
                        // x - 0 → x
                        result.Add(new IrInst.Borrow(sub.Target, sub.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryReduceIntMul(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.MulInt mul:
                {
                    bool leftKnown = knownInts.TryGetValue(mul.Left, out long lv);
                    bool rightKnown = knownInts.TryGetValue(mul.Right, out long rv);

                    if ((leftKnown && lv == 0) || (rightKnown && rv == 0))
                    {
                        // x * 0 or 0 * x → 0
                        result.Add(new IrInst.LoadConstInt(mul.Target, 0) { Location = inst.Location });
                        changed = true;
                    }
                    else if (leftKnown && lv == 1)
                    {
                        // 1 * x → x
                        result.Add(new IrInst.Borrow(mul.Target, mul.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightKnown && rv == 1)
                    {
                        // x * 1 → x
                        result.Add(new IrInst.Borrow(mul.Target, mul.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightKnown && rv == 2)
                    {
                        // x * 2 → x + x (strength reduction)
                        result.Add(new IrInst.AddInt(mul.Target, mul.Left, mul.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else if (leftKnown && lv == 2)
                    {
                        // 2 * x → x + x (strength reduction)
                        result.Add(new IrInst.AddInt(mul.Target, mul.Right, mul.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryReduceIntDiv(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.DivInt div:
                {
                    bool rightOne = knownInts.TryGetValue(div.Right, out long rv) && rv == 1;
                    if (rightOne)
                    {
                        // x / 1 → x
                        result.Add(new IrInst.Borrow(div.Target, div.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            case IrInst.DivUInt divU:
                {
                    bool rightOne = knownInts.TryGetValue(divU.Right, out long rvu) && rvu == 1;
                    if (rightOne)
                    {
                        // x / 1 → x
                        result.Add(new IrInst.Borrow(divU.Target, divU.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles the control-flow instructions of the identity-reduction pass (labels, jumps,
    /// switch, and every unhandled instruction), appending the instruction to
    /// <paramref name="result"/>. Returns the new "previous instruction was an
    /// unconditional terminator" flag.
    /// </summary>
    private static bool HandleIdentityControlFlow(
        IrInst inst,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        Dictionary<int, long> knownInts,
        Dictionary<string, Dictionary<int, long>> savedIntStates,
        List<IrInst> result)
    {
        switch (inst)
        {
            // Labels: preserve state across single-predecessor labels.
            case IrInst.Label lbl:
                {
                    bool hasFallthrough = !prevIsTerminator;
                    int branchCount = branchRefs.GetValueOrDefault(lbl.Name);
                    int totalPredecessors = branchCount + (hasFallthrough ? 1 : 0);

                    if (totalPredecessors <= 1 && savedIntStates.TryGetValue(lbl.Name, out var savedInts) && !hasFallthrough)
                    {
                        knownInts.Clear();
                        foreach (var kv in savedInts) knownInts[kv.Key] = kv.Value;
                    }
                    else if (totalPredecessors <= 1 && hasFallthrough && branchCount == 0)
                    {
                        // Fall-through-only — keep current state.
                    }
                    else
                    {
                        knownInts.Clear();
                    }

                    savedIntStates.Remove(lbl.Name);
                    result.Add(inst);
                    return false;
                }

            case IrInst.JumpIfFalse jif:
                savedIntStates[jif.Target] = new Dictionary<int, long>(knownInts);
                result.Add(inst);
                return false;

            case IrInst.Jump jmp:
                savedIntStates[jmp.Target] = new Dictionary<int, long>(knownInts);
                result.Add(inst);
                return true;

            case IrInst.SwitchTag:
                // Multi-way terminator — see the matching note in the constant-folding pass.
                result.Add(inst);
                return true;

            default:
                result.Add(inst);
                return inst is IrInst.Return;
        }
    }

    // Local common-subexpression elimination and store-to-load forwarding
    // Forwards a duplicate GetAdtField read, a duplicate CallKnown call to a provably pure
    // function, or a GetAdtField immediately following the SetAdtField that established the same
    // (pointer, field)'s value (through a pointer proven fresh in this block — e.g. a record
    // constructed and immediately destructured) to the first occurrence's/write's value, scoped to
    // a single straight-line block (reset at every Label), never across control flow. Operands are
    // canonicalized through a LoadLocal/StoreLocal/Borrow/RcDup alias map before keying the cache —
    // without it, the ubiquitous `let x = p.x in let y = p.x` shape never matches, since each
    // LoadLocal of the same never-rewritten slot produces a fresh temp for what is provably the
    // same value (the same lesson the constant-propagation pass above learned: real Ashes IR round-trips almost everything
    // through local slots, so raw-temp-identity-only tracking misses nearly every real occurrence).

    private static readonly HashSet<Type> LocalCseSafeInstructionTypes =
    [
        typeof(IrInst.LoadConstInt), typeof(IrInst.LoadConstFloat), typeof(IrInst.LoadConstBool),
        typeof(IrInst.LoadConstStr), typeof(IrInst.LoadLocal), typeof(IrInst.StoreLocal),
        typeof(IrInst.RcDup), typeof(IrInst.Borrow),
        typeof(IrInst.AddInt), typeof(IrInst.SubInt), typeof(IrInst.MulInt), typeof(IrInst.DivInt),
        typeof(IrInst.DivUInt), typeof(IrInst.AndInt), typeof(IrInst.OrInt), typeof(IrInst.XorInt),
        typeof(IrInst.ShlInt), typeof(IrInst.ShrInt),
        typeof(IrInst.AddFloat), typeof(IrInst.SubFloat), typeof(IrInst.MulFloat), typeof(IrInst.DivFloat),
        typeof(IrInst.IntToFloat), typeof(IrInst.FloatToInt),
        typeof(IrInst.CmpIntGt), typeof(IrInst.CmpIntGe), typeof(IrInst.CmpIntLt), typeof(IrInst.CmpIntLe),
        typeof(IrInst.CmpUIntGt), typeof(IrInst.CmpUIntGe), typeof(IrInst.CmpUIntLt), typeof(IrInst.CmpUIntLe),
        typeof(IrInst.CmpIntEq), typeof(IrInst.CmpIntNe),
        typeof(IrInst.CmpFloatGt), typeof(IrInst.CmpFloatGe), typeof(IrInst.CmpFloatLt), typeof(IrInst.CmpFloatLe),
        typeof(IrInst.CmpFloatEq), typeof(IrInst.CmpFloatNe),
        typeof(IrInst.CmpStrEq), typeof(IrInst.CmpStrNe),
        typeof(IrInst.GetAdtTag), typeof(IrInst.LoadArgumentOwnership), typeof(IrInst.LoadFuncAddr),
        // Arena/stack bookkeeping: these move an allocator cursor/watermark, never write through
        // an existing pointer. A value already copied out into a temp (a cached field read or
        // pure-call result) is not affected by a later bracket closing over allocations made
        // since — every `let` binding gets its own such bracket in practice, so treating these as
        // "could alias" would silence local CSE almost everywhere real Ashes code binds a value.
        typeof(IrInst.SaveArenaState), typeof(IrInst.RestoreArenaState), typeof(IrInst.ReclaimArenaChunks),
        typeof(IrInst.SaveStackPointer), typeof(IrInst.RestoreStackPointer),
    ];

    // Deny-by-default: only the instruction kinds explicitly listed above are known not to write
    // through an existing pointer or call into code with unmodeled effects. Everything else —
    // every allocation/reuse/SetAdtField/RcDrop variant, every non-pure call, and any instruction
    // added to the IR in the future — conservatively invalidates both caches, since an in-place
    // reuse write is exactly the kind of alias a raw temp-identity cache cannot see coming.
    private static bool LocalCseNeverAliasesHeapMemory(IrInst inst) =>
        LocalCseSafeInstructionTypes.Contains(inst.GetType());

    private sealed class LocalCseState
    {
        public readonly Dictionary<(int Ptr, int FieldIndex), int> FieldCache = [];
        public readonly Dictionary<(string FuncLabel, int EnvTemp, int ArgTemp, int FlagTemp, bool StackAllocated), int> CallCache = [];
        public readonly Dictionary<int, int> ValueOf = []; // temp -> canonical earlier temp with the same value
        public readonly Dictionary<int, int> SlotValue = []; // local slot -> canonical temp currently stored there

        // Pointers known to be a fresh allocation from this same straight-line block:
        // nothing that existed before this instruction could hold or derive a reference to memory
        // that didn't exist yet, so a SetAdtField through one of these can populate FieldCache
        // precisely (just this one (ptr, field) entry) instead of falling back to a full clear —
        // unlike a SetAdtField through an arbitrary (possibly-aliased) pointer, which still forces
        // the conservative full invalidation below.
        public readonly HashSet<int> FreshPointers = [];

        public bool HasEnvAndArgParams;

        // Negative, so it can never collide with a real (always non-negative) temp number — the
        // stable identity of the value the backend's entry prologue stores into env/arg slot 0/1
        // before any IrInst the optimizer can see.
        private static int EntrySlotIdentity(int slot) => -1 - slot;

        public int Resolve(int temp)
        {
            while (ValueOf.TryGetValue(temp, out int alias))
            {
                temp = alias;
            }

            return temp;
        }

        public void ResetBlock()
        {
            FieldCache.Clear();
            CallCache.Clear();
            ValueOf.Clear();
            SlotValue.Clear();
            FreshPointers.Clear();
            if (HasEnvAndArgParams)
            {
                SlotValue[0] = EntrySlotIdentity(0);
                SlotValue[1] = EntrySlotIdentity(1);
            }
        }
    }

    // Tracks pure value-identity aliases (LoadLocal/StoreLocal/Borrow/RcDup) so the field/call
    // caches below can be keyed by canonical value rather than raw temp. Returns true when the
    // instruction was one of these alias producers (already fully handled by the caller).
    private static bool TryTrackLocalCseAlias(IrInst inst, LocalCseState state)
    {
        switch (inst)
        {
            case IrInst.Borrow b: state.ValueOf[b.Target] = state.Resolve(b.SourceTemp); return true;
            case IrInst.RcDup d: state.ValueOf[d.Target] = state.Resolve(d.SourceTemp); return true;
            case IrInst.StoreLocal sl: state.SlotValue[sl.Slot] = state.Resolve(sl.Source); return true;
            case IrInst.LoadLocal ll:
                if (state.SlotValue.TryGetValue(ll.Slot, out int known))
                {
                    state.ValueOf[ll.Target] = known;
                }
                else
                {
                    state.ValueOf.Remove(ll.Target);
                }

                return true;
            default:
                return false;
        }
    }

    private static bool TryEliminateLocalCseField(IrInst inst, LocalCseState state, out IrInst rewritten)
    {
        if (inst is not IrInst.GetAdtField gaf)
        {
            rewritten = inst;
            return false;
        }

        var key = (state.Resolve(gaf.Ptr), gaf.FieldIndex);
        if (state.FieldCache.TryGetValue(key, out int cached))
        {
            rewritten = new IrInst.Borrow(gaf.Target, cached) { Location = inst.Location };
            state.ValueOf[gaf.Target] = cached;
        }
        else
        {
            state.FieldCache[key] = gaf.Target;
            rewritten = inst;
        }

        return true;
    }

    // Fresh allocations: the target can't alias anything that existed before it, so a
    // later SetAdtField through it can update FieldCache precisely instead of invalidating it.
    private static bool TryTrackFreshAllocation(IrInst inst, LocalCseState state)
    {
        switch (inst)
        {
            case IrInst.AllocAdt allocAdt: state.FreshPointers.Add(allocAdt.Target); return true;
            case IrInst.AllocAdtStack allocAdtStack: state.FreshPointers.Add(allocAdtStack.Target); return true;
            default: return false;
        }
    }

    // Store-to-load forwarding: a SetAdtField through a pointer already proven fresh in
    // this block populates FieldCache directly, so an immediately-following GetAdtField of the
    // same (ptr, field) forwards the stored value instead of round-tripping through memory (the
    // `Point(p.y, p.x)`-style construct-then-destructure shape). Writing through a NOT-known-fresh
    // pointer keeps the existing fully-conservative behavior (handled by the caller's fallback),
    // since it could alias any entry already in the cache.
    private static bool TryForwardSetAdtField(IrInst inst, LocalCseState state)
    {
        if (inst is not IrInst.SetAdtField saf)
        {
            return false;
        }

        int resolvedPtr = state.Resolve(saf.Ptr);
        if (!state.FreshPointers.Contains(resolvedPtr))
        {
            return false;
        }

        // The cached value must be a real, already-emitted temp — never Resolve(saf.Source):
        // Resolve can return a synthetic, negative sentinel identity (see LocalCseState's
        // EntrySlotIdentity) when the source traces back to a function's own env/arg slot with
        // no real defining instruction visible to this pass. A sentinel is only ever safe as a
        // cache KEY (for matching two operands as "the same value"); emitting it as a Borrow's
        // source temp would reference a temp that doesn't exist. saf.Source itself is always a
        // real, live temp at this point, exactly like gaf.Target/ck.Target are in the read-side
        // caches above.
        state.FieldCache[(resolvedPtr, saf.FieldIndex)] = saf.Source;
        return true;
    }

    private static bool TryEliminateLocalCseCall(
        IrInst inst, LocalCseState state, HashSet<string> evaluableFunctions, out IrInst rewritten)
    {
        if (inst is not IrInst.CallKnown ck || !evaluableFunctions.Contains(ck.FuncLabel))
        {
            rewritten = inst;
            return false;
        }

        var key = (ck.FuncLabel, state.Resolve(ck.EnvTemp), state.Resolve(ck.ArgTemp),
            state.Resolve(ck.RuntimeManagedArgumentFlagTemp), ck.EnvironmentIsStackAllocated);
        if (state.CallCache.TryGetValue(key, out int cached))
        {
            rewritten = new IrInst.Borrow(ck.Target, cached) { Location = inst.Location };
            state.ValueOf[ck.Target] = cached;
        }
        else
        {
            state.CallCache[key] = ck.Target;
            rewritten = inst;
        }

        return true;
    }

    private static List<IrInst> EliminateLocalRedundantComputation(
        List<IrInst> instructions,
        HashSet<string> evaluableFunctions,
        bool hasEnvAndArgParams)
    {
        var result = new List<IrInst>(instructions.Count);
        var state = new LocalCseState { HasEnvAndArgParams = hasEnvAndArgParams };

        // Slots 0 (env) and 1 (arg) are populated by the backend's function-entry prologue — a
        // native store the caller never sees as an IrInst.StoreLocal — so without seeding them
        // here, every LoadLocal of a function's own argument looks like an unknown value and two
        // reads of the same argument field never canonicalize to the same key. Re-seeded by
        // ResetBlock too: the slots stay stable for the whole function, not just its first block.
        state.ResetBlock();

        foreach (var inst in instructions)
        {
            if (inst is IrInst.Label)
            {
                state.ResetBlock();
                result.Add(inst);
                continue;
            }

            if (TryTrackLocalCseAlias(inst, state))
            {
                result.Add(inst);
                continue;
            }

            if (TryTrackFreshAllocation(inst, state))
            {
                result.Add(inst);
                continue;
            }

            if (TryEliminateLocalCseField(inst, state, out var rewrittenField))
            {
                result.Add(rewrittenField);
                continue;
            }

            if (TryForwardSetAdtField(inst, state))
            {
                result.Add(inst);
                continue;
            }

            if (TryEliminateLocalCseCall(inst, state, evaluableFunctions, out var rewrittenCall))
            {
                result.Add(rewrittenCall);
                continue;
            }

            if (!LocalCseNeverAliasesHeapMemory(inst))
            {
                state.FieldCache.Clear();
                state.CallCache.Clear();
            }

            result.Add(inst);
        }

        return result;
    }

    // Unreachable code elimination
    // Remove instructions after unconditional jumps or returns until the
    // next label (which re-establishes reachability).

    private static List<IrInst> ElideUnreachableCode(List<IrInst> instructions)
    {
        // Built fresh over this pass's own input (not shared with FoldConstants' pre-fold
        // count): the constant-condition branch folding above can remove the only edge that used to target a
        // label (e.g. a JumpIfFalse dropped because its condition is statically true), so a
        // label's real predecessor count can differ from what it was before folding. Uses the
        // shared IrControlFlowGraph rather than an ad hoc explicit-branch-only count:
        // gated by `unreachable`, which only becomes true right after a Jump/Return/SwitchTag —
        // the same three instruction kinds IrControlFlowGraph never adds a fall-through edge
        // after — a block's CFG predecessor count in that state is exactly its explicit-branch
        // count, so this is equivalent to (not just an approximation of) the earlier check.
        var cfgBlocks = IrControlFlowGraph.Build(instructions);
        var labelBlocks = IrControlFlowGraph.IndexLabels(instructions, cfgBlocks);
        var result = new List<IrInst>(instructions.Count);
        bool unreachable = false;
        bool changed = false;

        foreach (var inst in instructions)
        {
            if (inst is IrInst.Label lbl)
            {
                // A label re-establishes reachability only if something can actually
                // reach it: either it wasn't unreachable to begin with, or an explicit
                // branch still targets it. A label with zero incoming edges while
                // already in an unreachable region has no way to be entered, so it and
                // its body stay dead and are dropped together — this is what makes a
                // statically-true JumpIfFalse's now-orphaned false-arm actually vanish,
                // not just become unreachable code the compiler still emits.
                if (unreachable && cfgBlocks[labelBlocks[lbl.Name]].Predecessors.Count == 0)
                {
                    changed = true;
                    continue;
                }

                unreachable = false;
                result.Add(inst);
                continue;
            }

            if (unreachable)
            {
                // Skip instructions after an unconditional terminator.
                changed = true;
                continue;
            }

            result.Add(inst);

            // Unconditional terminators: Jump, Return, and SwitchTag make subsequent code
            // unreachable until the next label.
            if (inst is IrInst.Jump or IrInst.Return or IrInst.SwitchTag)
            {
                unreachable = true;
            }
        }

        return changed ? result : instructions;
    }

    // Dead code elimination
    // Remove LoadConst instructions whose target temp is never used,
    // and StoreLocal instructions whose slot is never loaded.

    private static List<IrInst> ElideDeadCode(List<IrInst> instructions)
    {
        // Run to a fixed point: removing a dead StoreLocal may leave its
        // source temp with no remaining uses, making the producing LoadConst*
        // dead as well. Iterate until no further instructions are removed.
        var current = instructions;
        while (true)
        {
            var result = ElideDeadCodeOnce(current);
            if (ReferenceEquals(result, current))
            {
                return result;
            }

            current = result;
        }
    }

    private static List<IrInst> ElideDeadCodeOnce(List<IrInst> instructions)
    {
        // Collect all temps that are used as operands (sources)
        var usedTemps = new HashSet<int>();
        foreach (var inst in instructions)
        {
            CollectUsedTemps(inst, usedTemps);
        }

        // Collect all local slots read explicitly or implicitly. Arena reset instructions read
        // watermark slots without a LoadLocal, so treating only LoadLocal as a use can delete the
        // coroutine stores that restore those watermarks after suspension.
        var loadedSlots = new HashSet<int>();
        foreach (var inst in instructions)
        {
            foreach (int slot in StateMachineTransform.GetReadLocalSlots(inst))
            {
                loadedSlots.Add(slot);
            }
        }

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;

        foreach (var inst in instructions)
        {
            if (IsDeadInstruction(inst, usedTemps, loadedSlots))
            {
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? result : instructions;
    }

    /// <summary>
    /// Returns true if the instruction is removable dead code: its result is observed
    /// by no remaining use.
    /// </summary>
    private static bool IsDeadInstruction(IrInst inst, HashSet<int> usedTemps, HashSet<int> loadedSlots)
    {
        // Remove LoadConst* instructions whose target is never read
        if (inst is IrInst.LoadConstInt lci && !usedTemps.Contains(lci.Target))
        {
            return true;
        }

        if (inst is IrInst.LoadConstFloat lcf && !usedTemps.Contains(lcf.Target))
        {
            return true;
        }

        if (inst is IrInst.LoadConstBool lcb && !usedTemps.Contains(lcb.Target))
        {
            return true;
        }

        // Remove StoreLocal instructions whose slot is never loaded
        if (inst is IrInst.StoreLocal sl && !loadedSlots.Contains(sl.Slot))
        {
            return true;
        }

        // Remove closure constructions whose target is never read — a pure arena/stack
        // allocation plus stores into that fresh allocation, observable by nothing once the
        // pointer is unused. Devirtualization routinely strands these.
        if (inst is IrInst.MakeClosure mc && !usedTemps.Contains(mc.Target))
        {
            return true;
        }

        if (inst is IrInst.MakeClosureStack mcs && !usedTemps.Contains(mcs.Target))
        {
            return true;
        }

        return false;
    }

    // Erased RC marker elision
    // RcDrop has no runtime behavior while arenas own ordinary heap reclamation. Remove each marker
    // and its otherwise-dead LoadLocal, then remove stores to slots with no remaining loads.
    // CleanupResource is a separate instruction and can never enter this pass.
    private static List<IrInst> ElideErasedRcDrops(List<IrInst> instructions)
    {
        // Build analysis data.

        // Map: temp → instruction index of the instruction that defines it.
        var tempDefinedAt = new Dictionary<int, int>();

        // Count how many times each temp is read as a source operand.
        var useCount = new Dictionary<int, int>();
        var tempBuf = new HashSet<int>();

        for (int i = 0; i < instructions.Count; i++)
        {
            var inst = instructions[i];

            // Track definitions from LoadLocal (these feed RcDrop markers).
            if (inst is IrInst.LoadLocal ll)
            {
                tempDefinedAt[ll.Target] = i;
            }

            tempBuf.Clear();
            CollectUsedTemps(inst, tempBuf);
            foreach (var t in tempBuf)
            {
                useCount[t] = useCount.GetValueOrDefault(t) + 1;
            }
        }

        // Identify erased RcDrop markers and their feeding LoadLocals.
        var toRemove = CollectElidableDropRemovals(instructions, tempDefinedAt, useCount);

        if (toRemove.Count == 0)
        {
            return instructions;
        }

        AddDeadStoresAfterDropElision(instructions, toRemove);

        // Rebuild the instruction list excluding removed instructions.
        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!toRemove.Contains(i))
            {
                result.Add(instructions[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the instruction indices of erased RcDrop markers and of the LoadLocals that feed
    /// them and have no other use.
    /// </summary>
    private static HashSet<int> CollectElidableDropRemovals(List<IrInst> instructions, Dictionary<int, int> tempDefinedAt, Dictionary<int, int> useCount)
    {
        var toRemove = new HashSet<int>();

        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.RcDrop { RuntimeManaged: false } drop) continue;

            // Erased ordinary-value marker: safe to elide while arenas own reclamation.
            toRemove.Add(i);

            // If the LoadLocal feeding this Drop has its target used only here,
            // remove the LoadLocal too.
            if (tempDefinedAt.TryGetValue(drop.SourceTemp, out int defIdx)
                && instructions[defIdx] is IrInst.LoadLocal
                && useCount.GetValueOrDefault(drop.SourceTemp) <= 1)
            {
                toRemove.Add(defIdx);
            }
        }

        return toRemove;
    }

    /// <summary>
    /// Checks for StoreLocals to slots that have no remaining LoadLocals.
    /// After removing drop-related LoadLocals, some slots may have zero loads,
    /// making their StoreLocals dead code; their indices are added to
    /// <paramref name="toRemove"/>.
    /// </summary>
    private static void AddDeadStoresAfterDropElision(List<IrInst> instructions, HashSet<int> toRemove)
    {
        var slotLoadCount = new Dictionary<int, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i)) continue;
            foreach (int slot in StateMachineTransform.GetReadLocalSlots(instructions[i]))
            {
                slotLoadCount[slot] = slotLoadCount.GetValueOrDefault(slot) + 1;
            }
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i)) continue;
            if (instructions[i] is IrInst.StoreLocal sl
                && slotLoadCount.GetValueOrDefault(sl.Slot) == 0)
            {
                toRemove.Add(i);
            }
        }
    }

    /// <summary>
    /// Collects all temp indices that are read (used as operands) by an instruction.
    /// This does NOT include target/destination temps — only sources.
    /// Dispatches through per-group collectors; every instruction kind with source
    /// temps is handled by exactly one collector, and the others fall through as no-ops.
    /// </summary>
    internal static void CollectUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        CollectIntArithmeticUsedTemps(inst, usedTemps);
        CollectFloatAndRegexUsedTemps(inst, usedTemps);
        CollectComparisonUsedTemps(inst, usedTemps);
        CollectStringMemoryClosureUsedTemps(inst, usedTemps);
        CollectAdtAndFileUsedTemps(inst, usedTemps);
        CollectTextAndBigIntUsedTemps(inst, usedTemps);
        CollectProcessUsedTemps(inst, usedTemps);
        CollectNetworkUsedTemps(inst, usedTemps);
        CollectBytesUsedTemps(inst, usedTemps);
        CollectBytesEncodingUsedTemps(inst, usedTemps);
        CollectOwnershipUsedTemps(inst, usedTemps);
        CollectTaskParallelUsedTemps(inst, usedTemps);
        CollectNetTaskUsedTemps(inst, usedTemps);
        CollectTlsTaskUsedTemps(inst, usedTemps);
        CollectSuspendControlUsedTemps(inst, usedTemps);

        // LoadConstInt, LoadConstFloat, LoadConstBool, LoadConstStr, LoadLocal,
        // LoadEnv, LoadProgramArgs, ReadLine, Alloc, AllocAdt, Label, Jump:
        // These either have no source temps or only define targets.
    }

    private static void CollectIntArithmeticUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulInt m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.DivUInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.AndInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.OrInt o: usedTemps.Add(o.Left); usedTemps.Add(o.Right); break;
            case IrInst.XorInt x: usedTemps.Add(x.Left); usedTemps.Add(x.Right); break;
            case IrInst.ShlInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.ShrInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
        }
    }

    private static void CollectFloatAndRegexUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddFloat a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubFloat s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulFloat m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivFloat d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.IntToFloat i: usedTemps.Add(i.ValueTemp); break;
            case IrInst.FloatToInt f: usedTemps.Add(f.ValueTemp); break;
            case IrInst.FloatUnaryIntrinsic u: usedTemps.Add(u.ValueTemp); break;
            case IrInst.CallLibm c: foreach (int a in c.Args) { usedTemps.Add(a); } break;
            case IrInst.RegexCompile c: usedTemps.Add(c.Pattern); break;
            case IrInst.RegexCompileError c: usedTemps.Add(c.Pattern); break;
            case IrInst.RegexFind c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Start); break;
            case IrInst.RegexCaptures c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Start); break;
            case IrInst.RegexSubstitute c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Replacement); break;
        }
    }

    private static void CollectComparisonUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        CollectUnsignedComparisonUsedTemps(inst, usedTemps);
        switch (inst)
        {
            case IrInst.CmpIntGt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
        }
    }

    private static void CollectUnsignedComparisonUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CmpUIntGt comparison: usedTemps.Add(comparison.Left); usedTemps.Add(comparison.Right); break;
            case IrInst.CmpUIntGe comparison: usedTemps.Add(comparison.Left); usedTemps.Add(comparison.Right); break;
            case IrInst.CmpUIntLt comparison: usedTemps.Add(comparison.Left); usedTemps.Add(comparison.Right); break;
            case IrInst.CmpUIntLe comparison: usedTemps.Add(comparison.Left); usedTemps.Add(comparison.Right); break;
        }
    }

    private static void CollectStringMemoryClosureUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CmpStrEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStr c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStrTip c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.StoreLocal s: usedTemps.Add(s.Source); break;
            case IrInst.StoreMemOffset s: usedTemps.Add(s.BasePtr); usedTemps.Add(s.Source); break;
            case IrInst.LoadMemOffset l: usedTemps.Add(l.BasePtr); break;
            case IrInst.MakeClosure mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.MakeClosureStack mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.CallClosure cc:
                usedTemps.Add(cc.ClosureTemp);
                usedTemps.Add(cc.ArgTemp);
                if (cc.RuntimeManagedArgumentFlagTemp >= 0)
                {
                    usedTemps.Add(cc.RuntimeManagedArgumentFlagTemp);
                }
                break;
            case IrInst.CallKnown ck:
                usedTemps.Add(ck.EnvTemp);
                usedTemps.Add(ck.ArgTemp);
                if (ck.RuntimeManagedArgumentFlagTemp >= 0)
                {
                    usedTemps.Add(ck.RuntimeManagedArgumentFlagTemp);
                }
                break;
            case IrInst.ToCString c: usedTemps.Add(c.StrTemp); break;
            case IrInst.CallExternal c:
                foreach (var argTemp in c.ArgTemps) usedTemps.Add(argTemp);
                break;
        }
    }

    private static void CollectAdtAndFileUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.LoadFfiOut load: usedTemps.Add(load.SlotTemp); break;
            case IrInst.CopyFfiString copy: usedTemps.Add(copy.PointerTemp); break;
            case IrInst.CopyFfiBytes copy: usedTemps.Add(copy.PointerTemp); usedTemps.Add(copy.LengthTemp); break;
            case IrInst.SetAdtField sf: usedTemps.Add(sf.Ptr); usedTemps.Add(sf.Source); break;
            case IrInst.GetAdtTag gt: usedTemps.Add(gt.Ptr); break;
            case IrInst.GetAdtField gf: usedTemps.Add(gf.Ptr); break;
            case IrInst.PrintInt p: usedTemps.Add(p.Source); break;
            case IrInst.PrintStr p: usedTemps.Add(p.Source); break;
            case IrInst.PrintBool p: usedTemps.Add(p.Source); break;
            case IrInst.WriteStr or IrInst.WriteBufferedStr or IrInst.WriteErrorStr: usedTemps.Add(GetWriteSource(inst)); break;
            case IrInst.ExitProcess e: usedTemps.Add(e.Source); break;
        }

        CollectFileAndEnvironmentUsedTemps(inst, usedTemps);
    }

    private static void CollectFileAndEnvironmentUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.FileReadText f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileWriteText f: usedTemps.Add(f.PathTemp); usedTemps.Add(f.TextTemp); break;
            case IrInst.FileExists f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileReplace f: usedTemps.Add(f.SourceTemp); usedTemps.Add(f.DestinationTemp); break;
            case IrInst.FileMakeExecutable f: usedTemps.Add(f.PathTemp); break;
            case IrInst.DirectoryEntries d: usedTemps.Add(d.PathTemp); break;
            case IrInst.DirectoryCreateAll d: usedTemps.Add(d.PathTemp); break;
            case IrInst.DirectoryRemoveTree d: usedTemps.Add(d.PathTemp); break;
            case IrInst.EnvironmentGet e: usedTemps.Add(e.NameTemp); break;
            case IrInst.FileOpen f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileReadChunk f: usedTemps.Add(f.HandleTemp); usedTemps.Add(f.CountTemp); break;
            case IrInst.FileReadLine f: usedTemps.Add(f.HandleTemp); break;
            case IrInst.FileClose f: usedTemps.Add(f.HandleTemp); break;
        }
    }

    private static int GetWriteSource(IrInst instruction) => instruction switch
    {
        IrInst.WriteStr write => write.Source,
        IrInst.WriteBufferedStr write => write.Source,
        IrInst.WriteErrorStr write => write.Source,
        _ => throw new ArgumentOutOfRangeException(nameof(instruction))
    };

    private static void CollectTextAndBigIntUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.TextUncons t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextUnconsText t: usedTemps.Add(t.TextTemp); break;
            case IrInst.RuneToText t: usedTemps.Add(t.RuneTemp); break;
            case IrInst.RuneFromInt t: usedTemps.Add(t.IntTemp); break;
            case IrInst.TextParseInt t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextParseFloat t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextFromInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextFromFloat t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextFormatFloat t: usedTemps.Add(t.ValueTemp); usedTemps.Add(t.DecimalsTemp); break;
            case IrInst.BigIntFromInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntToString t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntToInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntFromString t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntBinary t: usedTemps.Add(t.Left); usedTemps.Add(t.Right); break;
            case IrInst.BigIntCompare t: usedTemps.Add(t.Left); usedTemps.Add(t.Right); break;
            case IrInst.TextToHex t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextAsciiCase t: usedTemps.Add(t.SourceTemp); break;
            case IrInst.TextByteLength t: usedTemps.Add(t.TextTemp); break;
        }
    }

    private static void CollectProcessUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.ReadExact r: usedTemps.Add(r.CountTemp); break;
            case IrInst.ConsolePoll cp: usedTemps.Add(cp.TimeoutTemp); break;
            case IrInst.FileReadAllBytes f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileMmap f: usedTemps.Add(f.PathTemp); break;
            case IrInst.SpawnProcess s: usedTemps.Add(s.ExeTemp); usedTemps.Add(s.ArgsTemp); break;
            case IrInst.ProcessWriteStdin p: usedTemps.Add(p.ProcessTemp); usedTemps.Add(p.TextTemp); break;
            case IrInst.ProcessReadStdoutLine p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessReadStderrLine p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessWaitForExit p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessKill p: usedTemps.Add(p.ProcessTemp); break;
        }
    }

    private static void CollectNetworkUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.HttpGet h: usedTemps.Add(h.UrlTemp); break;
            case IrInst.HttpPost h: usedTemps.Add(h.UrlTemp); usedTemps.Add(h.BodyTemp); break;
            case IrInst.NetTcpConnect n: usedTemps.Add(n.HostTemp); usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpSend n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.TextTemp); break;
            case IrInst.NetTcpReceive n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.MaxBytesTemp); break;
            case IrInst.NetTcpClose n: usedTemps.Add(n.SocketTemp); break;
            case IrInst.NetTcpListen n: usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpAccept n: usedTemps.Add(n.SocketTemp); break;
        }
    }

    private static void CollectBytesUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        if (CollectBytesUpdateUsedTemps(inst, usedTemps))
        {
            return;
        }

        switch (inst)
        {
            case IrInst.BytesEmpty: break;
            case IrInst.BytesSingleton b: usedTemps.Add(b.ByteTemp); break;
            case IrInst.BytesLength b: usedTemps.Add(b.BytesTemp); break;
            case IrInst.BytesGet b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.IndexTemp); break;
            case IrInst.BytesIndexOf b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.NeedleTemp); usedTemps.Add(b.FromTemp); break;
            case IrInst.BytesCompare b: usedTemps.Add(b.LeftTemp); usedTemps.Add(b.RightTemp); break;
            case IrInst.BytesScanHash b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.NeedleTemp); usedTemps.Add(b.FromTemp); break;
            case IrInst.BytesSubText b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.StartTemp); usedTemps.Add(b.LenTemp); break;
            case IrInst.BytesSubView b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.StartTemp); usedTemps.Add(b.LenTemp); break;
            case IrInst.BytesAppend b: usedTemps.Add(b.LeftTemp); usedTemps.Add(b.RightTemp); break;
            case IrInst.BytesAppendByte b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.ByteTemp); break;
            case IrInst.BytesFromList b: usedTemps.Add(b.ListTemp); break;
            case IrInst.BytesHash b: usedTemps.Add(b.BytesTemp); break;
        }
    }

    private static bool CollectBytesUpdateUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.BytesAllocate value:
                usedTemps.Add(value.LengthTemp);
                return true;
            case IrInst.BytesCopyRange value:
                usedTemps.Add(value.BytesTemp);
                usedTemps.Add(value.OffsetTemp);
                usedTemps.Add(value.SourceTemp);
                usedTemps.Add(value.SourceOffsetTemp);
                usedTemps.Add(value.LengthTemp);
                return true;
            case IrInst.BytesSet value:
                CollectBytesSetterUsedTemps(value.BytesTemp, value.OffsetTemp, value.ValueTemp, usedTemps);
                return true;
            case IrInst.BytesSetU16Le value:
                CollectBytesSetterUsedTemps(value.BytesTemp, value.OffsetTemp, value.ValueTemp, usedTemps);
                return true;
            case IrInst.BytesSetU32Le value:
                CollectBytesSetterUsedTemps(value.BytesTemp, value.OffsetTemp, value.ValueTemp, usedTemps);
                return true;
            case IrInst.BytesSetU64Le value:
                CollectBytesSetterUsedTemps(value.BytesTemp, value.OffsetTemp, value.ValueTemp, usedTemps);
                return true;
            default:
                return false;
        }
    }

    private static void CollectBytesSetterUsedTemps(
        int bytesTemp,
        int offsetTemp,
        int valueTemp,
        HashSet<int> usedTemps)
    {
        usedTemps.Add(bytesTemp);
        usedTemps.Add(offsetTemp);
        usedTemps.Add(valueTemp);
    }

    private static void CollectBytesEncodingUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.BytesU16Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesU32Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesU64Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesGetU16Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.BytesGetU32Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.BytesGetU64Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.FileWriteBytes f: usedTemps.Add(f.PathTemp); usedTemps.Add(f.BytesTemp); break;
        }
    }

    // NOTE: Keep these source-temp users in sync with RemapSourceTemps().
    private static void CollectOwnershipUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CleanupResource d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.DropReuse d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcDrop d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcDup d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcIsUnique u: usedTemps.Add(u.SourceTemp); break;
            case IrInst.Borrow b: usedTemps.Add(b.SourceTemp); break;
            case IrInst.CopyOutArena c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutArenaToSpace c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyFixedInto c: usedTemps.Add(c.DestTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyStringIntoOrFresh c: usedTemps.Add(c.OldBlobTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyFixedIntoOrFresh c: usedTemps.Add(c.OldBlobTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutList c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutClosure c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutTcoListCell c: usedTemps.Add(c.SrcTemp); break;
        }
    }

    private static void CollectTaskParallelUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTask ct: usedTemps.Add(ct.ClosureTemp); break;
            case IrInst.CreateCompletedTask ct: usedTemps.Add(ct.ResultTemp); break;
            case IrInst.AwaitTask at: usedTemps.Add(at.TaskTemp); break;
            case IrInst.RunTask rt: usedTemps.Add(rt.TaskTemp); break;
            case IrInst.SpawnTask st: usedTemps.Add(st.TaskTemp); break;
            case IrInst.AllocReusing ar: usedTemps.Add(ar.TokenTemp); break;
            case IrInst.ParallelFork pf: usedTemps.Add(pf.RightClosureTemp); break;
            case IrInst.ParallelJoin pj: usedTemps.Add(pj.DescTemp); break;
            case IrInst.ParallelCleanup pc: usedTemps.Add(pc.DescTemp); break;
            case IrInst.StoreParallelWorkerOverride so: usedTemps.Add(so.Source); break;
            case IrInst.ParallelQueueStart qs: usedTemps.Add(qs.FClosureTemp); usedTemps.Add(qs.CombineClosureTemp); usedTemps.Add(qs.ListTemp); break;
            case IrInst.ParallelQueueAwait qa: usedTemps.Add(qa.DescTemp); break;
            case IrInst.ParallelQueueCleanup qc: usedTemps.Add(qc.DescTemp); break;
            case IrInst.AsyncSleep sl: usedTemps.Add(sl.MillisecondsTemp); break;
        }
    }

    private static void CollectNetTaskUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTcpConnectTask t: usedTemps.Add(t.HostTemp); usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateTcpSendTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.TextTemp); break;
            case IrInst.CreateTcpReceiveTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.MaxBytesTemp); break;
            case IrInst.CreateTcpCloseTask t: usedTemps.Add(t.SocketTemp); break;
            case IrInst.CreateTcpListenTask t: usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateForkWorkersTask t: usedTemps.Add(t.PortTemp); usedTemps.Add(t.CountTemp); break;
            case IrInst.SetDrainTimeout t: usedTemps.Add(t.MsTemp); break;
            case IrInst.CreateTcpAcceptTask t: usedTemps.Add(t.SocketTemp); break;
            case IrInst.CreateHttpGetTask t: usedTemps.Add(t.UrlTemp); break;
            case IrInst.CreateHttpPostTask t: usedTemps.Add(t.UrlTemp); usedTemps.Add(t.BodyTemp); break;
        }
    }

    private static void CollectTlsTaskUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTlsConnectTask t: usedTemps.Add(t.HostTemp); usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateTlsHandshakeTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.HostTemp); break;
            case IrInst.CreateTlsServerHandshakeTask t2: usedTemps.Add(t2.SocketTemp); usedTemps.Add(t2.CertTemp); usedTemps.Add(t2.KeyTemp); break;
            case IrInst.CreateTlsSendTask t: usedTemps.Add(t.SslTemp); usedTemps.Add(t.TextTemp); break;
            case IrInst.CreateTlsReceiveTask t: usedTemps.Add(t.SslTemp); usedTemps.Add(t.MaxBytesTemp); break;
            case IrInst.CreateTlsCloseTask t: usedTemps.Add(t.SslTemp); break;
            case IrInst.AsyncAll aa: usedTemps.Add(aa.TaskListTemp); break;
            case IrInst.AsyncRace ar: usedTemps.Add(ar.TaskListTemp); break;
            case IrInst.CreateScopedTask s: usedTemps.Add(s.ParentTaskTemp); usedTemps.Add(s.ScopeTemp); break;
            case IrInst.ForkScopedTask f: usedTemps.Add(f.OwnerTaskTemp); usedTemps.Add(f.TaskTemp); break;
            case IrInst.JoinScopedTask j: usedTemps.Add(j.HandleTemp); break;
        }
    }

    private static void CollectSuspendControlUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.Suspend s:
                usedTemps.Add(s.StateStructTemp);
                usedTemps.Add(s.AwaitedTaskTemp);
                foreach (var (_, sourceTemp) in s.SaveVars) usedTemps.Add(sourceTemp);
                break;
            case IrInst.Resume r:
                usedTemps.Add(r.StateStructTemp);
                break;
            case IrInst.PanicStr p: usedTemps.Add(p.Source); break;
            case IrInst.StoreCapabilityHandler se: usedTemps.Add(se.Source); break;
            case IrInst.JumpIfFalse j: usedTemps.Add(j.CondTemp); break;
            case IrInst.SwitchTag s: usedTemps.Add(s.TagTemp); break;
            case IrInst.Return r: usedTemps.Add(r.Source); break;
        }
    }

    // Control-flow simplification
    // Jump threading, redundant-jump elision, and unreferenced-label removal — the deterministic
    // CFG cleanup LLVM's own simplifycfg performs at -O1+ but never runs at -O0/--debug, and
    // which also improves --emit-ir/--explain output quality at every level. Every rewrite here
    // is locally safe without reachability analysis: redirecting a branch through an empty-label
    // chain aims it at the same eventual destination; dropping a label with zero remaining
    // references removes only a marker, never the code around it; and a Jump immediately
    // followed by its own target Label is a pure no-op (nothing can jump directly to the Jump
    // instruction itself — only to a label — so it's reached solely by fallthrough from above,
    // which reaches the Label just as well without it).

    private static List<IrInst> SimplifyControlFlow(List<IrInst> instructions)
    {
        var redirect = BuildEmptyLabelRedirectMap(instructions);
        var rewritten = redirect.Count == 0 ? instructions : RewriteBranchTargets(instructions, redirect);

        // Dropping unreferenced labels first, then eliding redundant jumps, catches jump/label
        // pairs the label removal itself brings into direct adjacency (a Jump immediately
        // followed by an unreferenced Label immediately followed by the Jump's own target).
        var withoutUnreferencedLabels = DropUnreferencedLabels(rewritten);
        return ElideRedundantFallthroughJumps(withoutUnreferencedLabels);
    }

    // A label immediately followed by nothing but an unconditional Jump is an empty hop: any
    // branch that targets it can be redirected straight to the jump's own target instead.
    // Chains (L1 -> L2 -> L3) are followed to their final destination.
    private static Dictionary<string, string> BuildEmptyLabelRedirectMap(List<IrInst> instructions)
    {
        var direct = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.Label label && instructions[i + 1] is IrInst.Jump jump
                && !string.Equals(label.Name, jump.Target, StringComparison.Ordinal))
            {
                direct[label.Name] = jump.Target;
            }
        }

        if (direct.Count == 0)
        {
            return direct;
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in direct.Keys)
        {
            resolved[name] = ChaseRedirectChain(direct, name);
        }

        return resolved;
    }

    // Follows a chain of empty-label hops to its final destination, with cycle protection for a
    // pathological Jump-only loop in the source program.
    private static string ChaseRedirectChain(Dictionary<string, string> direct, string start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        string current = start;
        while (direct.TryGetValue(current, out string? next))
        {
            if (!seen.Add(next))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static List<IrInst> RewriteBranchTargets(List<IrInst> instructions, Dictionary<string, string> redirect)
    {
        var result = new List<IrInst>(instructions.Count);
        foreach (var inst in instructions)
        {
            result.Add(inst switch
            {
                IrInst.Jump j when redirect.TryGetValue(j.Target, out string? t) => j with { Target = t },
                IrInst.JumpIfFalse jf when redirect.TryGetValue(jf.Target, out string? t) => jf with { Target = t },
                IrInst.SwitchTag sw => RewriteSwitchTagTargets(sw, redirect),
                _ => inst,
            });
        }

        return result;
    }

    private static IrInst.SwitchTag RewriteSwitchTagTargets(IrInst.SwitchTag sw, Dictionary<string, string> redirect)
    {
        bool changed = false;
        var cases = new List<(long Tag, string Label)>(sw.Cases.Count);
        foreach (var (tag, label) in sw.Cases)
        {
            if (redirect.TryGetValue(label, out string? t))
            {
                cases.Add((tag, t));
                changed = true;
            }
            else
            {
                cases.Add((tag, label));
            }
        }

        string defaultLabel = sw.DefaultLabel;
        if (redirect.TryGetValue(sw.DefaultLabel, out string? newDefault))
        {
            defaultLabel = newDefault;
            changed = true;
        }

        return changed ? sw with { Cases = cases, DefaultLabel = defaultLabel } : sw;
    }

    // A Label with zero remaining explicit-branch references is never a jump target any more —
    // dropping just the marker instruction changes nothing about execution order, since any
    // fallthrough from the preceding instruction reaches the same following code either way.
    private static List<IrInst> DropUnreferencedLabels(List<IrInst> instructions)
    {
        var refs = CountBranchRefsToLabels(instructions);
        var result = new List<IrInst>(instructions.Count);
        foreach (var inst in instructions)
        {
            if (inst is IrInst.Label label && refs.GetValueOrDefault(label.Name) == 0)
            {
                continue;
            }

            result.Add(inst);
        }

        return result;
    }

    // Jump L immediately followed by Label L is redundant: nothing can jump directly to the
    // Jump instruction itself (only to a label), so it's reached solely by fallthrough from
    // above, which reaches the Label just as well without the Jump in between.
    private static List<IrInst> ElideRedundantFallthroughJumps(List<IrInst> instructions)
    {
        var result = new List<IrInst>(instructions.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.Jump jump
                && i + 1 < instructions.Count
                && instructions[i + 1] is IrInst.Label label
                && string.Equals(jump.Target, label.Name, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(instructions[i]);
        }

        return result;
    }

    /// <summary>
    /// Counts the number of explicit branch instructions (Jump and JumpIfFalse)
    /// that target each label. Used to determine whether a label has a single
    /// predecessor and can safely propagate constant knowledge.
    /// </summary>
    private static Dictionary<string, int> CountBranchRefsToLabels(List<IrInst> instructions)
    {
        var refs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var inst in instructions)
        {
            if (inst is IrInst.SwitchTag sw)
            {
                // Every case label plus the default label is a predecessor edge.
                foreach (var (_, caseLabel) in sw.Cases)
                {
                    refs[caseLabel] = refs.GetValueOrDefault(caseLabel) + 1;
                }

                refs[sw.DefaultLabel] = refs.GetValueOrDefault(sw.DefaultLabel) + 1;
                continue;
            }

            string? target = inst switch
            {
                IrInst.Jump j => j.Target,
                IrInst.JumpIfFalse jf => jf.Target,
                _ => null
            };

            if (target is not null)
            {
                refs[target] = refs.GetValueOrDefault(target) + 1;
            }
        }

        return refs;
    }
}
