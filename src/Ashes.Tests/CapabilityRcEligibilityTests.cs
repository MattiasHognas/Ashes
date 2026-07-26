using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Regression coverage for the capability arena-exception narrowing: <c>_programHasDynamicCapabilityDispatch</c>
/// (Lowering.Capabilities.cs's <c>DetectDynamicCapabilityDispatch</c>) replaces the old whole-program
/// <c>CapabilityGlobalCount &gt; 0</c> gate at the RC-eligibility sites in Lowering.cs / Lowering.Ownership.cs /
/// Lowering.Patterns.cs. A program is only forced onto the conservative arena path when it contains an
/// actual <c>handle</c> expression somewhere (the only way handler evidence — and therefore a pending
/// one-shot post — can ever exist); a program whose capabilities are all resolved by static <c>provide</c>
/// now gets ordinary RC treatment for its values, same as a program with no capabilities at all.
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
        // `_programHasDynamicCapabilityDispatch` is false and `build`'s returned string should get
        // the same RC treatment it would if Clock didn't exist at all. Before this change, the mere
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
    public void Capability_with_a_handle_keeps_conservative_arena_treatment()
    {
        // Log is dynamically dispatched through a real `handle`, so the whole program must keep the
        // conservative (pre-existing) arena behavior: `build`'s returned string stays arena-managed
        // even though `build` itself has nothing to do with Log or the handle. This is the "no
        // regression for genuine dynamic dispatch" half of the narrowing.
        var ir = LowerProgram(
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

        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldAllBe(c => !c.RuntimeManaged);
    }

    [Test]
    public void Mixed_program_with_one_handled_and_one_provider_only_capability_stays_conservative_program_wide()
    {
        // Clock is entirely provider-resolved (no handle ever touches it), but Log is dynamically
        // handled elsewhere in the same program. `_programHasDynamicCapabilityDispatch` is
        // whole-program, not per-capability, so `build` — unrelated to either capability — still
        // conservatively stays arena-managed. This documents the intentional granularity limit of
        // this phase (a coarser, but sound, signal) rather than a crash or an accidental
        // over-narrowing: the provider-only capability does not "leak" RC eligibility to the rest of
        // the program once any handle exists anywhere.
        var ir = LowerProgram(
            """
            capability Clock =
                | now : Unit -> Int

            provide Clock =
                | now = given (_) -> 1234

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
            let _ = Clock.now(Unit) in
            build(Unit)
            """);

        AllInstructions(ir).OfType<IrInst.ConcatStr>().ShouldAllBe(c => !c.RuntimeManaged);
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
