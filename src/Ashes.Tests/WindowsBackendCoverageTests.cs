using System.Diagnostics;
using System.Reflection;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class WindowsBackendCoverageTests
{
    [Test]
    public void Windows_backend_compile_should_emit_pe_header_for_int_program()
    {
        var bytes = CompileForWindows("Ashes.IO.print(40 + 2)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForWindows("let z = 20 in let f = fun (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_string_concat_programs()
    {
        var bytes = CompileForWindows("Ashes.IO.print(\"hello \" + \"world\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_support_large_rdata_programs()
    {
        var bytes = CompileForWindows($"Ashes.IO.print(\"{new string('a', 20000)}\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_compile_should_not_emit_a_constant_stub_for_simple_programs()
    {
        var first = CompileForWindows("40 + 2");
        var second = CompileForWindows("40 + 3");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Windows_backend_compile_should_support_program_args_programs()
    {
        var bytes = CompileForWindows("match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        var ir = LowerExpression("if (1.5 + 2.5) == 4.0 then 42 else 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        var ir = LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> x + a + b | _ -> 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        var ir = LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> a + b
            """);

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        var ir = LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_closure_programs()
    {
        var ir = LowerExpression("let z = 20 in let f = fun (x) -> x + z in f(22)");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        var ir = LowerExpression("""let mk = fun (x) -> fun (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)""");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_print_programs()
    {
        var ir = LowerExpression("Ashes.IO.write(\"hi\")");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_program_args_programs()
    {
        var ir = LowerExpression("match Ashes.IO.args with | a :: b :: [] -> 1 | _ -> 0");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_read_line_programs()
    {
        var ir = LowerExpression("""match Ashes.IO.readLine(Unit) with | None -> 0 | Some(text) -> 1""");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_file_programs()
    {
        var ir = LowerExpression("""match Ashes.File.exists("present.txt") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0""");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_panic_programs()
    {
        var ir = LowerExpression("Ashes.IO.panic(\"boom\")");

        SupportsMinimalLlvm("SupportsMinimalWindowsLlvm", ir).ShouldBeTrue();
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("""let mk = fun (x) -> fun (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_program_args_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")",
            ["first", "second"]);
        result.Stdout.ShouldBe("first:second\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_read_line_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "hello\r\n");
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_return_none_at_read_line_eof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "");
        result.Stdout.ShouldBe("none\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_file_read_text_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "hello.txt"), "hello");

            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("hello.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_report_missing_file_read_errors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("missing.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir);
            result.Stdout.ShouldBe("Ashes.File.readText() failed\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_report_invalid_utf8_file_read_errors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, "invalid_utf8.bin"), [0xFF, 0xFE, 0xFD]);

            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("invalid_utf8.bin") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir);
            result.Stdout.ShouldBe("Ashes.File.readText() encountered invalid UTF-8\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_file_write_text_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.writeText("out.txt")("hello") with | Error(msg) -> Ashes.IO.print(msg) | Ok(_) -> match Ashes.File.readText("out.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_file_exists_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "present.txt"), "x");

            var result = await CompileRunWithWindowsLlvmAsync(
                """match (Ashes.File.exists("present.txt"), Ashes.File.exists("missing.txt")) with | (Ok(a), Ok(b)) -> Ashes.IO.print((if a then "true" else "false") + ":" + (if b then "true" else "false")) | (Error(msg), _) -> Ashes.IO.print(msg) | (_, Error(msg)) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir);
            result.Stdout.ShouldBe("true:false\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_float_arithmetic_and_comparisons()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_heap_backed_tuple_and_list_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)");
        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_string_compare_and_concat_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("if (\"he\" + \"llo\") == \"hello\" then Ashes.IO.print(42) else Ashes.IO.print(0)");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_print_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("Ashes.IO.write(\"hi\")");
        result.Stdout.ShouldBe("hi");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_adt_field_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """));
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_panic_programs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("Ashes.IO.panic(\"boom\")", expectedExitCode: 1);
        result.Stdout.ShouldBe("boom\n");
    }

    private static byte[] CompileForWindows(string source)
    {
        var ir = LowerExpression(source);
        return new WindowsX64PeBackend().Compile(ir);
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
        var method = typeof(WindowsX64PeBackend).Assembly
            .GetType("Ashes.Backend.Llvm.LlvmCodegen", throwOnError: true)!
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [ir])!;
    }

    private static async Task<ExecutionResult> CompileRunWithWindowsLlvmAsync(string source, IReadOnlyList<string>? args = null, string? stdin = null, string? workingDirectory = null, int expectedExitCode = 0)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithWindowsLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode);
    }

    private static async Task<ExecutionResult> CompileRunWithWindowsLlvmAsync(IrProgram ir, IReadOnlyList<string>? args = null, string? stdin = null, string? workingDirectory = null, int expectedExitCode = 0)
    {
        var exeBytes = new WindowsX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(exePath, exeBytes);

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
        if (args is not null)
        {
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        using var proc = Process.Start(psi)!;
        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(expectedExitCode, $"stderr: {stderr}");
        return new ExecutionResult(stdout, stderr, proc.ExitCode);
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
        catch
        {
        }
    }

    private readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
}
