using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// Validates that all four backend optimization levels (O0, O1, O2, O3)
/// produce correct executables for a representative set of programs.
/// </summary>
public sealed class OptimizationLevelTests
{
    // ── Arithmetic ──────────────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Arithmetic_program_produces_correct_output(BackendOptimizationLevel level)
    {
        var result = await CompileAndRunAsync("Ashes.IO.print(40 + 2)", level);
        result.Stdout.ShouldBe("42");
    }

    // ── String concatenation ────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task String_concat_program_produces_correct_output(BackendOptimizationLevel level)
    {
        var result = await CompileAndRunAsync("""Ashes.IO.print("hello " + "world")""", level);
        result.Stdout.ShouldBe("hello world");
    }

    // ── Pattern matching / ADT ──────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Pattern_match_program_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = """
            match ([1, 2], (3, 4)) with
            | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b)
            | _ -> Ashes.IO.print(0)
            """;
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("8");
    }

    // ── Recursion (fibonacci) ───────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Recursive_fibonacci_program_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = "let rec fib = fun (n) -> match n with | 0 -> 0 | 1 -> 1 | _ -> fib(n - 1) + fib(n - 2) in Ashes.IO.print(fib(10))";
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("55");
    }

    // ── Closure / lambda ────────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Closure_program_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = "let mk = fun (x) -> fun (y) -> x + y in let add20 = mk(20) in Ashes.IO.print(add20(22))";
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("42");
    }

    // ── Tail-recursive loop ─────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Tail_recursive_loop_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = "let rec loop = fun (acc) -> fun (n) -> match n with | 0 -> acc | _ -> loop(acc + n)(n - 1) in Ashes.IO.print(loop(0)(100))";
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("5050");
    }

    // ── Float arithmetic ────────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task Float_arithmetic_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = "if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)";
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("42");
    }

    // ── String equality ─────────────────────────────────────────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public async Task String_equality_produces_correct_output(BackendOptimizationLevel level)
    {
        const string source = """if ("he" + "llo") == "hello" then Ashes.IO.print(42) else Ashes.IO.print(0)""";
        var result = await CompileAndRunAsync(source, level);
        result.Stdout.ShouldBe("42");
    }

    // ── Windows target: compile-only (no execution on Linux) ────────────

    [Test]
    [Arguments(BackendOptimizationLevel.O0)]
    [Arguments(BackendOptimizationLevel.O1)]
    [Arguments(BackendOptimizationLevel.O2)]
    [Arguments(BackendOptimizationLevel.O3)]
    public void Windows_target_compiles_at_all_levels(BackendOptimizationLevel level)
    {
        var ir = LowerExpression("Ashes.IO.print(40 + 2)");
        var options = new BackendCompileOptions(level);
        var bytes = new WindowsX64LlvmBackend().Compile(ir, options);

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static IrProgram LowerExpression(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<ExecutionResult> CompileAndRunAsync(string source, BackendOptimizationLevel level)
    {
        var ir = LowerExpression(source);
        var options = new BackendCompileOptions(level);
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir, options);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-opt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var exePath = Path.Combine(tmpDir, $"opt_{level}_{Guid.NewGuid():N}");

        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = await TestProcessHelper.StartProcessAsync(psi);
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            proc.ExitCode.ShouldBe(0, $"[{level}] stderr: {stderr}");
            return new ExecutionResult(stdout.TrimEnd(), stderr, proc.ExitCode);
        }
        finally
        {
            try { if (File.Exists(exePath)) File.Delete(exePath); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
}
