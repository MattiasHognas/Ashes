namespace Ashes.Semantics;

/// <summary>
/// Whole-program closure-environment passes: devirtualizing a call through a captured closure whose
/// label every creation site agrees on, and collapsing a chain of pure currying stages into one
/// direct call with a stack-allocated environment.
/// </summary>
public static partial class IrOptimizer
{
    // Captured-closure devirtualization
    // A stitched module refers to its sibling functions through alias bindings that lambdas capture,
    // so a call such as `parserCurrent(state)` inside another parser function loads the callee from
    // its own environment and calls it indirectly. Every site that creates that lambda's closure
    // stores the same known closure into the same environment slot (the alias is bound once), so the
    // call target is statically known after all: a CallClosure on a LoadEnv whose slot resolves, at
    // every creation site of the enclosing function, to one closure label becomes a CallKnown with
    // the closure object's own environment word, exactly as DevirtualizeReturnedClosureCalls does for
    // a curried second application. Resolution follows single-definition temps, single-store local
    // slots, Borrow copies, known-returned call results, and the creating function's own captured
    // slots (to a whole-program fixpoint). A slot with a disagreeing or unresolvable site is never
    // rewritten.
    private static (IrFunction Entry, List<IrFunction> Functions) DevirtualizeCapturedClosureCalls(
        IrFunction entry, List<IrFunction> functions)
    {
        var all = new List<IrFunction>(functions.Count + 1) { entry };
        all.AddRange(functions);
        Dictionary<string, string> knownReturned = ComputeKnownReturnedClosureLabels(all);
        Dictionary<(string Label, int Index), string> knownCaptured = ComputeKnownCapturedClosureLabels(all, knownReturned);
        if (knownCaptured.Count == 0)
        {
            return (entry, functions);
        }

        return (
            DevirtualizeCapturedClosureCallsInFunction(entry, knownCaptured),
            functions.Select(f => DevirtualizeCapturedClosureCallsInFunction(f, knownCaptured)).ToList());
    }

    private sealed record CaptureSite(string TargetLabel, int Index, ClosureCreationFacts Creator, int SourceTemp);

    private sealed class ClosureCreationFacts
    {
        public required string Label { get; init; }

        public required List<IrInst> Instructions { get; init; }

        public required Dictionary<int, int> DefCount { get; init; }

        public required Dictionary<int, int> DefIndex { get; init; }

        public required Dictionary<int, int> SingleStoreSourceBySlot { get; init; }

        public static ClosureCreationFacts Collect(IrFunction function)
        {
            (Dictionary<int, int> defCount, Dictionary<int, int> defIndex, _) = ComputeTempDefUseFacts(function.Instructions);
            var storeCount = new Dictionary<int, int>();
            var storeSource = new Dictionary<int, int>();
            foreach (IrInst inst in function.Instructions)
            {
                if (inst is IrInst.StoreLocal store)
                {
                    storeCount[store.Slot] = storeCount.GetValueOrDefault(store.Slot) + 1;
                    storeSource[store.Slot] = store.Source;
                }
            }

            var single = new Dictionary<int, int>();
            foreach ((int slot, int count) in storeCount)
            {
                if (count == 1)
                {
                    single[slot] = storeSource[slot];
                }
            }

            return new ClosureCreationFacts
            {
                Label = function.Label,
                Instructions = function.Instructions,
                DefCount = defCount,
                DefIndex = defIndex,
                SingleStoreSourceBySlot = single,
            };
        }
    }

    private enum CaptureResolution
    {
        Known,
        Pending,
        Unknown,
    }

    private static Dictionary<(string Label, int Index), string> ComputeKnownCapturedClosureLabels(
        IReadOnlyList<IrFunction> functions,
        Dictionary<string, string> knownReturned)
    {
        List<CaptureSite> sites = [];
        var unresolvable = new HashSet<(string Label, int Index)>();
        foreach (IrFunction function in functions)
        {
            CollectCaptureSites(ClosureCreationFacts.Collect(function), sites, unresolvable);
        }

        var known = new Dictionary<(string Label, int Index), string>();
        var conflicting = new HashSet<(string Label, int Index)>(unresolvable);
        List<IGrouping<(string Label, int Index), CaptureSite>> groups = sites.GroupBy(site => (site.TargetLabel, site.Index)).ToList();
        WholeProgramFixpoint.RunToFixpoint(() =>
        {
            bool changed = false;
            foreach (IGrouping<(string Label, int Index), CaptureSite> group in groups)
            {
                if (known.ContainsKey(group.Key) || conflicting.Contains(group.Key))
                {
                    continue;
                }

                (CaptureResolution resolution, string? label) = ResolveCaptureGroup(group, known, knownReturned);
                if (resolution == CaptureResolution.Unknown)
                {
                    conflicting.Add(group.Key);
                    changed = true;
                }
                else if (resolution == CaptureResolution.Known)
                {
                    known[group.Key] = label!;
                    changed = true;
                }
            }

            return changed;
        });

        return known;
    }

    // Every closure creation with a non-empty environment contributes one site per environment
    // slot; a slot stored more than once, not at all, or through an environment pointer that is not
    // a single fresh allocation marks the whole (label, slot) pair unresolvable.
    private static void CollectCaptureSites(
        ClosureCreationFacts creator,
        List<CaptureSite> sites,
        HashSet<(string Label, int Index)> unresolvable)
    {
        List<IrInst> instructions = creator.Instructions;
        var storesByEnvAndOffset = new Dictionary<(int BasePtr, int OffsetBytes), List<int>>();
        foreach (IrInst inst in instructions)
        {
            if (inst is IrInst.StoreMemOffset store)
            {
                if (!storesByEnvAndOffset.TryGetValue((store.BasePtr, store.OffsetBytes), out List<int>? list))
                {
                    list = [];
                    storesByEnvAndOffset[(store.BasePtr, store.OffsetBytes)] = list;
                }

                list.Add(store.Source);
            }
        }

        foreach (IrInst inst in instructions)
        {
            (string label, int envTemp, int envSize, bool freshEnvironment) = DescribeCreationSite(creator, inst);
            if (envSize <= 0)
            {
                continue;
            }

            for (int index = 0; index < envSize / 8; index++)
            {
                if (freshEnvironment
                    && storesByEnvAndOffset.TryGetValue((envTemp, index * 8), out List<int>? sources)
                    && sources.Count == 1)
                {
                    sites.Add(new CaptureSite(label, index, creator, sources[0]));
                }
                else
                {
                    unresolvable.Add((label, index));
                }
            }
        }
    }

    // A closure whose only use was an immediate call has already been devirtualized into a
    // CallKnown over its environment, so such a call is a creation site too. The environment size
    // comes from the fresh allocation when there is one (a CallKnown carries none itself).
    private static (string Label, int EnvTemp, int EnvSizeBytes, bool FreshEnvironment) DescribeCreationSite(
        ClosureCreationFacts creator,
        IrInst inst)
    {
        (string label, int envTemp, int declaredSize) = inst switch
        {
            IrInst.MakeClosure mk => (mk.FuncLabel, mk.EnvPtrTemp, mk.EnvSizeBytes),
            IrInst.MakeClosureStack mks => (mks.FuncLabel, mks.EnvPtrTemp, mks.EnvSizeBytes),
            IrInst.CallKnown call => (call.FuncLabel, call.EnvTemp, 0),
            _ => ("", -1, 0),
        };
        if (envTemp < 0)
        {
            return (label, envTemp, 0, false);
        }

        if (creator.DefCount.GetValueOrDefault(envTemp) == 1
            && creator.DefIndex.TryGetValue(envTemp, out int envDef))
        {
            switch (creator.Instructions[envDef])
            {
                case IrInst.Alloc alloc:
                    return (label, envTemp, alloc.SizeBytes, true);
                case IrInst.AllocStack allocStack:
                    return (label, envTemp, allocStack.SizeBytes, true);
                default:
                    break;
            }
        }

        return (label, envTemp, declaredSize, false);
    }

    private static (CaptureResolution Resolution, string? Label) ResolveCaptureGroup(
        IEnumerable<CaptureSite> sites,
        Dictionary<(string Label, int Index), string> known,
        Dictionary<string, string> knownReturned)
    {
        string? agreed = null;
        bool pending = false;
        foreach (CaptureSite site in sites)
        {
            (CaptureResolution resolution, string? label) = ResolveClosureLabel(site.Creator, site.SourceTemp, known, knownReturned, depth: 0);
            if (resolution == CaptureResolution.Unknown)
            {
                return (CaptureResolution.Unknown, null);
            }

            if (resolution == CaptureResolution.Pending)
            {
                pending = true;
                continue;
            }

            if (agreed is not null && !string.Equals(agreed, label, StringComparison.Ordinal))
            {
                return (CaptureResolution.Unknown, null);
            }

            agreed = label;
        }

        return pending || agreed is null
            ? (CaptureResolution.Pending, null)
            : (CaptureResolution.Known, agreed);
    }

    private static (CaptureResolution Resolution, string? Label) ResolveClosureLabel(
        ClosureCreationFacts creator,
        int temp,
        Dictionary<(string Label, int Index), string> known,
        Dictionary<string, string> knownReturned,
        int depth)
    {
        if (depth > 16
            || creator.DefCount.GetValueOrDefault(temp) != 1
            || !creator.DefIndex.TryGetValue(temp, out int index))
        {
            return (CaptureResolution.Unknown, null);
        }

        switch (creator.Instructions[index])
        {
            case IrInst.MakeClosure mk:
                return (CaptureResolution.Known, mk.FuncLabel);
            case IrInst.MakeClosureStack mks:
                return (CaptureResolution.Known, mks.FuncLabel);
            case IrInst.Borrow borrow:
                return ResolveClosureLabel(creator, borrow.SourceTemp, known, knownReturned, depth + 1);
            case IrInst.LoadLocal load when creator.SingleStoreSourceBySlot.TryGetValue(load.Slot, out int source):
                return ResolveClosureLabel(creator, source, known, knownReturned, depth + 1);
            case IrInst.LoadEnv loadEnv:
                return known.TryGetValue((creator.Label, loadEnv.Index), out string? captured)
                    ? (CaptureResolution.Known, captured)
                    : (CaptureResolution.Pending, null);
            case IrInst.CallKnown call when knownReturned.TryGetValue(call.FuncLabel, out string? returned):
                return (CaptureResolution.Known, returned);
            default:
                return (CaptureResolution.Unknown, null);
        }
    }

    private static IrFunction DevirtualizeCapturedClosureCallsInFunction(
        IrFunction function,
        Dictionary<(string Label, int Index), string> knownCaptured)
    {
        if (!function.HasEnvAndArgParams || function.Coroutine is not null)
        {
            return function;
        }

        (Dictionary<int, int> defCount, Dictionary<int, int> defIndex, _) = ComputeTempDefUseFacts(function.Instructions);
        List<IrInst> instructions = function.Instructions;
        var result = new List<IrInst>(instructions.Count);
        int nextTemp = function.TempCount;
        bool changed = false;
        foreach (IrInst inst in instructions)
        {
            if (inst is IrInst.CallClosure call
                && TryResolveCapturedIndex(call.ClosureTemp, instructions, defCount, defIndex, 0) is { } index
                && knownCaptured.TryGetValue((function.Label, index), out string? label))
            {
                int envTemp = nextTemp++;
                result.Add(new IrInst.LoadMemOffset(envTemp, call.ClosureTemp, 8));
                result.Add(new IrInst.CallKnown(
                    call.Target, label, envTemp, call.ArgTemp, call.RuntimeManagedArgumentFlagTemp,
                    EnvironmentIsStackAllocated: false)
                { Location = call.Location });
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? function with { Instructions = result, TempCount = nextTemp } : function;
    }

    private static int? TryResolveCapturedIndex(
        int temp,
        List<IrInst> instructions,
        Dictionary<int, int> defCount,
        Dictionary<int, int> defIndex,
        int depth)
    {
        if (depth > 8 || defCount.GetValueOrDefault(temp) != 1 || !defIndex.TryGetValue(temp, out int index))
        {
            return null;
        }

        return instructions[index] switch
        {
            IrInst.LoadEnv loadEnv => loadEnv.Index,
            IrInst.Borrow borrow => TryResolveCapturedIndex(borrow.SourceTemp, instructions, defCount, defIndex, depth + 1),
            _ => null,
        };
    }

    // Currying-stage inlining
    // A curried function of several parameters lowers to a chain of stages, each of which only
    // copies its captures and its argument into a fresh environment and returns the next stage's
    // closure. Once the calls along a saturated chain are direct, the caller still pays one heap
    // environment and one closure object per stage. When the stage's whole body is that copy, the
    // caller can build the next stage's environment itself, on its own stack, and call the next
    // function directly with it: the stage's closure object is never needed, and the environment
    // dies with the caller's frame. The next function must read its environment only through
    // LoadEnv (no raw read of the environment pointer, no coroutine frame rewrite), and the call
    // must not become a native sibling tail call, which EnvironmentIsStackAllocated already
    // enforces. Repeated to a fixpoint so a chain of stages collapses into the innermost call.
    private static (IrFunction Entry, List<IrFunction> Functions) InlineCurryingStages(
        IrFunction entry, List<IrFunction> functions)
    {
        var functionsByLabel = new Dictionary<string, IrFunction>(StringComparer.Ordinal)
        {
            [entry.Label] = entry,
        };
        foreach (IrFunction f in functions)
        {
            functionsByLabel[f.Label] = f;
        }

        var stageShapes = new Dictionary<string, CurryingStage?>(StringComparer.Ordinal);
        return (
            InlineCurryingStagesInFunction(entry, functionsByLabel, stageShapes),
            functions.Select(f => InlineCurryingStagesInFunction(f, functionsByLabel, stageShapes)).ToList());
    }

    // CaptureIndex -1 stands for the stage's own argument.
    private sealed record CurryingStageStore(int OffsetBytes, int CaptureIndex);

    private sealed record CurryingStage(int EnvSizeBytes, List<CurryingStageStore> Stores, string NextLabel);

    private static IrFunction InlineCurryingStagesInFunction(
        IrFunction function,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, CurryingStage?> stageShapes)
    {
        bool changed;
        do
        {
            (function, changed) = InlineCurryingStagesOnce(function, functionsByLabel, stageShapes);
        }
        while (changed);

        return function;
    }

    private static (IrFunction, bool) InlineCurryingStagesOnce(
        IrFunction function,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, CurryingStage?> stageShapes)
    {
        List<IrInst> instructions = function.Instructions;
        (_, _, Dictionary<int, int> useCount) = ComputeTempDefUseFacts(instructions);
        (Dictionary<int, int> envLoadIndexByClosureTemp, Dictionary<int, int> callIndexByEnvTemp) = IndexStageChainSites(instructions);

        var expansions = new Dictionary<int, List<IrInst>>();
        var rewrites = new Dictionary<int, IrInst>();
        var removed = new HashSet<int>();
        int nextTemp = function.TempCount;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.CallKnown stageCall
                || rewrites.ContainsKey(i)
                || !TryMatchInlinableStageChain(
                    stageCall, instructions, useCount, envLoadIndexByClosureTemp, callIndexByEnvTemp,
                    functionsByLabel, stageShapes, out int loadIndex, out int nextCallIndex, out CurryingStage? stage))
            {
                continue;
            }

            expansions[i] = BuildStageEnvironment(stageCall, stage!, ref nextTemp, out int stackEnv);
            removed.Add(loadIndex);
            IrInst.CallKnown nextCall = (IrInst.CallKnown)instructions[nextCallIndex];
            rewrites[nextCallIndex] = nextCall with { EnvTemp = stackEnv, EnvironmentIsStackAllocated = true };
        }

        if (expansions.Count == 0)
        {
            return (function, false);
        }

        List<IrInst> result = RebuildWithStageExpansions(instructions, expansions, rewrites, removed);
        return (function with { Instructions = result, TempCount = nextTemp }, true);
    }

    private static (Dictionary<int, int> EnvLoadIndexByClosureTemp, Dictionary<int, int> CallIndexByEnvTemp) IndexStageChainSites(
        List<IrInst> instructions)
    {
        var envLoadIndexByClosureTemp = new Dictionary<int, int>();
        var callIndexByEnvTemp = new Dictionary<int, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            switch (instructions[i])
            {
                case IrInst.LoadMemOffset { OffsetBytes: 8 } load:
                    envLoadIndexByClosureTemp[load.BasePtr] = i;
                    break;
                case IrInst.CallKnown call:
                    callIndexByEnvTemp[call.EnvTemp] = i;
                    break;
            }
        }

        return (envLoadIndexByClosureTemp, callIndexByEnvTemp);
    }

    private static List<IrInst> RebuildWithStageExpansions(
        List<IrInst> instructions,
        Dictionary<int, List<IrInst>> expansions,
        Dictionary<int, IrInst> rewrites,
        HashSet<int> removed)
    {
        var result = new List<IrInst>(instructions.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (removed.Contains(i))
            {
                continue;
            }

            if (expansions.TryGetValue(i, out List<IrInst>? expansion))
            {
                result.AddRange(expansion);
                continue;
            }

            result.Add(rewrites.TryGetValue(i, out IrInst? rewritten) ? rewritten : instructions[i]);
        }

        return result;
    }

    // Emits the stack environment the stage would have built: its captures come from the stage's
    // own environment (the pointer the call passes), its argument from the call's argument temp.
    private static List<IrInst> BuildStageEnvironment(
        IrInst.CallKnown stageCall,
        CurryingStage stage,
        ref int nextTemp,
        out int stackEnv)
    {
        stackEnv = nextTemp++;
        var replacement = new List<IrInst>
        {
            new IrInst.AllocStack(stackEnv, stage.EnvSizeBytes) { Location = stageCall.Location },
        };
        foreach (CurryingStageStore store in stage.Stores)
        {
            int valueTemp;
            if (store.CaptureIndex < 0)
            {
                valueTemp = stageCall.ArgTemp;
            }
            else
            {
                valueTemp = nextTemp++;
                replacement.Add(new IrInst.LoadMemOffset(valueTemp, stageCall.EnvTemp, store.CaptureIndex * 8));
            }

            replacement.Add(new IrInst.StoreMemOffset(stackEnv, store.OffsetBytes, valueTemp));
        }

        return replacement;
    }

    // The chain: `r = CallKnown(stage, env, a)`, `e = LoadMemOffset(r, 8)`, `CallKnown(next, e, b)`,
    // with r and e each used exactly once, next being the label the stage returns, and next able to
    // take a caller-frame environment.
    private static bool TryMatchInlinableStageChain(
        IrInst.CallKnown stageCall,
        List<IrInst> instructions,
        Dictionary<int, int> useCount,
        Dictionary<int, int> envLoadIndexByClosureTemp,
        Dictionary<int, int> callIndexByEnvTemp,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, CurryingStage?> stageShapes,
        out int loadIndex,
        out int nextCallIndex,
        out CurryingStage? stage)
    {
        loadIndex = -1;
        nextCallIndex = -1;
        stage = GetCurryingStage(stageCall.FuncLabel, functionsByLabel, stageShapes);
        if (stage is null
            || useCount.GetValueOrDefault(stageCall.Target) != 1
            || !envLoadIndexByClosureTemp.TryGetValue(stageCall.Target, out loadIndex))
        {
            return false;
        }

        var load = (IrInst.LoadMemOffset)instructions[loadIndex];
        if (useCount.GetValueOrDefault(load.Target) != 1
            || !callIndexByEnvTemp.TryGetValue(load.Target, out nextCallIndex)
            || instructions[nextCallIndex] is not IrInst.CallKnown nextCall
            || !string.Equals(nextCall.FuncLabel, stage.NextLabel, StringComparison.Ordinal)
            || !functionsByLabel.TryGetValue(stage.NextLabel, out IrFunction? next)
            || !CalleeAcceptsCallerFrameEnvironment(next))
        {
            return false;
        }

        return true;
    }

    private static bool CalleeAcceptsCallerFrameEnvironment(IrFunction callee)
        => callee.HasEnvAndArgParams
            && callee.Coroutine is null
            && !callee.Instructions.Any(inst => inst is IrInst.LoadLocal { Slot: 0 });

    private static CurryingStage? GetCurryingStage(
        string label,
        Dictionary<string, IrFunction> functionsByLabel,
        Dictionary<string, CurryingStage?> stageShapes)
    {
        if (stageShapes.TryGetValue(label, out CurryingStage? cached))
        {
            return cached;
        }

        CurryingStage? stage = functionsByLabel.TryGetValue(label, out IrFunction? function)
            ? TryMatchCurryingStage(function)
            : null;
        stageShapes[label] = stage;
        return stage;
    }

    // A pure stage: one fresh environment allocation, loads of its own captures and argument,
    // exactly one store per environment slot from those loads, one closure construction over that
    // environment (not itself reference-counted, so skipping it drops no release), and a return of
    // that closure as the last instruction. Anything else disqualifies the function.
    private static CurryingStage? TryMatchCurryingStage(IrFunction function)
    {
        if (!function.HasEnvAndArgParams || function.Coroutine is not null || function.Instructions.Count < 3)
        {
            return null;
        }

        List<IrInst> body = function.Instructions;
        var scan = new CurryingStageScan();
        for (int i = 0; i < body.Count; i++)
        {
            if (!scan.Accept(body[i], last: i == body.Count - 1))
            {
                return null;
            }
        }

        return scan.ToStage();
    }

    // The instruction-by-instruction state of matching a stage body.
    private sealed class CurryingStageScan
    {
        private int _envTemp = -1;
        private int _envSize;
        private int _closureTemp = -1;
        private string? _nextLabel;
        private readonly Dictionary<int, int> _sourceIndexByTemp = new();
        private readonly List<CurryingStageStore> _stores = [];

        public bool Accept(IrInst inst, bool last)
        {
            switch (inst)
            {
                case IrInst.Alloc { RuntimeManaged: false } alloc when _envTemp < 0:
                    (_envTemp, _envSize) = (alloc.Target, alloc.SizeBytes);
                    return true;
                case IrInst.AllocStack allocStack when _envTemp < 0:
                    (_envTemp, _envSize) = (allocStack.Target, allocStack.SizeBytes);
                    return true;
                case IrInst.LoadEnv loadEnv:
                    _sourceIndexByTemp[loadEnv.Target] = loadEnv.Index;
                    return true;
                case IrInst.LoadLocal { Slot: 1 } argument:
                    _sourceIndexByTemp[argument.Target] = -1;
                    return true;
                case IrInst.StoreMemOffset store when _envTemp >= 0 && store.BasePtr == _envTemp
                    && _sourceIndexByTemp.TryGetValue(store.Source, out int captureIndex):
                    _stores.Add(new CurryingStageStore(store.OffsetBytes, captureIndex));
                    return true;
                case IrInst.MakeClosure { RuntimeManaged: false } mk when mk.EnvPtrTemp == _envTemp && mk.EnvSizeBytes == _envSize && _closureTemp < 0:
                    (_closureTemp, _nextLabel) = (mk.Target, mk.FuncLabel);
                    return true;
                case IrInst.MakeClosureStack mks when mks.EnvPtrTemp == _envTemp && mks.EnvSizeBytes == _envSize && _closureTemp < 0:
                    (_closureTemp, _nextLabel) = (mks.Target, mks.FuncLabel);
                    return true;
                case IrInst.Return ret:
                    return last && _closureTemp >= 0 && ret.Source == _closureTemp;
                default:
                    return false;
            }
        }

        public CurryingStage? ToStage()
        {
            if (_nextLabel is null || _envSize <= 0 || _stores.Count != _envSize / 8)
            {
                return null;
            }

            var offsets = new HashSet<int>();
            foreach (CurryingStageStore store in _stores)
            {
                if (store.OffsetBytes < 0 || store.OffsetBytes >= _envSize || store.OffsetBytes % 8 != 0 || !offsets.Add(store.OffsetBytes))
                {
                    return null;
                }
            }

            return new CurryingStage(_envSize, _stores, _nextLabel);
        }
    }
}
