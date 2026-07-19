using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Increment 5, S1: the per-function uniqueness summary is a first-class, queryable read-view over the
// move-safety (GFP) and result-reach (LFP) results the reuse path already consumes. These tests pin
// the observable summary for representative shapes; they assert the property is exposed faithfully,
// not any new lowering behaviour (S1 is behaviour-preserving).
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
        var s = LowerProgram(Sample).GetUniquenessSummary("fresh");
        s.ShouldNotBeNull();
        s.ResultFresh.ShouldBeTrue();
        s.ResultPoisoned.ShouldBeFalse();
        s.ResultReach.ShouldBeEmpty();
    }

    [Test]
    public void Identity_result_reaches_its_parameter()
    {
        var s = LowerProgram(Sample).GetUniquenessSummary("idish");
        s.ShouldNotBeNull();
        s.ResultReaches("x").ShouldBeTrue();
        s.ResultFresh.ShouldBeFalse();
    }

    [Test]
    public void Wrapping_only_copy_type_fields_stays_result_fresh()
    {
        // Full's first field is Int (a copy type): it is inlined into the cell, so the result does not
        // alias x — a genuine aliasing distinction the summary must preserve.
        var s = LowerProgram(Sample).GetUniquenessSummary("wrap");
        s.ShouldNotBeNull();
        s.ResultFresh.ShouldBeTrue();
        s.ResultReaches("x").ShouldBeFalse();
    }

    [Test]
    public void Fold_accumulator_is_unique_and_reached_by_the_result()
    {
        var s = LowerProgram(Sample).GetUniquenessSummary("count");
        s.ShouldNotBeNull();
        s.Parameters.ShouldBe(["n", "acc"]);
        s.UniqueParameters.ShouldContain("acc"); // the accumulator is proven uniquely owned
        s.ResultReaches("acc").ShouldBeTrue(); // the fold returns its accumulator
    }

    [Test]
    public void Unknown_function_has_no_summary()
    {
        LowerProgram(Sample).GetUniquenessSummary("nope").ShouldBeNull();
    }

    [Test]
    public void Registered_user_functions_are_enumerable()
    {
        var names = LowerProgram(Sample).AnalyzedFunctionNames;
        names.ShouldContain("fresh");
        names.ShouldContain("idish");
        names.ShouldContain("count");
    }
}
