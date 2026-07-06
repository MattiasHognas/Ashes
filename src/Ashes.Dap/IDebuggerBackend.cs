namespace Ashes.Dap;

/// <summary>
/// Abstraction over a native debugger backend (GDB, LLDB, etc.).
/// Each backend manages a subprocess that controls the debuggee via
/// ptrace or similar OS facilities.
/// </summary>
public interface IDebuggerBackend : IDisposable
{
    event Action<string>? OnStopped;
    event Action<int>? OnExited;
    event Action<string>? OnOutput;

    Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath, bool stopOnEntry);
    Task SetBreakpointAsync(string filePath, int line);
    Task ContinueAsync();
    Task StepOverAsync();
    Task StepInAsync();
    Task StepOutAsync();
    Task RunAsync();
    Task<DapStackFrame[]> GetStackTraceAsync();
    Task<DapVariable[]> GetLocalsAsync();
    Task TerminateAsync();
}
