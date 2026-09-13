using System.Text;
using Ashes.Fuzzing.Execution;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Oracles;

internal sealed class InvalidSourceOracle : IFuzzOracle
{
    /// <summary>
    /// Persists a mutation that left a lone surrogate behind. Truncation cuts at an arbitrary index, so
    /// it can land between the halves of a surrogate pair, and the default text writer throws on an
    /// unpaired half instead of producing a file — which failed the case before the parser ever saw it.
    /// Handing the parser hostile bytes is this oracle's whole purpose, so an unpaired half is encoded
    /// as the replacement character rather than aborting the campaign.
    /// </summary>
    internal static UTF8Encoding MutatedSourceEncoding { get; } = new(encoderShouldEmitUTF8Identifier: false);

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
                await File.WriteAllTextAsync(sourcePath, mutated, MutatedSourceEncoding, cancellationToken).ConfigureAwait(false);
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
