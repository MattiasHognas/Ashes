namespace Ashes.Dap;

/// <summary>
/// Abstraction over a native debugger backend (GDB, LLDB, etc.).
/// Each backend manages a subprocess that controls the debuggee via
/// ptrace or similar OS facilities.
/// </summary>
public interface IDebuggerBackend : IDisposable
{
    /// <summary>Raised when the debuggee stops, carrying the stop reason (e.g. <c>"breakpoint-hit"</c>, <c>"end-stepping-range"</c>).</summary>
    event Action<string>? OnStopped;

    /// <summary>Raised when the debuggee process exits, carrying its exit code.</summary>
    event Action<int>? OnExited;

    /// <summary>Raised when the debuggee produces console output, carrying the text.</summary>
    event Action<string>? OnOutput;

    /// <summary>Starts the debugger and loads the debuggee, honouring <paramref name="stopOnEntry"/>. When
    /// <paramref name="debuggerPath"/> is null the backend's default binary is used.</summary>
    /// <param name="program">Path to the compiled Ashes executable to debug.</param>
    /// <param name="cwd">Working directory for the debuggee, or null for the launcher's.</param>
    /// <param name="args">Command-line arguments passed to the debuggee.</param>
    /// <param name="debuggerPath">Explicit debugger binary path, or null for the backend default.</param>
    /// <param name="stopOnEntry">Whether to halt at the program entry point before running user code.</param>
    Task StartAsync(string program, string? cwd, string[]? args, string? debuggerPath, bool stopOnEntry);

    /// <summary>Sets a breakpoint at <paramref name="line"/> in <paramref name="filePath"/>, accumulating
    /// with any breakpoints already set for the same file.</summary>
    /// <param name="filePath">Source file to set the breakpoint in.</param>
    /// <param name="line">One-based line number for the breakpoint.</param>
    Task SetBreakpointAsync(string filePath, int line);

    /// <summary>Resumes execution of the debuggee until the next stop.</summary>
    Task ContinueAsync();

    /// <summary>Steps over the current source line, not descending into calls.</summary>
    Task StepOverAsync();

    /// <summary>Steps into the call at the current source line.</summary>
    Task StepInAsync();

    /// <summary>Steps out of the current function to its caller.</summary>
    Task StepOutAsync();

    /// <summary>Begins executing the debuggee after configuration is complete, honouring the launch's stop-on-entry setting.</summary>
    Task RunAsync();

    /// <summary>Retrieves the current call stack of the stopped debuggee.</summary>
    Task<DapStackFrame[]> GetStackTraceAsync();

    /// <summary>Retrieves the local variables of the current frame, with Ashes-aware value formatting.</summary>
    Task<DapVariable[]> GetLocalsAsync();

    /// <summary>Terminates the debuggee and shuts down the debugger subprocess.</summary>
    Task TerminateAsync();
}
