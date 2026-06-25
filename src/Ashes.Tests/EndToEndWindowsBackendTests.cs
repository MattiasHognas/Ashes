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
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(40 + 2)");
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task String_concat_program_runs_and_prints_expected_output()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(\"hello \" + \"world\")");
        stdout.ShouldBe("hello world\n");
    }

    [Test]
    public async Task Write_program_runs_without_trailing_newline()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("let _ = Ashes.IO.write(\"he\") in Ashes.IO.write(\"llo\")");
        stdout.ShouldBe("hello");
    }

    [Test]
    public async Task Write_line_program_runs_with_newline()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.writeLine(\"hello\")");
        stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_some_for_input()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "hello\r\n")).ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_none_at_eof()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "")).ShouldBe("none\n");
    }

    [Test]
    public async Task Lambda_and_closure_work()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = "let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Program_args_are_available_as_prelude_list_without_executable_name()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")";
        (await CompileRunCaptureAsync(src, ["first", "second"])).ShouldBe("first:second\n");
    }

    [Test]
    public async Task Read_exact_reads_requested_byte_count()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = "match Ashes.IO.readExact(5) with | Error(msg) -> Ashes.IO.print(msg) | Ok(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(src, stdin: "hello world")).ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_exact_reports_error_on_eof()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = "match Ashes.IO.readExact(10) with | Ok(_) -> Ashes.IO.print(\"ok\") | Error(_) -> Ashes.IO.print(\"eof\")";
        (await CompileRunCaptureAsync(src, stdin: "hi")).ShouldBe("eof\n");
    }

    [Test]
    public async Task Process_spawn_captures_child_stdout()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = """
            match Ashes.Process.spawn("C:\\Windows\\System32\\cmd.exe")(["/c", "echo hello"]) with
                | Error(msg) -> Ashes.IO.print(msg)
                | Ok(proc) ->
                    match Ashes.Process.readStdoutLine(proc) with
                        | None -> Ashes.IO.print("no output")
                        | Some(line) -> let _ = Ashes.Process.waitForExit(proc) in Ashes.IO.print(line)
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("hello\n");
    }

    [Test]
    public async Task Process_wait_for_exit_returns_child_exit_code()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        var src = """
            match Ashes.Process.spawn("C:\\Windows\\System32\\cmd.exe")(["/c", "exit 3"]) with
                | Error(msg) -> Ashes.IO.print(msg)
                | Ok(proc) -> Ashes.IO.print(Ashes.Process.waitForExit(proc))
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("3\n");
    }

    [Test]
    public async Task Process_write_stdin_is_delivered_to_child()
    {
        if (!CanRunWindowsPrograms())
        {
            return;
        }

        // findstr "^" echoes every input line, so it round-trips whatever we write
        // to the child's stdin back through its stdout.
        var src = """
            match Ashes.Process.spawn("C:\\Windows\\System32\\findstr.exe")(["^"]) with
                | Error(msg) -> Ashes.IO.print(msg)
                | Ok(proc) ->
                    let _ = Ashes.Process.writeStdin(proc)("hello\n")
                    in match Ashes.Process.readStdoutLine(proc) with
                        | None -> Ashes.IO.print("no output")
                        | Some(line) -> let _ = Ashes.Process.waitForExit(proc) in Ashes.IO.print(line)
            """;
        (await CompileRunCaptureAsync(src)).ShouldBe("hello\n");
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

        var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
        psi.RedirectStandardInput = stdin is not null;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
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

    private static bool CanRunWindowsPrograms()
    {
        return TestProcessHelper.CanRunWindowsExecutables();
    }
}
