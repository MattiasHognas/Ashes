namespace Ashes.Semantics;

/// <summary>Whether promoting a specific TCO loop parameter from arena to runtime-managed (RC)
/// representation is expected to reduce total cost, or merely relocate an existing arena-side cost
/// into a new, equally real (or larger) runtime-managed one. See
/// <see cref="Lowering.RecordTcoPromotionProfitability"/> for the analysis and
/// docs/md/future/PERCEUS_UNIFICATION.md's TCO promotion-cost signal section for the evidence this
/// classification is built on, including why the verdict depends only on whether a sibling parameter
/// permanently blocks the frame's own per-iteration reclaim — an earlier version of this signal also
/// tried to except a candidate whose OWN eligibility reason looked "unconditionally cheap regardless"
/// (a plain non-growth Str/BigInt/Tuple/ADT, as opposed to a consumed-list-tail or affine-append
/// accumulator) even when a sibling was blocking; that finer split was never validated against a real
/// program exercising it and was later found wrong on exactly the real program it was supposed to
/// describe (reverse-complement's own <c>buf</c>, which fails the strict affine-accumulator syntactic
/// test because of an intervening flush read, yet regresses in memory identically to the fully
/// unprofitable case when promoted) — removed rather than patched further, since nothing about "this
/// specific reason is cheap even when a sibling blocks the frame" has ever been confirmed on real
/// code.</summary>
internal enum TcoPromotionVerdict
{
    /// <summary>No sibling parameter in the same loop frame permanently blocks the frame's own
    /// per-iteration reclaim strategy, so this parameter's own promotion is expected to reach a
    /// genuinely cheap per-iteration representation (an O(1) retain/dup, or a copy the arena-only
    /// frame would have paid anyway) rather than adding cost with nothing to show for it.</summary>
    Profitable,

    /// <summary>Some other heap-typed parameter in the same loop frame independently fails every
    /// arena-reset and RC-eligibility exemption, so the frame's own per-iteration reclaim was already
    /// unreachable before this parameter. Promoting it anyway adds new, permanent per-node
    /// runtime-managed bookkeeping (header, dup/drop, and/or reservation regrowth) with nothing
    /// confirmed to offset that cost — no case tested against a real program has ever shown a
    /// parameter's promotion to be cheap under this condition, and one real case
    /// (reverse-complement's <c>buf</c>) has shown the opposite.</summary>
    NotProfitable,
}

/// <summary>One parameter's promotion verdict, plus the reason it was reached — the reason exists
/// for tests and any future consumer to assert on, not for end users.</summary>
internal sealed record TcoParamPromotionProfitability(string ParamName, TcoPromotionVerdict Verdict, string Reason);

public sealed partial class Lowering
{
    // Function name -> (param name -> verdict), populated once per TCO loop by
    // RecordTcoPromotionProfitability, after LowerLambdaCoreRefreshRuntimeManagedTcoParams has run
    // classifier A with fully resolved parameter types. Read-only with respect to every other
    // lowering decision: nothing else in the compiler consults this table today.
    private readonly Dictionary<string, IReadOnlyDictionary<string, TcoParamPromotionProfitability>>
        _tcoPromotionProfitability = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Looks up the promotion-profitability verdicts computed for a TCO loop's own parameters, keyed
    /// by the recursive function's name (<c>let recursive NAME ... = ...</c>). Returns <c>null</c> for
    /// a function with no TCO loop, or one lowered before this analysis ran. This is a read-only
    /// query: it never influences which representation the compiler actually chooses for any
    /// parameter (that remains classifier A plus the existing all-or-nothing frame veto, unchanged).
    /// </summary>
    internal IReadOnlyDictionary<string, TcoParamPromotionProfitability>? GetTcoPromotionProfitability(string functionName)
        => _tcoPromotionProfitability.TryGetValue(functionName, out var verdicts) ? verdicts : null;

    /// <summary>
    /// Computes, for every TCO loop parameter that classifier A (<see
    /// cref="IsIndependentlyRcEligibleTcoParam"/>) would independently promote to runtime-managed
    /// representation, whether that promotion is actually expected to reduce cost or merely relocate
    /// an existing arena-side cost into a new runtime-managed one.
    ///
    /// The question this answers is deliberately narrower than "is promoting this parameter safe" —
    /// per-parameter mixing across unrelated types in the same frame was independently proven safe
    /// (never a wrong value or a crash) by direct adversarial testing before this signal was built;
    /// what was missing was a way to tell a genuinely cheap promotion from an accidentally expensive
    /// one. Two real programs shipped in this repository (a station-merge helper threading a
    /// consumed-tail list of station entries alongside a self-recursive multi-constructor accumulator
    /// tree, and a line-wrapping helper threading an affine-growth string alongside a list of
    /// single-character strings that gets re-passed unchanged on one of its own two tail-call sites)
    /// both regressed in peak memory when every independently-eligible parameter in their frame was
    /// promoted — never a correctness regression, always a cost one, and always in a frame where some
    /// OTHER parameter permanently fails every reset/promotion exemption classifier A or the existing
    /// arena copy-out machinery (<see cref="GetTcoCopyOutKind"/>) can offer it.
    ///
    /// Tracing both regressions down to the actual emitted instructions (not just the classifier's own
    /// verdict) found the same underlying reason in both cases, confirmed directly rather than assumed:
    /// when a sibling parameter's type/usage shape can satisfy neither <see cref="CanArenaReset"/> nor
    /// any of the exemptions <see cref="TcoBackEdgeRuntimeManagedArgCanReset"/>/<see
    /// cref="GetTcoCopyOutKind"/> already offer, the loop's own back-edge reset strategy
    /// (<see cref="EmitTcoBackEdgeArenaBlock"/>) falls all the way through to "no per-iteration reset
    /// at all — accumulate until the call returns," identically whether or not the OTHER parameter is
    /// promoted (the strategy selection is type-shape-driven, not representation-driven). An
    /// arena-only parameter in that regime costs nothing extra per iteration (a bump-pointer
    /// allocation with no header and no explicit release). A promoted parameter in the SAME regime
    /// still gets none of the per-iteration reset it would have needed to justify the promotion, but
    /// now permanently carries the runtime-managed header plus per-node dup/drop (and, for an
    /// affine-growth accumulator, reservation-regrowth) bookkeeping the arena version never paid — a
    /// pure addition, not a relocation, scaling with how many nodes/iterations the loop touches.
    ///
    /// An earlier version of this method additionally tried to except a candidate whose own
    /// eligibility reason looked "affordable regardless of reclaim" (a plain non-growth Str/BigInt/
    /// Tuple/ADT, as opposed to a consumed-list-tail or affine-append accumulator) even when a sibling
    /// blocks the frame. That exception was removed after being checked against the real program it
    /// was meant to cover: reverse-complement's own <c>buf</c> fails the strict affine-accumulator
    /// syntactic test (<see cref="TcoParamOwnership.AffineStr"/>) because its growth is interrupted by a
    /// flush read on one tail-call path, so the exception classified it as "affordable regardless" —
    /// but promoting it regressed peak memory by the same ~370MB the fully-unprofitable case produces.
    /// With no case where "affordable regardless, even under a blocking sibling" has been confirmed on
    /// real code, and one case where it produced a real regression, the verdict now depends only on
    /// whether a sibling permanently blocks the frame at all — see <see cref="TcoPromotionVerdict"/>'s
    /// own doc comment.
    /// </summary>
    private void RecordTcoPromotionProfitability(IReadOnlyDictionary<string, Binding> scope, TcoContext tco)
    {
        if (tco.ParamNames.Count == 0)
        {
            return;
        }

        TypeRef?[] paramTypes = ResolveTcoParamTypes(scope, tco);
        var verdicts = new Dictionary<string, TcoParamPromotionProfitability>(StringComparer.Ordinal);
        for (int i = 0; i < tco.ParamSlots.Count; i++)
        {
            if (paramTypes[i] is not { } type
                || !IsIndependentlyRcEligibleTcoParam(type, i, tco, includeFreshClosures: true))
            {
                continue;
            }

            verdicts[tco.ParamNames[i]] = ClassifyTcoParamPromotion(tco, paramTypes, i, type);
        }

        if (verdicts.Count > 0)
        {
            _tcoPromotionProfitability[tco.SelfName] = verdicts;
        }
    }

    private TypeRef?[] ResolveTcoParamTypes(IReadOnlyDictionary<string, Binding> scope, TcoContext tco)
    {
        var paramTypes = new TypeRef?[tco.ParamSlots.Count];
        for (int i = 0; i < tco.ParamSlots.Count; i++)
        {
            Binding.Local? parameter = scope.Values.OfType<Binding.Local>()
                .FirstOrDefault(local => local.Slot == tco.ParamSlots[i]);
            paramTypes[i] = parameter is null ? null : Prune(parameter.T);
        }

        return paramTypes;
    }

    // Any OTHER parameter in the frame that permanently blocks a per-iteration reclaim strategy
    // regardless of what happens to the candidate: heap-typed, not a resource handle, not a
    // loop-invariant pass-through, and not itself independently RC-eligible. A closure (TFun) never
    // counts here even when it fails classifier A on its own — a non-escaping closure is
    // stack-allocated (see architecture.md's memory model), a genuinely different, zero-reset-cost
    // representation that does not force the frame into the "no reclaim at all" fallback the way a
    // heap ADT/List sibling does; this matches the one case already measured where an
    // occasionally-non-fresh closure sibling did NOT regress its Str co-accumulator.
    private bool IsPermanentlyBlockingTcoSibling(TcoContext tco, TypeRef?[] paramTypes, int index)
        => paramTypes[index] is { } type
            && type is not TypeRef.TFun
            && !CanArenaReset(type)
            && !IsResourceHandleType(type)
            && !(index < tco.ParamSlots.Count && tco.ParamOwnership[tco.ParamSlots[index]].LoopInvariant)
            && !IsIndependentlyRcEligibleTcoParam(type, index, tco, includeFreshClosures: true);

    // paramTypes is threaded through only to satisfy IsPermanentlyBlockingTcoSibling's own signature
    // (it needs every OTHER parameter's resolved type, not just the candidate's own); the candidate's
    // own type is unused here beyond having already gated which candidates this is even called for
    // (see RecordTcoPromotionProfitability), since the verdict no longer depends on a per-reason
    // cost-sensitivity split (see TcoPromotionVerdict's own doc comment for why that split was
    // removed).
    private TcoParamPromotionProfitability ClassifyTcoParamPromotion(
        TcoContext tco,
        TypeRef?[] paramTypes,
        int index,
        TypeRef type)
    {
        string name = tco.ParamNames[index];
        bool hasBlockingSibling = Enumerable.Range(0, tco.ParamSlots.Count)
            .Any(other => other != index && IsPermanentlyBlockingTcoSibling(tco, paramTypes, other));

        return hasBlockingSibling
            ? new TcoParamPromotionProfitability(
                name,
                TcoPromotionVerdict.NotProfitable,
                "a sibling parameter permanently fails every reset/RC exemption, and no real case has confirmed this parameter's own promotion stays cheap under that condition")
            : new TcoParamPromotionProfitability(
                name,
                TcoPromotionVerdict.Profitable,
                "no sibling parameter permanently blocks this frame's per-iteration reclaim");
    }
}
