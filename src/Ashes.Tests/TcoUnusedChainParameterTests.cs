using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// A curried self-recursive function whose loop body never reads one of its chain parameters: the
// innermost lambda captures nothing for it, so the parameter has no local of its own, while the
// tail self-call still passes an argument at its ordinal. The back edge stores that argument into
// a synthetic slot instead of failing the lowering. See tests/tco_unused_chain_parameter.ash for
// the compiled and run counterpart.
public sealed class TcoUnusedChainParameterTests
{
    [Test]
    public void Chain_parameter_the_loop_body_never_reads_gets_a_synthetic_back_edge_slot()
    {
        IrProgram program = LowerProgram("""
            let recursive f n acc =
                if acc == 0
                then 0
                else f(0)(acc - 1)

            Ashes.IO.print(Ashes.Text.fromInt(f(3)(5)))
            """);

        IrFunction loop = program.Functions.Single(function => function.Instructions
            .Any(instruction => instruction is IrInst.Jump jump
                && jump.Target.EndsWith("_body", StringComparison.Ordinal)));

        int backEdge = loop.Instructions.ToList().FindIndex(instruction => instruction is IrInst.Jump jump
            && jump.Target.EndsWith("_body", StringComparison.Ordinal));
        List<int> storedSlots = loop.Instructions
            .Take(backEdge)
            .OfType<IrInst.StoreLocal>()
            .Select(store => store.Slot)
            .Distinct()
            .ToList();
        HashSet<int> readSlots = loop.Instructions
            .OfType<IrInst.LoadLocal>()
            .Select(load => load.Slot)
            .ToHashSet();

        // The argument slot (acc) is read by the body; the synthetic slot for `n` is stored at the
        // entry and the back edge but read nowhere.
        storedSlots.ShouldContain(1);
        storedSlots.ShouldContain(slot => slot != 1 && !readSlots.Contains(slot),
            "The unread chain parameter must own a back-edge slot no instruction reads.");
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
