using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers mutual-recursion TCO: an eligible <c>let recursive … and …</c> group (function members with
/// a cross-member tail call, whose differing parameter types all have a default value) is compiled to
/// a single self-recursive dispatch function so the existing single-function TCO collapses it into
/// one loop. Ineligible groups fall back to the closure-based lowering.
/// </summary>
public sealed class MutualRecursionTcoTests
{
    [Test]
    public void SameTypeMutualGroup_SynthesizesDispatchFunction()
    {
        var ir = LowerProgram(
            """
            let recursive isEven n =
                match n with
                    | 0 -> true
                    | _ -> isOdd(n - 1)
            and isOdd n =
                match n with
                    | 0 -> false
                    | _ -> isEven(n - 1)

            isEven(10)
            """);

        DispatchFunctions(ir).Count.ShouldBe(1, "an eligible mutual group should produce one dispatch function");
    }

    [Test]
    public void HeterogeneousParamTypes_SynthesizesDispatchWithOneSlotPerType()
    {
        // ping : Int -> Str, pong : Str -> Str. The single parameter position has two distinct types,
        // both with a default value, so the dispatch carries one slot per type (plus the tag) instead
        // of falling back to the closure path.
        var ir = LowerProgram(
            """
            let recursive ping n =
                match n with
                    | 0 -> "done"
                    | _ -> pong("step")
            and pong s = ping(0)

            ping(3)
            """);

        DispatchFunctions(ir).Count.ShouldBe(1, "heterogeneous parameter types with defaults should merge");
        // The source has no empty-string literal; the only way one reaches the literal table is the
        // synthesized default a call into the Int member fills the Str member's slot with.
        ir.StringLiterals.ShouldContain(literal => literal.Value.Length == 0,
            "a call into the Int member must fill the Str member's slot with the empty-string default");
    }

    [Test]
    public void DifferingArity_SynthesizesDispatch()
    {
        // countDown : Int -> List(Int), collect : Int -> List(Int) -> List(Int). Position 0 is shared;
        // position 1 exists only for collect and is a list, whose default is the empty list.
        var ir = LowerProgram(
            """
            let recursive countDown = given (n: Int) ->
                if n <= 0 then [] else collect(n - 1)([n])
            and collect = given (n: Int) -> given (acc: List(Int)) ->
                if n <= 0 then acc else countDown(n - 1)

            countDown(5)
            """);

        DispatchFunctions(ir).Count.ShouldBe(1, "members of differing arity should merge when the extra slot has a default");
    }

    [Test]
    public void HeterogeneousSlotWithoutDefault_FallsBackToClosures()
    {
        // The second position differs between a user-declared type and Int; a user-declared type has
        // no constructible default to fill the inactive slot with, so the group keeps the closure path.
        var ir = LowerProgram(
            """
            type Shape =
                | Circle(Int)
                | Square(Int)

            let recursive measure = given (n: Int) -> given (s: Shape) ->
                if n <= 0 then 0 else count(n - 1)(n)
            and count = given (n: Int) -> given (k: Int) ->
                if n <= 0 then k else measure(n - 1)(Circle(k))

            measure(4)(Square(9))
            """);

        DispatchFunctions(ir).ShouldBeEmpty("a non-shared slot without a default value must keep the closure path");
    }

    [Test]
    public void DifferingResultTypes_FallsBackToClosures()
    {
        // a and b tail-call each other and return Int; c returns Str. Merging would put all three
        // bodies into one match whose arms cannot unify, so the group must decline (and compile).
        var ir = LowerProgram(
            """
            let recursive a = given (n: Int) -> if n <= 0 then 0 else b(n - 1)
            and b = given (n: Int) -> if n <= 0 then 1 else a(n - 1)
            and c = given (n: Int) -> "s"

            c(a(10))
            """);

        DispatchFunctions(ir).ShouldBeEmpty("members with differing result types must keep the closure path");
    }

    [Test]
    public void NonMutualSelfRecursion_DoesNotSynthesizeDispatch()
    {
        // Two members that never tail-call each other: single-function TCO already suffices, so the
        // mutual-recursion transform should not engage.
        var ir = LowerProgram(
            """
            let recursive countDown n =
                match n with
                    | 0 -> 0
                    | _ -> countDown(n - 1)
            and identity n = n

            countDown(5)
            """);

        DispatchFunctions(ir).ShouldBeEmpty("a group with no cross-member tail call needs no dispatch loop");
    }

    private static List<IrFunction> DispatchFunctions(IrProgram program)
    {
        return program.Functions
            .Where(f => f.Label.StartsWith("__recgroup_dispatch", StringComparison.Ordinal))
            .ToList();
    }

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
