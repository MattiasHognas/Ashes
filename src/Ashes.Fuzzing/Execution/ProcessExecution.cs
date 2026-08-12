using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Ashes.Fuzzing.Execution;

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated,
    TimeSpan Duration,
    long? MaximumResidentSetKilobytes = null);

internal static class ProcessTimeout
{
    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumOutputBytes,
        CancellationToken cancellationToken,
        bool capturePeakWorkingSet = false)
    {
        if (timeout <= TimeSpan.Zero || maximumOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(timeout <= TimeSpan.Zero ? nameof(timeout) : nameof(maximumOutputBytes));
        }

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
        Task<BoundedOutput> stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes);
        Task<BoundedOutput> stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, maximumOutputBytes);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        BoundedOutput stdout = await stdoutTask.ConfigureAwait(false);
        BoundedOutput stderr = await stderrTask.ConfigureAwait(false);
        if (cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        long? maximumResidentSetKilobytes = capturePeakWorkingSet
            ? process.PeakWorkingSet64 / 1024
            : null;
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            stdout.Text,
            stderr.Text,
            timedOut,
            stdout.Truncated || stderr.Truncated,
            stopwatch.Elapsed,
            maximumResidentSetKilobytes);
    }

    internal static async Task<ProcessResult> RunWithNativePeakRssAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return await RunAsync(
                fileName,
                arguments,
                workingDirectory,
                timeout,
                maximumOutputBytes,
                cancellationToken,
                capturePeakWorkingSet: true).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Peak RSS measurement is supported on native Linux and Windows hosts.");
        }

        const string timePath = "/usr/bin/time";
        const string marker = "__ASHES_FUZZ_MAX_RSS_KB__=";
        List<string> measuredArguments = ["-f", marker + "%M", fileName, .. arguments];
        ProcessResult measured = await RunAsync(
            timePath,
            measuredArguments,
            workingDirectory,
            timeout,
            maximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        string[] stderrLines = measured.StandardError.Split('\n');
        string? measurement = stderrLines.LastOrDefault(line =>
            line.StartsWith(marker, StringComparison.Ordinal));
        if (measurement is null ||
            !long.TryParse(measurement[marker.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long peakRssKb))
        {
            return measured;
        }

        string stderr = string.Join('\n', stderrLines.Where(line =>
            !line.StartsWith(marker, StringComparison.Ordinal)));
        return measured with
        {
            StandardError = stderr,
            MaximumResidentSetKilobytes = peakRssKb,
        };
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream retained = new(Math.Min(maximumBytes, buffer.Length));
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
        {
            int keep = Math.Min(read, maximumBytes - checked((int)retained.Length));
            if (keep > 0)
            {
                retained.Write(buffer, 0, keep);
            }
            truncated |= keep != read;
        }

        int retainedLength = checked((int)retained.Length);
        string text = Encoding.UTF8.GetString(retained.GetBuffer(), 0, retainedLength);
        while (retainedLength > 0 && Encoding.UTF8.GetByteCount(text) > maximumBytes)
        {
            retainedLength--;
            text = Encoding.UTF8.GetString(retained.GetBuffer(), 0, retainedLength);
        }
        return new BoundedOutput(text, truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed record BoundedOutput(string Text, bool Truncated);
}
