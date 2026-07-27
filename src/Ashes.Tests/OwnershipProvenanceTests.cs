using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// FunctionOwnershipSummary's ExpressionFreshness and ResultProvenance fields. These tests pin the
// fields' own correctness on hand-built shapes, independent of any consumer.
public sealed class OwnershipProvenanceTests
{
    private static Lowering LowerProgram(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        var lowering = new Lowering(diag);
        lowering.Lower(program);
        diag.Errors.ShouldBeEmpty();
        return lowering;
    }

    [Test]
    public void Expression_freshness_is_recorded_for_each_if_arm_not_just_the_whole_body()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let example x =
                if true
                then Empty
                else x
            in example(Empty)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("example");

        summary.ShouldNotBeNull();

        // The whole body reaches {x} (not fresh, since the else-arm returns the parameter), but the
        // then-arm (a sole nullary constructor) is independently fresh, and the else-arm (a bare
        // parameter reference) is not - both facts live below the whole-function-result level that
        // ResultFresh alone can express.
        summary.ExpressionFreshness.Values.ShouldContain(true);
        summary.ExpressionFreshness.Values.ShouldContain(false);
        summary.ExpressionFreshness.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public void Direct_constructor_result_is_rc_eligible_with_no_forward_target()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let fresh u = Empty
            in fresh(0)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("fresh");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Parameter_passthrough_is_conservatively_not_rc_eligible()
    {
        const string source =
            """
            let idish x = x
            in idish(0)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("idish");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Body_that_calls_a_sibling_helper_forwards_and_inherits_its_eligibility()
    {
        // The exact shape the IR-level backward-scan mechanism (RecordReturnedClosureLabel,
        // _functionReturnedClosureLabels) cannot see: the result is computed by CALLING another named
        // function, not by a literal MakeClosure/MakeClosureStack instruction on the body temp.
        // helper's own body deliberately does NOT fold its parameter into the construction
        // (Full(Empty), not Full(x)): a bare parameter argument to a constructor is never trusted as
        // fresh, even when consumed, since this pre-lowering classifier has no type information and
        // cannot rule out the parameter being a heap value aliased elsewhere (see IsDirectRcConstruction's
        // own doc — treating a consumed parameter as automatically fresh regardless of type was tried and
        // caused a real bug, reuse_result_alias_move_elision.ash printing 0 0 0 instead of 15 7 207). This
        // test is about the FORWARDING chain (caller -> helper), not about parameter-folding, so helper's
        // body is written to be safely provable fresh on its own terms.
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper x = Full(Empty)
            let caller x = helper(x)
            in caller(Empty)
            """;

        var lowering = LowerProgram(source);
        var helper = lowering.GetOwnershipSummary("helper");
        var caller = lowering.GetOwnershipSummary("caller");

        helper.ShouldNotBeNull();
        helper.ResultProvenance.RcEligible.ShouldBeTrue();

        caller.ShouldNotBeNull();
        caller.ResultProvenance.ForwardsTo.ShouldBe("helper");
        caller.ResultProvenance.RcEligible.ShouldBeTrue();
    }

    [Test]
    public void Self_recursive_arm_never_conflicts_with_a_fresh_base_case()
    {
        // Mirrors the CO-38 precedent ("a recursive call... never conflicts"): without excluding the
        // self-recursive arm from the agreement requirement, resolving it recurses back into this very
        // function's own in-progress classification, hits the cycle guard, and would wrongly poison an
        // otherwise-fresh base case.
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let recursive build n =
                match n with
                    | 0 -> Zero
                    | _ -> build(n - 1)
            in build(3)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("build");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Match_with_one_forwarding_arm_and_one_fresh_arm_resolves_like_fannkuch_nextPerm()
    {
        // The exact shape motivating this field (per project history: fannkuch-redux's nextPerm/loop -
        // "a capturing top-level recursive function calling a sibling helper"): a match whose base case
        // is a fresh construction and whose recursive case forwards to a sibling helper, not a literal
        // closure. A single-node inspection of the body (rather than recursing to terminal arms) would
        // see only the outermost Match node and default this to conservative-false. helper's own body is
        // Succ(Zero), not Succ(x) — this test is about the forwarding chain (loop -> helper), not about
        // folding a parameter into a constructor argument (never trusted regardless of type; see
        // Body_that_calls_a_sibling_helper_forwards_and_inherits_its_eligibility's own doc).
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let helper x = Succ(Zero)
            let recursive loop n =
                match n with
                    | 0 -> Zero
                    | _ -> helper(loop(n - 1))
            in loop(3)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBe("helper");
    }

    [Test]
    public void Disagreeing_forward_targets_across_arms_report_no_single_forward_hop()
    {
        // helperA/helperB's own bodies are Succ(Zero)/Succ(Succ(Zero)), not Succ(x)/Succ(Succ(x)) — this
        // test is about disagreeing forward targets across choose's two arms, not about folding a
        // parameter into a constructor argument (never trusted regardless of type; see
        // Body_that_calls_a_sibling_helper_forwards_and_inherits_its_eligibility's own doc).
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let helperA x = Succ(Zero)
            let helperB x = Succ(Succ(Zero))
            let choose n =
                match n with
                    | Zero -> helperA(n)
                    | Succ(_) -> helperB(n)
            in choose(Zero)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("choose");

        summary.ShouldNotBeNull();
        // Both arms are independently RC-eligible (each forwards to a constructor-building helper), so
        // the whole function is still RC-eligible - but there is no SINGLE forward hop to report, since
        // the two arms disagree on which function they forward to.
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Call_to_a_fresh_rc_producing_builtin_is_rc_eligible()
    {
        // Ashes.Text.fromInt is declared ProducesFreshRcResult: FreshRcResultKind.String in
        // BuiltinRegistry — a fully applied call into it is itself fresh construction, exactly like a
        // constructor application.
        const string source =
            """
            let describe n = Ashes.Text.fromInt(n)
            in describe(0)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("describe");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Call_to_a_non_producing_builtin_is_conservatively_not_rc_eligible()
    {
        // Ashes.Text.parseBigInt is NOT declared ProducesFreshRcResult (unlike its sibling fromBigInt) —
        // it returns a Result-wrapped value, not a bare fresh BigInt — so it must stay conservative.
        const string source =
            """
            let parse n = Ashes.Text.parseBigInt(n)
            in parse("0")
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("parse");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Rewrapping_a_match_extracted_field_is_never_treated_as_fresh()
    {
        // Adversarial false-positive guard, added deliberately (not discovered via an organic e2e
        // regression like the other tests in this file): `extract` LOOKS fresh at a glance — its only
        // terminal arm is a brand-new W(...) constructor application — but its argument, `inner`, is a
        // field extracted from the caller-supplied `w` via pattern match, not a freshly built value.
        // Rewrapping an aliased field in a new outer cell does NOT make the field itself fresh: if `w`
        // (and therefore `inner`) is aliased elsewhere, treating `extract`'s result as "fully fresh,
        // safe to arena-reset without a defensive copy" would let that reset invalidate memory the
        // alias still reads from - a real use-after-free, not just a missed optimization. This is
        // structurally the same hazard as the Cons-tail-aliasing and consumed-parameter bugs this phase
        // found and fixed (see IsFreshConstructionArgument's own doc), and is also exactly the shape of
        // the explicitly out-of-scope "third gap" (match-extracted-field provenance in
        // Lowering.Patterns.cs's GetAdtField) - this test asserts that, absent that separate piece of
        // work, the classifier stays conservative here rather than guessing.
        //
        // IsFreshConstructionArgument's case list (literals, Add, a nested direct-RC construction, or a
        // fresh-RC-producing builtin) does not include a bare Var under any circumstance - so `inner`,
        // a plain Expr.Var naming a match-bound pattern variable, correctly falls through to "not
        // proven fresh" and the whole W(inner) construction is correctly rejected.
        const string source =
            """
            type Box =
                | B(Int)
            type Wrap =
                | W(Box)
            let extract w =
                match w with
                    | W(inner) -> W(inner)
            in extract(W(B(1)))
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("extract");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }
}
