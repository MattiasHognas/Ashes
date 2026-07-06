using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ashes.Dap;

/// <summary>
/// Abstracts the debugger backend (GDB MI protocol).
/// Manages a GDB subprocess to debug the target Ashes-compiled binary.
/// </summary>
public sealed partial class GdbDebuggerBackend : IDebuggerBackend
{
    /// <summary>Entry function emitted by the Ashes backend in every binary.</summary>
    private const string EntryFunctionName = "_start_main";

    private Process? _gdb;
    private StreamWriter? _gdbIn;
    private int _tokenCounter;
    private bool _stopOnEntry;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();

    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public async Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath, bool stopOnEntry)
    {
        _stopOnEntry = stopOnEntry;
        var gdbPath = debuggerPath ?? "gdb";
        var psi = new ProcessStartInfo(gdbPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--interpreter=mi2");
        psi.ArgumentList.Add("--quiet");
        psi.ArgumentList.Add(program);

        if (cwd is not null)
        {
            psi.WorkingDirectory = cwd;
        }

        _gdb = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start GDB.");
        _gdbIn = _gdb.StandardInput;
        _gdbIn.AutoFlush = true;

        // Start reading GDB output and draining stderr to prevent pipe buffer deadlocks
        _ = Task.Run(() => ReadGdbOutputAsync(_gdb.StandardOutput));
        _ = Task.Run(() => DrainStreamAsync(_gdb.StandardError));

        // Wait for initial GDB prompt
        await Task.Delay(200).ConfigureAwait(false);

        // Set program arguments if provided
        if (args is not null && args.Length > 0)
        {
            var escapedArgs = string.Join(" ", args.Select(EscapeGdbArg));
            await SendCommandAsync($"-exec-arguments {escapedArgs}").ConfigureAwait(false);
        }
    }

    public async Task SetBreakpointAsync(string filePath, int line)
    {
        await SendCommandAsync(BuildBreakpointInsertCommand(filePath, line)).ConfigureAwait(false);
    }

    public async Task ContinueAsync()
    {
        await SendCommandAsync("-exec-continue").ConfigureAwait(false);
    }

    public async Task StepOverAsync()
    {
        await SendCommandAsync("-exec-next").ConfigureAwait(false);
    }

    public async Task StepInAsync()
    {
        await SendCommandAsync("-exec-step").ConfigureAwait(false);
    }

    public async Task StepOutAsync()
    {
        await SendCommandAsync("-exec-finish").ConfigureAwait(false);
    }

    public async Task RunAsync()
    {
        if (_stopOnEntry)
        {
            await SendCommandAsync($"-break-insert -t {EntryFunctionName}").ConfigureAwait(false);
        }

        await SendCommandAsync("-exec-run").ConfigureAwait(false);
    }

    public async Task<DapStackFrame[]> GetStackTraceAsync()
    {
        var miResponse = await SendCommandAsync("-stack-list-frames").ConfigureAwait(false);
        return MiResponseParser.ParseStackFrames(miResponse);
    }

    public async Task<DapVariable[]> GetLocalsAsync()
    {
        // -stack-list-variables includes function arguments; -stack-list-locals would not.
        var localsResponse = await SendCommandAsync("-stack-list-variables 1").ConfigureAwait(false);
        var locals = MiResponseParser.ParseVariables(localsResponse);
        var variables = new List<DapVariable>(locals.Length);

        foreach (var local in locals)
        {
            var typedVariable = await CreateTypedVariableAsync(local.Name, local.Value).ConfigureAwait(false);
            variables.Add(typedVariable ?? local);
        }

        return [.. variables];
    }

    public async Task TerminateAsync()
    {
        if (_gdb is not null && !_gdb.HasExited)
        {
            try
            {
                await SendCommandAsync("-gdb-exit").ConfigureAwait(false);
                await _gdb.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                try { _gdb.Kill(); }
                catch (InvalidOperationException) { /* process already exited */ }
                catch (SystemException) { /* process no longer accessible */ }
            }
        }
    }

    private async Task<string> SendCommandAsync(string command)
    {
        if (_gdbIn is null)
        {
            return "";
        }

        var token = Interlocked.Increment(ref _tokenCounter);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[token] = tcs;

        await _gdbIn.WriteLineAsync($"{token}{command}").ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            cts.Token.Register(() => tcs.TrySetResult(""));
            var result = await tcs.Task.ConfigureAwait(false);
            if (result.Contains("^error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ExtractGdbField(result, "msg") ?? result);
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

    private async Task ReadGdbOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                ProcessGdbLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // GDB process was disposed
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
            // GDB process was disposed
        }
    }

    private void ProcessGdbLine(string line)
    {
        // Result records: <token>^done,...  or <token>^error,...  or <token>^running
        if (TryCompleteResultRecord(line))
        {
            return;
        }

        if (line.StartsWith("*stopped", StringComparison.Ordinal))
        {
            var reason = ExtractGdbField(line, "reason");
            if (string.Equals(reason, "exited-normally", StringComparison.Ordinal) || string.Equals(reason, "exited", StringComparison.Ordinal))
            {
                var exitCodeStr = ExtractGdbField(line, "exit-code");
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

    private static string? ExtractGdbField(string miRecord, string fieldName)
    {
        var pattern = $"{fieldName}=\"([^\"]*)\"";
        var match = Regex.Match(miRecord, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string EscapeGdbArg(string arg)
    {
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string BuildBreakpointInsertCommand(string filePath, int line)
    {
        return $"-break-insert --source {EscapeGdbArg(filePath)} --line {line.ToString(CultureInfo.InvariantCulture)}";
    }

    private async Task<DapVariable?> CreateTypedVariableAsync(string localName, string fallbackValue)
    {
        var varCreateResponse = await SendCommandAsync($"-var-create - * {localName}").ConfigureAwait(false);
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
                EvaluateExpressionAsync).ConfigureAwait(false);

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
            _ = await SendCommandAsync($"-var-delete {variableObject.Name}").ConfigureAwait(false);
        }
    }

    private async Task<string?> EvaluateExpressionAsync(string expression)
    {
        var response = await SendCommandAsync($"-data-evaluate-expression {EscapeGdbArg(expression)}").ConfigureAwait(false);
        return MiResponseParser.ParseEvaluateExpressionValue(response);
    }

    public void Dispose()
    {
        foreach (var (_, tcs) in _pendingCommands)
        {
            tcs.TrySetResult("");
        }

        _pendingCommands.Clear();
        _gdb?.Dispose();
    }
}
