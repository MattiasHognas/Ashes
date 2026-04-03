using System.Diagnostics;
using System.Reflection;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxBackendCoverageTests
{
    [Test]
    public void Linux_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForLinux("let z = 20 in let f = fun (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_not_emit_a_constant_image_for_simple_programs()
    {
        var first = CompileForLinux("40 + 2");
        var second = CompileForLinux("40 + 3");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        var ir = LowerExpression("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)");

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        var ir = LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)");

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        var ir = LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """);

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        var ir = LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0");

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_closure_programs()
    {
        var ir = LowerExpression("let z = 20 in let f = fun (x) -> x + z in f(22)");

        SupportsMinimalLlvm("SupportsMinimalLinuxLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunWithLinuxLlvmAsync("let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))");
        stdout.ShouldBe("42\n");
    }

    private static byte[] CompileForLinux(string source)
    {
        var ir = LowerExpression(source);
        return new LinuxX64LlvmBackend().Compile(ir);
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

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static bool SupportsMinimalLlvm(string methodName, IrProgram ir)
    {
        var method = typeof(LinuxX64LlvmBackend).Assembly
            .GetType("Ashes.Backend.Llvm.LlvmCodegen", throwOnError: true)!
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [ir])!;
    }

    private static async Task<string> CompileRunWithLinuxLlvmAsync(string source)
    {
        var ir = LowerExpression(source);
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(exePath, elfBytes);

#pragma warning disable CA1416
        File.SetUnixFileMode(exePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
