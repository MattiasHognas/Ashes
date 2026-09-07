using System.Diagnostics;

namespace Ashes.TestRunner;

/// <summary>
/// Keeps one Wine server alive across the Windows programs a test run executes on Linux. Without
/// it Wine boots a fresh server, with its services and device processes, for every program and
/// tears it down as soon as the program exits, which costs seconds per test. The server is
/// started with an idle timeout rather than killed afterwards, so a run never interrupts another
/// Wine user of the same prefix; it exits by itself once the last program has been gone for the
/// timeout.
/// </summary>
public static class WinePersistentServer
{
    private const int IdleTimeoutSeconds = 30;
    private static readonly Lock SyncRoot = new();
    private static bool _started;

    /// <summary>
    /// Starts the persistent server when <paramref name="targetId"/> is a Windows target executed
    /// through Wine on this host; a no-op otherwise, and after the first call.
    /// </summary>
    public static void Ensure(string? targetId)
    {
        if (!string.Equals(targetId, Ashes.Backend.Backends.TargetIds.WindowsX64, StringComparison.Ordinal)
            || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            Start();
        }
    }

    // The wineserver executable on the search path, or null when Wine is not installed.
    private static string? ResolveWineServerPath()
    {
        string? searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (string directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, "wineserver");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static void Start()
    {
        string? serverPath = ResolveWineServerPath();
        if (serverPath is null)
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(serverPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add($"-p{IdleTimeoutSeconds}");
            psi.Environment["WINEDEBUG"] = "-all";
            using Process? server = Process.Start(psi);
            // The server daemonizes and returns at once; a server already running for the prefix
            // makes it exit with a message, which leaves that server in charge.
            server?.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No wineserver on the path: every program boots its own, as before.
        }
    }
}
