using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class ArenaCurryingBorrowTests
{
    private static IrFunction Stage(bool ownedEnvironment = false, bool ownedClosure = false)
        => new("stage", [
            new IrInst.Alloc(0, 8, RuntimeManaged: ownedEnvironment),
            new IrInst.LoadLocal(1, 1),
            new IrInst.StoreMemOffset(0, 0, 1),
            new IrInst.MakeClosure(2, "next", 0, 8, RuntimeManaged: ownedClosure),
            new IrInst.Return(2),
        ], 2, 3, true);

    [Test]
    public void Pure_arena_stage_borrows_capture()
        => IrOptimizer.IsBorrowingArenaCurryingStage(Stage()).ShouldBeTrue();

    [Test]
    public void Owned_environment_or_closure_is_not_borrowed()
    {
        IrOptimizer.IsBorrowingArenaCurryingStage(Stage(ownedEnvironment: true)).ShouldBeFalse();
        IrOptimizer.IsBorrowingArenaCurryingStage(Stage(ownedClosure: true)).ShouldBeFalse();
    }

    [Test]
    public void Stage_that_consumes_argument_is_not_borrowed()
    {
        IrFunction stage = Stage();
        stage.Instructions.Insert(2, new IrInst.RcDrop(1, "String", RuntimeManaged: true));
        IrOptimizer.IsBorrowingArenaCurryingStage(stage).ShouldBeFalse();
    }

    [Test]
    public void Raw_borrow_of_captured_argument_is_accepted_but_allocation_alias_is_not()
    {
        IrFunction stage = Stage();
        stage.Instructions.Insert(2, new IrInst.Borrow(3, 1));
        stage.Instructions[3] = new IrInst.StoreMemOffset(0, 0, 3);
        IrOptimizer.IsBorrowingArenaCurryingStage(stage).ShouldBeTrue();
        stage.Instructions[2] = new IrInst.Borrow(3, 0);
        IrOptimizer.IsBorrowingArenaCurryingStage(stage).ShouldBeFalse();
    }
}
