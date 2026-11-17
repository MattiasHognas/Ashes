namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool BorrowedStringTuplePartsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_TUPLESTRINGPARTS") is null;

    private bool IsTupleResultPartType(TypeRef type)
        => IsGeneralRcValueType(type)
            || (BorrowedStringTuplePartsEnabled && GeneralRcEnabled && Prune(type) is TypeRef.TStr or TypeRef.TBytes);

    // A known string-like child beside an abstract sibling was copied into the arena when the
    // tuple was built. For a sole returned tuple, the parts protocol can instead give its caller
    // a counted reference. Keep the copy for callers that do not request parts, including fixed
    // copy-out callers. Only borrowed reads qualify: a fresh unowned child's original reference
    // needs a separate move proof. The existing retain at the part store and caller mask settlement
    // balance the reference on the requested path.
    private Dictionary<int, int> PreserveRequestedBorrowedStringParts(TypeRef.TTuple tuple, List<int> cells, int[] parts)
    {
        Dictionary<int, int> borrowedParts = [];
        if (!BorrowedStringTuplePartsEnabled || cells.Count != 1)
        {
            return borrowedParts;
        }

        HashSet<int> ownedSlots = [.. _generalRcOwnedSlots.Select(owned => owned.Slot)];
        List<IrInst.StoreMemOffset> stores = [.. _inst.OfType<IrInst.StoreMemOffset>()
            .Where(store => store.BasePtr == cells[0])];
        foreach (IrInst.StoreMemOffset store in stores)
        {
            int part = store.OffsetBytes / 8;
            if (store.OffsetBytes < 0 || store.OffsetBytes % 8 != 0 || !parts.Contains(part)
                || Prune(tuple.Elements[part]) is not (TypeRef.TStr or TypeRef.TBytes)
                || stores.Count(candidate => candidate.OffsetBytes == store.OffsetBytes) != 1
                || !TryFindBorrowedStringDemotion(store.Source, ownedSlots, out int index, out int source, out string doneLabel)
                || !BorrowedStringSurvivesUntilStore(store))
            {
                continue;
            }

            List<IrInst> function = _inst;
            List<IrInst> guard = [];
            _inst = guard;
            try
            {
                string copyLabel = NewLabel("tuple_string_part_copy");
                EmitJumpUnlessTuplePartsRequested(copyLabel);
                Emit(new IrInst.Jump(doneLabel));
                Emit(new IrInst.Label(copyLabel));
            }
            finally
            {
                _inst = function;
            }

            _inst.InsertRange(index, guard);
            ShiftRecordedInstructionIndices(index - 1, guard.Count);
            borrowedParts[store.Source] = source;
        }

        return borrowedParts;
    }

    // The part's retain is inserted at its tuple store, not at the earlier demotion. A nested
    // scope may otherwise release the source owner between them. Accept only construction and
    // data operations, with no branch out of the interval; every unproved operation keeps copying.
    private bool BorrowedStringSurvivesUntilStore(IrInst.StoreMemOffset store)
    {
        int start = _inst.FindLastIndex(instruction => instruction is IrInst.LoadLocal load && load.Target == store.Source);
        int end = _inst.FindIndex(instruction => ReferenceEquals(instruction, store));
        if (start < 0 || end <= start)
        {
            return false;
        }

        HashSet<string> labels = new(StringComparer.Ordinal);
        for (int index = start + 1; index < end; index++)
        {
            if (_inst[index] is IrInst.Label label)
            {
                labels.Add(label.Name);
            }
        }

        for (int index = start + 1; index < end; index++)
        {
            bool safe = _inst[index] switch
            {
                IrInst.LoadLocal or IrInst.StoreLocal or IrInst.LoadMemOffset or IrInst.StoreMemOffset
                    or IrInst.LoadConstInt or IrInst.LoadArgumentOwnership or IrInst.IsReferenceCounted
                    or IrInst.CopyOutArena or IrInst.Alloc or IrInst.Borrow or IrInst.RcDup
                    or IrInst.AndInt or IrInst.ShrInt or IrInst.Label => true,
                IrInst.Jump jump => labels.Contains(jump.Target),
                IrInst.JumpIfFalse jump => labels.Contains(jump.Target),
                _ => false,
            };
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }

    // Match precisely the demotion emitted by EmitArenaCopyOfReferenceCounted without a fresh
    // source release. Its result must be used only by the returned tuple's one store. No other
    // CopyOutArena becomes an ownership identity, and no non-returned cell changes behavior.
    private bool TryFindBorrowedStringDemotion(int resultTemp, HashSet<int> ownedSlots, out int guardIndex, out int source, out string doneLabel)
    {
        guardIndex = -1;
        source = -1;
        doneLabel = "";
        int index = _inst.FindLastIndex(instruction => instruction is IrInst.LoadLocal load && load.Target == resultTemp);
        if (index < 6
            || _inst[index] is not IrInst.LoadLocal result
            || _inst[index - 1] is not IrInst.Label done
            || _inst[index - 2] is not IrInst.StoreLocal copiedStore
            || _inst[index - 3] is not IrInst.CopyOutArena { StaticSizeBytes: -1, RuntimeManaged: false, Purpose: IrInst.CopyOutPurpose.IndependentClone, DeferredElementType: null } copy
            || _inst[index - 4] is not IrInst.JumpIfFalse skip
            || _inst[index - 5] is not IrInst.IsReferenceCounted counted
            || _inst[index - 6] is not IrInst.StoreLocal initial
            || result.Slot != initial.Slot || result.Slot != copiedStore.Slot
            || initial.Source != copy.SrcTemp || initial.Source != counted.SourceTemp
            || copiedStore.Source != copy.DestTemp || skip.CondTemp != counted.Target
            || !string.Equals(skip.Target, done.Name, StringComparison.Ordinal)
            || IsNewlyProducedRcTemp(initial.Source)
            || !IsReadOutOfOwnedValue(initial.Source, ownedSlots, [])
            || _inst.Count(instruction => StateMachineTransform.GetUsedTemps(instruction).Contains(resultTemp)) != 1)
        {
            return false;
        }

        guardIndex = index - 5;
        source = initial.Source;
        doneLabel = done.Name;
        return true;
    }
}
