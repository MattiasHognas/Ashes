using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Execution;
using Ashes.Fuzzing.Generation;
using Ashes.Fuzzing.Oracles;

try
{
    FuzzConfiguration configuration = FuzzConfiguration.Parse(args);
    GeneratorRegistry rules = GeneratorRegistry.CreateDefault();
    CombinationRegistry combinations = CombinationRegistry.CreateDefault();
    FuzzProfileRegistry profiles = FuzzProfileRegistry.CreateDefault(rules, combinations);
    FuzzOracleRegistry oracles = FuzzOracleRegistry.CreateDefault();
    if (configuration.Command == FuzzCommandKind.List)
    {
        Console.WriteLine("profiles:");
        foreach (FuzzProfile profile in profiles.Profiles)
        {
            Console.WriteLine($"  {profile.Id,-16} oracles={string.Join(',', profile.Oracles)}");
        }
        Console.WriteLine("expression rules:");
        foreach (IExpressionGenerationRule rule in rules.Rules) Console.WriteLine($"  {rule.Id}");
        Console.WriteLine("combination templates:");
        foreach (ICombinationTemplate template in combinations.Templates) Console.WriteLine($"  {template.Id}");
        return 0;
    }
    string repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
    FuzzCampaign campaign = new(new ProgramGenerator(rules, combinations), profiles, oracles);
    return configuration.Command == FuzzCommandKind.Corpus
        ? await campaign.RunCorpusAsync(configuration, repositoryRoot, CancellationToken.None).ConfigureAwait(false)
        : await campaign.RunAsync(configuration, repositoryRoot, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"fuzz configuration or internal failure: {exception.Message}");
    return 2;
}

static string FindRepositoryRoot(string start)
{
    DirectoryInfo? directory = new(start);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ashes.slnx"))) directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Could not locate Ashes.slnx from the current directory.");
}
