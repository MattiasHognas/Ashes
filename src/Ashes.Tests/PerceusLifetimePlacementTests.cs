using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class PerceusLifetimePlacementTests
{
    [Test]
    public void Lowered_let_places_drop_immediately_after_final_print_use()
    {
        IrFunction function = Lower("let s = \"hello\" in Ashes.IO.print(s)").EntryFunction;

        int printIndex = function.Instructions.FindIndex(instruction => instruction is IrInst.PrintStr);
        function.Instructions[printIndex + 1].ShouldBeOfType<IrInst.RcDrop>();
    }

    [Test]
    public void Lowered_call_places_dup_before_consuming_use_when_owner_is_used_again()
    {
        IrFunction function = Lower(
            """
            let show = given (text) -> Ashes.IO.print(text + "") in
            let s = "hello" in
            let _ = show(s) in
            Ashes.IO.print(s)
            """).EntryFunction;

        int callIndex = function.Instructions.FindIndex(instruction => instruction is IrInst.CallClosure);
        function.Instructions[callIndex - 1].ShouldBeOfType<IrInst.RcDup>();
    }

    [Test]
    public void Lowered_match_splits_outer_owner_drop_across_constructor_arms()
    {
        IrFunction function = Lower(
            """
            let s = "hello" in
            match Some(1) with
                | Some(_) -> Ashes.IO.print(s)
                | None -> Ashes.IO.print(0)
            """).EntryFunction;

        function.Instructions.Count(instruction => instruction is IrInst.RcDrop { TypeName: "String" }).ShouldBe(2);
    }

    [Test]
    public void Let_owner_drop_moves_to_its_last_use()
    {
        var function = Function([
            new IrInst.LoadConstStr(0, "text"),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.Borrow(2, 1),
            new IrInst.PrintStr(2),
            new IrInst.LoadConstInt(3, 0),
            new IrInst.LoadLocal(4, 0),
            new IrInst.RcDrop(4, "String", 0),
            new IrInst.Return(3),
        ], tempCount: 5);

        IrFunction placed = PerceusLifetimePlacement.Place(function);

        int printIndex = placed.Instructions.FindIndex(instruction => instruction is IrInst.PrintStr);
        int dropIndex = placed.Instructions.FindIndex(instruction => instruction is IrInst.RcDrop);
        dropIndex.ShouldBe(printIndex + 1);
    }

    [Test]
    public void Consuming_call_gets_dup_when_owner_is_live_after_call()
    {
        var function = Function([
            new IrInst.LoadConstStr(0, "text"),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.LoadLocal(2, 0),
            new IrInst.Borrow(3, 2),
            new IrInst.CallClosure(4, 1, 3),
            new IrInst.LoadLocal(5, 0),
            new IrInst.Borrow(6, 5),
            new IrInst.PrintStr(6),
            new IrInst.LoadLocal(7, 0),
            new IrInst.RcDrop(7, "String", 0),
            new IrInst.Return(4),
        ], tempCount: 8);

        IrFunction placed = PerceusLifetimePlacement.Place(function);

        int callIndex = placed.Instructions.FindIndex(instruction => instruction is IrInst.CallClosure);
        placed.Instructions[callIndex - 1].ShouldBeOfType<IrInst.RcDup>();
        placed.Instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(1);
    }

    [Test]
    public void Borrowing_call_does_not_split_owner()
    {
        var call = new IrInst.CallClosure(4, 1, 3);
        var function = Function([
            new IrInst.LoadConstStr(0, "text"),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.LoadLocal(2, 0),
            new IrInst.Borrow(3, 2),
            call,
            new IrInst.LoadLocal(5, 0),
            new IrInst.Borrow(6, 5),
            new IrInst.PrintStr(6),
            new IrInst.LoadLocal(7, 0),
            new IrInst.RcDrop(7, "String", 0),
            new IrInst.Return(4),
        ], tempCount: 8);

        IrFunction placed = PerceusLifetimePlacement.Place(function, new HashSet<IrInst.CallClosure> { call });

        placed.Instructions.Any(instruction => instruction is IrInst.RcDup).ShouldBeFalse();
    }

    [Test]
    public void Match_drops_dead_owner_at_branch_entry_and_after_live_branch_use()
    {
        var function = Function([
            new IrInst.LoadConstStr(0, "text"),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.SwitchTag(1, [(0, "live")], "dead"),
            new IrInst.Label("live"),
            new IrInst.LoadLocal(2, 0),
            new IrInst.Borrow(3, 2),
            new IrInst.PrintStr(3),
            new IrInst.Jump("end"),
            new IrInst.Label("dead"),
            new IrInst.LoadConstInt(4, 0),
            new IrInst.PrintInt(4),
            new IrInst.Jump("end"),
            new IrInst.Label("end"),
            new IrInst.LoadLocal(5, 0),
            new IrInst.RcDrop(5, "String", 0),
            new IrInst.Return(4),
        ], tempCount: 6);

        IrFunction placed = PerceusLifetimePlacement.Place(function);

        placed.Instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(2);
        int livePrint = placed.Instructions.FindIndex(instruction => instruction is IrInst.PrintStr);
        placed.Instructions[livePrint + 1].ShouldBeOfType<IrInst.RcDrop>();
        int deadLabel = placed.Instructions.FindIndex(instruction => instruction is IrInst.Label { Name: "dead" });
        placed.Instructions[deadLabel + 1].ShouldBeOfType<IrInst.RcDrop>();
    }

    private static IrFunction Function(List<IrInst> instructions, int tempCount)
        => new("test", instructions, 1, tempCount, false);

    private static IrProgram Lower(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
