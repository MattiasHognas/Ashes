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
    double ShrinkDurationMilliseconds,
    int OutputMaximumBytes,
    int ArtifactMaximumBytes,
    string ReplayCommand);

internal sealed class FuzzArtifactWriter
{
    internal const int DefaultMaximumArtifactBytes = 4 * 1024 * 1024;

    internal async Task<string> WriteAsync(
        FuzzFailure failure,
        string root,
        CancellationToken cancellationToken,
        int maximumArtifactBytes = DefaultMaximumArtifactBytes)
    {
        if (maximumArtifactBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        }

        string identity = $"{failure.Original.Profile}:{failure.Original.MasterSeed}:{failure.Original.CaseIndex}:{failure.OracleResult.Oracle}";
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        string path = Path.Combine(root, id);
        Directory.CreateDirectory(path);
        string replay = FuzzReplayCommand.Format(failure.Original, failure.Configuration);
        FuzzFailureMetadata metadata = new(
            failure.Original.MasterSeed, failure.Original.CaseSeed, failure.Original.CaseIndex, failure.Original.Profile,
            failure.OracleResult.Oracle, failure.Configuration.Target,
            FuzzFailureReport.CompilerConfiguration(failure.OracleResult.Oracle), failure.Original.Budget,
            failure.Original.Features.Select(feature => feature.ToString()).ToArray(), failure.Original.Trace.Entries,
            failure.Shrink.Attempts, failure.Shrink.Accepted, failure.Shrink.Duration.TotalMilliseconds,
            failure.Configuration.MaximumOutputBytes, maximumArtifactBytes, replay);
        JsonSerializerOptions options = new() { WriteIndented = true };
        var files = new (string Name, string Contents)[]
        {
            ("metadata.json", JsonSerializer.Serialize(metadata, options)),
            ("failure.txt", failure.OracleResult.Message),
            ("minimized.ash", failure.Minimized.Source),
            ("original.ash", failure.Original.Source),
            ("stdout.txt", failure.OracleResult.StandardOutput),
            ("stderr.txt", failure.OracleResult.StandardError),
        };
        int remaining = maximumArtifactBytes;
        foreach ((string name, string contents) in files)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            int written = Math.Min(bytes.Length, remaining);
            await File.WriteAllBytesAsync(Path.Combine(path, name), bytes.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            remaining -= written;
        }
        return path;
    }
}
