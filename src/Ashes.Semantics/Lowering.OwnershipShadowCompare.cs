using Ashes.Frontend;

namespace Ashes.Semantics;

// Shadow-compare logging for the FunctionOwnershipSummary fields (ExpressionFreshness,
// ResultProvenance) against the ad hoc classifiers they are eventually meant to replace. Every hook
// here only LOGS a disagreement — it never changes which answer the compiler acts on. This reuses the
// project's existing ASHES_EXPLAIN_OWNERSHIP mechanism (FormatOwnershipSummaries,
// PerceusLifetimePlacement's ShouldExplain) rather than a second debug channel.
public sealed partial class Lowering
{
    private static bool ShouldExplainOwnership()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASHES_EXPLAIN_OWNERSHIP"));

    /// <summary>
    /// Looks up <paramref name="expression"/>'s recorded ExpressionFreshness verdict across every
    /// analyzed function, or null when the expression was never visited by the reach fixpoint (e.g. it
    /// belongs to a lambda body that is not a registered top-level/self-recursive function, or lowering
    /// ran before the analysis, or the node is a synthetic/generated one with no source counterpart).
    /// </summary>
    private bool? TryGetExpressionFreshness(Expr expression)
        => _maExpressionFreshnessAll.TryGetValue(expression, out bool fresh) ? fresh : null;

    private static void LogOwnershipShadowDisagreement(string predicate, string detail)
    {
        if (ShouldExplainOwnership())
        {
            Console.Error.WriteLine($"[ownership-shadow] {predicate} disagreement: {detail}");
        }
    }

    /// <summary>
    /// Compares an existing expression-level ADT freshness classifier's verdict for
    /// <paramref name="expression"/> against the new ExpressionFreshness field, logging when they
    /// disagree. The two questions are related but not identical — the old classifiers ask "is this a
    /// syntactically fresh, RC-manageable ADT construction of a specific type" (folding in per-field
    /// type-shape eligibility via CanArenaReset/CanRuntimeManage*), while ExpressionFreshness asks "does
    /// this expression's value alias any parameter of its enclosing function" — so a disagreement is
    /// informative, not automatically a bug in either side; only a NEW analysis reporting fresh where
    /// the old one (battle-tested) reports not-fresh warrants scrutiny as a possible new-analysis defect.
    /// </summary>
    private void ShadowCompareExpressionFreshness(string predicate, Expr expression, bool oldVerdict)
    {
        if (!ShouldExplainOwnership())
        {
            return;
        }

        if (TryGetExpressionFreshness(expression) is not { } newVerdict || newVerdict == oldVerdict)
        {
            return;
        }

        LogOwnershipShadowDisagreement(
            predicate,
            $"old={oldVerdict} new-expression-freshness={newVerdict} expr={DescribeForShadowLog(expression)}");
    }

    // ShadowCompareResultProvenance (the closure-provenance shadow-compare hook) has been retired:
    // TryResolveKnownFunctionResultOwnership reads FunctionOwnershipSummary.ResultProvenance directly,
    // so there is no separate "old" answer left to shadow-log against — ResultProvenance IS the real
    // decision now, not a comparison candidate.

    /// <summary>
    /// Compares the fresh-closure TCO category that still comes from <c>Collect*</c> against
    /// <c>FunctionOwnershipSummary.TcoParamFacts</c> for the same exact self-recursive function,
    /// logging every disagreement. The other structural categories are no longer compared here
    /// because they already source their live decisions from the canonical positional facts.
    /// </summary>
    private void ShadowCompareTcoParamFacts(
        FuncKey? function,
        string selfName,
        IReadOnlyList<string> paramNames,
        IReadOnlySet<string> freshClosureParams)
    {
        if (!ShouldExplainOwnership())
        {
            return;
        }

        IReadOnlyList<TcoParamStructuralFacts>? tcoParamFacts =
            function is { } key ? GetOwnershipSummary(key)?.TcoParamFacts : null;

        for (int parameterOrdinal = 0; parameterOrdinal < paramNames.Count; parameterOrdinal++)
        {
            string name = paramNames[parameterOrdinal];
            bool isFreshClosure = freshClosureParams.Contains(name);

            TcoSelfCallArgumentShape? oldShape =
                isFreshClosure ? TcoSelfCallArgumentShape.FreshRebuilt : null;

            TcoParamStructuralFacts? newFacts = tcoParamFacts?.FirstOrDefault(
                facts => facts.ParameterOrdinal == parameterOrdinal);
            bool haveNew = newFacts is not null;
            ShadowCompareTcoParamFactsForParameter(
                selfName,
                name,
                parameterOrdinal,
                oldShape,
                haveNew,
                newFacts);
        }
    }

    private static void ShadowCompareTcoParamFactsForParameter(
        string selfName,
        string name,
        int parameterOrdinal,
        TcoSelfCallArgumentShape? oldShape,
        bool haveNew,
        TcoParamStructuralFacts? newFacts)
    {
        TcoSelfCallArgumentShape? newShape = haveNew ? newFacts!.Shape : null;

        if (oldShape is null && !haveNew)
        {
            // Neither side classifies this parameter (an ordinary threaded accumulator with no
            // uniform self-call shape) — expected agreement, nothing to log.
            return;
        }

        if (oldShape is null && haveNew && newShape == TcoSelfCallArgumentShape.Mixed)
        {
            // Old side never positively classified this parameter (its Collect* sets all missed
            // it, the same "none of the above" verdict Mixed represents) — expected agreement.
            return;
        }

        if (oldShape is null
            && haveNew
            && newShape is TcoSelfCallArgumentShape.UnchangedPassthrough
                or TcoSelfCallArgumentShape.GrownCons
                or TcoSelfCallArgumentShape.ConsumedTail)
        {
            // Every non-closure structural category now consumes canonical facts, so there is no
            // remaining old classifier answer to compare it against.
            return;
        }

        if (oldShape is null
            && newShape == TcoSelfCallArgumentShape.FreshRebuilt
            && newFacts!.ArenaSelfContainedListRebuild)
        {
            // FreshRebuilt describes both list literals and closures. A self-contained list now
            // consumes the canonical arena fact and has no old verdict left to compare. A closure
            // that the old fresh-closure collector missed must still reach the disagreement below.
            return;
        }

        if (!haveNew || oldShape != newShape)
        {
            LogOwnershipShadowDisagreement(
                "TcoParamFacts",
                $"function={selfName} param={name} ordinal={parameterOrdinal} "
                    + $"old={(oldShape?.ToString() ?? "none")} "
                    + $"new={(haveNew ? newShape!.Value.ToString() : "absent")}");
        }
    }

    private static string DescribeForShadowLog(Expr expression)
    {
        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(expression, arguments);
        return head switch
        {
            Expr.Var v when arguments.Count > 0 => $"{v.Name}(...)",
            Expr.Var v => v.Name,
            _ => expression.GetType().Name,
        };
    }
}
