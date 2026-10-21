using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// The hand-over of a generic tuple result's contract parts. A tuple result with an element of
/// unresolved layout (a <c>List(a)</c> beside a contract value) can only be an arena cell, so the
/// function cannot release the owned values its contract elements point into, and a caller that
/// copies the tuple out retains those elements again. A caller that knows the concrete type asks
/// for the parts through bit 2 of the hidden ownership word; the callee then gives each contract
/// element of an arena result a reference of its own (a move of an owned value, a retain of a
/// borrowed one) and releases its owned values behind them, and the caller releases those
/// references once its copy holds its own.
/// </summary>
public sealed partial class Lowering
{
    private const long TupleResultPartsRequestBit = 4;

    private static bool TupleResultPartsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_TUPLERESULTPARTS") is null;

    // The functions that hand a requested tuple result's parts over, by label: the element indices
    // whose references the caller takes.
    private readonly Dictionary<string, int[]> _tupleResultPartLabels = new(StringComparer.Ordinal);

    // The calls that asked for their result's parts: the request constant (cleared when the call's
    // result is not copied out after all) and the parts the callee hands over.
    private readonly Dictionary<IrInst.CallClosure, (int RequestTemp, int[] Parts)> _tupleResultPartsRequests =
        new(ReferenceEqualityComparer.Instance);

    // The withdrawn requests, by the result temp of the call that made them: a function returning
    // that result as it is forwards its own caller's request in their place.
    private readonly Dictionary<IrInst.CallClosure, (int RequestTemp, int[] Parts)> _withdrawnTupleResultPartsRequests =
        new(ReferenceEqualityComparer.Instance);

    // The parts a known function hands over when its result is asked for, or null.
    private int[]? CalleeTupleResultParts(Expr rootExpr, int argumentCount)
    {
        if (!TupleResultPartsEnabled || argumentCount == 0 || !TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return null;
        }

        for (int i = 1; i < argumentCount; i++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                return null;
            }

            label = nextLabel;
        }

        return _tupleResultPartLabels.GetValueOrDefault(label);
    }

    // The hidden ownership word with the tuple-parts request beside whatever the call already
    // passes, the request constant on the left so a withdrawal of an arena-result request still
    // follows the right operand to its own constant; the request constant is returned too, so the
    // request can be withdrawn.
    private int EmitTupleResultPartsRequestWord(int ownershipWordTemp, out int requestTemp)
    {
        requestTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(requestTemp, TupleResultPartsRequestBit));
        if (ownershipWordTemp < 0)
        {
            return requestTemp;
        }

        int wordTemp = NewTemp();
        Emit(new IrInst.OrInt(wordTemp, requestTemp, ownershipWordTemp));
        return wordTemp;
    }

    /// <summary>
    /// The parts the call producing <paramref name="resultTemp"/> asked for, taken off the pending
    /// requests; null when it asked for none. A <paramref name="withdraw"/>n request is cleared in
    /// the call's ownership word, so the callee keeps its own references.
    /// </summary>
    private int[]? TakeTupleResultPartsRequest(int resultTemp, bool withdraw)
    {
        for (int i = _inst.Count - 1; i >= 0 && i >= _inst.Count - 64; i--)
        {
            if (_inst[i] is not IrInst.CallClosure call || call.Target != resultTemp)
            {
                continue;
            }

            if (!_tupleResultPartsRequests.Remove(call, out (int RequestTemp, int[] Parts) request))
            {
                return null;
            }

            if (!withdraw)
            {
                return request.Parts;
            }

            _withdrawnTupleResultPartsRequests[call] = request;
            for (int j = i - 1; j >= 0 && j >= i - 64; j--)
            {
                if (_inst[j] is IrInst.LoadConstInt constant && constant.Target == request.RequestTemp)
                {
                    _inst[j] = constant with { Value = 0 };
                    break;
                }
            }

            return null;
        }

        return null;
    }

    // The handed-over parts of a call's tuple result, read before the result is copied out.
    private int[] LoadHandedOverTupleParts(int resultTemp, int[]? parts)
    {
        if (parts is null)
        {
            return [];
        }

        int[] partTemps = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            partTemps[i] = NewTemp();
            Emit(new IrInst.LoadMemOffset(partTemps[i], resultTemp, parts[i] * 8));
        }

        return partTemps;
    }

    /// <summary>
    /// After a call's tuple result is copied out of the arena: the references the callee handed
    /// over with the parts are released, the copy holding its own. A result the callee placed on
    /// the reference-counted heap handed nothing over.
    /// </summary>
    private void ReleaseHandedOverTupleParts(int resultTemp, TypeRef resultType, int[]? parts, int[] partTemps)
    {
        if (parts is null || Prune(resultType) is not TypeRef.TTuple tuple)
        {
            return;
        }

        string arenaLabel = NewLabel("tuple_parts_arena");
        string doneLabel = NewLabel("tuple_parts_done");
        int resultCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(resultCountedTemp, resultTemp));
        Emit(new IrInst.JumpIfFalse(resultCountedTemp, arenaLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(arenaLabel));
        for (int i = 0; i < parts.Length; i++)
        {
            string skipLabel = NewLabel("tuple_part_kept");
            EmitSkipUnlessReferenceCounted(partTemps[i], skipLabel);
            EmitRuntimeManagedChildDrop(partTemps[i], tuple.Elements[parts[i]]);
            Emit(new IrInst.Label(skipLabel));
        }

        Emit(new IrInst.Label(doneLabel));
    }

    private void EmitSkipUnlessReferenceCounted(int valueTemp, string skipLabel)
    {
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int presentTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(presentTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(presentTemp, skipLabel));
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, valueTemp));
        Emit(new IrInst.JumpIfFalse(countedTemp, skipLabel));
    }

    // The contract elements of a tuple result type with an element of unresolved layout.
    private int[] TupleResultParts(TypeRef resultType)
    {
        if (Prune(resultType) is not TypeRef.TTuple tuple || !ContainsUnresolvedLayoutType(tuple, []))
        {
            return [];
        }

        return [.. Enumerable.Range(0, tuple.Elements.Count).Where(index => IsGeneralRcValueType(tuple.Elements[index]))];
    }

    /// <summary>
    /// The return of a function whose result is a generic tuple with contract elements. When the
    /// caller asks for the parts and the result is an arena cell, each part holds a reference of
    /// its own and the owned values only the parts read out of are released behind them; otherwise
    /// only the owned values out of the result's reach are. When every cell the result can be is
    /// known (outside a loop), a part takes its reference where it is stored: a fresh value's is
    /// the cell's already, any other value is retained under the request. Otherwise every part is
    /// retained at the return and only the owned values out of the result's reach are released.
    /// False when the result is not such a tuple.
    /// </summary>
    private bool TryReleaseOwnedSlotsHandingOverTupleParts(string label, int resultTemp, TypeRef resultType, bool inLoop)
    {
        int[] parts = TupleResultPartsEnabled && GeneralRcEnabled && !_inCoroutineBody && !IsRuntimeManagedResultTemp(resultTemp)
            ? TupleResultParts(resultType)
            : [];
        if (parts.Length == 0)
        {
            return false;
        }

        List<GeneralRcOwnedSlot> kept = OwnedSlotsOutOfResultReach(resultTemp, _generalRcOwnedSlots);
        if (TryForwardTupleResultPartsRequest(resultTemp, parts))
        {
            EmitGeneralRcOwnedSlotDrops(kept);
            _tupleResultPartLabels[label] = parts;
            return true;
        }

        List<int>? cells = inLoop ? null : ArenaCellsOfResult(resultTemp);
        List<GeneralRcOwnedSlot> handedOver = kept;
        if (cells is { Count: > 0 })
        {
            handedOver = OwnedSlotsReleasedBehindTupleParts((TypeRef.TTuple)Prune(resultType), cells, parts, out List<IrInst.StoreMemOffset> retainedStores);
            RetainTuplePartStoresWhenRequested(retainedStores);
        }

        string keepLabel = NewLabel("tuple_parts_not_requested");
        string doneLabel = NewLabel("tuple_parts_handed_over");
        EmitJumpUnlessTuplePartsRequested(resultTemp, keepLabel);
        if (cells is not { Count: > 0 })
        {
            foreach (int part in parts)
            {
                int childTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(childTemp, resultTemp, part * 8));
                EmitRetainIfReferenceCounted(childTemp);
            }
        }

        EmitGeneralRcOwnedSlotDrops(handedOver);
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(keepLabel));
        EmitGeneralRcOwnedSlotDrops(kept);
        Emit(new IrInst.Label(doneLabel));
        _tupleResultPartLabels[label] = parts;
        return true;
    }

    private static bool ForwardedTupleResultPartsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_FORWARDEDTUPLEPARTS") is null;

    /// <summary>
    /// A result that is another function's generic tuple result, returned as it is: the call's
    /// withdrawn request for the same parts is replaced by this function's own caller's request
    /// (bit 2 of its ownership word), so the callee hands the parts over to that caller directly and
    /// this function takes none. False when the result is no such call's.
    /// </summary>
    private bool TryForwardTupleResultPartsRequest(int resultTemp, int[] parts)
    {
        IrInst.CallClosure? call = SoleCallThroughLocals(resultTemp);
        if (!ForwardedTupleResultPartsEnabled
            || call is null
            || !_withdrawnTupleResultPartsRequests.Remove(call, out (int RequestTemp, int[] Parts) request)
            || !request.Parts.SequenceEqual(parts))
        {
            return false;
        }

        int callIndex = _inst.FindLastIndex(instruction => ReferenceEquals(instruction, call));
        int index = callIndex < 0 ? -1 : _inst.FindLastIndex(callIndex, instruction => instruction is IrInst.LoadConstInt constant && constant.Target == request.RequestTemp);
        if (index < 0)
        {
            return false;
        }

        int wordTemp = NewTemp();
        int bitTemp = NewTemp();
        IrInst[] forwarded =
        [
            new IrInst.LoadArgumentOwnership(wordTemp),
            new IrInst.LoadConstInt(bitTemp, TupleResultPartsRequestBit),
            new IrInst.AndInt(request.RequestTemp, wordTemp, bitTemp),
        ];
        _inst.RemoveAt(index);
        _inst.InsertRange(index, forwarded);
        ShiftRecordedInstructionIndices(index, forwarded.Length - 1);
        return true;
    }

    // The call whose result a temp is, through locals stored once and borrows; null when it may be
    // another value.
    private IrInst.CallClosure? SoleCallThroughLocals(int temp)
    {
        for (int depth = 0; depth < 16; depth++)
        {
            switch (DefinitionOf(temp))
            {
                case IrInst.CallClosure call:
                    return call;
                case IrInst.Borrow borrow:
                    temp = borrow.SourceTemp;
                    break;
                case IrInst.LoadLocal load:
                    List<int> sources = [.. _inst.OfType<IrInst.StoreLocal>().Where(store => store.Slot == load.Slot).Select(store => store.Source)];
                    if (sources.Count != 1)
                    {
                        return null;
                    }

                    temp = sources[0];
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private void EmitRetainIfReferenceCounted(int valueTemp)
    {
        string skipLabel = NewLabel("tuple_part_not_counted");
        EmitSkipUnlessReferenceCounted(valueTemp, skipLabel);
        Emit(new IrInst.RcDup(NewTemp(), valueTemp, RuntimeManaged: true));
        Emit(new IrInst.Label(skipLabel));
    }

    // Falls through when the caller asked for the parts and the result is an arena cell.
    private void EmitJumpUnlessTuplePartsRequested(int resultTemp, string keepLabel)
    {
        EmitJumpUnlessTuplePartsRequested(keepLabel);
        string arenaLabel = NewLabel("tuple_parts_arena_result");
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, resultTemp));
        Emit(new IrInst.JumpIfFalse(countedTemp, arenaLabel));
        Emit(new IrInst.Jump(keepLabel));
        Emit(new IrInst.Label(arenaLabel));
    }

    private void EmitJumpUnlessTuplePartsRequested(string keepLabel)
    {
        int wordTemp = NewTemp();
        Emit(new IrInst.LoadArgumentOwnership(wordTemp));
        int shiftTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(shiftTemp, 2));
        int shiftedTemp = NewTemp();
        Emit(new IrInst.ShrInt(shiftedTemp, wordTemp, shiftTemp));
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, 1));
        int requestedTemp = NewTemp();
        Emit(new IrInst.AndInt(requestedTemp, shiftedTemp, maskTemp));
        Emit(new IrInst.JumpIfFalse(requestedTemp, keepLabel));
    }

    // After each store of a part that does not hand its cell's reference over: the stored value is
    // retained when the caller asked for the parts, spliced in right behind the store.
    private void RetainTuplePartStoresWhenRequested(List<IrInst.StoreMemOffset> stores)
    {
        foreach (IrInst.StoreMemOffset store in stores)
        {
            int index = _inst.FindLastIndex(instruction => ReferenceEquals(instruction, store));
            if (index < 0)
            {
                throw new InvalidOperationException("A tuple part's store is no longer in its function.");
            }

            List<IrInst> function = _inst;
            List<IrInst> buffer = [];
            _inst = buffer;
            try
            {
                string skipLabel = NewLabel("tuple_part_not_requested");
                EmitJumpUnlessTuplePartsRequested(skipLabel);
                EmitRetainIfReferenceCounted(store.Source);
                Emit(new IrInst.Label(skipLabel));
            }
            finally
            {
                _inst = function;
            }

            _inst.InsertRange(index + 1, buffer);
            ShiftRecordedInstructionIndices(index, buffer.Count);
        }
    }

    /// <summary>
    /// The owned slots released when the parts are handed over: the ones no other element of the
    /// result's cells reaches (a scalar one reaches nothing), and no part reaches other than by
    /// reading out of an owned value (whose part is retained). <paramref name="retainedStores"/> are
    /// the part stores of a value other than a fresh one, which the cell's reference already hands
    /// over.
    /// </summary>
    private List<GeneralRcOwnedSlot> OwnedSlotsReleasedBehindTupleParts(TypeRef.TTuple tuple, List<int> cells, int[] parts, out List<IrInst.StoreMemOffset> retainedStores)
    {
        retainedStores = [];
        HashSet<int> ownedSlots = [.. _generalRcOwnedSlots.Select(owned => owned.Slot)];
        List<int> pinning = [];
        foreach (IrInst instruction in _inst)
        {
            if (instruction is not IrInst.StoreMemOffset store || !cells.Contains(store.BasePtr))
            {
                continue;
            }

            if (store.OffsetBytes % 8 != 0 || !parts.Contains(store.OffsetBytes / 8))
            {
                if (!HoldsNoPointer(tuple, store.OffsetBytes))
                {
                    pinning.Add(store.Source);
                }

                continue;
            }

            bool fresh = IsNewlyProducedRcTemp(store.Source);
            if (!fresh)
            {
                retainedStores.Add(store);
            }

            if (fresh || !IsReadOutOfOwnedValue(store.Source, ownedSlots, []))
            {
                pinning.Add(store.Source);
            }
        }

        HashSet<int> reached = LocalSlotsReachingTemps(pinning);
        return [.. _generalRcOwnedSlots.Where(owned => !reached.Contains(owned.Slot))];
    }

    // An element of a scalar type (an integer read out of an owned value, say) points into nothing.
    private bool HoldsNoPointer(TypeRef.TTuple tuple, int offset)
        => offset % 8 == 0 && offset >= 0 && offset / 8 < tuple.Elements.Count && CanArenaReset(Prune(tuple.Elements[offset / 8]));

    // The arena cells a result can be: the cell itself, or every cell stored into the locals it is
    // loaded from (the placeholder a back edge writes aside); null when anything else can reach it.
    private List<int>? ArenaCellsOfResult(int resultTemp)
    {
        List<int> cells = [];
        HashSet<int> visited = [];
        Stack<int> pending = new();
        pending.Push(resultTemp);
        while (pending.Count > 0)
        {
            int temp = pending.Pop();
            if (!visited.Add(temp))
            {
                continue;
            }

            switch (DefinitionOf(temp))
            {
                case IrInst.Alloc { RuntimeManaged: false }:
                    cells.Add(temp);
                    break;
                case IrInst.LoadConstInt { Value: 0 }:
                    break;
                case IrInst.LoadLocal load:
                    foreach (IrInst instruction in _inst)
                    {
                        if (instruction is IrInst.StoreLocal store && store.Slot == load.Slot)
                        {
                            pending.Push(store.Source);
                        }
                    }

                    break;
                default:
                    return null;
            }
        }

        return cells;
    }

    // Whether a value is an owned value, a borrowed one, or a part read out of either: a field
    // read through locals, fields and borrows from an owned slot, a parameter or the environment.
    private bool IsReadOutOfOwnedValue(int temp, HashSet<int> ownedSlots, HashSet<int> visited)
    {
        if (!visited.Add(temp))
        {
            return true;
        }

        switch (DefinitionOf(temp))
        {
            case IrInst.LoadEnv:
                return true;
            case IrInst.LoadMemOffset field:
                return IsReadOutOfOwnedValue(field.BasePtr, ownedSlots, visited);
            case IrInst.GetAdtField field:
                return IsReadOutOfOwnedValue(field.Ptr, ownedSlots, visited);
            case IrInst.Borrow borrow:
                return IsReadOutOfOwnedValue(borrow.SourceTemp, ownedSlots, visited);
            case IrInst.LoadLocal load:
                if (ownedSlots.Contains(load.Slot))
                {
                    return true;
                }

                foreach (IrInst instruction in _inst)
                {
                    if (instruction is IrInst.StoreLocal store && store.Slot == load.Slot
                        && !IsReadOutOfOwnedValue(store.Source, ownedSlots, visited))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
}
