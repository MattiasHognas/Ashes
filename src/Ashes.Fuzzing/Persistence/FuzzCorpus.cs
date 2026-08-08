namespace Ashes.Fuzzing.Persistence;

internal sealed class FuzzCorpus
{
    internal IReadOnlyList<string> Load(string repositoryRoot) => Directory.Exists(Path.Combine(repositoryRoot, "tests", "fuzz", "corpus"))
        ? Directory.EnumerateFiles(Path.Combine(repositoryRoot, "tests", "fuzz", "corpus"), "*.ash", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
        : [];
}
