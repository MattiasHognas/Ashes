namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// One arm of a control-flow join: the store that hands its value to the join slot, and whether
    /// the value is owned on the reference-counted heap (<paramref name="Owned"/>) or joins as one
    /// under a loop parameter's own release (<paramref name="JoinsRuntimeManaged"/>).
    /// </summary>
    private readonly record struct JoinArm(
        IrInst.StoreLocal? Store,
        TypeRef Type,
        bool Owned,
        bool JoinsRuntimeManaged,
        Ashes.Frontend.Expr? Body = null);

    /// <summary>
    /// A join whose arms mix an owned reference-counted value with a value it does not own (an arena
    /// value, or a borrowed reference) is not owned as a whole, so the owned arm's reference is never
    /// released: the caller retains or copies the join's value again. Such arms are normalized in
    /// place instead (retained when reference-counted, copied otherwise), right before their store,
    /// so every arm hands the join a reference of its own. An arm borrowing from a loop parameter
    /// (the parameter, or a part of it) is retained too: the join then owns its value outright, and
    /// the loop's exit releases every parameter. Returns true when the join is owned now.
    /// </summary>
    private bool NormalizeMixedJoinArms(IReadOnlyList<JoinArm> arms, TypeRef resultType)
    {
        TypeRef pruned = Prune(resultType);
        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        if (!GeneralRcEnabled
            || reaching.Count < 2
            || !reaching.Any(IsReferenceCountedJoinArm)
            || reaching.All(IsReferenceCountedJoinArm)
            || reaching.Any(arm => arm.Store is null)
            || CanArenaReset(pruned)
            || ContainsUnresolvedLayoutType(pruned, [])
            || !(CanNormalizeIntoOwnedRuntimeValue(pruned) || IsGeneralRcValueType(pruned)))
        {
            return false;
        }

        if (reaching.Any(ReturnsFunctionParameter))
        {
            RecordJoinRetainsParameter();
        }

        foreach (JoinArm arm in reaching)
        {
            if (!IsOwnedJoinArm(arm))
            {
                NormalizeJoinArmStore(arm.Store!, pruned, Environment.GetEnvironmentVariable("GRC_NO_FRESHARM") is null && arm.Body is { } body && ConstructsFreshCell(body));
            }
        }

        return true;
    }

    // An arm that builds its value itself (a cons, a literal, a constructor application): a
    // reference-counted value it hands on is fresh and its own, so it is kept rather than retained.
    private bool ConstructsFreshCell(Ashes.Frontend.Expr body)
    {
        while (body is Ashes.Frontend.Expr.Let let)
        {
            body = let.Body;
        }

        return body is Ashes.Frontend.Expr.Cons or Ashes.Frontend.Expr.TupleLit or Ashes.Frontend.Expr.ListLit { Elements.Count: > 0 }
            || IsConstructorExpression(body);
    }

    // An arm returning one of the function's own parameters as it is: a caller handing a fresh
    // argument to a callee whose result may be that argument transfers its reference to the result,
    // so a join that retained the parameter as well would leave one reference too many.
    private bool ReturnsFunctionParameter(JoinArm arm)
    {
        if (arm.Body is not { } body)
        {
            return false;
        }

        while (body is Ashes.Frontend.Expr.Let let)
        {
            body = let.Body;
        }

        return body is Ashes.Frontend.Expr.Var variable
            && Lookup(variable.Name) switch
            {
                Binding.Local local => local.Slot == 1 && _tcoCtx is null,
                Binding.Env => true,
                _ => false,
            };
    }

    // A join some arms of which never reach it (a tail self-call jumps back) holds whatever the arms
    // that do reach it hand over: reference-counted when every one of them does.
    private bool ReachingArmsAllReferenceCounted(IReadOnlyList<JoinArm> arms)
    {
        if (!GeneralRcEnabled || !arms.Any(IsOwnershipNeutralArm))
        {
            return false;
        }

        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        return reaching.Count > 0 && reaching.All(arm => IsReferenceCountedJoinArm(arm) || arm.JoinsRuntimeManaged);
    }

    // An arm that hands the join a reference-counted value: one it owns, or one a loop parameter owns.
    private bool IsReferenceCountedJoinArm(JoinArm arm) => IsOwnedValueArm(arm) || BorrowsLoopParameter(arm);

    // An arm whose value the join owns as it is: a reference of its own, not borrowed from a parameter.
    private bool IsOwnedJoinArm(JoinArm arm) => IsOwnedValueArm(arm) && !BorrowsLoopParameter(arm);

    // A nested join already normalized holds its own reference, whatever facts its reload carries.
    private bool IsOwnedValueArm(JoinArm arm)
        => arm.Owned || (arm.Store is { } store && IsNormalizedJoinTemp(store.Source));

    // A join every reaching arm of which hands over a reference of its own (a normalized join, a cell
    // it built, nil) owns its value as the normalized joins do.
    private void RecordOwnedUniformJoin(int resultTemp, IReadOnlyList<JoinArm> arms)
    {
        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        if (reaching.Count > 0
            && reaching.Any(arm => arm.Store is { } owned && IsNormalizedJoinTemp(owned.Source))
            && reaching.All(arm =>
                (arm.Body is { } nil && IsEmptyListLiteral(nil))
                || (!BorrowsLoopParameter(arm) && arm.Store is { } store && IsNormalizedJoinTemp(store.Source))))
        {
            RecordNormalizedJoinTemp(resultTemp);
        }
    }

    // The joins normalized in each function (temps are numbered per function): each holds its own
    // reference, so a loop exit returning one releases every parameter rather than transferring one.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _normalizedJoinTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordNormalizedJoinTemp(int temp)
    {
        if (!_normalizedJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps))
        {
            temps = [];
            _normalizedJoinTempsByBody[_inst] = temps;
        }

        temps.Add(temp);
    }

    private bool IsNormalizedJoinTemp(int temp)
        => _normalizedJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps) && temps.Contains(temp);

    // The values that never reach their join (a tail self-call's back edge jumps away), and joins of
    // nothing else: they decide nothing about the join's ownership.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _ownershipNeutralTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordOwnershipNeutralTemp(int temp)
    {
        if (!_ownershipNeutralTempsByBody.TryGetValue(_inst, out HashSet<int>? temps))
        {
            temps = [];
            _ownershipNeutralTempsByBody[_inst] = temps;
        }

        temps.Add(temp);
    }

    private bool IsOwnershipNeutralTemp(int temp)
        => _ownershipNeutralTempsByBody.TryGetValue(_inst, out HashSet<int>? temps) && temps.Contains(temp);

    private bool IsOwnershipNeutralArm(JoinArm arm)
        => Prune(arm.Type) is TypeRef.TNever || (arm.Store is { } store && IsOwnershipNeutralTemp(store.Source));

    // Joins that are reference-counted only through an arm a loop parameter owns (the parameter, or a
    // part of it): the join borrows from the parameter, so it is never taken as an owned arm.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _parameterBorrowingJoinTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    // Nil joins as a reference-counted list too, but it owns nothing and borrows from nothing.
    private bool BorrowsLoopParameter(JoinArm arm)
        => (arm.JoinsRuntimeManaged && !(arm.Body is { } body && IsEmptyListLiteral(body)))
            || (arm.Store is { } store
                && _parameterBorrowingJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps)
                && temps.Contains(store.Source));

    // A join all of whose arms are neutral is neutral itself; one with an arm borrowing from a loop
    // parameter borrows from it too.
    private void RecordNeutralJoin(int resultTemp, IReadOnlyList<JoinArm> arms)
    {
        if (arms.Count > 0 && arms.All(IsOwnershipNeutralArm))
        {
            RecordOwnershipNeutralTemp(resultTemp);
        }

        if (arms.Any(arm => !IsOwnershipNeutralArm(arm) && BorrowsLoopParameter(arm)))
        {
            if (!_parameterBorrowingJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps))
            {
                temps = [];
                _parameterBorrowingJoinTempsByBody[_inst] = temps;
            }

            temps.Add(resultTemp);
        }
    }

    // A match arm whose value is a reference-counted cell it built itself.
    private bool IsFreshJoinArm(MatchArmResultOwnership arm, Ashes.Frontend.Expr body)
    {
        if (arm.NewlyProduced)
        {
            return true;
        }

        while (body is Ashes.Frontend.Expr.Let let)
        {
            body = let.Body;
        }

        return arm.RuntimeManaged
            && (body is Ashes.Frontend.Expr.Cons or Ashes.Frontend.Expr.TupleLit or Ashes.Frontend.Expr.ListLit { Elements.Count: > 0 }
                || IsConstructorExpression(body));
    }

    private static bool IsEmptyListLiteral(Ashes.Frontend.Expr expression)
    {
        while (expression is Ashes.Frontend.Expr.Let let)
        {
            expression = let.Body;
        }

        return expression is Ashes.Frontend.Expr.ListLit { Elements.Count: 0 };
    }

    /// <summary>
    /// A constructor that would build an arena cell around a fresh reference-counted value (a call's
    /// owned result stored straight into a field) leaves that value unreleased: the arena cell never
    /// releases what it holds. Such a cell is built on the reference-counted heap instead, owning
    /// the fresh value outright and a reference of its own to every other heap field (retained, or
    /// copied from the arena). Owned bindings and loop-parameter reads keep the retains the
    /// constructor's own preparation gives them. Returns true when the cell is placed there.
    /// </summary>
    private bool PlaceConstructorOwningFreshChild(
        IReadOnlyList<Ashes.Frontend.Expr> arguments,
        List<int> argumentTemps,
        IReadOnlyList<TypeRef> argumentTypes,
        TypeRef.TNamedType resultType)
    {
        if (!GeneralRcEnabled || Environment.GetEnvironmentVariable("GRC_NO_CTORFRESH") is not null || ContainsUnresolvedLayoutType(resultType, []))
        {
            return false;
        }

        // A value of the contract's own types is passed borrowed, and a callee may store it into an
        // arena cell without a reference of its own, relying on the caller's: its cell stays in the
        // arena, and a fresh child's reference goes to an owned slot the function releases (after
        // its result, normalized at the return, has taken a reference of its own).
        if (IsGeneralRcValueType(resultType) || !CanNormalizeIntoOwnedRuntimeValue(resultType))
        {
            HandFreshConstructorChildrenToOwnedSlots(arguments, argumentTemps, argumentTypes);
            return false;
        }

        bool anyFresh = false;
        for (int i = 0; i < arguments.Count; i++)
        {
            TypeRef fieldType = Prune(argumentTypes[i]);
            if (CanArenaReset(fieldType))
            {
                continue;
            }

            // A fresh value is handed over as it is and a prepared one keeps its own retain; every
            // other heap field is normalized into the cell and must be expressible there.
            bool fresh = IsFreshOwnedConstructorArgument(arguments[i], argumentTemps[i]);
            anyFresh |= fresh;
            if (!fresh
                && !IsPreparedConstructorArgument(arguments[i], fieldType)
                && (ContainsUnresolvedLayoutType(fieldType, [])
                    || !(CanNormalizeIntoOwnedRuntimeValue(fieldType) || IsGeneralRcValueType(fieldType))))
            {
                return false;
            }
        }

        if (!anyFresh)
        {
            return false;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            TypeRef fieldType = Prune(argumentTypes[i]);
            if (CanArenaReset(fieldType)
                || IsFreshOwnedConstructorArgument(arguments[i], argumentTemps[i])
                || IsPreparedConstructorArgument(arguments[i], fieldType))
            {
                continue;
            }

            int normalizedTemp = EmitRuntimeManagedTcoDeepCopy(argumentTemps[i], fieldType);
            MarkRuntimeManagedTemp(normalizedTemp);
            argumentTemps[i] = normalizedTemp;
        }

        return true;
    }

    private void HandFreshConstructorChildrenToOwnedSlots(
        IReadOnlyList<Ashes.Frontend.Expr> arguments,
        IReadOnlyList<int> argumentTemps,
        IReadOnlyList<TypeRef> argumentTypes)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            TypeRef fieldType = Prune(argumentTypes[i]);
            if (CanArenaReset(fieldType)
                || ContainsUnresolvedLayoutType(fieldType, [])
                || !IsFreshOwnedConstructorArgument(arguments[i], argumentTemps[i]))
            {
                continue;
            }

            int ownedSlot = NewLocal();
            Emit(new IrInst.StoreLocal(ownedSlot, argumentTemps[i]));
            RegisterGeneralRcOwnedSlot(ownedSlot, fieldType);
        }
    }

    // A record update builds its cell in the arena, which never releases what it holds: a fresh
    // reference-counted value stored into an updated field goes to an owned slot, as a constructor's does.
    private void HandFreshRecordUpdateChildToOwnedSlot(Ashes.Frontend.Expr update, int temp, TypeRef fieldType, TypeRef.TNamedType recordType)
    {
        // The same records whose constructor hands its fresh children over: any other record's
        // children are owned by the placement that builds it on the reference-counted heap.
        if (GeneralRcEnabled
            && Environment.GetEnvironmentVariable("GRC_NO_RECUPDFRESH") is null
            && !ContainsUnresolvedLayoutType(recordType, [])
            && (IsGeneralRcValueType(recordType) || !CanNormalizeIntoOwnedRuntimeValue(recordType)))
        {
            HandFreshConstructorChildrenToOwnedSlots([update], [temp], [fieldType]);
        }
    }

    // A value produced by the argument expression itself (not read from a binding) that is owned on
    // the reference-counted heap.
    private bool IsFreshOwnedConstructorArgument(Ashes.Frontend.Expr argument, int temp)
    {
        while (argument is Ashes.Frontend.Expr.Let let)
        {
            argument = let.Body;
        }

        return argument is not (Ashes.Frontend.Expr.Var or Ashes.Frontend.Expr.QualifiedVar)
            && IsRuntimeManagedResultTemp(temp)
            && !IsOwnershipNeutralTemp(temp);
    }

    // An argument the constructor's preparation retains itself: an owned reference-counted binding,
    // or a read of a loop parameter.
    private bool IsPreparedConstructorArgument(Ashes.Frontend.Expr argument, TypeRef fieldType)
        => (argument is Ashes.Frontend.Expr.Var variable
                && LookupOwnedValue(variable.Name) is { RuntimeManaged: true, IsDropped: false, PerceusPatternOwner: false })
            || TryResolveTcoParameterRead(argument, fieldType, out _) is not null;

    // Whether a requested reference-counted tuple can be one: every element is manageable, one of
    // the contract's types included, and those are made the tuple's own.
    private bool PlaceRuntimeManagedTuple(List<LoweredValue> elements, LoweredValueRequest request)
    {
        bool runtimeManaged = request.EmitsRuntime(LoweredValueRuntimeRepresentation.Tuple);
        for (int i = 0; i < elements.Count && runtimeManaged; i++)
        {
            runtimeManaged = IsRuntimeManageableTupleElement(elements[i]) || (Environment.GetEnvironmentVariable("GRC_NO_TUPLE") is null && IsGeneralRcValueType(elements[i].Type));
        }

        NormalizeGeneralRcTupleElements(elements, runtimeManaged);
        return runtimeManaged;
    }

    // A reference-counted tuple owns its children: an element of the contract's types (borrowed, or
    // held by an owned slot the function releases) is retained or copied into it.
    private void NormalizeGeneralRcTupleElements(List<LoweredValue> elements, bool runtimeManaged)
    {
        if (!runtimeManaged)
        {
            return;
        }

        for (int i = 0; i < elements.Count; i++)
        {
            if (!IsRuntimeManageableTupleElement(elements[i]) && IsGeneralRcValueType(elements[i].Type))
            {
                int normalizedTemp = EmitRuntimeManagedTcoDeepCopy(elements[i].Temp, Prune(elements[i].Type));
                MarkRuntimeManagedTemp(normalizedTemp);
                elements[i] = CreateLoweredValue(normalizedTemp, elements[i].Type);
            }
        }
    }

    // An if's join: owned when both branches are, or once a mixed pair is normalized.
    private void MarkIfJoinResult(
        int resultTemp,
        Ashes.Frontend.Expr.If iff,
        (int Temp, TypeRef Type, IrInst.StoreLocal Store) then,
        (int Temp, TypeRef Type, IrInst.StoreLocal Store) otherwise,
        TypeRef resultType)
    {
        JoinArm[] arms =
        [
            new JoinArm(then.Store, then.Type, IsRuntimeManagedResultTemp(then.Temp), BranchJoinsRuntimeManagedResult(iff.Then, resultType), iff.Then),
            new JoinArm(otherwise.Store, otherwise.Type, IsRuntimeManagedResultTemp(otherwise.Temp), BranchJoinsRuntimeManagedResult(iff.Else, resultType), iff.Else),
        ];
        RecordNeutralJoin(resultTemp, arms);
        if (ReachingArmsAllReferenceCounted(arms))
        {
            RecordControlFlowJoinTemp(resultTemp, resultType, runtimeManaged: true, allBranchesNewlyProduced: false);
            return;
        }

        if (NormalizeMixedJoinArms(arms, resultType))
        {
            RecordNormalizedJoinTemp(resultTemp);
            RecordControlFlowJoinTemp(resultTemp, resultType, runtimeManaged: true, allBranchesNewlyProduced: false);
            return;
        }

        if (GeneralRcEnabled)
        {
            RecordOwnedUniformJoin(resultTemp, arms);
        }

        MarkUniformRuntimeManagedResult(resultTemp, iff.Then, then.Temp, iff.Else, otherwise.Temp, resultType);
    }

    // Replaces the arm's store with a normalization of its value followed by the store of the
    // normalized value, emitted into a buffer and spliced where the store was.
    private void NormalizeJoinArmStore(IrInst.StoreLocal store, TypeRef type, bool freshValue = false)
    {
        int index = _inst.FindLastIndex(instruction => ReferenceEquals(instruction, store));
        if (index < 0)
        {
            throw new InvalidOperationException("A control-flow join arm's store is no longer in its function.");
        }

        List<IrInst> function = _inst;
        List<IrInst> buffer = [];
        _inst = buffer;
        try
        {
            int normalizedTemp = freshValue
                ? EmitOwnedResultOrCopy(store.Source, type)
                : EmitRuntimeManagedTcoDeepCopy(store.Source, type);
            MarkRuntimeManagedTemp(normalizedTemp);
            Emit(new IrInst.StoreLocal(store.Slot, normalizedTemp));
        }
        finally
        {
            _inst = function;
        }

        _inst.RemoveAt(index);
        _inst.InsertRange(index, buffer);
        ShiftRecordedInstructionIndices(index, buffer.Count - 1);
    }

    // Positions recorded for a later splice into the function being lowered move with the
    // instructions after the arm's store.
    private void ShiftRecordedInstructionIndices(int index, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        for (int i = 0; i < _patternBindingPlacementSites.Count; i++)
        {
            if (_patternBindingPlacementSites[i].InsertIndex > index)
            {
                _patternBindingPlacementSites[i] = _patternBindingPlacementSites[i] with
                {
                    InsertIndex = _patternBindingPlacementSites[i].InsertIndex + delta,
                };
            }
        }

        if (_callArgumentRetainFrame is { } frame && ReferenceEquals(frame.Body, _inst) && frame.ArgumentStart > index)
        {
            frame.ArgumentStart += delta;
        }
    }
}
