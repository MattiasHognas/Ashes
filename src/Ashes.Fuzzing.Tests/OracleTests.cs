using Ashes.Frontend;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Execution;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Fuzzing.Tests;

public sealed class OracleTests
{
    [Test]
    public async Task FrontendAndSemanticOraclesAcceptGeneratedPrograms()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(123, 9, fixture.Profiles.Get("semantics"), 50);
        FuzzExecutionContext context = new(Environment.CurrentDirectory, "host", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 4096, new CompilerExecution());
        foreach (IFuzzOracle oracle in new IFuzzOracle[] { new ParseOracle(), new FormatOracle(), new SemanticOracle(), new IrVerificationOracle() })
        {
            FuzzOracleResult result = await oracle.EvaluateAsync(generated, context, CancellationToken.None);
            result.Success.ShouldBeTrue(result.Message);
        }
    }

    [Test]
    public async Task FormatOracleRejectsDriftFromTheOriginalFormattedAst()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(321, 4, fixture.Profiles.Get("syntax"), 50);
        GeneratedFuzzCase drifted = generated with { Source = "\n" + generated.Source };
        FuzzExecutionContext context = new(
            Environment.CurrentDirectory,
            "host",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2),
            4096,
            new CompilerExecution());

        FuzzOracleResult result = await new FormatOracle().EvaluateAsync(
            drifted,
            context,
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("original AST");
    }

    [Test]
    public async Task InvalidSourceMutationIsDeterministicAndDoesNotCrashParser()
    {
        var fixture = TestFixture.Create();
        GeneratedFuzzCase generated = fixture.Generator.Generate(123, 3, fixture.Profiles.Get("invalid-source"), 40);
        InvalidSourceMutator mutator = new();
        mutator.Mutate(generated.Source, generated.CaseSeed).ShouldBe(mutator.Mutate(generated.Source, generated.CaseSeed));
        FuzzExecutionContext context = new(Environment.CurrentDirectory, "host", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 4096, new CompilerExecution());
        (await new InvalidSourceOracle().EvaluateAsync(generated, context, CancellationToken.None)).Success.ShouldBeTrue();
    }

    [Test]
    public void ObservationRenderersProduceValidSemanticProgramsForEverySupportedTypeFamily()
    {
        var fixture = TestFixture.Create();
        AshesType[] types =
        [
            AshesType.Int,
            AshesType.Bool,
            AshesType.Str,
            AshesType.Float,
            AshesType.BigInt,
            new AshesType.UInt(8),
            new AshesType.UInt(16),
            new AshesType.UInt(32),
            new AshesType.UInt(64),
            new AshesType.Tuple([AshesType.Int, AshesType.Str]),
            new AshesType.List(AshesType.Int),
            new AshesType.Record("FuzzRecord"),
            new AshesType.Result(AshesType.Str, new AshesType.List(AshesType.Int)),
            new AshesType.Adt("FuzzTree", [AshesType.Str]),
            new AshesType.Adt("FuzzMaybe", [AshesType.Str]),
        ];
        for (int index = 0; index < types.Length; index++)
        {
            FuzzProfile profile = fixture.Profiles.Get("semantics") with { Types = [types[index]] };
            GeneratedFuzzCase generated = fixture.Generator.Generate(8008, index, profile, 80);
            string observable = ObservableValueRenderer.RenderProgram(generated);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program parsed = new Parser(observable, diagnostics).ParseProgram();
            _ = new Lowering(diagnostics).Lower(parsed);

            diagnostics.Errors.ShouldBeEmpty($"type {types[index]}:\n{observable}");

            string memoryWorkload = ObservableValueRenderer.RenderMemoryWorkload(generated, 7);
            Diagnostics memoryDiagnostics = new();
            Ashes.Frontend.Program memoryProgram = new Parser(memoryWorkload, memoryDiagnostics).ParseProgram();
            _ = new Lowering(memoryDiagnostics).Lower(memoryProgram);

            memoryDiagnostics.Errors.ShouldBeEmpty($"memory workload for type {types[index]}:\n{memoryWorkload}");
        }
    }

    [Test]
    public void MemoryGrowthAssessmentRequiresStableOutputAndPlateauingRss()
    {
        MemoryGrowthSample[] plateau =
        [
            new(2_000, 14_000, 1_024),
            new(10_000, 70_000, 2_048),
            new(50_000, 350_000, 4_096),
        ];
        MemoryGrowthOracle.Assess(plateau).ShouldBeNull();

        MemoryGrowthSample[] corrupted =
        [
            new(2_000, 14_000, 1_024),
            new(10_000, 80_000, 2_048),
            new(50_000, 350_000, 4_096),
        ];
        MemoryGrowthOracle.Assess(corrupted).ShouldNotBeNull().ShouldContain("checksum changed");

        MemoryGrowthSample[] growing =
        [
            new(2_000, 14_000, 1_024),
            new(10_000, 70_000, 9_000),
            new(50_000, 350_000, 12_000),
        ];
        MemoryGrowthOracle.Assess(growing).ShouldNotBeNull().ShouldContain("did not plateau");
    }

    [Test]
    public async Task NativePeakRssMeasurementReportsProcessUsage()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        string executable = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "/bin/true";
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows() ? ["/c", "exit", "0"] : [];

        ProcessResult result = await ProcessTimeout.RunWithNativePeakRssAsync(
            executable,
            arguments,
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(5),
            4096,
            CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.MaximumResidentSetKilobytes.ShouldNotBeNull();
        result.MaximumResidentSetKilobytes.Value.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task ProcessCaptureDrainsButDoesNotRetainOutputPastItsByteLimit()
    {
        ProcessResult result = await ProcessTimeout.RunAsync(
            "dotnet",
            ["--info"],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(10),
            32,
            CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.OutputTruncated.ShouldBeTrue();
        System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput).ShouldBeLessThanOrEqualTo(32);
        System.Text.Encoding.UTF8.GetByteCount(result.StandardError).ShouldBeLessThanOrEqualTo(32);
    }

    [Test]
    public void NativeOutcomeValidationRejectsTimeoutsCrashesAndTruncationBeforeComparison()
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(1);

        NativeOutcomeValidator.Failure("program", new ProcessResult(-1, "", "", true, false, duration))
            .ShouldBe("program timed out");
        NativeOutcomeValidator.Failure("program", new ProcessResult(0, "same", "", false, true, duration))
            .ShouldBe("program exceeded its output limit");
        NativeOutcomeValidator.Failure("program", new ProcessResult(139, "", "signal", false, false, duration))
            .ShouldBe("program exited with 139");
        NativeOutcomeValidator.Failure("program", new ProcessResult(0, "ok", "", false, false, duration))
            .ShouldBeNull();
    }
}
