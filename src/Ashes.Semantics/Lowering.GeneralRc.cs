using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

// A call whose result type has no copy-out strategy (a recursive or generic ADT, or an aggregate
// reaching one) leaves its result in the arena, so the call's window can never be reset: every
// allocation the callee made, garbage included, stays until the caller's own window ends. A
// state-threading program whose state reaches such a type therefore keeps everything it ever
// allocated.
//
// Such a result is normalized into the reference-counted heap instead, by a helper synthesized
// once per concrete type. The helper copies the arena cells of the result and takes a reference
// to every reference-counted part it reaches, so a result built mostly from an older
// reference-counted value copies only its new cells. After the copy the call's window is reset.
public sealed partial class Lowering
{
    private readonly Dictionary<string, string> _runtimeManagedAdtNormalizerLabels = new(StringComparer.Ordinal);

    private readonly Dictionary<TypeRef, bool> _generalRcAdmissibleByResolvedType =
        new(ConcreteTypeRefEqualityComparer.Instance);

    /// <summary>
    /// True when a value of <paramref name="type"/> can be normalized whole into the
    /// reference-counted heap: a resolved graph of inline values, strings, byte buffers, big
    /// integers, lists, tuples and algebraic data types, including recursive and generic ones.
    /// Closures, resources, tasks and unresolved types are excluded.
    /// </summary>
    private bool IsGeneralRcAdmissible(TypeRef type)
    {
        TypeRef pruned = Prune(type);
        if (ValueTypeRemainsAbstract(pruned))
        {
            return false;
        }

        if (_generalRcAdmissibleByResolvedType.TryGetValue(pruned, out bool cached))
        {
            return cached;
        }

        bool result = IsGeneralRcAdmissible(pruned, new HashSet<string>(StringComparer.Ordinal));
        _generalRcAdmissibleByResolvedType[pruned] = result;
        return result;
    }

    private bool IsGeneralRcAdmissible(TypeRef type, HashSet<string> path)
    {
        TypeRef pruned = Prune(type);
        if (CanArenaReset(pruned))
        {
            return true;
        }

        return pruned switch
        {
            TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt => true,
            TypeRef.TList list => IsGeneralRcAdmissible(list.Element, path),
            TypeRef.TTuple tuple => tuple.Elements.All(element => IsGeneralRcAdmissible(element, path)),
            TypeRef.TNamedType named => IsGeneralRcAdmissibleNamed(named, path),
            _ => false,
        };
    }

    private bool IsGeneralRcAdmissibleNamed(TypeRef.TNamedType named, HashSet<string> path)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count == 0
            || BuiltinRegistry.IsResourceTypeName(symbol.Name)
            || string.Equals(symbol.Name, "Task", StringComparison.Ordinal)
            || named.TypeArgs.Count != symbol.TypeParameters.Count)
        {
            return false;
        }

        string key = Pretty(named);
        if (!path.Add(key))
        {
            return true;
        }

        bool admissible = symbol.Constructors.All(constructor =>
            Enumerable.Range(0, constructor.Arity).All(index =>
                IsGeneralRcAdmissible(InstantiateConstructorParameterType(constructor, index, named), path)));
        path.Remove(key);
        return admissible;
    }

    /// <summary>
    /// The named type a call-result normalization reaches only through the synthesized helper:
    /// admissible, and not one the inline normalization already expresses.
    /// </summary>
    private bool NeedsRuntimeManagedAdtNormalizer(TypeRef.TNamedType named) =>
        !CanCopyOutAdt(named, out _)
        && !CanRuntimeManageTcoAdt(named)
        && IsGeneralRcAdmissible(named);

    // Values of these types follow one ownership contract of their own, kept apart from the
    // placement of the other runtime-managed values: they are passed borrowed and returned owned.
    // A function returning one normalizes its result itself; every owned value a function holds
    // (a call result) sits in a slot of its own, released once the function's result, or the
    // loop's successor state, holds its own references.
    private bool GeneralRcEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("ASHES_NO_GENERAL_RC"), "1", StringComparison.Ordinal)
        && CanTestRepresentation
        && AllowsOrdinaryRcPlacement
        && AllowsAsyncIndependentRcPlacement
        && !ProgramMayRunUnderLiveHandlerPost
        && !_inCoroutineBody
        && !InReuseSpecializationBody;

    // The contract is one calling convention for the whole program, and a function that may run
    // while a one-shot handler's post is pending keeps its values in the arena the post captures
    // (see AllowsOrdinaryRcPlacement): a program where any function may do so keeps the contract
    // off everywhere. Declared capabilities alone (a `provide`, an operation no `handle` catches)
    // leave nothing pending. The inferred-trait discovery pass runs before the handler analysis and
    // keeps the conservative answer.
    private bool ProgramMayRunUnderLiveHandlerPost =>
        CapabilityGlobalCount > 0
        && (_collectInferredTraitElaboration
            || !_maAnalyzed
            || _maEntryMayExecuteUnderLiveHandlerPost
            || _maFunctionsMayExecuteUnderLiveHandlerPost.Count > 0);

    // A reuse specialization builds its result in the persistent region, and the loop calling it
    // keeps that parameter there: nothing in its body takes or hands over an owned value, or each
    // update would copy the structure onto the reference-counted heap for no one to release.
    private bool InReuseSpecializationBody =>
        _inSpecialization && !_inTraitOperatorSpecialization && _specFreshInputNames is not null;

    // FreshSlot, inside a loop, holds 1 once this iteration stored the slot's value and 0 once a back
    // edge left the value to a successor: only a value this iteration produced can be proven out of
    // its successors' reach by their being the loop's own parameters.
    // Predecessor marks the slot holding what a call's owned slot held before this iteration stored
    // it again: an earlier iteration's value, left to a successor, that the loop still owns.
    // Branches names the match arms and if branches the slot was stored inside, outermost first.
    // Binders are the source names bound to the value or to its parts (the let it is bound by, the
    // pattern bindings of a match on it), shared between the slot and its predecessor slot.
    private sealed record GeneralRcOwnedSlot(int Slot, TypeRef Type, TcoContext? Loop, int FreshSlot = -1, bool Predecessor = false, GeneralRcBranch[]? Branches = null, HashSet<string>? Binders = null);

    // One arm of a match or branch of an if, by the join it belongs to.
    private readonly record struct GeneralRcBranch(string Join, int Arm);

    // The arms and branches being lowered, outermost first.
    private readonly List<GeneralRcBranch> _generalRcBranches = [];

    private void EnterGeneralRcBranch(string join, int arm) => _generalRcBranches.Add(new GeneralRcBranch(join, arm));

    private void LeaveGeneralRcBranch() => _generalRcBranches.RemoveAt(_generalRcBranches.Count - 1);

    // Whether a slot stored inside these branches can hold this iteration's value where the current
    // branches are: not when the two sit in different arms of the same join, which one iteration
    // never both runs. A value an earlier iteration left there is released at its own arm's back
    // edge or at the loop's exit.
    private bool SharesIterationWith(GeneralRcBranch[]? branches)
    {
        if (branches is null)
        {
            return true;
        }

        for (int i = 0; i < branches.Length && i < _generalRcBranches.Count; i++)
        {
            if (branches[i] != _generalRcBranches[i])
            {
                return !string.Equals(branches[i].Join, _generalRcBranches[i].Join, StringComparison.Ordinal);
            }
        }

        return true;
    }

    // Registers the owned slot just stored, marking it this iteration's when it sits in a loop.
    private GeneralRcOwnedSlot RegisterGeneralRcOwnedSlot(int slot, TypeRef type)
    {
        TcoContext? loop = _tcoCtx ?? _generalRcBackEdgeLoop;
        int freshSlot = -1;
        if (loop is not null)
        {
            freshSlot = NewLocal();
            int oneTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(oneTemp, 1));
            Emit(new IrInst.StoreLocal(freshSlot, oneTemp));
        }

        var owned = new GeneralRcOwnedSlot(slot, type, loop, freshSlot, Branches: [.. _generalRcBranches], Binders: new HashSet<string>(StringComparer.Ordinal));
        _generalRcOwnedSlots.Add(owned);
        return owned;
    }

    // Records the names a `let` or a match's patterns bind from a value just lowered, when that
    // value is the read of an owned slot of the loop being lowered: the names through which the
    // slot's value, or a part of it, can reach a successor.
    private void NoteGeneralRcOwnedSlotBinders(int valueTemp, IEnumerable<string> names)
    {
        TcoContext? loop = _tcoCtx ?? _generalRcBackEdgeLoop;
        if (loop is null)
        {
            return;
        }

        List<GeneralRcOwnedSlot> loopSlots = _generalRcOwnedSlots
            .Where(owned => !owned.Predecessor && ReferenceEquals(owned.Loop, loop))
            .ToList();
        int slot = OwnedSlotReadInto(valueTemp, loopSlots, parts: false);
        if (slot < 0)
        {
            return;
        }

        for (int i = loopSlots.Count - 1; i >= 0; i--)
        {
            if (loopSlots[i].Slot == slot)
            {
                loopSlots[i].Binders?.UnionWith(names);
                return;
            }
        }
    }

    // The names every arm of a match binds from its value, noted as binders of the owned slot the
    // value reads.
    private void NoteMatchScrutineeBinders(Expr.Match match, int valueTemp)
    {
        HashSet<string> binders = new(StringComparer.Ordinal);
        foreach (MatchCase matchCase in match.Cases)
        {
            CollectPatternBinders(matchCase.Pattern, binders);
        }

        NoteGeneralRcOwnedSlotBinders(valueTemp, binders);
    }

    // Inside a loop, moves what the owned slot about to be stored still holds into a slot of its own:
    // a value an earlier iteration left to a successor (a parameter threaded through a call) that
    // no back edge released. Returns that slot, or -1 outside a loop.
    private int SaveGeneralRcOwnedPredecessor(int ownedSlot)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_PREDECESSOR") is not null
            || (_tcoCtx ?? _generalRcBackEdgeLoop) is null)
        {
            return -1;
        }

        int predecessorSlot = NewLocal();
        int previousTemp = NewTemp();
        Emit(new IrInst.LoadLocal(previousTemp, ownedSlot));
        Emit(new IrInst.StoreLocal(predecessorSlot, previousTemp));
        return predecessorSlot;
    }

    private void RegisterGeneralRcOwnedPredecessor(int predecessorSlot, TypeRef type, HashSet<string>? binders = null)
    {
        if (predecessorSlot >= 0)
        {
            _generalRcOwnedSlots.Add(new GeneralRcOwnedSlot(predecessorSlot, type, _tcoCtx ?? _generalRcBackEdgeLoop, Predecessor: true, Branches: [.. _generalRcBranches], Binders: binders));
        }
    }

    // The functions whose compiled result is owned under the contract, and the ones being lowered.
    private readonly HashSet<string> _generalRcOwnedResultLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRef> _generalRcLabelsInProgress = new(StringComparer.Ordinal);

    // Records a function about to be lowered with its result type, which decides at the function's
    // return whether it normalizes its result; returns the label.
    private string MarkGeneralRcInProgress(string label, TypeRef resultType)
    {
        _generalRcLabelsInProgress[label] = resultType;
        _loweringFunctionLabels.Push(label);
        return label;
    }

    private void LeaveGeneralRcFunction(string label)
    {
        _generalRcLabelsInProgress.Remove(label);
        if (_loweringFunctionLabels.TryPeek(out string? innermost) && string.Equals(innermost, label, StringComparison.Ordinal))
        {
            _loweringFunctionLabels.Pop();
        }
    }

    // The functions being lowered, innermost last.
    private readonly Stack<string> _loweringFunctionLabels = new();

    // The functions whose result join retained a parameter it returns as it is: a caller keeps its
    // own reference to the argument it passes instead of transferring it to the result.
    private readonly HashSet<string> _joinRetainsParameterLabels = new(StringComparer.Ordinal);

    private void RecordJoinRetainsParameter()
    {
        if (_loweringFunctionLabels.TryPeek(out string? label))
        {
            _joinRetainsParameterLabels.Add(label);
        }
    }

    private bool CalleeJoinRetainsParameter(Expr rootExpr)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_NOTRANSFER") is not null
            || !TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        while (seen.Add(label))
        {
            if (_joinRetainsParameterLabels.Contains(label))
            {
                return true;
            }

            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                break;
            }

            label = nextLabel;
        }

        return false;
    }

    /// <summary>
    /// Whether a call of such a result type receives its result owned: from a function compiled
    /// under the contract, or from one still being lowered, since a function of this result type
    /// normalizes its result at its return. A mutually recursive sibling not lowered yet may still
    /// turn out generic and is not assumed.
    /// </summary>
    private bool CalleeReturnsGeneralRcOwned(Expr rootExpr, int argumentCount, TypeRef callResultType)
    {
        if (argumentCount == 0 || !TryResolveKnownFunctionLabel(rootExpr, out string label))
        {
            return false;
        }

        for (int i = 1; i < argumentCount; i++)
        {
            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? nextLabel))
            {
                return false;
            }

            label = nextLabel;
        }

        if (_generalRcOwnedResultLabels.Contains(label))
        {
            return true;
        }

        // A function still being lowered: its own result type may only resolve later, so this call
        // site's concrete one is recorded as a commitment its return keeps (the recursion is
        // monomorphic, so the two are the same type).
        if (!_generalRcLabelsInProgress.ContainsKey(label))
        {
            return false;
        }

        _generalRcCommittedResultTypes[label] = callResultType;
        return true;
    }

    private readonly Dictionary<string, TypeRef> _generalRcCommittedResultTypes = new(StringComparer.Ordinal);

    // The result temp the function being finished retained or copied at its return (temps are
    // numbered per function, so only the current one is kept): it holds its own reference, so the
    // loop's exit that follows releases its parameters instead of transferring one as the result.
    private int _generalRcIndependentResultTemp = -1;

    /// <summary>
    /// Marks every closure over a function that normalizes its result at its return, once the whole
    /// program is lowered (a recursive function's own closure is built before its body is done), so
    /// a caller that cannot name the callee takes such a result over owned instead of retaining it.
    /// </summary>
    private void MarkGeneralRcOwnedResultClosures(IrFunction entry)
    {
        foreach (List<IrInst> instructions in _funcs.Select(function => function.Instructions).Append(entry.Instructions))
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                instructions[i] = MarkClosureAdoption(instructions[i] switch
                {
                    IrInst.MakeClosure closure when _generalRcOwnedResultLabels.Contains(closure.FuncLabel) =>
                        closure with { ReturnsGeneralRcOwned = true },
                    IrInst.MakeClosureStack closure when _generalRcOwnedResultLabels.Contains(closure.FuncLabel) =>
                        closure with { ReturnsGeneralRcOwned = true },
                    IrInst other => other,
                });
            }
        }
    }

    // A closure built before its function's entry normalization was decided carries a stale
    // adoption bit; the function adopts a handed-over argument all the same.
    private IrInst MarkClosureAdoption(IrInst instruction)
        => instruction switch
        {
            IrInst.MakeClosure closure when OwnsKeptPiece(4) && _runtimeNormalizedFunctionArgumentLabels.Contains(closure.FuncLabel) =>
                closure with { AcceptsRuntimeManagedArgument = true },
            IrInst.MakeClosureStack closure when OwnsKeptPiece(4) && _runtimeNormalizedFunctionArgumentLabels.Contains(closure.FuncLabel) =>
                closure with { AcceptsRuntimeManagedArgument = true },
            _ => instruction,
        };

    /// <summary>
    /// For a call result an indirect closure application produced, the flag its closure's header
    /// carries (1 when the callee returns it owned under the contract); -1 for any other call.
    /// </summary>
    private int TryEmitClosureReturnsGeneralRcOwnedFlag(int resultTemp)
    {
        for (int i = _inst.Count - 1; i >= 0 && i >= _inst.Count - 64; i--)
        {
            if (_inst[i] is IrInst.CallClosure call && call.Target == resultTemp)
            {
                return EmitClosureReturnsGeneralRcOwnedFlag(call.ClosureTemp);
            }
        }

        return -1;
    }

    private int EmitClosureReturnsGeneralRcOwnedFlag(int closureTemp)
    {
        int packedTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(packedTemp, closureTemp, 16));
        int shiftTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(shiftTemp, 60));
        int shiftedTemp = NewTemp();
        Emit(new IrInst.ShrInt(shiftedTemp, packedTemp, shiftTemp));
        int maskTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(maskTemp, 1));
        int flagTemp = NewTemp();
        Emit(new IrInst.AndInt(flagTemp, shiftedTemp, maskTemp));
        return flagTemp;
    }

    // The loop whose back-edge arguments are being lowered, which run with no loop context of their own.
    private TcoContext? _generalRcBackEdgeLoop;

    // The owned slots each back edge releases, captured from the function's own frame when the tail
    // call is lowered (the back edge is emitted later, once the function is finished), by the
    // pending reset's id; and the id of the back edge being emitted.
    private readonly Dictionary<int, List<GeneralRcOwnedSlot>> _generalRcBackEdgeSlots = [];
    private int _generalRcBackEdgeId = -1;

    private void RecordGeneralRcBackEdgeSlots(int pendingId, TcoContext tco, IReadOnlyList<Expr> arguments, IReadOnlyList<int> argumentTemps)
    {
        List<GeneralRcOwnedSlot> loopSlots = _generalRcOwnedSlots
            .Where(owned => ReferenceEquals(owned.Loop, tco) && SharesIterationWith(owned.Branches))
            .ToList();
        _generalRcBackEdgeSlots[pendingId] = loopSlots;
        _generalRcBackEdgeParameterParts[pendingId] = arguments
            .Select(argument => IsLoopParameterPart(argument, tco))
            .ToArray();
        _generalRcBackEdgeStrictParameterParts[pendingId] = arguments
            .Select(argument => IsStrictLoopParameterPart(argument, tco))
            .ToArray();
        _generalRcBackEdgeArgumentSlots[pendingId] = argumentTemps
            .Select(temp => OwnedSlotReadInto(temp, loopSlots, parts: false))
            .ToArray();
        _generalRcBackEdgeArgumentPartSlots[pendingId] = argumentTemps
            .Select(temp => OwnedSlotReadInto(temp, loopSlots, parts: true))
            .ToArray();
    }

    // Per back edge, the owned slot each successor argument is a plain read of, or -1; and the owned
    // slot each is a plain read of or a part of.
    private readonly Dictionary<int, int[]> _generalRcBackEdgeArgumentSlots = [];
    private readonly Dictionary<int, int[]> _generalRcBackEdgeArgumentPartSlots = [];

    private int OwnedSlotReadInto(int temp, List<GeneralRcOwnedSlot> ownedSlots, bool parts)
    {
        for (int i = _inst.Count - 1; i >= 0; i--)
        {
            if (_inst[i] is IrInst.LoadLocal load && load.Target == temp)
            {
                return ownedSlots.Any(owned => owned.Slot == load.Slot) ? load.Slot
                    : parts ? OwnedSlotPartBoundInto(load.Slot, ownedSlots)
                    : -1;
            }

            if (parts && _inst[i] is IrInst.Borrow borrow && borrow.Target == temp)
            {
                temp = borrow.SourceTemp;
            }
        }

        return -1;
    }

    // A binding stored once with a field read out of an owned slot's value (a pattern binding of a
    // call's owned result): a part of a value holding its own references, which stays alive with it.
    // A binding stored once with the owned slot's value itself (`let next = f(x)`) is that value.
    private static bool OwnedSlotAliasesEnabled => Environment.GetEnvironmentVariable("GRC_NO_OWNEDSLOTALIAS") is null;

    private int OwnedSlotPartBoundInto(int slot, List<GeneralRcOwnedSlot> ownedSlots)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_OWNEDSLOTPART") is not null)
        {
            return -1;
        }

        int storeIndex = -1;
        for (int i = 0; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.StoreLocal store && store.Slot == slot)
            {
                if (storeIndex >= 0)
                {
                    return -1;
                }

                storeIndex = i;
            }
        }

        if (storeIndex < 0 || _inst[storeIndex] is not IrInst.StoreLocal { Source: int partTemp })
        {
            return -1;
        }

        int baseTemp = -1;
        for (int i = storeIndex - 1; i >= 0; i--)
        {
            if (StateMachineTransform.GetDefinedTemps(_inst[i]).Contains(partTemp))
            {
                if (_inst[i] is IrInst.LoadLocal alias && OwnedSlotAliasesEnabled)
                {
                    return ownedSlots.Any(owned => owned.Slot == alias.Slot) ? alias.Slot : -1;
                }

                baseTemp = _inst[i] switch
                {
                    IrInst.LoadMemOffset read => read.BasePtr,
                    IrInst.GetAdtField field => field.Ptr,
                    _ => -1,
                };
                break;
            }
        }

        for (int i = storeIndex - 1; i >= 0 && baseTemp >= 0; i--)
        {
            if (_inst[i] is IrInst.LoadLocal load && load.Target == baseTemp)
            {
                return ownedSlots.Any(owned => owned.Slot == load.Slot) ? load.Slot : -1;
            }
        }

        return -1;
    }

    // Per back edge, which successor arguments are a loop parameter or a part of one (a pattern
    // binding destructured out of it): values the iteration received, not ones it owns.
    private readonly Dictionary<int, bool[]> _generalRcBackEdgeParameterParts = [];

    // Per back edge, the successor arguments that are a loop parameter or a pattern binding read out
    // of one, read straight from their slots: never a value a call produced.
    private readonly Dictionary<int, bool[]> _generalRcBackEdgeStrictParameterParts = [];

    private bool IsStrictLoopParameterPart(Expr argument, TcoContext tco)
        => argument is Expr.Var variable
            && Lookup(variable.Name) is Binding.Local local
            && (tco.ParamSlots.Contains(local.Slot) || IsLoopParameterPartSlot(local.Slot));

    private bool IsLoopParameterPart(Expr argument, TcoContext tco)
        => argument is Expr.Var variable
            && ((Lookup(variable.Name) is Binding.Local local
                    && (tco.ParamSlots.Contains(local.Slot) || IsLoopParameterPartSlot(local.Slot)))
                || LookupOwnedValue(variable.Name) is { PerceusRootParameterSlot: >= 0 });

    // The pattern-binding slots, per function body, whose value was read out of a loop parameter
    // (directly, through field reads, or out of another such binding).
    private readonly Dictionary<List<IrInst>, HashSet<int>> _loopParameterPartSlotsByBody =
        new(ReferenceEqualityComparer.Instance);

    private bool IsLoopParameterPartSlot(int slot)
        => _loopParameterPartSlotsByBody.TryGetValue(_inst, out HashSet<int>? slots) && slots.Contains(slot);

    private void RecordLoopParameterPartSlot(int slot, int valueTemp)
    {
        if (_tcoCtx is not { } tco || !IsLoopParameterDerivedTemp(tco, valueTemp, 0))
        {
            return;
        }

        if (!_loopParameterPartSlotsByBody.TryGetValue(_inst, out HashSet<int>? slots))
        {
            slots = [];
            _loopParameterPartSlotsByBody[_inst] = slots;
        }

        slots.Add(slot);
    }

    private bool IsLoopParameterDerivedTemp(TcoContext tco, int temp, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        for (int i = _inst.Count - 1; i >= 0 && i >= _inst.Count - 256; i--)
        {
            switch (_inst[i])
            {
                case IrInst.LoadLocal load when load.Target == temp:
                    return tco.ParamSlots.Contains(load.Slot) || IsLoopParameterPartSlot(load.Slot);
                case IrInst.LoadMemOffset field when field.Target == temp:
                    return IsLoopParameterDerivedTemp(
                        tco,
                        TryGetLocalAggregateFieldSource(field.BasePtr, field.OffsetBytes, i, out int storedTemp) ? storedTemp : field.BasePtr,
                        depth + 1);
                case IrInst.GetAdtField field when field.Target == temp:
                    return IsLoopParameterDerivedTemp(tco, field.Ptr, depth + 1);
                case IrInst.Borrow borrow when borrow.Target == temp:
                    return IsLoopParameterDerivedTemp(tco, borrow.SourceTemp, depth + 1);
            }
        }

        return false;
    }

    // A read of a field of an aggregate this function built itself (the tuple a `match (a, b) with`
    // scrutinizes) is the value stored into that field: the read stands for the stored temp, so a
    // binding matched out of such a tuple is traced to the parameter the tuple was built from.
    private bool TryGetLocalAggregateFieldSource(int aggregateTemp, int offsetBytes, int beforeIndex, out int storedTemp)
    {
        storedTemp = -1;
        if (Environment.GetEnvironmentVariable("GRC_NO_TUPLEPARTS") is not null)
        {
            return false;
        }

        for (int i = beforeIndex - 1; i >= 0 && i >= beforeIndex - 256; i--)
        {
            switch (_inst[i])
            {
                case IrInst.StoreMemOffset store when store.BasePtr == aggregateTemp && store.OffsetBytes == offsetBytes && storedTemp < 0:
                    storedTemp = store.Source;
                    break;
                case IrInst.Alloc alloc when alloc.Target == aggregateTemp:
                    return storedTemp >= 0;
            }
        }

        return false;
    }

    // The owned slots of the function being lowered; saved and restored around a nested one.
    private List<GeneralRcOwnedSlot> _generalRcOwnedSlots
    {
        get
        {
            // Keyed by the instruction list of the function the slots are emitted into, which is
            // where their release belongs, whatever lowering frame is current.
            if (!_generalRcSlotsByBody.TryGetValue(_inst, out List<GeneralRcOwnedSlot>? slots))
            {
                slots = [];
                _generalRcSlotsByBody[_inst] = slots;
            }

            return slots;
        }
    }

    private readonly Dictionary<List<IrInst>, List<GeneralRcOwnedSlot>> _generalRcSlotsByBody =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// True when a resolved type reaches a named type only the normalization helper expresses and
    /// has no fixed copy-out, so a value of it follows the contract above.
    /// </summary>
    private bool IsGeneralRcValueType(TypeRef type)
    {
        TypeRef pruned = Prune(type);
        return GeneralRcEnabled
            && !CanArenaReset(pruned)
            && GetCallCopyOutKind(pruned, out _, out _) == CopyOutKind.None
            && !ContainsUnresolvedLayoutType(pruned, [])
            && IsGeneralRcAdmissible(pruned)
            && ReachesGeneralRcNamedType(pruned, new HashSet<string>(StringComparer.Ordinal));
    }

    // A record the contract admits although its copy could be written inline: it is copied by the
    // synthesized normalizer all the same, one call per site instead of its whole graph.
    private bool IsInlineCopiedContractRecord(TypeRef.TNamedType named)
        => GeneralRcEnabled
            && !NeedsRuntimeManagedAdtNormalizer(named)
            && IsNormalizableUncoveredRecord(named)
            && IsGeneralRcNamedType(named);

    private readonly Dictionary<string, string> _contractRecordDropperLabels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _contractRecordDroppersInProgress = new(StringComparer.Ordinal);

    // A loop threading such a record releases it at every back edge and exit, so its release is
    // one call to a dropper synthesized once per type, whose body is the release written inline.
    private bool TryEmitContractRecordDropCall(int valueTemp, TypeRef.TNamedType named)
    {
        string key = Pretty(named);
        if (!IsInlineCopiedContractRecord(named) || _contractRecordDroppersInProgress.Contains(key))
        {
            return false;
        }

        if (!_contractRecordDropperLabels.TryGetValue(key, out string? label))
        {
            label = $"__rcdrop_record_{_nextLambdaId++}";
            _contractRecordDropperLabels[key] = label;
            _contractRecordDroppersInProgress.Add(key);
            SynthesizedBodyState saved = BeginSynthesizedBody();
            NewLocal(); // slot 0: env (implicit)
            int argSlot = NewLocal(); // slot 1: value (implicit)
            int argTemp = NewTemp();
            Emit(new IrInst.LoadLocal(argTemp, argSlot));
            EmitRuntimeManagedAdtDrop(argTemp, named);
            int returnTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(returnTemp, 0));
            Emit(new IrInst.Return(returnTemp));
            AddFunction(
                new IrFunction(
                    Label: label,
                    Instructions: new List<IrInst>(_inst),
                    LocalCount: _nextLocalSlot,
                    TempCount: _nextTempSlot,
                    HasEnvAndArgParams: true),
                new IrFunctionOrigin(
                    label,
                    IrFunctionOriginKind.RuntimeManagedAdtDropper,
                    CompilerOwner: new CompilerFunctionOwner(CompilerFunctionOwnerKind.Type, key),
                    StableDiscriminator: key));
            RestoreEnclosingBodyState(saved);
            _contractRecordDroppersInProgress.Remove(key);
        }

        int envTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(envTemp, 0));
        Emit(new IrInst.CallKnown(NewTemp(), label, envTemp, valueTemp));
        return true;
    }

    /// <summary>
    /// A named type the contract governs: one only the normalization helper expresses, or a record
    /// the entry normalization re-establishes, that owns a heap child, and that the recursive and
    /// owned-child reference-counted paths do not already manage on their own terms.
    /// </summary>
    private bool IsGeneralRcNamedType(TypeRef.TNamedType named) =>
        (NeedsRuntimeManagedAdtNormalizer(named) || IsNormalizableUncoveredRecord(named))
        && !CanRuntimeManageRecursiveCopyAdt(named)
        && !CanRuntimeManageOwnedChildAdt(named)
        && named.Symbol.Constructors.Any(constructor =>
            Enumerable.Range(0, constructor.Arity).Any(index =>
                !CanArenaReset(Prune(InstantiateConstructorParameterType(constructor, index, named)))));

    private bool ReachesGeneralRcNamedType(TypeRef type, HashSet<string> path) => Prune(type) switch
    {
        TypeRef.TList list => ReachesGeneralRcNamedType(list.Element, path),
        TypeRef.TTuple tuple => tuple.Elements.Any(element => ReachesGeneralRcNamedType(element, path)),
        TypeRef.TNamedType named => IsGeneralRcNamedType(named)
            || (path.Add(Pretty(named))
                && named.Symbol.Constructors.Any(constructor =>
                    Enumerable.Range(0, constructor.Arity).Any(index =>
                        ReachesGeneralRcNamedType(InstantiateConstructorParameterType(constructor, index, named), path)))),
        _ => false,
    };

    // A call whose result type was unresolved when it was emitted requests an arena result, for a
    // caller that cannot own it. A result of the contract's types is owned by the caller once the
    // call is lowered, so the request word is rewritten to ask for nothing: the callee then hands
    // over its reference-counted result instead of deep-copying it into the arena.
    private void CancelArenaResultRequest(int resultTemp)
    {
        int callIndex = _inst.Count - 1;
        while (callIndex >= 0 && callIndex >= _inst.Count - 64 && !(_inst[callIndex] is IrInst.CallClosure { } candidate && candidate.Target == resultTemp))
        {
            callIndex--;
        }

        if (callIndex < 0 || _inst[callIndex] is not IrInst.CallClosure call || call.Target != resultTemp || !_arenaResultRequestingCalls.Contains(call))
        {
            return;
        }

        int requestTemp = call.RuntimeManagedArgumentFlagTemp;
        for (int i = callIndex - 1; i >= 0 && i >= callIndex - 64; i--)
        {
            switch (_inst[i])
            {
                case IrInst.OrInt word when word.Target == requestTemp:
                    requestTemp = word.Right;
                    break;
                case IrInst.LoadConstInt request when request.Target == requestTemp:
                    _inst[i] = request with { Value = 0 };
                    _arenaResultRequestingCalls.Remove(call);
                    return;
            }
        }
    }

    /// <summary>
    /// A call result of such a type, owned by the caller once the call's window is reset: taken
    /// over as it is from a callee known to return it owned (<paramref name="calleeReturnsOwned"/>),
    /// normalized otherwise: another callee's claim of a reference-counted result is not taken as
    /// the contract's, since a generic callee's result may borrow its arguments' parts. The value
    /// is kept in an owned slot and handed on without a runtime-managed fact.
    /// </summary>
    private int LowerGeneralRcCallResult(
        int callWmCursorSlot,
        int callWmEndSlot,
        int currentTemp,
        TypeRef callResultType,
        bool calleeReturnsOwned)
    {
        CancelArenaResultRequest(currentTemp);
        int ownedFlagTemp = calleeReturnsOwned ? -1 : TryEmitClosureReturnsGeneralRcOwnedFlag(currentTemp);
        int callPreRestoreEndSlot = NewLocal();
        int ownedSlot = NewLocal();
        int predecessorSlot = SaveGeneralRcOwnedPredecessor(ownedSlot);
        Emit(new IrInst.StoreLocal(ownedSlot, currentTemp));
        Emit(new IrInst.RestoreArenaState(callWmCursorSlot, callWmEndSlot, callPreRestoreEndSlot));
        if (!calleeReturnsOwned)
        {
            // A closure whose header says its function returns the result owned hands it over.
            string ownedLabel = NewLabel("general_rc_result_owned");
            if (ownedFlagTemp >= 0)
            {
                string normalizeLabel = NewLabel("general_rc_result_normalize");
                Emit(new IrInst.JumpIfFalse(ownedFlagTemp, normalizeLabel));
                Emit(new IrInst.Jump(ownedLabel));
                Emit(new IrInst.Label(normalizeLabel));
            }

            Emit(new IrInst.StoreLocal(ownedSlot, EmitRuntimeManagedTcoDeepCopy(currentTemp, callResultType)));
            Emit(new IrInst.Label(ownedLabel));
        }

        Emit(new IrInst.ReclaimArenaChunks(callWmEndSlot, callPreRestoreEndSlot));
        GeneralRcOwnedSlot owned = RegisterGeneralRcOwnedSlot(ownedSlot, Prune(callResultType));
        RegisterGeneralRcOwnedPredecessor(predecessorSlot, Prune(callResultType), owned.Binders);
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, ownedSlot));
        return resultTemp;
    }

    /// <summary>
    /// The result a function returns, made to hold its own references so the function's owned
    /// slots can be released after it: a result of the contract's types is retained or normalized
    /// here, and so is any other result the reference-counted heap can own outright (a string, a
    /// list of scalars or strings) when the function holds owned values it might point into.
    /// Returns the temp to return.
    /// </summary>
    private int FinishGeneralRcFunctionResult(string label, int bodyTemp, TypeRef bodyType, bool closesProducerChain = false, TcoContext? loopTco = null)
    {
        LeaveGeneralRcFunction(label);
        _generalRcIndependentResultTemp = -1;
        if (_generalRcCommittedResultTypes.Remove(label, out TypeRef? committed))
        {
            Unify(bodyType, committed);
        }

        TypeRef pruned = ResolvePhantomResultLayout(label, bodyType);
        _enclosingScopesByLabel.Remove(label);
        if (!GeneralRcEnabled)
        {
            return bodyTemp;
        }

        bool contractResult = IsGeneralRcValueType(pruned);
        bool normalizesOwnedResult = !contractResult
            && _generalRcOwnedSlots.Count > 0
            && !CanArenaReset(pruned)
            && !ContainsUnresolvedLayoutType(pruned, [])
            && (CanNormalizeIntoOwnedRuntimeValue(pruned) || HasFixedArenaCopyOut(pruned));
        int resultTemp = bodyTemp;
        // Any other result is copied into the arena, where the caller takes it like any fresh
        // result: a copy on the reference-counted heap would be owned by a caller that does not
        // know it (a join with an arm's static value hides it), and leak there.
        int arenaCopyTemp = contractResult || !normalizesOwnedResult || IsRuntimeManagedResultTemp(bodyTemp)
            ? -1
            : EmitGeneralRcArenaResultCopy(bodyTemp, pruned);
        if (arenaCopyTemp >= 0)
        {
            resultTemp = arenaCopyTemp;
            _generalRcIndependentResultTemp = resultTemp;
        }
        // The other placement's runtime-managed fact is not trusted for the contract's own types: a
        // join of a recursive producer's arms carries it while an arm's cells stay in the arena.
        else if (contractResult || (normalizesOwnedResult && !IsRuntimeManagedResultTemp(bodyTemp)))
        {
            resultTemp = EmitGeneralRcOwnedFunctionResult(bodyTemp, pruned, contractResult, closesProducerChain, loopTco);
            _generalRcIndependentResultTemp = resultTemp;
        }

        if (contractResult)
        {
            _generalRcOwnedResultLabels.Add(label);
        }

        // Any other result (a closure, a tuple) may still point into the owned values, which are
        // then left to leak. The slots stay registered: loop back edges are emitted later.
        if (contractResult || normalizesOwnedResult || CanArenaReset(pruned) || IsRuntimeManagedResultTemp(resultTemp))
        {
            EmitGeneralRcOwnedSlotDrops(_generalRcOwnedSlots);
        }

        return resultTemp;
    }

    // A type variable of a function's result that is quantified by the function's scheme and occurs
    // in none of its parameter types is a phantom: no value of its type can reach the function, so
    // the `a` of a result's `Maybe(a)` only ever reads `None`. The result's layout is decided with
    // the variable read as `Never`, at the function's return and at every call of it alike, so a
    // result of the contract's types but for the phantom is owned like any other instead of staying
    // in the arena beside the owned values it holds. A variable under a trait constraint is not a
    // phantom: a method of the trait may produce a value of its type. An external function's types
    // are the foreign interface's, which have no variables, so no external produces one either.
    // The reading is taken only where it makes the result one of the contract's types; any other
    // result keeps its variables, and the copy-out they choose.
    private static bool PhantomResultRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_PHANTOM") is null;

    // The scopes each function being lowered was created in, by label: the environment its scheme
    // is generalized against, which its own scope no longer holds.
    private readonly Dictionary<string, ImmutableSortedDictionary<string, Binding>[]> _enclosingScopesByLabel =
        new(StringComparer.Ordinal);

    // The functions a `let` binds, with the curry stages each one returns: their types are
    // generalized once the binding is lowered, so a result variable no parameter and no binding in
    // scope names is quantified there. Any other function (a lambda handed to a call, a constructor
    // taken as a function) is typed by the context it meets afterwards, where such a variable may
    // still be unified with a value's type.
    private readonly HashSet<string> _letBoundFunctionLabels = new(StringComparer.Ordinal);

    private void NoteLetBoundFunction(string label)
    {
        if (_activeFunctionOrigin is { Kind: IrFunctionOriginKind.SourceFunction })
        {
            _letBoundFunctionLabels.Add(label);
        }
    }

    private void NoteLetBoundCurryStage(string stageLabel, string label)
    {
        if (_letBoundFunctionLabels.Contains(stageLabel))
        {
            _letBoundFunctionLabels.Add(label);
        }
    }

    // Whether the function `label` is `selfLabel` or one of the curry stages it returns.
    private bool CurryChainReaches(string selfLabel, string label)
    {
        string current = selfLabel;
        for (int depth = 0; depth < 64; depth++)
        {
            if (string.Equals(current, label, StringComparison.Ordinal))
            {
                return true;
            }

            if (!_functionReturnedClosureLabels.TryGetValue(current, out string? next))
            {
                return false;
            }

            current = next;
        }

        return false;
    }

    // The layout of a function's own result at its return: the body's type with the phantoms read
    // as `Never`, for a function a `let` binds. The function's scope and the scopes it was created
    // in hold every variable that is not a phantom, but for the function's own binding (its self
    // reference), which names the result's variables and is skipped.
    private TypeRef ResolvePhantomResultLayout(string label, TypeRef resultType)
    {
        TypeRef pruned = Prune(resultType);
        if (!PhantomResultRuleEnabled
            || pruned is TypeRef.TFun
            || !_letBoundFunctionLabels.Contains(label)
            || !ContainsUnresolvedLayoutType(pruned, []))
        {
            return pruned;
        }

        HashSet<int> phantoms = [];
        FtvType(pruned, phantoms);
        if (phantoms.Count == 0)
        {
            return pruned;
        }

        phantoms.ExceptWith(VariablesHeldBesideFunction(label));
        return phantoms.Count == 0 ? pruned : ContractLayoutOrSelf(SubstitutePhantomVariables(pruned, phantoms), pruned);
    }

    private HashSet<int> VariablesHeldBesideFunction(string label)
    {
        HashSet<int> held = [];
        if (_ambientRow is not null)
        {
            FtvType(_ambientRow, held);
        }

        if (_scopes.Count > 0)
        {
            AddScopeFtvBesideSelf(_scopes.Peek(), label, held);
        }

        if (_enclosingScopesByLabel.TryGetValue(label, out ImmutableSortedDictionary<string, Binding>[]? enclosing))
        {
            foreach (ImmutableSortedDictionary<string, Binding> scope in enclosing)
            {
                AddScopeFtvBesideSelf(scope, label, held);
            }
        }

        foreach (TraitConstraintScope scope in _traitConstraintScopes)
        {
            foreach (TraitConstraint constraint in scope.Constraints)
            {
                foreach (TypeRef argument in constraint.TypeArgs)
                {
                    FtvType(argument, held);
                }
            }
        }

        return held;
    }

    private TypeRef ContractLayoutOrSelf(TypeRef substituted, TypeRef original)
        => IsGeneralRcValueType(substituted) ? substituted : original;

    private void AddScopeFtvBesideSelf(ImmutableSortedDictionary<string, Binding> scope, string label, HashSet<int> held)
    {
        foreach (Binding binding in scope.Values)
        {
            if (binding is Binding.Self self && CurryChainReaches(self.FuncLabel, label))
            {
                continue;
            }

            AddBindingFtv(binding, held);
        }
    }

    private void AddBindingFtv(Binding binding, HashSet<int> result)
    {
        switch (binding)
        {
            case Binding.Scheme scheme:
                AddSchemeFtv(scheme.S, result);
                break;
            case Binding.EnvScheme environmentScheme:
                AddSchemeFtv(environmentScheme.S, result);
                break;
            case Binding.Intrinsic intrinsic:
                AddSchemeFtv(intrinsic.S, result);
                break;
            case Binding.PreludeValue preludeValue:
                AddSchemeFtv(preludeValue.S, result);
                break;
            default:
                AddBindingTypeFtv(binding, result);
                break;
        }
    }

    private TypeRef SubstitutePhantomVariables(TypeRef type, HashSet<int> phantoms)
    {
        TypeRef pruned = Prune(type);
        return pruned switch
        {
            TypeRef.TVar variable when phantoms.Contains(variable.Id) => new TypeRef.TNever(),
            TypeRef.TList list => new TypeRef.TList(SubstitutePhantomVariables(list.Element, phantoms)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(
                tuple.Elements.Select(element => SubstitutePhantomVariables(element, phantoms)).ToList()),
            TypeRef.TNamedType { TypeArgs.Count: > 0 } named => named with
            {
                TypeArgs = named.TypeArgs.Select(argument => SubstitutePhantomVariables(argument, phantoms)).ToList(),
            },
            _ => pruned,
        };
    }

    // The layout of a call's result: the callee's scheme names the phantoms, and each one still
    // unresolved at this call reads as `Never`. A call of a function being lowered (a self call)
    // reads them the way the function's return will.
    private TypeRef ResolveCallPhantomResultLayout(Expr rootExpr, int argumentCount, TypeRef callResultType)
    {
        if (!PhantomResultRuleEnabled || callResultType is TypeRef.TFun || !ContainsUnresolvedLayoutType(callResultType, []))
        {
            return callResultType;
        }

        Binding? binding = rootExpr switch
        {
            Expr.Var variable => Lookup(variable.Name),
            Expr.QualifiedVar qualified => Lookup(
                $"{ProjectSupport.SanitizeModuleBindingName(ResolveModuleAlias(qualified.Module))}_{qualified.Name}"),
            _ => null,
        };
        return binding switch
        {
            Binding.Scheme scheme => SubstituteSchemePhantoms(scheme.S, argumentCount, callResultType),
            Binding.EnvScheme environmentScheme => SubstituteSchemePhantoms(environmentScheme.S, argumentCount, callResultType),
            Binding.Self self => ResolvePhantomResultLayout(self.FuncLabel, callResultType),
            _ => callResultType,
        };
    }

    private TypeRef SubstituteSchemePhantoms(TypeScheme scheme, int argumentCount, TypeRef callResultType)
    {
        HashSet<int> held = [];
        TypeRef cursor = Prune(scheme.Body);
        for (int index = 0; index < argumentCount; index++)
        {
            if (cursor is not TypeRef.TFun function)
            {
                return callResultType;
            }

            FtvType(function.Arg, held);
            cursor = Prune(function.Ret);
        }

        foreach (TraitConstraint constraint in scheme.Constraints)
        {
            foreach (TypeRef argument in constraint.TypeArgs)
            {
                FtvType(argument, held);
            }
        }

        HashSet<int> phantoms = [];
        FtvType(cursor, phantoms);
        phantoms.IntersectWith(scheme.Quantified.Select(variable => variable.Id));
        phantoms.ExceptWith(held);
        return phantoms.Count == 0
            ? callResultType
            : ContractLayoutOrSelf(SubstituteInstantiatedPhantoms(cursor, callResultType, phantoms), callResultType);
    }

    // The instantiated type is walked beside the scheme's: where the scheme has a phantom and the
    // instantiation still a variable, the variable reads as `Never`; a variable the call resolved
    // keeps its resolution, which is a layout of its own.
    private TypeRef SubstituteInstantiatedPhantoms(TypeRef schemeType, TypeRef instantiated, HashSet<int> phantoms)
    {
        TypeRef scheme = Prune(schemeType);
        TypeRef pruned = Prune(instantiated);
        return (scheme, pruned) switch
        {
            (TypeRef.TVar variable, TypeRef.TVar) when phantoms.Contains(variable.Id) => new TypeRef.TNever(),
            (TypeRef.TList schemeList, TypeRef.TList list) =>
                new TypeRef.TList(SubstituteInstantiatedPhantoms(schemeList.Element, list.Element, phantoms)),
            (TypeRef.TTuple schemeTuple, TypeRef.TTuple tuple) when schemeTuple.Elements.Count == tuple.Elements.Count =>
                new TypeRef.TTuple(tuple.Elements
                    .Select((element, index) => SubstituteInstantiatedPhantoms(schemeTuple.Elements[index], element, phantoms))
                    .ToList()),
            (TypeRef.TNamedType schemeNamed, TypeRef.TNamedType named)
                when ReferenceEquals(schemeNamed.Symbol, named.Symbol) && schemeNamed.TypeArgs.Count == named.TypeArgs.Count =>
                named with
                {
                    TypeArgs = named.TypeArgs
                        .Select((argument, index) => SubstituteInstantiatedPhantoms(schemeNamed.TypeArgs[index], argument, phantoms))
                        .ToList(),
                },
            _ => pruned,
        };
    }

    // A result the body produced owned (a fresh reference-counted value) is taken as it is when its
    // cells turn out reference-counted, and copied when they are still in the arena. The copy of an
    // arena result borrows its children ahead of the loop's exit releases.
    private int EmitGeneralRcOwnedFunctionResult(int bodyTemp, TypeRef pruned, bool contractResult, bool closesProducerChain, TcoContext? loopTco)
    {
        if (contractResult && loopTco is not null)
        {
            WithdrawLoopParameterRetainsOfCopiedCells(bodyTemp, LoopParametersOf(loopTco));
        }

        int resultTemp = contractResult && Environment.GetEnvironmentVariable("GRC_NO_KEEP") is null && (closesProducerChain || IsNewlyProducedRcTemp(bodyTemp) || IsNormalizedJoinTemp(bodyTemp))
            ? EmitOwnedResultOrCopy(bodyTemp, pruned)
            : EmitRuntimeManagedTcoDeepCopy(bodyTemp, pruned);
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private int EmitOwnedResultOrCopy(int sourceTemp, TypeRef type)
    {
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, sourceTemp));
        int referenceCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, sourceTemp));
        string copyLabel = NewLabel("general_rc_result_arena_copy");
        string ownedLabel = NewLabel("general_rc_result_already_owned");
        Emit(new IrInst.JumpIfFalse(referenceCountedTemp, copyLabel));
        Emit(new IrInst.Jump(ownedLabel));
        Emit(new IrInst.Label(copyLabel));
        Emit(new IrInst.StoreLocal(resultSlot, EmitRuntimeManagedTcoDeepCopy(sourceTemp, type)));
        Emit(new IrInst.Label(ownedLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    /// <summary>
    /// A copy of a result in the arena by its fixed copy-out, detached from the owned values the
    /// function releases after it; -1 when the type has none.
    /// </summary>
    // A result with a fixed copy-out (an optional integer, a list of scalars) is detached from the
    // function's owned values by that copy, whatever it was read out of.
    private bool HasFixedArenaCopyOut(TypeRef type)
        => Environment.GetEnvironmentVariable("GRC_NO_FIXEDCOPYRESULT") is null
            && (GetCallCopyOutKind(type, out _, out _) is CopyOutKind.DeepAdt or CopyOutKind.Shallow or CopyOutKind.List
                || IsScalarFieldVariant(type));

    // A variant every field of which is inline (an optional integer): the contract does not govern
    // it, since it owns no heap child, and a generic one has no fixed copy-out either, so the
    // normalization helper is what detaches it from a value it may have been read out of.
    private bool IsScalarFieldVariant(TypeRef type)
        => Prune(type) is TypeRef.TNamedType named
            && IsGeneralRcAdmissible(named)
            && named.Symbol.Constructors.All(constructor =>
                Enumerable.Range(0, constructor.Arity).All(index =>
                    CanArenaReset(Prune(InstantiateConstructorParameterType(constructor, index, named)))));

    private int EmitGeneralRcArenaResultCopy(int sourceTemp, TypeRef type)
    {
        CopyOutKind kind = GetCallCopyOutKind(type, out int sizeBytes, out IrInst.ListHeadCopyKind headCopy);
        if (kind == CopyOutKind.DeepAdt)
        {
            return EmitDeepCopy(sourceTemp, type);
        }

        if (kind is not (CopyOutKind.Shallow or CopyOutKind.List))
        {
            return -1;
        }

        int copyTemp = NewTemp();
        Emit(kind == CopyOutKind.Shallow
            ? new IrInst.CopyOutArena(copyTemp, sourceTemp, sizeBytes, RuntimeManaged: false, IrInst.CopyOutPurpose.IndependentClone)
            : new IrInst.CopyOutList(copyTemp, sourceTemp, headCopy, RuntimeManaged: false, IrInst.CopyOutPurpose.IndependentClone));
        return copyTemp;
    }

    /// <summary>
    /// Releases the owned values the loop iteration being closed holds, once every successor
    /// argument holds its own references: each is an inline value or a parameter placed on the
    /// reference-counted heap, or a parameter handed on unchanged. Otherwise they are left to leak,
    /// since an arena successor may name them without a reference.
    /// </summary>
    private void EmitGeneralRcBackEdgeDrops(PendingTcoReset info, bool successorsNormalized)
    {
        if (!_generalRcBackEdgeSlots.TryGetValue(_generalRcBackEdgeId, out List<GeneralRcOwnedSlot>? capturedSlots)
            || capturedSlots.Count == 0)
        {
            return;
        }

        EmitGeneralRcPredecessorDrops(info, capturedSlots.Where(owned => owned.Predecessor).ToList(), successorsNormalized);
        List<GeneralRcOwnedSlot> iterationSlots = capturedSlots.Where(owned => !owned.Predecessor).ToList();

        // A parameter placed on the reference-counted heap owns its successor only on the path that
        // normalized it; any other path may store an arena successor that borrows the owned values.
        // A loop-invariant parameter handed on as it is predates the iteration's owned values.
        bool[] parameterParts = _generalRcBackEdgeParameterParts.GetValueOrDefault(_generalRcBackEdgeId, []);

        bool successorsOwnTheirParts = Enumerable.Range(0, info.ArgTypes.Length).All(index =>
            CanArenaReset(Prune(info.ArgTypes[index]))
            || info.PassThrough[index]
            || (info.Tco.TmcShapePresent && index < parameterParts.Length && parameterParts[index])
            || (successorsNormalized && info.ParamPlacements[index]?.Representation == TcoPlacementRepresentation.RuntimeRc));
        if (successorsOwnTheirParts)
        {
            EmitGeneralRcOwnedSlotDrops(iterationSlots);
            return;
        }

        List<GeneralRcOwnedSlot> taken = successorsNormalized ? OwnedSlotsTakenBySuccessors(info, iterationSlots) : [];
        List<GeneralRcOwnedSlot> released = iterationSlots
            .Where(owned => taken.Contains(owned) || NoSuccessorCanHoldPartOf(info, owned.Type, parameterParts, successorsNormalized, useParameterParts: false))
            .ToList();
        EmitGeneralRcOwnedSlotDrops(released);

        // A value this iteration produced is out of reach of the successors that are the loop's own
        // parameters (or parts of them): they predate it. A value an earlier iteration left in its
        // slot may have become one of them, so that release is taken only when the slot is fresh.
        foreach (GeneralRcOwnedSlot owned in iterationSlots.Where(owned => !released.Contains(owned)))
        {
            if (owned.FreshSlot < 0)
            {
                continue;
            }

            if (NoSuccessorCanHoldPartOf(info, owned.Type, parameterParts, successorsNormalized, useParameterParts: true))
            {
                int freshTemp = NewTemp();
                Emit(new IrInst.LoadLocal(freshTemp, owned.FreshSlot));
                string staleLabel = NewLabel("general_rc_owned_stale");
                Emit(new IrInst.JumpIfFalse(freshTemp, staleLabel));
                EmitGeneralRcOwnedSlotDrops([owned]);
                Emit(new IrInst.Label(staleLabel));
            }

            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            Emit(new IrInst.StoreLocal(owned.FreshSlot, zeroTemp));
        }
    }

    // An earlier iteration's value is released once no successor can still name it: each successor
    // is inline, a plain read of an owned slot (a result holding its own references), a parameter
    // normalized onto the reference-counted heap, a parameter only ever walked or handed on by every
    // self call (it holds nothing newer than the loop's entry), an expression naming neither the
    // value's binders nor a parameter that may hold a part of it, or of a type that cannot hold its
    // parts. A parameter handed on unchanged at this edge alone may be that very value, so it counts
    // only by its type.
    private void EmitGeneralRcPredecessorDrops(PendingTcoReset info, List<GeneralRcOwnedSlot> predecessors, bool successorsNormalized)
    {
        int[] argumentSlots = _generalRcBackEdgeArgumentPartSlots.GetValueOrDefault(_generalRcBackEdgeId, []);
        foreach (GeneralRcOwnedSlot predecessor in predecessors)
        {
            List<TypeRef> parts = PredecessorPartTypes(predecessor.Type);
            bool[] unnamed = SuccessorsNamingNoPartOf(info, predecessor, successorsNormalized);
            bool unreachable = Enumerable.Range(0, info.ArgTypes.Length).All(index =>
                CanArenaReset(Prune(info.ArgTypes[index]))
                || (index < argumentSlots.Length && argumentSlots[index] >= 0)
                || SuccessorPredatesTheLoop(info, index)
                || (successorsNormalized && info.ParamPlacements[index]?.Representation == TcoPlacementRepresentation.RuntimeRc)
                || (index < unnamed.Length && unnamed[index])
                || parts.TrueForAll(part => !CallResultMayContainArgumentType(info.ArgTypes[index], part, new HashSet<string>(StringComparer.Ordinal))));
            if (unreachable)
            {
                EmitGeneralRcOwnedSlotDrops([predecessor]);
            }
        }
    }

    // The parts of an earlier iteration's value a successor could hold without a reference of its
    // own. A string inside the value is retained wherever it is kept, so a successor holding one of
    // the value's strings keeps it alive by itself; a value that is itself a string stays a part.
    private List<TypeRef> PredecessorPartTypes(TypeRef type)
    {
        List<TypeRef> parts = HeapPartTypes(type);
        return Environment.GetEnvironmentVariable("GRC_NO_STRINGPARTS") is null && Prune(type) is not TypeRef.TStr
            ? parts.Where(part => Prune(part) is not TypeRef.TStr).ToList()
            : parts;
    }

    // Per successor, whether its expression names nothing that can hold the predecessor's value or
    // a part of it: none of the names bound from the value (its binders and the names derived from
    // them), and none of the parameters a self call may hand such a name, or a part of one, whole
    // or through another parameter. A parameter normalized onto the reference-counted heap on this
    // path holds references of its own, so nothing reaches a successor through it. Claimed only
    // when the value was bound to names and the body's parameter flow was followed.
    private bool[] SuccessorsNamingNoPartOf(PendingTcoReset info, GeneralRcOwnedSlot predecessor, bool successorsNormalized)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_PREDECESSORNAMES") is not null
            || predecessor.Binders is not { Count: > 0 } binders
            || info.Tco.SelfCallParameterFlow is not { } flow
            || info.Tco.Body is not { } body)
        {
            return [];
        }

        List<string> parameters = info.Tco.ParamNames;
        FlowCallPolicy policy = info.Tco.FlowPolicy;
        HashSet<string> named = CollectDerivedNames(body, binders, parameters, policy);
        HashSet<int> holders = CollectSelfCallPositionsMentioning(info.Tco.SelfName, parameters, body, named, policy);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((int position, HashSet<int> sources) in flow)
            {
                bool normalized = successorsNormalized
                    && position < info.ParamPlacements.Length
                    && info.ParamPlacements[position]?.Representation == TcoPlacementRepresentation.RuntimeRc;
                if (!normalized && !holders.Contains(position) && sources.Any(holders.Contains))
                {
                    holders.Add(position);
                    changed = true;
                }
            }
        }

        foreach (int holder in holders)
        {
            if (holder < parameters.Count)
            {
                named.Add(parameters[holder]);
            }
        }

        return info.ArgExpressions
            .Select(argument => !named.Any(name => FlowMentionsName(argument, name, parameters, policy, 0)))
            .ToArray();
    }

    // A successor that is the parameter's own consumed tail, where every self call passes that tail,
    // or the parameter itself, where every self call hands it on unchanged: only parts of the value
    // the loop was entered with, which no iteration's owned value can be.
    private static bool SuccessorPredatesTheLoop(PendingTcoReset info, int index)
        => Environment.GetEnvironmentVariable("GRC_NO_PREDECESSORENTRYPARTS") is null
            && index < info.ParamSlots.Length
            && info.Tco.ParamFacts.TryGetValue(info.ParamSlots[index], out TcoParamStaticFacts? facts)
            && (facts.LoopInvariant || (facts.ConsumedListTail && info.ConsumedListTail[index]));

    // Whether every successor either owns its parts or is of a type that cannot hold any heap part
    // of an owned value of this type (a list of integer pairs never holds a record of strings): the
    // iteration's reference to such a value is released whatever the other successors borrow.
    private bool NoSuccessorCanHoldPartOf(PendingTcoReset info, TypeRef ownedType, bool[] parameterParts, bool successorsNormalized, bool useParameterParts)
    {
        List<TypeRef> parts = HeapPartTypes(ownedType);
        bool[] strictParts = _generalRcBackEdgeStrictParameterParts.GetValueOrDefault(_generalRcBackEdgeId, []);
        return Enumerable.Range(0, info.ArgTypes.Length).All(index =>
            CanArenaReset(Prune(info.ArgTypes[index]))
            || info.PassThrough[index]
            || (info.Tco.TmcShapePresent && index < parameterParts.Length && parameterParts[index])
            || (useParameterParts && index < strictParts.Length && strictParts[index])
            || (successorsNormalized && info.ParamPlacements[index]?.Representation == TcoPlacementRepresentation.RuntimeRc)
            || parts.TrueForAll(part => !CallResultMayContainArgumentType(info.ArgTypes[index], part, new HashSet<string>(StringComparer.Ordinal))));
    }

    // The heap types reachable inside a value of this type, itself included: every part a successor
    // could have been handed. Named types are expanded once each.
    private List<TypeRef> HeapPartTypes(TypeRef type)
    {
        var parts = new List<TypeRef>();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypeRef>();
        pending.Push(type);
        while (pending.Count > 0)
        {
            TypeRef pruned = Prune(pending.Pop());
            if (CanArenaReset(pruned))
            {
                continue;
            }

            parts.Add(pruned);
            switch (pruned)
            {
                case TypeRef.TList list:
                    pending.Push(list.Element);
                    break;
                case TypeRef.TTuple tuple:
                    foreach (TypeRef element in tuple.Elements)
                    {
                        pending.Push(element);
                    }

                    break;
                case TypeRef.TNamedType named when expanded.Add(Pretty(named)):
                    foreach (ConstructorSymbol constructor in named.Symbol.Constructors)
                    {
                        for (int index = 0; index < constructor.Arity; index++)
                        {
                            pending.Push(InstantiateConstructorParameterType(constructor, index, named));
                        }
                    }

                    break;
            }
        }

        return parts;
    }

    // The owned values a reference-counted parameter's successor is a plain read of: its
    // normalization took a reference of its own (the value is no fresh result it would move), so
    // the iteration's reference is released whatever the other successors borrow.
    private List<GeneralRcOwnedSlot> OwnedSlotsTakenBySuccessors(PendingTcoReset info, List<GeneralRcOwnedSlot> iterationSlots)
    {
        int[] argumentSlots = _generalRcBackEdgeArgumentSlots.GetValueOrDefault(_generalRcBackEdgeId, []);
        HashSet<int> taken = [];
        for (int index = 0; index < argumentSlots.Length && index < info.ArgTypes.Length; index++)
        {
            if (argumentSlots[index] >= 0
                && !info.PassThrough[index]
                && !info.RuntimeManagedArgResults[index]
                && info.ParamPlacements[index]?.Representation == TcoPlacementRepresentation.RuntimeRc)
            {
                taken.Add(argumentSlots[index]);
            }
        }

        return iterationSlots.Where(owned => taken.Contains(owned.Slot)).ToList();
    }

    // The program's entry holds owned call results like any function (a fresh argument of the
    // trailing pipe, say) and releases them at its end, as a function does at its return, when its
    // value cannot point into them: a scalar, or a value of a type whose constructors all carry
    // nothing.
    private void ReleaseEntryGeneralRcOwnedSlots(TypeRef resultType)
    {
        TypeRef pruned = Prune(resultType);
        if (GeneralRcEnabled
            && Environment.GetEnvironmentVariable("GRC_NO_ENTRYRELEASE") is null
            && (CanArenaReset(pruned)
                || pruned is TypeRef.TNamedType { Symbol.Constructors: { Count: > 0 } constructors } && constructors.All(constructor => constructor.Arity == 0)))
        {
            EmitGeneralRcOwnedSlotDrops(_generalRcOwnedSlots);
        }
    }

    private void EmitGeneralRcOwnedSlotDrops(List<GeneralRcOwnedSlot> ownedSlots)
    {
        foreach (GeneralRcOwnedSlot owned in ownedSlots)
        {
            int valueTemp = NewTemp();
            Emit(new IrInst.LoadLocal(valueTemp, owned.Slot));
            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int presentTemp = NewTemp();
            Emit(new IrInst.CmpIntNe(presentTemp, valueTemp, zeroTemp));
            string skipLabel = NewLabel("general_rc_owned_absent");
            Emit(new IrInst.JumpIfFalse(presentTemp, skipLabel));
            int referenceCountedTemp = NewTemp();
            Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, valueTemp));
            Emit(new IrInst.JumpIfFalse(referenceCountedTemp, skipLabel));
            EmitRuntimeManagedChildDrop(valueTemp, owned.Type);
            Emit(new IrInst.StoreLocal(owned.Slot, zeroTemp));
            Emit(new IrInst.Label(skipLabel));
        }
    }

    // A record loop parameter only the normalization helper expresses (a state threaded through a
    // loop) is placed on the reference-counted heap like any other: normalized at entry and at the
    // back edge, its predecessor released. So is a monomorphic variant of these types (a type
    // expression, a syntax node): left in the arena it can never be placed, and a parameter that
    // can never be placed blocks every sibling that may hold it, the threaded state included. A
    // generic collection (a map, a tree) stays with the in-place specializations that rebuild it
    // in their own persistent region.
    private bool IsGeneralRcTcoParameterType(TypeRef.TNamedType named) =>
        GeneralRcEnabled
        && Environment.GetEnvironmentVariable("GRC_NO_TCO") is null
        && IsGeneralRcNamedType(named)
        && ((named.Symbol.Constructors.Count == 1 && named.Symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0)
            || IsGeneralRcTcoVariantParameterType(named));

    private bool IsGeneralRcTcoVariantParameterType(TypeRef.TNamedType named) =>
        named.TypeArgs.Count == 0
        && Environment.GetEnvironmentVariable("GRC_NO_VARIANTPARAM") is null
        && !(named.Symbol.Constructors.Count == 1 && named.Symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0);

    // A variant parameter is placed only where the shape analysis saw the loop's self-calls: a
    // tail-modulo-cons function's self-call sits inside a cons and is never classified, and its
    // unchanged parameters are embedded in the cells it produces, which a release at the loop's exit
    // would leave dangling. A parameter every self-call hands on unchanged is left as it is too.
    private bool IsUnplaceableGeneralRcVariantParameter(TcoContext tco, TcoParamStaticFacts facts, TypeRef parameterType) =>
        parameterType is TypeRef.TNamedType named
        && IsGeneralRcTcoVariantParameterType(named)
        && IsGeneralRcNamedType(named)
        && (tco.TmcShapePresent || facts.LoopInvariant);

    // A list loop parameter whose elements are of the contract's types (an accumulator of call
    // results) is placed on the reference-counted heap too, so each iteration's owned results can be
    // released once the successor list holds its own references to them.
    private bool IsGeneralRcTcoListElement(TypeRef elementType) =>
        GeneralRcEnabled && Environment.GetEnvironmentVariable("GRC_NO_LISTS") is null && Prune(elementType) is TypeRef.TNamedType named && IsGeneralRcNamedType(named);

    // The loop parameters each loop hands to a specializable rewriter (`Map.set`, say): the reuse
    // specialization keeps those in its persistent region, which a reference-counted placement
    // would take away.
    private readonly Dictionary<TcoContext, HashSet<string>> _generalRcSpecializationAccumulators =
        new(ReferenceEqualityComparer.Instance);

    private void RecordGeneralRcSpecializationAccumulators(Expr.Lambda lam, TcoContext tco)
    {
        var paramNames = new HashSet<string>(tco.ParamNames, StringComparer.Ordinal) { lam.ParamName };
        var accumulators = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectSpecializableCallArgs(lam.Body, paramNames, accumulators);
        _generalRcSpecializationAccumulators[tco] = new HashSet<string>(accumulators.Keys, StringComparer.Ordinal);
    }

    private bool IsGeneralRcSpecializationAccumulator(TcoContext tco, TcoParamStaticFacts facts, TypeRef parameterType) =>
        parameterType is TypeRef.TNamedType named
        && IsGeneralRcTcoParameterType(named)
        && _generalRcSpecializationAccumulators.TryGetValue(tco, out HashSet<string>? accumulators)
        && accumulators.Contains(facts.ParamName);

    private readonly Dictionary<string, string> _generalRcDropperLabels = new(StringComparer.Ordinal);

    /// <summary>
    /// Releases a reference-counted value of a type only the normalization helper expresses,
    /// through a dropper synthesized once per type: on the last reference it releases every owned
    /// field of the live constructor, then the cell.
    /// </summary>
    private void EmitGeneralRuntimeManagedAdtDrop(int valueTemp, TypeRef.TNamedType named)
    {
        string key = Pretty(named);
        if (!_generalRcDropperLabels.TryGetValue(key, out string? label))
        {
            label = $"__rcdrop_general_{_nextLambdaId++}";
            // Registered before the body, so a field of the same type calls this dropper.
            _generalRcDropperLabels[key] = label;
            SynthesizedBodyState saved = BeginSynthesizedBody();
            EmitGeneralRuntimeManagedAdtDropperBody(named);
            AddFunction(
                new IrFunction(
                    Label: label,
                    Instructions: new List<IrInst>(_inst),
                    LocalCount: _nextLocalSlot,
                    TempCount: _nextTempSlot,
                    HasEnvAndArgParams: true),
                new IrFunctionOrigin(
                    label,
                    IrFunctionOriginKind.RuntimeManagedAdtDropper,
                    CompilerOwner: new CompilerFunctionOwner(CompilerFunctionOwnerKind.Type, key),
                    StableDiscriminator: key));
            RestoreEnclosingBodyState(saved);
        }

        int envTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(envTemp, 0));
        Emit(new IrInst.CallKnown(NewTemp(), label, envTemp, valueTemp));
    }

    private void EmitGeneralRuntimeManagedAdtDropperBody(TypeRef.TNamedType named)
    {
        NewLocal(); // slot 0: env (implicit)
        int argSlot = NewLocal(); // slot 1: value (implicit)
        int valueTemp = NewTemp();
        Emit(new IrInst.LoadLocal(valueTemp, argSlot));

        string sharedLabel = NewLabel("rcdrop_general_shared");
        int uniqueTemp = NewTemp();
        Emit(new IrInst.RcIsUnique(uniqueTemp, valueTemp));
        Emit(new IrInst.JumpIfFalse(uniqueTemp, sharedLabel));

        TypeSymbol symbol = named.Symbol;
        int tagTemp = EmitAdtTag(valueTemp, symbol);
        List<(long Tag, string Label)> cases = symbol.Constructors
            .Select(constructor => ((long)GetConstructorTag(constructor), NewLabel("rcdrop_general_ctor")))
            .ToList();
        Emit(new IrInst.SwitchTag(tagTemp, cases, sharedLabel));
        for (int i = 0; i < symbol.Constructors.Count; i++)
        {
            ConstructorSymbol constructor = symbol.Constructors[i];
            Emit(new IrInst.Label(cases[i].Label));
            bool tagless = IsTaglessConstructor(constructor);
            for (int index = 0; index < constructor.Arity; index++)
            {
                TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, index, named));
                if (CanArenaReset(fieldType))
                {
                    continue;
                }

                int childTemp = NewTemp();
                Emit(new IrInst.GetAdtField(childTemp, valueTemp, index, tagless));
                EmitRuntimeManagedChildDrop(childTemp, fieldType);
            }

            Emit(new IrInst.Jump(sharedLabel));
        }

        Emit(new IrInst.Label(sharedLabel));
        Emit(new IrInst.RcDrop(valueTemp, RuntimeManagedAdtTypeName(named), RuntimeManaged: true));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(resultTemp, 0));
        Emit(new IrInst.Return(resultTemp));
    }

    /// <summary>
    /// Emits a call to the normalization helper of <paramref name="valueType"/>, a named type only
    /// that helper expresses, and returns its result, an owned reference-counted value. A copy that
    /// must also release the source's children is a move the helper does not perform.
    /// </summary>
    private int EmitRuntimeManagedAdtNormalizerCall(int sourceTemp, TypeRef valueType, bool releaseSourceChildren)
    {
        if (releaseSourceChildren
            || valueType is not TypeRef.TNamedType named
            || !(NeedsRuntimeManagedAdtNormalizer(named) || IsInlineCopiedContractRecord(named)))
        {
            throw new InvalidOperationException($"Unsupported runtime-managed TCO aggregate: {Pretty(valueType)}.");
        }

        string label = SynthesizeRuntimeManagedAdtNormalizer(named);
        int envTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(envTemp, 0));
        int resultTemp = NewTemp();
        Emit(new IrInst.CallKnown(resultTemp, label, envTemp, sourceTemp));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private string SynthesizeRuntimeManagedAdtNormalizer(TypeRef.TNamedType named)
    {
        string key = Pretty(named);
        if (_runtimeManagedAdtNormalizerLabels.TryGetValue(key, out string? existing))
        {
            return existing;
        }

        string label = $"__rcnorm_{_nextLambdaId++}";
        // Registered before the body, so a field of the same type calls this helper.
        _runtimeManagedAdtNormalizerLabels[key] = label;

        SynthesizedBodyState saved = BeginSynthesizedBody();
        EmitRuntimeManagedAdtNormalizerBody(named);
        AddFunction(
            new IrFunction(
                Label: label,
                Instructions: new List<IrInst>(_inst),
                LocalCount: _nextLocalSlot,
                TempCount: _nextTempSlot,
                HasEnvAndArgParams: true),
            new IrFunctionOrigin(
                label,
                IrFunctionOriginKind.RuntimeManagedAdtNormalizer,
                CompilerOwner: new CompilerFunctionOwner(
                    CompilerFunctionOwnerKind.Type,
                    key),
                StableDiscriminator: key));
        RestoreEnclosingBodyState(saved);
        return label;
    }

    // A reference-counted value is retained whole; an arena or static one is copied cell by cell,
    // each constructor's owned fields normalized in turn.
    private void EmitRuntimeManagedAdtNormalizerBody(TypeRef.TNamedType named)
    {
        NewLocal(); // slot 0: env (implicit)
        int argSlot = NewLocal(); // slot 1: the value (implicit)
        int sourceTemp = NewTemp();
        Emit(new IrInst.LoadLocal(sourceTemp, argSlot));
        int resultTemp = EmitReferenceOrCopy(sourceTemp, () => EmitRuntimeManagedAdtCopy(sourceTemp, named));
        Emit(new IrInst.Return(resultTemp));
    }

    private int EmitRuntimeManagedAdtCopy(int sourceTemp, TypeRef.TNamedType named)
    {
        TypeSymbol symbol = named.Symbol;
        if (symbol.Constructors.Count == 1)
        {
            return EmitRuntimeManagedConstructorCopy(sourceTemp, named, symbol.Constructors[0]);
        }

        int resultSlot = NewLocal();
        int tagTemp = EmitAdtTag(sourceTemp, symbol);
        List<(long Tag, string Label)> cases = symbol.Constructors
            .Select(constructor => ((long)GetConstructorTag(constructor), NewLabel("rcnorm_ctor")))
            .ToList();
        string endLabel = NewLabel("rcnorm_end");
        Emit(new IrInst.SwitchTag(tagTemp, cases, cases[0].Label));
        for (int i = 0; i < symbol.Constructors.Count; i++)
        {
            Emit(new IrInst.Label(cases[i].Label));
            int branchTemp = EmitRuntimeManagedConstructorCopy(sourceTemp, named, symbol.Constructors[i]);
            Emit(new IrInst.StoreLocal(resultSlot, branchTemp));
            Emit(new IrInst.Jump(endLabel));
        }

        Emit(new IrInst.Label(endLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }

    private int EmitRuntimeManagedConstructorCopy(
        int sourceTemp,
        TypeRef.TNamedType named,
        ConstructorSymbol constructor)
    {
        int resultTemp = NewTemp();
        Emit(new IrInst.CopyOutArena(
            resultTemp,
            sourceTemp,
            AdtAllocationSizeBytes(constructor),
            RuntimeManaged: true,
            IrInst.CopyOutPurpose.RcNormalization));
        bool tagless = IsTaglessConstructor(constructor);
        for (int index = 0; index < constructor.Arity; index++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(constructor, index, named));
            if (CanArenaReset(fieldType))
            {
                continue;
            }

            int childTemp = NewTemp();
            Emit(new IrInst.GetAdtField(childTemp, sourceTemp, index, tagless));
            int normalizedChild = EmitRuntimeManagedTcoDeepCopy(childTemp, fieldType);
            Emit(new IrInst.SetAdtField(resultTemp, index, normalizedChild, tagless));
        }

        MarkRuntimeManagedTemp(resultTemp);
        return resultTemp;
    }
}
