using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Ashes.Backend.Backends;
using Shouldly;

namespace Ashes.Tests;

public sealed class ExampleSocketFixtureTests
{
    private static readonly SemaphoreSlim SocketExampleGate = new(1, 1);

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
    public async Task Tcp_connect_example_should_run_against_loopback_fixture()
    {
        await RunExampleWithServerAsync(
            "tcp_connect.ash",
            async client =>
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
            async client =>
            {
                await Task.Delay(200);
            },
            expectedStdout: "closed\n");
    }

    private static async Task RunExampleWithServerAsync(string exampleName, Func<TcpClient, Task> handleClientAsync, string expectedStdout)
    {
        await SocketExampleGate.WaitAsync();
        try
        {
            var examplePath = Path.Combine(GetExamplesRoot(), exampleName);
            File.Exists(examplePath).ShouldBeTrue($"Expected example file '{examplePath}' to exist.");

            using var listener = new TcpListener(IPAddress.Loopback, 8080);
            listener.Start();

            var serverTask = RunServerAsync(listener, handleClientAsync);
            var (exitCode, stdout, stderr) = await RunCliAsync(examplePath);
            var serverException = await serverTask;

            serverException.ShouldBeNull(serverException?.ToString());
            exitCode.ShouldBe(0, stderr);
            stdout.ShouldBe(expectedStdout, customMessage: stderr);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            SocketExampleGate.Release();
        }
    }

    private static async Task<Exception?> RunServerAsync(TcpListener listener, Func<TcpClient, Task> handleClientAsync)
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

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(string examplePath)
    {
        var startInfo = await CliTestHost.CreateStartInfoAsync("run", "--target", BackendFactory.DefaultForCurrentOS(), examplePath);

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
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

    private static string GetExamplesRoot([CallerFilePath] string? callerFile = null)
    {
        var sourceDir = Path.GetDirectoryName(callerFile)!;
        return Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "examples"));
    }
}