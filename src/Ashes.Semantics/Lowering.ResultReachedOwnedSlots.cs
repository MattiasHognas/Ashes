namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool ResultReachedOwnedSlotsEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_RESULTREACHEDSLOTS") is null;

    /// <summary>
    /// The owned slots of <paramref name="ownedSlots"/> a function's result cannot point into: no
    /// value that can flow into the result is read from them. A result outside the contract's types
    /// that holds no part of an owned value leaves that value to be released at the return.
    /// </summary>
    private List<GeneralRcOwnedSlot> OwnedSlotsOutOfResultReach(int resultTemp, List<GeneralRcOwnedSlot> ownedSlots)
    {
        if (!ResultReachedOwnedSlotsEnabled || ownedSlots.Count == 0)
        {
            return [];
        }

        HashSet<int> reached = LocalSlotsReachingTemp(resultTemp);
        return [.. ownedSlots.Where(owned => !reached.Contains(owned.Slot))];
    }

    private static bool OwnedTupleResultEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_OWNEDTUPLERESULT") is null;

    /// <summary>
    /// Whether a tuple result that holds a part of an owned value is placed on the
    /// reference-counted heap at the return: each element the owned values hold is retained into
    /// it, the others copied, so the owned slots are released behind it and their parts live on in
    /// the result, as a move. A tuple a runtime-managed parent can own outright qualifies; one with
    /// an element of unresolved layout does not.
    /// </summary>
    private bool MovesOwnedPartsIntoTupleResult(int bodyTemp, TypeRef resultType)
        => OwnedTupleResultEnabled
            && ResultReachedOwnedSlotsEnabled
            && _generalRcOwnedSlots.Count > 0
            && resultType is TypeRef.TTuple tuple
            && !IsRuntimeManagedResultTemp(bodyTemp)
            && !ContainsUnresolvedLayoutType(tuple, [])
            && CanRuntimeManageOwnedTupleType(tuple)
            && OwnedSlotsOutOfResultReach(bodyTemp, _generalRcOwnedSlots).Count < _generalRcOwnedSlots.Count;

    private static bool ArenaTupleChildMoveEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_ARENATUPLECHILDMOVE") is null;

    /// <summary>
    /// A result built as an arena tuple whose layout was unresolved when it was built (a phantom
    /// element read as <c>Never</c> only at the return) holds the references of the
    /// reference-counted values freshly produced into it: the arena cell took them and can release
    /// nothing. Once the tuple is copied onto the reference-counted heap, which takes references of
    /// its own, those references are released, so the fresh values move into the copy.
    /// </summary>
    private void ReleaseFreshChildrenOfCopiedArenaTuple(int bodyTemp, TypeRef type)
    {
        if (!ArenaTupleChildMoveEnabled || Prune(type) is not TypeRef.TTuple tuple)
        {
            return;
        }

        int cell = SoleArenaCellOf(bodyTemp);
        if (cell < 0)
        {
            return;
        }

        foreach (IrInst instruction in _inst.ToList())
        {
            if (instruction is not IrInst.StoreMemOffset { BasePtr: var basePtr, OffsetBytes: var offset, Source: var source }
                || basePtr != cell
                || offset % 8 != 0
                || offset / 8 >= tuple.Elements.Count)
            {
                continue;
            }

            TypeRef elementType = Prune(tuple.Elements[offset / 8]);
            if (CanArenaReset(elementType) || !IsNewlyProducedRcTemp(source))
            {
                continue;
            }

            int childTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(childTemp, bodyTemp, offset));
            EmitRuntimeManagedChildDrop(childTemp, elementType);
        }
    }

    // The arena cell a value is, directly or through the one store into a local that is not the
    // placeholder a back edge writes; -1 otherwise.
    private int SoleArenaCellOf(int temp)
    {
        IrInst? definition = DefinitionOf(temp);
        if (definition is IrInst.Alloc { RuntimeManaged: false })
        {
            return temp;
        }

        if (definition is not IrInst.LoadLocal load)
        {
            return -1;
        }

        int cell = -1;
        foreach (IrInst instruction in _inst)
        {
            if (instruction is not IrInst.StoreLocal store || store.Slot != load.Slot)
            {
                continue;
            }

            IrInst? stored = DefinitionOf(store.Source);
            if (stored is IrInst.LoadConstInt { Value: 0 })
            {
                continue;
            }

            if (stored is not IrInst.Alloc { RuntimeManaged: false } || cell >= 0)
            {
                return -1;
            }

            cell = store.Source;
        }

        return cell;
    }

    private IrInst? DefinitionOf(int temp)
    {
        for (int i = _inst.Count - 1; i >= 0; i--)
        {
            if (StateMachineTransform.GetDefinedTemps(_inst[i]).Contains(temp))
            {
                return _inst[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The local slots a value can have been read from or stored into: following, backwards from the temp, a
    /// slot's every store, a cell's every stored field, and every operand of any other instruction,
    /// and stopping at an instruction whose value carries no pointer into another value (a
    /// constant, a comparison or arithmetic, a string built fresh).
    /// </summary>
    private HashSet<int> LocalSlotsReachingTemp(int temp) => LocalSlotsReachingTemps([temp]);

    private HashSet<int> LocalSlotsReachingTemps(IEnumerable<int> temps)
    {
        var definitions = new Dictionary<int, List<IrInst>>();
        var slotStores = new Dictionary<int, List<int>>();
        var cellStores = new Dictionary<int, List<int>>();
        foreach (IrInst instruction in _inst)
        {
            IndexValueFlow(instruction, definitions, slotStores, cellStores);
        }

        var slots = new HashSet<int>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>(temps);
        while (pending.Count > 0)
        {
            int current = pending.Pop();
            if (!visited.Add(current) || !definitions.TryGetValue(current, out List<IrInst>? defining))
            {
                continue;
            }

            foreach (IrInst definition in defining)
            {
                foreach (int source in ValueFlowSources(definition, current, slots, slotStores, cellStores))
                {
                    pending.Push(source);
                }
            }
        }

        // A slot holding one of the values found is reached too, through whichever copy of the
        // pointer the result was built from.
        foreach ((int slot, List<int> sources) in slotStores)
        {
            if (sources.Exists(visited.Contains))
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    private static void IndexValueFlow(
        IrInst instruction,
        Dictionary<int, List<IrInst>> definitions,
        Dictionary<int, List<int>> slotStores,
        Dictionary<int, List<int>> cellStores)
    {
        switch (instruction)
        {
            case IrInst.StoreLocal store:
                AddValueFlow(slotStores, store.Slot, store.Source);
                break;
            case IrInst.StoreMemOffset store:
                AddValueFlow(cellStores, store.BasePtr, store.Source);
                break;
            case IrInst.SetAdtField field:
                AddValueFlow(cellStores, field.Ptr, field.Source);
                break;
        }

        foreach (int defined in StateMachineTransform.GetDefinedTemps(instruction))
        {
            if (!definitions.TryGetValue(defined, out List<IrInst>? list))
            {
                list = [];
                definitions[defined] = list;
            }

            list.Add(instruction);
        }
    }

    private static void AddValueFlow(Dictionary<int, List<int>> flows, int key, int source)
    {
        if (!flows.TryGetValue(key, out List<int>? sources))
        {
            sources = [];
            flows[key] = sources;
        }

        sources.Add(source);
    }

    private static IEnumerable<int> ValueFlowSources(
        IrInst definition,
        int temp,
        HashSet<int> slots,
        Dictionary<int, List<int>> slotStores,
        Dictionary<int, List<int>> cellStores)
    {
        switch (definition)
        {
            case IrInst.LoadLocal load:
                slots.Add(load.Slot);
                return slotStores.GetValueOrDefault(load.Slot) ?? [];
            case IrInst.Alloc or IrInst.AllocAdt or IrInst.AllocAdtStack or IrInst.AllocStack:
                return cellStores.GetValueOrDefault(temp) ?? [];
            default:
                return CarriesNoPointer(definition) ? [] : StateMachineTransform.GetUsedTemps(definition);
        }
    }

    // An instruction whose value holds no pointer into any value it reads: a constant, a
    // comparison, arithmetic, a length, or a string it builds fresh.
    private static bool CarriesNoPointer(IrInst instruction) => instruction is
        IrInst.LoadConstInt or IrInst.LoadConstFloat or IrInst.LoadConstBool or IrInst.LoadConstStr
        or IrInst.AddInt or IrInst.SubInt or IrInst.MulInt or IrInst.DivInt or IrInst.DivUInt
        or IrInst.AndInt or IrInst.OrInt or IrInst.XorInt or IrInst.ShlInt or IrInst.ShrInt
        or IrInst.AddFloat or IrInst.SubFloat or IrInst.MulFloat or IrInst.DivFloat
        or IrInst.CmpIntGt or IrInst.CmpIntGe or IrInst.CmpIntLt or IrInst.CmpIntLe
        or IrInst.CmpUIntGt or IrInst.CmpUIntGe or IrInst.CmpUIntLt or IrInst.CmpUIntLe
        or IrInst.CmpIntEq or IrInst.CmpIntNe
        or IrInst.CmpFloatGt or IrInst.CmpFloatGe or IrInst.CmpFloatLt or IrInst.CmpFloatLe
        or IrInst.CmpFloatEq or IrInst.CmpFloatNe
        or IrInst.CmpStrEq or IrInst.CmpStrNe or IrInst.IntToFloat or IrInst.FloatToInt
        or IrInst.ConcatStr or IrInst.ConcatStrN or IrInst.TextByteLength or IrInst.TextFromInt
        or IrInst.TextFromFloat or IrInst.TextFormatFloat or IrInst.TextToHex
        or IrInst.IsReferenceCounted or IrInst.RcIsUnique or IrInst.GetAdtTag or IrInst.BytesLength
        or IrInst.LoadArgumentOwnership or IrInst.MonotonicMillis;
}
