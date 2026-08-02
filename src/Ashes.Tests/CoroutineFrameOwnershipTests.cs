using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// The task frame's ownership protocol: the transform publishes where it saves each value, a saved
/// word is cleared as ownership moves back to the body, and the result leaves the frame explicitly.
/// </summary>
public sealed class CoroutineFrameOwnershipTests
{
    private const int OwnerSlot = 2;
    private const int OwnerTemp = 1;
    private const int AwaitResult = 5;

    [Test]
    public void Saved_temp_offsets_match_the_words_the_suspend_writes()
    {
        StateMachineResult result = Transform(LiveAcrossAwaitBody());

        IrInst.Suspend suspend = result.Instructions.OfType<IrInst.Suspend>().Single();
        foreach ((int offset, int sourceTemp) in suspend.SaveVars)
        {
            result.SavedTempOffsets.ShouldContainKeyAndValue(sourceTemp, offset);
        }

        suspend.SaveVars.ShouldNotBeEmpty();
    }

    [Test]
    public void Resume_clears_every_word_it_restores()
    {
        StateMachineResult result = Transform(LiveAcrossAwaitBody());

        int resumeIndex = result.Instructions.FindIndex(instruction => instruction is IrInst.Resume);
        resumeIndex.ShouldBeGreaterThan(-1);
        foreach (int offset in result.SavedTempOffsets.Values.Concat(result.SavedLocalOffsets.Values))
        {
            ClearedFrameOffsets(result.Instructions).ShouldContain(offset);
        }
    }

    [Test]
    public void Completion_clears_the_word_holding_the_transferred_result()
    {
        // The returned temp is also live across the await, so it occupies a saved word. Completion
        // must hand that reference to the awaiter and leave nothing behind for teardown to release.
        StateMachineResult result = Transform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
            .. Await(),
            new IrInst.Return(OwnerTemp),
        ]);

        result.SavedTempOffsets.ShouldContainKey(OwnerTemp);
        int completionIndex = result.Instructions.FindLastIndex(instruction =>
            instruction is IrInst.StoreMemOffset store && store.OffsetBytes == TaskStructLayout.ResultSlot);
        completionIndex.ShouldBeGreaterThan(-1);
        result.Instructions
            .Skip(completionIndex)
            .OfType<IrInst.StoreMemOffset>()
            .ShouldContain(store => store.OffsetBytes == result.SavedTempOffsets[OwnerTemp]);
    }

    [Test]
    public void A_body_without_awaits_saves_nothing()
    {
        StateMachineResult result = Transform([
            new IrInst.LoadConstStr(OwnerTemp, "text"),
            new IrInst.Return(OwnerTemp),
        ]);

        result.SavedTempOffsets.ShouldBeEmpty();
        result.SavedLocalOffsets.ShouldBeEmpty();
        result.Instructions.OfType<IrInst.Suspend>().ShouldBeEmpty();
    }

    [Test]
    public void Region_backed_coroutine_values_get_no_frame_dropper()
    {
        // Every coroutine value is region-backed until the async allocation gates are narrowed, so
        // no task frame owns a reference and no teardown helper is generated.
        IrProgram program = LowerAsyncProgram();

        program.Functions.Any(function => function.Coroutine is not null).ShouldBeTrue();
        program.Functions
            .SelectMany(function => function.Instructions)
            .OfType<IrInst.CreateTask>()
            .ShouldAllBe(task => task.FrameDropperLabel == null);
        program.Functions
            .Any(function => function.Origin?.Kind == IrFunctionOriginKind.CoroutineFrameDropper)
            .ShouldBeFalse();
    }

    private static List<IrInst> LiveAcrossAwaitBody() =>
    [
        new IrInst.LoadConstStr(OwnerTemp, "text"),
        new IrInst.StoreLocal(OwnerSlot, OwnerTemp),
        .. Await(),
        new IrInst.PrintStr(OwnerTemp),
        new IrInst.LoadLocal(6, OwnerSlot),
        new IrInst.PrintStr(6),
        new IrInst.Return(AwaitResult),
    ];

    private static IrInst[] Await() =>
    [
        new IrInst.LoadConstInt(3, 0),
        new IrInst.CreateCompletedTask(4, 3),
        new IrInst.AwaitTask(AwaitResult, 4),
    ];

    private static HashSet<int> ClearedFrameOffsets(List<IrInst> instructions)
    {
        var zeroTemps = instructions
            .OfType<IrInst.LoadConstInt>()
            .Where(load => load.Value == 0)
            .Select(load => load.Target)
            .ToHashSet();
        return [.. instructions
            .OfType<IrInst.StoreMemOffset>()
            .Where(store => zeroTemps.Contains(store.Source))
            .Select(store => store.OffsetBytes)];
    }

    private static StateMachineResult Transform(List<IrInst> body)
        => StateMachineTransform.Transform(body, captureCount: 0);

    private static IrProgram LowerAsyncProgram()
    {
        var diagnostics = new Frontend.Diagnostics();
        var parsed = new Frontend.Parser(
            """
            let make = given (s) -> async(match await Ashes.Task.sleep(1) with
                | Ok(_u) -> s + "!"
                | Error(_e) -> "err") in
            let job = make("hello") in
            match Ashes.Task.run(job) with
                | Ok(t) -> Ashes.IO.print(t)
                | Error(_e2) -> Ashes.IO.print("bad")
            """,
            diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(parsed);
        diagnostics.ThrowIfAny();
        return ir;
    }
}
