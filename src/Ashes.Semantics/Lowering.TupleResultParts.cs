using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// The hand-over of a generic tuple result's contract parts. A tuple result with an element of
/// unresolved layout (a <c>List(a)</c> beside a contract value) can only be an arena cell, so the
/// function cannot release the owned values its contract elements point into, and a caller that
/// copies the tuple out retains those elements again. A caller that knows the concrete type asks
/// for the parts through bit 2 of the hidden ownership word, whose bits from 3 up carry the address
/// of an arena word the caller cleared; the callee then gives each contract element of an arena
/// result a reference of its own (a move of an owned value, a retain of a borrowed one), releases
/// its owned values behind them, and writes the mask of the parts it handed over into that word.
/// The caller releases the parts the mask names once its copy holds its own references, so it can
/// ask any callee, one that does not honor the request included.
/// </summary>
public sealed partial class Lowering
{
    private const long TupleResultPartsRequestBit = 4;

    // The bits of the ownership word a function forwards: the request and the caller's mask address.
    private const long TupleResultPartsForwardedBits = -4;

    // The bits of the ownership word that address the caller's mask.
    private const long TupleResultPartsMaskAddressBits = -8;

    private static bool TupleResultPartsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_TUPLERESULTPARTS") is null;

    private static bool TupleResultPartsSignalEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_TUPLEPARTSSIGNAL") is null;

    /// <summary>
    /// A call's request for its tuple result's parts: the request constant (zeroed to withdraw it),
    /// the request word joining it to the mask address, the arena word the callee writes the mask
    /// of the handed-over parts into, and the parts asked for.
    /// </summary>
    private sealed record TupleResultPartsRequest(int ConstantTemp, int WordTemp, int MaskCellTemp, int[] Parts);

    // The functions that may hand a requested tuple result's parts over (or forward the request), by
    // label: the element indices whose references the function's own body hands over.
    private readonly Dictionary<string, int[]> _tupleResultPartLabels = new(StringComparer.Ordinal);

    // The calls that asked for their result's parts, until the request is taken or withdrawn.
    private readonly Dictionary<IrInst.CallClosure, TupleResultPartsRequest> _tupleResultPartsRequests =
        new(ReferenceEqualityComparer.Instance);

    // The withdrawn requests: a function returning that call's result as it is forwards its own
    // caller's request in their place.
    private readonly Dictionary<IrInst.CallClosure, TupleResultPartsRequest> _withdrawnTupleResultPartsRequests =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The parts a call asks its callee for, or null: the contract elements of the call's tuple
    /// result type (the callee's own parts when that type is not yet a tuple). A call asks a known
    /// function that may hand them over, and any callee it cannot name, since the mask the callee
    /// writes says what it handed over.
    /// </summary>
    private int[]? CalleeTupleResultParts(Expr rootExpr, int argumentCount, TypeRef resultType)
    {
        if (!TupleResultPartsEnabled || argumentCount == 0)
        {
            return null;
        }

        int[] candidates = ContractElementParts(resultType);
        bool listHeads = IsListHeadHandOverType(resultType);
        if (!TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return UnknownCalleeTupleResultParts(candidates, listHeads);
        }

        for (int i = 1; i < argumentCount; i++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                return UnknownCalleeTupleResultParts(candidates, listHeads);
            }

            label = nextLabel;
        }

        if (!_tupleResultPartLabels.TryGetValue(label, out int[]? calleeParts))
        {
            return null;
        }

        int[] parts = candidates.Length > 0 ? candidates : calleeParts;
        return parts.Length > 0
            || (_wholeResultHandOverLabels.Contains(label) && IsGeneralRcValueType(resultType))
            || (listHeads && _listHeadHandOverLabels.Contains(label))
            ? parts
            : null;
    }

    private static int[]? UnknownCalleeTupleResultParts(int[] candidates, bool listHeads)
        => TupleResultPartsSignalEnabled && (candidates.Length > 0 || listHeads) ? candidates : null;

    // The contract elements of a tuple type, by index.
    private int[] ContractElementParts(TypeRef type)
        => Prune(type) is TypeRef.TTuple tuple
            ? [.. Enumerable.Range(0, tuple.Elements.Count).Where(index => IsGeneralRcValueType(tuple.Elements[index]))]
            : [];

    // The hidden ownership word with the tuple-parts request beside whatever the call already
    // passes: a cleared arena word for the callee's mask, its address joined to the request
    // constant, and that request word on the left so a withdrawal of an arena-result request still
    // follows the right operand to its own constant.
    private int EmitTupleResultPartsRequestWord(int ownershipWordTemp, int[] parts, out TupleResultPartsRequest request)
    {
        int maskCellTemp = NewTemp();
        Emit(new IrInst.Alloc(maskCellTemp, 8));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.StoreMemOffset(maskCellTemp, 0, zeroTemp));
        int constantTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(constantTemp, TupleResultPartsRequestBit));
        int requestWordTemp = NewTemp();
        Emit(new IrInst.OrInt(requestWordTemp, constantTemp, maskCellTemp));
        request = new TupleResultPartsRequest(constantTemp, requestWordTemp, maskCellTemp, parts);
        if (ownershipWordTemp < 0)
        {
            return requestWordTemp;
        }

        int wordTemp = NewTemp();
        Emit(new IrInst.OrInt(wordTemp, requestWordTemp, ownershipWordTemp));
        return wordTemp;
    }

    /// <summary>
    /// The request of the call producing <paramref name="resultTemp"/>, taken off the pending
    /// requests; null when it asked for none. A <paramref name="withdraw"/>n request's constant is
    /// cleared in the call's ownership word, so the callee keeps its own references.
    /// </summary>
    private TupleResultPartsRequest? TakeTupleResultPartsRequest(int resultTemp, bool withdraw)
    {
        for (int i = _inst.Count - 1; i >= 0 && i >= _inst.Count - 64; i--)
        {
            if (_inst[i] is not IrInst.CallClosure call || call.Target != resultTemp)
            {
                continue;
            }

            if (!_tupleResultPartsRequests.Remove(call, out TupleResultPartsRequest? request))
            {
                return null;
            }

            if (!withdraw)
            {
                return request;
            }

            _withdrawnTupleResultPartsRequests[call] = request;
            for (int j = i - 1; j >= 0 && j >= i - 64; j--)
            {
                if (_inst[j] is IrInst.LoadConstInt constant && constant.Target == request.ConstantTemp)
                {
                    _inst[j] = constant with { Value = 0 };
                    break;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// The mask the callee wrote and the parts of a call's tuple result, read before the result is
    /// copied out; the mask temp is -1 when the call asked for nothing.
    /// </summary>
    private (int MaskTemp, int[] PartTemps) LoadHandedOverTupleParts(int resultTemp, TupleResultPartsRequest? request)
    {
        if (request is null)
        {
            return (-1, []);
        }

        int maskTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(maskTemp, request.MaskCellTemp, 0));
        int[] partTemps = new int[request.Parts.Length];
        for (int i = 0; i < request.Parts.Length; i++)
        {
            partTemps[i] = NewTemp();
            Emit(new IrInst.LoadMemOffset(partTemps[i], resultTemp, request.Parts[i] * 8));
        }

        return (maskTemp, partTemps);
    }

    /// <summary>
    /// After a call's tuple result is copied out of the arena: the references the callee handed
    /// over with the parts its mask names are released, the copy holding its own.
    /// </summary>
    private void ReleaseHandedOverTupleParts(TypeRef resultType, TupleResultPartsRequest? request, (int MaskTemp, int[] PartTemps) loaded)
    {
        if (request is null || Prune(resultType) is not TypeRef.TTuple tuple)
        {
            return;
        }

        for (int i = 0; i < request.Parts.Length; i++)
        {
            int part = request.Parts[i];
            string skipLabel = NewLabel("tuple_part_kept");
            int shiftTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(shiftTemp, part));
            int shiftedTemp = NewTemp();
            Emit(new IrInst.ShrInt(shiftedTemp, loaded.MaskTemp, shiftTemp));
            int oneTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(oneTemp, 1));
            int handedTemp = NewTemp();
            Emit(new IrInst.AndInt(handedTemp, shiftedTemp, oneTemp));
            Emit(new IrInst.JumpIfFalse(handedTemp, skipLabel));
            EmitSkipUnlessReferenceCounted(loaded.PartTemps[i], skipLabel);
            EmitRuntimeManagedChildDrop(loaded.PartTemps[i], tuple.Elements[part]);
            Emit(new IrInst.Label(skipLabel));
        }
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
            ForwardTupleResultPartsRequestThroughGenericResult(label, resultTemp, resultType);
            return false;
        }

        List<GeneralRcOwnedSlot> kept = OwnedSlotsOutOfResultReach(resultTemp, resultType, _generalRcOwnedSlots);
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
            List<int> partTemps = [];
            foreach (int part in parts)
            {
                int childTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(childTemp, resultTemp, part * 8));
                EmitRetainIfReferenceCounted(childTemp);
                partTemps.Add(childTemp);
            }

            EmitOwnedSlotDropsBehindRetainedParts([.. _generalRcOwnedSlots.Except(kept)], partTemps);
        }

        EmitTupleResultPartsMask(parts);
        EmitGeneralRcOwnedSlotDrops(handedOver);
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(keepLabel));
        EmitGeneralRcOwnedSlotDrops(kept);
        Emit(new IrInst.Label(doneLabel));
        _tupleResultPartLabels[label] = parts;
        return true;
    }

    // A part retained at the return that is the very value an owned slot holds: the slot's reference
    // is released behind it, so the retain hands that reference over rather than adding one the
    // function never releases. An owned value a part only reads out of keeps its reference.
    private void EmitOwnedSlotDropsBehindRetainedParts(List<GeneralRcOwnedSlot> ownedSlots, List<int> partTemps)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_PARTOWNERMOVE") is not null)
        {
            return;
        }

        foreach (GeneralRcOwnedSlot owned in ownedSlots)
        {
            string nextLabel = NewLabel("tuple_part_owner_kept");
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, owned.Slot));
            EmitSkipUnlessReferenceCounted(valueTemp, nextLabel);
            string dropLabel = NewLabel("tuple_part_owner_moved");
            foreach (int partTemp in partTemps)
            {
                string otherLabel = NewLabel("tuple_part_owner_other");
                int sameTemp = NewTemp();
                Emit(new IrInst.CmpIntEq(sameTemp, valueTemp, partTemp));
                Emit(new IrInst.JumpIfFalse(sameTemp, otherLabel));
                Emit(new IrInst.Jump(dropLabel));
                Emit(new IrInst.Label(otherLabel));
            }

            Emit(new IrInst.Jump(nextLabel));
            Emit(new IrInst.Label(dropLabel));
            EmitRuntimeManagedChildDrop(valueTemp, owned.Type);
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            Emit(new IrInst.StoreLocal(owned.Slot, zeroTemp));
            Emit(new IrInst.Label(nextLabel));
        }
    }

    private static bool ForwardedTupleResultPartsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_FORWARDEDTUPLEPARTS") is null;

    /// <summary>
    /// A result that is another function's generic tuple result, returned as it is: the call's
    /// withdrawn request for the same parts is replaced by this function's own caller's request
    /// (bit 2 of its ownership word and the mask address above it), so the callee hands the parts
    /// over to that caller directly and this function takes none. False when the result is no such
    /// call's.
    /// </summary>
    private bool TryForwardTupleResultPartsRequest(int resultTemp, int[] parts)
    {
        IrInst.CallClosure? call = SoleCallThroughLocals(resultTemp);
        if (!ForwardedTupleResultPartsEnabled
            || call is null
            || !_withdrawnTupleResultPartsRequests.Remove(call, out TupleResultPartsRequest? request)
            || !request.Parts.SequenceEqual(parts))
        {
            return false;
        }

        int callIndex = _inst.FindLastIndex(instruction => ReferenceEquals(instruction, call));
        int index = callIndex < 0 ? -1 : _inst.FindLastIndex(callIndex, instruction => instruction is IrInst.OrInt word && word.Target == request.WordTemp);
        if (index < 0)
        {
            return false;
        }

        IrInst[] forwarded = ForwardedTupleResultPartsRequest(request.WordTemp);
        _inst.RemoveAt(index);
        _inst.InsertRange(index, forwarded);
        ShiftRecordedInstructionIndices(index, forwarded.Length - 1);
        return true;
    }

    // This function's own caller's request and mask address, read out of its ownership word.
    private IrInst[] ForwardedTupleResultPartsRequest(int target)
    {
        int wordTemp = NewTemp();
        int bitsTemp = NewTemp();
        return
        [
            new IrInst.LoadArgumentOwnership(wordTemp),
            new IrInst.LoadConstInt(bitsTemp, TupleResultPartsForwardedBits),
            new IrInst.AndInt(target, wordTemp, bitsTemp),
        ];
    }

    /// <summary>
    /// A result of unresolved type that is a call's result returned as it is (a generic function
    /// applying a closure parameter, say): its own caller may have asked for a tuple's parts, so the
    /// request and mask address are forwarded to that call beside whatever its word already passes.
    /// Whether the callee hands anything over is the mask's to say, so any callee may be asked.
    /// </summary>
    private void ForwardTupleResultPartsRequestThroughGenericResult(string label, int resultTemp, TypeRef resultType)
    {
        _forwardedResultCallTemp = -1;
        if (!TupleResultPartsEnabled
            || !TupleResultPartsSignalEnabled
            || !GeneralRcEnabled
            || _inCoroutineBody
            || !IsGenericResultForwardedAsIs(resultType)
            || SoleCallThroughLocals(resultTemp) is not { } call
            || _tupleResultPartsRequests.ContainsKey(call))
        {
            return;
        }

        if (_withdrawnTupleResultPartsRequests.TryGetValue(call, out TupleResultPartsRequest? withdrawn))
        {
            if (WholeResultHandOverEnabled && TryForwardTupleResultPartsRequest(resultTemp, withdrawn.Parts))
            {
                NoteResultRequestForwarder(label);
                _forwardedResultCallTemp = call.Target;
            }

            return;
        }

        if (ForwardCallerRequestIntoCall(call))
        {
            NoteResultRequestForwarder(label);
            _forwardedResultCallTemp = call.Target;
        }
    }

    // This function's own caller's request and mask address joined to whatever the call's word
    // already passes, so the callee answers that caller. False when the call is no longer emitted.
    private bool ForwardCallerRequestIntoCall(IrInst.CallClosure call)
    {
        int callIndex = _inst.FindLastIndex(instruction => ReferenceEquals(instruction, call));
        if (callIndex < 0)
        {
            return false;
        }

        int requestTemp = NewTemp();
        List<IrInst> forwarded = [.. ForwardedTupleResultPartsRequest(requestTemp)];
        int wordTemp = requestTemp;
        if (call.RuntimeManagedArgumentFlagTemp >= 0)
        {
            wordTemp = NewTemp();
            forwarded.Add(new IrInst.OrInt(wordTemp, requestTemp, call.RuntimeManagedArgumentFlagTemp));
        }

        IrInst.CallClosure forwardingCall = call with { RuntimeManagedArgumentFlagTemp = wordTemp };
        _inst[callIndex] = forwardingCall;
        if (_borrowedArgumentCalls.Remove(call))
        {
            _borrowedArgumentCalls.Add(forwardingCall);
        }

        if (_arenaResultRequestingCalls.Remove(call))
        {
            _arenaResultRequestingCalls.Add(forwardingCall);
        }

        _inst.InsertRange(callIndex, forwarded);
        ShiftRecordedInstructionIndices(callIndex - 1, forwarded.Count);
        return true;
    }

    // A result of unresolved type, or of a generic named type outside the contract's types (a
    // `Result(e, T)` with `e` unresolved), whose caller may know a concrete type to ask for.
    private bool IsGenericResultForwardedAsIs(TypeRef resultType)
        => Prune(resultType) is TypeRef.TVar
            || (WholeResultHandOverEnabled
                && Prune(resultType) is TypeRef.TNamedType named
                && ContainsUnresolvedLayoutType(named, [])
                && !IsGeneralRcValueType(named));

    // A function forwarding its caller's request: callers ask it as they ask the function it calls.
    private void NoteResultRequestForwarder(string label)
    {
        _tupleResultPartLabels.TryAdd(label, []);
        if (WholeResultHandOverEnabled)
        {
            _wholeResultHandOverLabels.Add(label);
        }
    }

    // On the hand-over path: the mask of the parts handed over, written into the caller's word.
    private void EmitTupleResultPartsMask(int[] parts)
    {
        long mask = 0;
        foreach (int part in parts)
        {
            mask |= 1L << part;
        }

        EmitResultHandOverMask(mask);
    }

    // The mask of what was handed over, written into the word the caller's request addresses.
    private void EmitResultHandOverMask(long mask)
    {
        int wordTemp = NewTemp();
        Emit(new IrInst.LoadArgumentOwnership(wordTemp));
        int addressBitsTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(addressBitsTemp, TupleResultPartsMaskAddressBits));
        int addressTemp = NewTemp();
        Emit(new IrInst.AndInt(addressTemp, wordTemp, addressBitsTemp));
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, mask));
        Emit(new IrInst.StoreMemOffset(addressTemp, 0, maskTemp));
    }

    // The call whose result a temp is, through locals stored once and borrows (a match's fallback
    // storing no value aside); null when it may be another value.
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
                    if (IndependentResultSignalEnabled && sources.Count > 1)
                    {
                        sources.RemoveAll(source => DefinitionOf(source) is IrInst.LoadConstInt { Value: 0 });
                    }

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
