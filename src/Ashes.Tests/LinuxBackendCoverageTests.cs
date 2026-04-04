using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxBackendCoverageTests
{
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
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.File.exists("present.txt") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_network_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.Http.get("http://127.0.0.1:8080/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_print_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.write(\"hi\")"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("let z = 20 in let f = fun (x) -> x + z in f(22)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""let mk = fun (x) -> fun (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)"""));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("""let mk = fun (x) -> fun (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""");
        result.Stdout.ShouldBe("42\n");
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
            ["first", "second"]);
        result.Stdout.ShouldBe("first:second\n");
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
            stdin: "hello\n");
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
            stdin: "");
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
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "hello.txt"), "hello");

            var result = await CompileRunWithLinuxLlvmAsync(
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
    public async Task Linux_backend_llvm_should_report_invalid_utf8_file_read_errors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, "invalid_utf8.bin"), [0xFF, 0xFE, 0xFD]);

            var result = await CompileRunWithLinuxLlvmAsync(
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
    public async Task Linux_backend_llvm_should_run_file_exists_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "present.txt"), "x");

            var result = await CompileRunWithLinuxLlvmAsync(
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
    public async Task Linux_backend_llvm_should_run_tcp_connect_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """match Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match Ashes.Net.Tcp.close(sock) with | Ok(_) -> Ashes.IO.print("ok") | Error(msg) -> Ashes.IO.print(msg)""",
            async _ => await Task.Delay(100));
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
            """match Ashes.Net.Tcp.connect("localhost")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match Ashes.Net.Tcp.close(sock) with | Ok(_) -> Ashes.IO.print("ok") | Error(msg) -> Ashes.IO.print(msg)""",
            async _ => await Task.Delay(100));
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
            """match Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match Ashes.Net.Tcp.send(sock)("hello") with | Ok(n) -> Ashes.IO.print(n) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                await using var stream = client.GetStream();
                (await ReadTextAsync(stream, 64)).ShouldBe("hello");
            });
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
            """match Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match Ashes.Net.Tcp.receive(sock)(64) with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                await using var stream = client.GetStream();
                var payload = Encoding.UTF8.GetBytes("hello");
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            });
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
            """match Ashes.Http.get("http://__HOST__:__PORT__/hello") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                await using var stream = client.GetStream();
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("GET /hello HTTP/1.1");
                request.ShouldContain("Host: 127.0.0.1");
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            });
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
            """match Ashes.Http.post("http://__HOST__:__PORT__/echo")("hello") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                await using var stream = client.GetStream();
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("POST /echo HTTP/1.1");
                request.ShouldContain("Content-Length: 5");
                request.ShouldContain("\r\n\r\nhello");
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nposted");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            });
        result.Stdout.ShouldBe("posted\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_not_supported()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Http.get("https://example.com") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""");
        result.Stdout.ShouldBe("https not supported\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_http_non_success_statuses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """match Ashes.Http.get("http://__HOST__:__PORT__/missing") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                await using var stream = client.GetStream();
                _ = await ReadTextAsync(stream, 4096);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\nmissing");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            });
        result.Stdout.ShouldBe("HTTP 404\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_float_arithmetic_and_comparisons()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_heap_backed_tuple_and_list_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)");
        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_string_compare_and_concat_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (\"he\" + \"llo\") == \"hello\" then Ashes.IO.print(42) else Ashes.IO.print(0)");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_print_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.write(\"hi\")");
        result.Stdout.ShouldBe("hi");
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
            """));
        result.Stdout.ShouldBe("42\n");
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

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.panic(\"boom\")", expectedExitCode: 1);
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

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(string source, IReadOnlyList<string>? args = null, string? stdin = null, string? workingDirectory = null, int expectedExitCode = 0)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithLinuxLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode);
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(IrProgram ir, IReadOnlyList<string>? args = null, string? stdin = null, string? workingDirectory = null, int expectedExitCode = 0)
    {
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(exePath, elfBytes);

#pragma warning disable CA1416
            File.SetUnixFileMode(exePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

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

            using var proc = await StartProcessWithRetryAsync(psi);
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
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmLoopbackAsync(string sourceTemplate, Func<TcpClient, Task> handleClientAsync, string host = "127.0.0.1")
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(), StringComparison.Ordinal);
        var serverTask = RunLoopbackServerAsync(listener, handleClientAsync);
        var result = await CompileRunWithLinuxLlvmAsync(source);
        var serverException = await serverTask;
        serverException.ShouldBeNull(serverException?.ToString());
        return result;
    }

    private static async Task<Process> StartProcessWithRetryAsync(ProcessStartInfo psi)
    {
        const int textFileBusyError = 26;
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return Process.Start(psi)!;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == textFileBusyError && attempt < maxAttempts - 1)
            {
                await Task.Delay(20 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("Failed to start process after retrying transient ETXTBSY errors.");
    }

    private static async Task<Exception?> RunLoopbackServerAsync(TcpListener listener, Func<TcpClient, Task> handleClientAsync)
    {
        try
        {
            using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            await handleClientAsync(client);
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

    private static async Task<string> ReadTextAsync(NetworkStream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            try
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (!stream.DataAvailable)
                {
                    break;
                }
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

    private readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
}
