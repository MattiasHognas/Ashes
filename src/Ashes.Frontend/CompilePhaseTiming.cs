using System.Diagnostics;

namespace Ashes.Frontend;

/// <summary>
/// Reports how long each compile phase takes, on stderr, when the <c>ASHES_TIMING</c> environment
/// variable is set. Each report is one line of the form <c>timing: &lt;phase&gt; &lt;milliseconds&gt; ms</c>.
/// </summary>
public static class CompilePhaseTiming
{
    /// <summary>Whether phase timings are reported for this process.</summary>
    public static bool Enabled { get; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASHES_TIMING"));

    /// <summary>Runs <paramref name="phase"/> and reports its duration under <paramref name="name"/>.</summary>
    public static T Measure<T>(string name, Func<T> phase)
    {
        if (!Enabled)
        {
            return phase();
        }

        long start = Stopwatch.GetTimestamp();
        T result = phase();
        Report(name, Stopwatch.GetElapsedTime(start));
        return result;
    }

    /// <summary>Runs <paramref name="phase"/> and reports its duration under <paramref name="name"/>.</summary>
    public static void Measure(string name, Action phase)
    {
        if (!Enabled)
        {
            phase();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        phase();
        Report(name, Stopwatch.GetElapsedTime(start));
    }

    private static bool CollectBeforeReport { get; } =
        string.Equals(Environment.GetEnvironmentVariable("ASHES_TIMING"), "live", StringComparison.Ordinal);

    private static void Report(string name, TimeSpan elapsed)
    {
        GCMemoryInfo heap = GC.GetGCMemoryInfo();
        // With ASHES_TIMING=live each report first runs a full collection, so the live size is what
        // the phase leaves reachable; otherwise it is the heap as the last collection left it.
#pragma warning disable S1215
        long liveAfterCollection = GC.GetTotalMemory(forceFullCollection: CollectBeforeReport);
#pragma warning restore S1215
        using Process process = Process.GetCurrentProcess();
        const double MiB = 1024.0 * 1024.0;
        Console.Error.WriteLine(
            $"timing: {name} {elapsed.TotalMilliseconds:F0} ms"
            + $" live={liveAfterCollection / MiB:F0}MiB"
            + $" heap={heap.HeapSizeBytes / MiB:F0}MiB"
            + $" committed={heap.TotalCommittedBytes / MiB:F0}MiB"
            + $" rss={process.WorkingSet64 / MiB:F0}MiB"
            + $" peakRss={PeakResidentKiB() / 1024.0:F0}MiB");
    }

    // The process's high-water resident set (VmHWM, in KiB) from /proc/self/status, or -1 off Linux.
    private static long PeakResidentKiB()
    {
        const string StatusPath = "/proc/self/status";
        if (!File.Exists(StatusPath))
        {
            return -1;
        }

        foreach (string line in File.ReadLines(StatusPath))
        {
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }
}
