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
    public void Function_parameter_shadow_does_not_forward_to_global_helper()
    {
        const string source =
            """
            let helper value = value + 1
            let invoke helper value = helper(value)
            in invoke(helper)(1)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("invoke");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Shadowed_self_name_is_not_treated_as_recursive_arm()
    {
        const string source =
            """
            type Holder(A) =
                | Hold(A)
            let recursive choose holder depth =
                match holder with
                    | Hold(choose) ->
                        if depth <= 0
                        then depth + 1
                        else choose(depth - 1)
            in choose(Hold(given value -> value))(1)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("choose");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Branch_local_result_alias_does_not_leak_into_sibling_arm()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let passthrough value = value
            let fresh ignored = Full(Empty)
            let choose flag =
                let result = passthrough(Empty)
                in if flag
                    then let result = fresh(0) in result
                    else result
            in choose(true)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("choose");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Result_alias_uses_its_binding_scope_before_pattern_shadow()
    {
        const string source =
            """
            type Holder =
                | Hold(Int)
            let helper value = value + 1
            let invoke holder =
                let result = helper(0)
                in match holder with
                    | Hold(helper) -> result
            in invoke(Hold(1))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("invoke");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBe("helper");
    }

    [Test]
    public void Nested_recursive_return_uses_exact_self_identity()
    {
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let make seed =
                (let recursive go depth =
                    if depth <= 0
                    then Zero
                    else go(depth - 1)
                in go)
            in make(Zero)(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("make");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Colliding_nested_recursive_names_keep_exact_self_provenance()
    {
        const string source =
            """
            type Nat =
                | Zero
                | Succ(Nat)
            let first seed =
                (let recursive go depth =
                    if depth <= 0
                    then Zero
                    else go(depth - 1)
                in go)
            let second seed =
                (let recursive go depth =
                    if depth <= 0
                    then Zero
                    else go(depth - 1)
                in go)
            in second(first(Zero)(1))(1)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        first.ShouldNotBeNull();
        first.ResultProvenance.RcEligible.ShouldBeTrue();
        first.ResultProvenance.ForwardsTo.ShouldBeNull();

        second.ShouldNotBeNull();
        second.ResultProvenance.RcEligible.ShouldBeTrue();
        second.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Unrelated_same_named_helpers_keep_distinct_result_provenance()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let first input =
                let go value = value
                in go(input)
            let second input =
                let go ignored = Full(Empty)
                in go(input)
            in (first(Empty), second(Empty))
            """;

        Lowering lowering = LowerProgram(source);
        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("go");
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        helpers.Count.ShouldBe(2);
        lowering.GetOwnershipSummary("go").ShouldBeNull();
        helpers.Single(summary => summary.Parameters.Contains("value", StringComparer.Ordinal))
            .ResultProvenance.RcEligible.ShouldBeFalse();
        helpers.Single(summary => summary.Parameters.Contains("ignored", StringComparer.Ordinal))
            .ResultProvenance.RcEligible.ShouldBeTrue();

        first.ShouldNotBeNull();
        first.ResultProvenance.RcEligible.ShouldBeFalse();
        second.ShouldNotBeNull();
        second.ResultProvenance.RcEligible.ShouldBeTrue();
    }

    [Test]
    public void Nested_function_shadowing_outer_function_resolves_to_inner_identity()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let go value = value
            let caller input =
                let go ignored = Full(Empty)
                in go(input)
            in caller(Empty)
            """;

        Lowering lowering = LowerProgram(source);
        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("go");
        FunctionOwnershipSummary? caller = lowering.GetOwnershipSummary("caller");

        helpers.Count.ShouldBe(2);
        helpers.Single(summary => summary.Parameters.Contains("value", StringComparer.Ordinal))
            .ResultProvenance.RcEligible.ShouldBeFalse();
        helpers.Single(summary => summary.Parameters.Contains("ignored", StringComparer.Ordinal))
            .ResultProvenance.RcEligible.ShouldBeTrue();
        caller.ShouldNotBeNull();
        caller.ResultProvenance.RcEligible.ShouldBeTrue();
    }

    [Test]
    public void Non_function_shadow_keeps_function_summary_but_does_not_resolve_to_it()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper ignored = Full(Empty)
            let caller input =
                let helper = input
                in helper
            in caller(Empty)
            """;

        Lowering lowering = LowerProgram(source);
        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("helper");
        FunctionOwnershipSummary? caller = lowering.GetOwnershipSummary("caller");

        helpers.Count.ShouldBe(1);
        helpers[0].ResultProvenance.RcEligible.ShouldBeTrue();
        lowering.GetOwnershipSummary("helper").ShouldBeNull();

        caller.ShouldNotBeNull();
        caller.ResultReaches("input").ShouldBeTrue();
        caller.ResultProvenance.RcEligible.ShouldBeFalse();
    }

    [Test]
    public void Alias_stripped_colliding_helpers_keep_original_binding_identity()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper ignored = Full(Empty)
            let first =
                let helperAlias = helper
                in given input ->
                    let go value = helperAlias(value)
                    in go(input)
            let second =
                let helperAlias = helper
                in given input ->
                    let go value = value
                    in go(input)
            in (first(Empty), second(Empty))
            """;

        Lowering lowering = LowerProgram(source);
        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("go");
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        helpers.Count.ShouldBe(2);
        helpers.Single(summary => summary.ResultProvenance.RcEligible)
            .ResultProvenance.ForwardsTo.ShouldBe("helper");
        helpers.Single(summary => !summary.ResultProvenance.RcEligible)
            .ResultProvenance.ForwardsTo.ShouldBeNull();

        first.ShouldNotBeNull();
        first.ResultProvenance.RcEligible.ShouldBeTrue();
        second.ShouldNotBeNull();
        second.ResultProvenance.RcEligible.ShouldBeFalse();
    }

    [Test]
    public void Nested_alias_stripping_canonicalizes_rebuilt_binders_transitively()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper ignored = Full(Empty)
            let first =
                let outerAlias = helper
                in given seed ->
                    let container =
                        let innerAlias = outerAlias
                        in given input ->
                            let go value = innerAlias(value)
                            in go(input)
                    in container(seed)
            let second =
                let outerAlias = helper
                in given seed ->
                    let container =
                        let innerAlias = outerAlias
                        in given input ->
                            let go value = value
                            in go(input)
                    in container(seed)
            in (first(Empty), second(Empty))
            """;

        Lowering lowering = LowerProgram(source);
        IReadOnlyList<FunctionOwnershipSummary> helpers = lowering.GetOwnershipSummaries("go");
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        helpers.Count.ShouldBe(2);
        helpers.Single(summary => summary.ResultProvenance.RcEligible)
            .ResultProvenance.ForwardsTo.ShouldBe("helper");
        helpers.Single(summary => !summary.ResultProvenance.RcEligible)
            .ResultProvenance.ForwardsTo.ShouldBeNull();
        first.ShouldNotBeNull();
        first.ResultProvenance.RcEligible.ShouldBeTrue();
        second.ShouldNotBeNull();
        second.ResultProvenance.RcEligible.ShouldBeFalse();
    }

    [Test]
    public void Partial_forward_call_is_not_result_provenance()
    {
        const string source =
            """
            let maker left right = left + right
            let partial left = maker(left)
            in partial(1)(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("partial");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Alias_stripped_nested_function_uses_its_canonical_provenance_body()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let helper ignored = Full(Empty)
            let wrapper =
                let helperAlias = helper
                in given seed ->
                    let local item = helperAlias(item)
                    in local(seed)
            in wrapper(Empty)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("local");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
        summary.ResultProvenance.ForwardsTo.ShouldBe("helper");
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

    [Test]
    public void Positional_single_constructor_field_nested_in_a_multi_constructor_parent_is_rc_eligible()
    {
        // The exact shape fannkuch-redux's nextPerm/Step/State pair has: State is a positional
        // (no field names), single-constructor accumulator type, embedded as a field of Step, a
        // SEPARATE, multi-constructor (Done | Continue) parent. IsFreshConstructionArgument's own
        // narrower per-argument check rejects the nested S(...) construction outright (its own
        // fields are bare Vars, never literals), so this shape only resolves fresh via
        // IsDirectRcConstruction's fallback onto IsFreshRuntimeManageableAdtExpressionCore — the
        // same top-cell-freshness query the construction-time lowering itself consults, so the two
        // can never disagree about the same shape. Before that fallback existed, this construction
        // (and every recursive function shaped like it) never resolved through
        // TryResolveKnownFunctionResultOwnership, forcing every caller into an unconditional O(size)
        // defensive deep copy on every call — the dominant driver of fannkuch-redux's ~27.4GB
        // regression.
        const string source =
            """
            type State =
                | S(List(Int), List(Int))
            type Step =
                | Done
                | Continue(State, Int)
            let recursive next r perm count =
                if r == 0
                then Done
                else Continue(S(perm)(count))(r)
            in next(1)([1, 2])([3, 4])
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("next");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeTrue();
    }

    [Test]
    public void Bare_variable_directly_as_a_nested_adt_typed_field_is_never_treated_as_fresh()
    {
        // Adversarial guard for the new fallback (see the sibling test above): a nested ADT-typed
        // field is only ever recognized fresh when it is ITSELF a constructor application
        // (IsFreshTcoOwnedChildAdtConstructorApplication requires IsTopCellFreshAdtConstruction to
        // match) — never a bare variable, no matter how "fresh-looking" the surrounding shape is.
        // `make`'s only terminal arm builds a brand-new Continue cell, but hands it the CALLER'S OWN
        // `st` parameter unchanged as the nested State field, rather than a new S(...) construction.
        // Trusting this as fresh would let a caller's defensive copy be skipped for a value that is
        // still exactly the parameter it was handed — a genuine aliasing hazard structurally
        // identical to Rewrapping_a_match_extracted_field_is_never_treated_as_fresh, just through the
        // new TCO-shaped nested-field path instead of the record path.
        const string source =
            """
            type State =
                | S(List(Int), List(Int))
            type Step =
                | Done
                | Continue(State, Int)
            let make st r = Continue(st)(r)
            in make(S([1])([2]))(1)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("make");

        summary.ShouldNotBeNull();
        summary.ResultProvenance.RcEligible.ShouldBeFalse();
        summary.ResultProvenance.ForwardsTo.ShouldBeNull();
    }

    [Test]
    public void Recursive_group_members_and_following_declarations_receive_ownership_summaries()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let recursive forward seed = fresh(seed)
            and fresh ignored = Full(Empty)
            let after seed = forward(seed)
            in after(Empty)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? forward = lowering.GetOwnershipSummary("forward");
        FunctionOwnershipSummary? fresh = lowering.GetOwnershipSummary("fresh");
        FunctionOwnershipSummary? after = lowering.GetOwnershipSummary("after");

        forward.ShouldNotBeNull();
        forward.Parameters.ShouldBe(["seed"]);
        forward.ResultFresh.ShouldBeTrue();
        forward.ResultProvenance.ForwardsTo.ShouldBe("fresh");
        forward.ResultProvenance.RcEligible.ShouldBeTrue();

        fresh.ShouldNotBeNull();
        fresh.Parameters.ShouldBe(["ignored"]);
        fresh.ResultFresh.ShouldBeTrue();
        fresh.ResultProvenance.ForwardsTo.ShouldBeNull();
        fresh.ResultProvenance.RcEligible.ShouldBeTrue();

        after.ShouldNotBeNull();
        after.Parameters.ShouldBe(["seed"]);
        after.ResultFresh.ShouldBeTrue();
        after.ResultProvenance.ForwardsTo.ShouldBe("forward");
        after.ResultProvenance.RcEligible.ShouldBeTrue();
    }

    [Test]
    public void Recursive_group_result_reach_converges_across_member_calls()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let recursive first value =
                match value with
                    | Empty -> value
                    | Full(_) -> second(value)
            and second value =
                match value with
                    | Empty -> value
                    | Full(_) -> first(value)
            first(Empty)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        first.ShouldNotBeNull();
        first.ResultPoisoned.ShouldBeFalse();
        first.ResultReaches("value").ShouldBeTrue();

        second.ShouldNotBeNull();
        second.ResultPoisoned.ShouldBeFalse();
        second.ResultReaches("value").ShouldBeTrue();
    }
}
