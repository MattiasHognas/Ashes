using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // The taken-over record (GRC_NO_TAKENOVERRECORD), for a list whose element tuple's first
    // field has a resolved type T. A function F that hands an owned list S (a call result kept in an
    // owned slot) to a callee G, where G's result holds S only as whole unshared elements, frees S's
    // cells at the back edge or return that would otherwise keep S; the elements keep the reference
    // the result's arena cells now own. A caller that knows the concrete type releases those
    // references behind its copy of F's result, or after its last use of it, once the record is
    // proven: every value F's result can hold at a T position is one of those elements or comes from
    // an argument that holds nothing (the empty list).
    private static bool TakenOverRecordRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_TAKENOVERRECORD") is null;

    private readonly HashSet<string> _takenOverRecordLabels = new(StringComparer.Ordinal);
    private readonly HashSet<Expr> _spineFreedSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GeneralRcOwnedSlot, Expr> _ownedSlotSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, (Expr Root, int Index)> _argumentConsumers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, TypeRef> _argumentTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, FunctionOwnershipSummary?> _argumentConsumerSummaries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (int Count, string Example)> _takenOverDeclines = new(StringComparer.Ordinal);

    private void NoteTakenOverOwnedSlotSource(GeneralRcOwnedSlot owned)
    {
        if (TakenOverRecordRuleEnabled && _currentSourceExpr is Expr.Call call)
        {
            _ownedSlotSources[owned] = call;
        }
    }

    private void NoteTakenOverArgument(Expr rootExpr, Expr argument, int argumentIndex, TypeRef argumentType)
    {
        if (!TakenOverRecordRuleEnabled)
        {
            return;
        }

        _argumentConsumers[argument] = (rootExpr, argumentIndex);
        _argumentTypes[argument] = argumentType;
        _argumentConsumerSummaries[argument] = GetOwnershipSummaryForCallRoot(rootExpr);
    }

    private string SourceNameOfLabel(string label)
        => _functionNameByLabel.TryGetValue(label, out string? name) ? $"{name} ({label})" : label;

    private void DeclineTakenOver(string reason, string example)
    {
        _takenOverDeclines[reason] = _takenOverDeclines.TryGetValue(reason, out var seen)
            ? (seen.Count + 1, seen.Example)
            : (1, example);
    }

    // Obligation 1: the owned list's consumer holds it only as whole, unshared elements.
    private bool OwnedListHandedAsElementsOnly(GeneralRcOwnedSlot owned, string function)
    {
        if (!_ownedSlotSources.TryGetValue(owned, out Expr? source)
            || !_argumentConsumers.TryGetValue(source, out var consumer))
        {
            return false;
        }

        if (_argumentConsumerSummaries.GetValueOrDefault(source) is not { } summary
            || consumer.Index >= summary.Parameters.Count
            || !summary.ResultHoldsOnlyElementsOf(summary.Parameters[consumer.Index]))
        {
            DeclineTakenOver("consumer-keeps-more-than-elements", $"{SourceNameOfLabel(function)} via {(_argumentConsumers.TryGetValue(source, out var c) ? c.Root.ToString() : "?")}");
            return false;
        }

        return true;
    }

    private bool IsTakeOverCandidateList(TypeRef type)
        => Prune(type) is TypeRef.TList list
            && !ContainsUnresolvedLayoutType(Prune(list.Element), [])
            && !CanArenaReset(Prune(list.Element));

    private void EmitTakeOverSpineRelease(GeneralRcOwnedSlot owned, string label)
    {
        string skipLabel = NewLabel("taken_over_skip");
        if (owned.FreshSlot >= 0)
        {
            int freshTemp = NewTemp();
            Emit(new IrInst.LoadLocal(freshTemp, owned.FreshSlot));
            Emit(new IrInst.JumpIfFalse(freshTemp, skipLabel));
        }

        int valueTemp = NewTemp();
        Emit(new IrInst.LoadLocal(valueTemp, owned.Slot));
        EmitSkipUnlessPresent(valueTemp, skipLabel);
        EmitSkipUnlessReferenceCounted(valueTemp, skipLabel);
        EmitRuntimeManagedListSpineDrop(valueTemp);
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.StoreLocal(owned.Slot, zeroTemp));
        Emit(new IrInst.Label(skipLabel));
        _takenOverRecordLabels.Add(label);
        _spineFreedSources.Add(_ownedSlotSources[owned]);
    }

    // The iteration's owned slots left after the ones whose list cells the taken-over record frees.
    private List<GeneralRcOwnedSlot> KeptPastTakenOverRecord(PendingTcoReset info, IEnumerable<GeneralRcOwnedSlot> slots)
        => [.. slots.Where(owned => !TryTakeOverAtBackEdge(info, owned))];

    private bool TryTakeOverAtBackEdge(PendingTcoReset info, GeneralRcOwnedSlot owned)
    {
        if (!TakenOverRecordRuleEnabled
            || owned.FreshSlot < 0
            || !IsTakeOverCandidateList(owned.Type)
            || !info.ArgTypes.Any(type => ContainsUnresolvedLayoutType(Prune(type), []))
            || info.Tco.SelfLabel is not { Length: > 0 } label
            || !OwnedListHandedAsElementsOnly(owned, label))
        {
            return false;
        }

        EmitTakeOverSpineRelease(owned, label);
        int clearTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(clearTemp, 0));
        Emit(new IrInst.StoreLocal(owned.FreshSlot, clearTemp));
        return true;
    }

    // A recursive group member has no back edge: the owned list is kept at the return instead.
    private void TryTakeOverAtReturn(string label, TypeRef resultType, List<GeneralRcOwnedSlot> kept)
    {
        if (!TakenOverRecordRuleEnabled || !ContainsUnresolvedLayoutType(Prune(resultType), []))
        {
            return;
        }

        foreach (GeneralRcOwnedSlot owned in kept)
        {
            if (owned.Loop is null && IsTakeOverCandidateList(owned.Type) && OwnedListHandedAsElementsOnly(owned, label))
            {
                EmitTakeOverSpineRelease(owned, label);
            }
        }
    }

    private readonly Dictionary<int, PendingTakenOverSettle> _pendingTakenOverSettles = [];
    private bool _takenOverSettleRound;
    private int _nextTakenOverSettleId;
    private readonly HashSet<Expr> _settledBehindCopy = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Expr.Call Call, int Temp, TypeRef Type)> _takenOverHeldResults = [];

    private sealed record PendingTakenOverSettle(Expr.Call Call, string Label, TypeRef.TTuple? Tuple, FuncKey? Callee)
    {
        // The function whose let scope ends here, when the site is a let's end: the record is settled
        // only where that function's result is out of the let-bound value's reach.
        public IReadOnlyList<string> EnclosingLabels { get; init; } = [];

        public bool LetScoped { get; init; }

        // The own-field record's result type, when the settlement releases an arena result cell's own fields
        // rather than the list record's tuple fields.
        public TypeRef.TNamedType? OwnField { get; init; }
    }

    private static List<Expr> CallArguments(Expr.Call call, out Expr root)
    {
        List<Expr> arguments = [];
        root = call;
        while (root is Expr.Call inner)
        {
            arguments.Insert(0, inner.Arg);
            root = inner.Func;
        }

        return arguments;
    }

    private bool TryNewTakenOverSettle(Expr.Call call, TypeRef resultType, out PendingTakenOverSettle? settle)
    {
        settle = null;
        TypeRef.TTuple? tuple = null;
        TypeRef.TNamedType? ownField = null;
        if (Prune(resultType) is TypeRef.TList { Element: var element }
            && Prune(element) is TypeRef.TTuple elementTuple
            && !ContainsUnresolvedLayoutType(Prune(elementTuple.Elements[0]), [])
            && !CanArenaReset(Prune(elementTuple.Elements[0])))
        {
            tuple = elementTuple;
        }
        else if (OwnFieldRecordRuleEnabled && OwnFieldResultType(resultType, out TypeRef.TNamedType named))
        {
            ownField = named;
        }
        else
        {
            return false;
        }

        _ = CallArguments(call, out Expr root);
        if (!TryResolveKnownFunctionLabel(root, out string label))
        {
            return false;
        }

        FuncKey? key = _functionKeyByLabel.TryGetValue(label, out FuncKey found) ? found : null;
        settle = new PendingTakenOverSettle(call, label, tuple, key) { OwnField = ownField };
        return true;
    }

    // Behind a caller's copy of a call's arena result: the copy retained what it holds, so the
    // references the arena cells took over are released from the original. Whether the callee hands
    // a record over is known only once every back edge is emitted, so the site is a placeholder.
    private void TrySettleTakenOverBehindCopy(TypeRef callResultType, int arenaTemp)
    {
        if (TakenOverRecordRuleEnabled
            && _currentSourceExpr is Expr.Call call
            && TryNewTakenOverSettle(call, callResultType, out PendingTakenOverSettle? settle))
        {
            _settledBehindCopy.Add(call);
            int id = _nextTakenOverSettleId++;
            _pendingTakenOverSettles[id] = settle!;
            AnchorTakenOverSettle(_inst.Count > 0 ? _inst[^1] : null, id, arenaTemp);
        }
    }

    // A call result the caller holds without copying it out: settled right after the call that last
    // uses it as an argument when that call's result is a scalar that cannot hold it.
    private (int Temp, TypeRef Type) NoteTakenOverCallResult(Expr.Call call, (int Temp, TypeRef Type) lowered)
    {
        if (!TakenOverRecordRuleEnabled)
        {
            return lowered;
        }

        SettleHeldTakenOverResultsConsumedBy(call, lowered.Type);
        if (!_settledBehindCopy.Contains(call)
            && ((Prune(lowered.Type) is TypeRef.TList { Element: var element } && Prune(element) is TypeRef.TTuple)
                || (OwnFieldRecordRuleEnabled && OwnFieldResultType(lowered.Type, out _))))
        {
            _takenOverHeldResults.Add((call, lowered.Temp, lowered.Type));
        }

        return lowered;
    }

    private void SettleHeldTakenOverResultsConsumedBy(Expr.Call outer, TypeRef outerType)
    {
        if (!CanArenaReset(Prune(outerType)))
        {
            return;
        }

        for (int index = _takenOverHeldResults.Count - 1; index >= 0; index--)
        {
            (Expr.Call call, int temp, TypeRef heldType) = _takenOverHeldResults[index];
            if (!ReferenceEquals(StripOrSelf(outer.Arg), call))
            {
                continue;
            }

            _takenOverHeldResults.RemoveAt(index);
            if (TryNewTakenOverSettle(call, heldType, out PendingTakenOverSettle? settle))
            {
                int id = _nextTakenOverSettleId++;
                _pendingTakenOverSettles[id] = settle!;
                AnchorTakenOverSettle(_inst.FindLast(candidate => candidate is IrInst.CallClosure or IrInst.CallKnown), id, temp);
            }
        }
    }

    // At the end of a let scope whose value is such a held call result, the holder is the let: the record
    // is settled there when nothing the function returns can reach the value (a generic holder too, since
    // only the record's own type and the tuple's first field are read).
    private void SettleLetScopedTakenOver(Expr.Let binding)
    {
        if (!TakenOverRecordRuleEnabled || StripOrSelf(binding.Value) is not Expr.Call valueCall)
        {
            return;
        }

        int index = _takenOverHeldResults.FindIndex(held => ReferenceEquals(held.Call, valueCall));
        if (index < 0 || !_loweringFunctionLabels.TryPeek(out string? enclosingLabel))
        {
            return;
        }

        (Expr.Call call, int temp, TypeRef heldType) = _takenOverHeldResults[index];
        _takenOverHeldResults.RemoveAt(index);
        if (TryNewTakenOverSettle(call, heldType, out PendingTakenOverSettle? settle))
        {
            int id = _nextTakenOverSettleId++;
            _pendingTakenOverSettles[id] = settle! with
            {
                EnclosingLabels = [.. _loweringFunctionLabels],
                LetScoped = true,
            };
            int settled = LetOwnerLoadForSettle(binding, temp);
            AnchorTakenOverSettle(_inst.Count > 0 ? _inst[^1] : null, id, settled);
        }
    }

    // The walk at a let's end reads the value through a load of the let's own slot: lifetime placement
    // follows the owner's loads, so the owner's release lands after the walk instead of after the
    // value's last use in the body, ahead of a walk over freed cells (`GRC_NO_SETTLEOWNERLOAD`).
    private int LetOwnerLoadForSettle(Expr.Let binding, int temp)
    {
        if (Environment.GetEnvironmentVariable("GRC_NO_SETTLEOWNERLOAD") is not null
            || Lookup(binding.Name) switch { Binding.Local local => local.Slot, Binding.Scheme scheme => scheme.Slot, _ => (int?)null } is not int slot)
        {
            return temp;
        }

        int ownerLoad = NewTemp();
        Emit(new IrInst.LoadLocal(ownerLoad, slot));
        return ownerLoad;
    }

    // The function being lowered: by its label, or by its source name when exactly one function has it.
    private FuncKey? FuncKeyOfLoweringLabel(string label)
    {
        if (_functionKeyByLabel.TryGetValue(label, out FuncKey key))
        {
            return key;
        }

        if (!_functionNameByLabel.TryGetValue(label, out string? name))
        {
            return null;
        }

        List<FuncKey> named = [.. _maKeyName.Where(entry => string.Equals(entry.Value, name, StringComparison.Ordinal)).Select(entry => entry.Key)];
        return named.Count == 1 ? named[0] : null;
    }

    private bool DeclineLetHolder(PendingTakenOverSettle settle, string reason)
    {
        DeclineTakenOver(reason, SourceNameOfLabel(settle.Label));
        return false;
    }

    // A let-scoped settlement: the enclosing function's record reach, with the let's value as its own
    // value, names nothing of it.
    private bool LetScopedSettleOutOfReach(PendingTakenOverSettle settle)
    {
        if (settle.EnclosingLabels.Select(FuncKeyOfLoweringLabel).FirstOrDefault(key => key is not null) is not { } enclosing)
        {
            return !settle.LetScoped || DeclineLetHolder(settle, "let-holder-function-unknown");
        }

        HashSet<Expr> cut = new(ReferenceEqualityComparer.Instance) { settle.Call };
        if (ComputeRecordReach(enclosing, cut) is not { } reach
            || (reach.Causes & ~ResultReachCause.InternalSharing) != ResultReachCause.None
            || reach.Counts.Keys.Any(token => token.StartsWith("#S", StringComparison.Ordinal)))
        {
            DeclineTakenOver("let-holder-result-reaches-record", SourceNameOfLabel(settle.Label));
            return false;
        }

        return true;
    }

    // Settlements are kept out of the instruction stream, anchored to the instruction they follow, so the
    // lowering that runs before they are resolved sees the same instructions whether the record is on or
    // off. A settlement after a consuming call follows the call, ahead of that call's window restore,
    // while the arena still holds the cells. An anchor rewritten later loses its settlement, which only
    // leaves the references where they were.
    private readonly Dictionary<IrInst, List<(int Id, int Temp)>> _takenOverAnchors = new(ReferenceEqualityComparer.Instance);

    private void AnchorTakenOverSettle(IrInst? anchor, int id, int temp)
    {
        if (anchor is null)
        {
            return;
        }

        if (!_takenOverAnchors.TryGetValue(anchor, out List<(int Id, int Temp)>? settles))
        {
            settles = [];
            _takenOverAnchors[anchor] = settles;
        }

        settles.Add((id, temp));
    }

    private bool HasTakenOverAnchor(IReadOnlyList<IrInst> instructions)
        => _takenOverAnchors.Count > 0 && instructions.Any(_takenOverAnchors.ContainsKey);

    // While the deferred placeholders are spliced, an anchor moves to the instruction emitted last for it,
    // with its temp renamed as the splice renames; in the settlement round each one is resolved there.
    private void CarryTakenOverAnchors(IrInst raw, Dictionary<int, int> renames)
    {
        if (!_takenOverAnchors.Remove(raw, out List<(int Id, int Temp)>? settles))
        {
            return;
        }

        if (_takenOverSettleRound)
        {
            foreach ((int id, int temp) in settles)
            {
                ResolveTakenOverSettle(id, temp);
            }

            return;
        }

        if (_inst.Count == 0)
        {
            return;
        }

        foreach ((int id, int temp) in settles)
        {
            AnchorTakenOverSettle(_inst[^1], id, renames.TryGetValue(temp, out int renamed) ? renamed : temp);
        }
    }

    private void ResolveTakenOverSettle(int pendingId, int arenaTemp)
    {
        if (!_pendingTakenOverSettles.TryGetValue(pendingId, out PendingTakenOverSettle? settle))
        {
            return;
        }

        if (settle.OwnField is { } named)
        {
            if (OwnFieldRecordProven(settle) && LetScopedSettleOutOfReach(settle))
            {
                EmitOwnFieldWalk(arenaTemp, named);
            }

            return;
        }

        if (settle.Tuple is not { } tuple
            || !ReachesTakenOverRecordLabel(settle.Label)
            || !TakenOverRecordProven(settle)
            || !LetScopedSettleOutOfReach(settle))
        {
            return;
        }

        EmitTakenOverWalk(arenaTemp, tuple);
    }

    // Obligations 2 and 3 at the settling call: in the record reach of the callee's result (a spine
    // freed owned list is a value of its own there, see ComputeRecordReach), every parameter path whose
    // type at this call can hold a T value names an argument holding nothing; any other value the
    // result holds there (a fresh value of the callee's, an unproven reach) declines.
    private bool TakenOverRecordProven(PendingTakenOverSettle settle)
    {
        string where = SourceNameOfLabel(settle.Label);
        if (settle.Callee is not { } key
            || ComputeRecordReach(key, _spineFreedSources) is not { } reach
            || RecordReachParameters(key) is not { } parameters)
        {
            DeclineTakenOver("no-record-reach", where);
            return false;
        }

        if ((reach.Causes & ~ResultReachCause.InternalSharing) != ResultReachCause.None)
        {
            DeclineTakenOver($"unproven-reach:{reach.Causes}", where);
            return false;
        }

        TypeRef recordType = Prune(settle.Tuple!.Elements[0]);
        List<Expr> arguments = CallArguments(settle.Call, out _);
        return reach.Counts.All(entry => RecordTokenProven(entry.Key, entry.Value, parameters, arguments, recordType, where));
    }

    private bool SyntheticRecordTokenProven(string root, string token, int count, string where)
    {
        if (root.StartsWith("#S", StringComparison.Ordinal))
        {
            if (count > 1)
            {
                DeclineTakenOver("taken-over-elements-shared", where);
                return false;
            }

            return true;
        }

        // A binding's identity token ("#B") rides beside the reach of the value bound, which the other
        // tokens of the same sum account for: a value the function constructs carries its own "#F".
        if (root.StartsWith("#B", StringComparison.Ordinal))
        {
            return true;
        }

        if (root.StartsWith("#F", StringComparison.Ordinal))
        {
            DeclineTakenOver("fresh-construction-in-result", where);
            return false;
        }

        DeclineTakenOver("unrecognized-token", $"{where} {token}");
        return false;
    }

    private static int IndexOfParameter(IReadOnlyList<string> parameters, string name)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            if (string.Equals(parameters[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private bool RecordTokenProven(string token, int count, IReadOnlyList<string> parameters, List<Expr> arguments, TypeRef recordType, string where)
    {
        {
            string[] segments = token.Split('/');
            string root = UntagRoot(segments[0]);
            if (root.StartsWith('#'))
            {
                return SyntheticRecordTokenProven(root, token, count, where);
            }

            int index = IndexOfParameter(parameters, root);
            if (index < 0 || index >= arguments.Count)
            {
                DeclineTakenOver("unmapped-parameter", where);
                return false;
            }

            Expr argument = StripOrSelf(arguments[index]);
            if (argument is Expr.ListLit { Elements.Count: 0 })
            {
                return true;
            }

            if (!_argumentTypes.TryGetValue(arguments[index], out TypeRef? argumentType))
            {
                DeclineTakenOver("argument-type-unknown", where);
                return false;
            }

            if (TypeAtReachPath(Prune(argumentType), segments.AsSpan(1)) is { } atPath
                && ContainsResolvedType(atPath, recordType, []))
            {
                DeclineTakenOver("mixed-borrowed-at-record-position", $"{where} {root}/{string.Join('/', segments.Skip(1))}");
                return false;
            }
        }

        return true;
    }

    // The type a reach path names inside a value of this type, as far as the path can be read: a list's
    // head ("0", or a callee's element "e") and tail ("1"), a tuple's field by index. A segment it
    // cannot read leaves the type reached so far, which stands for everything below it.
    private TypeRef? TypeAtReachPath(TypeRef type, ReadOnlySpan<string> segments)
    {
        TypeRef current = type;
        foreach (string segment in segments)
        {
            current = Prune(current);
            switch (current)
            {
                case TypeRef.TList list when segment is "0" or "e":
                    current = list.Element;
                    break;
                case TypeRef.TList when segment is "1":
                    break;
                case TypeRef.TTuple tuple when int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int field) && field >= 0 && field < tuple.Elements.Count:
                    current = tuple.Elements[field];
                    break;
                default:
                    return current;
            }
        }

        return Prune(current);
    }

    // Whether a value of this type can hold a value of the record's type: the type itself, a type
    // argument, a constructor field, or a function (which may capture one). A type variable cannot: a
    // position the callee's own typing calls `b` never receives the record's type there.
    private bool ContainsResolvedType(TypeRef type, TypeRef target, HashSet<string> seen)
    {
        TypeRef pruned = Prune(type);
        if (string.Equals(Pretty(pruned), Pretty(target), StringComparison.Ordinal))
        {
            return true;
        }

        switch (pruned)
        {
            case TypeRef.TList list:
                return ContainsResolvedType(list.Element, target, seen);
            case TypeRef.TTuple tuple:
                return tuple.Elements.Any(element => ContainsResolvedType(element, target, seen));
            case TypeRef.TFun:
                return true;
            case TypeRef.TNamedType named:
                if (!seen.Add(Pretty(named)))
                {
                    return false;
                }

                return named.TypeArgs.Any(argument => ContainsResolvedType(argument, target, seen))
                    || named.Symbol.Constructors.Any(constructor => Enumerable.Range(0, constructor.Arity)
                        .Any(index => ContainsResolvedType(InstantiateConstructorParameterType(constructor, index, named), target, seen)));
            default:
                return false;
        }
    }

    private bool ReachesTakenOverRecordLabel(string label)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (seen.Add(label))
        {
            if (_takenOverRecordLabels.Contains(label))
            {
                return true;
            }

            if (!_functionReturnedClosureLabels.TryGetValue(label, out string? next))
            {
                return false;
            }

            label = next;
        }

        return false;
    }

    private void ReportTakenOverDeclines()
    {
        // A settlement whose anchor no splice reached was dropped with the instruction it followed;
        // counted when its callee hands a record over, so a rewrite that eats settlements shows.
        foreach (List<(int Id, int Temp)> lost in _takenOverAnchors.Values)
        {
            foreach ((int id, _) in lost)
            {
                if (_pendingTakenOverSettles.TryGetValue(id, out PendingTakenOverSettle? settle) && ReachesTakenOverRecordLabel(settle.Label))
                {
                    DeclineTakenOver("anchor-rewritten", SourceNameOfLabel(settle.Label));
                }
            }
        }

        if (Environment.GetEnvironmentVariable("ASHES_TAKEOVER_DECLINES") is null)
        {
            return;
        }

        foreach ((string reason, (int count, string example)) in _takenOverDeclines.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"TAKEOVER-DECLINES {reason} {count} e.g. {example}");
        }
    }

    // Releases the reference each arena cell's head tuple holds at its first field, up to the first
    // reference-counted cell; a head tuple on the reference-counted heap owns its fields already.
    private void EmitTakenOverWalk(int resultTemp, TypeRef.TTuple tuple)
    {
        string doneLabel = NewLabel("taken_over_done");
        int cursorSlot = NewLocal();
        Emit(new IrInst.StoreLocal(cursorSlot, resultTemp));
        string loopLabel = NewLabel("taken_over_cell");
        Emit(new IrInst.Label(loopLabel));
        int cellTemp = NewTemp();
        Emit(new IrInst.LoadLocal(cellTemp, cursorSlot));
        EmitSkipUnlessPresent(cellTemp, doneLabel);
        int countedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(countedTemp, cellTemp));
        string arenaLabel = NewLabel("taken_over_arena_cell");
        Emit(new IrInst.JumpIfFalse(countedTemp, arenaLabel));
        Emit(new IrInst.Jump(doneLabel));
        Emit(new IrInst.Label(arenaLabel));
        int headTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(headTemp, cellTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
        string nextLabel = NewLabel("taken_over_next");
        int headCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(headCountedTemp, headTemp));
        string headArenaLabel = NewLabel("taken_over_arena_head");
        Emit(new IrInst.JumpIfFalse(headCountedTemp, headArenaLabel));
        Emit(new IrInst.Jump(nextLabel));
        Emit(new IrInst.Label(headArenaLabel));
        int fieldTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(fieldTemp, headTemp, 0));
        EmitSkipUnlessReferenceCounted(fieldTemp, nextLabel);
        EmitRuntimeManagedChildDrop(fieldTemp, tuple.Elements[0]);
        Emit(new IrInst.Label(nextLabel));
        int tailTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(tailTemp, cellTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
        Emit(new IrInst.StoreLocal(cursorSlot, tailTemp));
        Emit(new IrInst.Jump(loopLabel));
        Emit(new IrInst.Label(doneLabel));
    }
}
