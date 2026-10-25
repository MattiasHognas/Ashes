namespace Ashes.Semantics;

internal enum TcoPromotionVerdict
{
    Profitable,
    NotProfitable,
}

internal sealed record TcoParamPromotionProfitability(
    string ParamName,
    TcoPromotionVerdict Verdict,
    string Reason);

internal sealed record TcoParamPlacementTrace(
    TcoParamPlacementDecision Current,
    IReadOnlyList<TcoParamPlacementDecision> History);

internal sealed record TcoFunctionPlacementSnapshot(
    string FunctionName,
    string FunctionLabel,
    IReadOnlyList<TcoParamPlacementTrace> Parameters);

internal sealed record TcoPlacementInputs(
    TcoRcEligibility[] Eligibility,
    bool[] RequestedRuntime,
    TcoPlacementReason[] Reasons,
    bool[] DynamicRestricted);

public sealed partial class Lowering
{
    private readonly List<TcoFunctionPlacementSnapshot> _tcoPlacementSnapshots = [];

    /// <summary>
    /// Returns immutable placement traces in parameter-ordinal order. This name-based helper is test
    /// compatibility only; the snapshot itself retains the generated function label.
    /// </summary>
    internal IReadOnlyList<TcoParamPlacementTrace>? GetTcoPlacementDecisions(string functionName)
        => _tcoPlacementSnapshots
            .LastOrDefault(snapshot =>
                string.Equals(snapshot.FunctionName, functionName, StringComparison.Ordinal))
            ?.Parameters;

    internal IReadOnlyList<TcoFunctionPlacementSnapshot> GetTcoPlacementSnapshots()
        => _tcoPlacementSnapshots.ToArray();

    /// <summary>
    /// Compatibility projection for the original promotion-cost tests. Placement snapshots are the
    /// authority; this projection never participates in lowering.
    /// </summary>
    internal IReadOnlyDictionary<string, TcoParamPromotionProfitability>? GetTcoPromotionProfitability(
        string functionName)
    {
        IReadOnlyList<TcoParamPlacementTrace>? traces = GetTcoPlacementDecisions(functionName);
        if (traces is null)
        {
            return null;
        }

        var verdicts = new Dictionary<string, TcoParamPromotionProfitability>(StringComparer.Ordinal);
        foreach (TcoParamPlacementTrace trace in traces)
        {
            if (trace.Current.Profitability is not { } verdict)
            {
                continue;
            }

            verdicts[trace.Current.ParameterName] = new TcoParamPromotionProfitability(
                trace.Current.ParameterName,
                verdict,
                verdict == TcoPromotionVerdict.Profitable
                    ? "no sibling parameter permanently blocks this frame's per-iteration reclaim"
                    : "a sibling parameter permanently fails every reset/RC exemption");
        }

        return verdicts.Count == 0 ? null : verdicts;
    }

    private void RecordTcoPlacementSnapshot(TcoContext tco)
    {
        var traces = new List<TcoParamPlacementTrace>();
        for (int ordinal = 0; ordinal < tco.ParamSlots.Count; ordinal++)
        {
            int slot = tco.ParamSlots[ordinal];
            if (!tco.ParamPlacements.TryGetValue(slot, out TcoParamPlacementState? placement)
                || placement.Current is not { } current)
            {
                continue;
            }

            traces.Add(new TcoParamPlacementTrace(current, placement.History.ToArray()));
        }

        _tcoPlacementSnapshots.Add(new TcoFunctionPlacementSnapshot(
            tco.SelfName,
            tco.SelfLabel,
            traces.ToArray()));
    }

    private TypeRef?[] ResolveTcoParamTypes(
        IReadOnlyDictionary<string, Binding> scope,
        TcoContext tco)
    {
        var parameterTypes = new TypeRef?[tco.ParamSlots.Count];
        for (int ordinal = 0; ordinal < parameterTypes.Length; ordinal++)
        {
            int slot = tco.ParamSlots[ordinal];
            Binding.Local? liveBinding = scope.Values
                .OfType<Binding.Local>()
                .FirstOrDefault(binding => binding.Slot == slot);
            parameterTypes[ordinal] = liveBinding is not null
                ? Prune(liveBinding.T)
                : tco.ParamTypes.TryGetValue(ordinal, out TypeRef? type)
                    ? Prune(type)
                    : null;
        }

        return parameterTypes;
    }

    private void EvaluateTcoParamPlacements(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        TcoPlacementResolutionPoint resolutionPoint,
        bool includeFreshClosures,
        bool applyReuseRestrictions)
    {
        TcoPlacementInputs inputs = BuildTcoPlacementInputs(
            tco,
            parameterTypes,
            includeFreshClosures,
            applyReuseRestrictions);
        for (int ordinal = 0; ordinal < inputs.Eligibility.Length; ordinal++)
        {
            ApplyTcoPlacementDecision(
                tco,
                parameterTypes,
                inputs,
                ordinal,
                resolutionPoint);
        }
    }

    private TcoPlacementInputs BuildTcoPlacementInputs(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        bool includeFreshClosures,
        bool applyReuseRestrictions)
    {
        int count = Math.Min(tco.ParamSlots.Count, parameterTypes.Length);
        var eligibility = new TcoRcEligibility[count];
        var requestedRuntime = new bool[count];
        var reasons = new TcoPlacementReason[count];
        var dynamicRestricted = new bool[count];

        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            int slot = tco.ParamSlots[ordinal];
            TcoParamStaticFacts facts = tco.ParamFacts[slot];
            TypeRef? type = parameterTypes[ordinal] is { } candidate ? Prune(candidate) : null;
            eligibility[ordinal] = EvaluateTcoRcEligibility(
                tco,
                facts,
                type,
                includeFreshClosures);

            TcoPlacementReason restriction = GetTcoPlacementRestriction(
                tco,
                facts,
                applyReuseRestrictions);
            reasons[ordinal] = restriction;
            dynamicRestricted[ordinal] = restriction is
                TcoPlacementReason.AsyncBoundary or
                TcoPlacementReason.CoroutineBoundary or
                TcoPlacementReason.DynamicCapabilityBoundary;

            if (!facts.HasVisibleBinding)
            {
                // Positional facts for a shadowed curried parameter are retained for diagnostics,
                // but its synthetic arity slot is never a live loop binding.
                reasons[ordinal] = TcoPlacementReason.ShadowedBinding;
                requestedRuntime[ordinal] = false;
                continue;
            }

            bool alreadyRuntime = tco.IsRuntimeManagedSlot(slot);
            bool newlyEligible = restriction == TcoPlacementReason.Eligible
                && eligibility[ordinal].OwnershipShapeEligible
                && eligibility[ordinal].ResolvedLayoutEligible;
            // Eligibility was historically monotone. A later incomplete view cannot demote a
            // parameter; only the explicit profitability decision below can.
            requestedRuntime[ordinal] = alreadyRuntime || newlyEligible;
            if (!newlyEligible && alreadyRuntime)
            {
                reasons[ordinal] = TcoPlacementReason.EarlierPlacementRetained;
            }
        }

        return new TcoPlacementInputs(
            eligibility,
            requestedRuntime,
            reasons,
            dynamicRestricted);
    }

    private void ApplyTcoPlacementDecision(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        TcoPlacementInputs inputs,
        int ordinal,
        TcoPlacementResolutionPoint resolutionPoint)
    {
        int slot = tco.ParamSlots[ordinal];
        TcoParamStaticFacts facts = tco.ParamFacts[slot];
        TcoParamPlacementState placement = tco.ParamPlacements[slot];
        bool wasRuntime =
            placement.Current?.Representation == TcoPlacementRepresentation.RuntimeRc;
        bool runtime = inputs.RequestedRuntime[ordinal];
        (TcoPromotionVerdict? profitability, int? decisiveBlocker) =
            EvaluateTcoPlacementProfitability(
                tco,
                parameterTypes,
                inputs.RequestedRuntime,
                ordinal,
                runtime);
        runtime = runtime && decisiveBlocker is null;

        TcoPlacementReason finalReason = GetFinalTcoPlacementReason(
            inputs,
            ordinal,
            decisiveBlocker);
        TcoPlacementTransitionKind transition = GetTcoPlacementTransition(
            placement,
            wasRuntime,
            runtime,
            finalReason);
        TcoPlacementResolutionPoint? firstPromotedAt =
            placement.Current?.FirstPromotedAt;
        if (runtime && firstPromotedAt is null)
        {
            firstPromotedAt = resolutionPoint;
        }

        TypeRef? type = parameterTypes[ordinal] is { } resolved ? Prune(resolved) : null;
        TypeRef? acceptedType = type is null or TypeRef.TVar ? null : type;
        string? resolvedType = acceptedType is null
            ? placement.Current?.ResolvedType
            : Pretty(acceptedType);
        var decision = new TcoParamPlacementDecision(
            facts.ParameterOrdinal,
            facts.ParamName,
            facts.Slot,
            resolutionPoint,
            runtime ? TcoPlacementRepresentation.RuntimeRc : TcoPlacementRepresentation.Arena,
            finalReason,
            inputs.Eligibility[ordinal],
            resolvedType,
            inputs.DynamicRestricted[ordinal],
            profitability,
            decisiveBlocker,
            transition,
            firstPromotedAt);
        tco.ApplyPlacementDecision(slot, decision, runtime ? acceptedType : null);
        if (!runtime && wasRuntime)
        {
            ClearTcoRuntimeManagedActivity(tco, slot);
        }
    }

    private (TcoPromotionVerdict? Verdict, int? BlockingSibling) EvaluateTcoPlacementProfitability(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        bool[] requestedRuntime,
        int ordinal,
        bool runtime)
    {
        if (!runtime)
        {
            return (null, null);
        }

        int? blocker = FindBlockingSiblingForCandidate(
            tco,
            parameterTypes,
            requestedRuntime,
            ordinal);
        return (blocker is null
            ? TcoPromotionVerdict.Profitable
            : TcoPromotionVerdict.NotProfitable, blocker);
    }

    private static void ClearTcoRuntimeManagedActivity(TcoContext tco, int slot)
    {
        tco.RuntimeManagedParamActiveSlots.Remove(slot);
        tco.RuntimeManagedClosureActiveSlots.Remove(slot);
        tco.RuntimeManagedClosureSlotsNeedingEntryInitialization.Remove(slot);
    }

    private static TcoPlacementReason GetFinalTcoPlacementReason(
        TcoPlacementInputs inputs,
        int ordinal,
        int? decisiveBlocker)
    {
        if (decisiveBlocker is not null)
        {
            return TcoPlacementReason.BlockingSiblingNotProfitable;
        }

        TcoPlacementReason reason = inputs.Reasons[ordinal];
        return reason == TcoPlacementReason.Eligible
            && (!inputs.Eligibility[ordinal].OwnershipShapeEligible
                || !inputs.Eligibility[ordinal].ResolvedLayoutEligible)
            ? TcoPlacementReason.NotEligible
            : reason;
    }

    private TcoPlacementReason GetTcoPlacementRestriction(
        TcoContext tco,
        TcoParamStaticFacts facts,
        bool applyReuseRestrictions)
    {
        if (!AllowsAsyncIndependentRcPlacement)
        {
            return TcoPlacementReason.AsyncBoundary;
        }
        if (_inCoroutineBody)
        {
            return TcoPlacementReason.CoroutineBoundary;
        }
        if (!AllowsOrdinaryRcPlacement)
        {
            return TcoPlacementReason.DynamicCapabilityBoundary;
        }
        if (!applyReuseRestrictions)
        {
            return TcoPlacementReason.Eligible;
        }
        if (_linearReuseNames.Contains(facts.ParamName))
        {
            return TcoPlacementReason.ReuseAccumulator;
        }
        if (_linearSpecializationAccumulators.Contains(facts.ParamName))
        {
            return TcoPlacementReason.SpecializationReuseAccumulator;
        }
        if (_resetSafeAccumulators.Contains(facts.ParamName))
        {
            return TcoPlacementReason.StableReuseAccumulator;
        }

        return TcoPlacementReason.Eligible;
    }

    private TcoRcEligibility EvaluateTcoRcEligibility(
        TcoContext tco,
        TcoParamStaticFacts facts,
        TypeRef? parameterType,
        bool includeFreshClosures)
    {
        if (parameterType is null or TypeRef.TVar)
        {
            return new TcoRcEligibility(
                OwnershipShapeEligible: false,
                ResolvedLayoutEligible: false,
                TcoRuntimeManagedKind.None,
                TcoRcEligibilityReason.UnresolvedType);
        }
        if (IsRcEligibleScalarTupleOrAdtType(parameterType)
            && !IsGeneralRcSpecializationAccumulator(tco, facts, parameterType)
            && !IsUnplaceableGeneralRcVariantParameter(tco, facts, parameterType))
        {
            return new TcoRcEligibility(
                OwnershipShapeEligible: true,
                ResolvedLayoutEligible: true,
                TcoRuntimeManagedKind.Ordinary,
                TcoRcEligibilityReason.ScalarTupleOrAdtLayout);
        }

        if (parameterType is TypeRef.TList list)
        {
            return EvaluateTcoListRcEligibility(tco, facts, list);
        }

        if (parameterType is TypeRef.TFun)
        {
            TcoRcEligibilityReason reason = facts.FreshClosureRebuild
                ? includeFreshClosures
                    ? TcoRcEligibilityReason.FreshClosureRebuild
                    : TcoRcEligibilityReason.LateClosureDeferred
                : TcoRcEligibilityReason.FreshClosureNotProven;
            return new TcoRcEligibility(
                facts.FreshClosureRebuild && includeFreshClosures,
                ResolvedLayoutEligible: true,
                TcoRuntimeManagedKind.Closure,
                reason);
        }

        return new TcoRcEligibility(
            OwnershipShapeEligible: false,
            ResolvedLayoutEligible: false,
            TcoRuntimeManagedKind.None,
            TcoRcEligibilityReason.UnsupportedLayout);
    }

    private TcoRcEligibility EvaluateTcoListRcEligibility(TcoContext tco, TcoParamStaticFacts facts, TypeRef.TList list)
    {
        bool generalAccumulator = IsGeneralRcAccumulatorList(tco, facts, list) || IsWalkedOrRefilledList(tco, facts, list);
        bool layoutEligible = (CanRuntimeManageTcoListElement(list.Element)
                || (IsGeneralRcTcoListElement(list.Element) && facts.AffineConsList && !facts.ConsumedListTail)
                || generalAccumulator)
            && IsBytesProvenanceEligibleTcoListElement(facts, list.Element);
        bool consumedTail = facts.ConsumedListTail
            && !CanArenaReset(Prune(list.Element))
            && !IsBorrowableInspectOnlyList(tco, facts.ParameterOrdinal, list);
        bool freshRebuiltList = facts.FreshRebuiltList || IsHandedOnFreshScalarList(tco, facts, list);
        TcoRcEligibilityReason reason = freshRebuiltList
            ? TcoRcEligibilityReason.FreshListRebuild
            : facts.AffineConsList
                ? TcoRcEligibilityReason.AffineConsList
                : consumedTail
                    ? TcoRcEligibilityReason.ConsumedListTail
                    : layoutEligible
                        ? TcoRcEligibilityReason.MissingListOwnershipShape
                        : TcoRcEligibilityReason.UnsupportedListElementLayout;
        return new TcoRcEligibility(
            freshRebuiltList || facts.AffineConsList || consumedTail || generalAccumulator,
            layoutEligible,
            TcoRuntimeManagedKind.List,
            reason);
    }

    // A list of the contract's values that the loop rebuilds with a different shape at different
    // self-calls (a call's owned result in one arm, the parameter itself in another) is an
    // accumulator all the same. Its successor is normalized at the back edge like any other list of
    // these values, and only a parameter placed on the reference-counted heap releases its
    // predecessor and lets the iteration release the owned value the successor was built from. The
    // shape analysis must have classified every self-call at this position: a tail-modulo-cons
    // function's self-call sits inside a cons and is never classified, so it is excluded. A list of
    // any other heap element is one when a self-call passes a choice between the parameter and a
    // cons onto it: its cells are built for a successor that is copied at the back edge, and left
    // in the arena it would keep the consed heads past the owners the back edge releases.
    private bool IsGeneralRcAccumulatorList(TcoContext tco, TcoParamStaticFacts facts, TypeRef.TList list)
        => GeneralRcEnabled
            && Environment.GetEnvironmentVariable("GRC_NO_MIXEDLIST") is null
            && !tco.TmcShapePresent
            && !facts.ConsumedListTail
            && !facts.LoopInvariant
            && ((IsCallResultAccumulatorElement(list.Element)
                    && GetTcoParameterOrdinals(tco.OwnershipFunction, static structural => structural.AccumulatorRebuild)
                        .Contains(facts.ParameterOrdinal))
                || (IsChoiceAccumulatorElement(list.Element)
                    && GetTcoParameterOrdinals(tco.OwnershipFunction, static structural => structural.ChoiceAccumulatorRebuild)
                        .Contains(facts.ParameterOrdinal)));

    // A list the loop walks by its consumed tail and refills with a call's result (a worklist's
    // front queue refilled from its back queue). Left in the arena, a refill borrows the heads of
    // whatever value the call was given, often a part of an earlier iteration's owned result, and
    // that result could then never be released. Placed on the reference-counted heap, the refill
    // is taken or copied at the back edge and the walk retains the tail it moves on to. Only a list
    // of strings or of lists of strings (a worklist of paths): a head of either matched out of the
    // list is protected wherever it escapes, where a head of any other heap type returned from the
    // loop would be released with the list.
    private bool IsWalkedOrRefilledList(TcoContext tco, TcoParamStaticFacts facts, TypeRef.TList list)
        => GeneralRcEnabled
            && Environment.GetEnvironmentVariable("GRC_NO_WALKREFILL") is null
            && !tco.TmcShapePresent
            && !facts.ConsumedListTail
            && !facts.LoopInvariant
            && IsWalkedOrRefilledElement(Prune(list.Element))
            && GetTcoParameterOrdinals(tco.OwnershipFunction, static structural => structural.WalkedOrRefilled)
                .Contains(facts.ParameterOrdinal);

    // A list of scalars the loop hands on at some self-calls and rebuilds fresh at the others (one
    // arm replaces it with a helper's result, another passes it through). Left in the arena, each
    // rebuild that comes back reference-counted, a tail-modulo-cons helper's result, becomes the
    // next iteration's parameter and is never released. Placed on the reference-counted heap it is
    // taken or copied at the back edge and its predecessor released. Only scalar elements: a
    // rebuild of a list whose elements hold heap values shares those values with the handed-on
    // list, and the back edge would release them with the predecessor.
    private bool IsHandedOnFreshScalarList(TcoContext tco, TcoParamStaticFacts facts, TypeRef.TList list)
        => Environment.GetEnvironmentVariable("GRC_NO_HANDEDONFRESHLIST") is null
            && !tco.TmcShapePresent
            && CanArenaReset(Prune(list.Element))
            && GetTcoParameterOrdinals(tco.OwnershipFunction, static structural => structural.FreshOrHandedOnListRebuild)
                .Contains(facts.ParameterOrdinal);

    private bool IsWalkedOrRefilledElement(TypeRef element)
        => element is TypeRef.TStr
            || (Environment.GetEnvironmentVariable("GRC_NO_WALKREFILLPATHS") is null
                && element is TypeRef.TList inner
                && Prune(inner.Element) is TypeRef.TStr);

    // An accumulator one self-call replaces with a call's result: a list of the contract's values,
    // or of any other heap element whose graph is bounded (no list). Left in the arena, the call's
    // result is built over values the iteration owns (an earlier helper's result), which then have
    // no owner left to release them. A list inside an element may be handed on to an arena sibling
    // parameter, which would keep it past the predecessor the back edge releases.
    private bool IsCallResultAccumulatorElement(TypeRef element)
        => IsGeneralRcValueType(element)
            || (Environment.GetEnvironmentVariable("GRC_NO_HEAPACCUMULATOR") is null
                && !CanArenaReset(Prune(element))
                && CanRuntimeManageTcoListElement(element)
                && HasBoundedCopy(element, []));

    private bool IsChoiceAccumulatorElement(TypeRef element)
        => Environment.GetEnvironmentVariable("GRC_NO_BRANCHACCUMULATOR") is null
            && (IsGeneralRcValueType(element)
                || (!CanArenaReset(Prune(element)) && CanRuntimeManageTcoListElement(element)));

    // Under the general contract the cells of a list holding Bytes are reference-counted whatever its
    // placement (a view is materialized or stays uncounted inside the cell), so a list kept in the
    // arena because one of its heads has no proven provenance would hold counted cells that nothing
    // releases: the loop's exit retains its result and leaves the parameter's list to the arena.
    private bool IsBytesProvenanceEligibleTcoListElement(
        TcoParamStaticFacts facts,
        TypeRef elementType)
    {
        return (GeneralRcEnabled && Environment.GetEnvironmentVariable("GRC_NO_UNGATEDBYTESLIST") is null)
            || !ContainsBytesLayout(elementType, new HashSet<TypeSymbol>())
            || facts.BytesProvenanceSafeListRebuild;
    }

    private int? FindBlockingSiblingForCandidate(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        bool[] requestedRuntime,
        int candidateOrdinal)
    {
        // Under the general contract reference-counted cells reach a list accumulator whatever its
        // placement: one kept in the arena for a sibling's sake holds them without releasing them, so
        // profitability no longer demotes a list, nor a value of the contract's own types threaded
        // through the loop (a state each iteration rebuilds): kept in the arena, its owned
        // successors are never released. A sibling that could hold the candidate itself (or a part
        // of it) still blocks: it would keep that part without a reference of its own.
        TypeRef? unblockedAccumulator = GeneralRcEnabled
            && candidateOrdinal < parameterTypes.Length
            && candidateOrdinal < tco.ParamSlots.Count
            && !tco.ParamFacts[tco.ParamSlots[candidateOrdinal]].ConsumedListTail
            && parameterTypes[candidateOrdinal] is { } candidateType
            && (Prune(candidateType) is TypeRef.TList { Element: var element } && IsUnblockedListAccumulatorElement(element)
                || (tco.SelfCallParameterFlow is not null && IsGeneralRcValueType(Prune(candidateType))))
                ? Prune(candidateType)
                : null;

        int count = Math.Min(parameterTypes.Length, tco.ParamSlots.Count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            if (ordinal != candidateOrdinal
                && IsPermanentlyBlockingTcoParam(
                    tco,
                    parameterTypes,
                    requestedRuntime,
                    ordinal)
                && (unblockedAccumulator is null
                    || SiblingMayReceiveAccumulator(tco, parameterTypes[ordinal]!, ordinal, candidateOrdinal, unblockedAccumulator)))
            {
                return ordinal;
            }
        }

        return null;
    }

    // A list accumulator a sibling blocks only when it may receive part of it. One of strings, or of
    // any element that owns heap parts, has its cells built on the reference-counted heap whatever its
    // placement. One of inline elements kept in the arena has the type of a list field of the contract
    // values the iteration owns, so the back edge could release none of them.
    private bool IsUnblockedListAccumulatorElement(TypeRef element)
        => Prune(element) is TypeRef.TStr
            || (Environment.GetEnvironmentVariable("GRC_NO_HEAPLISTUNBLOCK") is null && !CanArenaReset(Prune(element)))
            || (Environment.GetEnvironmentVariable("GRC_NO_INLINELISTUNBLOCK") is null && CanArenaReset(Prune(element)));

    // Whether a self-call may pass the accumulator, or a part of it, in the sibling's position: from
    // the body's parameter flow when it was followed, from the sibling's type otherwise.
    private bool SiblingMayReceiveAccumulator(
        TcoContext tco,
        TypeRef siblingType,
        int siblingOrdinal,
        int accumulatorOrdinal,
        TypeRef accumulatorType)
        => TypeMayContainArgumentType(siblingType, accumulatorType, new HashSet<string>(StringComparer.Ordinal), instantiated: false)
            && (AccumulatorFlowFor(tco, accumulatorType) is not { } flow
                || (flow.TryGetValue(siblingOrdinal, out HashSet<int>? sources) && sources.Contains(accumulatorOrdinal)));

    // A list of strings reaches a sibling only through its spine: a string head matched out of it
    // is retained wherever it is kept, so the sibling holding one keeps a reference of its own.
    private Dictionary<int, HashSet<int>>? AccumulatorFlowFor(TcoContext tco, TypeRef accumulatorType)
        => Environment.GetEnvironmentVariable("GRC_NO_SPINEFLOW") is null
            && accumulatorType is TypeRef.TList { Element: var element }
            && Prune(element) is TypeRef.TStr
            && tco.SelfCallSpineFlow is { } spine
                ? spine
                : tco.SelfCallParameterFlow;

    private bool IsPermanentlyBlockingTcoParam(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        bool[] requestedRuntime,
        int ordinal)
    {
        if (ordinal >= tco.ParamSlots.Count
            || parameterTypes[ordinal] is not { } candidate)
        {
            return false;
        }

        int slot = tco.ParamSlots[ordinal];
        TcoParamStaticFacts facts = tco.ParamFacts[slot];
        TypeRef type = Prune(candidate);
        return facts.HasVisibleBinding
            && type is not TypeRef.TFun
            && !CanArenaReset(type)
            && !IsResourceHandleType(type)
            && !facts.LoopInvariant
            && !(type is TypeRef.TList list
                && facts.ConsumedListTail
                && (CanArenaReset(Prune(list.Element))
                    || IsBorrowableInspectOnlyList(tco, ordinal, list)))
            && !requestedRuntime[ordinal];
    }

    private static TcoPlacementTransitionKind GetTcoPlacementTransition(
        TcoParamPlacementState placement,
        bool wasRuntime,
        bool isRuntime,
        TcoPlacementReason reason)
    {
        if (placement.Current is null)
        {
            return TcoPlacementTransitionKind.Initial;
        }
        if (!wasRuntime && isRuntime)
        {
            return placement.History.Any(decision =>
                decision.Representation == TcoPlacementRepresentation.RuntimeRc)
                ? TcoPlacementTransitionKind.RePromotedAfterResolution
                : TcoPlacementTransitionKind.PromotedAfterResolution;
        }
        if (wasRuntime && !isRuntime
            && reason == TcoPlacementReason.BlockingSiblingNotProfitable)
        {
            return TcoPlacementTransitionKind.DemotedByFrameProfitability;
        }

        return TcoPlacementTransitionKind.Retained;
    }
}
