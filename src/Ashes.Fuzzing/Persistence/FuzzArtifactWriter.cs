using System.Text.Json;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Fuzzing.Shrinking;

namespace Ashes.Fuzzing.Persistence;

internal sealed record FuzzFailure(
    GeneratedFuzzCase Original,
    GeneratedFuzzCase Minimized,
    FuzzOracleResult OracleResult,
    ShrinkResult Shrink,
    FuzzConfiguration Configuration);

internal sealed record FuzzFailureMetadata(
    ulong Seed,
    ulong CaseSeed,
    int CaseIndex,
    string Profile,
    string Oracle,
    string Target,
    string CompilerConfiguration,
    GenerationBudget GenerationBudget,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> GenerationTrace,
    int ShrinkAttempts,
    int ShrinkAccepted,
    string ReplayCommand);

internal sealed class FuzzArtifactWriter
{
    internal async Task<string> WriteAsync(FuzzFailure failure, string root, CancellationToken cancellationToken)
    {
        string identity = $"{failure.Original.Profile}:{failure.Original.MasterSeed}:{failure.Original.CaseIndex}:{failure.OracleResult.Oracle}";
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        string path = Path.Combine(root, id);
        Directory.CreateDirectory(path);
        string replay = FuzzReplayCommand.Format(failure.Original, failure.Configuration);
        FuzzFailureMetadata metadata = new(
            failure.Original.MasterSeed, failure.Original.CaseSeed, failure.Original.CaseIndex, failure.Original.Profile,
            failure.OracleResult.Oracle, failure.Configuration.Target, "Release", failure.Original.Budget,
            failure.Original.Features.Select(feature => feature.ToString()).ToArray(), failure.Original.Trace.Entries,
            failure.Shrink.Attempts, failure.Shrink.Accepted, replay);
        JsonSerializerOptions options = new() { WriteIndented = true };
        await File.WriteAllTextAsync(Path.Combine(path, "original.ash"), failure.Original.Source, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(path, "minimized.ash"), failure.Minimized.Source, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(path, "failure.txt"), failure.OracleResult.Message, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(path, "metadata.json"), JsonSerializer.Serialize(metadata, options), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(path, "stdout.txt"), failure.OracleResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(path, "stderr.txt"), failure.OracleResult.StandardError, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
