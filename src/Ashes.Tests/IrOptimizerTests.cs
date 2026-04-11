using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class IrOptimizerTests
{
    // ── Constant folding tests ──────────────────────────────────────────

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

    // ── Dead code elimination tests ─────────────────────────────────────

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

    // ── Observable behavior preservation tests ──────────────────────────

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

    // ── Pass pipeline ordering tests ────────────────────────────────────

    [Test]
    public void Optimizer_runs_on_all_functions()
    {
        var source = "let add = fun (x) -> fun (y) -> x + y in Ashes.IO.print(add(10)(32))";
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

    // ── Drop preservation tests ─────────────────────────────────────────

    [Test]
    public void Optimizer_preserves_drop_instructions()
    {
        var source = """
            let s = "hello" in Ashes.IO.print(s)
            """;
        var optimized = LowerAndOptimize(source);
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Drop)
            .ShouldBeTrue("Drop instructions must be preserved by the optimizer.");
    }

    // ── Borrow elision tests ────────────────────────────────────────────

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

    // ── End-to-end optimization correctness ─────────────────────────────

    [Test]
    public async Task Optimized_int_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("Ashes.IO.print(10 + 32)");
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Optimized_string_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("Ashes.IO.print(\"hello \" + \"world\")");
        stdout.ShouldBe("hello world\n");
    }

    [Test]
    public async Task Optimized_lambda_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileOptimizedAndRunAsync("let add = fun (x) -> fun (y) -> x + y in Ashes.IO.print(add(10)(32))");
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
            let rec sum = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else sum(n - 1)(acc + n)
            in Ashes.IO.print(sum(100)(0))
            """;
        var stdout = await CompileOptimizedAndRunAsync(source);
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
            match Ashes.File.exists("nonexistent.txt") with
                | Ok(result) -> if result then Ashes.IO.print("yes") else Ashes.IO.print("no")
                | Error(msg) -> Ashes.IO.print(msg)
            """;
        var stdout = await CompileOptimizedAndRunAsync(source);
        stdout.ShouldBe("no\n");
    }

    // ── Constant propagation across single-predecessor labels ─────────

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

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IrProgram Lower(string source)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();
        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();
        return ir;
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

        using var proc = await TestProcessHelper.StartProcessAsync(psi);
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout;
    }
}
