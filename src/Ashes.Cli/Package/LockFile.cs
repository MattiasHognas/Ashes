using System.Text.Json;
using Ashes.Semantics;

namespace Ashes.Cli.Package;

/// <summary>One pinned entry in a project lock file.</summary>
internal sealed record LockedPackage(
    string Namespace,
    string Version,
    string Source,
    string Hash,
    IReadOnlyList<string> Dependencies);

/// <summary>
/// A generated, committed project lock file: the fully resolved graph so the CLI, LSP, and test
/// runner consume an identical, deterministic set of roots. Integrity is the <c>ash1:</c>
/// source-tree hash.
/// </summary>
internal sealed class LockFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Version { get; init; } = 1;

    public IReadOnlyList<LockedPackage> Package { get; init; } = [];

    public static LockFile? Read(string projectFilePath)
    {
        string path = ProjectSupport.GetLockFilePath(projectFilePath);
        return File.Exists(path) ? JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), Json) : null;
    }

    public void Write(string projectFilePath) =>
        File.WriteAllText(
            ProjectSupport.GetLockFilePath(projectFilePath),
            JsonSerializer.Serialize(this, Json) + Environment.NewLine);
}
