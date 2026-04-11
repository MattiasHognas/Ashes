using System.Collections.Concurrent;
using System.ComponentModel;
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
    private string _launchError = string.Empty;

    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public async Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath)
    {
        Exception? lastError = null;
        foreach (var startInfo in CreateLaunchCandidates(program, cwd, debuggerPath))
        {
            try
            {
                var lldb = Process.Start(startInfo);
                if (lldb is not null)
                {
                    if (await ExitedDuringStartupAsync(lldb))
                    {
                        lastError = await CreateStartupFailureAsync(startInfo, lldb);
                        lldb.Dispose();
                        continue;
                    }

                    _lldb = lldb;
                    _lldbIn = _lldb.StandardInput;
                    _lldbIn.AutoFlush = true;

                    // Start reading LLDB output and draining stderr to prevent pipe buffer deadlocks.
                    _ = Task.Run(() => ReadOutputAsync(_lldb.StandardOutput));
                    _ = Task.Run(() => DrainStreamAsync(_lldb.StandardError));

                    if (args is not null && args.Length > 0)
                    {
                        var escapedArgs = string.Join(" ", args.Select(EscapeArg));
                        await SendCommandAsync($"-exec-arguments {escapedArgs}");
                    }

                    return;
                }

                lastError = new InvalidOperationException($"Failed to start LLDB using '{startInfo.FileName}'.");
            }
            catch (Win32Exception ex)
            {
                lastError = ex;
            }
        }

        if (_lldb is null)
        {
            throw CreateStartFailure(debuggerPath, lastError);
        }
    }

    internal static IReadOnlyList<ProcessStartInfo> CreateLaunchCandidates(string program, string? cwd, string? debuggerPath)
    {
        if (!string.IsNullOrWhiteSpace(debuggerPath))
        {
            return [CreateProcessStartInfo(debuggerPath, program, cwd, useInterpreterMi2: ShouldUseInterpreterMi2(debuggerPath))];
        }

        return
        [
            CreateProcessStartInfo("lldb-mi", program, cwd, useInterpreterMi2: false),
            CreateProcessStartInfo("lldb", program, cwd, useInterpreterMi2: true),
        ];
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
            throw new InvalidOperationException("LLDB is not running.");
        }

        var token = Interlocked.Increment(ref _tokenCounter);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[token] = tcs;

        try
        {
            await _lldbIn.WriteLineAsync($"{token}{command}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw CreateCommandFailure(command, ex);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException($"Timed out waiting for LLDB response to '{command}'.")));

        try
        {
            var result = await tcs.Task;
            if (result.Contains("^error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ExtractMiField(result, "msg") ?? result);
            }

            return result;
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

    private static async Task<bool> ExitedDuringStartupAsync(Process process)
    {
        await Task.Delay(200);
        return process.HasExited;
    }

    private async Task<InvalidOperationException> CreateStartupFailureAsync(ProcessStartInfo startInfo, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }

        var stderr = await SafeReadToEndAsync(process.StandardError);
        var stdout = await SafeReadToEndAsync(process.StandardOutput);
        _launchError = FirstNonEmpty(stderr, stdout);

        var message = $"LLDB exited immediately when started as '{BuildCommandDisplay(startInfo)}'.";
        if (!string.IsNullOrWhiteSpace(_launchError))
        {
            message += $" {NormalizeDiagnostic(_launchError)}";
        }

        return new InvalidOperationException(message);
    }

    private static ProcessStartInfo CreateProcessStartInfo(string debuggerPath, string program, string? cwd, bool useInterpreterMi2)
    {
        var psi = new ProcessStartInfo(debuggerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (useInterpreterMi2)
        {
            psi.ArgumentList.Add("--interpreter=mi2");
        }

        psi.ArgumentList.Add(program);

        if (cwd is not null)
        {
            psi.WorkingDirectory = cwd;
        }

        return psi;
    }

    private static InvalidOperationException CreateStartFailure(string? debuggerPath, Exception? lastError)
    {
        if (!string.IsNullOrWhiteSpace(debuggerPath))
        {
            var message = $"Failed to start LLDB using '{debuggerPath}'.";
            if (lastError is not null)
            {
                message += $" {lastError.Message}";
            }

            return new InvalidOperationException(message, lastError);
        }

        return new InvalidOperationException(
            "Failed to start LLDB. Tried 'lldb-mi' and 'lldb --interpreter=mi2'. Install 'lldb-mi' or set debuggerPath to an MI-compatible LLDB frontend.",
            lastError);
    }

    private static bool ShouldUseInterpreterMi2(string debuggerPath)
    {
        var fileName = Path.GetFileName(debuggerPath);
        return !fileName.StartsWith("lldb-mi", StringComparison.OrdinalIgnoreCase);
    }

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

    private InvalidOperationException CreateCommandFailure(string command, Exception innerException)
    {
        if (_lldb is not null && _lldb.HasExited)
        {
            var message = $"LLDB exited before handling '{command}' (exit code {_lldb.ExitCode.ToString(CultureInfo.InvariantCulture)}).";
            if (!string.IsNullOrWhiteSpace(_launchError))
            {
                message += $" {NormalizeDiagnostic(_launchError)}";
            }

            return new InvalidOperationException(message, innerException);
        }

        return new InvalidOperationException($"Failed to send command to LLDB: {command}", innerException);
    }

    private static async Task<string> SafeReadToEndAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync();
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static string BuildCommandDisplay(ProcessStartInfo startInfo)
    {
        if (startInfo.ArgumentList.Count == 0)
        {
            return startInfo.FileName;
        }

        return string.Join(" ", new[] { startInfo.FileName }.Concat(startInfo.ArgumentList));
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
