using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// The runtime-managed children an arena aggregate built as a call's argument retained, because
    /// the callee's result may reach the argument: they are released once the call's result holds
    /// its own references (a scalar, or a result normalized onto the reference-counted heap), since
    /// the arena aggregate itself is never released. Only retains in the argument's entry block are
    /// collected, so each dominates the release after the call.
    /// </summary>
    private sealed class CallArgumentRetainFrame(List<IrInst> body)
    {
        public List<IrInst> Body { get; } = body;

        // A retain of a loop parameter whose placement is not known yet (a marker
        // FinalizeTcoParameterAggregateRetains resolves) carries the parameter's slot; -1 otherwise.
        public List<(int Temp, TypeRef Type, int PendingSlot)> Retains { get; } = [];

        public bool Collecting { get; set; }

        public int ArgumentStart { get; set; }
    }

    private CallArgumentRetainFrame? _callArgumentRetainFrame;

    private CallArgumentRetainFrame? EnterCallArgumentRetainFrame()
    {
        CallArgumentRetainFrame? outer = _callArgumentRetainFrame;
        _callArgumentRetainFrame = new CallArgumentRetainFrame(_inst);
        return outer;
    }

    /// <summary>Starts or stops collecting for the argument about to be lowered; returns the previous state.</summary>
    private bool BeginCallArgumentRetains(bool transfersChildren)
    {
        if (_callArgumentRetainFrame is not { } frame)
        {
            return false;
        }

        bool previous = frame.Collecting;
        frame.Collecting = transfersChildren;
        frame.ArgumentStart = _inst.Count;
        return previous;
    }

    private void EndCallArgumentRetains(bool previous)
    {
        if (_callArgumentRetainFrame is { } frame)
        {
            frame.Collecting = previous;
        }
    }

    /// <summary>
    /// Records a retain an arena aggregate child took while a call argument is lowered: a real
    /// runtime retain emitted into the frame's own function, before any branch of the argument. A
    /// loop parameter's retain marker (<paramref name="pendingSlot"/> its slot) is recorded too: it
    /// becomes a real retain once the parameter is placed on the reference-counted heap, and its
    /// release after the call is guarded by a flag zeroed where the parameter stays in the arena.
    /// </summary>
    private bool RecordCallArgumentArenaRetain(int elementTemp, int retainedTemp, TypeRef elementType, int pendingSlot = -1)
    {
        if (retainedTemp == elementTemp
            || Environment.GetEnvironmentVariable("GRC_NO_ARGRET") is not null
            || _callArgumentRetainFrame is not { Collecting: true } frame
            || !ReferenceEquals(frame.Body, _inst)
            || _inst.Count == 0
            || _inst[^1] is not IrInst.RcDup retain
            || retain.Target != retainedTemp
            || !retain.RuntimeManaged && !RecordsPendingParameterRetain(pendingSlot))
        {
            return false;
        }

        for (int i = frame.ArgumentStart; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.Label)
            {
                return false;
            }
        }

        frame.Retains.Add((retainedTemp, Prune(elementType), retain.RuntimeManaged ? -1 : pendingSlot));
        return true;
    }

    private static bool RecordsPendingParameterRetain(int pendingSlot)
        => pendingSlot >= 0 && Environment.GetEnvironmentVariable("GRC_NO_ARGRETMARKER") is null;

    /// <summary>
    /// An arena aggregate that retained a child of the contract's own types for the scopes it
    /// outlives (a loop's result, a successor argument) is normalized where it goes, and the
    /// normalized value takes a reference of its own. The aggregate's reference, which no call
    /// argument frame releases, goes to an owned slot instead of staying with the arena cell, which
    /// never releases it: the slot is released only once the function's result, or the loop's
    /// successor state, holds its own references. The aggregate's own type may still be unresolved
    /// here (a `None` beside the state), so the child's decides.
    /// </summary>
    private void HandArenaAggregateRetainToOwnedSlot(int elementTemp, int retainedTemp, TypeRef elementType)
    {
        if (retainedTemp == elementTemp
            || !GeneralRcEnabled
            || Environment.GetEnvironmentVariable("GRC_NO_AGGRETSLOT") is not null
            || _inst.Count == 0
            || _inst[^1] is not IrInst.RcDup { RuntimeManaged: true } retain
            || retain.Target != retainedTemp
            || ContainsUnresolvedLayoutType(elementType, [])
            || !IsGeneralRcValueType(elementType))
        {
            return;
        }

        int ownedSlot = NewLocal();
        Emit(new IrInst.StoreLocal(ownedSlot, retainedTemp));
        RegisterGeneralRcOwnedSlot(ownedSlot, Prune(elementType));
    }

    // Per function body, the successor temps a tail self-call retained for the transfer out of an
    // iteration-local owner.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _transferRetainedSuccessorsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordTransferRetainedSuccessor(int borrowedTemp, int successorTemp)
    {
        if (successorTemp == borrowedTemp || Environment.GetEnvironmentVariable("GRC_NO_ARENASUCCESSORCOPY") is not null)
        {
            return;
        }

        if (!_transferRetainedSuccessorsByBody.TryGetValue(_inst, out HashSet<int>? temps))
        {
            temps = [];
            _transferRetainedSuccessorsByBody[_inst] = temps;
        }

        temps.Add(successorTemp);
    }

    private bool IsTransferRetainedSuccessor(int temp)
        => _transferRetainedSuccessorsByBody.TryGetValue(_inst, out HashSet<int>? temps) && temps.Contains(temp);

    /// <summary>
    /// A back edge that keeps the iteration's arena (no reset) hands an arena parameter a successor
    /// it retained out of an owner the iteration releases. The parameter releases nothing, so that
    /// reference was never released: the parameter takes an arena copy instead, and the retained
    /// reference is released after the copy. The arena copy lives as long as the loop's arena does,
    /// which no reset on this edge shortens.
    /// </summary>
    private void CopyTransferRetainedSuccessorsIntoArena(PendingTcoReset info)
    {
        for (int i = 0; i < info.TransferRetainedArgs.Length && i < info.ParamSlots.Length; i++)
        {
            TypeRef type = Prune(info.ArgTypes[i]);
            if (!info.TransferRetainedArgs[i]
                || info.ParamPlacements[i]?.Representation == TcoPlacementRepresentation.RuntimeRc
                || CanArenaReset(type)
                || ContainsUnresolvedLayoutType(type, [])
                || !IsGeneralRcAdmissible(type)
                || !HasBoundedCopy(type, []))
            {
                continue;
            }

            int slot = info.ParamSlots[i];
            int successorTemp = NewTemp();
            Emit(new IrInst.LoadLocal(successorTemp, slot));
            Emit(new IrInst.StoreLocal(slot, EmitArenaCopyOfReferenceCounted(successorTemp, type, releasesSource: true)));
        }
    }

    // Whether a value of the type is a bounded graph: no list and no recursive type, so copying it
    // on every iteration costs the same each time rather than growing with what the loop builds.
    private bool HasBoundedCopy(TypeRef type, HashSet<string> path)
    {
        switch (Prune(type))
        {
            case TypeRef.TList:
                return false;
            case TypeRef.TTuple tuple:
                return tuple.Elements.All(element => HasBoundedCopy(element, path));
            case TypeRef.TNamedType named:
                string key = Pretty(named);
                if (!path.Add(key))
                {
                    return false;
                }

                bool bounded = named.Symbol.Constructors.All(constructor =>
                    Enumerable.Range(0, constructor.Arity).All(index =>
                        HasBoundedCopy(InstantiateConstructorParameterType(constructor, index, named), path)));
                path.Remove(key);
                return bounded;
            default:
                return true;
        }
    }

    /// <summary>
    /// A loop that can neither reset its arena plainly nor copy every argument out still owns the
    /// reference-counted parameters it placed there: each successor takes a reference of its own
    /// and the predecessor is released, as the reset path does, only without the reset. The
    /// iteration's owned drops follow the successors, which may borrow from them. A loop whose
    /// only such parameters are closures gains nothing: with no reset, the closure each iteration
    /// builds stays in the arena beside the copy made of it.
    /// </summary>
    private bool TcoBackEdgeTryEmitRuntimeManagedSuccessorsWithoutReset(PendingTcoReset info)
    {
        if (!GeneralRcEnabled || Environment.GetEnvironmentVariable("GRC_NO_BACKEDGERC") is not null
            || info.CoroutineLoop
            || !Enumerable.Range(0, info.ArgTypes.Length).Any(i =>
                info.ParamPlacements[i]?.Representation == TcoPlacementRepresentation.RuntimeRc
                && Prune(info.ArgTypes[i]) is not TypeRef.TFun)
            || Enumerable.Range(0, info.ArgTypes.Length).All(i => TcoBackEdgeArgPlainResetSafe(info, i))
            || (TcoBackEdgeAllArgsCopyable(info) && !TcoBackEdgeCompactionCopiesRuntimeManagedList(info)))
        {
            return false;
        }

        int[] normalizedTemps = TcoBackEdgeNormalizeAndReleaseRuntimeManagedArgs(info);
        for (int i = 0; i < normalizedTemps.Length; i++)
        {
            if (normalizedTemps[i] < 0)
            {
                continue;
            }

            Emit(new IrInst.StoreLocal(info.ParamSlots[i], normalizedTemps[i]));
            if (info.RuntimeManagedParamActiveSlots[i] >= 0)
            {
                int activeTemp = NewTemp();
                Emit(new IrInst.LoadConstInt(activeTemp, 1));
                Emit(new IrInst.StoreLocal(info.RuntimeManagedParamActiveSlots[i], activeTemp));
            }

            if (info.RuntimeManagedClosureActiveSlots[i] >= 0)
            {
                int activeTemp = NewTemp();
                Emit(new IrInst.LoadConstInt(activeTemp, 1));
                Emit(new IrInst.StoreLocal(info.RuntimeManagedClosureActiveSlots[i], activeTemp));
            }
        }

        return true;
    }

    // A list parameter on the reference-counted heap whose successor the two-pass copy-out would
    // compact beside an arena sibling's. An arena cell built for such a parameter borrows its
    // children (BackEdgeCopiesListSuccessor), and the compaction copies the cell only after the
    // iteration's owners are released, so the successor takes its references here instead, with
    // the reset given up.
    private bool TcoBackEdgeCompactionCopiesRuntimeManagedList(PendingTcoReset info)
        => Enumerable.Range(0, info.ArgTypes.Length).Any(i =>
            info.ParamPlacements[i]?.Representation == TcoPlacementRepresentation.RuntimeRc
            && !info.PassThrough[i]
            && Prune(info.ArgTypes[i]) is TypeRef.TList
            && TcoBackEdgeArgCopyOutKind(info, i, out _, out _) != CopyOutKind.None);

    // A list literal's head retained for the list that owns or carries it; an arena list's retain is
    // recorded for the call it is an argument of.
    private LoweredValue RetainListLiteralElement(Expr element, LoweredValue lowered, bool runtimeManagedList)
    {
        int retainedTemp = RetainRuntimeManagedAggregateChild(element, lowered.Temp, lowered.Type);
        if (!runtimeManagedList)
        {
            _ = RecordCallArgumentArenaRetain(lowered.Temp, retainedTemp, lowered.Type);
        }

        return CreateLoweredValue(retainedTemp, lowered.Type);
    }

    /// <summary>
    /// Closes the frame opened for a call: releases the collected retains when the call's result
    /// holds its own references, and keeps them otherwise (an arena result may still point at them).
    /// </summary>
    private void LeaveCallArgumentRetainFrame(CallArgumentRetainFrame? outer, (int Temp, TypeRef Type)? result)
    {
        CallArgumentRetainFrame? frame = _callArgumentRetainFrame;
        _callArgumentRetainFrame = outer;
        if (result is not { } finished
            || frame is null
            || frame.Retains.Count == 0
            || !ReferenceEquals(frame.Body, _inst))
        {
            return;
        }

        TypeRef pruned = Prune(finished.Type);
        bool resultOwnsItself = CanArenaReset(pruned)
            || IsRuntimeManagedResultTemp(finished.Temp)
            || IsGeneralRcValueType(pruned);
        if (!resultOwnsItself)
        {
            return;
        }

        foreach ((int temp, TypeRef type, int pendingSlot) in frame.Retains)
        {
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int presentTemp = NewTemp();
            Emit(new IrInst.CmpIntNe(presentTemp, temp, zeroTemp));
            if (pendingSlot >= 0)
            {
                _pendingRuntimeArgumentFlags[presentTemp] = pendingSlot;
            }

            string skipLabel = NewLabel("call_argument_retain_absent");
            Emit(new IrInst.JumpIfFalse(presentTemp, skipLabel));
            EmitRuntimeManagedChildDrop(temp, type);
            Emit(new IrInst.Label(skipLabel));
        }
    }
}
