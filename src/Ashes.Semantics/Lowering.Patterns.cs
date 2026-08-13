using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private (int, TypeRef) LowerMatch(
        Expr.Match match,
        LoweredValueRequest request)
    {
        // The matched value is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = ShouldStackAllocateImmediateMatchScrutinee(match)
            && TryLowerConstructorExpression(match.Value, stackAllocate: true, out var loweredMatchValue)
                ? loweredMatchValue
                : LowerExpr(match.Value).AsPair();
        // Destructuring a resource-bearing binding consumes it: any nested resource moves to the
        // arm's pattern bindings, which take over its cleanup. Mark the binding moved so its own
        // recursive drop is skipped — otherwise the same resource would be closed twice (once by
        // the extracted binding, once by the aggregate's recursive Drop).
        if (match.Value is Expr.Var scrutineeVar
            && LookupOwnedValue(scrutineeVar.Name) is { IsDropped: false } scrutineeInfo
            && (scrutineeInfo.IsResource || scrutineeInfo.IsResourceBearing))
        {
            scrutineeInfo.ReleaseKind = ResourceReleaseKind.Moved;
        }
        var resultType = NewTypeVar();
        var resultSlot = NewLocal();
        var endLabel = NewLabel("match_end");
        var noMatchLabel = NewLabel("match_none");

        Debug.Assert(match.Cases.Count > 0, "Parser should ensure match has at least one case.");

        IReadOnlyList<MatchCase> diagnosticCases = ExpandPatternAlternatives(match.Cases);
        ValidateSingleAdtMatch(diagnosticCases);
        ValidateReachableMatchArms(diagnosticCases);
        var hasAnyTuplePattern = diagnosticCases.Any(c => c.Pattern is Pattern.Tuple);

        // In-place reuse (#2): if we're matching a linear (uniquely-owned, deep-copied-at-entry)
        // accumulator, its deconstructed node becomes a reuse token for same-arity constructions in
        // arms that don't reference the accumulator again (so its cell is dead).
        (string? reuseScrutineeName, TypeRef.TNamedType? runtimeReuseType) =
            GetMatchReuseScrutinee(match, valueType, savedTailPos);
        bool normalizeStaticStringArms = ShouldNormalizeStaticStringMatchArms(match.Cases);

        List<bool>? runtimeManagedResultArms = LowerMatchArms(
            match, valueTemp, valueType, resultType, resultSlot,
            endLabel,
            noMatchLabel,
            savedTailPos,
            reuseScrutineeName,
            runtimeReuseType,
            normalizeStaticStringArms,
            request);

        Emit(new IrInst.Label(noMatchLabel));
        EmitMatchExhaustivenessDiagnostics(match, diagnosticCases, valueType, hasAnyTuplePattern);

        int defaultTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(defaultTemp, 0));
        Emit(new IrInst.StoreLocal(resultSlot, defaultTemp));
        Emit(new IrInst.Label(endLabel));

        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return CompleteMatchResult(resultTemp, resultType, runtimeManagedResultArms, match.Cases);
    }

    private (int Temp, TypeRef Type) CompleteMatchResult(
        int resultTemp,
        TypeRef resultType,
        IReadOnlyList<bool>? runtimeManagedResultArms,
        IReadOnlyList<MatchCase> cases)
    {
        TypeRef prunedResultType = Prune(resultType);
        MarkRuntimeManagedMatchResult(
            resultTemp,
            prunedResultType,
            runtimeManagedResultArms,
            cases);
        return (resultTemp, prunedResultType);
    }

    private void MarkRuntimeManagedMatchResult(
        int resultTemp,
        TypeRef resultType,
        IReadOnlyList<bool>? runtimeManagedResultArms,
        IReadOnlyList<MatchCase> cases)
    {
        bool runtimeManaged = runtimeManagedResultArms is not null
            && runtimeManagedResultArms.Count == cases.Count
            && runtimeManagedResultArms.Select((runtimeManaged, index) =>
                runtimeManaged || MatchArmReturnsRuntimeManagedTcoParam(cases[index].Body))
                .All(value => value);
        RecordControlFlowJoinTemp(resultTemp, resultType, runtimeManaged);
    }

    private bool MatchArmReturnsRuntimeManagedTcoParam(Expr body)
    {
        Expr result = body;
        while (result is Expr.Let let)
        {
            result = let.Body;
        }

        return result is Expr.Var variable
            && Lookup(variable.Name) is Binding.Local local
            && IsRuntimeManagedTcoParamSlot(local);
    }

    private List<bool>? LowerMatchArms(
        Expr.Match match,
        int valueTemp,
        TypeRef valueType,
        TypeRef resultType,
        int resultSlot,
        string endLabel,
        string noMatchLabel,
        bool savedTailPos,
        string? reuseScrutineeName,
        TypeRef.TNamedType? runtimeReuseType,
        bool normalizeStaticStringArms,
        LoweredValueRequest request)
    {
        List<bool>? runtimeManagedResultArms = [];
        _runtimeManagedMatchResultArms.Push(runtimeManagedResultArms);
        if (TryPlanTagSwitch(match.Cases, out var switchPlan))
        {
            LowerMatchArmsViaTagSwitch(match.Value, match.Cases, switchPlan, valueTemp, valueType, resultType, resultSlot, endLabel, noMatchLabel, savedTailPos, reuseScrutineeName, runtimeReuseType, normalizeStaticStringArms, request);
        }
        else
        {
            LowerMatchArmsLinear(match, valueTemp, valueType, resultType, resultSlot, endLabel, noMatchLabel, savedTailPos, reuseScrutineeName, runtimeReuseType, normalizeStaticStringArms, request);
        }
        _runtimeManagedMatchResultArms.Pop();
        return runtimeManagedResultArms;
    }

    private bool ShouldNormalizeStaticStringMatchArms(IReadOnlyList<MatchCase> cases)
    {
        bool hasFreshStringResult = false;
        foreach (MatchCase matchCase in cases)
        {
            if (matchCase.Guard is not null
                || !IsRuntimeManagedStringMatchArm(matchCase.Body, out bool fresh))
            {
                return false;
            }

            hasFreshStringResult |= fresh;
        }

        return hasFreshStringResult;
    }

    private bool IsRuntimeManagedStringMatchArm(Expr expression, out bool fresh)
    {
        if (expression is Expr.StrLit)
        {
            fresh = false;
            return true;
        }

        if (IsRuntimeRcStringProducer(expression))
        {
            fresh = true;
            return true;
        }

        if (expression is Expr.Let let)
        {
            return IsRuntimeManagedStringMatchArm(let.Body, out fresh) && fresh;
        }

        fresh = false;
        return false;
    }

    private (string? Name, TypeRef.TNamedType? RuntimeType) GetMatchReuseScrutinee(
        Expr.Match match,
        TypeRef valueType,
        bool matchIsInTailPosition)
    {
        if (match.Value is Expr.Var variable && _linearReuseNames.Contains(variable.Name)
            && !IsArenaReuseUnsafeForRuntimeManagedChildren(variable, match))
        {
            return (variable.Name, null);
        }

        if (!TryGetRuntimeManagedReuseScrutinee(
                match,
                valueType,
                matchIsInTailPosition,
                out string runtimeScrutineeName,
                out TypeRef.TNamedType runtimeType))
        {
            return (null, null);
        }

        LookupOwnedValue(runtimeScrutineeName)!.ReleaseKind = ResourceReleaseKind.Moved;
        return (runtimeScrutineeName, runtimeType);
    }

    /// <summary>
    /// Proves the first source-level runtime reuse boundary. The scrutinee must be a live
    /// runtime-managed copy-only, nested-record, or supported self-recursive ADT, and the guard-free
    /// match must exhaustively consume it. Runtime-managed payload bindings may be dead or
    /// transferred exactly once into the compatible rebuild. A same-sized constructor may consume
    /// the token; otherwise the
    /// arm releases a non-null token with constructor-specialized cleanup after evaluating its body.
    /// </summary>
    private bool TryGetRuntimeManagedReuseScrutinee(
        Expr.Match match,
        TypeRef valueType,
        bool matchIsInTailPosition,
        out string scrutineeName,
        out TypeRef.TNamedType runtimeType)
    {
        scrutineeName = string.Empty;
        runtimeType = null!;
        if (match.Value is not Expr.Var variable
            || LookupOwnedValue(variable.Name) is not
            {
                RuntimeManaged: true,
                IsDropped: false,
                Type: TypeRef.TNamedType ownedType,
            }
            || Prune(valueType) is not TypeRef.TNamedType matchedType
            || !string.Equals(ownedType.Symbol.Name, matchedType.Symbol.Name, StringComparison.Ordinal)
            || (!CanRuntimeManageCopyAdt(matchedType)
                && !CanRuntimeManageAdt(matchedType)
                && !CanRuntimeManageOwnedChildAdt(matchedType)
                && !CanRuntimeManageRecursiveCopyAdt(matchedType))
            || match.Cases.Count != matchedType.Symbol.Constructors.Count)
        {
            return false;
        }

        var matchedConstructors = new HashSet<string>(StringComparer.Ordinal);
        bool hasReusableArm = false;
        foreach (MatchCase matchCase in match.Cases)
        {
            if (matchCase.Guard is not null
                || ExprReferencesName(matchCase.Body, variable.Name, shadowed: false)
                || !TryGetConstructorSymbol(matchCase.Pattern, out ConstructorSymbol matchedConstructor)
                || !string.Equals(matchedConstructor.ParentType, matchedType.Symbol.Name, StringComparison.Ordinal)
                || !matchedConstructors.Add(matchedConstructor.Name))
            {
                return false;
            }

            bool armConsumesToken = TryFindRuntimeReuseConstructorArguments(
                matchCase.Body,
                matchedConstructor.Arity,
                matchedType,
                out _);
            if (matchIsInTailPosition && !armConsumesToken)
            {
                return false;
            }

            hasReusableArm |= armConsumesToken;
        }

        if (!hasReusableArm
            || (!CanRuntimeManageCopyAdt(matchedType)
                && !RuntimeReusePointerFieldsAreSafe(match.Cases, matchedType)))
        {
            return false;
        }

        scrutineeName = variable.Name;
        runtimeType = matchedType;
        return true;
    }

    // Arena in-place reuse of a TCO accumulator's ADT cell is unsound when the ADT carries
    // runtime-managed (pointer-bearing) children: the cell is arena-managed but its children are RC,
    // so the back-edge deferred drop (TcoBackEdgeDropRuntimeManagedArgCore) releases the previous
    // value by re-reading THIS cell's fields — which the arena AllocReusing has already overwritten
    // with the new children, freeing the live new children (observed: a shared-tail child loses cells
    // across iterations). Keeping the old cell intact (a fresh rebuild) lets the deferred drop
    // release the real old value. The runtime-managed reuse path (CanCopyOutAdt / TryGetRuntimeManaged
    // ReuseScrutinee) manages its children explicitly and is unaffected.
    // Arena in-place reuse of a TCO-parameter ADT cell is unsound when the ADT carries any heap child
    // (a field that is not an inline copy scalar — List/String/Bytes/BigInt/Tuple/nested ADT). Such a
    // child is RC-normalized at runtime even when the ADT as a whole is flat-copy-out-able (e.g.
    // `S(List(Int))`, whose Int-element list still becomes an RC list). The back-edge arena reset then
    // frees the reused shell before it is stored as the next iteration's parameter — a use-after-free
    // (fannkuch's `S(perm, count)` enumeration segfaulted; a shared-tail RC child also lost cells,
    // b70b88a). Declining arena reuse routes the reconstruction through the runtime-managed (RC) reuse
    // path (or a fresh RC build), which survives the reset. The scrutinee's inferred type is often an
    // unresolved type variable here (inference is interleaved with lowering), so the ADT constructor
    // is taken from the match pattern rather than from the value type.
    private bool IsArenaReuseUnsafeForRuntimeManagedChildren(Expr.Var scrutinee, Expr.Match match)
        => Lookup(scrutinee.Name) is Binding.Local local
            && _tcoCtx?.ParamSlots.Contains(local.Slot) == true
            && match.Cases.Count > 0
            && TryGetConstructorSymbol(match.Cases[0].Pattern, out ConstructorSymbol constructor)
            && ConstructorHasHeapField(constructor);

    private bool ConstructorHasHeapField(ConstructorSymbol constructor)
    {
        foreach (TypeRef fieldType in constructor.ParameterTypes)
        {
            if (!CanArenaReset(Prune(fieldType)))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindRuntimeReuseConstructorArguments(
        Expr body,
        int fieldCount,
        TypeRef.TNamedType matchedType,
        out IReadOnlyList<Expr> arguments)
    {
        if (TryDescribeConstructorExpression(
                body,
                out ConstructorSymbol? constructor,
                out List<Expr>? constructorArguments,
                out TypeRef.TNamedType? resultType)
            && constructor is not null
            && constructorArguments is not null
            && resultType is not null
            && constructor.Arity == fieldCount
            && ReferenceEquals(resultType.Symbol, matchedType.Symbol)
            && (CanRuntimeManageCopyAdt(resultType)
                || CanRuntimeManageAdt(resultType)
                || CanRuntimeManageOwnedChildAdt(resultType)
                || CanRuntimeManageRecursiveCopyAdt(resultType)))
        {
            arguments = constructorArguments;
            return true;
        }

        if (body is Expr.Let let
            && (TryFindRuntimeReuseConstructorArguments(
                    let.Value,
                    fieldCount,
                    matchedType,
                    out arguments)
                || TryFindRuntimeReuseConstructorArguments(
                    let.Body,
                    fieldCount,
                    matchedType,
                    out arguments)))
        {
            return true;
        }

        arguments = [];
        return false;
    }

    private bool RuntimeReusePointerFieldsAreSafe(
        IReadOnlyList<MatchCase> cases,
        TypeRef.TNamedType matchedType)
    {
        foreach (MatchCase matchCase in cases)
        {
            if (!RuntimeReusePointerFieldsAreSafe(matchCase, matchedType))
            {
                return false;
            }
        }

        return true;
    }

    private bool RuntimeReusePointerFieldsAreSafe(
        MatchCase matchCase,
        TypeRef.TNamedType matchedType)
    {
        if (matchCase.Pattern is not Pattern.Constructor pattern
            || !TryGetConstructorSymbol(pattern, out ConstructorSymbol constructor))
        {
            return true;
        }

        TryFindRuntimeReuseConstructorArguments(
            matchCase.Body,
            constructor.Arity,
            matchedType,
            out IReadOnlyList<Expr> rebuildArguments);

        HashSet<string> transferableBindings = new(StringComparer.Ordinal);
        for (int i = 0; i < Math.Min(pattern.Patterns.Count, constructor.Arity); i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(
                constructor,
                i,
                matchedType));
            if (CanArenaReset(fieldType)
                || fieldType is not TypeRef.TNamedType)
            {
                continue;
            }

            if (pattern.Patterns[i] is not Pattern.Var binding
                || _constructorSymbols.ContainsKey(binding.Name))
            {
                if (MatchCaseReferencesAnyBinding(
                    matchCase,
                    PatternBindings(pattern.Patterns[i])))
                {
                    return false;
                }

                continue;
            }

            int references = CountNameOccurrences(matchCase.Body, binding.Name);
            int transfers = rebuildArguments.Count(argument => argument is Expr.Var variable
                && string.Equals(variable.Name, binding.Name, StringComparison.Ordinal));
            if (references != transfers || transfers > 1)
            {
                return false;
            }

            transferableBindings.Add(binding.Name);
        }

        return RuntimeReuseRebuildPointerFieldsAreSafe(
            constructor,
            rebuildArguments,
            matchedType,
            transferableBindings);
    }

    private bool RuntimeReuseRebuildPointerFieldsAreSafe(
        ConstructorSymbol constructor,
        IReadOnlyList<Expr> rebuildArguments,
        TypeRef.TNamedType matchedType,
        IReadOnlySet<string> transferableBindings)
    {
        for (int i = 0; i < Math.Min(rebuildArguments.Count, constructor.Arity); i++)
        {
            TypeRef fieldType = Prune(InstantiateConstructorParameterType(
                constructor,
                i,
                matchedType));
            if (CanArenaReset(fieldType)
                || (CanRuntimeManageRecursiveCopyAdt(matchedType)
                    && IsFreshConstructorTree(rebuildArguments[i], matchedType.Symbol))
                || ((CanRuntimeManageAdt(matchedType)
                        || CanRuntimeManageOwnedChildAdt(matchedType))
                    && rebuildArguments[i] is Expr.RecordLit)
                || (rebuildArguments[i] is Expr.Var variable
                    && transferableBindings.Contains(variable.Name)))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Lowers match arms as a linear chain of per-arm pattern tests, each falling through to the
    /// next arm on failure. This is the general path that handles guards, literals, tuples, cons
    /// patterns, and nested refinements.
    /// </summary>
    private void LowerMatchArmsLinear(
        Expr.Match match,
        int valueTemp,
        TypeRef valueType,
        TypeRef resultType,
        int resultSlot,
        string endLabel,
        string noMatchLabel,
        bool savedTailPos,
        string? reuseScrutineeName,
        TypeRef.TNamedType? runtimeReuseType,
        bool normalizeStaticStringArms,
        LoweredValueRequest request)
    {
        for (int i = 0; i < match.Cases.Count; i++)
        {
            var caseFailLabel = i == match.Cases.Count - 1 ? noMatchLabel : NewLabel("match_next");
            var armCleanupLabel = NewLabel("match_arm_cleanup");
            var caseScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal);
            _scopes.Push(caseScope);
            // Save the arena watermark before pattern matching and body evaluation
            // so allocations in guard expressions and the arm body are covered.
            EmitArenaWatermark();
            var (armCursorSlot, armEndSlot) = _arenaWatermarks.Peek();
            PushOwnershipScope();

            EmitLinearArmPatternAndGuard(match, i, valueTemp, valueType, armCleanupLabel);

            ArmReuseContext reuseContext = PublishLinearArmReuseToken(
                match,
                i,
                valueTemp,
                reuseScrutineeName,
                runtimeReuseType);

            LowerMatchArmBodyIntoResult(match.Cases, i, resultType, resultSlot, endLabel, savedTailPos, reuseContext, normalizeStaticStringArms, request);

            EmitLinearArmCleanupPath(armCleanupLabel, armCursorSlot, armEndSlot, caseFailLabel);

            _scopes.Pop();
            if (i < match.Cases.Count - 1)
            {
                Emit(new IrInst.Label(caseFailLabel));
            }
        }
    }

    /// <summary>
    /// Infers and emits one linear arm's pattern tests and bindings, then evaluates its guard
    /// (if any), jumping to the arm cleanup label when the pattern or guard fails.
    /// </summary>
    private void EmitLinearArmPatternAndGuard(Expr.Match match, int i, int valueTemp, TypeRef valueType, string armCleanupLabel)
    {
        var patternBindings = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        var patternType = InferPatternType(match.Cases[i].Pattern, patternBindings);
        var hasTupleArityMismatch = ValidateTuplePatternArity(Prune(valueType), match.Cases[i].Pattern);
        if (hasTupleArityMismatch)
        {
            RegisterPatternVariableBindings(patternBindings);
        }
        else
        {
            Unify(valueType, patternType);
            EmitPattern(
                match.Cases[i].Pattern,
                valueTemp,
                armCleanupLabel,
                patternBindings,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        // Track owned bindings created by pattern matching
        TrackOwnedBindingsInPattern(patternBindings);

        // If the case has a guard, evaluate it and jump to cleanup label if false
        if (match.Cases[i].Guard is { } guard)
        {
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;
            var (guardTemp, guardType) = LowerExpr(guard);
            Unify(guardType, new TypeRef.TBool());
            Emit(new IrInst.JumpIfFalse(guardTemp, armCleanupLabel));
        }

        TrackRuntimeManagedMatchScrutinee(match.Value, valueTemp, valueType, patternBindings, match.Cases[i].Pattern);
    }

    /// <summary>
    /// Publishes one linear arm's dead accumulator node as a reuse token when eligible.
    /// Returns the reuse-token count before publishing so the caller can drop any token
    /// the arm body didn't consume.
    /// </summary>
    private sealed record ArmReuseContext(int TokensBefore, IReadOnlyList<string> AddedLinearNames);

    private ArmReuseContext PublishLinearArmReuseToken(
        Expr.Match match,
        int i,
        int valueTemp,
        string? reuseScrutineeName,
        TypeRef.TNamedType? runtimeReuseType)
    {
        // In-place reuse (#2): publish the dead accumulator node as a reuse token for a same-arity
        // constructor in this arm's body. Only when the body doesn't reference the accumulator
        // again and there is no guard re-test below (payload fields are bound into temps above).
        int reuseTokensBefore = _reuseTokens.Count;
        var addedLinearNames = new List<string>();
        // A constructor pattern's matched cell is a reuse token. Includes nullary cells (e.g.
        // Leaf), whose bare pattern parses as Pattern.Var of a known nullary constructor.
        int? reuseArity = match.Cases[i].Pattern switch
        {
            Pattern.Constructor reuseCtorPat => reuseCtorPat.Patterns.Count,
            Pattern.Var pv when _constructorSymbols.TryGetValue(pv.Name, out var nc) && nc.Arity == 0 => 0,
            Pattern.Cons => 2,
            _ => null,
        };
        if (reuseScrutineeName is not null
            && reuseArity is int reuseArityVal
            && !ExprReferencesName(match.Cases[i].Body, reuseScrutineeName))
        {
            RuntimeReuseCleanup? runtimeCleanup = runtimeReuseType is not null
                && TryGetConstructorSymbol(match.Cases[i].Pattern, out ConstructorSymbol runtimeConstructor)
                    ? CreateRuntimeReuseCleanup(
                        runtimeReuseType,
                        runtimeConstructor,
                        match.Cases[i].Pattern)
                    : null;
            bool runtimeManaged = runtimeCleanup is not null;
            int tokenTemp = NewTemp();
            Emit(new IrInst.DropReuse(
                tokenTemp,
                valueTemp,
                reuseArityVal,
                runtimeManaged));
            ReuseToken token = new(
                tokenTemp,
                valueTemp,
                reuseArityVal,
                runtimeCleanup,
                reuseScrutineeName,
                ResolveSourceLocation(AstSpans.GetOrDefault(match.Cases[i].Pattern)),
                ListCell: match.Cases[i].Pattern is Pattern.Cons);
            _reuseTokens.Add(token);
            RecordReuseTokenProduction(token);
            RecordReuseTokenUniquenessDecision(
                match.Cases[i].Pattern,
                reuseScrutineeName,
                valueTemp,
                runtimeManaged);
            RecordReuseTokenFieldBindings(tokenTemp, match.Cases[i].Pattern, match.Cases[i].Body);
            if (match.Cases[i].Pattern is Pattern.Cons { Head: Pattern.Var head }
                && _linearReuseNames.Add(head.Name))
            {
                // A list specialization starts from a deep-unique spine. Its head owns a unique
                // pointer-bearing value as well, so a nested match may reuse that child cell before
                // the enclosing cons rebuild consumes the list-cell token.
                addedLinearNames.Add(head.Name);
            }
        }

        return new ArmReuseContext(reuseTokensBefore, addedLinearNames);
    }

    private void RecordReuseTokenUniquenessDecision(
        Pattern pattern,
        string sourceName,
        int valueTemp,
        bool runtimeManaged)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse token must belong to an active function."),
                ReuseDecisionKind.RuntimeUniquenessCheck,
                ReuseDecisionMechanism.ReuseToken,
                runtimeManaged
                    ? ReuseDecisionOutcome.Required
                    : ReuseDecisionOutcome.Omitted,
                runtimeManaged
                    ? ReuseDecisionReason.RuntimeManagedReuseCandidate
                    : ReuseDecisionReason.StaticallyUniqueReuseCandidate,
                new ReuseDecisionCandidate(
                    ReuseCandidateKind.Value,
                    sourceName,
                    Temp: valueTemp),
                RelatedGeneratedLabel: null,
                ResolveSourceLocation(AstSpans.GetOrDefault(pattern))));
    }

    /// <summary>
    /// Lowers one arm's body, unifies its type with the match result type, and stores the value
    /// into the result slot before jumping to the match end label. Shared by the linear and
    /// tag-switch arm lowerings.
    /// </summary>
    private void LowerMatchArmBodyIntoResult(
        IReadOnlyList<MatchCase> cases,
        int i,
        TypeRef resultType,
        int resultSlot,
        string endLabel,
        bool savedTailPos,
        ArmReuseContext reuseContext,
        bool normalizeStaticStringArms,
        LoweredValueRequest request)
    {
        // Each case body IS in tail position (if the match itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var armCredits = BeginExclusiveBranch(cases.Where((_, j) => j != i).Select(c => c.Body));
        var (bodyTemp, bodyType) = LowerMatchArmExpressionWithReuseContext(
            cases[i].Body,
            reuseContext.TokensBefore,
            normalizeStaticStringArms,
            request);
        foreach (string name in reuseContext.AddedLinearNames)
        {
            _linearReuseNames.Remove(name);
        }
        EndExclusiveBranch(armCredits);
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        ReleaseUnconsumedReuseTokens(reuseContext.TokensBefore);

        using (PushDiagnosticContext($"in match arm {i + 1}"))
        {
            using (PushDiagnosticCode(DiagnosticCodes.MatchBranchTypeMismatch))
            {
                Unify(resultType, bodyType);
            }
        }
        bodyTemp = TransferDirectRuntimeManagedMatchResult(cases[i].Body, bodyTemp);
        Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
        int armFinalTemp = PopOwnershipScope(bodyType, bodyTemp);
        if (armFinalTemp != bodyTemp)
        {
            // Copy-out occurred: update the result slot with the freshly allocated copy.
            Emit(new IrInst.StoreLocal(resultSlot, armFinalTemp));
        }
        if (_runtimeManagedMatchResultArms.TryPeek(out List<bool>? runtimeManagedArms)
            && runtimeManagedArms is not null)
        {
            runtimeManagedArms.Add(IsRuntimeManagedResultTemp(armFinalTemp));
        }
        Emit(new IrInst.Jump(endLabel));
    }

    private (int Temp, TypeRef Type) LowerMatchArmExpressionWithReuseContext(
        Expr body,
        int reuseTokensBefore,
        bool normalizeStaticStringArm,
        LoweredValueRequest request)
    {
        IrFunctionOrigin? savedReuseArmOrigin = _activeReuseArmOrigin;
        if (_reuseTokens.Count > reuseTokensBefore)
        {
            _activeReuseArmOrigin = _activeFunctionOrigin;
        }

        try
        {
            return LowerMatchArmExpression(
                body,
                reuseTokensBefore,
                normalizeStaticStringArm,
                request);
        }
        finally
        {
            _activeReuseArmOrigin = savedReuseArmOrigin;
        }
    }

    private (int Temp, TypeRef Type) LowerMatchArmExpression(
        Expr body,
        int reuseTokensBefore,
        bool normalizeStaticStringArm,
        LoweredValueRequest request)
    {
        TypeRef.TNamedType? runtimeReuseType = _reuseTokens
            .Skip(reuseTokensBefore)
            .Select(token => token.RuntimeCleanup?.Type)
            .FirstOrDefault(type => type is not null);
        request = request.WithRuntimeAdtContext(
            childBindings: null,
            reuseType: runtimeReuseType);
        if (normalizeStaticStringArm && body is Expr.StrLit literal)
        {
            var (sourceTemp, sourceType) = LowerStr(literal);
            int resultTemp = NewTemp();
            Emit(new IrInst.CopyOutArena(
                resultTemp,
                sourceTemp,
                -1,
                RuntimeManaged: true,
                IrInst.CopyOutPurpose.RcNormalization));
            MarkRuntimeManagedTemp(resultTemp);
            return (resultTemp, sourceType);
        }

        return LowerExpr(body, request).AsPair();
    }

    private void ReleaseUnconsumedReuseTokens(int reuseTokensBefore)
    {
        for (int i = reuseTokensBefore; i < _reuseTokens.Count; i++)
        {
            ReuseToken token = _reuseTokens[i];
            if (token.RuntimeCleanup is not { } cleanup)
            {
                RecordReuseTokenDisposition(
                    token,
                    ReuseDecisionOutcome.Discarded,
                    ReuseDecisionReason.UnconsumedArenaTokenDiscarded);
                continue;
            }

            int zeroTemp = NewTemp();
            Emit(new IrInst.LoadConstInt(zeroTemp, 0));
            int hasTokenTemp = NewTemp();
            Emit(new IrInst.CmpIntNe(hasTokenTemp, token.Temp, zeroTemp));
            string releasedLabel = NewLabel("reuse_token_released");
            Emit(new IrInst.JumpIfFalse(hasTokenTemp, releasedLabel));
            EmitKnownConstructorRuntimeManagedAdtDrop(
                token.Temp,
                cleanup.Type,
                cleanup.Constructor,
                knownUnique: true);
            Emit(new IrInst.Label(releasedLabel));
            RecordReuseTokenDisposition(
                token,
                ReuseDecisionOutcome.Released,
                ReuseDecisionReason.UnconsumedRuntimeTokenReleased);
        }

        if (_reuseTokens.Count > reuseTokensBefore)
        {
            _reuseTokens.RemoveRange(reuseTokensBefore, _reuseTokens.Count - reuseTokensBefore);
        }
    }

    /// <summary>
    /// Emits a linear arm's cleanup path (Label → RestoreArenaState → ReclaimArenaChunks → Jump):
    /// when pattern/guard fails, restore the arena watermark to reclaim any heap
    /// allocations made during pattern matching or guard evaluation. This is always
    /// safe on the failure path because no result escapes from a failed arm — all
    /// allocations between the watermark and the current cursor are unreachable garbage.
    /// </summary>
    private void EmitLinearArmCleanupPath(string armCleanupLabel, int armCursorSlot, int armEndSlot, string caseFailLabel)
    {
        int armCleanupPreRestoreEndSlot = NewLocal();
        Emit(new IrInst.Label(armCleanupLabel));
        // A guard expression can perform a one-shot capability operation; its pending post must
        // survive the failed-arm cleanup.
        var armCleanupSkipLabel = BeginLivePostsGuard();
        Emit(new IrInst.RestoreArenaState(armCursorSlot, armEndSlot, armCleanupPreRestoreEndSlot));
        Emit(new IrInst.ReclaimArenaChunks(armEndSlot, armCleanupPreRestoreEndSlot));
        EndLivePostsGuard(armCleanupSkipLabel);
        Emit(new IrInst.Jump(caseFailLabel));
    }

    /// <summary>
    /// Emits the non-exhaustiveness diagnostics for a match. Runs regardless of whether the arms
    /// were lowered linearly or as a tag switch — exhaustiveness checking is independent of the
    /// dispatch strategy.
    /// </summary>
    private void EmitMatchExhaustivenessDiagnostics(
        Expr.Match match,
        IReadOnlyList<MatchCase> diagnosticCases,
        TypeRef valueType,
        bool hasAnyTuplePattern)
    {
        var prunedValueType = Prune(valueType);
        var missingAdtConstructors = GetMissingAdtConstructors(prunedValueType, diagnosticCases);
        var missingListCases = GetMissingListCases(prunedValueType, diagnosticCases);
        var hasConstructorPatterns = HasConstructorPattern(diagnosticCases);
        var hasTuplePatternArm = prunedValueType is TypeRef.TTuple && hasAnyTuplePattern;
        bool reportedNonExhaustive = false;
        var matchPos = match.Pos ?? 0;
        if (missingAdtConstructors is not null)
        {
            if (missingAdtConstructors.Count > 0)
            {
                if (TryBuildMissingResultDiagnostic(prunedValueType, missingAdtConstructors, out var resultDiagnostic))
                {
                    _diag.Error(matchPos, resultDiagnostic);
                }
                else
                {
                    _diag.Error(matchPos, FormatMissingConstructorsDiagnostic(missingAdtConstructors));
                }

                reportedNonExhaustive = true;
            }
        }
        else if (missingListCases is not null)
        {
            foreach (var missingCase in missingListCases)
            {
                _diag.Error(matchPos, $"Non-exhaustive match expression. Missing case: {missingCase}.");
                reportedNonExhaustive = true;
            }
        }
        else if (!hasTuplePatternArm && !hasConstructorPatterns && !IsDefinitelyExhaustive(diagnosticCases) && !IsBoolExhaustive(diagnosticCases))
        {
            _diag.Error(matchPos, "Non-exhaustive match expression.");
            reportedNonExhaustive = true;
        }

        if (!reportedNonExhaustive &&
            TryGetMissingPattern(prunedValueType, diagnosticCases.Where(c => c.Guard is null).Select(c => c.Pattern).ToList(), out var missingPattern))
        {
            _diag.Error(matchPos, $"Non-exhaustive match expression. Missing case: {FormatPattern(missingPattern)}.");
        }
    }

    /// <summary>
    /// Determines whether a match can be lowered to a single tag switch (decision-tree dispatch)
    /// instead of a linear chain of tag comparisons. Eligible when there are more than four arms,
    /// every arm is a guard-free constructor pattern (including nullary constructors) over the same
    /// ADT, all constructor tags are distinct, and every payload sub-pattern is trivial
    /// (a wildcard or a plain variable binding) so field extraction can never fail.
    /// </summary>
    private bool TryPlanTagSwitch(IReadOnlyList<MatchCase> cases, out List<(ConstructorSymbol Ctor, long Tag)> plan)
    {
        const int LinearThreshold = 4;
        plan = null!;
        if (cases.Count <= LinearThreshold)
        {
            return false;
        }

        var result = new List<(ConstructorSymbol, long)>(cases.Count);
        var seenTags = new HashSet<int>();
        string? adtName = null;

        foreach (var matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                return false;
            }

            if (!TryGetConstructorSymbol(matchCase.Pattern, out var ctor))
            {
                return false;
            }

            if (matchCase.Pattern is Pattern.Constructor ctorPattern)
            {
                if (ctorPattern.Patterns.Count != ctor.Arity || !ctorPattern.Patterns.All(IsTrivialSubPattern))
                {
                    return false;
                }
            }

            adtName ??= ctor.ParentType;
            if (!string.Equals(adtName, ctor.ParentType, StringComparison.Ordinal))
            {
                return false;
            }

            int tag = GetConstructorTag(ctor);
            if (!seenTags.Add(tag))
            {
                return false;
            }

            result.Add((ctor, tag));
        }

        plan = result;
        return true;
    }

    /// <summary>
    /// A sub-pattern that can never fail and binds at most one variable, so it is safe to extract
    /// behind a tag switch without a fallback path. A variable that names a nullary constructor is
    /// itself a constructor test and is therefore not trivial.
    /// </summary>
    private bool IsTrivialSubPattern(Pattern pattern)
    {
        if (pattern is Pattern.Wildcard)
        {
            return true;
        }

        return pattern is Pattern.Var v &&
            !(_constructorSymbols.TryGetValue(v.Name, out var ctor) && ctor.Arity == 0);
    }

    /// <summary>
    /// Lowers match arms as a single tag switch: read the ADT tag once, dispatch directly to the
    /// matching arm, and bind that constructor's fields without re-testing the tag. Sub-patterns
    /// are guaranteed trivial by <see cref="TryPlanTagSwitch"/>, so no per-arm failure path is
    /// needed; the switch default handles the (diagnosed) non-exhaustive case.
    /// </summary>
    private void LowerMatchArmsViaTagSwitch(
        Expr matchValue,
        IReadOnlyList<MatchCase> cases,
        List<(ConstructorSymbol Ctor, long Tag)> plan,
        int valueTemp,
        TypeRef valueType,
        TypeRef resultType,
        int resultSlot,
        string endLabel,
        string noMatchLabel,
        bool savedTailPos,
        string? reuseScrutineeName = null,
        TypeRef.TNamedType? runtimeReuseType = null,
        bool normalizeStaticStringArms = false,
        LoweredValueRequest request = default)
    {
        int tagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, valueTemp));

        var armLabels = new string[cases.Count];
        var switchCases = new List<(long Tag, string Label)>(cases.Count);
        for (int i = 0; i < cases.Count; i++)
        {
            armLabels[i] = NewLabel("match_arm");
            switchCases.Add((plan[i].Tag, armLabels[i]));
        }

        Emit(new IrInst.SwitchTag(tagTemp, switchCases, noMatchLabel));

        for (int i = 0; i < cases.Count; i++)
        {
            Emit(new IrInst.Label(armLabels[i]));

            var caseScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal);
            _scopes.Push(caseScope);
            EmitArenaWatermark();
            PushOwnershipScope();

            EmitTagSwitchArmPattern(matchValue, cases, plan, i, valueTemp, valueType, noMatchLabel);

            ArmReuseContext reuseContext = PublishTagSwitchArmReuseToken(
                cases,
                plan,
                i,
                valueTemp,
                reuseScrutineeName,
                runtimeReuseType);

            LowerMatchArmBodyIntoResult(cases, i, resultType, resultSlot, endLabel, savedTailPos, reuseContext, normalizeStaticStringArms, request);

            _scopes.Pop();
        }
    }

    /// <summary>
    /// Infers one tag-switch arm's pattern type and binds its payload fields into the arm scope.
    /// </summary>
    private void EmitTagSwitchArmPattern(Expr matchValue, IReadOnlyList<MatchCase> cases, List<(ConstructorSymbol Ctor, long Tag)> plan, int i, int valueTemp, TypeRef valueType, string noMatchLabel)
    {
        var patternBindings = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        var patternType = InferPatternType(cases[i].Pattern, patternBindings);
        Unify(valueType, patternType);

        // The tag is already matched by the switch; only extract and bind payload fields.
        if (cases[i].Pattern is Pattern.Constructor ctorPattern)
        {
            EmitConstructorFieldBindings(plan[i].Ctor, ctorPattern, valueTemp, noMatchLabel, patternBindings);
        }

        TrackOwnedBindingsInPattern(patternBindings);
        TrackRuntimeManagedMatchScrutinee(matchValue, valueTemp, valueType, patternBindings, cases[i].Pattern);
    }

    private void TrackRuntimeManagedMatchScrutinee(
        Expr matchValue,
        int valueTemp,
        TypeRef valueType,
        IReadOnlyDictionary<string, TypeRef> patternBindings,
        Pattern armPattern)
    {
        // Task/coroutine bodies still use scheduler-owned arenas. Until cross-thread RC publication
        // exists, their match payloads must stay on that path instead of entering local RC transfer.
        //
        // Dynamic capability dispatch is the other exclusion: a pending one-shot post's closure (see
        // Lowering.Capabilities.cs's LivePostsIndex) must survive until its handle folds it, and while
        // one is pending an escaping scope's arena copy-out is skipped rather than performed (see
        // TryEmitScopeCopyOut/LowerCallCopyOutResult), leaving the value as a raw arena pointer instead
        // of the RC-managed representation this tracking assumes. That skip is only ever reachable once
        // some `handle` has installed a frame, so a program with no `handle` anywhere can never take it
        // — the current function's live-handler effect, not a whole-program capability or handler
        // count, is the right test here.
        if (!AllowsAsyncIndependentRcPlacement || !AllowsOrdinaryRcPlacement)
        {
            return;
        }

        TrackPerceusPatternBindingOwners(patternBindings, armPattern);

        (string? ownerName, HashSet<string>? independentlyTrackedFieldNames) =
            TrackRuntimeManagedMatchScrutineeOwner(matchValue, valueTemp, valueType, patternBindings, armPattern);

        if (ownerName is null)
        {
            return;
        }

        foreach ((string bindingName, TypeRef bindingType) in patternBindings)
        {
            if (independentlyTrackedFieldNames?.Contains(bindingName) == true)
            {
                continue;
            }

            bool hasIndependentPatternOwner = _ownershipScopes.Count > 0
                && _ownershipScopes.Peek().TryGetValue(bindingName, out OwnershipInfo? bindingOwner)
                && bindingOwner.PerceusPatternOwner;
            if (!CanArenaReset(Prune(bindingType)) && !hasIndependentPatternOwner)
            {
                _ownershipAliases[bindingName] = ownerName;
            }
        }
    }

    /// <summary>
    /// Establishes (or resolves) the single owner whose drop is responsible for a match scrutinee,
    /// returning its tracking name together with the names of any fields that got their own
    /// independent ownership tracking (see <see cref="TrackIndependentlyOwnedMatchFields"/>) and so
    /// must not also be aliased to it by the caller. A scrutinee that is itself an existing owned
    /// variable resolves to that variable's own owner unchanged — this narrower fix only applies to a
    /// freshly constructed scrutinee, which is referenced nowhere else.
    /// </summary>
    private (string? OwnerName, HashSet<string>? IndependentlyTrackedFieldNames) TrackRuntimeManagedMatchScrutineeOwner(
        Expr matchValue,
        int valueTemp,
        TypeRef valueType,
        IReadOnlyDictionary<string, TypeRef> patternBindings,
        Pattern armPattern)
    {
        if (matchValue is Expr.Var variable
            && LookupOwnedValue(variable.Name) is { RuntimeManaged: true })
        {
            return (ResolveOwnershipAlias(variable.Name), null);
        }

        if (matchValue is Expr.Var || !IsRuntimeManagedResultTemp(valueTemp))
        {
            return (null, null);
        }

        TypeRef ownedType = Prune(valueType);
        string? typeName = GetOwnedTypeName(ownedType);
        if (typeName is null)
        {
            return (null, null);
        }

        string ownerName = $"$match_rc_{valueTemp}";
        int ownerSlot = NewLocal();
        Emit(new IrInst.StoreLocal(ownerSlot, valueTemp));

        // A freshly constructed scrutinee is referenced nowhere else, so every one of its top-level
        // fields bound directly by a plain name (not `_`, and not a nested sub-pattern one level
        // further in) is extracted by EmitConstructorFieldBindings via a bare GetAdtField — never
        // duplicated. The moment that happens, ownership of that one field has already moved from this
        // wrapper to its own binding, so this wrapper's own eventual drop must never recurse into it:
        // doing so, on top of whatever that field's own independent tracking later does with it, would
        // release the same allocation twice. Give each such field its own independent tracking (instead
        // of only ever aliasing its name to this wrapper's), and record its index so this wrapper's own
        // drop skips it. Nested sub-patterns and wildcard fields stay on the pre-existing aliasing path.
        (HashSet<int>? excludedFieldIndices, HashSet<string>? trackedNames, ConstructorSymbol? matchedCtor) =
            TrackIndependentlyOwnedMatchFields(ownedType, armPattern, patternBindings);

        TrackOwnedValue(
            ownerName,
            ownerSlot,
            typeName,
            isResource: false,
            definitionSpan: null,
            ownedType,
            runtimeManaged: true,
            runtimeConstructor: excludedFieldIndices is not null ? matchedCtor : null,
            excludedDropFieldIndices: excludedFieldIndices);

        return (ownerName, excludedFieldIndices is not null ? trackedNames : null);
    }

    /// <summary>
    /// For a freshly constructed match scrutinee, finds every top-level constructor field that this
    /// arm's pattern binds directly to a plain name (not a nested sub-pattern, not a wildcard) whose
    /// own type is not arena-resettable, gives each one its own independent runtime-managed ownership
    /// entry (overwriting whatever placeholder <see cref="TrackOwnedBindingsInPattern"/> already
    /// registered for it), and returns the set of field indices this represents so the caller can
    /// exclude them from the wrapper's own recursive drop. Returns a null field-index set (and leaves
    /// every binding on the ordinary aliasing path) when the pattern is not a single top-level
    /// constructor pattern matching the scrutinee's own arity, so any nested or otherwise unusual shape
    /// is left entirely on the pre-existing, more conservative behavior.
    /// </summary>
    private (HashSet<int>? FieldIndices, HashSet<string>? FieldNames, ConstructorSymbol? Constructor) TrackIndependentlyOwnedMatchFields(
        TypeRef ownedType,
        Pattern armPattern,
        IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        if (ownedType is not TypeRef.TNamedType
            || armPattern is not Pattern.Constructor ctorPattern
            || !TryResolveConstructorSymbol(
                ctorPattern.Name,
                GetSpan(ctorPattern),
                out ConstructorSymbol? matchedCtor)
            || ctorPattern.Patterns.Count != matchedCtor.Arity)
        {
            return (null, null, null);
        }

        HashSet<int>? fieldIndices = null;
        HashSet<string>? fieldNames = null;
        for (int i = 0; i < ctorPattern.Patterns.Count; i++)
        {
            if (ctorPattern.Patterns[i] is not Pattern.Var { Name: var fieldName }
                || string.Equals(fieldName, "_", StringComparison.Ordinal)
                || !patternBindings.TryGetValue(fieldName, out TypeRef? fieldTypeOrNull)
                || Lookup(fieldName) is not Binding.Local fieldLocal)
            {
                continue;
            }

            TypeRef prunedFieldType = Prune(fieldTypeOrNull);
            if (CanArenaReset(prunedFieldType))
            {
                continue;
            }

            string? fieldOwnedTypeName = GetOwnedTypeName(prunedFieldType);
            if (fieldOwnedTypeName is null || GetResourceTypeName(prunedFieldType) is not null)
            {
                // Resources have their own deterministic-cleanup lifecycle (CleanupResource, moved/
                // closed diagnostics) entirely separate from RC drop bookkeeping; leave a resource-typed
                // field on the pre-existing aliasing path rather than folding it into this mechanism.
                continue;
            }

            TrackOwnedValue(
                fieldName,
                fieldLocal.Slot,
                fieldOwnedTypeName,
                isResource: false,
                fieldLocal.DefinitionSpan,
                prunedFieldType,
                runtimeManaged: true);

            fieldIndices ??= [];
            fieldIndices.Add(i);
            fieldNames ??= [];
            fieldNames.Add(fieldName);
        }

        return (fieldIndices, fieldNames, fieldIndices is not null ? matchedCtor : null);
    }

    /// <summary>
    /// Joins canonical pattern ownership to exact emitted slots. An escaping or embedded binding gets
    /// an ordinary scope owner immediately, so moves and lexical cleanup use the same machinery as all
    /// other owned values. Its physical RC representation is selected only after the root TCO
    /// parameter's type and placement have settled.
    /// </summary>
    private void TrackPerceusPatternBindingOwners(
        IReadOnlyDictionary<string, TypeRef> patternBindings,
        Pattern armPattern)
    {
        if (_tcoCtx is not { } tco)
        {
            return;
        }

        foreach (Pattern.Var binder in PatternVariableBinders(armPattern))
        {
            if (!patternBindings.TryGetValue(binder.Name, out TypeRef? bindingType)
                || !tco.TryGetPatternBinding(
                    binder,
                    out int localSlot,
                    out PatternBindingOwnershipFact? ownership)
                || ownership is null
                || ownership.RootParameterOrdinal < 0
                || ownership.RootParameterOrdinal >= tco.ParamSlots.Count)
            {
                continue;
            }

            _patternBindingPlacementSites.Add(new PatternBindingPlacementSite(
                localSlot,
                tco.ParamSlots[ownership.RootParameterOrdinal],
                _inst.Count,
                bindingType,
                ownership));

            if (!ownership.RequiresProtectiveDup)
            {
                continue;
            }

            TypeRef prunedType = Prune(bindingType);
            TrackOwnedValue(
                binder.Name,
                localSlot,
                GetOwnedTypeName(prunedType) ?? "PatternBinding",
                isResource: false,
                GetSpan(binder),
                bindingType,
                perceusPatternOwner: true,
                perceusRootParameterSlot: tco.ParamSlots[ownership.RootParameterOrdinal]);
        }
    }

    private int TransferDirectRuntimeManagedMatchResult(Expr body, int bodyTemp)
    {
        Expr result = body;
        while (result is Expr.Let let)
        {
            result = let.Body;
        }

        if (result is Expr.Var variable
            && LookupOwnedValue(variable.Name) is { IsDropped: false } owner)
        {
            return TransferVariableRuntimeManagedMatchResult(variable, bodyTemp, owner);
        }

        return bodyTemp;
    }

    private int TransferVariableRuntimeManagedMatchResult(
        Expr.Var variable,
        int bodyTemp,
        OwnershipInfo owner)
    {
        // Returning the owner binding itself transfers its one reference into the match result.
        // EmitRuntimeManagedParentFieldTransfer is only for an extracted child whose protective
        // parent must still be released. Treating the root as its own child drops the unique cell
        // and leaves the match join holding a dangling pointer.
        Binding? resultBinding = Lookup(variable.Name);
        int resultSlot = resultBinding switch
        {
            Binding.Local local => local.Slot,
            Binding.Scheme scheme => scheme.Slot,
            _ => -1,
        };
        if (resultSlot == owner.Slot)
        {
            if (!owner.RuntimeManaged)
            {
                return bodyTemp;
            }

            int duplicatedTemp = DuplicateRuntimeManagedMatchResult(bodyTemp, owner);
            Emit(new IrInst.RcDrop(
                bodyTemp,
                owner.TypeName,
                owner.Slot,
                RuntimeManaged: true,
                MayBeEmpty: owner.Type is TypeRef.TList));
            owner.ReleaseKind = ResourceReleaseKind.AutoDropped;
            return duplicatedTemp;
        }

        // A captured binding is owned by the current closure environment, not by the outer
        // function's local slot retained in ownership provenance. Returning it from a match arm
        // must retain the captured value; attempting the ordinary local-parent transfer would
        // emit the enclosing frame's slot number into this lifted function.
        if (Lookup(variable.Name) is Binding.Env or Binding.EnvScheme)
        {
            if (!owner.RuntimeManaged)
            {
                return bodyTemp;
            }

            int duplicatedTemp = NewTemp();
            Emit(new IrInst.RcDup(duplicatedTemp, bodyTemp, RuntimeManaged: true));
            return duplicatedTemp;
        }

        if (owner.PerceusPatternOwner)
        {
            int duplicatedTemp = NewTemp();
            Emit(new IrInst.RcDup(duplicatedTemp, bodyTemp));
            return duplicatedTemp;
        }

        if (!owner.RuntimeManaged)
        {
            return bodyTemp;
        }

        return EmitRuntimeManagedParentFieldTransfer(owner, bodyTemp);
    }

    private int DuplicateRuntimeManagedMatchResult(int bodyTemp, OwnershipInfo owner)
    {
        int duplicatedTemp = NewTemp();
        Emit(new IrInst.RcDup(
            duplicatedTemp,
            bodyTemp,
            RuntimeManaged: true,
            MayBeEmpty: owner.Type is TypeRef.TList));
        return duplicatedTemp;
    }

    /// <summary>
    /// Publishes one tag-switch arm's dead accumulator node as a reuse token when eligible.
    /// Returns the reuse-token count before publishing so the caller can drop any token the
    /// arm body didn't consume.
    /// </summary>
    private ArmReuseContext PublishTagSwitchArmReuseToken(
        IReadOnlyList<MatchCase> cases,
        List<(ConstructorSymbol Ctor, long Tag)> plan,
        int i,
        int valueTemp,
        string? reuseScrutineeName,
        TypeRef.TNamedType? runtimeReuseType)
    {
        // In-place reuse (#2): make this arm's dead accumulator node available as a reuse token
        // for a same-arity constructor in the body. Only when the body doesn't reference the
        // accumulator again (cell is dead) — payload fields are already bound into temps above.
        int reuseTokensBefore = _reuseTokens.Count;
        // Every arm here matched a constructor by tag (plan[i].Ctor is authoritative — a bare
        // nullary pattern like `Leaf` parses as Pattern.Var, so don't gate on Pattern.Constructor).
        // Nullary cells (Arity 0, e.g. Leaf) are reusable too, which keeps a recursive rebuild's
        // whole result below the watermark.
        if (reuseScrutineeName is not null
            && !ExprReferencesName(cases[i].Body, reuseScrutineeName))
        {
            RuntimeReuseCleanup? runtimeCleanup = runtimeReuseType is null
                ? null
                : CreateRuntimeReuseCleanup(
                    runtimeReuseType,
                    plan[i].Ctor,
                    cases[i].Pattern);
            int tokenTemp = NewTemp();
            Emit(new IrInst.DropReuse(
                tokenTemp,
                valueTemp,
                plan[i].Ctor.Arity,
                runtimeCleanup is not null));
            ReuseToken token = new(
                tokenTemp,
                valueTemp,
                plan[i].Ctor.Arity,
                runtimeCleanup,
                reuseScrutineeName,
                ResolveSourceLocation(AstSpans.GetOrDefault(cases[i].Pattern)));
            _reuseTokens.Add(token);
            RecordReuseTokenProduction(token);
            RecordReuseTokenFieldBindings(tokenTemp, cases[i].Pattern, cases[i].Body);
        }

        return new ArmReuseContext(reuseTokensBefore, []);
    }

    private RuntimeReuseCleanup CreateRuntimeReuseCleanup(
        TypeRef.TNamedType runtimeType,
        ConstructorSymbol constructor,
        Pattern pattern)
    {
        var transferableFields = new Dictionary<string, int>(StringComparer.Ordinal);
        if (pattern is Pattern.Constructor constructorPattern)
        {
            List<OrdinaryHeapLayoutChild> children =
                GetOwnedOrdinaryHeapChildren(runtimeType, constructor);
            foreach (OrdinaryHeapLayoutChild child in children)
            {
                if (child.Index < constructorPattern.Patterns.Count
                    && child.Type is TypeRef.TNamedType
                    && constructorPattern.Patterns[child.Index] is Pattern.Var binding
                    && !_constructorSymbols.ContainsKey(binding.Name))
                {
                    transferableFields[binding.Name] = child.Index;
                }
            }
        }

        return new RuntimeReuseCleanup(runtimeType, constructor, transferableFields);
    }

    private bool ValidateTuplePatternArity(TypeRef valueType, Pattern pattern)
    {
        if (valueType is not TypeRef.TTuple tupleType || pattern is not Pattern.Tuple tuplePattern)
        {
            return false;
        }

        if (tupleType.Elements.Count == tuplePattern.Elements.Count)
        {
            return false;
        }

        ReportDiagnostic(GetSpan(pattern), $"Tuple pattern arity mismatch: expected {tupleType.Elements.Count} element(s) but got {tuplePattern.Elements.Count}.");
        return true;
    }

    private void RegisterPatternVariableBindings(IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        foreach (var (name, type) in bindingTypes)
        {
            int slot = NewLocal();
            _scopes.Peek()[name] = new Binding.Local(slot, Prune(type));
        }
    }

    private (int Temp, TypeRef Type) LowerEmptyList()
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, 0));
        return (t, new TypeRef.TList(NewTypeVar()));
    }

    private (int Temp, TypeRef Type) LowerConsCell(
        int headTemp,
        int tailTemp,
        TypeRef headType,
        TypeRef tailType,
        SourceLocation? location,
        LoweredValueRequest request)
    {
        var listType = new TypeRef.TList(headType);
        Unify(tailType, listType);

        int nodeTemp = NewTemp();
        bool runtimeManaged =
            request.EmitsRuntime(LoweredValueRuntimeRepresentation.List)
            && IsRuntimeManageableListElement(headType, headTemp);
        bool reusedCell = EmitListCellAllocation(nodeTemp, runtimeManaged, location);
        Emit(new IrInst.StoreMemOffset(nodeTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex), headTemp));
        Emit(new IrInst.StoreMemOffset(nodeTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex), tailTemp));
        if (!reusedCell && runtimeManaged && request.RuntimeTcoListTailSlot is not null)
        {
            MarkRuntimeManagedTemp(nodeTemp);
        }
        return (nodeTemp, Prune(listType));
    }

    private bool EmitListCellAllocation(
        int nodeTemp,
        bool runtimeManaged,
        SourceLocation? location)
    {
        // Reconciled reuse-token rule: a list
        // cons cell satisfied by ANY reuse token — arena or (hypothetically) runtime-managed — is
        // ALWAYS an in-place arena reuse. There is no list-specific runtime-managed reuse cleanup (see
        // the assert below), so `runtimeManaged` only ever governs the FRESH-allocation branch; it must
        // never also drive the post-emission bookkeeping when the reuse branch actually ran. Before this
        // fix, the bookkeeping below keyed only off `runtimeManaged` (and the ambient TCO-tail slot),
        // independent of which branch emitted the cell — so a reuse-token hit could mark a cell that was
        // just given a plain arena AllocReusing (RuntimeManaged: false, no RC header) as runtime-managed
        // anyway, which would make a later RcDup/RcDrop read/write a bogus header at that address. Track
        // which branch ran and gate the bookkeeping on that fact directly, instead of re-deriving it from
        // `runtimeManaged` a second time after the emission decision has already been made.
        ReuseTokenMatch tokenMatch = TryConsumeReuseToken(
            2,
            runtimeManaged,
            listCell: true,
            targetConstructor: "::",
            location);
        if (tokenMatch.Token is { } reuseToken)
        {
            Debug.Assert(
                reuseToken.RuntimeCleanup is null,
                "Runtime-managed list reuse requires list-specific child cleanup.");
            Emit(new IrInst.AllocReusing(
                nodeTemp,
                0,
                2,
                reuseToken.Temp,
                RuntimeManaged: false,
                ListCell: true));
            _reuseResultTemps.Add(nodeTemp);
            RecordReuseTokenDisposition(
                reuseToken,
                ReuseDecisionOutcome.Consumed,
                ReuseDecisionReason.CompatibleTokenConsumed,
                nodeTemp,
                "::",
                location);
            return true;
        }

        Emit(new IrInst.Alloc(nodeTemp, HeapLayouts.List.FixedAllocationSizeBytes, runtimeManaged));
        if (ShouldRecordReuseFallback(tokenMatch))
        {
            RecordReuseFallbackAllocation(
                null,
                nodeTemp,
                "::",
                location,
                ReuseDecisionOutcome.Allocated,
                ReuseFallbackReason(tokenMatch),
                runtimeManaged
                    ? ReuseFallbackAllocationKind.RuntimeRc
                    : ReuseFallbackAllocationKind.Arena,
                fieldCount: 2,
                listCell: true,
                runtimeManaged);
        }
        return false;
    }

    private bool IsRuntimeManageableListElement(TypeRef type, int temp)
    {
        TypeRef elementType = Prune(type);
        return CanArenaReset(elementType)
            || IsRuntimeManagedResultTemp(temp)
                && elementType is TypeRef.TTuple or TypeRef.TStr or TypeRef.TBytes or TypeRef.TBigInt
                    or TypeRef.TList or TypeRef.TNamedType;
    }

    private TypeRef InferPatternType(Pattern pattern, Dictionary<string, TypeRef> bindings)
    {
        if (pattern is Pattern.Record or Pattern.As or Pattern.Or)
        {
            return InferExtendedPatternType(pattern, bindings);
        }

        switch (pattern)
        {
            case Pattern.EmptyList:
                return new TypeRef.TList(NewTypeVar());

            case Pattern.Wildcard:
                return NewTypeVar();

            case Pattern.Var v:
                // Check if this identifier is a known nullary constructor
                if (TryResolveConstructorSymbol(v.Name, GetSpan(v), out var nullaryCtor)
                    && nullaryCtor.Arity == 0)
                {
                    return InstantiateAdtType(nullaryCtor);
                }
                if (bindings.ContainsKey(v.Name))
                {
                    ReportDiagnostic(GetSpan(pattern), $"Duplicate binding '{v.Name}' in pattern.");
                    return bindings[v.Name];
                }
                var varType = NewTypeVar();
                bindings[v.Name] = varType;
                RecordHoverType(GetSpan(v), v.Name, varType);
                return varType;

            case Pattern.Cons c:
                var headType = InferPatternType(c.Head, bindings);
                var tailType = InferPatternType(c.Tail, bindings);
                var listType = new TypeRef.TList(headType);
                Unify(tailType, listType);
                return listType;

            case Pattern.Tuple tuple:
                return new TypeRef.TTuple(tuple.Elements.Select(p => InferPatternType(p, bindings)).ToList());

            case Pattern.Constructor ctor:
                return InferConstructorPatternType(ctor, bindings);

            case Pattern.IntLit:
                return new TypeRef.TInt();

            case Pattern.StrLit:
                return new TypeRef.TStr();

            case Pattern.RuneLit:
                return new TypeRef.TRune();

            case Pattern.BoolLit:
                return new TypeRef.TBool();

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    private TypeRef InferExtendedPatternType(Pattern pattern, Dictionary<string, TypeRef> bindings)
    {
        if (pattern is Pattern.Record record)
        {
            return InferRecordPatternType(record, bindings);
        }

        if (pattern is Pattern.Or orPattern)
        {
            return InferOrPatternType(orPattern, bindings);
        }

        var asPattern = (Pattern.As)pattern;
        TypeRef innerType = InferPatternType(asPattern.Inner, bindings);
        if (bindings.ContainsKey(asPattern.Name))
        {
            ReportDiagnostic(GetSpan(asPattern), $"Duplicate binding '{asPattern.Name}' in pattern.");
        }
        else
        {
            bindings[asPattern.Name] = innerType;
            RecordHoverType(AstSpans.GetAsPatternNameOrDefault(asPattern), asPattern.Name, innerType);
        }

        if (IsResourceBearing(Prune(innerType))
            && PatternBindings(asPattern.Inner).Any(name =>
                bindings.TryGetValue(name, out TypeRef? type) && IsResourceBearing(Prune(type))))
        {
            ReportDiagnostic(GetSpan(asPattern), $"Resource-bearing pattern alias '{asPattern.Name}' cannot coexist with a nested resource-bearing binding.");
        }

        return innerType;
    }

    private TypeRef InferRecordPatternType(Pattern.Record pattern, Dictionary<string, TypeRef> bindings)
    {
        if (!TryResolveConstructorSymbol(pattern.TypeName, GetSpan(pattern), out ConstructorSymbol? constructor)
            || constructor.DeclaringSyntax.FieldNames.Count == 0)
        {
            ReportDiagnostic(GetSpan(pattern), $"Unknown record type '{pattern.TypeName}' in pattern.");
            foreach ((string _, Pattern fieldPattern) in pattern.Fields)
            {
                InferPatternType(fieldPattern, bindings);
            }

            return NewTypeVar();
        }

        TypeRef.TNamedType resultType = InstantiateAdtType(constructor);
        IReadOnlyList<string> fieldNames = constructor.DeclaringSyntax.FieldNames;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string fieldName, Pattern fieldPattern) in pattern.Fields)
        {
            if (!seen.Add(fieldName))
            {
                ReportDiagnostic(GetSpan(fieldPattern), $"Duplicate field '{fieldName}' in record pattern for '{pattern.TypeName}'.");
            }

            int fieldIndex = FindFieldIndex(fieldNames, fieldName);
            TypeRef patternType = InferPatternType(fieldPattern, bindings);
            if (fieldIndex < 0)
            {
                ReportDiagnostic(GetSpan(fieldPattern), $"Record type '{pattern.TypeName}' has no field '{fieldName}'.");
                continue;
            }

            Unify(InstantiateConstructorParameterType(constructor, fieldIndex, resultType), patternType);
        }

        return resultType;
    }

    private TypeRef InferOrPatternType(Pattern.Or pattern, Dictionary<string, TypeRef> bindings)
    {
        TypeRef resultType = NewTypeVar();
        var outerNames = new HashSet<string>(bindings.Keys, StringComparer.Ordinal);
        Dictionary<string, TypeRef>? expectedBindings = null;
        var allIntroducedBindings = new Dictionary<string, TypeRef>(StringComparer.Ordinal);

        foreach (Pattern alternative in pattern.Alternatives)
        {
            var alternativeBindings = new Dictionary<string, TypeRef>(bindings, StringComparer.Ordinal);
            TypeRef alternativeType = InferPatternType(alternative, alternativeBindings);
            Unify(resultType, alternativeType);

            var introduced = alternativeBindings
                .Where(binding => !outerNames.Contains(binding.Key))
                .ToDictionary(binding => binding.Key, binding => binding.Value, StringComparer.Ordinal);
            foreach ((string name, TypeRef type) in introduced)
            {
                allIntroducedBindings.TryAdd(name, type);
            }
            if (expectedBindings is null)
            {
                expectedBindings = introduced;
                continue;
            }

            if (!expectedBindings.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(introduced.Keys))
            {
                ReportDiagnostic(
                    GetSpan(alternative),
                    "Every alternative of an or-pattern must bind exactly the same names.");
                continue;
            }

            foreach ((string name, TypeRef type) in introduced)
            {
                Unify(expectedBindings[name], type);
            }
        }

        foreach ((string name, TypeRef type) in allIntroducedBindings)
        {
            bindings[name] = type;
        }

        return resultType;
    }

    private TypeRef InferConstructorPatternType(
        Pattern.Constructor pattern,
        Dictionary<string, TypeRef> bindings)
    {
        string name = pattern.Name;
        IReadOnlyList<Pattern> patterns = pattern.Patterns;
        if (!TryResolveConstructorSymbol(name, GetSpan(pattern), out var ctor))
        {
            var span = patterns.Count > 0
                ? TextSpan.FromBounds(GetSpan(patterns[0]).Start, GetSpan(patterns[^1]).End)
                : TextSpan.FromBounds(0, 1);
            ReportDiagnostic(span, $"Unknown constructor '{name}' in pattern.{BuildUnknownConstructorHint(name)}");
            foreach (var p in patterns)
            {
                InferPatternType(p, bindings);
            }
            return NewTypeVar();
        }

        if (patterns.Count != ctor.Arity)
        {
            var span = patterns.Count > 0 ? TextSpan.FromBounds(GetSpan(patterns[0]).Start, GetSpan(patterns[^1]).End) : GetSpan(ctor.DeclaringSyntax);
            ReportDiagnostic(span, $"Constructor '{name}' expects {ctor.Arity} argument(s) but pattern has {patterns.Count}. Expected shape: {FormatConstructorShape(ctor)}.");
            foreach (var p in patterns)
            {
                InferPatternType(p, bindings);
            }
            return new TypeRef.TNever();
        }

        var resultType = InstantiateAdtType(ctor);

        // Infer types for sub-patterns (bind variables into the branch scope)
        for (int i = 0; i < patterns.Count; i++)
        {
            var patternType = InferPatternType(patterns[i], bindings);
            var parameterType = InstantiateConstructorParameterType(ctor, i, resultType);
            Unify(parameterType, patternType);
        }

        return resultType;
    }

    /// <summary>
    /// Emits tests and bindings for a pattern. Any mismatch jumps to
    /// <paramref name="failLabel"/>, letting the enclosing match arm perform
    /// guard failure and arena cleanup in one place.
    /// </summary>
    private void EmitPattern(
        Pattern pattern,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots = null)
    {
        if (TryEmitExtendedPattern(pattern, valueTemp, failLabel, bindingTypes, bindingSlots))
        {
            return;
        }

        switch (pattern)
        {
            case Pattern.EmptyList:
                EmitRequireZero(valueTemp, failLabel);
                return;
            case Pattern.Wildcard:
                return;
            case Pattern.Var v:
                EmitVarPattern(v, valueTemp, failLabel, bindingTypes, bindingSlots);
                return;
            case Pattern.Cons c:
                EmitRequireNonZero(valueTemp, failLabel);
                int headTemp = NewTemp();
                int tailTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(headTemp, valueTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
                Emit(new IrInst.LoadMemOffset(tailTemp, valueTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
                PropagateExtractedBytesProvenance(valueTemp, headTemp, c.Head, bindingTypes);
                EmitPattern(c.Head, headTemp, failLabel, bindingTypes, bindingSlots);
                EmitPattern(c.Tail, tailTemp, failLabel, bindingTypes, bindingSlots);
                return;
            case Pattern.Tuple tuple:
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    int elemTemp = NewTemp();
                    Emit(new IrInst.LoadMemOffset(elemTemp, valueTemp, i * 8));
                    PropagateExtractedBytesProvenance(
                        valueTemp, elemTemp, tuple.Elements[i], bindingTypes);
                    EmitPattern(tuple.Elements[i], elemTemp, failLabel, bindingTypes, bindingSlots);
                }
                return;

            case Pattern.Constructor ctor:
                EmitConstructorPattern(ctor, valueTemp, failLabel, bindingTypes, bindingSlots);
                return;

            case Pattern.IntLit intLit:
                EmitRequireIntEqual(valueTemp, intLit.Value, failLabel);
                return;

            case Pattern.StrLit strLit:
                EmitRequireStrEqual(valueTemp, strLit.Value, failLabel);
                return;

            case Pattern.RuneLit runeLit:
                EmitRequireIntEqual(valueTemp, runeLit.Value, failLabel);
                return;

            case Pattern.BoolLit boolLit:
                EmitRequireBoolEqual(valueTemp, boolLit.Value, failLabel);
                return;

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    private bool TryEmitExtendedPattern(
        Pattern pattern,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots)
    {
        if (pattern is Pattern.Record record)
        {
            EmitRecordPattern(record, valueTemp, failLabel, bindingTypes, bindingSlots);
            return true;
        }

        if (pattern is Pattern.As asPattern)
        {
            EmitPatternBinding(asPattern.Name, valueTemp, bindingTypes, bindingSlots, asPattern);
            EmitPattern(asPattern.Inner, valueTemp, failLabel, bindingTypes, bindingSlots);
            return true;
        }

        if (pattern is not Pattern.Or orPattern)
        {
            return false;
        }

        string successLabel = NewLabel("pattern_or_success");
        for (int i = 0; i < orPattern.Alternatives.Count; i++)
        {
            bool last = i == orPattern.Alternatives.Count - 1;
            string alternativeFailLabel = last ? failLabel : NewLabel("pattern_or_next");
            EmitPattern(orPattern.Alternatives[i], valueTemp, alternativeFailLabel, bindingTypes, bindingSlots);
            Emit(new IrInst.Jump(successLabel));
            if (!last)
            {
                Emit(new IrInst.Label(alternativeFailLabel));
            }
        }

        Emit(new IrInst.Label(successLabel));
        return true;
    }

    /// <summary>
    /// Emits a variable pattern: a variable naming a known nullary constructor is a tag test,
    /// any other variable binds the matched value into a fresh local.
    /// </summary>
    private void EmitVarPattern(
        Pattern.Var v,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots)
    {
        // If this is a known nullary constructor, emit a tag check instead of binding
        if (TryResolveConstructorSymbol(v.Name, GetSpan(v), out var nullaryCtor)
            && nullaryCtor.Arity == 0)
        {
            EmitRequireNonZero(valueTemp, failLabel);
            EmitRequireTagMatch(valueTemp, GetConstructorTag(nullaryCtor), failLabel);
            return;
        }
        EmitPatternBinding(v.Name, valueTemp, bindingTypes, bindingSlots, v);
    }

    private void EmitPatternBinding(
        string name,
        int valueTemp,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots,
        Pattern binder)
    {
        int slot;
        if (bindingSlots is not null && bindingSlots.TryGetValue(name, out int existingSlot))
        {
            slot = existingSlot;
        }
        else
        {
            slot = NewLocal();
            bindingSlots?[name] = slot;
        }

        Emit(new IrInst.StoreLocal(slot, valueTemp));
        RecordLocalBytesProvenance(slot, valueTemp);
        RecordLocalDebugInfo(slot, name, bindingTypes[name]);
        _scopes.Peek()[name] = new Binding.Local(slot, Prune(bindingTypes[name]));
        if (binder is Pattern.Var variable)
        {
            _tcoCtx?.RegisterPatternBindingSlot(variable, slot);
        }
    }

    private void EmitConstructorPattern(
        Pattern.Constructor ctor,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots)
    {
        if (!TryResolveConstructorSymbol(ctor.Name, GetSpan(ctor), out var ctorSym))
        {
            // Unknown constructor — already diagnosed in InferPatternType
            return;
        }

        if (ctorSym.ParentType is { } parentType && _typeSymbols[parentType].IsZeroCost)
        {
            EmitPattern(ctor.Patterns[0], valueTemp, failLabel, bindingTypes, bindingSlots);
            return;
        }

        // Ordinary ADT constructors are tagged heap allocations: [ctorTag, ...payloads].
        // Check ptr != null, then check the tag matches this constructor.
        EmitRequireNonZero(valueTemp, failLabel);
        EmitRequireTagMatch(valueTemp, GetConstructorTag(ctorSym), failLabel);

        EmitConstructorFieldBindings(ctorSym, ctor, valueTemp, failLabel, bindingTypes, bindingSlots);
    }

    /// <summary>
    /// Extracts each constructor payload field and binds its sub-pattern, without emitting the
    /// null/tag check. Shared by the linear pattern path (after its own tag check) and the
    /// tag-switch path (where the switch has already dispatched on the tag).
    /// </summary>
    private void EmitConstructorFieldBindings(
        ConstructorSymbol ctorSym,
        Pattern.Constructor ctor,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots = null)
    {
        if (ctorSym.ParentType is { } parentType && _typeSymbols[parentType].IsZeroCost)
        {
            EmitPattern(ctor.Patterns[0], valueTemp, failLabel, bindingTypes, bindingSlots);
            return;
        }

        for (int i = 0; i < ctorSym.Arity && i < ctor.Patterns.Count; i++)
        {
            // Extract payload at each field index and bind sub-patterns.
            int payloadTemp = NewTemp();
            Emit(new IrInst.GetAdtField(payloadTemp, valueTemp, i));
            PropagateExtractedBytesProvenance(
                valueTemp,
                payloadTemp,
                ctor.Patterns[i],
                bindingTypes);
            EmitPattern(ctor.Patterns[i], payloadTemp, failLabel, bindingTypes, bindingSlots);
        }
    }

    private void EmitRecordPattern(
        Pattern.Record record,
        int valueTemp,
        string failLabel,
        IReadOnlyDictionary<string, TypeRef> bindingTypes,
        Dictionary<string, int>? bindingSlots)
    {
        if (!TryResolveConstructorSymbol(record.TypeName, GetSpan(record), out ConstructorSymbol? constructor)
            || constructor.DeclaringSyntax.FieldNames.Count == 0)
        {
            return;
        }

        EmitRequireNonZero(valueTemp, failLabel);
        EmitRequireTagMatch(valueTemp, GetConstructorTag(constructor), failLabel);
        foreach ((string fieldName, Pattern fieldPattern) in record.Fields)
        {
            int fieldIndex = FindFieldIndex(constructor.DeclaringSyntax.FieldNames, fieldName);
            if (fieldIndex < 0)
            {
                continue;
            }

            int fieldTemp = NewTemp();
            Emit(new IrInst.GetAdtField(fieldTemp, valueTemp, fieldIndex));
            PropagateExtractedBytesProvenance(valueTemp, fieldTemp, fieldPattern, bindingTypes);
            EmitPattern(fieldPattern, fieldTemp, failLabel, bindingTypes, bindingSlots);
        }
    }

    private void PropagateExtractedBytesProvenance(
        int aggregateTemp,
        int fieldTemp,
        Pattern fieldPattern,
        IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        if (!_tempOwnershipFacts.TryGetValue(
                aggregateTemp,
                out LoweredTempOwnershipFact? aggregateFact)
            || aggregateFact.BytesProvenance
                == BuiltinRegistry.BytesOwnershipProvenance.Unknown
            || !PatternContainsBytesBinding(fieldPattern, bindingTypes))
        {
            return;
        }

        RecordUnknownBorrowedTemp(fieldTemp, location: null);
        RefineTempBytesProvenance(fieldTemp, aggregateFact.BytesProvenance);
    }

    private bool PatternContainsBytesBinding(
        Pattern pattern,
        IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        return pattern switch
        {
            Pattern.Var variable => bindingTypes.TryGetValue(
                    variable.Name,
                    out TypeRef? type)
                && Prune(type) is TypeRef.TBytes,
            Pattern.Cons cons => PatternContainsBytesBinding(cons.Head, bindingTypes)
                || PatternContainsBytesBinding(cons.Tail, bindingTypes),
            Pattern.Tuple tuple => tuple.Elements.Any(element =>
                PatternContainsBytesBinding(element, bindingTypes)),
            Pattern.Constructor constructor => constructor.Patterns.Any(child =>
                PatternContainsBytesBinding(child, bindingTypes)),
            _ => false,
        };
    }

    private void EmitRequireTagMatch(int ptrTemp, int expectedTag, string failLabel)
    {
        int tagTemp = NewTemp();
        int eqTemp = NewTemp();
        int expectedTagTemp = NewTemp();
        Emit(new IrInst.GetAdtTag(tagTemp, ptrTemp));
        Emit(new IrInst.LoadConstInt(expectedTagTemp, expectedTag));
        Emit(new IrInst.CmpIntEq(eqTemp, tagTemp, expectedTagTemp));
        Emit(new IrInst.JumpIfFalse(eqTemp, failLabel));
    }

    private void EmitRequireZero(int valueTemp, string failLabel)
    {
        int zeroTemp = NewTemp();
        int eqTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntEq(eqTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(eqTemp, failLabel));
    }

    private void EmitRequireNonZero(int valueTemp, string failLabel)
    {
        int zeroTemp = NewTemp();
        int neTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        Emit(new IrInst.CmpIntNe(neTemp, valueTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(neTemp, failLabel));
    }

    private void EmitRequireIntEqual(int valueTemp, long expected, string failLabel)
    {
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(expectedTemp, expected));
        Emit(new IrInst.CmpIntEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    private void EmitRequireStrEqual(int valueTemp, string expected, string failLabel)
    {
        var label = InternString(expected);
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstStr(expectedTemp, label));
        Emit(new IrInst.CmpStrEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    private void EmitRequireBoolEqual(int valueTemp, bool expected, string failLabel)
    {
        // Booleans are represented as integers (0 = false, 1 = true).
        int expectedTemp = NewTemp();
        int cmpTemp = NewTemp();
        Emit(new IrInst.LoadConstBool(expectedTemp, expected));
        Emit(new IrInst.CmpIntEq(cmpTemp, valueTemp, expectedTemp));
        Emit(new IrInst.JumpIfFalse(cmpTemp, failLabel));
    }

    private static IEnumerable<string> PatternBindings(Pattern p)
    {
        if (p is Pattern.Var variable)
        {
            yield return variable.Name;
            yield break;
        }

        foreach (Pattern child in PatternBindingChildren(p))
        {
            foreach (string name in PatternBindings(child))
            {
                yield return name;
            }
        }

        if (p is Pattern.As asPattern)
        {
            yield return asPattern.Name;
        }
    }

    private static IEnumerable<Pattern.Var> PatternVariableBinders(Pattern pattern)
    {
        if (pattern is Pattern.Var variable)
        {
            yield return variable;
            yield break;
        }

        foreach (Pattern child in PatternBindingChildren(pattern))
        {
            foreach (Pattern.Var binder in PatternVariableBinders(child))
            {
                yield return binder;
            }
        }
    }

    private static IEnumerable<Pattern> PatternBindingChildren(Pattern pattern)
    {
        return pattern switch
        {
            Pattern.Cons cons => [cons.Head, cons.Tail],
            Pattern.Tuple tuple => tuple.Elements,
            Pattern.Constructor constructor => constructor.Patterns,
            Pattern.Record record => record.Fields.Select(field => field.Pattern),
            Pattern.As asPattern => [asPattern.Inner],
            Pattern.Or { Alternatives.Count: > 0 } orPattern => [orPattern.Alternatives[0]],
            _ => []
        };
    }

    /// <summary>
    /// Formats a non-exhaustive-match diagnostic listing missing constructor names.
    /// When the list is long (more than 5 entries), only the first few names are shown
    /// followed by a "... and N more" suffix so the message stays readable for large ADTs
    /// such as the 50+ variant IrInst type.
    /// </summary>
    private static string FormatMissingConstructorsDiagnostic(IReadOnlyList<string> missing)
    {
        const int DisplayLimit = 5;
        const int TruncateShowCount = 3;
        bool hasHiddenConstructors = missing.Any(name =>
            name.StartsWith(ProjectSupport.PrivateConstructorPrefix, StringComparison.Ordinal));
        string[] visibleMissing = missing
            .Where(name => !name.StartsWith(ProjectSupport.PrivateConstructorPrefix, StringComparison.Ordinal))
            .ToArray();
        if (visibleMissing.Length == 0)
        {
            return "Non-exhaustive match expression. Abstract or partially exported types require a catch-all pattern.";
        }

        IEnumerable<string> shown = visibleMissing.Length <= DisplayLimit
            ? visibleMissing
            : visibleMissing.Take(TruncateShowCount);

        var listed = string.Join(", ", shown.Select(name => $"'{name}'"));

        if (visibleMissing.Length > DisplayLimit)
        {
            int remainder = visibleMissing.Length - TruncateShowCount;
            listed += $", ... and {remainder} more";
        }

        if (hasHiddenConstructors)
        {
            listed += ", plus hidden constructor case(s) requiring a catch-all pattern";
        }

        return $"Non-exhaustive match expression. Missing constructor(s): {listed}.";
    }

    private bool IsDefinitelyExhaustive(IEnumerable<MatchCase> cases)
    {
        bool hasEmptyList = false;
        bool hasCons = false;

        foreach (var matchCase in cases)
        {
            if (IsCatchAllPattern(matchCase.Pattern) && matchCase.Guard is null)
            {
                return true;
            }

            switch (matchCase.Pattern)
            {
                case Pattern.EmptyList:
                    hasEmptyList = true;
                    break;
                case Pattern.Cons:
                    hasCons = true;
                    break;
            }
        }

        return hasEmptyList && hasCons;
    }

    /// <summary>
    /// Checks whether boolean patterns cover both true and false.
    /// </summary>
    private static bool IsBoolExhaustive(IReadOnlyList<MatchCase> cases)
    {
        bool hasTrue = false;
        bool hasFalse = false;

        foreach (var matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                continue;
            }

            if (matchCase.Pattern is Pattern.BoolLit b)
            {
                if (b.Value) hasTrue = true;
                else hasFalse = true;
            }
            else if (matchCase.Pattern is Pattern.Wildcard or Pattern.Var)
            {
                return true;
            }
        }

        return hasTrue && hasFalse;
    }

    private bool IsCatchAllPattern(Pattern p)
    {
        if (p is Pattern.Wildcard)
        {
            return true;
        }

        if (p is Pattern.Tuple tuple)
        {
            return tuple.Elements.All(IsCatchAllPattern);
        }

        if (p is Pattern.As asPattern)
        {
            return IsCatchAllPattern(asPattern.Inner);
        }

        if (p is Pattern.Or orPattern)
        {
            return orPattern.Alternatives.Any(IsCatchAllPattern);
        }

        return p is Pattern.Var v
            && (!TryResolveConstructorSymbol(v.Name, GetSpan(v), out var ctor) || ctor.Arity != 0);
    }

    private IReadOnlyList<string>? GetMissingAdtConstructors(TypeRef valueType, IReadOnlyList<MatchCase> cases)
    {
        if (valueType is not TypeRef.TNamedType namedType)
        {
            return null;
        }

        if (cases.Any(c => IsCatchAllPattern(c.Pattern) && c.Guard is null))
        {
            return [];
        }

        var seenConstructors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var matchCase in cases)
        {
            if (matchCase.Guard is not null)
            {
                continue;
            }

            if (TryGetConstructorSymbol(matchCase.Pattern, out var ctor) &&
                string.Equals(ctor.ParentType, namedType.Symbol.Name, StringComparison.Ordinal))
            {
                seenConstructors.Add(ctor.Name);
            }
        }

        return namedType.Symbol.Constructors
            .Select(c => c.Name)
            .Where(name => !seenConstructors.Contains(name))
            .ToList();
    }

    private IReadOnlyList<string>? GetMissingListCases(TypeRef valueType, IReadOnlyList<MatchCase> cases)
    {
        if (valueType is not TypeRef.TList)
        {
            return null;
        }

        if (cases.Any(c => IsCatchAllPattern(c.Pattern) && c.Guard is null))
        {
            return [];
        }

        bool hasEmptyList = false;
        bool hasCons = false;

        foreach (var matchCase in cases)
        {
            switch (matchCase.Pattern)
            {
                case Pattern.EmptyList:
                    hasEmptyList = true;
                    break;
                case Pattern.Cons:
                    hasCons = true;
                    break;
            }
        }

        List<string> missingCases = [];
        if (!hasEmptyList)
        {
            missingCases.Add("[]");
        }

        if (!hasCons)
        {
            missingCases.Add("x :: xs");
        }

        return missingCases;
    }

    private bool TryGetConstructorSymbol(Pattern p, out ConstructorSymbol ctor)
    {
        ctor = default!;
        if (p is Pattern.Constructor ctorPattern
            && TryResolveConstructorSymbol(
                ctorPattern.Name,
                GetSpan(ctorPattern),
                out var ctorPatternSymbol))
        {
            ctor = ctorPatternSymbol;
            return true;
        }

        if (p is Pattern.Var v
            && TryResolveConstructorSymbol(v.Name, GetSpan(v), out var varPatternSymbol)
            && varPatternSymbol.Arity == 0)
        {
            ctor = varPatternSymbol;
            return true;
        }

        return false;
    }

    private bool HasConstructorPattern(IEnumerable<MatchCase> cases)
    {
        foreach (var matchCase in cases)
        {
            if (matchCase.Pattern is Pattern.Constructor)
            {
                return true;
            }

            if (TryGetConstructorSymbol(matchCase.Pattern, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetMissingPattern(TypeRef valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        if (patterns.Any(ContainsUnknownConstructorPattern))
        {
            return false;
        }

        return TryGetMissingPatternCore(valueType, patterns, out missingPattern);
    }

    private bool TryGetMissingPatternCore(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        if (patterns.Any(IsCatchAllPattern))
        {
            return false;
        }

        valueType = valueType is null ? null : Prune(valueType);

        if (TryGetMissingListPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingTuplePattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingAdtPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        if (TryGetMissingBoolPattern(valueType, patterns, out missingPattern))
        {
            return true;
        }

        // Int and string literal patterns have infinite domains — if there are only
        // literal patterns and no catch-all, the match is non-exhaustive.
        if (TryGetMissingLiteralPattern(patterns, out missingPattern))
        {
            return true;
        }

        return false;
    }

    private bool TryGetMissingListPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        var isListDomain = valueType is TypeRef.TList || patterns.Any(p => p is Pattern.EmptyList or Pattern.Cons);
        if (!isListDomain)
        {
            return false;
        }

        var consPatterns = patterns.OfType<Pattern.Cons>().ToList();
        if (!patterns.Any(p => p is Pattern.EmptyList))
        {
            missingPattern = new Pattern.EmptyList();
            return true;
        }

        if (consPatterns.Count == 0)
        {
            missingPattern = new Pattern.Cons(new Pattern.Wildcard(), new Pattern.Wildcard());
            return true;
        }

        var listTypeContext = valueType as TypeRef.TList;
        if (TryGetMissingPatternCore(
            listTypeContext?.Element,
            consPatterns.Select(c => c.Head).ToList(),
            out var missingHead))
        {
            missingPattern = new Pattern.Cons(missingHead, new Pattern.Wildcard());
            return true;
        }

        if (TryGetMissingPatternCore(
            // The tail of a cons pattern is itself a list.
            listTypeContext,
            consPatterns.Select(c => c.Tail).ToList(),
            out var missingTail))
        {
            missingPattern = new Pattern.Cons(new Pattern.Wildcard(), missingTail);
            return true;
        }

        return false;
    }

    private bool TryGetMissingTuplePattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        int? tupleArity = valueType is TypeRef.TTuple tupleType
            ? tupleType.Elements.Count
            : patterns.OfType<Pattern.Tuple>().Select(t => (int?)t.Elements.Count).FirstOrDefault();
        if (tupleArity is null)
        {
            return false;
        }

        var tuplePatterns = patterns
            .OfType<Pattern.Tuple>()
            .Where(t => t.Elements.Count == tupleArity.Value)
            .ToList();
        if (tuplePatterns.Count == 0)
        {
            missingPattern = new Pattern.Tuple(Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), tupleArity.Value).ToList());
            return true;
        }

        // Conservative approximation: report the first tuple element dimension with a missing subpattern
        // and use wildcards for the remaining dimensions.
        for (int i = 0; i < tupleArity.Value; i++)
        {
            TypeRef? elementType = valueType is TypeRef.TTuple tupleValueType ? tupleValueType.Elements[i] : null;
            if (TryGetMissingPatternCore(elementType, tuplePatterns.Select(t => t.Elements[i]).ToList(), out var missingElement))
            {
                var elements = Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), tupleArity.Value).ToArray();
                elements[i] = missingElement;
                missingPattern = new Pattern.Tuple(elements);
                return true;
            }
        }

        return false;
    }

    private bool TryGetMissingAdtPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        var constructors = GetAdtConstructorsForPatterns(valueType, patterns);
        if (constructors is null)
        {
            return false;
        }

        foreach (var ctor in constructors)
        {
            var ctorPatterns = patterns.Where(p => IsPatternForConstructor(p, ctor)).ToList();
            if (ctorPatterns.Count == 0)
            {
                missingPattern = CreateMissingConstructorPattern(ctor, -1, null);
                return true;
            }

            if (ctor.Arity == 0)
            {
                continue;
            }

            var ctorWithArgs = ctorPatterns.OfType<Pattern.Constructor>().ToList();
            for (int i = 0; i < ctor.Arity; i++)
            {
                if (TryGetMissingPatternCore(
                    null,
                    ctorWithArgs.Select(c => c.Patterns[i]).ToList(),
                    out var missingField))
                {
                    missingPattern = CreateMissingConstructorPattern(ctor, i, missingField);
                    return true;
                }
            }
        }

        return false;
    }

    private IReadOnlyList<ConstructorSymbol>? GetAdtConstructorsForPatterns(TypeRef? valueType, IReadOnlyList<Pattern> patterns)
    {
        if (valueType is TypeRef.TNamedType namedType)
        {
            return namedType.Symbol.Constructors;
        }

        var constructorSymbols = patterns
            .Select(p => TryGetConstructorSymbol(p, out var ctor) ? ctor : null)
            .OfType<ConstructorSymbol>()
            .ToList();
        if (constructorSymbols.Count == 0)
        {
            return null;
        }

        var adtName = constructorSymbols[0].ParentType;
        if (constructorSymbols.Any(c => !string.Equals(c.ParentType, adtName, StringComparison.Ordinal)))
        {
            return null;
        }

        return _typeSymbols[adtName].Constructors;
    }

    private bool TryGetMissingBoolPattern(TypeRef? valueType, IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();

        // Only apply when value type is Bool or patterns contain boolean literals
        bool isBoolType = valueType is TypeRef.TBool;
        bool hasBoolPatterns = patterns.Any(p => p is Pattern.BoolLit);
        if (!isBoolType && !hasBoolPatterns)
        {
            return false;
        }

        bool hasTrue = false;
        bool hasFalse = false;

        foreach (var p in patterns)
        {
            if (IsCatchAllPattern(p)) return false;
            if (p is Pattern.BoolLit b)
            {
                if (b.Value) hasTrue = true;
                else hasFalse = true;
            }
        }

        if (!hasTrue)
        {
            missingPattern = new Pattern.BoolLit(true);
            return true;
        }

        if (!hasFalse)
        {
            missingPattern = new Pattern.BoolLit(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detects non-exhaustive matches over integer or string literal patterns.
    /// Since int and string domains are infinite, any set of literal patterns
    /// without a catch-all is non-exhaustive. Reports a wildcard as the missing case.
    /// </summary>
    private static bool TryGetMissingLiteralPattern(IReadOnlyList<Pattern> patterns, out Pattern missingPattern)
    {
        missingPattern = new Pattern.Wildcard();
        if (patterns.Any(p => p is Pattern.IntLit or Pattern.StrLit or Pattern.RuneLit))
        {
            // Already checked for catch-all in the caller — reaching here means
            // there are literal patterns without a catch-all, which is non-exhaustive.
            return true;
        }

        return false;
    }

    private bool IsPatternForConstructor(Pattern pattern, ConstructorSymbol ctor)
    {
        if (pattern is Pattern.Constructor ctorPattern)
        {
            return string.Equals(ctorPattern.Name, ctor.Name, StringComparison.Ordinal);
        }

        return pattern is Pattern.Var varPattern &&
               ctor.Arity == 0 &&
               string.Equals(varPattern.Name, ctor.Name, StringComparison.Ordinal);
    }

    private Pattern CreateMissingConstructorPattern(ConstructorSymbol ctor, int missingFieldIndex, Pattern? missingFieldPattern)
    {
        if (ctor.Arity == 0)
        {
            return new Pattern.Var(ctor.Name);
        }

        var args = Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), ctor.Arity).ToArray();
        if (missingFieldIndex >= 0 && missingFieldIndex < args.Length && missingFieldPattern is not null)
        {
            args[missingFieldIndex] = missingFieldPattern;
        }

        return new Pattern.Constructor(ctor.Name, args);
    }

    private bool ContainsUnknownConstructorPattern(Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.Constructor ctor:
                return !_constructorSymbols.ContainsKey(ctor.Name) || ctor.Patterns.Any(ContainsUnknownConstructorPattern);
            case Pattern.Cons cons:
                return ContainsUnknownConstructorPattern(cons.Head) || ContainsUnknownConstructorPattern(cons.Tail);
            case Pattern.Tuple tuple:
                return tuple.Elements.Any(ContainsUnknownConstructorPattern);
            default:
                return false;
        }
    }

    private static string FormatPattern(Pattern pattern)
    {
        return pattern switch
        {
            Pattern.EmptyList => "[]",
            Pattern.Wildcard => "_",
            Pattern.Var v => v.Name,
            Pattern.Cons cons => $"{FormatPattern(cons.Head)} :: {FormatPattern(cons.Tail)}",
            Pattern.Tuple tuple => $"({string.Join(", ", tuple.Elements.Select(FormatPattern))})",
            Pattern.Constructor ctor => ctor.Patterns.Count == 0
                ? ctor.Name
                : $"{ctor.Name}({string.Join(", ", ctor.Patterns.Select(FormatPattern))})",
            Pattern.IntLit intLit => intLit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pattern.StrLit strLit => $"\"{strLit.Value}\"",
            Pattern.RuneLit runeLit => $"U+{runeLit.Value:X}",
            Pattern.BoolLit boolLit => boolLit.Value ? "true" : "false",
            _ => "_"
        };
    }

    private string? TryGetConstructorAdtName(Pattern p)
    {
        if (TryGetConstructorSymbol(p, out var ctor))
        {
            return ctor.ParentType;
        }

        return null;
    }

    private IReadOnlyList<MatchCase> ExpandPatternAlternatives(IReadOnlyList<MatchCase> cases)
    {
        var expanded = new List<MatchCase>();
        foreach (MatchCase matchCase in cases)
        {
            foreach (Pattern pattern in ExpandPatternAlternatives(matchCase.Pattern))
            {
                expanded.Add(new MatchCase(pattern, matchCase.Body, matchCase.Guard));
            }
        }

        return expanded;
    }

    private IReadOnlyList<Pattern> ExpandPatternAlternatives(Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.Or orPattern:
                return orPattern.Alternatives.SelectMany(ExpandPatternAlternatives).ToList();
            case Pattern.As asPattern:
                return ExpandPatternAlternatives(asPattern.Inner);
            case Pattern.Cons cons:
                return CombinePatternChildren([cons.Head, cons.Tail])
                    .Select(children => CopyPatternSpan(pattern, new Pattern.Cons(children[0], children[1])))
                    .ToList();
            case Pattern.Tuple tuple:
                return CombinePatternChildren(tuple.Elements)
                    .Select(children => CopyPatternSpan(pattern, new Pattern.Tuple(children)))
                    .ToList();
            case Pattern.Constructor constructor:
                return CombinePatternChildren(constructor.Patterns)
                    .Select(children => CopyPatternSpan(pattern, new Pattern.Constructor(constructor.Name, children)))
                    .ToList();
            case Pattern.Record record:
                return ExpandRecordPatternAlternatives(record);
            default:
                return [pattern];
        }
    }

    private IReadOnlyList<Pattern> ExpandRecordPatternAlternatives(Pattern.Record record)
    {
        if (!TryResolveConstructorSymbol(record.TypeName, GetSpan(record), out ConstructorSymbol? constructor)
            || constructor.DeclaringSyntax.FieldNames.Count == 0)
        {
            return [record];
        }

        var fields = Enumerable.Repeat<Pattern>(new Pattern.Wildcard(), constructor.Arity).ToArray();
        foreach ((string fieldName, Pattern fieldPattern) in record.Fields)
        {
            int index = FindFieldIndex(constructor.DeclaringSyntax.FieldNames, fieldName);
            if (index >= 0)
            {
                fields[index] = fieldPattern;
            }
        }

        return CombinePatternChildren(fields)
            .Select(children => CopyPatternSpan(record, new Pattern.Constructor(record.TypeName, children)))
            .ToList();
    }

    private IReadOnlyList<IReadOnlyList<Pattern>> CombinePatternChildren(IReadOnlyList<Pattern> children)
    {
        var combinations = new List<IReadOnlyList<Pattern>> { Array.Empty<Pattern>() };
        foreach (Pattern child in children)
        {
            IReadOnlyList<Pattern> alternatives = ExpandPatternAlternatives(child);
            combinations = combinations
                .SelectMany(prefix => alternatives.Select(alternative =>
                    (IReadOnlyList<Pattern>)[.. prefix, alternative]))
                .ToList();
        }

        return combinations;
    }

    private static TPattern CopyPatternSpan<TPattern>(Pattern source, TPattern target)
        where TPattern : Pattern
    {
        AstSpans.Set(target, GetSpan(source));
        return target;
    }

    private static int FindFieldIndex(IReadOnlyList<string> fieldNames, string fieldName)
    {
        for (int i = 0; i < fieldNames.Count; i++)
        {
            if (string.Equals(fieldNames[i], fieldName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void ValidateSingleAdtMatch(IReadOnlyList<MatchCase> cases)
    {
        var adtNames = cases
            .Select(c => TryGetConstructorAdtName(c.Pattern))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (adtNames.Count > 1)
        {
            ReportDiagnostic(GetSpan(cases[0].Pattern), $"Constructor patterns from different ADTs ({string.Join(", ", adtNames.Select(n => $"'{n}'"))}) cannot appear in the same match expression.");
        }
    }

    private void ValidateReachableMatchArms(IReadOnlyList<MatchCase> cases)
    {
        var seenConstructors = new HashSet<string>(StringComparer.Ordinal);
        var seenIntLiterals = new HashSet<long>();
        var seenStrLiterals = new HashSet<string>(StringComparer.Ordinal);
        var seenBoolTrue = false;
        var seenBoolFalse = false;
        var hasCatchAll = false;
        var seenCompositePatterns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var matchCase in cases)
        {
            if (hasCatchAll)
            {
                ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: a catch-all pattern was already matched earlier.");
                continue;
            }

            if (IsCatchAllPattern(matchCase.Pattern) && matchCase.Guard is null)
            {
                hasCatchAll = true;
                continue;
            }

            if (matchCase.Guard is not null)
            {
                continue;
            }

            if (matchCase.Pattern is Pattern.Constructor or Pattern.Cons or Pattern.Tuple)
            {
                string patternKey = FormatPattern(matchCase.Pattern);
                if (!seenCompositePatterns.Add(patternKey))
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: pattern {patternKey} is already matched earlier.");
                    continue;
                }
            }

            if (ValidateLiteralArmReachability(matchCase, seenIntLiterals, seenStrLiterals, ref seenBoolTrue, ref seenBoolFalse))
            {
                continue;
            }

            if (!TryGetConstructorSymbol(matchCase.Pattern, out var ctor))
            {
                continue;
            }

            // Payload constructors may need multiple arms for nested refinements (e.g. Some([]), Some(_ :: _)).
            // We still track all constructors in seenConstructors so that truly duplicate payload arms
            // can be detected after inspecting their nested patterns.
            var isNewConstructor = seenConstructors.Add(ctor.Name);
            if (ctor.Arity == 0 && !isNewConstructor)
            {
                ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: constructor {ctor.Name} is already matched earlier.");
            }
        }
    }

    /// <summary>
    /// Checks one arm's literal pattern (int, string, or bool) for reachability against the
    /// literals matched by earlier arms and records it as seen. Returns true when the arm was
    /// a literal pattern (and has been fully handled), false otherwise.
    /// </summary>
    private bool ValidateLiteralArmReachability(MatchCase matchCase, HashSet<long> seenIntLiterals, HashSet<string> seenStrLiterals, ref bool seenBoolTrue, ref bool seenBoolFalse)
    {
        switch (matchCase.Pattern)
        {
            case Pattern.IntLit intLit:
                if (!seenIntLiterals.Add(intLit.Value))
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: integer literal {intLit.Value} is already matched earlier.");
                }
                return true;
            case Pattern.StrLit strLit:
                if (!seenStrLiterals.Add(strLit.Value))
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: string literal \"{strLit.Value}\" is already matched earlier.");
                }
                return true;
            case Pattern.RuneLit runeLit:
                if (!seenIntLiterals.Add(runeLit.Value))
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), $"Unreachable match arm: rune literal U+{runeLit.Value:X} is already matched earlier.");
                }
                return true;
            case Pattern.BoolLit boolLit:
                if (boolLit.Value && seenBoolTrue)
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: 'true' is already matched earlier.");
                }
                else if (!boolLit.Value && seenBoolFalse)
                {
                    ReportDiagnostic(GetSpan(matchCase.Pattern), "Unreachable match arm: 'false' is already matched earlier.");
                }
                if (boolLit.Value) seenBoolTrue = true;
                else seenBoolFalse = true;
                return true;
            default:
                return false;
        }
    }

    private string FormatConstructorShape(ConstructorSymbol ctor)
    {
        if (ctor.Arity == 0)
        {
            return ctor.Name;
        }

        return $"{ctor.Name}({string.Join(", ", ctor.ParameterTypes.Select(FormatConstructorParameterType))})";
    }

    private TypeRef InstantiateConstructorParameterType(ConstructorSymbol ctor, int parameterIndex, TypeRef.TNamedType resultType)
    {
        var typeSym = _typeSymbols[ctor.ParentType];
        var typeParameterMap = CreateTypeParameterMap(typeSym, resultType.TypeArgs);
        return SubstituteTypeParameters(ctor.ParameterTypes[parameterIndex], typeParameterMap);
    }

    private static TypeRef SubstituteTypeParameters(TypeRef type, IReadOnlyDictionary<string, TypeRef> typeParameterMap)
    {
        return type switch
        {
            TypeRef.TTypeParam tp when typeParameterMap.TryGetValue(tp.Symbol.Name, out var replacement) => replacement,
            TypeRef.TList list => new TypeRef.TList(SubstituteTypeParameters(list.Element, typeParameterMap)),
            TypeRef.TPtr pointer => new TypeRef.TPtr(SubstituteTypeParameters(pointer.Pointee, typeParameterMap)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(element => SubstituteTypeParameters(element, typeParameterMap)).ToList()),
            TypeRef.TFun funType => new TypeRef.TFun(
                SubstituteTypeParameters(funType.Arg, typeParameterMap),
                SubstituteTypeParameters(funType.Ret, typeParameterMap)),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(typeArg => SubstituteTypeParameters(typeArg, typeParameterMap)).ToList()),
            _ => type
        };
    }

    private static string FormatConstructorParameterType(TypeRef type)
    {
        return type switch
        {
            TypeRef.TInt => "Int",
            TypeRef.TUInt { Bits: 8 } => "u8",
            TypeRef.TUInt { Bits: 16 } => "u16",
            TypeRef.TUInt { Bits: 32 } => "u32",
            TypeRef.TUInt { Bits: 64 } => "u64",
            TypeRef.TUInt u => $"u{u.Bits}",
            TypeRef.TFloat => "Float",
            TypeRef.TBigInt => "BigInt",
            TypeRef.TStr => "Str",
            TypeRef.TBool => "Bool",
            TypeRef.TNever => "Never",
            TypeRef.TTypeParam tp => tp.Symbol.Name,
            TypeRef.TList list => $"List<{FormatConstructorParameterType(list.Element)}>",
            TypeRef.TTuple tuple => $"({string.Join(", ", tuple.Elements.Select(FormatConstructorParameterType))})",
            TypeRef.TFun funType => $"{FormatConstructorParameterType(funType.Arg)} -> {FormatConstructorParameterType(funType.Ret)}",
            TypeRef.TNamedType named when named.TypeArgs.Count == 0 => named.Symbol.Name,
            TypeRef.TNamedType named => $"{named.Symbol.Name}<{string.Join(", ", named.TypeArgs.Select(FormatConstructorParameterType))}>",
            _ => type.GetType().Name
        };
    }

    /// <summary>
    /// Returns a dummy (int 0) temp with type <see cref="TypeRef.TNever"/>.
    /// Used as a sentinel return value after emitting a diagnostic so that
    /// downstream code can detect and suppress cascading type errors.
    /// </summary>
    private (int Temp, TypeRef Type) ReturnNeverWithDummyTemp()
    {
        int t = NewTemp();
        Emit(new IrInst.LoadConstInt(t, 0));
        return (t, new TypeRef.TNever());
    }

    private string BuildUnknownConstructorHint(string name)
    {
        if (_constructorSymbols.Count == 0)
        {
            return "";
        }

        // Only suggest constructors within a reasonable edit-distance threshold
        // to avoid surfacing very dissimilar names as suggestions.
        int threshold = Math.Max(3, name.Length / 2);
        var candidates = _constructorSymbols.Keys
            .Select(k => (Name: k, Dist: EditDistance(name, k)))
            .Where(x => x.Dist <= threshold)
            .OrderBy(x => x.Dist)
            .Take(3)
            .Select(x => x.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            return "";
        }

        return $" Did you mean: {string.Join(", ", candidates)}?";
    }

    private static string BuildUnknownVariableHint(string name)
    {
        foreach (var moduleName in BuiltinRegistry.StandardModuleNames)
        {
            if (!BuiltinRegistry.TryGetModule(moduleName, out var module))
            {
                continue;
            }

            if (module.Members.ContainsKey(name))
            {
                return $" Did you mean '{moduleName}.{name}'?";
            }
        }

        return "";
    }

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// Used to rank constructor name suggestions for diagnostic hints.
    /// </summary>
    private static int EditDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= n; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
    }

    /// <summary>Records a just-published reuse token's variable-bound fields: field index →
    /// (local slot, total references in the arm body). See the CO-23 guard fields in Lowering.cs.</summary>
    private void RecordReuseTokenFieldBindings(int tokenTemp, Pattern pattern, Expr armBody)
    {
        if (pattern is not Pattern.Constructor ctorPattern)
        {
            return;
        }

        Dictionary<int, (int Slot, int TotalRefs)>? fields = null;
        for (int i = 0; i < ctorPattern.Patterns.Count; i++)
        {
            if (ctorPattern.Patterns[i] is Pattern.Var fieldVar
                && !_constructorSymbols.ContainsKey(fieldVar.Name)
                && _scopes.Peek().TryGetValue(fieldVar.Name, out var binding)
                && binding is Binding.Local local)
            {
                fields ??= new Dictionary<int, (int, int)>();
                fields[i] = (local.Slot, CountNameOccurrences(armBody, fieldVar.Name));
                // Reset (not TryAdd): at token issuance no arm reference has been lowered yet.
                // The same slot id recurs when the function is lowered again (e.g. once normally
                // and once as a reuse specialization); a stale count from the earlier lowering
                // would inflate SEEN and wrongly authorize the in-place overwrite.
                _reuseBindingSeenBySlot[local.Slot] = 0;
                _reuseTrackedSlotNames[local.Slot] = fieldVar.Name;
            }
        }

        if (fields is not null)
        {
            _reuseTokenFieldBindings[tokenTemp] = fields;
        }
    }
}
