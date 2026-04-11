using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ashes.Dap;

/// <summary>
/// Debugger backend that drives LLDB via the LLDB-MI (Machine Interface)
/// protocol.  LLDB-MI is a GDB-MI–compatible front-end shipped with LLDB
/// (<c>lldb-mi</c>) or built into <c>lldb</c> via
/// <c>--interpreter=mi2</c> (LLDB 18+).
/// </summary>
public sealed partial class LldbDebuggerBackend : IDebuggerBackend
{
    private Process? _lldb;
    private StreamWriter? _lldbIn;
    private int _tokenCounter;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();

    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public async Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath)
    {
        var lldbPath = debuggerPath ?? "lldb-mi";
        var psi = new ProcessStartInfo(lldbPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--interpreter=mi2");
        psi.ArgumentList.Add(program);

        if (cwd is not null)
        {
            psi.WorkingDirectory = cwd;
        }

        _lldb = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start LLDB-MI.");
        _lldbIn = _lldb.StandardInput;
        _lldbIn.AutoFlush = true;

        // Start reading LLDB output and draining stderr to prevent pipe buffer deadlocks
        _ = Task.Run(() => ReadOutputAsync(_lldb.StandardOutput));
        _ = Task.Run(() => DrainStreamAsync(_lldb.StandardError));

        // Wait for initial prompt
        await Task.Delay(200);

        // Set program arguments if provided
        if (args is not null && args.Length > 0)
        {
            var escapedArgs = string.Join(" ", args.Select(EscapeArg));
            await SendCommandAsync($"-exec-arguments {escapedArgs}");
        }
    }

    public async Task SetBreakpointAsync(string filePath, int line)
    {
        await SendCommandAsync(BuildBreakpointInsertCommand(filePath, line));
    }

    public async Task ContinueAsync()
    {
        await SendCommandAsync("-exec-continue");
    }

    public async Task StepOverAsync()
    {
        await SendCommandAsync("-exec-next");
    }

    public async Task StepInAsync()
    {
        await SendCommandAsync("-exec-step");
    }

    public async Task StepOutAsync()
    {
        await SendCommandAsync("-exec-finish");
    }

    public async Task RunAsync()
    {
        await SendCommandAsync("-exec-run");
    }

    public async Task<string> GetStackTraceAsync()
    {
        return await SendCommandAsync("-stack-list-frames");
    }

    public async Task<DapVariable[]> GetLocalsAsync()
    {
        var localsResponse = await SendCommandAsync("-stack-list-locals 1");
        var locals = MiResponseParser.ParseLocals(localsResponse);
        var variables = new List<DapVariable>(locals.Length);

        foreach (var local in locals)
        {
            var typedVariable = await CreateTypedVariableAsync(local.Name, local.Value);
            variables.Add(typedVariable ?? local);
        }

        return [.. variables];
    }

    public async Task TerminateAsync()
    {
        if (_lldb is not null && !_lldb.HasExited)
        {
            try
            {
                await SendCommandAsync("-gdb-exit");
                await _lldb.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                try { _lldb.Kill(); }
                catch (InvalidOperationException) { /* process already exited */ }
                catch (SystemException) { /* process no longer accessible */ }
            }
        }
    }

    private async Task<string> SendCommandAsync(string command)
    {
        if (_lldbIn is null)
        {
            return "";
        }

        var token = Interlocked.Increment(ref _tokenCounter);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[token] = tcs;

        await _lldbIn.WriteLineAsync($"{token}{command}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            cts.Token.Register(() => tcs.TrySetResult(""));
            var result = await tcs.Task;
            if (result.Contains("^error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ExtractMiField(result, "msg") ?? result);
            }

            return result;
        }
        catch
        {
            return "";
        }
        finally
        {
            _pendingCommands.TryRemove(token, out _);
        }
    }

    private async Task ReadOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                ProcessMiLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // LLDB process was disposed
        }
    }

    private static async Task DrainStreamAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            while (await reader.ReadAsync(buffer, 0, buffer.Length) > 0) { }
        }
        catch (ObjectDisposedException)
        {
            // LLDB process was disposed
        }
    }

    private void ProcessMiLine(string line)
    {
        // Result records: <token>^done,...  or <token>^error,...  or <token>^running
        if (TryCompleteResultRecord(line))
        {
            return;
        }

        if (line.StartsWith("*stopped", StringComparison.Ordinal))
        {
            var reason = ExtractMiField(line, "reason");
            if (reason == "exited-normally" || reason == "exited")
            {
                var exitCodeStr = ExtractMiField(line, "exit-code");
                var exitCode = exitCodeStr is not null
                    ? int.Parse(exitCodeStr, CultureInfo.InvariantCulture)
                    : 0;
                OnExited?.Invoke(exitCode);
            }
            else
            {
                OnStopped?.Invoke(reason ?? "unknown");
            }
        }
        else if (line.StartsWith("~", StringComparison.Ordinal))
        {
            // Console output
            var content = line.Length > 2 ? line[2..^1] : "";
            OnOutput?.Invoke(content);
        }
    }

    private bool TryCompleteResultRecord(string line)
    {
        // MI result records look like: <token>^done,... or <token>^error,...
        var match = ResultRecordRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var token = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (_pendingCommands.TryRemove(token, out var tcs))
        {
            tcs.TrySetResult(line);
        }

        return true;
    }

    [GeneratedRegex(@"^(\d+)\^")]
    private static partial Regex ResultRecordRegex();

    private static string? ExtractMiField(string miRecord, string fieldName)
    {
        var pattern = $"{fieldName}=\"([^\"]*)\"";
        var match = Regex.Match(miRecord, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string EscapeArg(string arg)
    {
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string BuildBreakpointInsertCommand(string filePath, int line)
    {
        return $"-break-insert --source {EscapeArg(filePath)} --line {line.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task<DapVariable?> CreateTypedVariableAsync(string localName, string fallbackValue)
    {
        var varCreateResponse = await SendCommandAsync($"-var-create - * {localName}");
        var variableObject = MiResponseParser.ParseVariableObject(varCreateResponse);
        if (variableObject is null)
        {
            return null;
        }

        try
        {
            var formattedValue = await AshesValueFormatter.FormatAsync(
                variableObject.Value,
                variableObject.Type,
                EvaluateExpressionAsync);

            return new DapVariable
            {
                Name = localName,
                Value = string.IsNullOrWhiteSpace(formattedValue) ? fallbackValue : formattedValue,
                Type = variableObject.Type,
                VariablesReference = 0,
            };
        }
        finally
        {
            _ = await SendCommandAsync($"-var-delete {variableObject.Name}");
        }
    }

    private async Task<string?> EvaluateExpressionAsync(string expression)
    {
        var response = await SendCommandAsync($"-data-evaluate-expression {EscapeArg(expression)}");
        return MiResponseParser.ParseEvaluateExpressionValue(response);
    }

    public void Dispose()
    {
        foreach (var (_, tcs) in _pendingCommands)
        {
            tcs.TrySetResult("");
        }

        _pendingCommands.Clear();
        _lldb?.Dispose();
    }
}
