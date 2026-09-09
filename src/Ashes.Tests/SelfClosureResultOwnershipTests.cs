using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Regression coverage for the returns bit a recursive function's own call sites read. Those sites
/// build their callee closure from <c>Binding.Self</c> while the body is still being lowered, before
/// the body's runtime-managed verdict exists, so every self-closure was emitted with the bit clear.
/// The call site reads that bit at run time and takes the copy-out branch when it is clear — for a
/// non-tail recursive producer, one copy of the whole result per recursion level, which is quadratic
/// in the result's size. The verdict is written back into the closure instruction once it is known.
/// </summary>
public sealed class SelfClosureResultOwnershipTests
{
    // A non-tail recursive producer of a reference-counted result: each level concatenates onto the
    // result of the level below, so the caller-visible result is runtime-managed.
    private const string NonTailStringProducer = """
        let recursive rep (n: Int) =
            if n == 0
            then ""
            else "x" + rep(n - 1)

        rep(3)
        """;

    // The same shape over a list the arm rebuilds rather than a string.
    private const string NonTailListJoiner = """
        let recursive joinAll (values: List(Str)) =
            match values with
                | [] -> ""
                | value :: rest -> value + joinAll(rest)

        joinAll(["a", "b"])
        """;

    [Test]
    public void Self_closure_reports_the_runtime_managed_result_its_own_body_produces()
    {
        var ir = LowerProgram(NonTailStringProducer);

        IrFunction producer = FunctionWithSelfClosure(ir);
        producer.Instructions
            .OfType<IrInst.MakeClosure>()
            .Where(c => string.Equals(c.FuncLabel, producer.Label, StringComparison.Ordinal))
            .ShouldAllBe(c => c.ReturnsRuntimeManaged);
    }

    [Test]
    public void Self_closure_in_a_match_arm_rebuild_reports_it_too()
    {
        var ir = LowerProgram(NonTailListJoiner);

        IrFunction producer = FunctionWithSelfClosure(ir);
        producer.Instructions
            .OfType<IrInst.MakeClosure>()
            .Where(c => string.Equals(c.FuncLabel, producer.Label, StringComparison.Ordinal))
            .ShouldAllBe(c => c.ReturnsRuntimeManaged);
    }

    [Test]
    public void An_arena_returning_recursive_function_keeps_the_bit_clear()
    {
        // The control: this producer's cells stay arena-placed, so its self-closure must NOT claim a
        // reference-counted result — the call site's copy-out is what keeps that result alive past
        // the call's own arena window.
        var ir = LowerProgram("""
            let recursive makeList (count: Int) =
                if count == 0
                then []
                else 7 :: makeList(count - 1)

            let recursive sumList (values: List(Int)) (total: Int) =
                match values with
                    | [] -> total
                    | value :: rest -> sumList(rest)(total + value)

            sumList(makeList(3))(0)
            """);

        IrFunction producer = FunctionWithSelfClosure(ir);
        producer.Instructions
            .OfType<IrInst.MakeClosure>()
            .Where(c => string.Equals(c.FuncLabel, producer.Label, StringComparison.Ordinal))
            .ShouldAllBe(c => !c.ReturnsRuntimeManaged);
    }

    // --- Helpers ---

    private static IrFunction FunctionWithSelfClosure(IrProgram program)
    {
        IrFunction? found = program.Functions.FirstOrDefault(f => f.Instructions
            .OfType<IrInst.MakeClosure>()
            .Any(c => string.Equals(c.FuncLabel, f.Label, StringComparison.Ordinal)));
        found.ShouldNotBeNull();
        return found;
    }

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var parsed = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var lowering = new Lowering(diagnostics);
        IrProgram ir = lowering.Lower(parsed);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
