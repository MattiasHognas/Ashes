using System.Text.Json;

namespace Ashes.Cli.Package;

/// <summary>One pinned entry in <c>ashes.lock</c>.</summary>
internal sealed record LockedPackage(
    string Namespace,
    string Version,
    string Source,
    string Hash,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// The generated, committed <c>ashes.lock</c>: the fully resolved graph so the CLI, LSP, and test runner
/// consume an identical, deterministic set of roots. Integrity is the <c>ash1:</c> source-tree hash.
/// </summary>
internal sealed class LockFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Version { get; init; } = 1;

    public IReadOnlyList<LockedPackage> Package { get; init; } = [];

    public static LockFile? Read(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "ashes.lock");
        return File.Exists(path) ? JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Json) : null;
    }

    public void Write(string projectDirectory) =>
        File.WriteAllText(
            Path.Combine(projectDirectory, "ashes.lock"),
            JsonSerializer.Serialize(this, Json) + Environment.NewLine);
}
