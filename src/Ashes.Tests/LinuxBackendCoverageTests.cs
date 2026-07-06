using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        var bytes = CompileForLinux("let z = 20 in let f = given (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_not_emit_a_constant_image_for_simple_programs()
    {
        var first = CompileForLinux("Ashes.IO.print(40 + 2)");
        var second = CompileForLinux("Ashes.IO.print(40 + 3)");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Linux_backend_parallel_worker_stack_size_tunable_is_honored()
    {
        var ir = LowerExpression("match Ashes.Parallel.both(given (u) -> 3 + 4)(given (u) -> 5 + 6) with | (a, b) -> Ashes.IO.print(a + b)");

        byte[] unset = new LinuxX64LlvmBackend().Compile(ir);
        byte[] explicitDefault = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 1L * 1024 * 1024));
        byte[] custom = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 8L * 1024 * 1024));

        // Default is unchanged: leaving the tunable unset matches an explicit 1 MiB worker stack.
        explicitDefault.ShouldBe(unset);
        // The tunable is honored: an 8 MiB worker stack changes the emitted image.
        custom.ShouldNotBe(unset);
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
        AssertLinuxLlvmCompiles(LowerExpression("""match await Ashes.Http.get("http://127.0.0.1:8080/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_print_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.write(\"hi\")"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("let z = 20 in let f = given (x) -> x + z in f(22)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)"""));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("let z = 20 in let f = given (x) -> x + z in Ashes.IO.print(f(22))");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""");
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_preserve_nested_string_results_across_scope_cleanup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let prefix = "outer" in
            let text =
                match 1 with
                    | 1 ->
                        let suffix = "inner" in
                        prefix + suffix
                    | _ -> "bad"
            in Ashes.IO.print(text)
            """);
        result.Stdout.ShouldBe("outerinner\n");
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
    public async Task Linux_backend_llvm_should_uncons_unicode_scalars()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.uncons("é!") with | None -> Ashes.IO.print("empty") | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)""");
        result.Stdout.ShouldBe("é|!\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_uncons_long_json_like_strings()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let sample = "{ \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
            in match Ashes.Text.uncons(sample) with
            | None -> Ashes.IO.print("none")
            | Some((head, tail)) ->
                if head == "{"
                then if tail == " \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
                then Ashes.IO.print("ok")
                else Ashes.IO.print("bad")
                else Ashes.IO.print("bad")
            """);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_integers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseInt("-42") with | Ok(value) -> Ashes.IO.print(value) | Error(msg) -> Ashes.IO.print(msg)""");
        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_floats_with_exponents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseFloat("1e3") with | Ok(value) -> if value == 1000.0 then Ashes.IO.print("ok") else Ashes.IO.print("bad") | Error(msg) -> Ashes.IO.print(msg)""");
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_ints_floats_and_hex()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.fromInt(-42) + "|" + Ashes.Text.fromFloat(0.0 - 12.25) + "|" + Ashes.Text.toHex(48879))""");
        result.Stdout.ShouldBe("-42|-12.25|0xbeef\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_floats_with_fixed_precision()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.formatFloat(3.141592653589793)(9) + "|" + Ashes.Text.formatFloat(0.0 - 12.25)(3) + "|" + Ashes.Text.formatFloat(2.5)(0))""");
        result.Stdout.ShouldBe("3.141592654|-12.250|3\n");
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
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
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
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("localhost")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
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
            """match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match await Ashes.Net.Tcp.send(sock)("hello") with | Ok(n) -> Ashes.IO.print(n) | Error(msg) -> Ashes.IO.print(msg)""",
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
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.receive(sock)(64) with | Ok(text) -> text | Error(msg) -> msg)""",
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
            """Ashes.IO.print(match await Ashes.Http.get("http://__HOST__:__PORT__/hello") with | Ok(text) -> text | Error(msg) -> msg)""",
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
            """Ashes.IO.print(match await Ashes.Http.post("http://__HOST__:__PORT__/echo")("hello") with | Ok(text) -> text | Error(msg) -> msg)""",
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
    public async Task Linux_backend_llvm_should_run_https_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            host: "localhost");
        result.Stdout.ShouldBe("hello from https\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_trust_failures_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            trustServerCertificate: false,
            allowServerHandshakeFailure: true);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_hostname_mismatches_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            host: "127.0.0.1",
            certificateHost: "localhost",
            allowServerHandshakeFailure: true);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: NotValidForName\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_return_first_completed_https_race_task_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Async.race([Ashes.Http.get("https://__HOST__:__PORT__/a"), Ashes.Http.get("https://__HOST__:__PORT__/b")]) with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("Host: localhost");
                // Both endpoints respond with the same body ("ok") so the test result is
                // deterministic regardless of which Async.race task technically completes first
                // (avoids timing flakiness on loaded CI runners).
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            host: "localhost",
            expectedClientCount: 2,
            tolerateClientDisconnect: true);

        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_treat_https_close_notify_eof_as_end_of_body()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/empty") with | Ok(text) -> if text == "" then "empty" else "bad:" + text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("GET /empty HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            host: "localhost");

        result.Stdout.ShouldBe("empty\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_http_non_success_statuses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("http://__HOST__:__PORT__/missing") with | Ok(text) -> text | Error(msg) -> msg)""",
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

    [Test]
    public async Task Linux_backend_llvm_should_serve_tls_echo_via_serve_tls()
    {
        // Server-side TLS: the Ashes program terminates TLS (Ashes.Net.Tls.Server.serveTls with a
        // self-signed cert), the C# test is an SslStream CLIENT that trusts the test cert via a
        // validation callback. Exercises the rustls server config build (certified key from PEMs),
        // the server half of the handshake (parking on WaitTlsWantRead/Write), and the shared TLS
        // send/receive/close paths on an accepted connection.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
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

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_tls_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            WriteSelfSignedServerPems(tmpDir);
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            proc = Process.Start(new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,
            })!;

            foreach (var payload in new[] { "tls-one", "tls-two" })
            {
                var reply = await TlsConnectSendReceiveWithRetryAsync(port, payload);
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

    /// <summary>
    /// Writes a self-signed ECDSA P-256 localhost certificate + PKCS#8 key as cert.pem / key.pem.
    /// ECDSA keeps handshakes cheap under qemu emulation (see TlsLoopbackTestHost).
    /// </summary>
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
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout);
                await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync("localhost").WaitAsync(SocketTestConstants.SocketTimeout);
                var outBytes = Encoding.UTF8.GetBytes(payload);
                await tls.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                var buffer = new byte[4096];
                int read = await tls.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_http_over_the_tcp_server()
    {
        // HTTP layer coverage: Ashes.Http.Server.serve parses the request line, routes on the path,
        // and writes an HTTP/1.1 response. The test drives it with raw HTTP GETs over loopback.
        if (!OperatingSystem.IsLinux())
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
                    | "/echo" -> Ashes.Http.Server.text(200)("body=" + Ashes.Http.Server.body(req))
                    | "/ua" ->
                        match Ashes.Http.Server.header(req)("user-agent") with
                            | Some(ua) -> Ashes.Http.Server.text(200)(ua)
                            | None -> Ashes.Http.Server.text(200)("no-ua")
                    | "/data" -> Ashes.Http.Server.json(200)("{\"ok\":true}")
                    | _p -> Ashes.Http.Server.text(404)("not found"))
            in match Ashes.Async.run(Ashes.Http.Server.serve({{port}})(route)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_http_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            proc = Process.Start(new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,
            })!;

            var health = await HttpGetRawWithRetryAsync(port, "/health");
            health.ShouldContain("HTTP/1.1 200 OK");
            health.ShouldContain("Content-Length: 2");
            health.ShouldEndWith("ok");

            var missing = await HttpGetRawWithRetryAsync(port, "/nope");
            missing.ShouldContain("HTTP/1.1 404 Not Found");
            missing.ShouldEndWith("not found");

            // Request body is available to the handler.
            var echoed = await HttpRequestRawWithRetryAsync(port,
                "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 9\r\nConnection: close\r\n\r\nhi-there!");
            echoed.ShouldEndWith("body=hi-there!");

            // Request headers are read case-insensitively (handler asks "user-agent"; client sends "User-Agent").
            var ua = await HttpRequestRawWithRetryAsync(port,
                "GET /ua HTTP/1.1\r\nHost: localhost\r\nUser-Agent: probe/2.0\r\nConnection: close\r\n\r\n");
            ua.ShouldEndWith("probe/2.0");

            // json() sets an application/json Content-Type.
            var data = await HttpGetRawWithRetryAsync(port, "/data");
            data.ShouldContain("Content-Type: application/json");
            data.ShouldEndWith("{\"ok\":true}");

            // A body larger than one read is buffered across receives (cross-read buffering).
            var bigBody = new string('A', 100_000);
            var bigEcho = await HttpRequestRawWithRetryAsync(port,
                $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {bigBody.Length}\r\nConnection: close\r\n\r\n{bigBody}");
            bigEcho.ShouldEndWith("body=" + bigBody);

            // Keep-alive: two requests on a single TCP connection, second response still correct.
            var (first, second) = await HttpTwoRequestsOneConnectionAsync(port,
                "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n",
                "GET /data HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            first.ShouldContain("HTTP/1.1 200 OK");
            first.ShouldContain("Connection: keep-alive");
            first.ShouldEndWith("ok");
            second.ShouldContain("Content-Type: application/json");
            second.ShouldEndWith("{\"ok\":true}");
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

    // Sends two requests on one persistent connection, returning both responses (keep-alive).
    private static async Task<(string First, string Second)> HttpTwoRequestsOneConnectionAsync(int port, string firstRequest, string secondRequest)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout);
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(firstRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                var firstBuffer = new byte[4096];
                int firstRead = await stream.ReadAsync(firstBuffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(secondRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var second = await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout);
                return (Encoding.UTF8.GetString(firstBuffer, 0, firstRead).Trim(), second.Trim());
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    private static Task<string> HttpGetRawWithRetryAsync(int port, string path)
        => HttpRequestRawWithRetryAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

    private static async Task<string> HttpRequestRawWithRetryAsync(int port, string request)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout);
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(request)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout)).Trim();
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_read_across_a_parking_receive()
    {
        // Regression: a spawned handler that accumulates across a receive which PARKS on epoll used to
        // overflow the stack (ashes_detached_wait_meta counted the mid-step task as runnable, forcing a
        // non-blocking spin that leaked per-wait stack scratch). The client sends the request in two
        // writes with a gap so the second receive parks; the handler must buffer and reply.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Async
            import Ashes.String
            let onClient client =
                async(let recursive loop buffered =
                    if Ashes.String.length(buffered) >= 11
                    then
                        match await Ashes.Net.Tcp.send(client)("got:" + buffered) with
                            | Error(e) -> Error(e)
                            | Ok(_n) -> await Ashes.Net.Tcp.close(client)
                    else
                        match await Ashes.Net.Tcp.receive(client)(65536) with
                            | Error(e2) -> Error(e2)
                            | Ok(chunk) -> loop(buffered + chunk)
                in loop(""))
            in match Ashes.Async.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_park_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            proc = Process.Start(new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,
            })!;

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout);
                    await using var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("hello")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                    await Task.Delay(250);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("-world")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout)).Trim();
                    reply.ShouldBe("got:hello-world");
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
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
    public async Task Linux_backend_llvm_should_serve_connections_concurrently()
    {
        // serve() spawns each handler (Ashes.Async.spawn), so a slow handler must not serialize
        // other connections: four simultaneous clients against a handler that sleeps 300 ms before
        // echoing should all complete in roughly one sleep, not four.
        if (!OperatingSystem.IsLinux())
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
                        match await Ashes.Async.sleep(300) with
                            | Error(e2) -> Error(e2)
                            | Ok(_t) ->
                                match await Ashes.Net.Tcp.send(client)("echo: " + msg) with
                                    | Error(e3) -> Error(e3)
                                    | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Async.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_conc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            proc = Process.Start(new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,
            })!;

            // Wait for the listener, then fire four clients at once.
            _ = await ConnectSendReceiveWithRetryAsync(port, "warmup");

            var sw = Stopwatch.StartNew();
            var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(
                i => ConnectSendReceiveWithRetryAsync(port, $"conc-{i}")));
            sw.Stop();

            for (int i = 0; i < replies.Length; i++)
            {
                replies[i].ShouldBe($"echo: conc-{i}");
            }

            // Four sequential 300 ms handlers would take >= 1200 ms; concurrent ones finish in
            // roughly one sleep. Allow generous headroom for a loaded CI box.
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(900),
                "connections should be served concurrently, not serialized");
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
    public async Task Linux_backend_llvm_should_run_a_tcp_echo_server_via_serve()
    {
        // Server-side coverage on native linux-x64: the Ashes program is the LISTENER
        // (Ashes.Net.Tcp.Server.serve), the C# test is the CLIENT connecting in. Exercises the
        // socket/bind/listen/accept4 syscalls and the accept-park on WaitSocketRead.
        if (!OperatingSystem.IsLinux())
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

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            proc = Process.Start(new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = tmpDir,
            })!;

            foreach (var payload in new[] { "linux-one", "linux-two", "linux-three" })
            {
                var reply = await ConnectSendReceiveWithRetryAsync(port, payload);
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
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout);
                await using var stream = client.GetStream();
                var outBytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                var buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout);
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
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

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        string source,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithLinuxLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode, environmentVariables);
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        IrProgram ir,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);

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

            using var proc = await TestProcessHelper.StartProcessAsync(psi);
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

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmTlsLoopbackAsync(
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
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync(certificateHost ?? host);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(), StringComparison.Ordinal);
        var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync, tolerateClientDisconnect);
        IReadOnlyDictionary<string, string>? environmentVariables = trustServerCertificate
            ? new Dictionary<string, string>
            {
                ["SSL_CERT_FILE"] = tlsHost.TrustCertificatePath
            }
            : null;
        var result = await CompileRunWithLinuxLlvmAsync(source, environmentVariables: environmentVariables);
        var serverException = await serverTask;
        if (allowServerHandshakeFailure && serverException is IOException ioException)
        {
            ioException.Message.ShouldContain("unexpected EOF");
        }
        else
        {
            serverException.ShouldBeNull(serverException?.ToString());
        }
        return result;
    }

    private static async Task<Exception?> RunLoopbackServerAsync(TcpListener listener, Func<TcpClient, Task> handleClientAsync)
    {
        try
        {
            using var acceptCts = new CancellationTokenSource(SocketTestConstants.AcceptTimeout);
            using var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
            client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
            client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
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
                var count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total >= headerTerminator.Length && buffer.AsSpan(0, total).IndexOf(headerTerminator) >= 0)
                {
                    break;
                }

                if (stream is NetworkStream networkStream && !networkStream.DataAvailable)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (total > 0)
            {
                break;
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
