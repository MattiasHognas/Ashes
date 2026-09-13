using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class CapturedMatchResultOwnershipTests
{
    [Test]
    public void Captured_match_results_use_the_lambda_environment_instead_of_outer_local_slots()
    {
        string padding = string.Concat(Enumerable.Range(0, 30).Select(index =>
            $"let padding{index} = {index}\nin "));
        string source =
            """
            type Box =
                | first: Int
                | second: Bool

            """ + padding +
            """
            let captured =
                let seed = Box(first = 1, second = true)
                in
                    match Ashes.Task.run(async(match await Ashes.Task.task(seed) with
                        | Ok(awaited) -> awaited
                        | Error(_) -> seed)) with
                        | Ok(completed) -> completed
                        | Error(_) -> seed
            in
                let recursive loop : List(Int) -> Box =
                    given (items: List(Int)) ->
                        match items with
                            | [] -> captured
                            | _ :: tail -> loop(tail)
                in loop([1, 2, 3])
            """;
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        IrProgram ir = lowering.Lower(program);

        diagnostics.Errors.ShouldBeEmpty(source);
        IrFunction loop = ir.Functions.Single(function =>
            function.Origin?.Kind == IrFunctionOriginKind.SourceFunction &&
            string.Equals(function.Origin?.Source?.SourceName, "loop", StringComparison.Ordinal));
        loop.Instructions.OfType<IrInst.LoadEnv>().ShouldNotBeEmpty();
        loop.Instructions.OfType<IrInst.RcDup>()
            .Any(instruction => instruction.RuntimeManaged).ShouldBeTrue();
        loop.Instructions.OfType<IrInst.LoadLocal>()
            .ShouldAllBe(instruction => instruction.Slot >= 0 && instruction.Slot < loop.LocalCount);
        loop.Instructions.OfType<IrInst.StoreLocal>()
            .ShouldAllBe(instruction => instruction.Slot >= 0 && instruction.Slot < loop.LocalCount);
        loop.Instructions.OfType<IrInst.RcDrop>()
            .Where(instruction => instruction.OwnerSlot >= 0)
            .ShouldAllBe(instruction => instruction.OwnerSlot < loop.LocalCount);
    }

    [Test]
    public void A_lambda_binding_that_aliases_a_capture_owns_no_slot_of_the_enclosing_frame()
    {
        // Binding a capture to a name inside the lambda used to register that name as an alias of the
        // enclosing function's owner, so the scope-exit release was aimed at a slot only the enclosing
        // frame has. The capture is borrowed here: the closure's environment holds the reference.
        const string source = """
            let captured =
                let items = ["first"]
                in
                    match items with
                        | [] -> "empty"
                        | head :: _ -> head
            in
                let capturing =
                    given (parameter: Float) ->
                        let captureUse = captured
                        in
                            let parameterUse = parameter
                            in
                                let shared = captured
                                in
                                    if false
                                    then shared
                                    else shared
                in ""
            """;
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        Lowering lowering = new(diagnostics);
        IrProgram ir = lowering.Lower(program);

        diagnostics.Errors.ShouldBeEmpty(source);
        IrFunction lifted = ir.Functions.Single(function =>
            function.Origin?.Kind == IrFunctionOriginKind.SourceFunction &&
            string.Equals(function.Origin?.Source?.SourceName, "capturing", StringComparison.Ordinal));
        lifted.Instructions.OfType<IrInst.LoadLocal>()
            .ShouldAllBe(instruction => instruction.Slot >= 0 && instruction.Slot < lifted.LocalCount);
        lifted.Instructions.OfType<IrInst.StoreLocal>()
            .ShouldAllBe(instruction => instruction.Slot >= 0 && instruction.Slot < lifted.LocalCount);
        lifted.Instructions.OfType<IrInst.RcDrop>()
            .Where(instruction => instruction.OwnerSlot >= 0)
            .ShouldAllBe(instruction => instruction.OwnerSlot < lifted.LocalCount);
    }
}
