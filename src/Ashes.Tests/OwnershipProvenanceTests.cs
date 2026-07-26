using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Perceus unification Phase 0 (docs/md/future/PERCEUS_UNIFICATION.md §5 items 1 and 3): the two new
// FunctionOwnershipSummary fields, ExpressionFreshness and ResultProvenance. Nothing yet consults
// either field for a lowering decision (this phase is behaviour-preserving); these tests pin the
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
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper x = Full(x)
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
        // see only the outermost Match node and default this to conservative-false.
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let helper x = Succ(x)
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
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let helperA x = Succ(x)
            let helperB x = Succ(Succ(x))
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
    public void Call_to_an_unregistered_builtin_is_conservatively_not_rc_eligible()
    {
        const string source =
            """
            let describe n = Ashes.Text.fromInt(n)
            in describe(0)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("describe");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }
}
