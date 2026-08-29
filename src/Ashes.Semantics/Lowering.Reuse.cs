using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{

    private static Expr.Lambda? FindInnermostLambdaUnderLets(Expr value)
    {
        if (value is Expr.Lambda lam) return lam;
        if (value is Expr.Let let) return FindInnermostLambdaUnderLets(let.Body);
        // `let recursive inner = V in inner`: a self-returning recursive binding is just V under
        // another name (the shape a trait method's self-tie wrapper produces around a stdlib
        // helper like List's `equal = let recursive equalLists = ... in equalLists`) — dig into
        // its own value rather than its body, since the body here is only ever its own name.
        if (value is Expr.LetRecursive letRec
            && letRec.Body is Expr.Var selfReference
            && string.Equals(selfReference.Name, letRec.Name, StringComparison.Ordinal))
        {
            return FindInnermostLambdaUnderLets(letRec.Value);
        }
        return null;
    }

    private static int CountLambdaChain(Expr.Lambda lam)
    {
        int count = 1;
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            count++;
            body = inner.Body;
        }
        return count;
    }

    private static List<string> CollectLambdaParams(Expr.Lambda lam)
    {
        var names = new List<string> { lam.ParamName };
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            names.Add(inner.ParamName);
            body = inner.Body;
        }
        return names;
    }

    private static Expr GetInnermostBody(Expr.Lambda lam)
    {
        var body = lam.Body;
        while (body is Expr.Lambda inner)
        {
            body = inner.Body;
        }
        return body;
    }

    /// <summary>
    /// Check if an expression has any tail-position calls to the named function with the expected
    /// argument count. Lexical binders named <paramref name="selfName"/> shadow the recursive
    /// function and therefore cannot make the enclosing function TCO-eligible.
    /// </summary>
    private static bool HasTailSelfCalls(Expr body, string selfName, int paramCount)
    {
        return body switch
        {
            Expr.If iff => HasTailSelfCalls(iff.Then, selfName, paramCount) || HasTailSelfCalls(iff.Else, selfName, paramCount),
            Expr.Match m => m.Cases.Any(c =>
            {
                HashSet<string> binders = new(StringComparer.Ordinal);
                CollectPatternBinders(c.Pattern, binders);
                return !binders.Contains(selfName)
                    && HasTailSelfCalls(c.Body, selfName, paramCount);
            }),
            Expr.Let l => !string.Equals(l.Name, selfName, StringComparison.Ordinal)
                && HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetResult l => !string.Equals(l.Name, selfName, StringComparison.Ordinal)
                && HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.LetRecursive l => !string.Equals(l.Name, selfName, StringComparison.Ordinal)
                && HasTailSelfCalls(l.Body, selfName, paramCount),
            Expr.Call c => IsSelfCallChain(c, selfName, paramCount),
            _ => false
        };
    }

    /// <summary>Check if a call expression is a full self-call chain: f(a1)(a2)...(aN)</summary>
    private static bool IsSelfCallChain(Expr.Call call, string selfName, int expectedArgs)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(call, args);
        return root is Expr.Var v && string.Equals(v.Name, selfName, StringComparison.Ordinal) && args.Count == expectedArgs;
    }

    // Walk a left-recursive call chain and collect args in application order.
    // Handles multi-argument constructor calls desugared by the parser:
    //   Pair(1, 2) → Call(Call(Var("Pair"), 1), 2) → root=Var("Pair"), args=[1, 2]
    private static Expr CollectCallArgs(Expr expr, List<Expr> args)
    {
        if (expr is Expr.Call c)
        {
            var root = CollectCallArgs(c.Func, args);
            args.Add(c.Arg);
            return root;
        }

        return expr;
    }

    // Whether <paramref name="expr"/> evaluates to a loop accumulator threaded in place at a stable
    // address — one below the loop watermark that a plain arena reset (reclaiming everything above the
    // watermark) keeps live. <paramref name="isAcc"/> decides whether a bare Var is that accumulator:
    // at a back edge it is a live-scope slot check; inside a fold's stability recording it is name
    // equality against the (unshadowed) accumulator param. <paramref name="selfSpan"/>/<paramref
    // name="selfParamCount"/>, when set during recording, let a self back-edge count as a stable fold
    // before it is itself recorded. Conservative: an unrecognized shape (including a let-bound name) is
    // not stable, which only loses the reset (sound), never keeps an unsafe one.
    private bool IsStableAccumulatorExpr(Expr expr, Func<string, bool> isAcc, TextSpan? selfSpan = null, int selfParamCount = 0)
    {
        switch (expr)
        {
            case Expr.Var v:
                if (isAcc(v.Name))
                {
                    return true;
                }

                // Trace a let-bound accumulator (`let m2 = match … in loop(m2)`) back through its
                // binding: m2 is address-stable when the binding value is a match/if (or nested lets)
                // whose every leaf is itself a stable accumulator expr (the accumulator threaded
                // unchanged, or an in-place-reuse call on it). Lookup resolves to the live innermost
                // slot, so a shadowing rebind naturally picks the right binding. In the fold-recording
                // path the name's scope has been popped, so Lookup fails and this stays conservative.
                if (Lookup(v.Name) is { } vb
                    && TryGetBindingSlot(vb, out var vslot)
                    && _letBindingValues.TryGetValue(vslot, out var boundValue))
                {
                    return AccumulatorBindingLeavesStable(boundValue, isAcc, selfSpan, selfParamCount,
                        new HashSet<string>(StringComparer.Ordinal), 0);
                }

                return false;
            case Expr.Call:
                {
                    var args = new List<Expr>();
                    var root = CollectCallArgs(expr, args);
                    if (args.Count == 0)
                    {
                        return false;
                    }

                    // A reuse specialization rewrites its last argument's tree in place and returns it —
                    // stable exactly when that argument is.
                    if (_inPlaceReuseCallExprs.Contains(expr))
                    {
                        return IsStableAccumulatorExpr(args[^1], isAcc, selfSpan, selfParamCount);
                    }

                    if (root is not Expr.Var f || Lookup(f.Name)?.DefinitionSpan is not { } span)
                    {
                        return false;
                    }

                    // A call to a fold proven address-stable (or a self back-edge during recording) threads
                    // its accumulator (last arg) through at a stable address.
                    bool isSelf = selfSpan is { } s && span.Equals(s) && args.Count == selfParamCount;
                    bool isStableFold = _accStableFolds.TryGetValue(span, out var pc) && pc == args.Count;
                    if (isSelf || isStableFold)
                    {
                        return IsStableAccumulatorExpr(args[^1], isAcc, selfSpan, selfParamCount);
                    }

                    return false;
                }
            default:
                return false;
        }
    }

    private static bool TryGetBindingSlot(Binding binding, out int slot)
    {
        switch (binding)
        {
            case Binding.Local local:
                slot = local.Slot;
                return true;
            case Binding.Scheme scheme:
                slot = scheme.Slot;
                return true;
            default:
                slot = -1;
                return false;
        }
    }

    // Whether every leaf of a let-binding VALUE preserves the accumulator's address, so a var bound to
    // it is itself a stable accumulator. Walks If arms, Match case bodies, and nested Let bodies, and
    // checks each leaf via IsStableAccumulatorExpr. Tracks binders introduced INSIDE the value (match
    // pattern variables, nested let names) and removes them from the accumulator predicate at leaves,
    // so a leaf reference to a name that merely coincides with the accumulator's is never mistaken for
    // it (soundness). Depth-bounded against pathological chains.
    private bool AccumulatorBindingLeavesStable(Expr value, Func<string, bool> isAcc, TextSpan? selfSpan,
        int selfParamCount, HashSet<string> shadowed, int depth)
    {
        if (depth > 24)
        {
            return false;
        }

        switch (value)
        {
            case Expr.If iff:
                return AccumulatorBindingLeavesStable(iff.Then, isAcc, selfSpan, selfParamCount, shadowed, depth + 1)
                    && AccumulatorBindingLeavesStable(iff.Else, isAcc, selfSpan, selfParamCount, shadowed, depth + 1);
            case Expr.Match m:
                foreach (var c in m.Cases)
                {
                    var caseShadow = shadowed;
                    var binders = new HashSet<string>(StringComparer.Ordinal);
                    CollectPatternBinders(c.Pattern, binders);
                    if (binders.Count > 0)
                    {
                        caseShadow = new HashSet<string>(shadowed, StringComparer.Ordinal);
                        caseShadow.UnionWith(binders);
                    }

                    if (!AccumulatorBindingLeavesStable(c.Body, isAcc, selfSpan, selfParamCount, caseShadow, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.Let let:
                var bodyShadow = shadowed.Contains(let.Name)
                    ? shadowed
                    : new HashSet<string>(shadowed, StringComparer.Ordinal) { let.Name };
                return AccumulatorBindingLeavesStable(let.Body, isAcc, selfSpan, selfParamCount, bodyShadow, depth + 1);
            default:
                return IsStableAccumulatorExpr(
                    value,
                    name => !shadowed.Contains(name) && isAcc(name),
                    selfSpan,
                    selfParamCount);
        }
    }

    // Whether every tail leaf of a fold body preserves the accumulator's address, so calling the fold
    // returns it at a stable address. Walks the tail spine (If arms, Match case bodies, Let bodies),
    // tracking binders that shadow the accumulator name; each leaf must satisfy IsStableAccumulatorExpr
    // under the shadow set (a rebound accumulator name is no longer the accumulator). Used only when
    // recording a fold's stability, keyed by name — the caller side uses live-scope slots instead.

    private bool TailLeavesStable(Expr body, string accName, TextSpan selfSpan, int selfParamCount, HashSet<string> shadowed)
    {
        switch (body)
        {
            case Expr.If iff:
                return TailLeavesStable(iff.Then, accName, selfSpan, selfParamCount, shadowed)
                    && TailLeavesStable(iff.Else, accName, selfSpan, selfParamCount, shadowed);
            case Expr.Match m:
                foreach (var c in m.Cases)
                {
                    var caseShadow = shadowed;
                    var binders = new HashSet<string>(StringComparer.Ordinal);
                    CollectPatternBinders(c.Pattern, binders);
                    if (binders.Count > 0)
                    {
                        caseShadow = new HashSet<string>(shadowed, StringComparer.Ordinal);
                        caseShadow.UnionWith(binders);
                    }

                    if (!TailLeavesStable(c.Body, accName, selfSpan, selfParamCount, caseShadow))
                    {
                        return false;
                    }
                }

                return true;
            case Expr.Let let:
                {
                    var bodyShadow = shadowed;
                    if (!shadowed.Contains(let.Name))
                    {
                        bodyShadow = new HashSet<string>(shadowed, StringComparer.Ordinal) { let.Name };
                    }

                    return TailLeavesStable(let.Body, accName, selfSpan, selfParamCount, bodyShadow);
                }
            default:
                return IsStableAccumulatorExpr(
                    body,
                    name => !shadowed.Contains(name) && string.Equals(name, accName, StringComparison.Ordinal),
                    selfSpan,
                    selfParamCount);
        }
    }

    /// <summary>
    /// Finds accumulator parameters that are passed as the sole argument to a specializable recursive
    /// function — <c>f(acc)</c> where <c>f</c> is in <see cref="_specializableFunctions"/> and
    /// <c>acc</c> is a loop accumulator. These accumulators are deep-copied once at loop entry so the
    /// call can be routed to <c>f$reuse</c>. Walks calls + the if/let/match spine.
    /// </summary>
    /// <summary>
    /// Resolves a call root to the binding name used by the reuse registries: a plain <c>Var</c> yields
    /// its name; a qualified stdlib/user-module reference (<c>Ashes.Collection.Map.set</c>) yields its stitched
    /// top-level name (<c>Ashes_Map_set</c>). Returns null for intrinsics and non-name roots.
    /// </summary>
    private string? ResolveSpecializableCalleeName(Expr root)
    {
        switch (root)
        {
            case Expr.Var v:
                return v.Name;
            case Expr.QualifiedVar qv:
                var resolvedModule = ResolveModuleAlias(qv.Module);
                if (BuiltinRegistry.TryGetModule(resolvedModule, out var module) && module.Members.ContainsKey(qv.Name))
                {
                    return null; // intrinsic member — lowered directly, not a stitched function
                }

                return ProjectSupport.SanitizeModuleBindingName(resolvedModule) + "_" + qv.Name;
            default:
                return null;
        }
    }

    private void CollectSpecializableCallArgs(Expr e, HashSet<string> paramNames, Dictionary<string, string> result)
    {
        switch (e)
        {
            case Expr.Call call:
                var callArgs = new List<Expr>();
                var callRoot = CollectCallArgs(call, callArgs);
                if (ResolveSpecializableCalleeName(callRoot) is { } fnName
                    && _specializableFunctions.TryGetValue(fnName, out var fnSpec)
                    && !_freshCompositionOnlySpecializable.Contains(fnName)
                    && callArgs.Count == fnSpec.ArgCount
                    && callArgs[^1] is Expr.Var arg
                    && paramNames.Contains(arg.Name))
                {
                    result[arg.Name] = fnName;
                }

                CollectSpecializableCallArgs(call.Func, paramNames, result);
                CollectSpecializableCallArgs(call.Arg, paramNames, result);
                break;
            case Expr.If i:
                CollectSpecializableCallArgs(i.Cond, paramNames, result);
                CollectSpecializableCallArgs(i.Then, paramNames, result);
                CollectSpecializableCallArgs(i.Else, paramNames, result);
                break;
            case Expr.Let l:
                CollectSpecializableCallArgs(l.Value, paramNames, result);
                CollectSpecializableCallArgs(l.Body, paramNames, result);
                break;
            case Expr.LetRecursive lr:
                CollectSpecializableCallArgs(lr.Value, paramNames, result);
                CollectSpecializableCallArgs(lr.Body, paramNames, result);
                break;
            case Expr.Match m:
                CollectSpecializableCallArgs(m.Value, paramNames, result);
                foreach (var c in m.Cases)
                {
                    CollectSpecializableCallArgs(c.Body, paramNames, result);
                }

                break;
        }
    }

    /// <summary>
    /// In-place reuse: consumes an available reuse token whose cell has exactly
    /// <paramref name="fieldCount"/> fields (so it is the same size as the constructor being built).
    /// Returns the matching token plus whether any live token was compared. A null token with
    /// <see cref="ReuseTokenMatch.CandidateCompared"/> set means the caller must allocate because
    /// every available token was incompatible.
    /// </summary>
    private ReuseTokenMatch TryConsumeReuseToken(
        int fieldCount,
        bool allowRuntimeManaged,
        bool listCell,
        bool tagless,
        string targetConstructor,
        SourceLocation? location)
    {
        if (!_configuration.EnableReuse)
        {
            return new ReuseTokenMatch(Token: null, CandidateCompared: false);
        }

        for (int i = _reuseTokens.Count - 1; i >= 0; i--)
        {
            ReuseToken token = _reuseTokens[i];
            // A tagless cell is one word smaller than a tagged cell with the same field count, so
            // the two layouts are never interchangeable: reusing a tagless token for a tagged
            // constructor would write past its end.
            ReuseDecisionReason? rejectionReason =
                token.FieldCount != fieldCount
                    ? ReuseDecisionReason.ConstructorFieldCountMismatch
                    : token.ListCell != listCell || token.Tagless != tagless
                        ? ReuseDecisionReason.ConstructorCellKindMismatch
                        : !allowRuntimeManaged && token.RuntimeManaged
                            ? ReuseDecisionReason.RuntimeManagedTokenNotAllowed
                            : null;
            RecordReuseLayoutCompatibilityDecision(
                token,
                fieldCount,
                allowRuntimeManaged,
                listCell,
                targetConstructor,
                location,
                rejectionReason);
            if (rejectionReason is null)
            {
                _reuseTokens.RemoveAt(i);
                return new ReuseTokenMatch(token, CandidateCompared: true);
            }
        }

        return new ReuseTokenMatch(
            Token: null,
            CandidateCompared: _reuseTokens.Count > 0);
    }

    private void RecordReuseTokenProduction(ReuseToken token)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse token must belong to an active function."),
                ReuseDecisionKind.TokenProduction,
                ReuseDecisionMechanism.ReuseToken,
                ReuseDecisionOutcome.Produced,
                ReuseDecisionReason.MatchedCellBecameDead,
                ReuseTokenCandidate(token),
                RelatedGeneratedLabel: null,
                token.Location,
                TokenLifecycle: CreateReuseTokenLifecycle(token)));
    }

    private void RecordReuseTokenDisposition(
        ReuseToken token,
        ReuseDecisionOutcome outcome,
        ReuseDecisionReason reason,
        int? allocationTemp = null,
        string? targetConstructor = null,
        SourceLocation? location = null)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse token disposition must belong to an active function."),
                ReuseDecisionKind.TokenDisposition,
                ReuseDecisionMechanism.ReuseToken,
                outcome,
                reason,
                ReuseTokenCandidate(token),
                RelatedGeneratedLabel: null,
                location ?? token.Location,
                TokenLifecycle: CreateReuseTokenLifecycle(
                    token,
                    allocationTemp,
                    targetConstructor)));
    }

    private void RecordReuseFallbackAllocation(
        ReuseToken? token,
        int allocationTemp,
        string targetConstructor,
        SourceLocation? location,
        ReuseDecisionOutcome outcome,
        ReuseDecisionReason reason,
        ReuseFallbackAllocationKind fallbackKind,
        int? fieldCount = null,
        bool? listCell = null,
        bool? runtimeManaged = null)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse fallback must belong to an active function."),
                ReuseDecisionKind.FallbackAllocation,
                ReuseDecisionMechanism.ReuseToken,
                outcome,
                reason,
                token is null ? null : ReuseTokenCandidate(token),
                RelatedGeneratedLabel: null,
                location ?? token?.Location,
                TokenLifecycle: token is null
                    ? new ReuseTokenLifecycle(
                        TokenTemp: null,
                        SourceValueTemp: null,
                        allocationTemp,
                        fieldCount,
                        listCell,
                        runtimeManaged,
                        targetConstructor,
                        fallbackKind)
                    : CreateReuseTokenLifecycle(
                        token,
                        allocationTemp,
                        targetConstructor,
                        fallbackKind)));
    }

    private static ReuseDecisionCandidate ReuseTokenCandidate(ReuseToken token)
    {
        return new ReuseDecisionCandidate(
            ReuseCandidateKind.Token,
            token.SourceName,
            Temp: token.Temp);
    }

    private static ReuseTokenLifecycle CreateReuseTokenLifecycle(
        ReuseToken token,
        int? allocationTemp = null,
        string? targetConstructor = null,
        ReuseFallbackAllocationKind? fallbackKind = null)
    {
        return new ReuseTokenLifecycle(
            token.Temp,
            token.SourceValueTemp,
            allocationTemp,
            token.FieldCount,
            token.ListCell,
            token.RuntimeManaged,
            targetConstructor,
            fallbackKind);
    }

    private bool ShouldRecordReuseFallback(ReuseTokenMatch tokenMatch)
    {
        return tokenMatch.CandidateCompared
            || _inSpecialization
            || _activeReuseArmOrigin is not null
                && Equals(_activeReuseArmOrigin, _activeFunctionOrigin);
    }

    private static ReuseDecisionReason ReuseFallbackReason(
        ReuseTokenMatch tokenMatch)
    {
        return tokenMatch.CandidateCompared
            ? ReuseDecisionReason.NoCompatibleReuseToken
            : ReuseDecisionReason.NoReuseTokenAvailable;
    }

    private void RecordReuseLayoutCompatibilityDecision(
        ReuseToken token,
        int requestedFieldCount,
        bool runtimeManagedAllowed,
        bool requestedListCell,
        string targetConstructor,
        SourceLocation? location,
        ReuseDecisionReason? rejectionReason)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse layout decision must belong to an active function."),
                ReuseDecisionKind.ConstructorLayoutCompatibility,
                ReuseDecisionMechanism.ReuseToken,
                rejectionReason is null
                    ? ReuseDecisionOutcome.Accepted
                    : ReuseDecisionOutcome.Rejected,
                rejectionReason ?? ReuseDecisionReason.CompatibleConstructorLayout,
                new ReuseDecisionCandidate(
                    ReuseCandidateKind.Token,
                    token.SourceName,
                    Temp: token.Temp),
                RelatedGeneratedLabel: null,
                location ?? token.Location,
                Layout: new ReuseLayoutCompatibility(
                    token.FieldCount,
                    requestedFieldCount,
                    token.ListCell,
                    requestedListCell,
                    token.RuntimeManaged,
                    runtimeManagedAllowed,
                    targetConstructor)));
    }

    /// <summary>
    /// Inlines a saturated call to a non-recursive top-level helper: evaluates the arguments in the
    /// current scope (so they can't be captured by the helper's own parameter names), binds the
    /// parameters to those values, and lowers the helper body in place. Lowering it here means any
    /// constructor in the body can consume a live reuse token from the enclosing arm.
    /// </summary>
    private (int, TypeRef) InlineCall(
        string fnName,
        IReadOnlyList<string> paramNames,
        Expr body,
        List<Expr> args,
        LoweredValueRequest request = default)
    {
        var argSlots = new int[paramNames.Count];
        var argTypes = new TypeRef[paramNames.Count];
        // Params whose argument was built by in-place reuse are themselves linear: a match-then-rebuild
        // on them in the helper body reuses the same cell (intermediate-value linearity).
        var linearParams = new List<string>();
        // Params bound to a FRESH heap input of the enclosing specialization (e.g. makeNode's key/value
        // param bound to the spec's newKey/newValue) inherit that fresh-input status, so a constructor
        // field in the inlined body still materializes them into the persistent blob.
        var freshParams = new List<string>();
        for (int i = 0; i < paramNames.Count; i++)
        {
            // A param is a fresh heap input when its argument names a fresh input of the enclosing
            // specialization OR is a non-variable expression (a value computed in the arm — e.g. an
            // upsert's onHit(value) — which lives in per-iteration arena scratch). See the matching
            // materialization rule in LowerConstructorApplication.
            if (_specFreshInputNames is not null && !_specFreshInputNames.Contains(paramNames[i])
                && (args[i] is not Expr.Var fv || _specFreshInputNames.Contains(fv.Name)))
            {
                freshParams.Add(paramNames[i]);
            }

            var (argTemp, argType) = LowerExpr(args[i]);
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, argTemp));
            argSlots[i] = slot;
            argTypes[i] = argType;
            if (_reuseResultTemps.Contains(argTemp) && !_linearReuseNames.Contains(paramNames[i]))
            {
                linearParams.Add(paramNames[i]);
            }
        }

        var inlineScope = _scopes.Peek();
        for (int i = 0; i < paramNames.Count; i++)
        {
            inlineScope = inlineScope.SetItem(
                paramNames[i],
                new Binding.Local(argSlots[i], argTypes[i]));
        }

        _scopes.Push(inlineScope);
        _inliningInProgress.Add(fnName);
        foreach (var p in linearParams) _linearReuseNames.Add(p);
        if (_specFreshInputNames is not null) foreach (var p in freshParams) _specFreshInputNames.Add(p);
        LoweredValue result = LowerExpr(body, request);
        if (_specFreshInputNames is not null) foreach (var p in freshParams) _specFreshInputNames.Remove(p);
        foreach (var p in linearParams) _linearReuseNames.Remove(p);
        _inliningInProgress.Remove(fnName);
        _scopes.Pop();
        return result.AsPair();
    }

    /// <summary>
    /// Generates (once, cached) an in-place-reuse specialization <c>f$reuse</c> of a single-parameter
    /// recursive function <paramref name="name"/>: the same body lowered with its parameter treated
    /// as a linear (uniquely-owned) reuse root, and self-calls redirected to <c>f$reuse</c> (so the
    /// recursion reuses the unique subtrees). Returns the function label to call. The caller must only
    /// route a provably-unique argument here.
    /// </summary>
    private string GetOrCreateReuseSpecialization(
        string name,
        TypeRef funcType,
        IReadOnlyList<TypeRef> concreteParamTypes,
        Expr generationExpression,
        TraitDictionaryFunctionInfo? traitInfo = null,
        IReadOnlyList<TraitConstraint>? traitConstraints = null)
    {
        // Cache per concrete instantiation: the spec is monomorphized to the call's argument types, so a
        // function used at two element types gets two specializations.
        string cacheKey = name + "|" + string.Join(",", concreteParamTypes.Select(t => Pretty(Prune(t))));
        if (_reuseSpecializations.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        (Expr.Lambda Lambda, string LinearParam, int ArgCount) registeredSpec =
            _specializableFunctions[name];
        Expr.Lambda specializationLambda;
        if (traitInfo is null)
        {
            specializationLambda = registeredSpec.Lambda;
        }
        else
        {
            specializationLambda = (Expr.Lambda)TransformTraitDictionaryValue(
                registeredSpec.Lambda,
                traitInfo,
                threadRecursiveSelf: true,
                threadDictionaryFunctions: true);
            BindTraitDictionaryParameterConstraintsInAbiOrder(
                traitInfo,
                traitConstraints ?? []);
        }
        var spec = (
            Lambda: specializationLambda,
            registeredSpec.LinearParam,
            ArgCount: concreteParamTypes.Count,
            FreshInputNames: (IReadOnlyList<string>)CollectLambdaParams(registeredSpec.Lambda));
        string label = _reuseSpecializations.Count == 0 ? $"{name}__reuse" : $"{name}__reuse${_reuseSpecializations.Count}";
        _reuseSpecializations[cacheKey] = label;
        _reuseSpecializationCaptures[label] = CollectReuseSpecializationCaptures(
            spec.Lambda,
            name);

        string? reuseLabel = LowerReuseSpecializationBody(
            name,
            funcType,
            concreteParamTypes,
            cacheKey,
            label,
            spec);
        RecordReuseSpecializationDecisions(
            label,
            reuseLabel,
            spec.LinearParam,
            generationExpression);
        return label;
    }

    private IReadOnlyList<string> CollectReuseSpecializationCaptures(
        Expr.Lambda lambda,
        string selfName)
    {
        HashSet<string> free = LowerLambdaCoreCollectFreeVariables(lambda, selfName);
        return free
            .Where(name => Lookup(name) is Binding.Local or Binding.Env or Binding.EnvScheme
                or Binding.Self or Binding.Scheme)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private string? LowerReuseSpecializationBody(
        string name,
        TypeRef funcType,
        IReadOnlyList<TypeRef> concreteParamTypes,
        string cacheKey,
        string label,
        (Expr.Lambda Lambda, string LinearParam, int ArgCount, IReadOnlyList<string> FreshInputNames) spec)
    {
        string? savedLinear = _specializingLinearParam;
        string? savedReuseLabel = _specializingReuseLabel;
        bool savedInSpec = _inSpecialization;
        IReadOnlyList<TypeRef>? savedConcrete = _specializationConcreteParamTypes;
        int savedCursor = _specializationParamCursor;
        HashSet<string>? savedFreshInputs = _specFreshInputNames;
        TcoContext? savedTco = _tcoCtx;
        _specializingLinearParam = spec.LinearParam;
        _specializingReuseLabel = null;
        _inSpecialization = true;
        _specializationConcreteParamTypes = concreteParamTypes;
        _specializationParamCursor = 0;
        _specFreshInputNames = new HashSet<string>(spec.FreshInputNames, StringComparer.Ordinal);
        _tcoCtx = CreateReuseSpecializationTcoContext(spec.Lambda, name);
        try
        {
            // LowerLambdaCore emits an incidental closure after registering the function. Calls
            // construct their own specialized closure, so discard only those emitted instructions.
            int instBefore = _inst.Count;
            Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
                SnapshotTempOwnershipFacts();
            LowerReuseSpecializationLambda(spec.Lambda, name, funcType, label, cacheKey);
            if (_inst.Count > instBefore)
            {
                _inst.RemoveRange(instBefore, _inst.Count - instBefore);
            }
            RestoreTempOwnershipFacts(savedTempOwnershipFacts);

            return _specializingReuseLabel;
        }
        finally
        {
            _specializingLinearParam = savedLinear;
            _specializingReuseLabel = savedReuseLabel;
            _inSpecialization = savedInSpec;
            _specializationConcreteParamTypes = savedConcrete;
            _specializationParamCursor = savedCursor;
            _specFreshInputNames = savedFreshInputs;
            _tcoCtx = savedTco;
        }
    }

    private TcoContext? CreateReuseSpecializationTcoContext(
        Expr.Lambda lambda,
        string selfName)
    {
        int parameterCount = CountLambdaChain(lambda);
        return HasTailSelfCalls(GetInnermostBody(lambda), selfName, parameterCount)
            ? new TcoContext(selfName, parameterCount, CollectLambdaParams(lambda))
            {
                InTailPosition = false,
            }
            : null;
    }

    private void RecordReuseSpecializationDecisions(
        string label,
        string? reuseLabel,
        string linearParameter,
        Expr generationExpression)
    {
        // The recursive reuse function (f$reuse itself, or the inner go for a nested-rec shape) is the
        // one whose return values must be below the watermark. If it is fully reusing, the whole
        // specialized call's result is below the watermark, so mark the callable label reset-safe.
        IrFunctionOrigin specializationOrigin = _irFunctionOriginsByLabel[label];
        SourceLocation? generationLocation =
            ResolveSourceLocation(AstSpans.GetOrDefault(generationExpression));
        _reuseDecisions.Add(
            new ReuseDecision(
                specializationOrigin,
                ReuseDecisionKind.SpecializationGeneration,
                ReuseDecisionMechanism.Specialization,
                ReuseDecisionOutcome.Generated,
                ReuseDecisionReason.SpecializableCall,
                new ReuseDecisionCandidate(
                    ReuseCandidateKind.Parameter,
                    linearParameter),
                RelatedGeneratedLabel: null,
                generationLocation));

        IrFunction? recursiveFunc = reuseLabel is not null
            ? _funcs.LastOrDefault(
                function => string.Equals(
                    function.Label,
                    reuseLabel,
                    StringComparison.Ordinal))
            : null;
        FullyReusingResult resetSafety = recursiveFunc is not null
            ? IsFullyReusing(recursiveFunc, reuseLabel!)
            : new FullyReusingResult(
                Accepted: false,
                ReuseDecisionReason.RecursiveReuseFunctionMissing,
                Location: null);
        if (resetSafety.Accepted)
        {
            _fullyReusingLabels.Add(label);
        }

        _reuseDecisions.Add(
            new ReuseDecision(
                specializationOrigin,
                ReuseDecisionKind.ResetSafetyQualification,
                ReuseDecisionMechanism.Specialization,
                resetSafety.Accepted
                    ? ReuseDecisionOutcome.Accepted
                    : ReuseDecisionOutcome.Rejected,
                resetSafety.Reason,
                new ReuseDecisionCandidate(
                    ReuseCandidateKind.Parameter,
                    linearParameter),
                reuseLabel,
                resetSafety.Location ?? generationLocation));
    }

    private readonly record struct FullyReusingResult(
        bool Accepted,
        ReuseDecisionReason Reason,
        SourceLocation? Location);

    /// <summary>
    /// Conservative soundness check for the loop arena reset: returns true only if every value
    /// <paramref name="f"/> returns is guaranteed to lie below the loop watermark. Sufficient
    /// condition: no fresh result-heap allocation at all (no <c>AllocAdt</c>/<c>ConcatStr</c>/copy-out
    /// — every constructor must be an in-place <c>AllocReusing</c>), every raw <c>Alloc</c> is only an
    /// environment for a closure (recursion scaffolding), and every closure is a self-closure
    /// (label = <paramref name="selfLabel"/>) used only as a call target. Under those constraints the
    /// result is built solely from reused cells, scrutinee fields, and recursive self-results — all
    /// below the watermark — while the env/closure scaffolding is dead after the call and reclaimable.
    /// </summary>
    private FullyReusingResult IsFullyReusing(IrFunction f, string selfLabel)
    {
        if (IsFullyReusingFindForbiddenAllocation(f) is { } forbidden)
        {
            return forbidden;
        }

        // Build temp → reading instructions, then require every Alloc to feed only a MakeClosure env
        // and every MakeClosure to feed only a CallClosure callee.
        var readers = IsFullyReusingCollectReaders(f);

        // Local-slot dataflow, for values that pass through a let/inlined-arg slot: slot → number
        // of stores, and slot → the temps its loads produce.
        var (slotStores, slotLoads) = IsFullyReusingCollectSlotDataflow(f);

        return IsFullyReusingAllocationsConsumed(f, readers, slotStores, slotLoads);
    }

    private static FullyReusingResult? IsFullyReusingFindForbiddenAllocation(IrFunction f)
    {
        foreach (IrInst instruction in f.Instructions)
        {
            ReuseDecisionReason? reason = instruction switch
            {
                IrInst.AllocAdt => ReuseDecisionReason.FreshAdtAllocation,
                IrInst.AllocAdtStack => ReuseDecisionReason.FreshStackAdtAllocation,
                IrInst.AllocStack => ReuseDecisionReason.FreshStackAllocation,
                IrInst.ConcatStr => ReuseDecisionReason.StringConcatenationAllocation,
                IrInst.CopyOutArena => ReuseDecisionReason.ArenaCopyOut,
                IrInst.CopyOutList => ReuseDecisionReason.ListCopyOut,
                IrInst.CopyOutClosure => ReuseDecisionReason.ClosureCopyOut,
                IrInst.CopyOutTcoListCell => ReuseDecisionReason.TcoListCellCopyOut,
                _ => null,
            };
            if (reason is { } rejection)
            {
                return new FullyReusingResult(
                    Accepted: false,
                    rejection,
                    instruction.Location);
            }
        }

        return null;
    }

    private static Dictionary<int, List<IrInst>> IsFullyReusingCollectReaders(IrFunction f)
    {
        var readers = new Dictionary<int, List<IrInst>>();
        var buf = new HashSet<int>();
        foreach (var inst in f.Instructions)
        {
            buf.Clear();
            IrOptimizer.CollectUsedTemps(inst, buf);
            foreach (var t in buf)
            {
                if (!readers.TryGetValue(t, out var list))
                {
                    readers[t] = list = new List<IrInst>();
                }

                list.Add(inst);
            }
        }

        return readers;
    }

    private static (Dictionary<int, int> SlotStores, Dictionary<int, List<int>> SlotLoads) IsFullyReusingCollectSlotDataflow(IrFunction f)
    {
        var slotStores = new Dictionary<int, int>();
        var slotLoads = new Dictionary<int, List<int>>();
        foreach (var inst in f.Instructions)
        {
            switch (inst)
            {
                case IrInst.StoreLocal sl:
                    slotStores[sl.Slot] = slotStores.GetValueOrDefault(sl.Slot) + 1;
                    break;
                case IrInst.LoadLocal ll:
                    if (!slotLoads.TryGetValue(ll.Slot, out var loads))
                    {
                        slotLoads[ll.Slot] = loads = new List<int>();
                    }

                    loads.Add(ll.Target);
                    break;
            }
        }

        return (slotStores, slotLoads);
    }

    // A fresh in-arm value (e.g. an upsert's stats tuple) is SAFELY consumed when every reader
    // either writes INTO it, reads a field FROM it, materializes it into persistent storage
    // (CopyFixedInto / CopyOutArenaToSpace), captures it in a closure env (the closure itself is
    // checked separately), or moves it through a single-store local slot whose every load is itself
    // safely consumed. Anything else (a SetAdtField storing the raw pointer as a node field, a
    // call, a return) means per-iteration scratch could escape, so the reset is disqualified.
    private static bool IsFullyReusingSafelyConsumed(int temp, int depth, Dictionary<int, List<IrInst>> readers,
        Dictionary<int, int> slotStores, Dictionary<int, List<int>> slotLoads)
    {
        if (!readers.TryGetValue(temp, out var rs))
        {
            return true;
        }

        foreach (var r in rs)
        {
            switch (r)
            {
                case IrInst.MakeClosure:
                case IrInst.MakeClosureStack:
                    continue;
                case IrInst.SetAdtField sa when sa.Ptr == temp:
                    continue;
                case IrInst.GetAdtField gf when gf.Ptr == temp:
                    continue;
                case IrInst.StoreMemOffset sm when sm.BasePtr == temp:
                    continue;
                case IrInst.LoadMemOffset lm when lm.BasePtr == temp:
                    continue;
                case IrInst.CopyFixedInto cfi when cfi.SrcTemp == temp:
                    continue;
                case IrInst.CopyOutArenaToSpace co when co.SrcTemp == temp:
                    continue;
                case IrInst.Borrow b when b.SourceTemp == temp && depth < 4 && IsFullyReusingSafelyConsumed(b.Target, depth + 1, readers, slotStores, slotLoads):
                    continue;
                case IrInst.StoreLocal sl when depth < 4
                    && slotStores.GetValueOrDefault(sl.Slot) == 1
                    && (!slotLoads.TryGetValue(sl.Slot, out var loads)
                        || loads.All(lt => IsFullyReusingSafelyConsumed(lt, depth + 1, readers, slotStores, slotLoads))):
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    // A closure temp is CONSUMED AS A CALL TARGET (so it never escapes into a returned cell) when
    // every reader either calls it directly (a CallClosure with it as the CALLEE, not an argument)
    // or moves it through a single-store local slot / Borrow whose loads are themselves consumed as
    // call targets. This admits a `let rec go = … in go(x)` helper inlined per node (e.g.
    // HashMap's strCompare on the composite-key descent): go's closure is MakeClosure'd, stored to
    // a slot, loaded, and immediately called — transient scratch under an arena bracket that
    // produces a scalar, never captured into the rebuilt tree. Passing the closure as an ARGUMENT
    // (CallClosure.ArgTemp) is still rejected, since it could then be captured or returned.
    private static bool IsFullyReusingClosureConsumedAsCallTarget(int temp, int depth, Dictionary<int, List<IrInst>> readers,
        Dictionary<int, int> slotStores, Dictionary<int, List<int>> slotLoads)
    {
        if (!readers.TryGetValue(temp, out var rs))
        {
            return true;
        }

        foreach (var r in rs)
        {
            switch (r)
            {
                case IrInst.CallClosure cc when cc.ClosureTemp == temp:
                    continue;
                case IrInst.Borrow b when b.SourceTemp == temp && depth < 4 && IsFullyReusingClosureConsumedAsCallTarget(b.Target, depth + 1, readers, slotStores, slotLoads):
                    continue;
                case IrInst.StoreLocal sl when depth < 4
                    && slotStores.GetValueOrDefault(sl.Slot) == 1
                    && (!slotLoads.TryGetValue(sl.Slot, out var loads)
                        || loads.All(lt => IsFullyReusingClosureConsumedAsCallTarget(lt, depth + 1, readers, slotStores, slotLoads))):
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private FullyReusingResult IsFullyReusingAllocationsConsumed(
        IrFunction f,
        Dictionary<int, List<IrInst>> readers,
        Dictionary<int, int> slotStores, Dictionary<int, List<int>> slotLoads)
    {
        foreach (IrInst instruction in f.Instructions)
        {
            // Compiler-generated evidence is temporary call support and is never part of a source
            // value's reconstructed graph. Its allocations live inside the same loop arena window,
            // so they must not make an otherwise fully in-place specialization fail reset safety.
            if (_traitEvidenceConstructionInstructions.Contains(instruction))
            {
                continue;
            }
            switch (instruction)
            {
                case IrInst.Alloc alloc when !IsFullyReusingSafelyConsumed(alloc.Target, 0, readers, slotStores, slotLoads):
                    return new FullyReusingResult(
                        Accepted: false,
                        ReuseDecisionReason.RawAllocationMayEscape,
                        alloc.Location);
                case IrInst.MakeClosure mk when !IsFullyReusingClosureConsumedAsCallTarget(mk.Target, 0, readers, slotStores, slotLoads):
                    return new FullyReusingResult(
                        Accepted: false,
                        ReuseDecisionReason.ClosureMayEscape,
                        mk.Location);
                case IrInst.MakeClosureStack mks when !IsFullyReusingClosureConsumedAsCallTarget(mks.Target, 0, readers, slotStores, slotLoads):
                    return new FullyReusingResult(
                        Accepted: false,
                        ReuseDecisionReason.StackClosureMayEscape,
                        mks.Location);
            }
        }

        return new FullyReusingResult(
            Accepted: true,
            ReuseDecisionReason.NoResetInvalidatingAllocation,
            Location: null);
    }

    /// <summary>
    /// Lowers <c>f(args…)(acc)</c> as a call to the in-place-reuse specialization <c>f$reuse</c>:
    /// builds an empty-env closure for the (no-capture) specialized function and applies it to all
    /// arguments in turn (the last being the uniquely-owned accumulator). <c>f$reuse</c> rewrites that
    /// tree in place.
    /// </summary>
    // Whether a reuse specialization's rebuilt accumulator references only persistent (never-reset) memory,
    // so the loop back-edge may reclaim the main arena each iteration without dangling anything. The tree's
    // nodes are always persistent (to-space / in-place reuse) and its children are too; the only risk is a
    // heap LEAF field pointing into per-iteration scratch. For the stdlib Map (MapTree(K, V)): the key is
    // materialized to the persistent blob on insert and kept from the matched node on update, so a copy or
    // Str/Bytes key is always persistent; the value is freshly produced on every update and only
    // insert-materialized, so it is persistent only when it is a copy type. Other (non-Map) heap
    // accumulators are treated conservatively as not-yet-persistent (correct, just not reset-reclaimed):
    // the general fix is to materialize fresh heap leaf fields on the update path too.
    private bool AccumulatorIsFullyPersistent(TypeRef accType)
    {
        TypeRef accumulator = Prune(accType);
        if (accumulator is TypeRef.TList list)
        {
            return CanArenaReset(list.Element);
        }

        if (accumulator is not TypeRef.TNamedType named)
        {
            return false;
        }

        // Structural check over the accumulator ADT (this used to special-case MapTree): every
        // constructor field must be either the accumulator type itself (a recursive child — the
        // node cells live in to-space / are rewritten in place, so they are persistent) or a leaf
        // type the reuse materialization makes persistent (copy types inline; Str/Bytes and
        // copy-tuples are copied to the blob on insert and overwritten in place on update — see
        // LowerConstructorApplication). Any other field shape (a list, a closure, a foreign ADT)
        // could point into per-iteration scratch, so the loop must not reset.
        var sym = named.Symbol;
        if (sym.Constructors.Count == 0)
        {
            return false;
        }

        Dictionary<TypeParameterSymbol, TypeRef>? typeParamMap = null;
        if (sym.TypeParameters.Count > 0 && named.TypeArgs.Count == sym.TypeParameters.Count)
        {
            typeParamMap = new Dictionary<TypeParameterSymbol, TypeRef>();
            for (int i = 0; i < sym.TypeParameters.Count; i++)
            {
                typeParamMap[sym.TypeParameters[i]] = named.TypeArgs[i];
            }
        }

        foreach (var ctor in sym.Constructors)
        {
            foreach (var fieldType in ctor.ParameterTypes)
            {
                var resolved = Prune(ResolveFieldType(fieldType, typeParamMap));
                bool isSelf = resolved is TypeRef.TNamedType fieldNamed
                    && ReferenceEquals(fieldNamed.Symbol, sym);
                if (!isSelf && !IsReuseMaterializableFieldType(resolved))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // A reuse-node heap leaf field type the materialization can make persistent (blob): copy types (inline,
    // nothing to do), Str/Bytes (dynamic copy), tuples of copy-type elements (fixed-size shallow copy), a
    // list of Str (rebuilt fresh into to-space via EmitListToSpaceCopy), and a non-resource ADT whose own
    // layout EmitDeepCopyToSpace can relocate (e.g. a record of Str/copy-type fields). Must match the
    // materialization cases in MaterializeSpecializationField (called from LowerConstructorApplication).
    private bool IsReuseMaterializableFieldType(TypeRef t) =>
        CanArenaReset(t)
        || t is TypeRef.TStr or TypeRef.TBytes
        || (t is TypeRef.TTuple tup && tup.Elements.All(CanArenaReset))
        || (t is TypeRef.TList list && Prune(list.Element) is TypeRef.TStr)
        || (t is TypeRef.TNamedType named
            && !BuiltinRegistry.IsResourceTypeName(named.Symbol.Name)
            && IsToSpaceCopySafeType(named));

    private sealed record ReuseSpecializationQualification(
        string TargetFunction,
        TypeRef? FunctionType,
        ReuseDecisionCandidate Candidate,
        bool Accepted,
        ReuseDecisionReason Reason);

    /// <summary>
    /// Applies the existing direct-unique and fresh-composition specialization gates once.
    /// Returning null means the call does not have one of those candidate shapes; an accepted result
    /// carries the already-resolved function type used by lowering, while a rejected result carries
    /// the first concrete failed gate for the reporting path to retain when appropriate.
    /// </summary>
    private ReuseSpecializationQualification? QualifyReuseSpecializationCall(
        Expr rootExpr,
        IReadOnlyList<Expr> arguments)
    {
        if (ResolveSpecializableCalleeName(rootExpr) is not { } targetFunction
            || !_specializableFunctions.TryGetValue(targetFunction, out var specialization)
            || arguments.Count != specialization.ArgCount)
        {
            return null;
        }

        Expr accumulator = arguments[^1];
        if (accumulator is not (Expr.Var or Expr.Call))
        {
            return null;
        }

        ReuseDecisionCandidate candidate = CreateReuseSpecializationCandidate(accumulator);
        Binding? binding = Lookup(targetFunction);
        return accumulator switch
        {
            Expr.Var variable => QualifyVariableReuseSpecialization(
                targetFunction,
                candidate,
                binding,
                variable,
                arguments.Count),
            Expr.Call freshResult => QualifyFreshResultReuseSpecialization(
                targetFunction,
                candidate,
                binding,
                freshResult,
                specialization.ArgCount,
                arguments.Count),
            _ => throw new InvalidOperationException(
                "A reuse specialization candidate must be a variable or call result."),
        };
    }

    private bool ShouldRecordReuseSpecializationCandidateRejection(
        Expr rootExpr,
        string targetFunction)
    {
        return !(rootExpr is Expr.Var selfVariable
                && Lookup(selfVariable.Name) is Binding.Self)
            && !(_tcoCtx is { } tco && IsTcoSelfCallRoot(rootExpr, tco))
            && !IsActiveSourceFunction(targetFunction);
    }

    private bool CannotAttemptHigherOrderReuse(IReadOnlyList<Expr> arguments)
    {
        foreach (Expr argument in arguments)
        {
            if (ResolveSpecializableCalleeName(argument) is not { } name)
            {
                continue;
            }
            Binding? binding = Lookup(name);
            TypeRef? type = TryGetKnownBindingType(binding);
            if (type is not null
                && Prune(type) is TypeRef.TFun
                && (binding is Binding.Local or Binding.Env
                    || BindingHasTraitConstraints(binding)
                    || _traitDictionaryFunctions.ContainsKey(name)
                    || binding is Binding.Scheme scheme && ValueTypeRemainsAbstract(scheme.S.Body)
                    || binding is Binding.EnvScheme environment
                        && ValueTypeRemainsAbstract(environment.S.Body)))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsActiveSourceFunction(string targetFunction)
    {
        SourceFunctionOrigin? source = _activeFunctionOrigin?.Source;
        if (source is null)
        {
            return false;
        }

        if (string.Equals(
                targetFunction,
                source.SourceName,
                StringComparison.Ordinal))
        {
            return true;
        }

        int separator = source.QualifiedName?.LastIndexOf('.') ?? -1;
        if (separator <= 0)
        {
            return false;
        }

        string stitchedName =
            ProjectSupport.SanitizeModuleBindingName(
                source.QualifiedName![..separator])
            + "_"
            + source.SourceName;
        return string.Equals(
            targetFunction,
            stitchedName,
            StringComparison.Ordinal);
    }

    private ReuseSpecializationQualification QualifyVariableReuseSpecialization(
        string targetFunction,
        ReuseDecisionCandidate candidate,
        Binding? binding,
        Expr.Var variable,
        int argumentCount)
    {
        if (!_linearSpecializationAccumulators.Contains(variable.Name))
        {
            return CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.AccumulatorNotProvenUnique);
        }
        TypeRef? accumulatorType = TryGetKnownBindingType(Lookup(variable.Name));
        TypeRef? targetType = TryGetKnownBindingType(binding);
        if (accumulatorType is not null
            && Prune(accumulatorType) is TypeRef.TNamedType
            && ValueTypeRemainsAbstract(accumulatorType)
            && targetType is TypeRef.TFun { Arg: not TypeRef.TFun }
            && _specializableFunctions.TryGetValue(targetFunction, out var specialization)
            && ExpressionContainsMappedTraitOperator(specialization.Lambda))
        {
            return CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.FreshAccumulatorLayoutUnsupported);
        }
        return QualifyResolvedReuseSpecialization(
            targetFunction,
            candidate,
            binding,
            argumentCount);
    }

    private ReuseSpecializationQualification QualifyFreshResultReuseSpecialization(
        string targetFunction,
        ReuseDecisionCandidate candidate,
        Binding? binding,
        Expr.Call freshResult,
        int accumulatorOrdinal,
        int argumentCount)
    {
        if (!IsFreshOwnershipResultCall(freshResult))
        {
            return CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.FreshResultNotProven);
        }

        if (binding is not null
            && NthCurriedArgType(
                Prune(binding.Type),
                accumulatorOrdinal - 1) is not TypeRef.TList)
        {
            return CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.FreshAccumulatorLayoutUnsupported);
        }

        return QualifyResolvedReuseSpecialization(
            targetFunction,
            candidate,
            binding,
            argumentCount);
    }

    private ReuseSpecializationQualification QualifyResolvedReuseSpecialization(
        string targetFunction,
        ReuseDecisionCandidate candidate,
        Binding? binding,
        int argumentCount)
    {
        if (binding is null)
        {
            return CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.CalleeBindingUnavailable);
        }

        // A polymorphic binding's Body belongs to its reusable scheme. Specialization unifies the
        // function type with concrete call arguments, so it must operate on a fresh instantiation;
        // otherwise the first specialization monomorphizes the shared scheme and later Int/Str uses
        // leak into one another.
        TypeRef functionType = Prune(TryGetKnownBindingType(binding) ?? binding.Type);
        return SpecializationRebuildsAccumulator(functionType, argumentCount)
            ? new ReuseSpecializationQualification(
                targetFunction,
                functionType,
                candidate,
                Accepted: true,
                ReuseDecisionReason.SpecializableCall)
            : CreateReuseSpecializationQualification(
                targetFunction,
                candidate,
                ReuseDecisionReason.ResultDoesNotRebuildAccumulator);
    }

    private static ReuseSpecializationQualification CreateReuseSpecializationQualification(
        string targetFunction,
        ReuseDecisionCandidate candidate,
        ReuseDecisionReason reason)
    {
        return new ReuseSpecializationQualification(
            targetFunction,
            FunctionType: null,
            candidate,
            Accepted: false,
            reason);
    }

    private ReuseDecisionCandidate CreateReuseSpecializationCandidate(Expr accumulator)
    {
        string? sourceName = accumulator switch
        {
            Expr.Var variable => variable.Name,
            Expr.Call call => ResolveCallResultSourceName(call),
            _ => null,
        };
        int? localSlot = accumulator is Expr.Var localVariable
            ? Lookup(localVariable.Name) switch
            {
                Binding.Local local => local.Slot,
                Binding.Scheme scheme => scheme.Slot,
                _ => null,
            }
            : null;
        return new ReuseDecisionCandidate(
            ReuseCandidateKind.Value,
            sourceName,
            localSlot);
    }

    private string? ResolveCallResultSourceName(Expr.Call call)
    {
        var arguments = new List<Expr>();
        Expr root = CollectCallArgs(call, arguments);
        return ResolveSpecializableCalleeName(root);
    }

    private void RecordReuseSpecializationCandidateRejection(
        ReuseSpecializationQualification qualification,
        Expr call)
    {
        _reuseDecisions.Add(
            new ReuseDecision(
                _activeFunctionOrigin
                    ?? throw new InvalidOperationException(
                        "A reuse specialization decision must belong to an active function."),
                ReuseDecisionKind.SpecializationCandidateQualification,
                ReuseDecisionMechanism.Specialization,
                ReuseDecisionOutcome.Rejected,
                qualification.Reason,
                qualification.Candidate,
                RelatedGeneratedLabel: null,
                ResolveSourceLocation(AstSpans.GetOrDefault(call)),
                TargetFunction: qualification.TargetFunction));
    }

    // True only if a specializable function actually rebuilds its accumulator in place: its result
    // type (after applying all <paramref name="argCount"/> args) is the same named ADT as its last
    // parameter (the accumulator). A rewriter like Map.set (MapTree -> MapTree) qualifies; a pure
    // reader like Map.get (MapTree -> Maybe) does not — routing a reader through the reuse spec would
    // allocate its result cell (e.g. Some(value)) into the never-reset to-space, leaking one cell per
    // call. Readers stay on the normal path, where their result lives in the reclaimed main arena.
    //
    // Residual (rewriters): overwriting a node's heap-leaf value (a fresh Str each iteration)
    // materializes the new blob into the never-reset to-space while the old one becomes dead but is
    // not reclaimed — max RSS then grows linearly with iterations on a constant-size map. Reclaiming
    // the old blob is sound only once the value is known dead and unaliased, which the uniqueness
    // typing work establishes; the reclaim is folded into that work rather than a standalone
    // to-space freelist here.
    private bool SpecializationRebuildsAccumulator(TypeRef funcType, int argCount)
    {
        var current = Prune(funcType);
        TypeRef? lastParam = null;
        for (int i = 0; i < argCount; i++)
        {
            if (current is not TypeRef.TFun funType)
            {
                return false;
            }

            lastParam = Prune(funType.Arg);
            current = Prune(funType.Ret);
        }

        return lastParam switch
        {
            TypeRef.TList when current is TypeRef.TList => true,
            TypeRef.TNamedType paramNamed when current is TypeRef.TNamedType resultNamed
                => string.Equals(paramNamed.Symbol.Name, resultNamed.Symbol.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private (int, TypeRef) LowerReuseSpecializedCall(
        string name,
        TypeRef funcType,
        List<Expr> args,
        Expr callExpr,
        TraitDictionaryFunctionInfo? traitInfo = null,
        IReadOnlyList<TraitConstraint>? traitConstraints = null,
        int? unspecializedFunctionTemp = null)
    {
        PreparedReuseArguments prepared = PrepareReuseSpecializationArguments(
            name,
            funcType,
            args,
            callExpr,
            traitInfo,
            traitConstraints);

        // Reuse specialization currently proves uniqueness and layout only for data arguments; it
        // does not prove that an abstract or dictionary-bearing closure's captured evidence remains
        // valid in the generated environment. Keep those calls on their ordinary ownership-correct
        // path while retaining specialization for concrete evidence-free function arguments.
        if (CannotSpecializeAbstractEvidenceCall(name, args, prepared.SourceTypes))
        {
            return LowerUnspecializedReuseCall(name, funcType, prepared, unspecializedFunctionTemp);
        }

        // Every fresh constructor cell a specialization builds lives in the persistent to-space, and
        // only the field shapes the materialization can copy into the blob (copy types, Str/Bytes,
        // and tuples of copy types) become persistent there. An accumulator whose constructors can
        // hold any other shape (a list, a closure, a foreign ADT) would keep the caller's fresh
        // scratch value as a raw pointer that dangles once that scratch is reclaimed, so such a call
        // stays on its ordinary path, where the callee's entry normalizes the arguments it stores.
        if (!AccumulatorLayoutIsPersistable(prepared.SourceTypes[^1]))
        {
            RecordAccumulatorLayoutRejection(name, funcType, args[^1], callExpr);
            return LowerUnspecializedReuseCall(name, funcType, prepared, unspecializedFunctionTemp);
        }

        string label = GetOrCreateReuseSpecialization(
            name,
            BuildReuseSpecializationFunctionType(
                funcType,
                prepared.SpecializationTypes,
                traitInfo?.Dictionaries.Count ?? 0),
            prepared.SpecializationTypes,
            callExpr,
            traitInfo,
            traitConstraints);
        // If the specialization fully reuses, its result is the accumulator (the last argument)
        // rewritten in place — address-stable exactly when that argument was. Record the call node so a
        // back-edge stability check can trace through it, and (when the argument is a bare accumulator
        // name) mark that name reset-safe. The reset itself still requires the back-edge argument to be
        // proven address-stable — this marking is necessary, not sufficient.
        RecordFullyReusingCall(label, callExpr, args[^1], prepared.SourceTypes[^1]);

        int closureTemp = LowerReuseSpecializationClosure(label);

        // Apply the specialized closure to each (already-lowered) argument in turn.
        int current = closureTemp;
        foreach (int argTemp in prepared.AppliedTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, current, argTemp));
            current = next;
        }

        return (current, prepared.ResultType);
    }

    private (int, TypeRef) LowerUnspecializedReuseCall(
        string name,
        TypeRef funcType,
        PreparedReuseArguments prepared,
        int? unspecializedFunctionTemp)
    {
        if (unspecializedFunctionTemp is not null)
        {
            return ApplyLoweredArgs(
                unspecializedFunctionTemp.Value,
                prepared.AppliedTemps,
                prepared.ResultType);
        }
        (int sourceTemp, TypeRef sourceType) = TryLowerTraitDictionaryFunctionValue(new Expr.Var(name), funcType)
            ?? LowerVar(new Expr.Var(name));
        Unify(sourceType, funcType);
        return ApplyLoweredArgs(sourceTemp, prepared.SourceTemps, prepared.ResultType);
    }

    // A list accumulator's cells are rebuilt in the ordinary heap; only a named ADT accumulator has
    // its fresh cells allocated into the persistent to-space, so only its field layout is gated.
    private bool AccumulatorLayoutIsPersistable(TypeRef accumulatorType) =>
        Prune(accumulatorType) is not TypeRef.TNamedType
        || AccumulatorIsFullyPersistent(accumulatorType);

    private void RecordAccumulatorLayoutRejection(
        string name,
        TypeRef funcType,
        Expr accumulator,
        Expr call)
    {
        Expr root = call is Expr.Call callExpression
            ? CollectCallArgs(callExpression, [])
            : call;
        if (_activeFunctionOrigin is null
            || !ShouldRecordReuseSpecializationCandidateRejection(root, name))
        {
            return;
        }

        RecordReuseSpecializationCandidateRejection(
            new ReuseSpecializationQualification(
                name,
                funcType,
                CreateReuseSpecializationCandidate(accumulator),
                Accepted: false,
                ReuseDecisionReason.AccumulatorLayoutNotPersistent),
            call);
    }

    private void RecordFullyReusingCall(
        string label,
        Expr call,
        Expr accumulator,
        TypeRef accumulatorType)
    {
        if (!_fullyReusingLabels.Contains(label)
            || !AccumulatorIsFullyPersistent(accumulatorType))
        {
            return;
        }
        _inPlaceReuseCallExprs.Add(call);
        if (accumulator is Expr.Var variable)
        {
            _resetSafeAccumulators.Add(variable.Name);
        }
    }

    private static TypeRef BuildReuseSpecializationFunctionType(
        TypeRef exposedFunctionType,
        IReadOnlyList<TypeRef> specializationTypes,
        int dictionaryCount)
    {
        TypeRef runtimeFunctionType = exposedFunctionType;
        for (int index = dictionaryCount - 1; index >= 0; index--)
        {
            runtimeFunctionType = new TypeRef.TFun(
                specializationTypes[index],
                runtimeFunctionType);
        }
        return runtimeFunctionType;
    }

    private sealed record PreparedReuseArguments(
        int[] SourceTemps,
        List<TypeRef> SourceTypes,
        int[] AppliedTemps,
        List<TypeRef> SpecializationTypes,
        TypeRef ResultType);

    private PreparedReuseArguments PrepareReuseSpecializationArguments(
        string name,
        TypeRef functionType,
        IReadOnlyList<Expr> arguments,
        Expr call,
        TraitDictionaryFunctionInfo? traitInfo,
        IReadOnlyList<TraitConstraint>? traitConstraints)
    {
        (int[] sourceTemps, List<TypeRef> sourceTypes) =
            LowerTypedSpecializationArguments(arguments, functionType);
        TypeRef resultType = Prune(functionType);
        for (int index = 0; index < arguments.Count; index++)
        {
            resultType = Prune(((TypeRef.TFun)Prune(resultType)).Ret);
        }
        List<(int Temp, TypeRef Type)> dictionaries = traitInfo is null
            ? []
            : BuildResolvedTraitDictionaryValues(
                name,
                traitInfo,
                traitConstraints ?? [],
                GetSpan(call));
        return new PreparedReuseArguments(
            sourceTemps,
            sourceTypes,
            dictionaries.Select(dictionary => dictionary.Temp).Concat(sourceTemps).ToArray(),
            dictionaries.Select(dictionary => dictionary.Type).Concat(sourceTypes).ToList(),
            resultType);
    }

    private bool CannotSpecializeAbstractEvidenceCall(
        string name,
        IReadOnlyList<Expr> arguments,
        IReadOnlyList<TypeRef> types)
    {
        bool registered = _traitDictionaryFunctions.ContainsKey(name)
            || TryGetTraitDictionaryInfo(name, Lookup(name), out _)
            || BindingHasTraitConstraints(Lookup(name))
            || _specializableFunctions.TryGetValue(name, out var specialization)
            && ExpressionContainsMappedTraitOperator(specialization.Lambda);
        bool abstractValue = types.Any(ValueTypeRemainsAbstract);
        bool constrainedFunctionArgument = arguments.Any(argument =>
            IsTraitDictionaryFunctionValue(argument, out _)
            || ResolveSpecializableCalleeName(argument) is { } argumentName
            && (_traitDictionaryFunctions.ContainsKey(argumentName)
                || BindingHasTraitConstraints(Lookup(argumentName))));
        bool localFunctionArgument = arguments.Select((argument, index) => (argument, index))
            .Any(item => Prune(types[item.index]) is TypeRef.TFun
                && ResolveSpecializableCalleeName(item.argument) is { } argumentName
                && Lookup(argumentName) switch
                {
                    Binding.Local or Binding.Env => true,
                    Binding.Scheme scheme => ValueTypeRemainsAbstract(scheme.S.Body),
                    Binding.EnvScheme environment => ValueTypeRemainsAbstract(environment.S.Body),
                    _ => false,
                });
        return constrainedFunctionArgument
            || localFunctionArgument
            || types.Any(type => Prune(type) is TypeRef.TFun && ValueTypeRemainsAbstract(type))
            || registered && abstractValue;
    }

    private static bool ExpressionContainsMappedTraitOperator(Expr expression)
    {
        if (GetMappedOperatorTraitName(expression) is not null)
        {
            return true;
        }
        bool found = false;
        _ = MapChildExpressions(expression, child =>
        {
            found |= ExpressionContainsMappedTraitOperator(child);
            return child;
        });
        return found;
    }

    private static bool BindingHasTraitConstraints(Binding? binding) => binding switch
    {
        Binding.Scheme scheme => scheme.S.Constraints.Count > 0,
        Binding.EnvScheme environment => environment.S.Constraints.Count > 0,
        Binding.Self self => self.Requirements?.Count > 0,
        _ => false,
    };

    private bool ValueTypeRemainsAbstract(TypeRef type) => Prune(type) switch
    {
        TypeRef.TVar or TypeRef.TTypeParam => true,
        TypeRef.TFun function =>
            ValueTypeRemainsAbstract(function.Arg) || ValueTypeRemainsAbstract(function.Ret),
        TypeRef.TList list => ValueTypeRemainsAbstract(list.Element),
        TypeRef.TTuple tuple => tuple.Elements.Any(ValueTypeRemainsAbstract),
        TypeRef.TNamedType named => named.TypeArgs.Any(ValueTypeRemainsAbstract),
        TypeRef.TPtr pointer => ValueTypeRemainsAbstract(pointer.Pointee),
        _ => false,
    };

    private int LowerReuseSpecializationClosure(string label)
    {
        IReadOnlyList<string> captures = _reuseSpecializationCaptures[label];
        int envSizeBytes = captures.Count * 8;
        int envPtr = NewTemp();
        if (captures.Count == 0)
        {
            Emit(new IrInst.LoadConstInt(envPtr, 0));
        }
        else
        {
            Emit(new IrInst.Alloc(envPtr, envSizeBytes));
            for (int index = 0; index < captures.Count; index++)
            {
                (int captureTemp, _) = LowerVar(new Expr.Var(captures[index]));
                Emit(new IrInst.StoreMemOffset(envPtr, index * 8, captureTemp));
            }
        }

        int closureTemp = NewTemp();
        Emit(new IrInst.MakeClosure(closureTemp, label, envPtr, envSizeBytes));
        return closureTemp;
    }

    /// <summary>
    /// Resolves a call whose root is one of the four data-parallel combinators to the grained combinator
    /// it monomorphizes through, plus the full argument list (the `map`/`reduce` wrappers prepend a
    /// literal grain of 1). Returns null when the root is not a saturated combinator call.
    /// </summary>
    private (string GrainedName, Expr.Lambda Lambda, List<Expr> Args)? TryResolveParallelCombinatorCall(Expr rootExpr, List<Expr> collectedArgs)
    {
        if (ResolveSpecializableCalleeName(rootExpr) is not { } calleeName)
        {
            return null;
        }

        // Direct grained call: monomorphize as-is.
        if (_parallelSpecializable.TryGetValue(calleeName, out var grained) && collectedArgs.Count == grained.ArgCount)
        {
            return (calleeName, grained.Lambda, collectedArgs);
        }

        // grain-1 wrapper (`map`/`reduce`): route to the grained combinator with grain = 1 prepended.
        string? grainedName =
            string.Equals(calleeName, ParallelMapName, StringComparison.Ordinal) && collectedArgs.Count == 2 ? ParallelMapGrainedName
            : string.Equals(calleeName, ParallelReduceName, StringComparison.Ordinal) && collectedArgs.Count == 4 ? ParallelReduceGrainedName
            : null;
        if (grainedName is not null && _parallelSpecializable.TryGetValue(grainedName, out var grainedTarget))
        {
            var args = new List<Expr>(collectedArgs.Count + 1) { new Expr.IntLit(1) };
            args.AddRange(collectedArgs);
            return (grainedName, grainedTarget.Lambda, args);
        }

        return null;
    }

    // The polymorphic signatures of the grained data-parallel combinators, rebuilt with fresh type
    // variables so that lowering the arguments against them propagates concrete element types (e.g. an
    // identity mapper's element type is fixed by the list argument, not by the lambda alone). Returns the
    // full curried function type and the combinator's result type (List b for map, the accumulator for
    // reduce). The leading `grain : Int` parameter is present on both.
    private (TypeRef FuncType, TypeRef ResultType) BuildParallelCombinatorType(string name)
    {
        if (string.Equals(name, ParallelMapGrainedName, StringComparison.Ordinal))
        {
            // mapGrained : Int -> (a -> b) -> List a -> List b
            var a = NewTypeVar();
            var b = NewTypeVar();
            var func = new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(new TypeRef.TFun(a, b), new TypeRef.TFun(new TypeRef.TList(a), new TypeRef.TList(b))));
            return (func, new TypeRef.TList(b));
        }

        // reduceGrained : Int -> (c -> c -> c) -> c -> (e -> c) -> List e -> c
        var acc = NewTypeVar();
        var elem = NewTypeVar();
        var combine = new TypeRef.TFun(acc, new TypeRef.TFun(acc, acc));
        var mapFn = new TypeRef.TFun(elem, acc);
        var reduceFunc = new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TFun(combine, new TypeRef.TFun(acc, new TypeRef.TFun(mapFn, new TypeRef.TFun(new TypeRef.TList(elem), acc)))));
        return (reduceFunc, acc);
    }

    /// <summary>
    /// Data-parallel map/reduce. Lowers the arguments once (against a reconstructed generic signature so
    /// element types propagate), then — when the concrete result type is one a worker's arena-isolated
    /// result can be safely lifted back from, so <c>both</c> will genuinely fork — routes to a monomorphic
    /// self-recursive specialization whose recursive halves split through <c>both</c>. Otherwise it lowers
    /// a plain sequential call to the polymorphic combinator (an identical result). Never falls through, so
    /// the arguments are lowered exactly once.
    /// </summary>
    private (int, TypeRef) TryLowerParallelSpecializedCall(string name, Expr.Lambda lambda, List<Expr> args)
    {
        var (funcType, resultType) = BuildParallelCombinatorType(name);
        (int[] argTemps, List<TypeRef> concreteParamTypes) = LowerTypedSpecializationArguments(args, funcType);

        resultType = Prune(resultType);

        // Only specialize when the worker's result can be lifted into the parent arena — otherwise `both`
        // would fall back to sequential inside the body anyway, so a call to the polymorphic combinator is
        // equivalent and avoids emitting a dead specialization.
        if (CanRunRightOnWorker(resultType))
        {
            string label = GetOrCreateParallelSpecialization(name, lambda, Prune(funcType), concreteParamTypes);
            // Invoke with a null env, exactly like a top-level empty-env function: the specialization
            // captures nothing, and its self-closure is rebuilt from this env pointer, which must not be a
            // live arena allocation that a forked worker could observe dangling after an arena reset.
            int envPtr = NewTemp();
            Emit(new IrInst.LoadConstInt(envPtr, 0));
            int closureTemp = NewTemp();
            Emit(new IrInst.MakeClosure(closureTemp, label, envPtr, 0));
            return ApplyLoweredArgs(closureTemp, argTemps, resultType);
        }

        var (combinatorTemp, _) = LowerVar(new Expr.Var(name));
        return ApplyLoweredArgs(combinatorTemp, argTemps, resultType);
    }

    private (int[] Temps, List<TypeRef> Types) LowerTypedSpecializationArguments(
        IReadOnlyList<Expr> arguments,
        TypeRef functionType)
    {
        int[] temps = new int[arguments.Count];
        TypeRef[] expectedTypes = new TypeRef[arguments.Count];
        TypeRef cursor = Prune(functionType);
        for (int index = 0; index < arguments.Count; index++)
        {
            TypeRef.TFun function = (TypeRef.TFun)Prune(cursor);
            expectedTypes[index] = function.Arg;
            cursor = function.Ret;
        }

        bool[] deferred = arguments.Select(argument =>
            argument is Expr.Lambda || IsTraitDictionaryFunctionValue(argument, out _)).ToArray();
        TypeRef[] actualTypes = new TypeRef[arguments.Count];
        LowerTypedSpecializationArgumentPass(arguments, expectedTypes, deferred, deferTraitValues: true, temps, actualTypes);
        LowerTypedSpecializationArgumentPass(arguments, expectedTypes, deferred, deferTraitValues: false, temps, actualTypes);
        return (temps, actualTypes.Select(type => Prune(type)).ToList());
    }

    private void LowerTypedSpecializationArgumentPass(
        IReadOnlyList<Expr> arguments,
        IReadOnlyList<TypeRef> expectedTypes,
        IReadOnlyList<bool> deferred,
        bool deferTraitValues,
        int[] temps,
        TypeRef[] actualTypes)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (deferred[index] == deferTraitValues)
            {
                continue;
            }

            (int temp, TypeRef type) = deferred[index]
                ? TryLowerTraitDictionaryFunctionValue(arguments[index], expectedTypes[index])
                    ?? LowerExpr(
                        arguments[index],
                        default(LoweredValueRequest).WithExpectedType(expectedTypes[index])).AsPair()
                : LowerExpr(
                    arguments[index],
                    default(LoweredValueRequest).WithExpectedType(expectedTypes[index])).AsPair();
            temps[index] = temp;
            actualTypes[index] = type;
            Unify(expectedTypes[index], type);
        }
    }

    // Applies a sequence of already-lowered argument temps to a callable closure temp, returning the
    // final result temp paired with the supplied result type.
    private (int, TypeRef) ApplyLoweredArgs(int closureTemp, IReadOnlyList<int> argTemps, TypeRef resultType)
    {
        int current = closureTemp;
        foreach (var argTemp in argTemps)
        {
            int next = NewTemp();
            Emit(new IrInst.CallClosure(next, current, argTemp));
            current = next;
        }

        return (current, resultType);
    }

    /// <summary>
    /// Work-conserving lowering of a saturated <c>Parallel.reduce(combine)(identity)(f)(xs)</c> call.
    /// Lowers the arguments once (against the combinator's reconstructed generic signature so element
    /// types propagate), then — when the concrete result type is one a worker's arena-isolated result
    /// can be deep-copied back from — emits the runtime chunk queue: workers pull element indexes from
    /// a shared atomic counter, publish <c>f(element)</c> per index, and merge the results pairwise
    /// through <c>combine</c> in the fixed shape determined by the element count (deterministic no
    /// matter which worker computed what; equivalent to the fork-tree shape under reduce's
    /// associative-combine contract). This caller awaits only the merge root and deep-copies it out.
    /// An empty list yields <c>identity</c>; a singleton yields <c>f(x)</c> alone, exactly like the
    /// sequential combinator. Otherwise falls back to a plain sequential call to the polymorphic
    /// combinator (an identical result). Never falls through, so the arguments are lowered exactly
    /// once.
    /// </summary>
    private (int, TypeRef) LowerParallelReduceQueued(List<Expr> args)
    {
        // reduce : (c -> c -> c) -> c -> (e -> c) -> List e -> c
        var acc = NewTypeVar();
        var elem = NewTypeVar();
        var combineType = new TypeRef.TFun(acc, new TypeRef.TFun(acc, acc));
        var mapFnType = new TypeRef.TFun(elem, acc);
        TypeRef curType = new TypeRef.TFun(combineType, new TypeRef.TFun(acc, new TypeRef.TFun(mapFnType, new TypeRef.TFun(new TypeRef.TList(elem), acc))));

        var savedTailPos = _tcoCtx?.InTailPosition ?? false;
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = false;

        (int[] argTemps, _) = LowerTypedSpecializationArguments(args, curType);

        var resultType = Prune(acc);

        // The workers' raw results live in worker arenas, so the merge must deep-copy each one at
        // the concrete result type — unavailable for an abstract result, which (like `both`) degrades
        // to the sequential polymorphic combinator.
        if (!CanRunRightOnWorker(resultType))
        {
            if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
            var (combinatorTemp, _) = LowerVar(new Expr.Var(ParallelReduceName));
            return ApplyLoweredArgs(combinatorTemp, argTemps, resultType);
        }

        int resultTemp = LowerParallelReduceQueuedEmit(argTemps, resultType);
        if (_tcoCtx is not null) _tcoCtx.InTailPosition = savedTailPos;
        return (resultTemp, resultType);
    }

    // Emits the runtime chunk queue for an already-lowered, worker-liftable reduce: start the queue,
    // await the merge root on the non-empty path (deep-copying it into the parent arena), take the
    // identity on the empty path, and clean up the descriptor.
    private int LowerParallelReduceQueuedEmit(int[] argTemps, TypeRef resultType)
    {
        int combineTemp = argTemps[0];
        int identityTemp = argTemps[1];
        int fTemp = argTemps[2];
        int listTemp = argTemps[3];

        int descTemp = NewTemp();
        Emit(new IrInst.ParallelQueueStart(descTemp, fTemp, combineTemp, listTemp));
        // Element count, published by the queue-start runtime at descriptor offset 8.
        int countTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(countTemp, descTemp, 8));

        int accSlot = NewLocal();
        string emptyLabel = NewLabel("parq_empty");
        string doneLabel = NewLabel("parq_done");

        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int nonEmptyTemp = NewTemp();
        Emit(new IrInst.CmpIntGt(nonEmptyTemp, countTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(nonEmptyTemp, emptyLabel));

        // Non-empty: the workers fold and pairwise-merge everything; await the merge root.
        int rootRawTemp = NewTemp();
        Emit(new IrInst.ParallelQueueAwait(rootRawTemp, descTemp));
        int rootTemp = EmitDeepCopy(rootRawTemp, resultType);
        Emit(new IrInst.StoreLocal(accSlot, rootTemp));
        Emit(new IrInst.Jump(doneLabel));

        Emit(new IrInst.Label(emptyLabel));
        Emit(new IrInst.StoreLocal(accSlot, identityTemp));

        Emit(new IrInst.Label(doneLabel));
        Emit(new IrInst.ParallelQueueCleanup(descTemp));
        int resultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(resultTemp, accSlot));
        return resultTemp;
    }

    /// <summary>
    /// Generates (once per concrete instantiation) a monomorphic self-recursive specialization of a
    /// parallel combinator. The body is the combinator's own source, lowered with its parameters pinned to
    /// the call's concrete types, so the recursive <c>both</c> splits see a concrete result type and fork.
    /// Recursive self-calls resolve to this label (Binding.Self); stitched list helpers resolve by-label
    /// from the captured top-level scope (the <c>_inSpecialization</c> path). Not a linear-reuse spec.
    /// </summary>
    private string GetOrCreateParallelSpecialization(string name, Expr.Lambda lambda, TypeRef funcType, IReadOnlyList<TypeRef> concreteParamTypes)
    {
        string cacheKey = name + "|" + string.Join(",", concreteParamTypes.Select(t => Pretty(Prune(t))));
        if (_parallelSpecializations.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        string label = _parallelSpecializations.Count == 0 ? $"{name}__par" : $"{name}__par${_parallelSpecializations.Count}";
        _parallelSpecializations[cacheKey] = label;

        // Generate the body in an isolated scope: its only free names are the module's top-level list
        // helpers (resolved by-label as static globals via _topLevelFunctionRefs — never captured, so no
        // arena closure crosses a fork), the self reference (Binding.Self on this label), and the qualified
        // `both` primitive (module-resolved, scope-independent). Emptying the scope means the helpers are
        // NOT found by Lookup and fall through to by-label resolution; a captured helper would otherwise
        // become an arena closure a worker thread could read while the parent resets its arena — a race.
        // The bumped lambda depth keeps LowerLambdaCore from treating this as a top-level declaration and
        // snapshotting the emptied scope over the real one. Pin the concrete parameter types so the body
        // monomorphizes and `both` sees a concrete result.
        var savedInParSpec = _inParallelSpecialization;
        var savedConcrete = _specializationConcreteParamTypes;
        var savedCursor = _specializationParamCursor;
        var savedScopes = _scopes.ToArray();
        var savedLambdaDepth = _lambdaDepth;
        _inParallelSpecialization = true;
        _specializationConcreteParamTypes = concreteParamTypes;
        _specializationParamCursor = 0;
        _scopes.Clear();
        _scopes.Push(System.Collections.Immutable.ImmutableSortedDictionary.Create<string, Binding>(StringComparer.Ordinal));
        _lambdaDepth = savedLambdaDepth == 0 ? 1 : savedLambdaDepth;

        int instBefore = _inst.Count;
        Dictionary<int, LoweredTempOwnershipFact> savedTempOwnershipFacts =
            SnapshotTempOwnershipFacts();
        LowerParallelSpecializationLambda(lambda, name, funcType, label, cacheKey);
        if (_inst.Count > instBefore)
        {
            _inst.RemoveRange(instBefore, _inst.Count - instBefore);
        }
        RestoreTempOwnershipFacts(savedTempOwnershipFacts);

        _inParallelSpecialization = savedInParSpec;
        _specializationConcreteParamTypes = savedConcrete;
        _specializationParamCursor = savedCursor;
        _lambdaDepth = savedLambdaDepth;
        _scopes.Clear();
        for (int i = savedScopes.Length - 1; i >= 0; i--)
        {
            _scopes.Push(savedScopes[i]);
        }

        return label;
    }

    /// <summary>
    /// Marks/unmarks an inlinable-function name as shadowed by a more-local binding so a call to it
    /// is not mistaken for the top-level helper. No-op unless the name is actually a registered
    /// inlinable function. Returns whether a mark was added (so the caller can balance the unmark).
    /// </summary>
    private bool PushInlinableShadow(string name)
    {
        if (!_inlinableFunctions.ContainsKey(name))
        {
            return false;
        }

        _shadowedInlinables[name] = _shadowedInlinables.GetValueOrDefault(name) + 1;
        return true;
    }

    private void PopInlinableShadow(string name)
    {
        int remaining = _shadowedInlinables.GetValueOrDefault(name) - 1;
        if (remaining <= 0)
        {
            _shadowedInlinables.Remove(name);
        }
        else
        {
            _shadowedInlinables[name] = remaining;
        }
    }

    private static bool ExprReferencesName(Expr expr, string targetName, bool shadowed = false)
    {
        switch (expr)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.BigIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
            case Expr.QualifiedVar:
                return false;

            case Expr.Var v:
                return !shadowed && string.Equals(v.Name, targetName, StringComparison.Ordinal);

            case Expr.Add add:
                return ExprReferencesName(add.Left, targetName, shadowed) || ExprReferencesName(add.Right, targetName, shadowed);
            case Expr.Subtract sub:
                return ExprReferencesName(sub.Left, targetName, shadowed) || ExprReferencesName(sub.Right, targetName, shadowed);
            case Expr.Multiply mul:
                return ExprReferencesName(mul.Left, targetName, shadowed) || ExprReferencesName(mul.Right, targetName, shadowed);
            case Expr.Divide div:
                return ExprReferencesName(div.Left, targetName, shadowed) || ExprReferencesName(div.Right, targetName, shadowed);
            case Expr.Modulo modExpr:
                return ExprReferencesName(modExpr.Left, targetName, shadowed) || ExprReferencesName(modExpr.Right, targetName, shadowed);
            case Expr.BitwiseAnd bitAnd:
                return ExprReferencesName(bitAnd.Left, targetName, shadowed) || ExprReferencesName(bitAnd.Right, targetName, shadowed);
            case Expr.BitwiseOr bitOr:
                return ExprReferencesName(bitOr.Left, targetName, shadowed) || ExprReferencesName(bitOr.Right, targetName, shadowed);
            case Expr.BitwiseXor bitXor:
                return ExprReferencesName(bitXor.Left, targetName, shadowed) || ExprReferencesName(bitXor.Right, targetName, shadowed);
            case Expr.ShiftLeft shiftLeft:
                return ExprReferencesName(shiftLeft.Left, targetName, shadowed) || ExprReferencesName(shiftLeft.Right, targetName, shadowed);
            case Expr.ShiftRight shiftRight:
                return ExprReferencesName(shiftRight.Left, targetName, shadowed) || ExprReferencesName(shiftRight.Right, targetName, shadowed);
            case Expr.BitwiseNot bitwiseNot:
                return ExprReferencesName(bitwiseNot.Operand, targetName, shadowed);
            case Expr.LogicalNot logicalNot:
                return ExprReferencesName(logicalNot.Operand, targetName, shadowed);
            case Expr.LogicalAnd logicalAnd:
                return ExprReferencesName(logicalAnd.Left, targetName, shadowed) || ExprReferencesName(logicalAnd.Right, targetName, shadowed);
            case Expr.LogicalOr logicalOr:
                return ExprReferencesName(logicalOr.Left, targetName, shadowed) || ExprReferencesName(logicalOr.Right, targetName, shadowed);
            case Expr.GreaterThan gt:
                return ExprReferencesName(gt.Left, targetName, shadowed) || ExprReferencesName(gt.Right, targetName, shadowed);
            case Expr.GreaterOrEqual ge:
                return ExprReferencesName(ge.Left, targetName, shadowed) || ExprReferencesName(ge.Right, targetName, shadowed);
            case Expr.LessThan lt:
                return ExprReferencesName(lt.Left, targetName, shadowed) || ExprReferencesName(lt.Right, targetName, shadowed);
            case Expr.LessOrEqual le:
                return ExprReferencesName(le.Left, targetName, shadowed) || ExprReferencesName(le.Right, targetName, shadowed);
            case Expr.Equal eq:
                return ExprReferencesName(eq.Left, targetName, shadowed) || ExprReferencesName(eq.Right, targetName, shadowed);
            case Expr.NotEqual ne:
                return ExprReferencesName(ne.Left, targetName, shadowed) || ExprReferencesName(ne.Right, targetName, shadowed);
            default:
                return ExprReferencesNameStructured(expr, targetName, shadowed);
        }
    }

    // Continuation of ExprReferencesName for the non-operator expression shapes.
    private static bool ExprReferencesNameStructured(Expr expr, string targetName, bool shadowed)
    {
        switch (expr)
        {
            case Expr.ResultPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.ResultMapErrorPipe pipe:
                return ExprReferencesName(pipe.Left, targetName, shadowed) || ExprReferencesName(pipe.Right, targetName, shadowed);
            case Expr.Call call:
                return ExprReferencesName(call.Func, targetName, shadowed) || ExprReferencesName(call.Arg, targetName, shadowed);
            case Expr.TupleLit tuple:
                return tuple.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.ListLit list:
                return list.Elements.Any(elem => ExprReferencesName(elem, targetName, shadowed));
            case Expr.Cons cons:
                return ExprReferencesName(cons.Head, targetName, shadowed) || ExprReferencesName(cons.Tail, targetName, shadowed);
            case Expr.If iff:
                return ExprReferencesName(iff.Cond, targetName, shadowed)
                    || ExprReferencesName(iff.Then, targetName, shadowed)
                    || ExprReferencesName(iff.Else, targetName, shadowed);
            case Expr.Let let:
                return ExprReferencesName(let.Value, targetName, shadowed)
                    || ExprReferencesName(let.Body, targetName, shadowed || string.Equals(let.Name, targetName, StringComparison.Ordinal));
            case Expr.LetResult letResult:
                return ExprReferencesName(letResult.Value, targetName, shadowed)
                    || ExprReferencesName(letResult.Body, targetName, shadowed || string.Equals(letResult.Name, targetName, StringComparison.Ordinal));
            case Expr.LetRecursive letRecursive:
                {
                    bool nextShadowed = shadowed || string.Equals(letRecursive.Name, targetName, StringComparison.Ordinal);
                    return ExprReferencesName(letRecursive.Value, targetName, nextShadowed)
                        || ExprReferencesName(letRecursive.Body, targetName, nextShadowed);
                }
            case Expr.Lambda lam:
                return ExprReferencesName(lam.Body, targetName, shadowed || string.Equals(lam.ParamName, targetName, StringComparison.Ordinal));
            case Expr.Match match:
                return ExprReferencesNameMatch(match, targetName, shadowed);
            case Expr.Await awaitExpr:
                return ExprReferencesName(awaitExpr.Task, targetName, shadowed);
            case Expr.Perform perform:
                return ExprReferencesName(perform.Operation, targetName, shadowed);
            case Expr.Handle handleExpr:
                return ExprReferencesNameHandle(handleExpr, targetName, shadowed);
            case Expr.RecordLit rl:
                return rl.Fields.Any(f => ExprReferencesName(f.Value, targetName, shadowed));
            case Expr.RecordUpdate ru:
                return ExprReferencesName(ru.Target, targetName, shadowed)
                    || ru.Updates.Any(u => ExprReferencesName(u.Value, targetName, shadowed));
            default:
                throw new NotSupportedException(expr.GetType().Name);
        }
    }

    private static bool ExprReferencesNameMatch(Expr.Match match, string targetName, bool shadowed)
    {
        if (ExprReferencesName(match.Value, targetName, shadowed))
        {
            return true;
        }

        foreach (var matchCase in match.Cases)
        {
            bool caseShadowed = shadowed || PatternBindings(matchCase.Pattern).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal));
            if ((matchCase.Guard is not null && ExprReferencesName(matchCase.Guard, targetName, caseShadowed))
                || ExprReferencesName(matchCase.Body, targetName, caseShadowed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExprReferencesNameHandle(Expr.Handle handleExpr, string targetName, bool shadowed)
    {
        if (ExprReferencesName(handleExpr.Body, targetName, shadowed))
        {
            return true;
        }

        foreach (var arm in handleExpr.Arms)
        {
            bool armShadowed = shadowed
                || string.Equals("resume", targetName, StringComparison.Ordinal)
                || arm.Parameters.Any(p => PatternBindings(p).Any(boundName => string.Equals(boundName, targetName, StringComparison.Ordinal)));
            if (ExprReferencesName(arm.Body, targetName, armShadowed))
            {
                return true;
            }
        }

        return false;
    }
}
