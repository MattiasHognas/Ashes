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

    private static void Report(string name, TimeSpan elapsed)
    {
        Console.Error.WriteLine($"timing: {name} {elapsed.TotalMilliseconds:F0} ms");
    }
}
