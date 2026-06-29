using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers mutual-recursion TCO: an eligible <c>let rec … and …</c> group (same arity, identical
/// parameter types, a cross-member tail call) is compiled to a single self-recursive dispatch
/// function so the existing single-function TCO collapses it into one loop. Ineligible groups fall
/// back to the closure-based lowering.
/// </summary>
public sealed class MutualRecursionTcoTests
{
    [Test]
    public void SameTypeMutualGroup_SynthesizesDispatchFunction()
    {
        var ir = LowerProgram(
            """
            let rec isEven n =
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
    public void HeterogeneousParamTypes_FallsBackToClosures()
    {
        // ping : Int -> Str, pong : Str -> Str. Mutually tail-recursive but the shared parameter
        // would have two different types, so the merge is unsafe and must be skipped.
        var ir = LowerProgram(
            """
            let rec ping n =
                match n with
                    | 0 -> "done"
                    | _ -> pong("step")
            and pong s = ping(0)

            ping(3)
            """);

        DispatchFunctions(ir).ShouldBeEmpty("heterogeneous parameter types must keep the closure path");
    }

    [Test]
    public void NonMutualSelfRecursion_DoesNotSynthesizeDispatch()
    {
        // Two members that never tail-call each other: single-function TCO already suffices, so the
        // mutual-recursion transform should not engage.
        var ir = LowerProgram(
            """
            let rec countDown n =
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
