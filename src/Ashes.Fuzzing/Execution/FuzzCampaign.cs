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
        FuzzExecutionContext context = new(repositoryRoot, configuration.Target, configuration.CompilerTimeout, configuration.ProgramTimeout, 1024 * 1024, new CompilerExecution());
        int first = configuration.Command == FuzzCommandKind.Replay ? configuration.CaseIndex : 0;
        int count = configuration.Command == FuzzCommandKind.Replay ? 1 : configuration.Cases;
        int seedCount = configuration.Command == FuzzCommandKind.Replay ? 1 : configuration.SeedCount;
        for (int seedOffset = 0; seedOffset < seedCount; seedOffset++)
        {
            ulong masterSeed = configuration.Seed + (ulong)seedOffset;
            for (int offset = 0; offset < count; offset++)
            {
                int caseIndex = first + offset;
                FuzzProfile profile = ResolveProfile(requestedProfile, caseIndex);
                GeneratedFuzzCase testCase = _generator.Generate(masterSeed, caseIndex, profile, configuration.MaximumNodes);
                if (profile.MutateSource)
                {
                    testCase = UseInvalidSourceSeed(testCase, repositoryRoot, caseIndex);
                }
                if (configuration.Command == FuzzCommandKind.Replay)
                {
                    GeneratedFuzzCase replay = _generator.Generate(masterSeed, caseIndex, profile, configuration.MaximumNodes);
                    if (profile.MutateSource)
                    {
                        replay = UseInvalidSourceSeed(replay, repositoryRoot, caseIndex);
                    }
                    if (!string.Equals(testCase.Source, replay.Source, StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("Replay did not regenerate byte-identical source.");
                        return 2;
                    }
                    Console.WriteLine(testCase.Source);
                }
                foreach (string oracleId in profile.Oracles)
                {
                    IFuzzOracle oracle = _oracles.Get(oracleId);
                    FuzzOracleResult result = await oracle.EvaluateAsync(testCase, context, cancellationToken).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        return await ReportFailureAsync(testCase, result, configuration, context, cancellationToken).ConfigureAwait(false);
                    }
                }
                coverage.Record(testCase, profile.Oracles);
            }
        }
        Console.WriteLine($"passed {count * seedCount} case(s); profile={requestedProfile.Id}; seeds={configuration.Seed}..{configuration.Seed + (ulong)seedCount - 1}");
        Console.WriteLine(coverage.Summary());
        return 0;
    }

    private static GeneratedFuzzCase UseInvalidSourceSeed(GeneratedFuzzCase generated, string repositoryRoot, int caseIndex)
    {
        string[] roots = [Path.Combine(repositoryRoot, "tests"), Path.Combine(repositoryRoot, "examples")];
        string[] seeds = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.ash", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (seeds.Length == 0)
        {
            return generated;
        }
        string source = File.ReadAllText(seeds[caseIndex % seeds.Length]);
        if (source.Length > generated.Budget.MaximumSourceLength)
        {
            source = source[..generated.Budget.MaximumSourceLength];
        }
        return generated with { Source = source, Trace = generated.Trace.Append("invalid-seed:" + Path.GetRelativePath(repositoryRoot, seeds[caseIndex % seeds.Length])) };
    }

    private FuzzProfile ResolveProfile(FuzzProfile requested, int caseIndex)
    {
        if (!string.Equals(requested.Id, "all", StringComparison.Ordinal))
        {
            return requested;
        }
        if (caseIndex % 50 == 0)
        {
            return _profiles.Get("differential");
        }
        if (caseIndex % 25 == 0)
        {
            return _profiles.Get("invalid-source");
        }
        string[] stable = ["syntax", "semantics", "perceus", "combinations"];
        return _profiles.Get(stable[caseIndex % stable.Length]);
    }

    internal async Task<int> RunCorpusAsync(FuzzConfiguration configuration, string repositoryRoot, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files = new FuzzCorpus().Load(repositoryRoot);
        FuzzExecutionContext context = new(repositoryRoot, configuration.Target, configuration.CompilerTimeout, configuration.ProgramTimeout, 1024 * 1024, new CompilerExecution());
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
        string artifactPath = await _artifacts.WriteAsync(failure, configuration.ArtifactRoot, cancellationToken).ConfigureAwait(false);
        string replay = FuzzReplayCommand.Format(testCase, configuration);
        Console.Error.WriteLine($"fuzz failure: seed={testCase.MasterSeed} case={testCase.CaseIndex} profile={testCase.Profile} oracle={result.Oracle}");
        Console.Error.WriteLine(result.Message);
        Console.Error.WriteLine(testCase.Source);
        Console.Error.WriteLine($"artifact: {artifactPath}");
        Console.Error.WriteLine($"replay: {replay}");
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
}
