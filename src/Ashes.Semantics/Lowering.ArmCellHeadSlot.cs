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
/// duplicates. The write reaches the slot only if the duplicate is a real retain once the function's
/// retains are final, and only on the path where the join copied the cell.
/// </summary>
public sealed partial class Lowering
{
    private sealed record ArmCellHeadSlot(List<IrInst> Body, int Slot, int Duplicate);

    private sealed record PendingArmCellHeadWrite(List<IrInst> Body, IrInst.StoreLocal Store, int Duplicate, int Slot);

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
    // the duplicate goes into the slot registered at the cons, but only once the function's
    // ownership is final. The write is emitted into a slot nothing reads and retargeted to the
    // registered slot by `SettlePendingArmCellHeadWrites` only when the duplicate is then still a
    // real retain: a marker left an identity, a retain a later rule withdraws, or a duplicate the
    // pattern-owner placement replaced by a plain read retained nothing, and its release would free
    // a value the cell's source still owns. The write runs only when the cell was in fact copied: a
    // cell that is reference-counted at run time is shared by the copy as it is and keeps its head's
    // duplicate.
    private void WriteArmCellHeadSlot(ArmCellHeadSlot? record, int cellTemp, List<IrInst> functionBody)
    {
        if (record is null
            || (_tcoCtx is null && DefinitionInBody(functionBody, record.Duplicate) is not IrInst.RcDup { RuntimeManaged: true }))
        {
            return;
        }

        int referenceCountedTemp = NewTemp();
        string writeLabel = NewLabel("arm_cell_head_copied");
        string doneLabel = NewLabel("arm_cell_head_shared");
        Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, cellTemp));
        Emit(new IrInst.JumpIfFalse(referenceCountedTemp, writeLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(writeLabel));
        var store = new IrInst.StoreLocal(NewLocal(), record.Duplicate);
        Emit(store);
        Emit(new IrInst.Label(doneLabel));
        _pendingArmCellHeadWrites.Add(new PendingArmCellHeadWrite(functionBody, store, record.Duplicate, record.Slot));
    }

    // Once the function's retains are final (its loop's pattern owners placed and its withdrawals
    // done): a write whose duplicate is a real retain goes to the registered slot; any other stays in
    // the slot nothing reads, so the registered slot stays zero and releases nothing.
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
            if (DefinitionOf(pending.Duplicate) is not IrInst.RcDup { RuntimeManaged: true })
            {
                continue;
            }

            int index = _inst.FindIndex(instruction => instruction is IrInst.StoreLocal write && write.Slot == pending.Store.Slot && write.Source == pending.Duplicate);
            if (index >= 0)
            {
                _inst[index] = new IrInst.StoreLocal(pending.Slot, pending.Duplicate);
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
