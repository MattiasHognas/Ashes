using Ashes.Frontend;

namespace Ashes.Semantics;

// Perceus unification Phase 4 (docs/md/future/PERCEUS_UNIFICATION.md §6, Phase 4): the shared
// "top-cell fresh" engine that ProducesFreshRuntimeManageableAdt/IsFreshRuntimeManageableAdtExpression/
// IsFreshConstructorTree (ADTs) and ProducesFreshTuple (Tuples) are built on, instead of each
// independently re-deriving control-flow transparency and same-parent-type arm reconciliation from AST
// shape.
//
// "Top-cell fresh" is a deliberately NARROWER question than FunctionOwnershipSummary.ExpressionFreshness
// (Phase 0): ExpressionFreshness asks "does this expression's value alias any parameter anywhere in its
// reachable graph" (a whole-value aliasing question), while top-cell freshness asks only "was THIS
// expression's outermost cell (the constructor/tuple shell itself) freshly allocated here" — regardless
// of what its own field/element expressions alias. `Full(x)` is top-cell-fresh (the Full cell is a new
// allocation) even when `x` aliases a parameter and is therefore NOT ExpressionFreshness-fresh: aliased
// fields are handled correctly by ordinary per-field RC dup/drop, a separate and already-sound mechanism
// that does not require the field itself to be uniquely owned. See PERCEUS_UNIFICATION.md's Phase 0
// follow-up note and this phase's design checkpoint for the full reasoning.
//
// Top-cell freshness is a PURELY SYNTACTIC property (no interprocedural alias reasoning is needed to
// answer "is this expression literally a constructor/tuple application"), so — unlike ExpressionFreshness
// — it is implemented as an ordinary recursive query over the AST rather than folded into the
// interprocedural ResultReach fixpoint. Routing it through that fixpoint would narrow its domain to only
// _maFuncs-registered top-level/self-recursive function bodies, a strict regression versus the ad hoc
// classifiers it replaces, which run on any expression encountered during lowering (including inside
// nested lambdas ResultReach never visits).
public sealed partial class Lowering
{
    /// <summary>
    /// Collects every terminal expression reachable from <paramref name="body"/> by walking
    /// transparently through <c>let</c>/<c>let result</c>/<c>let recursive</c> (into the body only —
    /// the bound value's own freshness is a separate question) and every arm of an <c>if</c>/<c>match</c>.
    /// This is the one shared control-flow walk both the ADT and Tuple escape-level classifiers use;
    /// previously each re-implemented an equivalent walk independently (<c>CollectFreshAdtEscapeArms</c>
    /// for ADTs, a bespoke switch expression for Tuples).
    /// </summary>
    private static void CollectFreshEscapeTerminals(Expr body, List<Expr> terminals)
    {
        switch (body)
        {
            case Expr.If iff:
                CollectFreshEscapeTerminals(iff.Then, terminals);
                CollectFreshEscapeTerminals(iff.Else, terminals);
                break;
            case Expr.Match match:
                foreach (MatchCase matchCase in match.Cases)
                {
                    CollectFreshEscapeTerminals(matchCase.Body, terminals);
                }

                break;
            case Expr.Let let:
                CollectFreshEscapeTerminals(let.Body, terminals);
                break;
            case Expr.LetResult letResult:
                CollectFreshEscapeTerminals(letResult.Body, terminals);
                break;
            case Expr.LetRecursive letRecursive:
                CollectFreshEscapeTerminals(letRecursive.Body, terminals);
                break;
            default:
                terminals.Add(body);
                break;
        }
    }

    /// <summary>
    /// The canonical "is this single expression's outermost cell a fresh ADT construction" query: a
    /// <c>RecordLit</c>, or a saturated call chain rooted at a constructor <c>Var</c> (including a
    /// nullary constructor). No control-flow transparency at this level (a caller that wants to see
    /// through <c>if</c>/<c>match</c> walks with <see cref="CollectFreshEscapeTerminals"/> first) and no
    /// opinion on field aliasing — this is a thin, named alias for <see cref="TryDescribeConstructorExpression"/>
    /// so every top-cell-fresh consumer shares one entry point instead of re-deriving the same AST shape.
    /// </summary>
    private bool IsTopCellFreshAdtConstruction(
        Expr expression,
        out ConstructorSymbol? constructor,
        out List<Expr>? arguments,
        out TypeRef.TNamedType? resultType)
        => TryDescribeConstructorExpression(expression, out constructor, out arguments, out resultType);

    /// <summary>
    /// Reconciles a set of terminal arms (collected by <see cref="CollectFreshEscapeTerminals"/>) against
    /// a per-arm freshness predicate and a per-arm grouping key, preserving the CO-38/PR #299 invariant:
    /// a candidate arm is only accepted as making the WHOLE escape fresh when every OTHER arm sharing its
    /// group is independently fresh too (never an OR across arms that disagree). A funneling sibling
    /// (one with no group key — e.g. a recursive call that does not itself construct a value of the
    /// group's shape at this position) never conflicts, since it is not compared against.
    /// </summary>
    private static bool AnyArmConsistentlyFresh(
        IReadOnlyList<Expr> arms,
        Func<Expr, bool> isFresh,
        Func<Expr, string?> groupKey)
    {
        foreach (Expr arm in arms)
        {
            string? key = groupKey(arm);
            if (key is null || !isFresh(arm))
            {
                continue;
            }

            bool consistent = true;
            foreach (Expr other in arms)
            {
                if (ReferenceEquals(other, arm))
                {
                    continue;
                }

                string? otherKey = groupKey(other);
                if (otherKey is not null
                    && string.Equals(otherKey, key, StringComparison.Ordinal)
                    && !isFresh(other))
                {
                    consistent = false;
                    break;
                }
            }

            if (consistent)
            {
                return true;
            }
        }

        return false;
    }
}
