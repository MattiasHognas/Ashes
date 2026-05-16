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
                await using var stream = client.GetStream();
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: 127.0.0.1");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            expectedStdout: "hello from http\n");
    }

    [Test]
    public async Task Https_get_example_should_run_against_loopback_tls_fixture()
    {
        await RunExampleWithTlsServerAsync(
            "https_get.ash",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            expectedStdout: "hello from https\n");
    }

    [Test]
    public async Task Async_all_should_preserve_input_order_for_http_tasks_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(match Ashes.Async.run(async
    let responses = await Ashes.Async.all([
        Ashes.Http.get("http://127.0.0.1:8080/first"),
        Ashes.Http.get("http://127.0.0.1:8080/second")
    ])
    in match responses with
        | a :: b :: [] -> a + "," + b
        | _ -> "bad-shape") with
    | Ok(text) -> text
    | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 2,
            async client =>
            {
                await using var stream = client.GetStream();
                var request = await ReadTextAsync(stream, 4096);
                var responseBody = request.Contains("GET /first HTTP/1.1", StringComparison.Ordinal)
                    ? "first"
                    : request.Contains("GET /second HTTP/1.1", StringComparison.Ordinal)
                        ? "second"
                        : "unexpected";

                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{responseBody}");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            expectedStdout: "first,second\n");
    }

    [Test]
    public async Task Async_race_should_return_first_completed_http_task_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(match Ashes.Async.run(async
    await Ashes.Async.race([
        Ashes.Http.get("http://127.0.0.1:8080/slow"),
        Ashes.Http.get("http://127.0.0.1:8080/fast")
    ])) with
    | Ok(text) -> text
    | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 2,
            async client =>
            {
                await using var stream = client.GetStream();
                var request = await ReadTextAsync(stream, 4096);
                var isSlow = request.Contains("GET /slow HTTP/1.1", StringComparison.Ordinal);
                if (isSlow)
                {
                    await Task.Delay(250);
                }

                var responseBody = isSlow ? "slow" : "fast";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{responseBody}");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            expectedStdout: "fast\n");
    }

    [Test]
    public async Task Tcp_close_should_release_socket_and_allow_async_program_to_continue_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(match Ashes.Async.run(async
    let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(8080)
    in let _ = await Ashes.Net.Tcp.close(sock)
    in "cleanup-ok") with
    | Ok(text) -> text
    | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 1,
            async client =>
            {
                await using var stream = client.GetStream();
                await Task.Delay(200);
                var buffer = new byte[64];
                var bytesRead = await stream.ReadAsync(buffer);
                bytesRead.ShouldBe(0);
            },
            expectedStdout: "cleanup-ok\n");
    }

    [Test]
    public async Task Awaited_http_failure_should_propagate_error_through_async_program_against_loopback_fixture()
    {
        const string source = """
Ashes.IO.print(match Ashes.Async.run(async
    let response = await Ashes.Http.get("http://127.0.0.1:8080/fail")
    in "unreachable:" + response) with
    | Ok(text) -> text
    | Error(err) -> err)
""";

        await RunSourceWithServerAsync(
            source,
            expectedClientCount: 1,
            async client =>
            {
                await using var stream = client.GetStream();
                _ = await ReadTextAsync(stream, 4096);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 500 Internal Server Error\r\nConnection: close\r\n\r\nserver exploded");
                await stream.WriteAsync(response);
                await stream.FlushAsync();
            },
            expectedStdout: "HTTP 500\n");
    }

    [Test]
    public async Task Tcp_connect_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_connect.ash",
            async _ =>
            {
                await Task.Delay(200);
            },
            expectedStdout: "connected\n");
    }

    [Test]
    public async Task Tcp_send_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_send.ash",
            async client =>
            {
                await using var stream = client.GetStream();
                var sent = await ReadTextAsync(stream, 64);
                sent.ShouldBe("hello");
            },
            expectedStdout: "5\n");
    }

    [Test]
    public async Task Tcp_receive_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_receive.ash",
            async client =>
            {
                await using var stream = client.GetStream();
                var payload = Encoding.UTF8.GetBytes("hello-from-server");
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            },
            expectedStdout: "hello-from-server\n");
    }

    [Test]
    public async Task Tcp_close_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_close.ash",
            async _ =>
            {
                await Task.Delay(200);
            },
            expectedStdout: "closed\n");
    }

    private static async Task RunExampleWithServerAsync(string exampleName, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        var examplePath = Path.Combine(GetExamplesRoot(), exampleName);
        File.Exists(examplePath).ShouldBeTrue($"Expected example file '{examplePath}' to exist.");
        await RunPathWithServerAsync(examplePath, expectedClientCount: 1, handleClientAsync, expectedStdout);
    }

    private static async Task RunExampleWithTlsServerAsync(string exampleName, Func<SslStream, Task> handleClientAsync, string expectedStdout)
    {
        var examplePath = Path.Combine(GetExamplesRoot(), exampleName);
        File.Exists(examplePath).ShouldBeTrue($"Expected example file '{examplePath}' to exist.");
        await RunPathWithTlsServerAsync(examplePath, expectedClientCount: 1, handleClientAsync, expectedStdout);
    }

    private static async Task RunSourceWithServerAsync(string source, int expectedClientCount, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N") + ".ash");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        try
        {
            await File.WriteAllTextAsync(tempPath, source);
            await RunPathWithServerAsync(tempPath, expectedClientCount, handleClientAsync, expectedStdout);
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
        var tempSourcePath = await CreatePortSpecificExampleAsync(sourcePath, port);
        var startInfo = await CliTestHost.CreateStartInfoAsync("run", "--target", BackendFactory.DefaultForCurrentOS(), tempSourcePath);

        try
        {
            var serverTask = RunServerAsync(listener, expectedClientCount, handleClientAsync);
            var (exitCode, stdout, stderr) = await RunCliAsync(startInfo);
            var serverException = await serverTask;

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
        var tempSourcePath = await CreatePortSpecificExampleAsync(sourcePath, port);
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync("localhost");
        var startInfo = await CliTestHost.CreateStartInfoAsync("run", "--target", BackendFactory.DefaultForCurrentOS(), tempSourcePath);
        tlsHost.Configure(startInfo);

        try
        {
            var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync);
            var (exitCode, stdout, stderr) = await RunCliAsync(startInfo);
            var serverException = await serverTask;

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
                    var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    clients.Add(client);
                }

                await Task.WhenAll(clients.Select(handleClientAsync));
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
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
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

    private static string GetExamplesRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "examples"));
    }

    private static async Task<string> CreatePortSpecificExampleAsync(string examplePath, int port)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N") + ".ash");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var source = await File.ReadAllTextAsync(examplePath);
        await File.WriteAllTextAsync(tempPath, source.Replace("8080", port.ToString(), StringComparison.Ordinal));
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
