namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly List<CoroutineRepresentationRecord> _coroutineRepresentationRecords = [];

    /// <summary>
    /// Coroutine value representation decisions retained for the compiler-report handoff, in
    /// generation order. Observational only.
    /// </summary>
    internal IReadOnlyList<CoroutineRepresentationRecord> CoroutineRepresentationDecisions =>
        _coroutineRepresentationRecords;

    /// <summary>
    /// Takes the task frame's own reference to a captured runtime-managed value. The frame outlives
    /// the expression that created the task, so it cannot borrow: the creator releases its reference
    /// at its own last use while the frame keeps this one until teardown.
    /// </summary>
    private int RetainCoroutineCapture(string name, int captureTemp, TypeRef captureType)
    {
        OwnershipInfo? owner = LookupOwnedValue(name);
        if (owner is { IsDropped: false }
            && (owner.IsResource || owner.IsResourceBearing))
        {
            owner.ReleaseKind = ResourceReleaseKind.Moved;
            MarkFrameOwnedResourceTemp(captureTemp, Prune(captureType));
            return captureTemp;
        }

        if (owner is not { RuntimeManaged: true, IsDropped: false })
        {
            return captureTemp;
        }

        TypeRef resolved = Prune(captureType);
        if (CoroutineFrameDropKind(resolved) == OrdinaryHeapChildDropKind.None)
        {
            return captureTemp;
        }

        int retainedTemp = NewTemp();
        Emit(new IrInst.RcDup(
            retainedTemp,
            captureTemp,
            RuntimeManaged: true,
            MayBeEmpty: MayUseEmptyListRepresentation(resolved)));
        MarkRuntimeManagedTemp(
            retainedTemp,
            LoweredTempOwnershipReason.OwnershipTransfer,
            type: resolved);
        return retainedTemp;
    }

    /// <summary>
    /// Describes every task-frame word a coroutine owns or borrows: its captures, and the temps and
    /// locals the state-machine transform saves across a suspend. The frame dropper releases exactly
    /// the frame-owned words, so this is the one place the async teardown obligation is decided.
    /// </summary>
    private List<CoroutineFrameSlot> BuildCoroutineFrameSlots(
        StateMachineResult transformResult,
        IReadOnlyList<int> captureTemps,
        IReadOnlyDictionary<int, LoweredTempOwnershipFact> enclosingTempFacts,
        List<IrInst> bodyInstructions)
    {
        var slots = new List<CoroutineFrameSlot>();
        for (int i = 0; i < captureTemps.Count; i++)
        {
            slots.Add(DescribeCoroutineFrameSlot(
                TaskStructLayout.HeaderSize + i * 8,
                CoroutineFrameSlotKind.Capture,
                i,
                enclosingTempFacts.GetValueOrDefault(captureTemps[i])));
        }

        foreach ((int temp, int offset) in transformResult.SavedTempOffsets.OrderBy(pair => pair.Value))
        {
            slots.Add(DescribeCoroutineFrameSlot(
                offset,
                CoroutineFrameSlotKind.SavedTemp,
                temp,
                _tempOwnershipFacts.GetValueOrDefault(temp)));
        }

        foreach ((int slot, int offset) in transformResult.SavedLocalOffsets.OrderBy(pair => pair.Value))
        {
            CoroutineFrameSlot described = DescribeCoroutineFrameSlot(
                offset,
                CoroutineFrameSlotKind.SavedLocal,
                slot,
                ResolveSavedLocalOwnershipFact(slot, bodyInstructions));
            slots.Add(SavedLocalAliasesSavedTemp(slot, bodyInstructions, transformResult)
                ? described with
                {
                    Ownership = CoroutineFrameSlotOwnership.NotOwned,
                    DropKind = OrdinaryHeapChildDropKind.None,
                    Reason = CoroutineFrameSlotReason.AliasesOwnedFrameWord,
                }
                : described);
        }

        return slots;
    }

    /// <summary>
    /// True when a saved local word holds the same reference as a saved temp word. A body that binds
    /// a value (<c>let x = ...</c>) leaves it in both a temp and a local, and the transform saves
    /// both, but the body took one reference: releasing both words would release it twice. The temp
    /// word keeps the obligation because it is the definition; this demotes the local to an alias.
    /// </summary>
    /// <remarks>
    /// One aliasing store is enough to demote. A local fed by a saved temp on one path and something
    /// else on another would otherwise carry a double release on the aliasing path, and an unreleased
    /// word leaks where a double release corrupts. Captures are never demoted: each is a reference the
    /// frame took for itself in <see cref="RetainCoroutineCapture"/>.
    /// </remarks>
    private static bool SavedLocalAliasesSavedTemp(
        int localSlot,
        List<IrInst> bodyInstructions,
        StateMachineResult transformResult)
    {
        foreach (IrInst instruction in bodyInstructions)
        {
            if (instruction is IrInst.StoreLocal store
                && store.Slot == localSlot
                && transformResult.SavedTempOffsets.ContainsKey(store.Source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ownership fact for a saved local slot: the fact shared by every value stored into it.
    /// Disagreeing stores leave the slot conservatively unowned rather than picking one.
    /// </summary>
    private LoweredTempOwnershipFact? ResolveSavedLocalOwnershipFact(int localSlot, List<IrInst> bodyInstructions)
    {
        LoweredTempOwnershipFact? resolved = null;
        foreach (IrInst instruction in bodyInstructions)
        {
            if (instruction is not IrInst.StoreLocal store || store.Slot != localSlot)
            {
                continue;
            }

            if (!_tempOwnershipFacts.TryGetValue(store.Source, out LoweredTempOwnershipFact? fact))
            {
                return null;
            }

            if (resolved is null)
            {
                resolved = fact;
                continue;
            }

            if (resolved.Representation != fact.Representation)
            {
                return null;
            }
        }

        return resolved;
    }

    private CoroutineFrameSlot DescribeCoroutineFrameSlot(
        int offsetBytes,
        CoroutineFrameSlotKind kind,
        int sourceIndex,
        LoweredTempOwnershipFact? fact)
    {
        if (fact is null)
        {
            return new CoroutineFrameSlot(
                offsetBytes,
                kind,
                sourceIndex,
                CoroutineFrameSlotOwnership.ConservativeUnknown,
                OrdinaryHeapChildDropKind.None,
                Type: null,
                TypeName: null,
                MayBeEmpty: false,
                CoroutineFrameSlotReason.NoOwnershipFact,
                Location: null);
        }

        TypeRef? type = fact.Type is null ? null : Prune(fact.Type);
        (CoroutineFrameSlotOwnership ownership, CoroutineFrameSlotReason reason) = ClassifyCoroutineFrameSlot(fact, type);
        return new CoroutineFrameSlot(
            offsetBytes,
            kind,
            sourceIndex,
            ownership,
            ownership == CoroutineFrameSlotOwnership.FrameOwnedRuntimeRc
                ? CoroutineFrameDropKind(type)
                : OrdinaryHeapChildDropKind.None,
            type,
            type is null ? null : GetOwnedTypeName(type),
            MayBeEmpty: type is TypeRef.TList,
            reason,
            fact.Location);
    }

    private (CoroutineFrameSlotOwnership Ownership, CoroutineFrameSlotReason Reason) ClassifyCoroutineFrameSlot(
        LoweredTempOwnershipFact fact,
        TypeRef? type)
    {
        if (fact.Representation == LoweredTempRepresentation.BorrowedView)
        {
            return (CoroutineFrameSlotOwnership.ResourceOrBorrowedView, CoroutineFrameSlotReason.ResourceOrBorrowedStorage);
        }

        if (type is not null && IsResourceBearing(type))
        {
            return (CoroutineFrameSlotOwnership.FrameOwnedResource, CoroutineFrameSlotReason.FrameOwnedResource);
        }

        if (type is not null && CanArenaReset(type))
        {
            return (CoroutineFrameSlotOwnership.NotOwned, CoroutineFrameSlotReason.CopyTypedValue);
        }

        if (fact.Representation == LoweredTempRepresentation.ArenaRegion)
        {
            return (CoroutineFrameSlotOwnership.RegionOwned, CoroutineFrameSlotReason.RegionBackedValue);
        }

        if (fact.Representation != LoweredTempRepresentation.RuntimeRc)
        {
            return (CoroutineFrameSlotOwnership.ConservativeUnknown, CoroutineFrameSlotReason.NoOwnershipFact);
        }

        return CoroutineFrameDropKind(type) == OrdinaryHeapChildDropKind.None
            ? (CoroutineFrameSlotOwnership.ConservativeUnknown, CoroutineFrameSlotReason.RuntimeRcUndroppableLayout)
            : (CoroutineFrameSlotOwnership.FrameOwnedRuntimeRc, CoroutineFrameSlotReason.RuntimeRcDroppableLayout);
    }

    private OrdinaryHeapChildDropKind CoroutineFrameDropKind(TypeRef? type) => type is null
        ? OrdinaryHeapChildDropKind.None
        : Prune(type) switch
        {
            TypeRef.TStr => OrdinaryHeapChildDropKind.String,
            TypeRef.TBytes => OrdinaryHeapChildDropKind.Bytes,
            TypeRef.TBigInt => OrdinaryHeapChildDropKind.BigInt,
            TypeRef.TList => OrdinaryHeapChildDropKind.List,
            TypeRef.TTuple => OrdinaryHeapChildDropKind.Tuple,
            TypeRef.TNamedType => OrdinaryHeapChildDropKind.Adt,
            _ => OrdinaryHeapChildDropKind.None,
        };

    /// <summary>
    /// Generates the coroutine's frame dropper, or returns null when the frame owns nothing. The
    /// dropper takes the task pointer, releases every frame-owned word, and clears it, so it is
    /// idempotent: completion, cancellation, and reaping may all call it and the second call finds
    /// the words already cleared. A word that was never written is zero because task creation clears
    /// the live-variable region.
    /// </summary>
    private string? SynthesizeCoroutineFrameDropper(
        string coroutineLabel,
        IReadOnlyList<CoroutineFrameSlot> slots,
        IrFunctionOrigin? coroutineOrigin)
    {
        List<CoroutineFrameSlot> owned = [.. slots
            .Where(slot => slot.Ownership is CoroutineFrameSlotOwnership.FrameOwnedRuntimeRc
                or CoroutineFrameSlotOwnership.FrameOwnedResource)
            .OrderBy(slot => slot.OffsetBytes)];
        if (owned.Count == 0)
        {
            return null;
        }

        string label = $"{coroutineLabel}_frame_drop";
        SynthesizedBodyState saved = BeginSynthesizedBody();
        EmitCoroutineFrameDropperBody(owned);
        AddFunction(
            new IrFunction(
                Label: label,
                Instructions: new List<IrInst>(_inst),
                LocalCount: _nextLocalSlot,
                TempCount: _nextTempSlot,
                HasEnvAndArgParams: true),
            new IrFunctionOrigin(
                label,
                IrFunctionOriginKind.CoroutineFrameDropper,
                coroutineOrigin?.Source,
                coroutineLabel,
                coroutineOrigin?.Source is null
                    ? new CompilerFunctionOwner(CompilerFunctionOwnerKind.Program, "coroutine frame")
                    : null,
                $"frame-drop:{coroutineLabel}",
                coroutineOrigin?.GenerationLocation));
        RestoreEnclosingBodyState(saved);
        return label;
    }

    private void EmitCoroutineFrameDropperBody(IReadOnlyList<CoroutineFrameSlot> owned)
    {
        int taskSlot = NewLocal(); // slot 0: the task pointer, in the coroutine's own parameter position
        NewLocal(); // slot 1: unused argument (implicit)
        int taskTemp = NewTemp();
        Emit(new IrInst.LoadLocal(taskTemp, taskSlot));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));

        foreach (CoroutineFrameSlot slot in owned)
        {
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(valueTemp, taskTemp, slot.OffsetBytes));
            int presentTemp = NewTemp();
            Emit(new IrInst.CmpIntNe(presentTemp, valueTemp, zeroTemp));
            string clearedLabel = NewLabel("frame_slot_cleared");
            Emit(new IrInst.JumpIfFalse(presentTemp, clearedLabel));
            EmitCoroutineFrameSlotDrop(slot, valueTemp);
            Emit(new IrInst.StoreMemOffset(taskTemp, slot.OffsetBytes, zeroTemp));
            Emit(new IrInst.Label(clearedLabel));
        }

        Emit(new IrInst.Return(zeroTemp));
    }

    private void EmitCoroutineFrameSlotDrop(CoroutineFrameSlot slot, int valueTemp)
    {
        TypeRef? type = slot.Type is null ? null : Prune(slot.Type);
        if (slot.Ownership == CoroutineFrameSlotOwnership.FrameOwnedResource && type is not null)
        {
            EmitResourceBearingDrop(valueTemp, type);
            return;
        }

        switch (slot.DropKind)
        {
            case OrdinaryHeapChildDropKind.List when type is TypeRef.TList list:
                EmitRuntimeManagedListDrop(valueTemp, list.Element);
                break;
            case OrdinaryHeapChildDropKind.Tuple when type is TypeRef.TTuple tuple:
                EmitRuntimeManagedTupleDrop(valueTemp, tuple);
                break;
            case OrdinaryHeapChildDropKind.Adt when type is TypeRef.TNamedType named:
                EmitRecursiveRuntimeManagedAdtDrop(valueTemp, named);
                break;
            default:
                Emit(new IrInst.RcDrop(
                    valueTemp,
                    slot.TypeName ?? "PatternBinding",
                    RuntimeManaged: true,
                    MayBeEmpty: slot.MayBeEmpty));
                break;
        }
    }

    private void RecordCoroutineFrameRepresentation(
        string coroutineLabel,
        IrFunctionOrigin? origin,
        IReadOnlyList<CoroutineFrameSlot> slots)
    {
        foreach (CoroutineFrameSlot slot in slots)
        {
            _coroutineRepresentationRecords.Add(new CoroutineRepresentationRecord(
                origin,
                coroutineLabel,
                slot.Kind,
                slot.SourceIndex,
                slot.Ownership switch
                {
                    CoroutineFrameSlotOwnership.FrameOwnedRuntimeRc =>
                        CoroutineValueRepresentationDecision.SavedInTaskFrame,
                    CoroutineFrameSlotOwnership.FrameOwnedResource =>
                        CoroutineValueRepresentationDecision.SavedInTaskFrame,
                    CoroutineFrameSlotOwnership.RegionOwned =>
                        CoroutineValueRepresentationDecision.RegionBecauseSuspendGraphUnknown,
                    _ => CoroutineValueRepresentationDecision.Unknown,
                },
                slot.Reason,
                slot.Location));
        }
    }
}
