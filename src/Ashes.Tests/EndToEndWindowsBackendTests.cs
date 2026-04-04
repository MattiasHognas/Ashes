using System.Diagnostics;
using Ashes.Semantics;
using Shouldly;
using Ashes.Frontend;

namespace Ashes.Tests;

public sealed class EndToEndWindowsBackendTests
{
    [Test]
    public async Task Int_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(40 + 2)");
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task String_concat_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(\"hello \" + \"world\")");
        stdout.ShouldBe("hello world\n");
    }

    [Test]
    public async Task Write_program_runs_without_trailing_newline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("let _ = Ashes.IO.write(\"he\") in Ashes.IO.write(\"llo\")");
        stdout.ShouldBe("hello");
    }

    [Test]
    public async Task Write_line_program_runs_with_newline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.writeLine(\"hello\")");
        stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_some_for_input()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "hello\r\n")).ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_none_at_eof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "")).ShouldBe("none\n");
    }

    [Test]
    public async Task Lambda_and_closure_work()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var src = "let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Program_args_are_available_as_prelude_list_without_executable_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var src = "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")";
        (await CompileRunCaptureAsync(src, ["first", "second"])).ShouldBe("first:second\n");
    }

    private static async Task<string> CompileRunCaptureAsync(string source, string[]? programArgs = null, string? stdin = null)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();

        var exeBytes = new Ashes.Backend.Backends.WindowsX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"mf_{Guid.NewGuid():N}.exe");
        await File.WriteAllBytesAsync(exePath, exeBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in programArgs ?? [])
        {
            psi.ArgumentList.Add(arg);
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

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
