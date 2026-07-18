using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        var bytes = CompileForWindows("let z = 20 in let f = given (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

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
        var first = CompileForWindows("Ashes.IO.print(40 + 2)");
        var second = CompileForWindows("Ashes.IO.print(40 + 3)");

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
    public void Windows_backend_compile_should_support_user_external_imports()
    {
        var bytes = CompileForWindows(LowerProgram("""
            external lstrlen(Str) -> Int = "lstrlenA@KERNEL32.DLL"
            Ashes.IO.print(lstrlen("ash" + "es"))
            """));

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
        ContainsAscii(bytes, "lstrlenA").ShouldBeTrue();
        ContainsAscii(bytes, "KERNEL32.DLL").ShouldBeTrue();
    }

    [Test]
    public void Windows_backend_compile_should_support_debug_info_programs()
    {
        var bytes = new WindowsX64LlvmBackend().Compile(
            LowerExpression("let z = 20 in let f = given (x) -> x + z in f(22)"),
            new BackendCompileOptions(BackendOptimizationLevel.O0, EmitDebugInfo: true));

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        AssertWindowsLlvmCompiles(LowerExpression("if (1.5 + 2.5) == 4.0 then 42 else 0"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> x + a + b | _ -> 0"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        AssertWindowsLlvmCompiles(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> a + b
            """));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_closure_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("let z = 20 in let f = given (x) -> x + z in f(22)"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)"""));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_print_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("Ashes.IO.write(\"hi\")"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_program_args_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("match Ashes.IO.args with | a :: b :: [] -> 1 | _ -> 0"));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_read_line_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("""match Ashes.IO.readLine(Unit) with | None -> 0 | Some(text) -> 1"""));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_file_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("""match Ashes.File.exists("present.txt") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0"""));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_network_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("""match await Ashes.Http.get("http://127.0.0.1:8080/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_https_network_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("""match await Ashes.Http.get("https://localhost/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Windows_backend_llvm_support_check_should_accept_panic_programs()
    {
        AssertWindowsLlvmCompiles(LowerExpression("Ashes.IO.panic(\"boom\")"));
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("let z = 20 in let f = given (x) -> x + z in Ashes.IO.print(f(22))").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_program_args_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")",
            ["first", "second"]).ConfigureAwait(false);
        result.Stdout.ShouldBe("first:second\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_user_external_imports()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(LowerProgram("""
            external lstrlen(Str) -> Int = "lstrlenA@KERNEL32.DLL"
            Ashes.IO.print(lstrlen("ash" + "es"))
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("5\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_read_line_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "hello\r\n").ConfigureAwait(false);
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_return_none_at_read_line_eof()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "").ConfigureAwait(false);
        result.Stdout.ShouldBe("none\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_file_read_text_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "hello.txt"), "hello").ConfigureAwait(false);

            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("hello.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
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
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("missing.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
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
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, "invalid_utf8.bin"), [0xFF, 0xFE, 0xFD]).ConfigureAwait(false);

            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.readText("invalid_utf8.bin") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
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
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithWindowsLlvmAsync(
                """match Ashes.File.writeText("out.txt")("hello") with | Error(msg) -> Ashes.IO.print(msg) | Ok(_) -> match Ashes.File.readText("out.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_https_against_loopback_tls_fixture()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                // Drain the client's request before responding. If the server closes the
                // connection while the client's HTTP request is still unread, the OS issues a
                // TCP RST instead of a graceful FIN, which the client observes as a connection
                // reset (errno 10054) and turns this into a flaky failure.
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

        result.Stdout.ShouldBe("hello from https\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_report_https_trust_failures_against_loopback_tls_fixture()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            trustServerCertificate: false,
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_report_https_hostname_mismatches_against_loopback_tls_fixture()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "127.0.0.1",
            certificateHost: "localhost",
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: NotValidForName\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_return_first_completed_https_race_task_against_loopback_tls_fixture()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Async.race([Ashes.Http.get("https://__HOST__:__PORT__/a"), Ashes.Http.get("https://__HOST__:__PORT__/b")]) with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("Host: localhost");
                // Both endpoints respond with the same body ("ok") so the test result is
                // deterministic regardless of which Async.race task technically completes first
                // (avoids timing flakiness on Wine / CI runners).
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost",
            expectedClientCount: 2,
            tolerateClientDisconnect: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_treat_https_close_notify_eof_as_end_of_body()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/empty") with | Ok(text) -> if text == "" then "empty" else "bad:" + text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET /empty HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);

        result.Stdout.ShouldBe("empty\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_uncons_unicode_scalars()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.Text.uncons("é!") with | None -> Ashes.IO.print("empty") | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("é|!\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_parse_integers()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.Text.parseInt("-42") with | Ok(value) -> Ashes.IO.print(value) | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_parse_floats_with_exponents()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """match Ashes.Text.parseFloat("1e3") with | Ok(value) -> if value == 1000.0 then Ashes.IO.print("ok") else Ashes.IO.print("bad") | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_format_ints_floats_and_hex()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """Ashes.IO.print(Ashes.Text.fromInt(-42) + "|" + Ashes.Text.fromFloat(0.0 - 12.25) + "|" + Ashes.Text.toHex(48879))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42|-12.25|0xbeef\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_format_floats_with_fixed_precision()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(
            """Ashes.IO.print(Ashes.Text.formatFloat(3.141592653589793)(9) + "|" + Ashes.Text.formatFloat(0.0 - 12.25)(3) + "|" + Ashes.Text.formatFloat(2.5)(0))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("3.141592654|-12.250|3\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_file_exists_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "present.txt"), "x").ConfigureAwait(false);

            var result = await CompileRunWithWindowsLlvmAsync(
                """match (Ashes.File.exists("present.txt"), Ashes.File.exists("missing.txt")) with | (Ok(a), Ok(b)) -> Ashes.IO.print((if a then "true" else "false") + ":" + (if b then "true" else "false")) | (Error(msg), _) -> Ashes.IO.print(msg) | (_, Error(msg)) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
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
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_heap_backed_tuple_and_list_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_string_compare_and_concat_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("if (\"he\" + \"llo\") == \"hello\" then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_print_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("Ashes.IO.write(\"hi\")").ConfigureAwait(false);
        result.Stdout.ShouldBe("hi");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_adt_field_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_panic_programs()
    {
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        var result = await CompileRunWithWindowsLlvmAsync("Ashes.IO.panic(\"boom\")", expectedExitCode: 1).ConfigureAwait(false);
        result.Stdout.ShouldBe("boom\n");
    }

    private static byte[] CompileForWindows(string source)
    {
        var ir = LowerExpression(source);
        return CompileForWindows(ir);
    }

    private static byte[] CompileForWindows(IrProgram ir)
    {
        return new WindowsX64LlvmBackend().Compile(ir);
    }

    private static void AssertWindowsLlvmCompiles(IrProgram ir)
    {
        var bytes = CompileForWindows(ir);
        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)'M');
        bytes[1].ShouldBe((byte)'Z');
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

    private static async Task<ExecutionResult> CompileRunWithWindowsLlvmAsync(
        string source,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithWindowsLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode, environmentVariables).ConfigureAwait(false);
    }

    private static async Task<ExecutionResult> CompileRunWithWindowsLlvmAsync(
        IrProgram ir,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var exeBytes = new WindowsX64LlvmBackend().Compile(ir);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);

            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardInput = stdin is not null;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = workingDirectory ?? tmpDir;
            if (environmentVariables is not null)
            {
                foreach (var entry in environmentVariables)
                {
                    psi.Environment[entry.Key] = entry.Value;
                }
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
                await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            proc.ExitCode.ShouldBe(expectedExitCode, $"stderr: {stderr}");
            return new ExecutionResult(stdout, stderr, proc.ExitCode);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<ExecutionResult> CompileRunWithWindowsLlvmTlsLoopbackAsync(
        string sourceTemplate,
        Func<SslStream, Task> handleClientAsync,
        string host = "localhost",
        string? certificateHost = null,
        bool trustServerCertificate = true,
        int expectedClientCount = 1,
        bool allowServerHandshakeFailure = false,
        bool tolerateClientDisconnect = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync(certificateHost ?? host).ConfigureAwait(false);
        IReadOnlyDictionary<string, string>? environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SSL_CERT_FILE"] = TestProcessHelper.ConvertHostPathForWindowsExecution(
                trustServerCertificate ? tlsHost.TrustCertificatePath : tlsHost.UntrustedCertificatePath)
        };

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync, tolerateClientDisconnect);
        var result = await CompileRunWithWindowsLlvmAsync(source, environmentVariables: environmentVariables).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        if (allowServerHandshakeFailure && serverException is not null)
        {
            // The client rejects the certificate mid-handshake: Mbed TLS sends a fatal TLS alert,
            // which the .NET peer surfaces as an AuthenticationException (an abrupt close would
            // surface as an IOException instead).
            (serverException is AuthenticationException or IOException)
                .ShouldBeTrue(serverException.ToString());
        }
        else
        {
            serverException.ShouldBeNull(serverException?.ToString());
        }

        return result;
    }

    private static async Task<string> ReadTextAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        byte[] headerTerminator = "\r\n\r\n"u8.ToArray();

        while (total < buffer.Length)
        {
            try
            {
                using var readCts = new CancellationTokenSource(SocketTestConstants.ReadChunkTimeout);
                var count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (buffer.AsSpan(0, total).IndexOf(headerTerminator) >= 0)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    [Test]
    public async Task Windows_backend_llvm_should_serve_tls_echo_via_serve_tls()
    {
        // Server-side TLS under wine: the Ashes program terminates TLS (Ashes.Net.Tls.Server.serveTls,
        // self-signed cert), the C# test is an SslStream client trusting the test cert.
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = TlsEchoServerSource(port);

        // One worker: on real Windows serve is a fork-based multi-reactor (CreateProcessA relaunch +
        // an inherited shared listener), but Wine's cross-process inherited-socket accept is
        // unreliable, so we cap to a single reactor here — this still exercises the Windows forkWorkers
        // path (listener creation, the worker-listener global, the job object) and guards against a
        // single-process regression. Multi-process serving is verified on Linux (serveParallel test).
        var exeBytes = new WindowsX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_tls_{Guid.NewGuid():N}.exe");
        Process? proc = null;
        try
        {
            WriteSelfSignedServerPems(tmpDir);
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);
            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = Process.Start(psi)!;

            foreach (var payload in new[] { "win-tls-one", "win-tls-two" })
            {
                var reply = await TlsConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            if (proc is not null)
            {
                TryKillProcess(proc);
            }
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static string TlsEchoServerSource(int port) => $$"""
            import Ashes.IO
            import Ashes.Net.Tls
            import Ashes.Net.Tls.Server
            import Ashes.Async
            let onClient tls =
                async(match await Ashes.Net.Tls.receive(tls)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tls.send(tls)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tls.close(tls))
            in match Ashes.Async.run(Ashes.Net.Tls.Server.serveTls({{port}})("cert.pem")("key.pem")(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

    private static void WriteSelfSignedServerPems(string directory)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(directory, "cert.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
    }

    private static async Task<string> TlsConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await using (tls.ConfigureAwait(false))
                {
                    await tls.AuthenticateAsClientAsync("localhost").WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await tls.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await tls.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_serve_http_over_the_tcp_server()
    {
        // HTTP layer coverage under wine: Ashes.Http.Server.serve parses the request line, routes on
        // the path, and writes an HTTP/1.1 response; the C# test drives it with raw GETs over loopback.
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
            import Ashes.IO
            import Ashes.Http.Server
            import Ashes.Async
            let route req =
                async(match Ashes.Http.Server.path(req) with
                    | "/health" -> Ashes.Http.Server.text(200)("ok")
                    | "/" -> Ashes.Http.Server.text(200)("hello from ashes")
                    | _p -> Ashes.Http.Server.text(404)("not found"))
            in match Ashes.Async.run(Ashes.Http.Server.serve({{port}})(route)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var exeBytes = new WindowsX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_http_{Guid.NewGuid():N}.exe");
        Process? proc = null;
        try
        {
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);
            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = Process.Start(psi)!;

            var health = await HttpGetRawWithRetryAsync(port, "/health").ConfigureAwait(false);
            health.ShouldContain("HTTP/1.1 200 OK");
            health.ShouldEndWith("ok");

            var missing = await HttpGetRawWithRetryAsync(port, "/nope").ConfigureAwait(false);
            missing.ShouldContain("HTTP/1.1 404 Not Found");
            missing.ShouldEndWith("not found");
        }
        finally
        {
            if (proc is not null)
            {
                TryKillProcess(proc);
            }
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<string> HttpGetRawWithRetryAsync(int port, string path)
    {
        var request = $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(request)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_run_a_tcp_echo_server_via_serve()
    {
        // Server-side coverage under wine: the Ashes program is the LISTENER
        // (Ashes.Net.Tcp.Server.serve), the C# test is the CLIENT connecting in over loopback.
        // Exercises the win-x64 winsock socket/bind/listen/accept path and the accept-park on
        // WSAPoll readiness, plus the serve accept loop.
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Async
            let onClient client =
                async(match await Ashes.Net.Tcp.receive(client)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tcp.send(client)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Async.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var exeBytes = new WindowsX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_srv_{Guid.NewGuid():N}.exe");
        Process? proc = null;
        try
        {
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);
            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = Process.Start(psi)!;

            foreach (var payload in new[] { "win-one", "win-two", "win-three" })
            {
                var reply = await ConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            if (proc is not null)
            {
                TryKillProcess(proc);
            }
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_overlap_handlers_blocked_in_async_all()
    {
        // Run-queue scheduler parity regression: a spawned handler blocked in Ashes.Async.all must
        // not stop peer connections from advancing. Under the legacy re-entrant driver two handlers
        // that both aggregate sub-tasks serialized; on the scheduler (WSAPoll aggregate wait) they
        // overlap — four clients against a handler that Async.all-waits two 300 ms sleeps complete
        // in roughly one sleep, not four.
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = TcpAsyncAllEchoServerSource(port);

        var exeBytes = new WindowsX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_all_{Guid.NewGuid():N}.exe");
        Process? proc = null;
        try
        {
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);
            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = Process.Start(psi)!;

            // Wait for the listener, then fire four clients at once.
            _ = await ConnectSendReceiveWithRetryAsync(port, "warmup").ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(
                i => ConnectSendReceiveWithRetryAsync(port, $"conc-{i}"))).ConfigureAwait(false);
            sw.Stop();

            for (int i = 0; i < replies.Length; i++)
            {
                replies[i].ShouldBe($"echo: conc-{i}");
            }

            // Four serialized Async.all handlers would take >= 1200 ms; overlapping ones finish in
            // roughly one 300 ms sleep. Allow generous headroom for wine and a loaded CI box.
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(900),
                "handlers blocked in Async.all should overlap, not serialize");
        }
        finally
        {
            if (proc is not null)
            {
                TryKillProcess(proc);
            }
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Windows_backend_llvm_should_serve_http_concurrently_on_one_reactor()
    {
        // Concurrent HTTP through the run-queue scheduler on win-x64: many simultaneous requests
        // against a single reactor; every one must get a 200 and the server must stay up. One
        // worker because wine's cross-process inherited-socket accept is unreliable; the
        // multi-process path is covered by the Linux serveParallel test.
        if (!CanRunWindowsRuntimePrograms())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = HttpConcurrentServerSource(port);

        var exeBytes = new WindowsX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_httpc_{Guid.NewGuid():N}.exe");
        Process? proc = null;
        try
        {
            await File.WriteAllBytesAsync(exePath, exeBytes).ConfigureAwait(false);
            var psi = TestProcessHelper.CreateWindowsProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = Process.Start(psi)!;

            // Readiness.
            var warm = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            warm.ShouldContain("HTTP/1.1 200 OK");

            // Fire many concurrent requests at once; every one must get a 200 and the server must stay up.
            const int total = 60;
            int ok = await CountOkConcurrentHttpGetsAsync(port, total).ConfigureAwait(false);

            ok.ShouldBe(total);
            proc.HasExited.ShouldBeFalse();
        }
        finally
        {
            if (proc is not null)
            {
                TryKillProcess(proc);
            }
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static string TcpAsyncAllEchoServerSource(int port) => $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Async
            let onClient client =
                async(match await Ashes.Net.Tcp.receive(client)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Async.all([Ashes.Async.sleep(300), Ashes.Async.sleep(300)]) with
                            | Error(e2) -> Error(e2)
                            | Ok(_ts) ->
                                match await Ashes.Net.Tcp.send(client)("echo: " + msg) with
                                    | Error(e3) -> Error(e3)
                                    | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Async.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

    private static string HttpConcurrentServerSource(int port) => $$"""
            import Ashes.IO
            import Ashes.Http.Server
            import Ashes.Async
            let route req =
                async(Ashes.Http.Server.text(200)("ok"))
            in match Ashes.Async.run(Ashes.Http.Server.serve({{port}})(route)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

    private static async Task<int> CountOkConcurrentHttpGetsAsync(int port, int total)
    {
        var tasks = new List<Task<bool>>(total);
        for (int i = 0; i < total; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var r = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
                return r.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal);
            }));
        }
        bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        int ok = 0;
        foreach (bool r in results)
        {
            if (r)
            {
                ok++;
            }
        }

        return ok;
    }

    private static IrProgram LowerProgramWithImports(string source)
    {
        var parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var importedStdModules = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new Diagnostics();
        var program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static int GetFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<string> ConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool CanRunWindowsRuntimePrograms()
    {
        return TestProcessHelper.CanRunWindowsExecutables();
    }

    private static bool ContainsAscii(byte[] bytes, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
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
