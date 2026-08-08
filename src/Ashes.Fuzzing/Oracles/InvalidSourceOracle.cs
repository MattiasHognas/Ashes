using Ashes.Fuzzing.Execution;
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
            string temporaryRoot = Directory.CreateTempSubdirectory("ashes-fuzz-invalid-").FullName;
            try
            {
                string sourcePath = Path.Combine(temporaryRoot, "mutated.ash");
                await File.WriteAllTextAsync(sourcePath, mutated, cancellationToken).ConfigureAwait(false);
                string assemblyPath = typeof(InvalidSourceOracle).Assembly.Location;
                ProcessResult process = await ProcessTimeout.RunAsync(
                    "dotnet",
                    [assemblyPath, InvalidSourceWorker.Command, sourcePath],
                    context.RepositoryRoot,
                    context.CompilerTimeout,
                    context.MaximumOutputBytes,
                    cancellationToken).ConfigureAwait(false);
                if (process.TimedOut)
                {
                    return FuzzOracleResult.Failed(Id, "Parser worker timed out on mutated input.", process.StandardOutput, process.StandardError);
                }
                if (process.ExitCode != 0 || process.OutputTruncated)
                {
                    string message = process.OutputTruncated
                        ? "Parser worker exceeded its output limit."
                        : $"Parser worker crashed or rejected its diagnostic bound with exit code {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}.";
                    return FuzzOracleResult.Failed(Id, message, process.StandardOutput, process.StandardError);
                }
                return FuzzOracleResult.Passed(Id);
            }
            finally
            {
                TryDelete(temporaryRoot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FuzzOracleResult.Failed(Id, $"Parser crashed on mutated input: {exception}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
