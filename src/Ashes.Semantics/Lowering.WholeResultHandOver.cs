namespace Ashes.Semantics;

/// <summary>
/// The hand-over of a result that is one of the function's owned values returned as it is, or an
/// arena cell built around one, when the result's type is outside the contract's types (a generic
/// <c>Result(e, T)</c> whose <c>Ok</c> arm returns an owned call result, or wraps one in
/// <c>Ok</c>, say). Such a value is reached by the result and was left unreleased, and the caller,
/// which may know the concrete type, did not know it owned it. A caller asks with the tuple-parts
/// request naming no parts. A function that is asked, and whose reference-counted result is at run
/// time the value an owned slot holds, leaves that slot unreleased and writes
/// <see cref="WholeResultHandedOverBit"/> into the caller's mask; one whose arena result is a cell
/// of a constructor with one contract field, holding an owned slot's reference-counted value, and
/// scalar fields otherwise, copies the cell onto the reference-counted heap with the slot's
/// reference moved into it, and hands that copy over the same way. The caller takes the result as
/// its own owned value.
/// </summary>
public sealed partial class Lowering
{
    private const long WholeResultHandedOverBit = 1L << 62;

    private static bool WholeResultHandOverEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_WHOLERESULTHANDOVER") is null;

    private static bool WrappedResultHandOverEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_WRAPPEDRESULTHANDOVER") is null;

    // The functions, by label, that may hand their whole result over when asked.
    private readonly HashSet<string> _wholeResultHandOverLabels = new(StringComparer.Ordinal);

    // A constructor whose cell can be handed over: its tag, arity, layout, and the one field of a
    // contract type, the others scalars.
    private sealed record WrappedResultConstructor(int Tag, int Arity, bool Tagless, int ContractField);

    /// <summary>
    /// The return of a result outside the contract's types that may be an owned slot's value or a
    /// cell wrapping one: when the caller asked, the slot's reference is handed over as described
    /// above and the other releasable slots are released; otherwise the releasable slots are. The
    /// temp to return, -1 when no owned slot can be the result or wrapped by it.
    /// </summary>
    private int TryHandOverOwnedResultWhole(string label, int resultTemp, TypeRef resultType, List<GeneralRcOwnedSlot> releasable, bool inLoop)
    {
        List<GeneralRcOwnedSlot> candidates = WholeResultCandidates(releasable, inLoop);
        if (candidates.Count == 0)
        {
            return -1;
        }

        List<WrappedResultConstructor> wrappers = WrappedResultConstructors(resultType);
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, resultTemp));
        string keepLabel = NewLabel("whole_result_not_requested");
        string doneLabel = NewLabel("whole_result_handed_over");
        string arenaLabel = NewLabel("whole_result_arena");
        EmitJumpUnlessTuplePartsRequested(keepLabel);
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, resultTemp));
        Emit(new IrInst.JumpIfFalse(countedTemp, arenaLabel));
        foreach (GeneralRcOwnedSlot candidate in candidates)
        {
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, candidate.Slot));
            EmitHandOverIfSame(resultTemp, valueTemp, releasable, doneLabel, () => { });
        }

        Emit(new IrInst.Jump(keepLabel));
        Emit(new IrInst.Label(arenaLabel));
        foreach (WrappedResultConstructor wrapper in wrappers)
        {
            EmitWrappedResultHandOver(resultTemp, resultSlot, wrapper, candidates, releasable, keepLabel, doneLabel);
        }

        Emit(new IrInst.Label(keepLabel));
        EmitGeneralRcOwnedSlotDrops(releasable);
        Emit(new IrInst.Label(doneLabel));
        _wholeResultHandOverLabels.Add(label);
        _tupleResultPartLabels.TryAdd(label, []);
        int handedTemp = NewTemp();
        Emit(new IrInst.LoadLocal(handedTemp, resultSlot));
        return handedTemp;
    }

    // When `heldTemp` is the value `candidateTemp`: the copy `moveInto` makes, the mask, the
    // releasable slots' release, and the jump to `doneLabel`.
    private void EmitHandOverIfSame(int heldTemp, int candidateTemp, List<GeneralRcOwnedSlot> releasable, string doneLabel, Action moveInto)
    {
        string nextLabel = NewLabel("whole_result_other_value");
        int sameTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(sameTemp, heldTemp, candidateTemp));
        Emit(new IrInst.JumpIfFalse(sameTemp, nextLabel));
        moveInto();
        EmitResultHandOverMask(WholeResultHandedOverBit);
        EmitGeneralRcOwnedSlotDrops(releasable);
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(nextLabel));
    }

    // An arena result of the wrapper's constructor whose contract field holds an owned slot's
    // reference-counted value: copied onto the reference-counted heap field by field, the slot's
    // reference moving into the copy, which becomes the result.
    private void EmitWrappedResultHandOver(
        int resultTemp,
        int resultSlot,
        WrappedResultConstructor wrapper,
        List<GeneralRcOwnedSlot> candidates,
        List<GeneralRcOwnedSlot> releasable,
        string keepLabel,
        string doneLabel)
    {
        string otherLabel = NewLabel("whole_result_other_constructor");
        EmitSkipUnlessPresent(resultTemp, keepLabel);
        if (!wrapper.Tagless)
        {
            int tagTemp = NewTemp();
            Emit(new IrInst.GetAdtTag(tagTemp, resultTemp));
            int expectedTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(expectedTemp, wrapper.Tag));
            int sameTagTemp = NewTemp();
            Emit(new IrInst.CmpIntEq(sameTagTemp, tagTemp, expectedTemp));
            Emit(new IrInst.JumpIfFalse(sameTagTemp, otherLabel));
        }

        int fieldTemp = NewTemp();
        Emit(new IrInst.GetAdtField(fieldTemp, resultTemp, wrapper.ContractField, wrapper.Tagless));
        EmitSkipUnlessReferenceCounted(fieldTemp, otherLabel);
        foreach (GeneralRcOwnedSlot candidate in candidates)
        {
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, candidate.Slot));
            EmitHandOverIfSame(fieldTemp, valueTemp, releasable, doneLabel, () => EmitWrappedResultCopy(resultTemp, resultSlot, wrapper));
        }

        Emit(new IrInst.Label(otherLabel));
    }

    private void EmitWrappedResultCopy(int resultTemp, int resultSlot, WrappedResultConstructor wrapper)
    {
        int copyTemp = NewTemp();
        Emit(new IrInst.AllocAdt(copyTemp, wrapper.Tag, wrapper.Arity, RuntimeManaged: true, Tagless: wrapper.Tagless));
        for (int field = 0; field < wrapper.Arity; field++)
        {
            int valueTemp = NewTemp();
            Emit(new IrInst.GetAdtField(valueTemp, resultTemp, field, wrapper.Tagless));
            Emit(new IrInst.SetAdtField(copyTemp, field, valueTemp, wrapper.Tagless));
        }

        Emit(new IrInst.StoreLocal(resultSlot, copyTemp));
    }

    private void EmitSkipUnlessPresent(int valueTemp, string skipLabel)
    {
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int presentTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(presentTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(presentTemp, skipLabel));
    }

    // The constructors of a named result type whose cell can wrap an owned value: exactly one field
    // of a contract type, every other field a scalar.
    private List<WrappedResultConstructor> WrappedResultConstructors(TypeRef resultType)
    {
        List<WrappedResultConstructor> wrappers = [];
        if (!WrappedResultHandOverEnabled || Prune(resultType) is not TypeRef.TNamedType named)
        {
            return wrappers;
        }

        foreach (ConstructorSymbol constructor in named.Symbol.Constructors)
        {
            int contractField = -1;
            bool eligible = constructor.Arity > 0;
            for (int field = 0; field < constructor.Arity && eligible; field++)
            {
                TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, field, named));
                if (IsGeneralRcValueType(fieldType) && contractField < 0)
                {
                    contractField = field;
                }
                else if (fieldType is not (TypeRef.TInt or TypeRef.TUInt or TypeRef.TFloat or TypeRef.TBool or TypeRef.TRune))
                {
                    eligible = false;
                }
            }

            if (eligible && contractField >= 0)
            {
                wrappers.Add(new WrappedResultConstructor(GetConstructorTag(constructor), constructor.Arity, IsTaglessConstructor(constructor), contractField));
            }
        }

        wrappers.Sort((left, right) => left.Tag.CompareTo(right.Tag));
        return wrappers;
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
