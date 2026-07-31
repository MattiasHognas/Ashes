using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Regression coverage for the capability arena exception. The identity-aware ownership call graph
/// computes which functions may execute under a live handler post; placement stays conservative only
/// in those functions and possible higher-order targets, not every function in a program containing a
/// <c>handle</c>.
/// </summary>
public sealed class CapabilityRcEligibilityTests
{
    [Test]
    public void No_capabilities_at_all_gets_rc_normalization_baseline()
    {
        // Sanity control for the assertion technique below: with no capabilities declared, the
        // lambda's returned string concatenation is already known to normalize to RC (unrelated,
        // pre-existing behavior) — confirms the test harness detects the flag correctly before
        // trusting it to distinguish the two capability cases.
        var ir = LowerProgram(
            """
            let build =
                given (u) ->
                    "ab" + "cd"

            build(Unit)
            """);

        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldContain(c => c.RuntimeManaged);
    }

    [Test]
    public void Capability_resolved_only_via_static_provider_gets_rc_normalization()
    {
        // Clock is declared and used, but never `handle`d anywhere in the program — every use
        // resolves statically through `provide`. No `handle` expression exists anywhere, so
        // No function receives the live-handler effect and `build`'s returned string should get the
        // same RC treatment it would if Clock didn't exist at all. Before this change, the mere
        // presence of the `capability Clock` declaration (CapabilityGlobalCount > 0) forced `build`'s
        // result to stay arena-managed even though nothing here ever dynamically dispatches.
        var ir = LowerProgram(
            """
            capability Clock =
                | now : Unit -> Int

            provide Clock =
                | now = given (_) -> 1234

            let build =
                given (u) ->
                    "ab" + "cd"

            let _ = Clock.now(Unit) in
            build(Unit)
            """);

        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldContain(c => c.RuntimeManaged);
    }

    [Test]
    public void Unrelated_function_in_a_program_with_a_handle_gets_ordinary_rc_placement()
    {
        (Lowering lowering, IrProgram ir) = LowerProgramAndAnalysis(
            """
            capability Log =
                | log : Str -> Unit

            let build =
                given (u) ->
                    "ab" + "cd"

            let runLogged =
                given (u) ->
                    handle Log.log("hi") with
                        | Log.log(msg) -> resume(Unit)

            let _ = runLogged(Unit) in
            build(Unit)
            """);

        GetSummary(lowering, "runLogged").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        GetSummary(lowering, "build").MayExecuteUnderLiveHandlerPost.ShouldBeFalse();
        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldContain(c => c.RuntimeManaged);
    }

    [Test]
    public void Helper_reachable_from_a_handle_stays_on_the_guarded_arena_path()
    {
        (Lowering lowering, IrProgram ir) = LowerProgramAndAnalysis(
            """
            capability Log =
                | log : Str -> Str

            let build =
                given (u) ->
                    "ab" + "cd"

            let runLogged =
                given (u) ->
                    handle Log.log("hi") with
                        | Log.log(msg) ->
                            let _ = resume("ok") in
                            build(Unit)

            runLogged(Unit)
            """);

        GetSummary(lowering, "runLogged").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        GetSummary(lowering, "build").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldAllBe(c => !c.RuntimeManaged);
    }

    [Test]
    public void Higher_order_target_reachable_from_a_handle_falls_back_to_the_safe_arena_path()
    {
        (Lowering lowering, IrProgram ir) = LowerProgramAndAnalysis(
            """
            capability Log =
                | log : Str -> Str

            let build =
                given (u) ->
                    "ab" + "cd"

            let invoke =
                given (fn) ->
                    fn(Unit)

            let runLogged =
                given (u) ->
                    handle Log.log("hi") with
                        | Log.log(msg) ->
                            let _ = resume("ok") in
                            invoke(build)

            runLogged(Unit)
            """);

        GetSummary(lowering, "runLogged").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        GetSummary(lowering, "invoke").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        GetSummary(lowering, "build").MayExecuteUnderLiveHandlerPost.ShouldBeTrue();
        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldAllBe(c => !c.RuntimeManaged);
    }

    // --- Helpers ---

    private static IrProgram LowerProgram(string source)
        => LowerProgramAndAnalysis(source).Program;

    private static (Lowering Lowering, IrProgram Program) LowerProgramAndAnalysis(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        var lowering = new Lowering(diagnostics);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return (lowering, ir);
    }

    private static FunctionOwnershipSummary GetSummary(Lowering lowering, string function) =>
        lowering.GetOwnershipSummary(function)
            ?? throw new InvalidOperationException($"No ownership summary for '{function}'.");

    private static IEnumerable<IrInst> AllInstructions(IrProgram program)
    {
        foreach (var inst in program.EntryFunction.Instructions)
        {
            yield return inst;
        }

        foreach (var func in program.Functions)
        {
            foreach (var inst in func.Instructions)
            {
                yield return inst;
            }
        }
    }
}
