using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ReuseTokenTests
{
    [Test]
    public void Recursive_adt_accumulator_routes_alloc_reusing_through_drop_reuse()
    {
        IrProgram program = LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n tree =
                if n <= 0 then tree
                else
                    match tree with
                        | Leaf -> loop(n - 1)(Node(Leaf)(n)(Leaf))
                        | Node(left, value, right) -> loop(n - 1)(Node(left)(value + n)(right))

            let result = loop(3)(Node(Leaf)(5)(Leaf))
            match result with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) -> Ashes.IO.print(value)
            """);

        int reusingAllocations = 0;
        foreach (IrFunction function in program.Functions.Prepend(program.EntryFunction))
        {
            Dictionary<int, (IrInst.DropReuse Token, int Index)> tokens = function.Instructions
                .Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction is IrInst.DropReuse)
                .ToDictionary(
                    pair => ((IrInst.DropReuse)pair.instruction).Target,
                    pair => (((IrInst.DropReuse)pair.instruction), pair.index));

            foreach ((IrInst instruction, int index) in function.Instructions.Select((instruction, index) => (instruction, index)))
            {
                if (instruction is not IrInst.AllocReusing allocation)
                {
                    continue;
                }

                tokens.TryGetValue(allocation.TokenTemp, out (IrInst.DropReuse Token, int Index) definition)
                    .ShouldBeTrue($"AllocReusing token %{allocation.TokenTemp} must be defined by DropReuse");
                definition.Index.ShouldBeLessThan(index);
                definition.Token.FieldCount.ShouldBe(allocation.FieldCount);
                definition.Token.RuntimeManaged.ShouldBeFalse();
                reusingAllocations++;
            }
        }

        reusingAllocations.ShouldBeGreaterThan(0);
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
