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
        IrFunctionOrigin origin = coroutine.Origin
            ?? throw new InvalidOperationException("Missing coroutine origin.");
        origin.Kind.ShouldBe(IrFunctionOriginKind.Coroutine);
        origin.ParentGeneratedLabel.ShouldBe("_start_main");
        SourceLocation generationLocation = origin.GenerationLocation
            ?? throw new InvalidOperationException("Missing coroutine generation location.");
        generationLocation.FilePath.ShouldBe("async-origin.ash");
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

    [Test]
    public void SpawnedCompletedTaskBorrow_DoesNotDropTailForwardedParentListBeforeItsUse()
    {
        const string source = """
            let recursive render : List(Int) -> Str =
                given (items: List(Int)) ->
                    match items with
                        | [] -> "]"
                        | head :: tail ->
                            Ashes.Text.fromInt(head) + (match tail with
                                | [] -> "]"
                                | _ -> "," + render(tail))
            in
                "[" + render(let shared =
                    let head = 3
                    in
                        let tail = []
                        in head :: tail
                in
                    let _spawned = Ashes.Task.spawn(async(shared))
                    in shared)
            """;

        IrFunction entry = LowerProgram(source).EntryFunction;
        int spawnIndex = entry.Instructions.FindIndex(instruction => instruction is IrInst.SpawnTask);
        int renderCallIndex = entry.Instructions.FindIndex(
            spawnIndex + 1,
            instruction => instruction is IrInst.CallClosure);

        spawnIndex.ShouldBeGreaterThan(-1);
        renderCallIndex.ShouldBeGreaterThan(spawnIndex);
        bool droppedBeforeRender = entry.Instructions
            .Skip(spawnIndex + 1)
            .Take(renderCallIndex - spawnIndex - 1)
            .Any(instruction => instruction is IrInst.RcDrop { TypeName: "List" });
        bool droppedAfterRender = entry.Instructions
            .Skip(renderCallIndex + 1)
            .Any(instruction => instruction is IrInst.RcDrop { TypeName: "List" });
        droppedBeforeRender.ShouldBeFalse();
        droppedAfterRender.ShouldBeTrue();
    }

    [Test]
    public void TailForwarding_DoesNotCrossAShadowingLet()
    {
        const string source = """
            let recursive render : List(Int) -> Str =
                given (items: List(Int)) ->
                    match items with
                        | [] -> "]"
                        | head :: tail -> Ashes.Text.fromInt(head) + render(tail)
            in
                render(let shared =
                    let head = 3
                    in
                        let tail = []
                        in head :: tail
                in
                    let shared = []
                    in shared)
            """;

        IrFunction entry = LowerProgram(source).EntryFunction;
        int renderCallIndex = entry.Instructions.FindLastIndex(
            instruction => instruction is IrInst.CallClosure);
        renderCallIndex.ShouldBeGreaterThan(-1);
        bool droppedShadowedOuterList = entry.Instructions
            .Take(renderCallIndex)
            .Any(instruction => instruction is IrInst.RcDrop { TypeName: "List" });

        droppedShadowedOuterList.ShouldBeTrue();
    }

    [Test]
    public void CapabilityDictionaryRewrite_PreservesCoroutineGenerationLocation()
    {
        const string source = """
            capability Clock =
                | now : Unit -> Int

            let readLater : Unit -> Task(Str, Int) needs {Clock} =
                given (unit) ->
                    async(match await async (perform Clock.now(unit)) with
                        | Ok(value) -> value
                        | Error(error) -> 0)

            0
            """;

        var ir = LowerProgram(source);

        var coroutine = ir.Functions.Single(f => f.Coroutine is not null);
        IrFunctionOrigin origin = coroutine.Origin
            ?? throw new InvalidOperationException("Missing coroutine origin.");
        origin.Kind.ShouldBe(IrFunctionOriginKind.Coroutine);
        origin.StableDiscriminator.ShouldNotBe("coroutine:0:0");
        SourceLocation generationLocation = origin.GenerationLocation
            ?? throw new InvalidOperationException("Missing coroutine generation location.");
        generationLocation.FilePath.ShouldBe("async-origin.ash");
        generationLocation.Line.ShouldBeGreaterThan(0);
        generationLocation.Column.ShouldBeGreaterThan(0);
    }

    // --- Helpers ---

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var lowering = new Lowering(diagnostics);
        lowering.SetSourceContext("async-origin.ash", source);
        var ir = lowering.Lower(program);
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
