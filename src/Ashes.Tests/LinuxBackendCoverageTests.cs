using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxBackendCoverageTests
{
    [Test]
    public void Linux_backend_accepts_unoptimized_erased_rc_markers()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 42),
            new IrInst.RcDup(1, 0),
            new IrInst.RcDrop(1, "String"),
            new IrInst.Return(1),
        };
        var function = new IrFunction("entry", instructions, 0, 2, false);
        var program = new IrProgram(function, [], [], false, false, false, false, false, false);

        var bytes = new LinuxX64LlvmBackend().Compile(program);

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public async Task Linux_backend_keeps_stack_environment_alive_across_returned_call()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 0),
                new IrInst.CallKnown(2, "caller", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0,
            3,
            false);
        IrFunction caller = new(
            "caller",
            [
                new IrInst.AllocStack(0, 8),
                new IrInst.LoadConstInt(1, 42),
                new IrInst.StoreMemOffset(0, 0, 1),
                new IrInst.LoadConstInt(2, 0),
                new IrInst.CallKnown(3, "callee", 0, 2, EnvironmentIsStackAllocated: true),
                new IrInst.Return(3),
            ],
            2,
            4,
            true);
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.Return(0),
            ],
            2,
            1,
            true);
        IrProgram program = new(entry, [caller, callee], [], true, false, false, false, true, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
        result.ExitCode.ShouldBe(0);
    }

    [Test]
    public async Task Linux_backend_llvm_musttail_keeps_a_deep_mutual_tail_chain_stack_bounded()
    {
        // A non-self-recursive tail call between two distinct functions (unlike
        // loop-recognized self-recursion, which already gets an explicit IR back-edge) previously
        // got only LLVM's advisory `tail` marker — a hint, not a guarantee. ping/pong here mimic
        // a heterogeneous mutually-tail-recursive group (the doc's own motivating example: a
        // descent parser whose members differ in shape) that would never qualify for loop-merge
        // TCO. 10M mutual calls at ~tens of bytes of frame each would exceed the default 8 MB
        // stack by orders of magnitude if each call actually consumed a frame.
        //
        // Compiled explicitly at -O0: `tail` alone is not enough here — LLVM's own TailCallElim
        // pass, the thing that actually turns an advisory `tail` marker into a real sibling call,
        // only runs at -O1+. At the default -O2 this task's own fix and the pre-fix advisory-only
        // code are observably identical (both pass, since TailCallElim already does the work) —
        // -O0 is the level where `musttail`'s hard, codegen-level guarantee (independent of
        // whether any optimization pass runs at all) actually diverges from `tail`'s advisory
        // one, exactly matching this task's own doc prediction ("primarily valuable for -O0").
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 10_000_000),
                new IrInst.CallKnown(2, "ping", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0,
            3,
            false);
        IrFunction ping = BuildMutualTailCallFunction("ping", "pong");
        IrFunction pong = BuildMutualTailCallFunction("pong", "ping");
        IrProgram program = new(entry, [ping, pong], [], true, false, false, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: new BackendCompileOptions(BackendOptimizationLevel.O0)).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0, $"A deep mutual tail chain must not stack-overflow at -O0. stderr: {result.Stderr}");
        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_llvm_tco_loop_allocating_a_heap_cell_does_not_grow_the_stack_at_o0()
    {
        // A TCO loop that allocates a fresh heap cell each iteration runs the RC-allocator's
        // scratch-slot path, whose fixed-size allocas were once emitted at the loop-body insertion
        // point. At -O0 a non-entry-block fixed-size alloca is lowered as a runtime stack-pointer
        // adjustment reclaimed only by function return or llvm.stackrestore, so ~112 bytes of
        // scratch leaked per iteration and a 1,000,000-element build/walk overflowed the 8 MB stack
        // (segfault). Hoisting those scratch slots to the function entry block makes them single
        // fixed frame slots, reused every iteration. -O2 never showed the leak (mem2reg/SROA
        // promotes the scratch allocas away), so this must be exercised explicitly at -O0.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(
            """
            type Cell =
                | Cell(Int, Int)

            let recursive build = given k -> given acc ->
                if k == 0 then acc
                else build(k - 1)(Cell(k)(k + 1) :: acc)

            let recursive sum = given cells -> given acc ->
                match cells with
                    | [] -> acc
                    | Cell(x, _) :: rest -> sum(rest)(acc + x)

            Ashes.IO.print(sum(build(1000000)([]))(0))
            """);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: new BackendCompileOptions(BackendOptimizationLevel.O0)).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0, $"A 1M-iteration heap-allocating TCO loop must not overflow the stack at -O0. stderr: {result.Stderr}");
        result.Stdout.ShouldBe("500000500000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_does_not_musttail_a_call_in_a_function_that_allocates_stack_memory()
    {
        // Safety gate found during this task's own investigation, beyond what the doc's proposal
        // named: `AllocStack` is also used for a capability/effect handler frame (Lowering.
        // Capabilities.cs), whose pointer is installed into a dynamically-scoped global for the
        // whole `handle` body's extent — which can span a tail call later in the same function.
        // musttail is free to let LLVM reuse the caller's frame immediately, which would leave
        // that global pointing at overwritten memory. This function allocates stack memory (the
        // AllocStack below stands in for that frame) and then makes an otherwise fully
        // tail-position-eligible call — it must still compile and run correctly via the ordinary
        // advisory `tail` path, not attempt (and have LLVM's verifier reject, or worse, silently
        // miscompile) a musttail here. A shallow depth is enough: this test is about the gate
        // firing, not about stack-boundedness (which the function is NOT expected to guarantee
        // once it allocates stack memory of its own).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 1000),
                new IrInst.CallKnown(2, "hop", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0,
            3,
            false);
        IrFunction hop = new(
            "hop",
            [
                new IrInst.AllocStack(0, 8),
                new IrInst.LoadConstInt(1, 7),
                new IrInst.StoreMemOffset(0, 0, 1),
                new IrInst.LoadLocal(2, 1),
                new IrInst.LoadConstInt(3, 0),
                new IrInst.CmpIntLe(4, 2, 3),
                new IrInst.JumpIfFalse(4, "hop_recurse"),
                new IrInst.Return(2),
                new IrInst.Label("hop_recurse"),
                new IrInst.LoadLocal(5, 1),
                new IrInst.LoadConstInt(6, 1),
                new IrInst.SubInt(7, 5, 6),
                new IrInst.LoadConstInt(8, 0),
                new IrInst.CallKnown(9, "hop", 8, 7),
                new IrInst.Return(9),
            ],
            2,
            10,
            true);
        IrProgram program = new(entry, [hop], [], true, false, false, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: new BackendCompileOptions(BackendOptimizationLevel.O0)).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_llvm_preserves_a_capability_handler_frame_across_a_non_musttail_call()
    {
        // A direct reproduction of the specific hazard the AllocStack gate exists for: `hop`
        // installs a capability handler frame (StoreCapabilityHandler, exactly what
        // Lowering.Capabilities.cs's LowerHandleInstallFrames emits) — its pointer into a
        // stack-allocated frame — into the dynamically-scoped global, then makes an otherwise
        // fully tail-eligible call to `reader`, which reads back through that same global. If the
        // gate did not hold this call to advisory `tail`, `hop`'s stack-allocated frame — still
        // referenced by the global `reader` reads through — could be overwritten before `reader`
        // runs. Expected output is the marker value written into the frame, proving it survived.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = BuildCapabilityFrameSurvivalProgram();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: new BackendCompileOptions(BackendOptimizationLevel.O0)).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.Stdout.ShouldBe("777\n", "The caller's stack-allocated handler frame must survive the tail call intact.");
    }

    private static IrProgram BuildCapabilityFrameSurvivalProgram()
    {
        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 0),
                new IrInst.CallKnown(2, "hop", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0,
            3,
            false);
        IrFunction hop = new(
            "hop",
            [
                new IrInst.AllocStack(0, 8),
                new IrInst.LoadConstInt(1, 777),
                new IrInst.StoreMemOffset(0, 0, 1),
                new IrInst.StoreCapabilityHandler(0, 0),
                new IrInst.LoadConstInt(2, 0),
                new IrInst.CallKnown(3, "reader", 2, 2),
                new IrInst.Return(3),
            ],
            2,
            4,
            true);
        IrFunction reader = new(
            "reader",
            [
                new IrInst.LoadCapabilityHandler(0, 0),
                new IrInst.LoadMemOffset(1, 0, 0),
                new IrInst.Return(1),
            ],
            2,
            2,
            true);
        return new IrProgram(entry, [hop, reader], [], true, false, false, false, false, false)
        {
            CapabilityHandlerGlobals = 1,
        };
    }

    // if arg <= 0, return it directly; otherwise tail-call `otherLabel` with arg - 1.
    private static IrFunction BuildMutualTailCallFunction(string ownLabel, string otherLabel)
    {
        string recurseLabel = ownLabel + "_recurse";
        return new IrFunction(
            ownLabel,
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.LoadConstInt(1, 0),
                new IrInst.CmpIntLe(2, 0, 1),
                new IrInst.JumpIfFalse(2, recurseLabel),
                new IrInst.Return(0),
                new IrInst.Label(recurseLabel),
                new IrInst.LoadLocal(3, 1),
                new IrInst.LoadConstInt(4, 1),
                new IrInst.SubInt(5, 3, 4),
                new IrInst.LoadConstInt(6, 0),
                new IrInst.CallKnown(7, otherLabel, 6, 5),
                new IrInst.Return(7),
            ],
            2,
            8,
            true);
    }

    [Test]
    public async Task Linux_backend_runs_runtime_managed_adt_dup_and_drop()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(1, 42),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.RcDup(2, 0, RuntimeManaged: true),
            new IrInst.RcDrop(2, "Box", RuntimeManaged: true),
            new IrInst.GetAdtField(3, 0, 0),
            new IrInst.PrintInt(3),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(4, 0),
            new IrInst.Return(4),
        };
        var function = new IrFunction("entry", instructions, 0, 5, false);
        var program = new IrProgram(function, [], [], true, false, false, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_reports_runtime_rc_uniqueness_transitions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcIsUnique(1, 0),
            new IrInst.PrintBool(1),
            new IrInst.RcDup(2, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 0),
            new IrInst.PrintBool(3),
            new IrInst.RcDrop(2, "UnitBox", RuntimeManaged: true),
            new IrInst.RcIsUnique(4, 0),
            new IrInst.PrintBool(4),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.LoadConstInt(5, 0),
            new IrInst.Return(5),
        };
        var function = new IrFunction("entry", instructions, 0, 6, false);
        var program = new IrProgram(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\nfalse\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_runtime_drop_reuse_reuses_unique_cell()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(1, 42),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.DropReuse(2, 0, 1, RuntimeManaged: true),
            new IrInst.AllocReusing(3, 1, 1, 2, RuntimeManaged: true),
            new IrInst.LoadConstInt(4, 43),
            new IrInst.SetAdtField(3, 0, 4),
            new IrInst.GetAdtField(5, 3, 0),
            new IrInst.PrintInt(5),
            new IrInst.RcIsUnique(6, 3),
            new IrInst.PrintBool(6),
            new IrInst.RcDrop(3, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(7, 0),
            new IrInst.Return(7),
        };
        IrFunction function = new("entry", instructions, 0, 8, false);
        IrProgram program = new(function, [], [], true, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_shares_one_immortal_cell_per_reference_counted_nullary_constructor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A reference-counted nullary constructor carries only its tag: every allocation of one tag
        // is the same immortal cell, never unique, and retains and releases leave it alone.
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 2, 0, RuntimeManaged: true),
            new IrInst.AllocAdt(1, 2, 0, RuntimeManaged: true),
            new IrInst.CmpIntEq(2, 0, 1),
            new IrInst.PrintBool(2),
            new IrInst.AllocAdt(3, 3, 0, RuntimeManaged: true),
            new IrInst.CmpIntNe(4, 0, 3),
            new IrInst.PrintBool(4),
            new IrInst.RcIsUnique(5, 0),
            new IrInst.PrintBool(5),
            new IrInst.IsReferenceCounted(6, 0),
            new IrInst.PrintBool(6),
            new IrInst.RcDrop(0, "Choice", RuntimeManaged: true),
            new IrInst.RcDrop(1, "Choice", RuntimeManaged: true),
            new IrInst.GetAdtTag(7, 1),
            new IrInst.PrintInt(7),
            new IrInst.LoadConstInt(8, 0),
            new IrInst.Return(8),
        };
        IrFunction function = new("entry", instructions, 0, 9, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\ntrue\nfalse\ntrue\n2\n");
    }

    [Test]
    public async Task Linux_backend_runtime_drop_reuse_falls_back_when_cell_is_shared()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.DropReuse(2, 0, 1, RuntimeManaged: true),
            new IrInst.AllocReusing(3, 1, 1, 2, RuntimeManaged: true),
            new IrInst.CmpIntNe(4, 1, 3),
            new IrInst.PrintBool(4),
            new IrInst.RcIsUnique(5, 1),
            new IrInst.PrintBool(5),
            new IrInst.RcIsUnique(6, 3),
            new IrInst.PrintBool(6),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.RcDrop(3, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(7, 0),
            new IrInst.Return(7),
        };
        IrFunction function = new("entry", instructions, 0, 8, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\ntrue\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_runtime_list_reuse_preserves_untagged_layout_and_shared_fallback()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.Alloc(0, HeapLayouts.List.FixedAllocationSizeBytes, RuntimeManaged: true),
            new IrInst.LoadConstInt(1, 42),
            new IrInst.StoreMemOffset(0, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex), 1),
            new IrInst.DropReuse(2, 0, 2, RuntimeManaged: true),
            new IrInst.AllocReusing(3, 0, 2, 2, RuntimeManaged: true, ListCell: true),
            new IrInst.CmpIntEq(4, 0, 3),
            new IrInst.PrintBool(4),
            new IrInst.LoadConstInt(5, 43),
            new IrInst.StoreMemOffset(3, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex), 5),
            new IrInst.LoadMemOffset(6, 3, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)),
            new IrInst.PrintInt(6),
            new IrInst.RcDrop(3, "List", RuntimeManaged: true),

            new IrInst.Alloc(7, HeapLayouts.List.FixedAllocationSizeBytes, RuntimeManaged: true),
            new IrInst.LoadConstInt(8, 44),
            new IrInst.StoreMemOffset(7, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex), 8),
            new IrInst.RcDup(9, 7, RuntimeManaged: true),
            new IrInst.DropReuse(10, 7, 2, RuntimeManaged: true),
            new IrInst.AllocReusing(11, 0, 2, 10, RuntimeManaged: true, ListCell: true),
            new IrInst.CmpIntNe(12, 9, 11),
            new IrInst.PrintBool(12),
            new IrInst.LoadMemOffset(13, 9, HeapLayouts.List.PayloadWordOffsetBytes(HeapLayouts.ListHeadIndex)),
            new IrInst.PrintInt(13),
            new IrInst.RcDrop(9, "List", RuntimeManaged: true),
            new IrInst.RcDrop(11, "List", RuntimeManaged: true),
            new IrInst.LoadConstInt(14, 0),
            new IrInst.Return(14),
        };
        IrFunction function = new("entry", instructions, 0, 15, false);
        IrProgram program = new(function, [], [], true, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n43\ntrue\n44\n");
    }

    [Test]
    public async Task Linux_backend_runtime_reuse_child_transfer_dups_for_null_token()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.AllocAdt(1, 0, 1, RuntimeManaged: true),
            new IrInst.SetAdtField(1, 0, 0),
            new IrInst.RcDup(2, 1, RuntimeManaged: true),
            new IrInst.GetAdtField(3, 1, 0),
            new IrInst.DropReuse(4, 1, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(5, 0),
            new IrInst.CmpIntNe(6, 4, 5),
            new IrInst.JumpIfFalse(6, "transfer_dup"),
            new IrInst.StoreLocal(0, 3),
            new IrInst.Jump("transfer_continue"),
            new IrInst.Label("transfer_dup"),
            new IrInst.RcDup(7, 3, RuntimeManaged: true),
            new IrInst.StoreLocal(0, 7),
            new IrInst.Label("transfer_continue"),
            new IrInst.LoadLocal(8, 0),
            new IrInst.AllocReusing(9, 1, 1, 4, RuntimeManaged: true),
            new IrInst.SetAdtField(9, 0, 8),
            new IrInst.RcIsUnique(10, 3),
            new IrInst.PrintBool(10),
            new IrInst.RcDrop(3, "Child", RuntimeManaged: true),
            new IrInst.RcDrop(2, "Parent", RuntimeManaged: true),
            new IrInst.RcIsUnique(11, 8),
            new IrInst.PrintBool(11),
            new IrInst.RcDrop(8, "Child", RuntimeManaged: true),
            new IrInst.RcDrop(9, "Parent", RuntimeManaged: true),
            new IrInst.LoadConstInt(12, 0),
            new IrInst.Return(12),
        };
        IrFunction function = new("entry", instructions, 1, 13, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("false\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_lowers_copy_adt_rebuild_to_runtime_reuse()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_releases_incompatible_runtime_reuse_token()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Empty
                | One(Int)

            let choice = One(1)
            match choice with
                | Empty -> Empty
                | One(_) -> Empty
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_recursive_adt_after_releasing_old_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(_, value, _) -> Node(Leaf)(value + 1)(Leaf)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_recursive_adt_with_transferred_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(left, value, _) -> Node(left)(value + 1)(Leaf)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_nested_record_with_transferred_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let rebuilt =
                let node = Node(child = Leaf(value = 40), bonus = 2) in
                match node with
                    | Node(child, bonus) -> Node(child = child, bonus = bonus + 1)
            match rebuilt with
                | Node(Leaf(value), bonus) -> Ashes.IO.print(value + bonus)
            """);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\n");
    }

    [Test]
    public async Task Linux_backend_reuses_pointer_variant_with_record_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let rebuilt =
                let choice = Full(Leaf(value = 40))(2) in
                match choice with
                    | Empty -> Empty
                    | Full(child, bonus) -> Full(child)(bonus + 1)
            match rebuilt with
                | Empty -> Ashes.IO.print(0)
                | Full(Leaf(value), bonus) -> Ashes.IO.print(value + bonus)
            """);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\n");
    }

    [Test]
    public async Task Linux_backend_shares_existing_runtime_record_child_with_parent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let leaf = Leaf(value = 40)
            let node = Node(child = leaf, bonus = 2)
            Ashes.IO.print(node.bonus + leaf.value)
            """);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_string_concat()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let text = \"ab\" + \"cd\" in Ashes.IO.print(text)");
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("abcd\n");
    }

    [Test]
    public async Task Linux_backend_transfers_directly_escaping_runtime_string_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcEscapingStringProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_transfers_direct_known_function_runtime_string_result_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcKnownFunctionStringProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        program.EntryFunction.Instructions.Any(instruction => instruction is IrInst.CallClosure).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        program.Functions.Any(function =>
            function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
            && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_transfers_direct_known_function_runtime_bytes_and_bigint_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        program.Functions.Any(function =>
            function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true })
            && function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true })
            && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("133\n");
    }

    [Test]
    public async Task Linux_backend_transfers_directly_escaping_runtime_bytes_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcEscapingBytesProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("19\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_bytes_append()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.append(Ashes.Byte.fromText(\"ab\"))(Ashes.Byte.fromText(\"cd\")) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_append_byte()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText(\"ab\"))(33u8) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_bytes_from_list()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_byte_singleton()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.singleton(7u8) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_empty_bytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.empty(Unit) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_fixed_width_bytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcFixedWidthBytesProgram(iterations: 1));
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("14\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_byte_subtext()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Byte.subText(Ashes.Byte.fromText(\"abcdef\"))(1)(3) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram nestedArenaProducer = LowerProgram(BuildRuntimeRcOwnedHeapClosureScratchProgram());
        AllInstructions(nestedArenaProducer).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(nestedArenaProducer).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("bcd\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_text_from_int()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Text.fromInt(-42) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromInt(-42) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(escaping).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_text_parse_int_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextParseIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseInt(\"123\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("123\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_text_parse_float_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextParseFloatProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseFloat(\"1.5\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_bigint_to_int_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntToIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let converted = Ashes.Number.BigInt.toInt(123N) in converted");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("123\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_parse_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntParseResultProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseBigInt(\"123\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_escaping_text_uncons_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextUnconsProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Maybe", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        IrProgram escaping = LowerProgram("let split = Ashes.Text.uncons(\"abc\") in split");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("6\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_text_to_hex()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Text.toHex(48879) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.toHex(48879) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0xbeef\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_ascii_case_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcAsciiCaseTextProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(2);

        IrProgram escaping = LowerProgram("let text = Ashes.Text.asciiUpper(\"hello\") in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("HELLO\nhello\n1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_float_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcFloatTextProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFormatFloat { RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromFloat(12.25) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("12.25\n12.250\n1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_from_int()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntFromIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let escaped = (let value = Ashes.Number.BigInt.fromInt(42) in value) in Ashes.Number.BigInt.compare(escaped)(escaped)");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(escaping).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_arithmetic()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntArithmeticProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBe(5);
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(5);

        IrProgram escaping = LowerProgram("let value = Ashes.Number.BigInt.add(40N)(2N) in value");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_measured_bigint_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntTextProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromBigInt(42N) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("30\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_copy_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcCopyClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Function", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let n = 1 in let f = if n > 0 then given (x) -> x + n else given (x) -> x + n in f");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("2\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_heap_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedHeapClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_bytes_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedBytesClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram nestedProducer = LowerProgram(BuildNestedRcOwnedBytesClosureScratchProgram());
        AllInstructions(nestedProducer).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue(
            "fresh Bytes provenance should make nested owned producers closure-safe without a name whitelist.");

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_bigint_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedBigIntClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram arithmeticProducer = LowerProgram(BuildRuntimeRcOwnedBigIntClosureScratchProgram());
        AllInstructions(arithmeticProducer).Any(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(arithmeticProducer).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runs_optimized_runtime_rc_ownership_transfer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.RcIsUnique(2, 1),
            new IrInst.PrintBool(2),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.LoadConstInt(3, 0),
            new IrInst.Return(3),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(optimized).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n");
    }

    [Test]
    public async Task Linux_backend_empty_value_uniqueness_test_answers_not_unique()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The empty list is the null pointer and carries no RcHeader, so the unguarded form of this
        // test would read the count at -16. An empty value also owns no cell anyone could reuse.
        List<IrInst> instructions = new()
        {
            new IrInst.LoadConstInt(0, 0),
            new IrInst.RcIsUnique(1, 0, MayBeEmpty: true),
            new IrInst.PrintBool(1),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 3, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("false\n");
    }

    [Test]
    public async Task Linux_backend_possibly_empty_uniqueness_test_still_answers_for_a_present_value()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcIsUnique(1, 0, MayBeEmpty: true),
            new IrInst.PrintBool(1),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 3, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n");
    }

    [Test]
    public async Task Linux_backend_runs_optimized_branch_sunk_runtime_rc_dup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.LoadConstBool(2, true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.JumpIfFalse(2, "else"),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.PrintBool(3),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.Label("end"),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 1 }).ShouldBe(1);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(optimized).ConfigureAwait(false);

        result.Stdout.ShouldBe("false\n");
    }

    [Test]
    public async Task Linux_backend_keeps_runtime_rc_child_while_parent_is_shared()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.AllocAdt(1, 0, 1, RuntimeManaged: true),
            new IrInst.SetAdtField(1, 0, 0),
            new IrInst.RcDup(2, 1, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.JumpIfFalse(3, "first_parent_shared"),
            new IrInst.RcDrop(0, "Leaf", RuntimeManaged: true),
            new IrInst.Label("first_parent_shared"),
            new IrInst.RcDrop(1, "Node", RuntimeManaged: true),
            new IrInst.RcIsUnique(4, 0),
            new IrInst.PrintBool(4),
            new IrInst.RcIsUnique(5, 2),
            new IrInst.JumpIfFalse(5, "second_parent_shared"),
            new IrInst.RcDrop(0, "Leaf", RuntimeManaged: true),
            new IrInst.Label("second_parent_shared"),
            new IrInst.RcDrop(2, "Node", RuntimeManaged: true),
            new IrInst.LoadConstInt(6, 0),
            new IrInst.Return(6),
        };
        var function = new IrFunction("entry", instructions, 0, 7, false);
        var program = new IrProgram(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n");
    }

    [Test]
    public void Linux_backend_compile_should_emit_elf_header_for_int_program()
    {
        var bytes = CompileForLinux("Ashes.IO.print(40 + 2)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForLinux("let z = 20 in let f = given (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_not_emit_a_constant_image_for_simple_programs()
    {
        var first = CompileForLinux("Ashes.IO.print(40 + 2)");
        var second = CompileForLinux("Ashes.IO.print(40 + 3)");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Linux_backend_parallel_worker_stack_size_tunable_is_honored()
    {
        var ir = LowerExpression("match Ashes.Task.Parallel.both(given (u) -> 3 + 4)(given (u) -> 5 + 6) with | (a, b) -> Ashes.IO.print(a + b)");

        byte[] unset = new LinuxX64LlvmBackend().Compile(ir);
        byte[] explicitDefault = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 1L * 1024 * 1024));
        byte[] custom = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 8L * 1024 * 1024));

        // Default is unchanged: leaving the tunable unset matches an explicit 1 MiB worker stack.
        explicitDefault.ShouldBe(unset);
        // The tunable is honored: an 8 MiB worker stack changes the emitted image.
        custom.ShouldNotBe(unset);
    }

    [Test]
    public void Linux_backend_compile_should_support_string_concat_programs()
    {
        var bytes = CompileForLinux("Ashes.IO.print(\"hello \" + \"world\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_large_rdata_programs()
    {
        var bytes = CompileForLinux($"Ashes.IO.print(\"{new string('a', 20000)}\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_program_args_programs()
    {
        var bytes = CompileForLinux("match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        AssertLinuxLlvmCompiles(LowerExpression("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        AssertLinuxLlvmCompiles(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_program_args_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("match Ashes.IO.args with | a :: b :: [] -> 1 | _ -> 0"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_read_line_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.IO.readLine(Unit) with | None -> 0 | Some(text) -> 1"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_file_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.IO.File.exists("present.txt") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_network_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match await Ashes.Net.Http.get("http://127.0.0.1:8080/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_print_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.write(\"hi\")"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("let z = 20 in let f = given (x) -> x + z in f(22)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)"""));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("let z = 20 in let f = given (x) -> x + z in Ashes.IO.print(f(22))").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_preserve_nested_string_results_across_scope_cleanup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let prefix = "outer" in
            let text =
                match 1 with
                    | 1 ->
                        let suffix = "inner" in
                        prefix + suffix
                    | _ -> "bad"
            in Ashes.IO.print(text)
            """).ConfigureAwait(false);
        result.Stdout.ShouldBe("outerinner\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_program_args_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")",
            ["first", "second"]).ConfigureAwait(false);
        result.Stdout.ShouldBe("first:second\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_read_program_args_inside_function_bodies()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            LowerProgram(
            """
            let recursive count (args: List(Str)) (acc: Int) =
                match args with
                    | [] -> acc
                    | _ :: tail -> count(tail)(acc + 1)

            let countArgs (fallback: Int) = count(Ashes.IO.args)(fallback)

            let firstArg (unit: Int) =
                match Ashes.IO.args with
                    | head :: _ -> head
                    | [] -> "none"

            Ashes.IO.print(firstArg(0) + ":" + Ashes.Text.fromInt(countArgs(100)) + ":" + Ashes.Text.fromInt(count(Ashes.IO.args)(100)))
            """),
            ["first", "second"]).ConfigureAwait(false);
        result.Stdout.ShouldBe("first:102:102\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_read_line_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "hello\n").ConfigureAwait(false);
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_return_none_at_read_line_eof()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "").ConfigureAwait(false);
        result.Stdout.ShouldBe("none\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_read_text_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "hello.txt"), "hello").ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("hello.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_missing_file_read_errors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("missing.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("Ashes.IO.File.readText() failed\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_invalid_utf8_file_read_errors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, "invalid_utf8.bin"), [0xFF, 0xFE, 0xFD]).ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("invalid_utf8.bin") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("Ashes.IO.File.readText() encountered invalid UTF-8\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_write_text_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.writeText("out.txt")("hello") with | Error(msg) -> Ashes.IO.print(msg) | Ok(_) -> match Ashes.IO.File.readText("out.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_directory_remove_tree_should_not_follow_symlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tmpDir = CreateTempDirectory();
        try
        {
            string outside = Path.Combine(tmpDir, "outside");
            string tree = Path.Combine(tmpDir, "tree");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(tree);
            await File.WriteAllTextAsync(Path.Combine(outside, "kept.txt"), "kept").ConfigureAwait(false);
            Directory.CreateSymbolicLink(Path.Combine(tree, "link"), outside);

            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.Directory.removeTree("tree") with | Error(message) -> Ashes.IO.print(message) | Ok(_) -> Ashes.IO.print("removed")""",
                workingDirectory: tmpDir).ConfigureAwait(false);

            result.Stdout.ShouldBe("removed\n");
            File.Exists(Path.Combine(outside, "kept.txt")).ShouldBeTrue();
            Directory.Exists(tree).ShouldBeFalse();
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_file_make_executable_should_set_0755_and_reject_symlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string tmpDir = CreateTempDirectory();
        try
        {
            string file = Path.Combine(tmpDir, "tool");
            await File.WriteAllTextAsync(file, "content").ConfigureAwait(false);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(tmpDir, "link"), file);

            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.makeExecutable("tool") with | Error(message) -> Ashes.IO.print(message) | Ok(_) -> match Ashes.IO.File.makeExecutable("link") with | Ok(_) -> Ashes.IO.print("symlink-wrong") | Error(_) -> Ashes.IO.print("executable-ok")""",
                workingDirectory: tmpDir).ConfigureAwait(false);

            result.Stdout.ShouldBe("executable-ok\n");
            File.GetUnixFileMode(file).ShouldBe(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_uncons_unicode_scalars()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.uncons("é!") with | None -> Ashes.IO.print("empty") | Some((head, tail)) -> Ashes.IO.print(Ashes.Rune.toText(head) + "|" + tail)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("é|!\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_uncons_long_json_like_strings()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let sample = "{ \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
            in match Ashes.Text.uncons(sample) with
            | None -> Ashes.IO.print("none")
            | Some((head, tail)) ->
                if head == '{'
                then if tail == " \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
                then Ashes.IO.print("ok")
                else Ashes.IO.print("bad")
                else Ashes.IO.print("bad")
            """).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_integers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseInt("-42") with | Ok(value) -> Ashes.IO.print(value) | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_floats_with_exponents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseFloat("1e3") with | Ok(value) -> if value == 1000.0 then Ashes.IO.print("ok") else Ashes.IO.print("bad") | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_ints_floats_and_hex()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.fromInt(-42) + "|" + Ashes.Text.fromFloat(0.0 - 12.25) + "|" + Ashes.Text.toHex(48879))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42|-12.25|0xbeef\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_floats_with_fixed_precision()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.formatFloat(3.141592653589793)(9) + "|" + Ashes.Text.formatFloat(0.0 - 12.25)(3) + "|" + Ashes.Text.formatFloat(2.5)(0))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("3.141592654|-12.250|3\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_exists_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "present.txt"), "x").ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match (Ashes.IO.File.exists("present.txt"), Ashes.IO.File.exists("missing.txt")) with | (Ok(a), Ok(b)) -> Ashes.IO.print((if a then "true" else "false") + ":" + (if b then "true" else "false")) | (Error(msg), _) -> Ashes.IO.print(msg) | (_, Error(msg)) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("true:false\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_connect_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
            async _ => await Task.Delay(100).ConfigureAwait(false)).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_resolve_localhost_tcp_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("localhost")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
            async _ => await Task.Delay(100).ConfigureAwait(false)).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_send_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match await Ashes.Net.Tcp.send(sock)("hello") with | Ok(n) -> Ashes.IO.print(n) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    (await ReadTextAsync(stream, 64).ConfigureAwait(false)).ShouldBe("hello");
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("5\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_receive_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.receive(sock)(64) with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var payload = Encoding.UTF8.GetBytes("hello");
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_http_get_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("http://__HOST__:__PORT__/hello") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("GET /hello HTTP/1.1");
                    request.ShouldContain("Host: 127.0.0.1");
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("hello from http\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_http_post_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.post("http://__HOST__:__PORT__/echo")("hello") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("POST /echo HTTP/1.1");
                    request.ShouldContain("Content-Length: 5");
                    request.ShouldContain("\r\n\r\nhello");
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nposted");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("posted\n");
    }

    [Test]
    public async Task Linux_backend_llvm_task_all_waiting_on_a_slow_https_peer_does_not_spin_or_grow_the_stack()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Task A's TLS step yields once between its TCP connect and the handshake, parked with no
        // wait descriptor while task B is still in the composite. The aggregate wait must treat that
        // leaf as runnable, not register its zero WaitHandle: fd 0 (here a closed stdin pipe, always
        // ready) would stay in the persistent epoll set and turn the 1.5 s wait for the response into
        // a busy loop, and the loop's per-iteration stack slots then overflowed the native stack.
        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """
            let taskA =
                async(let _ = Ashes.IO.write("Astart")
                in
                    match await Ashes.Net.Http.get("https://__HOST__:__PORT__/x") with
                        | Ok(t) -> Ashes.IO.write(t)
                        | Error(m) -> Ashes.IO.write(m))
            in
            let taskB =
                async(match await Ashes.Task.task(0) with
                    | Ok(_) -> Ashes.IO.write("B")
                    | Error(_) -> Ashes.IO.write("Berr"))
            in
                match [taskA, taskB]
                |> Ashes.Task.all
                |> Ashes.Task.run with
                    | Ok(_) -> Ashes.IO.print("")
                    | Error(_) -> Ashes.IO.print("end-err")
            """,
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET /x HTTP/1.1");
                await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nslow");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost",
            stdin: string.Empty).ConfigureAwait(false);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("AstartBslow\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_https_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);
        result.Stdout.ShouldBe("hello from https\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_trust_failures_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            trustServerCertificate: false,
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_hostname_mismatches_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "127.0.0.1",
            certificateHost: "localhost",
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: NotValidForName\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_return_first_completed_https_race_task_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Task.race([Ashes.Net.Http.get("https://__HOST__:__PORT__/a"), Ashes.Net.Http.get("https://__HOST__:__PORT__/b")]) with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("Host: localhost");
                // Both endpoints respond with the same body ("ok") so the test result is
                // deterministic regardless of which Async.race task technically completes first
                // (avoids timing flakiness on loaded CI runners).
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost",
            expectedClientCount: 2,
            tolerateClientDisconnect: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_treat_https_close_notify_eof_as_end_of_body()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/empty") with | Ok(text) -> if text == "" then "empty" else "bad:" + text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET /empty HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);

        result.Stdout.ShouldBe("empty\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_http_non_success_statuses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("http://__HOST__:__PORT__/missing") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\nmissing");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("HTTP 404\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_float_arithmetic_and_comparisons()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_heap_backed_tuple_and_list_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_string_compare_and_concat_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (\"he\" + \"llo\") == \"hello\" then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_print_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.write(\"hi\")").ConfigureAwait(false);
        result.Stdout.ShouldBe("hi");
    }

    [Test]
    public async Task Linux_backend_llvm_buffered_stdout_should_bypass_for_oversized_values_and_keep_order()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let recursive grow : Int -> Str -> Str -> Str =
                given (n) -> given (piece) -> given (text) ->
                if n <= 0 then text else grow(n - 1)(piece)(text + piece)
            let first = grow(40000)("a")("")
            let second = grow(30000)("b")("")
            let exact = grow(65536)("c")("")
            let oversized = grow(70000)("d")("")
            let bufferedFirst = Ashes.IO.writeBuffered(first)
            let bufferedSecond = Ashes.IO.writeBuffered(second)
            let bufferedExact = Ashes.IO.writeBuffered(exact)
            let bufferedOversized = Ashes.IO.writeBuffered(oversized)
            let direct = Ashes.IO.write("|")
            Ashes.IO.writeBufferedLine("done")
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe(
            new string('a', 40000)
            + new string('b', 30000)
            + new string('c', 65536)
            + new string('d', 70000)
            + "|done\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_adt_field_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_runtime_rc_copy_adt_match()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Ashes.IO.print(value)
                | Right(value) -> Ashes.IO.print(value + 1)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_runtime_rc_copy_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let choice = Left(1) in
                    match choice with
                        | Left(value) -> loop(n - 1)(total + value)
                        | Right(value) -> loop(n - 1)(total + value + 1)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("20000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_allocator_reuses_mixed_size_blocks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Small =
                | Small(Int)

            type Large =
                | Large(Int, Int, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let small = Small(1) in
                    match small with
                        | Small(a) ->
                            let large = Large(1)(1)(1) in
                            match large with
                                | Large(b, c, d) -> loop(n - 1)(total + a + b + c + d)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("80000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_recursive_runtime_rc_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Node(Leaf)(20)(Leaf))(42)(Node(Leaf)(22)(Leaf))
            match tree with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) -> Ashes.IO.print(value)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_share_recursive_runtime_rc_adt_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let child = Node(Leaf)(20)(Leaf)
            let tree = Node(child)(42)(Leaf)
            match tree with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) ->
                    match child with
                        | Leaf -> Ashes.IO.print(value)
                        | Node(_, childValue, _) -> Ashes.IO.print(value + childValue)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("62\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_share_recursive_runtime_rc_adt_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let child = Node(Leaf)(20)(Leaf) in
                    let tree = Node(child)(42)(Leaf) in
                    match tree with
                        | Leaf -> loop(n - 1)(total)
                        | Node(_, value, _) ->
                            match child with
                                | Leaf -> loop(n - 1)(total + value)
                                | Node(_, childValue, _) -> loop(n - 1)(total + value + childValue)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("1240000\n");
    }

    [Test]
    [Skip("Passes only with the recursion-admitting reclamation classifiers, which are not landed: "
        + "they cut the self-hosted probe from 7.35 GB to 0.90 GB but segfault the self-hosted "
        + "semantics suite in collectCoreFunctionUses. Un-skip with a sound version.")]
    public async Task Linux_backend_recursive_accumulator_loop_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop threading a list of records whose element type is SELF-RECURSIVE, rebuilt once per
        // stage. IsArenaDeepCopyAdtLayout used to reject any type it re-entered, so such an element
        // had no deep-copy kind, TcoBackEdgeAllArgsCopyable refused, and the loop emitted no
        // back-edge reset at all — every stage's allocation stayed for the whole loop (24 MB at 16
        // stages here, and 7.35 GB compiling one self-hosted semantics module). The reclamation
        // classifiers now admit the recursion the synthesized copier already handles through its
        // env[0] self-closure, so the reset is expressible and the loop runs in constant memory.
        int[] stageCounts = [4, 8, 16];
        List<MemoryExecutionResult> samples = new(stageCounts.Length);
        foreach (int stages in stageCounts)
        {
            IrProgram ir = LowerProgram(BuildRecursiveAccumulatorLoopProgram(stages));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("10000\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("recursive accumulator loop", samples, maxRssKb: 16_000);
    }

    [Test]
    [Skip("The remaining half of the self-hosting memory defect: a straight-line `let` chain has no "
        + "reset point at all, so admitting recursive accumulators to the back-edge reset (which "
        + "fixed the loop half) does not reach it. Un-skip with the chain fix.")]
    public async Task Linux_backend_straight_line_rebind_chain_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A straight-line `let ... in` chain, each stage rebuilding a pointer-bearing record from
        // the previous one. Only the newest value is ever live: every earlier stage is dead the
        // moment the next one consumes it, so resident memory must not grow with the chain length.
        // This is the shape of the self-hosted optimizer's `optimizeIrProgramWithOptions`, where
        // roughly twenty whole-program stages are threaded through nested lets and each stage's
        // intermediate stays resident (measured 2026-09-16: ~1.4 MB per stage here, 5.2 GB across
        // the real optimizer). A tail-recursive loop hides this — its back-edge reset reclaims the
        // window every iteration — so the chain must be straight-line to expose it.
        int[] stageCounts = [8, 16, 32];
        List<MemoryExecutionResult> samples = new(stageCounts.Length);
        foreach (int stages in stageCounts)
        {
            IrProgram ir = LowerProgram(BuildStraightLineRebindChainProgram(stages));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("20000\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("straight-line rebind chain", samples, maxRssKb: 32_000);
    }

    [Test]
    public async Task Linux_backend_record_update_fresh_call_result_field_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A record update whose new field value is a call's owned reference-counted result. The
        // update's cell is an arena one, which never releases what it holds, so the fresh list needs
        // an owner of its own: without one the function's result normalization takes a second
        // reference and the first is never released, one whole list per round.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordUpdateFreshCallResultFieldProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 41}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record update with a fresh call-result field", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_record_update_successor_of_loop_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop whose state successor is an update of its own parameter: the unchanged list field
        // and the tail of the consed field are loop-parameter reads stored into arena cells. The
        // back edge's copy of the dying record borrows them, so the construction takes no reference
        // of its own; one taken and never released left a list cell behind per iteration.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordUpdateSuccessorOfLoopParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 162}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record update successor of a loop parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_literal_successor_borrowing_a_binding_of_an_arena_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A record literal successor holds `Some(span)`, a binding matched out of a list parameter the
        // loop keeps in the arena: the construction only borrows it, so the back edge's copy must not
        // release it the way it releases the retained fields beside it. Released, the spans of the
        // shared input list were freed in the first round and read freed in the second.
        int[] roundCounts = [1000, 4000, 16000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLiteralSuccessorBorrowingArenaBindingProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 80}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("literal successor borrowing an arena parameter's binding", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_exit_record_holding_its_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop whose exit arm builds a record holding its record parameter, returned and copied by
        // the result normalization, and one that builds such a record only to match its scalar field:
        // neither cell's retain of the parameter had a release, so each round left the state behind.
        int[] roundCounts = [50_000, 200_000, 800_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopExitRecordHoldingParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(long)rounds * (rounds + 1)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop exit record holding its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_worklist_front_walked_and_refilled_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist whose front queue is walked by its consumed tail and refilled from the reversed
        // back queue, each step pairing the grown back queue with a grown tree. Left in the arena,
        // the front borrowed the heads of the earlier pairs, so none of them could be released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildWorklistFrontWalkedAndRefilledProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 10}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist front walked and refilled", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_empty_list_field_under_cons_pattern_handed_to_curried_call_should_run()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop over reference-counted boxes binds each box's list of items under a cons pattern
        // and hands the list to a curried helper while the binding stays live past the call. The
        // duplicate taken for the call skips an empty list, a null reference with no header, as
        // the binding's own duplicate does; reading that header crashed the program.
        IrProgram ir = IrOptimizer.Optimize(LowerProgramWithStandardTraits(EmptyListFieldUnderConsPatternProgram));
        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(ir).ConfigureAwait(false);
        result.Stdout.ShouldBe("24\n");
    }

    [Test]
    public async Task Linux_backend_pattern_string_tested_by_curried_call_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop tests each box's label, bound under a cons pattern, with a curried library call and
        // branches on the result, whose last read is the branch itself. The label's release goes
        // before the branch; after it, the release sat on the fallthrough arm alone.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildPatternStringTestedByCurriedCallProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("pattern string tested by a curried call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_item_loop_closes_rebuilt_from_call_parts_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop over items threads a list of pending closes beside a state record: one arm conses a
        // helper's result onto the closes, another replaces them with a part matched out of a
        // helper's result. Both are accumulator edges, so the closes live on the reference-counted
        // heap and every round's cells are released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithSourceLocations(BuildItemLoopClosesRebuiltFromCallPartsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 27}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("item loop closes rebuilt from call parts", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_worklist_back_grown_and_emptied_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist's back queue is grown by a cons of the front's head and emptied when it is
        // reversed into the front. The queue lives on the reference-counted heap, so the cells of
        // each step are released once the front walks past them.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithSourceLocations(BuildWorklistBackGrownAndEmptiedProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 60}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist back queue grown and emptied", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_pattern_list_passed_to_borrowing_call_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop searches each box's item list, bound under a cons pattern, with a helper that
        // only reads it and returns early with what it found. The call borrows the list, which
        // stays live past it, so the call takes no duplicate of the list that nothing releases.
        // Lowered with source locations: the borrowing call is recorded as the located
        // instruction the lowering emits, which placement looks up.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithSourceLocations(BuildPatternListPassedToBorrowingCallProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 483}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("pattern list passed to a borrowing call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_variant_node_holding_walked_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop growing a chain of variant nodes, each holding a head the loop walked out of a
        // list of strings. The chain is normalized at the back edge: the arena node built for it
        // retains its head until the copy takes its own reference, and releases it right after,
        // since the walked cell (and the head's other reference with it) is released first.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildVariantNodeHoldingWalkedHeadProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 11}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("variant node holding a walked head", samples, maxRssKb: 16_000);
    }

    [Test]
    public void Worklist_front_whose_heads_reach_a_tree_only_as_keys_is_placed_on_the_reference_counted_heap()
    {
        // A worklist front walked and refilled while each walked head is marked in a tree. The
        // tree receives only the front's string heads, which it retains, never the front's spine,
        // so it does not keep the front in the arena, where each refill was never released. (The
        // tree's own updates grow the arena per round, so this is checked on the placement.)
        Diagnostics diagnostics = new();
        Program program = new Parser(BuildWorklistFrontHeadsReachTreeOnlyAsKeysProgram(3), diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.Lower(program);
        diagnostics.ThrowIfAny();
        IReadOnlyList<TcoParamPlacementTrace> decisions = lowering.GetTcoPlacementDecisions("solve")
            ?? throw new InvalidOperationException("No TCO placement snapshot for 'solve'.");
        TcoParamPlacementTrace front = decisions.Single(trace => string.Equals(trace.Current.ParameterName, "front", StringComparison.Ordinal));
        front.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
    }

    [Test]
    public void Map_loop_successor_bound_by_let_releases_the_earlier_map_at_the_back_edge()
    {
        // A map handed on through a let binding of the call that built it, on the general path a
        // map takes without its reuse specialization (the path stage 1 compiles). The binding is
        // the owned result itself, so the back edge releases the previous iteration's map, as it
        // does when the call is written in the argument's place. (The map's updates grow the arena
        // per round on this path, so this is checked on the lowered loop.)
        GeneralRcDropsInLoop(BuildMapLoopSuccessorBoundByLetProgram(3), "solve").ShouldBe(3);
    }

    [Test]
    public void Table_record_is_released_where_the_table_is_handed_on_unchanged()
    {
        // A map of records threaded through a loop that merges each key's earlier record into the
        // map or hands the map on unchanged, on the map's general path. The map position only ever
        // receives the parameter itself or a call's owned result, which holds the earlier record's
        // parts by references of its own, so the earlier record is released at the edge that hands
        // the map on unchanged.
        DropperCallsInLoop(BuildTableMergedOrHandedOnProgram(3), "solve", "__rcdrop_record").ShouldBe(5);
    }

    [Test]
    public void Map_loop_predecessor_is_released_past_a_queue_of_strings_handed_on_unchanged()
    {
        // A back queue of strings handed on unchanged while the map of queued keys is rebuilt, on
        // the map's general path: the queue holds nothing of an earlier map but strings it retains,
        // so the back edge releases the earlier map.
        GeneralRcDropsInLoop(BuildMapLoopPredecessorPastQueueOfStringsProgram(3), "solve").ShouldBe(7);
    }

    // The general droppers a loop's closure helper calls, the loop lowered without reuse
    // specializations.
    private static int GeneralRcDropsInLoop(string source, string loopName)
        => DropperCallsInLoop(source, loopName, "__rcdrop_general");

    // The droppers of one family (a label prefix) a loop's closure helper calls, the loop lowered
    // without reuse specializations.
    private static int DropperCallsInLoop(string source, string loopName, string dropperPrefix)
    {
        IrProgram ir = LowerProgramWithImportsWithoutReuse(source);
        IrFunction loop = ir.Functions
            .Where(function => function.Origin is { Kind: IrFunctionOriginKind.ClosureHelper, Source.SourceName: var name }
                && string.Equals(name, loopName, StringComparison.Ordinal))
            .Single(function => function.Instructions.Any(instruction =>
                instruction is IrInst.Jump { Target: var target }
                    && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        return loop.Instructions.Count(instruction =>
            instruction is IrInst.CallKnown { FuncLabel: var label }
                && label.StartsWith(dropperPrefix, StringComparison.Ordinal));
    }

    [Test]
    public async Task Linux_backend_generic_result_beside_owned_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returning a generic list builds an owned record it only tests. The record is
        // out of the result's reach, so it is released at the return.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericResultBesideOwnedRecordProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(rounds - 2) * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic result beside an owned record", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_opaque_handle_result_of_owned_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function hands an owned record a call built to a function returning an opaque foreign
        // handle. The handle cannot hold the record, so it is released at the return.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildOpaqueHandleResultOfOwnedRecordProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("opaque handle result of an owned record", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_result_hands_over_owned_call_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function with a generic error type returns an owned call result as it is in its `Ok`
        // arm. The caller knows the concrete type, asks for the result and takes it over.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericResultHandsOverOwnedCallResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(long)rounds * (rounds + 1) / 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic result handing over an owned call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_result_wraps_owned_call_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function with a generic error type wraps an owned call result in `Ok`. The caller knows
        // the concrete type, asks for the result and takes over a copy holding the owned value.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericResultWrapsOwnedCallResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1) / 2) + rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic result wrapping an owned call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_contract_parameter_returned_as_is_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns its contract-typed parameter as it is. Its entry adopts the owned call
        // result the caller hands over, and its return hands that reference on.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildContractParameterReturnedAsIsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(long)rounds * (rounds + 1) / 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("contract parameter returned as it is", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_owned_value_behind_contract_call_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function with a generic error type passes an owned value to a call of the contract's
        // types and returns that call's result, which holds references of its own: the owned value
        // is released at the return.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildOwnedValueBehindContractCallResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1) / 2) + rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("owned value behind a contract call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_record_update_of_unplaced_loop_parameter_passed_to_call_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop whose record parameter's type is unresolved at loop entry passes a record update of
        // it to a call: the update's unchanged fields take retain markers that become real retains
        // once the parameter is placed on the reference-counted heap at the back edge, and they are
        // released after the call like the retains of a parameter placed from the start.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildRecordUpdateOfUnplacedLoopParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 4}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record update of an unplaced loop parameter passed to a call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_recursive_producer_over_tuple_scrutinee_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A producer matches `(ownership, 0)` and conses the head it binds from the list's position:
        // that head descends from the list parameter as in a match on the list alone, so the cell is
        // reference-counted and the caller releases the whole result.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildRecursiveProducerOverTupleScrutineeProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{8L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("recursive producer over a tuple scrutinee", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_curried_producer_over_tuple_heads_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A curried producer conses tuple heads taken out of its parameter: each head gets an owned
        // reference-counted copy, so the cell holding the reference-counted rest of the result is
        // reference-counted too and nothing is left in an arena cell no one releases.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildCurriedProducerOverTupleHeadsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{8L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("curried producer over tuple heads", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_producer_returning_parameter_tail_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A producer's base case hands back its parameter's own tail: that tail takes a reference of
        // its own on both close-out branches, so the result is an owned list the caller releases
        // instead of copying it out and stranding the spine.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildProducerReturningParameterTailProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{9L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("producer returning its parameter's tail", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_list_parameter_tail_matched_in_tuple_beside_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop matches `(pending, lowered)` and passes the list's tail on: the tail is the list
        // parameter's consumed tail, as in a match on the list alone, so the parameter is placed on
        // the reference-counted heap and each iteration releases the list it was given.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildListParameterTailMatchedInTupleBesideRecordProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1)) + (8L * rounds)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list parameter tail matched in a tuple beside a record", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_tuple_constructor_child_handed_back_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop matches `(t, acc)` and passes the left child of the node it bound at the tree's own
        // position back: the read out of the tuple already retains the child, so the self call adds
        // no same-parameter transfer retain and each iteration's tree is released.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildTupleConstructorChildHandedBackProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1) / 2) + (6L * rounds)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple constructor child handed back to its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_tuple_cons_chain_tail_handed_back_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop matches `(items, kept)` taking two heads at once (`a :: b :: rest`) and passes the
        // tail back: the chain's tail is the list's consumed tail, so the list is placed on the
        // reference-counted heap and the copy the tuple normalized it into is released.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildTupleConsChainTailHandedBackProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(5L * rounds) + (5L * rounds * (rounds + 1))}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple cons chain tail handed back to its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_whole_tuple_element_handed_back_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop matches `(pending, kept)` and passes the record it bound whole at its own position
        // back unchanged: the read out of the tuple already retains it, so the self call adds no
        // same-parameter transfer retain and each iteration releases the record it was given.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildWholeTupleElementHandedBackProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(3L * rounds * (rounds + 1) / 2) + (16L * rounds)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("whole tuple element handed back to its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_ok_of_matched_record_fields_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns `Ok` of the fields of a record it matched, its error type a variable
        // nothing gives a type: read as `Never`, the result is copied out like any `Result` of lists
        // and the record is released behind it instead of staying behind the arena `Ok`.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildOkOfMatchedRecordFieldsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{2L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("Ok of a matched record's fields", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_fresh_start_beside_handed_on_list_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop starts its list afresh with `[Some((a, b))]`, built from a call's fresh result, at
        // one self call and hands it on at the other: as a choice accumulator its successor is
        // copied at the back edge and the element the literal built is released behind the copy.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildFreshStartBesideHandedOnListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(2L * rounds) + 1}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list started afresh beside a handed-on list", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_list_literal_successor_reading_heap_sibling_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop replaces its list with a literal whose element (a tuple, a record) holds a sibling
        // parameter on the reference-counted heap: the back edge copies the literal's cells, so the
        // element is released behind the copy and the retain its construction took of the sibling is
        // withdrawn, instead of both staying behind the arena cell.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildListLiteralSuccessorReadingHeapSiblingProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(4L * rounds) + 4}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list literal successor reading a heap sibling", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_scalar_list_handed_a_call_result_part_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A list of integers handed on from a part matched out of a call's result
        // (`| Some((a, b)) -> go(a)`) is placed on the reference-counted heap like a call's result,
        // so each back edge releases the list the iteration before was given.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildScalarListHandedACallResultPartProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(2L * rounds) + 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list of integers handed a part of a call's result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_int_list_parameter_tail_matched_in_tuple_beside_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop matches `(pending, lowered)` over a list of integers and passes its tail on: the
        // tuple is built on the reference-counted heap beside the record, so the list takes the
        // consumed-tail placement although its elements could live in the arena, and each
        // iteration releases the list it was given.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildIntListParameterTailMatchedInTupleBesideRecordProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1)) + (14L * rounds)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list of integers matched by its tail in a tuple beside a record", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_ok_tuple_built_before_error_type_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `Ok` of a fresh tuple in a function's first branch, before any branch has given the result
        // its error type: the tuple goes to an owned slot the function releases once its result has
        // a reference of its own, instead of staying behind the arena `Ok` with nothing to release it.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildOkTupleBuiltBeforeErrorTypeProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{6L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("Ok tuple built before its error type is known", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_normalized_parameter_behind_ok_record_update_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function normalizes its record parameter at entry and returns `Ok(state with ...)`: the
        // generic wrapper is normalized into an owned value at the return, and the parameter, which
        // nothing else releases, is released after that normalization.
        int[] roundCounts = [2000, 40000, 320000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildNormalizedParameterBehindOkRecordUpdateProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("normalized parameter behind an Ok record update", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_let_bound_list_kept_by_arena_record_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A let-bound owned list is stored into a record of the contract's types that stays in the
        // arena beside a record field, and the record is returned. The cell's retain goes to an
        // owned slot released once the result is normalized; the arena cell kept it before.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildLetBoundListKeptByArenaRecordResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("let-bound list kept by an arena record result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_pair_pushed_onto_loop_accumulator_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Each step pushes a pattern-bound buffer and a fresh slice at once, the linker's shape. The
        // outer cell kept in the arena made every step copy the counted spine under it and leave the
        // old copy behind, one spine per step; both cells are counted now and the list is released.
        int[] roundCounts = [200, 2000, 8000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildPairPushedOntoLoopAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{210L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("pair pushed onto a loop accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_map_passed_as_value_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `map` passed as a value reaches the generic list builder, whose arena cells store the
        // closure's owned results. The caller asks for the heads, the builder hands them over, and
        // the caller's copy of the list releases them; the cells kept them before.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildGenericMapPassedAsValueProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * rounds) + (2L * rounds)}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic map passed as a value", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_cons_chain_borrowed_bytes_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop conses three byte buffers onto its accumulator, the middle one matched out of the
        // list it walks. The middle cell takes its own reference to the head and is
        // reference-counted like the cells around it; left in the arena it made the next iteration
        // copy the whole spine and leak the previous one.
        int[] roundCounts = [200, 2000, 8000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildConsChainBorrowedBytesHeadProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 60}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("cons chain with a borrowed bytes head", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_bytes_record_accumulator_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop builds a list of records holding a byte buffer. Its cells are reference-counted, and
        // the accumulator kept in the arena (the record literal had no proven Bytes provenance) made
        // the loop retain its result at the exit and never release the list it had built.
        int[] roundCounts = [200, 2000, 8000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildBytesRecordAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{3040L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("bytes record accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_fresh_head_over_named_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop pushes a fresh buffer over one matched out of the list it walks. The inner cell
        // takes its own reference to the matched head and is reference-counted, so the fresh cell
        // over it is too; both stayed in the arena before, and each step lost the fresh buffer.
        int[] roundCounts = [200, 2000, 8000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildFreshHeadOverNamedHeadProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{1295L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("fresh head over a named head", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_variant_loop_field_consed_onto_copied_accumulator_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Each parameter type a loop peels off a variant parameter, a pattern field, is consed onto
        // the list accumulator. Once that accumulator is placed on the reference-counted heap the
        // back edge copies the arena cell, its head retained by the copy; the pattern owner's
        // duplicate the cell took was never released.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildVariantLoopFieldConsedOntoCopiedAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 11}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("variant loop field consed onto a copied accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_variant_loop_result_beside_split_list_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop over a variant parameter on the reference-counted heap peels a function type and
        // returns the rest, a pattern owner, beside the list in a reference-counted tuple. The tuple's
        // copy of the element takes its reference; the pattern owner's duplicate was never released.
        int[] roundCounts = [5000, 50000, 250000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithImports(BuildVariantLoopResultBesideSplitListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("variant loop result beside a split list", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_consumed_list_head_kept_by_tuple_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A fresh list of fresh strings goes to a function whose arena tuple result keeps a head (or
        // the rest of the list). The result is made owned before the list is released, so the list
        // goes with every head instead of spine-only, which stranded each head the result did not keep.
        int[] roundCounts = [2000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildConsumedListHeadKeptByTupleResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("consumed list head kept by a tuple result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_owned_values_behind_forwarded_contract_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns, as it is, a generic call's result built from owned values it passed
        // in. The innermost function's contract result says through the caller's mask that it holds
        // references of its own, so every forwarding function releases the owned values behind it.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildOwnedValuesBehindForwardedContractResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{((long)rounds * (rounds + 1) / 2) + rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("owned values behind a forwarded contract result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_tuple_result_holds_owned_record_field_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns a tuple holding a field of an owned record a call built. The tuple is
        // placed on the reference-counted heap with the field retained, and the record released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildTupleResultHoldsOwnedRecordFieldProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple result holding an owned record's field", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_recursive_phantom_list_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A recursive builder conses tuples built from an owned record's fields onto its own
        // result, beside a `None` of a type nothing reaches. Read with that type as `Never`, the
        // result is one of the contract's types: the builder owns it and releases the record.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecursivePhantomListResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("recursive builder with a phantom list result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_forwarded_generic_tuple_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A generic function returns another generic function's tuple result as it is. The caller's
        // request for the contract list goes on through it to the function that built the tuple,
        // which hands the list over and releases the owned record behind it.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildForwardedGenericTupleResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic tuple result forwarded through a generic function", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_tuple_result_through_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A generic function's tuple result comes back through a generic function applying its
        // closure parameter. The caller asks for the contract list without knowing who builds the
        // tuple, the request goes on through the closure call, and the builder's mask says the list
        // was handed over.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericTupleResultThroughClosureProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic tuple result through a closure", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_demoted_fields_beside_generic_value_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A generic function pairs its type parameter's value with the string-list fields of a
        // record a call returned. The arena tuples hold independent copies of the fields, so the
        // record is released behind the result.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildDemotedFieldsBesideGenericValueProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record fields demoted beside a generic value", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_tuple_result_contract_part_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A generic function returns a tuple pairing its type parameter's value with a contract
        // list, built per arm from a call, a borrowed parameter, a fresh list or an owned record's
        // field. The caller asks for the list, so the callee hands it over and releases its owned
        // values, and the caller's copy takes the handed-over reference.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericTupleResultContractPartProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic tuple result handing a contract part over", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_nested_recursive_helper_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A `let recursive` helper declared inside a function body, with annotated parameters, builds
        // its accumulated result on the reference-counted heap. The enclosing function calls it
        // through its local slot and must know that result is reference-counted, or the caller
        // treats it as an arena value and the whole result graph leaks every round.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildNestedRecursiveHelperResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(rounds * 17) / 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("nested recursive helper's reference-counted result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_consumed_list_head_kept_by_generic_call_successor_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop walking its list parameter's consumed tail hands the head to a generic call whose
        // result, the other parameter's successor, keeps the head in its arena cells. The old list
        // is released at the back edge only after that successor is normalized: released at the
        // self call, the head is freed while the successor still names it.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildConsumedListHeadKeptBySuccessorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("consumed list head kept by a generic call's successor", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_pattern_owner_handed_to_constrained_call_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop's accumulator is replaced in every arm by a constrained call's result (an
        // association update compared through `Eq`), whose key is a name a pattern binds out of the
        // loop's list parameter. The reference the key takes for the call's arena result is released
        // once the successor holds its own references; left with the arena result, every
        // iteration's key leaks.
        int[] roundCounts = [4000, 32000, 128000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildPatternOwnerHandedToConstrainedCallProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("pattern owner handed to a constrained call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_list_literal_static_head_over_built_string_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns a list literal whose middle string is built at run time and whose other
        // strings are static. An arena cell for a static string would be the only holder of the
        // reference-counted cell after it and be reclaimed without releasing it; the static head is
        // copied onto the reference-counted heap instead, so the consumer releases the whole list.
        int[] roundCounts = [4000, 64000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildListLiteralStaticHeadOverBuiltStringProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list literal with a static head over a built string", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_result_cell_holding_pattern_binding_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop over a table returns Some of the entry it finds, a list a pattern binds out of the
        // loop's parameter, and a helper rebuilds the Some around the entry it looked up. Each cell
        // holds a reference taken for it, which only a cell on the reference-counted heap releases.
        int[] roundCounts = [4000, 64000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildLoopResultCellHoldingPatternBindingProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 40}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop result cell holding a pattern binding", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_owner_released_on_earlier_owner_split_edge_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop binds a record's name and its list of uses, matches the list, and under a condition
        // skips the name. The release of the matched list's head binding splits the edges out of
        // the accumulator's release loop first; the name's release must reach those split edges
        // too, or every skipped name leaks.
        int[] roundCounts = [4000, 64000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildOwnerReleasedOnSplitEdgeProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = (5L * rounds) + ((long)rounds * (rounds + 1) / 2);
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("owner released on an earlier owner's split edge", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_copied_successor_head_tuple_unannotated_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop's accumulator, its type left to inference, grows by a cell whose head is a tuple
        // holding a label a pattern binds out of the loop's list parameter. The parameter is placed
        // on the reference-counted heap only once the self call's argument types resolve; the
        // reference the tuple took for the label is released after the back edge copies the
        // successor, or every iteration's label leaks.
        int[] roundCounts = [4000, 64000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildCopiedSuccessorHeadTupleUnannotatedProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("copied successor head tuple with an inferred accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_call_successor_sharing_owned_value_should_run()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop's accumulator, handed on in one arm, is replaced in another by a generic call's
        // result built over an owned value of the iteration. Left in the arena, that result shares
        // the owned value's cells, so the back edge must not release the value under it.
        const int rounds = 2000;
        IrProgram ir = LowerProgramWithStandardTraits(BuildGenericCallSuccessorSharingOwnedValueProgram(rounds));
        MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
        sample.Stdout.ShouldBe($"{rounds * 6}\n");
    }

    [Test]
    public async Task Linux_backend_generic_call_successor_sharing_owned_value_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The same loop's accumulator is a list of pairs, replaced in one arm by a generic call's
        // result built over an owned value of the iteration. Placed on the reference-counted heap,
        // the back edge copies that result and the owned value is released under the copy; left in
        // the arena, the owned value has no owner and leaks every round.
        int[] roundCounts = [4000, 32000, 128000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildGenericCallSuccessorSharingOwnedValueProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic call's successor over an owned value", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_nested_constrained_helper_list_argument_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A nested constrained helper (it compares through `Eq`) is handed a list a pattern binds
        // out of the loop's list parameter. Its result keeps the list's strings, never the list
        // itself, yet the reference the list takes for that result is released only once the
        // loop's successor holds references of its own; left with the arena result, it leaks.
        int[] roundCounts = [4000, 32000, 128000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildNestedConstrainedHelperListArgumentProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("nested constrained helper's list argument", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_phantom_tuple_result_fresh_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop exits with a tuple of a fresh record, an integer and `None`. The tuple is built in
        // the arena and copied onto the heap at the return, and the fresh record moves into the copy.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildPhantomTupleResultFreshRecordProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("phantom tuple result holding a fresh record", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_tuple_state_successor_borrows_pattern_tail_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop whose `(List(Str), Int)` state is placed on the reference-counted heap passes on
        // the state's tail in a fresh tuple. The back edge copies that tuple with references of its
        // own, so a pattern-owner duplicate taken for the tail was never released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildTupleStateSuccessorBorrowsPatternTailProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple state successor borrowing a pattern tail", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_choice_accumulator_cell_borrows_pattern_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A list accumulator handed on in one arm and grown by a cons of a pattern-bound head in
        // the other. The back edge copies the cell with references of its own, so a pattern-owner
        // duplicate taken for the cell's head was never released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildChoiceAccumulatorCellBorrowsPatternHeadProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("choice accumulator cell borrowing a pattern head", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_worklist_back_only_refilled_from_helper_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist whose back queue is only ever handed on, emptied, or refilled from a helper
        // that conses each step's target onto it. Left in the arena, the queue kept every refill's
        // reference-counted cells; placed on the reference-counted heap, each refill is taken at
        // the back edge and the one it replaces released.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildWorklistBackOnlyRefilledFromHelperProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist back queue only refilled from a helper", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_worklist_front_refilled_behind_marking_helper_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist whose back queue takes each key marked by a helper. The helper's ownership
        // summary says its result holds nothing of the key, so the back queue receives nothing of
        // the front, which is placed on the reference-counted heap and releases each refill.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildWorklistFrontRefilledBehindMarkingHelperProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 10}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist front refilled behind a marking helper", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_fresh_bytes_buffer_read_by_a_builtin_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A fresh buffer handed straight to a builtin that only reads it: a length, and the source
        // of a copyRange. Nothing owns the buffer once the read completes, so it is released there,
        // as a fresh string handed to print or byteLength is.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildFreshBytesBufferReadByBuiltinProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 768}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("fresh bytes buffer read by a builtin", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_bytes_buffer_handed_to_its_loop_through_set_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop that hands a buffer to itself through Byte.set. Left in the arena, each
        // iteration copied the buffer out and nothing released the copy it replaced; placed on
        // the reference-counted heap, the back edge releases the predecessor.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildBytesBufferHandedToItsLoopProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 63}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("bytes buffer handed to its loop through set", samples, maxRssKb: 16_000);
    }

    [Test]
    public void Worklist_front_beside_queued_map_of_its_keys_is_placed_on_the_reference_counted_heap()
    {
        // A worklist whose loop marks each key in a map beside the front queue. The map's update
        // is routed to the collection's in-place specialization, which copies the key it stores,
        // so the map receives nothing of the front, which is placed on the reference-counted heap
        // and releases each refill. (The map's own region grows by a map per round by design, so
        // this is checked on the placement, not by peak memory.)
        (Lowering lowering, _) = LowerProgramWithImportsAndLowering(BuildWorklistFrontBesideQueuedMapProgram(3));
        IReadOnlyList<TcoParamPlacementTrace> decisions = lowering.GetTcoPlacementDecisions("solve")
            ?? throw new InvalidOperationException("No TCO placement snapshot for 'solve'.");
        TcoParamPlacementTrace front = decisions.Single(trace => string.Equals(trace.Current.ParameterName, "front", StringComparison.Ordinal));
        front.Current.Representation.ShouldBe(TcoPlacementRepresentation.RuntimeRc);
    }

    [Test]
    public async Task Linux_backend_worklist_pair_released_past_unrelated_log_successor_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist threading a helper's pair as its back queue and tree, beside a log of fresh
        // strings the loop conses onto. The log's type could hold a part of an earlier pair, but its
        // successor names neither the pair's bindings nor a parameter that received them, so each
        // earlier pair is released at the back edge instead of being kept for the log's sake.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildWorklistPairPastUnrelatedLogSuccessorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 20}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist pair released past an unrelated log successor", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_variant_accumulator_grown_through_helper_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop growing a variant accumulator through a helper that conses onto the list it holds.
        // The accumulator is handed over for the helper's result to keep, and that result stays in
        // the arena as the loop's successor, so the handed-over reference goes to an owned slot the
        // back edge releases once the successor holds references of its own.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildVariantAccumulatorGrownThroughHelperProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 8}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("variant accumulator grown through a helper", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_inline_list_beside_recursive_list_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop over a list of a recursive type, which cannot be placed on the reference-counted
        // heap, threading a record state a helper returns owned and an Int list it conses onto. The
        // Int list is placed beside the blocking sibling, so the back edge can release each
        // iteration's owned states instead of keeping them for a successor that might hold them.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildInlineListBesideRecursiveListParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 25}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("inline list beside a recursive list parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Map_threaded_through_walked_list_loop_releases_earlier_maps_at_the_back_edge()
    {
        // A loop threading a map through a helper's owned result while it walks a list of strings.
        // The walked list's tail holds only parts of the list the loop was entered with, so each
        // earlier iteration's map is released at the back edge rather than overwritten in its slot:
        // one release there, beside the two at the loop's exit. (The map's own reuse specialization
        // grows the arena per round, so this is checked on the lowered loop, not by peak memory.)
        IrProgram ir = LowerProgramWithImports(BuildMapThreadedThroughWalkedListLoopProgram(3));
        IrFunction loop = ir.Functions
            .Where(function => function.Origin is { Kind: IrFunctionOriginKind.ClosureHelper, Source.SourceName: "dependentsOf" })
            .Single(function => function.Instructions.Any(instruction =>
                instruction is IrInst.Jump { Target: var target }
                    && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        loop.Instructions.Count(instruction =>
            instruction is IrInst.CallKnown { FuncLabel: var label }
                && label.StartsWith("__rcdrop_", StringComparison.Ordinal)).ShouldBe(3);

        if (OperatingSystem.IsLinux())
        {
            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(ir).ConfigureAwait(false);
            result.Stdout.ShouldBe("15\n");
        }
    }

    [Test]
    public async Task Map_upsert_releases_the_closure_result_it_copies_into_the_persistent_region()
    {
        // Inside the map's reuse specialization, the onHit closure's result is normalized to an
        // owned reference-counted value and then copied into the persistent region, where the
        // rebuilt node keeps the copy. The owned original is released once copied, directly or,
        // when makeNode is inlined, once the helper body is lowered; nothing released it before.
        // (The specialization copies the missing value into the persistent region at every call,
        // so this is checked on the lowered program, not by peak memory.)
        IrProgram ir = LowerProgramWithImports(BuildMapUpsertClosureResultLoopProgram(40));
        bool copiedResultReleased = ir.Functions.Any(function =>
        {
            List<IrInst> instructions = function.Instructions;
            return instructions.Select((instruction, index) => (instruction, index)).Any(entry =>
                entry.instruction is IrInst.CopyStringIntoOrFresh
                && instructions.Skip(entry.index + 1)
                    .TakeWhile(later => later is not (IrInst.Jump or IrInst.Label or IrInst.Return))
                    .Any(later => later is IrInst.RcDrop { OwnerSlot: -1, TypeName: "String", RuntimeManaged: true }));
        });
        copiedResultReleased.ShouldBeTrue();

        if (OperatingSystem.IsLinux())
        {
            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(ir).ConfigureAwait(false);
            result.Stdout.ShouldBe("44\n");
        }
    }

    [Test]
    public void Map_reuse_specialization_call_borrows_its_key_argument()
    {
        // A reuse specialization copies every argument but the accumulator into the persistent
        // region, so its calls borrow them: the loop keeps its own reference to the key it matched
        // out of the walked list, and no retain is taken for the call to consume.
        IrProgram ir = LowerProgramWithImports(BuildMapInsertIfAbsentWalkProgram(3));
        foreach (IrFunction function in ir.Functions.Prepend(ir.EntryFunction))
        {
            List<IrInst> instructions = function.Instructions;
            HashSet<int> specializationClosures = [.. instructions
                .OfType<IrInst.MakeClosure>()
                .Where(closure => closure.FuncLabel.EndsWith("__reuse", StringComparison.Ordinal))
                .Select(closure => closure.Target)];
            for (int index = 1; index < instructions.Count; index++)
            {
                if (instructions[index] is IrInst.CallClosure call
                    && specializationClosures.Contains(call.ClosureTemp))
                {
                    (instructions[index - 1] is IrInst.RcDup { SourceTemp: var retained } && retained == call.ArgTemp)
                        .ShouldBeFalse($"{function.Label} retains the key it hands the specialization");
                }
            }
        }
    }

    [Test]
    public async Task Linux_backend_arena_record_of_destructured_tuple_parts_in_result_tuple_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // An arena record built from the parts of a destructured reference-counted tuple and
        // returned inside a reference-counted tuple. The result tuple stores a copy of the record
        // that holds its own reference to every part it shares, so the destructured tuple needs no
        // extra reference: one taken for it has no release, and leaked the tuple every round.
        int[] roundCounts = [4000, 16000, 64000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildArenaRecordOfDestructuredTuplePartsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("arena record of destructured tuple parts in a result tuple", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_generic_tuple_beside_reference_counted_element_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A generic helper pairing its generic parameter with a reference-counted value, and
        // returning that pair's list beside a reference-counted state. Neither tuple can go onto
        // the reference-counted heap beside a generic value, so each holds an arena copy of its
        // reference-counted element; a reference it took instead was released by nothing.
        int[] roundCounts = [4000, 16000, 64000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildGenericTupleBesideReferenceCountedElementProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 17 / 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("generic tuple beside a reference-counted element", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_self_call_beside_error_arm_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop's tail self call sits in the branch lowered first, and the other branch forwards a
        // helper's error. The self call is typed with the loop's own result, so the loop's error
        // type is the helper's and each error's string is released; typed with a fresh variable, the
        // error type stayed unknown and every forwarded error leaked its string.
        int[] roundCounts = [4000, 128000, 1024000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildSelfCallBesideErrorArmProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldNotBeNullOrWhiteSpace();
            samples.Add(sample);
        }

        AssertMemoryPlateaus("self call beside a forwarded error", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_phantom_result_variable_tuple_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A function returns a record beside a `None` whose type variable none of its parameters
        // name. Read as `Never`, the tuple is one of the contract's types: the callee returns it
        // owned with the record moved in, and the caller releases it; read as unresolved, the
        // tuple stayed in the arena and the record's owned slot leaked on every call.
        int[] roundCounts = [4000, 32000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildPhantomResultVariableTupleProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{(long)rounds * rounds + 2L * rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple result with a phantom type variable", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_arena_list_cell_with_parameter_field_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop conses a record holding a field read of its reference-counted state onto a list
        // another arm shortens, so the list stays in the arena, which releases nothing it holds.
        // The record holds an arena copy of the field; a retain taken for it leaked the field's
        // list once per cell.
        int[] roundCounts = [4000, 32000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildArenaListCellWithParameterFieldProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("arena list cell holding a parameter field", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_record_cell_with_parameter_field_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop conses a record holding a field read of its reference-counted state onto a
        // reference-counted list. The record's constructor makes the field its own; a second
        // retain taken for the loop parameter's field read had no release, one list per round.
        int[] roundCounts = [4000, 32000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopRecordCellWithParameterFieldProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop record cell holding a parameter field", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_parameter_handed_to_helper_returning_optional_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop hands its reference-counted list parameter to a helper returning `Maybe(Int)`. The
        // retain taken for the call is released when the callee did not adopt it and its result
        // cannot hold the list; reading the generic `Maybe`'s field as a value of any type kept the
        // retain, one list cell per iteration.
        int[] roundCounts = [4000, 32000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopParameterHandedToOptionalHelperProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop parameter handed to a helper returning an optional", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_arena_loop_parameter_retained_successor_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop threading a generic list keeps its state parameter in the arena and never resets
        // it; the successor state is read out of a call's reference-counted pair and retained for
        // the transfer. The parameter releases nothing, so it takes an arena copy and the retain is
        // released: kept as it was, one state leaked per iteration.
        int[] roundCounts = [4000, 32000, 256000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildArenaLoopParameterRetainedSuccessorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 17 / 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("arena loop parameter holding a retained successor", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_fresh_operand_of_derived_equality_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A call's owned reference-counted result compared with a derived `!=`: the `Eq` method
        // borrows its operands and returns a Bool, so the caller releases the operand after the
        // call. Without that release one enum cell is left behind per comparison.
        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildFreshOperandOfDerivedEqualityProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("derived equality on a fresh call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_handler_loop_literal_string_match_arm_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop that handles a capability matches the handled result into a literal string arm
        // beside a fresh one. The function may run under a live handler post, so its string joins
        // stay in the arena and nothing releases the join's owner: a literal copied onto the
        // reference-counted heap there leaked one string per round.
        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildHandlerLoopLiteralStringArmProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 8}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("literal string match arm in a handler loop", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Program_entry_releases_the_fresh_argument_of_its_trailing_pipe()
    {
        // `0 |> pass(build(8)([]))`: the entry keeps the list `build` returns in an owned slot, as
        // a function does, and releases it at its end; the same pipe inside a function was already
        // released at that function's return.
        IrProgram ir = LowerProgram(BuildTrailingPipeFreshArgumentProgram(8));
        ir.EntryFunction.Instructions
            .OfType<IrInst.Label>()
            .Count(label => label.Name.StartsWith("general_rc_owned_absent_", StringComparison.Ordinal))
            .ShouldBe(1);

        if (OperatingSystem.IsLinux())
        {
            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(ir).ConfigureAwait(false);
            result.Stdout.ShouldBe("44\n");
        }
    }

    [Test]
    public async Task Linux_backend_accumulator_consing_a_part_of_a_call_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `name :: seen` in one arm, `seen` handed on in the other: the name is matched out of a
        // call's owned result. The accumulator is placed on the reference-counted heap, so its cons
        // retains the name and the back edge releases the call's result; kept in the arena it named
        // the result without a reference, and every result it borrowed from was left behind.
        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildSeenNameAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("accumulator consing a part of a call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Loop_exit_arm_returning_head_of_unresolved_type_retains_it_before_its_owner_release()
    {
        // `| last :: [] -> last` in a generic walk whose element type the `[] -> ""` arm fixes only
        // later: the head's owner takes a protective reference once the type resolves to a string,
        // and its release at the arm's exit cancels that reference. The arm retains the head too, so
        // the value the function returns survives the exit's release of the list parameter.
        IrProgram ir = LowerProgram(BuildLastOfUnresolvedHeadProgram(3));
        IrFunction lastOf = ir.Functions.Single(function =>
            function.Origin is { Source.SourceName: "lastOf" }
            && function.Instructions.Any(instruction => instruction is IrInst.Jump { Target: var target }
                && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        List<IrInst> instructions = lastOf.Instructions;
        int maximumRetains = 0;
        for (int index = 0; index < instructions.Count; index++)
        {
            if (instructions[index] is not IrInst.RcDrop { OwnerSlot: >= 0, RuntimeManaged: true, TypeName: "String" } drop)
            {
                continue;
            }

            int blockStart = index;
            while (blockStart > 0 && instructions[blockStart - 1] is not IrInst.Label)
            {
                blockStart--;
            }

            HashSet<int> ownerReads = [];
            int retains = 0;
            for (int position = blockStart; position < index; position++)
            {
                switch (instructions[position])
                {
                    case IrInst.LoadLocal load when load.Slot == drop.OwnerSlot:
                        ownerReads.Add(load.Target);
                        break;
                    case IrInst.Borrow borrow when ownerReads.Contains(borrow.SourceTemp):
                        ownerReads.Add(borrow.Target);
                        break;
                    case IrInst.RcDup { RuntimeManaged: true } dup when ownerReads.Contains(dup.SourceTemp):
                        retains++;
                        break;
                }
            }

            maximumRetains = Math.Max(maximumRetains, retains);
        }

        maximumRetains.ShouldBe(2);

        if (OperatingSystem.IsLinux())
        {
            ExecutionResult result = await CompileRunWithLinuxLlvmAsync(ir).ConfigureAwait(false);
            result.Stdout.ShouldBe("b3,b2,b1,\n");
        }
    }

    [Test]
    public void Declared_capability_keeps_the_ownership_contract_until_a_handler_can_leave_a_post_pending()
    {
        // The contract is one calling convention for the whole program. A capability that is only
        // provided leaves no handler post pending, so the program keeps it: its record results are
        // normalized and released through the synthesized dropper. A `handle` anywhere switches it
        // off for the whole program, since the functions it reaches keep their values in the arena.
        static int GeneralDropperCalls(IrProgram ir) => ir.Functions
            .Prepend(ir.EntryFunction)
            .SelectMany(function => function.Instructions)
            .Count(instruction => instruction is IrInst.CallKnown { FuncLabel: var label }
                && label.StartsWith("__rcdrop_general_", StringComparison.Ordinal));

        GeneralDropperCalls(LowerProgram(BuildProvidedCapabilityShapeLoopProgram(handled: false))).ShouldBeGreaterThan(0);
        GeneralDropperCalls(LowerProgram(BuildProvidedCapabilityShapeLoopProgram(handled: true))).ShouldBe(0);
    }

    [Test]
    public async Task Linux_backend_tuple_scrutinee_loop_owned_results_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop walking two lists through `match (a, b) with`, threading a state each iteration's
        // call returns owned. The tails it hands on are read out of the tuple the scrutinee builds,
        // and have to be traced through it to the loop's own parameters: values the iteration
        // received cannot hold the result it produced, which is what lets the back edge release it.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildTupleScrutineeLoopProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 16}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("tuple-scrutinee loop with owned results", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_result_tuple_holding_its_state_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop whose base case returns a tuple holding its own reference-counted state parameter.
        // The arena tuple retains the state to carry it past the loop's release of the parameter,
        // and the function's result normalization then takes a reference of its own: the tuple's
        // reference needs an owner that releases it, since the arena cell never does.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopResultTupleHoldingStateProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 41}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop result tuple holding its state parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_result_tuple_holding_its_unannotated_state_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The same loop with unannotated parameters: the state's type is still unresolved when the
        // base case's tuple is lowered, so the tuple stays in the arena and its retain of the
        // parameter is a marker the loop's placement later makes real. That retain needs an owner
        // that releases it once the loop's result holds its own reference.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            string source = BuildLoopResultTupleHoldingStateProgram(rounds).Replace(
                "let recursive bindAll (pairs: List((Int, Int))) (state: State) =",
                "let recursive bindAll pairs state =",
                StringComparison.Ordinal);
            IrProgram ir = LowerProgram(source);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 41}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop result tuple holding its unannotated state parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_spine_cell_head_bound_by_let_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A record a helper returns is let-bound and consed onto the spine a producer builds in
        // place: the cell retains the binding's value, so the binding's own reference is released
        // with the iteration rather than counted as moved into the cell.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordSpineProgram(rounds, "let entry = make(block)\n                    in entry :: step(rest)"));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("spine cell head bound by let", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_self_call_tail_under_owned_record_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A non-tail self call's result, let-bound and consed under a record a helper returns owned:
        // the call asks for an arena result, so the reference-counted cell copies an arena tail onto
        // the reference-counted heap instead of pointing into memory its scope reclaims.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordSpineProgram(rounds, "let tail = step(rest)\n                    in make(block) :: tail"));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("self call tail under owned record head", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_list_handed_on_or_replaced_by_helper_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A loop's list of integers handed on in one arm and replaced by a tail-modulo-cons helper's
        // reference-counted result in the other lives on the reference-counted heap, so each
        // iteration's predecessor is released at the back edge instead of left behind.
        int[] roundCounts = [5000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildHandedOnOrReplacedListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("list handed on or replaced by helper", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_fixpoint_comparing_rebuilt_list_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A fixpoint loop compares the list it rebuilt with its parameter: the comparison borrows
        // both operands, so the rebuilt list, live after it, takes no duplicate nothing releases.
        int[] roundCounts = [5000, 20000, 80000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgramWithStandardTraits(BuildFixpointCompareProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("fixpoint comparing rebuilt list", samples, maxRssKb: 16_000);
    }

    private static string BuildFixpointCompareProgram(int rounds)
        => $$"""
            let recursive lowerEach (items: List(Int)) =
                match items with
                    | [] -> []
                    | head :: rest ->
                        if head > 1
                        then (head - 1) :: lowerEach(rest)
                        else head :: lowerEach(rest)

            let recursive settle (values: List(Int)) =
                (let next = lowerEach(values)
                in
                    if next == values
                    then next
                    else settle(next))

            let recursive sum (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> sum(rest)(total + head)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + sum(settle([2, 3, 4]))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildHandedOnOrReplacedListProgram(int rounds)
        => $$"""
            let recursive replaceAt (items: List(Int)) (index: Int) (replacement: Int) =
                match items with
                    | [] -> []
                    | head :: rest ->
                        if index == 0
                        then replacement :: rest
                        else head :: replaceAt(rest)(index - 1)(replacement)

            let recursive round (order: List(Int)) (idoms: List(Int)) =
                match order with
                    | [] -> idoms
                    | block :: rest ->
                        if block == 0
                        then round(rest)(idoms)
                        else round(rest)(replaceAt(idoms)(block)(block - 1))

            let recursive sum (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> sum(rest)(total + head)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + sum(round([0, 1, 2, 3, 4])([-1, -1, -1, -1, n]))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    [Test]
    public async Task Linux_backend_closure_consing_its_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A closure applied in a pipe conses its parameter onto the producer's recursive tail: the
        // parameter reaches the result only as a cell's head, so its entry takes no owned copy an
        // arena cell would keep nothing of.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordSpineProgram(rounds, "make(block) |> (given (entry) -> entry :: step(rest))"));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("closure consing its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_accumulator_with_differing_self_call_shapes_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // An accumulator of the ownership contract's values that one arm extends with a call's owned
        // result and another hands on unchanged. No single self-call shape holds, and a parameter
        // left in the arena for that never releases its predecessor, so the owned result each
        // iteration handed it was orphaned. Classified as an accumulator it is placed on the
        // reference-counted heap and the iteration's owned value is released at the back edge.
        int[] roundCounts = [4000, 16000, 64000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildDifferingShapeAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 20}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("accumulator with differing self-call shapes", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_state_threaded_beside_a_variant_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A state threaded through a loop beside a variant of the ownership contract's types whose
        // successor is read out of a call's result (a function type peeled one arrow per argument).
        // Only records were admitted as reference-counted loop parameters, so the variant could
        // never be placed, and a parameter that can never be placed blocks every sibling that may
        // hold it: the state stayed in the arena and each iteration's owned results were kept.
        int[] roundCounts = [2000, 8000, 32000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildStateBesideVariantParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 50}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("state threaded beside a variant parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_returning_pattern_binding_of_its_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A tail-recursive walk whose exit arm returns a pattern binding of its loop parameter whole
        // (`other -> other`). The arm retains the binding before its scope releases it, so the join
        // holds a reference of its own; the function's result retained it again as if the join
        // borrowed from the parameter, one reference per call left behind.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopReturningPatternBindingOfParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop returning a pattern binding of its parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_step_pairing_its_state_parameter_with_a_rewrite_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A helper that returns its state parameter (or an update of it) in a pair beside a
        // rewritten record normalizes the parameter at entry, so it owns it. The pair retains or
        // copies the state it stores, so the parameter's own reference is released after the
        // result; it was kept, one state per call left behind.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildStepPairingStateWithRewriteProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 100}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("step pairing its state parameter with a rewrite", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_record_update_of_loop_state_handed_to_helper_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // An arena record update of a loop parameter retains the fields it keeps. Handed to a
        // helper rather than to the back edge, it is a call argument whose retains are released
        // once the helper's result holds its own references; they were never released.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildRecordUpdateHandedToHelperProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 20}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record update of loop state handed to a helper", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_matching_parts_of_a_helper_pair_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // A worklist loop threads the parts of a helper's owned pair result as its successors. The
        // pair an earlier iteration left in its owned slot was kept, because the successors were
        // pattern bindings rather than plain reads of the owned slot; they are parts of the newer
        // pair, which holds its own references, so the earlier pair is released.
        int[] roundCounts = [2000, 20000, 200000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram ir = LowerProgram(BuildLoopMatchingPartsOfHelperPairProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 12}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop matching the parts of a helper pair", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_recursive_map_bound_tail_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The old lowering grows from 8 MiB to 20 MiB at these scales. Use the low-floor
        // harness so the measurement wrapper's own resident set cannot hide that regression.
        List<MemoryExecutionResult> samples = await MeasureLowFloorMemoryGrowthAsync(iterations => $$"""
            let decorate (value: Str) = value + "!"
            let recursive mapText (values: List(Str)) =
                match values with
                    | [] -> []
                    | head :: tail ->
                        let decorated = decorate(head) in
                        let mappedTail = mapText(tail) in
                        let alias = mappedTail in decorated :: alias
            let recursive sumLengths (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | head :: tail -> sumLengths(tail)(total + Ashes.Text.byteLength(head))
            let recursive loop count total =
                if count == 0 then total
                else let values = mapText(["left", "mid", "right"]) in
                     loop(count - 1)(total + sumLengths(values)(0))
            Ashes.IO.print(loop({{iterations}})(0))
            """, outputPerIteration: 15).ConfigureAwait(false);
        AssertMemoryPlateaus("recursive map with a bound tail", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_hot_loop_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> list = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcListMemoryProgram,
            outputPerIteration: 41).ConfigureAwait(false);
        List<MemoryExecutionResult> escapingList = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingListMemoryProgram,
            outputPerIteration: 41).ConfigureAwait(false);
        List<MemoryExecutionResult> ownedElementList = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcOwnedElementListMemoryProgram,
            outputPerIteration: 3).ConfigureAwait(false);
        List<MemoryExecutionResult> tuple = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTupleMemoryProgram,
            outputPerIteration: 129).ConfigureAwait(false);
        List<MemoryExecutionResult> adt = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcAdtMemoryProgram,
            outputPerIteration: 196).ConfigureAwait(false);
        List<MemoryExecutionResult> recordOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcRecordOwnedChildMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);
        List<MemoryExecutionResult> variantOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcVariantOwnedChildMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);
        List<MemoryExecutionResult> genericOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcGenericOwnedChildMemoryProgram,
            outputPerIteration: 84).ConfigureAwait(false);
        List<MemoryExecutionResult> higherOrderResult = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderResultMemoryProgram,
            outputPerIteration: 8).ConfigureAwait(false);
        List<MemoryExecutionResult> reuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcAdtReuseMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> nestedRecordReuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcNestedRecordReuseMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);
        List<MemoryExecutionResult> pointerVariantReuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcPointerVariantReuseMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);
        List<MemoryExecutionResult> sharedRecordChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcSharedRecordChildMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC list", list);
        AssertMemoryPlateaus("runtime-RC escaping list", escapingList);
        AssertMemoryPlateaus("runtime-RC owned-element list", ownedElementList);
        AssertMemoryPlateaus("runtime-RC tuple", tuple);
        AssertMemoryPlateaus("runtime-RC ADT", adt);
        AssertMemoryPlateaus("runtime-RC record owned child", recordOwnedChild);
        AssertMemoryPlateaus("runtime-RC variant owned child", variantOwnedChild);
        AssertMemoryPlateaus("runtime-RC generic owned child", genericOwnedChild);
        AssertMemoryPlateaus("runtime-RC higher-order result", higherOrderResult);
        AssertMemoryPlateaus("runtime-RC ADT reuse", reuse);
        AssertMemoryPlateaus("runtime-RC nested-record reuse", nestedRecordReuse);
        AssertMemoryPlateaus("runtime-RC pointer-variant reuse", pointerVariantReuse);
        AssertMemoryPlateaus("runtime-RC shared record child", sharedRecordChild);
    }

    /// <summary>
    /// A runtime-managed <c>Str</c> loop parameter its successor reads more than once: joined with
    /// a literal behind a guard on its length, doubled and sliced by the byte builtin, and doubled
    /// and handed to a helper whose literal arms join the slice. Each leaked one string per
    /// iteration before the join normalization, the flow-bound concatenation promotion, and the
    /// fresh-builtin result reach were in place.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_runtime_rc_string_parameter_read_twice_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> joinSuccessor = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcStringJoinSuccessorMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> readTwice = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcStringReadTwiceMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> freshArgumentHelper = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcStringFreshArgumentHelperMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC string join successor", joinSuccessor);
        AssertMemoryPlateaus("runtime-RC string read twice", readTwice);
        AssertMemoryPlateaus("runtime-RC string fresh argument helper", freshArgumentHelper);
    }

    /// <summary>
    /// A TCO loop's own exit arm building a multi-constructor ADT (<c>| Pr(a, b)</c>, not a
    /// zero-cost single-payload wrapper) directly from the loop's own runtime-managed accumulator
    /// parameters. Unlike every ADT shape in
    /// <see cref="Linux_backend_llvm_runtime_rc_hot_loop_memory_should_plateau_as_work_scales"/>
    /// (which build fresh literals from scratch values each iteration), this one threads the loop's
    /// own List(Int) parameters straight into the constructor at the loop's terminal case — the shape
    /// that exposes the leak.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_tco_exit_adt_constructor_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildTcoExitAdtConstructorMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("TCO-exit ADT constructor over runtime-RC accumulators", samples);
    }

    /// <summary>
    /// A search loop over a runtime-managed list of strings that returns the matched head beside a
    /// literal arm: the branch retains the head for the result, so the literal arm must be normalized
    /// for the join to be uniformly runtime-managed and the caller to release the result and the list
    /// it passed. Before the normalization saw through the `if` and the retained owner, the retained
    /// head and the list's heads leaked on every search.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_find_loop_returning_string_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildFindLoopStringHeadMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("find loop returning a string head", samples);
    }

    /// <summary>
    /// The record-head sibling of the search above: the empty arm builds a static record (every
    /// field a literal), which cannot be allocated runtime-managed by placement alone, so it is built
    /// in the arena and deep-copied to the reference-counted heap beside the retained head.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_find_loop_returning_record_head_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildFindLoopRecordHeadMemoryProgram,
            outputPerIteration: 8).ConfigureAwait(false);

        AssertMemoryPlateaus("find loop returning a record head", samples);
    }

    /// <summary>
    /// A generic `map` applying a function that returns a fresh runtime-managed record (its
    /// always-returned string parameter owned at entry): the generic body cannot own the result,
    /// so it asks for an arena result and the callee clones the record into the arena and
    /// releases its own; the caller then deep-copies the list out of the call window. Before the
    /// arena-result boundary every record and string leaked once per element.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_generic_map_returning_records_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildGenericMapRecordResultMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("generic map returning records", samples);
    }

    /// <summary>
    /// The string sibling: the appended result takes the conditional list copy-out, whose arena
    /// branch copies the string heads, so the consumed first argument is released with its strings
    /// on that branch and spine-only on the owned one. Releasing spine-only on both leaked one string
    /// per element.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_generic_map_returning_strings_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildGenericMapStringResultMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("generic map returning strings", samples);
    }

    /// <summary>
    /// A tail self-call whose argument is a let-bound name passes a value built earlier in the same
    /// iteration. Reading the loop's structural facts off that bare variable left the parameter
    /// placed on the arena, which declined the back-edge reset and with it the release of the
    /// parameter's previous value, leaking the whole previous list every iteration.
    /// </summary>
    [Test]
    public async Task Linux_backend_llvm_let_bound_tail_call_argument_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureImportedMemoryGrowthAsync(
            BuildLetBoundTailCallArgumentMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("let-bound tail call argument", samples);
    }

    /// <summary>
    /// A call routed to an element specialization returns a result the compiler verified
    /// reference-counted, and such a result owns every part it kept. The reference the caller handed
    /// over for that result to keep must therefore be released once the callee declines to adopt it.
    /// Treating a statically reference-counted result as though it might still hold the argument
    /// stranded that retain, leaking one argument list per call: fannkuch N=9 went from 8,204 KiB to
    /// 253,968 KiB and N=11 stopped fitting in 1 GiB.
    /// </summary>
    [Test]
    public void Linux_backend_element_specialized_producer_releases_handed_over_argument()
    {
        IrProgram ir = LowerProgramWithImports(BuildElementSpecializedProducerProgram());

        ir.Functions.Any(function => function.Label.EndsWith("__element", StringComparison.Ordinal))
            .ShouldBeTrue("The generic producer should specialize at the call fixing its element type.");
        AllInstructions(ir).Any(instruction =>
            instruction is IrInst.Label label
            && label.Name.StartsWith("rc_handed_over", StringComparison.Ordinal))
            .ShouldBeTrue("The caller must settle the reference handed to the specialized producer.");
    }

    [Test]
    public async Task Linux_backend_cons_chain_accumulator_memory_should_plateau()
    {
        // `interleave(part :: ":" :: acc)(rest)`: a tail call consing a chain of two cells onto its
        // list parameter grows it as a single cons does, so both cells are reference-counted and the
        // head matched out of the walked list is released with them; kept in the arena, the head's
        // retain was stranded in the cell, one string per iteration.
        IrProgram ir = LowerProgramWithImports(BuildConsChainAccumulatorProgram(3));
        IrFunction loop = ir.Functions.Single(function =>
            function.Origin is { Source.SourceName: "interleave" }
            && function.Instructions.Any(instruction => instruction is IrInst.Jump { Target: var target }
                && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        loop.Instructions.Count(instruction => instruction is IrInst.Alloc { RuntimeManaged: true })
            .ShouldBe(2, "Both cells of the chain are reference-counted.");

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildConsChainAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("cons chain accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_accumulator_reset_by_literal_or_choice_memory_should_plateau()
    {
        // `go([first])(rest)` beside `go(first :: acc)(rest)`, and the same choice made inside one
        // self-call's argument: the literal start leaves the accumulator a grown one, and the cells
        // a chosen argument builds for a successor the back edge copies borrow their parts. Each
        // used to keep one string per element that nothing released.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildAccumulatorResetByLiteralOrChoiceProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 6}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("accumulator reset by a literal or a choice", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_parameter_the_body_never_reads_memory_should_plateau()
    {
        // A string or list loop parameter the body never reads has no captured argument: its slot
        // holds zero at entry, and normalizing that as a reference-counted value crashed. The slot
        // starts inactive and takes the successors the back edge stores.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildLoopParameterTheBodyNeverReadsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 9}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("loop parameter the body never reads", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_cons_chain_head_reading_loop_parameter_memory_should_plateau()
    {
        // A chain consed onto a list accumulator whose heads read loop parameters, a separator passed
        // on unchanged and the previous part, each beside a part matched out of the walked list. The
        // accumulator is reference-counted, so the parts it retains are released with it.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildConsChainHeadReadingLoopParameterProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 12}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("cons chain head reading a loop parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_piped_lambda_returning_call_result_list_memory_should_plateau()
    {
        // A lambda applied in a pipe returns a recursive producer's reference-counted list, which
        // the function piping into it hands on as its result: the call of the lambda owns it, so
        // the caller releases it.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildPipedLambdaReturningCallResultListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("piped lambda returning a call result list", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_record_consed_onto_call_result_list_memory_should_plateau()
    {
        // A record consed onto a function's reference-counted list result outside any recursion: the
        // cell is reference-counted too, so the list it holds is released with it.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildRecordConsedOntoCallResultListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 3}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("record consed onto a call result list", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_closure_conses_record_onto_self_call_result_memory_should_plateau()
    {
        // A recursive producer conses each record, inside a piped lambda, onto its own curried self
        // call reached through the lambda's capture: the call's result is owned and the cell is
        // reference-counted, so every list the producer builds is released.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildClosureConsesRecordOntoSelfCallResultProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 8}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("closure consing a record onto its self call result", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_exit_cell_passed_to_captured_callee_memory_should_plateau()
    {
        // The pairwise pass of `join` as first written: the exit passes its cell to a `let
        // recursive` reverse the loop captures, whose result-reach summary does not say it keeps the
        // argument. The cell is reference-counted all the same and released after the call.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildExitCellPassedToCapturedCalleeProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("exit cell passed to a captured callee", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_exit_cell_passed_to_call_memory_should_plateau()
    {
        // `| [] -> rev([])(first :: acc)`: the cell a loop's exit passes to a call that may keep it
        // is reference-counted like the cell a loop returns, and the call's fresh argument is
        // released after it. An arena cell there held a retain of its head, upgraded only after the
        // call's frame had closed, that nothing released: one string per loop.
        IrProgram ir = LowerProgramWithImports(BuildExitCellPassedToCallProgram(3));
        IrFunction loop = ir.Functions.Single(function =>
            function.Origin is { Source.SourceName: "keepOdd" }
            && function.Instructions.Any(instruction => instruction is IrInst.Jump { Target: var target }
                && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        loop.Instructions.Count(instruction => instruction is IrInst.Alloc { SizeBytes: 16, RuntimeManaged: false })
            .ShouldBe(0, "The exit's cell is reference-counted.");

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildExitCellPassedToCallProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 2}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("exit cell passed to a call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_exit_cell_of_records_passed_to_call_memory_should_plateau()
    {
        // `| [] -> reverseValues(eof :: tokens)([])` over a list of records: the cell a loop's exit
        // passes to a call is reference-counted and takes a reference to the accumulator, and a list
        // of the contract's types is passed borrowed, so no consumed-argument release follows the
        // call. The cell is kept in an owned slot of its own; without it the cell, and with it the
        // whole accumulator, was never released.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 100_000, 300_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildExitCellOfRecordsPassedToCallProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 9}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("exit cell of records passed to a call", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_call_result_onto_choice_accumulator_memory_should_plateau()
    {
        // `walk(rest)(join(p) :: seen)` beside `walk(rest)(seen)`: the back edge copies the successor
        // with references of its own and the cell only borrows the call's owned result, which keeps
        // an owner of its own, released once the successor holds its references. Without one, one
        // string per cons was never released.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildCallResultOntoChoiceAccumulatorProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 7}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("call results consed onto a choice accumulator", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_worklist_of_paths_memory_should_plateau()
    {
        // A worklist of paths walked by its tail and refilled from `append`'s result lives on the
        // reference-counted heap. Left in the arena, each refill was normalized onto the heap,
        // copied back into the arena by the back edge, and neither copy was released.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildWorklistOfPathsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 8}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("worklist of paths walked and refilled", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_accumulator_started_afresh_memory_should_plateau()
    {
        // `interleave(first :: [])(rest)` beside `interleave(first :: ":" :: acc)(rest)`: a self-call
        // starting the accumulator with a cell of its own leaves it a grown accumulator, so every
        // cell is reference-counted and the head matched out of the walked list is released with
        // them; kept in the arena, the head's retain was stranded in the cell, one string per element.
        IrProgram ir = LowerProgramWithImports(BuildAccumulatorStartedAfreshProgram(3));
        IrFunction loop = ir.Functions.Single(function =>
            function.Origin is { Source.SourceName: "interleave" }
            && function.Instructions.Any(instruction => instruction is IrInst.Jump { Target: var target }
                && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
        loop.Instructions.Count(instruction => instruction is IrInst.Alloc { SizeBytes: 16, RuntimeManaged: false })
            .ShouldBe(0, "Every cell the loop conses is reference-counted.");

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildAccumulatorStartedAfreshProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("accumulator started afresh beside grown cells", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_loop_exit_cell_memory_should_plateau()
    {
        // `| [] -> first :: acc` and `| [] -> "end" :: acc`: the cell a loop returns consed onto its
        // list parameter is reference-counted and takes a reference to the parameter of its own. An
        // arena cell there held references to its head and to the parameter that nothing released:
        // not the caller, which saw an arena result, nor the join copying it beside `| [] -> acc`.
        IrProgram ir = LowerProgramWithImports(BuildLoopExitCellProgram(3));
        foreach (string loopName in new[] { "pairwiseGo", "stampEnd" })
        {
            IrFunction loop = ir.Functions.Single(function =>
                function.Origin is { Source.SourceName: var sourceName }
                && string.Equals(sourceName, loopName, StringComparison.Ordinal)
                && function.Instructions.Any(instruction => instruction is IrInst.Jump { Target: var target }
                    && string.Equals(target, function.Label + "_body", StringComparison.Ordinal)));
            loop.Instructions.Count(instruction => instruction is IrInst.Alloc { SizeBytes: 16, RuntimeManaged: false })
                .ShouldBe(0, $"Every cell {loopName} conses is reference-counted.");
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildLoopExitCellProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 5}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("cells a loop returns onto its list parameter", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_text_join_of_loop_built_views_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `Ashes.Text.join` over a list of `subView` views a loop conses up: the join's interleave
        // and pairwise passes keep nothing of the parts past the call.
        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildJoinedViewsProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds * 14}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("joined views built in a loop", samples, maxRssKb: 16_000);
    }

    [Test]
    public async Task Linux_backend_mapped_list_of_nullary_constructors_memory_should_plateau()
    {
        // `map(kindOf)(nums)` over an enum of nullary constructors specializes `map` for that element,
        // whose cells are reference-counted, while the caller has no copy-out for such a list and
        // used to hand the result back untouched: never released, one list per call. The caller now
        // reads the result's ownership at run time and takes the list over.
        IrProgram ir = LowerProgramWithImports(BuildMappedNullaryEnumListProgram(3));
        ir.Functions.Any(function => function.Label.EndsWith("__element", StringComparison.Ordinal))
            .ShouldBeTrue("map should specialize at the call fixing its element type.");
        AllInstructions(ir).Any(instruction =>
            instruction is IrInst.Label label
            && label.Name.StartsWith("call_deep_copy_arena_result", StringComparison.Ordinal))
            .ShouldBeTrue("The caller must take the specialized map's list over or copy it, by its returns bit.");

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int[] roundCounts = [20_000, 250_000, 1_000_000];
        List<MemoryExecutionResult> samples = new(roundCounts.Length);
        foreach (int rounds in roundCounts)
        {
            IrProgram program = LowerProgramWithImports(BuildMappedNullaryEnumListProgram(rounds));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(program).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{rounds}\n");
            samples.Add(sample);
        }

        AssertMemoryPlateaus("mapped list of nullary constructors", samples, maxRssKb: 16_000);
    }

    /// <summary>
    /// Root-cause probe for the leak above: the ADT shell built at the TCO loop's exit arm must be
    /// runtime-managed (RC) whenever it stores runtime-managed children, exactly like the equivalent
    /// tuple shape already is (see <see cref="AssertRuntimeRcTupleTcoProbe"/>'s sibling coverage).
    /// Today the constructor's own field retain (<c>RcDup</c> on xs/ys) always fires, but the shell
    /// itself (<c>AllocAdt</c>) is placed on the arena — an orphaned retain, never balanced by any
    /// drop, leaking one reference per call. This assertion targets exactly that mismatch and is
    /// expected to fail until the TCO-exit ADT placement gap is fixed.
    /// </summary>
    [Test]
    public void Linux_backend_llvm_tco_exit_adt_constructor_shell_is_runtime_managed()
    {
        IrProgram probe = LowerProgramWithImports(BuildTcoExitAdtConstructorMemoryProgram(1));
        // The synthesized arena deep copiers build arena shells by design; only the source
        // functions' own shells are under test.
        List<IrInst> instructions = probe.Functions
            .Where(function => !function.Label.StartsWith("__deepcopy", StringComparison.Ordinal))
            .Prepend(probe.EntryFunction)
            .SelectMany(function => function.Instructions)
            .ToList();

        instructions.Any(instruction => instruction is IrInst.RcDup { RuntimeManaged: true })
            .ShouldBeTrue(
                "The exit arm's constructor arguments (xs, ys) should retain their runtime-managed " +
                "children exactly as they do today.");
        instructions.Any(instruction => instruction is IrInst.AllocAdt
        {
            FieldCount: 2,
            Tagless: true,
            RuntimeManaged: false,
        }).ShouldBeFalse(
            "The TCO loop's exit-arm ADT shell must itself be runtime-managed when it stores " +
            "runtime-managed children (RcDup'd above) — an arena shell around retained RC children " +
            "orphans the retain: it is never RcDrop'd, so every call leaks one reference per field.");
    }

    [Test]
    public async Task Linux_backend_llvm_async_coroutine_value_memory_should_plateau()
    {
        // No existing suite catches a per-iteration leak inside a coroutine: the async fixtures
        // preserve copy scalars across awaits, so an owned heap value that is never released still
        // produces the right answer. These workloads run a task per iteration and hold a quarter of
        // a megabyte, so the budget is tight enough for a per-iteration leak to show as a slope.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> coroutineOwned = await MeasureLowFloorMemoryGrowthAsync(
            BuildAsyncCoroutineOwnedStringMemoryProgram,
            outputPerIteration: 8).ConfigureAwait(false);
        List<MemoryExecutionResult> frameCaptured = await MeasureLowFloorMemoryGrowthAsync(
            BuildAsyncFrameCapturedStringMemoryProgram,
            outputPerIteration: 7).ConfigureAwait(false);

        List<MemoryExecutionResult> noSuspendResult = await MeasureLowFloorMemoryGrowthAsync(
            BuildAsyncNoSuspendResultMemoryProgram,
            outputPerIteration: 6).ConfigureAwait(false);

        AssertMemoryPlateaus("async coroutine-owned string", coroutineOwned, growthBudgetKb: 512);
        List<MemoryExecutionResult> noSuspendBound = await MeasureLowFloorMemoryGrowthAsync(
            BuildAsyncNoSuspendBoundResultMemoryProgram,
            outputPerIteration: 8).ConfigureAwait(false);

        List<MemoryExecutionResult> raceLoser = await MeasureLowFloorMemoryGrowthAsync(
            BuildAsyncRaceLoserOwnedStringMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("async no-suspend result", noSuspendResult, growthBudgetKb: 512);
        AssertMemoryPlateaus("async no-suspend bound result", noSuspendBound, growthBudgetKb: 512);
        AssertMemoryPlateaus("async frame-captured string", frameCaptured, growthBudgetKb: 512);
        AssertMemoryPlateaus("async cancelled race loser", raceLoser, growthBudgetKb: 512);
    }

    [Test]
    public async Task Linux_backend_llvm_concatenation_operand_memory_should_plateau()
    {
        // A concatenation copies its operands into a fresh allocation, so a reference-counted operand
        // nothing else owns is dead once the copy is done. Abandoning it there leaks one allocation
        // per operand per evaluation. No output-checking test can see this — the program is correct
        // either way — which is why the signal has to be resident set over a growing workload.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> singleOperand = await MeasureLowFloorMemoryGrowthAsync(
            BuildConcatCountedOperandMemoryProgram,
            outputPerIteration: 7).ConfigureAwait(false);
        List<MemoryExecutionResult> boundResult = await MeasureLowFloorMemoryGrowthAsync(
            BuildConcatBoundResultMemoryProgram,
            outputPerIteration: 12).ConfigureAwait(false);

        AssertMemoryPlateaus("concatenation counted operand", singleOperand, growthBudgetKb: 512);
        AssertMemoryPlateaus("concatenation bound result", boundResult, growthBudgetKb: 512);
    }

    // One counted operand — the call result — concatenated with a literal that owns nothing.
    private static string BuildConcatCountedOperandMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + Ashes.Text.byteLength(build(7) + "!"))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    [Test]
    public async Task Linux_backend_llvm_text_split_of_a_large_text_memory_stays_bounded()
    {
        // Ashes.Text.split once walked its text with a nested recursive closure that captured the
        // whole byte buffer, and every recursive call (one per piece) copied that capture: splitting
        // a 100 KB text into 4,000 lines mapped 3,300 chunks and peaked above 600 MB. The walk now
        // takes the buffer as an explicit parameter, so the program must stay near the runtime floor.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = """
            import Ashes.Text
            let recursive grow count acc =
                if count == 0 then acc else grow(count - 1)(acc + "some source line of text\n")

            let recursive count lines total =
                match lines with
                    | [] -> total
                    | _ :: rest -> count(rest)(total + 1)

            let text = grow(4000)("")

            Ashes.IO.print(count(Ashes.Text.split(text)("\n"))(0))
            """;

        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            "<memory>");
        Diagnostics diagnostics = new();
        Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(
            diagnostics,
            new HashSet<string>(parsed.ImportNames, StringComparer.Ordinal),
            parsed.ImportAliases,
            layout.ConstructorModules);
        lowering.SetSourceContext(layout);
        IrProgram ir = IrOptimizer.Optimize(lowering.Lower(program));
        diagnostics.ThrowIfAny();
        MemoryExecutionResult result = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
        result.Stdout.ShouldBe("4001\n");
        result.MaxRssKb.ShouldBeLessThan(65_536, "splitting a 100 KB text must not copy the text once per piece");
    }

    [Test]
    public async Task Linux_backend_llvm_module_function_keeping_an_rc_adt_argument_in_its_result_owns_it()
    {
        // A module function called through its alias closure receives a reference-counted argument
        // under the runtime ownership protocol: the caller hands over a retained reference only when
        // the callee advertises that it accepts one, and drops its own reference after the call
        // either way. A callee whose parameter reaches its result keeps the value past the call, so
        // it must advertise and take ownership, as a string parameter already did; an ADT parameter
        // did not, and every wrapped kind read as whatever the freed cell was reused for.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"ashes-kept-argument-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src", "Repro"));
        try
        {
            WriteKeptArgumentProject(root);

            AshesProject project = ProjectSupport.LoadProject(Path.Combine(root, "ashes.json"));
            ProjectCompilationPlan plan = ProjectSupport.BuildCompilationPlan(project);
            CombinedCompilationLayout layout = ProjectSupport.BuildCompilationLayout(plan);
            Diagnostics diagnostics = new();
            Frontend.Program program = new Parser(layout.Source, diagnostics).ParseProgram();
            diagnostics.ThrowIfAny();
            Lowering lowering = new(diagnostics, plan.ImportedStdModules, plan.MergedAliases, layout.ConstructorModules);
            lowering.SetSourceContext(layout);
            IrProgram ir = IrOptimizer.Optimize(lowering.Lower(program));
            diagnostics.ThrowIfAny();
            MemoryExecutionResult result = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            result.Stdout.ShouldBe("4 alpha, 2 beta of 21\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A module with an ADT and a function keeping its ADT parameter in its result, and an entry
    // that calls that function through the module alias with a reference-counted argument.
    private static void WriteKeptArgumentProject(string root)
    {
        File.WriteAllText(
            Path.Combine(root, "ashes.json"),
            """{"name":"kept-argument-app","entry":"Main.ash","sourceRoots":["src"]}""");
        File.WriteAllText(Path.Combine(root, "src", "Repro", "Tok.ash"), """
                export (
                    type Kind(..),
                    value wrap,
                    value classify,
                )

                type Kind =
                    | Alpha
                    | Beta
                    | Gamma
                    deriving {Eq, Show}

                let wrap (kind: Kind) = Some(kind)

                let classify (text: Str) =
                    match text with
                        | "abc" -> Alpha
                        | "def" -> Beta
                        | _ -> Gamma
                """);
        File.WriteAllText(Path.Combine(root, "Main.ash"), """
                import Repro.Tok

                let readNext (bytes: Bytes) (position: Int) =
                    let text = Ashes.Byte.subText(bytes)(position)(3)
                    in wrap(classify(text))

                let recursive scan (bytes: Bytes) (byteCount: Int) (position: Int) kinds =
                    if position >= byteCount
                    then kinds
                    else scan(bytes)(byteCount)(position + 3)(readNext(bytes)(position) :: kinds)

                let recursive countKind (want: Kind) kinds acc =
                    match kinds with
                        | [] -> acc
                        | Some(k) :: rest ->
                            if k == want
                            then countKind(want)(rest)(acc + 1)
                            else countKind(want)(rest)(acc)
                        | None :: rest -> countKind(want)(rest)(acc)

                let source = "abcdefabcxyzabcdefabc"

                let bytes = Ashes.Byte.fromText(source)

                let kinds = scan(bytes)(Ashes.Byte.length(bytes))(0)([])

                Ashes.IO.print(Ashes.Text.fromInt(countKind(Alpha)(kinds)(0)) + " alpha, " + Ashes.Text.fromInt(countKind(Beta)(kinds)(0)) + " beta of " + Ashes.Text.fromInt(Ashes.Text.byteLength(source)))
                """);
    }

    // Two counted operands, with the result bound so a scope owns it. The binding's own drop covers
    // the result, never the operands consumed to build it.
    private static string BuildConcatBoundResultMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let recursive loop n total =
                if n <= 0 then total
                else
                    let text = build(7) + build(7)
                    in loop(n - 1)(total + Ashes.Text.byteLength(text))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    [Test]
    public async Task Linux_backend_llvm_read_builtin_consumed_call_result_memory_should_plateau()
    {
        // A read-only builtin (byte-length, print, write) fully consumes its argument, so a
        // reference-counted call result nothing owns is dead once the read completes. A nested
        // let-bound helper's call survives inlining as a real closure call whose conditionally
        // RC-normalized result reaches the read with no owner — abandoning it there leaked one
        // string per evaluation. The merged-through-a-join and reloaded-past-a-let-scope variants
        // exercise the NewlyProduced fact surviving control-flow joins and frame restores; without
        // that survival the release declines and the same leak returns one layer up.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> direct = await MeasureLowFloorMemoryGrowthAsync(
            BuildReadBuiltinNestedHelperResultMemoryProgram,
            outputPerIteration: ReadBuiltinLeakPayloadBytes).ConfigureAwait(false);
        List<MemoryExecutionResult> matchJoined = await MeasureLowFloorMemoryGrowthAsync(
            BuildReadBuiltinMatchJoinedResultMemoryProgram,
            outputPerIteration: ReadBuiltinLeakPayloadBytes).ConfigureAwait(false);
        List<MemoryExecutionResult> letScopeReloaded = await MeasureLowFloorMemoryGrowthAsync(
            BuildReadBuiltinLetScopeReloadedResultMemoryProgram,
            outputPerIteration: ReadBuiltinLeakPayloadBytes).ConfigureAwait(false);

        AssertMemoryPlateaus("read-builtin nested-helper result", direct, growthBudgetKb: 512);
        AssertMemoryPlateaus("read-builtin match-joined result", matchJoined, growthBudgetKb: 512);
        AssertMemoryPlateaus("read-builtin let-scope-reloaded result", letScopeReloaded, growthBudgetKb: 512);
    }

    // Each leaked string carries a ~1 KB payload so that a per-iteration leak clears both the RC
    // allocator's chunk granularity and the growth budget within the harness's fixed 2k/10k/50k
    // iteration counts — at the original 2-byte payload, 48k leaked cells (~1.5 MB) hid inside
    // already-mapped allocator chunks and the plateau assertion could not see them.
    private const int ReadBuiltinLeakPayloadBytes = 1026;

    private static readonly string ReadBuiltinLeakPad = new('x', ReadBuiltinLeakPayloadBytes - 2);

    // The helper closure lives in the loop body, so its call is a real closure call whose string
    // result is conditionally RC-normalized and reaches byte-length with no owner. Every helper
    // argument below derives from the loop counter — a constant argument would let compile-time
    // evaluation fold the whole byte-length chain to a scalar, deleting the very call under test.
    private static string BuildReadBuiltinNestedHelperResultMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let f = given k -> Ashes.Text.fromInt(k) + "!{{ReadBuiltinLeakPad}}"
                    in loop(n - 1)(total + Ashes.Text.byteLength(f(n - (n / 2) * 2 + 7)))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // The two call results merge through a match join before the read; every arm is a fresh RC
    // value, so the joined value must stay releasable.
    private static string BuildReadBuiltinMatchJoinedResultMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let f = given k -> Ashes.Text.fromInt(k) + "!{{ReadBuiltinLeakPad}}"
                    in
                        let v =
                            match n - (n / 2) * 2 with
                                | 0 -> f(n - (n / 2) * 2 + 7)
                                | _ -> f(n - (n / 2) * 2 + 8)
                        in loop(n - 1)(total + Ashes.Text.byteLength(v))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // The helper lives inside a callee whose body is a let chain over if joins, so the result is
    // reloaded past the let scope's own drops (a frame restore) before the read consumes it.
    private static string BuildReadBuiltinLetScopeReloadedResultMemoryProgram(int iterations)
        => $$"""
            let classify k =
                let f = given j -> Ashes.Text.fromInt(j) + "!{{ReadBuiltinLeakPad}}"
                in
                    if k < 0 then f(k - 1)
                    else if k == 0 then f(k + 1)
                    else f(k + 2)

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + Ashes.Text.byteLength(classify(n - (n / 2) * 2)))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_higher_order_list_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // No longer probes for a direct RcDrop{TypeName=Step} here:
        // `build`'s result (Hit(updated), where `updated` is itself the result of a forwarding call to
        // setAt) is a let-bound argument to a constructor application one level down from build's own
        // terminal arm — FunctionResultProvenance conservatively does not resolve that shape (setAt's
        // own result is, correctly, not proven fresh — its base-case tail can share structure with an
        // aliased input list, exactly the hazard fixed in
        // Tco_adt_with_shared_tail_list_children_survives_across_iterations), so `build(0)`'s call no
        // longer resolves through the static known-function-result fast path. The value is still
        // released correctly — the memory-growth assertion below (which actually runs and measures the
        // compiled binary, unlike this probe) continues to pass — this is a missed optimization, not a
        // correctness regression.
        IrProgram aggregateProbe = LowerProgram(BuildRuntimeRcNormalizedListAdtResultMemoryProgram(1));
        IrProgram knownArenaCallProbe = LowerProgram(BuildRuntimeRcKnownArenaListCallMemoryProgram(1));
        AllInstructions(knownArenaCallProbe).Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeTrue(
                "A statically known arena list result should normalize to RC at the call boundary.");
        IrProgram scopeProbe = LowerProgram(BuildRuntimeRcBorrowedListScopeMemoryProgram(1));
        scopeProbe.Functions.Any(function => function.Instructions.Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true })).ShouldBeTrue(
                "A borrowed list result should normalize to RC at its lexical scope boundary.");
        IrProgram transferredPayloadProbe = LowerProgram(BuildRuntimeRcTransferredListPayloadMemoryProgram(1));
        AllInstructions(transferredPayloadProbe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> copySamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderListResultMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);
        List<MemoryExecutionResult> stringSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderStringListResultMemoryProgram,
            outputPerIteration: 5).ConfigureAwait(false);
        List<MemoryExecutionResult> nestedSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderNestedListResultMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);
        List<MemoryExecutionResult> aggregateSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcNormalizedListAdtResultMemoryProgram,
            outputPerIteration: 10).ConfigureAwait(false);
        List<MemoryExecutionResult> knownArenaCallSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcKnownArenaListCallMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);
        List<MemoryExecutionResult> scopeSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcBorrowedListScopeMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);
        List<MemoryExecutionResult> transferredPayloadSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTransferredListPayloadMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC higher-order copy-list result", copySamples);
        AssertMemoryPlateaus("runtime-RC higher-order String-list result", stringSamples);
        AssertMemoryPlateaus("runtime-RC higher-order nested-list result", nestedSamples);
        AssertMemoryPlateaus("runtime-RC normalized list ADT result", aggregateSamples);
        AssertMemoryPlateaus("runtime-RC known arena list call result", knownArenaCallSamples);
        AssertMemoryPlateaus("runtime-RC borrowed list scope result", scopeSamples);
        AssertMemoryPlateaus("runtime-RC transferred list payload", transferredPayloadSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();

        // No longer asserts a direct RcDrop{TypeName=Function} for `measure` here:
        // FunctionResultProvenance deliberately never classifies a function whose result is
        // itself a returned closure (see IsDirectRcConstruction's own doc for why — whether a closure is
        // RC- or arena-represented depends on capture-time ownership state that does not exist during
        // the pre-lowering AST pass), so `measure = make(n)` now falls back to the arena
        // (CleanupResource/RestoreArenaState) path instead of the direct RC-drop fast path. This is a
        // missed optimization, not a correctness regression — the arena watermark reset below still
        // reclaims `measure`'s allocation every iteration, which is exactly what AssertMemoryPlateaus
        // verifies.
        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingClosureMemoryProgram,
            outputPerIteration: 7).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_list_capture_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingListCaptureClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingListCaptureClosureMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping List capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_aggregate_capture_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping aggregate capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_string_concat_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcStringConcatMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC string concat", samples);
    }

    [Test]
    public void Linux_backend_llvm_transfers_TCO_promoted_string_concat_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = IrOptimizer.Optimize(LowerProgramWithImports(BuildTcoPromotedStringConcatProgram()));
        List<IrFunction> emitFunctions = probe.Functions.Where(function =>
            string.Equals(function.Origin?.Source?.SourceName, "emit", StringComparison.Ordinal)).ToList();
        int runtimeConcatCount = 0;
        foreach (IrFunction emitFunction in emitFunctions)
        {
            HashSet<int> runtimeConcatTargets = emitFunction.Instructions
                .OfType<IrInst.ConcatStr>()
                .Where(concat => concat.RuntimeManaged)
                .Select(concat => concat.Target)
                .ToHashSet();
            runtimeConcatCount += runtimeConcatTargets.Count;
            emitFunction.Instructions.Any(instruction =>
                instruction is IrInst.CopyOutArena copy
                && runtimeConcatTargets.Contains(copy.SrcTemp)).ShouldBeFalse();
        }

        runtimeConcatCount.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_string_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingStringMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping string", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_known_function_string_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcKnownFunctionStringMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC known-function String result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_known_function_bytes_and_bigint_results_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcKnownFunctionBytesAndBigIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC known-function Bytes and BigInt results", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping Bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bytes_append_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBytesAppendMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC bytes append", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_append_byte_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcAppendByteMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC append byte", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bytes_from_list_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBytesFromListMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC bytes from list", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_byte_singleton_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcByteSingletonMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC byte singleton", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_empty_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEmptyBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC empty bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_fixed_width_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcFixedWidthBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC fixed-width bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_byte_subtext_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcByteSubTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC byte subText", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_from_int_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextFromIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.fromInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_to_hex_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextToHexMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.toHex", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_ascii_case_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcAsciiCaseTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC ASCII case text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_float_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcFloatTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC float text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_from_int_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntFromIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt.fromInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_bigint_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingBigIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping BigInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_parse_int_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextParseIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.parseInt Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_parse_float_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextParseFloatMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.parseFloat Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_to_int_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntToIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt.toInt Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_parse_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntParseResultMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt parse Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_uncons_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextUnconsMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.uncons Maybe", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_arithmetic_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntArithmeticMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt arithmetic", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_copy_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcCopyClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC copy-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_heap_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedHeapClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-heap-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_bytes_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedBytesClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-Bytes-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_bigint_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedBigIntClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-BigInt-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_region_managed_task_frames_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRegionManagedTaskFrameMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("region-managed task frames", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_list_performance_stays_within_arena_baseline_budget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int iterations = 500_000;
        IrProgram lowered = LowerProgram(BuildRuntimeRcListMemoryProgram(iterations));
        IrProgram runtimeRc = IrOptimizer.Optimize(lowered);
        IrProgram arenaBaseline = IrOptimizer.Optimize(ConvertRuntimeRcToArenaBaseline(lowered));

        AllInstructions(runtimeRc).Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.Alloc { RuntimeManaged: false }).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.RestoreArenaState).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.RcDrop { RuntimeManaged: true }
            or IrInst.RcDup { RuntimeManaged: true }
            or IrInst.RcIsUnique).ShouldBeFalse();

        string expectedOutput = $"{iterations * 41L}\n";
        double runtimeRcMedianMs = await CompileAndMeasureMedianCpuTimeAsync(runtimeRc, expectedOutput).ConfigureAwait(false);
        double arenaMedianMs = await CompileAndMeasureMedianCpuTimeAsync(arenaBaseline, expectedOutput).ConfigureAwait(false);
        double allowedRuntimeRcMedianMs = Math.Max(arenaMedianMs * 8.0, arenaMedianMs + 100.0);

        runtimeRcMedianMs.ShouldBeLessThanOrEqualTo(allowedRuntimeRcMedianMs,
            $"runtime RC median CPU time was {runtimeRcMedianMs:F1} ms versus arena baseline " +
                $"{arenaMedianMs:F1} ms (relative budget {allowedRuntimeRcMedianMs:F1} ms)");
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_variable_size_recycling_stays_within_arena_baseline_budget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int iterations = 3_000;
        IrProgram lowered = LowerProgram(BuildRuntimeRcVariableSizeRecyclingProgram(iterations));
        IrProgram runtimeRc = IrOptimizer.Optimize(lowered);
        IrProgram arenaBaseline = IrOptimizer.Optimize(ConvertRuntimeRcToArenaBaseline(lowered));

        AllInstructions(runtimeRc).Any(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: true }).ShouldBeTrue();

        string expectedOutput = $"{iterations * 2}\n";
        double runtimeRcMedianMs = await CompileAndMeasureMedianCpuTimeAsync(runtimeRc, expectedOutput).ConfigureAwait(false);
        double arenaMedianMs = await CompileAndMeasureMedianCpuTimeAsync(arenaBaseline, expectedOutput).ConfigureAwait(false);
        double allowedRuntimeRcMedianMs = Math.Max(arenaMedianMs * 8.0, arenaMedianMs + 100.0);

        runtimeRcMedianMs.ShouldBeLessThanOrEqualTo(allowedRuntimeRcMedianMs,
            $"variable-size runtime RC median CPU time was {runtimeRcMedianMs:F1} ms versus arena baseline " +
            $"{arenaMedianMs:F1} ms (relative budget {allowedRuntimeRcMedianMs:F1} ms)");
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_list_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram rebuiltTcoProbe = LowerProgram(BuildRuntimeRcRebuiltListTcoMemoryProgram(1));
        AllInstructions(rebuiltTcoProbe).Count(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(2,
                "The annotated rebuilt-list TCO accumulator should normalize at loop entry and replacement.");
        AllInstructions(rebuiltTcoProbe).Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated rebuilt-list TCO path must not relocate through an arena allocation.");
        AllInstructions(rebuiltTcoProbe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "List",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Rebuilt-list replacement must recursively release fixed back-edge RC cells.");
        IrProgram sharedSpineProbe = LowerProgram(BuildRuntimeRcSharedSpineListTcoMemoryProgram(1));
        RepresentationTestIr.CountOutsideGuards(AllInstructions(sharedSpineProbe), instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBe(0,
                "A cons-growing accumulator copies its loop-entry spine or replacement tail only when it is not reference-counted already.");
        AllInstructions(sharedSpineProbe).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue(
                "A cons-growing accumulator should allocate its replacement cell through runtime RC.");
        AllInstructions(sharedSpineProbe).Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated shared-spine TCO path must not relocate through an arena allocation.");
        RepresentationTestIr.CountOutsideGuards(AllInstructions(sharedSpineProbe), instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(0,
                "An affine cons replacement should move its tail ownership without duplicating it.");

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaListMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> rebuiltTcoSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcRebuiltListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> sharedSpineTcoSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcSharedSpineListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena list", samples);
        AssertMemoryPlateaus("runtime-RC rebuilt-list TCO accumulator", rebuiltTcoSamples);
        AssertMemoryPlateaus("runtime-RC shared-spine TCO accumulator", sharedSpineTcoSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_head_list_TCO_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        AssertRuntimeRcOwnedHeadListTcoProbes();

        List<MemoryExecutionResult> stringSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcStringHeadListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> nestedSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcNestedListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> transferSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcListPatternTransferMemoryProgram,
            outputPerIteration: 4).ConfigureAwait(false);
        List<MemoryExecutionResult> consumedTupleHeadSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcConsumedTupleHeadListTcoMemoryProgram,
            outputPerIteration: 14).ConfigureAwait(false);
        List<MemoryExecutionResult> tupleHeadSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTupleHeadListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> recordHeadSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcRecordHeadListTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        MemoryExecutionResult nilTailTransfer = await CompileRunWithLinuxLlvmPeakRssAsync(
            LowerProgram(BuildRuntimeRcNilTailPatternTransferProgram())).ConfigureAwait(false);
        nilTailTransfer.Stdout.ShouldBe("1\n");
        MemoryExecutionResult transferredBuilderResult = await CompileRunWithLinuxLlvmPeakRssAsync(
            LowerProgram(BuildRuntimeRcTupleHeadBuilderResultProgram())).ConfigureAwait(false);
        transferredBuilderResult.Stdout.ShouldBe("1\n");
        AssertMemoryPlateaus("runtime-RC String-head list TCO accumulator", stringSamples);
        AssertMemoryPlateaus("runtime-RC nested-list TCO accumulator", nestedSamples);
        AssertMemoryPlateaus("runtime-RC list-pattern payload transfer", transferSamples);
        AssertMemoryPlateaus("runtime-RC consumed tuple-head list", consumedTupleHeadSamples);
        AssertMemoryPlateaus("runtime-RC tuple-head list TCO accumulator", tupleHeadSamples);
        AssertMemoryPlateaus("runtime-RC record-head list TCO accumulator", recordHeadSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_unreturned_TCO_parameter_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcUnreturnedTcoParameterMemoryProgram,
            outputPerIteration: 64).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC unreturned TCO parameter", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_pattern_owned_call_argument_in_arena_aggregate_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildPatternOwnedCallArgumentArenaAggregateMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("pattern-owned call argument in arena aggregate", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_match_field_successor_tco_argument_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildMatchFieldSuccessorTcoArgumentMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("match-field successor passed by name to a self-call", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_list_argument_kept_by_copied_callee_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildListArgumentKeptByCopiedCalleeResultMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list argument retained for a callee result the caller copies out", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_matched_head_stored_into_arena_state_for_normalizing_callee_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildMatchedHeadStoredIntoArenaStateForNormalizingCalleeMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("matched head stored into an arena state cell handed to a normalizing callee", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_string_adt_list_producer_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildStringAdtListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> chained = await MeasureMemoryGrowthAsync(
            BuildStringAdtListProducerLetChainMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of string-bearing variants rebuilt by a non-tail producer every round", samples);
        AssertMemoryPlateaus("list of string-bearing variants threaded through a let chain of producers", chained);
    }

    [Test]
    public async Task Linux_backend_llvm_nested_variant_list_producer_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> nested = await MeasureMemoryGrowthAsync(
            BuildNestedVariantListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> listField = await MeasureMemoryGrowthAsync(
            BuildListFieldVariantListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of variants carrying a nested variant rebuilt by a non-tail producer every round", nested);
        AssertMemoryPlateaus("list of variants carrying a string list rebuilt by a non-tail producer every round", listField);
    }

    [Test]
    public async Task Linux_backend_llvm_passthrough_or_fresh_result_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> passthrough = await MeasureMemoryGrowthAsync(
            BuildPassthroughOrFreshResultPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> recursive = await MeasureMemoryGrowthAsync(
            BuildRecursivePassthroughOrFreshResultPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of string-bearing variants returned unchanged or rebuilt by a pass every round", passthrough);
        AssertMemoryPlateaus("list of string-bearing variants rebuilt by a producer applied to its own non-tail recursive result", recursive);
    }

    [Test]
    public async Task Linux_backend_llvm_optional_string_variant_list_producer_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> optional = await MeasureMemoryGrowthAsync(
            BuildOptionalStringVariantListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of variants carrying an optional string rebuilt by a non-tail producer every round", optional);
    }

    [Test]
    public async Task Linux_backend_llvm_accumulate_and_reverse_producer_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> accumulated = await MeasureMemoryGrowthAsync(
            BuildAccumulateAndReverseProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of variants accumulated by a loop and reversed by the generic reverse every round", accumulated);
    }

    [Test]
    public async Task Linux_backend_llvm_record_head_list_producer_pipeline_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> matched = await MeasureMemoryGrowthAsync(
            BuildRecordHeadListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> fields = await MeasureMemoryGrowthAsync(
            BuildRecordHeadFieldReadListProducerPipelineMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("list of records carrying a list of string-bearing variants rebuilt by a non-tail producer every round", matched);
        AssertMemoryPlateaus("list of records rebuilt from field reads and a let-bound list by a non-tail producer every round", fields);
    }

    [Test]
    public async Task Linux_backend_llvm_callee_result_field_reads_reassembled_into_record_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildCalleeResultFieldReadsReassembledMemoryProgram,
            outputPerIteration: 64).ConfigureAwait(false);

        AssertMemoryPlateaus("two lists read out of a callee result by field name and reassembled into a record every round", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_tail_modulo_constructor_result_release_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildTailModuloConstructorResultReleaseMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> projected = await MeasureMemoryGrowthAsync(
            BuildProjectingProducerArgumentReleaseMemoryProgram,
            outputPerIteration: 311).ConfigureAwait(false);

        AssertMemoryPlateaus("tail-modulo-constructor producer result released by its caller", samples);
        AssertMemoryPlateaus("projecting producer's consumed argument released by its caller", projected);
    }

    // A map-shaped producer builds its spine in a loop, and a caller consumes and drops the result
    // every round. The spine is reference-counted, so the producer's closure must report a
    // reference-counted result: a caller reading it as arena copies the list out and reclaims only the
    // arena, stranding the original spine one cell per element per round.
    private static string BuildTailModuloConstructorResultReleaseMemoryProgram(int iterations)
        => $$"""
            let recursive build count acc =
                if count == 0
                then acc
                else build(count - 1)(count :: acc)

            let recursive incAll values =
                match values with
                    | [] -> []
                    | head :: tail -> head + 1 :: incAll(tail)

            let recursive sum (values: List(Int)) (acc: Int) =
                match values with
                    | [] -> acc
                    | head :: tail -> sum(tail)(acc + head)

            let round size =
                let source = build(size)([])
                in
                    if sum(incAll(source))(0) > 0
                    then 1
                    else 0

            let recursive loop remaining total =
                if remaining == 0
                then total
                else loop(remaining - 1)(total + round(64))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A producer applying a function parameter per element, whose result type holds none of its
    // argument's own type: the rows go in, their labels come out. The callee's result is
    // reference-counted and its summary is poisoned by the unknown call, so the argument is handed
    // over under the adoption flag — and a result that cannot hold a value of the argument's type
    // kept nothing of it, leaving the adoption flag alone to decide. Reading the callee's returns bit
    // there instead keeps the reference on every reference-counted result, stranding the whole input
    // list, its rows and their strings, once per round.
    private static string BuildProjectingProducerArgumentReleaseMemoryProgram(int iterations)
        => $$"""
            type Row =
                | label: Str
                | weight: Int

            let recursive rows count acc =
                if count == 0
                then acc
                else rows(count - 1)(Row(label = "row" + Ashes.Text.fromInt(count), weight = count) :: acc)

            let labelOf (row: Row) = row.label

            let recursive mapWith (f: Row -> Str) (values: List(Row)) =
                match values with
                    | [] -> []
                    | head :: tail -> f(head) :: mapWith(f)(tail)

            let recursive sumLengths (values: List(Str)) (acc: Int) =
                match values with
                    | [] -> acc
                    | head :: tail -> sumLengths(tail)(acc + Ashes.Text.byteLength(head))

            let round size =
                []
                |> rows(size)
                |> mapWith(labelOf)
                |> (given (labels: List(Str)) -> sumLengths(labels)(0))

            let recursive loop remaining total =
                if remaining == 0
                then total
                else loop(remaining - 1)(total + round(64))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // The same producer with the strings staying strings: a list of strings in, a list of strings out.
    // Structural containment cannot tell "the result embeds the argument" from "the result shares
    // nothing with it" when both are the same type, so the handed-over reference survives on every
    // reference-counted result and the input spine is stranded once per round. What settles it is the
    // callee's reach account: the unknown per-element call only ever sees a destructured head, so the
    // list itself provably never comes back, and the caller releases the spine it handed over while
    // preserving the heads the result may still hold.
    private static string BuildSameElementTypeProducerArgumentReleaseMemoryProgram(int iterations)
        => $$"""
            let recursive strs count acc =
                if count == 0
                then acc
                else strs(count - 1)("row" + Ashes.Text.fromInt(count) :: acc)

            let shout (value: Str) = value + "!"

            let recursive mapWith (f: Str -> Str) (values: List(Str)) =
                match values with
                    | [] -> []
                    | head :: tail -> f(head) :: mapWith(f)(tail)

            let recursive sumLengths (values: List(Str)) (acc: Int) =
                match values with
                    | [] -> acc
                    | head :: tail -> sumLengths(tail)(acc + Ashes.Text.byteLength(head))

            let round size =
                []
                |> strs(size)
                |> mapWith(shout)
                |> (given (values: List(Str)) -> if sumLengths(values)(0) > 0 then 1 else 0)

            let recursive loop remaining total =
                if remaining == 0
                then total
                else loop(remaining - 1)(total + round(64))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    [Test]
    public async Task Linux_backend_llvm_same_element_type_producer_argument_release_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildSameElementTypeProducerArgumentReleaseMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("same-element-type producer's consumed argument released by its caller", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_string_and_record_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        AssertRuntimeRcStringTcoProbe();
        AssertRuntimeRcTupleTcoProbe();
        AssertRuntimeRcOwnedTupleTcoProbe();
        AssertRuntimeRcCopyAdtTcoProbes();
        AssertRuntimeRcOwnedRecordTcoProbe();
        AssertRuntimeRcOwnedVariantTcoProbe();

        List<MemoryExecutionResult> strings = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaStringMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> records = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaRecordMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> growingStrings = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaGrowingStringMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> tuples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTupleTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> ownedTuples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcOwnedTupleTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> copyAdts = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcCopyAdtTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> ownedRecords = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcOwnedRecordTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> ownedVariants = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcOwnedVariantTcoMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena string", strings);
        AssertMemoryPlateaus("legacy arena pointer record", records);
        AssertMemoryPlateaus("runtime-RC growing string accumulator", growingStrings);
        AssertMemoryPlateaus("runtime-RC tuple TCO accumulator", tuples);
        AssertMemoryPlateaus("runtime-RC owned-child tuple TCO accumulator", ownedTuples);
        AssertMemoryPlateaus("runtime-RC copy-field ADT TCO accumulator", copyAdts);
        AssertMemoryPlateaus("runtime-RC owned-child record TCO accumulator", ownedRecords);
        AssertMemoryPlateaus("runtime-RC owned-child variant TCO accumulator", ownedVariants);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_bytes_and_bigint_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram growingBigIntProbe = LowerProgram(BuildRuntimeRcGrowingBigIntMemoryProgram(1));
        AllInstructions(growingBigIntProbe).Count(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: IrInst.CopyOutArena.BigIntSize,
                RuntimeManaged: true
            }).ShouldBeGreaterThanOrEqualTo(2,
                "The annotated TCO BigInt accumulator should normalize at loop entry and replacement.");
        AllInstructions(growingBigIntProbe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "BigInt",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "The BigInt replacement drop must remain fixed at the back-edge.");
        AllInstructions(growingBigIntProbe).Any(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: IrInst.CopyOutArena.BigIntSize,
                RuntimeManaged: false
            }).ShouldBeFalse(
                "The migrated BigInt TCO path must not relocate through an arena allocation.");

        List<MemoryExecutionResult> bytes = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaBytesMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> bigints = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaBigIntMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> growingBigInts = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcGrowingBigIntMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena bytes", bytes);
        AssertMemoryPlateaus("legacy arena bigint", bigints);
        AssertMemoryPlateaus("runtime-RC growing BigInt accumulator", growingBigInts);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildLegacyArenaClosureMemoryProgram(2_000));
        probe.Functions.SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.MakeClosure).ShouldBeTrue(
                "memory workload must exercise heap-backed closures");

        List<MemoryExecutionResult> closures = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaClosureMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena closure", closures);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_tco_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcTcoClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction => instruction is IrInst.MakeClosure
        {
            RuntimeManaged: true,
        }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction => instruction is IrInst.RcDrop
        {
            TypeName: "Function",
            RuntimeManaged: true,
        }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction => instruction is IrInst.CopyOutClosure
        {
            RuntimeManaged: false,
        }).ShouldBeFalse();

        List<MemoryExecutionResult> closures = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTcoClosureMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        AssertMemoryPlateaus("runtime-RC TCO closure", closures);
    }

    [Test]
    public void Reuse_update_reads_the_value_it_overwrites_from_the_token_cell()
    {
        IrProgram ir = LowerProgramWithImports(BuildPersistentMapStringUpdateMemoryProgram(1));
        int checkedUpdates = 0;
        foreach (IrFunction function in ir.Functions)
        {
            foreach (IrInst.CopyStringIntoOrFresh update in function.Instructions.OfType<IrInst.CopyStringIntoOrFresh>())
            {
                IrInst.GetAdtField oldValue = function.Instructions.OfType<IrInst.GetAdtField>()
                    .Single(read => read.Target == update.OldBlobTemp);
                function.Instructions.OfType<IrInst.AllocReusing>().Any(reuse => reuse.TokenTemp == oldValue.Ptr).ShouldBeTrue(
                    "The backend returns a fresh cell for a token that turns out reference counted, so the old "
                    + "value is only ever in the token.");
                checkedUpdates++;
            }
        }

        checkedUpdates.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Linux_backend_llvm_persistent_map_reuse_memory_should_plateau_as_updates_scale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram mapProbe = LowerProgramWithImports(BuildPersistentMapStringUpdateMemoryProgram(1));
        AllInstructions(mapProbe).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();
        AllInstructions(mapProbe).Any(instruction => instruction is IrInst.CopyStringIntoOrFresh).ShouldBeTrue();

        IrProgram hashMapProbe = LowerProgramWithImports(BuildPersistentHashMapUpdateMemoryProgram(1));
        AllInstructions(hashMapProbe).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();
        hashMapProbe.Functions.Any(function =>
            function.Origin?.Kind == IrFunctionOriginKind.TraitOperatorSpecialization
            && string.Equals(
                function.Origin.Source?.SourceName,
                "Ashes_Collection_HashMap_hMax",
                StringComparison.Ordinal)).ShouldBeTrue();

        List<MemoryExecutionResult> mapSamples = await MeasureImportedMemoryGrowthAsync(
            BuildPersistentMapStringUpdateMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> hashMapSamples = await MeasureImportedMemoryGrowthAsync(
            BuildPersistentHashMapUpdateMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("persistent Map String-value update", mapSamples);
        AssertMemoryPlateaus("persistent HashMap fixed-key update", hashMapSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_one_brc_memory_stays_bounded_as_rows_scale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string source = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "challenges", "1brc", "brc.ash"));
        IrProgram ir = LowerProgramWithImports(source);
        AllInstructions(ir).Any(instruction => instruction is IrInst.ParallelQueueStart).ShouldBeTrue();
        AllInstructions(ir).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();

        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(
            ir,
            BackendCompileOptions.Default with { ParallelWorkerCap = 4 });
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"one_brc_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            int[] rowCounts = [75_000, 150_000, 300_000];
            List<MemoryExecutionResult> samples = new(rowCounts.Length);
            List<MemoryExecutionResult> mappedInputAdjustedSamples = new(rowCounts.Length);
            foreach (int rows in rowCounts)
            {
                string inputPath = Path.Combine(tmpDir, $"measurements_{rows}.txt");
                File.WriteAllText(inputPath, BuildOneBrcMeasurements(rows));
                MemoryExecutionResult sample = await MeasureOneBrcMedianPeakRssAsync(exePath, inputPath)
                    .ConfigureAwait(false);
                samples.Add(sample);
                long mappedInputKb = (new FileInfo(inputPath).Length + 1023) / 1024;
                mappedInputAdjustedSamples.Add(sample with
                {
                    MaxRssKb = Math.Max(1, sample.MaxRssKb - mappedInputKb),
                });
            }

            // mmap-backed input pages are part of Linux maximum RSS and necessarily scale with the
            // generated file even when the compiler-managed heap is perfectly bounded. Keep the raw
            // ceiling, but subtract that known representation cost from the plateau comparison.
            AssertMemorySamplesBelowCeiling("1BRC bounded-row profile", samples, maxRssKb: 128_000);
            AssertMemoryPlateaus(
                "1BRC bounded-row profile excluding mapped input",
                mappedInputAdjustedSamples,
                maxRssKb: 128_000,
                growthBudgetKb: 8_192);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_parallel_worker_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildParallelWorkerMemoryProgram,
            outputPerIteration: 3).ConfigureAwait(false);

        AssertMemoryPlateaus("parallel worker shared list", samples);
    }

    [Test]
    public async Task Linux_backend_parallel_shared_values_stay_outside_non_atomic_runtime_rc()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildParallelWorkerMemoryProgram(iterations: 1));
        AllInstructions(program).Any(instruction => instruction is IrInst.ParallelFork).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_recursive_runtime_rc_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let tree = Node(Node(Leaf)(20)(Leaf))(42)(Node(Leaf)(22)(Leaf)) in
                    match tree with
                        | Leaf -> loop(n - 1)(total)
                        | Node(_, value, _) -> loop(n - 1)(total + value)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("840000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_runtime_rc_copy_lists()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let values = [42, 2, 1]
            match values with
                | [] -> Ashes.IO.print(0)
                | head :: _ -> Ashes.IO.print(head)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_runtime_rc_copy_lists()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let values = [1, 2, 3] in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("20000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_share_runtime_rc_copy_list_tails()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let tail = [40, 2]
            let values = 1 :: tail
            match values with
                | [] -> Ashes.IO.print(0)
                | head :: _ ->
                    match tail with
                        | [] -> Ashes.IO.print(0)
                        | tailHead :: _ -> Ashes.IO.print(head + tailHead)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("41\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_share_runtime_rc_copy_list_tails()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = (let fresh = [40, 2] in fresh) in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("820000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_local_runtime_rc_record()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Point =
                | x: Int
                | y: Int

            let point = Point(x = 40, y = 2)
            Ashes.IO.print(point.x + point.y)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_local_runtime_rc_records()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Point =
                | x: Int
                | y: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let point = Point(x = 40, y = 2)
                    in loop(n - 1)(total + point.x + point.y)

            Ashes.IO.print(loop(2000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("84000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_nested_runtime_rc_records()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let node = Node(child = Leaf(value = 40), bonus = 2)
            Ashes.IO.print(node.bonus)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("2\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_nested_runtime_rc_records_at_tco_back_edges()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let node = Node(child = Leaf(value = 40), bonus = 2)
                    in loop(n - 1)(total + node.bonus)

            Ashes.IO.print(loop(2000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("4000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_keep_direct_curried_list_result_alive_inside_arena_adt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = """
            type Step =
                | Done
                | Hit(List(Int))

            let recursive setAt i v xs =
                match xs with
                    | [] -> []
                    | h :: t ->
                        if i == 0
                        then v :: t
                        else h :: setAt(i - 1)(v)(t)

            let recursive show xs =
                match xs with
                    | [] -> ""
                    | h :: t -> Ashes.Text.fromInt(h) + "," + show(t)

            let build _ =
                let updated = setAt(1)(42)([10, 20, 30])
                in Hit(updated)

            let step = build(0)
            let rendered =
                match step with
                    | Done -> "done"
                    | Hit(values) -> show(values)
            Ashes.IO.print(rendered)
            """;

        IrProgram program = LowerProgram(source);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.AllocAdt { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("10,42,30,\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_transfer_runtime_managed_match_payload_before_parent_drop()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = """
            type Box =
                | First(List(Int))
                | Second(List(Int))

            let recursive setAt i v xs =
                match xs with
                    | [] -> []
                    | h :: t ->
                        if i == 0
                        then v :: t
                        else h :: setAt(i - 1)(v)(t)

            let recursive show xs =
                match xs with
                    | [] -> ""
                    | h :: t -> Ashes.Text.fromInt(h) + "," + show(t)

            let box = (let inner = First(setAt(1)(42)([10, 20, 30])) in inner)
            let values = match box with | First(items) -> items | Second(items) -> items
            Ashes.IO.print(show(values))
            """;

        IrProgram program = LowerProgram(source);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBeTrue(
                "A shared parent must duplicate the transferred child before decrementing the parent.");

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);
        result.Stdout.ShouldBe("10,42,30,\n");
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_panic_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.panic(\"boom\")"));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_panic_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.panic(\"boom\")", expectedExitCode: 1).ConfigureAwait(false);
        result.Stdout.ShouldBe("boom\n");
    }

    private static byte[] CompileForLinux(string source)
    {
        var ir = LowerExpression(source);
        return CompileForLinux(ir);
    }

    private static byte[] CompileForLinux(IrProgram ir)
    {
        return new LinuxX64LlvmBackend().Compile(ir);
    }

    private static void AssertLinuxLlvmCompiles(IrProgram ir)
    {
        var bytes = CompileForLinux(ir);
        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    private static IrProgram LowerExpression(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_tls_echo_via_serve_tls()
    {
        // Server-side TLS: the Ashes program terminates TLS (Ashes.Net.Tls.Server.serveTls with a
        // self-signed cert), the C# test is an SslStream CLIENT that trusts the test cert via a
        // validation callback. Exercises the TLS server config build (certified key from PEMs),
        // the server half of the handshake (parking on WaitTlsWantRead/Write), and the shared TLS
        // send/receive/close paths on an accepted connection.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tls
            import Ashes.Net.Tls.Server
            import Ashes.Task
            let onClient tls =
                async(match await Ashes.Net.Tls.receive(tls)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tls.send(tls)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tls.close(tls))
            in match Ashes.Task.run(Ashes.Net.Tls.Server.serveTls({{port}})("cert.pem")("key.pem")(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_tls_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            WriteSelfSignedServerPems(tmpDir);
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            foreach (var payload in new[] { "tls-one", "tls-two" })
            {
                var reply = await TlsConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    /// <summary>
    /// Writes a self-signed ECDSA P-256 localhost certificate + PKCS#8 key as cert.pem / key.pem.
    /// ECDSA keeps handshakes cheap under qemu emulation (see TlsLoopbackTestHost).
    /// </summary>
    private static void WriteSelfSignedServerPems(string directory)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(directory, "cert.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
    }

    private static async Task<string> TlsConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await using (tls.ConfigureAwait(false))
                {
                    await tls.AuthenticateAsClientAsync("localhost").WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await tls.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await tls.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_http_over_the_tcp_server()
    {
        // HTTP layer coverage: Ashes.Net.Http.Server.serve parses the request line, routes on the path,
        // and writes an HTTP/1.1 response. The test drives it with raw HTTP GETs over loopback.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = HttpRoutingServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_http_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            await AssertHttpRoutingResponsesAsync(port).ConfigureAwait(false);
            await AssertHttpBufferingAndPipeliningAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string HttpRoutingServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let route req =
            async(match Ashes.Net.Http.Server.path(req) with
                | "/health" -> Ashes.Net.Http.Server.text(200)("ok")
                | "/echo" -> Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req))
                | "/ua" ->
                    match Ashes.Net.Http.Server.header(req)("user-agent") with
                        | Some(ua) -> Ashes.Net.Http.Server.text(200)(ua)
                        | None -> Ashes.Net.Http.Server.text(200)("no-ua")
                | "/data" -> Ashes.Net.Http.Server.json(200)("{\"ok\":true}")
                | _p -> Ashes.Net.Http.Server.text(404)("not found"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    private static async Task AssertHttpRoutingResponsesAsync(int port)
    {
        var health = await HttpGetRawWithRetryAsync(port, "/health").ConfigureAwait(false);
        health.ShouldContain("HTTP/1.1 200 OK");
        health.ShouldContain("Content-Length: 2");
        health.ShouldEndWith("ok");

        var missing = await HttpGetRawWithRetryAsync(port, "/nope").ConfigureAwait(false);
        missing.ShouldContain("HTTP/1.1 404 Not Found");
        missing.ShouldEndWith("not found");

        // Request body is available to the handler.
        var echoed = await HttpRequestRawWithRetryAsync(port,
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 9\r\nConnection: close\r\n\r\nhi-there!").ConfigureAwait(false);
        echoed.ShouldEndWith("body=hi-there!");

        // Request headers are read case-insensitively (handler asks "user-agent"; client sends "User-Agent").
        var ua = await HttpRequestRawWithRetryAsync(port,
            "GET /ua HTTP/1.1\r\nHost: localhost\r\nUser-Agent: probe/2.0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        ua.ShouldEndWith("probe/2.0");

        // json() sets an application/json Content-Type.
        var data = await HttpGetRawWithRetryAsync(port, "/data").ConfigureAwait(false);
        data.ShouldContain("Content-Type: application/json");
        data.ShouldEndWith("{\"ok\":true}");
    }

    private static async Task AssertHttpBufferingAndPipeliningAsync(int port)
    {
        // A body larger than one read is buffered across receives (cross-read buffering).
        var bigBody = new string('A', 100_000);
        var bigEcho = await HttpRequestRawWithRetryAsync(port,
            $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {bigBody.Length}\r\nConnection: close\r\n\r\n{bigBody}").ConfigureAwait(false);
        bigEcho.ShouldEndWith("body=" + bigBody);

        // Keep-alive: two requests on a single TCP connection, second response still correct.
        var (first, second) = await HttpTwoRequestsOneConnectionAsync(port,
            "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n",
            "GET /data HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        first.ShouldContain("HTTP/1.1 200 OK");
        first.ShouldContain("Connection: keep-alive");
        first.ShouldEndWith("ok");
        second.ShouldContain("Content-Type: application/json");
        second.ShouldEndWith("{\"ok\":true}");

        // Pipelining across a split body: the tail of a POST body arrives in the same read as
        // the NEXT request. The incremental body path must hand the handler exactly
        // Content-Length bytes and carry the overshoot over as the next request's buffer.
        var combined = await HttpTwoSegmentsOneConnectionAsync(port,
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\n\r\nhello-",
            "tail" + "GET /data HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        combined.ShouldContain("body=hello-tail");
        combined.ShouldContain("{\"ok\":true}");
    }

    [Test]
    public async Task Linux_backend_llvm_http_keep_alive_memory_should_plateau_as_requests_scale()
    {
        // Async-loop arena reset: the HTTP connection loop reclaims its per-request allocations
        // (buffered reads, parse scaffolding, the rendered response) at the loop back-edge. Serve a
        // ~16 KB body over ONE keep-alive connection many times and assert the server's resident
        // memory stays flat — without the reset every request leaks its garbage into the connection's
        // arena (~50 MB across this run).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = HttpKeepAliveMemoryServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_httpka_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var warm = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            warm.ShouldContain("HTTP/1.1 200 OK");

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");

            // Sample after progressively larger request counts. The late phase starts only after
            // connection/parser/response scaffolding has settled, so retained per-request garbage
            // shows up as a proportional slope rather than being hidden in startup noise.
            await HttpKeepAliveBurstAsync(stream, request, 50).ConfigureAwait(false);
            long rssAt50Kb = ReadVmRssKb(proc.Id);
            await HttpKeepAliveBurstAsync(stream, request, 450).ConfigureAwait(false);
            long rssAt500Kb = ReadVmRssKb(proc.Id);
            await HttpKeepAliveBurstAsync(stream, request, 2500).ConfigureAwait(false);
            long rssAt3000Kb = ReadVmRssKb(proc.Id);

            proc.HasExited.ShouldBeFalse();
            rssAt50Kb.ShouldBeGreaterThan(0);
            rssAt500Kb.ShouldBeGreaterThan(0);
            rssAt3000Kb.ShouldBeGreaterThan(0);
            long totalGrowthKb = rssAt3000Kb - rssAt50Kb;
            long lateGrowthKb = rssAt3000Kb - rssAt500Kb;
            totalGrowthKb.ShouldBeLessThan(24_000,
                $"server RSS grew {totalGrowthKb} KB from 50 to 3000 requests " +
                $"({rssAt50Kb}, {rssAt500Kb}, {rssAt3000Kb} KB checkpoints)");
            lateGrowthKb.ShouldBeLessThan(16_000,
                $"server RSS grew {lateGrowthKb} KB from 500 to 3000 requests " +
                $"({rssAt50Kb}, {rssAt500Kb}, {rssAt3000Kb} KB checkpoints)");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string HttpKeepAliveMemoryServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        import Ashes.Text
        let recursive repeat : Str -> Int -> Str = given (s) -> given (n) ->
            if n == 0
            then s
            else repeat(s + s)(n - 1)
        let big = repeat("x")(14)
        let route req =
            async(Ashes.Net.Http.Server.text(200)(big))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serveParallel({{port}})(1)(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    // Sends `count` identical keep-alive requests on one connection, fully reading each response
    // (headers + the fixed 16384-byte body) before sending the next.
    private static async Task HttpKeepAliveBurstAsync(NetworkStream stream, byte[] request, int count)
    {
        var buffer = new byte[65536];
        for (int i = 0; i < count; i++)
        {
            await stream.WriteAsync(request).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                read.ShouldBeGreaterThan(0, "server closed a keep-alive connection mid-response");
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0 && total >= headerEnd + 4 + 16384)
                {
                    break;
                }
            }
        }
    }

    // Reads the process's resident set size (VmRSS, in KB) from /proc.
    private static long ReadVmRssKb(int pid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(line[6..].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    private static long ReadMinorFaults(int pid)
    {
        string stat = File.ReadAllText($"/proc/{pid}/stat");
        int commandEnd = stat.LastIndexOf(')');
        commandEnd.ShouldBeGreaterThan(0);
        string[] fieldsFromState = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fieldsFromState[7], CultureInfo.InvariantCulture);
    }

    // Sends two requests on one persistent connection, returning both responses (keep-alive).
    private static async Task<(string First, string Second)> HttpTwoRequestsOneConnectionAsync(int port, string firstRequest, string secondRequest)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(firstRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var firstBuffer = new byte[4096];
                    int firstRead = await stream.ReadAsync(firstBuffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(secondRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var second = await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return (Encoding.UTF8.GetString(firstBuffer, 0, firstRead).Trim(), second.Trim());
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static Task<string> HttpGetRawWithRetryAsync(int port, string path)
        => HttpRequestRawWithRetryAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

    private static async Task<string> HttpRequestRawWithRetryAsync(int port, string request)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(request)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> HttpTwoSegmentsOneConnectionAsync(int port, string firstSegment, string secondSegment)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(firstSegment)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    // Let the server consume the first segment and park mid-body before the rest arrives.
                    await Task.Delay(150).ConfigureAwait(false);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(secondSegment)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_shut_down_gracefully_on_sigterm()
    {
        // Graceful shutdown: SIGTERM interrupts the parked accept, serve stops and returns Ok(()), so
        // the program prints its clean-stop message and exits 0 (rather than being terminated).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = SigtermShutdownServerSource(port);

        // Single reactor keeps the signal/exit deterministic (no worker/pdeathsig race in the test).
        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_shutdown_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait until it accepts, then send SIGTERM and assert a clean stop.
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string SigtermShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(m) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_stop_capability_should_stop_the_server()
    {
        // Programmatic stop: a handler performs Stop.stop(Unit) after replying; the server stops
        // accepting, drains, and returns Ok(()) so the program prints its clean-stop message and
        // exits 0 — the capability's requirement threads out of the handler through serve (the
        // recursive-helper row fix), and Stop.stop rides the signal/drain path.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = StopCapabilityServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_stop_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // One client connects and sends; the handler replies, then requests stop.
            var reply = await ConnectSendReceiveWithRetryAsync(port, "now").ConfigureAwait(false);
            reply.ShouldBe("bye: now");

            await AssertExitsCleanlyAsync(proc, "stopped-by-request").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string StopCapabilityServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Net.Tcp.send(client)("bye: " + msg) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) ->
                            let _s = Stop.stop(Unit)
                            in await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-by-request")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_shutdown_should_drain_inflight_handlers()
    {
        // Drain-with-timeout: SIGTERM while a handler is mid-request stops accepting but lets the
        // in-flight handler finish (the client still gets its reply), then the server exits Ok(()).
        // Also covers the second-signal force: a SIGTERM during a never-ending drain exits at once.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = DrainShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_drain_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Open a connection and send; the handler sleeps 700 ms before replying.
            using var client = new TcpClient();
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("drain-me")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            // Give the server a moment to accept and spawn the handler, then SIGTERM mid-sleep.
            await Task.Delay(200).ConfigureAwait(false);
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            // The in-flight handler must still complete and the reply must arrive.
            var buf = new byte[256];
            int n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Encoding.UTF8.GetString(buf, 0, n).ShouldBe("done: drain-me");

            // And the server must then exit cleanly, well before the 10 s drain bound.
            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string DrainShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Task.sleep(700) with
                        | Error(e2) -> Error(e2)
                        | Ok(_t) ->
                            match await Ashes.Net.Tcp.send(client)("done: " + msg) with
                                | Error(e3) -> Error(e3)
                                | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_shutdown_should_forward_to_workers_and_reap_them()
    {
        // Multi-reactor graceful shutdown: SIGTERM to the PARENT forwards to the forked workers and
        // the parent reaps them before exiting, so no worker is cut mid-drain by the death signal
        // and no orphan keeps the port open after the parent reports a clean stop.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = WorkerForwardShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_mwdrain_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait for a worker to accept, then SIGTERM the parent only.
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);

            // All workers must be gone with the parent: the port must refuse new connections.
            await AssertPortRefusesConnectionsAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string WorkerForwardShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(m) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serveParallel({{port}})(3)(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    private static async Task AssertPortRefusesConnectionsAsync(int port)
    {
        await Task.Delay(200).ConfigureAwait(false);
        var refused = false;
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            refused = true;
        }
        refused.ShouldBeTrue("workers should have drained and exited with the parent");
    }

    [Test]
    public async Task Linux_backend_llvm_shutdown_second_signal_should_force_exit()
    {
        // A second SIGTERM during the drain forces an immediate clean exit even though a handler
        // is still running (here: a handler that sleeps far past any reasonable test bound).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ForceExitShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_force_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            using var client = new TcpClient();
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("hang")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);

            // First signal starts the drain (60 s handler keeps it alive), second forces exit(0).
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            var exited = await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);
            exited.ShouldBeTrue();
            proc.ExitCode.ShouldBe(0);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ForceExitShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Task.sleep(60000) with
                        | Error(e2) -> Error(e2)
                        | Ok(_t) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_parse_query_and_reject_oversized()
    {
        // Query-string parsing + percent-decoding (path stripped of the query, %XX/+ decoded) and the
        // request size limit (a declared Content-Length over the cap returns 413 on the header).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = QueryParsingServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_query_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    var reply = await HttpRequestRawWithRetryAsync(port, "GET /users?name=Ada%20Lovelace HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    reply.ShouldEndWith("p=/users n=Ada Lovelace");
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            // A declared Content-Length above the 8 MiB cap is rejected with 413 on the header.
            var tooBig = await HttpRequestRawWithRetryAsync(port, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 99999999\r\nConnection: close\r\n\r\nabc").ConfigureAwait(false);
            tooBig.ShouldContain("413 Payload Too Large");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string QueryParsingServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req =
            async(match Ashes.Net.Http.Server.queryParam(req)("name") with
                | Some(v) -> Ashes.Net.Http.Server.text(200)("p=" + Ashes.Net.Http.Server.path(req) + " n=" + v)
                | None -> Ashes.Net.Http.Server.text(200)("p=" + Ashes.Net.Http.Server.path(req) + " none"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_stream_a_chunked_response()
    {
        // Response streaming: the handler returns Ashes.Net.Http.Server.streamed with a pull `step`
        // producer (a function-typed field of the StreamStep ADT). The server frames the body with
        // Transfer-Encoding: chunked, one chunk per pulled StreamChunk, terminated by StreamDone.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ChunkedResponseServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_stream_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var raw = await HttpGetRawWithRetryAsync(port, "/stream").ConfigureAwait(false);
            raw.ShouldContain("Transfer-Encoding: chunked");
            raw.ShouldNotContain("Content-Length");
            // Three 6-byte chunks then the terminating zero-length chunk.
            raw.ShouldContain("6\r\npart0-\r\n");
            raw.ShouldContain("6\r\npart1-\r\n");
            raw.ShouldContain("6\r\npart2-\r\n");
            // The last chunk is immediately followed by the zero-length terminating chunk
            // (the helper trims the final CRLFs).
            raw.ShouldEndWith("part2-\r\n0");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedResponseServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Text
        import Ashes.Net.Http.Server
        import Ashes.Task
        let step acc =
            async(match Ashes.Text.parseInt(acc) with
                | Error(_e) -> StreamDone
                | Ok(i) ->
                    if i >= 3
                    then StreamDone
                    else StreamChunk("part" + Ashes.Text.fromInt(i) + "-")(Ashes.Text.fromInt(i + 1)))
        let route _req =
            async(Ashes.Net.Http.Server.streamed(200)("Content-Type: text/plain\r\n")("0")(step))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_decode_a_chunked_request_body()
    {
        // Transfer-Encoding: chunked request body — decoded and echoed. Also split across two writes so
        // the second read parks, exercising cross-read chunk buffering.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ChunkedRequestServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_chunked_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        var head = Encoding.ASCII.GetBytes("POST /e HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n4\r\nWiki\r\n");
                        await stream.WriteAsync(head).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(200).ConfigureAwait(false);
                        var tail = Encoding.ASCII.GetBytes("5\r\npedia\r\n0\r\n\r\n");
                        await stream.WriteAsync(tail).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldEndWith("body=Wikipedia");
                        break;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedRequestServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req = async(Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req)))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_decode_chunked_frames_across_many_writes()
    {
        // Incremental chunked decoding: many chunk frames arriving in many separate writes, with
        // writes deliberately split MID-frame (inside a size line and inside chunk data), keep-alive
        // (not Connection: close), and the NEXT pipelined request arriving in the same write as the
        // terminating 0-frame. The decoder must carry only the undecoded tail between reads, hand
        // the handler the exact body, and serve the pipelined request from the remainder.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ChunkedFramesServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_chunkinc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            await AssertChunkedFramesDecodedAcrossManyWritesAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedFramesServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Text
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req =
            async(match Ashes.Net.Http.Server.path(req) with
                | "/len" -> Ashes.Net.Http.Server.text(200)("len=" + Ashes.Text.fromInt(Ashes.Text.byteLength(Ashes.Net.Http.Server.body(req))))
                | _p -> Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req)))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    private static async Task AssertChunkedFramesDecodedAcrossManyWritesAsync(int port)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    // 30 frames of 100 bytes; the full wire text is cut into 37-byte writes so
                    // frame boundaries never align with write boundaries.
                    var piece = new string('x', 100);
                    var wire = new StringBuilder("POST /body HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
                    for (int i = 0; i < 30; i++)
                    {
                        wire.Append("64\r\n").Append(piece).Append("\r\n");
                    }
                    wire.Append("0\r\n\r\n");
                    // Pipelined second request in the same final write as the terminator.
                    wire.Append("GET /len HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    var wireBytes = Encoding.ASCII.GetBytes(wire.ToString());
                    for (int off = 0; off < wireBytes.Length; off += 37)
                    {
                        int n = Math.Min(37, wireBytes.Length - off);
                        await stream.WriteAsync(wireBytes.AsMemory(off, n)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(2).ConfigureAwait(false);
                    }
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var replies = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                    replies.ShouldContain("body=" + string.Concat(Enumerable.Repeat(piece, 30)));
                    replies.ShouldEndWith("len=0");
                    break;
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_http_concurrently_across_workers()
    {
        // Concurrent HTTP under the run-queue scheduler: many simultaneous requests against a
        // multi-reactor server, every one must get a 200 and the server must stay up. Regression
        // guard for the async-loop lowering — with the server loops compiled as nested blocking
        // scheduler runs (instead of one suspending coroutine each), this crashed the reactor.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ConcurrentHttpServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_httpc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Readiness.
            var warm = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            warm.ShouldContain("HTTP/1.1 200 OK");

            // Exercise sustained concurrency in bounded waves. Starting all clients through
            // Task.Run adds host thread-pool and socket-setup saturation to a scheduler test; the
            // test only needs enough simultaneous clients to keep every reactor busy.
            const int total = 120;
            const int waveSize = 8;
            int ok = 0;
            for (int offset = 0; offset < total; offset += waveSize)
            {
                int count = Math.Min(waveSize, total - offset);
                Task<bool>[] tasks = Enumerable.Range(0, count).Select(async _ =>
                {
                    var r = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
                    return r.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal);
                }).ToArray();
                foreach (bool succeeded in await Task.WhenAll(tasks).ConfigureAwait(false))
                {
                    if (succeeded)
                    {
                        ok++;
                    }
                }
            }

            ok.ShouldBe(total);
            proc.HasExited.ShouldBeFalse();
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ConcurrentHttpServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let route req =
            async(Ashes.Net.Http.Server.text(200)("ok"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serveParallel({{port}})(3)(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_link_a_program_split_into_several_objects()
    {
        // The partitions share the runtime globals (the arena cursors, the string literals), call
        // each other's functions by name, and one of them holds the entry: every user function,
        // string literal, and standard-library call below must resolve across the three objects.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = """
            import Ashes.IO
            import Ashes.Text
            let parts = Ashes.Text.split("GET / HTTP/1.1")(" ")
            in match parts with
                | method :: path :: version :: _rest ->
                    Ashes.IO.print(method + "|" + path + "|" + version + "|" + Ashes.Text.fromInt(Ashes.Text.byteLength(path)))
                | _other -> Ashes.IO.print("bad")
            """;
        IrProgram program = IrOptimizer.Optimize(LowerProgramWithImports(source));

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(
            program,
            compileOptions: BackendCompileOptions.Default with { ObjectPartitions = 3 }).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("GET|/|HTTP/1.1|1\n");
    }

    [Test]
    public async Task Linux_backend_llvm_string_list_copy_out_should_snapshot_all_heads_before_allocating()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string source = """
            import Ashes.IO
            import Ashes.Text
            let parts = Ashes.Text.split("GET / HTTP/1.1")(" ")
            in match parts with
                | method :: path :: version :: _rest ->
                    Ashes.IO.print(method + "|" + path + "|" + version)
                | _other -> Ashes.IO.print("bad")
            """;
        IrProgram program = IrOptimizer.Optimize(LowerProgramWithImports(source));
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutList
        {
            HeadCopy: IrInst.ListHeadCopyKind.String
        }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("GET|/|HTTP/1.1\n");
        result.ExitCode.ShouldBe(0);
    }

    [Test]
    public async Task Linux_backend_llvm_minimal_http_handler_should_serve_with_one_worker()
    {
        // Keep this at one worker: retries against a multi-worker server can hide a worker that
        // corrupts its private task arena while parsing the first request.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        string source = ConcurrentHttpServerSource(port).Replace(
            "serveParallel(" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(3)",
            "serveParallel(" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(1)",
            StringComparison.Ordinal);

        IrProgram ir = IrOptimizer.Optimize(LowerProgramWithImports(source));
        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(ir);
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_http1_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            string response = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            response.ShouldContain("HTTP/1.1 200 OK");
            response.ShouldEndWith("ok");
            proc.HasExited.ShouldBeFalse();
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_serve_parallel_should_serve_across_workers()
    {
        // serveParallel forks an explicit number of independent reactor processes that each bind the
        // port with SO_REUSEPORT; the kernel load-balances connections across them. Assert it serves
        // correctly with several workers (the functional contract; worker fan-out is a perf property).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ServeParallelEchoServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_par_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            int served = 0;
            while (served < 6 && DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        var payload = Encoding.UTF8.GetBytes($"m{served}");
                        await stream.WriteAsync(payload).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldBe($"echo:m{served}");
                        served++;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            served.ShouldBe(6);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ServeParallelEchoServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Net.Tcp.send(client)("echo:" + msg) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serveParallel({{port}})(3)(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_read_across_a_parking_receive()
    {
        // Regression: a spawned handler that accumulates across a receive which PARKS on epoll used to
        // overflow the stack (ashes_detached_wait_meta counted the mid-step task as runnable, forcing a
        // non-blocking spin that leaked per-wait stack scratch). The client sends the request in two
        // writes with a gap so the second receive parks; the handler must buffer and reply.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ParkingReceiveServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_park_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(250).ConfigureAwait(false);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("-world")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldBe("got:hello-world");
                        break;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ParkingReceiveServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        import Ashes.Text
        let onClient client =
            async(let recursive loop buffered =
                if Ashes.Text.length(buffered) >= 11
                then
                    match await Ashes.Net.Tcp.send(client)("got:" + buffered) with
                        | Error(e) -> Error(e)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client)
                else
                    match await Ashes.Net.Tcp.receive(client)(65536) with
                        | Error(e2) -> Error(e2)
                        | Ok(chunk) -> loop(buffered + chunk)
            in loop(""))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_serve_connections_concurrently()
    {
        // serve() spawns each handler (Ashes.Task.spawn), so a slow handler must not serialize
        // other connections. Concurrency is asserted from the handlers' own monotonic sleep
        // windows rather than client wall-clock time: all four connections are opened and their
        // payloads sent up front, each handler reports [sleepStart, sleepEnd] around a 500 ms
        // sleep, and the windows must pairwise overlap. Serialized handlers can never produce
        // overlapping windows, while load on the test box merely delays everything uniformly —
        // an absolute wall-clock bound here was flaky under a parallel suite run.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = ConcurrentConnectionsServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_conc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait for the listener, establish all four measured connections, then release their
            // payloads together. This keeps host scheduling delays while opening sockets from
            // consuming the server-side overlap window.
            _ = await ConnectSendReceiveWithRetryAsync(port, "warmup").ConfigureAwait(false);

            var clients = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    clients.Add(await ConnectAsync(port).ConfigureAwait(false));
                }

                await Task.WhenAll(clients.Select((client, index) =>
                    SendAsync(client, $"conc-{index}"))).ConfigureAwait(false);
                var replies = await Task.WhenAll(clients.Select(ReadWholeReplyAsync)).ConfigureAwait(false);
                var windows = replies.Select(ParseConcurrencyReply).ToList();

                windows.Max(w => w.SleepStart).ShouldBeLessThan(windows.Min(w => w.SleepEnd),
                    "handler sleep windows should overlap; serialized handlers can never overlap");
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
        return client;
    }

    private static async Task SendAsync(TcpClient client, string payload)
    {
        var outBytes = Encoding.UTF8.GetBytes(payload);
        await client.GetStream().WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
    }

    private static async Task<string> ReadWholeReplyAsync(TcpClient client)
    {
        var buffer = new byte[4096];
        int read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.AcceptTimeout).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static (long SleepStart, long SleepEnd) ParseConcurrencyReply(string reply, int index)
    {
        var parts = reply.Split(';');
        parts.Length.ShouldBe(3, $"reply '{reply}' should be 'echo: <payload>;<sleepStart>;<sleepEnd>'");
        parts[0].ShouldBe($"echo: conc-{index}");
        return (long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static string ConcurrentConnectionsServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        import Ashes.IO.Console
        import Ashes.Text
        let onClient client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    let sleepStart = Ashes.IO.Console.monotonicMillis()
                    in
                        match await Ashes.Task.sleep(500) with
                            | Error(e2) -> Error(e2)
                            | Ok(_t) ->
                                let sleepEnd = Ashes.IO.Console.monotonicMillis()
                                in
                                    match await Ashes.Net.Tcp.send(client)("echo: " + msg + ";" + Ashes.Text.fromInt(sleepStart) + ";" + Ashes.Text.fromInt(sleepEnd)) with
                                        | Error(e3) -> Error(e3)
                                        | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_run_a_tcp_echo_server_via_serve()
    {
        // Server-side coverage on native linux-x64: the Ashes program is the LISTENER
        // (Ashes.Net.Tcp.Server.serve), the C# test is the CLIENT connecting in. Exercises the
        // socket/bind/listen/accept4 syscalls and the accept-park on WaitSocketRead.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        int port = portLease.Port;
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Task
            let onClient client =
                async(match await Ashes.Net.Tcp.receive(client)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tcp.send(client)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            foreach (var payload in new[] { "linux-one", "linux-two", "linux-three" })
            {
                var reply = await ConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_spawned_task_server_handles_sustained_requests_without_fault_storm()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using LoopbackPortLease portLease = LoopbackPortLease.Create();
        string source = $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Task
            let onClient client =
                async(match await Ashes.Net.Tcp.receive(client)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tcp.send(client)(msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{portLease.Port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(
            LowerProgramWithImports(source),
            BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_lazy_footer_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);
            (await ConnectSendReceiveWithRetryAsync(portLease.Port, "warmup").ConfigureAwait(false)).ShouldBe("warmup");

            long faultsBefore = ReadMinorFaults(proc.Id);
            const int requestCount = 300;
            for (int request = 0; request < requestCount; request++)
            {
                (await ConnectSendReceiveWithRetryAsync(portLease.Port, "x").ConfigureAwait(false)).ShouldBe("x");
            }

            // Correctness above (every echo matched) is the primary guarantee; the fault ceiling is a
            // deliberately generous smoke bound, not a precise regression gate. The lazy-footer
            // optimization saves ~1 minor fault per handler, but that fault only appears when the far
            // footer page is non-resident and re-faults — in steady state the footer stays resident, so
            // fixed and regressed builds are indistinguishable by fault count. The extra faults surface
            // only under memory-pressure reclaim, and this parallel harness ([NotInParallel] is banned)
            // adds hundreds of unrelated reclaim faults that mask the signal (observed ~2.1 faults/req
            // here vs ~0.06 unloaded), so a tight threshold flakes on loaded/high-THP/high-core hosts.
            // The bound below only catches a gross regression (an allocator thrashing many pages/req); a
            // precise env-independent guard would instead inspect the spawn codegen for a footer store.
            long faultDelta = ReadMinorFaults(proc.Id) - faultsBefore;
            faultDelta.ShouldBeLessThan(requestCount * 8L,
                "a spawned handler must not thrash many pages per request");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    // Writes the compiled ELF image to exePath and starts it with redirected stdio.
    private static async Task<Process> StartServerProcessAsync(string exePath, string tmpDir, byte[] elfBytes)
    {
        TestProcessHelper.WriteExecutable(exePath, elfBytes);
        return await TestProcessHelper.StartProcessAsync(new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tmpDir,
        }).ConfigureAwait(false);
    }

    private static void CleanUpServerProcess(Process? proc, string exePath, string tmpDir)
    {
        if (proc is not null)
        {
            TryKillProcess(proc);
        }
        DeleteFileIfExists(exePath);
        DeleteDirectoryIfExists(tmpDir);
    }

    private static async Task SendSigtermAsync(int pid)
    {
        using (var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}") { UseShellExecute = false })!)
        {
            await kill.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    // Waits for the server process to exit on its own, then asserts a clean stop message.
    private static async Task AssertExitsCleanlyAsync(Process proc, string expectedOutput)
    {
        var exited = await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);
        exited.ShouldBeTrue();
        proc.ExitCode.ShouldBe(0);
        (await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).ShouldContain(expectedOutput);
    }

    internal static IrProgram LowerProgramWithImports(string source)
        => LowerProgramWithImportsAndLowering(source).Ir;

    // A program with imports lowered without reuse specializations, the general path stage 1 takes.
    private static IrProgram LowerProgramWithImportsWithoutReuse(string source)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        HashSet<string> importedStdModules = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);
        Diagnostics diagnostics = new();
        Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(
            diagnostics,
            importedStdModules,
            parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases,
            layout.ConstructorModules,
            new LoweringConfiguration(EnableReuse: false));
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    internal static (Lowering Lowering, IrProgram Ir) LowerProgramWithImportsAndLowering(string source)
    {
        var parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var importedStdModules = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new Diagnostics();
        var program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var lowering = new Lowering(diagnostics, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases);
        var ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return (lowering, ir);
    }

    private static async Task<string> ConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new IOException("The loopback server disconnected before sending a response.");
                    }

                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }

    // A standalone program whose types derive the standard traits, lowered as the CLI lowers it.
    private static IrProgram LowerProgramWithStandardTraits(string source)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            "<memory>");
        Diagnostics diagnostics = new();
        Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(
            diagnostics,
            new HashSet<string>(["Ashes.Trait"], StringComparer.Ordinal),
            parsed.ImportAliases,
            layout.ConstructorModules);
        lowering.SetSourceContext(layout);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    // A standalone program lowered with its source context, as the CLI lowers it: every emitted
    // instruction carries its source location.
    private static IrProgram LowerProgramWithSourceLocations(string source)
    {
        ParsedImportHeader parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        CombinedCompilationLayout layout = ProjectSupport.BuildStandaloneCompilationLayout(
            parsed.SourceWithoutImports,
            parsed.ImportNames,
            "<memory>");
        Diagnostics diagnostics = new();
        Program program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(
            diagnostics,
            new HashSet<string>(parsed.ImportNames, StringComparer.Ordinal),
            parsed.ImportAliases,
            layout.ConstructorModules);
        lowering.SetSourceContext(layout);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        string source,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithLinuxLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode, environmentVariables).ConfigureAwait(false);
    }

    internal static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        IrProgram ir,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        BackendCompileOptions? compileOptions = null)
    {
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir, compileOptions);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (workingDirectory is not null)
            {
                psi.WorkingDirectory = workingDirectory;
            }
            if (environmentVariables is not null)
            {
                foreach (var entry in environmentVariables)
                {
                    psi.Environment[entry.Key] = entry.Value;
                }
            }
            if (args is not null)
            {
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
            if (stdin is not null)
            {
                await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            proc.ExitCode.ShouldBe(expectedExitCode, $"stderr: {stderr}");
            return new ExecutionResult(stdout, stderr, proc.ExitCode);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<List<MemoryExecutionResult>> MeasureMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(sourceFactory(iterations));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
            samples.Add(sample);
        }

        return samples;
    }

    /// <summary>
    /// Peak resident set measured through a tiny wrapper. The shared Python-based measurement forks
    /// before exec, so the child's <c>ru_maxrss</c> inherits the interpreter's image and reports a
    /// floor around 13 MB — enough to hide a sub-megabyte per-iteration leak. Measuring through
    /// <c>/usr/bin/time</c> keeps the floor at the program's own quarter megabyte.
    /// </summary>
    private static async Task<MemoryExecutionResult> RunLinuxExecutableLowFloorPeakRssAsync(string exePath)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ProcessStartInfo startInfo = new("/usr/bin/time")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("__ASHES_RSS__=%M");
            startInfo.ArgumentList.Add(exePath);

            using Process process = await TestProcessHelper.StartProcessAsync(startInfo).ConfigureAwait(false);
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            bool transientTextFileBusy = process.ExitCode == 126
                && stderr.Contains("Text file busy", StringComparison.Ordinal);
            if (transientTextFileBusy && attempt < maxAttempts - 1)
            {
                await Task.Delay(20 * (attempt + 1)).ConfigureAwait(false);
                continue;
            }

            process.ExitCode.ShouldBe(0, $"stderr: {stderr}");

            const string marker = "__ASHES_RSS__=";
            string? usage = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
            usage.ShouldNotBeNull($"time wrapper did not report peak RSS. stderr: {stderr}");
            return new MemoryExecutionResult(stdout, long.Parse(usage[marker.Length..].Trim(), CultureInfo.InvariantCulture));
        }

        throw new InvalidOperationException("Unreachable process retry state.");
    }

    /// <summary>
    /// Growth measurement for workloads whose leak is small relative to the shared harness's
    /// measurement floor: the semantic optimizer runs first, as <c>CompileToImage</c> does, and the
    /// low-floor wrapper above reports the resident set.
    /// </summary>
    private static async Task<List<MemoryExecutionResult>> MeasureLowFloorMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = IrOptimizer.Optimize(LowerProgram(sourceFactory(iterations)));
            byte[] elfBytes = new LinuxX64LlvmBackend().Compile(ir);
            string tmpDir = CreateTempDirectory();
            string exePath = Path.Combine(tmpDir, $"llvm_lowfloor_{Guid.NewGuid():N}");
            try
            {
                TestProcessHelper.WriteExecutable(exePath, elfBytes);
                MemoryExecutionResult sample = await RunLinuxExecutableLowFloorPeakRssAsync(exePath).ConfigureAwait(false);
                sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
                samples.Add(sample);
            }
            finally
            {
                DeleteFileIfExists(exePath);
                DeleteDirectoryIfExists(tmpDir);
            }
        }

        return samples;
    }

    /// <summary>
    /// Measures growth over the IR that actually ships: the semantic optimizer runs first, exactly as
    /// <c>CompileToImage</c> does. The unoptimized measurement above cannot see a leak that only
    /// appears once ownership operations have been fused, sunk, or elided.
    /// </summary>
    private static async Task<List<MemoryExecutionResult>> MeasureOptimizedMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = IrOptimizer.Optimize(LowerProgram(sourceFactory(iterations)));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureImportedMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgramWithImports(sourceFactory(iterations));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcStringConcatMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcStringConcatMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 5) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingStringMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingStringProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcKnownFunctionStringMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcKnownFunctionStringProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            ir.EntryFunction.Instructions.Any(instruction => instruction is IrInst.CallClosure).ShouldBeTrue();
            ir.Functions.Any(function =>
                function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
                && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcKnownFunctionBytesAndBigIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            ir.Functions.Any(function =>
                function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true })
                && function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true })
                && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 133L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingBytesProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 19L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBytesAppendMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBytesAppendMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcAppendByteMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcAppendByteMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 3}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBytesFromListMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBytesFromListMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 3}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcByteSingletonMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcByteSingletonMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEmptyBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEmptyBytesMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcFixedWidthBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcFixedWidthBytesProgram(iterations));
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 14}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcByteSubTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcByteSubTextMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 4) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextFromIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextFromIntMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 4) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextToHexMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextToHexMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 7) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcAsciiCaseTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcAsciiCaseTextProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 12) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcFloatTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcFloatTextProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFormatFloat { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 13) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntFromIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntFromIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("0\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingBigIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingBigIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextParseIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextParseIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 123L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextParseFloatMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextParseFloatProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntToIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntToIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 123L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntParseResultMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntParseResultProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextUnconsMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextUnconsProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 6L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntArithmeticMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntArithmeticProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBe(5);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("0\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntTextProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 30}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcCopyClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcCopyClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = ((long)iterations * (iterations + 1) / 2) + iterations;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedHeapClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedHeapClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = iterations * 8L;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedBytesClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedBytesClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedBigIntClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedBigIntClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRegionManagedTaskFrameMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRegionManagedTaskFrameProgram(iterations));
            AllInstructions(ir).Any(instruction => instruction is IrInst.CreateTask).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.RestoreArenaState).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = (long)iterations * (iterations + 1) / 2;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static void AssertMemoryPlateaus(
        string workload,
        IReadOnlyList<MemoryExecutionResult> samples,
        long maxRssKb = 64_000,
        long growthBudgetKb = 8_192)
    {
        AssertMemorySamplesBelowCeiling(workload, samples, maxRssKb);

        string sampleSummary = string.Join(", ", samples.Select(sample => $"{sample.MaxRssKb} KB"));
        long totalGrowthKb = Math.Max(0, samples[2].MaxRssKb - samples[0].MaxRssKb);
        long lateGrowthKb = Math.Max(0, samples[2].MaxRssKb - samples[1].MaxRssKb);
        totalGrowthKb.ShouldBeLessThan(growthBudgetKb,
            $"{workload} RSS grew {totalGrowthKb} KB from first to last sample; samples: {sampleSummary}");
        lateGrowthKb.ShouldBeLessThan(growthBudgetKb,
            $"{workload} RSS grew {lateGrowthKb} KB from middle to last sample; samples: {sampleSummary}");
    }

    private static void AssertMemorySamplesBelowCeiling(
        string workload,
        IReadOnlyList<MemoryExecutionResult> samples,
        long maxRssKb = 64_000)
    {
        samples.Count.ShouldBe(3);
        string sampleSummary = string.Join(", ", samples.Select(sample => $"{sample.MaxRssKb} KB"));
        foreach (MemoryExecutionResult sample in samples)
        {
            sample.MaxRssKb.ShouldBeGreaterThan(0);
            sample.MaxRssKb.ShouldBeLessThan(maxRssKb,
                $"{workload} peaked at {sample.MaxRssKb} KB; samples: {sampleSummary}");
        }
    }

    private static void AssertRuntimeRcTupleTcoProbe()
    {
        IrProgram probe = LowerProgram(BuildRuntimeRcTupleTcoMemoryProgram(1));
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 16,
                RuntimeManaged: true
            }).ShouldBeGreaterThanOrEqualTo(2,
                "The annotated tuple TCO accumulator should normalize at loop entry and replacement.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "Tuple",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Tuple replacement must release its old RC cell at the back-edge.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 16,
                RuntimeManaged: false
            }).ShouldBeFalse(
                "The migrated tuple TCO path must not relocate through an arena allocation.");
    }

    private static void AssertRuntimeRcOwnedHeadListTcoProbe(
        IrProgram probe,
        IrInst.ListHeadCopyKind headCopy,
        string elementDescription)
    {
        AllInstructions(probe).Any(instruction => instruction is IrInst.CopyOutList
        {
            HeadCopy: var actualHeadCopy,
            RuntimeManaged: true,
        } && actualHeadCopy == headCopy).ShouldBeTrue(
            $"The {elementDescription} list graph should normalize under runtime RC.");
        AllInstructions(probe).Any(instruction => instruction is IrInst.CopyOutTcoListCell
            or IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                $"The migrated {elementDescription} list must not use arena relocation.");
        AllInstructions(probe).Any(instruction => instruction is IrInst.RcDrop
        {
            TypeName: "List",
            RuntimeManaged: true,
        }).ShouldBeTrue(
            $"The migrated {elementDescription} list must release its owned graph.");
    }

    private static void AssertRuntimeRcOwnedHeadListTcoProbes()
    {
        AssertRuntimeRcOwnedHeadListTcoProbe(
            LowerProgram(BuildRuntimeRcStringHeadListTcoMemoryProgram(1)),
            IrInst.ListHeadCopyKind.String,
            "String");
        AssertRuntimeRcOwnedHeadListTcoProbe(
            LowerProgram(BuildRuntimeRcNestedListTcoMemoryProgram(1)),
            IrInst.ListHeadCopyKind.InnerList,
            "nested-list");
        IrProgram transferProbe = LowerProgram(BuildRuntimeRcListPatternTransferMemoryProgram(1));
        AllInstructions(transferProbe).Any(instruction => instruction is IrInst.RcDup
        {
            RuntimeManaged: true,
        }).ShouldBeTrue();
        AssertRuntimeRcGeneralHeadListTcoProbe(
            LowerProgram(BuildRuntimeRcTupleHeadListTcoMemoryProgram(1)),
            "Tuple");
        AssertRuntimeRcGeneralHeadListTcoProbe(
            LowerProgram(BuildRuntimeRcRecordHeadListTcoMemoryProgram(1)),
            "Item");
    }

    private static void AssertRuntimeRcGeneralHeadListTcoProbe(IrProgram probe, string childTypeName)
    {
        AllInstructions(probe).Any(instruction => instruction is IrInst.Label label
            && label.Name.Contains("rc_normalize_list", StringComparison.Ordinal)).ShouldBeTrue();
        AllInstructions(probe).Any(instruction => instruction is IrInst.RcDrop
        {
            TypeName: var typeName,
            RuntimeManaged: true,
        } && string.Equals(typeName, childTypeName, StringComparison.Ordinal)).ShouldBeTrue();
    }

    private static void AssertRuntimeRcStringTcoProbe()
    {
        IrProgram probe = LowerProgram(BuildLegacyArenaGrowingStringMemoryProgram(1));
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: true }).ShouldBe(1,
                "The affine TCO String accumulator should normalize once at loop entry.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.ConcatStrTip { RuntimeManaged: true }).ShouldBeTrue(
                "The replacement should consume the RC accumulator directly instead of copying it.");
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBe(1,
                "ConcatStrTip consumes replacements internally; only function exit needs an IR drop.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", OwnerSlot: -1, RuntimeManaged: true }).ShouldBeTrue(
                "The function-exit drop must remain fixed instead of being moved by lifetime placement.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated String TCO path must not relocate through an arena allocation.");
        RepresentationTestIr.CountOutsideGuards(AllInstructions(probe), instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(0,
                "An affine TCO accumulator should move into its replacement without an extra reference.");
    }

    private static void AssertRuntimeRcCopyAdtTcoProbes()
    {
        AssertRuntimeRcCopyAdtTcoProbe(
            LowerProgram(BuildRuntimeRcCopyAdtTcoMemoryProgram(1)),
            "Pair");
        AssertRuntimeRcCopyAdtTcoProbe(
            LowerProgram(BuildRuntimeRcCopyRecordTcoProbe()),
            "Point");
    }

    private static void AssertRuntimeRcOwnedTupleTcoProbe()
    {
        IrProgram probe = LowerProgram(BuildRuntimeRcOwnedTupleTcoMemoryProgram(1));
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.Alloc
            {
                SizeBytes: 24,
                RuntimeManaged: true
            }).ShouldBeGreaterThanOrEqualTo(2,
                "The pointer-bearing tuple parent should normalize at entry and replacement.");
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(2,
                "The tuple's complete list child should normalize with its parent.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "Tuple",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Tuple replacement must recursively release its old owned-child graph.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: false }
                or IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated pointer-bearing tuple TCO path must not use arena relocation.");
    }

    private static void AssertRuntimeRcOwnedRecordTcoProbe()
    {
        IrProgram probe = LowerProgram(BuildRuntimeRcOwnedRecordTcoMemoryProgram(1));
        // The two-field record has one constructor, so its cell is tagless: two payload words. As a
        // record of the ownership contract it is copied by its synthesized normalizer, called at
        // each normalization site.
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 16,
                RuntimeManaged: true
            }
            || (instruction is IrInst.CallKnown call && call.FuncLabel.StartsWith("__rcnorm_", StringComparison.Ordinal))).ShouldBeGreaterThanOrEqualTo(2,
                "The pointer-bearing record parent should normalize at entry and replacement.");
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(1,
                "The record's complete list child should normalize with its parent, inside its normalizer.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "Box",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Record replacement must recursively release its old owned-child graph.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: false }
                or IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated pointer-bearing record TCO path must not use arena relocation.");
    }

    private static void AssertRuntimeRcOwnedVariantTcoProbe()
    {
        IrProgram probe = LowerProgram(BuildRuntimeRcOwnedVariantTcoMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 8,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Tag-dispatched normalization should use the nullary variant's actual size.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 24,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Tag-dispatched normalization should use the owned-child variant's actual size.");
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutList { RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(1,
                "The selected Full variant's complete list child should normalize with its replacement parent.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: "State",
                OwnerSlot: -1,
                RuntimeManaged: true
            }).ShouldBeTrue(
                "Variant replacement must recursively release its old owned-child graph.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena { RuntimeManaged: false }
                or IrInst.CopyOutList { RuntimeManaged: false }).ShouldBeFalse(
                "The migrated pointer-bearing variant TCO path must not use arena staging.");
    }

    private static void AssertRuntimeRcCopyAdtTcoProbe(IrProgram probe, string typeName)
    {
        // Both probed types have one two-field constructor, so their cells are tagless: two
        // payload words, no tag word.
        AllInstructions(probe).Count(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 16,
                RuntimeManaged: true
            }).ShouldBeGreaterThanOrEqualTo(2,
                $"The annotated {typeName} TCO accumulator should normalize at entry and replacement.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop
            {
                TypeName: var droppedType,
                OwnerSlot: -1,
                RuntimeManaged: true
            } && string.Equals(droppedType, typeName, StringComparison.Ordinal)).ShouldBeTrue(
                $"{typeName} replacement must release its old RC cell at the back-edge.");
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.CopyOutArena
            {
                StaticSizeBytes: 16,
                RuntimeManaged: false
            }).ShouldBeFalse(
                $"The migrated {typeName} TCO path must not relocate through an arena allocation.");
    }

    private static string BuildRuntimeRcListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = [40, 2] in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = (let fresh = [40, 2] in fresh) in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTupleMemoryProgram(int iterations)
        => $$"""
            type Pair =
                | Pair(Int, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let pair =
                        let fresh = ((40, 2), Ashes.Text.fromInt(40), Ashes.Byte.u16Le(258u16), Ashes.Number.BigInt.fromInt(42), [40, 2], Pair(40)(2)) in fresh
                    in
                    match pair with
                        | ((left, right), text, bytes, big, values, Pair(pairLeft, pairRight)) ->
                            match values with
                                | [] -> loop(n - 1)(total)
                                | head :: _ ->
                                    loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + pairLeft + pairRight)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedElementListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = [Ashes.Text.fromInt(2)] in
                    let escaped = Ashes.Text.fromInt(40) :: tail in
                    match escaped with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + Ashes.Text.byteLength(head) + Ashes.Text.byteLength(tailHead))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildTcoExitAdtConstructorMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.List

            type Pr(a, b) =
                | Pr(a, b)

            let recursive walk n xs ys =
                if n <= 0 then Pr(xs, ys) else walk(n - 1)(n :: xs)(n :: ys)

            let recursive loop count total =
                if count <= 0 then total
                else
                    match walk(50)([])([]) with
                        | Pr(xs, ys) ->
                            match xs with
                                | [] -> loop(count - 1)(total)
                                | xsHead :: _ ->
                                    match ys with
                                        | [] -> loop(count - 1)(total)
                                        | ysHead :: _ -> loop(count - 1)(total + xsHead + ysHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildFindLoopStringHeadMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.List

            let recursive build (n: Int) (acc: List(Str)) =
                if n == 0 then acc else build(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive findLong (items: List(Str)) (limit: Int) =
                match items with
                    | [] -> "none"
                    | head :: rest ->
                        if Ashes.Text.byteLength(head) > limit then head else findLong(rest)(limit)

            let recursive loop count total =
                if count <= 0 then total
                else
                    match findLong(build(12)([]))(1) with
                        | found -> loop(count - 1)(total + Ashes.Text.byteLength(found))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildFindLoopRecordHeadMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.List

            type Item =
                | name: Str
                | weight: Int

            let recursive build (n: Int) (acc: List(Item)) =
                if n == 0 then acc else build(n - 1)(Item(name = Ashes.Text.fromInt(n), weight = n) :: acc)

            let recursive findHeavy (items: List(Item)) (limit: Int) =
                match items with
                    | [] -> Item(name = "none", weight = 0)
                    | head :: rest ->
                        if head.weight > limit then head else findHeavy(rest)(limit)

            let recursive loop count total =
                if count <= 0 then total
                else
                    match findHeavy(build(12)([]))(6) with
                        | found -> loop(count - 1)(total + found.weight + Ashes.Text.byteLength(found.name))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildGenericMapRecordResultMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.List as list

            type Item =
                | name: Str
                | flag: Bool

            let toEntry (n: Str) = Item(name = n, flag = true)

            let recursive loop i acc =
                if i == 0
                then acc
                else
                    let entries = list.append(list.map(toEntry)([Ashes.Text.fromInt(i)]))(list.map(toEntry)(["Testing"]))
                    in loop(i - 1)(acc + list.length(entries))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildGenericMapStringResultMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.List as list

            let bang (n: Str) = n + "!"

            let recursive loop i acc =
                if i == 0
                then acc
                else
                    let entries = list.append(list.map(bang)([Ashes.Text.fromInt(i)]))(list.map(bang)(["Testing"]))
                    in loop(i - 1)(acc + list.length(entries))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // The rebuilt list reaches the tail call through a let binding rather than directly. Thirty
    // elements make the previous list, leaked once per iteration, exceed the plateau budget well
    // before the largest sample.
    private static string BuildLetBoundTailCallArgumentMemoryProgram(int iterations)
        => $$"""
            let recursive iota i n =
                if i > n
                then []
                else i :: iota(i + 1)(n)

            let recursive setAt i v xs =
                match xs with
                    | [] -> []
                    | h :: t ->
                        if i == 0
                        then v :: t
                        else h :: setAt(i - 1)(v)(t)

            let recursive loop i acc xs =
                if i == 0
                then acc
                else
                    let updated = setAt(i % 30)(i)(xs)
                    in loop(i - 1)(acc + 1)(updated)

            Ashes.IO.print(loop({{iterations}})(0)(iota(1)(30)))
            """;

    // A generic list producer whose tail-modulo-constructor cons the element type unblocks, called
    // where that element type is fixed, with its own result threaded on as the next call's argument.
    // The loop reads the threaded list on its exit arm, which is what keeps that list
    // runtime-managed and so makes the call hand a reference over for the result to keep.
    private static string BuildElementSpecializedProducerProgram()
        => """
            let recursive setAt i v xs =
                match xs with
                    | [] -> []
                    | h :: t ->
                        if i == 0
                        then v :: t
                        else h :: setAt(i - 1)(v)(t)

            let recursive run rounds xs =
                if rounds == 0
                then
                    match xs with
                        | [] -> 0
                        | h :: _ -> h
                else run(rounds - 1)(setAt(rounds % 3)(rounds)(xs))

            match Ashes.IO.args with
                | arg :: _ ->
                    match Ashes.Text.parseInt(arg) with
                        | Ok(rounds) -> Ashes.IO.print(Ashes.Text.fromInt(run(rounds)([1, 2, 3])))
                        | Error(_) -> Ashes.IO.print("bad argument")
                | [] -> Ashes.IO.print("missing argument")
            """;

    private static string BuildParallelWorkerMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let shared = 20 :: 22 :: [] in
                    match Ashes.Task.Parallel.both(given (_u) -> 1 :: shared)(given (_u) -> 2 :: shared) with
                        | (left, right) ->
                            match left with
                                | [] -> loop(n - 1)(total)
                                | leftHead :: _ ->
                                    match right with
                                        | [] -> loop(n - 1)(total)
                                        | rightHead :: _ -> loop(n - 1)(total + leftHead + rightHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAdtMemoryProgram(int iterations)
        => $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            type Pair =
                | Pair(Int, Int)

            type Box(a) =
                | Box(a)

            type TextBox =
                | TextBox(Str)

            type Payload =
                | Payload(Bytes, BigInt, List(Int))

            type Wrapped =
                | Wrapped((Int, Int))

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let pair = Pair(40)(2) in pair
                    in match escaped with
                        | Pair(left, right) ->
                            let textBox =
                                let fresh = TextBox(Ashes.Text.fromInt(40)) in fresh
                            in match textBox with
                                | TextBox(text) ->
                                    let payload =
                                        let fresh = Payload(Ashes.Byte.u16Le(258u16))(Ashes.Number.BigInt.fromInt(42))([40, 2]) in fresh
                                    in match payload with
                                        | Payload(bytes, big, values) ->
                                            match values with
                                                | [] -> loop(n - 1)(total)
                                                | head :: _ ->
                                                    let wrapped =
                                                        let fresh = Wrapped((40, 2)) in fresh
                                                    in match wrapped with
                                                        | Wrapped((wrappedLeft, wrappedRight)) ->
                                                            let boxed =
                                                                let fresh = Box(5) in fresh
                                                            in match boxed with
                                                                | Box(boxedValue) ->
                                                                    let tree =
                                                                        let fresh = Node(Node(Leaf)(20)(Leaf))(42)(Leaf) in fresh
                                                                    in
                                                                    match tree with
                                                                        | Leaf -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue)
                                                                        | Node(child, value, _) ->
                                                                            match child with
                                                                                | Leaf -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue + value)
                                                                                | Node(_, childValue, _) -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue + value + childValue)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAdtReuseMemoryProgram(int iterations)
        => $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let tree = Node(Node(Leaf)(2)(Leaf))(1)(Leaf) in
                    match tree with
                        | Leaf ->
                            let rebuilt = Leaf in
                            loop(n - 1)(total)
                        | Node(left, value, _) ->
                            let rebuilt = Node(left)(value + 1)(Leaf) in
                            loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcRecordOwnedChildMemoryProgram(int iterations)
        => $$"""
            type TextBox =
                | value: Str

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let box = TextBox(value = Ashes.Text.fromInt(42)) in box
                    in loop(n - 1)(total + Ashes.Text.byteLength(escaped.value))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcVariantOwnedChildMemoryProgram(int iterations)
        => $$"""
            type Choice =
                | Empty
                | Text(Str)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let choice = Text(Ashes.Text.fromInt(42)) in choice
                    in match escaped with
                        | Empty -> loop(n - 1)(total)
                        | Text(value) -> loop(n - 1)(total + Ashes.Text.byteLength(value))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcGenericOwnedChildMemoryProgram(int iterations)
        => $$"""
            type Box(a) =
                | Box(a)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let textBox =
                        let box = Box(Ashes.Text.fromInt(42)) in box
                    in match textBox with
                        | Box(text) ->
                            let listBox =
                                let box = Box([40, 2]) in box
                            in match listBox with
                                | Box(values) ->
                                    match values with
                                        | [] -> loop(n - 1)(total)
                                        | head :: _ ->
                                            let tupleBox =
                                                let box = Box((40, 2)) in box
                                            in match tupleBox with
                                                | Box((left, right)) -> loop(n - 1)(total + Ashes.Text.byteLength(text) + head + left + right)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A coroutine whose result is a freshly built string. The value is handed to the awaiter through
    // the task's result slot, which frame teardown deliberately does not touch, so it needs an owner
    // on the consuming side once in-coroutine values move to reference counting.
    private static string BuildAsyncCoroutineOwnedStringMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let once n =
                async(
                    let made = build(n)
                    in
                        match await Ashes.Task.sleep(0) with
                            | Ok(_u) -> "A" + made + "!"
                            | Error(_e) -> "e")

            let recursive loop n total =
                if n <= 0 then total
                else
                    match Ashes.Task.run(once(7)) with
                        | Ok(v) -> loop(n - 1)(total + Ashes.Text.byteLength(v))
                        | Error(_e) -> 0 - 1

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A non-suspending async body that binds its value before returning it. The escaping-result
    // representation decision has to respect the coroutine placement context here too, or the bound
    // value becomes reference counted inside a region-backed body and nothing releases it.
    private static string BuildAsyncNoSuspendBoundResultMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let once n =
                async(
                    let made = build(n)
                    in "A" + made + "!")

            let recursive loop n total =
                if n <= 0 then total
                else
                    match Ashes.Task.run(once(7)) with
                        | Ok(v) -> loop(n - 1)(total + Ashes.Text.byteLength(v))
                        | Error(_e) -> 0 - 1

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // An async block that completes without suspending. Its body is a coroutine body, so it must be
    // lowered as one: otherwise a call inside it takes the runtime-RC result path and produces a
    // counted allocation per task that the enclosing region reset cannot reclaim and no owner
    // releases.
    private static string BuildAsyncNoSuspendResultMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let once n = async(build(n))

            let recursive loop n total =
                if n <= 0 then total
                else
                    match Ashes.Task.run(once(7)) with
                        | Ok(v) -> loop(n - 1)(total + Ashes.Text.byteLength(v))
                        | Error(_e) -> 0 - 1

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A race loser holding an owned value, cancelled while parked. Nothing but the cancellation path
    // can release it: the loser never resumes, so its body's own drops never run and its frame words
    // are still set when the race resolves.
    private static string BuildAsyncRaceLoserOwnedStringMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let owner delayMs =
                async(
                    let text = build(7)
                    in
                        match await Ashes.Task.sleep(delayMs) with
                            | Ok(_u) -> Ashes.Text.byteLength(text) - 5
                            | Error(_e) -> 0 - 1)

            let recursive loop n total =
                if n <= 0 then total
                else
                    match Ashes.Task.run(Ashes.Task.race([owner(0), owner(60)])) with
                        | Ok(v) -> loop(n - 1)(total + v)
                        | Error(_e) -> 0 - 1

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A value the creating function owns and the async block captures. The task frame holds its own
    // reference, so the generated frame dropper is the only thing that releases it.
    private static string BuildAsyncFrameCapturedStringMemoryProgram(int iterations)
        => $$"""
            let build n = Ashes.Text.fromInt(n) + "-tail"

            let once n =
                (let text = build(n)
                in
                    async(match await Ashes.Task.sleep(0) with
                        | Ok(_u) -> text + "!"
                        | Error(_e) -> "e"))

            let recursive loop n total =
                if n <= 0 then total
                else
                    match Ashes.Task.run(once(7)) with
                        | Ok(v) -> loop(n - 1)(total + Ashes.Text.byteLength(v))
                        | Error(_e) -> 0 - 1

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // A runtime-managed string loop parameter whose successor joins a literal with a fresh append of
    // the parameter to itself: the join must be uniformly reference-counted, or the back edge's copy
    // out of the arena join orphans the append.
    private static string BuildRuntimeRcStringJoinSuccessorMemoryProgram(int iterations)
        => $$"""
            let recursive loop (n: Int) (text: Str) (total: Int) =
                if n <= 0 then total
                else
                    loop(n - 1)(if Ashes.Text.byteLength(text) >= 512 then "ab" else text + text)(total + 1)

            Ashes.IO.print(loop({{iterations}})("ab")(0))
            """;

    // A runtime-managed string loop parameter read twice by a byte slice of its own doubling, the
    // fresh doubled string handed to the builtin and left behind by the slice.
    private static string BuildRuntimeRcStringReadTwiceMemoryProgram(int iterations)
        => $$"""
            let recursive loop (n: Int) (text: Str) (total: Int) =
                if n <= 0 then total
                else loop(n - 1)(Ashes.Byte.subText(Ashes.Byte.fromText(text + text))(1)(64))(total + 1)

            Ashes.IO.print(loop({{iterations}})("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")(0))
            """;

    // The same doubling and slice behind a helper whose literal arms join the fresh slice, called
    // with the fresh doubled string as its argument: the helper's result reaches nothing, so the
    // argument is released after the call whether the helper is inlined or not.
    private static string BuildRuntimeRcStringFreshArgumentHelperMemoryProgram(int iterations)
        => $$"""
            let cut (text: Str) (count: Int) =
                if count <= 0
                then ""
                else Ashes.Byte.subText(Ashes.Byte.fromText(text))(1)(count)

            let recursive loop (n: Int) (text: Str) (total: Int) =
                if n <= 0 then total
                else loop(n - 1)(cut(text + text)(64))(total + 1)

            Ashes.IO.print(loop({{iterations}})("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")(0))
            """;

    private static string BuildRuntimeRcHigherOrderResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> Str) -> Str = given f -> f(0)
            let make unit = let text = "ab" + "cd" in text
            let literal unit = "wxyz"

            let recursive loop n total =
                if n <= 0 then total
                else
                    let first = apply(make) in
                    let second = apply(literal) in
                    loop(n - 1)(total + Ashes.Text.byteLength(first) + Ashes.Text.byteLength(second))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(Int)) -> List(Int) = given f -> f(0)
            let source = [40, 2]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcNormalizedListAdtResultMemoryProgram(int iterations)
        => $$"""
            type Step =
                | Done
                | Hit(List(Int))

            let recursive setAt i v xs =
                match xs with
                    | [] -> []
                    | h :: t ->
                        if i == 0
                        then v :: t
                        else h :: setAt(i - 1)(v)(t)

            let build unit =
                let updated = setAt(1)(42)([10, 20, 30])
                in Hit(updated)

            let recursive loop n total =
                if n <= 0 then total
                else
                    match build(0) with
                        | Done -> loop(n - 1)(total)
                        | Hit(values) ->
                            match values with
                                | [] -> loop(n - 1)(total)
                                | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcKnownArenaListCallMemoryProgram(int iterations)
        => $$"""
            let identity xs = xs

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = identity([40, 2])
                    in match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBorrowedListScopeMemoryProgram(int iterations)
        => $$"""
            let choose : List(Int) -> List(Int) = given source ->
                let marker = "owned" in source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = choose([40, 2])
                    in match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTransferredListPayloadMemoryProgram(int iterations)
        => $$"""
            type Box =
                | First(List(Int))
                | Second(List(Int))

            let recursive loop n total =
                if n <= 0 then total
                else
                    let box = (let inner = First([40, 2]) in inner) in
                    let values = match box with | First(items) -> items | Second(items) -> items in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderStringListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(Str)) -> List(Str) = given f -> f(0)
            let source = ["forty", "two"]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + Ashes.Text.byteLength(head))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderNestedListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(List(Int))) -> List(List(Int)) = given f -> f(0)
            let source = [[40, 2]]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match head with
                                | [] -> loop(n - 1)(total)
                                | value :: _ -> loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingClosureMemoryProgram(int iterations)
        => $$"""
            let make n =
                let text = "value-" + "x"
                in given (ignored) -> Ashes.Text.byteLength(text)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingListCaptureClosureMemoryProgram(int iterations)
        => $$"""
            let make n =
                let values = [n, 2]
                in given (ignored) -> match values with
                    | [] -> 0
                    | _ :: _ -> 2

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram(int iterations)
        => $$"""
            type Box =
                | Box(List(Int))

            let make n =
                let graph = (Box([n, 2]), n)
                in given (ignored) -> match graph with
                    | (Box(_), _) -> 2

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcNestedRecordReuseMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let node =
                        let fresh = Node(child = Leaf(value = 40), bonus = 2) in fresh
                    in
                    match node with
                        | Node(child, bonus) ->
                            let rebuilt = Node(child = child, bonus = bonus + 1) in
                            loop(n - 1)(total + bonus + 40)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcPointerVariantReuseMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let choice = Full(Leaf(value = 40))(2) in
                    match choice with
                        | Empty ->
                            let rebuilt = Empty in
                            loop(n - 1)(total)
                        | Full(child, bonus) ->
                            let rebuilt = Full(child)(bonus + 1) in
                            loop(n - 1)(total + bonus + 40)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcSharedRecordChildMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let leaf = Leaf(value = 40) in
                    let node = Node(child = leaf, bonus = 2) in
                    loop(n - 1)(total + node.bonus + leaf.value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcStringConcatMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let text = "ab" + "cd" in
                Ashes.IO.print(text)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildTcoPromotedStringConcatProgram()
        => """
            import Ashes.IO as io
            import Ashes.Text as text
            import Ashes.Rune as rune

            let complement c =
                match c with
                    | 'A' -> 'T'
                    | 'C' -> 'G'
                    | other -> other

            let recursive compLine line acc =
                match text.uncons(line) with
                    | None -> acc
                    | Some((c, rest)) -> compLine(rest)(complement(c) :: acc)

            let recursive emit chars col buf =
                match chars with
                    | [] -> if col == 0 then Unit else io.write(buf + "\n")
                    | c :: rest ->
                        if col == 60
                        then
                            let ignored = io.write(buf + "\n")
                            in emit(rest)(1)(rune.toText(c))
                        else emit(rest)(col + 1)(buf + rune.toText(c))

            let recursive loop revcomp =
                match io.readLine(Unit) with
                    | None -> emit(revcomp)(0)("")
                    | Some(line) ->
                        match text.uncons(line) with
                            | Some(('>', _)) ->
                                let ignored = emit(revcomp)(0)("")
                                in
                                    let ignored = io.writeLine(line)
                                    in loop([])
                            | _ -> loop(compLine(line)(revcomp))

            loop([])
            """;

    private static string BuildRuntimeRcEscapingStringProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let text = "ab" + "cd" in text
                    in let length = Ashes.Text.byteLength(escaped)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcKnownFunctionStringProgram(int iterations)
        => $$"""
            let make : Str -> Str -> Str = given (left) -> given (right) ->
                let ignored = Ashes.Text.byteLength(left) in
                let text = "ab" + "cd" in text

            let alias = make

            let recursive loop n total =
                if n <= 0 then total
                else
                    let value = alias("left")("right") in
                    let length = Ashes.Text.byteLength(value) in
                    loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(int iterations)
        => $$"""
            type Pair =
                | Pair(Int, Int)

            type Point =
                | x: Int
                | y: Int

            let makeBytes unit =
                let bytes = Ashes.Byte.u64Le(72623859790382856u64) in bytes

            let makeBigInt number =
                let big = Ashes.Number.BigInt.fromInt(number) in big

            let makeList unit =
                let values = [40, 2] in values

            let makePair unit =
                let pair = Pair(40)(2) in pair

            let makePoint unit =
                let point = Point(x = 40, y = 2) in point

            let recursive loop n total =
                if n <= 0 then total
                else
                    let bytes = makeBytes(0) in
                    let byteCount = Ashes.Byte.length(bytes) in
                    let big = makeBigInt(n) in
                    let comparison = Ashes.Number.BigInt.compare(big)(big) in
                    let values = makeList(0) in
                    let pair = makePair(0) in
                    let point = makePoint(0) in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match pair with
                                | Pair(left, right) ->
                                    loop(n - 1)(total + byteCount + comparison + 1 + head + left + right + point.x + point.y)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingBytesProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let one =
                        let escaped =
                            let bytes = Ashes.Byte.singleton(7u8) in bytes
                        in Ashes.Byte.length(escaped)
                    in let eight =
                        let escaped =
                            let bytes = Ashes.Byte.u64Le(72623859790382856u64) in bytes
                        in Ashes.Byte.length(escaped)
                    in let four =
                        let escaped =
                            let bytes = Ashes.Byte.append(Ashes.Byte.fromText("ab"))(Ashes.Byte.fromText("cd")) in bytes
                        in Ashes.Byte.length(escaped)
                    in let three =
                        let escaped =
                            let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText("ab"))(33u8) in bytes
                        in Ashes.Byte.length(escaped)
                    in let threeFromList =
                        let escaped =
                            let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in bytes
                        in Ashes.Byte.length(escaped)
                    in loop(n - 1)(total + one + eight + four + three + threeFromList)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBytesAppendMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.append(Ashes.Byte.fromText("ab"))(Ashes.Byte.fromText("cd")) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAppendByteMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText("ab"))(33u8) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBytesFromListMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcByteSingletonMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.singleton(7u8) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEmptyBytesMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.empty(Unit) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcFixedWidthBytesProgram(int iterations)
        => $$"""
            let measure16 unit =
                let bytes = Ashes.Byte.u16Le(258u16) in
                Ashes.Byte.length(bytes)

            let measure32 unit =
                let bytes = Ashes.Byte.u32Le(16909060u32) in
                Ashes.Byte.length(bytes)

            let measure64 unit =
                let bytes = Ashes.Byte.u64Le(72623859790382856u64) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + measure16(0) + measure32(0) + measure64(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcByteSubTextMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Byte.subText(Ashes.Byte.fromText("abcdef"))(1)(3) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcTextFromIntMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Text.fromInt(-42) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcTextToHexMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Text.toHex(48879) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcAsciiCaseTextProgram(int iterations)
        => $$"""
            let emitUpper unit =
                let escaped =
                    let text = Ashes.Text.asciiUpper("hello") in text
                in Ashes.IO.print(escaped)

            let emitLower unit =
                let escaped =
                    let text = Ashes.Text.asciiLower("HELLO") in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignoredUpper = emitUpper(0) in
                    let ignoredLower = emitLower(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcFloatTextProgram(int iterations)
        => $$"""
            let emitFloat unit =
                let escaped =
                    let text = Ashes.Text.fromFloat(12.25) in text
                in Ashes.IO.print(escaped)

            let emitFixed unit =
                let escaped =
                    let text = Ashes.Text.formatFloat(12.25)(3) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignoredFloat = emitFloat(0) in
                    let ignoredFixed = emitFixed(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcBigIntFromIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let comparison =
                        let value = Ashes.Number.BigInt.fromInt(n) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in loop(n - 1)(total + comparison)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingBigIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let value = Ashes.Number.BigInt.fromInt(n) in value
                    in let comparison = Ashes.Number.BigInt.compare(escaped)(escaped)
                    in loop(n - 1)(total + comparison + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntArithmeticProgram(int iterations)
        => $$"""
            let left = 123456789012345678901234567890N
            let right = 987654321N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let addComparison =
                        let escaped =
                            let value = Ashes.Number.BigInt.add(left)(right) in value
                        in Ashes.Number.BigInt.compare(escaped)(escaped)
                    in let subComparison =
                        let value = Ashes.Number.BigInt.sub(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let mulComparison =
                        let value = Ashes.Number.BigInt.mul(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let divComparison =
                        let value = Ashes.Number.BigInt.div(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let modComparison =
                        let value = Ashes.Number.BigInt.mod(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in loop(n - 1)(total + addComparison + subComparison + mulComparison + divComparison + modComparison)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntTextProgram(int iterations)
        => $$"""
            let value = 123456789012345678901234567890N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let escaped =
                            let text = Ashes.Text.fromBigInt(value) in text
                        in Ashes.Text.byteLength(escaped)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextParseIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value =
                        let escaped =
                            let parsed = Ashes.Text.parseInt("123") in parsed
                        in match escaped with
                            | Ok(number) -> number
                            | Error(_message) -> 0
                    in loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextParseFloatProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value =
                        let escaped =
                            let parsed = Ashes.Text.parseFloat("1.5") in parsed
                        in match escaped with
                            | Ok(number) -> if number == 1.5 then 1 else 0
                            | Error(_message) -> 0
                    in loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntToIntProgram(int iterations)
        => $$"""
            let value = 123N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let converted =
                        let escaped =
                            let result = Ashes.Number.BigInt.toInt(value) in result
                        in match escaped with
                            | Ok(number) -> number
                            | Error(_message) -> 0
                    in loop(n - 1)(total + converted)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntParseResultProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let valid =
                        let escaped =
                            let result = Ashes.Text.parseBigInt("123") in result
                        in match escaped with
                            | Ok(value) -> Ashes.Number.BigInt.compare(value)(value)
                            | Error(_message) -> 1
                    in let invalid =
                        let escaped =
                            let result = Ashes.Text.parseBigInt("bad") in result
                        in match escaped with
                            | Ok(value) -> Ashes.Number.BigInt.compare(value)(value)
                            | Error(_message) -> 1
                    in loop(n - 1)(total + valid + invalid)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextUnconsProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let nonEmpty =
                        let split = (let escaped = Ashes.Text.uncons("abcdef") in escaped) in
                        match split with
                            | None -> 0
                            | Some((head, tail)) -> Ashes.Text.byteLength(Ashes.Rune.toText(head)) + Ashes.Text.byteLength(tail)
                    in let empty =
                        let split = (let escaped = Ashes.Text.uncons("") in escaped) in
                        match split with
                            | None -> 0
                            | Some((head, tail)) -> Ashes.Text.byteLength(Ashes.Rune.toText(head)) + Ashes.Text.byteLength(tail)
                    in loop(n - 1)(total + nonEmpty + empty)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcCopyClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let result =
                        let f =
                            if n > 0
                            then given (x) -> x + n
                            else given (x) -> x + n
                        in f(1)
                    in loop(n - 1)(total + result)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedHeapClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let text = "value-" + Ashes.Text.fromInt(42) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Text.byteLength(text)
                            else given (unit) -> Ashes.Text.byteLength(text)
                        in f(0)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedHeapClosureScratchProgram()
        => """
            let n = 1 in
            let text = "value-" + Ashes.Text.fromInt(n) in
            let f =
                if n > 0
                then given (unit) -> Ashes.Text.byteLength(text)
                else given (unit) -> Ashes.Text.byteLength(text)
            in f(0)
            """;

    private static string BuildRuntimeRcOwnedBytesClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let bytes = Ashes.Byte.singleton(7u8) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Byte.length(bytes)
                            else given (unit) -> Ashes.Byte.length(bytes)
                        in f(0)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildNestedRcOwnedBytesClosureScratchProgram()
        => """
            let bytes = Ashes.Byte.append(Ashes.Byte.singleton(1u8))(Ashes.Byte.singleton(2u8)) in
            let f =
                if Ashes.Byte.length(bytes) > 0
                then given (unit) -> Ashes.Byte.length(bytes)
                else given (unit) -> Ashes.Byte.length(bytes)
            in f(0)
            """;

    private static string BuildRuntimeRcOwnedBigIntClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let comparison =
                        let value = Ashes.Number.BigInt.fromInt(n) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                            else given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                        in f(0)
                    in loop(n - 1)(total + comparison + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedBigIntClosureScratchProgram()
        => """
            let value = Ashes.Number.BigInt.add(1N)(2N) in
            let f =
                if Ashes.Number.BigInt.compare(value)(value) == 0
                then given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                else given (unit) -> Ashes.Number.BigInt.compare(value)(value)
            in f(0)
            """;

    // A loop of `stages` whole-program rebuilds over a list whose element record holds a
    // self-recursive ADT, the way IrFunction holds instruction trees. The live set is the same 500
    // records however many stages run.
    private static string BuildRecursiveAccumulatorLoopProgram(int stages)
        => $$"""
            type Node =
                | Leaf(Str)
                | Branch(Node, Node)

            type Fn =
                | fnName: Str
                | fnBody: List(Node)

            let recursive buildBody k acc =
                if k <= 0 then acc
                else buildBody(k - 1)(Branch(Leaf("a"), Leaf("b")) :: acc)

            let recursive genericMap f items =
                match items with
                    | [] -> []
                    | head :: tail -> f(head) :: genericMap(f)(tail)

            let rebuildNode nd =
                match nd with
                    | Leaf(s) -> Leaf(s)
                    | Branch(l, r) -> Branch(l, r)

            let rebuildFn fn =
                match fn with
                    | Fn { fnName = name, fnBody = body } ->
                        Fn(fnName = name, fnBody = genericMap(rebuildNode)(body))

            let recursive buildFns k acc =
                if k <= 0 then acc
                else buildFns(k - 1)(Fn(fnName = "f", fnBody = buildBody(20)([])) :: acc)

            let recursive countBody items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countBody(tail)(total + 1)

            let recursive countFns fns total =
                match fns with
                    | [] -> total
                    | Fn { fnBody = body } :: tail -> countFns(tail)(total + countBody(body)(0))

            let recursive runStages n (all: List(Fn)) =
                if n <= 0 then all
                else runStages(n - 1)(genericMap(rebuildFn)(all))

            Ashes.IO.print(countFns(runStages({{stages}})(buildFns(500)([])))(0))
            """;

    // `stages` nested `let`s, each rebuilding the previous stage's record. Every intermediate is
    // dead as soon as the next stage consumes it, so the program's live set is one record of
    // 20,000 strings regardless of how long the chain is.
    private static string BuildStraightLineRebindChainProgram(int stages)
    {
        StringBuilder chain = new();
        for (int stage = 0; stage < stages; stage++)
        {
            string source = stage == 0 ? "start" : $"s{stage - 1}";
            chain.Append("                let s").Append(stage)
                .Append(" = stage(").Append(source).AppendLine(") in");
        }

        // Each stage rebuilds only the cons cells and shares the element strings, so the live set
        // is the same 20,000-element list however long the chain is.
        return $$"""
            type Node =
                | nodeName: Str
                | nodeItems: List(Str)

            let recursive buildItems k acc =
                if k <= 0 then acc
                else buildItems(k - 1)("item" :: acc)

            let recursive rebuildItems items =
                match items with
                    | [] -> []
                    | head :: tail -> head :: rebuildItems(tail)

            let recursive countItems items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countItems(tail)(total + 1)

            let stage node =
                match node with
                    | Node { nodeName = name, nodeItems = items } ->
                        Node(nodeName = name, nodeItems = rebuildItems(items))

            let pipeline start =
            {{chain.ToString().TrimEnd()}}
                match s{{stages - 1}} with
                    | Node { nodeItems = items } -> countItems(items)(0)

            Ashes.IO.print(pipeline(Node(nodeName = "n", nodeItems = buildItems(20000)([]))))
            """;
    }

    // The state's instruction list has a self-recursive element, which makes the record one of the
    // general ownership contract's types, the ones whose constructor and update cells stay in the arena.
    private static string BuildInlineListBesideRecursiveListParameterProgram(int rounds)
        => $$"""
            type Expr =
                | ExprVar(Str)
                | ExprAdd(Expr, Expr)
                | ExprInt(Int)

            type State =
                | slot: Maybe(Int)
                | count: Int
                | items: List(Int)

            type Lowered =
                | state: State
                | temp: Int
                | error: Maybe(Str)

            let recursive countInts (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> countInts(rest)(total + 1)

            let withSlot value (state: State) = state with slot = value

            let lowerOne (argument: Expr) (state: State) =
                match argument with
                    | ExprInt(v) -> Lowered(state = (state with count = state.count + v, items = v :: state.items), temp = state.count, error = None)
                    | _ -> Lowered(state = (state with items = 0 :: state.items), temp = state.count, error = None)

            let recursive loop (arguments: List(Expr)) (ordinal: Int) (state: State) (temps: List(Int)) =
                match arguments with
                    | [] -> state.count + countInts(temps)(0) + countInts(state.items)(0)
                    | argument :: rest ->
                        match state
                        |> withSlot(Some(ordinal))
                        |> lowerOne(argument) with
                            | Lowered { state = failedState, error = Some(_error) } -> failedState.count
                            | Lowered { state = argumentState, temp = temp, error = None } -> loop(rest)(ordinal + 1)(argumentState)(temp :: temps)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + loop([ExprInt(1), ExprVar("a"), ExprInt(3), ExprInt(4), ExprInt(5), ExprVar("b")])(n % 7)(State(slot = None, count = 0, items = []))([]))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildTrailingPipeFreshArgumentProgram(int count)
        => $$"""
            type Where =
                | line: Int
                | column: Int

            type Instr =
                | kind: Int
                | location: Where

            let step (kind: Int) (location: Where) = (kind, Instr(kind = kind + 1, location = location))

            let recursive pass (instructions: List(Instr)) (total: Int) =
                match instructions with
                    | [] -> total
                    | Instr { kind = kind, location = location } :: tail ->
                        match step(kind)(location) with
                            | (_n, rewritten) -> pass(tail)(total + rewritten.kind)

            let recursive build (n: Int) (acc: List(Instr)) =
                if n == 0
                then acc
                else build(n - 1)(Instr(kind = n, location = Where(line = n, column = 1)) :: acc)

            0
            |> pass(build({{count}})([]))
            |> Ashes.IO.print
            """;

    private static string BuildSeenNameAccumulatorProgram(int rounds)
        => $$"""
            type T =
                | Fn(T, T)
                | Named(Int, Str, List(T))
                | Var(Int)

            let recursive resultType (t: T) =
                match t with
                    | Fn(_parameter, result) -> resultType(result)
                    | other -> other

            let recursive seenName (name: Str) (names: List(Str)) =
                match names with
                    | [] -> false
                    | candidate :: rest -> candidate == name || seenName(name)(rest)

            let recursive freeTypes (items: List(T)) (seen: List(Str)) (count: Int) =
                match items with
                    | [] -> count
                    | body :: rest ->
                        match resultType(body) with
                            | Named(_id, name, []) ->
                                if seenName(name)(seen)
                                then freeTypes(rest)(seen)(count)
                                else freeTypes(rest)(name :: seen)(count + 1)
                            | _ -> freeTypes(rest)(seen)(count)

            let items = [Fn(Var(1), Named(0, "Maybe", [Var(1)])), Named(0, "Int", []), Fn(Var(2), Named(0, "Int", [])), Named(0, "List", [Var(3)])]

            let recursive repeat (times: Int) (total: Int) =
                if times == 0
                then total
                else repeat(times - 1)(total + freeTypes(items)([])(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildConsChainAccumulatorProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive interleave (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> acc
                    | part :: rest -> interleave(part :: ":" :: acc)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(interleave([])(collect(3)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildAccumulatorResetByLiteralOrChoiceProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive startWithLiteral (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest ->
                        match acc with
                            | [] -> startWithLiteral([first])(rest)
                            | _ -> startWithLiteral(first :: acc)(rest)

            let recursive chooseStart (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest ->
                        chooseStart(match acc with
                            | [] -> [first]
                            | _ -> first :: acc)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(startWithLiteral([])(collect(3)([]))) + Ashes.Collection.List.length(chooseStart([])(collect(3)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildLoopParameterTheBodyNeverReadsProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive pairs (previous: Str) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest -> pairs(first)(first :: acc)(rest)

            let recursive skipLast (seen: List(Str)) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest -> skipLast(rest)(first :: acc)(rest)

            let recursive mark (label: Str) (count: Int) (ps: List(Str)) =
                match ps with
                    | [] -> count
                    | first :: rest -> mark(first + "!")(count + 1)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let parts = collect(3)([])
                    in rounds(n - 1)(total + Ashes.Collection.List.length(pairs(Ashes.Text.fromInt(n))([])(parts)) + Ashes.Collection.List.length(skipLast([])([])(parts)) + mark("")(0)(parts))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildConsChainHeadReadingLoopParameterProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive interleave (separator: Str) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest -> interleave(separator)(first :: separator :: acc)(rest)

            let recursive pairs (previous: Str) (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> acc
                    | first :: rest -> pairs(first)(previous :: first :: acc)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let parts = collect(3)([])
                    in rounds(n - 1)(total + Ashes.Collection.List.length(interleave(",")([])(parts)) + Ashes.Collection.List.length(pairs(Ashes.Text.fromInt(n))([])(parts)))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildPipedLambdaReturningCallResultListProgram(int rounds)
        => $$"""
            type LeakLive =
                | block: Int
                | liveIn: Bool

            let recursive build (n: Int) =
                if n == 0
                then []
                else LeakLive(block = n, liveIn = true) :: build(n - 1)

            let recursive stepLive (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | block :: rest -> block |> (given (m) -> build(m))

            let recursive countLive (entries: List(LeakLive)) (total: Int) =
                match entries with
                    | [] -> total
                    | LeakLive { liveIn = true } :: rest -> countLive(rest)(total + 1)
                    | _ :: rest -> countLive(rest)(total)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countLive(stepLive([3, 2]))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildRecordConsedOntoCallResultListProgram(int rounds)
        => $$"""
            type LeakLive =
                | block: Int
                | liveIn: Bool

            let recursive build (n: Int) =
                if n == 0
                then []
                else LeakLive(block = n, liveIn = true) :: build(n - 1)

            let wrap (n: Int) = LeakLive(block = n, liveIn = false) :: build(n)

            let recursive countLive (entries: List(LeakLive)) (total: Int) =
                match entries with
                    | [] -> total
                    | LeakLive { liveIn = true } :: rest -> countLive(rest)(total + 1)
                    | _ :: rest -> countLive(rest)(total)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countLive(wrap(3))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildClosureConsesRecordOntoSelfCallResultProgram(int rounds)
        => $$"""
            type LeakEntry =
                | slot: Int
                | live: Bool

            let makeEntry (value: Int) = LeakEntry(slot = value, live = value % 2 == 0)

            let recursive stepEntries (base: Int) (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | value :: rest ->
                        value + base
                        |> makeEntry
                        |> (given (made) -> made :: stepEntries(base)(rest))

            let recursive liveInOf (slot: Int) (entries: List(LeakEntry)) =
                match entries with
                    | [] -> false
                    | LeakEntry { slot = candidate, live = live } :: rest ->
                        if candidate == slot
                        then live
                        else liveInOf(slot)(rest)

            let recursive stepLive (entries: List(LeakEntry)) (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | slot :: rest ->
                        entries
                        |> liveInOf(slot + 1)
                        |> (given (liveOut) -> LeakEntry(slot = slot, live = liveOut || slot == 2))
                        |> (given (made) -> made :: stepLive(entries)(rest))

            let recursive countEntries (entries: List(LeakEntry)) (total: Int) =
                match entries with
                    | [] -> total
                    | LeakEntry { slot = found, live = true } :: rest -> countEntries(rest)(total + found)
                    | _ :: rest -> countEntries(rest)(total)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    rounds(n - 1)(total + countEntries(stepEntries(1)([1, 2, 3, 4]))(0) + countEntries(stepLive([])([1, 2, 3, 4]))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildExitCellPassedToCapturedCalleeProgram(int rounds)
        => $$"""
            let nestedPairs (parts: List(Str)) =
                (let recursive back (acc: List(Str)) (xs: List(Str)) =
                    match xs with
                        | [] -> acc
                        | head :: tail -> back(head :: acc)(tail)
                in
                    let recursive go (acc: List(Str)) (ps: List(Str)) =
                        match ps with
                            | [] -> back([])(acc)
                            | first :: rest ->
                                match rest with
                                    | [] -> back([])(first :: acc)
                                    | second :: more -> go((first + second) :: acc)(more)
                    in go([])(parts))

            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(nestedPairs(collect(3)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildExitCellPassedToCallProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive rev (acc: List(Str)) (xs: List(Str)) =
                match xs with
                    | [] -> acc
                    | head :: tail -> rev(head :: acc)(tail)

            let recursive keepOdd (acc: List(Str)) (ps: List(Str)) =
                match ps with
                    | [] -> rev([])(acc)
                    | first :: rest ->
                        match rest with
                            | [] -> rev([])(first :: acc)
                            | _ :: more -> keepOdd(acc)(more)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(keepOdd([])(collect(3)([]))) + 1)

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildExitCellOfRecordsPassedToCallProgram(int rounds)
        => $$"""
            type Token =
                | text: Str
                | position: Int

            let recursive reverseValues remaining result =
                match remaining with
                    | [] -> result
                    | head :: tail -> reverseValues(tail)(head :: result)

            let readToken (position: Int) = Token(text = Ashes.Text.fromInt(position), position = position)

            let recursive scan (n: Int) (position: Int) (tokens: List(Token)) =
                if position >= n
                then reverseValues(Token(text = "", position = position) :: tokens)([])
                else
                    let token = readToken(position)
                    in scan(n)(token.position + 1)(token :: tokens)

            let recursive measure (tokens: List(Token)) (total: Int) =
                match tokens with
                    | [] -> total
                    | token :: rest -> measure(rest)(total + Ashes.Text.byteLength(token.text) + token.position)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + measure(scan(3)(0)([]))(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildCallResultOntoChoiceAccumulatorProgram(int rounds)
        => $$"""
            let recursive walkNames (queue: List(Str)) (seen: List(Str)) (n: Int) =
                match queue with
                    | [] -> n + Ashes.Collection.List.length(seen)
                    | name :: rest ->
                        if n > 2
                        then walkNames(rest)(Ashes.Text.join("/")([name, "x"]) :: seen)(n + 1)
                        else walkNames(rest)(seen)(n + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + walkNames(["a", "b", "c", "d", "e" + Ashes.Text.fromInt(n)])([])(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildWorklistOfPathsProgram(int rounds)
        => $$"""
            let recursive walk (queue: List(List(Str))) (seen: List(Str)) (n: Int) =
                match queue with
                    | [] -> n + Ashes.Collection.List.length(seen)
                    | path :: rest ->
                        let current = Ashes.Text.join("")(path)
                        in
                            if n > 100
                            then walk(rest)(seen)(n + 1)
                            else walk(Ashes.Collection.List.append(rest)(if n < 2 then [["x"]] else []))(current :: seen)(n + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + walk([["a"], ["b" + Ashes.Text.fromInt(n)]])([])(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildAccumulatorStartedAfreshProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive interleave (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> acc
                    | first :: rest ->
                        match acc with
                            | [] -> interleave(first :: [])(rest)
                            | _ -> interleave(first :: ":" :: acc)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(interleave([])(collect(3)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildLoopExitCellProgram(int rounds)
        => $$"""
            let recursive collect (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive pairwiseGo (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> acc
                    | first :: rest ->
                        match rest with
                            | [] -> first :: acc
                            | second :: more -> pairwiseGo((first + second) :: acc)(more)

            let recursive stampEnd (acc: List(Str)) (parts: List(Str)) =
                match parts with
                    | [] -> "end" :: acc
                    | part :: rest -> stampEnd((part + "!") :: acc)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(pairwiseGo([])(collect(3)([]))) + Ashes.Collection.List.length(stampEnd([])(collect(2)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildJoinedViewsProgram(int rounds)
        => $$"""
            let recursive collect (bytes: Bytes) (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else collect(bytes)(n - 1)(Ashes.Byte.subView(bytes)(16)(4) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let bytes = Ashes.Byte.fromText("abcdefghijklmnopqrstuvwxyz" + Ashes.Text.fromInt(n % 10))
                    in rounds(n - 1)(total + Ashes.Text.byteLength(Ashes.Text.join(",")(collect(bytes)(3)([]))))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildMappedNullaryEnumListProgram(int rounds)
        => $$"""
            type Kind =
                | Alpha
                | Beta
                | Gamma

            let kindOf (n: Int) =
                if n % 3 == 0
                then Alpha
                else
                    if n % 3 == 1
                    then Beta
                    else Gamma

            let recursive countAlpha (kinds: List(Kind)) (total: Int) =
                match kinds with
                    | [] -> total
                    | Alpha :: rest -> countAlpha(rest)(total + 1)
                    | _ :: rest -> countAlpha(rest)(total)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let kinds = Ashes.Collection.List.map(kindOf)([n, n + 1, n + 2])
                    in rounds(n - 1)(total + countAlpha(kinds)(0))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildLastOfUnresolvedHeadProgram(int rounds)
        => $$"""
            let recursive lastOf list =
                match list with
                    | last :: [] -> last
                    | _ :: rest -> lastOf(rest)
                    | [] -> ""

            let recursive rounds (n: Int) (acc: Str) =
                if n == 0
                then acc
                else
                    let path = ["a", "b" + Ashes.Text.fromInt(n)]
                    in
                        let current = lastOf(path)
                        in rounds(n - 1)(acc + current + ",")

            Ashes.IO.print(rounds({{rounds}})(""))
            """;

    private static string BuildMapThreadedThroughWalkedListLoopProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map.MapTree

            let recursive addDependents (function: Str) (dependencies: List(Str)) (dependents: MapTree(Str, Str)) =
                match dependencies with
                    | [] -> dependents
                    | dependency :: rest ->
                        dependents
                        |> Ashes.Collection.Map.setStr(dependency)(function)
                        |> addDependents(function)(rest)

            let recursive dependentsOf (functions: List(Str)) (dependents: MapTree(Str, Str)) =
                match functions with
                    | [] -> dependents
                    | key :: rest ->
                        dependents
                        |> addDependents(key)(["n1", "n2", "n3", "n4", "n5"])
                        |> dependentsOf(rest)

            let functions = ["f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10"]

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.Map.size(dependentsOf(functions)(Ashes.Collection.Map.empty)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildProvidedCapabilityShapeLoopProgram(bool handled)
        => $$"""
            capability Clock =
                | now : Unit -> Int

            provide Clock =
                | now =
                    given (_) -> 1

            capability Probe =
                | get : Unit -> Int

            type Shape =
                | Fn(Shape, Shape)
                | Named(Int, Str, List(Shape))
                | Var(Int)

            let recursive resultType (shape: Shape) =
                match shape with
                    | Fn(_parameter, result) -> resultType(result)
                    | other -> other

            let isFree (body: Shape) =
                match resultType(body) with
                    | Named(_id, _name, []) -> 100
                    | _ -> 0

            let recursive rounds (item: Shape) (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(item)(n - 1)(total + isFree(item) + perform Clock.now(Unit))

            let tick =
                given (_) -> {{(handled ? "handle perform Probe.get(Unit) with\n            | Probe.get(_) -> resume(2)\n            | return(x) -> x" : "0")}}

            Ashes.IO.print(rounds(Fn(Var(1), Named(0, "Maybe", [])))(10)(tick(Unit)))
            """;

    private static string BuildHandlerLoopLiteralStringArmProgram(int rounds)
        => $$"""
            type Maybe2(a) =
                | None2
                | Some2(a)

            capability Probe(a) =
                | get : Unit -> a

            let recursive loop : Int -> Int -> Int needs e =
                given (n: Int) ->
                    given (acc: Int) ->
                        if n == 0
                        then acc
                        else
                            let observed =
                                match let r : Maybe2(Int) =
                                    handle perform Probe.get(Unit) with
                                        | Probe.get(_) -> resume(None2)
                                        | return(x) -> x
                                in r with
                                    | None2 -> "FuzzNone"
                                    | Some2(v) -> "FuzzSome(" + Ashes.Text.fromInt(v) + ")"
                            in loop(n - 1)(acc + Ashes.Text.byteLength(observed))
            in Ashes.IO.print(Ashes.Text.fromInt(loop({{rounds}})(0)))
            """;

    private static string BuildMapUpsertClosureResultLoopProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map.MapTree

            let recursive rounds (n: Int) (total: Int) (counts: MapTree(Str, Str)) =
                if n == 0
                then total + Ashes.Collection.Map.size(counts)
                else
                    counts
                    |> Ashes.Collection.Map.upsertStr("k" + Ashes.Text.fromInt(n % 4))("0")(given (existing: Str) -> Ashes.Text.fromInt((Ashes.Text.byteLength(existing) + n) % 10))
                    |> rounds(n - 1)(total + 1)

            Ashes.IO.print(rounds({{rounds}})(0)(Ashes.Collection.Map.empty))
            """;

    private static string BuildMapInsertIfAbsentWalkProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map.MapTree

            let recursive addDependents (function: Str) (dependencies: List(Str)) (dependents: MapTree(Str, List(Str))) =
                match dependencies with
                    | [] -> dependents
                    | dependency :: rest ->
                        (match Ashes.Collection.Map.getStr(dependency)(dependents) with
                            | Some(_existing) -> dependents
                            | None -> Ashes.Collection.Map.setStr(dependency)([function])(dependents))
                        |> addDependents(function)(rest)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.Map.size(addDependents("f")(["k" + Ashes.Text.fromInt(n), "a", "b"])(Ashes.Collection.Map.empty)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRecordUpdateSuccessorOfLoopParameterProgram(int rounds)
        => $$"""
            type State =
                | instructions: List(Int)
                | labels: List(Str)
                | counter: Int

            let recursive countInts (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: tail -> countInts(tail)(total + 1)

            let recursive countStrs (items: List(Str)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: tail -> countStrs(tail)(total + 1)

            let recursive lowerItems (items: List(Int)) (state: State) =
                match items with
                    | [] -> state.counter + countInts(state.instructions)(0) + countStrs(state.labels)(0)
                    | value :: rest -> lowerItems(rest)((state with instructions = value :: state.instructions, counter = state.counter + 1))

            let recursive numbers (n: Int) (built: List(Int)) =
                if n == 0 then built
                else numbers(n - 1)(n :: built)

            let program = numbers(80)([])

            let recursive repeat times total =
                if times == 0 then total
                else repeat(times - 1)(total + lowerItems(program)(State(instructions = [], labels = ["a", "b"], counter = 0)))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildLiteralSuccessorBorrowingArenaBindingProgram(int rounds)
        => $$"""
            type Span =
                | start: Int
                | length: Int

            type Item =
                | At(Span, Item)
                | Decl(Int)
                | Let(Int)

            type FunctionGroup =
                | declarationSpan: Maybe(Span)
                | names: List(Str)

            type State =
                | instructions: List(Int)
                | currentSpan: Maybe(Span)
                | functionGroup: FunctionGroup
                | counter: Int

            let recursive countInts (items: List(Int)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: tail -> countInts(tail)(total + 1)

            let recursive lowerItems (items: List(Item)) (body: List(Str)) (seen: List(Str)) (state: State) =
                match items with
                    | [] -> state.counter + countInts(state.instructions)(0)
                    | At(span, inner) :: rest ->
                        State(instructions = state.instructions, currentSpan = Some(span), functionGroup = state.functionGroup, counter = state.counter)
                        |> lowerItems(inner :: rest)(body)(seen)
                    | Decl(_value) :: rest -> lowerItems(rest)(body)(seen)(state)
                    | Let(value) :: rest -> lowerItems(rest)(body)(seen)((state with instructions = value :: state.instructions, counter = state.counter + 1))

            let recursive items (n: Int) (built: List(Item)) =
                if n == 0
                then built
                else items(n - 1)(At(Span(start = n, length = 1))(Decl(n)) :: At(Span(start = n, length = 2))(Let(n)) :: built)

            let program = items(40)([])

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + lowerItems(program)(["body"])(["seen"])(State(instructions = [], currentSpan = None, functionGroup = FunctionGroup(declarationSpan = None, names = ["a"]), counter = 0)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopExitRecordHoldingParameterProgram(int rounds)
        => $$"""
            type State =
                | instructions: List(Int)
                | counter: Int

            type Arguments =
                | state: State
                | count: Int

            let recursive lowerArguments (arguments: List(Int)) (state: State) =
                match arguments with
                    | [] -> Arguments(state = state, count = 0)
                    | _argument :: rest -> lowerArguments(rest)(state)

            let lowerCall (arguments: List(Int)) (state: State) =
                match lowerArguments(arguments)(state) with
                    | Arguments { state = argumentState, count = count } -> argumentState.counter + count

            let recursive countArguments (arguments: List(Int)) (state: State) =
                match arguments with
                    | [] ->
                        match Arguments(state = state, count = 0) with
                            | Arguments { count = count } -> count + state.counter
                    | _argument :: rest -> countArguments(rest)(state)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + lowerCall([1, 2, 3, 4, 5])(State(instructions = [n], counter = n)) + countArguments([1, 2, 3])(State(instructions = [n], counter = n)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildWorklistFrontWalkedAndRefilledProgram(int rounds)
        => $$"""
            type Seen =
                | Leaf
                | Node(Seen, Str, Seen)

            let recursive insert (key: Str) (seen: Seen) =
                match seen with
                    | Leaf -> Node(Leaf, key, Leaf)
                    | Node(left, current, right) -> Node(insert(key)(left), current, right)

            let recursive countSeen (seen: Seen) =
                match seen with
                    | Leaf -> 0
                    | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

            let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> reverseAll(rest)(value :: acc)

            let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

            let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> countSeen(seen)
                            | _ -> solve(reverseAll(back)([]))([])(seen)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then countSeen(seen)
                        else
                            match enqueue(key + "x")(back)(seen) with
                                | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)(fuel - 1)

            let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf)(10))

            Ashes.IO.print(rounds(["a", "b", "c"])({{rounds}})(0))
            """;

    private static string BuildPatternStringTestedByCurriedCallProgram(int rounds)
        => $$"""
            import Ashes.Text

            type Box =
                | label: Str
                | tag: Int

            let recursive countStarting (boxes: List(Box)) (count: Int) =
                match boxes with
                    | [] -> count
                    | Box { label = label } :: rest ->
                        if Ashes.Text.startsWith(label)("A")
                        then countStarting(rest)(count + 1)
                        else countStarting(rest)(count)

            let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
                match labels with
                    | [] -> built
                    | label :: rest -> boxesFrom(rest)(Box(label = label + "", tag = 1) :: built)

            let wide = "{{new string('w', 480)}}"

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countStarting(boxesFrom(["A" + wide, "B" + wide, "A" + wide + "e", "M" + wide])([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildItemLoopClosesRebuiltFromCallPartsProgram(int rounds)
        => $$"""
            type Item =
                | At(Int, Item)
                | Plain(Str)
                | Grouped(Str)

            type Binding =
                | name: Str
                | slot: Int

            type State =
                | bindings: List(Binding)
                | labels: List(Str)
                | counter: Int

            type Close =
                | closeName: Maybe(Str)
                | closeBindings: List(Binding)

            type Opened =
                | openedState: State
                | openedPending: List(Close)

            let withSpan (span: Int) (state: State) = state with counter = state.counter + span

            let open (name: Str) (pending: List(Close)) (state: State) = Opened(openedState = (state with bindings = Binding(name = name, slot = state.counter) :: state.bindings), openedPending = Close(closeName = Some(name), closeBindings = state.bindings) :: pending)

            let openGroup (name: Str) (state: State) = state with labels = name :: state.labels

            let restore (bindings: List(Binding)) = Close(closeName = None, closeBindings = bindings)

            let recursive countCloses (closes: List(Close)) (total: Int) =
                match closes with
                    | [] -> total
                    | _ :: rest -> countCloses(rest)(total + 1)

            let recursive lowerItems (items: List(Item)) (pending: List(Close)) (state: State) =
                match items with
                    | [] -> state.counter + countCloses(pending)(0)
                    | At(span, inner) :: rest ->
                        state
                        |> withSpan(span)
                        |> lowerItems(inner :: rest)(pending)
                    | Plain(name) :: rest ->
                        match open(name)(pending)(state) with
                            | Opened { openedState = opened, openedPending = nextPending } -> lowerItems(rest)(nextPending)(opened)
                    | Grouped(name) :: rest ->
                        state
                        |> openGroup(name)
                        |> lowerItems(rest)(restore(state.bindings) :: pending)

            let program = [At(1)(Plain("a")), At(2)(Grouped("b")), At(3)(Plain("c")), At(4)(Plain("d")), At(5)(Grouped("e")), At(6)(Plain("f"))]
            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + lowerItems(program)([])(State(bindings = [], labels = [], counter = 0)))

            0
            |> rounds({{rounds}})
            |> Ashes.IO.print
            """;

    private static string BuildWorklistBackGrownAndEmptiedProgram(int rounds)
        => $$"""
            let recursive reverseAll (acc: List(Str)) (items: List(Str)) =
                match items with
                    | [] -> acc
                    | head :: tail -> reverseAll(head :: acc)(tail)

            let recursive solve (front: List(Str)) (back: List(Str)) (queued: Int) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> queued
                            | _ -> solve(reverseAll([])(back))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then queued
                        else solve(rest)(key :: back)(queued + 1)(fuel - 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + solve(["cinder", "ember"])([])(0)(60))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildPatternListPassedToBorrowingCallProgram(int rounds)
        => $$"""
            type Item =
                | itemName: Str
                | weight: Int

            type Box =
                | label: Str
                | items: List(Item)

            let recursive find (name: Str) (items: List(Item)) =
                match items with
                    | [] -> None
                    | item :: rest ->
                        if item.itemName == name
                        then Some(item.itemName + "!")
                        else find(name)(rest)

            let recursive scan name (boxes: List(Box)) =
                match boxes with
                    | [] -> None
                    | Box { items = items } :: rest ->
                        match find(name)(items) with
                            | Some(found) -> Some(found)
                            | None -> scan(name)(rest)

            let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
                match labels with
                    | [] -> built
                    | label :: rest -> boxesFrom(rest)(Box(label = label + "", items = [Item(itemName = label + "x", weight = 1)]) :: built)

            let wide = "{{new string('w', 480)}}"

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match scan("A" + wide + "x")(boxesFrom(["A" + wide, "B" + wide, "C" + wide, "D" + wide, "M" + wide])([])) with
                        | Some(found) -> rounds(n - 1)(total + Ashes.Text.byteLength(found))
                        | None -> rounds(n - 1)(total)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildVariantNodeHoldingWalkedHeadProgram(int rounds)
        => $$"""
            import Ashes.Text

            type Chain =
                | End
                | Link(Str, Chain)

            let recursive size (chain: Chain) =
                match chain with
                    | End -> 0
                    | Link(_key, rest) -> 1 + size(rest)

            let recursive grow (keys: List(Str)) (chain: Chain) =
                match keys with
                    | [] -> chain
                    | key :: rest -> grow(rest)(Link(key)(chain))

            let wide = "{{new string('w', 480)}}"

            let run (seed: Int) = size(grow([wide + Ashes.Text.fromInt(seed), wide + "e", wide + "s"])(End)) + seed

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + run(8))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private const string EmptyListFieldUnderConsPatternProgram = """
            import Ashes.Text

            type Item =
                | itemName: Str
                | weight: Int

            type Box =
                | label: Str
                | tag: Int
                | items: List(Item)

            let recursive find (name: Str) (items: List(Item)) =
                match items with
                    | [] -> None
                    | item :: rest ->
                        if item.itemName == name
                        then Some(item.itemName + "!")
                        else find(name)(rest)

            let recursive scan name (boxes: List(Box)) =
                match boxes with
                    | [] -> None
                    | Box { label = label, items = items } :: rest ->
                        if Ashes.Text.startsWith(label)("A")
                        then
                            match find(name)(items) with
                                | Some(found) ->
                                    if Ashes.Text.byteLength(found) > 0
                                    then Some(found)
                                    else scan(name)(rest)
                                | None -> scan(name)(rest)
                        else scan(name)(rest)

            let recursive boxesFrom (labels: List(Str)) (built: List(Box)) =
                match labels with
                    | [] -> built
                    | label :: rest ->
                        let items =
                            if label == "A-empty"
                            then []
                            else [Item(itemName = label + "x", weight = 1), Item(itemName = label + "y", weight = 2)]
                        in boxesFrom(rest)(Box(label = label + "", tag = 1, items = items) :: built)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let boxes = boxesFrom(["A-full", "A-empty", "Main"])([])
                    in
                        match scan("A-fullx")(boxes) with
                            | Some(found) -> rounds(n - 1)(total + Ashes.Text.byteLength(found))
                            | None -> rounds(n - 1)(total)

            0
            |> rounds(3)
            |> Ashes.IO.print
            """;

    private static string BuildWorklistFrontHeadsReachTreeOnlyAsKeysProgram(int rounds)
        => $$"""
            type Tree(K, V) =
                | Leaf
                | Node(Tree(K, V), K, V, Tree(K, V))

            let compareKeys (left: Str) (right: Str) =
                right
                |> Ashes.Byte.fromText
                |> Ashes.Byte.compare(Ashes.Byte.fromText(left))

            let setStr newKey newValue =
                (let recursive go map =
                    match map with
                        | Leaf -> Node(Leaf)(newKey)(newValue)(Leaf)
                        | Node(left, key, value, right) ->
                            let ordering = compareKeys(newKey)(key)
                            in
                                if ordering == 0
                                then Node(left)(key)(newValue)(right)
                                else
                                    if ordering <= -1
                                    then Node(go(left))(key)(value)(right)
                                    else Node(left)(key)(value)(go(right))
                in go)

            let recursive size map =
                match map with
                    | Leaf -> 0
                    | Node(left, _key, _value, right) -> size(left) + 1 + size(right)

            let recursive reverseOnto (acc: List(Str)) (items: List(Str)) =
                match items with
                    | [] -> acc
                    | head :: tail -> reverseOnto(head :: acc)(tail)

            let recursive solve (front: List(Str)) (back: List(Str)) (queued: Tree(Str, Bool)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> queued
                            | _ -> solve(reverseOnto([])(back))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then queued
                        else solve(rest)((key + "x") :: back)(setStr(key)(true)(queued))(fuel - 1)

            let wide = "{{new string('w', 480)}}"

            let run (seed: Int) = size(solve([wide + "1", wide + "2"])([])(Leaf)(7)) + seed

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + run(8))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildMapLoopSuccessorBoundByLetProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map
            import Ashes.Collection.List

            let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> queued
                            | _ -> solve(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then queued
                        else
                            let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                            in solve(rest)((key + "x") :: back)(dequeued)(fuel - 1)

            let wide = "{{new string('w', 480)}}"

            let run (seed: Int) = Ashes.Collection.Map.size(solve([wide + "1", wide + "2"])([])(Ashes.Collection.Map.empty)(7)) + seed

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + run(8))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildTableMergedOrHandedOnProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map
            import Ashes.Collection.List

            type Reach =
                | fresh: Bool
                | parts: List(Str)

            let lookup (key: Str) (table: MapTree(Str, Reach)) =
                match Ashes.Collection.Map.getStr(key)(table) with
                    | Some(found) -> found
                    | None -> Reach(fresh = false, parts = [])

            let recursive solve (table: MapTree(Str, Reach)) (keys: List(Str)) (fuel: Int) =
                match keys with
                    | [] -> table
                    | key :: rest ->
                        if fuel <= 0
                        then table
                        else
                            let previous = lookup(key)(table)
                            in
                                let merged = Reach(fresh = true, parts = key :: previous.parts)
                                in
                                    if Ashes.Collection.List.length(previous.parts) > 1
                                    then solve(table)(rest)(fuel - 1)
                                    else solve(Ashes.Collection.Map.setStr(key)(merged)(table))(rest)(fuel - 1)

            let run (seed: Int) = Ashes.Collection.Map.size(solve(Ashes.Collection.Map.empty)(["flint", "coal", "flint", "flint", "coal", "flint", "flint"])(11)) + seed

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + run(8))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildMapLoopPredecessorPastQueueOfStringsProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map
            import Ashes.Collection.List

            let recursive enqueue (pending: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) =
                match pending with
                    | [] -> (back, queued)
                    | key :: rest ->
                        match Ashes.Collection.Map.getStr(key)(queued) with
                            | Some(true) -> enqueue(rest)(back)(queued)
                            | _ ->
                                queued
                                |> Ashes.Collection.Map.setStr(key)(true)
                                |> enqueue(rest)(key :: back)

            let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> Ashes.Collection.Map.size(queued)
                            | _ -> solve(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then Ashes.Collection.Map.size(queued)
                        else
                            let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                            in
                                if fuel % 4 == 0
                                then solve(rest)(back)(dequeued)(fuel - 1)
                                else
                                    match enqueue([key + "coal"])(back)(dequeued) with
                                        | (nextBack, nextQueued) -> solve(rest)(nextBack)(nextQueued)(fuel - 1)

            let wide = "{{new string('w', 480)}}"

            let run (seed: Int) = solve([wide + "1", wide + "2", wide + "3", wide + "4"])([])(Ashes.Collection.Map.empty)(7) + seed

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + run(8))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericResultBesideOwnedRecordProgram(int rounds)
        => $$"""
            type Box =
                | items: List(Str)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Box(items = [wide + Ashes.Text.fromInt(n), wide], count = n)

            let keepIfLarge (value: a) (n: Int) =
                let box = make(n)
                in
                    if box.count > 2
                    then [value, value]
                    else []

            let recursive countAll (values: List(Int)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countAll(keepIfLarge(n)(n))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOpaqueHandleResultOfOwnedRecordProgram(int rounds)
        => $$"""
            external type Handle

            external handleOf(Str) -> Handle = "strlen"

            type Box =
                | name: Str
                | items: List(Str)

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Box(name = wide + Ashes.Text.fromInt(n), items = [wide + Ashes.Text.fromInt(n), wide])

            let register (box: Box) = handleOf(box.name)

            let registerNew (n: Int) = register(make(n))

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match registerNew(n) with
                        | _handle -> rounds(n - 1)(total + 1)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericResultHandsOverOwnedCallResultProgram(int rounds)
        => $$"""
            type Collection =
                | items: List(Str)
                | count: Int

            type Loaded =
                | names: List(Str)
                | total: Int

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Collection(items = [wide + Ashes.Text.fromInt(n), wide], count = n)

            let complete (collection: Collection) =
                match collection with
                    | Collection { items = items, count = count } ->
                        Loaded(names = items, total = count)
                        |> Ashes.Internal.deepCopy
                        |> Ok

            let finish (collection: Collection) (result: Result(e, Str)) =
                match result with
                    | Error(error) -> Error(error)
                    | Ok(_outer) -> complete(collection)

            let totalOf (result: Result(Str, Loaded)) =
                match result with
                    | Error(_) -> 0
                    | Ok(Loaded { total = total }) -> total

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + totalOf(finish(make(n))(Ok("x"))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericResultWrapsOwnedCallResultProgram(int rounds)
        => $$"""
            type Collection =
                | items: List(Str)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Collection(items = [wide + Ashes.Text.fromInt(n), wide], count = n)

            let prepend (extra: Str) (collection: Collection) =
                match collection with
                    | Collection { items = items, count = count } -> Collection(items = extra :: items, count = count + 1)

            let continueParsing (extra: Str) (tailResult: Result(e, Collection)) =
                match tailResult with
                    | Error(error) -> Error(error)
                    | Ok(collection) ->
                        collection
                        |> prepend(extra)
                        |> Ok

            let countOf (result: Result(Str, Collection)) =
                match result with
                    | Error(_) -> 0
                    | Ok(Collection { count = count }) -> count

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countOf(continueParsing(wide)(Ok(make(n)))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildContractParameterReturnedAsIsProgram(int rounds)
        => $$"""
            type Collection =
                | items: List(Str)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Collection(items = [wide + Ashes.Text.fromInt(n), wide], count = n)

            let wrap (n: Int) = Ok(make(n))

            let pinned (result: Result(Str, Collection)) = result

            let countOf (result: Result(Str, Collection)) =
                match result with
                    | Error(_) -> 0
                    | Ok(Collection { count = count }) -> count

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countOf(pinned(wrap(n))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOwnedValueBehindContractCallResultProgram(int rounds)
        => $$"""
            type Collection =
                | items: List(Str)
                | count: Int

            type Loaded =
                | names: List(Str)
                | total: Int

            let wide = "{{new string('w', 480)}}"

            let make (n: Int) = Collection(items = [wide + Ashes.Text.fromInt(n), wide], count = n)

            let prepend (extra: Str) (collection: Collection) =
                match collection with
                    | Collection { items = items, count = count } -> Collection(items = extra :: items, count = count + 1)

            let complete (collection: Collection) =
                match collection with
                    | Collection { items = items, count = count } ->
                        Loaded(names = items, total = count)
                        |> Ashes.Internal.deepCopy
                        |> Ok

            let finish (collection: Collection) (result: Result(e, Str)) =
                match result with
                    | Error(error) -> Error(error)
                    | Ok(outer) ->
                        collection
                        |> prepend(outer)
                        |> complete

            let totalOf (result: Result(Str, Loaded)) =
                match result with
                    | Error(_) -> 0
                    | Ok(Loaded { total = total }) -> total

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + totalOf(finish(make(n))(Ok(wide))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRecursiveProducerOverTupleScrutineeProgram(int rounds)
        => $$"""
            let recursive keep (ownership: List(Str)) =
                match (ownership, 0) with
                    | (entry :: rest, _) -> entry :: keep(rest)
                    | _ -> []

            let recursive words (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else words(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(keep(words(8)([]))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildCurriedProducerOverTupleHeadsProgram(int rounds)
        => $$"""
            let recursive keep (marker: Str) (items: List((Str, Int))) =
                match items with
                    | entry :: rest -> entry :: keep(marker)(rest)
                    | [] -> []

            let recursive pairs (n: Int) (acc: List((Str, Int))) =
                if n <= 0
                then acc
                else pairs(n - 1)((Ashes.Text.fromInt(n), n) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(keep("x")(pairs(8)([]))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildProducerReturningParameterTailProgram(int rounds)
        => $$"""
            let recursive keepUntil (stop: Str) (items: List(Str)) =
                match items with
                    | entry :: rest ->
                        if entry == stop
                        then rest
                        else entry :: keepUntil(stop)(rest)
                    | [] -> []

            let recursive words (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else words(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(keepUntil("5")(words(10)([]))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildListParameterTailMatchedInTupleBesideRecordProgram(int rounds)
        => $$"""
            type Close =
                | span: Int
                | outer: List(Int)

            type Lowered =
                | state: List(Int)
                | temp: Int

            let recursive close (pending: List(Close)) (lowered: Lowered) =
                match (pending, lowered) with
                    | ([], _lowered) -> lowered
                    | (item :: rest, Lowered { state = bodyState, temp = temp }) -> close(rest)(Lowered(state = item.span :: bodyState, temp = temp + item.span))

            let closes (n: Int) = [Close(span = n, outer = [n]), Close(span = 2, outer = []), Close(span = 3, outer = [n, n])]

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match close(closes(n))(Lowered(state = [], temp = n)) with
                        | Lowered { state = st, temp = t } -> rounds(n - 1)(total + t + Ashes.Collection.List.length(st))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildTupleConstructorChildHandedBackProgram(int rounds)
        => $$"""
            type Kept =
                | state: List(Int)
                | temp: Int

            type Tree =
                | Leaf(Kept)
                | Node(Tree, Tree)

            let recursive build (d: Int) (n: Int) =
                if d == 0
                then Leaf(Kept(state = [n, d], temp = n))
                else Node(build(d - 1)(n), build(d - 1)(n + d))

            let recursive leftmost (t: Tree) (acc: Int) =
                match (t, acc) with
                    | (Leaf(Kept { state = st, temp = k }), a) -> a + k + Ashes.Collection.List.length(st)
                    | (Node(l, _), a) -> leftmost(l)(a + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + leftmost(build(4)(n))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildTupleConsChainTailHandedBackProgram(int rounds)
        => $$"""
            type Kept =
                | state: List(Int)
                | temp: Int

            let recursive pairs (items: List(Int)) (kept: Kept) (acc: Int) =
                match (items, kept) with
                    | (a :: b :: rest, same) -> pairs(rest)(same)(acc + a * b)
                    | (_, Kept { state = st, temp = k }) -> acc + k + Ashes.Collection.List.length(st)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + pairs([1, 2, n, 4, 5, n, 7])(Kept(state = [n, 7, 8], temp = n))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildWholeTupleElementHandedBackProgram(int rounds)
        => $$"""
            type Kept =
                | state: List(Int)
                | temp: Int

            let recursive close (pending: List(Int)) (kept: Kept) (acc: Int) =
                match (pending, kept) with
                    | ([], Kept { state = st, temp = t }) -> acc + t + Ashes.Collection.List.length(st)
                    | (n :: rest, same) -> close(rest)(same)(acc + n)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + close([1, 2, 3, 4, n])(Kept(state = [n, 7, 8, 9, 10, 11], temp = n))(n))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOkOfMatchedRecordFieldsProgram(int rounds)
        => $$"""
            type Fields =
                | xs: List(Int)
                | ps: List((Int, Bool))
                | js: List(Str)

            let make (k: Int) = Fields(xs = [k, k + 1], ps = [(k, true)], js = ["j"])

            let outer (k: Int) =
                match make(k) with
                    | Fields { xs = xs, ps = ps, js = js } -> Ok((xs, (ps, js)))

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match outer(n) with
                        | Ok((a, _rest)) -> rounds(n - 1)(total + Ashes.Collection.List.length(a))
                        | Error(_) -> rounds(n - 1)(total)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildFreshStartBesideHandedOnListProgram(int rounds)
        => $$"""
            let produce (k: Int) = Some(([k, k + 1], [k * 2]))

            let recursive restart (remaining: Int) (acc: List(Maybe((List(Int), List(Int))))) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(acc)
                else
                    match produce(remaining) with
                        | Some((a, b)) -> restart(remaining - 1)([Some((a, b))])(total + Ashes.Collection.List.length(a))
                        | None -> restart(remaining - 1)(acc)(total)

            Ashes.IO.print(restart({{rounds}})([])(0))
            """;

    private static string BuildListLiteralSuccessorReadingHeapSiblingProgram(int rounds)
        => $$"""
            type Parts =
                | front: List(Int)
                | back: List(Int)

            let produce (k: Int) = [k, k + 1]

            let recursive pairs (remaining: Int) (acc: List((List(Int), List(Int)))) (other: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(other)
                else pairs(remaining - 1)([(other, [remaining])])(produce(remaining))(total + Ashes.Collection.List.length(other))

            let recursive records (remaining: Int) (acc: List(Parts)) (other: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(other)
                else records(remaining - 1)([Parts(front = other, back = [remaining])])(produce(remaining))(total + Ashes.Collection.List.length(other))

            Ashes.IO.print(pairs({{rounds}})([])([7])(0) + records({{rounds}})([])([7])(0))
            """;

    private static string BuildScalarListHandedACallResultPartProgram(int rounds)
        => $$"""
            let produce (k: Int) = Some(([k, k + 1], [k * 2]))

            let recursive handOn (remaining: Int) (acc: List(Maybe((List(Int), List(Int))))) (other: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(other)
                else
                    match produce(remaining) with
                        | Some((a, b)) -> handOn(remaining - 1)([Some((other, b))])(a)(total + 1)
                        | None -> handOn(remaining - 1)(acc)(other)(total)

            let checked (k: Int) =
                if k < 0
                then Error("negative")
                else Ok(([k, k + 1, k + 2], [k * 2]))

            let recursive siblings (remaining: Int) (first: List(Int)) (second: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second)
                else
                    match checked(remaining) with
                        | Ok((a, b)) -> siblings(remaining - 1)(b)(a)(total + Ashes.Collection.List.length(first))
                        | Error(_) -> siblings(remaining - 1)(first)(second)(total)

            Ashes.IO.print(handOn({{rounds}})([])([7])(0) + siblings({{rounds}})([7])([8, 9])(0))
            """;

    private static string BuildIntListParameterTailMatchedInTupleBesideRecordProgram(int rounds)
        => $$"""
            type Lowered =
                | state: List(Int)
                | temp: Int

            let recursive close (pending: List(Int)) (lowered: Lowered) =
                match (pending, lowered) with
                    | ([], _lowered) -> lowered
                    | (n :: rest, Lowered { state = bodyState, temp = temp }) -> close(rest)(Lowered(state = bodyState, temp = temp + n))

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match close([n, 2, 3, 4, 5])(Lowered(state = [], temp = n)) with
                        | Lowered { state = st, temp = t } -> rounds(n - 1)(total + t + Ashes.Collection.List.length(st))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOkTupleBuiltBeforeErrorTypeProgram(int rounds)
        => $$"""
            let produce (k: Int) =
                if k >= 0
                then Ok(([k, k + 1, k + 2], [k * 2, k * 3, k * 4]))
                else Error("negative")

            let forward (k: Int) =
                match produce(k) with
                    | Ok((first, second)) -> Ok((k, first, second))
                    | Error(message) -> Error(message)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match forward(n) with
                        | Ok((_k, first, second)) -> rounds(n - 1)(total + Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second))
                        | Error(_) -> rounds(n - 1)(total)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRecordUpdateOfUnplacedLoopParameterProgram(int rounds)
        => $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            type Fs =
                | declSpan: Maybe(Int)
                | names: List(Str)

            type St =
                | tree: Tree
                | stringLiterals: List(Str)
                | currentSpan: Maybe(Int)
                | functionState: Fs

            type Item =
                | ItemAt(Int, Item)
                | ItemType
                | ItemLet(Str)

            let withDeclSpan value (state: St) =
                (let group = state.functionState
                in state with functionState = (group with declSpan = value))

            let lowerLet (name: Str) (state: St) = state with stringLiterals = Ashes.Collection.List.append(state.stringLiterals)([name])

            let finish (trailing: Int) (state: St) = trailing + Ashes.Collection.List.length(state.stringLiterals)

            let recursive go items trailing seen state =
                match items with
                    | [] -> state |> finish(trailing)
                    | ItemAt(span, inner) :: rest ->
                        (state with currentSpan = Some(span))
                        |> withDeclSpan(Some(span))
                        |> go(inner :: rest)(trailing)(seen)
                    | ItemType :: rest -> go(rest)(trailing)(seen)(state)
                    | ItemLet(name) :: rest ->
                        state
                        |> lowerLet(name)
                        |> go(rest)(trailing)(name :: seen)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + go([ItemAt(1)(ItemLet("x" + Ashes.Text.fromInt(n))), ItemAt(2)(ItemType), ItemLet("y")])(0)([])(St(tree = Node(Leaf)(n)(Leaf), stringLiterals = ["a" + Ashes.Text.fromInt(n), "b"], currentSpan = None, functionState = Fs(declSpan = None, names = []))))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildNormalizedParameterBehindOkRecordUpdateProgram(int rounds)
        => $$"""
            type St =
                | layouts: List(Str)
                | supply: Int

            let register (name: Str) (state: St) =
                match state with
                    | St { supply = supply } -> Ok(state with layouts = [name], supply = supply + 1)

            let recursive rounds (n: Int) (state: St) =
                if n == 0
                then state.supply
                else
                    match register("T" + Ashes.Text.fromInt(n))(state) with
                        | Ok(next) -> rounds(n - 1)(next)
                        | Error(_e) -> 0

            Ashes.IO.print(rounds({{rounds}})(St(layouts = [], supply = 0)))
            """;

    private static string BuildLetBoundListKeptByArenaRecordResultProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type Env =
                | name: Str
                | items: List(Str)

            type Dropper =
                | ids: List((Str, Int))
                | environment: Env

            let recursive namedIds (names: List(Str)) (reversed: List((Str, Int))) =
                match names with
                    | [] -> Ashes.Collection.List.reverse(reversed)
                    | name :: rest -> namedIds(rest)((name, Ashes.Collection.List.length(reversed)) :: reversed)

            let emptyEnv unit = Env(name = "standalone", items = [])

            let prepare (names: List(Str)) =
                (let ids = namedIds(names)([])
                in Dropper(ids = ids, environment = emptyEnv(Unit)))

            let measure (dropper: Dropper) =
                match dropper with
                    | Dropper { ids = ids, environment = Env { items = items } } -> Ashes.Collection.List.length(ids) + Ashes.Collection.List.length(items)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + measure(prepare(["T" + Ashes.Text.fromInt(n), "U"])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildBytesRecordAccumulatorProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type ResolvedPatch =
                | resolvedOffset: Int
                | resolvedBytes: Bytes

            let recursive makePatches (index: Int) (count: Int) (acc: List(ResolvedPatch)) =
                if index >= count
                then Ashes.Collection.List.reverse(acc)
                else makePatches(index + 1)(count)(ResolvedPatch(resolvedOffset = index * 10 + 3, resolvedBytes = Ashes.Byte.allocate(256)) :: acc)

            let recursive totalOffsets (patches: List(ResolvedPatch)) (total: Int) =
                match patches with
                    | [] -> total
                    | ResolvedPatch { resolvedOffset = offset, resolvedBytes = replacement } :: rest -> totalOffsets(rest)(total + offset + Ashes.Byte.length(replacement))

            let recursive repeat (left: Int) (total: Int) =
                if left <= 0
                then total
                else repeat(left - 1)(total + totalOffsets(makePatches(0)(10)([]))(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildFreshHeadOverNamedHeadProgram(int rounds)
        => $$"""
            let recursive build (items: List(Bytes)) (acc: List(Bytes)) =
                match items with
                    | [] -> acc
                    | item :: rest -> build(rest)(Ashes.Byte.allocate(256) :: item :: acc)

            let recursive count (xs: List(Bytes)) (total: Int) =
                match xs with
                    | [] -> total
                    | x :: rest -> count(rest)(total + Ashes.Byte.length(x))

            let source = [Ashes.Byte.allocate(1), Ashes.Byte.allocate(2), Ashes.Byte.allocate(3), Ashes.Byte.allocate(4), Ashes.Byte.allocate(5)]

            let recursive repeat (left: Int) (total: Int) =
                if left <= 0
                then total
                else repeat(left - 1)(total + count(build(source)([]))(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildConsChainBorrowedBytesHeadProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            let recursive sources (n: Int) (acc: List(Bytes)) =
                if n <= 0
                then acc
                else sources(n - 1)(Ashes.Byte.allocate(n) :: acc)

            let recursive pieces (items: List(Bytes)) (acc: List(Bytes)) =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | payload :: rest -> pieces(rest)(Ashes.Byte.allocate(2) :: payload :: Ashes.Byte.allocate(3) :: acc)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + Ashes.Collection.List.length(pieces(sources(20)([]))([])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildPairPushedOntoLoopAccumulatorProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type ResolvedPatch =
                | resolvedOffset: Int
                | resolvedBytes: Bytes

            let sliceBytes (bytes: Bytes) (start: Int) (length: Int) =
                length
                |> Ashes.Byte.subText(bytes)(start)
                |> Ashes.Byte.fromText

            let recursive patchPieces (bytes: Bytes) (patches: List(ResolvedPatch)) (cursor: Int) (acc: List(Bytes)) =
                match patches with
                    | [] -> Ashes.Collection.List.reverse(sliceBytes(bytes)(cursor)(Ashes.Byte.length(bytes) - cursor) :: acc)
                    | ResolvedPatch { resolvedOffset = offset, resolvedBytes = replacement } :: rest ->
                        patchPieces(bytes)(rest)(offset + Ashes.Byte.length(replacement))(replacement :: sliceBytes(bytes)(cursor)(offset - cursor) :: acc)

            let recursive makePatches (index: Int) (count: Int) (acc: List(ResolvedPatch)) =
                if index >= count
                then Ashes.Collection.List.reverse(acc)
                else makePatches(index + 1)(count)(ResolvedPatch(resolvedOffset = index * 10 + 3, resolvedBytes = Ashes.Byte.allocate(4)) :: acc)

            let recursive totalLength (pieces: List(Bytes)) (total: Int) =
                match pieces with
                    | [] -> total
                    | piece :: rest -> totalLength(rest)(total + Ashes.Byte.length(piece))

            let bytes = Ashes.Byte.allocate(210)

            let patches = makePatches(0)(20)([])

            let recursive repeat (left: Int) (total: Int) =
                if left <= 0
                then total
                else repeat(left - 1)(total + totalLength(patchPieces(bytes)(patches)(0)([]))(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildGenericMapPassedAsValueProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type Sem =
                | SemLeaf(Int)
                | SemNamed(Int, List(Sem))

            let renumber (shift: Int) (value: Sem) =
                match value with
                    | SemNamed(id, arguments) -> SemNamed(id + shift)(arguments)
                    | other -> other

            let viaMapper mapper xs = mapper(renumber(1))(xs)

            let recursive measure (values: List(Sem)) (total: Int) =
                match values with
                    | [] -> total
                    | SemLeaf(v) :: rest -> measure(rest)(total + v)
                    | SemNamed(id, _arguments) :: rest -> measure(rest)(total + id)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + measure(viaMapper(Ashes.Collection.List.map)([SemLeaf(n), SemNamed(n)([SemLeaf(1)])]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildVariantLoopFieldConsedOntoCopiedAccumulatorProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type Sem =
                | SemInt
                | SemVariable(Int)
                | SemFunction(Sem, Sem, Maybe(Sem))
                | SemNamed(Int, Str, List(Sem))

            let recursive substitute (shift: Int) (semanticType: Sem) =
                match semanticType with
                    | SemVariable(id) -> SemVariable(id + shift)
                    | SemFunction(argument, result, row) -> SemFunction(substitute(shift)(argument))(substitute(shift)(result))(row)
                    | other -> other

            let recursive splitConstructorType semanticType reversed =
                match semanticType with
                    | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
                    | resultType -> (Ashes.Collection.List.reverse(reversed), resultType)

            let nameLength (semanticType: Sem) =
                match semanticType with
                    | SemNamed(_id, name, _arguments) -> Ashes.Text.byteLength(name)
                    | _ -> 0

            let measure (body: Sem) (supply: Int) =
                match splitConstructorType(substitute(supply)(body))([]) with
                    | (fieldTypes, resultType) -> Ashes.Collection.List.length(fieldTypes) + nameLength(resultType)

            let recursive arrows (count: Int) (result: Sem) =
                if count == 0
                then result
                else arrows(count - 1)(SemFunction(SemVariable(count))(result)(None))

            let recursive rounds (n: Int) (body: Sem) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(body)(total + measure(body)(n))

            Ashes.IO.print(rounds({{rounds}})(arrows(6)(SemNamed(0)("Maybe" + "")([SemVariable(1)])))(0))
            """;

    private static string BuildVariantLoopResultBesideSplitListProgram(int rounds)
        => $$"""
            import Ashes.Collection.List

            type Sem =
                | SemInt
                | SemVariable(Int)
                | SemFunction(Sem, Sem, Maybe(Sem))
                | SemNamed(Int, Str, List(Sem))

            type Scheme =
                | quantified: List((Int, Str))
                | body: Sem

            type Instantiation =
                | semanticType: Sem
                | supply: Int

            let recursive substitute (shift: Int) (semanticType: Sem) =
                match semanticType with
                    | SemVariable(id) -> SemVariable(id + shift)
                    | SemFunction(argument, result, row) ->
                        let a = substitute(shift)(argument)
                        in
                            let r = substitute(shift)(result)
                            in SemFunction(a)(r)(row)
                    | SemNamed(symbolId, name, arguments) -> SemNamed(symbolId)(name)(arguments)
                    | other -> other

            let instantiate (scheme: Scheme) (supply: Int) =
                match scheme with
                    | Scheme { body = body } -> Instantiation(semanticType = substitute(supply)(body), supply = supply + 1)

            let recursive splitConstructorType semanticType reversed =
                match semanticType with
                    | SemFunction(parameterType, resultType, _row) -> splitConstructorType(resultType)(parameterType :: reversed)
                    | resultType -> (Ashes.Collection.List.reverse(reversed), resultType)

            let layoutFieldTypes (scheme: Scheme) (supply: Int) =
                match instantiate(scheme)(supply) with
                    | Instantiation { semanticType = semanticType } -> splitConstructorType(semanticType)([])

            let nameLength (semanticType: Sem) =
                match semanticType with
                    | SemNamed(_id, name, _arguments) -> Ashes.Text.byteLength(name)
                    | _ -> 0

            let measure (scheme: Scheme) (supply: Int) =
                match layoutFieldTypes(scheme)(supply) with
                    | (fieldTypes, resultType) -> Ashes.Collection.List.length(fieldTypes) + nameLength(resultType)

            let recursive rounds (n: Int) (scheme: Scheme) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(scheme)(total + measure(scheme)(n))

            let scheme = Scheme(quantified = [(1, "a")], body = SemFunction(SemVariable(1))(SemNamed(0)("Maybe" + "")([SemVariable(1)]))(None))

            Ashes.IO.print(rounds({{rounds}})(scheme)(0))
            """;

    private static string BuildConsumedListHeadKeptByTupleResultProgram(int rounds)
        => $$"""
            let splitQualified (parts: List(Str)) =
                match parts with
                    | [] -> ("", "")
                    | name :: _ -> ("x", name)

            let splitRest (parts: List(Str)) =
                match parts with
                    | [] -> ("", [])
                    | name :: rest -> (name, rest)

            let recursive build (n: Int) (acc: List(Str)) =
                if n == 0
                then acc
                else build(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive count (xs: List(Str)) (total: Int) =
                match xs with
                    | [] -> total
                    | x :: rest -> count(rest)(total + Ashes.Text.byteLength(x))

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let named =
                        match splitQualified(build(3)([])) with
                            | (a, b) -> Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b)
                    in
                        match splitRest(build(4)([])) with
                            | (a, rest) -> rounds(n - 1)(total + named + Ashes.Text.byteLength(a) + count(rest)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOwnedValuesBehindForwardedContractResultProgram(int rounds)
        => $$"""
            type St =
                | items: List(Str)
                | n: Int

            type Fresh =
                | state: St
                | temp: Int

            type Lowered =
                | state: St
                | ok: Bool

            let wide = "{{new string('w', 480)}}"

            let freshTemp (state: St) =
                match state with
                    | St { items = items, n = n } -> Fresh(state = St(items = items, n = n + 1), temp = n)

            let loaded pattern lower fresh =
                match fresh with
                    | Fresh { state = state, temp = temp } -> lower(pattern)(temp)(state)

            let loadField pattern lower (state: St) =
                state
                |> freshTemp
                |> (given (fresh) ->
                    match fresh with
                        | Fresh { state = St { items = items, n = n }, temp = temp } -> Fresh(state = St(items = wide :: items, n = n), temp = temp))
                |> loaded(pattern)(lower)

            let lowerPattern (pattern: Str) (temp: Int) (state: St) = Lowered(state = state, ok = temp > 0)

            let recursive rounds (remaining: Int) (total: Int) =
                if remaining == 0
                then total
                else
                    match loadField("p")(lowerPattern)(St(items = [wide + Ashes.Text.fromInt(remaining)], n = remaining)) with
                        | Lowered { state = St { n = n } } -> rounds(remaining - 1)(total + n)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildTupleResultHoldsOwnedRecordFieldProgram(int rounds)
        => $$"""
            type State =
                | items: List(Str)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let step (s: State) = State(items = wide + Ashes.Text.fromInt(s.count) :: s.items, count = s.count + 1)

            let summarize (s: State) =
                let next = step(s)
                in (next.items, next.count > 2)

            let recursive countAll (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match summarize(State(items = [wide + "x", wide + "y"], count = n)) with
                        | (texts, _big) -> rounds(n - 1)(total + countAll(texts)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRecursivePhantomListResultProgram(int rounds)
        => $$"""
            type Node =
                | Leaf(Str)
                | Group(List(Node))

            type Derived =
                | pattern: Node
                | fields: List(Str)

            let wide = "{{new string('w', 480)}}"

            let derive (n: Int) = Derived(pattern = Group([Leaf(wide + Ashes.Text.fromInt(n))]), fields = [wide + Ashes.Text.fromInt(n)])

            let recursive cases (n: Int) =
                if n == 0
                then []
                else
                    match derive(n) with
                        | Derived { pattern = p, fields = f } -> (Group([p]), f, None) :: cases(n - 1)

            let recursive countAll (values: List((Node, List(Str), Maybe(Int)))) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countAll(cases(3))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildForwardedGenericTupleResultProgram(int rounds)
        => $$"""
            type Item =
                | name: Str
                | n: Int

            type Box =
                | items: List(Item)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (k: Int) = [Item(name = wide + Ashes.Text.fromInt(k), n = k)]

            let makeBox (k: Int) = Box(items = make(k + 1), count = k)

            let pick value (k: Int) =
                let built = makeBox(k)
                in
                    if k > 2
                    then (value, make(k))
                    else (value, built.items)

            let forward value (k: Int) = pick(value)(k + 1)

            let recursive countAll (values: List(Item)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match forward(n)(n - n / 5 * 5) with
                        | (_m, items) -> rounds(n - 1)(total + countAll(items)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericTupleResultThroughClosureProgram(int rounds)
        => $$"""
            type Item =
                | name: Str
                | n: Int

            type Box =
                | items: List(Item)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (k: Int) = [Item(name = wide + Ashes.Text.fromInt(k), n = k)]

            let makeBox (k: Int) = Box(items = make(k + 1), count = k)

            let pick value (k: Int) =
                let built = makeBox(k)
                in
                    if k > 2
                    then (value, make(k))
                    else (value, built.items)

            let apply f (k: Int) = f(k)(k)

            let recursive countAll (values: List(Item)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match apply(pick)(n - n / 5 * 5) with
                        | (_m, items) -> rounds(n - 1)(total + countAll(items)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildDemotedFieldsBesideGenericValueProgram(int rounds)
        => $$"""
            type Derived =
                | pattern: List(Str)
                | fields: List(Str)

            let wide = "{{new string('w', 480)}}"

            let derive (n: Int) = Derived(pattern = [wide + Ashes.Text.fromInt(n)], fields = [wide + Ashes.Text.fromInt(n)])

            let tagged value (n: Int) =
                match derive(n) with
                    | Derived { pattern = p, fields = f } -> [(value, f), (value, p)]

            let recursive countAll (values: List((Int, List(Str)))) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + countAll(tagged(n)(n))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildConsumedListHeadKeptBySuccessorProgram(int rounds)
        => $$"""
            let recursive setAssociation key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: setAssociation(key)(value)(tail)

            let recursive addCases (cs: List(Str)) entries =
                match cs with
                    | [] -> entries
                    | label :: cTail -> addCases(cTail)(setAssociation(label)(1)(entries))

            let recursive total (refs: List((Str, Int))) (sum: Int) =
                match refs with
                    | [] -> sum
                    | (_, c) :: tail -> total(tail)(sum + c)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + total(addCases(["a", "b" + Ashes.Text.fromInt(n), "a"])([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildPatternOwnerHandedToConstrainedCallProgram(int rounds)
        => $$"""
            let recursive setAssociation key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: setAssociation(key)(value)(tail)

            let recursive collect (groups: List(Maybe(Str))) acc =
                match groups with
                    | [] -> acc
                    | Some(label) :: tail ->
                        acc
                        |> setAssociation(label)(1)
                        |> collect(tail)
                    | None :: tail -> collect(tail)(setAssociation("e")(2)(acc))

            let recursive total (refs: List((Str, Int))) (sum: Int) =
                match refs with
                    | [] -> sum
                    | (_, c) :: tail -> total(tail)(sum + c)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + total(collect([Some("a"), Some("b" + Ashes.Text.fromInt(n)), None, Some("a"), Some("c")])([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildListLiteralStaticHeadOverBuiltStringProgram(int rounds)
        => $$"""
            let recursive count (items: List(Str)) (n: Int) =
                match items with
                    | [] -> n
                    | _ :: rest -> count(rest)(n + 1)

            let labels (n: Int) = ["c", "d" + Ashes.Text.fromInt(n), "e"]

            let recursive run (n: Int) =
                if n < 0
                then run(n + 1)
                else count(labels(n))(0)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + run(n))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopResultCellHoldingPatternBindingProgram(int rounds)
        => $$"""
            let recursive lookup (key: Str) (table: List((Str, List(Int)))) =
                match table with
                    | [] -> None
                    | (name, entry) :: rest ->
                        if name == key
                        then Some(entry)
                        else lookup(key)(rest)

            let handOff (table: List((Str, List(Int)))) (key: Str) =
                match lookup(key)(table) with
                    | Some(entry) -> Some(entry)
                    | None -> None

            let recursive build (n: Int) (acc: List((Str, List(Int)))) =
                if n == 0
                then acc
                else build(n - 1)(("k" + Ashes.Text.fromInt(n), [n, n + 1, n + 2, n + 3, n + 4, n + 5, n + 6, n + 7, n + 8, n + 9, n + 10, n + 11, n + 12, n + 13, n + 14, n + 15, n + 16, n + 17, n + 18, n + 19]) :: acc)

            let count (table: List((Str, List(Int)))) =
                (match lookup("k1")(table) with
                    | Some(entry) -> Ashes.Collection.List.length(entry)
                    | None -> 0) + (match handOff(table)("k2") with
                    | Some(entry) -> Ashes.Collection.List.length(entry)
                    | None -> 0)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + count(build(3)([])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildOwnerReleasedOnSplitEdgeProgram(int rounds)
        => $$"""
            type Fact =
                | name: Str
                | uses: List(Int)

            let recursive withFacts items acc =
                match items with
                    | [] -> acc
                    | item :: rest -> withFacts(rest)(Fact(name = "a fact whose name is long enough that one leaked copy per round shows in the peak resident set: " + Ashes.Text.fromInt(item), uses = [item, item + 1]) :: acc)

            let recursive addKnown facts known =
                match facts with
                    | [] -> known
                    | Fact { name = name, uses = uses } :: rest ->
                        match uses with
                            | first :: _ ->
                                if first > 1
                                then addKnown(rest)((name, first) :: known)
                                else addKnown(rest)(known)
                            | [] -> addKnown(rest)((name, 0) :: known)

            let recursive total (names: List((Str, Int))) (sum: Int) =
                match names with
                    | [] -> sum
                    | (_, first) :: rest -> total(rest)(sum + first)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + total(addKnown(withFacts([1, 2, n + 3])([]))([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildCopiedSuccessorHeadTupleUnannotatedProgram(int rounds)
        => $$"""
            type Kind =
                | Target(Int, List(Str), Str)
                | Other(Int)

            let recursive collect instructions acc =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | Target(_, _, label) -> collect(tail)((label, 1) :: acc)
                            | _ -> collect(tail)(acc)

            let recursive total (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, c) :: rest -> total(rest)(sum + c)

            let program n = [Target(1)(["a", "b" + Ashes.Text.fromInt(n)])("c"), Other(1), Target(2)([])("ab")]

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + total(collect(program(n))([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildNestedConstrainedHelperListArgumentProgram(int rounds)
        => $$"""
            type Kind =
                | SwitchTag(List(Str), Str)
                | Other(Int)

            let recursive setAssociation key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: setAssociation(key)(value)(tail)

            let recursive countRefs instructions acc =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | SwitchTag(cases, defaultLabel) ->
                                let recursive addCases cs entries =
                                    match cs with
                                        | [] -> entries
                                        | l :: cTail -> addCases(cTail)(setAssociation(l)(1)(entries))
                                in
                                    let mapWithCases = addCases(cases)(acc)
                                    in countRefs(tail)(setAssociation(defaultLabel)(1)(mapWithCases))
                            | _ -> countRefs(tail)(acc)

            let recursive total (refs: List((Str, Int))) (sum: Int) =
                match refs with
                    | [] -> sum
                    | (_, c) :: tail -> total(tail)(sum + c)

            let run n = total(countRefs([SwitchTag(["a", "b" + Ashes.Text.fromInt(n)])("c"), Other(1), SwitchTag([])("a")])([]))(0)

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + run(n))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericCallSuccessorSharingOwnedValueProgram(int rounds)
        => $$"""
            let recursive setAssociation key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: setAssociation(key)(value)(tail)

            let recursive addCases (cs: List(Str)) entries =
                match cs with
                    | [] -> entries
                    | label :: cTail ->
                        entries
                        |> setAssociation(label)(1)
                        |> addCases(cTail)

            let recursive loop (groups: List(Maybe(List(Str)))) acc =
                match groups with
                    | [] -> acc
                    | Some(cases) :: tail ->
                        let mapWithCases = addCases(cases)(acc)
                        in
                            mapWithCases
                            |> setAssociation("d")(1)
                            |> loop(tail)
                    | None :: tail -> loop(tail)(acc)

            let recursive total (refs: List((Str, Int))) (sum: Int) =
                match refs with
                    | [] -> sum
                    | (_, c) :: tail -> total(tail)(sum + c)

            let run (n: Int) =
                (let refs = loop([Some(["a", "b" + Ashes.Text.fromInt(n)]), None, Some([])])([])
                in total(refs)(0) + total(refs)(0))

            let recursive rounds (n: Int) (sum: Int) =
                if n == 0
                then sum
                else rounds(n - 1)(sum + run(n))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildNestedRecursiveHelperResultProgram(int rounds)
        => $$"""
            type Sem =
                | SemVariable(Int)
                | SemName(Str)

            type Supply =
                | nextId: Int

            let fresh (supply: Supply) =
                match supply with
                    | Supply { nextId = nextId } -> (SemVariable(nextId), Supply(nextId = nextId + 1))

            let instantiateAll (quantified: List(Int)) (supply: Supply) =
                let recursive go (items: List(Int)) (current: Supply) (substitution: List((Int, Sem))) =
                    match items with
                        | [] -> (substitution, current)
                        | variableId :: tail ->
                            match fresh(current) with
                                | (freshVariable, nextSupply) -> go(tail, nextSupply, (variableId, freshVariable) :: substitution)
                in go(quantified, supply, [])

            let recursive count (items: List((Int, Sem))) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> count(rest)(total + 1)

            let quantified = [1, 2, 3, 4, 5, 6, 7, 8]

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match instantiateAll(quantified)(Supply(nextId = n)) with
                        | (substitution, Supply { nextId = next }) -> rounds(n - 1)(total + count(substitution)(0) + next % 2)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericTupleResultContractPartProgram(int rounds)
        => $$"""
            type Item =
                | name: Str
                | n: Int

            type Box =
                | items: List(Item)
                | count: Int

            let wide = "{{new string('w', 480)}}"

            let make (k: Int) = [Item(name = wide + Ashes.Text.fromInt(k), n = k)]

            let makeBox (k: Int) = Box(items = make(k + 1), count = k)

            let pick value (k: Int) (supplied: List(Item)) =
                let built = makeBox(k)
                in
                    if k > 3
                    then (value, make(k))
                    else
                        if k > 2
                        then (value, supplied)
                        else
                            if k > 1
                            then (value, [Item(name = wide + Ashes.Text.fromInt(k), n = k)])
                            else (value, built.items)

            let recursive countAll (values: List(Item)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let supplied = make(n + 7)
                    in
                        match pick(n)(n - n / 5 * 5)(supplied) with
                            | (_m, items) -> rounds(n - 1)(total + countAll(items)(0) + countAll(supplied)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildPhantomTupleResultFreshRecordProgram(int rounds)
        => $$"""
            type State =
                | label: Str
                | items: List(Int)

            let wide = "{{new string('w', 480)}}"

            let recursive build (n: Int) (acc: Int) =
                if n == 0
                then (State(label = wide + Ashes.Text.fromInt(acc), items = [acc, acc + 1]), 2, None)
                else build(n - 1)(acc + n)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match build(3)(n) with
                        | (_state, m, _h) -> rounds(n - 1)(total + m)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildTupleStateSuccessorBorrowsPatternTailProgram(int rounds)
        => $$"""
            let recursive parameters (reversed: List(Str)) (state: (List(Str), Int)) =
                match state with
                    | ("=" :: _, _count) -> (reversed, state)
                    | (token :: tail, count) -> parameters(token :: reversed)((tail, count + 1))
                    | ([], _count) -> (reversed, state)

            let recursive countAll (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let wide = "{{new string('w', 480)}}"

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let tokens = [wide + "1", wide + "2", wide + "3", "=", wide + "4"]
                    in
                        match parameters([])((tokens, n)) with
                            | (names, _after) -> rounds(n - 1)(total + countAll(names)(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildChoiceAccumulatorCellBorrowsPatternHeadProgram(int rounds)
        => $$"""
            let recursive enqueue (pending: List(Str)) (back: List(Str)) (index: Int) =
                match pending with
                    | [] -> back
                    | key :: rest ->
                        if index == 1
                        then enqueue(rest)(back)(index + 1)
                        else enqueue(rest)(key :: back)(index + 1)

            let recursive countAll (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let wide = "{{new string('w', 480)}}"

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let keys = [wide + "1", wide + "2", wide + "3", wide + "4"]
                    in rounds(n - 1)(total + countAll(enqueue(keys)([])(0))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildFreshBytesBufferReadByBuiltinProgram(int rounds)
        => $$"""
            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let wide = Ashes.Byte.copyRange(Ashes.Byte.allocate(256))(0)(Ashes.Byte.allocate(256))(0)(256)
                    in rounds(n - 1)(total + Ashes.Byte.length(Ashes.Byte.allocate(512)) + Ashes.Byte.length(wide))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildBytesBufferHandedToItsLoopProgram(int rounds)
        => $$"""
            let recursive fill (buffer: Bytes) (i: Int) =
                if i >= Ashes.Byte.length(buffer)
                then buffer
                else fill(Ashes.Byte.set(buffer)(i)(Ashes.Number.UInt.fromInt(i)))(i + 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    let filled = fill(Ashes.Byte.allocate(64))(0)
                    in rounds(n - 1)(total + Ashes.Number.UInt.toInt(Ashes.Byte.get(filled)(63)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildWorklistBackOnlyRefilledFromHelperProgram(int rounds)
        => $$"""
            let recursive enqueue (pending: List(Str)) (back: List(Str)) =
                match pending with
                    | [] -> back
                    | key :: rest -> enqueue(rest)(key :: back)

            let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> reverseAll(rest)(value :: acc)

            let next (key: Str) =
                if key == "cinder"
                then Some("smoke")
                else
                    if key == "smoke"
                    then Some("soot")
                    else
                        if key == "soot"
                        then Some("cinder")
                        else None

            let recursive solve (count: Int) (front: List(Str)) (back: List(Str)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> count
                            | _ -> solve(count)(reverseAll(back)([]))([])(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then count
                        else
                            match next(key) with
                                | None -> solve(count)(rest)(back)(fuel - 1)
                                | Some(target) -> solve(count + 1)(rest)(enqueue([target])(back))(fuel - 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + solve(0)(["cinder", "ember"])([])(7))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildWorklistFrontRefilledBehindMarkingHelperProgram(int rounds)
        => $$"""
            type Seen =
                | Leaf
                | Node(Seen, Str, Seen)

            let recursive insert (key: Str) (seen: Seen) =
                match seen with
                    | Leaf -> Node(Leaf, key, Leaf)
                    | Node(left, current, right) -> Node(insert(key)(left), current, right)

            let recursive countSeen (seen: Seen) =
                match seen with
                    | Leaf -> 0
                    | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

            let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> reverseAll(rest)(value :: acc)

            let mark (key: Str) = key + "x"

            let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

            let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> countSeen(seen)
                            | _ -> solve(reverseAll(back)([]))([])(seen)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then countSeen(seen)
                        else
                            match enqueue(mark(key))(back)(seen) with
                                | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)(fuel - 1)

            let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf)(10))

            Ashes.IO.print(rounds(["a", "b", "c"])({{rounds}})(0))
            """;

    private static string BuildWorklistFrontBesideQueuedMapProgram(int rounds)
        => $$"""
            import Ashes.Collection.Map.MapTree

            let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> reverseAll(rest)(value :: acc)

            let recursive solve (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> Ashes.Collection.Map.size(queued)
                            | _ -> solve(reverseAll(back)([]))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then Ashes.Collection.Map.size(queued)
                        else
                            match Ashes.Collection.Map.getStr(key)(queued) with
                                | Some(true) -> solve(rest)(back)(queued)(fuel - 1)
                                | _ -> solve(rest)(key + "x" :: back)(Ashes.Collection.Map.setStr(key)(true)(queued))(fuel - 1)

            let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(keys)(n - 1)(total + solve(keys)([])(Ashes.Collection.Map.empty)(10))

            Ashes.IO.print(rounds(["a", "b", "c"])({{rounds}})(0))
            """;

    private static string BuildWorklistPairPastUnrelatedLogSuccessorProgram(int rounds)
        => $$"""
            type Seen =
                | Leaf
                | Node(Seen, Str, Seen)

            let recursive insert (key: Str) (seen: Seen) =
                match seen with
                    | Leaf -> Node(Leaf, key, Leaf)
                    | Node(left, current, right) -> Node(insert(key)(left), current, right)

            let recursive countSeen : Seen -> Int =
                given (seen: Seen) ->
                    match seen with
                        | Leaf -> 0
                        | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

            let recursive reverseAll (values: List(Str)) (acc: List(Str)) =
                match values with
                    | [] -> acc
                    | value :: rest -> reverseAll(rest)(value :: acc)

            let recursive countAll (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | _value :: rest -> countAll(rest)(total + 1)

            let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

            let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) (visited: List(Str)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> countSeen(seen) + countAll(visited)(0)
                            | _ -> solve(reverseAll(back)([]))([])(seen)(visited)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then countSeen(seen) + countAll(visited)(0)
                        else
                            match enqueue(key + "x")(back)(seen) with
                                | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)((key + "!") :: visited)(fuel - 1)

            let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf)([])(10))

            Ashes.IO.print(rounds(["a", "b", "c"])({{rounds}})(0))
            """;

    private static string BuildVariantAccumulatorGrownThroughHelperProgram(int rounds)
        => $$"""
            type Names =
                | Names(List(Str), Int)
                | NoNames

            let add (name: Str) (acc: Names) =
                match acc with
                    | Names(names, count) -> Names(name :: names, count + 1)
                    | NoNames -> Names([name], 1)

            let size (acc: Names) =
                match acc with
                    | Names(_names, count) -> count
                    | NoNames -> 0

            let recursive walk (items: List(Str)) (acc: Names) =
                match items with
                    | [] -> size(acc)
                    | item :: rest ->
                        acc
                        |> add(item)
                        |> walk(rest)

            let items = ["a", "b", "c", "d", "e", "f", "g", "h"]

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + walk(items)(NoNames))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRecordUpdateFreshCallResultFieldProgram(int rounds)
        => $$"""
            type Instruction =
                | InstConst(Int)
                | InstSeq(Instruction, Instruction)

            type State =
                | reversedInstructions: List(Instruction)
                | stringLiterals: List(Str)
                | nextTemp: Int

            let recursive names n acc =
                if n <= 0 then acc
                else names(n - 1)("item" :: acc)

            let recursive countItems items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countItems(tail)(total + 1)

            let step (state: State) = state with stringLiterals = names(40)([]), nextTemp = state.nextTemp + 1

            let initial = State(reversedInstructions = [], stringLiterals = ["a"], nextTemp = 0)

            let recursive repeat times total =
                if times == 0 then total
                else
                    let stepped = step(initial)
                    in repeat(times - 1)(total + stepped.nextTemp + countItems(stepped.stringLiterals)(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private const string SubstitutionPreludeSource = """
        type Sem =
            | SemVariable(Int)
            | SemName(Str)

        type Supply =
            | nextId: Int

        let fresh (supply: Supply) =
            match supply with
                | Supply { nextId = nextId } -> (SemVariable(nextId), Supply(nextId = nextId + 1))

        let recursive count (items: List((Int, Sem))) (total: Int) =
            match items with
                | [] -> total
                | _ :: rest -> count(rest)(total + 1)

        let quantified = [1, 2, 3, 4, 5, 6, 7, 8]

        """;

    private static string BuildSelfCallBesideErrorArmProgram(int rounds)
        => $$"""
            type E =
                | Bad(Int, Str)

            let check (x: Int) =
                if x > 5
                then Error(Bad(x)((if x % 2 == 0 then "big" else "wide") + "!"))
                else Ok(x)

            let recursive go (n: Int) (acc: List(Int)) =
                if n == 0
                then Ok(acc)
                else
                    if n == 100
                    then go(n - 1)(acc)
                    else
                        match check(n) with
                            | Error(error) -> Error(error)
                            | Ok(v) -> go(n - 1)(v :: acc)

            let describe result =
                match result with
                    | Ok(items) ->
                        match items with
                            | [] -> 0
                            | head :: _ -> head
                    | Error(Bad(x, text)) -> if text == "big!" then x else x + 1

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + describe(go(n % 9)([])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildPhantomResultVariableTupleProgram(int rounds)
        => $$"""
            type State =
                | counter: Int
                | items: List(Int)

            let build (n: Int) = State(counter = n, items = [n, n + 1, n + 2])

            let make (n: Int) = (build(n), n + 1, None)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match make(n) with
                        | (state, m, _h) -> rounds(n - 1)(total + state.counter + m)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildArenaListCellWithParameterFieldProgram(int rounds)
        => $$"""
            type Close =
                | closeName: Int
                | closeBindings: List(Str)

            type State =
                | names: List(Str)
                | counter: Int

            let recursive countCloses (items: List(Close)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> countCloses(rest)(total + 1)

            let recursive lowerItems (items: List(Int)) (pending: List(Close)) (state: State) =
                match items with
                    | [] -> state.counter + countCloses(pending)(0)
                    | value :: rest ->
                        if value % 2 == 0
                        then lowerItems(rest)(Close(closeName = value, closeBindings = state.names) :: pending)(state with counter = state.counter + 1)
                        else
                            match pending with
                                | _ :: older -> lowerItems(rest)(older)(state)
                                | [] -> lowerItems(rest)([])(state)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + lowerItems([2, 7, 4, 4])([])(State(names = ["a", "b"], counter = 0)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopRecordCellWithParameterFieldProgram(int rounds)
        => $$"""
            type Close =
                | closeName: Int
                | closeBindings: List(Str)

            type State =
                | names: List(Str)
                | counter: Int

            let recursive countCloses (items: List(Close)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> countCloses(rest)(total + 1)

            let recursive lowerItems (items: List(Int)) (pending: List(Close)) (state: State) =
                match items with
                    | [] -> countCloses(pending)(0) + state.counter + 1
                    | _value :: rest -> lowerItems(rest)(Close(closeName = 0, closeBindings = state.names) :: pending)(state with counter = state.counter - 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + lowerItems([2, 4])([])(State(names = ["cinder", "cinder"], counter = 0)))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopParameterHandedToOptionalHelperProgram(int rounds)
        => $$"""
            type State =
                | slot: Maybe(Int)
                | names: List(Str)

            let headSlotOf (slots: List(Int)) =
                match slots with
                    | slot :: _rest -> Some(slot)
                    | [] -> None

            let restSlotsOf (slots: List(Int)) =
                match slots with
                    | _slot :: rest -> rest
                    | [] -> []

            let recursive loop (items: List(Int)) (slots: List(Int)) (state: State) =
                match items with
                    | [] ->
                        match state.slot with
                            | Some(s) -> s
                            | None -> 0
                    | _ :: rest -> loop(rest)(restSlotsOf(slots))(state with slot = headSlotOf(slots))

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else rounds(n - 1)(total + loop([1, 2, 3, 4, 5, 6])([1, 2, 3, 4, 5, 6])(State(slot = None, names = ["a"])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildArenaLoopParameterRetainedSuccessorProgram(int rounds)
        => SubstitutionPreludeSource + $$"""
            let recursive instantiateQuantifiers quantified supply substitution =
                match quantified with
                    | [] -> (substitution, supply)
                    | variableId :: tail ->
                        match fresh(supply) with
                            | (freshVariable, nextSupply) -> instantiateQuantifiers(tail)(nextSupply)((variableId, freshVariable) :: substitution)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match instantiateQuantifiers(quantified)(Supply(nextId = n))([]) with
                        | (substitution, Supply { nextId = next }) -> rounds(n - 1)(total + count(substitution)(0) + next % 2)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildGenericTupleBesideReferenceCountedElementProgram(int rounds)
        => SubstitutionPreludeSource + $$"""
            let step variableId supply substitution =
                match fresh(supply) with
                    | (freshVariable, nextSupply) -> ((variableId, freshVariable) :: substitution, nextSupply)

            let recursive instantiateQuantifiers (quantified: List(Int)) (supply: Supply) (substitution: List((Int, Sem))) =
                match quantified with
                    | [] -> (substitution, supply)
                    | variableId :: tail ->
                        match step(variableId)(supply)(substitution) with
                            | (nextSubstitution, nextSupply) -> instantiateQuantifiers(tail)(nextSupply)(nextSubstitution)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match instantiateQuantifiers(quantified)(Supply(nextId = n))([]) with
                        | (substitution, Supply { nextId = next }) -> rounds(n - 1)(total + count(substitution)(0) + next % 2)

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildArenaRecordOfDestructuredTuplePartsProgram(int rounds)
        => $$"""
            type Token =
                | text: Str
                | kind: Int

            type alias State = (List(Token), List(Str))

            type TypeExpr =
                | TypeName(Str)
                | TypeApp(Str, List(TypeExpr))

            type Binding =
                | name: Str
                | annotation: Maybe(TypeExpr)
                | requirements: List(TypeExpr)

            let advance (state: State) =
                match state with
                    | (token :: rest, notes) -> (token, (rest, notes))
                    | ([], _notes) -> (Token(text = "", kind = 0), state)

            let kindOf (state: State) =
                match state with
                    | (token :: _, _notes) -> token.kind
                    | ([], _notes) -> 0

            let parseBinding (state: State) =
                match advance(state) with
                    | (name, afterName) ->
                        let header =
                            if kindOf(afterName) == 2
                            then
                                match advance(afterName) with
                                    | (typeToken, afterType) -> (Some(TypeName(typeToken.text)), [TypeApp(typeToken.text)([])], afterType)
                            else (None, [], afterName)
                        in
                            match header with
                                | (annotation, requirements, afterHeader) ->
                                    let binding = Binding(name = name.text, annotation = annotation, requirements = requirements)
                                    in (binding, afterHeader)

            let recursive count (items: List(TypeExpr)) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> count(rest)(total + 1)

            let measure (binding: Binding) =
                match binding.annotation with
                    | Some(TypeName(text)) -> Ashes.Text.byteLength(binding.name + text) + count(binding.requirements)(0)
                    | _ -> 0

            let recursive rounds (tokens: List(Token)) (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    match parseBinding((tokens, [])) with
                        | (binding, _afterBinding) -> rounds(tokens)(n - 1)(total + measure(binding))

            Ashes.IO.print(rounds([Token(text = "f", kind = 0), Token(text = "Int", kind = 2), Token(text = "more", kind = 0)])({{rounds}})(0))
            """;

    private static string BuildFreshOperandOfDerivedEqualityProgram(int rounds)
        => $$"""
            type Kind =
                | Ident
                | If
                | Keyword
                deriving {Eq}

            type Token =
                | kind: Kind
                | position: Int

            type State =
                | tokens: List(Token)
                | index: Int

            let current (state: State) =
                match state.tokens with
                    | token :: _ -> token
                    | [] -> Token(kind = Ident, position = 0)

            let currentKind (state: State) =
                (let token = current(state)
                in token.kind)

            let recursive rounds (state: State) (n: Int) (total: Int) =
                if n == 0
                then total
                else
                    if currentKind(state) != If
                    then rounds(state)(n - 1)(total + 2)
                    else rounds(state)(n - 1)(total + 1)

            let state = State(tokens = [Token(kind = Keyword, position = 1), Token(kind = If, position = 2)], index = 0)

            Ashes.IO.print(rounds(state)({{rounds}})(0))
            """;

    private static string BuildTupleScrutineeLoopProgram(int rounds)
        => $$"""
            type Instruction =
                | InstConst(Int)
                | InstSeq(Instruction, Instruction)

            type State =
                | reversedInstructions: List(Instruction)
                | stringLiterals: List(Str)
                | nextTemp: Int

            type Lowered =
                | state: State
                | temp: Int

            let recursive names n acc =
                if n <= 0 then acc
                else names(n - 1)("item" :: acc)

            let lowerOne (argument: Instruction) (state: State) =
                Lowered(state = (state with stringLiterals = names(40)([]), nextTemp = state.nextTemp + 1), temp = state.nextTemp)

            let recursive countItems items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countItems(tail)(total + 1)

            let recursive lowerAll (arguments: List(Instruction)) (fieldTypes: List(Instruction)) (state: State) (reversedTemps: List(Int)) =
                match (arguments, fieldTypes) with
                    | (argument :: restArguments, fieldType :: restTypes) ->
                        match lowerOne(argument)(state) with
                            | Lowered { state = nextState, temp = temp } -> lowerAll(restArguments)(restTypes)(nextState)(temp :: reversedTemps)
                    | _ -> state.nextTemp + countItems(reversedTemps)(0)

            let initial = State(reversedInstructions = [], stringLiterals = ["a"], nextTemp = 0)

            let arguments = [InstConst(1), InstConst(2), InstConst(3), InstConst(4), InstConst(5), InstConst(6), InstConst(7), InstConst(8)]

            let recursive repeat times total =
                if times == 0 then total
                else repeat(times - 1)(total + lowerAll(arguments)(arguments)(initial)([]))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    // The element carries a string, which makes it one of the general ownership contract's types;
    // the inner loop is a local closure, as in the self-hosted optimizer this shape comes from.
    private static string BuildDifferingShapeAccumulatorProgram(int rounds)
        => $$"""
            type Instruction =
                | InstConst(Int)
                | InstName(Str)
                | InstSeq(Instruction, Instruction)

            let pushAssociation (key: Int) (value: Instruction) (entries: List((Int, Instruction))) = (key, value) :: entries

            let recursive lookupCount (key: Int) (counts: List((Int, Int))) =
                match counts with
                    | [] -> 0
                    | (candidate, count) :: rest ->
                        if candidate == key then count
                        else lookupCount(key)(rest)

            let definedTemps (inst: Instruction) =
                match inst with
                    | InstConst(n) -> [n, n + 1]
                    | InstName(_) -> []
                    | InstSeq(_, _) -> []

            let recursive collect (instructions: List(Instruction)) (defCounts: List((Int, Int))) (acc: List((Int, Instruction))) =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        let recursive addDefs (ds: List(Int)) (entries: List((Int, Instruction))) =
                            match ds with
                                | [] -> entries
                                | d :: dTail ->
                                    if lookupCount(d)(defCounts) == 1
                                    then
                                        entries
                                        |> pushAssociation(d)(inst)
                                        |> addDefs(dTail)
                                    else addDefs(dTail)(entries)
                        in
                            acc
                            |> addDefs(definedTemps(inst))
                            |> collect(tail)(defCounts)

            let recursive countEntries (entries: List((Int, Instruction))) total =
                match entries with
                    | [] -> total
                    | _ :: rest -> countEntries(rest)(total + 1)

            let recursive program n acc =
                if n == 0 then acc
                else program(n - 1)(InstConst(n * 2) :: acc)

            let recursive counts n acc =
                if n == 0 then acc
                else counts(n - 1)((n * 2, 1) :: acc)

            let recursive repeat times total =
                if times == 0 then total
                else
                    let singleDefs = collect(program(20)([]))(counts(20)([]))([])
                    in repeat(times - 1)(total + countEntries(singleDefs)(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildStateBesideVariantParameterProgram(int rounds)
        => $$"""
            type TypeExpr =
                | Named(Str)
                | Arrow(TypeExpr, TypeExpr)

            type Instruction =
                | InstConst(Int)
                | InstSeq(Instruction, Instruction)

            type State =
                | reversedInstructions: List(Instruction)
                | stringLiterals: List(Str)
                | nextTemp: Int

            type Lowered =
                | state: State
                | temp: Int

            type Resolution =
                | resolvedState: State
                | resultType: TypeExpr

            let recursive names n acc =
                if n <= 0 then acc
                else names(n - 1)("item" :: acc)

            let recursive countItems items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countItems(tail)(total + 1)

            let lowerOne (argument: Int) (state: State) =
                Lowered(state = (state with stringLiterals = names(40)([]), nextTemp = state.nextTemp + argument), temp = state.nextTemp)

            let resolveFunctionType (functionType: TypeExpr) (state: State) =
                match functionType with
                    | Arrow(_, result) -> Resolution(resolvedState = (state with nextTemp = state.nextTemp + 1), resultType = result)
                    | Named(_) -> Resolution(resolvedState = state, resultType = functionType)

            let recursive lowerArguments (arguments: List(Int)) (functionType: TypeExpr) (state: State) =
                match arguments with
                    | [] -> state.nextTemp + countItems(state.stringLiterals)(0)
                    | argument :: rest ->
                        match resolveFunctionType(functionType)(state) with
                            | Resolution { resolvedState = functionState, resultType = resultType } ->
                                match lowerOne(argument)(functionState) with
                                    | Lowered { state = nextState } -> lowerArguments(rest)(resultType)(nextState)

            let initial = State(reversedInstructions = [], stringLiterals = ["a"], nextTemp = 0)
            let functionType = Arrow(Named("a"))(Arrow(Named("b"))(Arrow(Named("c"))(Arrow(Named("d"))(Named("r")))))
            let recursive repeat times total =
                if times == 0 then total
                else repeat(times - 1)(total + lowerArguments([1, 1, 1, 1, 1, 1])(functionType)(initial))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildLoopReturningPatternBindingOfParameterProgram(int rounds)
        => $$"""
            type T =
                | Fn(T, T)
                | Named(Int, Str, List(T))
                | Var(Int)

            let recursive resultType (t: T) =
                match t with
                    | Fn(_parameter, result) -> resultType(result)
                    | other -> other

            let recursive seenName (name: Str) (names: List(Str)) =
                match names with
                    | [] -> false
                    | candidate :: rest -> candidate == name || seenName(name)(rest)

            let recursive freeTypes (items: List(T)) (seen: List(Str)) =
                match items with
                    | [] -> []
                    | body :: rest ->
                        match resultType(body) with
                            | Named(_id, name, []) as named ->
                                if seenName(name)(seen)
                                then freeTypes(rest)(seen)
                                else (name, named) :: freeTypes(rest)(name :: seen)
                            | _ -> freeTypes(rest)(seen)

            let recursive countItems (values: List((Str, T))) (total: Int) =
                match values with
                    | [] -> total
                    | _ :: rest -> countItems(rest)(total + 1)

            let items = [Fn(Var(1), Named(0, "Maybe", [Var(1)])), Named(0, "Int", []), Fn(Var(2), Named(0, "Int", [])), Named(0, "List", [Var(3)])]
            let recursive repeat times total =
                if times == 0 then total
                else repeat(times - 1)(total + countItems(freeTypes(items)([]))(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    private static string BuildStepPairingStateWithRewriteProgram(int rounds)
        => $$"""
            type Where =
                | line: Int
                | column: Int

            type Instr =
                | kind: Int
                | target: Int
                | location: Where

            type PassState =
                | cache: List((Int, Int))
                | fresh: List(Int)
                | seen: Int

            let emptyState = PassState(cache = [], fresh = [], seen = 0)

            let step (kind: Int) (target: Int) (location: Where) (state: PassState) =
                match kind with
                    | 0 -> ((state with fresh = target :: state.fresh), Instr(kind = kind, target = target, location = location))
                    | 1 -> ((state with cache = (target, kind) :: state.cache), Instr(kind = 9, target = target, location = location))
                    | _ -> (state, Instr(kind = kind, target = target, location = location))

            let recursive pass (instructions: List(Instr)) (state: PassState) (acc: List(Instr)) =
                match instructions with
                    | [] -> acc
                    | Instr { kind = kind, target = target, location = location } :: tail ->
                        match step(kind)(target)(location)(state) with
                            | (nextState, rewritten) -> pass(tail)(nextState)(rewritten :: acc)

            let recursive build (n: Int) (acc: List(Instr)) =
                if n == 0 then acc
                else build(n - 1)(Instr(kind = n - n / 4 * 4, target = n, location = Where(line = n, column = 1)) :: acc)

            let recursive weigh (xs: List(Instr)) (total: Int) =
                match xs with
                    | [] -> total
                    | Instr { kind = kind, target = target, location = location } :: rest -> weigh(rest)(total + kind + target + location.line)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0 then total
                else rounds(n - 1)(total + weigh(pass(build(8)([]))(emptyState)([]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopMatchingPartsOfHelperPairProgram(int rounds)
        => $$"""
            type Seen =
                | Leaf
                | Node(Seen, Str, Seen)

            let recursive insert (key: Str) (seen: Seen) =
                match seen with
                    | Leaf -> Node(Leaf, key, Leaf)
                    | Node(left, current, right) -> Node(insert(key)(left), current, right)

            let recursive countSeen (seen: Seen) =
                match seen with
                    | Leaf -> 0
                    | Node(left, _current, right) -> countSeen(left) + 1 + countSeen(right)

            let recursive countItems (values: List(Str)) (total: Int) =
                match values with
                    | [] -> total
                    | _ :: rest -> countItems(rest)(total + 1)

            let enqueue (key: Str) (back: List(Str)) (seen: Seen) = (key :: back, insert(key)(seen))

            let recursive solve (front: List(Str)) (back: List(Str)) (seen: Seen) =
                match front with
                    | [] -> countSeen(seen) + countItems(back)(0)
                    | key :: rest ->
                        match enqueue(key + "x")(back)(seen) with
                            | (nextBack, nextSeen) -> solve(rest)(nextBack)(nextSeen)

            let recursive rounds (keys: List(Str)) (n: Int) (total: Int) =
                if n == 0 then total
                else rounds(keys)(n - 1)(total + solve(keys)([])(Leaf))

            Ashes.IO.print(rounds(["a", "b", "c", "d", "e", "f"])({{rounds}})(0))
            """;

    private static string BuildRecordUpdateHandedToHelperProgram(int rounds)
        => $$"""
            type Inner =
                | span: Maybe(Int)
                | names: List(Str)

            type State =
                | inner: Inner
                | count: Int
                | items: List(Str)

            let withSpan (value: Maybe(Int)) (state: State) =
                let group = state.inner
                in state with inner = (group with span = value)

            let recursive walk (n: Int) (state: State) =
                if n == 0 then state.count
                else
                    (state with count = state.count + 1)
                    |> withSpan(Some(n))
                    |> walk(n - 1)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0 then total
                else rounds(n - 1)(total + walk(20)(State(inner = Inner(span = None, names = ["a"]), count = 0, items = ["b"])))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildLoopResultTupleHoldingStateProgram(int rounds)
        => $$"""
            type Instruction =
                | InstConst(Int)
                | InstSeq(Instruction, Instruction)

            type State =
                | reversedInstructions: List(Instruction)
                | stringLiterals: List(Str)
                | nextTemp: Int

            let recursive names n acc =
                if n <= 0 then acc
                else names(n - 1)("item" :: acc)

            let recursive countItems items total =
                match items with
                    | [] -> total
                    | _ :: tail -> countItems(tail)(total + 1)

            let bindOne (left: Int) (right: Int) (state: State) =
                if left == right
                then ((state with nextTemp = state.nextTemp + 1), None)
                else (state, Some("mismatch"))

            let recursive bindAll (pairs: List((Int, Int))) (state: State) =
                match pairs with
                    | [] -> (state, None)
                    | (left, right) :: rest ->
                        match bindOne(left)(right)(state) with
                            | (failedState, Some(error)) -> (failedState, Some(error))
                            | (bound, None) -> bindAll(rest)(bound)

            let fresh (seed: Int) (state: State) = state with stringLiterals = names(40)([]), nextTemp = seed

            let initial = State(reversedInstructions = [], stringLiterals = ["a"], nextTemp = 0)

            let recursive repeat times total =
                if times == 0 then total
                else
                    match bindAll([])(fresh(1)(initial)) with
                        | (bound, _) -> repeat(times - 1)(total + bound.nextTemp + countItems(bound.stringLiterals)(0))

            Ashes.IO.print(repeat({{rounds}})(0))
            """;

    // A producer whose cons arm is `consArm`, over a record a helper returns.
    private static string BuildRecordSpineProgram(int rounds, string consArm)
        => $$"""
            type Liveness =
                | block: Int
                | liveIn: Bool
                | liveOut: Bool

            let make (b: Int) = Liveness(block = b, liveIn = true, liveOut = false)

            let recursive step (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | block :: rest ->
                        {{consArm}}

            let recursive countLive (items: List(Liveness)) (total: Int) =
                match items with
                    | [] -> total
                    | Liveness { liveIn = true } :: rest -> countLive(rest)(total + 1)
                    | _ :: rest -> countLive(rest)(total)

            let recursive rounds (n: Int) (total: Int) =
                if n == 0 then total
                else rounds(n - 1)(total + countLive(step([0, 1, 2, 3, 4]))(0))

            Ashes.IO.print(rounds({{rounds}})(0))
            """;

    private static string BuildRegionManagedTaskFrameProgram(int iterations)
        => $$"""
            let runOne n =
                match Ashes.Task.run(async(match await async n with
                    | Ok(value) -> value
                    | Error(_) -> 0)) with
                    | Ok(value) -> value
                    | Error(_) -> 0

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + runOne(n))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let values = ["a", "b", "c"] in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            if head == "a"
                            then loop(n - 1)(total + 1)
                            else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcRebuiltListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List(Int) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)([n, n + 1])

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildRuntimeRcSharedSpineListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List(Int) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)(n :: values)

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildRuntimeRcStringHeadListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List(Str) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)("value" :: values)

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildRuntimeRcNestedListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List(List(Int)) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)([n] :: values)

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildRuntimeRcListPatternTransferMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List(Str) -> Str -> Int = given n -> given total -> given values -> given current ->
                if n <= 0 then total
                else
                    match values with
                        | [] -> loop(n - 1)(total + 4)(["next"])(current)
                        | head :: _ -> loop(n - 1)(total + Ashes.Text.byteLength(head))(["next"])(head)

            Ashes.IO.print(loop({{iterations}})(0)(["seed"])("initial"))
            """;

    private static string BuildRuntimeRcNilTailPatternTransferProgram()
        => """
            let recursive reverse : List(Str) -> List(Str) -> List(Str) = given acc -> given rest ->
                match rest with
                    | [] -> acc
                    | head :: tail -> reverse(head :: acc)(tail)

            match reverse([])(["x"]) with
                | [] -> Ashes.IO.print(0)
                | head :: _ -> Ashes.IO.print(Ashes.Text.byteLength(head))
            """;

    private static string BuildRuntimeRcTupleHeadBuilderResultProgram()
        => """
            let recursive build : Int -> List((Str, Int)) -> List((Str, Int)) = given n -> given acc ->
                if n <= 0 then acc
                else build(n - 1)(("x", n) :: acc)

            let recursive consume : List((Str, Int)) -> Int -> Int = given values -> given total ->
                match values with
                    | [] -> total
                    | (_, value) :: tail -> consume(tail)(total + value)

            Ashes.IO.print(consume(build(1)([]))(0))
            """;

    private static string BuildRuntimeRcConsumedTupleHeadListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive build : Int -> List((Str, Int)) -> List((Str, Int)) = given n -> given values ->
                if n <= 0 then values
                else build(n - 1)(("x", n) :: values)

            let recursive consume : List((Str, Int)) -> Int -> Int = given values -> given total ->
                match values with
                    | [] -> total
                    | (text, value) :: tail ->
                        consume(tail)(total + Ashes.Text.byteLength(text) + value)

            let recursive reverse : List((Str, Int)) -> List((Str, Int)) -> List((Str, Int)) = given values -> given reversed ->
                match values with
                    | [] -> reversed
                    | head :: tail -> reverse(tail)(head :: reversed)

            let recursive loop : Int -> Int -> Int = given n -> given total ->
                if n <= 0 then total
                else loop(n - 1)(total + consume(reverse(build(4)([]))([]))(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTupleHeadListTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> List((Int, Str)) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)((n, "value") :: values)

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildRuntimeRcUnreturnedTcoParameterMemoryProgram(int iterations)
        => $$"""
            let recursive build : Int -> List(Int) -> List(Int) -> List(Int) =
                given n -> given returned -> given discarded ->
                    if n <= 0 then returned
                    else build(n - 1)(n :: returned)(n :: discarded)

            let recursive consume : List(Int) -> Int -> Int = given values -> given total ->
                match values with
                    | [] -> total
                    | value :: tail -> consume(tail)(total + 1)

            let recursive loop : Int -> Int -> Int = given n -> given total ->
                if n <= 0 then total
                else loop(n - 1)(total + consume(build(64)([])([]))(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildPatternOwnedCallArgumentArenaAggregateMemoryProgram(int iterations)
        => $$"""
            type State =
                | S(List(Int), List(Int))

            let recursive setAt i value values =
                match values with
                    | [] -> []
                    | head :: tail ->
                        if i == 0
                        then value :: tail
                        else head :: setAt(i - 1)(value)(tail)

            let recursive resetCounts r count =
                if r == 1
                then count
                else resetCounts(r - 1)(setAt(r - 1)(r)(count))

            let identity value = value

            let recursive loop remaining state total =
                match state with
                    | S(perm, count) ->
                        let count1 = resetCounts(4)(count)
                        in
                            let next = identity(S(perm)(count1))
                            in
                                if remaining <= 0
                                then total
                                else loop(remaining - 1)(next)(total + 1)

            Ashes.IO.print(loop({{iterations}})(S([1, 2, 3, 4, 5, 6, 7, 8])([0, 0, 0, 0, 0, 0, 0, 0]))(0))
            """;

    // A runtime-managed successor state extracted from a match on a call result and passed by
    // name to the tail self-call. The argument is retained for the next iteration and the
    // extracted binding must be released at the back-edge; marking it as moved instead leaves the
    // whole permutation graph one reference too high on every iteration.
    private static string BuildMatchFieldSuccessorTcoArgumentMemoryProgram(int iterations)
        => $$"""
            type State =
                | S(List(Int), List(Int))

            type Step =
                | Done
                | Continue(State, Int)

            let recursive setAt i value values =
                match values with
                    | [] -> []
                    | head :: tail ->
                        if i == 0
                        then value :: tail
                        else head :: setAt(i - 1)(value)(tail)

            let advance r state =
                match state with
                    | S(perm, count) -> Continue(S(perm)(setAt(0)(r)(count)))(r)

            let recursive loop remaining state total =
                if remaining <= 0
                then total
                else
                    match advance(remaining)(state) with
                        | Done -> total
                        | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

            Ashes.IO.print(loop({{iterations}})(S([1, 2, 3, 4, 5, 6, 7, 8])([0, 0, 0, 0, 0, 0, 0, 0]))(0))
            """;

    // A runtime-managed list passed by name to a callee whose result may keep it (flip returns the
    // un-flipped suffix of its argument) is retained for that result; the caller copies the list
    // result out, so nothing of the argument survives in it and the retained reference must be
    // released after the call, or every flip leaks a whole permutation.
    private static string BuildListArgumentKeptByCopiedCalleeResultMemoryProgram(int iterations)
        => $$"""
            let recursive appendTail acc xs =
                match acc with
                    | [] -> xs
                    | h :: t -> h :: appendTail(t)(xs)

            let recursive flipInto k xs acc =
                if k == 0
                then appendTail(acc)(xs)
                else
                    match xs with
                        | [] -> appendTail(acc)(xs)
                        | h :: t -> flipInto(k - 1)(t)(h :: acc)

            let flip k xs = flipInto(k)(xs)([])

            let recursive countFlips perm flips =
                match perm with
                    | [] -> flips
                    | h :: _ ->
                        if h == 1
                        then flips
                        else countFlips(flip(h)(perm))(flips + 1)

            let recursive run i total =
                if i <= 0
                then total
                else run(i - 1)(total + countFlips([2, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, i % 7 + 12])(0))

            Ashes.IO.print(run({{iterations}})(0))
            """;

    // A list matched out of the runtime-managed loop state is stored into an arena-allocated state
    // cell handed to a callee that normalizes its argument on entry. The arena cell releases
    // nothing, so the store borrows the list and keeps its owner alive across the call instead of
    // retaining a reference the cell can never give back.
    private static string BuildMatchedHeadStoredIntoArenaStateForNormalizingCalleeMemoryProgram(int iterations)
        => $$"""
            type State =
                | S(List(Int), List(Int))

            type Step =
                | Done
                | Continue(State, Int)

            let recursive setAt i value values =
                match values with
                    | [] -> []
                    | head :: tail ->
                        if i == 0
                        then value :: tail
                        else head :: setAt(i - 1)(value)(tail)

            let recursive reset r count =
                if r == 1
                then count
                else reset(r - 1)(setAt(r - 1)(r)(count))

            let advance r state =
                match state with
                    | S(perm, count) -> Continue(S(perm)(count))(r)

            let recursive loop remaining state total =
                match state with
                    | S(perm, count) ->
                        let count1 = reset(3)(count)
                        in
                            if remaining <= 0
                            then total
                            else
                                match advance(remaining)(S(perm)(count1)) with
                                    | Done -> total
                                    | Continue(next, r) -> loop(remaining - 1)(next)(total + 1)

            Ashes.IO.print(loop({{iterations}})(S([1, 2, 3, 4, 5, 6, 7, 8])([0, 0, 0, 0, 0, 0, 0, 0]))(0))
            """;

    // A non-tail producer rebuilds a list of variants carrying a string every round. The list's
    // element has no arena copy-out strategy, so the producer must hand back a reference-counted
    // spine: an arena result would leave the call's window open and the consumed input unreleased,
    // one whole list per round.
    private static string BuildStringAdtListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bump(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // The element carries another variant, read out of the matched cell and stored into the
    // rebuilt one: the reference-counted parent takes an owned clone of it, so the rebuilt list is
    // reference-counted like the string-carrying one.
    private static string BuildNestedVariantListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inner =
                | I(Str, Int)

            type Inst =
                | Add(Int, Int)
                | Name(Inner, Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name(I("x", n), n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bump(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // The element carries a list of strings instead of a nested variant.
    private static string BuildListFieldVariantListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(List(Str), Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name(["x"], n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bump(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // A pass that returns its parameter unchanged on one arm and a rebuilt list on the other:
    // the parameter arm copies the list into an owned graph, so the pass's result is
    // reference-counted either way and the loop releases the previous list every round.
    private static string BuildPassthroughOrFreshResultPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let bumpIf (flag: Int) (insts: List(Inst)) =
                if flag == 0
                then insts
                else bump(insts)

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bumpIf(remaining - (remaining / 2) * 2)(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // The element carries an optional string: the builtin variant is an owned child like a user
    // variant, so the rebuilt list is reference-counted and the previous round's list is released.
    private static string BuildOptionalStringVariantListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Loc(Maybe(Str), Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Loc(Some("x"), n) :: Loc(None, n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Loc(loc, k) :: tail -> Loc(loc, k + 1) :: bump(tail)

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bump(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // The accumulate-and-reverse producer: the loop's reference-counted accumulator is handed to
    // the generic reverse, whose arena result is deep-copied at the call, so the accumulator
    // reference handed over for the result is released where the copy ran.
    private static string BuildAccumulateAndReverseProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let reverse xs =
                (let recursive go acc rest =
                    match rest with
                        | [] -> acc
                        | head :: tail -> go(head :: acc)(tail)
                in go([])(xs))

            let recursive bumpInto (insts: List(Inst)) (acc: List(Inst)) =
                match insts with
                    | [] -> reverse(acc)
                    | Add(a, b) :: tail -> bumpInto(tail)(Add(a + 1, b) :: acc)
                    | Name(s, k) :: tail -> bumpInto(tail)(Name(s, k + 1) :: acc)

            let bump (insts: List(Inst)) = bumpInto(insts)([])

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bump(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // A record whose fields are a string and a list of string-bearing variants, rebuilt by a
    // non-tail producer every round (the shape of the self-hosted optimizer.s per-function map).
    // The record head must be reference-counted under the runtime-list request, with the borrowed
    // label copied into an owned string, or the list of records stays an arena value whose call
    // windows are never restored: one whole list of records retained per round.
    private static string BuildRecordHeadListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            type Fn =
                | label: Str
                | insts: List(Inst)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let recursive buildFns (n: Int) (acc: List(Fn)) =
                if n == 0
                then acc
                else buildFns(n - 1)(Fn(label = "f", insts = build(8)([])) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let optFn (fn: Fn) =
                match fn with
                    | Fn { label = label, insts = insts } -> Fn(label = label, insts = bump(insts))

            let recursive mapFns (fns: List(Fn)) =
                match fns with
                    | [] -> []
                    | fn :: rest -> optFn(fn) :: mapFns(rest)

            let recursive loop (remaining: Int) (fns: List(Fn)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(mapFns(fns))(total + 1)

            Ashes.IO.print(loop({{iterations}})(buildFns(4)([]))(0))
            """;

    // The same producer reading the record.s fields by name and storing a let-bound list.
    private static string BuildRecordHeadFieldReadListProducerPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            type Fn =
                | label: Str
                | insts: List(Inst)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let recursive buildFns (n: Int) (acc: List(Fn)) =
                if n == 0
                then acc
                else buildFns(n - 1)(Fn(label = "f", insts = build(8)([])) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let optFn (fn: Fn) =
                let bumped = bump(fn.insts)
                in Fn(label = fn.label, insts = bumped)

            let recursive mapFns (fns: List(Fn)) =
                match fns with
                    | [] -> []
                    | fn :: rest -> optFn(fn) :: mapFns(rest)

            let recursive loop (remaining: Int) (fns: List(Fn)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(mapFns(fns))(total + 1)

            Ashes.IO.print(loop({{iterations}})(buildFns(4)([]))(0))
            """;

    // A callee's reference-counted result taken apart by field reads and reassembled into another
    // record: both lists are retained into the new cell so they outlive the callee result's own
    // release, and nothing releases those retained references unless the new record is itself
    // reference-counted. Reading the same two fields through a `match` arm instead already
    // plateaued, which is what made the field-read spelling the whole difference.
    private static string BuildCalleeResultFieldReadsReassembledMemoryProgram(int iterations)
        => $$"""
            type LexerResult =
                | tokens: List(Str)
                | diagnostics: List(Str)

            type Program =
                | items: List(Str)
                | diagnostics: List(Str)

            let recursive rev (remaining: List(Str)) (result: List(Str)) =
                match remaining with
                    | [] -> result
                    | head :: tail -> rev(tail)(head :: result)

            let recursive lexerScan (n: Int) (tokens: List(Str)) (diagnostics: List(Str)) =
                (let next = n - 1
                in
                    if next < 0
                    then LexerResult(tokens = rev(tokens)([]), diagnostics = rev(diagnostics)([]))
                    else lexerScan(next)("tok" + "x" :: tokens)("warn" + "y" :: diagnostics))

            let recursive countOf (values: List(Str)) (acc: Int) =
                match values with
                    | [] -> acc
                    | _ :: tail -> countOf(tail)(acc + 1)

            let parseProgram (n: Int) =
                (let lexed = lexerScan(n)([])([])
                in Program(items = lexed.tokens, diagnostics = lexed.diagnostics))

            let recursive loop (remaining: Int) (total: Int) =
                if remaining <= 0
                then total
                else
                    match parseProgram(64) with
                        | Program { diagnostics = diagnostics } -> loop(remaining - 1)(total + countOf(diagnostics)(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // The producer applied to its own non-tail recursive result: the recursive function's result
    // is promised reference-counted before its body is lowered, so the inner call's result is
    // handed to the producer as an owned value the producer releases.
    private static string BuildRecursivePassthroughOrFreshResultPipelineMemoryProgram(int iterations)
        => $$"""
            type Inst =
                | Add(Int, Int)
                | Name(Str, Int)

            let recursive build (n: Int) (acc: List(Inst)) =
                if n == 0
                then acc
                else build(n - 1)(Name("x", n) :: acc)

            let recursive bump (insts: List(Inst)) =
                match insts with
                    | [] -> []
                    | Add(a, b) :: tail -> Add(a + 1, b) :: bump(tail)
                    | Name(s, k) :: tail -> Name(s, k + 1) :: bump(tail)

            let recursive bumpTimes (p: Int) (insts: List(Inst)) =
                if p == 0
                then insts
                else bump(bumpTimes(p - 1)(insts))

            let recursive loop (remaining: Int) (insts: List(Inst)) (total: Int) =
                if remaining <= 0
                then total
                else loop(remaining - 1)(bumpTimes(2)(insts))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    // The same producer applied through a chain of let-bound intermediates inside each round:
    // every intermediate is released when the next stage has consumed it.
    private static string BuildStringAdtListProducerLetChainMemoryProgram(int iterations)
        => $$"""
            type Item =
                | label: Str
                | count: Int

            let recursive build (n: Int) (acc: List(Item)) =
                if n == 0
                then acc
                else build(n - 1)(Item(label = "x", count = n) :: acc)

            let recursive bump (items: List(Item)) =
                match items with
                    | [] -> []
                    | Item { label = label, count = count } :: tail -> Item(label = label, count = count + 1) :: bump(tail)

            let recursive loop (remaining: Int) (items: List(Item)) (total: Int) =
                if remaining <= 0
                then total
                else
                    let once = bump(items)
                    in
                        let twice = bump(once)
                        in loop(remaining - 1)(bump(twice))(total + 1)

            Ashes.IO.print(loop({{iterations}})(build(8)([]))(0))
            """;

    private static string BuildRuntimeRcRecordHeadListTcoMemoryProgram(int iterations)
        => $$"""
            type Item =
                | text: Str
                | value: Int

            let recursive loop : Int -> Int -> List(Item) -> Int = given n -> given limit -> given values ->
                if n <= 0 then
                    match values with
                        | [] -> limit
                        | _ :: _ -> limit
                else loop(n - 1)(limit)(Item(text = "value", value = n) :: values)

            Ashes.IO.print(loop({{iterations}})({{iterations}})([]))
            """;

    private static string BuildLegacyArenaStringMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let text = "value-" + Ashes.Text.fromInt(n) in
                    if Ashes.Text.byteLength(text) > 0
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaRecordMemoryProgram(int iterations)
        => $$"""
            type Box =
                | text: String
                | value: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let box = Box(text = "value-" + Ashes.Text.fromInt(n), value = 1) in
                    if Ashes.Text.byteLength(box.text) > 0
                    then loop(n - 1)(total + box.value)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaGrowingStringMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Str -> Int = given n -> given text ->
                if n <= 0 then Ashes.Text.byteLength(text)
                else loop(n - 1)(text + "x")

            Ashes.IO.print(loop({{iterations}})(""))
            """;

    private static string BuildRuntimeRcVariableSizeRecyclingProgram(int iterations)
        => $$"""
            let recursive grow : Int -> Str -> Int = given n -> given text ->
                if n <= 0 then Ashes.Text.byteLength(text)
                else grow(n - 1)(text + "x")

            let first = grow({{iterations}})("") in
            let second = grow({{iterations}})("") in
            Ashes.IO.print(first + second)
            """;

    private static string BuildRuntimeRcTupleTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> (Int, Int) -> Int = given n -> given limit -> given pair ->
                if n <= 0 then
                    match pair with
                        | (_, _) -> limit
                else loop(n - 1)(limit)((n, n + 1))

            Ashes.IO.print(loop({{iterations}})({{iterations}})((0, 0)))
            """;

    private static string BuildRuntimeRcCopyAdtTcoMemoryProgram(int iterations)
        => $$"""
            type Pair =
                | Pair(Int, Int)

            let recursive loop : Int -> Int -> Pair -> Int = given n -> given limit -> given pair ->
                if n <= 0 then
                    match pair with
                        | Pair(_, _) -> limit
                else loop(n - 1)(limit)(Pair(n)(n + 1))

            Ashes.IO.print(loop({{iterations}})({{iterations}})(Pair(0)(0)))
            """;

    private static string BuildRuntimeRcOwnedTupleTcoMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> (Str, BigInt, List(Int)) -> Int = given n -> given limit -> given value ->
                if n <= 0 then
                    match value with
                        | (_, _, _) -> limit
                else
                    loop(n - 1)(limit)(("value-" + Ashes.Text.fromInt(n), Ashes.Number.BigInt.fromInt(n), [n, n + 1]))

            Ashes.IO.print(loop({{iterations}})({{iterations}})(("", 0N, [])))
            """;

    private static string BuildRuntimeRcOwnedRecordTcoMemoryProgram(int iterations)
        => $$"""
            type Box =
                | text: Str
                | values: List(Int)

            let recursive loop : Int -> Int -> Box -> Int = given n -> given limit -> given box ->
                if n <= 0 then
                    if Ashes.Text.byteLength(box.text) >= 0 then limit else limit
                else
                    loop(n - 1)(limit)(Box(text = "value-" + Ashes.Text.fromInt(n), values = [n, n + 1]))

            Ashes.IO.print(loop({{iterations}})({{iterations}})(Box(text = "", values = [])))
            """;

    private static string BuildRuntimeRcOwnedVariantTcoMemoryProgram(int iterations)
        => $$"""
            type State =
                | Empty
                | Full(Str, List(Int))

            let recursive loop : Int -> Int -> State -> Int = given n -> given limit -> given state ->
                if n <= 0 then
                    match state with
                        | Empty -> limit
                        | Full(_, _) -> limit
                else
                    loop(n - 1)(limit)(Full("value-" + Ashes.Text.fromInt(n))([n, n + 1]))

            Ashes.IO.print(loop({{iterations}})({{iterations}})(Empty))
            """;

    private static string BuildRuntimeRcCopyRecordTcoProbe()
        => """
            type Point =
                | x: Int
                | y: Int

            let recursive loop : Int -> Point -> Int = given n -> given point ->
                if n <= 0 then point.x + point.y
                else loop(n - 1)(Point(x = n, y = n + 1))

            Ashes.IO.print(loop(1)(Point(x = 0, y = 0)))
            """;

    private static string BuildLegacyArenaBytesMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let left = Ashes.Byte.fromText("value-") in
                    let right = Ashes.Byte.fromText(Ashes.Text.fromInt(n)) in
                    let bytes = Ashes.Byte.appendByte(Ashes.Byte.append(left)(right))(33u8) in
                    if Ashes.Byte.length(bytes) > 0
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaBigIntMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value = 123456789012345678901234567890N * Ashes.Number.BigInt.fromInt(n) in
                    if value > 0N
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcGrowingBigIntMemoryProgram(int iterations)
        => $$"""
            let recursive loop : Int -> Int -> BigInt -> Int = given n -> given limit -> given value ->
                if n <= 0 then limit
                else loop(n - 1)(limit)(value * 2N + 1N)

            Ashes.IO.print(loop({{iterations}})({{iterations}})(1N))
            """;

    private static string BuildLegacyArenaClosureMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let text = "value-" + Ashes.Text.fromInt(n) in
                    let f =
                        if n > 0
                        then given (x) -> if Ashes.Text.byteLength(text) > 0 then x + 1 else x
                        else given (x) -> x
                    in loop(n - 1)(total + f(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    // Keep the closure parameter unannotated: its TFun layout is intentionally learned only after
    // body inference, exercising the post-body TCO placement and retroactive active-slot prologue.
    private static string BuildRuntimeRcTcoClosureMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total text transform =
                if n <= 0
                then total + Ashes.Text.byteLength(transform(text)) - 1
                else loop(n - 1)(total + 1)("x")(given value -> value)

            Ashes.IO.print(loop({{iterations}})(0)("x")(given value -> value))
            """;

    private static string BuildPersistentMapStringUpdateMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.Map

            let compareInt : Int -> Int -> Int = given (left) -> given (right) ->
                if left == right then 0
                else if left < right then -1
                else 1

            let recursive loop i limit map =
                if i > limit then map
                else loop(i + 1)(limit)(Ashes.Collection.Map.setWith(compareInt)(0)(Ashes.Text.fromInt(i))(map))

            let seeded = Ashes.Collection.Map.setWith(compareInt)(0)("seed")(Ashes.Collection.Map.empty)
            let final = loop(1)({{iterations}})(seeded)
            in match Ashes.Collection.Map.getWith(compareInt)(0)(final) with
                | None -> Ashes.IO.print(0)
                | Some(value) -> Ashes.IO.print(value)
            """;

    private static string BuildPersistentHashMapUpdateMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.HashMap

            let recursive loop i limit map =
                if i > limit then map
                else loop(i + 1)(limit)(Ashes.Collection.HashMap.set("fixed")(i)(map))

            let final = loop(1)({{iterations}})(Ashes.Collection.HashMap.empty)
            in match Ashes.Collection.HashMap.get("fixed")(final) with
                | None -> Ashes.IO.print(0)
                | Some(value) -> Ashes.IO.print(value)
            """;

    private static string BuildOneBrcMeasurements(int rows)
    {
        StringBuilder source = new(rows * 11);
        for (int row = 0; row < rows; row += 3)
        {
            source.Append("Alpha;1.0\nBeta;-2.0\nGamma;3.5\n");
        }

        return source.ToString();
    }

    private static async Task<MemoryExecutionResult> MeasureOneBrcMedianPeakRssAsync(
        string exePath,
        string inputPath)
    {
        List<MemoryExecutionResult> repetitions = new(3);
        for (int repetition = 0; repetition < 3; repetition++)
        {
            MemoryExecutionResult sample = await RunLinuxExecutablePeakRssAsync(exePath, [inputPath])
                .ConfigureAwait(false);
            sample.Stdout.ShouldContain("Alpha=1.0/1.0/1.0");
            sample.Stdout.ShouldContain("Beta=-2.0/-2.0/-2.0");
            sample.Stdout.ShouldContain("Gamma=3.5/3.5/3.5");
            repetitions.Add(sample);
        }

        return repetitions.OrderBy(sample => sample.MaxRssKb).ElementAt(1);
    }

    private static string GetRepositoryRoot([CallerFilePath] string callerFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", ".."));

    private static async Task<MemoryExecutionResult> CompileRunWithLinuxLlvmPeakRssAsync(IrProgram ir)
    {
        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(ir);
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_rss_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            return await RunLinuxExecutablePeakRssAsync(exePath).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<MemoryExecutionResult> RunLinuxExecutablePeakRssAsync(
        string exePath,
        IReadOnlyList<string>? arguments = null)
    {
        ProcessStartInfo startInfo = new(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (arguments is not null)
        {
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        LinuxMeasuredExecution result = await RunLinuxMeasuredProcessAsync(startInfo).ConfigureAwait(false);
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.MaxRssKb.ShouldBeGreaterThan(0, "Linux resource wrapper did not report peak RSS");
        return new MemoryExecutionResult(result.Stdout, result.MaxRssKb);
    }

    private static async Task<LinuxMeasuredExecution> RunLinuxMeasuredProcessAsync(ProcessStartInfo startInfo)
    {
        const string usageMarker = "__ASHES_LINUX_USAGE__=";
        const string measureScript = """
            import errno, resource, subprocess, sys, time
            for attempt in range(3):
                try:
                    completed = subprocess.run(sys.argv[1:])
                    break
                except OSError as error:
                    if error.errno != errno.ETXTBSY or attempt == 2:
                        raise
                    time.sleep(0.025)
            usage = resource.getrusage(resource.RUSAGE_CHILDREN)
            print(f"__ASHES_LINUX_USAGE__={usage.ru_maxrss}|{usage.ru_utime + usage.ru_stime}", file=sys.stderr)
            sys.exit(completed.returncode)
            """;
        ProcessStartInfo measuredStartInfo = new("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        measuredStartInfo.ArgumentList.Add("-c");
        measuredStartInfo.ArgumentList.Add(measureScript);
        measuredStartInfo.ArgumentList.Add(startInfo.FileName);
        foreach (string argument in startInfo.ArgumentList)
        {
            measuredStartInfo.ArgumentList.Add(argument);
        }

        using Process process = await TestProcessHelper.StartProcessAsync(measuredStartInfo).ConfigureAwait(false);
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(usageMarker, StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[usageMarker.Length..].Split('|');
            fields.Length.ShouldBe(2);
            long maxRssKb = long.Parse(fields[0], CultureInfo.InvariantCulture);
            double cpuMilliseconds = double.Parse(fields[1], CultureInfo.InvariantCulture) * 1_000.0;
            return new LinuxMeasuredExecution(stdout, stderr, process.ExitCode, maxRssKb, cpuMilliseconds);
        }

        throw new InvalidOperationException($"Python resource wrapper did not report child usage. stderr: {stderr}");
    }

    private static IrProgram ConvertRuntimeRcToArenaBaseline(IrProgram program)
    {
        return program with
        {
            EntryFunction = ConvertFunction(program.EntryFunction),
            Functions = program.Functions.Select(ConvertFunction).ToList()
        };

        static IrFunction ConvertFunction(IrFunction function)
        {
            List<IrInst> instructions = function.Instructions.Select(ConvertInstruction).ToList();
            return function with { Instructions = instructions };
        }

        static IrInst ConvertInstruction(IrInst instruction)
            => instruction switch
            {
                IrInst.Alloc allocation when allocation.RuntimeManaged => allocation with { RuntimeManaged = false },
                IrInst.AllocAdt allocation when allocation.RuntimeManaged => allocation with { RuntimeManaged = false },
                IrInst.RcDup duplicate when duplicate.RuntimeManaged => duplicate with { RuntimeManaged = false },
                IrInst.RcDrop drop when drop.RuntimeManaged => drop with { RuntimeManaged = false },
                IrInst.RcIsUnique unique => new IrInst.LoadConstBool(unique.Target, true) { Location = unique.Location },
                _ => instruction
            };
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram program)
        => program.Functions.Prepend(program.EntryFunction).SelectMany(function => function.Instructions);

    private static async Task<double> CompileAndMeasureMedianCpuTimeAsync(IrProgram program, string expectedOutput)
    {
        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(program);
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_perf_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            _ = await RunAndMeasureCpuTimeAsync(exePath, expectedOutput).ConfigureAwait(false);

            const int sampleCount = 3;
            double[] samples = new double[sampleCount];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = await RunAndMeasureCpuTimeAsync(exePath, expectedOutput).ConfigureAwait(false);
            }

            Array.Sort(samples);
            return samples[samples.Length / 2];
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<double> RunAndMeasureCpuTimeAsync(string exePath, string expectedOutput)
    {
        ProcessStartInfo startInfo = new(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        LinuxMeasuredExecution result = await RunLinuxMeasuredProcessAsync(startInfo).ConfigureAwait(false);
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.Stdout.ShouldBe(expectedOutput);
        return result.CpuMilliseconds;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmLoopbackAsync(string sourceTemplate, Func<TcpClient, Task> handleClientAsync, string host = "127.0.0.1")
    {
        using TcpListener listener = LoopbackPortLease.StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = RunLoopbackServerAsync(listener, handleClientAsync);
        var result = await CompileRunWithLinuxLlvmAsync(source).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        serverException.ShouldBeNull(serverException?.ToString());
        return result;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmTlsLoopbackAsync(
        string sourceTemplate,
        Func<SslStream, Task> handleClientAsync,
        string host = "localhost",
        string? certificateHost = null,
        bool trustServerCertificate = true,
        int expectedClientCount = 1,
        bool allowServerHandshakeFailure = false,
        bool tolerateClientDisconnect = false,
        string? stdin = null)
    {
        using TcpListener listener = LoopbackPortLease.StartListener();
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync(certificateHost ?? host).ConfigureAwait(false);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync, tolerateClientDisconnect);
        IReadOnlyDictionary<string, string>? environmentVariables = trustServerCertificate
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SSL_CERT_FILE"] = tlsHost.TrustCertificatePath
            }
            : null;
        var result = await CompileRunWithLinuxLlvmAsync(source, stdin: stdin, environmentVariables: environmentVariables).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        if (allowServerHandshakeFailure && serverException is not null)
        {
            // The client rejects the certificate mid-handshake: Mbed TLS sends a fatal TLS alert,
            // which the .NET peer surfaces as an AuthenticationException (an abrupt close would
            // surface as an IOException instead).
            (serverException is AuthenticationException or IOException)
                .ShouldBeTrue(serverException.ToString());
        }
        else
        {
            serverException.ShouldBeNull(serverException?.ToString());
        }
        return result;
    }

    private static async Task<Exception?> RunLoopbackServerAsync(TcpListener listener, Func<TcpClient, Task> handleClientAsync)
    {
        try
        {
            using var acceptCts = new CancellationTokenSource(SocketTestConstants.AcceptTimeout);
            using var client = await listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
            client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
            client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
            await handleClientAsync(client).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        byte[] headerTerminator = "\r\n\r\n"u8.ToArray();

        while (total < buffer.Length)
        {
            try
            {
                using var readCts = new CancellationTokenSource(SocketTestConstants.ReadChunkTimeout);
                var count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total >= headerTerminator.Length && buffer.AsSpan(0, total).IndexOf(headerTerminator) >= 0)
                {
                    break;
                }

                if (stream is NetworkStream networkStream && !networkStream.DataAvailable)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (total > 0)
            {
                break;
            }
            catch (IOException) when (total > 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string CreateTempDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        return tmpDir;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    internal readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
    private readonly record struct LinuxMeasuredExecution(
        string Stdout,
        string Stderr,
        int ExitCode,
        long MaxRssKb,
        double CpuMilliseconds);
    private readonly record struct MemoryExecutionResult(string Stdout, long MaxRssKb);
}
