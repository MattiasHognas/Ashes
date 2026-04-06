using System.ComponentModel;
using System.Diagnostics;

namespace Ashes.Tests;

/// <summary>
/// Shared helpers for test-time process execution.
/// </summary>
internal static class TestProcessHelper
{
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
}
