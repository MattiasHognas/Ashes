using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Covers that a user <c>async</c> task whose body contains an <c>await</c> is lowered through the
/// suspending-coroutine path (<see cref="StateMachineTransform"/> → a function with a
/// <c>CoroutineInfo</c> and an <see cref="IrInst.CreateTask"/>), while an await-free <c>async</c>
/// stays the eager pre-completed task (<see cref="IrInst.CreateCompletedTask"/>, no coroutine). The
/// end-to-end async test suite verifies the results are unchanged; these assertions verify the
/// state machine is actually exercised rather than dead code.
/// </summary>
public sealed class AsyncCoroutinePathTests
{
    [Test]
    public void AsyncBodyWithAwait_LowersThroughCoroutineStateMachine()
    {
        var ir = LowerProgram(
            "Ashes.Task.run(async(match await async 10 with | Ok(a) -> a | Error(e) -> 0))");

        // A coroutine function with a multi-state machine is produced (the await split it into >1
        // state), and the task is created as a live coroutine (CreateTask), not an eager completed
        // value. The AwaitTask itself is consumed by StateMachineTransform into suspend/resume, so it
        // is the CoroutineInfo (StateCount > 1) that evidences the split.
        var coroutine = ir.Functions.SingleOrDefault(f => f.Coroutine is not null);
        coroutine.ShouldNotBeNull();
        coroutine.Coroutine!.StateCount.ShouldBeGreaterThan(1);
        AllInstructions(ir).Any(i => i is IrInst.CreateTask).ShouldBeTrue();
    }

    [Test]
    public void AwaitFreeAsyncBody_StaysEagerCompletedTask()
    {
        var ir = LowerProgram("Ashes.Task.run(async 10)");

        // No suspension point → no coroutine, no CreateTask; the eager pre-completed path is kept.
        ir.Functions.Any(f => f.Coroutine is not null).ShouldBeFalse();
        AllInstructions(ir).Any(i => i is IrInst.CreateTask).ShouldBeFalse();
        AllInstructions(ir).Any(i => i is IrInst.CreateCompletedTask).ShouldBeTrue();
    }

    // --- Helpers ---

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram ir)
    {
        foreach (var inst in ir.EntryFunction.Instructions)
        {
            yield return inst;
        }

        foreach (var func in ir.Functions)
        {
            foreach (var inst in func.Instructions)
            {
                yield return inst;
            }
        }
    }
}
