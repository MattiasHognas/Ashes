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
        if (IsRcEligibleScalarTupleOrAdtType(parameterType))
        {
            return new TcoRcEligibility(
                OwnershipShapeEligible: true,
                ResolvedLayoutEligible: true,
                TcoRuntimeManagedKind.Ordinary,
                TcoRcEligibilityReason.ScalarTupleOrAdtLayout);
        }

        if (parameterType is TypeRef.TList list)
        {
            bool layoutEligible = CanRuntimeManageTcoListElement(list.Element) &&
                IsBytesProvenanceEligibleTcoListElement(facts, list.Element);
            bool consumedTail = facts.ConsumedListTail
                && !CanArenaReset(Prune(list.Element))
                && !IsBorrowableInspectOnlyList(tco, facts.ParameterOrdinal, list);
            TcoRcEligibilityReason reason = facts.FreshRebuiltList
                ? TcoRcEligibilityReason.FreshListRebuild
                : facts.AffineConsList
                    ? TcoRcEligibilityReason.AffineConsList
                    : consumedTail
                        ? TcoRcEligibilityReason.ConsumedListTail
                        : layoutEligible
                            ? TcoRcEligibilityReason.MissingListOwnershipShape
                            : TcoRcEligibilityReason.UnsupportedListElementLayout;
            return new TcoRcEligibility(
                facts.FreshRebuiltList || facts.AffineConsList || consumedTail,
                layoutEligible,
                TcoRuntimeManagedKind.List,
                reason);
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

    private bool IsBytesProvenanceEligibleTcoListElement(
        TcoParamStaticFacts facts,
        TypeRef elementType)
    {
        return !ContainsBytesLayout(elementType, new HashSet<TypeSymbol>())
            || facts.BytesProvenanceSafeListRebuild;
    }

    private int? FindBlockingSiblingForCandidate(
        TcoContext tco,
        TypeRef?[] parameterTypes,
        bool[] requestedRuntime,
        int candidateOrdinal)
    {
        int count = Math.Min(parameterTypes.Length, tco.ParamSlots.Count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            if (ordinal != candidateOrdinal
                && IsPermanentlyBlockingTcoParam(
                    tco,
                    parameterTypes,
                    requestedRuntime,
                    ordinal))
            {
                return ordinal;
            }
        }

        return null;
    }

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
