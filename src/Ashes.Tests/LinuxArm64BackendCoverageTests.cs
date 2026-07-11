using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxArm64BackendCoverageTests
{
    private const string HttpsProgram = """match await Ashes.Http.get("https://localhost/") with | Ok(text) -> text | Error(msg) -> msg""";
    private static readonly string[] LinuxArm64EmulatorCandidates = ["qemu-aarch64", "qemu-aarch64-static"];
    private static readonly string[] LinuxArm64SysrootCandidates = ["/usr/aarch64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/local/aarch64-linux-gnu", "/opt/aarch64-linux-gnu"];

    [Test]
    public void Linux_arm64_backend_compile_should_link_hermetic_mbedtls_payload_for_https_programs()
    {
        var bytes = CompileForLinuxArm64(HttpsProgram);

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
        // Error-table strings from the statically linked Mbed TLS bitcode payload.
        ContainsAscii(bytes, "SSL - The connection indicated an EOF").ShouldBeTrue();
        ContainsAscii(bytes, "X509 - Certificate verification failed").ShouldBeTrue();
    }

    [Test]
    public void Linux_arm64_backend_compile_should_not_link_hermetic_mbedtls_payload_for_plain_programs()
    {
        var bytes = CompileForLinuxArm64("Ashes.IO.print(42)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
        ContainsAscii(bytes, "SSL - The connection indicated an EOF").ShouldBeFalse();
        ContainsAscii(bytes, "X509 - Certificate verification failed").ShouldBeFalse();
    }

    [Test]
    public void Linux_arm64_backend_compile_should_support_user_external_imports()
    {
        var bytes = new LinuxArm64LlvmBackend().Compile(LowerProgram("""
            external strlen(Str) -> Int = "strlen@libc.so.6"
            Ashes.IO.print(strlen("ash" + "es"))
            """));

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_run_https_against_loopback_tls_fixture()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);

        result.Stdout.ShouldBe("hello from https\n");
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_run_both_and_https_in_one_image_coexisting()
    {
        // CO-3: an arm64 image that carries the `both` parallel runtime (PT_TLS + local-exec arena)
        // AND dlopen's rustls for a real TLS handshake. The main-thread arena resolves through the
        // loader-reserved PT_TLS slot (the entry prologue must not clobber the loader's TPIDR_EL0),
        // and `both`'s deterministic fork/join computes correctly alongside the live TLS session.
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
            """
            let doubled =
                match Ashes.Parallel.both(given (u) -> 3 + 4)(given (u) -> 5 + 6) with
                    | (a, b) -> a + b
            in Ashes.IO.print(Ashes.Text.fromInt(doubled) + "|" + (match await Ashes.Http.get("https://__HOST__:__PORT__/") with
                | Ok(text) -> text
                | Error(msg) -> msg))
            """,
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);

        result.Stdout.ShouldBe("18|hello from https\n");
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_run_queued_parallel_reduce_in_list_order()
    {
        // CO-25: the queued Parallel.reduce runtime on arm64 (TPIDR_EL0 worker TLS blocks, ldaxr/stlxr
        // atomics, futex publish/await). The combine is order-sensitive (string join), so the output
        // proves the fixed list-order merge regardless of which worker computed which element; the
        // odd element count exercises the pairwise merge tree's promotion rounds.
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmAsync(LowerProgramWithImports("""
            import Ashes.Parallel

            let recursive range lo hi =
                if lo >= hi
                then []
                else lo :: range(lo + 1)(hi)

            let joined = Ashes.Parallel.reduce(given (a) -> given (b) -> a + "," + b)("")(given (x) -> Ashes.Text.fromInt(x * x))(range(0)(7))

            Ashes.IO.print(joined)
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("0,1,4,9,16,25,36\n");
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_run_user_external_imports()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmAsync(LowerProgram("""
            external strlen(Str) -> Int = "strlen@libc.so.6"
            Ashes.IO.print(strlen("ash" + "es"))
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("5\n");
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_report_https_trust_failures_against_loopback_tls_fixture()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
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
    public async Task Linux_arm64_backend_llvm_should_report_https_hostname_mismatches_against_loopback_tls_fixture()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
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
    public async Task Linux_arm64_backend_llvm_should_return_first_completed_http_race_task_against_loopback_fixture()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        // The race semantics, error propagation through Async.run, and dual-task lifecycle are
        // verified using plain HTTP rather than HTTPS: running two full TLS handshakes inside
        // qemu-aarch64 user-mode emulation is the slowest path in the arm64 test suite and was
        // observed to exceed SocketTestConstants.ProcessExitTimeout on loaded CI runners. The
        // equivalent HTTPS race coverage continues to run natively on Linux x64 and Windows x64.
        var result = await CompileRunWithLinuxArm64LlvmHttpLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Async.race([Ashes.Http.get("http://__HOST__:__PORT__/a"), Ashes.Http.get("http://__HOST__:__PORT__/b")]) with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("Host: 127.0.0.1");
                    // Both endpoints respond with the same body ("ok") so the test result is
                    // deterministic regardless of which Async.race task technically completes first
                    // (avoids timing flakiness on QEMU/loaded CI runners).
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nok");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            },
            expectedClientCount: 2,
            tolerateClientDisconnect: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_arm64_backend_llvm_should_treat_https_close_notify_eof_as_end_of_body()
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
        {
            return;
        }

        var result = await CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
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
    public async Task Linux_arm64_backend_llvm_should_serve_tls_echo_via_serve_tls()
    {
        // Server-side TLS under qemu-aarch64: the Ashes program terminates TLS
        // (Ashes.Net.Tls.Server.serveTls, self-signed cert), the C# test is an SslStream client.
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
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

        var elfBytes = new LinuxArm64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_arm64_tls_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            WriteSelfSignedServerPems(tmpDir);
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            var psi = CreateLinuxArm64ProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = tmpDir;
            proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);

            foreach (var payload in new[] { "arm-tls-one", "arm-tls-two" })
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
    public async Task Linux_arm64_backend_llvm_should_serve_http_over_the_tcp_server()
    {
        // HTTP layer coverage under qemu-aarch64: Ashes.Http.Server.serve parses the request line,
        // routes on the path, and writes an HTTP/1.1 response; the C# test drives it with raw GETs.
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
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

        var elfBytes = new LinuxArm64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_arm64_http_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            var psi = CreateLinuxArm64ProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);

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
    public async Task Linux_arm64_backend_llvm_should_run_a_tcp_echo_server_via_serve()
    {
        // Server-side coverage: an Ashes program that is the LISTENER (Ashes.Net.Tcp.Server.serve),
        // run under qemu-aarch64, while the C# test acts as the CLIENT connecting in. Exercises the
        // arm64 socket()/bind/listen/accept4 syscalls, the cooperative accept-park on WaitSocketRead,
        // and the serve accept loop. qemu-user forwards the guest socket syscalls to the host, so the
        // emulated server binds a real loopback port the host client can reach.
        if (!TryResolveLinuxArm64ExecutionEnvironment(out _))
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

        var elfBytes = new LinuxArm64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_arm64_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            var psi = CreateLinuxArm64ProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);

            // Drive three sequential connections; serve handles one at a time.
            foreach (var payload in new[] { "arm64-one", "arm64-two", "arm64-three" })
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
                // Server not up yet (or between sequential connections) — retry until the accept timeout.
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static byte[] CompileForLinuxArm64(string source)
    {
        var ir = LowerExpression(source);
        return new LinuxArm64LlvmBackend().Compile(ir);
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxArm64LlvmAsync(
        string source,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        int expectedExitCode = 0)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithLinuxArm64LlvmAsync(ir, environmentVariables, expectedExitCode).ConfigureAwait(false);
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxArm64LlvmAsync(
        IrProgram ir,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        int expectedExitCode = 0)
    {
        var elfBytes = new LinuxArm64LlvmBackend().Compile(ir);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_arm64_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);

            var psi = CreateLinuxArm64ProcessStartInfo(exePath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            if (environmentVariables is not null)
            {
                foreach (var entry in environmentVariables)
                {
                    psi.Environment[entry.Key] = entry.Value;
                }
            }

            using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.WaitForExitAsync().WaitAsync(SocketTestConstants.ProcessExitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TryKillProcess(proc);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                throw new TimeoutException($"Compiled linux-arm64 process exceeded {SocketTestConstants.ProcessExitTimeout}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
            }

            var finalStdout = await stdoutTask.ConfigureAwait(false);
            var finalStderr = await stderrTask.ConfigureAwait(false);
            proc.ExitCode.ShouldBe(expectedExitCode, $"stderr: {finalStderr}");
            return new ExecutionResult(finalStdout, finalStderr, proc.ExitCode);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxArm64LlvmHttpLoopbackAsync(
        string sourceTemplate,
        Func<TcpClient, Task> handleClientAsync,
        string host = "127.0.0.1",
        int expectedClientCount = 1,
        bool tolerateClientDisconnect = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = RunHttpLoopbackServerAsync(listener, expectedClientCount, handleClientAsync, tolerateClientDisconnect);
        var result = await CompileRunWithLinuxArm64LlvmAsync(source).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        serverException.ShouldBeNull(serverException?.ToString());
        return result;
    }

    private static async Task<Exception?> RunHttpLoopbackServerAsync(
        TcpListener listener,
        int expectedClientCount,
        Func<TcpClient, Task> handleClientAsync,
        bool tolerateClientDisconnect)
    {
        try
        {
            var clients = new List<TcpClient>(expectedClientCount);
            try
            {
                for (var index = 0; index < expectedClientCount; index++)
                {
                    using var acceptCts = new CancellationTokenSource(SocketTestConstants.AcceptTimeout);
                    var client = await listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
                    client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    clients.Add(client);
                }

                await Task.WhenAll(clients.Select(client => HandleHttpClientAsync(client, handleClientAsync, tolerateClientDisconnect))).ConfigureAwait(false);
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }

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

    private static async Task HandleHttpClientAsync(TcpClient client, Func<TcpClient, Task> handleClientAsync, bool tolerateClientDisconnect)
    {
        try
        {
            await handleClientAsync(client).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
        }
        catch (IOException) when (tolerateClientDisconnect)
        {
            // The client may close its end before the server finishes writing (e.g. in race-style
            // scenarios where the loser's connection is abandoned). Treat as benign when opted-in.
        }
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxArm64LlvmTlsLoopbackAsync(
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
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync, tolerateClientDisconnect);
        IReadOnlyDictionary<string, string>? environmentVariables = trustServerCertificate
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SSL_CERT_FILE"] = tlsHost.TrustCertificatePath
            }
            : null;
        var result = await CompileRunWithLinuxArm64LlvmAsync(source, environmentVariables: environmentVariables).ConfigureAwait(false);
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

    // Stitches `import Ashes.*` module sources exactly like the CLI's standalone-compile path, so
    // stdlib bindings that live in embedded module source (e.g. Ashes.Parallel.reduce) resolve.
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

    private static bool ContainsAscii(byte[] bytes, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }

    private static bool TryResolveLinuxArm64ExecutionEnvironment(out LinuxArm64ExecutionEnvironment environment)
    {
        if (!OperatingSystem.IsLinux())
        {
            environment = default;
            return false;
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            environment = new LinuxArm64ExecutionEnvironment(null, null);
            return true;
        }

        var emulatorPath = FindCommandOnPath(LinuxArm64EmulatorCandidates);
        var sysrootPath = FindLinuxArm64Sysroot();
        if (emulatorPath is null || sysrootPath is null)
        {
            environment = default;
            return false;
        }

        environment = new LinuxArm64ExecutionEnvironment(emulatorPath, sysrootPath);
        return true;
    }

    private static ProcessStartInfo CreateLinuxArm64ProcessStartInfo(string exePath)
    {
        if (!TryResolveLinuxArm64ExecutionEnvironment(out var environment))
        {
            throw new InvalidOperationException("Linux arm64 execution requires either a native arm64 Linux host or qemu-aarch64 with an arm64 sysroot.");
        }

        if (environment.EmulatorPath is null)
        {
            return new ProcessStartInfo(exePath);
        }

        var psi = new ProcessStartInfo(environment.EmulatorPath);
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add(environment.SysrootPath!);
        psi.ArgumentList.Add(exePath);
        return psi;
    }

    private static string? FindCommandOnPath(IEnumerable<string> candidates)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
        {
            directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            directories.Add(Path.Combine(userProfile, ".local", "share", "ashes-tools", "qemu-user-static", "root", "usr", "bin"));
        }

        foreach (var candidate in candidates)
        {
            foreach (var directory in directories)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static string? FindLinuxArm64Sysroot()
    {
        foreach (var candidate in LinuxArm64SysrootCandidates)
        {
            if (File.Exists(Path.Combine(candidate, "lib", "ld-linux-aarch64.so.1")))
            {
                return candidate;
            }
        }

        return null;
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
        }
    }

    private readonly record struct LinuxArm64ExecutionEnvironment(string? EmulatorPath, string? SysrootPath);

    private readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
}
