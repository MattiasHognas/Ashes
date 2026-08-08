using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Oracles;

internal sealed class InvalidSourceOracle : IFuzzOracle
{
    public string Id => "invalid-source";
    public async ValueTask<FuzzOracleResult> EvaluateAsync(GeneratedFuzzCase testCase, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            string mutated = new InvalidSourceMutator().Mutate(testCase.Source, testCase.CaseSeed);
            Diagnostics diagnostics = await Task.Run(() =>
            {
                Diagnostics parserDiagnostics = new();
                _ = new Parser(mutated, parserDiagnostics).ParseProgram();
                return parserDiagnostics;
            }, cancellationToken).WaitAsync(context.CompilerTimeout, cancellationToken).ConfigureAwait(false);
            return diagnostics.Errors.Count <= 1024
                ? FuzzOracleResult.Passed(Id)
                : FuzzOracleResult.Failed(Id, $"Parser emitted an unbounded diagnostic set ({diagnostics.Errors.Count}).");
        }
        catch (TimeoutException)
        {
            return FuzzOracleResult.Failed(Id, "Parser timed out on mutated input.");
        }
        catch (Exception exception)
        {
            return FuzzOracleResult.Failed(Id, $"Parser crashed on mutated input: {exception}");
        }
    }
}
