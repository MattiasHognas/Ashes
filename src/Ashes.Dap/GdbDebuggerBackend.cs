using System.Diagnostics;

namespace Ashes.Dap;

/// <summary>
/// Debugger backend that drives GDB through its built-in Debug Adapter
/// Protocol interpreter (<c>gdb --interpreter=dap</c>, available since
/// GDB 14 on Python-enabled builds).
/// </summary>
public sealed class GdbDebuggerBackend : DapClientDebuggerBackend
{
    private const string DefaultAdapterBinary = "gdb";

    protected override string AdapterDisplayName => "gdb";

    protected override string InstallHint =>
        "Install GDB 14 or newer (its DAP interpreter requires a Python-enabled build) or set debuggerPath to the gdb binary.";

    protected override ProcessStartInfo CreateAdapterStartInfo(string? debuggerPath)
    {
        return CreateStartInfo(debuggerPath);
    }

    internal static ProcessStartInfo CreateStartInfo(string? debuggerPath)
    {
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(debuggerPath) ? DefaultAdapterBinary : debuggerPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--interpreter=dap");
        psi.ArgumentList.Add("--quiet");
        return psi;
    }

    protected override object CreateLaunchArguments(string program, string? cwd, string[]? args, bool stopOnEntry)
    {
        return new
        {
            program,
            args = args ?? [],
            cwd,
            stopOnEntry,
        };
    }

    /// <summary>
    /// GDB reports function arguments in a dedicated "Arguments" scope next to
    /// "Locals"; both belong in the Variables pane.
    /// </summary>
    protected override bool IsVariablesScope(string? scopeName)
    {
        return base.IsVariablesScope(scopeName)
            || string.Equals(scopeName, "Arguments", StringComparison.OrdinalIgnoreCase);
    }
}
