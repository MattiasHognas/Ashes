using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ashes.Dap;

/// <summary>
/// Debugger backend that drives LLDB through <c>lldb-dap</c>, the Debug
/// Adapter Protocol server that ships with LLDB. The backend acts as a DAP
/// client to the <c>lldb-dap</c> subprocess, translating the
/// <see cref="IDebuggerBackend"/> operations into DAP requests.
/// </summary>
public sealed partial class LldbDebuggerBackend : IDebuggerBackend
{
    private const string DefaultAdapterBinary = "lldb-dap";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private Process? _adapter;
    private Stream? _adapterIn;
    private int _seq;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly TaskCompletionSource _initializedEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<JsonElement>? _launchResponse;
    private readonly Dictionary<string, List<int>> _breakpointsByFile = [];
    private readonly object _writeLock = new();
    private int _lastThreadId;
    private string _launchError = string.Empty;

    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public async Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath, bool stopOnEntry)
    {
        var startInfo = CreateStartInfo(debuggerPath);

        try
        {
            _adapter = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw CreateStartFailure(startInfo.FileName, ex);
        }

        if (_adapter is null)
        {
            throw CreateStartFailure(startInfo.FileName, innerException: null);
        }

        if (await ExitedDuringStartupAsync(_adapter).ConfigureAwait(false))
        {
            var failure = await CreateStartupFailureAsync(startInfo, _adapter).ConfigureAwait(false);
            _adapter.Dispose();
            _adapter = null;
            throw failure;
        }

        _adapterIn = _adapter.StandardInput.BaseStream;
        _ = Task.Run(() => ReadAdapterOutputAsync(_adapter.StandardOutput.BaseStream));
        _ = Task.Run(() => DrainStreamAsync(_adapter.StandardError));

        _ = await SendRequestAsync("initialize", new
        {
            clientID = "ashes-dap",
            adapterID = "lldb-dap",
            linesStartAt1 = true,
            columnsStartAt1 = true,
            pathFormat = "path",
        }).ConfigureAwait(false);

        // lldb-dap answers the launch request only after configurationDone,
        // so the response is awaited later in RunAsync.
        _launchResponse = SendRequestWithoutAwaiting("launch", new
        {
            program,
            args = args ?? [],
            cwd,
            stopOnEntry,
        });

        await WaitForInitializedEventAsync().ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string? debuggerPath)
    {
        return new ProcessStartInfo(string.IsNullOrWhiteSpace(debuggerPath) ? DefaultAdapterBinary : debuggerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    public async Task SetBreakpointAsync(string filePath, int line)
    {
        if (!_breakpointsByFile.TryGetValue(filePath, out var lines))
        {
            lines = [];
            _breakpointsByFile[filePath] = lines;
        }

        if (!lines.Contains(line))
        {
            lines.Add(line);
        }

        // DAP setBreakpoints replaces all breakpoints for the file, so the
        // accumulated set is sent every time.
        _ = await SendRequestAsync("setBreakpoints", new
        {
            source = new { name = Path.GetFileName(filePath), path = filePath },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
        }).ConfigureAwait(false);
    }

    public async Task ContinueAsync()
    {
        _ = await SendRequestAsync("continue", new { threadId = _lastThreadId }).ConfigureAwait(false);
    }

    public async Task StepOverAsync()
    {
        _ = await SendRequestAsync("next", new { threadId = _lastThreadId }).ConfigureAwait(false);
    }

    public async Task StepInAsync()
    {
        _ = await SendRequestAsync("stepIn", new { threadId = _lastThreadId }).ConfigureAwait(false);
    }

    public async Task StepOutAsync()
    {
        _ = await SendRequestAsync("stepOut", new { threadId = _lastThreadId }).ConfigureAwait(false);
    }

    public async Task RunAsync()
    {
        _ = await SendRequestAsync("configurationDone", new { }).ConfigureAwait(false);

        if (_launchResponse is not null)
        {
            _ = await AwaitResponseAsync(_launchResponse, "launch").ConfigureAwait(false);
        }
    }

    public async Task<DapStackFrame[]> GetStackTraceAsync()
    {
        var body = await SendRequestAsync("stackTrace", new
        {
            threadId = _lastThreadId,
            startFrame = 0,
            levels = 20,
        }).ConfigureAwait(false);

        if (!body.TryGetProperty("stackFrames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. frames.EnumerateArray().Select(ParseStackFrame)];
    }

    public async Task<DapVariable[]> GetLocalsAsync()
    {
        var (localsReference, frameId) = await GetLocalsScopeReferenceAsync().ConfigureAwait(false);
        if (localsReference == 0)
        {
            return [];
        }

        var body = await SendRequestAsync("variables", new { variablesReference = localsReference }).ConfigureAwait(false);
        if (!body.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<DapVariable>();
        foreach (var variable in variables.EnumerateArray())
        {
            var name = GetStringProperty(variable, "name");
            if (name is null)
            {
                continue;
            }

            var value = GetStringProperty(variable, "value") ?? "";
            var type = GetStringProperty(variable, "type");
            var formattedValue = await AshesValueFormatter.FormatAsync(
                value,
                type,
                expression => EvaluateExpressionAsync(expression, frameId)).ConfigureAwait(false);

            result.Add(new DapVariable
            {
                Name = name,
                Value = string.IsNullOrWhiteSpace(formattedValue) ? value : formattedValue,
                Type = type,
                VariablesReference = 0,
            });
        }

        return [.. result];
    }

    public async Task TerminateAsync()
    {
        if (_adapter is not null && !_adapter.HasExited)
        {
            try
            {
                _ = await SendRequestAsync("disconnect", new { terminateDebuggee = true }).ConfigureAwait(false);
                await _adapter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                try { _adapter.Kill(); }
                catch (InvalidOperationException) { /* process already exited */ }
                catch (SystemException) { /* process no longer accessible */ }
            }
        }
    }

    private async Task<(int LocalsReference, int FrameId)> GetLocalsScopeReferenceAsync()
    {
        var stackBody = await SendRequestAsync("stackTrace", new
        {
            threadId = _lastThreadId,
            startFrame = 0,
            levels = 1,
        }).ConfigureAwait(false);

        if (!stackBody.TryGetProperty("stackFrames", out var frames)
            || frames.ValueKind != JsonValueKind.Array
            || frames.GetArrayLength() == 0
            || !frames[0].TryGetProperty("id", out var frameIdElement))
        {
            return (0, 0);
        }

        var frameId = frameIdElement.GetInt32();
        var scopesBody = await SendRequestAsync("scopes", new { frameId }).ConfigureAwait(false);
        if (!scopesBody.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
        {
            return (0, frameId);
        }

        foreach (var scope in scopes.EnumerateArray())
        {
            var name = GetStringProperty(scope, "name");
            if (string.Equals(name, "Locals", StringComparison.OrdinalIgnoreCase)
                && scope.TryGetProperty("variablesReference", out var reference))
            {
                return (reference.GetInt32(), frameId);
            }
        }

        return (0, frameId);
    }

    private async Task<string?> EvaluateExpressionAsync(string expression, int frameId)
    {
        try
        {
            // "watch" context returns bare values; "repl" wraps results in
            // "(type) $N = ..." and mis-evaluates cast expressions.
            var body = await SendRequestAsync("evaluate", new
            {
                expression,
                context = "watch",
                frameId,
            }).ConfigureAwait(false);

            var result = GetStringProperty(body, "result");
            return result is null ? null : NormalizeEvaluateResult(result);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// LLDB expression results look like <c>(long) $0 = 42</c>; strip the
    /// type/name prefix so callers see just the value.
    /// </summary>
    private static string NormalizeEvaluateResult(string result)
    {
        var match = EvaluateResultRegex().Match(result);
        return match.Success ? match.Groups[1].Value : result;
    }

    [GeneratedRegex(@"^\([^)]*\)\s*\$?\w*\s*=\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex EvaluateResultRegex();

    private static DapStackFrame ParseStackFrame(JsonElement frame)
    {
        var sourceName = default(string);
        var sourcePath = default(string);
        if (frame.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            sourceName = GetStringProperty(source, "name");
            sourcePath = GetStringProperty(source, "path");
        }

        return new DapStackFrame
        {
            Id = frame.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Name = GetStringProperty(frame, "name") ?? "??",
            Source = (sourceName ?? sourcePath) is not null
                ? new DapSource { Name = sourceName, Path = sourcePath ?? sourceName }
                : null,
            Line = frame.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
            Column = frame.TryGetProperty("column", out var column) ? column.GetInt32() : 0,
        };
    }

    private static string? GetStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private async Task<JsonElement> SendRequestAsync(string command, object arguments)
    {
        var tcs = SendRequestWithoutAwaiting(command, arguments);
        return await AwaitResponseAsync(tcs, command).ConfigureAwait(false);
    }

    private TaskCompletionSource<JsonElement> SendRequestWithoutAwaiting(string command, object arguments)
    {
        if (_adapterIn is null)
        {
            throw new InvalidOperationException("lldb-dap is not running.");
        }

        var seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[seq] = tcs;

        var json = JsonSerializer.Serialize(new
        {
            seq,
            type = "request",
            command,
            arguments,
        }, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");

        try
        {
            lock (_writeLock)
            {
                _adapterIn.Write(header);
                _adapterIn.Write(bytes);
                _adapterIn.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pendingRequests.TryRemove(seq, out _);
            throw CreateCommandFailure(command, ex);
        }

        return tcs;
    }

    private async Task<JsonElement> AwaitResponseAsync(TaskCompletionSource<JsonElement> tcs, string command)
    {
        using var cts = new CancellationTokenSource(RequestTimeout);
        using var registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException($"Timed out waiting for lldb-dap response to '{command}'.")));

        var response = await tcs.Task.ConfigureAwait(false);

        if (response.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var message = GetStringProperty(response, "message") ?? $"lldb-dap rejected '{command}'.";
            throw new InvalidOperationException(message);
        }

        return response.TryGetProperty("body", out var body) ? body.Clone() : default;
    }

    private async Task WaitForInitializedEventAsync()
    {
        using var cts = new CancellationTokenSource(RequestTimeout);
        using var registration = cts.Token.Register(
            () => _initializedEvent.TrySetException(new TimeoutException("Timed out waiting for the lldb-dap initialized event.")));

        await _initializedEvent.Task.ConfigureAwait(false);
    }

    private async Task ReadAdapterOutputAsync(Stream output)
    {
        try
        {
            while (await ReadMessageAsync(output).ConfigureAwait(false) is { } json)
            {
                ProcessAdapterMessage(json);
            }
        }
        catch (ObjectDisposedException)
        {
            // lldb-dap process was disposed
        }
    }

    private void ProcessAdapterMessage(string json)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(json);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        switch (GetStringProperty(message, "type"))
        {
            case "response":
                if (message.TryGetProperty("request_seq", out var requestSeq)
                    && _pendingRequests.TryRemove(requestSeq.GetInt32(), out var tcs))
                {
                    tcs.TrySetResult(message);
                }

                break;
            case "event":
                ProcessAdapterEvent(message);
                break;
            default:
                break;
        }
    }

    private void ProcessAdapterEvent(JsonElement message)
    {
        var body = message.TryGetProperty("body", out var b) ? b : default;
        switch (GetStringProperty(message, "event"))
        {
            case "initialized":
                _initializedEvent.TrySetResult();
                break;
            case "stopped":
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("threadId", out var threadId))
                {
                    _lastThreadId = threadId.GetInt32();
                }

                OnStopped?.Invoke((body.ValueKind == JsonValueKind.Object ? GetStringProperty(body, "reason") : null) ?? "unknown");
                break;
            case "exited":
                var exitCode = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("exitCode", out var code)
                    ? code.GetInt32()
                    : 0;
                OnExited?.Invoke(exitCode);
                break;
            case "output":
                if (body.ValueKind == JsonValueKind.Object && GetStringProperty(body, "output") is { } text)
                {
                    OnOutput?.Invoke(text.TrimEnd('\n'));
                }

                break;
            default:
                break;
        }
    }

    private static async Task<string?> ReadMessageAsync(Stream input)
    {
        var contentLength = 0;
        while (await ReadHeaderLineAsync(input).ConfigureAwait(false) is { } headerLine)
        {
            if (headerLine.Length == 0)
            {
                break;
            }

            if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(headerLine["Content-Length:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            }
        }

        if (contentLength <= 0)
        {
            return null;
        }

        var buffer = new byte[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await input.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead)).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream input)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var read = await input.ReadAsync(buf.AsMemory(0, 1)).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var c = (char)buf[0];
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append(c);
        }
    }

    private static async Task DrainStreamAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0) { }
        }
        catch (ObjectDisposedException)
        {
            // lldb-dap process was disposed
        }
    }

    private static async Task<bool> ExitedDuringStartupAsync(Process process)
    {
        await Task.Delay(200).ConfigureAwait(false);
        return process.HasExited;
    }

    private async Task<InvalidOperationException> CreateStartupFailureAsync(ProcessStartInfo startInfo, Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }

        var stderr = await SafeReadToEndAsync(process.StandardError).ConfigureAwait(false);
        var stdout = await SafeReadToEndAsync(process.StandardOutput).ConfigureAwait(false);
        _launchError = FirstNonEmpty(stderr, stdout);

        var message = $"lldb-dap exited immediately when started as '{startInfo.FileName}'.";
        if (!string.IsNullOrWhiteSpace(_launchError))
        {
            message += $" {NormalizeDiagnostic(_launchError)}";
        }

        return new InvalidOperationException(message);
    }

    private static InvalidOperationException CreateStartFailure(string adapterPath, Exception? innerException)
    {
        var message = string.Equals(adapterPath, DefaultAdapterBinary, StringComparison.Ordinal)
            ? "Failed to start lldb-dap. Install LLDB (which provides lldb-dap) or set debuggerPath to the lldb-dap binary."
            : $"Failed to start lldb-dap using '{adapterPath}'.";
        if (innerException is not null)
        {
            message += $" {innerException.Message}";
        }

        return new InvalidOperationException(message, innerException);
    }

    private InvalidOperationException CreateCommandFailure(string command, Exception innerException)
    {
        if (_adapter is not null && _adapter.HasExited)
        {
            var message = $"lldb-dap exited before handling '{command}' (exit code {_adapter.ExitCode.ToString(CultureInfo.InvariantCulture)}).";
            if (!string.IsNullOrWhiteSpace(_launchError))
            {
                message += $" {NormalizeDiagnostic(_launchError)}";
            }

            return new InvalidOperationException(message, innerException);
        }

        return new InvalidOperationException($"Failed to send request to lldb-dap: {command}", innerException);
    }

    private static async Task<string> SafeReadToEndAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeDiagnostic(string diagnostic)
    {
        return Regex.Replace(diagnostic.Trim(), "\\s+", " ");
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    public void Dispose()
    {
        foreach (var (_, tcs) in _pendingRequests)
        {
            tcs.TrySetCanceled();
        }

        _pendingRequests.Clear();
        _adapter?.Dispose();
    }
}
