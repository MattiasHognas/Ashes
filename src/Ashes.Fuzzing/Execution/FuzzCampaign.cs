using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;
using Ashes.Fuzzing.Persistence;
using Ashes.Fuzzing.Shrinking;
using Ashes.Frontend;

namespace Ashes.Fuzzing.Execution;

internal sealed class FuzzCampaign
{
    private readonly ProgramGenerator _generator;
    private readonly FuzzProfileRegistry _profiles;
    private readonly FuzzOracleRegistry _oracles;
    private readonly FuzzArtifactWriter _artifacts = new();
    private readonly FuzzShrinker _shrinker = new();

    internal FuzzCampaign(ProgramGenerator generator, FuzzProfileRegistry profiles, FuzzOracleRegistry oracles)
    {
        _generator = generator;
        _profiles = profiles;
        _oracles = oracles;
    }

    internal async Task<int> RunAsync(FuzzConfiguration configuration, string repositoryRoot, CancellationToken cancellationToken)
    {
        FuzzProfile requestedProfile = _profiles.Get(configuration.Profile);
        FuzzCoverage coverage = new(requestedProfile.EnabledRules, requestedProfile.EnabledCombinations);
        FuzzExecutionContext context = new(repositoryRoot, configuration.Target, configuration.CompilerTimeout, configuration.ProgramTimeout, configuration.MaximumOutputBytes, new CompilerExecution());
        int first = configuration.Command == FuzzCommandKind.Replay ? configuration.CaseIndex : 0;
        int count = configuration.Command == FuzzCommandKind.Replay ? 1 : configuration.Cases;
        int seedCount = configuration.Command == FuzzCommandKind.Replay ? 1 : configuration.SeedCount;
        int completed = 0;
        using CancellationTokenSource? timeoutSource = CreateCampaignTimeout(configuration, cancellationToken);
        CancellationToken campaignToken = timeoutSource?.Token ?? cancellationToken;
        try
        {
            for (int seedOffset = 0; seedOffset < seedCount; seedOffset++)
            {
                ulong masterSeed = configuration.Seed + (ulong)seedOffset;
                for (int offset = 0; offset < count; offset++)
                {
                    campaignToken.ThrowIfCancellationRequested();
                    int caseIndex = first + offset;
                    FuzzProfile profile = ResolveProfile(requestedProfile, caseIndex);
                    GeneratedFuzzCase testCase = _generator.Generate(masterSeed, caseIndex, profile, configuration.MaximumNodes);
                    if (profile.MutateSource)
                    {
                        testCase = InvalidSourceSeedSelector.Select(testCase, repositoryRoot, caseIndex);
                    }
                    if (configuration.Command == FuzzCommandKind.Replay)
                    {
                        GeneratedFuzzCase replay = _generator.Generate(masterSeed, caseIndex, profile, configuration.MaximumNodes);
                        if (profile.MutateSource)
                        {
                            replay = InvalidSourceSeedSelector.Select(replay, repositoryRoot, caseIndex);
                        }
                        if (!string.Equals(testCase.Source, replay.Source, StringComparison.Ordinal))
                        {
                            Console.Error.WriteLine("Replay did not regenerate byte-identical source.");
                            return 2;
                        }
                        Console.WriteLine(testCase.Source);
                    }
                    coverage.Record(testCase);
                    foreach (string oracleId in profile.Oracles)
                    {
                        IFuzzOracle oracle = _oracles.Get(oracleId);
                        coverage.RecordOracleExecution(oracleId);
                        FuzzOracleResult result = await oracle.EvaluateAsync(testCase, context, campaignToken).ConfigureAwait(false);
                        if (!result.Success)
                        {
                            Console.Error.WriteLine(coverage.Summary());
                            return await ReportFailureAsync(testCase, result, configuration, context, cancellationToken).ConfigureAwait(false);
                        }
                        if (!string.Equals(result.Message, "passed", StringComparison.Ordinal))
                        {
                            Console.WriteLine($"case={caseIndex} oracle={oracleId}: {result.Message}");
                        }
                    }
                    completed++;
                }
            }
        }
        catch (OperationCanceledException) when (
            timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"campaign time budget reached after {completed} completed case(s); profile={requestedProfile.Id}");
            Console.WriteLine(coverage.Summary());
            return 0;
        }
        Console.WriteLine($"passed {completed} case(s); profile={requestedProfile.Id}; seeds={configuration.Seed}..{configuration.Seed + (ulong)seedCount - 1}");
        Console.WriteLine(coverage.Summary());
        return 0;
    }

    internal FuzzProfile ResolveProfile(FuzzProfile requested, int caseIndex)
    {
        if (!string.Equals(requested.Id, "all", StringComparison.Ordinal))
        {
            return requested;
        }
        int slot = caseIndex % 50;
        string? focused = slot switch
        {
            0 => "differential",
            1 => "compile",
            2 => "cross-target",
            3 => "invalid-source",
            4 => "async",
            5 => "capabilities",
            6 => "resources",
            7 => "traits",
            8 => "invalid-semantics",
            9 => "traits-differential",
            10 => "memory-growth",
            _ => null,
        };
        if (focused is not null)
        {
            return _profiles.Get(focused);
        }
        string[] stable = ["syntax", "semantics", "perceus", "combinations"];
        return _profiles.Get(stable[(slot - 11) % stable.Length]);
    }

    internal async Task<int> RunCorpusAsync(FuzzConfiguration configuration, string repositoryRoot, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files = new FuzzCorpus().Load(repositoryRoot);
        FuzzExecutionContext context = new(repositoryRoot, configuration.Target, configuration.CompilerTimeout, configuration.ProgramTimeout, configuration.MaximumOutputBytes, new CompilerExecution());
        int index = 0;
        foreach (string file in files)
        {
            string source = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            Diagnostics diagnostics = new();
            Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
            GeneratedFuzzCase testCase = new(configuration.Seed, FuzzRandom.DeriveCaseSeed(configuration.Seed, index), index, "corpus", AshesType.Int, program, source, new GeneratedFeatureSet(), GenerationTrace.Empty, 1, GenerationBudget.Create(configuration.MaximumNodes));
            foreach (string oracleId in new[] { "parse", "format", "semantic", "ir" })
            {
                FuzzOracleResult result = await _oracles.Get(oracleId).EvaluateAsync(testCase, context, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    Console.Error.WriteLine($"corpus failure: {file}: {result.Message}");
                    return 1;
                }
            }
            index++;
        }
        Console.WriteLine($"passed {files.Count} corpus case(s)");
        return 0;
    }

    private async Task<int> ReportFailureAsync(GeneratedFuzzCase testCase, FuzzOracleResult result, FuzzConfiguration configuration, FuzzExecutionContext context, CancellationToken cancellationToken)
    {
        IFuzzOracle oracle = _oracles.Get(result.Oracle);
        ShrinkResult shrink = await _shrinker.ShrinkAsync(testCase, async (candidate, token) =>
        {
            if (!IsValidShrinkCandidate(candidate, result.Oracle))
            {
                return false;
            }
            FuzzOracleResult candidateResult = await oracle.EvaluateAsync(candidate, context, token).ConfigureAwait(false);
            return !candidateResult.Success;
        }, 100, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        FuzzFailure failure = new(testCase, shrink.Case, result, shrink, configuration);
        string artifactPath = await _artifacts.WriteAsync(
            failure,
            configuration.ArtifactRoot,
            cancellationToken,
            configuration.MaximumArtifactBytes).ConfigureAwait(false);
        string replay = FuzzReplayCommand.Format(testCase, configuration);
        foreach (string line in FuzzFailureReport.Lines(testCase, result, artifactPath, replay))
        {
            Console.Error.WriteLine(line);
        }
        return 1;
    }

    private static bool IsValidShrinkCandidate(GeneratedFuzzCase candidate, string oracle)
    {
        if (string.Equals(oracle, "parse", StringComparison.Ordinal) || string.Equals(oracle, "invalid-source", StringComparison.Ordinal))
        {
            return true;
        }
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(candidate.Source, diagnostics).ParseProgram();
        if (diagnostics.Errors.Count != 0)
        {
            return false;
        }
        if (string.Equals(oracle, "format", StringComparison.Ordinal) || string.Equals(oracle, "semantic", StringComparison.Ordinal))
        {
            return true;
        }
        _ = new Ashes.Semantics.Lowering(diagnostics).Lower(program);
        return diagnostics.Errors.Count == 0;
    }

    private static CancellationTokenSource? CreateCampaignTimeout(
        FuzzConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.CampaignTimeout <= TimeSpan.Zero)
        {
            return null;
        }
        CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(configuration.CampaignTimeout);
        return timeoutSource;
    }
}
