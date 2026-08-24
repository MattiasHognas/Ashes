using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class IrOptimizerTests
{
    // Constant folding tests

    [Test]
    public void Constant_folding_folds_int_addition()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(10 + 32)");
        // After folding, the AddInt(10, 32) should be replaced by LoadConstInt(42)
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 42 })
            .ShouldBeTrue("Expected constant-folded value 42.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt)
            .ShouldBeFalse("AddInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_int_subtraction()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(50 - 8)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 42 })
            .ShouldBeTrue("Expected constant-folded value 42.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.SubInt)
            .ShouldBeFalse("SubInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_int_multiplication()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(6 * 7)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 42 })
            .ShouldBeTrue("Expected constant-folded value 42.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.MulInt)
            .ShouldBeFalse("MulInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_int_division()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(84 / 2)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 42 })
            .ShouldBeTrue("Expected constant-folded value 42.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.DivInt)
            .ShouldBeFalse("DivInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_does_not_fold_division_by_zero()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(42 / 0)");
        // Division by zero should NOT be folded — keep the runtime instruction
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.DivInt)
            .ShouldBeTrue("Division by zero should not be folded at compile time.");
    }

    [Test]
    public void Constant_folding_folds_chained_arithmetic()
    {
        var ir = LowerAndOptimize("Ashes.IO.print(10 + 20 + 12)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 42 })
            .ShouldBeTrue("Expected constant-folded value 42 from chained addition.");
    }

    [Test]
    public void Constant_folding_folds_int_comparison()
    {
        // 10 == 10 folds to a known-true bool (CmpIntEq eliminated); the branch-folding pass
        // also folds away the JumpIfFalse guarding the
        // if-expression, at which point the folded bool itself has no remaining
        // consumer and dead-code elimination removes it too — a stronger result than
        // this test originally checked for (a materialized LoadConstBool), not a
        // regression: the whole conditional collapses to the always-taken arm.
        var ir = LowerAndOptimize("if 10 == 10 then Ashes.IO.print(1) else Ashes.IO.print(0)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.CmpIntEq)
            .ShouldBeFalse("CmpIntEq should be eliminated by constant folding.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.JumpIfFalse)
            .ShouldBeFalse("The always-true condition should fold the branch away entirely.");
    }

    [Test]
    public void DeadCodeEliminationRetainsUnsignedComparisonOperands()
    {
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 7),
            new IrInst.LoadConstInt(1, 8),
            new IrInst.CmpUIntLt(2, 0, 1),
            new IrInst.Return(2),
        ];
        IrFunction function = new("entry", instructions, 0, 3, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.LoadConstInt { Target: 0, Value: 7 }).ShouldBeTrue();
        optimized.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.LoadConstInt { Target: 1, Value: 8 }).ShouldBeTrue();
        optimized.EntryFunction.Instructions.ShouldContain(instruction => instruction is IrInst.CmpUIntLt);
    }

    // Bitwise and shift constant-folding tests

    [Test]
    public void Constant_folding_folds_bitwise_and()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0xFF),
            new IrInst.LoadConstInt(1, 0x0F),
            new IrInst.AndInt(2, 0, 1),
            new IrInst.PrintInt(2),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 0x0F })
            .ShouldBeTrue("Expected constant-folded value 0x0F from 0xFF & 0x0F.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AndInt)
            .ShouldBeFalse("AndInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_bitwise_or()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0xF0),
            new IrInst.LoadConstInt(1, 0x0F),
            new IrInst.OrInt(2, 0, 1),
            new IrInst.PrintInt(2),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 0xFF })
            .ShouldBeTrue("Expected constant-folded value 0xFF from 0xF0 | 0x0F.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.OrInt)
            .ShouldBeFalse("OrInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_bitwise_xor()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0xFF),
            new IrInst.LoadConstInt(1, 0x0F),
            new IrInst.XorInt(2, 0, 1),
            new IrInst.PrintInt(2),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 0xF0 })
            .ShouldBeTrue("Expected constant-folded value 0xF0 from 0xFF ^ 0x0F.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.XorInt)
            .ShouldBeFalse("XorInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_shift_left()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 1),
            new IrInst.LoadConstInt(1, 3),
            new IrInst.ShlInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 8 })
            .ShouldBeTrue("Expected constant-folded value 8 from 1 << 3.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.ShlInt)
            .ShouldBeFalse("ShlInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_shift_left_masks_shift_count_to_63()
    {
        // Shift count 64 is masked to 64 & 63 = 0, so 1 << 64 folds to 1 << 0 = 1.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 1),
            new IrInst.LoadConstInt(1, 64),
            new IrInst.ShlInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 1 })
            .ShouldBeTrue("Shift count 64 should be masked to 0; expected 1 << 0 = 1.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.ShlInt)
            .ShouldBeFalse("ShlInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_folds_shift_right_positive()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 8),
            new IrInst.LoadConstInt(1, 1),
            new IrInst.ShrInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 4 })
            .ShouldBeTrue("Expected constant-folded value 4 from 8 >> 1.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.ShrInt)
            .ShouldBeFalse("ShrInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_shift_right_is_logical_for_negative_inputs()
    {
        // Logical (unsigned) right shift: -1L >> 1 should zero-fill the high bit,
        // producing long.MaxValue, not -1 (which would be an arithmetic shift).
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, -1L),
            new IrInst.LoadConstInt(1, 1),
            new IrInst.ShrInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: long.MaxValue })
            .ShouldBeTrue("Logical right shift of -1 by 1 should produce long.MaxValue (zero-fill high bit).");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.ShrInt)
            .ShouldBeFalse("ShrInt should be eliminated by constant folding.");
    }

    [Test]
    public void Constant_folding_shift_right_masks_shift_count_to_63()
    {
        // Shift count 64 is masked to 64 & 63 = 0, so 8 >> 64 folds to 8 >> 0 = 8.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 8),
            new IrInst.LoadConstInt(1, 64),
            new IrInst.ShrInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 8 })
            .ShouldBeTrue("Shift count 64 should be masked to 0; expected 8 >> 0 = 8.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.ShrInt)
            .ShouldBeFalse("ShrInt should be eliminated by constant folding.");
    }

    // Dead code elimination tests

    [Test]
    public void Dead_code_eliminates_unused_constants_from_folding()
    {
        var unoptimized = Lower("Ashes.IO.print(10 + 32)");
        var optimized = IrOptimizer.Optimize(unoptimized);
        // After constant folding, the original LoadConstInt(10) and LoadConstInt(32)
        // become dead code (their targets are only used by the now-eliminated AddInt).
        // The optimizer should remove them.
        var unoptLoadConsts = unoptimized.EntryFunction.Instructions
            .Count(i => i is IrInst.LoadConstInt);
        var optLoadConsts = optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.LoadConstInt);
        optLoadConsts.ShouldBeLessThan(unoptLoadConsts,
            "Dead LoadConstInt instructions should be eliminated after folding.");
    }

    [Test]
    public void Dead_code_preserves_stores_read_implicitly_by_arena_reset()
    {
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 100),
            new IrInst.LoadConstInt(1, 200),
            new IrInst.StoreLocal(2, 0),
            new IrInst.StoreLocal(3, 1),
            new IrInst.RestoreArenaState(2, 3, 4),
            new IrInst.ReclaimArenaChunks(3, 4),
            new IrInst.RcDrop(0, "Int"),
            new IrInst.Return(0),
        ];
        IrFunction function = new("entry", instructions, 5, 2, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(
            instruction => instruction is IrInst.StoreLocal { Slot: 2 }).ShouldBeTrue();
        optimized.EntryFunction.Instructions.Any(
            instruction => instruction is IrInst.StoreLocal { Slot: 3 }).ShouldBeTrue();
    }

    // Observable behavior preservation tests

    [Test]
    public void Optimized_program_produces_same_output_as_unoptimized_int()
    {
        var source = "Ashes.IO.print(10 + 32)";
        var unoptimized = Lower(source);
        var optimized = IrOptimizer.Optimize(unoptimized);
        // Both should have PrintInt in them — the optimizer must not remove side-effectful instructions
        unoptimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintInt)
            .ShouldBeTrue("Unoptimized should have PrintInt.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintInt)
            .ShouldBeTrue("Optimized should still have PrintInt — side effects are preserved.");
    }

    [Test]
    public void Optimized_program_produces_same_output_as_unoptimized_string()
    {
        var source = "Ashes.IO.print(\"hello\")";
        var unoptimized = Lower(source);
        var optimized = IrOptimizer.Optimize(unoptimized);
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintStr)
            .ShouldBeTrue("Optimized should still have PrintStr — side effects are preserved.");
    }

    // Pass pipeline ordering tests

    [Test]
    public void Optimizer_runs_on_all_functions()
    {
        var source = "let add = given (x) -> given (y) -> x + y in Ashes.IO.print(add(10)(32))";
        var unoptimized = Lower(source);
        var optimized = IrOptimizer.Optimize(unoptimized);
        // All functions should be present (optimizer doesn't remove functions)
        optimized.Functions.Count.ShouldBe(unoptimized.Functions.Count);
    }

    [Test]
    public void Optimizer_preserves_string_literals()
    {
        var source = "Ashes.IO.print(\"hello\")";
        var unoptimized = Lower(source);
        var optimized = IrOptimizer.Optimize(unoptimized);
        optimized.StringLiterals.Count.ShouldBe(unoptimized.StringLiterals.Count);
    }

    [Test]
    public void Optimizer_preserves_program_flags()
    {
        var source = "Ashes.IO.print(42)";
        var unoptimized = Lower(source);
        var optimized = IrOptimizer.Optimize(unoptimized);
        optimized.UsesPrintInt.ShouldBe(unoptimized.UsesPrintInt);
        optimized.UsesPrintStr.ShouldBe(unoptimized.UsesPrintStr);
        optimized.UsesConcatStr.ShouldBe(unoptimized.UsesConcatStr);
        optimized.UsesClosures.ShouldBe(unoptimized.UsesClosures);
    }

    [Test]
    public void Devirtualized_stack_closure_call_retains_environment_lifetime()
    {
        List<IrInst> entryInstructions =
        [
            new IrInst.AllocStack(0, 8),
            new IrInst.MakeClosureStack(1, "callee", 0, 8),
            new IrInst.LoadConstInt(2, 42),
            new IrInst.CallClosure(3, 1, 2),
            new IrInst.Return(3),
        ];
        IrFunction entry = new("entry", entryInstructions, 0, 4, false);
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.PrintInt(0),
                new IrInst.Return(0),
            ],
            2,
            1,
            false);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        IrInst.CallKnown call = optimized.EntryFunction.Instructions
            .OfType<IrInst.CallKnown>()
            .ShouldHaveSingleItem();
        call.EnvironmentIsStackAllocated.ShouldBeTrue(
            "A direct tail jump cannot outlive the caller frame that owns its environment.");
    }

    // closure environment scalarization

    private static (IrFunction Entry, IrFunction Callee) BuildSingleCaptureStackClosureProgram(int capturedValue, int argValue)
    {
        List<IrInst> entryInstructions =
        [
            new IrInst.AllocStack(0, 8),
            new IrInst.LoadConstInt(1, capturedValue),
            new IrInst.StoreMemOffset(0, 0, 1),
            new IrInst.MakeClosureStack(2, "callee", 0, 8),
            new IrInst.LoadConstInt(3, argValue),
            new IrInst.CallClosure(4, 2, 3),
            new IrInst.PrintInt(4),
            new IrInst.Return(4),
        ];
        IrFunction entry = new("entry", entryInstructions, 0, 5, false);
        // Real (non-coroutine) lowered closures read a capture via the dedicated LoadEnv
        // instruction, never via an explicit LoadLocal(_, 0) + LoadMemOffset dereference pair —
        // confirmed via --emit-ir final on an actual .ash closure.
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.LoadLocal(1, 1),
                new IrInst.AddInt(2, 0, 1),
                new IrInst.Return(2),
            ],
            2,
            3,
            true);
        return (entry, callee);
    }

    [Test]
    public void Scalarize_single_capture_stack_closure_removes_environment_allocation()
    {
        (IrFunction entry, IrFunction callee) = BuildSingleCaptureStackClosureProgram(capturedValue: 7, argValue: 3);
        // A non-evaluable outer PrintInt keeps IrCompileTimeEval from folding the whole program to
        // a constant, so the scalarization pattern actually reaches this new pass.
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.AllocStack>().ShouldBeEmpty(
            "The stack environment allocation should be skipped entirely.");
        optimized.EntryFunction.Instructions.OfType<IrInst.StoreMemOffset>().ShouldBeEmpty(
            "There is no environment buffer left to populate.");
        optimized.EntryFunction.Instructions.OfType<IrInst.MakeClosureStack>().ShouldBeEmpty(
            "MakeClosureStack should already be dead-code-eliminated once devirtualized.");

        IrInst.CallKnown call = optimized.EntryFunction.Instructions
            .OfType<IrInst.CallKnown>()
            .ShouldHaveSingleItem();
        call.EnvironmentIsStackAllocated.ShouldBeFalse("No allocation remains to protect.");
        call.FuncLabel.ShouldNotBe("callee", "The original callee must be left untouched; a new scalar-env variant is generated instead.");

        optimized.Functions.ShouldContain(f => string.Equals(f.Label, "callee", StringComparison.Ordinal),
            "The original callee is left completely untouched, in case it is still used elsewhere.");
        IrFunction variant = optimized.Functions
            .Where(f => string.Equals(f.Label, call.FuncLabel, StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        variant.Instructions.OfType<IrInst.LoadEnv>().ShouldBeEmpty(
            "The variant must read the captured value directly from its own local slot 0 instead of an implicit env dereference.");
        variant.Instructions.OfType<IrInst.LoadLocal>().ShouldContain(l => l.Slot == 0,
            "The captured value now arrives as the raw env argument, read directly.");
    }

    [Test]
    public async Task Scalarize_single_capture_stack_closure_preserves_execution_semantics()
    {
        (IrFunction entry, IrFunction callee) = BuildSingleCaptureStackClosureProgram(capturedValue: 7, argValue: 3);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        string output = await RunAsync(IrOptimizer.Optimize(program)).ConfigureAwait(false);

        output.ShouldBe("10\n");
    }

    [Test]
    public void Scalarize_declines_a_two_capture_stack_closure()
    {
        List<IrInst> entryInstructions =
        [
            new IrInst.AllocStack(0, 16),
            new IrInst.LoadConstInt(1, 7),
            new IrInst.StoreMemOffset(0, 0, 1),
            new IrInst.LoadConstInt(2, 9),
            new IrInst.StoreMemOffset(0, 8, 2),
            new IrInst.MakeClosureStack(3, "callee2", 0, 16),
            new IrInst.LoadConstInt(4, 3),
            new IrInst.CallClosure(5, 3, 4),
            new IrInst.PrintInt(5),
            new IrInst.Return(5),
        ];
        IrFunction entry = new("entry", entryInstructions, 0, 6, false);
        IrFunction callee = new(
            "callee2",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.LoadEnv(1, 1),
                new IrInst.AddInt(2, 0, 1),
                new IrInst.LoadLocal(3, 1),
                new IrInst.AddInt(4, 2, 3),
                new IrInst.Return(4),
            ],
            2,
            5,
            true);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.AllocStack>().ShouldHaveSingleItem(
            "A two-field environment is out of this task's single-capture scope and must be left alone.");
        optimized.Functions.ShouldHaveSingleItem().Label.ShouldBe("callee2");
    }

    [Test]
    public void Scalarize_declines_a_capture_that_escapes_the_environment_pointer()
    {
        List<IrInst> entryInstructions =
        [
            new IrInst.AllocStack(0, 8),
            new IrInst.LoadConstInt(1, 7),
            new IrInst.StoreMemOffset(0, 0, 1),
            new IrInst.MakeClosureStack(2, "callee", 0, 8),
            new IrInst.LoadConstInt(3, 3),
            new IrInst.CallClosure(4, 2, 3),
            // The env pointer also escapes to an unrelated print, so it is not eligible: something
            // else in the function still needs it to be a real pointer.
            new IrInst.PrintInt(0),
            new IrInst.PrintInt(4),
            new IrInst.Return(4),
        ];
        IrFunction entry = new("entry", entryInstructions, 0, 5, false);
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.LoadLocal(1, 1),
                new IrInst.AddInt(2, 0, 1),
                new IrInst.Return(2),
            ],
            2,
            3,
            true);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.AllocStack>().ShouldHaveSingleItem(
            "The env pointer escapes to a use besides the store/call pair, so scalarization is unsafe.");
        optimized.Functions.ShouldHaveSingleItem().Label.ShouldBe("callee");
    }

    [Test]
    public void Scalarize_declines_a_callee_that_reads_the_env_pointer_as_a_raw_value()
    {
        (IrFunction entry, _) = BuildSingleCaptureStackClosureProgram(capturedValue: 7, argValue: 3);
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.LoadLocal(1, 1),
                new IrInst.AddInt(2, 0, 1),
                // A raw read of the env slot outside of LoadEnv — e.g. passed on as a genuine
                // pointer for some other purpose this task does not attempt to reason about.
                new IrInst.LoadLocal(3, 0),
                new IrInst.PrintInt(3),
                new IrInst.Return(2),
            ],
            2,
            4,
            true);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.AllocStack>().ShouldHaveSingleItem(
            "A callee that reads its env slot as a raw value outside of LoadEnv must not be scalarized.");
        optimized.Functions.ShouldHaveSingleItem().Label.ShouldBe("callee");
    }

    [Test]
    public async Task Scalarize_handles_a_capture_read_more_than_once_via_separate_LoadEnv_instructions()
    {
        (IrFunction entry, _) = BuildSingleCaptureStackClosureProgram(capturedValue: 7, argValue: 3);
        IrFunction callee = new(
            "callee",
            [
                new IrInst.LoadEnv(0, 0),
                new IrInst.LoadEnv(1, 0),
                new IrInst.LoadLocal(2, 1),
                new IrInst.AddInt(3, 0, 1),
                new IrInst.AddInt(4, 3, 2),
                new IrInst.Return(4),
            ],
            2,
            5,
            true);
        IrProgram program = new(entry, [callee], [], true, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);
        optimized.EntryFunction.Instructions.OfType<IrInst.AllocStack>().ShouldBeEmpty(
            "Two separate LoadEnv reads of the same sole capture are still eligible.");

        string output = await RunAsync(optimized).ConfigureAwait(false);
        output.ShouldBe("17\n");
    }

    // Erased RC marker and resource-cleanup tests

    [Test]
    public void Rc_dup_and_drop_markers_are_erased_by_the_optimizer()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstStr(0, "lbl_hello"),
            new IrInst.RcDup(1, 0),
            new IrInst.PrintStr(1),
            new IrInst.RcDrop(1, "String"),
            new IrInst.Return(1),
        };
        var fn = new IrFunction("entry", instructions, 0, 2, false);
        var program = new IrProgram(fn, [], [new IrStringLiteral("lbl_hello", "hello")], false, false, false, false, false, false);

        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup or IrInst.RcDrop).ShouldBeFalse();
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.PrintStr { Source: 0 }).ShouldBeTrue();
    }

    [Test]
    public void Runtime_rc_dup_and_drop_separated_by_uniqueness_check_are_preserved()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 0),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.Return(3),
        };
        var function = new IrFunction("entry", instructions, 0, 4, false);
        var program = new IrProgram(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(2);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcIsUnique).ShouldBe(1);
    }

    [Test]
    public void Adjacent_runtime_dup_and_drop_of_duplicate_are_fused()
    {
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 3, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup).ShouldBeFalse();
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Adjacent_runtime_dup_and_drop_of_source_transfer_duplicate_ownership()
    {
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.RcIsUnique(2, 1),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 3, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup).ShouldBeFalse();
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique { SourceTemp: 0 }).ShouldBeTrue();
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 0, RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Paper_map_shape_sinks_runtime_dup_out_of_nil_branch()
    {
        // The paper's map shape duplicates the input list for its Cons branch, while the Nil branch
        // immediately drops that ownership. Model the match diamond directly: the duplicate belongs
        // after the tag branch, and the Nil-side drop becomes unnecessary.
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.LoadConstBool(2, true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.JumpIfFalse(2, "else"),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Label("end"),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        int branchIndex = optimized.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.JumpIfFalse);
        int dupIndex = optimized.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.RcDup);
        dupIndex.ShouldBeGreaterThan(branchIndex);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 1, RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Runtime_dup_is_sunk_into_consuming_else_branch()
    {
        // The branch condition is an RcIsUnique check (opaque to constant folding), not a
        // literal — matching the sibling test below (Runtime_dup_is_not_sunk_when_unused_
        // branch_observes_source)'s own established reasoning: a literal condition here would
        // fold the JumpIfFalse to an unconditional Jump, which SimplifyControlFlow
        // then correctly recognizes as a fully redundant branch and removes the "else" label
        // entirely (nothing explicitly jumps to it any more) — a real, correct simplification
        // that just happens to break this test's own label-name-based lookup mechanism, not the
        // compiled program's behavior.
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(2, 0),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.JumpIfFalse(2, "else"),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Label("end"),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        int elseIndex = optimized.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.Label { Name: "else" });
        optimized.EntryFunction.Instructions[elseIndex + 1].ShouldBeOfType<IrInst.RcDup>();
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 1, RuntimeManaged: true }).ShouldBe(1);
    }

    [Test]
    public void Runtime_dup_is_not_sunk_when_unused_branch_observes_source()
    {
        // The branch condition is an RcIsUnique check (opaque to constant folding), not
        // a literal — this test probes SinkRuntimeRcDupsIntoDiamonds' behavior at a
        // genuinely runtime-determined branch, so it must not be foldable away by
        // constant-condition branch folding (a literal `LoadConstBool(2, true)` condition here would collapse
        // the whole branch this test exists to exercise).
        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(2, 0),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.JumpIfFalse(2, "else"),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.RcIsUnique(4, 0),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.Label("end"),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 5, false);
        IrProgram program = new(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        int branchIndex = optimized.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.JumpIfFalse);
        int dupIndex = optimized.EntryFunction.Instructions.FindIndex(inst => inst is IrInst.RcDup);
        dupIndex.ShouldBeLessThan(branchIndex);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 1, RuntimeManaged: true }).ShouldBe(2);
    }

    [Test]
    public void Unique_list_drop_uses_one_runtime_rc_operation()
    {
        IrProgram lowered = Lower("let values = [1, 2, 3] in match values with | [] -> Ashes.IO.print(0) | head :: _ -> Ashes.IO.print(head)");

        IrProgram optimized = IrOptimizer.Optimize(lowered);

        CountRuntimeRcOperations(optimized).ShouldBe(1);
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeFalse();
    }

    [Test]
    public void Unique_tree_root_drop_elides_uniqueness_operation()
    {
        IrProgram lowered = LowerProgram("type Tree = | Leaf | Node(Tree, Int, Tree)\nlet tree = Node(Leaf)(42)(Leaf) in match tree with | Leaf -> Ashes.IO.print(0) | Node(_, value, _) -> Ashes.IO.print(value)");

        IrProgram optimized = IrOptimizer.Optimize(lowered);

        CountRuntimeRcOperations(optimized).ShouldBe(3);
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcIsUnique).ShouldBeFalse();
    }

    [Test]
    public void Stdlib_list_map_shape_erases_markers_but_preserves_runtime_list_ownership()
    {
        IrProgram lowered = LowerProgram("""
            let map : (Int -> Int) -> List(Int) -> List(Int) =
                given (f) ->
                    (let recursive mapGo : List(Int) -> List(Int) =
                        given (xs) ->
                            match xs with
                                | [] -> []
                                | head :: tail -> f(head) :: mapGo(tail)
                    in mapGo)

            let mapped = map(given (x) -> x + 1)([1, 2, 3])
            in match mapped with
                | [] -> Ashes.IO.print(0)
                | head :: _ -> Ashes.IO.print(head)
            """);

        IrProgram optimized = IrOptimizer.Optimize(lowered);

        CountErasedRcOperations(lowered).ShouldBeGreaterThan(0);
        CountErasedRcOperations(optimized).ShouldBe(0);
        CountRuntimeRcOperations(optimized).ShouldBe(3,
            "The optimizer must retain the runtime-managed result list's lifetime operations.");
    }

    [Test]
    public void Resource_cleanup_is_never_erased_by_the_optimizer()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),
            new IrInst.CleanupResource(0, "Socket"),
            new IrInst.Return(0),
        };
        var fn = new IrFunction("entry", instructions, 0, 1, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);

        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(inst => inst is IrInst.CleanupResource { TypeName: "Socket" }).ShouldBeTrue();
    }

    [Test]
    public void Rc_drop_elision_removes_string_marker()
    {
        // String drops are no-ops in codegen (arena handles deallocation).
        // The optimizer should elide them.
        var source = """
            let s = "hello" in Ashes.IO.print(s)
            """;
        var unoptimized = Lower(source);
        unoptimized.EntryFunction.Instructions
            .Any(i => i is IrInst.RcDrop { TypeName: "String" })
            .ShouldBeTrue("Unoptimized IR should have a String Drop.");

        var optimized = IrOptimizer.Optimize(unoptimized);
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.RcDrop { TypeName: "String" })
            .ShouldBeFalse("String Drop should be elided by the optimizer.");
    }

    [Test]
    public void Rc_drop_elision_removes_list_marker()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),   // dummy list ptr
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.RcDrop(1, "List"),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.RcDrop)
            .ShouldBeFalse("List Drop should be elided — not a resource type.");
    }

    [Test]
    public void Rc_drop_elision_removes_plain_marker()
    {
        // String/List/Tuple/ADT drops are arena-reclaimed no-ops and are still elided.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.RcDrop(1, "String"),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.RcDrop)
            .ShouldBeFalse("String Drop should be elided — not a resource type, no cleanup behavior.");
    }

    [Test]
    public void Cleanup_elision_preserves_function_cleanup()
    {
        // Closure (Function) drops must NOT be elided: a closure may carry a resource dropper at
        // closure+24 (set when it captured-and-escaped a resource).
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),   // dummy closure ptr
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.CleanupResource(1, "Function"),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.CleanupResource { TypeName: "Function" })
            .ShouldBeTrue("Function Drop must be preserved — a closure may carry a resource dropper.");
    }

    [Test]
    public void Cleanup_elision_preserves_resource_cleanup()
    {
        // Socket drops must NEVER be elided — they route to TCP close.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),   // dummy socket handle
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.CleanupResource(1, "Socket"),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.CleanupResource { TypeName: "Socket" })
            .ShouldBeTrue("Socket Drop must be preserved — resource types need cleanup.");
    }

    [Test]
    public void Drop_elision_also_removes_dead_load_local()
    {
        // When a Drop is elided, the LoadLocal feeding it should also be
        // removed if its target is only used by the Drop.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 0),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),    // only used by the Drop below
            new IrInst.RcDrop(1, "String"),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        // The LoadLocal for slot 0 was only used by the Drop, so both should be gone.
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { Slot: 0 })
            .ShouldBeFalse("LoadLocal feeding an elided Drop should also be removed.");
    }

    [Test]
    public void Drop_elision_removes_dead_store_local_when_slot_has_no_loads()
    {
        // When the Drop and its LoadLocal are removed, if no other LoadLocal reads
        // from that slot, the StoreLocal is also dead and should be removed.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstStr(0, "lbl_hello"),
            new IrInst.StoreLocal(0, 0),    // only load of slot 0 is the Drop below
            new IrInst.LoadLocal(1, 0),
            new IrInst.RcDrop(1, "String"),
            new IrInst.LoadConstInt(2, 42),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.StoreLocal { Slot: 0 })
            .ShouldBeFalse("StoreLocal to a slot with no remaining loads should be removed.");
    }

    [Test]
    public void Drop_elision_keeps_store_local_when_slot_has_other_loads()
    {
        // If the slot has other LoadLocals besides the one feeding the Drop,
        // the StoreLocal must be preserved.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstStr(0, "lbl_hello"),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),      // used by PrintStr
            new IrInst.PrintStr(1),
            new IrInst.LoadLocal(2, 0),      // used only by the Drop
            new IrInst.RcDrop(2, "String"),
            new IrInst.LoadConstInt(3, 0),
            new IrInst.Return(3),
        };

        var fn = new IrFunction("entry", instructions, 1, 4, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        // Drop and its LoadLocal(2,0) should be removed.
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.RcDrop)
            .ShouldBeFalse("String Drop should be elided.");

        // But StoreLocal and the other LoadLocal must remain.
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.StoreLocal { Slot: 0 })
            .ShouldBeTrue("StoreLocal must remain — slot has other loads.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintStr)
            .ShouldBeTrue("PrintStr must remain — side effect.");
    }

    // Borrow elision tests

    [Test]
    public void Borrow_elision_removes_single_use_borrow()
    {
        // A single-use borrow is elided — the borrow target is remapped
        // to the original source, and the Borrow instruction is removed.
        var source = """
            let s = "hello" in Ashes.IO.print(s)
            """;
        var unoptimized = Lower(source);
        var borrowsBefore = unoptimized.EntryFunction.Instructions
            .Count(i => i is IrInst.Borrow);
        borrowsBefore.ShouldBeGreaterThan(0, "Unoptimized IR should have Borrow instructions.");

        var optimized = IrOptimizer.Optimize(unoptimized);
        var borrowsAfter = optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.Borrow);
        borrowsAfter.ShouldBeLessThan(borrowsBefore,
            "Single-use Borrow instructions should be elided by the optimizer.");
    }

    [Test]
    public void Borrow_elision_removes_copy_type_borrow()
    {
        // Borrows of copy-type temps (produced by LoadConstInt/Float/Bool)
        // are always elidable, regardless of use count.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 42),
            new IrInst.Borrow(1, 0),          // copy-type source → elidable
            new IrInst.PrintInt(1),
            new IrInst.Return(1),
        };

        var fn = new IrFunction("entry", instructions, 0, 2, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeFalse("Borrow of a copy-type constant should be elided.");

        // The PrintInt and Return should now reference temp 0 (the original source)
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintInt { Source: 0 })
            .ShouldBeTrue("PrintInt should be remapped to the original source temp.");
    }

    [Test]
    public void Borrow_elision_resolves_chains()
    {
        // Borrow(t1, t0), Borrow(t2, t1) should resolve t2 → t0.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 42),
            new IrInst.Borrow(1, 0),
            new IrInst.Borrow(2, 1),
            new IrInst.PrintInt(2),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeFalse("Chained borrows of copy-type source should all be elided.");

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.PrintInt { Source: 0 })
            .ShouldBeTrue("PrintInt should be remapped through the chain to the original source.");
    }

    [Test]
    public void Borrow_elision_preserves_multi_use_non_copy_borrow()
    {
        // A borrow whose target is used more than once and whose source is
        // not a copy-type producer should NOT be elided.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstStr(0, "lbl_hello"),  // non-copy type
            new IrInst.Borrow(1, 0),
            new IrInst.PrintStr(1),          // use 1
            new IrInst.PrintStr(1),          // use 2
            new IrInst.Return(1),            // use 3
        };

        var fn = new IrFunction("entry", instructions, 0, 2, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeTrue("Multi-use non-copy borrow should be preserved.");
    }

    [Test]
    public void Borrow_elision_remaps_text_builtin_operand()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstStr(0, "lbl_text"),
            new IrInst.Borrow(1, 0),
            new IrInst.TextUncons(2, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [new IrStringLiteral("lbl_text", "hello")], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeFalse("Single-use borrow feeding TextUncons should be elided.");

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.TextUncons { TextTemp: 0 })
            .ShouldBeTrue("TextUncons should be remapped to the original source temp when the borrow is elided.");
    }

    [Test]
    public void Identity_reduction_borrow_is_swept_by_a_second_ownership_copy_elision_pass()
    {
        // x + 0 reduces to a Borrow(target, x) in ReduceIdentitiesAndStrength (pass 6 of the
        // per-function sequence), which runs after ElideTrivialOwnershipCopies (pass 1) — so
        // without the identity-reduction pass's second call to ElideTrivialOwnershipCopies, this newly introduced
        // Borrow would never be swept within the same Optimize() invocation, even though its
        // target has exactly one use (the eligibility condition ElideTrivialOwnershipCopies
        // already checks for any other single-use Borrow). x itself is deliberately NOT a
        // copy-type-constant producer (it comes from LoadLocal, not LoadConstInt/Float/Bool),
        // so this specifically exercises the single-use elision path, not the already-covered
        // copy-type-source path from the tests above.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadLocal(0, 0),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.AddInt(2, 0, 1),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeFalse("The identity-reduction Borrow (x + 0 -> x) should be swept by the re-run ownership-copy elision pass.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt)
            .ShouldBeFalse("The original AddInt should not survive either (replaced by the now-elided Borrow).");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Return { Source: 0 })
            .ShouldBeTrue("Return should be remapped directly to the original source temp (x), with no leftover copy.");
    }

    // End-to-end optimization correctness

    [Test]
    public async Task Optimized_int_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("Ashes.IO.print(10 + 32)").ConfigureAwait(false);
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Optimized_string_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("Ashes.IO.print(\"hello \" + \"world\")").ConfigureAwait(false);
        stdout.ShouldBe("hello world\n");
    }

    [Test]
    public async Task Optimized_lambda_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("let add = given (x) -> given (y) -> x + y in Ashes.IO.print(add(10)(32))").ConfigureAwait(false);
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Optimized_tail_recursive_program_runs_correctly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = """
            let recursive sum = given (n) -> given (acc) ->
                if n == 0 then acc
                else sum(n - 1)(acc + n)
            in Ashes.IO.print(sum(100)(0))
            """;
        var stdout = await CompileOptimizedAndRunAsync(source).ConfigureAwait(false);
        stdout.ShouldBe("5050\n");
    }

    [Test]
    public async Task Optimized_match_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = """
            match Ashes.IO.File.exists("nonexistent.txt") with
                | Ok(result) -> if result then Ashes.IO.print("yes") else Ashes.IO.print("no")
                | Error(msg) -> Ashes.IO.print(msg)
            """;
        var stdout = await CompileOptimizedAndRunAsync(source).ConfigureAwait(false);
        stdout.ShouldBe("no\n");
    }

    [Test]
    public async Task Optimized_text_uncons_long_string_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = """
            let sample = "{ \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }" in
            match Ashes.Text.uncons(sample) with
                | None -> Ashes.IO.print("none")
                | Some((head, tail)) ->
                    if head == '{'
                    then if tail == " \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
                    then Ashes.IO.print("ok")
                    else Ashes.IO.print("tail")
                    else Ashes.IO.print("head")
            """;

        var stdout = await CompileOptimizedAndRunAsync(source).ConfigureAwait(false);
        stdout.ShouldBe("ok\n");
    }

    // Constant propagation across single-predecessor labels

    [Test]
    public void Constant_propagation_preserves_constants_across_single_predecessor_label()
    {
        // Build IR manually: LoadConstInt(t0, 10), LoadConstInt(t1, 20),
        // JumpIfFalse(bool, else_lbl), ..., Jump(end_lbl), Label(else_lbl),
        // AddInt(t3, t0, t1) — should fold to 30 at else_lbl because it's
        // a single-predecessor label (only the JumpIfFalse targets it).
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 10),
            new IrInst.LoadConstInt(1, 20),
            new IrInst.LoadConstBool(2, false), // condition
            new IrInst.JumpIfFalse(2, "else_0"),
            new IrInst.LoadConstInt(3, 99),     // then branch
            new IrInst.StoreLocal(0, 3),
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            // At this point, t0=10, t1=20 should be known (propagated from JumpIfFalse)
            new IrInst.AddInt(4, 0, 1),         // 10 + 20 = 30 → should fold
            new IrInst.StoreLocal(0, 4),
            new IrInst.Label("end_0"),
            new IrInst.LoadLocal(5, 0),
            new IrInst.Return(5),
        };

        var fn = new IrFunction("entry", instructions, 1, 6, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        // The AddInt(4, 0, 1) after else_0 should be folded to LoadConstInt(4, 30)
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 4, Value: 30 })
            .ShouldBeTrue("Expected constant 30 from folding across single-predecessor label.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt { Target: 4 })
            .ShouldBeFalse("AddInt should be folded at single-predecessor label.");
    }

    [Test]
    public void Constant_propagation_meets_agreeing_constants_at_multi_predecessor_label()
    {
        // end_0 has two predecessors (Jump from then + fall-through from else). Neither
        // branch touches t0/t1, so both agree they are still 10/20 — the meet over both
        // edges should retain that fact and let AddInt(5, 0, 1) fold to 30, even though
        // this is a genuine multi-predecessor label.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 10),
            new IrInst.LoadConstInt(1, 20),
            new IrInst.LoadConstBool(2, false),
            new IrInst.JumpIfFalse(2, "else_0"),
            new IrInst.LoadConstInt(3, 99),
            new IrInst.StoreLocal(0, 3),
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(4, 77),
            new IrInst.StoreLocal(0, 4),
            new IrInst.Label("end_0"),
            // t0=10 and t1=20 agree on every path into end_0, so this should fold.
            new IrInst.AddInt(5, 0, 1),
            new IrInst.Return(5),
        };

        var fn = new IrFunction("entry", instructions, 1, 6, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 5, Value: 30 })
            .ShouldBeTrue("Expected constant 30 from meeting agreeing facts across a multi-predecessor label.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt { Target: 5 })
            .ShouldBeFalse("AddInt should fold once the meet proves both operands agree on every path.");
    }

    [Test]
    public void Constant_propagation_clears_disagreeing_value_at_multi_predecessor_label_but_keeps_agreeing_one()
    {
        // t2 is reassigned to a different constant (99 vs 77) on each of the two paths
        // into end_0, so the meet must drop it — but t0 (10 on every path) must survive.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 10),
            new IrInst.LoadConstBool(1, false),
            new IrInst.JumpIfFalse(1, "else_0"),
            new IrInst.LoadConstInt(2, 99),      // then branch: t2 = 99
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(2, 77),      // else branch: t2 = 77 — disagrees with then
            new IrInst.Label("end_0"),
            new IrInst.AddInt(3, 2, 2),          // t2 disagrees — must NOT fold
            new IrInst.AddInt(4, 0, 0),          // t0 agrees (10 everywhere) — must fold to 20
            new IrInst.AddInt(5, 3, 4),          // keeps both t3 and t4 live for dead-code elimination
            new IrInst.Return(5),
        };

        var fn = new IrFunction("entry", instructions, 1, 6, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt { Target: 3 })
            .ShouldBeTrue("AddInt should NOT fold when its operand disagrees across incoming edges.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 4, Value: 20 })
            .ShouldBeTrue("AddInt on the agreeing operand should still fold even though a sibling temp disagreed.");
    }

    [Test]
    public void Constant_propagation_meets_across_three_predecessors()
    {
        // end_0 has three predecessors: two explicit Jumps (from the two "then" bodies)
        // plus fall-through from the final "else" body. All three set t1 to the same
        // constant 5, so the three-way meet should retain it and fold AddInt(3, 1, 1).
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, false),
            new IrInst.JumpIfFalse(0, "b2"),
            new IrInst.LoadConstInt(1, 5),        // path A
            new IrInst.Jump("end_0"),
            new IrInst.Label("b2"),
            new IrInst.LoadConstBool(2, false),
            new IrInst.JumpIfFalse(2, "b3"),
            new IrInst.LoadConstInt(1, 5),        // path B
            new IrInst.Jump("end_0"),
            new IrInst.Label("b3"),
            new IrInst.LoadConstInt(1, 5),        // path C (falls through into end_0)
            new IrInst.Label("end_0"),
            new IrInst.AddInt(3, 1, 1),
            new IrInst.Return(3),
        };

        var fn = new IrFunction("entry", instructions, 1, 4, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 3, Value: 10 })
            .ShouldBeTrue("Expected constant 10 from a three-way meet where every predecessor agrees.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt { Target: 3 })
            .ShouldBeFalse("AddInt should fold once all three incoming edges agree.");
    }

    [Test]
    public void Constant_propagation_preserves_state_into_switch_case_labels()
    {
        // A SwitchTag case label has exactly one predecessor edge — the switch itself —
        // whose source state is simply whatever was known right before dispatch. That
        // state should propagate into every case (and the default), not be cleared.
        //
        // Each case combines the shared pre-switch constant (t0=42) with a case-specific
        // literal, so every case's folded result is distinct (43/44/45) — this isolates
        // the SwitchTag-to-case-label propagation being tested from the (separately
        // tested) local-slot meet-over-paths folding: since the three results disagree,
        // the slot-0 round trip at the join stays live and can't be folded away, so DCE
        // can't strip the evidence that each individual case folded using t0.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 42),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.SwitchTag(1, [(0, "case_a"), (1, "case_b")], "default_0"),
            new IrInst.Label("case_a"),
            new IrInst.LoadConstInt(10, 1),
            new IrInst.AddInt(2, 0, 10),
            new IrInst.StoreLocal(0, 2),
            new IrInst.Jump("end_0"),
            new IrInst.Label("case_b"),
            new IrInst.LoadConstInt(11, 2),
            new IrInst.AddInt(3, 0, 11),
            new IrInst.StoreLocal(0, 3),
            new IrInst.Jump("end_0"),
            new IrInst.Label("default_0"),
            new IrInst.LoadConstInt(12, 3),
            new IrInst.AddInt(4, 0, 12),
            new IrInst.StoreLocal(0, 4),
            new IrInst.Label("end_0"),
            new IrInst.LoadLocal(5, 0),
            new IrInst.Return(5),
        };

        var fn = new IrFunction("entry", instructions, 1, 13, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        var expectedByTarget = new Dictionary<int, long> { [2] = 43, [3] = 44, [4] = 45 };
        foreach (var (target, expected) in expectedByTarget)
        {
            optimized.EntryFunction.Instructions
                .Any(i => i is IrInst.LoadConstInt loadConst && loadConst.Target == target && loadConst.Value == expected)
                .ShouldBeTrue($"Expected t0=42 to propagate into every switch case, folding target {target} to {expected}.");
            optimized.EntryFunction.Instructions
                .Any(i => i is IrInst.AddInt addInt && addInt.Target == target)
                .ShouldBeFalse($"AddInt at target {target} should fold using the pre-switch known state.");
        }

        // The three cases disagree on the value stored to slot 0, so the join's
        // LoadLocal must NOT fold — confirming the (separate) local-slot meet correctly
        // declines here rather than accidentally picking one case's value.
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { Target: 5, Slot: 0 })
            .ShouldBeTrue("LoadLocal should NOT fold when the switch's cases disagree on the slot's value.");
    }

    [Test]
    public void Constant_propagation_folds_local_slot_agreeing_across_both_arms()
    {
        // Mirrors real compiled output: an if/match join result always round-trips
        // through a StoreLocal (one per arm) then a LoadLocal at the point of use —
        // never a raw temp reused directly across the label (see Ir.cs). Both arms
        // store the same constant 0 into slot 2; the LoadLocal after the join should
        // fold to that constant via meet-over-paths on the slot's tracked state.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, false),
            new IrInst.JumpIfFalse(0, "else_0"),
            new IrInst.LoadConstInt(1, 0),
            new IrInst.StoreLocal(2, 1),
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(3, 0),
            new IrInst.StoreLocal(2, 3),
            new IrInst.Label("end_0"),
            new IrInst.LoadLocal(4, 2),
            new IrInst.Return(4),
        };

        var fn = new IrFunction("entry", instructions, 3, 5, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 4, Value: 0 })
            .ShouldBeTrue("Expected the LoadLocal reading slot 2 to fold to 0, since both arms store the same constant.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { Target: 4 })
            .ShouldBeFalse("LoadLocal should be replaced once the slot's value is proven constant on every path.");
    }

    [Test]
    public void Constant_propagation_does_not_fold_local_slot_disagreeing_across_arms()
    {
        // Same shape as the agreeing-arms test, but the two arms store different
        // constants into the same slot — the meet must drop that knowledge, so the
        // LoadLocal after the join must NOT be folded.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, false),
            new IrInst.JumpIfFalse(0, "else_0"),
            new IrInst.LoadConstInt(1, 99),
            new IrInst.StoreLocal(2, 1),
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(3, 77),
            new IrInst.StoreLocal(2, 3),
            new IrInst.Label("end_0"),
            new IrInst.LoadLocal(4, 2),
            new IrInst.Return(4),
        };

        var fn = new IrFunction("entry", instructions, 3, 5, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { Target: 4, Slot: 2 })
            .ShouldBeTrue("LoadLocal should NOT fold when the two arms store disagreeing constants to the same slot.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 4 })
            .ShouldBeFalse("No LoadConstInt should be synthesized for a slot whose value disagrees across arms.");
    }

    [Test]
    public void Constant_propagation_kills_local_slot_knowledge_on_non_constant_store()
    {
        // Slot 1 starts out known (10), but is then overwritten with a value from an
        // unknown source (a LoadLocal from a never-recorded slot, standing in for e.g.
        // a parameter). The subsequent StoreLocal must kill the stale knowledge, not
        // let it survive to the following LoadLocal — a slot is mutable storage, not
        // single-assignment.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 10),
            new IrInst.StoreLocal(1, 0),   // slot 1 = known 10
            new IrInst.LoadLocal(2, 2),    // t2 = unknown (slot 2 was never stored to)
            new IrInst.StoreLocal(1, 2),   // slot 1 = unknown now — must kill prior knowledge
            new IrInst.LoadLocal(3, 1),    // must NOT fold
            new IrInst.Return(3),
        };

        var fn = new IrFunction("entry", instructions, 3, 4, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { Target: 3, Slot: 1 })
            .ShouldBeTrue("LoadLocal should NOT fold once its slot has been overwritten with an unknown value.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 3 })
            .ShouldBeFalse("No stale constant should survive a store of an unknown value to the same slot.");
    }

    [Test]
    public void Branch_folding_drops_jumpiffalse_when_condition_known_true()
    {
        // cond is always true, so the false-branch (else_0) is never taken. Each arm
        // returns directly (no post-branch join), isolating this pass's own claim from
        // FoldConstants/ElideUnreachableCode's single-pass ordering (a join read via a
        // local slot wouldn't fold here regardless — FoldConstants runs before dead-arm
        // elimination, so it still conservatively sees both arms' writes as live).
        // The JumpIfFalse should disappear entirely, and — since nothing branches to
        // else_0 anymore — its now-orphaned body should be stripped too, not just
        // become inert dead code the compiler still emits.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.JumpIfFalse(0, "else_0"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Return(1),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(i => i is IrInst.JumpIfFalse)
            .ShouldBeFalse("No JumpIfFalse with a statically-known condition should survive.");
        optimized.EntryFunction.Instructions.Any(i => i is IrInst.Label { Name: "else_0" })
            .ShouldBeFalse("The orphaned else_0 label (zero remaining predecessors) should be dropped.");
        optimized.EntryFunction.Instructions.Any(i => i is IrInst.LoadConstInt { Value: 20 })
            .ShouldBeFalse("The unreachable false-arm's body should be stripped, not just made dead.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 1, Value: 10 } && optimized.EntryFunction.Instructions.Any(r => r is IrInst.Return { Source: 1 }))
            .ShouldBeTrue("The always-taken true-arm's value (10) should still reach the return.");
    }

    [Test]
    public void Branch_folding_rewrites_jumpiffalse_to_jump_when_condition_known_false()
    {
        // cond is always false, so the false-branch (else_0) is always taken. Each arm
        // returns directly, for the same reason as the known-true test above. The
        // JumpIfFalse should become an unconditional Jump, and the orphaned
        // true-arm's body (now between two terminators with no label re-entry) should
        // be stripped. That unconditional Jump then falls immediately before its own
        // target label (else_0) with nothing left between them — a redundant fallthrough
        // jump SimplifyControlFlow correctly elides too, so neither a
        // JumpIfFalse nor a Jump to else_0 should survive; only the branch's own
        // conditional-vs-unconditional-vs-none shape has changed release over release,
        // never the correctness of what value reaches the return.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, false),
            new IrInst.JumpIfFalse(0, "else_0"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Return(1),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 0, 3, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(i => i is IrInst.JumpIfFalse)
            .ShouldBeFalse("No JumpIfFalse with a statically-known condition should survive.");
        optimized.EntryFunction.Instructions.Any(i => i is IrInst.Jump { Target: "else_0" })
            .ShouldBeFalse("The unconditional jump immediately precedes its own target label with nothing between — SimplifyControlFlow correctly elides this redundant fallthrough jump too.");
        optimized.EntryFunction.Instructions.Any(i => i is IrInst.LoadConstInt { Value: 10 })
            .ShouldBeFalse("The unreachable true-arm's body should be stripped, not just made dead.");
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Target: 2, Value: 20 } && optimized.EntryFunction.Instructions.Any(r => r is IrInst.Return { Source: 2 }))
            .ShouldBeTrue("The always-taken false-arm's value (20) should still reach the return.");
    }

    [Test]
    public void Branch_folding_applies_to_a_condition_known_via_local_slot_propagation()
    {
        // The condition itself arrives via a StoreLocal/LoadLocal round trip (the real
        // shape every let-bound value takes — see the local-slot meet-over-paths
        // tests above), not a raw literal feeding JumpIfFalse directly. This confirms
        // Constant-condition branch folding composes with the constant propagation pass's local-slot tracking rather than needing its
        // own separate wiring: TryFoldLocalLoad already records the folded value in
        // state.Bools before HandleJumpIfFalse ever sees it.
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstBool(0, true),
            new IrInst.StoreLocal(0, 0),
            new IrInst.LoadLocal(1, 0),
            new IrInst.JumpIfFalse(1, "else_0"),
            new IrInst.LoadConstInt(2, 10),
            new IrInst.Jump("end_0"),
            new IrInst.Label("else_0"),
            new IrInst.LoadConstInt(3, 20),
            new IrInst.Label("end_0"),
            new IrInst.Return(2),
        };

        var fn = new IrFunction("entry", instructions, 1, 4, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Any(i => i is IrInst.JumpIfFalse)
            .ShouldBeFalse("The condition folds through the StoreLocal/LoadLocal round trip, so JumpIfFalse should still fold.");
        optimized.EntryFunction.Instructions.Any(i => i is IrInst.LoadLocal)
            .ShouldBeFalse("The condition's own LoadLocal should fold to a constant, not survive.");
    }

    // Compile-time evaluation tests

    [Test]
    public void Compile_time_eval_folds_recursive_scalar_call()
    {
        var ir = LowerAndOptimize(
            "let recursive fib = given (n) -> if n < 2 then n else fib(n - 1) + fib(n - 2) " +
            "in Ashes.IO.print(fib(20))");

        // fib(20) = 6765. The whole recursive computation is replaced by a constant load,
        // and no closure call to the fib lambda survives in the entry function.
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 6765 })
            .ShouldBeTrue("Expected fib(20) to be evaluated to the constant 6765.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.CallClosure)
            .ShouldBeFalse("The recursive fib call should be evaluated away at compile time.");
    }

    [Test]
    public void Compile_time_eval_folds_pure_user_function()
    {
        var ir = LowerAndOptimize(
            "let square = given (n) -> n * n in Ashes.IO.print(square(12))");

        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstInt { Value: 144 })
            .ShouldBeTrue("Expected square(12) to be evaluated to the constant 144.");
    }

    [Test]
    public void Compile_time_eval_does_not_fold_non_terminating_recursion()
    {
        // countUp(1) never terminates; the depth budget must make evaluation bail and keep the
        // runtime call rather than hanging the compiler.
        var ir = LowerAndOptimize(
            "let recursive countUp = given (n) -> if n < 0 then n else countUp(n + 1) " +
            "in Ashes.IO.print(countUp(1))");

        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.CallClosure)
            .ShouldBeTrue("Non-terminating recursion must not be folded; the call stays runtime.");
    }

    [Test]
    public void Compile_time_eval_does_not_fold_side_effecting_call()
    {
        // A call to a function that performs IO must never be evaluated away — doing so would
        // delete the observable side effect. The impurity gate keeps the call as runtime code.
        var ir = LowerAndOptimize(
            "let logIt = given (n) -> Ashes.IO.print(n + 1) in logIt(42)");

        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.CallClosure)
            .ShouldBeTrue("A call performing IO must stay runtime code, not be folded away.");
    }

    // Local common-subexpression elimination tests

    [Test]
    public void Local_cse_merges_duplicate_pure_call_with_identical_operands()
    {
        // makePair(arg) allocates a fresh cell and stores arg into it — every instruction is
        // modeled-pure, so makePair lands in IrCompileTimeEval's evaluable-function set, but its
        // result is a pointer (not Int/Bool/Float), so compile-time-eval can never embed it as a
        // constant regardless of argument tracking — isolating this test to local CSE alone.
        IrFunction makePair = new(
            "makePair",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.AllocAdt(1, 0, 1),
                new IrInst.SetAdtField(1, 0, 0),
                new IrInst.Return(1),
            ],
            2, 2, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 99),
                new IrInst.CallKnown(2, "makePair", 0, 1),
                new IrInst.CallKnown(3, "makePair", 0, 1),
                new IrInst.GetAdtField(4, 2, 0),
                new IrInst.GetAdtField(5, 3, 0),
                new IrInst.AddInt(6, 4, 5),
                new IrInst.PrintInt(6),
                new IrInst.Return(6),
            ],
            0, 7, false);

        IrProgram program = new(entry, [makePair], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.CallKnown ck && string.Equals(ck.FuncLabel, "makePair", StringComparison.Ordinal))
            .ShouldBe(1, "The second identical call to a pure function should be merged into the first.");
    }

    [Test]
    public void Local_cse_does_not_merge_calls_to_non_pure_functions()
    {
        IrFunction printAndReturn = new(
            "printAndReturn",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.PrintInt(0),
                new IrInst.Return(0),
            ],
            2, 1, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 42),
                new IrInst.CallKnown(2, "printAndReturn", 0, 1),
                new IrInst.CallKnown(3, "printAndReturn", 0, 1),
                new IrInst.AddInt(4, 2, 3),
                new IrInst.PrintInt(4),
                new IrInst.Return(4),
            ],
            0, 5, false);

        IrProgram program = new(entry, [printAndReturn], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.CallKnown ck && string.Equals(ck.FuncLabel, "printAndReturn", StringComparison.Ordinal))
            .ShouldBe(2, "Two calls to a side-effecting function must both survive, unmerged.");
    }

    [Test]
    public void Local_cse_merges_duplicate_adt_field_reads()
    {
        // Both reads are also each forwarded directly from their establishing SetAdtField
        // (store-to-load forwarding, since the pointer is a fresh allocation) — a stronger result than plain
        // read-to-read CSE alone would give, so zero GetAdtField survive, not one.
        List<IrInst> instructions =
        [
            new IrInst.AllocAdt(0, 0, 2),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.SetAdtField(0, 1, 2),
            new IrInst.GetAdtField(3, 0, 0),
            new IrInst.GetAdtField(4, 0, 0),
            new IrInst.AddInt(5, 3, 4),
            new IrInst.PrintInt(5),
            new IrInst.Return(5),
        ];
        IrFunction entry = new("entry", instructions, 0, 6, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.GetAdtField)
            .ShouldBe(0, "Both field reads should be forwarded, either from each other or from the store that established the value.");
    }

    [Test]
    public void Local_cse_merges_field_reads_of_a_functions_own_argument()
    {
        // The exact `let x = p.x in let y = p.x in x + y` shape: p is the function's own
        // argument (slot 1), reloaded via a fresh LoadLocal at each use. Slot 1 is populated by
        // the backend's entry prologue, never by a visible IrInst.StoreLocal, so this only
        // merges if EliminateLocalRedundantComputation seeds the function's own arg/env slots.
        IrFunction describe = new(
            "describe",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.GetAdtField(1, 0, 0),
                new IrInst.LoadLocal(2, 1),
                new IrInst.GetAdtField(3, 2, 0),
                new IrInst.AddInt(4, 1, 3),
                new IrInst.Return(4),
            ],
            2, 5, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 1),
                new IrInst.AllocAdt(2, 0, 1),
                new IrInst.SetAdtField(2, 0, 1),
                new IrInst.CallKnown(3, "describe", 0, 2),
                new IrInst.PrintInt(3),
                new IrInst.Return(3),
            ],
            0, 4, false);

        IrProgram program = new(entry, [describe], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        IrFunction? optimizedDescribe = optimized.Functions
            .FirstOrDefault(f => string.Equals(f.Label, "describe", StringComparison.Ordinal));
        optimizedDescribe.ShouldNotBeNull();
        optimizedDescribe.Instructions
            .Count(i => i is IrInst.GetAdtField)
            .ShouldBe(1, "Two reads of the same field of the function's own argument should merge.");
    }

    [Test]
    public async Task Local_cse_does_not_merge_field_reads_across_intervening_set_adt_field()
    {
        // The second read follows an in-place write to the exact same (pointer, field index) —
        // merging it with the first read's now-stale result would silently keep the old value.
        // With store-to-load forwarding, both reads are now correctly forwarded from
        // their RESPECTIVE nearest write (5, then 7) rather than surviving as real GetAdtField
        // instructions — so the meaningful check is the actual computed value (12), not whether
        // any GetAdtField instructions remain.
        List<IrInst> instructions =
        [
            new IrInst.AllocAdt(0, 0, 1),
            new IrInst.LoadConstInt(1, 5),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.GetAdtField(2, 0, 0),
            new IrInst.LoadConstInt(3, 7),
            new IrInst.SetAdtField(0, 0, 3),
            new IrInst.GetAdtField(4, 0, 0),
            new IrInst.AddInt(5, 2, 4),
            new IrInst.PrintInt(5),
            new IrInst.Return(5),
        ];
        IrFunction entry = new("entry", instructions, 0, 6, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        string stdout = await RunAsync(optimized).ConfigureAwait(false);
        stdout.Trim().ShouldBe("12", "The second read must see the second write's value (7), not the first (5) — 5 + 7, not 5 + 5 or 7 + 7.");
    }

    [Test]
    public void Local_cse_does_not_merge_field_reads_across_a_block_boundary()
    {
        // Scoped to a single straight-line block: a Label between the two reads (an extended
        // basic block boundary) must reset the cache, even with nothing else intervening. Reads
        // a function's own argument (slot 1, not a fresh allocation) so this specifically
        // exercises read-to-read CSE's block-boundary reset, uncontaminated by
        // store-to-load forwarding (which never applies here — no SetAdtField at all).
        IrFunction reader = new(
            "reader",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.GetAdtField(1, 0, 0),
                new IrInst.Jump("mid"),
                new IrInst.Label("mid"),
                new IrInst.LoadLocal(2, 1),
                new IrInst.GetAdtField(3, 2, 0),
                new IrInst.AddInt(4, 1, 3),
                new IrInst.Return(4),
            ],
            2, 5, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 3),
                new IrInst.AllocAdt(2, 0, 1),
                new IrInst.SetAdtField(2, 0, 1),
                new IrInst.CallKnown(3, "reader", 0, 2),
                new IrInst.PrintInt(3),
                new IrInst.Return(3),
            ],
            0, 4, false);

        IrProgram program = new(entry, [reader], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        IrFunction? optimizedReader = optimized.Functions
            .FirstOrDefault(f => string.Equals(f.Label, "reader", StringComparison.Ordinal));
        optimizedReader.ShouldNotBeNull();
        optimizedReader.Instructions
            .Count(i => i is IrInst.GetAdtField)
            .ShouldBe(2, "A block boundary must reset local CSE's cache.");
    }

    // Store-to-load / projection forwarding tests

    [Test]
    public void Store_to_load_forwarding_forwards_the_swap_pattern()
    {
        // The doc's own motivating example: `given swap = given p: Point -> Point(p.y, p.x)`.
        // A fresh record is constructed, its fields set, then immediately read back — the read
        // should forward directly from the store instead of round-tripping through memory.
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 7),
            new IrInst.LoadConstInt(1, 9),
            new IrInst.AllocAdt(2, 0, 2),
            new IrInst.SetAdtField(2, 0, 0),
            new IrInst.SetAdtField(2, 1, 1),
            new IrInst.GetAdtField(3, 2, 0),
            new IrInst.GetAdtField(4, 2, 1),
            new IrInst.PrintInt(3),
            new IrInst.PrintInt(4),
            new IrInst.Return(3),
        ];
        IrFunction entry = new("entry", instructions, 0, 5, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions
            .Count(i => i is IrInst.GetAdtField)
            .ShouldBe(0, "Both reads should forward directly from the SetAdtField that established them.");
    }

    [Test]
    public async Task Store_to_load_forwarding_forwards_distinct_field_indices_correctly()
    {
        // A negative-adjacent check: forwarding field 0 must not leak into a read of field 1 on
        // the same fresh pointer — each (pointer, field index) key must stay independent.
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 100),
            new IrInst.LoadConstInt(1, 200),
            new IrInst.AllocAdt(2, 0, 2),
            new IrInst.SetAdtField(2, 0, 0),
            new IrInst.SetAdtField(2, 1, 1),
            new IrInst.GetAdtField(3, 2, 1),
            new IrInst.GetAdtField(4, 2, 0),
            new IrInst.SubInt(5, 3, 4),
            new IrInst.PrintInt(5),
            new IrInst.Return(5),
        ];
        IrFunction entry = new("entry", instructions, 0, 6, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        string stdout = await RunAsync(optimized).ConfigureAwait(false);
        stdout.Trim().ShouldBe("100", "Field 1 (200) minus field 0 (100) must not be confused by sharing the same pointer.");
    }

    [Test]
    public async Task Store_to_load_forwarding_does_not_forward_through_a_non_fresh_pointer()
    {
        // The pointer here is the function's own argument (not a fresh allocation in this
        // block) — forwarding a write through it would be unsound in general, since the caller
        // could hold another reference to the same cell. Correctness (not instruction count) is
        // the meaningful check: the read must see the write's new value, proving the write was
        // neither skipped nor silently miscompiled by the conservative fallback.
        IrFunction writer = new(
            "writer",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.LoadConstInt(1, 42),
                new IrInst.SetAdtField(0, 0, 1),
                new IrInst.LoadLocal(2, 1),
                new IrInst.GetAdtField(3, 2, 0),
                new IrInst.Return(3),
            ],
            2, 4, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 0),
                new IrInst.AllocAdt(2, 0, 1),
                new IrInst.SetAdtField(2, 0, 1),
                new IrInst.CallKnown(3, "writer", 0, 2),
                new IrInst.PrintInt(3),
                new IrInst.Return(3),
            ],
            0, 4, false);

        IrProgram program = new(entry, [writer], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        string stdout = await RunAsync(optimized).ConfigureAwait(false);
        stdout.Trim().ShouldBe("42", "The read must see the value just written through the function's own argument.");
    }

    [Test]
    public async Task Store_to_load_forwarding_forwards_a_value_sourced_from_the_functions_own_argument()
    {
        // Regression for a real bug caught only by compiling and running actual .ash source (the
        // doc's own `let p = Point(x = n, ...) in ... p.x` shape, with n a function parameter):
        // the stored value's source temp aliases the function's own arg slot, whose canonical
        // identity (LocalCseState.EntrySlotIdentity) is a synthetic, negative sentinel that must
        // never be used as the cache's forwarded VALUE — only as a key for matching. The fix
        // caches the store's raw, unresolved source temp instead of its resolved identity; the
        // first fixture version emitted `Borrow(target, -2)`, an unemittable reference to a temp
        // that doesn't exist, and this test's compile-and-run would have caught it (a raw
        // GetAdtField/CallKnown-count unit test would not, since it doesn't run the result).
        IrFunction reader = new(
            "reader",
            [
                new IrInst.LoadLocal(0, 1), // n (the function's own argument, slot 1)
                new IrInst.AllocAdt(1, 0, 1),
                new IrInst.SetAdtField(1, 0, 0), // field 0 := n, sourced from the arg slot alias
                new IrInst.GetAdtField(2, 1, 0), // must forward to n's value (99), not a sentinel
                new IrInst.Return(2),
            ],
            2, 3, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, 99),
                new IrInst.CallKnown(2, "reader", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0, 3, false);

        IrProgram program = new(entry, [reader], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        string stdout = await RunAsync(optimized).ConfigureAwait(false);
        stdout.Trim().ShouldBe("99", "The forwarded field read must see the argument's real value.");
    }

    // Control-flow simplification tests

    [Test]
    public void Simplify_control_flow_redirects_jump_through_a_three_hop_empty_label_chain()
    {
        // L1 -> L2 -> L3, each an empty label (nothing but an unconditional Jump). Every jump
        // that used to target L1 or L2, including the ones inside the chain itself, should end
        // up pointing directly at L3, and L1/L2 should be dropped as unreferenced once nothing
        // points at them any more. Rewriting the chain's own internal jumps to the same final
        // target stacks several Jump L3 instructions back-to-back once the separating labels are
        // dropped — a second ElideUnreachableCode pass must remove every one past the first.
        List<IrInst> instructions =
        [
            new IrInst.Jump("L1"),
            new IrInst.Label("L1"),
            new IrInst.Jump("L2"),
            new IrInst.Label("L2"),
            new IrInst.Jump("L3"),
            new IrInst.Label("L3"),
            new IrInst.LoadConstInt(0, 42),
            new IrInst.PrintInt(0),
            new IrInst.Return(0),
        ];
        IrFunction entry = new("entry", instructions, 0, 1, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.Jump>().ShouldBeEmpty(
            "Every branch is redundant fallthrough once the final target's label sits right after it.");
        optimized.EntryFunction.Instructions.OfType<IrInst.Label>().Select(l => l.Name)
            .ShouldNotContain("L1", "L1 should be dropped once nothing targets it any more.");
        optimized.EntryFunction.Instructions.OfType<IrInst.Label>().Select(l => l.Name)
            .ShouldNotContain("L2", "L2 should be dropped once nothing targets it any more.");
    }

    [Test]
    public async Task Simplify_control_flow_does_not_redirect_through_a_non_empty_label()
    {
        // L1 has real work (not just an unconditional Jump) before falling into "done" — a
        // negative test against over-eager chain-following. The condition is the function's own
        // argument, not a constant, so the branch survives the earlier passes' constant folding and
        // both arms remain real code for this pass to reason about. Execution correctness (not
        // instruction-shape matching) is the meaningful check here: if L1 were ever wrongly
        // treated as an empty hop and skipped, the branch would return the wrong value.
        IrFunction worker = new(
            "worker",
            [
                new IrInst.LoadLocal(0, 1),
                new IrInst.LoadConstInt(1, 0),
                new IrInst.CmpIntGt(2, 0, 1),
                new IrInst.JumpIfFalse(2, "L1"),
                new IrInst.LoadConstInt(3, 100),
                new IrInst.Jump("done"),
                new IrInst.Label("L1"),
                new IrInst.LoadConstInt(3, 7),
                new IrInst.Label("done"),
                new IrInst.Return(3),
            ],
            2, 4, true);

        IrFunction entry = new(
            "entry",
            [
                new IrInst.LoadConstInt(0, 0),
                new IrInst.LoadConstInt(1, -5),
                new IrInst.CallKnown(2, "worker", 0, 1),
                new IrInst.PrintInt(2),
                new IrInst.Return(2),
            ],
            0, 3, false);

        IrProgram program = new(entry, [worker], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        string stdout = await RunAsync(optimized).ConfigureAwait(false);
        stdout.Trim().ShouldBe("7", "arg <= 0 takes the L1 branch — its real work (7) must actually execute, not be skipped as if L1 were an empty hop.");
    }

    [Test]
    public void Simplify_control_flow_drops_an_unreferenced_label_left_by_an_earlier_pass()
    {
        // A label with zero remaining branch references (here, simply never targeted by
        // anything) should be dropped — the marker alone, never the code around it.
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 1),
            new IrInst.Label("never_targeted"),
            new IrInst.LoadConstInt(1, 2),
            new IrInst.AddInt(2, 0, 1),
            new IrInst.PrintInt(2),
            new IrInst.Return(2),
        ];
        IrFunction entry = new("entry", instructions, 0, 3, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.OfType<IrInst.Label>().Select(l => l.Name)
            .ShouldNotContain("never_targeted");
    }

    [Test]
    public void Simplify_control_flow_rewrites_switchtag_case_and_default_targets()
    {
        // Both a case label and the default label can independently be empty hops; both must be
        // redirected, and SwitchTag's own structure (case count, tags) must survive unchanged.
        List<IrInst> instructions =
        [
            new IrInst.LoadConstInt(0, 0),
            new IrInst.SwitchTag(0, [(0, "case0"), (1, "case1")], "default_case"),
            new IrInst.Label("case0"),
            new IrInst.Jump("real_case0"),
            new IrInst.Label("real_case0"),
            new IrInst.LoadConstInt(1, 10),
            new IrInst.Return(1),
            new IrInst.Label("case1"),
            new IrInst.LoadConstInt(2, 20),
            new IrInst.Return(2),
            new IrInst.Label("default_case"),
            new IrInst.Jump("real_default"),
            new IrInst.Label("real_default"),
            new IrInst.LoadConstInt(3, 30),
            new IrInst.Return(3),
        ];
        IrFunction entry = new("entry", instructions, 0, 4, false);
        IrProgram program = new(entry, [], [], true, false, false, false, false, false);
        IrProgram optimized = IrOptimizer.Optimize(program);

        IrInst.SwitchTag sw = optimized.EntryFunction.Instructions.OfType<IrInst.SwitchTag>().ShouldHaveSingleItem();
        sw.Cases.Count.ShouldBe(2);
        sw.Cases.Any(c => string.Equals(c.Label, "case0", StringComparison.Ordinal))
            .ShouldBeFalse("case0 was an empty hop to real_case0 and should be redirected.");
        string.Equals(sw.DefaultLabel, "default_case", StringComparison.Ordinal)
            .ShouldBeFalse("default_case was an empty hop to real_default and should be redirected.");
    }

    [Test]
    public async Task Simplify_control_flow_preserves_correct_output_through_a_redirected_chain()
    {
        // A -O0 backend execution test (the tier where this task's win is real, per its own
        // Testing requirement): correctness, not just instruction shape, through a redirected
        // jump chain feeding a real conditional branch.
        var source = """
            let describe n =
                if n > 0
                then "positive"
                else "non-positive"

            in Ashes.IO.print(describe(5) + " " + describe(-3))
            """;
        var stdout = await CompileOptimizedAndRunAsync(source).ConfigureAwait(false);
        stdout.Trim().ShouldBe("positive non-positive");
    }

    // Helpers

    private static IrProgram Lower(string source)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();
        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        return ir;
    }

    private static IrProgram LowerProgram(string source)
    {
        Diagnostics diagnostics = new();
        Program program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        IrProgram ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static int CountRuntimeRcOperations(IrProgram program)
    {
        return Count(program.EntryFunction)
            + program.Functions.Sum(Count);

        static int Count(IrFunction function)
            => function.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }
                or IrInst.RcDup { RuntimeManaged: true }
                or IrInst.RcIsUnique);
    }

    private static int CountErasedRcOperations(IrProgram program)
    {
        return Count(program.EntryFunction)
            + program.Functions.Sum(Count);

        static int Count(IrFunction function)
            => function.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: false }
                or IrInst.RcDup { RuntimeManaged: false });
    }

    [Test]
    public void Borrow_elision_rewrites_text_byte_length_operand()
    {
        // TextByteLength was missing from the optimizer's temp-rewrite and used-temp scans, so
        // ElideTrivialBorrows saw the feeding Borrow as unused, elided it, and left TextTemp
        // pointing at the deleted borrow's never-written temp (a null deref at runtime).
        var ir = LowerAndOptimize("""let s = "nope" in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Text.byteLength(s)))""");
        var byteLength = ir.EntryFunction.Instructions.OfType<IrInst.TextByteLength>().ShouldHaveSingleItem();
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadLocal { } load && load.Target == byteLength.TextTemp
                || i is IrInst.Borrow { } borrow && borrow.Target == byteLength.TextTemp)
            .ShouldBeTrue("TextByteLength must read a temp that is actually defined (LoadLocal or a kept Borrow).");
    }

    private static IrProgram LowerAndOptimize(string source)
    {
        return IrOptimizer.Optimize(Lower(source));
    }

    private static async Task<string> CompileOptimizedAndRunAsync(string source)
    {
        return await RunAsync(LowerAndOptimize(source)).ConfigureAwait(false);
    }

    // For raw-IR-constructed programs (already optimized via IrOptimizer.Optimize) rather than
    // ones lowered from .ash source, to verify the actual printed output of a compiled-and-run
    // program — a stronger check than counting surviving instructions when several equally-valid
    // optimization outcomes (e.g. read-to-read forwarding vs. store-to-load forwarding) could
    // legitimately produce different instruction shapes for the same correct result.
    private static async Task<string> RunAsync(IrProgram ir)
    {
        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"opt_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return stdout;
    }
}
