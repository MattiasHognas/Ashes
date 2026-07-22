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
        var ir = LowerAndOptimize("if 10 == 10 then Ashes.IO.print(1) else Ashes.IO.print(0)");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.LoadConstBool { Value: true })
            .ShouldBeTrue("Expected constant-folded comparison result true.");
        ir.EntryFunction.Instructions
            .Any(i => i is IrInst.CmpIntEq)
            .ShouldBeFalse("CmpIntEq should be eliminated by constant folding.");
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
    public void Runtime_rc_dup_and_drop_are_preserved_by_the_optimizer()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 0),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(2, 0),
            new IrInst.Return(2),
        };
        var function = new IrFunction("entry", instructions, 0, 4, false);
        var program = new IrProgram(function, [], [], false, false, false, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);

        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { RuntimeManaged: true }).ShouldBe(2);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcIsUnique).ShouldBe(1);
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
                    if head == "{"
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
    public void Constant_propagation_clears_at_multi_predecessor_label()
    {
        // end_0 has two predecessors (Jump from then + fall-through from else)
        // so constants should NOT propagate through it.
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
            // At end_0: both then and else branches reach here — t0, t1 should NOT be known
            new IrInst.AddInt(5, 0, 1),         // should NOT be folded
            new IrInst.Return(5),
        };

        var fn = new IrFunction("entry", instructions, 1, 6, false);
        var program = new IrProgram(fn, [], [], false, false, false, false, false, false);
        var optimized = IrOptimizer.Optimize(program);

        // AddInt should NOT be folded at multi-predecessor label
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.AddInt { Target: 5 })
            .ShouldBeTrue("AddInt should NOT be folded at multi-predecessor label.");
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
        var ir = LowerAndOptimize(source);
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
