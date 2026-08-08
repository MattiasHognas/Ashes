namespace Ashes.Fuzzing.Generation;

internal static class InvalidSourceSeedSelector
{
    private sealed record SeedCategory(string Id, string Root, Func<string, bool>? Include = null);

    internal static GeneratedFuzzCase Select(
        GeneratedFuzzCase generated,
        string repositoryRoot,
        int caseIndex)
    {
        SeedCategory[] categories =
        [
            new("corpus", Path.Combine(repositoryRoot, "tests", "fuzz", "corpus")),
            new("tests", Path.Combine(repositoryRoot, "tests"), path =>
                !path.StartsWith(Path.Combine(repositoryRoot, "tests", "fuzz", "corpus") + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            new("examples", Path.Combine(repositoryRoot, "examples")),
            new("parser-fixtures", Path.Combine(repositoryRoot, "src", "Ashes.Lsp.Tests", "fixtures")),
        ];
        int selection = caseIndex % (categories.Length + 1);
        if (selection == 0)
        {
            return generated with { Trace = generated.Trace.Append("invalid-seed:generated") };
        }

        SeedCategory category = categories[selection - 1];
        if (!Directory.Exists(category.Root))
        {
            return generated with { Trace = generated.Trace.Append("invalid-seed:generated:fallback") };
        }
        string[] files = Directory.EnumerateFiles(category.Root, "*.ash", SearchOption.AllDirectories)
            .Where(path => category.Include?.Invoke(path) ?? true)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            return generated with { Trace = generated.Trace.Append("invalid-seed:generated:fallback") };
        }

        int categoryIndex = caseIndex / (categories.Length + 1);
        string selected = files[categoryIndex % files.Length];
        string source = File.ReadAllText(selected);
        if (source.Length > generated.Budget.MaximumSourceLength)
        {
            source = source[..generated.Budget.MaximumSourceLength];
        }
        return generated with
        {
            Source = source,
            Trace = generated.Trace.Append($"invalid-seed:{category.Id}:" + Path.GetRelativePath(repositoryRoot, selected)),
        };
    }
}
