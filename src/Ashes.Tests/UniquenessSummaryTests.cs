using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// The per-function ownership summary is a first-class, queryable read-view over the move-safety
// (GFP), result-reach (LFP), borrow inference, and closure-capture facts. These tests pin the
// observable summary for representative shapes; they assert the property is exposed faithfully,
// not any new lowering behaviour (the first RC Perceus slice is behaviour-preserving).
public sealed class UniquenessSummaryTests
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

    // A small program with a fresh builder, an identity, a copy-field wrapper, and a fold accumulator.
    private const string Sample =
        """
        type Box =
            | Empty
            | Full(Int, Box)
        let fresh u = Empty
        let wrap x = Full(x)(Empty)
        let idish x = x
        let recursive count n acc =
            if n <= 0
            then acc
            else count(n - 1)(Full(n)(acc))
        in count(3)(Empty)
        """;

    [Test]
    public void Nullary_constructor_body_is_result_fresh()
    {
        var s = LowerProgram(Sample).GetOwnershipSummary("fresh");
        s.ShouldNotBeNull();
        s.ResultFresh.ShouldBeTrue();
        s.ResultPoisoned.ShouldBeFalse();
        s.ResultReach.ShouldBeEmpty();
    }

    [Test]
    public void Identity_result_reaches_its_parameter()
    {
        var s = LowerProgram(Sample).GetOwnershipSummary("idish");
        s.ShouldNotBeNull();
        s.ResultReaches("x").ShouldBeTrue();
        s.ResultFresh.ShouldBeFalse();
    }

    [Test]
    public void Wrapping_only_copy_type_fields_stays_result_fresh()
    {
        // Full's first field is Int (a copy type): it is inlined into the cell, so the result does not
        // alias x — a genuine aliasing distinction the summary must preserve.
        var s = LowerProgram(Sample).GetOwnershipSummary("wrap");
        s.ShouldNotBeNull();
        s.ResultFresh.ShouldBeTrue();
        s.ResultReaches("x").ShouldBeFalse();
    }

    [Test]
    public void Record_literal_copy_fields_do_not_alias_their_parameters()
    {
        const string source =
            """
            type Body =
                | x: Float
                | velocity: Float

            let move dt body =
                match body with
                    | Body(x, velocity) -> Body(x = x + dt * velocity, velocity = velocity)
            in move(1.0)(Body(x = 0.0, velocity = 2.0))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("move");

        summary.ShouldNotBeNull();
        summary.ResultFresh.ShouldBeTrue();
        summary.ResultReaches("dt").ShouldBeFalse();
        summary.ResultReaches("body").ShouldBeFalse();
    }

    [Test]
    public void Fold_accumulator_is_unique_and_reached_by_the_result()
    {
        var s = LowerProgram(Sample).GetOwnershipSummary("count");
        s.ShouldNotBeNull();
        s.Parameters.ShouldBe(["n", "acc"]);
        s.UniqueParameters.ShouldContain("acc"); // the accumulator is proven uniquely owned
        s.ResultReaches("acc").ShouldBeTrue(); // the fold returns its accumulator
    }

    [Test]
    public void Unknown_function_has_no_summary()
    {
        LowerProgram(Sample).GetOwnershipSummary("nope").ShouldBeNull();
    }

    [Test]
    public void Registered_user_functions_are_enumerable()
    {
        var names = LowerProgram(Sample).AnalyzedFunctionNames;
        names.ShouldContain("fresh");
        names.ShouldContain("idish");
        names.ShouldContain("count");
    }

    [Test]
    public void Read_only_resource_parameter_is_borrowed_while_close_consumes()
    {
        const string source =
            """
            let peek h = Ashes.IO.File.readChunk(h)(2)
            let closeIt h = Ashes.IO.File.close(h)
            in 0
            """;

        var lowering = LowerProgram(source);
        var peek = lowering.GetOwnershipSummary("peek");
        var closeIt = lowering.GetOwnershipSummary("closeIt");

        peek.ShouldNotBeNull();
        peek.ParameterOwnership["h"].ShouldBe(ParameterOwnership.Borrowed);
        peek.BorrowedParameters.ShouldBe(["h"]);
        peek.ConsumedParameters.ShouldBeEmpty();

        closeIt.ShouldNotBeNull();
        closeIt.ParameterOwnership["h"].ShouldBe(ParameterOwnership.Consumed);
        closeIt.ConsumedParameters.ShouldBe(["h"]);
    }

    [Test]
    public void Closure_capture_is_part_of_the_ownership_contract()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let seed = Full(Empty)
            let capture ignored = given value -> Full(seed)
            in capture(0)(Empty)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("capture");

        summary.ShouldNotBeNull();
        summary.CapturedValues.ShouldBe(["seed"]);
        summary.ResultPoisoned.ShouldBeTrue();
    }

    [Test]
    public void Result_can_alias_multiple_parameters_without_internal_sharing()
    {
        const string source =
            """
            type Pair =
                | Pair(Pair, Pair)
                | Empty
            let pair left right = Pair(left)(right)
            in pair(Empty)(Empty)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("pair");

        summary.ShouldNotBeNull();
        summary.ResultReach.Keys.ShouldBe(["left", "right"], ignoreOrder: true);
        summary.ResultPoisoned.ShouldBeFalse();
    }

    [Test]
    public void Direct_call_substitutes_the_callee_result_reach()
    {
        const string source =
            """
            let identity value = value
            let direct value = identity(value)
            in direct(1)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("direct");

        summary.ShouldNotBeNull();
        summary.ResultReaches("value").ShouldBeTrue();
        summary.ResultPoisoned.ShouldBeFalse();
    }

    [Test]
    public void Higher_order_call_remains_conservatively_poisoned()
    {
        const string source =
            """
            let apply f value = f(value)
            let identity value = value
            in apply(identity)(1)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("apply");

        summary.ShouldNotBeNull();
        summary.ResultPoisoned.ShouldBeTrue();
        summary.ConsumedParameters.ShouldBe(["f", "value"]);
    }

    [Test]
    public void Function_parameter_shadow_does_not_pollute_outer_call_site_census()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let target value = value
            let invoke target value = target(value)
            in target(Full(Empty))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void Pattern_binding_shadow_does_not_escape_outer_function()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            type Carrier =
                | Vacant
                | Carry(Box)
            let target value = value
            let inspect wrapped =
                match wrapped with
                    | Vacant -> Empty
                    | Carry(target) -> target
            in target(Full(Empty))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void Recursive_binding_is_visible_in_its_value_for_escape_tracking()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let apply f value = f(value)
            let recursive target value = apply(target)(value)
            in target(Full(Empty))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldNotContain("value");
    }

    [Test]
    public void Partial_application_alias_preserves_visible_callee_census()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let recursive fill n acc =
                if n <= 0
                then acc
                else fill(n - 1)(Full(acc))
            let finish = fill(2)
            in finish(Empty)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("fill");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldContain("acc");
    }

    [Test]
    public void Nested_recursive_return_parameters_shadow_outer_functions_in_census()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let outerFn value = value
            let accFn value = value
            let set outerFn =
                (let recursive go accFn =
                    match accFn with
                        | Empty -> outerFn
                        | Full(rest) -> go(rest)
                in go)
            let observed = set(Empty)(Full(Empty))
            in (outerFn(Full(Empty)), accFn(Full(Empty)), observed)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? outer = lowering.GetOwnershipSummary("outerFn");
        FunctionOwnershipSummary? accumulator = lowering.GetOwnershipSummary("accFn");

        outer.ShouldNotBeNull();
        accumulator.ShouldNotBeNull();
        outer.UniqueParameters.ShouldContain("value");
        accumulator.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void Alias_stripped_function_body_preserves_nested_binder_identity()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let target value = value
            let wrapper =
                let targetAlias = target
                in given seed ->
                    let recursive loop current = targetAlias(current)
                    in loop(seed)
            in wrapper(Full(Empty))
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? target = lowering.GetOwnershipSummary("target");
        FunctionOwnershipSummary? loop = lowering.GetOwnershipSummary("loop");

        target.ShouldNotBeNull();
        loop.ShouldNotBeNull();
        target.UniqueParameters.ShouldContain("value");
        loop.UniqueParameters.ShouldContain("current");
    }

    [Test]
    public void Function_parameter_shadow_keeps_result_reach_conservative()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let fresh value = Full(Empty)
            let invoke fresh value = fresh(value)
            in invoke(given item -> item)(Empty)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("invoke");

        summary.ShouldNotBeNull();
        summary.ResultPoisoned.ShouldBeTrue();
        summary.ResultFresh.ShouldBeFalse();
    }

    [Test]
    public void Pattern_function_shadow_keeps_result_reach_conservative()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            type Holder(A) =
                | Hold(A)
            let fresh value = Full(Empty)
            let invoke holder =
                match holder with
                    | Hold(fresh) -> fresh(Empty)
            in invoke(Hold(given item -> item))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("invoke");

        summary.ShouldNotBeNull();
        summary.ResultPoisoned.ShouldBeTrue();
        summary.ResultFresh.ShouldBeFalse();
    }

    [Test]
    public void Shadowed_result_builder_does_not_make_call_argument_a_move()
    {
        const string source =
            """
            let make ignored = 42
            let target value = value
            let invoke make seed = target(make(seed))
            in invoke(make)(0)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldNotContain("value");
    }

    [Test]
    public void Sequential_local_result_builder_can_prove_call_argument_move()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let target value = value
            let invoke seed =
                let make item = Full(item)
                in target(make(seed))
            in invoke(Empty)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void Partial_application_uses_completion_site_scope_for_move_arguments()
    {
        const string source =
            """
            let target first second = second
            let partial = target(0)
            let make ignored = 42
            in partial(make(0))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("target");

        summary.ShouldNotBeNull();
        summary.UniqueParameters.ShouldContain("second");
    }

    [Test]
    public void Colliding_nested_recursive_names_keep_exact_outer_result_reach()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let first replacement =
                (let recursive go current =
                    match current with
                        | Empty -> replacement
                        | Full(rest) -> go(rest)
                in go)
            let second replacement =
                (let recursive go current =
                    match current with
                        | Empty -> replacement
                        | Full(rest) -> go(rest)
                in go)
            in (first(Empty)(Full(Empty)), second(Empty)(Full(Empty)))
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first.ResultPoisoned.ShouldBeFalse();
        second.ResultPoisoned.ShouldBeFalse();
        first.ResultReaches("replacement").ShouldBeTrue();
        second.ResultReaches("replacement").ShouldBeTrue();
    }

    [Test]
    public void Same_named_local_helpers_do_not_make_their_callers_conservative()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let first seed =
                let go value = value
                in go(seed)
            let second seed =
                let go value = value
                in go(seed)
            in (first(Empty), second(Empty))
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? first = lowering.GetOwnershipSummary("first");
        FunctionOwnershipSummary? second = lowering.GetOwnershipSummary("second");

        lowering.GetOwnershipSummaries("go").Count.ShouldBe(2);
        first.ShouldNotBeNull();
        first.ResultPoisoned.ShouldBeFalse();
        first.ResultReaches("seed").ShouldBeTrue();
        second.ShouldNotBeNull();
        second.ResultPoisoned.ShouldBeFalse();
        second.ResultReaches("seed").ShouldBeTrue();
    }

    [Test]
    public void Nested_recursive_helper_gets_its_own_self_scope()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let maker ignored =
                (let recursive uniqueGo current =
                    if true
                    then current
                    else uniqueGo(current)
                in uniqueGo)
            in maker(0)(Full(Empty))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("uniqueGo");

        summary.ShouldNotBeNull();
        summary.ResultPoisoned.ShouldBeFalse();
        summary.ResultReaches("current").ShouldBeTrue();
    }

    [Test]
    public void Pattern_shadowed_self_name_does_not_create_tco_parameter_facts()
    {
        const string source =
            """
            type Holder(A) =
                | Hold(A)
            let recursive loop state =
                match state with
                    | Hold(loop) -> loop(0)
            in loop(Hold(given value -> value))
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts.ShouldBeEmpty();
    }

    [Test]
    public void Plain_local_function_calling_outer_same_named_function_is_not_self_recursive()
    {
        const string source =
            """
            let go outerValue = outerValue
            let caller value =
                let go innerValue = go(innerValue)
                in go(value)
            in caller(42)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary inner = lowering.GetOwnershipSummaries("go")
            .Single(summary => summary.Parameters.Contains("innerValue", StringComparer.Ordinal));

        inner.TcoParamFacts.ShouldBeEmpty();
    }

    [Test]
    public void Let_shadowed_consumed_tail_is_not_classified_as_the_original_tail()
    {
        const string source =
            """
            let recursive loop values replacement =
                match values with
                    | [] -> replacement
                    | _ :: rest ->
                        let rest = replacement
                        in loop(rest)(replacement)
            in loop([1])([])
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        summary.TcoParamFacts[0].ArenaSelfContainedListRebuild.ShouldBeFalse();
    }

    [Test]
    public void Exact_parameter_passthrough_is_classified_by_parameter_ordinal()
    {
        const string source =
            """
            let recursive loop stable remaining =
                if remaining <= 0
                then stable
                else loop(stable)(remaining - 1)
            in loop([1])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts stable = summary.TcoParamFacts[0];
        stable.ParameterOrdinal.ShouldBe(0);
        stable.ParameterName.ShouldBe("stable");
        stable.Shape.ShouldBe(TcoSelfCallArgumentShape.UnchangedPassthrough);
    }

    [Test]
    public void Let_shadowed_parameter_is_not_classified_as_unchanged_passthrough()
    {
        const string source =
            """
            let recursive loop stable remaining =
                if remaining <= 0
                then stable
                else
                    let stable = [remaining]
                    in loop(stable)(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
    }

    [Test]
    public void Pattern_shadowed_parameter_is_not_classified_as_unchanged_passthrough()
    {
        const string source =
            """
            type Holder(A) =
                | Hold(A)
            let recursive loop stable holder remaining =
                if remaining <= 0
                then stable
                else
                    match holder with
                        | Hold(stable) -> loop(stable)(holder)(remaining - 1)
            in loop([])(Hold([1]))(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
    }

    [Test]
    public void Duplicate_parameter_names_retain_distinct_positional_facts()
    {
        const string source =
            """
            let recursive loop value value remaining =
                if remaining <= 0
                then value
                else loop([remaining])(value)(remaining - 1)
            in loop([])([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts.Count.ShouldBe(3);
        summary.TcoParamFacts[0].ParameterOrdinal.ShouldBe(0);
        summary.TcoParamFacts[0].Shape.ShouldNotBe(TcoSelfCallArgumentShape.UnchangedPassthrough);
        summary.TcoParamFacts[1].ParameterOrdinal.ShouldBe(1);
        summary.TcoParamFacts[1].Shape.ShouldBe(TcoSelfCallArgumentShape.UnchangedPassthrough);
    }

    [Test]
    public void Unchanged_passthrough_requires_every_exact_self_call_site()
    {
        const string source =
            """
            let recursive loop stable remaining =
                if remaining <= 0
                then stable
                else if remaining == 1
                then loop(stable)(remaining - 1)
                else
                    let stable = [remaining]
                    in loop(stable)(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
    }

    [Test]
    public void HelperResultCanBeArenaSelfContainedWithoutBeingReferenceFresh()
    {
        const string source =
            """
            let rebuild head tail = head :: tail
            let recursive loop values remaining =
                if remaining <= 0
                then values
                else loop(rebuild(remaining)(values))(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        values.ArenaSelfContainedListRebuild.ShouldBeTrue();
    }

    [Test]
    public void ConsSpineEndingInAHelperResultIsArenaSelfContainedWithoutBeingReferenceFresh()
    {
        const string source =
            """
            let rebuild head tail = head :: tail
            let recursive loop values remaining =
                if remaining <= 0
                then values
                else loop(0 :: rebuild(remaining)(values))(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        values.ArenaSelfContainedListRebuild.ShouldBeTrue();
    }

    [Test]
    public void HigherOrderCallResultRetainsTheLiveArenaSelfContainmentContract()
    {
        const string source =
            """
            let recursive loop transform values remaining =
                if remaining <= 0
                then values
                else loop(transform)(transform(values))(remaining - 1)
            in loop(given value -> value)([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[1];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        values.ArenaSelfContainedListRebuild.ShouldBeTrue();
    }

    [Test]
    public void FreshListLiteralIsBothReferenceFreshAndArenaSelfContained()
    {
        const string source =
            """
            let recursive loop values remaining =
                if remaining <= 0
                then values
                else loop([1])(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.FreshRebuilt);
        values.ArenaSelfContainedListRebuild.ShouldBeTrue();
        values.FreshClosureRebuild.ShouldBeFalse();
    }

    [Test]
    public void FreshClosureRebuildIsSeparateFromReferenceFreshness()
    {
        const string source =
            """
            let recursive loop fn remaining =
                if remaining <= 0
                then fn(0)
                else loop(given value -> value + remaining)(remaining - 1)
            in loop(given value -> value)(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts function = summary.TcoParamFacts[0];
        function.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed,
            "A closure that captures an input is not reference-fresh.");
        function.FreshClosureRebuild.ShouldBeTrue(
            "The closure allocation itself is rebuilt on every exact self edge.");
        function.ArenaSelfContainedListRebuild.ShouldBeFalse();
    }

    [Test]
    public void ConditionalFreshClosureRebuildAcceptsClosureConstructionInBothArms()
    {
        const string source =
            """
            let recursive loop fn remaining =
                if remaining <= 0
                then fn(0)
                else
                    loop(
                        if remaining > 1
                        then given value -> value + remaining
                        else given value -> value - remaining
                    )(remaining - 1)
            in loop(given value -> value)(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts function = summary.TcoParamFacts[0];
        function.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        function.FreshClosureRebuild.ShouldBeTrue();
    }

    [Test]
    public void ConditionalFreshClosureRebuildRejectsANonClosureArm()
    {
        const string source =
            """
            let recursive loop fn remaining =
                if remaining <= 0
                then fn(0)
                else
                    loop(
                        if remaining > 1
                        then given value -> value + remaining
                        else fn
                    )(remaining - 1)
            in loop(given value -> value)(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].FreshClosureRebuild.ShouldBeFalse();
    }

    [Test]
    public void ConsOntoThePreviousAccumulatorIsNotArenaSelfContained()
    {
        const string source =
            """
            let recursive loop values remaining =
                if remaining <= 0
                then values
                else loop(remaining :: values)(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.GrownCons);
        values.ArenaSelfContainedListRebuild.ShouldBeFalse();
    }

    [Test]
    public void Let_shadowed_cons_tail_is_not_classified_as_the_original_parameter()
    {
        const string source =
            """
            let recursive loop values replacement remaining =
                if remaining <= 0
                then values
                else
                    let values = replacement
                    in loop(remaining :: values)(replacement)(remaining - 1)
            in loop([])([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
    }

    [Test]
    public void Pattern_shadowed_cons_tail_is_not_classified_as_the_original_parameter()
    {
        const string source =
            """
            type Holder(A) =
                | Hold(A)
            let recursive loop values holder remaining =
                if remaining <= 0
                then values
                else
                    match holder with
                        | Hold(values) -> loop(remaining :: values)(holder)(remaining - 1)
            in loop([])(Hold([]))(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
    }

    [Test]
    public void Duplicate_parameter_name_cons_tail_resolves_to_the_live_ordinal()
    {
        const string source =
            """
            let recursive loop value value remaining =
                if remaining <= 0
                then value
                else loop(value)(remaining :: value)(remaining - 1)
            in loop([])([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        summary.TcoParamFacts[0].Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        summary.TcoParamFacts[1].Shape.ShouldBe(TcoSelfCallArgumentShape.GrownCons);
    }

    [Test]
    public void ArenaSelfContainmentRequiresEveryExactSelfCallArgumentToRebuild()
    {
        const string source =
            """
            let recursive loop values remaining =
                if remaining <= 0
                then values
                else if remaining == 1
                then loop([1])(remaining - 1)
                else loop(remaining :: values)(remaining - 1)
            in loop([])(2)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("loop");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.Mixed);
        values.ArenaSelfContainedListRebuild.ShouldBeFalse();
    }

    [Test]
    public void Consumed_tail_inspection_is_a_canonical_parameter_use_mode()
    {
        const string source =
            """
            type Body =
                | value: Int

            let recursive sum values total =
                match values with
                    | [] -> total
                    | body :: tail ->
                        match body with
                            | Body(value) -> sum(tail)(total + value)
            in sum([Body(value = 1), Body(value = 2)])(0)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("sum");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.ConsumedTail);
        values.UseMode.ShouldBe(TcoParamUseMode.BorrowInspectOnly);
    }

    [Test]
    public void Consumed_tail_escape_uses_the_conservative_parameter_mode()
    {
        const string source =
            """
            type Body =
                | value: Int

            let hasAny values =
                match values with
                    | [] -> false
                    | _ :: _ -> true

            let recursive sum values total =
                match values with
                    | [] -> total
                    | body :: tail ->
                        match body with
                            | Body(value) ->
                                if hasAny(tail)
                                then sum(tail)(total + value)
                                else total
            in sum([Body(value = 1), Body(value = 2)])(0)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("sum");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.ConsumedTail);
        values.UseMode.ShouldBe(TcoParamUseMode.GeneralOrUnknown);
    }

    [Test]
    public void Same_named_disjoint_pattern_binding_does_not_inherit_consumed_tail_ownership()
    {
        const string source =
            """
            type Body =
                | value: Int

            let recursive sum values fallback total =
                match values with
                    | [] -> total
                    | body :: tail ->
                        match body with
                            | Body(value) ->
                                if value > 0
                                then sum(tail)(fallback)(total + value)
                                else
                                    match fallback with
                                        | [] -> total
                                        | tail :: _ ->
                                            match tail with
                                                | Body(other) -> total + other
            in sum([Body(value = 1)])([Body(value = 2)])(0)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("sum");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.ConsumedTail);
        values.UseMode.ShouldBe(TcoParamUseMode.BorrowInspectOnly);
    }

    [Test]
    public void Same_named_let_binding_does_not_inherit_consumed_tail_ownership()
    {
        const string source =
            """
            type Body =
                | value: Int

            let recursive sum values total =
                match values with
                    | [] -> total
                    | body :: tail ->
                        match body with
                            | Body(value) ->
                                if value > 0
                                then sum(tail)(total + value)
                                else
                                    let tail = total
                                    in tail
            in sum([Body(value = 1)])(0)
            """;

        FunctionOwnershipSummary? summary = LowerProgram(source).GetOwnershipSummary("sum");

        summary.ShouldNotBeNull();
        TcoParamStructuralFacts values = summary.TcoParamFacts[0];
        values.Shape.ShouldBe(TcoSelfCallArgumentShape.ConsumedTail);
        values.UseMode.ShouldBe(TcoParamUseMode.BorrowInspectOnly);
    }

    [Test]
    public void Recursive_group_sibling_call_is_included_in_uniqueness_census()
    {
        const string source =
            """
            type Box =
                | Empty
                | Full(Box)
            let recursive caller ignored = target(Full(Empty))
            and target value = value
            caller(0)
            """;

        Lowering lowering = LowerProgram(source);
        FunctionOwnershipSummary? caller = lowering.GetOwnershipSummary("caller");
        FunctionOwnershipSummary? target = lowering.GetOwnershipSummary("target");

        caller.ShouldNotBeNull();
        target.ShouldNotBeNull();
        target.UniqueParameters.ShouldContain("value");
    }

    [Test]
    public void Nested_recursive_return_shape_carries_outer_and_accumulator_facts()
    {
        const string source =
            """
            type Tree =
                | Empty
                | Node(Tree, Tree)
            let set newValue =
                (let recursive go tree =
                    match tree with
                        | Empty -> Node(newValue)(Empty)
                        | Node(left, right) -> Node(go(left))(right)
                in go)
            in set(Empty)(Empty)
            """;

        var summary = LowerProgram(source).GetOwnershipSummary("set");

        summary.ShouldNotBeNull();
        summary.Parameters.ShouldBe(["newValue", "tree"]);
        summary.ResultReaches("newValue").ShouldBeTrue();
        summary.ResultReaches("tree").ShouldBeTrue();
        summary.ResultPoisoned.ShouldBeFalse();
    }

    [Test]
    public void Explain_output_is_stable_and_can_select_functions()
    {
        var lowering = LowerProgram(Sample);

        var lines = lowering.FormatOwnershipSummaries("fresh,idish");

        lines.Count.ShouldBe(2);
        lines[0].ShouldStartWith("[ownership] fresh(");
        lines[0].ShouldContain("result=fresh");
        lines[1].ShouldStartWith("[ownership] idish(");
        lines[1].ShouldContain("result=reaches{x}");
    }
}
