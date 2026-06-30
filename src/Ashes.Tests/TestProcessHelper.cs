using System.ComponentModel;
using System.Diagnostics;

namespace Ashes.Tests;

/// <summary>
/// Shared helpers for test-time process execution.
/// </summary>
internal static class TestProcessHelper
{
    private static readonly string[] WineExecutableCandidates = ["wine64", "wine", "wine-stable", "/usr/lib/wine/wine64"];

    /// <summary>
    /// Starts a process, retrying on transient ETXTBSY ("Text file busy") errors.
    /// On Linux, a freshly-written executable can briefly fail to exec while the
    /// kernel page cache is still finishing writeback. This is a known race that
    /// is not reliably preventable with file operations alone, so we retry.
    /// </summary>
    internal static async Task<Process> StartProcessAsync(ProcessStartInfo psi)
    {
        // ETXTBSY is errno 26 on Linux.
        const int textFileBusyError = 26;
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return Process.Start(psi)!;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == textFileBusyError && attempt < maxAttempts - 1)
            {
                await Task.Delay(20 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("Failed to start process after retrying transient ETXTBSY errors.");
    }

    /// <summary>
    /// Writes an executable to disk and sets Unix execute permissions.
    /// Uses synchronous I/O with explicit flush-to-disk to minimise the
    /// window for ETXTBSY races on Linux.
    /// </summary>
    internal static void WriteExecutable(string path, byte[] bytes)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        }
    }

    internal static bool CanRunWindowsExecutables()
    {
        return TryResolveWindowsExecutionEnvironment(out _);
    }

    internal static ProcessStartInfo CreateWindowsProcessStartInfo(string exePath)
    {
        if (!TryResolveWindowsExecutionEnvironment(out var environment))
        {
            throw new InvalidOperationException("Windows executable execution requires either a native Windows host or Wine on Linux.");
        }

        if (environment.RunnerPath is null)
        {
            return new ProcessStartInfo(exePath);
        }

        var psi = new ProcessStartInfo(environment.RunnerPath);
        psi.ArgumentList.Add(exePath);
        psi.Environment["WINEDEBUG"] = "-all";
        // Ashes emits standalone native PE binaries — they never load the .NET (mscoree) or
        // Gecko (mshtml) runtimes — so suppress Wine's first-run installer dialogs for them.
        // Without this, a fresh Wine prefix blocks the test run on an interactive popup.
        psi.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=d";
        return psi;
    }

    internal static string ConvertHostPathForWindowsExecution(string path)
    {
        if (!TryResolveWindowsExecutionEnvironment(out var environment) || environment.RunnerPath is null)
        {
            return path;
        }

        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        return "Z:" + path.Replace('/', '\\');
    }

    private static bool TryResolveWindowsExecutionEnvironment(out WindowsExecutionEnvironment environment)
    {
        if (OperatingSystem.IsWindows())
        {
            environment = new WindowsExecutionEnvironment(null);
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            environment = default;
            return false;
        }

        var runnerPath = FindCommandOnPath(WineExecutableCandidates);
        if (runnerPath is null)
        {
            environment = default;
            return false;
        }

        environment = new WindowsExecutionEnvironment(runnerPath);
        return true;
    }

    private static string? FindCommandOnPath(IEnumerable<string> candidates)
    {
        return FindCommand(candidates, Environment.GetEnvironmentVariable("PATH"));
    }

    internal static string? FindCommand(IEnumerable<string> candidates, string? path)
    {
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                continue;
            }

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private readonly record struct WindowsExecutionEnvironment(string? RunnerPath);
}
