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
