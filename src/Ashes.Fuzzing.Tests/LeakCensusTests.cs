using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Execution;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class LeakCensusTests
{
    [Test]
    public void LiveCellCountIsReadFromTheLastReportLine()
    {
        LeakCensus.ParseLiveCells("some diagnostic\nashes-rc-live 12\n").ShouldBe(12);
        LeakCensus.ParseLiveCells("ashes-rc-live 3\nashes-rc-live 7").ShouldBe(7);
        LeakCensus.ParseLiveCells("nothing reported").ShouldBeNull();
    }

    [Test]
    public void OnlyAGrowingCountBetweenOneRoundAndTwoIsALeak()
    {
        new LeakCensusResult("steady", 5, 5, null).Leaks.ShouldBeFalse();
        new LeakCensusResult("growing", 5, 9, null).Leaks.ShouldBeTrue();
        new LeakCensusResult("uncounted", null, null, "compile exit 1").Leaks.ShouldBeFalse();
    }

    [Test]
    public void LeakCensusCommandParsesWithTheMemoryProfileByDefault()
    {
        FuzzConfiguration configuration = FuzzConfiguration.Parse(["leak-census", "--templates", "somewhere"]);
        configuration.Command.ShouldBe(FuzzCommandKind.LeakCensus);
        configuration.Profile.ShouldBe("memory-growth");
        configuration.TemplateDirectory.ShouldBe("somewhere");
    }

    [Test]
    public async Task ACompiledLoopThatDropsItsWorkReportsNoGrowth()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string repositoryRoot = Environment.CurrentDirectory;
        while (!File.Exists(Path.Combine(repositoryRoot, "Ashes.slnx")))
        {
            repositoryRoot = Directory.GetParent(repositoryRoot)!.FullName;
        }

        long?[] live = new long?[2];
        for (int rounds = 1; rounds <= 2; rounds++)
        {
            string source = $"let recursive loop (n: Int) (total: Int) =\n    if n == 0\n    then total\n    else loop(n - 1)(total + Ashes.Text.byteLength(\"a\" + Ashes.Text.fromInt(n)))\n\nAshes.IO.print(loop({rounds})(0))\n";
            (ProcessResult _, ProcessResult? run) = await new CompilerExecution().CompileAndRunAsync(
                source, repositoryRoot, "host", "-O2", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), 4096, CancellationToken.None, leakCheck: true);
            run.ShouldNotBeNull();
            live[rounds - 1] = LeakCensus.ParseLiveCells(run.StandardError);
        }

        live[0].ShouldNotBeNull();
        live[1].ShouldBe(live[0]);
    }
}
