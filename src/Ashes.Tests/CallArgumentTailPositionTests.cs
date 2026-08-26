using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class CallArgumentTailPositionTests
{
    [Test]
    public void Recursive_call_used_as_pipeline_lambda_argument_is_not_lowered_as_tail_call()
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(
            """
            let recursive rebuild values preserve =
                match values with
                    | [] -> []
                    | head :: tail ->
                        if preserve
                        then
                            rebuild(tail)(true)
                            |> (given (rebuilt) -> head :: rebuilt)
                        else rebuild(tail)(true)

            rebuild(["a", "b", "c"])(true)
            """,
            diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();

        IrFunction rebuild = ir.Functions.Single(function =>
            string.Equals(function.Origin?.Source?.SourceName, "rebuild", StringComparison.Ordinal)
            && function.Instructions.Any(instruction =>
                instruction is IrInst.Label { Name: var name }
                && name.EndsWith("_body", StringComparison.Ordinal)));
        string bodyLabel = rebuild.Instructions
            .OfType<IrInst.Label>()
            .Single(label => label.Name.EndsWith("_body", StringComparison.Ordinal))
            .Name;

        rebuild.Instructions.Any(instruction => instruction is IrInst.CallClosure).ShouldBeTrue();
        rebuild.Instructions.Count(instruction =>
            instruction is IrInst.Jump jump
            && string.Equals(jump.Target, bodyLabel, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void Recursive_call_used_as_operator_operand_is_not_lowered_as_tail_call()
    {
        // The `else` arm's genuine tail call makes the function a TCO loop; the `then` arm's
        // call is an operand of `+` and must stay an ordinary call whose result feeds the add.
        Diagnostics diagnostics = new();
        Program program = new Parser(
            """
            let recursive countEvens n =
                if n == 0
                then 0
                else
                    if n % 2 == 0
                    then 1 + countEvens(n - 1)
                    else countEvens(n - 1)

            countEvens(4)
            """,
            diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();

        IrFunction countEvens = ir.Functions.Single(function =>
            string.Equals(function.Origin?.Source?.SourceName, "countEvens", StringComparison.Ordinal)
            && function.Instructions.Any(instruction =>
                instruction is IrInst.Label { Name: var name }
                && name.EndsWith("_body", StringComparison.Ordinal)));
        string bodyLabel = countEvens.Instructions
            .OfType<IrInst.Label>()
            .Single(label => label.Name.EndsWith("_body", StringComparison.Ordinal))
            .Name;

        countEvens.Instructions.Any(instruction =>
            instruction is IrInst.CallClosure or IrInst.CallKnown).ShouldBeTrue();
        countEvens.Instructions.Count(instruction =>
            instruction is IrInst.Jump jump
            && string.Equals(jump.Target, bodyLabel, StringComparison.Ordinal)).ShouldBe(1);
    }
}
