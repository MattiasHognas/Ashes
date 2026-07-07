using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Direct unit tests for <see cref="StateMachineTransform"/> on hand-built IR — exercising a
/// specific edge (the structured-parallelism `both` liveness extension and the fork/join/cleanup
/// within-one-segment invariant) in isolation from a full async program. Without the liveness cases
/// a `both` result/closure live across an `await` split would be dropped from the coroutine
/// save/restore set and miscompile on resume.
/// </summary>
public sealed class StateMachineTransformTests
{
    // A minimal completed sub-task to await against. Returns the temp holding the task.
    private static void EmitCompletedTask(List<IrInst> body, int valueTemp, int taskTemp)
    {
        body.Add(new IrInst.LoadConstInt(valueTemp, 0));
        body.Add(new IrInst.CreateCompletedTask(taskTemp, valueTemp));
    }

    [Test]
    public void ParallelJoin_result_live_across_await_is_saved_and_restored()
    {
        // A `both` fork/join/cleanup group runs BEFORE the await; the joined result (temp 3,
        // defined by ParallelJoin) is then consumed AFTER the await. It must survive the suspend.
        var body = new List<IrInst>
        {
            new IrInst.LoadConstInt(1, 7),   // stand-in right-thunk closure temp
            new IrInst.ParallelFork(2, 1),   // desc = 2
            new IrInst.ParallelJoin(3, 2),   // res  = 3  (defined by ParallelJoin)
            new IrInst.ParallelCleanup(2),
        };
        EmitCompletedTask(body, valueTemp: 6, taskTemp: 5);
        body.Add(new IrInst.AwaitTask(4, 5));  // suspend/resume split
        body.Add(new IrInst.AddInt(8, 3, 4));  // uses res(3) after the await
        body.Add(new IrInst.Return(8));

        var result = StateMachineTransform.Transform(body, captureCount: 0);

        var suspend = result.Instructions.OfType<IrInst.Suspend>().ShouldHaveSingleItem();
        suspend.SaveVars.ShouldContain(v => v.SourceTemp == 3,
            "ParallelJoin result (temp 3) must be saved across the await.");
        var resume = result.Instructions.OfType<IrInst.Resume>().ShouldHaveSingleItem();
        resume.RestoreVars.ShouldContain(v => v.TargetTemp == 3,
            "ParallelJoin result (temp 3) must be restored on resume.");
    }

    [Test]
    public void ParallelFork_closure_live_across_await_is_saved_and_restored()
    {
        // The right-thunk closure (temp 1) is defined BEFORE the await and first read AFTER it by a
        // ParallelFork. GetUsedTemps must recognise ParallelFork.RightClosureTemp or temp 1 would be
        // dropped from the save set and the fork would read garbage on resume.
        var body = new List<IrInst>();
        body.Add(new IrInst.LoadConstInt(1, 7));   // closure temp, live across await
        EmitCompletedTask(body, valueTemp: 6, taskTemp: 5);
        body.Add(new IrInst.AwaitTask(4, 5));      // suspend/resume split
        body.Add(new IrInst.ParallelFork(2, 1));   // reads closure(1) after the await
        body.Add(new IrInst.ParallelJoin(3, 2));
        body.Add(new IrInst.ParallelCleanup(2));
        body.Add(new IrInst.AddInt(8, 3, 4));
        body.Add(new IrInst.Return(8));

        var result = StateMachineTransform.Transform(body, captureCount: 0);

        var suspend = result.Instructions.OfType<IrInst.Suspend>().ShouldHaveSingleItem();
        suspend.SaveVars.ShouldContain(v => v.SourceTemp == 1,
            "ParallelFork closure (temp 1) must be saved across the await.");
        var resume = result.Instructions.OfType<IrInst.Resume>().ShouldHaveSingleItem();
        resume.RestoreVars.ShouldContain(v => v.TargetTemp == 1,
            "ParallelFork closure (temp 1) must be restored on resume.");
    }

    [Test]
    public void Await_between_fork_and_cleanup_violates_invariant()
    {
        // An `await` between the fork and its cleanup would strand the worker descriptor/arena
        // across a suspend/resume — the transform must reject it rather than miscompile.
        var body = new List<IrInst>();
        body.Add(new IrInst.LoadConstInt(1, 7));
        body.Add(new IrInst.ParallelFork(2, 1));
        EmitCompletedTask(body, valueTemp: 6, taskTemp: 5);
        body.Add(new IrInst.AwaitTask(4, 5));   // illegal: between fork(2) and its cleanup
        body.Add(new IrInst.ParallelJoin(3, 2));
        body.Add(new IrInst.ParallelCleanup(2));
        body.Add(new IrInst.Return(3));

        Should.Throw<InvalidOperationException>(() => StateMachineTransform.Transform(body, captureCount: 0));
    }

    [Test]
    public void Contiguous_fork_join_cleanup_with_later_await_does_not_throw()
    {
        // The shape LowerParallelBoth actually emits: fork/join/cleanup contiguous, await afterwards.
        var body = new List<IrInst>
        {
            new IrInst.LoadConstInt(1, 7),
            new IrInst.ParallelFork(2, 1),
            new IrInst.ParallelJoin(3, 2),
            new IrInst.ParallelCleanup(2),
        };
        EmitCompletedTask(body, valueTemp: 6, taskTemp: 5);
        body.Add(new IrInst.AwaitTask(4, 5));
        body.Add(new IrInst.AddInt(8, 3, 4));
        body.Add(new IrInst.Return(8));

        Should.NotThrow(() => StateMachineTransform.Transform(body, captureCount: 0));
    }
}
