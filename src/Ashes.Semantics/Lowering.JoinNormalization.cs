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
            || !(CanNormalizeIntoOwnedRuntimeValue(pruned) || IsGeneralRcValueType(pruned) || IsNormalizableJoinTuple(pruned)))
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

    // A tuple join every element of which is a scalar or normalizes into an owned value: an arena
    // arm is copied into an owned tuple that retains each reference-counted element it keeps.
    private bool IsNormalizableJoinTuple(TypeRef type)
        => Environment.GetEnvironmentVariable("GRC_NO_TUPLEJOIN") is null
            && type is TypeRef.TTuple tuple
            && tuple.Elements.Any(element => !CanArenaReset(Prune(element)))
            && tuple.Elements.All(element => CanArenaReset(Prune(element)) || CanNormalizeIntoOwnedRuntimeValue(element));

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

    // A join some arms of which never reach it, every one of the rest handing over a value it just
    // produced (a loop's exit building its result): the join's value is fresh and its own.
    private bool ReachingArmsAllNewlyProduced(IReadOnlyList<JoinArm> arms)
    {
        if (!GeneralRcEnabled || !arms.Any(IsOwnershipNeutralArm))
        {
            return false;
        }

        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        return reaching.Count > 0
            && reaching.All(arm => !BorrowsLoopParameter(arm) && arm.Store is { } store && (IsNewlyProducedRcTemp(store.Source) || IsRetainedPatternOwnerArm(arm)));
    }

    // An arm that hands the join a reference-counted value: one it owns, or one a loop parameter owns.
    private bool IsReferenceCountedJoinArm(JoinArm arm) => IsOwnedValueArm(arm) || BorrowsLoopParameter(arm);

    // An arm whose value the join owns as it is: a reference of its own, not borrowed from a parameter.
    private bool IsOwnedJoinArm(JoinArm arm) => IsOwnedValueArm(arm) && !BorrowsLoopParameter(arm);

    // A nested join already normalized holds its own reference, whatever facts its reload carries.
    private bool IsOwnedValueArm(JoinArm arm)
        => arm.Owned || IsRetainedPatternOwnerArm(arm) || (arm.Store is { } store && IsNormalizedJoinTemp(store.Source));

    // A join every reaching arm of which hands over a reference of its own (a normalized join, a value
    // it just produced, nil) owns its value as the normalized joins do.
    private void RecordOwnedUniformJoin(int resultTemp, IReadOnlyList<JoinArm> arms)
    {
        List<JoinArm> reaching = arms.Where(arm => !IsOwnershipNeutralArm(arm)).ToList();
        if (reaching.Count > 0
            && reaching.Any(arm => arm.Store is { } owned && IsNormalizedJoinTemp(owned.Source))
            && reaching.All(arm =>
                (arm.Body is { } nil && IsEmptyListLiteral(nil))
                || (!BorrowsLoopParameter(arm) && arm.Store is { } store
                    && (IsNormalizedJoinTemp(store.Source) || HandsOverNewlyProducedValue(arm, store)))))
        {
            RecordNormalizedJoinTemp(resultTemp);
        }

        if (reaching.Count > 0 && reaching.All(IsOwnedScopeResultArm))
        {
            RecordOwnedScopeJoinTemp(resultTemp);
        }
    }

    private static bool FreshUniformJoinRuleEnabled =>
        Environment.GetEnvironmentVariable("GRC_NO_FRESHUNIFORMJOIN") is null;

    // An owned arm whose value it just produced (a tuple it built) holds a reference no slot of the
    // function owns, so the join takes it over as it takes a normalized join's.
    private bool HandsOverNewlyProducedValue(JoinArm arm, IrInst.StoreLocal store)
        => FreshUniformJoinRuleEnabled && arm.Owned && IsNewlyProducedRcTemp(store.Source);

    // An arm handing the join a reference of its own without borrowing from a loop parameter: nil, a
    // value it just produced on the reference-counted heap, a retain it took of a binding whose scope
    // releases its own reference, or a join that owns its value itself.
    private bool IsOwnedScopeResultArm(JoinArm arm)
        => (arm.Body is { } nil && IsEmptyListLiteral(nil))
            || (!BorrowsLoopParameter(arm)
                && arm.Store is { } store
                && (IsNormalizedJoinTemp(store.Source)
                    || IsOwnedScopeJoinTemp(store.Source)
                    || IsNewlyProducedRcTemp(store.Source)
                    || IsRuntimeRetainTemp(store.Source)));

    private bool IsRuntimeRetainTemp(int temp)
        => _tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? fact)
            && fact.Representation == LoweredTempRepresentation.RuntimeRc
            && fact.Producer == LoweredTempProducerKind.RcDup;

    // The joins whose every arm hands over a reference of its own: a scope replacing such a join
    // with its arena copy releases the join (MaterializeScopeResultViews), and a loop exit returning
    // one releases every parameter rather than transferring the one it equals.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _ownedScopeJoinTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordOwnedScopeJoinTemp(int temp)
    {
        if (!_ownedScopeJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps))
        {
            temps = [];
            _ownedScopeJoinTempsByBody[_inst] = temps;
        }

        temps.Add(temp);
    }

    private bool IsOwnedScopeJoinTemp(int temp)
        => _ownedScopeJoinTempsByBody.TryGetValue(_inst, out HashSet<int>? temps) && temps.Contains(temp);

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

    // A branch returning a pattern binding of a loop parameter retains it before the binding's scope
    // releases it: the arm hands the join a reference of its own, not one the parameter owns.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _retainedPatternOwnerArmTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordRetainedPatternOwnerArmTemp(int temp)
    {
        if (!_retainedPatternOwnerArmTempsByBody.TryGetValue(_inst, out HashSet<int>? temps))
        {
            temps = [];
            _retainedPatternOwnerArmTempsByBody[_inst] = temps;
        }

        temps.Add(temp);
    }

    private bool IsRetainedPatternOwnerArm(JoinArm arm)
        => arm.Store is { } store
            && ((_retainedPatternOwnerArmTempsByBody.TryGetValue(_inst, out HashSet<int>? temps)
                    && temps.Contains(store.Source))
                || IsResolvedDeferredPatternOwnerArmTemp(store.Source));

    // A branch returning a pattern binding lowered while the binding's type was still unresolved
    // (a generic walk whose element type a later arm fixes) hands the join a reference or a copy of
    // it (TransferDeferredPatternOwnerResult). Whether that is a reference of its own is read when
    // the join is completed, after every arm has been lowered: it is one once the binding's type has
    // resolved to a string, which its owner protects whatever the loop parameter's placement
    // (FinalizePerceusPatternBindingOwners) and whose copy is the one the branch emits. The
    // parameter's own placement is not read here, since it may still be revised before the owner is
    // finalized.
    private readonly Dictionary<List<IrInst>, Dictionary<int, OwnershipInfo>> _deferredPatternOwnerArmTempsByBody =
        new(ReferenceEqualityComparer.Instance);

    private void RecordDeferredPatternOwnerArmTemp(int temp, OwnershipInfo owner)
    {
        if (!_deferredPatternOwnerArmTempsByBody.TryGetValue(_inst, out Dictionary<int, OwnershipInfo>? temps))
        {
            temps = [];
            _deferredPatternOwnerArmTempsByBody[_inst] = temps;
        }

        temps[temp] = owner;
    }

    private bool IsResolvedDeferredPatternOwnerArmTemp(int temp)
        => _deferredPatternOwnerArmTempsByBody.TryGetValue(_inst, out Dictionary<int, OwnershipInfo>? temps)
            && temps.TryGetValue(temp, out OwnershipInfo? owner)
            && owner.Type is { } bindingType
            && Prune(bindingType) is TypeRef.TStr;

    // Nil joins as a reference-counted list too, but it owns nothing and borrows from nothing. A
    // nested join normalized in place borrowed from a parameter before its arms were retained, and
    // holds its own reference since, as does an arm that retained the pattern binding it returns.
    private bool BorrowsLoopParameter(JoinArm arm)
        => (arm.JoinsRuntimeManaged && !(arm.Body is { } body && IsEmptyListLiteral(body)) && !IsRetainedPatternOwnerArm(arm))
            || (arm.Store is { } store
                && !IsNormalizedJoinTemp(store.Source)
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
        if (!GeneralRcEnabled || Environment.GetEnvironmentVariable("GRC_NO_CTORFRESH") is not null)
        {
            return false;
        }

        // A type argument no field of this cell mentions (the error type of an `Ok`) may be
        // unresolved while the cell is built, though the cell's own layout is not: its fresh
        // children go to owned slots, as the contract's types do, and the return decides whether
        // the result is normalized and the slots released.
        if (ContainsUnresolvedLayoutType(resultType, []))
        {
            if (OnlyFieldlessTypeArgumentsUnresolved(resultType, argumentTypes))
            {
                HandFreshConstructorChildrenToOwnedSlots(arguments, argumentTemps, argumentTypes);
            }

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
            anyFresh |= fresh || HoldsReferenceTakenForCell(arguments[i], argumentTemps[i]);
            if (!fresh
                && !IsPreparedConstructorArgument(arguments[i], fieldType)
                && (ContainsUnresolvedLayoutType(fieldType, [])
                    || !(CanNormalizeIntoOwnedRuntimeValue(fieldType) || IsGeneralRcValueType(fieldType))))
            {
                return false;
            }
        }

        if (anyFresh)
        {
            NormalizePlacedConstructorFields(arguments, argumentTemps, argumentTypes);
        }

        return anyFresh;
    }

    // The reference a cell placed on the reference-counted heap takes to each heap field that holds
    // none of its own yet: a retain of a counted value, a copy of an arena one.
    private void NormalizePlacedConstructorFields(
        IReadOnlyList<Ashes.Frontend.Expr> arguments,
        List<int> argumentTemps,
        IReadOnlyList<TypeRef> argumentTypes)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            TypeRef fieldType = Prune(argumentTypes[i]);
            if (CanArenaReset(fieldType)
                || IsFreshOwnedConstructorArgument(arguments[i], argumentTemps[i])
                || IsPreparedConstructorArgument(arguments[i], fieldType)
                || IsRetainTakenForCell(argumentTemps[i]))
            {
                continue;
            }

            int normalizedTemp = EmitRuntimeManagedTcoDeepCopy(WithdrawPatternOwnerMarker(argumentTemps[i]), fieldType);
            MarkRuntimeManagedTemp(normalizedTemp);
            argumentTemps[i] = normalizedTemp;
        }
    }

    // Whether every unresolved type argument of a cell's type is a variable none of its fields'
    // types mention.
    private bool OnlyFieldlessTypeArgumentsUnresolved(TypeRef.TNamedType resultType, IReadOnlyList<TypeRef> argumentTypes)
        => Environment.GetEnvironmentVariable("GRC_NO_CTORFIELDLESS") is null
            && resultType.TypeArgs.All(argument =>
                !ContainsUnresolvedLayoutType(argument, [])
                || (Prune(argument) is TypeRef.TVar variable
                    && !argumentTypes.Any(fieldType => Occurs(variable.Id, fieldType) || ContainsUnresolvedLayoutType(fieldType, []))));

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
                || !(IsFreshOwnedConstructorArgument(arguments[i], argumentTemps[i]) || IsArenaCellRetainHandedToSlot(argumentTemps[i])))
            {
                continue;
            }

            int ownedSlot = NewLocal();
            Emit(new IrInst.StoreLocal(ownedSlot, argumentTemps[i]));
            RegisterGeneralRcOwnedSlot(ownedSlot, fieldType);
            _ownedSlotConstructorArguments.Add(arguments[i]);
        }
    }

    // A real retain taken for the arena cell (a let-bound owned value the cell keeps) is the arena's
    // to hold with nothing to release it, as a fresh value is: it goes to an owned slot the same way.
    private bool IsArenaCellRetainHandedToSlot(int temp)
        => Environment.GetEnvironmentVariable("GRC_NO_CTORRETAINSLOT") is null && IsRetainTakenForCell(temp);

    // The constructor arguments whose fresh value went to an owned slot rather than to the arena cell
    // storing it: the slot releases the value, so a release of the cell's children skips that field.
    private readonly HashSet<Ashes.Frontend.Expr> _ownedSlotConstructorArguments =
        new(ReferenceEqualityComparer.Instance);

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

    // An argument holding a reference taken for the cell under construction.
    private bool HoldsReferenceTakenForCell(Ashes.Frontend.Expr argument, int temp)
        => IsRetainedTailPatternOwner(argument) || IsRetainTakenForCell(temp);

    // A loop's result built in tail position around a binding matched out of a reference-counted
    // loop parameter: the constructor retains the binding (the loop releases its parameters at the
    // exit), and only a reference-counted cell releases that retain.
    private bool IsRetainedTailPatternOwner(Ashes.Frontend.Expr argument)
        => Environment.GetEnvironmentVariable("GRC_NO_CTORTAILOWNER") is null
            && _tcoCtx is { InTailPosition: true } loop
            && argument is Ashes.Frontend.Expr.Var variable
            && LookupOwnedValue(variable.Name) is { PerceusPatternOwner: true, IsDropped: false } owner
            && loop.IsRuntimeManagedSlot(owner.PerceusRootParameterSlot);

    // A reference the preparation of a constructor argument took for the cell (a real retain of an
    // owned value the cell keeps), outside a loop's successor: an arena cell would hold it with
    // nothing to release it. A successor's back edge, written in place or bound by a `let` the self
    // call passes on, balances the retains its own cells hold.
    private bool IsRetainTakenForCell(int temp)
        => Environment.GetEnvironmentVariable("GRC_NO_CTORRETAIN") is null
            && !(_tcoCtx is { } loop
                && (_tcoArgumentDestination is { } destination && ReferenceEquals(destination.Loop, loop)
                    || ReferenceEquals(_arenaSuccessorLetLoop, loop)))
            && FindLastInstruction(instruction => instruction switch
            {
                IrInst.RcDup duplicate => duplicate.Target == temp,
                IrInst.LoadLocal load => load.Target == temp,
                _ => false,
            }) is int index and >= 0
            && _inst[index] is IrInst.RcDup { RuntimeManaged: true };

    // The pattern-owner duplicate an argument took for an arena cell becomes a plain read when the
    // cell is placed on the reference-counted heap instead: the normalization of the value the
    // duplicate read takes the cell's reference, whatever the owner's root parameter is finally
    // placed as. Answers the value to normalize.
    private int WithdrawPatternOwnerMarker(int temp)
    {
        int index = FindLastInstruction(instruction => instruction is IrInst.RcDup duplicate && duplicate.Target == temp);
        if (index < 0 || _inst[index] is not IrInst.RcDup { RuntimeManaged: false } marker)
        {
            return temp;
        }

        _inst[index] = new IrInst.Borrow(marker.Target, marker.SourceTemp) { Location = marker.Location };
        return marker.SourceTemp;
    }

    // An argument the constructor's preparation retains itself: an owned reference-counted binding,
    // or a read of a loop parameter.
    private bool IsPreparedConstructorArgument(Ashes.Frontend.Expr argument, TypeRef fieldType)
        => (argument is Ashes.Frontend.Expr.Var variable
                && LookupOwnedValue(variable.Name) is { RuntimeManaged: true, IsDropped: false, PerceusPatternOwner: false })
            || TryResolveTcoParameterRead(argument, fieldType, out _) is not null;

    // A tuple beside an element of unresolved layout (a generic value) stays in the arena, where
    // nothing releases what it holds. An arena tuple never holds the only reference to a
    // reference-counted value: such an element is copied into the arena beside it, a fresh one is
    // released after the copy, and none is retained for the tuple. Returns the elements copied.
    // A tuple kept in the arena by a sibling the reference-counted heap cannot take (a borrowed
    // tuple parameter) is in the same place beside a reference-counted element: that element alone
    // is copied, so the sibling is not.
    private bool[] DemoteElementsBesideAbstractSibling(List<LoweredValue> elements, bool runtimeManaged)
    {
        bool[] demoted = new bool[elements.Count];
        if (runtimeManaged
            || !GeneralRcEnabled
            || Environment.GetEnvironmentVariable("GRC_NO_DEMOTE") is not null)
        {
            return demoted;
        }

        bool besideAbstract = elements.Exists(element => ContainsUnresolvedLayoutType(Prune(element.Type), []));
        bool besideArenaOnly = !besideAbstract && DemotesBesideArenaOnlySibling && HoldsReferenceCountedElement(elements);
        if (!besideAbstract && !besideArenaOnly)
        {
            return demoted;
        }

        for (int i = 0; i < elements.Count; i++)
        {
            TypeRef type = Prune(elements[i].Type);
            if ((besideArenaOnly && elements[i].Ownership.Representation != LoweredTempRepresentation.RuntimeRc)
                || CanArenaReset(type)
                || type is TypeRef.TFun
                || ContainsUnresolvedLayoutType(type, [])
                || !IsGeneralRcAdmissible(type)
                || IsGeneralRcValueType(type))
            {
                continue;
            }

            int sourceTemp = NeutralizeAggregateDuplicate(elements[i].Temp);
            int copiedTemp = EmitArenaCopyOfReferenceCounted(sourceTemp, type, IsNewlyProducedRcTemp(sourceTemp));
            elements[i] = CreateLoweredValue(copiedTemp, type);
            demoted[i] = true;
        }

        return demoted;
    }

    private static bool DemotesBesideArenaOnlySibling
        => Environment.GetEnvironmentVariable("GRC_NO_DEMOTEBESIDEARENA") is null;

    // The duplicate a pattern-owner read took for the aggregate is not needed once the aggregate
    // holds a copy; left as a marker, it could still be upgraded to a retain nothing releases.
    // Returns the value the element was read as.
    private int NeutralizeAggregateDuplicate(int elementTemp)
    {
        int index = _inst.FindLastIndex(instruction => instruction is IrInst.RcDup duplicate && duplicate.Target == elementTemp);
        if (index < 0 || _inst[index] is not IrInst.RcDup { RuntimeManaged: false } marker)
        {
            return elementTemp;
        }

        _inst[index] = new IrInst.Borrow(marker.Target, marker.SourceTemp) { Location = marker.Location };
        return marker.SourceTemp;
    }

    // An arena copy of a value that may be reference-counted, releasing the original when the
    // caller owned it; a value already outside the reference-counted heap is used as it is.
    private int EmitArenaCopyOfReferenceCounted(int sourceTemp, TypeRef type, bool releasesSource)
    {
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, sourceTemp));
        int referenceCountedTemp = NewTemp();
        Emit(new IrInst.IsReferenceCounted(referenceCountedTemp, sourceTemp));
        string doneLabel = NewLabel("rc_arena_demote_done");
        Emit(new IrInst.JumpIfFalse(referenceCountedTemp, doneLabel));
        Emit(new IrInst.StoreLocal(resultSlot, EmitDeepCopy(sourceTemp, type)));
        if (releasesSource)
        {
            EmitRuntimeManagedChildDrop(sourceTemp, type);
        }

        Emit(new IrInst.Label(doneLabel));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    // Whether a tuple is a reference-counted one: it was requested, or it holds a reference-counted
    // element; and every element is manageable, one of the contract's types included, and those are
    // made the tuple's own. An element normalized here already holds the reference the tuple stores.
    private bool PlaceRuntimeManagedTuple(List<LoweredValue> elements, LoweredValueRequest request, out bool[] normalized)
    {
        bool holdsReferenceCounted = HoldsReferenceCountedElement(elements);
        bool runtimeManaged = request.EmitsRuntime(LoweredValueRuntimeRepresentation.Tuple)
            || holdsReferenceCounted;
        for (int i = 0; i < elements.Count && runtimeManaged; i++)
        {
            runtimeManaged = IsRuntimeManageableTupleElement(elements[i])
                || (Environment.GetEnvironmentVariable("GRC_NO_TUPLE") is null && IsGeneralRcValueType(elements[i].Type))
                || (holdsReferenceCounted && IsNormalizableSiblingElement(elements[i]));
        }

        normalized = NormalizeGeneralRcTupleElements(elements, runtimeManaged, holdsReferenceCounted);
        return runtimeManaged;
    }

    // An arena element beside a reference-counted one: copied onto the reference-counted heap, it
    // lets the tuple own both instead of stranding the reference-counted sibling in the arena.
    private bool IsNormalizableSiblingElement(LoweredValue element)
        => Environment.GetEnvironmentVariable("GRC_NO_RCTUPLESIBLING") is null
            && !ContainsUnresolvedLayoutType(Prune(element.Type), [])
            && (CanNormalizeIntoOwnedRuntimeValue(element.Type)
                || (PromotesOverStringSiblings && Prune(element.Type) is TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt)
                || (Prune(element.Type) is TypeRef.TList list && CanRuntimeManageTcoListElement(list.Element)));

    // Stage 2 of the arena-aggregate ownership design (SELF_HOSTING.md): an aggregate holding an
    // owned element is promoted over string-like siblings, which are retained or copied like a string.
    private static bool PromotesOverStringSiblings => Environment.GetEnvironmentVariable("GRC_PROMOTE_SIBLINGS") is not null;

    // A tuple's store takes a reference to a reference-counted element. An arena tuple can release
    // nothing, so that reference would outlive the tuple: a function returning its list inside a
    // pair leaked the list at every call. On the reference-counted heap the tuple owns the element
    // and whoever matches on it releases both.
    private bool HoldsReferenceCountedElement(List<LoweredValue> elements)
        => Environment.GetEnvironmentVariable("GRC_NO_RCTUPLE") is null
            && elements.Exists(element =>
                element.Ownership.Representation == LoweredTempRepresentation.RuntimeRc
                && !CanArenaReset(Prune(element.Type)));

    // A reference-counted tuple owns its children: an element of the contract's types (borrowed, or
    // held by an owned slot the function releases) is retained or copied into it.
    // Returns the elements normalized, which the tuple stores without a further retain.
    private bool[] NormalizeGeneralRcTupleElements(List<LoweredValue> elements, bool runtimeManaged, bool holdsReferenceCounted)
    {
        bool[] normalized = new bool[elements.Count];
        if (!runtimeManaged)
        {
            return normalized;
        }

        for (int i = 0; i < elements.Count; i++)
        {
            if (!IsRuntimeManageableTupleElement(elements[i])
                && (IsGeneralRcValueType(elements[i].Type) || (holdsReferenceCounted && IsNormalizableSiblingElement(elements[i]))))
            {
                int normalizedTemp = EmitRuntimeManagedTcoDeepCopy(elements[i].Temp, Prune(elements[i].Type));
                ReleaseNormalizedPatternOwnerDuplicate(elements[i].Temp, elements[i].Type);
                MarkRuntimeManagedTemp(normalizedTemp);
                elements[i] = CreateLoweredValue(normalizedTemp, elements[i].Type);
                normalized[i] = NormalizedElementOwnsItsReference;
            }
        }

        return normalized;
    }

    private static bool NormalizedElementOwnsItsReference
        => Environment.GetEnvironmentVariable("GRC_NO_NORMALIZEDELEMENTRETAIN") is null;

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
            RecordControlFlowJoinTemp(resultTemp, resultType, runtimeManaged: true, allBranchesNewlyProduced: ReachingArmsAllNewlyProduced(arms));
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
        if (!_joinNormalizedArmSourcesByBody.TryGetValue(_inst, out HashSet<int>? sources))
        {
            sources = [];
            _joinNormalizedArmSourcesByBody[_inst] = sources;
        }

        sources.Add(store.Source);
    }

    // The arm values each function's join normalization retained or copied: the normalization keeps
    // its source alive as it found it, so a source the loop's string promotion later turned into a
    // fresh reference-counted concatenation would be retained and never released.
    private readonly Dictionary<List<IrInst>, HashSet<int>> _joinNormalizedArmSourcesByBody =
        new(ReferenceEqualityComparer.Instance);

    private bool IsJoinNormalizedArmSource(int temp)
        => Environment.GetEnvironmentVariable("GRC_NO_NORMALIZEDARMCONCAT") is null
            && _joinNormalizedArmSourcesByBody.TryGetValue(_inst, out HashSet<int>? sources)
            && sources.Contains(temp);

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
