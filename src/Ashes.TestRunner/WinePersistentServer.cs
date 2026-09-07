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
    private const int IdleTimeoutSeconds = 15;
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

    // The named Wine executable on the search path, or null when Wine is not installed.
    private static string? ResolveWinePath(string executable)
    {
        string? searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (string directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    // Starts the server, then boots the prefix through a trivial Wine command run with this
    // process's own standard handles. Wine's service processes are forked by the first client
    // and inherit its handles for the server's lifetime; booted here they hold nothing a test
    // waits on, whereas booted by a test program they would keep that program's output pipe open
    // and block the runner's read of it until the server exits.
    private static void Start()
    {
        string? serverPath = ResolveWinePath("wineserver");
        string? winePath = ResolveWinePath("wine");
        if (serverPath is null || winePath is null)
        {
            return;
        }

        try
        {
            // A server another program left running for a few seconds makes the start exit
            // with a status of 2; it is gone shortly, so retry until the persistent one is ours.
            for (int attempt = 0; attempt < StartAttempts; attempt++)
            {
                if (RunWineCommand(serverPath, [$"-p{IdleTimeoutSeconds}"]) == 0)
                {
                    RunWineCommand(winePath, ["cmd", "/c", "exit"]);
                    return;
                }

                Thread.Sleep(StartRetryDelayMilliseconds);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No usable Wine: every program boots its own server, as before.
        }
    }

    private const int StartAttempts = 20;
    private const int StartRetryDelayMilliseconds = 250;

    // Runs a Wine executable to completion without redirecting its output, so nothing it forks
    // inherits a pipe, and returns its exit status.
    private static int RunWineCommand(string path, string[] arguments)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["WINEDEBUG"] = "-all";
        psi.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=d";
        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return -1;
        }

        return process.WaitForExit(30000) ? process.ExitCode : -1;
    }
}
