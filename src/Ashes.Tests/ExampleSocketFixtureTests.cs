using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Ashes.Backend.Backends;
using Shouldly;
using TUnit.Core;

namespace Ashes.Tests;

public sealed class ExampleSocketFixtureTests
{
    [Test]
    public async Task Http_get_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "http_get.ash",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("GET / HTTP/1.1");
                    request.ShouldContain("Host: 127.0.0.1");

                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            },
            expectedStdout: "hello from http\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Https_get_example_should_run_against_loopback_tls_fixture()
    {
        await RunExampleWithTlsServerAsync(
            "https_get.ash",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            expectedStdout: "hello from https\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Async_all_should_preserve_input_order_for_http_tasks_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(
    match await Ashes.Async.all([
        Ashes.Http.get("http://127.0.0.1:8080/first"),
        Ashes.Http.get("http://127.0.0.1:8080/second")
    ]) with
        | Error(err) -> err
        | Ok(responses) ->
            match responses with
                | a :: b :: [] -> a + "," + b
                | _ -> "bad-shape")
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 2,
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    var responseBody = request.Contains("GET /first HTTP/1.1", StringComparison.Ordinal)
                        ? "first"
                        : request.Contains("GET /second HTTP/1.1", StringComparison.Ordinal)
                            ? "second"
                            : "unexpected";

                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{responseBody}");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            },
            expectedStdout: "first,second\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Async_race_should_return_first_completed_http_task_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(
    match await Ashes.Async.race([
        Ashes.Http.get("http://127.0.0.1:8080/slow"),
        Ashes.Http.get("http://127.0.0.1:8080/fast")
    ]) with
        | Ok(text) -> text
        | Error(err) -> err)
""";

        var fastResponded = new TaskCompletionSource();
        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 2,
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    var isSlow = request.Contains("GET /slow HTTP/1.1", StringComparison.Ordinal);

                    if (isSlow)
                    {
                        // Block until /fast has actually sent its response so the client deterministically
                        // observes /fast winning the race regardless of CI scheduling jitter.
                        await fastResponded.Task.WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);

                        // Never send a body for /slow. Hold the connection open until the client process
                        // exits (which closes the socket) so /fast is unambiguously the first — and only —
                        // completed task the runtime observes. This removes any reliance on a wall-clock gap.
                        await WaitForClientCloseAsync(stream).ConfigureAwait(false);
                        return;
                    }

                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nfast");
                    try
                    {
                        await stream.WriteAsync(response).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // The race loser's connection may have been closed by the client; ignore.
                    }

                    fastResponded.TrySetResult();
                }
            },
            expectedStdout: "fast\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Tcp_close_should_release_socket_and_allow_async_program_to_continue_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(
    match await Ashes.Net.Tcp.connect("127.0.0.1")(8080) with
        | Error(err) -> err
        | Ok(sock) ->
            match await Ashes.Net.Tcp.close(sock) with
                | Ok(_) -> "cleanup-ok"
                | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 1,
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    var buffer = new byte[64];
                    var bytesRead = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    bytesRead.ShouldBe(0);
                }
            },
            expectedStdout: "cleanup-ok\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Awaited_http_failure_should_propagate_error_through_async_program_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(
    match await Ashes.Http.get("http://127.0.0.1:8080/fail") with
        | Ok(response) -> "unreachable:" + response
        | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 1,
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error\r\nConnection: close\r\n\r\nserver exploded");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            },
            expectedStdout: "HTTP 500\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Tcp_connect_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_connect.ash",
            async _ =>
            {
                await Task.Delay(200).ConfigureAwait(false);
            },
            expectedStdout: "connected\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Tcp_send_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_send.ash",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var sent = await ReadTextAsync(stream, 64).ConfigureAwait(false);
                    sent.ShouldBe("hello");
                }
            },
            expectedStdout: "5\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Tcp_receive_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_receive.ash",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var payload = Encoding.UTF8.GetBytes("hello-from-server");
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            },
            expectedStdout: "hello-from-server\n").ConfigureAwait(false);
    }

    [Test]
    public async Task Tcp_close_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_close.ash",
            async _ =>
            {
                await Task.Delay(200).ConfigureAwait(false);
            },
            expectedStdout: "closed\n").ConfigureAwait(false);
    }

    private static async Task RunExampleWithServerAsync(string exampleName, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        var examplePath = Path.Combine(GetExamplesRoot(), exampleName);
        File.Exists(examplePath).ShouldBeTrue($"Expected example file '{examplePath}' to exist.");
        await RunPathWithServerAsync(examplePath, expectedClientCount: 1, handleClientAsync, expectedStdout).ConfigureAwait(false);
    }

    private static async Task RunExampleWithTlsServerAsync(string exampleName, Func<SslStream, Task> handleClientAsync, string expectedStdout)
    {
        var examplePath = Path.Combine(GetExamplesRoot(), exampleName);
        File.Exists(examplePath).ShouldBeTrue($"Expected example file '{examplePath}' to exist.");
        await RunPathWithTlsServerAsync(examplePath, expectedClientCount: 1, handleClientAsync, expectedStdout).ConfigureAwait(false);
    }

    private static async Task RunSourceWithServerAsync(string source, int expectedClientCount, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N") + ".ash");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            await File.WriteAllTextAsync(tempPath, source).ConfigureAwait(false);
            await RunPathWithServerAsync(tempPath, expectedClientCount, handleClientAsync, expectedStdout).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task RunPathWithServerAsync(string sourcePath, int expectedClientCount, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tempSourcePath = await CreatePortSpecificExampleAsync(sourcePath, port).ConfigureAwait(false);
        var startInfo = await CliTestHost.CreateStartInfoAsync("run", "--target", BackendFactory.DefaultForCurrentOS(), tempSourcePath).ConfigureAwait(false);

        try
        {
            var serverTask = RunServerAsync(listener, expectedClientCount, handleClientAsync);
            var (exitCode, stdout, stderr) = await RunCliAsync(startInfo).ConfigureAwait(false);
            var serverException = await serverTask.ConfigureAwait(false);

            var serverDiagnostic = serverException is null
                ? null
                : $"{serverException}{Environment.NewLine}exit={exitCode}{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}";
            serverException.ShouldBeNull(serverDiagnostic);
            exitCode.ShouldBe(0, stderr);
            stdout.ShouldBe(expectedStdout, customMessage: stderr);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            TryDeleteFile(tempSourcePath);
        }
    }

    private static async Task RunPathWithTlsServerAsync(string sourcePath, int expectedClientCount, Func<SslStream, Task> handleClientAsync, string expectedStdout)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tempSourcePath = await CreatePortSpecificExampleAsync(sourcePath, port).ConfigureAwait(false);
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync("localhost").ConfigureAwait(false);
        var startInfo = await CliTestHost.CreateStartInfoAsync("run", "--target", BackendFactory.DefaultForCurrentOS(), tempSourcePath).ConfigureAwait(false);
        tlsHost.Configure(startInfo);

        try
        {
            var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync);
            var (exitCode, stdout, stderr) = await RunCliAsync(startInfo).ConfigureAwait(false);
            var serverException = await serverTask.WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);

            var serverDiagnostic = serverException is null
                ? null
                : $"{serverException}{Environment.NewLine}exit={exitCode}{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}";
            serverException.ShouldBeNull(serverDiagnostic);
            exitCode.ShouldBe(0, stderr);
            stdout.ShouldBe(expectedStdout, customMessage: stderr);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            TryDeleteFile(tempSourcePath);
        }
    }

    private static async Task<Exception?> RunServerAsync(TcpListener listener, int expectedClientCount, Func<TcpClient, Task> handleClientAsync)
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

                await Task.WhenAll(clients.Select(handleClientAsync)).ConfigureAwait(false);
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(SocketTestConstants.ProcessExitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
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
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new TimeoutException($"Compiled CLI process exceeded {SocketTestConstants.ProcessExitTimeout}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task WaitForClientCloseAsync(Stream stream)
    {
        var buffer = new byte[256];
        try
        {
            using var readCts = new CancellationTokenSource(SocketTestConstants.SocketTimeout);
            while (true)
            {
                var count = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    // The client closed its end of the connection (it exited after observing /fast win).
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client did not close in time; tear down anyway so the server task can complete.
        }
        catch (IOException)
        {
            // Connection reset/closed by the client; treat as a normal teardown.
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

    private static string GetExamplesRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "examples"));
    }

    private static async Task<string> CreatePortSpecificExampleAsync(string examplePath, int port)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N") + ".ash");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var source = await File.ReadAllTextAsync(examplePath).ConfigureAwait(false);
        await File.WriteAllTextAsync(tempPath, source.Replace("8080", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ConfigureAwait(false);
        return tempPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
