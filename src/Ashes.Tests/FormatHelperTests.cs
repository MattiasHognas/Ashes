using Ashes.TestRunner;
using Shouldly;

namespace Ashes.Tests;

public sealed class FormatHelperTests
{
    [Test]
    public void FormatElapsed_zero_milliseconds() =>
        Runner.FormatElapsed(0).ShouldBe("0ms");

    [Test]
    public void FormatElapsed_sub_second() =>
        Runner.FormatElapsed(500).ShouldBe("500ms");

    [Test]
    public void FormatElapsed_999ms_stays_in_milliseconds() =>
        Runner.FormatElapsed(999).ShouldBe("999ms");

    [Test]
    public void FormatElapsed_exactly_1000ms_switches_to_seconds() =>
        Runner.FormatElapsed(1000).ShouldBe("1.00s");

    [Test]
    public void FormatElapsed_fractional_seconds() =>
        Runner.FormatElapsed(1500).ShouldBe("1.50s");

    [Test]
    public void FormatElapsed_under_60_seconds_stays_in_seconds() =>
        Runner.FormatElapsed(59_000).ShouldBe("59.00s");

    [Test]
    public void FormatElapsed_exactly_60_seconds_switches_to_minutes() =>
        Runner.FormatElapsed(60_000).ShouldBe("1.00min");

    [Test]
    public void FormatElapsed_fractional_minutes() =>
        Runner.FormatElapsed(90_000).ShouldBe("1.50min");

    [Test]
    public void FormatSize_zero_bytes() =>
        Runner.FormatSize(0).ShouldBe("0 B");

    [Test]
    public void FormatSize_below_1KB() =>
        Runner.FormatSize(512).ShouldBe("512 B");

    [Test]
    public void FormatSize_1023_bytes_stays_in_bytes() =>
        Runner.FormatSize(1023).ShouldBe("1023 B");

    [Test]
    public void FormatSize_exactly_1024_bytes_switches_to_KB() =>
        Runner.FormatSize(1024).ShouldBe("1.0 KB");

    [Test]
    public void FormatSize_fractional_KB() =>
        Runner.FormatSize(1536).ShouldBe("1.5 KB");

    [Test]
    public void FormatSize_1048575_bytes_stays_in_KB() =>
        Runner.FormatSize(1_048_575).ShouldBe("1024.0 KB");

    [Test]
    public void FormatSize_exactly_1048576_bytes_switches_to_MB() =>
        Runner.FormatSize(1_048_576).ShouldBe("1.0 MB");

    [Test]
    public void FormatSize_fractional_MB() =>
        Runner.FormatSize(1_572_864).ShouldBe("1.5 MB");
}
