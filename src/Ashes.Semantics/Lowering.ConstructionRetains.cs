using Ashes.Frontend;

namespace Ashes.Semantics;

// An arena cell built for a loop's successor or result holds retains its construction made for the
// loop-parameter reads it stores (`RetainRuntimeManagedTcoConstructorArguments`,
// `DuplicateTcoParameterReadForAggregate`). Each retain is balanced by one release or by none:
//
// - A successor whose copy releases the dying cell's children (a record literal built at the
//   self-call) releases exactly the children the construction retained. A child the construction
//   only borrowed (a retain marker finalization left an identity, say a pattern binding matched
//   out of a parameter kept in the arena) is not released.
// - A cell a normalizer copies instead (a record update successor, a let-bound successor, a loop's
//   result), or one built only to be matched, borrows its children: the copy takes references of
//   its own before the loop parameter is released, so the construction's retain of a parameter
//   read is withdrawn, provided the cell holds that retain's only reference and no path releases
//   the parameter earlier.
//
// The retains are read back from the instructions that built the cell, which by the time a back
// edge or a function's result is emitted carry their final, finalized form.
public sealed partial class Lowering
{
    /// <summary>
    /// The index of the last instruction of the function being lowered that <paramref name="matches"/>,
    /// or -1.
    /// </summary>
    private int FindLastInstruction(Func<IrInst, bool> matches)
    {
        for (int index = _inst.Count - 1; index >= 0; index--)
        {
            if (matches(_inst[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The temp the construction of <paramref name="cellTemp"/> stored into field
    /// <paramref name="fieldIndex"/>, when that cell was built by this function; otherwise null.
    /// </summary>
    private int? ConstructionFieldSource(int cellTemp, int fieldIndex)
    {
        int index = FindLastInstruction(instruction =>
            instruction is IrInst.SetAdtField field && field.Ptr == cellTemp && field.FieldIndex == fieldIndex);
        return index >= 0 ? ((IrInst.SetAdtField)_inst[index]).Source : null;
    }

    /// <summary>
    /// The temp the construction of the list or tuple cell <paramref name="cellTemp"/> stored at
    /// <paramref name="offsetBytes"/>, when that cell was built by this function; otherwise null.
    /// </summary>
    private int? ConstructionWordSource(int cellTemp, int offsetBytes)
    {
        int index = FindLastInstruction(instruction =>
            instruction is IrInst.StoreMemOffset store && store.BasePtr == cellTemp && store.OffsetBytes == offsetBytes);
        return index >= 0 ? ((IrInst.StoreMemOffset)_inst[index]).Source : null;
    }

    /// <summary>
    /// True when <paramref name="storedTemp"/> is a retain the construction emitted that finalization
    /// left without effect: a retain marker never made real, or a pending field retain whose flag
    /// was zeroed. The cell only borrows such a child.
    /// </summary>
    private bool IsUnrealizedConstructionRetain(int storedTemp)
    {
        int index = FindLastInstruction(instruction => instruction switch
        {
            IrInst.RcDup duplicate => duplicate.Target == storedTemp,
            IrInst.LoadLocal load => load.Target == storedTemp,
            _ => false,
        });
        return index >= 0 && _inst[index] switch
        {
            IrInst.RcDup duplicate => !duplicate.RuntimeManaged,
            IrInst.LoadLocal => PendingFieldRetainFlagIndex(index) is int flagIndex
                && _inst[flagIndex] is IrInst.LoadConstInt { Value: 0 },
            _ => false,
        };
    }

    /// <summary>
    /// For the reload at <paramref name="reloadIndex"/> that ends a pending constructor field retain
    /// (<see cref="EmitPendingRuntimeManagedConstructorFieldRetain"/>), the index of the instruction
    /// defining its flag; otherwise null.
    /// </summary>
    private int? PendingFieldRetainFlagIndex(int reloadIndex)
    {
        if (reloadIndex < 6
            || _inst[reloadIndex] is not IrInst.LoadLocal reload
            || _inst[reloadIndex - 1] is not IrInst.Label done
            || _inst[reloadIndex - 2] is not IrInst.StoreLocal retainedStore
            || _inst[reloadIndex - 3] is not IrInst.RcDup { RuntimeManaged: true } retain
            || _inst[reloadIndex - 4] is not IrInst.JumpIfFalse skip
            || _inst[reloadIndex - 5] is not IrInst.StoreLocal borrowedStore
            || _inst[reloadIndex - 6] is not IrInst.LoadConstInt flag
            || retainedStore.Slot != reload.Slot
            || borrowedStore.Slot != reload.Slot
            || retainedStore.Source != retain.Target
            || !string.Equals(skip.Target, done.Name, StringComparison.Ordinal)
            || skip.CondTemp != flag.Target)
        {
            return null;
        }

        return reloadIndex - 6;
    }

    /// <summary>
    /// Withdraws the retains of loop-parameter reads that the arena cells reaching
    /// <paramref name="valueTemp"/> were built with, for a copy that borrows those cells' children:
    /// a normalizer's copy takes its own reference to every reference-counted child before the loop
    /// releases its parameters. Each of <paramref name="parameters"/> is a parameter slot with its
    /// active flag, or -1. A retain is withdrawn only when the cell holds its sole reference and the
    /// parameter it reads is not released ahead of the loop's end.
    /// </summary>
    private void WithdrawLoopParameterRetainsOfCopiedCells(int valueTemp, IReadOnlyCollection<(int Slot, int ActiveSlot)> parameters)
    {
        if (parameters.Count == 0 || Environment.GetEnvironmentVariable("GRC_NO_COPIEDCELLRETAINS") is not null)
        {
            return;
        }

        HashSet<int> parameterSlots = [.. parameters.Select(parameter => parameter.Slot)];
        HashSet<int> heldSlots = ParametersHeldToTheEnd(parameters);
        var visited = new HashSet<int>();
        foreach (int cell in ArenaCellsCarrying(valueTemp, parameterSlots))
        {
            WithdrawLoopParameterRetainsOfCell(cell, heldSlots, visited);
        }
    }

    /// <summary>
    /// The parameter slots no path releases ahead of the loop's end: a parameter a self call hands
    /// on as the root of a pattern-bound successor is released there and its active flag cleared.
    /// </summary>
    private HashSet<int> ParametersHeldToTheEnd(IReadOnlyCollection<(int Slot, int ActiveSlot)> parameters)
    {
        var zeroTemps = new HashSet<int>();
        var clearedSlots = new HashSet<int>();
        foreach (IrInst instruction in _inst)
        {
            switch (instruction)
            {
                case IrInst.LoadConstInt { Value: 0 } zero:
                    zeroTemps.Add(zero.Target);
                    break;
                case IrInst.StoreLocal store when zeroTemps.Contains(store.Source):
                    clearedSlots.Add(store.Slot);
                    break;
            }
        }

        return [.. parameters
            .Where(parameter => parameter.ActiveSlot < 0 || !clearedSlots.Contains(parameter.ActiveSlot))
            .Select(parameter => parameter.Slot)];
    }

    /// <summary>
    /// The arena cells built by this function whose value reaches <paramref name="valueTemp"/>: the
    /// cell itself, or one a borrow or a local slot (a `let` binding, the join of a `match`) carries
    /// there. A loop parameter's own slot is not followed, since its stores are other iterations'.
    /// </summary>
    private List<int> ArenaCellsCarrying(int valueTemp, HashSet<int> parameterSlots)
    {
        var cells = new List<int>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(valueTemp);
        while (pending.Count > 0)
        {
            int temp = pending.Pop();
            if (!visited.Add(temp))
            {
                continue;
            }

            if (IsArenaCell(temp))
            {
                cells.Add(temp);
                continue;
            }

            int index = FindLastInstruction(instruction =>
                instruction is IrInst.Borrow borrow && borrow.Target == temp
                    || instruction is IrInst.LoadLocal load && load.Target == temp);
            switch (index >= 0 ? _inst[index] : null)
            {
                case IrInst.Borrow borrow:
                    pending.Push(borrow.SourceTemp);
                    break;
                case IrInst.LoadLocal load when !parameterSlots.Contains(load.Slot):
                    foreach (IrInst instruction in _inst)
                    {
                        if (instruction is IrInst.StoreLocal store && store.Slot == load.Slot)
                        {
                            pending.Push(store.Source);
                        }
                    }

                    break;
            }
        }

        return cells;
    }

    private void WithdrawLoopParameterRetainsOfCell(int cellTemp, HashSet<int> heldSlots, HashSet<int> visited)
    {
        if (!visited.Add(cellTemp))
        {
            return;
        }

        var stored = new List<int>();
        foreach (IrInst instruction in _inst)
        {
            switch (instruction)
            {
                case IrInst.SetAdtField field when field.Ptr == cellTemp:
                    stored.Add(field.Source);
                    break;
                case IrInst.StoreMemOffset store when store.BasePtr == cellTemp:
                    stored.Add(store.Source);
                    break;
            }
        }

        foreach (int value in stored)
        {
            if (!WithdrawLoopParameterRetain(value, heldSlots) && IsArenaCell(value))
            {
                WithdrawLoopParameterRetainsOfCell(value, heldSlots, visited);
            }
        }
    }

    private bool IsArenaCell(int temp)
        => FindLastInstruction(instruction => instruction switch
        {
            IrInst.AllocAdt { RuntimeManaged: false } cell => cell.Target == temp,
            IrInst.Alloc { RuntimeManaged: false } cell => cell.Target == temp,
            IrInst.AllocAdtStack cell => cell.Target == temp,
            _ => false,
        }) >= 0;

    // How many instructions read the temp the instruction at `definitionIndex` defines: every reader
    // follows the definition.
    private int TempUseCount(int temp, int definitionIndex)
    {
        int count = 0;
        for (int index = definitionIndex + 1; index < _inst.Count; index++)
        {
            if (StateMachineTransform.GetUsedTemps(_inst[index]).Contains(temp))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Withdraws the retain <paramref name="storedTemp"/> names when it reads a parameter of
    /// <paramref name="heldSlots"/> and the cell storing it is its only reader: a real retain becomes
    /// an identity, a pending field retain's flag is zeroed. True when <paramref name="storedTemp"/>
    /// was a retain at all.
    /// </summary>
    private bool WithdrawLoopParameterRetain(int storedTemp, HashSet<int> heldSlots)
    {
        int index = FindLastInstruction(instruction => instruction switch
        {
            IrInst.RcDup duplicate => duplicate.Target == storedTemp,
            IrInst.LoadLocal load => load.Target == storedTemp,
            _ => false,
        });
        if (index < 0)
        {
            return false;
        }

        if (_inst[index] is IrInst.RcDup duplicate)
        {
            if (duplicate.RuntimeManaged && ReadsLoopParameter(duplicate.SourceTemp, heldSlots) && TempUseCount(storedTemp, index) == 1)
            {
                _inst[index] = duplicate with { RuntimeManaged = false, MayBeEmpty = false };
            }

            return true;
        }

        if (PendingFieldRetainFlagIndex(index) is not int flagIndex)
        {
            return false;
        }

        var retain = (IrInst.RcDup)_inst[index - 3];
        if (ReadsLoopParameter(retain.SourceTemp, heldSlots) && TempUseCount(storedTemp, index) == 1)
        {
            _inst[flagIndex] = new IrInst.LoadConstInt(((IrInst.LoadConstInt)_inst[flagIndex]).Target, 0)
            {
                Location = _inst[flagIndex].Location,
            };
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="temp"/> reads one of <paramref name="parameterSlots"/> or a field,
    /// at any depth, of one: a value that parameter keeps alive. A provisional copy of a tuple
    /// element whose type was unresolved when the tuple was built is a plain read once that type
    /// turns out not to be a string (ResolveDeferredTupleMaterializations makes it one at the end).
    /// </summary>
    private bool ReadsLoopParameter(int temp, HashSet<int> parameterSlots)
    {
        for (int hops = 0; hops < 16; hops++)
        {
            int current = temp;
            int index = FindLastInstruction(instruction => instruction switch
            {
                IrInst.Borrow borrow => borrow.Target == current,
                IrInst.LoadLocal load => load.Target == current,
                IrInst.GetAdtField field => field.Target == current,
                IrInst.CopyOutArena { DeferredElementType: { } deferred } copy => copy.DestTemp == current
                    && Prune(deferred) is not TypeRef.TStr
                    && Environment.GetEnvironmentVariable("GRC_NO_DEFERREDCOPYREAD") is null,
                _ => false,
            });
            switch (index >= 0 ? _inst[index] : null)
            {
                case IrInst.LoadLocal load:
                    return parameterSlots.Contains(load.Slot);
                case IrInst.Borrow borrow:
                    temp = borrow.SourceTemp;
                    break;
                case IrInst.CopyOutArena copy:
                    temp = copy.SrcTemp;
                    break;
                case IrInst.GetAdtField field:
                    temp = field.Ptr;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    // Each loop parameter's slot beside its active flag, -1 for one without.
    private static List<(int Slot, int ActiveSlot)> LoopParametersOf(TcoContext tco)
        => [.. tco.ParamSlots.Select(slot => (slot, tco.TryGetRuntimeManagedActiveSlot(slot, out int activeSlot) ? activeSlot : -1))];


    // A fresh aggregate a loop body builds only to match it, with no arm binding a heap-typed part
    // of it, keeps nothing past the match: its cell dies in the arena, so the construction's
    // retains of loop-parameter reads have no release to balance them. They are withdrawn once the
    // loop's placements are final and the retains carry their resolved form.
    private readonly record struct BorrowedScrutinee(TcoContext Tco, int CellTemp);

    private readonly List<BorrowedScrutinee> _borrowedScrutinees = [];

    private void RecordBorrowedScrutinee(Expr.Match match, int valueTemp, TypeRef valueType)
    {
        if (_tcoCtx is TcoContext tco
            && IsFreshAggregateLiteral(match.Value)
            && !match.Cases.Any(matchCase => PatternBindsHeapValue(matchCase.Pattern, valueType)))
        {
            _borrowedScrutinees.Add(new BorrowedScrutinee(tco, valueTemp));
        }
    }

    private void WithdrawBorrowedScrutineeRetains(TcoContext? tco)
    {
        for (int index = _borrowedScrutinees.Count - 1; index >= 0; index--)
        {
            BorrowedScrutinee scrutinee = _borrowedScrutinees[index];
            if (!ReferenceEquals(scrutinee.Tco, tco))
            {
                continue;
            }

            _borrowedScrutinees.RemoveAt(index);
            WithdrawLoopParameterRetainsOfCopiedCells(scrutinee.CellTemp, LoopParametersOf(scrutinee.Tco));
        }
    }

    /// <summary>
    /// True when <paramref name="pattern"/>, matched against a value of <paramref name="type"/>,
    /// may bind a value that is not a copy type; any shape it cannot see through counts as one.
    /// </summary>
    private bool PatternBindsHeapValue(Pattern pattern, TypeRef type)
    {
        TypeRef pruned = Prune(type);
        switch (pattern)
        {
            case Pattern.Wildcard or Pattern.EmptyList or Pattern.IntLit or Pattern.StrLit or Pattern.RuneLit or Pattern.BoolLit:
                return false;
            case Pattern.Var:
                return !CanArenaReset(pruned);
            case Pattern.As alias:
                return !CanArenaReset(pruned) || PatternBindsHeapValue(alias.Inner, type);
            case Pattern.Or alternatives:
                return alternatives.Alternatives.Any(alternative => PatternBindsHeapValue(alternative, type));
            case Pattern.Tuple tuple when pruned is TypeRef.TTuple tupleType && tupleType.Elements.Count == tuple.Elements.Count:
                return tuple.Elements.Where((element, index) => PatternBindsHeapValue(element, tupleType.Elements[index])).Any();
            case Pattern.Cons cons when pruned is TypeRef.TList list:
                return PatternBindsHeapValue(cons.Head, list.Element) || PatternBindsHeapValue(cons.Tail, pruned);
            case Pattern.Constructor constructor when pruned is TypeRef.TNamedType named:
                return ConstructorPatternBindsHeapValue(named, constructor.Name, constructor.Patterns.Select((field, index) => (index, field)));
            case Pattern.Record record when pruned is TypeRef.TNamedType named && named.Symbol.Constructors.Count == 1:
                List<string> fieldNames = [.. named.Symbol.Constructors[0].DeclaringSyntax.FieldNames];
                return ConstructorPatternBindsHeapValue(
                    named,
                    named.Symbol.Constructors[0].Name,
                    record.Fields.Select(field => (fieldNames.FindIndex(name => string.Equals(name, field.Name, StringComparison.Ordinal)), field.Pattern)));
            default:
                return true;
        }
    }

    private bool ConstructorPatternBindsHeapValue(TypeRef.TNamedType named, string constructorName, IEnumerable<(int Index, Pattern Pattern)> fields)
    {
        ConstructorSymbol? constructor = named.Symbol.Constructors.FirstOrDefault(candidate => string.Equals(candidate.Name, constructorName, StringComparison.Ordinal));
        return constructor is null
            || fields.Any(field => field.Index < 0
                || field.Index >= constructor.Arity
                || PatternBindsHeapValue(field.Pattern, InstantiateConstructorParameterType(constructor, field.Index, named)));
    }

}
