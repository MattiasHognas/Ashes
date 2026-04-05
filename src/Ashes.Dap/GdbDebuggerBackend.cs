using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ashes.Dap;

/// <summary>
/// Abstracts the debugger backend (GDB MI protocol).
/// Manages a GDB subprocess to debug the target Ashes-compiled binary.
/// </summary>
public sealed class GdbDebuggerBackend : IDisposable
{
    private Process? _gdb;
    private StreamWriter? _gdbIn;
    private int _tokenCounter;

    public event Action<string>? OnStopped;
    public event Action<int>? OnExited;
    public event Action<string>? OnOutput;

    public async Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath)
    {
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

        // Start reading GDB output
        _ = Task.Run(() => ReadGdbOutputAsync(_gdb.StandardOutput));

        // Wait for initial GDB prompt
        await Task.Delay(200);

        // Set program arguments if provided
        if (args is not null && args.Length > 0)
        {
            var escapedArgs = string.Join(" ", args.Select(EscapeGdbArg));
            await SendCommandAsync($"-exec-arguments {escapedArgs}");
        }
    }

    public async Task SetBreakpointAsync(string filePath, int line)
    {
        await SendCommandAsync($"-break-insert {EscapeGdbArg(filePath)}:{line}");
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

    public async Task<string> GetLocalsAsync()
    {
        return await SendCommandAsync("-stack-list-locals 1");
    }

    public async Task TerminateAsync()
    {
        if (_gdb is not null && !_gdb.HasExited)
        {
            try
            {
                await SendCommandAsync("-gdb-exit");
                await _gdb.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
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
        await _gdbIn.WriteLineAsync($"{token}{command}");
        // In a full implementation we'd wait for the token-matched response.
        // For now, return empty and rely on the output reader for events.
        return "";
    }

    private async Task ReadGdbOutputAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                ProcessGdbLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // GDB process was disposed
        }
    }

    private void ProcessGdbLine(string line)
    {
        if (line.StartsWith("*stopped", StringComparison.Ordinal))
        {
            var reason = ExtractGdbField(line, "reason");
            if (reason == "exited-normally" || reason == "exited")
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

    public void Dispose()
    {
        _gdb?.Dispose();
    }
}
