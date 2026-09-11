using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Regression coverage for the two decisions that keep a non-tail recursive producer's memory linear.
/// Its call sites open an arena window each, and a window whose result is arena-placed is torn down
/// by copying that result out first — one copy of the whole list built below, at every level. Both
/// decisions exist to make that copy unnecessary rather than to remove the window, which is load
/// bearing: the window is where an arena result is normalized to reference-counted, and every
/// enclosing bracket is emitted assuming that already happened.
/// The first is the returns bit those call sites read. They build the callee closure from
/// <c>Binding.Self</c> while the body is still being lowered, before its runtime-managed verdict
/// exists, so every self-closure was emitted with the bit clear and always took the copy branch; the
/// verdict is written back into the instruction once known. The second is the placement of the
/// producer's own spine cells, which are requested on the reference-counted heap exactly when the
/// cons tail is a call to a member of the enclosing recursive group.
/// </summary>
public sealed class NonTailRecursiveProducerTests
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
        // The control: this producer builds an ADT whose cells stay arena-placed — the recursive
        // result is wrapped in a list literal rather than consed onto, so it is not the spine of a
        // recursive list producer and keeps the arena placement. Its self-closure must NOT claim a
        // reference-counted result: the call site's copy-out is what keeps that result alive past
        // the call's own arena window.
        var ir = LowerProgram("""
            type Node =
                | value: Int
                | next: List(Node)

            let recursive chain (n: Int) =
                if n == 0
                then Node(value = 0, next = [])
                else Node(value = n, next = [chain(n - 1)])

            let recursive depth (node: Node) (acc: Int) =
                match node.next with
                    | [] -> acc + node.value
                    | inner :: _rest -> depth(inner)(acc + node.value)

            depth(chain(3))(0)
            """);

        IrFunction producer = FunctionWithSelfClosure(ir);
        producer.Instructions
            .OfType<IrInst.MakeClosure>()
            .Where(c => string.Equals(c.FuncLabel, producer.Label, StringComparison.Ordinal))
            .ShouldAllBe(c => !c.ReturnsRuntimeManaged);
    }

    [Test]
    public void A_recursive_producers_spine_cells_are_reference_counted()
    {
        // The cons whose tail is the recursive call is the spine of a non-tail list producer. Its
        // cells go on the reference-counted heap so the call site's conditional copy-out is skipped
        // at run time; an arena cell would make every level copy the whole result out of its own
        // window before reclaiming it.
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

        // The producer is found by its source name rather than by a closure of itself: the
        // tail-modulo-constructor transform replaces that self-call with a back edge, so the
        // self-closure this test originally keyed on is exactly what is no longer emitted.
        IrFunction producer = FunctionFromSource(ir, "makeList");
        producer.Instructions
            .OfType<IrInst.Alloc>()
            .Where(a => a.SizeBytes == 16)
            .ShouldAllBe(a => a.RuntimeManaged);
    }

    [Test]
    public void A_recursive_producer_builds_its_spine_in_a_loop_instead_of_recursing()
    {
        // Tail modulo constructor: `7 :: makeList(...)` becomes one cell per iteration plus a jump
        // back to the loop body, so the pending constructor no longer holds a native frame per
        // element. The cell's tail is stored as nil first, which is what keeps every intermediate
        // state of the spine a complete, walkable list.
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

        IrFunction producer = FunctionFromSource(ir, "makeList");
        string bodyLabel = producer.Instructions
            .OfType<IrInst.Label>()
            .Select(l => l.Name)
            .FirstOrDefault(name => name.EndsWith("_body", StringComparison.Ordinal))
            .ShouldNotBeNull();
        producer.Instructions
            .OfType<IrInst.Jump>()
            .ShouldContain(j => string.Equals(j.Target, bodyLabel, StringComparison.Ordinal));
        producer.Instructions
            .OfType<IrInst.MakeClosure>()
            .ShouldNotContain(c => string.Equals(c.FuncLabel, producer.Label, StringComparison.Ordinal));
        int tailOffset = HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListTailIndex);
        int cellTemp = producer.Instructions
            .OfType<IrInst.Alloc>()
            .First(a => a.SizeBytes == 16)
            .Target;
        int nilTemp = producer.Instructions
            .OfType<IrInst.StoreMemOffset>()
            .First(s => s.BasePtr == cellTemp && s.OffsetBytes == tailOffset)
            .Source;
        producer.Instructions
            .OfType<IrInst.LoadConstInt>()
            .ShouldContain(c => c.Target == nilTemp && c.Value == 0);
    }

    [Test]
    public void A_cons_over_a_plain_tail_stays_in_the_arena()
    {
        // The control for the rule above: this cons's tail is an ordinary parameter, not a call to a
        // member of the enclosing recursive group, so nothing forces the spine onto the
        // reference-counted heap and the arena placement stands.
        var ir = LowerProgram("""
            let prepend (value: Int) (rest: List(Int)) =
                value :: rest

            let recursive sumList (values: List(Int)) (total: Int) =
                match values with
                    | [] -> total
                    | value :: rest -> sumList(rest)(total + value)

            sumList(prepend(1)([2, 3]))(0)
            """);

        List<IrInst.Alloc> cells = ir.Functions
            .Where(f => f.Origin?.Source?.SourceName is "prepend")
            .SelectMany(f => f.Instructions)
            .OfType<IrInst.Alloc>()
            .Where(a => a.SizeBytes == 16)
            .ToList();
        cells.ShouldNotBeEmpty();
        cells.ShouldAllBe(a => !a.RuntimeManaged);
    }

    [Test]
    [Arguments("let rest = makeList(count - 1) in 7 :: rest")]
    [Arguments("let rest = makeList(count - 1) in let alias = rest in 7 :: alias")]
    [Arguments("let rest = if count > 2 then makeList(count - 1) else makeList(0) in 7 :: rest")]
    [Arguments("let rest = match count with | 1 -> makeList(0) | _ -> makeList(count - 1) in 7 :: rest")]
    public void Recursive_result_aliases_keep_their_spine_owned(string body)
    {
        IrProgram program = LowerProgram($$"""
            let recursive makeList (count: Int) =
                if count == 0 then [] else {{body}}
            makeList(3)
            """);
        IrFunction producer = FunctionWithSelfClosure(program);
        List<IrInst.Alloc> cells = producer.Instructions.OfType<IrInst.Alloc>()
            .Where(allocation => allocation.SizeBytes == 16).ToList();
        cells.ShouldNotBeEmpty();
        cells.ShouldAllBe(allocation => allocation.RuntimeManaged);
    }

    [Test]
    [Arguments("let rest = makeList(count - 1)(input) in let rest = input in 7 :: rest")]
    [Arguments("let rest = if count > 2 then makeList(count - 1)(input) else input in 7 :: rest")]
    [Arguments("let rest = makeList(count - 1)(input) in match input with | rest -> 7 :: rest")]
    public void Unproven_tails_do_not_inherit_producer_provenance(string body)
    {
        IrProgram program = LowerProgram($$"""
            let recursive makeList (count: Int) (input: List(Int)) =
                if count == 0 then []
                else {{body}}
            makeList(3)([1, 2])
            """);
        List<IrInst.Alloc> cells = program.Functions
            .Where(function => function.Origin?.Source?.SourceName is "makeList")
            .SelectMany(function => function.Instructions).OfType<IrInst.Alloc>()
            .Where(allocation => allocation.SizeBytes == 16).ToList();
        cells.ShouldNotBeEmpty();
        cells.ShouldContain(allocation => !allocation.RuntimeManaged);
    }

    // --- Helpers ---

    private static IrFunction FunctionFromSource(IrProgram program, string sourceName)
    {
        IrFunction? found = program.Functions.FirstOrDefault(f =>
            string.Equals(f.Origin?.Source?.SourceName, sourceName, StringComparison.Ordinal));
        found.ShouldNotBeNull();
        return found;
    }

    private static IrFunction FunctionWithSelfClosure(IrProgram program)
    {
        IrFunction? found = program.Functions.FirstOrDefault(f => f.Instructions
            .OfType<IrInst.MakeClosure>()
            .Any(c => string.Equals(c.FuncLabel, f.Label, StringComparison.Ordinal)));
        found.ShouldNotBeNull();
        return found;
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram program)
        => program.EntryFunction.Instructions.Concat(program.Functions.SelectMany(f => f.Instructions));

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
