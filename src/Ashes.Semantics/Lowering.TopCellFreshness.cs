using Ashes.Frontend;

namespace Ashes.Semantics;

// The shared "top-cell fresh" engine that ProducesFreshRuntimeManageableAdt/
// IsFreshRuntimeManageableAdtExpression/IsFreshConstructorTree (ADTs) and ProducesFreshTuple (Tuples)
// are built on, instead of each independently re-deriving control-flow transparency and
// same-parent-type arm reconciliation from AST shape.
//
// "Top-cell fresh" is a deliberately NARROWER question than FunctionOwnershipSummary.ExpressionFreshness:
// ExpressionFreshness asks "does this expression's value alias any parameter anywhere in its
// reachable graph" (a whole-value aliasing question), while top-cell freshness asks only "was THIS
// expression's outermost cell (the constructor/tuple shell itself) freshly allocated here" — regardless
// of what its own field/element expressions alias. `Full(x)` is top-cell-fresh (the Full cell is a new
// allocation) even when `x` aliases a parameter and is therefore NOT ExpressionFreshness-fresh: aliased
// fields are handled correctly by ordinary per-field RC dup/drop, a separate and already-sound mechanism
// that does not require the field itself to be uniquely owned.
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
    /// The escaping-result-boundary counterpart of <see cref="IsFreshListConstructionExpression"/> for
    /// Lists, giving it the same control-flow transparency <c>ProducesFreshRuntimeManageableAdt</c>/
    /// <c>ProducesFreshTuple</c> have — a list literal returned from a match/if arm (e.g.
    /// <c>if empty then [] else x :: rest</c>) is now recognized as top-cell fresh at a function's
    /// escaping result, not just when the whole body IS the construction directly.
    ///
    /// This deliberately does NOT touch <see cref="IsFreshListConstructionExpression"/>'s terminal
    /// case set (still only <c>ListLit</c>, or a <c>Cons</c> chain bottoming out in one — never
    /// <c>Var</c> or <c>Call</c>). That narrower set is exactly what makes a threaded/shared-tail list
    /// (a bare accumulator var, a pattern-derived tail, a cons onto an existing list) NEVER classify as
    /// fresh here: widening it to accept a call result (the way the arena/TCO-side
    /// <see cref="IsArenaSelfContainedListRebuildExpr"/> deliberately does, for a different risk profile — CO-32's
    /// whole-list-clone-cost question, not this RC-promotion question) would let a shared-tail list get
    /// promoted to RC, silently aliasing the previous iteration's cells. Preserving the exact terminal
    /// set across both engines is the tail-sharing safeguard for this phase: a list that is not
    /// syntactically a fresh literal/cons-chain both here and at IsFreshListConstructionExpression's
    /// direct-position call sites is always treated as shared/threaded (the existing, conservative
    /// default), independent of which call site asks.
    ///
    /// EVERY terminal must be independently fresh (AND, not OR/existence-check). An earlier version of
    /// this predicate used an existence check, matching ProducesFreshTuple's OR semantics on the theory
    /// that a list has no sibling-constructor identity for an AND-style reconciliation to key on the
    /// way ADTs' same-parent-type mixing does. That theory was incomplete: it addressed why there is no
    /// "different constructor of the same type" hazard, but missed a DIFFERENT mixing hazard that
    /// doesn't need one. `if cond then existingList else [fresh, list]` has one arm that is a bare Var
    /// passthrough of an existing (not provably fresh) binding and one arm that IS a fresh construction.
    /// An existence check requests the runtime-RC List representation for the WHOLE if, so the fresh
    /// arm's cons cells get allocated on the RC heap (`Alloc RuntimeManaged: true`) — but
    /// the join-level `MarkUniformRuntimeManagedResult`/`MarkRuntimeManagedMatchResult` machinery (which
    /// decides whether the JOINED/matched value is actually treated as owned) requires BOTH/ALL arms to
    /// be independently verified runtime-managed, which the passthrough arm never is. The two mechanisms
    /// disagree — confirmed by direct IR inspection: an orphaned `Alloc{RuntimeManaged: true}` cell with
    /// no reachable `RcDrop` anywhere in the lowered program for the fresh arm. This IS a real,
    /// unambiguous bookkeeping inconsistency (the ambient flag's "permission to allocate RC" and the
    /// join's "was it actually RC" verdict disagreeing), and this fix removes it outright — every
    /// terminal fresh is required before the representation request is granted at all, so the two
    /// verdicts can no longer disagree, by construction.
    ///
    /// What this fix is NOT shown to be: a demonstrated process-memory leak. Compiled-binary testing
    /// (both a discarding loop and a consuming loop, up to 50M iterations, and a version with a much
    /// larger per-iteration payload to make any real per-call leak show up clearly in RSS) found NO
    /// measurable difference between the pre-fix (existence-check) and post-fix binaries — the orphaned
    /// RC-tagged cell is apparently reclaimed by the same scope-based bulk arena watermark reset that
    /// reclaims ordinary arena garbage, since it never escapes its own allocating scope before that
    /// reset runs (the call-boundary CopyOutList/CopyOutArena normalization that DOES escape the value
    /// makes its own independent, correctly-tracked copy, rather than promoting the orphaned cell).
    /// Whether that reclaim path is guaranteed for every context this shape could occur in (a different
    /// call convention, a value that escapes further before any reset, a deeper TCO nesting) was not
    /// re-derived from the codegen/runtime source in the time available, so this should be read as "a
    /// real, provable bookkeeping defect worth fixing outright since it costs nothing to fix and cannot
    /// make anything less safe" rather than "a proven corruption/leak this phase's own testing caught in
    /// the wild" — the two are different claims and this comment intentionally does not conflate them.
    /// (The same latent gap, by the same reasoning, appears to reproduce for ADTs/Tuples'
    /// `AnyArmConsistentlyFresh` too, per a quick IR-level check of the same shape against an existing
    /// binding — not independently confirmed at the compiled-binary level, and not fixed here, since
    /// that is already-shipped, more broadly-used code this phase was not scoped to touch; reported
    /// separately for whoever owns that decision.)
    /// </summary>
    private bool ProducesFreshRuntimeManageableList(Expr body)
    {
        var terminals = new List<Expr>();
        CollectFreshEscapeTerminals(body, terminals);
        return terminals.Count > 0 && terminals.All(IsFreshListConstructionExpression);
    }

    /// <summary>
    /// Reconciles a set of terminal arms (collected by <see cref="CollectFreshEscapeTerminals"/>) against
    /// a per-arm freshness predicate and a per-arm grouping key, preserving the CO-38/PR #299 invariant:
    /// a candidate arm is only accepted as making the WHOLE escape fresh when every OTHER arm sharing its
    /// group is independently fresh too (never an OR across arms that disagree). A TRUE funneling
    /// sibling (<paramref name="isFunnel"/> — a tail self-call back to the function currently being
    /// lowered) never conflicts: it constructs nothing itself at this position, but by structural
    /// induction whichever arm it eventually bottoms out at is one of THIS SAME reconciliation's own
    /// terminals, already governed here.
    ///
    /// Every OTHER terminal with no group key (a bare Var, a projected field, an ordinary call — a
    /// passthrough of a pre-existing, not-provably-fresh value, not a fresh construction and not a
    /// funnel) DOES conflict with any fresh candidate, unconditionally. Since every terminal collected
    /// by <see cref="CollectFreshEscapeTerminals"/> is an alternative at the exact same escape position,
    /// they all share one declared type by construction — there is never a second, genuinely
    /// different-typed group for a null key to legitimately avoid. Earlier code treated a null group key
    /// as "no opinion" and skipped it here, so a passthrough sibling's non-freshness never blocked a
    /// fresh candidate's verdict from winning unopposed — while the passthrough arm's own join-level
    /// bookkeeping (<c>MarkUniformRuntimeManagedResult</c>/<c>MarkRuntimeManagedMatchResult</c>) correctly
    /// declined to claim ownership of its own value. The two verdicts disagreeing produced an
    /// <c>AllocAdt</c>/tuple <c>Alloc</c> with <c>RuntimeManaged: true</c> and no reachable
    /// <c>RcDrop</c> anywhere in the lowered program: a leak, not a use-after-free.
    /// </summary>
    private static bool AnyArmConsistentlyFresh(
        IReadOnlyList<Expr> arms,
        Func<Expr, bool> isFresh,
        Func<Expr, string?> groupKey,
        Func<Expr, bool> isFunnel)
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
                if (ReferenceEquals(other, arm) || isFunnel(other))
                {
                    continue;
                }

                string? otherKey = groupKey(other);
                if (otherKey is null || string.Equals(otherKey, key, StringComparison.Ordinal))
                {
                    if (!isFresh(other))
                    {
                        consistent = false;
                        break;
                    }
                }
            }

            if (consistent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The one case a null group key must still NOT conflict during <see cref="AnyArmConsistentlyFresh"/>
    /// reconciliation: a full self-call chain back to the tail-recursive function currently being
    /// lowered (<c>_tcoCtx</c>), at this exact arity. A tail-recursive parser like
    /// <c>parseStrBody acc text = match ... with ... -> Ok((acc, t)) | ... -> parseStrBody(acc + h)(t)</c>
    /// has the recursive-call arm construct nothing here, but every execution path through it bottoms
    /// out at one of this SAME function's own terminal arms (by structural induction on the recursion) —
    /// arms this exact reconciliation already governs. Any OTHER non-constructing arm (a bare Var, a
    /// projected field, a call to a different function) is not exempt: see the leak this distinction
    /// exists to close in <see cref="AnyArmConsistentlyFresh"/>'s doc comment.
    /// </summary>
    private bool IsSelfRecursiveTailFunnelArm(Expr arm)
    {
        if (_tcoCtx is null || _tcoCtx.SelfName.Length == 0)
        {
            return false;
        }

        return arm switch
        {
            Expr.Call call => IsSelfCallChain(call, _tcoCtx.SelfName, _tcoCtx.ParamCount),
            Expr.Var v => _tcoCtx.ParamCount == 0 && string.Equals(v.Name, _tcoCtx.SelfName, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// A non-constructing call arm can participate in a uniformly fresh escaping result when the
    /// whole-program ownership fixpoint proves that its saturated callee returns a fresh, RC-eligible
    /// value. This generalizes the self-recursive structural-induction case to exact mutual-recursion
    /// cycles and other statically known fresh forwarding functions. The caller's representation
    /// request will normalize an arena-backed callee result when that callee has not selected RC.
    /// </summary>
    private bool IsProvenFreshCallFunnelArm(Expr arm)
    {
        if (IsSelfRecursiveTailFunnelArm(arm))
        {
            return true;
        }
        if (arm is not Expr.Call call)
        {
            return false;
        }

        List<Expr> arguments = [];
        Expr root = CollectCallArgs(call, arguments);
        FunctionOwnershipSummary? summary = GetOwnershipSummaryForCallRoot(root);
        return summary is
        {
            ResultFresh: true,
            ResultProvenance.RcEligible: true,
        }
            && arguments.Count == summary.Parameters.Count;
    }
}
