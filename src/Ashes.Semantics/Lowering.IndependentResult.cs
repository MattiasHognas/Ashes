namespace Ashes.Semantics;

/// <summary>
/// The signal that a call's result holds nothing of the values its call borrowed. A function of a
/// contract result type that is asked (the tuple-parts request, as a forwarding function passes it
/// on) writes <see cref="IndependentResultBit"/> into the caller's mask: its result holds references
/// of its own. A function returning another call's result of unresolved type as it is forwards its
/// caller's request to that call, and once the call returns it reads the mask: when the call's
/// result is independent (this bit, or <see cref="WholeResultHandedOverBit"/>), the owned values the
/// result reached only through that call are released rather than left to leak.
/// </summary>
public sealed partial class Lowering
{
    private const long IndependentResultBit = 1L << 61;

    private static bool IndependentResultSignalEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_INDEPENDENTRESULT") is null;

    // The result temp of the call the function being finished forwarded its caller's request to; -1
    // when it forwarded none.
    private int _forwardedResultCallTemp = -1;

    // A function of a contract result type: noted as owning its result, and, when asked, the result's
    // independence written into the caller's mask.
    private void NoteOwnedContractResult(string label)
    {
        _generalRcOwnedResultLabels.Add(label);
        if (!IndependentResultSignalEnabled || !TupleResultPartsEnabled || _inCoroutineBody)
        {
            return;
        }

        string skipLabel = NewLabel("independent_result_not_requested");
        EmitJumpUnlessTuplePartsRequested(skipLabel);
        EmitResultHandOverMask(IndependentResultBit);
        Emit(new IrInst.Label(skipLabel));
    }

    private static bool IndependentCountedResultEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_INDEPENDENTCOUNTEDRESULT") is null;

    /// <summary>
    /// After a call that asked for a record or variant result of the contract's types and named no
    /// parts: jumps to the owned path when the mask says the result is independent and the result is
    /// reference-counted. Only a function of a contract result type writes that bit, and a function
    /// in between forwards the request only to a call whose result it returns as it is, so the value
    /// is that function's owned result. An arena result is the copy that function made when a
    /// forwarder asked for one, and is copied as before.
    /// </summary>
    private void EmitJumpIfIndependentCountedResult(int maskTemp, int resultTemp, TypeRef resultType, TupleResultPartsRequest? request, string ownedLabel)
    {
        if (!IndependentCountedResultEnabled
            || !IndependentResultSignalEnabled
            || maskTemp < 0
            || request is not { Parts.Length: 0 }
            || Prune(resultType) is not TypeRef.TNamedType named
            || !IsGeneralRcValueType(named))
        {
            return;
        }

        string copyLabel = NewLabel("independent_result_not_counted");
        int bitTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(bitTemp, IndependentResultBit));
        int independentTemp = NewTemp();
        Emit(new IrInst.AndInt(independentTemp, maskTemp, bitTemp));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int signalledTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(signalledTemp, independentTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(signalledTemp, copyLabel));
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, resultTemp));
        Emit(new IrInst.JumpIfFalse(countedTemp, copyLabel));
        Emit(new IrInst.Jump(ownedLabel));
        Emit(new IrInst.Label(copyLabel));
    }

    /// <summary>
    /// After a forwarded call whose result is returned as it is: the owned slots out of the result's
    /// reach once that call's result is independent of its arguments, but not otherwise, released
    /// when this function was asked and the callee's mask says the result is independent.
    /// </summary>
    private void ReleaseOwnedSlotsBehindIndependentForwardedResult(int resultTemp, TypeRef resultType, List<GeneralRcOwnedSlot> releasable, bool inLoop)
    {
        int callTemp = _forwardedResultCallTemp;
        _forwardedResultCallTemp = -1;
        if (!IndependentResultSignalEnabled || callTemp < 0 || inLoop || _inCoroutineBody)
        {
            return;
        }

        List<GeneralRcOwnedSlot> behindCall = [.. OwnedSlotsOutOfResultReach(resultTemp, resultType, _generalRcOwnedSlots, [callTemp])
            .Where(owned => !owned.Predecessor && owned.Loop is null && !releasable.Contains(owned))];
        if (behindCall.Count == 0)
        {
            return;
        }

        string skipLabel = NewLabel("forwarded_result_not_independent");
        EmitJumpUnlessTuplePartsRequested(skipLabel);
        int wordTemp = NewTemp();
        Emit(new IrInst.LoadArgumentOwnership(wordTemp));
        int addressBitsTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(addressBitsTemp, TupleResultPartsMaskAddressBits));
        int addressTemp = NewTemp();
        Emit(new IrInst.AndInt(addressTemp, wordTemp, addressBitsTemp));
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(maskTemp, addressTemp, 0));
        int bitsTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(bitsTemp, IndependentResultBit | WholeResultHandedOverBit));
        int independentTemp = NewTemp();
        Emit(new IrInst.AndInt(independentTemp, maskTemp, bitsTemp));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int signalledTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(signalledTemp, independentTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(signalledTemp, skipLabel));
        EmitGeneralRcOwnedSlotDrops(behindCall);
        Emit(new IrInst.Label(skipLabel));
    }
}
