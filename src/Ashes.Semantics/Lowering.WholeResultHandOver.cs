namespace Ashes.Semantics;

/// <summary>
/// The hand-over of a result that is one of the function's owned values returned as it is, when
/// the result's type is outside the contract's types (a generic <c>Result(e, T)</c> whose
/// <c>Ok</c> arm returns an owned call result, say). Such a value is reached by the result and was
/// left unreleased, and the caller, which may know the concrete type, did not know it owned it. A
/// caller asks with the tuple-parts request naming no parts; a function that is asked, and whose
/// reference-counted result is at run time the value an owned slot holds, leaves that slot
/// unreleased and writes <see cref="WholeResultHandedOverBit"/> into the caller's mask, and the
/// caller takes the result as its own owned value.
/// </summary>
public sealed partial class Lowering
{
    private const long WholeResultHandedOverBit = 1L << 62;

    private static bool WholeResultHandOverEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_WHOLERESULTHANDOVER") is null;

    // The functions, by label, that may hand their whole result over when asked.
    private readonly HashSet<string> _wholeResultHandOverLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// The return of a result outside the contract's types that may be an owned slot's value: when
    /// the caller asked and the result is, at run time, the reference-counted value one such slot
    /// holds, that slot's reference is handed over with the mask and the other releasable slots
    /// are released; otherwise the releasable slots are. False when no owned slot can be the
    /// result.
    /// </summary>
    private bool TryHandOverOwnedResultWhole(string label, int resultTemp, List<GeneralRcOwnedSlot> releasable, bool inLoop)
    {
        List<GeneralRcOwnedSlot> candidates = WholeResultCandidates(releasable, inLoop);
        if (candidates.Count == 0)
        {
            return false;
        }

        string keepLabel = NewLabel("whole_result_not_requested");
        string doneLabel = NewLabel("whole_result_handed_over");
        EmitJumpUnlessTuplePartsRequested(keepLabel);
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, resultTemp));
        Emit(new IrInst.JumpIfFalse(countedTemp, keepLabel));
        foreach (GeneralRcOwnedSlot candidate in candidates)
        {
            string nextLabel = NewLabel("whole_result_other_value");
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, candidate.Slot));
            int sameTemp = NewTemp();
            Emit(new IrInst.CmpIntEq(sameTemp, resultTemp, valueTemp));
            Emit(new IrInst.JumpIfFalse(sameTemp, nextLabel));
            EmitResultHandOverMask(WholeResultHandedOverBit);
            EmitGeneralRcOwnedSlotDrops(releasable);
            Emit(new IrInst.Jump(doneLabel));
            Emit(new IrInst.Label(nextLabel));
        }

        Emit(new IrInst.Label(keepLabel));
        EmitGeneralRcOwnedSlotDrops(releasable);
        Emit(new IrInst.Label(doneLabel));
        _wholeResultHandOverLabels.Add(label);
        _tupleResultPartLabels.TryAdd(label, []);
        return true;
    }

    // The owned slots the result may be as it is: the ones out of the releasable set, stored by
    // this function outside any loop.
    private List<GeneralRcOwnedSlot> WholeResultCandidates(List<GeneralRcOwnedSlot> releasable, bool inLoop)
    {
        if (!WholeResultHandOverEnabled || !TupleResultPartsEnabled || inLoop || _inCoroutineBody)
        {
            return [];
        }

        return [.. _generalRcOwnedSlots.Where(owned => !owned.Predecessor && owned.Loop is null && !releasable.Contains(owned))];
    }

    // After a call that asked for its result's hand-over: falls through to the caller's own copy
    // unless the callee's mask says it handed the whole result over, which jumps to the owned path.
    private void EmitJumpIfWholeResultHandedOver(int maskTemp, string ownedLabel)
    {
        if (maskTemp < 0)
        {
            return;
        }

        int bitTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(bitTemp, WholeResultHandedOverBit));
        int wholeTemp = NewTemp();
        Emit(new IrInst.AndInt(wholeTemp, maskTemp, bitTemp));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int handedTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(handedTemp, wholeTemp, zeroTemp));
        string copyLabel = NewLabel("whole_result_not_handed_over");
        Emit(new IrInst.JumpIfFalse(handedTemp, copyLabel));
        Emit(new IrInst.Jump(ownedLabel));
        Emit(new IrInst.Label(copyLabel));
    }
}
