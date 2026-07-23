using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private (int, TypeRef) LowerMatch(Expr.Match match)
    {
        // The matched value is NOT in tail position
        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        var (valueTemp, valueType) = ShouldStackAllocateImmediateMatchScrutinee(match)
            && TryLowerConstructorExpression(match.Value, stackAllocate: true, out var loweredMatchValue)
                ? loweredMatchValue
                : LowerExpr(match.Value);


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

        ValidateSingleAdtMatch(match.Cases);
        ValidateReachableMatchArms(match.Cases);
        var hasAnyTuplePattern = match.Cases.Any(c => c.Pattern is Pattern.Tuple);

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
            normalizeStaticStringArms);

        Emit(new IrInst.Label(noMatchLabel));
        EmitMatchExhaustivenessDiagnostics(match, valueType, hasAnyTuplePattern);

        int defaultTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(defaultTemp, 0));
        Emit(new IrInst.StoreLocal(resultSlot, defaultTemp));
        Emit(new IrInst.Label(endLabel));

        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        MarkRuntimeManagedMatchResult(resultTemp, runtimeManagedResultArms, match.Cases);
        return (resultTemp, Prune(resultType));
    }

    private void MarkRuntimeManagedMatchResult(
        int resultTemp,
        IReadOnlyList<bool>? runtimeManagedResultArms,
        IReadOnlyList<MatchCase> cases)
    {
        if (runtimeManagedResultArms is not null
            && runtimeManagedResultArms.Count == cases.Count
            && runtimeManagedResultArms.Select((runtimeManaged, index) =>
                runtimeManaged || MatchArmReturnsRuntimeManagedTcoParam(cases[index].Body)).All(value => value))
        {
            _runtimeManagedResultTemps.Add(resultTemp);
        }
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
            && _tcoCtx?.RuntimeManagedParamSlots.Contains(local.Slot) == true;
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
        bool normalizeStaticStringArms)
    {
        List<bool>? runtimeManagedResultArms = [];
        _runtimeManagedMatchResultArms.Push(runtimeManagedResultArms);
        if (TryPlanTagSwitch(match.Cases, out var switchPlan))
        {
            LowerMatchArmsViaTagSwitch(match.Value, match.Cases, switchPlan, valueTemp, valueType, resultType, resultSlot, endLabel, noMatchLabel, savedTailPos, reuseScrutineeName, runtimeReuseType, normalizeStaticStringArms);
        }
        else
        {
            LowerMatchArmsLinear(match, valueTemp, valueType, resultType, resultSlot, endLabel, noMatchLabel, savedTailPos, reuseScrutineeName, runtimeReuseType, normalizeStaticStringArms);
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
        if (match.Value is Expr.Var variable && _linearReuseNames.Contains(variable.Name))
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
    private void LowerMatchArmsLinear(Expr.Match match, int valueTemp, TypeRef valueType, TypeRef resultType, int resultSlot, string endLabel, string noMatchLabel, bool savedTailPos, string? reuseScrutineeName = null, TypeRef.TNamedType? runtimeReuseType = null, bool normalizeStaticStringArms = false)
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

            int reuseTokensBefore = PublishLinearArmReuseToken(
                match,
                i,
                valueTemp,
                reuseScrutineeName,
                runtimeReuseType);

            LowerMatchArmBodyIntoResult(match.Cases, i, resultType, resultSlot, endLabel, savedTailPos, reuseTokensBefore, normalizeStaticStringArms);

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
            EmitPattern(match.Cases[i].Pattern, valueTemp, armCleanupLabel, patternBindings);
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

        TrackRuntimeManagedMatchScrutinee(match.Value, valueTemp, valueType, patternBindings);
    }

    /// <summary>
    /// Publishes one linear arm's dead accumulator node as a reuse token when eligible.
    /// Returns the reuse-token count before publishing so the caller can drop any token
    /// the arm body didn't consume.
    /// </summary>
    private int PublishLinearArmReuseToken(
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
            int tokenTemp = NewTemp();
            Emit(new IrInst.DropReuse(
                tokenTemp,
                valueTemp,
                reuseArityVal,
                runtimeCleanup is not null));
            _reuseTokens.Add(new ReuseToken(
                tokenTemp,
                reuseArityVal,
                runtimeCleanup,
                ListCell: match.Cases[i].Pattern is Pattern.Cons));
            RecordReuseTokenFieldBindings(tokenTemp, match.Cases[i].Pattern, match.Cases[i].Body);
        }

        return reuseTokensBefore;
    }

    /// <summary>
    /// Lowers one arm's body, unifies its type with the match result type, and stores the value
    /// into the result slot before jumping to the match end label. Shared by the linear and
    /// tag-switch arm lowerings.
    /// </summary>
    private void LowerMatchArmBodyIntoResult(IReadOnlyList<MatchCase> cases, int i, TypeRef resultType, int resultSlot, string endLabel, bool savedTailPos, int reuseTokensBefore, bool normalizeStaticStringArms)
    {
        // Each case body IS in tail position (if the match itself is)
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        var armCredits = BeginExclusiveBranch(cases.Where((_, j) => j != i).Select(c => c.Body));
        var (bodyTemp, bodyType) = LowerMatchArmExpression(cases[i].Body, reuseTokensBefore, normalizeStaticStringArms);
        EndExclusiveBranch(armCredits);
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        ReleaseUnconsumedReuseTokens(reuseTokensBefore);

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
            bool runtimeManaged = _runtimeManagedResultTemps.Contains(armFinalTemp)
                || _inst.Any(instruction =>
                    (instruction is IrInst.AllocAdt { Target: var adtTarget, RuntimeManaged: true }
                        && adtTarget == armFinalTemp)
                    || (instruction is IrInst.AllocReusing { Target: var reusedTarget, RuntimeManaged: true }
                        && reusedTarget == armFinalTemp));
            runtimeManagedArms.Add(runtimeManaged);
        }
        Emit(new IrInst.Jump(endLabel));
    }

    private (int Temp, TypeRef Type) LowerMatchArmExpression(Expr body, int reuseTokensBefore, bool normalizeStaticStringArm)
    {
        TypeRef.TNamedType? runtimeReuseType = _reuseTokens
            .Skip(reuseTokensBefore)
            .Select(token => token.RuntimeCleanup?.Type)
            .FirstOrDefault(type => type is not null);
        TypeRef.TNamedType? savedReuseType = _runtimeRcReuseAllocationTypeRequested;
        _runtimeRcReuseAllocationTypeRequested = runtimeReuseType ?? savedReuseType;
        try
        {
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
                _runtimeManagedResultTemps.Add(resultTemp);
                return (resultTemp, sourceType);
            }

            return LowerExpr(body);
        }
        finally
        {
            _runtimeRcReuseAllocationTypeRequested = savedReuseType;
        }
    }

    private void ReleaseUnconsumedReuseTokens(int reuseTokensBefore)
    {
        for (int i = reuseTokensBefore; i < _reuseTokens.Count; i++)
        {
            ReuseToken token = _reuseTokens[i];
            if (token.RuntimeCleanup is not { } cleanup)
            {
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
    private void EmitMatchExhaustivenessDiagnostics(Expr.Match match, TypeRef valueType, bool hasAnyTuplePattern)
    {
        var prunedValueType = Prune(valueType);
        var missingAdtConstructors = GetMissingAdtConstructors(prunedValueType, match.Cases);
        var missingListCases = GetMissingListCases(prunedValueType, match.Cases);
        var hasConstructorPatterns = HasConstructorPattern(match.Cases);
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
        else if (!hasTuplePatternArm && !hasConstructorPatterns && !IsDefinitelyExhaustive(match.Cases) && !IsBoolExhaustive(match.Cases))
        {
            _diag.Error(matchPos, "Non-exhaustive match expression.");
            reportedNonExhaustive = true;
        }

        if (!reportedNonExhaustive &&
            TryGetMissingPattern(prunedValueType, match.Cases.Where(c => c.Guard is null).Select(c => c.Pattern).ToList(), out var missingPattern))
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
        bool normalizeStaticStringArms = false)
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

            int reuseTokensBefore = PublishTagSwitchArmReuseToken(
                cases,
                plan,
                i,
                valueTemp,
                reuseScrutineeName,
                runtimeReuseType);

            LowerMatchArmBodyIntoResult(cases, i, resultType, resultSlot, endLabel, savedTailPos, reuseTokensBefore, normalizeStaticStringArms);

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
        TrackRuntimeManagedMatchScrutinee(matchValue, valueTemp, valueType, patternBindings);
    }

    private void TrackRuntimeManagedMatchScrutinee(
        Expr matchValue,
        int valueTemp,
        TypeRef valueType,
        IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        // Task/coroutine bodies still use scheduler-owned arenas. Until cross-thread RC publication
        // exists, their match payloads must stay on that path instead of entering local RC transfer.
        if (_usesAsync || _inCoroutineBody || CapabilityGlobalCount > 0)
        {
            return;
        }

        TrackRuntimeManagedTcoListPatternAliases(matchValue, valueType, patternBindings);

        string? ownerName = null;
        if (matchValue is Expr.Var variable
            && LookupOwnedValue(variable.Name) is { RuntimeManaged: true })
        {
            ownerName = ResolveOwnershipAlias(variable.Name);
        }
        else if (matchValue is not Expr.Var && IsRuntimeManagedResultTemp(valueTemp))
        {
            TypeRef ownedType = Prune(valueType);
            string? typeName = GetOwnedTypeName(ownedType);
            if (typeName is not null)
            {
                ownerName = $"$match_rc_{valueTemp}";
                int ownerSlot = NewLocal();
                Emit(new IrInst.StoreLocal(ownerSlot, valueTemp));
                TrackOwnedValue(
                    ownerName,
                    ownerSlot,
                    typeName,
                    isResource: false,
                    definitionSpan: null,
                    ownedType,
                    runtimeManaged: true);
            }
        }

        if (ownerName is null)
        {
            return;
        }

        foreach ((string bindingName, TypeRef bindingType) in patternBindings)
        {
            if (!CanArenaReset(Prune(bindingType)))
            {
                _ownershipAliases[bindingName] = ownerName;
            }
        }
    }

    private void TrackRuntimeManagedTcoListPatternAliases(
        Expr matchValue,
        TypeRef valueType,
        IReadOnlyDictionary<string, TypeRef> patternBindings)
    {
        if (matchValue is not Expr.Var variable
            || Prune(valueType) is not TypeRef.TList
            || Lookup(variable.Name) is not Binding.Local parent
            || _tcoCtx?.RuntimeManagedParamSlots.Contains(parent.Slot) != true
            || !_tcoCtx.RuntimeManagedParamActiveSlots.TryGetValue(parent.Slot, out int activeSlot))
        {
            return;
        }

        foreach ((string bindingName, TypeRef bindingType) in patternBindings)
        {
            TypeRef payloadType = Prune(bindingType);
            if (CanArenaReset(payloadType)
                || Lookup(bindingName) is not Binding.Local payload)
            {
                continue;
            }

            _runtimeManagedTcoPatternAliases[bindingName] = new RuntimeManagedTcoPatternAlias(
                variable.Name,
                parent.Slot,
                activeSlot,
                Prune(valueType),
                payload.Slot,
                payloadType);
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
            && LookupOwnedValue(variable.Name) is { RuntimeManaged: true, IsDropped: false } owner)
        {
            return EmitRuntimeManagedParentFieldTransfer(owner, bodyTemp);
        }

        return bodyTemp;
    }

    /// <summary>
    /// Publishes one tag-switch arm's dead accumulator node as a reuse token when eligible.
    /// Returns the reuse-token count before publishing so the caller can drop any token the
    /// arm body didn't consume.
    /// </summary>
    private int PublishTagSwitchArmReuseToken(
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
            _reuseTokens.Add(new ReuseToken(tokenTemp, plan[i].Ctor.Arity, runtimeCleanup));
            RecordReuseTokenFieldBindings(tokenTemp, cases[i].Pattern, cases[i].Body);
        }

        return reuseTokensBefore;
    }

    private RuntimeReuseCleanup CreateRuntimeReuseCleanup(
        TypeRef.TNamedType runtimeType,
        ConstructorSymbol constructor,
        Pattern pattern)
    {
        var transferableFields = new Dictionary<string, int>(StringComparer.Ordinal);
        if (pattern is Pattern.Constructor constructorPattern)
        {
            for (int i = 0; i < Math.Min(constructorPattern.Patterns.Count, constructor.Arity); i++)
            {
                TypeRef fieldType = Prune(InstantiateConstructorParameterType(
                    constructor,
                    i,
                    runtimeType));
                if (!CanArenaReset(fieldType)
                    && fieldType is TypeRef.TNamedType
                    && constructorPattern.Patterns[i] is Pattern.Var binding
                    && !_constructorSymbols.ContainsKey(binding.Name))
                {
                    transferableFields[binding.Name] = i;
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

    private (int Temp, TypeRef Type) LowerConsCell(int headTemp, int tailTemp, TypeRef headType, TypeRef tailType)
    {
        var listType = new TypeRef.TList(headType);
        Unify(tailType, listType);

        int nodeTemp = NewTemp();
        bool runtimeManaged = _runtimeRcListAllocationRequested
            && IsRuntimeManageableListElement(headType, headTemp);
        if (TryConsumeReuseToken(
                2,
                runtimeManaged,
                out int reuseTokenTemp,
                out RuntimeReuseCleanup? runtimeCleanup,
                listCell: true))
        {
            Debug.Assert(runtimeCleanup is null, "Runtime-managed list reuse requires list-specific child cleanup.");
            Emit(new IrInst.AllocReusing(
                nodeTemp,
                0,
                2,
                reuseTokenTemp,
                RuntimeManaged: false,
                ListCell: true));
            _reuseResultTemps.Add(nodeTemp);
        }
        else
        {
            Emit(new IrInst.Alloc(nodeTemp, HeapLayouts.List.FixedAllocationSizeBytes, runtimeManaged));
        }
        Emit(new IrInst.StoreMemOffset(nodeTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex), headTemp));
        Emit(new IrInst.StoreMemOffset(nodeTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex), tailTemp));
        if (runtimeManaged && _runtimeRcTcoListTailBinding is not null)
        {
            _runtimeManagedResultTemps.Add(nodeTemp);
        }
        return (nodeTemp, Prune(listType));
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
        switch (pattern)
        {
            case Pattern.EmptyList:
                return new TypeRef.TList(NewTypeVar());

            case Pattern.Wildcard:
                return NewTypeVar();

            case Pattern.Var v:
                // Check if this identifier is a known nullary constructor
                if (_constructorSymbols.TryGetValue(v.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
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
                return InferConstructorPatternType(ctor.Name, ctor.Patterns, bindings);

            case Pattern.IntLit:
                return new TypeRef.TInt();

            case Pattern.StrLit:
                return new TypeRef.TStr();

            case Pattern.BoolLit:
                return new TypeRef.TBool();

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    private TypeRef InferConstructorPatternType(string name, IReadOnlyList<Pattern> patterns, Dictionary<string, TypeRef> bindings)
    {
        if (!_constructorSymbols.TryGetValue(name, out var ctor))
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
    private void EmitPattern(Pattern pattern, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        switch (pattern)
        {
            case Pattern.EmptyList:
                EmitRequireZero(valueTemp, failLabel);
                return;

            case Pattern.Wildcard:
                return;

            case Pattern.Var v:
                EmitVarPattern(v, valueTemp, failLabel, bindingTypes);
                return;

            case Pattern.Cons c:
                EmitRequireNonZero(valueTemp, failLabel);
                int headTemp = NewTemp();
                int tailTemp = NewTemp();
                Emit(new IrInst.LoadMemOffset(headTemp, valueTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)));
                Emit(new IrInst.LoadMemOffset(tailTemp, valueTemp, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex)));
                EmitPattern(c.Head, headTemp, failLabel, bindingTypes);
                EmitPattern(c.Tail, tailTemp, failLabel, bindingTypes);
                return;

            case Pattern.Tuple tuple:
                for (int i = 0; i < tuple.Elements.Count; i++)
                {
                    int elemTemp = NewTemp();
                    Emit(new IrInst.LoadMemOffset(elemTemp, valueTemp, i * 8));
                    EmitPattern(tuple.Elements[i], elemTemp, failLabel, bindingTypes);
                }
                return;

            case Pattern.Constructor ctor:
                EmitConstructorPattern(ctor, valueTemp, failLabel, bindingTypes);
                return;

            case Pattern.IntLit intLit:
                EmitRequireIntEqual(valueTemp, intLit.Value, failLabel);
                return;

            case Pattern.StrLit strLit:
                EmitRequireStrEqual(valueTemp, strLit.Value, failLabel);
                return;

            case Pattern.BoolLit boolLit:
                EmitRequireBoolEqual(valueTemp, boolLit.Value, failLabel);
                return;

            default:
                throw new NotSupportedException(pattern.GetType().Name);
        }
    }

    /// <summary>
    /// Emits a variable pattern: a variable naming a known nullary constructor is a tag test,
    /// any other variable binds the matched value into a fresh local.
    /// </summary>
    private void EmitVarPattern(Pattern.Var v, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        // If this is a known nullary constructor, emit a tag check instead of binding
        if (_constructorSymbols.TryGetValue(v.Name, out var nullaryCtor) && nullaryCtor.Arity == 0)
        {
            EmitRequireNonZero(valueTemp, failLabel);
            EmitRequireTagMatch(valueTemp, GetConstructorTag(nullaryCtor), failLabel);
            return;
        }
        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, valueTemp));
        RecordLocalDebugInfo(slot, v.Name, bindingTypes[v.Name]);
        _scopes.Peek()[v.Name] = new Binding.Local(slot, Prune(bindingTypes[v.Name]));
    }

    private void EmitConstructorPattern(Pattern.Constructor ctor, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        if (!_constructorSymbols.TryGetValue(ctor.Name, out var ctorSym))
        {
            // Unknown constructor — already diagnosed in InferPatternType
            return;
        }

        // All constructors are tagged heap allocations: [ctorTag, ...payloads].
        // Check ptr != null, then check the tag matches this constructor.
        EmitRequireNonZero(valueTemp, failLabel);
        EmitRequireTagMatch(valueTemp, GetConstructorTag(ctorSym), failLabel);

        EmitConstructorFieldBindings(ctorSym, ctor, valueTemp, failLabel, bindingTypes);
    }

    /// <summary>
    /// Extracts each constructor payload field and binds its sub-pattern, without emitting the
    /// null/tag check. Shared by the linear pattern path (after its own tag check) and the
    /// tag-switch path (where the switch has already dispatched on the tag).
    /// </summary>
    private void EmitConstructorFieldBindings(ConstructorSymbol ctorSym, Pattern.Constructor ctor, int valueTemp, string failLabel, IReadOnlyDictionary<string, TypeRef> bindingTypes)
    {
        for (int i = 0; i < ctorSym.Arity && i < ctor.Patterns.Count; i++)
        {
            // Extract payload at each field index and bind sub-patterns.
            int payloadTemp = NewTemp();
            Emit(new IrInst.GetAdtField(payloadTemp, valueTemp, i));
            EmitPattern(ctor.Patterns[i], payloadTemp, failLabel, bindingTypes);
        }
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
        switch (p)
        {
            case Pattern.Var v:
                if (!string.Equals(v.Name, "_", StringComparison.Ordinal))
                {
                    yield return v.Name;
                }

                yield break;
            case Pattern.Cons c:
                foreach (var n in PatternBindings(c.Head))
                {
                    yield return n;
                }

                foreach (var n in PatternBindings(c.Tail))
                {
                    yield return n;
                }

                yield break;
            case Pattern.Tuple tuple:
                foreach (var sub in tuple.Elements)
                {
                    foreach (var n in PatternBindings(sub))
                    {
                        yield return n;
                    }
                }

                yield break;
            case Pattern.Constructor ctor:
                foreach (var sub in ctor.Patterns)
                {
                    foreach (var n in PatternBindings(sub))
                    {
                        yield return n;
                    }
                }

                yield break;
            default:
                yield break;
        }
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

        IEnumerable<string> shown = missing.Count <= DisplayLimit
            ? missing
            : missing.Take(TruncateShowCount);

        var listed = string.Join(", ", shown.Select(name => $"'{name}'"));

        if (missing.Count > DisplayLimit)
        {
            int remainder = missing.Count - TruncateShowCount;
            listed += $", ... and {remainder} more";
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

        return p is Pattern.Var v && (!_constructorSymbols.TryGetValue(v.Name, out var ctor) || ctor.Arity != 0);
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
        if (p is Pattern.Constructor ctorPattern && _constructorSymbols.TryGetValue(ctorPattern.Name, out var ctorPatternSymbol))
        {
            ctor = ctorPatternSymbol;
            return true;
        }

        if (p is Pattern.Var v && _constructorSymbols.TryGetValue(v.Name, out var varPatternSymbol) && varPatternSymbol.Arity == 0)
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
        if (patterns.Any(p => p is Pattern.IntLit or Pattern.StrLit))
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
