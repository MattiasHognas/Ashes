using Ashes.Frontend;

namespace Ashes.Semantics;

/// <summary>
/// The hand-over of a generic list builder's heads. A function whose result is a list of unresolved
/// element type built from arena cells, each consing a closure call's result onto the empty list or
/// onto the function's own recursive call (`f(head) :: mapGo(tail)`), stores results it cannot
/// release: a closure returning its result owned under the contract leaves a reference in the cell
/// that the caller's copy, which retains the heads again, never balances. A caller that knows the
/// concrete element type asks with the tuple-parts request naming no parts. A builder that is asked
/// forwards the request to its recursive call and makes every head hold a reference of its own (an
/// owned result's moves into the cell, any other reference-counted head is retained), and writes
/// <see cref="ListHeadsHandedOverBit"/> into the caller's mask. The caller copies the list out and
/// then releases the reference each arena cell's reference-counted head holds.
/// </summary>
public sealed partial class Lowering
{
    private const long ListHeadsHandedOverBit = 1L << 60;

    private static bool ListHeadHandOverEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_LISTHEADHANDOVER") is null;

    // An arena cell of a generic list builder: its head is a closure call's result, and its tail
    // either the empty list (no call) or the function's own recursive call.
    private sealed record ListHeadCell(int CellTemp, int HeadTemp, IrInst.CallClosure HeadCall, IrInst.CallClosure? TailCall);

    // The builder cells of the functions being lowered, by label.
    private readonly Dictionary<string, List<ListHeadCell>> _listHeadCells = new(StringComparer.Ordinal);

    // The functions that may hand their list's heads over when asked.
    private readonly HashSet<string> _listHeadHandOverLabels = new(StringComparer.Ordinal);

    // A list of the contract's types whose heads a caller can release behind its copy: a concrete
    // element type the copy retains when it is reference-counted.
    private bool IsListHeadHandOverType(TypeRef type)
        => ListHeadHandOverEnabled
            && CanTestRepresentation
            && Prune(type) is TypeRef.TList list
            && !CanArenaReset(Prune(list.Element))
            && !TryGetRuntimeManagedListHeadCopy(list.Element, out _)
            && !ContainsUnresolvedLayoutType(list, [])
            && IsGeneralRcValueType(list);

    /// <summary>
    /// Records an arena cell consing a closure call's result of unresolved type onto the empty list
    /// or onto the enclosing function's own recursive call.
    /// </summary>
    private void NoteListHeadCell(Expr.Cons cons, LoweredValue head, int tailTemp, int cellTemp)
    {
        if (!ListHeadHandOverEnabled
            || !GeneralRcEnabled
            || !TupleResultPartsEnabled
            || _inCoroutineBody
            || !_loweringFunctionLabels.TryPeek(out string? label)
            || DefinitionOf(cellTemp) is not IrInst.Alloc { RuntimeManaged: false }
            || Prune(head.Type) is not TypeRef.TVar
            || SoleCallThroughLocals(head.Temp) is not { } headCall)
        {
            return;
        }

        IrInst.CallClosure? tailCall = null;
        if (DefinitionOf(tailTemp) is not IrInst.LoadConstInt { Value: 0 })
        {
            if (cons.Tail is not Expr.Call || !IsRecursiveProducerResult(cons.Tail) || SoleCallThroughLocals(tailTemp) is not { } call)
            {
                return;
            }

            tailCall = call;
        }

        if (!_listHeadCells.TryGetValue(label, out List<ListHeadCell>? cells))
        {
            cells = [];
            _listHeadCells[label] = cells;
        }

        cells.Add(new ListHeadCell(cellTemp, head.Temp, headCall, tailCall));
    }

    /// <summary>
    /// The return of a generic list builder every cell of whose result is one of its builder cells:
    /// when the caller asks, the recursive calls are asked on its behalf, each head holds a
    /// reference of its own, and the mask says so. Returns the temp to return.
    /// </summary>
    private int HandOverListBuilderHeads(string label, int resultTemp, TypeRef resultType)
    {
        TryHandOverListBuilderHeads(label, resultTemp, resultType);
        return resultTemp;
    }

    private void TryHandOverListBuilderHeads(string label, int resultTemp, TypeRef resultType)
    {
        if (!_listHeadCells.Remove(label, out List<ListHeadCell>? noted)
            || Prune(resultType) is not TypeRef.TList { Element: var element }
            || Prune(element) is not TypeRef.TVar
            || ArenaCellsOfResult(resultTemp) is not { Count: > 0 } cells)
        {
            return;
        }

        List<ListHeadCell> builders = [.. noted.Where(cell => cells.Contains(cell.CellTemp))];
        if (builders.Count != cells.Distinct().Count()
            || builders.Any(cell => cell.TailCall is { } call
                && (_tupleResultPartsRequests.ContainsKey(call) || !_inst.Any(instruction => ReferenceEquals(instruction, call)))))
        {
            return;
        }

        foreach (ListHeadCell cell in builders)
        {
            if (cell.TailCall is { } call)
            {
                _ = ForwardCallerRequestIntoCall(call);
            }
        }

        foreach (ListHeadCell cell in builders)
        {
            OwnListHeadWhenRequested(cell);
        }

        string skipLabel = NewLabel("list_heads_not_requested");
        EmitJumpUnlessTuplePartsRequested(skipLabel);
        EmitResultHandOverMask(ListHeadsHandedOverBit);
        Emit(new IrInst.Label(skipLabel));
        _listHeadHandOverLabels.Add(label);
        _tupleResultPartLabels.TryAdd(label, []);
    }

    // Right behind the cell's head store: under the request, a reference-counted head the closure
    // did not return owned is retained, so the cell holds a reference of its own either way.
    private void OwnListHeadWhenRequested(ListHeadCell cell)
    {
        int headOffset = HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex);
        int index = _inst.FindLastIndex(instruction => instruction is IrInst.StoreMemOffset store
            && store.BasePtr == cell.CellTemp
            && store.OffsetBytes == headOffset
            && store.Source == cell.HeadTemp);
        if (index < 0)
        {
            throw new InvalidOperationException("A list builder's head store is no longer in its function.");
        }

        List<IrInst> function = _inst;
        List<IrInst> buffer = [];
        _inst = buffer;
        try
        {
            string skipLabel = NewLabel("list_head_not_requested");
            string retainLabel = NewLabel("list_head_borrowed");
            EmitJumpUnlessTuplePartsRequested(skipLabel);
            EmitSkipUnlessReferenceCounted(cell.HeadTemp, skipLabel);
            int ownedTemp = EmitClosureReturnsGeneralRcOwnedFlag(cell.HeadCall.ClosureTemp);
            Emit(new IrInst.JumpIfFalse(ownedTemp, retainLabel));
            Emit(new IrInst.Jump(skipLabel));
            Emit(new IrInst.Label(retainLabel));
            Emit(new IrInst.RcDup(NewTemp(), cell.HeadTemp, RuntimeManaged: true));
            Emit(new IrInst.Label(skipLabel));
        }
        finally
        {
            _inst = function;
        }

        _inst.InsertRange(index + 1, buffer);
        ShiftRecordedInstructionIndices(index, buffer.Count);
    }

    /// <summary>
    /// After a call's list result is copied out of the arena: when the callee's mask says it handed
    /// the heads over, the reference each arena cell's reference-counted head holds is released, up
    /// to the first reference-counted cell, from which on the copy shares the list.
    /// </summary>
    private void ReleaseHandedOverListHeads(TypeRef resultType, int maskTemp, int resultTemp)
    {
        if (maskTemp < 0 || !IsListHeadHandOverType(resultType) || Prune(resultType) is not TypeRef.TList list)
        {
            return;
        }

        string doneLabel = NewLabel("list_heads_kept");
        int bitTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(bitTemp, ListHeadsHandedOverBit));
        int maskedTemp = NewTemp();
        Emit(new IrInst.AndInt(maskedTemp, maskTemp, bitTemp));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int handedTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(handedTemp, maskedTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(handedTemp, doneLabel));
        int cursorSlot = NewLocal();
        Emit(new IrInst.StoreLocal(cursorSlot, resultTemp));
        string loopLabel = NewLabel("list_heads_release");
        Emit(new IrInst.Label(loopLabel));
        int cellTemp = NewTemp();
        Emit(new IrInst.LoadLocal(cellTemp, cursorSlot));
        EmitSkipUnlessPresent(cellTemp, doneLabel);
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, cellTemp));
        string arenaLabel = NewLabel("list_heads_arena_cell");
        Emit(new IrInst.JumpIfFalse(countedTemp, arenaLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(arenaLabel));
        int headTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(headTemp, cellTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        string nextLabel = NewLabel("list_heads_next");
        EmitSkipUnlessReferenceCounted(headTemp, nextLabel);
        EmitRuntimeManagedChildDrop(headTemp, list.Element);
        Emit(new IrInst.Label(nextLabel));
        int tailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(tailTemp, cellTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        Emit(new IrInst.StoreLocal(cursorSlot, tailTemp));
        Emit(new IrInst.Jump(loopLabel));
        Emit(new IrInst.Label(doneLabel));
    }
}
