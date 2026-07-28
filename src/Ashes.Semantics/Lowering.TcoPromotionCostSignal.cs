namespace Ashes.Semantics;

/// <summary>Whether promoting a specific TCO loop parameter from arena to runtime-managed (RC)
/// representation is expected to reduce total cost, or merely relocate an existing arena-side cost
/// into a new, equally real (or larger) runtime-managed one. See
/// <see cref="Lowering.RecordTcoPromotionProfitability"/> for the analysis and
/// docs/md/future/PERCEUS_UNIFICATION.md's TCO promotion-cost signal section for the evidence this
/// classification is built on.</summary>
internal enum TcoPromotionVerdict
{
    /// <summary>No sibling parameter in the same loop frame permanently blocks the frame's own
    /// per-iteration reclaim strategy, so this parameter's own promotion reason (see
    /// <see cref="TcoParamPromotionProfitability.Reason"/>) is expected to reach a genuinely cheap
    /// per-iteration representation (an O(1) retain/dup, or a copy the arena-only frame would have
    /// paid anyway) rather than adding cost with nothing to show for it.</summary>
    Profitable,

    /// <summary>Some other heap-typed parameter in the same loop frame independently fails every
    /// arena-reset and RC-eligibility exemption, and this parameter's own eligibility reason is one
    /// that only pays off when the frame can actually reclaim per iteration (a list walked one cons
    /// cell at a time, or a string/list grown by repeated affine append). In that combination the
    /// frame's per-iteration reclaim was already unreachable before this parameter, so promoting it
    /// adds new, permanent per-node runtime-managed bookkeeping (header, dup/drop, and/or reservation
    /// regrowth) that buys no corresponding reduction in the arena-only frame's own cost.</summary>
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
    /// This is exactly why the two regressions share no type in common (a List, and a Str) but do
    /// share a shape: in both, the promoted parameter's OWN eligibility reason is one that is only
    /// affordable when the loop can actually reclaim between iterations (a list walked one cons cell
    /// at a time rather than rebuilt fresh, or a string grown by repeated affine append) — reasons
    /// already distinguished from the "affordable regardless" reasons (a list rebuilt fresh every
    /// iteration, or a plain non-growth-accumulator Str/BigInt/Tuple/ADT) by <see
    /// cref="TcoContext.ConsumedListTailParams"/>/<see cref="TcoContext.AffineConsListParams"/>/<see
    /// cref="TcoContext.AffineStrParams"/> and the existing DeepAdt-for-non-fresh-list downgrade inside
    /// <see cref="TcoBackEdgeArgCopyOutKind"/> — this signal reuses those same facts rather than
    /// re-deriving a fourth, potentially-diverging notion of "cheap."
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
            && !tco.LoopInvariantParams.Contains(tco.ParamNames[index])
            && !IsIndependentlyRcEligibleTcoParam(type, index, tco, includeFreshClosures: true);

    private TcoParamPromotionProfitability ClassifyTcoParamPromotion(
        TcoContext tco,
        TypeRef?[] paramTypes,
        int index,
        TypeRef type)
    {
        string name = tco.ParamNames[index];
        bool hasBlockingSibling = Enumerable.Range(0, tco.ParamSlots.Count)
            .Any(other => other != index && IsPermanentlyBlockingTcoSibling(tco, paramTypes, other));

        // Only affordable when the loop can actually reclaim between iterations: a consumed list
        // tail (walked one cons cell at a time, never rebuilt), an affine cons chain, or an affine
        // string append — as opposed to a list rebuilt fresh every iteration, or a plain
        // non-growth-accumulator Str/BigInt/Tuple/ADT, which classifier A also allows regardless.
        bool costSensitive = type is TypeRef.TList
                && tco.ConsumedListTailParams.Contains(name)
                && !tco.FreshRebuiltListParams.Contains(name)
                && !tco.AffineConsListParams.Contains(name)
            || type is TypeRef.TList && tco.AffineConsListParams.Contains(name)
            || tco.AffineStrParams.Contains(name);

        if (hasBlockingSibling && costSensitive)
        {
            return new TcoParamPromotionProfitability(
                name,
                TcoPromotionVerdict.NotProfitable,
                "a sibling parameter permanently fails every reset/RC exemption, and this parameter's own eligibility only pays off when the frame can reclaim per iteration");
        }

        string reason = hasBlockingSibling
            ? "a sibling parameter fails every exemption, but this parameter's own representation is unconditionally cheap regardless"
            : "no sibling parameter permanently blocks this frame's per-iteration reclaim";
        return new TcoParamPromotionProfitability(name, TcoPromotionVerdict.Profitable, reason);
    }
}
