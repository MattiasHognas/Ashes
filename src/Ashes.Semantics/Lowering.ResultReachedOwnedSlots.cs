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

    /// <summary>
    /// The local slots a value can have been read from or stored into: following, backwards from the temp, a
    /// slot's every store, a cell's every stored field, and every operand of any other instruction,
    /// and stopping at an instruction whose value carries no pointer into another value (a
    /// constant, a comparison or arithmetic, a string built fresh).
    /// </summary>
    private HashSet<int> LocalSlotsReachingTemp(int temp)
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
        var pending = new Stack<int>();
        pending.Push(temp);
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
