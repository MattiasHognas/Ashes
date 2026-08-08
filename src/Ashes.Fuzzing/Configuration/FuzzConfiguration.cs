namespace Ashes.Fuzzing.Configuration;

internal enum FuzzCommandKind
{
    Run,
    Smoke,
    Replay,
    Corpus,
    List,
}

internal sealed record FuzzConfiguration(
    FuzzCommandKind Command,
    string Profile,
    int Cases,
    ulong Seed,
    int SeedCount,
    int CaseIndex,
    string Target,
    int MaximumNodes,
    TimeSpan CompilerTimeout,
    TimeSpan ProgramTimeout,
    int MaximumOutputBytes,
    int MaximumArtifactBytes,
    string ArtifactRoot,
    IReadOnlySet<string> ExplicitOptions)
{
    internal static FuzzConfiguration Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("Expected a command: smoke, run, replay, corpus, or list.");
        }

        FuzzCommandKind command = args[0] switch
        {
            "smoke" => FuzzCommandKind.Smoke,
            "run" => FuzzCommandKind.Run,
            "replay" => FuzzCommandKind.Replay,
            "corpus" => FuzzCommandKind.Corpus,
            "list" => FuzzCommandKind.List,
            _ => throw new ArgumentException($"Unknown fuzz command '{args[0]}'."),
        };

        string profile = command == FuzzCommandKind.Smoke ? "smoke" : "all";
        int cases = command == FuzzCommandKind.Smoke ? 40 : 100;
        ulong seed = 0xA55E5UL;
        int seedCount = 1;
        int caseIndex = -1;
        string target = "host";
        int maximumNodes = command == FuzzCommandKind.Smoke ? 40 : 80;
        int compilerTimeoutSeconds = 20;
        int programTimeoutSeconds = 5;
        int maximumOutputBytes = 1024 * 1024;
        int maximumArtifactBytes = 4 * 1024 * 1024;
        string artifactRoot = Path.Combine("artifacts", "fuzz");
        var explicitOptions = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 1; i < args.Count; i++)
        {
            string option = args[i];
            explicitOptions.Add(option);
            string Value()
            {
                if (++i >= args.Count)
                {
                    throw new ArgumentException($"Missing value after '{option}'.");
                }
                return args[i];
            }

            switch (option)
            {
                case "--profile": profile = Value(); break;
                case "--cases": cases = ParsePositiveInt(Value(), option); break;
                case "--seed": seed = ulong.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seeds": seedCount = ParsePositiveInt(Value(), option); break;
                case "--case": caseIndex = ParseNonNegativeInt(Value(), option); break;
                case "--target": target = Value(); break;
                case "--max-nodes": maximumNodes = ParsePositiveInt(Value(), option); break;
                case "--compiler-timeout": compilerTimeoutSeconds = ParsePositiveInt(Value(), option); break;
                case "--program-timeout": programTimeoutSeconds = ParsePositiveInt(Value(), option); break;
                case "--max-output-bytes": maximumOutputBytes = ParsePositiveInt(Value(), option); break;
                case "--max-artifact-bytes": maximumArtifactBytes = ParsePositiveInt(Value(), option); break;
                case "--artifacts": artifactRoot = Value(); break;
                default: throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        if (command == FuzzCommandKind.Replay && caseIndex < 0)
        {
            throw new ArgumentException("Replay requires --case <index>.");
        }

        return new FuzzConfiguration(command, profile, cases, seed, seedCount, caseIndex, target, maximumNodes,
            TimeSpan.FromSeconds(compilerTimeoutSeconds), TimeSpan.FromSeconds(programTimeoutSeconds),
            maximumOutputBytes, maximumArtifactBytes, artifactRoot, explicitOptions);
    }

    internal FuzzConfiguration ApplyProfileDefaults(FuzzProfile profile)
    {
        FuzzProfileDefaults defaults = profile.EffectiveDefaults;
        return this with
        {
            Cases = ExplicitOptions.Contains("--cases") ? Cases : defaults.Cases,
            MaximumNodes = ExplicitOptions.Contains("--max-nodes") ? MaximumNodes : defaults.MaximumNodes,
            CompilerTimeout = ExplicitOptions.Contains("--compiler-timeout")
                ? CompilerTimeout
                : TimeSpan.FromSeconds(defaults.CompilerTimeoutSeconds),
            ProgramTimeout = ExplicitOptions.Contains("--program-timeout")
                ? ProgramTimeout
                : TimeSpan.FromSeconds(defaults.ProgramTimeoutSeconds),
            Target = ExplicitOptions.Contains("--target") ? Target : defaults.Targets[0],
        };
    }

    private static int ParsePositiveInt(string value, string option)
    {
        int parsed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentException($"{option} must be positive.");
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        int parsed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return parsed >= 0 ? parsed : throw new ArgumentException($"{option} must not be negative.");
    }
}
