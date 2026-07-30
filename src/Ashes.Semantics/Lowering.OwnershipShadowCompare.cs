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
    /// Compares a TCO loop's own <c>Collect*</c>-derived per-parameter classification (still the real
    /// decision — nothing reads the new field yet) against <c>FunctionOwnershipSummary.TcoParamFacts</c>
    /// for the same self-recursive function, logging every disagreement. <paramref name="affineConsListParams"/>
    /// (the growing-cons-accumulator shape) compares against <see cref="TcoSelfCallArgumentShape.GrownCons"/>
    /// like every other old name-set compares against its own positive shape.
    /// </summary>
    private void ShadowCompareTcoParamFacts(
        FuncKey? function,
        string selfName,
        IReadOnlyList<string> paramNames,
        IReadOnlySet<string> loopInvariantParams,
        IReadOnlySet<string> freshRebuiltListParams,
        IReadOnlySet<string> affineConsListParams,
        IReadOnlySet<string> consumedListTailParams,
        IReadOnlySet<string> freshClosureParams)
    {
        if (!ShouldExplainOwnership())
        {
            return;
        }

        var tcoParamFacts = (function is { } key
            ? GetOwnershipSummary(key)
            : GetOwnershipSummary(selfName))?.TcoParamFacts;

        foreach (string name in paramNames)
        {
            bool isLoopInvariant = loopInvariantParams.Contains(name);
            bool isFreshRebuilt = freshRebuiltListParams.Contains(name) || freshClosureParams.Contains(name);
            bool isConsumedTail = consumedListTailParams.Contains(name);
            bool isGrownCons = affineConsListParams.Contains(name);

            TcoSelfCallArgumentShape? oldShape =
                isLoopInvariant ? TcoSelfCallArgumentShape.UnchangedPassthrough
                : isFreshRebuilt ? TcoSelfCallArgumentShape.FreshRebuilt
                : isConsumedTail ? TcoSelfCallArgumentShape.ConsumedTail
                : isGrownCons ? TcoSelfCallArgumentShape.GrownCons
                : null;

            bool haveNew = tcoParamFacts is not null && tcoParamFacts.TryGetValue(name, out var newFacts);
            TcoSelfCallArgumentShape? newShape = haveNew ? tcoParamFacts![name].Shape : null;

            if (oldShape is null && !haveNew)
            {
                // Neither side classifies this parameter (an ordinary threaded accumulator with no
                // uniform self-call shape) — expected agreement, nothing to log.
                continue;
            }

            if (oldShape is null && haveNew && newShape == TcoSelfCallArgumentShape.Mixed)
            {
                // Old side never positively classified this parameter (its Collect* sets all missed
                // it, the same "none of the above" verdict Mixed represents) — expected agreement.
                continue;
            }

            if (!haveNew || oldShape != newShape)
            {
                LogOwnershipShadowDisagreement(
                    "TcoParamFacts",
                    $"function={selfName} param={name} old={(oldShape?.ToString() ?? "none")} "
                        + $"new={(haveNew ? newShape!.Value.ToString() : "absent")}");
            }
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
