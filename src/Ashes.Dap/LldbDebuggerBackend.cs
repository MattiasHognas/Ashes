using System.Diagnostics;

namespace Ashes.Dap;

/// <summary>
/// Debugger backend that drives LLDB through <c>lldb-dap</c>, the Debug
/// Adapter Protocol binary that ships with LLDB.
/// </summary>
public sealed class LldbDebuggerBackend : DapClientDebuggerBackend
{
    private const string DefaultAdapterBinary = "lldb-dap";

    /// <inheritdoc/>
    protected override string AdapterDisplayName => "lldb-dap";

    /// <inheritdoc/>
    protected override string InstallHint =>
        "Install LLDB (which provides lldb-dap) or set debuggerPath to the lldb-dap binary.";

    /// <inheritdoc/>
    protected override ProcessStartInfo CreateAdapterStartInfo(string? debuggerPath)
    {
        return CreateStartInfo(debuggerPath);
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

    /// <inheritdoc/>
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
}
