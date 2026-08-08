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
    public void AggregateObservationRenderersProduceValidSemanticPrograms()
    {
        var fixture = TestFixture.Create();
        AshesType[] types =
        [
            new AshesType.Tuple([AshesType.Int, AshesType.Str]),
            new AshesType.List(AshesType.Int),
            new AshesType.Record("FuzzRecord"),
            new AshesType.Result(AshesType.Str, new AshesType.List(AshesType.Int)),
            new AshesType.Adt("FuzzTree", [AshesType.Str]),
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
}
