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
        new LeakMeasurement(5, 5, null).Leaks.ShouldBeFalse();
        new LeakMeasurement(5, 9, null).Leaks.ShouldBeTrue();
        LeakMeasurement.Skipped("compile exit 1").Leaks.ShouldBeFalse();
        new LeakCensusResult("steady", new LeakMeasurement(5, 5, null)).Leaks.ShouldBeFalse();
        new LeakCensusResult("stage 1 only", new LeakMeasurement(5, 5, null), new LeakMeasurement(5, 8, null)).Leaks.ShouldBeTrue();
        new LeakCensusResult("stage 1 skipped", new LeakMeasurement(5, 5, null), LeakMeasurement.Skipped("program exit 139")).Failed.ShouldBeTrue();
    }

    [Test]
    public void LeakCensusCommandParsesWithTheMemoryProfileByDefault()
    {
        FuzzConfiguration configuration = FuzzConfiguration.Parse(["leak-census", "--templates", "somewhere"]);
        configuration.Command.ShouldBe(FuzzCommandKind.LeakCensus);
        configuration.Profile.ShouldBe("memory-growth");
        configuration.TemplateDirectory.ShouldBe("somewhere");
        configuration.Stage1Compiler.ShouldBeNull();
    }

    [Test]
    public void LeakCensusCommandTakesASelfHostedCompiler()
    {
        FuzzConfiguration configuration = FuzzConfiguration.Parse(["leak-census", "--stage1", "artifacts/selfhost-dev/s1-tip"]);
        configuration.Stage1Compiler.ShouldBe("artifacts/selfhost-dev/s1-tip");
        Should.Throw<ArgumentException>(() => FuzzConfiguration.Parse(["leak-census", "--stage1"]));
    }

    [Test]
    public void AResultWithOneCompilerIsReportedAsBefore()
    {
        LeakCensusResult leaking = new("leak.case@1:Int", new LeakMeasurement(4, 7, null));
        LeakCensus.Describe(leaking).ShouldBe("LEAK  leak.case@1:Int: 3 cell(s) per round (live 4 -> 7)");
        LeakCensus.ReportLine(leaking).ShouldBe("leak.case@1:Int\t4\t7\tleak\t");
        LeakCensus.Describe(new LeakCensusResult("ok.case", new LeakMeasurement(2, 2, null))).ShouldBe("ok    ok.case");
        LeakCensus.Describe(new LeakCensusResult("skip.case", LeakMeasurement.Skipped("compile exit 1: ASH014"))).ShouldBe("SKIP  skip.case: compile exit 1: ASH014");
    }

    [Test]
    public void AResultWithBothCompilersHasOneCellPerStage()
    {
        LeakCensusResult result = new("leak.case@1:Int", new LeakMeasurement(2, 2, null), new LeakMeasurement(2, 5, null));
        LeakCensus.Describe(result).ShouldBe("LEAK  leak.case@1:Int: stage0 ok; stage1 LEAK 3");
        LeakCensus.ReportLine(result).ShouldBe("leak.case@1:Int\t2\t2\tok\t\t2\t5\tleak\t");
        LeakCensusResult skipped = new("skip.case", new LeakMeasurement(1, 1, null), LeakMeasurement.Skipped("program exit 139"));
        LeakCensus.Describe(skipped).ShouldBe("SKIP  skip.case: stage0 ok; stage1 SKIP program exit 139");
        LeakCensus.Describe(new LeakCensusResult("ok.case", new LeakMeasurement(1, 1, null), new LeakMeasurement(3, 3, null))).ShouldBe("ok    ok.case: stage0 ok; stage1 ok");
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
