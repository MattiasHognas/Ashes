using System.Diagnostics;
using System.Text;

namespace Ashes.Fuzzing.Execution;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputTruncated, TimeSpan Duration);

internal static class ProcessTimeout
{
    internal static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, int maximumOutputBytes, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = new() { StartInfo = startInfo };
        Stopwatch stopwatch = Stopwatch.StartNew();
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        bool truncated = Encoding.UTF8.GetByteCount(stdout) > maximumOutputBytes || Encoding.UTF8.GetByteCount(stderr) > maximumOutputBytes;
        stdout = Truncate(stdout, maximumOutputBytes);
        stderr = Truncate(stderr, maximumOutputBytes);
        return new ProcessResult(timedOut ? -1 : process.ExitCode, stdout, stderr, timedOut, truncated, stopwatch.Elapsed);
    }

    private static string Truncate(string value, int maximumBytes)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length <= maximumBytes ? value : Encoding.UTF8.GetString(bytes, 0, maximumBytes);
    }
}
