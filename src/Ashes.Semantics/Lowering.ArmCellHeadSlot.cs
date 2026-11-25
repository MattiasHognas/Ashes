using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// The head duplicate of a producer's result cell. An arm consing a pattern-bound head onto the
/// function's own recursive call (`head :: copyAll(rest)`, or a curried `head :: append(rest)(right)`)
/// builds its cell in the arena when the head cannot make it a reference-counted cell, and the cell
/// takes the pattern owner's duplicate of its head. The join then copies that arena cell onto the
/// reference-counted heap with a reference of its own to the head, and an arena cell releases
/// nothing, so the cell's duplicate was released by nothing.
///
/// The duplicate's release is keyed to the join's copy. At the cons an owned slot is registered
/// and zeroed (before any exit of the function is emitted, so every exit releases it); the join
/// writes the duplicate into it right behind its copy of that arm's cell, on that arm's path only.
/// An arm the join does not copy leaves the slot zero, which releases nothing. Each cell's record
/// is taken once by the join, so no duplicate gets two slots; a cell that is a constructor field,
/// a back edge's successor or a call's argument is left to the rules that already own its
/// duplicates. A duplicate that is still a pattern-owner marker at the join is written only if the
/// loop's finalize makes it a real retain: the write is neutralized otherwise.
/// </summary>
public sealed partial class Lowering
{
    private sealed record ArmCellHeadSlot(List<IrInst> Body, int Slot, int Duplicate);

    private sealed record PendingArmCellHeadWrite(List<IrInst> Body, IrInst.StoreLocal Store, int Duplicate);

    private static bool ArmCellHeadSlotRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_ARMCELLHEADSLOT") is null;

    private readonly Dictionary<int, ArmCellHeadSlot> _armCellHeadSlots = [];

    private readonly List<PendingArmCellHeadWrite> _pendingArmCellHeadWrites = [];

    // The duplicate a result cell took of its head: a constructor field cell's goes to its owned slot
    // at once, an arm's own cell's to the slot its join writes.
    private void KeepResultCellHeadDuplicateOwned(Expr.Cons cons, int cellTemp, int duplicateTemp, TypeRef headType)
    {
        KeepLoopResultFieldCellHeadDuplicateOwned(cons, cellTemp, duplicateTemp, headType);
        RecordArmCellHeadSlot(cons, cellTemp, duplicateTemp, headType);
    }

    // At the cons: the arm's own arena cell over the function's recursive producer call, holding the
    // duplicate it took of its pattern-owned head.
    private void RecordArmCellHeadSlot(Expr.Cons cons, int cellTemp, int duplicateTemp, TypeRef headType)
    {
        if (duplicateTemp < 0
            || !ArmCellHeadSlotRuleEnabled
            || !GeneralRcEnabled
            || _backEdgeArgumentPosition is not null
            || _tcoArgumentDestination is not null
            || _callArgumentRetainFrame is not null
            || IsBackEdgeCopiedCell(cons)
            || _backEdgeLateCopiedCells.Contains(cons)
            || _backEdgeRecordFieldCells.Contains(cons)
            || _constructorFieldArgument is not null && ReferenceEquals(StripOrSelf(_constructorFieldArgument), cons)
            || !(IsRecursiveProducerTail(cons.Tail) || _loweringFunctionLabels.TryPeek(out string? label) && IsConsChainOntoSelfCall(cons, label))
            || IsRuntimeManagedResultTemp(cellTemp)
            || DefinitionOf(cellTemp) is not IrInst.Alloc { RuntimeManaged: false }
            || ContainsUnresolvedLayoutType(Prune(headType), []))
        {
            return;
        }

        int ownedSlot = NewLocal();
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.StoreLocal(ownedSlot, zeroTemp));
        RegisterGeneralRcOwnedSlot(ownedSlot, Prune(headType));
        _armCellHeadSlots[cellTemp] = new ArmCellHeadSlot(_inst, ownedSlot, duplicateTemp);
    }

    // The record of the arm cell `cellTemp` of the function being lowered, taken by its join.
    private ArmCellHeadSlot? TakeArmCellHeadSlot(int cellTemp)
    {
        if (!_armCellHeadSlots.TryGetValue(cellTemp, out ArmCellHeadSlot? record) || !ReferenceEquals(record.Body, _inst))
        {
            return null;
        }

        _armCellHeadSlots.Remove(cellTemp);
        return record;
    }

    // Right behind the join's copy (emitted into the join's buffer, which replaces the arm's store):
    // the duplicate goes into the slot registered at the cons.
    private void WriteArmCellHeadSlot(ArmCellHeadSlot? record, List<IrInst> functionBody)
    {
        if (record is null)
        {
            return;
        }

        bool settled = DefinitionInBody(functionBody, record.Duplicate) is IrInst.RcDup { RuntimeManaged: true };
        if (!settled && _tcoCtx is null)
        {
            return;
        }

        var store = new IrInst.StoreLocal(record.Slot, record.Duplicate);
        Emit(store);
        if (!settled)
        {
            _pendingArmCellHeadWrites.Add(new PendingArmCellHeadWrite(functionBody, store, record.Duplicate));
        }
    }

    // After the loop's pattern owners are placed: a write whose duplicate stayed a marker (an
    // identity, which retained nothing) is redirected to a slot nothing reads, so the registered slot
    // stays zero and releases nothing.
    private void SettlePendingArmCellHeadWrites()
    {
        if (_pendingArmCellHeadWrites.Count == 0)
        {
            return;
        }

        List<PendingArmCellHeadWrite> settled = [];
        foreach (PendingArmCellHeadWrite pending in _pendingArmCellHeadWrites)
        {
            if (!ReferenceEquals(pending.Body, _inst))
            {
                continue;
            }

            settled.Add(pending);
            if (DefinitionOf(pending.Duplicate) is IrInst.RcDup { RuntimeManaged: true })
            {
                continue;
            }

            int index = _inst.FindIndex(instruction => ReferenceEquals(instruction, pending.Store));
            if (index >= 0)
            {
                _inst[index] = new IrInst.StoreLocal(NewLocal(), pending.Duplicate);
            }
        }

        foreach (PendingArmCellHeadWrite pending in settled)
        {
            _pendingArmCellHeadWrites.Remove(pending);
        }
    }

    private static IrInst? DefinitionInBody(List<IrInst> body, int temp)
    {
        for (int i = body.Count - 1; i >= 0; i--)
        {
            if (body[i] is IrInst.RcDup dup && dup.Target == temp)
            {
                return dup;
            }
        }

        return null;
    }
}
