using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Ashes.Lsp.Tests;

public sealed class LspHarness : IAsyncDisposable
{
    private static readonly SemaphoreSlim ServerLock = new(1, 1);

    private readonly Process _process;
    private readonly TimeSpan _timeout;
    private readonly string _lspAssemblyPath;
    private bool _disposed;
    private bool _shutdownRequested;
    private int _nextRequestId = 1;

    private LspHarness(Process process, string lspAssemblyPath, TimeSpan timeout)
    {
        _process = process;
        _lspAssemblyPath = lspAssemblyPath;
        _timeout = timeout;
    }

    public static async Task<LspHarness> StartAsync(TimeSpan? timeout = null)
    {
        await ServerLock.WaitAsync();

        try
        {
            var lspAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Ashes.Lsp.dll");
            File.Exists(lspAssemblyPath).ShouldBeTrue($"Expected LSP assembly at '{lspAssemblyPath}'");

            var startInfo = new ProcessStartInfo("dotnet", $"\"{lspAssemblyPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Ashes.Lsp process.");

            var harness = new LspHarness(process, lspAssemblyPath, timeout ?? TimeSpan.FromSeconds(10));
            await harness.InitializeAsync();
            return harness;
        }
        catch
        {
            ServerLock.Release();
            throw;
        }
    }

    public async Task<PublishedDiagnostics> DidOpenAsync(string uri, string text)
    {
        await SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                text
            }
        });

        return await WaitForDiagnosticsAsync(uri);
    }

    public async Task<PublishedDiagnostics> DidChangeAsync(string uri, string text)
    {
        await SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri },
            contentChanges = new[]
            {
                new { text }
            }
        });

        return await WaitForDiagnosticsAsync(uri);
    }

    public async Task<PublishedDiagnostics> DidCloseAsync(string uri)
    {
        await SendNotificationAsync("textDocument/didClose", new
        {
            textDocument = new { uri }
        });

        return await WaitForDiagnosticsAsync(uri);
    }

    public async Task<JsonElement?> HoverAsync(string uri, int line, int character)
    {
        var response = await SendRequestAsync("textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line, character }
        });

        var result = response.GetProperty("result");
        return result.ValueKind == JsonValueKind.Null ? null : result.Clone();
    }

    public async Task<JsonElement?> DefinitionAsync(string uri, int line, int character)
    {
        var response = await SendRequestAsync("textDocument/definition", new
        {
            textDocument = new { uri },
            position = new { line, character }
        });

        var result = response.GetProperty("result");
        return result.ValueKind == JsonValueKind.Null ? null : result.Clone();
    }

    public async Task ShutdownAsync()
    {
        ThrowIfDisposed();

        if (_process.HasExited || _shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        var response = await SendRequestAsync("shutdown", new { });
        response.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);

        await SendNotificationAsync("exit", null);

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess();
            throw new TimeoutException($"Timed out waiting for Ashes.Lsp to exit. Assembly: '{_lspAssemblyPath}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await ShutdownAsync();
                }
                catch
                {
                    TryKillProcess();
                }
            }
        }
        finally
        {
            _disposed = true;
            _process.Dispose();
            ServerLock.Release();
        }
    }

    private async Task InitializeAsync()
    {
        var response = await SendRequestAsync("initialize", new { });
        var capabilities = response.GetProperty("result").GetProperty("capabilities");
        capabilities.GetProperty("textDocumentSync").GetInt32().ShouldBe(1);
        capabilities.GetProperty("hoverProvider").GetBoolean().ShouldBeTrue();
        capabilities.GetProperty("definitionProvider").GetBoolean().ShouldBeTrue();
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters)
    {
        ThrowIfDisposed();

        var id = _nextRequestId++;
        await WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });

        while (true)
        {
            var message = await ReadMessageAsync();
            if (message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                return message;
            }
        }
    }

    private async Task SendNotificationAsync(string method, object? parameters)
    {
        ThrowIfDisposed();

        await WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
    }

    private async Task<PublishedDiagnostics> WaitForDiagnosticsAsync(string uri)
    {
        while (true)
        {
            var message = await ReadMessageAsync();
            if (!message.TryGetProperty("method", out var methodElement)
                || !string.Equals(methodElement.GetString(), "textDocument/publishDiagnostics", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = message.GetProperty("params");
            var messageUri = parameters.GetProperty("uri").GetString();
            if (!string.Equals(messageUri, uri, StringComparison.Ordinal))
            {
                continue;
            }

            var diagnostics = parameters
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Select(d => d.Clone())
                .ToArray();

            return new PublishedDiagnostics(messageUri!, diagnostics);
        }
    }

    private async Task WriteMessageAsync(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");

        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(header, cts.Token);
            await _process.StandardInput.BaseStream.WriteAsync(bytes, cts.Token);
            await _process.StandardInput.BaseStream.FlushAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw CreateTimeoutException("writing an LSP message");
        }
    }

    private async Task<JsonElement> ReadMessageAsync()
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync();
            if (line is null)
            {
                throw CreateProtocolException("LSP output closed before a complete message header was read.");
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength <= 0)
        {
            throw CreateProtocolException("Received an LSP message without a valid Content-Length header.");
        }

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            using var cts = new CancellationTokenSource(_timeout);
            try
            {
                var chunk = await _process.StandardOutput.BaseStream.ReadAsync(body.AsMemory(read, contentLength - read), cts.Token);
                if (chunk == 0)
                {
                    throw CreateProtocolException("LSP output closed before a complete message body was read.");
                }

                read += chunk;
            }
            catch (OperationCanceledException)
            {
                throw CreateTimeoutException("reading an LSP message body");
            }
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<string?> ReadHeaderLineAsync()
    {
        using var ms = new MemoryStream();

        while (true)
        {
            var b = new byte[1];
            using var cts = new CancellationTokenSource(_timeout);
            int read;

            try
            {
                read = await _process.StandardOutput.BaseStream.ReadAsync(b, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw CreateTimeoutException("reading an LSP message header");
            }

            if (read == 0)
            {
                return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
            }

            if (b[0] == '\r')
            {
                using var nextCts = new CancellationTokenSource(_timeout);
                var next = new byte[1];
                int nextRead;

                try
                {
                    nextRead = await _process.StandardOutput.BaseStream.ReadAsync(next, nextCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw CreateTimeoutException("reading an LSP message header terminator");
                }

                if (nextRead == 0)
                {
                    return Encoding.ASCII.GetString(ms.ToArray());
                }

                if (next[0] == '\n')
                {
                    return Encoding.ASCII.GetString(ms.ToArray());
                }

                ms.WriteByte(next[0]);
                continue;
            }

            ms.WriteByte(b[0]);
        }
    }

    private Exception CreateTimeoutException(string operation)
    {
        return new TimeoutException($"Timed out after {_timeout.TotalSeconds:0.#} seconds while {operation}. {BuildProcessContext()}");
    }

    private Exception CreateProtocolException(string message)
    {
        return new InvalidOperationException($"{message} {BuildProcessContext()}");
    }

    private string BuildProcessContext()
    {
        var state = _process.HasExited ? $"Process exited with code {_process.ExitCode}." : "Process is still running.";
        var stderr = string.Empty;

        try
        {
            if (_process.HasExited)
            {
                stderr = _process.StandardError.ReadToEnd();
            }
        }
        catch
        {
            stderr = string.Empty;
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? state
            : state + " stderr: " + stderr.Trim();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void TryKillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
        }
        catch
        {
        }
    }
}

public sealed record PublishedDiagnostics(string Uri, IReadOnlyList<JsonElement> Diagnostics);
