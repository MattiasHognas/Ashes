using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

[NotInParallel]
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

    // ── Borrow preservation tests ───────────────────────────────────────

    [Test]
    public void Optimizer_preserves_borrow_instructions()
    {
        var source = """
            let s = "hello" in Ashes.IO.print(s)
            """;
        var optimized = LowerAndOptimize(source);
        optimized.EntryFunction.Instructions
            .Any(i => i is IrInst.Borrow)
            .ShouldBeTrue("Borrow instructions must be preserved by the optimizer.");
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
        await File.WriteAllBytesAsync(exePath, elfBytes);

#pragma warning disable CA1416
        File.SetUnixFileMode(exePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return stdout;
    }
}
