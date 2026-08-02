using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Lifetime placement for coroutines runs on the linear pre-transform body, where an await is still
/// an ordinary control-flow edge. These tests drive that order — place, then split — and assert on
/// the resulting state machine.
/// </summary>
public sealed class CoroutineLifetimePlacementTests
{
    // Local slots 0 and 1 are the coroutine's state struct and dummy argument; temp 0 is reserved by
    // the state machine transform. The fixtures below therefore number from the first free index.
    private const int OwnerSlot = 2;
    private const int OwnerTemp = 1;
    private const int OwnerLoadTemp = 2;
    private const int BorrowTemp = 3;
    private const int DropLoadTemp = 4;
    private const int ConditionTemp = 5;
    private const int FirstAwaitResult = 8;
    private const int SecondAwaitResult = 11;
    private const int TempCount = 12;

    [Test]
    public void Owner_used_only_before_an_await_is_dropped_before_the_suspend()
    {
        List<IrInst> instructions = PlaceAndTransform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            .. UseOwner(),
            .. Await(FirstAwaitResult),
            .. DropOwner(),
            new IrInst.Return(FirstAwaitResult),
        ]);

        int dropIndex = instructions.FindIndex(instruction => instruction is IrInst.RcDrop);
        int suspendIndex = instructions.FindIndex(instruction => instruction is IrInst.Suspend);
        dropIndex.ShouldBeGreaterThan(-1);
        dropIndex.ShouldBeLessThan(suspendIndex);
        SavedTemps(instructions).ShouldNotContain(OwnerTemp);
    }

    [Test]
    public void Owner_live_across_an_await_is_saved_and_dropped_after_the_resume()
    {
        List<IrInst> instructions = PlaceAndTransform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            .. Await(FirstAwaitResult),
            .. UseOwner(),
            .. DropOwner(),
            new IrInst.Return(FirstAwaitResult),
        ]);

        SavedTemps(instructions).ShouldContain(OwnerTemp);
        instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(1);
        int resumeIndex = instructions.FindIndex(instruction => instruction is IrInst.Resume);
        int printIndex = instructions.FindIndex(instruction => instruction is IrInst.PrintStr);
        int dropIndex = instructions.FindIndex(instruction => instruction is IrInst.RcDrop);
        dropIndex.ShouldBe(printIndex + 1);
        dropIndex.ShouldBeGreaterThan(resumeIndex);
    }

    [Test]
    public void Owner_live_across_multiple_awaits_is_saved_at_every_suspend()
    {
        List<IrInst> instructions = PlaceAndTransform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            .. Await(FirstAwaitResult),
            .. Await(SecondAwaitResult),
            .. UseOwner(),
            .. DropOwner(),
            new IrInst.Return(SecondAwaitResult),
        ]);

        List<IrInst.Suspend> suspends = [.. instructions.OfType<IrInst.Suspend>()];
        suspends.Count.ShouldBe(2);
        foreach (IrInst.Suspend suspend in suspends)
        {
            suspend.SaveVars.ShouldContain(saved => saved.SourceTemp == OwnerTemp);
        }

        instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(1);
    }

    [Test]
    public void Owner_live_across_an_await_in_a_loop_is_dropped_once_on_the_exit_edge()
    {
        List<IrInst> instructions = PlaceAndTransform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            new IrInst.Label("loop"),
            .. Await(FirstAwaitResult),
            .. UseOwner(),
            new IrInst.LoadConstBool(ConditionTemp, false),
            new IrInst.JumpIfFalse(ConditionTemp, "done"),
            new IrInst.Jump("loop"),
            new IrInst.Label("done"),
            .. DropOwner(),
            new IrInst.Return(FirstAwaitResult),
        ]);

        SavedTemps(instructions).ShouldContain(OwnerTemp);
        instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(1);
        int doneIndex = instructions.FindIndex(instruction => instruction is IrInst.Label { Name: "done" });
        instructions[doneIndex + 1].ShouldBeOfType<IrInst.RcDrop>();
    }

    [Test]
    public void Owner_live_on_one_branch_after_resume_is_dropped_on_both_branches()
    {
        List<IrInst> instructions = PlaceAndTransform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            .. Await(FirstAwaitResult),
            new IrInst.LoadConstBool(ConditionTemp, false),
            new IrInst.JumpIfFalse(ConditionTemp, "other"),
            .. UseOwner(),
            new IrInst.Jump("end"),
            new IrInst.Label("other"),
            new IrInst.PrintBool(ConditionTemp),
            new IrInst.Jump("end"),
            new IrInst.Label("end"),
            .. DropOwner(),
            new IrInst.Return(FirstAwaitResult),
        ]);

        SavedTemps(instructions).ShouldContain(OwnerTemp);
        instructions.Count(instruction => instruction is IrInst.RcDrop).ShouldBe(2);
        int printIndex = instructions.FindIndex(instruction => instruction is IrInst.PrintStr);
        instructions[printIndex + 1].ShouldBeOfType<IrInst.RcDrop>();
        int otherIndex = instructions.FindIndex(instruction => instruction is IrInst.Label { Name: "other" });
        instructions[otherIndex + 1].ShouldBeOfType<IrInst.RcDrop>();
    }

    [Test]
    public void Program_wide_placement_leaves_an_already_placed_function_alone()
    {
        var placed = new IrFunction(
            "coroutine_0",
            [
                new IrInst.LoadConstStr(OwnerTemp, "text"),
                new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
                .. DropOwner(),
                .. UseOwner(),
                new IrInst.Return(BorrowTemp),
            ],
            LocalCount: OwnerSlot + 1,
            TempCount: TempCount,
            HasEnvAndArgParams: true)
        {
            LifetimesPlaced = true,
        };

        PerceusLifetimePlacement.Place(placed).ShouldBeSameAs(placed);
    }

    /// <summary>A completed sub-task and the await of it: the split point every fixture needs.</summary>
    private static IrInst[] Await(int resultTemp) =>
    [
        new IrInst.LoadConstInt(resultTemp - 2, 0),
        new IrInst.CreateCompletedTask(resultTemp - 1, resultTemp - 2),
        new IrInst.AwaitTask(resultTemp, resultTemp - 1),
    ];

    /// <summary>A borrowing read of the owner: the last use placement anchors a drop to.</summary>
    private static IrInst[] UseOwner() =>
    [
        new IrInst.LoadLocal(OwnerLoadTemp, OwnerSlot),
        new IrInst.Borrow(BorrowTemp, OwnerLoadTemp),
        new IrInst.PrintStr(BorrowTemp),
    ];

    /// <summary>The lexical scope-exit drop that placement moves to the owner's real last use.</summary>
    private static IrInst[] DropOwner() =>
    [
        new IrInst.LoadLocal(DropLoadTemp, OwnerSlot),
        new IrInst.RcDrop(DropLoadTemp, "String", OwnerSlot),
    ];

    private static List<IrInst> PlaceAndTransform(List<IrInst> body)
    {
        LifetimePlacementResult placed = PerceusLifetimePlacement.Place(body, TempCount, "coroutine_0");
        return StateMachineTransform.Transform(placed.Instructions, captureCount: 0).Instructions;
    }

    private static HashSet<int> SavedTemps(List<IrInst> instructions)
        => [.. instructions.OfType<IrInst.Suspend>().SelectMany(suspend => suspend.SaveVars).Select(saved => saved.SourceTemp)];
}
