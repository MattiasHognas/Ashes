using Ashes.Frontend;

namespace Ashes.Semantics;

// Perceus unification Phase 0 (docs/md/future/PERCEUS_UNIFICATION.md §6, Phase 0): shadow-compare
// logging for the two new FunctionOwnershipSummary fields (ExpressionFreshness, ResultProvenance)
// against the ~40 ad hoc classifiers they are eventually meant to replace. Every hook here only LOGS a
// disagreement — it never changes which answer the compiler acts on. This reuses the project's
// existing ASHES_EXPLAIN_OWNERSHIP mechanism (FormatOwnershipSummaries, PerceusLifetimePlacement's
// ShouldExplain) rather than a second debug channel, per docs/md/guide/development.md.
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

    /// <summary>
    /// Compares the IR-level closure-provenance chain's verdict (_functionReturnedClosureLabels /
    /// _runtimeManagedFunctionResultLabels, resolved via TryResolveKnownFunctionResultOwnership) against
    /// the new ResultProvenance field for a saturated direct call to a registered top-level/
    /// self-recursive function. Only compared when the call supplies exactly as many arguments as the
    /// function's flattened parameter list (see FunctionOwnershipSummary.Parameters) — the shape both
    /// mechanisms are answering the same question for.
    /// </summary>
    private void ShadowCompareResultProvenance(string function, int argumentCount, bool oldRuntimeManaged)
    {
        if (!ShouldExplainOwnership())
        {
            return;
        }

        if (GetOwnershipSummary(function) is not { } summary || argumentCount != summary.Parameters.Count)
        {
            return;
        }

        bool newRcEligible = summary.ResultProvenance.RcEligible;
        if (newRcEligible == oldRuntimeManaged)
        {
            return;
        }

        LogOwnershipShadowDisagreement(
            "closure-provenance",
            $"function={function} old-runtime-managed={oldRuntimeManaged} "
                + $"new-rc-eligible={newRcEligible} new-forwards-to={summary.ResultProvenance.ForwardsTo ?? "none"}");
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
