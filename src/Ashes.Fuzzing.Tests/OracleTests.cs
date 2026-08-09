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
        }
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
